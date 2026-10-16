using System.Globalization;
using MarocLIS.Application.Treasury;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Treasury;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

/// <summary>The site's bank accounts: declared, corrected, closed.</summary>
internal sealed class BankAccountsDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly TreasuryService _treasury;
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Button _new = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _edit = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _close = UiTheme.SecondaryButton(string.Empty);

    internal BankAccountsDialog(ILocalizer localizer, IUserDialogs dialogs, TreasuryService treasury)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _treasury = treasury;

        Text = localizer["Treasury.BankAccounts"];
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(900, 420);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        BankReconciliationView.AddColumns(_grid, ("bank", 16), ("label", 18), ("rib", 22), ("account", 10),
                                          ("balance", 12), ("last", 10), ("active", 6));
        string[] headers =
        [
            localizer["Treasury.BankName"], localizer["Treasury.AccountLabel"], localizer["Treasury.Rib"],
            localizer["Treasury.AccountingAccount"], localizer["Treasury.StatementBalance"],
            localizer["Treasury.LastLine"], localizer["Field.Active"]
        ];
        for (var i = 0; i < headers.Length; i++) _grid.Columns[i].HeaderText = headers[i];
        _grid.Columns["balance"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _grid.Columns["rib"]!.DefaultCellStyle.Font = UiTheme.Monospace;

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 48 };
        _new.Text = localizer["Action.New"];
        _edit.Text = localizer["Action.Edit"];
        _close.Text = localizer["Action.Close"];
        _new.Bounds = new Rectangle(12, 8, 140, UiTheme.ButtonHeight);
        _edit.Bounds = new Rectangle(162, 8, 140, UiTheme.ButtonHeight);
        _close.Bounds = new Rectangle(312, 8, 140, UiTheme.ButtonHeight);
        _close.DialogResult = DialogResult.Cancel;
        CancelButton = _close;
        bar.Controls.AddRange([_new, _edit, _close]);

        Controls.Add(_grid);
        Controls.Add(bar);

        DialogFrame.Apply(this, localizer["Finance.Dialog.BankAccounts"], [_edit], _close, _new);

        _new.Click += async (_, _) => await EditAsync(null);
        _edit.Click += async (_, _) => await EditAsync(_grid.CurrentRow?.Tag as BankAccountRow);
        _grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await EditAsync(_grid.Rows[e.RowIndex].Tag as BankAccountRow); };
        Shown += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var result = await _treasury.GetBankAccountsAsync(includeInactive: true);
        if (result.IsFailure) { _dialogs.ShowFailure(result, Text); return; }

        _grid.Rows.Clear();
        foreach (var a in result.Value)
        {
            var index = _grid.Rows.Add(a.BankName, a.Label, a.Rib, a.AccountingAccount,
                                       Money.Of(a.StatementBalanceMad).Format(_localizer.CurrentLanguage),
                                       a.LastLineDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "—",
                                       a.IsActive ? _localizer["Action.Yes"] : _localizer["Action.No"]);
            _grid.Rows[index].Tag = a;
        }
    }

    private async Task EditAsync(BankAccountRow? row)
    {
        using var editor = new BankAccountEditor(_localizer, _dialogs, row);
        if (editor.ShowDialog(this) != DialogResult.OK || editor.Input is null) return;

        var saved = await _treasury.SaveBankAccountAsync(editor.Input);
        if (saved.IsFailure) { _dialogs.ShowFailure(saved, Text); return; }

        await LoadAsync();
    }
}

/// <summary>One bank account: bank, RIB, accounting account, opening balance.</summary>
internal sealed class BankAccountEditor : MasterDataEditor
{
    private readonly Guid? _id;
    private readonly TextBox _bank, _label, _rib, _accounting, _opening;
    private readonly DateTimePicker _openingDate;
    private readonly CheckBox _active;

    internal BankAccountInput? Input { get; private set; }

