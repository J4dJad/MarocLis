using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Sequencing;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Sequencing;

/// <summary>
/// Allocates gapless document numbers.
/// <para>
/// The row holding the counter is read with <c>UPDLOCK, HOLDLOCK</c>, so a second workstation
/// asking for the same sequence blocks until the first has committed. That is slower than a
/// database sequence and deliberately so: Moroccan tax law requires the invoice series to be
/// chronological and unbroken, and both <c>IDENTITY</c> and <c>CREATE SEQUENCE</c> consume values
/// on a rolled-back transaction — leaving holes that an inspection will ask about and the
/// laboratory cannot explain.
/// </para>
/// <para>
/// The caller must already be inside a transaction. Allocating outside one would commit the
/// increment independently of the document, which reintroduces the very gap this avoids.
/// </para>
/// </summary>
public sealed class SequenceAllocator(LisDbContext context, IClock clock, ICurrentUser? currentUser)
    : ISequenceAllocator
{
    public async Task<Result<string>> NextAsync(SequencePurpose purpose, Guid? siteId,
                                                int? year = null, CancellationToken ct = default)
    {
        if (context.Database.CurrentTransaction is null)
            return Result.Failure<string>(Error.Conflict("Sequence_RequiresTransaction", purpose));

        var effectiveYear = year ?? ResolveYear(purpose, clock.Today);

        var sequence = await LoadLockedAsync(purpose, siteId, effectiveYear, ct);

        if (sequence is null)
        {
            // First document of the period. Created here rather than by a migration, because a
            // laboratory that opens a new site or rolls into a new year should not need one.
            sequence = new NumberSequence(purpose, siteId, effectiveYear, DefaultFormat(purpose));
            context.NumberSequences.Add(sequence);

            try
            {
                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Another workstation created it first; take theirs under the lock.
                context.Entry(sequence).State = EntityState.Detached;
                sequence = await LoadLockedAsync(purpose, siteId, effectiveYear, ct);

                if (sequence is null)
                    return Result.Failure<string>(Error.Conflict("Sequence_NotConfigured", purpose));
            }
        }

        var siteCode = await ResolveSiteCodeAsync(siteId, ct);
        var allocated = sequence.Allocate(clock.Now, siteCode);

        if (allocated.IsFailure) return allocated;

        await context.SaveChangesAsync(ct);
        return allocated;
    }

    public async Task<string?> PeekAsync(SequencePurpose purpose, Guid? siteId,
                                         int? year = null, CancellationToken ct = default)
    {
        var effectiveYear = year ?? ResolveYear(purpose, clock.Today);

        var sequence = await context.NumberSequences
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Purpose == purpose && s.SiteId == siteId && s.Year == effectiveYear, ct);

        if (sequence is null) return null;

        var siteCode = await ResolveSiteCodeAsync(siteId, ct);
        return sequence.Render(sequence.NextValue, clock.Now, siteCode);
    }

    /// <summary>
    /// Reads the counter row under a pessimistic lock held to the end of the transaction. Raw SQL
    /// because the lock hints are the point of the query and EF has no expression for them.
    /// <para>
    /// The row read under the lock must also be the one used. A screen keeps its context open for
    /// hours, and a tracking query hands back the instance already tracked, with the counter as
    /// it was when this context last allocated: another workstation's numbers since then, or a
    /// rolled-back increment, are not seen, and the number is reused or skipped. Any tracked copy
    /// is therefore forgotten first, so the values come from the locked row.
    /// </para>
    /// </summary>
    private async Task<NumberSequence?> LoadLockedAsync(SequencePurpose purpose, Guid? siteId,
                                                        int? year, CancellationToken ct)
    {
        foreach (var stale in context.ChangeTracker.Entries<NumberSequence>()
                                     .Where(e => e.State != EntityState.Added && e.Entity.Purpose == purpose
                                                 && e.Entity.SiteId == siteId && e.Entity.Year == year)
                                     .ToList())
            stale.State = EntityState.Detached;

        // Two statements rather than one parameterised predicate: SQL Server cannot use the unique
        // index for an `IS NULL` comparison written as `= @p`, and the installation-wide sequences
        // (site-less, year-less) must hit the index too.
        var rows = siteId is { } site
            ? await context.NumberSequences
                .FromSqlInterpolated($"""
                    SELECT * FROM [cfg].[NumberSequences] WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                    WHERE [Purpose] = {(int)purpose} AND [SiteId] = {site}
                      AND (([Year] IS NULL AND {year} IS NULL) OR [Year] = {year})
                    """)
                .ToListAsync(ct)
            : await context.NumberSequences
                .FromSqlInterpolated($"""
                    SELECT * FROM [cfg].[NumberSequences] WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                    WHERE [Purpose] = {(int)purpose} AND [SiteId] IS NULL
                      AND (([Year] IS NULL AND {year} IS NULL) OR [Year] = {year})
                    """)
                .ToListAsync(ct);

        return rows.FirstOrDefault();
    }

    private async Task<string?> ResolveSiteCodeAsync(Guid? siteId, CancellationToken ct)
    {
        if (siteId is null) return null;

        if (currentUser is { IsAuthenticated: true } user && user.SiteId == siteId)
            return user.SiteCode;

        return await context.Sites
            .AsNoTracking()
            .Where(s => s.Id == siteId)
            .Select(s => s.Code)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Whether the sequence resets each year. Accounting series do, so the year appears in the
    /// number and the previous year's series can be closed when its accounts are filed. Barcodes
    /// do not, because a reused barcode is never acceptable.
    /// </summary>
    private static int? ResolveYear(SequencePurpose purpose, DateOnly today) => purpose switch
    {
        SequencePurpose.SampleBarcode => null,
        SequencePurpose.MedicalRecord => null,
        SequencePurpose.ControlledDocument => null,
        SequencePurpose.EmployeeNumber => null,
        _ => today.Year
    };

    private static string DefaultFormat(SequencePurpose purpose) => purpose switch
    {
        SequencePurpose.Accession => "{YY}{MM}-{SEQ:6}",
        SequencePurpose.MedicalRecord => "P-{SEQ:8}",
        SequencePurpose.SampleBarcode => "{SEQ:10}",
        SequencePurpose.Invoice => "FAC/{SITE}/{YEAR}/{SEQ:6}",
        SequencePurpose.CreditNote => "AV/{SITE}/{YEAR}/{SEQ:6}",
        SequencePurpose.Receipt => "REC/{SITE}/{YEAR}/{SEQ:6}",
        SequencePurpose.Quotation => "DEV/{SITE}/{YEAR}/{SEQ:5}",
        SequencePurpose.NonConformity => "NC-{YEAR}-{SEQ:4}",
        SequencePurpose.PurchaseOrder => "BC/{YEAR}/{SEQ:5}",
        SequencePurpose.ClaimBatch => "TP/{SITE}/{YEAR}/{SEQ:5}",
        SequencePurpose.ControlledDocument => "DOC-{SEQ:5}",
        SequencePurpose.TransportRun => "TR/{YEAR}/{SEQ:5}",
        SequencePurpose.SubcontractDispatch => "ST/{YEAR}/{SEQ:5}",
        SequencePurpose.EmployeeNumber => "M{SEQ:4}",
        SequencePurpose.WorkAccident => "AT-{YEAR}-{SEQ:4}",
        _ => "{SEQ:6}"
    };
}
