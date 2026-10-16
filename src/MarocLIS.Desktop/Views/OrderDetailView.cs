using System.ComponentModel;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Orders;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// One order, in full, with everything that can still be done to it.
/// <para>
/// This is the screen an operator reaches for when something has changed since the counter: the
/// prescriber telephones to add a TSH, the clerk typed the wrong prescriber, an analyser gave an
/// implausible value and the analysis must be re-run, the patient left before the draw. Without it
/// the only way to record any of those is to register a second order — which splits one episode of
/// care across two file numbers, bills the draw twice, and makes the turnaround figures fiction.
/// </para>
/// <para>
/// Laid out as the « Consultation d'un dossier » mockup: the site's recent files on the left, newest
/// first, with three quick filters; on the right a card for the file — number and state, the patient,
/// the prescriber, the payer and the clinical information, and a line showing how far the file has
/// got — then a card with the analyses, the tubes, the billing and the history in tabs, and the
/// actions in a bar at the foot. The everyday actions are buttons; cancelling an analysis or the
/// file and re-running an analysis are under « Plus d'actions » and on the analysis's right click.
/// </para>
/// <para>
/// The box filters the list by number, name, CIN or file number as the operator types; Enter still
/// resolves an exact accession number or tube barcode, which is what a scanner sends.
/// </para>
/// </summary>
public sealed class OrderDetailView : LocalizedView
{
    private const int ListWidth = 300;
    private const int ListRowHeight = 54;
    private const int ItemRowHeight = 46;
    private const int HeaderCardHeight = 240;
    private const int CancellationHeight = 24;

    private readonly OrderAmendmentService _orders;
    private readonly ICatalogQueries _catalogQueries;
    private readonly IOrderFileQueries _fileQueries;
    private readonly BillingService _billing;
    private readonly INavigationService _navigation;
    private readonly ISampleLabelRenderer _labels;
    private readonly ILabelPrinter _labelPrinter;
    private readonly IDocumentOutput _documents;
    private readonly IUserDialogs _dialogs;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly Application.Samples.SampleWorkflowService _samplesWorkflow;
    private readonly Application.Reports.ReportReleaseService _reports;
    private readonly ILogger<OrderDetailView>? _logger;
    private readonly ToolTip _tips = new();

