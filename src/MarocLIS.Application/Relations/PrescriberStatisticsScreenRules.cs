using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Application.Steering;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Relations;

/// <summary>
/// What « Statistiques prescripteurs » offers: computing to anyone who may see the relations (or the
/// steering figures, which the service also accepts), exporting a table and opening a row's detail
/// once a period has been computed.
/// </summary>
public sealed record PrescriberStatisticsActions(bool Compute, bool Export, bool Detail);

/// <summary>The six tables of the screen.</summary>
public enum PrescriberStatisticsTab
{
    ByPrescriber,
    ByInstitution,
    ByMonth,
    NewPrescribers,
    LostPrescribers,
    MonthlyDetail
}

/// <summary>
/// The detail of a row of the tables: a prescriber's months, an institution's prescribers, or the
/// prescribers of a month — its title, its columns and its rows, as the CSV writes them too.
/// </summary>
public sealed record PrescriberDrill(string Title, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<ReportRow> Rows);

/// <summary>
/// The rules of « Statistiques prescripteurs » that need no window: what is offered by which right,
/// the four key figures (files and revenue against the period before, prescribers gained and lost),
/// the columns and cells of the six tables as the screen writes them (a prescriber who started
/// sending marked as new, a file without prescriber as « (Sans prescripteur) », a prescriber in
/// private practice under « (Cabinet / sans établissement) »), the detail of a row, the table's CSV
/// and its file's name, and the period refused beside its dates. The workstation's
/// PrescriberStatisticsView and the web's screen apply the same ones; the computation is
/// PrescriberStatisticsService's.
/// </summary>
public static class PrescriberStatisticsScreenRules
{
    public const string PeriodField = "prescriberstats-period";

    public static PrescriberStatisticsActions Actions(Func<string, bool> has, PrescriberStatistics? result)
    {
        ArgumentNullException.ThrowIfNull(has);
        var view = has(Permissions.Relations.View) || has(Permissions.Steering.View);
        var computed = view && result is not null;
        return new PrescriberStatisticsActions(view, computed, computed);
    }

    public static string TabKey(PrescriberStatisticsTab tab) => tab switch
    {
        PrescriberStatisticsTab.ByInstitution => "Relations.ByInstitution",
        PrescriberStatisticsTab.ByMonth => "Relations.ByMonth",
        PrescriberStatisticsTab.NewPrescribers => "Relations.NewPrescribers",
        PrescriberStatisticsTab.LostPrescribers => "Relations.LostPrescribers",
        PrescriberStatisticsTab.MonthlyDetail => "Relations.MonthlyDetail",
        _ => "Relations.ByPrescriber"
    };

    public static string ColumnKey(string column) => $"Relations.Column.{column}";

    private static ReportColumn Column(string name, bool numeric = false) => new(name, ColumnKey(name), numeric);

    private static IEnumerable<ReportColumn> VolumeColumns() =>
    [
        Column("orders", true), Column("tests", true), Column("revenue", true), Column("prevOrders", true),
        Column("prevRevenue", true), Column("ordersVsPrevious", true), Column("ordersVsLastYear", true),
        Column("revenueVsPrevious", true), Column("revenueVsLastYear", true)
    ];

    private static IReadOnlyList<ReportColumn> DetailColumns { get; } =
    [
        Column("name"), Column("institution"), Column("month"), Column("orders", true), Column("tests", true), Column("revenue", true)
    ];

    /// <summary>The columns of a table, in order: their captions are « Relations.Column.{name} ».</summary>
    public static IReadOnlyList<ReportColumn> Columns(PrescriberStatisticsTab tab) => tab switch
    {
        PrescriberStatisticsTab.ByInstitution => [Column("institution"), .. VolumeColumns()],
        PrescriberStatisticsTab.ByMonth =>
        [
            Column("month"), Column("orders", true), Column("tests", true), Column("revenue", true), Column("lyOrders", true),
            Column("lyRevenue", true), Column("vsLastYear", true), Column("active", true), Column("newOnes", true)
        ],
        PrescriberStatisticsTab.MonthlyDetail => DetailColumns,
        _ => [Column("name"), Column("institution"), .. VolumeColumns()]
    };

    /// <summary>The columns read as identifiers, in the fixed-pitch face: the months.</summary>
    public static bool IsIdentifier(string column) => column is "month";

