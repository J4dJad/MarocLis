using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.Orders;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Sequencing;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for lot c3b, created from the model and dropped at the end.</summary>
public sealed class Cycle3bScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_Cycle3b_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public Cycle3bScratchDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            ConnectionString = "";
            return;
        }

        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = Name }.ConnectionString;

        using var context = Context();
        context.Database.EnsureCreated();
        _created = true;
    }

    public LisDbContext Context() =>
        new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(ConnectionString).Options);

    public void Dispose()
    {
        if (!_created) return;
        SqlConnection.ClearAllPools();
        using var context = Context();
        context.Database.EnsureDeleted();
    }
}

/// <summary>
/// Cycle 3, lot c3b, against SQL Server and the services as the desktop composes them: an account
/// from its creation with a role to its lockout and its own password change, and a new price that
/// applies to the requests registered after it and to none before.
/// </summary>
public sealed class DefectCycle3bDatabaseTests(Cycle3bScratchDatabase database) : IClassFixture<Cycle3bScratchDatabase>
{
    private sealed class FixedClock : IClock
    {
        public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.Today);
        public DateTimeOffset Now => new(Today.ToDateTime(new TimeOnly(10, 0)));
    }

    /// <summary>A biologist holding every right, signed in on the test's site.</summary>
    private sealed class Operator(Guid userId, Guid siteId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId => userId;
        public string UserName => "c3b";
        public string FullName => "Dr Cycle Trois";
        public Guid SiteId => siteId;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.MedicalBiologist;
        public IReadOnlyList<Guid> AccessibleSiteIds => [siteId];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>(MarocLIS.Domain.Identity.Permissions.All);
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => Permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "C3B-1";
    }

    private sealed record Lab(ServiceProvider Services, LabSite Site, AppUser Biologist);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string Errors(Result result) => string.Join(", ", result.Errors.Select(e => e.ResourceKey));

    private async Task<Lab> LabAsync()
    {
        var biologist = new AppUser(Unique("c3b"), "Dr Cycle Trois", "x")
        {
            Qualification = ProfessionalQualification.MedicalBiologist,
            ProfessionalOrderNumber = "CNOM-C3B-1"
        };
        var site = new LabSite(Unique("L"), new MultilingualText("Laboratoire C3B"), LabSiteKind.MainLaboratory)
        {
            LegalName = "LABORATOIRE C3B SARL",
            Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(),
            FiscalId = "45128796",
            AddressLine = new MultilingualText("1, rue du Test"),
            City = "Rabat",
            HealthMinistryAuthorizationNumber = "AUT-C3B-1",
            ResponsibleBiologistUserId = biologist.Id
        };

        await using (var context = database.Context())
        {
            context.Users.Add(biologist);
            context.Sites.Add(site);
            await context.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(biologist.Id, site.Id));
        services.AddSingleton<IClock>(new FixedClock());
        services.AddIsolatedQueries<IPatientQueries, PatientQueries>();
        services.AddIsolatedQueries<ICatalogQueries, CatalogQueries>();
        services.AddIsolatedQueries<IOrderQueries, OrderQueries>();
        services.AddIsolatedQueries<ICatalogMaintenanceQueries, CatalogMaintenanceQueries>();
        services.AddIsolatedQueries<IAdministrationQueries, AdministrationQueries>();
        return new Lab(services.BuildServiceProvider(), site, biologist);
    }

    private static async Task<T> On<TService, T>(Lab lab, Func<TService, Task<T>> action) where TService : notnull
    {
        using var scope = lab.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    // ---- An account, from its creation to its own password change ---------------------------------

    /// <summary>
    /// A secretary's account created with a role: she signs in with the role's rights and nothing
    /// more, and a right taken from the role is gone at her next sign-in. Five wrong passwords lock
    /// the account, the right one then says so, the administrator unlocks it, and her own password
    /// change is refused with a wrong current password and works with the right one, after which
    /// the old password no longer opens the session.
    /// </summary>
    [SqlServerFact]
    public async Task AnAccount_FromItsRoleToItsLockoutAndItsOwnPasswordChange()
    {
        var lab = await LabAsync();

        var roleId = (await On<UserAdministrationService, Result<Guid>>(lab, s => s.CreateRoleAsync(
            Unique("SEC"), new MultilingualText("Secrétariat C3B"),
            [Permissions.Patients.View, Permissions.Orders.Create]))).Value;

        const string initial = "Accueil-Initial-2026!";
        var userName = Unique("sec").ToLowerInvariant();
        var created = await On<UserAdministrationService, Result<Guid>>(lab, s => s.CreateUserAsync(new CreateUserRequest
        {
            UserName = userName, FullName = "Salma Idrissi", InitialPassword = initial,
            RoleIds = [roleId], SiteIds = [lab.Site.Id], DefaultSiteId = lab.Site.Id
        }));
        created.IsSuccess.Should().BeTrue(Errors(created));

        var session = await On<IAuthenticationService, Result<AuthenticatedSession>>(lab, s => s.SignInAsync(userName, initial, null));
        session.IsSuccess.Should().BeTrue(Errors(session));
        session.Value.Permissions.Should().BeEquivalentTo([Permissions.Patients.View, Permissions.Orders.Create]);
        session.Value.Permissions.Should().NotContain(Permissions.Administration.ViewUsers);

        (await On<UserAdministrationService, Result>(lab, s => s.SetRolePermissionsAsync(roleId, [Permissions.Patients.View])))
            .IsSuccess.Should().BeTrue();
        (await On<IAuthenticationService, Result<AuthenticatedSession>>(lab, s => s.SignInAsync(userName, initial, null)))
            .Value.Permissions.Should().BeEquivalentTo([Permissions.Patients.View]);

        // Five wrong passwords: each answered like an unknown name, then the account is locked.
        for (var attempt = 0; attempt < 5; attempt++)
            Errors(await On<IAuthenticationService, Result<AuthenticatedSession>>(lab, s => s.SignInAsync(userName, "Mauvais-2026!", null)))
                .Should().Be("Auth_InvalidCredentials");

        var locked = await On<IAuthenticationService, Result<AuthenticatedSession>>(lab, s => s.SignInAsync(userName, initial, null));
        locked.IsSuccess.Should().BeFalse();
        Errors(locked).Should().NotBe("Auth_InvalidCredentials", "the holder, with the right password, is told the account is locked");

        (await On<UserAdministrationService, Result>(lab, s => s.UnlockAsync(created.Value))).IsSuccess.Should().BeTrue();
        (await On<IAuthenticationService, Result<AuthenticatedSession>>(lab, s => s.SignInAsync(userName, initial, null)))
            .IsSuccess.Should().BeTrue();

        // Her own change: the current password is checked, the new one then is the only one.
        const string chosen = "Choisi-Par-Salma-2026!";
        Errors(await On<IAuthenticationService, Result>(lab, s => s.ChangePasswordAsync(created.Value, "Mauvais-2026!", chosen)))
            .Should().Be("Auth_InvalidCredentials");
        (await On<IAuthenticationService, Result>(lab, s => s.ChangePasswordAsync(created.Value, initial, chosen)))
            .IsSuccess.Should().BeTrue();

        (await On<IAuthenticationService, Result<AuthenticatedSession>>(lab, s => s.SignInAsync(userName, chosen, null)))
            .IsSuccess.Should().BeTrue();
        Errors(await On<IAuthenticationService, Result<AuthenticatedSession>>(lab, s => s.SignInAsync(userName, initial, null)))
            .Should().Be("Auth_InvalidCredentials");
    }

    // ---- A new price -------------------------------------------------------------------------------

    /// <summary>
    /// A price changed in the catalogue applies to the requests registered from its date and to
    /// none registered before: a new tariff from today charges the new price on the next request,
    /// a correction of that tariff the corrected one, and the earlier requests keep the price they
    /// were registered at. A tariff backdated behind the one in force is refused.
    /// </summary>
    [SqlServerFact]
    public async Task ANewPrice_AppliesToTheRequestsRegisteredAfterIt_AndToNoneBefore()
    {
        var lab = await LabAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);

        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sérum"));
        var analysis = new AnalysisTest(Unique("GLY"), new MultilingualText("Glycémie"), department.Id, specimen.Id)
        {
            RoutineTurnaroundHours = 4
        };
        var parameter = new TestParameter(Unique("GLY"), new MultilingualText("Glycémie"), ResultValueType.Numeric) { Decimals = 2 };
        parameter.AddReferenceRange(ReferenceRange.Numeric(0.7m, 1.1m));
        analysis.AddParameter(parameter);
        analysis.AddTariff(new AnalysisTestTariff(analysis.Id, payerId: null, today.AddYears(-1))
        {
            LetterKey = "B", Coefficient = 20m, LetterKeyValue = 1m, Price = 20m, IsVatExempt = true
        });
        var patient = new Patient(Unique("P"), "BENNANI", "Karim", new DateOnly(1975, 3, 2), Gender.Male, lab.Site.Id)
        {
            MobilePhone = "0612345678",
            SearchKey = PatientSearchKey.Build("BENNANI", "Karim")
        };

        await using (var context = database.Context())
        {
            context.Departments.Add(department);
            context.SpecimenTypes.Add(specimen);
            context.AnalysisTests.Add(analysis);
            context.Patients.Add(patient);
            await context.SaveChangesAsync();
        }

        async Task<Guid> RegisterAsync()
        {
            var registered = await On<OrderRegistrationService, Result<RegisteredOrder>>(lab, s => s.RegisterAsync(new RegisterOrderRequest
            {
                PatientId = patient.Id, AnalysisTestIds = [analysis.Id], PrescriberFreeText = "Dr Prescripteur"
            }));
            registered.IsSuccess.Should().BeTrue(Errors(registered));
            return registered.Value.OrderId;
        }

        async Task<decimal> ChargedAsync(Guid orderId)
        {
            await using var context = database.Context();
            return await context.LabOrderItems.AsNoTracking().Where(i => i.LabOrderId == orderId)
                                .Select(i => i.ChargedPrice).SingleAsync();
        }

        TariffRequest Tariff(decimal price, DateOnly from) => new()
        {
            AnalysisTestId = analysis.Id, PayerId = null, ValidFrom = from, LetterKey = "B",
            Coefficient = price, LetterKeyValue = 1m, Price = price, IsVatExempt = true
        };

        var before = await RegisterAsync();
        (await ChargedAsync(before)).Should().Be(20m);

        Errors(await On<CatalogMaintenanceService, Result<Guid>>(lab, s => s.AddTariffAsync(Tariff(30m, today.AddYears(-2)))))
            .Should().Contain("Tariff_NotAfterCurrent");

        var newTariff = await On<CatalogMaintenanceService, Result<Guid>>(lab, s => s.AddTariffAsync(Tariff(35m, today)));
        newTariff.IsSuccess.Should().BeTrue(Errors(newTariff));

        var after = await RegisterAsync();
        (await ChargedAsync(after)).Should().Be(35m);
        (await ChargedAsync(before)).Should().Be(20m, "a request keeps the price it was registered at");

        var corrected = await On<CatalogMaintenanceService, Result>(lab, s => s.UpdateTariffAsync(newTariff.Value, Tariff(36m, today)));
        corrected.IsSuccess.Should().BeTrue(Errors(corrected));

        var afterCorrection = await RegisterAsync();
        (await ChargedAsync(afterCorrection)).Should().Be(36m);
        (await ChargedAsync(after)).Should().Be(35m);
        (await ChargedAsync(before)).Should().Be(20m);
    }

    // ---- Numbering ---------------------------------------------------------------------------------

    /// <summary>
    /// A numbering format whose numbers would not fit the column they are written to is refused
    /// when it is typed, with the number it would give, instead of failing every document of the
    /// series afterwards; a format that fits is saved.
    /// </summary>
    [SqlServerFact]
    public async Task ANumberingFormat_TooLongForItsRegister_IsRefusedWhenTyped()
    {
        var lab = await LabAsync();
        var sequence = new NumberSequence(SequencePurpose.WorkAccident, null, 2099, "AT-{YEAR}-{SEQ:4}");
        await using (var context = database.Context())
        {
            context.Add(sequence);
            await context.SaveChangesAsync();
        }

        var tooLong = await On<ConfigurationService, Result>(lab, s => s.SetSequenceFormatAsync(sequence.Id, "ACCIDENT-{YEAR}-{SEQ:10}"));
        Errors(tooLong).Should().Be("Sequence_FormatTooLong");
        tooLong.Errors[0].Args.Should().Contain(NumberSequence.MaxLength(SequencePurpose.WorkAccident));

        var fits = await On<ConfigurationService, Result>(lab, s => s.SetSequenceFormatAsync(sequence.Id, "AT{YY}-{SEQ:6}"));
        fits.IsSuccess.Should().BeTrue(Errors(fits));

        await using var check = database.Context();
        (await check.Set<NumberSequence>().AsNoTracking().SingleAsync(x => x.Id == sequence.Id)).Format.Should().Be("AT{YY}-{SEQ:6}");
    }

    /// <summary>The longest number of each series is the length of the column it is written to.</summary>
    [Fact]
    public void EachSeries_MaxLength_IsItsColumns()
    {
        using var context = new LisDbContext(new DbContextOptionsBuilder<LisDbContext>()
            .UseSqlServer("Server=.;Database=none;TrustServerCertificate=true").Options);
        int Column<T>(string property) => context.Model.FindEntityType(typeof(T))!.FindProperty(property)!.GetMaxLength()!.Value;

        NumberSequence.MaxLength(SequencePurpose.Accession).Should().Be(Column<MarocLIS.Domain.Orders.LabOrder>("AccessionNumber"));
        NumberSequence.MaxLength(SequencePurpose.MedicalRecord).Should().Be(Column<Patient>("MedicalRecordNumber"));
        NumberSequence.MaxLength(SequencePurpose.SampleBarcode).Should().Be(Column<MarocLIS.Domain.Samples.Sample>("Barcode"));
        NumberSequence.MaxLength(SequencePurpose.Invoice).Should().Be(Column<MarocLIS.Domain.Billing.Invoice>("Number"));
        NumberSequence.MaxLength(SequencePurpose.CreditNote).Should().Be(Column<MarocLIS.Domain.Billing.Invoice>("Number"));
        NumberSequence.MaxLength(SequencePurpose.ClaimBatch).Should().Be(Column<MarocLIS.Domain.Billing.Invoice>("ClaimReference"));
        NumberSequence.MaxLength(SequencePurpose.NonConformity).Should().Be(Column<MarocLIS.Domain.Quality.NonConformity>("Reference"));
        NumberSequence.MaxLength(SequencePurpose.SubcontractDispatch).Should().Be(Column<MarocLIS.Domain.Orders.LabOrderItem>("SubcontractDispatchNumber"));
        NumberSequence.MaxLength(SequencePurpose.EmployeeNumber).Should().Be(Column<MarocLIS.Domain.HumanResources.Employee>("Number"));
        NumberSequence.MaxLength(SequencePurpose.WorkAccident).Should().Be(Column<MarocLIS.Domain.HumanResources.WorkAccident>("Number"));
        NumberSequence.MaxLength(SequencePurpose.PurchaseOrder).Should().Be(Column<MarocLIS.Domain.Inventory.PurchaseOrder>("Number"));
        NumberSequence.MaxLength(SequencePurpose.QuoteRequest).Should().Be(Column<MarocLIS.Domain.Inventory.QuoteRequest>("Number"));
        NumberSequence.MaxLength(SequencePurpose.InventoryCount).Should().Be(Column<MarocLIS.Domain.Inventory.InventoryCount>("Number"));
    }
}
