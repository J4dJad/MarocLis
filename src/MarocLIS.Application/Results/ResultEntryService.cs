using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Calculations;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;

namespace MarocLIS.Application.Results;

/// <summary>One row of the result-entry grid, with everything the technician needs visible at once.</summary>
public sealed record ResultEntryRow
{
    public required Guid TestResultId { get; init; }
    public required Guid LabOrderItemId { get; init; }
    public required Guid TestParameterId { get; init; }

    public required string AnalysisCode { get; init; }
    public required MultilingualText AnalysisName { get; init; }
    public required string ParameterCode { get; init; }
    public required MultilingualText ParameterName { get; init; }

    public ResultValueType ValueType { get; init; }
    public string? UnitSymbol { get; init; }
    public int Decimals { get; init; }
    public int IndentLevel { get; init; }
    public bool IsCalculated { get; init; }
    public bool IsReadOnly { get; init; }

    public string? CurrentValue { get; init; }
    public decimal? NumericValue { get; init; }
    public string? ValueOperator { get; init; }

    public string? ReferenceRangeText { get; init; }
    public decimal? LowerLimit { get; init; }
    public decimal? UpperLimit { get; init; }

    public decimal? PreviousValue { get; init; }
    public DateOnly? PreviousDate { get; init; }

    public ResultInterpretation Interpretation { get; init; }
    public ResultFlag Flags { get; init; }
    public TestResultStatus Status { get; init; }

    public IReadOnlyList<CodedOptionView> CodedOptions { get; init; } = [];

    public string? TechnicalComment { get; init; }

    /// <summary>Set when the sample this result came from was accepted with a documented defect.</summary>
    public SampleDefect SampleDefects { get; init; }

    /// <summary>A structural heading inside a panel — carries no value and takes no input.</summary>
    public bool IsHeading => ValueType == ResultValueType.Heading;

    /// <summary>
    /// Whether the value can be typed into. A calculated parameter is derived, a heading holds
    /// nothing, and a released result needs the revision path with a recorded reason.
    /// </summary>
    public bool IsEditable => !IsHeading && !IsCalculated && !IsReadOnly;
}

public sealed record CodedOptionView(string Code, MultilingualText Label, bool IsAbnormal, bool IsCritical);

/// <summary>The entry sheet for one order: patient context plus its result rows.</summary>
public sealed record ResultEntrySheet
{
    public required Guid LabOrderId { get; init; }
    public required string AccessionNumber { get; init; }
    public required string PatientName { get; init; }
    public string? PatientNameArabic { get; init; }
    public Gender Gender { get; init; }
    public int? AgeYears { get; init; }
    public DateOnly? DateOfBirth { get; init; }
    public bool IsPregnant { get; init; }
    public OrderPriority Priority { get; init; }
    public string? ClinicalInformation { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public required IReadOnlyList<ResultEntryRow> Rows { get; init; }
}

/// <summary>A value a technician submits for one parameter.</summary>
public sealed record ResultEntry(
    Guid TestResultId,
    string? RawValue,
    string? TechnicalComment = null,
    bool PrintTechnicalComment = false,
    decimal? DilutionFactor = null,
    Guid? InstrumentId = null,
    Guid? ReagentLotId = null);

public sealed record ResultEntryOutcome(
    int Accepted,
    IReadOnlyList<Error> Rejected,
    IReadOnlyList<CriticalFinding> CriticalFindings,
    IReadOnlyList<ReflexSuggestion> ReflexSuggestions);

public sealed record CriticalFinding(Guid TestResultId, string ParameterName, string Value,
                                     ResultInterpretation Interpretation);

public sealed record ReflexSuggestion(Guid TriggerResultId, Guid AnalysisToAddId,
                                      MultilingualText Description, bool AddAutomatically);

/// <summary>
/// Captures results and runs the post-analytical checks that make them interpretable.
/// <para>
/// The order of operations matters and is fixed here rather than left to the caller: a value is
/// stored, then converted and rounded to the parameter's precision, then calculated parameters are
/// recomputed in dependency order, then the reference interval is resolved from the patient's
/// actual demographics, then the delta check runs against the patient's own history, then critical
/// thresholds are evaluated. Each step depends on the previous one — interpreting before
/// recalculating would flag a stale LDL, and delta-checking before rounding would fire on noise.
/// </para>
/// </summary>
public sealed class ResultEntryService(
    IRepository<LabOrder> orders,
    IRepository<AnalysisTest> analyses,
    IRepository<Patient> patients,
    IRepository<Sample> samples,
    IResultQueries queries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IDataAccessAuditor accessAuditor,
    IClock clock)
{
    /// <summary>
    /// Resolves whatever the technician scanned or typed to an order.
    /// <para>
    /// Accepts an accession number or a tube barcode, because the label in front of them carries
    /// one or the other and making them know which is friction that produces wrong lookups. The
    /// match is exact and case-sensitive in both cases: a fuzzy barcode match is how one patient's
    /// tube gets attached to another patient's file.
    /// </para>
    /// </summary>
    public async Task<Result<Guid>> ResolveOrderAsync(string term, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Results.View);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        if (string.IsNullOrWhiteSpace(term))
            return Result.Failure<Guid>(Error.Validation("Order_AccessionNumberRequired"));

        var orderId = await queries.ResolveOrderIdAsync(term.Trim(), currentUser.SiteId, ct);

        return orderId is { } found
            ? Result.Success(found)
            : Result.Failure<Guid>(Error.NotFound("Order_NotFoundForScan", term.Trim()));
    }

