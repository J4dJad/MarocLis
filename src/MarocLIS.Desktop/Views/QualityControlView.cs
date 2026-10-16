using MarocLIS.Application.Quality;
using MarocLIS.Desktop.Controls;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Internal quality control: entry, the Levey-Jennings chart, and review of rejections.
/// <para>
/// The three are on one screen because they are one task. A technician runs the controls, looks at
/// where the points landed, and — if one is out — records what they did about it. Splitting that
/// across three screens is how the review step gets skipped, and an unreviewed rejection is an
/// ISO 15189 §5.6 finding waiting to happen.
/// </para>
/// <para>
/// Values are submitted as a <b>run</b>, all levels together, because the multi-level Westgard
/// rules (2₂ₛ within run, R₄ₛ) compare the levels against each other. Saving one level at a time
/// would leave those two rules unevaluable.
/// </para>
/// </summary>
public sealed class QualityControlView : LocalizedView
{
    private readonly SelectionLoads _selectionLoads = new();
    private const string ColParameter = "parameter";
    private const string ColLevel = "level";
    private const string ColLot = "lot";
    private const string ColInstrument = "instrument";
    private const string ColValue = "value";
    private const string ColTarget = "target";
    private const string ColLast = "last";
    private const string ColRules = "rules";

    private readonly QualityControlService _qc;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<QualityControlView>? _logger;

    private readonly DataGridView _entry = UiTheme.Grid();
    private readonly LeveyJenningsChart _chart = new();
    private readonly DataGridView _review = UiTheme.Grid();

    private readonly Label _entryHeading = new();
    private readonly Label _chartHeading = new();
    private readonly Label _reviewHeading = new();
    private readonly Button _submit = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    // Secondary: the run's Save is the screen's one primary action.
    private readonly Button _reviewAction = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _establishLimits = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _editRules = UiTheme.SecondaryButton(string.Empty);
    // The control's limits and rules are set once in a while: they wait behind « Plus d'actions »
    // in the chart's heading, the two buttons kept (hidden) for their rights and their state.
    private readonly Button _chartMore = UiTheme.SecondaryButton(string.Empty);
    private readonly ContextMenuStrip _chartMenu = new();
    private readonly Label _status = new();

    private readonly Label _title = new();
    private readonly Label _count = new();
    private readonly Label _lotCaption = new();
    private readonly Label _rulesLine = new();
    private readonly PillLabel _blocked = new();

    private IReadOnlyList<QcEntryRow> _rows = [];
    private IReadOnlyList<QcReviewRow> _pendingReview = [];

