using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Quality;

namespace MarocLIS.Application.Administration;

/// <summary>The tabs of « Référentiels », in the workstation's order.</summary>
public enum MasterDataTab
{
    Instruments,
    QualityControl,
    Stock,
    Suppliers,
    Prescribers,
    Payers
}

/// <summary>How a row of « Référentiels » reads: the workstation's cell colours.</summary>
public enum MasterDataTone
{
    Normal,

    /// <summary>Switched off or decommissioned: grey.</summary>
    Muted,

    /// <summary>Said in the warning's colour on an otherwise plain row.</summary>
    Warning,

    /// <summary>A row somebody has to come back to: filled in the warning's colours.</summary>
    Abnormal,

    /// <summary>A row that silently disarms a control or gets every invoice rejected.</summary>
    Critical,

    /// <summary>Set in bold: a critical stock article.</summary>
    Strong
}

/// <summary>A Westgard rule as « Règles de Westgard » lists it: its name and the description the evaluator quotes.</summary>
public sealed record WestgardRuleChoice(WestgardRule Rule, string LabelKey, string DescriptionKey);

/// <summary>
/// The rules of « Référentiels » that need no window: which right keeps each tab, how each row reads
/// (its tone and its state), the screens the reference data still leaves unusable, and the Westgard
/// rules a control can run. The workstation's MasterDataView and the web's screen apply the same
/// ones; the refusals on saving stay MasterDataService's and the domain's.
/// </summary>
public static class MasterDataScreenRules
{
    /// <summary>
    /// Each tab is kept by its own right; its buttons are greyed out for a user without it, as the
    /// service refuses them in any case.
    /// </summary>
    public static string ManageRight(MasterDataTab tab) => tab switch
    {
        MasterDataTab.Instruments => Permissions.Equipment.Manage,
        MasterDataTab.QualityControl => Permissions.Quality.ManageQcRules,
        MasterDataTab.Stock or MasterDataTab.Suppliers => Permissions.Inventory.ManagePurchaseOrders,
        MasterDataTab.Prescribers => Permissions.Catalog.Manage,
        _ => Permissions.Billing.ManageTariffs
    };

