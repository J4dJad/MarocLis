using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Billing;

/// <summary>What « Tiers payant » offers on the ticked invoices and the batch selected.</summary>
public sealed record ClaimActions(bool Submit, bool SelectClaimable, bool RecordOutcome);

/// <summary>Where a batch stands with its payer, as its pill says it.</summary>
public enum ClaimBatchState
{
    AwaitingOutcome = 0,

    /// <summary>No answer after sixty days: the batch was probably never received.</summary>
    NeedsChasing = 1,

    PartlyRejected = 2,
    Accepted = 3
}

/// <summary>
/// The rules of « Tiers payant » that need no window: which invoices « Sélectionner les
/// transmissibles » ticks, what may be sent, and where a batch stands. A batch holds one payer:
/// a payer that receives a batch containing another payer's invoices rejects the batch, not the
/// invoice. The workstation's view and the web's screen apply the same ones.
/// </summary>
public static class ClaimScreenRules
{
    /// <summary>The periods the batches are listed over, in days; 180 by default.</summary>
    public static readonly IReadOnlyList<int> Windows = [30, 90, 180, 365];
    public const int DefaultWindow = 180;

    /// <summary>
    /// The payer whose ready invoices are ticked: the highlighted row's when it can be claimed, or
    /// else the first one ready. Ticking every payer's invoices at once left the transmission greyed
    /// out as soon as the list held two payers, which is the usual case.
    /// </summary>
    public static Guid? PayerToSelect(ClaimCandidateRow? current, IEnumerable<ClaimCandidateRow> rows) =>
        current is { IsClaimable: true } ? current.PayerId : rows.FirstOrDefault(c => c.IsClaimable)?.PayerId;

    /// <summary>The invoices ticked for that payer: the ready ones only, so the selection is always submittable.</summary>
    public static IReadOnlyList<ClaimCandidateRow> ClaimableOf(IEnumerable<ClaimCandidateRow> rows, Guid? payerId) =>
        [.. rows.Where(c => c.IsClaimable && c.PayerId == payerId)];

    /// <summary>
    /// One payer per batch, and every member claimable; ticking prepares a transmission, so it is
    /// offered, like « Transmettre », only to whoever may send one.
    /// </summary>
    public static ClaimActions Actions(IReadOnlyList<ClaimCandidateRow> ticked, IReadOnlyList<ClaimCandidateRow> candidates,
                                       ClaimBatchRow? batch, ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(ticked);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(user);

        var may = user.Has(Permissions.Billing.ExportAccounting);
        var onePayer = ticked.Count > 0 && ticked.Select(t => t.PayerId).Distinct().Count() == 1;

        return new ClaimActions(
            Submit: may && onePayer && ticked.All(t => t.IsClaimable),
            SelectClaimable: may && candidates.Any(c => c.IsClaimable),
            RecordOutcome: may && batch is not null);
    }

    /// <summary>Whether the ticked invoices span several payers, which the summary says instead of their value.</summary>
    public static int PayerCount(IEnumerable<ClaimCandidateRow> ticked) => ticked.Select(t => t.PayerId).Distinct().Count();

    public static ClaimBatchState State(ClaimBatchRow batch, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(batch);

        return batch.IsAwaitingOutcome
            ? batch.NeedsChasing(today) ? ClaimBatchState.NeedsChasing : ClaimBatchState.AwaitingOutcome
            : batch.RejectedCount > 0 ? ClaimBatchState.PartlyRejected : ClaimBatchState.Accepted;
    }

    /// <summary>The claim's own state in a batch: abandoned (rejected, never resent), partly refused, settled, or running.</summary>
    public static string InvoiceTone(ClaimInvoiceRow invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        return invoice.IsAbandoned ? "Critical"
            : invoice.WasRejected ? "Warning"
            : invoice.ClaimStatus == ClaimStatus.Settled ? "Ok"
            : "Info";
    }

    /// <summary>The payer's own deadline runs from the date of the act: past sixty days a claim not sent is late.</summary>
    public static bool IsLate(ClaimCandidateRow candidate) => candidate.IsClaimable && candidate.DaysSinceIssue > 60;
}
