using MarocLIS.Application.Abstractions;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The opening screen: what needs attention, in severity order.
/// <para>
/// Alerts come before activity counts. The question an operator opens the day with is "what is
/// overdue or uncommunicated?", not "how many files did we register?" — and an uncommunicated
/// critical result is a breached obligation, so it is the first thing on the screen.
/// </para>
/// </summary>
public sealed class DashboardView : LocalizedView
{
    private readonly IDashboardQueries _queries;
    private readonly ICatalogQueries _catalogQueries;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly ILogger<DashboardView>? _logger;

    private readonly Label _greeting = new();
    private readonly Label _alertsHeading = new();
    private readonly Label _activityHeading = new();
    private readonly FlowLayoutPanel _alerts = new();
    private readonly FlowLayoutPanel _activity = new();
    private readonly Panel _setupBanner = new();
    private readonly Label _setupText = new();
    private readonly Label _generatedAt = new();

    public DashboardView(IDashboardQueries queries, ICatalogQueries catalogQueries,
                         ICurrentUser currentUser, IClock clock,
                         ILocalizer localizer, ICultureManager cultureManager,
                         ILogger<DashboardView>? logger = null)
        : base(localizer, cultureManager)
    {
        _queries = queries;
        _catalogQueries = catalogQueries;
        _currentUser = currentUser;
        _clock = clock;
        _logger = logger;

        BuildLayout();
    }

