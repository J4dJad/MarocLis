using MarocLIS.Domain.HumanResources;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

/// <summary>
/// The public holidays between two dates, the civil ones of every year the range touches put in
/// first.
/// <para>
/// The civil holidays were only added when someone opened the holiday screen for the year, so a
/// leave running into January, or January's payroll, counted the 1st and the 11th as working days
/// until then. Every computation that reads holiday dates goes through here instead.
/// </para>
/// <para>
/// The query classes run on a context of their own (see IsolatedQueryProxy), so the missing dates
/// are saved on it without touching a screen's pending changes. When the context is shared — it
/// holds a transaction or tracked changes of a use case — nothing is written: saving there would
/// carry the use case's changes along, and the dates are read as they stand.
/// </para>
/// </summary>
public sealed class HolidayCalendar(LisDbContext db)
{
    public async Task<IReadOnlySet<DateOnly>> DatesAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (to < from) return new HashSet<DateOnly>();

        await EnsureFixedHolidaysAsync(from.Year, to.Year, ct);

        return (await db.PublicHolidays.AsNoTracking()
            .Where(h => h.Date >= from && h.Date <= to)
            .Select(h => h.Date)
            .ToListAsync(ct)).ToHashSet();
    }

    /// <summary>
    /// Adds the civil holidays of the years not in the calendar yet. A date ever recorded, removed
    /// ones included, is left alone: a holiday HR took out stays out.
    /// </summary>
    private async Task EnsureFixedHolidaysAsync(int firstYear, int lastYear, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()) return;

        var from = new DateOnly(firstYear, 1, 1);
        var to = new DateOnly(lastYear, 12, 31);
        var recorded = (await db.PublicHolidays.IgnoreQueryFilters().AsNoTracking()
            .Where(h => h.Date >= from && h.Date <= to)
            .Select(h => h.Date)
            .ToListAsync(ct)).ToHashSet();

        var missing = Enumerable.Range(firstYear, lastYear - firstYear + 1)
            .SelectMany(year => PublicHoliday.FixedHolidays.Select(h => (Date: new DateOnly(year, h.Month, h.Day), h.Name)))
            .Where(h => !recorded.Contains(h.Date))
            .Select(h => new PublicHoliday(h.Date, h.Name, false))
            .ToList();
        if (missing.Count == 0) return;

        db.PublicHolidays.AddRange(missing);

        var reason = AuditSaveChangesInterceptor.PendingReason;
        AuditSaveChangesInterceptor.PendingReason = firstYear == lastYear
            ? $"Jours fériés civils {firstYear} ajoutés au calendrier"
            : $"Jours fériés civils {firstYear}-{lastYear} ajoutés au calendrier";
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another workstation put them in at the same moment (unique date): theirs are read.
            foreach (var holiday in missing) db.Entry(holiday).State = EntityState.Detached;
        }
        finally
        {
            AuditSaveChangesInterceptor.PendingReason = reason;
        }
    }
}
