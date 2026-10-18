using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Samples;

namespace MarocLIS.Application.Samples;

/// <summary>One place of the site's storage list, as the settings show it.</summary>
/// <param name="StoredSampleCount">Tubes of the site recorded there and not discarded.</param>
public sealed record SampleStorageLocationRow(Guid Id, string Name, bool IsActive, int StoredSampleCount);

/// <summary>
/// The site's list of sample storage places (« Paramètres › Lieux de stockage »).
/// <para>
/// Reception and the serum bank offer only the active places of this list, so the same fridge is
/// written the same way on every tube. Kept with the settings, under the same right: the list is
/// installation set-up, not bench work. A tube keeps the name it was given, so renaming a place or
/// taking it out of use changes what is offered next, never where a stored tube was recorded.
/// </para>
/// </summary>
public sealed class SampleStorageLocationService(
    IRepository<SampleStorageLocation> places,
    ISampleStorageLocationQueries queries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    ILicenceState? licence = null)
{
    /// <summary>Every place of the operator's site, active or not, with the tubes recorded there.</summary>
    public async Task<Result<IReadOnlyList<SampleStorageLocationRow>>> GetAllAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageSettings);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<SampleStorageLocationRow>>(permitted.FirstError);

        return Result.Success(await queries.GetStorageLocationsAsync(currentUser.SiteId, includeInactive: true, ct));
    }

    public async Task<Result<Guid>> CreateAsync(string? name, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageSettings);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var licensed = licence.RequireSettingsChange();
        if (licensed.IsFailure) return Result.Failure<Guid>(licensed.FirstError);

        var created = SampleStorageLocation.Create(currentUser.SiteId, name);
        if (created.IsFailure) return Result.Failure<Guid>(created.FirstError);

        var place = created.Value;

        if (await queries.NameExistsAsync(currentUser.SiteId, place.Name, Guid.Empty, ct))
            return Result.Failure<Guid>(Error.Conflict("SampleStorageLocation_NameTaken", place.Name));

        await places.AddAsync(place, ct);

        unitOfWork.AuditReason = $"Création du lieu de stockage « {place.Name} »";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(place.Id);
    }

    /// <summary>Renames a place. The tubes already recorded there keep the old name.</summary>
    public async Task<Result> RenameAsync(Guid id, string? name, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageSettings);
        if (permitted.IsFailure) return permitted;

        var licensed = licence.RequireSettingsChange();
        if (licensed.IsFailure) return licensed;

        var valid = SampleStorageLocation.ValidateName(name);
        if (valid.IsFailure) return valid;

        var place = await places.GetByIdAsync(id, ct);
        if (place is null) return Result.Failure(Error.NotFound("SampleStorageLocation_NotFound"));

        var normalised = SampleStorageLocation.Normalize(name);
        if (normalised == place.Name) return Result.Success();

        if (await queries.NameExistsAsync(place.SiteId, normalised, place.Id, ct))
            return Result.Failure(Error.Conflict("SampleStorageLocation_NameTaken", normalised));

        var previous = place.Name;
        var renamed = place.Rename(normalised);
        if (renamed.IsFailure) return renamed;

        unitOfWork.AuditReason = $"Lieu de stockage « {previous} » renommé « {place.Name} »";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Takes a place out of the lists offered, or puts it back. The tubes recorded there keep it:
    /// a freezer emptied and switched off still has to be named on the tubes that were in it.
    /// </summary>
    public async Task<Result> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageSettings);
        if (permitted.IsFailure) return permitted;

        var licensed = licence.RequireSettingsChange();
        if (licensed.IsFailure) return licensed;

        var place = await places.GetByIdAsync(id, ct);
        if (place is null) return Result.Failure(Error.NotFound("SampleStorageLocation_NotFound"));

        var changed = active ? place.Reactivate() : place.Deactivate();
        if (changed.IsFailure) return changed;

        unitOfWork.AuditReason = $"Lieu de stockage « {place.Name} » {(active ? "réactivé" : "désactivé")}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }
}

/// <summary>Read side of the storage-place list.</summary>
public interface ISampleStorageLocationQueries
{
    /// <summary>The site's places by name; the inactive ones only when asked for.</summary>
    Task<IReadOnlyList<SampleStorageLocationRow>> GetStorageLocationsAsync(
        Guid siteId, bool includeInactive, CancellationToken ct = default);

    /// <summary>Whether another place of the site (active or not) already has this name, case ignored.</summary>
    Task<bool> NameExistsAsync(Guid siteId, string name, Guid exceptId, CancellationToken ct = default);
}
