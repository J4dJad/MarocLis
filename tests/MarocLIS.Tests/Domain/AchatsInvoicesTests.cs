using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Organization;
using MarocLIS.Infrastructure.Persistence;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Lot D of the purchasing cycle: supplier invoices, the three-way match, the loi 69-21 due dates
/// and late-payment penalties, the accounting export and the payment-approval right.
/// </summary>
public sealed class AchatsInvoicesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly Guid Enterer = Guid.NewGuid();
    private static readonly Guid Manager = Guid.NewGuid();

    private static SupplierInvoice Invoice(params SupplierInvoiceLine[] lines)
    {
        var invoice = new SupplierInvoice(Guid.NewGuid(), Guid.NewGuid(), "FA-2026-118", Today.AddDays(-5),
                                          Today.AddDays(-3), Enterer);
        foreach (var line in lines) invoice.AddLine(line).IsSuccess.Should().BeTrue();
        return invoice;
    }

    private static SupplierInvoiceLine Line(Guid orderLineId, decimal quantity, decimal price, decimal vat = 0.20m) =>
        new(orderLineId, Guid.NewGuid(), "GLU — Glucose", quantity, price, vat);

    private static Dictionary<Guid, OrderLineFacts> Facts(params OrderLineFacts[] facts) =>
        facts.ToDictionary(f => f.PurchaseOrderLineId);

    // ---- D2: three-way match ---------------------------------------------------------------------

    [Fact]
    public void Match_WhenQuantityPriceAndVatAgree_ApprovesForPayment()
    {
        var orderLine = Guid.NewGuid();
        var invoice = Invoice(Line(orderLine, 10m, 90m));

        var variances = ThreeWayMatch.Compare(invoice.Lines, Facts(new OrderLineFacts(orderLine, 10m, 0m, 90m, 0.20m)),
                                              PriceTolerance.None);
        invoice.ApplyMatch(variances).IsSuccess.Should().BeTrue();

        invoice.Status.Should().Be(SupplierInvoiceStatus.ApprovedForPayment);
        invoice.Variances.Should().BeEmpty();
        invoice.TotalIncludingVat.Should().Be(1080m);
    }

    [Fact]
    public void Match_BillingMoreThanReceivedAndNotYetInvoiced_Blocks()
    {
        var orderLine = Guid.NewGuid();
        var invoice = Invoice(Line(orderLine, 6m, 90m));

        // 10 received, 5 already billed on an earlier invoice: only 5 can be billed now.
        var variances = ThreeWayMatch.Compare(invoice.Lines, Facts(new OrderLineFacts(orderLine, 10m, 5m, 90m, 0.20m)),
                                              PriceTolerance.None);
        invoice.ApplyMatch(variances);

        invoice.Status.Should().Be(SupplierInvoiceStatus.Blocked);
        var variance = invoice.Variances.Single();
        variance.Kind.Should().Be(VarianceKind.QuantityExceedsReceived);
        variance.Expected.Should().Be(5m);
        variance.Actual.Should().Be(6m);
    }

    [Theory]
    [InlineData(0, 0, 90.01, true)]     // default: the price must match to the centime
    [InlineData(1, 0, 90.90, false)]    // 1 % of 90 = 0.90
    [InlineData(1, 0, 90.91, true)]
    [InlineData(0, 1, 89.00, false)]    // 1 MAD either way
    [InlineData(0, 1, 88.99, true)]
    [InlineData(1, 2, 91.95, false)]    // the larger of the two
    public void Match_PriceTolerance_IsAPercentOrAnAmount(decimal percent, decimal amount, decimal invoiced, bool blocked)
    {
        var orderLine = Guid.NewGuid();
        var invoice = Invoice(Line(orderLine, 1m, invoiced));

        invoice.ApplyMatch(ThreeWayMatch.Compare(invoice.Lines, Facts(new OrderLineFacts(orderLine, 1m, 0m, 90m, 0.20m)),
                                                 new PriceTolerance(percent, amount)));

        (invoice.Status == SupplierInvoiceStatus.Blocked).Should().Be(blocked);
        if (blocked) invoice.Variances.Single().Kind.Should().Be(VarianceKind.PriceDiffers);
    }

    [Fact]
    public void Match_AnotherVatRate_Blocks()
    {
        var orderLine = Guid.NewGuid();
        var invoice = Invoice(Line(orderLine, 1m, 90m, vat: 0.07m));

        invoice.ApplyMatch(ThreeWayMatch.Compare(invoice.Lines, Facts(new OrderLineFacts(orderLine, 1m, 0m, 90m, 0.20m)),
                                                 PriceTolerance.None));

        invoice.Variances.Single().Kind.Should().Be(VarianceKind.VatRateDiffers);
    }

    [Fact]
    public void Match_AgainAfterTheGoodsArrive_Approves_ButNotOnceApproved()
    {
        var orderLine = Guid.NewGuid();
        var invoice = Invoice(Line(orderLine, 10m, 90m));

        invoice.ApplyMatch(ThreeWayMatch.Compare(invoice.Lines, Facts(new OrderLineFacts(orderLine, 4m, 0m, 90m, 0.20m)),
                                                 PriceTolerance.None));
        invoice.Status.Should().Be(SupplierInvoiceStatus.Blocked);

        invoice.ApplyMatch(ThreeWayMatch.Compare(invoice.Lines, Facts(new OrderLineFacts(orderLine, 10m, 0m, 90m, 0.20m)),
                                                 PriceTolerance.None)).IsSuccess.Should().BeTrue();
        invoice.Status.Should().Be(SupplierInvoiceStatus.ApprovedForPayment);
        invoice.Variances.Should().BeEmpty();

        invoice.ApplyMatch([]).FirstError.ResourceKey.Should().Be("SupplierInvoice_AlreadyMatched");
    }

    [Fact]
    public void AnOrderLine_CannotBeBilledTwiceOnOneInvoice()
    {
        var orderLine = Guid.NewGuid();
        var invoice = Invoice(Line(orderLine, 1m, 90m));

        invoice.AddLine(Line(orderLine, 1m, 90m)).FirstError.ResourceKey.Should().Be("SupplierInvoice_OrderLineTwice");
    }

    // ---- D2: release by the payment approver -------------------------------------------------------

    private static SupplierInvoice Blocked()
    {
        var orderLine = Guid.NewGuid();
        var invoice = Invoice(Line(orderLine, 10m, 100m));
        invoice.ApplyMatch([new MatchVariance(1, VarianceKind.PriceDiffers, 90m, 100m)]);
        invoice.SetPaymentTerms(Today.AddDays(-4), 60);
        return invoice;
    }

    [Fact]
    public void Unblock_ByWhoeverEnteredTheInvoice_IsRefused()
    {
        Blocked().Unblock(Now, Enterer, "Hausse de tarif acceptée")
            .FirstError.ResourceKey.Should().Be("SupplierInvoice_UnblockerMustDifferFromEnterer");
    }

    [Fact]
    public void Unblock_WithoutJustification_IsRefused()
    {
        Blocked().Unblock(Now, Manager, "  ").FirstError.ResourceKey.Should().Be("SupplierInvoice_JustificationRequired");
    }

    [Fact]
    public void Unblock_ByAnotherPersonWithAReason_ApprovesAndKeepsTheReason()
    {
        var invoice = Blocked();

        invoice.Unblock(Now, Manager, "Hausse de tarif 2026 acceptée par avenant").IsSuccess.Should().BeTrue();

        invoice.Status.Should().Be(SupplierInvoiceStatus.ApprovedForPayment);
        invoice.UnblockedByUserId.Should().Be(Manager);
        invoice.UnblockJustification.Should().Be("Hausse de tarif 2026 acceptée par avenant");
        invoice.Variances.Should().ContainSingle("the variances stay on record");
    }

    // ---- D3: payments --------------------------------------------------------------------------------

    private static SupplierPayment Payment(decimal amount, SupplierPaymentMethod method = SupplierPaymentMethod.Transfer,
                                           string? reference = "VIR-1", DateOnly? on = null) =>
        new(on ?? Today, amount, method, reference, Now, Manager);

    [Fact]
    public void Payment_OnABlockedInvoice_IsRefused()
    {
        Blocked().RecordPayment(Payment(100m)).FirstError.ResourceKey.Should().Be("SupplierInvoice_NotApprovedForPayment");
    }

    [Fact]
    public void Payments_PartThenBalance_EndPaid()
    {
        var invoice = Blocked();
        invoice.Unblock(Now, Manager, "Hausse de tarif acceptée");

        invoice.RecordPayment(Payment(500m)).IsSuccess.Should().BeTrue();
        invoice.Status.Should().Be(SupplierInvoiceStatus.PartiallyPaid);
        invoice.AmountOutstanding.Should().Be(700m);

        invoice.RecordPayment(Payment(700.01m)).FirstError.ResourceKey.Should().Be("SupplierPayment_ExceedsOutstanding");
        invoice.RecordPayment(Payment(700m, SupplierPaymentMethod.Cheque, reference: null))
            .FirstError.ResourceKey.Should().Be("SupplierPayment_ReferenceRequired");

        invoice.RecordPayment(Payment(700m, SupplierPaymentMethod.Cash, reference: null)).IsSuccess.Should().BeTrue();
        invoice.Status.Should().Be(SupplierInvoiceStatus.Paid);
        invoice.PaidOn.Should().Be(Today);
        invoice.IsOverdue(Today.AddYears(1)).Should().BeFalse();
    }

    [Fact]
    public void Cancel_AfterAPayment_IsRefused()
    {
        var invoice = Blocked();
        invoice.Unblock(Now, Manager, "Hausse de tarif acceptée");
        invoice.RecordPayment(Payment(100m));

        invoice.Cancel(Now, "Saisie en double").FirstError.ResourceKey.Should().Be("SupplierInvoice_HasPayments");
    }

    // ---- D3: loi 69-21 due dates ---------------------------------------------------------------------

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-5, 60)]
    [InlineData(30, 30)]
    [InlineData(120, 120)]
    [InlineData(180, 120)]
    public void PaymentTerms_DefaultTo60_AndNeverExceed120(int agreed, int effective)
    {
        PaymentTerms.Effective(agreed).Should().Be(effective);
    }

    [Fact]
    public void DueDate_RunsFromTheLatestDelivery_OrFromTheInvoiceDate()
    {
        var invoiceDate = new DateOnly(2026, 9, 30);

        PaymentTerms.DueDate(invoiceDate, new DateOnly(2026, 9, 10), 60).Should().Be(new DateOnly(2026, 11, 9));
        PaymentTerms.DueDate(invoiceDate, null, 60).Should().Be(new DateOnly(2026, 11, 29));

        var invoice = Blocked();
        invoice.SetPaymentTerms(new DateOnly(2026, 9, 10), 200);
        invoice.PaymentTermsDays.Should().Be(120);
        invoice.DueDate.Should().Be(new DateOnly(2027, 1, 8));
    }

    [Fact]
    public void Supplier_WithTermsAbove120Days_IsRefused_AndDefaultsTo60()
    {
        var supplier = new Supplier("BIOMED", "Biomed Maroc");
        supplier.PaymentTermsDays.Should().Be(60);

        supplier.PaymentTermsDays = 150;
        supplier.Validate().Errors.Select(e => e.ResourceKey).Should().Contain("Supplier_PaymentTermsTooLong");
    }

    [Fact]
    public void Overdue_CountsABlockedInvoiceToo()
    {
        var invoice = Blocked();   // delivered 4 days ago, 60 days
        invoice.IsOverdue(Today.AddDays(56)).Should().BeFalse();
        invoice.IsOverdue(Today.AddDays(57)).Should().BeTrue();
        invoice.DaysOverdue(Today.AddDays(66)).Should().Be(10);
    }

    // ---- D3: late-payment penalties and declaration --------------------------------------------------

    [Fact]
    public void Penalty_IsAmountTimesRateTimesDaysOver365()
    {
        // 10 000 MAD, 9.25 % (2.25 + 7), 30 days late.
        LatePayment.Penalty(10_000m, 9.25m, 30).Should().Be(76.03m);
        LatePayment.Penalty(10_000m, 9.25m, 0).Should().Be(0m);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(30, 1)]
    [InlineData(31, 2)]
    [InlineData(61, 3)]
    public void Fine_CountsEachMonthBegun(int days, int months)
    {
        LatePayment.MonthsBegun(days).Should().Be(months);
    }

    [Fact]
    public void Fine_FirstMonthThenEachFurtherMonth()
    {
        // 2.25 % + 2 × 0.85 % = 3.95 % of 10 000.
        LatePayment.Fine(10_000m, 61, 2.25m, 0.85m).Should().Be(395m);
    }

    [Fact]
    public void LateAmounts_ArePaymentsMadeLateInThePeriod_AndWhatIsStillUnpaidAtItsEnd()
    {
        var due = new DateOnly(2026, 7, 31);
        var payments = new[] { (new DateOnly(2026, 7, 20), 1000m), (new DateOnly(2026, 8, 20), 2000m) };

        var late = LatePayment.LateAmounts(due, 5000m, payments, new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30));

        late.Should().HaveCount(2);
        late[0].Should().Be(new LateAmount(2000m, new DateOnly(2026, 8, 20), 20));
        late[1].Should().Be(new LateAmount(2000m, null, 61));
    }

    [Fact]
    public void LatePaymentDeclaration_CarriesPenaltyAndFine_AndExportsAsCsv()
    {
        var source = new LatePaymentSource("Biomed Maroc", "001234567000089", "1234567", "FA-1",
                                           new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 1), new DateOnly(2026, 7, 31),
                                           10_000m, [new PaymentFact(new DateOnly(2026, 8, 30), 10_000m)]);

        var rows = SupplierInvoiceService.ComputeLatePayments([source], new DateOnly(2026, 7, 1),
                                                               new DateOnly(2026, 9, 30), PurchasingSettings.Default);

        var row = rows.Single();
        row.DaysLate.Should().Be(30);
        row.Penalty.Should().Be(76.03m);
        row.Fine.Should().Be(225m);

        var csv = Encoding.UTF8.GetString(SupplierInvoiceService.LatePaymentsCsv(rows));
        csv.Should().Contain("Biomed Maroc;001234567000089;1234567;FA-1;01/06/2026;01/06/2026;31/07/2026;10000,00;10000,00;30/08/2026;30;76,03;225,00");
    }

    // ---- Settings ------------------------------------------------------------------------------------

    [Fact]
    public void Settings_FallBackToDefaults_AndReadCommaDecimals()
    {
        var settings = PurchasingSettings.FromValues(new Dictionary<string, string?>
        {
            [PurchasingSettings.PriceTolerancePercentKey] = "1,5",
            [PurchasingSettings.KeyRatePercentKey] = "pas un nombre",
            [PurchasingSettings.ChargeAccountKeyPrefix + nameof(StockItemKind.Reagent)] = "61221"
        });

        settings.PriceTolerancePercent.Should().Be(1.5m);
        settings.KeyRatePercent.Should().Be(2.25m);
        settings.PenaltyRatePercent.Should().Be(9.25m);
        settings.ChargeAccountFor(StockItemKind.Reagent).Should().Be("61221");
        settings.ChargeAccountFor(StockItemKind.OfficeSupply).Should().Be("6125");
        settings.VatAccount.Should().Be("34552");

        PurchasingSettings.FromValues(settings.ToValues().ToDictionary(p => p.Key, p => (string?)p.Value))
            .ToValues().Should().BeEquivalentTo(settings.ToValues());
    }

    // ---- D4: accounting export -----------------------------------------------------------------------

    [Fact]
    public void Entries_BalancePerInvoiceAndPayment_OnTheCgncAccounts()
    {
        var invoice = new AccountingInvoiceSource("BIOMED", "Biomed Maroc", "FBIOMED", "FA-1", new DateOnly(2026, 9, 1),
        [
            new AccountingInvoiceLine(StockItemKind.Reagent, 1000m, 200m),
            new AccountingInvoiceLine(StockItemKind.Control, 500m, 100m),
            new AccountingInvoiceLine(StockItemKind.OfficeSupply, 100m, 20m)
        ]);
        var transfer = new AccountingPaymentSource("BIOMED", "Biomed Maroc", null, "FA-1", new DateOnly(2026, 9, 30),
                                                   1000m, SupplierPaymentMethod.Transfer, "VIR-77");
        var cash = new AccountingPaymentSource("BIOMED", "Biomed Maroc", null, "FA-1", new DateOnly(2026, 9, 30),
                                               920m, SupplierPaymentMethod.Cash, null);

        var entries = SupplierAccountingExport.BuildEntries([invoice], [transfer, cash], PurchasingSettings.Default);

        var purchases = entries.Where(e => e.Journal == "ACH").ToList();
        purchases.Sum(e => e.Debit).Should().Be(purchases.Sum(e => e.Credit)).And.Be(1920m);
        purchases.Single(e => e.Account == "6122").Debit.Should().Be(1500m);
        purchases.Single(e => e.Account == "6125").Debit.Should().Be(100m);
        purchases.Single(e => e.Account == "34552").Debit.Should().Be(320m);
        purchases.Single(e => e.Account == "4411").Should().Match<AccountingEntry>(e => e.ThirdParty == "FBIOMED" && e.Credit == 1920m);

        var bank = entries.Where(e => e.Journal == "BQ").ToList();
        bank.Should().HaveCount(2);
        bank.Single(e => e.Account == "4411").Should().Match<AccountingEntry>(e => e.ThirdParty == "BIOMED" && e.Debit == 1000m && e.Piece == "VIR-77");
        bank.Single(e => e.Account == "5141").Credit.Should().Be(1000m);

        entries.Where(e => e.Journal == "CAI").Single(e => e.Account == "5161").Credit.Should().Be(920m);
    }

    [Fact]
    public void Csv_SageUsesFrenchNumbers_GenericUsesIsoDatesAndPoints()
    {
        var entries = new[]
        {
            new AccountingEntry(new DateOnly(2026, 9, 1), "ACH", "4411", "BIOMED",
                                "Fact. FA-1 Biomed Maroc, réactifs et contrôles de septembre", "FA-1", 0m, 1234.5m)
        };

        var sage = Encoding.UTF8.GetString(SupplierAccountingExport.ToCsv(entries, AccountingExportLayout.Sage));
        sage.Should().StartWith("﻿Date;Journal;Compte général;Compte tiers;Libellé;N° pièce;Débit;Crédit");
        sage.Should().Contain("01/09/2026;ACH;4411;BIOMED;Fact. FA-1 Biomed Maroc, réactifs e;FA-1;;1234,50");

        var generic = Encoding.UTF8.GetString(SupplierAccountingExport.ToCsv(entries, AccountingExportLayout.Generic));
        generic.Should().Contain("2026-09-01;ACH;4411;BIOMED;Fact. FA-1 Biomed Maroc, réactifs et contrôles de septembre;FA-1;0.00;1234.50");
    }

    // ---- D5: permissions -----------------------------------------------------------------------------

    [Fact]
    public void NewRights_AreDeclared_AndGivenToTheDirectorAndTheAccountant()
    {
        Permissions.Exists(Permissions.Inventory.EnterInvoices).Should().BeTrue();
        Permissions.Exists(Permissions.Inventory.ApprovePayments).Should().BeTrue();

        ShippedRole(SystemRoles.ResponsibleBiologist).Should()
            .Contain([Permissions.Inventory.EnterInvoices, Permissions.Inventory.ApprovePayments]);
        ShippedRole(SystemRoles.Accountant).Should().Contain(Permissions.Inventory.EnterInvoices)
            .And.NotContain(Permissions.Inventory.ApprovePayments);
        ShippedRole(SystemRoles.Administrator).Should().Contain(Permissions.Inventory.ApprovePayments);
        ShippedRole(SystemRoles.Technician).Should().NotContain(Permissions.Inventory.EnterInvoices);
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

    // ---- Service ---------------------------------------------------------------------------------------

    private sealed class Harness
    {
        public Harness(Supplier supplier, IReadOnlyList<OrderLineMatchSource> sources, params string[] rights)
        {
            Granted = [.. rights];
            Service = new SupplierInvoiceService(
                new Store<SupplierInvoice>(Invoices),
                new Store<SupplierInvoiceFile>([]),
                new Store<Supplier>([supplier]),
                new Queries(sources, Existing),
                new NoSettings(),
                new UnitOfWork(this),
                new Guard(Granted),
                new User(() => UserId, Granted),
                new Clock());
        }

        public HashSet<string> Granted { get; }
        public Guid UserId { get; set; } = Enterer;
        public List<SupplierInvoice> Invoices { get; } = [];
        public HashSet<string> Existing { get; } = [];
        public List<string?> Reasons { get; } = [];
        public SupplierInvoiceService Service { get; }
    }

    private static (Supplier Supplier, OrderLineMatchSource Source) OrderedAndReceived(decimal received)
    {
        var supplier = new Supplier("BIOMED", "Biomed Maroc") { PaymentTermsDays = 90 };
        var source = new OrderLineMatchSource(Guid.NewGuid(), supplier.Id, Guid.NewGuid(), "GLU — Glucose", received,
                                              0m, 90m, 0.20m, Today.AddDays(-10));
        return (supplier, source);
    }

    private static CreateSupplierInvoiceRequest Request(Supplier supplier, Guid orderLineId, decimal quantity,
                                                        decimal price = 90m, string number = "FA-2026-118") => new()
    {
        SupplierId = supplier.Id,
        InvoiceNumber = number,
        InvoiceDate = Today.AddDays(-5),
        ReceivedOn = Today.AddDays(-2),
        Lines = [new SupplierInvoiceLineRequest(orderLineId, quantity, price, 0.20m)]
    };

    [Fact]
    public async Task Create_MatchesAtOnce_AndDatesTheDueDateFromTheDelivery()
    {
        var (supplier, source) = OrderedAndReceived(10m);
        var harness = new Harness(supplier, [source], Permissions.Inventory.EnterInvoices);

        var outcome = await harness.Service.CreateAsync(Request(supplier, source.PurchaseOrderLineId, 10m));

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Status.Should().Be(SupplierInvoiceStatus.ApprovedForPayment);
        outcome.Value.DueDate.Should().Be(Today.AddDays(-10).AddDays(90));
        harness.Invoices.Single().DeliveryDate.Should().Be(Today.AddDays(-10));
        harness.Reasons.Single().Should().Contain("bonne à payer");
    }

    [Fact]
    public async Task Create_BillingGoodsNotReceived_IsSavedBlocked()
    {
        var (supplier, source) = OrderedAndReceived(4m);
        var harness = new Harness(supplier, [source], Permissions.Inventory.EnterInvoices);

        var outcome = await harness.Service.CreateAsync(Request(supplier, source.PurchaseOrderLineId, 10m));

        outcome.Value.Status.Should().Be(SupplierInvoiceStatus.Blocked);
        outcome.Value.VarianceCount.Should().Be(1);
        harness.Reasons.Single().Should().Contain("bloquée");
    }

    [Fact]
    public async Task Create_ANumberAlreadyEnteredForTheSupplier_IsRefused()
    {
        var (supplier, source) = OrderedAndReceived(10m);
        var harness = new Harness(supplier, [source], Permissions.Inventory.EnterInvoices);
        harness.Existing.Add("FA-2026-118");

        (await harness.Service.CreateAsync(Request(supplier, source.PurchaseOrderLineId, 10m)))
            .FirstError.ResourceKey.Should().Be("SupplierInvoice_NumberAlreadyEntered");
    }

    [Fact]
    public async Task Create_AnotherSuppliersOrderLine_IsRefused()
    {
        var (supplier, source) = OrderedAndReceived(10m);
        var foreign = source with { SupplierId = Guid.NewGuid() };
        var harness = new Harness(supplier, [foreign], Permissions.Inventory.EnterInvoices);

        (await harness.Service.CreateAsync(Request(supplier, source.PurchaseOrderLineId, 10m)))
            .FirstError.ResourceKey.Should().Be("SupplierInvoice_OrderLineNotFound");
    }

    [Fact]
    public async Task Unblock_AndPay_NeedThePaymentApprovalRight()
    {
        var (supplier, source) = OrderedAndReceived(10m);
        var harness = new Harness(supplier, [source], Permissions.Inventory.EnterInvoices);
        var created = await harness.Service.CreateAsync(Request(supplier, source.PurchaseOrderLineId, 10m, price: 95m));
        created.Value.Status.Should().Be(SupplierInvoiceStatus.Blocked);

        // The accountant who keyed it holds only the entry right.
        harness.UserId = Manager;
        (await harness.Service.UnblockAsync(created.Value.InvoiceId, "Nouveau tarif accepté"))
            .FirstError.Code.Should().Be("forbidden");

        harness.Granted.Add(Permissions.Inventory.ApprovePayments);
        (await harness.Service.UnblockAsync(created.Value.InvoiceId, "Nouveau tarif accepté")).IsSuccess.Should().BeTrue();
        harness.Reasons.Last().Should().Contain("Déblocage").And.Contain("Nouveau tarif accepté");

        (await harness.Service.RecordPaymentAsync(created.Value.InvoiceId,
            new RecordSupplierPaymentRequest(Today, 1140m, SupplierPaymentMethod.Transfer, "VIR-9"))).IsSuccess.Should().BeTrue();
        harness.Invoices.Single().Status.Should().Be(SupplierInvoiceStatus.Paid);
    }

    [Fact]
    public void Summary_CountsOverdueDueSoonAndBlocked()
    {
        SupplierInvoiceRow Row(int daysUntilDue, SupplierInvoiceStatus status, decimal total = 100m, decimal paid = 0m) =>
            new(Guid.NewGuid(), Guid.NewGuid(), "S", "N", Today, Today, null, Today.AddDays(daysUntilDue), daysUntilDue,
                status, total, total, paid, 0, false, "U");

        var summary = SupplierInvoiceService.Summarize(
        [
            Row(-3, SupplierInvoiceStatus.Blocked),
            Row(-1, SupplierInvoiceStatus.PartiallyPaid, 100m, 40m),
            Row(5, SupplierInvoiceStatus.ApprovedForPayment),
            Row(12, SupplierInvoiceStatus.ApprovedForPayment),
            Row(-30, SupplierInvoiceStatus.Paid, 100m, 100m)
        ]);

        summary.Overdue.Should().Be(2);
        summary.OverdueAmount.Should().Be(160m);
        summary.DueWithin7Days.Should().Be(1);
        summary.DueWithin15Days.Should().Be(2);
        summary.Blocked.Should().Be(1);
    }

    // ---- Stand-ins -------------------------------------------------------------------------------------

    private sealed class Store<T>(List<T> items) : IRepository<T> where T : AggregateRoot
    {
        public Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(items.FirstOrDefault(i => i.Id == id));
        public Task<T?> GetByIdWithDetailsAsync(Guid id, CancellationToken ct = default) => GetByIdAsync(id, ct);
        public Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) => Task.FromResult(items.Any(i => i.Id == id));
        public Task<IReadOnlyList<T>> ListAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<T>>([.. items.Where(predicate.Compile())]);
        public Task AddAsync(T entity, CancellationToken ct = default) { items.Add(entity); return Task.CompletedTask; }
        public void Update(T entity) { }
        public void SoftDelete(T entity, Guid byUserId, string reason) { }
    }

    private sealed class Queries(IReadOnlyList<OrderLineMatchSource> sources, HashSet<string> existing) : ISupplierInvoiceQueries
    {
        public Task<IReadOnlyList<SupplierInvoiceRow>> GetInvoicesAsync(Guid siteId, bool includeClosed, DateOnly today, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SupplierInvoiceRow>>([]);
        public Task<SupplierInvoiceDetail?> GetDetailAsync(Guid invoiceId, DateOnly today, CancellationToken ct = default) =>
            Task.FromResult<SupplierInvoiceDetail?>(null);
        public Task<IReadOnlyList<SupplierOption>> GetInvoicingSuppliersAsync(Guid siteId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SupplierOption>>([]);
        public Task<IReadOnlyList<InvoiceableOrderLine>> GetInvoiceableLinesAsync(Guid siteId, Guid supplierId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InvoiceableOrderLine>>([]);
        public Task<IReadOnlyList<OrderLineMatchSource>> GetOrderLineSourcesAsync(IReadOnlyCollection<Guid> purchaseOrderLineIds, Guid? excludingInvoiceId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OrderLineMatchSource>>([.. sources.Where(s => purchaseOrderLineIds.Contains(s.PurchaseOrderLineId))]);
        public Task<bool> InvoiceNumberExistsAsync(Guid supplierId, string invoiceNumber, CancellationToken ct = default) =>
            Task.FromResult(existing.Contains(invoiceNumber));
        public Task<IReadOnlyDictionary<string, string?>> GetSettingValuesAsync(string prefix, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>());
        public Task<IReadOnlyList<LatePaymentSource>> GetLatePaymentSourcesAsync(Guid siteId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LatePaymentSource>>([]);
        public Task<IReadOnlyList<AccountingInvoiceSource>> GetAccountingInvoicesAsync(Guid siteId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AccountingInvoiceSource>>([]);
        public Task<IReadOnlyList<AccountingPaymentSource>> GetAccountingPaymentsAsync(Guid siteId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AccountingPaymentSource>>([]);
    }

    private sealed class NoSettings : IConfigurationWriter
    {
        public Task<MarocLIS.Domain.Sequencing.NumberSequence?> GetSequenceAsync(Guid sequenceId, CancellationToken ct = default) =>
            Task.FromResult<MarocLIS.Domain.Sequencing.NumberSequence?>(null);
        public Task<AppSetting?> GetSettingAsync(string key, Guid? siteId, CancellationToken ct = default) =>
            Task.FromResult<AppSetting?>(null);
        public Task AddSettingAsync(AppSetting setting, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class UnitOfWork(Harness harness) : IUnitOfWork
    {
        public string? AuditReason { get; set; }
        public Task<int> SaveChangesAsync(CancellationToken ct = default) { harness.Reasons.Add(AuditReason); return Task.FromResult(1); }
        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Guard(HashSet<string> granted) : IPermissionGuard
    {
        public Result Require(string permission) =>
            granted.Contains(permission) ? Result.Success() : Result.Failure(Error.Forbidden("Forbidden"));
        public Result RequireAny(params string[] permissions) =>
            permissions.Any(granted.Contains) ? Result.Success() : Result.Failure(Error.Forbidden("Forbidden"));
        public Result RequireAll(params string[] permissions) =>
            permissions.All(granted.Contains) ? Result.Success() : Result.Failure(Error.Forbidden("Forbidden"));
        public Task<Result> RequireBiologicalValidationAsync(string departmentCode, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class User(Func<Guid> userId, HashSet<string> granted) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId => userId();
        public string UserName => "user";
        public string FullName => "User";
        public Guid SiteId { get; } = Guid.NewGuid();
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => default;
        public IReadOnlyList<Guid> AccessibleSiteIds => [SiteId];
        public IReadOnlySet<string> Permissions => granted;
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => granted.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "COMPTA-1";
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset Now => AchatsInvoicesTests.Now;
        public DateOnly Today => AchatsInvoicesTests.Today;
    }
}
