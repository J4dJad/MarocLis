using System.Globalization;
using System.Text.RegularExpressions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Patients;

namespace MarocLIS.Application.Catalog;

// ---- Forms -------------------------------------------------------------------------------------
// What each editor of « Catalogue » holds while it is typed in: the boxes as text, the lists as
// their ids, the ticks as booleans. The workstation's dialogs and the web's forms fill one, check it
// and turn it into the service's request with the same rules.

/// <summary>An analysis as typed, with the laboratory's own price beside it.</summary>
public sealed record AnalysisForm
{
    public string Code { get; init; } = string.Empty;
    public string NameFr { get; init; } = string.Empty;
    public string NameAr { get; init; } = string.Empty;
    public string NameEn { get; init; } = string.Empty;
    public string ShortName { get; init; } = string.Empty;
    public string Aliases { get; init; } = string.Empty;
    public Guid? DepartmentId { get; init; }
    public Guid? SpecimenTypeId { get; init; }
    public Guid? ContainerTypeId { get; init; }
    public string Volume { get; init; } = string.Empty;
    public string Loinc { get; init; } = string.Empty;
    public string ActCode { get; init; } = string.Empty;
    public ConsentRequirement Consent { get; init; } = ConsentRequirement.None;
    public string Method { get; init; } = string.Empty;
    public string Reagent { get; init; } = string.Empty;
    public bool Accredited { get; init; }
    public bool Subcontracted { get; init; }
    public bool Notifiable { get; init; }
    public Guid? SubcontractorSiteId { get; init; }
    public int RoutineTurnaroundHours { get; init; } = 24;
    public int UrgentTurnaroundMinutes { get; init; } = 120;
    public bool AvailableUrgently { get; init; } = true;
    public int FastingHours { get; init; }
    public int StabilityRoomHours { get; init; }
    public int StabilityFridgeHours { get; init; }
    public int DisplayOrder { get; init; }
    public string ClinicalInformation { get; init; } = string.Empty;
    public bool Active { get; init; } = true;

    public string LetterKey { get; init; } = NationalBiologyAct.BiologyLetterKey;
    public string Coefficient { get; init; } = string.Empty;
    public string LetterKeyValue { get; init; } = string.Empty;
    public string Price { get; init; } = string.Empty;
}

/// <summary>An allowed answer of a coded parameter, as a line of the editor's list.</summary>
public sealed record CodedOptionLine(string Code, string LabelFr, string LabelAr = "", bool ExpectedNormal = false,
                                     bool Abnormal = false, bool Critical = false);

/// <summary>A parameter as typed, with its allowed answers.</summary>
public sealed record ParameterForm
{
    public string Code { get; init; } = string.Empty;
    public ResultValueType ValueType { get; init; } = ResultValueType.Numeric;
    public Guid? UnitId { get; init; }
    public int Decimals { get; init; } = 2;
    public string NameFr { get; init; } = string.Empty;
    public string NameAr { get; init; } = string.Empty;
    public string NameEn { get; init; } = string.Empty;
    public string ShortName { get; init; } = string.Empty;
    public string Loinc { get; init; } = string.Empty;
    public string Cv { get; init; } = string.Empty;
    public int DisplayOrder { get; init; }
    public int IndentLevel { get; init; }
    public string MeasuringLow { get; init; } = string.Empty;
    public string MeasuringHigh { get; init; } = string.Empty;
    public string TechnicalLow { get; init; } = string.Empty;
    public string TechnicalHigh { get; init; } = string.Empty;
    public string CriticalLow { get; init; } = string.Empty;
    public string CriticalHigh { get; init; } = string.Empty;
    public string Delta { get; init; } = string.Empty;
    public int DeltaWindowDays { get; init; }
    public string Formula { get; init; } = string.Empty;
    public bool InternalOnly { get; init; }
    public bool AlwaysShowOnReport { get; init; } = true;
    public bool Active { get; init; } = true;
    public IReadOnlyList<CodedOptionLine> Options { get; init; } = [];
}

/// <summary>Which fields a value type uses: the others are turned off, not hidden, so the form does not jump.</summary>
public sealed record ParameterFields(bool Numeric, bool Coded, bool Heading);

/// <summary>A reference interval as typed: its population, its limits, its provenance and its validity.</summary>
public sealed record RangeForm
{
    public Gender Gender { get; init; } = Gender.Unknown;
    public bool? Pregnancy { get; init; }
    public int? Trimester { get; init; }
    public int AgeFrom { get; init; }
    public int AgeTo { get; init; }

    /// <summary>The unit the band is typed in, in days: 1, 30 or 365.</summary>
    public int AgeUnitDays { get; init; } = 365;

    public Guid? InstrumentId { get; init; }
    public string Lower { get; init; } = string.Empty;
    public string Upper { get; init; } = string.Empty;
    public string ExpectedCoded { get; init; } = string.Empty;
    public bool DecisionThreshold { get; init; }
    public string DisplayFr { get; init; } = string.Empty;
    public string DisplayAr { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public DateOnly? LastReviewedOn { get; init; }
    public DateOnly ValidFrom { get; init; }
    public DateOnly? ValidUntil { get; init; }
    public bool Active { get; init; } = true;
}

/// <summary>A tariff revision (or a correction of one entered today) as typed.</summary>
public sealed record TariffForm
{
    public Guid? PayerId { get; init; }
    public DateOnly ValidFrom { get; init; }
    public string LetterKey { get; init; } = NationalBiologyAct.BiologyLetterKey;
    public string Coefficient { get; init; } = string.Empty;
    public string LetterKeyValue { get; init; } = string.Empty;
    public string Price { get; init; } = string.Empty;
    public bool VatExempt { get; init; } = true;
    public string VatRate { get; init; } = string.Empty;
}

/// <summary>A panel as typed: its identity, its package price and the analyses it orders.</summary>
public sealed record PanelForm
{
    public string Code { get; init; } = string.Empty;
    public string NameFr { get; init; } = string.Empty;
    public string NameAr { get; init; } = string.Empty;
    public string NameEn { get; init; } = string.Empty;
    public string Aliases { get; init; } = string.Empty;
    public string PackagePrice { get; init; } = string.Empty;
    public int DisplayOrder { get; init; }
    public bool ItemizeOnInvoice { get; init; } = true;
    public bool GroupOnReport { get; init; }
    public bool Active { get; init; } = true;
    public IReadOnlyList<PanelMemberRequest> Members { get; init; } = [];
}

/// <summary>A reflex rule as typed.</summary>
public sealed record ReflexForm
{
    public Guid? TriggerAnalysisId { get; init; }
    public Guid? TriggerParameterId { get; init; }
    public ReflexCondition Condition { get; init; } = ReflexCondition.GreaterThan;
    public string Threshold { get; init; } = string.Empty;
    public string ThresholdUpper { get; init; } = string.Empty;
    public string CodedValue { get; init; } = string.Empty;
    public Guid? TargetAnalysisId { get; init; }
    public string DescriptionFr { get; init; } = string.Empty;
    public string DescriptionAr { get; init; } = string.Empty;
    public bool AddAutomatically { get; init; }
    public bool Billable { get; init; } = true;
    public bool Active { get; init; } = true;
}

/// <summary>Which threshold boxes a condition reads, and the sentence that says what it means.</summary>
public sealed record ReflexFields(bool Threshold, bool ThresholdUpper, bool CodedValue, string NoticeKey);

/// <summary>An entry of one of the four lists the catalogue is built on, as typed.</summary>
public sealed record NomenclatureForm
{
    public string Code { get; init; } = string.Empty;
    public string NameFr { get; init; } = string.Empty;
    public string NameAr { get; init; } = string.Empty;
    public string NameEn { get; init; } = string.Empty;
    public bool Active { get; init; } = true;

    // Department
    public int DisplayOrder { get; init; }
    public bool InAccreditationScope { get; init; }

