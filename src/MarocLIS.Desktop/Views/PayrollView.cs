using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.HumanResources;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The month's payroll: one line per payslip with gross, contributions, tax and net, the month's
/// variables one double-click away, the payslips in PDF, the journal and CNSS summary to export, and
/// the declaration files: the month's BDS for DAMANCOM and the year's état 9421 for Simpl-IR.
/// Reserved to the salary right.
/// </summary>
public sealed class PayrollView : LocalizedView
{
    private readonly PayrollService _payroll;
    private readonly IPayslipRenderer _renderer;
    private readonly IHrDocumentRenderer _letters;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<PayrollView>? _logger;

    private readonly Label _heading = new();
    private readonly ComboBox _period = new();
    private readonly Button _open = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _settings = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _simplIr = UiTheme.SecondaryButton(string.Empty);
    private readonly DataGridView _grid = UiTheme.Grid();

    private readonly Label _totals = new();
    private readonly Button _variables = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _payslip = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _allPayslips = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _journal = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _cnss = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _bds = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _update = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _close = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _transfer = UiTheme.SecondaryButton(string.Empty);
    private readonly ContextMenuStrip _transferMenu = new();
    private readonly Label _title = new();
    private readonly Label _periodCaption = new();
    // The rates, the Simpl-IR return and the BDS file: kept as buttons (hidden) for their state,
    // offered from « Plus d'actions ».
    private readonly Button _more = UiTheme.SecondaryButton(string.Empty);
    private readonly ContextMenuStrip _moreMenu = new();

    private IReadOnlyList<PayrollPeriodRow> _periods = [];
    private PayrollPeriodDetail? _detail;
    private bool _loading;

