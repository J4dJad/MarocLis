using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Steering;

/// <summary>
/// What « Coût de revient et marge » offers: computing and exporting to anyone who may see the
/// steering figures; the allocation of indirect costs only with the right to manage the costs —
/// the workstation shows that button to no one else.
/// </summary>
public sealed record CostMarginActions(bool Compute, bool Export, bool Allocation);

/// <summary>The three tables of the screen.</summary>
public enum MarginTab
{
    ByAnalysis,
    BySite,
    ByInstrument
}

/// <summary>A key figure above the tables: its caption's key, the figure, and its ink.</summary>
public sealed record MarginTile(string CaptionKey, string Value, StockInk Ink);

/// <summary>
/// The rules of « Coût de revient et marge » that need no window: what is offered by which right,
/// the period each chip gives, the four key figures, the twelve columns of each table written as
/// the screen shows them, a group sold at a loss in red and tests without a known cost in orange,
/// the table's CSV, and the allocation of indirect costs typed and refused in its box. The
/// workstation's CostMarginView and the web's screen apply the same ones; the computation is
/// CostMarginService's.
/// </summary>
public static class CostMarginScreenRules
{
    public const string PeriodField = "margin-period";
    public const string AllocationField = "margin-allocation";

    /// <summary>The columns of every table, in order: their captions are « Steering.Column.{name} ».</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "code", "name", "tests", "revenue", "averagePrice", "reagentCost", "allocation", "costPerTest", "marginPerTest", "margin",
        "marginPercent", "withoutCost"
    ];

    /// <summary>The columns holding a figure, right-aligned.</summary>
    public static bool IsNumeric(string column) => column is not ("code" or "name");

    public static string ColumnKey(string column) => $"Steering.Column.{column}";

    public static CostMarginActions Actions(Func<string, bool> has, MarginAnalysis? result)
    {
        ArgumentNullException.ThrowIfNull(has);
        var view = has(Permissions.Steering.View);
        return new CostMarginActions(view, view && result is not null, has(Permissions.Steering.ManageCosts));
    }

    public static string PresetKey(PeriodPreset preset) => PeriodReport.PresetKey(preset);

    /// <summary>The days a chip gives (<see cref="PeriodReport.Range"/>).</summary>
    public static (DateOnly From, DateOnly To)? Range(PeriodPreset preset, DateOnly today) => PeriodReport.Range(preset, today);

    public static string TabKey(MarginTab tab) => $"Steering.{tab}";

    public static IReadOnlyList<MarginRow> Rows(MarginAnalysis? result, MarginTab tab) => result is null
        ? []
        : tab switch
        {
            MarginTab.BySite => result.BySite,
            MarginTab.ByInstrument => result.ByInstrument,
            _ => result.ByAnalysis
        };

    public static string Count(int value, IFormatProvider culture) => PeriodReport.Count(value, culture);

    /// <summary>« 42,5 % », or a dash where nothing was charged.</summary>
    public static string Share(double? value, IFormatProvider culture) => PeriodReport.Share(value, culture);

    public static string Date(DateOnly date) => PeriodReport.Date(date);

    /// <summary>« Coût de revient et marge — 01/10/2026 → 26/10/2026 ».</summary>
    public static string Heading(MarginAnalysis? result, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return result is null
            ? text("Steering.CostMarginTitle")
            : $"{text("Steering.CostMarginTitle")} — {Date(result.From)} → {Date(result.To)}";
    }

    /// <summary>
    /// The four key figures: the revenue and the tests, the reagent cost (in orange while some tests
    /// have no known cost), the allocation and its rate per test, the margin and its share.
    /// </summary>
    public static IReadOnlyList<MarginTile> Tiles(MarginAnalysis result, ScreenText text, Func<decimal, string> amount,
                                                  IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(culture);
        var t = result.Totals;
        return
        [
            new(ColumnKey("revenue"), $"{amount(t.Revenue)} · {Count(t.Tests, culture)} {text("Steering.TestsUnit")}", StockInk.Plain),
            new(ColumnKey("reagentCost"), amount(t.ReagentCost), t.TestsWithoutCost > 0 ? StockInk.Abnormal : StockInk.Plain),
            new(ColumnKey("allocation"), $"{amount(t.Allocation)} ({amount(result.AllocationPerTest)} {text("Steering.PerTest")})", StockInk.Plain),
            new(ColumnKey("margin"), $"{amount(t.Margin)} · {Share(t.MarginPercent, culture)}", t.Margin < 0m ? StockInk.Critical : StockInk.Plain)
        ];
    }

    /// <summary>A group's cells in the columns' order, as the table shows them and the CSV writes them.</summary>
    public static IReadOnlyList<string> Cells(MarginRow row, Func<decimal, string> amount, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(culture);
        return
        [
            row.Code, row.Name, Count(row.Tests, culture), amount(row.Revenue), amount(row.AverageNetPrice), amount(row.ReagentCost),
            amount(row.Allocation), amount(row.CostPerTest), amount(row.MarginPerTest), amount(row.Margin),
            Share(row.MarginPercent, culture), row.TestsWithoutCost == 0 ? string.Empty : Count(row.TestsWithoutCost, culture)
        ];
    }

    /// <summary>A group sold at a loss: the whole row in red.</summary>
    public static bool IsLoss(MarginRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Margin < 0m;
    }

    /// <summary>Tests without a known cost, on a group that is not at a loss: that cell in orange.</summary>
    public static bool WarnsWithoutCost(MarginRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !IsLoss(row) && row.TestsWithoutCost > 0;
    }

    /// <summary>
    /// The table as CSV — semicolons, a BOM, every cell as shown and neutralised against spreadsheet
    /// formulas — or nothing when the table is empty (« Aucune donnée »).
    /// </summary>
    public static byte[]? Csv(IReadOnlyList<MarginRow> rows, ScreenText text, Func<decimal, string> amount, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        return PeriodReport.Csv([.. Columns.Select(c => text(ColumnKey(c)))], rows.Select(row => Cells(row, amount, culture)));
    }

    /// <summary>« marge-par-analyse-20261001-20261026.csv ».</summary>
    public static string FileName(MarginAnalysis result, MarginTab tab)
    {
        ArgumentNullException.ThrowIfNull(result);
        var what = tab switch { MarginTab.BySite => "par-site", MarginTab.ByInstrument => "par-automate", _ => "par-analyse" };
        return $"marge-{what}-{PeriodReport.FileStamp(result.From, result.To)}.csv";
    }

    /// <summary>The box a refusal of CostMarginService names: the period, or the allocation.</summary>
    public static string? FieldOf(Error? error) =>
        PeriodReport.IsPeriodRefusal(error) ? PeriodField
        : error?.ResourceKey == "Steering_AllocationOutOfRange" ? AllocationField
        : null;

    // ---- The allocation of indirect costs --------------------------------------------------------

    /// <summary>The allocation as its box shows it, in the session's culture without thousands separator.</summary>
    public static string AllocationText(CostSettings settings, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return TypedNumber.ForEditing(settings.AllocationPerTestMad, 2, culture ?? CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The allocation as CostMarginService.SaveSettingsAsync takes it, or why its box is refused: a
    /// number, between nothing and the most the screen accepts per test.
    /// </summary>
    public static (FieldRefusal? Refusal, CostSettings? Settings) Check(string? typed)
    {
        if (TypedNumber.IsUnreadable(typed) || TypedNumber.Parse(typed) is not { } value)
            return (new FieldRefusal(AllocationField, "Result_NotANumber", typed?.Trim() ?? string.Empty), null);
        if (value is < 0m or > CostSettings.MaximumAllocationPerTestMad)
            return (new FieldRefusal(AllocationField, "Steering_AllocationOutOfRange"), null);
        return (null, new CostSettings { AllocationPerTestMad = Money.Round(value) });
    }
}
