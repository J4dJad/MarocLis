using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Samples;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Samples;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Sample reception — the bench that decides whether a tube is fit to analyse.
/// <para>
/// ISO 15189 §5.4 puts the acceptance decision here, and the screen is built so that the decision
/// is deliberate rather than reflexive:
/// </para>
/// <list type="bullet">
/// <item><b>The scan box always holds focus.</b> Reception is a scanning loop, not a form.</item>
/// <item><b>The stability verdict is computed and stated</b> — "delay exceeded" rather than two
/// timestamps and a table the operator has to reason about under pressure.</item>
/// <item><b>Accepting a defective tube is a separate button</b> from accepting a conforming one.
/// The domain refuses a silent acceptance, and the UI reflects that instead of hiding it behind a
/// checkbox somebody leaves ticked.</item>
/// <item><b>A rejection reason comes from a closed list.</b> The rejection rate by reason is a
/// mandatory quality indicator, and free text cannot be counted.</item>
/// </list>
/// <para>
/// Laid out as the design system's « Réception des échantillons » screen: one card for the scanned
/// tube — scan box, the tube's band with its transport delay, the non-conformities as toggles, the
/// conditions and the decision — and below it the card of tubes awaiting reception, with chips.
/// </para>
/// </summary>
public sealed class SampleReceptionView : LocalizedView
{
    private const string ColBarcode = "barcode";
    private const string ColAccession = "accession";
    private const string ColPatient = "patient";
    private const string ColSpecimen = "specimen";
    private const string ColPriority = "priority";
    private const string ColCollected = "collected";
    private const string ColElapsed = "elapsed";
    private const string ColVerdict = "verdict";

    private readonly SampleWorkflowService _workflow;
    private readonly ISampleLabelRenderer _labels;
    private readonly ILabelPrinter _labelPrinter;
    private readonly IDocumentOutput _documents;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<SampleReceptionView>? _logger;

    // Scan
    private readonly TextBox _scan = UiTheme.Input(320);
    private readonly Label _scanLabel = new();
    private readonly Label _scanHint = new();

    // Candidate detail
    private readonly Panel _detail = new();
    private readonly CollectionTubeCapControl _detailCap = new();
    private readonly Label _patientLine = new();
    private readonly Label _accessionLine = new();
    private readonly Label _identityLine = new();
    private readonly PillLabel _priorityPill = new();
    private readonly Label _orderLine = new();
    private readonly Label _tubeLine = new();
    private readonly Label _delayCaption = new();
    private readonly Label _delayValue = new();
    private readonly Label _delayLine = new();

    // Conformity
    private readonly Label _defectLegend = new();
    private readonly FlowLayoutPanel _defectBox = new();
    private readonly Dictionary<SampleDefect, CheckBox> _defectChecks = [];
    private readonly Label _temperatureLabel = new();
    private readonly TextBox _temperature = UiTheme.Input(120);
    private readonly CheckBox _coldChain = new();
    private readonly Label _storageLabel = new();
    private readonly ComboBox _storage = new();
    private IReadOnlyList<string> _storagePlaces = [];
    private Control? _temperatureField;

    // Actions
    private readonly Button _accept = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _acceptReserved = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _reject = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _rejectReasonLabel = new();
    private readonly ComboBox _rejectReason = new();
    private readonly CheckBox _requestRecollection = new();
    private readonly Panel _statusStrip = new();
    private readonly Label _status = new();
    private readonly ToolTip _tips = new();

    // Pending list
    private readonly DataGridView _awaiting = UiTheme.Grid();
    private readonly Label _awaitingHeading = new();
    private readonly Label _awaitingCount = new();
    private readonly ChipButton _chipAll = new();
    private readonly ChipButton _chipUrgent = new();
    private readonly ChipButton _chipLongDelay = new();

    private SampleReceptionCandidate? _candidate;
    private IReadOnlyList<SampleReceptionCandidate> _awaitingRows = [];
    private AwaitingFilter _filter = AwaitingFilter.All;

    // Set while an accept or reject for the displayed tube is being recorded. A scan arriving then
    // is held and shown once the decision is in, rather than replacing the tube being decided.
    private bool _deciding;
    private string? _queuedScan;

    public SampleReceptionView(SampleWorkflowService workflow, ISampleLabelRenderer labels, ILabelPrinter labelPrinter, IDocumentOutput documents,
                              IUserDialogs dialogs, ILocalizer localizer,
                              ICultureManager cultureManager,
                              ILogger<SampleReceptionView>? logger = null)
        : base(localizer, cultureManager)
    {
        _workflow = workflow;
        _labels = labels;
        _labelPrinter = labelPrinter;
        _documents = documents;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.SampleReception");

    private bool IsRightToLeft => RightToLeft == RightToLeft.Yes;

    private enum AwaitingFilter { All, Urgent, LongDelay }

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(0);

        var statusStrip = BuildStatusStrip();

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(24, 16, 24, 12),
            Margin = new Padding(0),
            BackColor = UiTheme.Surface
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        content.Controls.Add(BuildScanCard(), 0, 0);
        content.Controls.Add(BuildAwaitingCard(), 0, 1);

        Controls.Add(content);
        Controls.Add(statusStrip);

        SetActionsEnabled(false);
    }

    private Control BuildStatusStrip()
    {
        _statusStrip.Dock = DockStyle.Bottom;
        _statusStrip.Height = 26;
        _statusStrip.Padding = new Padding(24, 2, 24, 4);
        _statusStrip.BackColor = UiTheme.Surface;
        _statusStrip.Tag = UiTags.OwnLayout;

        _status.Dock = DockStyle.Fill;
        _status.AutoSize = false;
        _status.AutoEllipsis = true;
        _status.Font = UiTheme.Small;
        _status.ForeColor = UiTheme.TextSecondary;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.TextChanged += (_, _) => _tips.SetToolTip(_status, _status.Text);
        _statusStrip.Controls.Add(_status);

        return _statusStrip;
    }

