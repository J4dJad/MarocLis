using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Migration;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Persistence.Demo;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// tools/mobile-demo — « Validation à distance »: a database to show the phone's screens on.
//
//   dotnet run --project tools/mobile-demo -- "Server=…;Database=MarocLIS_MobileDemo;…" [--reset]
//
// Creates (or, with --reset, drops and re-creates) the database through the application's own
// initialiser, then adds one biologist allowed to validate from a phone and five fictitious files,
// each showing one thing the phone has to handle:
//
//   1. an urgent ionogram with a critical potassium nobody has been told about yet;
//   2. a creatinine far from the patient's last one (delta check), with an antériorité going back
//      to values taken over from the previous application (« reprise »);
//   3. a glycaemia covered by an out-of-control QC run;
//   4. a CRP entered but not yet technically validated, beside a glycaemia that is;
//   5. a routine file with nothing to report, to validate and issue in one go.
//
// Every value goes through the services the workstation uses — registration, collection,
// reception, entry, technical and biological validation — so the flags are the ones the rules
// compute. The only direct write is the QC flag of file 3, which in the laboratory comes from a
// rejected QC run on an analyser this database has none of.
//
// It refuses any database whose name does not contain « Demo »: --reset drops it.

var arguments = args.ToList();
var reset = arguments.Remove("--reset");
var connectionString = arguments.FirstOrDefault()
                       ?? Environment.GetEnvironmentVariable("MAROCLIS_Database__ConnectionString");

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Usage : mobile-demo <chaîne de connexion> [--reset]");
    return 2;
}

var databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
if (!databaseName.Contains("Demo", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Base « {databaseName} » refusée : le nom d'une base de démonstration contient « Demo ».");
    return 2;
}

var clock = new DemoClock();
var operatorSession = new DemoOperator();

var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = connectionString })
    .Build();

var services = new ServiceCollection();
services.AddMarocLisInfrastructure(configuration);
services.AddMarocLisApplication(configuration);
// After the infrastructure's: the clock moves back for the antériorités, and whoever acts is set
// per step — the technician enters, the biologist validates.
services.AddSingleton<IClock>(clock);
services.AddSingleton<ICurrentUser>(operatorSession);
services.AddScoped<IResultQueries, ResultQueries>();
services.AddScoped<IValidationQueries, ValidationQueries>();
services.AddScoped<IReportQueries, ReportQueries>();
services.AddScoped<IPatientQueries, PatientQueries>();
services.AddScoped<ICatalogQueries, CatalogQueries>();
services.AddScoped<IOrderQueries, OrderQueries>();
services.AddScoped<ISampleQueries, SampleQueries>();
services.AddScoped<ISubcontractQueries, SubcontractQueries>();
services.AddScoped<IRecollectionQueries, RecollectionQueries>();

await using var provider = services.BuildServiceProvider();

async Task<T> On<TService, T>(Func<TService, Task<T>> action) where TService : notnull
{
    using var scope = provider.CreateScope();
    return await action(scope.ServiceProvider.GetRequiredService<TService>());
}

async Task Write(Func<LisDbContext, Task> write)
{
    using var scope = provider.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<LisDbContext>();
    await write(db);
    await db.SaveChangesAsync();
}

static T Must<T>(Result<T> result, string step) =>
    result.IsSuccess ? result.Value : throw new InvalidOperationException($"{step} : {Describe(result.Errors)}");

static void MustSucceed(Result result, string step)
{
    if (result.IsFailure) throw new InvalidOperationException($"{step} : {Describe(result.Errors)}");
}

static string Describe(IEnumerable<Error> errors) =>
    string.Join(", ", errors.Select(e => e.Args.Length == 0 ? e.ResourceKey : $"{e.ResourceKey}({string.Join("; ", e.Args)})"));

// ---- The database ----------------------------------------------------------------------------

if (reset)
{
    await using var db = provider.CreateScope().ServiceProvider.GetRequiredService<LisDbContext>();
    await db.Database.EnsureDeletedAsync();
    SqlConnection.ClearAllPools();
}

using (var scope = provider.CreateScope())
{
    var initialised = await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
    if (initialised.IsFailure)
    {
        Console.Error.WriteLine("Initialisation impossible : " + Describe(initialised.Errors));
        return 1;
    }
}

const string BiologistLogin = "dr.berrada";
const string TechnicianLogin = "s.lahlou";
const string Password = "Mobile-Demo-2026!";

using (var scope = provider.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LisDbContext>();
    if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.UserName == BiologistLogin))
    {
        Console.Error.WriteLine($"« {databaseName} » contient déjà le jeu de démonstration : relancer avec --reset.");
        return 1;
    }
}

// ---- The laboratory, its biologist and its technician -----------------------------------------

