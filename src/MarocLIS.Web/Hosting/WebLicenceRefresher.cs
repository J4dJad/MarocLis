using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Licensing;

namespace MarocLIS.Web.Hosting;

/// <summary>
/// Evaluates the licence when the web interface starts and again at the workstations' heartbeat, as
/// the API's refresher does: a licence imported at a Windows workstation — the « Version Web »
/// module added, say — reaches the browsers within minutes, and an end date is noticed on the day.
/// The tabs open see the change at once (<see cref="LicenceStatusHolder.Changed"/>).
/// </summary>
public sealed class WebLicenceRefresher(IServiceScopeFactory scopes, ILogger<WebLicenceRefresher> logger) : BackgroundService
{
    /// <summary>The shortest time between two evaluations asked for by the licence's page.</summary>
    public static readonly TimeSpan OnDemandInterval = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastOnDemand = DateTimeOffset.MinValue;

    /// <summary>
    /// Evaluates the licence now, for the page that says to import it at a Windows workstation and
    /// then reload: the reload finds it. At most once every <see cref="OnDemandInterval"/>, since
    /// anyone on the network may load that page.
    /// </summary>
    public async Task RefreshNowAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            if (DateTimeOffset.UtcNow - _lastOnDemand < OnDemandInterval) return;
            _lastOnDemand = DateTimeOffset.UtcNow;

            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<LicenceService>().RefreshAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The licence could not be evaluated.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Workstation.HeartbeatInterval);

        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<LicenceService>().RefreshAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The database out of reach: the last evaluation stands until the next tick.
                logger.LogWarning(ex, "The licence could not be evaluated.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
