using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Infrastructure.Persistence.Demo;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Infrastructure.Persistence;

/// <summary>
/// Brings the database up on first run and keeps the reference data current.
/// <para>
/// The schema is applied by running the EF migrations, not by <c>EnsureCreated</c>. The two are not
/// interchangeable and mixing them is a trap: <c>EnsureCreated</c> builds the tables but writes
/// nothing to <c>cfg.__MigrationsHistory</c>, so a database stood up that way looks completely
/// unmigrated — the next <c>dotnet ef database update</c> tries to create all 65 tables again and
/// fails, and the site has no upgrade path without hand-writing the history rows. Since
/// <c>docs/DEPLOYMENT.md</c> tells a production site to deploy by migration, the first-run path has
/// to agree with it.
/// </para>
/// <para>
/// <c>Migrate</c> also covers both cases in one call — a new database and one already at an earlier
/// migration — which is what removes the need for the "created or mismatched" branch this used to
/// carry.
/// </para>
/// <para>
/// The append-only triggers on the traceability tables, which EF cannot express, now live in the
/// migration that creates those tables. They were previously applied here, which meant only
/// databases created by this code path ever got them.
/// </para>
/// </summary>
public sealed class DatabaseInitializer(
    LisDbContext context,
    IPasswordHasher passwordHasher,
    IClock clock,
    ILogger<DatabaseInitializer>? logger = null,
    DemoDataSeeder? demoDataSeeder = null)
{
    /// <summary>
    /// Ensures the database exists, has the current schema, and holds the reference data the
    /// application cannot start without.
    /// </summary>
    public async Task<Result> InitializeAsync(bool seedDemoData = false, CancellationToken ct = default)
    {
        try
        {
            // Materialised once: GetPendingMigrationsAsync returns a lazy sequence, and asking it
            // for Any() and then Count() enumerated it twice.
            var pending = (await context.Database.GetPendingMigrationsAsync(ct)).ToList();

            if (pending.Count > 0)
            {
                logger?.LogInformation("Applying {Count} migration(s) to the MarocLIS database.",
                                        pending.Count);
                await context.Database.MigrateAsync(ct);
            }

            // Not a migration concern: a database that answers but holds somebody else's schema is
            // a misconfigured connection string, and saying so is more use than the foreign-key
            // error the seeder would otherwise raise.
            if (!await CanConnectAndHasSchemaAsync(ct))
                return Result.Failure(Error.Conflict("Db_SchemaMismatch"));

            await WarnIfTraceabilityUnprotectedAsync(ct);

            // One workstation at a time. The seeders check what exists and insert what does not,
            // and two workstations started together both found the administrator missing: the
            // second insert broke the unique index and that workstation refused to start. The
            // lock is the session's, on a connection held open until seeding is over, so it also
            // covers the demonstration seeder, which works through scopes of its own.
            await context.Database.OpenConnectionAsync(ct);

            try
            {
                if (!await AcquireSeedingLockAsync(context.Database.GetDbConnection(), SeedingLockTimeout, ct))
                {
                    logger?.LogError("Another workstation has been initialising the database for {Minutes} minutes.",
                                     SeedingLockTimeout.TotalMinutes);
                    return Result.Failure(Error.Conflict("Db_InitializationFailed", "seeding lock timeout"));
                }

                try
                {
                    await SeedReferenceDataAsync(ct);

                    // Demonstration data only on explicit request (Application:SeedDemoDataOnFirstRun),
                    // which must be false on any installation holding real patients — see
                    // DemoDataSeeder. It runs at every start while the flag is on and completes
                    // whatever is missing; a section it cannot complete is logged, never fatal.
                    if (seedDemoData)
                    {
                        if (demoDataSeeder is null)
                            logger?.LogWarning("Demonstration data requested but no DemoDataSeeder is registered.");
                        else
                            await demoDataSeeder.SeedAsync(ct);
                    }
                }
                finally
                {
                    await ReleaseSeedingLockAsync(context.Database.GetDbConnection());
                }
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }

            return Result.Success();
        }
        catch (SqlException ex)
        {
            logger?.LogError(ex, "Could not initialise the database.");
            return Result.Failure(Error.Conflict("Db_InitializationFailed", ex.Message));
        }
        catch (DbUpdateException ex)
        {
            // Reported like any other database failure. It used to escape as an unexpected
            // exception, and the operator saw only that the application could not start.
            logger?.LogError(ex, "Could not initialise the database.");
            return Result.Failure(Error.Conflict("Db_InitializationFailed", ex.GetBaseException().Message));
        }
    }

    /// <summary>Name of the application lock that serialises seeding across workstations.</summary>
    internal const string SeedingLockResource = "MarocLIS.Seeding";

    /// <summary>
    /// How long a workstation waits for another to finish seeding. Generous: a first run with the
    /// demonstration data takes a while, and a lock left by a workstation that died goes with its
    /// session.
    /// </summary>
    private static readonly TimeSpan SeedingLockTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The reference data, tried a second time when it lost an insert race. The lock prevents the
    /// race between workstations running this version; one still running an earlier version takes
    /// no lock, and its duplicate is not a reason to refuse to start — on the second pass the row
    /// it inserted is found and skipped.
    /// </summary>
    private async Task SeedReferenceDataAsync(CancellationToken ct)
    {
        try
        {
            await ReferenceDataSeeder.SeedAsync(context, passwordHasher, clock, ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            logger?.LogWarning(ex, "Reference data inserted concurrently by another workstation; seeding again.");

            // The rejected inserts are still tracked and would be sent again.
            context.ChangeTracker.Clear();
            await ReferenceDataSeeder.SeedAsync(context, passwordHasher, clock, ct);
        }
    }

    /// <summary>A unique index or primary key violation: SQL Server errors 2601 and 2627.</summary>
    internal static bool IsDuplicateKey(DbUpdateException ex) =>
        ex.GetBaseException() is SqlException sql && sql.Errors.Cast<SqlError>().Any(e => e.Number is 2601 or 2627);

    /// <summary>
    /// Takes the seeding lock for the connection's session; false when another session held it for
    /// the whole of <paramref name="timeout"/>.
    /// </summary>
    internal static async Task<bool> AcquireSeedingLockAsync(System.Data.Common.DbConnection connection,
                                                             TimeSpan timeout, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "sp_getapplock";
        command.CommandType = System.Data.CommandType.StoredProcedure;
        // Longer than the lock wait, or the command would be abandoned before SQL Server answers.
        command.CommandTimeout = (int)timeout.TotalSeconds + 30;

        AddParameter(command, "@Resource", SeedingLockResource);
        AddParameter(command, "@LockMode", "Exclusive");
        AddParameter(command, "@LockOwner", "Session");
        AddParameter(command, "@LockTimeout", (int)timeout.TotalMilliseconds);

        var status = command.CreateParameter();
        status.ParameterName = "@Status";
        status.DbType = System.Data.DbType.Int32;
        status.Direction = System.Data.ParameterDirection.ReturnValue;
        command.Parameters.Add(status);

        await command.ExecuteNonQueryAsync(ct);

        // 0 granted at once, 1 granted after waiting; negative is a timeout, a deadlock or an error.
        return status.Value is int code && code >= 0;
    }

    /// <summary>
    /// Releases the seeding lock. Never throws: a connection that broke has already released it
    /// along with its session, and a failure here must not mask the outcome of the seeding.
    /// </summary>
    internal static async Task ReleaseSeedingLockAsync(System.Data.Common.DbConnection connection)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                IF APPLOCK_MODE('public', @Resource, 'Session') <> 'NoLock'
                    EXEC sp_releaseapplock @Resource = @Resource, @LockOwner = 'Session';
                """;
            AddParameter(command, "@Resource", SeedingLockResource);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception)
        {
            // See above.
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    public Task<bool> CanConnectAsync(CancellationToken ct = default) =>
        context.Database.CanConnectAsync(ct);

    private async Task<bool> CanConnectAndHasSchemaAsync(CancellationToken ct)
    {
        if (!await context.Database.CanConnectAsync(ct)) return false;

        // A cheap probe: if the users table is absent, this is not a MarocLIS database.
        var count = await context.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value]
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = 'sec' AND t.name = 'Users'
                """)
            .FirstOrDefaultAsync(ct);

        return count > 0;
    }

    /// <summary>
    /// Logs a warning naming any traceability table whose append-only trigger is missing or
    /// disabled.
    /// <para>
    /// A warning and not a refusal to start. The triggers are installed by the initial migration,
    /// but a <c>db_owner</c> may legitimately disable one to make a correction, and the thing that
    /// goes wrong afterwards is forgetting to put it back — leaving an audit trail the application
    /// itself could rewrite, with nothing on screen to say so. Halting a laboratory's LIS over it
    /// would stop patient care to protect a log, which is the wrong trade; naming the table in the
    /// startup log is what lets the next audit find it.
    /// </para>
    /// </summary>
    private async Task WarnIfTraceabilityUnprotectedAsync(CancellationToken ct)
    {
        var unprotected = await GetUnprotectedTraceabilityTablesAsync(ct);

        if (unprotected.Count > 0)
        {
            logger?.LogWarning(
                "Append-only protection is missing or disabled on: {Triggers}. The traceability " +
                "tables can be modified until it is restored.",
                string.Join(", ", unprotected));
        }
    }

    /// <summary>
    /// The append-only triggers that should be guarding the traceability tables and are not.
    /// Empty when the installation is intact.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetUnprotectedTraceabilityTablesAsync(
        CancellationToken ct = default)
    {
        var expected = new[]
        {
            "aud.TR_AuditEntries_NoUpdate",
            "aud.TR_DataAccessLog_NoUpdate",
            "aud.TR_SecurityEvents_NoUpdate",
            "res.TR_ResultRevisions_NoUpdate"
        };

        // The schema comes from the parent table: sys.triggers has no schema_id of its own, because
        // a DML trigger lives in whatever schema owns the table it is attached to.
        var present = await context.Database
            .SqlQuery<string>($"""
                SELECT SCHEMA_NAME(o.schema_id) + '.' + tr.name AS [Value]
                FROM sys.triggers tr
                JOIN sys.objects o ON o.object_id = tr.parent_id
                WHERE tr.parent_class = 1 AND tr.is_disabled = 0
                """)
            .ToListAsync(ct);

        return [.. expected.Except(present, StringComparer.OrdinalIgnoreCase)];
    }
}
