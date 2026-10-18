using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MarocLIS.Api.Contracts;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Application.Samples;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Infrastructure.Security;
using MarocLIS.Tests.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Api;

/// <summary>
/// A scratch database for the phone's API, created from the model, and the API itself started in
/// memory on it — the real pipeline, cookie and session re-check included.
/// </summary>
public sealed class MobileApiDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_Mobile_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    public IPasswordHasher Hasher { get; } = new BCryptPasswordHasher(10);

    private readonly bool _created;
    private MobileApiFactory? _factory;

    public MobileApiDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            ConnectionString = "";
            return;
        }

        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = Name }.ConnectionString;

        using var context = Context();
        context.Database.EnsureCreated();
        _created = true;

        context.Database.ExecuteSqlRaw("EXEC sp_MSforeachtable 'ALTER TABLE ? NOCHECK CONSTRAINT ALL'");
    }

    public LisDbContext Context() =>
        new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(ConnectionString).Options);

    /// <summary>The API, started once for the class.</summary>
    public MobileApiFactory Api => _factory ??= new MobileApiFactory(ConnectionString);

    public void Dispose()
    {
        _factory?.Dispose();
        if (!_created) return;
        SqlConnection.ClearAllPools();
        using var context = Context();
        context.Database.EnsureDeleted();
    }
}

/// <summary>
/// The API on the scratch database. The test server has no TCP peer, so a filter gives each request
/// the address the test names in <see cref="PeerHeader"/> — the loopback by default, which is where
/// <c>tailscale serve</c> connects from.
/// </summary>
public sealed class MobileApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string PeerHeader = "X-Test-Peer";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:ConnectionString", connectionString);
        builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, PeerAddress>());
    }

    private sealed class PeerAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) =>
            {
                var peer = context.Request.Headers[PeerHeader].ToString();
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer.Length > 0 ? peer : "127.0.0.1");
                return following(context);
            });
            next(app);
        };
    }
}

/// <summary>
/// « Validation à distance », phase 1: the API the biologist's phone validates through, replayed
/// against SQL Server — who may sign in, how a session ends, the worklist and its chips, an open
/// file, validation with its gates, a value changed under the biologist's eyes, a critical call,
/// the report and its preview, and the device recorded on every trace.
/// </summary>
public sealed class MobileApiDatabaseTests(MobileApiDatabase database) : IClassFixture<MobileApiDatabase>
{
    private const string Password = "Mobile-Valide-2026!";
    private const string Login = "dr.alami@example.ma";
    private const string TailnetAddress = "100.64.0.7";
    private static readonly string Device = $"Mobile · {Login} ({TailnetAddress})";