    // The reagent lot the run is recorded against: a new lot under evaluation, or (by default) the
    // lot in use. The QC on a lot is what its release is gated on.
    private readonly ComboBox _reagentLot = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat
    };
    private readonly MarocLIS.Application.Inventory.LotQualityService? _lotQuality;

    // The analyser the named lot is loaded on. The lot applies to that analyser's rows only: one
    // lot for every row turned another analyser's routine QC into the new lot's acceptance run.
    // The same analyser is where an evaluation run is started for the lot's transmitted controls.
    private readonly ComboBox _lotAnalyser = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat
    };
    private readonly Button _startEvaluation = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _stopEvaluation = UiTheme.SecondaryButton(string.Empty);

    public QualityControlView(QualityControlService qc, IUserDialogs dialogs,
                              ILocalizer localizer, ICultureManager cultureManager,
                              ILogger<QualityControlView>? logger = null,
                              MarocLIS.Application.Inventory.LotQualityService? lotQuality = null)
        : base(localizer, cultureManager)
    {
        _lotQuality = lotQuality;
        _qc = qc;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.QualityControl");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = QhPage.PagePadding;

        BuildEntryColumns();
        BuildReviewColumns();

        // --- title, the run's actions, the lot under evaluation --------------------------------------

        var header = QhPage.Header(_title, _count, _refresh, _submit);

        var toolbar = QhPage.Toolbar(QhPage.Caption(_lotCaption),
                                     QhPage.Filter(_reagentLot, 300),
                                     QhPage.Filter(_lotAnalyser, 200),
                                     QhPage.Fit(_startEvaluation),
                                     QhPage.Fit(_stopEvaluation));

        // --- top: entry plus chart, side by side ----------------------------------------------------

        var outerSplit = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 330,
            SplitterWidth = 14,
            BackColor = UiTheme.Surface
        };

        var topSplit = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 640,
            SplitterWidth = 14,
            BackColor = UiTheme.Surface
        };

        var entryCard = QhPage.TitledCard(_entryHeading, null, _entry);
        entryCard.Dock = DockStyle.Fill;

        // The chart's card: the points (the chart writes the control, its lot and limits above
        // them), then the rules in force and the two actions on the control's limits and rules —
        // the E2 board's rules panel, reduced to what the laboratory has on file.
        var chartBody = new Panel { BackColor = UiTheme.SurfaceRaised, Margin = Padding.Empty };

        _chart.Dock = DockStyle.Fill;
        // The chart asks 200 px; at 1366 × 768 the card leaves it a little less under the rules
        // line, and a docked control larger than its room covers the line.
        _chart.MinimumSize = new Size(280, 140);

        var chartBar = new QhSelectionBar(_rulesLine);

        chartBody.Controls.Add(_chart);
        chartBody.Controls.Add(chartBar);
        chartBody.Controls.Add(QhMenu.Holder(_establishLimits, _editRules));

        _chartMenu.Items.Add(QhMenu.For(_establishLimits, async (_, _) => await RunBusyAsync(EstablishLimitsAsync)));
        _chartMenu.Items.Add(QhMenu.For(_editRules, async (_, _) => await RunBusyAsync(EditRulesAsync)));
        _chartMenu.Opening += (_, e) => e.Cancel = !_establishLimits.Enabled && !_editRules.Enabled;
        _chartMore.Margin = new Padding(8, 6, 0, 0);

        var chartCard = QhPage.TitledCard(_chartHeading, null, chartBody, QhPage.MoreButton(_chartMore, _chartMenu));
        chartCard.Dock = DockStyle.Fill;

        topSplit.Panel1.Controls.Add(entryCard);
        topSplit.Panel2.Controls.Add(chartCard);

        // --- bottom: rejections awaiting review -------------------------------------------------------

        var reviewBody = new Panel { BackColor = UiTheme.SurfaceRaised, Margin = Padding.Empty };
        var statusStrip = new QhSelectionBar(_status);
        reviewBody.Controls.Add(_review);
        reviewBody.Controls.Add(statusStrip);
        _review.Dock = DockStyle.Fill;

        var reviewCard = QhPage.TitledCard(_reviewHeading, null, reviewBody, _blocked, _reviewAction);
        reviewCard.Dock = DockStyle.Fill;

        outerSplit.Panel1.Controls.Add(topSplit);
        outerSplit.Panel2.Controls.Add(reviewCard);

        _blocked.Visible = false;
        _blocked.Margin = new Padding(8, 8, 0, 0);

        Controls.Add(QhPage.Frame(2, header, toolbar, outerSplit));
    }

    private void BuildEntryColumns()
    {
        _entry.Columns.Clear();
        _entry.ReadOnly = false;
        _entry.EditMode = DataGridViewEditMode.EditOnEnter;

        void Add(string name, int weight, Font? font = null, bool readOnly = true,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
            => _entry.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = readOnly,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body, Alignment = align
                }
            });

        Add(ColParameter, 22);
        Add(ColLevel, 10, UiTheme.Small);
        Add(ColLot, 14, UiTheme.Monospace);
        Add(ColInstrument, 11, UiTheme.Small);
        Add(ColValue, 12, UiTheme.MonospaceBold, readOnly: false,
            DataGridViewContentAlignment.MiddleRight);
        Add(ColTarget, 15, UiTheme.Monospace, align: DataGridViewContentAlignment.MiddleRight);
        Add(ColLast, 8, UiTheme.Monospace, align: DataGridViewContentAlignment.MiddleRight);
        Add(ColRules, 8, UiTheme.Small);
    }

    private void BuildReviewColumns()
    {
        _review.Columns.Clear();

        void Add(string name, int weight, Font? font = null,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
            => _review.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body, Alignment = align
                }
            });

        Add("measured", 12, UiTheme.Monospace);
        Add("parameter", 18);
        Add("instrument", 11, UiTheme.Small);
        Add("lot", 12, UiTheme.Monospace);
        Add("level", 8, UiTheme.Small);
        Add("value", 9, UiTheme.MonospaceBold, DataGridViewContentAlignment.MiddleRight);
        Add("z", 7, UiTheme.MonospaceBold, DataGridViewContentAlignment.MiddleRight);
        Add("rules", 14, UiTheme.SmallBold);
        Add("affected", 9, UiTheme.MonospaceBold, DataGridViewContentAlignment.MiddleRight);
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();
        _submit.Click += async (_, _) => await RunBusyAsync(SubmitAsync);
        _reviewAction.Click += async (_, _) => await RunBusyAsync(ReviewSelectedAsync);
        _establishLimits.Click += async (_, _) => await RunBusyAsync(EstablishLimitsAsync);
        _editRules.Click += async (_, _) => await RunBusyAsync(EditRulesAsync);
        _startEvaluation.Click += async (_, _) => await RunBusyAsync(() => ChangeEvaluationAsync(start: true));
        _stopEvaluation.Click += async (_, _) => await RunBusyAsync(() => ChangeEvaluationAsync(start: false));
        _reagentLot.SelectedIndexChanged += (_, _) => UpdateLotControls();
        _lotAnalyser.SelectedIndexChanged += (_, _) => UpdateLotControls();

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Quality.EnterQc, _submit, _startEvaluation, _stopEvaluation);
        PermissionGate.Require(Permissions.Quality.ReviewQc, _reviewAction);
        PermissionGate.Require(Permissions.Quality.ManageQcRules, _establishLimits, _editRules);

        _entry.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadChartAsync());
        _review.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadChartForReviewAsync());

        _entry.CellFormatting += OnEntryFormatting;
        _review.CellFormatting += OnReviewFormatting;

        // Enter commits and steps down the column, the same convention as result entry.
        _entry.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;

            e.SuppressKeyPress = true;
            _entry.EndEdit();

            var next = (_entry.CurrentCell?.RowIndex ?? -1) + 1;

            if (next < _entry.Rows.Count)
                _entry.CurrentCell = _entry.Rows[next].Cells[ColValue];
            else
                _submit.Focus();
        };

        _chart.PointSelected += (_, point) =>
        {
            _status.ForeColor = point.Outcome == QcOutcome.Rejected
                ? UiTheme.Critical
                : UiTheme.TextSecondary;

            _status.Text = $"{point.MeasuredAt:dd/MM/yyyy HH:mm} · {point.Value:N3} · " +
                           $"Z {point.ZScore:N2}" +
                           (point.ViolatedRules == WestgardRule.None
                               ? string.Empty
                               : $" · {WestgardRuleNames.Describe(point.ViolatedRules)}");
        };
    }

    private void OnEntryFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _entry.Rows.Count) return;
        if (_entry.Rows[e.RowIndex].Tag is not QcEntryRow row) return;

        // An unusable control lot is flagged on the row, not hidden in a tooltip: running an
        // expired control produces a verdict nobody can rely on.
        if (!row.MaterialUsable)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.UsingAssignedStatistics)
        {
            // Manufacturer's limits rather than the laboratory's own — worth seeing, because
            // ISO 15189 expects the laboratory to establish its own.
            if (_entry.Columns[e.ColumnIndex].Name == ColTarget)
                e.CellStyle!.ForeColor = UiTheme.Abnormal;
        }

        if (_entry.Columns[e.ColumnIndex].Name == ColLast && row.LastOutcome is { } outcome)
        {
            e.CellStyle!.ForeColor = outcome switch
            {
                QcOutcome.Rejected => UiTheme.Critical,
                QcOutcome.Warning => UiTheme.Abnormal,
                _ => UiTheme.TextSecondary
            };
        }
    }

    private void OnReviewFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _review.Rows.Count) return;
        if (_review.Rows[e.RowIndex].Tag is not QcReviewRow row) return;

        e.CellStyle!.BackColor = UiTheme.CriticalBackground;
        e.CellStyle.ForeColor = UiTheme.Critical;

        // A rejection that has already blocked patient results is the more urgent file.
        if (row.AffectedResultCount > 0
            && _review.Columns[e.ColumnIndex].Name == "affected")
            e.CellStyle.Font = new Font(e.CellStyle.Font ?? UiTheme.Body, FontStyle.Bold);
    }

    protected override void ApplyTexts()
    {
        _title.Text = T("Nav.QualityControl");
        _lotCaption.Text = T("QualityHr.Qc.ReagentLot");
        RenderCount();
        _entryHeading.Text = T("Quality.QualityControlEntry");
        _chartHeading.Text = T("Quality.LeveyJennings");
        _reviewHeading.Text = T("Quality.PendingReview");
        _submit.Text = T("Action.Save");
        _refresh.Text = T("Action.Refresh");
        _reviewAction.Text = T("Quality.Review");
        _establishLimits.Text = T("Quality.EstablishLimits");
        _editRules.Text = T("Quality.EditRules");
        _chartMore.Text = T("ResultEntry.MoreActions");
        _startEvaluation.Text = T("Qc.StartEvaluation");
        _stopEvaluation.Text = T("Qc.StopEvaluation");
        if (_lotAnalyser.Items.Count > 0 && _lotAnalyser.Items[0] is AnalyserChoice { Id: null })
            _lotAnalyser.Items[0] = new AnalyserChoice(null, T("Qc.LotAnalyser"));

        if (_entry.Columns.Count > 0)
        {
            _entry.Column(ColParameter).HeaderText = T("Result.Parameter");
            _entry.Column(ColLevel).HeaderText = T("Quality.QcLevel");
            _entry.Column(ColLot).HeaderText = T("Quality.QcLot");
            _entry.Column(ColInstrument).HeaderText = T("Result.Instrument");
            _entry.Column(ColValue).HeaderText = T("Result.Value");
            _entry.Column(ColTarget).HeaderText = T("Quality.Target");
            _entry.Column(ColLast).HeaderText = T("Result.Previous");
            _entry.Column(ColRules).HeaderText = T("Quality.Rules");
        }

        if (_review.Columns.Count > 0)
        {
            _review.Column("measured").HeaderText = T("Result.MeasuredAt");
            _review.Column("parameter").HeaderText = T("Result.Parameter");
            _review.Column("instrument").HeaderText = T("Result.Instrument");
            _review.Column("lot").HeaderText = T("Quality.QcLot");
            _review.Column("level").HeaderText = T("Quality.QcLevel");
            _review.Column("value").HeaderText = T("Result.Value");
            _review.Column("z").HeaderText = T("Quality.ZScore");
            _review.Column("rules").HeaderText = T("QualityHr.Qc.ViolatedRules");
            _review.Column("affected").HeaderText = T("NonConformity.AffectedResults");
        }

        if (_rows.Count > 0)
        {
            _entry.EndEdit();
            RenderEntry(CaptureTyped());
        }
        if (_pendingReview.Count > 0) RenderReview();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    private async Task LoadReagentLotsAsync(CancellationToken ct)
    {
        var keep = (_reagentLot.SelectedItem as LotChoice)?.Id;
        _reagentLot.Items.Clear();
        _reagentLot.Items.Add(new LotChoice(null, T("Qc.ReagentLotInUse")));

        if (_lotQuality is not null && PermissionGate.Allows(Permissions.Quality.EnterQc))
        {
            var lots = await _lotQuality.GetLotsForQcAsync(ct);

            if (lots.IsSuccess)
            {
                foreach (var lot in lots.Value)
                {
                    var label = $"{lot.ItemCode} · {lot.LotNumber}" +
                                (lot.IsQuarantined ? $" ({T("Inventory.Quarantined").ToLowerInvariant()})" : string.Empty);
                    var index = _reagentLot.Items.Add(new LotChoice(lot.ReagentLotId, label));
                    if (lot.ReagentLotId == keep) _reagentLot.SelectedIndex = index;
                }
            }
        }

        if (_reagentLot.SelectedIndex < 0) _reagentLot.SelectedIndex = 0;
    }

    private sealed record LotChoice(Guid? Id, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record AnalyserChoice(Guid? Id, string Display)
    {
        public override string ToString() => Display;
    }

    private Guid? NamedLotId => (_reagentLot.SelectedItem as LotChoice)?.Id;

    private Guid? LotAnalyserId => (_lotAnalyser.SelectedItem as AnalyserChoice)?.Id;

    /// <summary>The analysers of the sheet, for the named lot; one alone is chosen already.</summary>
    private void LoadLotAnalysers()
    {
        var keep = LotAnalyserId;
        var analysers = _rows.GroupBy(r => r.InstrumentId)
                             .Select(g => new AnalyserChoice(g.Key, g.First().InstrumentCode))
                             .OrderBy(a => a.Display, StringComparer.CurrentCulture)
                             .ToList();

        _lotAnalyser.Items.Clear();
        _lotAnalyser.Items.Add(new AnalyserChoice(null, T("Qc.LotAnalyser")));

        foreach (var analyser in analysers)
        {
            var index = _lotAnalyser.Items.Add(analyser);
            if (analyser.Id == keep || analysers.Count == 1) _lotAnalyser.SelectedIndex = index;
        }

        if (_lotAnalyser.SelectedIndex < 0) _lotAnalyser.SelectedIndex = 0;
        UpdateLotControls();
    }

    private void UpdateLotControls()
    {
        var named = NamedLotId is not null;
        _lotAnalyser.Enabled = named;

        var canEvaluate = named && LotAnalyserId is not null && PermissionGate.Allows(Permissions.Quality.EnterQc);
        _startEvaluation.Enabled = canEvaluate;
        _stopEvaluation.Enabled = named && PermissionGate.Allows(Permissions.Quality.EnterQc);
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        await LoadReagentLotsAsync(ct);

        try
        {
            var sheet = await _qc.GetEntrySheetAsync(ct: ct);

            if (sheet.IsFailure)
            {
                _dialogs.ShowFailure(sheet);
                return;
            }

            // A reload (refresh, a review, a rule change) keeps the values typed and not yet sent.
            _entry.EndEdit();
            var typed = CaptureTyped();

            _rows = sheet.Value;
            RenderEntry(typed);
            LoadLotAnalysers();

            var review = await _qc.GetPendingReviewAsync(ct);

            if (review.IsSuccess)
            {
                _pendingReview = review.Value;
                RenderReview();
            }

            await LoadChartAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the quality control screen failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    /// <summary>
    /// Leaving the screen with control values typed and not yet submitted asks first: the sidebar
    /// and the shortcuts dropped them without a word, and a run believed recorded was not.
    /// </summary>
    public override bool ConfirmLeave()
    {
        // While a submission runs the values typed are the ones being saved: the navigation waits
        // for it to end rather than asking whether to drop them.
        if (IsBusy) return true;

        _entry.EndEdit();

        return CaptureTyped().IsEmpty
               || _dialogs.Confirm(T("Message.UnsavedChanges"), T("Nav.QualityControl"));
    }

    /// <summary>
    /// The control values typed and not yet submitted, by control target. The value column starts
    /// empty, so anything in it is pending. Rebuilding the grid — on a language change or any
    /// reload — used to blank it.
    /// </summary>
    private TypedEdits<Guid> CaptureTyped()
    {
        var typed = new TypedEdits<Guid>();

        foreach (DataGridViewRow gridRow in _entry.Rows)
        {
            if (gridRow.Tag is QcEntryRow row)
                typed.Capture(row.QcTargetId, gridRow.Cells[ColValue].Value?.ToString(), null);
        }

        return typed;
    }

    private void RenderEntry(TypedEdits<Guid>? keep = null)
    {
        _entry.SuspendLayout();
        _entry.Rows.Clear();

        foreach (var row in _rows)
        {
            var target = row.TargetMean is { } mean && row.TargetSd is { } sd
                ? $"{mean.ToString("N" + row.Decimals)} ± {sd.ToString("N" + row.Decimals)}"
                : T("Qc_NoTargetEstablished");

            var index = _entry.Rows.Add(
                row.ParameterName.For(Localizer.CurrentLanguage),
                T($"QcLevel.{row.Level}"),
                row.MaterialLotNumber,
                row.InstrumentCode,
                string.Empty,
                target,
                row.LastValue?.ToString("N" + row.Decimals) ?? string.Empty,
                // Abbreviated rule set: the full flags string would not fit and is on hover.
                row.ActiveRules == WestgardRule.None ? "—" : $"{CountRules(row.ActiveRules)}");

            var gridRow = _entry.Rows[index];
            gridRow.Tag = row;

            if (keep is not null && keep.TryGet(row.QcTargetId, out var pending))
                gridRow.Cells[ColValue].Value = pending;

            var tooltip = new List<string> { WestgardRuleNames.Describe(row.ActiveRules, " · ") };

            if (row.CvPercent is { } cv)
            {
                tooltip.Add($"CV {cv:N2} %" +
                            (row.MaxAcceptableCvPercent is { } max ? $" / {max:N2} %" : string.Empty));
            }

            tooltip.Add($"{row.PointCount} {T("Quality.PointCount").ToLowerInvariant()}");

            if (!row.MaterialUsable)
                tooltip.Add(Localizer.Format("Qc_MaterialExpired", row.MaterialLotNumber));

            gridRow.Cells[ColRules].ToolTipText = string.Join(Environment.NewLine, tooltip);
        }

        _entry.ResumeLayout(performLayout: true);

        _entryHeading.Text = $"{T("Quality.QualityControlEntry")} — {_rows.Count}";
        RenderCount();
    }

    private void RenderCount() =>
        _count.Text = Localizer.Format("QualityHr.Qc.Count", _rows.Count, _pendingReview.Count);

    /// <summary>The rules in force for the control shown, under its chart, by their short names.</summary>
    private void ShowControlFacts(Guid qcTargetId)
    {
        var row = _rows.FirstOrDefault(r => r.QcTargetId == qcTargetId);

        if (row is null)
        {
            _rulesLine.Text = string.Empty;
            return;
        }

        var active = WestgardRuleNames.Of(row.ActiveRules);
        _rulesLine.ForeColor = active.Count == 0 ? UiTheme.Critical : UiTheme.TextSecondary;
        _rulesLine.Text = Localizer.Format("QualityHr.Qc.ActiveRules", active.Count == 0 ? "—" : string.Join(" · ", active));
    }

    private static int CountRules(WestgardRule rules) =>
        Enum.GetValues<WestgardRule>().Count(r => r != WestgardRule.None && rules.HasFlag(r));

    private void RenderReview()
    {
        _review.SuspendLayout();
        _review.Rows.Clear();

        foreach (var row in _pendingReview)
        {
            var index = _review.Rows.Add(
                row.MeasuredAt.ToString("dd/MM HH:mm"),
                row.ParameterName.For(Localizer.CurrentLanguage),
                row.InstrumentCode,
                row.MaterialLotNumber,
                T($"QcLevel.{row.Level}"),
                row.Value.ToString("N3"),
                row.ZScore?.ToString("N2") ?? string.Empty,
                WestgardRuleNames.Describe(row.ViolatedRules),
                row.AffectedResultCount > 0 ? row.AffectedResultCount.ToString() : string.Empty);

            _review.Rows[index].Tag = row;
        }

        _review.ResumeLayout(performLayout: true);

        _reviewHeading.Text = $"{T("Quality.PendingReview")} — {_pendingReview.Count}";

        _reviewAction.Enabled = _pendingReview.Count > 0;

        // The patient results these rejections hold back, said where the reviewer looks first.
        var blocked = _pendingReview.Sum(r => r.AffectedResultCount);
        if (blocked > 0) _blocked.Show(Localizer.Format("QualityHr.Qc.Blocked", blocked), QhTone.Crit);
        else _blocked.Visible = false;

        RenderCount();
    }

    private async Task LoadChartAsync()
    {
        if (_entry.CurrentRow?.Tag is not QcEntryRow row) return;
        await ShowChartAsync(row.QcTargetId);
    }

    private async Task LoadChartForReviewAsync()
    {
        if (_review.CurrentRow?.Tag is not QcReviewRow row) return;
        await ShowChartAsync(row.QcTargetId);
    }

    private async Task ShowChartAsync(Guid qcTargetId)
    {
        try
        {
            ShowControlFacts(qcTargetId);

            var chart = await _qc.GetChartAsync(qcTargetId);

            _chart.SetSeries(chart.IsSuccess ? chart.Value : null);

            if (chart.IsSuccess)
            {
                // The chart writes the parameter, the lot and the analyser above its points; the
                // heading keeps to its name so « Plus d'actions » stays in the card.
                _chartHeading.Text = T("Quality.LeveyJennings");

                _establishLimits.Enabled = true;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the Levey-Jennings chart failed.");
            _chart.SetSeries(null);
        }
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    /// <summary>
    /// Starts (or stops) the evaluation run of the named lot on its analyser: the next control of
    /// each level that analyser transmits today is credited to the lot, the rest stays routine QC.
    /// </summary>
    private async Task ChangeEvaluationAsync(bool start)
    {
        if (NamedLotId is not { } lotId) return;

        if (start && LotAnalyserId is null)
        {
            _dialogs.Warn(T("Qc.ChooseLotAnalyser"));
            return;
        }

        var changed = start
            ? await _qc.StartLotEvaluationAsync(lotId, LotAnalyserId!.Value)
            : await _qc.StopLotEvaluationAsync(lotId);

        if (changed.IsFailure)
        {
            _dialogs.ShowFailure(changed);
            return;
        }

        _status.ForeColor = UiTheme.TextSecondary;
        _status.Text = start
            ? Localizer.Format("Qc.EvaluationStarted", _reagentLot.Text, _lotAnalyser.Text)
            : Localizer.Format("Qc.EvaluationStopped", _reagentLot.Text);
    }

    private async Task SubmitAsync()
    {
        _entry.EndEdit();

        var entries = new List<QcEntry>();

        // A named lot is the lot of one analyser's rows; the other analysers' rows are routine QC.
        var namedLot = NamedLotId;
        if (namedLot is not null && LotAnalyserId is null)
        {
            _dialogs.Warn(T("Qc.ChooseLotAnalyser"));
            _lotAnalyser.Focus();
            return;
        }

        foreach (DataGridViewRow gridRow in _entry.Rows)
        {
            if (gridRow.Tag is not QcEntryRow row) continue;

            var typed = gridRow.Cells[ColValue].Value?.ToString()?.Trim();

            if (string.IsNullOrWhiteSpace(typed)) continue;

            if (!decimal.TryParse(typed.Replace(',', '.'),
                                  System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture,
                                  out var value))
            {
                _dialogs.Warn(Localizer.Format("Result_NotANumber", typed));
                _entry.CurrentCell = gridRow.Cells[ColValue];
                return;
            }

            entries.Add(new QcEntry(row.QcTargetId, value,
                                    row.InstrumentId == LotAnalyserId ? namedLot : null));
        }

        if (entries.Count == 0)
        {
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Message.NothingSelected");
            return;
        }

        _submit.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var result = await _qc.SubmitAsync(entries);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            var outcome = result.Value;

            // A rejection is announced as a modal: patient results covered by the run are now
            // blocked, and that has to be acted on rather than noticed later.
            if (outcome.AnyRejected)
            {
                var lines = outcome.Results
                    .Where(r => r.Outcome == QcOutcome.Rejected)
                    .Select(r => $"• {r.ParameterCode} : {r.Value:N3} (Z {r.ZScore:N2}) — " +
                                 Localizer.Describe(
                                     r.ViolationKeys.Select(k => Error.Regulatory(k)).ToList()));

                _dialogs.Warn(
                    $"{T("QcOutcome.Rejected")}\n\n{string.Join('\n', lines)}\n\n" +
                    T("Qc_RejectionRequiresCorrectiveAction"),
                    T("Nav.QualityControl"));
            }

            if (outcome.Warnings.Count > 0)
                _dialogs.Info(Localizer.Describe(outcome.Warnings), T("Nav.QualityControl"));

            var accepted = outcome.Results.Count(r => r.Outcome == QcOutcome.Accepted);

            // Submitted: the reload below must not carry these values over as still pending.
            foreach (DataGridViewRow gridRow in _entry.Rows) gridRow.Cells[ColValue].Value = string.Empty;

            _status.ForeColor = outcome.AnyRejected ? UiTheme.Critical : UiTheme.Normal;
            _status.Text = $"{accepted}/{outcome.Results.Count} {T("QcOutcome.Accepted").ToLowerInvariant()}";

            await ActivateAsync();
        }
        finally
        {
            Cursor = Cursors.Default;
            _submit.Enabled = true;
        }
    }

    /// <summary>
    /// Reviews a rejected control. The corrective action is mandatory, and the reviewer is asked
    /// whether to open a non-conformity — pre-answered yes when patient results were affected,
    /// because at that point a departure has demonstrably occurred.
    /// </summary>
    private async Task ReviewSelectedAsync()
    {
        if (_review.CurrentRow?.Tag is not QcReviewRow row)
        {
            _status.Text = T("Message.NothingSelected");
            return;
        }

        var action = _dialogs.PromptForReason(
            $"{row.ParameterName.For(Localizer.CurrentLanguage)} · {WestgardRuleNames.Describe(row.ViolatedRules)}\n\n" +
            T("Quality.CorrectiveActionPrompt"),
            T("Quality.CorrectiveAction"), minimumLength: 15, maxLength: 2000);

        if (action is null) return;

        var openNonConformity = row.AffectedResultCount > 0
            || _dialogs.Confirm(T("Quality.OpenNonConformityQuestion"), T("NonConformity.Title"));

        var result = await _qc.ReviewAsync(row.QcResultId, action, openNonConformity);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Message.Saved");

        await ActivateAsync();
    }

    /// <summary>
    /// Establishes the laboratory's own control limits from a chosen window.
    /// <para>
    /// Deliberately a window and not "all history": a laboratory that has explained and corrected
    /// a bad period should be able to exclude it, and recomputing over everything would bake that
    /// period's scatter into the limits permanently.
    /// </para>
    /// </summary>
    /// <summary>
    /// Chooses which Westgard rules apply to the selected control.
    /// <para>
    /// Not a preference. The rule set is the analytical control strategy: 1₃ₛ alone on a
    /// high-precision assay under-detects systematic error, while running every rule on a noisy one
    /// produces so many false rejections that the bench learns to override them — which is worse
    /// than no rules at all. The laboratory has to be able to choose per control, and until now it
    /// was stuck with whatever the seeder set.
    /// </para>
    /// </summary>
    private async Task EditRulesAsync()
    {
        if (_entry.CurrentRow?.Tag is not QcEntryRow row)
        {
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Message.NothingSelected");
            return;
        }

        using var dialog = new WestgardRuleDialog(
            Localizer, row.ParameterCode, row.MaterialLotNumber, row.ActiveRules);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (dialog.Rules == row.ActiveRules) return;

        var result = await _qc.SetActiveRulesAsync(row.QcTargetId, dialog.Rules);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("Quality.EditRules"));
            return;
        }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Message.Saved");

        await ActivateAsync();
    }

    private async Task EstablishLimitsAsync()
    {
        if (_entry.CurrentRow?.Tag is not QcEntryRow row)
        {
            _status.Text = T("Message.NothingSelected");
            return;
        }

        var answer = _dialogs.PromptForText(
            T("Quality.EstablishLimitsPrompt", QcTarget.MinimumPointsForLaboratoryStatistics),
            T("Quality.EstablishLimits"), "60");

        if (answer is null) return;

        if (!int.TryParse(answer.Trim(), out var days) || days < 1)
        {
            _dialogs.Warn(Localizer.Format("Result_NotANumber", answer));
            return;
        }

        // A period beyond the calendar made the date arithmetic throw (« Erreur inattendue »).
        if (days > 3650)
        {
            _dialogs.Warn(Localizer["QcTarget_PeriodOutOfRange"]);
            return;
        }

        var to = DateOnly.FromDateTime(DateTime.Today);
        var from = to.AddDays(-days);

        var result = await _qc.RecomputeStatisticsAsync(row.QcTargetId, from, to);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        var (mean, sd, points) = result.Value;

        _dialogs.Info(
            $"{T("Quality.LaboratoryMean")} : {mean:N4}\n" +
            $"{T("Quality.LaboratorySd")} : {sd:N4}\n" +
            $"{T("Quality.PointCount")} : {points}",
            T("Quality.EstablishLimits"));

        await ActivateAsync();
    }
}
