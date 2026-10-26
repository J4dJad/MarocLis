using System.Globalization;
using System.Text;
using FluentAssertions;
using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Dossiers du personnel », « Habilitations » and « Échéances RH », moved out of the
/// workstation's views and dialogs, now shared with the web.
/// </summary>
public sealed class HumanResourcesScreenRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 18);

    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    private static Func<string, bool> Rights(params string[] rights) => rights.Contains;

    private static EmployeeRow Person(string number = "M0002", string name = "AÏT OUAHMANE Youssef", DateOnly? exit = null,
                                      DateOnly? contractEnd = null, ContractType contract = ContractType.Permanent,
                                      string? title = "Technicien de laboratoire", string? department = "Hématologie",
                                      string? user = "y.aitouahmane") =>
        new(Guid.NewGuid(), number, name, EmployeeJob.Technician, title, department, contract, new DateOnly(2021, 3, 1),
            contractEnd, "0661 00 00 00", user, exit, exit is null ? null : ExitReason.Resignation);

    // ---- Dossiers du personnel ---------------------------------------------------------------------

    [Fact]
    public void The_search_reads_the_name_the_number_and_the_job_title()
    {
        IReadOnlyList<EmployeeRow> rows = [Person(), Person("M0003", "BOUZID Hanane", title: "Secrétaire d'accueil")];

        PersonnelScreenRules.Shown(rows, "  ").Should().HaveCount(2);
        PersonnelScreenRules.Shown(rows, "bouzid").Should().ContainSingle().Which.Number.Should().Be("M0003");
        PersonnelScreenRules.Shown(rows, "m0002").Should().ContainSingle().Which.Number.Should().Be("M0002");
        PersonnelScreenRules.Shown(rows, "accueil").Should().ContainSingle().Which.Number.Should().Be("M0003");
    }

    [Fact]
    public void A_departure_to_come_is_still_on_the_staff_one_taken_effect_is_muted_and_a_contract_over_alarms()
    {
        var leaving = Person(exit: Today.AddDays(18));
        var left = Person(exit: new DateOnly(2026, 6, 30));
        var over = Person(contract: ContractType.FixedTerm, contractEnd: Today.AddDays(-12));

        PersonnelScreenRules.Ink(Person(), Today).Should().Be(PersonnelInk.Plain);
        PersonnelScreenRules.Ink(leaving, Today).Should().Be(PersonnelInk.Plain, "the notice is being served");
        PersonnelScreenRules.Ink(left, Today).Should().Be(PersonnelInk.Departed);
        PersonnelScreenRules.Ink(over, Today).Should().Be(PersonnelInk.ContractOver);

        PersonnelScreenRules.Pill(leaving, Today, Text).Should().Be(new HrPill("Personnel.LeavingOn(05/11/2026)", "Warning"));
        PersonnelScreenRules.Pill(left, Today, Text).Should().Be(new HrPill("Personnel.ExitedOn(30/06/2026)", "Muted"));
        PersonnelScreenRules.Pill(over, Today, Text).Should().Be(new HrPill("Personnel.ContractEnd 06/10/2026", "Critical"));
        PersonnelScreenRules.Pill(Person(), Today, Text).Should().Be(new HrPill("ContractType.Permanent", "Ok"));

        PersonnelScreenRules.Departure(Person(), Today, Text).Should().BeEmpty();
        PersonnelScreenRules.NameLine(left, Today, Text).Should().Be("AÏT OUAHMANE Youssef  (Personnel.ExitedOn(30/06/2026))");
        PersonnelScreenRules.Count([Person(), leaving, left], 3, Today, Text).Should().Be("QualityHr.Personnel.Count(2,3)");
    }

    [Fact]
    public void The_card_states_no_personal_data_and_the_subtitle_skips_what_is_missing()
    {
        var facts = PersonnelScreenRules.Facts(Person(user: null), Text);

        facts.Select(f => f.Label).Should().Equal("Personnel.Number", "Personnel.Job", "Personnel.ContractType",
            "Personnel.HireDate", "Personnel.ContractEnd", "Personnel.LinkedUser");
        facts.Select(f => f.Value).Should().Equal("M0002", "EmployeeJob.Technician", "ContractType.Permanent", "01/03/2021", "—", "—");
        PersonnelScreenRules.Subtitle(Person()).Should().Be("Technicien de laboratoire · Hématologie");
        PersonnelScreenRules.Subtitle(Person(title: null)).Should().Be("Hématologie");
    }

    [Fact]
    public void The_actions_follow_the_rights_and_the_departure_step_follows_the_exit_date()
    {
        var manage = Rights(Permissions.HumanResources.View, Permissions.HumanResources.Manage);
        var all = Rights(Permissions.HumanResources.View, Permissions.HumanResources.Manage, Permissions.HumanResources.Salary);
        var view = Rights(Permissions.HumanResources.View);

        var none = PersonnelScreenRules.Actions(null, Today, all);
        none.New.Should().BeTrue();
        none.Edit.Should().BeFalse();
        none.Documents.Should().BeFalse();

        var onStaff = PersonnelScreenRules.Actions(Person(), Today, manage);
        onStaff.Should().Match<PersonnelActions>(a => a.Edit && a.Documents && a.Exit && a.EmploymentLetter && !a.SalaryLetter
                                                       && !a.Certificate && !a.Settlement && a.ExitStep == ExitStep.Record);
        onStaff.ExitKey.Should().Be("Personnel.RecordExit");

        var leaving = PersonnelScreenRules.Actions(Person(exit: Today), Today, all);
        leaving.ExitStep.Should().Be(ExitStep.Change, "the exit day is still worked");
        leaving.Certificate.Should().BeTrue();
        leaving.Settlement.Should().BeTrue();
        leaving.EmploymentLetter.Should().BeTrue();

        var left = PersonnelScreenRules.Actions(Person(exit: Today.AddDays(-1)), Today, manage);
        left.ExitStep.Should().Be(ExitStep.Rehire);
        left.ExitKey.Should().Be("Personnel.Rehire");
        left.EmploymentLetter.Should().BeFalse();
        left.Settlement.Should().BeFalse("the settlement needs the salary right");

        var reader = PersonnelScreenRules.Actions(Person(), Today, view);
        reader.Should().Match<PersonnelActions>(a => !a.New && !a.Edit && a.Documents && !a.Exit && !a.Letters);

        var payroll = PersonnelScreenRules.Actions(Person(), Today, Rights(Permissions.HumanResources.Salary));
        payroll.Letters.Should().BeTrue();
        payroll.SalaryLetter.Should().BeTrue();
        payroll.EmploymentLetter.Should().BeFalse();
    }

    [Fact]
    public void The_account_left_by_a_departure_is_said_in_its_tone_and_a_letter_has_its_file_name()
    {
        var on = new DateOnly(2026, 11, 5);
        PersonnelScreenRules.AccountOutcome(null, Text).Should().BeNull();
        PersonnelScreenRules.AccountOutcome(new DepartedAccountOutcome("o.benjelloun", DepartedAccountState.Scheduled, on), Text)
            .Should().Be(("Personnel.AccountDeactivationScheduled(o.benjelloun,06/11/2026)", "normal"));
        PersonnelScreenRules.AccountOutcome(new DepartedAccountOutcome("admin", DepartedAccountState.KeptLastAdministrator, on), Text)!
            .Value.Tone.Should().Be("critical");
        PersonnelScreenRules.SettlementSaved(SettlementPayroll.Closed, Text).Should().Be(("Settlement.Saved.Closed", "abnormal"));
        PersonnelScreenRules.CancelExitPrompt(Person(exit: on), Text).Should().Be("Personnel.CancelExitPrompt(AÏT OUAHMANE Youssef,05/11/2026)");
        PersonnelScreenRules.Who(Person()).Should().Be("M0002 AÏT OUAHMANE Youssef");
        PersonnelScreenRules.LetterFileName(Person(), HrDocumentKind.WorkCertificate).Should().StartWith("M0002-certificat-travail").And.EndWith(".pdf");
    }

    // ---- Le dossier ----------------------------------------------------------------------------------

    [Fact]
    public void The_file_opens_its_pay_only_with_the_salary_right_and_its_documents_to_any_reader()
    {
        EmployeeFileRules.Tabs(Rights(Permissions.HumanResources.View)).Should().Equal(EmployeeFileTab.Documents);
        EmployeeFileRules.Tabs(Rights(Permissions.HumanResources.View, Permissions.HumanResources.Manage)).Should().Equal(
            EmployeeFileTab.Identity, EmployeeFileTab.Position, EmployeeFileTab.Contract, EmployeeFileTab.Documents);
        EmployeeFileRules.Tabs(Rights(Permissions.HumanResources.View, Permissions.HumanResources.Manage, Permissions.HumanResources.Salary))
            .Should().Contain(EmployeeFileTab.Pay);

        EmployeeFileRules.TabOf(EmployeeFileRules.RibField).Should().Be(EmployeeFileTab.Pay);
        EmployeeFileRules.TabOf(EmployeeFileRules.WeeklyHoursField).Should().Be(EmployeeFileTab.Contract);
        EmployeeFileRules.TabOf(EmployeeFileRules.UserField).Should().Be(EmployeeFileTab.Position);
        EmployeeFileRules.TabOf(EmployeeFileRules.CinField).Should().Be(EmployeeFileTab.Identity);
        EmployeeFileRules.TabKey(EmployeeFileTab.Pay).Should().Be("Personnel.Pay");
    }

    [Fact]
    public void A_new_file_is_hired_today_follows_its_job_and_a_dated_contract_gets_an_end()
    {
        var form = EmployeeFileRules.FormFor(null, Today);
        form.HireDate.Should().Be(Today);
        form.WeeklyHours.Should().Be(44m);

        form.Job = EmployeeJob.Cleaner;
        EmployeeFileRules.JobChanged(form, isNew: true);
        form.Exposed.Should().Be(Employee.IsExposedByDefault(EmployeeJob.Cleaner));

        form.Exposed = false;
        EmployeeFileRules.JobChanged(form, isNew: false);
        form.Exposed.Should().BeFalse("an existing file keeps what HR decided");

        form.ContractType = ContractType.FixedTerm;
        EmployeeFileRules.ContractChanged(form, Today);
        form.ContractEndDate.Should().Be(Today);
        EmployeeFileRules.ShowsEndNotice(ContractType.FixedTerm).Should().BeTrue();
        EmployeeFileRules.ShowsEndNotice(ContractType.Permanent).Should().BeFalse();
    }

    [Fact]
    public void A_cin_or_a_cnss_number_written_wrong_is_refused_beside_its_box()
    {
        var form = new EmployeeForm { LastName = "Bouzid", FirstName = "Hanane", HireDate = Today, Cin = "4821B" };
        EmployeeFileRules.Check(form, null, canSeeSalary: true).Refusal.Should()
            .Be(new MarocLIS.Application.Input.FieldRefusal(EmployeeFileRules.CinField, "Employee_CinInvalid", "4821B"));

        form.Cin = "be902117";
        form.Cnss = "123 456 7890";
        EmployeeFileRules.Check(form, null, true).Refusal!.Field.Should().Be(EmployeeFileRules.CnssField);

        EmployeeFileRules.IsCnssNumber("184 552 907").Should().BeTrue();
        EmployeeFileRules.IsCnssNumber("184-552.907").Should().BeTrue();
        EmployeeFileRules.IsCnssNumber("18455290A").Should().BeFalse();
        EmployeeFileRules.IsCnssNumber("--").Should().BeFalse();

        form.Cnss = "184 552 907";
        EmployeeFileRules.Check(form, null, true).Refusal.Should().BeNull();

        var blank = new EmployeeForm { LastName = " ", FirstName = "Hanane", HireDate = Today };
        EmployeeFileRules.Check(blank, null, true).Refusal!.Field.Should().Be(EmployeeFileRules.LastNameField);
        var hours = new EmployeeForm { LastName = "B", FirstName = "H", HireDate = Today, WeeklyHours = 61m };
        EmployeeFileRules.Check(hours, null, true).Refusal!.Field.Should().Be(EmployeeFileRules.WeeklyHoursField);
    }

    [Fact]
    public void The_salary_and_the_bank_travel_only_with_the_salary_right()
    {
        var form = new EmployeeForm
        {
            LastName = " Bouzid ", FirstName = "Hanane", HireDate = Today, Salary = "abc", Rib = "123", Bank = "BMCE", City = "  "
        };

        EmployeeFileRules.Check(form, null, canSeeSalary: true).Refusal!.Field.Should().Be(EmployeeFileRules.SalaryField);

        var (refusal, request) = EmployeeFileRules.Check(form, Guid.Empty, canSeeSalary: false);
        refusal.Should().BeNull("what cannot be seen is not checked");
        request!.BaseSalary.Should().BeNull();
        request.Rib.Should().BeNull();
        request.BankName.Should().BeNull();
        request.LastName.Should().Be("Bouzid");
        request.City.Should().BeNull();

        form.Salary = "7 400,50";
        EmployeeFileRules.Check(form, null, true).Refusal!.Field.Should().Be(EmployeeFileRules.RibField);
    }

    [Fact]
    public void A_refusal_of_the_service_goes_beside_its_box_with_its_date_written_as_the_screens_write_dates()
    {
        EmployeeFileRules.RefusalOf(Error.Validation("Employee_CinAlreadyUsed"))!.Field.Should().Be(EmployeeFileRules.CinField);
        EmployeeFileRules.RefusalOf(Error.Validation("Employee_Unknown")).Should().BeNull();
        EmployeeFileRules.RefusalOf(Error.Validation("Employee_ContractEndBeforeHire", new DateOnly(2026, 3, 1)))!
            .Typed.Should().Be("01/03/2026");
    }

    // ---- Les pièces --------------------------------------------------------------------------------

    private static CredentialRow Paper(CredentialKind kind = CredentialKind.HepatitisBVaccination, DateOnly? expires = null,
                                       MedicalFitness? fitness = null, string? note = null, string? file = null) =>
        new(Guid.NewGuid(), kind, "Vaccination hépatite B", null, null, new DateOnly(2018, 3, 2), expires, fitness, note, file);

    [Fact]
    public void An_expired_paper_or_an_unfit_visit_alarms_and_the_note_starts_with_the_fitness()
    {
        CredentialRules.IsAlarm(Paper(expires: Today.AddDays(-1)), Today).Should().BeTrue();
        CredentialRules.IsAlarm(Paper(expires: Today), Today).Should().BeFalse();
        CredentialRules.IsAlarm(Paper(CredentialKind.MedicalVisit, fitness: MedicalFitness.Unfit), Today).Should().BeTrue();

        CredentialRules.Note(Paper(CredentialKind.MedicalVisit, fitness: MedicalFitness.Fit, note: "revoir en mars"), Text)
            .Should().Be("MedicalFitness.Fit · revoir en mars");
        CredentialRules.Note(Paper(), Text).Should().BeEmpty();
        CredentialRules.Scan(Paper(), Text).Should().Be("—");
        CredentialRules.Scan(Paper(file: "vhb.pdf"), Text).Should().Be("Credential.HasScan");
    }

    [Fact]
    public void The_papers_are_changed_by_whoever_edits_files_and_a_scan_opened_by_whoever_reads_them()
    {
        var reader = CredentialRules.Actions(Paper(file: "vhb.pdf"), busy: false, Rights(Permissions.HumanResources.View));
        reader.Should().Be(new CredentialActions(false, false, false, true, false));

        var editor = CredentialRules.Actions(Paper(), false, Rights(Permissions.HumanResources.View, Permissions.HumanResources.Manage));
        editor.Should().Be(new CredentialActions(true, true, true, false, true));

        CredentialRules.Actions(null, false, Rights(Permissions.HumanResources.Manage)).Should().Be(new CredentialActions(true, false, false, false, false));
        CredentialRules.Actions(Paper(), true, Rights(Permissions.HumanResources.Manage)).Add.Should().BeFalse();
    }

    [Fact]
    public void A_paper_proposes_its_kind_as_title_without_overwriting_one_typed_and_is_checked()
    {
        var form = CredentialRules.FormFor(null, Today, Text);
        form.ObtainedOn.Should().Be(Today);
        form.Title.Should().BeEmpty("a diploma has its own title");

        form.Kind = CredentialKind.HepatitisBVaccination;
        CredentialRules.KindChanged(form, Text);
        form.Title.Should().Be("CredentialKind.HepatitisBVaccination");

        form.Kind = CredentialKind.MedicalVisit;
        CredentialRules.KindChanged(form, Text);
        form.Title.Should().Be("CredentialKind.MedicalVisit", "the title proposed follows the kind");

        form.Title = "Visite d'embauche";
        form.Kind = CredentialKind.SafetyTraining;
        CredentialRules.KindChanged(form, Text);
        form.Title.Should().Be("Visite d'embauche");

        form.ExpiresOn = Today.AddDays(-1);
        CredentialRules.Check(form).Refusal!.Field.Should().Be(CredentialRules.ExpiresField);
        form.ExpiresOn = null;
        form.Kind = CredentialKind.Diploma;
        form.Fitness = MedicalFitness.Unfit;
        CredentialRules.Check(form).Request!.Fitness.Should().BeNull("only a medical visit carries a fitness");
        CredentialRules.AsksFitness(CredentialKind.MedicalVisit).Should().BeTrue();

        form.Title = "  ";
        CredentialRules.Check(form).Refusal!.Key.Should().Be("Credential_TitleRequired");
    }

    [Fact]
    public void A_scan_is_a_pdf_or_a_picture_of_a_size_the_database_keeps()
    {
        CredentialRules.CheckScan("diplome.pdf", 0)!.Key.Should().Be("Credential_FileEmpty");
        CredentialRules.CheckScan("diplome.docx", 10)!.Key.Should().Be("Credential_FileType");
        CredentialRules.CheckScan("diplome.pdf", CredentialFile.MaximumBytes + 1)!.Key.Should().Be("Credential_FileTooLarge");
        CredentialRules.CheckScan("diplome.JPG", 2048).Should().BeNull();
        CredentialRules.RefusalOf(Error.Validation("Credential_FitnessRequired"))!.Field.Should().Be(CredentialRules.FitnessField);
    }

    // ---- La sortie ---------------------------------------------------------------------------------

    [Fact]
    public void A_departure_says_the_account_it_stops_and_a_rehire_starts_after_it()
    {
        DepartureRules.FormFor(Today).Should().Match<ExitForm>(f => f.Date == Today && f.Reason == ExitReason.Resignation);
        DepartureRules.AccountNotice(null, Text).Should().BeNull();
        DepartureRules.AccountNotice("o.benjelloun", Text).Should().Be("Personnel.ExitDeactivatesAccount(o.benjelloun)");
        DepartureRules.Comment(new ExitForm { Comment = "  " }).Should().BeNull();
        DepartureRules.TitleKey(correcting: true).Should().Be("Personnel.CorrectExit");

        var exit = new DateOnly(2026, 6, 30);
        DepartureRules.RehireDefault(exit, Today).Should().Be(Today);
        DepartureRules.RehireDefault(Today, Today).Should().Be(Today.AddDays(1));
        DepartureRules.CheckRehire(exit, exit)!.Typed.Should().Be("30/06/2026");
        DepartureRules.CheckRehire(exit.AddDays(1), exit).Should().BeNull();

        DepartureRules.RefusalOf(Error.Validation("Employee_ExitBeforeHire", new DateOnly(2021, 3, 1)))!.Field.Should().Be(DepartureRules.DateField);
        DepartureRules.RefusalOf(Error.Validation("Employee_LeaveAfterExit", Today, Today, Today))
            .Should().BeNull("a message naming three dates is said whole under the dialog");
    }

    // ---- Habilitations -----------------------------------------------------------------------------

    private static readonly Guid Analyser1 = Guid.NewGuid(), Analyser2 = Guid.NewGuid(), Bench = Guid.NewGuid();

    private static HabilitationRow Held(EmployeeRow who, HabilitationActivity activity, Guid? subject = null, string? label = null,
                                        HabilitationLevel level = HabilitationLevel.Authorized, DateOnly? review = null) =>
        new(Guid.NewGuid(), who.EmployeeId, activity, subject, label, level, new DateOnly(2025, 10, 1), "Dr Lahlou", review, "Évaluation");

    [Fact]
    public void The_matrix_has_a_column_per_activity_held_the_collections_always_and_its_subjects_share_the_cell()
    {
        var tech = Person();
        var matrix = new HabilitationMatrix([tech],
        [
            Held(tech, HabilitationActivity.InstrumentOperation, Analyser2, "Sysmex XN-550"),
            Held(tech, HabilitationActivity.InstrumentOperation, Analyser1, "Cobas c311"),
            Held(tech, HabilitationActivity.SampleReception)
        ]);

        var columns = HabilitationScreenRules.Columns(matrix);
        columns.Select(c => c.Activity).Should().Contain([HabilitationActivity.AdultCollection, HabilitationActivity.PaediatricCollection]);
        columns.Count(c => c.Activity == HabilitationActivity.InstrumentOperation).Should().Be(2);

        var activities = HabilitationScreenRules.Activities(matrix);
        activities.Should().OnlyHaveUniqueItems().And.HaveCount(columns.Count - 1);
        activities.Should().BeInAscendingOrder();

        HabilitationScreenRules.HeldIn(matrix, tech.EmployeeId, HabilitationActivity.InstrumentOperation)
            .Select(h => h.SubjectLabel).Should().Equal("Cobas c311", "Sysmex XN-550");
        HabilitationScreenRules.HeldIn(matrix, Guid.NewGuid(), HabilitationActivity.InstrumentOperation).Should().BeEmpty();

        var held = matrix.Habilitations[0];
        HabilitationScreenRules.ColumnOf(HabilitationActivity.InstrumentOperation, held)
            .Should().Be(new HabilitationColumn(HabilitationActivity.InstrumentOperation, Analyser2, "Sysmex XN-550"));
        HabilitationScreenRules.ColumnOf(HabilitationActivity.AdultCollection, held)
            .Should().Be(new HabilitationColumn(HabilitationActivity.AdultCollection, null, null));
        HabilitationScreenRules.Held(matrix, tech.EmployeeId, new HabilitationColumn(HabilitationActivity.InstrumentOperation, Analyser1, "x"))
            !.SubjectLabel.Should().Be("Cobas c311");
        HabilitationScreenRules.HeaderText(new HabilitationColumn(HabilitationActivity.InstrumentOperation, Analyser1, "Cobas c311"), Text)
            .Should().Be("HabilitationActivity.InstrumentOperation\nCobas c311");
    }

    [Fact]
    public void A_review_overdue_leads_then_a_person_in_training_and_the_count_says_the_overdue()
    {
        var tech = Person();
        var overdue = Held(tech, HabilitationActivity.ManualTechnique, Bench, "Hématologie", review: Today.AddDays(-5));
        var training = Held(tech, HabilitationActivity.AdultCollection, level: HabilitationLevel.InTraining, review: Today.AddDays(50));
        var matrix = new HabilitationMatrix([tech], [overdue, training]);

        HabilitationScreenRules.Ink(null, Today).Should().Be(HabilitationInk.None);
        HabilitationScreenRules.Ink(overdue, Today).Should().Be(HabilitationInk.ReviewOverdue);
        HabilitationScreenRules.Ink(training, Today).Should().Be(HabilitationInk.InTraining);
        HabilitationScreenRules.Ink(training with { ReviewDueOn = Today.AddDays(-1) }, Today).Should().Be(HabilitationInk.ReviewOverdue);
        HabilitationScreenRules.Tone(HabilitationInk.ReviewOverdue).Should().Be("Critical");
        HabilitationScreenRules.Tone(HabilitationInk.InTraining).Should().Be("Warning");
        HabilitationScreenRules.Tone(HabilitationInk.Held).Should().Be("Ok");

        HabilitationScreenRules.Count(matrix, Today, Text).Should().Be("QualityHr.Habilitations.Count(1,2,1)");
        HabilitationScreenRules.ShortCell(overdue, Text).Should().Be("HabilitationLevelShort.Authorized · 10/26");
        HabilitationScreenRules.ReviewDue(overdue).Should().Be("13/10/2026");
        HabilitationScreenRules.Detail(matrix, overdue, Text)
            .Should().Be("Habilitation.Detail(HabilitationLevel.Authorized,01/10/2025,Dr Lahlou,13/10/2026,Évaluation)");
        HabilitationScreenRules.Detail(new HabilitationMatrix([tech], []), null, Text).Should().Be("Habilitation.Empty");
        HabilitationScreenRules.WithdrawPrompt(overdue, tech, Text)
            .Should().Be("Habilitation.WithdrawPrompt(HabilitationActivity.ManualTechnique,AÏT OUAHMANE Youssef)");
    }

    [Fact]
    public void Granting_needs_the_habilitation_right_and_a_person_withdrawing_a_habilitation_held()
    {
        var tech = Person();
        var held = Held(tech, HabilitationActivity.SampleReception);
        var matrix = new HabilitationMatrix([tech], [held]);
        var may = Rights(Permissions.HumanResources.ManageHabilitations);

        HabilitationScreenRules.Actions(matrix, tech, held, may).Should().Be((true, true, true));
        HabilitationScreenRules.Actions(matrix, tech, null, may).Should().Be((true, false, true));
        HabilitationScreenRules.Actions(matrix, tech, held, Rights(Permissions.HumanResources.Manage)).Should().Be((false, false, true));
        HabilitationScreenRules.Actions(new HabilitationMatrix([], []), null, null, may).Should().Be((false, false, false));
    }

    [Fact]
    public void A_grant_opens_on_its_column_reviewed_in_a_year_and_names_a_subject_where_its_activity_needs_one()
    {
        var lookups = new PersonnelLookups(
            [new HabilitationSubject(Bench, "Hématologie", false)],
            [new HabilitationSubject(Analyser1, "Cobas c311", true), new HabilitationSubject(Analyser2, "Sysmex XN-550", true)],
            []);

        var form = HabilitationScreenRules.FormFor(null, lookups, Today);
        form.Activity.Should().Be(HabilitationActivity.AdultCollection);
        form.SubjectId.Should().BeNull();
        form.ReviewDueOn.Should().Be(Today.AddYears(1));

        form = HabilitationScreenRules.FormFor(new HabilitationColumn(HabilitationActivity.InstrumentOperation, Analyser2, "Sysmex XN-550"), lookups, Today);
        form.SubjectId.Should().Be(Analyser2);

        form.Activity = HabilitationActivity.ManualTechnique;
        HabilitationScreenRules.ActivityChanged(form, lookups);
        form.SubjectId.Should().Be(Bench, "an analyser is no discipline");
        HabilitationScreenRules.Subjects(HabilitationActivity.InstrumentOperation, lookups).Should().HaveCount(2);
        HabilitationScreenRules.Subjects(HabilitationActivity.SampleReception, lookups).Should().BeEmpty();

        form.SubjectId = null;
        HabilitationScreenRules.Check(form, Today).Refusal!.Field.Should().Be(HabilitationScreenRules.SubjectField);
        form.SubjectId = Bench;
        form.GrantedOn = Today.AddDays(1);
        HabilitationScreenRules.Check(form, Today).Refusal!.Field.Should().Be(HabilitationScreenRules.GrantedOnField);
        form.GrantedOn = Today;
        form.ReviewDueOn = Today.AddDays(-1);
        HabilitationScreenRules.Check(form, Today).Refusal!.Field.Should().Be(HabilitationScreenRules.ReviewField);
        form.ReviewDueOn = null;
        form.Evidence = "  Lecture de 30 frottis ";
        HabilitationScreenRules.Check(form, Today).Grant.Should().Be(
            new HabilitationGrant(HabilitationActivity.ManualTechnique, Bench, HabilitationLevel.Authorized, Today, null, "Lecture de 30 frottis"));

        HabilitationScreenRules.RefusalOf(Error.Validation("Habilitation_BiologistSignatureRequired"))!.Field.Should().Be(HabilitationScreenRules.LevelField);
    }

    [Fact]
    public void The_export_is_one_line_per_habilitation_with_its_mark_and_formulas_neutralised()
    {
        var tech = Person(name: "=CMD|' /C calc'!A0");
        var matrix = new HabilitationMatrix([tech], [Held(tech, HabilitationActivity.SampleReception, review: Today)]);

        var bytes = HabilitationScreenRules.ExportCsv(matrix, Text);
        bytes.Take(3).Should().Equal(Encoding.UTF8.GetPreamble());
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines[1].Should().StartWith("M0002;").And.NotContain(";=CMD").And.Contain("18/10/2026");
        HabilitationScreenRules.ExportFileName(Today).Should().Be("habilitations-2026-10-18.csv");
    }

    // ---- Échéances RH ------------------------------------------------------------------------------

    private static DeadlineRow Due(DeadlineKind kind, DateOnly? on, bool overdue = false, HabilitationActivity? activity = null,
                                   string? subject = null) =>
        new(Guid.NewGuid(), "M0007", "EL OUAZZANI TOUHAMI Abdelilah", EmployeeJob.Technician, kind, subject, activity, on, overdue);

    [Fact]
    public void A_deadline_reads_missing_overdue_today_or_in_days_written_in_full()
    {
        PersonnelDeadlineScreenRules.State(Due(DeadlineKind.MissingMedicalVisit, null), Today, Text).Should().Be("Deadline.Missing");
        PersonnelDeadlineScreenRules.State(Due(DeadlineKind.ContractEnd, Today.AddDays(-12), true), Today, Text).Should().Be("Deadline.Overdue");
        PersonnelDeadlineScreenRules.State(Due(DeadlineKind.ContractEnd, Today), Today, Text).Should().Be("Deadline.Today");
        PersonnelDeadlineScreenRules.State(Due(DeadlineKind.ContractEnd, Today.AddDays(12)), Today, Text).Should().Be("Deadline.InDaysFull(12)");

        PersonnelDeadlineScreenRules.Ink(Due(DeadlineKind.MissingMedicalVisit, null), Today).Should().Be(DeadlineInk.Late);
        PersonnelDeadlineScreenRules.Ink(Due(DeadlineKind.ContractEnd, Today.AddDays(30)), Today).Should().Be(DeadlineInk.Soon);
        PersonnelDeadlineScreenRules.Ink(Due(DeadlineKind.ContractEnd, Today.AddDays(31)), Today).Should().Be(DeadlineInk.Plain);
        PersonnelDeadlineScreenRules.Tone(Due(DeadlineKind.ContractEnd, Today.AddDays(31)), Today).Should().Be("Info");
        PersonnelDeadlineScreenRules.Due(Due(DeadlineKind.MissingHepatitisB, null)).Should().Be("—");
    }

    [Fact]
    public void A_habilitation_review_names_its_activity_and_subject_and_the_horizon_is_one_offered()
    {
        PersonnelDeadlineScreenRules.Subject(Due(DeadlineKind.HabilitationReview, Today, activity: HabilitationActivity.InstrumentOperation,
            subject: "Cobas c311"), Text).Should().Be("HabilitationActivity.InstrumentOperation — Cobas c311");
        PersonnelDeadlineScreenRules.Subject(Due(DeadlineKind.CredentialExpiry, Today, subject: "Visite périodique"), Text)
            .Should().Be("Visite périodique");
        PersonnelDeadlineScreenRules.What(Due(DeadlineKind.TrialPeriodEnd, Today), Text).Should().Be("DeadlineKind.TrialPeriodEnd");
        PersonnelDeadlineScreenRules.Count([Due(DeadlineKind.ContractEnd, Today, true), Due(DeadlineKind.ContractEnd, Today)], Text)
            .Should().Be("QualityHr.Deadlines.Count(2,1)");

        PersonnelDeadlineScreenRules.Horizon(90).Should().Be(90);
        PersonnelDeadlineScreenRules.Horizon(45).Should().Be(PersonnelDeadlineScreenRules.DefaultHorizon);
        PersonnelDeadlineScreenRules.HorizonCaption(30, Text).Should().Be("Deadline.Horizon(30)");

        var row = Due(DeadlineKind.ContractEnd, Today);
        PersonnelDeadlineScreenRules.Actions(row, Rights(Permissions.HumanResources.View)).Should().Be((true, false));
        PersonnelDeadlineScreenRules.Actions(row, Rights(Permissions.HumanResources.View, Permissions.HumanResources.Manage)).Should().Be((true, true));
        PersonnelDeadlineScreenRules.Actions(null, Rights(Permissions.HumanResources.Manage)).Should().Be((false, false));
        PersonnelDeadlineScreenRules.KeyOf(row).Should().Be(PersonnelDeadlineScreenRules.KeyOf(row with { DueOn = Today.AddDays(3) }));
    }

    // ---- Solde de tout compte ---------------------------------------------------------------------

    private static SettlementDetail Settlement(DateOnly? signed = null, SettlementPayroll payroll = SettlementPayroll.Applied,
                                               bool severance = true, int months = 12) =>
        new(Guid.NewGuid(), "M0009", "CHRAÏBI Karim", EmployeeJob.Technician, new DateOnly(2020, 2, 1), new DateOnly(2026, 6, 30),
            ExitReason.MutualAgreement, 6.41m, StaffCategory.Employee, new NoticePeriod(1, 0), true, severance, 96m, 7400m, months,
            191m, 26m, new SettlementAmounts(7400m, 12m, 3415.38m, false, 0m, 7100m, null), null, signed, payroll);

    [Fact]
    public void The_settlement_opens_on_the_law_s_figures_and_the_reference_salary_moves_them()
    {
        var d = Settlement();
        var form = SettlementScreenRules.FormFor(d);
        form.ReferenceSalary.Should().Be("7400");
        form.LeaveIndemnity.Should().Be("3415.38");
        SettlementScreenRules.NoticeEditable(form, d).Should().BeFalse();

        form.ReferenceSalary = "8000";
        form.NoticeNotWorked = true;
        SettlementScreenRules.Recompute(form, d, leave: true, notice: true, severance: true);
        form.LeaveIndemnity.Should().Be(SettlementCalculator.LeaveIndemnity(12m, 8000m, 26m).ToString("0.##", CultureInfo.InvariantCulture));
        form.NoticeIndemnity.Should().NotBe("0");
        SettlementScreenRules.NoticeEditable(form, d).Should().BeTrue();

        form.ReferenceSalary = "huit mille";
        var before = form.LeaveIndemnity;
        SettlementScreenRules.Recompute(form, d, leave: true);
        form.LeaveIndemnity.Should().Be(before, "a reference that is no number moves nothing");

        SettlementScreenRules.Check(form).Refusal!.Field.Should().Be(SettlementScreenRules.ReferenceField);
        form.ReferenceSalary = "8000";
        form.Severance = "-1";
        SettlementScreenRules.Check(form).Refusal!.Field.Should().Be(SettlementScreenRules.SeveranceField);
        form.Severance = "";
        form.NoticeNotWorked = false;
        var amounts = SettlementScreenRules.Check(form).Amounts!;
        amounts.SeverancePay.Should().Be(0m);
        amounts.NoticeIndemnity.Should().Be(0m, "a notice worked is not paid as an indemnity");
    }

    [Fact]
    public void The_settlement_is_prefilled_with_the_decimal_comma_of_the_screen_and_reads_back()
    {
        var fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
        var d = Settlement();
        var form = SettlementScreenRules.FormFor(d, fr);
        form.LeaveIndemnity.Should().Be("3415,38");
        SettlementScreenRules.Check(form).Refusal.Should().BeNull();

        form.ReferenceSalary = "8000";
        SettlementScreenRules.Recompute(form, d, leave: true, culture: fr);
        form.LeaveIndemnity.Should().NotContain(".", "recomputed in the same writing as it opened");
    }

    [Fact]
    public void The_settlement_says_where_its_amounts_go_and_is_final_once_signed()
    {
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        SettlementScreenRules.Notice(Settlement(), Text).Should().Be(("Settlement.NoticePayroll", false));
        SettlementScreenRules.Notice(Settlement(payroll: SettlementPayroll.Closed), Text).Should().Be(("Settlement.NoticePayrollClosed", true));
        var signed = Settlement(signed: new DateOnly(2026, 7, 2));
        SettlementScreenRules.IsReadOnly(signed).Should().BeTrue();
        SettlementScreenRules.Notice(signed, Text).Should().Be(("Settlement.Signed(02/07/2026)", true));

        SettlementScreenRules.ReferenceHint(Settlement(), Text).Should().Be("Settlement.ReferenceFromMonths(12)");
        SettlementScreenRules.ReferenceHint(Settlement(months: 0), Text).Should().Be("Settlement.ReferenceFromBase");
        SettlementScreenRules.NoticeHint(Settlement(), Text).Should().Be("Settlement.NoticeLegal(Settlement.NoticeMonths(1))");
        SettlementScreenRules.SeveranceHint(Settlement(severance: false), Text, fr).Should().Be("Settlement.SeveranceNotDue");
        SettlementScreenRules.Summary(Settlement(), Text, fr)
            .Should().Be("Settlement.Summary(01/02/2020,30/06/2026,ExitReason.MutualAgreement,6,41,StaffCategory.Employee)");
        SettlementScreenRules.Total(new SettlementForm { LeaveIndemnity = "100", NoticeIndemnity = "abc", Severance = "50,5" }).Should().Be(150.5m);

        SettlementScreenRules.CheckSigned(Today.AddDays(1), new DateOnly(2026, 6, 30), Today)!.Key.Should().Be("Settlement_SignedInFuture");
        SettlementScreenRules.CheckSigned(new DateOnly(2026, 6, 29), new DateOnly(2026, 6, 30), Today)!.Key.Should().Be("Settlement_SignedBeforeExit");
        SettlementScreenRules.CheckSigned(Today, Today.AddDays(10), Today).Should().BeNull("a receipt can be signed before the exit when it is ahead");
    }
}
