using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Assets;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Assets;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// "Immobilisations": the fixed-assets register of a financial year — gross value, depreciation at
/// the opening, the year's charge, the closing and the net book value — with the equipment placed by
/// suppliers listed apart as "mis à disposition". Below, the instrument behind the selected line, read
/// from the equipment module: its state, its interventions and breakdowns.
/// </summary>
public sealed class FixedAssetView : LocalizedView
{
    private readonly SelectionLoads _selectionLoads = new();
    private readonly FixedAssetService _assets;
    private readonly IListPdfRenderer _lists;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<FixedAssetView>? _logger;

    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly DataGridView _interventions = UiTheme.Grid();
    private readonly Label _title = new();
    private readonly Label _heading = new();
    private readonly Label _yearLabel = new();
    private readonly NumericUpDown _year = new() { Minimum = 1990, Maximum = 2100 };
    private readonly Label _instrumentHeading = new();
    private readonly Label _instrumentSummary = new();
    private readonly Label _notice = new();

    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _new = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _fromInstrument = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _edit = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _schedule = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _disposal = UiTheme.SecondaryButton(string.Empty);

    // The register's printouts and the accounts, under « Plus d'actions ▾ ».
    private readonly ContextMenuStrip _moreMenu = new();

    // Kept to translate it with the screen: it stayed « Plus d'actions » in Arabic and English.
    private Button? _more;
    private readonly MenuAction _pdf = new();
    private readonly MenuAction _csv = new();
    private readonly MenuAction _accounts = new();

    private FixedAssetRegister? _register;
    private AssetInstrumentSummary? _summary;

    public FixedAssetView(FixedAssetService assets, IListPdfRenderer lists, IDocumentOutput output,
                          IUserDialogs dialogs, ILocalizer localizer, ICultureManager cultureManager,
                          ILogger<FixedAssetView>? logger = null)
        : base(localizer, cultureManager)
    {
        _assets = assets;
        _lists = lists;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;

        _year.Value = DateTime.Today.Year;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.FixedAssets");

    private int Year => (int)_year.Value;

    private FixedAssetRegisterRow? SelectedAsset => _grid.CurrentRow?.Tag as FixedAssetRegisterRow;

    private Guid? SelectedInstrumentId => _grid.CurrentRow?.Tag switch
    {
        FixedAssetRegisterRow row => row.Asset.InstrumentId,
        SupplierProvidedRow provided => provided.InstrumentId,
        _ => null
    };

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(24, 20, 24, 12);

        // The title, the register's summary in grey under it (it is long: a line of its own), and
        // the actions at the far end, a new asset the primary.
        _more = FinanceUi.MoreButton(_moreMenu, Localizer, _pdf, _csv, _accounts);
        var header = FinanceUi.PageHeader(_title, null, _more, _fromInstrument, _new);
        header.Padding = new Padding(0);

        FinanceUi.Muted(_heading);
        _heading.Dock = DockStyle.Top;
        _heading.Height = 26;
        _heading.AutoEllipsis = true;
        _heading.TextAlign = ContentAlignment.MiddleLeft;

        _year.Width = 80;
        _year.TextAlign = HorizontalAlignment.Right;
        var filters = FinanceUi.FilterRow(FinanceUi.Inline(_yearLabel, _year), _refresh);
        filters.Padding = new Padding(0, 6, 0, 8);

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 330,
            SplitterWidth = 12,
            BackColor = UiTheme.Surface
        };

        BuildColumns();

        // The selected line's actions at the foot of the register.
        var registerBar = FinanceUi.ActionBar(null, [_edit, _schedule, _disposal]);
        split.Panel1.Controls.Add(new FinanceCard(_grid, footer: registerBar));

        _instrumentHeading.Font = UiTheme.SectionTitle;
        _instrumentHeading.ForeColor = UiTheme.TextPrimary;
        _instrumentHeading.Dock = DockStyle.Top;
        _instrumentHeading.Height = 30;
        _instrumentHeading.Padding = new Padding(16, 8, 16, 0);
        _instrumentHeading.AutoEllipsis = true;

        _instrumentSummary.Font = UiTheme.Small;
        _instrumentSummary.ForeColor = UiTheme.TextSecondary;
        _instrumentSummary.Dock = DockStyle.Top;
        _instrumentSummary.Height = 40;
        _instrumentSummary.Padding = new Padding(16, 2, 16, 4);