var hasher = provider.GetRequiredService<IPasswordHasher>();
Guid siteId = default, biologistId = default, technicianId = default;
string siteCode = "";

await Write(async db =>
{
    var site = await db.Sites.IgnoreQueryFilters().FirstAsync();
    site.Name = new MultilingualText("Laboratoire Atlas — démonstration", "مختبر أطلس — عرض توضيحي", "Atlas laboratory — demonstration");
    site.LegalName = "LABORATOIRE ATLAS DÉMONSTRATION SARL";
    site.Ice = "000000000000099";
    site.FiscalId = "99999999";
    site.City = "Rabat";
    site.AddressLine = new MultilingualText("12, avenue de l'Exemple", "12، شارع المثال", "12 Example avenue");
    site.HealthMinistryAuthorizationNumber = "DEMO-AUT-1";
    site.Phone = "05 37 00 00 00";
    siteId = site.Id;
    siteCode = site.Code;

    // Remote validation is granted by a role of its own, as the manual has the administrator do:
    // no built-in role carries it.
    var remote = new Role("VALIDATION_DISTANCE",
                          new MultilingualText("Validation à distance", "المصادقة عن بعد", "Remote validation"));
    remote.Grant(Permissions.Results.ValidateRemote);
    db.Roles.Add(remote);

    var responsible = await db.Roles.FirstAsync(r => r.Code == SystemRoles.ResponsibleBiologist);
    var technicianRole = await db.Roles.FirstAsync(r => r.Code == SystemRoles.Technician);

    var biologist = new AppUser(BiologistLogin, "Dr Nadia BERRADA", hasher.Hash(Password))
    {
        Qualification = ProfessionalQualification.MedicalBiologist,
        ProfessionalOrderNumber = "CNOM-DEMO-0417",
        PreferredLanguage = AppLanguage.French
    };
    biologist.SetPassword(biologist.PasswordHash, clock.Now);
    biologist.AssignRole(responsible.Id);
    biologist.AssignRole(remote.Id);
    biologist.GrantSiteAccess(site.Id, isDefault: true);

    var technician = new AppUser(TechnicianLogin, "Samir LAHLOU", hasher.Hash(Password))
    {
        Qualification = ProfessionalQualification.LaboratoryTechnician,
        PreferredLanguage = AppLanguage.French
    };
    technician.SetPassword(technician.PasswordHash, clock.Now);
    technician.AssignRole(technicianRole.Id);
    technician.GrantSiteAccess(site.Id, isDefault: true);

    db.Users.AddRange(biologist, technician);
    site.ResponsibleBiologistUserId = biologist.Id;

    biologistId = biologist.Id;
    technicianId = technician.Id;
});

// Every permission for the preparation: the technician also registers and receives here, which
// in the laboratory the reception does. What the phone then does is the biologist's own session.
void ActAs(Guid userId, string userName, string fullName, ProfessionalQualification qualification) =>
    operatorSession.Set(userId, userName, fullName, siteId, siteCode, qualification);

void AsTechnician() => ActAs(technicianId, TechnicianLogin, "Samir LAHLOU", ProfessionalQualification.LaboratoryTechnician);
void AsBiologist() => ActAs(biologistId, BiologistLogin, "Dr Nadia BERRADA", ProfessionalQualification.MedicalBiologist);

// ---- Patients ---------------------------------------------------------------------------------

async Task<Patient> PatientAsync(string record, string last, string first, DateOnly born, Gender gender,
                                 string? lastAr = null, string? firstAr = null)
{
    var patient = new Patient(record, last, first, born, gender, siteId)
    {
        SearchKey = PatientSearchKey.Build(last, first),
        LastNameArabic = lastAr,
        FirstNameArabic = firstAr
    };
    await Write(db => { db.Patients.Add(patient); return Task.CompletedTask; });
    return patient;
}

var fatima = await PatientAsync("DEMO-P-0001", "BENNANI", "Fatima", new DateOnly(1956, 3, 14), Gender.Female, "بناني", "فاطمة");
var youssef = await PatientAsync("DEMO-P-0002", "EL IDRISSI", "Youssef", new DateOnly(1949, 11, 2), Gender.Male, "الإدريسي", "يوسف");
var salma = await PatientAsync("DEMO-P-0003", "AMRANI", "Salma", new DateOnly(1990, 7, 21), Gender.Female, "العمراني", "سلمى");
var omar = await PatientAsync("DEMO-P-0004", "TAZI", "Omar", new DateOnly(1975, 1, 30), Gender.Male, "التازي", "عمر");
var khadija = await PatientAsync("DEMO-P-0005", "OUAZZANI", "Khadija", new DateOnly(1968, 9, 5), Gender.Female, "الوزاني", "خديجة");

