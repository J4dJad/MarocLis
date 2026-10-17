using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// The payer split at invoicing, the till summary, refunds, collections, claim batches and lot
/// valuation, as corrected after the October 2026 review (F5 to F11).
/// </summary>
public sealed class ReviewFixesBilling2Tests
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateTimeOffset RegisteredAt = new(2026, 9, 20, 9, 0, 0, TimeSpan.FromHours(1));

    // ---- F5: the split frozen at registration -----------------------------------------------------

    private static Payer NewPayer(string code, decimal defaultRate) =>
        new(code, new MultilingualText(code), PayerKind.AmoCnss) { DefaultReimbursementRate = defaultRate };

    private static Patient NewPatient() =>
        new("MRN-0001", "ALAOUI", "Fatima", new DateOnly(1980, 1, 1), Gender.Female, Guid.NewGuid());

    private static LabOrder OrderWith(Payer? payer, decimal? frozenRate) =>
        new("2026-000123", Guid.NewGuid(), Guid.NewGuid(), RegisteredAt, Guid.NewGuid())
        {
            PayerId = payer?.Id,
            ReimbursementRateAtRegistration = frozenRate
        };

    [Fact]
    public void ACoverageThatLapsedBeforeInvoicing_KeepsTheRateFrozenAtRegistration()
    {
        var cnss = NewPayer("CNSS", 0.70m);
        var patient = NewPatient();
        patient.AddCoverage(new PatientCoverage(patient.Id, cnss.Id, "123456", new DateOnly(2025, 1, 1))
        {
            ValidUntil = new DateOnly(2026, 9, 30)
        });

        // Month-end invoicing on 15 October: the coverage expired on 30 September.
        patient.PrimaryCoverage(Today).Should().BeNull();

        BillingService.FrozenReimbursementRate(OrderWith(cnss, 0.80m), patient, cnss)
            .Should().Be(0.80m);
    }

    [Fact]
    public void ASecondaryMutuelle_IsNotPricedAtThePrimaryRate()
    {
        var cnss = NewPayer("CNSS", 0.70m);
        var mutuelle = NewPayer("MUT", 0.90m);
        var patient = NewPatient();
        patient.AddCoverage(new PatientCoverage(patient.Id, mutuelle.Id, "M-1", new DateOnly(2025, 1, 1))
        {
            IsPrimary = false,
            ReimbursementRateOverride = 0.50m
        });
        patient.AddCoverage(new PatientCoverage(patient.Id, cnss.Id, "C-1", new DateOnly(2025, 1, 1)));

        BillingService.FrozenReimbursementRate(OrderWith(mutuelle, 0.50m), patient, mutuelle)
            .Should().Be(0.50m);
    }

    [Fact]
    public void AnOrderRegisteredUncovered_GivesThePayerNothing()
    {
        var cnss = NewPayer("CNSS", 0.70m);
        var patient = NewPatient();
        patient.AddCoverage(new PatientCoverage(patient.Id, cnss.Id, "C-1", new DateOnly(2025, 1, 1)));

        BillingService.FrozenReimbursementRate(OrderWith(cnss, null), patient, cnss).Should().Be(0m);
        BillingService.FrozenReimbursementRate(OrderWith(cnss, 0.80m), patient, null).Should().Be(0m);
    }

    [Fact]
    public void AnOverriddenPayer_UsesThatPayersCoverageAsOfRegistration()
    {
        var cnss = NewPayer("CNSS", 0.70m);
        var cnops = NewPayer("CNOPS", 0.80m);
        var patient = NewPatient();
        patient.AddCoverage(new PatientCoverage(patient.Id, cnss.Id, "C-1", new DateOnly(2025, 1, 1)));
        patient.AddCoverage(new PatientCoverage(patient.Id, cnops.Id, "P-1", new DateOnly(2025, 1, 1))
        {
            IsPrimary = false,
            ValidUntil = new DateOnly(2026, 9, 30)
        });

        // Registered with CNSS, invoiced to CNOPS: CNOPS covered the patient on 20 September.
        BillingService.FrozenReimbursementRate(OrderWith(cnss, 0.70m), patient, cnops)
            .Should().Be(0.80m);

        // No coverage at all with the override payer: nothing for it to pay.
        var other = NewPayer("FAR", 0.80m);
        BillingService.FrozenReimbursementRate(OrderWith(cnss, 0.70m), patient, other)
            .Should().Be(0m);
    }

    // ---- F6: the till summary agrees with the domain ---------------------------------------------

    [Fact]
    public void TheTillSummary_UsesTheSessionCounters_RefundsOnceAndDepositsIncluded()
    {
        var session = new CashSession(Guid.NewGuid(), Guid.NewGuid(), Now, 200m);

        session.Accumulate(PaymentMethod.Cash, 300m);   // a counter payment, later refunded
        session.RecordCashRefund(300m);
        session.Accumulate(PaymentMethod.Cash, 150m);   // a cash deposit (acompte)
        session.Accumulate(PaymentMethod.Card, 80m);

        var summary = CashSessionSummary.From(session, "Caissière", 3);

        summary.CashReceipts.Should().Be(450m);
        summary.CashRefunds.Should().Be(300m);
        summary.CardReceipts.Should().Be(80m);
        // Float + 300 + 150 − 300: the refund comes off once, the deposit is in the drawer.
        summary.ExpectedClosingCash.Should().Be(350m);
        summary.ExpectedClosingCash.Should().Be(session.ExpectedClosingCash);

        // Counting exactly what the screen pre-fills records no variance.
        session.Close(Now.AddHours(8), Guid.NewGuid(), summary.ExpectedClosingCash, null)
            .IsSuccess.Should().BeTrue();
        session.Variance.Should().Be(0m);
    }

    // ---- F7: a refused refund leaves the payment untouched ----------------------------------------

    [Fact]
    public async Task ACashRefundWithoutAnOpenTill_LeavesThePaymentSettled()
    {
        var invoice = IssuedInvoice(500m);
        var payment = new Payment(200m, PaymentMethod.Cash, Today, Guid.NewGuid(), invoice.SiteId)
        {
            CashSessionId = Guid.NewGuid()
        };
        invoice.RecordPayment(payment).IsSuccess.Should().BeTrue();

        var harness = new Harness(invoice);
        // No open session for the operator: the stub answers null.

        var result = await harness.Service.RefundPaymentAsync(invoice.Id, payment.Id, "Erreur de saisie");

        result.FirstError.ResourceKey.Should().Be("Payment_CashRefundRequiresOpenSession");
        payment.Status.Should().Be(PaymentStatus.Settled);
        payment.RefundedAt.Should().BeNull();
        invoice.PaidAmount.Should().Be(200m);
        harness.Saves.Should().Be(0);
    }

    [Fact]
    public async Task ACashRefundWithAnOpenTill_IsRecordedInTheTill()
    {
        var invoice = IssuedInvoice(500m);
        var payment = new Payment(200m, PaymentMethod.Cash, Today, Guid.NewGuid(), invoice.SiteId)
        {
            CashSessionId = Guid.NewGuid()
        };
        invoice.RecordPayment(payment).IsSuccess.Should().BeTrue();

        // A drawer that holds the 200: one that does not refuses the refund (owner's decision, cycle 3).
        var session = new CashSession(invoice.SiteId, Guid.NewGuid(), Now, 300m);
        var harness = new Harness(invoice) { OpenSession = session };

        var result = await harness.Service.RefundPaymentAsync(invoice.Id, payment.Id, "Erreur de saisie");

        result.IsSuccess.Should().BeTrue();
        payment.Status.Should().Be(PaymentStatus.Refunded);
        session.CashRefunds.Should().Be(200m);
        invoice.PaidAmount.Should().Be(0m);
    }

    // ---- F8 and F9: what counts as collected, and what the payer paid ----------------------------

    [Fact]
    public void AWriteOff_IsNotMoneyCollected()
    {
        var collected = Payment.IsMoneyCollected.Compile();

        collected(new Payment(100m, PaymentMethod.Cash, Today, Guid.NewGuid(), Guid.NewGuid()))
            .Should().BeTrue();
        collected(new Payment(100m, PaymentMethod.ThirdPartyPayer, Today, Guid.NewGuid(), Guid.NewGuid()))
            .Should().BeTrue();
        collected(new Payment(100m, PaymentMethod.WriteOff, Today, Guid.NewGuid(), Guid.NewGuid()))
            .Should().BeFalse();
    }

    [Fact]
    public void OnlyThePayersOwnSettlement_CountsAsSettledOnAClaimBatch()
    {
        var payerPaid = Payment.IsPayerSettlement.Compile();

        payerPaid(new Payment(100m, PaymentMethod.ThirdPartyPayer, Today, Guid.NewGuid(), Guid.NewGuid()))
            .Should().BeTrue();
        payerPaid(new Payment(100m, PaymentMethod.Cash, Today, Guid.NewGuid(), Guid.NewGuid()))
            .Should().BeFalse();
        payerPaid(new Payment(100m, PaymentMethod.WriteOff, Today, Guid.NewGuid(), Guid.NewGuid()))
            .Should().BeFalse();
    }

    [Fact]
    public void ThePaymentPredicates_TranslateToSql()
    {
        var options = new DbContextOptionsBuilder<LisDbContext>()
            .UseSqlServer("Server=.;Database=MarocLIS;Integrated Security=true")
            .Options;
        using var db = new LisDbContext(options);

        // The statistics shape and the claim-batch shape, a nested sum in a projection.
        db.Payments.Where(Payment.IsMoneyCollected).ToQueryString().Should().Contain("[Method]");

        db.Invoices
            .Select(i => new
            {
                i.Id,
                PayerPaid = db.Payments.Where(p => p.InvoiceId == i.Id)
                                       .Where(Payment.IsPayerSettlement)
                                       .Sum(p => (decimal?)p.Amount) ?? 0m
            })
            .ToQueryString().Should().Contain("SUM");
    }

    // ---- F10: a rejected claim can be resubmitted and then paid -----------------------------------

    [Fact]
    public async Task ARejectedClaim_Resubmitted_ReversesTheWriteOffAndAcceptsThePayersPayment()
    {
        var invoice = IssuedInvoice(400m, payerRate: 0.75m);
        invoice.PayerShareAmount.Should().Be(300m);

        // The patient paid their share at the counter.
        invoice.RecordPayment(new Payment(100m, PaymentMethod.Card, Today, Guid.NewGuid(), invoice.SiteId))
            .IsSuccess.Should().BeTrue();

        invoice.ClaimStatus = ClaimStatus.Submitted;
        var harness = new Harness(invoice);

        (await harness.Service.RecordPayerSettlementAsync(
                invoice.Id, 0m, PayerShortfallHandling.WriteOff, "Prescription illisible", null))
            .IsSuccess.Should().BeTrue();

        invoice.ClaimStatus.Should().Be(ClaimStatus.Rejected);
        invoice.OutstandingAmount.Should().Be(0m);

        (await harness.Service.SubmitClaimBatchAsync([invoice.Id])).IsSuccess.Should().BeTrue();

        invoice.ClaimStatus.Should().Be(ClaimStatus.Submitted);
        invoice.PayerAcceptedAmount.Should().BeNull();
        invoice.Payments.Single(p => p.Method == PaymentMethod.WriteOff).Status
            .Should().Be(PaymentStatus.Cancelled);
        invoice.OutstandingAmount.Should().Be(300m);

        (await harness.Service.RecordPayerSettlementAsync(
                invoice.Id, 300m, PayerShortfallHandling.PatientPays, null, "VIR-2026-77"))
            .IsSuccess.Should().BeTrue();

        invoice.ClaimStatus.Should().Be(ClaimStatus.Settled);
        invoice.OutstandingAmount.Should().Be(0m);
        invoice.Status.Should().Be(InvoiceStatus.Paid);
    }

    [Fact]
    public void OnlyARejectedClaim_CanBeReopened()
    {
        var invoice = IssuedInvoice(100m, payerRate: 0.5m);
        invoice.ClaimStatus = ClaimStatus.PartiallyAccepted;

        invoice.ReopenRejectedClaim("renvoi").FirstError.ResourceKey.Should().Be("Claim_AlreadySubmitted");
    }

    // ---- F11: received lots valued net of the line discount ---------------------------------------

    [Fact]
    public void APurchaseOrderLine_NetUnitPrice_TakesTheDiscountOff()
    {
        var line = new PurchaseOrderLine(Guid.NewGuid(), 10m, 123.45m) { DiscountRate = 0.15m };

        // 123.45 × 0.85 = 104.9325
        line.NetUnitPriceMad.Should().Be(104.93m);
        new PurchaseOrderLine(Guid.NewGuid(), 1m, 50m).NetUnitPriceMad.Should().Be(50m);
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static Invoice IssuedInvoice(decimal price, decimal payerRate = 0m)
    {
        var invoice = new Invoice(string.Empty, Guid.NewGuid(), InvoiceKind.Invoice, Today, Guid.NewGuid())
        {
            BilledToName = "ALAOUI Fatima",
            PayerId = Guid.NewGuid()
        };

        var line = new InvoiceLine("Analyse", price);
        invoice.AddLine(line).IsSuccess.Should().BeTrue();
        if (payerRate > 0m) line.SetCoverageSplit(payerRate, payerUsesReferenceTariff: false);

        invoice.Issue("FAC/CASA/2026/000200", Now, null).IsSuccess.Should().BeTrue();
        return invoice;
    }

    /// <summary>A billing service over one invoice, with every other dependency stubbed.</summary>
    private sealed class Harness
    {
        public Harness(Invoice invoice)
        {
            var user = Guid.NewGuid();

            Service = new BillingService(
                Stub<IRepository<Invoice>>.With(
                    ("GetByIdWithDetailsAsync", _ => Task.FromResult<Invoice?>(invoice)),
                    ("GetByIdAsync", _ => Task.FromResult<Invoice?>(invoice))),
                Stub<IRepository<LabOrder>>.Create(),
                Stub<IRepository<Patient>>.Create(),
                Stub<IRepository<Payer>>.Create(),
                Stub<IRepository<LabSite>>.Create(),
                Stub<IRepository<AppUser>>.Create(),
                Stub<IRepository<CashSession>>.Create(),
                Stub<IRepository<Deposit>>.Create(),
                Stub<IBillingQueries>.With(
                    ("GetOpenCashSessionAsync", _ => Task.FromResult(OpenSession))),
                Stub<ISequenceAllocator>.With(
                    ("NextAsync", _ => Task.FromResult(Result.Success("TP/CASA/2026/000007")))),
                Stub<IUnitOfWork>.With(
                    ("SaveChangesAsync", _ => { Saves++; return Task.FromResult(1); })),
                Stub<IPermissionGuard>.Create(),
                Stub<ICurrentUser>.With(
                    ("get_UserId", _ => user),
                    ("get_SiteId", _ => invoice.SiteId)),
                Stub<IClock>.With(
                    ("get_Now", _ => Now),
                    ("get_Today", _ => Today)));
        }

        public BillingService Service { get; }

        public CashSession? OpenSession { get; init; }

        public int Saves { get; private set; }
    }

    /// <summary>
    /// An interface implementation answering named members from the given handlers and everything
    /// else with a neutral value: a completed task, a successful result, or the default.
    /// </summary>
    public class Stub<T> : DispatchProxy where T : class
    {
        private Dictionary<string, Func<object?[]?, object?>> _handlers = [];

        public static T Create() => With();

        public static T With(params (string Member, Func<object?[]?, object?> Handler)[] handlers)
        {
            var proxy = Create<T, Stub<T>>();
            ((Stub<T>)(object)proxy)._handlers = handlers.ToDictionary(h => h.Member, h => h.Handler);
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;

            if (_handlers.TryGetValue(targetMethod.Name, out var handler)) return handler(args);

            var type = targetMethod.ReturnType;

            if (type == typeof(void)) return null;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type == typeof(Result)) return Result.Success();

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                                   .MakeGenericMethod(inner)
                                   .Invoke(null, [value]);
            }

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
