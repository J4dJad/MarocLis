using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Samples;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesSecurityTests;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// The site's list of sample storage places (« Paramètres › Lieux de stockage »): a name is
/// required, unique on the site, and a place is taken out of use rather than deleted.
/// </summary>
public sealed class SampleStorageLocationTests
{
    private static readonly Guid Site = Guid.NewGuid();

    // ---- Domain ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_place_needs_a_name(string? name)
    {
        var created = SampleStorageLocation.Create(Site, name);

        created.IsFailure.Should().BeTrue();
        created.FirstError.ResourceKey.Should().Be("SampleStorageLocation_NameRequired");
    }

    [Fact]
    public void A_name_longer_than_the_tubes_column_is_refused()
    {
        var created = SampleStorageLocation.Create(Site, new string('R', SampleStorageLocation.NameMaxLength + 1));

        created.IsFailure.Should().BeTrue();
        created.FirstError.ResourceKey.Should().Be("SampleStorageLocation_NameTooLong");
    }

    [Fact]
    public void A_new_place_is_trimmed_active_and_of_the_site()
    {
        var place = SampleStorageLocation.Create(Site, "  Réfrigérateur R1 ").Value;

        place.Name.Should().Be("Réfrigérateur R1");
        place.IsActive.Should().BeTrue();
        place.SiteId.Should().Be(Site);
    }

    [Fact]
    public void A_place_is_deactivated_and_reactivated_once()
    {
        var place = SampleStorageLocation.Create(Site, "Congélateur C1").Value;

        place.Deactivate().IsSuccess.Should().BeTrue();
        place.IsActive.Should().BeFalse();
        place.Deactivate().FirstError.ResourceKey.Should().Be("SampleStorageLocation_AlreadyInactive");

        place.Reactivate().IsSuccess.Should().BeTrue();
        place.IsActive.Should().BeTrue();
        place.Reactivate().FirstError.ResourceKey.Should().Be("SampleStorageLocation_AlreadyActive");
    }

    [Fact]
    public void Renaming_to_an_empty_name_keeps_the_old_one()
    {
        var place = SampleStorageLocation.Create(Site, "Paillasse").Value;

        place.Rename(" ").FirstError.ResourceKey.Should().Be("SampleStorageLocation_NameRequired");
        place.Name.Should().Be("Paillasse");
    }

    // ---- Service --------------------------------------------------------------------------------

    [Fact]
    public async Task A_place_is_created_on_the_operators_site_and_saved()
    {
        var fixture = new Fixture();

        var created = await fixture.Service.CreateAsync(" Réfrigérateur R2 ");

        created.IsSuccess.Should().BeTrue();
        fixture.Added.Should().ContainSingle().Which.Should().Match<SampleStorageLocation>(
            p => p.Name == "Réfrigérateur R2" && p.SiteId == Site && p.Id == created.Value);
        fixture.Saves.Should().Be(1);
        fixture.CheckedName.Should().Be("Réfrigérateur R2");
    }

    [Fact]
    public async Task A_name_already_on_the_site_is_refused()
    {
        var fixture = new Fixture { NameTaken = true };

        var created = await fixture.Service.CreateAsync("réfrigérateur r1");

        created.IsFailure.Should().BeTrue();
        created.FirstError.ResourceKey.Should().Be("SampleStorageLocation_NameTaken");
        fixture.Added.Should().BeEmpty();
        fixture.Saves.Should().Be(0);
    }