    /// <summary>« 10/2026 »: a month as the tables write it, its figures read left to right in every language.</summary>
    public static string Month(DateOnly month) => month.ToString("MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>A prescriber's name, or « (Sans prescripteur) » for the files that named none.</summary>
    public static string NameOf(Guid? prescriberId, string? name, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return prescriberId is null ? text("Relations.NoPrescriber") : name ?? "?";
    }

    /// <summary>
    /// The rows of the table <paramref name="tab"/> that are prescribers who started sending in the
    /// period (and none in the year before): the workstation draws them on the green of a normal
    /// result.
    /// </summary>
    public static bool IsNew(PrescriberStatistics? result, PrescriberStatisticsTab tab, int index) =>
        Volumes(result, tab) is { } rows && index >= 0 && index < rows.Count && rows[index].IsNew;

    private static IReadOnlyList<PrescriberVolumeRow>? Volumes(PrescriberStatistics? result, PrescriberStatisticsTab tab) =>
        result is null
            ? null
            : tab switch
            {
                PrescriberStatisticsTab.ByPrescriber => result.Prescribers,
                PrescriberStatisticsTab.ByInstitution => result.Institutions,
                PrescriberStatisticsTab.NewPrescribers => result.NewPrescribers,
                PrescriberStatisticsTab.LostPrescribers => result.LostPrescribers,
                _ => null
            };

    /// <summary>
    /// A table's rows, their cells in the columns' order as the screen shows them and the CSV writes
    /// them: counts, amounts as <paramref name="amount"/> writes them, changes signed in percent or a
    /// dash where there was nothing to compare with.
    /// </summary>
    public static IReadOnlyList<ReportRow> Rows(PrescriberStatistics? result, PrescriberStatisticsTab tab, ScreenText text,
                                                Func<decimal, string> amount, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(culture);
        if (result is null) return [];

        string Count(int value) => PeriodReport.Count(value, culture);
        string Change(double? value) => PeriodReport.Change(value, culture);

        IEnumerable<string> Figures(PrescriberVolumeRow r) =>
        [
            Count(r.Current.Orders), Count(r.Current.Tests), amount(r.Current.Revenue), Count(r.Previous.Orders),
            amount(r.Previous.Revenue), Change(r.OrdersVsPrevious), Change(r.OrdersVsLastYear),
            Change(r.RevenueVsPrevious), Change(r.RevenueVsLastYear)
        ];

        IReadOnlyList<ReportRow> ByName(IEnumerable<PrescriberVolumeRow> rows) =>
            [.. rows.Select(r => new ReportRow([NameOf(r.PrescriberId, r.Name, text), r.Institution ?? string.Empty, .. Figures(r)]))];

        return tab switch
        {
            PrescriberStatisticsTab.ByInstitution => [.. result.Institutions.Select(r =>
                new ReportRow([r.Institution ?? text("Relations.NoInstitution"), .. Figures(r)]))],
            PrescriberStatisticsTab.ByMonth => [.. result.Months.Select(m => new ReportRow(
                [Month(m.Month), Count(m.Current.Orders), Count(m.Current.Tests), amount(m.Current.Revenue),
                 Count(m.LastYear.Orders), amount(m.LastYear.Revenue), Change(m.OrdersVsLastYear),
                 Count(m.ActivePrescribers), Count(m.NewPrescribers)]))],
            PrescriberStatisticsTab.NewPrescribers => ByName(result.NewPrescribers),
            PrescriberStatisticsTab.LostPrescribers => ByName(result.LostPrescribers),
            PrescriberStatisticsTab.MonthlyDetail => DetailRows(result.Detail, text, amount, culture),
            _ => ByName(result.Prescribers)
        };
    }

    private static IReadOnlyList<ReportRow> DetailRows(IEnumerable<PrescriberMonthRow> rows, ScreenText text,
                                                       Func<decimal, string> amount, IFormatProvider culture) =>
    [
        .. rows.Select(d => new ReportRow(
            [NameOf(d.PrescriberId, d.Name, text), d.Institution ?? string.Empty, Month(d.Month),
             PeriodReport.Count(d.Figures.Orders, culture), PeriodReport.Count(d.Figures.Tests, culture), amount(d.Figures.Revenue)]))
    ];

    /// <summary>
    /// The detail of the row <paramref name="index"/> of a table, or nothing for the monthly detail
    /// itself: a prescriber's months (« Par prescripteur », new and lost prescribers), the
    /// prescribers of an institution with their months, the prescribers of a month.
    /// </summary>
    public static PrescriberDrill? Drill(PrescriberStatistics? result, PrescriberStatisticsTab tab, int index, ScreenText text,
                                         Func<decimal, string> amount, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(culture);
        if (result is null || index < 0) return null;

        var title = text(TabKey(tab));
        switch (tab)
        {
            case PrescriberStatisticsTab.ByMonth when index < result.Months.Count:
            {
                var month = result.Months[index].Month;
                return new PrescriberDrill($"{title} — {Month(month)}", DetailColumns,
                                           DetailRows(result.Detail.Where(d => d.Month == month), text, amount, culture));
            }
            case PrescriberStatisticsTab.ByInstitution when index < result.Institutions.Count:
            {
                var key = Institution(result.Institutions[index].Institution);
                var name = result.Institutions[index].Institution ?? text("Relations.NoInstitution");
                return new PrescriberDrill($"{title} — {name}", DetailColumns,
                                           DetailRows(result.Detail.Where(d => Institution(d.Institution) == key), text, amount, culture));
            }
            case PrescriberStatisticsTab.MonthlyDetail:
                return null;
            default:
            {
                if (Volumes(result, tab) is not { } rows || index >= rows.Count) return null;
                var row = rows[index];
                return new PrescriberDrill($"{title} — {NameOf(row.PrescriberId, row.Name, text)}", DetailColumns,
                                           DetailRows(result.Detail.Where(d => d.PrescriberId == row.PrescriberId), text, amount, culture));
            }
        }
    }

    // The service adds up institutions on their trimmed, upper-cased name; the detail is matched the same way.
    private static string Institution(string? name) => (name ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>
    /// The key figures: the files and the revenue with their change against the period before, the
    /// prescribers who started sending and those who stopped.
    /// </summary>
    public static IReadOnlyList<ReportTile> Tiles(PrescriberStatistics result, ScreenText text, Func<decimal, string> amount,
                                                  IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(culture);
        var s = result;

        return
        [
            new("orders", text(ColumnKey("orders")), PeriodReport.Count(s.Total.Orders, culture),
                PeriodReport.Change(PrescriberStatisticsService.Change(s.Total.Orders, s.TotalPrevious.Orders), culture)),
            new("revenue", text(ColumnKey("revenue")), amount(s.Total.Revenue),
                PeriodReport.Change(PrescriberStatisticsService.Change(s.Total.Revenue, s.TotalPrevious.Revenue), culture)),
            new("new", text("Relations.NewPrescribers"), PeriodReport.Count(s.NewPrescribers.Count, culture)),
            new("lost", text("Relations.LostPrescribers"), PeriodReport.Count(s.LostPrescribers.Count, culture))
        ];
    }

    /// <summary>« 01/10/2026 → 26/10/2026 (comparé à 01/09/2026 → 30/09/2026) »; nothing before the first computation.</summary>
    public static string Subtitle(PrescriberStatistics? result, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return result is null
            ? string.Empty
            : $"{PeriodReport.Period(result.From, result.To)} ({text("Relations.ComparedWith")} {PeriodReport.Period(result.PreviousFrom, result.PreviousTo)})";
    }

    /// <summary>The table as CSV, its captions first, or nothing when it is empty (« Aucune donnée »).</summary>
    public static byte[]? Csv(PrescriberStatistics? result, PrescriberStatisticsTab tab, ScreenText text,
                              Func<decimal, string> amount, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        return PeriodReport.Csv([.. Columns(tab).Select(c => text(c.CaptionKey))],
                                Rows(result, tab, text, amount, culture).Select(r => r.Cells));
    }

    /// <summary>A row's detail as CSV, or nothing when it is empty.</summary>
    public static byte[]? Csv(PrescriberDrill drill, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(drill);
        ArgumentNullException.ThrowIfNull(text);
        return PeriodReport.Csv([.. drill.Columns.Select(c => text(c.CaptionKey))], drill.Rows.Select(r => r.Cells));
    }

    /// <summary>« prescripteurs-par-prescripteur-20261001-20261026.csv »; a row's detail ends in « -detail ».</summary>
    public static string FileName(PrescriberStatistics result, PrescriberStatisticsTab tab, bool detail = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        var what = tab switch
        {
            PrescriberStatisticsTab.ByInstitution => "par-etablissement",
            PrescriberStatisticsTab.ByMonth => "par-mois",
            PrescriberStatisticsTab.NewPrescribers => "nouveaux",
            PrescriberStatisticsTab.LostPrescribers => "perdus",
            PrescriberStatisticsTab.MonthlyDetail => "detail-mensuel",
            _ => "par-prescripteur"
        };
        return $"prescripteurs-{what}-{PeriodReport.FileStamp(result.From, result.To)}{(detail ? "-detail" : string.Empty)}.csv";
    }

    /// <summary>The box a refusal of PrescriberStatisticsService names: the period, reversed or too long.</summary>
    public static string? FieldOf(Error? error) => PeriodReport.IsPeriodRefusal(error) ? PeriodField : null;
}
