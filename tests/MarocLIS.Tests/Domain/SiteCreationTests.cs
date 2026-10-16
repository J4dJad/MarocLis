using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesSecurityTests;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Creating a site from the settings screen — above all the partner laboratory the subcontracting
/// circuit sends analyses to, which could not be created anywhere in the application — and keeping
/// a partner out of the places where someone works.
/// </summary>
public sealed class SiteCreationTests
{
    [Fact]
    public async Task A_site_is_created_with_its_code_in_capitals_and_is_audited()
    {
        var fixture = new Fixture(Permissions.Administration.ManageSites);

        var result = await fixture.Service.CreateSiteAsync(new NewSiteRequest
        {
            Code = " sidi-1 ",
            Name = new MultilingualText("  Centre Sidi Maârouf ", "  مركز سيدي معروف "),
            Kind = LabSiteKind.CollectionCentre
        });

        result.IsSuccess.Should().BeTrue();
        var site = fixture.Added.Should().ContainSingle().Subject;
        site.Id.Should().Be(result.Value);
        site.Code.Should().Be("SIDI-1");
        site.Name.Fr.Should().Be("Centre Sidi Maârouf");
        site.Name.Ar.Should().Be("مركز سيدي معروف");
        site.Kind.Should().Be(LabSiteKind.CollectionCentre);
        site.IsActive.Should().BeTrue();

        fixture.Saves.Should().Be(1);
        fixture.AuditReason.Should().Contain("SIDI-1");
        fixture.SecurityEvents.Should().Equal(SecurityEventKind.SecuritySettingChanged);
    }

    [Fact]
    public async Task A_partner_laboratory_is_the_default_kind_and_needs_only_a_code_and_a_name()
    {
        var fixture = new Fixture(Permissions.Administration.ManageSites);

        var result = await fixture.Service.CreateSiteAsync(new NewSiteRequest
        {
            Code = "PASTEUR",
            Name = new MultilingualText("Institut Pasteur du Maroc")
        });

        result.IsSuccess.Should().BeTrue();
        var site = fixture.Added.Should().ContainSingle().Subject;
        site.Kind.Should().Be(LabSiteKind.ReferralPartner);
        site.Name.Ar.Should().BeNull("an empty Arabic name is not stored as blanks");
    }

    [Fact]
    public async Task A_code_already_used_is_refused()
    {
        var fixture = new Fixture(Permissions.Administration.ManageSites) { CodeTaken = true };

        var result = await fixture.Service.CreateSiteAsync(new NewSiteRequest
        {
            Code = "principal",
            Name = new MultilingualText("Doublon")
        });

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Site_CodeAlreadyTaken");
        fixture.CheckedCode.Should().Be("PRINCIPAL", "the code is compared as it will be stored");
        fixture.Added.Should().BeEmpty();
        fixture.Saves.Should().Be(0);
    }

