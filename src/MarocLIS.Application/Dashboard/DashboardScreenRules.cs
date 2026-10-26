using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Application.Navigation;
using MarocLIS.Application.Reports;

namespace MarocLIS.Application.Dashboard;

/// <summary>The kind of attention an alert asks for, in the order the list shows them.</summary>
public enum DashboardAlertKind
{
    Urgent = 0,
    ToPlan = 1,
    ToHandle = 2,
    ToFollow = 3
}

/// <summary>
/// How an alert reads in the list: its wording, the caption of its button, and the kind of
/// attention it asks for when it is not urgent.
/// </summary>
public sealed record DashboardAlertLook(string TextKey, string ActionKey, DashboardAlertKind Kind);

/// <summary>
/// A step of the day's flow: its figure, the caption of the way to its screen, whether it is the
/// step the biologist's day turns on, and the screen it opens for this operator (none when they may
/// not open it: the figure is still theirs to know).
/// </summary>
public sealed record DashboardStep(DashboardMetric Metric, string StepKey, bool Highlighted, string? Opens);

/// <summary>
/// An alert not at zero: its figure, its wording, the caption of its button, the kind of attention
/// it asks for, the pill that says so, and the screen it opens for this operator (none when they may
/// not open it).
/// </summary>
public sealed record DashboardAlert(DashboardMetric Metric, string TextKey, string ActionKey, DashboardAlertKind Kind,
                                    string PillKey, string? Opens);

/// <summary>
/// What the dashboard shows an operator: the line beside the flow's heading (files registered and
/// reports issued today), the steps of the flow, the alerts not at zero in the order they are read,
/// and the wording of the checks at zero.
/// </summary>
public sealed record DashboardBoard(IReadOnlyList<string> Summary, IReadOnlyList<DashboardStep> Steps,
                                    IReadOnlyList<DashboardAlert> Pending, IReadOnlyList<string> QuietKeys)
{
    /// <summary>The flow's card is drawn when it has a step or a line to show.</summary>
    public bool ShowsFlow => Steps.Count > 0 || Summary.Count > 0;

    /// <summary>« 3 sujet(s) » beside the alerts' heading.</summary>
    public const string CountKey = "Dashboard.SubjectCount";

    /// <summary>The sentence under « Rien à signaler »: what the list is, or that nothing is at zero.</summary>
    public string QuietHintKey => QuietKeys.Count > 0 ? "Dashboard.NothingToReportHint" : "Dashboard.NothingAtZero";
}

/// <summary>
/// The pre-go-live banner: unverified reference intervals and placeholder tariffs stay visible until
/// an administrator has dealt with them. Its title, the codes named (the first three, then « and N
/// others »), the caption of its button, and whether the button is offered — only to whom may open
/// the catalogue; the banner itself shows to everyone.
/// </summary>
public sealed record DashboardSetupBanner(string Title, string Detail, string ReviewKey, bool Review);

/// <summary>
/// The rules of the dashboard that need no window: the five steps of a file through the
/// laboratory, which one stands out, the figures and the screens each operator is offered
/// (<see cref="DashboardRights"/>), the alerts not at zero graded and sorted — urgent first, then
/// what to plan, to handle, to follow, the larger first — with their wording, pill and button, the
/// checks at zero named on the side, the pre-go-live banner, and the date and refresh line under the
/// greeting. The workstation's DashboardView and the web's dashboard apply the same ones; the
/// figures are IDashboardQueries'.
/// </summary>
public static class DashboardScreenRules
{
    /// <summary>The five steps of a file through the laboratory, in the order it takes them.</summary>
    public static IReadOnlyList<(string MetricKey, string StepKey)> FlowSteps { get; } =
    [
        ("Dashboard.AwaitingCollection", "Dashboard.StepCollection"),
        ("Dashboard.AwaitingReception", "Dashboard.StepReception"),
        ("Dashboard.AwaitingEntry", "Dashboard.StepEntry"),
        ("Dashboard.AwaitingValidation", "Dashboard.StepValidation"),
        ("Dashboard.ReportsToRelease", "Dashboard.StepReports")
    ];

    /// <summary>The step the biologist's day turns on; it stands out in the flow.</summary>
    public const string HighlightedStep = "Dashboard.AwaitingValidation";

