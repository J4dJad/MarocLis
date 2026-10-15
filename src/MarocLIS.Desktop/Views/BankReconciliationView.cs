using System.Globalization;
using MarocLIS.Application.Treasury;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Treasury;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The bank reconciliation (rapprochement bancaire) of one account over a period: the statement
/// lines on the left, the movements the application recorded and no line holds yet on the right,
/// and the balances underneath. The automatic match takes only what is certain; the rest is matched,
/// justified or undone by hand.
/// </summary>
public sealed class BankReconciliationView : LocalizedView
{
    private readonly TreasuryService _treasury;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<BankReconciliationView>? _logger;

    private readonly Label _heading = new();
    private readonly Label _accountLabel = new();
    private readonly ComboBox _account = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
    private readonly Label _fromLabel = new();
    private readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Short };
    private readonly Label _toLabel = new();
    private readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Short };
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _accounts = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _import = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _auto = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _match = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _justify = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _unmatch = UiTheme.SecondaryButton(string.Empty);
    private readonly CheckBox _openOnly = new() { AutoSize = true };

    private readonly Label _linesTitle = new();
    private readonly Label _movementsTitle = new();
    private readonly DataGridView _lines = UiTheme.Grid();
    private readonly DataGridView _movements = UiTheme.Grid();
    private readonly Label _summary = new();

    private IReadOnlyList<BankAccountRow> _accountRows = [];
    private ReconciliationData? _data;

    public BankReconciliationView(TreasuryService treasury, IUserDialogs dialogs, ILocalizer localizer,
                                  ICultureManager cultureManager, ILogger<BankReconciliationView>? logger = null)
        : base(localizer, cultureManager)
    {
        _treasury = treasury;
        _dialogs = dialogs;
        _logger = logger;

        var today = DateTime.Today;
        _from.Value = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
        _to.Value = today;

        BuildLayout();

        _refresh.Click += async (_, _) => await LoadAsync();
        _account.SelectedIndexChanged += async (_, _) => await LoadAsync();
        _openOnly.CheckedChanged += (_, _) => Render();
        _accounts.Click += async (_, _) => await RunBusyAsync(ManageAccountsAsync);
        _import.Click += async (_, _) => await RunBusyAsync(ImportAsync);
        _auto.Click += async (_, _) => await RunBusyAsync(AutoMatchAsync);
        _match.Click += async (_, _) => await RunBusyAsync(MatchAsync);
        _justify.Click += async (_, _) => await RunBusyAsync(JustifyAsync);
        _unmatch.Click += async (_, _) => await RunBusyAsync(UnmatchAsync);
        _lines.SelectionChanged += (_, _) => RefreshButtons();
        _movements.SelectionChanged += (_, _) => RefreshButtons();

        PermissionGate.Require(Permissions.Treasury.Manage, _accounts, _import, _auto, _match, _justify, _unmatch);
    }

    public override string Title => T("Nav.BankReconciliation");

    private StatementLineRow? CurrentLine => _lines.CurrentRow?.Tag as StatementLineRow;

    private BankAccountRow? CurrentAccount =>
        _account.SelectedIndex >= 0 && _account.SelectedIndex < _accountRows.Count ? _accountRows[_account.SelectedIndex] : null;

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var bar = new Panel { Dock = DockStyle.Top, Height = 110, BackColor = UiTheme.Surface };

        _heading.Font = UiTheme.PageTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.AutoSize = true;
        _heading.Location = new Point(0, 0);

        foreach (var label in new[] { _accountLabel, _fromLabel, _toLabel })
        {
            label.Font = UiTheme.Small;
            label.ForeColor = UiTheme.TextSecondary;
            label.AutoSize = true;
        }

        _accountLabel.Location = new Point(0, 34);
        _account.Bounds = new Rectangle(0, 52, 320, UiTheme.InputHeight);
        _fromLabel.Location = new Point(332, 34);
        _from.Bounds = new Rectangle(332, 52, 120, UiTheme.InputHeight);
        _toLabel.Location = new Point(462, 34);
        _to.Bounds = new Rectangle(462, 52, 120, UiTheme.InputHeight);
        _refresh.Bounds = new Rectangle(592, 50, 110, UiTheme.ButtonHeight);
        _accounts.Bounds = new Rectangle(712, 50, 150, UiTheme.ButtonHeight);
        _import.Bounds = new Rectangle(872, 50, 170, UiTheme.ButtonHeight);

        _auto.Bounds = new Rectangle(0, 82, 220, UiTheme.ButtonHeight);
        _match.Bounds = new Rectangle(230, 82, 170, UiTheme.ButtonHeight);
        _justify.Bounds = new Rectangle(410, 82, 150, UiTheme.ButtonHeight);
        _unmatch.Bounds = new Rectangle(570, 82, 170, UiTheme.ButtonHeight);
        _openOnly.Location = new Point(752, 86);

        bar.Controls.AddRange([_heading, _accountLabel, _account, _fromLabel, _from, _toLabel, _to, _refresh,
                               _accounts, _import, _auto, _match, _justify, _unmatch, _openOnly]);

        AddColumns(_lines, ("date", 9), ("label", 30), ("reference", 12), ("amount", 11), ("status", 18));
        AddColumns(_movements, ("date", 9), ("kind", 16), ("label", 26), ("reference", 13), ("amount", 11));
        _lines.Columns["amount"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _movements.Columns["amount"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;

        // Several movements against one line: a cheque remittance, a day's card batch split in two.
        _movements.MultiSelect = true;

        _lines.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _lines.Rows[e.RowIndex].Tag is not StatementLineRow row) return;
            if (!row.IsReconciled) return;
            e.CellStyle!.ForeColor = row.DifferenceMad != 0m ? UiTheme.Abnormal : UiTheme.Normal;
        };

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6 };
        split.Panel1.Controls.Add(_lines);
        split.Panel1.Controls.Add(SectionLabel(_linesTitle));
        split.Panel2.Controls.Add(_movements);
        split.Panel2.Controls.Add(SectionLabel(_movementsTitle));
        split.HandleCreated += (_, _) => split.SplitterDistance = Math.Max(100, split.Width / 2);

        _summary.Dock = DockStyle.Bottom;
        _summary.Height = 64;
        _summary.Font = UiTheme.Monospace;
        _summary.ForeColor = UiTheme.TextPrimary;
        _summary.Padding = new Padding(0, 6, 0, 0);

        Controls.Add(split);
        Controls.Add(_summary);
        Controls.Add(bar);
    }

    internal static Label SectionLabel(Label label)
    {
        label.Dock = DockStyle.Top;
        label.Height = 34;
        label.Font = UiTheme.SectionTitle;
        label.ForeColor = UiTheme.TextPrimary;
        return label;
    }

    internal static void AddColumns(DataGridView grid, params (string Name, int Weight)[] columns)
    {
        foreach (var (name, weight) in columns)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }
    }

    protected override void ApplyTexts()
    {
        _heading.Text = T("Nav.BankReconciliation");
        _accountLabel.Text = T("Treasury.BankAccount");
        _fromLabel.Text = T("Field.From");
        _toLabel.Text = T("Field.To");
        _refresh.Text = T("Action.Refresh");
        _accounts.Text = T("Treasury.BankAccounts");
        _import.Text = T("Treasury.ImportStatement");
        _auto.Text = T("Treasury.AutoMatch");
        _match.Text = T("Treasury.Match");
        _justify.Text = T("Treasury.Justify");
        _unmatch.Text = T("Treasury.Unmatch");
        _openOnly.Text = T("Treasury.OpenOnly");
        _linesTitle.Text = T("Treasury.StatementLines");
        _movementsTitle.Text = T("Treasury.UnmatchedMovements");

        string[] lineHeaders = [T("Field.Date"), T("Treasury.Label"), T("Payment.Reference"), T("Payment.Amount"), T("Field.Status")];
        for (var i = 0; i < lineHeaders.Length; i++) _lines.Columns[i].HeaderText = lineHeaders[i];

        string[] movementHeaders = [T("Field.Date"), T("Treasury.MovementKind"), T("Treasury.Label"), T("Payment.Reference"), T("Payment.Amount")];
        for (var i = 0; i < movementHeaders.Length; i++) _movements.Columns[i].HeaderText = movementHeaders[i];

        Render();
    }

    // ---- Loading -------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            await LoadAccountsAsync(ct);
            await LoadAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the bank reconciliation failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private async Task LoadAccountsAsync(CancellationToken ct = default, Guid? select = null)
    {
        var result = await _treasury.GetBankAccountsAsync(includeInactive: false, ct);
        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        var previous = select ?? CurrentAccount?.Id;
        _accountRows = result.Value;

        _account.BeginUpdate();
        _account.Items.Clear();
        foreach (var a in _accountRows) _account.Items.Add(a.Display);
        _account.EndUpdate();

        var index = _accountRows.ToList().FindIndex(a => a.Id == previous);
        if (_accountRows.Count > 0) _account.SelectedIndex = index >= 0 ? index : 0;
    }

    private async Task LoadAsync(CancellationToken ct = default)
    {
        if (CurrentAccount is not { } account)
        {
            _data = null;
            Render();
            return;
        }

        var result = await _treasury.GetReconciliationAsync(account.Id, DateOnly.FromDateTime(_from.Value.Date),
                                                            DateOnly.FromDateTime(_to.Value.Date), ct);
        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        _data = result.Value;
        Render();
    }

    private string Amount(decimal value) => Money.Of(value).Format(Localizer.CurrentLanguage);

    private string Kind(TreasuryMovementKind kind) => T($"TreasuryMovementKind.{kind}");

    private void Render()
    {
        _lines.Rows.Clear();
        _movements.Rows.Clear();

        if (_data is null)
        {
            _summary.Text = _accountRows.Count == 0 ? T("Treasury.NoBankAccount") : string.Empty;
            RefreshButtons();
            return;
        }

        foreach (var line in _data.Lines.Where(l => !_openOnly.Checked || !l.IsReconciled))
        {
            var status = !line.IsReconciled
                ? T("Treasury.Unreconciled")
                : string.Join(", ", line.MatchKinds.Select(Kind))
                  + (line.IsAutomatic ? $" ({T("Treasury.Automatic")})" : string.Empty)
                  + (line.DifferenceMad != 0m ? $" — {T("Treasury.Difference")} {Amount(line.DifferenceMad)}" : string.Empty);

            var index = _lines.Rows.Add(line.OperationDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                                        line.Label, line.Reference ?? string.Empty, Amount(line.AmountMad), status);
            _lines.Rows[index].Tag = line;
            if (line.MatchComment is { } comment) _lines.Rows[index].Cells["status"].ToolTipText = comment;
        }

        foreach (var movement in _data.UnmatchedMovements)
        {
            var label = movement.Kind == TreasuryMovementKind.CardBatch
                ? T("Treasury.CardBatchLabel", movement.Label)
                : movement.Label;

            var index = _movements.Rows.Add(movement.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                                            Kind(movement.Kind), label, movement.Reference ?? string.Empty,
                                            Amount(movement.AmountMad));
            _movements.Rows[index].Tag = movement;
        }

        var s = _data.Summary;
        string Row(string key, decimal value) => $"{T(key),-38}{Amount(value),18}";
        _summary.Text = string.Join(Environment.NewLine,
            $"{Row("Treasury.StatementBalance", s.StatementBalanceMad)}    {Row("Treasury.ReconciledBalance", s.ReconciledBalanceMad)}",
            $"{Row("Treasury.UnmatchedLines", s.UnmatchedStatementMad)}    {Row("Treasury.UnmatchedMovementsTotal", s.UnmatchedMovementsMad)}",
            $"{T("Treasury.OpeningAsOf", s.OpeningBalanceDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), Amount(s.OpeningBalanceMad))}   ·   " +
            $"{T("Treasury.UnmatchedCounts", s.UnmatchedStatementCount, s.UnmatchedMovementCount)}");

        RefreshButtons();
    }

    private void RefreshButtons()
    {
        var line = CurrentLine;
        var manage = PermissionGate.Allows(Permissions.Treasury.Manage);
        _auto.Enabled = manage && _data is not null;
        _match.Enabled = manage && line is { IsReconciled: false } && _movements.SelectedRows.Count > 0;
        _justify.Enabled = manage && line is { IsReconciled: false };
        _unmatch.Enabled = manage && line is { IsReconciled: true };
    }

    // ---- Actions ---------------------------------------------------------------------------------

    private async Task ManageAccountsAsync()
    {
        using var dialog = new BankAccountsDialog(Localizer, _dialogs, _treasury);
        dialog.ShowDialog(this);
        await LoadAccountsAsync(select: CurrentAccount?.Id);
        await LoadAsync();
    }

    private async Task ImportAsync()
    {
        if (CurrentAccount is not { } account)
        {
            _dialogs.Warn(T("Treasury.NoBankAccount"), _import.Text);
            return;
        }

        using var picker = new OpenFileDialog
        {
            Title = _import.Text,
            Filter = $"{T("Treasury.CsvFiles")} (*.csv;*.txt)|*.csv;*.txt",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        byte[] content;
        try
        {
            var info = new FileInfo(picker.FileName);
            if (info.Length > 10 * 1024 * 1024)
            {
                _dialogs.Warn(T("BankStatement_FileTooLarge"), _import.Text);
                return;
            }
            content = await File.ReadAllBytesAsync(info.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Warn($"{T("Documents.FileUnreadable")}\n{ex.Message}", _import.Text);
            return;
        }

        using var dialog = new BankStatementImportDialog(Localizer, _dialogs, _treasury, account, content,
                                                         picker.FileName);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        await LoadAccountsAsync(select: account.Id);
        await LoadAsync();
    }

    private async Task AutoMatchAsync()
    {
        if (CurrentAccount is not { } account) return;

        var result = await _treasury.AutoMatchAsync(account.Id, DateOnly.FromDateTime(_from.Value.Date),
                                                    DateOnly.FromDateTime(_to.Value.Date));
        if (result.IsFailure) { _dialogs.ShowFailure(result, _auto.Text); return; }

        _dialogs.Info(T("Treasury.AutoMatched", result.Value), _auto.Text);
        await LoadAsync();
    }

    private async Task MatchAsync()
    {
        if (CurrentLine is not { IsReconciled: false } line) return;

        var movements = _movements.SelectedRows.Cast<DataGridViewRow>()
                                  .Select(r => r.Tag).OfType<TreasuryMovement>().ToList();
        if (movements.Count == 0) return;

        var difference = Money.Round(line.AmountMad - movements.Sum(m => m.AmountMad));
        string? comment = null;
        if (difference != 0m)
        {
            comment = _dialogs.PromptForReason(T("Treasury.MatchDifferencePrompt", Amount(difference)), _match.Text);
            if (comment is null) return;
        }

        var result = await _treasury.MatchAsync(line.Id, movements, comment);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _match.Text); return; }

        await LoadAsync();
    }

    private async Task JustifyAsync()
    {
        if (CurrentLine is not { IsReconciled: false } line) return;

        var comment = _dialogs.PromptForReason(T("Treasury.JustifyPrompt", line.Label, Amount(line.AmountMad)),
                                               _justify.Text);
        if (comment is null) return;

        var result = await _treasury.JustifyAsync(line.Id, comment);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _justify.Text); return; }

        await LoadAsync();
    }

    private async Task UnmatchAsync()
    {
        if (CurrentLine is not { IsReconciled: true } line) return;
        if (!_dialogs.Confirm(T("Treasury.UnmatchConfirm", line.Label, Amount(line.AmountMad)), _unmatch.Text)) return;

        var result = await _treasury.UnmatchAsync(line.Id);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _unmatch.Text); return; }

        await LoadAsync();
    }
}
