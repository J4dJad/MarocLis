using System.Globalization;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Licensing;
using MarocLIS.Domain.Organization;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Licensing;

/// <summary>
/// The licence in the database: the signed files in <c>cfg.Licences</c>, three markers among the
/// settings, and what the evaluation reads around them — the server's identity, the orders, the
/// patients.
/// </summary>
public sealed class LicenceStore(LisDbContext db) : ILicenceStore
{
    /// <summary>The database's own identifier, the third part of the server ID.</summary>
    public const string InstallationIdKey = "Licence.InstallationId";

    /// <summary>The latest day MarocLIS ran on: a clock put back cannot go before it.</summary>
    public const string LastSeenKey = "Licence.LastSeenOn";

    /// <summary>The day the read-only mode began for want of a valid licence: the bench days count from it.</summary>
    public const string RestrictedSinceKey = "Licence.RestrictedSince";

    public Task<StoredLicence> ReadAsync(CancellationToken ct = default) =>
        db.RunExclusiveAsync(async () =>
        {
            var current = await db.Licences.AsNoTracking()
                .Where(l => l.ReplacedAt == null)
                .OrderByDescending(l => l.ImportedAt)
                .FirstOrDefaultAsync(ct);

            var (machine, instance) = await ServerNamesAsync(ct);
            var installationId = await InstallationIdAsync(ct);

            // Every order and patient, whatever the site and whether cancelled: the count a training
            // licence is held to, and the data that keeps a database from the blocking import screen.
            var orders = db.LabOrders.IgnoreQueryFilters();
            var orderCount = await orders.CountAsync(ct);
            var latestOrder = orderCount == 0 ? null : await orders.MaxAsync(o => (DateTimeOffset?)o.RegisteredAt, ct);
            var hasPatients = await db.Patients.IgnoreQueryFilters().AnyAsync(ct);

            var markers = await db.Settings.AsNoTracking()
                .Where(s => s.SiteId == null && (s.Key == LastSeenKey || s.Key == RestrictedSinceKey))
                .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

            var site = await db.Sites.AsNoTracking().IgnoreQueryFilters()
                .Where(s => !s.IsDeleted && s.Kind == LabSiteKind.MainLaboratory)
                .OrderBy(s => s.CreatedAt)
                .Select(s => new { s.Name, s.LegalName, s.HealthMinistryAuthorizationNumber })
                .FirstOrDefaultAsync(ct);

            return new StoredLicence
            {
                Content = current?.Content,
                Number = current?.Number,
                Version = current?.Version,
                MachineName = machine,
                InstanceName = instance,
                InstallationId = installationId,
                OrderCount = orderCount,
                LatestOrderOn = latestOrder is { } at ? DateOnly.FromDateTime(at.Date) : null,
                HasPatients = hasPatients,
                LatestDateSeen = DateOf(markers.GetValueOrDefault(LastSeenKey)),
                RestrictedSince = DateOf(markers.GetValueOrDefault(RestrictedSinceKey)),
                SiteName = site?.Name.Fr,
                LegalName = string.IsNullOrWhiteSpace(site?.LegalName) ? null : site.LegalName,
                Authorisation = site?.HealthMinistryAuthorizationNumber
            };
        }, ct);

    public Task<IReadOnlyList<LicenceHistoryRow>> HistoryAsync(CancellationToken ct = default) =>
        db.RunExclusiveAsync<IReadOnlyList<LicenceHistoryRow>>(async () =>
            await db.Licences.AsNoTracking()
                .OrderByDescending(l => l.ImportedAt)
                .Select(l => new LicenceHistoryRow(l.Number, l.Version, l.Kind, l.ImportedAt, l.ImportedByUserName,
                                                   l.Workstation, l.ReplacedAt))
                .ToListAsync(ct), ct);

    public Task<(int Version, string Content)?> HighestVersionAsync(string number, CancellationToken ct = default) =>
        db.RunExclusiveAsync<(int Version, string Content)?>(async () =>
        {
            var row = await db.Licences.AsNoTracking()
                .Where(l => l.Number == number)
                .OrderByDescending(l => l.Version)
                .Select(l => new { l.Version, l.Content })
                .FirstOrDefaultAsync(ct);

            return row is null ? null : (row.Version, row.Content);
        }, ct);

