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
        InstrumentContractScreenRules.AlertTone(row) is { } tone ? StockPill.Of(tone) : null;

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
        _rule.Text = InstrumentContractScreenRules.AlertRule(T);

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

    /// <summary>Tests counted whole, an amount bought with two decimals (InstrumentContractScreenRules.Figure).</summary>
    private static string Number(decimal value, CommitmentStatus commitment) =>
        InstrumentContractScreenRules.Figure(value, commitment, v => v.ToString("N2", CultureInfo.CurrentCulture));

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
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading instrument contracts failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)));
        }
    }

    private void Render(Guid? keep = null)
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var row in _rows)
        {
            var c = row.Commitment;

            var index = _grid.Rows.Add(
                row.Reference,
                row.SupplierName,
                T($"InstrumentContractKind.{row.Kind}"),
                row.Instruments,
                InstrumentContractScreenRules.End(row, T),
                T($"CommitmentBasis.{c.Basis}"),
                InstrumentContractScreenRules.Period(c),
                Number(c.Committed, c),
                Number(c.ExpectedToDate, c),
                Number(c.Consumed, c),
                Number(c.Projected, c),
                InstrumentContractScreenRules.Ratio(c, CultureInfo.CurrentCulture),
                InstrumentContractScreenRules.Alerts(row, T),
                InstrumentContractScreenRules.Maintenance(row, T, CultureInfo.CurrentCulture));

            _grid.Rows[index].Tag = row;
            if (row.Id == keep) _grid.CurrentCell = _grid.Rows[index].Cells[0];
        }

        _grid.ResumeLayout(performLayout: true);
        StockGrid.Filter(_grid, _search.Query);
        RenderHeading();

        if (keep is null && _grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells[0];

        // The grid makes its first row current as it is added, before the row carries its
        // contract: that selection loaded nothing, and setting the same row current again raises
        // no event. The interventions of the contract now shown are loaded here, once the rows are filled.
        _ = _selectionLoads.RunAsync(() => LoadInterventionsAsync());

        RefreshButtons();
    }

    private void RenderHeading() => _count.Text = InstrumentContractScreenRules.Count(_rows, T);

    private void OnContractFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not InstrumentContractRow row) return;

        var column = _grid.Columns[e.ColumnIndex].Name;
        switch (InstrumentContractScreenRules.RowInk(row))
        {
            case StockInk.Critical:
                e.CellStyle!.BackColor = UiTheme.CriticalBackground;
                if (column is "alert" or "ratio" && InstrumentContractScreenRules.RatioInk(row) == StockInk.CriticalBold)
                    e.CellStyle.ForeColor = UiTheme.Critical;
                return;

            case StockInk.Muted:
                e.CellStyle!.ForeColor = UiTheme.TextMuted;
                return;
        }

        // A reminder, not an emergency: the alert pill and the end date carry it, not the row.
        if (column == "end" && InstrumentContractScreenRules.EndInk(row) == StockInk.AbnormalBold)
        {
            e.CellStyle!.ForeColor = UiTheme.Abnormal;
            e.CellStyle.Font = UiTheme.MonospaceBold;
        }
    }

    private void OnInterventionFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _interventions.Rows[e.RowIndex].Tag is not ContractInterventionRow row) return;

        // The delay beyond what the contract allows is marked on its own cell, not on the row.
        if (InstrumentContractScreenRules.DelayInk(row) == StockInk.CriticalBold && _interventions.Columns[e.ColumnIndex].Name == "delay")
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
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
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
            var index = _interventions.Rows.Add([.. InstrumentContractScreenRules.InterventionCells(row, T, CultureInfo.CurrentCulture)]);

            _interventions.Rows[index].Tag = row;
            if (row.MaintenanceRecordId == keep) _interventions.CurrentCell = _interventions.Rows[index].Cells[0];
        }

        _interventions.ResumeLayout(performLayout: true);

        var contract = SelectedContract;
        _interventionsCard.Note.Text = InstrumentContractScreenRules.InterventionsNote(contract, T);

        RefreshButtons();
    }

    private void RefreshButtons()
    {
        // Nothing that writes is offered while a write is in flight (see RunActionAsync).
        var idle = !IsBusy;
        var actions = InstrumentContractScreenRules.Actions(p => PermissionGate.Allows(p), SelectedContract, SelectedIntervention);

        _new.Enabled = idle && actions.New;
        _edit.Enabled = idle && actions.Edit;
        _attach.Enabled = idle && actions.Attach;
        _openFile.Enabled = idle && actions.OpenFile;
        _times.Enabled = idle && actions.Times;
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
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)));
        }
    }

    private async Task AttachAsync()
    {
        if (SelectedContract is not { } contract) return;

        if (InstrumentContractScreenRules.ReplaceQuestion(contract, T) is { } question
            && !_dialogs.Confirm(question, _attach.Text))
            return;

        using var picker = new OpenFileDialog
        {
            Title = UserDialogs.CaptionOf(_attach.Text, _attach.Text),
            Filter = FileFilter,
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        byte[] content;
        try
        {
            var info = new FileInfo(picker.FileName);
            if (InstrumentContractScreenRules.CheckFile(info.Name, info.Length) is { } refused)
            {
                _dialogs.Warn(T(refused.Key, refused.Typed ?? string.Empty), _attach.Text);
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

    /// <summary>The kinds of file a contract is kept as (InstrumentContractScreenRules.FileExtensions).</summary>
    private static string FileFilter
    {
        get
        {
            var pattern = string.Join(";", InstrumentContractScreenRules.FileExtensions.Select(e => "*" + e));
            return $"PDF, images, Office ({pattern})|{pattern}";
        }
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
        string Display(ContractLookup entry) => InstrumentContractScreenRules.Display(entry, language, localizer.Format);
        // What the boxes show, new or edited: InstrumentContractScreenRules.Form, as the web's form.
        var form = InstrumentContractScreenRules.Form(detail, DateOnly.FromDateTime(DateTime.Today), options);

        _instrumentOptions = options.Instruments;
        _itemOptions = options.StockItems;

        _reference = TextField("Contracts.Reference", 20, 14, 180, form.Reference, latin: true);
        _supplier = Lookup("Inventory.Supplier", 220, 14, 280,
                           options.Suppliers.Select(s => (s.Id, Display(s))), detail?.SupplierId,
                           allowNone: false);
        _kind = Choice("Contracts.Kind", 520, 14, 180, form.Kind, "InstrumentContractKind");
        // 220 pixels to the edge: in Arabic the caption ran past the window's edge from 740.
        _active = Check("Contracts.Active", 720, 34, form.IsActive);

        Label("Contracts.Start", 20, 68);
        _start = new DateTimePicker
        {
            Bounds = new Rectangle(20, 86, 160, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            Value = form.Start.ToDateTime(TimeOnly.MinValue)
        };
        Controls.Add(_start);
        _duration = Number("Contracts.DurationMonths", 200, 68, 120, form.DurationMonths, InstrumentContractScreenRules.DurationMax);

        _basis = Choice("Contracts.Basis", 340, 68, 240, form.Basis, "CommitmentBasis");
        _committed = TextField("Contracts.Committed", 600, 68, 140, form.Committed,
                               latin: true, mono: true, right: true);
        _penaltyAmount = TextField("Contracts.PenaltyAmount", 760, 68, 160, form.PenaltyAmount,
                                   latin: true, mono: true, right: true);

        // The two short boxes are wider than their figures need: their captions, in Arabic, ran
        // into each other over 140 pixels.
        _codes = TextField("Contracts.CountedAnalyses", 20, 122, 440, form.CountedAnalyses, latin: true, mono: true);
        _visits = TextField("Contracts.PreventiveVisits", 480, 122, 200, form.PreventiveVisits, latin: true, mono: true, right: true);
        _maxDelay = TextField("Contracts.MaxDelayHours", 700, 122, 220, form.MaxDelayHours, latin: true, mono: true, right: true);

        Label("Contracts.Instruments", 20, 176);
        _instruments.Bounds = new Rectangle(20, 196, 300, 220);
        _instruments.CheckOnClick = true;
        foreach (var instrument in options.Instruments)
        {
            var index = _instruments.Items.Add(Display(instrument));
            if (form.InstrumentIds.Contains(instrument.Id)) _instruments.SetItemChecked(index, true);
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
        foreach (var reagent in form.Reagents)
            _reagents.Rows.Add(reagent.StockItemId.GetValueOrDefault(), reagent.Price ?? string.Empty, reagent.CostPerTest ?? string.Empty);
        _reagents.DataError += (_, e) => e.ThrowException = false;
        Controls.Add(_reagents);

        _maintenance = TextField("Contracts.MaintenanceTerms", 20, 426, 440, form.MaintenanceTerms);
        _training = TextField("Contracts.TrainingTerms", 480, 426, 440, form.TrainingTerms);
        _exit = TextField("Contracts.ExitConditions", 20, 480, 440, form.ExitConditions);
        _penalty = TextField("Contracts.PenaltyTerms", 480, 480, 440, form.PenaltyTerms);
        _notes = TextField("Field.Notes", 20, 534, 900, form.Notes);
        // Bounded to the columns: beyond, the save failed with « Erreur inattendue ».
        _reference.MaxLength = InstrumentContractScreenRules.ReferenceMaxLength;
        _codes.MaxLength = InstrumentContractScreenRules.CodesMaxLength;
        _maintenance.MaxLength = _exit.MaxLength = _penalty.MaxLength = InstrumentContractScreenRules.LongTermsMaxLength;
        _training.MaxLength = _notes.MaxLength = InstrumentContractScreenRules.ShortTermsMaxLength;
        // A penalty typed with its currency (« 5000 DH ») was saved as no penalty.
        Numeric(EditorValues.AmountMaxLength, _committed, _penaltyAmount);
        Numeric(InstrumentContractScreenRules.IntegerMaxLength, _visits, _maxDelay);

        Notice(20, 590, 900, 60, "Contracts.EditorNotice");

        Buttons(700, 940);

        StockDialog.Frame(this, localizer, "StocksUi.Dialog.InstrumentContract");
    }

    private sealed record ReagentChoice(Guid Id, string Display);

    protected override void Accept()
    {
        _reagents.EndEdit();

        var form = new InstrumentContractForm
        {
            Reference = _reference.Text,
            SupplierId = SelectedId(_supplier),
            Kind = Selected<InstrumentContractKind>(_kind),
            IsActive = _active.Checked,
            Start = DateOnly.FromDateTime(_start.Value.Date),
            DurationMonths = (int)_duration.Value,
            Basis = Selected<CommitmentBasis>(_basis),
            Committed = _committed.Text,
            PenaltyAmount = _penaltyAmount.Text,
            CountedAnalyses = _codes.Text,
            PreventiveVisits = _visits.Text,
            MaxDelayHours = _maxDelay.Text,
            InstrumentIds = [.. _instruments.CheckedIndices.Cast<int>().Select(i => _instrumentOptions[i].Id)],
            MaintenanceTerms = _maintenance.Text,
            TrainingTerms = _training.Text,
            ExitConditions = _exit.Text,
            PenaltyTerms = _penalty.Text,
            Notes = _notes.Text
        };

        var lines = new List<DataGridViewRow>();
        foreach (DataGridViewRow row in _reagents.Rows)
        {
            if (row.IsNewRow || row.Cells["item"].Value is not Guid itemId) continue;
            lines.Add(row);
            form.Reagents.Add(new ContractReagentForm
            {
                StockItemId = itemId,
                Price = row.Cells["price"].Value?.ToString(),
                CostPerTest = row.Cells["costPerTest"].Value?.ToString()
            });
        }

        // A reference, a supplier, the commitment unless « Sans engagement », whole positive terms and
        // readable reagent prices (InstrumentContractScreenRules.Check, as the web's form).
        var (refusal, request) = InstrumentContractScreenRules.Check(form, _id);
        if (refusal is not null)
        {
            Dialogs.Warn(Localizer.Format(refusal.Key, refusal.Typed ?? string.Empty), Text);
            switch (refusal.Field)
            {
                case InstrumentContractScreenRules.ReferenceField: _reference.Focus(); break;
                case InstrumentContractScreenRules.CommittedField: _committed.Focus(); break;
                case InstrumentContractScreenRules.PenaltyAmountField: _penaltyAmount.Focus(); break;
                case InstrumentContractScreenRules.VisitsField: _visits.Focus(); break;
                case InstrumentContractScreenRules.MaxDelayField: _maxDelay.Focus(); break;
                default:
                    for (var i = 0; i < lines.Count; i++)
                    {
                        if (refusal.Field == InstrumentContractScreenRules.ReagentField("price", i)) _reagents.CurrentCell = lines[i].Cells["price"];
                        else if (refusal.Field == InstrumentContractScreenRules.ReagentField("cost", i)) _reagents.CurrentCell = lines[i].Cells["costPerTest"];
                    }
                    break;
            }

            return;
        }

        Request = request;
        DialogResult = DialogResult.OK;
        Close();
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
        // One reading of the clock: read twice, the second was later than the limit set from the
        // first, and an intervention entered the same morning (before 08:00 or 10:00) opened on an
        // exception instead of the dialog.
        var now = DateTime.Now;
        var form = InstrumentContractScreenRules.TimesForm(row, now);
        (_hasRequested, _requested) = Moment("Contracts.RequestedAt", 20, form.HasRequested, form.Requested, now);
        (_hasArrived, _arrived) = Moment("Contracts.ArrivedAt", 250, form.HasArrived, form.Arrived, now);

        Notice(20, 76, 440, 40, "Contracts.InterventionTimesNotice");
        Buttons(140, 480);

        StockDialog.Frame(this, localizer, "StocksUi.Dialog.InterventionTimes");
    }

    private (CheckBox, DateTimePicker) Moment(string key, int x, bool recorded, DateTime shown, DateTime now)
    {
        var toggle = Check(key, x, 14, recorded);
        var picker = new DateTimePicker
        {
            Bounds = new Rectangle(x, 36, 210, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "dd/MM/yyyy HH:mm",
            MaxDate = now,
            Value = shown > now ? now : shown,
            Enabled = recorded
        };
        toggle.CheckedChanged += (_, _) => picker.Enabled = toggle.Checked;
        Controls.Add(picker);
        return (toggle, picker);
    }

    protected override void Accept()
    {
        // The arrival cannot precede the call (InstrumentContractScreenRules.Check, as the web's form).
        var (refusal, requested, arrived) = InstrumentContractScreenRules.Check(new InterventionTimesForm
        {
            HasRequested = _hasRequested.Checked,
            Requested = _requested.Value,
            HasArrived = _hasArrived.Checked,
            Arrived = _arrived.Value
        }, DateTimeOffset.Now.AddMinutes(1));

        if (refusal is not null)
        {
            Dialogs.Warn(Localizer[refusal.Key], Text);
            return;
        }

        RequestedAt = requested;
        ArrivedAt = arrived;

        DialogResult = DialogResult.OK;
        Close();
    }
}
