using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Accounting;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Every journal and account of the accounting export, sales, receipts, payroll and purchases alike,
/// on one screen: a line per setting, the value typed in place. A department, site or payment
/// method left blank follows the default account of its section.
/// </summary>
public sealed class AccountingSettingsView : LocalizedView
{
    private const string ColSection = "section", ColLabel = "label", ColValue = "value";

    private readonly AccountingExportService _accounting;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<AccountingSettingsView>? _logger;

    private readonly Label _heading = new();
    private readonly Label _notice = new();
    private readonly Button _save = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _reload = UiTheme.SecondaryButton(string.Empty);
    private readonly DataGridView _grid = UiTheme.Grid();

    private IReadOnlyList<AccountingSettingRow> _rows = [];

    public AccountingSettingsView(AccountingExportService accounting, IUserDialogs dialogs, ILocalizer localizer,
                                  ICultureManager cultureManager, ILogger<AccountingSettingsView>? logger = null)
        : base(localizer, cultureManager)
    {
        _accounting = accounting;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();

        _reload.Click += async (_, _) => await RunBusyAsync(() => ActivateAsync());
        _save.Click += async (_, _) => await RunBusyAsync(SaveAsync);

        PermissionGate.Require(Permissions.Accounting.Export, _save, _reload);
    }

    public override string Title => T("Nav.AccountingSettings");

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var bar = new Panel { Dock = DockStyle.Top, Height = 104, BackColor = UiTheme.Surface };

        _heading.Font = UiTheme.PageTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.AutoSize = true;
        _heading.Location = new Point(0, 0);

        _reload.Bounds = new Rectangle(0, 34, 140, UiTheme.ButtonHeight);
        _save.Bounds = new Rectangle(150, 34, 160, UiTheme.ButtonHeight);

        _notice.Font = UiTheme.Small;
        _notice.ForeColor = UiTheme.Abnormal;
        _notice.Bounds = new Rectangle(0, 70, 900, 32);

        bar.Controls.AddRange([_heading, _reload, _save, _notice]);

        _grid.ReadOnly = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = ColSection, FillWeight = 16, ReadOnly = true,
                                                          SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = ColLabel, FillWeight = 50, ReadOnly = true,
                                                          SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = ColValue, FillWeight = 24, MaxInputLength = 20,
                                                          SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns[2].DefaultCellStyle.Font = UiTheme.Monospace;
        _grid.Dock = DockStyle.Fill;

        Controls.Add(_grid);
        Controls.Add(bar);
    }

    protected override void ApplyTexts()
    {
        _heading.Text = T("Nav.AccountingSettings");
        _reload.Text = T("Action.Refresh");
        _save.Text = T("Action.Save");
        _notice.Text = T("Accounting.SettingsNotice");
        _grid.Columns[0].HeaderText = T("Accounting.Section");
        _grid.Columns[1].HeaderText = T("Accounting.Setting");
        _grid.Columns[2].HeaderText = T("Accounting.Value");

        if (_rows.Count > 0) Render(Values());
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var rows = await _accounting.GetSettingsAsync(ct);
            if (rows.IsFailure) { _dialogs.ShowFailure(rows); return; }

            _rows = rows.Value;
            Render(null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the accounting settings failed.");
        }
    }

    private sealed record ModeChoice(string Value, string Display);

    private void Render(IReadOnlyDictionary<string, string?>? typed)
    {
        _grid.Rows.Clear();

        foreach (var row in _rows)
        {
            var argument = row.Argument is null ? null : row.ArgumentKey ? T(row.Argument) : row.Argument;
            var label = argument is null ? T(row.LabelKey) : T(row.LabelKey, argument);
            var value = typed is not null && typed.TryGetValue(row.Key, out var v) ? v : row.Value;

            var index = _grid.Rows.Add(T($"AccountingSection.{row.Section}"), label, value ?? string.Empty);
            var gridRow = _grid.Rows[index];
            gridRow.Tag = row;

            if (row.Kind == AccountingSettingKind.PatientMode)
            {
                var cell = new DataGridViewComboBoxCell
                {
                    DisplayMember = nameof(ModeChoice.Display),
                    ValueMember = nameof(ModeChoice.Value),
                    DataSource = Enum.GetValues<PatientThirdPartyMode>()
                                     .Select(m => new ModeChoice(m.ToString(), T($"PatientThirdPartyMode.{m}")))
                                     .ToList(),
                    FlatStyle = FlatStyle.Flat
                };
                gridRow.Cells[ColValue] = cell;
                cell.Value = value;
            }
        }
    }

    private IReadOnlyDictionary<string, string?> Values()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DataGridViewRow gridRow in _grid.Rows)
            if (gridRow.Tag is AccountingSettingRow row)
                values[row.Key] = gridRow.Cells[ColValue].Value?.ToString();
        return values;
    }

    private async Task SaveAsync()
    {
        try
        {
            _grid.EndEdit();
            var saved = await _accounting.SaveSettingsAsync(Values());
            if (saved.IsFailure) { _dialogs.ShowFailure(saved, _save.Text); return; }

            _dialogs.Info(T("Message.Saved"), _save.Text);
            await ActivateAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Saving the accounting settings failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), _save.Text);
        }
    }
}
