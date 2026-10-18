using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// A migrated and seeded scratch database of its own, <c>MarocLIS_BkTest_&lt;guid&gt;</c>, backed up
/// into a scratch folder and restored over itself, then dropped with its files and its msdb history.
/// The SQL Server of <c>MAROCLIS_TEST_SQLSERVER</c> must be able to write to the test's temporary
/// folder (it runs on the same machine).
/// </summary>
public sealed class BackupScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_BkTest_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; } = "";

    /// <summary>The backup folder, created for the test and deleted afterwards.</summary>
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "maroclis-bktest-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>A login that is db_owner of the scratch database and nothing more: a workstation's.</summary>
    public string OwnerLogin { get; }

    public string OwnerPassword { get; } = "Bk-Owner-" + Guid.NewGuid().ToString("N")[..10] + "!";

    private readonly bool _created;

    public BackupScratchDatabase()
    {
        OwnerLogin = "bktest_" + Name[^8..];

        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = Name }.ConnectionString;

        using (var context = Context())
        {
            if (context.Database.CanConnect())
                throw new InvalidOperationException($"Database {Name} already exists; the tests create and drop their own.");
        }

        _created = true;
        Directory.CreateDirectory(Folder);

        using (var scope = Services().CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync().GetAwaiter().GetResult()
                .IsSuccess.Should().BeTrue();
        }

        Execute($"CREATE LOGIN [{OwnerLogin}] WITH PASSWORD = N'{OwnerPassword}', CHECK_POLICY = OFF;", master: true);
        Execute($"CREATE USER [{OwnerLogin}] FOR LOGIN [{OwnerLogin}]; ALTER ROLE db_owner ADD MEMBER [{OwnerLogin}];");
    }

    public string OwnerConnectionString =>
        new SqlConnectionStringBuilder(ConnectionString) { UserID = OwnerLogin, Password = OwnerPassword }.ConnectionString;

    public LisDbContext Context(ICurrentUser? user = null, string? connectionString = null) =>
        new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(connectionString ?? ConnectionString).Options, user);

    /// <summary>What the application registers for the engine and the auditor: a context per scope, the initializer.</summary>
    public ServiceProvider Services(ICurrentUser? user = null, string? connectionString = null) =>
        new ServiceCollection()
            .AddScoped(_ => Context(user, connectionString))
            .AddSingleton<IPasswordHasher>(new BCryptPasswordHasher(10))
            .AddSingleton<IClock, SystemClock>()
            .AddScoped<DatabaseInitializer>()
            .BuildServiceProvider();

    public object? Scalar(string sql, bool master = false)
    {
        using var connection = new SqlConnection(master
            ? new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" }.ConnectionString
            : ConnectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        return command.ExecuteScalar();
    }

    public void Execute(string sql, bool master = false) => Scalar(sql, master);

    public void Dispose()
    {
        if (!_created) return;

        SqlConnection.ClearAllPools();
        try
        {
            Execute($"IF DB_ID(N'{Name}') IS NOT NULL BEGIN ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]; END; " +
                    $"EXEC msdb.dbo.sp_delete_database_backuphistory @database_name = N'{Name}'; " +
                    $"IF SUSER_ID(N'{OwnerLogin}') IS NOT NULL DROP LOGIN [{OwnerLogin}];", master: true);
        }
        finally
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
    }
}

