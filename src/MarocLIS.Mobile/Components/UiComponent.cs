using MarocLIS.Mobile.Services;
using Microsoft.AspNetCore.Components;

namespace MarocLIS.Mobile.Components;

/// <summary>
/// A component that shows text: <c>T(…)</c> reads the catalogue, and the component re-renders
/// when the language changes. The key is always written out as a literal at the call, which is what
/// lets the build keep it (Build/MobileStrings.targets) and check-resource-keys.ps1 verify it.
/// </summary>
public abstract class UiComponent : ComponentBase, IDisposable
{
    [Inject] protected Texts Texts { get; set; } = default!;

    protected string T(string key, params object?[] args) => Texts.Get(key, args);

    protected override void OnInitialized()
    {
        Texts.Changed += OnLanguageChanged;
        base.OnInitialized();
    }

    private void OnLanguageChanged() => InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        Texts.Changed -= OnLanguageChanged;
        Disposing();
        GC.SuppressFinalize(this);
    }

    protected virtual void Disposing() { }
}

/// <summary>
/// A screen behind the sign-in: without a session it sends the phone to the sign-in screen and
/// shows nothing — not even the frame of a patient's file.
/// </summary>
public abstract class SecurePage : UiComponent
{
    [Inject] protected SessionState Session { get; set; } = default!;
    [Inject] protected ApiClient Api { get; set; } = default!;
    [Inject] protected Workflow Workflow { get; set; } = default!;
    [Inject] protected Browser Browser { get; set; } = default!;
    [Inject] protected NavigationManager Navigation { get; set; } = default!;

    /// <summary>False when the page was opened without a session and is on its way to the sign-in.</summary>
    protected bool Allowed { get; private set; }

    protected bool Online => Browser.IsOnline;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Browser.ConnectivityChanged += OnConnectivityChanged;

        Allowed = Session.IsSignedIn;
        if (!Allowed) Navigation.NavigateTo("connexion", replace: true);
    }

    private void OnConnectivityChanged() => InvokeAsync(StateHasChanged);

    protected override void Disposing() => Browser.ConnectivityChanged -= OnConnectivityChanged;
}
