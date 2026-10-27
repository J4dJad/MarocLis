using System.ComponentModel;
using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Results;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
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
/// <item><b>Flags are words and glyphs in their own column</b>, not colour alone — colour-blind
/// operators and monochrome screens both exist.</item>
/// <item><b>The fiche de paillasse is printed from here</b>, because this is where the values on it
/// come back to be keyed in: the technician prints the bench's pending work, measures, writes, and
/// returns to the same screen with the sheet in hand.</item>
/// </list>
/// <para>
/// Laid out as the laboratory's « LIS · A Fluent clair » mock-up: the dossiers waiting on the left
/// with a sector filter, the patient and the results as cards on the right, the actions on the
/// selected line in a "more actions" menu, and the primary action last in the bottom bar.
/// </para>
/// </summary>
public sealed class ResultEntryView : LocalizedView
{
    private const string ColumnParameter = "parameter";
    private const string ColumnValue = "value";
    private const string ColumnFlag = "flag";
    private const string ColumnUnit = "unit";
    private const string ColumnRange = "range";
    private const string ColumnPrevious = "previous";
    private const string ColumnOrigin = "origin";
    private const string ColumnComment = "comment";

    /// <summary>The shell's id for the save shortcut, to show the key the user actually has.</summary>
    private const string SaveShortcutId = "action.save";

    private const int WorklistWidth = 280;
    private const int WorklistRowHeight = 58;
    private const int SectionRowHeight = 26;

    private readonly ResultEntryService _entry;
    private readonly BenchWorksheetService _benchSheets;
    private readonly ResultEntryContextService _context;
    private readonly IBenchWorksheetRenderer _benchSheetRenderer;
    private readonly IDocumentOutput _documents;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<ResultEntryView>? _logger;

    // ---- worklist: the dossiers with results to enter (left panel) ----
    private readonly Label _worklistTitle = new();
    private readonly Button _refreshWorklist = UiTheme.SecondaryButton(string.Empty);
    private readonly TextBox _scan = UiTheme.Input(160);
    private readonly Button _load = UiTheme.SecondaryButton(string.Empty);
    private readonly FlowLayoutPanel _sectorChips = new();
    private readonly DataGridView _worklist = UiTheme.Grid();
    private readonly Button _benchSheet = UiTheme.SecondaryButton(string.Empty);
    private readonly ToolTip _tips = new();
    private IReadOnlyList<PendingEntryOrder> _pending = [];
    private Dictionary<string, ResultEntryProgress> _progress = [];
    private IReadOnlyList<ResultEntrySector> _sectors = [];
    private string? _sectorFilter;
    private bool _renderingWorklist;

    // ---- the open dossier (right) ----
    private readonly Panel _patientCard = new();
    private readonly Label _patientName = new();
    private readonly Label _patientDetail = new();
    private readonly PillLabel _priorityPill = new();
    private readonly PillLabel _duePill = new();
    private readonly PillLabel _progressPill = new();
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly FlowLayoutPanel _keyHints = new();
    private readonly Label _status = new();
    private readonly Panel _statusStrip = new();

    // ---- bottom bar ----
    private readonly Button _more = UiTheme.SecondaryButton(string.Empty);
    private readonly ContextMenuStrip _moreMenu = new();
    private readonly ToolStripMenuItem _notDone = new();
    private readonly ToolStripMenuItem _clearValue = new();
    private readonly ToolStripMenuItem _correct = new();
    private readonly ToolStripMenuItem _comment = new();
    private readonly Button _history = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _remaining = new();
    private readonly Button _validate = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _save = UiTheme.PrimaryButton(string.Empty);
    private string _saveShortcut = string.Empty;

    private ResultEntrySheet? _sheet;
    private readonly Dictionary<Guid, ResultEntryRow> _rowsById = [];
    private Dictionary<Guid, ResultEntryOrigin> _origins = [];

    /// <summary>The department last printed for, offered again: a technician prints for their own bench.</summary>
    private Guid? _lastBenchDepartmentId;

