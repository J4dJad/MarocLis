using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Samples;

namespace MarocLIS.Application.Results;

/// <summary>What the technician asks for: one discipline or all of them, one analyser or none.</summary>
public sealed record BenchWorksheetRequest(Guid? DepartmentId, Guid? InstrumentId, bool OnlyUrgent);

/// <summary>
/// The fiche de paillasse: the work waiting at one bench, printed to be carried to it.
/// <para>
/// A snapshot, and it says so — the time it was printed and by whom are part of the document. A
/// sheet on a bench is read for hours after it leaves the printer, and a technician holding two of
/// them needs to know which one is current without walking back to a screen.
/// </para>
/// </summary>
public sealed record BenchWorksheet
{
    public required BenchLaboratory Laboratory { get; init; }

    /// <summary>Null when the sheet covers every discipline.</summary>
    public string? DepartmentCode { get; init; }
    public MultilingualText? DepartmentName { get; init; }

    /// <summary>Null when the sheet is not restricted to one analyser.</summary>
    public string? InstrumentCode { get; init; }
    public MultilingualText? InstrumentName { get; init; }

    public bool OnlyUrgent { get; init; }

    public required DateTimeOffset PrintedAt { get; init; }
    public required string PrintedBy { get; init; }

    /// <summary>Urgent first, then in order of reception — the order the bench works in.</summary>
    public required IReadOnlyList<BenchWorksheetLine> Lines { get; init; }

    /// <summary>
    /// Set when the pending work exceeded what one sheet carries. Printed on the sheet, because a
    /// list that silently stops is read as "that is everything".
    /// </summary>
    public bool IsTruncated { get; init; }

    public int UrgentCount => Lines.Count(l => l.IsUrgent);
}

/// <summary>The laboratory's letterhead, as the result report and the invoice print it.</summary>
public sealed record BenchLaboratory(
    MultilingualText Name,
    string? LegalName,
    MultilingualText? Address,
    string? City,
    string? Phone,
    byte[]? LogoPng);

/// <summary>One analysis on one tube, waiting for its result.</summary>
public sealed record BenchWorksheetLine
{
    public required Guid LabOrderItemId { get; init; }
    public required string AccessionNumber { get; init; }
    public required string SampleBarcode { get; init; }

    public required string PatientName { get; init; }
    public string? PatientNameArabic { get; init; }
    public Gender Gender { get; init; }
    public int? AgeYears { get; init; }

    public OrderPriority Priority { get; init; }

    public required string DepartmentCode { get; init; }
    public required string AnalysisCode { get; init; }
    public required MultilingualText AnalysisName { get; init; }

    public DateTimeOffset? ReceivedAt { get; init; }
    public DateTimeOffset? DueAt { get; init; }

    /// <summary>Defects the tube was accepted with — the technician has to see them before measuring.</summary>
    public SampleDefect SampleDefects { get; init; }

    /// <summary>The parameters still awaiting a value, in the catalogue's display order.</summary>
    public required IReadOnlyList<BenchWorksheetParameter> Parameters { get; init; }

    public bool IsUrgent => Priority != OrderPriority.Routine;

    public bool IsOverdue(DateTimeOffset at) => DueAt is { } due && at > due;
}

/// <summary>A value the technician has to write down. Calculated parameters are never listed: nobody measures them.</summary>
public sealed record BenchWorksheetParameter(
    string Code,
    MultilingualText Name,
    string? UnitSymbol,
    ResultValueType ValueType);

/// <summary>The site, discipline and analyser a sheet is printed for, resolved to their names.</summary>
public sealed record BenchWorksheetContext(
    BenchLaboratory Laboratory,
    string? DepartmentCode,
    MultilingualText? DepartmentName,
    string? InstrumentCode,
    MultilingualText? InstrumentName);

/// <summary>The choices offered before printing.</summary>
public sealed record BenchWorksheetOptions(
    IReadOnlyList<BenchDepartmentOption> Departments,
    IReadOnlyList<BenchInstrumentOption> Instruments);

/// <summary>A discipline, with the number of analyses waiting in it so an empty bench is visible before printing.</summary>
/// <summary>A dossier with results still to enter, for the entry screen's worklist.</summary>
public sealed record PendingEntryOrder(
    string AccessionNumber,
    string PatientName,
    OrderPriority Priority,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? DueAt,
    int PendingAnalyses,
    string Departments)
{
    public bool IsUrgent => Priority != OrderPriority.Routine;
}

public sealed record BenchDepartmentOption(Guid Id, string Code, MultilingualText Name, int PendingCount);

/// <summary>An analyser, with the discipline it belongs to so the choice can follow the department.</summary>
public sealed record BenchInstrumentOption(Guid Id, string Code, MultilingualText Name, Guid? DepartmentId);

