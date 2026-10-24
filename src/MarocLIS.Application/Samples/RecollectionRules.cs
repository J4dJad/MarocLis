using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;

namespace MarocLIS.Application.Samples;

/// <summary>The chips above the patients to call back: everyone, those never reached, those drawn in red.</summary>
public enum RecollectionFilter
{
    All = 0,
    NotContacted = 1,
    Priority = 2
}

/// <summary>What the « Rappels » card offers on the patient highlighted.</summary>
public sealed record RecollectionActions(bool RecordCall, bool SendSms, bool Abandon)
{
    public static readonly RecollectionActions None = new(false, false, false);
}

/// <summary>The rules of the « Rappels » list (tubes to draw again) that need no window.</summary>
public static class RecollectionRules
{
    /// <summary>How long a rejected tube may wait for its patient to be reached.</summary>
    public static readonly TimeSpan ContactDelay = TimeSpan.FromHours(24);

    /// <summary>A patient nobody has reached a day after the tube was rejected.</summary>
    public static bool IsOverdue(RecollectionRow row, DateTimeOffset now) =>
        row.ContactCount == 0 && now - row.RejectedAt > ContactDelay;

    /// <summary>The rows drawn in red: an urgent file, or a patient not reached within a day.</summary>
    public static bool IsPriority(RecollectionRow row, DateTimeOffset now) =>
        row.Priority != OrderPriority.Routine || IsOverdue(row, now);

    /// <summary>
    /// Whether a row is listed under a chip and the search: the file, the patient, the new tube's
    /// barcode or the telephone number containing the text typed.
    /// </summary>
    public static bool Matches(RecollectionRow row, RecollectionFilter filter, string? term, DateTimeOffset now)
    {
        var kept = filter switch
        {
            RecollectionFilter.NotContacted => row.ContactCount == 0,
            RecollectionFilter.Priority => IsPriority(row, now),
            _ => true
        };

        var text = term?.Trim() ?? string.Empty;

        return kept
               && (text.Length == 0
                   || row.AccessionNumber.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || row.PatientName.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || row.ReplacementBarcode.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || (row.Phone?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    /// <summary>
    /// The card's actions: the call and the SMS need the right to edit the file — the SMS a channel
    /// configured — and closing a case the right to cancel; none without a patient highlighted.
    /// </summary>
    public static RecollectionActions Actions(RecollectionRow? row, bool canSendSms, ICurrentUser user) =>
        row is null
            ? RecollectionActions.None
            : new RecollectionActions(
                RecordCall: user.Has(Permissions.Orders.Edit),
                SendSms: canSendSms && user.Has(Permissions.Orders.Edit),
                Abandon: user.Has(Permissions.Orders.Cancel));
}
