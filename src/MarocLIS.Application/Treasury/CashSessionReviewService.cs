using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Treasury;

/// <summary>
/// The supervisor's sign-off of the closed tills (visa des caisses). The operator counts and closes
/// the till; someone else checks the count, the variance and its explanation, the withdrawals
/// against the bank's deposit slips, and marks the session reconciled with a comment.
/// </summary>
public sealed class CashSessionReviewService(
    IRepository<CashSession> sessions,
    ITreasuryQueries queries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<Result<IReadOnlyList<CashSessionReviewRow>>> GetSessionsAsync(
        DateOnly from, DateOnly to, bool pendingOnly, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.ReconcileCashSession);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<CashSessionReviewRow>>(permitted.FirstError);

        if (to < from) return Result.Failure<IReadOnlyList<CashSessionReviewRow>>(Error.Validation("Treasury_PeriodInvalid"));

        return Result.Success(await queries.GetClosedCashSessionsAsync(currentUser.SiteId, from, to, pendingOnly, ct));
    }

    public async Task<Result> ReconcileAsync(Guid sessionId, string? comment, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.ReconcileCashSession);
        if (permitted.IsFailure) return permitted;

        var session = await sessions.GetByIdAsync(sessionId, ct);
        if (session is null) return Result.Failure(Error.NotFound("CashSession_NotFound"));

        var signed = session.Reconcile(clock.Now, currentUser.UserId, comment);
        if (signed.IsFailure) return signed;

        unitOfWork.AuditReason = $"Visa de la caisse du {session.OpenedAt:dd/MM/yyyy} — écart {session.Variance ?? 0m:N2} DH" +
                                 (string.IsNullOrWhiteSpace(comment) ? string.Empty : $" : {comment}");
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
