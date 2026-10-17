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
    Guid? ReagentLotId = null,
    ResultEntryAction Action = ResultEntryAction.Value);

/// <summary>What an entry does to its result.</summary>
public enum ResultEntryAction
{
    /// <summary>Stores <see cref="ResultEntry.RawValue"/>; an empty value records the analysis as not done.</summary>
    Value = 0,

    /// <summary>
    /// Records the analysis as not done, with <see cref="ResultEntry.TechnicalComment"/> as the
    /// reason printed on the report. The reason is required: "Non fait" alone tells the clinician
    /// nothing about whether to send the patient back.
    /// </summary>
    NotDone = 1,

    /// <summary>
    /// Withdraws a value keyed by mistake, or a "not done", so the result is awaited again. Only
    /// before biological validation; a released value is corrected, never cleared.
    /// </summary>
    Clear = 2
}

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

    /// <summary>The most points the antériorité chart shows: beyond that it stops being readable.</summary>
    public const int ResultHistoryLimit = 30;

    /// <summary>
    /// The patient's validated values for one parameter, oldest first, for the antériorité chart of
    /// the entry and validation screens. Read from the order so the screen cannot ask for the
    /// history of a patient it is not showing, and through the same merged-file walk as the
    /// antériorité column: a chart that lost the survivor's years of values the day two files were
    /// merged would contradict the column beside it.
    /// </summary>
    public async Task<Result<IReadOnlyList<ResultHistoryPoint>>> GetHistoryAsync(
        Guid orderId, Guid testParameterId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Results.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<ResultHistoryPoint>>(permitted.FirstError);

        var order = await orders.GetByIdAsync(orderId, ct);
        if (order is null) return Result.Failure<IReadOnlyList<ResultHistoryPoint>>(Error.NotFound("Order_NotFound"));

        var points = await queries.GetValidatedHistoryAsync(order.PatientId, testParameterId,
                                                            ResultHistoryLimit, ct);

        // Other files' values are shown, so the access is recorded like opening the order.
        await accessAuditor.LogAsync(Domain.Audit.DataAccessKind.ViewOrder, order.PatientId, order.Id,
                                     "ResultHistory", points.Count, ct: ct);

        return Result.Success(points);
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

            // A value keyed on the wrong line, or a "not done" set by mistake: the result goes back
            // to awaited, and nothing of the withdrawn value — its lot, its not-done reason — stays
            // to be printed or traced as if it had been measured.
            if (entry.Action == ResultEntryAction.Clear)
            {
                var cleared = result.ClearValue();
                if (cleared.IsFailure)
                {
                    rejected.Add(cleared.FirstError);
                    continue;
                }

                if (valueBefore.NotDone)
                {
                    result.TechnicalComment = null;
                    result.PrintTechnicalComment = false;
                }

                result.ReagentLotId = null;
                accepted++;
                continue;
            }

            var applied = ApplyEntry(result, parameter, entry, now);
            if (applied.IsFailure)
            {
                rejected.Add(applied.FirstError);
                continue;
            }

            result.TechnicalComment = entry.TechnicalComment ?? result.TechnicalComment;
            // The reason an analysis was not done is printed in the value's place: never hidden.
            result.PrintTechnicalComment = entry.PrintTechnicalComment
                                           || entry.Action == ResultEntryAction.NotDone;
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
    /// Corrects a value a biologist has already released, printed or not — the rectification of a
    /// result, which the entry grid refuses (<c>Result_ValidatedRequiresRevision</c>).
    /// <para>
    /// The value is never overwritten in place: <see cref="TestResult.Revise"/> keeps the released
    /// figure, its interpretation, who validated it and when, with the stated reason, as a revision.
    /// The new figure is read exactly as the grid reads it (same parsing, same impossibility and
    /// measuring-range rules) and then stands where any new value stands — entered, checked by
    /// nobody — so it goes through technical and biological validation again, critical call
    /// included, before a report can carry it. Its analysis goes back to entry with it, and so does
    /// every released calculated value that read it (an LDL after a corrected cholesterol).
    /// </para>
    /// <para>
    /// Reserved to whoever may withdraw a release (<see cref="Permissions.Results.Unvalidate"/>,
    /// with <see cref="Permissions.Results.Modify"/>) and is qualified to validate the discipline;
    /// once the value has been printed, also to whoever may issue a rectified report
    /// (<see cref="Permissions.Reports.Amend"/>), since that is the only way out for the corrected
    /// figure: the next release of the file is refused unless it is issued as a rectification.
    /// </para>
    /// </summary>
    public async Task<Result<ResultEntryOutcome>> CorrectReleasedAsync(
        Guid orderId, Guid testResultId, string? newValue, string reason, CancellationToken ct = default)
    {
        var permitted = guard.RequireAll(Permissions.Results.Modify, Permissions.Results.Unvalidate);
        if (permitted.IsFailure) return Result.Failure<ResultEntryOutcome>(permitted.FirstError);

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure<ResultEntryOutcome>(Error.Validation("Result_RevisionReasonRequired"));
        if (string.IsNullOrWhiteSpace(newValue))
            return Result.Failure<ResultEntryOutcome>(Error.Validation("Result_CorrectionValueRequired"));

        reason = reason.Trim();

        var order = await orders.GetByIdWithDetailsAsync(orderId, ct);
        if (order is null) return Result.Failure<ResultEntryOutcome>(Error.NotFound("Order_NotFound"));

        var patient = await _pipeline.PatientOfAsync(order, ct);
        if (patient is null) return Result.Failure<ResultEntryOutcome>(Error.NotFound("Order_PatientNotFound"));

        var catalogue = await _pipeline.LoadCatalogueAsync(order, ct);
        var stored = await queries.GetResultsForOrderAsync(orderId, null, ct);

        var result = stored.FirstOrDefault(r => r.Id == testResultId);
        if (result is null)
            return Result.Failure<ResultEntryOutcome>(Error.NotFound("Result_NotFound", testResultId));

        var parameter = catalogue.Parameters.GetValueOrDefault(result.TestParameterId);
        if (parameter is null)
            return Result.Failure<ResultEntryOutcome>(
                Error.NotFound("Result_ParameterNotFound", result.TestParameterId));

        // A calculated value follows its inputs: the input is what gets corrected.
        if (parameter.IsCalculated)
            return Result.Failure<ResultEntryOutcome>(
                Error.Validation("Result_CalculatedCannotBeEntered", parameter.Code));

        // A value nobody has released yet is simply re-entered in the grid.
        if (!result.IsValidated)
            return Result.Failure<ResultEntryOutcome>(
                Error.Conflict("Result_CorrectionOnlyForReleased", parameter.Code));

        var item = order.Items.FirstOrDefault(i => i.Id == result.LabOrderItemId);
        if (item is null || item.IsCancelled)
            return Result.Failure<ResultEntryOutcome>(Error.Conflict("Result_ItemCancelled", parameter.Code));

        if (result.Status == TestResultStatus.Reported || item.HasReportedResults)
        {
            var amend = guard.Require(Permissions.Reports.Amend);
            if (amend.IsFailure) return Result.Failure<ResultEntryOutcome>(amend.FirstError);
        }

        var departmentCode = catalogue.TestsByParameter.GetValueOrDefault(parameter.Id)?.Department?.Code
                             ?? string.Empty;
        var qualified = await guard.RequireBiologicalValidationAsync(departmentCode, ct);
        if (qualified.IsFailure) return Result.Failure<ResultEntryOutcome>(qualified.FirstError);

        var now = clock.Now;

        // Read on a scratch result, so a figure the parameter refuses changes nothing at all.
        var probe = new TestResult(order.Id, item.Id, parameter.Id, order.SiteId);
        var parsed = ApplyEntry(probe, parameter, new ResultEntry(result.Id, newValue), now);
        if (parsed.IsFailure) return Result.Failure<ResultEntryOutcome>(parsed.FirstError);

        // Withdrawing a release without changing the figure is "Dévalider", not a correction.
        if (ValueOf(probe) == ValueOf(result))
            return Result.Failure<ResultEntryOutcome>(
                Error.Conflict("Result_CorrectionUnchanged", parameter.Code));

        var revised = result.Revise(probe.NumericValue, probe.TextValue, probe.CodedValue, now,
                                    currentUser.UserId, reason, probe.ValueOperator,
                                    probe.TitreDenominator);
        if (revised.IsFailure) return Result.Failure<ResultEntryOutcome>(revised.FirstError);

        ReopenItemForCorrection(item, reason);

        // Released calculated values that read the corrected figure, directly or through another
        // calculated one, are reopened too so the pipeline recomputes them: a corrected
        // cholesterol under a released LDL computed from the old one would print both.
        var correctedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { parameter.Code };
        bool reopenedAny;

        do
        {
            reopenedAny = false;

            foreach (var dependent in stored.Where(r => r.IsValidated).ToList())
            {
                var formula = catalogue.Parameters.GetValueOrDefault(dependent.TestParameterId);
                if (formula is not { IsCalculated: true } || correctedCodes.Contains(formula.Code)) continue;
                if (!FormulaEvaluator.ReferencedParameters(formula.CalculationFormula).Any(correctedCodes.Contains))
                    continue;

                var owner = order.Items.FirstOrDefault(i => i.Id == dependent.LabOrderItemId);
                if (owner is null || owner.IsCancelled) continue;

                var dependentReason = $"Recalcul après correction de {parameter.Code} : {reason}";
                // Within the revision's reason column.
                if (dependentReason.Length > 500) dependentReason = dependentReason[..500];
                if (dependent.Revise(dependent.NumericValue, dependent.TextValue, dependent.CodedValue, now,
                                     currentUser.UserId, dependentReason, dependent.ValueOperator,
                                     dependent.TitreDenominator).IsFailure)
                    continue;

                ReopenItemForCorrection(owner, dependentReason);
                correctedCodes.Add(formula.Code);
                reopenedAny = true;
            }
        }
        while (reopenedAny);

        var criticalFindings = await _pipeline.RunAsync(order, patient, stored, catalogue,
                                                        currentUser.UserId, currentUser.Language, ct);

        ResultPostEntryPipeline.AdvanceCompletedItems(order, stored, now);
        order.RecomputeStatus();

        unitOfWork.AuditReason =
            $"Correction d'un résultat validé — dossier {order.AccessionNumber}, {parameter.Code} : {reason}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new ResultEntryOutcome(1, [], criticalFindings, []));
    }

    /// <summary>
    /// Takes the analysis of a corrected value back to entry, where its validation starts again. An
    /// analysis still before validation (sent back to the bench, partly released) is there already.
    /// </summary>
    private static void ReopenItemForCorrection(LabOrderItem item, string reason)
    {
        if (item.Status is LabOrderItemStatus.TechnicallyValidated or LabOrderItemStatus.Validated
                        or LabOrderItemStatus.Reported)
            item.ReopenForCorrection(reason);
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

        if (entry.Action == ResultEntryAction.NotDone)
            return string.IsNullOrWhiteSpace(entry.TechnicalComment)
                ? Result.Failure(Error.Validation("Result_NotDoneReasonRequired"))
                : result.MarkNotDone(entry.TechnicalComment.Trim(), now, currentUser.UserId);

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
        // The whole file means what is still to check: its released values, printed or not, are
        // not refused one by one as "already validated" — a file with one corrected value among
        // printed ones could otherwise never be checked without an error. Values already checked
        // keep their check: validating the file again after a late value used to stamp the second
        // technician's name and time on the values the first one had checked.
        List<TestResult> selected = resultIds.Count == 0
            ? [.. stored.Where(r => !r.IsValidated && r.Status != TestResultStatus.TechnicallyValidated)]
            : [.. stored.Where(r => resultIds.Contains(r.Id))];

        var errors = new List<Error>();
        var now = clock.Now;

        // A repeated analysis whose new run is not in yet still carries the run that was set aside,
        // and a cancelled one is off the file: neither is checked. Validating the whole file
        // re-checked the first run, which the biologist could then release value by value.
        var setAside = order.Items
            .Where(i => i.IsCancelled
                        || ResultPostEntryPipeline.AwaitsRerun(i, stored.Where(r => r.LabOrderItemId == i.Id)))
            .Select(i => i.Id)
            .ToHashSet();

        foreach (var result in selected.Where(r => r.HasValue && !setAside.Contains(r.LabOrderItemId)))
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

    /// <summary>
    /// The patient's validated numeric values for one parameter, merged files included, the latest
    /// <paramref name="limit"/> of them in chronological order (oldest first) — the antériorité
    /// chart. Unlike <see cref="GetPreviousValuesAsync"/> there is no cut-off date: the current
    /// order's value is part of the curve once it is validated.
    /// </summary>
    Task<IReadOnlyList<ResultHistoryPoint>> GetValidatedHistoryAsync(
        Guid patientId, Guid parameterId, int limit, CancellationToken ct = default);

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

/// <summary>One validated value on the antériorité chart, with the file it was reported in.</summary>
public sealed record ResultHistoryPoint(Guid TestResultId, DateTimeOffset ValidatedAt, decimal Value,
                                        string? ValueOperator, string AccessionNumber);