/// <summary>
/// Prints the fiche de paillasse.
/// <para>
/// The bench still works on paper. An analyser's run list, a manual technique timed with a
/// stopwatch, a reading under the microscope — the technician writes the value next to the tube
/// and keys it in afterwards. Without a sheet produced by the system they copy the list by hand
/// from the screen, and a hand-copied accession number is how a value lands in the wrong file.
/// </para>
/// <para>
/// Guarded by <see cref="Permissions.Results.Enter"/> rather than by the read permission: the sheet
/// is a tool for the people who produce results, and it is a list of patients — a reception desk
/// with read access to results has no reason to print the whole bench's.
/// </para>
/// </summary>
public sealed class BenchWorksheetService(
    IBenchWorksheetQueries queries,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IDataAccessAuditor accessAuditor,
    IClock clock)
{
    /// <summary>
    /// Parameter rows read for one sheet. Several hundred analyses is a full day's backlog on a busy
    /// bench; past that, the sheet is no longer a bench document and says it was cut short.
    /// </summary>
    private const int MaximumParameterRows = 3000;

    public async Task<Result<BenchWorksheetOptions>> GetOptionsAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Results.Enter);
        if (permitted.IsFailure) return Result.Failure<BenchWorksheetOptions>(permitted.FirstError);

        return Result.Success(await queries.GetOptionsAsync(currentUser.SiteId, ct));
    }

    /// <summary>
    /// The dossiers with results to enter, one row each, in the order the bench works: urgent
    /// first, then by reception. Same selection as the bench sheet, so the screen and the paper
    /// never disagree about what is outstanding.
    /// </summary>
    public async Task<Result<IReadOnlyList<PendingEntryOrder>>> GetPendingOrdersAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Results.Enter);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<PendingEntryOrder>>(permitted.FirstError);

        var (lines, _) = await queries.GetPendingWorkAsync(
            currentUser.SiteId, null, null, false, clock.Today, MaximumParameterRows, ct,
            includeEntered: true);

        IReadOnlyList<PendingEntryOrder> orders =
        [
            .. lines.GroupBy(l => l.AccessionNumber)
                .Select(g =>
                {
                    var first = g.First();
                    return new PendingEntryOrder(
                        first.AccessionNumber,
                        first.PatientName,
                        g.Max(l => l.Priority),
                        g.Min(l => l.ReceivedAt),
                        g.Min(l => l.DueAt),
                        g.Count(),
                        string.Join(" ", g.Select(l => l.DepartmentCode).Distinct().Order()));
                })
        ];

        return Result.Success(orders);
    }

    public async Task<Result<BenchWorksheet>> GetWorksheetAsync(BenchWorksheetRequest request,
                                                                CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var permitted = guard.Require(Permissions.Results.Enter);
        if (permitted.IsFailure) return Result.Failure<BenchWorksheet>(permitted.FirstError);

        var context = await queries.GetContextAsync(currentUser.SiteId, request.DepartmentId,
                                                    request.InstrumentId, ct);

        if (context is null) return Result.Failure<BenchWorksheet>(Error.NotFound("Site_NotFound"));

        // A department or analyser that did not resolve would otherwise print as "all of them" — the
        // one misreading of a bench sheet that sends a technician to do someone else's work.
        if (request.DepartmentId is not null && context.DepartmentName is null)
            return Result.Failure<BenchWorksheet>(Error.NotFound("BenchSheet_DepartmentNotFound"));

        if (request.InstrumentId is not null && context.InstrumentName is null)
            return Result.Failure<BenchWorksheet>(Error.NotFound("BenchSheet_InstrumentNotFound"));

        var (lines, truncated) = await queries.GetPendingWorkAsync(
            currentUser.SiteId, request.DepartmentId, request.InstrumentId, request.OnlyUrgent,
            clock.Today, MaximumParameterRows, ct);

        if (lines.Count == 0)
            return Result.Failure<BenchWorksheet>(Error.NotFound("BenchSheet_NothingPending"));

        // Printing a list of patients is an access to their data like opening one file is, and the
        // register has to be able to say who carried which list out of the screen.
        await accessAuditor.LogAsync(
            DataAccessKind.Print, recordCount: lines.Count,
            queryDescription: $"Fiche de paillasse — {context.DepartmentCode ?? "tous secteurs"}" +
                              (context.InstrumentCode is { } instrument ? $" / {instrument}" : string.Empty),
            ct: ct);

        return Result.Success(new BenchWorksheet
        {
            Laboratory = context.Laboratory,
            DepartmentCode = context.DepartmentCode,
            DepartmentName = context.DepartmentName,
            InstrumentCode = context.InstrumentCode,
            InstrumentName = context.InstrumentName,
            OnlyUrgent = request.OnlyUrgent,
            PrintedAt = clock.Now,
            PrintedBy = currentUser.FullName,
            Lines = lines,
            IsTruncated = truncated
        });
    }
}

/// <summary>
/// Read side of the bench worksheet: crosses orders, samples, results and the catalogue, and is
/// shaped for one printout, so it is a query rather than a repository method.
/// </summary>
public interface IBenchWorksheetQueries
{
    Task<BenchWorksheetOptions> GetOptionsAsync(Guid siteId, CancellationToken ct = default);

    /// <summary>The letterhead and the names of the chosen department and analyser; null when the site does not exist.</summary>
    Task<BenchWorksheetContext?> GetContextAsync(Guid siteId, Guid? departmentId, Guid? instrumentId,
                                                 CancellationToken ct = default);

    /// <summary>
    /// Analyses whose tube is in the laboratory and whose results are not yet entered, urgent first
    /// then oldest received. <c>Truncated</c> is true when more than <paramref name="maximumRows"/>
    /// parameter rows were waiting. With <paramref name="includeEntered"/>, entered values still
    /// awaiting technical validation count as waiting too (the entry screen's list).
    /// </summary>
    Task<(IReadOnlyList<BenchWorksheetLine> Lines, bool Truncated)> GetPendingWorkAsync(
        Guid siteId, Guid? departmentId, Guid? instrumentId, bool onlyUrgent, DateOnly today,
        int maximumRows, CancellationToken ct = default, bool includeEntered = false);
}

/// <summary>Turns a <see cref="BenchWorksheet"/> into a printable PDF.</summary>
public interface IBenchWorksheetRenderer
{
    byte[] Render(BenchWorksheet worksheet, AppLanguage language);
}