// ---- Files ------------------------------------------------------------------------------------

Dictionary<string, Guid> analyses;
using (var scope = provider.CreateScope())
{
    analyses = await scope.ServiceProvider.GetRequiredService<LisDbContext>().AnalysisTests.AsNoTracking()
        .ToDictionaryAsync(a => a.Code, a => a.Id);
}

/// A file: registered, collected and received, each value entered by its parameter's code, and the
/// listed ones technically validated. Returns the order and its results by parameter code.
async Task<(Guid OrderId, Dictionary<string, Guid> Results)> FileAsync(
    Patient patient, OrderPriority priority, string clinical, string[] analysisCodes,
    (string Code, string Value)[] values, string[] technicallyValidated)
{
    AsTechnician();

    var order = Must(await On<OrderRegistrationService, Result<RegisteredOrder>>(s => s.RegisterAsync(new RegisterOrderRequest
    {
        PatientId = patient.Id,
        AnalysisTestIds = [.. analysisCodes.Select(c => analyses[c])],
        PrescriberFreeText = "Dr Hicham MOUSSAOUI",
        Priority = priority,
        ClinicalInformation = clinical,
        InternalNote = DemoDataSeeder.DemoMarker
    })), $"Enregistrement ({patient.LastName})");

    foreach (var label in order.Labels)
    {
        MustSucceed(await On<SampleWorkflowService, Result>(s => s.RecordCollectionAsync(
            [new RecordCollectionRequest { SampleId = label.SampleId, VolumeMl = 5m }])), "Prélèvement");
        Must(await On<SampleWorkflowService, Result<ReceptionOutcome>>(s => s.ReceiveAsync(
            [new ReceiveSampleRequest { SampleId = label.SampleId }])), "Réception");
    }

    var results = await ResultsAsync(order.OrderId);

    Must(await On<ResultEntryService, Result<ResultEntryOutcome>>(s => s.SubmitAsync(order.OrderId,
        [.. values.Select(v => new ResultEntry(results[v.Code], v.Value))])), $"Saisie ({patient.LastName})");

    if (technicallyValidated.Length > 0)
        MustSucceed(await On<ResultEntryService, Result>(s => s.TechnicallyValidateAsync(order.OrderId,
            [.. technicallyValidated.Select(c => results[c])])), $"Validation technique ({patient.LastName})");

    Console.WriteLine($"  {order.AccessionNumber}  {patient.LastName} {patient.FirstName}");
    return (order.OrderId, results);
}

async Task<Dictionary<string, Guid>> ResultsAsync(Guid orderId)
{
    using var scope = provider.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<LisDbContext>();
    return await db.TestResults.AsNoTracking()
        .Where(r => r.LabOrderId == orderId)
        .Join(db.TestParameters.AsNoTracking(), r => r.TestParameterId, p => p.Id, (r, p) => new { p.Code, r.Id })
        .ToDictionaryAsync(x => x.Code, x => x.Id);
}

async Task BiologicallyValidateAsync(Guid orderId, IEnumerable<Guid> resultIds)
{
    AsBiologist();
    var outcome = Must(await On<ValidationService, Result<ValidationOutcome>>(s => s.ValidateAsync(orderId,
        [.. resultIds.Select(id => new ValidationDecision(id, true))])), "Validation biologique");
    if (outcome.Validated == 0) throw new InvalidOperationException("Validation biologique : rien n'a été validé.");
}

Console.WriteLine($"Base « {databaseName} » :");

// 2 (first): the antériorité of Youssef EL IDRISSI's creatinine — two values taken over from the
// previous application, then three MarocLIS files validated months apart. The clock goes back for
// each, so registration, entry and validation carry their own dates.
var creatinineParameter = await On<LisDbContext, Guid>(db => db.TestParameters.Where(p => p.Code == "CREAT").Select(p => p.Id).FirstAsync());

await Write(db =>
{
    var batch = new MigrationBatch(DataMigrationKind.History, "AncienSIL", "export-anteriorites.csv (démonstration)",
                                   clock.Now, technicianId, TechnicianLogin);
    batch.Complete(clock.Now, interrupted: false);
    db.MigrationBatches.Add(batch);

    foreach (var (monthsAgo, value, oldNumber) in new[] { (27, 9.6m, "A-2024-01873"), (21, 10.2m, "A-2024-07702") })
    {
        db.HistoricalResults.Add(new HistoricalResult(youssef.Id, creatinineParameter, clock.Now.AddMonths(-monthsAgo),
                                                      value.ToString(System.Globalization.CultureInfo.InvariantCulture), siteId, batch.Id)
        {
            NumericValue = value,
            Unit = "mg/L",
            ReferenceRange = "7,0 – 13,0",
            OldAccessionNumber = oldNumber,
            SourceCode = "CREA"
        });
    }

    return Task.CompletedTask;
});

