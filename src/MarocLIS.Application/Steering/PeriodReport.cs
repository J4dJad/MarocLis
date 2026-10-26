using System.Globalization;
using System.Text;
using MarocLIS.Application.Input;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Steering;

/// <summary>The period chips of the steering and relations screens.</summary>
public enum PeriodPreset
{
    ThisMonth,
    LastMonth,
    ThisYear,
    LastYear,
    Custom
}

/// <summary>A column of a period report's table: its name, its caption's key, and whether it holds a figure.</summary>
public sealed record ReportColumn(string Name, string CaptionKey, bool Numeric = false);

/// <summary>A row of a period report's table: its cells in the columns' order, and its ink (a band overdue in red).</summary>
public sealed record ReportRow(IReadOnlyList<string> Cells, StockInk Ink = StockInk.Plain);

/// <summary>
/// A key figure above a period report's tables: its key, its caption (translated), the figure, the
/// smaller line under it (none when absent), and its ink.
/// </summary>
public sealed record ReportTile(string Key, string Caption, string Value, string? Sub = null, StockInk Ink = StockInk.Plain)
{
    /// <summary>
    /// The workstation's tile text: the caption, then the figure, and after « · » the line under it —
    /// the form its figure tile splits and draws.
    /// </summary>
    public string TileText => Sub is { Length: > 0 } sub ? $"{Caption}\n{Value} · {sub}" : $"{Caption}\n{Value}";
}

/// <summary>
/// What the steering and relations screens share: the period chips and the days they give, the
/// period written beside the title, the figures written as these screens write them (counts,
/// shares, changes, hours), and a table written as CSV. The workstation's PeriodBar and ReportGrids
/// and the web's screens apply the same ones.
/// </summary>
public static class PeriodReport
{
    /// <summary>« 12,5 h »: an average delay in hours, the unit in the reader's language.</summary>
    public const string HoursKey = "Statistics.HoursValue";

    public static string PresetKey(PeriodPreset preset) => $"Statistics.Preset{preset}";

    /// <summary>The days a chip gives: this month or last, this year or last, up to today; none for « Personnalisée ».</summary>
    public static (DateOnly From, DateOnly To)? Range(PeriodPreset preset, DateOnly today)
    {
        var month = new DateOnly(today.Year, today.Month, 1);
        return preset switch
        {
            PeriodPreset.ThisMonth => (month, today),
            PeriodPreset.LastMonth => (month.AddMonths(-1), month.AddDays(-1)),
            PeriodPreset.ThisYear => (new DateOnly(today.Year, 1, 1), today),
            PeriodPreset.LastYear => (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31)),
            _ => null
        };
    }

    public static string Count(int value, IFormatProvider culture) => value.ToString("N0", culture);

    /// <summary>« 42,5 % », or a dash where there is nothing to divide.</summary>
    public static string Share(double? value, IFormatProvider culture) =>
        value is { } p ? string.Format(culture, "{0:0.0} %", p) : "—";

    /// <summary>« +4,2 % » or « −3,0 % »: a change against the previous period, or a dash.</summary>
    public static string Change(double? value, IFormatProvider culture) =>
        value is { } p ? string.Format(culture, "{0:+0.0;-0.0;0.0} %", p) : "—";

    /// <summary>« 12,5 h », or a dash where nothing was timed.</summary>
    public static string Hours(double? value, ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        return value is { } h ? text(HoursKey, h.ToString("0.0", culture)) : "—";
    }

    public static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>« 01/10/2026 → 26/10/2026 ».</summary>
    public static string Period(DateOnly from, DateOnly to) => $"{Date(from)} → {Date(to)}";

    /// <summary>« 20261001-20261026 », the period in a file's name.</summary>
    public static string FileStamp(DateOnly from, DateOnly to) =>
        $"{from.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{to.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

    /// <summary>A refusal of the period by the statistics and steering services: reversed, or too long.</summary>
    public static bool IsPeriodRefusal(Error? error) =>
        error?.ResourceKey is "Stats_PeriodReversed" or "Stats_PeriodTooLong";

    /// <summary>
    /// A table as CSV — semicolons, a BOM, every cell as shown and neutralised against spreadsheet
    /// formulas: what a French-locale Excel opens directly into columns — or nothing when the table is
    /// empty (« Aucune donnée »).
    /// </summary>
    public static byte[]? Csv(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(';', headers.Select(CsvCell.Of)));
        var any = false;
        foreach (var row in rows)
        {
            any = true;
            csv.AppendLine(string.Join(';', row.Select(CsvCell.Of)));
        }

        return any ? [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())] : null;
    }
}
