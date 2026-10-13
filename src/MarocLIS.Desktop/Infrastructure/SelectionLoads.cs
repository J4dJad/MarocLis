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

        await _gate.WaitAsync();
        try
        {
            if (_versions[key] != version) return;
            await load();
        }
        finally
        {
            _gate.Release();
        }
    }
}
