namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// Runs a view's selection-driven loads one at a time, skipping any that a newer selection has
/// already replaced.
/// <para>
/// A grid raises <see cref="DataGridView.SelectionChanged"/> several times while it is filled and
/// its first row is made current, and each event started a detail load. The loads of one view
/// share its database context, which refuses a second query while the first is still running:
/// the validation screen opened on "A second operation was started on this context instance".
/// The loads now queue behind each other, and one whose selection has changed while it waited is
/// dropped, so the details always follow the row last selected.
/// </para>
/// </summary>
public sealed class SelectionLoads
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<object, int> _versions = [];

    public async Task RunAsync(Func<Task> load)
    {
        // Keyed by the load itself: two grids of one view share the queue but not the versions,
        // so selecting in one never drops the other's pending load.
        var key = load.Method;
        var version = _versions[key] = _versions.GetValueOrDefault(key) + 1;

        // The loads are the view's own methods, so the delegate's target is the view. Leaving a
        // screen disposes it, and its grids lose their columns; a load still in flight then came
        // back to fill a grid that no longer had any and failed with "cannot add a row to a
        // DataGridView without columns". A load for a screen that is gone has nobody to show to.
        var owner = load.Target as Control;

        await _gate.WaitAsync();
        try
        {
            if (_versions[key] != version || IsGone(owner)) return;
            await load();
        }
        // The view's queries report a closed screen as a cancellation (IsolatedQueries): leaving the
        // validation screen while a row was loading ended in « Erreur inattendue ».
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException
                                       or OperationCanceledException
                                   && IsGone(owner))
        {
            // The screen was closed while the load ran: there is nothing left to fill.
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsGone(Control? owner) => owner is { IsDisposed: true } or { Disposing: true };
}
