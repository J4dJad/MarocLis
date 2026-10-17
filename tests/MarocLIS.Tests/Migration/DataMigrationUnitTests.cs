using System.Data;
using System.Data.Common;
using System.Text;
using FluentAssertions;
using MarocLIS.Application.Migration;
using MarocLIS.Desktop.Views;
using MarocLIS.Domain.Migration;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Sequencing;
using MarocLIS.Infrastructure.Reprise;
using Xunit;

namespace MarocLIS.Tests.Migration;

/// <summary>
/// « Reprise de données » without a database: reading CSV and xlsx files, the dates and amounts
/// old applications write, the automatic column matching, the SELECT-only guard, the
/// correspondence tables, the row checks and duplicate detection of the simulation, the sources
/// reached through MySQL and ODBC (on a mocked connection) and the profiles kept on the workstation.
/// </summary>
public sealed class DataMigrationUnitTests
{
    private static readonly DateOnly Today = new(2026, 10, 17);

    // ---- CSV ---------------------------------------------------------------------------------------

    [Fact]
    public void Csv_FromAFrenchExcel_IsReadWithItsSemicolonsQuotesAndMark()
    {
        var text = "﻿Nom;Prénom;Adresse\r\nALAOUI;Salma;\"12; rue \"\"des\"\" Orangers\r\nCasablanca\"\r\n\r\nBENNANI;;Rabat\r\n";
        var table = CsvSource.Read(new MemoryStream(Encoding.UTF8.GetBytes(text)));

        table.Columns.Should().Equal("Nom", "Prénom", "Adresse");
        table.Rows.Should().HaveCount(2, "the blank line is not a row");
        table.Rows[0][2].Should().Be("12; rue \"des\" Orangers\r\nCasablanca");
        table.Rows[1][1].Should().BeNull("an empty cell is no value");
    }

    [Fact]
    public void Csv_SeparatorAndEncoding_AreFoundFromTheFile()
    {
        CsvSource.DetectSeparator("id,nom,prenom").Should().Be(',');
        CsvSource.DetectSeparator("id\tnom\tprenom").Should().Be('\t');
        CsvSource.DetectSeparator("\"a;b\",c,d").Should().Be(',', "a separator inside quotes does not count");

        // An export in Windows-1252: « é » is 0xE9 and « € » 0x80, not valid UTF-8.
        var bytes = new byte[] { (byte)'N', (byte)'o', (byte)'m', (byte)'\n', (byte)'H', 0xE9, (byte)'l', 0xE8, (byte)'n', (byte)'e', (byte)' ', 0x80 };
        var table = CsvSource.Read(new MemoryStream(bytes));
        table.Rows[0][0].Should().Be("Hélène €");
    }

    [Fact]
    public void Csv_MaxRows_TruncatesAndSaysSo()
    {
        var table = CsvSource.Parse("a\n1\n2\n3\n", maxRows: 2);
        table.Rows.Should().HaveCount(2);
        table.Truncated.Should().BeTrue();
    }

    [Fact]
    public void CsvLine_QuotesWhatNeedsIt_AndNeverWritesAFormula()
    {
        CsvSource.Line(["a;b", "say \"hi\"", "=CMD()", "-12.5", "@x"])
            .Should().Be("\"a;b\";\"say \"\"hi\"\"\";'=CMD();-12.5;'@x");
    }

    // ---- xlsx --------------------------------------------------------------------------------------

