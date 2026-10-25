using System.Globalization;
using System.Text;
using FluentAssertions;
using MarocLIS.Application.Migration;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Migration;
using MarocLIS.Tests.Licensing;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Reprise de données » and of its profiles, moved out of the workstation's
/// DataMigrationView and DataMigrationProfiles, now shared with the web.
/// </summary>
public sealed class DataMigrationScreenRulesTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    private static SourceTable Patients() =>
        new(["id", "nom", "prenom", "sexe"],
            [["P1", "EL AMRANI", "Karim", "M"], ["P2", "BENNANI", "Salma", "F"], ["P3", " ", "Nadia", "F"]]);

    private static MigrationAnalysis Analysis(params MigrationOutcome[] outcomes) => Analysis([], outcomes);

    private static MigrationAnalysis Analysis(IReadOnlyList<MigrationAnomaly> anomalies, params MigrationOutcome[] outcomes) =>
        new()
        {
            Kind = DataMigrationKind.Patients,
            Rows = [.. outcomes.Select((o, i) => new PlannedRow { Row = i + 2, Outcome = o })],
            Anomalies = anomalies
        };

    private static MigrationAnomaly Blocking() =>
        new(0, "Sex", MigrationSeverity.Blocking, "Migration.NoCorrespondence", ["MigrationCodeList.Sex", "X"]);

    private static MigrationBatchRow Batch(bool undone = false, bool completed = true, int created = 3, bool interrupted = false) =>
        new(Guid.NewGuid(), DataMigrationKind.Patients, "LABOWIN", "patients.csv",
            new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero), completed ? DateTimeOffset.UtcNow : null, "admin",
            3, created, 0, 0, 0, interrupted, undone, null);

    // ---- Rights and source ---------------------------------------------------------------------

    [Fact]
    public void Running_takes_the_migration_right_and_importing_a_simulation_that_would_write()
    {
        var operatorOnly = new LicenceTestKit.User(true, Permissions.Results.View);
        var migration = new LicenceTestKit.User(true, Permissions.Administration.Migration);

        DataMigrationScreenRules.CanRun(operatorOnly).Should().BeFalse();
        DataMigrationScreenRules.CanRun(migration).Should().BeTrue();
        DataMigrationScreenRules.CanImport(migration, null).Should().BeFalse();
        DataMigrationScreenRules.CanImport(migration, Analysis(MigrationOutcome.Skip)).Should().BeFalse();
        DataMigrationScreenRules.CanImport(migration, Analysis([Blocking()], MigrationOutcome.Create)).Should().BeFalse();
        DataMigrationScreenRules.CanImport(migration, Analysis(MigrationOutcome.Create)).Should().BeTrue();
        DataMigrationScreenRules.CanImport(operatorOnly, Analysis(MigrationOutcome.Create)).Should().BeFalse();
    }

    [Fact]
    public void A_source_file_is_named_with_its_sheet_and_read_from_the_profile_s_one_when_it_has_it()
    {
        DataMigrationScreenRules.IsWorkbook("patients.XLSX").Should().BeTrue();
        DataMigrationScreenRules.IsWorkbook("patients.csv").Should().BeFalse();
        DataMigrationScreenRules.FileLabel("/tmp/x/patients.xlsx", "Feuil1").Should().Be("patients.xlsx › Feuil1");
        DataMigrationScreenRules.FileLabel("patients.csv", null).Should().Be("patients.csv");

        DataMigrationScreenRules.IsSourceFile("export.TXT").Should().BeTrue();
        DataMigrationScreenRules.IsSourceFile("export.xls").Should().BeFalse();
        DataMigrationScreenRules.IsReadableSourceSize(0).Should().BeFalse();
        DataMigrationScreenRules.IsReadableSourceSize(DataMigrationScreenRules.MaxSourceFileBytes).Should().BeTrue();
        DataMigrationScreenRules.IsReadableSourceSize(DataMigrationScreenRules.MaxSourceFileBytes + 1).Should().BeFalse();

        var profile = new MigrationProfile { Name = "LABOWIN" };
        profile.For(DataMigrationKind.Patients).Sheet = "Patients";
        DataMigrationScreenRules.PreferredSheet(["Feuil1", "Patients"], profile, DataMigrationKind.Patients).Should().Be("Patients");
        DataMigrationScreenRules.PreferredSheet(["Feuil1"], profile, DataMigrationKind.Patients).Should().Be("Feuil1");
        DataMigrationScreenRules.PreferredSheet([], profile, DataMigrationKind.Patients).Should().BeNull();

        DataMigrationScreenRules.SourceLabel(new string('x', 600)).Length.Should().Be(MigrationBatch.SourceLabelMaxLength);
        DataMigrationScreenRules.RowsRead(Patients(), Text).Should().Be("Migration.RowsRead(3,4)");
        DataMigrationScreenRules.RowsRead(null, Text).Should().BeEmpty();
    }

    // ---- Columns -------------------------------------------------------------------------------

    [Fact]
    public void The_profile_s_matching_wins_where_its_columns_exist_and_a_field_it_left_out_stays_out()
    {
        var automatic = DataMigrationScreenRules.MatchColumns(DataMigrationKind.Patients, Patients(), null);
        automatic[MigrationSchema.LastName].Should().Be("nom");
        automatic[MigrationSchema.SourceId].Should().Be("id");

        var profile = new MigrationProfile();
        profile.For(DataMigrationKind.Patients).Columns = new(StringComparer.Ordinal)
        {
            [MigrationSchema.SourceId] = "",
            [MigrationSchema.LastName] = "NOM",
            [MigrationSchema.FirstName] = "absent"
        };

        var matched = DataMigrationScreenRules.MatchColumns(DataMigrationKind.Patients, Patients(), profile);
        matched.Should().NotContainKey(MigrationSchema.SourceId);
        matched[MigrationSchema.LastName].Should().Be("nom");
        matched[MigrationSchema.FirstName].Should().Be("prenom", "a saved column the source lacks falls back to the automatic match");

        DataMigrationScreenRules.SetColumn(matched, MigrationSchema.FirstName, null);
        matched.Should().NotContainKey(MigrationSchema.FirstName);
        DataMigrationScreenRules.IsMissing(MigrationSchema.Fields(DataMigrationKind.Patients)
                                                          .Single(f => f.Key == MigrationSchema.FirstName), matched)
            .Should().BeTrue();
        DataMigrationScreenRules.ColumnsMapped(DataMigrationKind.Patients, matched, Text)
            .Should().StartWith($"Migration.ColumnsMapped({matched.Count},");
    }

    [Fact]
    public void A_column_is_recognised_by_its_first_values_and_offered_after_not_mapped()
    {
        DataMigrationScreenRules.Sample(Patients(), "NOM").Should().Be("EL AMRANI · BENNANI");
        DataMigrationScreenRules.Sample(Patients(), "absent").Should().BeEmpty();
        DataMigrationScreenRules.ColumnChoices(Patients(), Text).Select(c => c.Value)
            .Should().Equal(DataMigrationScreenRules.NotMapped, "id", "nom", "prenom", "sexe");
        DataMigrationScreenRules.Preview(Patients()).Should().HaveCount(3);
    }

    // ---- Correspondences -----------------------------------------------------------------------

    [Fact]
    public void A_value_without_a_target_is_missing_but_a_unit_is_kept_as_written()
    {
        DataMigrationScreenRules.InitialTarget(new CorrespondenceRow(MigrationCodeList.Unit, "mg/dl", 4, null, false))
            .Should().Be(CorrespondenceTables.KeepAsIs);
        DataMigrationScreenRules.InitialTarget(new CorrespondenceRow(MigrationCodeList.Sex, "X", 1, null, false))
            .Should().Be(DataMigrationScreenRules.NotMapped);

        DataMigrationScreenRules.State(MigrationCodeList.Sex, "", false).Should().Be(CorrespondenceState.Missing);
        DataMigrationScreenRules.State(MigrationCodeList.Unit, "", false).Should().Be(CorrespondenceState.Settled);
        DataMigrationScreenRules.State(MigrationCodeList.Analysis, CorrespondenceTables.Ignore, true).Should().Be(CorrespondenceState.Ignored);
        DataMigrationScreenRules.State(MigrationCodeList.Sex, "Female", true).Should().Be(CorrespondenceState.Suggested);
        DataMigrationScreenRules.CorrespondencesCount(0, 0, Text).Should().Be("Migration.NoCodedValues");
        DataMigrationScreenRules.CorrespondencesCount(6, 2, Text).Should().Be("Migration.CorrespondencesCount(6,2)");
    }

    [Fact]
    public void The_targets_offered_depend_on_the_list_and_keep_an_unknown_current_one()
    {
        var targets = new Dictionary<MigrationCodeList, IReadOnlyList<CodeTarget>>
        {
            [MigrationCodeList.Sex] = [new("Female", "F"), new("Male", "M")],
            [MigrationCodeList.Analysis] = [new("GLY", "Glycémie")]
        };

        DataMigrationScreenRules.TargetChoices(MigrationCodeList.Sex, targets, null, Text).Select(c => c.Display)
            .Should().Equal("Migration.ChooseTarget", "Migration.Sex.Female", "Migration.Sex.Male");
        DataMigrationScreenRules.TargetChoices(MigrationCodeList.Analysis, targets, "OLD", Text).Select(c => c.Value)
            .Should().Equal("", "GLY", CorrespondenceTables.Ignore, "OLD");
        DataMigrationScreenRules.TargetChoices(MigrationCodeList.Unit, targets, null, Text).Select(c => c.Value)
            .Should().Equal("", CorrespondenceTables.KeepAsIs);

        var settled = DataMigrationScreenRules.Settled(new CorrespondenceTables(),
            [(MigrationCodeList.Sex, "M", "Male"), (MigrationCodeList.Sex, "X", null)]);
        settled.TargetOf(MigrationCodeList.Sex, "M").Should().Be("Male");
        settled.TargetOf(MigrationCodeList.Sex, "X").Should().BeNull();
    }

    // ---- Simulation and import -----------------------------------------------------------------

    [Fact]
    public void Nothing_is_simulated_without_a_source_and_an_old_application_and_nothing_imported_unsimulated_or_blocked()
    {
        DataMigrationScreenRules.RequestRefusal(null, "LABOWIN").Should().Be("Migration.ReadSourceFirst");
        DataMigrationScreenRules.RequestRefusal(Patients(), " ").Should().Be("Migration_SourceSystemRequired");
        DataMigrationScreenRules.RequestRefusal(Patients(), "LABOWIN").Should().BeNull();

        DataMigrationScreenRules.ImportRefusal(null, Text).Should().Be("Migration.SimulateFirst");
        DataMigrationScreenRules.ImportRefusal(Analysis([Blocking(), Blocking()], MigrationOutcome.Create), Text)
            .Should().Be("Migration_ImportBlocked(2)");
        DataMigrationScreenRules.ImportRefusal(Analysis(MigrationOutcome.Create), Text).Should().BeNull();

        var columns = new Dictionary<string, string> { [MigrationSchema.LastName] = "nom" };
        var request = DataMigrationScreenRules.Request(DataMigrationKind.Patients, " LABOWIN ", "patients.csv", Patients(),
                                                       columns, new CorrespondenceTables(), false);
        request.SourceSystem.Should().Be("LABOWIN");
        request.Columns.Should().NotBeSameAs(columns);
        request.LinkByNameAndBirthDate.Should().BeFalse();
    }

    [Fact]
    public void The_import_is_confirmed_with_the_simulation_s_counts_and_its_progress_and_outcome_said()
    {
        var analysis = Analysis(MigrationOutcome.Create, MigrationOutcome.Create, MigrationOutcome.Update, MigrationOutcome.Error);
        DataMigrationScreenRules.ConfirmImport(analysis, DataMigrationKind.Patients, Text)
            .Should().Be("Migration.ConfirmImport(2,1,0,1,MigrationKind.Patients)");

        var stopped = new MigrationSummary(Guid.NewGuid(), DataMigrationKind.Patients, 4, 2, 0, 0, 0, true, []);
        DataMigrationScreenRules.SummaryMessage(stopped, Text)
            .Should().Be("Migration.SummaryInterrupted(MigrationKind.Patients,2,0,0,0)");

        var progress = new MigrationProgress(1200, 5000, "Migration.StepWriting");
        DataMigrationScreenRules.Progress(progress, Text, CultureInfo.InvariantCulture).Should().Be("Migration.StepWriting 1,200 / 5,000");
        DataMigrationScreenRules.Thousandths(progress).Should().Be(240);
        DataMigrationScreenRules.Thousandths(new MigrationProgress(0, 0, "x")).Should().Be(0);
    }

    [Fact]
    public void The_status_line_leads_from_reading_to_simulating_to_importing()
    {
        DataMigrationScreenRules.Status(null, null, null, Text).Should().Be(("Migration.StatusStart", false));
        DataMigrationScreenRules.Status(Patients(), null, null, Text).Should().Be(("Migration.StatusSimulate", false));
        DataMigrationScreenRules.Status(Patients(), Analysis([Blocking()], MigrationOutcome.Create), null, Text)
            .Should().Be(("Migration_ImportBlocked(1)", true));
        DataMigrationScreenRules.Status(Patients(), Analysis(MigrationOutcome.Create), null, Text)
            .Should().Be(("Migration.StatusReady(1,0)", false));
    }

    // ---- Report --------------------------------------------------------------------------------

    [Fact]
    public void The_report_reads_by_row_the_most_serious_first_and_exports_as_a_french_excel_csv()
    {
        var anomalies = new List<MigrationAnomaly>
        {
            new(3, "LastName", MigrationSeverity.Warning, "Migration.Trimmed", ["Migration.Field.LastName"]),
            new(3, null, MigrationSeverity.Error, "Migration.Duplicate", [], "Migration.FixSource", [12.5m]),
            Blocking()
        };

        var lines = DataMigrationScreenRules.ReportLines(anomalies, Text);
        lines.Select(l => l.Row).Should().Equal("—", "3", "3");
        lines[1].Severity.Should().Be(MigrationSeverity.Error);
        lines[1].Field.Should().Be("—");
        lines[2].Problem.Should().Be("Migration.Trimmed(Migration.Field.LastName)");
        lines[0].Problem.Should().Be("Migration.NoCorrespondence(MigrationCodeList.Sex,X)");

        DataMigrationScreenRules.ReportCounts(null, Analysis([Blocking()], MigrationOutcome.Create), Text)
            .Should().Be(("Migration.SimulationCounts(1,0,0,0,1)", true));
        DataMigrationScreenRules.ReportCounts(null, null, Text).Should().Be(("Migration.NoSimulation", false));

        var csv = DataMigrationScreenRules.ReportCsv(anomalies, Text);
        csv.Take(3).Should().Equal(Encoding.UTF8.GetPreamble());
        var text = Encoding.UTF8.GetString(csv, 3, csv.Length - 3);
        text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(4);
        text.Should().StartWith("Migration.ColRow;");

        DataMigrationScreenRules.ReportFileName(DataMigrationKind.Patients, new DateTime(2026, 10, 25, 14, 30, 0))
            .Should().Be("rapport-anomalies-patients-20261025-1430.csv");
        DataMigrationScreenRules.Anomalies(null, null).Should().BeEmpty();
    }

    // ---- Profiles ------------------------------------------------------------------------------

    [Fact]
    public void A_profile_drawn_from_the_screen_keeps_every_field_and_never_the_password()
    {
        var columns = new Dictionary<string, string> { [MigrationSchema.LastName] = "nom" };
        var connection = new SourceConnectionSettings { Password = "s3cret" };

        var profile = DataMigrationScreenRules.ToProfile(null, "LABOWIN", DataMigrationKind.Patients, " LABOWIN ", true,
                                                         new CorrespondenceTables(), columns, true, "Feuil1", true, connection,
                                                         null, "SELECT * FROM patients");

        profile.SourceSystem.Should().Be("LABOWIN");
        profile.Connection!.Password.Should().BeNull();
        connection.Password.Should().Be("s3cret");
        var saved = profile.For(DataMigrationKind.Patients);
        saved.Columns.Should().HaveCount(MigrationSchema.Fields(DataMigrationKind.Patients).Count);
        saved.Columns[MigrationSchema.LastName].Should().Be("nom");
        saved.Columns[MigrationSchema.FirstName].Should().Be(DataMigrationScreenRules.NotMapped);
        saved.Sheet.Should().Be("Feuil1");
        saved.Query.Should().Be("SELECT * FROM patients");

        DataMigrationScreenRules.ToProfile(profile, "LABOWIN", DataMigrationKind.Prescribers, "LABOWIN", true,
                                           new CorrespondenceTables(), columns, false, null, true)
            .Should().BeSameAs(profile, "saving under the same name updates the profile in hand");

        DataMigrationScreenRules.SuggestedProfileName(null, " LABOWIN ").Should().Be("LABOWIN");
        DataMigrationScreenRules.SuggestedProfileName(null, " ").Should().BeNull();
        DataMigrationScreenRules.ProfileFileName(profile).Should().Be("profil-reprise-LABOWIN.json");
        DataMigrationScreenRules.IsReadableProfileSize(MigrationProfile.MaxFileBytes + 1).Should().BeFalse();
    }

    [Fact]
    public void A_store_without_a_protector_keeps_no_password_and_an_export_carries_none()
    {
        var folder = Path.Combine(Path.GetTempPath(), "maroclis-profiles-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MigrationProfileStore(folder);
            var profile = new MigrationProfile { Name = "  LABO/WIN  ", SourceSystem = "LABOWIN" };

            store.Save(profile, "s3cret", rememberPassword: true);

            store.Names().Should().Equal("LABO/WIN");
            store.Load("LABO/WIN")!.ProtectedPassword.Should().BeNull();

            var guarded = new MigrationProfileStore(folder, p => "sealed:" + p);
            guarded.Save(profile, "s3cret", rememberPassword: true);
            guarded.Load("LABO/WIN")!.ProtectedPassword.Should().Be("sealed:s3cret");

            MigrationProfileStore.Import(MigrationProfileStore.Export(guarded.Load("LABO/WIN")!))
                .ProtectedPassword.Should().BeNull();

            store.Delete("LABO/WIN");
            store.Names().Should().BeEmpty();
            FluentActions.Invoking(() => store.Save(new MigrationProfile { Name = " " }, null, false))
                .Should().Throw<ArgumentException>();
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    // ---- Templates, journal, old numbers, balances ---------------------------------------------

    [Fact]
    public void Every_template_comes_in_excel_and_in_csv()
    {
        var templates = DataMigrationScreenRules.AllTemplates(_ => [1, 2]);

        templates.Should().HaveCount(Enum.GetValues<DataMigrationKind>().Length * 2);
        templates.Select(t => Path.GetExtension(t.FileName)).Distinct().Should().BeEquivalentTo(".xlsx", ".csv");
        templates.First(t => t.FileName.EndsWith(".xlsx", StringComparison.Ordinal)).Content().Should().Equal(1, 2);
        templates.First(t => t.FileName.EndsWith(".csv", StringComparison.Ordinal)).Content().Should().NotBeEmpty();
    }

    [Fact]
    public void Only_the_newest_finished_run_that_created_something_is_undone_and_the_question_names_it()
    {
        var migration = new LicenceTestKit.User(true, Permissions.Administration.Migration);
        var running = Batch(completed: false);
        var last = Batch();

        DataMigrationScreenRules.CanUndo(migration, [running, last]).Should().BeTrue();
        DataMigrationScreenRules.CanUndo(migration, [Batch(undone: true), Batch(created: 0)]).Should().BeFalse();
        DataMigrationScreenRules.CanUndo(new LicenceTestKit.User(true), [last]).Should().BeFalse();
        DataMigrationScreenRules.LastUndoable([running, last, Batch()]).Should().Be(last);
        DataMigrationScreenRules.ConfirmUndo(last, Text, French).Should().StartWith("Migration.ConfirmUndo(MigrationKind.Patients,20/10/2026").And.EndWith(",3)");

        DataMigrationScreenRules.BatchState(Batch(undone: true)).Should().Be("Migration.BatchUndone");
        DataMigrationScreenRules.BatchState(Batch(interrupted: true)).Should().Be("Migration.BatchInterrupted");
        DataMigrationScreenRules.BatchState(running).Should().Be("Migration.BatchRunning");
        DataMigrationScreenRules.BatchState(last).Should().Be("Migration.BatchDone");
    }

    [Fact]
    public void An_old_number_lists_its_patient_once_per_result_and_the_balances_add_up()
    {
        var result = new HistoricalResultRow(Guid.NewGuid(), new DateTimeOffset(2019, 3, 4, 12, 0, 0, TimeSpan.Zero), "GLY",
                                             "Glycémie", "1,02", "g/L", "0,70–1,10", "A-77");
        var lines = DataMigrationScreenRules.OldNumberLines(
            [new OldNumberMatch(Guid.NewGuid(), "2026-0001", "EL AMRANI Karim", new DateOnly(1980, 5, 1), "D-12", [result, result]),
             new OldNumberMatch(Guid.NewGuid(), "2026-0002", "BENNANI Salma", null, null, [])], French);

        lines.Should().HaveCount(3);
        lines[0].Should().Be(new OldNumberLine("2026-0001", "EL AMRANI Karim", "01/05/1980", "D-12", "04/03/2019",
                                               "GLY — Glycémie", "1,02 g/L", "0,70–1,10", "A-77"));
        lines[2].Date.Should().BeEmpty();

        var balance = new OpeningBalanceRow(Guid.NewGuid(), new DateOnly(2026, 1, 1), null, null, null, null, 1000m, null, null, null);
        DataMigrationScreenRules.BalancesTotal([balance, balance with { Amount = 250m }], Text, CultureInfo.InvariantCulture)
            .Should().Be("Migration.BalancesTotal(2,1,250.00)");
    }
}