    internal BankAccountEditor(ILocalizer localizer, IUserDialogs dialogs, BankAccountRow? row)
        : base(localizer, dialogs, "Treasury.BankAccount", row?.Display, 560, 330)
    {
        _id = row?.Id;

        _bank = TextField("Treasury.BankName", 20, 14, 250, row?.BankName);
        _label = TextField("Treasury.AccountLabel", 290, 14, 250, row?.Label);
        _bank.MaxLength = BankAccount.NameMaxLength;
        _label.MaxLength = BankAccount.NameMaxLength;
        _rib = TextField("Treasury.Rib", 20, 68, 300, row?.Rib, latin: true, mono: true);
        _accounting = TextField("Treasury.AccountingAccount", 340, 68, 200, row?.AccountingAccount ?? "5141",
                                latin: true, mono: true);
        _rib.MaxLength = 40;
        _accounting.MaxLength = 12;
        _opening = TextField("Treasury.OpeningBalance", 20, 122, 180,
                             (row?.OpeningBalanceMad ?? 0m).ToString("0.00", CultureInfo.InvariantCulture),
                             latin: true, mono: true, right: true);

        Label("Treasury.OpeningBalanceDate", 220, 122);
        _openingDate = new DateTimePicker
        {
            Bounds = new Rectangle(220, 140, 160, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            Value = row is { OpeningBalanceDate: var d } && d != default ? d.ToDateTime(TimeOnly.MinValue) : DateTime.Today
        };
        Controls.Add(_openingDate);

        _active = Check("Field.Active", 400, 142, row?.IsActive ?? true);

        Notice(20, 180, 520, 70, "Treasury.BankAccountNotice");

        Buttons(272, 560);
        DialogFrame.Apply(this, localizer["Finance.Dialog.BankAccount"]);
    }

    protected override void Accept()
    {
        // The RIB check the domain makes, ahead of the round trip.
        if (!Domain.HumanResources.Employee.IsValidRib(BankAccount.NormalizeRib(_rib.Text)))
        {
            Dialogs.Warn(Localizer["BankAccount_RibInvalid"], Text);
            _rib.Focus();
            return;
        }

        if (DecimalOrNull(_opening) is not { } opening)
        {
            Dialogs.Warn(Localizer.Format("Result_NotANumber", _opening.Text), Text);
            _opening.Focus();
            return;
        }

        Input = new BankAccountInput(_id, _bank.Text.Trim(), _label.Text.Trim(), _rib.Text, _accounting.Text.Trim(),
                                     opening, DateOnly.FromDateTime(_openingDate.Value.Date), _active.Checked);
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Imports a bank statement: the columns are picked against the file itself, every line is shown
/// with what the import will do with it (new, already imported, unreadable), and nothing is written
/// until the operator confirms.
/// </summary>
internal sealed class BankStatementImportDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly TreasuryService _treasury;
    private readonly BankAccountRow _account;
    private readonly byte[] _content;
    private readonly string _fileName;

    private readonly ComboBox _delimiter = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
    private readonly NumericUpDown _skip = new() { Maximum = 100 };
    private readonly CheckBox _header = new() { AutoSize = true };
    private readonly NumericUpDown _date = new() { Maximum = 60 };
    private readonly NumericUpDown _valueDate = new() { Maximum = 60 };
    private readonly NumericUpDown _label = new() { Maximum = 60 };
    private readonly NumericUpDown _reference = new() { Maximum = 60 };
    private readonly NumericUpDown _amount = new() { Maximum = 60 };
    private readonly NumericUpDown _debit = new() { Maximum = 60 };
    private readonly NumericUpDown _credit = new() { Maximum = 60 };
    private readonly TextBox _dateFormat = UiTheme.Input(110);
    private readonly ComboBox _decimal = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
    private readonly Button _preview = UiTheme.SecondaryButton(string.Empty);
    private readonly DataGridView _raw = UiTheme.Grid();
    private readonly DataGridView _parsed = UiTheme.Grid();
    private readonly Label _summary = new();
    private readonly Button _import = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _cancel = UiTheme.SecondaryButton(string.Empty);

    private ImportPreview? _result;

    /// <summary>
    /// The mapping the preview on screen was read with: the import uses it, never the fields as
    /// they stand, and any change to a field takes the import away until the preview is redone.
    /// </summary>
    private BankStatementCsvMapping? _previewed;

    internal BankStatementImportDialog(ILocalizer localizer, IUserDialogs dialogs, TreasuryService treasury,
                                       BankAccountRow account, byte[] content, string fileName)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _treasury = treasury;
        _account = account;
        _content = content;
        _fileName = fileName;

        Text = $"{localizer["Treasury.ImportStatement"]} — {account.Display} — {Path.GetFileName(fileName)}";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(1080, 680);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        BuildLayout();
        Apply(BankStatementCsvMapping.FromJson(account.CsvMapping));

        _preview.Click += async (_, _) => await PreviewAsync();
        _import.Click += async (_, _) => await ImportAsync();

        foreach (var combo in new[] { _delimiter, _decimal }) combo.SelectedIndexChanged += (_, _) => MappingChanged();
        foreach (var number in new[] { _skip, _date, _valueDate, _label, _reference, _amount, _debit, _credit })
            number.ValueChanged += (_, _) => MappingChanged();
        _header.CheckedChanged += (_, _) => MappingChanged();
        _dateFormat.TextChanged += (_, _) => MappingChanged();
        Shown += async (_, _) => await PreviewAsync();
    }

    private void BuildLayout()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 118 };

