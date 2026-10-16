using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Reports;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Reports;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The register of released reports: what was issued, to whom it went, and what has not gone
/// anywhere yet.
/// <para>
/// Three service methods had no caller before this screen, and their absence was not three missing
/// conveniences but one compounding defect. <c>GetStoredPdfAsync</c> unreachable meant a reprint
/// re-rendered the report against today's catalogue instead of returning the document actually
/// issued. <c>RecordDeliveryAsync</c> unreachable meant no report was ever marked delivered — and
/// because the amendment guard tested exactly that flag, re-releasing quietly minted v2 and v3 of
/// the same report with none of them marked as a rectification. The guard has been changed to
/// treat any reissue of a <em>final</em> report as an amendment, so it no longer depends on
/// clerical bookkeeping; recording the hand-over is now possible as well.
/// </para>
/// <para>
/// The list leads on what has been released and not handed over, with a critical result first.
/// ISO 15189 §5.9 obliges the laboratory to make sure a panic value reaches a clinician, so an
/// undelivered report carrying one is the row somebody has to act on today — not a row to be
/// found by scrolling.
/// </para>
/// <para>
/// Laid out as the laboratory's « LIS · A Fluent clair » mock-up: « Comptes rendus » with three
/// tabs — À émettre, Émis, À remettre — the search and the period, the list in a card with the
/// hand-overs of the selected version under it, an « Aperçu » card beside it, and the actions in
/// the bottom bar with « Émettre le compte rendu » last.
/// </para>
/// </summary>
public sealed class ReportRegisterView : LocalizedView
{
    private const int AsideWidth = 330;

    private readonly SelectionLoads _selectionLoads = new();
    private readonly ReportReleaseService _reports;
    private readonly IDocumentOutput _documents;
    private readonly IUserDialogs _dialogs;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<ReportRegisterView>? _logger;

    // ---- heading: title, tabs, search, period ----
    private readonly Label _heading = new();
    private readonly Dictionary<ReportTab, FlatTab> _tabs = [];
    private ReportTab _tab = ReportRegisterRules.DefaultTab;
    private readonly TextBox _search = UiTheme.Input(260);
    private readonly ComboBox _window = new();
    private readonly ToolTip _tips = new();

    // ---- « À émettre » ----
    private readonly Panel _pendingPanel = new();
    private readonly DataGridView _pending = UiTheme.Grid();
    private readonly Label _partialNote = new();

    // ---- « Émis » / « À remettre » ----
    private readonly Panel _registerPanel = new();
    private readonly DataGridView _register = UiTheme.Grid();
    private readonly DataGridView _deliveries = UiTheme.Grid();
    private readonly Label _deliveryHeading = new();

    // ---- « Aperçu » ----
    private readonly Label _previewTitle = new();
    private readonly LinkLabel _openPdf = new();
    private readonly ReportPagePreview _preview = new();
    private readonly TableLayoutPanel _facts = new();
    private readonly Label _signatoryCaption = new(), _signatory = new(),
                           _prescriberCaption = new(), _prescriber = new(),
                           _versionsCaption = new(), _versions = new(),
                           _deliveryCaption = new(), _delivery = new();

    // ---- bottom bar ----
    private readonly Button _more = UiTheme.SecondaryButton(string.Empty);
    private readonly ContextMenuStrip _moreMenu = new();
    private readonly ToolStripMenuItem _amend = new();
    private readonly ToolStripMenuItem _refresh = new();
    private readonly Button _reprint = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _recordDelivery = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _hint = new();
    private readonly Button _issue = UiTheme.PrimaryButton(string.Empty);
    private readonly Label _status = new();
    private readonly Panel _statusStrip = new();

    private IReadOnlyList<ReportRegisterRow> _rows = [];
    private IReadOnlyList<PendingReportRow> _pendingRows = [];