        var instrument = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceRaised };
        instrument.Controls.Add(_interventions);
        instrument.Controls.Add(_instrumentSummary);
        instrument.Controls.Add(_instrumentHeading);
        split.Panel2.Controls.Add(new FinanceCard(instrument));

        _notice.Dock = DockStyle.Bottom;
        _notice.Height = 36;
        _notice.Font = UiTheme.Small;
        _notice.ForeColor = UiTheme.TextSecondary;
        _notice.Padding = new Padding(0, 6, 0, 0);
        _notice.AutoEllipsis = true;

        Controls.Add(split);
        Controls.Add(_notice);
        Controls.Add(filters);
        Controls.Add(_heading);
        Controls.Add(header);
    }

    private static void AddColumn(DataGridView grid, string name, int weight, Font? font = null,
                                  DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name, FillWeight = weight, ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body, Alignment = align }
        });

    private static readonly string[] RegisterColumns =
    [
        "code", "label", "category", "account", "acquired", "inservice", "gross", "method", "life", "rate",
        "opening", "charge", "grossout", "depreciationout", "closinggross", "closing", "nbv", "disposal", "price",
        "result", "supplier", "invoice", "instrument"
    ];

    private void BuildColumns()
    {
        const DataGridViewContentAlignment right = DataGridViewContentAlignment.MiddleRight;

        foreach (var name in RegisterColumns)
        {
            var amount = name is "gross" or "opening" or "charge" or "grossout" or "depreciationout" or "closinggross"
                                 or "closing" or "nbv" or "price" or "result" or "life" or "rate";
            AddColumn(_grid, name, name switch { "label" => 16, "category" or "supplier" => 10, _ => 7 },
                      name is "code" ? UiTheme.MonospaceBold : amount || name is "account" ? UiTheme.Monospace : UiTheme.Body,
                      amount ? right : DataGridViewContentAlignment.MiddleLeft);
        }

        // Hidden on screen to keep the register readable; the CSV carries them.
        _grid.Column("supplier").Visible = false;
        _grid.Column("invoice").Visible = false;

        AddColumn(_interventions, "date", 8, UiTheme.Monospace);
        AddColumn(_interventions, "kind", 10, UiTheme.Small);
        AddColumn(_interventions, "description", 30);
        AddColumn(_interventions, "downtime", 8, UiTheme.Monospace, right);
        AddColumn(_interventions, "cost", 9, UiTheme.Monospace, right);
        AddColumn(_interventions, "technician", 12, UiTheme.Small);
        AddColumn(_interventions, "check", 8, UiTheme.Small);
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();
        _year.ValueChanged += async (_, _) => await LoadAsync();
        _grid.SelectionChanged += async (_, _) =>
        {
            RefreshButtons();
            await _selectionLoads.RunAsync(() => LoadInstrumentAsync());
        };
        _grid.CellFormatting += OnRegisterFormatting;
        _grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && SelectedAsset is not null) await RunActionAsync(ShowScheduleAsync);
        };
        _interventions.CellFormatting += OnInterventionFormatting;

        _new.Click += async (_, _) => await RunActionAsync(() => EditAsync(null, null));
        _fromInstrument.Click += async (_, _) => await RunActionAsync(FromInstrumentAsync);
        _edit.Click += async (_, _) => await RunActionAsync(() => EditAsync(SelectedAsset?.Asset.Id, null));
        _schedule.Click += async (_, _) => await RunActionAsync(ShowScheduleAsync);
        _disposal.Click += async (_, _) => await RunActionAsync(DisposalAsync);
        _pdf.Click += async (_, _) => await RunBusyAsync(PrintAsync);
        _csv.Click += async (_, _) => await RunBusyAsync(ExportAsync);
        _accounts.Click += async (_, _) => await RunActionAsync(EditAccountsAsync);

        PermissionGate.Require(Permissions.Assets.Manage, _new, _fromInstrument, _edit, _disposal, _accounts);
    }

    protected override void ApplyTexts()
    {
        _title.Text = T("Assets.Title");
        _yearLabel.Text = T("Assets.Year");
        _refresh.Text = T("Action.Refresh");
        _new.Text = T("Assets.New");
        if (_more is not null) _more.Text = T("ResultEntry.MoreActions");
        _fromInstrument.Text = T("Assets.FromInstrument");
        _edit.Text = T("Action.Edit");
        _schedule.Text = T("Assets.Schedule");
        _disposal.Text = T("Assets.Disposal");
        _pdf.Text = T("Assets.Pdf");
        _csv.Text = T("Assets.Csv");
        _accounts.Text = T("Assets.Accounts");
        _notice.Text = T("Assets.Notice");

        var headers = Headers();
        for (var i = 0; i < RegisterColumns.Length; i++) _grid.Column(RegisterColumns[i]).HeaderText = headers[i];

        _interventions.Column("date").HeaderText = T("Field.Date");
        _interventions.Column("kind").HeaderText = T("Equipment.MaintenanceKind");
        _interventions.Column("description").HeaderText = T("Field.Description");
        _interventions.Column("downtime").HeaderText = T("Assets.Downtime");
        _interventions.Column("cost").HeaderText = T("Assets.MaintenanceCost");
        _interventions.Column("technician").HeaderText = T("Equipment.ExternalTechnician");
        _interventions.Column("check").HeaderText = T("Assets.CheckPassed");

        if (_register is not null) Render();
        else RenderHeading();
        RenderInstrument();
    }

    private IReadOnlyList<string> Headers() =>
    [
        T("Field.Code"), T("Assets.Label"), T("Assets.Category"), T("Assets.Account"), T("Assets.AcquisitionDate"),
        T("Assets.InServiceDate"), T("Assets.GrossValue"), T("Assets.Method"), T("Assets.UsefulLife"),
        T("Assets.Rate"), T("Assets.Opening"), T("Assets.Charge"), T("Assets.GrossOut"), T("Assets.DepreciationOut"),
        T("Assets.ClosingGross"), T("Assets.Closing"), T("Assets.NetBookValue"),
        T("Assets.DisposalDate"), T("Assets.DisposalPrice"), T("Assets.DisposalResult"), T("Inventory.Supplier"),
        T("Assets.InvoiceReference"), T("Assets.Instrument")
    ];

    // ---- Loading -------------------------------------------------------------------------------------

    public override Task ActivateAsync(CancellationToken ct = default) => LoadAsync(null, ct);

    private async Task LoadAsync(Guid? keep = null, CancellationToken ct = default)
    {
        try
        {
            keep ??= SelectedAsset?.Asset.Id;

            var result = await _assets.GetRegisterAsync(Year, ct);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _register = result.Value;
            Render(keep);
        }
        catch (OperationCanceledException) { }
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the fixed-assets register failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)));
        }
    }

    private string Category(FixedAssetCategory category) => T($"FixedAssetCategory.{category}");

    private string Method(DepreciationMethod method) => T($"DepreciationMethod.{method}");

    private string ProvidedName(SupplierProvidedRow row) => row.InstrumentName.For(Localizer.CurrentLanguage);

    private void Render(Guid? keep = null)
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();

        var register = _register;
        if (register is not null)
        {
            foreach (var row in register.Assets)
            {
                var index = _grid.Rows.Add([.. FixedAssetRegisterReport.Cells(row, Category(row.Asset.Category),
                                                                                Method(row.Asset.Method))]);
                _grid.Rows[index].Tag = row;
                if (row.Asset.Id == keep) _grid.CurrentCell = _grid.Rows[index].Cells[0];
            }

            foreach (var provided in register.SupplierProvided)
            {
                var cells = FixedAssetRegisterReport.ProvidedCells(provided, ProvidedName(provided), T("Assets.SupplierProvided")).ToArray();
                cells[3] = T($"InstrumentContractKind.{provided.Kind}");
                var index = _grid.Rows.Add([.. cells]);
                _grid.Rows[index].Tag = provided;
            }
        }

        _grid.ResumeLayout(performLayout: true);
        if (keep is null && _grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells[0];

        RenderHeading();
        RefreshButtons();

        // The grid makes its first row current as it is added, before the row carries its
        // asset: that selection loaded nothing, and setting the same row current again raises
        // no event. The instrument of the asset now shown is loaded here, once the rows are filled.
        _ = _selectionLoads.RunAsync(() => LoadInstrumentAsync());
    }

    private void RenderHeading()
    {
        if (_register is not { } r)
        {
            _heading.Text = string.Empty;
            return;
        }

        _heading.Text = T("Assets.Heading", r.Year, r.Assets.Count, FixedAssetRegisterReport.Amount(r.GrossValue),
                          FixedAssetRegisterReport.Amount(r.Charge), FixedAssetRegisterReport.Amount(r.NetBookValue));
        if (r.SupplierProvided.Count > 0) _heading.Text += $" · {T("Assets.SupplierProvidedCount", r.SupplierProvided.Count)}";
    }

    private void OnRegisterFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0) return;

        switch (_grid.Rows[e.RowIndex].Tag)
        {
            case SupplierProvidedRow:
                e.CellStyle!.ForeColor = UiTheme.TextMuted;
                e.CellStyle.Font = UiTheme.Small;
                break;
            case FixedAssetRegisterRow { Asset.DisposalDate: not null }:
                e.CellStyle!.ForeColor = UiTheme.TextMuted;
                break;
        }
    }

    private void OnInterventionFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _interventions.Rows[e.RowIndex].Tag is not AssetMaintenanceRow row) return;
        if (row.Kind == Domain.Equipment.MaintenanceKind.Corrective)
        {
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            if (_interventions.Columns[e.ColumnIndex].Name == "kind") e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
    }

    private async Task LoadInstrumentAsync()
    {
        try
        {
            if (SelectedInstrumentId is not { } instrumentId)
            {
                _summary = null;
                RenderInstrument();
                return;
            }

            var result = await _assets.GetInstrumentSummaryAsync(instrumentId);
            if (SelectedInstrumentId != instrumentId) return;

            _summary = result.IsSuccess ? result.Value : null;
            RenderInstrument();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the instrument of an asset failed.");
        }
    }

    private void RenderInstrument()
    {
        _interventions.SuspendLayout();
        _interventions.Rows.Clear();

        if (_summary is not { } s)
        {
            _instrumentHeading.Text = T("Assets.InstrumentSection");
            _instrumentSummary.Text = SelectedAsset is null && _grid.CurrentRow?.Tag is null
                ? string.Empty
                : T("Assets.NoInstrument");
            _interventions.ResumeLayout();
            return;
        }

        var language = Localizer.CurrentLanguage;
        var culture = CultureInfo.CurrentCulture;

        foreach (var row in s.Interventions)
        {
            var index = _interventions.Rows.Add(
                row.PerformedOn.ToString("dd/MM/yy", culture),
                T($"MaintenanceKind.{row.Kind}"),
                row.Description.For(language),
                row.DowntimeHours?.ToString("0.#", culture) ?? string.Empty,
                row.CostMad?.ToString("N2", culture) ?? string.Empty,
                row.Technician ?? string.Empty,
                row.CheckPassed ? T("Action.Yes") : T("Action.No"));
            _interventions.Rows[index].Tag = row;
        }

        _interventions.ResumeLayout(performLayout: true);

        _instrumentHeading.Text = $"{T("Assets.InstrumentSection")} — {s.Code} · {s.Name.For(language)} · {T($"EquipmentStatus.{s.Status}")}";

        string Date(DateOnly? date) => date?.ToString("dd/MM/yyyy", culture) ?? "—";
        _instrumentSummary.Text = T("Assets.InstrumentSummary",
                                    string.Join(" ", new[] { s.Manufacturer, s.Model }.Where(v => !string.IsNullOrWhiteSpace(v))),
                                    s.SerialNumber ?? "—", s.Interventions.Count, s.Breakdowns,
                                    s.DowntimeHours.ToString("0.#", culture), s.MaintenanceCost.ToString("N2", culture),
                                    Date(s.NextPreventiveMaintenanceDue), Date(s.NextCalibrationDue),
                                    Date(s.MaintenanceContractValidUntil));
    }

    private void RefreshButtons()
    {
        var idle = !IsBusy;
        var asset = SelectedAsset;

        _new.Enabled = idle;
        _fromInstrument.Enabled = idle;
        _edit.Enabled = idle && asset is not null;
        _schedule.Enabled = idle && asset is not null;
        _disposal.Enabled = idle && asset is not null;
        _disposal.Text = asset?.Asset.DisposalDate is not null ? T("Assets.CancelDisposal") : T("Assets.Disposal");
        _pdf.Enabled = idle && _register is not null;
        _csv.Enabled = idle && _register is not null;
        _accounts.Enabled = idle;
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        var ran = await RunBusyAsync(async () =>
        {
            RefreshButtons();
            await action();
        });

        if (ran && !IsDisposed) RefreshButtons();
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private async Task EditAsync(Guid? assetId, SaveFixedAssetRequest? draft)
    {
        try
        {
            var options = await _assets.GetOptionsAsync(assetId);
            if (options.IsFailure) { _dialogs.ShowFailure(options); return; }

            FixedAssetDetail? detail = null;
            if (assetId is { } id)
            {
                var loaded = await _assets.GetAssetAsync(id);
                if (loaded.IsFailure) { _dialogs.ShowFailure(loaded); return; }
                detail = loaded.Value;
            }

            using var dialog = new FixedAssetDialog(Localizer, _dialogs, options.Value, detail?.Values ?? draft,
                                                    supplierId => _assets.GetSupplierInvoicesAsync(supplierId));
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;

            var saved = await _assets.SaveAsync(dialog.Request);
            if (saved.IsFailure) { _dialogs.ShowFailure(saved, dialog.Text); return; }

            await LoadAsync(saved.Value);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Editing a fixed asset failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)));
        }
    }

    private async Task FromInstrumentAsync()
    {
        var options = await _assets.GetOptionsAsync();
        if (options.IsFailure) { _dialogs.ShowFailure(options, _fromInstrument.Text); return; }

        var candidates = FixedAssetService.Candidates(options.Value);
        if (candidates.Count == 0)
        {
            _dialogs.Info(T("Assets.NoCandidates"), _fromInstrument.Text);
            return;
        }

        using var picker = new InstrumentCandidateDialog(Localizer, candidates);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected is not { } instrument) return;

        await EditAsync(null, FixedAssetService.DraftFromInstrument(instrument));
    }

    private async Task ShowScheduleAsync()
    {
        if (SelectedAsset is not { } row) return;

        var schedule = await _assets.GetScheduleAsync(row.Asset.Id);
        if (schedule.IsFailure) { _dialogs.ShowFailure(schedule, _schedule.Text); return; }

        using var dialog = new DepreciationScheduleDialog(Localizer, row, schedule.Value, Method(row.Asset.Method),
                                                          async bytes =>
                                                          {
                                                              var opened = await _output.OpenAsync(bytes, "csv");
                                                              if (opened.IsFailure) _dialogs.ShowFailure(opened);
                                                          });
        dialog.ShowDialog(this);
    }

    private async Task DisposalAsync()
    {
        if (SelectedAsset is not { } row) return;

        if (row.Asset.DisposalDate is not null)
        {
            if (!_dialogs.Confirm(T("Assets.CancelDisposalQuestion", row.Asset.Code), _disposal.Text)) return;

            var cancelled = await _assets.CancelDisposalAsync(row.Asset.Id);
            if (cancelled.IsFailure) { _dialogs.ShowFailure(cancelled, _disposal.Text); return; }

            await LoadAsync(row.Asset.Id);
            return;
        }

        using var dialog = new AssetDisposalDialog(Localizer, _dialogs, row);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var disposed = await _assets.RecordDisposalAsync(row.Asset.Id, dialog.Date, dialog.Price, dialog.Reason);
        if (disposed.IsFailure) { _dialogs.ShowFailure(disposed, dialog.Text); return; }

        await LoadAsync(row.Asset.Id);
    }

    private IReadOnlyList<IReadOnlyList<string>> ReportLines(FixedAssetRegister register) =>
        FixedAssetRegisterReport.Lines(register, Category, Method, ProvidedName, T("Assets.Subtotal"),
                                       T("Assets.Total"), T("Assets.SupplierProvided"));

    private async Task PrintAsync()
    {
        if (_register is not { } register) return;

        // The PDF keeps the accounting columns; supplier, invoice and instrument are in the CSV.
        const int columns = 20;
        var document = new ListDocument(
            T("Assets.ReportTitle", register.Year),
            $"{T("Assets.ReportCaption")} — {DateTime.Now:dd/MM/yyyy HH:mm}",
            [.. Headers().Take(columns)],
            [.. ReportLines(register).Select(l => (IReadOnlyList<string>)[.. l.Take(columns)])],
            Localizer.CurrentLanguage == AppLanguage.Arabic,
            // Twenty columns do not fit A4 at equal widths: dates and amounts were cut in two (S9).
            new ListLayout(FixedAssetRegisterReport.PdfColumnWidths, FixedAssetRegisterReport.PdfFontSize, A3: true));

        var opened = await _output.OpenAsync(_lists.Render(document), "pdf");
        if (opened.IsFailure) _dialogs.ShowFailure(opened, _pdf.Text);
    }

    private async Task ExportAsync()
    {
        if (_register is not { } register) return;

        var opened = await _output.OpenAsync(FixedAssetRegisterReport.ToCsv(register), "csv");
        if (opened.IsFailure) _dialogs.ShowFailure(opened, _csv.Text);
    }

    private async Task EditAccountsAsync()
    {
        var current = await _assets.GetSettingsAsync();
        if (current.IsFailure) { _dialogs.ShowFailure(current, _accounts.Text); return; }

        using var dialog = new FixedAssetAccountsDialog(Localizer, _dialogs, current.Value);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Settings is null) return;

        var saved = await _assets.SaveSettingsAsync(dialog.Settings);
        if (saved.IsFailure) { _dialogs.ShowFailure(saved, _accounts.Text); return; }

        await LoadAsync();
    }
}

