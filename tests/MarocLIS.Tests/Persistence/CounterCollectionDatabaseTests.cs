using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Orders;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// « Encaissement » at the counter, on SQL Server with the services as the workstation composes them:
/// a request of 747 DH (400 + 347) less the clinic's 10 % convention, 672,30 DH for a self-paying
/// patient. « Tout payer » issues the invoice through the invoicing service and settles it in the
/// till; « Acompte » is a deposit, applied when the invoice is issued; a refusal leaves nothing
/// issued; « À encaisser » lists today's files of the site still owing; the method preselected is
/// the user's last one; whoever does not take payments sees none of it and still registers.
/// </summary>
public sealed class CounterCollectionDatabaseTests(OrderEntryScratchDatabase database)
    : IClassFixture<OrderEntryScratchDatabase>
{
    private sealed class Operator(Guid siteId, IReadOnlySet<string> permissions, Guid userId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = userId;
        public string UserName => "caisse";
        public string FullName => "Caisse";
        public Guid SiteId => siteId;
        public string SiteCode { get; } = "S" + ((uint)siteId.GetHashCode()).ToString("X8");
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.None;
        public IReadOnlyList<Guid> AccessibleSiteIds => [siteId];
        public IReadOnlySet<string> Permissions { get; } = permissions;
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "ACCUEIL-1";
    }

    /// <summary>Reception that registers but takes no money.</summary>
    private static readonly HashSet<string> Reception =
    [
        Permissions.Patients.View, Permissions.Orders.View, Permissions.Orders.Create, Permissions.Billing.View
    ];

    /// <summary>The counter as the seeded « Secrétariat / accueil » role: registers, invoices, takes payments.</summary>
    private static readonly HashSet<string> Counter =
        [.. Reception, Permissions.Billing.CreateInvoice, Permissions.Billing.RecordPayment, Permissions.Billing.CloseCashSession];

    /// <summary>Takes payments without issuing invoices.</summary>
    private static readonly HashSet<string> CashOnly = [.. Reception, Permissions.Billing.RecordPayment];

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string Keys(IEnumerable<Error> errors) => string.Join(", ", errors.Select(e => e.ResourceKey));

    private ServiceProvider Services(Guid siteId, IReadOnlySet<string> permissions, Guid? userId = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(siteId, permissions, userId ?? Guid.NewGuid()));
        services.AddIsolatedQueries<IBillingQueries, MarocLIS.Infrastructure.Queries.BillingQueries>();
        services.AddIsolatedQueries<IOrderQueries, MarocLIS.Infrastructure.Queries.OrderQueries>();
        services.AddIsolatedQueries<IInvoicePrintSettingsQueries, MarocLIS.Infrastructure.Queries.InvoicePrintSettingsQueries>();
        services.AddIsolatedQueries<IDiscountQueries, MarocLIS.Infrastructure.Queries.DiscountQueries>();
        services.AddIsolatedQueries<ICounterCollectionQueries, MarocLIS.Infrastructure.Queries.CounterCollectionQueries>();
        services.AddIsolatedQueries<IOrderQuotes, OrderQuoteService>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> On<TService, T>(ServiceProvider provider, Func<TService, Task<T>> action)
        where TService : notnull
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static Task<T> Counting<T>(ServiceProvider provider, Func<CounterCollectionService, Task<T>> action) =>
        On(provider, action);

    private sealed record Lab(Guid SiteId, Guid PatientId, Guid InsuredId, Guid BilanId, Guid HormonesId, Guid PrescriberId);

    /// <summary>
    /// A site that can invoice, a self-paying patient and an insured one (AMO with third-party
    /// payment), two acts (400 + 347 = 747 DH), a prescriber whose clinic convention gives 10 %.
    /// </summary>
    private async Task<Lab> LabAsync()
    {
        var validFrom = DateOnly.FromDateTime(DateTime.Today).AddYears(-1);

        var site = new LabSite(Unique("S"), new MultilingualText("Principal"), LabSiteKind.MainLaboratory)
        {
            LegalName = "Laboratoire Test SARL",
            Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
            FiscalId = "40112233",
            AddressLine = new MultilingualText("12 rue Test"),
            City = "Casablanca"
        };

        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sang"));
        var tube = new ContainerType(Unique("SEC"), new MultilingualText("Tube sec"), "#C8A000");

        AnalysisTest Analysis(string code, decimal price)
        {
            var analysis = new AnalysisTest(Unique(code), new MultilingualText(code), department.Id, specimen.Id)
            {
                ContainerTypeId = tube.Id,
                RoutineTurnaroundHours = 4
            };
            var parameter = new TestParameter(Unique(code), new MultilingualText(code), ResultValueType.Numeric);
            parameter.AddReferenceRange(ReferenceRange.Numeric(1m, 100m));
            analysis.AddParameter(parameter);
            analysis.AddTariff(new AnalysisTestTariff(analysis.Id, payerId: null, validFrom) { Price = price, IsVatExempt = true });
            return analysis;
        }

        var bilan = Analysis("BILAN", 400m);
        var hormones = Analysis("HORM", 347m);
        var patient = new Patient(Unique("P"), "ALAOUI", "Salma", new DateOnly(1979, 6, 3), Gender.Female, site.Id);

        var amo = new Payer(Unique("AMO"), new MultilingualText("AMO — CNOPS"), PayerKind.AmoCnops)
        {
            DefaultReimbursementRate = 0.80m,
            // Priced on the list price, not on the national letter-key: 80 % of what is charged.
            UsesNationalReferenceTariff = false,
            ThirdPartyPaymentAgreement = true,
            RequiresPrescription = false,
            RequiresInpeOnClaim = false
        };
        var insured = new Patient(Unique("P"), "SQUALLI", "Hamza", new DateOnly(1986, 2, 1), Gender.Male, site.Id);
        insured.AddCoverage(new PatientCoverage(insured.Id, amo.Id, "123456789", validFrom));

        var prescriber = new Prescriber("TAZI", "Omar", PrescriberKind.GeneralPractitioner);
        var convention = new MarocLIS.Domain.Relations.Partnership(site.Id, "Clinique Al Amal " + Unique(""),
            MarocLIS.Domain.Relations.PartnerKind.Clinic, MarocLIS.Domain.Relations.PartnershipKind.CareConvention,
            DateOnly.FromDateTime(DateTime.Today).AddMonths(-3))
        {
            PrescriberId = prescriber.Id,
            DiscountPercent = 10m
        };

        await using var context = database.Context();
        context.Add(site);
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.ContainerTypes.Add(tube);
        context.AnalysisTests.AddRange(bilan, hormones);
        context.Payers.Add(amo);
        context.Patients.AddRange(patient, insured);
        context.Prescribers.Add(prescriber);
        context.Partnerships.Add(convention);
        await context.SaveChangesAsync();

        return new Lab(site.Id, patient.Id, insured.Id, bilan.Id, hormones.Id, prescriber.Id);
    }

    private static async Task<RegisteredOrder> RegisterAsync(ServiceProvider provider, Lab lab, Guid? patientId = null)
    {
        var registered = await On<OrderRegistrationService, Result<RegisteredOrder>>(provider, s => s.RegisterAsync(new RegisterOrderRequest
        {
            PatientId = patientId ?? lab.PatientId,
            AnalysisTestIds = [lab.BilanId, lab.HormonesId],
            PrescriberId = lab.PrescriberId
        }));
        registered.IsSuccess.Should().BeTrue(Keys(registered.Errors));
        return registered.Value;
    }

    private static async Task OpenTillAsync(ServiceProvider provider, decimal openingFloat = 200m)
    {
        var opened = await On<BillingService, Result<Guid>>(provider, s => s.OpenCashSessionAsync(openingFloat));
        opened.IsSuccess.Should().BeTrue(Keys(opened.Errors));
    }

    private async Task<LabOrder> ReadOrderAsync(Guid orderId)
    {
        await using var context = database.Context();
        return await context.LabOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
    }

    private async Task<Invoice> ReadInvoiceAsync(Guid invoiceId)
    {
        await using var context = database.Context();
        return await context.Invoices.AsNoTracking().Include(i => i.Payments).SingleAsync(i => i.Id == invoiceId);
    }

    private async Task<CashSession> ReadTillAsync(Guid userId)
    {
        await using var context = database.Context();
        return await context.CashSessions.AsNoTracking()
            .SingleAsync(s => s.OperatorUserId == userId && s.Status == CashSessionStatus.Open);
    }

    private async Task<List<string>> ReasonsAsync(string entityName, Guid entityId)
    {
        await using var context = database.Context();
        return await context.AuditEntries.AsNoTracking()
            .Where(a => a.EntityName == entityName && a.EntityId == entityId && a.Reason != null)
            .Select(a => a.Reason!)
            .ToListAsync();
    }

    // ---- 1. « Tout payer » at the end of the registration --------------------------------------------

    [SqlServerFact]
    public async Task PayingEverything_IssuesTheInvoice_SettlesItInTheTill_AndPrintsItsReceipt()
    {
        var lab = await LabAsync();
        var cashier = Guid.NewGuid();
        using var counter = Services(lab.SiteId, Counter, cashier);

        var registered = await RegisterAsync(counter, lab);
        registered.Pricing.DueAtCounter.Should().Be(Money.Of(672.30m), "747 DH less the clinic's 10 %");

        var balance = await Counting(counter, s => s.GetBalanceAsync(registered.OrderId));
        balance.IsSuccess.Should().BeTrue(Keys(balance.Errors));
        balance.Value.Due.Should().Be(672.30m, "the panel shows the share the registration computed");
        balance.Value.Discount.Should().Be(74.70m);
        balance.Value.IsInvoiced.Should().BeFalse();
        balance.Value.Payment.State.Should().Be(OrderPaymentState.NotInvoiced);

        // The till checked before anything is issued: cash with no till is refused, no invoice burned.
        (await Counting(counter, s => s.GetTillStateAsync())).Value.Should().Be(TillState.NotOpen);
        var refused = await Counting(counter, s => s.CollectAsync(
            new CollectRequest(registered.OrderId, CollectionKind.Full, 672.30m, PaymentMethod.Cash)));
        Keys(refused.Errors).Should().Be("Payment_CashRequiresOpenSession");
        (await ReadOrderAsync(registered.OrderId)).InvoiceId.Should().BeNull("a refusal leaves nothing issued");

        await OpenTillAsync(counter);
        (await Counting(counter, s => s.GetTillStateAsync())).Value.Should().Be(TillState.Open);

        var done = await Counting(counter, s => s.CollectAsync(
            new CollectRequest(registered.OrderId, CollectionKind.Full, 672.30m, PaymentMethod.Cash)));
        done.IsSuccess.Should().BeTrue(Keys(done.Errors));
        done.Value.InvoiceIssued.Should().BeTrue();
        done.Value.InvoiceNumber.Should().NotBeNullOrEmpty();
        done.Value.Amount.Should().Be(672.30m);
        done.Value.RemainingDue.Should().Be(0m);

        var order = await ReadOrderAsync(registered.OrderId);
        order.InvoiceId.Should().Be(done.Value.InvoiceId);
        var invoice = await ReadInvoiceAsync(order.InvoiceId!.Value);
        invoice.Status.Should().Be(InvoiceStatus.Paid);
        invoice.TotalAmount.Should().Be(672.30m);
        invoice.Payments.Should().ContainSingle().Which.Notes.Should().Be(CounterCollectionService.CounterNote);

        var till = await ReadTillAsync(cashier);
        till.ExpectedCashReceipts.Should().Be(672.30m, "the money went into the operator's till");

        var reasons = await ReasonsAsync(nameof(Invoice), invoice.Id);
        reasons.Should().Contain(r => r.StartsWith("Émission de la facture"));
        reasons.Should().Contain(r => r.StartsWith("Règlement de") && r.Contains(invoice.Number));

        var settled = await Counting(counter, s => s.GetBalanceAsync(registered.OrderId));
        settled.Value.Payment.State.Should().Be(OrderPaymentState.Paid);
        settled.Value.Due.Should().Be(0m);

        var receipt = await Counting(counter, s => s.GetReceiptAsync(done.Value));
        receipt.IsSuccess.Should().BeTrue(Keys(receipt.Errors));
        receipt.Value.Number.Should().Be(invoice.Number);
        receipt.Value.Kind.Should().Be(InvoiceKind.Invoice);
        receipt.Value.OutstandingAmount.Should().Be(0m);

        var again = await Counting(counter, s => s.CollectAsync(
            new CollectRequest(registered.OrderId, CollectionKind.Full, 672.30m, PaymentMethod.Cash)));
        Keys(again.Errors).Should().Be("Collect_NothingDue");
    }

    [SqlServerFact]
    public async Task ADeposit_IsHeldOnTheFile_ItsReceiptPrinted_ThenAppliedWhenTheRestIsPaid()
    {
        var lab = await LabAsync();
        var cashier = Guid.NewGuid();
        using var counter = Services(lab.SiteId, Counter, cashier);
        await OpenTillAsync(counter);

        var registered = await RegisterAsync(counter, lab);

        var deposit = await Counting(counter, s => s.CollectAsync(
            new CollectRequest(registered.OrderId, CollectionKind.Deposit, 200m, PaymentMethod.Cash)));
        deposit.IsSuccess.Should().BeTrue(Keys(deposit.Errors));
        deposit.Value.DepositNumber.Should().NotBeNullOrEmpty();
        deposit.Value.InvoiceId.Should().BeNull("an advance does not issue the invoice");
        deposit.Value.RemainingDue.Should().Be(472.30m);

        var receipt = await Counting(counter, s => s.GetReceiptAsync(deposit.Value));
        receipt.IsSuccess.Should().BeTrue(Keys(receipt.Errors));
        receipt.Value.Kind.Should().Be(InvoiceKind.Receipt);
        receipt.Value.Number.Should().Be(deposit.Value.DepositNumber);
        receipt.Value.Payments.Should().ContainSingle().Which.Amount.Should().Be(200m);
        receipt.Value.OutstandingAmount.Should().Be(472.30m);
        receipt.Value.Issuer.Ice.Should().NotBeNullOrEmpty();

        var queue = await Counting(counter, s => s.GetQueueAsync());
        queue.Value.Should().ContainSingle(r => r.LabOrderId == registered.OrderId)
             .Which.Should().Match<CollectionQueueRow>(r => r.Due == 472.30m && r.AlreadyPaid == 200m
                                                            && r.State == OrderPaymentState.NotInvoiced);

        // Too much refused, and « Tout payer » on a sum the panel no longer shows.
        Keys((await Counting(counter, s => s.CollectAsync(
            new CollectRequest(registered.OrderId, CollectionKind.Deposit, 500m, PaymentMethod.Cash)))).Errors)
            .Should().Be("Deposit_ExceedsOrderTotal");
        Keys((await Counting(counter, s => s.CollectAsync(
            new CollectRequest(registered.OrderId, CollectionKind.Full, 672.30m, PaymentMethod.Card)))).Errors)
            .Should().Be("Collect_DueChanged");

        // A cheque without its number is refused before the invoice is issued.
        Keys((await Counting(counter, s => s.CollectAsync(
            new CollectRequest(registered.OrderId, CollectionKind.Full, 472.30m, PaymentMethod.Cheque)))).Errors)
            .Should().Be("Payment_ReferenceRequired");
        (await ReadOrderAsync(registered.OrderId)).InvoiceId.Should().BeNull();

        var rest = await Counting(counter, s => s.CollectAsync(
            new CollectRequest(registered.OrderId, CollectionKind.Full, 472.30m, PaymentMethod.Card, "4970 **** 1234")));
        rest.IsSuccess.Should().BeTrue(Keys(rest.Errors));
        rest.Value.Amount.Should().Be(472.30m);

        var invoice = await ReadInvoiceAsync(rest.Value.InvoiceId!.Value);
        invoice.Status.Should().Be(InvoiceStatus.Paid);
        invoice.Payments.Should().HaveCount(2, "the deposit applied, then the rest by card");
        invoice.Payments.Should().Contain(p => p.DepositId != null && p.Amount == 200m);
        invoice.Payments.Should().Contain(p => p.Method == PaymentMethod.Card && p.Amount == 472.30m);

        var till = await ReadTillAsync(cashier);
        till.ExpectedCashReceipts.Should().Be(200m);
        till.CardReceipts.Should().Be(472.30m);

        (await Counting(counter, s => s.GetQueueAsync())).Value.Should().NotContain(r => r.LabOrderId == registered.OrderId);
        (await Counting(counter, s => s.GetPreferredMethodAsync())).Should().Be(PaymentMethod.Card, "the user's last method");
    }

    [SqlServerFact]
    public async Task OnAnInvoicedFile_APartPayment_ThenTheRest_SettleTheInvoice()
    {
        var lab = await LabAsync();
        using var counter = Services(lab.SiteId, Counter);
        await OpenTillAsync(counter);

        var registered = await RegisterAsync(counter, lab);
        var issued = await On<BillingService, Result<Guid>>(counter, s => s.IssueInvoiceAsync([registered.OrderId]));
        issued.IsSuccess.Should().BeTrue(Keys(issued.Errors));

        var part = await Counting(counter, s => s.CollectAsync(
            new CollectRequest(registered.OrderId, CollectionKind.Deposit, 72.30m, PaymentMethod.Cash)));
        part.IsSuccess.Should().BeTrue(Keys(part.Errors));
        part.Value.InvoiceId.Should().Be(issued.Value);
        part.Value.InvoiceIssued.Should().BeFalse();
        part.Value.RemainingDue.Should().Be(600m);
        (await ReadInvoiceAsync(issued.Value)).Status.Should().Be(InvoiceStatus.PartiallyPaid);

        var queue = await Counting(counter, s => s.GetQueueAsync());
        queue.Value.Should().ContainSingle(r => r.LabOrderId == registered.OrderId)
             .Which.State.Should().Be(OrderPaymentState.PatientShareDue);

        var rest = await Counting(counter, s => s.CollectAsync(
            new CollectRequest(registered.OrderId, CollectionKind.Full, 600m, PaymentMethod.Cash)));
        rest.IsSuccess.Should().BeTrue(Keys(rest.Errors));
        (await ReadInvoiceAsync(issued.Value)).Status.Should().Be(InvoiceStatus.Paid);
    }

    [SqlServerFact]
    public async Task UnderThirdPartyPayment_OnlyThePatientsShare_IsTaken()
    {
        var lab = await LabAsync();
        using var counter = Services(lab.SiteId, Counter);
        await OpenTillAsync(counter);

        var registered = await RegisterAsync(counter, lab, lab.InsuredId);
        var share = registered.Pricing.DueAtCounter.Amount;
        share.Should().BeLessThan(registered.Pricing.Total.Amount, "the payer covers part of it");

        var balance = await Counting(counter, s => s.GetBalanceAsync(registered.OrderId));
        balance.Value.Due.Should().Be(share);
        balance.Value.PayerShare.Should().Be(registered.Pricing.Total.Amount - share);

        var done = await Counting(counter, s => s.CollectAsync(
            new CollectRequest(registered.OrderId, CollectionKind.Full, share, PaymentMethod.Cash)));
        done.IsSuccess.Should().BeTrue(Keys(done.Errors));

        var invoice = await ReadInvoiceAsync(done.Value.InvoiceId!.Value);
        invoice.PaidAmount.Should().Be(share);
        invoice.ClaimStatus.Should().Be(ClaimStatus.Prepared, "the payer's share is claimed, not asked of the patient");
        (await Counting(counter, s => s.GetBalanceAsync(registered.OrderId))).Value.Payment.State
            .Should().Be(OrderPaymentState.Paid);
    }

    // ---- 2. Rights -----------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task WhoTakesPaymentsButDoesNotInvoice_TakesEverythingAsADeposit()
    {
        var lab = await LabAsync();
        using var cash = Services(lab.SiteId, CashOnly);
        await OpenTillAsync(cash);

        var registered = await RegisterAsync(cash, lab);
        var done = await Counting(cash, s => s.CollectAsync(
            new CollectRequest(registered.OrderId, CollectionKind.Full, 672.30m, PaymentMethod.Cash)));
        done.IsSuccess.Should().BeTrue(Keys(done.Errors));
        done.Value.InvoiceId.Should().BeNull();
        done.Value.DepositNumber.Should().NotBeNullOrEmpty();
        done.Value.RemainingDue.Should().Be(0m);

        (await ReadOrderAsync(registered.OrderId)).InvoiceId.Should().BeNull();
        (await Counting(cash, s => s.GetQueueAsync())).Value.Should().NotContain(r => r.LabOrderId == registered.OrderId);
    }

    [SqlServerFact]
    public async Task WithoutThePaymentRight_NothingIsShownOrTaken_AndTheRegistrationStands()
    {
        var lab = await LabAsync();
        using var reception = Services(lab.SiteId, Reception);

        var registered = await RegisterAsync(reception, lab);

        Keys((await Counting(reception, s => s.GetBalanceAsync(registered.OrderId))).Errors).Should().Be("Message.PermissionDenied");
        Keys((await Counting(reception, s => s.GetQueueAsync())).Errors).Should().Be("Message.PermissionDenied");
        Keys((await Counting(reception, s => s.GetTillStateAsync())).Errors).Should().Be("Message.PermissionDenied");
        Keys((await Counting(reception, s => s.CollectAsync(
            new CollectRequest(registered.OrderId, CollectionKind.Full, 672.30m, PaymentMethod.Card)))).Errors)
            .Should().Be("Message.PermissionDenied");

        (await ReadOrderAsync(registered.OrderId)).InvoiceId.Should().BeNull("« Plus tard »: the billing desk takes it");
    }

    // ---- 3. Site scoping -----------------------------------------------------------------------------

    [SqlServerFact]
    public async Task AnotherSitesFile_IsNeitherListedNorCollected()
    {
        var here = await LabAsync();
        var there = await LabAsync();
        using var counterHere = Services(here.SiteId, Counter);
        using var counterThere = Services(there.SiteId, Counter);

        var theirs = await RegisterAsync(counterThere, there);
        var ours = await RegisterAsync(counterHere, here);

        var queue = await Counting(counterHere, s => s.GetQueueAsync());
        queue.Value.Select(r => r.LabOrderId).Should().Contain(ours.OrderId).And.NotContain(theirs.OrderId);

        Keys((await Counting(counterHere, s => s.GetBalanceAsync(theirs.OrderId))).Errors).Should().Be("Order_NotFound");
        Keys((await Counting(counterHere, s => s.CollectAsync(
            new CollectRequest(theirs.OrderId, CollectionKind.Full, 672.30m, PaymentMethod.Card)))).Errors)
            .Should().Be("Order_NotFound");
    }

    [SqlServerFact]
    public async Task TheQueue_IsTodaysFilesOldestFirst_AndNotACancelledOne()
    {
        var lab = await LabAsync();
        using var counter = Services(lab.SiteId, Counter);

        var first = await RegisterAsync(counter, lab);
        var second = await RegisterAsync(counter, lab);
        var cancelled = await RegisterAsync(counter, lab);

        await using (var context = database.Context())
        {
            var order = await context.LabOrders.Include(o => o.Items).SingleAsync(o => o.Id == cancelled.OrderId);
            order.Cancel(DateTimeOffset.Now, Guid.NewGuid(), "Patient parti sans prélèvement").IsSuccess.Should().BeTrue();
            await context.SaveChangesAsync();
        }

        var ids = (await Counting(counter, s => s.GetQueueAsync())).Value.Select(r => r.LabOrderId).ToList();
        ids.Should().ContainInOrder(first.OrderId, second.OrderId);
        ids.Should().NotContain(cancelled.OrderId);
    }
}
