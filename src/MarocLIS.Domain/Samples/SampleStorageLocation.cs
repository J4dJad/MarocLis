using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Samples;

/// <summary>
/// A place of the site where tubes are kept (« lieu de stockage ») — a fridge, a freezer, a bench —
/// from the list the administrator keeps in the settings.
/// <para>
/// The tube keeps the place's name as text (<see cref="Sample.StorageLocation"/>), not a reference:
/// what was written at the time stays true of the tube, so renaming a place or taking it out of use
/// never rewrites where a stored tube was recorded. The list exists so the same fridge is written
/// the same way every time, and can be found again when an add-on request arrives.
/// </para>
/// </summary>
public sealed class SampleStorageLocation : AuditableAggregateRoot, ISiteScoped
{
    /// <summary>The length of <see cref="Sample.StorageLocation"/>, where the name is copied.</summary>
    public const int NameMaxLength = 100;

    private SampleStorageLocation() { }

    private SampleStorageLocation(Guid siteId, string name)
    {
        SiteId = siteId;
        Name = name;
        IsActive = true;
    }

    public Guid SiteId { get; set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Offered at reception and in the serum bank only while active. A place taken out of use stays
    /// on the tubes recorded there.
    /// </summary>
    public bool IsActive { get; private set; }

    public static Result<SampleStorageLocation> Create(Guid siteId, string? name)
    {
        var valid = ValidateName(name);
        if (valid.IsFailure) return Result.Failure<SampleStorageLocation>(valid.FirstError);

        return Result.Success(new SampleStorageLocation(siteId, Normalize(name)));
    }

    public Result Rename(string? name)
    {
        var valid = ValidateName(name);
        if (valid.IsFailure) return valid;

        Name = Normalize(name);
        return Result.Success();
    }

    public Result Deactivate()
    {
        if (!IsActive) return Result.Failure(Error.Conflict("SampleStorageLocation_AlreadyInactive", Name));

        IsActive = false;
        return Result.Success();
    }

    public Result Reactivate()
    {
        if (IsActive) return Result.Failure(Error.Conflict("SampleStorageLocation_AlreadyActive", Name));

        IsActive = true;
        return Result.Success();
    }

    public static Result ValidateName(string? name)
    {
        var normalised = Normalize(name);

        if (normalised.Length == 0)
            return Result.Failure(Error.Validation("SampleStorageLocation_NameRequired"));

        if (normalised.Length > NameMaxLength)
            return Result.Failure(Error.Validation("SampleStorageLocation_NameTooLong", NameMaxLength));

        return Result.Success();
    }

    /// <summary>Trimmed: a trailing space would otherwise make a second « Frigo R1 ».</summary>
    public static string Normalize(string? name) => (name ?? string.Empty).Trim();
}
