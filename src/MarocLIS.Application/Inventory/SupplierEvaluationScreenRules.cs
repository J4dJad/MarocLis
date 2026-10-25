using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Inventory;

/// <summary>
/// What « Évaluation fournisseurs » offers: whoever manages suppliers evaluates them — the score
/// proposed for the supplier selected applied, the year printed or exported for the management
/// review. SupplierEvaluationService stays the judge.
/// </summary>
public sealed record SupplierEvaluationScreenActions(bool Apply, bool Pdf, bool Csv);

/// <summary>
/// The rules of « Évaluation fournisseurs » that need no window: the year evaluated, how a supplier
/// reads (a lapsed registration or a proposed score under 50 in the alarm's colours), its cells —
/// shared with the CSV and the PDF —, and how the score applied is typed and refused. The
/// workstation's SupplierEvaluationView and the web's screen apply the same ones.
/// </summary>
public static class SupplierEvaluationScreenRules
{
    public const int YearMin = 2000;
    public const int YearMax = 2100;
    public const int ScoreMaxLength = 3;

    /// <summary>Under this proposed score, a supplier is said in the alarm's colours.</summary>
    public const int PoorScore = 50;

    public const string ScoreField = "evaluation-score";

    public static SupplierEvaluationScreenActions Actions(ICurrentUser user, SupplierEvaluationRow? row)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Actions(user.Has, row);
    }

    public static SupplierEvaluationScreenActions Actions(Func<string, bool> has, SupplierEvaluationRow? row)
    {
        ArgumentNullException.ThrowIfNull(has);
        var manage = has(Permissions.Inventory.ManagePurchaseOrders);
        return new SupplierEvaluationScreenActions(
            Apply: manage && row?.Kpis.ProposedScore is not null,
            Pdf: manage,
            Csv: manage);
    }

    /// <summary>The year kept within the bounds the year box takes.</summary>
    public static int Year(int year) => Math.Clamp(year, YearMin, YearMax);

    /// <summary>The supplier just scored stays the current one: back on the first row after each score applied, the evaluator had to look the next one up again.</summary>
    public static SupplierEvaluationRow? Keep(IReadOnlyList<SupplierEvaluationRow> rows, Guid? kept)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.FirstOrDefault(r => r.SupplierId == kept) ?? (rows.Count > 0 ? rows[0] : null);
    }

    /// <summary>A lapsed AMMPS / DMP registration or a proposed score under 50: the alarm's colours on the row.</summary>
    public static StockInk Ink(SupplierEvaluationRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.RegistrationExpired || row.Kpis.ProposedScore is < PoorScore ? StockInk.Critical : StockInk.Plain;
    }

    public static IReadOnlyList<string> Headers(ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            text("Field.Code"), text("Inventory.Supplier"), text("SupplierEvaluation.Deliveries"),
            text("SupplierEvaluation.OnTime"), text("SupplierEvaluation.AverageDelay"),
            text("SupplierEvaluation.Refused"), text("SupplierEvaluation.NonConformities"),
            text("SupplierEvaluation.ProposedScore"), text("SupplierEvaluation.CurrentScore"),
            text("Inventory.RegulatoryRegistration"), text("Inventory.RegulatoryRegistrationValidUntil")
        ];
    }

    /// <summary>The cells of one supplier, the same in the list, the CSV and the PDF.</summary>
    public static IReadOnlyList<string> Cells(SupplierEvaluationRow row) => PurchasingCsv.SupplierReportCells(row);

    /// <summary>« 14 fournisseurs · 2026 ».</summary>
    public static string Count(IReadOnlyCollection<SupplierEvaluationRow> rows, int year, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        return text("StocksUi.Suppliers", rows.Count, year);
    }

    public static string ApplyPrompt(SupplierEvaluationRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return text("SupplierEvaluation.ApplyPrompt", row.Name, row.Kpis.ProposedScore ?? 0);
    }

    /// <summary>The score proposed, offered in the box.</summary>
    public static string Initial(SupplierEvaluationRow row) =>
        row.Kpis.ProposedScore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>The score typed: a whole number from 0 to 100, else refused with what was typed.</summary>
    public static (int? Score, FieldRefusal? Refusal) ReadScore(string? typed)
    {
        if (!int.TryParse(typed?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var score))
            return (null, new FieldRefusal(ScoreField, "Result_NotANumber", typed?.Trim() ?? string.Empty));
        return score is < 0 or > 100 ? (null, new FieldRefusal(ScoreField, "Supplier_ScoreOutOfRange")) : (score, null);
    }

    /// <summary>The title of the year's report.</summary>
    public static string ReportTitle(int year, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text("SupplierEvaluation.ReportTitle", year);
    }

    /// <summary>The formula, then when the report was drawn up.</summary>
    public static string ReportSubtitle(DateTimeOffset now, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return $"{text("SupplierEvaluation.Formula")} — {now.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}";
    }

    /// <summary>« evaluation-fournisseurs-2026.pdf ».</summary>
    public static string FileName(int year, string extension) =>
        $"evaluation-fournisseurs-{year.ToString(CultureInfo.InvariantCulture)}.{extension}";
}
