using MarocLIS.Application.Input;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Steering;

/// <summary>
/// What « Pilotage » offers: computing to anyone who may see the steering figures, exporting once a
/// period has been computed. The payroll figure needs the salary right as well; SteeringService
/// leaves it out for anyone else, and the screen then shows no tile for it.
/// </summary>
public sealed record SteeringActions(bool Compute, bool Export);

/// <summary>The six tables of the screen.</summary>
public enum SteeringTab
{
    ByDay,
    BySite,
    ByPayer,
    ByPrescriber,
    Receivables,
    Quality
}

/// <summary>
/// The rules of « Pilotage » that need no window: what is offered by which right, the key figures
/// (revenue, invoiced and collected, reagent cost, margin, receivables overdue in orange, payroll
/// against revenue for whom may see salaries, turnaround and QC rejections), the revenue by day,
/// site, payer and prescriber, the receivables by age (a band past 60 days still owed in orange) and
/// the quality indicators as the screen writes them, the table's CSV and its file's name, and the
/// period refused beside its dates. The workstation's SteeringView and the web's screen apply the
/// same ones; the computation is SteeringService's.
/// </summary>
public static class SteeringScreenRules
{
    public const string PeriodField = "steering-period";

    public static SteeringActions Actions(Func<string, bool> has, SteeringDashboard? result)
    {
        ArgumentNullException.ThrowIfNull(has);
        var view = has(Permissions.Steering.View);
        return new SteeringActions(view, view && result is not null);
    }

    public static string TabKey(SteeringTab tab) => $"Steering.{tab}";

    public static string ColumnKey(string column) => $"Steering.Column.{column}";

    /// <summary>The columns of a table, in order: their captions are « Steering.Column.{name} ».</summary>
    public static IReadOnlyList<ReportColumn> Columns(SteeringTab tab) => tab switch
    {
        SteeringTab.Receivables => [Column("band"), Column("invoices", true), Column("owed", true)],
        SteeringTab.Quality => [Column("indicator"), Column("value", true)],
        _ => [Column("label"), Column("orders", true), Column("tests", true), Column("revenue", true), Column("share", true)]
    };

    private static ReportColumn Column(string name, bool numeric = false) => new(name, ColumnKey(name), numeric);

    /// <summary>
    /// A table's rows, their cells in the columns' order as the screen shows them and the CSV writes
    /// them: a group without a name as « (Non renseigné) », the self-paying as « Patient payant », a
    /// file without prescriber as « (Sans prescripteur) »; a band of receivables past 60 days still
    /// owed in orange; the quality indicators one per row.
    /// </summary>
    public static IReadOnlyList<ReportRow> Rows(SteeringDashboard? result, SteeringTab tab, ScreenText text,
                                                Func<decimal, string> amount, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(culture);
        if (result is null) return [];

        string Count(int value) => PeriodReport.Count(value, culture);
        string Share(double? value) => PeriodReport.Share(value, culture);

        IReadOnlyList<ReportRow> Revenue(IEnumerable<RevenueRow> rows, string emptyKey) =>
        [
            .. rows.Select(r => new ReportRow(
                [r.Label.Length == 0 ? text(emptyKey) : r.Label, Count(r.Orders), Count(r.Tests), amount(r.Revenue), Share(r.SharePercent)]))
        ];

        switch (tab)
        {
            case SteeringTab.BySite: return Revenue(result.BySite, "Steering.NoLabel");
            case SteeringTab.ByPayer: return Revenue(result.ByPayer, "Statistics.SelfPaying");
            case SteeringTab.ByPrescriber: return Revenue(result.ByPrescriber, "Relations.NoPrescriber");
            case SteeringTab.Receivables:
                return
                [
                    .. result.Ageing.Select(a => new ReportRow(
                        [text($"AgeingBand.{a.Band}"), Count(a.Invoices), amount(a.Amount)],
                        IsLate(a) ? StockInk.Abnormal : StockInk.Plain))
                ];
            case SteeringTab.Quality:
            {
                var q = result.Quality;
                return
                [
                    new([text("Steering.QcResults"), Count(q.QcResults)]),
                    new([text("Steering.QcRejections"), $"{Count(q.QcRejected)} ({Share(q.QcRejectedPercent)})"]),
                    new([text("Steering.OpenNonConformities"), Count(q.OpenNonConformities)]),
                    new([text("Steering.NonConformitiesOpened"), Count(q.NonConformitiesOpened)]),
                    new([text("Steering.EqaSatisfactory"), Count(q.EqaSatisfactory)]),
                    new([text("Steering.EqaQuestionable"), Count(q.EqaQuestionable)]),
                    new([text("Steering.EqaUnsatisfactory"), Count(q.EqaUnsatisfactory)]),
                    new([text("Steering.EqaNotSubmitted"), Count(q.EqaNotSubmitted)]),
                    new([text("Steering.Turnaround"), PeriodReport.Hours(result.AverageTurnaroundHours, text, culture)]),
                    new([text("Statistics.OnTime"), Share(result.OnTimePercent)])
                ];
            }
            default: return Revenue(result.ByDay, "Steering.NoLabel");
        }
    }