    [Fact]
    public void Xlsx_TemplateWrittenThenRead_GivesItsHeadersAndExampleRow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"reprise-{Guid.NewGuid():N}.xlsx");
        try
        {
            var workbook = new MigrationWorkbook();
            workbook.WriteTemplate(DataMigrationKind.Patients, path);

            workbook.SheetNames(path).Should().HaveCount(1);
            var table = workbook.Read(path, null);
            table.Columns.Should().Equal(MigrationTemplates.Headers(DataMigrationKind.Patients));
            table.Rows.Should().HaveCount(1);
            table.Rows[0][table.IndexOf("Nom")].Should().Be("ALAOUI");
            table.Rows[0][table.IndexOf("Date de naissance")].Should().Be("05/03/1971", "the template's cells are text");

            // Every template is matched to its own fields without help.
            foreach (var kind in MigrationSchema.Kinds)
            {
                var headers = MigrationTemplates.Headers(kind);
                ColumnMatcher.AutoMatch(kind, headers).Should().HaveCount(MigrationSchema.Fields(kind).Count, kind.ToString());
                MigrationTemplates.Example(kind).Should().HaveCount(headers.Count, kind.ToString());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Xlsx_TypedCells_AreReadAsTheTextTheChecksExpect()
    {
        var path = Path.Combine(Path.GetTempPath(), $"reprise-{Guid.NewGuid():N}.xlsx");
        try
        {
            using (var book = new ClosedXML.Excel.XLWorkbook())
            {
                var sheet = book.AddWorksheet("Patients");
                sheet.Cell(1, 1).Value = "Nom";
                sheet.Cell(1, 2).Value = "Date de naissance";
                sheet.Cell(1, 3).Value = "Prix";
                sheet.Cell(2, 1).Value = "ALAOUI";
                sheet.Cell(2, 2).Value = new DateTime(1971, 3, 5);
                sheet.Cell(2, 3).Value = 1234.5;
                book.AddWorksheet("Autre").Cell(1, 1).Value = "X";
                book.SaveAs(path);
            }

            var workbook = new MigrationWorkbook();
            workbook.SheetNames(path).Should().Equal("Patients", "Autre");
            var table = workbook.Read(path, "Patients");
            ValueParsers.ParseDate(table.Rows[0][1], Today).Should().Be(new DateOnly(1971, 3, 5));
            ValueParsers.ParseDecimal(table.Rows[0][2]).Should().Be(1234.5m);

            var missing = () => workbook.Read(path, "Absente");
            missing.Should().Throw<ArgumentException>().WithMessage("Migration_SheetNotFound");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Values ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("05/03/1971", 1971, 3, 5)]
    [InlineData("5/3/1971", 1971, 3, 5)]
    [InlineData("1971-03-05", 1971, 3, 5)]
    [InlineData("19710305", 1971, 3, 5)]
    [InlineData("05.03.1971", 1971, 3, 5)]
    [InlineData("05/03/71", 1971, 3, 5)]
    [InlineData("05/03/24", 2024, 3, 5)]
    [InlineData("26000", 1971, 3, 8)]
    [InlineData("2024-02-14 08:30:00", 2024, 2, 14)]
    public void Dates_InTheFormsOldApplicationsWrite_AreRead(string text, int year, int month, int day) =>
        ValueParsers.ParseDate(text, Today).Should().Be(new DateOnly(year, month, day));

    [Theory]
    [InlineData("31/02/2020")]
    [InlineData("hier")]
    [InlineData("")]
    public void Dates_ThatAreNot_AreNull(string text) => ValueParsers.ParseDate(text, Today).Should().BeNull();

    [Theory]
    [InlineData("1 234,50", 1234.50)]
    [InlineData("1.234,50", 1234.50)]
    [InlineData("1,234.50", 1234.50)]
    [InlineData("1234.5", 1234.5)]
    [InlineData("120 DH", 120)]
    [InlineData("-15,5", -15.5)]
    [InlineData("1.234.567", 1234567)]
    public void Amounts_AreReadWhateverTheSeparators(string text, double expected) =>
        ValueParsers.ParseDecimal(text).Should().Be((decimal)expected);

    [Fact]
    public void Results_KeepTheirOperator_AndTextIsNotANumber()
    {
        ValueParsers.ParseResult("5,4").Should().Be(((string?)null, 5.4m));
        ValueParsers.ParseResult("< 0.5").Should().Be(("<", 0.5m));
        ValueParsers.ParseResult("≥200").Should().Be((">=", 200m));
        ValueParsers.ParseResult("Négatif").Should().BeNull();
        ValueParsers.ParseResult("5,4 g/l").Should().BeNull();
        ValueParsers.ParseInteger("12,5").Should().BeNull();
        ValueParsers.ParseInteger("42").Should().Be(42);
    }

    // ---- Column matching ---------------------------------------------------------------------------

    [Fact]
    public void Columns_AreMatchedByNameSynonymAndLikeness()
    {
        var columns = new[] { "ID_PATIENT", "NOM_PAT", "Prénom", "DATE_NAISS", "SEXE", "N° CIN", "GSM", "MUTUELLE", "Obs" };
        var matched = ColumnMatcher.AutoMatch(DataMigrationKind.Patients, columns);

        matched[MigrationSchema.SourceId].Should().Be("ID_PATIENT");
        matched[MigrationSchema.LastName].Should().Be("NOM_PAT");
        matched[MigrationSchema.FirstName].Should().Be("Prénom");
        matched[MigrationSchema.BirthDate].Should().Be("DATE_NAISS");
        matched[MigrationSchema.Sex].Should().Be("SEXE");
        matched[MigrationSchema.Mobile].Should().Be("GSM");
        matched[MigrationSchema.PayerCode].Should().Be("MUTUELLE");
        matched.Values.Should().NotContain("Obs");
        matched.Values.Should().OnlyHaveUniqueItems("one source column feeds one field");
    }

    // ---- SELECT-only guard -------------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT * FROM patients")]
    [InlineData("select p.id, p.nom from dbo.patients p where p.nom <> 'DELETE' order by 1;")]
    [InlineData("WITH r AS (SELECT * FROM resultats) SELECT * FROM r")]
    [InlineData("SELECT [update], `drop` FROM t -- ; DROP TABLE t")]
    [InlineData("SELECT * FROM t /* INSERT INTO x */ WHERE a = 1")]
    public void Guard_AcceptsOneReadOnlyQuery(string sql) => SourceQueryGuard.Check(sql).IsSuccess.Should().BeTrue(sql);

    [Theory]
    [InlineData("INSERT INTO patients VALUES (1)", "Migration_QuerySelectOnly")]
    [InlineData("UPDATE patients SET nom = 'X'", "Migration_QuerySelectOnly")]
    [InlineData("DELETE FROM patients", "Migration_QuerySelectOnly")]
    [InlineData("DROP TABLE patients", "Migration_QuerySelectOnly")]
    [InlineData("EXEC sp_who", "Migration_QuerySelectOnly")]
    [InlineData("SELECT 1; DROP TABLE patients", "Migration_QuerySingleStatement")]
    [InlineData("SELECT 1\nGO\nSELECT 2", "Migration_QuerySingleStatement")]
    [InlineData("SELECT * INTO copie FROM patients", "Migration_QueryForbiddenWord")]
    [InlineData("WITH x AS (SELECT 1 a) DELETE FROM patients", "Migration_QueryForbiddenWord")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'y')", "Migration_QueryForbiddenWord")]
    [InlineData("SELECT xp_cmdshell('dir')", "Migration_QueryForbiddenWord")]
    [InlineData("SELECT * FROM t FOR UPDATE", "Migration_QueryForbiddenWord")]
    [InlineData("SELECT 'unterminated FROM t", "Migration_QueryUnterminated")]
    [InlineData("   ", "Migration_QueryRequired")]
    public void Guard_RefusesWhatCouldWrite(string sql, string key)
    {
        var result = SourceQueryGuard.Check(sql);
        result.IsFailure.Should().BeTrue(sql);
        result.FirstError.ResourceKey.Should().Be(key, sql);
    }

    [Fact]
    public void Guard_RefusesAQueryTooLong() =>
        SourceQueryGuard.Check("SELECT " + new string('a', SourceQueryGuard.MaxLength))
            .FirstError.ResourceKey.Should().Be("Migration_QueryTooLong");

    // ---- Correspondences (point 2) -----------------------------------------------------------------

    private static readonly IReadOnlyList<CodeTarget> Payers =
        [new("CNSS", "CNSS — Caisse nationale", ["Caisse nationale de sécurité sociale"]), new("CNOPS", "CNOPS — Mutuelles", ["CNOPS"])];

    private static readonly IReadOnlyList<CodeTarget> Analyses =
    [
        new("GLY/GLY", "GLY — Glycémie", ["GLY", "Glycémie", "Glycémie à jeun", "B118"]),
        new("NFS/HB", "NFS/HB — Hémogramme : Hémoglobine", ["HB", "Hémoglobine"]),
        new("NFS/GR", "NFS/GR — Hémogramme : Hématies", ["GR", "Hématies"])
    ];

    [Theory]
    [InlineData("m", "M")]
    [InlineData("Masculin", "M")]
    [InlineData("H", "M")]
    [InlineData("1", "M")]
    [InlineData("Mme", "F")]
    [InlineData("féminin", "F")]
    [InlineData("2", "F")]
    [InlineData("ND", "U")]
    public void Suggester_ReadsTheSexesOldApplicationsUse(string value, string expected) =>
        CorrespondenceSuggester.Suggest(MigrationCodeList.Sex, value, [new("M", "M"), new("F", "F"), new("U", "U")])
            .Should().Be(expected);

    [Fact]
    public void Suggester_FindsPayersAnalysesAndSpecialties_AndLeavesTheUnknownOpen()
    {
        CorrespondenceSuggester.Suggest(MigrationCodeList.Payer, "cnss", Payers).Should().Be("CNSS");
        CorrespondenceSuggester.Suggest(MigrationCodeList.Payer, "RAMED", Payers).Should().BeNull();

        CorrespondenceSuggester.Suggest(MigrationCodeList.Analysis, "GLY", Analyses).Should().Be("GLY/GLY");
        CorrespondenceSuggester.Suggest(MigrationCodeList.Analysis, "B118", Analyses).Should().Be("GLY/GLY", "the NABM act is an alias");
        CorrespondenceSuggester.Suggest(MigrationCodeList.Analysis, "Hémoglobine", Analyses).Should().Be("NFS/HB");
        CorrespondenceSuggester.Suggest(MigrationCodeList.Analysis, "TSH", Analyses).Should().BeNull();

        var kinds = Enum.GetNames<MarocLIS.Domain.Orders.PrescriberKind>().Select(n => new CodeTarget(n, n)).ToList();
        CorrespondenceSuggester.Suggest(MigrationCodeList.Specialty, "Médecin généraliste", kinds).Should().Be("GeneralPractitioner");
        CorrespondenceSuggester.Suggest(MigrationCodeList.Specialty, "Chirurgien dentiste", kinds).Should().Be("Dentist");
    }

    [Fact]
    public void Tables_AreKeyedByTheNormalisedValue_AndCountTheDistinctValues()
    {
        var tables = new CorrespondenceTables();
        tables.Set(MigrationCodeList.Payer, " Mut. Fonct. ", "CNOPS");
        tables.TargetOf(MigrationCodeList.Payer, "MUT FONCT").Should().Be("CNOPS");
        tables.TargetOf(MigrationCodeList.Sex, "MUT FONCT").Should().BeNull();
        tables.Clone().TargetOf(MigrationCodeList.Payer, "mut. fonct.").Should().Be("CNOPS");

        var table = CsvSource.Parse("Nom;Sexe;Organisme\nA;m;CNSS\nB;M.;Mut. Fonct.\nC;F;RAMED\nD;f;\n");
        var columns = ColumnMatcher.AutoMatch(DataMigrationKind.Patients, table.Columns);
        var targets = new Dictionary<MigrationCodeList, IReadOnlyList<CodeTarget>>
        {
            [MigrationCodeList.Sex] = [new("M", "M"), new("F", "F"), new("U", "U")],
            [MigrationCodeList.Payer] = Payers
        };

        var rows = CorrespondenceSuggester.Rows(table, columns, DataMigrationKind.Patients, tables, targets);
        rows.Where(r => r.List == MigrationCodeList.Sex).Select(r => (r.SourceValue, r.Count, r.Target))
            .Should().BeEquivalentTo([("m", 2, "M"), ("F", 2, "F")]);
        rows.Single(r => r.SourceValue == "Mut. Fonct.").Should().Match<CorrespondenceRow>(r => r.Target == "CNOPS" && !r.Suggested);
        rows.Single(r => r.SourceValue == "CNSS").Suggested.Should().BeTrue();
        rows.Single(r => r.SourceValue == "RAMED").Target.Should().BeNull();
    }

    [Fact]
    public void UnmappedValue_BlocksTheImport_UntilItsCorrespondenceIsChosen()
    {
        var table = CsvSource.Parse(
            "Ancien identifiant;Ancien identifiant patient;Date du résultat;Code analyse;Résultat\n" +
            "R1;P1;14/02/2024;GLY;1,05\nR2;P1;14/02/2024;TSHUS;2,1\nR3;P1;14/02/2024;TSHUS;1,9\nR4;P1;14/02/2024;ZZ;x\n");
        var patient = Guid.NewGuid();
        var context = Context(patientRecords: new Dictionary<string, Guid> { [MigrationPlanner.KeyOf("OLD", DataMigrationKind.Patients, "P1")] = patient },
                              targets: new() { [MigrationCodeList.Analysis] = Analyses });

        var blocked = Plan(DataMigrationKind.History, table, context);
        blocked.IsBlocked.Should().BeTrue();
        blocked.Anomalies.Where(a => a.Severity == MigrationSeverity.Blocking).Select(a => a.ProblemArgs[0])
            .Should().BeEquivalentTo(["TSHUS", "ZZ"]);
        blocked.Anomalies.Single(a => a.Severity == MigrationSeverity.Blocking && (string?)a.ProblemArgs[0] == "TSHUS")
            .ProblemArgs[1].Should().Be(2, "the anomaly counts the rows that wait for it");

        var tables = new CorrespondenceTables();
        tables.Set(MigrationCodeList.Analysis, "TSHUS", "NFS/HB");
        tables.Set(MigrationCodeList.Analysis, "zz", CorrespondenceTables.Ignore);
        var settled = Plan(DataMigrationKind.History, table, context, tables);

        settled.IsBlocked.Should().BeFalse();
        settled.ToCreate.Should().Be(3);
        settled.Skipped.Should().Be(1, "an ignored value skips its rows");
        settled.Rows.Single(r => r.Row == 3).Text("Parameter").Should().Be("NFS/HB");
    }

    // ---- Row checks and duplicates -----------------------------------------------------------------

    [Fact]
    public void PatientRows_AreChecked_FieldByField()
    {
        var table = CsvSource.Parse(
            "Ancien identifiant;Nom;Prénom;Date de naissance;Âge (ans);Sexe;CIN;Téléphone mobile;E-mail\n" +
            "P1;ALAOUI;Salma;05/03/1971;;F;AB123456;0661234567;salma@exemple.ma\n" +
            "P2;;Karim;01/01/1980;;M;;;\n" +
            "P3;IDRISSI;Omar;;;M;;;\n" +
            "P4;FASSI;Nadia;31/02/1990;35;F;123;06;pas-un-mail\n" +
            "P5;TAZI;Ali;;45;X;;;\n");

        var analysis = Plan(DataMigrationKind.Patients, table, Context(targets: new() { [MigrationCodeList.Sex] = [new("M", "M"), new("F", "F"), new("U", "U")] }));

        analysis.Rows.Single(r => r.Row == 2).Outcome.Should().Be(MigrationOutcome.Create);
        Problems(analysis, 3).Should().Contain("Migration_Required");
        Problems(analysis, 4).Should().Contain("Migration_BirthDateOrAgeRequired");
        Problems(analysis, 5).Should().Contain(["Migration_DateUnreadable", "Migration_CinInvalid", "Migration_PhoneInvalid", "Migration_EmailInvalid"]);
        analysis.Rows.Single(r => r.Row == 5).Outcome.Should().Be(MigrationOutcome.Create, "what cannot be read is left out, the age stands in");
        analysis.Rows.Single(r => r.Row == 6).Outcome.Should().Be(MigrationOutcome.Create, "the age stands in for the date");
        analysis.InError.Should().Be(2);

        // The field named in a message is a key the screen translates.
        analysis.Anomalies.First(a => a.ProblemKey == "Migration_Required").ProblemArgs[0].Should().Be("Migration.Field.LastName");
    }

    [Fact]
    public void Duplicates_InTheSourceAndAgainstExistingPatients_AreFound()
    {
        var existing = new PatientSnapshot(Guid.NewGuid(), "D2025-0001", "BENNANI", "KARIM", new DateOnly(1980, 1, 1), Gender.Male,
                                           "BK445566", null, null, null, null, null, null, null, null, []);
        var byName = new PatientSnapshot(Guid.NewGuid(), "D2025-0002", "TAZI", "ALI", new DateOnly(1950, 6, 1), Gender.Male,
                                         null, null, null, null, null, null, null, null, null, []);
        var table = CsvSource.Parse(
            "Ancien identifiant;Nom;Prénom;Date de naissance;CIN\n" +
            "P1;ALAOUI;Salma;05/03/1971;AB123456\n" +
            "P1;ALAOUI;Salma;05/03/1971;\n" +
            "P2;ALAOUI;Salma;05/03/1971;\n" +
            "P3;IDRISSI;Omar;01/01/1990;AB123456\n" +
            "P4;Bennani;Karim;02/02/1982;bk 445566\n" +
            "P5;Tazi;Ali;01/06/1950;\n");

        var analysis = Plan(DataMigrationKind.Patients, table, Context(patients: [existing, byName]));

        Problems(analysis, 3).Should().Contain("Migration_DuplicateInSource");
        analysis.Rows.Single(r => r.Row == 3).Outcome.Should().Be(MigrationOutcome.Error);
        Problems(analysis, 4).Should().Contain("Migration_DuplicateNameInSource");
        Problems(analysis, 5).Should().Contain("Migration_DuplicateCinInSource");

        var cin = analysis.Rows.Single(r => r.Row == 6);
        cin.Outcome.Should().Be(MigrationOutcome.Update);
        cin.ExistingId.Should().Be(existing.Id, "the CIN identifies the patient whatever the spelling");
        Problems(analysis, 6).Should().Contain("Migration_PatientMatchedByCin");

        var name = analysis.Rows.Single(r => r.Row == 7);
        name.Outcome.Should().Be(MigrationOutcome.Update);
        name.ExistingId.Should().Be(byName.Id);
        analysis.Anomalies.Should().Contain(a => a.Row == 7 && a.ProblemKey == "Migration_PatientMatchedByName"
                                                 && a.Severity == MigrationSeverity.Warning,
                                            "a match by name is shown for confirmation");

        var notLinked = Plan(DataMigrationKind.Patients, table, Context(patients: [existing, byName]), link: false);
        notLinked.Rows.Single(r => r.Row == 7).Outcome.Should().Be(MigrationOutcome.Skip);
    }

    [Fact]
    public void ARowAlreadyTakenOver_IsUpdated_NotCreatedAgain()
    {
        var id = Guid.NewGuid();
        var snapshot = new PatientSnapshot(id, "D2025-0009", "ALAOUI", "SALMA", new DateOnly(1971, 3, 5), Gender.Female,
                                           null, null, null, null, null, null, null, null, null, []);
        var context = Context(patients: [snapshot],
                              records: new Dictionary<string, Guid> { [MigrationPlanner.KeyOf("OLD", DataMigrationKind.Patients, "P1")] = id });
        var same = Plan(DataMigrationKind.Patients, CsvSource.Parse("Ancien identifiant;Nom;Prénom;Date de naissance\nP1;ALAOUI;Salma;05/03/1971\n"), context);
        same.Rows.Single().Should().Match<PlannedRow>(r => r.Outcome == MigrationOutcome.Skip && r.ExistingId == id && r.KnownSourceKey,
                                                       "nothing new: the second run leaves it");

        var analysis = Plan(DataMigrationKind.Patients, CsvSource.Parse("Ancien identifiant;Nom;Prénom;Date de naissance;GSM\nP1;ALAOUI;Salma;05/03/1971;0661234567\n"), context);
        analysis.Rows.Single().Should().Match<PlannedRow>(r => r.Outcome == MigrationOutcome.Update && r.ExistingId == id && r.KnownSourceKey);
        MigrationPlanner.KeyOf("old", DataMigrationKind.Patients, "P1").Should().Be(MigrationPlanner.KeyOf("OLD", DataMigrationKind.Patients, "P1"));
    }

    [Fact]
    public void Balances_RefuseNegativeAndSkipZero_AndHistoryNeedsItsPatient()
    {
        var patient = Guid.NewGuid();
        var context = Context(patientRecords: new Dictionary<string, Guid> { [MigrationPlanner.KeyOf("OLD", DataMigrationKind.Patients, "P1")] = patient });
        var balances = Plan(DataMigrationKind.OpeningBalances, CsvSource.Parse(
            "Ancien identifiant;Ancien identifiant patient;Montant dû (MAD)\nF1;P1;150,00\nF2;P1;-20\nF3;P1;0\nF4;P9;10\nF5;;10\n"), context);

        balances.Rows.Single(r => r.Row == 2).Outcome.Should().Be(MigrationOutcome.Create);
        Problems(balances, 3).Should().Contain("Migration_NegativeBalance");
        balances.Rows.Single(r => r.Row == 4).Outcome.Should().Be(MigrationOutcome.Skip);
        Problems(balances, 5).Should().Contain("Migration_PatientNotFound");
        Problems(balances, 6).Should().Contain("Migration_BalanceDebtorRequired");
    }

    private static List<string> Problems(MigrationAnalysis analysis, int row) =>
        [.. analysis.Anomalies.Where(a => a.Row == row).Select(a => a.ProblemKey)];

    private static MigrationAnalysis Plan(DataMigrationKind kind, SourceTable table, MigrationContext context,
                                          CorrespondenceTables? tables = null, bool link = true) =>
        new MigrationPlanner(new MigrationRequest
        {
            Kind = kind,
            SourceSystem = "OLD",
            SourceLabel = "test",
            Table = table,
            Columns = ColumnMatcher.AutoMatch(kind, table.Columns),
            Correspondences = tables ?? new CorrespondenceTables(),
            LinkByNameAndBirthDate = link
        }, context).Plan();

    private static MigrationContext Context(
        IReadOnlyList<PatientSnapshot>? patients = null,
        Dictionary<string, Guid>? records = null,
        Dictionary<string, Guid>? patientRecords = null,
        Dictionary<MigrationCodeList, IReadOnlyList<CodeTarget>>? targets = null) => new()
    {
        Patients = patients ?? [],
        Records = records ?? [],
        PatientRecords = patientRecords ?? [],
        Targets = targets ?? new Dictionary<MigrationCodeList, IReadOnlyList<CodeTarget>>
        {
            [MigrationCodeList.Sex] = [new("M", "M"), new("F", "F"), new("U", "U")]
        },
        Today = Today
    };

    // ---- Numbering start (Paramètres › Numérotations) ----------------------------------------------

    [Fact]
    public void NumberingStart_CanBeSetBeforeUse_AndOnlyForwardAfter_ButNeverForAFiscalSeries()
    {
        var file = new NumberSequence(SequencePurpose.MedicalRecord, null, null, "D{YY}-{SEQ:6}");
        file.SetNextValue(5001).IsSuccess.Should().BeTrue();
        file.SetNextValue(0).FirstError.ResourceKey.Should().Be("Sequence_StartOutOfRange");

        var invoice = new NumberSequence(SequencePurpose.Invoice, null, null, "F{YEAR}-{SEQ:6}");
        invoice.SetNextValue(100).IsSuccess.Should().BeTrue("a fiscal series may start anywhere before its first number");
        invoice.Allocate(DateTimeOffset.Now, "A").Value.Should().EndWith("000100");
        invoice.SetNextValue(500).FirstError.ResourceKey.Should().Be("Sequence_StartFrozenAfterFirstUse");

        file.Allocate(DateTimeOffset.Now, "A").Value.Should().EndWith("005001");
        file.SetNextValue(4000).FirstError.ResourceKey.Should().Be("Sequence_StartBelowLastUsed");
        file.SetNextValue(9000).IsSuccess.Should().BeTrue("a file series may jump forward, never back");
        file.Allocate(DateTimeOffset.Now, "A").Value.Should().EndWith("009000");
    }

    // ---- MySQL and ODBC sources, on a mocked connection ---------------------------------------------

    [Fact]
    public void ConnectionStrings_AreBuiltPerProvider_ReadOnlyWhereTheDriverCan()
    {
        var factory = new SourceDatabaseFactory();

        var express = factory.ConnectionString(new SourceConnectionSettings
        {
            Provider = SourceProvider.SqlServer, Server = @".\SQLEXPRESS", Database = "LABO", IntegratedSecurity = true
        });
        express.Should().Contain(@"Data Source=.\SQLEXPRESS").And.Contain("Initial Catalog=LABO")
               .And.Contain("Application Intent=ReadOnly").And.Contain("Integrated Security=True");

        var mysql = factory.ConnectionString(new SourceConnectionSettings
        {
            Provider = SourceProvider.MySql, Server = "srv", UserName = "lecture", Password = "s3cret", Database = "labo"
        });
        mysql.Should().Contain("Port=3306").And.Contain("Database=labo").And.Contain("User ID=lecture");

        var odbc = factory.ConnectionString(new SourceConnectionSettings { Provider = SourceProvider.Odbc, OdbcDsn = "ANCIEN", UserName = "u" });
        odbc.Should().Contain("Dsn=ANCIEN").And.Contain("UID=u");

        var none = () => factory.ConnectionString(new SourceConnectionSettings { Provider = SourceProvider.Odbc });
        none.Should().Throw<ArgumentException>().WithMessage("Migration_OdbcSourceRequired");

        new SourceConnectionSettings { Provider = SourceProvider.MySql, Server = "srv", Password = "s3cret" }
            .Describe().Should().NotContain("s3cret");
        new SourceConnectionSettings { Provider = SourceProvider.Odbc, OdbcConnectionString = "Driver={x};PWD=s3cret;Dbq=a.mdb" }
            .Describe().Should().NotContain("s3cret");
    }

    [Fact]
    public async Task MySqlSource_ListsReadsAndQuotesWithBackticks_AlwaysRollingBack()
    {
        var connection = new FakeConnection();
        connection.Results["SELECT SCHEMA_NAME"] = Table(["SCHEMA_NAME"], ["labo"], ["archives"]);
        connection.Results["SELECT NULL, TABLE_NAME"] = Table(["c0", "TABLE_NAME", "TABLE_TYPE"], [null, "patients", "BASE TABLE"], [null, "v_res", "VIEW"]);
        connection.Results["SELECT * FROM `pat``ients`"] = Table(["id", "nom", "naissance"], [1, "ALAOUI", new DateTime(1971, 3, 5)]);
        var reader = new SourceDatabaseReader(new FakeFactory(SourceProvider.MySql, connection));
        var settings = new SourceConnectionSettings { Provider = SourceProvider.MySql, Server = "srv", Database = "labo" };

        (await reader.ListDatabasesAsync(settings)).Should().Equal("labo", "archives");
        (await reader.ListTablesAsync(settings)).Should().BeEquivalentTo([new SourceTableInfo(null, "patients", false), new SourceTableInfo(null, "v_res", true)]);

        var table = await reader.ReadAsync(settings, new SourceSelection(new SourceTableInfo(null, "pat`ients", false), null), 100);
        table.Columns.Should().Equal("id", "nom", "naissance");
        table.Rows.Single().Should().Equal("1", "ALAOUI", "1971-03-05");

        connection.RolledBack.Should().Be(3, "every read runs in a transaction that is rolled back");
        connection.Committed.Should().Be(0);

        var refused = () => reader.ReadAsync(settings, new SourceSelection(null, "DELETE FROM patients"), 100);
        await refused.Should().ThrowAsync<ArgumentException>().WithMessage("Migration_QuerySelectOnly");
        connection.Executed.Should().NotContain(s => s.Contains("DELETE"));
    }

    [Fact]
    public async Task OdbcSource_ListsTablesFromTheDriverCatalogue_AndReadsAQuery()
    {
        var connection = new FakeConnection();
        var schema = Table(["TABLE_SCHEM", "TABLE_NAME", "TABLE_TYPE"],
                           [null, "Patients", "TABLE"], [null, "MSysObjects", "SYSTEM TABLE"], [null, "Resultats", "VIEW"], [null, "x", "SYNONYM"]);
        connection.Schema = schema;
        connection.Results["SELECT nom FROM Patients"] = Table(["nom"], ["ALAOUI"], [DBNull.Value]);
        var reader = new SourceDatabaseReader(new FakeFactory(SourceProvider.Odbc, connection));
        var settings = new SourceConnectionSettings { Provider = SourceProvider.Odbc, OdbcDsn = "ANCIEN" };

        (await reader.ListDatabasesAsync(settings)).Should().BeEmpty("a DSN names its database");
        (await reader.ListTablesAsync(settings)).Select(t => (t.Name, t.IsView)).Should().Equal(("Patients", false), ("Resultats", true));

        var table = await reader.ReadAsync(settings, new SourceSelection(null, "SELECT nom FROM Patients;"), 100);
        table.Rows.Select(r => r[0]).Should().Equal("ALAOUI", null);
        (await reader.TestAsync(settings)).Should().Be("fake 1.0");
    }

    private static DataTable Table(string[] columns, params object?[][] rows)
    {
        var table = new DataTable();
        foreach (var column in columns)
        {
            var name = column;
            for (var n = 2; table.Columns.Contains(name); n++) name = column + "#" + n;
            table.Columns.Add(name, typeof(object));
        }
        foreach (var row in rows) table.Rows.Add([.. row.Select(v => v ?? DBNull.Value)]);
        return table;
    }

    private sealed class FakeFactory(SourceProvider provider, FakeConnection connection) : ISourceDatabaseFactory
    {
        public string ConnectionString(SourceConnectionSettings settings) => "fake";

        public Task<ISourceDatabase> OpenAsync(SourceConnectionSettings settings, CancellationToken ct) =>
            Task.FromResult<ISourceDatabase>(new FakeDatabase(provider, connection));
    }

    private sealed class FakeDatabase(SourceProvider provider, FakeConnection connection) : ISourceDatabase
    {
        public SourceProvider Provider => provider;
        public DbConnection Connection => connection;

        public string Quote(string identifier) => provider == SourceProvider.MySql
            ? "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`"
            : "\"" + identifier + "\"";

        public async Task<DbTransaction?> BeginReadOnlyAsync(CancellationToken ct) =>
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeConnection : DbConnection
    {
        public Dictionary<string, DataTable> Results { get; } = new(StringComparer.Ordinal);
        public DataTable Schema { get; set; } = new();
        public List<string> Executed { get; } = [];
        public int RolledBack { get; set; }
        public int Committed { get; set; }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = "fake";
        public override string Database => "labo";
        public override string DataSource => "srv";
        public override string ServerVersion => "fake 1.0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        public override DataTable GetSchema(string collectionName) => Schema;
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeTransaction(this);
        protected override DbCommand CreateDbCommand() => new FakeCommand(this);

        public DataTable Find(string sql)
        {
            Executed.Add(sql);
            return Results.FirstOrDefault(r => sql.StartsWith(r.Key, StringComparison.Ordinal)).Value
                   ?? throw new InvalidOperationException("Unexpected query: " + sql);
        }
    }

    private sealed class FakeTransaction(FakeConnection connection) : DbTransaction
    {
        protected override DbConnection DbConnection => connection;
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        public override void Commit() => connection.Committed++;
        public override void Rollback() => connection.RolledBack++;
    }

    private sealed class FakeCommand(FakeConnection connection) : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => throw new NotSupportedException("the reader never writes");
        public override object? ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => connection.Find(CommandText).CreateDataReader();
    }

    // ---- Profiles ----------------------------------------------------------------------------------

    [Fact]
    public void Profile_KeepsColumnsAndCorrespondences_AndNeverThePasswordInClear()
    {
        var folder = Path.Combine(Path.GetTempPath(), "reprise-profils-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MigrationProfileStore(folder);
            var profile = new MigrationProfile
            {
                Name = "LABOWIN / site 1",
                SourceSystem = "LABOWIN",
                Connection = new SourceConnectionSettings { Provider = SourceProvider.MySql, Server = "srv", UserName = "u", Password = "s3cret" }
            };
            profile.For(DataMigrationKind.Patients).Columns[MigrationSchema.LastName] = "NOM_PAT";
            profile.For(DataMigrationKind.History).TableName = "resultats";
            profile.Correspondences.Set(MigrationCodeList.Analysis, "GLYC", "GLY/GLY");

            store.Save(profile, "s3cret", rememberPassword: true);

            var file = Directory.GetFiles(folder).Single();
            File.ReadAllText(file).Should().NotContain("s3cret");

            var loaded = store.Load("LABOWIN / site 1")!;
            store.Names().Should().Equal("LABOWIN / site 1");
            loaded.For(DataMigrationKind.Patients).Columns[MigrationSchema.LastName].Should().Be("NOM_PAT");
            loaded.For(DataMigrationKind.History).TableName.Should().Be("resultats");
            loaded.Correspondences.TargetOf(MigrationCodeList.Analysis, "glyc").Should().Be("GLY/GLY");
            loaded.Connection!.Password.Should().BeNull();
            if (OperatingSystem.IsWindows()) MigrationProfileStore.PasswordOf(loaded).Should().Be("s3cret");
            else loaded.ProtectedPassword.Should().BeNull("without DPAPI the password is not kept at all");

            var exported = Encoding.UTF8.GetString(MigrationProfileStore.Export(loaded));
            exported.Should().NotContain("ProtectedPassword\": \"");
            MigrationProfileStore.Import(Encoding.UTF8.GetBytes(exported)).Correspondences
                .TargetOf(MigrationCodeList.Analysis, "GLYC").Should().Be("GLY/GLY");

            store.Delete("LABOWIN / site 1");
            store.Names().Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
