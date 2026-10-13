using System.ComponentModel;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Results;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Results;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Result entry.
/// <para>
/// This screen is used all day, at speed, with one hand on a barcode scanner. Every design choice
/// below follows from that:
/// </para>
/// <list type="bullet">
/// <item><b>The scan box always has focus.</b> A technician scans a tube, types values, presses
/// Enter and scans the next. No mouse in the loop.</item>
/// <item><b>Enter commits a cell and moves down</b>, rather than opening a row or submitting the
/// form. Values are entered column-wise, so downward is the direction the hand expects.</item>
/// <item><b>Reference interval and previous value are on the same row as the input.</b> Judging
/// plausibility means comparing against both; putting either in a side panel means it is not
/// looked at.</item>
/// <item><b>Calculated rows are visibly read-only</b> and recompute after each save, so a
/// technician never wonders why the LDL will not accept a value.</item>
/// <item><b>Flags are glyphs in their own column</b>, not colour alone — colour-blind operators
/// and monochrome screens both exist.</item>
/// <item><b>The fiche de paillasse is printed from here</b>, because this is where the values on it
/// come back to be keyed in: the technician prints the bench's pending work, measures, writes, and
/// returns to the same screen with the sheet in hand.</item>
/// </list>
/// </summary>
public sealed class ResultEntryView : LocalizedView
{
    private const string ColumnParameter = "parameter";
    private const string ColumnValue = "value";
    private const string ColumnFlag = "flag";
    private const string ColumnUnit = "unit";
    private const string ColumnRange = "range";
    private const string ColumnPrevious = "previous";
    private const string ColumnComment = "comment";

    private readonly ResultEntryService _entry;
    private readonly BenchWorksheetService _benchSheets;
    private readonly IBenchWorksheetRenderer _benchSheetRenderer;
    private readonly IDocumentOutput _documents;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<ResultEntryView>? _logger;

    private readonly TextBox _scan = UiTheme.Input(260);
    private readonly Label _scanLabel = new();
    private readonly Button _load = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _benchSheet = UiTheme.SecondaryButton(string.Empty);
    private readonly Panel _patientBar = new();
    private readonly Label _patientName = new();
    private readonly Label _patientDetail = new();
    private readonly Label _orderDetail = new();
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Button _save = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _validate = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new();

    // ---- worklist: the dossiers with results to enter ----
    private readonly Label _worklistTitle = new();
    private readonly DataGridView _worklist = UiTheme.Grid();
    private readonly Button _refreshWorklist = UiTheme.SecondaryButton(string.Empty);
    private IReadOnlyList<PendingEntryOrder> _pending = [];
    private bool _renderingWorklist;

    private ResultEntrySheet? _sheet;
    private readonly Dictionary<Guid, ResultEntryRow> _rowsById = [];

    /// <summary>The department last printed for, offered again: a technician prints for their own bench.</summary>
    private Guid? _lastBenchDepartmentId;

    public ResultEntryView(ResultEntryService entry, BenchWorksheetService benchSheets,
                           IBenchWorksheetRenderer benchSheetRenderer, IDocumentOutput documents,
                           IUserDialogs dialogs, ILocalizer localizer, ICultureManager cultureManager,
                           ILogger<ResultEntryView>? logger = null)
        : base(localizer, cultureManager)
    {
        _entry = entry;
        _benchSheets = benchSheets;
        _benchSheetRenderer = benchSheetRenderer;
        _documents = documents;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.ResultEntry");

    /// <summary>
    /// Set by the caller to open straight onto a known order, e.g. from a worklist click.
    /// <para>
    /// Hidden from designer serialization: every screen in this application is built in code, so
    /// there is no .Designer.cs for a designer to write this into, and the WinForms analyser is
    /// right that a public settable property on a control with no serialization policy is
    /// ambiguous.
    /// </para>
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Guid? InitialOrderId { get; set; }

    // ---- Layout -----------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter, UiTheme.Gutter, UiTheme.Gutter, UiTheme.Gutter);

        // --- scan bar ---------------------------------------------------------------------------

        var scanBar = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = UiTheme.Surface };

