using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Treasury;

/// <summary>How a till's variance reads: balanced, within the tolerance, or beyond it.</summary>
public enum VarianceReading
{
    None,
    WithinTolerance,
    BeyondTolerance
}

/// <summary>
/// The rules of « Visa des caisses » that need no window: the period it opens on, the tills still to
/// sign off, how a variance reads, whether the selected till may be signed off by this operator,
/// and whether the comment is required. The workstation's view and the web's screen apply the same
/// ones; the four-eyes rule itself is the domain's (<see cref="CashSession.Reconcile"/>).
/// </summary>
public static class CashReviewRules
{
    /// <summary>The period the screen opens on: the thirty days up to today.</summary>
    public static (DateOnly From, DateOnly To) DefaultPeriod(DateOnly today) => (today.AddDays(-30), today);

    /// <summary>The comment's column: a longer one failed the sign-off.</summary>
    public const int CommentMaxLength = CashSession.ReconciliationCommentMaxLength;

    public static int PendingCount(IEnumerable<CashSessionReviewRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Count(r => !r.IsReconciled);
    }

    /// <summary>A variance at all is read; beyond the tolerance it is the till's problem.</summary>
    public static VarianceReading Reading(decimal? variance) => variance switch
    {
        null or 0m => VarianceReading.None,
        { } v when Math.Abs(v) > CashSession.VarianceToleranceMad => VarianceReading.BeyondTolerance,
        _ => VarianceReading.WithinTolerance
    };

    /// <summary>
    /// The operator's own till, still to sign off: refused by the domain (four eyes), and said so
    /// up front rather than after the comment has been typed.
    /// </summary>
    public static bool IsOwnTill(ICurrentUser user, CashSessionReviewRow? row)
    {
        ArgumentNullException.ThrowIfNull(user);
        return row is { IsReconciled: false } && user.UserId == row.OperatorUserId;
    }

    public static bool CanReconcile(ICurrentUser user, CashSessionReviewRow? row)
    {
        ArgumentNullException.ThrowIfNull(user);
        return row is not null && row.CanBeReconciledBy(user.UserId) && user.Has(Permissions.Billing.ReconcileCashSession);
    }

    /// <summary>A till that balanced is signed off with an optional comment; one that did not needs one.</summary>
    public static bool CommentRequired(CashSessionReviewRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Variance is { } v && v != 0m;
    }
}
