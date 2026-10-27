using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Inventory;

/// <summary>The settings of the stock alerts as typed: the variance threshold, the window, the two expiry levels.</summary>
public sealed class StockSettingsForm
{
    public decimal VarianceThresholdPercent { get; set; }
    public int ConsumptionWindowDays { get; set; }
    public int ExpiryWarningDays { get; set; }
    public int ExpiryCriticalDays { get; set; }
}

/// <summary>One line of a consumption breakdown: an origin, its tests, and what they are in the article's unit.</summary>
public sealed record ConsumptionDetailLine(string Label, string Tests, string Units, bool Total);

/// <summary>
/// The rules of « Consommation » that need no window: the period opened on, the columns, how a line
/// reads (flagged beyond the threshold, muted when the activity cannot explain it), the breakdown of
/// a line, the report and its export, and how the stock settings are typed and refused box by box.
/// The workstation's ConsumptionView and its dialogs and the web's screen apply the same ones; the
/// figures are StockPlanningService's.
/// </summary>
public static class ConsumptionScreenRules
{
    public const string ThresholdField = "stock-threshold";
    public const string WindowField = "stock-window";
    public const string WarningField = "stock-warning";
    public const string CriticalField = "stock-critical";

    public const decimal ThresholdMax = 100m;
    public const int WindowMin = 7;
    public const int WindowMax = 730;
    public const int ExpiryMax = 365;

    /// <summary>The settings are the administrator's; the rest takes the right to see the stock.</summary>
    public static bool MayEditSettings(Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(has);
        return has(Permissions.Administration.ManageSettings);
    }

    /// <summary>The month before this one: the period a consumption is reviewed for.</summary>
    public static (DateOnly From, DateOnly To) DefaultPeriod(DateOnly today)
    {
        var first = new DateOnly(today.Year, today.Month, 1);
        return (first.AddMonths(-1), first.AddDays(-1));
    }