    private static readonly Dictionary<string, DashboardAlertLook> Looks = new(StringComparer.Ordinal)
    {
        ["Dashboard.CriticalPending"] = new("Dashboard.CriticalPending", "Dashboard.ActionView", DashboardAlertKind.ToHandle),
        ["Dashboard.OverdueOrders"] = new("Dashboard.OverdueOrdersLong", "Dashboard.ActionView", DashboardAlertKind.ToHandle),
        ["Dashboard.QcAlerts"] = new("Dashboard.QcAlertsLong", "Dashboard.ActionOpenQc", DashboardAlertKind.ToHandle),
        ["Equipment.NextCalibration"] = new("Dashboard.CalibrationOverdue", "Dashboard.ActionPlan", DashboardAlertKind.ToPlan),
        ["Dashboard.OpenNonConformities"] = new("Dashboard.OpenNonConformities", "Dashboard.ActionView", DashboardAlertKind.ToHandle),
        ["Inventory.ToReorder"] = new("Inventory.ToReorder", "Dashboard.ActionOrder", DashboardAlertKind.ToHandle),
        ["Inventory.ExpiringSoon"] = new("Dashboard.ReagentsExpiringSoon", "Dashboard.ActionView", DashboardAlertKind.ToHandle),
        ["Stock.DashboardExpiringCritical"] = new("Stock.DashboardExpiringCritical", "Dashboard.ActionView", DashboardAlertKind.ToHandle),
        ["Stock.DashboardStockOuts"] = new("Stock.DashboardStockOuts", "Dashboard.ActionView", DashboardAlertKind.ToHandle),
        ["Temperatures.DashboardMissing"] = new("Temperatures.DashboardMissing", "Dashboard.ActionView", DashboardAlertKind.ToHandle),
        ["Dashboard.RejectedSamplesToday"] = new("Dashboard.RejectedSamplesToday", "Dashboard.ActionView", DashboardAlertKind.ToFollow),
        ["Dashboard.Receivables"] = new("Dashboard.ReceivablesLong", "Dashboard.ActionView", DashboardAlertKind.ToFollow),
        ["SupplierInvoices.DashboardOverdue"] = new("SupplierInvoices.DashboardOverdue", "Dashboard.ActionView", DashboardAlertKind.ToHandle),
        ["SupplierInvoices.DashboardDueSoon"] = new("SupplierInvoices.DashboardDueSoon", "Dashboard.ActionView", DashboardAlertKind.ToPlan)
    };

    /// <summary>How an alert reads; a figure not listed reads as its own label, « Voir », to handle.</summary>
    public static DashboardAlertLook LookOf(string labelKey) =>
        Looks.TryGetValue(labelKey, out var look) ? look : new DashboardAlertLook(labelKey, "Dashboard.ActionView", DashboardAlertKind.ToHandle);

    /// <summary>An urgent figure is urgent whatever it is; any other asks for its look's kind of attention.</summary>
    public static DashboardAlertKind KindOf(DashboardMetric metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return metric.Severity == DashboardSeverity.Urgent ? DashboardAlertKind.Urgent : LookOf(metric.LabelKey).Kind;
    }

    public static string PillKey(DashboardAlertKind kind) => kind switch
    {
        DashboardAlertKind.Urgent => "Dashboard.PillUrgent",
        DashboardAlertKind.ToPlan => "Dashboard.PillToPlan",
        DashboardAlertKind.ToFollow => "Dashboard.PillToFollow",
        _ => "Dashboard.PillToHandle"
    };

