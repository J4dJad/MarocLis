using MarocLIS.Application.Input;
using MarocLIS.Application.Steering;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Statistics;

/// <summary>
/// What « Statistiques d'activité » offers: computing to anyone who may see the statistics,
/// exporting once a period has been computed.
/// </summary>
public sealed record StatisticsActions(bool Compute, bool Export);

/// <summary>The four tables of the screen.</summary>
public enum StatisticsTab
{
    ByDepartment,
    ByTest,
    ByPayer,
    ByDay
}

/// <summary>
/// The rules of « Statistiques d'activité » that need no window: what is offered by which right, the
/// eight key figures (volumes, turnaround and money), the columns and cells of the tables by
/// department, analysis, payer and day as the screen writes them, the table's CSV and its file's
/// name, and the period refused beside its dates. The workstation's StatisticsView and the web's
/// screen apply the same ones; the computation is StatisticsService's.
/// </summary>
public static class StatisticsScreenRules
{
    public const string PeriodField = "statistics-period";

    public static StatisticsActions Actions(Func<string, bool> has, ActivityStatistics? result)
    {
        ArgumentNullException.ThrowIfNull(has);
        var view = has(Permissions.Statistics.View);
        return new StatisticsActions(view, view && result is not null);
    }

    public static string TabKey(StatisticsTab tab) => $"Statistics.{tab}";

    /// <summary>The columns of a table, in order, with their captions.</summary>
    public static IReadOnlyList<ReportColumn> Columns(StatisticsTab tab) => tab switch
    {
        StatisticsTab.ByTest =>
        [
            new("code", "Field.Code"), new("name", "Field.Name"), new("tests", "Statistics.Tests", true),
            new("tat", "Statistics.AverageTat", true), new("amount", "Statistics.Charged", true)
        ],
        StatisticsTab.ByPayer =>
        [
            new("name", "Statistics.Payer"), new("orders", "Statistics.Orders", true), new("tests", "Statistics.Tests", true),
            new("amount", "Statistics.Charged", true), new("payerShare", "Statistics.PayerShare", true),
            new("patientShare", "Statistics.PatientShare", true)
        ],
        StatisticsTab.ByDay =>
        [
            new("date", "Field.Date"), new("orders", "Statistics.Orders", true), new("tests", "Statistics.Tests", true)
        ],
        _ =>
        [
            new("code", "Field.Code"), new("name", "Field.Department"), new("tests", "Statistics.Tests", true),
            new("validated", "Statistics.Validated", true), new("tat", "Statistics.AverageTat", true),
            new("onTime", "Statistics.OnTime", true), new("amount", "Statistics.Charged", true)
        ]
    };

    /// <summary>The columns read as identifiers, in the fixed-pitch face: codes and dates.</summary>
    public static bool IsIdentifier(string column) => column is "code" or "date";

    /// <summary>
    /// A table's rows, their cells in the columns' order as the screen shows them and the CSV writes
    /// them: names in the operator's language, a payer-less file as « Patient payant », delays in
    /// hours, amounts as <paramref name="amount"/> writes them.
    /// </summary>
    public static IReadOnlyList<ReportRow> Rows(ActivityStatistics? result, StatisticsTab tab, AppLanguage language,
                                                ScreenText text, Func<decimal, string> amount, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(culture);
        if (result is null) return [];

        string Count(int value) => PeriodReport.Count(value, culture);
        string Hours(double? value) => PeriodReport.Hours(value, text, culture);

        return tab switch
        {
            StatisticsTab.ByTest => [.. result.Tests.Select(t => new ReportRow(
                [t.Code, t.Name.For(language), Count(t.TestCount), Hours(t.AverageTurnaroundHours), amount(t.ChargedAmount)]))],
            StatisticsTab.ByPayer => [.. result.Payers.Select(p => new ReportRow(
                [p.Name?.For(language) ?? text("Statistics.SelfPaying"), Count(p.OrderCount), Count(p.TestCount),
                 amount(p.ChargedAmount), amount(p.PayerShare), amount(p.PatientShare)]))],
            StatisticsTab.ByDay => [.. result.Days.Select(d => new ReportRow(
                [d.Date.ToString("ddd dd/MM/yy", culture), Count(d.OrderCount), Count(d.TestCount)]))],
            _ => [.. result.Departments.Select(d => new ReportRow(
                [d.Code, d.Name.For(language), Count(d.TestCount), Count(d.ValidatedCount), Hours(d.AverageTurnaroundHours),
                 PeriodReport.Share(d.OnTimePercent, culture), amount(d.ChargedAmount)]))]
        };
    }

