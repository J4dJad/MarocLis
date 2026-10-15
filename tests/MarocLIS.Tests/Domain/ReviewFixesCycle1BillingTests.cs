using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Pricing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Tests.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Patient billing as corrected after the third October 2026 review (B1 to B5): refunds off the
/// shift that took the money, one payer per claim batch, deposits never stranded on an invoiced
/// order, the patient's own invoice when the payer is not billed directly, and the payer's tariff
/// basis for an analysis added later.
/// </summary>
public sealed class ReviewFixesCycle1BillingTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));

    // ---- B1: a non-cash refund comes off the shift that took the payment ---------------------------

    [Fact]
    public async Task ACardRefund_ComesOffTheCashiersShift_NotTheRefundersOwn()
    {
        var cashier = new CashSession(Guid.NewGuid(), Guid.NewGuid(), Now.AddHours(-2), 0m);
        cashier.Accumulate(PaymentMethod.Card, 900m);
        var accountant = new CashSession(cashier.SiteId, Guid.NewGuid(), Now.AddHours(-1), 0m);

        var invoice = PatientInvoice(900m);
        var payment = new Payment(900m, PaymentMethod.Card, Today, cashier.OperatorUserId, invoice.SiteId)
        {
            Reference = "AUT-1",
            CashSessionId = cashier.Id
        };
        invoice.RecordPayment(payment).IsSuccess.Should().BeTrue();

        var harness = new Harness(invoices: [invoice], sessions: [cashier, accountant])
        {
            OpenSession = accountant
        };

        (await harness.Service.RefundPaymentAsync(invoice.Id, payment.Id, "Erreur de saisie"))
            .IsSuccess.Should().BeTrue();

        cashier.CardReceipts.Should().Be(0m);
        accountant.CardReceipts.Should().Be(0m);
    }

    [Fact]
    public async Task AChequeDepositRefund_LeavesAClosedShiftAsItWasRemitted()
    {
        var yesterday = new CashSession(Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(-1), 0m);
        yesterday.Accumulate(PaymentMethod.Cheque, 500m);
        yesterday.Close(Now.AddDays(-1).AddHours(8), yesterday.OperatorUserId, 0m, null)
                 .IsSuccess.Should().BeTrue();
        var today = new CashSession(yesterday.SiteId, Guid.NewGuid(), Now.AddHours(-1), 0m);

        var deposit = new Deposit("REC/CASA/2026/000010", yesterday.SiteId, Guid.NewGuid(), Guid.NewGuid(),
                                  500m, PaymentMethod.Cheque, Now.AddDays(-1), yesterday.OperatorUserId)
        {
            Reference = "CHQ-1",
            CashSessionId = yesterday.Id
        };

        var harness = new Harness(sessions: [yesterday, today], deposits: [deposit]) { OpenSession = today };

        (await harness.Service.RefundDepositAsync(deposit.Id, "Dossier annulé")).IsSuccess.Should().BeTrue();

        deposit.Status.Should().Be(DepositStatus.Refunded);
        yesterday.ChequeReceipts.Should().Be(500m);
        today.ChequeReceipts.Should().Be(0m);
    }

    // ---- B2: one payer per claim batch, prescriber INPE required as on the claims screen -----------

    [Fact]
    public async Task AClaimBatch_KeepsToOnePayer()
    {
        var cnss = Payer("CNSS");
        var cnops = Payer("CNOPS");
        var rejected = ClaimInvoice(cnss, "FAC/CASA/2026/000801");
        rejected.ClaimStatus = ClaimStatus.Rejected;
        var fresh = ClaimInvoice(cnops, "FAC/CASA/2026/000802");

        var harness = new Harness(invoices: [rejected, fresh], payers: [cnss, cnops]);

        var result = await harness.Service.SubmitClaimBatchAsync([rejected.Id, fresh.Id]);

        result.IsSuccess.Should().BeTrue();
        result.Value.Submitted.Should().Be(1);
        result.Value.Skipped.Single().ResourceKey.Should().Be("Claim_MixedPayers");
        rejected.ClaimStatus.Should().Be(ClaimStatus.Submitted);
        fresh.ClaimStatus.Should().Be(ClaimStatus.Prepared);
        fresh.ClaimReference.Should().BeNull();
    }

    [Fact]
    public async Task AClaim_WithoutThePrescribersInpe_IsNotSentToAPayerRequiringIt()
    {
        var cnss = Payer("CNSS");
        cnss.RequiresPrescription = true;
        var invoice = ClaimInvoice(cnss, "FAC/CASA/2026/000803");
        invoice.PrescriberInpe = null;

        var harness = new Harness(invoices: [invoice], payers: [cnss]);

        var result = await harness.Service.SubmitClaimBatchAsync([invoice.Id]);

        result.FirstError.ResourceKey.Should().Be("Invoice_ClaimRequiresPrescriberInpe");
        invoice.ClaimStatus.Should().Be(ClaimStatus.Prepared);
        harness.Commits.Should().Be(0);
    }

    // ---- B3: a deposit is never left stranded --------------------------------------------------------

    [Fact]
    public async Task UnderDirectBilling_ADepositIsCappedAtThePatientsShare()
    {
        var cnss = Payer("CNSS");
        var order = CoveredOrder(cnss, directBilling: true, price: 1_000m, payerShare: 700m);
        var harness = new Harness(orders: [order], payers: [cnss]);

        var refused = await harness.Service.TakeDepositAsync(
            new TakeDepositRequest(order.Id, 1_000m, PaymentMethod.Card, "AUT-1", null, null));

        refused.FirstError.ResourceKey.Should().Be("Deposit_ExceedsOrderTotal");
        refused.FirstError.Args.Single().Should().Be(300m);

        (await harness.Service.TakeDepositAsync(
                new TakeDepositRequest(order.Id, 300m, PaymentMethod.Card, "AUT-1", null, null)))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task WithoutDirectBilling_ADepositMayCoverTheWholeOrder()
    {
        var cnss = Payer("CNSS");
        var order = CoveredOrder(cnss, directBilling: false, price: 1_000m, payerShare: 700m);
        var harness = new Harness(orders: [order], payers: [cnss]);

        (await harness.Service.TakeDepositAsync(
                new TakeDepositRequest(order.Id, 1_000m, PaymentMethod.Card, "AUT-1", null, null)))
            .IsSuccess.Should().BeTrue();
    }

    [SqlServerFact]
    public async Task AnInvoicedOrder_StillHoldingADeposit_StaysListedForItsRefund()
    {
        using var database = new ScratchDatabase();
        var site = InvoiceableSite();
        var patient = new Patient("MRN-0001", "ALAOUI", "Fatima", new DateOnly(1980, 1, 1), Gender.Female, site.Id);

        var invoiced = new LabOrder("2026-000901", patient.Id, site.Id, Now.AddDays(-2), Guid.NewGuid())
        {
            InvoiceId = Guid.NewGuid()
        };
        var invoicedClean = new LabOrder("2026-000902", patient.Id, site.Id, Now.AddDays(-2), Guid.NewGuid())
        {
            InvoiceId = Guid.NewGuid()
        };

        // 500 taken, 300 absorbed by the invoice after an analysis was withdrawn: 200 still held.
        var deposit = new Deposit("REC/CASA/2026/000011", site.Id, invoiced.Id, patient.Id, 500m,
                                  PaymentMethod.Card, Now.AddDays(-2), Guid.NewGuid()) { Reference = "AUT-2" };
        deposit.ApplyTo(invoiced.InvoiceId!.Value, 300m, Now.AddDays(-1)).IsSuccess.Should().BeTrue();

        await using (var db = database.Context())
        {
            db.Add(site);
            db.Add(patient);
            db.AddRange(invoiced, invoicedClean);
            db.Add(deposit);
            await db.SaveChangesAsync();
        }

        await using (var db = database.Context())
        {
            var rows = await new BillingQueries(db).GetUninvoicedOrdersAsync(site.Id);

            rows.Select(r => r.AccessionNumber).Should().Equal("2026-000901");
            rows.Single().DepositHeld.Should().Be(200m);
        }
    }

    // ---- B4: no direct billing, the patient's own invoice -------------------------------------------

    [Fact]
    public async Task AnOrderNotBilledDirectly_IsInvoicedToThePatient_AndNotClaimed()
    {
        var cnss = Payer("CNSS");
        var order = CoveredOrder(cnss, directBilling: false, price: 1_000m, payerShare: 700m);
        var harness = new Harness(orders: [order], payers: [cnss], patients: [PatientOf(order)]);

        var result = await harness.Service.IssueInvoiceAsync([order.Id]);

        result.IsSuccess.Should().BeTrue();
        var invoice = harness.Added.Single();
        invoice.PayerId.Should().BeNull();
        invoice.BilledToName.Should().Be("ALAOUI Fatima");
        invoice.ClaimStatus.Should().Be(ClaimStatus.NotApplicable);
        invoice.PayerShareAmount.Should().Be(0m);
        invoice.OutstandingAmount.Should().Be(1_000m);
    }

    [Fact]
    public async Task AnOrderBilledDirectly_IsStillInvoicedToThePayer_AsAClaim()
    {
        var cnss = Payer("CNSS");
        var order = CoveredOrder(cnss, directBilling: true, price: 1_000m, payerShare: 700m);
        var harness = new Harness(orders: [order], payers: [cnss], patients: [PatientOf(order)]);

        (await harness.Service.IssueInvoiceAsync([order.Id])).IsSuccess.Should().BeTrue();

        var invoice = harness.Added.Single();
        invoice.PayerId.Should().Be(cnss.Id);
        invoice.BilledToName.Should().Be("CNSS");
        invoice.ClaimStatus.Should().Be(ClaimStatus.Prepared);
        invoice.PayerShareAmount.Should().Be(700m);
    }

    [Fact]
    public async Task AnInvoiceToAnotherPayer_UnderAnExpiredAgreement_IsNotPreparedAsAClaim()
    {
        var cnss = Payer("CNSS");
        var mutuelle = Payer("MUT");
        mutuelle.AgreementValidUntil = Today.AddDays(-15);
        var order = CoveredOrder(cnss, directBilling: false, price: 1_000m, payerShare: 700m);
        var harness = new Harness(orders: [order], payers: [cnss, mutuelle], patients: [PatientOf(order)]);

        (await harness.Service.IssueInvoiceAsync([order.Id], payerIdOverride: mutuelle.Id))
            .IsSuccess.Should().BeTrue();

        harness.Added.Single().ClaimStatus.Should().Be(ClaimStatus.NotApplicable);
    }

    [Fact]
    public async Task OrdersBilledDirectlyAndNot_AreNotGroupedOnOneInvoice()
    {
        var cnss = Payer("CNSS");
        var direct = CoveredOrder(cnss, directBilling: true, price: 1_000m, payerShare: 700m);
        var notDirect = CoveredOrder(cnss, directBilling: false, price: 500m, payerShare: 350m,
                                     patientId: direct.PatientId);
        var harness = new Harness(orders: [direct, notDirect], payers: [cnss], patients: [PatientOf(direct)]);

        (await harness.Service.IssueInvoiceAsync([direct.Id, notDirect.Id]))
            .FirstError.ResourceKey.Should().Be("Invoice_MultiplePayers");
    }

    // ---- B5: an added analysis is split on the payer's own tariff basis -----------------------------

    [Fact]
    public async Task AnAddedAnalysis_IsSplitOnTheLaboratoryPrice_ForAPayerThatUsesIt()
    {
        var insurer = Payer("ASSUR");
        insurer.UsesNationalReferenceTariff = false;
        var order = CoveredOrder(insurer, directBilling: true, price: 100m, payerShare: 80m, rate: 0.80m);

        var composer = new OrderComposer(
            Stub<IRepository<AnalysisTest>>.Create(),
            Stub<IRepository<TestPanel>>.Create(),
            Stub<IRepository<Sample>>.Create(),
            Stub<IRepository<TestResult>>.Create(),
            Stub<IRepository<Payer>>.With(("GetByIdAsync", _ => Task.FromResult<Payer?>(insurer))),
            Stub<ISequenceAllocator>.Create(),
            Stub<ICurrentUser>.Create(),
            Clock(),
            new OrderRegistrationOptions());

        var coverage = await composer.RegisteredCoverageAsync(order);

        coverage.UsesReferenceTariff.Should().BeFalse();
        coverage.DirectBilling.Should().BeTrue();

        // 200 DH on the laboratory's list, a reference base of 100.
        var pricing = ActPricingCalculator.Price(
        [
            new ActPricingInput
            {
                AnalysisTestId = Guid.NewGuid(),
                AnalysisCode = "FER",
                Description = "Ferritine",
                ServiceDate = Today,
                Coefficient = 100m,
                LetterKeyValue = 1m,
                ChargedUnitPrice = 200m
            }
        ], coverage, 0m);

        pricing.Acts.Single().PayerShare.Amount.Should().Be(160m);
        pricing.Acts.Single().PatientShare.Amount.Should().Be(40m);
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static Payer Payer(string code) =>
        new(code, new MultilingualText(code), PayerKind.AmoCnss)
        {
            ThirdPartyPaymentAgreement = true,
            RequiresPrescription = false
        };

    private static Invoice PatientInvoice(decimal price)
    {
        var invoice = new Invoice(string.Empty, Guid.NewGuid(), InvoiceKind.Invoice, Today, Guid.NewGuid())
        {
            BilledToName = "ALAOUI Fatima"
        };
        invoice.AddLine(new InvoiceLine("Analyse", price)).IsSuccess.Should().BeTrue();
        invoice.Issue("FAC/CASA/2026/000800", Now, null).IsSuccess.Should().BeTrue();
        return invoice;
    }

    private static Invoice ClaimInvoice(Payer payer, string number)
    {
        var invoice = new Invoice(string.Empty, Guid.NewGuid(), InvoiceKind.Invoice, Today, Guid.NewGuid())
        {
            BilledToName = payer.Name.Fr,
            PayerId = payer.Id,
            PrescriberInpe = "123456789",
            ClaimStatus = ClaimStatus.Prepared
        };
        var line = new InvoiceLine("Analyse", 400m);
        invoice.AddLine(line).IsSuccess.Should().BeTrue();
        line.SetCoverageSplit(0.70m, payerUsesReferenceTariff: false);
        invoice.Issue(number, Now, null).IsSuccess.Should().BeTrue();
        return invoice;
    }

    private static LabOrder CoveredOrder(Payer payer, bool directBilling, decimal price, decimal payerShare,
                                         decimal rate = 0.70m, Guid? patientId = null)
    {
        var order = new LabOrder($"2026-{Random.Shared.Next(100_000, 999_999)}", patientId ?? Guid.NewGuid(),
                                 Guid.NewGuid(), Now.AddDays(-3), Guid.NewGuid())
        {
            PayerId = payer.Id,
            ReimbursementRateAtRegistration = rate,
            DirectBillingAtRegistration = directBilling
        };

        order.AddItem(new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine)
        {
            ChargedPrice = price,
            ReferenceTariffBase = price,
            PayerShare = payerShare,
            PatientShare = price - payerShare,
            BilledActCode = "B100"
        }).IsSuccess.Should().BeTrue();

        return order;
    }

    private static Patient PatientOf(LabOrder order)
    {
        var patient = new Patient("MRN-0002", "ALAOUI", "Fatima", new DateOnly(1980, 1, 1), Gender.Female,
                                  order.SiteId);
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(patient, order.PatientId);
        return patient;
    }

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

    /// <summary>A billing service over a few invoices, orders, tills and deposits, the rest stubbed.</summary>
    private sealed class Harness
    {
        public Harness(Invoice[]? invoices = null, LabOrder[]? orders = null, CashSession[]? sessions = null,
                       Deposit[]? deposits = null, Payer[]? payers = null, Patient[]? patients = null)
        {
            invoices ??= [];
            orders ??= [];
            sessions ??= [];
            payers ??= [];
            patients ??= [];
            var held = (deposits ?? []).ToList();
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
                    ("GetByIdAsync", FindInvoice),
                    ("AddAsync", args => { Added.Add((Invoice)args![0]!); return Task.CompletedTask; })),
                Stub<IRepository<LabOrder>>.With(("GetByIdWithDetailsAsync", FindOrder)),
                Stub<IRepository<Patient>>.With(("GetByIdWithDetailsAsync", args =>
                    Task.FromResult(patients.FirstOrDefault(p => p.Id == (Guid)args![0]!)))),
                Stub<IRepository<Payer>>.With(("GetByIdAsync", args =>
                    Task.FromResult(payers.FirstOrDefault(p => p.Id == (Guid)args![0]!)))),
                Stub<IRepository<LabSite>>.With(("GetByIdAsync", _ => Task.FromResult<LabSite?>(site))),
                Stub<IRepository<AppUser>>.Create(),
                Stub<IRepository<CashSession>>.With(("GetByIdAsync", args =>
                    Task.FromResult(sessions.FirstOrDefault(s => s.Id == (Guid)args![0]!)))),
                Stub<IRepository<Deposit>>.With(
                    ("GetByIdAsync", args => Task.FromResult(held.FirstOrDefault(d => d.Id == (Guid)args![0]!))),
                    ("ListAsync", _ => Task.FromResult<IReadOnlyList<Deposit>>(held.ToList())),
                    ("AddAsync", args => { held.Add((Deposit)args![0]!); return Task.CompletedTask; })),
                Stub<IBillingQueries>.With(
                    ("GetOpenCashSessionAsync", _ => Task.FromResult(OpenSession))),
                Stub<ISequenceAllocator>.With(
                    ("NextAsync", _ => Task.FromResult(Result.Success("TP/CASA/2026/000009")))),
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

        public List<Invoice> Added { get; } = [];

        public int Commits { get; private set; }
    }

    private sealed class Stub<T> : ReviewFixesBilling2Tests.Stub<T> where T : class;

    /// <summary>
    /// A database of its own, <c>MarocLIS_FixCycle1Billing</c>, created from the model and dropped at
    /// the end. An existing database of that name is refused rather than reused.
    /// </summary>
    private sealed class ScratchDatabase : IDisposable
    {
        private const string Name = "MarocLIS_FixCycle1Billing";
        private readonly string _connectionString;

        public ScratchDatabase()
        {
            _connectionString = new SqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable)) { InitialCatalog = Name }
                .ConnectionString;

            using var context = Context();
            if (context.Database.CanConnect())
                throw new InvalidOperationException($"Database {Name} already exists.");

            context.Database.EnsureCreated();
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
