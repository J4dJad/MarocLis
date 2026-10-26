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

    // Withdrawing a whole import is rare and heavy: under « Plus d'actions ▾ ».
    private readonly ContextMenuStrip _moreMenu = new();

    // Kept to translate it with the screen: it stayed « Plus d'actions » in Arabic and English.
    private Button? _more;
    private readonly MenuAction _withdrawImport = new();

    // « Lignes ouvertes seulement » is kept as the tick box it was, off screen; a chip sets it.
    private readonly CheckBox _openOnly = new() { AutoSize = true };
    private readonly ChipButton _openChip = new() { Height = 30 };

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

        var (from, to) = BankReconciliationScreenRules.DefaultPeriod(DateOnly.FromDateTime(DateTime.Today));
        _from.Value = from.ToDateTime(TimeOnly.MinValue);
        _to.Value = to.ToDateTime(TimeOnly.MinValue);

        BuildLayout();

        _refresh.Click += async (_, _) => await ReloadAsync();
        _account.SelectedIndexChanged += async (_, _) => await ReloadAsync();
        _openOnly.CheckedChanged += (_, _) => Render();
        _openOnly.CheckedChanged += (_, _) => RenderChip();
        _accounts.Click += async (_, _) => await RunBusyAsync(ManageAccountsAsync);
        _import.Click += async (_, _) => await RunBusyAsync(ImportAsync);
        _auto.Click += async (_, _) => await RunBusyAsync(AutoMatchAsync);
        _match.Click += async (_, _) => await RunBusyAsync(MatchAsync);
        _justify.Click += async (_, _) => await RunBusyAsync(JustifyAsync);
        _unmatch.Click += async (_, _) => await RunBusyAsync(UnmatchAsync);
        _withdrawImport.Click += async (_, _) => await RunBusyAsync(WithdrawImportAsync);
        _lines.SelectionChanged += (_, _) => RefreshButtons();
        _movements.SelectionChanged += (_, _) => RefreshButtons();

        PermissionGate.Require(Permissions.Treasury.Manage, _accounts, _import, _auto, _match, _justify, _unmatch,
                               _withdrawImport);
    }

    public override string Title => T("Nav.BankReconciliation");

    private StatementLineRow? CurrentLine => _lines.CurrentRow?.Tag as StatementLineRow;

    private BankAccountRow? CurrentAccount =>
        _account.SelectedIndex >= 0 && _account.SelectedIndex < _accountRows.Count ? _accountRows[_account.SelectedIndex] : null;

    private void BuildLayout()
    {
        Padding = new Padding(24, 20, 24, 16);

        _more = FinanceUi.MoreButton(_moreMenu, Localizer, _withdrawImport);
        var header = FinanceUi.PageHeader(_heading, null, _more, _accounts, _import);

        _account.Width = 320;
        _from.Width = 120;
        _to.Width = 120;
        _openChip.Click += (_, _) => _openOnly.Checked = !_openOnly.Checked;

        var filters = FinanceUi.FilterRow(FinanceUi.Inline(_accountLabel, _account), FinanceUi.Inline(_fromLabel, _from),
                                          FinanceUi.Inline(_toLabel, _to), _refresh, _openChip);

        AddColumns(_lines, ("date", 9), ("label", 30), ("reference", 12), ("amount", 11), ("status", 18));
        AddColumns(_movements, ("date", 9), ("kind", 16), ("label", 26), ("reference", 13), ("amount", 11));
        foreach (var grid in new[] { _lines, _movements })
        {
            grid.Columns["amount"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.Columns["amount"]!.DefaultCellStyle.Font = UiTheme.Monospace;
            grid.Columns["date"]!.DefaultCellStyle.Font = UiTheme.Monospace;
            grid.Columns["reference"]!.DefaultCellStyle.Font = UiTheme.Monospace;
        }

        // Several movements against one line: a cheque remittance, a day's card batch split in two.
        _movements.MultiSelect = true;

        _lines.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _lines.Rows[e.RowIndex].Tag is not StatementLineRow row) return;
            if (!row.IsReconciled) return;
            e.CellStyle!.ForeColor = row.DifferenceMad != 0m ? UiTheme.Abnormal : UiTheme.Normal;
        };

        // A line still open as a pill; a reconciled one keeps its text, which names the match.
        FinanceUi.PillColumn(_lines, "status", row => row.Tag is StatementLineRow { IsReconciled: false }
            ? FinanceUi.PillWarning
            : null);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 12,
            BackColor = UiTheme.Surface
        };
        split.Panel1.Controls.Add(new FinanceCard(_lines, FinanceUi.CardHeader(_linesTitle, null)));
        split.Panel2.Controls.Add(new FinanceCard(_movements, FinanceUi.CardHeader(_movementsTitle, null)));
        split.HandleCreated += (_, _) => split.SplitterDistance = Math.Max(100, split.Width / 2);

        // The balances, then the actions on the selected line; the automatic match at the far end.
        _summary.Dock = DockStyle.Fill;
        _summary.Font = UiTheme.Monospace;
        _summary.ForeColor = UiTheme.TextPrimary;
        _summary.Padding = new Padding(16, 8, 16, 0);
        _summary.RightToLeft = RightToLeft.No;

        var bar = FinanceUi.ActionBar(null, [_match, _justify, _unmatch], _auto);
        var footer = new FinanceCard(_summary, footer: bar) { Dock = DockStyle.Bottom, Height = 132 };
        var gap = new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = UiTheme.Surface, Tag = UiTags.OwnLayout };

        Controls.Add(split);
        Controls.Add(gap);
        Controls.Add(footer);
        Controls.Add(filters);
        Controls.Add(header);
    }

    private void RenderChip()
    {
        _openChip.Text = BankReconciliationScreenRules.OpenOnlyChip(_data, T);
        _openChip.Selected = _openOnly.Checked;
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
        if (_more is not null) _more.Text = T("ResultEntry.MoreActions");
        _accounts.Text = T("Treasury.BankAccounts");
        _import.Text = T("Treasury.ImportStatement");
        _auto.Text = T("Treasury.AutoMatch");
        _match.Text = T("Treasury.Match");
        _justify.Text = T("Treasury.Justify");
        _unmatch.Text = T("Treasury.Unmatch");
        _withdrawImport.Text = T("Treasury.WithdrawImport");
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
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the bank reconciliation failed.");
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
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

        if (BankReconciliationScreenRules.Keep(_accountRows, previous) is { } kept)
            _account.SelectedIndex = _accountRows.ToList().IndexOf(kept);
    }

    /// <summary>
    /// <see cref="LoadAsync"/> from a button or the account list. Choosing the account while the
    /// screen opens runs it too: leaving the screen then let its cancellation escape as an
    /// unexpected error.
    /// </summary>
    private async Task ReloadAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the bank reconciliation failed.");
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
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

    private string Kind(TreasuryMovementKind kind) => T(BankReconciliationScreenRules.KindKey(kind));

    private void Render()
    {
        _movements.Rows.Clear();
        RenderChip();

        if (_data is null)
        {
            _lines.Rows.Clear();
            _summary.Text = _accountRows.Count == 0 ? T("Treasury.NoBankAccount") : string.Empty;
            RefreshButtons();
            return;
        }

        // Kept on the line just matched or justified, or on the next one when « open only » hides it:
        // the list went back to its first line after each one.
        _lines.RebuildKeeping<StatementLineRow, Guid>(r => r.Id, () =>
        {
            _lines.Rows.Clear();
            foreach (var line in BankReconciliationScreenRules.Visible(_data, _openOnly.Checked))
            {
                var status = BankReconciliationScreenRules.LineStatus(line, T, Amount);

                var index = _lines.Rows.Add(BankReconciliationScreenRules.Date(line.OperationDate),
                                            line.Label, line.Reference ?? string.Empty, Amount(line.AmountMad), status);
                _lines.Rows[index].Tag = line;
                if (line.MatchComment is { } comment) _lines.Rows[index].Cells["status"].ToolTipText = comment;
            }
        });

        foreach (var movement in _data.UnmatchedMovements)
        {
            var label = BankReconciliationScreenRules.MovementLabel(movement, T);

            var index = _movements.Rows.Add(BankReconciliationScreenRules.Date(movement.Date),
                                            Kind(movement.Kind), label, movement.Reference ?? string.Empty,
                                            Amount(movement.AmountMad));
            _movements.Rows[index].Tag = movement;
        }

        var s = _data.Summary;
        var b = BankReconciliationScreenRules.Balances(s);
        string Row(ReconciliationBalance balance) => $"{T(balance.Key),-38}{Amount(balance.Amount),18}";
        _summary.Text = string.Join(Environment.NewLine,
            $"{Row(b[0])}    {Row(b[1])}",
            $"{Row(b[2])}    {Row(b[3])}",
            $"{BankReconciliationScreenRules.OpeningLine(s, T, Amount)}   ·   {BankReconciliationScreenRules.CountsLine(s, T)}");

        RefreshButtons();
    }

    private void RefreshButtons()
    {
        var actions = BankReconciliationScreenRules.Actions(p => PermissionGate.Allows(p), _data, CurrentLine, _movements.SelectedRows.Count);
        _auto.Enabled = actions.AutoMatch;
        _match.Enabled = actions.Match;
        _justify.Enabled = actions.Justify;
        _unmatch.Enabled = actions.Unmatch;
        _withdrawImport.Enabled = actions.WithdrawImport;
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
            Title = UserDialogs.CaptionOf(_import.Text, _import.Text),
            Filter = $"{T("Treasury.CsvFiles")} (*.csv;*.txt)|*.csv;*.txt",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        byte[] content;
        try
        {
            var info = new FileInfo(picker.FileName);
            if (BankReconciliationScreenRules.CheckFile(info.Length) is { } refusal)
            {
                _dialogs.Warn(T(refusal.Key), _import.Text);
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

        // The service refuses a movement going the other way than the line; said before the
        // difference is asked for, not after the operator has explained it.
        var check = BankReconciliationScreenRules.CheckMatch(line, movements);
        if (check.RefusalKey is { } refused)
        {
            _dialogs.Warn(T(refused), _match.Text);
            return;
        }

        string? comment = null;
        if (check.NeedsReason)
        {
            comment = _dialogs.PromptForReason(BankReconciliationScreenRules.MatchDifferencePrompt(check.Difference, T, Amount), _match.Text,
                                               maxLength: BankReconciliationScreenRules.CommentMaxLength);
            if (comment is null) return;
        }

        var result = await _treasury.MatchAsync(line.Id, movements, comment);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _match.Text); return; }

        await LoadAsync();
    }

    private async Task JustifyAsync()
    {
        if (CurrentLine is not { IsReconciled: false } line) return;

        var comment = _dialogs.PromptForReason(BankReconciliationScreenRules.JustifyPrompt(line, T, Amount),
                                               _justify.Text, maxLength: BankReconciliationScreenRules.CommentMaxLength);
        if (comment is null) return;

        var result = await _treasury.JustifyAsync(line.Id, comment);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _justify.Text); return; }

        await LoadAsync();
    }

    /// <summary>
    /// Withdraws the whole import the selected line came with, read with a wrong mapping. The
    /// service refuses while a line of it is reconciled.
    /// </summary>
    private async Task WithdrawImportAsync()
    {
        if (CurrentLine is not { } line) return;
        if (!_dialogs.Confirm(BankReconciliationScreenRules.WithdrawQuestion(line, T, Amount), _withdrawImport.Text)) return;

        var result = await _treasury.WithdrawImportAsync(line.Id);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _withdrawImport.Text); return; }

        _dialogs.Info(T("Treasury.ImportWithdrawn", result.Value), _withdrawImport.Text);
        await LoadAccountsAsync(select: CurrentAccount?.Id);
        await LoadAsync();
    }

    private async Task UnmatchAsync()
    {
        if (CurrentLine is not { IsReconciled: true } line) return;
        if (!_dialogs.Confirm(BankReconciliationScreenRules.UnmatchQuestion(line, T, Amount), _unmatch.Text)) return;

        var result = await _treasury.UnmatchAsync(line.Id);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _unmatch.Text); return; }

        await LoadAsync();
    }
}