    /// <summary>The scanned tube: scan box, the tube's band, the non-conformities, the decision.</summary>
    private Control BuildScanCard()
    {
        // The card is the table itself, its height measured for the width it is given (see
        // FitScanCard): auto-sized, it kept the height of the flows wrapped at their narrowest.
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Margin = new Padding(0, 0, 0, 14),
            Padding = new Padding(16, 14, 16, 14),
            BackColor = UiTheme.SurfaceRaised
        };
        UiChrome.AsCard(stack);
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 6; i++) stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // --- scan row -------------------------------------------------------------------------------

        var scanRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 12),
            BackColor = UiTheme.SurfaceRaised
        };

        _scanLabel.Font = UiTheme.BodyBold;
        _scanLabel.ForeColor = UiTheme.TextPrimary;
        _scanLabel.AutoSize = true;
        _scanLabel.Margin = new Padding(0, 10, 12, 0);

        _scan.Tag = UiTags.LatinValue;
        _scan.Font = UiTheme.Mono(12f, FontStyle.Bold);
        _scan.Height = 34;
        _scan.Width = 320;
        _scan.Margin = new Padding(0, 0, 12, 0);

        _scanHint.Font = UiTheme.Body;
        _scanHint.ForeColor = UiTheme.TextSecondary;
        _scanHint.AutoSize = true;
        _scanHint.Margin = new Padding(0, 10, 0, 0);

        scanRow.Controls.AddRange([_scanLabel, _scan, _scanHint]);

        // --- the tube's band ------------------------------------------------------------------------

        _detail.Dock = DockStyle.Fill;
        _detail.Height = 96;
        _detail.Margin = new Padding(0, 0, 0, 12);
        _detail.Padding = new Padding(14, 10, 14, 10);
        _detail.BackColor = UiTheme.AccentSubtle;
        _detail.Visible = false;
        UiChrome.AsCard(_detail, UiTheme.AccentSubtle);

        var band = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
            BackColor = UiTheme.AccentSubtle
        };
        band.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        band.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        band.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        band.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _detailCap.Anchor = AnchorStyles.Left;
        _detailCap.Margin = new Padding(0, 0, 16, 0);

        var lines = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = new Padding(0),
            BackColor = UiTheme.AccentSubtle
        };
        lines.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) lines.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Line 1: the patient, the file number and the priority.
        var first = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            BackColor = UiTheme.AccentSubtle
        };

        _patientLine.Font = EntryPaint.Resized(UiTheme.SectionTitle, 12f);
        _patientLine.ForeColor = UiTheme.AccentInk;
        _patientLine.AutoSize = true;
        _patientLine.Margin = new Padding(0, 0, 8, 0);

        _accessionLine.Font = UiTheme.Monospace;
        _accessionLine.ForeColor = UiTheme.TextPrimary;
        _accessionLine.AutoSize = true;
        _accessionLine.Margin = new Padding(0, 3, 8, 0);

        _priorityPill.Margin = new Padding(0, 2, 8, 0);

        _identityLine.Font = UiTheme.Body;
        _identityLine.ForeColor = UiTheme.TextSecondary;
        _identityLine.AutoSize = true;
        _identityLine.Margin = new Padding(0, 3, 0, 0);

        first.Controls.AddRange([_patientLine, _accessionLine, _priorityPill, _identityLine]);

        foreach (var label in new[] { _orderLine, _tubeLine })
        {
            label.Font = UiTheme.Body;
            label.ForeColor = UiTheme.TextSecondary;
            label.AutoSize = false;
            label.AutoEllipsis = true;
            label.Dock = DockStyle.Fill;
            label.Height = 19;
            label.Margin = new Padding(0, 2, 0, 0);
        }

        // The computed verdict, in its own emphasised line.
        _delayLine.Font = UiTheme.BodyBold;
        _delayLine.AutoSize = false;
        _delayLine.AutoEllipsis = true;
        _delayLine.Dock = DockStyle.Fill;
        _delayLine.Height = 19;
        _delayLine.Margin = new Padding(0, 2, 0, 0);
        _delayLine.Visible = false;

        lines.Controls.Add(first, 0, 0);
        lines.Controls.Add(_tubeLine, 0, 1);
        lines.Controls.Add(_orderLine, 0, 2);
        lines.Controls.Add(_delayLine, 0, 3);

        var delay = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(16, 0, 0, 0),
            BackColor = UiTheme.AccentSubtle
        };
        delay.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        delay.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _delayCaption.Font = UiTheme.Small;
        _delayCaption.ForeColor = UiTheme.TextSecondary;
        _delayCaption.AutoSize = true;
        _delayCaption.Anchor = AnchorStyles.Right;
        _delayCaption.Margin = new Padding(0);

        _delayValue.Font = UiTheme.Mono(13.5f, FontStyle.Bold);
        _delayValue.AutoSize = true;
        _delayValue.Anchor = AnchorStyles.Right;
        _delayValue.Margin = new Padding(0, 2, 0, 0);

        delay.Controls.Add(_delayCaption, 0, 0);
        delay.Controls.Add(_delayValue, 0, 1);

        band.Controls.Add(_detailCap, 0, 0);
        band.Controls.Add(lines, 1, 0);
        band.Controls.Add(delay, 2, 0);
        _detail.Controls.Add(band);

        // --- non-conformities -----------------------------------------------------------------------

        _defectLegend.Font = UiTheme.SmallBold;
        _defectLegend.ForeColor = UiTheme.TextSecondary;
        _defectLegend.AutoSize = true;
        _defectLegend.Margin = new Padding(0, 0, 0, 8);

        // Sized by FitScanCard: auto-sized in the table, it was measured unwrapped and its last
        // line ran over the conditions below.
        _defectBox.Dock = DockStyle.Fill;
        _defectBox.AutoSize = false;
        _defectBox.Height = 72;
        _defectBox.WrapContents = true;
        _defectBox.Margin = new Padding(0, 0, 0, 6);
        _defectBox.BackColor = UiTheme.SurfaceRaised;

        BuildDefectChecks();

        // --- conditions and decision ----------------------------------------------------------------

        var decision = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0, 12, 0, 0),
            BackColor = UiTheme.SurfaceRaised
        };
        decision.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        decision.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        decision.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        decision.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // The line of the design system above the conditions.
        decision.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(0xE6, 0xEB, 0xEF));
            e.Graphics.DrawLine(pen, 0, 0, decision.ClientSize.Width, 0);
        };

        var conditions = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };

        _temperature.Tag = UiTags.LatinValue;
        _temperature.Font = UiTheme.Monospace;
        _temperature.PlaceholderText = "—";
        _temperatureField = CollectionStackedField.Of(_temperatureLabel, _temperature, 120, new Padding(0, 0, 16, 0));

        _coldChain.Font = UiTheme.Body;
        _coldChain.ForeColor = UiTheme.TextPrimary;
        _coldChain.AutoSize = true;
        _coldChain.Margin = new Padding(0, 26, 16, 0);
        _coldChain.Checked = true;

        // A closed list: the site's active places, created in « Paramètres › Lieux de stockage ».
        // The empty first entry leaves the tube unshelved, as an empty field did.
        StoragePlacePicker.Configure(_storage);
        var storageField = CollectionStackedField.Of(_storageLabel, _storage, 240, new Padding(0, 0, 16, 0));

        conditions.Controls.AddRange([_temperatureField, _coldChain, storageField]);

        var acceptButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            Margin = new Padding(12, 0, 0, 0),
            BackColor = UiTheme.SurfaceRaised
        };

        _acceptReserved.AutoSize = true;
        _acceptReserved.MinimumSize = new Size(0, 34);
        _acceptReserved.Margin = new Padding(0, 6, 8, 0);

        _accept.AutoSize = true;
        _accept.MinimumSize = new Size(0, 40);
        _accept.Margin = new Padding(0, 3, 0, 0);

        acceptButtons.Controls.AddRange([_acceptReserved, _accept]);

        // The refusal, with its reason from the closed list and the recollection, on a line of its own.
        var refusal = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 10, 0, 0),
            BackColor = UiTheme.SurfaceRaised
        };

        _rejectReasonLabel.Font = UiTheme.SmallBold;
        _rejectReasonLabel.ForeColor = UiTheme.TextSecondary;
        _rejectReasonLabel.AutoSize = true;
        _rejectReasonLabel.Margin = new Padding(0, 10, 8, 0);

        _rejectReason.DropDownStyle = ComboBoxStyle.DropDownList;
        _rejectReason.FlatStyle = FlatStyle.Flat;
        _rejectReason.Font = UiTheme.Body;
        _rejectReason.Width = 260;
        _rejectReason.Margin = new Padding(0, 5, 12, 0);

        _requestRecollection.Font = UiTheme.Body;
        _requestRecollection.ForeColor = UiTheme.TextSecondary;
        _requestRecollection.AutoSize = true;
        _requestRecollection.Margin = new Padding(0, 8, 12, 0);
        _requestRecollection.Checked = true;

        _reject.AutoSize = true;
        _reject.MinimumSize = new Size(0, 34);
        _reject.Margin = new Padding(0, 0, 0, 0);
        _reject.ForeColor = UiTheme.CriticalInk;

        refusal.Controls.AddRange([_rejectReasonLabel, _rejectReason, _requestRecollection, _reject]);

        decision.Controls.Add(conditions, 0, 0);
        decision.Controls.Add(acceptButtons, 1, 0);
        decision.Controls.Add(refusal, 0, 1);
        decision.SetColumnSpan(refusal, 2);

        stack.Controls.Add(scanRow, 0, 0);
        stack.Controls.Add(_detail, 0, 1);
        stack.Controls.Add(_defectLegend, 0, 2);
        stack.Controls.Add(_defectBox, 0, 3);
        stack.Controls.Add(decision, 0, 4);

        _scanCard = stack;
        _scanRow = scanRow;
        _decision = decision;
        _conditions = conditions;
        _acceptButtons = acceptButtons;
        _refusal = refusal;
        stack.SizeChanged += (_, _) => FitScanCard();
        _detail.VisibleChanged += (_, _) => FitScanCard();
        _detail.SizeChanged += (_, _) => FitScanCard();
        _defectBox.ControlAdded += (_, _) => FitScanCard();

        return stack;
    }

    private TableLayoutPanel? _scanCard;
    private Control? _scanRow;
    private Control? _decision;
    private Control? _conditions;
    private Control? _acceptButtons;
    private Control? _refusal;
    private bool _fitting;

    /// <summary>
    /// Sets the scanned-tube card to the height its rows need at its present width, so the list
    /// below gets the rest of the screen.
    /// </summary>
    private void FitScanCard()
    {
        if (_fitting || _scanCard is null || _scanRow is null || _decision is null || _conditions is null
            || _acceptButtons is null || _refusal is null) return;

        var width = _scanCard.ClientSize.Width - _scanCard.Padding.Horizontal;
        if (width <= 0) return;

        int Height(Control control, int at) =>
            control.GetPreferredSize(new Size(Math.Max(1, at - control.Margin.Horizontal), 0)).Height
            + control.Margin.Vertical;

        var buttons = _acceptButtons.GetPreferredSize(Size.Empty);
        var decision = _decision.Padding.Vertical
                       + Math.Max(Height(_conditions, width - buttons.Width - _acceptButtons.Margin.Horizontal),
                                  buttons.Height + _acceptButtons.Margin.Vertical)
                       + Height(_refusal, width);

        var defects = _defectBox.GetPreferredSize(new Size(width, 0)).Height;
        if (_defectBox.Height != defects) _defectBox.Height = defects;

        var needed = _scanCard.Padding.Vertical
                     + Height(_scanRow, width)
                     + (_detail.Visible ? _detail.Height + _detail.Margin.Vertical : 0)
                     + Height(_defectLegend, width)
                     + defects + _defectBox.Margin.Vertical
                     + decision
                     + 4;

        // The row is checked as well as the card: measured while the screen was still narrow, the
        // row kept the height of the wrapped flows after the card had shrunk back, and pushed the
        // list of tubes awaiting reception off the bottom of the screen.
        var content = _scanCard.Parent as TableLayoutPanel;
        var row = needed + _scanCard.Margin.Vertical;
        if (_scanCard.Height == needed
            && (content is null || content.RowStyles[0] is { SizeType: SizeType.Absolute } style && (int)style.Height == row))
            return;

        _fitting = true;
        try
        {
            _scanCard.Height = needed;
            if (content is not null)
            {
                content.RowStyles[0] = new RowStyle(SizeType.Absolute, row);
                // Changed from inside a layout pass, the new row height is not laid out until
                // something else moves: the list kept the place the old row gave it.
                if (content.IsHandleCreated) content.BeginInvoke(() => content.PerformLayout());
                else content.PerformLayout();
            }
        }
        finally
        {
            _fitting = false;
        }
    }

    /// <summary>« En attente de réception », the count, the chips and the tubes.</summary>
    private Control BuildAwaitingCard()
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(1),
            BackColor = UiTheme.SurfaceRaised
        };
        UiChrome.AsCard(card);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(14, 10, 14, 8),
            BackColor = UiTheme.SurfaceRaised
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _awaitingHeading.Font = UiTheme.SectionTitle;
        _awaitingHeading.ForeColor = UiTheme.TextPrimary;
        _awaitingHeading.AutoSize = true;
        _awaitingHeading.Anchor = AnchorStyles.Left;
        _awaitingHeading.Margin = new Padding(0, 0, 10, 0);

        _awaitingCount.Font = UiTheme.Body;
        _awaitingCount.ForeColor = UiTheme.TextSecondary;
        _awaitingCount.AutoSize = true;
        _awaitingCount.Anchor = AnchorStyles.Left;
        _awaitingCount.Margin = new Padding(0, 2, 0, 0);

        var chips = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(12, 0, 0, 0),
            BackColor = UiTheme.SurfaceRaised
        };
        foreach (var chip in new[] { _chipAll, _chipUrgent, _chipLongDelay })
        {
            chip.Height = 28;
            chip.Margin = new Padding(0, 0, 6, 0);
        }
        _chipAll.Selected = true;
        chips.Controls.AddRange([_chipAll, _chipUrgent, _chipLongDelay]);

        header.Controls.Add(_awaitingHeading, 0, 0);
        header.Controls.Add(_awaitingCount, 1, 0);
        header.Controls.Add(chips, 2, 0);

        BuildAwaitingColumns();
        _awaiting.Dock = DockStyle.Fill;
        _awaiting.Margin = new Padding(0);
        _awaiting.RowTemplate.Height = 34;
        _awaiting.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _awaiting.MinimumSize = new Size(0, 110);

        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(_awaiting, 0, 1);

        card.Controls.Add(layout);
        return card;
    }

    /// <summary>
    /// One toggle per observable defect.
    /// <para>
    /// Every member of the flags enum is offered, because the rejection and reservation statistics
    /// are only meaningful if the operator can record what they actually saw rather than picking the
    /// nearest of three options.
    /// </para>
    /// </summary>
    private void BuildDefectChecks()
    {
        _defectChecks.Clear();
        _defectBox.Controls.Clear();

        var defects = Enum.GetValues<SampleDefect>()
                          .Where(d => d != SampleDefect.None)
                          .ToList();

        foreach (var defect in defects)
        {
            var check = new CollectionDefectToggle();

            check.CheckedChanged += (_, _) => RefreshActionAvailability();

            _defectChecks[defect] = check;
            _defectBox.Controls.Add(check);
        }
    }

    private void BuildAwaitingColumns()
    {
        _awaiting.Columns.Clear();

        void Add(string name, int weight, Font? font = null,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft, int minimum = 0)
        {
            var column = new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body, Alignment = align
                }
            };
            if (minimum > 0) column.MinimumWidth = minimum;
            _awaiting.Columns.Add(column);
        }

        Add(ColBarcode, 12, UiTheme.Monospace, minimum: 96);
        Add(ColAccession, 12, UiTheme.Monospace, minimum: 92);
        Add(ColPatient, 20, minimum: 130);
        Add(ColSpecimen, 15, UiTheme.Body, minimum: 110);
        Add(ColPriority, 9, UiTheme.SmallBold, minimum: 74);
        Add(ColCollected, 10, UiTheme.Monospace, minimum: 86);
        Add(ColElapsed, 9, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight, minimum: 70);
        Add(ColVerdict, 13, UiTheme.SmallBold, minimum: 110);
    }

    private void WireBehaviour()
    {
        // A scanner terminates its scan with Enter. Enter on an empty box accepts the tube shown,
        // when it is conforming — the « Entrée » the accept button shows.
        _scan.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;

            if (string.IsNullOrWhiteSpace(_scan.Text))
            {
                if (_candidate is not null && _accept.Enabled) _accept.PerformClick();
                return;
            }

            await ScanAsync(_scan.Text);
        };

        // Writes run through RunBusyAsync, as on the billing screens: a double-click records one
        // action, and leaving the screen mid-write no longer disposes its context under it.
        _accept.Click += async (_, _) => await RunBusyAsync(() => AcceptAsync(withReservation: false));
        _acceptReserved.Click += async (_, _) => await RunBusyAsync(() => AcceptAsync(withReservation: true));
        _reject.Click += async (_, _) => await RunBusyAsync(RejectAsync);

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Samples.Receive, _accept, _acceptReserved);
        PermissionGate.Require(Permissions.Samples.Reject, _reject);

        _awaiting.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex < 0) return;
            var barcode = _awaiting.Rows[e.RowIndex].Cells[ColBarcode].Value?.ToString();
            if (!string.IsNullOrWhiteSpace(barcode)) await ScanAsync(barcode);
        };

        _awaiting.CellFormatting += OnAwaitingFormatting;
        _awaiting.CellPainting += OnAwaitingPainting;

        _chipAll.Click += (_, _) => SetFilter(AwaitingFilter.All);
        _chipUrgent.Click += (_, _) => SetFilter(AwaitingFilter.Urgent);
        _chipLongDelay.Click += (_, _) => SetFilter(AwaitingFilter.LongDelay);

        CollectionKeyHint.Attach(_accept, () => T("Sample.EnterKey"));
    }

    private void OnAwaitingFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _awaiting.Rows.Count) return;
        if (_awaiting.Rows[e.RowIndex].Tag is not SampleReceptionCandidate candidate) return;

        // The tube on the bench is the highlighted line.
        if (_candidate is { } shown && shown.SampleId == candidate.SampleId)
        {
            e.CellStyle!.BackColor = UiTheme.AccentSubtle;
            e.CellStyle.SelectionBackColor = UiTheme.AccentSubtle;
        }

        var column = _awaiting.Columns[e.ColumnIndex].Name;

        if (column == ColBarcode && _candidate?.SampleId == candidate.SampleId)
            e.CellStyle!.Font = UiTheme.MonospaceBold;
    }

    /// <summary>
    /// The specimen with its cap-colour dot, the priority, the transport time and the status as
    /// pills: an exceeded stability window in critical colours, recorded defects in warning colours.
    /// </summary>
    private void OnAwaitingPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.Graphics is null) return;
        if (_awaiting.Rows[e.RowIndex].Tag is not SampleReceptionCandidate candidate) return;

        var column = _awaiting.Columns[e.ColumnIndex].Name;
        var rtl = IsRightToLeft;
        var bounds = e.CellBounds;
        var selected = (e.State & DataGridViewElementStates.Selected) != 0;

        void Pill(string text, (Color Back, Color Fore) colours, bool atEnd = false)
        {
            e.PaintBackground(e.ClipBounds, selected);
            if (text.Length == 0) return;
            var size = EntryPaint.MeasurePill(text, UiTheme.SmallBold, 8);
            var x = atEnd ? bounds.Right - 10 - size.Width : bounds.X + 10;
            var rectangle = new Rectangle(x, bounds.Y + (bounds.Height - size.Height) / 2,
                                          Math.Min(size.Width, bounds.Width - 16), size.Height);
            EntryPaint.DrawPill(e.Graphics, EntryPaint.Mirror(rectangle, bounds, rtl), text, UiTheme.SmallBold, colours, rtl);
            e.Handled = true;
        }

        switch (column)
        {
            case ColSpecimen:
            {
                e.PaintBackground(e.ClipBounds, selected);
                var dot = EntryPaint.Mirror(new Rectangle(bounds.X + 10, bounds.Y, 10, bounds.Height), bounds, rtl);
                TubeSwatch.Dot(e.Graphics, dot, candidate.ContainerColourHex);
                var text = EntryPaint.Mirror(new Rectangle(bounds.X + 26, bounds.Y, bounds.Width - 32, bounds.Height),
                                             bounds, rtl);
                TextRenderer.DrawText(e.Graphics, e.FormattedValue?.ToString() ?? string.Empty, UiTheme.Body, text,
                                      UiTheme.TextPrimary,
                                      TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                                      | TextFormatFlags.EndEllipsis
                                      | (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left));
                e.Handled = true;
                return;
            }

            case ColPriority:
                if (candidate.Priority == OrderPriority.Routine) return;
                Pill(T($"Priority.{candidate.Priority}"),
                     candidate.Priority == OrderPriority.Routine ? EntryPaint.Waiting
                     : (UiTheme.AbnormalBackground, UiTheme.ForPriority(candidate.Priority)));
                return;

            case ColElapsed when candidate.StabilityExceeded:
                Pill(FormatElapsed(candidate.Elapsed), EntryPaint.Critical, atEnd: true);
                return;

            case ColVerdict:
            {
                var (text, colours) = Verdict(candidate);
                Pill(text, colours);
                return;
            }
        }
    }

    /// <summary>The status pill of a waiting tube.</summary>
    private (string Text, (Color Back, Color Fore) Colours) Verdict(SampleReceptionCandidate candidate)
    {
        if (_candidate is { } shown && shown.SampleId == candidate.SampleId)
            return (T("Sample.Scanned"), (UiTheme.Accent, UiTheme.TextOnAccent));
        if (candidate.StabilityExceeded)
            return (T("SampleDefect.ExcessiveDelay"), EntryPaint.Critical);
        if (candidate.ExistingDefects != SampleDefect.None)
            return (T("Sample.Defects"), EntryPaint.Warning);
        return (T("SampleStatus." + candidate.Status),
                candidate.Status == SampleStatus.InTransit ? EntryPaint.Info : EntryPaint.Waiting);
    }

    protected override void ApplyTexts()
    {
        _scanLabel.Text = T("Sample.ScanTube");
        _scan.PlaceholderText = T("Sample.ScanPrompt");
        _scanHint.Text = T("Sample.ScannerReady");
        _defectLegend.Text = T("Sample.DefectsLegend");
        _temperatureLabel.Text = T("Sample.Temperature");
        _coldChain.Text = T("Sample.ColdChain");
        _storageLabel.Text = T("Sample.Storage");
        FillStoragePlaces();
        _accept.Text = T("Action.Accept");
        CollectionKeyHint.Reserve(_accept, T("Sample.EnterKey"));
        _acceptReserved.Text = T("Action.AcceptWithReservation");
        _reject.Text = T("Action.Reject");
        _rejectReasonLabel.Text = T("Recollection.Reason");
        _requestRecollection.Text = T("Sample.RequestRecollection");
        _awaitingHeading.Text = T("Sample.AwaitingReception");
        _delayCaption.Text = T("Sample.TransportDelay");

        foreach (var (defect, check) in _defectChecks)
            check.Text = T($"SampleDefect.{defect}");

        var selectedReason = _rejectReason.SelectedItem as ReasonChoice;

        _rejectReason.BeginUpdate();
        _rejectReason.Items.Clear();

        foreach (var reason in Enum.GetValues<SampleRejectionReason>())
            _rejectReason.Items.Add(new ReasonChoice(reason, T($"RejectionReason.{reason}")));

        // No reason preselected: the reason is counted monthly as a quality indicator, and a
        // refusal confirmed too quickly was filed under the first one (identity mismatch).
        _rejectReason.SelectedIndex = -1;

        if (selectedReason is not null)
        {
            for (var i = 0; i < _rejectReason.Items.Count; i++)
                if (_rejectReason.Items[i] is ReasonChoice choice
                    && choice.Value == selectedReason.Value)
                    _rejectReason.SelectedIndex = i;
        }

        _rejectReason.EndUpdate();

        if (_awaiting.Columns.Count > 0)
        {
            _awaiting.Column(ColBarcode).HeaderText = T("Sample.Barcode");
            _awaiting.Column(ColAccession).HeaderText = T("Order.AccessionNumber");
            _awaiting.Column(ColPatient).HeaderText = T("Patient.Title");
            _awaiting.Column(ColSpecimen).HeaderText = T("Sample.SpecimenType");
            _awaiting.Column(ColPriority).HeaderText = T("Field.Priority");
            _awaiting.Column(ColCollected).HeaderText = T("Sample.CollectedAt");
            _awaiting.Column(ColElapsed).HeaderText = T("Sample.TransportDelay");
            _awaiting.Column(ColVerdict).HeaderText = T("Field.Status");
        }

        RenderAwaiting();

        if (_candidate is not null) ShowCandidate(_candidate);
        FitScanCard();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        await RefreshAwaitingAsync(ct);
        await LoadStorageLocationsAsync(ct);
        _scan.Focus();
    }

    /// <summary>The site's active storage places, the only ones the list offers.</summary>
    private async Task LoadStorageLocationsAsync(CancellationToken ct = default)
    {
        try
        {
            var places = await _workflow.GetStorageLocationsAsync(ct);
            if (places.IsFailure) return;

            _storagePlaces = places.Value;
            FillStoragePlaces();
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
        catch (Exception ex)
        {
            // Without the list the tube is received unshelved; it is shelved later in the serum bank.
            _logger?.LogError(ex, "Loading the storage places failed.");
        }
    }

    /// <summary>Refills the list, keeping the place chosen for the current tube.</summary>
    private void FillStoragePlaces() =>
        StoragePlacePicker.Fill(_storage, _storagePlaces, StoragePlacePicker.Selected(_storage),
                                T("Sample.StorageNotSpecified"), place => T("Sample.StorageOutOfList", place));

    private async Task RefreshAwaitingAsync(CancellationToken ct = default)
    {
        try
        {
            var awaiting = await _workflow.GetAwaitingReceptionAsync(ct);

            if (awaiting.IsFailure) return;

            _awaitingRows = awaiting.Value;
            RenderAwaiting();
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the reception list failed.");
        }
    }

    private void SetFilter(AwaitingFilter filter)
    {
        _filter = filter;
        _chipAll.Selected = filter == AwaitingFilter.All;
        _chipUrgent.Selected = filter == AwaitingFilter.Urgent;
        _chipLongDelay.Selected = filter == AwaitingFilter.LongDelay;
        RenderAwaiting();
    }

    private static bool Matches(SampleReceptionCandidate candidate, AwaitingFilter filter) => filter switch
    {
        AwaitingFilter.Urgent => candidate.Priority != OrderPriority.Routine,
        AwaitingFilter.LongDelay => candidate.StabilityExceeded,
        _ => true
    };

    private void RenderAwaiting()
    {
        _chipAll.Text = T("Collection.FilterAll", _awaitingRows.Count);
        _chipUrgent.Text = T("Collection.FilterUrgent", _awaitingRows.Count(c => Matches(c, AwaitingFilter.Urgent)));
        _chipLongDelay.Text = T("Sample.FilterLongDelay", _awaitingRows.Count(c => Matches(c, AwaitingFilter.LongDelay)));
        _awaitingCount.Text = _awaitingRows.Count == 1
            ? T("Collection.OneTube")
            : T("Collection.ManyTubes", _awaitingRows.Count);

        _awaiting.SuspendLayout();
        _awaiting.Rows.Clear();

        foreach (var candidate in _awaitingRows.Where(c => Matches(c, _filter)))
        {
            var index = _awaiting.Rows.Add(
                candidate.Barcode,
                candidate.AccessionNumber,
                candidate.PatientName,
                candidate.SpecimenTypeName.For(Localizer.CurrentLanguage),
                candidate.Priority == OrderPriority.Routine
                    ? T("Priority.Routine")
                    : T($"Priority.{candidate.Priority}"),
                candidate.CollectedAt?.ToLocalTime().ToString("dd/MM HH:mm") ?? string.Empty,
                FormatElapsed(candidate.Elapsed),
                Verdict(candidate).Text);

            var row = _awaiting.Rows[index];
            row.Tag = candidate;

            if (candidate.Priority == OrderPriority.Routine)
                row.Cells[ColPriority].Style.ForeColor = UiTheme.TextSecondary;
        }

        _awaiting.ResumeLayout(performLayout: true);
        _awaiting.ClearSelection();
    }

    private static string FormatElapsed(TimeSpan? elapsed) =>
        elapsed is not { } value
            ? string.Empty
            : value.TotalHours >= 1
                ? $"{(int)value.TotalHours}h{value.Minutes:D2}"
                : $"{value.Minutes} min";

    // ---- Scan ----------------------------------------------------------------------------------------

    private async Task ScanAsync(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return;

        if (_deciding)
        {
            _queuedScan = barcode.Trim();
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Sample.ScanQueued", _queuedScan);
            _scan.SelectAll();
            return;
        }

        var result = await _workflow.ScanAsync(barcode);

        if (result.IsFailure)
        {
            ClearCandidate();

            _status.ForeColor = UiTheme.Critical;
            _status.Text = Describe(result);

            _scan.SelectAll();
            _scan.Focus();
            return;
        }

        _candidate = result.Value;

        // Every tube starts from blank inputs: the ticks, temperature and storage place entered for
        // the previous one say nothing about this one.
        ResetTubeInputs();
        ShowCandidate(_candidate);

        _scan.Clear();
        _scan.Focus();
    }

    private void ShowCandidate(SampleReceptionCandidate candidate)
    {
        _detail.Visible = true;
        _detailCap.ColourHex = candidate.ContainerColourHex ?? "#FFFFFF";

        // Line 1: the patient, the file number, the priority; then sex and date of birth.
        var identity = new List<string> { T($"Gender.{candidate.Gender}") };
        if (candidate.DateOfBirth is { } dob) identity.Add(dob.ToString("dd/MM/yyyy"));

        _patientLine.Text = candidate.PatientName;
        _accessionLine.Text = candidate.AccessionNumber;
        _identityLine.Text = string.Join(" · ", identity);

        if (candidate.Priority != OrderPriority.Routine)
            _priorityPill.Show(T($"Priority.{candidate.Priority}"),
                               (UiTheme.AbnormalBackground, UiTheme.ForPriority(candidate.Priority)));
        else
            _priorityPill.Show(string.Empty, EntryPaint.Waiting);

        // Line 2: the tube — specimen, container, barcode, volume, when it was drawn, and how many
        // tubes the order has.
        var tubeInfo = new List<string> { candidate.SpecimenTypeName.For(Localizer.CurrentLanguage) };

        if (candidate.ContainerName is { } container)
            tubeInfo.Add(container.For(Localizer.CurrentLanguage));

        tubeInfo.Add(candidate.Barcode);

        if (candidate.VolumeMl is { } volume) tubeInfo.Add($"{volume:N1} mL");

        if (candidate.CollectedAt is { } collected)
        {
            var local = collected.ToLocalTime();
            tubeInfo.Add(T("Sample.CollectedOnAt", local.ToString("dd/MM"), local.ToString("HH:mm")));
        }

        if (candidate.TotalSamplesInOrder > 0)
            tubeInfo.Add(candidate.TotalSamplesInOrder == 1
                ? T("Sample.TubesInOrderOne")
                : T("Sample.TubesInOrder", candidate.TotalSamplesInOrder));

        _tubeLine.Text = string.Join(" · ", tubeInfo);
        _tips.SetToolTip(_tubeLine, _tubeLine.Text);

        // Line 3: the analyses.
        _orderLine.Text = string.Join(" ", candidate.AnalysisCodes);
        _tips.SetToolTip(_orderLine, _orderLine.Text);

        // The verdict, computed rather than left to the operator to infer.
        if (candidate.StabilityExceeded)
        {
            _delayValue.Text = FormatElapsed(candidate.Elapsed);
            _delayValue.ForeColor = UiTheme.Critical;
            _delayLine.Text = $"⚠ {T("SampleDefect.ExcessiveDelay")} — " +
                              $"{FormatElapsed(candidate.Elapsed)} > {candidate.ShortestStabilityHours} h";
            _delayLine.ForeColor = UiTheme.Critical;
            _delayLine.Visible = true;

            // Pre-tick the defect: the operator still decides, but is not left to notice it.
            if (_defectChecks.TryGetValue(SampleDefect.ExcessiveDelay, out var check))
                check.Checked = true;
        }
        else if (candidate.Elapsed is { } elapsed)
        {
            _delayValue.Text = FormatElapsed(elapsed) +
                               (candidate.ShortestStabilityHours is { } hours ? $" / {hours} h" : string.Empty);
            _delayValue.ForeColor = UiTheme.Normal;
            _delayLine.Visible = false;
        }
        else
        {
            _delayValue.Text = "—";
            _delayValue.ForeColor = UiTheme.Abnormal;
            _delayLine.Text = T("Sample_NotCollected");
            _delayLine.ForeColor = UiTheme.Abnormal;
            _delayLine.Visible = true;
        }

        _detail.Height = _delayLine.Visible ? 112 : 92;

        // Volume already recorded as insufficient at the draw.
        if (candidate.VolumeMl is { } drawn && candidate.MinimumVolumeMl is { } minimum
            && drawn < minimum
            && _defectChecks.TryGetValue(SampleDefect.InsufficientVolume, out var volumeCheck))
            volumeCheck.Checked = true;

        // Defects already recorded upstream are shown as ticked, so reception cannot unknowingly
        // accept a tube the phlebotomist already flagged.
        foreach (var (defect, check) in _defectChecks)
            if (candidate.ExistingDefects.HasFlag(defect)) check.Checked = true;

        _coldChain.Visible = candidate.RequiresColdChain;
        if (_temperatureField is not null) _temperatureField.Visible = candidate.RequiresColdChain;

        SetActionsEnabled(true);
        RefreshActionAvailability();

        // Scanned again after reception or after a refusal: say where the tube stands.
        if (candidate.Status is not (SampleStatus.Collected or SampleStatus.InTransit
                                     or SampleStatus.AwaitingCollection))
        {
            _status.ForeColor = UiTheme.Abnormal;
            _status.Text = $"{candidate.Barcode} — {T("SampleStatus." + candidate.Status)}";
        }

        _awaiting.Invalidate();
        SelectAwaitingRow(candidate.SampleId);
    }

    /// <summary>Brings the scanned tube's line into view in the waiting list, without selecting it.</summary>
    private void SelectAwaitingRow(Guid sampleId)
    {
        foreach (DataGridViewRow row in _awaiting.Rows)
        {
            if (row.Tag is not SampleReceptionCandidate candidate || candidate.SampleId != sampleId) continue;
            row.Cells[ColVerdict].Value = Verdict(candidate).Text;
            if (!row.Displayed && row.Index >= 0)
            {
                try { _awaiting.FirstDisplayedScrollingRowIndex = row.Index; }
                catch (InvalidOperationException) { /* not laid out yet */ }
            }
        }
    }

    private void ClearCandidate()
    {
        _candidate = null;
        _detail.Visible = false;

        ResetTubeInputs();

        SetActionsEnabled(false);
        _awaiting.Invalidate();
    }

    /// <summary>
    /// Puts the per-tube inputs back to their defaults. Not done in <see cref="ShowCandidate"/>,
    /// which also re-renders the current tube after a language change and must keep what the
    /// operator has entered for it.
    /// </summary>
    private void ResetTubeInputs()
    {
        foreach (var check in _defectChecks.Values) check.Checked = false;

        _temperature.Clear();
        _coldChain.Checked = true;
        if (_storage.Items.Count > 0) _storage.SelectedIndex = 0;
        _requestRecollection.Checked = true;

        _rejectReason.SelectedIndex = -1;
    }

    /// <summary>
    /// Ends a decision: re-enables the actions if the tube is still displayed (the decision was
    /// refused), then shows a tube scanned in the meantime.
    /// </summary>
    private async Task EndDecisionAsync(SampleReceptionCandidate decided)
    {
        _deciding = false;

        if (ReferenceEquals(_candidate, decided))
        {
            SetActionsEnabled(true);
            RefreshActionAvailability();
        }

        if (_queuedScan is { } queued)
        {
            _queuedScan = null;
            await ScanAsync(queued);
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        _accept.Enabled = enabled;
        _acceptReserved.Enabled = enabled;
        _reject.Enabled = enabled;
        _rejectReason.Enabled = enabled;
    }

    /// <summary>
    /// Keeps the two acceptance buttons honest: a tube with a recorded defect cannot be accepted by
    /// the plain button, and a conforming tube does not offer the reservation path.
    /// </summary>
    private void RefreshActionAvailability()
    {
        if (_candidate is null) return;

        var hasDefects = SelectedDefects() != SampleDefect.None;

        _accept.Enabled = !hasDefects;
        _acceptReserved.Enabled = hasDefects;

        _acceptReserved.ForeColor = hasDefects ? UiTheme.Abnormal : UiTheme.TextSecondary;
        _acceptReserved.Font = hasDefects ? UiTheme.BodyBold : UiTheme.Body;

        // A tube already on the bench can still be refused (a clot found after centrifugation),
        // but not received a second time; a refused or discarded tube admits neither.
        if (_candidate.Status is SampleStatus.Received or SampleStatus.InAnalysis or SampleStatus.Stored)
        {
            _accept.Enabled = false;
            _acceptReserved.Enabled = false;
        }
        else if (_candidate.Status is SampleStatus.Rejected or SampleStatus.Discarded)
        {
            SetActionsEnabled(false);
        }
    }

    private SampleDefect SelectedDefects()
    {
        var defects = SampleDefect.None;

        foreach (var (defect, check) in _defectChecks)
            if (check.Checked) defects |= defect;

        return defects;
    }

    // ---- Decisions -------------------------------------------------------------------------------------

    private async Task AcceptAsync(bool withReservation)
    {
        // The tube being decided, held locally: a scan may replace _candidate before this returns.
        if (_deciding || _candidate is not { } candidate) return;

        var defects = SelectedDefects();

        if (defects != SampleDefect.None && !withReservation)
        {
            // Should be unreachable given RefreshActionAvailability, but the domain refuses it too
            // and the operator deserves the explanation rather than a silent no-op.
            _dialogs.Warn(T("Sample_DefectsRequireExplicitDecision", defects));
            return;
        }

        decimal? temperature = decimal.TryParse(
            _temperature.Text.Replace(',', '.'),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;

        var request = new ReceiveSampleRequest
        {
            SampleId = candidate.SampleId,
            ObservedDefects = defects,
            AcceptWithReservation = withReservation,
            TemperatureCelsius = temperature,
            ColdChainRespected = candidate.RequiresColdChain ? _coldChain.Checked : null,
            StorageLocation = StoragePlacePicker.Selected(_storage)
        };

        _deciding = true;
        SetActionsEnabled(false);
        Cursor = Cursors.WaitCursor;

        try
        {
            var result = await _workflow.ReceiveAsync([request]);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            if (result.Value.Problems.Count > 0)
            {
                _status.ForeColor = UiTheme.Abnormal;
                _status.Text = Localizer.Describe(result.Value.Problems);
            }
            else
            {
                _status.ForeColor = withReservation ? UiTheme.Abnormal : UiTheme.Normal;
                _status.Text = withReservation
                    ? $"{candidate.Barcode} — {T("Action.AcceptWithReservation")}"
                    : $"{candidate.Barcode} — {T("SampleStatus.Received")}";
            }

            if (ReferenceEquals(_candidate, candidate)) ClearCandidate();
            await RefreshAwaitingAsync();

        }
        finally
        {
            Cursor = Cursors.Default;
            _scan.Focus();
            await EndDecisionAsync(candidate);
        }
    }

    private async Task RejectAsync()
    {
        // The tube being decided, held locally: a scan may replace _candidate before this returns.
        if (_deciding || _candidate is not { } candidate) return;

        if (_rejectReason.SelectedItem is not ReasonChoice reason)
        {
            _dialogs.Warn(T("Sample_RejectionReasonRequired"), T("Action.Reject"));
            _rejectReason.Focus();
            return;
        }

        var comment = _dialogs.PromptForText(
            $"{candidate.Barcode} — {reason.Display}\n\n{T("Field.Comment")}",
            T("Action.Reject"));

        if (comment is null) return;

        if (!_dialogs.Confirm(
                T("Sample.ConfirmRejection", candidate.Barcode, reason.Display),
                T("Action.Reject")))
            return;

        _deciding = true;
        SetActionsEnabled(false);
        Cursor = Cursors.WaitCursor;

        try
        {
            var result = await _workflow.RejectAsync(
            [
                new RejectSampleRequest(candidate.SampleId, reason.Value, comment,
                                        _requestRecollection.Checked)
            ]);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            // Refused by the rules (already refused, nothing left to redraw): nothing was recorded.
            if (result.Value.Rejected == 0)
            {
                _dialogs.Warn(Localizer.Describe(result.Value.Problems), T("Action.Reject"));
                return;
            }

            // A recollection produces a fresh tube with a new barcode — never a reuse of the
            // rejected one — so its label is printed immediately.
            if (result.Value.ReplacementLabels.Count > 0)
            {
                await PrintReplacementLabelsAsync(result.Value.ReplacementLabels);

                _status.ForeColor = UiTheme.Abnormal;
                _status.Text = $"{candidate.Barcode} — {T("SampleStatus.Rejected")} · " +
                               T("Sample.ReplacementIssued");
            }
            else
            {
                _status.ForeColor = UiTheme.Abnormal;
                _status.Text = $"{candidate.Barcode} — {T("SampleStatus.Rejected")}";
            }

            if (result.Value.Problems.Count > 0)
                _dialogs.Warn(Localizer.Describe(result.Value.Problems));

            if (ReferenceEquals(_candidate, candidate)) ClearCandidate();
            await RefreshAwaitingAsync();
        }
        finally
        {
            Cursor = Cursors.Default;
            _scan.Focus();
            await EndDecisionAsync(candidate);
        }
    }

    private async Task PrintReplacementLabelsAsync(
        IReadOnlyList<Application.Orders.SampleLabel> labels)
    {
        try
        {
            var emitted = await LabelOutput.EmitAsync(_labelPrinter, _documents, _labels.Render, labels);

            if (emitted.IsFailure) _dialogs.ShowFailure(emitted, T("Action.PrintLabels"));
        }
        catch (Exception ex)
        {
            // The rejection and the replacement tube are already recorded; a label failure must
            // not undo them. The label can be reprinted from the collection screen.
            _logger?.LogError(ex, "Printing the replacement label failed.");
            _dialogs.Warn(T("Action.PrintLabels") + " — " + T("Message.UnexpectedError"));
        }
    }

    private sealed record ReasonChoice(SampleRejectionReason Value, string Display)
    {
        public override string ToString() => Display;
    }
}