    public ReportRegisterView(ReportReleaseService reports, IDocumentOutput documents, IUserDialogs dialogs,
                              ICurrentUser currentUser,
                              ILocalizer localizer, ICultureManager cultureManager,
                              ILogger<ReportRegisterView>? logger = null)
        : base(localizer, cultureManager)
    {
        _reports = reports;
        _documents = documents;
        _dialogs = dialogs;
        _currentUser = currentUser;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Reports");

    private bool IsRightToLeft => RightToLeft == RightToLeft.Yes;

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(0);

        var host = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };

        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(24, 12, 24, 12),
            Margin = new Padding(0),
            BackColor = UiTheme.Surface
        };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        page.Controls.Add(BuildHeadingRow(), 0, 0);
        page.Controls.Add(BuildCards(), 0, 1);

        host.Controls.Add(page);
        host.Controls.Add(BuildStatusStrip());
        host.Controls.Add(BuildActionBar());

        Controls.Add(host);
    }

    /// <summary>« Comptes rendus », the three tabs, and the search and period at the far end.</summary>
    private TableLayoutPanel BuildHeadingRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 5,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 12),
            BackColor = UiTheme.Surface
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _heading.Font = UiTheme.PageTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.AutoSize = true;
        _heading.Anchor = AnchorStyles.Left;
        _heading.Margin = new Padding(0, 0, 12, 0);

        var tabs = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0),
            BackColor = UiTheme.Surface
        };

        foreach (var tab in new[] { ReportTab.ToIssue, ReportTab.Issued, ReportTab.ToDeliver })
        {
            var control = new FlatTab { Selected = tab == _tab };
            control.Click += async (_, _) => await SelectTabAsync(tab);
            _tabs[tab] = control;
            tabs.Controls.Add(control);
        }

        _search.Tag = UiTags.LatinValue;
        _search.Anchor = AnchorStyles.Right;
        _search.Margin = new Padding(12, 0, 8, 0);

        _window.DropDownStyle = ComboBoxStyle.DropDownList;
        _window.FlatStyle = FlatStyle.Flat;
        _window.Width = 160;
        _window.Anchor = AnchorStyles.Right;
        _window.Margin = new Padding(0);

        row.Controls.Add(_heading, 0, 0);
        row.Controls.Add(tabs, 1, 0);
        row.Controls.Add(_search, 3, 0);
        row.Controls.Add(_window, 4, 0);
        return row;
    }

    /// <summary>The list card and the « Aperçu » card beside it.</summary>
    private TableLayoutPanel BuildCards()
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
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AsideWidth));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        row.Controls.Add(BuildListCard(), 0, 0);
        row.Controls.Add(BuildAsideCard(), 1, 0);
        return row;
    }

    private Panel BuildListCard()
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

        BuildPendingColumns();
        BuildRegisterColumns();
        BuildDeliveryColumns();

        foreach (var grid in new[] { _pending, _register, _deliveries })
        {
            grid.RowTemplate.Height = 34;
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(10, 6, 10, 6);
            grid.DefaultCellStyle.Padding = new Padding(10, 0, 10, 0);
        }

        _deliveries.RowTemplate.Height = 28;
        _pending.CellPainting += PaintPendingCell;
        _register.CellPainting += PaintRegisterCell;

        // « À émettre »: the files, and the note on partial reports at the foot.
        _pendingPanel.Dock = DockStyle.Fill;
        _pendingPanel.BackColor = UiTheme.SurfaceRaised;
        _pendingPanel.Margin = new Padding(0);

        _partialNote.Dock = DockStyle.Bottom;
        _partialNote.AutoSize = false;
        _partialNote.Height = 34;
        _partialNote.Padding = new Padding(12, 0, 12, 0);
        _partialNote.TextAlign = ContentAlignment.MiddleLeft;
        _partialNote.Font = UiTheme.Small;
        _partialNote.ForeColor = UiTheme.TextSecondary;
        _partialNote.AutoEllipsis = true;
        _partialNote.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(0xE6, 0xEB, 0xEF));
            e.Graphics.DrawLine(pen, 0, 0, _partialNote.Width, 0);
        };

        _pendingPanel.Controls.Add(_pending);
        _pendingPanel.Controls.Add(_partialNote);

        // « Émis » / « À remettre »: the versions, and the hand-overs of the selected one under them.
        _registerPanel.Dock = DockStyle.Fill;
        _registerPanel.BackColor = UiTheme.SurfaceRaised;
        _registerPanel.Margin = new Padding(0);

        var deliveries = new Panel { Dock = DockStyle.Bottom, Height = 150, BackColor = UiTheme.SurfaceRaised };
        _deliveryHeading.Dock = DockStyle.Top;
        _deliveryHeading.Height = 30;
        _deliveryHeading.Padding = new Padding(12, 0, 12, 0);
        _deliveryHeading.TextAlign = ContentAlignment.MiddleLeft;
        _deliveryHeading.Font = UiTheme.SmallBold;
        _deliveryHeading.ForeColor = UiTheme.TextSecondary;
        _deliveryHeading.AutoEllipsis = true;
        _deliveryHeading.UseMnemonic = false;
        _deliveryHeading.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, 0, 0, _deliveryHeading.Width, 0);
        };

        deliveries.Controls.Add(_deliveries);
        deliveries.Controls.Add(_deliveryHeading);

        _registerPanel.Controls.Add(_register);
        _registerPanel.Controls.Add(deliveries);

        card.Controls.Add(_pendingPanel);
        card.Controls.Add(_registerPanel);
        return card;
    }

    /// <summary>« Aperçu · N° », « Ouvrir en PDF », the page, and who signs, who prescribed, which versions.</summary>
    private Panel BuildAsideCard()
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
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(14, 12, 14, 12),
            Margin = new Padding(0),
            AutoScroll = true,
            BackColor = UiTheme.SurfaceRaised
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 250));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _previewTitle.Font = UiTheme.SectionTitle;
        _previewTitle.ForeColor = UiTheme.TextPrimary;
        _previewTitle.AutoSize = true;
        _previewTitle.AutoEllipsis = true;
        _previewTitle.Anchor = AnchorStyles.Left;
        _previewTitle.Margin = new Padding(0, 0, 8, 10);
        _previewTitle.UseMnemonic = false;

        _openPdf.AutoSize = true;
        _openPdf.Font = UiTheme.Small;
        _openPdf.LinkColor = UiTheme.Accent;
        _openPdf.ActiveLinkColor = UiTheme.AccentInk;
        _openPdf.DisabledLinkColor = UiTheme.TextMuted;
        _openPdf.LinkBehavior = LinkBehavior.HoverUnderline;
        _openPdf.Anchor = AnchorStyles.Right;
        _openPdf.Margin = new Padding(0, 0, 0, 10);

        _preview.Dock = DockStyle.Fill;
        _preview.Margin = new Padding(0, 0, 0, 12);

        _facts.AutoSize = true;
        _facts.Dock = DockStyle.Fill;
        _facts.ColumnCount = 2;
        _facts.RowCount = 4;
        _facts.Margin = new Padding(0);
        _facts.BackColor = UiTheme.SurfaceRaised;
        _facts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        _facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var pairs = new (Label Caption, Label Value)[]
        {
            (_signatoryCaption, _signatory), (_prescriberCaption, _prescriber),
            (_versionsCaption, _versions), (_deliveryCaption, _delivery)
        };

        for (var i = 0; i < pairs.Length; i++)
        {
            _facts.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var (caption, value) = pairs[i];
            caption.AutoSize = true;
            caption.Font = UiTheme.Body;
            caption.ForeColor = UiTheme.TextSecondary;
            caption.Margin = new Padding(0, 0, 8, 6);

            value.AutoSize = true;
            value.Font = UiTheme.Body;
            value.ForeColor = UiTheme.TextPrimary;
            value.Margin = new Padding(0, 0, 0, 6);
            value.MaximumSize = new Size(AsideWidth - 36 - 118, 0);
            value.UseMnemonic = false;

            _facts.Controls.Add(caption, 0, i);
            _facts.Controls.Add(value, 1, i);
        }

        body.Controls.Add(_previewTitle, 0, 0);
        body.Controls.Add(_openPdf, 1, 0);
        body.Controls.Add(_preview, 0, 1);
        body.SetColumnSpan(_preview, 2);
        body.Controls.Add(_facts, 0, 2);
        body.SetColumnSpan(_facts, 2);

        card.Controls.Add(body);
        return card;
    }

    /// <summary>What the last action said. Shown only while there is something to say.</summary>
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

    /// <summary>« Plus d'actions ▾ », Réimprimer l'original, Enregistrer une remise, the hint, « Émettre le compte rendu ».</summary>
    private EntryStrip BuildActionBar()
    {
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
        for (var i = 0; i < 3; i++) bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        strip.Controls.Add(bar);

        var buttons = new[] { _more, _reprint, _recordDelivery, _issue };

        void FitHint()
        {
            var used = bar.Padding.Horizontal + _hint.Margin.Horizontal
                       + buttons.Sum(b => b.PreferredSize.Width + b.Margin.Horizontal);
            var left = Math.Max(90, bar.ClientSize.Width - used);
            var font = TextRenderer.MeasureText(_hint.Text, UiTheme.Body).Width <= left ? UiTheme.Body : UiTheme.Small;
            if (_hint.Font != font) _hint.Font = font;
            if (_hint.MaximumSize.Width != left) _hint.MaximumSize = new Size(left, 0);
        }

        bar.Resize += (_, _) => FitHint();
        _hint.TextChanged += (_, _) => FitHint();

        foreach (var button in buttons)
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(0, 34);
            button.Height = 34;
            button.Margin = new Padding(0, 0, 8, 0);
        }

        _issue.Margin = new Padding(0);
        ButtonKey.ShowChevron(_more);

        _hint.AutoSize = true;
        _hint.Anchor = AnchorStyles.Right;
        _hint.TextAlign = ContentAlignment.MiddleRight;
        _hint.Font = UiTheme.Body;
        _hint.ForeColor = UiTheme.TextSecondary;
        _hint.Margin = new Padding(8, 0, 12, 0);
        _hint.UseMnemonic = false;

        var spacer = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = UiTheme.SurfaceRaised };

        bar.Controls.Add(_more, 0, 0);
        bar.Controls.Add(_reprint, 1, 0);
        bar.Controls.Add(_recordDelivery, 2, 0);
        bar.Controls.Add(spacer, 3, 0);
        bar.Controls.Add(_hint, 4, 0);
        bar.Controls.Add(_issue, 5, 0);

        _moreMenu.Items.AddRange([_amend, new ToolStripSeparator(), _refresh]);
        _moreMenu.Font = UiTheme.Body;
        _amend.Image = Icons.Render(Glyph.FilePen, UiTheme.TextPrimary, 16);
        _refresh.Image = Icons.Render(Glyph.RefreshCw, UiTheme.TextPrimary, 16);
        _refresh.ShortcutKeyDisplayString = "F5";

        return strip;
    }

    private static void AddColumn(DataGridView grid, string name, int weight, Font? font = null, bool right = false)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name, FillWeight = weight, ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = font ?? UiTheme.GridBody,
                Alignment = right ? DataGridViewContentAlignment.MiddleRight
                                  : DataGridViewContentAlignment.MiddleLeft
            }
        };
        if (right) column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        grid.Columns.Add(column);
    }

    private void BuildPendingColumns()
    {
        AddColumn(_pending, "accession", 12, UiTheme.Monospace);
        AddColumn(_pending, "patient", 20);
        AddColumn(_pending, "priority", 10);
        AddColumn(_pending, "validated", 10, UiTheme.Monospace, right: true);
        AddColumn(_pending, "awaiting", 9, UiTheme.Monospace, right: true);
        AddColumn(_pending, "validatedat", 14, UiTheme.Monospace);
        AddColumn(_pending, "kind", 12);
    }

    private void BuildRegisterColumns()
    {
        AddColumn(_register, "accession", 11, UiTheme.Monospace);
        AddColumn(_register, "patient", 16);
        AddColumn(_register, "version", 6, UiTheme.MonospaceBold, right: true);
        AddColumn(_register, "completeness", 10);
        AddColumn(_register, "released", 11, UiTheme.Monospace);
        AddColumn(_register, "signatory", 12, UiTheme.Small);
        AddColumn(_register, "prescriber", 12, UiTheme.Small);
        AddColumn(_register, "delivery", 12, UiTheme.SmallBold);
        AddColumn(_register, "flags", 14, UiTheme.SmallBold);
    }

    private void BuildDeliveryColumns()
    {
        AddColumn(_deliveries, "when", 14, UiTheme.Monospace);
        AddColumn(_deliveries, "channel", 14, UiTheme.Small);
        AddColumn(_deliveries, "recipientkind", 14, UiTheme.Small);
        AddColumn(_deliveries, "recipient", 18);
        AddColumn(_deliveries, "bearer", 16, UiTheme.Small);
        AddColumn(_deliveries, "status", 10, UiTheme.SmallBold);
        AddColumn(_deliveries, "by", 14, UiTheme.Small);
    }

    private void WireBehaviour()
    {
        _search.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await ActivateAsync();
        };

        _window.SelectedIndexChanged += async (_, _) => await ActivateAsync();
        _refresh.Click += async (_, _) => await ActivateAsync();

        _pending.SelectionChanged += (_, _) =>
        {
            RefreshActions();
            ShowPreview();
        };
        _pending.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && _issue.Enabled) await RunActionAsync(IssueAsync); };
        _issue.Click += async (_, _) => await RunActionAsync(IssueAsync);

        _register.SelectionChanged += async (_, _) =>
        {
            ShowPreview();
            await _selectionLoads.RunAsync(() => LoadDeliveriesAsync());
        };
        _register.CellFormatting += OnRegisterFormatting;
        _register.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && _reprint.Enabled) await RunActionAsync(ReprintAsync); };

        _reprint.Click += async (_, _) => await RunActionAsync(ReprintAsync);
        _openPdf.LinkClicked += async (_, _) => { if (_reprint.Enabled) await RunActionAsync(ReprintAsync); };
        _recordDelivery.Click += async (_, _) => await RunActionAsync(RecordDeliveryAsync);
        _amend.Click += async (_, _) => await RunActionAsync(AmendAsync);

        _more.Click += (_, _) =>
        {
            RefreshActions();
            _moreMenu.RightToLeft = RightToLeft;
            _moreMenu.Show(_more, new Point(IsRightToLeft ? _more.Width : 0, _more.Height));
        };

        ShowTab();
    }

    /// <summary>Ctrl+E issues the selected file's report, exactly as « Émettre le compte rendu » does.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.E) && _issue.Enabled && _issue.Visible && !IsBusy)
        {
            _issue.PerformClick();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// Runs one action with every action button held disabled until it ends. Disabling the clicked
    /// button alone was not enough: the reload inside the action moves the selection, which
    /// re-enabled the button mid-await, and a second click (or double-click) issued a second,
    /// identical report version.
    /// </summary>
    private async Task RunActionAsync(Func<Task> action)
    {
        var ran = await RunBusyAsync(async () =>
        {
            RefreshActions();
            await action();
        });

        if (ran && !IsDisposed) RefreshActions();
    }

    // ---- Tabs ---------------------------------------------------------------------------------------

    private async Task SelectTabAsync(ReportTab tab)
    {
        if (_tab == tab) return;

        _tab = tab;
        ShowTab();

        if (tab != ReportTab.ToIssue)
        {
            Render();
            await _selectionLoads.RunAsync(() => LoadDeliveriesAsync());
        }
    }

    /// <summary>Shows the selected tab's list and the matching hint, preview and actions.</summary>
    private void ShowTab()
    {
        foreach (var (tab, control) in _tabs) control.Selected = tab == _tab;

        _pendingPanel.Visible = _tab == ReportTab.ToIssue;
        _registerPanel.Visible = _tab != ReportTab.ToIssue;

        // A list filled while hidden has no current row: the first one is taken, so the preview
        // and the actions have something to work on.
        var shown = _tab == ReportTab.ToIssue ? _pending : _register;
        if (shown.CurrentRow is null && shown.Rows.Count > 0) shown.SelectFirstRow();

        RenderTabs();
        RenderHint();
        ShowPreview();
        RefreshActions();
    }

    private void RenderTabs()
    {
        foreach (var (tab, control) in _tabs)
        {
            var caption = tab switch
            {
                ReportTab.ToIssue => T("Report.TabToIssue"),
                ReportTab.ToDeliver => T("Report.TabToDeliver"),
                _ => T("Report.TabIssued")
            };

            var count = ReportRegisterRules.Count(_pendingRows, _rows, tab);

            // Work to do is counted in a pill: files to issue in the warning pair, versions to hand
            // over in the critical pair when one of them carries a critical value.
            (Color Back, Color Fore)? pill = count == 0 ? null
                : tab == ReportTab.ToIssue ? EntryPaint.Warning
                : ReportRegisterRules.IsUrgent(_rows, tab) ? EntryPaint.Critical
                : tab == ReportTab.ToDeliver ? EntryPaint.Warning
                : null;

            control.Show(caption, count, pill);
        }

        _tips.SetToolTip(_tabs[ReportTab.ToIssue], T("Report.PendingRelease", _pendingRows.Count));
        _tips.SetToolTip(_tabs[ReportTab.ToDeliver], T("Report.OnlyUndelivered"));
    }

    /// <summary>
    /// The line beside the buttons: on « À émettre », that hand-overs and rectifications come after
    /// issue; on the register, its count, the versions awaiting hand-over and the critical ones —
    /// what the register's heading said before, in critical ink when a critical report waits.
    /// </summary>
    private void RenderHint()
    {
        if (_tab == ReportTab.ToIssue)
        {
            _hint.Text = T("Report.ActionsAfterIssue");
            _hint.ForeColor = UiTheme.TextSecondary;
            return;
        }

        var undelivered = _rows.Count(r => r.AwaitingDelivery);
        var urgent = _rows.Count(r => r.IsUrgentToDeliver);

        var parts = new List<string>(3) { T("Report.VersionCount", _rows.Count) };

        if (undelivered > 0)
            parts.Add($"{undelivered} {T("Report.AwaitingDelivery").ToLowerInvariant()}");

        if (urgent > 0)
            parts.Add($"{urgent} {T("Report.CriticalUndelivered").ToLowerInvariant()}");

        _hint.Text = string.Join(" · ", parts);
        _hint.ForeColor = urgent > 0 ? UiTheme.Critical : UiTheme.TextSecondary;
    }

    // ---- Painting -------------------------------------------------------------------------------------

    private void OnRegisterFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _register.Rows.Count) return;
        if (_register.Rows[e.RowIndex].Tag is not ReportRegisterRow row) return;

        if (row.IsSuperseded)
        {
            // Superseded versions stay in the list — a reprint of what a clinician actually holds
            // is a legitimate request — but they are visibly not the current document.
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
            return;
        }

        if (row.IsUrgentToDeliver)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.AwaitingDelivery)
        {
            e.CellStyle!.ForeColor = UiTheme.Abnormal;
        }
        else if (row.IsAmendment)
        {
            e.CellStyle!.ForeColor = UiTheme.Accent;
        }
    }

    /// <summary>The nature of a report as a pill: « Définitif » in the ok pair, « Partiel » in the info pair.</summary>
    private static (Color Back, Color Fore) CompletenessColours(bool final) =>
        final ? ValidationPaint.Ok : EntryPaint.Info;

    private void PaintPendingCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.Graphics is null) return;
        if (_pending.Rows[e.RowIndex].Tag is not PendingReportRow row) return;

        var name = _pending.Columns[e.ColumnIndex].Name;
        if (name is not ("priority" or "kind" or "patient")) return;

        var rtl = IsRightToLeft;
        e.PaintBackground(e.ClipBounds, (e.State & DataGridViewElementStates.Selected) != 0);

        switch (name)
        {
            case "patient":
                TextRenderer.DrawText(e.Graphics, e.FormattedValue?.ToString() ?? string.Empty, GridBold,
                                      EntryPaint.Mirror(new Rectangle(e.CellBounds.X + 10, e.CellBounds.Y,
                                                                      e.CellBounds.Width - 20, e.CellBounds.Height),
                                                        e.CellBounds, rtl),
                                      row.Priority != OrderPriority.Routine ? UiTheme.Critical : UiTheme.TextPrimary,
                                      TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                                      | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding
                                      | (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left));
                break;

            case "priority":
                PaintPriority(e, row.Priority, rtl);
                break;

            case "kind":
                var (final, _) = ReportRegisterRules.NextRelease(row);
                ValidationPaint.DrawPills(e.Graphics, e.CellBounds,
                    [(e.FormattedValue?.ToString() ?? string.Empty, CompletenessColours(final))], rtl);
                break;
        }

        e.Handled = true;
    }

    private void PaintRegisterCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.Graphics is null) return;
        if (_register.Columns[e.ColumnIndex].Name != "completeness") return;
        if (_register.Rows[e.RowIndex].Tag is not ReportRegisterRow row) return;

        e.PaintBackground(e.ClipBounds, (e.State & DataGridViewElementStates.Selected) != 0);
        ValidationPaint.DrawPills(e.Graphics, e.CellBounds,
            [(e.FormattedValue?.ToString() ?? string.Empty,
              row.IsSuperseded ? EntryPaint.Waiting : CompletenessColours(row.Completeness == ReportCompleteness.Final))],
            IsRightToLeft);
        e.Handled = true;
    }

    /// <summary>« Urgent » as a pill, « Routine » in plain muted text, as in the mock-up.</summary>
    private void PaintPriority(DataGridViewCellPaintingEventArgs e, OrderPriority priority, bool rtl)
    {
        if (priority == OrderPriority.Routine)
        {
            TextRenderer.DrawText(e.Graphics!, T("Priority.Routine"), UiTheme.GridBody,
                                  EntryPaint.Mirror(new Rectangle(e.CellBounds.X + 10, e.CellBounds.Y,
                                                                  e.CellBounds.Width - 20, e.CellBounds.Height),
                                                    e.CellBounds, rtl),
                                  UiTheme.TextSecondary,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                                  | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis
                                  | (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left));
            return;
        }

        ValidationPaint.DrawPills(e.Graphics!, e.CellBounds,
            [(T($"Priority.{priority}"),
              priority == OrderPriority.VitalEmergency ? EntryPaint.Critical : EntryPaint.Warning)], rtl);
    }

    private static readonly Font GridBold = EntryPaint.Resized(UiTheme.BodyBold, UiTheme.GridBody.Size);

    protected override void ApplyTexts()
    {
        _heading.Text = T("Report.Plural");
        _search.PlaceholderText = T("Report.RegisterSearchPlaceholder");
        _partialNote.Text = T("Report.PartialNote");
        _openPdf.Text = T("Report.OpenPdf");

        _more.Text = T("ResultEntry.MoreActions");
        _amend.Text = $"{T("Report.IssueAmendment")}…";
        _refresh.Text = T("Action.Refresh");
        _issue.Text = T("Report.Issue");
        _reprint.Text = T("Report.Reprint");
        _recordDelivery.Text = T("Report.RecordDelivery");
        ButtonKey.Show(_issue, KeyboardShortcuts.Format(Keys.Control | Keys.E, Localizer));

        _signatoryCaption.Text = T("Report.Signatory");
        _prescriberCaption.Text = T("Order.Prescriber");
        _versionsCaption.Text = T("Report.Versions");
        _deliveryCaption.Text = T("Report.Delivery");

        RefreshWindows();

        if (_register.Columns.Count > 0)
        {
            _register.Column("accession").HeaderText = T("Order.Title");
            _register.Column("patient").HeaderText = T("Patient.Title");
            _register.Column("version").HeaderText = T("Report.Version");
            _register.Column("completeness").HeaderText = T("Report.Completeness");
            _register.Column("released").HeaderText = T("Report.ReleasedAt");
            _register.Column("signatory").HeaderText = T("Report.Signatory");
            _register.Column("prescriber").HeaderText = T("Order.Prescriber");
            _register.Column("delivery").HeaderText = T("Report.Delivery");
            _register.Column("flags").HeaderText = T("Field.Status");
        }

        if (_pending.Columns.Count > 0)
        {
            _pending.Column("accession").HeaderText = T("Order.Title");
            _pending.Column("patient").HeaderText = T("Patient.Title");
            _pending.Column("priority").HeaderText = T("Field.Priority");
            _pending.Column("validated").HeaderText = T("Report.ValidatedResults");
            _pending.Column("awaiting").HeaderText = T("Report.AwaitingShort");
            _pending.Column("validatedat").HeaderText = T("Report.LastValidatedAt");
            _pending.Column("kind").HeaderText = T("Report.Completeness");
        }

        if (_deliveries.Columns.Count > 0)
        {
            _deliveries.Column("when").HeaderText = T("Field.DateTime");
            _deliveries.Column("channel").HeaderText = T("Report.Channel");
            _deliveries.Column("recipientkind").HeaderText = T("Report.RecipientKind");
            _deliveries.Column("recipient").HeaderText = T("Report.Recipient");
            _deliveries.Column("bearer").HeaderText = T("Report.Bearer");
            _deliveries.Column("status").HeaderText = T("Field.Status");
            _deliveries.Column("by").HeaderText = T("Report.HandedOverBy");
        }

        if (_rows.Count > 0) Render();
        RenderPending();
        if (_deliveryHeading.Text.Length == 0) _deliveryHeading.Text = T("Report.Deliveries");
        ShowTab();
    }

    private void RefreshWindows()
    {
        var selected = _window.SelectedIndex;

        _window.Items.Clear();

        foreach (var days in new[] { 7, 30, 90, 365 })
            _window.Items.Add(new EnumChoice(days, T("Report.WithinDays", days)));

        _window.SelectedIndex = selected >= 0 ? selected : 1;
    }

    // ---- Loading -------------------------------------------------------------------------------------

    private int SelectedWindow => (_window.SelectedItem as EnumChoice)?.Value ?? 30;

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var search = string.IsNullOrWhiteSpace(_search.Text) ? null : _search.Text.Trim();
            var pending = await _reports.GetPendingReleaseAsync(search, ct);

            _pendingRows = pending.IsSuccess ? pending.Value : [];
            RenderPending();

            // The whole register, every time: « À remettre » is a tab over it, and the tabs show
            // their counts. The tick box it replaces asked the query for exactly those rows.
            var result = await _reports.GetRegisterAsync(search, onlyUndelivered: false, SelectedWindow, ct);

            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _rows = result.Value;
            Render();

            await LoadDeliveriesAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the report register failed.");
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T("Message.UnexpectedError");
        }
    }

    private string PatientName(string latin, string? arabic) =>
        Localizer.CurrentLanguage == AppLanguage.Arabic && arabic is { Length: > 0 } ? arabic : latin;

    private void Render()
    {
        var selected = Selected?.ReportId ?? (_register.CurrentRow?.Tag as ReportRegisterRow)?.ReportId;

        _register.SuspendLayout();
        _register.Rows.Clear();

        foreach (var row in ReportRegisterRules.RegisterRows(_rows, _tab))
        {
            var index = _register.Rows.Add(
                row.AccessionNumber,
                PatientName(row.PatientName, row.PatientNameArabic),
                $"v{row.VersionNumber}",
                T($"ReportCompleteness.{row.Completeness}"),
                row.ReleasedAt.ToLocalTime().ToString("dd/MM/yy HH:mm"),
                row.SignatoryName,
                row.PrescriberName ?? string.Empty,
                DeliveryText(row),
                string.Join(" · ", Flags(row)));

            _register.Rows[index].Tag = row;
        }

        _register.ResumeLayout(performLayout: true);

        if (selected is { } id)
        {
            foreach (DataGridViewRow row in _register.Rows)
            {
                if (row.Tag is ReportRegisterRow candidate && candidate.ReportId == id)
                {
                    row.Selected = true;
                    break;
                }
            }
        }

        if (_register.CurrentRow is null && _register.Rows.Count > 0)
            _register.SelectFirstRow();

        RenderTabs();
        RenderHint();
        ShowPreview();
        RefreshActions();
    }

    private List<string> Flags(ReportRegisterRow row)
    {
        var flags = new List<string>(4);

        if (row.IsSuperseded) flags.Add(T("Report.Superseded"));
        if (row.IsAmendment) flags.Add(T($"ReportAmendmentKind.{row.AmendmentKind}"));
        if (row.ContainsCriticalResults) flags.Add(T("Flag.Critical"));
        if (row.ContainsReservations) flags.Add(T("Report.WithReservations"));
        if (row.ContainsSubcontractedResults) flags.Add(T("Catalog.Subcontracted"));
        if (!row.AllResultsAccredited) flags.Add(T("Report.PartlyOutsideScope"));

        return flags;
    }

    private string DeliveryText(ReportRegisterRow row) =>
        row.WasDelivered
            ? $"{row.LastDeliveredAt!.Value.ToLocalTime():dd/MM/yy HH:mm}"
              + (row.DeliveryCount > 1 ? $" ({row.DeliveryCount})" : string.Empty)
            : row.AwaitingDelivery
                ? T($"ReportDeliveryPreference.{row.PrescriberPreference}")
                : "—";

    private void RenderPending()
    {
        var selected = (_pending.CurrentRow?.Tag as PendingReportRow)?.LabOrderId;

        _pending.SuspendLayout();
        _pending.Rows.Clear();

        foreach (var row in _pendingRows)
        {
            var (final, next) = ReportRegisterRules.NextRelease(row);
            var kind = T($"ReportCompleteness.{(final ? "Final" : "Partial")}")
                       + (row.ReleasedVersions > 0 ? $" · v{next}" : string.Empty);

            var index = _pending.Rows.Add(
                row.AccessionNumber,
                PatientName(row.PatientName, row.PatientNameArabic),
                T($"Priority.{row.Priority}"),
                row.ValidatedResults.ToString(),
                row.AwaitingAnalyses.ToString(),
                row.LastValidatedAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "—",
                kind);

            _pending.Rows[index].Tag = row;
        }

        _pending.ResumeLayout(performLayout: true);

        if (selected is { } id)
        {
            foreach (DataGridViewRow row in _pending.Rows)
            {
                if (row.Tag is PendingReportRow candidate && candidate.LabOrderId == id)
                {
                    row.Selected = true;
                    break;
                }
            }
        }

        if (_pending.CurrentRow is null && _pending.Rows.Count > 0)
            _pending.SelectFirstRow();

        RenderTabs();
        ShowPreview();
        RefreshActions();
    }

    /// <summary>The waiting file under the cursor, while « À émettre » is shown.</summary>
    private PendingReportRow? SelectedPending =>
        _tab == ReportTab.ToIssue ? _pending.CurrentRow?.Tag as PendingReportRow : null;

    /// <summary>The released version under the cursor, while the register is shown.</summary>
    private ReportRegisterRow? Selected =>
        _tab != ReportTab.ToIssue ? _register.CurrentRow?.Tag as ReportRegisterRow : null;

    private async Task LoadDeliveriesAsync(CancellationToken ct = default)
    {
        _deliveries.Rows.Clear();

        if (Selected is not { } row)
        {
            _deliveryHeading.Text = T("Report.Deliveries");
            _deliveryHeading.ForeColor = UiTheme.TextSecondary;
            RefreshActions();
            return;
        }

        var result = await _reports.GetDeliveriesAsync(row.ReportId, ct);

        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        _deliveries.SuspendLayout();

        foreach (var delivery in result.Value)
        {
            var index = _deliveries.Rows.Add(
                (delivery.CompletedAt ?? delivery.RequestedAt).ToLocalTime()
                    .ToString("dd/MM/yy HH:mm"),
                T($"DeliveryChannel.{delivery.Channel}"),
                T($"ReportRecipientKind.{delivery.RecipientKind}"),
                delivery.RecipientName ?? delivery.RecipientIdentifier ?? string.Empty,
                delivery.BearerName is { Length: > 0 } bearer
                    ? $"{bearer}{(delivery.BearerCin is { Length: > 0 } cin ? $" ({cin})" : string.Empty)}"
                    : string.Empty,
                T($"DeliveryStatus.{delivery.Status}"),
                delivery.HandedOverByName ?? string.Empty);

            var grid = _deliveries.Rows[index];
            grid.Tag = delivery;

            if (delivery.Status == DeliveryStatus.Failed)
                grid.DefaultCellStyle.ForeColor = UiTheme.Critical;
            else if (delivery.Status == DeliveryStatus.Delivered)
                grid.DefaultCellStyle.ForeColor = UiTheme.Normal;
        }

        _deliveries.ResumeLayout(performLayout: true);

        _deliveryHeading.Text = result.Value.Count == 0
            ? $"{T("Report.Deliveries")} — {T("Report.NoDeliveryRecorded")}"
            : $"{T("Report.Deliveries")} — {row.AccessionNumber} v{row.VersionNumber}";

        _deliveryHeading.ForeColor = result.Value.Count == 0 && row.AwaitingDelivery
            ? UiTheme.Abnormal
            : UiTheme.TextSecondary;

        RefreshActions();
    }

    /// <summary>
    /// « Aperçu »: the selected file or version, sketched as its first page, with who signs, who
    /// prescribed and which versions exist. A waiting file has no signatory yet — the release
    /// resolves it — and no document to open.
    /// </summary>
    private void ShowPreview()
    {
        var site = _currentUser.SiteCode;

        if (SelectedPending is { } pending)
        {
            var (final, next) = ReportRegisterRules.NextRelease(pending);
            var kind = T($"ReportCompleteness.{(final ? "Final" : "Partial")}");
            var name = PatientName(pending.PatientName, pending.PatientNameArabic);

            _previewTitle.Text = $"{T("Report.Preview")} · {pending.AccessionNumber}";
            _preview.Show(site, $"v{next}", name, $"{pending.AccessionNumber} · {kind}", T("Validation.Signature"));
            _preview.Visible = true;
            _facts.Visible = true;
            _openPdf.Visible = false;

            _signatory.Text = T("Report.SignatoryAtRelease");
            _signatory.ForeColor = UiTheme.TextSecondary;
            _prescriber.Text = pending.PrescriberName ?? "—";
            _versions.Text = pending.ReleasedVersions == 0
                ? T("Report.NoneIssued")
                : T("Report.IssuedVersions", pending.ReleasedVersions, next);
            _versions.ForeColor = UiTheme.TextSecondary;
            _delivery.Text = "—";
            return;
        }

        if (Selected is { } row)
        {
            var name = PatientName(row.PatientName, row.PatientNameArabic);
            var kind = T($"ReportCompleteness.{row.Completeness}");

            _previewTitle.Text = $"{T("Report.Preview")} · {row.AccessionNumber}";
            _preview.Show(site, $"v{row.VersionNumber}", name,
                          $"{row.AccessionNumber} · {kind} · {row.ReleasedAt.ToLocalTime():dd/MM/yyyy}",
                          row.SignatoryName);
            _preview.Visible = true;
            _facts.Visible = true;
            _openPdf.Visible = row.HasStoredPdf;

            _signatory.Text = row.SignatoryName.Length > 0 ? row.SignatoryName : "—";
            _signatory.ForeColor = UiTheme.TextPrimary;
            _prescriber.Text = row.PrescriberName ?? "—";

            var version = new List<string>(4) { $"v{row.VersionNumber} · {kind}" };
            if (row.IsAmendment) version.Add(T($"ReportAmendmentKind.{row.AmendmentKind}"));
            if (row.IsSuperseded) version.Add(T("Report.Superseded"));
            if (row.PageCount > 0) version.Add(T("Report.PageCountShort", row.PageCount));
            _versions.Text = string.Join(" · ", version);
            _versions.ForeColor = row.IsSuperseded ? UiTheme.TextMuted : UiTheme.TextPrimary;

            _delivery.Text = DeliveryText(row);
            return;
        }

        _previewTitle.Text = T("Report.Preview");
        _preview.Visible = false;
        _openPdf.Visible = false;
        // Nothing selected: no blank page and no column of dashes floating under it.
        _facts.Visible = false;
        _signatory.Text = _prescriber.Text = _versions.Text = _delivery.Text = "—";
    }

    private void RefreshActions()
    {
        // Nothing is offered while an action is in flight (see RunActionAsync).
        var idle = !IsBusy;

        _issue.Enabled = idle && SelectedPending is not null && _currentUser.Has(Permissions.Reports.Print);

        var row = Selected;

        _reprint.Enabled = idle && row is { HasStoredPdf: true }
                           && _currentUser.Has(Permissions.Reports.ReprintValidated);
        _openPdf.Enabled = _reprint.Enabled;

        _recordDelivery.Enabled = idle && row is not null
                                  && (_currentUser.Has(Permissions.Reports.Print)
                                      || _currentUser.Has(Permissions.Reports.Send));

        // An amendment supersedes the current version, so it is offered on that version only.
        _amend.Enabled = idle && row is { IsSuperseded: false }
                         && _currentUser.Has(Permissions.Reports.Amend);

        _refresh.Enabled = idle;
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    /// <summary>
    /// Returns the stored PDF — the document that was issued, byte for byte, with its hash checked
    /// against the archive. Never a re-render: a report re-rendered two years later would come out
    /// against a catalogue whose reference intervals and units have moved, which would make it a
    /// different document bearing the same number.
    /// </summary>
    private async Task ReprintAsync()
    {
        if (Selected is not { } row) return;

        var result = await _reports.GetStoredPdfAsync(row.ReportId);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Report.Reprint")); return; }

        try
        {
            var opened = await _documents.OpenAsync(result.Value, "pdf");

            if (opened.IsFailure) { _dialogs.ShowFailure(opened, T("Report.Reprint")); return; }

            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Report.ReprintLogged", row.AccessionNumber, row.VersionNumber);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Opening the stored report failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), T("Report.Reprint"));
        }
    }

    /// <summary>
    /// Releases the selected order's validated results: partial while analyses are still awaited,
    /// final once none are. The same release the validation screen offers.
    /// </summary>
    private async Task IssueAsync()
    {
        if (SelectedPending is not { } row) return;

        if (!_dialogs.Confirm($"{T("Report.Title")} — {row.AccessionNumber}")) return;

        UseWaitCursor = true;

        try
        {
            var released = await _reports.ReleaseAsync(row.LabOrderId);

            if (released.IsFailure) { _dialogs.ShowFailure(released, T("Report.Issue")); return; }

            try
            {
                var opened = await _documents.OpenAsync(released.Value.Pdf, "pdf");

                if (opened.IsFailure) _dialogs.ShowFailure(opened, T("Report.Issue"));
            }
            catch (Exception ex)
            {
                // The report exists either way; it can be reprinted from the register.
                _logger?.LogError(ex, "Opening the released report failed.");
            }

            _status.ForeColor = UiTheme.Normal;
            _status.Text = T("Report.Issued", row.AccessionNumber, released.Value.VersionNumber);

            await ActivateAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Releasing the report failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
        finally
        {
            UseWaitCursor = false;
            RefreshActions();
        }
    }

    private async Task RecordDeliveryAsync()
    {
        if (Selected is not { } row) return;

        var (emailReady, smsReady) = _reports.ElectronicChannels;

        using var dialog = new ReportDeliveryDialog(Localizer, _dialogs, row, emailReady, smsReady);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        Cursor = Cursors.WaitCursor;
        Result<DeliveryOutcome> result;

        try
        {
            result = await _reports.RecordDeliveryAsync(
                row.ReportId, dialog.Channel, dialog.RecipientKind, dialog.RecipientName,
                dialog.RecipientIdentifier, dialog.BearerName, dialog.BearerCin,
                dialog.AuthorizingConsentId);
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Report.RecordDelivery")); return; }

        switch (result.Value.Kind)
        {
            case DeliveryResultKind.Failed:
                // The attempt is on record; the operator needs to know it did not go, and why.
                var reason = result.Value.Failure is { } error ? T(error.ResourceKey, error.Args) : string.Empty;
                _dialogs.Warn(T("Report.SendFailed", dialog.RecipientIdentifier, reason),
                              T("Report.RecordDelivery"));
                _status.ForeColor = UiTheme.Critical;
                _status.Text = T("Report.SendFailedShort", row.AccessionNumber);
                break;

            case DeliveryResultKind.Sent:
                _status.ForeColor = UiTheme.Normal;
                _status.Text = T(dialog.Channel == DeliveryChannel.Email ? "Report.EmailSent" : "Report.SmsSent",
                                 row.AccessionNumber, dialog.RecipientIdentifier);
                break;

            default:
                _status.ForeColor = UiTheme.Normal;
                _status.Text = T("Report.DeliveryRecorded", row.AccessionNumber, row.VersionNumber);
                break;
        }

        await ActivateAsync();
    }

    /// <summary>
    /// Issues a rectified version.
    /// <para>
    /// The reason is mandatory and printed on the face of the new report, because a clinician
    /// holding the previous one needs to know what changed — an amended report that does not say
    /// what it amends is worse than no amendment, since the recipient has two documents and no way
    /// to tell which to act on.
    /// </para>
    /// </summary>
    private async Task AmendAsync()
    {
        if (Selected is not { } row) return;

        // The menu item is not a gated control: the right is checked again here. The service
        // refuses in any case.
        if (row.IsSuperseded || !_currentUser.Has(Permissions.Reports.Amend)) return;

        using var dialog = new ReportAmendmentDialog(Localizer, _dialogs, row);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        var result = await _reports.ReleaseAsync(row.LabOrderId, dialog.Language, dialog.Conclusion,
                                                  dialog.Kind, dialog.Reason);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Report.IssueAmendment")); return; }

        var released = result.Value;

        try
        {
            var opened = await _documents.OpenAsync(released.Pdf, "pdf");

            if (opened.IsFailure) _dialogs.ShowFailure(opened, T("Report.IssueAmendment"));
        }
        catch (Exception ex)
        {
            // The amendment exists either way; it can be reprinted from this list.
            _logger?.LogError(ex, "Opening the amended report failed.");
        }

        _status.ForeColor = UiTheme.Abnormal;
        _status.Text = T("Report.AmendmentIssued", released.VersionNumber, row.AccessionNumber);

        await ActivateAsync();
    }
}
