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
using MarocLIS.Domain.Relations;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// Discounts at registration, on SQL Server with the services as the workstation composes them: a
/// request of 747 DH (400 + 347) for a self-paying patient. The convention of the prescriber and the
/// rate on the prescriber's file apply by themselves, the higher one alone; a profile chosen at the
/// counter needs the right, a reason, and above the ceiling another user's approval; the prices
/// negotiated in the convention replace the list price unless the order's rate leaves less to pay;
/// the profiles and the prices are kept with the tariffs right and traced; the report lists who
/// granted what, why, and who approved it.
/// </summary>
public sealed class DiscountsDatabaseTests(OrderEntryScratchDatabase database)
    : IClassFixture<OrderEntryScratchDatabase>
{
    private const string Password = "Remise-Test-2026!";

    private sealed class Operator(Guid siteId, IReadOnlySet<string>? permissions, string userName, Guid? userId = null) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = userId ?? Guid.NewGuid();
        public string UserName => userName;
        public string FullName => "Accueil";
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
        public string Workstation => "ACCUEIL-1";
    }

    /// <summary>Reception without the right to grant a discount.</summary>
    private static readonly HashSet<string> Reception =
    [
        Permissions.Patients.View, Permissions.Orders.View, Permissions.Orders.Create, Permissions.Billing.View
    ];

    /// <summary>Reception that may grant a discount.</summary>
    private static readonly HashSet<string> Counter = [.. Reception, Permissions.Billing.ApplyDiscount, Permissions.Billing.CreateInvoice];

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string Keys(IEnumerable<Error> errors) => string.Join(", ", errors.Select(e => e.ResourceKey));

    private ServiceProvider Services(Guid siteId, IReadOnlySet<string>? permissions = null, string userName = "accueil",
                                     Guid? userId = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(siteId, permissions, userName, userId));
        services.AddIsolatedQueries<IBillingQueries, MarocLIS.Infrastructure.Queries.BillingQueries>();
        services.AddIsolatedQueries<IOrderQueries, MarocLIS.Infrastructure.Queries.OrderQueries>();
        services.AddIsolatedQueries<IInvoicePrintSettingsQueries, MarocLIS.Infrastructure.Queries.InvoicePrintSettingsQueries>();
        services.AddIsolatedQueries<IDiscountQueries, MarocLIS.Infrastructure.Queries.DiscountQueries>();
        services.AddIsolatedQueries<IOrderQuotes, OrderQuoteService>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> On<TService, T>(ServiceProvider provider, Func<TService, Task<T>> action)
        where TService : notnull
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private sealed record Lab(Guid SiteId, Guid PatientId, Guid BilanId, Guid HormonesId, Guid PrescriberId, Guid ConventionId);

    /// <summary>
    /// A site that can invoice, a self-paying patient, two acts (400 + 347 = 747 DH), a prescriber
    /// with no rate of their own and the clinic's convention in force with them at 10 %.
    /// </summary>
    private async Task<Lab> LabAsync(decimal? conventionPercent = 10m, decimal? prescriberPercent = null)
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
        var prescriber = new Prescriber("TAZI", "Omar", PrescriberKind.GeneralPractitioner) { DiscountPercent = prescriberPercent };
        var convention = new Partnership(site.Id, "Clinique Al Amal " + Unique(""), PartnerKind.Clinic, PartnershipKind.CareConvention,
                                         DateOnly.FromDateTime(DateTime.Today).AddMonths(-3))
        {
            PrescriberId = prescriber.Id,
            DiscountPercent = conventionPercent
        };

        await using var context = database.Context();
        context.Add(site);
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.ContainerTypes.Add(tube);
        context.AnalysisTests.AddRange(bilan, hormones);
        context.Patients.Add(patient);
        context.Prescribers.Add(prescriber);
        context.Partnerships.Add(convention);
        await context.SaveChangesAsync();

        return new Lab(site.Id, patient.Id, bilan.Id, hormones.Id, prescriber.Id, convention.Id);
    }

    private static Task<Result<RegisteredOrder>> RegisterAsync(ServiceProvider provider, Lab lab,
                                                               Func<RegisterOrderRequest, RegisterOrderRequest>? with = null)
    {
        var request = new RegisterOrderRequest
        {
            PatientId = lab.PatientId,
            AnalysisTestIds = [lab.BilanId, lab.HormonesId],
            PrescriberId = lab.PrescriberId
        };
        return On<OrderRegistrationService, Result<RegisteredOrder>>(provider, s => s.RegisterAsync(with?.Invoke(request) ?? request));
    }

    private async Task<LabOrder> ReadOrderAsync(Guid orderId)
    {
        await using var context = database.Context();
        return await context.LabOrders.AsNoTracking().Include(o => o.Items).SingleAsync(o => o.Id == orderId);
    }

    private async Task<List<string>> ReasonsAsync(string entityName, Guid entityId)
    {
        await using var context = database.Context();
        return await context.AuditEntries.AsNoTracking()
            .Where(a => a.EntityName == entityName && a.EntityId == entityId && a.Reason != null)
            .Select(a => a.Reason!)
            .ToListAsync();
    }

    private async Task<Guid> ProfileAsync(ServiceProvider admin, string name, decimal percent, bool active = true)
    {
        var saved = await On<DiscountService, Result<Guid>>(admin, s => s.SaveProfileAsync(new SaveDiscountProfileRequest
        {
            Name = name, Percent = percent, IsActive = active
        }));
        saved.IsSuccess.Should().BeTrue(Keys(saved.Errors));
        return saved.Value;
    }

    /// <summary>A user in the database with a role holding <paramref name="permissions"/>.</summary>
    private async Task<(string UserName, Guid Id)> UserAsync(Guid siteId, string fullName, params string[] permissions)
    {
        var role = new Role(Unique("R"), new MultilingualText("Rôle " + fullName));
        role.GrantRange(permissions);
        var user = new AppUser(Unique("u").ToLowerInvariant(), fullName, new BCryptPasswordHasher().Hash(Password));
        user.AssignRole(role.Id);
        user.GrantSiteAccess(siteId, isDefault: true);

        await using var context = database.Context();
        context.Roles.Add(role);
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return (user.UserName, user.Id);
    }

    // ---- 1. The automatic discount ---------------------------------------------------------------------

    [SqlServerFact]
    public async Task ThePrescribersConvention_AppliesByItself_ShownWithItsSource_AndAudited()
    {
        var lab = await LabAsync(conventionPercent: 10m);
        using var reception = Services(lab.SiteId, Reception);

        var quoted = await On<IOrderQuotes, Result<OrderQuote>>(reception, q => q.QuoteAsync(new OrderQuoteRequest
        {
            PatientId = lab.PatientId, AnalysisTestIds = [lab.BilanId, lab.HormonesId], PrescriberId = lab.PrescriberId
        }));
        quoted.IsSuccess.Should().BeTrue(Keys(quoted.Errors));
        quoted.Value.Discount!.Source.Should().Be(DiscountSource.Convention);
        quoted.Value.Discount.Percent.Should().Be(10m);
        quoted.Value.Discount.Label.Should().StartWith("Clinique Al Amal");
        quoted.Value.Discount.Amount.Should().Be(Money.Of(74.70m));
        quoted.Value.Pricing.Total.Should().Be(Money.Of(672.30m));

        // A receptionist without the right to grant discounts: the convention applies all the same.
        var registered = await RegisterAsync(reception, lab);
        registered.IsSuccess.Should().BeTrue(Keys(registered.Errors));
        registered.Value.Pricing.Discount.Should().Be(Money.Of(74.70m));

        var order = await ReadOrderAsync(registered.Value.OrderId);
        order.DiscountSource.Should().Be(DiscountSource.Convention);
        order.DiscountPercent.Should().Be(10m);
        order.DiscountAmount.Should().Be(74.70m);
        order.PartnershipId.Should().Be(lab.ConventionId);
        order.DiscountReason.Should().StartWith("Convention : Clinique Al Amal");
        order.Items.Sum(i => i.ChargedPrice).Should().Be(672.30m);

        (await ReasonsAsync(nameof(LabOrder), order.Id)).Should()
            .Contain(r => r.Contains("remise (convention) : 10 %, 74,70 DH"));
    }

    [SqlServerFact]
    public async Task TheHigherOfTheConventionAndThePrescribersRate_Applies_Alone()
    {
        var lab = await LabAsync(conventionPercent: 10m, prescriberPercent: 15m);
        using var reception = Services(lab.SiteId, Reception);

        var registered = await RegisterAsync(reception, lab);
        registered.IsSuccess.Should().BeTrue(Keys(registered.Errors));

        var order = await ReadOrderAsync(registered.Value.OrderId);
        order.DiscountSource.Should().Be(DiscountSource.Prescriber);
        order.DiscountPercent.Should().Be(15m, "15 % and 10 % are not added: the higher applies");
        order.DiscountAmount.Should().Be(112.05m);
        order.PartnershipId.Should().Be(lab.ConventionId, "the order is still the convention's");

        // « Sans convention »: the prescriber's own rate remains.
        var without = await RegisterAsync(reception, lab, r => r with { WithoutConvention = true });
        (await ReadOrderAsync(without.Value.OrderId)).PartnershipId.Should().BeNull();

        // Choosing a convention is granting its price: the right is needed.
        (await RegisterAsync(reception, lab, r => r with { PartnershipId = lab.ConventionId }))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
    }

    // ---- 2. Profiles at the counter -----------------------------------------------------------------------

    [SqlServerFact]
    public async Task AProfile_NeedsTheRight_AReason_AndToBeOffered_ThenAppliesIfHigher()
    {
        var lab = await LabAsync(conventionPercent: 5m);
        using var admin = Services(lab.SiteId);
        using var reception = Services(lab.SiteId, Reception);
        using var counter = Services(lab.SiteId, Counter);

        var family = await ProfileAsync(admin, Unique("Famille "), 8m);
        var stopped = await ProfileAsync(admin, Unique("Ancien "), 9m, active: false);

        (await RegisterAsync(reception, lab, r => r with { DiscountProfileId = family, DiscountReason = "Sœur d'une employée" }))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
        (await RegisterAsync(counter, lab, r => r with { DiscountProfileId = family }))
            .FirstError.ResourceKey.Should().Be("Order_DiscountReasonRequired");
        (await RegisterAsync(counter, lab, r => r with { DiscountProfileId = stopped, DiscountReason = "x" }))
            .FirstError.ResourceKey.Should().Be("Order_DiscountProfileNotAvailable");

        var options = await On<DiscountService, Result<CounterDiscountOptions>>(counter, s => s.GetCounterOptionsAsync());
        options.Value.Profiles.Select(p => p.Id).Should().Contain(family).And.NotContain(stopped);
        options.Value.Conventions.Should().Contain(c => c.Id == lab.ConventionId);
        (await On<DiscountService, Result<CounterDiscountOptions>>(reception, s => s.GetCounterOptionsAsync()))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");

        // 8 % beats the convention's 5 %: the profile applies, alone, within the ceiling of 10 %.
        var registered = await RegisterAsync(counter, lab, r => r with { DiscountProfileId = family, DiscountReason = "Sœur d'une employée" });
        registered.IsSuccess.Should().BeTrue(Keys(registered.Errors));
        var order = await ReadOrderAsync(registered.Value.OrderId);
        order.DiscountSource.Should().Be(DiscountSource.Profile);
        order.DiscountProfileId.Should().Be(family);
        order.DiscountPercent.Should().Be(8m);
        order.DiscountAmount.Should().Be(59.76m);
        order.DiscountReason.Should().Be("Sœur d'une employée");
        order.DiscountApprovedByUserId.Should().BeNull();
    }

    [SqlServerFact]
    public async Task AProfileAboveTheCeiling_NeedsAnotherUsersApproval_TheSameAsAtTheBillingDesk()
    {
        var lab = await LabAsync(conventionPercent: null);
        using var admin = Services(lab.SiteId);
        using var counter = Services(lab.SiteId, Counter, userName: "accueil");

        var staff = await ProfileAsync(admin, Unique("Personnel "), 50m);
        var approver = await UserAsync(lab.SiteId, "Dr BENNANI Leila", Permissions.Billing.ApproveDiscount);
        var clerk = await UserAsync(lab.SiteId, "Agent CAISSE", Permissions.Billing.ApplyDiscount);

        RegisterOrderRequest Staff(RegisterOrderRequest r) => r with { DiscountProfileId = staff, DiscountReason = "Employée du laboratoire" };

        var refused = await RegisterAsync(counter, lab, Staff);
        refused.FirstError.ResourceKey.Should().Be("Invoice_DiscountApprovalRequired");
        refused.FirstError.Args.Should().ContainSingle().Which.As<string>().Should().MatchRegex(@"^74[.,]70 DH$");

        (await RegisterAsync(counter, lab, r => Staff(r) with { DiscountApproval = new DiscountApproval("ACCUEIL", Password) }))
            .FirstError.ResourceKey.Should().Be("Approval_SelfNotAllowed");
        (await RegisterAsync(counter, lab, r => Staff(r) with { DiscountApproval = new DiscountApproval(clerk.UserName, Password) }))
            .FirstError.ResourceKey.Should().Be("Approval_PermissionMissing");
        (await RegisterAsync(counter, lab, r => Staff(r) with { DiscountApproval = new DiscountApproval(approver.UserName, "faux") }))
            .FirstError.ResourceKey.Should().Be("Approval_InvalidCredentials");

        var approved = await RegisterAsync(counter, lab, r => Staff(r) with { DiscountApproval = new DiscountApproval(approver.UserName, Password) });
        approved.IsSuccess.Should().BeTrue(Keys(approved.Errors));
        approved.Value.Pricing.Total.Should().Be(Money.Of(373.50m));

        var order = await ReadOrderAsync(approved.Value.OrderId);
        order.DiscountApprovedByUserId.Should().Be(approver.Id);
        (await ReasonsAsync(nameof(LabOrder), order.Id)).Should().Contain(r =>
            r.Contains("remise (profil) : 50 %, 373,50 DH") && r.Contains("approuvée par Dr BENNANI Leila (" + approver.UserName + ")"));
    }

    // ---- 3. Negotiated prices -----------------------------------------------------------------------------

    [SqlServerFact]
    public async Task NegotiatedPrices_AreKeptWithTheTariffsRight_WithoutOverlap_AndApplyWithoutDoubleCounting()
    {
        var lab = await LabAsync(conventionPercent: 10m);
        using var admin = Services(lab.SiteId);
        using var reception = Services(lab.SiteId, Reception);
        using var counter = Services(lab.SiteId, Counter);
        var start = DateOnly.FromDateTime(DateTime.Today).AddDays(-10);

        var request = new SavePartnershipPriceRequest
        {
            PartnershipId = lab.ConventionId, AnalysisTestId = lab.BilanId, FixedPrice = 300m, ValidFrom = start
        };
        (await On<DiscountService, Result<Guid>>(reception, s => s.SavePriceAsync(request)))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");

        var saved = await On<DiscountService, Result<Guid>>(admin, s => s.SavePriceAsync(request));
        saved.IsSuccess.Should().BeTrue(Keys(saved.Errors));
        (await On<DiscountService, Result<Guid>>(admin, s => s.SavePriceAsync(request with { FixedPrice = 280m, ValidFrom = start.AddDays(5) })))
            .FirstError.ResourceKey.Should().Be("PartnershipPrice_Overlap");

        var listed = await On<DiscountService, Result<IReadOnlyList<PartnershipPriceRow>>>(admin, s => s.GetPricesAsync(lab.ConventionId));
        listed.Value.Should().ContainSingle().Which.Should().Match<PartnershipPriceRow>(r => r.ListPrice == 400m && r.NegotiatedPrice == 300m && r.InForce);
        (await ReasonsAsync(nameof(PartnershipPrice), saved.Value)).Should().Contain(r => r.Contains("Tarif négocié ajouté") && r.Contains("300,00 DH"));

        // The convention: BILAN at its negotiated 300 (its 10 % would leave 360), HORM at 347 less 10 %.
        var registered = await RegisterAsync(reception, lab);
        registered.IsSuccess.Should().BeTrue(Keys(registered.Errors));
        registered.Value.Pricing.Total.Should().Be(Money.Of(612.30m));
        var order = await ReadOrderAsync(registered.Value.OrderId);
        order.Items.Select(i => i.ChargedPrice).Should().BeEquivalentTo([300m, 312.30m]);
        order.DiscountAmount.Should().Be(134.70m);
        order.DiscountSource.Should().Be(DiscountSource.Convention);

        // A profile of 20 %: BILAN keeps its 300 (20 % would leave 320), HORM takes the 20 % — never both.
        var family = await ProfileAsync(admin, Unique("Famille "), 20m);
        var withProfile = await RegisterAsync(counter, lab, r => r with { DiscountProfileId = family, DiscountReason = "Famille" });
        withProfile.IsSuccess.Should().BeTrue(Keys(withProfile.Errors));
        withProfile.Value.Pricing.Total.Should().Be(Money.Of(577.60m));
        withProfile.Value.Pricing.RateDiscount.Should().Be(Money.Of(69.40m), "only the rate's part counts against the ceiling of 74,70 DH");

        // Withdrawn with a reason: the next order is back at the convention's rate on the list price.
        (await On<DiscountService, Result>(admin, s => s.RemovePriceAsync(saved.Value, " "))).FirstError.ResourceKey
            .Should().Be("PartnershipPrice_RemovalReasonRequired");
        (await On<DiscountService, Result>(admin, s => s.RemovePriceAsync(saved.Value, "Avenant 2026"))).IsSuccess.Should().BeTrue();
        (await RegisterAsync(reception, lab)).Value.Pricing.Total.Should().Be(Money.Of(672.30m));
    }

    // ---- Profiles kept with the tariffs right -------------------------------------------------------------

    [SqlServerFact]
    public async Task Profiles_AreKeptWithTheTariffsRight_UniqueByName_AndTraced()
    {
        var lab = await LabAsync();
        using var admin = Services(lab.SiteId);
        using var counter = Services(lab.SiteId, Counter);
        var name = Unique("Campagne ");

        (await On<DiscountService, Result<Guid>>(counter, s => s.SaveProfileAsync(new SaveDiscountProfileRequest { Name = name, Percent = 20m })))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");

        var id = await ProfileAsync(admin, name, 20m);
        (await On<DiscountService, Result<Guid>>(admin, s => s.SaveProfileAsync(new SaveDiscountProfileRequest { Name = name, Percent = 10m })))
            .FirstError.ResourceKey.Should().Be("DiscountProfile_NameTaken");

        var changed = await On<DiscountService, Result<Guid>>(admin, s => s.SaveProfileAsync(new SaveDiscountProfileRequest
        {
            Id = id, Name = name, Percent = 25m, ValidFrom = new DateOnly(2026, 11, 1), ValidUntil = new DateOnly(2026, 11, 30)
        }));
        changed.IsSuccess.Should().BeTrue(Keys(changed.Errors));

        (await ReasonsAsync(nameof(DiscountProfile), id)).Should()
            .Contain(r => r.StartsWith("Profil de remise créé : " + name + " 20 %"))
            .And.Contain(r => r.Contains("→ " + name + " 25 % du 01/11/2026 au 30/11/2026"));
    }

    // ---- 4. The report --------------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task TheReport_ListsWhoGrantedWhat_ItsSource_Reason_AndApprover_AtTheCounterAndOnInvoices()
    {
        var lab = await LabAsync(conventionPercent: 10m);
        using var admin = Services(lab.SiteId);
        var clerk = await UserAsync(lab.SiteId, "Nadia ACCUEIL", [.. Counter]);
        using var counter = Services(lab.SiteId, Counter, userName: clerk.UserName, userId: clerk.Id);
        using var reception = Services(lab.SiteId, Reception);
        var approver = await UserAsync(lab.SiteId, "Dr IDRISSI", Permissions.Billing.ApproveDiscount);
        var staff = await ProfileAsync(admin, Unique("Personnel "), 40m);

        var byConvention = await RegisterAsync(counter, lab);
        var byProfile = await RegisterAsync(counter, lab, r => r with
        {
            DiscountProfileId = staff, DiscountReason = "Employé", DiscountApproval = new DiscountApproval(approver.UserName, Password)
        });
        byProfile.IsSuccess.Should().BeTrue(Keys(byProfile.Errors));

        // A discount granted at the billing desk, on a request registered without the convention.
        var plain = await RegisterAsync(counter, lab, r => r with { WithoutConvention = true });
        var invoice = await On<BillingService, Result<Guid>>(counter, s => s.IssueInvoiceAsync(
            [plain.Value.OrderId], discountAmount: 20m, discountReason: "Geste commercial"));
        invoice.IsSuccess.Should().BeTrue(Keys(invoice.Errors));

        var today = DateOnly.FromDateTime(DateTime.Today);
        (await On<DiscountService, Result<IReadOnlyList<DiscountReportRow>>>(reception, s => s.GetReportAsync(today, today)))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");

        var report = await On<DiscountService, Result<IReadOnlyList<DiscountReportRow>>>(admin, s => s.GetReportAsync(today.AddDays(-1), today));
        report.IsSuccess.Should().BeTrue(Keys(report.Errors));
        var rows = report.Value.Where(r => r.SiteId == lab.SiteId).ToList();
        rows.Should().HaveCount(3);

        var convention = rows.Single(r => r.Reference == byConvention.Value.AccessionNumber);
        convention.Source.Should().Be(DiscountSource.Convention);
        convention.Label.Should().StartWith("Clinique Al Amal");
        convention.Amount.Should().Be(74.70m);
        convention.Gross.Should().Be(747m);
        convention.PatientName.Should().Be("ALAOUI Salma");
        convention.GrantedBy.Should().Be("Nadia ACCUEIL");

        var profile = rows.Single(r => r.Reference == byProfile.Value.AccessionNumber);
        profile.Source.Should().Be(DiscountSource.Profile);
        profile.Percent.Should().Be(40m);
        profile.Amount.Should().Be(298.80m);
        profile.Reason.Should().Be("Employé");
        profile.ApprovedBy.Should().Be("Dr IDRISSI");

        var desk = rows.Single(r => r.OnInvoice);
        desk.Source.Should().Be(DiscountSource.Manual);
        desk.Amount.Should().Be(20m);
        desk.Reason.Should().Be("Geste commercial");
        desk.GrantedBy.Should().Be("Nadia ACCUEIL");

        (await On<DiscountService, Result<IReadOnlyList<DiscountReportRow>>>(admin, s => s.GetReportAsync(today, today.AddDays(-1))))
            .FirstError.ResourceKey.Should().Be("Stats_PeriodReversed");
    }
}