    public PayrollView(PayrollService payroll, IPayslipRenderer renderer, IHrDocumentRenderer letters, IDocumentOutput output,
                       IUserDialogs dialogs, ILocalizer localizer, ICultureManager cultureManager,
                       ILogger<PayrollView>? logger = null)
        : base(localizer, cultureManager)
    {
        _payroll = payroll;
        _renderer = renderer;
        _letters = letters;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Payroll");

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static string Dh(decimal value) => value.ToString("N2", Fr);

    private void BuildLayout()
    {
        Padding = QhPage.PagePadding;

        // Direction A: the title and the month's state; Refresh and « Plus d'actions » (the rates,
        // the yearly Simpl-IR return and the BDS file, the rare ones) on the right; the month and
        // Open under them; the payslips in a card; under the list the totals and the month's
        // actions, the variables of the selected payslip (the primary) at the far end.
        var header = QhPage.Header(_title, _heading, _refresh, QhPage.MoreButton(_more, _moreMenu));

        _period.DropDownStyle = ComboBoxStyle.DropDownList;
        _period.FlatStyle = FlatStyle.Flat;
        var toolbar = QhPage.Toolbar(QhPage.Caption(_periodCaption), QhPage.Filter(_period, 260), QhPage.Fit(_open));

        void Add(string name, int weight, Font? font = null, bool right = false)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, Tag = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body,
                    Alignment = right ? DataGridViewContentAlignment.MiddleRight : DataGridViewContentAlignment.MiddleLeft
                }
            });

        Add("number", 7, UiTheme.Monospace);
        Add("name", 16);
        Add("days", 5, UiTheme.Monospace, true);
        Add("base", 9, UiTheme.Monospace, true);
        Add("gross", 9, UiTheme.Monospace, true);
        Add("social", 9, UiTheme.Monospace, true);
        Add("tax", 8, UiTheme.Monospace, true);
        Add("net", 9, UiTheme.Monospace, true);
        Add("cost", 9, UiTheme.Monospace, true);
        Add("alert", 16, UiTheme.Small);

        _totals.Font = UiTheme.SmallBold;
        _totals.ForeColor = UiTheme.TextSecondary;

        _grid.Dock = DockStyle.Fill;
        var body = new Panel { BackColor = UiTheme.SurfaceRaised, Margin = Padding.Empty };
        body.Controls.Add(_grid);
        body.Controls.Add(new QhSelectionBar(_totals, _payslip, _allPayslips, _journal, _cnss, _transfer,
                                             _update, _close, _variables));
        body.Controls.Add(QhMenu.Holder(_settings, _simplIr, _bds));

        Controls.Add(QhPage.Frame(2, header, toolbar, QhPage.Card(body)));
    }

    private void WireBehaviour()
    {
        _period.SelectedIndexChanged += async (_, _) => { if (!_loading) await LoadPeriodAsync(); };
        _open.Click += async (_, _) => await RunBusyAsync(OpenMonthAsync);
        _refresh.Click += async (_, _) => await ActivateAsync();
        _settings.Click += async (_, _) => await RunBusyAsync(SettingsAsync);
        _variables.Click += async (_, _) => await RunBusyAsync(VariablesAsync);
        _grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await RunBusyAsync(VariablesAsync); };
        _payslip.Click += async (_, _) => await PrintAsync(single: true);
        _allPayslips.Click += async (_, _) => await PrintAsync(single: false);
        _journal.Click += async (_, _) => await ExportAsync(cnss: false);
        _cnss.Click += async (_, _) => await ExportAsync(cnss: true);
        _bds.Click += async (_, _) => await RunBusyAsync(() => GuardedAsync(BdsAsync, "Building the CNSS BDS failed."));
        _simplIr.Click += async (_, _) => await RunBusyAsync(() => GuardedAsync(SimplIrAsync, "Building the état 9421 failed."));
        _update.Click += async (_, _) => await RunBusyAsync(UpdateFromFilesAsync);
        _close.Click += async (_, _) => await RunBusyAsync(CloseOrReopenAsync);
        _transferMenu.Items.Add(new ToolStripMenuItem(string.Empty, null, async (_, _) => await TransferAsync(file: false)));
        _transferMenu.Items.Add(new ToolStripMenuItem(string.Empty, null, async (_, _) => await TransferAsync(file: true)));
        _transfer.Click += (_, _) => _transferMenu.Show(_transfer, new Point(0, _transfer.Height));
        _grid.SelectionChanged += (_, _) => RefreshButtons();

        _moreMenu.Items.Add(QhMenu.For(_settings, async (_, _) => await RunBusyAsync(SettingsAsync)));
        _moreMenu.Items.Add(QhMenu.For(_simplIr, async (_, _) =>
            await RunBusyAsync(() => GuardedAsync(SimplIrAsync, "Building the état 9421 failed."))));
        _moreMenu.Items.Add(QhMenu.For(_bds, async (_, _) =>
            await RunBusyAsync(() => GuardedAsync(BdsAsync, "Building the CNSS BDS failed."))));

        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not PayslipRow row) return;
            if (e.ColumnIndex == _grid.Column("alert").Index && row.Warnings.Count > 0)
                e.CellStyle!.ForeColor = row.Warnings.Contains(PayslipWarning.NegativeNet) ? UiTheme.Critical : UiTheme.Abnormal;
            if (e.ColumnIndex == _grid.Column("net").Index) e.CellStyle!.Font = UiTheme.SmallBold;
        };
    }

    protected override void ApplyTexts()
    {
        _title.Text = T("Nav.Payroll");
        _periodCaption.Text = T("Payroll.Month");
        _more.Text = T("ResultEntry.MoreActions");
        _open.Text = T("Payroll.OpenMonth");
        _refresh.Text = T("Action.Refresh");
        _settings.Text = T("Payroll.Settings");
        _variables.Text = T("Payroll.Variables");
        _payslip.Text = T("Payroll.Payslip");
        _allPayslips.Text = T("Payroll.AllPayslips");
        _journal.Text = T("Payroll.Journal");
        _cnss.Text = T("Payroll.CnssSummary");
        _bds.Text = T("Payroll.BdsFile");
        _simplIr.Text = T("Payroll.SimplIr");
        _update.Text = T("Payroll.UpdateFromFiles");
        _transfer.Text = T("Payroll.Transfer");
        _transferMenu.Items[0].Text = T("Payroll.TransferOrder");
        _transferMenu.Items[1].Text = T("Payroll.TransferFile");

        _grid.Column("number").HeaderText = T("Personnel.Number");
        _grid.Column("name").HeaderText = T("Field.Name");
        _grid.Column("days").HeaderText = T("Payroll.Days");
        _grid.Column("base").HeaderText = T("Payroll.Base");
        _grid.Column("gross").HeaderText = T("Payroll.Gross");
        _grid.Column("social").HeaderText = T("Payroll.Social");
        _grid.Column("tax").HeaderText = T("Payroll.IncomeTax");
        _grid.Column("net").HeaderText = T("Payroll.Net");
        _grid.Column("cost").HeaderText = T("Payroll.EmployerCost");
        _grid.Column("alert").HeaderText = T("Payroll.Alerts");

        FillPeriods(CurrentPeriodId());
        Render();
    }

    private Guid? CurrentPeriodId() => _period.SelectedItem is PeriodChoice choice ? choice.Row.PeriodId : null;

    private sealed record PeriodChoice(PayrollPeriodRow Row, string Display)
    {
        public override string ToString() => Display;
    }

    private static string MonthName(int year, int month) =>
        CultureInfo.CurrentUICulture.TextInfo.ToTitleCase(new DateTime(year, month, 1).ToString("MMMM yyyy", CultureInfo.CurrentUICulture));

    private void FillPeriods(Guid? select)
    {
        _loading = true;
        try
        {
            _period.Items.Clear();
            foreach (var row in _periods)
            {
                var index = _period.Items.Add(new PeriodChoice(row, $"{MonthName(row.Year, row.Month)} — {T($"PayrollStatus.{row.Status}")}"));
                if (row.PeriodId == select) _period.SelectedIndex = index;
            }
            if (_period.SelectedIndex < 0 && _period.Items.Count > 0) _period.SelectedIndex = 0;
        }
        finally
        {
            _loading = false;
        }
    }

    public override async Task ActivateAsync(CancellationToken ct = default) => await ReloadAsync(CurrentPeriodId(), ct);

    private async Task ReloadAsync(Guid? select, CancellationToken ct = default)
    {
        try
        {
            var periods = await _payroll.GetPeriodsAsync(ct);
            if (periods.IsFailure) { _dialogs.ShowFailure(periods); return; }

            _periods = periods.Value;
            FillPeriods(select);
            await LoadPeriodAsync(ct);
        }
        catch (OperationCanceledException) { }
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the payroll failed.");
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
    }

    private async Task LoadPeriodAsync(CancellationToken ct = default)
    {
        _detail = null;
        if (CurrentPeriodId() is { } id)
        {
            var detail = await _payroll.GetPeriodAsync(id, ct);
            if (detail.IsFailure) { _dialogs.ShowFailure(detail); return; }
            _detail = detail.Value;
        }

        Render();
    }

    private string WarningsText(PayslipRow row) =>
        string.Join(" · ", row.Warnings.Select(w => T($"PayslipWarning.{w}"))
                              .Concat(row.Amounts.Exemption == PayrollExemption.None
                                  ? [] : [T($"PayrollExemption.{row.Amounts.Exemption}")]));

    private void Render()
    {
        var selected = (_grid.CurrentRow?.Tag as PayslipRow)?.PayslipId;

        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var row in _detail?.Payslips ?? [])
        {
            var a = row.Amounts;
            var index = _grid.Rows.Add(row.Number, row.FullName, a.PaidDays.ToString("0.#", Fr), Dh(row.BaseSalary),
                                       Dh(a.Gross), Dh(a.CnssEmployee + a.AmoEmployee), Dh(a.IncomeTax), Dh(a.NetPay),
                                       Dh(a.EmployerCost), WarningsText(row));
            _grid.Rows[index].Tag = row;
            if (row.PayslipId == selected) _grid.CurrentCell = _grid.Rows[index].Cells[0];
        }

        _grid.ResumeLayout(performLayout: true);
        _grid.RestoreFillWeights();

        if (_detail is { } d)
        {
            _heading.Text = T("QualityHr.Payroll.Count", MonthName(d.Period.Year, d.Period.Month), T($"PayrollStatus.{d.Period.Status}"),
                              d.Payslips.Count);
            _totals.Text = T("Payroll.Totals", Dh(d.Total(a => a.Gross)), Dh(d.Total(a => a.CnssEmployee + a.AmoEmployee)),
                             Dh(d.Total(a => a.IncomeTax)), Dh(d.Total(a => a.NetPay)), Dh(d.Total(a => a.EmployerCost)));
        }
        else
        {
            _heading.Text = string.Empty;
            _totals.Text = T("Payroll.NoPeriod");
        }

        RefreshButtons();
    }

    private void RefreshButtons()
    {
        var draft = _detail?.Period.Status == PayrollStatus.Draft;
        var row = _grid.CurrentRow?.Tag is PayslipRow;

        _variables.Enabled = draft && row;
        _update.Enabled = draft;
        _payslip.Enabled = row;
        _allPayslips.Enabled = _journal.Enabled = _cnss.Enabled = _close.Enabled = _detail is not null;
        _close.Text = _detail?.Period.Status == PayrollStatus.Closed ? T("Payroll.Reopen") : T("Payroll.Close");

        // Only a closed month goes to the bank or to the CNSS: its amounts no longer change.
        _transfer.Enabled = _bds.Enabled = _detail?.Period.Status == PayrollStatus.Closed;
    }

    private async Task TransferAsync(bool file)
    {
        if (_detail is not { } d) return;
        try
        {
            if (file)
            {
                var csv = await _payroll.ExportTransferAsync(d.Period.PeriodId);
                if (csv.IsFailure) { _dialogs.ShowFailure(csv); return; }
                var saved = await _output.OpenAsync(csv.Value, "csv");
                if (saved.IsFailure) _dialogs.ShowFailure(saved);
                return;
            }

            var order = await _payroll.GetTransferOrderAsync(d.Period.PeriodId);
            if (order.IsFailure) { _dialogs.ShowFailure(order); return; }
            var opened = await _output.OpenAsync(_letters.RenderTransferOrder(order.Value), "pdf");
            if (opened.IsFailure) _dialogs.ShowFailure(opened, _transfer.Text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Preparing the salary transfer failed.");
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
    }

    private async Task OpenMonthAsync()
    {
        var next = _periods.Count == 0
            ? new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1)
            : new DateOnly(_periods[0].Year, _periods[0].Month, 1).AddMonths(1);

        using var dialog = new PayrollMonthDialog(Localizer, _dialogs, next);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var opened = await _payroll.OpenAsync(dialog.Month.Year, dialog.Month.Month);
        if (opened.IsFailure) { _dialogs.ShowFailure(opened, _open.Text); return; }
        await ReloadAsync(opened.Value);
    }

    private async Task UpdateFromFilesAsync()
    {
        if (_detail is null) return;
        var done = await _payroll.RefreshAsync(_detail.Period.PeriodId);
        if (done.IsFailure) { _dialogs.ShowFailure(done, _update.Text); return; }
        await ReloadAsync(_detail.Period.PeriodId);
        _dialogs.Info(T("Payroll.Updated", done.Value), _update.Text);
    }

    private async Task VariablesAsync()
    {
        if (_detail is not { Period.Status: PayrollStatus.Draft } d || _grid.CurrentRow?.Tag is not PayslipRow row) return;

        using var dialog = new PayslipVariablesDialog(Localizer, _dialogs, row);
        while (dialog.ShowDialog(this) == DialogResult.OK)
        {
            var saved = await _payroll.UpdateVariablesAsync(d.Period.PeriodId, row.PayslipId, dialog.Variables);
            if (saved.IsSuccess)
            {
                await LoadPeriodAsync();
                return;
            }

            _dialogs.ShowFailure(saved, dialog.Text);
        }
    }

    private async Task CloseOrReopenAsync()
    {
        if (_detail is not { } d) return;

        Domain.Common.Result done;
        if (d.Period.Status == PayrollStatus.Draft)
        {
            if (!_dialogs.Confirm(T("Payroll.ClosePrompt", MonthName(d.Period.Year, d.Period.Month)), _close.Text)) return;
            done = await _payroll.CloseAsync(d.Period.PeriodId);
        }
        else
        {
            var reason = _dialogs.PromptForReason(T("Payroll.ReopenPrompt", MonthName(d.Period.Year, d.Period.Month)), _close.Text);
            if (reason is null) return;
            // Normally the month keeps the rates it was closed with; taking the year's current
            // ones is only for a rate typed wrong and corrected since. "No" is the default answer.
            var currentRates = _dialogs.Confirm(T("Payroll.ReopenCurrentRatesPrompt", d.Period.Year), _close.Text);
            done = await _payroll.ReopenAsync(d.Period.PeriodId, reason, currentRates);
        }

        if (done.IsFailure) { _dialogs.ShowFailure(done, _close.Text); return; }
        await ReloadAsync(d.Period.PeriodId);
    }

    private async Task PrintAsync(bool single)
    {
        if (_detail is not { } d) return;
        Guid? only = single ? (_grid.CurrentRow?.Tag as PayslipRow)?.PayslipId : null;
        if (single && only is null) return;

        try
        {
            var docs = await _payroll.GetDocumentsAsync(d.Period.PeriodId, only);
            if (docs.IsFailure) { _dialogs.ShowFailure(docs); return; }

            var bytes = _renderer.Render(docs.Value);
            var opened = await _output.OpenAsync(bytes, "pdf");
            if (opened.IsFailure) _dialogs.ShowFailure(opened, _payslip.Text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Printing payslips failed.");
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
    }

    private async Task ExportAsync(bool cnss)
    {
        if (_detail is not { } d) return;
        try
        {
            var file = cnss ? await _payroll.ExportCnssAsync(d.Period.PeriodId) : await _payroll.ExportJournalAsync(d.Period.PeriodId);
            if (file.IsFailure) { _dialogs.ShowFailure(file); return; }

            var opened = await _output.OpenAsync(file.Value, "csv");
            if (opened.IsFailure) _dialogs.ShowFailure(opened);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exporting the payroll failed.");
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
    }

    private async Task GuardedAsync(Func<Task> operation, string failure)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "{Failure}", failure);
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
    }

    /// <summary>
    /// The month's BDS: the préétabli DAMANCOM issued for the month is chosen, the BDS built from it
    /// saved under the name DAMANCOM expects.
    /// </summary>
    private async Task BdsAsync()
    {
        if (_detail is not { Period.Status: PayrollStatus.Closed } d) return;

        using var picker = new OpenFileDialog
        {
            Title = T("Payroll.BdsPickPreestablished"),
            Filter = $"{T("Payroll.BdsFiles")} (AFFEBDS_*.txt)|AFFEBDS_*.txt|{T("Payroll.TextFiles")} (*.txt)|*.txt",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        byte[] preestablished;
        try
        {
            preestablished = await File.ReadAllBytesAsync(picker.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Warn($"{T("Documents.FileUnreadable")}\n{ex.Message}", _bds.Text);
            return;
        }

        var built = await _payroll.ExportBdsAsync(d.Period.PeriodId, preestablished);
        if (built.IsFailure) { _dialogs.ShowFailure(built, _bds.Text); return; }

        var path = Save(built.Value, $"{T("Payroll.TextFiles")} (*.txt)|*.txt", _bds.Text,
                        Path.GetDirectoryName(picker.FileName));
        if (path is not null)
            _dialogs.Info(T("Payroll.BdsSaved", path, built.Value.Declared, built.Value.Entrants), _bds.Text);
    }

    /// <summary>The year's état 9421: the DGI codes asked (and remembered on this workstation), then the XML saved.</summary>
    private async Task SimplIrAsync()
    {
        var remembered = SimplIrCodesStore.Load(_logger);
        using var dialog = new SimplIrDialog(Localizer, _dialogs, DateTime.Today.Year - 1, remembered);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        SimplIrCodesStore.Save(dialog.Codes, _logger);
        var built = await _payroll.ExportSimplIrAsync(dialog.Year, dialog.Codes);
        if (built.IsFailure) { _dialogs.ShowFailure(built, _simplIr.Text); return; }

        var path = Save(built.Value, $"{T("Payroll.XmlFiles")} (*.xml)|*.xml", _simplIr.Text, null);
        if (path is not null) _dialogs.Info(T("Payroll.SimplIrSaved", path, built.Value.Declared), _simplIr.Text);
    }

    /// <summary>Asks where to write a declaration file, under the name proposed; null when cancelled or not written.</summary>
    private string? Save(PayrollExportFile file, string filter, string title, string? folder)
    {
        using var saver = new SaveFileDialog
        {
            Title = UserDialogs.CaptionOf(title, title),
            FileName = file.FileName,
            Filter = filter,
            OverwritePrompt = true,
            AddExtension = true
        };
        if (folder is not null) saver.InitialDirectory = folder;
        if (saver.ShowDialog(this) != DialogResult.OK) return null;

        try
        {
            File.WriteAllBytes(saver.FileName, file.Content);
            return saver.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Writing a declaration file failed.");
            _dialogs.Warn($"{T("Document_WriteFailed")}\n{ex.Message}", title);
            return null;
        }
    }

    private async Task SettingsAsync()
    {
        var year = _detail?.Period.Year ?? DateTime.Today.Year;
        var current = await _payroll.GetParametersAsync(year);
        if (current.IsFailure) { _dialogs.ShowFailure(current); return; }

        using var dialog = new PayrollParametersDialog(Localizer, _dialogs, current.Value);
        while (dialog.ShowDialog(this) == DialogResult.OK)
        {
            var saved = await _payroll.SaveParametersAsync(year, dialog.Apply);
            if (saved.IsSuccess)
            {
                await LoadPeriodAsync();
                return;
            }

            _dialogs.ShowFailure(saved, dialog.Text);
        }
    }
}

internal sealed class PayrollMonthDialog : MasterDataEditor
{
    private readonly DateTimePicker _month = new();

    internal DateOnly Month => new(_month.Value.Year, _month.Value.Month, 1);

    internal PayrollMonthDialog(ILocalizer localizer, IUserDialogs dialogs, DateOnly suggested)
        : base(localizer, dialogs, "Payroll.OpenMonth", null, 420, 200)
    {
        Label("Payroll.Month", 20, 10);
        _month.Bounds = new Rectangle(20, 28, 200, UiTheme.InputHeight);
        _month.Format = DateTimePickerFormat.Custom;
        _month.CustomFormat = "MM/yyyy";
        _month.ShowUpDown = true;
        _month.Value = suggested.ToDateTime(TimeOnly.MinValue);
        Controls.Add(_month);

        Notice(20, 66, 380, 60, "Payroll.OpenNotice");
        Buttons(140, 420, "Payroll.OpenMonth");
        QhDialog.Dress(this, localizer, null);
    }

    protected override void Accept() => DialogResult = DialogResult.OK;
}

/// <summary>The month's variables of one payslip, with the result shown as it will be paid.</summary>
internal sealed class PayslipVariablesDialog : MasterDataEditor
{
    private readonly TextBox _absence, _ot25, _ot50, _ot100, _bonus, _transport, _exempt, _advance, _other, _note;
    private readonly PayslipVariables _original;

    internal PayslipVariables Variables { get; private set; } = new();

    internal PayslipVariablesDialog(ILocalizer localizer, IUserDialogs dialogs, PayslipRow row)
        : base(localizer, dialogs, "Payroll.Variables", $"{row.Number} {row.FullName}", 620, 430)
    {
        var v = _original = row.Variables;
        static string N(decimal d) => d == 0 ? string.Empty : d.ToString("0.##", CultureInfo.InvariantCulture);

        TextBox Field(string key, int x, int y, decimal value)
        {
            var box = TextField(key, x, y, 180, N(value), mono: true, right: true);
            Numeric(EditorValues.AmountMaxLength, box);
            return box;
        }

        // Only the days entered by hand: those from the leave are taken again at each refresh.
        _absence = Field("Payroll.ManualAbsenceDays", 20, 10, v.ManualAbsenceDays);
        _ot25 = Field("Payroll.Overtime25", 220, 10, v.OvertimeHours25);
        _ot50 = Field("Payroll.Overtime50", 420, 10, v.OvertimeHours50);
        _ot100 = Field("Payroll.Overtime100", 20, 62, v.OvertimeHours100);
        _bonus = Field("Payroll.TaxableBonus", 220, 62, v.TaxableBonus);
        _transport = Field("Payroll.Transport", 420, 62, v.TransportAllowance);
        _exempt = Field("Payroll.OtherExempt", 20, 114, v.OtherExemptAllowance);
        _advance = Field("Payroll.Advance", 220, 114, v.AdvanceDeduction);
        _other = Field("Payroll.OtherDeduction", 420, 114, v.OtherDeduction);
        _note = TextField("Field.Notes", 20, 166, 580, v.Note);
        _note.MaxLength = 300;

        var a = row.Amounts;
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var current = new Label
        {
            Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary, AutoSize = false,
            Bounds = new Rectangle(20, 222, 580, 60),
            Text = localizer.Format("Payroll.CurrentResult", a.Gross.ToString("N2", fr), a.CnssEmployee.ToString("N2", fr),
                                    a.AmoEmployee.ToString("N2", fr), a.IncomeTax.ToString("N2", fr), a.NetPay.ToString("N2", fr),
                                    a.SeniorityRate.ToString("0.#", fr))
        };
        if (v.AbsenceDays > 0)
            current.Text += " " + localizer.Format("Payroll.LeaveAbsenceIncluded", v.AbsenceDays.ToString("0.#", fr));
        if (v.IsProrated)
            current.Text += " " + localizer.Format("Payroll.ProratedMonth", v.ContractWorkingDays.ToString("0.#", fr),
                                                   v.MonthWorkingDays.ToString("0.#", fr));
        var departure = v.LeaveIndemnity + v.NoticeIndemnity + v.SeverancePay;
        if (departure > 0)
            current.Text += " " + localizer.Format("Payroll.DepartureIncluded", departure.ToString("N2", fr));
        if (v.WorkAccidentDays > 0)
            current.Text += " " + localizer.Format("Payroll.AccidentIncluded", v.WorkAccidentDays.ToString("0.#", fr),
                                                   v.WorkAccidentAllowance.ToString("N2", fr));
        Controls.Add(current);

        Notice(20, 290, 580, 74, "Payroll.VariablesNotice");
        Buttons(374, 620);
        QhDialog.Dress(this, localizer, null);
    }

    // The shared reader: "1 250,50" with the no-break space of the French culture, or "1.250,50",
    // was refused here as a negative variable.
    private static bool Read(TextBox box, out decimal value) => EditorValues.TryParseOrZero(box.Text, out value);

    protected override void Accept()
    {
        var values = new decimal[9];
        TextBox[] boxes = [_absence, _ot25, _ot50, _ot100, _bonus, _transport, _exempt, _advance, _other];
        for (var i = 0; i < boxes.Length; i++)
            if (!Read(boxes[i], out values[i]) || values[i] < 0)
            {
                Dialogs.Warn(Localizer["Payroll_NegativeVariable"], Text);
                boxes[i].Focus();
                return;
            }

        // The departure indemnities and the leave-derived days come from the files and are kept as they are.
        Variables = _original with
        {
            ManualAbsenceDays = values[0], OvertimeHours25 = values[1], OvertimeHours50 = values[2], OvertimeHours100 = values[3],
            TaxableBonus = values[4], TransportAllowance = values[5], OtherExemptAllowance = values[6],
            AdvanceDeduction = values[7], OtherDeduction = values[8], Note = Blank(_note)
        };
        DialogResult = DialogResult.OK;
    }
}

/// <summary>The year's legal rates. Draft months of the year are recomputed when they change.</summary>
internal sealed class PayrollParametersDialog : MasterDataEditor
{
    private readonly (TextBox Box, Action<PayrollParameters, decimal> Set)[] _fields;
    private readonly TextBox _scale;
    private decimal[] _values = [];

    internal PayrollParametersDialog(ILocalizer localizer, IUserDialogs dialogs, PayrollParameters p)
        : base(localizer, dialogs, "Payroll.Settings", p.Year.ToString(CultureInfo.InvariantCulture), 700, 470)
    {
        static string N(decimal d) => d.ToString("0.##", CultureInfo.InvariantCulture);
        (TextBox, Action<PayrollParameters, decimal>) F(string key, int x, int y, decimal value, Action<PayrollParameters, decimal> set)
        {
            var box = TextField(key, x, y, 200, N(value), mono: true, right: true);
            // decimal(18,6) columns, ceilings and SMIG included: the shared check refuses a
            // fifteen-figure amount the save could not store.
            Numeric(EditorValues.AmountMaxLength, box);
            return (box, set);
        }

        _fields =
        [
            F("PayrollRate.CnssCeiling", 20, 10, p.CnssCeiling, (r, v) => r.CnssCeiling = v),
            F("PayrollRate.CnssEmployee", 240, 10, p.CnssEmployeeRate, (r, v) => r.CnssEmployeeRate = v),
            F("PayrollRate.CnssEmployer", 460, 10, p.CnssEmployerRate, (r, v) => r.CnssEmployerRate = v),
            F("PayrollRate.FamilyAllowance", 20, 62, p.FamilyAllowanceRate, (r, v) => r.FamilyAllowanceRate = v),
            F("PayrollRate.TrainingTax", 240, 62, p.TrainingTaxRate, (r, v) => r.TrainingTaxRate = v),
            F("PayrollRate.AmoEmployee", 460, 62, p.AmoEmployeeRate, (r, v) => r.AmoEmployeeRate = v),
            F("PayrollRate.AmoEmployer", 20, 114, p.AmoEmployerRate, (r, v) => r.AmoEmployerRate = v),
            F("PayrollRate.ExpensesLow", 240, 114, p.ProfessionalExpensesLowRate, (r, v) => r.ProfessionalExpensesLowRate = v),
            F("PayrollRate.ExpensesHigh", 460, 114, p.ProfessionalExpensesHighRate, (r, v) => r.ProfessionalExpensesHighRate = v),
            F("PayrollRate.ExpensesThreshold", 20, 166, p.ProfessionalExpensesThreshold, (r, v) => r.ProfessionalExpensesThreshold = v),
            F("PayrollRate.ExpensesCap", 240, 166, p.ProfessionalExpensesAnnualCap, (r, v) => r.ProfessionalExpensesAnnualCap = v),
            F("PayrollRate.FamilyDeduction", 460, 166, p.FamilyDeductionPerPerson, (r, v) => r.FamilyDeductionPerPerson = v),
            F("PayrollRate.Smig", 20, 218, p.SmigMonthly, (r, v) => r.SmigMonthly = v),
            F("PayrollRate.MonthlyHours", 240, 218, p.MonthlyHours, (r, v) => r.MonthlyHours = v),
            F("PayrollRate.Transport", 460, 218, p.TransportExemptionCap, (r, v) => r.TransportExemptionCap = v)
        ];

        _scale = TextField("PayrollRate.Scale", 20, 270, 660, p.IncomeTaxScale, latin: true, mono: true);
        _scale.MaxLength = 300;
        Notice(20, 322, 660, 84, "PayrollRate.Notice");
        Buttons(414, 700);
        QhDialog.Dress(this, localizer, null);
    }

    protected override void Accept()
    {
        _values = new decimal[_fields.Length];
        for (var i = 0; i < _fields.Length; i++)
        {
            if (EditorValues.ParseDecimal(_fields[i].Box.Text) is not { } value || value < 0)
            {
                Dialogs.Warn(Localizer["Payroll_RateInvalid"], Text);
                _fields[i].Box.Focus();
                return;
            }
            _values[i] = value;
        }

        if (PayrollParameters.ParseScale(_scale.Text).Count == 0)
        {
            Dialogs.Warn(Localizer["Payroll_ScaleInvalid"], Text);
            _scale.Focus();
            return;
        }

        DialogResult = DialogResult.OK;
    }

    internal void Apply(PayrollParameters target)
    {
        for (var i = 0; i < _fields.Length; i++) _fields[i].Set(target, _values[i]);
        target.IncomeTaxScale = _scale.Text.Trim();
    }
}

/// <summary>
/// The year of the état 9421 and the Simpl-IR referential codes the application does not hold.
/// </summary>
internal sealed class SimplIrDialog : MasterDataEditor
{
    private readonly NumericUpDown _year;
    private readonly TextBox _commune, _lowRate, _highRate;

    internal int Year => (int)_year.Value;
    internal SimplIrCodes Codes => new(_commune.Text.Trim(), _lowRate.Text.Trim(), _highRate.Text.Trim());

    internal SimplIrDialog(ILocalizer localizer, IUserDialogs dialogs, int year, SimplIrCodes remembered)
        : base(localizer, dialogs, "Payroll.SimplIr", null, 560, 330)
    {
        _year = Number("Payroll.SimplIrYear", 20, 10, 120, year, max: 2100);
        _year.Minimum = 2000;
        _year.Value = Math.Clamp(year, 2000, 2100);
        _commune = TextField("Payroll.SimplIrCommune", 160, 10, 380, remembered.Commune, latin: true, mono: true);
        _lowRate = TextField("Payroll.SimplIrLowRateCode", 20, 62, 520, remembered.LowIncomeRateCode, latin: true, mono: true);
        _highRate = TextField("Payroll.SimplIrHighRateCode", 20, 114, 520, remembered.HighIncomeRateCode, latin: true, mono: true);

        Notice(20, 166, 520, 100, "Payroll.SimplIrNotice");
        Buttons(276, 560, "Action.Export");
        QhDialog.Dress(this, localizer, null);
    }

    protected override void Accept()
    {
        foreach (var box in new[] { _commune, _lowRate, _highRate })
            if (box.Text.Trim().Length == 0)
            {
                Dialogs.Warn(Localizer["Payroll_SimplIrCodesRequired"], Text);
                box.Focus();
                return;
            }

        DialogResult = DialogResult.OK;
    }
}

/// <summary>
/// The last Simpl-IR codes typed, kept for the next year in
/// <c>%LOCALAPPDATA%\MarocLIS\simplir-codes.json</c>: referential codes, not data, so a file
/// on the workstation rather than a column.
/// </summary>
internal static class SimplIrCodesStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarocLIS", "simplir-codes.json");

    internal static SimplIrCodes Load(ILogger? logger)
    {
        var defaults = new SimplIrCodes(string.Empty, SimplIrCodes.DefaultLowIncomeRateCode, SimplIrCodes.DefaultHighIncomeRateCode);
        try
        {
            if (!File.Exists(FilePath)) return defaults;
            var stored = System.Text.Json.JsonSerializer.Deserialize<SimplIrCodes>(File.ReadAllText(FilePath));
            return stored is null
                ? defaults
                : new SimplIrCodes(stored.Commune ?? string.Empty,
                                   string.IsNullOrWhiteSpace(stored.LowIncomeRateCode) ? defaults.LowIncomeRateCode : stored.LowIncomeRateCode,
                                   string.IsNullOrWhiteSpace(stored.HighIncomeRateCode) ? defaults.HighIncomeRateCode : stored.HighIncomeRateCode);
        }
        catch (Exception ex)
        {
            // A damaged file only costs the codes typed last time.
            logger?.LogWarning(ex, "Reading the Simpl-IR codes failed.");
            return defaults;
        }
    }

    internal static void Save(SimplIrCodes codes, ILogger? logger)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(codes));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Saving the Simpl-IR codes failed.");
        }
    }
}
