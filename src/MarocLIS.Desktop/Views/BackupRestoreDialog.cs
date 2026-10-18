using System.Globalization;
using MarocLIS.Application.Administration;
using MarocLIS.Desktop.Forms;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The confirmation of a restore: which backup, what it holds, which workstations will be cut off,
/// and the laboratory's name typed by hand. The restore button stays off until the name matches —
/// the service checks it again — because the one thing this dialog must prevent is a restore
/// confirmed by reflex, with Enter, over a morning of results.
/// </summary>
internal sealed class BackupRestoreDialog : Form
{
    private readonly TextBox _name = UiTheme.Input(360);
    private readonly Button _restore;
    private readonly string _expected;

    /// <summary>The name as typed, which the service compares again.</summary>
    internal string TypedName => _name.Text;

    internal BackupRestoreDialog(ILocalizer localizer, BackupEntry backup, string origin, string check, string size,
                                 IReadOnlyList<DatabaseSessionRow> sessions, string laboratoryName)
    {
        _expected = laboratoryName;

        Text = localizer["Backup.RestoreTitle"];
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(640, 520);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var lead = new Label
        {
            Text = localizer["Backup.RestoreLead"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(20, 14, 600, 36)
        };

        var warning = new Label
        {
            Text = "⚠  " + localizer["Backup.RestoreWarning"],
            Font = UiTheme.SmallBold,
            ForeColor = UiTheme.CriticalInk,
            BackColor = UiTheme.CriticalBackground,
            AutoSize = false,
            Padding = new Padding(10, 6, 10, 6),
            Bounds = new Rectangle(20, 56, 600, 62),
            UseMnemonic = false
        };

        var what = UiTheme.FieldLabel(localizer["Backup.RestoreWhich"]);
        what.Location = new Point(20, 128);

        var finished = backup.Set.FinishedAt.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture);
        var details = new Label
        {
            Text = string.Join(Environment.NewLine,
                localizer.Format("Backup.RestoreDate", finished),
                localizer.Format("Backup.RestoreOrigin", origin),
                localizer.Format("Backup.RestoreSize", size, check),
                localizer.Format("Backup.RestoreFile", backup.Set.Path)),
            Font = UiTheme.Body,
            ForeColor = UiTheme.TextPrimary,
            AutoSize = false,
            Bounds = new Rectangle(20, 148, 600, 82),
            UseMnemonic = false
        };

        var connected = UiTheme.FieldLabel(sessions.Count == 0
            ? localizer["Backup.RestoreNoSessions"]
            : localizer.Format("Backup.RestoreSessions", sessions.Select(s => s.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count()));
        connected.Location = new Point(20, 238);

        // One line per connection: a workstation often holds several (the screen, the session
        // check, the analyser host), and the host name is what the laboratory knows it by.
        var list = new ListBox
        {
            Bounds = new Rectangle(20, 258, 600, 92),
            Font = UiTheme.Small,
            IntegralHeight = false,
            SelectionMode = SelectionMode.None,
            BorderStyle = BorderStyle.FixedSingle
        };
        foreach (var session in sessions.GroupBy(s => (s.Host ?? "?", s.Program ?? string.Empty, s.Login ?? string.Empty)))
        {
            var since = session.Min(s => s.LoginTime).ToString("g", CultureInfo.CurrentCulture);
            list.Items.Add(localizer.Format("Backup.RestoreSessionLine", session.Key.Item1, session.Key.Item2,
                                            session.Key.Item3, since, session.Count()));
        }
        list.Visible = sessions.Count > 0;

        var confirm = UiTheme.FieldLabel(localizer.Format("Backup.RestoreTypeName", laboratoryName));
        confirm.AutoSize = false;
        confirm.Bounds = new Rectangle(20, 362, 600, 36);
        _name.Location = new Point(20, 400);

        _restore = UiTheme.PrimaryButton(localizer["Backup.RestoreConfirm"]);
        _restore.DialogResult = DialogResult.OK;
        _restore.Enabled = false;

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.DialogResult = DialogResult.Cancel;

        _name.TextChanged += (_, _) => _restore.Enabled = BackupService.ConfirmationMatches(_expected, _name.Text);

        Controls.AddRange([lead, warning, what, details, connected, list, confirm, _name, _restore, cancel]);

        // No AcceptButton: Enter in the name box must not restore.
        CancelButton = cancel;

        PatientDialogFrame.Apply(this, Text, lead.Text, lead, null, cancel, _restore);
        ButtonIcons.Apply(this, localizer);
        RtlLayout.Apply(this, localizer.CurrentLanguage);

        // The danger colour once it may be pressed: this button is not « Enregistrer ». Subscribed
        // last, after the primary style's own handler (which the frame may set again), so it wins;
        // disabled, the button keeps the pale fill on which its grey caption reads.
        _restore.EnabledChanged += (_, _) =>
        {
            if (!_restore.Enabled) return;
            _restore.BackColor = UiTheme.Critical;
            _restore.FlatAppearance.BorderColor = UiTheme.Critical;
            _restore.FlatAppearance.MouseOverBackColor = UiTheme.CriticalInk;
            _restore.FlatAppearance.MouseDownBackColor = UiTheme.CriticalInk;
        };

        Shown += (_, _) => _name.Focus();
    }
}
