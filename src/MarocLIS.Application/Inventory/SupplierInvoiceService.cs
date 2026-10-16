using System.Globalization;
using System.Text;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Organization;

namespace MarocLIS.Application.Inventory;

// ---- Read models -------------------------------------------------------------------------------

/// <summary>One supplier invoice in the register.</summary>
/// <param name="DaysUntilDue">Negative once the due date has passed.</param>
public sealed record SupplierInvoiceRow(
    Guid Id,
    Guid SupplierId,
    string SupplierName,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    DateOnly ReceivedOn,
    DateOnly? DeliveryDate,
    DateOnly DueDate,
    int DaysUntilDue,
    SupplierInvoiceStatus Status,
    decimal TotalExcludingVat,
    decimal TotalIncludingVat,
    decimal AmountPaid,
    int VarianceCount,
    bool HasScan,
    string EnteredByName,
    Guid EnteredByUserId = default)
{
    public decimal AmountOutstanding => Math.Max(0m, TotalIncludingVat - AmountPaid);

    public bool IsOpen => Status is SupplierInvoiceStatus.Pending or SupplierInvoiceStatus.Blocked
                                  or SupplierInvoiceStatus.ApprovedForPayment
                                  or SupplierInvoiceStatus.PartiallyPaid;

    public bool IsOverdue => IsOpen && AmountOutstanding > 0m && DaysUntilDue < 0;
}

public sealed record SupplierInvoiceLineRow(
    int LineNumber,
    string OrderNumber,
    string ItemCode,
    string Description,
    decimal Quantity,
    decimal UnitPriceMad,
    decimal VatRate,
    decimal TotalExcludingVat);

public sealed record SupplierInvoiceVarianceRow(int LineNumber, VarianceKind Kind, decimal Expected, decimal Actual);

public sealed record SupplierPaymentRow(
    DateOnly PaidOn,
    decimal AmountMad,
    SupplierPaymentMethod Method,
    string? Reference,
    string RecordedByName);

public sealed record SupplierInvoiceDetail(
    SupplierInvoiceRow Header,
    IReadOnlyList<SupplierInvoiceLineRow> Lines,
    IReadOnlyList<SupplierInvoiceVarianceRow> Variances,
    IReadOnlyList<SupplierPaymentRow> Payments,
    int PaymentTermsDays,
    string? UnblockedByName,
    DateTimeOffset? UnblockedAt,
    string? UnblockJustification,
    string? CancellationReason,
    string? Notes);

/// <summary>An order line that can still be invoiced, with what the entry dialog pre-fills.</summary>
/// <param name="QuantityInvoiced">Already on the supplier's other invoices, cancelled ones excepted.</param>
public sealed record InvoiceableOrderLine(
    Guid PurchaseOrderLineId,
    string OrderNumber,
    DateOnly OrderDate,
    string ItemCode,
    string Description,
    string UnitOfMeasure,
    decimal QuantityOrdered,
    decimal QuantityReceived,
    decimal QuantityInvoiced,
    decimal NetUnitPriceMad,
    decimal VatRate)
{
    public decimal ReceivedNotInvoiced => Math.Max(0m, QuantityReceived - QuantityInvoiced);
}

/// <summary>What the match needs of one order line, and where it belongs.</summary>
public sealed record OrderLineMatchSource(
    Guid PurchaseOrderLineId,
    Guid SupplierId,
    Guid StockItemId,
    string Description,
    decimal QuantityReceived,
    decimal QuantityInvoicedElsewhere,
    decimal NetUnitPriceMad,
    decimal VatRate,
    DateOnly? LastReceivedOn,
    IReadOnlyList<DatedQuantity>? Receipts = null,
    IReadOnlyList<BilledQuantity>? OtherInvoices = null)
{
    /// <summary>
    /// The delivery the invoice's quantity on this line bills (loi 69-21): the receipts are walked
    /// in order past what the invoices before this one already billed — those dated earlier, and
    /// those of the same date entered before it. Without dated receipts, the line's latest delivery.
    /// </summary>
    /// <param name="enteredAt">When this invoice was entered; null for one being entered now, after all the others.</param>
    public DateOnly? DeliveryFor(DateOnly invoiceDate, decimal quantity, DateTimeOffset? enteredAt = null)
    {
        if (Receipts is not { Count: > 0 }) return LastReceivedOn;

        var before = (OtherInvoices ?? [])
                     .Where(i => i.On < invoiceDate
                                 || i.On == invoiceDate && (enteredAt is null || i.EnteredAt <= enteredAt))
                     .Sum(i => i.Quantity);
        return PaymentTerms.DeliveryCovering(Receipts, before, quantity);
    }

    public OrderLineFacts Facts => new(PurchaseOrderLineId, QuantityReceived, QuantityInvoicedElsewhere,
                                       NetUnitPriceMad, VatRate);
}

public sealed record PaymentFact(DateOnly PaidOn, decimal Amount);

