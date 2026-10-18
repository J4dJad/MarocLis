using MarocLIS.Api.Contracts;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Results;
using MarocLIS.Localization;

namespace MarocLIS.Api.Endpoints;

/// <summary>
/// The worklist, an open file, the antériorité of a value and the validation itself.
/// <para>
/// Every rule is the application's: the services decide who may see and release what, and
/// <see cref="ValidationScreenRules"/> decides what each chip holds, which alerts come first and
/// which rows can be ticked — the same code the workstation's « Validation biologique » screen runs.
/// This file only translates.
/// </para>
/// </summary>
public static class ValidationEndpoints
{
    /// <summary>
    /// The shortest QC override justification, as the workstation's prompt requires: « RAS » or « ok »
    /// does not account for releasing a value the quality control declared unreliable.
    /// </summary>
    public const int QcJustificationMinimumLength = 15;

    public static void MapValidationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiProtocol.WorklistRoute, WorklistAsync).RequireAuthorization();

        var files = app.MapGroup("/api/dossiers/{orderId:guid}").RequireAuthorization();
        files.MapGet("", OpenAsync);
        files.MapGet("/parametres/{parameterId:guid}/historique", HistoryAsync);
        files.MapPost("/validation", ValidateAsync);
    }

    private static async Task<IResult> WorklistAsync(
        HttpContext http, ValidationService validation, ICurrentUser user, ILocalizer localizer,
        string? filtre, CancellationToken ct)
    {
        if (!TryParseFilter(filtre, out var filter))
            return ApiProblems.From(http, Error.Validation("Mobile_UnknownFilter", filtre));

        // The whole list, then the chip: the counts of the other chips come from the same rows,
        // as on the workstation.
        var worklist = await validation.GetWorklistAsync(ct: ct);
        if (worklist.IsFailure) return ApiProblems.From(http, worklist);

        var entries = worklist.Value;
        var language = user.Language;

        var counts = new FilterCounts(
            ValidationScreenRules.Count(entries, ValidationFilter.All),
            ValidationScreenRules.Count(entries, ValidationFilter.Urgent),
            ValidationScreenRules.Count(entries, ValidationFilter.Abnormal),
            ValidationScreenRules.Count(entries, ValidationFilter.QcBlocked));

        var shown = entries
            .Where(e => ValidationScreenRules.Matches(e, filter))
            .Select(e => new WorklistEntry(
                e.LabOrderId, e.AccessionNumber, e.PatientName, e.AgeYears, e.Gender.ToString(),
                e.Priority.ToString(), e.RegisteredAt, e.DueAt, e.IsOverdue, e.AwaitingCount,
                e.AbnormalCount, e.CriticalCount, e.UncommunicatedCriticalCount, e.HasQcFailure,
                [.. ValidationScreenRules.Alerts(e)
                    .Select(a => new AlertPill(Tone(a.Tone), localizer.Format(language, a.Key, a.Args)))]))
            .ToList();

        return Results.Ok(new WorklistResponse(FilterName(filter), counts, shown));
    }

    /// <summary>
    /// The file as the validation screen lists it: what awaits release (an entered value is shown,
    /// neither ticked nor tickable) and the headings. Opening it is logged as a consultation by the
    /// entry service, against the phone.
    /// </summary>
    private static async Task<IResult> OpenAsync(
        Guid orderId, HttpContext http, ResultEntryService entry, ICurrentUser user, ILocalizer localizer,
        IClock clock, IReportQueries reports, CancellationToken ct)
    {
        var opened = await entry.OpenAsync(orderId, ct: ct);
        if (opened.IsFailure) return ApiProblems.From(http, opened);

        var sheet = opened.Value;
        var language = user.Language;
        var listed = sheet.Rows.Where(r => ValidationScreenRules.IsListed(r, showValidated: false)).ToList();

        var context = ValidationScreenRules.Context(listed, sheet.Priority, sheet.DueAt, clock.Now)
            .Select(l => new ContextLine(Tone(l.Tone), l.Badge, localizer.Format(language, l.Key, l.Args)))
            .ToList();

        var file = new OrderFile(
            sheet.LabOrderId,
            sheet.AccessionNumber,
            new PatientSummary(sheet.PatientName, sheet.PatientNameArabic, sheet.Gender.ToString(),
                               sheet.AgeYears, sheet.DateOfBirth, sheet.IsPregnant),
            sheet.Priority.ToString(),
            sheet.ClinicalInformation,
            sheet.DueAt,
            ValidationScreenRules.CanIssueReport(sheet.Rows),
            context,
            [.. listed.Select(r => Row(r, language))],
            sheet.Rows.Count(r => !r.IsHeading && r.Status is TestResultStatus.Validated or TestResultStatus.Reported),
            (await reports.GetLatestVersionAsync(orderId, ct))?.VersionNumber,
            Prescriber(sheet.Prescriber, language));

        return Results.Ok(file);
    }

    /// <summary>The prescriber block of the dossier header, by the workstation's rules (<see cref="PrescriberContactRules"/>).</summary>
    public static PrescriberSummary? Prescriber(OrderPrescriber? prescriber, AppLanguage language)
    {
        if (PrescriberContactRules.DisplayName(prescriber, language) is not { } name) return null;

        var contacts = PrescriberContactRules.Contacts(prescriber);
        string? Of(PrescriberContactKind kind) => contacts.FirstOrDefault(c => c.Kind == kind)?.Value;

        var speciality = prescriber!.Speciality?.For(language);

        return new PrescriberSummary(
            name,
            string.IsNullOrWhiteSpace(speciality) ? null : speciality.Trim(),
            string.IsNullOrWhiteSpace(prescriber.City) ? null : prescriber.City.Trim(),
            Of(PrescriberContactKind.CriticalLine),
            Of(PrescriberContactKind.Phone),
            Of(PrescriberContactKind.Mobile),
            Of(PrescriberContactKind.Email),
            Of(PrescriberContactKind.Fax),
            PrescriberContactRules.CriticalContact(prescriber));
    }

    private static async Task<IResult> HistoryAsync(
        Guid orderId, Guid parameterId, HttpContext http, ResultEntryService entry, CancellationToken ct)
    {
        var history = await entry.GetHistoryAsync(orderId, parameterId, ct);
        if (history.IsFailure) return ApiProblems.From(http, history);

        return Results.Ok(history.Value
            .Select(p => new HistoryPoint(p.ValidatedAt, p.Value, p.ValueOperator, p.AccessionNumber, p.Imported))
            .ToList());
    }

    /// <summary>
    /// Applies the biologist's decisions. Each release names the value it was decided on
    /// (<c>displayedEnteredAt</c>); the service refuses one that changed since, and the phone is told
    /// to reload. Everything else — qualification, department scope, quality control, critical
    /// values, the analysis as a whole — is the service's gate, exactly as at the workstation.
    /// </summary>
    private static async Task<IResult> ValidateAsync(
        Guid orderId, ValidationRequest request, HttpContext http, ValidationService validation,
        ICurrentUser user, ILocalizer localizer, CancellationToken ct)
    {
        if (request.Decisions is not { Count: > 0 } decisions)
            return ApiProblems.From(http, Error.Validation("Message.NothingSelected"));

        foreach (var decision in decisions)
            if (ApiProblems.TooLong((decision.Comment, 2000), (decision.QcJustification, 1000)) is { } tooLong)
                return ApiProblems.From(http, tooLong);

        foreach (var decision in decisions.Where(d => d.Validate))
        {
            if (decision.DisplayedEnteredAt is null)
                return ApiProblems.From(http, Error.Validation("Mobile_DisplayedValueRequired"));

            if (decision.QcJustification is { } justification
                && justification.Trim().Length < QcJustificationMinimumLength)
                return ApiProblems.From(http, Error.Validation("Mobile_QcJustificationTooShort",
                                                               QcJustificationMinimumLength));
        }

        var outcome = await validation.ValidateAsync(orderId,
        [
            .. decisions.Select(d => new ValidationDecision(
                d.ResultId,
                d.Validate,
                // As at the workstation: a comment goes with a released line only.
                d.Validate && !string.IsNullOrWhiteSpace(d.Comment) ? new MultilingualText(d.Comment.Trim()) : null,
                string.IsNullOrWhiteSpace(d.QcJustification) ? null : d.QcJustification.Trim(),
                d.DisplayedEnteredAt))
        ], ct);

        if (outcome.IsFailure) return ApiProblems.From(http, outcome);

        var result = outcome.Value;

        return Results.Ok(new ValidationOutcomeResponse(
            result.Validated,
            result.Refused,
            ApiProblems.Describe(localizer, result.Blocked, user.Language),
            result.OrderFullyValidated,
            result.Blocked.Any(e => e.ResourceKey == "Validation_ValueChangedSinceDisplayed")));
    }

    private static ResultRow Row(ResultEntryRow row, AppLanguage language) => new(
        row.TestResultId,
        row.TestParameterId,
        row.AnalysisCode,
        row.AnalysisName.For(language),
        row.ParameterName.For(language),
        row.IsHeading,
        row.IndentLevel,
        row.IsHeading ? null : row.CurrentValue,
        row.UnitSymbol,
        row.ReferenceRangeText,
        row.Interpretation.ToString(),
        [.. Enum.GetValues<ResultFlag>().Where(f => f != ResultFlag.None && row.Flags.HasFlag(f)).Select(f => f.ToString())],
        ValidationScreenRules.Indicator(row),
        ValidationScreenRules.IsAbnormal(row),
        row.PreviousValue,
        row.PreviousDate,
        row.PreviousImported,
        row.InstrumentFlags,
        row.IsCritical,
        row.IsCriticalCommunicated,
        row.Status.ToString(),
        row.EnteredAt,
        ValidationScreenRules.IsReleasable(row),
        ValidationScreenRules.IsPreTicked(row));

    private static bool TryParseFilter(string? value, out ValidationFilter filter)
    {
        filter = value?.Trim().ToLowerInvariant() switch
        {
            null or "" or ApiProtocol.Filters.All => ValidationFilter.All,
            ApiProtocol.Filters.Urgent => ValidationFilter.Urgent,
            ApiProtocol.Filters.Abnormal => ValidationFilter.Abnormal,
            ApiProtocol.Filters.QcBlocked => ValidationFilter.QcBlocked,
            _ => (ValidationFilter)(-1)
        };

        return Enum.IsDefined(filter);
    }

    private static string FilterName(ValidationFilter filter) => filter switch
    {
        ValidationFilter.Urgent => ApiProtocol.Filters.Urgent,
        ValidationFilter.Abnormal => ApiProtocol.Filters.Abnormal,
        ValidationFilter.QcBlocked => ApiProtocol.Filters.QcBlocked,
        _ => ApiProtocol.Filters.All
    };

    internal static string Tone(ValidationTone tone) => tone switch
    {
        ValidationTone.Ok => ApiProtocol.Tones.Ok,
        ValidationTone.Waiting => ApiProtocol.Tones.Waiting,
        ValidationTone.Info => ApiProtocol.Tones.Info,
        ValidationTone.Warning => ApiProtocol.Tones.Warning,
        _ => ApiProtocol.Tones.Critical
    };
}
