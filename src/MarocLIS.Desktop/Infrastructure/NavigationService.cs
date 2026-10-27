using MarocLIS.Desktop.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// Swaps the shell's active view.
/// <para>
/// Each view gets <b>its own DI scope</b>, disposed when the view is replaced. That is what keeps
/// the EF change tracker per screen: an operator who opens a registration, edits it, then navigates
/// away must not have those edits saved by the next screen's commit. A single shared scope across
/// the shell would do exactly that.
/// </para>
/// </summary>
public interface INavigationService
{
    /// <summary>Called once by the shell to supply the panel views are hosted in.</summary>
    void Attach(Panel host, Action<LocalizedView> onViewActivated);

    Task NavigateToAsync<TView>(Action<TView>? configure = null) where TView : LocalizedView;

    /// <summary>
    /// Lets go of the host and of the view shown in it, which is disposed with its scope (once idle,
    /// for a view awaiting a write). Called when the window closes or is replaced by a new one;
    /// nothing happens when the navigation is attached to another window's <paramref name="host"/>.
    /// </summary>
    void Detach(Panel host);

    /// <summary>The view currently shown, or null before the first navigation.</summary>
    LocalizedView? Current { get; }

    /// <summary>Re-runs the current view's data load, for a Refresh command.</summary>
    Task RefreshCurrentAsync();
}

public sealed class NavigationService(
    IServiceProvider rootProvider,
    IUserDialogs dialogs,
    ILogger<NavigationService>? logger = null) : INavigationService, IDisposable
{
    private Panel? _host;
    private Action<LocalizedView>? _onViewActivated;
    private IServiceScope? _currentScope;
    private CancellationTokenSource? _activation;

    public LocalizedView? Current { get; private set; }

    public void Attach(Panel host, Action<LocalizedView> onViewActivated)
    {
        _host = host;
        _onViewActivated = onViewActivated;
    }

    public void Detach(Panel host)
    {
        if (!ReferenceEquals(_host, host)) return;

        _activation?.Cancel();
        _activation?.Dispose();
        _activation = null;

        var view = Current;
        var scope = _currentScope;
        Current = null;
        _currentScope = null;

        if (view is not null) view.Parent?.Controls.Remove(view);
        Retire(view, scope);

        _host = null;
        _onViewActivated = null;
    }

    public async Task NavigateToAsync<TView>(Action<TView>? configure = null)
        where TView : LocalizedView
    {
        if (_host is null)
            throw new InvalidOperationException("The navigation host has not been attached.");

        // The screen being left decides whether it can go: results or control values typed and not
        // yet saved are confirmed first, as opening another file on the same screen already was.
        if (Current is { IsDisposed: false } current && !current.ConfirmLeave()) return;

        // Abandon an activation still in flight: a fast double-click on the sidebar would otherwise
        // let a slower earlier load finish last and overwrite the newer view's data.
        if (_activation is not null)
        {
            await _activation.CancelAsync();
            _activation.Dispose();
            _activation = null;

            // The window was closed or replaced meanwhile.
            if (_host is null) return;
        }

        _activation = new CancellationTokenSource();
        var token = _activation.Token;

        var previousScope = _currentScope;
        var previousView = Current;

        var scope = rootProvider.CreateScope();
        var installed = false;
        TView? view = null;

        try
        {
            view = scope.ServiceProvider.GetRequiredService<TView>();
            configure?.Invoke(view);

            _host.SuspendLayout();

            _host.Controls.Clear();
            _host.Controls.Add(view);
            view.BringToFront();

            Current = view;
            _currentScope = scope;
            installed = true;

            _host.ResumeLayout(performLayout: true);

            _onViewActivated?.Invoke(view);

            // Dispose the outgoing view and its scope only once the new one is in place, so a
            // failure leaves the operator looking at the old screen rather than an empty panel.
            Retire(previousView, previousScope);

            await view.ActivateAsync(token);
        }
        // Once the view is on screen its scope belongs to it, and is disposed with it by the next
        // navigation. Disposing it here when its first load failed or was superseded left the
        // screen displayed on a dead scope: every later click failed with "Cannot access a
        // disposed object", and the screen looked empty for good.
        catch (OperationCanceledException)
        {
            if (!installed) scope.Dispose();
        }
        // A screen left before its first load finished is disposed under it, and the load then
        // failed on its emptied grids ("cannot add a row to a DataGridView without columns"):
        // the operator, already on the next screen, got that message for a screen they had left.
        catch (Exception ex) when (installed && view is { } left
                                   && (left.IsDisposed || !ReferenceEquals(Current, left)))
        {
            logger?.LogDebug(ex, "The load of {View}, left before it finished, was abandoned.", typeof(TView).Name);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Navigation to {View} failed.", typeof(TView).Name);
            if (!installed) scope.Dispose();
            dialogs.Error(ex.Message);
        }
    }

    /// <summary>
    /// Disposes an outgoing view and its scope — at once if it is idle, otherwise only when the
    /// write it is awaiting completes. Disposing the scope under a pending payment or cash close
    /// failed it half-way, with "unexpected error" and nobody able to say whether it committed;
    /// the detached view now finishes on its live scope and is disposed afterwards.
    /// </summary>
    private void Retire(LocalizedView? view, IServiceScope? scope)
    {
        if (view is not { IsBusy: true })
        {
            view?.Dispose();
            scope?.Dispose();
            return;
        }

        var host = _host;
        EventHandler? onIdle = null;
        onIdle = (_, _) =>
        {
            view.BecameIdle -= onIdle;

            // Posted rather than run inline: the event fires inside the operation's finally block,
            // and the click handler that awaited it still has a line or two to run on the view.
            void Release()
            {
                view.Dispose();
                scope?.Dispose();
            }

            if (host is { IsDisposed: false, IsHandleCreated: true }) host.BeginInvoke(Release);
            else Release();
        };
        view.BecameIdle += onIdle;
    }

    public async Task RefreshCurrentAsync()
    {
        if (Current is null) return;

        // A reload on the same DbContext as a write still in flight fails with "a second operation
        // was started on this context"; the write reloads the screen itself when it completes.
        if (Current.IsBusy) return;

        _activation ??= new CancellationTokenSource();

        try
        {
            await Current.ActivateAsync(_activation.Token);
        }
        catch (OperationCanceledException)
        {
            // A refresh superseded by a navigation is not worth reporting.
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Refreshing {View} failed.", Current.GetType().Name);
            dialogs.Error(ex.Message);
        }
    }

    public void Dispose()
    {
        _activation?.Dispose();
        _currentScope?.Dispose();
    }
}
