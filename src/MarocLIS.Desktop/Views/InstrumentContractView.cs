using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Instrument contracts (placement, comodat, lease, purchase) and their dashboard: committed against
/// consumed pro rata of the time elapsed, the projection to the end of the period with the risk
/// alert under 90 %, the end of the contract within 90 days, and the supplier's interventions on the
/// contract's analysers against the delay the contract allows.
/// </summary>
public sealed class InstrumentContractView : LocalizedView
{
    private readonly SelectionLoads _selectionLoads = new();
    private readonly InstrumentContractService _contracts;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<InstrumentContractView>? _logger;

    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly DataGridView _interventions = UiTheme.Grid();
    // Direction A: title and count, search and chip, the contracts card and the interventions card.
    private readonly Label _heading = StockPage.Title();
    private readonly Label _count = StockPage.Count();
    private readonly StockSearchBox _search = new();
    private readonly StockFooter _rule = new();
    private readonly CheckBox _includeInactive = new StockChip();
    private readonly StockCard _interventionsCard;
    private readonly ContextMenuStrip _more = new();
    private readonly Button _moreButton;

    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _new = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _edit = UiTheme.SecondaryButton(string.Empty);
    private readonly StockMenuButton _attach = new();
    private readonly StockMenuButton _openFile = new();
    private readonly Button _times = UiTheme.SecondaryButton(string.Empty);

    private IReadOnlyList<InstrumentContractRow> _rows = [];
    private IReadOnlyList<ContractInterventionRow> _interventionRows = [];

    public InstrumentContractView(InstrumentContractService contracts, IDocumentOutput output, IUserDialogs dialogs,
                                  ILocalizer localizer, ICultureManager cultureManager,
                                  ILogger<InstrumentContractView>? logger = null)
        : base(localizer, cultureManager)
    {
        _contracts = contracts;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;

        _interventionsCard = new StockCard(_interventions);
        _moreButton = StockMenu.More(_more, localizer);
        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Contracts");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = StockPage.ContentPadding;

        // The contract's file is reached less often than the contract itself: « Plus d'actions ».
        foreach (var button in new[] { _attach, _openFile }) _more.Items.Add(StockMenu.Entry(button));
        var header = StockPage.Header(_heading, _count, _refresh, _edit, _moreButton, _new);
        ShortcutHints.Attach(_new, NewKey);
        var filters = StockPage.Filters(_search, _includeInactive);

        BuildColumns();
        StockGrid.Pills(_grid, "alert", tag => tag is InstrumentContractRow row ? AlertPill(row) : null);
        _search.Changed += (_, _) => StockGrid.Filter(_grid, _search.Query);
        var contractsCard = new StockCard(_grid, heading: false, footer: _rule);

        StockPage.Action(_times);
        _interventionsCard.Heading.End(_times);

        Controls.Add(StockPage.Stack(contractsCard, _interventionsCard, 300));
        Controls.Add(filters);
        Controls.Add(header);
    }

    private const string NewKey = "Ctrl+N";

