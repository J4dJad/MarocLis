using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Licensing;

namespace MarocLIS.Api;

/// <summary>
/// Evaluates the licence when the API starts and again at the workstations' heartbeat, so a
/// licence imported at a workstation reaches the phones within minutes and an end date is noticed
/// on the day. The API counts no workstation: a phone is not one.
/// </summary>
public sealed class LicenceRefresher(IServiceScopeFactory scopes, ILogger<LicenceRefresher> logger) : BackgroundService
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
