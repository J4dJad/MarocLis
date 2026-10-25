using System.Globalization;
using FluentAssertions;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Équipements » and « Contrats automates », moved out of the workstation's views and
/// dialogs, now shared with the web.
/// </summary>
public sealed class EquipmentScreenRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 18);

    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    internal static EquipmentRow Instrument(string code = "AUT-01", EquipmentStatus status = EquipmentStatus.Operational,
                                            bool maintenanceOverdue = false, bool calibrationOverdue = false,
                                            bool contractExpired = false, string? reason = null) =>
        new(Guid.NewGuid(), code, new MultilingualText("Automate hématologie", "جهاز الدم"), EquipmentKind.Analyser, status,
            "Sysmex", "XN-550", "SN-12345", "Paillasse 2", new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 1), null,
            maintenanceOverdue, calibrationOverdue, contractExpired, new DateOnly(2026, 6, 30), reason);

    // ---- Équipements -------------------------------------------------------------------------------

    [Fact]
    public void An_action_needs_its_right_and_an_instrument_still_in_the_register()
    {
        bool All(string _) => true;
        EquipmentScreenRules.Actions(All, Instrument()).Should().Be(new EquipmentScreenActions(true, true, true, true, true, true));
        EquipmentScreenRules.Actions(All, null).Should().Match<EquipmentScreenActions>(a => !a.Maintenance && !a.Calibration && !a.Status);
        EquipmentScreenRules.Actions(All, Instrument(status: EquipmentStatus.Decommissioned))
            .Should().Match<EquipmentScreenActions>(a => a.MaintenanceRight && !a.Maintenance && !a.Calibration && !a.Status);

        var technician = EquipmentScreenRules.Actions(p => p == Permissions.Equipment.RecordMaintenance, Instrument());
        technician.Should().Be(new EquipmentScreenActions(true, false, false, true, false, false));
    }

    [Fact]
    public void A_lapsed_calibration_puts_the_row_in_the_alarms_colours_a_late_maintenance_only_its_date()
    {
        EquipmentScreenRules.RowInk(Instrument(calibrationOverdue: true)).Should().Be(StockInk.CriticalBold);
        EquipmentScreenRules.RowInk(Instrument(status: EquipmentStatus.OutOfService)).Should().Be(StockInk.CriticalBold);
        EquipmentScreenRules.RowInk(Instrument(status: EquipmentStatus.Decommissioned)).Should().Be(StockInk.Muted);

        var late = Instrument(maintenanceOverdue: true, contractExpired: true);
        EquipmentScreenRules.RowInk(late).Should().Be(StockInk.Plain);
        EquipmentScreenRules.MaintenanceInk(late).Should().Be(StockInk.AbnormalBold);
        EquipmentScreenRules.ContractInk(late).Should().Be(StockInk.AbnormalBold);
        EquipmentScreenRules.MaintenanceInk(Instrument(maintenanceOverdue: true, calibrationOverdue: true)).Should().Be(StockInk.Plain);

        EquipmentScreenRules.StatusTone(EquipmentStatus.Operational).Should().Be(StockTone.Done);
        EquipmentScreenRules.StatusTone(EquipmentStatus.OutOfService).Should().Be(StockTone.Critical);
        EquipmentScreenRules.StatusTone(EquipmentStatus.UnderMaintenance).Should().Be(StockTone.Warning);
        EquipmentScreenRules.StatusTone(EquipmentStatus.Commissioning).Should().Be(StockTone.Running);
        EquipmentScreenRules.StatusTone(EquipmentStatus.Decommissioned).Should().Be(StockTone.Waiting);
    }

    [Fact]
    public void A_due_date_carries_its_marker_and_the_count_says_how_many_are_late()
    {
        EquipmentScreenRules.Due(new DateOnly(2026, 10, 1), overdue: true).Should().Be("! 01/10/26");
        EquipmentScreenRules.Due(new DateOnly(2026, 10, 1), overdue: false).Should().Be("01/10/26");
        EquipmentScreenRules.Due(null, overdue: false).Should().Be("—");

        EquipmentScreenRules.Count([Instrument(), Instrument(calibrationOverdue: true), Instrument(maintenanceOverdue: true)], Text)
            .Should().Be("StocksUi.Instruments(3) · Equipment.Overdue 2");
        EquipmentScreenRules.Count([Instrument()], Text).Should().Be("StocksUi.Instruments(1)");

        EquipmentScreenRules.SearchCells(Instrument(), AppLanguage.Arabic, Text).Should().Contain(["جهاز الدم", "SN-12345", "Paillasse 2"]);
    }

    [Fact]
    public void The_card_says_what_is_wrong_rather_than_leaving_two_dates_to_compare()
    {
        EquipmentScreenRules.Warning(Instrument(calibrationOverdue: true, maintenanceOverdue: true))
            .Should().Be(("Equipment.CalibrationOverdueWarning", StockInk.Critical));
        EquipmentScreenRules.Warning(Instrument(maintenanceOverdue: true)).Should().Be(("Equipment.MaintenanceOverdueWarning", StockInk.Abnormal));
        EquipmentScreenRules.Warning(Instrument(contractExpired: true)).Should().Be(("Equipment.ContractExpiredWarning", StockInk.Abnormal));
        EquipmentScreenRules.Warning(Instrument(status: EquipmentStatus.Decommissioned, calibrationOverdue: true))
            .Should().Be(("Equipment.DecommissionedFinal", StockInk.Muted));
        EquipmentScreenRules.Warning(Instrument()).Should().BeNull();

        EquipmentScreenRules.Detail(Instrument(reason: "Panne carte mère"), Text)
            .Should().Be("Sysmex  ·  XN-550  ·  Equipment.SerialNumber SN-12345  ·  Paillasse 2  ·  Field.Reason : Panne carte mère");
        EquipmentScreenRules.Facts(Instrument(calibrationOverdue: true), Text).Should().HaveCount(6)
            .And.Contain(new EquipmentFact("Equipment.NextCalibration", "! 01/10/26", true));
    }

    [Fact]
    public void An_intervention_needs_its_french_description_and_a_readable_downtime()
    {
        var form = EquipmentScreenRules.NewMaintenance();
        form.Kind.Should().Be(MaintenanceKind.Corrective, "a breakdown repair is the commonest thing recorded off the cuff");
        form.PostCheckPassed.Should().BeTrue();

        EquipmentScreenRules.Check(form).Refusal!.Field.Should().Be(EquipmentScreenRules.DescriptionField);

        form.DescriptionFr = "  Remplacement de la pompe  ";
        form.Downtime = "2 h";
        var refused = EquipmentScreenRules.Check(form).Refusal!;
        refused.Should().Be(new Application.Input.FieldRefusal(EquipmentScreenRules.DowntimeField, "Result_NotANumber", "2 h"));

        form.Downtime = "3,5";
        form.Technician = " M. Idrissi ";
        form.PostCheckPassed = false;
        var entry = EquipmentScreenRules.Check(form).Entry!;
        entry.Description.Fr.Should().Be("Remplacement de la pompe");
        entry.DowntimeHours.Should().Be(3.5m);
        entry.ExternalTechnician.Should().Be("M. Idrissi");
        entry.PartsReplaced.Should().BeNull();
        entry.PostCheckPassed.Should().BeFalse();

        form.Downtime = "0";
        EquipmentScreenRules.Check(form).Entry!.DowntimeHours.Should().BeNull("no downtime is no downtime");
    }

    [Fact]
    public void A_failed_check_takes_an_operational_instrument_out_and_offers_the_non_conformity()
    {
        var operational = Instrument();
        EquipmentScreenRules.TakenOutOfService(operational).Should().BeTrue();
        EquipmentScreenRules.PostCheckQuestion(operational, Text)
            .Should().Be("Equipment.PostCheckFailedOutOfService\n\nEquipment.PostCheckFailedOfferNc");
        EquipmentScreenRules.PostCheckQuestion(Instrument(status: EquipmentStatus.UnderMaintenance), Text)
            .Should().Be("Equipment.PostCheckFailedOfferNc");

        EquipmentScreenRules.MayReportNonConformity(p => p == Permissions.Quality.ReportNonConformity).Should().BeTrue();
        EquipmentScreenRules.MayReportNonConformity(p => p == Permissions.Equipment.RecordMaintenance).Should().BeFalse();

        var nc = EquipmentScreenRules.NonConformity(operational, Today, AppLanguage.French, Text);
        nc.Category.Should().Be(NonConformityCategory.Equipment);
        nc.Severity.Should().Be(NonConformitySeverity.Major);
        nc.Source.Should().Be(DetectionSource.StaffObservation);
        nc.InstrumentId.Should().Be(operational.InstrumentId);
        nc.Description.Should().Be("Equipment.PostCheckFailedNcDescription(AUT-01,Automate hématologie)");
    }

    [Fact]
    public void A_passing_calibration_needs_its_calibrator_lot_and_a_validity_to_come()
    {
        var form = EquipmentScreenRules.NewCalibration(Today);
        form.ValidUntil.Should().Be(new DateOnly(2026, 11, 18));
        EquipmentScreenRules.LotRequired(CalibrationOutcome.PassedWithReservation).Should().BeTrue();
        EquipmentScreenRules.LotRequired(CalibrationOutcome.Failed).Should().BeFalse();

        EquipmentScreenRules.Check(form, Today).Refusal!.Key.Should().Be("Calibration_CalibratorLotRequired");

        form.CalibratorLot = " CAL-2611 ";
        form.ValidUntil = Today.AddDays(-1);
        EquipmentScreenRules.Check(form, Today).Refusal!.Field.Should().Be(EquipmentScreenRules.ValidUntilField);

        form.ValidUntil = Today.AddDays(30);
        form.Slope = "1,02x";
        EquipmentScreenRules.Check(form, Today).Refusal!.Field.Should().Be(EquipmentScreenRules.SlopeField);

        form.Slope = "1,02";
        form.Intercept = "-0.3";
        form.HasValidity = false;
        var entry = EquipmentScreenRules.Check(form, Today).Entry!;
        entry.Should().Be(new CalibrationEntry(CalibrationOutcome.Passed, "CAL-2611", null, 1.02m, -0.3m, null));

        var failed = new CalibrationForm { Outcome = CalibrationOutcome.Failed, ValidUntil = Today };
        EquipmentScreenRules.Check(failed, Today).Entry!.CalibratorLotNumber.Should().BeNull();
    }

    [Fact]
    public void A_status_is_one_of_the_four_the_service_applies_and_its_reason_is_said()
    {
        EquipmentScreenRules.StatusChoices.Should().Equal(EquipmentStatus.Operational, EquipmentStatus.UnderMaintenance,
                                                          EquipmentStatus.OutOfService, EquipmentStatus.Decommissioned);
        EquipmentScreenRules.StatusNoticeKey(EquipmentStatus.Operational).Should().Be("Equipment.ReturnToServiceNotice");
        EquipmentScreenRules.StatusNoticeKey(EquipmentStatus.Decommissioned).Should().Be("Equipment.DecommissionNotice");
        EquipmentScreenRules.StatusNoticeKey(EquipmentStatus.OutOfService).Should().BeNull();

        EquipmentScreenRules.Check(new EquipmentStatusForm { Reason = " pan " }).Refusal!.Key.Should().Be("Equipment_StatusReasonRequired");
        EquipmentScreenRules.Check(new EquipmentStatusForm { Reason = "  Panne pompe  " }).Reason.Should().Be("Panne pompe");
        EquipmentScreenRules.CurrentStatus(Instrument(), Text).Should().Be("Field.Status : EquipmentStatus.Operational");

        EquipmentScreenRules.FieldOf(Error.Regulatory("Calibration_CalibratorLotRequired")).Should().Be(EquipmentScreenRules.LotField);
        EquipmentScreenRules.FieldOf(Error.Validation("Equipment_CalibrationOverdue")).Should().BeNull();
    }

    // ---- Contrats automates ------------------------------------------------------------------------

    internal static InstrumentContractRow Contract(string reference = "CTR-2024-07", bool atRisk = false, bool endingSoon = false,
                                                   int daysToEnd = 400, bool active = true, int delays = 0,
                                                   CommitmentBasis basis = CommitmentBasis.TestsPerYear, string? file = null) =>
        new(Guid.NewGuid(), reference, Guid.NewGuid(), "Biosystems Maroc", InstrumentContractKind.Placement,
            new DateOnly(2024, 1, 1), Today.AddDays(daysToEnd), daysToEnd, active, endingSoon, "AUT-01, AUT-02",
            new CommitmentStatus(basis, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), 40_000m, 32_000m, 26_000m,
                                 32_500m, 0.8m, basis == CommitmentBasis.None ? null : 0.8125m, atRisk),
            3, 12.5m, delays, 48, file);

    internal static ContractInterventionRow Intervention(bool exceeded = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "AUT-01", new DateOnly(2026, 10, 12), MaintenanceKind.Corrective, "Pompe remplacée",
            6m, new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 12, 14, 0, 0, TimeSpan.Zero),
            54m, exceeded, "M. Idrissi");

    [Fact]
    public void A_contract_action_needs_its_right_and_what_it_acts_on()
    {
        var all = InstrumentContractScreenRules.Actions(_ => true, Contract(file: "contrat.pdf"), Intervention());
        all.Should().Be(new InstrumentContractScreenActions(true, true, true, true, true, true, true));

        var none = InstrumentContractScreenRules.Actions(_ => true, null, null);
        none.Should().Be(new InstrumentContractScreenActions(true, true, true, false, false, false, false));

        var reader = InstrumentContractScreenRules.Actions(p => p == Permissions.Equipment.ViewContracts, Contract(file: "c.pdf"), Intervention());
        reader.Should().Be(new InstrumentContractScreenActions(false, false, false, false, false, true, false), "the file is read with the screen's right");
        InstrumentContractScreenRules.Actions(_ => true, Contract(), null).Times.Should().BeFalse();
    }

    [Fact]
    public void The_dashboard_says_the_commitment_at_risk_the_end_near_and_the_delays_exceeded()
    {
        var risk = Contract(atRisk: true, endingSoon: true, daysToEnd: 45, delays: 2);
        InstrumentContractScreenRules.Alerts(risk, Text).Should().Be("Contracts.AtRisk · Contracts.EndingSoon · Contracts.DelaysExceeded(2)");
        InstrumentContractScreenRules.AlertTone(risk).Should().Be(StockTone.Critical);
        InstrumentContractScreenRules.RowInk(risk).Should().Be(StockInk.Critical);
        InstrumentContractScreenRules.RatioInk(risk).Should().Be(StockInk.CriticalBold);
        InstrumentContractScreenRules.EndInk(risk).Should().Be(StockInk.Plain, "at risk, the whole row carries it");

        var ending = Contract(endingSoon: true, daysToEnd: 45);
        InstrumentContractScreenRules.AlertTone(ending).Should().Be(StockTone.Warning);
        InstrumentContractScreenRules.EndInk(ending).Should().Be(StockInk.AbnormalBold);
        InstrumentContractScreenRules.End(ending, Text).Should().Be($"{Today.AddDays(45):dd/MM/yy} (45 Quotes.DaysShort)");

        var ended = Contract(daysToEnd: -3);
        InstrumentContractScreenRules.Alerts(ended, Text).Should().Be("Contracts.Expired");
        InstrumentContractScreenRules.AlertTone(ended).Should().Be(StockTone.Waiting);
        InstrumentContractScreenRules.RowInk(ended).Should().Be(StockInk.Muted);
        InstrumentContractScreenRules.End(ended, Text).Should().NotContain("(");
        InstrumentContractScreenRules.Alerts(Contract(active: false), Text).Should().Be("Contracts.Inactive");
        InstrumentContractScreenRules.AlertTone(Contract()).Should().BeNull();

        InstrumentContractScreenRules.Count([risk, ending, Contract()], Text)
            .Should().Be("StocksUi.Contracts(3) · Contracts.AtRisk 1 · Contracts.EndingSoon 2");
        InstrumentContractScreenRules.AlertRule(Text).Should().Be("Contracts.AlertRule(90,90)");
    }

    [Fact]
    public void Tests_are_counted_whole_and_purchases_written_as_money()
    {
        var culture = CultureInfo.InvariantCulture;
        var tests = Contract().Commitment;
        InstrumentContractScreenRules.Figure(26_000m, tests, m => $"{m} DH", culture).Should().Be("26,000");
        InstrumentContractScreenRules.Ratio(tests, culture).Should().Be("81 %");
        InstrumentContractScreenRules.Period(tests).Should().Be("01/01/26–31/12/26");

        var money = Contract(basis: CommitmentBasis.PurchaseAmountPerYear).Commitment;
        InstrumentContractScreenRules.Figure(26_000m, money, m => m.ToString("0.00", culture) + " DH", culture).Should().Be("26000.00 DH");

        var none = Contract(basis: CommitmentBasis.None).Commitment;
        InstrumentContractScreenRules.Figure(26_000m, none, m => "x", culture).Should().BeEmpty();
        InstrumentContractScreenRules.Ratio(none, culture).Should().BeEmpty();
        InstrumentContractScreenRules.Period(none).Should().BeEmpty();

        InstrumentContractScreenRules.Maintenance(Contract(), Text, culture).Should().Be("Contracts.MaintenanceSummary(3,12.5)");
    }

    [Fact]
    public void An_intervention_reads_against_the_delay_the_contract_allows()
    {
        var cells = InstrumentContractScreenRules.InterventionCells(Intervention(exceeded: true), Text, CultureInfo.InvariantCulture);
        cells.Should().HaveCount(9);
        cells[0].Should().Be("12/10/26");
        cells[2].Should().Be("MaintenanceKind.Corrective");
        cells[7].Should().Be("54");
        InstrumentContractScreenRules.DelayInk(Intervention(exceeded: true)).Should().Be(StockInk.CriticalBold);
        InstrumentContractScreenRules.DelayInk(Intervention()).Should().Be(StockInk.Plain);
        InstrumentContractScreenRules.InterventionsNote(Contract(), Text).Should().Be("CTR-2024-07 · Contracts.MaxDelayShort(48)");
        InstrumentContractScreenRules.InterventionsNote(null, Text).Should().BeEmpty();
    }

    [Fact]
    public void A_contract_is_refused_on_its_box()
    {
        var supplier = Guid.NewGuid();
        var item = Guid.NewGuid();
        var form = InstrumentContractScreenRules.Form(null, Today);
        form.Start.Should().Be(Today);
        form.DurationMonths.Should().Be(60);
        form.Basis.Should().Be(CommitmentBasis.TestsPerYear);

        InstrumentContractScreenRules.Check(form, null).Refusal!.Field.Should().Be(InstrumentContractScreenRules.ReferenceField);
        form.Reference = " CTR-2026-01 ";
        InstrumentContractScreenRules.Check(form, null).Refusal!.Key.Should().Be("Contract_SupplierRequired");
        form.SupplierId = supplier;
        InstrumentContractScreenRules.Check(form, null).Refusal!.Key.Should().Be("Contract_CommitmentRequired");
        form.Committed = "40000 tests";
        InstrumentContractScreenRules.Check(form, null).Refusal!.Should().Match<Application.Input.FieldRefusal>(
            r => r.Field == InstrumentContractScreenRules.CommittedField && r.Key == "Result_NotANumber" && r.Typed == "40000 tests");
        form.Committed = "40000";
        form.PenaltyAmount = "5000 DH";
        InstrumentContractScreenRules.Check(form, null).Refusal!.Field.Should().Be(InstrumentContractScreenRules.PenaltyAmountField);
        form.PenaltyAmount = "5000";
        form.MaxDelayHours = "-4";
        InstrumentContractScreenRules.Check(form, null).Refusal!.Should().Match<Application.Input.FieldRefusal>(
            r => r.Field == InstrumentContractScreenRules.MaxDelayField && r.Key == "Contract_NegativeTerm");
        form.MaxDelayHours = "48";
        form.DurationMonths = 300;
        InstrumentContractScreenRules.Check(form, null).Refusal!.Key.Should().Be("Contract_DurationOutOfRange");
        form.DurationMonths = 60;
        form.Reagents.Add(new ContractReagentForm { StockItemId = item, Price = "-2" });
        form.Reagents.Add(new ContractReagentForm { Price = "ignored, no item" });
        InstrumentContractScreenRules.Check(form, null).Refusal!.Field.Should().Be(InstrumentContractScreenRules.ReagentField("price", 0));
        form.Reagents[0].Price = "112,50";
        form.Reagents[0].CostPerTest = "1.25";
        form.InstrumentIds.Add(Guid.NewGuid());

        var request = InstrumentContractScreenRules.Check(form, null).Request!;
        request.Reference.Should().Be("CTR-2026-01");
        request.CommittedVolume.Should().Be(40_000m);
        request.PenaltyAmountMad.Should().Be(5000m);
        request.MaxInterventionDelayHours.Should().Be(48);
        request.PreventiveVisitsPerYear.Should().BeNull();
        request.Reagents.Should().ContainSingle().Which.Should().Be(new InstrumentContractReagentInput(item, 112.5m, 1.25m));
        request.InstrumentIds.Should().HaveCount(1);

        form.Basis = CommitmentBasis.None;
        form.Committed = null;
        InstrumentContractScreenRules.Check(form, null).Request!.CommittedVolume.Should().Be(0m);

        InstrumentContractScreenRules.FieldOf(Error.Conflict("Contract_ReferenceExists", "X")).Should().Be(InstrumentContractScreenRules.ReferenceField);
    }

    [Fact]
    public void A_contract_edited_shows_what_was_saved()
    {
        var item = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var detail = new InstrumentContractDetail(Guid.NewGuid(), "CTR-1", Guid.NewGuid(), InstrumentContractKind.Comodat, new DateOnly(2024, 1, 1), 36,
            CommitmentBasis.PurchaseAmountPerYear, 250_000m, "GLU,CREA", 2, 48, "Préventive 2/an", null, null, null, 1500m, null, false,
            [Guid.NewGuid()], [new InstrumentContractReagentInput(item, 112.5m, null), new InstrumentContractReagentInput(gone, 3m, null)]);
        var options = new ContractOptions([], [], [new ContractLookup(item, "R-1", new MultilingualText("Réactif"))]);

        var form = InstrumentContractScreenRules.Form(detail, Today, options);
        form.Committed.Should().Be("250000");
        form.PenaltyAmount.Should().Be("1500.00");
        form.PreventiveVisits.Should().Be("2");
        form.IsActive.Should().BeFalse();
        form.Reagents.Should().ContainSingle().Which.Price.Should().Be("112.50");

        InstrumentContractScreenRules.Display(new ContractLookup(item, "R-1", new MultilingualText("Réactif", "كاشف"), false), AppLanguage.Arabic, Text)
            .Should().Be("R-1 — كاشف Contracts.InactiveSuffix");
    }

    [Fact]
    public void A_contract_file_is_one_of_the_kinds_kept()
    {
        InstrumentContractScreenRules.CheckFile("contrat.pdf", 1200).Should().BeNull();
        InstrumentContractScreenRules.CheckFile("contrat.exe", 1200)!.Key.Should().Be("Purchasing_FileType");
        InstrumentContractScreenRules.CheckFile("contrat.pdf", 0)!.Key.Should().Be("Purchasing_FileEmpty");
        InstrumentContractScreenRules.CheckFile("contrat.pdf", MarocLIS.Domain.Inventory.PurchasingDocument.MaximumBytes + 1)!.Key.Should().Be("Purchasing_FileTooLarge");
        InstrumentContractScreenRules.FileAccept.Should().Contain(".pdf").And.Contain(".docx");
        InstrumentContractScreenRules.ReplaceQuestion(Contract(file: "v1.pdf"), Text).Should().Be("Contracts.ReplaceFileQuestion(v1.pdf)");
        InstrumentContractScreenRules.ReplaceQuestion(Contract(), Text).Should().BeNull();
    }

    [Fact]
    public void The_times_open_on_the_morning_of_the_intervention_and_the_arrival_follows_the_call()
    {
        var row = new ContractInterventionRow(Guid.NewGuid(), Guid.NewGuid(), "AUT-01", new DateOnly(2026, 10, 12), MaintenanceKind.Corrective,
                                              "Pompe", null, null, null, null, false, null);
        var form = InstrumentContractScreenRules.TimesForm(row, new DateTime(2026, 10, 12, 9, 15, 0));
        form.HasRequested.Should().BeFalse();
        form.Requested.Should().Be(new DateTime(2026, 10, 12, 8, 0, 0));
        form.Arrived.Should().Be(new DateTime(2026, 10, 12, 9, 15, 0), "never later than now");
        InstrumentContractScreenRules.TimesTitle(row).Should().Be("AUT-01 · 12/10/2026");

        var now = new DateTimeOffset(new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Local));
        form.HasRequested = form.HasArrived = true;
        form.Arrived = new DateTime(2026, 10, 12, 7, 0, 0);
        InstrumentContractScreenRules.Check(form, now).Refusal!.Key.Should().Be("Contract_ArrivalBeforeRequest");
        form.Arrived = new DateTime(2026, 10, 19, 7, 0, 0);
        InstrumentContractScreenRules.Check(form, now).Refusal!.Should().Match<Application.Input.FieldRefusal>(
            r => r.Field == InstrumentContractScreenRules.ArrivedField && r.Key == "Contract_InterventionTimeInFuture");
        form.Arrived = new DateTime(2026, 10, 12, 14, 0, 0);
        var (refusal, requested, arrived) = InstrumentContractScreenRules.Check(form, now);
        refusal.Should().BeNull();
        (arrived - requested).Should().Be(TimeSpan.FromHours(6));
        form.HasRequested = false;
        InstrumentContractScreenRules.Check(form, now).Requested.Should().BeNull();
    }
}