    // ---- the files (left panel) ----
    private readonly TextBox _search = UiTheme.Input(240);
    private readonly FlowLayoutPanel _chips = new();
    private readonly ChipButton _chipWeek = new();
    private readonly ChipButton _chipActive = new();
    private readonly ChipButton _chipUrgent = new();
    private readonly Label _listCount = new();
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly DataGridView _list = UiTheme.Grid();
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };

    // ---- the file's header card ----
    private readonly TableLayoutPanel _content = new();
    private readonly Panel _noteBanner = new();
    private readonly Label _note = new();
    private readonly Panel _headerCard = new();
    private readonly Label _accession = new();
    private readonly PillLabel _statusPill = new();
    private readonly PillLabel _priorityPill = new();
    private readonly Label _registered = new();
    private readonly Label _cancellation = new();
    private readonly Label _patient = new();
    private readonly Label _patientInfo = new();
    private readonly Label _patientRecord = new();
    private readonly Label _patientPhone = new();
    private readonly Label[] _factCaptions = [new(), new(), new(), new()];
    private readonly Label[] _factValues = [new(), new(), new(), new()];
    private readonly Label[] _factNotes = [new(), new(), new(), new()];
    private readonly FileProgressLine _progress = new();
    private readonly TableLayoutPanel _headerLayout = new();

    // ---- the content card: analyses, tubes, billing, history ----
    private readonly FileTabStrip _tabs = new();
    private readonly Panel[] _pages = [new(), new(), new(), new()];
    private readonly DataGridView _items = UiTheme.Grid();
    private readonly DataGridView _samples = UiTheme.Grid();
    private readonly FlowLayoutPanel _tubeLegend = new();
    private readonly Label _totals = new();
    private readonly Label[] _billingCaptions = [new(), new(), new(), new()];
    private readonly Label[] _billingValues = [new(), new(), new(), new()];
    private readonly Label[] _billingNotes = [new(), new(), new(), new()];
    private readonly Label _paymentsTitle = new();
    private readonly Label _paymentsNote = new();
    private readonly DataGridView _payments = UiTheme.Grid();
    private readonly Button _openBilling = UiTheme.SecondaryButton(string.Empty);
    private readonly DataGridView _history = UiTheme.Grid();
    private readonly Label _historyNote = new();
    private readonly ContextMenuStrip _itemMenu = new();
    private readonly ToolStripMenuItem _itemCancel = new();
    private readonly ToolStripMenuItem _itemRepeat = new();

    // ---- action bar ----
    private readonly Button _addAnalyses = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _printLabels = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _printReport = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _editHeader = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _more = UiTheme.SecondaryButton(string.Empty);
    private readonly ContextMenuStrip _moreMenu = new();
    private readonly ToolStripMenuItem _cancelItem = new();
    private readonly ToolStripMenuItem _repeatItem = new();
    private readonly ToolStripMenuItem _cancelOrder = new();
    private readonly ToolStripMenuItem _editHeaderItem = new();
    private readonly ToolStripMenuItem _printReportItem = new();
    private TableLayoutPanel? _bar;
    private readonly Panel _statusStrip = new();
    private readonly Label _status = new();

    private OrderDetail? _detail;
    private IReadOnlyList<OrderListRow> _rows = [];
    private IReadOnlyList<OrderListRow> _shown = [];
    private IReadOnlyList<OrderFileEvent> _events = [];
    private IReadOnlyList<PaymentRow> _paymentRows = [];

    /// <summary>
    /// Set while the list is being repopulated or its selection moved in code, so that the
    /// selection events this raises are not taken for the operator picking a row — which would
    /// reload, and log a consultation of, a file nobody asked to open.
    /// </summary>
    private bool _populatingList;

    // One load of each kind in flight at a time: a newer filter or a quicker arrow key supersedes
    // the older request, which would otherwise finish last and overwrite what is on screen.
    private CancellationTokenSource? _listLoad;
    private CancellationTokenSource? _detailLoad;

    /// <summary>
    /// Set by the caller before activation, to open a particular file. The view also has its own
    /// list and search box, so it is usable from the sidebar without a prior selection.
    /// <para>
    /// Hidden from designer serialization: the screens here are built in code, so nothing would
    /// consume a designer-written value.
    /// </para>
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Guid? OrderId { get; set; }

    public OrderDetailView(OrderAmendmentService orders, ICatalogQueries catalogQueries,
                           IOrderFileQueries fileQueries, BillingService billing,
                           INavigationService navigation,
                           Application.Samples.SampleWorkflowService samplesWorkflow,
                           Application.Reports.ReportReleaseService reports,
                           ISampleLabelRenderer labels, ILabelPrinter labelPrinter,
                           IDocumentOutput documents, IUserDialogs dialogs,
                           ICurrentUser currentUser, IClock clock,
                           ILocalizer localizer, ICultureManager cultureManager,
                           ILogger<OrderDetailView>? logger = null)
        : base(localizer, cultureManager)
    {
        _orders = orders;
        _catalogQueries = catalogQueries;
        _fileQueries = fileQueries;
        _billing = billing;
        _navigation = navigation;
        _samplesWorkflow = samplesWorkflow;
        _reports = reports;
        _labels = labels;
        _labelPrinter = labelPrinter;
        _documents = documents;
        _dialogs = dialogs;
        _currentUser = currentUser;
        _clock = clock;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title =>
        _detail is null ? T("Order.Title") : $"{T("Order.Title")} {_detail.AccessionNumber}";

    private bool IsRightToLeft => RightToLeft == RightToLeft.Yes;

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(0);

        // Two columns, the files then the file. A table rather than docking: it mirrors by itself
        // in Arabic, the files going to the right.
        var columns = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ListWidth));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        columns.Controls.Add(BuildListPanel(), 0, 0);
        columns.Controls.Add(BuildFilePanel(), 1, 0);

        Controls.Add(columns);
    }

    /// <summary>The search box, the quick filters, the count and the files.</summary>
    private TableLayoutPanel BuildListPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 1, 0),
            ColumnCount = 1,
            RowCount = 4,
            BackColor = UiTheme.SurfaceRaised
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // The line between the files and the file, on whichever side the file is.
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

        // An accession number, a barcode or a CIN is a Latin token; the box stays LTR in Arabic.
        // First in the tab order, so the shell's Ctrl+F lands in it.
        _search.Tag = UiTags.LatinValue;
        _search.Font = UiTheme.Body;
        _search.Dock = DockStyle.Fill;
        _search.Margin = new Padding(14, 14, 14, 8);
        _search.TabIndex = 0;

        _chips.Dock = DockStyle.Fill;
        _chips.AutoSize = true;
        _chips.WrapContents = true;
        _chips.Margin = new Padding(14, 0, 14, 2);
        _chips.BackColor = UiTheme.SurfaceRaised;
        _chips.Controls.AddRange([_chipWeek, _chipActive, _chipUrgent]);

        var countRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(14, 0, 10, 6),
            BackColor = UiTheme.SurfaceRaised
        };
        countRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        countRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _listCount.Font = UiTheme.Small;
        _listCount.ForeColor = UiTheme.TextSecondary;
        _listCount.AutoSize = false;
        _listCount.AutoEllipsis = true;
        _listCount.Dock = DockStyle.Fill;
        _listCount.TextAlign = ContentAlignment.MiddleLeft;
        _listCount.Margin = new Padding(0);

        // Icon only: the screen refreshes on F5 as well, and the panel is narrow.
        _refresh.Size = new Size(30, 28);
        _refresh.Padding = new Padding(0);
        _refresh.Margin = new Padding(0);
        _refresh.TabStop = false;
        ButtonIcons.Set(_refresh, Glyph.RefreshCw, always: true);

        countRow.Controls.Add(_listCount, 0, 0);
        countRow.Controls.Add(_refresh, 1, 0);

        BuildListColumns();
        _list.ColumnHeadersVisible = false;
        _list.CellBorderStyle = DataGridViewCellBorderStyle.None;
        _list.RowTemplate.Height = ListRowHeight;
        _list.Margin = new Padding(0);
        _list.ShowCellToolTips = true;
        // Ctrl+P prints this list, as it did when it was the screen's largest grid.
        _list.Tag = ListPrinting.MainList;

        panel.Controls.Add(_search, 0, 0);
        panel.Controls.Add(_chips, 0, 1);
        panel.Controls.Add(countRow, 0, 2);
        panel.Controls.Add(_list, 0, 3);

        return panel;
    }

    /// <summary>The note, the header card, the content card, the status line and the action bar.</summary>
    private Panel BuildFilePanel()
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
            Padding = new Padding(20, 11, 20, 11),
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        strip.Controls.Add(bar);
        _bar = bar;
        bar.Resize += (_, _) => FitBar();

        foreach (var button in new[] { _addAnalyses, _printLabels, _printReport, _editHeader, _more })
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(0, 34);
            button.Height = 34;
            button.Margin = new Padding(0, 0, 8, 0);
        }

        _more.Margin = new Padding(0);

        // The key a button answers to, in small grey type after its caption.
        ShortcutHints.Attach(_printLabels, "F8");

        // « Plus d'actions ▾ »: the chevron drawn after the caption, where a font may lack the glyph.
        _more.Paint += PaintMoreChevron;
        _more.RightToLeftChanged += (_, _) =>
            _more.Padding = IsRightToLeft ? new Padding(26, 0, 12, 0) : new Padding(12, 0, 26, 0);
        _more.Padding = new Padding(12, 0, 26, 0);

        bar.Controls.Add(_addAnalyses, 0, 0);
        bar.Controls.Add(_printLabels, 1, 0);
        bar.Controls.Add(_printReport, 2, 0);
        bar.Controls.Add(_editHeader, 3, 0);
        bar.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = UiTheme.SurfaceRaised }, 4, 0);
        bar.Controls.Add(_more, 5, 0);

        // The actions that undo something, one click further away than the everyday ones; each
        // keeps the icon its button had.
        // « Modifier l'en-tête », then « Compte rendu (PDF) », join them only when the bar is too
        // narrow for their buttons (a 1366-pixel screen).
        _moreMenu.Items.AddRange([_editHeaderItem, _printReportItem, _cancelItem, _repeatItem,
                                  new ToolStripSeparator(), _cancelOrder]);
        _editHeaderItem.Visible = _printReportItem.Visible = false;
        _editHeaderItem.Image = Icons.Render(Glyph.Pencil, UiTheme.TextPrimary, 16);
        _printReportItem.Image = Icons.Render(Glyph.FileText, UiTheme.TextPrimary, 16);
        _moreMenu.Font = UiTheme.Body;
        _itemMenu.Items.AddRange([_itemCancel, _itemRepeat]);
        _itemMenu.Font = UiTheme.Body;
        _cancelItem.Image = _itemCancel.Image = Icons.Render(Glyph.X, UiTheme.TextPrimary, 16);
        _repeatItem.Image = _itemRepeat.Image = Icons.Render(Glyph.Repeat, UiTheme.TextPrimary, 16);
        _cancelOrder.Image = Icons.Render(Glyph.Ban, UiTheme.TextPrimary, 16);

        // --- status line ------------------------------------------------------------------------------
        // What the last action said — added, cancelled, refused. Shown above the bar only while there
        // is something to say.

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

        _content.Dock = DockStyle.Fill;
        _content.ColumnCount = 1;
        _content.RowCount = 3;
        _content.Padding = new Padding(24, 16, 24, 14);
        _content.Margin = new Padding(0);
        _content.BackColor = UiTheme.Surface;
        _content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _content.RowStyles.Add(new RowStyle(SizeType.Absolute, HeaderCardHeight));
        _content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _content.Controls.Add(BuildNoteBanner(), 0, 0);
        _content.Controls.Add(BuildHeaderCard(), 0, 1);
        _content.Controls.Add(BuildContentCard(), 0, 2);

        host.Controls.Add(_content);
        host.Controls.Add(_statusStrip);
        host.Controls.Add(strip);

        return host;
    }

    /// <summary>The file's internal note, in the warning colours above the cards: « ! note ».</summary>
    private Panel BuildNoteBanner()
    {
        _noteBanner.Dock = DockStyle.Fill;
        _noteBanner.Height = 28;
        _noteBanner.Margin = new Padding(0, 0, 0, 12);
        _noteBanner.Padding = new Padding(12, 0, 12, 0);
        _noteBanner.BackColor = UiTheme.AbnormalBackground;
        _noteBanner.Visible = false;
        _noteBanner.Tag = UiTags.OwnLayout;

        _note.Dock = DockStyle.Fill;
        _note.AutoSize = false;
        _note.AutoEllipsis = true;
        _note.Font = UiTheme.SmallBold;
        _note.ForeColor = UiTheme.Abnormal;
        _note.BackColor = UiTheme.AbnormalBackground;
        _note.TextAlign = ContentAlignment.MiddleLeft;
        _note.UseMnemonic = false;

        _noteBanner.Controls.Add(_note);
        return _noteBanner;
    }

    private Panel BuildHeaderCard()
    {
        _headerCard.Dock = DockStyle.Fill;
        _headerCard.Margin = new Padding(0, 0, 0, 14);
        _headerCard.Padding = new Padding(16, 12, 16, 10);
        _headerCard.BackColor = UiTheme.SurfaceRaised;
        _headerCard.Visible = false;
        UiChrome.AsCard(_headerCard);

        _headerLayout.Dock = DockStyle.Fill;
        _headerLayout.ColumnCount = 1;
        _headerLayout.RowCount = 7;
        _headerLayout.Margin = new Padding(0);
        _headerLayout.BackColor = UiTheme.SurfaceRaised;
        _headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _headerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        _headerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _headerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        _headerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 13));
        _headerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        _headerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 13));
        _headerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // --- number, state, priority, registration ------------------------------------------------

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _accession.Font = UiTheme.Mono(15f, FontStyle.Bold);
        _accession.ForeColor = UiTheme.TextPrimary;
        _accession.AutoSize = true;
        _accession.Anchor = AnchorStyles.Left;
        _accession.Margin = new Padding(0, 0, 10, 0);
        _accession.UseMnemonic = false;

        foreach (var pill in new[] { _statusPill, _priorityPill })
        {
            pill.Font = UiTheme.SmallBold;
            pill.Anchor = AnchorStyles.Left;
            pill.Margin = new Padding(0, 0, 8, 0);
        }

        _registered.Font = UiTheme.Body;
        _registered.ForeColor = UiTheme.TextSecondary;
        _registered.AutoSize = false;
        _registered.AutoEllipsis = true;
        _registered.Dock = DockStyle.Fill;
        _registered.TextAlign = ContentAlignment.MiddleRight;
        _registered.UseMnemonic = false;

        top.Controls.Add(_accession, 0, 0);
        top.Controls.Add(_statusPill, 1, 0);
        top.Controls.Add(_priorityPill, 2, 0);
        top.Controls.Add(_registered, 3, 0);

        // --- the cancellation, when the file is cancelled ------------------------------------------

        _cancellation.Dock = DockStyle.Fill;
        _cancellation.AutoSize = false;
        _cancellation.AutoEllipsis = true;
        _cancellation.Font = UiTheme.SmallBold;
        _cancellation.ForeColor = UiTheme.CriticalInk;
        _cancellation.BackColor = UiTheme.CriticalBackground;
        _cancellation.TextAlign = ContentAlignment.MiddleLeft;
        _cancellation.Padding = new Padding(8, 0, 8, 0);
        _cancellation.Margin = new Padding(0, 0, 0, 2);
        _cancellation.UseMnemonic = false;

        // --- the patient ----------------------------------------------------------------------------

        var patientLine = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };

        _patient.Font = EntryPaint.Resized(UiTheme.SectionTitle, 12f);
        _patient.ForeColor = UiTheme.AccentInk;
        _patient.Margin = new Padding(0, 2, 12, 0);
        _patientInfo.Font = UiTheme.Body;
        _patientInfo.ForeColor = UiTheme.TextPrimary;
        _patientInfo.Margin = new Padding(0, 5, 12, 0);

        foreach (var label in new[] { _patientRecord, _patientPhone })
        {
            label.Font = UiTheme.Body;
            label.ForeColor = UiTheme.TextSecondary;
            label.Margin = new Padding(0, 5, 12, 0);
        }

        foreach (var label in new[] { _patient, _patientInfo, _patientRecord, _patientPhone })
        {
            label.AutoSize = true;
            label.UseMnemonic = false;
        }

        patientLine.Controls.AddRange([_patient, _patientInfo, _patientRecord, _patientPhone]);

        // --- prescriber, prescription, payer, clinical information ----------------------------------

        var facts = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        for (var i = 0; i < 4; i++) facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        facts.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        for (var i = 0; i < 4; i++)
            facts.Controls.Add(Fact(_factCaptions[i], _factValues[i], _factNotes[i]), i, 0);

        _progress.Dock = DockStyle.Fill;
        _progress.Margin = new Padding(0);

        _headerLayout.Controls.Add(top, 0, 0);
        _headerLayout.Controls.Add(_cancellation, 0, 1);
        _headerLayout.Controls.Add(patientLine, 0, 2);
        _headerLayout.Controls.Add(Rule(), 0, 3);
        _headerLayout.Controls.Add(facts, 0, 4);
        _headerLayout.Controls.Add(Rule(), 0, 5);
        _headerLayout.Controls.Add(_progress, 0, 6);

        _headerCard.Controls.Add(_headerLayout);
        return _headerCard;
    }

    /// <summary>A hairline across the card, centred in its row.</summary>
    private static FileRule Rule() => new FileRule { Dock = DockStyle.Top, Margin = new Padding(0, 6, 0, 6) };

    /// <summary>A caption in small muted type, its value in semibold, and a note under it.</summary>
    private static TableLayoutPanel Fact(Label caption, Label value, Label note)
    {
        var cell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0, 0, 16, 0),
            BackColor = UiTheme.SurfaceRaised
        };
        cell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cell.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        cell.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        cell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        caption.Font = UiTheme.Small;
        caption.ForeColor = UiTheme.TextSecondary;
        value.Font = UiTheme.BodyBold;
        value.ForeColor = UiTheme.TextPrimary;
        note.Font = UiTheme.Small;
        note.ForeColor = UiTheme.TextSecondary;

        foreach (var label in new[] { caption, value, note })
        {
            label.Dock = DockStyle.Fill;
            label.AutoSize = false;
            label.AutoEllipsis = true;
            label.Margin = new Padding(0);
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.UseMnemonic = false;
        }

        cell.Controls.Add(caption, 0, 0);
        cell.Controls.Add(value, 0, 1);
        cell.Controls.Add(note, 0, 2);
        return cell;
    }

    /// <summary>The tabs, and under them the analyses, the tubes, the billing or the history.</summary>
    private Panel BuildContentCard()
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(1),
            BackColor = UiTheme.SurfaceRaised,
            MinimumSize = new Size(0, 180)
        };
        UiChrome.AsCard(card);

        _tabs.Dock = DockStyle.Top;

        var pagesHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceRaised, Margin = new Padding(0) };

        foreach (var page in _pages)
        {
            page.Dock = DockStyle.Fill;
            page.BackColor = UiTheme.SurfaceRaised;
            page.Margin = new Padding(0);
            page.Visible = false;
            page.Tag = UiTags.OwnLayout;
            pagesHost.Controls.Add(page);
        }

        _pages[0].Visible = true;

        BuildAnalysesPage(_pages[0]);
        BuildTubesPage(_pages[1]);
        BuildBillingPage(_pages[2]);
        BuildHistoryPage(_pages[3]);

        card.Controls.Add(pagesHost);
        card.Controls.Add(_tabs);
        return card;
    }

    private void BuildAnalysesPage(Panel page)
    {
        BuildItemColumns();
        _items.RowTemplate.Height = ItemRowHeight;
        _items.ColumnHeadersDefaultCellStyle.Padding = new Padding(12, 6, 12, 6);
        _items.DefaultCellStyle.Padding = new Padding(12, 0, 12, 0);
        _items.ShowCellToolTips = true;

        // The tubes of the file and the totals, under the analyses on the work-surface colour; the
        // totals go to a line of their own when the tubes take the width.
        _tubeLegend.Dock = DockStyle.Bottom;
        _tubeLegend.AutoSize = true;
        _tubeLegend.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _tubeLegend.WrapContents = true;
        _tubeLegend.Padding = new Padding(12, 8, 12, 4);
        _tubeLegend.Margin = new Padding(0);
        _tubeLegend.BackColor = UiTheme.Surface;
        _tubeLegend.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, 0, 0, _tubeLegend.Width, 0);
        };

        _totals.AutoSize = true;
        _totals.Font = UiTheme.Body;
        _totals.ForeColor = UiTheme.TextPrimary;
        _totals.Margin = new Padding(0, 5, 0, 4);
        _totals.UseMnemonic = false;

        var footer = _tubeLegend;
        page.Controls.Add(_items);
        page.Controls.Add(footer);
    }

    private void BuildTubesPage(Panel page)
    {
        BuildSampleColumns();
        _samples.ColumnHeadersDefaultCellStyle.Padding = new Padding(12, 6, 12, 6);
        _samples.DefaultCellStyle.Padding = new Padding(12, 0, 12, 0);
        _samples.RowTemplate.Height = 38;
        page.Controls.Add(_samples);
    }

    private void BuildBillingPage(Panel page)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16, 12, 16, 8),
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var figures = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        for (var i = 0; i < 4; i++) figures.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        figures.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++)
            figures.Controls.Add(Fact(_billingCaptions[i], _billingValues[i], _billingNotes[i]), i, 0);

        var paymentsHead = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 4, 0, 4),
            BackColor = UiTheme.SurfaceRaised
        };
        paymentsHead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        paymentsHead.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        paymentsHead.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _paymentsTitle.Font = UiTheme.SectionTitle;
        _paymentsTitle.ForeColor = UiTheme.TextPrimary;
        _paymentsTitle.AutoSize = true;
        _paymentsTitle.Anchor = AnchorStyles.Left;
        _paymentsTitle.Margin = new Padding(0);

        // To the billing desk, where the file is invoiced, paid or corrected.
        _openBilling.AutoSize = true;
        _openBilling.MinimumSize = new Size(0, 30);
        _openBilling.Anchor = AnchorStyles.Right;
        _openBilling.Margin = new Padding(0);
        ButtonIcons.Set(_openBilling, Glyph.Receipt);

        paymentsHead.Controls.Add(_paymentsTitle, 0, 0);
        paymentsHead.Controls.Add(_openBilling, 1, 0);

        _paymentsNote.AutoSize = true;
        _paymentsNote.Font = UiTheme.Body;
        _paymentsNote.ForeColor = UiTheme.TextSecondary;
        _paymentsNote.Margin = new Padding(0, 0, 0, 6);
        _paymentsNote.UseMnemonic = false;

        BuildPaymentColumns();
        _payments.Margin = new Padding(0);

        layout.Controls.Add(figures, 0, 0);
        layout.Controls.Add(paymentsHead, 0, 1);
        layout.Controls.Add(_paymentsNote, 0, 2);
        layout.Controls.Add(_payments, 0, 3);

        page.Controls.Add(layout);
    }

    private void BuildHistoryPage(Panel page)
    {
        BuildHistoryColumns();
        _history.ShowCellToolTips = true;
        _history.ColumnHeadersDefaultCellStyle.Padding = new Padding(12, 6, 12, 6);
        _history.DefaultCellStyle.Padding = new Padding(12, 0, 12, 0);

        _historyNote.Dock = DockStyle.Top;
        _historyNote.Height = 30;
        _historyNote.Font = UiTheme.Body;
        _historyNote.ForeColor = UiTheme.TextSecondary;
        _historyNote.Padding = new Padding(16, 8, 16, 0);
        _historyNote.Visible = false;

        page.Controls.Add(_history);
        page.Controls.Add(_historyNote);
    }

    private static DataGridViewTextBoxColumn Column(string name, int weight, Font? font = null, bool right = false,
                                                    bool printedHidden = false) =>
        new()
        {
            Name = name,
            FillWeight = weight,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            Visible = !printedHidden,
            Tag = printedHidden ? ListPrinting.PrintedWhenHidden : null,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = font ?? UiTheme.GridBody,
                Alignment = right ? DataGridViewContentAlignment.MiddleRight
                                  : DataGridViewContentAlignment.MiddleLeft
            },
            HeaderCell = { Style = { Alignment = right ? DataGridViewContentAlignment.MiddleRight
                                                       : DataGridViewContentAlignment.MiddleLeft } }
        };

    private void BuildListColumns()
    {
        // One painted column: the name and the time, then the number and the state. The other
        // columns of the former grid stay, hidden, so that Ctrl+P prints the same list as before.
        _list.Columns.Add(Column("accession", 100, UiTheme.Monospace));
        _list.Columns.Add(Column("patient", 28, printedHidden: true));
        _list.Columns.Add(Column("registered", 17, UiTheme.Monospace, printedHidden: true));
        _list.Columns.Add(Column("status", 17, UiTheme.Small, printedHidden: true));
        _list.Columns.Add(Column("priority", 12, UiTheme.Small, printedHidden: true));
        _list.Columns.Add(Column("count", 8, UiTheme.Monospace, right: true, printedHidden: true));
    }

    /// <summary>
    /// The grid moves its fill weights about as columns reach their minimum widths; after a few
    /// widenings (wider figures in Arabic) the name kept a share it no longer had room for and the
    /// grid scrolled sideways. Once the columns overflow, the declared weights are put back.
    /// </summary>
    private void RefitItemColumns()
    {
        var visible = _items.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).ToList();
        if (visible.Sum(c => c.Width) <= _items.DisplayRectangle.Width) return;
        _items.RestoreFillWeights();
    }

    private void BuildItemColumns()
    {
        // The analysis is drawn on two lines — its name, then « CODE · secteur » and the remark —
        // so code, sector and remark keep hidden columns of their own for printing.
        _items.Columns.Add(Column("code", 9, UiTheme.MonospaceBold, printedHidden: true));
        _items.Columns.Add(Column("name", 30));
        _items.Columns.Add(Column("department", 8, UiTheme.Small, printedHidden: true));
        _items.Columns.Add(Column("status", 19, UiTheme.SmallBold));
        _items.Columns.Add(Column("progress", 8, UiTheme.Monospace, right: true));
        _items.Columns.Add(Column("barcode", 16, UiTheme.Monospace));
        _items.Columns.Add(Column("price", 10, UiTheme.Monospace, right: true));
        _items.Columns.Add(Column("share", 10, UiTheme.Monospace, right: true));
        _items.Columns.Add(Column("note", 20, UiTheme.Small, printedHidden: true));

        // The analysis's name is what the operator reads the line by: never « Glycémie à… ».
        foreach (DataGridViewColumn column in _items.Columns)
            if (column.Visible) column.Tag = (int)column.FillWeight;
        _items.Columns["name"]!.MinimumWidth = 160;
        // The name is painted on two lines: marked as wrapping so that the grid's own fitting does
        // not widen it to its longest name and push the figures out of sight.
        _items.Columns["name"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _items.Columns["status"]!.MinimumWidth = 140;
        // The cap's colour and the ten digits of the barcode, never « 0000… ».
        _items.Columns["barcode"]!.MinimumWidth = 140;
    }

    private void BuildSampleColumns()
    {
        _samples.Columns.Add(Column("sequence", 6, UiTheme.Monospace, right: true));
        _samples.Columns.Add(Column("barcode", 13, UiTheme.MonospaceBold));
        _samples.Columns.Add(Column("specimen", 16));
        _samples.Columns.Add(Column("container", 16, UiTheme.Small));
        _samples.Columns.Add(Column("status", 14, UiTheme.SmallBold));
        _samples.Columns.Add(Column("collected", 11, UiTheme.Monospace));
        _samples.Columns.Add(Column("received", 11, UiTheme.Monospace));
        _samples.Columns.Add(Column("storage", 13, UiTheme.Monospace));
    }

    private void BuildPaymentColumns()
    {
        _payments.Columns.Add(Column("date", 12, UiTheme.Monospace));
        _payments.Columns.Add(Column("method", 16));
        _payments.Columns.Add(Column("amount", 12, UiTheme.Monospace, right: true));
        _payments.Columns.Add(Column("status", 12, UiTheme.Small));
        _payments.Columns.Add(Column("by", 18));
        _payments.Columns.Add(Column("reference", 18, UiTheme.Monospace));
    }

    private void BuildHistoryColumns()
    {
        _history.Columns.Add(Column("date", 14, UiTheme.Monospace));
        _history.Columns.Add(Column("user", 16));
        _history.Columns.Add(Column("event", 70));
    }

    // ---- Behaviour ---------------------------------------------------------------------------------

    private void WireBehaviour()
    {
        _search.KeyDown += async (_, e) =>
        {
            // Down moves into the list, so the keyboard alone gets from a typed name to a file.
            if (e.KeyCode == Keys.Down && _list.Rows.Count > 0)
            {
                e.SuppressKeyPress = true;
                _list.Focus();
                return;
            }

            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            _debounce.Stop();
            await SearchAsync();
        };

        // Debounced like the patient search: a query per keystroke while a name is still being
        // typed would be wasted, and 300 ms is short enough to feel immediate.
        _search.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };

        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await LoadListAsync();
        };

        // The quick filters narrow the files already listed; each is a toggle.
        foreach (var chip in new[] { _chipWeek, _chipActive, _chipUrgent })
        {
            chip.Click += (_, _) =>
            {
                chip.Selected = !chip.Selected;
                RenderList();
            };
        }

        _list.SelectionChanged += async (_, _) => await OpenSelectedAsync();
        _list.CellPainting += PaintListCell;

        _refresh.Click += async (_, _) => await ActivateAsync();

        _tabs.SelectedIndexChanged += (_, _) =>
        {
            for (var i = 0; i < _pages.Length; i++) _pages[i].Visible = i == _tabs.SelectedIndex;
        };

        _addAnalyses.Click += async (_, _) => await RunBusyAsync(AddAnalysesAsync);
        _printLabels.Click += async (_, _) => await ReprintLabelsAsync();
        _printReport.Click += async (_, _) => await RunBusyAsync(OpenReportAsync);
        _editHeader.Click += async (_, _) => await RunBusyAsync(EditHeaderAsync);

        _more.Click += (_, _) =>
        {
            RefreshActions();
            _moreMenu.RightToLeft = RightToLeft;
            // The bar sits at the foot of the screen: the menu opens upward, along the button's outer edge.
            _moreMenu.Show(_more, new Point(IsRightToLeft ? 0 : _more.Width, 0),
                           IsRightToLeft ? ToolStripDropDownDirection.AboveRight : ToolStripDropDownDirection.AboveLeft);
        };
        _cancelItem.Click += async (_, _) => await RunBusyAsync(CancelItemAsync);
        _repeatItem.Click += async (_, _) => await RunBusyAsync(RepeatItemAsync);
        _cancelOrder.Click += async (_, _) => await RunBusyAsync(CancelOrderAsync);
        _editHeaderItem.Click += async (_, _) => await RunBusyAsync(EditHeaderAsync);
        _printReportItem.Click += async (_, _) => await RunBusyAsync(OpenReportAsync);
        _itemCancel.Click += async (_, _) => await RunBusyAsync(CancelItemAsync);
        _itemRepeat.Click += async (_, _) => await RunBusyAsync(RepeatItemAsync);

        // A right click on an analysis selects it and offers what can be done to it.
        _items.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            _items.CurrentCell = _items.Rows[e.RowIndex].Cells["name"];
            RefreshActions();
            _itemMenu.RightToLeft = RightToLeft;
            _itemMenu.Show(Cursor.Position);
        };

        _openBilling.Click += async (_, _) => await _navigation.NavigateToAsync<BillingView>();

        _items.SelectionChanged += (_, _) => RefreshActions();
        _items.CellFormatting += OnItemFormatting;
        _items.CellPainting += PaintItemCell;
        _items.SizeChanged += (_, _) => RefitItemColumns();
        _samples.CellPainting += PaintSampleCell;
        _samples.CellFormatting += OnSampleFormatting;
    }

    /// <summary>F8 reprints the labels, as the button says.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F8)
        {
            if (_printLabels.Enabled && _printLabels.Visible) _printLabels.PerformClick();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void ApplyTexts()
    {
        _search.PlaceholderText = T("Order.SearchPlaceholder");
        _search.AccessibleName = T("Order.SearchPlaceholder");
        _chipWeek.Text = T("OrderFile.FilterWeek");
        _chipActive.Text = T("OrderFile.FilterActive");
        _chipUrgent.Text = T("OrderFile.FilterUrgent");
        _tips.SetToolTip(_refresh, T("Action.Refresh"));
        _refresh.AccessibleName = T("Action.Refresh");

        _addAnalyses.Text = T("Order.AddAnalyses");
        _printLabels.Text = T("Action.PrintLabels");
        _printReport.Text = T("Order.PrintReport");
        _editHeader.Text = T("Order.EditHeader");
        _more.Text = T("ResultEntry.MoreActions");
        _cancelItem.Text = _itemCancel.Text = T("Order.CancelAnalysis");
        _repeatItem.Text = _itemRepeat.Text = T("Order.RepeatAnalysis");
        _cancelOrder.Text = T("Order.CancelOrder");
        _editHeaderItem.Text = T("Order.EditHeader");
        _printReportItem.Text = T("Order.PrintReport");
        _openBilling.Text = T("OrderFile.OpenBilling");
        _paymentsTitle.Text = T("Payment.Plural");

        _factCaptions[0].Text = T("Order.Prescriber");
        _factCaptions[1].Text = T("OrderFile.PrescriptionOf");
        _factCaptions[2].Text = T("OrderFile.Payer");
        _factCaptions[3].Text = T("Order.ClinicalInformation");

        _billingCaptions[0].Text = T("Field.Total");
        _billingCaptions[1].Text = T("Billing.PayerShare");
        _billingCaptions[2].Text = T("Billing.PatientShare");
        _billingCaptions[3].Text = T("Billing.Invoice");

        _list.Column("accession").HeaderText = T("Order.AccessionNumber");
        _list.Column("patient").HeaderText = T("Patient.Title");
        _list.Column("registered").HeaderText = T("Order.RegisteredAt");
        _list.Column("status").HeaderText = T("Field.Status");
        _list.Column("priority").HeaderText = T("Field.Priority");
        _list.Column("count").HeaderText = T("Catalog.Analyses");

        _items.Column("code").HeaderText = T("Catalog.Analysis");
        _items.Column("name").HeaderText = T("Catalog.Analysis");
        _items.Column("department").HeaderText = T("Field.Department");
        _items.Column("status").HeaderText = T("Field.Status");
        _items.Column("progress").HeaderText = T("Result.Plural");
        _items.Column("barcode").HeaderText = T("OrderEntry.Tube");
        _items.Column("price").HeaderText = T("Payment.Amount");
        _items.Column("share").HeaderText = T("Billing.PatientShare");
        _items.Column("note").HeaderText = T("Field.Comment");

        _samples.Column("sequence").HeaderText = T("Sample.Sequence");
        _samples.Column("barcode").HeaderText = T("Sample.Barcode");
        _samples.Column("specimen").HeaderText = T("Sample.SpecimenType");
        _samples.Column("container").HeaderText = T("Sample.ContainerType");
        _samples.Column("status").HeaderText = T("Field.Status");
        _samples.Column("collected").HeaderText = T("Sample.CollectedAt");
        _samples.Column("received").HeaderText = T("Sample.ReceivedAt");
        _samples.Column("storage").HeaderText = T("Sample.Storage");

        _payments.Column("date").HeaderText = T("Field.Date");
        _payments.Column("method").HeaderText = T("Payment.Method");
        _payments.Column("amount").HeaderText = T("Payment.Amount");
        _payments.Column("status").HeaderText = T("Field.Status");
        _payments.Column("by").HeaderText = T("OrderFile.ReceivedBy");
        _payments.Column("reference").HeaderText = T("Payment.Reference");

        _history.Column("date").HeaderText = T("Field.Date");
        _history.Column("user").HeaderText = T("OrderFile.User");
        _history.Column("event").HeaderText = T("Audit.Operation");

        RenderList();

        if (_detail is not null) Render(_detail);
        else RenderTabs(null);

        RefreshActions();
        FitBar();
    }

    /// <summary>
    /// Keeps every button of the bar whole: when they do not all fit, « Modifier l'en-tête », then
    /// « Compte rendu (PDF) », move into « Plus d'actions », where they do the same thing under the
    /// same right.
    /// </summary>
    private void FitBar()
    {
        if (_bar is null || _bar.ClientSize.Width <= 0) return;

        static int Width(Button b) => b.GetPreferredSize(Size.Empty).Width + b.Margin.Horizontal;

        var available = _bar.ClientSize.Width - _bar.Padding.Horizontal;
        var fixedWidth = Width(_addAnalyses) + Width(_printLabels) + Width(_more);
        var showReport = fixedWidth + Width(_printReport) <= available;
        var showHeader = showReport && fixedWidth + Width(_printReport) + Width(_editHeader) <= available;

        if (_printReport.Visible != showReport) _printReport.Visible = showReport;
        if (_editHeader.Visible != showHeader) _editHeader.Visible = showHeader;
        _printReportItem.Visible = !showReport;
        _editHeaderItem.Visible = !showHeader;
    }

    private void PaintMoreChevron(object? sender, PaintEventArgs e)
    {
        var x = IsRightToLeft ? 12 : _more.Width - 20;
        var y = _more.Height / 2 - 2;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(_more.Enabled ? UiTheme.TextPrimary : UiTheme.TextMuted, 1.6f);
        e.Graphics.DrawLines(pen, [new Point(x, y), new Point(x + 4, y + 4), new Point(x + 8, y)]);
    }

    // ---- Loading -------------------------------------------------------------------------------------

    /// <summary>
    /// Loads the list, keeps the file on screen selected in it, and loads that file — or, when
    /// nothing has been chosen yet, the newest file of the list, so the screen never opens empty.
    /// <para>
    /// Called again after every action, which is why the selection is carried over rather than
    /// reset: an operator who has just cancelled an analysis expects to be looking at the same
    /// file, not at whichever one was registered last.
    /// </para>
    /// </summary>
    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        await LoadListAsync(ct);

        if (ct.IsCancellationRequested) return;

        OrderId ??= _shown.Count > 0 ? _shown[0].OrderId : _rows.Count > 0 ? _rows[0].OrderId : null;
        SelectListRow(OrderId);

        if (OrderId is null)
        {
            _search.Focus();
            return;
        }

        await LoadDetailAsync(ct);
    }

    /// <summary>
    /// Fetches the list for what is in the search box and renders it. Returns false when the
    /// load failed or was superseded by a newer one.
    /// </summary>
    private async Task<bool> LoadListAsync(CancellationToken ct = default)
    {
        _listLoad?.Cancel();
        _listLoad?.Dispose();
        _listLoad = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Taken before the await: the source is disposed when a newer load replaces it.
        var token = _listLoad.Token;
        var term = _search.Text.Trim();

        try
        {
            var result = await _orders.SearchOrdersAsync(term, token);

            if (token.IsCancellationRequested) return false;

            if (result.IsFailure) { _dialogs.ShowFailure(result); return false; }

            _rows = result.Value;
            RenderList();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the order list for {Term} failed.", term);
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T("Message.UnexpectedError");
            return false;
        }
    }

    private async Task LoadDetailAsync(CancellationToken ct = default)
    {
        if (OrderId is not { } orderId) return;

        _detailLoad?.Cancel();
        _detailLoad?.Dispose();
        _detailLoad = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var token = _detailLoad.Token;

        try
        {
            var result = await _orders.GetDetailAsync(orderId, token);

            if (token.IsCancellationRequested) return;

            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            // The history and the payments of the previous file are not shown against this one
            // while this one's are being read.
            if (_detail?.OrderId != result.Value.OrderId)
            {
                _events = [];
                _paymentRows = [];
            }

            _detail = result.Value;
            Render(_detail);

            await LoadHistoryAsync(_detail, token);
            await LoadPaymentsAsync(_detail, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading order {OrderId} failed.", orderId);
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T("Message.UnexpectedError");
        }
    }

    /// <summary>
    /// The file's history, for its tab and for the report's date on the progress line. A failure
    /// leaves the tab saying so; the file itself is already on screen.
    /// </summary>
    private async Task LoadHistoryAsync(OrderDetail detail, CancellationToken token)
    {
        try
        {
            var events = await _fileQueries.GetHistoryAsync(detail.OrderId, _currentUser.SiteId, token);

            if (token.IsCancellationRequested || _detail?.OrderId != detail.OrderId) return;

            _events = events;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the history of {Accession} failed.", detail.AccessionNumber);
            _events = [];
        }

        RenderProgress(detail);
        RenderHistory();
    }

    /// <summary>
    /// The payments recorded against the file's invoice — only for an invoiced file, and only for
    /// an operator who may see the billing, as on the billing desk.
    /// </summary>
    private async Task LoadPaymentsAsync(OrderDetail detail, CancellationToken token)
    {
        if (detail.InvoiceId is not { } invoiceId || !_currentUser.Has(Permissions.Billing.View))
        {
            RenderBilling(detail);
            return;
        }

        try
        {
            var payments = await _billing.GetPaymentsAsync(invoiceId, token);

            if (token.IsCancellationRequested || _detail?.OrderId != detail.OrderId) return;

            _paymentRows = payments.IsSuccess ? payments.Value : [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the payments of {Accession} failed.", detail.AccessionNumber);
            _paymentRows = [];
        }

        RenderBilling(detail);
    }

    /// <summary>Opens the file the operator selected in the list.</summary>
    private async Task OpenSelectedAsync()
    {
        if (_populatingList) return;
        if (_list.CurrentRow?.Tag is not OrderListRow row) return;
        if (row.OrderId == OrderId && _detail?.OrderId == row.OrderId) return;

        OrderId = row.OrderId;
        _status.Text = string.Empty;
        await LoadDetailAsync();
    }

    /// <summary>
    /// Resolves what the operator typed — an accession number or a tube barcode — to an order.
    /// Both, because the thing in their hand is as often a tube as a paper file.
    /// <para>
    /// When it is neither — a name, a CIN, part of a number — Enter opens the first file of the
    /// filtered list instead of answering "not found": the operator pressed Enter to get to a file,
    /// and the list already says which one fits best.
    /// </para>
    /// </summary>
    private async Task SearchAsync()
    {
        var term = _search.Text.Trim();

        try
        {
            if (term.Length == 0)
            {
                await LoadListAsync();
                return;
            }

            var resolved = await _orders.ResolveOrderIdAsync(term);

            if (resolved.IsFailure) { _dialogs.ShowFailure(resolved); return; }

            if (resolved.Value is { } orderId)
            {
                OrderId = orderId;
                _status.Text = string.Empty;
                await ActivateAsync();
                return;
            }

            if (!await LoadListAsync()) return;

            if (_shown.Count == 0)
            {
                _status.ForeColor = UiTheme.Abnormal;
                _status.Text = T("Order.NotFoundForTerm", term);
                return;
            }

            OrderId = _shown[0].OrderId;
            _status.Text = string.Empty;
            SelectListRow(OrderId);
            await LoadDetailAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Searching orders for {Term} failed.", term);
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T("Message.UnexpectedError");
        }
    }

    // ---- The list --------------------------------------------------------------------------------

    /// <summary>The files the quick filters let through, in the list's order.</summary>
    private IReadOnlyList<OrderListRow> Filtered()
    {
        var since = _clock.Now.AddDays(-7);

        return
        [
            .. _rows.Where(r => (!_chipWeek.Selected || r.RegisteredAt >= since)
                                && (!_chipActive.Selected || r.Status is not (LabOrderStatus.Validated
                                                                              or LabOrderStatus.Reported
                                                                              or LabOrderStatus.Cancelled))
                                && (!_chipUrgent.Selected || r.Priority != OrderPriority.Routine))
        ];
    }

    private void RenderList()
    {
        if (_list.Columns.Count == 0) return;

        _shown = Filtered();
        _populatingList = true;

        try
        {
            _list.SuspendLayout();
            _list.Rows.Clear();

            foreach (var row in _shown)
            {
                var index = _list.Rows.Add(
                    row.AccessionNumber,
                    row.PatientName,
                    row.RegisteredAt.ToLocalTime().ToString("dd/MM HH:mm"),
                    T($"OrderStatus.{row.Status}"),
                    T($"Priority.{row.Priority}"),
                    row.AnalysisCount);

                _list.Rows[index].Tag = row;
                _list.Rows[index].Cells[0].ToolTipText =
                    $"{row.PatientName} · {row.AccessionNumber} · {T($"OrderStatus.{row.Status}")} · "
                    + $"{T($"Priority.{row.Priority}")} · {row.AnalysisCount} {T("Catalog.Analyses").ToLowerInvariant()}";
            }

            _list.ResumeLayout(performLayout: true);
        }
        finally
        {
            _populatingList = false;
        }

        SelectListRow(OrderId);

        var term = _search.Text.Trim();

        // Said when the cap is reached, so that a file missing from the list reads as "search for
        // it", not as "it does not exist".
        _listCount.Text = _shown.Count == 0 && term.Length > 0
            ? T("Order.NotFoundForTerm", term)
            : _rows.Count >= IOrderQueries.ListPageSize
                ? T("Order.ListTruncated", _rows.Count)
                : T("Order.ListCount", _shown.Count);
        _tips.SetToolTip(_listCount, _listCount.Text);
    }

    /// <summary>
    /// Moves the list's selection to the given order without opening it, or clears the selection
    /// when the order is not in the list — a file opened from the patient screen may be older than
    /// the newest two hundred, and is still shown, just not highlighted.
    /// </summary>
    private void SelectListRow(Guid? orderId)
    {
        _populatingList = true;

        try
        {
            var match = orderId is null
                ? null
                : _list.Rows.Cast<DataGridViewRow>()
                            .FirstOrDefault(r => r.Tag is OrderListRow row && row.OrderId == orderId);

            if (match is null)
            {
                _list.ClearSelection();
                _list.CurrentCell = null;
                return;
            }

            _list.CurrentCell = match.Cells[0];
            match.Selected = true;
        }
        finally
        {
            _populatingList = false;
        }
    }

    /// <summary>The text and colours of a file's state pill: « Validé » in green, « En cours · Urgent » in amber.</summary>
    private (string Text, (Color Back, Color Fore) Colours) OrderPill(LabOrderStatus status, OrderPriority priority)
    {
        var released = status is LabOrderStatus.Validated or LabOrderStatus.Reported;
        var text = T($"OrderStatus.{status}");

        // Urgent work still to do is flagged in the warning colours; done or cancelled, it no longer matters.
        if (priority != OrderPriority.Routine && !released && status != LabOrderStatus.Cancelled)
            return ($"{text} · {T($"Priority.{priority}")}", EntryPaint.Warning);

        return (text, FilePaint.ForOrder(status));
    }

    /// <summary>
    /// One file: the NAME and the registration time, then the number and its state pill. A
    /// cancelled file is greyed — still listed, because "what happened to that file?" is a question
    /// this screen answers.
    /// </summary>
    private void PaintListCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.Graphics is null) return;
        if (_list.Rows[e.RowIndex].Tag is not OrderListRow row) return;

        var graphics = e.Graphics;
        var bounds = e.CellBounds;
        var rtl = IsRightToLeft;
        var selected = (e.State & DataGridViewElementStates.Selected) != 0;
        var cancelled = row.Status == LabOrderStatus.Cancelled;

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

        const int pad = 14;
        var inner = new Rectangle(bounds.X + pad, bounds.Y + 8, bounds.Width - 2 * pad, bounds.Height - 16);
        var alignment = rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left;
        var opposite = rtl ? TextFormatFlags.Left | TextFormatFlags.RightToLeft : TextFormatFlags.Right;
        const TextFormatFlags single = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                                       | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis
                                       | TextFormatFlags.VerticalCenter;

        var ink = cancelled ? UiTheme.TextMuted : UiTheme.TextPrimary;
        var muted = cancelled ? UiTheme.TextMuted : UiTheme.TextSecondary;

        // Line 1: the name, and the registration time at the far end.
        var time = row.RegisteredAt.ToLocalTime().ToString("dd/MM HH:mm");
        var timeFont = UiTheme.Mono(8.25f);
        var timeWidth = TextRenderer.MeasureText(time, timeFont, Size.Empty, TextFormatFlags.NoPadding).Width;
        var top = new Rectangle(inner.X, inner.Y, inner.Width, 19);

        TextRenderer.DrawText(graphics, time, timeFont, EntryPaint.Mirror(top, bounds, rtl), muted, single | opposite);
        TextRenderer.DrawText(graphics, row.PatientName, UiTheme.BodyBold,
                              EntryPaint.Mirror(new Rectangle(top.X, top.Y, top.Width - timeWidth - 8, top.Height), bounds, rtl),
                              ink, single | alignment);

        // Line 2: the number, and the state pill at the far end.
        var (pillText, colours) = OrderPill(row.Status, row.Priority);
        if (cancelled) colours = (UiTheme.SurfaceSunken, UiTheme.TextMuted);
        var pillFont = UiTheme.Small;
        var pillSize = EntryPaint.MeasurePill(pillText, pillFont, 8);
        var bottom = new Rectangle(inner.X, inner.Y + 21, inner.Width, 18);
        var pillWidth = Math.Min(pillSize.Width, Math.Max(40, bottom.Width - 100));
        var pill = EntryPaint.Mirror(new Rectangle(bottom.Right - pillWidth, bottom.Y + (bottom.Height - pillSize.Height) / 2,
                                                   pillWidth, pillSize.Height), bounds, rtl);
        EntryPaint.DrawPill(graphics, pill, pillText, pillFont, colours, rtl);

        TextRenderer.DrawText(graphics, row.AccessionNumber, timeFont,
                              EntryPaint.Mirror(new Rectangle(bottom.X, bottom.Y, bottom.Width - pillWidth - 8, bottom.Height),
                                                bounds, rtl),
                              ink, single | alignment);

        e.Handled = true;
    }

    // ---- The file -------------------------------------------------------------------------------------

    private void Render(OrderDetail detail)
    {
        var language = Localizer.CurrentLanguage;

        _headerCard.Visible = true;

        // --- note ---------------------------------------------------------------------------------
        _noteBanner.Visible = detail.InternalNote is { Length: > 0 };
        _note.Text = detail.InternalNote is { Length: > 0 } note ? $"!  {note}" : string.Empty;
        _tips.SetToolTip(_note, _note.Text);

        // --- number, state, registration --------------------------------------------------------
        _accession.Text = detail.AccessionNumber;

        var (statusText, statusColours) = OrderPill(detail.Status, OrderPriority.Routine);
        _statusPill.Show("● " + statusText, statusColours);
        _priorityPill.Show(detail.Priority == OrderPriority.Routine ? string.Empty : T($"Priority.{detail.Priority}"),
                           detail.Priority == OrderPriority.VitalEmergency ? EntryPaint.Critical : EntryPaint.Warning);

        var registered = detail.RegisteredAt.ToLocalTime();
        _registered.Text = T("OrderFile.RegisteredOn", registered.ToString("dd/MM/yyyy"),
                             registered.ToString("HH:mm"), detail.RegisteredByName);
        _tips.SetToolTip(_registered, _registered.Text);

        // --- cancellation -----------------------------------------------------------------------------
        var cancelled = detail.CancelledAt is { } cancelledAt;
        _cancellation.Text = cancelled
            ? T("OrderFile.CancelledOn", detail.CancelledAt!.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                detail.CancellationReason ?? string.Empty)
            : string.Empty;
        _tips.SetToolTip(_cancellation, _cancellation.Text);
        _headerLayout.RowStyles[1].Height = cancelled ? CancellationHeight : 0;
        _cancellation.Visible = cancelled;
        _content.RowStyles[1].Height = HeaderCardHeight + (cancelled ? CancellationHeight : 0);

        // --- patient ----------------------------------------------------------------------------------
        _patient.Text = detail.PatientName;

        var info = new List<string>(2) { T($"Gender.{detail.Gender}") };
        if (detail.DateOfBirth is { } dob) info.Add(T("OrderFile.AgeYears", AgeInYears(dob)));
        _patientInfo.Text = string.Join(" · ", info);
        _patientRecord.Text = $"{T("Patient.RecordNumber")} {detail.MedicalRecordNumber}";
        _patientPhone.Text = detail.PhoneNumber is { Length: > 0 } phone ? $"{T("OrderEntry.PhoneShort")} {phone}" : string.Empty;
        _patientPhone.Visible = _patientPhone.Text.Length > 0;

        // --- prescriber, prescription, payer, clinical information ------------------------------------
        var prescriber = detail.PrescriberName ?? detail.PrescriberFreeText;
        SetFact(0, prescriber ?? T("Order.NoPrescriber"),
                detail.PrescriberInpe is { Length: > 0 } inpe ? $"INPE {inpe}" : string.Empty);

        SetFact(1, detail.PrescriptionDate is { } prescribed ? prescribed.ToString("dd/MM/yyyy") : "—",
                T($"OrderOrigin.{detail.Origin}"));

        var payer = detail.PayerName is { Length: > 0 } payerName
            ? payerName + (detail.ReimbursementRate is { } rate ? $" · {rate:P0}" : string.Empty)
            : T("Billing.PayerNotCovered");
        var payerNotes = new List<string>(2);
        if (detail.PriorAgreementNumber is { Length: > 0 } agreement)
            payerNotes.Add($"{T("Coverage.PriorAgreement")} {agreement}");
        if (detail.InvoiceNumber is { Length: > 0 } invoice)
            payerNotes.Add($"{T("Billing.Invoice")} {invoice}");
        SetFact(2, payer, string.Join(" · ", payerNotes));

        var clinical = new List<string>(3);
        if (detail.DiagnosisCode is { Length: > 0 } code) clinical.Add(code);
        if (detail.WardOrUnit is { Length: > 0 } ward)
            clinical.Add(detail.RoomOrBed is { Length: > 0 } bed ? $"{ward} / {bed}" : ward);
        if (detail.PatientFasted is false) clinical.Add(T("Order.NotFasted"));
        SetFact(3, detail.ClinicalInformation is { Length: > 0 } text ? text : "—", string.Join(" · ", clinical));

        RenderProgress(detail);
        RenderItems(detail, language);
        RenderSamples(detail, language);
        RenderTabs(detail);
        RenderBilling(detail);
        RenderHistory();

        RefreshActions();

        // The card was hidden until the first file: repainted whole, or what lay under it shows through.
        // After the grid's own fitting, which is queued once the rows are in.
        if (IsHandleCreated) BeginInvoke(RefitItemColumns);
        _content.Invalidate(invalidateChildren: true);
    }

    private void SetFact(int index, string value, string note)
    {
        _factValues[index].Text = value;
        _factNotes[index].Text = note;
        _tips.SetToolTip(_factValues[index], value);
        _tips.SetToolTip(_factNotes[index], note);
    }

    /// <summary>The progress line: each stage with its time once reached, or how far it has got.</summary>
    private void RenderProgress(OrderDetail detail)
    {
        var released = _events.Where(e => e.Kind == OrderFileEventKind.ReportReleased)
                              .Select(e => (DateTimeOffset?)e.OccurredAt)
                              .Max();

        var steps = FileProgress.Compute(detail, released);

        _progress.Show(
        [
            .. steps.Select(step => (step, T($"OrderFile.Stage{step.Stage}"), StepDetail(step)))
        ]);
    }

    private string StepDetail(FileStep step)
    {
        if (step.State == FileStageState.Done && step.At is { } at)
            return at.ToLocalTime().ToString("dd/MM HH:mm");

        if (step.State == FileStageState.Ahead && step.Done == 0) return "—";

        return step.Stage switch
        {
            FileStage.Collected or FileStage.Received => T("OrderFile.StepTubes", step.Done, step.Total),
            FileStage.Results => T("OrderFile.StepValues", step.Done, step.Total),
            FileStage.Validated => T("OrderFile.StepAnalyses", step.Done, step.Total),
            _ => "—"
        };
    }

    private void RenderTabs(OrderDetail? detail)
    {
        _tabs.SetCaptions(
            T("OrderFile.TabAnalyses", detail?.Items.Count ?? 0),
            T("OrderFile.TabTubes", detail?.Samples.Count ?? 0),
            T("OrderFile.TabBilling"),
            T("OrderFile.TabHistory"));
    }

    /// <summary>
    /// The remark of an analysis: why it was cancelled, which run it is on and why, who validated
    /// it, or that it is sub-contracted.
    /// </summary>
    private string ItemNote(OrderItemDetail item) =>
        (item.IsCancelled
            ? item.CancellationReason
            : item.RunCount > 1
                ? $"{T("Result.Run")} {item.RunCount} — {item.RepeatReason}"
                : item.ValidatedByName is { Length: > 0 } validator
                    ? $"{T("Validation.ValidatedBy")} {validator}"
                    : item.IsSubcontracted ? T("Catalog.Subcontracted") : string.Empty) ?? string.Empty;

    private void RenderItems(OrderDetail detail, AppLanguage language)
    {
        // The analysis just acted on stays current when its file is shown again (after a repeat,
        // a cancellation, an addition): the list went back to its first analysis, and the next
        // action from « Plus d'actions » was pointed at another one.
        var kept = _items.CurrentRow?.Tag is OrderItemDetail current
                   && detail.Items.Any(i => i.ItemId == current.ItemId)
            ? current.ItemId
            : (Guid?)null;

        _items.SuspendLayout();
        _items.Rows.Clear();

        foreach (var item in detail.Items)
        {
            var index = _items.Rows.Add(
                item.Code,
                item.Name.For(language),
                item.DepartmentCode ?? string.Empty,
                T($"OrderItemStatus.{item.Status}"),
                item.ResultCount == 0 ? string.Empty
                                      : $"{item.EnteredResultCount} / {item.ResultCount}",
                item.SampleBarcode ?? string.Empty,
                Money.Of(item.ChargedPrice).Format(language),
                Money.Of(item.PatientShare).Format(language),
                ItemNote(item));

            _items.Rows[index].Tag = item;
            if (item.IsOverdueAt(_clock.Now))
                _items.Rows[index].Cells["status"].ToolTipText = $"{T($"OrderItemStatus.{item.Status}")} · {T("Order.Overdue")}";
            _items.Rows[index].Cells["name"].ToolTipText =
                string.Join(" · ", new[] { item.Code, item.DepartmentCode, ItemNote(item) }
                                       .Where(s => !string.IsNullOrEmpty(s)));
        }

        _items.ResumeLayout(performLayout: true);

        // Otherwise the first live row rather than the first row: the cancelled ones are history
        // and are not what an operator wants an action pointed at.
        var live = _items.Rows.Cast<DataGridViewRow>()
                              .FirstOrDefault(r => kept is { } id && r.Tag is OrderItemDetail item && item.ItemId == id)
                   ?? _items.Rows.Cast<DataGridViewRow>()
                              .FirstOrDefault(r => r.Tag is OrderItemDetail { IsCancelled: false });

        if (live is not null)
        {
            _items.CurrentCell = live.Cells["name"];
            live.Selected = true;
        }

        // The tubes of the file, by colour, under the analyses; then what the analyses cost.
        _tubeLegend.SuspendLayout();
        foreach (Control old in _tubeLegend.Controls.Cast<Control>().Where(c => c != _totals).ToList()) old.Dispose();
        _tubeLegend.Controls.Clear();

        foreach (var sample in detail.Samples.Where(s => s.Status is not (SampleStatus.Rejected or SampleStatus.Discarded)))
        {
            var text = string.Join(" · ", new[] { sample.ContainerName, sample.SpecimenTypeName.For(language), sample.Barcode }
                                              .Where(s => !string.IsNullOrEmpty(s)));
            var chip = new TubeChip(text, sample.ContainerColourHex ?? string.Empty)
            {
                Margin = new Padding(0, 0, 18, 0),
                RightToLeft = RightToLeft
            };
            _tips.SetToolTip(chip, text);
            _tubeLegend.Controls.Add(chip);
        }

        _tubeLegend.Controls.Add(_totals);
        _tubeLegend.ResumeLayout(performLayout: true);

        _totals.Text = T("OrderFile.Totals", detail.LiveItemCount,
                         Money.Of(detail.ChargedTotal).Format(language),
                         Money.Of(detail.PatientShareTotal).Format(language),
                         Money.Of(detail.ChargedTotal - detail.PatientShareTotal).Format(language));
    }

    private void RenderSamples(OrderDetail detail, AppLanguage language)
    {
        _samples.SuspendLayout();
        _samples.Rows.Clear();

        foreach (var sample in detail.Samples)
        {
            var index = _samples.Rows.Add(
                sample.SequenceInOrder,
                sample.Barcode,
                sample.SpecimenTypeName.For(language),
                sample.ContainerName ?? string.Empty,
                T($"SampleStatus.{sample.Status}"),
                sample.CollectedAt?.ToLocalTime().ToString("dd/MM HH:mm") ?? string.Empty,
                sample.ReceivedAt?.ToLocalTime().ToString("dd/MM HH:mm") ?? string.Empty,
                sample.StorageLocation ?? string.Empty);

            _samples.Rows[index].Tag = sample;
        }

        _samples.ResumeLayout(performLayout: true);
    }

    private void RenderBilling(OrderDetail detail)
    {
        var language = Localizer.CurrentLanguage;
        var payerShare = detail.ChargedTotal - detail.PatientShareTotal;

        _billingValues[0].Text = Money.Of(detail.ChargedTotal).Format(language);
        _billingNotes[0].Text = $"{detail.LiveItemCount} {T("Catalog.Analyses").ToLowerInvariant()}";
        _billingValues[1].Text = Money.Of(payerShare).Format(language);
        _billingNotes[1].Text = detail.PayerName is { Length: > 0 } payer
            ? payer + (detail.ReimbursementRate is { } rate ? $" · {rate:P0}" : string.Empty)
            : T("Billing.PayerNotCovered");
        _billingValues[2].Text = Money.Of(detail.PatientShareTotal).Format(language);
        _billingNotes[2].Text = detail.PriorAgreementNumber is { Length: > 0 } agreement
            ? $"{T("Coverage.PriorAgreement")} {agreement}"
            : string.Empty;
        _billingValues[3].Text = detail.InvoiceNumber is { Length: > 0 } invoice ? invoice : T("OrderFile.NotInvoiced");
        _billingNotes[3].Text = string.Empty;

        for (var i = 0; i < 4; i++)
        {
            _tips.SetToolTip(_billingValues[i], _billingValues[i].Text);
            _tips.SetToolTip(_billingNotes[i], _billingNotes[i].Text);
        }

        var mayView = _currentUser.Has(Permissions.Billing.View);
        _openBilling.Visible = mayView;

        _payments.Rows.Clear();
        foreach (var payment in _paymentRows)
        {
            _payments.Rows.Add(payment.PaymentDate.ToString("dd/MM/yyyy"),
                               T($"PaymentMethod.{payment.Method}"),
                               Money.Of(payment.Amount).Format(language),
                               T($"PaymentStatus.{payment.Status}"),
                               payment.ReceivedByName,
                               payment.Reference ?? string.Empty);
        }

        _paymentsNote.Text = !mayView
            ? T("OrderFile.PaymentsNotAllowed")
            : detail.InvoiceId is null
                ? T("OrderFile.NotInvoicedNotice")
                : _paymentRows.Count == 0 ? T("OrderFile.NoPayments") : string.Empty;
        _paymentsNote.Visible = _paymentsNote.Text.Length > 0;
        _payments.Visible = _paymentRows.Count > 0;
    }

    private void RenderHistory()
    {
        _history.Rows.Clear();

        foreach (var entry in _events)
        {
            var description = entry.Kind == OrderFileEventKind.ReportReleased
                ? T("OrderFile.ReportReleased", entry.ReportVersion ?? 1)
                : entry.Description ?? string.Empty;

            var index = _history.Rows.Add(entry.OccurredAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                                          entry.UserName ?? string.Empty, description);
            _history.Rows[index].Cells["event"].ToolTipText = description;
        }

        _historyNote.Text = T("OrderFile.HistoryEmpty");
        _historyNote.Visible = _events.Count == 0;
    }

    private void OnItemFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _items.Rows.Count) return;
        if (_items.Rows[e.RowIndex].Tag is not OrderItemDetail item) return;

        if (item.IsCancelled)
        {
            // Struck through rather than hidden: a cancelled analysis and its reason are the
            // answer to "why is this not on the report?".
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
            // CellStyle.Font is null when the cell inherits the grid's font, which is the normal
            // case here — and Font's prototype constructor throws on a null prototype.
            e.CellStyle.Font = new Font(e.CellStyle.Font ?? UiTheme.GridBody, FontStyle.Strikeout);
        }
    }

    /// <summary>
    /// The analysis on two lines (its name; « CODE · secteur » and the remark), its state as a pill,
    /// and its tube as the cap's colour before the barcode.
    /// </summary>
    private void PaintItemCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.Graphics is null) return;
        if (_items.Rows[e.RowIndex].Tag is not OrderItemDetail item) return;

        var name = _items.Columns[e.ColumnIndex].Name;
        if (name is not ("name" or "status" or "barcode")) return;

        var graphics = e.Graphics;
        var bounds = e.CellBounds;
        var rtl = IsRightToLeft;
        e.PaintBackground(e.ClipBounds, (e.State & DataGridViewElementStates.Selected) != 0);

        var alignment = rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left;
        const TextFormatFlags single = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                                       | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis
                                       | TextFormatFlags.VerticalCenter;
        var inner = new Rectangle(bounds.X + 12, bounds.Y, bounds.Width - 24, bounds.Height);

        switch (name)
        {
            case "name":
            {
                var cancelled = item.IsCancelled;
                var nameFont = cancelled ? new Font(UiTheme.BodyBold, FontStyle.Bold | FontStyle.Strikeout) : UiTheme.BodyBold;
                TextRenderer.DrawText(graphics, item.Name.For(Localizer.CurrentLanguage), nameFont,
                                      EntryPaint.Mirror(new Rectangle(inner.X, bounds.Y + 5, inner.Width, 19), bounds, rtl),
                                      cancelled ? UiTheme.TextMuted : UiTheme.TextPrimary, single | alignment);
                if (cancelled) nameFont.Dispose();

                var code = string.Join(" · ", new[] { item.Code, item.DepartmentCode }.Where(s => !string.IsNullOrEmpty(s)));
                var codeFont = UiTheme.Mono(8.25f);
                var codeWidth = TextRenderer.MeasureText(code, codeFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                var line = new Rectangle(inner.X, bounds.Y + 24, inner.Width, 17);
                TextRenderer.DrawText(graphics, code, codeFont,
                                      EntryPaint.Mirror(new Rectangle(line.X, line.Y, Math.Min(codeWidth, line.Width), line.Height), bounds, rtl),
                                      UiTheme.TextSecondary, single | alignment);

                var note = ItemNote(item);
                if (note.Length > 0 && codeWidth < line.Width)
                {
                    var colour = item.IsCancelled ? UiTheme.CriticalInk
                               : item.RunCount > 1 ? UiTheme.Abnormal
                               : UiTheme.TextSecondary;
                    TextRenderer.DrawText(graphics, " · " + note, UiTheme.Small,
                                          EntryPaint.Mirror(new Rectangle(line.X + codeWidth, line.Y, line.Width - codeWidth, line.Height),
                                                            bounds, rtl),
                                          colour, single | alignment);
                }

                break;
            }

            case "status":
            {
                var released = item.Status is LabOrderItemStatus.Validated or LabOrderItemStatus.Reported;
                var text = T($"OrderItemStatus.{item.Status}");
                var colours = FilePaint.ForItem(item.Status);

                // Late, or on a second run: the pill in the warning colours, the reason in its tooltip
                // (« En retard ») or on the line's remark (the run and its reason).
                if (item.IsOverdueAt(_clock.Now) || (item.RunCount > 1 && !item.IsCancelled && !released))
                    colours = EntryPaint.Warning;

                var size = EntryPaint.MeasurePill(text, UiTheme.SmallBold, 10);
                var pill = EntryPaint.Mirror(
                    new Rectangle(inner.X, bounds.Y + (bounds.Height - size.Height) / 2,
                                  Math.Min(size.Width, inner.Width), size.Height), bounds, rtl);
                EntryPaint.DrawPill(graphics, pill, text, UiTheme.SmallBold, colours, rtl);
                break;
            }

            case "barcode" when item.SampleBarcode is { Length: > 0 } barcode:
            {
                var colour = _detail?.Samples.FirstOrDefault(s => s.SampleId == item.SampleId)?.ContainerColourHex;
                var dot = EntryPaint.Mirror(new Rectangle(inner.X, bounds.Y, 10, bounds.Height), bounds, rtl);
                TubeSwatch.Dot(graphics, dot, colour);
                TextRenderer.DrawText(graphics, barcode, UiTheme.Monospace,
                                      EntryPaint.Mirror(new Rectangle(inner.X + 16, bounds.Y, inner.Width - 16, bounds.Height), bounds, rtl),
                                      item.IsCancelled ? UiTheme.TextMuted : UiTheme.TextPrimary, single | TextFormatFlags.Left);
                break;
            }
        }

        e.Handled = true;
    }

    private void OnSampleFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _samples.Rows.Count) return;
        if (_samples.Rows[e.RowIndex].Tag is not OrderSampleDetail sample) return;

        if (sample.Status is SampleStatus.Rejected or SampleStatus.Discarded)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (sample.Status == SampleStatus.AwaitingCollection)
        {
            e.CellStyle!.ForeColor = UiTheme.Abnormal;
        }
    }

    /// <summary>The tube's state as a pill, and its type after the cap's colour.</summary>
    private void PaintSampleCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.Graphics is null) return;
        if (_samples.Rows[e.RowIndex].Tag is not OrderSampleDetail sample) return;

        var name = _samples.Columns[e.ColumnIndex].Name;
        if (name is not ("status" or "container")) return;

        var rtl = IsRightToLeft;
        var bounds = e.CellBounds;
        e.PaintBackground(e.ClipBounds, (e.State & DataGridViewElementStates.Selected) != 0);
        var inner = new Rectangle(bounds.X + 12, bounds.Y, bounds.Width - 24, bounds.Height);

        if (name == "status")
        {
            var text = T($"SampleStatus.{sample.Status}");
            var size = EntryPaint.MeasurePill(text, UiTheme.SmallBold, 10);
            var pill = EntryPaint.Mirror(new Rectangle(inner.X, bounds.Y + (bounds.Height - size.Height) / 2,
                                                       Math.Min(size.Width, inner.Width), size.Height), bounds, rtl);
            EntryPaint.DrawPill(e.Graphics, pill, text, UiTheme.SmallBold, FilePaint.ForSample(sample.Status), rtl);
        }
        else
        {
            TubeSwatch.Dot(e.Graphics, EntryPaint.Mirror(new Rectangle(inner.X, bounds.Y, 10, bounds.Height), bounds, rtl),
                           sample.ContainerColourHex);
            TextRenderer.DrawText(e.Graphics, sample.ContainerName ?? string.Empty, UiTheme.Small,
                                  EntryPaint.Mirror(new Rectangle(inner.X + 16, bounds.Y, inner.Width - 16, bounds.Height), bounds, rtl),
                                  UiTheme.TextPrimary,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                                  | TextFormatFlags.EndEllipsis
                                  | (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left));
        }

        e.Handled = true;
    }

    private OrderItemDetail? SelectedItem => _items.CurrentRow?.Tag as OrderItemDetail;

    private void RefreshActions()
    {
        var detail = _detail;
        var item = SelectedItem;

        var editable = detail is { IsCancelled: false };

        // Off on an invoiced or reported file, which the service refuses: offered, the operator
        // picked the analyses and typed the reason before being told so.
        _addAnalyses.Enabled = detail is { CanAddAnalyses: true } && _currentUser.Has(Permissions.Orders.Edit);
        _editHeader.Enabled = _editHeaderItem.Enabled = editable && _currentUser.Has(Permissions.Orders.Edit);

        _cancelOrder.Enabled = detail is { CanCancel: true }
                               && _currentUser.Has(Permissions.Orders.Cancel);

        _printLabels.Enabled = detail is { IsCancelled: false }
                               && _currentUser.Has(Permissions.Samples.Reprint);

        _printReport.Enabled = _printReportItem.Enabled =
            detail is not null && _currentUser.Has(Permissions.Reports.ReprintValidated);

        _cancelItem.Enabled = _itemCancel.Enabled = item is { CanCancel: true }
                                                    && _currentUser.Has(Permissions.Orders.Cancel);

        _repeatItem.Enabled = _itemRepeat.Enabled = item is { CanRepeat: true }
                                                    && _currentUser.Has(Permissions.Results.Enter);

        _more.Enabled = detail is not null;
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private async Task AddAnalysesAsync()
    {
        if (_detail is not { } detail) return;

        using var dialog = new AnalysisPickerDialog(Localizer, _catalogQueries, _dialogs, _clock,
                                                     detail.AccessionNumber, detail.PatientName,
                                                     [.. detail.Items.Where(i => !i.IsCancelled)
                                                                     .Select(i => i.AnalysisTestId)]);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        var result = await _orders.AddAnalysesAsync(detail.OrderId, dialog.AnalysisIds,
                                                     dialog.PanelIds, dialog.Reason);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Order.AddAnalyses")); return; }

        var amendment = result.Value;

        // Warnings are shown, not swallowed: "added onto a tube already in the laboratory" is a
        // job for somebody — go and check there is serum left — and it is not visible anywhere else.
        if (amendment.Warnings.Count > 0)
            _dialogs.Warn(Localizer.Describe(amendment.Warnings), T("Order.AddAnalyses"));

        // A new tube means a new label. Printed here rather than left to the collection screen,
        // because the person who just added the analysis is the one standing by the printer.
        await PrintLabelsAsync(detail.AccessionNumber, amendment.Labels);

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Order.AnalysesAdded", amendment.Pricing.Acts.Count,
                         amendment.Pricing.Total.Format(Localizer.CurrentLanguage));

        await ActivateAsync();
    }

    private async Task CancelItemAsync()
    {
        if (_detail is not { } detail || SelectedItem is not { } item) return;
        if (!item.CanCancel || !_currentUser.Has(Permissions.Orders.Cancel)) return;

        var reason = _dialogs.PromptForReason(
            T("Order.CancelAnalysisPrompt", item.Code, item.Name.For(Localizer.CurrentLanguage)),
            T("Order.CancelAnalysis"), maxLength: 500);

        if (reason is null) return;

        var result = await _orders.CancelItemAsync(detail.OrderId, item.ItemId, reason);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Order.CancelAnalysis")); return; }

        // The charge goes with the analysis while the order is uninvoiced; once invoiced, the money
        // is put right by a credit note, and saying so here saves the operator discovering it later.
        _status.ForeColor = UiTheme.TextSecondary;
        _status.Text = detail.IsInvoiced
            ? T("Order.CancelledItemNeedsCreditNote", item.Code)
            : $"{item.Code} — {T("OrderItemStatus.Cancelled")}";

        await ActivateAsync();
    }

    private async Task RepeatItemAsync()
    {
        if (_detail is not { } detail || SelectedItem is not { } item) return;
        if (!item.CanRepeat || !_currentUser.Has(Permissions.Results.Enter)) return;

        var reason = _dialogs.PromptForReason(
            T("Order.RepeatAnalysisPrompt", item.Code, item.RunCount),
            T("Order.RepeatAnalysis"), maxLength: 500);

        if (reason is null) return;

        var result = await _orders.RepeatItemAsync(detail.OrderId, item.ItemId, reason);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Order.RepeatAnalysis")); return; }

        _status.ForeColor = UiTheme.Abnormal;
        _status.Text = T("Order.RepeatRecorded", item.Code, item.RunCount + 1);

        await ActivateAsync();
    }

    private async Task EditHeaderAsync()
    {
        if (_detail is not { } detail) return;

        using var dialog = new OrderHeaderDialog(Localizer, _dialogs, detail);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        var result = await _orders.AmendHeaderAsync(dialog.Request, dialog.Reason);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Order.EditHeader")); return; }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Message.Saved");

        await ActivateAsync();
    }

    private async Task CancelOrderAsync()
    {
        if (_detail is not { } detail) return;
        if (!detail.CanCancel || !_currentUser.Has(Permissions.Orders.Cancel)) return;

        // Named in the confirmation, with the tube count, because cancelling a file also discards
        // its tubes and the operator should know that before they click rather than after.
        var live = detail.Samples.Count(
            s => s.Status is not (SampleStatus.Rejected or SampleStatus.Discarded));

        if (!_dialogs.Confirm(
                T("Order.ConfirmCancelOrder", detail.AccessionNumber, detail.LiveItemCount, live),
                T("Order.CancelOrder")))
            return;

        var reason = _dialogs.PromptForReason(
            T("Order.CancelOrderPrompt", detail.AccessionNumber), T("Order.CancelOrder"), maxLength: 500);

        if (reason is null) return;

        var result = await _orders.CancelOrderAsync(detail.OrderId, reason);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Order.CancelOrder")); return; }

        _status.ForeColor = UiTheme.Critical;
        _status.Text = detail.IsInvoiced
            ? T("Order.CancelledNeedsCreditNote", detail.InvoiceNumber ?? string.Empty)
            : T("OrderStatus.Cancelled");

        await ActivateAsync();
    }

    /// <summary>
    /// Reprints every label of the file, against a reason: a reprinted label is a second barcode
    /// for the same tube, which is how two tubes end up carrying one identity, so it is recorded.
    /// </summary>
    private async Task ReprintLabelsAsync()
    {
        if (_detail is not { } detail) return;

        var reason = _dialogs.PromptForReason(T("Sample.ReprintReasonPrompt"), T("Action.PrintLabels"));
        if (reason is null) return;

        try
        {
            var labels = await _samplesWorkflow.ReprintLabelsAsync(detail.OrderId, reason);

            if (labels.IsFailure) { _dialogs.ShowFailure(labels, T("Action.PrintLabels")); return; }

            await PrintLabelsAsync(detail.AccessionNumber, labels.Value);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Reprinting the labels of {Accession} failed.", detail.AccessionNumber);
            _dialogs.Warn(T("Action.PrintLabels") + " — " + T("Message.UnexpectedError"));
        }
    }

    /// <summary>
    /// Opens the latest released report of the file, exactly as it was archived — a reprint shows
    /// what the clinician was given, never a fresh rendering of today's data.
    /// </summary>
    private async Task OpenReportAsync()
    {
        if (_detail is not { } detail) return;

        var caption = T("Order.PrintReport");

        try
        {
            var register = await _reports.GetRegisterAsync(detail.AccessionNumber, withinDays: 3650);

            if (register.IsFailure) { _dialogs.ShowFailure(register, caption); return; }

            var latest = register.Value
                .Where(r => r.LabOrderId == detail.OrderId)
                .OrderByDescending(r => r.VersionNumber)
                .FirstOrDefault();

            if (latest is null)
            {
                _dialogs.Info(T("Order.NoReportYet"), caption);
                return;
            }

            var pdf = await _reports.GetStoredPdfAsync(latest.ReportId);

            if (pdf.IsFailure) { _dialogs.ShowFailure(pdf, caption); return; }

            var opened = await _documents.OpenAsync(pdf.Value, "pdf");

            if (opened.IsFailure) _dialogs.ShowFailure(opened, caption);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Opening the report of {Accession} failed.", detail.AccessionNumber);
            _dialogs.Warn(caption + " — " + T("Message.UnexpectedError"));
        }
    }

    /// <summary>
    /// Renders and opens the labels for tubes an addition or a reprint produced. A label failure
    /// must not lose the amendment — the analyses are on the order, and the labels can be reprinted.
    /// </summary>
    private async Task PrintLabelsAsync(string accessionNumber, IReadOnlyList<SampleLabel> labels)
    {
        if (labels.Count == 0) return;

        try
        {
            var emitted = await LabelOutput.EmitAsync(_labelPrinter, _documents, _labels.Render, labels);

            if (emitted.IsFailure) _dialogs.ShowFailure(emitted, T("Action.PrintLabels"));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Rendering the labels of {Accession} failed.", accessionNumber);
            _dialogs.Warn(T("Action.PrintLabels") + " — " + T("Message.UnexpectedError"));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Disposed twice — by the navigation, then by its scope — so each source is released
            // once and forgotten.
            _debounce.Dispose();
            _tips.Dispose();
            _moreMenu.Dispose();
            _itemMenu.Dispose();
            _listLoad?.Cancel();
            _listLoad?.Dispose();
            _listLoad = null;
            _detailLoad?.Cancel();
            _detailLoad?.Dispose();
            _detailLoad = null;
        }

        base.Dispose(disposing);
    }

    private int AgeInYears(DateOnly dateOfBirth)
    {
        var today = _clock.Today;
        var age = today.Year - dateOfBirth.Year;
        if (dateOfBirth > today.AddYears(-age)) age--;
        return age;
    }
}
