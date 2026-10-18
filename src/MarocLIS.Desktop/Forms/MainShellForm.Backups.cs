using System.Globalization;
using MarocLIS.Application.Administration;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Desktop.Views;
using MarocLIS.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Forms;

/// <summary>
/// The warning, in the status bar, that the last backup is more than 48 hours old: shown to the
/// users who may act on it (<see cref="Permissions.Administration.Backup"/>) from the moment they
/// sign in, on every screen, until a backup is taken. A dead scheduled task fails silently, and the
/// day it matters is the day the server's disk does.
/// </summary>
public sealed partial class MainShellForm
{
    /// <summary>Read again every half hour: a backup taken meanwhile clears the warning.</summary>
    private readonly System.Windows.Forms.Timer _backupTimer = new() { Interval = 30 * 60 * 1000 };
    private readonly ToolStripStatusLabel _statusBackup = new();
    private BackupHealth? _backupHealth;
    private bool _backupWired;

    private async Task CheckBackupAgeAsync()
    {
        if (!PermissionGate.Allows(Permissions.Administration.Backup)) return;

        if (!_backupWired)
        {
            _backupWired = true;
            _statusBackup.BackColor = UiTheme.CriticalBackground;
            _statusBackup.ForeColor = UiTheme.CriticalInk;
            _statusBackup.Font = UiTheme.SmallBold;
            _statusBackup.Margin = new Padding(8, 3, 8, 3);
            _statusBackup.Padding = new Padding(6, 0, 6, 0);
            _statusBackup.Visible = false;
            _statusBackup.Click += async (_, _) => await OpenBackupsAsync();
            // The status bar's renderer paints no item background: drawn here, so the warning reads
            // as one on the bar's accent colour rather than as red text lost on it.
            _statusBackup.Paint += (_, e) =>
            {
                var area = new Rectangle(Point.Empty, _statusBackup.Size);
                using (var fill = new SolidBrush(UiTheme.CriticalBackground)) e.Graphics.FillRectangle(fill, area);
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis
                            | TextFormatFlags.NoPrefix | (RightToLeft == RightToLeft.Yes ? TextFormatFlags.RightToLeft : 0);
                TextRenderer.DrawText(e.Graphics, _statusBackup.Text, _statusBackup.Font, area, UiTheme.CriticalInk, flags);
            };
            _statusBar.Items.Insert(1, _statusBackup);

            _backupTimer.Tick += async (_, _) => await CheckBackupAgeAsync();
            _backupTimer.Start();
            FormClosed += (_, _) => _backupTimer.Dispose();
        }

        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            _backupHealth = await scope.ServiceProvider.GetRequiredService<BackupService>().GetHealthAsync();
        }
        catch (Exception ex)
        {
            // Not knowing is not a warning: the tab says why when it is opened.
            _logger?.LogWarning(ex, "The age of the last backup could not be read.");
            _backupHealth = null;
        }

        ShowBackupWarning();
    }

    /// <summary>Read again at once after a backup taken from the tab, rather than at the next half hour.</summary>
    internal Task RefreshBackupWarningAsync() => CheckBackupAgeAsync();

    private void ShowBackupWarning()
    {
        if (_backupHealth is not { IsStale: true } health)
        {
            _statusBackup.Visible = false;
            return;
        }

        _statusBackup.Text = health.LastSuccessAt is { } last
            ? "⚠  " + T("Backup.StatusStale", last.ToString("g", CultureInfo.CurrentCulture))
            : "⚠  " + T("Backup.StatusNone");
        _statusBackup.ToolTipText = T("Backup.StatusHint");
        _statusBackup.Visible = true;
    }

    private async Task OpenBackupsAsync()
    {
        if (_entries.FirstOrDefault(e => e.Key == "settings") is not { } settings) return;

        await ActivateAsync(settings);
        if (_navigation.Current is SettingsView view) view.OpenBackupsTab();
    }
}
