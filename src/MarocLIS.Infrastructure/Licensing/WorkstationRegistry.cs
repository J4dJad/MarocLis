using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Licensing;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Licensing;

/// <summary>
/// The workstations' presence in <c>cfg.Workstations</c>. A claim runs under an application lock
/// held for its transaction, so two workstations signing in at the same second see each other and
/// cannot both take the last place.
/// </summary>
public sealed class WorkstationRegistry(LisDbContext db) : IWorkstationRegistry
{
    private const string LockResource = "MarocLIS.Licence.Workstations";

    public Task<WorkstationClaim> ClaimAsync(string machineName, string? userName, string? appVersion, int? limit,
                                             DateTimeOffset now, CancellationToken ct = default) =>
        db.RunExclusiveAsync(async () =>
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(ct)
                : null;

            if (transaction is not null)
                // Waits up to ten seconds for another claim to finish; past that it carries on
                // unlocked, a rare double place being better than a workstation that cannot sign in.
                await db.Database.ExecuteSqlRawAsync(
                    "DECLARE @r int; EXEC @r = sp_getapplock @Resource = {0}, @LockMode = 'Exclusive', "
                    + "@LockOwner = 'Transaction', @LockTimeout = 10000;", [LockResource], ct);

            var rows = await db.Workstations.ToListAsync(ct);
            var claim = WorkstationClaim.Decide(rows, machineName, now, limit);

            if (claim.Granted)
            {
                var row = rows.FirstOrDefault(w => string.Equals(w.MachineName, machineName, StringComparison.OrdinalIgnoreCase));
                if (row is null) db.Workstations.Add(row = new Workstation(machineName, now));

                row.Seen(now, userName);
                row.AppVersion = appVersion ?? row.AppVersion;
                await db.SaveChangesAsync(ct);

                // The list as it stands now, this workstation in it.
                claim = WorkstationClaim.Decide(rows.Contains(row) ? rows : [.. rows, row], machineName, now, limit);
            }

            if (transaction is not null) await transaction.CommitAsync(ct);
            return claim;
        }, ct);

    public Task ReleaseAsync(string machineName, DateTimeOffset now, CancellationToken ct = default) =>
        db.RunExclusiveAsync(async () =>
        {
            var row = await db.Workstations.FirstOrDefaultAsync(w => w.MachineName == machineName, ct);
            if (row is null) return 0;

            row.Release(now);
            return await db.SaveChangesAsync(ct);
        }, ct);

    public Task<IReadOnlyList<ActiveWorkstation>> ActiveAsync(DateTimeOffset now, CancellationToken ct = default) =>
        db.RunExclusiveAsync<IReadOnlyList<ActiveWorkstation>>(async () =>
        {
            var rows = await db.Workstations.AsNoTracking().ToListAsync(ct);
            return rows.Where(w => w.IsActive(now))
                       .OrderBy(w => w.FirstSeenAt)
                       .Select(w => new ActiveWorkstation(w.MachineName, w.LastUserName, w.FirstSeenAt, w.LastSeenAt))
                       .ToList();
        }, ct);
}