        var x = 12;
        void Field(string key, Control control, int width, int row = 0)
        {
            var label = UiTheme.FieldLabel(_localizer[key]);
            label.Location = new Point(x, 8 + row * 54);
            control.Bounds = new Rectangle(x, 26 + row * 54, width, UiTheme.InputHeight);
            top.Controls.Add(label);
            top.Controls.Add(control);
            x += width + 14;
        }

        _delimiter.Items.AddRange([_localizer["Treasury.DelimiterAuto"], ";", ",", _localizer["Treasury.DelimiterTab"]]);
        _decimal.Items.AddRange([_localizer["Treasury.DelimiterAuto"], ",", "."]);

        Field("Treasury.Delimiter", _delimiter, 110);
        Field("Treasury.SkipLines", _skip, 70);
        Field("Treasury.DateFormat", _dateFormat, 110);
        Field("Treasury.DecimalSeparator", _decimal, 110);
        _header.Text = _localizer["Treasury.HasHeader"];
        _header.Location = new Point(x, 28);
        top.Controls.Add(_header);

        x = 12;
        Field("Treasury.DateColumn", _date, 70, 1);
        Field("Treasury.ValueDateColumn", _valueDate, 70, 1);
        Field("Treasury.LabelColumn", _label, 70, 1);
        Field("Treasury.ReferenceColumn", _reference, 70, 1);
        Field("Treasury.AmountColumn", _amount, 70, 1);
        Field("Treasury.DebitColumn", _debit, 70, 1);
        Field("Treasury.CreditColumn", _credit, 70, 1);

        _preview.Text = _localizer["Action.Preview"];
        _preview.Bounds = new Rectangle(x + 10, 78, 140, UiTheme.ButtonHeight);
        top.Controls.Add(_preview);