    // Specimen type
    public int FastingHours { get; init; }
    public string CollectionInstructions { get; init; } = string.Empty;
    public string PatientInstructions { get; init; } = string.Empty;

    // Container type
    public string CapColour { get; init; } = "#FFFFFF";
    public string Additive { get; init; } = string.Empty;
    public string NominalVolume { get; init; } = string.Empty;
    public string MinimumVolume { get; init; } = string.Empty;
    public int DrawOrder { get; init; }
    public bool ProtectFromLight { get; init; }
    public bool IceTransport { get; init; }

    // Measurement unit
    public string Symbol { get; init; } = string.Empty;
    public string SymbolArabic { get; init; } = string.Empty;
    public string Ucum { get; init; } = string.Empty;
    public string FactorToSi { get; init; } = string.Empty;
    public string SiUnit { get; init; } = string.Empty;
}

/// <summary>What the code box of the analysis editor names: nothing, a known act, or a code the list does not know.</summary>
public enum ActInfoKind
{
    /// <summary>No code and no list to pick from: nothing is said.</summary>
    None,

    /// <summary>No code yet: the hint that one can be picked.</summary>
    Hint,
    Known,
    Unknown
}

/// <summary>The act the code box names, with its official coefficient, or why it names none.</summary>
public sealed record ActInfo(ActInfoKind Kind, string? Code = null, NationalActRow? Act = null);

/// <summary>
/// The rules of the editors of « Catalogue » that need no window: how each opens on what is stored,
/// what it refuses before the service is asked (on the field it names), which fields a choice turns
/// off, what it computes as it is typed — the reimbursement base and the excess, the specificity of
/// an interval, the total of a panel — and the request it sends. The workstation's dialogs and the
/// web's forms apply the same ones; the refusals that need the database — a code taken, a value
/// type frozen by results, a tariff backdated behind another — stay CatalogMaintenanceService's.
/// </summary>
public static partial class CatalogEditorRules
{
    // The lengths of the columns: a longer value was refused by the database with an unexpected
    // error, and the form was lost with it.
    public const int CodeMaxLength = 30;
    public const int NameMaxLength = 300;
    public const int ShortNameMaxLength = 60;
    public const int AliasesMaxLength = 500;
    public const int LoincMaxLength = 20;
    public const int MethodMaxLength = 200;
    public const int ClinicalInformationMaxLength = 500;
    public const int LetterKeyMaxLength = 5;
    public const int FormulaMaxLength = 1000;
    public const int CodedValueMaxLength = 60;
    public const int PrintedIntervalMaxLength = 200;
    public const int SourceMaxLength = 300;
    public const int DescriptionMaxLength = 500;

    // The bounds of the number boxes, as the workstation's spinners hold them.
    public const int TurnaroundHoursMax = 8760;
    public const int UrgentMinutesMax = 10080;
    public const int FastingHoursMax = 48;
    public const int StabilityHoursMax = 8760;
    public const int DisplayOrderMax = 9999;
    public const int DecimalsMax = 6;
    public const int IndentMax = 5;
    public const int DeltaWindowMax = 3650;
    public const int AgeMax = 40_000;
    public const int DrawOrderMax = 99;
    public const int DepartmentOrderMax = 999;

    /// <summary>The units an age band is typed in, in days: days, months, years.</summary>
    public static IReadOnlyList<int> AgeUnits { get; } = [1, 30, 365];

    // ---- Shared ---------------------------------------------------------------------------------

    /// <summary>The first box of <paramref name="boxes"/> whose text is no number the editor can save.</summary>
    private static FieldRefusal? Unreadable(params (string Field, string Text)[] boxes) =>
        boxes.FirstOrDefault(b => TypedNumber.IsUnreadable(b.Text)) is { Field: not null } box
            ? new FieldRefusal(box.Field, "Result_NotANumber", Typed: box.Text.Trim())
            : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// A text the editor edits in French only. While the French is unchanged the stored text is
    /// kept whole, translations included; rebuilding it from the one box dropped the Arabic and
    /// English on every save, even when nothing was touched.
    /// </summary>
    public static MultilingualText? FrenchEdited(MultilingualText? stored, string? french) =>
        Blank(french) is not { } typed ? null
        : stored is not null && string.Equals(stored.Fr, typed, StringComparison.Ordinal) ? stored
        : new MultilingualText(typed);

    /// <summary>
    /// A box left at the stored value returns that value, so a stored zero is not saved back as
    /// « not set »; otherwise a value above zero, or none.
    /// </summary>
    public static int? IntOrStored(int value, int? stored, int max) =>
        stored is { } kept && value == Math.Clamp(kept, 0, max) ? stored : value > 0 ? value : null;

    private static string Edit(decimal? value, int decimals, IFormatProvider? culture) =>
        value is { } number ? TypedNumber.ForEditing(number, decimals, culture) : string.Empty;

    private static string Fixed(decimal? value, string format, IFormatProvider? culture) =>
        value?.ToString(format, culture) ?? string.Empty;

    // ---- Analysis --------------------------------------------------------------------------------

    /// <summary>
    /// The editor opened on what is stored — the whole stored analysis when editing, never the
    /// grid's summary, which saved blanks over every field it does not carry — or on a new
    /// analysis's defaults. The laboratory price in force fills the price; a first price starts
    /// from the letter-key value known on any analysis.
    /// </summary>
    public static AnalysisForm FormOf(AnalysisRow? existing, AnalysisRequest? stored, TariffRow? labTariff,
                                      decimal? knownLetterKeyValue, IFormatProvider? culture = null)
    {
        var kept = existing is null ? null : stored;

        return new AnalysisForm
        {
            Code = existing?.Code ?? string.Empty,
            NameFr = existing?.Name.Fr ?? string.Empty,
            NameAr = existing?.Name.Ar ?? string.Empty,
            NameEn = existing?.Name.En ?? string.Empty,
            ShortName = kept?.ShortName?.Fr ?? string.Empty,
            Aliases = kept?.SearchAliases ?? string.Empty,
            DepartmentId = existing?.DepartmentId,
            SpecimenTypeId = existing?.SpecimenTypeId,
            ContainerTypeId = existing?.ContainerTypeId,
            Volume = Edit(kept?.RequiredVolumeMl, 0, culture),
            Loinc = kept?.LoincCode ?? string.Empty,
            ActCode = existing?.NationalActCode ?? string.Empty,
            Consent = existing?.ConsentRequirement ?? ConsentRequirement.None,
            Method = kept?.Method?.Fr ?? string.Empty,
            Reagent = kept?.ReagentReference ?? string.Empty,
            Accredited = existing?.IsAccredited ?? false,
            Subcontracted = existing?.IsSubcontracted ?? false,
            Notifiable = kept?.IsNotifiableDisease ?? false,
            SubcontractorSiteId = kept?.SubcontractorSiteId,
            RoutineTurnaroundHours = Math.Clamp(existing?.RoutineTurnaroundHours ?? 24, 0, TurnaroundHoursMax),
            UrgentTurnaroundMinutes = Math.Clamp(kept?.UrgentTurnaroundMinutes ?? 120, 0, UrgentMinutesMax),
            AvailableUrgently = kept?.IsAvailableUrgently ?? true,
            FastingHours = Math.Clamp(kept?.FastingHoursOverride ?? 0, 0, FastingHoursMax),
            StabilityRoomHours = Math.Clamp(kept?.StabilityAtRoomTemperatureHours ?? 0, 0, StabilityHoursMax),
            StabilityFridgeHours = Math.Clamp(kept?.StabilityRefrigeratedHours ?? 0, 0, StabilityHoursMax),
            DisplayOrder = Math.Clamp(kept?.DisplayOrder ?? 0, 0, DisplayOrderMax),
            ClinicalInformation = kept?.RequiredClinicalInformation?.Fr ?? string.Empty,
            Active = existing?.IsActive ?? true,
            LetterKey = labTariff?.LetterKey ?? NationalBiologyAct.BiologyLetterKey,
            Coefficient = Fixed(labTariff?.Coefficient, "0.##", culture),
            LetterKeyValue = Fixed(labTariff is { LetterKeyValue: > 0m } ? labTariff.LetterKeyValue : knownLetterKeyValue, "0.####", culture),
            Price = Fixed(labTariff?.Price, "0.00", culture)
        };
    }

