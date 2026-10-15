using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;

namespace MarocLIS.Application.Inventory;

/// <summary>
/// Everything printed on a purchase order (bon de commande), read in one go.
/// <para>
/// The laboratory's identity is the same <see cref="InvoiceIssuer"/> the invoices carry: a supplier
/// files a purchase order with the invoice it answers, and the ICE, IF and RC on both have to agree.
/// </para>
/// </summary>
public sealed record PurchaseOrderDocument(
    Guid PurchaseOrderId,
    string Number,
    PurchaseOrderStatus Status,
    DateOnly OrderDate,
    DateOnly? ExpectedDeliveryDate,
    InvoiceIssuer Laboratory,
    PurchaseOrderSupplierParty Supplier,
    string? DeliveryAddress,
    string? SupplierReference,
    string? Notes,
    string RaisedByName,
    string? ApprovedByName,
    DateTimeOffset? ApprovedAt,
    IReadOnlyList<PurchaseOrderDocumentLine> Lines)
{
    public decimal TotalExcludingVat => Money.Round(Lines.Sum(l => l.TotalExcludingVat));
    public decimal TotalVat => Money.Round(Lines.Sum(l => l.VatAmount));
    public decimal TotalIncludingVat => Money.Round(TotalExcludingVat + TotalVat);

    /// <summary>VAT per rate, as an invoice states it when the lines carry different rates.</summary>
    public IReadOnlyList<(decimal Rate, decimal Base, decimal Vat)> VatBreakdown =>
        [.. Lines.GroupBy(l => l.VatRate)
                 .OrderBy(g => g.Key)
                 .Select(g => (g.Key, Money.Round(g.Sum(l => l.TotalExcludingVat)),
                               Money.Round(g.Sum(l => l.VatAmount))))];

    /// <summary>A file name without the slashes of the order number.</summary>
    public string FileName => $"{string.Concat(Number.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'))}.pdf";
}

/// <summary>The supplier as the order is addressed to it.</summary>
public sealed record PurchaseOrderSupplierParty(
    string Code,
    string Name,
    string? Ice,
    string? FiscalId,
    string? CommercialRegisterNumber,
    string? Address,
    string? City,
    string? ContactName,
    string? Phone,
    string? Email);

/// <summary>One ordered article, amounts computed exactly as the order aggregate computes them.</summary>
public sealed record PurchaseOrderDocumentLine(
    int LineNumber,
    string Reference,
    string Designation,
    string Unit,
    decimal Quantity,
    decimal UnitPriceMad,
    decimal DiscountRate,
    decimal VatRate)
{
    public decimal TotalExcludingVat => Money.Round(Quantity * UnitPriceMad * (1m - DiscountRate));
    public decimal VatAmount => Money.Round(TotalExcludingVat * VatRate);
    public decimal TotalIncludingVat => Money.Round(TotalExcludingVat + VatAmount);
}

/// <summary>Turns a <see cref="PurchaseOrderDocument"/> into a PDF.</summary>
public interface IPurchaseOrderRenderer
{
    byte[] Render(PurchaseOrderDocument document, AppLanguage language);
}

public interface IPurchaseOrderDocumentQueries
{
    Task<PurchaseOrderDocument?> GetDocumentAsync(Guid purchaseOrderId, CancellationToken ct = default);
}

/// <summary>The rendered order, with what the screen needs to offer sending it.</summary>
public sealed record PurchaseOrderPdf(string FileName, byte[] Content, string SupplierName, string? SupplierEmail);