/// <summary>Entering or editing an asset.</summary>
internal sealed class FixedAssetDialog : MasterDataEditor
{
    private readonly Guid? _id;
    private readonly FixedAssetOptions _options;
    private readonly Func<Guid, Task<Result<IReadOnlyList<AssetInvoiceOption>>>> _loadInvoices;

    private readonly TextBox _code, _label, _cost, _vat, _residual, _invoiceReference, _notes;
    private readonly ComboBox _category, _method, _supplier, _invoice, _instrument;
    private readonly DateTimePicker _acquired, _inService;
    private readonly NumericUpDown _life;
    private readonly CheckBox _vatRecoverable;
    private readonly Label _plan, _instrumentState;

    private IReadOnlyList<AssetInvoiceOption> _invoices = [];
    private readonly Guid? _initialInvoiceId;

    internal SaveFixedAssetRequest? Request { get; private set; }

    internal FixedAssetDialog(ILocalizer localizer, IUserDialogs dialogs, FixedAssetOptions options,
                              SaveFixedAssetRequest? values,
                              Func<Guid, Task<Result<IReadOnlyList<AssetInvoiceOption>>>> loadInvoices)
        : base(localizer, dialogs, values?.Id is null ? "Assets.New" : "Action.Edit", values?.Code, 900, 520)
    {
        _id = values?.Id;
        _options = options;
        _loadInvoices = loadInvoices;
        _initialInvoiceId = values?.SupplierInvoiceId;

        var language = localizer.CurrentLanguage;
        var category = values?.Category ?? FixedAssetCategory.LaboratoryEquipment;

        static string Amount(decimal value) => value == 0m ? string.Empty : value.ToString("0.00", CultureInfo.InvariantCulture);

        _code = TextField("Field.Code", 20, 14, 160, values?.Code, latin: true, mono: true);
        _code.MaxLength = FixedAsset.CodeMaxLength;
        _label = TextField("Assets.Label", 200, 14, 440, values?.Label);
        _label.MaxLength = FixedAsset.LabelMaxLength;
        _category = Choice("Assets.Category", 660, 14, 220, category, "FixedAssetCategory");

        _acquired = DatePicker("Assets.AcquisitionDate", 20, 68, values?.AcquisitionDate);
        _inService = DatePicker("Assets.InServiceDate", 200, 68, values?.InServiceDate);
        _method = Choice("Assets.Method", 380, 68, 200, values?.Method ?? DepreciationMethod.StraightLine, "DepreciationMethod");
        _life = Number("Assets.UsefulLife", 600, 68, 100,
                       values?.UsefulLifeYears is > 0 and var life ? life : FixedAssetCategories.DefaultUsefulLifeYears(category),
                       FixedAsset.MaximumUsefulLifeYears);
        _residual = TextField("Assets.ResidualValue", 720, 68, 160, Amount(values?.ResidualValueMad ?? 0m),
                              latin: true, mono: true, right: true);

        _cost = TextField("Assets.CostExcludingVat", 20, 122, 160, Amount(values?.CostExcludingVatMad ?? 0m),
                          latin: true, mono: true, right: true);
        _vat = TextField("Assets.Vat", 200, 122, 140, Amount(values?.VatAmountMad ?? 0m), latin: true, mono: true, right: true);
        Numeric(EditorValues.AmountMaxLength, _residual, _cost, _vat);
        _vatRecoverable = Check("Assets.VatRecoverable", 360, 144, values?.VatRecoverable ?? false);
        _plan = new Label
        {
            Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary, AutoSize = false,
            Bounds = new Rectangle(600, 126, 280, 44)
        };
        Controls.Add(_plan);

        _supplier = Lookup("Inventory.Supplier", 20, 176, 300,
                           options.Suppliers.Select(s => (s.Id, s.IsActive ? $"{s.Code} — {s.Name}"
                                                                           : $"{s.Code} — {s.Name} {localizer["Contracts.InactiveSuffix"]}")),
                           values?.SupplierId);
        Label("Assets.SupplierInvoice", 340, 176);
        _invoice = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            Bounds = new Rectangle(340, 194, 300, UiTheme.InputHeight)
        };
        Controls.Add(_invoice);
        _invoiceReference = TextField("Assets.InvoiceReference", 660, 176, 220, values?.InvoiceReference, latin: true);
        _invoiceReference.MaxLength = FixedAsset.InvoiceReferenceMaxLength;

