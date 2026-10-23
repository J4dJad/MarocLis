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
