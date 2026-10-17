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
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// The discount ceiling (« Plafond des remises », Paramètres › Factures) and the approval on the
/// spot of a discount above it, on SQL Server with the services as the desktop composes them: a
/// request of 747 DH, so 10 % is 74,70 DH; within the ceiling the invoice is issued as before;
/// above it the service refuses it without an approver, with the operator's own name, with a user
/// who lacks the right or with a wrong password (counted towards that user's lockout), and accepts
/// it with another user holding billing.discount.approve, traced in the audit trail.
/// </summary>
public sealed class DiscountCapDatabaseTests(OrderEntryScratchDatabase database)
    : IClassFixture<OrderEntryScratchDatabase>
{
    private const string Password = "Remise-Test-2026!";

    private sealed class Operator(Guid siteId, IReadOnlySet<string>? permissions, string userName) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => userName;
        public string FullName => "Caisse";
        public Guid SiteId => siteId;
        public string SiteCode { get; } = "S" + ((uint)siteId.GetHashCode()).ToString("X8");
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.None;
        public IReadOnlyList<Guid> AccessibleSiteIds => [siteId];
        public IReadOnlySet<string> Permissions { get; } = permissions ?? new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => permissions is null || permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "CAISSE-1";
    }

    private static readonly HashSet<string> Cashier =
    [
        Permissions.Patients.View, Permissions.Patients.Create, Permissions.Orders.View,
        Permissions.Orders.Create, Permissions.Billing.View, Permissions.Billing.CreateInvoice,
        Permissions.Billing.RecordPayment, Permissions.Billing.ApplyDiscount
    ];

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string Keys(IEnumerable<Error> errors) => string.Join(", ", errors.Select(e => e.ResourceKey));

    private ServiceProvider Services(Guid siteId, IReadOnlySet<string>? permissions = null, string userName = "caisse")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = database.ConnectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(siteId, permissions, userName));
        services.AddIsolatedQueries<IBillingQueries, MarocLIS.Infrastructure.Queries.BillingQueries>();
        services.AddIsolatedQueries<IOrderQueries, MarocLIS.Infrastructure.Queries.OrderQueries>();
        services.AddIsolatedQueries<IInvoicePrintSettingsQueries, MarocLIS.Infrastructure.Queries.InvoicePrintSettingsQueries>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> On<TService, T>(ServiceProvider provider, Func<TService, Task<T>> action)
        where TService : notnull
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private sealed record Lab(Guid SiteId, Guid PatientId, Guid[] AnalysisIds);

    /// <summary>A site that can invoice, a self-paying patient and two acts: 400 + 347 = 747 DH.</summary>
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
            analysis.AddTariff(new AnalysisTestTariff(analysis.Id, payerId: null, validFrom)
            {
                Price = price, IsVatExempt = true
            });
            return analysis;
        }

        var bilan = Analysis("BILAN", 400m);
        var hormones = Analysis("HORM", 347m);
        var patient = new Patient(Unique("P"), "ALAOUI", "Salma", new DateOnly(1979, 6, 3), Gender.Female, site.Id);

        await using var context = database.Context();
        context.Add(site);
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.ContainerTypes.Add(tube);
        context.AnalysisTests.AddRange(bilan, hormones);
        context.Patients.Add(patient);
        await context.SaveChangesAsync();

        return new Lab(site.Id, patient.Id, [bilan.Id, hormones.Id]);
    }

    private static async Task<Guid> RegisterAsync(ServiceProvider cashier, Lab lab)
    {
        var registered = await On<OrderRegistrationService, Result<RegisteredOrder>>(cashier, s => s.RegisterAsync(
            new RegisterOrderRequest
            {
                PatientId = lab.PatientId,
                AnalysisTestIds = lab.AnalysisIds,
                PrescriberFreeText = "Dr TAZI"
            }));
        registered.IsSuccess.Should().BeTrue(Keys(registered.Errors));
        return registered.Value.OrderId;
    }

    /// <summary>A user in the database with a role holding <paramref name="permissions"/>.</summary>
    private async Task<string> UserAsync(Guid siteId, string fullName, params string[] permissions)
    {
        var hasher = new BCryptPasswordHasher();
        var role = new Role(Unique("R"), new MultilingualText("Rôle " + fullName));
        role.GrantRange(permissions);

        var user = new AppUser(Unique("u").ToLowerInvariant(), fullName, hasher.Hash(Password));
        user.AssignRole(role.Id);
        user.GrantSiteAccess(siteId, isDefault: true);

        await using var context = database.Context();
        context.Roles.Add(role);
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user.UserName;
    }

    private async Task<AppUser> ReadUserAsync(string userName)
    {
        await using var context = database.Context();
        return await context.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.UserName == userName);
    }

    private static async Task SetCapAsync(ServiceProvider admin, DiscountCapSettings cap) =>
        (await On<InvoicePrintSettingsService, Result>(admin, s => s.SaveDiscountCapAsync(cap)))
            .IsSuccess.Should().BeTrue();

    private static Task<Result<Guid>> IssueAsync(ServiceProvider cashier, Guid orderId, decimal discount,
                                                 DiscountApproval? approval = null) =>
        On<BillingService, Result<Guid>>(cashier, s => s.IssueInvoiceAsync(
            [orderId], discountAmount: discount, discountReason: "Geste commercial", approval: approval));

    [SqlServerFact]
    public async Task WithinTheCeiling_TheInvoiceIsIssuedAsBefore_WithoutApproval()
    {
        var lab = await LabAsync();
        using var admin = Services(lab.SiteId);
        using var cashier = Services(lab.SiteId, Cashier);
        await SetCapAsync(admin, DiscountCapSettings.Default);

        // « 10 % » of 747 DH: exactly the ceiling.
        var orderId = await RegisterAsync(cashier, lab);
        var issued = await IssueAsync(cashier, orderId, 74.70m);
        issued.IsSuccess.Should().BeTrue(Keys(issued.Errors));

        var document = (await On<BillingService, Result<InvoiceDocument>>(cashier, s => s.GetInvoiceDocumentAsync(issued.Value))).Value;
        document.GrossAmount.Should().Be(747m);
        document.DiscountAmount.Should().Be(74.70m);
        document.TotalAmount.Should().Be(672.30m);

        await using var context = database.Context();
        (await context.AuditEntries.AsNoTracking()
            .AnyAsync(a => a.EntityId == issued.Value && a.Reason != null && a.Reason.Contains("approuvée")))
            .Should().BeFalse("no approval was needed");
    }

    [SqlServerFact]
    public async Task AboveTheCeiling_IsRefused_WithoutApprover_WithOwnName_WithoutTheRight_OrWithAWrongPassword()
    {
        var lab = await LabAsync();
        using var admin = Services(lab.SiteId);
        using var cashier = Services(lab.SiteId, Cashier, userName: "caisse");
        await SetCapAsync(admin, DiscountCapSettings.Default);

        var approver = await UserAsync(lab.SiteId, "Dr BENNANI", Permissions.Billing.ApproveDiscount);
        var clerk = await UserAsync(lab.SiteId, "Agent ACCUEIL", Permissions.Billing.View, Permissions.Billing.ApplyDiscount);
        var orderId = await RegisterAsync(cashier, lab);

        // Without an approver: the ceiling in dirhams on this invoice is named.
        var refused = await IssueAsync(cashier, orderId, 80m);
        refused.FirstError.ResourceKey.Should().Be("Invoice_DiscountApprovalRequired");
        refused.FirstError.Args.Should().ContainSingle().Which.As<string>().Should().MatchRegex(@"^74[.,]70 DH$", "the ceiling in dirhams, in the culture of the screen");

        (await IssueAsync(cashier, orderId, 80m, new DiscountApproval("", "")))
            .FirstError.ResourceKey.Should().Be("Invoice_DiscountApprovalRequired");

        // The operator's own name, whatever the case they type it in.
        (await IssueAsync(cashier, orderId, 80m, new DiscountApproval(" CAISSE ", Password)))
            .FirstError.ResourceKey.Should().Be("Approval_SelfNotAllowed");

        // A user without billing.discount.approve, even with the right to grant discounts.
        var noRight = await IssueAsync(cashier, orderId, 80m, new DiscountApproval(clerk, Password));
        noRight.FirstError.ResourceKey.Should().Be("Approval_PermissionMissing");
        noRight.FirstError.Args.Should().Contain("Agent ACCUEIL");

        // A wrong password, or a name that is no account: the same answer.
        (await IssueAsync(cashier, orderId, 80m, new DiscountApproval(approver, "wrong-password")))
            .FirstError.ResourceKey.Should().Be("Approval_InvalidCredentials");
        (await IssueAsync(cashier, orderId, 80m, new DiscountApproval("personne", Password)))
            .FirstError.ResourceKey.Should().Be("Approval_InvalidCredentials");

        (await ReadUserAsync(approver)).FailedLoginAttempts.Should().Be(1, "a wrong approval password counts like a failed sign-in");

        // Nothing was issued: the request still waits for its invoice.
        await using var context = database.Context();
        (await context.LabOrders.AsNoTracking().SingleAsync(o => o.Id == orderId)).InvoiceId.Should().BeNull();
        (await context.Invoices.AsNoTracking().CountAsync(i => i.SiteId == lab.SiteId)).Should().Be(0);
    }

    [SqlServerFact]
    public async Task WrongApprovalPasswords_LockTheApproverOut_AsAtTheSignInScreen()
    {
        var lab = await LabAsync();
        using var admin = Services(lab.SiteId);
        using var cashier = Services(lab.SiteId, Cashier);
        await SetCapAsync(admin, DiscountCapSettings.Default);

        var approver = await UserAsync(lab.SiteId, "Dr IDRISSI", Permissions.Billing.ApproveDiscount);
        var orderId = await RegisterAsync(cashier, lab);

        for (var attempt = 0; attempt < new PasswordPolicy().MaxFailedAttempts; attempt++)
            (await IssueAsync(cashier, orderId, 80m, new DiscountApproval(approver, "wrong-password")))
                .FirstError.ResourceKey.Should().Be("Approval_InvalidCredentials");

        var locked = await ReadUserAsync(approver);
        locked.LockedOutUntil.Should().NotBeNull();

        // Locked: even the right password is refused, and the holder learns why.
        (await IssueAsync(cashier, orderId, 80m, new DiscountApproval(approver, Password)))
            .FirstError.ResourceKey.Should().Be("Auth_AccountLockedOut");
    }

    [SqlServerFact]
    public async Task AboveTheCeiling_WithAnotherUserHoldingTheRight_IsIssued_AndTheApprovalIsAudited()
    {
        var lab = await LabAsync();
        using var admin = Services(lab.SiteId);
        using var cashier = Services(lab.SiteId, Cashier);
        await SetCapAsync(admin, DiscountCapSettings.Default);

        var approver = await UserAsync(lab.SiteId, "Dr BENNANI Leila", Permissions.Billing.ApproveDiscount);
        var orderId = await RegisterAsync(cashier, lab);

        // A first typo, then the right password: issued, and the counter starts again from zero.
        (await IssueAsync(cashier, orderId, 150m, new DiscountApproval(approver, "typo")))
            .FirstError.ResourceKey.Should().Be("Approval_InvalidCredentials");

        var issued = await IssueAsync(cashier, orderId, 150m, new DiscountApproval(approver.ToUpperInvariant(), Password));
        issued.IsSuccess.Should().BeTrue(Keys(issued.Errors));

        var document = (await On<BillingService, Result<InvoiceDocument>>(cashier, s => s.GetInvoiceDocumentAsync(issued.Value))).Value;
        document.DiscountAmount.Should().Be(150m);
        document.TotalAmount.Should().Be(597m);
        document.DiscountReason.Should().Be("Geste commercial", "the printed reason is the operator's, the approval is in the audit trail");

        var user = await ReadUserAsync(approver);
        user.FailedLoginAttempts.Should().Be(0);
        user.LastLoginAt.Should().BeNull("approving is not signing in");

        // The session at the counter is the operator's: nobody was signed in by the approval.
        cashier.GetRequiredService<CurrentUserAccessor>().IsAuthenticated.Should().BeFalse();
        cashier.GetRequiredService<ICurrentUser>().UserName.Should().Be("caisse");

        await using var context = database.Context();
        var reasons = await context.AuditEntries.AsNoTracking()
            .Where(a => a.EntityName == nameof(Invoice) && a.EntityId == issued.Value && a.Reason != null)
            .Select(a => a.Reason!)
            .ToListAsync();
        reasons.Should().NotBeEmpty();
        reasons.Should().Contain(r => r.Contains("remise de 150,00 DH")
                                      && r.Contains("plafond de 74,70 DH")
                                      && r.Contains("10 % du brut")
                                      && r.Contains("approuvée par Dr BENNANI Leila (" + approver + ")"));
    }

    [SqlServerFact]
    public async Task TheCeilingInForce_IsTheLowerOfThePercentageAndTheAmount()
    {
        var lab = await LabAsync();
        using var admin = Services(lab.SiteId);
        using var cashier = Services(lab.SiteId, Cashier);

        // 10 % = 74,70 DH, but at most 50 DH: the amount is the ceiling.
        await SetCapAsync(admin, new DiscountCapSettings { MaxPercent = 10m, MaxAmount = 50m });
        var first = await RegisterAsync(cashier, lab);
        var refused = await IssueAsync(cashier, first, 60m);
        refused.FirstError.ResourceKey.Should().Be("Invoice_DiscountApprovalRequired");
        refused.FirstError.Args.Should().ContainSingle().Which.As<string>().Should().MatchRegex(@"^50[.,]00 DH$");
        (await IssueAsync(cashier, first, 50m)).IsSuccess.Should().BeTrue();

        // 5 % = 37,35 DH under a ceiling of 500 DH: the percentage is the ceiling.
        await SetCapAsync(admin, new DiscountCapSettings { MaxPercent = 5m, MaxAmount = 500m });
        var second = await RegisterAsync(cashier, lab);
        (await IssueAsync(cashier, second, 37.36m)).FirstError.ResourceKey.Should().Be("Invoice_DiscountApprovalRequired");
        (await IssueAsync(cashier, second, 37.35m)).IsSuccess.Should().BeTrue();

        // Read back as saved; no amount ceiling when the box is left empty.
        await SetCapAsync(admin, new DiscountCapSettings { MaxPercent = 15m, MaxAmount = null });
        var read = (await On<InvoicePrintSettingsService, Result<DiscountCapSettings>>(cashier, s => s.GetDiscountCapAsync())).Value;
        read.Should().Be(new DiscountCapSettings { MaxPercent = 15m, MaxAmount = null });
        var third = await RegisterAsync(cashier, lab);
        (await IssueAsync(cashier, third, 112.05m)).IsSuccess.Should().BeTrue("15 % of 747 DH");

        await SetCapAsync(admin, DiscountCapSettings.Default);
    }

    [SqlServerFact]
    public async Task ChangingTheCeiling_NeedsTheSettingsRight_IsValidated_AndAudited()
    {
        var lab = await LabAsync();
        using var admin = Services(lab.SiteId);
        using var cashier = Services(lab.SiteId, Cashier);
        await SetCapAsync(admin, DiscountCapSettings.Default);

        var cap = new DiscountCapSettings { MaxPercent = 12.5m, MaxAmount = 200m };
        (await On<InvoicePrintSettingsService, Result>(cashier, s => s.SaveDiscountCapAsync(cap)))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");

        (await On<InvoicePrintSettingsService, Result>(admin, s => s.SaveDiscountCapAsync(cap with { MaxPercent = 120m })))
            .FirstError.ResourceKey.Should().Be("DiscountCap_PercentOutOfRange");
        (await On<InvoicePrintSettingsService, Result>(admin, s => s.SaveDiscountCapAsync(cap with { MaxAmount = -1m })))
            .FirstError.ResourceKey.Should().Be("DiscountCap_AmountOutOfRange");

        (await On<InvoicePrintSettingsService, Result>(admin, s => s.SaveDiscountCapAsync(cap))).IsSuccess.Should().BeTrue();
        (await On<InvoicePrintSettingsService, Result<DiscountCapSettings>>(cashier, s => s.GetDiscountCapAsync()))
            .Value.Should().Be(cap);

        await using (var context = database.Context())
        {
            var traced = await context.AuditEntries.AsNoTracking()
                .Where(a => a.EntityName == nameof(AppSetting) && a.Reason != null
                            && a.Reason.StartsWith("Plafond des remises modifié : 12,5 % du brut, montant maximal 200,00 DH"))
                .ToListAsync();
            traced.Should().NotBeEmpty();
        }

        await SetCapAsync(admin, DiscountCapSettings.Default);
    }
}