    public static bool CanManage(ICurrentUser user, MasterDataTab tab)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Has(ManageRight(tab));
    }

    /// <summary>
    /// The screens the reference data still leaves unusable, as the keys of their names — what an
    /// administrator setting up an installation needs to know, and not obvious from five empty
    /// lists. A payer carrying a problem counts too: the seeded payers are not an empty list, so
    /// nothing else would say their rates were never confirmed against the convention.
    /// </summary>
    public static IReadOnlyList<string> Blockers(int instruments, int materials, int targets, int stockItems,
                                                 int prescribers, IEnumerable<PayerRow> payers)
    {
        ArgumentNullException.ThrowIfNull(payers);

        var blockers = new List<string>(5);

        if (instruments == 0) blockers.Add("Nav.Equipment");
        if (materials == 0 || targets == 0) blockers.Add("Nav.QualityControl");
        if (stockItems == 0) blockers.Add("Nav.Inventory");
        if (prescribers == 0) blockers.Add("Order.Prescribers");
        if (payers.Any(p => p.Problems.Count > 0)) blockers.Add("Coverage.Payers");

        return blockers;
    }

    /// <summary>« ✓ Référentiels complets », or « ⚠ Référentiels incomplets — Équipements · Stock ».</summary>
    public static string Completeness(IReadOnlyList<string> blockers, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(blockers);
        ArgumentNullException.ThrowIfNull(text);

        return blockers.Count == 0
            ? "✓ " + text("Admin.MasterDataComplete")
            : "⚠ " + text("Admin.MasterDataIncomplete") + " — " + string.Join(" · ", blockers.Select(k => text(k)));
    }

    // ---- Instruments ---------------------------------------------------------------------------

    /// <summary>
    /// A missing interval is the sharpest defect of the screen: the overdue indicators and the
    /// calibration guard are both computed from it, so a null silently disarms them.
    /// </summary>
    public static MasterDataTone Tone(InstrumentMasterRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.IntervalsIncomplete ? MasterDataTone.Critical
            : row.Status == EquipmentStatus.Decommissioned ? MasterDataTone.Muted
            : MasterDataTone.Normal;
    }

    /// <summary>« 30 jours », or « — » for an interval never set.</summary>
    public static string Interval(int? days, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return days is { } value ? text("Stock.Days", value) : "—";
    }

    // ---- Quality control -----------------------------------------------------------------------

    /// <summary>
    /// Unusable is critical; close to expiry, or with no target to enter a value against, is
    /// present but unusable in practice.
    /// </summary>
    public static MasterDataTone Tone(QcMaterialRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !row.IsUsable ? MasterDataTone.Critical
            : row.DaysToExpiry <= 30 || row.TargetCount == 0 ? MasterDataTone.Abnormal
            : MasterDataTone.Normal;
    }

    public static string StateKey(QcMaterialRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !row.IsActive ? "Admin.Inactive"
            : row.DaysToExpiry < 0 ? "Inventory.Expired"
            : row.TargetCount == 0 ? "Quality.NoTarget"
            : "Inventory.Usable";
    }

    /// <summary>No usable limits is critical; running on the supplier's limits is said in the warning's colour.</summary>
    public static MasterDataTone Tone(QcTargetRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !row.HasUsableLimits ? MasterDataTone.Critical
            : !row.IsActive ? MasterDataTone.Muted
            : row.LaboratoryMean is null ? MasterDataTone.Warning
            : MasterDataTone.Normal;
    }

    public static string StateKey(QcTargetRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !row.HasUsableLimits ? "Qc_NoTargetEstablished"
            : !row.IsActive ? "Admin.Inactive"
            : row.LaboratoryMean is null ? "Quality.SupplierLimits"
            : "Quality.LaboratoryLimits";
    }

    /// <summary>« 5.120 ± 0.150 », or « — » without both.</summary>
    public static string Limits(decimal? mean, decimal? sd, IFormatProvider? culture = null) =>
        mean is { } m && sd is { } s ? $"{m.ToString("N3", culture)} ± {s.ToString("N3", culture)}" : "—";

    /// <summary>
    /// The Westgard multirule set: warning rule plus the rejection rules that detect random and
    /// systematic error. What most laboratories run on a two-level control, and what a new target starts with.
    /// </summary>
    public const WestgardRule ClassicMultirule =
        WestgardRule.OneTwoS | WestgardRule.OneThreeS | WestgardRule.TwoTwoS |
        WestgardRule.R4S | WestgardRule.FourOneS | WestgardRule.TenX;

    /// <summary>1₃ₛ alone, for a method whose imprecision makes the tighter rules fire on noise.</summary>
    public const WestgardRule SingleRule = WestgardRule.OneThreeS;

    /// <summary>The rules a control can run, each with the description the evaluator quotes when it fires.</summary>
    public static IReadOnlyList<WestgardRuleChoice> WestgardRules { get; } =
    [
        new(WestgardRule.OneTwoS, "Qc.Rule_1_2s", "Qc_Rule_1_2s"),
        new(WestgardRule.OneThreeS, "Qc.Rule_1_3s", "Qc_Rule_1_3s"),
        new(WestgardRule.TwoTwoS, "Qc.Rule_2_2s", "Qc_Rule_2_2s"),
        new(WestgardRule.R4S, "Qc.Rule_R_4s", "Qc_Rule_R_4s"),
        new(WestgardRule.ThreeOneS, "Qc.Rule_3_1s", "Qc_Rule_3_1s"),
        new(WestgardRule.FourOneS, "Qc.Rule_4_1s", "Qc_Rule_4_1s"),
        new(WestgardRule.TwoOfThreeTwoS, "Qc.Rule_2of3_2s", "Qc_Rule_2of3_2s"),
        new(WestgardRule.SevenT, "Qc.Rule_7T", "Qc_Rule_7T"),
        new(WestgardRule.EightX, "Qc.Rule_8x", "Qc_Rule_8x"),
        new(WestgardRule.TenX, "Qc.Rule_10x", "Qc_Rule_10x"),
        new(WestgardRule.TwelveX, "Qc.Rule_12x", "Qc_Rule_12x")
    ];

    public static int CountRules(WestgardRule rules) =>
        Enum.GetValues<WestgardRule>().Count(r => r != WestgardRule.None && rules.HasFlag(r));

    /// <summary>The rules ticked, as one set.</summary>
    public static WestgardRule Combine(IEnumerable<WestgardRule> ticked)
    {
        ArgumentNullException.ThrowIfNull(ticked);
        return ticked.Aggregate(WestgardRule.None, (all, rule) => all | rule);
    }

    /// <summary>
    /// « 6 règles actives », or the warning that a control with no rule records points and never
    /// rejects a run — which looks like a control that is passing.
    /// </summary>
    public static string RuleSummary(WestgardRule rules, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var count = CountRules(rules);
        return count == 0 ? text("Quality.NoRulesWarning") : text("Quality.RuleCount", count);
    }

    // ---- Stock ---------------------------------------------------------------------------------

    public static MasterDataTone Tone(StockItemMasterRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !row.IsActive ? MasterDataTone.Muted : row.IsCritical ? MasterDataTone.Strong : MasterDataTone.Normal;
    }

    /// <summary>★ critical, ☣ hazardous, ∅ no lot received yet.</summary>
    public static string Flags(StockItemMasterRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var flags = new List<string>(3);
        if (row.IsCritical) flags.Add("★");
        if (row.IsHazardous) flags.Add("☣");
        if (row.LotCount == 0) flags.Add("∅");
        return string.Join(" ", flags);
    }

    // ---- Suppliers -----------------------------------------------------------------------------

    public static MasterDataTone Tone(SupplierRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.IsEvaluationOverdue ? MasterDataTone.Abnormal : !row.IsActive ? MasterDataTone.Muted : MasterDataTone.Normal;
    }

    /// <summary>« 12/03/26 (85) », or « — » for a supplier never evaluated.</summary>
    public static string Evaluation(SupplierRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.LastEvaluationDate is { } date
            ? date.ToString("dd/MM/yy", CultureInfo.InvariantCulture)
              + (row.EvaluationScore is { } score ? $" ({score.ToString(CultureInfo.InvariantCulture)})" : string.Empty)
            : "—";
    }

    // ---- Prescribers ---------------------------------------------------------------------------

    /// <summary>No INPE on a physician means an AMO claim carrying the prescription cannot be filed.</summary>
    public static MasterDataTone Tone(PrescriberRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !row.IsActive ? MasterDataTone.Muted
            : PrescriberEntryRules.WarnsOfMissingInpe(row.Kind, row.Inpe) ? MasterDataTone.Abnormal
            : MasterDataTone.Normal;
    }

    /// <summary>The name in Arabic script for an Arabic reader when it was recorded.</summary>
    public static string Name(PrescriberRow row, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(row);
        return language == AppLanguage.Arabic && row.FullNameArabic is { Length: > 0 } arabic ? arabic : row.FullName;
    }

    // ---- Payers --------------------------------------------------------------------------------

    /// <summary>
    /// An expired convention is the sharpest defect: invoices keep being raised against the payer
    /// and every one of them comes back rejected.
    /// </summary>
    public static MasterDataTone Tone(PayerRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !row.IsActive ? MasterDataTone.Muted
            : row.IsAgreementExpired ? MasterDataTone.Critical
            : row.Problems.Count > 0 ? MasterDataTone.Abnormal
            : MasterDataTone.Normal;
    }

    /// <summary>The percentage, the unit a convention is written in; the stored value is the fraction the pricing needs.</summary>
    public static string Rate(PayerRow row, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"{row.ReimbursementPercent.ToString("0.##", culture)} %";
    }

    /// <summary>What would make the payer reject or mis-price a claim, in the screen's language.</summary>
    public static string Problems(PayerRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return string.Join(" · ", row.Problems.Select(k => text(k)));
    }
}