        var notice = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            Text = _localizer["Treasury.ImportNotice"]
        };

        _raw.Dock = DockStyle.Top;
        _raw.Height = 170;

        BankReconciliationView.AddColumns(_parsed, ("line", 6), ("date", 10), ("valuedate", 10), ("label", 34),
                                          ("reference", 14), ("amount", 12), ("status", 18));
        string[] headers =
        [
            _localizer["Treasury.FileLine"], _localizer["Field.Date"], _localizer["Treasury.ValueDate"],
            _localizer["Treasury.Label"], _localizer["Payment.Reference"], _localizer["Payment.Amount"],
            _localizer["Field.Status"]
        ];
        for (var i = 0; i < headers.Length; i++) _parsed.Columns[i].HeaderText = headers[i];
        _parsed.Columns["amount"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _parsed.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _parsed.Rows[e.RowIndex].Tag is not ImportPreviewLine line) return;
            e.CellStyle!.ForeColor = line.Status switch
            {
                ImportLineStatus.Invalid => UiTheme.Critical,
                ImportLineStatus.Duplicate => UiTheme.TextMuted,
                _ => UiTheme.TextPrimary
            };
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52 };
        _summary.Bounds = new Rectangle(12, 4, 680, 44);
        _summary.Font = UiTheme.Small;
        _summary.ForeColor = UiTheme.TextSecondary;
        _import.Text = _localizer["Action.Import"];
        _cancel.Text = _localizer["Action.Cancel"];
        _import.Bounds = new Rectangle(720, 10, 170, UiTheme.ButtonHeight);
        _cancel.Bounds = new Rectangle(900, 10, 150, UiTheme.ButtonHeight);
        _cancel.DialogResult = DialogResult.Cancel;
        CancelButton = _cancel;
        bottom.Controls.AddRange([_summary, _import, _cancel]);

        Controls.Add(_parsed);
        Controls.Add(_raw);
        Controls.Add(notice);
        Controls.Add(top);
        Controls.Add(bottom);

        DialogFrame.Apply(this, _localizer["Finance.Dialog.StatementImport"], [], _cancel, _import);
    }

    private void Apply(BankStatementCsvMapping m)
    {
        _delimiter.SelectedIndex = m.Delimiter switch { ';' => 1, ',' => 2, '\t' => 3, _ => 0 };
        _skip.Value = Math.Clamp(m.SkipLines, 0, 100);
        _header.Checked = m.HasHeader;
        _date.Value = Math.Clamp(m.DateColumn, 0, 60);
        _valueDate.Value = Math.Clamp(m.ValueDateColumn, 0, 60);
        _label.Value = Math.Clamp(m.LabelColumn, 0, 60);
        _reference.Value = Math.Clamp(m.ReferenceColumn, 0, 60);
        _amount.Value = Math.Clamp(m.AmountColumn, 0, 60);
        _debit.Value = Math.Clamp(m.DebitColumn, 0, 60);
        _credit.Value = Math.Clamp(m.CreditColumn, 0, 60);
        _dateFormat.Text = m.DateFormat;
        _decimal.SelectedIndex = m.DecimalSeparator switch { ',' => 1, '.' => 2, _ => 0 };
    }

    private BankStatementCsvMapping Mapping() => new()
    {
        Delimiter = _delimiter.SelectedIndex switch { 1 => ';', 2 => ',', 3 => '\t', _ => '\0' },
        SkipLines = (int)_skip.Value,
        HasHeader = _header.Checked,
        DateColumn = (int)_date.Value,
        ValueDateColumn = (int)_valueDate.Value,
        LabelColumn = (int)_label.Value,
        ReferenceColumn = (int)_reference.Value,
        AmountColumn = (int)_amount.Value,
        DebitColumn = (int)_debit.Value,
        CreditColumn = (int)_credit.Value,
        DateFormat = string.IsNullOrWhiteSpace(_dateFormat.Text) ? "dd/MM/yyyy" : _dateFormat.Text.Trim(),
        DecimalSeparator = _decimal.SelectedIndex switch { 1 => ',', 2 => '.', _ => '\0' }
    };

    private void MappingChanged()
    {
        if (_previewed is null || Mapping() == _previewed) return;

        _previewed = null;
        _import.Enabled = false;
        _summary.Text = _localizer["Treasury.PreviewOutdated"];
        _summary.ForeColor = UiTheme.Critical;
    }

    private async Task PreviewAsync()
    {
        var mapping = Mapping();
        _previewed = null;
        _import.Enabled = false;
        var result = await _treasury.PreviewImportAsync(_account.Id, _content, mapping);
        if (result.IsFailure) { _dialogs.ShowFailure(result, Text); return; }

        _result = result.Value;

        // The raw cells, numbered as the mapping counts them.
        _raw.Columns.Clear();
        var width = _result.RawRows.Count == 0 ? 0 : _result.RawRows.Max(r => r.Count);
        for (var c = 1; c <= width; c++)
            _raw.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = $"c{c}", HeaderText = c.ToString(CultureInfo.InvariantCulture), ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        foreach (var row in _result.RawRows)
            _raw.Rows.Add([.. row.Cast<object>().Concat(Enumerable.Repeat<object>(string.Empty, width - row.Count))]);

        _parsed.Rows.Clear();
        foreach (var line in _result.Lines)
        {
            var l = line.Line;
            var status = line.Status switch
            {
                ImportLineStatus.New => _localizer["Treasury.LineNew"],
                ImportLineStatus.Duplicate => _localizer["Treasury.LineDuplicate"],
                _ => _localizer[l.ErrorKey ?? "BankStatement_DateUnreadable"]
            };
            var index = _parsed.Rows.Add(l.LineNumber.ToString(CultureInfo.InvariantCulture),
                                         l.OperationDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "?",
                                         l.ValueDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
                                         l.Label, l.Reference ?? string.Empty,
                                         l.Amount is { } a ? Money.Of(a).Format(_localizer.CurrentLanguage) : "?",
                                         status);
            _parsed.Rows[index].Tag = line;
        }

        var valid = mapping.Validate();
        _summary.Text = valid.IsFailure
            ? _localizer.Describe(valid.Errors)
            : _localizer.Format("Treasury.ImportSummary", _result.NewCount, _result.DuplicateCount, _result.InvalidCount,
                                Money.Of(_result.NewTotalMad).Format(_localizer.CurrentLanguage));
        _summary.ForeColor = valid.IsFailure ? UiTheme.Critical : UiTheme.TextSecondary;
        // A field changed while the preview was being read: it is already out of date.
        _previewed = Mapping() == mapping ? mapping : null;
        _import.Enabled = _previewed is not null && valid.IsSuccess && _result.NewCount > 0;
    }

    private async Task ImportAsync()
    {
        if (_result is null || _result.NewCount == 0 || _previewed is null) return;
        if (Mapping() != _previewed) { MappingChanged(); return; }

        if (_result.InvalidCount > 0
            && !_dialogs.Confirm(_localizer.Format("Treasury.ImportWithInvalid", _result.InvalidCount), Text))
            return;

        _import.Enabled = false;
        try
        {
            var imported = await _treasury.ImportAsync(_account.Id, _content, _fileName, _previewed);
            if (imported.IsFailure) { _dialogs.ShowFailure(imported, Text); return; }

            _dialogs.Info(_localizer.Format("Treasury.Imported", imported.Value.Imported, imported.Value.Duplicates,
                                            imported.Value.Invalid), Text);
            DialogResult = DialogResult.OK;
            Close();
        }
        finally
        {
            if (!IsDisposed) _import.Enabled = _previewed is not null;
        }
    }
}

