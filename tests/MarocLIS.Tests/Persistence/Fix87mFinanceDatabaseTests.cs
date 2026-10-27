using System.Reflection;
using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Treasury;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// The counter's money after the total verification (round 87), against SQL Server: only counter
/// methods at the counter, a till never taking another site's cash, the service holding the
/// screens' limits, and a past period's takings that do not move.
/// </summary>
public sealed class Fix87mFinanceDatabaseTests(OrderEntryScratchDatabase database)
    : IClassFixture<OrderEntryScratchDatabase>
{
    private sealed class Operator(IReadOnlyList<Guid> sites, IReadOnlySet<string> permissions) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "biologiste";
        public string FullName => "Biologiste itinérant";
        public Guid SiteId { get; set; } = sites[0];
        public string SiteCode => "S" + ((uint)SiteId.GetHashCode()).ToString("X8");
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.None;
        public IReadOnlyList<Guid> AccessibleSiteIds => sites;
        public IReadOnlySet<string> Permissions { get; } = permissions;
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "POSTE-87";
    }

    private static HashSet<string> Shipped(string roleCode)
    {
        var type = typeof(LisDbContext).Assembly.GetType("MarocLIS.Infrastructure.Persistence.RolePermissionSets")!;
        var definitions = (System.Collections.IEnumerable)type
            .GetMethod("Build", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

        foreach (var definition in definitions)
        {
            var tuple = definition.GetType();
            if ((string)tuple.GetField("Item1")!.GetValue(definition)! == roleCode)
                return [.. (string[])tuple.GetField("Item3")!.GetValue(definition)!];
        }

        throw new InvalidOperationException(roleCode);
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private ServiceProvider Services(ICurrentUser user)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton(user);
        services.AddIsolatedQueries<Application.Results.IResultQueries, ResultQueries>();
        services.AddIsolatedQueries<IBillingQueries, BillingQueries>();
        services.AddIsolatedQueries<IOrderQueries, OrderQueries>();
        services.AddIsolatedQueries<ITreasuryQueries, TreasuryQueries>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> On<TService, T>(ServiceProvider provider, Func<TService, Task<T>> action)
        where TService : notnull
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static string Keys(IEnumerable<Error> errors) => string.Join(", ", errors.Select(e => e.ResourceKey));

    private static void Ok(Result result) => result.IsSuccess.Should().BeTrue(Keys(result.Errors));

    private sealed record Lab(Guid SiteA, Guid SiteB, Patient Patient, AnalysisTest Nfs);

    private static LabSite Site(string name) => new(Unique("S"), new MultilingualText(name), LabSiteKind.MainLaboratory)
    {
        LegalName = "Laboratoire 87 SARL",
        Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture),
        FiscalId = "40112233",
        AddressLine = new MultilingualText("12 rue Test"),
        City = "Casablanca"
    };

    private async Task<Lab> LabAsync()
    {
        var validFrom = DateOnly.FromDateTime(DateTime.Today).AddYears(-1);
        var siteA = Site("Site A");
        var siteB = Site("Site B");

        var department = new Department(Unique("D"), new MultilingualText("Hématologie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sang"));
        var edta = new ContainerType(Unique("EDTA"), new MultilingualText("EDTA"), "#7B3FBF");

        var nfs = new AnalysisTest(Unique("NFS"), new MultilingualText("NFS"), department.Id, specimen.Id)
        {
            ContainerTypeId = edta.Id,
            RoutineTurnaroundHours = 4
        };
        var parameter = new TestParameter(Unique("HB"), new MultilingualText("HB"), ResultValueType.Numeric);
        parameter.AddReferenceRange(ReferenceRange.Numeric(1m, 100m));
        nfs.AddParameter(parameter);
        nfs.AddTariff(new AnalysisTestTariff(nfs.Id, payerId: null, validFrom)
        {
            LetterKey = "B", Coefficient = 100m, LetterKeyValue = 1m, Price = 100m, IsVatExempt = true
        });

        var patient = new Patient(Unique("P"), "TAZI", "Youssef", new DateOnly(1988, 7, 3), Gender.Male, siteA.Id);

        await using var context = database.Context();
        context.Add(siteA);
        context.Add(siteB);
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.ContainerTypes.Add(edta);
        context.AnalysisTests.Add(nfs);
        context.Patients.Add(patient);
        await context.SaveChangesAsync();

        return new Lab(siteA.Id, siteB.Id, patient, nfs);
    }

    private static async Task<Guid> InvoicedRequestAsync(ServiceProvider provider, Lab lab)
    {
        var registered = await On<OrderRegistrationService, Result<RegisteredOrder>>(provider, s => s.RegisterAsync(
            new RegisterOrderRequest
            {
                PatientId = lab.Patient.Id, AnalysisTestIds = [lab.Nfs.Id], TestPanelIds = [],
                PrescriberFreeText = "Dr BENNIS Karim"
            }));
        registered.IsSuccess.Should().BeTrue(Keys(registered.Errors));

        var issued = await On<BillingService, Result<Guid>>(provider, s => s.IssueInvoiceAsync([registered.Value.OrderId]));
        issued.IsSuccess.Should().BeTrue(Keys(issued.Errors));
        return issued.Value;
    }

    private static Task<Result> PayAsync(ServiceProvider provider, Guid invoiceId, decimal amount,
                                         PaymentMethod method = PaymentMethod.Cash, string? reference = null) =>
        On<BillingService, Result>(provider, s => s.RecordPaymentAsync(
            new RecordPaymentRequest(invoiceId, amount, method, reference, null, null, null)));

    private async Task<decimal> OutstandingAsync(Guid invoiceId)
    {
        await using var context = database.Context();
        return (await context.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId)).OutstandingAmount;
    }

    [SqlServerFact]
    public async Task TheCounter_CannotRecordAWriteOffNorAPayersSettlement_AsAPayment()
    {
        var lab = await LabAsync();
        var cashier = new Operator([lab.SiteA], Shipped(SystemRoles.Receptionist));
        using var counter = Services(cashier);

        var invoiceId = await InvoicedRequestAsync(counter, lab);
        Ok(await On<BillingService, Result<Guid>>(counter, s => s.OpenCashSessionAsync(0m)));

        (await PayAsync(counter, invoiceId, 100m, PaymentMethod.WriteOff))
            .FirstError.ResourceKey.Should().Be("Payment_MethodNotAllowed");
        (await PayAsync(counter, invoiceId, 100m, PaymentMethod.ThirdPartyPayer))
            .FirstError.ResourceKey.Should().Be("Payment_MethodNotAllowed");
        (await OutstandingAsync(invoiceId)).Should().Be(100m, "the receivable stays owed");

        Ok(await PayAsync(counter, invoiceId, 100m, PaymentMethod.Card, "TPE-1"));
        (await OutstandingAsync(invoiceId)).Should().Be(0m);
    }

    [SqlServerFact]
    public async Task ATillLeftOpenAtSiteA_NeverTakesSiteBsCash_AndClosesAtSiteA()
    {
        var lab = await LabAsync();
        var biologist = new Operator([lab.SiteA, lab.SiteB], Shipped(SystemRoles.Administrator));
        using var provider = Services(biologist);

        // Morning at site A: the till opens and takes 100 in cash.
        var morning = await InvoicedRequestAsync(provider, lab);
        Ok(await On<BillingService, Result<Guid>>(provider, s => s.OpenCashSessionAsync(50m)));
        Ok(await PayAsync(provider, morning, 100m));

        // Afternoon at site B, the till of A still open.
        biologist.SiteId = lab.SiteB;
        var afternoon = await InvoicedRequestAsync(provider, lab);

        (await PayAsync(provider, afternoon, 100m))
            .FirstError.ResourceKey.Should().Be("CashSession_OpenOnAnotherSite");
        (await On<BillingService, Result<Guid>>(provider, s => s.OpenCashSessionAsync(0m)))
            .FirstError.ResourceKey.Should().Be("CashSession_OpenOnAnotherSite");
        (await On<BillingService, Result<decimal>>(provider, s => s.CloseCashSessionAsync(150m, null)))
            .FirstError.ResourceKey.Should().Be("CashSession_OpenOnAnotherSite");
        (await On<BillingService, Result<CashSessionSummary>>(provider, s => s.GetOpenCashSessionAsync()))
            .IsFailure.Should().BeTrue();

        // A card payment at B is taken, but stays off A's drawer and shift.
        Ok(await PayAsync(provider, afternoon, 100m, PaymentMethod.Card, "TPE-B"));

        await using (var context = database.Context())
        {
            var tillA = await context.CashSessions.AsNoTracking().SingleAsync(s => s.SiteId == lab.SiteA);
            tillA.ExpectedClosingCash.Should().Be(150m);
            tillA.CardReceipts.Should().Be(0m);
            (await context.Payments.AsNoTracking().SingleAsync(p => p.SiteId == lab.SiteB))
                .CashSessionId.Should().BeNull();
        }

        // Back at A, the till closes on its own count.
        biologist.SiteId = lab.SiteA;
        (await On<BillingService, Result<decimal>>(provider, s => s.CloseCashSessionAsync(150m, null))).Value.Should().Be(0m);
    }

    [SqlServerFact]
    public async Task TheService_RefusesANegativeFloatOrCount_AndACreditNoteReasonTooShortOrTooLong()
    {
        var lab = await LabAsync();
        var accountant = new Operator([lab.SiteA], Shipped(SystemRoles.Administrator));
        using var provider = Services(accountant);

        (await On<BillingService, Result<Guid>>(provider, s => s.OpenCashSessionAsync(-500m)))
            .FirstError.ResourceKey.Should().Be("Payment_AmountMustBePositive");
        Ok(await On<BillingService, Result<Guid>>(provider, s => s.OpenCashSessionAsync(0m)));
        (await On<BillingService, Result<decimal>>(provider, s => s.CloseCashSessionAsync(-10m, "Erreur de comptage")))
            .FirstError.ResourceKey.Should().Be("Payment_AmountMustBePositive");

        var invoiceId = await InvoicedRequestAsync(provider, lab);
        (await On<BillingService, Result<Guid>>(provider, s => s.IssueCreditNoteAsync(invoiceId, "x")))
            .FirstError.ResourceKey.Should().Be("Reason_TooShort");
        (await On<BillingService, Result<Guid>>(provider, s => s.IssueCreditNoteAsync(invoiceId, new string('a', 501))))
            .FirstError.ResourceKey.Should().Be("Reason_TooLong");
        Ok(await On<BillingService, Result<Guid>>(provider, s => s.IssueCreditNoteAsync(invoiceId, "Erreur de destinataire")));
    }

    [SqlServerFact]
    public async Task APeriodsTakings_DoNotChange_WhenAChequeBouncesInALaterPeriod()
    {
        var lab = await LabAsync();
        var accountant = new Operator([lab.SiteA], Shipped(SystemRoles.Administrator));
        using var provider = Services(accountant);

        var invoiceId = await InvoicedRequestAsync(provider, lab);
        Ok(await PayAsync(provider, invoiceId, 100m, PaymentMethod.Cheque, "CHQ-87"));

        var today = DateOnly.FromDateTime(DateTime.Today);
        var later = DateTimeOffset.Now.AddDays(8);

        await using (var context = database.Context())
        {
            var cheque = await context.Payments.SingleAsync(p => p.SiteId == lab.SiteA);
            Ok(cheque.MarkBounced(later, "Sans provision"));
            await context.SaveChangesAsync();
        }

        await using (var context = database.Context())
        {
            var statistics = new StatisticsQueries(context);
            (await statistics.GetFinanceAsync(lab.SiteA, today, today)).Collected
                .Should().Be(100m, "the cheque was taken that day");
            var bounceDay = DateOnly.FromDateTime(later.Date);
            (await statistics.GetFinanceAsync(lab.SiteA, bounceDay, bounceDay)).Collected
                .Should().Be(-100m, "the bounce comes off the period it happened in");
            (await statistics.GetFinanceAsync(lab.SiteA, today, bounceDay)).Collected.Should().Be(0m);
        }
    }
}