    /// <summary>The laboratory price-list tariff in force: the one this editor prices, the latest started.</summary>
    public static TariffRow? LabTariff(IEnumerable<TariffRow> tariffs, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(tariffs);
        return tariffs.Where(t => t.PayerId is null && t.IsInForce(today)).MaxBy(t => t.ValidFrom);
    }

    /// <summary>
    /// The partner site follows the tick box: an analysis marked subcontracted with no partner is
    /// refused, and with no partner configured there is nothing to pick.
    /// </summary>
    public static bool SubcontractorEnabled(AnalysisForm form, int partnerSiteCount)
    {
        ArgumentNullException.ThrowIfNull(form);
        return form.Subcontracted && partnerSiteCount > 0;
    }

    /// <summary>
    /// What the analysis editor refuses before the service is asked: a box holding no number, the
    /// code, the French name, the department and the specimen, and a subcontracted analysis with no
    /// partner — saying which of the two problems it is: none chosen, or none configured at all.
    /// </summary>
    public static FieldRefusal? Check(AnalysisForm form, int partnerSiteCount)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (Unreadable(("volume", form.Volume), ("coefficient", form.Coefficient),
                       ("letterkeyvalue", form.LetterKeyValue), ("price", form.Price)) is { } unreadable)
            return unreadable;

        if (string.IsNullOrWhiteSpace(form.Code)) return new FieldRefusal("code", "Test_CodeRequired");
        if (string.IsNullOrWhiteSpace(form.NameFr)) return new FieldRefusal("namefr", "Test_NameFrenchRequired");
        if (form.DepartmentId is null || form.SpecimenTypeId is null)
            return new FieldRefusal("department", "Catalog_DepartmentAndSpecimenRequired");

        if (form.Subcontracted && form.SubcontractorSiteId is null)
            return new FieldRefusal("subcontractor",
                                    partnerSiteCount > 0 ? "Test_SubcontractorRequired" : "Catalog_NoPartnerSiteConfigured");

        return null;
    }

    /// <summary>The act the code box names, with its official coefficient, or that the list does not know it.</summary>
    public static ActInfo Act(string? typed, IReadOnlyCollection<NationalActRow> acts)
    {
        ArgumentNullException.ThrowIfNull(acts);

        var code = NationalBiologyAct.Normalize(typed);

        if (code is null || acts.Count == 0)
            return new ActInfo(code is null && acts.Count > 0 ? ActInfoKind.Hint : ActInfoKind.None, code);

        return acts.FirstOrDefault(a => a.Code == code) is { } act
            ? new ActInfo(ActInfoKind.Known, code, act)
            : new ActInfo(ActInfoKind.Unknown, code);
    }

    /// <summary>
    /// A code typed by hand that the nomenclature does not know — a slip, an old code, or the
    /// internal code typed in the wrong box: asked about before saving, the operator may keep it.
    /// The code to name in the question, or null when there is nothing to ask.
    /// </summary>
    public static string? UnknownActCode(AnalysisForm form, IReadOnlyCollection<NationalActRow> acts)
    {
        ArgumentNullException.ThrowIfNull(form);
        return Act(form.ActCode, acts) is { Kind: ActInfoKind.Unknown, Code: { } code } ? code : null;
    }

    /// <summary>
    /// The act picked from the nomenclature: its code in the box and, for whoever may price, the
    /// official coefficient for the laboratory price. A price that followed the old base follows the
    /// new one; a price the laboratory set apart from the base is left for the operator to decide.
    /// </summary>
    public static AnalysisForm WithAct(AnalysisForm form, NationalActRow act, bool canPrice, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(act);

        var picked = form with { ActCode = act.Code };
        if (!canPrice) return picked;

        var keyValue = TypedNumber.Parse(form.LetterKeyValue);
        var oldCoefficient = TypedNumber.Parse(form.Coefficient);
        var oldPrice = TypedNumber.Parse(form.Price);

        picked = picked with
        {
            LetterKey = act.LetterKey,
            Coefficient = TypedNumber.ForEditing(act.Coefficient, 0, culture)
        };

        if (keyValue is > 0m
            && (oldPrice is null
                || (oldCoefficient is { } coefficient && oldPrice == Money.Round(coefficient * keyValue.Value))))
            picked = picked with { Price = TypedNumber.ForEditing(Money.Round(act.Coefficient * keyValue.Value), 2, culture) };

        return picked;
    }

    /// <summary>
    /// The reimbursement base (coefficient × letter-key value) and what the patient bears above it,
    /// computed as they are typed: the excess is the figure a reception clerk is most often asked
    /// to explain.
    /// </summary>
    public static (decimal Reference, decimal Excess) Computed(string? coefficient, string? letterKeyValue, string? price)
    {
        var reference = Math.Round((TypedNumber.Parse(coefficient) ?? 0m) * (TypedNumber.Parse(letterKeyValue) ?? 0m), 2);
        return (reference, Math.Max(0m, (TypedNumber.Parse(price) ?? 0m) - reference));
    }

    /// <summary>
    /// The laboratory price as typed. A refusal on its field; or no request when there is nothing to
    /// save — the price left blank on an analysis with none yet, the price unchanged, or an
    /// operator who may not set prices.
    /// </summary>
    public static (FieldRefusal? Refusal, TariffRequest? Request) ReadPrice(AnalysisForm form, bool canPrice, TariffRow? labTariff)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (!canPrice) return (null, null);

        var price = TypedNumber.Parse(form.Price);

        if (price is null && string.IsNullOrWhiteSpace(form.Price) && labTariff is null) return (null, null);

        if (price is null or < 0m) return (new FieldRefusal("price", "Tariff_NegativePrice"), null);
        if (string.IsNullOrWhiteSpace(form.LetterKey)) return (new FieldRefusal("letterkey", "Tariff_LetterKeyRequired"), null);

        var letterKey = form.LetterKey.Trim().ToUpperInvariant();
        var coefficient = TypedNumber.Parse(form.Coefficient) ?? 0m;
        var letterKeyValue = TypedNumber.Parse(form.LetterKeyValue) ?? 0m;

        if (labTariff is not null
            && labTariff.LetterKey == letterKey
            && labTariff.Coefficient == coefficient
            && labTariff.LetterKeyValue == letterKeyValue
            && labTariff.Price == price.Value)
            return (null, null);