        string InstrumentDisplay(AssetInstrumentOption i)
        {
            var text = $"{i.Code} — {i.Name.For(language)}";
            if (i.ProvidedBy is not null) text += $" ({localizer["Assets.SupplierProvided"]})";
            else if (i.AssetCode is not null) text += $" ({localizer.Format("Assets.AlreadyCapitalised", i.AssetCode)})";
            return text;
        }

        _instrument = Lookup("Assets.Instrument", 20, 230, 420,
                             options.Instruments.Select(i => (i.Id, InstrumentDisplay(i))), values?.InstrumentId);
        _instrumentState = new Label
        {
            Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary, AutoSize = false,
            Bounds = new Rectangle(460, 244, 420, 40)
        };
        Controls.Add(_instrumentState);

        _notes = TextField("Field.Notes", 20, 290, 860, values?.Notes);
        _notes.MaxLength = FixedAsset.NotesMaxLength;
        Notice(20, 346, 860, 100, "Assets.EditorNotice");

        _method.SelectedIndexChanged += (_, _) => RenderPlan();
        _life.ValueChanged += (_, _) => RenderPlan();
        _cost.TextChanged += (_, _) => RenderPlan();
        _vat.TextChanged += (_, _) => RenderPlan();
        _vatRecoverable.CheckedChanged += (_, _) => RenderPlan();
        _instrument.SelectedIndexChanged += (_, _) => RenderInstrumentState();
        _supplier.SelectedIndexChanged += async (_, _) => await LoadInvoicesAsync(null);
        _invoice.SelectedIndexChanged += (_, _) => ApplyInvoice();