/// <summary>An open or late-paid invoice, for the late-payment declaration.</summary>
/// <param name="DeclaredThrough">
/// The end of the last earlier declaration that listed the invoice; null when none did, and its
/// whole delay is owed in this one.
/// </param>
public sealed record LatePaymentSource(
    string SupplierName,
    string? SupplierIce,
    string? SupplierFiscalId,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    DateOnly? DeliveryDate,
    DateOnly DueDate,
    decimal TotalIncludingVat,
    IReadOnlyList<PaymentFact> Payments,
    DateOnly? DeclaredThrough = null);

/// <summary>One late amount of the declaration, with the penalty and the fine it carries.</summary>
/// <param name="PaidOn">Null for an amount still unpaid at the end of the period.</param>
public sealed record LatePaymentRow(
    string SupplierName,
    string? SupplierIce,
    string? SupplierFiscalId,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    DateOnly? DeliveryDate,
    DateOnly DueDate,
    decimal InvoiceTotal,
    decimal LateAmount,
    DateOnly? PaidOn,
    int DaysLate,
    decimal Penalty,
    decimal Fine,
    int DaysDeclaredBefore = 0,
    decimal FineDeclaredBefore = 0m);

/// <summary>The due-date dashboard of the invoice screen.</summary>
public sealed record SupplierDueSummary(
    int Overdue,
    decimal OverdueAmount,
    int DueWithin7Days,
    decimal DueWithin7DaysAmount,
    int DueWithin15Days,
    decimal DueWithin15DaysAmount,
    int Blocked);

public sealed record SupplierInvoiceScan(string FileName, string ContentType, byte[] Content);

public sealed record SupplierInvoiceOutcome(Guid InvoiceId, SupplierInvoiceStatus Status, int VarianceCount,
                                            DateOnly DueDate);

public enum SupplierInvoiceFilter
{
    Open = 0,
    Overdue = 1,
    DueWithin7Days = 2,
    DueWithin15Days = 3,
    Blocked = 4,
    All = 5
}

// ---- Requests ----------------------------------------------------------------------------------

public sealed record SupplierInvoiceLineRequest(Guid PurchaseOrderLineId, decimal Quantity,
                                                decimal UnitPriceMad, decimal VatRate);

public sealed record CreateSupplierInvoiceRequest
{
    public required Guid SupplierId { get; init; }
    public required string InvoiceNumber { get; init; }
    public required DateOnly InvoiceDate { get; init; }

    /// <summary>The day the invoice reached the laboratory.</summary>
    public required DateOnly ReceivedOn { get; init; }

    public string? Notes { get; init; }
    public required IReadOnlyList<SupplierInvoiceLineRequest> Lines { get; init; }
}

/// <param name="MaturityDate">
/// Due date of a bill of exchange (LCN) or a post-dated cheque, when the bank debits it later than
/// <paramref name="PaidOn"/>; the bank reconciliation looks for it on that day.
/// </param>
public sealed record RecordSupplierPaymentRequest(DateOnly PaidOn, decimal AmountMad,
                                                  SupplierPaymentMethod Method, string? Reference,
                                                  DateOnly? MaturityDate = null);