        return (null, new TariffRequest
        {
            AnalysisTestId = Guid.Empty,
            PayerId = null,
            ValidFrom = DateOnly.MinValue,
            LetterKey = letterKey,
            Coefficient = coefficient,
            LetterKeyValue = letterKeyValue,
            Price = price.Value,
            IsVatExempt = labTariff?.IsVatExempt ?? true,
            VatRate = labTariff?.VatRate ?? 0m
        });
    }

    /// <summary>
    /// The price saved with the analysis: from today, for the analysis just saved. A price set
    /// earlier today is corrected in place (true); otherwise the new price starts today and the
    /// previous one closes yesterday, so orders already billed keep their price.
    /// </summary>
    public static (TariffRequest Request, bool CorrectInPlace) PriceRevision(TariffRequest price, Guid analysisId,
                                                                           TariffRow? labTariff, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(price);
        return (price with { AnalysisTestId = analysisId, ValidFrom = today }, labTariff is not null && labTariff.ValidFrom == today);
    }

    /// <summary>
    /// The request saved, built on the stored analysis so a field the editor does not show (the
    /// execution days) is saved as it was rather than reset.
    /// </summary>
    public static AnalysisRequest ToRequest(AnalysisForm form, Guid? id, AnalysisRequest? stored)
    {
        ArgumentNullException.ThrowIfNull(form);

        var basis = stored ?? new AnalysisRequest
        {
            Code = string.Empty,
            Name = new MultilingualText(),
            DepartmentId = Guid.Empty,
            SpecimenTypeId = Guid.Empty
        };

        return basis with
        {
            Id = id,
            Code = form.Code.Trim().ToUpperInvariant(),
            Name = new MultilingualText(form.NameFr.Trim(), Blank(form.NameAr), Blank(form.NameEn)),
            ShortName = FrenchEdited(stored?.ShortName, form.ShortName),
            SearchAliases = Blank(form.Aliases),
            DepartmentId = form.DepartmentId!.Value,
            SpecimenTypeId = form.SpecimenTypeId!.Value,
            ContainerTypeId = form.ContainerTypeId,
            RequiredVolumeMl = TypedNumber.Parse(form.Volume),
            LoincCode = Blank(form.Loinc),
            NationalActCode = NationalBiologyAct.Normalize(form.ActCode),
            Method = FrenchEdited(stored?.Method, form.Method),
            ReagentReference = Blank(form.Reagent),
            IsAccredited = form.Accredited,
            IsSubcontracted = form.Subcontracted,
            SubcontractorSiteId = form.Subcontracted ? form.SubcontractorSiteId : null,
            RoutineTurnaroundHours = form.RoutineTurnaroundHours,
            UrgentTurnaroundMinutes = form.UrgentTurnaroundMinutes,
            IsAvailableUrgently = form.AvailableUrgently,
            FastingHoursOverride = IntOrStored(form.FastingHours, stored?.FastingHoursOverride, FastingHoursMax),
            StabilityAtRoomTemperatureHours = IntOrStored(form.StabilityRoomHours, stored?.StabilityAtRoomTemperatureHours, StabilityHoursMax),
            StabilityRefrigeratedHours = IntOrStored(form.StabilityFridgeHours, stored?.StabilityRefrigeratedHours, StabilityHoursMax),
            RequiredClinicalInformation = FrenchEdited(stored?.RequiredClinicalInformation, form.ClinicalInformation),
            ConsentRequirement = form.Consent,
            IsNotifiableDisease = form.Notifiable,
            IsActive = form.Active,
            DisplayOrder = form.DisplayOrder
        };
    }

    // ---- Parameter -------------------------------------------------------------------------------

    /// <summary>The parameter opened on what is stored — its allowed answers included — or a new one's defaults.</summary>
    public static ParameterForm FormOf(ParameterRow? existing, ParameterRequest? stored, IFormatProvider? culture = null)
    {
        var kept = existing is null ? null : stored;

        return new ParameterForm
        {
            Code = existing?.Code ?? string.Empty,
            ValueType = existing?.ValueType ?? ResultValueType.Numeric,
            UnitId = existing?.UnitId,
            Decimals = Math.Clamp(existing?.Decimals ?? 2, 0, DecimalsMax),
            NameFr = existing?.Name.Fr ?? string.Empty,
            NameAr = existing?.Name.Ar ?? string.Empty,
            NameEn = existing?.Name.En ?? string.Empty,
            ShortName = kept?.ShortName?.Fr ?? string.Empty,
            Loinc = kept?.LoincCode ?? string.Empty,
            Cv = Fixed(existing?.AnalyticalCvPercent, "0.##", culture),
            DisplayOrder = Math.Clamp(existing?.DisplayOrder ?? 0, 0, DisplayOrderMax),
            IndentLevel = Math.Clamp(existing?.IndentLevel ?? 0, 0, IndentMax),
            // Through the editing format: the culture's own text for a negative limit carries a
            // direction mark in Arabic, and "0.###" also rounded the stored value on the next save.
            MeasuringLow = Edit(existing?.MeasuringRangeLow, 0, culture),
            MeasuringHigh = Edit(existing?.MeasuringRangeHigh, 0, culture),
            TechnicalLow = Edit(existing?.TechnicalLimitLow, 0, culture),
            TechnicalHigh = Edit(existing?.TechnicalLimitHigh, 0, culture),
            CriticalLow = Edit(existing?.CriticalLow, 0, culture),
            CriticalHigh = Edit(existing?.CriticalHigh, 0, culture),
            Delta = Fixed(existing?.DeltaCheckPercent, "0.##", culture),
            DeltaWindowDays = Math.Clamp(existing?.DeltaCheckWindowDays ?? 0, 0, DeltaWindowMax),
            Formula = existing?.CalculationFormula ?? string.Empty,
            InternalOnly = existing?.IsInternalOnly ?? false,
            AlwaysShowOnReport = existing?.AlwaysShowOnReport ?? true,
            Active = existing?.IsActive ?? true,
            // The stored answers, so a coded parameter is not saved with a list to retype from memory.
            Options = [.. (kept?.CodedOptions ?? []).Select(o => new CodedOptionLine(o.Code, o.Label.Fr, o.Label.Ar ?? string.Empty,
                                                                                     o.IsExpectedNormal, o.IsAbnormal, o.IsCritical))]
        };
    }

    /// <summary>
    /// The fields the value type uses: limits, unit, decimals, delta check and formula for a number,
    /// the answer list for a coded or semi-quantitative one, nothing printed for a heading.
    /// </summary>
    public static ParameterFields Fields(ResultValueType type) =>
        new(type is ResultValueType.Numeric or ResultValueType.NumericWithOperator,
            type is ResultValueType.Coded or ResultValueType.SemiQuantitative,
            type == ResultValueType.Heading);

    /// <summary>The value type is frozen once a result exists: reading an old column another way makes the history unreadable.</summary>
    public static bool ValueTypeEditable(ParameterRow? existing) => existing is null || !existing.HasResults;

    /// <summary>
    /// The list a qualitative parameter almost always needs, offered while the list is empty: typed
    /// by hand for every serology, a laboratory ends up with three spellings of the same answer.
    /// </summary>
    public static IReadOnlyList<CodedOptionLine> NegativePositive(ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return [new CodedOptionLine("NEG", text("Catalog.OptionNegative"), "سالب", ExpectedNormal: true),
                new CodedOptionLine("POS", text("Catalog.OptionPositive"), "موجب", Abnormal: true)];
    }

    /// <summary>The codes a calculation formula cites, in brackets: « [HB]*3 ».</summary>
    public static IReadOnlyList<string> FormulaReferences(string? formula) =>
        string.IsNullOrWhiteSpace(formula)
            ? []
            : [.. FormulaReference().Matches(formula).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase)];

    [GeneratedRegex(@"\[([A-Za-z0-9_]+)\]")]
    private static partial Regex FormulaReference();

    /// <summary>
    /// What the parameter editor refuses before the service is asked: a number box holding no
    /// number (only those the value type uses), the code, the French name, a coded parameter
    /// without an answer, and a formula citing a code that is neither this parameter nor a sibling —
    /// said while the formula is still in front of the operator.
    /// </summary>
    public static FieldRefusal? Check(ParameterForm form, IEnumerable<string> siblingCodes)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(siblingCodes);

        if (Fields(form.ValueType).Numeric
            && Unreadable(("cv", form.Cv), ("measuringlow", form.MeasuringLow), ("measuringhigh", form.MeasuringHigh),
                          ("technicallow", form.TechnicalLow), ("technicalhigh", form.TechnicalHigh),
                          ("criticallow", form.CriticalLow), ("criticalhigh", form.CriticalHigh), ("delta", form.Delta)) is { } unreadable)
            return unreadable;

        if (string.IsNullOrWhiteSpace(form.Code)) return new FieldRefusal("code", "Parameter_CodeRequired");
        if (string.IsNullOrWhiteSpace(form.NameFr)) return new FieldRefusal("namefr", "Parameter_NameFrenchRequired");

        if (Fields(form.ValueType).Coded && ReadOptions(form.Options, null).Count == 0)
            return new FieldRefusal("options", "Parameter_CodedNeedsOptions");

        if (Blank(form.Formula) is { } formula)
        {
            var known = siblingCodes.Append(form.Code.Trim().ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = FormulaReferences(formula).Where(code => !known.Contains(code)).ToList();

            if (unknown.Count > 0)
                return new FieldRefusal("formula", "Parameter_FormulaUnknownReference", Typed: string.Join(", ", unknown));
        }

        return null;
    }

    /// <summary>
    /// The answers as listed, in their order: a line without a code or a label is left out; a
    /// stored answer whose French and Arabic are unchanged keeps its English label rather than
    /// losing it on save — the list has no English column.
    /// </summary>
    public static IReadOnlyList<CodedOptionRequest> ReadOptions(IEnumerable<CodedOptionLine> lines, ParameterRequest? stored)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var options = new List<CodedOptionRequest>();
        var order = 1;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Code) || string.IsNullOrWhiteSpace(line.LabelFr)) continue;

            var code = line.Code.Trim().ToUpperInvariant();
            var text = new MultilingualText(line.LabelFr.Trim(), Blank(line.LabelAr));

            if (stored?.CodedOptions.FirstOrDefault(o => string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase)) is { } kept
                && kept.Label.Fr == text.Fr && kept.Label.Ar == text.Ar)
                text = kept.Label;

            options.Add(new CodedOptionRequest(code, text, order++, line.Abnormal, line.Critical, line.ExpectedNormal));
        }

        return options;
    }

    /// <summary>The request saved, built on the stored parameter so a field the editor does not show is saved as it was.</summary>
    public static ParameterRequest ToRequest(ParameterForm form, Guid? id, Guid analysisId, ParameterRequest? stored)
    {
        ArgumentNullException.ThrowIfNull(form);

        var basis = stored ?? new ParameterRequest
        {
            AnalysisTestId = analysisId,
            Code = string.Empty,
            Name = new MultilingualText(),
            ValueType = form.ValueType
        };

        return basis with
        {
            Id = id,
            AnalysisTestId = analysisId,
            Code = form.Code.Trim().ToUpperInvariant(),
            Name = new MultilingualText(form.NameFr.Trim(), Blank(form.NameAr), Blank(form.NameEn)),
            ShortName = FrenchEdited(stored?.ShortName, form.ShortName),
            ValueType = form.ValueType,
            LoincCode = Blank(form.Loinc),
            UnitId = form.UnitId,
            Decimals = form.Decimals,
            MeasuringRangeLow = TypedNumber.Parse(form.MeasuringLow),
            MeasuringRangeHigh = TypedNumber.Parse(form.MeasuringHigh),
            TechnicalLimitLow = TypedNumber.Parse(form.TechnicalLow),
            TechnicalLimitHigh = TypedNumber.Parse(form.TechnicalHigh),
            CriticalLow = TypedNumber.Parse(form.CriticalLow),
            CriticalHigh = TypedNumber.Parse(form.CriticalHigh),
            AnalyticalCvPercent = TypedNumber.Parse(form.Cv),
            DeltaCheckPercent = TypedNumber.Parse(form.Delta),
            DeltaCheckWindowDays = form.DeltaWindowDays > 0 ? form.DeltaWindowDays : null,
            CalculationFormula = Blank(form.Formula),
            IsInternalOnly = form.InternalOnly,
            AlwaysShowOnReport = form.AlwaysShowOnReport,
            DisplayOrder = form.DisplayOrder,
            IndentLevel = form.IndentLevel,
            IsActive = form.Active,
            CodedOptions = ReadOptions(form.Options, stored)
        };
    }

    // ---- Reference interval ------------------------------------------------------------------------

    /// <summary>
    /// The interval opened on what is stored, its age band in the unit it was typed in — for a band
    /// saved before the unit was kept, the largest unit that holds both bounds exactly, or the band
    /// would be re-saved truncated — or a new interval from today, its band in years.
    /// </summary>
    public static RangeForm FormOf(ReferenceRangeRow? existing, int decimals, DateOnly today, IFormatProvider? culture = null)
    {
        if (existing is null) return new RangeForm { ValidFrom = today };

        var divisor = existing.AgeUnitDays is { } typed and (1 or 30 or 365)
                      && (existing.AgeFromDays ?? 0) % typed == 0
                      && (existing.AgeToDays ?? 0) % typed == 0
            ? typed
            : ReferenceRange.AgeBandUnitDays(existing.AgeFromDays, existing.AgeToDays);

        divisor = divisor switch { 1 => 1, 30 => 30, _ => 365 };

        return new RangeForm
        {
            Gender = existing.AppliesToGender,
            Pregnancy = existing.AppliesToPregnancy,
            Trimester = existing.PregnancyTrimester is { } t and >= 1 and <= 3 ? t : null,
            AgeUnitDays = divisor,
            AgeFrom = Math.Clamp((existing.AgeFromDays ?? 0) / divisor, 0, AgeMax),
            AgeTo = Math.Clamp((existing.AgeToDays ?? 0) / divisor, 0, AgeMax),
            InstrumentId = existing.InstrumentId,
            // No thousands separator: the box is read back as typed, and "150 000" did not survive it.
            Lower = Edit(existing.LowerLimit, decimals, culture),
            Upper = Edit(existing.UpperLimit, decimals, culture),
            ExpectedCoded = existing.ExpectedCodedValue ?? string.Empty,
            DecisionThreshold = existing.IsDecisionThreshold,
            DisplayFr = existing.DisplayText?.Fr ?? string.Empty,
            DisplayAr = existing.DisplayText?.Ar ?? string.Empty,
            Source = existing.Source ?? string.Empty,
            LastReviewedOn = existing.LastReviewedOn == default(DateOnly) ? null : existing.LastReviewedOn,
            ValidFrom = existing.ValidFrom,
            ValidUntil = existing.ValidUntil,
            Active = existing.IsActive
        };
    }

    /// <summary>
    /// The number the resolver picks between two matching intervals by — trimester 8, pregnancy 4,
    /// instrument 3, sex 2, age 1 — shown so an operator building a pregnancy interval on top of a
    /// female one can see that it will win.
    /// </summary>
    public static int Specificity(RangeForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        return (form.Trimester is not null ? 8 : 0)
               + (form.Pregnancy is not null ? 4 : 0)
               + (form.InstrumentId is not null ? 3 : 0)
               + (form.Gender != Gender.Unknown ? 2 : 0)
               + (form.AgeFrom > 0 || form.AgeTo > 0 ? 1 : 0);
    }

    /// <summary>
    /// What the interval editor refuses before the service is asked: a limit holding no number, an
    /// interval with neither limit, coded answer nor printed text, a lower limit above the upper,
    /// and an age band that ends before it starts.
    /// </summary>
    public static FieldRefusal? Check(RangeForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (Unreadable(("lower", form.Lower), ("upper", form.Upper)) is { } unreadable) return unreadable;

        var lower = TypedNumber.Parse(form.Lower);
        var upper = TypedNumber.Parse(form.Upper);

        if (lower is null && upper is null && string.IsNullOrWhiteSpace(form.ExpectedCoded) && string.IsNullOrWhiteSpace(form.DisplayFr))
            return new FieldRefusal("lower", "Range_EmptyInterval");

        if (lower is { } low && upper is { } high && low > high) return new FieldRefusal("lower", "Range_LowerAboveUpper");

        var (from, to) = AgeBand(form);
        if (from is { } f && to is { } t && f >= t) return new FieldRefusal("agefrom", "Range_InvalidAgeBand");

        return null;
    }

    /// <summary>The band in days, as stored: typed in its unit, nothing for a bound left at zero.</summary>
    public static (int? FromDays, int? ToDays) AgeBand(RangeForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return (form.AgeFrom > 0 ? form.AgeFrom * form.AgeUnitDays : null, form.AgeTo > 0 ? form.AgeTo * form.AgeUnitDays : null);
    }

    public static ReferenceRangeRequest ToRequest(RangeForm form, Guid? id, Guid parameterId)
    {
        ArgumentNullException.ThrowIfNull(form);

        var (from, to) = AgeBand(form);

        return new ReferenceRangeRequest
        {
            Id = id,
            TestParameterId = parameterId,
            AppliesToGender = form.Gender,
            AgeFromDays = from,
            AgeToDays = to,
            AgeUnitDays = form.AgeUnitDays,
            AppliesToPregnancy = form.Pregnancy,
            PregnancyTrimester = form.Trimester,
            InstrumentId = form.InstrumentId,
            LowerLimit = TypedNumber.Parse(form.Lower),
            UpperLimit = TypedNumber.Parse(form.Upper),
            DisplayText = Blank(form.DisplayFr) is { } printed ? new MultilingualText(printed, Blank(form.DisplayAr)) : null,
            ExpectedCodedValue = Blank(form.ExpectedCoded),
            IsDecisionThreshold = form.DecisionThreshold,
            Source = Blank(form.Source),
            LastReviewedOn = form.LastReviewedOn,
            ValidFrom = form.ValidFrom,
            ValidUntil = form.ValidUntil,
            IsActive = form.Active
        };
    }

    /// <summary>
    /// A change saved on an interval returned another id: it opened a revision (the interval in
    /// force had interpreted results) rather than rewriting it — the operator's cue it did.
    /// </summary>
    public static bool Superseded(ReferenceRangeRow? edited, Guid saved) => edited is not null && saved != edited.Id;

    // ---- Tariff --------------------------------------------------------------------------------------

    /// <summary>
    /// The payers a tariff can be entered for: every active payer, plus any this analysis already
    /// has a tariff for — one since switched off still has its history. Offering only the latter
    /// meant the first tariff for a payer could never be entered.
    /// </summary>
    public static IReadOnlyList<(Guid Id, string Display)> Payers(IEnumerable<TariffPayerOption> options,
                                                                  IEnumerable<TariffRow> existing, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(existing);

        var active = options.ToList();
        return [.. active.Select(p => (p.Id, $"{p.Code} — {p.Name.For(language)}"))
                         .Concat(existing.Where(t => t.PayerId is not null && active.All(p => p.Id != t.PayerId))
                                         .GroupBy(t => t.PayerId!.Value)
                                         .Select(g => (g.Key, g.First().PayerName ?? g.Key.ToString())))];
    }

    /// <summary>
    /// The letter-key value is national, the same for every analysis: the most recent one known on
    /// this analysis, else the latest anywhere, so a new tariff does not start with a base of zero
    /// and the whole price shown as excess.
    /// </summary>
    public static decimal? KnownLetterKeyValue(TariffRow? editing, IEnumerable<TariffRow> existing, decimal? national)
    {
        ArgumentNullException.ThrowIfNull(existing);

        return editing?.LetterKeyValue
               ?? existing.Where(t => t.LetterKeyValue > 0m).OrderByDescending(t => t.ValidFrom)
                          .Select(t => (decimal?)t.LetterKeyValue).FirstOrDefault()
               ?? national;
    }

    /// <summary>The tariff this revision closes (the one corrected, when correcting), or none.</summary>
    public static TariffRow? CurrentTariff(TariffRow? editing, IEnumerable<TariffRow> existing, Guid? payerId)
    {
        ArgumentNullException.ThrowIfNull(existing);
        return editing ?? existing.FirstOrDefault(t => t.PayerId == payerId && t.ValidUntil is null);
    }

    /// <summary>
    /// A new version starts after the one it closes: backdating behind it is refused, so the date
    /// moves to the day after when the payer chosen has a later tariff. A correction keeps its date.
    /// </summary>
    public static DateOnly ValidFrom(DateOnly typed, TariffRow? current, bool correcting) =>
        !correcting && current is not null && typed <= current.ValidFrom ? current.ValidFrom.AddDays(1) : typed;

    /// <summary>
    /// The revision opened: on the tariff corrected, or for the laboratory list from today with
    /// the analysis's coefficient and price in force.
    /// </summary>
    public static TariffForm FormOf(AnalysisRow analysis, IReadOnlyList<TariffRow> existing, DateOnly today,
                                    TariffRow? editing, decimal? national, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(existing);

        var form = new TariffForm
        {
            PayerId = editing?.PayerId,
            ValidFrom = editing?.ValidFrom ?? today,
            LetterKey = editing?.LetterKey ?? NationalBiologyAct.BiologyLetterKey,
            Coefficient = Fixed(editing?.Coefficient ?? analysis.CurrentCoefficient, "0.##", culture),
            LetterKeyValue = Fixed(KnownLetterKeyValue(editing, existing, national), "0.####", culture),
            Price = Fixed(editing?.Price ?? analysis.CurrentPrice, "0.00", culture),
            VatExempt = editing?.IsVatExempt ?? true,
            VatRate = editing is { IsVatExempt: false } ? editing.VatRate.ToString("0.####", culture) : string.Empty
        };

        return form with { ValidFrom = ValidFrom(form.ValidFrom, CurrentTariff(editing, existing, form.PayerId), editing is not null) };
    }

    /// <summary>
    /// What the tariff editor refuses before the service is asked: a box holding no number, a
    /// price missing or negative, no letter key, and a taxed tariff without its rate.
    /// </summary>
    public static FieldRefusal? Check(TariffForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (Unreadable(("coefficient", form.Coefficient), ("letterkeyvalue", form.LetterKeyValue), ("price", form.Price),
                       ("vatrate", form.VatExempt ? string.Empty : form.VatRate)) is { } unreadable)
            return unreadable;

        if (TypedNumber.Parse(form.Price) is null or < 0m) return new FieldRefusal("price", "Tariff_NegativePrice");
        if (string.IsNullOrWhiteSpace(form.LetterKey)) return new FieldRefusal("letterkey", "Tariff_LetterKeyRequired");
        if (!form.VatExempt && TypedNumber.Parse(form.VatRate) is null or <= 0m) return new FieldRefusal("vatrate", "Tariff_VatRateRequired");

        return null;
    }

    public static TariffRequest ToRequest(TariffForm form, Guid analysisId)
    {
        ArgumentNullException.ThrowIfNull(form);

        return new TariffRequest
        {
            AnalysisTestId = analysisId,
            PayerId = form.PayerId,
            ValidFrom = form.ValidFrom,
            LetterKey = form.LetterKey.Trim().ToUpperInvariant(),
            Coefficient = TypedNumber.Parse(form.Coefficient) ?? 0m,
            LetterKeyValue = TypedNumber.Parse(form.LetterKeyValue) ?? 0m,
            Price = TypedNumber.Parse(form.Price) ?? 0m,
            IsVatExempt = form.VatExempt,
            VatRate = form.VatExempt ? 0m : TypedNumber.Parse(form.VatRate) ?? 0m
        };
    }

    // ---- Panel ---------------------------------------------------------------------------------------

    /// <summary>
    /// Only orderable analyses are offered as members: a panel built on an analysis with no tariff
    /// or no parameter fails at registration, on a component the operator did not pick.
    /// </summary>
    public static IReadOnlyList<AnalysisRow> Candidates(IEnumerable<AnalysisRow> analyses)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        return [.. analyses.Where(a => a.IsOrderable)];
    }

    /// <summary>The panel opened on what is stored, or a new one's defaults. Its search aliases are not read back.</summary>
    public static PanelForm FormOf(PanelRow? existing, IFormatProvider? culture = null) => new()
    {
        Code = existing?.Code ?? string.Empty,
        NameFr = existing?.Name.Fr ?? string.Empty,
        NameAr = existing?.Name.Ar ?? string.Empty,
        NameEn = existing?.Name.En ?? string.Empty,
        PackagePrice = Fixed(existing?.PackagePrice, "0.00", culture),
        DisplayOrder = Math.Clamp(existing?.DisplayOrder ?? 0, 0, DisplayOrderMax),
        ItemizeOnInvoice = existing?.ItemizeOnInvoice ?? true,
        GroupOnReport = existing?.GroupOnReport ?? false,
        Active = existing?.IsActive ?? true,
        Members = [.. (existing?.Members ?? []).Select(m => new PanelMemberRequest(m.AnalysisTestId, m.IsOptional))]
    };

    /// <summary>
    /// How many members are ticked and what the bundle costs bought one by one — the optional ones
    /// left out, as they are not added when the panel is selected — against the package price: a
    /// package above the sum of its parts is almost always a typing slip.
    /// </summary>
    public static (int Count, decimal Total, bool PackageAbove) Total(IEnumerable<AnalysisRow> candidates, PanelForm form)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(form);

        var prices = candidates.ToDictionary(c => c.Id, c => c.CurrentPrice ?? 0m);
        var ticked = form.Members.Where(m => prices.ContainsKey(m.AnalysisTestId)).ToList();
        var total = ticked.Where(m => !m.IsOptional).Sum(m => prices[m.AnalysisTestId]);

        return (ticked.Count, total, TypedNumber.Parse(form.PackagePrice) is { } package && package > total);
    }

    /// <summary>
    /// What the panel editor refuses before the service is asked: a package price holding no
    /// number, the code, the French name, a panel of nothing and a panel of optional members only.
    /// </summary>
    public static FieldRefusal? Check(PanelForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (Unreadable(("package", form.PackagePrice)) is { } unreadable) return unreadable;
        if (string.IsNullOrWhiteSpace(form.Code)) return new FieldRefusal("code", "Panel_CodeRequired");
        if (string.IsNullOrWhiteSpace(form.NameFr)) return new FieldRefusal("namefr", "Panel_NameFrenchRequired");
        if (form.Members.Count == 0) return new FieldRefusal("members", "Panel_AtLeastOneAnalysisRequired");
        if (form.Members.All(m => m.IsOptional)) return new FieldRefusal("members", "Panel_AllMembersOptional");
        return null;
    }

    public static PanelRequest ToRequest(PanelForm form, Guid? id)
    {
        ArgumentNullException.ThrowIfNull(form);

        return new PanelRequest
        {
            Id = id,
            Code = form.Code.Trim().ToUpperInvariant(),
            Name = new MultilingualText(form.NameFr.Trim(), Blank(form.NameAr), Blank(form.NameEn)),
            SearchAliases = Blank(form.Aliases),
            PackagePrice = TypedNumber.Parse(form.PackagePrice),
            ItemizeOnInvoice = form.ItemizeOnInvoice,
            GroupOnReport = form.GroupOnReport,
            DisplayOrder = form.DisplayOrder,
            IsActive = form.Active,
            Members = [.. form.Members]
        };
    }

    // ---- Reflex rule -----------------------------------------------------------------------------------

    /// <summary>
    /// The boxes a condition reads — one threshold, two, or a coded answer — and the sentence that
    /// says what it means, so a « greater than » is not saved with an upper bound it never reads.
    /// </summary>
    public static ReflexFields Fields(ReflexCondition condition)
    {
        var numeric = condition != ReflexCondition.EqualsCoded;

        return new ReflexFields(numeric, condition is ReflexCondition.Between or ReflexCondition.Outside, !numeric, condition switch
        {
            ReflexCondition.GreaterThan => "Catalog.ReflexGreaterNotice",
            ReflexCondition.LessThan => "Catalog.ReflexLessNotice",
            ReflexCondition.Between => "Catalog.ReflexBetweenNotice",
            ReflexCondition.Outside => "Catalog.ReflexOutsideNotice",
            _ => "Catalog.ReflexCodedNotice"
        });
    }

    /// <summary>A new condition chosen: the boxes it does not read are emptied, as they are turned off.</summary>
    public static ReflexForm WithCondition(ReflexForm form, ReflexCondition condition)
    {
        ArgumentNullException.ThrowIfNull(form);

        var fields = Fields(condition);
        return form with
        {
            Condition = condition,
            Threshold = fields.Threshold ? form.Threshold : string.Empty,
            ThresholdUpper = fields.ThresholdUpper ? form.ThresholdUpper : string.Empty,
            CodedValue = fields.CodedValue ? form.CodedValue : string.Empty
        };
    }

    /// <summary>The parameters that can trigger a rule: the active ones, headings carrying no result left out.</summary>
    public static IReadOnlyList<ParameterRow> TriggerParameters(IEnumerable<ParameterRow> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return [.. parameters.Where(p => p.IsActive && p.ValueType != ResultValueType.Heading)];
    }

    /// <summary>The parameter kept when the trigger's analysis changes: the one chosen if it is listed, else the first.</summary>
    public static Guid? TriggerParameter(IReadOnlyList<ParameterRow> offered, Guid? chosen)
    {
        ArgumentNullException.ThrowIfNull(offered);
        return offered.FirstOrDefault(p => p.Id == chosen)?.Id ?? (offered.Count > 0 ? offered[0].Id : null);
    }

    /// <summary>
    /// The rule opened on what is stored — the trigger picked through its analysis, as a biologist
    /// thinks of it — or a new one's defaults: billed, not automatic.
    /// </summary>
    public static ReflexForm FormOf(ReflexRuleRow? existing, IReadOnlyList<AnalysisRow> analyses, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(analyses);

        Guid? first = analyses.Count > 0 ? analyses[0].Id : null;

        var form = new ReflexForm
        {
            TriggerAnalysisId = existing is null ? first
                : analyses.FirstOrDefault(a => a.Code == existing.TriggerAnalysisCode)?.Id ?? first,
            TriggerParameterId = existing?.TriggerParameterId,
            Threshold = Fixed(existing?.Threshold, "0.###", culture),
            ThresholdUpper = Fixed(existing?.ThresholdUpper, "0.###", culture),
            CodedValue = existing?.TriggerCodedValue ?? string.Empty,
            TargetAnalysisId = existing is null ? first
                : analyses.FirstOrDefault(a => a.Code == existing.AnalysisToAddCode)?.Id ?? first,
            DescriptionFr = existing?.Description.Fr ?? string.Empty,
            DescriptionAr = existing?.Description.Ar ?? string.Empty,
            AddAutomatically = existing?.AddAutomatically ?? false,
            Billable = existing?.IsBillable ?? true,
            Active = existing?.IsActive ?? true
        };

        return WithCondition(form, existing?.Condition ?? ReflexCondition.GreaterThan);
    }

    /// <summary>
    /// What the reflex editor refuses before the service is asked: a threshold holding no number,
    /// no trigger parameter, no analysis to add, no description, and the thresholds or the coded
    /// answer the condition needs.
    /// </summary>
    public static FieldRefusal? Check(ReflexForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        var fields = Fields(form.Condition);

        if (Unreadable(("threshold", fields.Threshold ? form.Threshold : string.Empty),
                       ("thresholdupper", fields.ThresholdUpper ? form.ThresholdUpper : string.Empty)) is { } unreadable)
            return unreadable;

        if (form.TriggerParameterId is null || form.TriggerParameterId == Guid.Empty)
            return new FieldRefusal("parameter", "Catalog_TriggerParameterRequired");
        if (form.TargetAnalysisId is null) return new FieldRefusal("target", "Catalog_ReflexTargetRequired");
        if (string.IsNullOrWhiteSpace(form.DescriptionFr)) return new FieldRefusal("descriptionfr", "Reflex_DescriptionRequired");

        var threshold = TypedNumber.Parse(form.Threshold);
        var upper = TypedNumber.Parse(form.ThresholdUpper);

        return form.Condition switch
        {
            ReflexCondition.GreaterThan or ReflexCondition.LessThan when threshold is null
                => new FieldRefusal("threshold", "Reflex_ThresholdRequired"),
            ReflexCondition.Between or ReflexCondition.Outside when threshold is null || upper is null
                => new FieldRefusal("threshold", "Reflex_TwoThresholdsRequired"),
            ReflexCondition.EqualsCoded when string.IsNullOrWhiteSpace(form.CodedValue)
                => new FieldRefusal("codedvalue", "Reflex_CodedValueRequired"),
            _ => null
        };
    }

    public static ReflexRuleRequest ToRequest(ReflexForm form, Guid? id)
    {
        ArgumentNullException.ThrowIfNull(form);

        return new ReflexRuleRequest
        {
            Id = id,
            TriggerParameterId = form.TriggerParameterId!.Value,
            AnalysisToAddId = form.TargetAnalysisId!.Value,
            Description = new MultilingualText(form.DescriptionFr.Trim(), Blank(form.DescriptionAr)),
            Condition = form.Condition,
            Threshold = TypedNumber.Parse(form.Threshold),
            ThresholdUpper = TypedNumber.Parse(form.ThresholdUpper),
            TriggerCodedValue = Blank(form.CodedValue),
            AddAutomatically = form.AddAutomatically,
            IsBillable = form.Billable,
            IsActive = form.Active
        };
    }

    // ---- Nomenclature ----------------------------------------------------------------------------------

    /// <summary>The longest code a list takes: twenty characters for a department, thirty for the others.</summary>
    public static int CodeMaxLengthOf(NomenclatureKind kind) => kind == NomenclatureKind.Department ? 20 : CodeMaxLength;

    /// <summary>A unit's symbol is forty characters at most; a name, three hundred.</summary>
    public static int NameMaxLengthOf(NomenclatureKind kind) => kind == NomenclatureKind.MeasurementUnit ? 40 : NameMaxLength;

    public static NomenclatureForm FormOf(NomenclatureRow? existing, IFormatProvider? culture = null) => new()
    {
        Code = existing?.Code ?? string.Empty,
        NameFr = existing?.Name.Fr ?? string.Empty,
        NameAr = existing?.Name.Ar ?? string.Empty,
        NameEn = existing?.Name.En ?? string.Empty,
        Active = existing?.IsActive ?? true,
        DisplayOrder = Math.Clamp(existing?.DisplayOrder ?? 0, 0, DepartmentOrderMax),
        InAccreditationScope = existing?.IsInAccreditationScope ?? false,
        FastingHours = Math.Clamp(existing?.FastingHours ?? 0, 0, FastingHoursMax),
        CollectionInstructions = existing?.CollectionInstructions?.Fr ?? string.Empty,
        PatientInstructions = existing?.PatientInstructions?.Fr ?? string.Empty,
        CapColour = existing?.CapColourHex ?? "#FFFFFF",
        Additive = existing?.Additive?.Fr ?? string.Empty,
        NominalVolume = Fixed(existing?.NominalVolumeMl, "0.#", culture),
        MinimumVolume = Fixed(existing?.MinimumVolumeMl, "0.#", culture),
        DrawOrder = Math.Clamp(existing?.DrawOrder ?? 0, 0, DrawOrderMax),
        ProtectFromLight = existing?.RequiresProtectionFromLight ?? false,
        IceTransport = existing?.RequiresIceTransport ?? false,
        Symbol = existing?.DisplaySymbol ?? string.Empty,
        SymbolArabic = existing?.DisplaySymbolArabic ?? string.Empty,
        Ucum = existing?.UcumCode ?? string.Empty,
        FactorToSi = Fixed(existing?.FactorToSi, "0.######", culture),
        SiUnit = existing?.SiUnitCode ?? string.Empty
    };

    /// <summary>
    /// What the list editor refuses before the service is asked: a box holding no number, the code,
    /// a unit's symbol or another entry's French name, and a tube without its draw order.
    /// </summary>
    public static FieldRefusal? Check(NomenclatureKind kind, NomenclatureForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        var numbers = kind switch
        {
            NomenclatureKind.MeasurementUnit => Unreadable(("factor", form.FactorToSi)),
            NomenclatureKind.ContainerType => Unreadable(("nominalvolume", form.NominalVolume), ("minimumvolume", form.MinimumVolume)),
            _ => null
        };
        if (numbers is not null) return numbers;

        if (string.IsNullOrWhiteSpace(form.Code)) return new FieldRefusal("code", "Nomenclature_CodeRequired");

        if (kind == NomenclatureKind.MeasurementUnit)
        {
            if (string.IsNullOrWhiteSpace(form.Symbol)) return new FieldRefusal("symbol", "Unit_SymbolRequired");
        }
        else if (string.IsNullOrWhiteSpace(form.NameFr))
        {
            return new FieldRefusal("namefr", "Nomenclature_NameFrenchRequired");
        }

        return kind == NomenclatureKind.ContainerType && form.DrawOrder <= 0
            ? new FieldRefusal("draworder", "Container_DrawOrderRequired")
            : null;
    }

    /// <summary>
    /// The request saved. A unit has no translated name: « mg/dL » is the same in every language,
    /// so its symbol is its name; a field the list does not have is left at its default.
    /// </summary>
    public static NomenclatureRequest ToRequest(NomenclatureKind kind, NomenclatureForm form, Guid? id)
    {
        ArgumentNullException.ThrowIfNull(form);

        var unit = kind == NomenclatureKind.MeasurementUnit;
        var department = kind == NomenclatureKind.Department;
        var specimen = kind == NomenclatureKind.SpecimenType;
        var container = kind == NomenclatureKind.ContainerType;

        return new NomenclatureRequest
        {
            Id = id,
            Code = form.Code.Trim().ToUpperInvariant(),
            Name = unit
                ? new MultilingualText(form.Symbol.Trim(), Blank(form.SymbolArabic))
                : new MultilingualText(form.NameFr.Trim(), Blank(form.NameAr), Blank(form.NameEn)),

            DisplayOrder = department ? form.DisplayOrder : 0,
            IsInAccreditationScope = department && form.InAccreditationScope,
            SupervisorUserId = null,

            CollectionInstructions = specimen && Blank(form.CollectionInstructions) is { } collection ? new MultilingualText(collection) : null,
            PatientInstructions = specimen && Blank(form.PatientInstructions) is { } patient ? new MultilingualText(patient) : null,
            FastingHours = specimen ? form.FastingHours : 0,

            CapColourHex = container ? form.CapColour.Trim().ToUpperInvariant() : "#FFFFFF",
            Additive = container && Blank(form.Additive) is { } additive ? new MultilingualText(additive) : null,
            NominalVolumeMl = container ? TypedNumber.Parse(form.NominalVolume) : null,
            MinimumVolumeMl = container ? TypedNumber.Parse(form.MinimumVolume) : null,
            DrawOrder = container ? form.DrawOrder : 0,
            RequiresProtectionFromLight = container && form.ProtectFromLight,
            RequiresIceTransport = container && form.IceTransport,

            DisplaySymbol = unit ? form.Symbol.Trim() : string.Empty,
            DisplaySymbolArabic = unit ? Blank(form.SymbolArabic) : null,
            UcumCode = unit ? Blank(form.Ucum) : null,
            FactorToSi = unit ? TypedNumber.Parse(form.FactorToSi) : null,
            SiUnitCode = unit ? Blank(form.SiUnit) : null,

            IsActive = form.Active
        };
    }

    /// <summary>Whether a typed cap colour can be painted, for the swatch beside the box.</summary>
    public static string? Swatch(string? typed) =>
        typed?.Trim() is { } colour && (CatalogScreenRules.IsRenderableColour(colour) || IsShortHex(colour)) ? colour : null;

    private static bool IsShortHex(string colour) => colour is { Length: 4 } && colour[0] == '#' && colour[1..].All(Uri.IsHexDigit);
}