foreach (var (daysAgo, value) in new[] { (270, "11,4"), (120, "12,8"), (12, "15,1") })
{
    clock.Offset = TimeSpan.FromDays(-daysAgo);
    var earlier = await FileAsync(youssef, OrderPriority.Routine, "Suivi néphrologique", ["CREAT"],
                                  [("CREAT", value)], ["CREAT"]);
    await BiologicallyValidateAsync(earlier.OrderId, [earlier.Results["CREAT"]]);
}
clock.Offset = TimeSpan.Zero;

// 1. Urgent ionogram, critical potassium (> 6,0 mmol/L), not yet communicated.
clock.Offset = TimeSpan.FromMinutes(-75);
await FileAsync(fatima, OrderPriority.Urgent, "Insuffisance rénale chronique, sous IEC — malaise",
                ["IONO", "GLY"], [("NA", "134"), ("K", "6,9"), ("CL", "101"), ("GLY", "1,04")], ["NA", "K", "CL", "GLY"]);

// 2. Today's creatinine: + 62 % on the value of twelve days ago.
clock.Offset = TimeSpan.FromMinutes(-60);
await FileAsync(youssef, OrderPriority.Routine, "Suivi néphrologique — contrôle", ["CREAT", "IONO"],
                [("CREAT", "24,5"), ("NA", "139"), ("K", "4,6"), ("CL", "103")], ["CREAT", "NA", "K", "CL"]);

// 3. Glycaemia under an out-of-control QC run.
clock.Offset = TimeSpan.FromMinutes(-50);
var qc = await FileAsync(salma, OrderPriority.Routine, "Bilan de grossesse — 24 SA", ["GLY", "CRP"],
                         [("GLY", "1,32"), ("CRP", "3,1")], ["GLY", "CRP"]);
await Write(async db =>
{
    var result = await db.TestResults.FirstAsync(r => r.Id == qc.Results["GLY"]);
    result.FlagQcFailure();
});

// 4. CRP entered, waiting for the technician.
clock.Offset = TimeSpan.FromMinutes(-35);
await FileAsync(omar, OrderPriority.Routine, "Fièvre depuis 3 jours", ["CRP", "GLY"],
                [("CRP", "48,2"), ("GLY", "0,92")], ["GLY"]);

// 5. Nothing to report.
clock.Offset = TimeSpan.FromMinutes(-20);
await FileAsync(khadija, OrderPriority.Routine, "Bilan annuel", ["GLY", "IONO"],
                [("GLY", "0,88"), ("NA", "140"), ("K", "4,2"), ("CL", "102")], ["GLY", "NA", "K", "CL"]);

clock.Offset = TimeSpan.Zero;

Console.WriteLine();
Console.WriteLine($"Biologiste : {BiologistLogin} / {Password}");
return 0;

/// <summary>The real time, moved back while the antériorités are written.</summary>
internal sealed class DemoClock : IClock
{
    public TimeSpan Offset { get; set; }
    public DateTimeOffset Now => DateTimeOffset.Now + Offset;
    public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
}

/// <summary>Whoever the current step is done by.</summary>
internal sealed class DemoOperator : ICurrentUser
{
    private static readonly IReadOnlySet<string> All = new HashSet<string>(MarocLIS.Domain.Identity.Permissions.All, StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> NoDepartments = new HashSet<string>(StringComparer.Ordinal);

    public void Set(Guid userId, string userName, string fullName, Guid siteId, string siteCode,
                    ProfessionalQualification qualification)
    {
        UserId = userId;
        UserName = userName;
        FullName = fullName;
        SiteId = siteId;
        SiteCode = siteCode;
        Qualification = qualification;
        AccessibleSiteIds = [siteId];
    }

    public bool IsAuthenticated => UserId != Guid.Empty;
    public Guid UserId { get; private set; }
    public string UserName { get; private set; } = "";
    public string FullName { get; private set; } = "";
    public Guid SiteId { get; private set; }
    public string SiteCode { get; private set; } = "";
    public AppLanguage Language => AppLanguage.French;
    public ProfessionalQualification Qualification { get; private set; }
    public IReadOnlyList<Guid> AccessibleSiteIds { get; private set; } = [];
    public IReadOnlySet<string> Permissions => All;
    public IReadOnlySet<string> AuthorizedDepartmentCodes => NoDepartments;
    public bool Has(string permission) => true;
    public string Workstation => "OUTIL-DEMO-MOBILE";
    public Guid? PrescriberId => null;
    public bool IsPrescriberScoped => false;
}
