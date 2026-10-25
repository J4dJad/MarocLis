using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Patients;

namespace MarocLIS.Application.Catalog;

/// <summary>The tabs of « Catalogue », in the workstation's order.</summary>
public enum CatalogTab
{
    Analyses,
    Tariffs,
    Panels,
    ReflexRules,
    Nomenclature,

    /// <summary>« Nomenclature des actes »: the official list, read-only.</summary>
    Acts,

    /// <summary>« Écarts avec la nomenclature ».</summary>
    Gaps
}

/// <summary>How a row of the catalogue reads: the same reading as the workstation's cell colours.</summary>
public enum CatalogTone
{
    Normal,

    /// <summary>Switched off, superseded or out of force: grey, nothing to act on.</summary>
    Muted,

    /// <summary>Configured but failing somewhere (not orderable, unusable, an age unit to confirm, a review due).</summary>
    Abnormal,

    /// <summary>Active and impossible: a reflex rule somebody relies on that can never fire.</summary>
    Critical
}

/// <summary>What is selected on « Catalogue », for the actions it offers.</summary>
public sealed record CatalogSelection(
    bool Analysis = false, bool Parameter = false, bool Range = false, bool Tariff = false,
    bool Panel = false, bool Reflex = false, bool Nomenclature = false, NomenclatureGapRow? Gap = null);

/// <summary>What « Catalogue » offers, by right and by selection — the workstation's RefreshActions.</summary>
public sealed record CatalogActions(
    bool NewAnalysis, bool EditAnalysis, bool NewParameter, bool EditParameter,
    bool NewRange, bool EditRange, bool ReviewRange, bool NewTariff, bool EditTariff,
    bool NewPanel, bool EditPanel, bool NewReflex, bool EditReflex,
    bool NewNomenclature, bool EditNomenclature, bool ApplyOfficial, bool EditGapAnalysis);

/// <summary>
/// The rules of « Catalogue » that need no window: what is offered on the selection by which right,
/// how each row reads (its tone and its flags), how an interval, a population, an age band and a
/// reflex condition are written, and which analyses the « À compléter » chip keeps. The
/// workstation's CatalogView and the web's screen apply the same ones; the decisions on saving —
/// the immutable code, the frozen value type, the versioned tariff, the superseded interval — stay
/// CatalogMaintenanceService's.
/// </summary>
public static class CatalogScreenRules
{
    /// <summary>
    /// Managing the catalogue, the reference intervals and the tariffs are three rights; editing an
    /// analysis's parameter needs the analysis, an interval its parameter, a tariff its analysis.
    /// The official coefficient is a tariff change; correcting a gap's act code, a catalogue one.
    /// </summary>
    public static CatalogActions Actions(ICurrentUser user, CatalogSelection selection)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(selection);

        var manage = user.Has(Permissions.Catalog.Manage);
        var ranges = user.Has(Permissions.Catalog.ManageReferenceRanges);
        var tariffs = user.Has(Permissions.Billing.ManageTariffs);