/// <summary>The flows entered by hand for the cash forecast.</summary>
internal sealed class ForecastItemsDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly TreasuryService _treasury;
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Button _new = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _edit = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _close = UiTheme.SecondaryButton(string.Empty);

    internal ForecastItemsDialog(ILocalizer localizer, IUserDialogs dialogs, TreasuryService treasury, bool canManage)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _treasury = treasury;

        Text = localizer["Treasury.ForecastItems"];
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(860, 420);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        BankReconciliationView.AddColumns(_grid, ("label", 30), ("amount", 12), ("recurrence", 14), ("start", 11),
                                          ("end", 11), ("active", 7));
        string[] headers =
        [
            localizer["Treasury.Label"], localizer["Payment.Amount"], localizer["Treasury.Recurrence"],
            localizer["Treasury.StartDate"], localizer["Treasury.EndDate"], localizer["Field.Active"]
        ];
        for (var i = 0; i < headers.Length; i++) _grid.Columns[i].HeaderText = headers[i];
        _grid.Columns["amount"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 48 };
        _new.Text = localizer["Action.New"];
        _edit.Text = localizer["Action.Edit"];
        _close.Text = localizer["Action.Close"];
        _new.Bounds = new Rectangle(12, 8, 140, UiTheme.ButtonHeight);
        _edit.Bounds = new Rectangle(162, 8, 140, UiTheme.ButtonHeight);
        _close.Bounds = new Rectangle(312, 8, 140, UiTheme.ButtonHeight);
        _close.DialogResult = DialogResult.Cancel;
        CancelButton = _close;
        _new.Enabled = _edit.Enabled = canManage;
        bar.Controls.AddRange([_new, _edit, _close]);

        Controls.Add(_grid);
        Controls.Add(bar);

        DialogFrame.Apply(this, localizer["Finance.Dialog.ForecastItems"], [_edit], _close, _new);

        _new.Click += async (_, _) => await EditAsync(null);
        _edit.Click += async (_, _) => await EditAsync(_grid.CurrentRow?.Tag as ForecastItemRow);
        if (canManage)
            _grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await EditAsync(_grid.Rows[e.RowIndex].Tag as ForecastItemRow); };
        Shown += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var result = await _treasury.GetForecastItemsAsync();
        if (result.IsFailure) { _dialogs.ShowFailure(result, Text); return; }

        _grid.Rows.Clear();
        foreach (var item in result.Value)
        {
            var index = _grid.Rows.Add(item.Label, Money.Of(item.AmountMad).Format(_localizer.CurrentLanguage),
                                       _localizer[$"ForecastRecurrence.{item.Recurrence}"],
                                       item.StartDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                                       item.EndDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "—",
                                       item.IsActive ? _localizer["Action.Yes"] : _localizer["Action.No"]);
            _grid.Rows[index].Tag = item;
        }
    }

    private async Task EditAsync(ForecastItemRow? row)
    {
        using var editor = new ForecastItemEditor(_localizer, _dialogs, row);
        if (editor.ShowDialog(this) != DialogResult.OK || editor.Input is null) return;

        var saved = await _treasury.SaveForecastItemAsync(editor.Input);
        if (saved.IsFailure) { _dialogs.ShowFailure(saved, Text); return; }

        await LoadAsync();
    }
}

