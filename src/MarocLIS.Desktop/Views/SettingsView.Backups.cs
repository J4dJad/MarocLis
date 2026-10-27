using System.Globalization;
using MarocLIS.Application.Administration;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The « Sauvegardes » tab: the schedule the server's task follows, the backups SQL Server has
/// written, « Sauvegarder maintenant » and the restore.
/// <para>
/// Offered only with <see cref="Permissions.Administration.Backup"/>: a backup is a copy of every
/// patient file, and a restore replaces the laboratory's whole database. The list comes from SQL
/// Server's history (msdb), not from the folder, which is on the server's disk and which a
/// workstation cannot read.
/// </para>
/// </summary>
public sealed partial class SettingsView
{
    private readonly BackupService? _backups;
    private readonly TabPage _backupsTab = new();

    // ---- Status, along the top ----
    private readonly Label _bkBanner = new();
    private readonly Label _bkAgent = new();
    private readonly Label _bkNext = new();
    private readonly Label _bkLastResult = new();
    private readonly ProgressBar _bkProgress = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };

    // ---- Schedule ----
    private readonly Label _bkScheduleHeading = new();
    private readonly CheckBox _bkEnabled = new();
    private readonly Label _bkTimesLabel = new();
    private readonly TextBox _bkTimes = UiTheme.Input(200);
    private readonly Label _bkRetentionLabel = new();
    private readonly NumericUpDown _bkRetention = new();
    private readonly Label _bkDaysLabel = new();
    private readonly CheckBox[] _bkDays = [.. Enumerable.Range(0, 7).Select(_ => new CheckBox())];
    private readonly Label _bkFolderLabel = new();
    private readonly TextBox _bkFolder = UiTheme.Input(400);
    private readonly Button _bkTestFolder = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _bkFolderHint = new();
    private readonly Button _bkSave = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _bkReload = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _bkStatus = new();

    // ---- Backups ----
    private readonly Label _bkListHeading = new();
    private readonly DataGridView _bkList = UiTheme.Grid();
    private readonly Button _bkBackupNow = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _bkRestore = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _bkRestoreNote = new();

    private BackupOverview? _bkOverview;
    private bool _bkBusy;

    // Monday first, as a French calendar is printed; the stored names are English (BackupSettings).
    private static IReadOnlyList<DayOfWeek> WeekDays => BackupScreenRules.WeekDays;

    /// <summary>Where a restore would be asked from; read once, the machine does not change.</summary>
    private RestoreContext _restoreContext = new(IsServerMachine: false, IsLocalAdministrator: false);

    private bool BackupsOffered => PermissionGate.User is { } user && SettingsScreenRules.BackupsOffered(user, _backups is not null);

    private void BuildBackupsTab()
    {
        _backupsTab.BackColor = UiTheme.Surface;

        // ---- Status strip: the age of the last backup first, it is what the tab is opened for.
        _bkBanner.Font = UiTheme.BodyBold;
        _bkBanner.AutoSize = false;
        _bkBanner.Dock = DockStyle.Top;
        _bkBanner.Height = 30;
        _bkBanner.Padding = new Padding(12, 0, 12, 0);
        _bkBanner.TextAlign = ContentAlignment.MiddleLeft;
        _bkBanner.UseMnemonic = false;

        foreach (var line in new[] { _bkAgent, _bkNext, _bkLastResult })
        {
            line.Font = UiTheme.Small;
            line.ForeColor = UiTheme.TextSecondary;
            line.AutoSize = false;
            line.Dock = DockStyle.Top;
            line.Height = 20;
            line.Padding = new Padding(12, 2, 12, 0);
            line.UseMnemonic = false;
            // A failure quoted from SQL Server runs long: cut with "…" rather than under the card's edge.
            line.AutoEllipsis = true;
        }

        // The outcome of the last action of this screen (saved, folder tried, backup taken), with
        // the state lines rather than at the foot of the schedule card, whose buttons it overran.
        _bkStatus.Font = UiTheme.SmallBold;
        _bkStatus.ForeColor = UiTheme.TextSecondary;
        _bkStatus.AutoSize = false;
        _bkStatus.Dock = DockStyle.Top;
        _bkStatus.Height = 22;
        _bkStatus.Padding = new Padding(12, 3, 12, 0);
        _bkStatus.UseMnemonic = false;
        _bkStatus.AutoEllipsis = true;

        _bkProgress.Dock = DockStyle.Top;
        _bkProgress.Height = 6;
        _bkProgress.Visible = false;

        var status = new Panel { Dock = DockStyle.Top, Height = 122, BackColor = UiTheme.SurfaceRaised,
                                 Padding = new Padding(1, 1, 1, 6) };
        // Docked from the bottom up: the last added sits on top.
        status.Controls.AddRange([_bkProgress, _bkStatus, _bkLastResult, _bkNext, _bkAgent, _bkBanner]);
        UiChrome.AsCard(status);

        // ---- Schedule card, on the reading side.
        static Label Caption(Label label, Rectangle bounds, Font? font = null)
        {
            label.Font = font ?? UiTheme.Small;
            label.ForeColor = UiTheme.TextSecondary;
            label.AutoSize = false;
            label.Bounds = bounds;
            label.UseMnemonic = false;
            return label;
        }

        Caption(_bkScheduleHeading, new Rectangle(0, 0, 520, 24), UiTheme.SectionTitle).ForeColor = UiTheme.TextPrimary;

        _bkEnabled.Font = UiTheme.BodyBold;
        _bkEnabled.AutoSize = false;
        _bkEnabled.Bounds = new Rectangle(0, 32, 520, 26);

        Caption(_bkTimesLabel, new Rectangle(0, 66, 250, 18));
        _bkTimes.Location = new Point(0, 84);
        _bkTimes.Tag = UiTags.LatinValue;
        _bkTimes.Font = UiTheme.Monospace;

        Caption(_bkRetentionLabel, new Rectangle(270, 66, 250, 18));
        _bkRetention.Bounds = new Rectangle(270, 84, 90, UiTheme.InputHeight);
        _bkRetention.Minimum = BackupSettings.MinRetentionDays;
        _bkRetention.Maximum = BackupSettings.MaxRetentionDays;
        _bkRetention.Font = UiTheme.Body;

        Caption(_bkDaysLabel, new Rectangle(0, 124, 520, 18));
        for (var i = 0; i < _bkDays.Length; i++)
        {
            _bkDays[i].Font = UiTheme.Body;
            _bkDays[i].AutoSize = false;
            _bkDays[i].Bounds = new Rectangle(i * 74, 142, 72, 24);
            _bkDays[i].Tag = WeekDays[i];
        }

        Caption(_bkFolderLabel, new Rectangle(0, 178, 520, 18));
        _bkFolder.Location = new Point(0, 196);
        _bkFolder.Width = 350;
        _bkFolder.Tag = UiTags.LatinValue;
        _bkFolder.MaxLength = BackupSettings.MaxFolderLength;
        AdminLayout.Action(_bkTestFolder);
        _bkTestFolder.AutoSize = false;
        _bkTestFolder.Bounds = new Rectangle(360, 194, 160, UiTheme.ButtonHeight);
        Caption(_bkFolderHint, new Rectangle(0, 232, 520, 64));

        var fields = new Control[]
        {
            _bkScheduleHeading, _bkEnabled, _bkTimesLabel, _bkTimes, _bkRetentionLabel, _bkRetention, _bkDaysLabel,
            _bkFolderLabel, _bkFolder, _bkTestFolder, _bkFolderHint
        };

        var body = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceRaised, AutoScroll = true };
        body.Controls.Add(ReadingColumn([.. fields, .. _bkDays]));

        var schedule = new Panel { Dock = DockStyle.Left, Width = 560, BackColor = UiTheme.SurfaceRaised,
                                   Padding = new Padding(18, 14, 2, 1) };
        schedule.Controls.Add(body);
        schedule.Controls.Add(ActionBar(new Label(), _bkSave, _bkReload));
        UiChrome.AsCard(schedule);

        // ---- The backups.
        _bkList.Columns.Clear();
        void Column(string name, int weight, Font? font = null, bool right = false)
            => _bkList.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body,
                    Alignment = right ? DataGridViewContentAlignment.MiddleRight : DataGridViewContentAlignment.MiddleLeft
                }
            });

        Column("date", 18);
        Column("kind", 30);
        Column("size", 10, UiTheme.Monospace, right: true);
        Column("check", 14, UiTheme.SmallBold);
        Column("file", 28, UiTheme.Small);

        AdminLayout.ListGrid(_bkList);
        AdminLayout.PillColumn(_bkList, "check", r => r.Tag is BackupEntry entry
            ? entry.Set.IsDamaged
                ? AdminLayout.Crit
                : entry.Check.Status switch
                {
                    BackupCheckStatus.Verified => AdminLayout.Ok,
                    BackupCheckStatus.Failed => AdminLayout.Crit,
                    BackupCheckStatus.Pending => AdminLayout.Run,
                    _ => AdminLayout.Wait
                }
            : null);

        // « Restaurer… » along the foot: the card is too narrow for its title and both buttons, and
        // the restore is the rare, deliberate action, not the one beside the title.
        var list = AdminLayout.CardSection(_bkListHeading, _bkList, AdminLayout.Actions(_bkBackupNow),
                                           AdminLayout.Actions(null, _bkRestore));
        var listHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Padding = new Padding(AdminLayout.Gap, 0, 0, 0) };
        listHost.Controls.Add(list);
        listHost.Controls.Add(AdminLayout.Note(_bkRestoreNote, 58));

        var columns = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Padding = new Padding(0, AdminLayout.Gap, 0, 0) };
        columns.Controls.Add(listHost);
        columns.Controls.Add(schedule);

        // Arabic: the schedule on the right, the list on its left, like every two-column screen.
        columns.RightToLeftChanged += (_, _) =>
        {
            var rtl = columns.RightToLeft == RightToLeft.Yes;
            schedule.Dock = rtl ? DockStyle.Right : DockStyle.Left;
            listHost.Padding = rtl ? new Padding(0, 0, AdminLayout.Gap, 0) : new Padding(AdminLayout.Gap, 0, 0, 0);
        };

        _backupsTab.Controls.Add(columns);
        _backupsTab.Controls.Add(status);

        _bkSave.Click += async (_, _) => await SaveBackupSettingsAsync();
        _bkReload.Click += async (_, _) => await LoadBackupsAsync();
        _bkTestFolder.Click += async (_, _) => await TestBackupFolderAsync();
        _bkBackupNow.Click += async (_, _) => await BackupNowAsync();
        _bkRestore.Click += async (_, _) => await RestoreAsync();
        _bkList.SelectionChanged += (_, _) => RefreshBackupActions();
        _bkEnabled.CheckedChanged += (_, _) => RefreshScheduleFields();

        if (OperatingSystem.IsWindows())
        {
            try { _restoreContext = new RestoreContext(false, ServerMachine.IsLocalAdministrator()); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Whether the Windows user is an administrator could not be read."); }
        }
    }

    /// <summary>Brings the « Sauvegardes » tab forward: the status bar's warning opens it.</summary>
    internal void OpenBackupsTab()
    {
        if (_tabs.TabPages.Contains(_backupsTab)) _tabs.SelectedTab = _backupsTab;
    }

    private void ApplyBackupsTexts()
    {
        _backupsTab.Text = T("Backup.Tab");
        _bkScheduleHeading.Text = T("Backup.ScheduleHeading");
        _bkEnabled.Text = T("Backup.Enabled");
        _bkTimesLabel.Text = T("Backup.Times");
        _bkRetentionLabel.Text = T("Backup.RetentionDays");
        _bkDaysLabel.Text = T("Backup.Days");
        _bkFolderLabel.Text = T("Backup.Folder");
        _bkFolderHint.Text = T("Backup.FolderHint");
        _bkTestFolder.Text = T("Backup.TestFolder");
        _bkSave.Text = T("Action.Save");
        _bkReload.Text = T("Action.Refresh");
        _bkListHeading.Text = T("Backup.ListHeading");
        _bkBackupNow.Text = T("Backup.BackupNow");
        _bkRestore.Text = T("Backup.Restore");

        // The days in the language of the screen, as the calendar of the operating system writes them.
        var names = CultureInfo.CurrentUICulture.DateTimeFormat;
        for (var i = 0; i < _bkDays.Length; i++)
            _bkDays[i].Text = names.GetAbbreviatedDayName(WeekDays[i]);

        if (_bkList.Columns.Count > 0)
        {
            _bkList.Column("date").HeaderText = T("Backup.Column.Date");
            _bkList.Column("kind").HeaderText = T("Backup.Column.Origin");
            _bkList.Column("size").HeaderText = T("Backup.Column.Size");
            _bkList.Column("check").HeaderText = T("Backup.Column.Check");
            _bkList.Column("file").HeaderText = T("Backup.Column.File");
        }

        if (_bkOverview is not null) RenderBackups();
    }

    // ---- Loading ------------------------------------------------------------------------------------

    private async Task LoadBackupsAsync(CancellationToken ct = default)
    {
        if (!BackupsOffered || _bkBusy) return;

        try
        {
            var overview = await _backups!.GetOverviewAsync(ct);
            if (overview.IsFailure)
            {
                _bkStatus.ForeColor = UiTheme.Critical;
                _bkStatus.Text = Localizer.Describe(overview.Errors);
                return;
            }

            _bkOverview = overview.Value;
            _restoreContext = _restoreContext with { IsServerMachine = ServerMachine.IsThisMachine(_bkOverview.Server.ServerMachine) };
            ShowBackupSettings(_bkOverview.Settings);
            RenderBackups();
            _bkStatus.Text = string.Empty;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            // SQL Server's history unreadable, or the server gone: the rest of the settings still work.
            _logger?.LogError(ex, "The backups could not be read.");
            _bkBanner.BackColor = UiTheme.CriticalBackground;
            _bkBanner.ForeColor = UiTheme.CriticalInk;
            _bkBanner.Text = T("Backup.ReadFailed", ex.Message);
        }
    }

    private void ShowBackupSettings(BackupSettings settings)
    {
        _bkEnabled.Checked = settings.Enabled;
        _bkTimes.Text = BackupScreenRules.TimesText(settings);
        _bkRetention.Value = BackupScreenRules.Retention(settings.RetentionDays);
        foreach (var box in _bkDays)
            box.Checked = settings.RunsOn((DayOfWeek)box.Tag!);
        _bkFolder.Text = settings.Folder;
        RefreshScheduleFields();
    }

    private void RefreshScheduleFields()
    {
        // The schedule is kept while switched off, greyed: switching on again finds it as it was.
        var on = _bkEnabled.Checked;
        _bkTimes.Enabled = on;
        foreach (var box in _bkDays) box.Enabled = on;
    }

    private void RenderBackups()
    {
        if (_bkOverview is not { } overview) return;

        // ---- The banner: older than 48 hours, or none at all, in the critical colours.
        var (critical, banner) = BackupScreenRules.Banner(overview);
        Banner(critical, Line(banner));

        var (agentWarning, agent) = BackupScreenRules.Agent(overview);
        _bkAgent.Text = Line(agent);
        _bkAgent.ForeColor = agentWarning ? UiTheme.Abnormal : UiTheme.TextSecondary;

        var (nextWarning, next) = BackupScreenRules.Next(overview);
        _bkNext.Text = Line(next);
        _bkNext.ForeColor = nextWarning ? UiTheme.Abnormal : UiTheme.TextSecondary;

        if (BackupScreenRules.LastResult(overview) is { } result)
        {
            var at = result.At is { } when ? Moment(when) : string.Empty;
            _bkLastResult.Text = result.Failed
                ? T("Backup.LastResultFailed", at, Reason(result.Message))
                : T("Backup.LastResultOk", at, result.Message ?? string.Empty);
            _bkLastResult.ForeColor = result.Failed ? UiTheme.Critical : UiTheme.TextSecondary;
        }
        else
        {
            _bkLastResult.Text = string.Empty;
        }

        // ---- The list, the newest first.
        _bkList.RebuildKeeping<BackupEntry, long>(e => e.Set.BackupSetId, () =>
        {
            _bkList.Rows.Clear();
            foreach (var entry in overview.Backups)
            {
                var index = _bkList.Rows.Add(Moment(entry.Set.FinishedAt), Origin(entry), FileSize(entry.FileBytes),
                                             CheckText(entry), entry.Set.Path);
                _bkList.Rows[index].Tag = entry;
            }
        });

        // ---- The restore: on the server only, said where it is done otherwise.
        var onServer = BackupScreenRules.RestoreOfferedHere(_restoreContext);
        _bkRestore.Visible = onServer;
        _bkRestoreNote.ForeColor = onServer ? UiTheme.TextSecondary : UiTheme.Info;
        _bkRestoreNote.Text = onServer
            ? T("Backup.RestoreNoteServer")
            : T("Backup.RestoreNoteWorkstation", overview.Server.ServerMachine);

        RefreshBackupActions();
    }

    private void Banner(bool critical, string text)
    {
        _bkBanner.BackColor = critical ? UiTheme.CriticalBackground : UiTheme.NormalBackground;
        _bkBanner.ForeColor = critical ? UiTheme.CriticalInk : UiTheme.Normal;
        _bkBanner.Text = (critical ? "⚠  " : "✓  ") + text;
    }

    private void RefreshBackupActions()
    {
        var actions = BackupScreenRules.Actions(_bkBusy, _bkOverview, _bkList.CurrentRow?.Tag as BackupEntry);
        _bkBackupNow.Enabled = actions.BackupNow;
        _bkRestore.Enabled = actions.Restore;
        _bkSave.Enabled = actions.Save;
        _bkTestFolder.Enabled = actions.TestFolder;
    }

    /// <summary>
    /// The tab while a backup or a restore runs: the bar moving, the actions off, the status saying
    /// what is happening. Shown for as long as SQL Server works, minutes for a large database.
    /// </summary>
    private void ShowBackupRunning(bool running, string? text = null)
    {
        _bkBusy = running;
        _bkProgress.Visible = running;
        UseWaitCursor = running;
        if (text is not null)
        {
            _bkStatus.ForeColor = UiTheme.Info;
            _bkStatus.Text = text;
        }
        RefreshBackupActions();
    }

    // ---- Texts --------------------------------------------------------------------------------------

    private static string Moment(DateTime at) => at.ToString("g", CultureInfo.CurrentCulture);

    /// <summary>A sentence of the tab (BackupScreenRules), its moments and its age written out.</summary>
    private string Line(BackupLine line) =>
        T(line.Key, [.. line.Arguments.Select(a => a switch
        {
            DateTime at => Moment(at),
            BackupLine inner => Line(inner),
            _ => a
        })]);

    private string FileSize(long bytes)
    {
        var (key, value, format) = BackupScreenRules.Size(bytes);
        return T(key, value.ToString(format, CultureInfo.CurrentCulture));
    }

    private string Origin(BackupEntry entry)
    {
        // Who: the description we wrote for a manual one, SQL Server's login for one we did not write.
        var (kindKey, who) = BackupScreenRules.Origin(entry);
        return who is null ? T(kindKey) : $"{T(kindKey)} — {who}";
    }

    private string CheckText(BackupEntry entry) => T(BackupScreenRules.CheckKey(entry));

    /// <summary>
    /// A failure recorded as a resource key (by MarocLIS or the server's task), in words: the
    /// messages name the folder, which is the schedule's.
    /// </summary>
    private string Reason(string? key) =>
        BackupScreenRules.Reason(key, _bkOverview?.Settings.Folder) is { } error ? Localizer.Describe(error) : string.Empty;

    // ---- Actions ------------------------------------------------------------------------------------

    private BackupSettings? ReadBackupSettings()
    {
        // Every piece typed must be a time, a day at least ticked (BackupScreenRules.Read).
        var read = BackupScreenRules.Read(_bkEnabled.Checked, _bkTimes.Text,
                                          _bkDays.Where(b => b.Checked).Select(b => (DayOfWeek)b.Tag!),
                                          (int)_bkRetention.Value, _bkFolder.Text);
        if (read.IsFailure)
        {
            _dialogs.ShowFailure(read, T("Backup.Tab"));
            return null;
        }

        return read.Value;
    }

    private async Task SaveBackupSettingsAsync()
    {
        if (!BackupsOffered || ReadBackupSettings() is not { } settings) return;

        ShowBackupRunning(true, T("Backup.CheckingFolder"));
        Result saved;
        try
        {
            saved = await _backups!.SaveSettingsAsync(settings);

            // A new folder SQL Server cannot write to yet: the server's task creates a missing local
            // folder and opens it to SQL Server, so saving anyway is a real choice, not a mistake.
            if (BackupScreenRules.MaySaveAnyway(saved)
                && _dialogs.Confirm(T("Backup.SaveAnyway", Localizer.Describe(saved.Errors)), T("Backup.Tab")))
            {
                saved = await _backups.SaveSettingsAsync(settings, checkFolder: false);
            }
        }
        finally
        {
            ShowBackupRunning(false, string.Empty);
        }

        if (saved.IsFailure)
        {
            _bkStatus.ForeColor = UiTheme.Critical;
            _bkStatus.Text = Localizer.Describe(saved.Errors);
            return;
        }

        await LoadBackupsAsync();
        _bkStatus.ForeColor = UiTheme.Normal;
        _bkStatus.Text = T("Backup.Saved");
    }

    private async Task TestBackupFolderAsync()
    {
        if (!BackupsOffered) return;

        ShowBackupRunning(true, T("Backup.CheckingFolder"));
        Result tested;
        try
        {
            tested = await _backups!.TestFolderAsync(_bkFolder.Text);
        }
        finally
        {
            ShowBackupRunning(false, string.Empty);
        }

        _bkStatus.ForeColor = tested.IsSuccess ? UiTheme.Normal : UiTheme.Critical;
        _bkStatus.Text = tested.IsSuccess ? T("Backup.FolderOk", _bkFolder.Text.Trim()) : Localizer.Describe(tested.Errors);

        // SQL Server's own answer is the useful part, and it does not fit on the state line.
        if (tested.IsFailure) _dialogs.ShowFailure(tested, T("Backup.TestFolder"));
    }

    private async Task BackupNowAsync()
    {
        if (!BackupsOffered || _bkBusy) return;

        ShowBackupRunning(true, T("Backup.Running"));
        Result<BackupOutcome> outcome;
        try
        {
            outcome = await _backups!.BackupNowAsync();
        }
        finally
        {
            ShowBackupRunning(false, string.Empty);
        }

        await LoadBackupsAsync();

        if (outcome.IsFailure)
        {
            _bkStatus.ForeColor = UiTheme.Critical;
            _bkStatus.Text = Localizer.Describe(outcome.Errors);
            _dialogs.ShowFailure(outcome, T("Backup.BackupNow"));
            return;
        }

        // The status bar's « dernière sauvegarde » warning goes with the backup that answers it.
        if (FindForm() is Forms.MainShellForm shell) await shell.RefreshBackupWarningAsync();

        _bkStatus.ForeColor = UiTheme.Normal;
        _bkStatus.Text = T(BackupScreenRules.DoneKey(outcome.Value), BackupNaming.FileOf(outcome.Value.Path));
    }

    private async Task RestoreAsync()
    {
        if (!BackupsOffered || _bkBusy || _bkList.CurrentRow?.Tag is not BackupEntry entry) return;

        var allowed = await _backups!.CanRestoreAsync(_restoreContext);
        if (allowed.IsFailure)
        {
            // Not sysadmin from an ordinary process: on a server set up by MarocLIS's installer the
            // administrators are sysadmin, but only in a process started « en tant qu'administrateur ».
            if (allowed.FirstError.ResourceKey == "Backup_RestoreNoSqlRights" && !ServerMachine.IsElevated())
            {
                if (_dialogs.Confirm(T("Backup.RelaunchElevated", Localizer.Describe(allowed.Errors)), T("Backup.Restore"))
                    && ServerMachine.RelaunchElevated())
                {
                    FindForm()?.Close();
                }
                return;
            }

            _dialogs.ShowFailure(allowed, T("Backup.Restore"));
            return;
        }

        var name = await _backups.GetConfirmationNameAsync();
        var sessions = await _backups.GetSessionsAsync();
        if (name.IsFailure || sessions.IsFailure)
        {
            _dialogs.ShowFailure(name.IsFailure ? name : sessions, T("Backup.Restore"));
            return;
        }

        using var dialog = new BackupRestoreDialog(Localizer, entry, Origin(entry), CheckText(entry), FileSize(entry.FileBytes),
                                                   sessions.Value, name.Value);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        ShowBackupRunning(true, T("RestoreStep.Checking"));
        Result<RestoreOutcome> restored;
        try
        {
            var progress = new Progress<RestoreStep>(step => _bkStatus.Text = T($"RestoreStep.{step}"));
            restored = await _backups.RestoreAsync(
                new RestoreRequest(entry.Set.BackupSetId, dialog.TypedName, _restoreContext), progress);
        }
        finally
        {
            ShowBackupRunning(false);
        }

        if (restored.IsFailure)
        {
            _bkStatus.ForeColor = UiTheme.Critical;
            _bkStatus.Text = Localizer.Describe(restored.Errors);
            _dialogs.ShowFailure(restored, T("Backup.Restore"));
            await LoadBackupsAsync();
            return;
        }

        // Every connection of this process was ended by the restore, and the session it opened
        // belongs to a database that no longer exists: the application starts again on the
        // restored one, through its sign-in.
        _dialogs.Info(T("Backup.RestoreDone", restored.Value.RestoredPath, restored.Value.SafetyBackupPath), T("Backup.Restore"));
        if (FindForm() is Forms.MainShellForm shell) shell.CloseForRestart();
        else FindForm()?.Close();
    }
}
