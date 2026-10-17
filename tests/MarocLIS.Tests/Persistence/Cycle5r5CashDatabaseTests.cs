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
/// The owner's decision on the front desk's cash (cycle 5, lot r5), against SQL Server with the
/// permission sets the roles really ship with: « Déclarer impayé » stays with the accounts office
/// (Comptabilité, Administrateur), the receptionist is refused; she closes her own till with her
/// count, never a colleague's, and the closed till goes to the accounts office's sign-off.
/// </summary>
public sealed class Cycle5r5CashDatabaseTests(OrderEntryScratchDatabase database)
    : IClassFixture<OrderEntryScratchDatabase>
{
    private sealed class Operator(Guid siteId, IReadOnlySet<string> permissions, string userName) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => userName;
        public string FullName => userName;
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
        public string Workstation => "ACCUEIL-R5";
    }

    private const string Denied = "Message.PermissionDenied";

    /// <summary>The permissions a built-in role ships with, as the seeder grants them.</summary>
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

    private ServiceProvider Services(Guid siteId, string roleCode, string userName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(siteId, Shipped(roleCode), userName));
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

    private sealed record Lab(Guid SiteId, Patient Patient, AnalysisTest Nfs);

    private async Task<Lab> LabAsync()
    {
        var validFrom = DateOnly.FromDateTime(DateTime.Today).AddYears(-1);

        var site = new LabSite(Unique("S"), new MultilingualText("Principal"), LabSiteKind.MainLaboratory)
        {
            LegalName = "Laboratoire R5 SARL",
            Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture),
            FiscalId = "40112233",
            AddressLine = new MultilingualText("12 rue Test"),
            City = "Casablanca"
        };

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

        var patient = new Patient(Unique("P"), "TAZI", "Youssef", new DateOnly(1988, 7, 3), Gender.Male, site.Id);

        await using var context = database.Context();
        context.Add(site);
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.ContainerTypes.Add(edta);
        context.AnalysisTests.Add(nfs);
        context.Patients.Add(patient);
        await context.SaveChangesAsync();

        return new Lab(site.Id, patient, nfs);
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

    [Fact]
    public void TheShippedRoles_KeepTheUnpaidChequeWithTheAccountsOffice_AndLetReceptionCloseItsTill()
    {
        var reception = Shipped(SystemRoles.Receptionist);
        reception.Should().NotContain(Permissions.Billing.DeclareUnpaid);
        reception.Should().NotContain(Permissions.Billing.ReconcileCashSession);
        reception.Should().Contain([Permissions.Billing.RecordPayment, Permissions.Billing.CloseCashSession]);

        Shipped(SystemRoles.Accountant).Should().Contain([Permissions.Billing.DeclareUnpaid,
                                                          Permissions.Billing.ReconcileCashSession]);
        Shipped(SystemRoles.Administrator).Should().Contain(Permissions.Billing.DeclareUnpaid);
        Permissions.All.Should().Contain(Permissions.Billing.DeclareUnpaid);

        // On an installation that already exists, the new right reaches the accounts office once,
        // and nobody else.
        var accountant = new Role(SystemRoles.Accountant, new MultilingualText("Comptabilité"), isBuiltIn: true);
        accountant.SeedShippedPermissions(Shipped(SystemRoles.Accountant).Where(p => p != Permissions.Billing.DeclareUnpaid));
        accountant.ApplyShippedPermissions(Shipped(SystemRoles.Accountant))
            .Should().ContainSingle().Which.Should().Be(Permissions.Billing.DeclareUnpaid);
    }

    [SqlServerFact]
    public async Task AReceptionist_CannotDeclareAChequeUnpaid_TheAccountsOfficeCan()
    {
        var lab = await LabAsync();
        using var reception = Services(lab.SiteId, SystemRoles.Receptionist, "accueil");
        using var accounts = Services(lab.SiteId, SystemRoles.Accountant, "compta");

        var invoiceId = await InvoicedRequestAsync(reception, lab);
        Ok(await On<BillingService, Result<Guid>>(reception, s => s.OpenCashSessionAsync(0m)));
        Ok(await PayAsync(reception, invoiceId, 100m, PaymentMethod.Cheque, "CHQ-R5-1"));

        var cheque = (await On<BillingService, Result<IReadOnlyList<PaymentRow>>>(reception, s => s.GetPaymentsAsync(invoiceId)))
            .Value.Single();
        cheque.CanBounce.Should().BeTrue();

        // The counter is refused, whatever the screen shows; the cheque stays settled.
        (await On<BillingService, Result>(reception, s => s.MarkPaymentBouncedAsync(invoiceId, cheque.PaymentId, "Sans provision")))
            .FirstError.ResourceKey.Should().Be(Denied);
        await using (var context = database.Context())
            (await context.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId)).OutstandingAmount.Should().Be(0m);

        // The accounts office declares it: owed again, and off the receptionist's open shift.
        Ok(await On<BillingService, Result>(accounts, s => s.MarkPaymentBouncedAsync(invoiceId, cheque.PaymentId, "Sans provision")));
        await using (var context = database.Context())
        {
            (await context.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId)).OutstandingAmount.Should().Be(100m);
            (await context.CashSessions.AsNoTracking().SingleAsync(s => s.SiteId == lab.SiteId)).ChequeReceipts.Should().Be(0m);
        }
    }

    [SqlServerFact]
    public async Task AReceptionist_ClosesHerOwnTillWithHerCount_NotAColleagues_AndTheAccountsOfficeSignsItOff()
    {
        var lab = await LabAsync();
        using var amina = Services(lab.SiteId, SystemRoles.Receptionist, "amina");
        using var salma = Services(lab.SiteId, SystemRoles.Receptionist, "salma");
        using var accounts = Services(lab.SiteId, SystemRoles.Accountant, "compta");

        var invoiceId = await InvoicedRequestAsync(amina, lab);
        Ok(await On<BillingService, Result<Guid>>(amina, s => s.OpenCashSessionAsync(200m)));
        Ok(await PayAsync(amina, invoiceId, 100m));

        // A colleague has no till of her own open: her « Clôturer » has nothing to close, and does
        // not reach Amina's.
        (await On<BillingService, Result<decimal>>(salma, s => s.CloseCashSessionAsync(300m, null)))
            .FirstError.ResourceKey.Should().Be("CashSession_NotOpen");

        // Nor can anyone close it in her place through the till itself.
        await using (var context = database.Context())
        {
            var till = await context.CashSessions.SingleAsync(s => s.SiteId == lab.SiteId);
            till.Close(DateTimeOffset.Now, Guid.NewGuid(), 300m, null)
                .FirstError.ResourceKey.Should().Be("CashSession_CloserIsNotOperator");
            till.Status.Should().Be(CashSessionStatus.Open);
        }

        // Amina counts her drawer and closes: 200 float + 100 taken, 5 short, within tolerance.
        (await On<BillingService, Result<decimal>>(amina, s => s.CloseCashSessionAsync(295m, null))).Value.Should().Be(-5m);

        // She cannot sign her own till off; the accounts office sees it waiting and signs it.
        (await On<CashSessionReviewService, Result<IReadOnlyList<CashSessionReviewRow>>>(amina,
            s => s.GetSessionsAsync(DateOnly.FromDateTime(DateTime.Today).AddDays(-1), DateOnly.FromDateTime(DateTime.Today), true)))
            .FirstError.ResourceKey.Should().Be(Denied);

        var waiting = (await On<CashSessionReviewService, Result<IReadOnlyList<CashSessionReviewRow>>>(accounts,
            s => s.GetSessionsAsync(DateOnly.FromDateTime(DateTime.Today).AddDays(-1), DateOnly.FromDateTime(DateTime.Today), true)))
            .Value.Should().ContainSingle().Subject;
        waiting.CountedCash.Should().Be(295m);
        waiting.Variance.Should().Be(-5m);

        (await On<CashSessionReviewService, Result>(accounts, s => s.ReconcileAsync(waiting.Id, null)))
            .FirstError.ResourceKey.Should().Be("CashSession_ReconciliationCommentRequired");
        Ok(await On<CashSessionReviewService, Result>(accounts, s => s.ReconcileAsync(waiting.Id, "Écart de rendu de monnaie accepté")));

        await using (var context = database.Context())
            (await context.CashSessions.AsNoTracking().SingleAsync(s => s.SiteId == lab.SiteId))
                .Status.Should().Be(CashSessionStatus.Reconciled);
    }
}
