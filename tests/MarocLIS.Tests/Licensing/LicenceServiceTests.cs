using FluentAssertions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Licensing;
using Xunit;
using static MarocLIS.Tests.Licensing.LicenceTestKit;

namespace MarocLIS.Tests.Licensing;

/// <summary>
/// Paramètres › Licence: the import, the replacement by number and version, the refusals and the
/// security log they leave, the request file, and the markers that hold the clock forward.
/// </summary>
public sealed class LicenceServiceTests
{
    private sealed class Harness
    {
        public Harness(bool authenticated = true, params string[] permissions)
        {
            User = new User(authenticated, permissions.Length == 0 ? [Permissions.Administration.License] : permissions);
            Service = new LicenceService(Store, Holder, new LicenceTrust(), Clock, User, new Guard(User), Auditor);
        }

        public Store Store { get; } = new();
        public LicenceStatusHolder Holder { get; } = new();
        public Clock Clock { get; } = new();
        public User User { get; }
        public Auditor Auditor { get; } = new();
        public LicenceService Service { get; }

        public string Licence(int version = 1, LicenceKind kind = LicenceKind.Annual, int days = 365) =>
            Sign(Annual(days) with { Version = version, Kind = kind, ServerId = Store.ServerId });
    }

    [Fact]
    public async Task AValidLicence_IsImported_InForceAtOnce_AndLogged()
    {
        var harness = new Harness();
        var changed = 0;
        harness.Holder.Changed += (_, _) => changed++;

        var imported = await harness.Service.ImportAsync(harness.Licence(), "licence.lic");

        imported.IsSuccess.Should().BeTrue();
        harness.Holder.Current.State.Should().Be(LicenceState.Valid, "no restart: the process's state is replaced");
        changed.Should().Be(1);
        harness.Store.Records.Should().ContainSingle(r => r.Number == "LIC-2026-0042" && r.Version == 1
                                                          && r.ImportedByUserName == "admin");
        harness.Auditor.Events.Should().ContainSingle(e => e.Kind == SecurityEventKind.LicenceImported && e.Succeeded);
        harness.Auditor.Events[0].Details.Should().Contain("LIC-2026-0042").And.Contain("licence.lic");
    }

    [Fact]
    public async Task ANewerVersion_ReplacesTheLicence_AndAnOlderOne_IsRefused()
    {
        var harness = new Harness();
        (await harness.Service.ImportAsync(harness.Licence(1))).IsSuccess.Should().BeTrue();
        (await harness.Service.ImportAsync(harness.Licence(3))).IsSuccess.Should().BeTrue();

        harness.Store.Records.Single(r => r.Version == 1).ReplacedAt.Should().NotBeNull();
        harness.Auditor.Events.Should().Contain(e => e.Kind == SecurityEventKind.LicenceReplaced
                                                     && e.Details!.Contains("en remplacement de LIC-2026-0042 v1"));

        var older = await harness.Service.ImportAsync(harness.Licence(2));
        older.FirstError.ResourceKey.Should().Be("Licence_OlderVersion");

        var again = await harness.Service.ImportAsync(harness.Licence(3));
        again.FirstError.ResourceKey.Should().Be("Licence_AlreadyImported");

        harness.Auditor.Events.Count(e => e.Kind == SecurityEventKind.LicenceRefused).Should().Be(2);
        harness.Holder.Current.Terms!.Version.Should().Be(3);
    }

    [Fact]
    public async Task AnAlteredLicence_IsRefused_WithItsCause_AndLogged()
    {
        var harness = new Harness();
        var altered = harness.Licence().Replace("\"postes\": 5", "\"postes\": 50", StringComparison.Ordinal);
        altered.Should().Contain("\"postes\": 50");

        var refused = await harness.Service.ImportAsync(altered, @"C:\Users\lab\licence.lic");

        refused.FirstError.ResourceKey.Should().Be("Licence_SignatureInvalid");
        harness.Store.Records.Should().BeEmpty();
        harness.Auditor.Events.Should().ContainSingle(e => e.Kind == SecurityEventKind.LicenceRefused && !e.Succeeded
                                                           && e.Reason == "Licence_SignatureInvalid");
    }

