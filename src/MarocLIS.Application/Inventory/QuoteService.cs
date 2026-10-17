using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.Inventory;

/// <summary>One request for quotation in the register.</summary>
public sealed record QuoteRequestRow(
    Guid Id,
    string Number,
    DateOnly RequestedOn,
    string? Subject,
    QuoteRequestStatus Status,
    DateOnly? ResponseDueDate,
    int ItemCount,
    int SupplierCount,
    int OfferCount,
    string RaisedByName,
    string? AwardedSupplierName,
    string? PurchaseOrderNumber);

public sealed record QuoteItemRow(Guid Id, int LineNumber, Guid StockItemId, string Code, string Designation,
                                  string Unit, decimal Quantity);

public sealed record QuoteSupplierRow(Guid SupplierId, string Code, string Name, string? Email, bool IsApproved,
                                      DateTimeOffset? SentAt, bool HasAnswered);

public sealed record QuoteOfferRow(
    Guid Id,
    Guid SupplierId,
    string SupplierName,
    DateOnly ReceivedOn,
    DateOnly? ValidUntil,
    int? DeliveryDelayDays,
    string? Conditions,
    string? SupplierReference,
    bool IsExpired,
    decimal TotalExcludingVat,
    int MissingItems,
    string? FileName);

/// <summary>A request with its items, the suppliers asked, their answers and the comparison grid.</summary>
public sealed record QuoteRequestDetail(
    Guid Id,
    string Number,
    QuoteRequestStatus Status,
    DateOnly RequestedOn,
    string? Subject,
    DateOnly? ResponseDueDate,
    string? Notes,
    Guid? AwardedQuoteId,
    IReadOnlyList<QuoteItemRow> Items,
    IReadOnlyList<QuoteSupplierRow> Suppliers,
    IReadOnlyList<QuoteOfferRow> Offers,
    IReadOnlyList<QuoteComparisonLine> Comparison,
    bool OrderCancelled = false);

/// <summary>An article that can be put out to quotation; captive reagents name their contract.</summary>
public sealed record QuotableItem(Guid Id, string Code, MultilingualText Name, string UnitOfMeasure,
                                  decimal ReorderQuantity, string? ContractReference);

public sealed record QuoteSupplierOption(Guid Id, string Code, string Name, string? Email, bool IsApproved);

/// <summary>A reagent bought under an active instrument contract, at the contracted price.</summary>
public sealed record CaptiveReagent(Guid StockItemId, Guid ContractId, string ContractReference,
                                    Guid SupplierId, decimal? ContractPriceMad);

public sealed record QuoteItemInput(Guid StockItemId, decimal Quantity);

public sealed record CreateQuoteRequest
{
    public string? Subject { get; init; }
    public DateOnly? ResponseDueDate { get; init; }
    public string? Notes { get; init; }
    public required IReadOnlyList<QuoteItemInput> Items { get; init; }
    public required IReadOnlyList<Guid> SupplierIds { get; init; }
}

public sealed record RecordQuoteOfferRequest
{
    public required Guid SupplierId { get; init; }
    public required DateOnly ReceivedOn { get; init; }
    public DateOnly? ValidUntil { get; init; }
    public int? DeliveryDelayDays { get; init; }
    public string? Conditions { get; init; }
    public string? SupplierReference { get; init; }
    public required IReadOnlyList<SupplierQuoteLineInput> Lines { get; init; }

    /// <summary>The supplier's quotation as received, optional.</summary>
    public string? FileName { get; init; }
    public byte[]? FileContent { get; init; }
}

/// <summary>How sending a request by e-mail went, supplier by supplier.</summary>
public sealed record QuoteSendOutcome(int Sent, IReadOnlyList<string> WithoutEmail, IReadOnlyList<string> Failed);

