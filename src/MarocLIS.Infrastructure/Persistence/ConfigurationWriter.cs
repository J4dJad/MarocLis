using MarocLIS.Application.Administration;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Sequencing;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Persistence;

/// <summary>
/// Tracked access to the two configuration entities that are not aggregate roots, so the
/// application layer can edit them without depending on the DbContext.
/// <para>
/// Tracked on purpose — unlike everything in <c>Queries</c>, these instances are mutated and saved
/// through the shared <see cref="UnitOfWork"/>, which is what puts the change through the audit
/// interceptor in the same transaction.
/// </para>
/// </summary>
public sealed class ConfigurationWriter(LisDbContext context) : IConfigurationWriter
{
    public Task<NumberSequence?> GetSequenceAsync(Guid sequenceId, CancellationToken ct = default) =>
        context.NumberSequences.FirstOrDefaultAsync(s => s.Id == sequenceId, ct);

    public Task<AppSetting?> GetSettingAsync(string key, Guid? siteId,
                                              CancellationToken ct = default) =>
        context.Settings.FirstOrDefaultAsync(s => s.Key == key && s.SiteId == siteId, ct);

    public async Task AddSettingAsync(AppSetting setting, CancellationToken ct = default) =>
        await context.Settings.AddAsync(setting, ct);
}