    /// <summary>The screen a figure opens for <paramref name="user"/>, or none (<see cref="DashboardRights.CanOpen"/>).</summary>
    public static string? Opens(DashboardMetric metric, ICurrentUser? user)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return DashboardRights.CanOpen(metric.NavigationKey, user) ? metric.NavigationKey : null;
    }

    /// <summary>
    /// The tab a figure opens its screen on, when it is not the screen's own first tab: « Comptes
    /// rendus à émettre » opens the reports on « À émettre », where those files are, and not on the
    /// register of the reports already issued. Null for every other figure.
    /// </summary>
    public static string? TabOf(DashboardMetric metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return metric.LabelKey == "Dashboard.ReportsToRelease" ? nameof(ReportTab.ToIssue) : null;
    }

    /// <summary>
    /// What <paramref name="user"/> is shown of <paramref name="snapshot"/>: only the figures their
    /// rights let them see, each with the way to its screen when they may open it.
    /// </summary>
    public static DashboardBoard Board(DashboardSnapshot snapshot, ICurrentUser? user, ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(culture);

        bool MaySee(DashboardMetric m) => DashboardRights.MaySee(m.LabelKey, user);

        var byKey = snapshot.Activity.GroupBy(m => m.LabelKey, StringComparer.Ordinal)
                                     .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Files registered and reports issued today, as the line beside the heading.
        var summary = new List<string>();
        if (byKey.TryGetValue("Dashboard.OrdersToday", out var orders) && MaySee(orders))
            summary.Add(text("Dashboard.OrdersRegisteredToday", orders.Value.ToString("N0", culture)));
        if (byKey.TryGetValue("Report.Plural", out var released) && MaySee(released) && released.Value > 0)
            summary.Add(text("Dashboard.ReportsReleasedToday", released.Value.ToString("N0", culture)));

        var steps = FlowSteps
            .Where(s => byKey.TryGetValue(s.MetricKey, out var metric) && MaySee(metric))
            .Select(s => new DashboardStep(byKey[s.MetricKey], s.StepKey, s.MetricKey == HighlightedStep, Opens(byKey[s.MetricKey], user)))
            .ToList();

        var visible = snapshot.Alerts.Where(MaySee).ToList();

        // Urgent first, then what to plan, to handle, to follow — so the eye lands on what matters
        // without having to read every row.
        var pending = visible.Where(m => m.Value > 0)
            .OrderBy(KindOf)
            .ThenByDescending(m => m.Value)
            .Select(m =>
            {
                var look = LookOf(m.LabelKey);
                var kind = KindOf(m);
                return new DashboardAlert(m, look.TextKey, look.ActionKey, kind, PillKey(kind), Opens(m, user));
            })
            .ToList();

        var quiet = visible.Where(m => m.Value <= 0).Select(m => LookOf(m.LabelKey).TextKey).ToList();

        return new DashboardBoard(summary, steps, pending, quiet);
    }

    /// <summary>
    /// The pre-go-live banner for the codes still awaiting the laboratory's check, or none when the
    /// catalogue is complete: the reference intervals first when both are pending, the tariffs then
    /// named after them as a count.
    /// </summary>
    public static DashboardSetupBanner? Setup(IReadOnlyList<string> unverifiedRanges, IReadOnlyList<string> placeholderTariffs,
                                              ICurrentUser? user, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(unverifiedRanges);
        ArgumentNullException.ThrowIfNull(placeholderTariffs);
        ArgumentNullException.ThrowIfNull(text);
        if (unverifiedRanges.Count == 0 && placeholderTariffs.Count == 0) return null;

        string Names(IReadOnlyList<string> codes)
        {
            var shown = string.Join(", ", codes.Take(3));
            return codes.Count > 3 ? text("Dashboard.AndOthers", shown, codes.Count - 3) : shown;
        }

        string title;
        var details = new List<string>();
        if (unverifiedRanges.Count > 0)
        {
            title = text("Dashboard.RangesPending", unverifiedRanges.Count);
            details.Add(Names(unverifiedRanges));
            if (placeholderTariffs.Count > 0) details.Add(text("Dashboard.TariffsPending", placeholderTariffs.Count));
        }
        else
        {
            title = text("Dashboard.TariffsPending", placeholderTariffs.Count);
            details.Add(Names(placeholderTariffs));
        }

        return new DashboardSetupBanner(title, string.Join("  ·  ", details),
                                        unverifiedRanges.Count > 0 ? "Dashboard.ReviewRanges" : "Dashboard.OpenCatalog",
                                        DashboardRights.CanOpen("catalog", user));
    }

    /// <summary>
    /// The line under the greeting: « Lundi 26 octobre 2026 », then when the figures were read
    /// (« actualisé à 09:41:07 »), or that they could not be.
    /// </summary>
    public static string DateLine(DateTimeOffset now, DateTimeOffset? generatedAt, bool failed, ScreenText text, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(culture);
        var date = (generatedAt ?? now).ToString("dddd d MMMM yyyy", culture);
        if (date.Length > 0) date = char.ToUpper(date[0], culture) + date[1..];

        if (failed) return $"{date} · {text("Message.UnexpectedError")}";
        return generatedAt is { } at
            ? $"{date} · {text("Dashboard.RefreshedAt", at.ToString("HH:mm:ss", CultureInfo.InvariantCulture))}"
            : date;
    }
}
