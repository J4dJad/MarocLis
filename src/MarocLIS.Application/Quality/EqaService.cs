using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.Quality;

/// <summary>One analyte in one round of an external quality assessment scheme.</summary>
public sealed record EqaRow(
    Guid Id,
    string SchemeName,
    string RoundReference,
    DateOnly RoundDate,
    DateOnly? SubmissionDeadline,
    DateOnly? SubmittedOn,
    string ParameterCode,
    MultilingualText ParameterName,
    string? InstrumentCode,
    decimal? ReportedValue,
    decimal? AssignedValue,
    decimal? PeerGroupMean,
    decimal? PeerGroupSd,
    decimal? ZScore,
    decimal? BiasPercent,
    EqaOutcome Outcome,
    string? NonConformityReference,
    MultilingualText? InvestigationNotes,
    bool IsSubmissionOverdue);

/// <summary>A choice for a picker: an id with its code and name.</summary>
public sealed record EqaOption(Guid Id, string Code, MultilingualText Name);

public sealed record RegisterEqaRoundRequest
{
    public required string SchemeName { get; init; }
    public required string RoundReference { get; init; }
    public required DateOnly RoundDate { get; init; }
    public DateOnly? SubmissionDeadline { get; init; }
    public Guid? InstrumentId { get; init; }

    /// <summary>The analytes the round's samples cover; one participation is recorded per analyte.</summary>
    public required IReadOnlyList<Guid> TestParameterIds { get; init; }
}

public sealed record EqaEvaluation
{
    public decimal? AssignedValue { get; init; }
    public decimal? PeerGroupMean { get; init; }
    public decimal? PeerGroupSd { get; init; }

    /// <summary>The organiser's Z-score. Computed from the peer group when not published.</summary>
    public decimal? ZScore { get; init; }

    /// <summary>
    /// Set only for the two outcomes a Z-score cannot express: a missed round and a result the
    /// organiser excluded. Every other outcome follows from the score.
    /// </summary>
    public EqaOutcome? OutcomeOverride { get; init; }

    public MultilingualText? InvestigationNotes { get; init; }
}