    /// <summary>The operator the test data is created under, through the services.</summary>
    private sealed class Operator : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; init; }
        public string UserName => "preparation";
        public string FullName => "Préparation des données";
        public Guid SiteId { get; init; }
        public string SiteCode { get; init; } = "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.MedicalBiologist;
        public IReadOnlyList<Guid> AccessibleSiteIds { get; init; } = [];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>(MarocLIS.Domain.Identity.Permissions.All);
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => true;
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "PAILLASSE-MOBILE";
    }

    private sealed class Renderer : IReportRenderer
    {
        public byte[] Render(ReportModel model) => [0x25, 0x50, 0x44, 0x46];
        public int CountPages(byte[] pdf) => 1;
    }

    private sealed record Lab(LabSite Site, AppUser Biologist, Role Role, AnalysisTest Glucose,
                              AnalysisTest Creatinine, AnalysisTest Sodium, Patient Patient);

    private sealed record File(Guid OrderId, Dictionary<Guid, TestResult> ByAnalysis);

    private static readonly string[] MobileBiologist =
    [
        Permissions.Results.View, Permissions.Results.BiologicalValidate, Permissions.Results.ValidateRemote,
        Permissions.Results.EnterCritical, Permissions.Reports.View, Permissions.Reports.Print
    ];

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    // ---- Data ---------------------------------------------------------------------------------

    private static AnalysisTest Analysis(string code, string name, Department department, SpecimenType specimen,
                                         decimal? criticalHigh = null)
    {
        var analysis = new AnalysisTest(Unique(code), new MultilingualText(name), department.Id, specimen.Id)
        {
            RoutineTurnaroundHours = 4
        };

        var parameter = new TestParameter(Unique(code), new MultilingualText(name), ResultValueType.Numeric)
        {
            Decimals = 1,
            CriticalHigh = criticalHigh
        };
        parameter.AddReferenceRange(ReferenceRange.Numeric(1m, 100m));
        analysis.AddParameter(parameter);

        analysis.AddTariff(new AnalysisTestTariff(analysis.Id, payerId: null, DateOnly.FromDateTime(DateTime.Today).AddYears(-1))
        {
            LetterKey = "B",
            Coefficient = 20m,
            LetterKeyValue = 1m,
            Price = 20m,
            IsVatExempt = true
        });

        analysis.Validate().IsSuccess.Should().BeTrue();
        return analysis;
    }

    private async Task<Lab> LabAsync(params string[] permissions)
    {
        var biologist = new AppUser(Unique("bio").ToLowerInvariant(), "Dr Karim ALAMI", database.Hasher.Hash(Password))
        {
            Qualification = ProfessionalQualification.MedicalBiologist,
            ProfessionalOrderNumber = "CNOM-MOB-1"
        };
        biologist.SetPassword(biologist.PasswordHash, DateTimeOffset.Now);

        var site = new LabSite(Unique("L"), new MultilingualText("Laboratoire mobile"), LabSiteKind.MainLaboratory)
        {
            LegalName = "LABORATOIRE MOBILE SARL",
            Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(),
            FiscalId = "45128796",
            AddressLine = new MultilingualText("1, rue du Test"),
            City = "Rabat",
            HealthMinistryAuthorizationNumber = "AUT-MOB-1",
            ResponsibleBiologistUserId = biologist.Id
        };

        var role = new Role(Unique("MOBILE_"), new MultilingualText("Biologiste nomade"));
        role.GrantRange(permissions.Length > 0 ? permissions : MobileBiologist);

        biologist.GrantSiteAccess(site.Id, isDefault: true);
        biologist.AssignRole(role.Id);

        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sérum"));

        var glucose = Analysis("GLY", "Glycémie", department, specimen, criticalHigh: 400m);
        var creatinine = Analysis("CRE", "Créatinine", department, specimen);
        var sodium = Analysis("NA", "Sodium", department, specimen);

        var patient = new Patient(Unique("P"), "TAZI", "Imane", new DateOnly(1981, 6, 12), Gender.Female, site.Id)
        {
            SearchKey = PatientSearchKey.Build("TAZI", "Imane")
        };

        await using var context = database.Context();
        context.Roles.Add(role);
        context.Users.Add(biologist);
        context.Sites.Add(site);
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.AnalysisTests.AddRange(glucose, creatinine, sodium);
        context.Patients.Add(patient);
        await context.SaveChangesAsync();

        return new Lab(site, biologist, role, glucose, creatinine, sodium, patient);
    }

    private ServiceProvider Preparation(Lab lab)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator
        {
            // Not the biologist: what the phone does must be told apart from what was prepared.
            UserId = Guid.NewGuid(), SiteId = lab.Site.Id, SiteCode = lab.Site.Code, AccessibleSiteIds = [lab.Site.Id]
        });
        services.AddSingleton<IReportRenderer>(new Renderer());
        services.AddIsolatedQueries<IResultQueries, ResultQueries>();
        services.AddIsolatedQueries<IValidationQueries, ValidationQueries>();
        services.AddIsolatedQueries<IReportQueries, ReportQueries>();
        services.AddIsolatedQueries<IPatientQueries, PatientQueries>();
        services.AddIsolatedQueries<ICatalogQueries, CatalogQueries>();
        services.AddIsolatedQueries<IOrderQueries, OrderQueries>();
        services.AddIsolatedQueries<ISampleQueries, SampleQueries>();
        services.AddIsolatedQueries<ISubcontractQueries, SubcontractQueries>();
        services.AddIsolatedQueries<IRecollectionQueries, RecollectionQueries>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> On<TService, T>(ServiceProvider provider, Func<TService, Task<T>> action)
        where TService : notnull
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    /// <summary>
    /// A file of the lab's patient: each analysis with its value, technically validated or only entered.
    /// </summary>
    private async Task<File> FileAsync(Lab lab, OrderPriority priority,
                                       params (AnalysisTest Analysis, string Value, bool Checked)[] values)
    {
        using var provider = Preparation(lab);

        var registered = await On<OrderRegistrationService, Result<RegisteredOrder>>(provider,
            s => s.RegisterAsync(new RegisterOrderRequest
            {
                PatientId = lab.Patient.Id,
                AnalysisTestIds = [.. values.Select(v => v.Analysis.Id)],
                PrescriberFreeText = "Dr Prescripteur",
                Priority = priority,
                ClinicalInformation = "Bilan de contrôle"
            }));
        registered.IsSuccess.Should().BeTrue(string.Join(", ", registered.Errors.Select(e => e.ResourceKey)));
        var order = registered.Value;

        foreach (var label in order.Labels)
        {
            (await On<SampleWorkflowService, Result>(provider, s => s.RecordCollectionAsync(
                [new RecordCollectionRequest { SampleId = label.SampleId, VolumeMl = 5m }]))).IsSuccess.Should().BeTrue();
            (await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.ReceiveAsync(
                [new ReceiveSampleRequest { SampleId = label.SampleId }]))).Value.Received.Should().Be(1);
        }

        var results = await ResultsByAnalysisAsync(lab, order.OrderId);

        (await On<ResultEntryService, Result<ResultEntryOutcome>>(provider, s => s.SubmitAsync(order.OrderId,
            [.. values.Select(v => new ResultEntry(results[v.Analysis.Id].Id, v.Value))])))
            .Value.Accepted.Should().Be(values.Length);

        var toCheck = values.Where(v => v.Checked).Select(v => results[v.Analysis.Id].Id).ToList();
        if (toCheck.Count > 0)
            (await On<ResultEntryService, Result>(provider, s => s.TechnicallyValidateAsync(order.OrderId, toCheck)))
                .IsSuccess.Should().BeTrue();

        return new File(order.OrderId, await ResultsByAnalysisAsync(lab, order.OrderId));
    }

    private async Task<Dictionary<Guid, TestResult>> ResultsByAnalysisAsync(Lab lab, Guid orderId)
    {
        await using var context = database.Context();
        var results = await context.TestResults.AsNoTracking().Where(r => r.LabOrderId == orderId).ToListAsync();
        var byParameter = new[] { lab.Glucose, lab.Creatinine, lab.Sodium }
            .ToDictionary(a => a.Parameters.Single().Id, a => a.Id);
        return results.ToDictionary(r => byParameter[r.TestParameterId]);
    }

    // ---- The phone ------------------------------------------------------------------------------

    /// <summary>A phone behind <c>tailscale serve</c>: from the loopback, with the tailnet's headers.</summary>
    private HttpClient Phone(string? login = Login, string? peer = null, bool clientHeader = true)
    {
        var client = database.Api.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        if (clientHeader) client.DefaultRequestHeaders.Add(ApiProtocol.RequestHeader, ApiProtocol.RequestHeaderValue);
        if (login is not null) client.DefaultRequestHeaders.Add("Tailscale-User-Login", login);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", TailnetAddress);
        if (peer is not null) client.DefaultRequestHeaders.Add(MobileApiFactory.PeerHeader, peer);

        return client;
    }

    private async Task<HttpClient> SignedInAsync(Lab lab, string? login = Login, string? peer = null)
    {
        var phone = Phone(login, peer);
        var response = await phone.PostAsJsonAsync(ApiProtocol.SessionRoute, new SignInRequest(lab.Biologist.UserName, Password));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return phone;
    }

    private static async Task<string> ProblemKeyAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("errors")[0].GetProperty("key").GetString()!;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static Task<HttpResponseMessage> ValidateAsync(HttpClient phone, Guid orderId,
                                                           params ValidationDecisionRequest[] decisions) =>
        phone.PostAsJsonAsync($"/api/dossiers/{orderId}/validation", new ValidationRequest(decisions));

    private static ValidationDecisionRequest Release(ResultRow row) =>
        new(row.ResultId, true, DisplayedEnteredAt: row.EnteredAt);

    // ---- 1. Who may sign in -----------------------------------------------------------------------

    [SqlServerFact]
    public async Task A_SignIn_IsRefusedWithoutRemoteValidation_OrWithoutBiologicalValidation_AndRecordedAgainstThePhone()
    {
        var withoutRemote = await LabAsync(Permissions.Results.View, Permissions.Results.BiologicalValidate);
        var withoutBiological = await LabAsync(Permissions.Results.View, Permissions.Results.ValidateRemote);

        foreach (var lab in new[] { withoutRemote, withoutBiological })
        {
            var refused = await Phone().PostAsJsonAsync(ApiProtocol.SessionRoute,
                                                         new SignInRequest(lab.Biologist.UserName, Password));

            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ProblemKeyAsync(refused)).Should().Be("Mobile_RemoteValidationNotGranted");
            (await refused.Content.ReadAsStringAsync()).Should().Contain("valider à distance");
            refused.Headers.TryGetValues("Set-Cookie", out _).Should().BeFalse("no session is opened");

            await using var context = database.Context();
            var denial = await context.SecurityEvents.AsNoTracking()
                .SingleAsync(e => e.AttemptedUserName == lab.Biologist.UserName && e.Kind == SecurityEventKind.AuthorizationDenied);
            denial.FailureReasonKey.Should().Be("Mobile_RemoteValidationNotGranted");
            denial.Workstation.Should().Be(Device);
        }

        // A wrong password is the workstation's answer, counted towards the same lockout.
        var wrong = await Phone().PostAsJsonAsync(ApiProtocol.SessionRoute,
                                                  new SignInRequest(withoutRemote.Biologist.UserName, "Pas-Le-Bon-2026!"));
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ProblemKeyAsync(wrong)).Should().Be("Auth_InvalidCredentials");

        await using (var context = database.Context())
            (await context.Users.AsNoTracking().SingleAsync(u => u.Id == withoutRemote.Biologist.Id))
                .FailedLoginAttempts.Should().Be(1);
    }

    [SqlServerFact]
    public async Task B_SignIn_OpensAStrictHttpOnlySession_ThatSigningOutEnds()
    {
        var lab = await LabAsync();
        var phone = Phone();

        var signIn = await phone.PostAsJsonAsync(ApiProtocol.SessionRoute, new SignInRequest(lab.Biologist.UserName, Password));
        var info = await ReadAsync<SessionInfo>(signIn);

        info.FullName.Should().Be("Dr Karim ALAMI");
        info.Qualification.Should().Be(nameof(ProfessionalQualification.MedicalBiologist));
        info.SiteCode.Should().Be(lab.Site.Code);
        info.IdleTimeoutMinutes.Should().Be(15);
        info.Workstation.Should().Be(Device);

        var cookie = signIn.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        cookie.Should().Contain("httponly").And.Contain("samesite=strict");
        // Plain HTTP on the loopback here: Secure only when tailscale serve says the phone used HTTPS.
        cookie.Should().NotContain("secure;");

        signIn.Headers.CacheControl!.NoStore.Should().BeTrue();

        (await ReadAsync<SessionInfo>(await phone.GetAsync(ApiProtocol.SessionRoute))).UserName.Should().Be(lab.Biologist.UserName);

        (await phone.DeleteAsync(ApiProtocol.SessionRoute)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await phone.GetAsync(ApiProtocol.SessionRoute)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Behind tailscale serve, over HTTPS: the cookie is Secure.
        var secure = Phone();
        secure.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        var overHttps = await secure.PostAsJsonAsync(ApiProtocol.SessionRoute, new SignInRequest(lab.Biologist.UserName, Password));
        overHttps.StatusCode.Should().Be(HttpStatusCode.OK);
        overHttps.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant().Should().Contain("secure");

        await using var context = database.Context();
        var events = await context.SecurityEvents.AsNoTracking()
            .Where(e => e.AttemptedUserName == lab.Biologist.UserName).ToListAsync();
        events.Should().Contain(e => e.Kind == SecurityEventKind.LoginSucceeded && e.Workstation == Device);
        events.Should().Contain(e => e.Kind == SecurityEventKind.Logout && e.Workstation == Device);
        (await context.Users.AsNoTracking().SingleAsync(u => u.Id == lab.Biologist.Id))
            .LastLoginWorkstation.Should().Be(Device);
    }

    [SqlServerFact]
    public async Task C_WithoutASession_401_AndWithoutTheClientHeader_AStateChangingRequestIsRefused()
    {
        var lab = await LabAsync();

        var anonymous = await Phone().GetAsync(ApiProtocol.WorklistRoute);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ProblemKeyAsync(anonymous)).Should().Be("Message.SessionExpired");
        anonymous.Headers.CacheControl!.NoStore.Should().BeTrue();

        // A forged form or script cannot add the header: refused before any password is checked.
        var forged = await Phone(clientHeader: false).PostAsJsonAsync(ApiProtocol.SessionRoute,
                                                                      new SignInRequest(lab.Biologist.UserName, Password));
        forged.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemKeyAsync(forged)).Should().Be("Mobile_ClientHeaderMissing");

        var phone = await SignedInAsync(lab);
        phone.DefaultRequestHeaders.Remove(ApiProtocol.RequestHeader);

        (await phone.GetAsync(ApiProtocol.WorklistRoute)).StatusCode.Should().Be(HttpStatusCode.OK, "a read needs no header");
        var unsigned = await ValidateAsync(phone, Guid.NewGuid(), new ValidationDecisionRequest(Guid.NewGuid(), false));
        unsigned.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemKeyAsync(unsigned)).Should().Be("Mobile_ClientHeaderMissing");
        (await phone.DeleteAsync(ApiProtocol.SessionRoute)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- 2. How a session ends ---------------------------------------------------------------------

    [SqlServerFact]
    public async Task D_TheSessionEndsAtTheNextRequest_WhenTheAccountIsDeactivated_ItsPasswordChanged_OrTheRightWithdrawn()
    {
        async Task ExpectRevokedAsync(HttpClient phone)
        {
            var revoked = await phone.GetAsync(ApiProtocol.WorklistRoute);
            revoked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await ProblemKeyAsync(revoked)).Should().Be("Auth_SessionRevoked");

            // Gone for good: the same cookie, presented again, opens nothing.
            (await phone.GetAsync(ApiProtocol.SessionRoute)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var deactivated = await LabAsync();
        var phone = await SignedInAsync(deactivated);
        (await phone.GetAsync(ApiProtocol.WorklistRoute)).StatusCode.Should().Be(HttpStatusCode.OK);
        await using (var context = database.Context())
        {
            (await context.Users.SingleAsync(u => u.Id == deactivated.Biologist.Id)).Deactivate();
            await context.SaveChangesAsync();
        }
        await ExpectRevokedAsync(phone);

        var passwordChanged = await LabAsync();
        phone = await SignedInAsync(passwordChanged);
        await using (var context = database.Context())
        {
            var user = await context.Users.SingleAsync(u => u.Id == passwordChanged.Biologist.Id);
            user.SetPassword(database.Hasher.Hash("Nouveau-Secret-2026!"), DateTimeOffset.Now);
            await context.SaveChangesAsync();
        }
        await ExpectRevokedAsync(phone);

        var withdrawn = await LabAsync();
        phone = await SignedInAsync(withdrawn);
        await using (var context = database.Context())
        {
            var role = await context.Roles.Include(r => r.Permissions).SingleAsync(r => r.Id == withdrawn.Role.Id);
            role.Revoke(Permissions.Results.ValidateRemote);
            await context.SaveChangesAsync();
        }
        await ExpectRevokedAsync(phone);

        await using (var context = database.Context())
            (await context.SecurityEvents.AsNoTracking()
                .CountAsync(e => e.AttemptedUserName == withdrawn.Biologist.UserName && e.Kind == SecurityEventKind.Logout
                                 && e.Details!.Contains("account changed")))
                .Should().Be(1);
    }

    [SqlServerFact]
    public async Task E_TheSessionBelongsToTheDeviceThatOpenedIt()
    {
        var lab = await LabAsync();
        var phone = await SignedInAsync(lab);

        // The same cookie, presented by another tailnet account.
        phone.DefaultRequestHeaders.Remove("Tailscale-User-Login");
        phone.DefaultRequestHeaders.Add("Tailscale-User-Login", "autre@example.ma");

        var stolen = await phone.GetAsync(ApiProtocol.WorklistRoute);
        stolen.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ProblemKeyAsync(stolen)).Should().Be("Mobile_DeviceMismatch");

        // And the session is ended for its owner too.
        phone.DefaultRequestHeaders.Remove("Tailscale-User-Login");
        phone.DefaultRequestHeaders.Add("Tailscale-User-Login", Login);
        (await phone.GetAsync(ApiProtocol.WorklistRoute)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- 3. Worklist and file -----------------------------------------------------------------------

    [SqlServerFact]
    public async Task F_TheWorklistChips_HoldWhatTheWorkstationsChipsHold_WithTheirCounts()
    {
        var lab = await LabAsync();
        var routine = await FileAsync(lab, OrderPriority.Routine, (lab.Creatinine, "50", true));
        var urgent = await FileAsync(lab, OrderPriority.Urgent, (lab.Sodium, "150", true));

        var phone = await SignedInAsync(lab);

        var all = await ReadAsync<WorklistResponse>(await phone.GetAsync(ApiProtocol.WorklistRoute));
        all.Filter.Should().Be(ApiProtocol.Filters.All);
        all.Counts.Should().Be(new FilterCounts(2, 1, 1, 0));
        all.Entries.Select(e => e.OrderId).Should().BeEquivalentTo([routine.OrderId, urgent.OrderId]);

        var urgents = await ReadAsync<WorklistResponse>(await phone.GetAsync($"{ApiProtocol.WorklistRoute}?filtre=urgents"));
        urgents.Entries.Should().ContainSingle().Which.OrderId.Should().Be(urgent.OrderId);
        urgents.Entries[0].Priority.Should().Be(nameof(OrderPriority.Urgent));

        var abnormal = await ReadAsync<WorklistResponse>(await phone.GetAsync($"{ApiProtocol.WorklistRoute}?filtre=anomalies"));
        abnormal.Entries.Should().ContainSingle().Which.OrderId.Should().Be(urgent.OrderId);

        (await ReadAsync<WorklistResponse>(await phone.GetAsync($"{ApiProtocol.WorklistRoute}?filtre=cq"))).Entries.Should().BeEmpty();

        var unknown = await phone.GetAsync($"{ApiProtocol.WorklistRoute}?filtre=tout");
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemKeyAsync(unknown)).Should().Be("Mobile_UnknownFilter");
    }

    [SqlServerFact]
    public async Task G_AnOpenFile_OffersToTickOnlyTheCheckedValues_WithTheirEntryTime_AndTheConsultationIsLogged()
    {
        var lab = await LabAsync();
        var file = await FileAsync(lab, OrderPriority.Routine, (lab.Creatinine, "150", true), (lab.Sodium, "40", false));

        var phone = await SignedInAsync(lab);
        var response = await phone.GetAsync($"/api/dossiers/{file.OrderId}");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var opened = await ReadAsync<OrderFile>(response);

        opened.Patient.Name.Should().Contain("TAZI");
        opened.ClinicalInformation.Should().Be("Bilan de contrôle");
        opened.CanIssueReport.Should().BeFalse();
        // A prescriber typed on the order: the name, and no number to offer.
        opened.Prescriber.Should().Be(new PrescriberSummary("Dr Prescripteur"));
        opened.Context.Should().Contain(l => l.Text.Length > 0 && l.Tone == ApiProtocol.Tones.Warning);

        var checkedRow = opened.Rows.Single(r => r.ResultId == file.ByAnalysis[lab.Creatinine.Id].Id);
        checkedRow.CanTick.Should().BeTrue();
        checkedRow.PreTicked.Should().BeTrue();
        checkedRow.Status.Should().Be(nameof(TestResultStatus.TechnicallyValidated));
        checkedRow.Interpretation.Should().Be(nameof(ResultInterpretation.High));
        checkedRow.Indicator.Should().Be("↑");
        checkedRow.IsAbnormal.Should().BeTrue();
        checkedRow.EnteredAt.Should().Be(file.ByAnalysis[lab.Creatinine.Id].EnteredAt);

        var entered = opened.Rows.Single(r => r.ResultId == file.ByAnalysis[lab.Sodium.Id].Id);
        entered.CanTick.Should().BeFalse("only a technically validated value can be released");
        entered.PreTicked.Should().BeFalse();

        var history = await phone.GetAsync($"/api/dossiers/{file.OrderId}/parametres/{checkedRow.ParameterId}/historique");
        (await ReadAsync<List<HistoryPoint>>(history)).Should().BeEmpty();

        await using var context = database.Context();
        var logs = await context.DataAccessLogs.AsNoTracking()
            .Where(l => l.LabOrderId == file.OrderId && l.UserId == lab.Biologist.Id).ToListAsync();
        logs.Should().Contain(l => l.Context == "ResultEntry" && l.Workstation == Device);
        logs.Should().Contain(l => l.Context == "ResultHistory" && l.Workstation == Device);
    }

    // ---- 4. Validation ---------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task H_Validation_ReleasesWhatPassesTheGates_AndReportsTheUncalledCriticalAndTheUncheckedValue()
    {
        var lab = await LabAsync();
        var file = await FileAsync(lab, OrderPriority.Routine,
            (lab.Creatinine, "50", true), (lab.Glucose, "450", true), (lab.Sodium, "40", false));

        var phone = await SignedInAsync(lab);
        var opened = await ReadAsync<OrderFile>(await phone.GetAsync($"/api/dossiers/{file.OrderId}"));

        var critical = opened.Rows.Single(r => r.ResultId == file.ByAnalysis[lab.Glucose.Id].Id);
        critical.IsCritical.Should().BeTrue();
        critical.CriticalCommunicated.Should().BeFalse();

        // Every row sent for release, the unchecked one included, as a client could.
        var outcome = await ReadAsync<ValidationOutcomeResponse>(
            await ValidateAsync(phone, file.OrderId, [.. opened.Rows.Where(r => !r.IsHeading).Select(Release)]));

        outcome.Validated.Should().Be(1);
        outcome.Blocked.Should().HaveCount(2);
        outcome.ValueChanged.Should().BeFalse();
        outcome.OrderFullyValidated.Should().BeFalse();

        // The release decided needs the value it was decided on.
        var blind = await ValidateAsync(phone, file.OrderId, new ValidationDecisionRequest(critical.ResultId, true));
        blind.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemKeyAsync(blind)).Should().Be("Mobile_DisplayedValueRequired");

        // The critical call: opened, abandoned as unreachable, opened again and read back.
        var first = await ReadAsync<RaisedCritical>(await phone.PostAsJsonAsync($"/api/critiques/{critical.ResultId}",
            new RaiseCriticalRequest(file.OrderId, critical.EnteredAt, "0612345678")));
        first.Value.Should().Contain("450");
        (await phone.PostAsJsonAsync($"/api/critiques/notifications/{first.NotificationId}/injoignable",
            new UnreachableRequest("Pas de réponse au service"))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var second = await ReadAsync<RaisedCritical>(await phone.PostAsJsonAsync($"/api/critiques/{critical.ResultId}",
            new RaiseCriticalRequest(file.OrderId, critical.EnteredAt)));

        var noReadBack = await phone.PostAsJsonAsync($"/api/critiques/notifications/{second.NotificationId}/acquittement",
            new AcknowledgeCriticalRequest("Dr BENNANI", "Interne", " "));
        noReadBack.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemKeyAsync(noReadBack)).Should().Be("Notification_ReadBackRequired");

        (await phone.PostAsJsonAsync($"/api/critiques/notifications/{second.NotificationId}/acquittement",
            new AcknowledgeCriticalRequest("Dr BENNANI", "Interne", "450"))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var called = await phone.PostAsJsonAsync($"/api/critiques/{critical.ResultId}",
            new RaiseCriticalRequest(file.OrderId, critical.EnteredAt));
        called.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemKeyAsync(called)).Should().Be("Mobile_NoCriticalToCommunicate");

        var reopened = await ReadAsync<OrderFile>(await phone.GetAsync($"/api/dossiers/{file.OrderId}"));
        reopened.Rows.Single(r => r.ResultId == critical.ResultId).CriticalCommunicated.Should().BeTrue();
        reopened.CanIssueReport.Should().BeTrue();

        var released = await ReadAsync<ValidationOutcomeResponse>(await ValidateAsync(phone, file.OrderId, Release(critical)));
        released.Validated.Should().Be(1);
        released.Blocked.Should().BeEmpty();

        await using var context = database.Context();
        var notifications = await context.CriticalResultNotifications.AsNoTracking()
            .Where(n => n.TestResultId == critical.ResultId).OrderBy(n => n.AttemptNumber).ToListAsync();
        notifications.Should().HaveCount(2);
        notifications[0].Outcome.Should().Be(NotificationOutcome.Unreachable);
        notifications[1].ReadBackConfirmed.Should().BeTrue();
        notifications[1].ContactUsed.Should().BeNull();
        notifications[0].ContactUsed.Should().Be("0612345678");
    }

    [SqlServerFact]
    public async Task I_AValueChangedSinceItWasDisplayed_IsNotReleased_NorCalledIn_AndThePhoneIsToldToReload()
    {
        var lab = await LabAsync();
        var file = await FileAsync(lab, OrderPriority.Routine, (lab.Creatinine, "50", true), (lab.Glucose, "450", true));

        var phone = await SignedInAsync(lab);
        var opened = await ReadAsync<OrderFile>(await phone.GetAsync($"/api/dossiers/{file.OrderId}"));
        var creatinine = opened.Rows.Single(r => r.ResultId == file.ByAnalysis[lab.Creatinine.Id].Id);
        var glucose = opened.Rows.Single(r => r.ResultId == file.ByAnalysis[lab.Glucose.Id].Id);

        // The analyser re-runs both while the file stands open on the phone.
        await using (var context = database.Context())
            await context.TestResults.Where(r => r.LabOrderId == file.OrderId)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.EnteredAt, r => r.EnteredAt!.Value.AddMinutes(1)));

        var outcome = await ReadAsync<ValidationOutcomeResponse>(await ValidateAsync(phone, file.OrderId, Release(creatinine)));
        outcome.Validated.Should().Be(0);
        outcome.ValueChanged.Should().BeTrue();
        outcome.Blocked.Should().ContainSingle().Which.Should().Contain("Rechargez le dossier");

        var call = await phone.PostAsJsonAsync($"/api/critiques/{glucose.ResultId}",
                                               new RaiseCriticalRequest(file.OrderId, glucose.EnteredAt));
        call.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemKeyAsync(call)).Should().Be("Notification_ValueChangedSinceDisplayed");

        await using (var context = database.Context())
            (await context.TestResults.AsNoTracking().SingleAsync(r => r.Id == creatinine.ResultId))
                .Status.Should().Be(TestResultStatus.TechnicallyValidated);

        // Reloaded, the current value is released.
        var reloaded = await ReadAsync<OrderFile>(await phone.GetAsync($"/api/dossiers/{file.OrderId}"));
        (await ReadAsync<ValidationOutcomeResponse>(await ValidateAsync(phone, file.OrderId,
            Release(reloaded.Rows.Single(r => r.ResultId == creatinine.ResultId))))).Validated.Should().Be(1);
    }

    // ---- 5. The report -------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task J_TheReportIsIssued_AndItsPagesAreShownAsImagesTheBrowserMayNotKeep()
    {
        var lab = await LabAsync();
        var file = await FileAsync(lab, OrderPriority.Routine, (lab.Creatinine, "50", true), (lab.Sodium, "40", false));

        var phone = await SignedInAsync(lab);
        var opened = await ReadAsync<OrderFile>(await phone.GetAsync($"/api/dossiers/{file.OrderId}"));
        (await ReadAsync<ValidationOutcomeResponse>(await ValidateAsync(phone, file.OrderId,
            Release(opened.Rows.Single(r => r.CanTick))))).Validated.Should().Be(1);

        var release = await phone.PostAsync($"/api/dossiers/{file.OrderId}/compte-rendu", null);
        var report = await ReadAsync<ReleasedReportResponse>(release);
        report.Version.Should().Be(1);
        report.Partial.Should().BeTrue("the sodium is still awaited");
        report.PageCount.Should().BeGreaterThan(0);

        var page = await phone.GetAsync($"/api/comptes-rendus/{report.ReportId}/apercu?page=1");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        page.Headers.CacheControl!.NoStore.Should().BeTrue();
        page.Content.Headers.ContentDisposition!.DispositionType.Should().Be("inline");
        page.Headers.GetValues(ApiProtocol.PageCountHeader).Single().Should().Be(report.PageCount.ToString());
        (await page.Content.ReadAsByteArrayAsync()).Take(8).Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);

        var beyond = await phone.GetAsync($"/api/comptes-rendus/{report.ReportId}/apercu?page={report.PageCount + 1}");
        beyond.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemKeyAsync(beyond)).Should().Be("Mobile_PreviewPageNotFound");

        // A report this phone did not issue has no preview here.
        (await phone.GetAsync($"/api/comptes-rendus/{Guid.NewGuid()}/apercu")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var context = database.Context();
        (await context.LabReports.AsNoTracking().SingleAsync(r => r.Id == report.ReportId)).SignatoryOrderNumber
            .Should().Be("CNOM-MOB-1");
        (await context.DataAccessLogs.AsNoTracking()
            .CountAsync(l => l.LabOrderId == file.OrderId && l.Context == "ReportPreviewMobile" && l.Workstation == Device))
            .Should().Be(1);
    }

    // ---- 5b. What the phone's screens show ------------------------------------------------------------

    [SqlServerFact]
    public async Task L_ThePhonesScreens_GetTheSiteTheLanguageTheChannelAndTheReportsSignatory()
    {
        var lab = await LabAsync();
        var file = await FileAsync(lab, OrderPriority.Urgent, (lab.Glucose, "450", true), (lab.Sodium, "40", true));

        // « Mon compte »: the site by its name, and since when.
        var phone = Phone();
        var signIn = await phone.PostAsJsonAsync(ApiProtocol.SessionRoute, new SignInRequest(lab.Biologist.UserName, Password));
        var session = await ReadAsync<SessionInfo>(signIn);
        session.SiteName.Should().Be("Laboratoire mobile");
        session.SignedInAt.Should().BeCloseTo(DateTimeOffset.Now, TimeSpan.FromMinutes(1));
        session.Language.Should().Be(nameof(AppLanguage.French));
        (await ReadAsync<SessionInfo>(await phone.GetAsync(ApiProtocol.SessionRoute))).SignedInAt.Should().Be(session.SignedInAt);

        // The language: the session's, from the next request on — what the server writes follows.
        var unknown = await phone.PutAsJsonAsync(ApiProtocol.LanguageRoute, new LanguageRequest("Klingon"));
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemKeyAsync(unknown)).Should().Be("Mobile_UnknownLanguage");

        var arabic = await ReadAsync<SessionInfo>(await phone.PutAsJsonAsync(ApiProtocol.LanguageRoute, new LanguageRequest("Arabic")));
        arabic.Language.Should().Be(nameof(AppLanguage.Arabic));
        arabic.SignedInAt.Should().Be(session.SignedInAt);
        var list = await ReadAsync<WorklistResponse>(await phone.GetAsync(ApiProtocol.WorklistRoute));
        list.Entries.Single(e => e.OrderId == file.OrderId).Alerts.Should()
            .Contain(a => a.Text.Any(c => c >= '\u0600' && c <= '\u06FF'), "the alerts are written in Arabic now");

        await using (var context = database.Context())
            (await context.Users.AsNoTracking().SingleAsync(u => u.Id == lab.Biologist.Id)).PreferredLanguage
                .Should().Be(AppLanguage.French, "the account's own language is the workstation's to change");

        // The critical call « sur place », and a channel the phone does not offer.
        var opened = await ReadAsync<OrderFile>(await phone.GetAsync($"/api/dossiers/{file.OrderId}"));
        var critical = opened.Rows.Single(r => r.ResultId == file.ByAnalysis[lab.Glucose.Id].Id);

        var fax = await phone.PostAsJsonAsync($"/api/critiques/{critical.ResultId}",
            new RaiseCriticalRequest(file.OrderId, critical.EnteredAt, null, "Fax"));
        fax.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemKeyAsync(fax)).Should().Be("Mobile_UnknownChannel");

        var call = await ReadAsync<RaisedCritical>(await phone.PostAsJsonAsync($"/api/critiques/{critical.ResultId}",
            new RaiseCriticalRequest(file.OrderId, critical.EnteredAt, null, ApiProtocol.Channels.InPerson)));
        (await phone.PostAsJsonAsync($"/api/critiques/notifications/{call.NotificationId}/acquittement",
            new AcknowledgeCriticalRequest("Dr BENNANI", null, "450"))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using (var context = database.Context())
            (await context.CriticalResultNotifications.AsNoTracking().SingleAsync(n => n.Id == call.NotificationId))
                .Channel.Should().Be(NotificationChannel.InPerson);

        // « Validée »: what was released, and the last report — none yet, then the one just issued.
        opened = await ReadAsync<OrderFile>(await phone.GetAsync($"/api/dossiers/{file.OrderId}"));
        opened.ValidatedCount.Should().Be(0);
        opened.LatestReportVersion.Should().BeNull();
        (await ReadAsync<ValidationOutcomeResponse>(await ValidateAsync(phone, file.OrderId,
            [.. opened.Rows.Where(r => r.CanTick).Select(Release)]))).Validated.Should().Be(2);

        var report = await ReadAsync<ReleasedReportResponse>(await phone.PostAsync($"/api/dossiers/{file.OrderId}/compte-rendu", null));
        report.SignedBy.Should().Be("Dr Karim ALAMI");
        report.ReleasedAt.Should().BeCloseTo(DateTimeOffset.Now, TimeSpan.FromMinutes(1));

        var after = await ReadAsync<OrderFile>(await phone.GetAsync($"/api/dossiers/{file.OrderId}"));
        after.ValidatedCount.Should().Be(2);
        after.LatestReportVersion.Should().Be(1);
    }

    // ---- 6. The device on every trace ---------------------------------------------------------------------

    [SqlServerFact]
    public async Task K_TheAuditTrailNamesThePhone_FromTheTailscaleHeaders_OnlyWhenTheyComeThroughTheLoopback()
    {
        var lab = await LabAsync();
        var file = await FileAsync(lab, OrderPriority.Routine, (lab.Creatinine, "50", true));

        var phone = await SignedInAsync(lab);
        var row = (await ReadAsync<OrderFile>(await phone.GetAsync($"/api/dossiers/{file.OrderId}"))).Rows.Single(r => r.CanTick);
        (await ReadAsync<ValidationOutcomeResponse>(await ValidateAsync(phone, file.OrderId, Release(row)))).Validated.Should().Be(1);

        await using (var context = database.Context())
        {
            var entries = await context.AuditEntries.AsNoTracking()
                .Where(e => e.EntityId == row.ResultId && e.UserId == lab.Biologist.Id).ToListAsync();
            entries.Should().NotBeEmpty();
            entries.Should().OnlyContain(e => e.Workstation == Device);
        }

        // From the LAN, the same headers are anybody's to type: the phone is named by its address.
        var other = await LabAsync();
        var otherFile = await FileAsync(other, OrderPriority.Routine, (other.Creatinine, "50", true));
        var fromLan = await SignedInAsync(other, login: "directeur@example.ma", peer: "192.168.1.50");
        var lanRow = (await ReadAsync<OrderFile>(await fromLan.GetAsync($"/api/dossiers/{otherFile.OrderId}"))).Rows.Single(r => r.CanTick);
        (await ReadAsync<ValidationOutcomeResponse>(await ValidateAsync(fromLan, otherFile.OrderId, Release(lanRow))))
            .Validated.Should().Be(1);

        await using (var context = database.Context())
        {
            var entries = await context.AuditEntries.AsNoTracking()
                .Where(e => e.EntityId == lanRow.ResultId && e.UserId == other.Biologist.Id).ToListAsync();
            entries.Should().NotBeEmpty();
            entries.Should().OnlyContain(e => e.Workstation == "Mobile · 192.168.1.50");

            (await context.SecurityEvents.AsNoTracking()
                .SingleAsync(e => e.AttemptedUserName == other.Biologist.UserName && e.Kind == SecurityEventKind.LoginSucceeded))
                .Workstation.Should().Be("Mobile · 192.168.1.50");
        }
    }
}