    public async Task<Result<ResultEntrySheet>> OpenAsync(Guid orderId, Guid? departmentId = null,
                                                           CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Results.View);
        if (permitted.IsFailure) return Result.Failure<ResultEntrySheet>(permitted.FirstError);

        var order = await orders.GetByIdWithDetailsAsync(orderId, ct);
        if (order is null) return Result.Failure<ResultEntrySheet>(Error.NotFound("Order_NotFound"));

        var patient = await patients.GetByIdAsync(order.PatientId, ct);
        if (patient is null) return Result.Failure<ResultEntrySheet>(Error.NotFound("Order_PatientNotFound"));

        var catalogue = await LoadCatalogueAsync(order, ct);
        var resultRows = await queries.GetResultsForOrderAsync(orderId, departmentId, ct);
        var sampleDefects = await LoadSampleDefectsAsync(order, ct);

        var today = clock.Today;
        var context = ReferenceRangeContext.For(patient, today);
        var rows = new List<ResultEntryRow>(resultRows.Count);

        foreach (var result in resultRows)
        {
            var parameter = catalogue.Parameters.GetValueOrDefault(result.TestParameterId);
            var test = parameter is null ? null : catalogue.TestsByParameter.GetValueOrDefault(parameter.Id);
            if (parameter is null || test is null) continue;

            var range = parameter.ResolveRange(context);
            var previous = await queries.GetPreviousValueAsync(order.PatientId, parameter.Id,
                                                                order.RegisteredAt, ct);

            var item = order.Items.FirstOrDefault(i => i.Id == result.LabOrderItemId);

            rows.Add(new ResultEntryRow
            {
                TestResultId = result.Id,
                LabOrderItemId = result.LabOrderItemId,
                TestParameterId = parameter.Id,
                AnalysisCode = test.Code,
                AnalysisName = test.Name,
                ParameterCode = parameter.Code,
                ParameterName = parameter.Name,
                ValueType = parameter.ValueType,
                UnitSymbol = parameter.Unit?.DisplaySymbol,
                Decimals = parameter.Decimals,
                IndentLevel = parameter.IndentLevel,
                IsCalculated = parameter.IsCalculated,
                // A calculated parameter is never typed into, and a validated one needs the
                // revision path rather than the entry grid.
                IsReadOnly = parameter.IsCalculated || result.IsValidated,
                // Empty until something is entered: FormatValue's "-" placeholder in the cell read
                // as a value, and the cursor skipped the row as already filled.
                CurrentValue = result.HasValue || result.Flags.HasFlag(ResultFlag.NotDone)
                    ? result.FormatValue(currentUser.Language)
                    : null,
                NumericValue = result.NumericValue,
                ValueOperator = result.ValueOperator,
                ReferenceRangeText = range?.Format(currentUser.Language, parameter.Decimals),
                LowerLimit = range?.LowerLimit,
                UpperLimit = range?.UpperLimit,
                PreviousValue = previous?.Value,
                PreviousDate = previous?.Date,
                Interpretation = result.Interpretation,
                Flags = result.Flags,
                Status = result.Status,
                TechnicalComment = result.TechnicalComment,
                SampleDefects = item?.SampleId is { } sampleId
                    ? sampleDefects.GetValueOrDefault(sampleId)
                    : SampleDefect.None,
                CodedOptions = [.. parameter.CodedOptions
                    .OrderBy(o => o.DisplayOrder)
                    .Select(o => new CodedOptionView(o.Code, o.Label, o.IsAbnormal, o.IsCritical))]
            });
        }

