using MarocLIS.Application.Licensing;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Desktop.Views;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Licensing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Forms;

/// <summary>
/// The licence in the shell: the modules in the menu, the banner under the header, and the
/// heartbeat that keeps this workstation counted and the licence evaluated.
/// </summary>
public sealed partial class MainShellForm
{
    private readonly LicenceStatusHolder? _licence;
    private readonly Panel _licenceBanner = new();
    private readonly Label _licenceBannerText = new();
    private readonly LinkLabel _licenceBannerLink = new();
    private readonly System.Windows.Forms.Timer _heartbeatTimer = new()
    {
        Interval = (int)Workstation.HeartbeatInterval.TotalMilliseconds
    };
    private bool _beating;

    private void BuildLicenceBanner()
    {
        _licenceBanner.Dock = DockStyle.Top;
        _licenceBanner.Height = 30;
        _licenceBanner.Padding = new Padding(UiTheme.Gutter, 0, UiTheme.Gutter, 0);
        _licenceBanner.Visible = false;

        _licenceBannerText.Dock = DockStyle.Fill;
        _licenceBannerText.Font = UiTheme.SmallBold;
        _licenceBannerText.TextAlign = ContentAlignment.MiddleLeft;
        _licenceBannerText.AutoEllipsis = true;

        // To the licence tab, for whoever can import the next licence there.
        _licenceBannerLink.Dock = DockStyle.Right;
        _licenceBannerLink.AutoSize = true;
        _licenceBannerLink.Font = UiTheme.SmallBold;
        _licenceBannerLink.TextAlign = ContentAlignment.MiddleRight;
        _licenceBannerLink.Padding = new Padding(0, 7, 0, 0);
        _licenceBannerLink.LinkClicked += async (_, _) => await OpenLicenceTabAsync();
        _licenceBannerLink.Visible = _currentUser.Has(Permissions.Administration.License);

        _licenceBanner.Controls.Add(_licenceBannerText);
        _licenceBanner.Controls.Add(_licenceBannerLink);
    }

    private void StartLicence()
    {
        if (_licence is null) return;

        // Raised from wherever the evaluation ran: the heartbeat here, or an import on the tab.
        _licence.Changed += OnLicenceChanged;
        ShowLicenceBanner();

        _heartbeatTimer.Tick += async (_, _) => await HeartbeatAsync();
        _heartbeatTimer.Start();
    }

    private void OnLicenceChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing) return;

        if (InvokeRequired)
        {
            BeginInvoke(() => OnLicenceChanged(sender, e));
            return;
        }

        // A licence imported here or seen at the heartbeat: modules opened or closed show at
        // once, without signing out.
        RenderNavigation();
        ShowLicenceBanner();
    }

    private void ShowLicenceBanner()
    {
        if (_licence is null) return;

        var banner = LicenceNavigation.BannerFor(_licence.Current,
                                                 _currentUser.Has(Permissions.Administration.License),
                                                 _currentUser.Has(Permissions.Results.BiologicalValidate));

        if (banner is null)
        {
            _licenceBanner.Visible = false;
            return;
        }

        object?[] arguments = banner.CauseKey is { } cause ? [.. banner.Arguments, T(cause)] : banner.Arguments;
        _licenceBannerText.Text = T(banner.ResourceKey, arguments);
        _licenceBannerLink.Text = T("Licence.Banner.Open");

        var (back, ink) = banner.Level == LicenceBannerLevel.Warning
            ? (UiTheme.AbnormalBackground, UiTheme.Abnormal)
            : (UiTheme.CriticalBackground, UiTheme.CriticalInk);

        _licenceBanner.BackColor = back;
        _licenceBannerText.ForeColor = ink;
        _licenceBannerLink.LinkColor = _licenceBannerLink.ActiveLinkColor = ink;
        _licenceBanner.Visible = true;
    }

    private async Task OpenLicenceTabAsync()
    {
        if (_entries.FirstOrDefault(e => e.Key == "settings") is not { } settings) return;

        await ActivateAsync(settings with { Navigate = n => n.NavigateToAsync<SettingsView>(v => v.ShowLicenceTab()) });
    }

    /// <summary>
    /// Every five minutes: the licence evaluated again — a licence imported at another workstation,
    /// the end date reached — and this workstation's presence renewed, so its place is not given to
    /// another after a quarter of an hour of silence.
    /// <para>
    /// A workstation found over the count here — back from sleep after its place was taken — is not
    /// signed out in the middle of a task; it is refused at its next sign-in.
    /// </para>
    /// </summary>
    private async Task HeartbeatAsync()
    {
        if (_beating || _endingSession) return;
        _beating = true;

        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<LicenceService>().RefreshAsync();
            await scope.ServiceProvider.GetRequiredService<WorkstationPresenceService>()
                       .ClaimAsync(Environment.MachineName, _currentUser.UserName, LicenceFileActions.AppVersion);
        }
        catch (Exception ex)
        {
            // The database out of reach for a moment: the last evaluation stands.
            _logger?.LogWarning(ex, "The licence heartbeat failed.");
        }
        finally
        {
            _beating = false;
        }
    }

    /// <summary>Gives the workstation's place back at once when its session ends.</summary>
    private async Task ReleaseWorkstationAsync()
    {
        if (_licence is null) return;

        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<WorkstationPresenceService>()
                       .ReleaseAsync(Environment.MachineName);
        }
        catch (Exception ex)
        {
            // Freed after the quarter of an hour of silence instead.
            _logger?.LogWarning(ex, "The workstation's place could not be released.");
        }
    }

    private void StopLicence()
    {
        if (_licence is not null) _licence.Changed -= OnLicenceChanged;
        _heartbeatTimer.Dispose();
    }
}