    /// <summary>Ctrl+N enters a new contract, as the key drawn on the button says.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.N) && _new.Enabled && _new.Visible)
        {
            _new.PerformClick();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static StockPill? AlertPill(InstrumentContractRow row) =>
        row.Commitment.AtRisk ? StockPill.Critical
        : row.IsEndingSoon || row.DelaysExceeded > 0 ? StockPill.Warning
        : !row.IsActive || row.DaysToEnd < 0 ? StockPill.Waiting
        : null;

    private static void AddColumn(DataGridView grid, string name, int weight, Font? font = null,
                                  DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name, FillWeight = weight, ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body, Alignment = align }
        });

    private void BuildColumns()
    {
        const DataGridViewContentAlignment right = DataGridViewContentAlignment.MiddleRight;

        AddColumn(_grid, "reference", 10, UiTheme.MonospaceBold);
        AddColumn(_grid, "supplier", 12);
        AddColumn(_grid, "kind", 8, UiTheme.Small);
        AddColumn(_grid, "instruments", 10, UiTheme.Small);
        AddColumn(_grid, "end", 8, UiTheme.Monospace);
        AddColumn(_grid, "basis", 10, UiTheme.Small);
        AddColumn(_grid, "period", 11, UiTheme.Monospace);
        AddColumn(_grid, "committed", 8, UiTheme.Monospace, right);
        AddColumn(_grid, "expected", 8, UiTheme.Monospace, right);
        AddColumn(_grid, "consumed", 8, UiTheme.Monospace, right);
        AddColumn(_grid, "projected", 8, UiTheme.Monospace, right);
        AddColumn(_grid, "ratio", 6, UiTheme.MonospaceBold, right);
        AddColumn(_grid, "alert", 14, UiTheme.SmallBold);
        AddColumn(_grid, "maintenance", 11, UiTheme.Small);

        AddColumn(_interventions, "date", 8, UiTheme.Monospace);
        AddColumn(_interventions, "instrument", 8, UiTheme.Monospace);
        AddColumn(_interventions, "kind", 10, UiTheme.Small);
        AddColumn(_interventions, "description", 26);
        AddColumn(_interventions, "downtime", 8, UiTheme.Monospace, right);
        AddColumn(_interventions, "requested", 11, UiTheme.Monospace);
        AddColumn(_interventions, "arrived", 11, UiTheme.Monospace);
        AddColumn(_interventions, "delay", 8, UiTheme.MonospaceBold, right);
        AddColumn(_interventions, "technician", 10, UiTheme.Small);
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();
        _includeInactive.CheckedChanged += async (_, _) => await LoadAsync();
        _grid.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadInterventionsAsync());
        _grid.CellFormatting += OnContractFormatting;
        _grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && !PermissionGate.IsBlocked(_edit))
                await RunActionAsync(() => EditAsync(SelectedContract?.Id));
        };
        _interventions.CellFormatting += OnInterventionFormatting;
        _interventions.SelectionChanged += (_, _) => RefreshButtons();

        _new.Click += async (_, _) => await RunActionAsync(() => EditAsync(null));
        _edit.Click += async (_, _) => await RunActionAsync(() => EditAsync(SelectedContract?.Id));
        _attach.Click += async (_, _) => await RunActionAsync(AttachAsync);
        _openFile.Click += async (_, _) => await OpenFileAsync();
        _times.Click += async (_, _) => await RunActionAsync(RecordTimesAsync);

        PermissionGate.Require(Permissions.Equipment.ManageContracts, _new, _edit, _attach);
        PermissionGate.Require(Permissions.Equipment.RecordMaintenance, _times);
    }

    protected override void ApplyTexts()
    {
        _heading.Text = T("Contracts.Title");
        _search.Placeholder = T("StocksUi.SearchContracts");
        _moreButton.Text = T("ResultEntry.MoreActions");
        _interventionsCard.Title.Text = T("Contracts.Interventions");
        _includeInactive.Text = T("Contracts.IncludeInactive");
        _refresh.Text = T("Action.Refresh");
        _new.Text = T("Contracts.New");
        _edit.Text = T("Action.Edit");
        _attach.Text = T("Contracts.AttachFile");
        _openFile.Text = T("Contracts.OpenFile");
        _times.Text = T("Contracts.InterventionTimes");
        _rule.Text = T("Contracts.AlertRule", (int)(ContractCommitment.RiskThreshold * 100m),
                       InstrumentContract.EndingSoonDays);

        _grid.Column("reference").HeaderText = T("Contracts.Reference");
        _grid.Column("supplier").HeaderText = T("Inventory.Supplier");
        _grid.Column("kind").HeaderText = T("Contracts.Kind");
        _grid.Column("instruments").HeaderText = T("Contracts.Instruments");
        _grid.Column("end").HeaderText = T("Contracts.End");
        _grid.Column("basis").HeaderText = T("Contracts.Basis");
        _grid.Column("period").HeaderText = T("Contracts.Period");
        _grid.Column("committed").HeaderText = T("Contracts.Committed");
        _grid.Column("expected").HeaderText = T("Contracts.ExpectedToDate");
        _grid.Column("consumed").HeaderText = T("Contracts.Consumed");
        _grid.Column("projected").HeaderText = T("Contracts.Projected");
        _grid.Column("ratio").HeaderText = T("Contracts.Ratio");
        _grid.Column("alert").HeaderText = T("Contracts.Alert");
        _grid.Column("maintenance").HeaderText = T("Contracts.Maintenance");

        _interventions.Column("date").HeaderText = T("Field.Date");
        _interventions.Column("instrument").HeaderText = T("Contracts.Instrument");
        _interventions.Column("kind").HeaderText = T("Equipment.MaintenanceKind");
        _interventions.Column("description").HeaderText = T("Field.Description");
        _interventions.Column("downtime").HeaderText = T("Equipment.DowntimeHours");
        _interventions.Column("requested").HeaderText = T("Contracts.RequestedAt");
        _interventions.Column("arrived").HeaderText = T("Contracts.ArrivedAt");
        _interventions.Column("delay").HeaderText = T("Contracts.DelayHours");
        _interventions.Column("technician").HeaderText = T("Equipment.ExternalTechnician");

        RenderHeading();
        if (_rows.Count > 0) Render();
        RenderInterventions();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override Task ActivateAsync(CancellationToken ct = default) => LoadAsync(null, ct);

    private InstrumentContractRow? SelectedContract => _grid.CurrentRow?.Tag as InstrumentContractRow;
    private ContractInterventionRow? SelectedIntervention => _interventions.CurrentRow?.Tag as ContractInterventionRow;

    private static string Number(decimal value, CommitmentBasis basis) =>
        ContractCommitment.CountsPurchases(basis)
            ? value.ToString("N2", CultureInfo.CurrentCulture)
            : value.ToString("N0", CultureInfo.CurrentCulture);

    private async Task LoadAsync(Guid? keep = null, CancellationToken ct = default)
    {
        try
        {
            keep ??= SelectedContract?.Id;

            var result = await _contracts.GetContractsAsync(_includeInactive.Checked, ct);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _rows = result.Value;
            Render(keep);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading instrument contracts failed.");
            _dialogs.Warn(T("Message.UnexpectedError"));
        }
    }

    private void Render(Guid? keep = null)
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var row in _rows)
        {
            var c = row.Commitment;
            var hasCommitment = c.Basis != CommitmentBasis.None;

            var index = _grid.Rows.Add(
                row.Reference,
                row.SupplierName,
                T($"InstrumentContractKind.{row.Kind}"),
                row.Instruments,
                row.DaysToEnd >= 0 ? $"{row.EndDate:dd/MM/yy} ({row.DaysToEnd} {T("Quotes.DaysShort")})" : row.EndDate.ToString("dd/MM/yy"),
                T($"CommitmentBasis.{c.Basis}"),
                hasCommitment ? $"{c.PeriodStart:dd/MM/yy}–{c.PeriodEnd:dd/MM/yy}" : string.Empty,
                hasCommitment ? Number(c.Committed, c.Basis) : string.Empty,
                hasCommitment ? Number(c.ExpectedToDate, c.Basis) : string.Empty,
                hasCommitment ? Number(c.Consumed, c.Basis) : string.Empty,
                hasCommitment ? Number(c.Projected, c.Basis) : string.Empty,
                c.ProjectedRatio is { } ratio && hasCommitment ? $"{ratio * 100m:0} %" : string.Empty,
                AlertText(row),
                MaintenanceText(row));

            _grid.Rows[index].Tag = row;
            if (row.Id == keep) _grid.CurrentCell = _grid.Rows[index].Cells[0];
        }

        _grid.ResumeLayout(performLayout: true);
        StockGrid.Filter(_grid, _search.Query);
        RenderHeading();

        if (keep is null && _grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells[0];
        if (_grid.Rows.Count == 0)
        {
            _interventionRows = [];
            RenderInterventions();
        }

        RefreshButtons();
    }

    private string AlertText(InstrumentContractRow row)
    {
        var alerts = new List<string>();
        if (!row.IsActive) alerts.Add(T("Contracts.Inactive"));
        else if (row.DaysToEnd < 0) alerts.Add(T("Contracts.Expired"));
        if (row.Commitment.AtRisk) alerts.Add(T("Contracts.AtRisk"));
        if (row.IsEndingSoon) alerts.Add(T("Contracts.EndingSoon"));
        if (row.DelaysExceeded > 0) alerts.Add(T("Contracts.DelaysExceeded", row.DelaysExceeded));
        return string.Join(" · ", alerts);
    }

    private string MaintenanceText(InstrumentContractRow row) =>
        row.Interventions == 0
            ? string.Empty
            : T("Contracts.MaintenanceSummary", row.Interventions,
                row.DowntimeHours.ToString("0.#", CultureInfo.CurrentCulture));

    private void RenderHeading()
    {
        var atRisk = _rows.Count(r => r.Commitment.AtRisk);
        var ending = _rows.Count(r => r.IsEndingSoon);

        var text = T("StocksUi.Contracts", _rows.Count);
        if (atRisk > 0) text += $" · {T("Contracts.AtRisk")} {atRisk}";
        if (ending > 0) text += $" · {T("Contracts.EndingSoon")} {ending}";
        _count.Text = text;
    }

    private void OnContractFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not InstrumentContractRow row) return;

        if (row.Commitment.AtRisk)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            if (_grid.Columns[e.ColumnIndex].Name is "alert" or "ratio") e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.IsEndingSoon || row.DelaysExceeded > 0)
        {
            // A reminder, not an emergency: the alert pill and the end date carry it, not the row.
            if (row.IsEndingSoon && _grid.Columns[e.ColumnIndex].Name == "end")
            {
                e.CellStyle!.ForeColor = UiTheme.Abnormal;
                e.CellStyle.Font = UiTheme.MonospaceBold;
            }
        }
        else if (!row.IsActive || row.DaysToEnd < 0)
        {
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
        }
    }

    private void OnInterventionFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _interventions.Rows[e.RowIndex].Tag is not ContractInterventionRow row) return;

        // The delay beyond what the contract allows is marked on its own cell, not on the row.
        if (row.DelayExceeded && _interventions.Columns[e.ColumnIndex].Name == "delay")
        {
            e.CellStyle!.ForeColor = UiTheme.Critical;
            e.CellStyle.BackColor = UiTheme.CriticalBackground;
        }
    }

    private async Task LoadInterventionsAsync()
    {
        try
        {
            if (SelectedContract is not { } contract)
            {
                _interventionRows = [];
                RenderInterventions();
                return;
            }

            var result = await _contracts.GetInterventionsAsync(contract.Id);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }
            if (SelectedContract?.Id != contract.Id) return;

            _interventionRows = result.Value;
            RenderInterventions();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading contract interventions failed.");
        }
    }

    private void RenderInterventions()
    {
        var keep = SelectedIntervention?.MaintenanceRecordId;

        _interventions.SuspendLayout();
        _interventions.Rows.Clear();

        foreach (var row in _interventionRows)
        {
            var index = _interventions.Rows.Add(
                row.PerformedOn.ToString("dd/MM/yy"),
                row.InstrumentCode,
                T($"MaintenanceKind.{row.Kind}"),
                row.Description,
                row.DowntimeHours?.ToString("0.#", CultureInfo.CurrentCulture) ?? string.Empty,
                row.RequestedAt?.ToLocalTime().ToString("dd/MM/yy HH:mm") ?? string.Empty,
                row.ArrivedAt?.ToLocalTime().ToString("dd/MM/yy HH:mm") ?? string.Empty,
                row.DelayHours?.ToString("0.#", CultureInfo.CurrentCulture) ?? string.Empty,
                row.Technician ?? string.Empty);

            _interventions.Rows[index].Tag = row;
            if (row.MaintenanceRecordId == keep) _interventions.CurrentCell = _interventions.Rows[index].Cells[0];
        }

        _interventions.ResumeLayout(performLayout: true);

        var contract = SelectedContract;
        _interventionsCard.Note.Text = contract is null
            ? string.Empty
            : contract.MaxInterventionDelayHours is { } max
                ? $"{contract.Reference} · {T("Contracts.MaxDelayShort", max)}"
                : contract.Reference;

        RefreshButtons();
    }

    private void RefreshButtons()
    {
        // Nothing that writes is offered while a write is in flight (see RunActionAsync).
        var idle = !IsBusy;

        _new.Enabled = idle;
        _edit.Enabled = idle && SelectedContract is not null;
        _attach.Enabled = idle && SelectedContract is not null;
        _openFile.Enabled = idle && SelectedContract is { FileName: not null };
        _times.Enabled = idle && SelectedIntervention is not null;
    }

    /// <summary>
    /// Runs one write with every write button held disabled until it ends, and the view flagged busy
    /// so that leaving the screen does not dispose its context under a pending save.
    /// </summary>
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

    private async Task EditAsync(Guid? contractId)
    {
        try
        {
            var options = await _contracts.GetOptionsAsync(contractId);
            if (options.IsFailure) { _dialogs.ShowFailure(options); return; }

            InstrumentContractDetail? detail = null;
            if (contractId is { } id)
            {
                var loaded = await _contracts.GetContractAsync(id);
                if (loaded.IsFailure) { _dialogs.ShowFailure(loaded); return; }
                detail = loaded.Value;
            }

            using var dialog = new InstrumentContractDialog(Localizer, _dialogs, options.Value, detail);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;

            var saved = await _contracts.SaveAsync(dialog.Request);
            if (saved.IsFailure) { _dialogs.ShowFailure(saved, dialog.Text); return; }

            await LoadAsync(saved.Value);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Editing an instrument contract failed.");
            _dialogs.Warn(T("Message.UnexpectedError"));
        }
    }

    private async Task AttachAsync()
    {
        if (SelectedContract is not { } contract) return;

        if (contract.FileName is not null
            && !_dialogs.Confirm(T("Contracts.ReplaceFileQuestion", contract.FileName), _attach.Text))
            return;

        using var picker = new OpenFileDialog
        {
            Title = _attach.Text,
            Filter = "PDF, images, Office (*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.docx)|*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.docx",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        byte[] content;
        try
        {
            var info = new FileInfo(picker.FileName);
            if (info.Length > PurchasingDocument.MaximumBytes)
            {
                _dialogs.Warn(T("Purchasing_FileTooLarge", PurchasingDocument.MaximumBytes / (1024 * 1024)), _attach.Text);
                return;
            }
            content = await File.ReadAllBytesAsync(info.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Warn($"{T("Documents.FileUnreadable")}\n{ex.Message}", _attach.Text);
            return;
        }

        var attached = await _contracts.AttachFileAsync(contract.Id, Path.GetFileName(picker.FileName), content);
        if (attached.IsFailure) { _dialogs.ShowFailure(attached, _attach.Text); return; }

        await LoadAsync(contract.Id);
    }

    private async Task OpenFileAsync()
    {
        if (SelectedContract is not { FileName: not null } contract) return;

        var file = await _contracts.GetFileAsync(contract.Id);
        if (file.IsFailure) { _dialogs.ShowFailure(file, _openFile.Text); return; }

        var opened = await _output.ViewAsync(file.Value.Content, Path.GetExtension(file.Value.FileName));
        if (opened.IsFailure) _dialogs.ShowFailure(opened, _openFile.Text);
    }

    private async Task RecordTimesAsync()
    {
        if (SelectedContract is not { } contract || SelectedIntervention is not { } intervention) return;

        using var dialog = new InterventionTimesDialog(Localizer, _dialogs, intervention);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var saved = await _contracts.RecordInterventionTimesAsync(intervention.InstrumentId,
                                                                  intervention.MaintenanceRecordId,
                                                                  dialog.RequestedAt, dialog.ArrivedAt);
        if (saved.IsFailure) { _dialogs.ShowFailure(saved, _times.Text); return; }

        await LoadAsync(contract.Id);
        await LoadInterventionsAsync();
    }
}

/// <summary>Creating or editing a contract: its terms, its analysers and its captive reagents.</summary>
internal sealed class InstrumentContractDialog : MasterDataEditor
{
    private readonly Guid? _id;
    private readonly IReadOnlyList<ContractLookup> _instrumentOptions;
    private readonly IReadOnlyList<ContractLookup> _itemOptions;
    private readonly TextBox _reference, _committed, _codes, _maxDelay, _visits, _penaltyAmount,
                             _maintenance, _training, _exit, _penalty, _notes;
    private readonly ComboBox _supplier, _kind, _basis;
    private readonly DateTimePicker _start;
    private readonly NumericUpDown _duration;
    private readonly CheckBox _active;
    private readonly CheckedListBox _instruments = new();
    private readonly DataGridView _reagents = UiTheme.Grid();

    internal SaveInstrumentContractRequest? Request { get; private set; }

    internal InstrumentContractDialog(ILocalizer localizer, IUserDialogs dialogs, ContractOptions options,
                                      InstrumentContractDetail? detail)
        : base(localizer, dialogs, detail is null ? "Contracts.New" : "Action.Edit", detail?.Reference, 940, 760)
    {
        _id = detail?.Id;
        var language = localizer.CurrentLanguage;
        var inactive = localizer["Contracts.InactiveSuffix"];
        string Display(ContractLookup entry) =>
            entry.IsActive
                ? $"{entry.Code} — {entry.Name.For(language)}"
                : $"{entry.Code} — {entry.Name.For(language)} {inactive}";

        _instrumentOptions = options.Instruments;
        _itemOptions = options.StockItems;

        _reference = TextField("Contracts.Reference", 20, 14, 180, detail?.Reference, latin: true);
        _supplier = Lookup("Inventory.Supplier", 220, 14, 300,
                           options.Suppliers.Select(s => (s.Id, Display(s))), detail?.SupplierId,
                           allowNone: false);
        _kind = Choice("Contracts.Kind", 540, 14, 180, detail?.Kind ?? InstrumentContractKind.Placement,
                       "InstrumentContractKind");
        _active = Check("Contracts.Active", 740, 34, detail?.IsActive ?? true);

        Label("Contracts.Start", 20, 68);
        _start = new DateTimePicker
        {
            Bounds = new Rectangle(20, 86, 160, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            Value = (detail?.StartDate ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue)
        };
        Controls.Add(_start);
        _duration = Number("Contracts.DurationMonths", 200, 68, 120, detail?.DurationMonths ?? 60, 240);

        _basis = Choice("Contracts.Basis", 340, 68, 240, detail?.CommitmentBasis ?? CommitmentBasis.TestsPerYear,
                        "CommitmentBasis");
        _committed = TextField("Contracts.Committed", 600, 68, 140,
                               detail?.CommittedVolume is { } v and > 0m ? v.ToString("0.##", CultureInfo.InvariantCulture) : null,
                               latin: true, mono: true, right: true);
        _penaltyAmount = TextField("Contracts.PenaltyAmount", 760, 68, 160,
                                   detail?.PenaltyAmountMad?.ToString("0.00", CultureInfo.InvariantCulture),
                                   latin: true, mono: true, right: true);

        _codes = TextField("Contracts.CountedAnalyses", 20, 122, 560, detail?.CountedAnalysisCodesCsv, latin: true, mono: true);
        _visits = TextField("Contracts.PreventiveVisits", 600, 122, 140,
                            detail?.PreventiveVisitsPerYear?.ToString(CultureInfo.InvariantCulture), latin: true, mono: true, right: true);
        _maxDelay = TextField("Contracts.MaxDelayHours", 760, 122, 160,
                              detail?.MaxInterventionDelayHours?.ToString(CultureInfo.InvariantCulture), latin: true, mono: true, right: true);

        Label("Contracts.Instruments", 20, 176);
        _instruments.Bounds = new Rectangle(20, 196, 300, 220);
        _instruments.CheckOnClick = true;
        foreach (var instrument in options.Instruments)
        {
            var index = _instruments.Items.Add(Display(instrument));
            if (detail?.InstrumentIds.Contains(instrument.Id) == true) _instruments.SetItemChecked(index, true);
        }
        Controls.Add(_instruments);

        Label("Contracts.CaptiveReagents", 340, 176);
        _reagents.Dock = DockStyle.None;
        _reagents.Bounds = new Rectangle(340, 196, 580, 220);
        _reagents.ReadOnly = false;
        _reagents.AllowUserToAddRows = true;
        _reagents.AllowUserToDeleteRows = true;
        var itemColumn = new DataGridViewComboBoxColumn
        {
            Name = "item", HeaderText = localizer["Inventory.Item"], FillWeight = 56,
            DisplayMember = nameof(ReagentChoice.Display), ValueMember = nameof(ReagentChoice.Id),
            FlatStyle = FlatStyle.Flat
        };
        itemColumn.Items.AddRange([.. options.StockItems.Select(i => new ReagentChoice(i.Id, Display(i)))]);
        _reagents.Columns.Add(itemColumn);
        _reagents.Columns.Add(new DataGridViewTextBoxColumn { Name = "price", HeaderText = localizer["Contracts.ContractPrice"], FillWeight = 22 });
        _reagents.Columns.Add(new DataGridViewTextBoxColumn { Name = "costPerTest", HeaderText = localizer["Contracts.CostPerTest"], FillWeight = 22 });
        foreach (var reagent in detail?.Reagents ?? [])
        {
            if (options.StockItems.All(i => i.Id != reagent.StockItemId)) continue;
            _reagents.Rows.Add(reagent.StockItemId,
                               reagent.ContractPriceMad?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty,
                               reagent.CostPerTestMad?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty);
        }
        _reagents.DataError += (_, e) => e.ThrowException = false;
        Controls.Add(_reagents);

        _maintenance = TextField("Contracts.MaintenanceTerms", 20, 426, 440, detail?.MaintenanceTerms);
        _training = TextField("Contracts.TrainingTerms", 480, 426, 440, detail?.TrainingTerms);
        _exit = TextField("Contracts.ExitConditions", 20, 480, 440, detail?.ExitConditions);
        _penalty = TextField("Contracts.PenaltyTerms", 480, 480, 440, detail?.PenaltyTerms);
        _notes = TextField("Field.Notes", 20, 534, 900, detail?.Notes);

        Notice(20, 590, 900, 60, "Contracts.EditorNotice");

        Buttons(700, 940);

        StockDialog.Frame(this, localizer, "StocksUi.Dialog.InstrumentContract");
    }

    private sealed record ReagentChoice(Guid Id, string Display);

    protected override void Accept()
    {
        _reagents.EndEdit();

        if (string.IsNullOrWhiteSpace(_reference.Text))
        {
            Dialogs.Warn(Localizer["Contract_ReferenceRequired"], Text);
            _reference.Focus();
            return;
        }

        if (SelectedId(_supplier) is not { } supplierId)
        {
            Dialogs.Warn(Localizer["Contract_SupplierRequired"], Text);
            return;
        }

        var basis = Selected<CommitmentBasis>(_basis);
        var committed = DecimalOrNull(_committed) ?? 0m;
        if (basis != CommitmentBasis.None && committed <= 0m)
        {
            Dialogs.Warn(Localizer["Contract_CommitmentRequired"], Text);
            _committed.Focus();
            return;
        }

        if (!TryInt(_visits, out var visits) || !TryInt(_maxDelay, out var maxDelay))
        {
            Dialogs.Warn(Localizer["Contract_NegativeTerm"], Text);
            return;
        }

        var reagents = new List<InstrumentContractReagentInput>();
        foreach (DataGridViewRow row in _reagents.Rows)
        {
            if (row.IsNewRow || row.Cells["item"].Value is not Guid itemId) continue;

            var price = EditorValues.ParseDecimal(row.Cells["price"].Value?.ToString());
            var cost = EditorValues.ParseDecimal(row.Cells["costPerTest"].Value?.ToString());
            if (price < 0m || cost < 0m)
            {
                Dialogs.Warn(Localizer["Po_PriceMustNotBeNegative"], Text);
                return;
            }

            reagents.Add(new InstrumentContractReagentInput(itemId, price, cost));
        }

        Request = new SaveInstrumentContractRequest
        {
            Id = _id,
            Reference = _reference.Text.Trim(),
            SupplierId = supplierId,
            Kind = Selected<InstrumentContractKind>(_kind),
            StartDate = DateOnly.FromDateTime(_start.Value.Date),
            DurationMonths = (int)_duration.Value,
            CommitmentBasis = basis,
            CommittedVolume = committed,
            CountedAnalysisCodesCsv = Blank(_codes),
            PreventiveVisitsPerYear = visits,
            MaxInterventionDelayHours = maxDelay,
            MaintenanceTerms = Blank(_maintenance),
            TrainingTerms = Blank(_training),
            ExitConditions = Blank(_exit),
            PenaltyTerms = Blank(_penalty),
            PenaltyAmountMad = DecimalOrNull(_penaltyAmount),
            Notes = Blank(_notes),
            IsActive = _active.Checked,
            InstrumentIds = [.. _instruments.CheckedIndices.Cast<int>().Select(i => _instrumentOptions[i].Id)],
            Reagents = reagents
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    private static bool TryInt(TextBox box, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(box.Text)) return true;
        if (!int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
        value = parsed;
        return true;
    }
}

/// <summary>When the supplier was called and when its technician arrived.</summary>
internal sealed class InterventionTimesDialog : MasterDataEditor
{
    private readonly CheckBox _hasRequested, _hasArrived;
    private readonly DateTimePicker _requested, _arrived;

    internal DateTimeOffset? RequestedAt { get; private set; }
    internal DateTimeOffset? ArrivedAt { get; private set; }

    internal InterventionTimesDialog(ILocalizer localizer, IUserDialogs dialogs, ContractInterventionRow row)
        : base(localizer, dialogs, "Contracts.InterventionTimes", $"{row.InstrumentCode} · {row.PerformedOn:dd/MM/yyyy}", 480, 200)
    {
        (_hasRequested, _requested) = Moment("Contracts.RequestedAt", 20, row.RequestedAt,
                                             row.PerformedOn.ToDateTime(new TimeOnly(8, 0)));
        (_hasArrived, _arrived) = Moment("Contracts.ArrivedAt", 250, row.ArrivedAt,
                                         row.PerformedOn.ToDateTime(new TimeOnly(10, 0)));

        Notice(20, 76, 440, 40, "Contracts.InterventionTimesNotice");
        Buttons(140, 480);

        StockDialog.Frame(this, localizer, "StocksUi.Dialog.InterventionTimes");
    }

    private (CheckBox, DateTimePicker) Moment(string key, int x, DateTimeOffset? value, DateTime fallback)
    {
        var toggle = Check(key, x, 14, value is not null);
        var picker = new DateTimePicker
        {
            Bounds = new Rectangle(x, 36, 210, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "dd/MM/yyyy HH:mm",
            MaxDate = DateTime.Now,
            Value = value?.LocalDateTime ?? (fallback > DateTime.Now ? DateTime.Now : fallback),
            Enabled = value is not null
        };
        toggle.CheckedChanged += (_, _) => picker.Enabled = toggle.Checked;
        Controls.Add(picker);
        return (toggle, picker);
    }

    protected override void Accept()
    {
        RequestedAt = _hasRequested.Checked ? new DateTimeOffset(_requested.Value) : null;
        ArrivedAt = _hasArrived.Checked ? new DateTimeOffset(_arrived.Value) : null;

        if (RequestedAt is { } requested && ArrivedAt is { } arrived && arrived < requested)
        {
            Dialogs.Warn(Localizer["Contract_ArrivalBeforeRequest"], Text);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
