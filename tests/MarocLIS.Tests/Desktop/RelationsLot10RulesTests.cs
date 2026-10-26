using System.Globalization;
using System.Text;
using FluentAssertions;
using MarocLIS.Application.Input;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Relations;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Relations;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Statistiques prescripteurs », « Visites et conventions » and « Rappels et enquêtes »,
/// moved out of the workstation's views and dialogs, now shared with the web.
/// </summary>
public sealed class RelationsLot10RulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 26);
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    private static string Dh(decimal amount) => amount.ToString("0.00", Invariant) + " DH";

    private static Func<string, bool> Rights(params string[] rights) => rights.Contains;

    // ---- Statistiques prescripteurs ------------------------------------------------------------------

    private static readonly Guid Alami = Guid.NewGuid(), Bennani = Guid.NewGuid();

    private static PrescriberVolumeRow Volume(Guid? id, string? name, string? institution, int orders, decimal revenue, bool isNew = false,
                                              int previous = 0) =>
        new(id, name, institution, new VolumeFigures(orders, orders * 3, revenue), new VolumeFigures(previous, previous * 3, previous * 100m),
            VolumeFigures.Zero, previous == 0 ? null : (orders - previous) * 100d / previous, null, null, null, isNew);

    private static PrescriberStatistics Statistics() =>
        new(new DateOnly(2026, 10, 1), Today, new DateOnly(2026, 9, 5), new DateOnly(2026, 9, 30),
            new VolumeFigures(12, 36, 4200m), new VolumeFigures(8, 20, 2800m), VolumeFigures.Zero,
            [Volume(Alami, "Dr ALAMI Karim", "Clinique Al Amal", 8, 3000m, previous: 6), Volume(Bennani, "Dr BENNANI Salma", null, 3, 900m, isNew: true),
             Volume(null, null, null, 1, 300m)],
            [Volume(null, null, " clinique al amal ", 8, 3000m), Volume(null, null, null, 4, 1200m)],
            [new MonthlyVolumeRow(new DateOnly(2026, 10, 1), new VolumeFigures(12, 36, 4200m), VolumeFigures.Zero, 3, 1, null)],
            [Volume(Bennani, "Dr BENNANI Salma", null, 3, 900m, isNew: true)],
            [],
            [new PrescriberMonthRow(Alami, "Dr ALAMI Karim", "Clinique Al Amal", new DateOnly(2026, 10, 1), new VolumeFigures(8, 24, 3000m)),
             new PrescriberMonthRow(Bennani, "Dr BENNANI Salma", null, new DateOnly(2026, 10, 1), new VolumeFigures(3, 9, 900m)),
             new PrescriberMonthRow(Alami, "Dr ALAMI Karim", "Clinique Al Amal", new DateOnly(2026, 9, 1), new VolumeFigures(6, 18, 2000m))]);

    [Fact]
    public void The_statistics_are_for_whom_sees_the_relations_or_the_steering_and_a_row_opens_once_computed()
    {
        PrescriberStatisticsScreenRules.Actions(Rights(Permissions.Relations.View), null)
            .Should().Be(new PrescriberStatisticsActions(true, false, false));
        PrescriberStatisticsScreenRules.Actions(Rights(Permissions.Steering.View), Statistics())
            .Should().Be(new PrescriberStatisticsActions(true, true, true));
        PrescriberStatisticsScreenRules.Actions(Rights(Permissions.Messaging.Campaign), Statistics())
            .Should().Be(new PrescriberStatisticsActions(false, false, false));
    }

    [Fact]
    public void The_key_figures_say_the_revenue_in_dirhams_and_the_change_against_the_period_before()
    {
        var tiles = PrescriberStatisticsScreenRules.Tiles(Statistics(), Text, Dh, Invariant);

        tiles.Select(t => t.Key).Should().Equal("orders", "revenue", "new", "lost");
        tiles[1].Value.Should().Be("4200.00 DH");
        tiles[1].Caption.Should().Be("Relations.Column.revenue");
        tiles[0].Sub.Should().Contain("+50");
        tiles[2].Value.Should().Be("1");
        PrescriberStatisticsScreenRules.Subtitle(Statistics(), Text)
            .Should().Be("01/10/2026 → 26/10/2026 (Relations.ComparedWith 05/09/2026 → 30/09/2026)");
        PrescriberStatisticsScreenRules.Subtitle(null, Text).Should().BeEmpty();
    }

    [Fact]
    public void A_new_prescriber_is_marked_and_a_file_without_prescriber_is_named_so()
    {
        var result = Statistics();

        PrescriberStatisticsScreenRules.IsNew(result, PrescriberStatisticsTab.ByPrescriber, 1).Should().BeTrue();
        PrescriberStatisticsScreenRules.IsNew(result, PrescriberStatisticsTab.ByPrescriber, 0).Should().BeFalse();
        PrescriberStatisticsScreenRules.IsNew(result, PrescriberStatisticsTab.ByMonth, 0).Should().BeFalse();
        PrescriberStatisticsScreenRules.IsNew(result, PrescriberStatisticsTab.ByPrescriber, 9).Should().BeFalse();

        var rows = PrescriberStatisticsScreenRules.Rows(result, PrescriberStatisticsTab.ByPrescriber, Text, Dh, Invariant);
        rows.Should().HaveCount(3);
        rows[2].Cells[0].Should().Be("Relations.NoPrescriber");
        rows[0].Cells.Should().Contain("3000.00 DH");
        PrescriberStatisticsScreenRules.Columns(PrescriberStatisticsTab.ByPrescriber).Count.Should().Be(rows[0].Cells.Count);
        PrescriberStatisticsScreenRules.Rows(null, PrescriberStatisticsTab.ByPrescriber, Text, Dh, Invariant).Should().BeEmpty();
    }

    [Fact]
    public void A_rows_detail_shows_a_prescribers_months_an_institutions_or_a_months_prescribers()
    {
        var result = Statistics();

        var prescriber = PrescriberStatisticsScreenRules.Drill(result, PrescriberStatisticsTab.ByPrescriber, 0, Text, Dh, Invariant)!;
        prescriber.Title.Should().Be("Relations.ByPrescriber — Dr ALAMI Karim");
        prescriber.Rows.Should().HaveCount(2, "Dr Alami sent in September and in October");

        var institution = PrescriberStatisticsScreenRules.Drill(result, PrescriberStatisticsTab.ByInstitution, 0, Text, Dh, Invariant)!;
        institution.Rows.Should().HaveCount(2, "the institution is matched on its trimmed, upper-cased name");
        var none = PrescriberStatisticsScreenRules.Drill(result, PrescriberStatisticsTab.ByInstitution, 1, Text, Dh, Invariant)!;
        none.Title.Should().EndWith("Relations.NoInstitution");
        none.Rows.Should().ContainSingle();

        var month = PrescriberStatisticsScreenRules.Drill(result, PrescriberStatisticsTab.ByMonth, 0, Text, Dh, Invariant)!;
        month.Title.Should().Be("Relations.ByMonth — 10/2026");
        month.Rows.Should().HaveCount(2);

        PrescriberStatisticsScreenRules.Drill(result, PrescriberStatisticsTab.MonthlyDetail, 0, Text, Dh, Invariant).Should().BeNull();
        PrescriberStatisticsScreenRules.Drill(result, PrescriberStatisticsTab.ByPrescriber, 7, Text, Dh, Invariant).Should().BeNull();
        PrescriberStatisticsScreenRules.Drill(null, PrescriberStatisticsTab.ByPrescriber, 0, Text, Dh, Invariant).Should().BeNull();
    }

    [Fact]
    public void A_table_and_a_detail_leave_as_csv_named_by_the_period()
    {
        var result = Statistics();
        var csv = Encoding.UTF8.GetString(PrescriberStatisticsScreenRules.Csv(result, PrescriberStatisticsTab.ByPrescriber, Text, Dh, Invariant)!);
        csv.Should().Contain("Dr ALAMI Karim").And.Contain("Relations.Column.revenue");
        PrescriberStatisticsScreenRules.Csv(result, PrescriberStatisticsTab.LostPrescribers, Text, Dh, Invariant).Should().BeNull();

        var drill = PrescriberStatisticsScreenRules.Drill(result, PrescriberStatisticsTab.ByPrescriber, 0, Text, Dh, Invariant)!;
        Encoding.UTF8.GetString(PrescriberStatisticsScreenRules.Csv(drill, Text)!).Should().Contain("09/2026");

        PrescriberStatisticsScreenRules.FileName(result, PrescriberStatisticsTab.ByInstitution)
            .Should().Be("prescripteurs-par-etablissement-20261001-20261026.csv");
        PrescriberStatisticsScreenRules.FileName(result, PrescriberStatisticsTab.ByPrescriber, detail: true).Should().EndWith("-detail.csv");
        PrescriberStatisticsScreenRules.FieldOf(Error.Validation("Stats_PeriodReversed")).Should().Be(PrescriberStatisticsScreenRules.PeriodField);
        PrescriberStatisticsScreenRules.FieldOf(Error.Validation("Message.UnexpectedError")).Should().BeNull();
    }

    // ---- Visites et conventions ----------------------------------------------------------------------

    private static DelegateVisitRow Visit(DateOnly? next = null, bool due = false) =>
        new(Guid.NewGuid(), Today.AddDays(-5), Alami, "Dr ALAMI Karim", "Clinique Al Amal", Guid.NewGuid(), "Nadia Fassi",
            VisitPurpose.Complaint, "Deux patientes renvoyées pour un jeûne mal indiqué.", next, due);

    private static PartnershipRow Partnership(bool active = true, bool inForce = true, bool endingSoon = false, string? file = null) =>
        new(Guid.NewGuid(), "Clinique Al Amal", PartnerKind.Clinic, PartnershipKind.CareConvention, "CONV-2026-014", Today.AddMonths(-8),
            Today.AddDays(24), active, inForce, endingSoon, 10m, "Mme Berrada", "0522 98 45 10", file);

    [Fact]
    public void Recording_is_for_whom_manages_the_relations_and_the_signed_document_opens_for_whom_sees_them()
    {
        var manage = Rights(Permissions.Relations.View, Permissions.Relations.Manage);
        var view = Rights(Permissions.Relations.View);

        RelationsScreenRules.Actions(manage, RelationsTab.Visits, Visit(), null)
            .Should().Match<RelationsActions>(a => a.VisitActions && a.EditVisit && a.DeleteVisit && !a.PartnershipActions && !a.ShowInactive);
        RelationsScreenRules.Actions(manage, RelationsTab.Visits, null, null)
            .Should().Match<RelationsActions>(a => a.VisitActions && !a.EditVisit && !a.DeleteVisit);
        RelationsScreenRules.Actions(manage, RelationsTab.Partnerships, null, Partnership(file: "convention.pdf"))
            .Should().Match<RelationsActions>(a => a.PartnershipActions && a.EditPartnership && a.Attach && a.OpenFile && a.ShowInactive && !a.VisitActions);
        RelationsScreenRules.Actions(view, RelationsTab.Partnerships, null, Partnership(file: "convention.pdf"))
            .Should().Match<RelationsActions>(a => !a.PartnershipActions && !a.EditPartnership && !a.Attach && a.OpenFile);
        RelationsScreenRules.Actions(view, RelationsTab.Partnerships, null, Partnership()).OpenFile.Should().BeFalse("no document attached");
        RelationsScreenRules.Actions(view, RelationsTab.DueVisits, Visit(), null)
            .Should().Match<RelationsActions>(a => !a.VisitActions && !a.EditVisit && !a.DeleteVisit);
    }

    [Fact]
    public void A_convention_ending_soon_is_on_the_warnings_colour_and_one_switched_off_is_muted()
    {
        RelationsScreenRules.State(Partnership(endingSoon: true)).Should().Be(PartnershipState.EndingSoon);
        RelationsScreenRules.Tone(PartnershipState.EndingSoon).Should().Be(StockTone.Warning);
        RelationsScreenRules.State(Partnership(active: false, endingSoon: true)).Should().Be(PartnershipState.Inactive);
        RelationsScreenRules.State(Partnership(inForce: false)).Should().Be(PartnershipState.NotInForce);
        RelationsScreenRules.State(Partnership()).Should().Be(PartnershipState.InForce);
        RelationsScreenRules.Tone(PartnershipState.InForce).Should().Be(StockTone.Done);
        RelationsScreenRules.EndingSoon(Partnership(endingSoon: true)).Should().BeTrue();

        var cells = RelationsScreenRules.Cells(Partnership(file: "convention.pdf"), Text, Invariant);
        cells.Should().HaveCount(RelationsScreenRules.Columns(RelationsTab.Partnerships).Count);
        cells.Should().Contain("10 %").And.Contain("Relations.State.InForce");
    }

    [Fact]
    public void A_visit_whose_next_date_has_come_is_flagged_and_its_file_shows_who_why_and_when()
    {
        RelationsScreenRules.NextVisitDue(Visit(Today.AddDays(-3), due: true)).Should().BeTrue();
        RelationsScreenRules.NextVisitDue(Visit(Today.AddDays(10))).Should().BeFalse();

        var facts = RelationsScreenRules.Facts(Visit(Today.AddDays(-3), due: true), Text);
        facts.Select(f => f.Value).Should().Contain(["Dr ALAMI Karim", "Nadia Fassi", "VisitPurpose.Complaint", "23/10/2026"]);
        facts.Single(f => f.Label == "Relations.Column.next").Mono.Should().BeTrue();

        var detail = new PartnershipDetail(Guid.NewGuid(), "Clinique Al Amal", PartnerKind.Clinic, null, "001524789000056", "Mme Berrada",
                                           "0522 98 45 10", "direction@alamal.ma", PartnershipKind.CareConvention, "CONV-2026-014",
                                           Today.AddMonths(-8), null, "Ramassage deux fois par jour.", 10m, 60, null, true);
        var file = RelationsScreenRules.Facts(detail, Partnership(), Text, Invariant);
        file.Select(f => f.Label).Should().Contain("Billing.Ice");
        file.Select(f => f.Value).Should().Contain("001524789000056");
    }

    [Fact]
    public void A_visit_is_refused_beside_its_box_and_a_convention_too()
    {
        var prescriber = Guid.NewGuid();
        var delegateUser = Guid.NewGuid();
        RelationsScreenRules.FormFor(null, delegateUser, Today).Should().Be(new VisitForm(Today, Guid.Empty, delegateUser, VisitPurpose.FollowUp, null, null));

        RelationsScreenRules.Check(new VisitForm(Today, Guid.Empty, delegateUser, VisitPurpose.FollowUp, null, null), null, Today)
            .Refusal!.Field.Should().Be(RelationsScreenRules.VisitPrescriberField);
        RelationsScreenRules.Check(new VisitForm(Today.AddDays(1), prescriber, delegateUser, VisitPurpose.FollowUp, null, null), null, Today)
            .Refusal!.Field.Should().Be(RelationsScreenRules.VisitDateField);
        RelationsScreenRules.Check(new VisitForm(Today, prescriber, delegateUser, VisitPurpose.FollowUp, null, Today), null, Today)
            .Refusal!.Field.Should().Be(RelationsScreenRules.VisitNextField);
        RelationsScreenRules.Check(new VisitForm(Today, prescriber, delegateUser, VisitPurpose.FollowUp, new string('a', 2001), null), null, Today)
            .Refusal.Should().Be(new FieldRefusal(RelationsScreenRules.VisitNotesField, "Relations_TextTooLong", "2000"));
        var (ok, request) = RelationsScreenRules.Check(new VisitForm(Today, prescriber, delegateUser, VisitPurpose.Complaint, "  ", Today.AddDays(30)), null, Today);
        ok.Should().BeNull();
        request!.Notes.Should().BeNull();

        PartnershipForm Form(string? discount = null, string? days = null, string? ice = null) =>
            new() { PartnerName = "Clinique Al Amal", StartDate = Today, Discount = discount, PaymentDays = days, Ice = ice };
        RelationsScreenRules.Check(new PartnershipForm { StartDate = Today }, null).Refusal!.Field.Should().Be(RelationsScreenRules.PartnerNameField);
        RelationsScreenRules.Check(Form() with { EndDate = Today.AddDays(-1) }, null).Refusal!.Field.Should().Be(RelationsScreenRules.PartnerEndField);
        RelationsScreenRules.Check(Form("140"), null).Refusal!.Field.Should().Be(RelationsScreenRules.PartnerDiscountField);
        RelationsScreenRules.Check(Form(days: "12,5"), null).Refusal!.Field.Should().Be(RelationsScreenRules.PartnerPaymentField);
        RelationsScreenRules.Check(Form(ice: "12345"), null).Refusal!.Field.Should().Be(RelationsScreenRules.PartnerIceField);
        var (none, saved) = RelationsScreenRules.Check(Form("0", "0", " 001524789000056 "), null);
        none.Should().BeNull();
        saved!.DiscountPercent.Should().BeNull("a discount of 0 is none");
        saved.PaymentTermsDays.Should().BeNull();
        saved.PartnerIce.Should().Be("001524789000056");
        RelationsScreenRules.Check(Form("12,5", "45"), null).Request!.DiscountPercent.Should().Be(12.5m);

        RelationsScreenRules.FieldOf("Prescriber_IceInvalid", partnership: true).Should().Be(RelationsScreenRules.PartnerIceField);
        RelationsScreenRules.FieldOf("Relations_TextTooLong", partnership: false).Should().Be(RelationsScreenRules.VisitNotesField);
        RelationsScreenRules.FieldOf("Relations_TextTooLong", partnership: true).Should().Be(RelationsScreenRules.PartnerTermsField);
    }

    [Fact]
    public void The_signed_convention_is_a_document_that_runs_nothing_within_its_size()
    {
        RelationsScreenRules.CheckDocument("convention.pdf", 1000).Should().BeNull();
        RelationsScreenRules.CheckDocument("convention.exe", 1000)!.Key.Should().Be("Purchasing_FileType");
        RelationsScreenRules.CheckDocument("convention.pdf", 0)!.Key.Should().Be("Purchasing_FileEmpty");
        RelationsScreenRules.CheckDocument("convention.pdf", PartnershipDocument.MaximumBytes + 1)!.Key.Should().Be("Purchasing_FileTooLarge");
        RelationsScreenRules.DocumentAccept.Should().Contain(".pdf");
    }

    [Fact]
    public void The_lists_leave_as_csv_named_by_their_tab()
    {
        Encoding.UTF8.GetString(RelationsScreenRules.Csv(RelationsTab.Visits, [Visit()], [], Text, Invariant)!).Should().Contain("Dr ALAMI Karim");
        RelationsScreenRules.Csv(RelationsTab.Partnerships, [Visit()], [], Text, Invariant).Should().BeNull();
        RelationsScreenRules.FileName(RelationsTab.Visits, new DateOnly(2026, 10, 1), Today, Today).Should().Be("visites-20261001-20261026.csv");
        RelationsScreenRules.FileName(RelationsTab.DueVisits, new DateOnly(2026, 10, 1), Today, Today).Should().Be("visites-a-prevoir-20261026.csv");
        RelationsScreenRules.FileName(RelationsTab.Partnerships, new DateOnly(2026, 10, 1), Today, Today).Should().Be("conventions-20261026.csv");
        RelationsScreenRules.Counts(6, 2, 3, Text).Should().Be("Relations.Counts(6,2,3)");
    }

    // ---- Rappels et enquêtes -------------------------------------------------------------------------

    private static ReminderRuleRow Rule(bool active = true) =>
        new(Guid.NewGuid(), Guid.NewGuid(), null, "GLY", new MultilingualText("Glycémie à jeun", "سكر الدم"), 3, 60,
            new MultilingualText(MessagingSettings.DefaultReminderFr, MessagingSettings.DefaultReminderAr), active);

    [Fact]
    public void Campaigns_are_for_whom_may_run_them_and_nothing_is_sent_without_a_patient_ticked()
    {
        var campaign = Rights(Permissions.Messaging.Campaign);

        PatientMessagingScreenRules.Actions(campaign, MessagingTab.Reminders, 3, null)
            .Should().Be(new MessagingActions(true, true, false, false, false, true));
        PatientMessagingScreenRules.Actions(campaign, MessagingTab.Reminders, 0, null).SendEnabled.Should().BeFalse();
        PatientMessagingScreenRules.Actions(campaign, MessagingTab.Rules, 0, Rule())
            .Should().Be(new MessagingActions(false, false, true, true, false, true));
        PatientMessagingScreenRules.Actions(campaign, MessagingTab.Rules, 0, null).EditRule.Should().BeFalse();
        PatientMessagingScreenRules.Actions(campaign, MessagingTab.SurveyResults, 0, null).SurveyTools.Should().BeTrue();
        PatientMessagingScreenRules.Actions(Rights(Permissions.Relations.View), MessagingTab.Surveys, 4, null)
            .Should().Be(new MessagingActions(false, false, false, false, false, false));
    }

    [Fact]
    public void The_notice_says_the_consent_and_that_without_a_gateway_the_lists_are_only_previewed()
    {
        PatientMessagingScreenRules.Notice(true, Text).Should().Be("Messaging.ConsentNotice Messaging.WhatsAppNotice");
        PatientMessagingScreenRules.Notice(false, Text).Should().Contain("Messaging.SmsNotConfiguredNotice");
        PatientMessagingScreenRules.UsesPeriod(MessagingTab.Reminders).Should().BeFalse();
        PatientMessagingScreenRules.UsesPeriod(MessagingTab.Log).Should().BeTrue();
    }

    [Fact]
    public void A_message_the_gateway_refused_is_red_with_its_reason_in_the_readers_language()
    {
        var failed = new PatientMessageRow(new DateTimeOffset(2026, 10, 20, 8, 30, 0, TimeSpan.Zero), PatientMessageKind.BilanReminder,
                                           "IDRISSI Yassine", "0661234599", PatientMessageStatus.Failed, "Messaging_SmsFailed HTTP 500 numero bloque",
                                           "Bonjour, votre bilan…");
        PatientMessagingScreenRules.Tone(PatientMessageStatus.Failed).Should().Be(StockTone.Critical);
        PatientMessagingScreenRules.Tone(PatientMessageStatus.Sent).Should().Be(StockTone.Done);

        var cells = PatientMessagingScreenRules.Cells(failed, TimeSpan.FromHours(1), Text);
        cells[0].Should().Be("20/10/2026 09:30");
        cells[1].Should().Be("PatientMessageKind.BilanReminder");
        cells[^1].Should().Be("Bonjour, votre bilan…  [Messaging_SmsFailed(HTTP 500 numero bloque)]");
        PatientMessagingScreenRules.Failure("le serveur ne répond pas", Text).Should().Be("le serveur ne répond pas");
        PatientMessagingScreenRules.Failure("Messaging_SmsNotConfigured", Text).Should().Be("Messaging_SmsNotConfigured");
    }

    [Fact]
    public void A_send_says_what_went_and_an_import_its_refusals_in_the_readers_language()
    {
        PatientMessagingScreenRules.ConfirmSend(3, Text).Should().Be("Messaging.ConfirmSend(3)");
        PatientMessagingScreenRules.Outcome(new CampaignOutcome(3, 0, 0), Text).Should().Be(("Messaging.CampaignOutcome(3,0,0)", StockInk.Plain));
        PatientMessagingScreenRules.Outcome(new CampaignOutcome(2, 1, 0), Text).Ink.Should().Be(StockInk.Abnormal);

        var (summary, details) = PatientMessagingScreenRules.ImportOutcome(
            new ReplyImportOutcome(1, ["merci pour tout"], ["QQQQQQ: Survey_CodeUnknown"]), Text);
        summary.Should().Be("Messaging.ImportOutcome(1,1,1)");
        details.Should().Equal("Survey_CodeUnknown(QQQQQQ)", "? merci pour tout");
    }

    [Fact]
    public void The_survey_results_give_the_rate_the_average_and_the_scores()
    {
        var results = new SurveyResults(new DateOnly(2026, 10, 1), Today, 10, 9, 4, 44.4, 4.25, [0, 0, 1, 1, 2],
                                        [new SurveyComment(Today, 5, "Accueil très aimable.")]);
        var tiles = PatientMessagingScreenRules.Tiles(results, Text, Invariant);

        tiles.Select(t => t.Key).Should().Equal("invited", "answered", "average", "distribution");
        tiles[0].Sub.Should().Be("9 Messaging.Delivered");
        tiles[2].Value.Should().Be("4.25 / 5");
        tiles[3].Value.Should().Be("1★ 0  2★ 0  3★ 1  4★ 1  5★ 2");
        PatientMessagingScreenRules.Cells(results.Comments[0], Invariant).Should().Equal("26/10/2026", "5", "Accueil très aimable.");
    }

    [Fact]
    public void A_list_naming_patients_is_logged_when_it_leaves_as_a_file()
    {
        PatientMessagingScreenRules.ExportContext(MessagingTab.Reminders).Should().Be("Relations/ReminderPreview");
        PatientMessagingScreenRules.ExportContext(MessagingTab.Surveys).Should().Be("Relations/SurveyPreview");
        PatientMessagingScreenRules.ExportContext(MessagingTab.Log).Should().Be("Relations/MessageLog");
        PatientMessagingScreenRules.ExportContext(MessagingTab.Rules).Should().BeNull();
        PatientMessagingScreenRules.FileName(MessagingTab.Log, new DateOnly(2026, 10, 1), Today, Today).Should().Be("journal-sms-20261001-20261026.csv");
        PatientMessagingScreenRules.FileName(MessagingTab.Reminders, new DateOnly(2026, 10, 1), Today, Today).Should().Be("rappels-20261026.csv");
        PatientMessagingScreenRules.Csv(MessagingTab.Log, [], Text).Should().BeNull();
    }

    [Fact]
    public void A_rule_is_refused_beside_its_box_and_keeps_its_analysis_when_no_longer_offered()
    {
        var gly = new ReminderTarget(Guid.NewGuid(), false, "GLY", new MultilingualText("Glycémie à jeun"));
        var bilan = new ReminderTarget(Guid.NewGuid(), true, "BILAN_LIP", new MultilingualText("Bilan lipidique"));
        IReadOnlyList<ReminderTarget> targets = [gly, bilan];
        var rule = Rule();

        PatientMessagingScreenRules.Targets(targets, rule).Should().HaveCount(3, "the rule's own analysis stays offered");
        PatientMessagingScreenRules.Targets(targets, null).Should().HaveCount(2);
        PatientMessagingScreenRules.Display(bilan, AppLanguage.French, Text).Should().Be("BILAN_LIP — Bilan lipidique (Messaging.Panel)");

        var form = PatientMessagingScreenRules.FormFor((ReminderRuleRow?)null, Invariant);
        form.Should().Match<ReminderRuleForm>(f => f.Delay == "3" && f.Window == "60" && f.TemplateFr == MessagingSettings.DefaultReminderFr && f.IsActive);
        PatientMessagingScreenRules.Check(form, targets, null).Refusal!.Field.Should().Be(PatientMessagingScreenRules.RuleTargetField);
        PatientMessagingScreenRules.Check(form with { TargetId = gly.Id, Delay = "99" }, targets, null).Refusal!.Field
            .Should().Be(PatientMessagingScreenRules.RuleDelayField);
        PatientMessagingScreenRules.DelayRefusal(Text).Should().Be("Reminder_DelayOutOfRange(1,60)");
        PatientMessagingScreenRules.Check(form with { TargetId = gly.Id, Window = "400" }, targets, null).Refusal!.Field
            .Should().Be(PatientMessagingScreenRules.RuleWindowField);
        PatientMessagingScreenRules.Check(form with { TargetId = gly.Id, TemplateFr = "Bonjour {inconnu}, revenez." }, targets, null).Refusal!.Field
            .Should().Be(PatientMessagingScreenRules.RuleTemplateFrField);

        var (ok, request) = PatientMessagingScreenRules.Check(form with { TargetId = bilan.Id, TemplateAr = "  " }, targets, null);
        ok.Should().BeNull();
        request!.TestPanelId.Should().Be(bilan.Id);
        request.AnalysisTestId.Should().BeNull();
        request.Template.Ar.Should().BeNull();
        PatientMessagingScreenRules.RuleFieldOf("Reminder_AlreadyExists").Should().Be(PatientMessagingScreenRules.RuleTargetField);
    }

    [Fact]
    public void The_survey_settings_and_an_answer_are_refused_beside_their_box()
    {
        var settings = MessagingSettings.Default;
        var form = PatientMessagingScreenRules.FormFor(settings, Invariant);
        PatientMessagingScreenRules.Check(form, settings).Refusal.Should().BeNull();
        PatientMessagingScreenRules.Check(form with { Url = "http://enquete.ma" }, settings).Refusal!.Field
            .Should().Be(PatientMessagingScreenRules.SettingsUrlField);
        PatientMessagingScreenRules.Check(form with { Interval = "-1" }, settings).Refusal!.Field
            .Should().Be(PatientMessagingScreenRules.SettingsIntervalField);
        PatientMessagingScreenRules.Check(form with { TemplateFr = "Donnez-nous votre avis, merci." }, settings).Refusal!.Field
            .Should().Be(PatientMessagingScreenRules.SettingsTemplateFrField);

        PatientMessagingScreenRules.Check(new SurveyAnswerForm("??", "5", null)).Refusal
            .Should().Be(new FieldRefusal(PatientMessagingScreenRules.AnswerCodeField, "Survey_CodeInvalid", "??"));
        PatientMessagingScreenRules.Check(new SurveyAnswerForm("Z9QZ4Q", "6", null)).Refusal!.Field.Should().Be(PatientMessagingScreenRules.AnswerScoreField);
        PatientMessagingScreenRules.Check(new SurveyAnswerForm("Z9QZ4Q", "4", new string('a', 1001))).Refusal!.Field
            .Should().Be(PatientMessagingScreenRules.AnswerCommentField);
        PatientMessagingScreenRules.Check(new SurveyAnswerForm(" z9qz4q ", "4", " Bien ")).Answer.Should().Be(("Z9QZ4Q", 4, "Bien"));
        PatientMessagingScreenRules.AnswerFieldOf("Survey_CodeUnknown").Should().Be(PatientMessagingScreenRules.AnswerCodeField);
    }
}