/// <summary>
/// External quality assessment (évaluation externe de la qualité).
/// <para>
/// ISO 15189 §7.3.7.3 makes participation mandatory where a scheme exists, and — the part a
/// spreadsheet never enforces — requires the laboratory to act on unsatisfactory performance. So an
/// unsatisfactory evaluation opens a non-conformity in the same transaction rather than waiting for
/// someone to remember to.
/// </para>
/// </summary>
public sealed class EqaService(
    IRepository<EqaParticipation> participations,
    IRepository<NonConformity> nonConformities,
    IEqaQueries queries,
    ISequenceAllocator sequences,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<Result<IReadOnlyList<EqaRow>>> GetParticipationsAsync(
        int year, bool onlyPending, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<EqaRow>>(permitted.FirstError);

        return Result.Success(
            await queries.GetParticipationsAsync(currentUser.SiteId, year, onlyPending, clock.Today, ct));
    }

    public async Task<Result<IReadOnlyList<EqaOption>>> GetParameterOptionsAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<EqaOption>>(permitted.FirstError);

        return Result.Success(await queries.GetParameterOptionsAsync(ct));
    }

    public async Task<Result<IReadOnlyList<EqaOption>>> GetInstrumentOptionsAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<EqaOption>>(permitted.FirstError);

        return Result.Success(await queries.GetInstrumentOptionsAsync(currentUser.SiteId, ct));
    }

    public async Task<Result<int>> RegisterRoundAsync(RegisterEqaRoundRequest request,
                                                      CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ReviewQc);
        if (permitted.IsFailure) return Result.Failure<int>(permitted.FirstError);

        if (string.IsNullOrWhiteSpace(request.SchemeName))
            return Result.Failure<int>(Error.Validation("Eqa_SchemeRequired"));
        if (string.IsNullOrWhiteSpace(request.RoundReference))
            return Result.Failure<int>(Error.Validation("Eqa_RoundRequired"));
        if (request.TestParameterIds.Count == 0)
            return Result.Failure<int>(Error.Validation("Eqa_ParameterRequired"));
        if (request.SubmissionDeadline is { } deadline && deadline < request.RoundDate)
            return Result.Failure<int>(Error.Validation("Eqa_DeadlineBeforeRound"));

        var scheme = request.SchemeName.Trim();
        var round = request.RoundReference.Trim();

        var already = await queries.GetRegisteredParameterIdsAsync(currentUser.SiteId, scheme, round, ct);

        var added = 0;

        foreach (var parameterId in request.TestParameterIds.Distinct())
        {
            // Registering the same round twice is an easy slip when the samples arrive in two
            // parcels; a duplicate row would then be scored twice in the year's statistics.
            if (already.Contains(parameterId)) continue;

            await participations.AddAsync(
                new EqaParticipation(scheme, round, parameterId, currentUser.SiteId, request.RoundDate)
                {
                    SubmissionDeadline = request.SubmissionDeadline,
                    InstrumentId = request.InstrumentId
                }, ct);

            added++;
        }

        if (added == 0)
            return Result.Failure<int>(Error.Conflict("Eqa_RoundAlreadyRegistered", round));

        unitOfWork.AuditReason = $"Enregistrement de la campagne EEQ {scheme} {round}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(added);
    }

    public async Task<Result> RecordSubmissionAsync(Guid participationId, decimal reportedValue,
                                                    DateOnly submittedOn,
                                                    CancellationToken ct = default)
    {
        // Sending the value measured is entry work; judging the evaluation stays with the review.
        var permitted = guard.Require(Permissions.Quality.EnterQc);
        if (permitted.IsFailure) return permitted;

        if (submittedOn > clock.Today)
            return Result.Failure(Error.Validation("Eqa_SubmissionInFuture"));

        var participation = await participations.GetByIdAsync(participationId, ct);
        if (participation is null) return Result.Failure(Error.NotFound("Eqa_NotFound"));

        // A result changed after the organiser's report is out would be marking one's own homework.
        if (participation.Outcome is not EqaOutcome.Pending)
            return Result.Failure(Error.Conflict("Eqa_AlreadyEvaluated"));

        participation.ReportedValue = reportedValue;
        participation.SubmittedOn = submittedOn;

        unitOfWork.AuditReason =
            $"Résultat transmis pour {participation.SchemeName} {participation.RoundReference}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Records the organiser's evaluation.
    /// <para>
    /// When the organiser publishes a Z-score it is taken as is: it is the figure the accreditation
    /// body will read. Otherwise it is computed from the peer group, and failing that the bias
    /// against the assigned value is still recorded so the round is not left looking unscored.
    /// </para>
    /// </summary>
    public async Task<Result<string?>> RecordEvaluationAsync(Guid participationId,
                                                             EqaEvaluation evaluation,
                                                             CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ReviewQc);
        if (permitted.IsFailure) return Result.Failure<string?>(permitted.FirstError);

        if (evaluation.OutcomeOverride is { } forced
            && forced is not (EqaOutcome.NotSubmitted or EqaOutcome.Excluded))
            return Result.Failure<string?>(Error.Validation("Eqa_OutcomeFollowsScore"));

        if (evaluation.PeerGroupSd is <= 0m)
            return Result.Failure<string?>(Error.Validation("Eqa_SdMustBePositive"));

        var participation = await participations.GetByIdAsync(participationId, ct);
        if (participation is null) return Result.Failure<string?>(Error.NotFound("Eqa_NotFound"));

        var zScore = evaluation.ZScore
            ?? (participation.ReportedValue is { } reported
                && evaluation.PeerGroupSd is { } sd
                && (evaluation.PeerGroupMean ?? evaluation.AssignedValue) is { } target
                    ? Math.Round((reported - target) / sd, 2)
                    : null);

        // Checked before anything is written to the tracked entity, so a refused evaluation leaves
        // nothing behind for a later save on the same screen to pick up.
        if (evaluation.OutcomeOverride is null)
        {
            if (participation.ReportedValue is null)
                return Result.Failure<string?>(Error.Validation("Eqa_NoReportedValue"));
            if (zScore is null)
                return Result.Failure<string?>(Error.Validation("Eqa_ScoreRequired"));
        }

        participation.AssignedValue = evaluation.AssignedValue;
        participation.PeerGroupMean = evaluation.PeerGroupMean;
        participation.PeerGroupSd = evaluation.PeerGroupSd;
        participation.InvestigationNotes = evaluation.InvestigationNotes;
        participation.ZScore = zScore;

        participation.BiasPercent =
            participation.ReportedValue is { } value && evaluation.AssignedValue is { } assigned
                                                      && assigned != 0m
                ? Math.Round((value - assigned) / assigned * 100m, 2)
                : null;

        participation.Outcome = evaluation.OutcomeOverride ?? participation.ClassifyFromZScore();

        participation.ReviewedOn = clock.Today;
        participation.ReviewedByUserId = currentUser.UserId;

        if (!participation.RequiresNonConformity || participation.NonConformityId is not null)
        {
            unitOfWork.AuditReason =
                $"Évaluation EEQ {participation.RoundReference} : {participation.Outcome}";
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success<string?>(null);
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var reference = await sequences.NextAsync(SequencePurpose.NonConformity, null,
                                                  clock.Today.Year, ct);
        if (reference.IsFailure) return Result.Failure<string?>(reference.FirstError);

        var parameterCode = await queries.GetParameterCodeAsync(participation.TestParameterId, ct);
        var z = participation.ZScore?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        var nonConformity = new NonConformity(
            reference.Value,
            currentUser.SiteId,
            NonConformityCategory.Analytical,
            NonConformitySeverity.Major,
            new MultilingualText(
                $"EEQ non satisfaisante — {participation.SchemeName} {participation.RoundReference}, {parameterCode} (Z = {z})",
                $"تقييم خارجي غير مرضٍ — {participation.SchemeName} {participation.RoundReference}، {parameterCode} (Z = {z})",
                $"Unsatisfactory EQA — {participation.SchemeName} {participation.RoundReference}, {parameterCode} (Z = {z})"),
            clock.Now,
            currentUser.UserId)
        {
            DetectionSource = DetectionSource.ExternalQualityAssessment,
            InstrumentId = participation.InstrumentId,
            DueDate = clock.Today.AddDays(14)
        };

        await nonConformities.AddAsync(nonConformity, ct);
        participation.NonConformityId = nonConformity.Id;

        unitOfWork.AuditReason =
            $"EEQ non satisfaisante {participation.RoundReference} — ouverture de la NC {reference.Value}";
        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return Result.Success<string?>(reference.Value);
    }
}

/// <summary>Read-side queries for the external quality assessment screen.</summary>
public interface IEqaQueries
{
    Task<IReadOnlyList<EqaRow>> GetParticipationsAsync(Guid siteId, int year, bool onlyPending,
                                                       DateOnly today, CancellationToken ct = default);

    Task<IReadOnlyList<EqaOption>> GetParameterOptionsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<EqaOption>> GetInstrumentOptionsAsync(Guid siteId,
                                                             CancellationToken ct = default);

    Task<IReadOnlySet<Guid>> GetRegisteredParameterIdsAsync(Guid siteId, string schemeName,
                                                            string roundReference,
                                                            CancellationToken ct = default);

    Task<string> GetParameterCodeAsync(Guid testParameterId, CancellationToken ct = default);
}