/// <summary>
/// Requests for quotation (demandes de devis) for purchases outside a contract.
/// <para>
/// An article that is a captive reagent of an active instrument contract is refused here: its price
/// and its supplier are fixed by the contract, and quoting it elsewhere is both pointless and a way
/// of drifting from the commitment the contract penalises. The off-contract purchases — consumables,
/// reagents of analysers the laboratory owns — are the ones a comparison actually saves money on.
/// </para>
/// </summary>
public sealed class QuoteService(
    IRepository<QuoteRequest> requests,
    IRepository<PurchaseOrder> orders,
    IRepository<PurchasingDocument> documents,
    IQuoteQueries queries,
    IPurchaseOrderQueries orderQueries,
    ISequenceAllocator sequences,
    IMessageSender messages,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    public bool CanEmail => messages.CanSendEmail;

    public async Task<Result<IReadOnlyList<QuoteRequestRow>>> GetRequestsAsync(
        bool onlyOpen = false, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.ManageQuotes);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<QuoteRequestRow>>(permitted.FirstError);

        return Result.Success(await queries.GetRequestsAsync(currentUser.SiteId, onlyOpen, ct));
    }

    public async Task<Result<QuoteRequestDetail>> GetDetailAsync(Guid requestId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.ManageQuotes);
        if (permitted.IsFailure) return Result.Failure<QuoteRequestDetail>(permitted.FirstError);

        var detail = await queries.GetDetailAsync(requestId, clock.Today, ct);
        return detail is null
            ? Result.Failure<QuoteRequestDetail>(Error.NotFound("Quote_NotFound"))
            : Result.Success(detail);
    }

    public async Task<Result<IReadOnlyList<QuotableItem>>> GetQuotableItemsAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.ManageQuotes);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<QuotableItem>>(permitted.FirstError);

        return Result.Success(await queries.GetQuotableItemsAsync(currentUser.SiteId, clock.Today, ct));
    }

    public async Task<Result<IReadOnlyList<QuoteSupplierOption>>> GetSuppliersAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.ManageQuotes);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<QuoteSupplierOption>>(permitted.FirstError);

        return Result.Success(await queries.GetSuppliersAsync(ct));
    }

    public async Task<Result<Guid>> CreateAsync(CreateQuoteRequest request, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.ManageQuotes);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        if (request.Items.Count == 0) return Result.Failure<Guid>(Error.Validation("Quote_NoItems"));
        if (request.SupplierIds.Count == 0) return Result.Failure<Guid>(Error.Validation("Quote_NoSuppliers"));
        if (request.ResponseDueDate is { } due && due < clock.Today)
            return Result.Failure<Guid>(Error.Validation("Quote_DueDateInPast"));

        var onContract = await CaptiveRefusalAsync(request.Items.Select(i => i.StockItemId), ct);
        if (onContract is not null) return Result.Failure<Guid>(onContract);

        var codes = await queries.GetItemCodesAsync(request.Items.Select(i => i.StockItemId).ToList(), ct);

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var number = await sequences.NextAsync(SequencePurpose.QuoteRequest, null, clock.Today.Year, ct);
        if (number.IsFailure) return Result.Failure<Guid>(number.FirstError);

        var quote = new QuoteRequest(number.Value, currentUser.SiteId, clock.Today, currentUser.UserId)
        {
            Subject = Blank(request.Subject),
            ResponseDueDate = request.ResponseDueDate,
            Notes = Blank(request.Notes)
        };

        foreach (var item in request.Items)
        {
            if (!codes.TryGetValue(item.StockItemId, out var designation))
                return Result.Failure<Guid>(Error.NotFound("Stock_ItemNotFound"));

            var added = quote.AddItem(item.StockItemId, item.Quantity, designation);
            if (added.IsFailure) return Result.Failure<Guid>(added.FirstError);
        }

        foreach (var supplierId in request.SupplierIds)
        {
            var added = quote.AddSupplier(supplierId);
            if (added.IsFailure) return Result.Failure<Guid>(added.FirstError);
        }

        await requests.AddAsync(quote, ct);

        unitOfWork.AuditReason = $"Création de la demande de devis {quote.Number}";
        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return Result.Success(quote.Id);
    }

    public Task<Result> AddSupplierAsync(Guid requestId, Guid supplierId, CancellationToken ct = default) =>
        ApplyAsync(requestId, q => q.AddSupplier(supplierId), "Fournisseur ajouté à", ct);

    public Task<Result> MarkSentAsync(Guid requestId, Guid supplierId, CancellationToken ct = default) =>
        ApplyAsync(requestId, q =>
        {
            q.MarkSentTo(supplierId, clock.Now);
            return Result.Success();
        }, "Envoi manuel de", ct);

    public async Task<Result> CancelAsync(Guid requestId, string reason, CancellationToken ct = default) =>
        await ApplyAsync(requestId, q => q.Cancel(reason), $"Annulation ({reason?.Trim()}) de", ct);

    /// <summary>
    /// E-mails the request to every supplier asked that has an address and has not been sent it yet.
    /// The others are named back, to be contacted by telephone or fax and marked sent by hand.
    /// </summary>
    public async Task<Result<QuoteSendOutcome>> SendByEmailAsync(Guid requestId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.ManageQuotes);
        if (permitted.IsFailure) return Result.Failure<QuoteSendOutcome>(permitted.FirstError);

        if (!messages.CanSendEmail)
            return Result.Failure<QuoteSendOutcome>(Error.Validation("Purchasing_EmailNotConfigured"));

        var detail = await queries.GetDetailAsync(requestId, clock.Today, ct);
        if (detail is null) return Result.Failure<QuoteSendOutcome>(Error.NotFound("Quote_NotFound"));
        if (detail.Status != QuoteRequestStatus.Open)
            return Result.Failure<QuoteSendOutcome>(Error.Conflict("Quote_NotOpen"));

        var laboratory = await queries.GetLaboratoryNameAsync(currentUser.SiteId, ct);

        var sentTo = new List<Guid>();
        var withoutEmail = new List<string>();
        var failed = new List<string>();

        foreach (var supplier in detail.Suppliers.Where(s => s.SentAt is null))
        {
            if (string.IsNullOrWhiteSpace(supplier.Email))
            {
                withoutEmail.Add(supplier.Name);
                continue;
            }

            var sent = await messages.SendEmailAsync(new EmailMessage(
                supplier.Email.Trim(),
                $"Demande de devis {detail.Number} — {laboratory}",
                RequestBody(detail, laboratory),
                []), ct);

            if (sent.IsSuccess) sentTo.Add(supplier.SupplierId);
            else failed.Add(supplier.Name);
        }

        if (sentTo.Count > 0)
        {
            var quote = await requests.GetByIdWithDetailsAsync(requestId, ct);
            if (quote is null) return Result.Failure<QuoteSendOutcome>(Error.NotFound("Quote_NotFound"));

            foreach (var supplierId in sentTo) quote.MarkSentTo(supplierId, clock.Now);

            unitOfWork.AuditReason = $"Envoi par e-mail de la demande de devis {quote.Number}";
            await unitOfWork.SaveChangesAsync(ct);
        }

        return Result.Success(new QuoteSendOutcome(sentTo.Count, withoutEmail, failed));
    }

    public async Task<Result> RecordOfferAsync(Guid requestId, RecordQuoteOfferRequest request,
                                               CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.ManageQuotes);
        if (permitted.IsFailure) return permitted;

        if (request.ReceivedOn > clock.Today)
            return Result.Failure(Error.Validation("Quote_ReceivedInFuture"));

        if (request.FileContent is { Length: > PurchasingDocument.MaximumBytes })
            return Result.Failure(Error.Validation("Purchasing_FileTooLarge", PurchasingDocument.MaximumBytes / (1024 * 1024)));

        var attached = request.FileContent is { Length: > 0 } && !string.IsNullOrWhiteSpace(request.FileName);
        var fileName = attached ? Path.GetFileName(request.FileName!.Trim()) : string.Empty;
        var contentType = attached ? PurchasingDocument.ContentTypeFor(fileName) : null;
        if (attached && contentType is null)
            return Result.Failure(Error.Validation("Purchasing_FileType"));

        var quote = await requests.GetByIdWithDetailsAsync(requestId, ct);
        if (quote is null) return Result.Failure(Error.NotFound("Quote_NotFound"));

        var recorded = quote.RecordOffer(request.SupplierId, request.ReceivedOn, request.Lines,
                                         request.ValidUntil, request.DeliveryDelayDays,
                                         Blank(request.Conditions), Blank(request.SupplierReference));
        if (recorded.IsFailure) return Result.Failure(recorded.FirstError);

        // An answer is proof the request reached the supplier, however it was sent.
        quote.MarkSentTo(request.SupplierId, clock.Now);

        if (attached && contentType is not null)
        {
            await documents.AddAsync(new PurchasingDocument(PurchasingDocumentOwner.SupplierQuote, recorded.Value.Id,
                                                            fileName, contentType, request.FileContent!,
                                                            clock.Now, currentUser.UserId), ct);
        }

        unitOfWork.AuditReason = $"Réponse fournisseur enregistrée sur la demande de devis {quote.Number}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    public async Task<Result<(string FileName, byte[] Content)>> GetOfferFileAsync(Guid supplierQuoteId,
                                                                                  CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.ManageQuotes);
        if (permitted.IsFailure) return Result.Failure<(string, byte[])>(permitted.FirstError);

        var file = await queries.GetDocumentAsync(PurchasingDocumentOwner.SupplierQuote, supplierQuoteId, ct);
        if (file is not { } found) return Result.Failure<(string, byte[])>(Error.NotFound("Purchasing_FileNotFound"));

        // Opening runs the file through the shell: a kind attached before the allow-list stays closed.
        return PurchasingDocument.ContentTypeFor(found.FileName) is null
            ? Result.Failure<(string, byte[])>(Error.Validation("Purchasing_FileType"))
            : Result.Success(found);
    }

    /// <summary>
    /// Chooses an offer and drafts the purchase order at its prices, in one transaction. The draft
    /// still goes through approval by someone other than its author, like any other order: choosing
    /// the cheapest offer is not the same decision as committing the money.
    /// </summary>
    public async Task<Result<string>> AwardAsync(Guid requestId, Guid supplierQuoteId, CancellationToken ct = default)
    {
        var permitted = guard.RequireAll(Permissions.Inventory.ManageQuotes, Permissions.Inventory.ManagePurchaseOrders);
        if (permitted.IsFailure) return Result.Failure<string>(permitted.FirstError);

        var quote = await requests.GetByIdWithDetailsAsync(requestId, ct);
        if (quote is null) return Result.Failure<string>(Error.NotFound("Quote_NotFound"));

        var offer = quote.Offers.FirstOrDefault(o => o.Id == supplierQuoteId);
        if (offer is null) return Result.Failure<string>(Error.NotFound("Quote_OfferNotFound"));

        if (!await orderQueries.IsSupplierApprovedAsync(offer.SupplierId, ct))
            return Result.Failure<string>(Error.Regulatory("Po_SupplierNotApproved"));

        // A contract signed since the request was sent takes the article out of quotation.
        var onContract = await CaptiveRefusalAsync(quote.Items.Select(i => i.StockItemId), ct);
        if (onContract is not null) return Result.Failure<string>(onContract);

        var priced = quote.Items.OrderBy(i => i.LineNumber)
            .Select(i => (Item: i, Price: offer.Lines.FirstOrDefault(l => l.QuoteRequestItemId == i.Id)))
            .Where(x => x.Price is not null)
            .ToList();
        if (priced.Count == 0) return Result.Failure<string>(Error.Validation("Quote_OfferHasNoPrice"));

        // An award whose order was cancelled can be made again; any other awarded request is closed.
        var orderCancelled = quote is { Status: QuoteRequestStatus.Awarded, PurchaseOrderId: { } previousOrderId }
                             && await orders.GetByIdAsync(previousOrderId, ct) is { Status: PurchaseOrderStatus.Cancelled };

        var awarded = quote.Award(supplierQuoteId, clock.Today, currentUser.UserId, orderCancelled);
        if (awarded.IsFailure) return Result.Failure<string>(awarded.FirstError);

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var number = await sequences.NextAsync(SequencePurpose.PurchaseOrder, null, clock.Today.Year, ct);
        if (number.IsFailure) return Result.Failure<string>(number.FirstError);

        var order = new PurchaseOrder(number.Value, offer.SupplierId, currentUser.SiteId, clock.Today,
                                      currentUser.UserId)
        {
            ExpectedDeliveryDate = offer.DeliveryDelayDays is { } days ? clock.Today.AddDays(days) : null,
            SupplierReference = offer.SupplierReference ?? quote.Number,
            Notes = $"Offre retenue sur la demande de devis {quote.Number}"
        };

        // Articles the chosen supplier did not price stay out of the order; the grid showed them.
        foreach (var (item, price) in priced)
            order.AddLine(new PurchaseOrderLine(item.StockItemId, item.Quantity, price!.UnitPriceMad)
            {
                Description = item.Description,
                DiscountRate = price.DiscountRate,
                VatRate = price.VatRate
            });

        await orders.AddAsync(order, ct);
        quote.LinkPurchaseOrder(order.Id);

        unitOfWork.AuditReason = $"Offre retenue sur la demande de devis {quote.Number} : bon de commande {order.Number}";
        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return Result.Success(order.Number);
    }

    private async Task<Error?> CaptiveRefusalAsync(IEnumerable<Guid> stockItemIds, CancellationToken ct)
    {
        var ids = stockItemIds.Distinct().ToList();
        var captive = await queries.GetCaptiveReagentsAsync(currentUser.SiteId, clock.Today, ids, ct);
        if (captive.Count == 0) return null;

        var codes = await queries.GetItemCodesAsync(ids, ct);
        var first = captive[0];
        return Error.Conflict("Quote_ItemOnContract",
                              codes.TryGetValue(first.StockItemId, out var code) ? code : "?",
                              first.ContractReference);
    }

    private async Task<Result> ApplyAsync(Guid requestId, Func<QuoteRequest, Result> step, string label,
                                          CancellationToken ct)
    {
        var permitted = guard.Require(Permissions.Inventory.ManageQuotes);
        if (permitted.IsFailure) return permitted;

        var quote = await requests.GetByIdWithDetailsAsync(requestId, ct);
        if (quote is null) return Result.Failure(Error.NotFound("Quote_NotFound"));

        var done = step(quote);
        if (done.IsFailure) return done;

        unitOfWork.AuditReason = $"{label} la demande de devis {quote.Number}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>The request as e-mailed, in French: it is read by the supplier.</summary>
    internal static string RequestBody(QuoteRequestDetail detail, string laboratory)
    {
        var lines = new List<string>
        {
            "Madame, Monsieur,",
            string.Empty,
            $"Nous vous remercions de bien vouloir nous adresser votre meilleure offre pour les articles suivants (demande N° {detail.Number}) :",
            string.Empty
        };

        lines.AddRange(detail.Items.Select(i =>
            $"  {i.LineNumber}. {i.Designation} — quantité : {i.Quantity:0.##} {i.Unit}"));

        lines.Add(string.Empty);
        lines.Add("Merci de préciser pour chaque article le prix unitaire HT, le taux de TVA, la remise éventuelle, " +
                  "le délai de livraison, la durée de validité de l'offre et vos conditions de paiement.");

        if (detail.ResponseDueDate is { } due)
            lines.Add($"Réponse souhaitée avant le {due:dd/MM/yyyy}.");

        lines.Add(string.Empty);
        lines.Add("Cordialement,");
        lines.Add(laboratory);

        return string.Join(Environment.NewLine, lines);
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Read-side queries for the quotation screen.</summary>
public interface IQuoteQueries
{
    Task<IReadOnlyList<QuoteRequestRow>> GetRequestsAsync(Guid siteId, bool onlyOpen, CancellationToken ct = default);

    Task<QuoteRequestDetail?> GetDetailAsync(Guid requestId, DateOnly today, CancellationToken ct = default);

    Task<IReadOnlyList<QuotableItem>> GetQuotableItemsAsync(Guid siteId, DateOnly today, CancellationToken ct = default);

    /// <summary>Active suppliers, approved or not: a quotation is how a new supplier is tried out.</summary>
    Task<IReadOnlyList<QuoteSupplierOption>> GetSuppliersAsync(CancellationToken ct = default);

    /// <summary>"CODE — Désignation" per stock item, as a request line describes it.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetItemCodesAsync(IReadOnlyList<Guid> stockItemIds,
                                                             CancellationToken ct = default);

    /// <summary>
    /// The articles among <paramref name="stockItemIds"/> (all of them when null) that are captive
    /// reagents of a contract in force today.
    /// </summary>
    Task<IReadOnlyList<CaptiveReagent>> GetCaptiveReagentsAsync(Guid siteId, DateOnly today,
                                                               IReadOnlyList<Guid>? stockItemIds,
                                                               CancellationToken ct = default);

    Task<string> GetLaboratoryNameAsync(Guid siteId, CancellationToken ct = default);

    /// <summary>The latest file attached to a purchasing record.</summary>
    Task<(string FileName, byte[] Content)?> GetDocumentAsync(PurchasingDocumentOwner ownerKind, Guid ownerId,
                                                              CancellationToken ct = default);
}
