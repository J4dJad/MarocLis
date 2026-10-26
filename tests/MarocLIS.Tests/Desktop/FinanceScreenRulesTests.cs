using FluentAssertions;
using MarocLIS.Application.Accounting;
using MarocLIS.Application.Assets;
using MarocLIS.Application.Input;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Assets;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Export comptable », « Comptes et journaux » and « Immobilisations », moved out of
/// the workstation's views and dialogs, now shared with the web.
/// </summary>
public sealed class FinanceScreenRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 18);

    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    private static string Amount(decimal value) => value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " DH";

    internal static readonly AccountingSiteOption Casablanca = new(Guid.NewGuid(), "CAS", "Casablanca Centre");
    internal static readonly AccountingSiteOption Rabat = new(Guid.NewGuid(), "RAB", "Rabat Agdal");

    internal static JournalEntry Entry(string journal, AccountingJournals kind, decimal debit, decimal credit, string piece = "FA-1") =>
        new(new DateOnly(2026, 9, 30), journal, piece, "Facture " + piece, kind, "CAS",
            [new AccountingEntry(new DateOnly(2026, 9, 30), journal, "3421", null, "Facture", piece, debit, 0m),
             new AccountingEntry(new DateOnly(2026, 9, 30), journal, "7124", null, "Facture", piece, 0m, credit)]);

    internal static FixedAssetSource Source(string code = "IMM-2026-001", DateOnly? disposal = null, Guid? instrument = null) =>
        new(Guid.NewGuid(), code, "Automate de biochimie", FixedAssetCategory.LaboratoryEquipment, Guid.NewGuid(),
            new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 10), 600_000m, 120_000m, false, DepreciationMethod.StraightLine,
            5, 0m, disposal, disposal is null ? null : 10_000m, disposal is null ? null : "Cession", "Bio-Distribution", "FA-118",
            instrument, instrument is null ? null : "AUT-01");

    internal static FixedAssetRegisterRow Row(FixedAssetSource? source = null) =>
        new(source ?? Source(), FixedAssetSettings.Defaults(FixedAssetCategory.LaboratoryEquipment), 0.2m, 0m, 96_000m, 96_000m,
            624_000m, null);

    // ---- Export comptable ----------------------------------------------------------------------------

    [Fact]
    public void The_export_opens_on_the_last_calendar_month_every_site_and_every_journal()
    {
        var form = AccountingExportScreenRules.NewForm(Today, [Casablanca, Rabat]);

        form.From.Should().Be(new DateOnly(2026, 9, 1));
        form.To.Should().Be(new DateOnly(2026, 9, 30));
        form.SiteIds.Should().BeEquivalentTo([Casablanca.Id, Rabat.Id]);
        form.Journals.Should().Be(AccountingJournals.Sales | AccountingJournals.Receipts | AccountingJournals.Adjustments
                                  | AccountingJournals.Payroll | AccountingJournals.Depreciation | AccountingJournals.Purchases);
        form.Layout.Should().Be(AccountingExportLayout.Sage);
        AccountingExportScreenRules.NewForm(new DateOnly(2026, 1, 12), []).From.Should().Be(new DateOnly(2025, 12, 1));
        AccountingExportScreenRules.SiteDisplay(Casablanca).Should().Be("CAS — Casablanca Centre");
    }

    [Fact]
    public void A_choice_is_refused_on_its_box_a_period_in_reverse_no_site_no_journal()
    {
        var form = AccountingExportScreenRules.NewForm(Today, [Casablanca, Rabat]);
        (form.From, form.To) = (form.To, form.From);
        AccountingExportScreenRules.Check(form, [Casablanca, Rabat]).Refusal
            .Should().Be(new FieldRefusal(AccountingExportScreenRules.PeriodField, "Purchasing_PeriodReversed"));

        form = AccountingExportScreenRules.NewForm(Today, [Casablanca]);
        form.SiteIds.Clear();
        AccountingExportScreenRules.Check(form, [Casablanca]).Refusal!.Field.Should().Be(AccountingExportScreenRules.SitesField);

        form = AccountingExportScreenRules.NewForm(Today, [Casablanca]);
        foreach (var journal in AccountingExportScreenRules.JournalChoices)
            form.Journals = AccountingExportScreenRules.Toggle(form.Journals, journal, false);
        AccountingExportScreenRules.Check(form, [Casablanca]).Refusal!.Key.Should().Be("Accounting_NoJournal");

        form = AccountingExportScreenRules.NewForm(Today, [Rabat, Casablanca]);
        form.Journals = AccountingExportScreenRules.Toggle(form.Journals, AccountingJournals.Payroll, false);
        var (refusal, request) = AccountingExportScreenRules.Check(form, [Casablanca, Rabat]);
        refusal.Should().BeNull();
        request!.SiteIds.Should().Equal(Casablanca.Id, Rabat.Id);
        request.Journals.HasFlag(AccountingJournals.Payroll).Should().BeFalse();

        AccountingExportScreenRules.FieldOf(Error.Forbidden("Accounting_SiteNotAccessible")).Should().Be(AccountingExportScreenRules.SitesField);
        AccountingExportScreenRules.FieldOf(Error.Validation("Accounting_ExportRefused", 2)).Should().BeNull();
    }

    [Fact]
    public void The_check_says_what_it_found_and_opens_the_export_only_when_everything_balances()
    {
        var balanced = new AccountingExportPreview([Entry("VT", AccountingJournals.Sales, 100m, 100m), Entry("VT", AccountingJournals.Sales, 50m, 50m, "FA-2"),
                                                    Entry("CAI", AccountingJournals.Receipts, 80m, 80m)], [], true);
        AccountingExportScreenRules.Summary(balanced).Should().Equal(
            new AccountingJournalSummary("CAI", AccountingJournals.Receipts, 1, 80m, 80m),
            new AccountingJournalSummary("VT", AccountingJournals.Sales, 2, 150m, 150m));
        AccountingExportScreenRules.Status(balanced, AccountingJournals.Sales, Text)
            .Should().Be(new AccountingExportStatus("Accounting.Balanced(3)", StockInk.Plain));
        AccountingExportScreenRules.Actions(_ => true, balanced).Export.Should().BeTrue();

        var refused = balanced with { Problems = [new AccountingProblem(AccountingProblemKind.Unmapped, Today, "VT", "FA-1", "Facture", "")] };
        AccountingExportScreenRules.Status(refused, AccountingJournals.Sales, Text).Ink.Should().Be(StockInk.CriticalBold);
        AccountingExportScreenRules.Status(refused, AccountingJournals.Sales, Text).Text.Should().Be("Accounting_ExportRefused(1)");
        AccountingExportScreenRules.Actions(_ => true, refused).Export.Should().BeFalse();

        var empty = new AccountingExportPreview([], [], false);
        AccountingExportScreenRules.Status(empty, AccountingJournals.Depreciation, Text).Text
            .Should().Be("Purchasing_NothingToExport  ·  Accounting.NoDepreciationSource");
        AccountingExportScreenRules.Status(null, AccountingJournals.None, Text).Should().Be(new AccountingExportStatus("Accounting.CheckFirst", StockInk.Muted));
        AccountingExportScreenRules.Actions(_ => true, null).Should().Be(new AccountingExportActions(true, true, false, true));
        AccountingExportScreenRules.Actions(_ => false, balanced).Should().Be(new AccountingExportActions(false, false, false, false));
    }

    [Fact]
    public void The_files_are_named_after_their_layout_and_period()
    {
        var request = new AccountingExportRequest(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), [Casablanca.Id],
                                                  AccountingJournals.Sales, AccountingExportLayout.Generic);
        AccountingExportScreenRules.ExportFileName(request).Should().Be("export-comptable-generic-20260901-20260930.csv");
        AccountingExportScreenRules.VatFileName(request.From, request.To, pdf: true).Should().Be("etat-tva-20260901-20260930.pdf");
        AccountingExportScreenRules.VatTitle(request.From, request.To).Should().Be("État de TVA du 01/09/2026 au 30/09/2026");
    }

    // ---- Comptes et journaux -------------------------------------------------------------------------

    [Fact]
    public void A_setting_line_says_what_it_names_and_each_line_refused_is_said_on_its_own()
    {
        var sheet = AccountingExportService.Sheet(new AccountingSettings(), new PurchasingSettings(),
                                                  [new AccountingDepartmentOption("BIO", "Biochimie")], [Casablanca]);
        var department = sheet.Single(r => r.Key == AccountingSettings.RevenueKeyPrefix + "BIO");
        AccountingSettingsScreenRules.Label(department, Text).Should().Be("AccountingSetting.DepartmentRevenue(BIO — Biochimie)");
        var cheque = sheet.First(r => r.ArgumentKey);
        AccountingSettingsScreenRules.Label(cheque, Text).Should().StartWith("AccountingSetting.ReceiptAccount(PaymentMethod.");
        AccountingSettingsScreenRules.SectionKey(department).Should().Be("AccountingSection.Revenue");

        var values = AccountingSettingsScreenRules.Values(sheet);
        AccountingSettingsScreenRules.Check(sheet, values, Text).Should().BeEmpty();
        AccountingSettingsScreenRules.Changed(sheet, values).Should().Be(0);

        values[AccountingSettings.VatCollectedKey] = "44 55";
        values[AccountingSettings.SalesJournalKey] = "";
        values[department.Key] = "";
        var refusals = AccountingSettingsScreenRules.Check(sheet, values, Text);
        refusals.Should().HaveCount(2, "a department left blank follows the default account");
        refusals.Should().Contain(r => r.Field == AccountingSettings.VatCollectedKey && r.Key == "Accounting_AccountInvalid" && r.Typed == "44 55");
        refusals.Should().Contain(r => r.Field == AccountingSettings.SalesJournalKey && r.Key == "Accounting_SettingRequired"
                                       && r.Typed == "AccountingSetting.SalesJournal");
        AccountingSettingsScreenRules.Changed(sheet, values).Should().Be(2, "the department was blank already");
        AccountingSettingsScreenRules.ModeChoices.Select(m => m.Value).Should().Equal("Global", "PerPatient");
        AccountingSettingsScreenRules.Actions(p => p == Permissions.Accounting.Export).Should().Be(new AccountingSettingsActions(true, true));
    }

    // ---- Immobilisations -----------------------------------------------------------------------------

    [Fact]
    public void An_asset_needs_the_right_to_manage_the_register_its_plan_only_the_right_to_see_it()
    {
        bool All(string _) => true;
        var row = Row();
        FixedAssetScreenRules.Actions(All, row, true)
            .Should().Be(new FixedAssetActions(true, true, true, true, true, true, false, true, true, true));

        var viewer = FixedAssetScreenRules.Actions(p => p == Permissions.Assets.View, row, true);
        viewer.Should().Be(new FixedAssetActions(false, false, false, false, true, false, false, true, true, false));

        var sold = Row(Source(disposal: new DateOnly(2026, 6, 30)));
        FixedAssetScreenRules.Actions(All, sold, true).Should().Match<FixedAssetActions>(a => !a.Disposal && a.CancelDisposal);
        FixedAssetScreenRules.DisposalKey(sold).Should().Be("Assets.CancelDisposal");
        FixedAssetScreenRules.DisposalKey(row).Should().Be("Assets.Disposal");
        FixedAssetScreenRules.RowInk(sold).Should().Be(StockInk.Muted);
        FixedAssetScreenRules.Actions(All, null, false).Should().Match<FixedAssetActions>(a => a.New && !a.Edit && !a.Schedule && !a.Pdf);
    }

    [Fact]
    public void A_new_asset_starts_as_laboratory_equipment_bought_today_over_its_categorys_life()
    {
        var form = FixedAssetScreenRules.Form(null, Today);
        form.Category.Should().Be(FixedAssetCategory.LaboratoryEquipment);
        form.AcquisitionDate.Should().Be(Today);
        form.UsefulLifeYears.Should().Be(5);
        form.Cost.Should().BeEmpty();

        var furniture = FixedAssetScreenRules.Form(new SaveFixedAssetRequest
        {
            Code = "MOB-1", Label = "Paillasse", Category = FixedAssetCategory.Furniture, AcquisitionDate = default,
            InServiceDate = default, CostExcludingVatMad = 12_500m
        }, Today);
        furniture.UsefulLifeYears.Should().Be(10);
        furniture.Cost.Should().Be("12500.00");
        furniture.InServiceDate.Should().Be(Today);

        var french = FixedAssetScreenRules.Form(new SaveFixedAssetRequest
        {
            Code = "MOB-1", Label = "Paillasse", AcquisitionDate = Today, InServiceDate = Today, CostExcludingVatMad = 12_500.5m
        }, Today, new System.Globalization.CultureInfo("fr-MA"));
        french.Cost.Should().Be("12500,50", "the browser's box shows the amount as the session writes it, and reads it back");
        MarocLIS.Application.Input.TypedNumber.Parse(french.Cost).Should().Be(12_500.5m);
    }

    [Fact]
    public void The_plan_is_said_as_it_is_typed_the_declining_one_refused_under_three_years()
    {
        var form = FixedAssetScreenRules.Form(null, Today);
        form.Cost = "100 000";
        form.Vat = "20000";
        FixedAssetScreenRules.Plan(form, Text, Amount).Should().Be("Assets.PlanStraightLine(120000.00 DH,20 %)",
                                                                   "VAT not recoverable is part of the base");
        form.VatRecoverable = true;
        form.Method = DepreciationMethod.Declining;
        FixedAssetScreenRules.Plan(form, Text, Amount).Should().Be("Assets.PlanDeclining(100000.00 DH,2,40 %)");
        form.UsefulLifeYears = 2;
        FixedAssetScreenRules.Plan(form, Text, Amount).Should().Be("Asset_DecliningNeedsThreeYears");
    }

    [Fact]
    public void An_asset_is_refused_box_by_box_and_an_instrument_its_supplier_placed_is_refused()
    {
        var placed = new AssetInstrumentOption(Guid.NewGuid(), "AUT-09", new MultilingualText("Automate placé"), null, null, null,
                                               "PLC-2025-04", null);
        var options = new FixedAssetOptions([], [placed]);
        var form = FixedAssetScreenRules.Form(null, Today);

        FixedAssetScreenRules.Check(form, options).Refusal!.Field.Should().Be(FixedAssetScreenRules.CodeField);
        form.Code = " imm-2026-004 ";
        FixedAssetScreenRules.Check(form, options).Refusal!.Field.Should().Be(FixedAssetScreenRules.LabelField);
        form.Label = "Centrifugeuse";
        FixedAssetScreenRules.Check(form, options).Refusal!.Key.Should().Be("Asset_CostRequired");
        form.Cost = "12 mille";
        FixedAssetScreenRules.Check(form, options).Refusal.Should().Match<FieldRefusal>(r =>
            r.Field == FixedAssetScreenRules.CostField && r.Key == "Result_NotANumber" && r.Typed == "12 mille");
        form.Cost = "12 000,50";
        form.Residual = "-5";
        FixedAssetScreenRules.Check(form, options).Refusal!.Field.Should().Be(FixedAssetScreenRules.ResidualField);
        form.Residual = null;
        form.InstrumentId = placed.Id;
        FixedAssetScreenRules.Check(form, options).Refusal.Should().Match<FieldRefusal>(r =>
            r.Field == FixedAssetScreenRules.InstrumentField && r.Key == "Asset_SupplierProvided" && r.Typed == "PLC-2025-04");
        FixedAssetScreenRules.InstrumentState(placed, Text)!.Critical.Should().BeTrue();

        form.InstrumentId = null;
        form.SupplierInvoiceId = Guid.NewGuid();
        var (refusal, request) = FixedAssetScreenRules.Check(form, options);
        refusal.Should().BeNull();
        request!.Code.Should().Be("imm-2026-004");
        request.CostExcludingVatMad.Should().Be(12_000.50m);
        request.SupplierInvoiceId.Should().BeNull("an invoice belongs to a supplier");

        FixedAssetScreenRules.FieldOf(Error.Conflict("Asset_CodeExists", "IMM-2026-004")).Should().Be(FixedAssetScreenRules.CodeField);
        FixedAssetScreenRules.FieldOf(Error.Validation("Asset_InServiceBeforeAcquisition")).Should().Be(FixedAssetScreenRules.InServiceField);
        FixedAssetScreenRules.FieldOf(Error.Validation("Asset_NotFound")).Should().BeNull();
    }

    [Fact]
    public void An_invoice_fills_its_number_and_the_amounts_still_empty_never_what_was_typed()
    {
        var invoice = new AssetInvoiceOption(Guid.NewGuid(), "FA-2026-118", new DateOnly(2026, 3, 12), 185_000m, 37_000m);
        var form = FixedAssetScreenRules.Form(null, Today);
        FixedAssetScreenRules.ApplyInvoice(form, invoice);
        form.Should().Match<FixedAssetForm>(f => f.InvoiceReference == "FA-2026-118" && f.Cost == "185000.00" && f.Vat == "37000.00"
                                                 && f.AcquisitionDate == new DateOnly(2026, 3, 12) && f.SupplierInvoiceId == invoice.Id);

        var typed = FixedAssetScreenRules.Form(null, Today);
        typed.Id = Guid.NewGuid();
        typed.Cost = "90000";
        FixedAssetScreenRules.ApplyInvoice(typed, invoice);
        typed.Cost.Should().Be("90000");
        typed.AcquisitionDate.Should().Be(Today, "an asset already registered keeps its date");
        FixedAssetScreenRules.InvoiceDisplay(invoice, Amount).Should().Be("FA-2026-118 · 12/03/2026 · 185000.00 DH");
    }

    [Fact]
    public void A_disposal_is_dated_today_at_the_latest_with_its_reason_and_a_price_that_is_an_amount()
    {
        var row = Row();
        var form = FixedAssetScreenRules.NewDisposal(Today);
        FixedAssetScreenRules.Check(form, row, Today).Refusal!.Field.Should().Be(FixedAssetScreenRules.DisposalReasonField);
        form.Date = Today.AddDays(1);
        FixedAssetScreenRules.Check(form, row, Today).Refusal!.Key.Should().Be("Asset_DisposalInFuture");
        form.Date = new DateOnly(2025, 12, 31);
        FixedAssetScreenRules.Check(form, row, Today).Refusal!.Key.Should().Be("Asset_DisposalBeforeAcquisition");
        form.Date = Today;
        form.Price = "dix mille";
        FixedAssetScreenRules.Check(form, row, Today).Refusal!.Field.Should().Be(FixedAssetScreenRules.DisposalPriceField);
        form.Price = "10 000";
        form.Reason = " Cédé au laboratoire de Fès ";
        FixedAssetScreenRules.Check(form, row, Today).Entry.Should().Be(new AssetDisposalEntry(Today, 10_000m, "Cédé au laboratoire de Fès"));
        FixedAssetScreenRules.CancelDisposalQuestion(row, Text).Should().Be("Assets.CancelDisposalQuestion(IMM-2026-001)");
    }

    [Fact]
    public void The_accounts_of_a_category_are_refused_box_by_box()
    {
        var form = FixedAssetScreenRules.AccountsForm(FixedAssetSettings.Default);
        FixedAssetScreenRules.Check(form, FixedAssetSettings.Default).Settings!.IsValid().Should().BeTrue();

        form[FixedAssetCategory.ItEquipment].Depreciation = "2355";
        FixedAssetScreenRules.Check(form, FixedAssetSettings.Default).Refusal.Should().Match<FieldRefusal>(r =>
            r.Field == "account-ItEquipment-Depreciation" && r.Typed == "2355");
        form[FixedAssetCategory.ItEquipment].Depreciation = " 283551 ";
        form[FixedAssetCategory.Vehicles].Charge = "7193";
        FixedAssetScreenRules.Check(form, FixedAssetSettings.Default).Refusal!.Field.Should().Be("account-Vehicles-Charge");
        form[FixedAssetCategory.Vehicles].Charge = "61931";
        var saved = FixedAssetScreenRules.Check(form, FixedAssetSettings.Default).Settings!;
        saved.For(FixedAssetCategory.ItEquipment).Depreciation.Should().Be("283551");
        FixedAssetSettings.IsAssetAccount("2832").Should().BeFalse();
    }

    [Fact]
    public void The_plan_by_month_carries_the_depreciation_accumulated_at_its_end()
    {
        var schedule = Depreciation.Compute(Source().Terms);
        var months = FixedAssetScreenRules.ScheduleMonths(schedule);
        months.Should().HaveCount(schedule.Months.Count);
        months[0].Month.Should().Be("02/2026");
        months[^1].Accumulated.Should().Be(schedule.GrossValue);
        FixedAssetScreenRules.ScheduleHeader(Row(), schedule, Text, Amount)
            .Should().Be("Assets.ScheduleHeader(720000.00 DH,DepreciationMethod.StraightLine,5,01/02/2026,10/02/2026,6193,28332)");
        FixedAssetScreenRules.ScheduleFileName(Row()).Should().Be("plan-amortissement-IMM-2026-001.csv");
    }

    [Fact]
    public void The_instrument_behind_a_line_and_its_breakdowns_are_read_from_the_equipment_module()
    {
        var instrument = Guid.NewGuid();
        FixedAssetScreenRules.InstrumentOf(Row(Source(instrument: instrument))).Should().Be(instrument);
        FixedAssetScreenRules.InstrumentOf(null).Should().BeNull();
        var summary = new AssetInstrumentSummary(instrument, "AUT-01", new MultilingualText("Automate"), EquipmentStatus.Operational,
                                                 "Roche", "Cobas", "SN-1", null, null, new DateOnly(2026, 11, 1), null,
                                                 [new AssetMaintenanceRow(Today, MaintenanceKind.Corrective, new MultilingualText("Panne"), 7.5m, 1_200m, "M. Idrissi", true)]);
        FixedAssetScreenRules.InstrumentHeading(summary, AppLanguage.French, Text)
            .Should().Be("Assets.InstrumentSection — AUT-01 · Automate · EquipmentStatus.Operational");
        FixedAssetScreenRules.InstrumentSummaryText(summary, Text, Amount, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be("Assets.InstrumentSummary(Roche Cobas,SN-1,1,1,7.5,1200.00 DH,—,01/11/2026,—)");
        FixedAssetScreenRules.IsBreakdown(summary.Interventions[0]).Should().BeTrue();
    }

    [Fact]
    public void The_register_is_printed_in_the_screens_language_with_its_accounting_columns()
    {
        var register = FixedAssetService.BuildRegister(2026, [Source()], [], FixedAssetSettings.Default);
        var (columns, rows) = FixedAssetScreenRules.PdfTable(register, AppLanguage.French, Text);
        columns.Should().HaveCount(FixedAssetScreenRules.PdfColumns);
        rows.Should().HaveCount(3, "the asset, its category's subtotal and the total");
        rows[0][2].Should().Be("FixedAssetCategory.LaboratoryEquipment");
        rows[1][1].Should().Be("Assets.Subtotal FixedAssetCategory.LaboratoryEquipment");
        FixedAssetScreenRules.Headers(Text).Should().HaveCount(23);
        FixedAssetScreenRules.ReportFileName(register, pdf: false).Should().Be("immobilisations-2026.csv");
        FixedAssetScreenRules.Heading(register, Text, Amount).Should().StartWith("Assets.Heading(2026,1,720000.00 DH,");
    }
}