    public override string Title => T("Nav.Dashboard");

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter * 2, UiTheme.Gutter, UiTheme.Gutter * 2, UiTheme.Gutter);
        AutoScroll = true;

        _greeting.Font = UiTheme.PageTitle;
        _greeting.ForeColor = UiTheme.TextPrimary;
        _greeting.AutoSize = true;
        _greeting.Margin = new Padding(0, 0, 0, UiTheme.GutterTight);

        // The pre-go-live banner: placeholder tariffs and unverified reference intervals stay
        // visible until an administrator has dealt with them, rather than being buried in a report.
        _setupBanner.BackColor = UiTheme.AbnormalBackground;
        _setupBanner.Padding = new Padding(UiTheme.Gutter);
        _setupBanner.Height = 64;
        _setupBanner.Dock = DockStyle.Top;
        _setupBanner.Visible = false;

        _setupText.Dock = DockStyle.Fill;
        _setupText.Font = UiTheme.Small;
        _setupText.ForeColor = UiTheme.Abnormal;
        _setupText.AutoSize = false;
        _setupBanner.Controls.Add(_setupText);

        _alertsHeading.Font = UiTheme.SectionTitle;
        _alertsHeading.ForeColor = UiTheme.TextPrimary;
        _alertsHeading.AutoSize = true;
        _alertsHeading.Margin = new Padding(0, UiTheme.Gutter, 0, UiTheme.GutterTight);

        _activityHeading.Font = UiTheme.SectionTitle;
        _activityHeading.ForeColor = UiTheme.TextPrimary;
        _activityHeading.AutoSize = true;
        _activityHeading.Margin = new Padding(0, UiTheme.Gutter, 0, UiTheme.GutterTight);

        foreach (var panel in (FlowLayoutPanel[])[_alerts, _activity])
        {
            panel.AutoSize = true;
            panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            panel.WrapContents = true;
            panel.FlowDirection = FlowDirection.LeftToRight;
            panel.Margin = new Padding(0);
        }

        _generatedAt.Font = UiTheme.Small;
        _generatedAt.ForeColor = UiTheme.TextMuted;
        _generatedAt.AutoSize = true;
        _generatedAt.Margin = new Padding(0, UiTheme.Gutter, 0, 0);

        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(0)
        };

        stack.Controls.AddRange([_greeting, _alertsHeading, _alerts,
                                 _activityHeading, _activity, _generatedAt]);

        Controls.Add(stack);
        Controls.Add(_setupBanner);
    }

    protected override void ApplyTexts()
    {
        _greeting.Text = $"{T("App.ConnectedAs")} : {_currentUser.FullName}";
        _alertsHeading.Text = T("Dashboard.AlertsHeading");
        _activityHeading.Text = T("Dashboard.ActivityHeading");
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var snapshot = await _queries.GetSnapshotAsync(_currentUser.SiteId, _clock.Now, ct);

            if (ct.IsCancellationRequested) return;

            Render(_alerts, snapshot.Alerts);
            Render(_activity, snapshot.Activity);

            _generatedAt.Text = $"{T("Action.Refresh")} — {snapshot.GeneratedAt:HH:mm:ss}";

            await ShowSetupWarningsAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer navigation; nothing to report.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the dashboard failed.");
            _generatedAt.Text = T("Message.UnexpectedError");
        }
    }

    // The right each figure belongs to. The dashboard is open to everyone signed in, and showing the
    // cashier the critical results waiting, or the technician the unpaid invoices, is telling them
    // about work the rest of the application keeps from them.
    private static readonly Dictionary<string, string> TileRights = new(StringComparer.Ordinal)
    {
        ["Dashboard.OrdersToday"] = Permissions.Orders.View,
        ["Dashboard.OverdueOrders"] = Permissions.Orders.View,
        ["Dashboard.AwaitingCollection"] = Permissions.Samples.View,
        ["Dashboard.RejectedSamplesToday"] = Permissions.Samples.View,
        ["Dashboard.AwaitingValidation"] = Permissions.Results.View,
        ["Dashboard.CriticalPending"] = Permissions.Results.View,
        ["Report.Plural"] = Permissions.Reports.View,
        ["Dashboard.QcAlerts"] = Permissions.Quality.View,
        ["Dashboard.OpenNonConformities"] = Permissions.Quality.View,
        ["Equipment.NextCalibration"] = Permissions.Equipment.View,
        ["Inventory.ToReorder"] = Permissions.Inventory.View,
        ["Inventory.ExpiringSoon"] = Permissions.Inventory.View,
        ["Stock.DashboardExpiringCritical"] = Permissions.Inventory.View,
        ["Stock.DashboardStockOuts"] = Permissions.Inventory.View,
        ["Temperatures.DashboardMissing"] = Permissions.Inventory.RecordTemperatures,
        ["Dashboard.Receivables"] = Permissions.Billing.View,
        ["SupplierInvoices.DashboardOverdue"] = Permissions.Inventory.EnterInvoices,
        ["SupplierInvoices.DashboardDueSoon"] = Permissions.Inventory.EnterInvoices
    };

    private void Render(FlowLayoutPanel host, IReadOnlyList<DashboardMetric> metrics)
    {
        host.SuspendLayout();
        foreach (Control existing in host.Controls) existing.Dispose();
        host.Controls.Clear();

        // Urgent first, then attention, then the quiet ones — so the eye lands on what matters
        // without having to read every tile.
        foreach (var metric in metrics.Where(m => !TileRights.TryGetValue(m.LabelKey, out var right)
                                                  || PermissionGate.Allows(right))
                                      .OrderByDescending(m => m.Severity)
                                      .ThenByDescending(m => m.Value))
            host.Controls.Add(BuildTile(metric));

        host.ResumeLayout(performLayout: true);
    }

    private Panel BuildTile(DashboardMetric metric)
    {
        var (foreground, background) = metric.Severity switch
        {
            DashboardSeverity.Urgent when metric.Value > 0 =>
                (UiTheme.Critical, UiTheme.CriticalBackground),
            DashboardSeverity.Attention when metric.Value > 0 =>
                (UiTheme.Abnormal, UiTheme.AbnormalBackground),
            _ => (UiTheme.TextPrimary, UiTheme.SurfaceRaised)
        };

        var tile = new Panel
        {
            Width = 196,
            Height = 88,
            BackColor = background,
            Margin = new Padding(0, 0, UiTheme.Gutter, UiTheme.Gutter),
            Padding = new Padding(UiTheme.Gutter, UiTheme.GutterTight, UiTheme.Gutter, UiTheme.GutterTight)
        };

        var value = new Label
        {
            Text = metric.Value.ToString("N0"),
            Font = new Font("Segoe UI", 24f, FontStyle.Bold),
            ForeColor = foreground,
            AutoSize = true,
            Location = new Point(UiTheme.Gutter, 8)
        };

        var label = new Label
        {
            Text = T(metric.LabelKey),
            Font = UiTheme.Small,
            ForeColor = metric.Value > 0 ? foreground : UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(UiTheme.Gutter, 54, 172, 30)
        };

        tile.Controls.AddRange([value, label]);

        // A non-zero actionable tile is clickable and says so; a zero one is inert, because there
        // is nothing to go and look at.
        if (metric.NavigationKey is not null && metric.Value > 0)
        {
            tile.Cursor = Cursors.Hand;
            foreach (Control child in tile.Controls) child.Cursor = Cursors.Hand;
        }

        return tile;
    }

    /// <summary>
    /// Surfaces the configuration a laboratory must complete before the system is fit for
    /// production — unconfirmed tariffs and unvalidated reference intervals.
    /// </summary>
    private async Task ShowSetupWarningsAsync(CancellationToken ct)
    {
        try
        {
            var pending = await _catalogQueries.GetUnverifiedCatalogueEntriesAsync(ct);

            if (pending.Count == 0)
            {
                _setupBanner.Visible = false;
                return;
            }

            var shown = pending.Take(3).ToList();
            var remainder = pending.Count - shown.Count;

            _setupText.Text = string.Join("   ·   ", shown) +
                              (remainder > 0 ? $"   ·   (+{remainder})" : string.Empty);

            _setupBanner.Visible = true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not evaluate the catalogue setup warnings.");
            _setupBanner.Visible = false;
        }
    }
}
