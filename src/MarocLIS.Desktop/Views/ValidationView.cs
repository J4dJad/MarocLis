using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
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
/// <para>
/// Laid out as the laboratory's « LIS · A Fluent clair » mock-up: « À valider » with filter chips,
/// the files in a card, then the open file's results beside its context (what is abnormal, late,
/// critical or out of control, the clinical information and the biologist's comment), and the
/// actions in the bottom bar with « Valider la sélection » last.
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

    private const int ContextWidth = 290;

    /// <summary>The grid's size in the semibold face: a patient's name, an abnormal parameter.</summary>
    private static readonly Font GridBold = EntryPaint.Resized(UiTheme.BodyBold, UiTheme.GridBody.Size);

    private readonly ValidationService _validation;
    private readonly ResultEntryService _entry;
    private readonly ReportReleaseService _reports;
    private readonly IDocumentOutput _documents;
    private readonly IUserDialogs _dialogs;
    private readonly INavigationService _navigation;
    private readonly ILogger<ValidationView>? _logger;

    // ---- « À valider », the chips, « Afficher les résultats validés » ----
    private readonly Label _title = new();
    private readonly FlowLayoutPanel _chips = new();
    private readonly Dictionary<ValidationFilter, ChipButton> _chipButtons = [];
    private ValidationFilter _filter = ValidationFilter.All;
    private readonly CheckBox _showValidated = new();

    // ---- the files ----
    private readonly DataGridView _worklist = UiTheme.Grid();
    private readonly ToolTip _tips = new();

    // ---- the open file's results ----
    private readonly Label _detailHeading = new();
    private readonly Label _detailSubtitle = new();
    private readonly Label _selectionCount = new();
    private readonly DataGridView _details = UiTheme.Grid();
    private readonly FlowLayoutPanel _keyHints = new();

    // ---- the context card ----
    private readonly Label _contextTitle = new();
    private readonly TableLayoutPanel _contextLines = new();
    private readonly TableLayoutPanel _prescriber = new();
    private Bitmap? _copyIcon;
    private readonly Label _clinicalCaption = ValidationPaint.Caption();
    private readonly Label _clinicalText = new();
    private readonly Label _commentCaption = ValidationPaint.Caption();
    private readonly FramedTextBox _comment = new();
    private readonly LinkLabel _openOrder = new();

    // ---- bottom bar ----
    private readonly Button _more = UiTheme.SecondaryButton(string.Empty);
    private readonly ContextMenuStrip _moreMenu = new();
    private readonly ToolStripMenuItem _selectAll = new();
    private readonly ToolStripMenuItem _unvalidate = new();
    private readonly ToolStripMenuItem _refresh = new();
    private readonly Button _history = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _communicate = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _release = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _remaining = new();
    private readonly Button _validate = UiTheme.PrimaryButton(string.Empty);
    private readonly Label _status = new();
    private readonly Panel _statusStrip = new();

    private IReadOnlyList<ValidationWorklistEntry> _entries = [];
    private ResultEntrySheet? _sheet;
    private readonly Dictionary<Guid, ResultEntryRow> _rowsById = [];
    private readonly List<ResultEntryRow> _listed = [];

    /// <summary>
    /// The biologist's comments typed on the open file, by result: printed under the value on the
    /// report once the line is validated. Kept while the same file stays open, so a validation that
    /// refuses a line does not lose what was typed for it.
    /// </summary>
    private readonly Dictionary<Guid, string> _comments = [];
    private Guid? _commentsOrderId;
    private Guid? _commentRowId;
    private bool _showingComment;

    public ValidationView(ValidationService validation, ResultEntryService entry,
                          ReportReleaseService reports, IDocumentOutput documents, IUserDialogs dialogs,
                          INavigationService navigation,
                          ILocalizer localizer, ICultureManager cultureManager,
                          ILogger<ValidationView>? logger = null)
        : base(localizer, cultureManager)
    {
        _validation = validation;
        _entry = entry;
        _reports = reports;
        _documents = documents;
        _dialogs = dialogs;
        _navigation = navigation;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Validation");

    private bool IsRightToLeft => RightToLeft == RightToLeft.Yes;

    // ---- Layout -----------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(0);

        var host = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };

        // The cards: the heading row, the files, then the results beside their context. A table
        // rather than docking: it mirrors by itself in Arabic, the context going to the left.
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(24, 14, 24, 12),
            Margin = new Padding(0),
            BackColor = UiTheme.Surface
        };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 68));

        page.Controls.Add(BuildHeadingRow(), 0, 0);
        page.Controls.Add(BuildWorklistCard(), 0, 1);
        page.Controls.Add(BuildLowerRow(), 0, 2);

        host.Controls.Add(page);
        host.Controls.Add(BuildStatusStrip());
        host.Controls.Add(BuildActionBar());

        Controls.Add(host);

        SetDetailActionsEnabled(false);
    }

    /// <summary>« À valider », the chips, and « Afficher les résultats validés » at the far end.</summary>
    private TableLayoutPanel BuildHeadingRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 10),
            BackColor = UiTheme.Surface
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _title.Font = UiTheme.PageTitle;
        _title.ForeColor = UiTheme.TextPrimary;
        _title.AutoSize = true;
        _title.Anchor = AnchorStyles.Left;
        _title.Margin = new Padding(0, 0, 14, 0);

        // One line beside the title: a wrapping panel in an auto-sized row measures itself as a
        // column of chips and opens a tall gap above the files.
        _chips.AutoSize = true;
        _chips.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _chips.WrapContents = false;
        _chips.Anchor = AnchorStyles.Left;
        _chips.Margin = new Padding(0, 4, 0, 0);
        _chips.BackColor = UiTheme.Surface;

        foreach (var filter in Enum.GetValues<ValidationFilter>())
        {
            var chip = new ChipButton { Height = 28, Selected = filter == _filter };
            chip.Click += async (_, _) => await SelectFilterAsync(filter);
            _chipButtons[filter] = chip;
            _chips.Controls.Add(chip);
        }

        // Brings back the files and results already validated but not yet reported: the only way
        // to reach a release in order to withdraw it, since the worklist otherwise drops a file the
        // moment its last result is validated.
        _showValidated.AutoSize = true;
        _showValidated.Font = UiTheme.Body;
        _showValidated.ForeColor = UiTheme.TextPrimary;
        _showValidated.Anchor = AnchorStyles.Right;
        _showValidated.Margin = new Padding(12, 0, 0, 0);

        row.Controls.Add(_title, 0, 0);
        row.Controls.Add(_chips, 1, 0);
        row.Controls.Add(_showValidated, 2, 0);
        return row;
    }

    private Panel BuildWorklistCard()
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 14),
            Padding = new Padding(1),
            BackColor = UiTheme.SurfaceRaised
        };
        UiChrome.AsCard(card);

        BuildWorklistColumns();
        _worklist.RowTemplate.Height = 34;
        _worklist.ColumnHeadersDefaultCellStyle.Padding = new Padding(12, 6, 12, 6);
        _worklist.DefaultCellStyle.Padding = new Padding(12, 0, 12, 0);
        _worklist.ShowCellToolTips = true;
        _worklist.CellPainting += PaintWorklistCell;

        card.Controls.Add(_worklist);
        return card;
    }

    /// <summary>The results card, and the context card beside it.</summary>
    private TableLayoutPanel BuildLowerRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = UiTheme.Surface
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ContextWidth));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        row.Controls.Add(BuildResultsCard(), 0, 0);
        row.Controls.Add(BuildContextCard(), 1, 0);
        return row;
    }

    private Panel BuildResultsCard()
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 14, 0),
            Padding = new Padding(1),
            BackColor = UiTheme.SurfaceRaised
        };
        UiChrome.AsCard(card);
        card.RightToLeftChanged += (_, _) =>
            card.Margin = IsRightToLeft ? new Padding(14, 0, 0, 0) : new Padding(0, 0, 14, 0);

        // « 2610-000008 — NOM Prénom   31 ans · Masculin            14 sur 16 sélectionnés »
        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(14, 10, 14, 8),
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _detailHeading.Font = UiTheme.SectionTitle;
        _detailHeading.ForeColor = UiTheme.TextPrimary;
        _detailHeading.AutoSize = true;
        _detailHeading.Anchor = AnchorStyles.Left;
        _detailHeading.Margin = new Padding(0, 0, 10, 0);
        _detailHeading.UseMnemonic = false;

        _detailSubtitle.Font = UiTheme.Body;
        _detailSubtitle.ForeColor = UiTheme.TextSecondary;
        _detailSubtitle.AutoSize = true;
        _detailSubtitle.AutoEllipsis = true;
        _detailSubtitle.Anchor = AnchorStyles.Left;
        _detailSubtitle.Margin = new Padding(0, 2, 10, 0);

        _selectionCount.Font = UiTheme.Body;
        _selectionCount.ForeColor = UiTheme.TextSecondary;
        _selectionCount.AutoSize = true;
        _selectionCount.Anchor = AnchorStyles.Right;
        _selectionCount.Margin = new Padding(0, 2, 0, 0);

        heading.Controls.Add(_detailHeading, 0, 0);
        heading.Controls.Add(_detailSubtitle, 1, 0);
        heading.Controls.Add(_selectionCount, 2, 0);

        BuildDetailColumns();
        _details.RowTemplate.Height = 30;
        _details.ColumnHeadersDefaultCellStyle.Padding = new Padding(10, 6, 10, 6);
        _details.DefaultCellStyle.Padding = new Padding(10, 0, 10, 0);
        _details.ShowCellToolTips = true;
        _details.CellPainting += PaintDetailCell;

        // The keys the grid answers to, under it, in the work-surface colour.
        _keyHints.Dock = DockStyle.Bottom;
        _keyHints.Height = 30;
        _keyHints.WrapContents = false;
        _keyHints.AutoScroll = false;
        _keyHints.Padding = new Padding(12, 8, 12, 0);
        _keyHints.BackColor = UiTheme.Surface;
        _keyHints.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, 0, 0, _keyHints.Width, 0);
        };

        card.Controls.Add(_details);
        card.Controls.Add(_keyHints);
        card.Controls.Add(heading);
        return card;
    }

    /// <summary>
    /// « Contexte »: what the file carries that a biologist must see before signing, the clinical
    /// information, the comment printed under the selected value, and the way to the full file.
    /// Scrolls inside itself on a short screen.
    /// </summary>
    private Panel BuildContextCard()
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(1),
            BackColor = UiTheme.SurfaceRaised
        };
        UiChrome.AsCard(card);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 9,
            Padding = new Padding(14, 12, 14, 12),
            Margin = new Padding(0),
            AutoScroll = true,
            BackColor = UiTheme.SurfaceRaised
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 7; i++) body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _contextTitle.Font = UiTheme.SectionTitle;
        _contextTitle.ForeColor = UiTheme.TextPrimary;
        _contextTitle.AutoSize = true;
        _contextTitle.Margin = new Padding(0, 0, 0, 8);

        _contextLines.AutoSize = true;
        _contextLines.Dock = DockStyle.Fill;
        _contextLines.ColumnCount = 2;
        _contextLines.Margin = new Padding(0);
        _contextLines.BackColor = UiTheme.SurfaceRaised;
        _contextLines.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _contextLines.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // The rule between the signals and the clinical information.
        var rule = new Panel { Dock = DockStyle.Fill, Height = 1, Margin = new Padding(0, 6, 0, 10), BackColor = UiTheme.Border };

        // « Prescripteur »: who to call, each number with its copy button. A table, so the labels
        // go to the right in Arabic like the signals above.
        _prescriber.AutoSize = true;
        _prescriber.Dock = DockStyle.Fill;
        _prescriber.ColumnCount = 3;
        _prescriber.Margin = new Padding(0, 0, 0, 12);
        _prescriber.BackColor = UiTheme.SurfaceRaised;
        _prescriber.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _prescriber.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _prescriber.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _clinicalText.AutoSize = true;
        _clinicalText.Font = UiTheme.Body;
        _clinicalText.ForeColor = UiTheme.TextPrimary;
        _clinicalText.Margin = new Padding(0, 0, 0, 12);
        _clinicalText.MaximumSize = new Size(ContextWidth - 36, 0);
        _clinicalText.UseMnemonic = false;

        _commentCaption.AutoEllipsis = true;
        _commentCaption.MaximumSize = new Size(ContextWidth - 36, 0);

        _comment.Dock = DockStyle.Fill;
        _comment.Margin = new Padding(0, 0, 0, 8);
        _comment.Box.MaxLength = 2000;
        _comment.Box.Enabled = false;

        _openOrder.AutoSize = true;
        _openOrder.Font = UiTheme.Small;
        _openOrder.LinkColor = UiTheme.Accent;
        _openOrder.ActiveLinkColor = UiTheme.AccentInk;
        _openOrder.LinkBehavior = LinkBehavior.HoverUnderline;
        _openOrder.Margin = new Padding(0, 2, 0, 0);

        body.Controls.Add(_contextTitle, 0, 0);
        body.Controls.Add(_contextLines, 0, 1);
        body.Controls.Add(rule, 0, 2);
        body.Controls.Add(_prescriber, 0, 3);
        body.Controls.Add(_clinicalCaption, 0, 4);
        body.Controls.Add(_clinicalText, 0, 5);
        body.Controls.Add(_commentCaption, 0, 6);
        body.Controls.Add(_comment, 0, 7);
        body.Controls.Add(_openOrder, 0, 8);

        card.Controls.Add(body);
        return card;
    }

    /// <summary>What the last action said — validated, refused, released. Shown only while there is something to say.</summary>
    private Panel BuildStatusStrip()
    {
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
        _status.UseMnemonic = false;
        _status.TextChanged += (_, _) =>
        {
            _statusStrip.Visible = !string.IsNullOrEmpty(_status.Text);
            _tips.SetToolTip(_status, _status.Text);
        };
        _statusStrip.Controls.Add(_status);
        return _statusStrip;
    }

    /// <summary>« Plus d'actions ▾ », Antériorités, Résultats critiques…, Compte rendu, the count, « Valider la sélection ».</summary>
    private EntryStrip BuildActionBar()
    {
        // In a strip of its own: a white table straight on the work surface would be drawn as a card.
        var strip = new EntryStrip { Dock = DockStyle.Bottom, Height = 56, BackColor = UiTheme.SurfaceRaised };
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 7,
            RowCount = 1,
            Padding = new Padding(24, 11, 24, 11),
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        strip.Controls.Add(bar);

        var buttons = new[] { _more, _history, _communicate, _release, _validate };

        // On a 1366-pixel screen the count does not always fit on one line beside the buttons: it
        // takes what is left and wraps, rather than pushing a button out of the bar.
        void FitRemaining()
        {
            var used = bar.Padding.Horizontal + _remaining.Margin.Horizontal
                       + buttons.Where(b => b.Visible).Sum(b => b.PreferredSize.Width + b.Margin.Horizontal);
            var left = Math.Max(90, bar.ClientSize.Width - used);
            var font = TextRenderer.MeasureText(_remaining.Text, UiTheme.Body).Width <= left ? UiTheme.Body : UiTheme.Small;
            if (_remaining.Font != font) _remaining.Font = font;
            if (_remaining.MaximumSize.Width != left) _remaining.MaximumSize = new Size(left, 0);
        }

        bar.Resize += (_, _) => FitRemaining();
        _remaining.TextChanged += (_, _) => FitRemaining();

        foreach (var button in buttons)
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(0, 34);
            button.Height = 34;
            button.Margin = new Padding(0, 0, 8, 0);
        }

        _validate.Margin = new Padding(0);
        ButtonKey.ShowChevron(_more);

        _remaining.AutoSize = true;
        _remaining.Anchor = AnchorStyles.Right;
        _remaining.TextAlign = ContentAlignment.MiddleRight;
        _remaining.Font = UiTheme.Body;
        _remaining.ForeColor = UiTheme.TextSecondary;
        _remaining.Margin = new Padding(8, 0, 12, 0);

        var spacer = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = UiTheme.SurfaceRaised };

        bar.Controls.Add(_more, 0, 0);
        bar.Controls.Add(_history, 1, 0);
        bar.Controls.Add(_communicate, 2, 0);
        bar.Controls.Add(_release, 3, 0);
        bar.Controls.Add(spacer, 4, 0);
        bar.Controls.Add(_remaining, 5, 0);
        bar.Controls.Add(_validate, 6, 0);

        // The everyday buttons that were in the strip and are used less: select all, withdraw a
        // release, refresh. Each keeps its icon.
        _moreMenu.Items.AddRange([_selectAll, _unvalidate, new ToolStripSeparator(), _refresh]);
        _moreMenu.Font = UiTheme.Body;
        _selectAll.Image = Icons.Render(Glyph.ListChecks, UiTheme.TextPrimary, 16);
        _unvalidate.Image = Icons.Render(Glyph.Undo2, UiTheme.TextPrimary, 16);
        _refresh.Image = Icons.Render(Glyph.RefreshCw, UiTheme.TextPrimary, 16);
        _refresh.ShortcutKeyDisplayString = "F5";

        // The captions are not plain resource texts any more ("Valider la sélection (14)",
        // "Résultats critiques…"): their icons are set here rather than found by caption.
        ButtonIcons.Set(_validate, Glyph.BadgeCheck, always: true);
        ButtonIcons.Set(_communicate, Glyph.TriangleAlert, always: true);

        return strip;
    }

    private void BuildWorklistColumns()
    {
        _worklist.Columns.Clear();

        void Add(string name, int weight, Font? font = null,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
        {
            var column = new DataGridViewTextBoxColumn
            {
                Name = name,
                FillWeight = weight,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.GridBody,
                    Alignment = align
                }
            };
            if (align == DataGridViewContentAlignment.MiddleRight)
                column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            _worklist.Columns.Add(column);
        }

        Add("accession", 13, UiTheme.Monospace);
        Add("patient", 27);
        Add("priority", 10);
        Add("awaiting", 8, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);
        Add("abnormal", 8, UiTheme.MonospaceBold, DataGridViewContentAlignment.MiddleRight);
        Add("alerts", 26);
        Add("registered", 10, UiTheme.Monospace);
    }

    private void BuildDetailColumns()
    {
        _details.Columns.Clear();
        _details.ReadOnly = false;
        _details.MultiSelect = false;

        _details.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = ColumnSelected,
            // A tick box has one width; by weight it took a fifth of the grid from the parameter names.
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            Width = 40,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        void Add(string name, int weight, Font? font = null,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft,
                 Color? foreground = null)
        {
            var column = new DataGridViewTextBoxColumn
            {
                Name = name,
                FillWeight = weight,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.GridBody,
                    Alignment = align,
                    ForeColor = foreground ?? UiTheme.TextPrimary
                }
            };
            if (align == DataGridViewContentAlignment.MiddleRight)
                column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            _details.Columns.Add(column);
        }

        Add(ColumnParameter, 30);
        Add(ColumnValue, 11, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);
        Add(ColumnFlag, 13, UiTheme.SmallBold);
        Add(ColumnUnit, 8, UiTheme.Small, foreground: UiTheme.TextSecondary);
        Add(ColumnRange, 14, UiTheme.Monospace, foreground: UiTheme.TextSecondary);
        Add(ColumnPrevious, 13, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight,
            UiTheme.TextSecondary);

        // The analyser's own doubts (linearity, reagent, sample alarms): a value it flagged must not
        // be signed without the biologist seeing why.
        Add(ColumnAnalyser, 13, UiTheme.Small, foreground: UiTheme.Abnormal);
    }

    private void WireBehaviour()
    {
        // A reload while an action is in flight would replace the file under it: it is put off
        // until the action ends. Not every action reloads the worklist (a refusal, a cancel, a
        // critical value communicated), so a filter ticked meanwhile is applied then.
        _refresh.Click += async (_, _) => await ReloadOrDeferAsync();
        _showValidated.CheckedChanged += async (_, _) => await ReloadOrDeferAsync();
        BecameIdle += async (_, _) =>
        {
            // Left mid-action, the screen is no longer hosted and is about to be disposed.
            if (_worklistStale && !IsDisposed && Parent is not null) await ActivateAsync();
        };

        _worklist.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadSelectedAsync());

        _selectAll.Click += (_, _) => ToggleAll();

        // One action at a time, each through RunBusyAsync: a second click while one runs is
        // ignored (a double-click on "communicate" recorded every critical value twice), a release
        // can no longer be confirmed for one file while a validation still running moves the
        // screen to the next, and leaving the screen mid-write no longer disposes its context.
        // Each also waits its turn behind a selection load: the two share the view's context, and
        // « Résultats critiques… » clicked while the file was still loading failed with "A second
        // operation was started on this context instance".
        _validate.Click += async (_, _) => await RunBusyAsync(() => _selectionLoads.RunAsync(ValidateSelectedAsync));
        _communicate.Click += async (_, _) => await RunBusyAsync(() => _selectionLoads.RunAsync(CommunicateCriticalAsync));
        _release.Click += async (_, _) => await RunBusyAsync(() => _selectionLoads.RunAsync(ReleaseReportAsync));
        _unvalidate.Click += async (_, _) => await RunBusyAsync(() => _selectionLoads.RunAsync(UnvalidateSelectedAsync));

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Results.BiologicalValidate, _validate);
        PermissionGate.Require(Permissions.Results.EnterCritical, _communicate);
        PermissionGate.Require(Permissions.Reports.Print, _release);

        // Un-validation is in the « Plus d'actions » menu: a menu item is not a control, so its
        // right is checked each time the menu opens (see UpdateMenu), with the same permission.
        _more.Click += (_, _) =>
        {
            UpdateMenu();
            _moreMenu.RightToLeft = RightToLeft;
            _moreMenu.Show(_more, new Point(IsRightToLeft ? _more.Width : 0, _more.Height));
        };

        // The antériorités of the selected parameter: the button, or a double-click on the
        // antériorité cell, which is where the eye already is when the last value raises a doubt.
        _history.Visible = PermissionGate.Allows(Permissions.Results.View);
        PermissionGate.Require(Permissions.Results.View, _history);
        _history.Click += async (_, _) => await ShowHistoryAsync();
        _details.CurrentCellChanged += (_, _) =>
        {
            _history.Enabled = ResultHistory.IsCharted(SelectedDetail());
            ShowComment();
        };
        _details.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (_details.Columns[e.ColumnIndex].Name != ColumnPrevious) return;
            if (PermissionGate.IsBlocked(_history)) return;

            await ShowHistoryAsync();
        };

        // A tick counts the moment it is clicked, not when the cell is left: the count and the
        // caption of « Valider la sélection » follow it.
        _details.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_details.IsCurrentCellDirty && _details.CurrentCell is DataGridViewCheckBoxCell)
                _details.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _details.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && _details.Columns[e.ColumnIndex].Name == ColumnSelected)
                ShowSelectionCount();
        };

        _details.CellFormatting += OnDetailFormatting;
        _worklist.CellFormatting += OnWorklistFormatting;

        // Double-clicking a file puts the keyboard in its results, which is where the biologist
        // goes next: ticking with the space bar and validating with F9.
        _worklist.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _worklist.Rows[e.RowIndex].Tag is Guid) _details.Focus();
        };

        _comment.Box.TextChanged += (_, _) => KeepComment();

        _openOrder.Visible = PermissionGate.Allows(Permissions.Orders.View);
        _openOrder.LinkClicked += async (_, _) => await OpenOrderAsync();
    }

    /// <summary>
    /// The screen's keys: F9 validates the selection, F10 opens the next file, the space bar ticks
    /// the result under the cursor. Each does exactly what its button does, and nothing when that
    /// button is unavailable, so a key the user gave to something else in the shell still reaches it.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F9 when _validate.Enabled && _validate.Visible && !IsBusy:
                _validate.PerformClick();
                return true;

            case Keys.F10 when !IsBusy:
                return SelectNextFile();

            case Keys.Space when _details.ContainsFocus && _details.CurrentRow is { } row
                                 && !row.Cells[ColumnSelected].ReadOnly:
                _details.EndEdit();
                row.Cells[ColumnSelected].Value = row.Cells[ColumnSelected].Value is not true;
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool SelectNextFile()
    {
        if (_worklist.Rows.Count == 0) return false;

        var next = (_worklist.CurrentRow?.Index ?? -1) + 1;
        if (next >= _worklist.Rows.Count) return true;

        _worklist.CurrentCell = _worklist.Rows[next].Cells[0];
        return true;
    }

    private void OnWorklistFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _worklist.Rows.Count) return;
        if (_worklist.Rows[e.RowIndex].Tag is not Guid id) return;

        var entry = _entries.FirstOrDefault(x => x.LabOrderId == id);
        if (entry is null) return;

        var name = _worklist.Columns[e.ColumnIndex].Name;

        // An uncommunicated critical result is the only state here that is a breached obligation
        // rather than a backlog, so it is the only one painted as critical; late files and failed
        // QC carry their pill in « Signalements ».
        if (entry.UncommunicatedCriticalCount > 0)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
            e.CellStyle.Font = new Font(e.CellStyle.Font ?? UiTheme.GridBody, FontStyle.Bold);
        }
        else if (name == "abnormal" && entry.AbnormalCount > 0)
        {
            e.CellStyle!.ForeColor = UiTheme.Abnormal;
        }

        if (name == "accession" && _worklist.Rows[e.RowIndex].Selected)
            e.CellStyle!.Font = UiTheme.MonospaceBold;
    }

    /// <summary>
    /// The patient as « NOM Prénom · M · 31 ans », the priority as a pill (« Routine » in plain
    /// text), and the signals as pills.
    /// </summary>
    private void PaintWorklistCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.Graphics is null) return;
        if (_worklist.Rows[e.RowIndex].Tag is not Guid id) return;

        var entry = _entries.FirstOrDefault(x => x.LabOrderId == id);
        if (entry is null) return;

        var name = _worklist.Columns[e.ColumnIndex].Name;
        if (name is not ("patient" or "priority" or "alerts")) return;

        var rtl = IsRightToLeft;
        var selected = (e.State & DataGridViewElementStates.Selected) != 0;
        e.PaintBackground(e.ClipBounds, selected);

        var inner = new Rectangle(e.CellBounds.X + 12, e.CellBounds.Y, e.CellBounds.Width - 24, e.CellBounds.Height);
        var alignment = rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left;
        const TextFormatFlags single = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                                       | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;

        switch (name)
        {
            case "patient":
            {
                var nameFont = GridBold;
                var nameColour = entry.UncommunicatedCriticalCount > 0 ? UiTheme.Critical : UiTheme.TextPrimary;
                var nameWidth = Math.Min(inner.Width,
                    TextRenderer.MeasureText(entry.PatientName, nameFont, Size.Empty, TextFormatFlags.NoPadding).Width);
                TextRenderer.DrawText(e.Graphics, entry.PatientName, nameFont,
                                      EntryPaint.Mirror(new Rectangle(inner.X, inner.Y, nameWidth, inner.Height), e.CellBounds, rtl),
                                      nameColour, single | TextFormatFlags.EndEllipsis | alignment);

                var rest = PatientDetail(entry);
                if (rest.Length > 0 && nameWidth < inner.Width - 10)
                {
                    var restBounds = new Rectangle(inner.X + nameWidth, inner.Y, inner.Width - nameWidth, inner.Height);
                    TextRenderer.DrawText(e.Graphics, $" · {rest}", UiTheme.GridBody,
                                          EntryPaint.Mirror(restBounds, e.CellBounds, rtl),
                                          UiTheme.TextSecondary, single | TextFormatFlags.EndEllipsis | alignment);
                }

                break;
            }

            case "priority":
                if (entry.Priority == OrderPriority.Routine)
                {
                    TextRenderer.DrawText(e.Graphics, T("Priority.Routine"), UiTheme.GridBody,
                                          EntryPaint.Mirror(inner, e.CellBounds, rtl), UiTheme.TextSecondary,
                                          single | TextFormatFlags.EndEllipsis | alignment);
                }
                else
                {
                    ValidationPaint.DrawPills(e.Graphics, e.CellBounds,
                        [(T($"Priority.{entry.Priority}"),
                          entry.Priority == OrderPriority.VitalEmergency ? EntryPaint.Critical : EntryPaint.Warning)],
                        rtl, 12);
                }

                break;

            case "alerts":
                ValidationPaint.DrawPills(e.Graphics, e.CellBounds,
                    [.. ValidationScreenRules.Alerts(entry).Select(a => (T(a.Key, a.Args), ValidationPaint.Colours(a.Tone)))],
                    rtl, 12);
                break;
        }

        e.Handled = true;
    }

    /// <summary>« M · 31 ans »: the sex and the age after the patient's name.</summary>
    private string PatientDetail(ValidationWorklistEntry entry)
    {
        var parts = new List<string>(2);
        if (entry.Gender != Domain.Patients.Gender.Unknown) parts.Add(T($"GenderShort.{entry.Gender}"));
        if (entry.AgeYears is { } age) parts.Add($"{age} {T("Report.Years")}");
        return string.Join(" · ", parts);
    }

    private void OnDetailFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _details.Rows.Count) return;
        if (_details.Rows[e.RowIndex].Tag is not Guid id) return;
        if (!_rowsById.TryGetValue(id, out var row)) return;

        var name = _details.Columns[e.ColumnIndex].Name;
        var appearance = UiTheme.ForInterpretation(row.Interpretation);
        var abnormal = ValidationScreenRules.IsAbnormal(row);

        // An abnormal line in the warm tint of the mock-up, its parameter and value in bold; the
        // critical one keeps the critical tint. The value takes the interpretation's ink.
        if (abnormal)
        {
            e.CellStyle!.BackColor = row.IsCritical ? UiTheme.CriticalBackground : ValidationPaint.AbnormalRow;
            if (name == ColumnParameter) e.CellStyle.Font = GridBold;
        }

        if (name == ColumnValue)
        {
            e.CellStyle!.ForeColor = appearance.Foreground;
            if (appearance.Bold || abnormal)
                e.CellStyle.Font = new Font(e.CellStyle.Font ?? UiTheme.Monospace, FontStyle.Bold);
        }

        // A comment typed for the line is marked on its parameter, and read in its tooltip.
        if (name == ColumnParameter && _comments.TryGetValue(id, out var comment) && e.Value is string text)
        {
            e.Value = text + "  ✎";
            e.FormattingApplied = true;
            _details.Rows[e.RowIndex].Cells[e.ColumnIndex].ToolTipText = comment;
        }

        // Already released, listed only so it can be withdrawn: set apart from what awaits release.
        if (ValidationScreenRules.IsReleased(row))
        {
            e.CellStyle!.ForeColor = UiTheme.TextSecondary;
            e.CellStyle.Font = new Font(e.CellStyle.Font ?? UiTheme.GridBody, FontStyle.Italic);
        }

        if (row.IsHeading)
        {
            e.CellStyle!.BackColor = UiTheme.SurfaceSunken;
            e.CellStyle.Font = new Font(UiTheme.GridBody, FontStyle.Bold | FontStyle.Italic);
        }
    }

    /// <summary>
    /// « Indic. »: the indicator as a pill — « ▲ Haut » in the warning pair, « ▼ Bas » in the info
    /// pair, critical in the critical pair — then the result's other flags (CIQ, Δ, ×…) as before.
    /// </summary>
    private void PaintDetailCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.Graphics is null) return;
        if (_details.Columns[e.ColumnIndex].Name != ColumnFlag) return;
        if (_details.Rows[e.RowIndex].Tag is not Guid id || !_rowsById.TryGetValue(id, out var row)) return;

        var rtl = IsRightToLeft;
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

    /// <summary>The indicator pill of an interpretation; null for a normal or uninterpretable value.</summary>
    private (string Text, (Color Back, Color Fore) Colours)? Indicator(ResultInterpretation interpretation) =>
        ResultEntryRules.Indicator(interpretation) is { } indicator
            ? ($"{indicator.Arrow} {T(indicator.Key)}", ValidationPaint.Colours(indicator.Tone))
            : null;

    protected override void ApplyTexts()
    {
        _title.Text = T("Dashboard.AwaitingValidation");
        _showValidated.Text = T("Validation.ShowValidated");
        _contextTitle.Text = T("Validation.Context");
        _clinicalCaption.Text = T("Order.ClinicalInformation");
        _openOrder.Text = T("Validation.OpenFullOrder");

        _more.Text = T("ResultEntry.MoreActions");
        _selectAll.Text = T("Action.SelectAll");
        _unvalidate.Text = $"{T("Validation.Unvalidate")}…";
        _refresh.Text = T("Action.Refresh");
        _history.Text = T("Result.History");
        _communicate.Text = $"{T("Nav.CriticalResults")}…";
        _release.Text = T("Report.Title");
        ButtonKey.Show(_validate, "F9");

        if (_worklist.Columns.Count > 0)
        {
            _worklist.Column("accession").HeaderText = T("Order.Title");
            _worklist.Column("patient").HeaderText = T("Patient.Title");
            _worklist.Column("priority").HeaderText = T("Field.Priority");
            _worklist.Column("awaiting").HeaderText = T("Validation.ToValidateColumn");
            _worklist.Column("abnormal").HeaderText = T("Validation.AbnormalColumn");
            _worklist.Column("alerts").HeaderText = T("Result.Flags");
            _worklist.Column("registered").HeaderText = T("Order.RegisteredAt");
        }

        if (_details.Columns.Count > 0)
        {
            _details.Column(ColumnSelected).HeaderText = string.Empty;
            _details.Column(ColumnParameter).HeaderText = T("Result.Parameter");
            _details.Column(ColumnValue).HeaderText = T("Result.Value");
            _details.Column(ColumnFlag).HeaderText = T("ResultEntry.Indicator");
            _details.Column(ColumnUnit).HeaderText = T("Result.Unit");
            _details.Column(ColumnRange).HeaderText = T("Result.ReferenceRange");
            _details.Column(ColumnPrevious).HeaderText = T("Result.Previous");
            _details.Column(ColumnAnalyser).HeaderText = T("Result.InstrumentFlags");
        }

        RenderKeyHints();
        RenderChips();
        RenderRemaining();

        if (_sheet is { } sheet) RenderDetails(sheet, keepTicks: true);
        else ClearDetails();

        _worklist.Invalidate();
    }

    /// <summary>The keys the screen answers to — and only those.</summary>
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

        Hint(T("Validation.KeySpace"), T("Validation.KeyTick"));
        Hint("F9", T("Validation.KeyValidate"));
        Hint("F10", T("Validation.KeyNextOrder"));
        Hint(T("ResultEntry.DoubleClick"), T("ResultEntry.KeyHistory"));

        _keyHints.ResumeLayout(performLayout: true);
    }

    // ---- Worklist ---------------------------------------------------------------------------------

    /// <summary>Set when a reload was asked for during an action; cleared by the reload.</summary>
    private bool _worklistStale;

    private async Task ReloadOrDeferAsync()
    {
        if (IsBusy)
        {
            _worklistStale = true;
            return;
        }

        await ActivateAsync();
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        // The filters are read below: a change after this point asks for another reload.
        _worklistStale = false;
        try
        {
            // The whole list, every time: the chips filter it here and show their counts. « Urgents »
            // is the server's former « urgent only », the same test on the same rows.
            var result = await _validation.GetWorklistAsync(onlyUrgent: false,
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
        // Left while the list loaded: its grid is emptied with the screen.
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException
                                   && (IsDisposed || Disposing)) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the validation worklist failed.");
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
    }

    private async Task SelectFilterAsync(ValidationFilter filter)
    {
        if (_filter == filter) return;

        _filter = filter;
        RenderChips();

        // A filter changed while an action runs is applied when it ends, like the tick box.
        if (IsBusy)
        {
            _worklistStale = true;
            return;
        }

        RenderWorklist();
        await Task.CompletedTask;
    }

    private void RenderChips()
    {
        foreach (var (filter, chip) in _chipButtons)
        {
            var caption = filter switch
            {
                ValidationFilter.Urgent => T("Validation.FilterUrgent"),
                ValidationFilter.Abnormal => T("Validation.FilterAbnormal"),
                ValidationFilter.QcBlocked => T("Validation.FilterQc"),
                _ => T("ResultEntry.AllSectors")
            };

            chip.Text = $"{caption} · {ValidationScreenRules.Count(_entries, filter)}";
            chip.Selected = filter == _filter;
        }
    }

    private IReadOnlyList<ValidationWorklistEntry> VisibleEntries =>
        [.. _entries.Where(e => ValidationScreenRules.Matches(e, _filter))];

    private void RenderWorklist()
    {
        var keep = _worklist.CurrentRow?.Tag as Guid?;
        var visible = VisibleEntries;

        _worklist.SuspendLayout();
        _worklist.Rows.Clear();

        foreach (var entry in visible)
        {
            var alerts = ValidationScreenRules.Alerts(entry);
            var detail = PatientDetail(entry);

            // The cells hold the text a print of the list (Ctrl+P) or a screen reader gets; the
            // patient, the priority and the signals are painted over it.
            var index = _worklist.Rows.Add(
                entry.AccessionNumber,
                detail.Length > 0 ? $"{entry.PatientName} · {detail}" : entry.PatientName,
                T($"Priority.{entry.Priority}"),
                entry.AwaitingCount.ToString(),
                entry.AbnormalCount > 0 ? entry.AbnormalCount.ToString() : string.Empty,
                string.Join(" · ", alerts.Select(a => T(a.Key, a.Args))),
                entry.RegisteredAt.ToString("dd/MM HH:mm"));

            var gridRow = _worklist.Rows[index];
            gridRow.Tag = entry.LabOrderId;
            gridRow.Cells["alerts"].ToolTipText = gridRow.Cells["alerts"].Value as string ?? string.Empty;
            gridRow.Cells["patient"].ToolTipText = gridRow.Cells["patient"].Value as string ?? string.Empty;
        }

        _worklist.ResumeLayout(performLayout: true);

        RenderChips();
        RenderRemaining();

        if (_worklist.Rows.Count == 0)
        {
            ClearDetails();
            return;
        }

        // The file that was open stays open across a reload, as long as it is still listed.
        var reselect = keep is { } id
            ? _worklist.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is Guid g && g == id)
            : null;

        if (reselect is null || reselect.Index == 0)
        {
            _worklist.SelectFirstRow();
            return;
        }

        try
        {
            _worklist.CurrentCell = reselect.Cells[0];
            reselect.Selected = true;
        }
        catch (InvalidOperationException)
        {
            // Mid-layout: fall back on the first file, as before.
            _worklist.SelectFirstRow();
        }
    }

    /// <summary>« 2 dossiers · 31 résultats en attente », over what the chip shows.</summary>
    private void RenderRemaining()
    {
        var visible = VisibleEntries;
        _remaining.Text = T("Validation.PendingSummary", visible.Count, visible.Sum(e => e.AwaitingCount));
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
        RenderDetails(_sheet, keepTicks: false);
    }

    private void RenderDetails(ResultEntrySheet sheet, bool keepTicks)
    {
        // The comments typed belong to one file: another file starts with none.
        if (_commentsOrderId != sheet.LabOrderId)
        {
            _comments.Clear();
            _commentsOrderId = sheet.LabOrderId;
        }

        // Re-rendered for a new language, the ticks the biologist set are put back.
        var ticks = keepTicks
            ? _details.Rows.Cast<DataGridViewRow>()
                .Where(r => r.Tag is Guid)
                .ToDictionary(r => (Guid)r.Tag!, r => r.Cells[ColumnSelected].Value is true)
            : null;

        _rowsById.Clear();
        _listed.Clear();

        _detailHeading.Text = $"{sheet.AccessionNumber} — {sheet.PatientName}";

        var subtitle = new List<string>(2);
        if (sheet.AgeYears is { } age) subtitle.Add($"{age} {T("Report.Years")}");
        subtitle.Add(T($"Gender.{sheet.Gender}"));
        _detailSubtitle.Text = string.Join(" · ", subtitle);

        _details.SuspendLayout();
        _details.Rows.Clear();

        var showValidated = _showValidated.Checked;

        foreach (var row in sheet.Rows)
        {
            _rowsById[row.TestResultId] = row;

            if (!ValidationScreenRules.IsListed(row, showValidated)) continue;

            _listed.Add(row);

            var releasable = ValidationScreenRules.IsReleasable(row);
            var withdrawable = ValidationScreenRules.IsWithdrawable(row, showValidated);

            var index = _details.Rows.Add(
                // Pre-ticked: the common case is releasing the whole file, and the biologist
                // unticks what they want to hold back.
                ticks is not null && ticks.TryGetValue(row.TestResultId, out var ticked)
                    ? ticked
                    : ValidationScreenRules.IsPreTicked(row),
                new string(' ', row.IndentLevel * 4) + row.ParameterName.For(Localizer.CurrentLanguage),
                row.IsHeading ? string.Empty : row.CurrentValue ?? string.Empty,
                // The text behind the painted pill, for a print of the grid and a screen reader.
                UiTheme.FlagGlyphs(row.Flags) is { Length: > 0 } glyphs
                    ? glyphs
                    : UiTheme.ForInterpretation(row.Interpretation).Glyph,
                row.UnitSymbol ?? string.Empty,
                row.ReferenceRangeText ?? string.Empty,
                row.PreviousValue is { } previous
                    ? $"{previous:N2}" +
                      (row.PreviousDate is { } date ? $"  {date:dd/MM/yy}" : string.Empty) +
                      (row.PreviousImported ? $" · {T("Migration.HistoryMarker")}" : string.Empty)
                    : "—",
                row.InstrumentFlags ?? string.Empty);

            var gridRow = _details.Rows[index];
            gridRow.Tag = row.TestResultId;
            gridRow.Cells[ColumnSelected].ReadOnly = !releasable && !withdrawable;
        }

        _details.ResumeLayout(performLayout: true);

        SetDetailActionsEnabled(_details.Rows.Count > 0);

        var uncommunicated = sheet.Rows.Any(r => r.NeedsCriticalCommunication);

        _communicate.Enabled = uncommunicated;
        _communicate.ForeColor = uncommunicated ? UiTheme.Critical : UiTheme.TextPrimary;
        _communicate.Font = uncommunicated ? UiTheme.BodyBold : UiTheme.Body;

        RenderContext(sheet);
        ShowSelectionCount();
        ShowComment();
    }

    /// <summary>The « Contexte » card of the open file.</summary>
    private void RenderContext(ResultEntrySheet sheet)
    {
        var entry = _entries.FirstOrDefault(e => e.LabOrderId == sheet.LabOrderId);
        var lines = ValidationScreenRules.Context(_listed, sheet.Priority, sheet.DueAt ?? entry?.DueAt,
                                                  DateTimeOffset.Now);

        _contextLines.SuspendLayout();
        _contextLines.Controls.Clear();
        _contextLines.RowStyles.Clear();
        _contextLines.RowCount = lines.Count;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            _contextLines.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var pill = new PillLabel { Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 8, 3) };
            pill.Show(line.Badge, ValidationPaint.Colours(line.Tone));

            var text = new Label
            {
                Text = T(line.Key, line.Args),
                AutoSize = true,
                Font = UiTheme.Body,
                ForeColor = line.Tone == ValidationTone.Critical ? UiTheme.CriticalInk : UiTheme.TextPrimary,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 3, 0, 3),
                MaximumSize = new Size(ContextWidth - 36 - pill.Width - 8, 0),
                UseMnemonic = false
            };

            _contextLines.Controls.Add(pill, 0, i);
            _contextLines.Controls.Add(text, 1, i);
        }

        _contextLines.ResumeLayout(performLayout: true);

        var clinical = string.IsNullOrWhiteSpace(sheet.ClinicalInformation) ? null : sheet.ClinicalInformation.Trim();
        _clinicalText.Text = clinical ?? T("Validation.NotProvided");
        _clinicalText.ForeColor = clinical is null ? UiTheme.TextSecondary : UiTheme.TextPrimary;

        RenderPrescriber(sheet.Prescriber);
    }

    /// <summary>
    /// The « Prescripteur » block: the name, the speciality, then each recorded way of reaching
    /// them — the critical line first and in red, as it is the one to dial for a critical value.
    /// A value not recorded is not listed; a prescriber typed on the order has a name only.
    /// </summary>
    private void RenderPrescriber(OrderPrescriber? prescriber)
    {
        var language = Localizer.CurrentLanguage;
        var name = PrescriberContactRules.DisplayName(prescriber, language);

        _prescriber.SuspendLayout();
        ClearPrescriber();

        var row = 0;
        void Whole(Control control)
        {
            _prescriber.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _prescriber.Controls.Add(control, 0, row);
            _prescriber.SetColumnSpan(control, 3);
            row++;
        }

        Label Line(string text, Font font, Color colour, int width) => new()
        {
            Text = text,
            AutoSize = true,
            Font = font,
            ForeColor = colour,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 2, 0, 2),
            MaximumSize = new Size(width, 0),
            UseMnemonic = false
        };

        var caption = ValidationPaint.Caption();
        caption.Text = T("Validation.Prescriber");
        Whole(caption);

        Whole(Line(name ?? T("Validation.NotProvided"), name is null ? UiTheme.Body : UiTheme.BodyBold,
                   name is null ? UiTheme.TextSecondary : UiTheme.TextPrimary, ContextWidth - 36));

        var speciality = prescriber?.Speciality?.For(language);
        if (!string.IsNullOrWhiteSpace(speciality))
            Whole(Line(speciality.Trim(), UiTheme.Small, UiTheme.TextSecondary, ContextWidth - 36));

        foreach (var contact in PrescriberContactRules.Contacts(prescriber))
        {
            var critical = contact.Kind == PrescriberContactKind.CriticalLine;
            _prescriber.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var label = Line(T(ContactKey(contact.Kind)), critical ? UiTheme.SmallBold : UiTheme.Small,
                             critical ? UiTheme.CriticalInk : UiTheme.TextSecondary, 120);
            label.Margin = new Padding(0, 4, 8, 4);

            var value = Line(contact.Value, critical ? UiTheme.BodyBold : UiTheme.Body,
                             critical ? UiTheme.CriticalInk : UiTheme.TextPrimary, ContextWidth - 36 - 120);
            // A number reads left to right in Arabic too.
            value.RightToLeft = RightToLeft.No;

            _prescriber.Controls.Add(label, 0, row);
            _prescriber.Controls.Add(value, 1, row);
            _prescriber.Controls.Add(CopyButton(contact.Value), 2, row);
            row++;
        }

        _prescriber.RowCount = row;
        _prescriber.ResumeLayout(performLayout: true);
    }

    private static string ContactKey(PrescriberContactKind kind) => kind switch
    {
        PrescriberContactKind.CriticalLine => "Validation.PrescriberCriticalLine",
        PrescriberContactKind.Phone => "Validation.PrescriberPhone",
        PrescriberContactKind.Mobile => "Validation.PrescriberMobile",
        PrescriberContactKind.Email => "Validation.PrescriberEmail",
        _ => "Validation.PrescriberFax"
    };

    /// <summary>A small button copying one value, which the status line then confirms.</summary>
    private Button CopyButton(string value)
    {
        var button = new Button
        {
            FlatStyle = FlatStyle.Flat,
            Size = new Size(26, 24),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(4, 1, 0, 1),
            BackColor = UiTheme.SurfaceRaised,
            Cursor = Cursors.Hand,
            AccessibleName = $"{T("Validation.PrescriberCopy")} {value}",
            UseMnemonic = false
        };
        button.FlatAppearance.BorderSize = 0;

        _copyIcon ??= Icons.Render(Glyph.CopyPlus, UiTheme.TextSecondary, ButtonIcons.Size(this));
        if (_copyIcon is not null)
        {
            button.Image = _copyIcon;
        }
        else
        {
            // Without the icon font, the word.
            button.AutoSize = true;
            button.Font = UiTheme.Small;
            button.Text = T("Validation.PrescriberCopy");
        }

        _tips.SetToolTip(button, T("Validation.PrescriberCopy"));
        button.Click += (_, _) => CopyToClipboard(value);
        return button;
    }

    private void CopyToClipboard(string value)
    {
        try
        {
            Clipboard.SetText(value);
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Validation.PrescriberCopied", value);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard held by another application: the biologist can click again.
        }
    }

    /// <summary>Empties the block; its controls are the screen's own, so they are disposed (the shared icon is not).</summary>
    private void ClearPrescriber()
    {
        var old = _prescriber.Controls.Cast<Control>().ToList();
        _prescriber.Controls.Clear();
        _prescriber.RowStyles.Clear();
        _prescriber.RowCount = 0;

        foreach (var control in old)
        {
            if (control is Button button) button.Image = null;
            _tips.SetToolTip(control, null);
            control.Dispose();
        }
    }

    private void ClearDetails()
    {
        _sheet = null;
        _rowsById.Clear();
        _listed.Clear();
        _details.Rows.Clear();
        _detailHeading.Text = T("App.NoData");
        _detailSubtitle.Text = string.Empty;
        _selectionCount.Text = string.Empty;
        _contextLines.Controls.Clear();
        ClearPrescriber();
        _clinicalText.Text = string.Empty;
        _communicate.ForeColor = UiTheme.TextPrimary;
        _communicate.Font = UiTheme.Body;
        SetDetailActionsEnabled(false);
        ShowComment();
        ShowValidateCaption(0);
    }

    /// <summary>Whether the open file has a validated value for a report to carry (see the rule).</summary>
    private bool CanIssueReport() =>
        _sheet is { } sheet && ValidationScreenRules.CanIssueReport(sheet.Rows);

    private void SetDetailActionsEnabled(bool enabled)
    {
        _validate.Enabled = enabled;
        _release.Enabled = enabled && CanIssueReport();
        if (!enabled) _communicate.Enabled = false;
        _history.Enabled = enabled && ResultHistory.IsCharted(SelectedDetail());
        UpdateMenu();
    }

    /// <summary>
    /// The menu's items, enabled as their buttons were. « Dévalider » is offered only when something
    /// on the sheet has actually been validated — retracting nothing is not a meaningful action —
    /// and only to a user holding the right to.
    /// </summary>
    private void UpdateMenu()
    {
        var actions = PermissionGate.User is { } user
            ? ValidationScreenRules.Actions([.. _rowsById.Values], _details.Rows.Count, SelectedDetail(), user)
            : ValidationActions.None;

        _selectAll.Enabled = actions.SelectAll;
        _unvalidate.Enabled = actions.Unvalidate;
    }

    /// <summary>« 14 sur 16 sélectionnés » and « Valider la sélection (14) ».</summary>
    private void ShowSelectionCount()
    {
        var selection = ValidationScreenRules.Selection(_listed, IsTicked, _showValidated.Checked);

        _selectionCount.Text = selection.Tickable == 0
            ? string.Empty
            : T("Validation.SelectedCount", selection.Ticked, selection.Tickable);
        ShowValidateCaption(selection.ToValidate);
    }

    private void ShowValidateCaption(int count)
    {
        _validate.Text = T("Validation.ValidateSelection", count);
        _validate.AccessibleName = _validate.Text;
    }

    // ---- The biologist's comment ----------------------------------------------------------------------

    /// <summary>
    /// Shows the comment of the line under the cursor. Only a line awaiting release takes one: it is
    /// written onto the result when that line is validated, and printed under the value.
    /// </summary>
    private void ShowComment()
    {
        var row = SelectedDetail();
        var editable = PermissionGate.User is { } user && ValidationScreenRules.TakesComment(row, user);

        _showingComment = true;
        try
        {
            _commentRowId = editable ? row!.TestResultId : null;
            _comment.Box.Text = editable && _comments.TryGetValue(row!.TestResultId, out var text) ? text : string.Empty;
            _comment.Box.Enabled = editable;
            _comment.Box.PlaceholderText = editable ? T("Validation.BiologistCommentHint") : string.Empty;
            _commentCaption.Text = editable
                ? $"{T("Validation.BiologistComment")} · {row!.ParameterName.For(Localizer.CurrentLanguage)}"
                : T("Validation.BiologistComment");
        }
        finally
        {
            _showingComment = false;
        }
    }

    private void KeepComment()
    {
        if (_showingComment || _commentRowId is not { } id) return;

        var text = _comment.Box.Text.Trim();
        var had = _comments.ContainsKey(id);

        if (text.Length == 0) _comments.Remove(id);
        else _comments[id] = text;

        // The ✎ on the parameter appears or goes with the first or last letter.
        if (had != _comments.ContainsKey(id)) _details.InvalidateColumn(_details.Column(ColumnParameter).Index);
    }

    /// <summary>Comments typed and not yet written onto a validated result are lost on leaving: asked first.</summary>
    public override bool ConfirmLeave() =>
        _comments.Count == 0 || _dialogs.Confirm(T("Validation.CommentsNotSaved", _comments.Count), T("Nav.Validation"));

    private async Task OpenOrderAsync()
    {
        if (_sheet is not { } sheet || !PermissionGate.Allows(Permissions.Orders.View)) return;

        var orderId = sheet.LabOrderId;
        await _navigation.NavigateToAsync<OrderDetailView>(view => view.OrderId = orderId);
    }

    // ---- Antériorités ---------------------------------------------------------------------------------

    /// <summary>The sheet row under the detail grid's cursor, or null.</summary>
    private ResultEntryRow? SelectedDetail() =>
        _details.CurrentRow?.Tag is Guid id && _rowsById.TryGetValue(id, out var row) ? row : null;

    /// <summary>
    /// Opens the chart of the selected parameter's validated values, the value awaiting this
    /// biologist's signature drawn as the current point. Read in the selection queue: the detail
    /// loads share this screen's context, which refuses a second query while one runs.
    /// </summary>
    private async Task ShowHistoryAsync()
    {
        if (_sheet is not { } sheet) return;
        if (SelectedDetail() is not { } row || !ResultHistory.IsCharted(row)) return;

        IReadOnlyList<ResultHistoryPoint>? points = null;

        async Task FetchAsync()
        {
            var history = await _entry.GetHistoryAsync(sheet.LabOrderId, row.TestParameterId);

            if (history.IsFailure)
            {
                _dialogs.ShowFailure(history);
                return;
            }

            points = history.Value;
        }

        try
        {
            // Busy for the read only: an action in flight on the same context is not raced, and
            // a second click while the read runs is ignored.
            if (!await RunBusyAsync(() => _selectionLoads.RunAsync(FetchAsync))) return;
        }
        catch (OperationCanceledException)
        {
            // Superseded.
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the history of parameter {ParameterId} failed.", row.TestParameterId);
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
            return;
        }

        if (points is null) return;

        using var dialog = new ResultHistoryDialog(Localizer, row.ParameterName.For(Localizer.CurrentLanguage),
                                                   ResultHistory.SeriesFor(row, points, sheet.AccessionNumber));
        dialog.ShowDialog(FindForm());
    }

    private void ToggleAll()
    {
        // Selects what awaits release; released rows are ticked one by one, on purpose.
        var (rows, tick) = ValidationScreenRules.ToggleAll(_listed, IsTicked, _showValidated.Checked);

        foreach (var id in rows)
            if (GridRowOf(id) is { } gridRow) gridRow.Cells[ColumnSelected].Value = tick;
    }

    /// <summary>Whether the grid line of a result is ticked.</summary>
    private bool IsTicked(Guid id) => GridRowOf(id)?.Cells[ColumnSelected].Value is true;

    private DataGridViewRow? GridRowOf(Guid id) =>
        _details.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is Guid tag && tag == id);

    private string? CommentOf(Guid id) => _comments.TryGetValue(id, out var text) ? text : null;

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
        // The menu item is not a gated control: the right is checked again here, before the
        // reason is asked for. The service refuses in any case.
        if (!PermissionGate.Allows(Permissions.Results.Unvalidate)) return;

        // The file the operator is looking at, held locally: _sheet follows the selection.
        if (_sheet is not { } sheet) return;

        _details.EndEdit();

        // Only a released result has a validation to withdraw; the ticked rows awaiting
        // release are what the Validate button is for.
        var ids = ValidationScreenRules.Withdrawals(_listed, IsTicked);

        if (ids.Count == 0)
        {
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Message.NothingSelected");
            return;
        }

        var reason = _dialogs.PromptForReason(
            T("Validation.UnvalidatePrompt", ids.Count), T("Validation.Unvalidate"),
            minimumLength: 15, maxLength: 500);

        if (reason is null) return;

        var result = await _validation.UnvalidateAsync(sheet.LabOrderId, ids, reason);

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
        if (_sheet is not { } sheet) return;

        _details.EndEdit();

        var decisions = ValidationScreenRules.Decisions(_listed, IsTicked, CommentOf);
        var qcAffected = ValidationScreenRules.QcAffected(_listed, IsTicked)
            .Select(r => r.ParameterName.For(Localizer.CurrentLanguage))
            .ToList();

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
                minimumLength: 15, maxLength: 1000);

            if (qcJustification is null) return;

            decisions = ValidationScreenRules.WithQcJustification(decisions, _rowsById.Values, qcJustification);
        }

        _validate.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var outcome = await _validation.ValidateAsync(sheet.LabOrderId, decisions);

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
            // « N validé par » ended on nothing (and read « 0 validé par » after a refusal): the count
            // gets its own sentence.
            _status.Text = T("Validation.ValidatedCount", result.Validated);

            // The comments of the lines now validated are on their results; the others stay.
            foreach (var decision in decisions.Where(d => d.Validate && d.BiologistComment is not null))
                _comments.Remove(decision.TestResultId);

            await ActivateAsync();
        }
        finally
        {
            Cursor = Cursors.Default;
            // As the reload left it: once the last file is validated the list is empty, no file
            // is open, and « Valider » stays grey.
            _validate.Enabled = _sheet is not null && _details.Rows.Count > 0;
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
        if (_sheet is not { } sheet) return;

        // Only the values not yet communicated: one already read back must not be called again.
        var critical = ValidationScreenRules.CriticalToCommunicate(sheet.Rows);

        if (critical.Count == 0) return;

        // Whom the value is phoned to, and the number recorded against the call: the one the
        // « Prescripteur » block shows first (PrescriberContactRules).
        var prescriber = sheet.Prescriber;
        var contactUsed = PrescriberContactRules.ContactUsed(null, prescriber, NotificationChannel.Telephone);
        var whom = string.Join(" — ", new[] { PrescriberContactRules.DisplayName(prescriber, Localizer.CurrentLanguage), contactUsed }
                                          .Where(part => part is not null));
        var callLine = whom.Length > 0 ? $"{T("Validation.CriticalCallWhom", whom)}\n\n" : string.Empty;

        foreach (var row in critical)
        {
            var parameterName = row.ParameterName.For(Localizer.CurrentLanguage);
            var value = $"{row.CurrentValue} {row.UnitSymbol}".Trim();

            var raised = await _validation.RaiseCriticalNotificationAsync(
                row.TestResultId, parameterName, value,
                NotificationChannel.Telephone, contactUsed, prescriber?.PrescriberId,
                displayedEnteredAt: row.EnteredAt);

            if (raised.IsFailure)
            {
                _dialogs.ShowFailure(raised);
                return;
            }

            // Within the notification's columns: a longer name or read-back failed the save and
            // left the call open with the value unreleasable.
            var recipient = _dialogs.PromptForText(
                $"{T("App.LabelValue", parameterName, value)}\n\n{callLine}{T("Notification_RecipientNameRequired")}",
                T("Nav.CriticalResults"), maxLength: 200);

            if (recipient is null)
            {
                await _validation.MarkCriticalUnreachableAsync(
                    raised.Value, CriticalCallNotes.Abandoned);
                continue;
            }

            var readBack = _dialogs.PromptForText(
                $"{T("Validation.ReadBackPrompt")}\n\n{parameterName}",
                T("Nav.CriticalResults"), maxLength: 100);

            if (string.IsNullOrWhiteSpace(readBack))
            {
                _dialogs.Warn(T("Notification_ReadBackRequired"), T("Nav.CriticalResults"));
                await _validation.MarkCriticalUnreachableAsync(
                    raised.Value, CriticalCallNotes.ReadBackNotConfirmed);
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
        // The file named in the confirmation is the file released. _sheet was read again after the
        // modal, and a selection load finishing behind it had replaced it with the next file.
        if (_sheet is not { } sheet) return;

        // A question, not the bare file number: Yes/No under « Compte rendu — 2610-000007 » did
        // not say that answering Yes signs and issues it.
        if (!_dialogs.Confirm(T("Report.ConfirmIssue", sheet.AccessionNumber), T("Report.Issue"))) return;

        _release.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var released = await _reports.ReleaseAsync(sheet.LabOrderId);

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
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
        finally
        {
            Cursor = Cursors.Default;
            _release.Enabled = CanIssueReport();
        }
    }
}
