using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Forms;

/// <summary>
/// The list of keyboard shortcuts, opened with Maj+F1, where each user can change their own.
/// <para>
/// The operator selects a line and presses the keys they want. A combination already used by
/// another line moves to the new one and the other is left without a shortcut, with a note saying
/// so, rather than refusing: refusing makes swapping two shortcuts a three-step puzzle.
/// </para>
/// </summary>
public sealed class ShortcutsDialog : LocalizedForm
{
    private readonly KeyboardShortcuts _shortcuts;
    private readonly IReadOnlyList<ShortcutAction> _actions;
    private readonly Dictionary<string, Keys> _chosen = new(StringComparer.Ordinal);

    private readonly Label _hint = new();
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Label _status = new();
    private readonly Button _restore = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _save = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _cancel = UiTheme.SecondaryButton(string.Empty);

    public ShortcutsDialog(KeyboardShortcuts shortcuts, IReadOnlyList<ShortcutAction> actions,
                           ILocalizer localizer, ICultureManager cultureManager)
        : base(localizer, cultureManager)
    {
        _shortcuts = shortcuts;
        _actions = actions;

        foreach (var action in actions) _chosen[action.Id] = shortcuts.For(action.Id);

        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(620, 600);
        BackColor = UiTheme.SurfaceRaised;
        KeyPreview = true;

        BuildLayout();

        _grid.KeyDown += OnGridKeyDown;
        _restore.Click += (_, _) => RestoreDefaults();
        _save.Click += (_, _) => Save();
        _cancel.DialogResult = DialogResult.Cancel;
        CancelButton = _cancel;
    }

    private void BuildLayout()
    {
        var gutter = UiTheme.Gutter;
        Padding = new Padding(gutter);

        _hint.Font = UiTheme.Small;
        _hint.ForeColor = UiTheme.TextSecondary;
        _hint.AutoSize = false;
        _hint.Dock = DockStyle.Top;
        _hint.Height = 58;

        _grid.Columns.Add("action", string.Empty);
        _grid.Columns.Add("keys", string.Empty);
        _grid.Columns["action"]!.FillWeight = 70;
        _grid.Columns["keys"]!.FillWeight = 30;
        _grid.Columns["keys"]!.DefaultCellStyle.Font = UiTheme.BodyBold;

        _status.Font = UiTheme.Small;
        _status.ForeColor = UiTheme.Abnormal;
        _status.AutoSize = false;
        _status.Dock = DockStyle.Bottom;
        _status.Height = 40;
        _status.TextAlign = ContentAlignment.MiddleLeft;

        var actions = new Panel { Dock = DockStyle.Bottom, Height = UiTheme.ButtonHeight + 8, Tag = UiTags.OwnLayout };
        _restore.Bounds = new Rectangle(0, 8, 270, UiTheme.ButtonHeight);
        _restore.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _cancel.Bounds = new Rectangle(ClientSize.Width - 2 * gutter - 120, 8, 120, UiTheme.ButtonHeight);
        _save.Bounds = new Rectangle(_cancel.Left - 10 - 150, 8, 150, UiTheme.ButtonHeight);
        _cancel.Anchor = _save.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        actions.Width = ClientSize.Width - 2 * gutter;
        actions.Controls.AddRange([_restore, _save, _cancel]);

        // Fill first so it docks last, between the hint above and the message and buttons below.
        Controls.AddRange([_grid, _status, actions, _hint]);
    }

    protected override void ApplyTexts()
    {
        Text = T("Shortcut.Title");
        _hint.Text = T("Shortcut.Hint");
        _grid.Columns["action"]!.HeaderText = T("Shortcut.Action");
        _grid.Columns["keys"]!.HeaderText = T("Shortcut.Keys");
        _restore.Text = T("Shortcut.Restore");
        _save.Text = T("Action.Save");
        _cancel.Text = T("Action.Cancel");
        Render();
    }

    private void Render()
    {
        var current = _grid.CurrentRow?.Index ?? 0;

        _grid.Rows.Clear();
        foreach (var action in _actions)
        {
            var index = _grid.Rows.Add(action.Label, KeyboardShortcuts.Format(_chosen[action.Id], Localizer));
            _grid.Rows[index].Tag = action.Id;
        }

        if (_grid.Rows.Count > 0)
            _grid.CurrentCell = _grid.Rows[Math.Clamp(current, 0, _grid.Rows.Count - 1)].Cells[0];
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (_grid.CurrentRow?.Tag is not string id) return;

        var code = e.KeyCode;

        // Moving through the list and leaving the dialog keep their usual keys.
        if (e.Modifiers == Keys.None
            && code is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
                    or Keys.Tab or Keys.Escape or Keys.Return)
            return;

        e.Handled = true;
        e.SuppressKeyPress = true;

        if (e.Modifiers == Keys.None && code is Keys.Delete or Keys.Back)
        {
            _chosen[id] = Keys.None;
            _status.Text = string.Empty;
            Render();
            return;
        }

        if (code is Keys.ControlKey or Keys.ShiftKey or Keys.Menu) return;   // still composing

        var keys = e.KeyData;
        if (!KeyboardShortcuts.IsAssignable(keys))
        {
            _status.Text = T("Shortcut.NeedsModifier");
            return;
        }

        _status.Text = string.Empty;

        var previous = _chosen.FirstOrDefault(c => c.Key != id && c.Value == keys).Key;
        if (previous is not null)
        {
            _chosen[previous] = Keys.None;
            _status.Text = T("Shortcut.Moved", KeyboardShortcuts.Format(keys, Localizer),
                             _actions.First(a => a.Id == previous).Label);
        }

        _chosen[id] = keys;
        Render();
    }

    private void RestoreDefaults()
    {
        foreach (var action in _actions) _chosen[action.Id] = action.DefaultKeys;
        _status.Text = string.Empty;
        Render();
    }

    private void Save()
    {
        if (!_shortcuts.Save(_chosen))
        {
            _status.Text = T("Shortcut.SaveFailed");
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
