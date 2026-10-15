using MarocLIS.Domain.Equipment;
using MarocLIS.Instruments.Drivers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Instruments.Hosting;

/// <summary>
/// The sink a running driver is given: every call is served by a sink from a fresh DI scope, with
/// a database context of its own.
/// <para>
/// A link runs for days. With one context for its whole life, EF kept handing back the mappings,
/// tubes and results as it had first loaded them: a corrected conversion factor or a disabled
/// mapping was ignored until the application was restarted, and a tube rejected at another
/// workstation still looked received here. A scope per message reads the database as it is now,
/// and a failed import leaves nothing behind in a context the next message would reuse.
/// </para>
/// <para>
/// Failures stop here too. The analyser has already been acknowledged and the frame is on record,
/// replayable; letting the exception climb into the driver would stop the link and leave the
/// analyser's next results waiting for the host to restart it.
/// </para>
/// </summary>
public sealed class ScopedInstrumentMessageSink(IServiceScopeFactory scopes, ILogger? logger = null)
    : IInstrumentMessageSink
{
    public async Task<bool> HandleAsync(Guid interfaceId, InstrumentMessage message,
                                         CancellationToken ct = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await Sink(scope).HandleAsync(interfaceId, message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The log row was recorded as failed before the import started, so it stays replayable.
            logger?.LogError(ex, "Import of a message from analyser link {Interface} failed.", interfaceId);
            return false;
        }
    }

    public async Task<IReadOnlyList<InstrumentOrderDownload>> ResolveQueryAsync(
        Guid interfaceId, IReadOnlyList<string> specimenIds, CancellationToken ct = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await Sink(scope).ResolveQueryAsync(interfaceId, specimenIds, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger?.LogError(ex, "Query from analyser link {Interface} could not be answered.", interfaceId);
            return [];
        }
    }

    public async Task LogRawAsync(Guid interfaceId, MessageDirection direction, string content,
                                  CancellationToken ct = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await Sink(scope).LogRawAsync(interfaceId, direction, content, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger?.LogError(ex, "Raw frame from analyser link {Interface} could not be logged.", interfaceId);
        }
    }

    public async Task ReportConnectionStateAsync(Guid interfaceId, bool connected, string? error,
                                                 CancellationToken ct = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await Sink(scope).ReportConnectionStateAsync(interfaceId, connected, error, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger?.LogError(ex, "Connection state of analyser link {Interface} could not be saved.", interfaceId);
        }
    }

    private static IInstrumentMessageSink Sink(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IInstrumentMessageSink>();
}
