using Microsoft.JSInterop;

namespace MarocLIS.Mobile.Services;

/// <summary>
/// The little the application needs from the browser that .NET cannot reach by itself: the page's
/// language and direction on the <c>html</c> element, whether the phone has a network, and the
/// clipboard. All of it
/// is in wwwroot/js/app.js, a file the Content-Security-Policy allows like any other of the site's.
/// </summary>
public sealed class Browser(IJSRuntime js) : IDisposable, IAsyncDisposable
{
    private DotNetObjectReference<Browser>? _self;

    /// <summary>True until the browser says otherwise.</summary>
    public bool IsOnline { get; private set; } = true;

    /// <summary>Raised when the phone loses or regains its network.</summary>
    public event Action? ConnectivityChanged;

    public async Task StartAsync()
    {
        _self ??= DotNetObjectReference.Create(this);
        try
        {
            IsOnline = await js.InvokeAsync<bool>("marocLis.watchConnectivity", _self);
        }
        catch (JSException)
        {
            IsOnline = true;
        }
    }

    [JSInvokable]
    public void SetOnline(bool online)
    {
        if (online == IsOnline) return;
        IsOnline = online;
        ConnectivityChanged?.Invoke();
    }

    /// <summary>Sets <c>lang</c> and <c>dir</c> on the page, so the whole document — scroll bars, form controls — follows.</summary>
    public async Task SetDocumentLanguageAsync(string lang, string dir)
    {
        try
        {
            await js.InvokeVoidAsync("marocLis.setDocumentLanguage", lang, dir);
        }
        catch (JSException)
        {
            // The layout's own dir attribute still lays the screen out; nothing to report.
        }
    }

    /// <summary>Puts <paramref name="text"/> on the phone's clipboard; false when the browser refused.</summary>
    public async Task<bool> CopyAsync(string text)
    {
        try
        {
            return await js.InvokeAsync<bool>("marocLis.copyText", text);
        }
        catch (JSException)
        {
            return false;
        }
    }

    public void Dispose() => _self?.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
