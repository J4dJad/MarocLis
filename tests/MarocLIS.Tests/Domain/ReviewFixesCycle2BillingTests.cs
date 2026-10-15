using System.Linq.Expressions;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Orders;
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
/// Patient billing as corrected after the fourth October 2026 review (B1 to B5): no analysis added
/// to an invoiced order, a wholly credited invoice frees its orders, the cashier's shift read fresh,
/// the deposit ceiling under direct billing, and invoiced orders holding a deposit told apart.
/// </summary>
public sealed class ReviewFixesCycle2BillingTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));

    // ---- B1: an analysis is not added to an invoiced order ------------------------------------------

    [Fact]
    public async Task AnAnalysis_IsNotAddedToAnInvoicedOrder()
    {
        var order = new LabOrder("2026-000950", Guid.NewGuid(), Guid.NewGuid(), Now.AddHours(-2), Guid.NewGuid())
        {
            InvoiceId = Guid.NewGuid()
        };

        var composer = new OrderComposer(
            Stub<IRepository<AnalysisTest>>.Create(),
            Stub<IRepository<TestPanel>>.Create(),
            Stub<IRepository<Sample>>.Create(),
            Stub<IRepository<TestResult>>.Create(),
            Stub<IRepository<Payer>>.Create(),
            Stub<ISequenceAllocator>.Create(),
            Stub<ICurrentUser>.Create(),
            Clock(),
            new OrderRegistrationOptions());

        var service = new OrderAmendmentService(
            Stub<IRepository<LabOrder>>.With(
                ("GetByIdWithDetailsAsync", _ => Task.FromResult<LabOrder?>(order))),
            Stub<IRepository<Patient>>.Create(),
            Stub<IRepository<Sample>>.Create(),
            composer,
            Stub<IOrderQueries>.Create(),
            Stub<IUnitOfWork>.Create(),
            Stub<IPermissionGuard>.Create(),
            Stub<ICurrentUser>.Create(),
            Stub<IDataAccessAuditor>.Create(),
            Clock(),
            Stub<IRepository<TestResult>>.Create());

        var result = await service.AddAnalysesAsync(order.Id, [Guid.NewGuid()], [], "Demande du médecin");

        result.FirstError.ResourceKey.Should().Be("Order_InvoicedCannotAddItem");
        result.FirstError.Args.Single().Should().Be("2026-000950");
        order.Items.Should().BeEmpty();
    }

    // ---- B2: a wholly credited invoice frees its orders for a new invoice ---------------------------

    [Fact]
    public async Task AFullCreditNote_FreesTheOrder_WhichIsThenInvoicedToThePayer()
    {
        var cnss = Payer("CNSS");
        // Registered with the third-party box unticked by mistake: invoiced to the patient in full.
        var order = CoveredOrder(cnss, directBilling: false, price: 1_000m, payerShare: 700m);
        var harness = new Harness(orders: [order], payers: [cnss], patients: [PatientOf(order)]);

        (await harness.Service.IssueInvoiceAsync([order.Id])).IsSuccess.Should().BeTrue();
        var wrong = harness.Invoices.Single();
        wrong.PayerId.Should().BeNull();

        (await harness.Service.IssueCreditNoteAsync(wrong.Id, "Tiers payant non coché"))
            .IsSuccess.Should().BeTrue();

        wrong.IsFullyCredited.Should().BeTrue();
        order.InvoiceId.Should().BeNull();

        var reissued = await harness.Service.IssueInvoiceAsync([order.Id], payerIdOverride: cnss.Id);

        reissued.IsSuccess.Should().BeTrue();
        var right = harness.Invoices.Single(i => i.Id == reissued.Value);
        right.PayerId.Should().Be(cnss.Id);
        right.PayerShareAmount.Should().Be(700m);
        right.ClaimStatus.Should().Be(ClaimStatus.Prepared);
        order.InvoiceId.Should().Be(right.Id);
    }

    [Fact]
    public async Task APartialCreditNote_LeavesTheOrderOnItsInvoice()
    {
        var cnss = Payer("CNSS");
        var order = CoveredOrder(cnss, directBilling: false, price: 1_000m, payerShare: 700m);
        var harness = new Harness(orders: [order], payers: [cnss], patients: [PatientOf(order)]);

        (await harness.Service.IssueInvoiceAsync([order.Id])).IsSuccess.Should().BeTrue();
        var invoice = harness.Invoices.Single();

        (await harness.Service.IssueCreditNoteAsync(invoice.Id, "Geste commercial", 100m))
            .IsSuccess.Should().BeTrue();

        order.InvoiceId.Should().Be(invoice.Id);
        (await harness.Service.IssueInvoiceAsync([order.Id]))
            .FirstError.ResourceKey.Should().Be("Invoice_OrderAlreadyInvoiced");

        // The balance credited later completes the reversal: the order is freed then.
        (await harness.Service.IssueCreditNoteAsync(invoice.Id, "Annulation")).IsSuccess.Should().BeTrue();
        order.InvoiceId.Should().BeNull();
    }

    // ---- B3: the cashier's shift is read fresh after a change made elsewhere ------------------------

    [SqlServerFact]
    public async Task ARefundOnAnotherWorkstation_DoesNotFailTheCashiersNextPayment()
    {
        using var database = new ScratchDatabase();
        var site = InvoiceableSite();
        var cashierId = Guid.NewGuid();
        var shift = new CashSession(site.Id, cashierId, Now.AddHours(-2), 100m);
        shift.Accumulate(PaymentMethod.Card, 900m);

        await using (var db = database.Context())
        {
            db.Add(site);
            db.Add(shift);
            await db.SaveChangesAsync();
        }

        // The cashier's screen keeps its context and has the shift tracked.
        await using var cashier = database.Context();
        var queries = new BillingQueries(cashier);
        (await queries.GetOpenCashSessionAsync(cashierId)).Should().NotBeNull();

        // The accountant refunds the card payment from another workstation.
        await using (var accountant = database.Context())
        {
            var taken = await accountant.CashSessions.SingleAsync(s => s.Id == shift.Id);
            taken.ReverseNonCash(PaymentMethod.Card, 900m);
            await accountant.SaveChangesAsync();
        }

        var session = await queries.GetOpenCashSessionAsync(cashierId);
        session!.CardReceipts.Should().Be(0m);
        session.Accumulate(PaymentMethod.Cash, 200m);

        await cashier.SaveChangesAsync();

        await using (var db = database.Context())
        {
            var saved = await db.CashSessions.AsNoTracking().SingleAsync(s => s.Id == shift.Id);
            saved.CardReceipts.Should().Be(0m);
            saved.ExpectedCashReceipts.Should().Be(200m);
        }
    }

    // ---- B4: the deposit ceiling is the patient's share under direct billing ------------------------

    [Fact]
    public void UnderDirectBilling_TheDepositCeiling_IsThePatientsShareLessWhatIsHeld()
    {
        var direct = Row(directBilling: true, depositHeld: 0m);
        direct.DepositCeiling.Should().Be(300m);

        (direct with { DepositHeld = 300m }).DepositCeiling.Should().Be(0m);

        Row(directBilling: false, depositHeld: 300m).DepositCeiling.Should().Be(700m);
        (Row(directBilling: true, depositHeld: 0m) with { PayerId = null }).DepositCeiling.Should().Be(1_000m);
    }

    // ---- B5: an invoiced order listed for its deposit is marked and not counted ---------------------

    [SqlServerFact]
    public async Task TheBillingDesk_TellsInvoicedAndReinvoicedOrdersApart()
    {
        using var database = new ScratchDatabase();
        var site = InvoiceableSite();
        var patient = new Patient("MRN-0003", "ALAOUI", "Fatima", new DateOnly(1980, 1, 1), Gender.Female, site.Id);
        var department = new Department("BIO", new MultilingualText("Biochimie"));
        var specimen = new SpecimenType("SER", new MultilingualText("Sérum"));
        var analysis = new AnalysisTest("GLU", new MultilingualText("Glycémie"), department.Id, specimen.Id);

        var invoice = new Invoice(string.Empty, site.Id, InvoiceKind.Invoice, Today, Guid.NewGuid())
        {
            BilledToName = "ALAOUI Fatima",
            PatientId = patient.Id
        };

        var invoiced = OrderAt(site, patient, analysis, "2026-000961");
        invoiced.InvoiceId = invoice.Id;
        var fresh = OrderAt(site, patient, analysis, "2026-000962");
        var credited = OrderAt(site, patient, analysis, "2026-000963");

        invoice.AddLine(new InvoiceLine("Analyse", 1_000m) { LabOrderId = invoiced.Id }).IsSuccess.Should().BeTrue();
        invoice.AddLine(new InvoiceLine("Analyse", 300m) { LabOrderId = credited.Id }).IsSuccess.Should().BeTrue();
        invoice.Issue("FAC/CASA/2026/000960", Now, null).IsSuccess.Should().BeTrue();

        var deposit = new Deposit("REC/CASA/2026/000020", site.Id, invoiced.Id, patient.Id, 1_700m,
                                  PaymentMethod.Card, Now.AddDays(-2), Guid.NewGuid()) { Reference = "AUT-3" };
        deposit.ApplyTo(invoice.Id, 1_000m, Now.AddDays(-1)).IsSuccess.Should().BeTrue();

        await using (var db = database.Context())
        {
            db.Add(site);
            db.Add(patient);
            db.AddRange(department, specimen, analysis);
            db.AddRange(invoiced, fresh, credited);
            db.Add(invoice);
            db.Add(deposit);
            await db.SaveChangesAsync();
        }

        await using (var db = database.Context())
        {
            var rows = (await new BillingQueries(db).GetUninvoicedOrdersAsync(site.Id))
                .ToDictionary(r => r.AccessionNumber);

            rows.Keys.Should().BeEquivalentTo(["2026-000961", "2026-000962", "2026-000963"]);

            rows["2026-000961"].InvoiceId.Should().Be(invoice.Id);
            rows["2026-000961"].InvoiceNumber.Should().Be("FAC/CASA/2026/000960");
            rows["2026-000961"].DepositHeld.Should().Be(700m);
            rows["2026-000961"].IsInvoiceable.Should().BeFalse();

            rows["2026-000962"].IsInvoiceable.Should().BeTrue();
            rows["2026-000962"].PreviouslyInvoiced.Should().BeFalse();

            rows["2026-000963"].IsInvoiceable.Should().BeTrue();
            rows["2026-000963"].PreviouslyInvoiced.Should().BeTrue();
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static BillableOrderRow Row(bool directBilling, decimal depositHeld) =>
        new(Guid.NewGuid(), "2026-000970", Guid.NewGuid(), "ALAOUI Fatima", Now, "CNSS", Guid.NewGuid(),
            1_000m, 700m, 300m, 1, LabOrderStatus.Registered, depositHeld, directBilling);

    private static LabOrder OrderAt(LabSite site, Patient patient, AnalysisTest analysis, string accession)
    {
        var order = new LabOrder(accession, patient.Id, site.Id, Now.AddDays(-2), Guid.NewGuid());
        order.AddItem(new LabOrderItem(analysis.Id, OrderPriority.Routine)
        {
            ChargedPrice = 300m,
            PatientShare = 300m
        }).IsSuccess.Should().BeTrue();
        return order;
    }

    private static Payer Payer(string code) =>
        new(code, new MultilingualText(code), PayerKind.AmoCnss)
        {
            ThirdPartyPaymentAgreement = true,
            RequiresPrescription = false
        };

    private static LabOrder CoveredOrder(Payer payer, bool directBilling, decimal price, decimal payerShare,
                                         decimal rate = 0.70m)
    {
        var order = new LabOrder($"2026-{Random.Shared.Next(100_000, 999_999)}", Guid.NewGuid(),
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

    /// <summary>
    /// A billing service over a few orders and payers; the invoices it issues are found again, so a
    /// credit note can be issued on them.
    /// </summary>
    private sealed class Harness
    {
        private int _sequence;

        public Harness(LabOrder[] orders, Payer[] payers, Patient[] patients)
        {
            var user = Guid.NewGuid();
            var siteId = Guid.NewGuid();
            var site = InvoiceableSite();

            Task<Invoice?> FindInvoice(object?[]? args) =>
                Task.FromResult(Invoices.FirstOrDefault(i => i.Id == (Guid)args![0]!));

            Service = new BillingService(
                Stub<IRepository<Invoice>>.With(
                    ("GetByIdWithDetailsAsync", FindInvoice),
                    ("GetByIdAsync", FindInvoice),
                    ("AddAsync", args => { Invoices.Add((Invoice)args![0]!); return Task.CompletedTask; })),
                Stub<IRepository<LabOrder>>.With(
                    ("GetByIdWithDetailsAsync", args =>
                        Task.FromResult(orders.FirstOrDefault(o => o.Id == (Guid)args![0]!))),
                    ("ListAsync", args =>
                    {
                        var predicate = ((Expression<Func<LabOrder, bool>>)args![0]!).Compile();
                        return Task.FromResult<IReadOnlyList<LabOrder>>([.. orders.Where(predicate)]);
                    })),
                Stub<IRepository<Patient>>.With(("GetByIdWithDetailsAsync", args =>
                    Task.FromResult(patients.FirstOrDefault(p => p.Id == (Guid)args![0]!)))),
                Stub<IRepository<Payer>>.With(("GetByIdAsync", args =>
                    Task.FromResult(payers.FirstOrDefault(p => p.Id == (Guid)args![0]!)))),
                Stub<IRepository<LabSite>>.With(("GetByIdAsync", _ => Task.FromResult<LabSite?>(site))),
                Stub<IRepository<AppUser>>.Create(),
                Stub<IRepository<CashSession>>.Create(),
                Stub<IRepository<Deposit>>.With(
                    ("ListAsync", _ => Task.FromResult<IReadOnlyList<Deposit>>([]))),
                Stub<IBillingQueries>.Create(),
                Stub<ISequenceAllocator>.With(
                    ("NextAsync", _ => Task.FromResult(Result.Success($"FAC/CASA/2026/{++_sequence:000000}")))),
                Stub<IUnitOfWork>.Create(),
                Stub<IPermissionGuard>.Create(),
                Stub<ICurrentUser>.With(
                    ("get_UserId", _ => user),
                    ("get_SiteId", _ => siteId)),
                Clock());
        }

        public BillingService Service { get; }

        public List<Invoice> Invoices { get; } = [];
    }

    private sealed class Stub<T> : ReviewFixesBilling2Tests.Stub<T> where T : class;

    /// <summary>
    /// A database of its own, <c>MarocLIS_FixCycle2Billing</c>, created from the model and dropped at
    /// the end. An existing database of that name is refused rather than reused.
    /// </summary>
    private sealed class ScratchDatabase : IDisposable
    {
        private const string Name = "MarocLIS_FixCycle2Billing";
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