        RenderPlan();
        RenderInstrumentState();
        Buttons(460, 900);
        FinanceDialogFrame.Apply(this, localizer["Finance.Dialog.FixedAsset"]);

        // Mirrored in Arabic like every other window: this one was left to right, its labels on
        // the left of their fields and its buttons in the wrong corner.
        RtlLayout.Apply(this, localizer.CurrentLanguage);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await LoadInvoicesAsync(_initialInvoiceId);
    }

    private DateTimePicker DatePicker(string key, int x, int y, DateOnly? value)
    {
        Label(key, x, y);
        var picker = new DateTimePicker
        {
            Bounds = new Rectangle(x, y + 18, 160, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            Value = (value is { } date && date != default ? date : DateOnly.FromDateTime(DateTime.Today))
                    .ToDateTime(TimeOnly.MinValue)
        };
        Controls.Add(picker);
        return picker;
    }

    private void RenderPlan()
    {
        var life = (int)_life.Value;
        var gross = FixedAsset.GrossValue(DecimalOrNull(_cost) ?? 0m, DecimalOrNull(_vat) ?? 0m, _vatRecoverable.Checked);
        var grossText = gross.ToString("N2", CultureInfo.CurrentCulture);

        _plan.Text = Selected<DepreciationMethod>(_method) == DepreciationMethod.Declining
            ? Depreciation.DecliningCoefficient(life) is { } coefficient
                ? Localizer.Format("Assets.PlanDeclining", grossText, coefficient.ToString("0.#", CultureInfo.CurrentCulture),
                                   FixedAssetRegisterReport.Percent(Depreciation.DecliningRate(life) ?? 0m))
                : Localizer["Asset_DecliningNeedsThreeYears"]
            : Localizer.Format("Assets.PlanStraightLine", grossText,
                               FixedAssetRegisterReport.Percent(Depreciation.StraightLineRate(life)));
    }

    private AssetInstrumentOption? SelectedInstrument =>
        SelectedId(_instrument) is { } id ? _options.Instruments.FirstOrDefault(i => i.Id == id) : null;

    private void RenderInstrumentState()
    {
        var instrument = SelectedInstrument;
        _instrumentState.ForeColor = UiTheme.TextSecondary;
        _instrumentState.Text = instrument switch
        {
            null => string.Empty,
            { ProvidedBy: { } contract } => Localizer.Format("Asset_SupplierProvided", contract),
            { AssetCode: { } code } => Localizer.Format("Asset_InstrumentAlreadyCapitalised", code),
            { PurchaseDate: { } bought } => Localizer.Format("Assets.InstrumentBought", bought.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture)),
            _ => Localizer["Assets.InstrumentNoPurchaseDate"]
        };

        if (instrument is { ProvidedBy: not null } or { AssetCode: not null })
            _instrumentState.ForeColor = UiTheme.Critical;
    }

    private async Task LoadInvoicesAsync(Guid? select)
    {
        _invoice.Items.Clear();
        _invoice.Items.Add(new LookupChoice(null, "—"));
        _invoice.SelectedIndex = 0;
        _invoices = [];

        if (SelectedId(_supplier) is not { } supplierId) return;

        try
        {
            var loaded = await _loadInvoices(supplierId);
            if (loaded.IsFailure || IsDisposed || SelectedId(_supplier) != supplierId) return;

            _invoices = loaded.Value;
            foreach (var invoice in _invoices)
            {
                var index = _invoice.Items.Add(new LookupChoice(invoice.Id,
                    $"{invoice.InvoiceNumber} · {invoice.InvoiceDate:dd/MM/yyyy} · {invoice.TotalExcludingVat.ToString("N2", CultureInfo.CurrentCulture)}"));
                if (invoice.Id == select) _invoice.SelectedIndex = index;
            }
        }
        catch (Exception)
        {
            // The invoice list is a convenience: the reference can still be typed.
        }
    }

    /// <summary>
    /// Picking an invoice fills its number and, when they are still empty, the amounts and the
    /// acquisition date: one invoice may bill more than this asset, so nothing typed is overwritten.
    /// </summary>
    private void ApplyInvoice()
    {
        if (SelectedId(_invoice) is not { } id || _invoices.FirstOrDefault(i => i.Id == id) is not { } invoice) return;

        _invoiceReference.Text = invoice.InvoiceNumber;
        if (string.IsNullOrWhiteSpace(_cost.Text))
        {
            _cost.Text = invoice.TotalExcludingVat.ToString("0.00", CultureInfo.InvariantCulture);
            _vat.Text = invoice.TotalVat.ToString("0.00", CultureInfo.InvariantCulture);
        }
        if (_id is null) _acquired.Value = invoice.InvoiceDate.ToDateTime(TimeOnly.MinValue);
    }

    protected override void Accept()
    {
        if (string.IsNullOrWhiteSpace(_code.Text)) { Dialogs.Warn(Localizer["Asset_CodeRequired"], Text); _code.Focus(); return; }
        if (string.IsNullOrWhiteSpace(_label.Text)) { Dialogs.Warn(Localizer["Asset_LabelRequired"], Text); _label.Focus(); return; }

        var cost = DecimalOrNull(_cost);
        if (cost is not > 0m) { Dialogs.Warn(Localizer["Asset_CostRequired"], Text); _cost.Focus(); return; }

        var vat = string.IsNullOrWhiteSpace(_vat.Text) ? 0m : DecimalOrNull(_vat);
        var residual = string.IsNullOrWhiteSpace(_residual.Text) ? 0m : DecimalOrNull(_residual);
        if (vat is not >= 0m || residual is not >= 0m) { Dialogs.Warn(Localizer["Asset_AmountNegative"], Text); return; }

        if (SelectedInstrument is { ProvidedBy: { } contract })
        {
            Dialogs.Warn(Localizer.Format("Asset_SupplierProvided", contract), Text);
            return;
        }

        Request = new SaveFixedAssetRequest
        {
            Id = _id,
            Code = _code.Text.Trim(),
            Label = _label.Text.Trim(),
            Category = Selected<FixedAssetCategory>(_category),
            AcquisitionDate = DateOnly.FromDateTime(_acquired.Value.Date),
            InServiceDate = DateOnly.FromDateTime(_inService.Value.Date),
            CostExcludingVatMad = cost.Value,
            VatAmountMad = vat.Value,
            VatRecoverable = _vatRecoverable.Checked,
            SupplierId = SelectedId(_supplier),
            InvoiceReference = Blank(_invoiceReference),
            SupplierInvoiceId = SelectedId(_invoice),
            InstrumentId = SelectedId(_instrument),
            Method = Selected<DepreciationMethod>(_method),
            UsefulLifeYears = (int)_life.Value,
            ResidualValueMad = residual.Value,
            Notes = Blank(_notes)
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>Picking the instrument a new asset is created from.</summary>
internal sealed class InstrumentCandidateDialog : Form
{
    private readonly DataGridView _grid = UiTheme.Grid();

    internal AssetInstrumentOption? Selected { get; private set; }

    internal InstrumentCandidateDialog(ILocalizer localizer, IReadOnlyList<AssetInstrumentOption> candidates)
    {
        Text = localizer["Assets.FromInstrument"];
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(720, 420);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var language = localizer.CurrentLanguage;
        foreach (var key in new[] { "Field.Code", "Assets.Instrument", "Assets.PurchaseDate", "Assets.InServiceDate" })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = localizer[key], ReadOnly = true });

        foreach (var candidate in candidates)
        {
            var index = _grid.Rows.Add(candidate.Code, candidate.Name.For(language),
                                       candidate.PurchaseDate?.ToString("dd/MM/yyyy") ?? string.Empty,
                                       candidate.CommissioningDate?.ToString("dd/MM/yyyy") ?? string.Empty);
            _grid.Rows[index].Tag = candidate;
        }

        _grid.Dock = DockStyle.Fill;
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Choose(); };

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 90, BackColor = UiTheme.SurfaceRaised };
        var notice = new Label
        {
            Text = localizer["Assets.FromInstrumentNotice"], Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary,
            AutoSize = false, Bounds = new Rectangle(12, 6, 696, 36)
        };
        var ok = UiTheme.PrimaryButton(localizer["Action.Select"]);
        ok.Bounds = new Rectangle(12, 48, 200, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Choose();
        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(222, 48, 200, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;
        footer.Controls.AddRange([notice, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(_grid);
        Controls.Add(footer);

        FinanceDialogFrame.Apply(this, localizer["Finance.Dialog.FromInstrument"], [], cancel, ok);

        // Mirrored in Arabic like every other window: this one was left to right, its labels on
        // the left of their fields and its buttons in the wrong corner.
        RtlLayout.Apply(this, localizer.CurrentLanguage);
    }

    private void Choose()
    {
        if (_grid.CurrentRow?.Tag is not AssetInstrumentOption candidate) return;
        Selected = candidate;
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>An asset's depreciation plan, by year and by month.</summary>
internal sealed class DepreciationScheduleDialog : Form
{
    internal DepreciationScheduleDialog(ILocalizer localizer, FixedAssetRegisterRow row, DepreciationSchedule schedule,
                                        string method, Func<byte[], Task> openCsv)
    {
        Text = localizer.Format("Assets.ScheduleTitle", row.Asset.Code, row.Asset.Label);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(900, 620);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var culture = CultureInfo.CurrentCulture;
        string N(decimal v) => v.ToString("N2", culture);

        var header = new Label
        {
            Text = localizer.Format("Assets.ScheduleHeader", N(schedule.GrossValue), method, row.Asset.UsefulLifeYears,
                                    row.Asset.AcquisitionDate.ToString("dd/MM/yyyy", culture),
                                    row.Asset.InServiceDate.ToString("dd/MM/yyyy", culture),
                                    row.Accounts.Charge, row.Accounts.Depreciation),
            Dock = DockStyle.Top, Height = 44, Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary,
            Padding = new Padding(8, 4, 8, 4)
        };

        var years = UiTheme.Grid();
        years.Dock = DockStyle.Top;
        years.Height = 240;
        foreach (var key in new[] { "Assets.Year", "Assets.Months", "Assets.Opening", "Assets.Charge", "Assets.Closing",
                                    "Assets.NetBookValue", "Assets.Rate", "Assets.Mode" })
            years.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = localizer[key], ReadOnly = true });

        foreach (var y in schedule.Years)
            years.Rows.Add(y.Year, y.Months, N(y.Opening), N(y.Charge), N(y.Closing), N(y.NetBookValue),
                           FixedAssetRegisterReport.Percent(y.Rate),
                           y.StraightLine ? localizer["DepreciationMethod.StraightLine"] : localizer["DepreciationMethod.Declining"]);

        var months = UiTheme.Grid();
        months.Dock = DockStyle.Fill;
        foreach (var key in new[] { "Assets.Month", "Field.Date", "Assets.Charge", "Assets.Closing" })
            months.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = localizer[key], ReadOnly = true });

        var accumulated = 0m;
        foreach (var m in schedule.Months)
        {
            accumulated += m.Amount;
            months.Rows.Add($"{m.Month:00}/{m.Year}", m.Date.ToString("dd/MM/yyyy", culture), N(m.Amount), N(accumulated));
        }

        Label Heading(string key) => new()
        {
            Text = localizer[key], Dock = DockStyle.Top, Height = 26, Font = UiTheme.SectionTitle,
            ForeColor = UiTheme.TextPrimary, Padding = new Padding(4, 4, 4, 0)
        };

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = UiTheme.SurfaceRaised };
        var csv = UiTheme.SecondaryButton(localizer["Assets.Csv"]);
        csv.Bounds = new Rectangle(8, 8, 160, UiTheme.ButtonHeight);
        csv.Click += async (_, _) => await openCsv(FixedAssetRegisterReport.ScheduleCsv(row.Asset.Code, schedule));
        var close = UiTheme.SecondaryButton(localizer["Action.Close"]);
        close.Bounds = new Rectangle(178, 8, 160, UiTheme.ButtonHeight);
        close.DialogResult = DialogResult.Cancel;
        footer.Controls.AddRange([csv, close]);
        CancelButton = close;

        // Docked in reverse: the last added top control sits highest.
        Controls.Add(months);
        Controls.Add(Heading("Assets.Monthly"));
        Controls.Add(years);
        Controls.Add(Heading("Assets.Yearly"));
        Controls.Add(header);
        Controls.Add(footer);

        FinanceDialogFrame.Apply(this, localizer["Finance.Dialog.Schedule"], [csv], close, null);

        // Mirrored in Arabic like every other window: this one was left to right, its labels on
        // the left of their fields and its buttons in the wrong corner.
        RtlLayout.Apply(this, localizer.CurrentLanguage);
    }
}