        _scanLabel.Font = UiTheme.Small;
        _scanLabel.ForeColor = UiTheme.TextSecondary;
        _scanLabel.AutoSize = true;
        _scanLabel.Location = new Point(0, 4);

        // An accession number and a barcode are Latin tokens; the field stays LTR in Arabic.
        _scan.Tag = UiTags.LatinValue;
        _scan.Font = UiTheme.Monospace;
        _scan.Location = new Point(0, 22);

        _load.Bounds = new Rectangle(270, 21, 110, UiTheme.ButtonHeight);

        // At the far end of the scan bar, away from the scan box: it is used once a run, not once a
        // tube, and must not be what an Enter or a stray click lands on mid-entry.
        _benchSheet.Size = new Size(200, UiTheme.ButtonHeight);
        _benchSheet.TabStop = false;

        scanBar.Controls.AddRange([_scanLabel, _scan, _load, _benchSheet]);
        scanBar.Resize += (_, _) =>
            _benchSheet.Location = new Point(
                Math.Max(_load.Right + UiTheme.Gutter, scanBar.ClientSize.Width - _benchSheet.Width), 21);

        // --- patient context --------------------------------------------------------------------

        _patientBar.Dock = DockStyle.Top;
        _patientBar.Height = 58;
        _patientBar.BackColor = UiTheme.SurfaceRaised;
        _patientBar.Padding = new Padding(UiTheme.Gutter, UiTheme.GutterTight, UiTheme.Gutter, 0);
        _patientBar.Visible = false;

        _patientName.Font = UiTheme.SectionTitle;
        _patientName.ForeColor = UiTheme.TextPrimary;
        _patientName.AutoSize = true;
        _patientName.Location = new Point(UiTheme.Gutter, 6);

        _patientDetail.Font = UiTheme.Small;
        _patientDetail.ForeColor = UiTheme.TextSecondary;
        _patientDetail.AutoSize = true;
        _patientDetail.Location = new Point(UiTheme.Gutter, 32);

        _orderDetail.Font = UiTheme.SmallBold;
        _orderDetail.ForeColor = UiTheme.Accent;
        _orderDetail.AutoSize = true;
        _orderDetail.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        _patientBar.Controls.AddRange([_patientName, _patientDetail, _orderDetail]);
        // Re-placed when its text changes as well: it sizes itself to the text, and placed from its
        // previous width it ran off the right edge of the panel.
        void PlaceOrderDetail() =>
            _orderDetail.Location = new Point(
                Math.Max(_patientName.Right + UiTheme.Gutter,
                         _patientBar.ClientSize.Width - _orderDetail.Width - UiTheme.Gutter), 8);

        _patientBar.Resize += (_, _) => PlaceOrderDetail();
        _orderDetail.SizeChanged += (_, _) => PlaceOrderDetail();

        // --- grid --------------------------------------------------------------------------------

        BuildColumns();
        _grid.ReadOnly = false;          // only the value and comment columns are writable
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.StandardTab = false;
        _grid.MultiSelect = false;
        _grid.AllowUserToResizeColumns = true;

