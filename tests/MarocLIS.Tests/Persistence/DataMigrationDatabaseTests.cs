using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Migration;
using MarocLIS.Application.Results;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Migration;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Sequencing;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the « Reprise de données » tests, created from the model and dropped at the end.</summary>
public sealed class RepriseScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_Reprise_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public RepriseScratchDatabase()
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
    }

    public LisDbContext Context() =>
        new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(ConnectionString).Options);

    public void Dispose()
    {
        if (!_created) return;
        SqlConnection.ClearAllPools();
        using var context = Context();
        context.Database.EnsureDeleted();
    }
}

/// <summary>
/// « Reprise de données » against SQL Server and the services as the desktop composes them: an
/// import run twice that updates rather than duplicates, the antériorités shown in the result
/// history, the old numbers that find the patient, the opening balances kept out of the invoice
/// series, the undo of the last run, the permission, and an old application's SQL Server database
/// read (and never written) through the connection the screen offers.
/// </summary>
public sealed class DataMigrationDatabaseTests(RepriseScratchDatabase database) : IClassFixture<RepriseScratchDatabase>
{
    private sealed class FixedClock : IClock
    {
        public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.Today);
        // The real time: « Annuler la dernière reprise » takes the run that started last.
        public DateTimeOffset Now => DateTimeOffset.Now;
    }

    private sealed class Operator(Guid userId, Guid siteId, IReadOnlySet<string> permissions) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId => userId;
        public string UserName => "reprise";
        public string FullName => "Admin Reprise";
        public Guid SiteId => siteId;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.MedicalBiologist;
        public IReadOnlyList<Guid> AccessibleSiteIds => [siteId];
        public IReadOnlySet<string> Permissions => permissions;
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => Permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "REPRISE-1";
    }

    private sealed record Lab(LabSite Site, AppUser Admin, AnalysisTest Analysis, TestParameter Parameter, Payer Payer);

    private static readonly IReadOnlySet<string> Everything = new HashSet<string>(Permissions.All);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static string Errors(Result result) => string.Join(", ", result.Errors.Select(e => e.ResourceKey));

    private async Task<Lab> LabAsync()
    {
        var admin = new AppUser(Unique("rep").ToLowerInvariant(), "Admin Reprise", "x")
        {
            Qualification = ProfessionalQualification.MedicalBiologist,
            ProfessionalOrderNumber = "CNOM-REP-1"
        };
        var site = new LabSite(Unique("L"), new MultilingualText("Laboratoire Reprise"), LabSiteKind.MainLaboratory)
        {
            LegalName = "LABORATOIRE REPRISE SARL",
            Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(),
            FiscalId = "45128796",
            AddressLine = new MultilingualText("1, rue du Test"),
            City = "Rabat",
            HealthMinistryAuthorizationNumber = "AUT-REP-1",
            ResponsibleBiologistUserId = admin.Id
        };
        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sérum"));
        var analysis = new AnalysisTest(Unique("G"), new MultilingualText("Glycémie à jeun"), department.Id, specimen.Id);
        var parameter = new TestParameter(analysis.Code, new MultilingualText("Glycémie"), ResultValueType.Numeric) { Decimals = 2 };
        analysis.AddParameter(parameter);
        var payer = new Payer(Unique("P"), new MultilingualText("Mutuelle de test"), PayerKind.AmoCnss);

        await using var context = database.Context();
        context.Users.Add(admin);
        context.Sites.Add(site);
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.AnalysisTests.Add(analysis);
        context.Payers.Add(payer);
        await context.SaveChangesAsync();
        return new Lab(site, admin, analysis, parameter, payer);
    }

    private ServiceProvider Services(Lab lab, IReadOnlySet<string>? permissions = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(lab.Admin.Id, lab.Site.Id, permissions ?? Everything));
        services.AddSingleton<IClock>(new FixedClock());
        services.AddIsolatedQueries<IPatientQueries, PatientQueries>();
        services.AddIsolatedQueries<IResultQueries, ResultQueries>();
        return services.BuildServiceProvider();
    }

    private async Task<T> On<TService, T>(Lab lab, Func<TService, Task<T>> action, IReadOnlySet<string>? permissions = null)
        where TService : notnull
    {
        await using var provider = Services(lab, permissions);
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static MigrationRequest Request(DataMigrationKind kind, string system, string csv, CorrespondenceTables? tables = null)
    {
        var table = CsvSource.Parse(csv);
        return new MigrationRequest
        {
            Kind = kind,
            SourceSystem = system,
            SourceLabel = "test.csv",
            Table = table,
            Columns = ColumnMatcher.AutoMatch(kind, table.Columns),
            Correspondences = tables ?? new CorrespondenceTables(),
            BatchSize = 2
        };
    }

    private Task<Result<MigrationSummary>> ImportAsync(Lab lab, MigrationRequest request) =>
        On<DataMigrationService, Result<MigrationSummary>>(lab, s => s.ImportAsync(request));

    private string Patients(Lab lab, string cin) =>
        "Ancien identifiant;Ancien n° de dossier;Nom;Prénom;Date de naissance;Sexe;CIN;Téléphone mobile;Organisme (code);N° d'immatriculation\n" +
        $"P1;OLD-{cin}-1;ALAOUI{cin};Salma;05/03/1971;F;{cin};0661234567;{lab.Payer.Code};123456789\n" +
        $"P2;OLD-{cin}-2;BENNANI{cin};Karim;12/11/1985;M;;;;\n" +
        $"P3;OLD-{cin}-3;IDRISSI{cin};Omar;;M;;;;\n" +
        $"P4;;TAZI{cin};Ali;01/06/1950;Masculin;;;;\n";

    // ---- Idempotent import -------------------------------------------------------------------------

    [SqlServerFact]
    public async Task ImportingTwice_UpdatesWhatItCreated_AndNeverDuplicates()
    {
        var lab = await LabAsync();
        var system = Unique("SYS");
        var cin = "RP" + Random.Shared.Next(100000, 999999);
        var csv = Patients(lab, cin);

        var simulation = await On<DataMigrationService, Result<MigrationAnalysis>>(lab, s => s.AnalyseAsync(Request(DataMigrationKind.Patients, system, csv)));
        simulation.IsSuccess.Should().BeTrue(Errors(simulation));
        simulation.Value.ToCreate.Should().Be(3);
        simulation.Value.InError.Should().Be(1, "P3 has neither date of birth nor age");
        simulation.Value.Anomalies.Should().Contain(a => a.Row == 4 && a.ProblemKey == "Migration_BirthDateOrAgeRequired");

        var first = await ImportAsync(lab, Request(DataMigrationKind.Patients, system, csv));
        first.IsSuccess.Should().BeTrue(Errors(first));
        first.Value.Should().Match<MigrationSummary>(s => s.Created == 3 && s.Updated == 0 && s.Failed == 1 && !s.Interrupted);

        await using (var context = database.Context())
        {
            var created = await context.Patients.Include(p => p.Coverages).Where(p => p.LastName.EndsWith(cin)).ToListAsync();
            created.Should().HaveCount(3);
            created.Single(p => p.Cin == cin).Coverages.Should().ContainSingle(c => c.PayerId == lab.Payer.Id && c.MembershipNumber == "123456789");
            created.Single(p => p.LastName.StartsWith("TAZI")).Gender.Should().Be(Gender.Male, "« Masculin » was matched to M");
            (await context.MigrationRecords.CountAsync(r => r.SourceKey.StartsWith(system + "|"))).Should().Be(3);
            var batch = await context.MigrationBatches.SingleAsync(b => b.SourceSystem == system);
            batch.Created.Should().Be(3);
            (await context.AuditEntries.AnyAsync(a => a.Reason != null && a.Reason.Contains(batch.Id.ToString("N")))).Should().BeTrue(
                "each run leaves its trace in the audit trail");
        }

        // The old application exported P2 again with a phone number now.
        var second = await ImportAsync(lab, Request(DataMigrationKind.Patients, system,
            csv.Replace($"BENNANI{cin};Karim;12/11/1985;M;;;;", $"BENNANI{cin};Karim;12/11/1985;M;;0677000000;;")));
        second.IsSuccess.Should().BeTrue(Errors(second));
        second.Value.Created.Should().Be(0);
        second.Value.Updated.Should().Be(1);
        second.Value.Skipped.Should().Be(2, "unchanged rows are left alone");

        await using (var context = database.Context())
        {
            (await context.Patients.CountAsync(p => p.LastName.EndsWith(cin))).Should().Be(3);
            (await context.Patients.SingleAsync(p => p.LastName == "BENNANI" + cin)).MobilePhone.Should().Be("+212677000000");
        }

        // Another source with the same patient: found by its CIN, not created again.
        var other = await ImportAsync(lab, Request(DataMigrationKind.Patients, Unique("AUTRE"),
            "Ancien identifiant;Nom;Prénom;Date de naissance;CIN\n" + $"X9;Alaoui{cin};SALMA;05/03/1971;{cin}\n"));
        other.IsSuccess.Should().BeTrue(Errors(other));
        other.Value.Created.Should().Be(0);
        other.Value.Updated.Should().Be(1);
        await using (var context = database.Context())
            (await context.Patients.CountAsync(p => p.Cin == cin)).Should().Be(1);
    }

    // ---- Antériorités and old numbers (point 4) ----------------------------------------------------

    [SqlServerFact]
    public async Task History_IsShownReadOnlyAsReprise_AndTheOldNumbersFindThePatient()
    {
        var lab = await LabAsync();
        var system = Unique("SYS");
        var cin = "RH" + Random.Shared.Next(100000, 999999);
        (await ImportAsync(lab, Request(DataMigrationKind.Patients, system, Patients(lab, cin)))).IsSuccess.Should().BeTrue();

        var accession = Unique("24/");
        var history =
            "Ancien identifiant;Ancien identifiant patient;Date du résultat;Code analyse;Résultat;Unité;Valeurs de référence;N° dossier ancien\n" +
            $"R1;P1;14/02/2024;{lab.Analysis.Code};1,05;g/L;0,70 - 1,10;{accession}\n" +
            $"R2;P1;20/09/2025;{lab.Analysis.Code};< 0,5;g/L;0,70 - 1,10;{accession}B\n" +
            $"R3;P1;20/09/2025;{lab.Analysis.Code};Hémolysé;;;{accession}B\n" +
            $"R4;P9;20/09/2025;{lab.Analysis.Code};1,2;;;\n";

        var imported = await ImportAsync(lab, Request(DataMigrationKind.History, system, history));
        imported.IsSuccess.Should().BeTrue(Errors(imported));
        imported.Value.Created.Should().Be(3);
        imported.Value.Failed.Should().Be(1);
        imported.Value.Anomalies.Should().Contain(a => a.Row == 5 && a.ProblemKey == "Migration_PatientNotFound");

        // Re-importing the same results changes nothing.
        var again = await ImportAsync(lab, Request(DataMigrationKind.History, system, history));
        again.Value.Created.Should().Be(0);

        Guid patientId;
        await using (var context = database.Context())
        {
            patientId = (await context.Patients.SingleAsync(p => p.Cin == cin)).Id;
            var stored = await context.HistoricalResults.Where(h => h.PatientId == patientId).OrderBy(h => h.ResultDate).ToListAsync();
            stored.Should().HaveCount(3);
            stored[0].Should().Match<HistoricalResult>(h => h.NumericValue == 1.05m && h.Unit == "g/L" && h.OldAccessionNumber == accession
                                                            && h.TestParameterId == lab.Parameter.Id);
            stored.Should().Contain(h => h.ValueOperator == "<" && h.NumericValue == 0.5m);
            stored.Should().Contain(h => h.ValueText == "Hémolysé" && h.NumericValue == null);
            (await context.TestResults.CountAsync(r => r.TestParameterId == lab.Parameter.Id)).Should().Be(0,
                "an antériorité is not a MarocLIS result: it is never validated or released again");
        }

        // Shown in the history of result entry and validation, flagged « reprise ».
        var points = await On<IResultQueries, IReadOnlyList<ResultHistoryPoint>>(lab,
            q => q.GetValidatedHistoryAsync(patientId, lab.Parameter.Id, 20));
        points.Should().HaveCount(2, "the numeric antériorités are drawn");
        points.Should().OnlyContain(p => p.Imported);
        points.Select(p => p.AccessionNumber).Should().Equal(accession, accession + "B");

        var previous = await On<IResultQueries, IReadOnlyDictionary<Guid, PreviousValue>>(lab,
            q => q.GetPreviousValuesAsync(patientId, [lab.Parameter.Id], DateTimeOffset.Now));
        previous[lab.Parameter.Id].Should().Match<PreviousValue>(p => p.Imported && p.Value == 0.5m);

        // The old file number and the old accession number both find the patient…
        foreach (var term in new[] { $"OLD-{cin}-1", accession })
        {
            var found = await On<IPatientQueries, PagedResult<PatientSearchRow>>(lab, q => q.SearchAsync(term, lab.Site.Id));
            found.Items.Should().ContainSingle(p => p.Id == patientId, term);
        }

        // … and « Ancien n° → patient » lists the antériorités carrying it.
        var match = await On<DataMigrationService, Result<IReadOnlyList<OldNumberMatch>>>(lab, s => s.FindByOldNumberAsync(accession + "B"));
        match.IsSuccess.Should().BeTrue(Errors(match));
        match.Value.Should().ContainSingle(m => m.PatientId == patientId && m.OldNumber == $"OLD-{cin}-1");
        match.Value[0].History.Should().HaveCount(2);

        var byFile = await On<DataMigrationService, Result<IReadOnlyList<OldNumberMatch>>>(lab, s => s.FindByOldNumberAsync($"OLD-{cin}-1"));
        byFile.Value.Should().ContainSingle(m => m.PatientId == patientId && m.History.Count == 3);
    }

    [SqlServerFact]
    public async Task AnUnmappedAnalysisCode_BlocksTheImport_UntilItsCorrespondenceIsSet()
    {
        var lab = await LabAsync();
        var system = Unique("SYS");
        var cin = "RC" + Random.Shared.Next(100000, 999999);
        (await ImportAsync(lab, Request(DataMigrationKind.Patients, system, Patients(lab, cin)))).IsSuccess.Should().BeTrue();

        var history = "Ancien identifiant;Ancien identifiant patient;Date du résultat;Code analyse;Résultat\n" +
                      "R1;P1;14/02/2024;GLYC-OLD;1,05\nR2;P2;14/02/2024;GLYC-OLD;0,95\nR3;P2;14/02/2024;TEST-INTERNE;x\n";

        var blocked = await ImportAsync(lab, Request(DataMigrationKind.History, system, history));
        blocked.IsFailure.Should().BeTrue();
        blocked.FirstError.ResourceKey.Should().Be("Migration_ImportBlocked");
        await using (var context = database.Context())
            (await context.MigrationBatches.AnyAsync(b => b.SourceSystem == system && b.Kind == DataMigrationKind.History)).Should().BeFalse(
                "a blocked import writes nothing");

        var tables = new CorrespondenceTables();
        tables.Set(MigrationCodeList.Analysis, "glyc-old", $"{lab.Analysis.Code}/{lab.Parameter.Code}");
        tables.Set(MigrationCodeList.Analysis, "TEST-INTERNE", CorrespondenceTables.Ignore);
        var imported = await ImportAsync(lab, Request(DataMigrationKind.History, system, history, tables));
        imported.IsSuccess.Should().BeTrue(Errors(imported));
        imported.Value.Created.Should().Be(2);
        imported.Value.Skipped.Should().Be(1);
    }

    // ---- Opening balances --------------------------------------------------------------------------

    [SqlServerFact]
    public async Task OpeningBalances_AreTheirOwnRecords_OutsideTheInvoiceSeries()
    {
        var lab = await LabAsync();
        var system = Unique("SYS");
        var cin = "RB" + Random.Shared.Next(100000, 999999);
        (await ImportAsync(lab, Request(DataMigrationKind.Patients, system, Patients(lab, cin)))).IsSuccess.Should().BeTrue();

        int invoices;
        long allocated;
        await using (var context = database.Context())
        {
            invoices = await context.Invoices.IgnoreQueryFilters().CountAsync();
            allocated = await context.NumberSequences.Where(s => s.Purpose == SequencePurpose.Invoice).SumAsync(s => (long?)s.LastAllocatedValue) ?? 0;
        }

        var csv = "Ancien identifiant;Ancien identifiant patient;Organisme (code);Montant dû (MAD);Date du solde;N° facture ancienne;Date facture;Libellé\n" +
                  $"F1;P1;;150,00;31/12/2025;FA-2024-0815;15/11/2024;Reste à payer\n" +
                  $"F2;;{lab.Payer.Code};1 250,40;31/12/2025;BORD-77;;Bordereau en attente\n" +
                  "F3;P2;;0;31/12/2025;FA-1;;\n";
        var imported = await ImportAsync(lab, Request(DataMigrationKind.OpeningBalances, system, csv));
        imported.IsSuccess.Should().BeTrue(Errors(imported));
        imported.Value.Created.Should().Be(2);
        imported.Value.Skipped.Should().Be(1, "a zero balance is no receivable");

        await using (var context = database.Context())
        {
            (await context.Invoices.IgnoreQueryFilters().CountAsync()).Should().Be(invoices, "no invoice is issued for an old balance");
            ((await context.NumberSequences.Where(s => s.Purpose == SequencePurpose.Invoice).SumAsync(s => (long?)s.LastAllocatedValue)) ?? 0)
                .Should().Be(allocated, "the invoice series is not touched");
        }

        var listed = await On<DataMigrationService, Result<IReadOnlyList<OpeningBalanceRow>>>(lab, s => s.GetOpeningBalancesAsync());
        listed.Value.Where(b => b.OldInvoiceNumber is "FA-2024-0815" or "BORD-77" && (b.PayerCode == lab.Payer.Code || b.PatientName!.Contains(cin)))
            .Select(b => b.Amount).Should().BeEquivalentTo([150.00m, 1250.40m]);
    }

    // ---- Undo --------------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task UndoLast_RemovesWhatTheRunCreated_AndNothingElse()
    {
        var lab = await LabAsync();
        var system = Unique("SYS");
        var cin = "RU" + Random.Shared.Next(100000, 999999);
        (await ImportAsync(lab, Request(DataMigrationKind.Patients, system, Patients(lab, cin)))).IsSuccess.Should().BeTrue();

        var undone = await On<DataMigrationService, Result<MigrationUndoOutcome>>(lab, s => s.UndoLastAsync());
        undone.IsSuccess.Should().BeTrue(Errors(undone));
        undone.Value.Removed.Should().Be(3);

        await using (var context = database.Context())
        {
            (await context.Patients.CountAsync(p => p.LastName.EndsWith(cin))).Should().Be(0);
            (await context.MigrationBatches.SingleAsync(b => b.SourceSystem == system)).IsUndone.Should().BeTrue();
        }

        // Imported again after the undo: created anew, not refused as known.
        var again = await ImportAsync(lab, Request(DataMigrationKind.Patients, system, Patients(lab, cin)));
        again.Value.Created.Should().Be(3);
    }

    // ---- Permission --------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task WithoutTheMigrationPermission_NothingIsReadOrWritten()
    {
        var lab = await LabAsync();
        var withoutMigration = new HashSet<string>(Permissions.All.Where(p => p != Permissions.Administration.Migration));
        var request = Request(DataMigrationKind.Patients, Unique("SYS"), Patients(lab, "RX" + Random.Shared.Next(100000, 999999)));

        var analysis = await On<DataMigrationService, Result<MigrationAnalysis>>(lab, s => s.AnalyseAsync(request), withoutMigration);
        var import = await On<DataMigrationService, Result<MigrationSummary>>(lab, s => s.ImportAsync(request), withoutMigration);
        var read = await On<DataMigrationService, Result<SourceTable>>(lab, s => s.ReadSourceAsync(
            new SourceConnectionSettings(), new SourceSelection(null, "SELECT 1")), withoutMigration);
        var undo = await On<DataMigrationService, Result<MigrationUndoOutcome>>(lab, s => s.UndoLastAsync(), withoutMigration);

        foreach (var result in new Result[] { analysis, import, read, undo })
            result.FirstError.Code.Should().Be("forbidden");

        Permissions.All.Should().Contain(Permissions.Administration.Migration, "the Administrator role holds every permission");
    }

    // ---- An old application's SQL Server database --------------------------------------------------

    [SqlServerFact]
    public async Task ASqlServerSource_IsListedAndRead_ButNeverWritten()
    {
        var lab = await LabAsync();
        var configured = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable));
        var sourceName = "MarocLIS_RepriseSource_" + Guid.NewGuid().ToString("N")[..8];

        await using (var master = new SqlConnection(new SqlConnectionStringBuilder(configured.ConnectionString) { InitialCatalog = "master" }.ConnectionString))
        {
            await master.OpenAsync();
            await using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{sourceName}]";
            await create.ExecuteNonQueryAsync();
        }

        var sourceConnection = new SqlConnectionStringBuilder(configured.ConnectionString) { InitialCatalog = sourceName }.ConnectionString;
        try
        {
            await using (var source = new SqlConnection(sourceConnection))
            {
                await source.OpenAsync();
                await using var setup = source.CreateCommand();
                setup.CommandText =
                    "CREATE TABLE dbo.T_PATIENT (ID_PATIENT int PRIMARY KEY, NOM_PAT nvarchar(60), PRENOM nvarchar(60), DATE_NAISS date, SEXE char(1), SOLDE decimal(10,2));" +
                    "INSERT INTO dbo.T_PATIENT VALUES (1, N'ALAOUI', N'Salma', '1971-03-05', 'F', 150.5), (2, N'BENNANI', N'Karim', NULL, 'M', NULL);";
                await setup.ExecuteNonQueryAsync();
                setup.CommandText = "CREATE VIEW dbo.V_FEMMES AS SELECT * FROM dbo.T_PATIENT WHERE SEXE = 'F'";
                await setup.ExecuteNonQueryAsync();
            }

            var settings = new SourceConnectionSettings
            {
                Provider = SourceProvider.SqlServer,
                Server = configured.DataSource,
                UserName = configured.UserID,
                Password = configured.Password,
                Database = sourceName
            };

            var tested = await On<DataMigrationService, Result<string>>(lab, s => s.TestConnectionAsync(settings));
            tested.IsSuccess.Should().BeTrue(Errors(tested));

            var databases = await On<DataMigrationService, Result<IReadOnlyList<string>>>(lab, s => s.ListDatabasesAsync(settings));
            databases.Value.Should().Contain(sourceName);

            var tables = await On<DataMigrationService, Result<IReadOnlyList<SourceTableInfo>>>(lab, s => s.ListTablesAsync(settings));
            tables.Value.Should().Contain(new SourceTableInfo("dbo", "T_PATIENT", false)).And.Contain(new SourceTableInfo("dbo", "V_FEMMES", true));

            var table = await On<DataMigrationService, Result<SourceTable>>(lab, s => s.ReadSourceAsync(
                settings, new SourceSelection(new SourceTableInfo("dbo", "T_PATIENT", false), null)));
            table.IsSuccess.Should().BeTrue(Errors(table));
            table.Value.Columns.Should().Equal("ID_PATIENT", "NOM_PAT", "PRENOM", "DATE_NAISS", "SEXE", "SOLDE");
            table.Value.Rows.Should().HaveCount(2);
            table.Value.Rows[0].Should().Equal("1", "ALAOUI", "Salma", "1971-03-05", "F", "150.50");
            table.Value.Rows[1][3].Should().BeNull();

            var preview = await On<DataMigrationService, Result<SourceTable>>(lab, s => s.ReadSourceAsync(
                settings, new SourceSelection(null, "SELECT ID_PATIENT, ID_PATIENT, NOM_PAT FROM dbo.V_FEMMES"), maxRows: 1));
            preview.Value.Columns.Should().Equal("ID_PATIENT", "ID_PATIENT_2", "NOM_PAT");
            preview.Value.Rows.Should().ContainSingle();

            // The columns are matched to MarocLIS's fields as the screen does.
            var matched = ColumnMatcher.AutoMatch(DataMigrationKind.Patients, table.Value.Columns);
            matched[MigrationSchema.SourceId].Should().Be("ID_PATIENT");
            matched[MigrationSchema.LastName].Should().Be("NOM_PAT");
            matched[MigrationSchema.BirthDate].Should().Be("DATE_NAISS");

            foreach (var sql in new[] { "DELETE FROM dbo.T_PATIENT", "SELECT 1; DELETE FROM dbo.T_PATIENT", "EXEC('DELETE FROM dbo.T_PATIENT')",
                                        "UPDATE dbo.T_PATIENT SET NOM_PAT = 'X'", "DROP TABLE dbo.T_PATIENT" })
            {
                var refused = await On<DataMigrationService, Result<SourceTable>>(lab, s => s.ReadSourceAsync(settings, new SourceSelection(null, sql)));
                refused.IsFailure.Should().BeTrue(sql);
            }

            var wrong = await On<DataMigrationService, Result<SourceTable>>(lab, s => s.ReadSourceAsync(
                settings, new SourceSelection(new SourceTableInfo("dbo", "ABSENTE", false), null)));
            wrong.FirstError.ResourceKey.Should().Be("Migration_SourceFailed");

            await using (var source = new SqlConnection(sourceConnection))
            {
                await source.OpenAsync();
                await using var count = source.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM dbo.T_PATIENT WHERE NOM_PAT IN (N'ALAOUI', N'BENNANI')";
                ((int)(await count.ExecuteScalarAsync())!).Should().Be(2, "the old database is never written");
            }
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var master = new SqlConnection(new SqlConnectionStringBuilder(configured.ConnectionString) { InitialCatalog = "master" }.ConnectionString);
            await master.OpenAsync();
            await using var drop = master.CreateCommand();
            drop.CommandText = $"IF DB_ID('{sourceName}') IS NOT NULL BEGIN ALTER DATABASE [{sourceName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{sourceName}]; END";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
