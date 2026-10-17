using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Samples;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.Samples;

/// <summary>Where a subcontracted act stands in the send-out circuit.</summary>
public enum SubcontractStage
{
    /// <summary>Received here, not sent yet.</summary>
    ToSend = 0,

    /// <summary>Sent on a dispatch slip, results not back yet.</summary>
    AwaitingReturn = 1
}

/// <summary>One subcontracted act in the send-out list.</summary>
public sealed record SubcontractRow(
    Guid ItemId,
    Guid LabOrderId,
    string AccessionNumber,
    string PatientName,
    Gender Gender,
    DateOnly? DateOfBirth,
    OrderPriority Priority,
    string AnalysisCode,
    MultilingualText AnalysisName,
    string? Barcode,
    MultilingualText? ContainerName,
    DateTimeOffset? CollectedAt,
    DateTimeOffset? ReceivedAt,
    Guid? PartnerSiteId,
    string? PartnerCode,
    MultilingualText? PartnerName,
    DateTimeOffset? SentAt,
    string? DispatchNumber,
    string? PartnerReference,
    DateTimeOffset? DueAt)
{
    public SubcontractStage Stage =>
        SentAt is null ? SubcontractStage.ToSend : SubcontractStage.AwaitingReturn;
}

/// <summary>A partner laboratory the act can be sent to.</summary>
public sealed record SubcontractPartner(Guid Id, string Code, MultilingualText Name);

/// <summary>A dispatch slip as created: one per partner laboratory.</summary>
public sealed record SubcontractDispatch(string Number, Guid PartnerSiteId, int ItemCount);

/// <summary>Everything a dispatch slip prints.</summary>
public sealed record SubcontractSlip(
    string Number,
    SubcontractPartner Partner,
    DateTimeOffset SentAt,
    string SentByName,
    IReadOnlyList<SubcontractRow> Items);