/// <summary>
/// The purchase order as a document: printed or saved as PDF, and e-mailed to the supplier.
/// <para>
/// Only an approved order is printed: a draft is not a commitment, and a supplier handed one could
/// deliver against prices nobody approved. E-mailing it is what marks the order as sent; when the
/// laboratory has no SMTP account configured the screen says so and the operator sends the saved
/// PDF themselves, then confirms — the order is never marked sent on an e-mail that did not leave.
/// </para>
/// </summary>
public sealed class PurchaseOrderDocumentService(
    IRepository<PurchaseOrder> orders,
    IPurchaseOrderDocumentQueries queries,
    IPurchaseOrderRenderer renderer,
    IMessageSender messages,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Whether an e-mail can be sent at all from this installation.</summary>
    public bool CanEmail => messages.CanSendEmail;

    public async Task<Result<PurchaseOrderPdf>> RenderAsync(Guid purchaseOrderId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.View);
        if (permitted.IsFailure) return Result.Failure<PurchaseOrderPdf>(permitted.FirstError);

        var document = await queries.GetDocumentAsync(purchaseOrderId, ct);
        if (document is null) return Result.Failure<PurchaseOrderPdf>(Error.NotFound("Po_NotFound"));

        if (!IsPrintable(document.Status))
            return Result.Failure<PurchaseOrderPdf>(Error.Conflict("Po_DocumentRequiresApproval"));

        return Result.Success(new PurchaseOrderPdf(document.FileName,
                                                   renderer.Render(document, currentUser.Language),
                                                   document.Supplier.Name, Blank(document.Supplier.Email)));
    }

    /// <summary>
    /// Sends the order to the supplier's e-mail address with the PDF attached, and marks an approved
    /// order as sent once the message has gone. Sending again an order already sent is allowed (a
    /// supplier who lost it) and changes nothing on the order.
    /// </summary>
    public async Task<Result> EmailAsync(Guid purchaseOrderId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.ManagePurchaseOrders);
        if (permitted.IsFailure) return permitted;

        var document = await queries.GetDocumentAsync(purchaseOrderId, ct);
        if (document is null) return Result.Failure(Error.NotFound("Po_NotFound"));

        if (document.Status is not (PurchaseOrderStatus.Approved or PurchaseOrderStatus.Sent
                                    or PurchaseOrderStatus.PartiallyReceived))
            return Result.Failure(Error.Conflict("Po_MustBeApprovedBeforeSending"));

        if (!messages.CanSendEmail)
            return Result.Failure(Error.Validation("Purchasing_EmailNotConfigured"));

        if (Blank(document.Supplier.Email) is not { } address)
            return Result.Failure(Error.Validation("Po_SupplierEmailMissing", document.Supplier.Name));

        var pdf = renderer.Render(document, currentUser.Language);
        var laboratory = string.IsNullOrWhiteSpace(document.Laboratory.Name)
            ? document.Laboratory.LegalName
            : document.Laboratory.Name;

        var sent = await messages.SendEmailAsync(new EmailMessage(
            address,
            $"Bon de commande {document.Number} — {laboratory}",
            EmailBody(document, laboratory),
            [new EmailAttachment(document.FileName, pdf, "application/pdf")]), ct);

        if (sent.IsFailure) return sent;

        if (document.Status != PurchaseOrderStatus.Approved) return Result.Success();

        var order = await orders.GetByIdWithDetailsAsync(purchaseOrderId, ct);
        if (order is null) return Result.Failure(Error.NotFound("Po_NotFound"));

        var marked = order.Send(clock.Now);
        if (marked.IsFailure) return marked;

        unitOfWork.AuditReason = $"Envoi par e-mail du bon de commande {order.Number} à {address}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    public static bool IsPrintable(PurchaseOrderStatus status) =>
        status is PurchaseOrderStatus.Approved or PurchaseOrderStatus.Sent
               or PurchaseOrderStatus.PartiallyReceived or PurchaseOrderStatus.Received;

    /// <summary>
    /// The message itself, in French like the order it carries: it is read by the supplier's sales
    /// administration, not by the operator who sends it.
    /// </summary>
    internal static string EmailBody(PurchaseOrderDocument document, string laboratory)
    {
        var lines = new List<string>
        {
            "Madame, Monsieur,",
            string.Empty,
            $"Veuillez trouver ci-joint notre bon de commande N° {document.Number} du {document.OrderDate:dd/MM/yyyy}."
        };

        if (document.ExpectedDeliveryDate is { } expected)
            lines.Add($"Livraison souhaitée au plus tard le {expected:dd/MM/yyyy}.");

        lines.Add("Merci d'en accuser réception et de rappeler ce numéro sur le bon de livraison et sur la facture.");
        lines.Add(string.Empty);
        lines.Add("Cordialement,");
        lines.Add(laboratory);

        if (!string.IsNullOrWhiteSpace(document.Laboratory.Phone)) lines.Add($"Tél. {document.Laboratory.Phone}");

        return string.Join(Environment.NewLine, lines);
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