/// <summary>
/// Paramètres › Sauvegardes against a real SQL Server: BACKUP, RESTORE VERIFYONLY, the folder probe,
/// the msdb history and a RESTORE WITH REPLACE over the database, with a connection cut on the way.
/// </summary>
public sealed class BackupDatabaseTests(BackupScratchDatabase database) : IClassFixture<BackupScratchDatabase>
{
    private sealed class Admin : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "admin";
        public string FullName => "Administrateur";
        public Guid SiteId => Guid.Empty;
        public string SiteCode => "LAB";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => default;
        public IReadOnlyList<Guid> AccessibleSiteIds => [];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string> { global::MarocLIS.Domain.Identity.Permissions.Administration.Backup };
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => Permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "SRV-TEST";
    }

    private sealed class Guard(ICurrentUser user) : IPermissionGuard
    {
        public Result Require(string permission) =>
            user.Has(permission) ? Result.Success() : Result.Failure(Error.Forbidden("Message.PermissionDenied", permission));

        public Result RequireAny(params string[] permissions) => throw new NotSupportedException();
        public Result RequireAll(params string[] permissions) => throw new NotSupportedException();
        public Task<Result> RequireBiologicalValidationAsync(string departmentCode, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static readonly RestoreContext OnServer = new(IsServerMachine: true, IsLocalAdministrator: true);

    /// <summary>The service as the desktop composes it, on the given login.</summary>
    private sealed class Composition : IAsyncDisposable
    {
        public required ServiceProvider Services { get; init; }
        public required LisDbContext Context { get; init; }
        public required BackupService Backups { get; init; }
        public required SqlServerBackupEngine Engine { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Services.DisposeAsync();
        }
    }

    private async Task<Composition> ComposeAsync(string? connectionString = null)
    {
        var user = new Admin();
        var services = database.Services(user, connectionString);
        var context = database.Context(user, connectionString);
        var engine = new SqlServerBackupEngine(context, services.GetRequiredService<IServiceScopeFactory>());
        var service = new BackupService(engine, new ConfigurationWriter(context), new UnitOfWork(context), new Guard(user),
                                        new SecurityAuditor(services.GetRequiredService<IServiceScopeFactory>(), new SystemClock(), user),
                                        user, new SystemClock());

        var composition = new Composition { Services = services, Context = context, Backups = service, Engine = engine };

        // The schedule's folder is the scratch folder, as the tab would save it.
        var current = BackupSettings.FromValues(await engine.ReadValuesAsync(BackupSettings.Prefix));
        if (current.Folder != database.Folder)
            (await service.SaveSettingsAsync(current with { Folder = database.Folder })).IsSuccess.Should().BeTrue();

        return composition;
    }

    private int Events(SecurityEventKind kind) =>
        (int)database.Scalar($"SELECT COUNT(*) FROM aud.SecurityEvents WHERE Kind = {(int)kind};")!;

    [SqlServerFact]
    public async Task Backup_now_writes_a_verified_file_that_the_history_lists()
    {
        await using var app = await ComposeAsync();
        var before = Events(SecurityEventKind.BackupCompleted);

        var outcome = await app.Backups.BackupNowAsync();

        outcome.IsSuccess.Should().BeTrue(outcome.IsFailure ? string.Join(" ", outcome.FirstError.Args) : null);
        outcome.Value.Verification.Should().Be(BackupCheckStatus.Verified, "the test login may verify");
        File.Exists(outcome.Value.Path).Should().BeTrue();
        BackupNaming.FileOf(outcome.Value.Path).Should().StartWith(database.Name + "_").And.EndWith("_manuelle.bak");

        var overview = (await app.Backups.GetOverviewAsync()).Value;
        var entry = overview.Backups.Should().Contain(b => b.Set.Path == outcome.Value.Path).Which;
        entry.Kind.Should().Be(BackupKind.Manual);
        entry.Check.Status.Should().Be(BackupCheckStatus.Verified);
        entry.Set.HasChecksums.Should().BeTrue("WITH CHECKSUM");
        entry.FileBytes.Should().BeGreaterThan(0);
        entry.Set.Description.Should().Contain("Administrateur").And.Contain("SRV-TEST");
        entry.Restorable.Should().BeTrue();
        overview.IsStale.Should().BeFalse();
        overview.LastSuccessAt.Should().NotBeNull();
        overview.State.LastResult!.Status.Should().Be(BackupCheckStatus.Verified);

        (await app.Backups.GetHealthAsync())!.IsStale.Should().BeFalse();
        Events(SecurityEventKind.BackupCompleted).Should().Be(before + 1);
    }

    [SqlServerFact]
    public async Task Folder_is_tried_by_SQL_Server_itself()
    {
        await using var app = await ComposeAsync();

        (await app.Backups.TestFolderAsync(database.Folder)).IsSuccess.Should().BeTrue();
        Directory.GetFiles(database.Folder, "MarocLIS-test-ecriture-*.cer").Should().NotBeEmpty();
        ((int)database.Scalar("SELECT COUNT(*) FROM sys.certificates WHERE name LIKE 'MarocLIS_FolderProbe_%';")!)
            .Should().Be(0, "the probe's certificate is dropped as soon as its file is written");

        // /proc refuses new files, even to root: the error a real backup would give, by its key.
        var refused = await app.Backups.TestFolderAsync("/proc/maroclis-interdit");
        refused.FirstError.ResourceKey.Should().Be("Backup_FolderNotWritable");
        refused.FirstError.Args[0].Should().Be("/proc/maroclis-interdit");
        refused.FirstError.Args[1].Should().NotBeNull();

        (await app.Backups.SaveSettingsAsync(BackupSettings.Default with { Folder = "/proc/maroclis-interdit" }))
            .FirstError.ResourceKey.Should().Be("Backup_FolderNotWritable", "a folder SQL Server cannot write to is not saved");
    }

    [SqlServerFact]
    public async Task Backup_into_a_folder_SQL_Server_cannot_write_to_is_reported_and_logged()
    {
        await using var app = await ComposeAsync();
        var settings = BackupSettings.FromValues(await app.Engine.ReadValuesAsync(BackupSettings.Prefix));
        (await app.Backups.SaveSettingsAsync(settings with { Folder = "/proc/maroclis-interdit" }, checkFolder: false))
            .IsSuccess.Should().BeTrue();
        var failures = Events(SecurityEventKind.BackupFailed);

        try
        {
            var outcome = await app.Backups.BackupNowAsync();

            outcome.FirstError.ResourceKey.Should().Be("Backup_FolderNotWritable");
            var state = BackupState.FromValues(await app.Engine.ReadValuesAsync(BackupState.Prefix));
            state.LastResult!.Status.Should().Be(BackupCheckStatus.Failed);
            state.LastResult.Message.Should().Be("Backup_FolderNotWritable");
            Events(SecurityEventKind.BackupFailed).Should().Be(failures + 1);
        }
        finally
        {
            (await app.Backups.SaveSettingsAsync(settings with { Folder = database.Folder })).IsSuccess.Should().BeTrue();
        }
    }

    [SqlServerFact]
    public async Task Workstation_login_backs_up_but_leaves_verification_and_restore_to_the_server()
    {
        await using (await ComposeAsync()) { }
        await using var app = await ComposeAsync(database.OwnerConnectionString);

        var server = await app.Engine.GetServerInfoAsync();
        server.Login.Should().Be(database.OwnerLogin);
        server.CanVerify.Should().BeFalse("db_owner lacks CREATE DATABASE, which RESTORE VERIFYONLY needs");
        server.CanRestore.Should().BeFalse("db_owner is not the database's owner");

        var outcome = await app.Backups.BackupNowAsync();
        outcome.IsSuccess.Should().BeTrue(outcome.IsFailure ? string.Join(" ", outcome.FirstError.Args) : null);
        outcome.Value.Verification.Should().Be(BackupCheckStatus.Pending);

        // The history is msdb's, readable by every login.
        (await app.Backups.GetOverviewAsync()).Value.Backups.Should().Contain(b => b.Set.Path == outcome.Value.Path
                                                                                 && b.Check.Status == BackupCheckStatus.Pending);

        // And the probe works with db_owner alone.
        (await app.Backups.TestFolderAsync(database.Folder)).IsSuccess.Should().BeTrue();

        var refused = await app.Backups.CanRestoreAsync(OnServer);
        refused.FirstError.ResourceKey.Should().Be("Backup_RestoreNoSqlRights");
        refused.FirstError.Args.Should().Equal(database.OwnerLogin);

        // The real thing, verified by the server's login: RESTORE VERIFYONLY really needed the right.
        (await app.Engine.VerifyAsync(outcome.Value.Path, withChecksum: true)).FirstError.ResourceKey
            .Should().Be("Backup_PermissionDenied");
    }

    [SqlServerFact]
    public async Task Restore_brings_the_data_back_cuts_the_other_connections_and_keeps_a_safety_backup()
    {
        await using var app = await ComposeAsync();

        database.Execute("DELETE FROM cfg.Settings WHERE [Key] = N'Test.Restauration';" +
                         "INSERT INTO cfg.Settings (Id, [Key], [Value], SiteId, Description, IsSecuritySensitive, CreatedAt) " +
                         "VALUES (NEWID(), N'Test.Restauration', N'avant', NULL, N'test', 0, SYSDATETIMEOFFSET());");

        var backup = await app.Backups.BackupNowAsync();
        backup.IsSuccess.Should().BeTrue();
        var setId = (await app.Backups.GetOverviewAsync()).Value.Backups.Single(b => b.Set.Path == backup.Value.Path).Set.BackupSetId;

        // A morning of work after the backup, and a workstation still connected.
        database.Execute("UPDATE cfg.Settings SET [Value] = N'après' WHERE [Key] = N'Test.Restauration';");
        await using var workstation = new SqlConnection(new SqlConnectionStringBuilder(database.ConnectionString)
        {
            ApplicationName = "MarocLIS poste test", Pooling = false,
            // Another machine as SQL Server sees it: the restore lists the other workstations, not
            // the connections of the process asking for it.
            WorkstationID = "POSTE-ACCUEIL",
            // Without it SqlClient reconnects an idle connection behind the caller's back, and the
            // cut would not show: the real workstations do reconnect, which is why they restart.
            ConnectRetryCount = 0
        }.ConnectionString);
        await workstation.OpenAsync();

        var sessions = (await app.Backups.GetSessionsAsync()).Value;
        sessions.Should().Contain(s => s.Program == "MarocLIS poste test" && s.Host == "POSTE-ACCUEIL");
        sessions.Should().NotContain(s => s.Host == Environment.MachineName && s.Program != "MarocLIS poste test",
                                     "this process's own connections are not another workstation");

        (await app.Backups.RestoreAsync(new RestoreRequest(setId, "pas le bon nom", OnServer))).FirstError.ResourceKey
            .Should().Be("Backup_RestoreConfirmationMismatch");
        database.Scalar("SELECT [Value] FROM cfg.Settings WHERE [Key] = N'Test.Restauration';").Should().Be("après");

        var name = (await app.Backups.GetConfirmationNameAsync()).Value;
        name.Should().NotBeNullOrWhiteSpace();
        var steps = new List<RestoreStep>();

        var restored = await app.Backups.RestoreAsync(new RestoreRequest(setId, name.ToUpperInvariant(), OnServer),
                                                      new Progress(steps));

        restored.IsSuccess.Should().BeTrue(restored.IsFailure ? string.Join(" ", restored.FirstError.Args) : null);
        steps.Should().EndWith(RestoreStep.Done);
        restored.Value.SchemaUpgraded.Should().BeFalse("the backup is of the current schema");

        database.Scalar("SELECT [Value] FROM cfg.Settings WHERE [Key] = N'Test.Restauration';").Should().Be("avant");
        database.Scalar($"SELECT user_access_desc + N' ' + state_desc FROM sys.databases WHERE name = N'{database.Name}';", master: true)
            .Should().Be("MULTI_USER ONLINE");

        // The workstation's connection was ended by SQL Server.
        var cut = async () =>
        {
            await using var command = new SqlCommand("SELECT 1;", workstation);
            await command.ExecuteScalarAsync();
        };
        await cut.Should().ThrowAsync<SqlException>();

        // The safety backup is on disk, verified and listed; the outcome is in the restored log.
        File.Exists(restored.Value.SafetyBackupPath).Should().BeTrue();
        var safety = (await app.Backups.GetOverviewAsync()).Value.Backups.Single(b => b.Set.Path == restored.Value.SafetyBackupPath);
        safety.Kind.Should().Be(BackupKind.BeforeRestore);
        Events(SecurityEventKind.RestoreCompleted).Should().Be(1);

        // The start was written before the restore, into the database the restore replaced: it is
        // in the safety backup, not in the restored log.
        Events(SecurityEventKind.RestoreStarted).Should().Be(0);
    }

    private sealed class Progress(List<RestoreStep> steps) : IProgress<RestoreStep>
    {
        public void Report(RestoreStep value) => steps.Add(value);
    }
}