    /// <summary>The columns of the screen, the PDF and the CSV — the same, in the screen's language.</summary>
    public static IReadOnlyList<string> Headers(ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            text("Field.Code"), text("Inventory.Item"), text("Field.Unit"), text("Consumption.Opening"), text("Consumption.Receipts"),
            text("Consumption.Tests"), text("Consumption.Theoretical"), text("Consumption.Real"),
            text("Consumption.TheoreticalClosing"), text("Consumption.RealClosing"), text("Consumption.Variance"),
            text("Consumption.VariancePercent"), text("Consumption.Count")
        ];
    }

    /// <summary>
    /// A line whose variance goes beyond the threshold in the alarm's colours: reagent went that no
    /// result accounts for, or results were produced with reagent nobody recorded. A line the
    /// activity cannot explain (no tests per pack, no analysis) muted.
    /// </summary>
    public static StockInk Ink(ConsumptionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Reconciliation.IsFlagged ? StockInk.Critical
            : !row.Reconciliation.Computable ? StockInk.Muted
            : StockInk.Plain;
    }

    public static string Count(IReadOnlyCollection<ConsumptionRow> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        return text("StocksUi.Items", rows.Count);
    }

    /// <summary>The foot of the list: how many lines are flagged, else what the screen compares.</summary>
    public static (string Text, bool Critical) Notice(IReadOnlyCollection<ConsumptionRow> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        var flagged = rows.Count(r => r.Reconciliation.IsFlagged);
        return flagged > 0 ? (text("Consumption.Flagged", flagged), true) : (text("Consumption.Notice"), false);
    }

    /// <summary>« 01/09/2026 – 30/09/2026 ».</summary>
    public static string Period(DateOnly from, DateOnly to) =>
        $"{from.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} – {to.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}";

    /// <summary>A period that ends before it starts is refused before anything is computed.</summary>
    public static bool IsPeriodValid(DateOnly from, DateOnly to) => from <= to;

    public static string ReportTitle(DateOnly from, DateOnly to, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text("Consumption.ReportTitle", Period(from, to));
    }

    public static string ReportCaption(DateTimeOffset now, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return $"{text("Consumption.Notice")} — {now.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}";
    }

    /// <summary>The name the report and its export are handed over as.</summary>
    public static string FileName(DateOnly from, DateOnly to, string extension) =>
        $"consommation-{from:yyyyMMdd}-{to:yyyyMMdd}.{extension}";

    // ---- The breakdown of a line ---------------------------------------------------------------------

    public static string DetailTitle(ConsumptionRow row, DateOnly from, DateOnly to, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return text("Consumption.DetailTitle", row.Code, Period(from, to));
    }

    /// <summary>
    /// Where the theoretical consumption comes from — patients, QC, calibrations, reruns, the dead
    /// volume of each opening — then the theoretical, the real and the variance.
    /// </summary>
    public static IReadOnlyList<ConsumptionDetailLine> DetailLines(ConsumptionRow row, ScreenText text, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        var culture = provider ?? CultureInfo.CurrentCulture;
        string N(decimal v) => v.ToString("0.###", culture);

        var b = row.Breakdown;
        var r = row.Reconciliation;
        string Units(decimal tests) => N(Math.Round(tests / b.TestsPerUnit, 3));

        return
        [
            new(text("Consumption.Patients"), N(b.PatientTests), Units(b.PatientTests), false),
            new(text("Consumption.Qc"), N(b.QcTests), Units(b.QcTests), false),
            new(text("Consumption.Calibrations"), N(b.CalibrationTests), Units(b.CalibrationTests), false),
            new(text("Consumption.Reruns"), N(b.RerunTests), Units(b.RerunTests), false),
            new(text("Consumption.DeadVolume", N(b.Openings), b.Openings), N(b.DeadVolumeTests), Units(b.DeadVolumeTests), false),
            new(text("Consumption.Theoretical"), N(b.TotalTests), N(r.TheoreticalConsumption), true),
            new(text("Consumption.Real"), "—", N(r.RealConsumption), true),
            new(text("Consumption.Variance"), "—",
                r.VariancePercent is { } p ? $"{N(r.Variance)} ({p.ToString("0.#", culture)} %)" : N(r.Variance), true)
        ];
    }

    public static string DetailNoticeKey(ConsumptionRow row) =>
        row.Reconciliation.Computable ? "Consumption.DetailNotice" : "Consumption.NotComputable";

    // ---- The settings --------------------------------------------------------------------------------

    public static StockSettingsForm Form(StockSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return new StockSettingsForm
        {
            VarianceThresholdPercent = current.VarianceThresholdPercent,
            ConsumptionWindowDays = current.ConsumptionWindowDays,
            ExpiryWarningDays = current.ExpiryWarningDays,
            ExpiryCriticalDays = current.ExpiryCriticalDays
        };
    }

    /// <summary>
    /// The settings refused on the box out of range — the threshold 0 to 100 %, the window 7 to 730
    /// days, the first expiry level 1 to 365 days, the second before the first — with the
    /// workstation's message, as StockSettings.Validate refuses them.
    /// </summary>
    public static FieldRefusal? Check(StockSettingsForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        const string key = "Stock_SettingOutOfRange";
        if (form.VarianceThresholdPercent is < 0m or > ThresholdMax) return new FieldRefusal(ThresholdField, key);
        if (form.ConsumptionWindowDays is < WindowMin or > WindowMax) return new FieldRefusal(WindowField, key);
        if (form.ExpiryWarningDays is < 1 or > ExpiryMax) return new FieldRefusal(WarningField, key);
        if (form.ExpiryCriticalDays < 0 || form.ExpiryCriticalDays >= form.ExpiryWarningDays) return new FieldRefusal(CriticalField, key);
        return null;
    }

    public static StockSettings ToSettings(StockSettings current, StockSettingsForm form)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(form);
        return current with
        {
            VarianceThresholdPercent = form.VarianceThresholdPercent,
            ConsumptionWindowDays = form.ConsumptionWindowDays,
            ExpiryWarningDays = form.ExpiryWarningDays,
            ExpiryCriticalDays = form.ExpiryCriticalDays
        };
    }

    /// <summary>The box a refusal of the settings names: the service says only that one is out of range.</summary>
    public static string? FieldOf(Error? error, StockSettingsForm form) =>
        error?.ResourceKey == "Stock_SettingOutOfRange" ? Check(form)?.Field ?? ThresholdField : null;
}