/// <summary>An asset leaving the register: when, for how much, why.</summary>
internal sealed class AssetDisposalDialog : MasterDataEditor
{
    private readonly DateTimePicker _date;
    private readonly TextBox _price, _reason;

    internal DateOnly Date { get; private set; }
    internal decimal Price { get; private set; }
    internal string Reason { get; private set; } = string.Empty;

    internal AssetDisposalDialog(ILocalizer localizer, IUserDialogs dialogs, FixedAssetRegisterRow row)
        : base(localizer, dialogs, "Assets.Disposal", row.Asset.Code, 520, 250)
    {
        Label("Assets.DisposalDate", 20, 14);
        _date = new DateTimePicker
        {
            Bounds = new Rectangle(20, 32, 160, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            MaxDate = DateTime.Today,
            Value = DateTime.Today
        };
        Controls.Add(_date);
        _price = TextField("Assets.DisposalPrice", 200, 14, 140, null, latin: true, mono: true, right: true);
        Numeric(EditorValues.AmountMaxLength, _price);
        _reason = TextField("Assets.DisposalReason", 20, 68, 480);
        _reason.MaxLength = FixedAsset.DisposalReasonMaxLength;
        Notice(20, 122, 480, 60, "Assets.DisposalNotice");
        Buttons(190, 520);
        FinanceDialogFrame.Apply(this, localizer["Finance.Dialog.Disposal"]);

        // Mirrored in Arabic like every other window: this one was left to right, its labels on
        // the left of their fields and its buttons in the wrong corner.
        RtlLayout.Apply(this, localizer.CurrentLanguage);
    }

    protected override void Accept()
    {
        var price = string.IsNullOrWhiteSpace(_price.Text) ? 0m : DecimalOrNull(_price);
        if (price is not >= 0m) { Dialogs.Warn(Localizer["Asset_AmountNegative"], Text); return; }
        if (string.IsNullOrWhiteSpace(_reason.Text)) { Dialogs.Warn(Localizer["Asset_DisposalReasonRequired"], Text); return; }

        Date = DateOnly.FromDateTime(_date.Value.Date);
        Price = price.Value;
        Reason = _reason.Text.Trim();
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>The CGNC accounts of each category: asset, depreciation, charge.</summary>
internal sealed class FixedAssetAccountsDialog : MasterDataEditor
{
    private readonly FixedAssetSettings _current;
    private readonly Dictionary<FixedAssetCategory, (TextBox Asset, TextBox Depreciation, TextBox Charge)> _boxes = [];

    internal FixedAssetSettings? Settings { get; private set; }

    internal FixedAssetAccountsDialog(ILocalizer localizer, IUserDialogs dialogs, FixedAssetSettings current)
        : base(localizer, dialogs, "Assets.Accounts", null, 700, 140 + Enum.GetValues<FixedAssetCategory>().Length * 54)
    {
        _current = current;

        var y = 14;
        foreach (var category in Enum.GetValues<FixedAssetCategory>())
        {
            var accounts = current.For(category);
            var name = new Label
            {
                Text = localizer[$"FixedAssetCategory.{category}"], Font = UiTheme.BodyBold, AutoSize = false,
                // Two lines when the category's name is longer than the column (« Agencements et
                // aménagements »), centred on the boxes beside it.
                Bounds = new Rectangle(20, y + 14, 224, 36), TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(name);
            _boxes[category] = (TextField("Assets.AssetAccount", 250, y, 130, accounts.Asset, latin: true, mono: true),
                                TextField("Assets.DepreciationAccount", 400, y, 130, accounts.Depreciation, latin: true, mono: true),
                                TextField("Assets.ChargeAccount", 550, y, 130, accounts.Charge, latin: true, mono: true));
            // A CGNC account has at most 20 digits (FixedAssetSettings.IsValid): typing stops there.
            foreach (var box in new[] { _boxes[category].Asset, _boxes[category].Depreciation, _boxes[category].Charge })
                box.MaxLength = 20;
            y += 54;
        }

        Notice(20, y + 4, 660, 50, "Assets.AccountsNotice");
        Buttons(y + 64, 700);
        FinanceDialogFrame.Apply(this, localizer["Finance.Dialog.AssetAccounts"]);

        // Mirrored in Arabic like every other window: this one was left to right, its labels on
        // the left of their fields and its buttons in the wrong corner.
        RtlLayout.Apply(this, localizer.CurrentLanguage);
    }

    protected override void Accept()
    {
        var settings = _current with
        {
            Accounts = _boxes.ToDictionary(b => b.Key,
                                           b => new CategoryAccounts(b.Value.Asset.Text.Trim(), b.Value.Depreciation.Text.Trim(),
                                                                     b.Value.Charge.Text.Trim()))
        };

        if (!settings.IsValid())
        {
            Dialogs.Warn(Localizer["Asset_AccountInvalid"], Text);
            return;
        }

        Settings = settings;
        DialogResult = DialogResult.OK;
        Close();
    }
}