    /// <summary>A band of receivables past 60 days that is still owed: in orange, to be chased.</summary>
    public static bool IsLate(AgeingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Band >= AgeingBand.Days61To90 && row.Amount > 0m;
    }

    /// <summary>
    /// The key figures: the revenue and the files; invoiced net of credit notes against collected;
    /// the reagent cost; the margin and its share; the receivables overdue against outstanding, in
    /// orange while some are overdue; the payroll against the revenue, only when SteeringService
    /// computed it (the salary right) — not shown at all otherwise, not shown empty; the turnaround
    /// and the share on time; the QC rejections.
    /// </summary>
    public static IReadOnlyList<ReportTile> Tiles(SteeringDashboard result, ScreenText text, Func<decimal, string> amount,
                                                  IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(culture);
        var d = result;

        var tiles = new List<ReportTile>
        {
            new("revenue", text("Steering.Revenue"), amount(d.Revenue),
                $"{PeriodReport.Count(d.Orders, culture)} {text("Steering.OrdersUnit")}"),
            new("invoiced", text("Steering.InvoicedCollected"), $"{amount(d.Invoiced - d.Credited)} / {amount(d.Collected)}"),
            new("reagentCost", text(ColumnKey("reagentCost")), amount(d.Margin.ReagentCost)),
            new("margin", text(ColumnKey("margin")), amount(d.Margin.Margin), PeriodReport.Share(d.Margin.MarginPercent, culture)),
            new("overdue", text("Steering.OverdueReceivables"), $"{amount(d.Overdue)} / {amount(d.Outstanding)}", null,
                d.Overdue > 0m ? StockInk.Abnormal : StockInk.Plain)
        };

        if (d.Payroll is { } p)
            tiles.Add(p.SiteMonths == 0
                ? new("payroll", text("Steering.PayrollRatio"), text("Steering.NoClosedPayroll"))
                : new("payroll", text("Steering.PayrollRatio"), amount(p.EmployerCost),
                      PeriodReport.Share(p.EmployerCostToRevenuePercent, culture)));

        tiles.Add(new("turnaround", text("Steering.Turnaround"), PeriodReport.Hours(d.AverageTurnaroundHours, text, culture),
                      PeriodReport.Share(d.OnTimePercent, culture)));
        tiles.Add(new("qc", text("Steering.QcRejections"),
                      $"{PeriodReport.Count(d.Quality.QcRejected, culture)} / {PeriodReport.Count(d.Quality.QcResults, culture)}",
                      PeriodReport.Share(d.Quality.QcRejectedPercent, culture)));
        return tiles;
    }

    /// <summary>« 01/10/2026 → 26/10/2026 · 2 sites » beside the title; nothing before the first computation.</summary>
    public static string Subtitle(SteeringDashboard? result, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return result is null
            ? string.Empty
            : $"{PeriodReport.Period(result.From, result.To)} · {text("Steering.Sites", result.SiteCount)}";
    }

    /// <summary>The table as CSV, its captions first, or nothing when it is empty (« Aucune donnée »).</summary>
    public static byte[]? Csv(SteeringDashboard? result, SteeringTab tab, ScreenText text, Func<decimal, string> amount,
                              IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        return PeriodReport.Csv([.. Columns(tab).Select(c => text(c.CaptionKey))],
                                Rows(result, tab, text, amount, culture).Select(r => r.Cells));
    }

    /// <summary>« pilotage-par-jour-20261001-20261026.csv ».</summary>
    public static string FileName(SteeringDashboard result, SteeringTab tab)
    {
        ArgumentNullException.ThrowIfNull(result);
        var what = tab switch
        {
            SteeringTab.BySite => "par-site",
            SteeringTab.ByPayer => "par-payeur",
            SteeringTab.ByPrescriber => "par-prescripteur",
            SteeringTab.Receivables => "creances",
            SteeringTab.Quality => "qualite",
            _ => "par-jour"
        };
        return $"pilotage-{what}-{PeriodReport.FileStamp(result.From, result.To)}.csv";
    }

    /// <summary>The box a refusal of SteeringService names: the period, reversed or too long.</summary>
    public static string? FieldOf(Error? error) => PeriodReport.IsPeriodRefusal(error) ? PeriodField : null;
}
