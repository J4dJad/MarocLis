using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Tests.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Billing, till, claim batches and purchase orders as corrected after the second October 2026
/// review (B3 to B10; B1 and B2 are the data backfill, tested against SQL Server separately).
/// </summary>
public sealed class ReviewFixesBilling3Tests
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));

    // ---- B3: a share on the laboratory price follows the discount --------------------------------

    [Fact]
    public void ADiscount_LowersAShareOnTheLaboratoryPrice_ByThePayersRate()
    {
        var invoice = Draft();
        var line = new InvoiceLine("Analyse", 1_000m);
        invoice.AddLine(line).IsSuccess.Should().BeTrue();
        line.SetCoverageSplit(0.80m, payerUsesReferenceTariff: false);

        invoice.ApplyDiscount(200m, "Geste commercial").IsSuccess.Should().BeTrue();

        // 80 % of the 800 the line finally costs, as at registration — not min(800, 800).
        line.PayerShare.Should().Be(640m);
        invoice.PayerShareAmount.Should().Be(640m);
        invoice.PatientShareAmount.Should().Be(160m);

        // Taking the discount back gives the payer its full share again.
        invoice.ApplyDiscount(0m, string.Empty).IsSuccess.Should().BeTrue();
        invoice.PayerShareAmount.Should().Be(800m);
    }

    [Fact]
    public void ADiscount_OnlyCapsAShareOnTheReferenceTariff()
    {
        var invoice = Draft();
        var line = new InvoiceLine("Analyse", 1_000m) { ReferenceTariffBase = 500m };
        invoice.AddLine(line).IsSuccess.Should().BeTrue();
        line.SetCoverageSplit(0.70m, payerUsesReferenceTariff: true);

        // The tariff share does not depend on the price charged.
        invoice.ApplyDiscount(200m, "Geste commercial").IsSuccess.Should().BeTrue();
        invoice.PayerShareAmount.Should().Be(350m);

        // Only capped once the line costs less than it.
        invoice.ApplyDiscount(700m, "Geste commercial").IsSuccess.Should().BeTrue();
        invoice.PayerShareAmount.Should().Be(300m);
        invoice.PatientShareAmount.Should().Be(0m);
    }

    // ---- B4: a batch-level answer leaves invoices settled one by one alone ------------------------

    [Fact]
    public async Task ABatchLevelRejection_SkipsInvoicesWhoseSettlementIsRecorded()
    {
        var settled = Issued(400m, payerRate: 0.75m, number: "FAC/CASA/2026/000301");
        var refused = Issued(400m, payerRate: 0.75m, number: "FAC/CASA/2026/000302");
        settled.ClaimStatus = ClaimStatus.Submitted;
        refused.ClaimStatus = ClaimStatus.Submitted;

        var harness = new Harness(settled, refused);

        (await harness.Service.RecordPayerSettlementAsync(
                settled.Id, 300m, PayerShortfallHandling.PatientPays, null, "VIR-1"))
            .IsSuccess.Should().BeTrue();
        settled.ClaimStatus.Should().Be(ClaimStatus.Settled);

        var result = await harness.Service.RecordClaimOutcomeAsync(
            [settled.Id, refused.Id], ClaimStatus.Rejected, "Ordonnance illisible");

        result.IsSuccess.Should().BeTrue();
        result.Value.Updated.Should().Be(1);
        result.Value.Skipped.Single().ResourceKey.Should().Be("Claim_OutcomeSkippedSettled");

        settled.ClaimStatus.Should().Be(ClaimStatus.Settled);
        settled.ClaimRejectionReason.Should().BeNull();
        refused.ClaimStatus.Should().Be(ClaimStatus.Rejected);
    }

    [Fact]
    public void APaidClaim_LabelledRejected_CannotBeReopened()
    {
        var invoice = Issued(400m, payerRate: 0.75m);
        invoice.ClaimStatus = ClaimStatus.Submitted;
        invoice.RecordPayment(new Payment(300m, PaymentMethod.ThirdPartyPayer, Today, Guid.NewGuid(),
                                          invoice.SiteId)).IsSuccess.Should().BeTrue();
        invoice.PayerAcceptedAmount = 300m;

        // A stale batch-level refusal written over it before the fix.
        invoice.ClaimStatus = ClaimStatus.Rejected;

        invoice.ReopenRejectedClaim("renvoi").FirstError.ResourceKey
            .Should().Be("Claim_SettlementAlreadyRecorded");
        invoice.PayerAcceptedAmount.Should().Be(300m);
        invoice.PaidAmount.Should().Be(300m);
    }

    // ---- B5: one invoice, one registered payer ----------------------------------------------------

    [Fact]
    public async Task OrdersRegisteredWithDifferentPayers_AreNotGroupedOnOneInvoice()
    {
        var patientId = Guid.NewGuid();
        var cnss = Guid.NewGuid();
        var selfPaid = Order("2026-000401", patientId, payerId: null);
        var covered = Order("2026-000402", patientId, payerId: cnss);

        var harness = new Harness(orders: [selfPaid, covered]);

        var result = await harness.Service.IssueInvoiceAsync([covered.Id, selfPaid.Id]);

        result.FirstError.ResourceKey.Should().Be("Invoice_MultiplePayers");
        result.FirstError.Args.Single().Should().Be("2026-000402, 2026-000401");
        selfPaid.InvoiceId.Should().BeNull();
        covered.InvoiceId.Should().BeNull();
    }

    // ---- B6: the last price stays the list price --------------------------------------------------

    [Fact]
    public async Task AReceipt_KeepsTheListPriceForTheNextOrder_AndValuesTheLotNet()
    {
        var siteId = Guid.NewGuid();
        var item = new StockItem("GLU-R1", new MultilingualText("Glucose R1"), StockItemKind.Reagent,
                                 "flacon", siteId);
        var order = new PurchaseOrder("BC/2026/0042", Guid.NewGuid(), siteId, Today, Guid.NewGuid());
        var line = new PurchaseOrderLine(item.Id, 10m, 100m) { DiscountRate = 0.10m };
        order.AddLine(line).IsSuccess.Should().BeTrue();
        order.Approve(Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        order.Send(Now).IsSuccess.Should().BeTrue();

        var service = new PurchaseOrderService(
            Stub<IRepository<PurchaseOrder>>.With(
                ("GetByIdWithDetailsAsync", _ => Task.FromResult<PurchaseOrder?>(order))),
            Stub<IRepository<StockItem>>.With(
                ("GetByIdWithDetailsAsync", _ => Task.FromResult<StockItem?>(item))),
            Stub<IRepository<StockMovement>>.Create(),
            Stub<IPurchaseOrderQueries>.Create(),
            Stub<ISequenceAllocator>.Create(),
            Stub<IUnitOfWork>.Create(),
            Stub<IPermissionGuard>.Create(),
            Stub<ICurrentUser>.With(("get_SiteId", _ => siteId)),
            Clock());

        (await service.ReceiveLineAsync(order.Id, new ReceivePurchaseOrderLineRequest
        {
            LineId = line.Id,
            Quantity = 10m,
            LotNumber = "L2611",
            ExpiryDate = Today.AddYears(1)
        })).IsSuccess.Should().BeTrue();

        item.Lots.Single().UnitPriceMad.Should().Be(90m);
        // The next line is pre-filled with 100 next to its 10 % discount, not with 90.
        item.LastUnitPriceMad.Should().Be(100m);
    }

    // ---- B7: every method reaches the shift report ------------------------------------------------

    [Theory]
    [InlineData(PaymentMethod.Cheque, "CHQ-1")]
    [InlineData(PaymentMethod.Card, "AUT-1")]
    [InlineData(PaymentMethod.BankTransfer, "VIR-1")]
    public async Task ANonCashPayment_IsCountedOnTheOperatorsShift(PaymentMethod method, string reference)
    {
        var invoice = Issued(500m);
        var session = new CashSession(invoice.SiteId, Guid.NewGuid(), Now, 100m);
        var harness = new Harness(invoice) { OpenSession = session };

        (await harness.Service.RecordPaymentAsync(new RecordPaymentRequest(
                invoice.Id, 200m, method, reference, null, null, null)))
            .IsSuccess.Should().BeTrue();

        var summary = CashSessionSummary.From(session, "Caissière", 1);
        (summary.ChequeReceipts + summary.CardReceipts + summary.TransferReceipts).Should().Be(200m);
        session.ExpectedClosingCash.Should().Be(100m);
        invoice.Payments.Single().CashSessionId.Should().Be(session.Id);
    }

    [Fact]
    public async Task ANonCashPayment_StillNeedsNoOpenTill()
    {
        var invoice = Issued(500m);
        var harness = new Harness(invoice);

        (await harness.Service.RecordPaymentAsync(new RecordPaymentRequest(
                invoice.Id, 200m, PaymentMethod.Card, "AUT-1", null, null, null)))
            .IsSuccess.Should().BeTrue();

        (await harness.Service.RecordPaymentAsync(new RecordPaymentRequest(
                invoice.Id, 100m, PaymentMethod.Cash, null, null, null, null)))
            .FirstError.ResourceKey.Should().Be("Payment_CashRequiresOpenSession");
    }

    // ---- B8: a partly refused batch is still a batch ----------------------------------------------

    [Fact]
    public async Task APartlyRefusedSubmission_ReturnsTheBatchReference()
    {
        var fresh = Issued(400m, payerRate: 0.75m, number: "FAC/CASA/2026/000501");
        var alreadySent = Issued(400m, payerRate: 0.75m, number: "FAC/CASA/2026/000502");
        fresh.ClaimStatus = ClaimStatus.Prepared;
        alreadySent.ClaimStatus = ClaimStatus.Submitted;

        var harness = new Harness(fresh, alreadySent);

        var result = await harness.Service.SubmitClaimBatchAsync([fresh.Id, alreadySent.Id]);

        result.IsSuccess.Should().BeTrue();
        result.Value.Reference.Should().Be("TP/CASA/2026/000007");
        result.Value.Submitted.Should().Be(1);
        result.Value.Skipped.Single().ResourceKey.Should().Be("Claim_AlreadySubmitted");
        fresh.ClaimReference.Should().Be("TP/CASA/2026/000007");
        harness.Commits.Should().Be(1);
    }

    [Fact]
    public async Task AWhollyRefusedSubmission_IsStillAFailure()
    {
        var alreadySent = Issued(400m, payerRate: 0.75m);
        alreadySent.ClaimStatus = ClaimStatus.Submitted;
        var harness = new Harness(alreadySent);

        var result = await harness.Service.SubmitClaimBatchAsync([alreadySent.Id]);

        result.FirstError.ResourceKey.Should().Be("Claim_AlreadySubmitted");
        harness.Commits.Should().Be(0);
    }

    // ---- B9: a write-off is not paid ---------------------------------------------------------------

    [Fact]
    public void AWriteOff_ClosesTheBalanceButIsNotCollected()
    {
        var invoice = Issued(1_000m, payerRate: 0.30m);
        invoice.RecordPayment(new Payment(700m, PaymentMethod.Cash, Today, Guid.NewGuid(), invoice.SiteId)
        {
            CashSessionId = Guid.NewGuid()
        }).IsSuccess.Should().BeTrue();
        invoice.RecordPayment(new Payment(300m, PaymentMethod.WriteOff, Today, Guid.NewGuid(), invoice.SiteId))
            .IsSuccess.Should().BeTrue();

        invoice.OutstandingAmount.Should().Be(0m);
        invoice.PaidAmount.Should().Be(1_000m);
        invoice.WrittenOffAmount.Should().Be(300m);
        invoice.CollectedAmount.Should().Be(700m);
    }

    [Fact]
    public void TheReceipt_StatesWhatWasPaid_AndTheWriteOffApart()
    {
        var document = Document(paid: 1_000m,
        [
            new InvoiceDocumentPayment(Today, PaymentMethod.Cash, PaymentStatus.Settled, 700m, null, null),
            new InvoiceDocumentPayment(Today, PaymentMethod.WriteOff, PaymentStatus.Settled, 300m, null, null),
            // A write-off cancelled on resubmission no longer counts.
            new InvoiceDocumentPayment(Today, PaymentMethod.WriteOff, PaymentStatus.Cancelled, 300m, null, null)
        ]);

        document.CollectedAmount.Should().Be(700m);
        document.WrittenOffAmount.Should().Be(300m);
        document.OutstandingAmount.Should().Be(0m);
    }

    // ---- B9 and B10 in SQL: the ledger and the claim batches --------------------------------------

    [SqlServerFact]
    public async Task TheLedgerAndTheBatches_KeepWriteOffsAndCounterPaymentsApart()
    {
        using var database = new ScratchDatabase();
        var siteId = Guid.NewGuid();

        // Deposit applied at issue on 15/09, sent on 01/10, no answer from the payer yet.
        var waiting = Issued(1_000m, payerRate: 0.30m, number: "FAC/CASA/2026/000601", siteId: siteId);
        waiting.RecordPayment(new Payment(700m, PaymentMethod.Card, new DateOnly(2026, 9, 15),
                                          Guid.NewGuid(), siteId)).IsSuccess.Should().BeTrue();
        waiting.ClaimReference = "TP/CASA/2026/00042";
        waiting.ClaimSubmittedOn = new DateOnly(2026, 10, 1);
        waiting.ClaimStatus = ClaimStatus.Submitted;

        // Same batch: the payer refused on 10/10 and the share was written off.
        var refused = Issued(1_000m, payerRate: 0.30m, number: "FAC/CASA/2026/000602", siteId: siteId);
        refused.RecordPayment(new Payment(700m, PaymentMethod.Card, new DateOnly(2026, 9, 15),
                                          Guid.NewGuid(), siteId)).IsSuccess.Should().BeTrue();
        refused.RecordPayment(new Payment(300m, PaymentMethod.WriteOff, new DateOnly(2026, 10, 10),
                                          Guid.NewGuid(), siteId)).IsSuccess.Should().BeTrue();
        refused.PayerId = waiting.PayerId;
        refused.ClaimReference = "TP/CASA/2026/00042";
        refused.ClaimSubmittedOn = new DateOnly(2026, 10, 1);
        refused.ClaimStatus = ClaimStatus.Rejected;
        refused.PayerAcceptedAmount = 0m;

        await using (var db = database.Context())
        {
            db.Invoices.AddRange(waiting, refused);
            await db.SaveChangesAsync();
        }

        await using (var db = database.Context())
        {
            var queries = new BillingQueries(db);

            var ledger = await queries.GetInvoicesAsync(siteId, null, null, 1, 50, Today);
            var row = ledger.Items.Single(r => r.InvoiceId == refused.Id);
            row.Paid.Should().Be(700m);
            row.WrittenOff.Should().Be(300m);
            row.Outstanding.Should().Be(0m);

            var claimRows = await queries.GetClaimInvoicesAsync("TP/CASA/2026/00042");
            claimRows.Single(r => r.InvoiceId == refused.Id).Paid.Should().Be(700m);

            var batch = (await queries.GetClaimBatchesAsync(siteId, new DateOnly(2026, 1, 1), Today)).Single();
            // The write-off of 10/10 answered the claim; the card payment of 15/09 did not.
            batch.LastOutcomeOn.Should().Be(new DateOnly(2026, 10, 10));
        }

        await using (var db = database.Context())
        {
            db.Payments.RemoveRange(db.Payments.Where(p => p.InvoiceId == refused.Id));
            db.Invoices.RemoveRange(db.Invoices.Where(i => i.Id == refused.Id));
            await db.SaveChangesAsync();
        }

        await using (var db = database.Context())
        {
            var batch = (await new BillingQueries(db).GetClaimBatchesAsync(
                siteId, new DateOnly(2026, 1, 1), Today)).Single();
            batch.LastOutcomeOn.Should().BeNull();
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static Invoice Draft(Guid? siteId = null) =>
        new(string.Empty, siteId ?? Guid.NewGuid(), InvoiceKind.Invoice, Today, Guid.NewGuid())
        {
            BilledToName = "ALAOUI Fatima",
            PayerId = Guid.NewGuid()
        };

    private static Invoice Issued(decimal price, decimal payerRate = 0m,
                                  string number = "FAC/CASA/2026/000300", Guid? siteId = null)
    {
        var invoice = Draft(siteId);
        var line = new InvoiceLine("Analyse", price);
        invoice.AddLine(line).IsSuccess.Should().BeTrue();
        if (payerRate > 0m) line.SetCoverageSplit(payerRate, payerUsesReferenceTariff: false);

        invoice.Issue(number, Now, null).IsSuccess.Should().BeTrue();
        return invoice;
    }

    private static LabOrder Order(string accession, Guid patientId, Guid? payerId) =>
        new(accession, patientId, Guid.NewGuid(), Now.AddDays(-20), Guid.NewGuid()) { PayerId = payerId };

    private static InvoiceDocument Document(decimal paid, IReadOnlyList<InvoiceDocumentPayment> payments) =>
        new(Guid.NewGuid(), "FAC/CASA/2026/000700", InvoiceKind.Invoice, InvoiceStatus.Paid, Today, null,
            new InvoiceIssuer("Labo", "Labo SARL", null, null, null, null, null, null, null, null, null,
                              null, null, null, null),
            "ALAOUI Fatima", null, null, null, null, null, null, null, null, null, null, null,
            1_000m, 0m, null, 1_000m, 0m, 1_000m, 300m, 700m, paid, null, [], payments);

    private static LabSite InvoiceableSite() =>
        new("CASA", new MultilingualText("Casablanca"), LabSiteKind.MainLaboratory)
        {
            LegalName = "Laboratoire Test SARL",
            Ice = "003456789000012",
            FiscalId = "40112233",
            AddressLine = new MultilingualText("12 rue Test"),
            City = "Casablanca"
        };

    private static IClock Clock() =>
        Stub<IClock>.With(("get_Now", _ => Now), ("get_Today", _ => Today));

    /// <summary>A billing service over a few invoices and orders, with everything else stubbed.</summary>
    private sealed class Harness
    {
        public Harness(params Invoice[] invoices) : this(invoices, []) { }

        public Harness(Invoice[]? invoices = null, LabOrder[]? orders = null)
        {
            invoices ??= [];
            orders ??= [];
            var user = Guid.NewGuid();
            var siteId = invoices.FirstOrDefault()?.SiteId ?? Guid.NewGuid();
            var site = InvoiceableSite();

            Task<Invoice?> FindInvoice(object?[]? args) =>
                Task.FromResult(invoices.FirstOrDefault(i => i.Id == (Guid)args![0]!));
            Task<LabOrder?> FindOrder(object?[]? args) =>
                Task.FromResult(orders.FirstOrDefault(o => o.Id == (Guid)args![0]!));

            Service = new BillingService(
                Stub<IRepository<Invoice>>.With(
                    ("GetByIdWithDetailsAsync", FindInvoice),
                    ("GetByIdAsync", FindInvoice)),
                Stub<IRepository<LabOrder>>.With(("GetByIdWithDetailsAsync", FindOrder)),
                Stub<IRepository<Patient>>.Create(),
                Stub<IRepository<Payer>>.Create(),
                Stub<IRepository<LabSite>>.With(("GetByIdAsync", _ => Task.FromResult<LabSite?>(site))),
                Stub<IRepository<AppUser>>.Create(),
                Stub<IRepository<CashSession>>.Create(),
                Stub<IRepository<Deposit>>.Create(),
                Stub<IBillingQueries>.With(
                    ("GetOpenCashSessionAsync", _ => Task.FromResult(OpenSession))),
                Stub<ISequenceAllocator>.With(
                    ("NextAsync", _ => Task.FromResult(Result.Success("TP/CASA/2026/000007")))),
                Stub<IUnitOfWork>.With(
                    ("CommitAsync", _ => { Commits++; return Task.CompletedTask; })),
                Stub<IPermissionGuard>.Create(),
                Stub<ICurrentUser>.With(
                    ("get_UserId", _ => user),
                    ("get_SiteId", _ => siteId)),
                Clock());
        }

        public BillingService Service { get; }

        public CashSession? OpenSession { get; init; }

        public int Commits { get; private set; }
    }

    private sealed class Stub<T> : ReviewFixesBilling2Tests.Stub<T> where T : class;

    /// <summary>
    /// A database of its own, <c>MarocLIS_FixBilling3</c>, brought up by migration and dropped at the
    /// end. An existing database of that name is refused rather than reused.
    /// </summary>
    private sealed class ScratchDatabase : IDisposable
    {
        private const string Name = "MarocLIS_FixBilling3";
        private readonly string _connectionString;

        public ScratchDatabase()
        {
            _connectionString = new SqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable)) { InitialCatalog = Name }
                .ConnectionString;

            using var context = Context();
            if (context.Database.CanConnect())
                throw new InvalidOperationException($"Database {Name} already exists.");

            context.Database.Migrate();
        }

        public LisDbContext Context() =>
            new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(_connectionString).Options);

        public void Dispose()
        {
            SqlConnection.ClearAllPools();
            using var context = Context();
            context.Database.EnsureDeleted();
        }
    }
}