        var gridHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, UiTheme.GutterTight, 0, 0),
            BackColor = UiTheme.Surface
        };
        gridHost.Controls.Add(_grid);

        // --- actions -----------------------------------------------------------------------------

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = UiTheme.Surface };

        _save.Bounds = new Rectangle(0, 10, 190, UiTheme.ButtonHeight);
        _validate.Bounds = new Rectangle(200, 10, 220, UiTheme.ButtonHeight);
        _validate.Enabled = false;

        _status.Bounds = new Rectangle(434, 16, 620, 20);
        _status.Font = UiTheme.Small;
        _status.ForeColor = UiTheme.TextSecondary;
        _status.AutoSize = false;

        actions.Controls.AddRange([_save, _validate, _status]);

        var entryHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        entryHost.Controls.Add(gridHost);
        entryHost.Controls.Add(actions);
        entryHost.Controls.Add(_patientBar);
        entryHost.Controls.Add(scanBar);

        // --- worklist ----------------------------------------------------------------------------
        // The screen used to open on an empty grid and a scan box: without a tube in hand there was
        // no way to see what was waiting. The dossiers with results to enter are listed on the left.

        var worklistBar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = UiTheme.Surface };

        _worklistTitle.Font = UiTheme.SectionTitle;
        _worklistTitle.ForeColor = UiTheme.TextPrimary;
        _worklistTitle.AutoSize = true;
        _worklistTitle.Location = new Point(0, 8);

        _refreshWorklist.Size = new Size(110, UiTheme.ButtonHeight - 4);
        _refreshWorklist.TabStop = false;

        worklistBar.Controls.AddRange([_worklistTitle, _refreshWorklist]);
        worklistBar.Resize += (_, _) =>
            _refreshWorklist.Location = new Point(
                Math.Max(_worklistTitle.Right + UiTheme.Gutter,
                         worklistBar.ClientSize.Width - _refreshWorklist.Width - UiTheme.GutterTight), 4);

        BuildWorklistColumns();

        var worklistHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        worklistHost.Controls.Add(_worklist);
        worklistHost.Controls.Add(worklistBar);

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 360,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        split.Panel1.Controls.Add(worklistHost);
        split.Panel2.Controls.Add(entryHost);
        split.Panel2.Padding = new Padding(UiTheme.GutterTight, 0, 0, 0);

        Controls.Add(split);
    }

    private void BuildWorklistColumns()
    {
        _worklist.Columns.Clear();

        void Add(string name, int weight, Font? font = null, bool right = false)
            => _worklist.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body,
                    Alignment = right ? DataGridViewContentAlignment.MiddleRight
                                      : DataGridViewContentAlignment.MiddleLeft
                }
            });

        Add("accession", 26, UiTheme.Monospace);
        Add("patient", 40);
        Add("pending", 10, UiTheme.MonospaceBold, right: true);
        Add("received", 16, UiTheme.Monospace, right: true);
        Add("departments", 14, UiTheme.Small);

        _worklist.Columns["patient"]!.MinimumWidth = 130;
    }

    // The worklist, a click on it and a scan all read through this screen's one database
    // context, which refuses a second query while one is running: a click raised both
    // SelectionChanged and CellClick, and the second load failed with "A second operation was
    // started on this context instance". Every load now waits for the one before it.
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private async Task LoadWorklistAsync(CancellationToken ct = default)
    {
        await _loadGate.WaitAsync(CancellationToken.None);
        try
        {
            await LoadWorklistCoreAsync(ct);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task LoadWorklistCoreAsync(CancellationToken ct)
    {
        try
        {
            var result = await _benchSheets.GetPendingOrdersAsync(ct);

            if (result.IsFailure)
            {
                // Without the right to enter results the list stays empty; the scan box still works
                // for the screens that only read.
                _pending = [];
            }
            else
            {
                _pending = result.Value;
            }

            RenderWorklist();
        }
        catch (OperationCanceledException)
        {
            // Navigated away mid-load.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the result entry worklist failed.");
        }
    }

    private void RenderWorklist()
    {
        var current = _sheet?.AccessionNumber;

        _renderingWorklist = true;

        try
        {
            _worklist.SuspendLayout();
            _worklist.Rows.Clear();

            foreach (var order in _pending)
            {
                var index = _worklist.Rows.Add(
                    order.AccessionNumber,
                    order.PatientName,
                    order.PendingAnalyses.ToString(),
                    order.ReceivedAt?.ToLocalTime().ToString("dd/MM HH:mm") ?? string.Empty,
                    order.Departments);

                var row = _worklist.Rows[index];
                row.Tag = order.AccessionNumber;

                if (order.IsUrgent)
                {
                    row.DefaultCellStyle.ForeColor = UiTheme.Critical;
                    row.DefaultCellStyle.Font = UiTheme.BodyBold;
                }
            }

            _worklist.ResumeLayout(performLayout: true);
            _worklist.ClearSelection();

            if (current is not null)
            {
                var row = _worklist.Rows.Cast<DataGridViewRow>()
                    .FirstOrDefault(r => r.Tag is string accession && accession == current);

                if (row is not null)
                {
                    _worklist.CurrentCell = row.Cells[0];
                    row.Selected = true;
                }
            }

            _worklistTitle.Text = T("ResultEntry.Worklist", _pending.Count);
        }
        finally
        {
            _renderingWorklist = false;
        }
    }

    private void BuildColumns()
    {
        _grid.Columns.Clear();

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnParameter,
            FillWeight = 34,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnValue,
            FillWeight = 14,
            // Monospaced and right-aligned: digits line up down the column, which is how a
            // technician spots the value with an extra zero.
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = UiTheme.MonospaceBold,
                Alignment = DataGridViewContentAlignment.MiddleRight
            },
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnFlag,
            FillWeight = 6,
            ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = UiTheme.MonospaceBold,
                Alignment = DataGridViewContentAlignment.MiddleCenter
            },
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnUnit, FillWeight = 9, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { Font = UiTheme.Small },
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnRange, FillWeight = 16, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary
            },
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnPrevious, FillWeight = 11, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = UiTheme.Monospace,
                ForeColor = UiTheme.TextSecondary,
                Alignment = DataGridViewContentAlignment.MiddleRight
            },
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnComment, FillWeight = 10,
            DefaultCellStyle = new DataGridViewCellStyle { Font = UiTheme.Small },
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        // The parameter name is what the technician reads the value against: never "Polynucléaires …".
        _grid.Columns[ColumnParameter]!.MinimumWidth = 210;
    }

    private void WireBehaviour()
    {
        _load.Click += async (_, _) => await LoadAsync(_scan.Text);
        _refreshWorklist.Click += async (_, _) => await LoadWorklistAsync();

        // A click (or the arrow keys) on a dossier opens it, as scanning its tube would.
        _worklist.SelectionChanged += async (_, _) =>
        {
            if (_renderingWorklist || !_worklist.Focused) return;
            if (_worklist.CurrentRow?.Tag is not string accession) return;
            if (_sheet?.AccessionNumber == accession) return;

            await LoadAsync(accession, skipIfOpen: true);
        };
        _worklist.CellClick += async (_, e) =>
        {
            if (e.RowIndex < 0 || _worklist.Rows[e.RowIndex].Tag is not string accession) return;
            if (_sheet?.AccessionNumber == accession) return;

            await LoadAsync(accession, skipIfOpen: true);
        };
        _benchSheet.Click += async (_, _) => await PrintBenchSheetAsync();

        // A barcode scanner ends its scan with Enter, so Enter in the scan box loads the order.
        _scan.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await LoadAsync(_scan.Text);
        };

        _save.Click += async (_, _) => await SubmitAsync();
        _validate.Click += async (_, _) => await TechnicallyValidateAsync();

        // Enter commits the cell and steps down the column, which is how values are actually
        // entered. The grid's default — move right — would land in a read-only unit cell.
        _grid.KeyDown += OnGridKeyDown;
        _grid.CellFormatting += OnCellFormatting;
        _grid.CellBeginEdit += OnCellBeginEdit;
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;

        e.SuppressKeyPress = true;
        _grid.EndEdit();

        var column = _grid.CurrentCell?.ColumnIndex ?? 0;
        var next = (_grid.CurrentCell?.RowIndex ?? -1) + 1;

        // Skip read-only rows (calculated parameters and headings) on the way down.
        while (next < _grid.Rows.Count && _grid.Rows[next].Cells[ColumnValue].ReadOnly) next++;

        if (next < _grid.Rows.Count)
            _grid.CurrentCell = _grid.Rows[next].Cells[column];
        else
            _save.Focus();
    }

    private void OnCellBeginEdit(object? sender, DataGridViewCellCancelEventArgs e)
    {
        if (_grid.Rows[e.RowIndex].Tag is not Guid id) return;
        if (!_rowsById.TryGetValue(id, out var row)) return;

        // A validated value cannot be edited in the grid: it needs the revision path, with a
        // recorded reason, because a released figure may already have been acted on.
        if (row.Status is TestResultStatus.Validated or TestResultStatus.Reported)
        {
            e.Cancel = true;
            _status.ForeColor = UiTheme.Abnormal;
            _status.Text = T("Result_ValidatedRequiresRevision");
        }
    }

    /// <summary>
    /// Paints clinical state. Colour <em>and</em> weight <em>and</em> a glyph, never colour alone.
    /// </summary>
    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
        if (_grid.Rows[e.RowIndex].Tag is not Guid id) return;
        if (!_rowsById.TryGetValue(id, out var row)) return;

        var appearance = UiTheme.ForInterpretation(row.Interpretation);

        if (_grid.Columns[e.ColumnIndex].Name is ColumnValue or ColumnFlag)
        {
            e.CellStyle!.ForeColor = appearance.Foreground;
            e.CellStyle.BackColor = appearance.Background;

            if (appearance.Bold)
                e.CellStyle.Font = new Font(e.CellStyle.Font ?? UiTheme.Monospace, FontStyle.Bold);
        }

        if (row.IsHeading)
        {
            e.CellStyle!.BackColor = UiTheme.SurfaceSunken;
            e.CellStyle.Font = new Font(UiTheme.Body, FontStyle.Bold | FontStyle.Italic);
        }
        else if (row.IsReadOnly && _grid.Columns[e.ColumnIndex].Name == ColumnValue)
        {
            // Calculated: visibly derived rather than simply refusing input.
            e.CellStyle!.BackColor = UiTheme.SurfaceSunken;
            e.CellStyle.ForeColor = UiTheme.TextSecondary;
        }
    }

    protected override void ApplyTexts()
    {
        _scanLabel.Text = $"{T("Order.AccessionNumber")} / {T("Sample.Barcode")}";
        _load.Text = T("Action.Open");
        _refreshWorklist.Text = T("Action.Refresh");
        _worklistTitle.Text = T("ResultEntry.Worklist", _pending.Count);
        if (_worklist.Columns.Count > 0)
        {
            _worklist.Column("accession").HeaderText = T("Order.AccessionNumber");
            _worklist.Column("patient").HeaderText = T("Patient.Title");
            _worklist.Column("pending").HeaderText = T("ResultEntry.PendingShort");
            _worklist.Column("received").HeaderText = T("ResultEntry.Received");
            _worklist.Column("departments").HeaderText = T("ResultEntry.Departments");
        }
        _benchSheet.Text = T("BenchSheet.Open");
        _save.Text = T("Action.Save");
        _validate.Text = T("Validation.Technical");

        if (_grid.Columns.Count == 0) return;

        _grid.Column(ColumnParameter).HeaderText = T("Result.Parameter");
        _grid.Column(ColumnValue).HeaderText = T("Result.Value");
        _grid.Column(ColumnFlag).HeaderText = T("Result.Flags");
        _grid.Column(ColumnUnit).HeaderText = T("Result.Unit");
        _grid.Column(ColumnRange).HeaderText = T("Result.ReferenceRange");
        _grid.Column(ColumnPrevious).HeaderText = T("Result.Previous");
        _grid.Column(ColumnComment).HeaderText = T("Result.TechnicalComment");

        if (_sheet is not null) RenderSheet(_sheet);
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        await LoadWorklistAsync(ct);

        if (InitialOrderId is { } orderId)
        {
            await LoadOrderAsync(orderId, ct);
        }
        else
        {
            // The first dossier waiting (urgent first) is opened, so the screen starts on work
            // rather than on an empty grid; scanning a tube still jumps straight to it.
            if (_sheet is null && _pending.Count > 0) await LoadAsync(_pending[0].AccessionNumber);
            _scan.Focus();
        }
    }

    // ---- Loading -----------------------------------------------------------------------------------

    private async Task LoadAsync(string term, bool skipIfOpen = false)
    {
        if (string.IsNullOrWhiteSpace(term)) return;

        await _loadGate.WaitAsync();
        try
        {
            // The same click arrives twice (selection, then click); the second finds it open.
            if (skipIfOpen && _sheet?.AccessionNumber == term) return;
            await LoadCoreAsync(term);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task LoadCoreAsync(string term)
    {

        _status.ForeColor = UiTheme.TextSecondary;
        _status.Text = T("App.Loading");

        // Accepts an accession number or a tube barcode: the technician scans whichever label is
        // in front of them and should not have to know which identifier it carries.
        var resolved = await _entry.ResolveOrderAsync(term.Trim());

        if (resolved.IsFailure)
        {
            _status.ForeColor = UiTheme.Critical;
            _status.Text = Describe(resolved);
            _scan.SelectAll();
            _scan.Focus();
            return;
        }

        await LoadOrderAsync(resolved.Value, CancellationToken.None);
    }

    private async Task LoadOrderAsync(Guid orderId, CancellationToken ct)
    {
        try
        {
            var sheet = await _entry.OpenAsync(orderId, ct: ct);

            if (sheet.IsFailure)
            {
                _status.ForeColor = UiTheme.Critical;
                _status.Text = Describe(sheet);
                return;
            }

            _sheet = sheet.Value;
            RenderSheet(_sheet);
            RenderWorklist();

            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = string.Empty;
        }
        catch (OperationCanceledException)
        {
            // Navigated away mid-load.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading order {OrderId} for entry failed.", orderId);
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private void RenderSheet(ResultEntrySheet sheet)
    {
        _rowsById.Clear();

        _patientBar.Visible = true;
        _patientName.Text = sheet.PatientName;

        var demographics = new List<string> { T($"Gender.{sheet.Gender}") };
        if (sheet.AgeYears is { } age) demographics.Add($"{age} {T("Report.Years")}");
        if (sheet.DateOfBirth is { } dob) demographics.Add(dob.ToString("dd/MM/yyyy"));
        if (sheet.IsPregnant) demographics.Add(T("Patient.IsPregnant"));

        _patientDetail.Text = string.Join("   ·   ", demographics);

        var orderInfo = new List<string> { sheet.AccessionNumber };
        if (sheet.Priority != Domain.Orders.OrderPriority.Routine)
            orderInfo.Add(T($"Priority.{sheet.Priority}").ToUpperInvariant());
        if (sheet.DueAt is { } due) orderInfo.Add($"{T("Order.DueAt")} {due:dd/MM HH:mm}");

        _orderDetail.Text = string.Join("   ·   ", orderInfo);
        _orderDetail.ForeColor = sheet.Priority != Domain.Orders.OrderPriority.Routine
            ? UiTheme.Critical
            : UiTheme.Accent;

        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var row in sheet.Rows)
        {
            _rowsById[row.TestResultId] = row;

            var index = _grid.Rows.Add(
                Indent(row),
                row.IsHeading ? string.Empty : row.CurrentValue ?? string.Empty,
                UiTheme.FlagGlyphs(row.Flags) is { Length: > 0 } glyphs
                    ? glyphs
                    : UiTheme.ForInterpretation(row.Interpretation).Glyph,
                row.UnitSymbol ?? string.Empty,
                row.ReferenceRangeText ?? string.Empty,
                FormatPrevious(row),
                row.TechnicalComment ?? string.Empty);

            var gridRow = _grid.Rows[index];
            gridRow.Tag = row.TestResultId;

            var locked = row.IsReadOnly || row.IsHeading;
            gridRow.Cells[ColumnValue].ReadOnly = locked;
            gridRow.Cells[ColumnComment].ReadOnly = row.IsHeading;

            if (row.SampleDefects != Domain.Samples.SampleDefect.None)
            {
                gridRow.Cells[ColumnParameter].ToolTipText =
                    $"{T("Sample.Defects")} : {row.SampleDefects}";
            }
        }

        _grid.ResumeLayout(performLayout: true);

        // Technical validation only makes sense once something has been entered.
        _validate.Enabled = sheet.Rows.Any(r => r.Status == TestResultStatus.Entered);

        FocusFirstEditableCell();
    }

    private string Indent(ResultEntryRow row) =>
        new string(' ', row.IndentLevel * 4) + row.ParameterName.For(Localizer.CurrentLanguage) +
        (row.IsCalculated ? "  ƒ" : string.Empty);

    private static string FormatPrevious(ResultEntryRow row) =>
        row.PreviousValue is { } value
            ? $"{value:N2}" + (row.PreviousDate is { } date ? $"  {date:dd/MM/yy}" : string.Empty)
            : string.Empty;

    private void FocusFirstEditableCell()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Cells[ColumnValue].ReadOnly) continue;
            if (!string.IsNullOrWhiteSpace(row.Cells[ColumnValue].Value?.ToString())) continue;

            _grid.CurrentCell = row.Cells[ColumnValue];
            _grid.BeginEdit(selectAll: true);
            return;
        }

        _scan.Focus();
    }

    // ---- Bench worksheet ---------------------------------------------------------------------------

    private async Task PrintBenchSheetAsync()
    {
        var title = T("BenchSheet.Title");

        _benchSheet.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var options = await _benchSheets.GetOptionsAsync();

            if (options.IsFailure)
            {
                _dialogs.ShowFailure(options, title);
                return;
            }

            Cursor = Cursors.Default;

            using var dialog = new BenchWorksheetDialog(Localizer, _dialogs, options.Value,
                                                        _lastBenchDepartmentId);

            if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

            var request = dialog.Request;
            _lastBenchDepartmentId = request.DepartmentId;

            Cursor = Cursors.WaitCursor;

            var worksheet = await _benchSheets.GetWorksheetAsync(request);

            if (worksheet.IsFailure)
            {
                _dialogs.ShowFailure(worksheet, title);
                return;
            }

            var language = Localizer.CurrentLanguage;

            // QuestPDF lays out synchronously; off the UI thread so a long backlog does not freeze
            // the screen while it renders.
            var bytes = await Task.Run(() => _benchSheetRenderer.Render(worksheet.Value, language));

            var opened = await _documents.OpenAsync(bytes, "pdf");

            if (opened.IsFailure)
            {
                _dialogs.ShowFailure(opened, title);
                return;
            }

            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = $"{title} — {T("Print.RowCount", worksheet.Value.Lines.Count)}";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Printing the bench worksheet failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), title);
        }
        finally
        {
            Cursor = Cursors.Default;
            _benchSheet.Enabled = true;
            _scan.Focus();
        }
    }

    // ---- Submission ----------------------------------------------------------------------------

    private async Task SubmitAsync()
    {
        if (_sheet is null) return;

        _grid.EndEdit();

        var entries = new List<ResultEntry>();

        foreach (DataGridViewRow gridRow in _grid.Rows)
        {
            if (gridRow.Tag is not Guid id) continue;
            if (!_rowsById.TryGetValue(id, out var row)) continue;
            if (row.IsHeading || row.IsReadOnly) continue;

            var typed = gridRow.Cells[ColumnValue].Value?.ToString()?.Trim() ?? string.Empty;
            var comment = gridRow.Cells[ColumnComment].Value?.ToString()?.Trim();

            // Only send what actually changed: resubmitting an unchanged value would stamp a new
            // entry time and lose the original one.
            var unchanged = string.Equals(typed, row.CurrentValue?.Trim() ?? string.Empty,
                                          StringComparison.Ordinal)
                            && string.Equals(comment ?? string.Empty,
                                             row.TechnicalComment ?? string.Empty,
                                             StringComparison.Ordinal);

            if (unchanged || typed.Length == 0) continue;

            entries.Add(new ResultEntry(id, typed, comment,
                                        PrintTechnicalComment: !string.IsNullOrWhiteSpace(comment)));
        }

        if (entries.Count == 0)
        {
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Message.NothingSelected");
            return;
        }

        _save.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var outcome = await _entry.SubmitAsync(_sheet.LabOrderId, entries);

            if (outcome.IsFailure)
            {
                _dialogs.ShowFailure(outcome);
                return;
            }

            var result = outcome.Value;

            // Reload: calculated parameters, interpretations and flags are all recomputed server
            // side, and showing the pre-save grid would misrepresent what was stored.
            await LoadOrderAsync(_sheet.LabOrderId, CancellationToken.None);
            await LoadWorklistAsync();

            if (result.CriticalFindings.Count > 0) ShowCriticalFindings(result);

            if (result.ReflexSuggestions.Count > 0) ShowReflexSuggestions(result);

            _status.ForeColor = result.Rejected.Count > 0 ? UiTheme.Abnormal : UiTheme.Normal;
            _status.Text = result.Rejected.Count > 0
                ? $"{result.Accepted} {T("Message.Saved")} — {Localizer.Describe(result.Rejected)}"
                : $"{result.Accepted} · {T("Message.Saved")}";
        }
        finally
        {
            Cursor = Cursors.Default;
            _save.Enabled = true;
        }
    }

    /// <summary>
    /// A panic value is announced as a modal, not as a status line. It obliges the laboratory to
    /// telephone the prescriber before the result can be released, and a message that can be
    /// missed is not an obligation anybody will meet.
    /// </summary>
    private void ShowCriticalFindings(ResultEntryOutcome outcome)
    {
        var lines = outcome.CriticalFindings
            .Select(f => $"• {f.ParameterName} : {f.Value}   ({T($"Interpretation.{f.Interpretation}")})");

        _dialogs.Warn(
            $"{T("Dashboard.CriticalPending")}\n\n{string.Join('\n', lines)}\n\n" +
            T("Result_CriticalMustBeCommunicated"),
            T("Nav.CriticalResults"));
    }

    private void ShowReflexSuggestions(ResultEntryOutcome outcome)
    {
        var automatic = outcome.ReflexSuggestions.Where(s => s.AddAutomatically).ToList();
        var proposed = outcome.ReflexSuggestions.Where(s => !s.AddAutomatically).ToList();

        if (proposed.Count == 0 && automatic.Count == 0) return;

        var lines = outcome.ReflexSuggestions
            .Select(s => $"• {s.Description.For(Localizer.CurrentLanguage)}");

        _dialogs.Info($"{T("Catalog.ReflexRules")}\n\n{string.Join('\n', lines)}",
                      T("Catalog.ReflexRules"));
    }

    private async Task TechnicallyValidateAsync()
    {
        if (_sheet is null) return;

        if (!_dialogs.Confirm(T("Validation.Technical"))) return;

        _validate.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var result = await _entry.TechnicallyValidateAsync(_sheet.LabOrderId, []);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            await LoadOrderAsync(_sheet.LabOrderId, CancellationToken.None);
            await LoadWorklistAsync();

            _status.ForeColor = UiTheme.Normal;
            _status.Text = T("Message.OperationSucceeded");
        }
        finally
        {
            Cursor = Cursors.Default;
            _validate.Enabled = true;
        }
    }
}