    [Fact]
    public async Task An_empty_name_is_refused_before_anything_is_read()
    {
        var fixture = new Fixture();

        var created = await fixture.Service.CreateAsync("  ");

        created.FirstError.ResourceKey.Should().Be("SampleStorageLocation_NameRequired");
        fixture.CheckedName.Should().BeNull();
        fixture.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Without_the_settings_right_nothing_is_listed_created_renamed_or_deactivated()
    {
        var place = SampleStorageLocation.Create(Site, "Paillasse").Value;
        var fixture = new Fixture(allowed: false) { Existing = place };

        (await fixture.Service.GetAllAsync()).IsFailure.Should().BeTrue();
        (await fixture.Service.CreateAsync("Frigo")).IsFailure.Should().BeTrue();
        (await fixture.Service.RenameAsync(place.Id, "Frigo")).IsFailure.Should().BeTrue();
        (await fixture.Service.SetActiveAsync(place.Id, false)).IsFailure.Should().BeTrue();

        fixture.RequiredPermissions.Should().OnlyContain(p => p == Permissions.Administration.ManageSettings);
        place.IsActive.Should().BeTrue();
        place.Name.Should().Be("Paillasse");
        fixture.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Renaming_to_a_name_another_place_has_is_refused()
    {
        var place = SampleStorageLocation.Create(Site, "Paillasse").Value;
        var fixture = new Fixture { Existing = place, NameTaken = true };

        var renamed = await fixture.Service.RenameAsync(place.Id, "Frigo R1");

        renamed.FirstError.ResourceKey.Should().Be("SampleStorageLocation_NameTaken");
        place.Name.Should().Be("Paillasse");
        fixture.Saves.Should().Be(0);
    }

    [Fact]
    public async Task A_place_is_renamed_and_the_rename_is_audited()
    {
        var place = SampleStorageLocation.Create(Site, "Paillasse").Value;
        var fixture = new Fixture { Existing = place };

        var renamed = await fixture.Service.RenameAsync(place.Id, "Paillasse hémostase");

        renamed.IsSuccess.Should().BeTrue();
        place.Name.Should().Be("Paillasse hémostase");
        fixture.ExceptId.Should().Be(place.Id, "the place itself does not make its own name taken");
        fixture.Saves.Should().Be(1);
    }

    [Fact]
    public async Task A_place_is_deactivated_then_refused_a_second_deactivation()
    {
        var place = SampleStorageLocation.Create(Site, "Congélateur C1").Value;
        var fixture = new Fixture { Existing = place };

        (await fixture.Service.SetActiveAsync(place.Id, false)).IsSuccess.Should().BeTrue();
        place.IsActive.Should().BeFalse();

        var again = await fixture.Service.SetActiveAsync(place.Id, false);
        again.FirstError.ResourceKey.Should().Be("SampleStorageLocation_AlreadyInactive");
        fixture.Saves.Should().Be(1);

        (await fixture.Service.SetActiveAsync(place.Id, true)).IsSuccess.Should().BeTrue();
        place.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_place_is_not_found()
    {
        var fixture = new Fixture();

        (await fixture.Service.RenameAsync(Guid.NewGuid(), "Frigo")).FirstError.ResourceKey
            .Should().Be("SampleStorageLocation_NotFound");
        (await fixture.Service.SetActiveAsync(Guid.NewGuid(), false)).FirstError.ResourceKey
            .Should().Be("SampleStorageLocation_NotFound");
    }

    private sealed class Fixture
    {
        public List<SampleStorageLocation> Added { get; } = [];
        public List<string> RequiredPermissions { get; } = [];
        public SampleStorageLocation? Existing { get; init; }
        public bool NameTaken { get; init; }
        public string? CheckedName { get; private set; }
        public Guid? ExceptId { get; private set; }
        public int Saves { get; private set; }

        public SampleStorageLocationService Service { get; }

        public Fixture(bool allowed = true)
        {
            var guard = Stub<IPermissionGuard>.Create(new()
            {
                [nameof(IPermissionGuard.Require)] = a =>
                {
                    RequiredPermissions.Add((string)a[0]!);
                    return allowed ? Result.Success() : Result.Failure(Error.Forbidden("Forbidden"));
                }
            });

            var places = Stub<IRepository<SampleStorageLocation>>.Create(new()
            {
                [nameof(IRepository<SampleStorageLocation>.GetByIdAsync)] = a =>
                    Task.FromResult(Existing is { } e && e.Id == (Guid)a[0]! ? e : null),
                [nameof(IRepository<SampleStorageLocation>.AddAsync)] = a =>
                {
                    Added.Add((SampleStorageLocation)a[0]!);
                    return Task.CompletedTask;
                }
            });

            var queries = Stub<ISampleStorageLocationQueries>.Create(new()
            {
                [nameof(ISampleStorageLocationQueries.NameExistsAsync)] = a =>
                {
                    CheckedName = (string)a[1]!;
                    ExceptId = (Guid)a[2]!;
                    return Task.FromResult(NameTaken);
                },
                [nameof(ISampleStorageLocationQueries.GetStorageLocationsAsync)] = _ =>
                    Task.FromResult<IReadOnlyList<SampleStorageLocationRow>>([])
            });

            var unitOfWork = Stub<IUnitOfWork>.Create(new()
            {
                [nameof(IUnitOfWork.SaveChangesAsync)] = _ =>
                {
                    Saves++;
                    return Task.FromResult(1);
                }
            });

            var user = Stub<ICurrentUser>.Create(new()
            {
                ["get_" + nameof(ICurrentUser.SiteId)] = _ => Site,
                ["get_" + nameof(ICurrentUser.UserId)] = _ => Guid.NewGuid()
            });

            Service = new SampleStorageLocationService(places, queries, unitOfWork, guard, user);
        }
    }
}
