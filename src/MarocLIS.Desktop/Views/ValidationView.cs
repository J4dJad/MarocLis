using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Results;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Biological validation — the biologist's screen.
/// <para>
/// The worklist is ordered by obligation, not by arrival: files with an uncommunicated critical
/// result come first, then urgent, then oldest. A biologist opening this screen should be looking
/// at the thing that most needs them without having to sort anything.
/// </para>
/// <para>
/// The three gates — qualification, quality control, critical communication — are enforced in the
/// application layer, so this screen's job is to make each refusal <em>understandable</em> and give
/// the biologist the action that clears it: record the telephone call, or justify the QC override.
/// Neither can be clicked past.
/// </para>
/// </summary>
public sealed class ValidationView : LocalizedView
{
    private readonly SelectionLoads _selectionLoads = new();
    private const string ColumnSelected = "selected";
    private const string ColumnParameter = "parameter";
    private const string ColumnValue = "value";
    private const string ColumnFlag = "flag";
    private const string ColumnAnalyser = "analyser";
    private const string ColumnUnit = "unit";
    private const string ColumnRange = "range";
    private const string ColumnPrevious = "previous";

    private readonly ValidationService _validation;
    private readonly ResultEntryService _entry;
    private readonly ReportReleaseService _reports;
    private readonly IDocumentOutput _documents;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<ValidationView>? _logger;

    private readonly DataGridView _worklist = UiTheme.Grid();
    private readonly DataGridView _details = UiTheme.Grid();
    private readonly Label _worklistHeading = new();
    private readonly Label _detailHeading = new();
    private readonly CheckBox _onlyUrgent = new();
    private readonly CheckBox _showValidated = new();
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _selectAll = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _validate = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _communicate = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _release = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _unvalidate = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new();

    private IReadOnlyList<ValidationWorklistEntry> _entries = [];
    private ResultEntrySheet? _sheet;
    private readonly Dictionary<Guid, ResultEntryRow> _rowsById = [];

    public ValidationView(ValidationService validation, ResultEntryService entry,
                          ReportReleaseService reports, IDocumentOutput documents, IUserDialogs dialogs,
                          ILocalizer localizer, ICultureManager cultureManager,
                          ILogger<ValidationView>? logger = null)
        : base(localizer, cultureManager)
    {
        _validation = validation;
        _entry = entry;
        _reports = reports;
        _documents = documents;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Validation");

    // ---- Layout -----------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 240,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        // --- worklist ----------------------------------------------------------------------------

        var top = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };

        _worklistHeading.Font = UiTheme.SectionTitle;
        _worklistHeading.ForeColor = UiTheme.TextPrimary;
        _worklistHeading.AutoSize = true;
        _worklistHeading.Location = new Point(0, 0);

        _onlyUrgent.Font = UiTheme.Small;
        _onlyUrgent.ForeColor = UiTheme.TextSecondary;
        _onlyUrgent.AutoSize = true;
        _onlyUrgent.Location = new Point(260, 4);

        // Brings back the files and results already validated but not yet reported: the only way
        // to reach a release in order to withdraw it, since the worklist otherwise drops a file the
        // moment its last result is validated.
        _showValidated.Font = UiTheme.Small;
        _showValidated.ForeColor = UiTheme.TextSecondary;
        _showValidated.AutoSize = true;
        _showValidated.Location = new Point(340, 4);

        _refresh.Bounds = new Rectangle(420, 0, 110, UiTheme.ButtonHeight - 4);

        // The heading sizes itself to its text, which in French is longer than the gap left for
        // it: the tick box and the button follow it instead of sitting at fixed positions under it.
        void FollowHeading()
        {
            _onlyUrgent.Left = _worklistHeading.Right + 16;
            _showValidated.Left = _onlyUrgent.Right + 16;
            _refresh.Left = Math.Max(420, _showValidated.Right + 16);
        }

        _worklistHeading.SizeChanged += (_, _) => FollowHeading();
        _onlyUrgent.SizeChanged += (_, _) => FollowHeading();
        _showValidated.SizeChanged += (_, _) => FollowHeading();

