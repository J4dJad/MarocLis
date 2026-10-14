using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Samples;

namespace MarocLIS.Application.Samples;

/// <summary>How the patient was told to come back.</summary>
public enum RecallChannel
{
    Phone = 0,
    Sms = 1,
    InPerson = 2
}

/// <summary>A refused tube whose replacement has not been drawn yet: a patient to call back.</summary>
public sealed record RecollectionRow(
    Guid RejectedSampleId,
    Guid ReplacementSampleId,
    Guid LabOrderId,
    string AccessionNumber,
    string PatientName,
    string? Phone,
    OrderPriority Priority,
    string RejectedBarcode,
    string ReplacementBarcode,
    SampleRejectionReason Reason,
    string? Comment,
    DateTimeOffset RejectedAt,
    string AnalysisCodes,
    int ContactCount,
    DateTimeOffset? LastContactAt,
    string? LastContactDetail);

/// <summary>
/// The patients to bring back after a refused tube.
/// <para>
/// A recollection used to stop at the replacement label: nothing said which patients still had to
/// come back, whether anybody had called them, or what to do when one never returned. The order
/// simply waited. This list is that follow-up: every replacement tube not drawn yet, with the
/// patient's number, the calls and messages already made, and a way to close the case when the
/// patient will not come.
/// </para>
/// </summary>
public sealed class RecollectionService(
    IRepository<Sample> samples,
    IRepository<LabOrder> orders,
    IRepository<LabSite> sites,
    IRecollectionQueries queries,
    IMessageSender messages,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    public bool CanSendSms => messages.CanSendSms;

    public async Task<Result<IReadOnlyList<RecollectionRow>>> GetPendingAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Samples.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<RecollectionRow>>(permitted.FirstError);

        return Result.Success(await queries.GetPendingAsync(currentUser.SiteId, ct));
    }

    /// <summary>Records a call or a visit: who was reached, or that nobody answered.</summary>
    public async Task<Result> RecordContactAsync(Guid replacementSampleId, RecallChannel channel, string note,
                                                 CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Orders.Edit);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(note))
            return Result.Failure(Error.Validation("Recollection_ContactNoteRequired"));

        var tube = await PendingReplacementAsync(replacementSampleId, ct);
        if (tube.IsFailure) return tube;

        tube.Value.AddEvent(SampleEventKind.PatientRecalled, clock.Now, currentUser.UserId,
                            $"{Describe(channel)} : {note.Trim()}", workstation: currentUser.Workstation);
        samples.Update(tube.Value);

        unitOfWork.AuditReason = $"Rappel du patient pour re-prélèvement ({tube.Value.Barcode}) : {Describe(channel)}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Texts the patient to come back. The message names the laboratory and the file number only:
    /// no analysis, no reason for the refusal.
    /// </summary>
    public async Task<Result> SendRecallSmsAsync(Guid replacementSampleId, string phone,
                                                 CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Orders.Edit);
        if (permitted.IsFailure) return permitted;

        if (!messages.CanSendSms) return Result.Failure(Error.Validation("Messaging_SmsNotConfigured"));

        var tube = await PendingReplacementAsync(replacementSampleId, ct);
        if (tube.IsFailure) return tube;

        var order = await orders.GetByIdAsync(tube.Value.LabOrderId, ct);
        if (order is null) return Result.Failure(Error.NotFound("Order_NotFound"));

        var site = await sites.GetByIdAsync(order.SiteId, ct);
        var laboratory = site?.Name.For(AppLanguage.French) ?? string.Empty;

        var text = $"{laboratory} : merci de repasser au laboratoire pour un nouveau prélèvement " +
                   $"(dossier {order.AccessionNumber})."
                   + (string.IsNullOrWhiteSpace(site?.Phone) ? string.Empty : $" Tél. {site!.Phone}");

        var sent = await messages.SendSmsAsync(phone, text, ct);
        if (sent.IsFailure) return sent;

        tube.Value.AddEvent(SampleEventKind.PatientRecalled, clock.Now, currentUser.UserId,
                            $"{Describe(RecallChannel.Sms)} : {phone.Trim()}", workstation: currentUser.Workstation);
        samples.Update(tube.Value);

        unitOfWork.AuditReason = $"SMS de rappel pour re-prélèvement ({tube.Value.Barcode})";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Closes a recollection the patient will not come back for: the analyses awaiting the new tube
    /// are cancelled with the reason, and the unused tube is withdrawn. The rest of the order, if
    /// any, goes on to its report.
    /// </summary>
    public async Task<Result> AbandonAsync(Guid replacementSampleId, string reason, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Orders.Cancel);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Order_CancellationReasonRequired"));

        var tube = await PendingReplacementAsync(replacementSampleId, ct);
        if (tube.IsFailure) return tube;

        var order = await orders.GetByIdWithDetailsAsync(tube.Value.LabOrderId, ct);
        if (order is null) return Result.Failure(Error.NotFound("Order_NotFound"));

        var now = clock.Now;
        var text = $"Re-prélèvement abandonné : {reason.Trim()}";

        foreach (var item in order.Items.Where(i => i.SampleId == tube.Value.Id && !i.IsCancelled).ToList())
        {
            var cancelled = order.CancelItem(item.Id, now, currentUser.UserId, text);
            if (cancelled.IsFailure) return cancelled;
        }

        var withdrawn = tube.Value.Discard(now, currentUser.UserId, text);
        if (withdrawn.IsFailure) return withdrawn;

        orders.Update(order);
        samples.Update(tube.Value);

        unitOfWork.AuditReason = $"{text} — dossier {order.AccessionNumber}, tube {tube.Value.Barcode}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    private async Task<Result<Sample>> PendingReplacementAsync(Guid replacementSampleId, CancellationToken ct)
    {
        var tube = await samples.GetByIdWithDetailsAsync(replacementSampleId, ct);

        if (tube is null) return Result.Failure<Sample>(Error.NotFound("Sample_NotFound", replacementSampleId));

        return tube.Status == SampleStatus.AwaitingCollection
            ? Result.Success(tube)
            : Result.Failure<Sample>(Error.Conflict("Recollection_AlreadyDrawn", tube.Barcode));
    }

    private static string Describe(RecallChannel channel) => channel switch
    {
        RecallChannel.Phone => "Appel",
        RecallChannel.Sms => "SMS",
        _ => "Sur place"
    };
}

/// <summary>Read side of the recollection follow-up.</summary>
public interface IRecollectionQueries
{
    /// <summary>Replacement tubes not drawn yet, oldest refusal first, urgent ones on top.</summary>
    Task<IReadOnlyList<RecollectionRow>> GetPendingAsync(Guid siteId, CancellationToken ct = default);
}