    /// <summary>
    /// The eight key figures, two rows of four large enough to read across a meeting room: the
    /// files, the patients, the tests (and those cancelled), the turnaround from reception to
    /// validation and the share on time, then the money charged, invoiced, credited and collected.
    /// </summary>
    public static IReadOnlyList<ReportTile> Tiles(ActivityStatistics result, ScreenText text, Func<decimal, string> amount,
                                                  IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(culture);
        var s = result.Summary;

        // « Délai réception → validation · dans les délais »: the caption names the figure, its
        // second part the line under it, where it is read with its share.
        var turnaround = text("Statistics.TurnaroundLine");
        var dot = turnaround.IndexOf(" · ", StringComparison.Ordinal);
        var (caption, after) = dot > 0 ? (turnaround[..dot], turnaround[(dot + 3)..]) : (turnaround, null);
        var onTime = PeriodReport.Share(s.OnTimePercent, culture);

        return
        [
            new("orders", text("Statistics.Orders"), PeriodReport.Count(s.OrderCount, culture)),
            new("patients", text("Statistics.Patients"), PeriodReport.Count(s.PatientCount, culture)),
            new("tests", text("Statistics.Tests"), PeriodReport.Count(s.TestCount, culture),
                s.CancelledTestCount > 0 ? $"{text("Statistics.Cancelled")} {PeriodReport.Count(s.CancelledTestCount, culture)}" : null),
            new("turnaround", caption, PeriodReport.Hours(s.AverageTurnaroundHours, text, culture),
                after is null ? onTime : $"{onTime} {after}"),
            new("charged", text("Statistics.Charged"), amount(s.ChargedAmount)),
            new("invoiced", text("Statistics.Invoiced"), amount(s.InvoicedAmount)),
            new("credited", text("Statistics.Credited"), amount(s.CreditedAmount)),
            new("collected", text("Statistics.Collected"), amount(s.CollectedAmount))
        ];
    }

    /// <summary>The period computed, beside the title: « 01/10/2026 → 26/10/2026 »; nothing before the first computation.</summary>
    public static string Subtitle(ActivityStatistics? result) =>
        result is null ? string.Empty : PeriodReport.Period(result.From, result.To);

    /// <summary>The table as CSV, its captions first, or nothing when it is empty (« Aucune donnée »).</summary>
    public static byte[]? Csv(ActivityStatistics? result, StatisticsTab tab, AppLanguage language, ScreenText text,
                              Func<decimal, string> amount, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        return PeriodReport.Csv([.. Columns(tab).Select(c => text(c.CaptionKey))],
                                Rows(result, tab, language, text, amount, culture).Select(r => r.Cells));
    }

    /// <summary>« statistiques-par-secteur-20261001-20261026.csv ».</summary>
    public static string FileName(ActivityStatistics result, StatisticsTab tab)
    {
        ArgumentNullException.ThrowIfNull(result);
        var what = tab switch
        {
            StatisticsTab.ByTest => "par-analyse",
            StatisticsTab.ByPayer => "par-payeur",
            StatisticsTab.ByDay => "par-jour",
            _ => "par-secteur"
        };
        return $"statistiques-{what}-{PeriodReport.FileStamp(result.From, result.To)}.csv";
    }

    /// <summary>The box a refusal of StatisticsService names: the period, reversed or too long.</summary>
    public static string? FieldOf(Error? error) => PeriodReport.IsPeriodRefusal(error) ? PeriodField : null;
}
