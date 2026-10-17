using System.Globalization;
using System.Reflection;
using System.Text;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Calculations;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// A database of its own, uniquely named, built by the migrations like an installation's first
/// start, and dropped at the end.
/// </summary>
internal sealed class StarterDataScratchDatabase : IAsyncDisposable
{
    private StarterDataScratchDatabase(string connectionString) => ConnectionString = connectionString;

    public string ConnectionString { get; }

    public static async Task<StarterDataScratchDatabase> CreateAsync()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable)!;
        var database = new StarterDataScratchDatabase(new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "MarocLIS_T_seed_" + Guid.NewGuid().ToString("N")[..8]
        }.ConnectionString);

        await using var context = database.Context();
        await context.Database.MigrateAsync();
        return database;
    }

    public LisDbContext Context() =>
        new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(ConnectionString).Options);

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var context = Context();
        await context.Database.EnsureDeletedAsync();
    }
}

/// <summary>
/// What a new installation starts with — the starter catalogue with its routine analyses, the
/// storage places, the panels — and the rule that goes with it: a database already in use sees no
/// change at start-up, whatever it holds or lacks.
/// </summary>
public sealed class StarterDataDatabaseTests
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);
        public DateOnly Today => new(2026, 10, 17);
    }

    private const string PendingMarker = "À vérifier par le laboratoire";

    /// <summary>The analyses of the earlier starter catalogue, whose codes the upgrade steps rely on.</summary>
    private static readonly string[] CoreCodes =
    [
        "GLY", "CREAT", "UREE", "BILAN_LIP", "TRANSAM", "IONO", "HBA1C", "CRP", "NFS", "VS",
        "TP_INR", "TSH", "HBS_AG", "CHIM_URIN", "ECBU", "GROUPAGE"
    ];

    private static Task SeedAsync(LisDbContext context) =>
        ReferenceDataSeeder.SeedAsync(context, new BCryptPasswordHasher(4), new FixedClock());

    /// <summary>
    /// Every table's row count and content checksum: two equal fingerprints mean start-up wrote
    /// nothing, whichever table it might have touched.
    /// </summary>
    private static async Task<Dictionary<string, (int Rows, int? Checksum)>> FingerprintAsync(LisDbContext context)
    {
        var tables = context.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null && e.GetViewName() is null)
            .Select(e => (Schema: e.GetSchema() ?? "dbo", Table: e.GetTableName()!))
            .Distinct()
            .OrderBy(t => t.Schema).ThenBy(t => t.Table)
            .ToList();

        var result = new Dictionary<string, (int, int?)>(StringComparer.Ordinal);
        var connection = (SqlConnection)context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            foreach (var (schema, table) in tables)
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM [{schema}].[{table}]";
                await using var reader = await command.ExecuteReaderAsync();
                await reader.ReadAsync();
                result[$"{schema}.{table}"] = ((int)reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt32(1));
            }
        }
        finally
        {
            await connection.CloseAsync();
        }

        return result;
    }

    [SqlServerFact]
    public async Task A_new_installation_gets_the_starter_data_and_a_second_start_adds_nothing()
    {
        await using var database = await StarterDataScratchDatabase.CreateAsync();

        await using (var context = database.Context())
            await SeedAsync(context);

        await using (var context = database.Context())
        {
            var codes = await context.AnalysisTests.Select(a => a.Code).ToListAsync();
            codes.Should().Contain(CoreCodes, "the analyses of the earlier starter catalogue keep their codes");
            codes.Should().HaveCountGreaterThanOrEqualTo(60).And.HaveCountLessThanOrEqualTo(100);
            codes.Should().Contain(["FER", "FERRITINE", "T4L", "BILIRUBINE", "TCA", "VIH", "EPS", "PSA"]);

            var places = await context.SampleStorageLocations.IgnoreQueryFilters()
                .OrderBy(p => p.Name).Select(p => p.Name).ToListAsync();
            places.Should().BeEquivalentTo(StarterStorageLocations.Names);

            var site = await context.Sites.IgnoreQueryFilters().SingleAsync();
            (await context.SampleStorageLocations.IgnoreQueryFilters().AllAsync(p => p.SiteId == site.Id && p.IsActive))
                .Should().BeTrue();

            (await context.TestPanels.Select(p => p.Code).ToListAsync())
                .Should().Contain(["BILAN_STD", "BILAN_PRENAT", "BILAN_HEPAT", "BILAN_THYR", "BILAN_HEMOST"]);

            // Every panel line found its analysis.
            (await context.TestPanels.Include(p => p.Items).AllAsync(p => p.Items.Count >= 2)).Should().BeTrue();
        }

        Dictionary<string, (int, int?)> before;
        await using (var context = database.Context())
            before = await FingerprintAsync(context);

        await using (var context = database.Context())
            await SeedAsync(context);

        await using (var context = database.Context())
            (await FingerprintAsync(context)).Should().Equal(before, "a second start must write nothing");
    }

    [SqlServerFact]
    public async Task A_database_in_use_with_its_own_catalogue_is_left_as_it_is()
    {
        await using var database = await StarterDataScratchDatabase.CreateAsync();

        // A laboratory's own database: its own discipline, specimen and analysis — one of them under a
        // code of the routine starter list (« FER ») with its own act text — and no storage place
        // (no tube was ever stored, so the LieuxDeStockage migration found none to list).
        await using (var context = database.Context())
        {
            var department = new Department(Department.Codes.Biochemistry, new MultilingualText("Biochimie"));
            var specimen = new SpecimenType(SpecimenType.Codes.SerumDryTube, new MultilingualText("Sérum"));
            context.Departments.Add(department);
            context.SpecimenTypes.Add(specimen);

            var own = new AnalysisTest("FER", new MultilingualText("Fer — méthode maison"), department.Id, specimen.Id)
            {
                NationalActCode = "FER"
            };
            own.AddParameter(new TestParameter("FE_MAISON", new MultilingualText("Fer"), ResultValueType.Numeric));
            context.AnalysisTests.Add(own);
            await context.SaveChangesAsync();
        }

        // The start-up of this release on that database.
        await using (var context = database.Context())
            await SeedAsync(context);

        await using (var context = database.Context())
        {
            var analyses = await context.AnalysisTests.IgnoreQueryFilters().Include(a => a.Parameters).ToListAsync();
            analyses.Should().ContainSingle();
            analyses[0].Name.Fr.Should().Be("Fer — méthode maison");
            analyses[0].NationalActCode.Should().Be("FER", "a routine act is never written over a laboratory's own analysis");
            analyses[0].Parameters.Select(p => p.Code).Should().Equal("FE_MAISON");

            (await context.SampleStorageLocations.IgnoreQueryFilters().CountAsync()).Should().Be(0,
                "storage places are a new installation's starting point, not added to a database in use");
            (await context.TestPanels.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        }

        Dictionary<string, (int, int?)> before;
        await using (var context = database.Context())
            before = await FingerprintAsync(context);

        await using (var context = database.Context())
            await SeedAsync(context);

        await using (var context = database.Context())
            (await FingerprintAsync(context)).Should().Equal(before);
    }

    [SqlServerFact]
    public async Task The_starter_catalogue_is_consistent_with_the_nomenclature_and_its_own_rules()
    {
        await using var database = await StarterDataScratchDatabase.CreateAsync();

        await using (var context = database.Context())
            await SeedAsync(context);

        await using var db = database.Context();

        var acts = await db.NationalBiologyActs.ToDictionaryAsync(a => a.Code, a => a.Coefficient);

        var analyses = await db.AnalysisTests
            .Include(a => a.Parameters).ThenInclude(p => p.ReferenceRanges)
            .Include(a => a.Parameters).ThenInclude(p => p.CodedOptions)
            .Include(a => a.Tariffs)
            .AsSplitQuery()
            .ToListAsync();

        // The upgrade step that rewrites seeded act codes knows only the core analyses: a routine
        // code in its table would rewrite a laboratory's own analysis of that code.
        // (StarterCatalogue is internal to the infrastructure: read by reflection.)
        var catalogue = typeof(ReferenceDataSeeder).Assembly
            .GetType("MarocLIS.Infrastructure.Persistence.StarterCatalogue", throwOnError: true)!;
        var actCodes = (IReadOnlyDictionary<string, string>)catalogue
            .GetField("ActCodes", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var unmapped = (IReadOnlyList<string>)catalogue
            .GetField("UnmappedCodes", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var upgradeCodes = actCodes.Keys.Concat(unmapped).ToHashSet();
        upgradeCodes.Should().BeEquivalentTo(CoreCodes);

        foreach (var analysis in analyses)
        {
            analysis.Validate().IsSuccess.Should().BeTrue(analysis.Code);
            analysis.DepartmentId.Should().NotBeEmpty(analysis.Code);
            analysis.SpecimenTypeId.Should().NotBeEmpty(analysis.Code);
            analysis.ContainerTypeId.Should().NotBeNull(analysis.Code);

            var tariff = analysis.Tariffs.Should().ContainSingle(t => t.PayerId == null, analysis.Code).Subject;
            tariff.Coefficient.Should().BePositive(analysis.Code);

            if (analysis.NationalActCode is { } act)
            {
                acts.Should().ContainKey(act, $"{analysis.Code} cites an act of the nomenclature");
                tariff.Coefficient.Should().Be(acts[act], $"{analysis.Code} is priced at the coefficient of {act}");
            }

            var codes = analysis.Parameters.Select(p => p.Code).ToHashSet(StringComparer.Ordinal);

            foreach (var parameter in analysis.Parameters)
            {
                var label = $"{analysis.Code}/{parameter.Code}";

                if (parameter.ValueType is ResultValueType.Numeric or ResultValueType.NumericWithOperator)
                    parameter.UnitId.Should().NotBeNull(label);

                if (parameter.ValueType is ResultValueType.Coded or ResultValueType.SemiQuantitative)
                    parameter.CodedOptions.Should().NotBeEmpty(label);

                if (parameter.CalculationFormula is { } formula)
                    FormulaEvaluator.Validate(formula, codes).IsSuccess.Should().BeTrue(label);

                foreach (var range in parameter.ReferenceRanges)
                {
                    range.Validate().IsSuccess.Should().BeTrue(label);
                    if (range.LowerLimit is { } low && range.UpperLimit is { } high)
                        low.Should().BeLessThan(high, label);
                    if (range.AgeFromDays is not null || range.AgeToDays is not null)
                        ReferenceRange.HasUnconfirmedAgeUnit(range.AgeFromDays, range.AgeToDays, range.AgeUnitDays)
                            .Should().BeFalse($"{label}: an age band states its unit");
                }
            }

            // The routine analyses' intervals are all listed by the set-up check until validated.
            if (!CoreCodes.Contains(analysis.Code))
                analysis.Parameters.SelectMany(p => p.ReferenceRanges)
                    .Where(r => r.Source != PendingMarker).Should().BeEmpty(analysis.Code);
        }

        // Tumour markers are reported in arbitrary units (U/mL), not international units.
        var unitCodes = await db.Units.ToDictionaryAsync(u => u.Id, u => u.Code);
        foreach (var marker in (string[])["CA125", "CA153", "CA199"])
            unitCodes[analyses.Single(a => a.Code == marker).Parameters.Single().UnitId!.Value]
                .Should().Be("U_ML", marker);

        // The calculated parameters compute what the bench expects.
        FormulaEvaluator.Evaluate("round([TCA_P]/[TCA_T];2)",
                                  new Dictionary<string, decimal> { ["TCA_P"] = 36m, ["TCA_T"] = 30m })
            .Value.Should().Be(1.20m);
        FormulaEvaluator.Evaluate("round([BILT]-[BILD];1)",
                                  new Dictionary<string, decimal> { ["BILT"] = 10m, ["BILD"] = 2.5m })
            .Value.Should().Be(7.5m);

        WriteReviewCsvIfAsked(analyses, acts,
                              await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name.Fr),
                              await db.SpecimenTypes.ToDictionaryAsync(s => s.Id, s => s.Name.Fr),
                              await db.Units.ToDictionaryAsync(u => u.Id, u => u.DisplaySymbol));
    }

    [Fact]
    public void A_new_QC_target_runs_the_classic_Westgard_multirule()
    {
        // Westgard rules are kept per QC target (material × parameter × analyser), created by the
        // laboratory: there is no global set to seed, the default of a new target is the preset.
        new QcTarget(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()).ActiveRules.Should().Be(
            WestgardRule.OneTwoS | WestgardRule.OneThreeS | WestgardRule.TwoTwoS |
            WestgardRule.R4S | WestgardRule.FourOneS | WestgardRule.TenX);
    }

    // ---- The biologist's review list ----------------------------------------------------------

    /// <summary>
    /// Writes the catalogue as the biologist reviews it — one line per parameter, semicolon-separated
    /// for a French spreadsheet — when <c>MAROCLIS_CATALOGUE_CSV</c> names a file.
    /// </summary>
    private static void WriteReviewCsvIfAsked(IEnumerable<AnalysisTest> analyses, Dictionary<string, decimal> acts, Dictionary<Guid, string> departments,
                                              Dictionary<Guid, string> specimens, Dictionary<Guid, string> units)
    {
        var path = Environment.GetEnvironmentVariable("MAROCLIS_CATALOGUE_CSV");
        if (string.IsNullOrWhiteSpace(path)) return;

        var fr = CultureInfo.GetCultureInfo("fr-FR");
        string N(decimal v) => v.ToString("0.###", fr);
        static string Q(string s) => s.Contains(';') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        string Describe(ReferenceRange r)
        {
            var who = r.AppliesToGender switch { Gender.Male => "H", Gender.Female => "F", _ => "" };
            if (r.AppliesToPregnancy is true) who += (who.Length > 0 ? " " : "") + "grossesse"
                                                     + (r.PregnancyTrimester is { } t ? $" T{t}" : "");
            if (r.AppliesToPregnancy is false) who += (who.Length > 0 ? " " : "") + "hors grossesse";

            var unit = r.EffectiveAgeUnitDays;
            var name = unit switch { ReferenceRange.DaysPerYear => "ans", ReferenceRange.DaysPerMonth => "mois", _ => "j" };
            var age = (r.AgeFromDays, r.AgeToDays) switch
            {
                (null or 0, null) => "",
                ({ } from, null) => $"≥ {from / unit} {name}",
                (null or 0, { } to) => $"< {to / unit} {name}",
                ({ } from, { } to) => $"{from / unit}–{to / unit} {name}"
            };

            var limits = (r.LowerLimit, r.UpperLimit) switch
            {
                ({ } low, { } high) => $"{N(low)}–{N(high)}",
                (null, { } high) => $"≤ {N(high)}",
                ({ } low, null) => $"≥ {N(low)}",
                _ => ""
            };

            var head = string.Join(" ", new[] { who, age }.Where(s => s.Length > 0));
            var text = r.DisplayText?.Fr is { Length: > 0 } d ? (limits.Length > 0 ? $" ({d})" : d) : "";
            var threshold = r.IsDecisionThreshold ? " [seuil de décision]" : "";
            return (head.Length > 0 ? head + " : " : "") + limits + text + threshold;
        }

        var csv = new StringBuilder();
        csv.Append("code;paramètre;nom;secteur;échantillon;unité;acte NABM;coefficient;valeurs de référence;source\r\n");

        foreach (var a in analyses.OrderBy(a => departments[a.DepartmentId]).ThenBy(a => a.Code, StringComparer.Ordinal))
        {
            var coefficient = a.Tariffs.First(t => t.PayerId == null).Coefficient;
            foreach (var p in a.Parameters.OrderBy(p => p.DisplayOrder))
            {
                var ranges = p.ReferenceRanges.Count > 0
                    ? string.Join(" | ", p.ReferenceRanges
                        .OrderBy(r => r.AppliesToGender).ThenBy(r => r.AppliesToPregnancy == true)
                        .ThenBy(r => r.PregnancyTrimester).ThenBy(r => r.AgeFromDays ?? 0)
                        .Select(Describe))
                    : p.ValueType switch
                    {
                        ResultValueType.Coded or ResultValueType.SemiQuantitative =>
                            p.CodedOptions.Any(o => o.IsExpectedNormal)
                                ? "attendu : " + string.Join(", ", p.CodedOptions.Where(o => o.IsExpectedNormal).Select(o => o.Label.Fr))
                                : "valeur codée : " + string.Join(", ", p.CodedOptions.Select(o => o.Label.Fr)),
                        ResultValueType.Text => "texte libre",
                        _ when p.CalculationFormula is not null => "calculé : " + p.CalculationFormula,
                        _ => "selon le réactif"
                    };
                var source = p.ReferenceRanges.Select(r => r.Source).FirstOrDefault(s => s is not null) ?? "";

                csv.Append(string.Join(";", new[]
                {
                    a.Code, p.Code, Q(a.Code == p.Code || a.Parameters.Count == 1 ? a.Name.Fr : $"{a.Name.Fr} — {p.Name.Fr}"),
                    Q(departments[a.DepartmentId]), Q(specimens[a.SpecimenTypeId]),
                    p.UnitId is { } u ? Q(units[u]) : "", a.NationalActCode ?? "",
                    N(coefficient) + (a.NationalActCode is { } act && acts.ContainsKey(act) ? "" : " (sans acte)"),
                    Q(ranges), Q(source)
                })).Append("\r\n");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