    [Fact]
    public async Task ALicenceForAnotherServer_OrAlreadyEnded_IsRefused()
    {
        var harness = new Harness();

        (await harness.Service.ImportAsync(Sign(Annual()))).FirstError.ResourceKey.Should().Be("Licence_WrongServer");
        (await harness.Service.ImportAsync(harness.Licence(days: -30))).FirstError.ResourceKey
            .Should().Be("Licence_AlreadyExpired");
    }

    [Fact]
    public async Task Importing_TakesTheLicencesRight()
    {
        var harness = new Harness(true, Permissions.Administration.ManageSettings);

        (await harness.Service.ImportAsync(harness.Licence())).FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
    }

    [Fact]
    public async Task BeforeSignIn_ALicenceCanBeImported_OnlyWhileThereIsNone()
    {
        var harness = new Harness(authenticated: false);
        await harness.Service.RefreshAsync();
        harness.Holder.Current.State.Should().Be(LicenceState.Missing);

        (await harness.Service.ImportAsync(harness.Licence(1))).IsSuccess.Should().BeTrue();
        harness.Store.Records.Single().Workstation.Should().Be(Environment.MachineName);

        (await harness.Service.ImportAsync(harness.Licence(2))).FirstError.ResourceKey
            .Should().Be("Licence_ImportNeedsSignIn", "replacing a licence takes someone who answers for it");
    }

    [Fact]
    public async Task ATrainingLicence_IsRefusedOnAWorkingDatabase()
    {
        var harness = new Harness();
        harness.Store.OrderCount = 1500;
        harness.Store.HasPatients = true;

        var training = Sign(Annual() with { Kind = LicenceKind.Training, EndsOn = null, Workstations = 1, ServerId = null });

        (await harness.Service.ImportAsync(training)).FirstError.ResourceKey.Should().Be("Licence_TrainingTooManyOrders");
    }

    [Fact]
    public async Task TheRefresh_KeepsTheLatestDaySeen_AndTheStartOfTheReadOnlyMode()
    {
        var harness = new Harness();
        harness.Store.HasPatients = true;

        await harness.Service.RefreshAsync();

        harness.Store.LatestDateSeen.Should().Be(Today);
        harness.Store.RestrictedSince.Should().Be(Today, "the bench days count from the first day without a licence");
        harness.Holder.Current.BenchClearingUntil.Should().Be(Today.AddDays(7));

        // A day later the start stays where it was.
        harness.Clock.Now = harness.Clock.Now.AddDays(1);
        await harness.Service.RefreshAsync();
        harness.Store.RestrictedSince.Should().Be(Today);
        harness.Store.LatestDateSeen.Should().Be(Today.AddDays(1));

        // The clock put back: the day seen does not move back, and the evaluation does not either.
        harness.Clock.Now = harness.Clock.Now.AddDays(-10);
        var status = await harness.Service.RefreshAsync();
        harness.Store.LatestDateSeen.Should().Be(Today.AddDays(1));
        status.Today.Should().Be(Today.AddDays(1));
        status.ClockRolledBack.Should().BeTrue();

        // A licence imported: the read-only mode is forgotten.
        harness.Clock.Now = harness.Clock.Now.AddDays(10);
        (await harness.Service.ImportAsync(harness.Licence())).IsSuccess.Should().BeTrue();
        harness.Store.RestrictedSince.Should().BeNull();
    }

    [Fact]
    public async Task ALicenceThatStopsBeingValid_IsSaidOnceInTheSecurityLog()
    {
        var harness = new Harness();
        (await harness.Service.ImportAsync(harness.Licence())).IsSuccess.Should().BeTrue();

        // The database restored on another server.
        harness.Store.MachineName = "AUTRESRV";
        await harness.Service.RefreshAsync();
        await harness.Service.RefreshAsync();

        harness.Holder.Current.State.Should().Be(LicenceState.Invalid);
        harness.Auditor.Events.Count(e => e.Kind == SecurityEventKind.LicenceRefused).Should().Be(1);
    }

    [Fact]
    public async Task TheRequest_CarriesThisServersIdentifier_AndTheLicenceInPlace()
    {
        var harness = new Harness();
        await harness.Service.ImportAsync(harness.Licence(2));

        var request = await harness.Service.BuildRequestAsync("1.4.0");

        request.ServerId.Should().Be(harness.Store.ServerId);
        request.Site.Should().Be("Laboratoire Al Amal");
        request.Authorisation.Should().Be("AUT-123/2019");
        request.CurrentNumber.Should().Be("LIC-2026-0042");
        request.CurrentVersion.Should().Be(2);
    }

