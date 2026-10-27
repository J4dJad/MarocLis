using System.Globalization;
using FluentAssertions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using MarocLIS.Tests.Licensing;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Référentiels » and of its editors, moved out of the workstation's MasterDataView,
/// MasterDataEditors and WestgardRuleDialog, now shared with the web.
/// </summary>
public sealed class MasterDataScreenRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);

    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    internal static InstrumentMasterRow Instrument(EquipmentKind kind = EquipmentKind.Analyser, bool incomplete = false,
                                                   EquipmentStatus status = EquipmentStatus.Operational, int? calibration = 30) =>
        new(Guid.NewGuid(), "AU480", new MultilingualText("Beckman AU480"), kind, status, "Beckman", "AU480", "SN-1",
            "Biochimie", 90, calibration, 30, Today.AddDays(-10), Today.AddYears(1), true, 2, incomplete);

    internal static QcMaterialRow Material(bool active = true, bool usable = true, int days = 120, int targets = 2) =>
        new(Guid.NewGuid(), "L-2026-04", new MultilingualText("Lyphochek niveau 1"), QcLevel.Normal, Today.AddDays(days),
            days, "Bio-Rad", "C-310", null, 30, active, usable, targets);

    internal static QcTargetRow Target(bool usable = true, bool active = true, decimal? labMean = 5.1m,
                                       WestgardRule rules = MasterDataScreenRules.ClassicMultirule) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "L-2026-04", QcLevel.Normal, Guid.NewGuid(), "GLY",
            new MultilingualText("Glucose"), Guid.NewGuid(), "AU480", 5.12m, 0.15m, labMean, labMean is null ? null : 0.14m,
            labMean is null ? 0 : 20, 4m, rules, active, usable);

    internal static PayerRow Payer(PayerKind kind = PayerKind.AmoCnss, decimal rate = 0.7m, bool tnr = true,
                                   bool thirdParty = false, string? agreement = null, bool expired = false,
                                   bool active = true) =>
        new(Guid.NewGuid(), "CNSS", new MultilingualText("CNSS — AMO", "الصندوق الوطني"), kind, rate, tnr, thirdParty,
            agreement, null, expired, 60, true, false, null, null, null, null, null, "Casablanca", active, 10, 2);

    internal static PrescriberRow Prescriber(PrescriberKind kind = PrescriberKind.GeneralPractitioner, string? inpe = null,
                                             bool active = true, string? arabic = null) =>
        new(Guid.NewGuid(), "EL AMRANI Karim", arabic, kind, new MultilingualText("Médecine générale"), "12345", inpe,
            null, "0661000000", "k.elamrani@example.ma", "Rabat", ReportDeliveryPreference.PatientCollects, false, null,
            active, 4);

    // ---- Rights and completeness ---------------------------------------------------------------

    [Fact]
    public void Each_tab_is_kept_by_its_own_right()
    {
        MasterDataScreenRules.ManageRight(MasterDataTab.Instruments).Should().Be(Permissions.Equipment.Manage);
        MasterDataScreenRules.ManageRight(MasterDataTab.QualityControl).Should().Be(Permissions.Quality.ManageQcRules);
        MasterDataScreenRules.ManageRight(MasterDataTab.Stock).Should().Be(Permissions.Inventory.ManagePurchaseOrders);
        MasterDataScreenRules.ManageRight(MasterDataTab.Suppliers).Should().Be(Permissions.Inventory.ManagePurchaseOrders);
        MasterDataScreenRules.ManageRight(MasterDataTab.Prescribers).Should().Be(Permissions.Catalog.Manage);
        MasterDataScreenRules.ManageRight(MasterDataTab.Payers).Should().Be(Permissions.Billing.ManageTariffs);

        var billing = new LicenceTestKit.User(true, Permissions.Billing.ManageTariffs);
        MasterDataScreenRules.CanManage(billing, MasterDataTab.Payers).Should().BeTrue();
        MasterDataScreenRules.CanManage(billing, MasterDataTab.Prescribers).Should().BeFalse();
    }

    [Fact]
    public void A_secretary_is_shown_only_the_tabs_she_may_read_and_the_screen_opens_on_the_first()
    {
        // Before: the screen opened on the analysers' tab and answered « Vous n'avez pas le droit ».
        var secretary = new LicenceTestKit.User(true, Permissions.Orders.Create, Permissions.Billing.View);
        MasterDataScreenRules.VisibleTabs(secretary).Should().Equal(MasterDataTab.Prescribers, MasterDataTab.Payers);

        var engineer = new LicenceTestKit.User(true, Permissions.Equipment.Manage);
        MasterDataScreenRules.VisibleTabs(engineer).Should().Equal(MasterDataTab.Instruments);

        var everything = new LicenceTestKit.User(true, Permissions.Equipment.View, Permissions.Quality.View,
                                                 Permissions.Inventory.View, Permissions.Orders.View, Permissions.Billing.View);
        MasterDataScreenRules.VisibleTabs(everything).Should().Equal(Enum.GetValues<MasterDataTab>());

        // The lists she may not read are empty for her, not for the laboratory: not named as missing.
        var blockers = MasterDataScreenRules.Blockers(0, 0, 0, 0, 0, [Payer(rate: 0m)]);
        MasterDataScreenRules.VisibleBlockers(blockers, [MasterDataTab.Prescribers, MasterDataTab.Payers])
            .Should().Equal("Order.Prescribers", "Coverage.Payers");
    }

    [Fact]
    public void The_completeness_names_each_screen_the_reference_data_leaves_unusable()
    {
        var none = MasterDataScreenRules.Blockers(0, 1, 0, 0, 0, [Payer(rate: 0m)]);
        none.Should().Equal("Nav.Equipment", "Nav.QualityControl", "Nav.Inventory", "Order.Prescribers", "Coverage.Payers");
        MasterDataScreenRules.Completeness(none.Take(2).ToList(), Text)
            .Should().Be("⚠ Admin.MasterDataIncomplete — Nav.Equipment · Nav.QualityControl");

        var all = MasterDataScreenRules.Blockers(1, 1, 1, 1, 1, [Payer()]);
        all.Should().BeEmpty();
        MasterDataScreenRules.Completeness(all, Text).Should().Be("✓ Admin.MasterDataComplete");
    }

    // ---- Rows ----------------------------------------------------------------------------------

    [Fact]
    public void An_instrument_without_its_intervals_reads_critical_and_a_decommissioned_one_grey()
    {
        MasterDataScreenRules.Tone(Instrument(incomplete: true)).Should().Be(MasterDataTone.Critical);
        MasterDataScreenRules.Tone(Instrument(status: EquipmentStatus.Decommissioned)).Should().Be(MasterDataTone.Muted);
        MasterDataScreenRules.Tone(Instrument()).Should().Be(MasterDataTone.Normal);
        MasterDataScreenRules.Interval(30, Text).Should().Be("Stock.Days(30)");
        MasterDataScreenRules.Interval(null, Text).Should().Be("—");
    }

    [Fact]
    public void A_control_material_reads_by_its_usability_its_expiry_and_its_targets()
    {
        MasterDataScreenRules.Tone(Material(usable: false)).Should().Be(MasterDataTone.Critical);
        MasterDataScreenRules.Tone(Material(days: 20)).Should().Be(MasterDataTone.Abnormal);
        MasterDataScreenRules.Tone(Material(targets: 0)).Should().Be(MasterDataTone.Abnormal);
        MasterDataScreenRules.Tone(Material()).Should().Be(MasterDataTone.Normal);

        MasterDataScreenRules.StateKey(Material(active: false)).Should().Be("Admin.Inactive");
        MasterDataScreenRules.StateKey(Material(days: -1)).Should().Be("Inventory.Expired");
        MasterDataScreenRules.StateKey(Material(targets: 0)).Should().Be("Quality.NoTarget");
        MasterDataScreenRules.StateKey(Material()).Should().Be("Inventory.Usable");
    }

    [Fact]
    public void A_target_reads_by_the_limits_it_runs_on()
    {
        MasterDataScreenRules.Tone(Target(usable: false)).Should().Be(MasterDataTone.Critical);
        MasterDataScreenRules.StateKey(Target(usable: false)).Should().Be("Qc_NoTargetEstablished");
        MasterDataScreenRules.Tone(Target(active: false)).Should().Be(MasterDataTone.Muted);
        MasterDataScreenRules.Tone(Target(labMean: null)).Should().Be(MasterDataTone.Warning);
        MasterDataScreenRules.StateKey(Target(labMean: null)).Should().Be("Quality.SupplierLimits");
        MasterDataScreenRules.StateKey(Target()).Should().Be("Quality.LaboratoryLimits");

        MasterDataScreenRules.Limits(5.12m, 0.15m, CultureInfo.InvariantCulture).Should().Be("5.120 ± 0.150");
        MasterDataScreenRules.Limits(null, 0.15m, CultureInfo.InvariantCulture).Should().Be("—");
    }

    [Fact]
    public void Stock_suppliers_prescribers_and_payers_read_as_on_the_workstation()
    {
        var item = new StockItemMasterRow(Guid.NewGuid(), "R-GLY", new MultilingualText("Glucose GOD-PAP"),
            StockItemKind.Reagent, "kit", 2m, 1m, true, StorageCondition.Refrigerated, "Sothema", true, true, 0);
        MasterDataScreenRules.Tone(item).Should().Be(MasterDataTone.Strong);
        MasterDataScreenRules.Tone(item with { IsActive = false }).Should().Be(MasterDataTone.Muted);
        MasterDataScreenRules.Flags(item).Should().Be("★ ☣ ∅");
        MasterDataScreenRules.Flags(item with { IsCritical = false, IsHazardous = false, LotCount = 3 }).Should().BeEmpty();

        var supplier = new SupplierRow(Guid.NewGuid(), "SOTH", "Sothema", null, null, null, null, "Casablanca", 7, true,
            new DateOnly(2026, 3, 12), 85, false, true, 4);
        MasterDataScreenRules.Evaluation(supplier).Should().Be("12/03/26 (85)");
        MasterDataScreenRules.Evaluation(supplier with { LastEvaluationDate = null }).Should().Be("—");
        MasterDataScreenRules.Tone(supplier with { IsEvaluationOverdue = true }).Should().Be(MasterDataTone.Abnormal);
        MasterDataScreenRules.Tone(supplier with { IsActive = false }).Should().Be(MasterDataTone.Muted);

        MasterDataScreenRules.Tone(Prescriber()).Should().Be(MasterDataTone.Abnormal);
        MasterDataScreenRules.Tone(Prescriber(inpe: "123456789")).Should().Be(MasterDataTone.Normal);
        MasterDataScreenRules.Tone(Prescriber(PrescriberKind.Dentist)).Should().Be(MasterDataTone.Normal);
        MasterDataScreenRules.Tone(Prescriber(active: false)).Should().Be(MasterDataTone.Muted);
        MasterDataScreenRules.Name(Prescriber(arabic: "كريم العمراني"), AppLanguage.Arabic).Should().Be("كريم العمراني");
        MasterDataScreenRules.Name(Prescriber(arabic: "كريم العمراني"), AppLanguage.French).Should().Be("EL AMRANI Karim");

        MasterDataScreenRules.Tone(Payer(expired: true)).Should().Be(MasterDataTone.Critical);
        MasterDataScreenRules.Tone(Payer(tnr: false)).Should().Be(MasterDataTone.Abnormal);
        MasterDataScreenRules.Tone(Payer(active: false, expired: true)).Should().Be(MasterDataTone.Muted);
        MasterDataScreenRules.Tone(Payer()).Should().Be(MasterDataTone.Normal);
        MasterDataScreenRules.Rate(Payer(rate: 0.875m), CultureInfo.InvariantCulture).Should().Be("87.5 %");
        MasterDataScreenRules.Problems(Payer(thirdParty: true, tnr: false), Text)
            .Should().Be("Payer.AgreementNumberMissing · Payer.AmoWithoutReferenceTariff");
    }

    // ---- Westgard ------------------------------------------------------------------------------

    [Fact]
    public void The_westgard_dialog_lists_every_rule_and_warns_of_a_control_that_never_rejects()
    {
        MasterDataScreenRules.WestgardRules.Should().HaveCount(11);
        MasterDataScreenRules.WestgardRules.Select(r => r.Rule).Should().OnlyHaveUniqueItems();
        MasterDataScreenRules.CountRules(MasterDataScreenRules.ClassicMultirule).Should().Be(6);
        MasterDataScreenRules.CountRules(MasterDataScreenRules.SingleRule).Should().Be(1);

        MasterDataScreenRules.Combine([WestgardRule.OneThreeS, WestgardRule.R4S])
            .Should().Be(WestgardRule.OneThreeS | WestgardRule.R4S);
        MasterDataScreenRules.Combine([]).Should().Be(WestgardRule.None);

        MasterDataScreenRules.RuleSummary(WestgardRule.None, Text).Should().Be("Quality.NoRulesWarning");
        MasterDataScreenRules.RuleSummary(MasterDataScreenRules.ClassicMultirule, Text).Should().Be("Quality.RuleCount(6)");
    }

    // ---- Editors -------------------------------------------------------------------------------

    [Fact]
    public void The_instrument_editor_refuses_an_analyser_without_a_calibration_interval_and_keeps_the_english_name()
    {
        var form = MasterDataEditorRules.FormOf((InstrumentMasterRow?)null, null);
        form.Kind.Should().Be(EquipmentKind.Analyser);
        MasterDataEditorRules.Check(form)!.Field.Should().Be("code");
        MasterDataEditorRules.Check(form with { Code = "AU" })!.Field.Should().Be("namefr");
        MasterDataEditorRules.Check(form with { Code = "AU", NameFr = "AU480" })!.Key
            .Should().Be("Equipment_AnalyserNeedsCalibrationInterval");
        MasterDataEditorRules.Check(form with { Code = "AU", NameFr = "AU480", Kind = EquipmentKind.Centrifuge })
            .Should().BeNull();

        var row = Instrument();
        var stored = new InstrumentRequest
        {
            Code = "AU480", Name = new MultilingualText("Beckman AU480", null, "Beckman AU480 analyser"),
            SoftwareVersion = "v3.2"
        };
        var opened = MasterDataEditorRules.FormOf(row, stored);
        opened.SoftwareVersion.Should().Be("v3.2");
        opened.CalibrationIntervalDays.Should().Be(30);

        var kept = MasterDataEditorRules.ToRequest(opened with { MaintenanceIntervalDays = 0 }, row.InstrumentId, stored);
        kept.Name.En.Should().Be("Beckman AU480 analyser");
        kept.PreventiveMaintenanceIntervalDays.Should().BeNull();
        MasterDataEditorRules.ToRequest(opened with { NameFr = "Automate AU480" }, row.InstrumentId, stored).Name.En
            .Should().BeNull();
    }

    [Fact]
    public void The_control_editors_check_their_numbers_their_key_and_their_rules()
    {
        MasterDataEditorRules.FormOf((QcMaterialRow?)null, Today).ExpiryDate.Should().Be(Today.AddMonths(12));
        MasterDataEditorRules.KeyEditable((QcMaterialRow?)null).Should().BeTrue();
        MasterDataEditorRules.KeyEditable(Material()).Should().BeFalse();
        MasterDataEditorRules.Check(new QcMaterialForm())!.Field.Should().Be("lot");
        MasterDataEditorRules.Check(new QcMaterialForm { LotNumber = "L1" })!.Field.Should().Be("namefr");

        MasterDataEditorRules.MissingPrerequisite(0, 1, 1).Should().Be("Quality.NeedMaterialFirst");
        MasterDataEditorRules.MissingPrerequisite(1, 0, 1).Should().Be("Quality.NeedInstrumentFirst");
        MasterDataEditorRules.MissingPrerequisite(1, 1, 0).Should().Be("Quality.NeedParameterFirst");
        MasterDataEditorRules.MissingPrerequisite(1, 1, 1).Should().BeNull();

        var fresh = MasterDataEditorRules.FormOf((QcTargetRow?)null, CultureInfo.InvariantCulture);
        fresh.Rules.Should().Be(MasterDataScreenRules.ClassicMultirule);
        MasterDataEditorRules.Check(fresh with { Mean = "5,1x" })
            .Should().Be(new FieldRefusal("mean", "Result_NotANumber", Typed: "5,1x"));
        MasterDataEditorRules.Check(fresh)!.Key.Should().Be("Message.NothingSelected");

        var complete = fresh with { MaterialId = Guid.NewGuid(), InstrumentId = Guid.NewGuid(), ParameterId = Guid.NewGuid() };
        MasterDataEditorRules.Check(complete with { Rules = WestgardRule.None })!.Field.Should().Be("rules");
        MasterDataEditorRules.Check(complete).Should().BeNull();

        var opened = MasterDataEditorRules.FormOf(Target(), CultureInfo.InvariantCulture);
        opened.Mean.Should().Be("5.12");
        MasterDataEditorRules.ToRequest(opened, null).AssignedSd.Should().Be(0.15m);

        MasterDataEditorRules.CvEcho("5", "0.125", CultureInfo.InvariantCulture).Should().Be("CV 2.5 %");
        MasterDataEditorRules.CvEcho("5", "", CultureInfo.InvariantCulture).Should().BeEmpty();
        MasterDataEditorRules.CvEcho("0", "1", CultureInfo.InvariantCulture).Should().BeEmpty();
    }

    [Fact]
    public void The_article_editor_refuses_a_reorder_point_below_the_safety_stock()
    {
        var form = MasterDataEditorRules.FormOf((StockItemMasterRow?)null, CultureInfo.InvariantCulture) with { Code = "R-GLY", NameFr = "Glucose" };
        form.Unit.Should().Be("u");
        MasterDataEditorRules.HazardClassEnabled(form).Should().BeFalse();

        MasterDataEditorRules.Check(form with { Maximum = "beaucoup" })!.Field.Should().Be("maximum");
        MasterDataEditorRules.Check(form with { Unit = " " })!.Key.Should().Be("Stock_UnitRequired");
        MasterDataEditorRules.Check(form with { Minimum = "10", Reorder = "5" })!.Key.Should().Be("Stock_ReorderBelowMinimum");
        MasterDataEditorRules.Check(form with { DeadVolumePercent = "x" })!.Field.Should().Be("deadpercent");
        MasterDataEditorRules.Check(form with { Minimum = "5", Reorder = "10" }).Should().BeNull();

        var request = MasterDataEditorRules.ToRequest(form with { Minimum = "5", Reorder = "10", TestsPerPack = 0,
                                                                  DeadVolumePercent = "2.5" }, null);
        request.MinimumStock.Should().Be(5m);
        request.MaximumStock.Should().BeNull();
        request.TestsPerPack.Should().BeNull();
        request.ConsumptionPolicy!.DeadVolumePercent.Should().Be(2.5m);
    }

    [Fact]
    public void The_supplier_editor_wants_an_evaluation_behind_an_approval()
    {
        var form = MasterDataEditorRules.FormOf((SupplierRow?)null) with { Code = "SOTH", Name = "Sothema", Approved = true };
        form.LeadTimeDays.Should().Be(7);
        MasterDataEditorRules.ApprovalNeedsEvaluation(form).Should().BeTrue();
        MasterDataEditorRules.Check(form)!.Field.Should().Be("evaluation");
        MasterDataEditorRules.Check(form with { LastEvaluationDate = Today }).Should().BeNull();

        MasterDataEditorRules.RegistrationExpired(form with { RegistrationValidUntil = Today.AddDays(-1) }, Today).Should().BeTrue();
        MasterDataEditorRules.RegistrationExpired(form with { RegistrationValidUntil = Today }, Today).Should().BeFalse();

        var request = MasterDataEditorRules.ToRequest(form with { LastEvaluationDate = Today, Ice = "  ", EvaluationScore = 0 }, null);
        request.Ice.Should().BeNull();
        request.EvaluationScore.Should().BeNull();
    }

    [Fact]
    public void The_prescriber_editor_opens_on_the_stored_names_and_wants_the_address_a_report_goes_to()
    {
        var typed = MasterDataEditorRules.FormOf(null, null, "BENNANI Salma Zineb", CultureInfo.InvariantCulture);
        typed.LastName.Should().Be("BENNANI");
        typed.FirstName.Should().Be("Salma Zineb");

        var row = Prescriber();
        var stored = new PrescriberRequest { LastName = "EL AMRANI", FirstName = "Karim", MobilePhone = "0661000000" };
        var opened = MasterDataEditorRules.FormOf(row, stored, culture: CultureInfo.InvariantCulture);
        opened.LastName.Should().Be("EL AMRANI");
        opened.FirstName.Should().Be("Karim");

        MasterDataEditorRules.DeliveryContactMissing(opened with { Delivery = ReportDeliveryPreference.Email, Email = "" })
            .Should().BeTrue();
        MasterDataEditorRules.DeliveryContactMissing(opened with { Delivery = ReportDeliveryPreference.SmsLink }).Should().BeFalse();
        MasterDataEditorRules.DeliveryContactMissing(opened with { Delivery = ReportDeliveryPreference.Fax }).Should().BeTrue();

        MasterDataEditorRules.Check(opened with { Discount = "dix" })!.Field.Should().Be("discount");
        MasterDataEditorRules.Check(opened with { LastName = " " })!.Field.Should().Be("lastname");
        MasterDataEditorRules.Check(opened).Should().BeNull();

        MasterDataEditorRules.ToRequest(opened with { Discount = "10" }, row.PrescriberId, stored).DiscountPercent
            .Should().Be(10m);
    }

    [Fact]
    public void The_payer_editor_follows_its_kind_and_sends_the_rate_as_a_fraction()
    {
        MasterDataEditorRules.Fields(PayerKind.SelfPay).Should().Be(new PayerKindFields(false, false, false));
        MasterDataEditorRules.Fields(PayerKind.AmoCnops).Should().Be(new PayerKindFields(true, true, true));
        MasterDataEditorRules.Fields(PayerKind.Mutuelle).Should().Be(new PayerKindFields(true, true, false));

        var form = MasterDataEditorRules.FormOf((PayerRow?)null) with { Code = "cnss", NameFr = "CNSS", ThirdParty = true,
                                                             AgreementNumber = "C-12", UsesReferenceTariff = false };
        var self = MasterDataEditorRules.WithKind(form, PayerKind.SelfPay);
        self.RatePercent.Should().Be(0m);
        self.ThirdParty.Should().BeFalse();
        MasterDataEditorRules.AgreementEnabled(self).Should().BeFalse();
        MasterDataEditorRules.WithKind(form, PayerKind.AmoTadamon).UsesReferenceTariff.Should().BeTrue();

        MasterDataEditorRules.Check(form with { Code = "" })!.Field.Should().Be("code");
        MasterDataEditorRules.Check(form with { NameFr = "" })!.Key.Should().Be("Payer_NameFrenchRequired");

        var request = MasterDataEditorRules.ToRequest(form with { RatePercent = 80m }, null);
        request.Code.Should().Be("CNSS");
        request.DefaultReimbursementRate.Should().Be(0.8m);
        request.AgreementNumber.Should().Be("C-12");
        MasterDataEditorRules.ToRequest(form with { ThirdParty = false }, null).AgreementNumber.Should().BeNull();

        MasterDataEditorRules.FormOf(Payer(rate: 0.9m)).RatePercent.Should().Be(90m);
    }
}
