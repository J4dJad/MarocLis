using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Billing;

/// <summary>
/// Everything printed on an invoice or on its payment receipt, read in one go.
/// <para>
/// The billed-to identity and every amount come from the invoice as it was frozen at issue, never
/// from the current patient, payer or tariff records: a reprint has to say what the customer was
/// handed. Only the laboratory's own letterhead is read from the site, because its legal identity
/// is the issuer's and is the same on every document.
/// </para>
/// </summary>
public sealed record InvoiceDocument(
    Guid InvoiceId,
    string Number,
    InvoiceKind Kind,
    InvoiceStatus Status,
    DateOnly IssueDate,
    DateOnly? DueDate,
    InvoiceIssuer Issuer,
    string BilledToName,
    string? BilledToIce,
    string? BilledToFiscalId,
    string? BilledToAddress,
    string? BilledToCity,
    string? PatientName,
    string? PatientRecordNumber,
    string? PayerName,
    string? CoveredOrders,
    string? PrescriberName,
    string? CorrectsInvoiceNumber,
    string? CorrectionReason,
    decimal GrossAmount,
    decimal DiscountAmount,
    string? DiscountReason,
    decimal NetAmount,
    decimal VatAmount,
    decimal TotalAmount,
    decimal PayerShareAmount,
    decimal PatientShareAmount,
    decimal PaidAmount,
    string? AmountInWords,
    IReadOnlyList<InvoiceDocumentLine> Lines,
    IReadOnlyList<InvoiceDocumentPayment> Payments)
{
    /// <summary>Total of the credit notes issued against this invoice.</summary>
    public decimal CreditedAmount { get; init; }

    /// <summary>Same rule as the invoice itself: what was credited is no longer owed.</summary>
    public decimal OutstandingAmount =>
        Invoice.ComputeOutstanding(Kind, TotalAmount, PaidAmount, CreditedAmount);

    /// <summary>True when at least one line carries VAT.</summary>
    public bool HasVat => VatAmount > 0m;
}

/// <summary>The laboratory's legal identity, as the DGI requires it on every invoice.</summary>
public sealed record InvoiceIssuer(
    string Name,
    string LegalName,
    string? Address,
    string? City,
    string? PostalCode,
    string? Phone,
    string? Email,
    string? Ice,
    string? FiscalId,
    string? CommercialRegisterNumber,
    string? CommercialRegisterCourt,
    string? ProfessionalTaxNumber,
    string? CnssAffiliationNumber,
    string? Inpe,
    byte[]? LogoPng);

/// <summary>One billed act, with the amounts already computed.</summary>
public sealed record InvoiceDocumentLine(
    int LineNumber,
    string? Code,
    string Description,
    int Quantity,
    decimal UnitPrice,
    decimal NetAmount,
    decimal VatAmount,
    decimal TotalAmount,
    decimal PayerShare,
    decimal PatientShare,
    bool IsVatExempt,
    decimal VatRate);

/// <summary>One settlement, as the receipt lists it.</summary>
public sealed record InvoiceDocumentPayment(
    DateOnly PaymentDate,
    PaymentMethod Method,
    PaymentStatus Status,
    decimal Amount,
    string? Reference,
    string? BankName)
{
    /// <summary>Only a settled payment counts towards what has been paid.</summary>
    public bool IsSettled => Status == PaymentStatus.Settled;
}

/// <summary>Turns an <see cref="InvoiceDocument"/> into a printable PDF.</summary>
public interface IInvoiceRenderer
{
    /// <summary>The invoice (or credit note) itself.</summary>
    byte[] RenderInvoice(InvoiceDocument doc, AppLanguage language);

    /// <summary>A payment receipt: what was paid, when, how, and what is left.</summary>
    byte[] RenderReceipt(InvoiceDocument doc, AppLanguage language);
}
