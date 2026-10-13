using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Catalog;

/// <summary>
/// An orderable analysis (une analyse). This is what a prescriber writes and what reception
/// registers. It is deliberately separate from <see cref="TestParameter"/>, the measurand that
/// actually carries a result: an NFS is one orderable analysis with some twenty parameters, and
/// billing, turnaround and sample requirements belong to the order level, not the parameter level.
/// </summary>
public sealed class AnalysisTest : AuditableAggregateRoot
{
    private readonly List<TestParameter> _parameters = [];
    private readonly List<AnalysisTestTariff> _tariffs = [];

    private AnalysisTest() { }

    public AnalysisTest(string code, MultilingualText name, Guid departmentId, Guid specimenTypeId)
    {
        Code = code.Trim().ToUpperInvariant();
        Name = name;
        DepartmentId = departmentId;
        SpecimenTypeId = specimenTypeId;
    }

    /// <summary>Internal code used on worklists and by instrument mappings, e.g. "GLY", "NFS", "TSH".</summary>
    public string Code { get; private set; } = string.Empty;

    public MultilingualText Name { get; set; } = new();

    /// <summary>Short label that has to fit a report column or a barcode label.</summary>
    public MultilingualText? ShortName { get; set; }

    /// <summary>Synonyms the reception desk might type ("glycémie, sucre, glucose").</summary>
    public string? SearchAliases { get; set; }

    public Guid DepartmentId { get; set; }
    public Department? Department { get; private set; }

    public Guid SpecimenTypeId { get; set; }
    public SpecimenType? SpecimenType { get; private set; }

    /// <summary>Preferred tube. The collection worklist groups draws by container.</summary>
    public Guid? ContainerTypeId { get; set; }
    public ContainerType? ContainerType { get; private set; }

    /// <summary>Volume of material the method consumes, used to decide whether one tube is enough.</summary>
    public decimal? RequiredVolumeMl { get; set; }

    // ---- Coding -----------------------------------------------------------------------------

    /// <summary>LOINC code — kept for interoperability and for English-language exports.</summary>
    public string? LoincCode { get; set; }

    /// <summary>
    /// Code of the act in the Moroccan nomenclature of biology acts, as used on AMO claims.
    /// The reimbursable price is derived from the letter-key and coefficient in
    /// <see cref="AnalysisTestTariff"/>, not stored here.
    /// </summary>
    public string? NationalActCode { get; set; }

    // ---- Method and analytical characteristics (ISO 15189 §5.5) -----------------------------

    /// <summary>Analytical technique, printed on the report so a result can be compared over time.</summary>
    public MultilingualText? Method { get; set; }

    /// <summary>Reagent manufacturer and kit reference — part of the traceability chain.</summary>
    public string? ReagentReference { get; set; }

    /// <summary>Whether the method is covered by the laboratory's ISO 15189 accreditation.</summary>
    public bool IsAccredited { get; set; }

    /// <summary>Set when the analysis is always sent to a partner laboratory (sous-traitance).</summary>
    public bool IsSubcontracted { get; set; }
    public Guid? SubcontractorSiteId { get; set; }

    // ---- Turnaround and scheduling ----------------------------------------------------------

    /// <summary>Promised turnaround for a routine request, in hours, from sample reception.</summary>
    public int RoutineTurnaroundHours { get; set; } = 24;

    /// <summary>Promised turnaround for an urgent request, in minutes.</summary>
    public int UrgentTurnaroundMinutes { get; set; } = 120;

    /// <summary>Whether the analysis can be run at all outside a scheduled series.</summary>
    public bool IsAvailableUrgently { get; set; } = true;

    /// <summary>Days of the week the series runs, when the analysis is batched (1 = Monday).</summary>
    public string? ScheduledDaysCsv { get; set; }

    // ---- Pre-analytical rules ---------------------------------------------------------------

    /// <summary>Hours of fasting required; overrides the specimen type default when set.</summary>
    public int? FastingHoursOverride { get; set; }