        await accessAuditor.LogAsync(Domain.Audit.DataAccessKind.ViewOrder, order.PatientId, order.Id,
                                     "ResultEntry", rows.Count, ct: ct);

        return Result.Success(new ResultEntrySheet
        {
            LabOrderId = order.Id,
            AccessionNumber = order.AccessionNumber,
            PatientName = patient.FullName,
            PatientNameArabic = patient.FullNameArabic,
            Gender = patient.Gender,
            AgeYears = patient.AgeInYears(today),
            DateOfBirth = patient.DateOfBirth,
            IsPregnant = patient.IsPregnant,
            Priority = order.Priority,
            ClinicalInformation = order.ClinicalInformation,
            DueAt = order.DueAt,
            Rows = rows
        });
    }

    /// <summary>
    /// Stores a batch of entries and runs the full post-analytical pipeline over the order.
    /// Batched because a technician types a whole panel and expects the calculated parameters and
    /// the flags to settle once, consistently, rather than after each keystroke.
    /// </summary>
    public async Task<Result<ResultEntryOutcome>> SubmitAsync(Guid orderId,
                                                               IReadOnlyList<ResultEntry> entries,
                                                               CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Results.Enter);
        if (permitted.IsFailure) return Result.Failure<ResultEntryOutcome>(permitted.FirstError);

        var order = await orders.GetByIdWithDetailsAsync(orderId, ct);
        if (order is null) return Result.Failure<ResultEntryOutcome>(Error.NotFound("Order_NotFound"));

        var patient = await patients.GetByIdAsync(order.PatientId, ct);
        if (patient is null) return Result.Failure<ResultEntryOutcome>(Error.NotFound("Order_PatientNotFound"));

        var catalogue = await LoadCatalogueAsync(order, ct);
        var stored = await queries.GetResultsForOrderAsync(orderId, null, ct);
        var byId = stored.ToDictionary(r => r.Id);

        var rejected = new List<Error>();
        var accepted = 0;
        var now = clock.Now;

        // --- 1. Store the raw values ------------------------------------------------------------

        foreach (var entry in entries)
        {
            if (!byId.TryGetValue(entry.TestResultId, out var result))
            {
                rejected.Add(Error.NotFound("Result_NotFound", entry.TestResultId));
                continue;
            }

            var parameter = catalogue.Parameters.GetValueOrDefault(result.TestParameterId);
            if (parameter is null)
            {
                rejected.Add(Error.NotFound("Result_ParameterNotFound", result.TestParameterId));
                continue;
            }

            if (parameter.IsCalculated)
            {
                rejected.Add(Error.Validation("Result_CalculatedCannotBeEntered", parameter.Code));
                continue;
            }

            if (result.IsValidated)
            {
                rejected.Add(Error.Conflict("Result_ValidatedRequiresRevision", parameter.Code));
                continue;
            }

            // Correcting one's own entry before it is checked is part of entering; changing a value
            // that was already technically validated is a modification, and needs that right.
            if (result.Status == TestResultStatus.TechnicallyValidated
                && !currentUser.Has(Permissions.Results.Modify))
            {
                rejected.Add(Error.Forbidden("Result_ModifyRequiresPermission", parameter.Code));
                continue;
            }

            var applied = ApplyEntry(result, parameter, entry, now);
            if (applied.IsFailure)
            {
                rejected.Add(applied.FirstError);
                continue;
            }

            result.TechnicalComment = entry.TechnicalComment ?? result.TechnicalComment;
            result.PrintTechnicalComment = entry.PrintTechnicalComment;
            result.InstrumentId = entry.InstrumentId ?? result.InstrumentId;
            result.ReagentLotId = entry.ReagentLotId ?? result.ReagentLotId;

            // A dilution is disclosed on the report: the same figure means something different
            // when the sample had to be diluted to bring it into the measuring range.
            if (entry.DilutionFactor is { } factor and > 1m) result.FlagDiluted(factor);

            accepted++;
        }

        // --- 2. Recompute calculated parameters -------------------------------------------------

        RecalculateDerived(stored, catalogue, patient, now);

        // --- 3. Interpret, delta-check and flag -------------------------------------------------

        var context = ReferenceRangeContext.For(patient, clock.Today);
        var criticalFindings = new List<CriticalFinding>();

        foreach (var result in stored.Where(r => r.HasValue && !r.IsValidated))
        {
            var parameter = catalogue.Parameters.GetValueOrDefault(result.TestParameterId);
            if (parameter is null) continue;

            var range = parameter.ResolveRange(context);
            result.ApplyInterpretation(parameter, range, currentUser.Language,
                                       parameter.Unit?.DisplaySymbol);

            var previous = await queries.GetPreviousValueAsync(order.PatientId, parameter.Id,
                                                               order.RegisteredAt, ct);

            if (previous is not null)
            {
                result.PreviousNumericValue = previous.Value;
                result.PreviousResultDate = previous.Date;

                var daysSince = clock.Today.DayNumber - previous.Date.DayNumber;

                if (result.NumericValue is { } current &&
                    parameter.FailsDeltaCheck(current, previous.Value, daysSince))
                    result.FlagDeltaCheckFailure();
            }

            // A defect on the sample this result came from is disclosed on the report, so the
            // clinician can weigh it. Silently reporting a haemolysed potassium is the error.
            var item = order.Items.FirstOrDefault(i => i.Id == result.LabOrderItemId);
            if (item?.SampleId is { } sampleId)
            {
                var sample = await samples.GetByIdAsync(sampleId, ct);
                if (sample?.HasDefects == true) result.FlagSampleDefect();
            }

            if (result.IsCritical)
            {
                criticalFindings.Add(new CriticalFinding(
                    result.Id,
                    parameter.Name.For(currentUser.Language),
                    $"{result.FormatValue(currentUser.Language)} {parameter.Unit?.DisplaySymbol}".Trim(),
                    result.Interpretation));
            }
        }

        // --- 4. Advance the order items whose parameters now all carry a value -------------------

        foreach (var item in order.Items.Where(i => !i.IsCancelled))
        {
            var itemResults = stored.Where(r => r.LabOrderItemId == item.Id).ToList();
            if (itemResults.Count == 0) continue;

            if (itemResults.All(r => r.HasValue || r.Status == TestResultStatus.NotDone))
                item.MarkResultEntered(now);
        }

        order.RecomputeStatus();

        // --- 5. Reflex rules -------------------------------------------------------------------

        var suggestions = await EvaluateReflexRulesAsync(stored, ct);

        unitOfWork.AuditReason = $"Saisie de résultats — dossier {order.AccessionNumber}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new ResultEntryOutcome(accepted, rejected, criticalFindings, suggestions));
    }

    // ---- Entry parsing --------------------------------------------------------------------------

    /// <summary>
    /// Interprets the typed text according to the parameter's declared value type.
    /// <para>
    /// Both <c>.</c> and <c>,</c> are accepted as the decimal separator, and a leading
    /// <c>&lt;</c>, <c>&gt;</c>, <c>≤</c> or <c>≥</c> is honoured as a censoring operator, because
    /// that is genuinely what a technician types when a value is below the detection limit.
    /// </para>
    /// </summary>
    private Result ApplyEntry(TestResult result, TestParameter parameter, ResultEntry entry,
                              DateTimeOffset now)
    {
        var raw = entry.RawValue?.Trim();

        if (string.IsNullOrEmpty(raw))
            return result.MarkNotDone(entry.TechnicalComment ?? "Non fait", now, currentUser.UserId);

        switch (parameter.ValueType)
        {
            case ResultValueType.Text:
                return result.EnterText(raw, now, currentUser.UserId);

            case ResultValueType.Coded or ResultValueType.SemiQuantitative:
                return result.EnterCoded(parameter, raw, now, currentUser.UserId);

            case ResultValueType.Titre:
            {
                // Accepts "1/320", "1:320" or a bare "320".
                var denominator = raw.Split('/', ':').LastOrDefault()?.Trim();
                return int.TryParse(denominator, out var parsed)
                    ? result.EnterTitre(parsed, now, currentUser.UserId)
                    : Result.Failure(Error.Validation("Result_TitreMustBePositive"));
            }

            case ResultValueType.Heading:
                return Result.Success();

            default:
            {
                var text = raw;
                string? oper = null;

                foreach (var candidate in (string[])["<=", ">=", "≤", "≥", "<", ">"])
                {
                    if (!text.StartsWith(candidate, StringComparison.Ordinal)) continue;
                    oper = candidate;
                    text = text[candidate.Length..].Trim();
                    break;
                }

                if (!decimal.TryParse(text.Replace(',', '.'),
                                      System.Globalization.NumberStyles.Float,
                                      System.Globalization.CultureInfo.InvariantCulture,
                                      out var value))
                    return Result.Failure(Error.Validation("Result_NotANumber", raw));

                var entered = result.EnterNumeric(parameter, value, now, currentUser.UserId,
                                                  rawValue: raw);
                if (entered.IsFailure) return entered;

                // An operator the technician typed deliberately is kept even when the value sits
                // inside the measuring range: "< 5" from a qualitative read is not the number 5.
                // EnterNumeric may already have set one for an out-of-range value; that takes
                // precedence, because it reflects what the method can actually support.
                return oper is not null && result.ValueOperator is null
                    ? result.SetCensoringOperator(oper)
                    : Result.Success();
            }
        }
    }

    // ---- Calculated parameters ------------------------------------------------------------------

    /// <summary>
    /// Recomputes every calculated parameter of the order in dependency order.
    /// <para>
    /// Dependency order is resolved rather than assumed: an LDL depends on a cholesterol, an
    /// HDL and a triglyceride, and a laboratory is free to define a parameter that depends on
    /// another calculated one. Evaluating in catalogue order would silently produce a value from a
    /// stale input.
    /// </para>
    /// </summary>
    private void RecalculateDerived(IReadOnlyList<TestResult> stored, CatalogueSnapshot catalogue,
                                    Patient patient, DateTimeOffset now)
    {
        var calculated = catalogue.Parameters.Values
            .Where(p => p.IsCalculated)
            .ToDictionary(p => p.Code, p => p.CalculationFormula!, StringComparer.Ordinal);

        if (calculated.Count == 0) return;

        var ordering = FormulaEvaluator.ResolveEvaluationOrder(calculated);
        if (ordering.IsFailure) return;   // A circular definition is a configuration error; the
                                          // catalogue screen reports it, entry just skips.

        // Measured values, keyed by parameter code, plus the patient context variables.
        var values = BuildValueBag(stored, catalogue, patient);

        foreach (var code in ordering.Value)
        {
            var parameter = catalogue.Parameters.Values
                .FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.Ordinal));

            if (parameter is null) continue;

            var target = stored.FirstOrDefault(r => r.TestParameterId == parameter.Id);
            if (target is null || target.IsValidated) continue;

            var evaluation = FormulaEvaluator.Evaluate(parameter.CalculationFormula!, values);

            if (evaluation.IsFailure)
            {
                // An input that has not been entered yet is the normal case mid-panel, not an
                // error worth surfacing; the value simply stays awaited.
                continue;
            }

            target.EnterNumeric(parameter, evaluation.Value, now, currentUser.UserId);
            values[parameter.Code] = parameter.Round(evaluation.Value);
        }
    }

    private Dictionary<string, decimal> BuildValueBag(IReadOnlyList<TestResult> stored,
                                                       CatalogueSnapshot catalogue, Patient patient)
    {
        var values = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var result in stored)
        {
            if (result.NumericValue is not { } value) continue;
            var parameter = catalogue.Parameters.GetValueOrDefault(result.TestParameterId);
            if (parameter is null) continue;
            values[parameter.Code] = value;
        }

        var today = clock.Today;

        values[FormulaEvaluator.ContextVariables.AgeYears] = patient.AgeInYears(today) ?? 0m;
        values[FormulaEvaluator.ContextVariables.AgeDays] = patient.AgeInDays(today) ?? 0m;
        values[FormulaEvaluator.ContextVariables.IsFemale] = patient.Gender == Gender.Female ? 1m : 0m;
        values[FormulaEvaluator.ContextVariables.IsMale] = patient.Gender == Gender.Male ? 1m : 0m;
        values[FormulaEvaluator.ContextVariables.IsPregnant] = patient.IsPregnant ? 1m : 0m;
        values[FormulaEvaluator.ContextVariables.WeightKg] = patient.WeightKg ?? 0m;
        values[FormulaEvaluator.ContextVariables.HeightCm] = patient.HeightCm ?? 0m;
        values[FormulaEvaluator.ContextVariables.BodySurfaceArea] = patient.BodySurfaceAreaM2 ?? 0m;

        return values;
    }

    // ---- Reflex rules ---------------------------------------------------------------------------

    private async Task<List<ReflexSuggestion>> EvaluateReflexRulesAsync(
        IReadOnlyList<TestResult> stored, CancellationToken ct)
    {
        var suggestions = new List<ReflexSuggestion>();

        var parameterIds = stored.Where(r => r.HasValue).Select(r => r.TestParameterId).Distinct().ToList();
        if (parameterIds.Count == 0) return suggestions;

        var rules = await queries.GetActiveReflexRulesAsync(parameterIds, ct);

        foreach (var rule in rules)
        {
            var trigger = stored.FirstOrDefault(r => r.TestParameterId == rule.TriggerParameterId);
            if (trigger is null || !trigger.HasValue) continue;

            if (!rule.Fires(trigger.NumericValue, trigger.CodedValue)) continue;

            suggestions.Add(new ReflexSuggestion(trigger.Id, rule.AnalysisToAddId,
                                                  rule.Description, rule.AddAutomatically));
        }

        return suggestions;
    }

    // ---- Technical validation -------------------------------------------------------------------

    /// <summary>
    /// Technical validation: the technician confirms the values are plausible and the run was in
    /// control. Explicitly not the legally binding release — that is a biologist's act.
    /// </summary>
    public async Task<Result> TechnicallyValidateAsync(Guid orderId, IReadOnlyList<Guid> resultIds,
                                                        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Results.TechnicalValidate);
        if (permitted.IsFailure) return permitted;

        var order = await orders.GetByIdWithDetailsAsync(orderId, ct);
        if (order is null) return Result.Failure(Error.NotFound("Order_NotFound"));

        var stored = await queries.GetResultsForOrderAsync(orderId, null, ct);
        var selected = resultIds.Count == 0
            ? stored
            : [.. stored.Where(r => resultIds.Contains(r.Id))];

        var errors = new List<Error>();
        var now = clock.Now;

        foreach (var result in selected.Where(r => r.HasValue))
        {
            var validated = result.TechnicallyValidate(now, currentUser.UserId);
            if (validated.IsFailure) errors.Add(validated.FirstError);
        }

        foreach (var item in order.Items.Where(i => !i.IsCancelled))
        {
            var itemResults = stored.Where(r => r.LabOrderItemId == item.Id).ToList();
            if (itemResults.Count == 0) continue;

            if (itemResults.All(r => r.Status is TestResultStatus.TechnicallyValidated
                                            or TestResultStatus.Validated
                                            or TestResultStatus.Reported
                                            or TestResultStatus.NotDone))
                item.MarkTechnicallyValidated(now, currentUser.UserId);
        }

        order.RecomputeStatus();

        unitOfWork.AuditReason = $"Validation technique — dossier {order.AccessionNumber}";
        await unitOfWork.SaveChangesAsync(ct);

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }

    // ---- Catalogue snapshot ---------------------------------------------------------------------

    private sealed record CatalogueSnapshot(
        Dictionary<Guid, TestParameter> Parameters,
        Dictionary<Guid, AnalysisTest> TestsByParameter);

    private async Task<CatalogueSnapshot> LoadCatalogueAsync(LabOrder order, CancellationToken ct)
    {
        var parameters = new Dictionary<Guid, TestParameter>();
        var testsByParameter = new Dictionary<Guid, AnalysisTest>();

        foreach (var analysisId in order.Items.Select(i => i.AnalysisTestId).Distinct())
        {
            var test = await analyses.GetByIdWithDetailsAsync(analysisId, ct);
            if (test is null) continue;

            foreach (var parameter in test.Parameters)
            {
                parameters[parameter.Id] = parameter;
                testsByParameter[parameter.Id] = test;
            }
        }

        return new CatalogueSnapshot(parameters, testsByParameter);
    }

    private async Task<Dictionary<Guid, SampleDefect>> LoadSampleDefectsAsync(
        LabOrder order, CancellationToken ct)
    {
        var defects = new Dictionary<Guid, SampleDefect>();

        foreach (var sampleId in order.Items.Select(i => i.SampleId)
                                            .Where(id => id is not null)
                                            .Select(id => id!.Value)
                                            .Distinct())
        {
            var sample = await samples.GetByIdAsync(sampleId, ct);
            if (sample is not null) defects[sampleId] = sample.Defects;
        }

        return defects;
    }
}

/// <summary>
/// Read-side queries the entry pipeline needs but that a repository should not carry: they cross
/// aggregates, are shaped for one screen, and must stay cheap on SQL Server Express.
/// </summary>
public interface IResultQueries
{
    Task<IReadOnlyList<TestResult>> GetResultsForOrderAsync(Guid orderId, Guid? departmentId,
                                                             CancellationToken ct = default);

    /// <summary>
    /// The patient's most recent validated value for a parameter before the current order — the
    /// antériorité column on the report and the baseline for the delta check.
    /// </summary>
    Task<PreviousValue?> GetPreviousValueAsync(Guid patientId, Guid parameterId,
                                                DateTimeOffset before, CancellationToken ct = default);

    Task<IReadOnlyList<ReflexRule>> GetActiveReflexRulesAsync(IReadOnlyList<Guid> triggerParameterIds,
                                                               CancellationToken ct = default);

    /// <summary>
    /// Resolves an accession number or a tube barcode to an order on the given site. Exact match
    /// only; returns null when nothing matches.
    /// </summary>
    Task<Guid?> ResolveOrderIdAsync(string term, Guid siteId, CancellationToken ct = default);
}

public sealed record PreviousValue(decimal Value, DateOnly Date, Guid TestResultId);
