using MarocLIS.Application.Billing;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class InvoicePrintSettingsQueries(LisDbContext db) : IInvoicePrintSettingsQueries
{
    // Installation-wide only, like the other dedicated settings: one invoice format per laboratory.
    public async Task<IReadOnlyDictionary<string, string?>> GetSettingValuesAsync(string prefix,
                                                                                  CancellationToken ct = default)
    {
        var rows = await db.Settings.AsNoTracking()
            .Where(s => s.SiteId == null && s.Key.StartsWith(prefix))
            .Select(s => new { s.Key, s.Value })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }
}