    [Fact]
    public async Task A_missing_name_or_code_is_refused()
    {
        var fixture = new Fixture(Permissions.Administration.ManageSites);

        var result = await fixture.Service.CreateSiteAsync(new NewSiteRequest
        {
            Code = "  ",
            Name = new MultilingualText("  ", "اسم")
        });

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.ResourceKey).Should().BeEquivalentTo("Site_CodeRequired", "Site_NameRequired");
        fixture.Added.Should().BeEmpty();
    }

    [Theory]
    [InlineData("CASA/1")]
    [InlineData("CASA 1")]
    [InlineData("ÉCOLE")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU")]
    public async Task A_code_that_would_break_a_document_number_is_refused(string code)
    {
        var fixture = new Fixture(Permissions.Administration.ManageSites);

        var result = await fixture.Service.CreateSiteAsync(new NewSiteRequest
        {
            Code = code,
            Name = new MultilingualText("Site")
        });

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Site_CodeInvalid");
        fixture.Added.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_the_right_to_manage_sites_nothing_is_created()
    {
        var fixture = new Fixture(Permissions.Administration.ManageSettings);

        var result = await fixture.Service.CreateSiteAsync(new NewSiteRequest
        {
            Code = "PART",
            Name = new MultilingualText("Partenaire")
        });

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be(Fixture.ForbiddenKey);
        fixture.Added.Should().BeEmpty();
        fixture.Saves.Should().Be(0);
    }

    [Fact]
    public async Task A_site_people_work_on_cannot_be_turned_into_a_partner()
    {
        var site = new LabSite("PRINCIPAL", new MultilingualText("Laboratoire principal"), LabSiteKind.MainLaboratory);
        var fixture = new Fixture(Permissions.Administration.ManageSites) { HasUsers = true };
        fixture.Sites[site.Id] = site;

        var result = await fixture.Service.SaveSiteAsync(Request(site, LabSiteKind.ReferralPartner));

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Site_CannotBecomePartner");
        site.Kind.Should().Be(LabSiteKind.MainLaboratory);
        fixture.Saves.Should().Be(0);
    }

    [Fact]
    public async Task A_new_site_nobody_works_on_can_still_be_switched_to_a_partner()
    {
        var site = new LabSite("CENTRE", new MultilingualText("Centre"), LabSiteKind.CollectionCentre);
        var fixture = new Fixture(Permissions.Administration.ManageSites);
        fixture.Sites[site.Id] = site;

        var result = await fixture.Service.SaveSiteAsync(Request(site, LabSiteKind.ReferralPartner));

        result.IsSuccess.Should().BeTrue();
        site.Kind.Should().Be(LabSiteKind.ReferralPartner);
    }

    [Fact]
    public async Task A_partner_laboratory_cannot_be_given_to_an_account()
    {
        var user = new AppUser("tech", "Technicien", "hash");
        user.GrantSiteAccess(Guid.NewGuid(), isDefault: true);

        var queries = Stub<IAdministrationQueries>.Create(new()
        {
            [nameof(IAdministrationQueries.AnyPartnerSiteAsync)] = _ => Task.FromResult(true)
        });
        var users = Stub<IRepository<AppUser>>.Create(new()
        {
            [nameof(IRepository<AppUser>.GetByIdWithDetailsAsync)] = _ => Task.FromResult<AppUser?>(user)
        });
        var saves = 0;
        var unitOfWork = Stub<IUnitOfWork>.Create(new()
        {
            [nameof(IUnitOfWork.SaveChangesAsync)] = _ => { saves++; return Task.FromResult(1); }
        });
        var guard = Stub<IPermissionGuard>.Create(new()
        {
            [nameof(IPermissionGuard.Require)] = _ => Result.Success()
        });

        var service = new UserAdministrationService(
            users, Stub<IRepository<Role>>.Create(), queries, Stub<IAuthenticationService>.Create(), unitOfWork,
            guard, Stub<ICurrentUser>.Create(), Stub<ISecurityAuditor>.Create(),
            Stub<IPasswordHasher>.Create(), new PasswordPolicy(), Stub<IClock>.Create());

        var result = await service.SetSiteAccessAsync(user.Id, [Guid.NewGuid()], null);

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("User_PartnerSiteNotAccessible");
        saves.Should().Be(0);
    }

    [Theory]
    [InlineData("PRINCIPAL", true)]
    [InlineData("DEMO-PART", true)]
    [InlineData("SITE_2", true)]
    [InlineData("", false)]
    [InlineData("casa", false)]
    public void Site_codes_are_capitals_digits_hyphens_and_underscores(string code, bool valid) =>
        ConfigurationService.IsValidSiteCode(code).Should().Be(valid);

    private static SiteRequest Request(LabSite site, LabSiteKind kind) => new()
    {
        SiteId = site.Id,
        Code = site.Code,
        Name = site.Name,
        Kind = kind,
        LegalName = site.LegalName
    };

    // ---- Fixture -------------------------------------------------------------------------------

    private sealed class Fixture
    {
        public const string ForbiddenKey = "Forbidden";

        public Dictionary<Guid, LabSite> Sites { get; } = [];
        public List<LabSite> Added { get; } = [];
        public List<SecurityEventKind> SecurityEvents { get; } = [];
        public bool CodeTaken { get; init; }
        public bool HasUsers { get; init; }
        public string? CheckedCode { get; private set; }
        public int Saves { get; private set; }
        public string? AuditReason { get; private set; }

        public ConfigurationService Service { get; }

        public Fixture(params string[] callerPermissions)
        {
            var granted = callerPermissions.ToHashSet(StringComparer.Ordinal);

            var guard = Stub<IPermissionGuard>.Create(new()
            {
                [nameof(IPermissionGuard.Require)] = a => granted.Contains((string)a[0]!)
                    ? Result.Success()
                    : Result.Failure(Error.Forbidden(ForbiddenKey))
            });

            var sites = Stub<IRepository<LabSite>>.Create(new()
            {
                [nameof(IRepository<LabSite>.GetByIdAsync)] = a => Task.FromResult(Sites.GetValueOrDefault((Guid)a[0]!)),
                [nameof(IRepository<LabSite>.AddAsync)] = a => { Added.Add((LabSite)a[0]!); return Task.CompletedTask; }
            });

            var queries = Stub<IAdministrationQueries>.Create(new()
            {
                [nameof(IAdministrationQueries.SiteCodeExistsAsync)] = a =>
                {
                    CheckedCode = (string)a[0]!;
                    return Task.FromResult(CodeTaken);
                },
                [nameof(IAdministrationQueries.SiteHasUserAccessAsync)] = _ => Task.FromResult(HasUsers)
            });

            UnitOfWorkStub? unitOfWork = null;
            unitOfWork = new UnitOfWorkStub(() => { Saves++; AuditReason = unitOfWork!.AuditReason; });

            var auditor = Stub<ISecurityAuditor>.Create(new()
            {
                [nameof(ISecurityAuditor.LogAsync)] = a =>
                {
                    SecurityEvents.Add((SecurityEventKind)a[0]!);
                    return Task.CompletedTask;
                }
            });

            var clock = Stub<IClock>.Create(new()
            {
                ["get_" + nameof(IClock.Today)] = _ => new DateOnly(2026, 10, 15)
            });

            Service = new ConfigurationService(sites, Stub<IRepository<AppUser>>.Create(), queries,
                                               Stub<IConfigurationWriter>.Create(), unitOfWork, guard,
                                               auditor, clock);
        }
    }

    /// <summary>A unit of work that remembers the audit reason it was given.</summary>
    private sealed class UnitOfWorkStub(Action onSave) : IUnitOfWork
    {
        private readonly IUnitOfWork _rest = Stub<IUnitOfWork>.Create();

        public string? AuditReason { get; set; }

        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            onSave();
            return Task.FromResult(1);
        }

        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default) => _rest.BeginTransactionAsync(ct);
        public Task CommitAsync(CancellationToken ct = default) => _rest.CommitAsync(ct);
        public Task RollbackAsync(CancellationToken ct = default) => _rest.RollbackAsync(ct);
    }
}