    public ResultEntryView(ResultEntryService entry, BenchWorksheetService benchSheets,
                           ResultEntryContextService context,
                           IBenchWorksheetRenderer benchSheetRenderer, IDocumentOutput documents,
                           IUserDialogs dialogs, ILocalizer localizer, ICultureManager cultureManager,
                           ILogger<ResultEntryView>? logger = null)
        : base(localizer, cultureManager)
    {
        _entry = entry;
        _benchSheets = benchSheets;
        _context = context;
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

    private bool IsRightToLeft => RightToLeft == RightToLeft.Yes;

    // ---- Layout -----------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(0);

        // Two columns, the dossiers then the entry. A table rather than docking: it mirrors by
        // itself in Arabic, the dossiers going to the right.
        var columns = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
            // White, so the panels inside are not taken for cards on the work surface; the entry
            // side sets its own surface.
            BackColor = UiTheme.SurfaceRaised
        };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, WorklistWidth));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        columns.Controls.Add(BuildWorklistPanel(), 0, 0);
        columns.Controls.Add(BuildEntryPanel(), 1, 0);

        Controls.Add(columns);
    }

    /// <summary>« À saisir · N », the scan box, the sector chips, the dossiers, the bench sheet.</summary>
    private Control BuildWorklistPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 1, 0),
            ColumnCount = 1,
            RowCount = 5,
            BackColor = UiTheme.SurfaceRaised
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // The line between the dossiers and the entry, on whichever side the entry is.
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            var x = IsRightToLeft ? 0 : panel.ClientSize.Width - 1;
            e.Graphics.DrawLine(pen, x, 0, x, panel.ClientSize.Height);
        };
        panel.RightToLeftChanged += (_, _) =>
        {
            panel.Padding = IsRightToLeft ? new Padding(1, 0, 0, 0) : new Padding(0, 0, 1, 0);
            panel.Invalidate();
        };

        // --- title and refresh --------------------------------------------------------------------

        var titleRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(14, 12, 14, 6),
            BackColor = UiTheme.SurfaceRaised
        };
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _worklistTitle.Font = EntryPaint.Resized(UiTheme.SectionTitle, 12.75f);
        _worklistTitle.ForeColor = UiTheme.TextPrimary;
        _worklistTitle.AutoSize = true;
        _worklistTitle.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _worklistTitle.Margin = new Padding(0, 2, 0, 0);

        // Icon only: the screen refreshes on F5 as well, and the panel is narrow.
        _refreshWorklist.Size = new Size(30, 28);
        _refreshWorklist.Padding = new Padding(0);
        _refreshWorklist.Margin = new Padding(0);
        _refreshWorklist.TabStop = false;
        ButtonIcons.Set(_refreshWorklist, Glyph.RefreshCw, always: true);

        titleRow.Controls.Add(_worklistTitle, 0, 0);
        titleRow.Controls.Add(_refreshWorklist, 1, 0);

        // --- scan box -------------------------------------------------------------------------------

        var scanRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(14, 0, 14, 8),
            BackColor = UiTheme.SurfaceRaised
        };
        scanRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scanRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // An accession number and a barcode are Latin tokens; the field stays LTR in Arabic.
        _scan.Tag = UiTags.LatinValue;
        _scan.Font = UiTheme.Monospace;
        _scan.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _scan.Margin = new Padding(0, 1, 6, 0);

        _load.AutoSize = true;
        _load.MinimumSize = new Size(0, UiTheme.ButtonHeight - 2);
        _load.Height = UiTheme.ButtonHeight - 2;
        _load.Margin = new Padding(0);

        scanRow.Controls.Add(_scan, 0, 0);
        scanRow.Controls.Add(_load, 1, 0);

        // --- sector chips -------------------------------------------------------------------------

        _sectorChips.Dock = DockStyle.Fill;
        _sectorChips.AutoSize = true;
        _sectorChips.WrapContents = true;
        _sectorChips.Margin = new Padding(14, 0, 14, 6);
        _sectorChips.BackColor = UiTheme.SurfaceRaised;

        // --- the dossiers -------------------------------------------------------------------------

        BuildWorklistColumns();
        _worklist.ColumnHeadersVisible = false;
        _worklist.CellBorderStyle = DataGridViewCellBorderStyle.None;
        _worklist.RowTemplate.Height = WorklistRowHeight;
        _worklist.Margin = new Padding(0);
        _worklist.ShowCellToolTips = true;
        _worklist.CellPainting += PaintWorklistCell;

        // --- bench sheet ----------------------------------------------------------------------------

        // At the foot of the list, away from the scan box: it is used once a run, not once a tube,
        // and must not be what an Enter or a stray click lands on mid-entry.
        _benchSheet.Dock = DockStyle.Fill;
        _benchSheet.Height = UiTheme.ButtonHeight + 2;
        _benchSheet.Margin = new Padding(14, 10, 14, 14);
        _benchSheet.TabStop = false;

        panel.Controls.Add(titleRow, 0, 0);
        panel.Controls.Add(scanRow, 0, 1);
        panel.Controls.Add(_sectorChips, 0, 2);
        panel.Controls.Add(_worklist, 0, 3);
        panel.Controls.Add(_benchSheet, 0, 4);

        return panel;
    }

    /// <summary>The patient card, the results card, the status line and the action bar.</summary>
    private Control BuildEntryPanel()
    {
        var host = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = UiTheme.Surface };

        // --- action bar -----------------------------------------------------------------------------

        // In a strip of its own: a white table straight on the work surface would be drawn as a card.
        var strip = new EntryStrip { Dock = DockStyle.Bottom, Height = 56, BackColor = UiTheme.SurfaceRaised };
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
            Padding = new Padding(24, 11, 24, 11),
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        strip.Controls.Add(bar);

        // On a 1366-pixel screen the count does not fit on one line beside the four buttons: it
        // takes what is left and wraps onto two, rather than pushing a button out of the bar.
        // When even that is not left — the Arabic captions, a narrower window — the count goes on a
        // line of its own above the buttons: squeezed into the bar it slid under « Antériorités »
        // and « Tout est saisi » was not seen at all.
        var placing = false;

        void FitRemaining()
        {
            var width = strip.ClientSize.Width;
            if (width <= 0) return;

            var used = bar.Padding.Horizontal + 8 + 12
                       + new[] { _more, _history, _validate, _save }
                           .Where(b => b.Visible)
                           .Sum(b => b.PreferredSize.Width + b.Margin.Horizontal);
            var room = width - used;

            // A little slack before it comes back into the bar, so that a width on the edge does
            // not move it to and fro.
            var onOwnLine = _remaining.Parent == strip;
            var ownLine = onOwnLine ? room < 100 : room < 80;

            if (ownLine != onOwnLine)
            {
                // Moved once the layout pass that asked for it is over.
                if (placing) return;
                placing = true;
                if (strip.IsHandleCreated) strip.BeginInvoke(() => PlaceRemaining(ownLine));
                else PlaceRemaining(ownLine);
                return;
            }

            if (ownLine) return;

            var left = Math.Max(90, room);

            // In the body size when it fits on one line, else smaller, on two lines at most.
            var font = TextRenderer.MeasureText(_remaining.Text, UiTheme.Body).Width <= left ? UiTheme.Body : UiTheme.Small;
            if (_remaining.Font != font) _remaining.Font = font;
            if (_remaining.MaximumSize.Width != left) _remaining.MaximumSize = new Size(left, 0);
        }

        void PlaceRemaining(bool ownLine)
        {
            placing = false;
            if (ownLine == (_remaining.Parent == strip)) return;

            strip.SuspendLayout();
            if (ownLine)
            {
                bar.Controls.Remove(_remaining);
                _remaining.AutoSize = false;
                _remaining.MaximumSize = Size.Empty;
                _remaining.Dock = DockStyle.Top;
                _remaining.Height = 20;
                _remaining.Margin = new Padding(0);
                _remaining.Padding = new Padding(24, 4, 24, 0);
                _remaining.Font = UiTheme.Small;
                strip.Controls.Add(_remaining);
                strip.Height = 56 + _remaining.Height;
            }
            else
            {
                strip.Controls.Remove(_remaining);
                _remaining.Dock = DockStyle.None;
                _remaining.Padding = new Padding(0);
                _remaining.Margin = new Padding(8, 0, 12, 0);
                _remaining.AutoSize = true;
                _remaining.Anchor = AnchorStyles.Right;
                bar.Controls.Add(_remaining, 3, 0);
                strip.Height = 56;
            }
            strip.ResumeLayout(true);
            if (!ownLine) FitRemaining();
        }

        strip.Resize += (_, _) => FitRemaining();
        _remaining.TextChanged += (_, _) => FitRemaining();

        foreach (var button in new[] { _more, _history, _validate, _save })
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(0, 34);
            button.Height = 34;
            button.Margin = new Padding(0, 0, 8, 0);
        }

        _save.Margin = new Padding(0);

        // « Plus d'actions ▾ »: the chevron drawn after the caption, where a font may lack the glyph.
        _more.Paint += PaintMoreChevron;
        _more.RightToLeftChanged += (_, _) =>
            _more.Padding = IsRightToLeft ? new Padding(26, 0, 12, 0) : new Padding(12, 0, 26, 0);
        _more.Padding = new Padding(12, 0, 26, 0);
        _history.Enabled = false;
        _validate.Enabled = false;

        _remaining.AutoSize = true;
        _remaining.Anchor = AnchorStyles.Right;
        _remaining.TextAlign = ContentAlignment.MiddleRight;
        _remaining.Font = UiTheme.Body;
        _remaining.ForeColor = UiTheme.TextSecondary;
        _remaining.Margin = new Padding(8, 0, 12, 0);

        var spacer = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = UiTheme.SurfaceRaised };

        bar.Controls.Add(_more, 0, 0);
        bar.Controls.Add(_history, 1, 0);
        bar.Controls.Add(spacer, 2, 0);
        bar.Controls.Add(_remaining, 3, 0);
        bar.Controls.Add(_validate, 4, 0);
        bar.Controls.Add(_save, 5, 0);

        // The selected line's actions, one click further away than the everyday ones: not done,
        // clear, correct a released value, and the technical comment.
        _moreMenu.Items.AddRange([_notDone, _clearValue, _correct, new ToolStripSeparator(), _comment]);
        _moreMenu.Font = UiTheme.Body;
        _notDone.Enabled = _clearValue.Enabled = _correct.Enabled = _comment.Enabled = false;

        // --- status line ------------------------------------------------------------------------------
        // What the last action said — saved, refused, the tube not received. Shown above the bar
        // only while there is something to say.

        _statusStrip.Dock = DockStyle.Bottom;
        _statusStrip.Height = 24;
        _statusStrip.Padding = new Padding(24, 2, 24, 2);
        _statusStrip.BackColor = UiTheme.Surface;
        _statusStrip.Tag = UiTags.OwnLayout;
        _statusStrip.Visible = false;

        _status.Dock = DockStyle.Fill;
        _status.AutoSize = false;
        _status.AutoEllipsis = true;
        _status.Font = UiTheme.Small;
        _status.ForeColor = UiTheme.TextSecondary;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.TextChanged += (_, _) =>
        {
            _statusStrip.Visible = !string.IsNullOrEmpty(_status.Text);
            _tips.SetToolTip(_status, _status.Text);
        };
        _statusStrip.Controls.Add(_status);

        // --- cards ------------------------------------------------------------------------------------

        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(24, 18, 24, 14),
            Margin = new Padding(0),
            BackColor = UiTheme.Surface
        };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        cards.Controls.Add(BuildPatientCard(), 0, 0);
        cards.Controls.Add(BuildResultsCard(), 0, 1);

        host.Controls.Add(cards);
        host.Controls.Add(_statusStrip);
        host.Controls.Add(strip);

        return host;
    }

    private Control BuildPatientCard()
    {
        _patientCard.Dock = DockStyle.Fill;
        _patientCard.Height = 66;
        _patientCard.Margin = new Padding(0, 0, 0, 14);
        _patientCard.Padding = new Padding(16, 10, 16, 10);
        _patientCard.BackColor = UiTheme.SurfaceRaised;
        _patientCard.Visible = false;
        UiChrome.AsCard(_patientCard);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

        _patientName.Font = EntryPaint.Resized(UiTheme.SectionTitle, 12.75f);
        _patientName.ForeColor = UiTheme.TextPrimary;
        _patientName.AutoSize = true;
        _patientName.AutoEllipsis = true;
        _patientName.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _patientName.Margin = new Padding(0);

        _patientDetail.Font = UiTheme.Body;
        _patientDetail.ForeColor = UiTheme.TextSecondary;
        _patientDetail.AutoSize = true;
        _patientDetail.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        _patientDetail.Margin = new Padding(0, 2, 0, 0);

        foreach (var pill in new[] { _priorityPill, _duePill, _progressPill })
        {
            pill.Font = UiTheme.SmallBold;
            pill.Anchor = AnchorStyles.None;
            pill.Margin = new Padding(12, 0, 0, 0);
            pill.Visible = false;
        }

        layout.Controls.Add(_patientName, 0, 0);
        layout.Controls.Add(_patientDetail, 0, 1);
        layout.Controls.Add(_priorityPill, 1, 0);
        layout.SetRowSpan(_priorityPill, 2);
        layout.Controls.Add(_duePill, 2, 0);
        layout.SetRowSpan(_duePill, 2);
        layout.Controls.Add(_progressPill, 3, 0);
        layout.SetRowSpan(_progressPill, 2);

        _patientCard.Controls.Add(layout);
        return _patientCard;
    }

    private Control BuildResultsCard()
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(1),
            BackColor = UiTheme.SurfaceRaised
        };
        UiChrome.AsCard(card);

        BuildColumns();
        _grid.ReadOnly = false;          // only the value column is writable
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.StandardTab = false;
        _grid.MultiSelect = false;
        _grid.AllowUserToResizeColumns = true;
        _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(12, 6, 12, 6);
        _grid.DefaultCellStyle.Padding = new Padding(12, 0, 12, 0);

        // The keys the grid actually answers to, under it: a hint strip in the work-surface colour.
        _keyHints.Dock = DockStyle.Bottom;
        _keyHints.Height = 34;
        _keyHints.WrapContents = false;
        _keyHints.AutoScroll = false;
        _keyHints.Padding = new Padding(10, 9, 10, 0);
        _keyHints.BackColor = UiTheme.Surface;
        _keyHints.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, 0, 0, _keyHints.Width, 0);
        };

        card.Controls.Add(_grid);
        card.Controls.Add(_keyHints);
        return card;
    }

    private void BuildWorklistColumns()
    {
        _worklist.Columns.Clear();

        // One painted column: name and status on the first line, file, analyses and due time on
        // the second. The cell's text is what a print of the list or a screen reader gets.
        _worklist.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "dossier",
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
    }

    private void BuildColumns()
    {
        _grid.Columns.Clear();

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnParameter,
            FillWeight = 30,
            ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = EntryPaint.Resized(UiTheme.BodyBold, UiTheme.GridBody.Size)
            },
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnValue,
            FillWeight = 13,
            // As long as a text result's column.
            MaxInputLength = TestResult.TextValueMaxLength,
            // Monospaced and right-aligned: digits line up down the column, which is how a
            // technician spots the value with an extra zero. The padding is the input box drawn
            // round the value; the editor sits inside it.
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = UiTheme.Monospace,
                Alignment = DataGridViewContentAlignment.MiddleRight,
                Padding = new Padding(14, 6, 14, 6)
            },
            HeaderCell = { Style = { Alignment = DataGridViewContentAlignment.MiddleRight } },
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnFlag,
            FillWeight = 12,
            ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { Font = UiTheme.SmallBold },
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnUnit, FillWeight = 8, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { ForeColor = UiTheme.TextSecondary },
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnRange, FillWeight = 14, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = UiTheme.Monospace, ForeColor = UiTheme.TextSecondary
            },
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnPrevious, FillWeight = 12, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = UiTheme.Monospace,
                ForeColor = UiTheme.TextSecondary
            },
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnOrigin, FillWeight = 11, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { ForeColor = UiTheme.TextSecondary },
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        // The technical comment keeps its cell — typed comments are read, kept across a language
        // switch and saved from it as before — but is edited with F3 or "Plus d'actions", and shown
        // as a mark and a tooltip on the parameter, so the seven columns fit a 1366-pixel screen.
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColumnComment, Visible = false,
            // Still printed with Ctrl+P, as when it was on screen.
            Tag = ListPrinting.PrintedWhenHidden,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        // The parameter name is what the technician reads the value against: never "Polynucléaires …".
        _grid.Columns[ColumnParameter]!.MinimumWidth = 190;
        _grid.Columns[ColumnValue]!.MinimumWidth = 104;
        _grid.Columns[ColumnFlag]!.MinimumWidth = 86;
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

        // Typing narrows the list to the dossiers whose number or name contains the text; a tube
        // barcode matches none of them and is resolved on Enter as before.
        _scan.TextChanged += (_, _) => RenderWorklist();

        // Busy while saving: leaving the screen mid-save no longer disposes the context under it.
        _save.Click += async (_, _) => await RunBusyAsync(SubmitAsync);
        _validate.Click += async (_, _) => await RunBusyAsync(TechnicallyValidateAsync);

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Results.Enter, _save, _benchSheet);
        PermissionGate.Require(Permissions.Results.TechnicalValidate, _validate);

        // The antériorités of the selected parameter: the button, or a double-click on the
        // antériorité cell, which is where the eye already is when the last value raises a doubt.
        _history.Visible = PermissionGate.Allows(Permissions.Results.View);
        PermissionGate.Require(Permissions.Results.View, _history);
        _history.Click += async (_, _) => await ShowHistoryAsync();
        _grid.CurrentCellChanged += (_, _) =>
        {
            _history.Enabled = ResultHistory.IsCharted(SelectedRow());
            UpdateRowActions();
        };

        // The selected line's actions, in the "more actions" menu. Gated like the buttons they
        // were; the service checks again.
        _more.Click += (_, _) =>
        {
            UpdateRowActions();
            _moreMenu.RightToLeft = RightToLeft;
            _moreMenu.Show(_more, new Point(IsRightToLeft ? _more.Width : 0, _more.Height));
        };
        _notDone.Click += async (_, _) => await RunBusyAsync(MarkSelectedNotDoneAsync);
        _clearValue.Click += async (_, _) => await RunBusyAsync(ClearSelectedAsync);
        _correct.Click += async (_, _) => await RunBusyAsync(CorrectSelectedAsync);
        _comment.Click += (_, _) => EditSelectedComment();

        _grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (_grid.Columns[e.ColumnIndex].Name != ColumnPrevious) return;
            if (PermissionGate.IsBlocked(_history)) return;

            await ShowHistoryAsync();
        };

        // Enter commits the cell and steps down the column, which is how values are actually
        // entered. The grid's default — move right — would land in a read-only unit cell.
        _grid.KeyDown += OnGridKeyDown;
        _grid.CellFormatting += OnCellFormatting;
        _grid.CellBeginEdit += OnCellBeginEdit;
        _grid.CellPainting += PaintResultCell;
        _grid.RowPostPaint += PaintSectionRow;
        _grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && _grid.Columns[e.ColumnIndex].Name == ColumnValue)
                ShowRemaining();
        };
        _grid.EditingControlShowing += (_, e) =>
        {
            // The editor fills the input box drawn in the cell, white on the selected line's tint.
            e.Control.BackColor = UiTheme.SurfaceRaised;
            _grid.EditingPanel.BackColor = UiTheme.AccentSubtle;
        };
        _grid.EditingPanel.Paint += (_, e) =>
        {
            var panel = _grid.EditingPanel;
            var box = new Rectangle(4, 2, panel.Width - 9, panel.Height - 5);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = UiChrome.RoundedRectangle(box, 4);
            using var fill = new SolidBrush(UiTheme.SurfaceRaised);
            e.Graphics.FillPath(fill, path);
            using var pen = new Pen(UiTheme.Accent, 2);
            e.Graphics.DrawPath(pen, path);
        };
    }

    /// <summary>
    /// The grid's keys, read before the cell editor gets them: Enter and Shift+Enter move down and
    /// up the values to enter, F3 opens the technical comment of the line.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_grid.ContainsFocus)
        {
            switch (keyData)
            {
                case Keys.Enter:
                    MoveToEditable(+1);
                    return true;
                case Keys.Shift | Keys.Enter:
                    MoveToEditable(-1);
                    return true;
                case Keys.F3:
                    EditSelectedComment();
                    return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;

        e.SuppressKeyPress = true;
        MoveToEditable(e.Shift ? -1 : +1);
    }

    /// <summary>
    /// Commits the cell and moves to the next line that takes a value in <paramref name="step"/>'s
    /// direction, skipping calculated lines, headings and section rows. Past the last one, the
    /// focus goes to Enregistrer.
    /// </summary>
    private void MoveToEditable(int step)
    {
        _grid.EndEdit();

        var column = _grid.CurrentCell?.ColumnIndex ?? 0;
        var next = (_grid.CurrentCell?.RowIndex ?? -1) + step;

        while (next >= 0 && next < _grid.Rows.Count && _grid.Rows[next].Cells[ColumnValue].ReadOnly) next += step;

        if (next >= 0 && next < _grid.Rows.Count)
            _grid.CurrentCell = _grid.Rows[next].Cells[column];
        else if (step > 0)
            _save.Focus();
    }

    private void OnCellBeginEdit(object? sender, DataGridViewCellCancelEventArgs e)
    {
        if (_grid.Rows[e.RowIndex].Tag is not Guid id) return;
        if (!_rowsById.TryGetValue(id, out var row)) return;

        // A tube not received takes nothing; a validated value cannot be edited in the grid: it
        // needs the revision path, with a recorded reason (ResultEntryRules.EditRefusal).
        if (ResultEntryRules.EditRefusal(row) is { } refusal)
        {
            e.Cancel = true;
            _status.ForeColor = UiTheme.Abnormal;
            _status.Text = T(refusal, row.ParameterCode);
        }
    }

    /// <summary>
    /// Paints clinical state. Colour <em>and</em> weight <em>and</em> a word with a glyph, never
    /// colour alone.
    /// </summary>
    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
        if (_grid.Rows[e.RowIndex].Tag is not Guid id) return;
        if (!_rowsById.TryGetValue(id, out var row)) return;

        var name = _grid.Columns[e.ColumnIndex].Name;
        var appearance = UiTheme.ForInterpretation(row.Interpretation);

        if (name == ColumnValue)
        {
            e.CellStyle!.ForeColor = appearance.Foreground;

            if (appearance.Bold)
                e.CellStyle.Font = new Font(e.CellStyle.Font ?? UiTheme.Monospace, FontStyle.Bold);
        }

        // « — » for no antériorité or no origin yet, in the face of the figures beside it.
        if (name == ColumnOrigin && e.Value is "—") e.CellStyle!.Font = UiTheme.Monospace;

        // A typed technical comment is marked on the parameter, and read in its tooltip.
        if (name == ColumnParameter
            && _grid.Rows[e.RowIndex].Cells[ColumnComment].Value is string comment
            && !string.IsNullOrWhiteSpace(comment) && e.Value is string text)
        {
            e.Value = text + "  ✎";
            e.FormattingApplied = true;
        }

        if (row.IsHeading)
        {
            e.CellStyle!.BackColor = UiTheme.SurfaceSunken;
            e.CellStyle.Font = new Font(UiTheme.GridBody, FontStyle.Bold | FontStyle.Italic);
        }
        else if (row.IsReadOnly && name == ColumnValue)
        {
            // Calculated: visibly derived rather than simply refusing input.
            e.CellStyle!.BackColor = UiTheme.SurfaceSunken;
            e.CellStyle.ForeColor = UiTheme.TextSecondary;
        }
    }

    /// <summary>
    /// The value as an input box on a line that takes one, and the indicator as a pill: « ▲ Haut »
    /// in the warning pair, « ▼ Bas » in the info pair, critical in the critical pair, then the
    /// result's other flags (CIQ, Δ, ×…) as before.
    /// </summary>
    private void PaintResultCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.Graphics is null) return;
        if (_grid.Rows[e.RowIndex].Tag is not Guid id || !_rowsById.TryGetValue(id, out var row)) return;

        var name = _grid.Columns[e.ColumnIndex].Name;
        var rtl = IsRightToLeft;

        if (name == ColumnValue && !_grid.Rows[e.RowIndex].Cells[ColumnValue].ReadOnly)
        {
            e.PaintBackground(e.ClipBounds, (e.State & DataGridViewElementStates.Selected) != 0);

            var box = new Rectangle(e.CellBounds.X + 8, e.CellBounds.Y + 4,
                                    e.CellBounds.Width - 17, e.CellBounds.Height - 9);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = UiChrome.RoundedRectangle(box, 4))
            {
                using var fill = new SolidBrush(UiTheme.SurfaceRaised);
                e.Graphics.FillPath(fill, path);
                using var pen = new Pen(UiTheme.BorderStrong);
                e.Graphics.DrawPath(pen, path);
            }

            var text = e.FormattedValue?.ToString() ?? string.Empty;
            var textBounds = new Rectangle(box.X + 8, box.Y, box.Width - 16, box.Height);
            // A number reads the same in both directions: right-aligned under the header.
            TextRenderer.DrawText(e.Graphics, text, e.CellStyle?.Font ?? UiTheme.Monospace, textBounds,
                                  e.CellStyle?.ForeColor ?? UiTheme.TextPrimary,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                                  | (rtl ? TextFormatFlags.Left : TextFormatFlags.Right)
                                  | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.Handled = true;
            return;
        }

        if (name == ColumnFlag)
        {
            e.PaintBackground(e.ClipBounds, (e.State & DataGridViewElementStates.Selected) != 0);

            var x = e.CellBounds.X + 10;
            if (Indicator(row.Interpretation) is { } indicator)
            {
                var size = EntryPaint.MeasurePill(indicator.Text, UiTheme.SmallBold, 8);
                var pill = EntryPaint.Mirror(
                    new Rectangle(x, e.CellBounds.Y + (e.CellBounds.Height - size.Height) / 2,
                                  Math.Min(size.Width, e.CellBounds.Width - 14), size.Height),
                    e.CellBounds, rtl);
                EntryPaint.DrawPill(e.Graphics, pill, indicator.Text, UiTheme.SmallBold, indicator.Colours, rtl);
                x += size.Width + 6;
            }

            var glyphs = UiTheme.FlagGlyphs(row.Flags);
            if (glyphs.Length > 0 && x < e.CellBounds.Right - 8)
            {
                var rest = EntryPaint.Mirror(new Rectangle(x, e.CellBounds.Y, e.CellBounds.Right - x - 4,
                                                           e.CellBounds.Height), e.CellBounds, rtl);
                TextRenderer.DrawText(e.Graphics, glyphs, UiTheme.MonospaceBold, rest, UiTheme.Abnormal,
                                      TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                                      | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis
                                      | (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left));
            }

            e.Handled = true;
        }
    }

    /// <summary>The indicator pill of an interpretation; null for a normal or uninterpretable value.</summary>
    private (string Text, (Color Back, Color Fore) Colours)? Indicator(ResultInterpretation interpretation) =>
        ResultEntryRules.Indicator(interpretation) is { } indicator
            ? ($"{indicator.Arrow} {T(indicator.Key)}", ValidationPaint.Colours(indicator.Tone))
            : null;

    /// <summary>A section row, « CODE · SECTEUR », drawn across the whole line.</summary>
    private void PaintSectionRow(object? sender, DataGridViewRowPostPaintEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not EntrySection section) return;

        var bounds = new Rectangle(e.RowBounds.X, e.RowBounds.Y, _grid.ClientSize.Width, e.RowBounds.Height);

        using (var fill = new SolidBrush(EntryPaint.SectionBackground))
            e.Graphics.FillRectangle(fill, bounds);
        using (var pen = new Pen(UiTheme.Border))
            e.Graphics.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);

        var text = new Rectangle(bounds.X + 12, bounds.Y, bounds.Width - 24, bounds.Height);
        TextRenderer.DrawText(e.Graphics, section.Caption, UiTheme.SmallBold, text, UiTheme.TextSecondary,
                              TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                              | (IsRightToLeft ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left));
    }

    /// <summary>
    /// One dossier: NAME and the status pill (« Retard » once past due, else « saisis / total »),
    /// then the file number, the analyses waiting and the due time, in critical ink when late.
    /// </summary>
    private void PaintWorklistCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.Graphics is null) return;
        if (_worklist.Rows[e.RowIndex].Tag is not string accession) return;

        var order = _pending.FirstOrDefault(p => p.AccessionNumber == accession);
        if (order is null) return;

        var graphics = e.Graphics;
        var bounds = e.CellBounds;
        var rtl = IsRightToLeft;
        var selected = (e.State & DataGridViewElementStates.Selected) != 0;

        using (var fill = new SolidBrush(selected ? UiTheme.AccentSubtle : UiTheme.SurfaceRaised))
            graphics.FillRectangle(fill, bounds);
        using (var line = new Pen(UiTheme.Border))
            graphics.DrawLine(line, bounds.Left, bounds.Top, bounds.Right, bounds.Top);
        if (selected)
        {
            using var accent = new SolidBrush(UiTheme.Accent);
            graphics.FillRectangle(accent, EntryPaint.Mirror(new Rectangle(bounds.X, bounds.Y, 3, bounds.Height),
                                                             bounds, rtl));
        }

        var now = DateTimeOffset.Now;
        var pill = ResultEntryRules.WorklistPill(order, _progress.GetValueOrDefault(accession), now);
        var late = ResultEntryRules.IsLate(order.DueAt, now);
        const int pad = 14;
        var inner = new Rectangle(bounds.X + pad, bounds.Y + 9, bounds.Width - 2 * pad, bounds.Height - 18);
        var alignment = rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left;
        var opposite = rtl ? TextFormatFlags.Left | TextFormatFlags.RightToLeft : TextFormatFlags.Right;
        const TextFormatFlags single = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                                       | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

        // Line 1: the name, and the pill at the far end.
        // Every value in: the file stays listed for its technical validation, and says so.
        var progress = _progress.GetValueOrDefault(accession);
        var complete = pill == EntryWorklistPill.ReadyToValidate;
        var pillText = pill switch
        {
            EntryWorklistPill.ReadyToValidate => T("ResultEntry.ReadyToValidate"),
            EntryWorklistPill.Late => T("ResultEntry.Late"),
            EntryWorklistPill.Progress => $"{progress!.Entered} / {progress.Total}",
            _ => string.Empty
        };
        var pillSize = pillText.Length == 0 ? Size.Empty : EntryPaint.MeasurePill(pillText, UiTheme.SmallBold, 8);
        var topLine = new Rectangle(inner.X, inner.Y, inner.Width, 20);

        if (pillSize.Width > 0)
        {
            var pillBounds = EntryPaint.Mirror(new Rectangle(topLine.Right - pillSize.Width, topLine.Y + 1,
                                                             pillSize.Width, pillSize.Height), bounds, rtl);
            EntryPaint.DrawPill(graphics, pillBounds, pillText, UiTheme.SmallBold,
                                complete ? EntryPaint.Info : late ? EntryPaint.Critical : EntryPaint.Waiting, rtl);
        }

        var nameBounds = EntryPaint.Mirror(new Rectangle(topLine.X, topLine.Y, topLine.Width - pillSize.Width - 8, 20),
                                           bounds, rtl);
        TextRenderer.DrawText(graphics, order.PatientName, UiTheme.BodyBold, nameBounds,
                              order.IsUrgent ? UiTheme.Critical : UiTheme.TextPrimary,
                              single | TextFormatFlags.VerticalCenter | alignment);

        // Line 2: file number · analyses, and the due time.
        var bottom = new Rectangle(inner.X, inner.Y + 22, inner.Width, 18);
        var dueText = order.DueAt is { } dueAt
            ? dueAt.ToLocalTime().Date == now.ToLocalTime().Date
                ? dueAt.ToLocalTime().ToString("HH:mm")
                : dueAt.ToLocalTime().ToString("dd/MM HH:mm")
            : string.Empty;
        var dueWidth = dueText.Length == 0
            ? 0
            : TextRenderer.MeasureText(dueText, UiTheme.Monospace, Size.Empty, TextFormatFlags.NoPadding).Width;

        TextRenderer.DrawText(graphics, dueText, UiTheme.Monospace, EntryPaint.Mirror(bottom, bounds, rtl),
                              late ? UiTheme.CriticalInk : UiTheme.TextSecondary,
                              single | TextFormatFlags.VerticalCenter | opposite);

        var fileWidth = TextRenderer.MeasureText(accession, UiTheme.Monospace, Size.Empty, TextFormatFlags.NoPadding).Width;
        var fileBounds = new Rectangle(bottom.X, bottom.Y, Math.Min(fileWidth, bottom.Width - dueWidth - 8), bottom.Height);
        TextRenderer.DrawText(graphics, accession, UiTheme.Monospace, EntryPaint.Mirror(fileBounds, bounds, rtl),
                              UiTheme.TextSecondary, single | TextFormatFlags.VerticalCenter | alignment);

        var count = order.PendingAnalyses == 1
            ? T("ResultEntry.AnalysisOne")
            : T("ResultEntry.AnalysisMany", order.PendingAnalyses);
        var countBounds = new Rectangle(fileBounds.Right, bottom.Y,
                                        Math.Max(0, bottom.Width - fileBounds.Width - dueWidth - 8), bottom.Height);
        TextRenderer.DrawText(graphics, $" · {count}", UiTheme.Small, EntryPaint.Mirror(countBounds, bounds, rtl),
                              UiTheme.TextSecondary, single | TextFormatFlags.VerticalCenter | alignment);

        e.Handled = true;
    }

    protected override void ApplyTexts()
    {
        _scan.PlaceholderText = T("ResultEntry.ScanPlaceholder");
        _tips.SetToolTip(_scan, $"{T("Order.AccessionNumber")} / {T("Sample.Barcode")}");
        _load.Text = T("Action.Open");
        _tips.SetToolTip(_refreshWorklist, T("Action.Refresh"));
        _refreshWorklist.AccessibleName = T("Action.Refresh");
        _worklistTitle.Text = T("ResultEntry.WorklistCount", _pending.Count);
        if (_worklist.Columns.Count > 0)
            _worklist.Column("dossier").HeaderText = T("ResultEntry.Worklist", _pending.Count);
        _benchSheet.Text = T("BenchSheet.Open");
        _save.Text = T("Action.Save");
        _validate.Text = T("Validation.Technical");
        _history.Text = T("Result.History");
        _more.Text = T("ResultEntry.MoreActions");
        _notDone.Text = T("ResultEntry.MarkNotDone");
        _clearValue.Text = T("ResultEntry.ClearValue");
        _correct.Text = T("ResultEntry.CorrectReleased");
        _comment.Text = $"{T("Result.TechnicalComment")}…";
        _comment.ShortcutKeyDisplayString = "F3";

        ShowSaveShortcut();
        RenderSectorChips();
        RenderKeyHints();
        ShowRemaining();

        if (_grid.Columns.Count == 0) return;

        _grid.Column(ColumnParameter).HeaderText = T("Result.Parameter");
        _grid.Column(ColumnValue).HeaderText = T("Result.Value");
        _grid.Column(ColumnFlag).HeaderText = T("ResultEntry.Indicator");
        _grid.Column(ColumnUnit).HeaderText = T("Result.Unit");
        _grid.Column(ColumnRange).HeaderText = T("Result.ReferenceRange");
        _grid.Column(ColumnPrevious).HeaderText = T("Result.Previous");
        _grid.Column(ColumnOrigin).HeaderText = T("ResultEntry.Origin");
        _grid.Column(ColumnComment).HeaderText = T("Result.TechnicalComment");

        // Re-rendered for the new language with what was typed put back: a technician switching
        // language to read a name to a patient must not lose a panel of results doing it.
        if (_sheet is not null)
        {
            _grid.EndEdit();
            RenderSheet(_sheet, CaptureTyped());
        }
    }

    /// <summary>
    /// « Enregistrer » carries the key that saves, as the user has it: the shell's save shortcut,
    /// Ctrl+S unless they changed it. Drawn beside the caption rather than written into it — the
    /// shell finds the button to press by its caption.
    /// </summary>
    protected override void OnRightToLeftChanged(EventArgs e)
    {
        base.OnRightToLeftChanged(e);

        // The key drawn on « Enregistrer » moves to the other end of the button.
        ShowSaveShortcut();
    }

    private void ShowSaveShortcut()
    {
        var keys = Keys.Control | Keys.S;

        try
        {
            if (PermissionGate.User is { } user)
            {
                var shortcuts = new KeyboardShortcuts(user.UserId);
                shortcuts.Define(SaveShortcutId, keys);
                keys = shortcuts.For(SaveShortcutId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Reading the save shortcut failed.");
        }

        _saveShortcut = KeyboardShortcuts.Format(keys, Localizer);

        var width = _saveShortcut.Length == 0
            ? 0
            : TextRenderer.MeasureText(_saveShortcut, UiTheme.Mono(8.25f), Size.Empty, TextFormatFlags.NoPadding).Width + 10;
        _save.Padding = IsRightToLeft ? new Padding(14 + width, 0, 14, 0) : new Padding(14, 0, 14 + width, 0);

        _save.Paint -= PaintSaveShortcut;
        _save.Paint += PaintSaveShortcut;
        _save.Invalidate();
    }

    private void PaintSaveShortcut(object? sender, PaintEventArgs e)
    {
        if (_saveShortcut.Length == 0) return;

        var font = UiTheme.Mono(8.25f);
        var width = TextRenderer.MeasureText(_saveShortcut, font, Size.Empty, TextFormatFlags.NoPadding).Width;
        var bounds = IsRightToLeft
            ? new Rectangle(12, 0, width, _save.Height)
            : new Rectangle(_save.Width - 12 - width, 0, width, _save.Height);

        // The key at three-quarters of the caption's strength, as the design system shows it.
        var colour = _save.Enabled ? Color.FromArgb(0xC2, 0xDB, 0xDE) : UiTheme.TextMuted;
        TextRenderer.DrawText(e.Graphics, _saveShortcut, font, bounds, colour,
                              TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    private void PaintMoreChevron(object? sender, PaintEventArgs e)
    {
        var x = IsRightToLeft ? 12 : _more.Width - 20;
        var y = _more.Height / 2 - 2;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(_more.Enabled ? UiTheme.TextPrimary : UiTheme.TextMuted, 1.6f);
        e.Graphics.DrawLines(pen, [new Point(x, y), new Point(x + 4, y + 4), new Point(x + 8, y)]);
    }

    /// <summary>
    /// The keys the grid answers to — and only those: Enter, Shift+Enter, F3 and the double-click
    /// on the antériorité. The save key is on « Enregistrer » itself.
    /// </summary>
    private void RenderKeyHints()
    {
        _keyHints.SuspendLayout();
        _keyHints.Controls.Clear();

        void Hint(string key, string meaning)
        {
            _keyHints.Controls.Add(new Label
            {
                Text = key,
                AutoSize = true,
                Font = UiTheme.Mono(8.25f, FontStyle.Bold),
                ForeColor = UiTheme.TextPrimary,
                Margin = new Padding(0, 0, 4, 0),
                UseMnemonic = false
            });
            _keyHints.Controls.Add(new Label
            {
                Text = meaning,
                AutoSize = true,
                Font = UiTheme.Small,
                ForeColor = UiTheme.TextSecondary,
                Margin = new Padding(0, 1, 16, 0),
                UseMnemonic = false
            });
        }

        Hint(KeyboardShortcuts.Format(Keys.Enter, Localizer), T("ResultEntry.KeyNext"));
        Hint(KeyboardShortcuts.Format(Keys.Shift | Keys.Enter, Localizer), T("ResultEntry.KeyPrevious"));
        Hint("F3", T("ResultEntry.KeyComment"));
        Hint(T("ResultEntry.DoubleClick"), T("ResultEntry.KeyHistory"));

        _keyHints.ResumeLayout(performLayout: true);
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        await LoadWorklistAsync(ct);

        if (InitialOrderId is { } orderId)
        {
            if (!ConfirmDiscardTyped()) return;

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

    // ---- Worklist ---------------------------------------------------------------------------------

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

            // Without the right to enter results the list stays empty; the scan box still works
            // for the screens that only read.
            _pending = result.IsFailure ? [] : result.Value;

            // The progress of each dossier and the names of its sectors, beside the list. Either
            // failing leaves the list as it is, without the pill or with the sector codes.
            var progress = await _context.GetProgressAsync([.. _pending.Select(p => p.AccessionNumber)], ct);
            _progress = progress.IsSuccess
                ? progress.Value.ToDictionary(p => p.AccessionNumber, StringComparer.Ordinal)
                : [];

            var codes = _pending.SelectMany(SectorsOf).Distinct(StringComparer.Ordinal).ToList();
            var sectors = await _context.GetSectorsAsync(codes, ct);
            var named = sectors.IsSuccess ? sectors.Value : [];
            _sectors =
            [
                .. named.Where(s => codes.Contains(s.Code, StringComparer.Ordinal)),
                .. codes.Where(c => named.All(s => s.Code != c)).Order()
                        .Select(c => new ResultEntrySector(c, new MultilingualText(c)))
            ];

            if (_sectorFilter is not null && _sectors.All(s => s.Code != _sectorFilter)) _sectorFilter = null;

            RenderSectorChips();
            RenderWorklist();
        }
        catch (OperationCanceledException)
        {
            // Navigated away mid-load.
        }
        // Left while the list loaded: its grid is emptied with the screen.
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException
                                   && (IsDisposed || Disposing)) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the result entry worklist failed.");
        }
    }

    private static IEnumerable<string> SectorsOf(PendingEntryOrder order) => ResultEntryRules.SectorsOf(order);

    /// <summary>« Tous » and one chip per sector present in the list.</summary>
    private void RenderSectorChips()
    {
        _sectorChips.SuspendLayout();
        _sectorChips.Controls.Clear();

        ChipButton Chip(string text, string? value)
        {
            var chip = new ChipButton { Value = value, Text = text, Selected = _sectorFilter == value };
            chip.Click += (_, _) =>
            {
                _sectorFilter = value;
                foreach (var other in _sectorChips.Controls.OfType<ChipButton>())
                    other.Selected = other.Value == _sectorFilter;
                RenderWorklist();
            };
            return chip;
        }

        _sectorChips.Controls.Add(Chip(T("ResultEntry.AllSectors"), null));
        foreach (var sector in _sectors)
            _sectorChips.Controls.Add(Chip(sector.Name.For(Localizer.CurrentLanguage), sector.Code));

        _sectorChips.Visible = _sectors.Count > 0;
        _sectorChips.ResumeLayout(performLayout: true);
    }

    private void RenderWorklist()
    {
        var current = _sheet?.AccessionNumber;
        var term = _scan.Text.Trim();

        var shown = _pending
            .Where(o => ResultEntryRules.WorklistMatches(o, _sectorFilter, term, CultureInfo.CurrentCulture))
            .ToList();

        _renderingWorklist = true;

        try
        {
            _worklist.SuspendLayout();
            _worklist.Rows.Clear();

            foreach (var order in shown)
            {
                var index = _worklist.Rows.Add($"{order.AccessionNumber} — {order.PatientName}");

                var row = _worklist.Rows[index];
                row.Tag = order.AccessionNumber;
                row.Height = WorklistRowHeight;

                // What the two lines leave out: reception time, sectors, priority.
                var tip = new List<string>();
                if (order.IsUrgent) tip.Add(T($"Priority.{order.Priority}"));
                if (order.ReceivedAt is { } received)
                    tip.Add($"{T("ResultEntry.Received")} {received.ToLocalTime():dd/MM HH:mm}");
                if (order.DueAt is { } due) tip.Add($"{T("Order.DueAt")} {due.ToLocalTime():dd/MM HH:mm}");
                if (order.Departments.Length > 0) tip.Add($"{T("App.LabelValue", T("ResultEntry.Departments"), order.Departments)}");
                row.Cells[0].ToolTipText = string.Join("\n", tip);
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

            _worklistTitle.Text = T("ResultEntry.WorklistCount", _pending.Count);
        }
        finally
        {
            _renderingWorklist = false;
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

            // ...or, when the operator just refused to leave the current file, finds it refused.
            if (skipIfOpen && _declined is { } declined && declined.Term == term
                && Environment.TickCount64 - declined.At < 1500)
            {
                _declined = null;
                RenderWorklist();
                return;
            }

            _declined = null;

            if (!ConfirmDiscardTyped())
            {
                _declined = (term, Environment.TickCount64);

                // Back on the file still open, so the list does not claim another one is shown.
                RenderWorklist();
                return;
            }

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

        // Scanned or typed into the box: the box is emptied once the file is open, so the list
        // it narrowed shows every dossier again and the next tube goes into an empty box.
        if (_scan.Text.Trim() == term.Trim()) _scan.Clear();

        try
        {
            await LoadOrderAsync(resolved.Value, CancellationToken.None);
        }
        finally
        {
            // A load abandoned on the way (the screen reloaded, the file left) does not leave
            // « Chargement… » above a screen with no file open.
            if (!IsDisposed && _status.Text == T("App.Loading")) _status.Text = string.Empty;
        }
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

            // The sector and the origin of each line; without them the lines still show, under
            // their analysis code alone.
            var origins = await _context.GetOriginsAsync(orderId, ct);
            _origins = origins.IsSuccess ? origins.Value.ToDictionary(o => o.TestResultId) : [];

            _sheet = sheet.Value;

            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = string.Empty;

            RenderSheet(_sheet);
            RenderWorklist();
        }
        catch (OperationCanceledException)
        {
            // Navigated away mid-load.
        }
        // Left while the dossier opened: the screen's context is gone with it, and the next screen
        // showed « Erreur inattendue ».
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException
                                   && (IsDisposed || Disposing))
        {
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading order {OrderId} for entry failed.", orderId);
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
    }

    /// <summary>Set when the operator refused to leave unsaved values, to absorb the click's twin event.</summary>
    private (string Term, long At)? _declined;

    /// <summary>Values and comments typed in the grid and not yet saved, keyed by result and column.</summary>
    private TypedEdits<(Guid Id, string Column)> CaptureTyped()
    {
        var typed = new TypedEdits<(Guid, string)>();

        foreach (DataGridViewRow gridRow in _grid.Rows)
        {
            if (gridRow.Tag is not Guid id || !_rowsById.TryGetValue(id, out var row)) continue;
            if (row.IsHeading) continue;

            if (!gridRow.Cells[ColumnValue].ReadOnly)
                typed.Capture((id, ColumnValue), gridRow.Cells[ColumnValue].Value?.ToString(),
                              row.CurrentValue);

            typed.Capture((id, ColumnComment), gridRow.Cells[ColumnComment].Value?.ToString(),
                          row.TechnicalComment);
        }

        return typed;
    }

    /// <summary>Leaving the screen asks too: the sidebar and the shortcuts dropped typed results.</summary>
    /// <summary>
    /// While a save runs the grid still holds the values being saved, which differ from the stored
    /// ones: no "unsaved changes" prompt then, the navigation waits for the save to end.
    /// </summary>
    public override bool ConfirmLeave() => IsBusy || ConfirmDiscardTyped();

    /// <summary>
    /// True when nothing typed would be lost by replacing the sheet, or the operator agrees to
    /// lose it. Opening another file used to replace the grid silently.
    /// </summary>
    private bool ConfirmDiscardTyped()
    {
        if (_sheet is null) return true;

        _grid.EndEdit();

        return !ResultEntryRules.HasUnsavedTyping(GridRows(), TypedValue, TypedComment)
               || _dialogs.Confirm(T("Message.UnsavedChanges"), _sheet.AccessionNumber);
    }

    private void RenderSheet(ResultEntrySheet sheet, TypedEdits<(Guid Id, string Column)>? keep = null)
    {
        _rowsById.Clear();

        RenderPatientCard(sheet);

        _grid.SuspendLayout();
        _grid.Rows.Clear();

        string? section = null;

        foreach (var row in sheet.Rows)
        {
            _rowsById[row.TestResultId] = row;

            var origin = _origins.GetValueOrDefault(row.TestResultId);

            // One section row per analysis, « CODE · SECTEUR », as the bench reads the panel.
            var caption = origin?.DepartmentName is { } department
                ? $"{row.AnalysisCode} · {department.For(Localizer.CurrentLanguage).ToUpper(Localizer.CurrentLanguage.ToCulture())}"
                : row.AnalysisCode;
            if (caption != section)
            {
                section = caption;
                AddSectionRow(caption);
            }

            var index = _grid.Rows.Add(
                Indent(row),
                row.IsHeading ? string.Empty : row.CurrentValue ?? string.Empty,
                FlagText(row),
                row.UnitSymbol ?? string.Empty,
                row.ReferenceRangeText ?? string.Empty,
                FormatPrevious(row),
                FormatOrigin(row, origin),
                row.TechnicalComment ?? string.Empty);

            var gridRow = _grid.Rows[index];
            gridRow.Tag = row.TestResultId;

            // The analyser's own flags, on hover; the validation screen shows them as a column.
            if (!string.IsNullOrEmpty(row.InstrumentFlags))
                gridRow.Cells[ColumnFlag].ToolTipText = $"{T("App.LabelValue", T("Result.InstrumentFlags"), row.InstrumentFlags)}";

            var locked = !ResultEntryRules.TakesValue(row);
            gridRow.Cells[ColumnValue].ReadOnly = locked;
            gridRow.Cells[ColumnComment].ReadOnly = !ResultEntryRules.TakesComment(row);

            if (row.IsAwaitingSample)
                gridRow.Cells[ColumnValue].ToolTipText = T(ResultEntryRules.EditRefusal(row)!, row.ParameterCode);

            if (keep is not null)
            {
                if (!locked && keep.TryGet((row.TestResultId, ColumnValue), out var value))
                    gridRow.Cells[ColumnValue].Value = value;

                if (keep.TryGet((row.TestResultId, ColumnComment), out var comment))
                    gridRow.Cells[ColumnComment].Value = comment;
            }

            ShowParameterTip(gridRow, row);
        }

        _grid.ResumeLayout(performLayout: true);

        // Technical validation only makes sense once something has been entered.
        _validate.Enabled = ResultEntryRules.CanTechnicallyValidate(sheet.Rows);
        _history.Enabled = ResultHistory.IsCharted(SelectedRow());
        UpdateRowActions();
        ShowRemaining();

        // Said once on opening, rather than discovered row by row as cells refuse the cursor.
        if (ResultEntryRules.AwaitingNotice(sheet.Rows, T) is { } awaiting)
        {
            _status.ForeColor = UiTheme.Abnormal;
            _status.Text = awaiting;
        }

        FocusFirstEditableCell();
    }

    private void AddSectionRow(string caption)
    {
        var index = _grid.Rows.Add(caption);
        var gridRow = _grid.Rows[index];
        gridRow.Tag = new EntrySection(caption);
        gridRow.Height = SectionRowHeight;
        gridRow.ReadOnly = true;
    }

    /// <summary>The sample's defects and the typed technical comment, on the parameter's tooltip.</summary>
    private void ShowParameterTip(DataGridViewRow gridRow, ResultEntryRow row)
    {
        var tip = new List<string>();

        if (row.SampleDefects != Domain.Samples.SampleDefect.None)
            tip.Add($"{T("App.LabelValue", T("Sample.Defects"), row.SampleDefects)}");

        if (gridRow.Cells[ColumnComment].Value is string comment && !string.IsNullOrWhiteSpace(comment))
            tip.Add($"{T("App.LabelValue", T("Result.TechnicalComment"), comment)}");

        gridRow.Cells[ColumnParameter].ToolTipText = string.Join("\n", tip);
    }

    /// <summary>Name, sex · age · birth date · file, and the pills: priority, due time, progress.</summary>
    private void RenderPatientCard(ResultEntrySheet sheet)
    {
        _patientCard.Visible = true;
        _patientName.Text = sheet.PatientName;

        var demographics = new List<string> { T($"Gender.{sheet.Gender}") };
        if (sheet.AgeYears is { } age) demographics.Add($"{age} {T("Report.Years")}");
        if (sheet.DateOfBirth is { } dob)
            demographics.Add(T(sheet.Gender == Domain.Patients.Gender.Female ? "ResultEntry.BornOnFemale" : "ResultEntry.BornOn",
                               dob.ToString("dd/MM/yyyy")));
        demographics.Add(sheet.AccessionNumber);
        if (sheet.IsPregnant) demographics.Add(T("Patient.IsPregnant"));

        _patientDetail.Text = string.Join("  ·  ", demographics);

        if (sheet.Priority != Domain.Orders.OrderPriority.Routine)
            _priorityPill.Show(T($"Priority.{sheet.Priority}").ToUpperInvariant(), EntryPaint.Critical);
        else
            _priorityPill.Show(string.Empty, EntryPaint.Waiting);

        if (sheet.DueAt is { } due)
        {
            var at = due.ToLocalTime().ToString("dd/MM HH:mm");
            if (ResultEntryRules.IsLate(due, DateTimeOffset.Now))
                _duePill.Show(T("ResultEntry.Overdue", at), EntryPaint.Critical);
            else
                _duePill.Show($"{T("Order.DueAt")} · {at}", EntryPaint.Waiting);
        }
        else
        {
            _duePill.Show(string.Empty, EntryPaint.Waiting);
        }

        var (entered, owed) = ResultEntryRules.Progress(sheet.Rows);
        _progressPill.Show(owed == 0 ? string.Empty : T("ResultEntry.Progress", entered, owed), EntryPaint.Info);
    }

    /// <summary>
    /// « N valeurs restent à saisir »: the lines still awaited whose cell is still empty, so the
    /// count falls as values are typed, before they are saved.
    /// </summary>
    private void ShowRemaining()
    {
        if (_sheet is null)
        {
            _remaining.Text = string.Empty;
            return;
        }

        var left = ResultEntryRules.RemainingToEnter(GridRows(), TypedValue);

        _remaining.Text = left switch
        {
            0 => T("ResultEntry.RemainingNone"),
            1 => T("ResultEntry.RemainingOne"),
            _ => T("ResultEntry.RemainingMany", left)
        };
    }

    private string Indent(ResultEntryRow row) =>
        new string(' ', row.IndentLevel * 4) + row.ParameterName.For(Localizer.CurrentLanguage) +
        (row.IsCalculated ? "  ƒ" : string.Empty);

    /// <summary>The indicator column's text — what a print of the grid or a screen reader gets.</summary>
    private string FlagText(ResultEntryRow row)
    {
        var parts = new List<string>();
        if (Indicator(row.Interpretation) is { } indicator) parts.Add(indicator.Text);
        if (UiTheme.FlagGlyphs(row.Flags) is { Length: > 0 } glyphs) parts.Add(glyphs);
        return string.Join(" ", parts);
    }

    private string FormatPrevious(ResultEntryRow row) =>
        row.PreviousValue is { } value
            ? $"{value:N2}" + (row.PreviousDate is { } date ? $"  {date:dd/MM/yy}" : string.Empty)
              + (row.PreviousImported ? $" · {T("Migration.HistoryMarker")}" : string.Empty)
            : "—";

    /// <summary>The analyser that sent the value, « Manuel » for a value keyed in, « — » before any value.</summary>
    private string FormatOrigin(ResultEntryRow row, ResultEntryOrigin? origin) =>
        ResultEntryRules.Origin(row, origin, Localizer.CurrentLanguage) ?? T("ResultEntry.Manual");

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

    // ---- Technical comment ---------------------------------------------------------------------------

    /// <summary>
    /// F3, or "Plus d'actions › Commentaire technique…": the comment of the selected line, typed in
    /// a box and kept in the line's comment cell until it is saved with the values, as when it was
    /// typed in its own column.
    /// </summary>
    private void EditSelectedComment()
    {
        if (SelectedRow() is not { } row || _grid.CurrentRow is not { } gridRow) return;

        var cell = gridRow.Cells[ColumnComment];
        if (cell.ReadOnly) return;

        _grid.EndEdit();

        var name = row.ParameterName.For(Localizer.CurrentLanguage);
        var comment = _dialogs.PromptForText(T("ResultEntry.CommentPrompt", name), T("Result.TechnicalComment"),
                                             cell.Value?.ToString(), maxLength: 2000);
        if (comment is null) return;

        cell.Value = comment.Trim();
        ShowParameterTip(gridRow, row);
        _grid.InvalidateRow(gridRow.Index);

        if (gridRow.Cells[ColumnValue] is { ReadOnly: false } value) _grid.CurrentCell = value;
    }

    // ---- Antériorités ---------------------------------------------------------------------------------

    /// <summary>The sheet row under the grid's cursor, or null on a heading, a section or an empty grid.</summary>
    private ResultEntryRow? SelectedRow() =>
        _grid.CurrentRow?.Tag is Guid id && _rowsById.TryGetValue(id, out var row) ? row : null;

    /// <summary>
    /// Opens the chart of the selected parameter's validated values, with the value typed in the
    /// grid — saved or not — as the current point. Read through the load gate: the screen's one
    /// context refuses a second query while the worklist or a file is loading.
    /// </summary>
    private async Task ShowHistoryAsync()
    {
        if (_sheet is not { } sheet) return;
        if (SelectedRow() is not { } row || !ResultHistory.IsCharted(row)) return;

        _grid.EndEdit();
        var typed = _grid.CurrentRow?.Cells[ColumnValue].Value?.ToString();

        IReadOnlyList<ResultHistoryPoint> points;

        await _loadGate.WaitAsync();
        try
        {
            var history = await _entry.GetHistoryAsync(sheet.LabOrderId, row.TestParameterId);

            if (history.IsFailure)
            {
                _dialogs.ShowFailure(history);
                return;
            }

            points = history.Value;
        }
        catch (OperationCanceledException)
        {
            // Navigated away mid-load.
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the history of parameter {ParameterId} failed.", row.TestParameterId);
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
            return;
        }
        finally
        {
            _loadGate.Release();
        }

        // Shown once the gate is released: the dialog is modal, and the screen must not stay
        // locked behind it.
        var series = ResultHistory.SeriesFor(row, points, sheet.AccessionNumber,
                                             row.IsEditable ? typed : null);

        using var dialog = new ResultHistoryDialog(Localizer, row.ParameterName.For(Localizer.CurrentLanguage),
                                                   series);
        dialog.ShowDialog(FindForm());
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
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), title);
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

        var entries = CollectTypedEntries();

        if (entries.Count == 0)
        {
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Message.NothingSelected");
            return;
        }

        await SubmitEntriesAsync(entries);
    }

    /// <summary>
    /// The values and comments typed in the grid, except on <paramref name="except"/>: only what
    /// actually changed is sent (ResultEntryRules.ChangedEntries), in the grid's order.
    /// </summary>
    private List<ResultEntry> CollectTypedEntries(Guid? except = null) =>
        ResultEntryRules.ChangedEntries(GridRows(), TypedValue, TypedComment, except);

    /// <summary>The sheet's rows in the order of the grid.</summary>
    private IEnumerable<ResultEntryRow> GridRows() =>
        _grid.Rows.Cast<DataGridViewRow>()
             .Select(r => r.Tag is Guid id && _rowsById.TryGetValue(id, out var row) ? row : null)
             .OfType<ResultEntryRow>();

    private DataGridViewRow? GridRowOf(Guid id) =>
        _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is Guid tag && tag == id);

    /// <summary>What the value cell of a result holds now, typed or not.</summary>
    private string? TypedValue(Guid id) => GridRowOf(id)?.Cells[ColumnValue].Value?.ToString();

    /// <summary>What the comment cell of a result holds now, typed or not.</summary>
    private string? TypedComment(Guid id) => GridRowOf(id)?.Cells[ColumnComment].Value?.ToString();

    private async Task SubmitEntriesAsync(List<ResultEntry> entries)
    {
        if (_sheet is null) return;

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

    // ---- Actions on the selected line -------------------------------------------------------------

    /// <summary>A line a value can be keyed on: not derived, not a heading, its tube received, not released.</summary>
    private static bool IsKeyable(ResultEntryRow? row) => ResultEntryRules.IsKeyable(row);

    /// <summary>
    /// Enables the menu's line actions for the selected line, and only for a user with their right
    /// — the gate the buttons had; the service checks again.
    /// </summary>
    private void UpdateRowActions()
    {
        var row = SelectedRow();
        var actions = PermissionGate.User is { } user ? ResultEntryRules.RowActions(row, user) : ResultRowActions.None;

        _notDone.Enabled = actions.NotDone;
        _clearValue.Enabled = actions.Clear;
        // Both rights, as the service asks (ResultEntryRules.RowActions).
        _correct.Enabled = actions.Correct;
        _comment.Enabled = row is not null && _grid.CurrentRow?.Cells[ColumnComment].ReadOnly == false;
    }

    /// <summary>
    /// Records the selected analysis as not done, with the reason printed on the report in the
    /// value's place. An empty cell used to be skipped on save, so nothing could be recorded as
    /// not done from this screen. Other values typed meanwhile are saved with it.
    /// </summary>
    private async Task MarkSelectedNotDoneAsync()
    {
        if (_sheet is null || SelectedRow() is not { } row || !IsKeyable(row)) return;

        _grid.EndEdit();

        var name = row.ParameterName.For(Localizer.CurrentLanguage);
        var reason = _dialogs.PromptForReason(T("ResultEntry.NotDonePrompt", name),
                                              T("ResultEntry.MarkNotDone"), minimumLength: 3,
                                              maxLength: 500);
        if (reason is null) return;

        var entries = CollectTypedEntries(except: row.TestResultId);
        entries.Add(new ResultEntry(row.TestResultId, null, reason, PrintTechnicalComment: true,
                                    Action: ResultEntryAction.NotDone));

        await SubmitEntriesAsync(entries);
    }

    /// <summary>
    /// Withdraws a value keyed by mistake (or a "not done"): the line is awaited again. Emptying the
    /// cell did nothing — an empty cell is skipped on save. Not for a released value, which is
    /// corrected with a reason instead.
    /// </summary>
    private async Task ClearSelectedAsync()
    {
        if (_sheet is null || SelectedRow() is not { } row || !IsKeyable(row)) return;

        _grid.EndEdit();

        var name = row.ParameterName.For(Localizer.CurrentLanguage);
        if (!_dialogs.Confirm(T("ResultEntry.ClearValueConfirm", name, row.CurrentValue ?? string.Empty),
                              T("ResultEntry.ClearValue")))
            return;

        var entries = CollectTypedEntries(except: row.TestResultId);
        entries.Add(new ResultEntry(row.TestResultId, null, Action: ResultEntryAction.Clear));

        await SubmitEntriesAsync(entries);
    }

    /// <summary>
    /// Corrects a released value, printed or not: the new figure and a reason. The released value
    /// is kept in the result's revisions; the new one is validated again, technically then
    /// biologically, and once printed goes out on a rectified report naming it.
    /// </summary>
    private async Task CorrectSelectedAsync()
    {
        if (_sheet is not { } sheet || SelectedRow() is not { } row) return;
        if (row.Status is not (TestResultStatus.Validated or TestResultStatus.Reported)) return;

        var title = T("ResultEntry.CorrectReleased");
        var name = row.ParameterName.For(Localizer.CurrentLanguage);

        // The values typed elsewhere would be lost by the reload that follows.
        if (!ConfirmDiscardTyped()) return;

        var value = _dialogs.PromptForText(
            T("ResultEntry.CorrectValuePrompt", name, row.CurrentValue ?? string.Empty,
              row.UnitSymbol ?? string.Empty),
            title, row.CurrentValue, maxLength: 100);
        if (string.IsNullOrWhiteSpace(value)) return;

        var reason = _dialogs.PromptForReason(T("ResultEntry.CorrectReasonPrompt", name), title,
                                              minimumLength: 15, maxLength: 500);
        if (reason is null) return;

        var outcome = await _entry.CorrectReleasedAsync(sheet.LabOrderId, row.TestResultId, value, reason);

        if (outcome.IsFailure)
        {
            _dialogs.ShowFailure(outcome, title);
            return;
        }

        await LoadOrderAsync(sheet.LabOrderId, CancellationToken.None);
        await LoadWorklistAsync();

        if (outcome.Value.CriticalFindings.Count > 0) ShowCriticalFindings(outcome.Value);

        _dialogs.Info(T("ResultEntry.CorrectedNotice", name), title);

        _status.ForeColor = UiTheme.Abnormal;
        _status.Text = T("ResultEntry.CorrectedStatus", name);
    }

    /// <summary>
    /// A panic value is announced as a modal, not as a status line. It obliges the laboratory to
    /// telephone the prescriber before the result can be released, and a message that can be
    /// missed is not an obligation anybody will meet.
    /// </summary>
    private void ShowCriticalFindings(ResultEntryOutcome outcome)
    {
        var lines = outcome.CriticalFindings
            .Select(f => $"• {T("App.LabelValue", f.ParameterName, f.Value)}   ({T($"Interpretation.{f.Interpretation}")})");

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

        if (!_dialogs.Confirm(ResultEntryRules.TechnicalConfirmation(_sheet.Rows, T), T("Validation.Technical"))) return;

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
            // As the reload left it: once every entered value is checked there is nothing more to
            // validate, and the button stays grey.
            _validate.Enabled = _sheet is { } open && ResultEntryRules.CanTechnicallyValidate(open.Rows);
        }
    }
}
