using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Samples;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Samples;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The collection worklist.
/// <para>
/// Organised around one fact: the phlebotomist works on <b>one patient at a time</b>, draws all
/// their tubes together, and needs the order of draw right. So selecting any tube selects that
/// patient's whole set, the set is sorted by draw order rather than alphabetically, and the cap
/// colour of each is shown — because additive carry-over between containers drawn out of order is
/// a real pre-analytical error, and the colour is what the operator actually matches against the
/// rack.
/// </para>
/// <para>
/// Fasting and patient alerts are stated <em>before</em> the draw, at the top of the panel. Asking
/// afterwards is useless: a biologist interpreting a glycaemia needs to know whether the patient
/// had eaten, and the only person who can honestly ask is the one at the chair.
/// </para>
/// <para>
/// Laid out as the design system's « Prélèvements » screen: the patients to draw on the left, with
/// filter chips; on the right the patient card, the fasting band with its Oui / Non / Ne sait pas
/// answer, the tubes in draw order with a scan box to tick them, the collection details, and the
/// action bar (F8 labels, F12 validate).
/// </para>
/// </summary>
public sealed class CollectionView : LocalizedView
{
    private const string ColSelected = "selected";
    private const string ColDrawOrder = "draworder";
    private const string ColTube = "tube";
    private const string ColContainer = "container";
    private const string ColBarcode = "barcode";
    private const string ColTime = "time";
    private const string ColVolume = "volume";
    private const string ColState = "state";

    private const int PatientListWidth = 280;
    private const int PatientRowHeight = 58;
    private const int TubeRowHeight = 50;

    private readonly SampleWorkflowService _workflow;
    private readonly ISampleLabelRenderer _labels;
    private readonly ILabelPrinter _labelPrinter;
    private readonly IDocumentOutput _documents;
    private readonly IUserDialogs _dialogs;
    private readonly INavigationService? _navigation;
    private readonly ICurrentUser? _currentUser;
    private readonly IClock? _clock;
    private readonly ILogger<CollectionView>? _logger;

    // Patients to draw
    private readonly DataGridView _patients = UiTheme.Grid();
    private readonly Label _patientsHeading = new();
    private readonly Label _patientsCount = new();
    private readonly FlowLayoutPanel _chips = new();
    private readonly ChipButton _chipAll = new();
    private readonly ChipButton _chipUrgent = new();
    private readonly ChipButton _chipFasting = new();

    // Patient card
    private readonly Panel _patientCard = new();
    private readonly InitialsBadge _initials = new();
    private readonly Label _patientBanner = new();
    private readonly Label _patientDetails = new();
    private readonly Button _openFile = UiTheme.SecondaryButton(string.Empty);

    // Alerts and fasting
    private readonly Label _alertBanner = new();
    private readonly Panel _fastingBand = new();
    private readonly CollectionClockGlyph _fastingClock = new();
    private readonly Label _fastingBanner = new();
    private readonly FlowLayoutPanel _fastingChoice = new();
    private readonly CollectionSegmentButton _fastedYes = new();
    private readonly CollectionSegmentButton _fastedNo = new();
    private readonly CollectionSegmentButton _fastedUnknown = new();

    // Tubes
    private readonly Label _tubesHeading = new();
    private readonly PillLabel _tubesProgress = new();
    private readonly TextBox _scan = UiTheme.Input(320);
    private readonly DataGridView _tubes = UiTheme.Grid();

    // Collection details
    private readonly Label _collectedByLabel = new();
    private readonly TextBox _collectedBy = UiTheme.Input(200);
    private readonly Label _timeLabel = new();
    private readonly TextBox _time = UiTheme.Input(140);
    private readonly Label _locationLabel = new();
    private readonly ComboBox _location = new();
    private readonly Label _commentLabel = new();
    private readonly TextBox _comment = UiTheme.Input(280);

    // Actions
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _record = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _dispatch = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _reprint = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _remaining = new();
    private readonly Panel _statusStrip = new();
    private readonly Label _status = new();
    private readonly ToolTip _tips = new();

    private IReadOnlyList<CollectionWorklistEntry> _entries = [];
    private IReadOnlyList<CollectionWorklistEntry> _currentSet = [];

    private PatientFilter _filter = PatientFilter.All;

    /// <summary>
    /// The fasting answer given in the band for the patient on screen: true, false, or "Ne sait
    /// pas" (asked, not known). Null while nothing was pressed — the draw then asks, as before.
    /// </summary>
    private FastingAnswer _fastingAnswer = FastingAnswer.NotAsked;

    /// <summary>Set once the operator picks a place; until then each tube keeps the one its order implied.</summary>
    private bool _locationChosen;

    private bool _loadingLocation;

