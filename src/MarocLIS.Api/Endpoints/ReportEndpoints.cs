using MarocLIS.Api.Contracts;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Reports;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Reports;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Net.Http.Headers;

namespace MarocLIS.Api.Endpoints;

/// <summary>
/// Issuing the report of a validated file, and looking at what was issued.
/// <para>
/// The phone never receives the PDF. A report downloaded to a phone is a patient document in the
/// phone's download folder, its photo backup and whatever app it was shared to — outside anything
/// the laboratory can account for under loi 08-09. The biologist sees the pages as images instead,
/// drawn from the same document at the moment of release, held a few minutes in the server's memory
/// for the biologist who issued it, and sent with no-store so the browser keeps no copy.
/// </para>
/// </summary>
public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/dossiers/{orderId:guid}/compte-rendu", ReleaseAsync).RequireAuthorization();
        app.MapGet("/api/comptes-rendus/{reportId:guid}/apercu", PreviewAsync).RequireAuthorization();
    }

    /// <summary>
    /// Issues the report as « Compte rendu » does at the workstation — partial while results are
    /// awaited, signed by the biologist who validated. The confirmation is the phone's to ask.
    /// </summary>
    private static async Task<IResult> ReleaseAsync(
        Guid orderId, HttpContext http, ReportReleaseService reports, ICurrentUser user,
        ReportPreviews previews, IReportQueries queries, CancellationToken ct)
    {
        var released = await reports.ReleaseAsync(orderId, renderPageImages: true, ct: ct);
        if (released.IsFailure) return ApiProblems.From(http, released);

        var report = released.Value;
        previews.Keep(user.UserId, orderId, report);

        // The time and the signatory as the report states them, for « Émis et signé par … à … ».
        var issued = await queries.GetLatestVersionAsync(orderId, ct);

        return Results.Ok(new ReleasedReportResponse(
            report.ReportId, report.VersionNumber, report.Completeness == ReportCompleteness.Partial,
            report.PageImages.Count, issued?.ReleasedAt, issued?.SignatoryName ?? user.FullName));
    }

    /// <summary>
    /// One page of a report just issued, as a PNG to show inline. Each look is a consultation of a
    /// patient document and is logged as such, against the phone.
    /// </summary>
    private static async Task<IResult> PreviewAsync(
        Guid reportId, HttpContext http, ICurrentUser user, ReportPreviews previews,
        IDataAccessAuditor accessAuditor, int? page, CancellationToken ct)
    {
        if (previews.Find(user.UserId, reportId) is not { } preview)
            return ApiProblems.From(http, Error.NotFound("Mobile_PreviewUnavailable"));

        var number = page ?? 1;
        if (number < 1 || number > preview.Pages.Count)
            return ApiProblems.From(http, Error.NotFound("Mobile_PreviewPageNotFound", number, preview.Pages.Count));

        await accessAuditor.LogAsync(DataAccessKind.ViewReport, null, preview.OrderId,
                                     "ReportPreviewMobile", 1, $"Page {number}/{preview.Pages.Count}", ct);

        http.Response.Headers[ApiProtocol.PageCountHeader] = preview.Pages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers[HeaderNames.ContentDisposition] =
            $"inline; filename=\"compte-rendu-v{preview.Version}-p{number}.png\"";

        return Results.Bytes(preview.Pages[number - 1], "image/png");
    }
}

/// <summary>A report's preview pages, kept for the biologist who issued it.</summary>
public sealed record ReportPreview(Guid OrderId, int Version, IReadOnlyList<byte[]> Pages);

/// <summary>
/// The preview pages of the reports issued from phones, in memory for the idle timeout and no
/// longer. Keyed by the biologist as well as the report: another account, even one allowed to
/// reprint, does not reach them through here. A report issued elsewhere, or longer ago, has no
/// preview — the workstation's report register is where it is read again.
/// </summary>
public sealed class ReportPreviews(IMemoryCache cache, PasswordPolicy policy)
{
    public void Keep(Guid userId, Guid orderId, ReleasedReport report) =>
        cache.Set(Key(userId, report.ReportId),
                  new ReportPreview(orderId, report.VersionNumber, report.PageImages),
                  new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = policy.IdleTimeout });

    public ReportPreview? Find(Guid userId, Guid reportId) =>
        cache.TryGetValue(Key(userId, reportId), out ReportPreview? preview) ? preview : null;

    private static string Key(Guid userId, Guid reportId) => $"preview:{userId:N}:{reportId:N}";
}
