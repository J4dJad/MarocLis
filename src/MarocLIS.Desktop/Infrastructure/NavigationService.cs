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

    public async Task NavigateToAsync<TView>(Action<TView>? configure = null)
        where TView : LocalizedView
    {
        if (_host is null)
            throw new InvalidOperationException("The navigation host has not been attached.");

        // Abandon an activation still in flight: a fast double-click on the sidebar would otherwise
        // let a slower earlier load finish last and overwrite the newer view's data.
        if (_activation is not null)
        {
            await _activation.CancelAsync();
            _activation.Dispose();
        }

        _activation = new CancellationTokenSource();
        var token = _activation.Token;

        var previousScope = _currentScope;
        var previousView = Current;

        var scope = rootProvider.CreateScope();
        var installed = false;

        try
        {
            var view = scope.ServiceProvider.GetRequiredService<TView>();
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
            previousView?.Dispose();
            previousScope?.Dispose();

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
        catch (Exception ex)
        {
            logger?.LogError(ex, "Navigation to {View} failed.", typeof(TView).Name);
            if (!installed) scope.Dispose();
            dialogs.Error(ex.Message);
        }
    }

    public async Task RefreshCurrentAsync()
    {
        if (Current is null) return;

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