    public Task SaveAsync(LicenceRecord record, CancellationToken ct = default) =>
        db.RunExclusiveAsync(async () =>
        {
            foreach (var previous in await db.Licences.Where(l => l.ReplacedAt == null).ToListAsync(ct))
                previous.Replace(record.ImportedAt);

            db.Licences.Add(record);
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);

    public Task SaveMarkersAsync(DateOnly? latestDateSeen, DateOnly? restrictedSince, bool clearRestricted,
                                 CancellationToken ct = default) =>
        db.RunExclusiveAsync(async () =>
        {
            if (latestDateSeen is { } seen) await PutAsync(LastSeenKey, Text(seen), onlyForward: true, ct);
            if (restrictedSince is { } since) await PutAsync(RestrictedSinceKey, Text(since), onlyForward: false, ct);
            else if (clearRestricted) await PutAsync(RestrictedSinceKey, null, onlyForward: false, ct);

            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);

    // ---- The server's identity -------------------------------------------------------------------

    /// <summary>
    /// The Windows name of the machine SQL Server runs on and its instance, as SQL Server itself
    /// reports them — the same answer from every workstation, whatever name it uses to reach the
    /// server (an IP address, an alias, « localhost » on the server itself).
    /// </summary>
    private async Task<(string Machine, string? Instance)> ServerNamesAsync(CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return (Environment.MachineName, null);

        var row = await db.Database.SqlQueryRaw<ServerNames>(
                "SELECT CAST(SERVERPROPERTY('MachineName') AS nvarchar(128)) AS Machine, "
                + "CAST(SERVERPROPERTY('InstanceName') AS nvarchar(128)) AS Instance")
            .SingleAsync(ct);

        return (row.Machine ?? string.Empty, row.Instance);
    }

    private sealed record ServerNames(string? Machine, string? Instance);

    /// <summary>
    /// The identifier written into this database the first time it was read. Copied with the
    /// database, so it alone would follow a copy to another server; with the machine's name it does
    /// not. Two workstations starting together on a new database both try to write it: the second
    /// meets the unique index and reads the first one's.
    /// </summary>
    private async Task<Guid> InstallationIdAsync(CancellationToken ct)
    {
        if (await ReadInstallationIdAsync(ct) is { } existing) return existing;

        var created = new AppSetting(InstallationIdKey, Guid.NewGuid().ToString("D"))
        {
            Description = "Identifiant de cette base, partie de l'identifiant serveur de la licence.",
            IsSecuritySensitive = true
        };
        db.Settings.Add(created);

        try
        {
            await db.SaveChangesAsync(ct);
            return Guid.Parse(created.Value!);
        }
        catch (DbUpdateException ex) when (DatabaseInitializer.IsDuplicateKey(ex))
        {
            db.Entry(created).State = EntityState.Detached;
            return await ReadInstallationIdAsync(ct)
                   ?? throw new InvalidOperationException("The installation identifier could not be read back.");
        }
    }

    private async Task<Guid?> ReadInstallationIdAsync(CancellationToken ct)
    {
        var value = await db.Settings.AsNoTracking()
            .Where(s => s.Key == InstallationIdKey && s.SiteId == null)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        return Guid.TryParse(value, out var id) ? id : null;
    }

    // ---- Markers ---------------------------------------------------------------------------------

    private async Task PutAsync(string key, string? value, bool onlyForward, CancellationToken ct)
    {
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key && s.SiteId == null, ct);

        if (setting is null)
        {
            if (value is null) return;
            db.Settings.Add(new AppSetting(key, value) { IsSecuritySensitive = true });
            return;
        }

        // Another workstation may have written a later day meanwhile; the marker never goes back.
        if (onlyForward && DateOf(setting.Value) is { } stored && DateOf(value) is { } proposed && proposed <= stored)
            return;

        setting.Value = value;
    }

    private static string Text(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? DateOf(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}
