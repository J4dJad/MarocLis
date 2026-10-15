using System.Reflection;
using System.Text;
using FluentAssertions;
using MarocLIS.Application.Accounting;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Infrastructure.Persistence;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Lot E, the complete accounting export: the sales, receipts and adjustments journals, the payroll
/// journal, depreciation, the checks that refuse an unbalanced or unmapped entry, the settings and
/// the simple VAT statement.
/// </summary>
public sealed class FinanceETests
{
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 30);
    private static readonly Guid Site = Guid.NewGuid();
    private static readonly AccountingSettings Defaults = AccountingSettings.Default;

    private static readonly AccountingCustomer Patient = new("MR-000123", "ALAMI Sara", null, null);
    private static readonly AccountingCustomer Covered = new("MR-000123", "ALAMI Sara", "CNSS", "34211");

    private static SalesDocumentSource Invoice(string number, AccountingCustomer customer, decimal payerShare,
                                               params SalesLineSource[] lines)
    {
        var total = lines.Sum(l => l.Net + l.Vat);
        return new SalesDocumentSource(Site, "CASA", InvoiceKind.Invoice, number, new DateOnly(2026, 9, 10), customer,
                                       payerShare > 0m, total, lines.Sum(l => l.Vat), payerShare, lines);
    }

    private static SalesLineSource Line(string? department, decimal net) => new(department, net, 0m, true, 0m);

    private static SalesPaymentSource Payment(PaymentMethod method, decimal amount, AccountingCustomer? customer = null,
                                              PaymentStatus status = PaymentStatus.Settled, Guid? session = null,
                                              string? deposit = null, DateOnly? bounced = null, DateOnly? refunded = null,
                                              DateOnly? cancelled = null, bool payerClaimed = false, string? reference = null) =>
        new(Site, "CASA", "FAC/CASA/2026/000001", new DateOnly(2026, 9, 10), customer ?? Patient, payerClaimed, method, status,
            amount, new DateOnly(2026, 9, 12), bounced, refunded, cancelled, deposit, session, null, reference);

    private static IReadOnlyList<JournalEntry> Build(SalesAccountingSources sources, AccountingSettings? settings = null,
                                                     AccountingJournals journals = AccountingJournals.All) =>
        SalesAccountingJournal.Build(sources, settings ?? Defaults, From, To, journals);

    private static SalesAccountingSources Sources(IEnumerable<SalesDocumentSource>? documents = null,
                                                  IEnumerable<SalesPaymentSource>? payments = null,
                                                  IEnumerable<PayerShortfallSource>? shortfalls = null,
                                                  IEnumerable<DepositSource>? deposits = null,
                                                  IEnumerable<CashSessionSource>? sessions = null) =>
        new([.. documents ?? []], [.. payments ?? []], [.. shortfalls ?? []], [.. deposits ?? []], [.. sessions ?? []]);

    private static AccountingEntry Single(JournalEntry entry, string account) =>
        entry.Lines.Single(l => l.Account == account);

    // ---- Sales journal --------------------------------------------------------------------------

    [Fact]
    public void An_invoice_splits_the_receivable_between_payer_and_patient_and_the_revenue_by_department()
    {
        var settings = Defaults with
        {
            RevenueByDepartment = new Dictionary<string, string> { ["HEMA"] = "71241", ["BIOCH"] = "71242" }
        };
        var invoice = Invoice("FAC/CASA/2026/000001", Covered, 140m,
                              Line("HEMA", 80m), Line("BIOCH", 100m), Line("BIOCH", 20m));

        var entry = Build(Sources([invoice]), settings).Single();

        entry.Journal.Should().Be("VT");
        entry.IsBalanced.Should().BeTrue();
        Single(entry, "34211").Debit.Should().Be(140m);
        Single(entry, "34211").ThirdParty.Should().Be("CNSS");
        Single(entry, "3421").Debit.Should().Be(60m);
        Single(entry, "3421").ThirdParty.Should().Be("PATIENTS");
        Single(entry, "71241").Credit.Should().Be(80m);
        Single(entry, "71242").Credit.Should().Be(120m);
        entry.Lines.Should().OnlyContain(l => l.Piece == "FAC/CASA/2026/000001" && l.Date == new DateOnly(2026, 9, 10));
    }

    [Fact]
    public void A_payer_account_that_is_not_a_number_is_its_auxiliary_under_the_default_receivable()
    {
        var invoice = Invoice("F1", Covered with { PayerAccount = "CNSS01" }, 70m, Line("HEMA", 100m));

        var entry = Build(Sources([invoice])).Single();

        entry.Lines.Where(l => l.Account == "3421").Should().HaveCount(2);
        entry.Lines.Should().Contain(l => l.Account == "3421" && l.ThirdParty == "CNSS01" && l.Debit == 70m);
    }

    [Fact]
    public void Per_patient_mode_uses_the_medical_record_number_as_third_party()
    {
        var settings = Defaults with { PatientMode = PatientThirdPartyMode.PerPatient };

        var entry = Build(Sources([Invoice("F1", Patient, 0m, Line("HEMA", 100m))]), settings).Single();

        Single(entry, "3421").ThirdParty.Should().Be("MR-000123");
        Single(entry, "3421").Debit.Should().Be(100m);
    }

    [Fact]
    public void Taxable_lines_credit_the_vat_collected()
    {
        var invoice = Invoice("F1", Patient, 0m, Line("HEMA", 100m), new SalesLineSource(null, 50m, 10m, false, 0.20m));

        var entry = Build(Sources([invoice])).Single();

        entry.IsBalanced.Should().BeTrue();
        Single(entry, "4455").Credit.Should().Be(10m);
        Single(entry, "3421").Debit.Should().Be(160m);
    }

    [Fact]
    public void A_partial_credit_note_is_spread_over_the_departments_of_the_invoice_it_reverses()
    {
        var settings = Defaults with
        {
            RevenueByDepartment = new Dictionary<string, string> { ["HEMA"] = "71241", ["BIOCH"] = "71242" }
        };
        var original = Invoice("F1", Covered, 150m, Line("HEMA", 100m), Line("BIOCH", 200m));
        var credit = new SalesDocumentSource(Site, "CASA", InvoiceKind.CreditNote, "AV/CASA/2026/000001",
                                             new DateOnly(2026, 9, 20), Covered, false, 90m, 0m, 0m,
                                             [Line(null, 90m)], original);

        var entry = Build(Sources([credit]), settings).Single();

        entry.IsBalanced.Should().BeTrue();
        Single(entry, "71241").Debit.Should().Be(30m);
        Single(entry, "71242").Debit.Should().Be(60m);
        // Half the invoice was the payer's: half the credit note comes off its receivable.
        Single(entry, "34211").Credit.Should().Be(45m);
        Single(entry, "3421").Credit.Should().Be(45m);
    }

    [Fact]
    public void A_document_outside_the_period_is_not_exported()
    {
        var invoice = Invoice("F1", Patient, 0m, Line("HEMA", 100m)) with { IssueDate = new DateOnly(2026, 10, 1) };

        Build(Sources([invoice])).Should().BeEmpty();
    }

    // ---- Receipts -------------------------------------------------------------------------------

    [Fact]
    public void Cash_taken_in_a_till_is_summed_per_session_when_the_patients_share_one_account()
    {
        var session = Guid.NewGuid();
        var payments = new[]
        {
            Payment(PaymentMethod.Cash, 100m, session: session),
            Payment(PaymentMethod.Cash, 50m, session: session),
            Payment(PaymentMethod.Card, 70m, session: session)
        };
        var settings = Defaults with { CashBySite = new Dictionary<string, string> { ["CASA"] = "51611" } };

        var entries = Build(Sources(payments: payments), settings);

        entries.Should().HaveCount(2).And.OnlyContain(e => e.IsBalanced && e.Kind == AccountingJournals.Receipts);
        var cash = entries.Single(e => e.Journal == "CAI");
        Single(cash, "51611").Debit.Should().Be(150m);
        Single(cash, "3421").Credit.Should().Be(150m);
        cash.Piece.Should().Be(SalesAccountingJournal.SessionPiece(session));
        Single(entries.Single(e => e.Journal == "BQ"), "5141").Debit.Should().Be(70m);
    }

    [Fact]
    public void Per_patient_mode_keeps_each_receipt_with_its_patient()
    {
        var session = Guid.NewGuid();
        var settings = Defaults with { PatientMode = PatientThirdPartyMode.PerPatient };

        var entries = Build(Sources(payments: [Payment(PaymentMethod.Cash, 100m, session: session),
                                               Payment(PaymentMethod.Cash, 50m, session: session)]), settings);

        entries.Should().HaveCount(2);
        entries.SelectMany(e => e.Lines).Where(l => l.Account == "3421")
               .Should().OnlyContain(l => l.ThirdParty == "MR-000123");
    }

    [Fact]
    public void A_payer_settlement_credits_the_payer_and_a_bounced_cheque_reverses_its_receipt()
    {
        var entries = Build(Sources(payments:
        [
            Payment(PaymentMethod.ThirdPartyPayer, 140m, Covered, payerClaimed: true, reference: "VIR-CNSS-09"),
            Payment(PaymentMethod.Cheque, 60m, Covered, PaymentStatus.Bounced, bounced: new DateOnly(2026, 9, 25))
        ]));

        var settlement = entries.Single(e => e.Piece == "VIR-CNSS-09");
        Single(settlement, "5141").Debit.Should().Be(140m);
        Single(settlement, "34211").Credit.Should().Be(140m);

        var bounced = entries.Single(e => e.Date == new DateOnly(2026, 9, 25));
        Single(bounced, "3421").Debit.Should().Be(60m);
        Single(bounced, "5141").Credit.Should().Be(60m);
        entries.Should().OnlyContain(e => e.IsBalanced);
    }

    [Fact]
    public void A_deposit_is_held_in_4421_until_the_invoice_then_what_is_left_is_refunded()
    {
        var deposit = new DepositSource(Site, "CASA", "REC/CASA/2026/000007", Patient, PaymentMethod.Cash, 200m,
                                        new DateOnly(2026, 9, 5), 50m, new DateOnly(2026, 9, 15), Guid.NewGuid(), null);
        var applied = Payment(PaymentMethod.Cash, 150m, deposit: "REC/CASA/2026/000007") with
        {
            PaymentDate = new DateOnly(2026, 9, 5)
        };

        var entries = Build(Sources(payments: [applied], deposits: [deposit]));

        entries.Should().HaveCount(3).And.OnlyContain(e => e.IsBalanced);
        var taken = entries.Single(e => e.Date == new DateOnly(2026, 9, 5));
        Single(taken, "5161").Debit.Should().Be(200m);
        Single(taken, "4421").Credit.Should().Be(200m);

        // Applied on the invoice's day, not on the deposit's: the receivable exists from then.
        var application = entries.Single(e => e.Kind == AccountingJournals.Adjustments);
        application.Date.Should().Be(new DateOnly(2026, 9, 10));
        Single(application, "4421").Debit.Should().Be(150m);
        Single(application, "3421").Credit.Should().Be(150m);

        var refund = entries.Single(e => e.Date == new DateOnly(2026, 9, 15));
        Single(refund, "4421").Debit.Should().Be(50m);
        Single(refund, "5161").Credit.Should().Be(50m);
    }

    [Fact]
    public void Write_offs_their_cancellation_and_the_share_left_to_the_patient_go_to_the_adjustments()
    {
        var shortfall = new PayerShortfallSource(Site, "CASA", "F1", new DateOnly(2026, 9, 18), Covered, 20m);

        var entries = Build(Sources(
            payments:
            [
                Payment(PaymentMethod.WriteOff, 30m, Covered, payerClaimed: true),
                Payment(PaymentMethod.WriteOff, 10m, Covered, PaymentStatus.Cancelled, cancelled: new DateOnly(2026, 9, 28),
                        payerClaimed: true) with { PaymentDate = new DateOnly(2026, 8, 30) }
            ],
            shortfalls: [shortfall]));

        entries.Should().HaveCount(3).And.OnlyContain(e => e.Journal == "OD" && e.IsBalanced);
        Single(entries.Single(e => e.Label.StartsWith("Perte")), "6585").Debit.Should().Be(30m);
        Single(entries.Single(e => e.Label.StartsWith("Annul.")), "6585").Credit.Should().Be(10m);
        var moved = entries.Single(e => e.Label.StartsWith("Refus"));
        Single(moved, "3421").Debit.Should().Be(20m);
        Single(moved, "34211").Credit.Should().Be(20m);
    }

    [Fact]
    public void A_closed_till_sends_its_withdrawals_to_5115_and_its_difference_to_6588_or_7588()
    {
        var sessions = new[]
        {
            new CashSessionSource(Site, "CASA", Guid.NewGuid(), "Accueil 1", new DateOnly(2026, 9, 3),
                                  new DateOnly(2026, 9, 3), 1500m, -12.5m),
            new CashSessionSource(Site, "CASA", Guid.NewGuid(), "Accueil 2", new DateOnly(2026, 9, 4),
                                  new DateOnly(2026, 9, 4), 0m, 4m)
        };

        var entries = Build(Sources(sessions: sessions));

        entries.Should().HaveCount(3).And.OnlyContain(e => e.IsBalanced && e.Journal == "CAI");
        entries.SelectMany(e => e.Lines).Should().Contain(l => l.Account == "5115" && l.Debit == 1500m)
               .And.Contain(l => l.Account == "6588" && l.Debit == 12.5m)
               .And.Contain(l => l.Account == "7588" && l.Credit == 4m);
    }

    [Fact]
    public void Only_the_journals_asked_for_are_built()
    {
        var sources = Sources([Invoice("F1", Patient, 0m, Line("HEMA", 100m))],
                              [Payment(PaymentMethod.Card, 100m), Payment(PaymentMethod.WriteOff, 5m)]);

        Build(sources, journals: AccountingJournals.Sales).Should().ContainSingle(e => e.Kind == AccountingJournals.Sales);
        Build(sources, journals: AccountingJournals.Receipts).Should().ContainSingle(e => e.Kind == AccountingJournals.Receipts);
        Build(sources, journals: AccountingJournals.Adjustments).Should().ContainSingle(e => e.Kind == AccountingJournals.Adjustments);
    }

    // ---- Payroll and depreciation ---------------------------------------------------------------

    [Fact]
    public void A_closed_payroll_month_balances_from_the_payslip_figures()
    {
        var rates = new PayrollParameters(2026);
        var payslips = new[]
        {
            PayrollCalculator.Compute(new PayslipInput(6000m, new DateOnly(2020, 1, 6), new DateOnly(2026, 9, 30),
                                                       ContractType.Permanent, 2,
                                                       new PayslipVariables { AdvanceDeduction = 500m, OtherDeduction = 120m }), rates),
            PayrollCalculator.Compute(new PayslipInput(14250m, new DateOnly(2012, 3, 1), new DateOnly(2026, 9, 30),
                                                       ContractType.Permanent, 0, new PayslipVariables { OvertimeHours25 = 6m }), rates)
        };
        var month = new PayrollMonthSource(
            Site, "CASA", 2026, 9, payslips.Length,
            payslips.Sum(p => p.Gross),
            payslips.Sum(p => p.CnssEmployer + p.FamilyAllowanceEmployer + p.TrainingTaxEmployer + p.AmoEmployer),
            payslips.Sum(p => p.CnssEmployee + p.AmoEmployee),
            payslips.Sum(p => p.IncomeTax), 500m, payslips.Sum(p => p.OtherDeductions) - 500m, payslips.Sum(p => p.NetPay));

        var entry = PayrollAccountingJournal.Build([month], Defaults).Single();

        entry.IsBalanced.Should().BeTrue();
        entry.Date.Should().Be(new DateOnly(2026, 9, 30));
        Single(entry, "6171").Debit.Should().Be(month.Gross);
        Single(entry, "4432").Credit.Should().Be(month.NetPay);
        Single(entry, "3431").Credit.Should().Be(500m);
        Single(entry, "4488").Credit.Should().Be(120m);
        Single(entry, "4441").Credit.Should().Be(month.EmployeeContributions + month.EmployerCharges);
        AccountingExportCheck.Check([entry]).Should().BeEmpty();
    }

    [Fact]
    public void Depreciation_entries_are_exported_and_a_missing_account_is_refused()
    {
        var entries = DepreciationAccountingJournal.Build(
        [
            ("CASA", new DepreciationEntry(new DateOnly(2026, 9, 30), "IMMO-001", "Dotation automate", "6193", "2833", 1250m)),
            ("CASA", new DepreciationEntry(new DateOnly(2026, 9, 30), "IMMO-002", "Dotation PC", "", "2835", 80m))
        ], Defaults);

        entries.Should().HaveCount(2).And.OnlyContain(e => e.IsBalanced && e.Journal == "OD");
        var problems = AccountingExportCheck.Check(entries);
        problems.Should().ContainSingle(p => p.Kind == AccountingProblemKind.Unmapped && p.Piece == "IMMO-002");
    }

    // ---- Checks, purchases and file -------------------------------------------------------------

    [Fact]
    public void An_unbalanced_entry_or_an_invalid_account_is_listed()
    {
        var settings = Defaults with { RevenueByDepartment = new Dictionary<string, string> { ["HEMA"] = "VENTES" } };
        var good = Build(Sources([Invoice("F1", Patient, 0m, Line("BIOCH", 100m))]), settings).Single();
        var bad = Build(Sources([Invoice("F2", Patient, 0m, Line("HEMA", 100m))]), settings).Single();
        var unbalanced = good with { Piece = "F3", Lines = [.. good.Lines.Take(1)] };

        var problems = AccountingExportCheck.Check([good, bad, unbalanced]);

        problems.Should().HaveCount(2);
        problems.Should().Contain(p => p.Kind == AccountingProblemKind.Unmapped && p.Piece == "F2" && p.Detail == "VENTES");
        problems.Should().Contain(p => p.Kind == AccountingProblemKind.Unbalanced && p.Piece == "F3");
    }

    [Fact]
    public void Supplier_entries_are_grouped_back_into_balanced_entries_and_written_in_the_same_file()
    {
        var supplierLines = SupplierAccountingExport.BuildEntries(
            [new AccountingInvoiceSource("F001", "Biomed", null, "FA-77", new DateOnly(2026, 9, 2),
                                         [new AccountingInvoiceLine(StockItemKind.Reagent, 1000m, 200m),
                                          new AccountingInvoiceLine(StockItemKind.OfficeSupply, 100m, 20m)])],
            [new AccountingPaymentSource("F001", "Biomed", null, "FA-77", new DateOnly(2026, 9, 20), 1320m,
                                         SupplierPaymentMethod.Transfer, "VIR-1")],
            PurchasingSettings.Default);

        var grouped = AccountingExportCheck.Group(supplierLines, "CASA");

        grouped.Should().HaveCount(2).And.OnlyContain(e => e.IsBalanced && e.Kind == AccountingJournals.Purchases);
        AccountingExportCheck.Check(grouped).Should().BeEmpty();

        var sales = Build(Sources([Invoice("F1", Patient, 0m, Line("HEMA", 100m))]));
        var csv = Encoding.UTF8.GetString(SupplierAccountingExport.ToCsv(
            grouped.Concat(sales).SelectMany(e => e.Lines), AccountingExportLayout.Sage));
        csv.Should().Contain("ACH").And.Contain(";VT;7124;").And.Contain("10/09/2026");
    }

    // ---- Settings -------------------------------------------------------------------------------

    [Fact]
    public void Settings_survive_storage_and_blank_values_fall_back_to_the_defaults()
    {
        var settings = Defaults with
        {
            SalesJournal = "VTE",
            PatientMode = PatientThirdPartyMode.PerPatient,
            RevenueByDepartment = new Dictionary<string, string> { ["HEMA"] = "71241" },
            CashBySite = new Dictionary<string, string> { ["CASA"] = "51611" },
            ReceiptAccounts = new Dictionary<PaymentMethod, string> { [PaymentMethod.Cheque] = "5111" }
        };

        var read = AccountingSettings.FromValues(settings.ToValues().ToDictionary(v => v.Key, v => (string?)v.Value));

        read.SalesJournal.Should().Be("VTE");
        read.PatientMode.Should().Be(PatientThirdPartyMode.PerPatient);
        read.RevenueFor("HEMA").Should().Be("71241");
        read.RevenueFor("BIOCH").Should().Be("7124");
        read.RevenueFor(null).Should().Be("7124");
        read.CashFor("CASA").Should().Be("51611");
        read.CashFor("RABAT").Should().Be("5161");
        read.ReceiptAccountFor(PaymentMethod.Cheque, "CASA").Should().Be("5111");
        read.ReceiptAccountFor(PaymentMethod.Card, "CASA").Should().Be("5141");

        AccountingSettings.FromValues(new Dictionary<string, string?> { [AccountingSettings.SalesJournalKey] = " " })
                          .SalesJournal.Should().Be("VT");
    }

    [Fact]
    public void The_settings_screen_lists_every_account_and_refuses_an_invalid_one()
    {
        var sheet = AccountingExportService.Sheet(Defaults, PurchasingSettings.Default,
                                                  [new AccountingDepartmentOption("HEMA", "Hématologie")],
                                                  [new AccountingSiteOption(Site, "CASA", "Casablanca")]);

        sheet.Select(r => r.Key).Should().OnlyHaveUniqueItems()
             .And.Contain(Defaults.ToValues().Keys)
             .And.Contain(PurchasingSettings.Default.ToValues().Keys
                                            .Where(k => k.Contains(".Journal.") || k.Contains(".Account.")))
             .And.Contain(AccountingSettings.RevenueKeyPrefix + "HEMA")
             .And.Contain(AccountingSettings.CashKeyPrefix + "CASA");

        AccountingExportService.Validate(sheet, new Dictionary<string, string?>
        {
            [AccountingSettings.WriteOffKey] = "6585",
            [AccountingSettings.RevenueKeyPrefix + "HEMA"] = "",
            [AccountingSettings.PatientModeKey] = "PerPatient"
        }).IsSuccess.Should().BeTrue();

        AccountingExportService.Validate(sheet, new Dictionary<string, string?> { [AccountingSettings.WriteOffKey] = "65 85" })
                               .FirstError.ResourceKey.Should().Be("Accounting_AccountInvalid");
        AccountingExportService.Validate(sheet, new Dictionary<string, string?> { [AccountingSettings.WriteOffKey] = "" })
                               .FirstError.ResourceKey.Should().Be("Accounting_SettingRequired");
        AccountingExportService.Validate(sheet, new Dictionary<string, string?> { [AccountingSettings.SalesJournalKey] = "V T" })
                               .IsFailure.Should().BeTrue();
    }

    // ---- VAT ------------------------------------------------------------------------------------

    [Fact]
    public void The_vat_statement_separates_exempt_and_taxable_turnover_and_applies_the_prorata()
    {
        var sales = new[]
        {
            Invoice("F1", Patient, 0m, Line("HEMA", 900m), new SalesLineSource(null, 100m, 20m, false, 0.20m)),
            new SalesDocumentSource(Site, "CASA", InvoiceKind.CreditNote, "AV1", new DateOnly(2026, 9, 12), Patient, false,
                                    50m, 0m, 0m, [Line("HEMA", 50m)])
        };
        var purchases = new[]
        {
            new AccountingInvoiceSource("F001", "Biomed", null, "FA-1", new DateOnly(2026, 9, 3),
                                        [new AccountingInvoiceLine(StockItemKind.Reagent, 1000m, 200m)])
        };

        var statement = VatStatement.Build(From, To, sales, purchases);

        statement.ExemptTurnover.Should().Be(850m);
        statement.TaxableTurnover.Should().Be(100m);
        statement.Collected.Should().Be(20m);
        statement.Invoiced.Should().Be(200m);
        statement.ProrataPercent.Should().Be(11m); // 100 / 950 = 10.5 %, rounded up
        statement.Deductible.Should().Be(22m);
        statement.Balance.Should().Be(-2m);
        Encoding.UTF8.GetString(statement.ToCsv()).Should().Contain("vérifier avec votre comptable");
    }

    // ---- Permission and contract ----------------------------------------------------------------

    [Fact]
    public void The_export_right_is_shipped_to_the_director_the_accountant_and_the_administrator()
    {
        Permissions.All.Should().Contain("accounting.export");
        ShippedRole(SystemRoles.Accountant).Should().Contain(Permissions.Accounting.Export);
        ShippedRole(SystemRoles.ResponsibleBiologist).Should().Contain(Permissions.Accounting.Export);
        ShippedRole(SystemRoles.Administrator).Should().Contain(Permissions.Accounting.Export);
        ShippedRole(SystemRoles.Receptionist).Should().NotContain(Permissions.Accounting.Export);
    }

    [Fact]
    public void The_depreciation_contract_shared_with_the_assets_register_is_byte_exact()
    {
        var root = Directory.GetCurrentDirectory();
        while (root is not null && !File.Exists(Path.Combine(root, "MarocLIS.sln"))) root = Path.GetDirectoryName(root);
        if (root is null) return;

        var bytes = File.ReadAllBytes(Path.Combine(root, "src", "MarocLIS.Application", "Accounting", "IDepreciationEntrySource.cs"));
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        Encoding.UTF8.GetString(bytes).Should().Contain("\r\n").And.Contain("namespace MarocLIS.Application.Accounting;");
    }

    private static string[] ShippedRole(string roleCode)
    {
        var type = typeof(LisDbContext).Assembly.GetType("MarocLIS.Infrastructure.Persistence.RolePermissionSets")!;
        var definitions = (System.Collections.IEnumerable)type
            .GetMethod("Build", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

        foreach (var definition in definitions)
        {
            var tuple = definition.GetType();
            if ((string)tuple.GetField("Item1")!.GetValue(definition)! == roleCode)
                return (string[])tuple.GetField("Item3")!.GetValue(definition)!;
        }

        return [];
    }
}