/// <summary>One flow entered by hand: an outflow typed as a negative amount.</summary>
internal sealed class ForecastItemEditor : MasterDataEditor
{
    private readonly Guid? _id;
    private readonly TextBox _label, _amount;
    private readonly ComboBox _recurrence;
    private readonly DateTimePicker _start;
    private readonly (CheckBox Toggle, DateTimePicker Picker) _end;
    private readonly CheckBox _active;

    internal ForecastItemInput? Input { get; private set; }

    internal ForecastItemEditor(ILocalizer localizer, IUserDialogs dialogs, ForecastItemRow? row)
        : base(localizer, dialogs, "Treasury.ForecastItem", row?.Label, 560, 300)
    {
        _id = row?.Id;

        _label = TextField("Treasury.Label", 20, 14, 330, row?.Label);
        _label.MaxLength = TreasuryForecastItem.LabelMaxLength;
        _amount = TextField("Treasury.SignedAmount", 370, 14, 170,
                            (row?.AmountMad ?? 0m).ToString("0.00", CultureInfo.InvariantCulture),
                            latin: true, mono: true, right: true);
        _recurrence = Choice("Treasury.Recurrence", 20, 68, 200, row?.Recurrence ?? ForecastRecurrence.Monthly,
                             "ForecastRecurrence");

        Label("Treasury.StartDate", 240, 68);
        _start = new DateTimePicker
        {
            Bounds = new Rectangle(240, 86, 150, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            Value = row?.StartDate.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today
        };
        Controls.Add(_start);

        _end = OptionalDate("Treasury.EndDate", 20, 124, row?.EndDate);
        _active = Check("Field.Active", 240, 128, row?.IsActive ?? true);

        Notice(20, 180, 520, 50, "Treasury.ForecastItemNotice");

        Buttons(242, 560);
        DialogFrame.Apply(this, localizer["Finance.Dialog.ForecastItem"]);
    }

    protected override void Accept()
    {
        if (DecimalOrNull(_amount) is not { } amount || amount == 0m)
        {
            Dialogs.Warn(Localizer["TreasuryItem_AmountRequired"], Text);
            _amount.Focus();
            return;
        }

        Input = new ForecastItemInput(_id, _label.Text.Trim(), amount, Selected<ForecastRecurrence>(_recurrence),
                                      DateOnly.FromDateTime(_start.Value.Date), DateOrNull(_end.Toggle, _end.Picker),
                                      _active.Checked);
        DialogResult = DialogResult.OK;
        Close();
    }
}