    public CollectionView(SampleWorkflowService workflow, ISampleLabelRenderer labels, ILabelPrinter labelPrinter, IDocumentOutput documents,
                          IUserDialogs dialogs, ILocalizer localizer, ICultureManager cultureManager,
                          INavigationService? navigation = null, ICurrentUser? currentUser = null, IClock? clock = null,
                          ILogger<CollectionView>? logger = null)
        : base(localizer, cultureManager)
    {
        _workflow = workflow;
        _labels = labels;
        _labelPrinter = labelPrinter;
        _documents = documents;
        _dialogs = dialogs;
        _navigation = navigation;
        _currentUser = currentUser;
        _clock = clock;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Collection");

    private bool IsRightToLeft => RightToLeft == RightToLeft.Yes;

    private enum PatientFilter { All, Urgent, Fasting }

    private enum FastingAnswer { NotAsked, Yes, No, Unknown }

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(0);

        // Two columns, the patients then the draw. A table rather than docking: it mirrors by
        // itself in Arabic, the patients going to the right.
        var columns = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, PatientListWidth));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        columns.Controls.Add(BuildPatientPanel(), 0, 0);
        columns.Controls.Add(BuildDrawPanel(), 1, 0);

        Controls.Add(columns);

        SetActionsEnabled(false);
    }

    /// <summary>« À prélever », the count, the filter chips and the patients.</summary>
    private Control BuildPatientPanel()
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

        // The line between the patients and the draw, on whichever side the draw is.
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

        // « À prélever » and, at the far end, the refresh: as on the result entry screen, an icon
        // beside the list it reloads (F5 does the same), which leaves the action bar room.
        var titleRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(14, 12, 14, 2),
            BackColor = UiTheme.SurfaceRaised
        };
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _patientsHeading.Font = EntryPaint.Resized(UiTheme.SectionTitle, 12.75f);
        _patientsHeading.ForeColor = UiTheme.TextPrimary;
        _patientsHeading.AutoSize = true;
        _patientsHeading.Anchor = AnchorStyles.Left;
        _patientsHeading.Margin = new Padding(0, 2, 0, 0);

        _refresh.Size = new Size(30, 28);
        _refresh.Padding = new Padding(0);
        _refresh.Margin = new Padding(0);
        _refresh.TabStop = false;
        ButtonIcons.Set(_refresh, Glyph.RefreshCw, always: true);

        titleRow.Controls.Add(_patientsHeading, 0, 0);
        titleRow.Controls.Add(_refresh, 1, 0);

        _patientsCount.Font = UiTheme.Body;
        _patientsCount.ForeColor = UiTheme.TextSecondary;
        _patientsCount.AutoSize = true;
        _patientsCount.Margin = new Padding(14, 0, 14, 8);

        _chips.Dock = DockStyle.Fill;
        _chips.AutoSize = true;
        _chips.WrapContents = true;
        _chips.Margin = new Padding(14, 0, 14, 8);
        _chips.BackColor = UiTheme.SurfaceRaised;
        _chips.Controls.AddRange([_chipAll, _chipUrgent, _chipFasting]);
        _chipAll.Selected = true;

        BuildPatientColumns();
        _patients.ColumnHeadersVisible = false;
        _patients.CellBorderStyle = DataGridViewCellBorderStyle.None;
        _patients.RowTemplate.Height = PatientRowHeight;
        _patients.Margin = new Padding(0);
        _patients.Dock = DockStyle.Fill;
        _patients.ShowCellToolTips = true;
        _patients.CellPainting += PaintPatientCell;

        panel.Controls.Add(titleRow, 0, 0);
        panel.Controls.Add(_patientsCount, 0, 1);
        panel.Controls.Add(_chips, 0, 2);
        panel.Controls.Add(_patients, 0, 3);

        return panel;
    }

    /// <summary>The patient card, the bands, the tubes, the collection details and the action bar.</summary>
    private Control BuildDrawPanel()
    {
        var host = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = UiTheme.Surface };

        // --- action bar -----------------------------------------------------------------------------

        // In a strip of its own: a white table straight on the work surface would be drawn as a card.
        var strip = new EntryStrip { Dock = DockStyle.Bottom, Height = 56, BackColor = UiTheme.SurfaceRaised };
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(24, 11, 24, 11),
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        strip.Controls.Add(bar);

        foreach (var button in new[] { _reprint, _dispatch, _record })
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(0, 34);
            button.Height = 34;
            button.Margin = new Padding(0, 0, 8, 0);
        }

        _record.Margin = new Padding(0);

        // Takes what the buttons leave, cut with an ellipsis rather than pushing a button out.
        _remaining.AutoSize = false;
        _remaining.AutoEllipsis = true;
        _remaining.Dock = DockStyle.Fill;
        _remaining.TextAlign = ContentAlignment.MiddleRight;
        _remaining.Font = UiTheme.Body;
        _remaining.ForeColor = UiTheme.TextSecondary;
        _remaining.Margin = new Padding(8, 0, 12, 0);

        bar.Controls.Add(_reprint, 0, 0);
        bar.Controls.Add(_remaining, 1, 0);
        bar.Controls.Add(_dispatch, 2, 0);
        bar.Controls.Add(_record, 3, 0);

        // --- status line ----------------------------------------------------------------------------
        // What the last action said — drawn, sent, printed, refused. Shown only while there is
        // something to say.

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
            RowCount = 6,
            Padding = new Padding(24, 16, 24, 12),
            Margin = new Padding(0),
            BackColor = UiTheme.Surface
        };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        cards.Controls.Add(BuildPatientCard(), 0, 0);
        cards.Controls.Add(BuildAlertBand(), 0, 1);
        cards.Controls.Add(BuildFastingBand(), 0, 2);
        cards.Controls.Add(BuildTubesHeader(), 0, 3);
        cards.Controls.Add(BuildTubesGrid(), 0, 4);
        cards.Controls.Add(BuildDetailsCard(), 0, 5);

        host.Controls.Add(cards);
        host.Controls.Add(_statusStrip);
        host.Controls.Add(strip);

        return host;
    }

    private Control BuildPatientCard()
    {
        _patientCard.Dock = DockStyle.Fill;
        _patientCard.Height = 68;
        _patientCard.Margin = new Padding(0, 0, 0, 12);
        _patientCard.Padding = new Padding(16, 12, 16, 12);
        _patientCard.BackColor = UiTheme.SurfaceRaised;
        UiChrome.AsCard(_patientCard);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

        _initials.Margin = new Padding(0, 2, 14, 0);
        _initials.Anchor = AnchorStyles.Left;

        _patientBanner.Font = EntryPaint.Resized(UiTheme.SectionTitle, 12.75f);
        _patientBanner.ForeColor = UiTheme.TextPrimary;
        _patientBanner.AutoSize = false;
        _patientBanner.AutoEllipsis = true;
        _patientBanner.Dock = DockStyle.Fill;
        _patientBanner.TextAlign = ContentAlignment.BottomLeft;
        _patientBanner.Margin = new Padding(0);

        _patientDetails.Font = UiTheme.Body;
        _patientDetails.ForeColor = UiTheme.TextSecondary;
        _patientDetails.AutoSize = false;
        _patientDetails.AutoEllipsis = true;
        _patientDetails.Dock = DockStyle.Fill;
        _patientDetails.TextAlign = ContentAlignment.TopLeft;
        _patientDetails.Margin = new Padding(0, 2, 0, 0);

        _openFile.AutoSize = true;
        _openFile.MinimumSize = new Size(0, 34);
        _openFile.Anchor = AnchorStyles.Right;
        _openFile.Margin = new Padding(12, 0, 0, 0);
        ButtonIcons.Set(_openFile, Glyph.FolderOpen, always: true);

        layout.Controls.Add(_initials, 0, 0);
        layout.SetRowSpan(_initials, 2);
        layout.Controls.Add(_patientBanner, 1, 0);
        layout.Controls.Add(_patientDetails, 1, 1);
        layout.Controls.Add(_openFile, 2, 0);
        layout.SetRowSpan(_openFile, 2);

        _patientCard.Controls.Add(layout);
        return _patientCard;
    }

    /// <summary>Biohazard, difficult draw, unpaid balance: a band of its own so it cannot be skimmed past.</summary>
    private Control BuildAlertBand()
    {
        _alertBanner.Font = UiTheme.BodyBold;
        _alertBanner.ForeColor = UiTheme.CriticalInk;
        _alertBanner.BackColor = UiTheme.CriticalBackground;
        _alertBanner.AutoSize = false;
        _alertBanner.AutoEllipsis = true;
        _alertBanner.Dock = DockStyle.Fill;
        _alertBanner.Height = 34;
        _alertBanner.Padding = new Padding(16, 0, 16, 0);
        _alertBanner.TextAlign = ContentAlignment.MiddleLeft;
        _alertBanner.Margin = new Padding(0, 0, 0, 12);
        _alertBanner.Visible = false;
        return _alertBanner;
    }

    /// <summary>« Jeûne requis : 8 h … Le patient est-il à jeun ? » and its three answers.</summary>
    private Control BuildFastingBand()
    {
        var bandBorder = Color.FromArgb(0xF2, 0xC9, 0xA8);

        _fastingBand.Dock = DockStyle.Fill;
        _fastingBand.Height = 56;
        _fastingBand.Margin = new Padding(0, 0, 0, 12);
        _fastingBand.Padding = new Padding(16, 12, 16, 12);
        _fastingBand.BackColor = UiTheme.AbnormalBackground;
        _fastingBand.Visible = false;
        UiChrome.AsCard(_fastingBand, bandBorder);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
            BackColor = UiTheme.AbnormalBackground
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _fastingClock.ForeColor = UiTheme.Abnormal;
        _fastingClock.Anchor = AnchorStyles.Left;
        _fastingClock.Margin = new Padding(0, 0, 12, 0);

        _fastingBanner.Font = UiTheme.Body;
        _fastingBanner.ForeColor = UiTheme.Abnormal;
        _fastingBanner.AutoSize = false;
        _fastingBanner.AutoEllipsis = true;
        _fastingBanner.Dock = DockStyle.Fill;
        _fastingBanner.TextAlign = ContentAlignment.MiddleLeft;
        _fastingBanner.Margin = new Padding(0);

        _fastingChoice.AutoSize = true;
        _fastingChoice.WrapContents = false;
        _fastingChoice.Margin = new Padding(12, 0, 0, 0);
        _fastingChoice.Anchor = AnchorStyles.Right;
        _fastingChoice.BackColor = UiTheme.AbnormalBackground;
        _fastingChoice.Controls.AddRange([_fastedYes, _fastedNo, _fastedUnknown]);

        layout.Controls.Add(_fastingClock, 0, 0);
        layout.Controls.Add(_fastingBanner, 1, 0);
        layout.Controls.Add(_fastingChoice, 2, 0);

        _fastingBand.Controls.Add(layout);
        return _fastingBand;
    }

    /// <summary>« Tubes à prélever », « 2 sur 4 prélevés » and the scan box that ticks a tube.</summary>
    private Control BuildTubesHeader()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8),
            BackColor = UiTheme.Surface
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _tubesHeading.Font = UiTheme.SectionTitle;
        _tubesHeading.ForeColor = UiTheme.TextPrimary;
        _tubesHeading.AutoSize = true;
        _tubesHeading.Anchor = AnchorStyles.Left;
        _tubesHeading.Margin = new Padding(0, 0, 12, 0);

        _tubesProgress.Anchor = AnchorStyles.Left;
        _tubesProgress.Margin = new Padding(0);

        // A barcode is a Latin token; the field stays LTR in Arabic.
        _scan.Tag = UiTags.LatinValue;
        _scan.Font = UiTheme.Body;
        _scan.Width = 320;
        _scan.Anchor = AnchorStyles.Right;
        _scan.Margin = new Padding(12, 0, 0, 0);

        row.Controls.Add(_tubesHeading, 0, 0);
        row.Controls.Add(_tubesProgress, 1, 0);
        row.Controls.Add(new Panel { Dock = DockStyle.Fill, Height = 1, Margin = new Padding(0), BackColor = UiTheme.Surface }, 2, 0);
        row.Controls.Add(_scan, 3, 0);
        return row;
    }

    private Control BuildTubesGrid()
    {
        BuildTubeColumns();
        _tubes.Dock = DockStyle.Fill;
        _tubes.Margin = new Padding(0, 0, 0, 12);
        _tubes.RowTemplate.Height = TubeRowHeight;
        _tubes.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _tubes.ShowCellToolTips = true;
        _tubes.MinimumSize = new Size(0, 120);
        return _tubes;
    }

    /// <summary>« Prélevé par », « Heure », « Lieu », « Commentaire ».</summary>
    private Control BuildDetailsCard()
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            Height = 76,
            Margin = new Padding(0),
            Padding = new Padding(16, 10, 16, 10),
            BackColor = UiTheme.SurfaceRaised
        };
        UiChrome.AsCard(card);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 21));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        void Field(Label label, Control field, int column)
        {
            label.Font = UiTheme.SmallBold;
            label.ForeColor = UiTheme.TextSecondary;
            label.AutoSize = true;
            label.Margin = new Padding(0, 0, 16, 4);
            field.Dock = DockStyle.Fill;
            field.Margin = new Padding(0, 0, column == 3 ? 0 : 16, 0);
            layout.Controls.Add(label, column, 0);
            layout.Controls.Add(field, column, 1);
        }

        // Recorded by the service as the signed-in user, at the server's clock: shown, not typed.
        _collectedBy.ReadOnly = true;
        _collectedBy.TabStop = false;
        _collectedBy.BackColor = UiTheme.SurfaceSunken;
        _time.ReadOnly = true;
        _time.TabStop = false;
        _time.Tag = UiTags.LatinValue;
        _time.Font = UiTheme.Monospace;
        _time.BackColor = UiTheme.SurfaceSunken;

        _location.DropDownStyle = ComboBoxStyle.DropDownList;
        _location.FlatStyle = FlatStyle.Flat;
        _location.Font = UiTheme.Body;

        _comment.MaxLength = 500;

        Field(_collectedByLabel, _collectedBy, 0);
        Field(_timeLabel, _time, 1);
        Field(_locationLabel, _location, 2);
        Field(_commentLabel, _comment, 3);

        card.Controls.Add(layout);
        return card;
    }

    private void BuildPatientColumns()
    {
        _patients.Columns.Clear();

        // One painted column: the name and state on the first line, the file, age, tubes and
        // arrival on the second.
        _patients.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "patient", ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
    }

    private void BuildTubeColumns()
    {
        _tubes.Columns.Clear();
        _tubes.ReadOnly = false;
        _tubes.EditMode = DataGridViewEditMode.EditOnEnter;

        void Add(string name, int weight, Font? font = null, bool readOnly = true,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft, int minimum = 0)
        {
            var column = new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = readOnly,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body, Alignment = align
                }
            };
            if (minimum > 0) column.MinimumWidth = minimum;
            _tubes.Columns.Add(column);
        }

        Add(ColDrawOrder, 4, UiTheme.Monospace, align: DataGridViewContentAlignment.MiddleCenter, minimum: 32);

        // The cap colour, drawn as a tube: the fastest check the operator makes against the rack.
        Add(ColContainer, 3, minimum: 26);

        // Tube and specimen on two lines, with any special handling and the analyses.
        Add(ColTube, 34, minimum: 160);
        Add(ColBarcode, 14, UiTheme.Monospace, minimum: 96);
        Add(ColTime, 8, UiTheme.Monospace, minimum: 52);
        Add(ColVolume, 9, UiTheme.MonospaceBold, readOnly: false, DataGridViewContentAlignment.MiddleRight, minimum: 70);

        _tubes.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = ColSelected, FillWeight = 4, MinimumWidth = 34,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        Add(ColState, 14, UiTheme.SmallBold, minimum: 120);
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();

        _chipAll.Click += (_, _) => SetFilter(PatientFilter.All);
        _chipUrgent.Click += (_, _) => SetFilter(PatientFilter.Urgent);
        _chipFasting.Click += (_, _) => SetFilter(PatientFilter.Fasting);

        _patients.SelectionChanged += (_, _) => ShowSelectedPatient();

        // Writes run through RunBusyAsync, as on the billing screens: a double-click records one
        // action, and leaving the screen mid-write no longer disposes its context under it.
        _record.Click += async (_, _) => await RunBusyAsync(() => RecordAsync(dispatch: false));
        _dispatch.Click += async (_, _) => await RunBusyAsync(() => RecordAsync(dispatch: true));
        _reprint.Click += async (_, _) => await RunBusyAsync(ReprintAsync);
        _openFile.Click += async (_, _) => await OpenFileAsync();

        _fastedYes.Click += (_, _) => SetFastingAnswer(FastingAnswer.Yes);
        _fastedNo.Click += (_, _) => SetFastingAnswer(FastingAnswer.No);
        _fastedUnknown.Click += (_, _) => SetFastingAnswer(FastingAnswer.Unknown);

        _location.SelectedIndexChanged += (_, _) =>
        {
            if (!_loadingLocation) _locationChosen = true;
        };

        // A scanner terminates its scan with Enter.
        _scan.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            TickScanned(_scan.Text);
        };

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Samples.Collect, _record);
        PermissionGate.Require(Permissions.Samples.Dispatch, _dispatch);
        PermissionGate.Require(Permissions.Samples.Reprint, _reprint);
        PermissionGate.Require(Permissions.Orders.View, _openFile);

        _tubes.CellPainting += OnTubeCellPainting;
        _tubes.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && _tubes.Columns[e.ColumnIndex].Name == ColSelected) UpdateRemaining();
        };
        // A check box commits on the click, not when the cell is left, so the count follows it.
        _tubes.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_tubes.IsCurrentCellDirty && _tubes.CurrentCell is DataGridViewCheckBoxCell)
                _tubes.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        ShortcutHints.Attach(_reprint, "F8");
        ShortcutHints.Attach(_record, "F12");
    }

    /// <summary>F12 validates the draw and F8 prints the labels, as the action bar shows.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F12 when _record.Enabled && _record.Visible:
                _record.PerformClick();
                return true;
            case Keys.F8 when _reprint.Enabled && _reprint.Visible:
                _reprint.PerformClick();
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void ApplyTexts()
    {
        _patientsHeading.Text = T("SampleStatus.AwaitingCollection");
        // Icon only; the caption is its tooltip.
        _refresh.Text = string.Empty;
        _tips.SetToolTip(_refresh, $"{T("Action.Refresh")} (F5)");
        _record.Text = T("Collection.ValidateDraw");
        _dispatch.Text = T("Collection.DispatchToBench");
        _reprint.Text = T("Action.PrintLabels");
        _openFile.Text = T("Collection.OpenFile");

        // The captions follow the design system; the icons stay those of « Prélever » and « Expédier ».
        ButtonIcons.Set(_record, Glyph.Syringe, always: true);
        ButtonIcons.Set(_dispatch, Glyph.Truck, always: true);

        // Sized by hand: an auto-sized button does not count the icon or the key drawn on it, and
        // the bar's buttons then overlapped on a 1366-pixel screen.
        _reprint.MinimumSize = new Size(ButtonWidth(_reprint, "F8"), 34);
        _dispatch.MinimumSize = new Size(ButtonWidth(_dispatch), 34);
        _record.MinimumSize = new Size(ButtonWidth(_record, "F12"), 34);
        _openFile.MinimumSize = new Size(ButtonWidth(_openFile), 34);

        _fastedYes.Text = T("Action.Yes");
        _fastedNo.Text = T("Action.No");
        _fastedUnknown.Text = T("Collection.FastingUnknown");

        _tubesHeading.Text = T("Collection.TubesHeading");
        _scan.PlaceholderText = T("Collection.ScanPlaceholder");
        _tips.SetToolTip(_scan, T("Sample.Barcode"));

        _collectedByLabel.Text = T("Sample.CollectedBy");
        _timeLabel.Text = T("Field.Time");
        _locationLabel.Text = T("Sample.CollectionLocation");
        _commentLabel.Text = T("Field.Comment");
        _comment.PlaceholderText = T("Collection.CommentPlaceholder");
        _collectedBy.Text = _currentUser?.FullName ?? string.Empty;

        FillLocations();

        if (_tubes.Columns.Count > 0)
        {
            _tubes.Column(ColSelected).HeaderText = string.Empty;
            _tubes.Column(ColDrawOrder).HeaderText = "#";
            _tubes.Column(ColContainer).HeaderText = string.Empty;
            _tubes.Column(ColTube).HeaderText = T("Sample.ContainerType");
            _tubes.Column(ColBarcode).HeaderText = T("Sample.Barcode");
            _tubes.Column(ColTime).HeaderText = T("Field.Time");
            _tubes.Column(ColVolume).HeaderText = T("Sample.Volume");
            _tubes.Column(ColState).HeaderText = T("Field.Status");
        }

        RenderPatients();
    }

    private static int ButtonWidth(Button button, string? hint = null) =>
        TextRenderer.MeasureText(Mnemonics.Plain(button.Text), button.Font).Width + 28 + 24
        + (hint is null ? 0 : ShortcutHints.Width(hint));

    /// <summary>The places of collection, in the current language; the selection is kept.</summary>
    private void FillLocations()
    {
        var selected = (_location.SelectedItem as LocationChoice)?.Value;

        _loadingLocation = true;
        _location.BeginUpdate();
        _location.Items.Clear();

        foreach (var place in Enum.GetValues<CollectionLocation>())
            _location.Items.Add(new LocationChoice(place, T($"Collection.Location.{place}")));

        if (selected is { } value) SelectLocation(value);

        _location.EndUpdate();
        _loadingLocation = false;
    }

    private void SelectLocation(CollectionLocation value)
    {
        for (var i = 0; i < _location.Items.Count; i++)
            if (_location.Items[i] is LocationChoice choice && choice.Value == value)
                _location.SelectedIndex = i;
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            // Everything is loaded and the chips filter on screen, so each chip can show its count.
            // « Urgents » keeps the server's rule: any priority above routine.
            var result = await _workflow.GetCollectionWorklistAsync(false, ct);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            _entries = result.Value;
            RenderPatients();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer navigation.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the collection worklist failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private void SetFilter(PatientFilter filter)
    {
        _filter = filter;
        _chipAll.Selected = filter == PatientFilter.All;
        _chipUrgent.Selected = filter == PatientFilter.Urgent;
        _chipFasting.Selected = filter == PatientFilter.Fasting;
        RenderPatients();
    }

    /// <summary>The patients' orders, one group per order, in priority then arrival order.</summary>
    private List<IGrouping<Guid, CollectionWorklistEntry>> Orders(PatientFilter filter) =>
    [
        .. _entries
            .GroupBy(e => e.LabOrderId)
            .Where(g => filter switch
            {
                PatientFilter.Urgent => g.First().Priority != OrderPriority.Routine,
                PatientFilter.Fasting => g.Max(e => e.RequiredFastingHours) > 0,
                _ => true
            })
            .OrderByDescending(g => g.First().Priority)
            .ThenBy(g => g.First().RegisteredAt)
    ];

    private void RenderPatients()
    {
        var previous = SelectedOrderId();

        var all = Orders(PatientFilter.All);
        var shown = Orders(_filter);

        _chipAll.Text = T("Collection.FilterAll", all.Count);
        _chipUrgent.Text = T("Collection.FilterUrgent", Orders(PatientFilter.Urgent).Count);
        _chipFasting.Text = T("Collection.FilterFasting", Orders(PatientFilter.Fasting).Count);

        _patientsCount.Text = T("Collection.PatientsAndTubes", all.Count, _entries.Count);

        _patients.SuspendLayout();
        _patients.Rows.Clear();

        // One row per order, not per tube: the operator picks a patient, not a tube.
        foreach (var group in shown)
        {
            var index = _patients.Rows.Add(DisplayName(group.First()));
            _patients.Rows[index].Tag = group.Key;
        }

        _patients.ResumeLayout(performLayout: true);

        if (_patients.Rows.Count == 0)
        {
            ClearPatientPanel();
            return;
        }

        // Keep the patient being drawn selected across a refresh; otherwise the first one. Setting
        // Selected alone highlighted the row without making it current, so the tube panel stayed
        // empty and "Prélever" disabled until the operator clicked the row again.
        var row = _patients.Rows.Cast<DataGridViewRow>()
                      .FirstOrDefault(r => r.Tag is Guid id && id == previous)
                  ?? _patients.Rows[0];

        _patients.CurrentCell = row.Cells[0];
        row.Selected = true;
        ShowSelectedPatient();
    }

    private string DisplayName(CollectionWorklistEntry entry) =>
        Localizer.CurrentLanguage == AppLanguage.Arabic && entry.PatientNameArabic is { Length: > 0 }
            ? entry.PatientNameArabic
            : entry.PatientName;

    /// <summary>
    /// One patient: the name — in the priority's colour when urgent — and the state pill (« 3 à
    /// prélever », or « Prélevé · à expédier » once all are drawn); then the file number, the age, the
    /// tubes and the arrival time, and the fasting the draw requires.
    /// </summary>
    private void PaintPatientCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.Graphics is null) return;
        if (_patients.Rows[e.RowIndex].Tag is not Guid orderId) return;

        var set = _entries.Where(x => x.LabOrderId == orderId).ToList();
        if (set.Count == 0) return;

        var first = set[0];
        var graphics = e.Graphics;
        var bounds = e.CellBounds;
        var rtl = IsRightToLeft;
        var selected = (e.State & DataGridViewElementStates.Selected) != 0;
        var alerted = set.Any(x => x.PatientAlerts.Count > 0);
        var toDraw = set.Count(x => !x.IsCollected);

        var background = selected ? UiTheme.AccentSubtle
                       : alerted ? UiTheme.CriticalBackground
                       : toDraw == 0 ? UiTheme.NormalBackground
                       : UiTheme.SurfaceRaised;

        using (var fill = new SolidBrush(background))
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
        var inner = new Rectangle(bounds.X + pad, bounds.Y + 9, bounds.Width - 2 * pad, bounds.Height - 18);
        var alignment = rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left;
        var opposite = rtl ? TextFormatFlags.Left | TextFormatFlags.RightToLeft : TextFormatFlags.Right;
        const TextFormatFlags single = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                                       | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis
                                       | TextFormatFlags.VerticalCenter;

        // Line 1: the name, and the state pill at the far end.
        var pillText = toDraw > 0 ? T("Collection.ToDraw", toDraw) : T("Collection.ReadyToDispatch");
        var pillColours = toDraw > 0 ? EntryPaint.Waiting : (UiTheme.SurfaceRaised, UiTheme.Normal);
        var pillSize = EntryPaint.MeasurePill(pillText, UiTheme.SmallBold, 8);
        var topLine = new Rectangle(inner.X, inner.Y, inner.Width, 20);

        var pill = EntryPaint.Mirror(new Rectangle(topLine.Right - pillSize.Width, topLine.Y + 1,
                                                   pillSize.Width, pillSize.Height), bounds, rtl);
        EntryPaint.DrawPill(graphics, pill, pillText, UiTheme.SmallBold, pillColours, rtl);

        var name = (alerted ? "⚠ " : string.Empty) + DisplayName(first);
        var nameBounds = EntryPaint.Mirror(new Rectangle(topLine.X, topLine.Y, topLine.Width - pillSize.Width - 8, 20),
                                           bounds, rtl);
        TextRenderer.DrawText(graphics, name, UiTheme.BodyBold, nameBounds,
                              first.Priority != OrderPriority.Routine ? UiTheme.ForPriority(first.Priority) : UiTheme.TextPrimary,
                              single | alignment);

        // Line 2: file · age · tubes · arrival, and the fasting or the priority at the far end.
        var bottom = new Rectangle(inner.X, inner.Y + 22, inner.Width, 18);
        var fasting = set.Max(x => x.RequiredFastingHours);
        var (endText, endColour) = fasting > 0
            ? (T("Collection.FastingHours", fasting), UiTheme.Abnormal)
            : first.Priority != OrderPriority.Routine
                ? (T($"Priority.{first.Priority}"), UiTheme.ForPriority(first.Priority))
                : (string.Empty, UiTheme.TextSecondary);
        var endWidth = endText.Length == 0
            ? 0
            : TextRenderer.MeasureText(endText, UiTheme.SmallBold, Size.Empty, TextFormatFlags.NoPadding).Width;

        TextRenderer.DrawText(graphics, endText, UiTheme.SmallBold, EntryPaint.Mirror(bottom, bounds, rtl),
                              endColour, single | opposite);

        var fileWidth = TextRenderer.MeasureText(first.AccessionNumber, UiTheme.Monospace, Size.Empty,
                                                 TextFormatFlags.NoPadding).Width;
        var fileBounds = new Rectangle(bottom.X, bottom.Y, Math.Min(fileWidth, bottom.Width - endWidth - 8), bottom.Height);
        TextRenderer.DrawText(graphics, first.AccessionNumber, UiTheme.Monospace, EntryPaint.Mirror(fileBounds, bounds, rtl),
                              UiTheme.TextSecondary, single | alignment);

        var facts = new List<string>();
        if (first.AgeYears is { } age) facts.Add($"{age} {T("Report.Years")}");
        facts.Add(set.Count == 1 ? T("Collection.OneTube") : T("Collection.ManyTubes", set.Count));
        facts.Add($"{T("Collection.Registered")} {first.RegisteredAt.ToLocalTime():HH:mm}");

        var factsBounds = new Rectangle(fileBounds.Right, bottom.Y,
                                        Math.Max(0, bottom.Width - fileBounds.Width - endWidth - 8), bottom.Height);
        TextRenderer.DrawText(graphics, " · " + string.Join(" · ", facts), UiTheme.Small,
                              EntryPaint.Mirror(factsBounds, bounds, rtl), UiTheme.TextSecondary, single | alignment);

        e.Handled = true;
    }

    private Guid? SelectedOrderId() =>
        (_patients.CurrentRow ?? _patients.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault())?.Tag as Guid?;

    private void ShowSelectedPatient()
    {
        if (SelectedOrderId() is not { } orderId)
        {
            ClearPatientPanel();
            return;
        }

        var previousOrder = _currentSet.Count > 0 ? _currentSet[0].LabOrderId : (Guid?)null;

        // Selecting a tube selects the patient's whole set: they are drawn together, in draw order.
        _currentSet = [.. _entries.Where(e => e.LabOrderId == orderId).OrderBy(e => e.DrawOrder)];

        if (_currentSet.Count == 0)
        {
            ClearPatientPanel();
            return;
        }

        var first = _currentSet[0];

        // Another patient: the answers given for the previous one say nothing about this one.
        if (previousOrder != orderId)
        {
            _fastingAnswer = FastingAnswer.NotAsked;
            _comment.Clear();
            _locationChosen = false;
            _loadingLocation = true;
            SelectLocation(first.CollectionLocation);
            _loadingLocation = false;
        }

        _patientCard.Visible = true;
        _initials.Text = Initials(DisplayName(first));
        _initials.Fill = first.Priority != OrderPriority.Routine ? UiTheme.ForPriority(first.Priority) : UiTheme.Accent;
        _initials.Invalidate();
        _patientBanner.Text = DisplayName(first);

        var identity = new List<string> { T($"Gender.{first.Gender}") };
        if (first.AgeYears is { } age) identity.Add($"{age} {T("Report.Years")}");
        if (first.DateOfBirth is { } dob) identity.Add(T("Collection.BornOn", dob.ToString("dd/MM/yyyy")));
        identity.Add(T("Collection.OrderNumber", first.AccessionNumber));
        if (first.Priority != OrderPriority.Routine) identity.Add(T($"Priority.{first.Priority}").ToUpperInvariant());

        _patientDetails.Text = string.Join(" · ", identity);
        _tips.SetToolTip(_patientDetails, _patientDetails.Text);

        ShowFasting();

        // Patient alerts — biohazard, difficult draw, unpaid balance.
        var alerts = _currentSet.SelectMany(e => e.PatientAlerts)
                                .Select(a => a.For(Localizer.CurrentLanguage))
                                .Distinct()
                                .ToList();

        if (alerts.Count > 0)
        {
            _alertBanner.Text = $"⚠ {string.Join("   ·   ", alerts)}";
            _tips.SetToolTip(_alertBanner, _alertBanner.Text);
            _alertBanner.Visible = true;
        }
        else
        {
            _alertBanner.Visible = false;
        }

        _time.Text = (_clock?.Now ?? DateTimeOffset.Now).ToLocalTime().ToString("dd/MM/yyyy HH:mm");

        RenderTubes();
    }

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
    }

    /// <summary>
    /// The fasting band, stated before the draw. The patient's earlier declaration is shown as
    /// pressed; the operator can answer here, or leave it and be asked at the draw, as before.
    /// </summary>
    private void ShowFasting()
    {
        var fasting = _currentSet.Count == 0 ? 0 : _currentSet.Max(e => e.RequiredFastingHours);

        if (fasting <= 0)
        {
            _fastingBand.Visible = false;
            return;
        }

        var declared = _currentSet[0].PatientDeclaredFasted;
        var answer = _fastingAnswer switch
        {
            FastingAnswer.Yes => true,
            FastingAnswer.No => false,
            FastingAnswer.Unknown => (bool?)null,
            _ => declared
        };

        var analyses = _currentSet.Where(e => e.RequiredFastingHours > 0)
                                  .SelectMany(e => e.AnalysisCodes)
                                  .Distinct()
                                  .ToList();
        var forWhat = analyses.Count > 0 ? " " + T("Collection.FastingFor", string.Join(", ", analyses)) : string.Empty;

        var verdict = answer switch
        {
            true => $"{T("Order.Fasted")} ✓",
            false => T("SampleDefect.FastingNotRespected"),
            _ when _fastingAnswer == FastingAnswer.Unknown => T("Collection.FastingUnknown"),
            _ => T("Collection.FastingBandQuestion")
        };

        _fastingBanner.Text = $"{T("Catalog.FastingRequired")} : {fasting} h{forWhat}. {verdict}";
        _fastingBanner.Font = UiTheme.BodyBold;
        _fastingBanner.ForeColor = answer is false ? UiTheme.Critical : UiTheme.Abnormal;
        _tips.SetToolTip(_fastingBanner, _fastingBanner.Text);

        _fastedYes.Pressed = answer is true;
        _fastedNo.Pressed = answer is false;
        _fastedUnknown.Pressed = _fastingAnswer == FastingAnswer.Unknown;

        _fastingBand.Visible = true;
    }

    private void SetFastingAnswer(FastingAnswer answer)
    {
        _fastingAnswer = answer;
        ShowFasting();
    }

    private void RenderTubes()
    {
        _tubes.SuspendLayout();
        _tubes.Rows.Clear();

        var position = 1;
        var anyAwaiting = _currentSet.Any(e => !e.IsCollected);

        foreach (var entry in _currentSet)
        {
            var handling = Handling(entry);

            // Tubes still to draw are ticked; once they are all drawn, the drawn ones are ticked
            // instead, ready for "Expédier".
            var ticked = anyAwaiting ? !entry.IsCollected : true;

            var index = _tubes.Rows.Add(
                position.ToString(),
                string.Empty,                           // painted tube
                entry.ContainerName?.For(Localizer.CurrentLanguage) ?? "-",
                entry.Barcode,
                entry.CollectedAt is { } drawn ? drawn.ToLocalTime().ToString("HH:mm") : "—",
                entry.NominalVolumeMl?.ToString("N1") ?? string.Empty,
                ticked,
                entry.IsCollected ? T("SampleStatus.Collected") : T("SampleStatus.AwaitingCollection"));

            var row = _tubes.Rows[index];
            row.Tag = entry.SampleId;

            // Drawn or not is the first thing read off the row: a green band once drawn.
            if (entry.IsCollected)
            {
                row.DefaultCellStyle.BackColor = Color.FromArgb(0xF6, 0xFB, 0xF8);
                row.Cells[ColVolume].ReadOnly = true;
            }

            var tooltip = new List<string>();

            if (entry.CollectionInstructions is { } instructions)
                tooltip.Add(instructions.For(Localizer.CurrentLanguage));

            if (handling.Count > 0) tooltip.Add(string.Join(" · ", handling));

            if (entry.MinimumVolumeMl is { } minimum)
                tooltip.Add($"{T("Sample.Volume")} ≥ {minimum:N1} mL");

            if (entry.AnalysisCodes.Count > 0)
                tooltip.Add($"{T("Catalog.Analyses")} : {string.Join(" ", entry.AnalysisCodes)}");

            if (tooltip.Count > 0)
                row.Cells[ColTube].ToolTipText = string.Join(Environment.NewLine, tooltip);

            position++;
        }

        _tubes.ResumeLayout(performLayout: true);

        var drawnCount = _currentSet.Count(e => e.IsCollected);
        _tubesProgress.Show(T("Collection.DrawnOfTotal", drawnCount, _currentSet.Count),
                            drawnCount == _currentSet.Count ? (UiTheme.NormalBackground, UiTheme.Normal)
                            : drawnCount == 0 ? EntryPaint.Waiting
                            : (UiTheme.NormalBackground, UiTheme.Normal));

        SetActionsEnabled(_tubes.Rows.Count > 0);
        _record.Enabled = anyAwaiting;
        UpdateRemaining();
    }

    private List<string> Handling(CollectionWorklistEntry entry)
    {
        var handling = new List<string>();
        if (entry.RequiresLightProtection) handling.Add(T("Collection.ProtectFromLight"));
        if (entry.RequiresIceTransport) handling.Add(T("Collection.TransportOnIce"));
        return handling;
    }

    /// <summary>« 2 tubes restent à cocher »: the tubes still to draw that are not ticked.</summary>
    private void UpdateRemaining()
    {
        if (_currentSet.Count == 0)
        {
            _remaining.Text = string.Empty;
            return;
        }

        var ticked = SelectedSampleIds().ToHashSet();
        var left = _currentSet.Count(e => !e.IsCollected && !ticked.Contains(e.SampleId));

        _remaining.Text = left == 0
            ? _currentSet.All(e => e.IsCollected) ? T("Collection.ReadyToDispatch") : string.Empty
            : left == 1 ? T("Collection.RemainingToTickOne") : T("Collection.RemainingToTick", left);
    }

    /// <summary>
    /// Paints the tube in its cap colour, the tube and specimen on two lines, and the state pill.
    /// The colour is drawn rather than written because it <em>is</em> the information; the tube's
    /// name beside it carries the same fact in words for a monochrome or low-contrast screen.
    /// </summary>
    private void OnTubeCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.Graphics is null) return;
        if (_tubes.Rows[e.RowIndex].Tag is not Guid id) return;

        var entry = _currentSet.FirstOrDefault(x => x.SampleId == id);
        if (entry is null) return;

        var name = _tubes.Columns[e.ColumnIndex].Name;
        var rtl = IsRightToLeft;
        var bounds = e.CellBounds;
        var alignment = rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left;
        const TextFormatFlags single = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                                       | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;

        switch (name)
        {
            case ColContainer:
            {
                e.PaintBackground(e.ClipBounds, (e.State & DataGridViewElementStates.Selected) != 0);
                var tube = new Rectangle(bounds.X + (bounds.Width - 14) / 2, bounds.Y + 5, 14, bounds.Height - 10);
                CollectionTubeCap.Draw(e.Graphics, tube, entry.ContainerColourHex);
                e.Handled = true;
                return;
            }

            case ColTube:
            {
                e.PaintBackground(e.ClipBounds, (e.State & DataGridViewElementStates.Selected) != 0);

                var inner = new Rectangle(bounds.X + 6, bounds.Y + 6, bounds.Width - 12, bounds.Height - 12);
                var top = new Rectangle(inner.X, inner.Y, inner.Width, inner.Height / 2);
                var below = new Rectangle(inner.X, inner.Y + inner.Height / 2, inner.Width, inner.Height - inner.Height / 2);

                TextRenderer.DrawText(e.Graphics, entry.ContainerName?.For(Localizer.CurrentLanguage) ?? "-",
                                      UiTheme.BodyBold, top, UiTheme.TextPrimary, single | alignment);

                // The specimen, then any special handling in warning ink and the analyses.
                var handling = Handling(entry);
                var specimen = entry.SpecimenTypeName.For(Localizer.CurrentLanguage);
                var second = new List<string> { specimen };
                if (handling.Count > 0) second.Add(string.Join(" · ", handling));
                if (entry.AnalysisCodes.Count > 0) second.Add(string.Join(" ", entry.AnalysisCodes));

                TextRenderer.DrawText(e.Graphics, string.Join(" · ", second), UiTheme.Small, below,
                                      handling.Count > 0 ? UiTheme.Abnormal : UiTheme.TextSecondary,
                                      single | alignment);
                e.Handled = true;
                return;
            }

            case ColState:
            {
                e.PaintBackground(e.ClipBounds, (e.State & DataGridViewElementStates.Selected) != 0);

                var text = entry.IsCollected
                    ? $"✓ {T("SampleStatus.Collected")}"
                    : T("SampleStatus.AwaitingCollection");
                var colours = entry.IsCollected ? (UiTheme.NormalBackground, UiTheme.Normal) : EntryPaint.Warning;
                var size = EntryPaint.MeasurePill(text, UiTheme.SmallBold);
                var pill = EntryPaint.Mirror(
                    new Rectangle(bounds.X + 8, bounds.Y + (bounds.Height - size.Height) / 2,
                                  Math.Min(size.Width, bounds.Width - 16), size.Height),
                    bounds, rtl);
                EntryPaint.DrawPill(e.Graphics, pill, text, UiTheme.SmallBold, colours, rtl);
                e.Handled = true;
                return;
            }
        }
    }

    private void ClearPatientPanel()
    {
        _currentSet = [];
        _tubes.Rows.Clear();
        _patientBanner.Text = T("App.NoData");
        _patientDetails.Text = string.Empty;
        _initials.Text = string.Empty;
        _fastingBand.Visible = false;
        _alertBanner.Visible = false;
        _tubesProgress.Show(string.Empty, EntryPaint.Waiting);
        _remaining.Text = string.Empty;
        SetActionsEnabled(false);
    }

    private void SetActionsEnabled(bool enabled)
    {
        _record.Enabled = enabled;
        _dispatch.Enabled = enabled;
        _reprint.Enabled = enabled;
        _openFile.Enabled = enabled;
    }

    // ---- Scan ------------------------------------------------------------------------------------------

    /// <summary>
    /// Ticks the scanned tube. A tube of another patient on the list brings that patient up first:
    /// the label in the operator's hand is the most reliable identification there is.
    /// </summary>
    private void TickScanned(string text)
    {
        var barcode = text.Trim();
        if (barcode.Length == 0) return;

        // Equality, not a partial match: a near barcode is another tube.
        var entry = _entries.FirstOrDefault(e => e.Barcode == barcode);

        if (entry is null)
        {
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T("Collection.ScanUnknown", barcode);
            _scan.SelectAll();
            return;
        }

        if (_currentSet.All(e => e.SampleId != entry.SampleId))
        {
            if (_filter != PatientFilter.All
                && _patients.Rows.Cast<DataGridViewRow>().All(r => r.Tag is not Guid g || g != entry.LabOrderId))
                SetFilter(PatientFilter.All);

            var patientRow = _patients.Rows.Cast<DataGridViewRow>()
                                 .FirstOrDefault(r => r.Tag is Guid g && g == entry.LabOrderId);
            if (patientRow is null) return;

            _patients.CurrentCell = patientRow.Cells[0];
            patientRow.Selected = true;
            ShowSelectedPatient();
        }

        var row = _tubes.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is Guid id && id == entry.SampleId);

        if (row is not null)
        {
            row.Cells[ColSelected].Value = true;
            _tubes.CurrentCell = row.Cells[ColVolume].ReadOnly ? row.Cells[ColSelected] : row.Cells[ColVolume];
            UpdateRemaining();
        }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Collection.ScanTicked", barcode);
        _scan.Clear();
        _scan.Focus();
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private List<Guid> SelectedSampleIds() =>
    [
        .. _tubes.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells[ColSelected].Value is true && r.Tag is Guid)
            .Select(r => (Guid)r.Tag!)
    ];

    private async Task OpenFileAsync()
    {
        if (_navigation is null || _currentSet.Count == 0) return;
        if (!PermissionGate.Allows(Permissions.Orders.View)) return;

        var orderId = _currentSet[0].LabOrderId;
        await _navigation.NavigateToAsync<OrderDetailView>(view => view.OrderId = orderId);
    }

    /// <summary>
    /// "Prélever" records the draw of the ticked tubes; "Expédier" sends the ticked tubes, drawing
    /// first any that were not drawn yet. Drawn tubes stay on the list until they are sent or
    /// received, so the collection point can draw a series of patients and dispatch them together.
    /// </summary>
    private async Task RecordAsync(bool dispatch)
    {
        _tubes.EndEdit();

        var selected = SelectedSampleIds();

        if (selected.Count == 0)
        {
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Message.NothingSelected");
            return;
        }

        var collected = _currentSet.Where(e => e.IsCollected).Select(e => e.SampleId).ToHashSet();
        var toDraw = selected.Where(id => !collected.Contains(id)).ToList();

        if (!dispatch && toDraw.Count == 0)
        {
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Collection.AlreadyCollected");
            return;
        }

        // The volumes first: one typed and unreadable, or impossible, is said before anything is
        // asked or recorded. It used to be dropped without a word and the tube recorded with no
        // volume, and 5 000 000 typed for 5 mL was kept as what the tube held.
        var volumes = new List<(Guid Id, decimal? Volume)>(toDraw.Count);

        foreach (DataGridViewRow row in _tubes.Rows)
        {
            if (row.Tag is not Guid id || !toDraw.Contains(id)) continue;

            var typed = row.Cells[ColVolume].Value?.ToString()?.Trim();

            decimal? volume = decimal.TryParse(
                typed?.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;

            if ((!string.IsNullOrEmpty(typed) && volume is null)
                || volume is <= 0m or >= Sample.MaximumVolumeMl)
            {
                var barcode = _currentSet.FirstOrDefault(e => e.SampleId == id)?.Barcode ?? string.Empty;
                _dialogs.Warn(T("Sample_CollectedVolumeInvalid", barcode), T("Sample.Volume"));
                _tubes.CurrentCell = row.Cells[ColVolume];
                return;
            }

            volumes.Add((id, volume));
        }

        // Where fasting matters, the answer given in the band is used. Not answered there and not
        // declared before, it is asked now — before the draw is recorded, because afterwards the
        // answer cannot honestly be obtained. « Ne sait pas » records nothing.
        bool? fasted = null;
        var requiredFasting = _currentSet.Max(e => e.RequiredFastingHours);

        if (toDraw.Count > 0 && requiredFasting > 0)
        {
            switch (_fastingAnswer)
            {
                case FastingAnswer.Yes:
                    fasted = true;
                    break;
                case FastingAnswer.No:
                    fasted = false;
                    break;
                case FastingAnswer.NotAsked when _currentSet[0].PatientDeclaredFasted is null:
                    fasted = _dialogs.Confirm(
                        T("Collection.FastingQuestion", requiredFasting),
                        T("Catalog.FastingRequired"));
                    break;
            }
        }

        var location = _locationChosen ? (_location.SelectedItem as LocationChoice)?.Value : null;
        var note = string.IsNullOrWhiteSpace(_comment.Text) ? null : _comment.Text.Trim();

        var requests = new List<RecordCollectionRequest>(selected.Count);

        foreach (var (id, volume) in volumes)
        {
            requests.Add(new RecordCollectionRequest
            {
                SampleId = id,
                VolumeMl = volume,
                PatientFasted = fasted,
                DeclaredFastingHours = fasted == true ? requiredFasting : null,
                Location = location,
                Note = note
            });
        }

        _record.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            if (requests.Count > 0)
            {
                var result = await _workflow.RecordCollectionAsync(requests);

                if (result.IsFailure)
                {
                    _dialogs.ShowFailure(result);
                    return;
                }

                _status.ForeColor = UiTheme.Normal;
                _status.Text = $"{requests.Count} {T("SampleStatus.Collected").ToLowerInvariant()}";
                _comment.Clear();
            }

            if (dispatch)
            {
                var sent = await _workflow.DispatchAsync(selected);

                if (sent.IsFailure)
                {
                    _dialogs.ShowFailure(sent);
                }
                else
                {
                    _status.ForeColor = UiTheme.Normal;
                    _status.Text = $"{selected.Count} {T("SampleStatus.InTransit").ToLowerInvariant()}";
                }
            }

            await ActivateAsync();
        }
        finally
        {
            Cursor = Cursors.Default;
            _record.Enabled = _tubes.Rows.Count > 0 && _currentSet.Any(e => !e.IsCollected);
        }
    }

    /// <summary>
    /// Reprints the patient's labels.
    /// <para>
    /// A reason is required and recorded: two tubes bearing one barcode is the mechanism by which
    /// one patient's sample gets reported under another patient's name, so a reprint is a logged
    /// event rather than a convenience.
    /// </para>
    /// </summary>
    private async Task ReprintAsync()
    {
        if (_currentSet.Count == 0) return;

        var reason = _dialogs.PromptForReason(
            T("Sample.ReprintReasonPrompt"), T("Action.PrintLabels"), maxLength: 500);

        if (reason is null) return;

        try
        {
            var result = await _workflow.ReprintLabelsAsync(_currentSet[0].LabOrderId, reason);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            var emitted = await LabelOutput.EmitAsync(_labelPrinter, _documents, _labels.Render,
                                                       result.Value);

            if (emitted.IsFailure) { _dialogs.ShowFailure(emitted, T("Action.PrintLabels")); return; }

            _status.ForeColor = UiTheme.Normal;
            _status.Text = emitted.Value.Destination == LabelDestination.Printer
                ? T("Sample.LabelsPrinted", emitted.Value.Count)
                : T("Sample.LabelsAsPdf", emitted.Value.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Reprinting labels failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private sealed record LocationChoice(CollectionLocation Value, string Display)
    {
        public override string ToString() => Display;
    }
}