    /// <summary>Stability of the primary sample at room temperature, in hours.</summary>
    public int? StabilityAtRoomTemperatureHours { get; set; }

    /// <summary>Stability refrigerated (2–8 °C), in hours.</summary>
    public int? StabilityRefrigeratedHours { get; set; }

    /// <summary>Clinical information the biologist needs before interpreting, prompted at registration.</summary>
    public MultilingualText? RequiredClinicalInformation { get; set; }

    /// <summary>
    /// Requires the patient's specific written consent — genetics, HIV serology, prenatal
    /// screening. Registration is blocked until the consent is on file.
    /// </summary>
    public ConsentRequirement ConsentRequirement { get; set; } = ConsentRequirement.None;

    /// <summary>
    /// Result is reportable to the health authorities (maladies à déclaration obligatoire).
    /// A positive result raises a declaration task rather than silently closing the file.
    /// </summary>
    public bool IsNotifiableDisease { get; set; }

    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; }

    public IReadOnlyCollection<TestParameter> Parameters => _parameters.AsReadOnly();
    public IReadOnlyCollection<AnalysisTestTariff> Tariffs => _tariffs.AsReadOnly();

    // Not ISoftDeletable. Retiring an analysis is <see cref="IsActive"/>, and that distinction is
    // not pedantry: LabOrderItem, TestParameter and TestPanelItem all hold a *required* reference
    // to this aggregate and carry no filter of their own, so a global "not deleted" filter here
    // filtered the principal out from under them. EF warns about exactly this shape, and the effect
    // is that a required navigation becomes an inner join: retiring a catalogue entry would have
    // dropped every historical order item that used it out of worklists, reports and invoices —
    // rows still in the database, silently absent from every query that resolved the analysis.
    //
    // A laboratory that stops offering a test needs it to stop being orderable and to keep
    // appearing on ten years of past orders. That is IsActive, which the order composer and the
    // catalogue screens already filter on deliberately. The soft-delete columns were never written
    // by anything — nothing in the application calls IRepository.SoftDelete — so they bought no
    // protection in exchange.

    // ---- Behaviour --------------------------------------------------------------------------

    public TestParameter AddParameter(TestParameter parameter)
    {
        if (_parameters.Any(p => string.Equals(p.Code, parameter.Code, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException($"Parameter '{parameter.Code}' already exists on analysis '{Code}'.");

        parameter.AttachTo(Id);
        if (parameter.DisplayOrder == 0)
            parameter.DisplayOrder = _parameters.Count == 0 ? 1 : _parameters.Max(p => p.DisplayOrder) + 1;
        _parameters.Add(parameter);
        return parameter;
    }

    public void AddTariff(AnalysisTestTariff tariff)
    {
        // Closing the open-ended tariff keeps price history contiguous and auditable.
        var current = _tariffs.FirstOrDefault(t => t.PayerId == tariff.PayerId && t.ValidUntil is null);
        if (current is not null && tariff.ValidFrom > current.ValidFrom)
            current.ValidUntil = tariff.ValidFrom.AddDays(-1);

        _tariffs.Add(tariff);
    }

    /// <summary>The tariff applicable to a payer on a date, falling back to the default price list.</summary>
    public AnalysisTestTariff? TariffFor(Guid? payerId, DateOnly on) =>
        _tariffs.Where(t => t.PayerId == payerId && t.IsValidOn(on)).OrderByDescending(t => t.ValidFrom).FirstOrDefault()
        ?? _tariffs.Where(t => t.PayerId is null && t.IsValidOn(on)).OrderByDescending(t => t.ValidFrom).FirstOrDefault();

    public int EffectiveFastingHours(int specimenTypeFastingHours) =>
        FastingHoursOverride ?? specimenTypeFastingHours;

    /// <summary>
    /// Expected completion time for a newly received sample. Urgent requests are promised in
    /// minutes; routine requests in hours from reception.
    /// </summary>
    public DateTimeOffset ExpectedCompletion(DateTimeOffset receivedAt, bool isUrgent) =>
        isUrgent && IsAvailableUrgently
            ? receivedAt.AddMinutes(UrgentTurnaroundMinutes)
            : receivedAt.AddHours(RoutineTurnaroundHours);

    /// <summary>
    /// Whether the primary sample is still within its stability window. Beyond it the result is
    /// not defensible and the sample must be recollected — a recurring ISO 15189 finding.
    /// </summary>
    public bool IsWithinStability(DateTimeOffset collectedAt, DateTimeOffset now, bool refrigerated)
    {
        var limit = refrigerated ? StabilityRefrigeratedHours : StabilityAtRoomTemperatureHours;
        return limit is null || now <= collectedAt.AddHours(limit.Value);
    }

    public Result Validate()
    {
        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(Code))
            errors.Add(Error.Validation("Test_CodeRequired"));
        if (string.IsNullOrWhiteSpace(Name.Fr))
            errors.Add(Error.Validation("Test_NameFrenchRequired"));
        if (_parameters.Count == 0)
            errors.Add(Error.Validation("Test_AtLeastOneParameterRequired"));
        if (IsSubcontracted && SubcontractorSiteId is null)
            errors.Add(Error.Validation("Test_SubcontractorRequired"));
        if (RoutineTurnaroundHours <= 0)
            errors.Add(Error.Validation("Test_TurnaroundMustBePositive"));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }
}

public enum ConsentRequirement
{
    /// <summary>Covered by the general diagnostic consent taken at registration.</summary>
    None = 0,

    /// <summary>Specific written consent for genetic analysis.</summary>
    Genetic = 1,

    /// <summary>Specific consent and counselling for HIV serology.</summary>
    Hiv = 2,

    /// <summary>Specific consent for prenatal screening.</summary>
    Prenatal = 3
}

/// <summary>
/// A price for one analysis, for one payer, over a period. Prices are versioned rather than
/// overwritten: an invoice issued last year must still reprint with the price that was charged.
/// </summary>
public sealed class AnalysisTestTariff : Entity, IAuditable
{
    private AnalysisTestTariff() { }

    public AnalysisTestTariff(Guid analysisTestId, Guid? payerId, DateOnly validFrom)
    {
        AnalysisTestId = analysisTestId;
        PayerId = payerId;
        ValidFrom = validFrom;
    }

    public Guid AnalysisTestId { get; private set; }

    /// <summary>Null means the laboratory's default price list, used for self-paying patients.</summary>
    public Guid? PayerId { get; private set; }

    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidUntil { get; set; }

    /// <summary>
    /// Letter-key of the act in the Moroccan biology nomenclature — "B" for biology acts.
    /// </summary>
    public string LetterKey { get; set; } = "B";

    /// <summary>
    /// Coefficient attached to the act. The reimbursement base is
    /// <c>Coefficient × LetterKeyValue</c>, the letter-key value being fixed nationally.
    /// </summary>
    public decimal Coefficient { get; set; }

    /// <summary>
    /// Value of the letter-key in dirham at <see cref="ValidFrom"/>, copied onto the tariff so the
    /// computed base is reproducible even after a national revaluation.
    /// </summary>
    public decimal LetterKeyValue { get; set; }

    /// <summary>
    /// Price actually charged. For AMO payers this equals the Tarif National de Référence; for the
    /// default list it is the laboratory's own price, which may exceed the TNR.
    /// </summary>
    public decimal Price { get; set; }

    /// <summary>
    /// Biology acts are exempt from VAT in Morocco. The flag exists because a laboratory also
    /// sells non-exempt items (certificates, consumables) through the same invoice.
    /// </summary>
    public bool IsVatExempt { get; set; } = true;
    public decimal VatRate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    public bool IsValidOn(DateOnly date) =>
        ValidFrom <= date && (ValidUntil is null || ValidUntil >= date);

    /// <summary>Reimbursement base under the national reference tariff.</summary>
    public Money NationalReferenceBase => Money.Of(Coefficient * LetterKeyValue);

    public Money ChargedPrice => Money.Of(Price);
}