        BuildWorklistColumns();

        var worklistHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 30, 0, 0),
            BackColor = UiTheme.Surface
        };
        worklistHost.Controls.Add(_worklist);

        top.Controls.AddRange([worklistHost, _worklistHeading, _onlyUrgent, _showValidated, _refresh]);

        // The host is first so that it docks last and fills what is left, which also puts it in
        // front: the heading, the tick box and the refresh button sat behind it, invisible.
        _worklistHeading.BringToFront();
        _onlyUrgent.BringToFront();
        _showValidated.BringToFront();
        _refresh.BringToFront();

        // --- detail ------------------------------------------------------------------------------

        var bottom = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };

        _detailHeading.Font = UiTheme.SectionTitle;
        _detailHeading.ForeColor = UiTheme.TextPrimary;
        _detailHeading.AutoSize = true;
        _detailHeading.Location = new Point(0, 0);

        BuildDetailColumns();

        var detailHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 30, 0, 0),
            BackColor = UiTheme.Surface
        };
        detailHost.Controls.Add(_details);

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = UiTheme.Surface };

        _selectAll.Bounds = new Rectangle(0, 8, 150, UiTheme.ButtonHeight);
        _validate.Bounds = new Rectangle(160, 8, 220, UiTheme.ButtonHeight);
        _communicate.Bounds = new Rectangle(390, 8, 240, UiTheme.ButtonHeight);
        _release.Bounds = new Rectangle(640, 8, 200, UiTheme.ButtonHeight);
        _unvalidate.Bounds = new Rectangle(850, 8, 210, UiTheme.ButtonHeight);

        _status.Bounds = new Rectangle(1074, 14, 440, 20);
        _status.Font = UiTheme.Small;
        _status.ForeColor = UiTheme.TextSecondary;
        _status.AutoSize = false;

        actions.Controls.AddRange([_selectAll, _validate, _communicate, _release, _unvalidate, _status]);

        bottom.Controls.AddRange([detailHost, actions, _detailHeading]);
        _detailHeading.BringToFront();

        split.Panel1.Controls.Add(top);
        split.Panel2.Controls.Add(bottom);

        Controls.Add(split);

        SetDetailActionsEnabled(false);
    }

    private void BuildWorklistColumns()
    {
        _worklist.Columns.Clear();

        void Add(string name, int weight, bool mono = false,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
            => _worklist.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                FillWeight = weight,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = mono ? UiTheme.Monospace : UiTheme.Body,
                    Alignment = align
                }
            });

        Add("accession", 14, mono: true);
        Add("patient", 22);
        Add("age", 6, align: DataGridViewContentAlignment.MiddleRight);
        Add("priority", 9);
        Add("awaiting", 8, align: DataGridViewContentAlignment.MiddleRight);
        Add("abnormal", 8, align: DataGridViewContentAlignment.MiddleRight);
        Add("critical", 9, align: DataGridViewContentAlignment.MiddleRight);
        Add("alerts", 12);
        Add("registered", 12, mono: true);
    }

    private void BuildDetailColumns()
    {
        _details.Columns.Clear();
        _details.ReadOnly = false;
        _details.MultiSelect = false;

        _details.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = ColumnSelected,
            FillWeight = 5,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        void Add(string name, int weight, Font? font = null,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft,
                 Color? foreground = null)
            => _details.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                FillWeight = weight,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body,
                    Alignment = align,
                    ForeColor = foreground ?? UiTheme.TextPrimary
                }
            });

        Add(ColumnParameter, 32);
        Add(ColumnValue, 13, UiTheme.MonospaceBold, DataGridViewContentAlignment.MiddleRight);
        Add(ColumnFlag, 8, UiTheme.MonospaceBold, DataGridViewContentAlignment.MiddleCenter);
        Add(ColumnUnit, 9, UiTheme.Small);
        Add(ColumnRange, 18, UiTheme.Small, foreground: UiTheme.TextSecondary);
        Add(ColumnPrevious, 15, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight,
            UiTheme.TextSecondary);

        // The analyser's own doubts (linearity, reagent, sample alarms): a value it flagged must not
        // be signed without the biologist seeing why.
        Add(ColumnAnalyser, 16, UiTheme.Small, foreground: UiTheme.Abnormal);
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();
        _onlyUrgent.CheckedChanged += async (_, _) => await ActivateAsync();
        _showValidated.CheckedChanged += async (_, _) => await ActivateAsync();

        _worklist.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadSelectedAsync());

        _selectAll.Click += (_, _) => ToggleAll();
        _validate.Click += async (_, _) => await ValidateSelectedAsync();
        _communicate.Click += async (_, _) => await CommunicateCriticalAsync();
        _release.Click += async (_, _) => await ReleaseReportAsync();
        _unvalidate.Click += async (_, _) => await UnvalidateSelectedAsync();

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Results.BiologicalValidate, _validate);
        PermissionGate.Require(Permissions.Results.EnterCritical, _communicate);
        PermissionGate.Require(Permissions.Reports.Print, _release);
        PermissionGate.Require(Permissions.Results.Unvalidate, _unvalidate);

        _details.CellFormatting += OnDetailFormatting;
        _worklist.CellFormatting += OnWorklistFormatting;

        // Double-clicking a file opens it for entry rather than validation, which is what a
        // biologist wants when a value looks wrong and needs re-checking at the bench.
        _worklist.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _worklist.Rows[e.RowIndex].Tag is Guid) _details.Focus();
        };
    }

    private void OnWorklistFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _worklist.Rows.Count) return;
        if (_worklist.Rows[e.RowIndex].Tag is not Guid id) return;

        var entry = _entries.FirstOrDefault(x => x.LabOrderId == id);
        if (entry is null) return;

        // An uncommunicated critical result is the only state here that is a breached obligation
        // rather than a backlog, so it is the only one painted as critical.
        if (entry.UncommunicatedCriticalCount > 0)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
            e.CellStyle.Font = new Font(e.CellStyle.Font ?? UiTheme.Body, FontStyle.Bold);
        }
        else if (entry.IsOverdue || entry.HasQcFailure)
        {
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
    }

    private void OnDetailFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _details.Rows.Count) return;
        if (_details.Rows[e.RowIndex].Tag is not Guid id) return;
        if (!_rowsById.TryGetValue(id, out var row)) return;

        var appearance = UiTheme.ForInterpretation(row.Interpretation);

        if (_details.Columns[e.ColumnIndex].Name is ColumnValue or ColumnFlag)
        {
            e.CellStyle!.ForeColor = appearance.Foreground;
            e.CellStyle.BackColor = appearance.Background;
            if (appearance.Bold)
                e.CellStyle.Font = new Font(e.CellStyle.Font ?? UiTheme.Monospace, FontStyle.Bold);
        }

        // Already released, listed only so it can be withdrawn: set apart from what awaits release.
        if (IsReleased(row))
        {
            e.CellStyle!.ForeColor = UiTheme.TextSecondary;
            e.CellStyle.Font = new Font(e.CellStyle.Font ?? UiTheme.Body, FontStyle.Italic);
        }

        if (row.IsHeading)
        {
            e.CellStyle!.BackColor = UiTheme.SurfaceSunken;
            e.CellStyle.Font = new Font(UiTheme.Body, FontStyle.Bold | FontStyle.Italic);
        }
    }

    protected override void ApplyTexts()
    {
        _worklistHeading.Text = T("Validation.PendingBiological");
        _detailHeading.Text = T("Result.Plural");
        _onlyUrgent.Text = T("Priority.Urgent");
        _showValidated.Text = T("Validation.ShowValidated");
        _refresh.Text = T("Action.Refresh");
        _selectAll.Text = T("Action.SelectAll");
        _validate.Text = T("Validation.Biological");
        _communicate.Text = T("Nav.CriticalResults");
        _release.Text = T("Report.Title");
        _unvalidate.Text = T("Validation.Unvalidate");

        if (_worklist.Columns.Count > 0)
        {
            _worklist.Column("accession").HeaderText = T("Order.AccessionNumber");
            _worklist.Column("patient").HeaderText = T("Patient.Title");
            _worklist.Column("age").HeaderText = T("Patient.Age");
            _worklist.Column("priority").HeaderText = T("Field.Priority");
            _worklist.Column("awaiting").HeaderText = T("Validation.PendingBiological");
            _worklist.Column("abnormal").HeaderText = T("Interpretation.Abnormal");
            _worklist.Column("critical").HeaderText = T("Flag.CriticalHigh");
            _worklist.Column("alerts").HeaderText = T("Result.Flags");
            _worklist.Column("registered").HeaderText = T("Order.RegisteredAt");
        }

        if (_details.Columns.Count > 0)
        {
            _details.Column(ColumnSelected).HeaderText = string.Empty;
            _details.Column(ColumnParameter).HeaderText = T("Result.Parameter");
            _details.Column(ColumnValue).HeaderText = T("Result.Value");
            _details.Column(ColumnFlag).HeaderText = T("Result.Flags");
            _details.Column(ColumnUnit).HeaderText = T("Result.Unit");
            _details.Column(ColumnRange).HeaderText = T("Result.ReferenceRange");
            _details.Column(ColumnPrevious).HeaderText = T("Result.Previous");
            _details.Column(ColumnAnalyser).HeaderText = T("Result.InstrumentFlags");
        }
    }

    // ---- Worklist ---------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _validation.GetWorklistAsync(onlyUrgent: _onlyUrgent.Checked,
                                                            includeValidated: _showValidated.Checked,
                                                            ct: ct);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            _entries = result.Value;
            RenderWorklist();
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the validation worklist failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private void RenderWorklist()
    {
        _worklist.SuspendLayout();
        _worklist.Rows.Clear();

        foreach (var entry in _entries)
        {
            var alerts = new List<string>();
            if (entry.UncommunicatedCriticalCount > 0) alerts.Add($"!{entry.UncommunicatedCriticalCount}");
            if (entry.HasQcFailure) alerts.Add("CIQ");
            if (entry.HasDeltaCheckFailure) alerts.Add("Δ");
            if (entry.HasSampleDefect) alerts.Add("éch.");
            if (entry.IsOverdue) alerts.Add(T("Order.Overdue"));

            var index = _worklist.Rows.Add(
                entry.AccessionNumber,
                entry.PatientName,
                entry.AgeYears?.ToString() ?? string.Empty,
                entry.Priority == Domain.Orders.OrderPriority.Routine
                    ? string.Empty
                    : T($"Priority.{entry.Priority}"),
                entry.AwaitingCount.ToString(),
                entry.AbnormalCount > 0 ? entry.AbnormalCount.ToString() : string.Empty,
                entry.CriticalCount > 0 ? entry.CriticalCount.ToString() : string.Empty,
                string.Join(" ", alerts),
                entry.RegisteredAt.ToString("dd/MM HH:mm"));

            _worklist.Rows[index].Tag = entry.LabOrderId;
        }

        _worklist.ResumeLayout(performLayout: true);

        _status.ForeColor = UiTheme.TextSecondary;
        _status.Text = $"{_entries.Count} {T("Order.Plural").ToLowerInvariant()}";

        if (_worklist.Rows.Count > 0) _worklist.SelectFirstRow();
        else ClearDetails();
    }

    private async Task LoadSelectedAsync()
    {
        if (_worklist.CurrentRow?.Tag is not Guid orderId)
        {
            ClearDetails();
            return;
        }

        var sheet = await _entry.OpenAsync(orderId);

        if (sheet.IsFailure)
        {
            ClearDetails();
            _status.ForeColor = UiTheme.Critical;
            _status.Text = Describe(sheet);
            return;
        }

        _sheet = sheet.Value;
        RenderDetails(_sheet);
    }

    private void RenderDetails(ResultEntrySheet sheet)
    {
        _rowsById.Clear();

        _detailHeading.Text = $"{sheet.AccessionNumber} — {sheet.PatientName}" +
                              (sheet.AgeYears is { } age ? $", {age} {T("Report.Years")}" : string.Empty) +
                              $", {T($"Gender.{sheet.Gender}")}";

        _details.SuspendLayout();
        _details.Rows.Clear();

        foreach (var row in sheet.Rows)
        {
            _rowsById[row.TestResultId] = row;

            // Only what is awaiting release is listed: a biologist validating a file does not need
            // to scroll past results they released last week. Validated results come back on
            // request, to be ticked for un-validation.
            var withdrawable = _showValidated.Checked && !row.IsHeading
                               && row.Status == TestResultStatus.Validated;

            if (row.Status is not (TestResultStatus.TechnicallyValidated or TestResultStatus.Entered)
                && !row.IsHeading && !withdrawable)
                continue;

            // Only a technically validated value can be released. An entered one is listed so the
            // biologist sees the whole file, but it is neither pre-ticked nor tickable: the service
            // refuses it until the technician has checked it. A validated one is tickable for
            // un-validation but never pre-ticked: withdrawing a release is a deliberate act.
            var releasable = !row.IsHeading && row.Status == TestResultStatus.TechnicallyValidated;

            var index = _details.Rows.Add(
                // Pre-ticked: the common case is releasing the whole file, and the biologist
                // unticks what they want to hold back.
                releasable,
                new string(' ', row.IndentLevel * 4) + row.ParameterName.For(Localizer.CurrentLanguage),
                row.IsHeading ? string.Empty : row.CurrentValue ?? string.Empty,
                UiTheme.FlagGlyphs(row.Flags) is { Length: > 0 } glyphs
                    ? glyphs
                    : UiTheme.ForInterpretation(row.Interpretation).Glyph,
                row.UnitSymbol ?? string.Empty,
                row.ReferenceRangeText ?? string.Empty,
                row.PreviousValue is { } previous
                    ? $"{previous:N2}" +
                      (row.PreviousDate is { } date ? $"  {date:dd/MM/yy}" : string.Empty)
                    : string.Empty,
                row.InstrumentFlags ?? string.Empty);

            var gridRow = _details.Rows[index];
            gridRow.Tag = row.TestResultId;
            gridRow.Cells[ColumnSelected].ReadOnly = !releasable && !withdrawable;
        }

        _details.ResumeLayout(performLayout: true);

        SetDetailActionsEnabled(_details.Rows.Count > 0);

        var uncommunicated = sheet.Rows.Any(r => r.NeedsCriticalCommunication);

        _communicate.Enabled = uncommunicated;
        _communicate.ForeColor = uncommunicated ? UiTheme.Critical : UiTheme.TextSecondary;
        _communicate.Font = uncommunicated ? UiTheme.BodyBold : UiTheme.Body;
    }

    private void ClearDetails()
    {
        _sheet = null;
        _rowsById.Clear();
        _details.Rows.Clear();
        _detailHeading.Text = T("App.NoData");
        SetDetailActionsEnabled(false);
    }

    private void SetDetailActionsEnabled(bool enabled)
    {
        _selectAll.Enabled = enabled;
        _validate.Enabled = enabled;
        _release.Enabled = enabled;

        // Offered only when something on the sheet has actually been validated — retracting
        // nothing is not a meaningful action, and an always-enabled button next to Validate invites
        // the wrong click.
        _unvalidate.Enabled = enabled
                              && _rowsById.Values.Any(r => !r.IsHeading
                                                           && r.Status is TestResultStatus.Validated
                                                                       or TestResultStatus.Reported);
    }

    private void ToggleAll()
    {
        // Selects what awaits release; released rows are ticked one by one, on purpose.
        var toggleable = _details.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.Cells[ColumnSelected].ReadOnly
                        && !(r.Tag is Guid id && _rowsById.TryGetValue(id, out var row) && IsReleased(row)))
            .ToList();

        var target = toggleable.Any(r => r.Cells[ColumnSelected].Value is not true);

        foreach (var row in toggleable) row.Cells[ColumnSelected].Value = target;
    }

    private static bool IsReleased(ResultEntryRow row) =>
        row.Status is TestResultStatus.Validated or TestResultStatus.Reported;

    // ---- Validation --------------------------------------------------------------------------------

    /// <summary>
    /// Retracts a biological validation.
    /// <para>
    /// Required by ISO 15189 and by ordinary practice: a biologist who releases a result and then
    /// sees the reason it is wrong has to be able to withdraw it. The domain refuses once the
    /// result has reached a report — at that point the remedy is a rectified report, not making the
    /// figure quietly disappear — and that refusal is shown as written rather than as a generic
    /// failure.
    /// </para>
    /// <para>
    /// The value itself is not changed. The release is withdrawn and a revision is recorded against
    /// it, so the trail shows a validated figure that was retracted rather than a figure that was
    /// never validated.
    /// </para>
    /// </summary>
    private async Task UnvalidateSelectedAsync()
    {
        if (_sheet is null) return;

        _details.EndEdit();

        var ids = new List<Guid>();

        foreach (DataGridViewRow gridRow in _details.Rows)
        {
            if (gridRow.Tag is not Guid id) continue;
            if (!_rowsById.TryGetValue(id, out var row) || row.IsHeading) continue;
            if (gridRow.Cells[ColumnSelected].Value is not true) continue;

            // Only a released result has a validation to withdraw; the ticked rows awaiting
            // release are what the Validate button is for.
            if (!IsReleased(row)) continue;

            ids.Add(id);
        }

        if (ids.Count == 0)
        {
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Message.NothingSelected");
            return;
        }

        var reason = _dialogs.PromptForReason(
            T("Validation.UnvalidatePrompt", ids.Count), T("Validation.Unvalidate"),
            minimumLength: 15);

        if (reason is null) return;

        var result = await _validation.UnvalidateAsync(_sheet.LabOrderId, ids, reason);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("Validation.Unvalidate"));
            return;
        }

        _status.ForeColor = UiTheme.Abnormal;
        _status.Text = $"{ids.Count} × {T("Validation.Unvalidated")}";

        await ActivateAsync();
    }

    private async Task ValidateSelectedAsync()
    {
        if (_sheet is null) return;

        _details.EndEdit();

        var decisions = new List<ValidationDecision>();
        var qcAffected = new List<string>();

        foreach (DataGridViewRow gridRow in _details.Rows)
        {
            if (gridRow.Tag is not Guid id) continue;
            if (!_rowsById.TryGetValue(id, out var row) || row.IsHeading) continue;

            // A released row is listed for un-validation only: validating it again means nothing.
            if (IsReleased(row)) continue;

            var selected = gridRow.Cells[ColumnSelected].Value is true;

            if (selected && row.Flags.HasFlag(ResultFlag.QcOutOfControl))
                qcAffected.Add(row.ParameterName.For(Localizer.CurrentLanguage));

            decisions.Add(new ValidationDecision(id, selected));
        }

        if (decisions.All(d => !d.Validate))
        {
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Message.NothingSelected");
            return;
        }

        // A QC override needs its justification collected before the attempt, not as a rejection
        // afterwards — otherwise the biologist validates, is refused, and has to start again.
        string? qcJustification = null;

        if (qcAffected.Count > 0)
        {
            qcJustification = _dialogs.PromptForReason(
                $"{T("Result_QcOutOfControlBlocksValidation")}\n\n" +
                $"{string.Join(", ", qcAffected)}\n\n{T("Validation.OverrideJustification")}",
                T("Validation.QcOverride"),
                minimumLength: 15);

            if (qcJustification is null) return;

            decisions = [.. decisions.Select(d =>
                d.Validate && _rowsById.TryGetValue(d.TestResultId, out var r)
                           && r.Flags.HasFlag(ResultFlag.QcOutOfControl)
                    ? d with { QcOverrideJustification = qcJustification }
                    : d)];
        }

        _validate.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var outcome = await _validation.ValidateAsync(_sheet.LabOrderId, decisions);

            if (outcome.IsFailure)
            {
                _dialogs.ShowFailure(outcome);
                return;
            }

            var result = outcome.Value;

            if (result.Blocked.Count > 0)
            {
                // Refusals are shown in full: each one names a gate the biologist has to clear,
                // and summarising them as "some results could not be validated" would be useless.
                _dialogs.Warn(Localizer.Describe(result.Blocked), T("Validation.Biological"));
            }

            _status.ForeColor = result.Blocked.Count > 0 ? UiTheme.Abnormal : UiTheme.Normal;
            _status.Text = $"{result.Validated} {T("Validation.ValidatedBy").ToLowerInvariant()}";

            await ActivateAsync();
        }
        finally
        {
            Cursor = Cursors.Default;
            _validate.Enabled = true;
        }
    }

    /// <summary>
    /// Records the telephone communication of a critical result, including the read-back.
    /// <para>
    /// The read-back is not optional and not a checkbox the operator can tick without thinking:
    /// they are asked what the recipient repeated, and the value is stored. A notification without
    /// a confirmed read-back leaves the result unreleasable, which is the intended behaviour.
    /// </para>
    /// </summary>
    private async Task CommunicateCriticalAsync()
    {
        if (_sheet is null) return;

        // Only the values not yet communicated: one already read back must not be called again.
        var critical = _sheet.Rows.Where(r => r.NeedsCriticalCommunication).ToList();

        if (critical.Count == 0) return;

        foreach (var row in critical)
        {
            var parameterName = row.ParameterName.For(Localizer.CurrentLanguage);
            var value = $"{row.CurrentValue} {row.UnitSymbol}".Trim();

            var raised = await _validation.RaiseCriticalNotificationAsync(
                row.TestResultId, parameterName, value,
                NotificationChannel.Telephone, contactUsed: null, prescriberId: null,
                displayedEnteredAt: row.EnteredAt);

            if (raised.IsFailure)
            {
                _dialogs.ShowFailure(raised);
                return;
            }

            var recipient = _dialogs.PromptForText(
                $"{parameterName} : {value}\n\n{T("Notification_RecipientNameRequired")}",
                T("Nav.CriticalResults"));

            if (recipient is null)
            {
                await _validation.MarkCriticalUnreachableAsync(
                    raised.Value, "Communication abandonnée par l'opérateur");
                continue;
            }

            var readBack = _dialogs.PromptForText(
                $"{T("Validation.ReadBackPrompt")}\n\n{parameterName}",
                T("Nav.CriticalResults"));

            if (string.IsNullOrWhiteSpace(readBack))
            {
                _dialogs.Warn(T("Notification_ReadBackRequired"), T("Nav.CriticalResults"));
                await _validation.MarkCriticalUnreachableAsync(
                    raised.Value, "Relecture non confirmée");
                continue;
            }

            var acknowledged = await _validation.AcknowledgeCriticalNotificationAsync(
                raised.Value, recipient, recipientRole: null,
                readBackConfirmed: true, readBackValue: readBack);

            if (acknowledged.IsFailure) _dialogs.ShowFailure(acknowledged);
        }

        await LoadSelectedAsync();
    }

    private async Task ReleaseReportAsync()
    {
        if (_sheet is null) return;

        if (!_dialogs.Confirm($"{T("Report.Title")} — {_sheet.AccessionNumber}")) return;

        _release.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var released = await _reports.ReleaseAsync(_sheet.LabOrderId);

            if (released.IsFailure)
            {
                _dialogs.ShowFailure(released);
                return;
            }

            // Handed to the operator's usual viewer rather than shown in a preview window this
            // application would build worse. IDocumentOutput owns the file's lifetime on disk.
            var opened = await _documents.OpenAsync(released.Value.Pdf, "pdf");

            if (opened.IsFailure) _dialogs.ShowFailure(opened, T("Report.Title"));

            _status.ForeColor = UiTheme.Normal;
            _status.Text = $"{T("Report.Title")} v{released.Value.VersionNumber} — {T("Message.Saved")}";

            await ActivateAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Releasing the report failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
        finally
        {
            Cursor = Cursors.Default;
            _release.Enabled = true;
        }
    }
}
