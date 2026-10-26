using System.Drawing.Drawing2D;
using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Dashboard;
using MarocLIS.Application.Navigation;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The opening screen: the day's flow, then what needs attention, in severity order.
/// <para>
/// The flow comes first because it is where the work is — tubes to draw, to receive, values to
/// type, to validate, reports to issue — and each step opens the screen that holds it. Below it,
/// only the alerts that are not at zero are listed, each with the screen that deals with it; the
/// checks that are at zero are named on the side, so a quiet day reads as checked rather than as
/// empty. An uncommunicated critical result is a breached obligation, so it is graded urgent and
/// sorts first.
/// </para>
/// </summary>
public sealed class DashboardView : LocalizedView
{
    private readonly IDashboardQueries _queries;
    private readonly ICatalogQueries _catalogQueries;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly INavigationService _navigation;
    private readonly ILogger<DashboardView>? _logger;

    // Pre-go-live banner.
    private readonly TableLayoutPanel _setupBanner = new();
    private readonly PictureBox _setupIcon = new();
    private readonly Label _setupTitle = new();
    private readonly Label _setupDetail = new();
    private readonly Button _setupReview = UiTheme.SecondaryButton(string.Empty);

    // Greeting.
    private readonly Label _greeting = new();
    private readonly Label _generatedAt = new();
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);

    // The day's flow.
    private readonly TableLayoutPanel _flowCard = new();
    private readonly Label _flowHeading = new();
    private readonly Label _flowSummary = new();
    private readonly TableLayoutPanel _flowSteps = new();

    // What needs attention, and what is at zero.
    private readonly TableLayoutPanel _cards = new();
    private readonly TableLayoutPanel _alertsCard = new();
    private readonly Label _alertsHeading = new();
    private readonly Label _alertsCount = new();
    private readonly Panel _alertsScroller = new();
    private readonly TableLayoutPanel _alerts = new();
    private readonly TableLayoutPanel _quietCard = new();
    private readonly Label _quietHeading = new();
    private readonly Label _quietHint = new();
    private readonly Panel _quietScroller = new();
    private readonly TableLayoutPanel _quiet = new();

    private readonly ToolTip _tips = new();

    private DashboardSnapshot? _snapshot;
    private CatalogueSetupStatus? _setup;
    private bool _loadFailed;
    private bool _loading;

    private const int AlertRowHeight = 44;
    private const int QuietRowHeight = 26;
    private const int StepTileHeight = 100;

    // The banner's frame and its button's border: the warning pair of the design system, a shade
    // darker than warning-soft so the band reads as one object on the work surface.
    private static readonly Color BannerBorder = Color.FromArgb(0xF2, 0xC9, 0xA8);
    private static readonly Color BannerButtonBorder = Color.FromArgb(0xE0, 0xB4, 0x8F);

    public DashboardView(IDashboardQueries queries, ICatalogQueries catalogQueries,
                         ICurrentUser currentUser, IClock clock, INavigationService navigation,
                         ILocalizer localizer, ICultureManager cultureManager,
                         ILogger<DashboardView>? logger = null)
        : base(localizer, cultureManager)
    {
        _queries = queries;
        _catalogQueries = catalogQueries;
        _currentUser = currentUser;
        _clock = clock;
        _navigation = navigation;
        _logger = logger;

        BuildLayout();
    }

    public override string Title => T("Nav.Dashboard");

    // ---- Where each figure leads ----------------------------------------------------------------

    // The screen each figure opens. Which figure opens which screen, and for whom, is decided by
    // DashboardRights (with the rights of the shared menu); this only says how to open it here.
    private static readonly Dictionary<string, Func<INavigationService, Task>> Targets = new(StringComparer.Ordinal)
    {
        ["collection"] = n => n.NavigateToAsync<CollectionView>(),
        ["reception"] = n => n.NavigateToAsync<SampleReceptionView>(),
        ["recollections"] = n => n.NavigateToAsync<RecollectionView>(),
        ["entry"] = n => n.NavigateToAsync<ResultEntryView>(),
        ["validation"] = n => n.NavigateToAsync<ValidationView>(),
        ["reports"] = n => n.NavigateToAsync<ReportRegisterView>(),
        ["billing"] = n => n.NavigateToAsync<BillingView>(),
        ["qc"] = n => n.NavigateToAsync<QualityControlView>(),
        ["nonconformities"] = n => n.NavigateToAsync<NonConformityView>(),
        ["inventory"] = n => n.NavigateToAsync<InventoryView>(),
        ["supplierinvoices"] = n => n.NavigateToAsync<SupplierInvoiceView>(),
        ["temperatures"] = n => n.NavigateToAsync<TemperatureView>(),
        ["equipment"] = n => n.NavigateToAsync<EquipmentView>(),
        ["catalog"] = n => n.NavigateToAsync<CatalogView>()
    };

    private static bool CanOpen(string? key) => DashboardRights.CanOpen(key, PermissionGate.User);

    // ---- Layout ---------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter * 2, UiTheme.Gutter + 8, UiTheme.Gutter * 2, UiTheme.Gutter + 8);
        AutoScroll = true;

        BuildBanner();
        var greeting = BuildGreeting();
        BuildFlowCard();
        BuildCards();

        // Banner, greeting and flow take what they need; the two cards share the rest and scroll
        // inside themselves, so the page fits a 1366 × 768 screen whatever the day holds.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = UiTheme.Surface,
            Margin = new Padding(0),
            Padding = new Padding(0),
            MinimumSize = new Size(720, 500)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        root.Controls.Add(_setupBanner, 0, 0);
        root.Controls.Add(greeting, 0, 1);
        root.Controls.Add(_flowCard, 0, 2);
        root.Controls.Add(_cards, 0, 3);

        Controls.Add(root);
    }

    private void BuildBanner()
    {
        // The pre-go-live banner: unverified reference intervals and placeholder tariffs stay
        // visible until an administrator has dealt with them, rather than being buried in a report.
        _setupBanner.Dock = DockStyle.Fill;
        _setupBanner.AutoSize = true;
        _setupBanner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _setupBanner.BackColor = UiTheme.AbnormalBackground;
        _setupBanner.Padding = new Padding(14, 8, 14, 8);
        _setupBanner.Margin = new Padding(0, 0, 0, UiTheme.Gutter + 4);
        _setupBanner.ColumnCount = 4;
        _setupBanner.RowCount = 1;
        _setupBanner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _setupBanner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _setupBanner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _setupBanner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _setupBanner.Visible = false;
        UiChrome.AsCard(_setupBanner, BannerBorder);

        _setupIcon.Size = new Size(16, 16);
        _setupIcon.SizeMode = PictureBoxSizeMode.CenterImage;
        _setupIcon.Anchor = AnchorStyles.Left;
        _setupIcon.Margin = new Padding(0, 0, UiTheme.Gutter, 0);
        _setupIcon.Image = Icons.Render(Glyph.TriangleAlert, UiTheme.Abnormal, 16);

        _setupTitle.AutoSize = true;
        _setupTitle.Font = UiTheme.BodyBold;
        _setupTitle.ForeColor = UiTheme.Abnormal;
        _setupTitle.Anchor = AnchorStyles.Left;
        _setupTitle.Margin = new Padding(0, 0, UiTheme.Gutter, 0);

        _setupDetail.AutoSize = false;
        _setupDetail.AutoEllipsis = true;
        _setupDetail.Dock = DockStyle.Fill;
        _setupDetail.TextAlign = ContentAlignment.MiddleLeft;
        _setupDetail.ForeColor = UiTheme.Abnormal;
        _setupDetail.Margin = new Padding(0);

        _setupReview.AutoSize = true;
        _setupReview.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _setupReview.Height = 28;
        _setupReview.MinimumSize = new Size(0, 28);
        _setupReview.Font = UiTheme.Small;
        _setupReview.ForeColor = UiTheme.Abnormal;
        _setupReview.BackColor = UiTheme.AbnormalBackground;
        _setupReview.FlatAppearance.BorderColor = BannerButtonBorder;
        _setupReview.FlatAppearance.MouseOverBackColor = UiTheme.SurfaceRaised;
        _setupReview.Anchor = AnchorStyles.Right;
        _setupReview.Margin = new Padding(UiTheme.Gutter, 0, 0, 0);
        _setupReview.Click += async (_, _) => await OpenAsync("catalog");

        _setupBanner.Controls.Add(_setupIcon, 0, 0);
        _setupBanner.Controls.Add(_setupTitle, 1, 0);
        _setupBanner.Controls.Add(_setupDetail, 2, 0);
        _setupBanner.Controls.Add(_setupReview, 3, 0);
    }

    private TableLayoutPanel BuildGreeting()
    {
        _greeting.Font = UiTheme.PageTitle;
        _greeting.ForeColor = UiTheme.TextPrimary;
        _greeting.AutoSize = true;
        _greeting.Margin = new Padding(0);

        _generatedAt.ForeColor = UiTheme.TextSecondary;
        _generatedAt.AutoSize = true;
        _generatedAt.Margin = new Padding(0, 2, 0, 0);

        // F5 does the same through the shell; the button is for whoever does not know it.
        _refresh.AutoSize = true;
        _refresh.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _refresh.MinimumSize = new Size(0, UiTheme.ButtonHeight + 4);
        _refresh.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        _refresh.Margin = new Padding(UiTheme.Gutter, 0, 0, 0);
        _refresh.Click += async (_, _) => await _navigation.RefreshCurrentAsync();
        ButtonIcons.Set(_refresh, Glyph.RefreshCw);

        var titles = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            Margin = new Padding(0),
            BackColor = UiTheme.Surface
        };
        titles.Controls.AddRange([_greeting, _generatedAt]);

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = UiTheme.Surface,
            Margin = new Padding(0, 0, 0, UiTheme.Gutter + 4)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(titles, 0, 0);
        row.Controls.Add(_refresh, 1, 0);
        return row;
    }

    private void BuildFlowCard()
    {
        _flowCard.Dock = DockStyle.Fill;
        _flowCard.AutoSize = true;
        _flowCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _flowCard.BackColor = UiTheme.SurfaceRaised;
        _flowCard.Padding = new Padding(16);
        _flowCard.Margin = new Padding(0, 0, 0, UiTheme.Gutter + 4);
        _flowCard.ColumnCount = 2;
        _flowCard.RowCount = 2;
        _flowCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _flowCard.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _flowCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _flowCard.RowStyles.Add(new RowStyle(SizeType.Absolute, StepTileHeight));
        UiChrome.AsCard(_flowCard);

        StyleCardHeading(_flowHeading);
        _flowHeading.Margin = new Padding(0, 0, 0, UiTheme.Gutter);

        _flowSummary.AutoSize = true;
        _flowSummary.ForeColor = UiTheme.TextSecondary;
        _flowSummary.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _flowSummary.Margin = new Padding(UiTheme.Gutter, 2, 0, 0);

        _flowSteps.Dock = DockStyle.Fill;
        _flowSteps.BackColor = UiTheme.SurfaceRaised;
        _flowSteps.Margin = new Padding(0);
        _flowSteps.Padding = new Padding(0);
        _flowSteps.RowCount = 1;
        _flowSteps.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _flowCard.Controls.Add(_flowHeading, 0, 0);
        _flowCard.Controls.Add(_flowSummary, 1, 0);
        _flowCard.Controls.Add(_flowSteps, 0, 1);
        _flowCard.SetColumnSpan(_flowSteps, 2);
    }

    private void BuildCards()
    {
        _cards.Dock = DockStyle.Fill;
        _cards.BackColor = UiTheme.Surface;
        _cards.Margin = new Padding(0);
        _cards.ColumnCount = 2;
        _cards.RowCount = 1;
        _cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60f));
        _cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40f));
        _cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        // « À traiter en priorité »: one row per alert that is not at zero.
        BuildCard(_alertsCard, _alertsScroller, _alerts, new Padding(0, 0, UiTheme.Gutter + 4, 0));
        _alerts.ColumnCount = 4;
        _alerts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        _alerts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        _alerts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _alerts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _alerts.Padding = new Padding(16, 0, 16, 0);
        _alerts.Paint += (_, e) => PaintRowRules(_alerts, e.Graphics);

        StyleCardHeading(_alertsHeading);
        _alertsCount.AutoSize = true;
        _alertsCount.ForeColor = UiTheme.TextSecondary;
        _alertsCount.Anchor = AnchorStyles.Right;
        _alertsCount.Margin = new Padding(UiTheme.Gutter, 0, 0, 0);

        var alertsHeader = CardHeader(new Padding(16, 14, 16, 10));
        alertsHeader.ColumnCount = 2;
        alertsHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        alertsHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        alertsHeader.Controls.Add(_alertsHeading, 0, 0);
        alertsHeader.Controls.Add(_alertsCount, 1, 0);
        _alertsCard.Controls.Add(alertsHeader, 0, 0);
        _alertsCard.Controls.Add(_alertsScroller, 0, 1);

        // « Rien à signaler »: the checks that are at zero, so a quiet day reads as checked.
        BuildCard(_quietCard, _quietScroller, _quiet, new Padding(0));
        _quiet.ColumnCount = 2;
        _quiet.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        _quiet.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _quiet.Padding = new Padding(16, 0, 16, 10);

        StyleCardHeading(_quietHeading);
        _quietHint.AutoSize = true;
        _quietHint.ForeColor = UiTheme.TextSecondary;
        _quietHint.Margin = new Padding(0, 4, 0, 6);

        var quietHeader = CardHeader(new Padding(16, 14, 16, 4));
        quietHeader.ColumnCount = 1;
        quietHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        quietHeader.RowCount = 2;
        quietHeader.Controls.Add(_quietHeading, 0, 0);
        quietHeader.Controls.Add(_quietHint, 0, 1);
        _quietCard.Controls.Add(quietHeader, 0, 0);
        _quietCard.Controls.Add(_quietScroller, 0, 1);

        _cards.Controls.Add(_alertsCard, 0, 0);
        _cards.Controls.Add(_quietCard, 1, 0);
        _cards.Resize += (_, _) => SizeCards();
    }

    private static void BuildCard(TableLayoutPanel card, Panel scroller, TableLayoutPanel list, Padding margin)
    {
        // A card is as tall as its rows, up to the room left on the screen; past that its list
        // scrolls. SizeCards sets the height.
        card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        card.BackColor = UiTheme.SurfaceRaised;
        card.Margin = margin;
        card.Padding = new Padding(1);
        card.ColumnCount = 1;
        card.RowCount = 2;
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        UiChrome.AsCard(card);

        scroller.Dock = DockStyle.Fill;
        scroller.AutoScroll = true;
        scroller.BackColor = UiTheme.SurfaceRaised;
        scroller.Margin = new Padding(0, 0, 0, 6);

        list.Dock = DockStyle.Top;
        list.AutoSize = true;
        list.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        list.BackColor = UiTheme.SurfaceRaised;
        list.Margin = new Padding(0);
        scroller.Controls.Add(list);
    }

    private static TableLayoutPanel CardHeader(Padding padding) => new()
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        RowCount = 1,
        BackColor = UiTheme.SurfaceRaised,
        Padding = padding,
        Margin = new Padding(0)
    };

    private static void StyleCardHeading(Label heading)
    {
        heading.Font = UiTheme.SectionTitle;
        heading.ForeColor = UiTheme.TextPrimary;
        heading.AutoSize = true;
        heading.Anchor = AnchorStyles.Left;
        heading.Margin = new Padding(0);
    }

    // A hairline above each alert row, as in the mockup's list.
    private static void PaintRowRules(TableLayoutPanel list, Graphics graphics)
    {
        if (list.Controls.Count == 0) return;

        using var pen = new Pen(Color.FromArgb(0xE6, 0xEB, 0xEF));
        var y = 0;
        foreach (var height in list.GetRowHeights())
        {
            graphics.DrawLine(pen, 0, y, list.Width, y);
            y += height;
        }
    }

    /// <summary>Sets each card's height to its content, capped to the room below the flow.</summary>
    private void SizeCards()
    {
        var room = Math.Max(120, _cards.ClientSize.Height);

        Fit(_alertsCard, _alerts, room);
        Fit(_quietCard, _quiet, room);

        static void Fit(TableLayoutPanel card, TableLayoutPanel list, int room)
        {
            var header = card.Controls.Count > 0 ? card.Controls[0].GetPreferredSize(Size.Empty).Height : 0;
            var wanted = card.Padding.Vertical + header + list.GetPreferredSize(new Size(card.Width, 0)).Height + 6;
            card.Height = Math.Min(room - card.Margin.Vertical, wanted);
        }
    }

    // ---- Texts ----------------------------------------------------------------------------------

    protected override void ApplyTexts()
    {
        _greeting.Text = T("Dashboard.Greeting", _currentUser.FullName);
        _refresh.Text = $"{T("Action.Refresh")}   {KeyboardShortcuts.Format(Keys.F5, Localizer)}";
        _flowHeading.Text = T("Dashboard.FlowHeading");
        _alertsHeading.Text = T("Dashboard.AlertsHeading");
        _quietHeading.Text = T("Dashboard.NothingToReport");

        // The figures carry words too: a language change redraws them from the last load.
        ShowGeneratedAt();
        if (_snapshot is not null) Render(_snapshot);
        ShowSetup(_setup);
    }

    private void ShowGeneratedAt()
    {
        _generatedAt.Text = DashboardScreenRules.DateLine(_clock.Now, _loadFailed ? null : _snapshot?.GeneratedAt, _loadFailed, T,
                                                          CultureInfo.CurrentCulture);
        _generatedAt.ForeColor = _loadFailed ? UiTheme.Critical : UiTheme.TextSecondary;
    }

    // ---- Loading --------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        // A second press of Actualiser while the first load runs would start a second query on the
        // same context, which EF refuses.
        if (_loading) return;
        _loading = true;

        try
        {
            var snapshot = await _queries.GetSnapshotAsync(_currentUser.SiteId, _clock.Now, ct);

            if (ct.IsCancellationRequested || IsDisposed) return;

            _snapshot = snapshot;
            _loadFailed = false;
            Render(snapshot);
            ShowGeneratedAt();

            await ShowSetupWarningsAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer navigation; nothing to report.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the dashboard failed.");
            if (IsDisposed) return;
            _loadFailed = true;
            ShowGeneratedAt();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Surfaces the configuration a laboratory must complete before the system is fit for
    /// production — unvalidated reference intervals and unconfirmed tariffs.
    /// </summary>
    private async Task ShowSetupWarningsAsync(CancellationToken ct)
    {
        try
        {
            var status = await _catalogQueries.GetCatalogueSetupStatusAsync(ct);
            if (IsDisposed) return;
            _setup = status;
            ShowSetup(status);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer navigation.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not evaluate the catalogue setup warnings.");
            _setup = null;
            ShowSetup(null);
        }
    }

    private void ShowSetup(CatalogueSetupStatus? status)
    {
        if (status is null
            || DashboardScreenRules.Setup(status.UnverifiedRangeParameterCodes, status.PlaceholderTariffAnalysisCodes,
                                          PermissionGate.User, T) is not { } banner)
        {
            _setupBanner.Visible = false;
            return;
        }

        _setupTitle.Text = banner.Title;
        _setupDetail.Text = banner.Detail;
        _tips.SetToolTip(_setupDetail, _setupDetail.Text);

        // The banner shows to everyone, as before; the way to the catalogue only to whom may open it.
        _setupReview.Text = T(banner.ReviewKey);
        _setupReview.Visible = banner.Review;
        _setupBanner.Visible = true;
    }

    // ---- Rendering ------------------------------------------------------------------------------

    private void Render(DashboardSnapshot snapshot)
    {
        SuspendLayout();
        try
        {
            var board = DashboardScreenRules.Board(snapshot, PermissionGate.User, T, CultureInfo.CurrentCulture);
            RenderFlow(board);
            RenderAlerts(board);
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }

        SizeCards();

        // The tiles are laid out again once their columns are known; in a mirrored (Arabic) page
        // a part of them otherwise kept the paint of where they first stood.
        Invalidate(invalidateChildren: true);
    }

    private void RenderFlow(DashboardBoard board)
    {
        // Files registered and reports issued today, as the line beside the heading.
        _flowSummary.Text = string.Join("  ·  ", board.Summary);
        var steps = board.Steps;

        _flowSteps.SuspendLayout();
        ClearControls(_flowSteps);
        _flowSteps.ColumnStyles.Clear();
        _flowSteps.ColumnCount = Math.Max(1, steps.Count * 2 - 1);

        for (var i = 0; i < steps.Count; i++)
        {
            if (i > 0)
            {
                _flowSteps.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
                _flowSteps.Controls.Add(new FlowArrow(), i * 2 - 1, 0);
            }

            _flowSteps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / steps.Count));
            _flowSteps.Controls.Add(BuildStepTile(steps[i]), i * 2, 0);
        }

        _flowSteps.ResumeLayout(performLayout: true);
        _flowCard.Visible = board.ShowsFlow;
    }

    private TableLayoutPanel BuildStepTile(DashboardStep step)
    {
        var metric = step.Metric;
        var stepKey = step.StepKey;
        var highlighted = step.Highlighted;
        var ink = highlighted ? UiTheme.AccentInk : UiTheme.TextPrimary;

        var tile = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = highlighted ? UiTheme.AccentSubtle : UiTheme.SurfaceRaised,
            Padding = new Padding(12, 10, 12, 10),
            Margin = new Padding(0)
        };
        tile.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        tile.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tile.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        tile.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        UiChrome.AsCard(tile, highlighted ? UiTheme.Accent : UiTheme.Border);

        // One line, so the five figures stand level; a caption too long for a narrow tile is cut
        // and given whole in its tooltip.
        var caption = new Label
        {
            Text = T(metric.LabelKey),
            AutoSize = false,
            AutoEllipsis = true,
            Height = TextRenderer.MeasureText("Ag", Font).Height + 2,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = highlighted ? UiTheme.AccentInk : UiTheme.TextSecondary,
            Margin = new Padding(0)
        };
        _tips.SetToolTip(caption, caption.Text);

        var value = new Label
        {
            Text = metric.Value.ToString("N0", CultureInfo.CurrentCulture),
            Font = UiTheme.Kpi,
            ForeColor = ink,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0)
        };

        tile.Controls.Add(caption, 0, 0);
        tile.Controls.Add(value, 0, 1);

        // The way to the step's screen, only for whom may open it: the tile is then clickable as a
        // whole, and its link takes the keyboard.
        if (step.Opens is { } key)
        {
            var chevron = RightToLeft == RightToLeft.Yes ? "‹" : "›";
            var link = new LinkLabel
            {
                Text = $"{T(stepKey)} {chevron}",
                AutoSize = true,
                Font = UiTheme.SmallBold,
                LinkColor = highlighted ? UiTheme.AccentInk : UiTheme.Accent,
                ActiveLinkColor = UiTheme.AccentInk,
                VisitedLinkColor = highlighted ? UiTheme.AccentInk : UiTheme.Accent,
                LinkBehavior = LinkBehavior.HoverUnderline,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0),
                UseMnemonic = false
            };
            link.LinkClicked += async (_, _) => await OpenAsync(key, metric);
            tile.Controls.Add(link, 0, 2);

            tile.Cursor = Cursors.Hand;
            tile.Click += async (_, _) => await OpenAsync(key, metric);
            foreach (var label in (Label[])[caption, value])
            {
                label.Cursor = Cursors.Hand;
                label.Click += async (_, _) => await OpenAsync(key, metric);
            }
        }

        return tile;
    }

    private void RenderAlerts(DashboardBoard board)
    {
        // Urgent first, then what to plan, to handle, to follow (the rules' order).
        var pending = board.Pending;
        var quiet = board.QuietKeys;

        _alertsCount.Text = T("Dashboard.SubjectCount", pending.Count);

        _alerts.SuspendLayout();
        ClearControls(_alerts);
        _alerts.RowStyles.Clear();
        _alerts.RowCount = Math.Max(1, pending.Count);

        if (pending.Count == 0)
        {
            _alerts.RowStyles.Add(new RowStyle(SizeType.Absolute, AlertRowHeight));
            var none = new Label
            {
                Text = T("Dashboard.NoPriority"),
                AutoSize = true,
                ForeColor = UiTheme.TextSecondary,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0)
            };
            _alerts.Controls.Add(none, 0, 0);
            _alerts.SetColumnSpan(none, 4);
        }

        for (var row = 0; row < pending.Count; row++)
        {
            _alerts.RowStyles.Add(new RowStyle(SizeType.Absolute, AlertRowHeight));
            AddAlertRow(pending[row], row);
        }

        _alerts.ResumeLayout(performLayout: true);
        _alerts.Invalidate();

        _quiet.SuspendLayout();
        ClearControls(_quiet);
        _quiet.RowStyles.Clear();
        _quiet.RowCount = Math.Max(1, quiet.Count);

        _quietHint.Text = T(board.QuietHintKey);

        for (var row = 0; row < quiet.Count; row++)
        {
            _quiet.RowStyles.Add(new RowStyle(SizeType.Absolute, QuietRowHeight));

            var check = new PictureBox
            {
                Size = new Size(16, 16),
                SizeMode = PictureBoxSizeMode.CenterImage,
                Image = Icons.Render(Glyph.Check, UiTheme.Normal, 14),
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0)
            };

            var text = new Label
            {
                Text = T(quiet[row]),
                AutoSize = false,
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiTheme.TextPrimary,
                Margin = new Padding(0)
            };

            _quiet.Controls.Add(check, 0, row);
            _quiet.Controls.Add(text, 1, row);
        }

        _quiet.ResumeLayout(performLayout: true);
    }

    private void AddAlertRow(DashboardAlert alert, int row)
    {
        var metric = alert.Metric;

        var (fill, ink, count) = alert.Kind switch
        {
            DashboardAlertKind.Urgent => (UiTheme.CriticalBackground, UiTheme.CriticalInk, UiTheme.Critical),
            DashboardAlertKind.ToFollow => (UiTheme.InfoBackground, UiTheme.Info, UiTheme.Info),
            _ => (UiTheme.AbnormalBackground, UiTheme.Abnormal, UiTheme.Abnormal)
        };

        var pill = new Pill(T(alert.PillKey), fill, ink) { Anchor = AnchorStyles.Left, Margin = new Padding(0) };

        var value = new Label
        {
            Text = metric.Value.ToString("N0", CultureInfo.CurrentCulture),
            Font = UiTheme.SectionTitle,
            ForeColor = count,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(0, 1, UiTheme.Gutter, 0)
        };

        var text = new Label
        {
            Text = T(alert.TextKey),
            AutoSize = false,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextPrimary,
            Margin = new Padding(0, 1, 0, 0)
        };

        _alerts.Controls.Add(pill, 0, row);
        _alerts.Controls.Add(value, 1, row);
        _alerts.Controls.Add(text, 2, row);

        // No button where the user may not open the screen: the figure is still theirs to know.
        if (alert.Opens is { } key)
        {
            var action = UiTheme.SecondaryButton(T(alert.ActionKey));
            action.AutoSize = true;
            action.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            action.Font = UiTheme.Small;
            action.Padding = new Padding(6, 0, 6, 0);
            action.MinimumSize = new Size(64, 28);
            action.Height = 28;
            action.Anchor = AnchorStyles.Right;
            action.Margin = new Padding(UiTheme.Gutter, 0, 0, 0);
            action.Click += async (_, _) => await OpenAsync(key, alert.Metric);
            _alerts.Controls.Add(action, 3, row);
        }
    }

    private static void ClearControls(Control host)
    {
        var existing = host.Controls.Cast<Control>().ToList();
        host.Controls.Clear();
        foreach (var control in existing) control.Dispose();
    }

    // ---- Navigation -----------------------------------------------------------------------------

    /// <summary>
    /// Opens the screen behind a figure. Through its menu entry when the menu shows it, so the
    /// highlighted entry, the breadcrumb and F1 follow as if the operator had clicked the menu;
    /// otherwise — the entry folded away in its group — straight through the navigation service.
    /// </summary>
    private async Task OpenAsync(string key, DashboardMetric? metric = null)
    {
        if (!CanOpen(key) || !Targets.TryGetValue(key, out var open)) return;

        // A figure counted on a tab other than the screen's first opens that tab, as on the web:
        // « Comptes rendus à émettre » opened the reports on those already issued.
        if (metric is not null && DashboardScreenRules.TabOf(metric) is { } tab && key == "reports")
        {
            await _navigation.NavigateToAsync<ReportRegisterView>(view => view.OpenOnTab(tab));
            return;
        }

        if (FindForm() is { } shell && FindMenuEntry(shell, key) is { CanSelect: true } entry)
        {
            entry.PerformClick();
            return;
        }

        await open(_navigation);
    }

    private Button? FindMenuEntry(Control parent, string key)
    {
        foreach (Control child in parent.Controls)
        {
            if (ReferenceEquals(child, this)) continue;
            if (child is Button { Tag: string tag } button && tag == key) return button;
            if (FindMenuEntry(child, key) is { } found) return found;
        }

        return null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }

    // ---- Small drawn parts ----------------------------------------------------------------------

    /// <summary>A status pill: a rounded tag in one of the design system's soft/ink pairs.</summary>
    private sealed class Pill : Control
    {
        private readonly Color _fill;

        public Pill(string text, Color fill, Color ink)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = text;
            _fill = fill;
            ForeColor = ink;
            Font = UiTheme.SmallBold;
            TabStop = false;
            var size = TextRenderer.MeasureText(text, Font);
            Size = new Size(size.Width + 20, Math.Max(22, size.Height + 6));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? UiTheme.SurfaceRaised);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using (var path = UiChrome.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2))
            using (var brush = new SolidBrush(_fill))
                e.Graphics.FillPath(brush, path);

            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// The arrow between two steps, drawn rather than typed: arrow characters are missing from the
    /// fonts of some workstations. It points the way the page reads, so leftwards in Arabic.
    /// </summary>
    private sealed class FlowArrow : Control
    {
        public FlowArrow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Fill;
            Margin = new Padding(0);
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? UiTheme.SurfaceRaised);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var y = Height / 2;
            var left = Width / 2 - 7;
            var right = Width / 2 + 7;
            var rtl = RightToLeft == RightToLeft.Yes;
            var tip = rtl ? left : right;
            var back = rtl ? 5 : -5;

            using var pen = new Pen(UiTheme.BorderStrong, 1.6f);
            e.Graphics.DrawLine(pen, left, y, right, y);
            e.Graphics.DrawLine(pen, tip, y, tip + back, y - 5);
            e.Graphics.DrawLine(pen, tip, y, tip + back, y + 5);
        }
    }
}