/// <summary>
/// Supplier invoices and their payment (factures fournisseurs).
/// <para>
/// Entering an invoice runs the three-way match straight away: an invoice whose lines all agree with
/// the order and the receipts is approved for payment; any variance blocks it, and only a user with
/// <see cref="Permissions.Inventory.ApprovePayments"/>, other than the one who keyed it, can release
/// it, with a justification kept in the audit trail. The due date follows loi n° 69-21 and the
/// late-payment list carries what the law makes the laboratory declare.
/// </para>
/// </summary>
public sealed class SupplierInvoiceService(
    IRepository<SupplierInvoice> invoices,
    IRepository<SupplierInvoiceFile> files,
    IRepository<Supplier> suppliers,
    IRepository<LatePaymentDeclaration> declarations,
    ISupplierInvoiceQueries queries,
    IConfigurationWriter settingsWriter,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock,
    IRepository<SupplierInvoiceExport>? invoiceExports = null)
{
    private static readonly string[] ReadRights =
        [Permissions.Inventory.EnterInvoices, Permissions.Inventory.ApprovePayments];

    // ---- Reading ---------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<SupplierInvoiceRow>>> GetInvoicesAsync(
        SupplierInvoiceFilter filter = SupplierInvoiceFilter.Open, CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(ReadRights);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<SupplierInvoiceRow>>(permitted.FirstError);

        var rows = await queries.GetInvoicesAsync(currentUser.SiteId, filter == SupplierInvoiceFilter.All,
                                                  clock.Today, ct);

        IReadOnlyList<SupplierInvoiceRow> filtered = filter switch
        {
            SupplierInvoiceFilter.Open => [.. rows.Where(r => r.IsOpen)],
            SupplierInvoiceFilter.Overdue => [.. rows.Where(r => r.IsOverdue)],
            SupplierInvoiceFilter.DueWithin7Days => [.. rows.Where(r => IsDueWithin(r, 7))],
            SupplierInvoiceFilter.DueWithin15Days => [.. rows.Where(r => IsDueWithin(r, 15))],
            SupplierInvoiceFilter.Blocked => [.. rows.Where(r => r.Status == SupplierInvoiceStatus.Blocked)],
            _ => rows
        };

        return Result.Success(filtered);
    }

    private static bool IsDueWithin(SupplierInvoiceRow row, int days) =>
        row.IsOpen && row.AmountOutstanding > 0m && row.DaysUntilDue >= 0 && row.DaysUntilDue <= days;

    /// <summary>Counts and amounts falling due: overdue, within 7 days, within 15 days, and blocked.</summary>
    public static SupplierDueSummary Summarize(IEnumerable<SupplierInvoiceRow> rows)
    {
        var open = rows.Where(r => r.IsOpen).ToList();
        var overdue = open.Where(r => r.IsOverdue).ToList();
        var within7 = open.Where(r => IsDueWithin(r, 7)).ToList();
        var within15 = open.Where(r => IsDueWithin(r, 15)).ToList();

        return new SupplierDueSummary(
            overdue.Count, overdue.Sum(r => r.AmountOutstanding),
            within7.Count, within7.Sum(r => r.AmountOutstanding),
            within15.Count, within15.Sum(r => r.AmountOutstanding),
            open.Count(r => r.Status == SupplierInvoiceStatus.Blocked));
    }

    public async Task<Result<SupplierDueSummary>> GetDueSummaryAsync(CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(ReadRights);
        if (permitted.IsFailure) return Result.Failure<SupplierDueSummary>(permitted.FirstError);

        return Result.Success(Summarize(await queries.GetInvoicesAsync(currentUser.SiteId, false, clock.Today, ct)));
    }

    public async Task<Result<SupplierInvoiceDetail>> GetDetailAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(ReadRights);
        if (permitted.IsFailure) return Result.Failure<SupplierInvoiceDetail>(permitted.FirstError);

        var detail = await queries.GetDetailAsync(invoiceId, clock.Today, ct);
        return detail is null
            ? Result.Failure<SupplierInvoiceDetail>(Error.NotFound("SupplierInvoice_NotFound"))
            : Result.Success(detail);
    }

    public async Task<Result<IReadOnlyList<SupplierOption>>> GetSuppliersAsync(CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(ReadRights);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<SupplierOption>>(permitted.FirstError);

        return Result.Success(await queries.GetInvoicingSuppliersAsync(currentUser.SiteId, ct));
    }

    public async Task<Result<IReadOnlyList<InvoiceableOrderLine>>> GetInvoiceableLinesAsync(
        Guid supplierId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.EnterInvoices);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<InvoiceableOrderLine>>(permitted.FirstError);

        return Result.Success(await queries.GetInvoiceableLinesAsync(currentUser.SiteId, supplierId, ct));
    }

    // ---- Entry and match ---------------------------------------------------------------------------

    public async Task<Result<SupplierInvoiceOutcome>> CreateAsync(CreateSupplierInvoiceRequest request,
                                                                  CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.EnterInvoices);
        if (permitted.IsFailure) return Result.Failure<SupplierInvoiceOutcome>(permitted.FirstError);

        var errors = new List<Error>();
        if (string.IsNullOrWhiteSpace(request.InvoiceNumber))
            errors.Add(Error.Validation("SupplierInvoice_NumberRequired"));
        if (request.InvoiceDate > clock.Today)
            errors.Add(Error.Validation("SupplierInvoice_DateInFuture"));
        if (request.ReceivedOn > clock.Today || request.ReceivedOn < request.InvoiceDate)
            errors.Add(Error.Validation("SupplierInvoice_ReceivedOnInvalid"));
        if (request.Lines.Count == 0)
            errors.Add(Error.Validation("SupplierInvoice_NoLines"));
        if (errors.Count > 0) return Result.Failure<SupplierInvoiceOutcome>(errors);

        var supplier = await suppliers.GetByIdAsync(request.SupplierId, ct);
        if (supplier is null) return Result.Failure<SupplierInvoiceOutcome>(Error.NotFound("Supplier_NotFound"));

        var number = request.InvoiceNumber.Trim();
        if (await queries.InvoiceNumberExistsAsync(supplier.Id, number, ct))
            return Result.Failure<SupplierInvoiceOutcome>(
                Error.Conflict("SupplierInvoice_NumberAlreadyEntered", number, supplier.Name));

        var sources = await queries.GetOrderLineSourcesAsync(
            [.. request.Lines.Select(l => l.PurchaseOrderLineId)], null, ct);
        var byLine = sources.ToDictionary(s => s.PurchaseOrderLineId);

        var invoice = new SupplierInvoice(supplier.Id, currentUser.SiteId, number, request.InvoiceDate,
                                          request.ReceivedOn, currentUser.UserId)
        {
            Notes = Blank(request.Notes)
        };

        foreach (var line in request.Lines)
        {
            // Only this supplier's own order lines, on orders this site can see.
            if (!byLine.TryGetValue(line.PurchaseOrderLineId, out var source) || source.SupplierId != supplier.Id)
                return Result.Failure<SupplierInvoiceOutcome>(Error.NotFound("SupplierInvoice_OrderLineNotFound"));

            var added = invoice.AddLine(new SupplierInvoiceLine(source.PurchaseOrderLineId, source.StockItemId,
                                                                source.Description, line.Quantity,
                                                                line.UnitPriceMad, line.VatRate));
            if (added.IsFailure) return Result.Failure<SupplierInvoiceOutcome>(added.Errors);
        }

        var settings = await GetSettingsCoreAsync(ct);
        var matched = Match(invoice, sources, settings, supplier.PaymentTermsDays, enteredAt: null);
        if (matched.IsFailure) return Result.Failure<SupplierInvoiceOutcome>(matched.Errors);

        await invoices.AddAsync(invoice, ct);
        await MoveLaterTermsAsync(invoice, live: true, ct);

        unitOfWork.AuditReason = invoice.Status == SupplierInvoiceStatus.Blocked
            ? $"Saisie de la facture fournisseur {number} ({supplier.Code}) : bloquée, {invoice.Variances.Count} écart(s)"
            : $"Saisie de la facture fournisseur {number} ({supplier.Code}) : rapprochée, bonne à payer";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new SupplierInvoiceOutcome(invoice.Id, invoice.Status, invoice.Variances.Count,
                                                         invoice.DueDate));
    }

    /// <summary>
    /// Runs the match again on a blocked invoice, typically after the missing goods were received.
    /// The due date moves with the new delivery date.
    /// </summary>
    public async Task<Result<SupplierInvoiceOutcome>> RematchAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(ReadRights);
        if (permitted.IsFailure) return Result.Failure<SupplierInvoiceOutcome>(permitted.FirstError);

        var invoice = await invoices.GetByIdWithDetailsAsync(invoiceId, ct);
        if (invoice is null) return Result.Failure<SupplierInvoiceOutcome>(Error.NotFound("SupplierInvoice_NotFound"));

        var supplier = await suppliers.GetByIdAsync(invoice.SupplierId, ct);
        var sources = await queries.GetOrderLineSourcesAsync(
            [.. invoice.Lines.Select(l => l.PurchaseOrderLineId)], invoice.Id, ct);

        var settings = await GetSettingsCoreAsync(ct);
        var matched = Match(invoice, sources, settings, supplier?.PaymentTermsDays ?? PaymentTerms.DefaultDays,
                            invoice.CreatedAt);
        if (matched.IsFailure) return Result.Failure<SupplierInvoiceOutcome>(matched.Errors);

        unitOfWork.AuditReason = $"Nouveau rapprochement de la facture fournisseur {invoice.InvoiceNumber} : " +
                                 (invoice.Status == SupplierInvoiceStatus.Blocked
                                     ? $"toujours bloquée, {invoice.Variances.Count} écart(s)"
                                     : "rapprochée, bonne à payer");
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new SupplierInvoiceOutcome(invoice.Id, invoice.Status, invoice.Variances.Count,
                                                         invoice.DueDate));
    }

    private static Result Match(SupplierInvoice invoice, IReadOnlyList<OrderLineMatchSource> sources,
                                PurchasingSettings settings, int termsDays, DateTimeOffset? enteredAt)
    {
        var facts = sources.ToDictionary(s => s.PurchaseOrderLineId, s => s.Facts);
        var applied = invoice.ApplyMatch(ThreeWayMatch.Compare(invoice.Lines, facts, settings.Tolerance));
        if (applied.IsFailure) return applied;

        invoice.SetPaymentTerms(DeliveryOf(invoice, sources, enteredAt), termsDays);
        return Result.Success();
    }

    /// <summary>
    /// Loi 69-21: the term runs from the delivery of the goods billed; the latest of them when the
    /// invoice bills several deliveries.
    /// </summary>
    private static DateOnly? DeliveryOf(SupplierInvoice invoice, IReadOnlyList<OrderLineMatchSource> sources,
                                        DateTimeOffset? enteredAt)
    {
        var bySource = sources.ToDictionary(s => s.PurchaseOrderLineId);
        var delivered = invoice.Lines
                               .Select(l => bySource.TryGetValue(l.PurchaseOrderLineId, out var s)
                                                ? s.DeliveryFor(invoice.InvoiceDate, l.Quantity, enteredAt)
                                                : null)
                               .Where(d => d is not null).Select(d => d!.Value)
                               .DefaultIfEmpty().Max();
        return delivered == default ? null : delivered;
    }

    /// <summary>
    /// Deliveries are billed in invoice order: an invoice entered (or cancelled) on the same order
    /// lines moves the deliveries the invoices of the same date or later bill, and so their due
    /// dates. Those are worked out again, without touching their match, approval or payments; a
    /// paid invoice too, whose due date is what the late-payment declaration measures it against.
    /// </summary>
    /// <param name="live">Whether <paramref name="changed"/> now bills its lines (entered) or not (cancelled).</param>
    private async Task MoveLaterTermsAsync(SupplierInvoice changed, bool live, CancellationToken ct)
    {
        var lineIds = changed.Lines.Select(l => l.PurchaseOrderLineId).Distinct().ToList();
        var laterIds = await queries.GetOpenInvoicesBillingAsync(lineIds, changed.InvoiceDate, changed.Id, ct);

        foreach (var id in laterIds)
        {
            var later = await invoices.GetByIdWithDetailsAsync(id, ct);
            if (later is null || later.Status == SupplierInvoiceStatus.Cancelled) continue;

            // The changed invoice as it now stands, whatever the database says before the save.
            var sources = (await queries.GetOrderLineSourcesAsync(
                               [.. later.Lines.Select(l => l.PurchaseOrderLineId)], later.Id, ct))
                .Select(s => s with
                {
                    OtherInvoices =
                    [
                        .. (s.OtherInvoices ?? []).Where(o => o.InvoiceId != changed.Id),
                        .. live
                            ? changed.Lines.Where(l => l.PurchaseOrderLineId == s.PurchaseOrderLineId)
                                     .Select(l => new BilledQuantity(changed.InvoiceDate, l.Quantity, clock.Now, changed.Id))
                            : []
                    ]
                })
                .ToList();

            var delivery = DeliveryOf(later, sources, later.CreatedAt);
            if (delivery != later.DeliveryDate) later.SetPaymentTerms(delivery, later.PaymentTermsDays);
        }
    }

    // ---- Release, payment, cancellation --------------------------------------------------------------

    public async Task<Result> UnblockAsync(Guid invoiceId, string justification, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.ApprovePayments);
        if (permitted.IsFailure) return permitted;

        var invoice = await invoices.GetByIdWithDetailsAsync(invoiceId, ct);
        if (invoice is null) return Result.Failure(Error.NotFound("SupplierInvoice_NotFound"));

        var done = invoice.Unblock(clock.Now, currentUser.UserId, justification ?? string.Empty);
        if (done.IsFailure) return done;

        unitOfWork.AuditReason =
            $"Déblocage de la facture fournisseur {invoice.InvoiceNumber} malgré {invoice.Variances.Count} écart(s) : {justification!.Trim()}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> RecordPaymentAsync(Guid invoiceId, RecordSupplierPaymentRequest request,
                                                 CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.ApprovePayments);
        if (permitted.IsFailure) return permitted;

        if (request.PaidOn > clock.Today)
            return Result.Failure(Error.Validation("SupplierPayment_DateInFuture"));

        var invoice = await invoices.GetByIdWithDetailsAsync(invoiceId, ct);
        if (invoice is null) return Result.Failure(Error.NotFound("SupplierInvoice_NotFound"));

        var done = invoice.RecordPayment(new SupplierPayment(request.PaidOn, request.AmountMad, request.Method,
                                                             request.Reference, clock.Now, currentUser.UserId,
                                                             request.MaturityDate));
        if (done.IsFailure) return done;

        unitOfWork.AuditReason =
            $"Règlement de {request.AmountMad.ToString("N2", CultureInfo.GetCultureInfo("fr-FR"))} MAD " +
            $"({request.Method}) sur la facture fournisseur {invoice.InvoiceNumber}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> CancelAsync(Guid invoiceId, string reason, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.EnterInvoices);
        if (permitted.IsFailure) return permitted;

        var invoice = await invoices.GetByIdWithDetailsAsync(invoiceId, ct);
        if (invoice is null) return Result.Failure(Error.NotFound("SupplierInvoice_NotFound"));

        var done = invoice.Cancel(clock.Now, reason ?? string.Empty);
        if (done.IsFailure) return done;

        await MoveLaterTermsAsync(invoice, live: false, ct);

        unitOfWork.AuditReason = $"Annulation de la facture fournisseur {invoice.InvoiceNumber} : {reason!.Trim()}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ---- Scan ----------------------------------------------------------------------------------------

    public async Task<Result> AttachScanAsync(Guid invoiceId, string fileName, byte[] content,
                                              CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.EnterInvoices);
        if (permitted.IsFailure) return permitted;

        if (content.Length == 0 || content.Length > SupplierInvoiceFile.MaximumBytes)
            return Result.Failure(Error.Validation("SupplierInvoice_ScanTooLarge",
                                                   SupplierInvoiceFile.MaximumBytes / (1024 * 1024)));

        var name = Path.GetFileName(fileName);
        if (CredentialFile.ContentTypeOf(name) is not { } contentType)
            return Result.Failure(Error.Validation("SupplierInvoice_ScanType"));
        // The name columns hold 200 characters and Windows allows 255: shortened, extension kept,
        // as for the lot certificates, rather than failing the save with « Erreur inattendue ».
        if (name.Length > 200) name = name[..(200 - Path.GetExtension(name).Length)] + Path.GetExtension(name);

        var invoice = await invoices.GetByIdAsync(invoiceId, ct);
        if (invoice is null) return Result.Failure(Error.NotFound("SupplierInvoice_NotFound"));

        var existing = (await files.ListAsync(f => f.SupplierInvoiceId == invoiceId, ct)).FirstOrDefault();
        if (existing is null)
            await files.AddAsync(new SupplierInvoiceFile(invoiceId, name, contentType, content), ct);
        else
            existing.Replace(name, contentType, content);

        invoice.ScanFileName = name;

        unitOfWork.AuditReason = $"Scan joint à la facture fournisseur {invoice.InvoiceNumber} : {name}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<SupplierInvoiceScan>> GetScanAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(ReadRights);
        if (permitted.IsFailure) return Result.Failure<SupplierInvoiceScan>(permitted.FirstError);

        var file = (await files.ListAsync(f => f.SupplierInvoiceId == invoiceId, ct)).FirstOrDefault();
        return file is null
            ? Result.Failure<SupplierInvoiceScan>(Error.NotFound("SupplierInvoice_NoScan"))
            : Result.Success(new SupplierInvoiceScan(file.FileName, file.ContentType, file.Content));
    }

    // ---- Settings ------------------------------------------------------------------------------------

    private async Task<PurchasingSettings> GetSettingsCoreAsync(CancellationToken ct) =>
        PurchasingSettings.FromValues(await queries.GetSettingValuesAsync(PurchasingSettings.Prefix, ct));

    public async Task<Result<PurchasingSettings>> GetSettingsAsync(CancellationToken ct = default)
    {
        var permitted = guard.RequireAny([.. ReadRights, Permissions.Administration.ManageSettings,
                                          Permissions.Billing.ExportAccounting]);
        if (permitted.IsFailure) return Result.Failure<PurchasingSettings>(permitted.FirstError);

        return Result.Success(await GetSettingsCoreAsync(ct));
    }

    public async Task<Result> SaveSettingsAsync(PurchasingSettings settings, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageSettings);
        if (permitted.IsFailure) return permitted;

        if (settings.PriceTolerancePercent is < 0m or > 100m || settings.PriceToleranceMad < 0m
            || settings.KeyRatePercent is < 0m or > 100m || settings.PenaltyMarginPoints is < 0m or > 100m
            || settings.FineFirstMonthPercent is < 0m or > 100m
            || settings.FineAdditionalMonthPercent is < 0m or > 100m)
            return Result.Failure(Error.Validation("Purchasing_SettingOutOfRange"));

        var values = settings.ToValues();
        if (values.Values.Any(string.IsNullOrWhiteSpace) || values.Values.Any(v => v.Length > 20))
            return Result.Failure(Error.Validation("Purchasing_AccountInvalid"));

        var changed = new List<string>();
        foreach (var (key, value) in values)
        {
            var existing = await settingsWriter.GetSettingAsync(key, null, ct);
            if (existing is null)
            {
                await settingsWriter.AddSettingAsync(new AppSetting(key, value)
                {
                    Description = key.StartsWith("Purchasing.LatePenalty.KeyRate", StringComparison.Ordinal)
                        ? "Taux directeur de Bank Al-Maghrib, à mettre à jour à chaque décision de la banque"
                        : "Achats : rapprochement, loi 69-21 et export comptable"
                }, ct);
                changed.Add(key);
            }
            else if (existing.Value != value)
            {
                existing.Value = value;
                changed.Add(key);
            }
        }

        if (changed.Count == 0) return Result.Success();

        unitOfWork.AuditReason = $"Paramètres d'achat modifiés : {string.Join(", ", changed)}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ---- Late payments (loi 69-21) -------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<LatePaymentRow>>> GetLatePaymentsAsync(DateOnly from, DateOnly to,
                                                                                 CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(ReadRights);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<LatePaymentRow>>(permitted.FirstError);
        if (from > to) return Result.Failure<IReadOnlyList<LatePaymentRow>>(Error.Validation("Purchasing_PeriodReversed"));

        var settings = await GetSettingsCoreAsync(ct);
        var sources = await queries.GetLatePaymentSourcesAsync(currentUser.SiteId, from, to, ct);

        return Result.Success(ComputeLatePayments(sources, from, to, settings));
    }

    public static IReadOnlyList<LatePaymentRow> ComputeLatePayments(IEnumerable<LatePaymentSource> sources,
                                                                    DateOnly from, DateOnly to,
                                                                    PurchasingSettings settings) =>
    [
        .. sources
            .SelectMany(s => LatePayment.LateAmounts(s.DueDate, s.TotalIncludingVat,
                                                     s.Payments.Select(p => (p.PaidOn, p.Amount)), from, to,
                                                     s.DeclaredThrough)
                                        .Select(late => (Source: s, Late: late)))
            .OrderBy(x => x.Source.SupplierName, StringComparer.CurrentCulture)
            .ThenBy(x => x.Source.InvoiceNumber, StringComparer.Ordinal)
            .ThenBy(x => x.Late.PaidOn ?? DateOnly.MaxValue)
            .Select(x => new LatePaymentRow(
                x.Source.SupplierName, x.Source.SupplierIce, x.Source.SupplierFiscalId, x.Source.InvoiceNumber,
                x.Source.InvoiceDate, x.Source.DeliveryDate, x.Source.DueDate, x.Source.TotalIncludingVat,
                x.Late.Amount, x.Late.PaidOn, x.Late.DaysLate,
                LatePayment.PenaltyForPeriod(x.Late, settings.PenaltyRatePercent),
                LatePayment.FineForPeriod(x.Late, settings.FineFirstMonthPercent, settings.FineAdditionalMonthPercent),
                x.Late.DaysDeclaredBefore,
                LatePayment.FineDeclaredBefore(x.Late, settings.FineFirstMonthPercent,
                                               settings.FineAdditionalMonthPercent)))
    ];

    public async Task<Result<byte[]>> ExportLatePaymentsAsync(DateOnly from, DateOnly to,
                                                              CancellationToken ct = default)
    {
        var rows = await GetLatePaymentsAsync(from, to, ct);
        if (rows.IsFailure) return Result.Failure<byte[]>(rows.FirstError);

        // The export of an ended period is what gets filed: the period is recorded, with what had
        // been entered by now. A later period then deducts only what this one listed, and exporting
        // it again gives this file.
        var siteId = currentUser.SiteId;
        if (to < clock.Today
            && (await declarations.ListAsync(d => d.SiteId == siteId && d.PeriodFrom == from && d.PeriodTo == to, ct))
                .Count == 0)
        {
            await declarations.AddAsync(new LatePaymentDeclaration(siteId, from, to, clock.Now, currentUser.UserId), ct);
            unitOfWork.AuditReason = $"Déclaration des retards de paiement (loi 69-21) exportée : {from:dd/MM/yyyy} au {to:dd/MM/yyyy}";
            await unitOfWork.SaveChangesAsync(ct);
        }

        return Result.Success(LatePaymentsCsv(rows.Value));
    }

    public static byte[] LatePaymentsCsv(IReadOnlyList<LatePaymentRow> rows)
    {
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        string D(DateOnly? d) => d?.ToString("dd/MM/yyyy", fr) ?? string.Empty;
        string M(decimal v) => v.ToString("0.00", fr);
        string C(string? s) => SupplierAccountingExport.Cell(s ?? string.Empty);

        var text = new StringBuilder();
        text.AppendLine("Fournisseur;ICE;IF;N° facture;Date facture;Date de livraison;Date d'échéance;" +
                        "Montant TTC facture;Montant en retard;Date de paiement;Jours de retard;" +
                        "Pénalité due au fournisseur;Amende (déclaration);" +
                        "Jours de retard déjà déclarés;Amende déjà déclarée");

        foreach (var r in rows)
        {
            text.AppendLine(string.Join(';', C(r.SupplierName), C(r.SupplierIce), C(r.SupplierFiscalId),
                C(r.InvoiceNumber), D(r.InvoiceDate), D(r.DeliveryDate), D(r.DueDate), M(r.InvoiceTotal),
                M(r.LateAmount), r.PaidOn is null ? "Impayé" : D(r.PaidOn),
                r.DaysLate.ToString(CultureInfo.InvariantCulture), M(r.Penalty), M(r.Fine),
                r.DaysDeclaredBefore.ToString(CultureInfo.InvariantCulture), M(r.FineDeclaredBefore)));
        }

        text.AppendLine(string.Join(';', "TOTAL", string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, M(rows.Sum(r => r.LateAmount)), string.Empty,
            string.Empty, M(rows.Sum(r => r.Penalty)), M(rows.Sum(r => r.Fine)), string.Empty,
            M(rows.Sum(r => r.FineDeclaredBefore))));

        return SupplierAccountingExport.Encode(text.ToString());
    }

    // ---- Accounting export ---------------------------------------------------------------------------

    public async Task<Result<byte[]>> ExportAccountingAsync(DateOnly from, DateOnly to, AccountingExportLayout layout,
                                                            CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.ExportAccounting);
        if (permitted.IsFailure) return Result.Failure<byte[]>(permitted.FirstError);
        if (from > to) return Result.Failure<byte[]>(Error.Validation("Purchasing_PeriodReversed"));

        var settings = await GetSettingsCoreAsync(ct);
        var invoiceSources = await queries.GetAccountingInvoicesAsync(currentUser.SiteId, from, to, ct);
        var paymentSources = await queries.GetAccountingPaymentsAsync(currentUser.SiteId, from, to, ct);

        var reclassified = await queries.GetAccountingReclassificationsAsync(currentUser.SiteId, from, to, ct);

        IReadOnlyList<AccountingEntry> entries =
        [
            .. SupplierAccountingExport.BuildEntries(invoiceSources, paymentSources, settings),
            .. SupplierAccountingExport.Reclassifications(reclassified, settings, from, to)
        ];
        if (entries.Count == 0) return Result.Failure<byte[]>(Error.Validation("Purchasing_NothingToExport"));

        var file = SupplierAccountingExport.ToCsv(entries, layout);

        // The invoices now in the accounting software: an asset linked to one later is reclassified (I7).
        var exported = invoiceSources.Where(i => !i.Exported && i.Id != Guid.Empty).ToList();
        if (invoiceExports is not null && exported.Count > 0)
        {
            foreach (var invoice in exported)
                await invoiceExports.AddAsync(new SupplierInvoiceExport(invoice.Id, clock.Now), ct);
            unitOfWork.AuditReason = $"Factures fournisseurs exportées du {from:dd/MM/yyyy} au {to:dd/MM/yyyy}";
            await unitOfWork.SaveChangesAsync(ct);
        }

        return Result.Success(file);
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Read-side queries for supplier invoices.</summary>
public interface ISupplierInvoiceQueries
{
    /// <param name="includeClosed">False: open invoices only (not paid, not cancelled).</param>
    Task<IReadOnlyList<SupplierInvoiceRow>> GetInvoicesAsync(Guid siteId, bool includeClosed, DateOnly today,
                                                             CancellationToken ct = default);

