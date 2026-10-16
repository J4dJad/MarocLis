using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Results;

/// <summary>
/// How far a dossier's entry has gone: the values keyed (or recorded as not done) against the
/// values the dossier owes. Calculated parameters and section headings are not counted — nobody
/// types them — and neither are cancelled analyses.
/// </summary>
public sealed record ResultEntryProgress(string AccessionNumber, int Entered, int Total);

/// <summary>A discipline as the entry screen's sector filter names it.</summary>
public sealed record ResultEntrySector(string Code, MultilingualText Name);

/// <summary>
/// Where one result belongs and where it came from: the discipline its analysis is run in, for the
/// section rows of the entry grid, and the analyser that sent it — null for a value keyed by hand.
/// </summary>
public sealed record ResultEntryOrigin(
    Guid TestResultId,
    string? DepartmentCode,
    MultilingualText? DepartmentName,
    string? InstrumentCode,
    MultilingualText? InstrumentName);

/// <summary>
/// The context the entry screen shows around the values: progress per dossier in the worklist,
/// the names of the sectors to filter it by, and the sector and origin of each line of an open
/// dossier. Read beside <see cref="ResultEntryService.OpenAsync"/> rather than inside it, so the
/// entry pipeline itself does not change.
/// </summary>
public sealed class ResultEntryContextService(
    IResultEntryContextQueries queries,
    IPermissionGuard guard,
    ICurrentUser currentUser)
{
    /// <summary>
    /// Entry progress of the given dossiers on the user's site. Guarded like the worklist it
    /// completes: the people who enter results.
    /// </summary>
    public async Task<Result<IReadOnlyList<ResultEntryProgress>>> GetProgressAsync(
        IReadOnlyCollection<string> accessionNumbers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(accessionNumbers);

        var permitted = guard.Require(Permissions.Results.Enter);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<ResultEntryProgress>>(permitted.FirstError);

        if (accessionNumbers.Count == 0) return Result.Success<IReadOnlyList<ResultEntryProgress>>([]);

        return Result.Success(await queries.GetProgressAsync(currentUser.SiteId, accessionNumbers, ct));
    }

    /// <summary>The names of the given department codes, in the catalogue's display order.</summary>
    public async Task<Result<IReadOnlyList<ResultEntrySector>>> GetSectorsAsync(
        IReadOnlyCollection<string> departmentCodes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(departmentCodes);

        var permitted = guard.Require(Permissions.Results.Enter);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<ResultEntrySector>>(permitted.FirstError);

        if (departmentCodes.Count == 0) return Result.Success<IReadOnlyList<ResultEntrySector>>([]);

        return Result.Success(await queries.GetSectorsAsync(departmentCodes, ct));
    }

    /// <summary>
    /// The sector and origin of each result of an order. Guarded like opening the order for entry,
    /// which already shows the values themselves.
    /// </summary>
    public async Task<Result<IReadOnlyList<ResultEntryOrigin>>> GetOriginsAsync(
        Guid orderId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Results.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<ResultEntryOrigin>>(permitted.FirstError);

        return Result.Success(await queries.GetOriginsAsync(orderId, ct));
    }
}

/// <summary>Read side of <see cref="ResultEntryContextService"/>.</summary>
public interface IResultEntryContextQueries
{
    /// <summary>
    /// For each accession number of the site that matches an order, the values entered and owed.
    /// A dossier that is not found is absent.
    /// </summary>
    Task<IReadOnlyList<ResultEntryProgress>> GetProgressAsync(
        Guid siteId, IReadOnlyCollection<string> accessionNumbers, CancellationToken ct = default);

    /// <summary>The departments with the given codes; an unknown code is absent.</summary>
    Task<IReadOnlyList<ResultEntrySector>> GetSectorsAsync(
        IReadOnlyCollection<string> departmentCodes, CancellationToken ct = default);

    /// <summary>Every result of the order with its department and analyser.</summary>
    Task<IReadOnlyList<ResultEntryOrigin>> GetOriginsAsync(Guid orderId, CancellationToken ct = default);
}
