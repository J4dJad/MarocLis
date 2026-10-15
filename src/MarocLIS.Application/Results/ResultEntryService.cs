using MarocLIS.Application.Abstractions;
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

    /// <summary>The tube this value comes from is not received yet: nothing can be entered.</summary>
    public bool IsAwaitingSample { get; init; }

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

    /// <summary>
    /// When the value shown was stored. A critical call names it, so a call dictated from a screen
    /// loaded before a re-run is refused instead of covering a value nobody was told about.
    /// </summary>
    public DateTimeOffset? EnteredAt { get; init; }

    /// <summary>The value is beyond a critical threshold, low or high.</summary>
    public bool IsCritical => (Flags & (ResultFlag.CriticalLow | ResultFlag.CriticalHigh)) != 0;

    /// <summary>
    /// An acknowledged, read-back notification covers the value the result holds now (the rule of
    /// <see cref="CriticalResultNotification.Covers"/>). Only meaningful on a critical row.
    /// </summary>
    public bool IsCriticalCommunicated { get; init; }

    /// <summary>
    /// A critical value nobody has yet been told about. "Communicate critical" calls only these:
    /// it used to raise a fresh notification for every critical row, including values already
    /// read back, which put calls in the trail that were never made.
    /// </summary>
    public bool NeedsCriticalCommunication => IsCritical && !IsCriticalCommunicated;

    public IReadOnlyList<CodedOptionView> CodedOptions { get; init; } = [];

    public string? TechnicalComment { get; init; }

    /// <summary>The analyser's own flags and comments on the value, as it sent them.</summary>
    public string? InstrumentFlags { get; init; }

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
    /// <summary>The steps every stored value goes through, shared with the analyser interface.</summary>
    private readonly ResultPostEntryPipeline _pipeline = new(analyses, patients, samples, queries, clock);

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

        // A merged duplicate's order is read with the surviving file's demographics.
        var patient = await _pipeline.PatientOfAsync(order, ct);
        if (patient is null) return Result.Failure<ResultEntrySheet>(Error.NotFound("Order_PatientNotFound"));

        var catalogue = await _pipeline.LoadCatalogueAsync(order, ct);
        var resultRows = await queries.GetCurrentResultsForOrderAsync(orderId, departmentId, ct);
        var history = resultRows.Count == 0
            ? new Dictionary<Guid, PreviousValue>()
            : await queries.GetPreviousValuesAsync(
                order.PatientId, [.. resultRows.Select(r => r.TestParameterId).Distinct()],
                order.RegisteredAt, ct);
        var sampleDefects = await LoadSampleDefectsAsync(order, ct);
        var sampleStatuses = await LoadSampleStatusesAsync(order, ct);

        // Asked only when there is something critical, which is rare: one round trip, not one per row.
        var criticalIds = resultRows
            .Where(r => (r.Flags & (ResultFlag.CriticalLow | ResultFlag.CriticalHigh)) != 0)
            .Select(r => r.Id)
            .ToList();
        var communicated = criticalIds.Count == 0
            ? (IReadOnlyCollection<Guid>)[]
            : await queries.GetCommunicatedCriticalResultIdsAsync(criticalIds, ct);

        var today = clock.Today;
        var rows = new List<ResultEntryRow>(resultRows.Count);

        foreach (var result in resultRows)
        {
            var parameter = catalogue.Parameters.GetValueOrDefault(result.TestParameterId);
            var test = parameter is null ? null : catalogue.TestsByParameter.GetValueOrDefault(parameter.Id);
            if (parameter is null || test is null) continue;

            // The interval shown is the one the value is interpreted against: same instrument,
            // same day.
            var range = parameter.ResolveRange(ReferenceRangeContext.ForResult(patient, result, today));
            var previous = history.GetValueOrDefault(parameter.Id);

            var item = order.Items.FirstOrDefault(i => i.Id == result.LabOrderItemId);

            // A cancelled analysis is off the order: its rows, such as those of a refused tube,
            // are not offered for entry.
            if (item is null || item.IsCancelled) continue;

            var awaitingSample = item.SampleId is { } tube
                                 && sampleStatuses.TryGetValue(tube, out var tubeStatus)
                                 && tubeStatus is not (SampleStatus.Received or SampleStatus.InAnalysis
                                                       or SampleStatus.Stored);

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
                IsReadOnly = parameter.IsCalculated || result.IsValidated || awaitingSample,
                IsAwaitingSample = awaitingSample,
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
                EnteredAt = result.EnteredAt,
                IsCriticalCommunicated = communicated.Contains(result.Id),
                TechnicalComment = result.TechnicalComment,
                InstrumentFlags = result.InstrumentFlags,
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

        // A merged duplicate's order is read with the surviving file's demographics.
        var patient = await _pipeline.PatientOfAsync(order, ct);
        if (patient is null) return Result.Failure<ResultEntryOutcome>(Error.NotFound("Order_PatientNotFound"));

        var catalogue = await _pipeline.LoadCatalogueAsync(order, ct);
        var stored = await queries.GetResultsForOrderAsync(orderId, null, ct);
        var byId = stored.ToDictionary(r => r.Id);

        var rejected = new List<Error>();
        var accepted = 0;
        var now = clock.Now;

        // The lot each value was measured with, from the stock: opened, released, in date. Looked
        // up once for the batch; an entry naming its own lot keeps it.
        var entered = entries.Select(e => byId.TryGetValue(e.TestResultId, out var r) ? r.TestParameterId : Guid.Empty)
                             .Where(id => id != Guid.Empty).Distinct().ToList();
        var lotsInUse = entered.Count == 0
            ? null
            : await queries.GetReagentLotsInUseAsync(entered, currentUser.SiteId, clock.Today, ct);

        var tubeStatuses = new Dictionary<Guid, SampleStatus?>();

        async Task<SampleStatus?> SampleStatusAsync(Guid sampleId)
        {
            if (!tubeStatuses.TryGetValue(sampleId, out var status))
                tubeStatuses[sampleId] = status = (await samples.GetByIdAsync(sampleId, ct))?.Status;

            return status;
        }

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

            // Nothing is entered on an analysis that no longer exists, nor on a tube the
            // laboratory has not taken in: a value on it would have no sample behind it.
            var owner = order.Items.FirstOrDefault(i => i.Id == result.LabOrderItemId);

            if (owner is null || owner.IsCancelled)
            {
                rejected.Add(Error.Conflict("Result_ItemCancelled", parameter.Code));
                continue;
            }

            if (owner.SampleId is { } tubeId && await SampleStatusAsync(tubeId) is { } tubeStatus
                && tubeStatus is not (SampleStatus.Received or SampleStatus.InAnalysis or SampleStatus.Stored))
            {
                rejected.Add(Error.Conflict("Result_SampleNotReceived", parameter.Code));
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

            var valueBefore = ValueOf(result);

            var applied = ApplyEntry(result, parameter, entry, now);
            if (applied.IsFailure)
            {
                rejected.Add(applied.FirstError);
                continue;
            }

            result.TechnicalComment = entry.TechnicalComment ?? result.TechnicalComment;
            result.PrintTechnicalComment = entry.PrintTechnicalComment;
            result.InstrumentId = entry.InstrumentId ?? result.InstrumentId;
            // A new value gets the lot of its own measurement: a re-run on a newer lot must not keep
            // the lot of the value it replaces. Unknown stays unknown rather than being guessed.
            // An unchanged value (only its comment edited) keeps the lot it was measured with, and
            // a partner laboratory's value never carries one of the laboratory's own lots.
            if (owner.IsSubcontracted)
                result.ReagentLotId = entry.ReagentLotId;
            else if (entry.ReagentLotId is not null || ValueOf(result) != valueBefore)
                result.ReagentLotId = entry.ReagentLotId
                                      ?? (lotsInUse is not null && lotsInUse.TryGetValue(result.TestParameterId, out var inUse)
                                              ? inUse
                                              : null);

            // A dilution is disclosed on the report: the same figure means something different
            // when the sample had to be diluted to bring it into the measuring range.
            if (entry.DilutionFactor is { } factor and > 1m) result.FlagDiluted(factor);

            // A value on a subcontracted act is the partner's answer: the report must say who
            // measured it, and the send-out list stops waiting for it.
            if (order.Items.FirstOrDefault(i => i.Id == result.LabOrderItemId) is { IsSubcontracted: true } sent)
            {
                result.FlagSubcontracted();
                sent.MarkSubcontractReturned(now);
            }

            accepted++;
        }

        // --- 2. Recompute calculated parameters, then interpret, delta-check and flag -----------

        var criticalFindings = await _pipeline.RunAsync(order, patient, stored, catalogue,
                                                        currentUser.UserId, currentUser.Language, ct);

        // --- 3. Advance the order items whose parameters now all carry a value -------------------

        ResultPostEntryPipeline.AdvanceCompletedItems(order, stored, now);

        order.RecomputeStatus();

        // --- 4. Reflex rules -------------------------------------------------------------------

        var suggestions = await EvaluateReflexRulesAsync(stored, ct);

        unitOfWork.AuditReason = $"Saisie de résultats — dossier {order.AccessionNumber}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new ResultEntryOutcome(accepted, rejected, criticalFindings, suggestions));
    }

    /// <summary>
    /// Re-runs the calculations and the interpretation over an order's results that are not yet
    /// validated, after the patient's sex, date of birth or pregnancy was corrected. Changes are
    /// left for the caller to save with its own audit reason; validated results are not touched,
    /// since a released figure changes only through a revision.
    /// </summary>
    /// <returns>
    /// The number of results re-interpreted, and of validated results left as they were — which
    /// were interpreted against the former demographics and may need amending.
    /// </returns>
    public async Task<(int Reinterpreted, int Validated)> ReinterpretPendingAsync(
        Guid orderId, Patient patient, CancellationToken ct = default)
    {
        var order = await orders.GetByIdWithDetailsAsync(orderId, ct);
        if (order is null) return (0, 0);

        var stored = await queries.GetResultsForOrderAsync(orderId, null, ct);
        var validated = stored.Count(r => r.IsValidated);

        if (!stored.Any(r => !r.IsValidated && (r.HasValue || r.Status == TestResultStatus.NotDone)))
            return (0, validated);

        var catalogue = await _pipeline.LoadCatalogueAsync(order, ct);

        // An eGFR or any formula reading the age or the sex changes with the demographics too.
        await _pipeline.RunAsync(order, patient, stored, catalogue, currentUser.UserId,
                                 currentUser.Language, ct);

        return (stored.Count(r => r.HasValue && !r.IsValidated), validated);
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
    /// <summary>What was measured, without who or when: tells a new value from a resent one.</summary>
    private static (decimal? Numeric, string? Operator, string? Text, string? Coded, int? Titre, bool NotDone)
        ValueOf(TestResult result) =>
        (result.NumericValue, result.ValueOperator, result.TextValue, result.CodedValue,
         result.TitreDenominator, result.Flags.HasFlag(ResultFlag.NotDone));

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

    // ---- Catalogue and tubes -------------------------------------------------------------------

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

    private async Task<Dictionary<Guid, SampleStatus>> LoadSampleStatusesAsync(LabOrder order, CancellationToken ct)
    {
        var statuses = new Dictionary<Guid, SampleStatus>();

        foreach (var sampleId in order.Items.Select(i => i.SampleId).OfType<Guid>().Distinct())
        {
            var sample = await samples.GetByIdAsync(sampleId, ct);
            if (sample is not null) statuses[sampleId] = sample.Status;
        }

        return statuses;
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
    /// The order's results as the database holds them now, for a screen about to show them.
    /// <para>
    /// A screen keeps one context for its life, and a context hands back the copy it already
    /// tracks rather than the row as it now stands: re-opening a file showed the potassium the
    /// analyser had since replaced, and a critical call was dictated from it. Copies with no pending
    /// change are read again; one with a change of its own is kept, and its save is then checked
    /// against the row version it was read with.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<TestResult>> GetCurrentResultsForOrderAsync(Guid orderId, Guid? departmentId,
                                                                    CancellationToken ct = default);

    /// <summary>
    /// The patient's most recent validated value for each parameter before the current order — the
    /// antériorité column on the report and the baseline for the delta check. A parameter with no
    /// history is absent. One lookup for a whole order, not one per value.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, PreviousValue>> GetPreviousValuesAsync(
        Guid patientId, IReadOnlyCollection<Guid> parameterIds, DateTimeOffset before,
        CancellationToken ct = default);

    Task<IReadOnlyList<ReflexRule>> GetActiveReflexRulesAsync(IReadOnlyList<Guid> triggerParameterIds,
                                                               CancellationToken ct = default);

    /// <summary>
    /// Resolves an accession number or a tube barcode to an order on the given site. Exact match
    /// only; returns null when nothing matches.
    /// </summary>
    Task<Guid?> ResolveOrderIdAsync(string term, Guid siteId, CancellationToken ct = default);

    /// <summary>
    /// Of the given results, those whose current value an acknowledged, read-back notification
    /// covers — the rule of <see cref="CriticalResultNotification.Covers"/>, applied in SQL.
    /// </summary>
    Task<IReadOnlyCollection<Guid>> GetCommunicatedCriticalResultIdsAsync(
        IReadOnlyCollection<Guid> testResultIds, CancellationToken ct = default);

    /// <summary>
    /// The reagent lot in use on the site for each parameter's analysis, by the rule of
    /// <see cref="Domain.Inventory.ReagentLotSelector"/>. A parameter whose lot the stock does not
    /// name unambiguously is absent.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid>> GetReagentLotsInUseAsync(
        IReadOnlyCollection<Guid> testParameterIds, Guid siteId, DateOnly today,
        CancellationToken ct = default);

    /// <summary>
    /// For each control target (<paramref name="parameterOfTarget"/>: target to its parameter), the
    /// lot awaiting release whose evaluation run was started today on <paramref name="instrumentId"/>
    /// (<see cref="Domain.Inventory.ReagentLot.EvaluationStartedAt"/>) and has not yet received that
    /// level. A target with none, or with more than one lot, is absent.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid>> GetReagentLotsUnderEvaluationAsync(
        IReadOnlyDictionary<Guid, Guid> parameterOfTarget, Guid siteId, Guid instrumentId, DateOnly today,
        CancellationToken ct = default);
}

public sealed record PreviousValue(decimal Value, DateOnly Date, Guid TestResultId);