    [Fact]
    public async Task TheWorkstationCount_IsTheLicences_OrNoneInReadOnly()
    {
        var registry = new RecordingRegistry();
        var presence = new WorkstationPresenceService(registry, new FixedLicenceState(FullStatus() with { WorkstationLimit = 3 }), new Clock());
        await presence.ClaimAsync(new string('P', 80), "accueil", "1.4.0");
        registry.Limit.Should().Be(3);
        registry.Machine.Should().HaveLength(Workstation.MachineNameMaxLength);

        var readOnly = new WorkstationPresenceService(registry, new FixedLicenceState(ReadOnlyStatus()), new Clock());
        await readOnly.ClaimAsync("POSTE-01", "accueil", null);
        registry.Limit.Should().BeNull();
    }

    private sealed class RecordingRegistry : IWorkstationRegistry
    {
        public int? Limit { get; private set; }
        public string? Machine { get; private set; }

        public Task<WorkstationClaim> ClaimAsync(string machineName, string? userName, string? appVersion, int? limit,
                                                 DateTimeOffset now, CancellationToken ct = default)
        {
            Machine = machineName;
            Limit = limit;
            return Task.FromResult(new WorkstationClaim(true, 1, limit, []));
        }

        public Task ReleaseAsync(string machineName, DateTimeOffset now, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ActiveWorkstation>> ActiveAsync(DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActiveWorkstation>>([]);
    }

    // ---- Arabic, a module --------------------------------------------------------------------------

    [Fact]
    public async Task Arabic_IsOffWithoutItsModule_AndCannotBeSwitchedOn()
    {
        var withoutArabic = new FixedLicenceState(FullStatus() with
        {
            Modules = new HashSet<string>([LicenceModules.Instruments], StringComparer.Ordinal)
        });
        var user = new User(true, Permissions.Administration.ManageSettings);
        var service = new ActiveLanguagesService(new AllLanguages(), new ActiveLanguagesCache(), new NoWriter(),
                                                 new NoUnitOfWork(), new Guard(user), new Clock(), withoutArabic);

        (await service.GetAsync()).Active.Should().Equal(AppLanguage.French, AppLanguage.English);
        (await service.SaveAsync([AppLanguage.Arabic])).FirstError.ResourceKey.Should().Be("Licence_ModuleMissing");
        (await service.SaveAsync([AppLanguage.English])).IsSuccess.Should().BeTrue();

        var readOnly = new ActiveLanguagesService(new AllLanguages(), new ActiveLanguagesCache(), new NoWriter(),
                                                  new NoUnitOfWork(), new Guard(user), new Clock(),
                                                  new FixedLicenceState(ReadOnlyStatus()));
        (await readOnly.SaveAsync([AppLanguage.English])).FirstError.ResourceKey.Should().Be("Licence_ReadOnlySettings");
    }

    private sealed class AllLanguages : ILanguageSettingsQueries
    {
        public Task<IReadOnlyDictionary<string, string?>> GetSettingValuesAsync(string prefix, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(
                new Dictionary<string, string?> { [LanguageSettings.ActiveLanguagesKey] = "French,Arabic,English" });
    }

    private sealed class NoWriter : IConfigurationWriter
    {
        public Task<MarocLIS.Domain.Sequencing.NumberSequence?> GetSequenceAsync(Guid sequenceId, CancellationToken ct = default) =>
            Task.FromResult<MarocLIS.Domain.Sequencing.NumberSequence?>(null);

        public Task<MarocLIS.Domain.Organization.AppSetting?> GetSettingAsync(string key, Guid? siteId, CancellationToken ct = default) =>
            Task.FromResult<MarocLIS.Domain.Organization.AppSetting?>(null);

        public Task AddSettingAsync(MarocLIS.Domain.Organization.AppSetting setting, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoUnitOfWork : MarocLIS.Application.Abstractions.IUnitOfWork
    {
        public string? AuditReason { get; set; }
        public Task<int> SaveChangesAsync(CancellationToken ct = default) => Task.FromResult(1);
        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