    Task<SupplierInvoiceDetail?> GetDetailAsync(Guid invoiceId, DateOnly today, CancellationToken ct = default);

    /// <summary>Active suppliers with at least one order past the draft stage, at this site.</summary>
    Task<IReadOnlyList<SupplierOption>> GetInvoicingSuppliersAsync(Guid siteId, CancellationToken ct = default);

    /// <summary>The supplier's order lines, on approved and later orders, not yet fully invoiced.</summary>
    Task<IReadOnlyList<InvoiceableOrderLine>> GetInvoiceableLinesAsync(Guid siteId, Guid supplierId,
                                                                        CancellationToken ct = default);

    /// <param name="excludingInvoiceId">The invoice being matched again, whose own lines do not count as "elsewhere".</param>
    /// <summary>
    /// The open or paid (not cancelled) invoices, other than <paramref name="exceptInvoiceId"/>, that
    /// bill one of these order lines and are dated on or after <paramref name="onOrAfter"/>.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetOpenInvoicesBillingAsync(IReadOnlyCollection<Guid> purchaseOrderLineIds, DateOnly onOrAfter,
                                                          Guid exceptInvoiceId, CancellationToken ct = default);

    Task<IReadOnlyList<OrderLineMatchSource>> GetOrderLineSourcesAsync(IReadOnlyCollection<Guid> purchaseOrderLineIds,
                                                                        Guid? excludingInvoiceId,
                                                                        CancellationToken ct = default);

    /// <summary>Whether the supplier already has a live (not cancelled) invoice with this number.</summary>
    Task<bool> InvoiceNumberExistsAsync(Guid supplierId, string invoiceNumber, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, string?>> GetSettingValuesAsync(string prefix, CancellationToken ct = default);

    /// <summary>Invoices not cancelled, due before <paramref name="to"/>, not settled before <paramref name="from"/>.</summary>
    Task<IReadOnlyList<LatePaymentSource>> GetLatePaymentSourcesAsync(Guid siteId, DateOnly from, DateOnly to,
                                                                      CancellationToken ct = default);

    /// <summary>Invoices not cancelled, dated within the period.</summary>
    Task<IReadOnlyList<AccountingInvoiceSource>> GetAccountingInvoicesAsync(Guid siteId, DateOnly from, DateOnly to,
                                                                            CancellationToken ct = default);

    /// <summary>
    /// Invoices, whatever their date, with an asset linked after their purchases entry was exported
    /// on a day within the period: the reclassifications of <see cref="SupplierAccountingExport.Reclassifications"/>.
    /// </summary>
    Task<IReadOnlyList<AccountingInvoiceSource>> GetAccountingReclassificationsAsync(Guid siteId, DateOnly from,
                                                                                     DateOnly to,
                                                                                     CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AccountingInvoiceSource>>([]);

    /// <summary>Payments made within the period.</summary>
    Task<IReadOnlyList<AccountingPaymentSource>> GetAccountingPaymentsAsync(Guid siteId, DateOnly from, DateOnly to,
                                                                            CancellationToken ct = default);
}
