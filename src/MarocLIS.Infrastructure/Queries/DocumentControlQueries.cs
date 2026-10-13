using MarocLIS.Application.Quality;
using MarocLIS.Domain.Quality;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class DocumentControlQueries(LisDbContext db) : IDocumentControlQueries
{
    public async Task<IReadOnlyList<ControlledDocumentRow>> GetDocumentsAsync(
        Guid siteId, Guid currentUserId, bool includeInactive, DateOnly today,
        CancellationToken ct = default)
    {
        // The file content is never part of this projection: a register of fifty procedures would
        // otherwise pull every PDF across the network to draw one grid.
        var rows = await db.ControlledDocuments.AsNoTracking()
            .Where(d => d.SiteId == siteId && (includeInactive || d.IsActive))
            .OrderBy(d => d.Reference)
            .Select(d => new
            {
                d.Id,
                d.Reference,
                d.Title,
                d.Kind,
                d.ProcessCode,
                OwnerName = db.Users.Where(u => u.Id == d.OwnerUserId)
                                    .Select(u => u.FullName).FirstOrDefault(),
                d.ReviewPeriodMonths,
                d.RequiresAcknowledgement,
                d.IsActive,
                Versions = d.Versions.Select(v => new { v.VersionNumber, v.Status, v.EffectiveFrom })
                                     .ToList(),
                Acknowledged = d.Acknowledgements.Where(a => a.UserId == currentUserId)
                                                 .Select(a => a.VersionNumber).ToList()
            })
            .ToListAsync(ct);

        return rows.Select(d =>
        {
            var effective = d.Versions.Where(v => v.Status == DocumentVersionStatus.Effective)
                                      .MaxBy(v => v.VersionNumber);
            var latest = d.Versions.MaxBy(v => v.VersionNumber);
            DateOnly? nextReview = effective?.EffectiveFrom.AddMonths(d.ReviewPeriodMonths);

            return new ControlledDocumentRow(
                d.Id, d.Reference, d.Title, d.Kind, d.ProcessCode, d.OwnerName,
                d.ReviewPeriodMonths, d.RequiresAcknowledgement, d.IsActive,
                effective?.VersionNumber, effective?.EffectiveFrom, nextReview,
                nextReview is { } due && due < today,
                latest?.VersionNumber, latest?.Status,
                effective is not null && d.Acknowledged.Contains(effective.VersionNumber));
        }).ToList();
    }

    public async Task<IReadOnlyList<DocumentVersionRow>> GetVersionsAsync(
        Guid documentId, CancellationToken ct = default)
    {
        var rows = await db.DocumentVersions.AsNoTracking()
            .Where(v => v.ControlledDocumentId == documentId)
            .OrderBy(v => v.VersionNumber)
            .Select(v => new
            {
                v.Id,
                v.VersionNumber,
                v.Status,
                v.ChangeSummary,
                v.FileName,
                AuthorName = db.Users.Where(u => u.Id == v.AuthorUserId)
                                     .Select(u => u.FullName).FirstOrDefault(),
                v.CreatedAt,
                ReviewedByName = db.Users.Where(u => u.Id == v.ReviewedByUserId)
                                         .Select(u => u.FullName).FirstOrDefault(),
                v.ReviewedAt,
                ApprovedByName = db.Users.Where(u => u.Id == v.ApprovedByUserId)
                                         .Select(u => u.FullName).FirstOrDefault(),
                v.ApprovedAt,
                v.EffectiveFrom,
                v.WithdrawnOn,
                HasContent = v.Content != null
            })
            .ToListAsync(ct);

        return rows.Select(v => new DocumentVersionRow(
            v.Id, v.VersionNumber, v.Status, v.ChangeSummary, v.FileName, v.AuthorName ?? "?",
            v.CreatedAt, v.ReviewedByName, v.ReviewedAt, v.ApprovedByName, v.ApprovedAt,
            // EffectiveFrom is only meaningful once a version has been brought into force.
            v.Status is DocumentVersionStatus.Effective or DocumentVersionStatus.Superseded
                ? v.EffectiveFrom : null,
            v.WithdrawnOn, v.HasContent)).ToList();
    }

    public async Task<DocumentContent?> GetContentAsync(Guid documentId, int versionNumber,
                                                        CancellationToken ct = default)
    {
        var row = await db.DocumentVersions.AsNoTracking()
            .Where(v => v.ControlledDocumentId == documentId && v.VersionNumber == versionNumber
                        && v.Content != null)
            .Select(v => new { v.Content, v.FileName, v.ContentType })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? null
            : new DocumentContent(row.Content!, row.FileName ?? $"document-v{versionNumber}",
                                  row.ContentType);
    }

    public Task<bool> ReferenceExistsAsync(string reference, CancellationToken ct = default) =>
        db.ControlledDocuments.IgnoreQueryFilters().AnyAsync(d => d.Reference == reference, ct);
}
