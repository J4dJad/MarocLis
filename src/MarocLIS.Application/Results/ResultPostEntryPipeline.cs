using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Calculations;
using MarocLIS.Application.Patients;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;

namespace MarocLIS.Application.Results;

/// <summary>The catalogue behind an order's results: each parameter, and the analysis it belongs to.</summary>
public sealed record ResultCatalogue(
    Dictionary<Guid, TestParameter> Parameters,
    Dictionary<Guid, AnalysisTest> TestsByParameter);

/// <summary>
/// The post-analytical steps every stored value goes through, whoever stored it: calculated
/// parameters recomputed in dependency order, then each value interpreted against the patient's
/// interval, given its previous value, delta-checked and flagged for a sample defect.
/// <para>
/// One code path for the entry screen and for the analyser interface. The interface used to apply
/// only the interpretation, so the bulk of the laboratory's values — those that arrive by ASTM or
/// HL7 — carried no previous value and no delta check, an LDL waited for a manual save that never
/// came, and an analyser re-run removed the delta flag a manual save had set, because
/// interpreting clears that flag and nothing checked again.
/// </para>
/// <para>
/// Saves nothing: the caller saves with its own audit reason.
/// </para>
/// </summary>
public sealed class ResultPostEntryPipeline(
    IRepository<AnalysisTest> analyses,
    IRepository<Patient> patients,
    IRepository<Sample> samples,
    IResultQueries queries,
    IClock clock)
{
    /// <summary>Printed next to a calculated parameter whose formula does not apply to the values.</summary>
    public const string NotCalculableComment = "Non calculable avec ces valeurs : dosage direct requis";

    /// <summary>
    /// The patient whose demographics an order's results are interpreted with: the order's own
    /// patient, or, when that file was merged as a duplicate, the surviving file it points at. The
    /// merge leaves the duplicate's orders where they were, and the duplicate keeps whatever sex or
    /// date of birth was typed on it by mistake; the corrections are made on the survivor.
    /// </summary>
    public Task<Patient?> PatientOfAsync(LabOrder order, CancellationToken ct = default) =>
        patients.SurvivorAsync(order.PatientId, ct: ct);

    /// <summary>
    /// The reagent lot in use for each parameter on the site, from the stock (opened, released, in
    /// date, first expiry first). Empty when nothing is unambiguous.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, Guid>> ReagentLotsInUseAsync(
        IReadOnlyCollection<Guid> testParameterIds, Guid siteId, CancellationToken ct = default) =>
        testParameterIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await queries.GetReagentLotsInUseAsync(testParameterIds, siteId, clock.Today, ct)
              ?? new Dictionary<Guid, Guid>();

    /// <summary>
    /// The lot awaiting release whose evaluation run is started on the analyser, for each control
    /// target (target to its parameter) that run still expects. That control belongs to the lot.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, Guid>> ReagentLotsUnderEvaluationAsync(
        IReadOnlyDictionary<Guid, Guid> parameterOfTarget, Guid siteId, Guid instrumentId,
        CancellationToken ct = default) =>
        parameterOfTarget.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await queries.GetReagentLotsUnderEvaluationAsync(parameterOfTarget, siteId, instrumentId,
                                                               clock.Today, ct)
              ?? new Dictionary<Guid, Guid>();

    public async Task<ResultCatalogue> LoadCatalogueAsync(LabOrder order, CancellationToken ct = default)
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

        return new ResultCatalogue(parameters, testsByParameter);
    }

    /// <summary>
    /// Recomputes the calculated parameters, then interprets, delta-checks and flags every
    /// non-validated result of the order that carries a value, and returns the critical ones.
    /// <para>
    /// Only the rows of live analyses take part. A recollection cancels the analysis of the
    /// refused tube and keeps its rows; read alongside the replacement's, a stale value from the
    /// refused tube could feed a formula, or receive the calculated value meant for the new one.
    /// </para>
    /// </summary>
    /// <param name="byUserId">Who calculated values are attributed to: the operator, or the
    /// analyser's service account.</param>
    /// <param name="language">The language the applied interval and the findings are written in.</param>
    public Task<List<CriticalFinding>> RunAsync(LabOrder order, Patient patient,
                                                IReadOnlyList<TestResult> stored,
                                                ResultCatalogue catalogue, Guid byUserId,
                                                AppLanguage language, CancellationToken ct = default) =>
        RunAsync(order, patient, stored, catalogue, byUserId, language, changed: null, ct);

    /// <summary>
    /// As above, but interprets only <paramref name="changed"/> and the calculated values that
    /// move with them; null interprets every value of the order.
    /// <para>
    /// The analyser path passes the values of the tube just imported. The other values of the order
    /// were interpreted, delta-checked and flagged when they were stored, and nothing about them has
    /// changed since; going over them again for every tube cost a history lookup per value — about a
    /// hundred queries per tube on a full panel, and a day's replay that never finished.
    /// </para>
    /// </summary>
    public async Task<List<CriticalFinding>> RunAsync(LabOrder order, Patient patient,
                                                      IReadOnlyList<TestResult> stored,
                                                      ResultCatalogue catalogue, Guid byUserId,
                                                      AppLanguage language,
                                                      IReadOnlyCollection<TestResult>? changed,
                                                      CancellationToken ct = default)
    {
        var live = LiveResults(order, stored);

        var recalculated = RecalculateDerived(live, catalogue, patient, clock.Now, byUserId);

        var toInterpret = changed is null
            ? live
            : [.. live.Where(r => changed.Contains(r) || recalculated.Contains(r))];

        return await InterpretAsync(order, patient, toInterpret, catalogue, language, ct);
    }

    /// <summary>
    /// Moves to result-entered each live analysis whose parameters now all carry a value or are
    /// recorded as not done — calculated ones included, which is what lets a lipid panel whose LDL
    /// was computed here go on to technical validation.
    /// <para>
    /// An analysis sent back to the bench keeps its first run's values as their record, so "all
    /// valued" is true of it from the start. It waits instead for a value stored since the repeat:
    /// otherwise the next save or analyser import anywhere on the order put it straight back on the
    /// validation list with the very figures that were not trusted, without the re-run ever having
    /// been done.
    /// </para>
    /// </summary>
    public static void AdvanceCompletedItems(LabOrder order, IReadOnlyList<TestResult> stored,
                                             DateTimeOffset now)
    {
        foreach (var item in order.Items.Where(i => !i.IsCancelled))
        {
            var itemResults = stored.Where(r => r.LabOrderItemId == item.Id).ToList();
            if (itemResults.Count == 0) continue;

            if (!itemResults.All(IsStored)) continue;

            if (AwaitsRerun(item, itemResults)) continue;

            item.MarkResultEntered(now);
        }
    }

    /// <summary>
    /// Whether the analysis was repeated and none of its values has been stored since: everything
    /// it carries is still the run that was set aside.
    /// </summary>
    public static bool AwaitsRerun(LabOrderItem item, IEnumerable<TestResult> itemResults) =>
        item.RepeatedAt is { } repeatedAt
        && !itemResults.Any(r => IsStored(r) && r.EnteredAt >= repeatedAt);

    private static bool IsStored(TestResult result) =>
        result.HasValue || result.Status == TestResultStatus.NotDone;

    /// <summary>The results of the order's analyses that are not cancelled.</summary>
    public static List<TestResult> LiveResults(LabOrder order, IReadOnlyList<TestResult> stored)
    {
        var liveItemIds = order.Items.Where(i => !i.IsCancelled).Select(i => i.Id).ToHashSet();

        return [.. stored.Where(r => liveItemIds.Contains(r.LabOrderItemId))];
    }

    // ---- Interpretation -------------------------------------------------------------------------

    /// <summary>
    /// Each result is interpreted in its own context: the instrument that produced it and the day
    /// it was measured. A shared context without the instrument re-resolved a TSH from the
    /// analyser — interpreted against its method-specific interval when it arrived — against the
    /// generic interval at the next manual submit on the same order.
    /// <para>
    /// Interpreting clears the delta flag, which described the value as it was; the check runs
    /// again right after, so a value that still fails it is flagged again.
    /// </para>
    /// </summary>
    private async Task<List<CriticalFinding>> InterpretAsync(LabOrder order, Patient patient,
                                                             IReadOnlyList<TestResult> stored,
                                                             ResultCatalogue catalogue,
                                                             AppLanguage language,
                                                             CancellationToken ct)
    {
        var criticalFindings = new List<CriticalFinding>();
        var defectiveTubes = new Dictionary<Guid, bool>();

        var pending = stored
            .Where(r => r.HasValue && !r.IsValidated && catalogue.Parameters.ContainsKey(r.TestParameterId))
            .ToList();

        if (pending.Count == 0) return criticalFindings;

        // The history of every parameter in one lookup, not one per value.
        var history = await queries.GetPreviousValuesAsync(
            order.PatientId, [.. pending.Select(r => r.TestParameterId).Distinct()], order.RegisteredAt, ct);

        foreach (var result in pending)
        {
            var parameter = catalogue.Parameters[result.TestParameterId];

            var range = parameter.ResolveRange(
                ReferenceRangeContext.ForResult(patient, result, clock.Today));
            result.ApplyInterpretation(parameter, range, language, parameter.Unit?.DisplaySymbol);

            if (history.GetValueOrDefault(parameter.Id) is { } previous)
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
                if (!defectiveTubes.TryGetValue(sampleId, out var defective))
                    defectiveTubes[sampleId] = defective =
                        (await samples.GetByIdAsync(sampleId, ct))?.HasDefects == true;

                if (defective) result.FlagSampleDefect();
            }

            if (result.IsCritical)
            {
                criticalFindings.Add(new CriticalFinding(
                    result.Id,
                    parameter.Name.For(language),
                    $"{result.FormatValue(language)} {parameter.Unit?.DisplaySymbol}".Trim(),
                    result.Interpretation));
            }
        }

        return criticalFindings;
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
    /// <returns>The results whose value this changed.</returns>
    private HashSet<TestResult> RecalculateDerived(IReadOnlyList<TestResult> stored, ResultCatalogue catalogue,
                                                   Patient patient, DateTimeOffset now, Guid byUserId)
    {
        var changed = new HashSet<TestResult>();

        var calculated = catalogue.Parameters.Values
            .Where(p => p.IsCalculated)
            .ToDictionary(p => p.Code, p => p.CalculationFormula!, StringComparer.Ordinal);

        if (calculated.Count == 0) return changed;

        var ordering = FormulaEvaluator.ResolveEvaluationOrder(calculated);
        if (ordering.IsFailure) return changed;   // A circular definition is a configuration error;
                                                  // the catalogue screen reports it, entry just skips.

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
                // Whatever the parameter held before is no input for the formulas that read it.
                values.Remove(parameter.Code);

                if (evaluation.FirstError.ResourceKey == FormulaEvaluator.NotApplicableKey)
                {
                    // The inputs are there but outside the formula's domain — Friedewald above
                    // 4 g/L of triglycerides. Reported as not calculable, with the reason, so the
                    // panel can be completed and the biologist sees a direct measurement is needed.
                    if (target.Status != TestResultStatus.NotDone
                        && target.MarkNotDone(NotCalculableComment, now, byUserId).IsSuccess)
                        changed.Add(target);
                    continue;
                }

                // An input that has not been entered yet is the normal case mid-panel, not an
                // error worth surfacing; the value simply stays awaited. But a value computed
                // earlier from an input that has since been removed, or one no longer calculable,
                // no longer has anything behind it and is withdrawn rather than left standing.
                if (target.HasValue || target.Status == TestResultStatus.NotDone)
                {
                    target.ClearValue();
                    ClearNotCalculableComment(target);
                    changed.Add(target);
                }
                continue;
            }

            var rounded = parameter.Round(evaluation.Value);
            values[parameter.Code] = rounded;

            // Unchanged inputs give an unchanged value: re-entering it would only reset the
            // technical validation of a figure nobody changed.
            if (target.Status != TestResultStatus.NotDone && target.NumericValue == rounded
                && target.ValueOperator is null)
                continue;

            ClearNotCalculableComment(target);
            if (target.EnterNumeric(parameter, evaluation.Value, now, byUserId).IsSuccess)
                changed.Add(target);
        }

        return changed;
    }

    private static void ClearNotCalculableComment(TestResult target)
    {
        if (target.TechnicalComment != NotCalculableComment) return;

        target.TechnicalComment = null;
        target.PrintTechnicalComment = false;
    }

    private Dictionary<string, decimal> BuildValueBag(IReadOnlyList<TestResult> stored,
                                                       ResultCatalogue catalogue, Patient patient)
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
}