/// <summary>
/// The send-out circuit for analyses the laboratory does not perform itself.
/// <para>
/// The catalogue already says which analyses are subcontracted and to whom, and the report already
/// marks them. What was missing is the middle: knowing which tubes are waiting to leave, sending
/// them on a numbered slip the courier and the partner can sign, keeping the partner's own
/// reference, and seeing which results are late coming back. The return itself is the ordinary
/// result entry: entering the partner's values marks the act as returned and flags each result as
/// subcontracted, so the report says so.
/// </para>
/// </summary>
public sealed class SubcontractingService(
    IRepository<LabOrder> orders,
    IRepository<Sample> samples,
    ISubcontractQueries queries,
    ISequenceAllocator sequences,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<Result<IReadOnlyList<SubcontractRow>>> GetWorklistAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Samples.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<SubcontractRow>>(permitted.FirstError);

        return Result.Success(await queries.GetWorklistAsync(currentUser.SiteId, ct));
    }

    public async Task<Result<IReadOnlyList<SubcontractPartner>>> GetPartnersAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Samples.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<SubcontractPartner>>(permitted.FirstError);

        return Result.Success(await queries.GetPartnersAsync(ct));
    }

    /// <summary>
    /// Sends the given acts. One slip is numbered per partner laboratory, so a selection spanning
    /// two partners gives two slips. <paramref name="partnerOverride"/> sends everything to one
    /// partner instead of the catalogue's, for the day the usual one cannot take the samples.
    /// </summary>
    public async Task<Result<IReadOnlyList<SubcontractDispatch>>> DispatchAsync(
        IReadOnlyList<Guid> itemIds, Guid? partnerOverride, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Samples.Subcontract);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<SubcontractDispatch>>(permitted.FirstError);

        if (itemIds.Count == 0)
            return Result.Failure<IReadOnlyList<SubcontractDispatch>>(Error.Validation("Subcontract_NothingSelected"));

        var partners = (await queries.GetPartnersAsync(ct)).ToDictionary(p => p.Id);

        if (partnerOverride is { } chosen && !partners.ContainsKey(chosen))
            return Result.Failure<IReadOnlyList<SubcontractDispatch>>(Error.Validation("Subcontract_NoPartner"));

        var orderIds = await queries.GetOrderIdsForItemsAsync(itemIds, ct);
        var items = new List<(LabOrder Order, LabOrderItem Item, Guid Partner)>();

        foreach (var orderId in orderIds)
        {
            var order = await orders.GetByIdWithDetailsAsync(orderId, ct);

            // This site's acts only, as the list shows them. Another site's act sent from here took
            // this site's slip number and was then on no slip either site could print.
            if (order is null || order.SiteId != currentUser.SiteId) continue;

            foreach (var item in order.Items.Where(i => itemIds.Contains(i.Id)))
            {
                // An act performed here has no partner to look up: asking for one hid the reason.
                if (!item.IsSubcontracted)
                    return Result.Failure<IReadOnlyList<SubcontractDispatch>>(
                        Error.Conflict("Subcontract_NotSubcontracted", order.AccessionNumber));

                var partner = partnerOverride ?? item.SubcontractorSiteId;

                if (partner is not { } partnerId || !partners.ContainsKey(partnerId))
                    return Result.Failure<IReadOnlyList<SubcontractDispatch>>(
                        Error.Validation("Subcontract_NoPartner"));

                items.Add((order, item, partnerId));
            }
        }

        if (items.Count == 0)
            return Result.Failure<IReadOnlyList<SubcontractDispatch>>(Error.NotFound("Subcontract_NothingSelected"));

        var now = clock.Now;
        var created = new List<SubcontractDispatch>();

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        foreach (var group in items.GroupBy(x => x.Partner))
        {
            var number = await sequences.NextAsync(SequencePurpose.SubcontractDispatch, currentUser.SiteId,
                                                   clock.Today.Year, ct);
            if (number.IsFailure)
            {
                await unitOfWork.RollbackAsync(ct);
                return Result.Failure<IReadOnlyList<SubcontractDispatch>>(number.FirstError);
            }

            foreach (var (order, item, _) in group)
            {
                var sent = item.SendToSubcontractor(group.Key, number.Value, now, currentUser.UserId);
                if (sent.IsFailure)
                {
                    await unitOfWork.RollbackAsync(ct);
                    return Result.Failure<IReadOnlyList<SubcontractDispatch>>(
                        Error.Conflict(sent.FirstError.ResourceKey, order.AccessionNumber));
                }

                orders.Update(order);
            }

            // The tube's own history says where it went, on which slip.
            foreach (var sampleId in group.Select(x => x.Item.SampleId).OfType<Guid>().Distinct())
            {
                var sample = await samples.GetByIdAsync(sampleId, ct);
                if (sample is null) continue;

                sample.AddEvent(SampleEventKind.SentToSubcontractor, now, currentUser.UserId,
                                $"{number.Value} → {partners[group.Key].Code}");
                samples.Update(sample);
            }

            created.Add(new SubcontractDispatch(number.Value, group.Key, group.Count()));
        }

        unitOfWork.AuditReason = "Envoi en sous-traitance : " +
                                 string.Join(", ", created.Select(c => $"{c.Number} ({c.ItemCount} analyse(s) → {partners[c.PartnerSiteId].Code})"));

        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return Result.Success<IReadOnlyList<SubcontractDispatch>>(created);
    }

    /// <summary>The slip as sent, for printing or reprinting.</summary>
    public async Task<Result<SubcontractSlip>> GetSlipAsync(string dispatchNumber, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Samples.View);
        if (permitted.IsFailure) return Result.Failure<SubcontractSlip>(permitted.FirstError);

        var slip = await queries.GetSlipAsync(currentUser.SiteId, dispatchNumber, ct);

        return slip is null
            ? Result.Failure<SubcontractSlip>(Error.NotFound("Subcontract_SlipNotFound", dispatchNumber))
            : Result.Success(slip);
    }

    /// <summary>Keeps the partner laboratory's own number for the request, for chasing the result.</summary>
    public async Task<Result> SetPartnerReferenceAsync(Guid itemId, string? reference, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Samples.Subcontract);
        if (permitted.IsFailure) return permitted;

        var orderIds = await queries.GetOrderIdsForItemsAsync([itemId], ct);
        var order = orderIds.Count == 0 ? null : await orders.GetByIdWithDetailsAsync(orderIds[0], ct);
        var item = order?.Items.FirstOrDefault(i => i.Id == itemId);

        if (order is null || item is null || order.SiteId != currentUser.SiteId)
            return Result.Failure(Error.NotFound("Subcontract_NothingSelected"));

        if (!item.IsSubcontracted)
            return Result.Failure(Error.Conflict("Subcontract_NotSubcontracted", order.AccessionNumber));

        var value = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();

        if (value is { Length: > 60 })
            return Result.Failure(Error.Validation("Subcontract_ReferenceTooLong"));

        item.SubcontractorReference = value;
        orders.Update(order);

        unitOfWork.AuditReason = $"Référence du laboratoire partenaire sur {order.AccessionNumber} : {value ?? "effacée"}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }
}

/// <summary>Read side of the send-out circuit.</summary>
public interface ISubcontractQueries
{
    /// <summary>Subcontracted acts received and not sent, then sent and awaiting results.</summary>
    Task<IReadOnlyList<SubcontractRow>> GetWorklistAsync(Guid siteId, CancellationToken ct = default);

    Task<IReadOnlyList<SubcontractPartner>> GetPartnersAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetOrderIdsForItemsAsync(IReadOnlyList<Guid> itemIds, CancellationToken ct = default);

    Task<SubcontractSlip?> GetSlipAsync(Guid siteId, string dispatchNumber, CancellationToken ct = default);
}