        return new CatalogActions(
            NewAnalysis: manage,
            EditAnalysis: manage && selection.Analysis,
            NewParameter: manage && selection.Analysis,
            EditParameter: manage && selection.Parameter,
            NewRange: ranges && selection.Parameter,
            EditRange: ranges && selection.Range,
            ReviewRange: ranges && selection.Range,
            NewTariff: tariffs && selection.Analysis,
            EditTariff: tariffs && selection.Analysis && selection.Tariff,
            NewPanel: manage,
            EditPanel: manage && selection.Panel,
            NewReflex: manage,
            EditReflex: manage && selection.Reflex,
            NewNomenclature: manage,
            EditNomenclature: manage && selection.Nomenclature,
            ApplyOfficial: tariffs && selection.Gap is { CanApplyOfficialCoefficient: true },
            EditGapAnalysis: manage && selection.Gap is not null);
    }

    // ---- Analyses ------------------------------------------------------------------------------

    /// <summary>
    /// An analysis somebody has to come back to: the analyses the « À compléter » chip keeps
    /// (<see cref="CatalogRules.NeedsAttention"/>).
    /// </summary>
    public static IReadOnlyList<AnalysisRow> Shown(IEnumerable<AnalysisRow> rows, bool onlyToComplete)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return onlyToComplete ? [.. rows.Where(CatalogRules.NeedsAttention)] : [.. rows];
    }

    /// <summary>
    /// Active but not orderable is the warning: it appears in the picker and fails at the counter.
    /// </summary>
    public static CatalogTone Tone(AnalysisRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !row.IsActive ? CatalogTone.Muted : !row.IsOrderable ? CatalogTone.Abnormal : CatalogTone.Normal;
    }

    /// <summary>The status column of an analysis: why it is off, what it lacks and what it carries.</summary>
    public static IReadOnlyList<string> Flags(AnalysisRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);

        var flags = new List<string>(4);

        if (!row.IsActive) flags.Add(text("Catalog.Inactive"));
        if (row.ActiveParameterCount == 0) flags.Add(text("Catalog.NoParameter"));
        if (row.TariffCount == 0) flags.Add(text("Catalog.NoTariff"));
        if (row.IsAccredited) flags.Add("ISO");
        if (row.IsSubcontracted) flags.Add(text("Catalog.Subcontracted"));
        if (row.ConsentRequirement != ConsentRequirement.None) flags.Add(text("Consent.Required"));
        // Age bands saved before their unit was kept, which may be read in months although they
        // were typed in days: listed so the biologist can find and re-save them.
        if (row.UnconfirmedAgeBandCount > 0)
            flags.Add($"{text("Catalog.AgeUnitToConfirm")} ({row.UnconfirmedAgeBandCount})");

        return flags;
    }

    /// <summary>« 8 », or « 7/8 » when one of the eight parameters is switched off.</summary>
    public static string ParameterCount(AnalysisRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.ActiveParameterCount == row.ParameterCount
            ? row.ParameterCount.ToString(CultureInfo.InvariantCulture)
            : $"{row.ActiveParameterCount.ToString(CultureInfo.InvariantCulture)}/{row.ParameterCount.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The heading of the list: how many analyses, how many cannot be ordered, how many carry an
    /// age unit to confirm — « Analyses — 120 · 3 non commandables ».
    /// </summary>
    public static string Heading(IReadOnlyCollection<AnalysisRow> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);

        var blocked = rows.Count(a => !a.IsOrderable);
        var unconfirmed = rows.Count(a => a.UnconfirmedAgeBandCount > 0);

        return $"{text("Catalog.Analyses")} — {rows.Count}"
               + (blocked > 0 ? $" · {blocked} {text("Catalog.NotOrderable").ToLowerInvariant()}" : string.Empty)
               + (unconfirmed > 0 ? $" · {unconfirmed} {text("Catalog.AgeUnitToConfirm").ToLowerInvariant()}" : string.Empty);
    }

    // ---- Parameters ----------------------------------------------------------------------------

    public static CatalogTone Tone(ParameterRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !row.IsActive ? CatalogTone.Muted : row.IsUnusable ? CatalogTone.Abnormal : CatalogTone.Normal;
    }

    public static IReadOnlyList<string> Flags(ParameterRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);

        var flags = new List<string>(4);

        if (!row.IsActive) flags.Add(text("Catalog.Inactive"));
        if (row.IsCalculated) flags.Add("ƒ");
        if (row.IsInternalOnly) flags.Add(text("Catalog.InternalOnly"));
        if (row.IsUnusable) flags.Add(text("Catalog.Unusable"));
        if (row.UnconfirmedAgeBandCount > 0) flags.Add(text("Catalog.AgeUnitToConfirm"));
        if (row.HasResults) flags.Add(row.ResultCount.ToString(CultureInfo.InvariantCulture));

        return flags;
    }

    /// <summary>A pair of limits: « 0.5 – 1.2 », « ≤ 1.2 », « ≥ 0.5 », or nothing.</summary>
    public static string Interval(decimal? low, decimal? high, IFormatProvider? culture = null) => (low, high) switch
    {
        ({ } l, { } h) => $"{l.ToString("0.###", culture)} – {h.ToString("0.###", culture)}",
        (null, { } h) => $"≤ {h.ToString("0.###", culture)}",
        ({ } l, null) => $"≥ {l.ToString("0.###", culture)}",
        _ => string.Empty
    };

    /// <summary>« 15 % » for a delta check, nothing without one.</summary>
    public static string DeltaCheck(ParameterRow row, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.DeltaCheckPercent is { } delta ? $"{delta.ToString("0.#", culture)} %" : string.Empty;
    }

    // ---- Reference intervals -------------------------------------------------------------------

    /// <summary>
    /// Out of force is grey; an age unit to confirm is the warning; a review due is said in the
    /// warning's colour on an otherwise plain row.
    /// </summary>
    public static CatalogTone Tone(ReferenceRangeRow row, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!row.IsInForce(today)) return CatalogTone.Muted;
        return row.HasUnconfirmedAgeUnit || row.NeedsReview(today) ? CatalogTone.Abnormal : CatalogTone.Normal;
    }

    public static IReadOnlyList<string> Flags(ReferenceRangeRow row, DateOnly today, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);

        var flags = new List<string>(3);

        if (!row.IsInForce(today)) flags.Add(text("Catalog.Superseded"));
        if (row.NeedsReview(today)) flags.Add(text("Catalog.ReviewDue"));
        if (row.HasUnconfirmedAgeUnit) flags.Add(text("Catalog.AgeUnitToConfirm"));
        if (row.IsDecisionThreshold) flags.Add(text("Catalog.DecisionThreshold"));

        return flags;
    }

    /// <summary>
    /// The interval as it reads: its printed text when it has one, the expected coded answer, or
    /// its limits in the parameter's decimals — « 4.00 - 10.00 », « &lt; 5.00 », « &gt; 40.00 ».
    /// </summary>
    public static string FormatInterval(ReferenceRangeRow row, int decimals, AppLanguage language, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.DisplayText is { } text && text.For(language).Length > 0) return text.For(language);
        if (row.ExpectedCodedValue is { Length: > 0 } coded) return coded;

        var format = "N" + decimals.ToString(CultureInfo.InvariantCulture);

        return (row.LowerLimit, row.UpperLimit) switch
        {
            ({ } low, { } high) => $"{low.ToString(format, culture)} - {high.ToString(format, culture)}",
            (null, { } high) => $"< {high.ToString(format, culture)}",
            ({ } low, null) => $"> {low.ToString(format, culture)}",
            _ => "—"
        };
    }

    /// <summary>« 01/01/24 → 31/12/26 », or « 01/01/24 → » while it runs on.</summary>
    public static string Validity(DateOnly from, DateOnly? until) =>
        until is { } end
            ? $"{from.ToString("dd/MM/yy", CultureInfo.InvariantCulture)} → {end.ToString("dd/MM/yy", CultureInfo.InvariantCulture)}"
            : $"{from.ToString("dd/MM/yy", CultureInfo.InvariantCulture)} →";

    /// <summary>
    /// Who the interval applies to, in the order that decides which of two overlapping intervals
    /// wins — trimester, pregnancy, instrument, sex, age. The same order as the resolver's.
    /// </summary>
    public static string DescribePopulation(ReferenceRangeRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);

        var parts = new List<string>(5);

        if (row.PregnancyTrimester is { } trimester)
            parts.Add(text("Catalog.Trimester", trimester));
        else if (row.AppliesToPregnancy is { } pregnant)
            parts.Add(pregnant ? text("Patient.Pregnant") : text("Catalog.NotPregnant"));

        if (row.InstrumentName is { Length: > 0 } instrument) parts.Add(instrument);

        if (row.AppliesToGender != Gender.Unknown)
            parts.Add(text($"Gender.{row.AppliesToGender}"));

        if (row.AgeFromDays is not null || row.AgeToDays is not null)
            parts.Add(DescribeAgeBand(row.AgeFromDays, row.AgeToDays, text));

        return parts.Count == 0 ? text("Catalog.AllPatients") : string.Join(" · ", parts);
    }

    /// <summary>
    /// An age band in the unit a human would use for it. Neonatal intervals are written in days and
    /// adult ones in years, and printing « 0 – 10950 jours » for an adult band is how a reviewer
    /// stops reading the column.
    /// </summary>
    public static string DescribeAgeBand(int? fromDays, int? toDays, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var from = fromDays is { } f ? Age(f) : null;
        var to = toDays is { } t ? Age(t) : null;

        return (from, to) switch
        {
            ({ } a, { } b) => $"{a} – {b}",
            (null, { } b) => $"< {b}",
            ({ } a, null) => $"≥ {a}",
            _ => string.Empty
        };

        string Age(int days) => days switch
        {
            < 31 => $"{days} {text("Catalog.Days")}",
            < 731 => $"{days / 30} {text("Catalog.Months")}",
            _ => $"{days / 365} {text("Catalog.Years")}"
        };
    }

    // ---- Tariffs -------------------------------------------------------------------------------

    /// <summary>In force is the one that matters; the history is grey behind it.</summary>
    public static CatalogTone Tone(TariffRow row, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.IsInForce(today) ? CatalogTone.Normal : CatalogTone.Muted;
    }

    /// <summary>« Exonéré », or the rate as a percentage.</summary>
    public static string Vat(TariffRow row, ScreenText text, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return row.IsVatExempt ? text("Billing.VatExempt") : row.VatRate.ToString("P0", culture);
    }

    // ---- Panels --------------------------------------------------------------------------------

    public static CatalogTone Tone(PanelRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !row.IsActive ? CatalogTone.Muted : row.InactiveMemberCount > 0 ? CatalogTone.Abnormal : CatalogTone.Normal;
    }

    public static IReadOnlyList<string> Flags(PanelRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);

        var flags = new List<string>(4);

        if (!row.IsActive) flags.Add(text("Catalog.Inactive"));
        if (row.InactiveMemberCount > 0) flags.Add(text("Catalog.PanelHasInactiveMember", row.InactiveMemberCount));
        if (row.PackageAboveComponents) flags.Add(text("Catalog.PackageAboveComponents"));
        if (!row.ItemizeOnInvoice) flags.Add(text("Catalog.NotItemized"));

        return flags;
    }

    /// <summary>« 5 », or « 4+1 » with one optional member.</summary>
    public static string MemberCount(PanelRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.OptionalCount > 0
            ? $"{(row.MemberCount - row.OptionalCount).ToString(CultureInfo.InvariantCulture)}+{row.OptionalCount.ToString(CultureInfo.InvariantCulture)}"
            : row.MemberCount.ToString(CultureInfo.InvariantCulture);
    }

    public static IReadOnlyList<string> Flags(PanelMemberRow member, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(text);

        var flags = new List<string>(2);
        if (member.IsOptional) flags.Add(text("Catalog.Optional"));
        if (!member.IsAnalysisActive) flags.Add(text("Catalog.Inactive"));
        return flags;
    }

    // ---- Reflex rules --------------------------------------------------------------------------

    /// <summary>
    /// Active and impossible is the worst of the three states, because somebody is relying on a
    /// confirmation that will never be ordered.
    /// </summary>
    public static CatalogTone Tone(ReflexRuleRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !row.IsActive ? CatalogTone.Muted : row.CanNeverFire ? CatalogTone.Critical : CatalogTone.Normal;
    }

    public static IReadOnlyList<string> Flags(ReflexRuleRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);

        var flags = new List<string>(3);
        if (!row.IsActive) flags.Add(text("Catalog.Inactive"));
        if (row.CanNeverFire) flags.Add(text("Catalog.RuleCanNeverFire"));
        if (!row.IsBillable) flags.Add(text("Catalog.NotBilled"));
        return flags;
    }

    /// <summary>« &gt; 10 », « 4 – 10 », « ∉ [4 ; 10] », « = POS ».</summary>
    public static string DescribeCondition(ReflexRuleRow row, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(row);

        string N(decimal? value) => value?.ToString("0.###", culture) ?? string.Empty;

        return row.Condition switch
        {
            ReflexCondition.GreaterThan => $"> {N(row.Threshold)}",
            ReflexCondition.LessThan => $"< {N(row.Threshold)}",
            ReflexCondition.Between => $"{N(row.Threshold)} – {N(row.ThresholdUpper)}",
            ReflexCondition.Outside => $"∉ [{N(row.Threshold)} ; {N(row.ThresholdUpper)}]",
            ReflexCondition.EqualsCoded => $"= {row.TriggerCodedValue}",
            _ => string.Empty
        };
    }

    /// <summary>The mode of a rule: added without asking, or offered for confirmation.</summary>
    public static string ModeKey(ReflexRuleRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.AddAutomatically ? "Catalog.Automatic" : "Catalog.NeedsConfirmation";
    }

    // ---- Nomenclature --------------------------------------------------------------------------

    /// <summary>
    /// The detail column of a list entry: the accreditation scope and supervisor of a department,
    /// the fasting and collection instructions of a specimen type, the draw order, additive, volume
    /// and handling of a tube, the UCUM code of a unit.
    /// </summary>
    public static string Detail(NomenclatureKind kind, NomenclatureRow row, AppLanguage language, ScreenText text,
                                IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);

        var detail = kind switch
        {
            NomenclatureKind.Department =>
                (row.IsInAccreditationScope ? "ISO 15189" : string.Empty)
                + (row.SupervisorName is { Length: > 0 } supervisor ? $" · {supervisor}" : string.Empty),

            NomenclatureKind.SpecimenType =>
                (row.FastingHours > 0 ? $"{text("Order.Fasted")} {row.FastingHours} h" : string.Empty)
                + (row.CollectionInstructions is { } instructions ? $" · {instructions.For(language)}" : string.Empty),

            NomenclatureKind.ContainerType =>
                $"{text("Sample.DrawOrder")} {row.DrawOrder}"
                + (row.Additive is { } additive ? $" · {additive.For(language)}" : string.Empty)
                + (row.NominalVolumeMl is { } volume ? $" · {volume.ToString("0.#", culture)} mL" : string.Empty)
                + (row.RequiresProtectionFromLight ? $" · {text("Collection.ProtectFromLight")}" : string.Empty)
                + (row.RequiresIceTransport ? $" · {text("Collection.TransportOnIce")}" : string.Empty),

            _ => row.UcumCode is { Length: > 0 } ucum ? $"UCUM {ucum}" : string.Empty
        };

        return detail.Trim(' ', '·');
    }

    /// <summary>A unit is listed by its symbol, which is the same in every language; the others by name.</summary>
    public static string Name(NomenclatureKind kind, NomenclatureRow row, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(row);
        return kind == NomenclatureKind.MeasurementUnit ? row.DisplaySymbol : row.Name.For(language);
    }

    public static IReadOnlyList<string> Flags(NomenclatureKind kind, NomenclatureRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);

        var flags = new List<string>(2);
        if (!row.IsActive) flags.Add(text("Catalog.Inactive"));
        if (kind == NomenclatureKind.ContainerType && !IsRenderableColour(row.CapColourHex))
            flags.Add(text("Catalog.InvalidColour"));
        return flags;
    }

    /// <summary>A cap colour a screen can paint: « #RRGGBB ».</summary>
    public static bool IsRenderableColour(string? value) =>
        value is { Length: 7 } colour && colour[0] == '#' && colour[1..].All(Uri.IsHexDigit);

    // ---- National acts and gaps -----------------------------------------------------------------

    /// <summary>The decree years of the nomenclature, as the chips of « Nomenclature des actes » offer them.</summary>
    public static IReadOnlyList<int> DecreeYears { get; } = [2005, 2012, 2016];

    /// <summary>The acts the search finds, then those of the decree chosen (all of them without one).</summary>
    public static (IReadOnlyList<NationalActRow> Matching, IReadOnlyList<NationalActRow> Shown) Acts(
        IEnumerable<NationalActRow> acts, string? search, int? decree)
    {
        ArgumentNullException.ThrowIfNull(acts);

        var matching = acts.Where(a => NationalActSearch.Matches(a, search)).ToList();
        return (matching, [.. matching.Where(a => decree is null || a.DecreeYear == decree)]);
    }

    /// <summary>The acts the picker of the analysis editor offers: the active ones the search finds.</summary>
    public static IReadOnlyList<NationalActRow> Pickable(IEnumerable<NationalActRow> acts, string? search)
    {
        ArgumentNullException.ThrowIfNull(acts);
        return [.. acts.Where(a => a.IsActive && NationalActSearch.Matches(a, search))];
    }

    /// <summary>« Chapitre · sous-chapitre », the chapter alone without one.</summary>
    public static string Chapter(NationalActRow act)
    {
        ArgumentNullException.ThrowIfNull(act);
        return act.SubChapter is { Length: > 0 } sub ? $"{act.Chapter} · {sub}" : act.Chapter;
    }

    /// <summary>« B 30 »: the letter key and the coefficient.</summary>
    public static string Coefficient(NationalActRow act, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(act);
        return $"{act.LetterKey} {act.Coefficient.ToString("0.##", culture)}";
    }

    /// <summary>The gaps of the kind chosen, all of them without one.</summary>
    public static IReadOnlyList<NomenclatureGapRow> Gaps(IEnumerable<NomenclatureGapRow> gaps, NomenclatureGapKind? kind)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        return [.. gaps.Where(g => kind is null || g.Kind == kind)];
    }

    /// <summary>The kind of a gap, with how many tariffs differ when several do.</summary>
    public static string GapText(NomenclatureGapRow gap, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(gap);
        ArgumentNullException.ThrowIfNull(text);

        return gap.Kind == NomenclatureGapKind.CoefficientDiffers && gap.DifferingTariffCount > 1
            ? $"{text($"NabmGap.{gap.Kind}")} ({gap.DifferingTariffCount})"
            : text($"NabmGap.{gap.Kind}");
    }

    /// <summary>A coefficient gap is a warning, an unknown code critical, a missing one waiting.</summary>
    public static CatalogTone Tone(NomenclatureGapRow gap)
    {
        ArgumentNullException.ThrowIfNull(gap);
        return gap.Kind switch
        {
            NomenclatureGapKind.CoefficientDiffers => CatalogTone.Abnormal,
            NomenclatureGapKind.UnknownCode => CatalogTone.Critical,
            _ => CatalogTone.Muted
        };
    }

    /// <summary>
    /// The line below the one put right, or above it for the last: where the list goes once the
    /// current line has left it, so the analyses are corrected one after the other.
    /// </summary>
    public static NomenclatureGapRow? Neighbour(IReadOnlyList<NomenclatureGapRow> shown, Guid analysisId)
    {
        ArgumentNullException.ThrowIfNull(shown);

        var at = shown.ToList().FindIndex(g => g.AnalysisId == analysisId);
        if (at < 0) return null;
        return at + 1 < shown.Count ? shown[at + 1] : at > 0 ? shown[at - 1] : null;
    }

    /// <summary>
    /// What applying the official coefficient did: the new versions and the corrections, from today,
    /// and the scheduled tariffs left alone.
    /// </summary>
    public static string Applied(NomenclatureGapRow gap, OfficialCoefficientOutcome outcome, DateOnly today, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(gap);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(text);

        var day = today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        return text("Nabm.Applied", gap.AnalysisCode, gap.NationalActCode!, outcome.NewVersions + outcome.Corrected, day)
               + (outcome.SkippedFutureDated > 0 ? " " + text("Nabm.SkippedScheduled", outcome.SkippedFutureDated) : string.Empty);
    }

    /// <summary>The question asked before the official coefficient is applied.</summary>
    public static string ConfirmApply(NomenclatureGapRow gap, DateOnly today, ScreenText text, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(gap);
        ArgumentNullException.ThrowIfNull(text);

        return text("Nabm.ConfirmApply", gap.NationalActCode!,
                    gap.OfficialCoefficient!.Value.ToString("0.##", culture),
                    gap.AnalysisCode, gap.DifferingTariffCount, today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
    }
}
