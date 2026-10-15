using MarocLIS.Domain.Billing;

namespace MarocLIS.Application.Accounting;

/// <summary>Who an amount is owed by, as the receivable accounts need it.</summary>
/// <param name="PatientRecordNumber">The patient's medical record number, the per-patient third party.</param>
/// <param name="PayerAccount">The payer's own account (<c>Payer.AccountingAccount</c>), if it has one.</param>
public sealed record AccountingCustomer(
    string? PatientRecordNumber,
    string? PatientName,
    string? PayerCode,
    string? PayerAccount);

/// <summary>A billed line's amounts, with the department its revenue account depends on.</summary>
/// <param name="DepartmentCode">Null for a line billing no analysis.</param>
public sealed record SalesLineSource(string? DepartmentCode, decimal Net, decimal Vat, bool IsVatExempt, decimal VatRate);

/// <summary>
/// An issued invoice, receipt or credit note as the sales journal needs it.
/// </summary>
/// <param name="PayerClaimed">The payer's share is claimed from the payer (a claim was prepared): it is the payer's receivable.</param>
/// <param name="Original">For a credit note: the invoice it reverses, to reverse the same accounts.</param>
public sealed record SalesDocumentSource(
    Guid SiteId,
    string SiteCode,
    InvoiceKind Kind,
    string Number,
    DateOnly IssueDate,
    AccountingCustomer Customer,
    bool PayerClaimed,
    decimal Total,
    decimal Vat,
    decimal PayerShare,
    IReadOnlyList<SalesLineSource> Lines,
    SalesDocumentSource? Original = null);

/// <summary>
/// A settlement line of an invoice, with what happened to it since: bounced, refunded, or (a
/// write-off) cancelled when the claim it closed was sent again.
/// </summary>
/// <param name="InvoiceDate">The settled invoice's issue date: a deposit is applied to it on that day.</param>
/// <param name="DepositNumber">Set when the payment is a deposit applied to the invoice.</param>
/// <param name="CashSessionId">The till shift that took it, if any.</param>
public sealed record SalesPaymentSource(
    Guid SiteId,
    string SiteCode,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    AccountingCustomer Customer,
    bool PayerClaimed,
    PaymentMethod Method,
    PaymentStatus Status,
    decimal Amount,
    DateOnly PaymentDate,
    DateOnly? BouncedOn,
    DateOnly? RefundedOn,
    DateOnly? CancelledOn,
    string? DepositNumber,
    Guid? CashSessionId,
    DateOnly? CashSessionDate,
    string? Reference);

/// <summary>
/// The part of a payer's share the payer refused and the patient is left to pay: it moves from the
/// payer's receivable to the patient's.
/// </summary>
public sealed record PayerShortfallSource(
    Guid SiteId,
    string SiteCode,
    string InvoiceNumber,
    DateOnly Date,
    AccountingCustomer Customer,
    decimal Amount);

/// <summary>A deposit (acompte) taken before the invoice, and what was handed back of it.</summary>
public sealed record DepositSource(
    Guid SiteId,
    string SiteCode,
    string Number,
    AccountingCustomer Customer,
    PaymentMethod Method,
    decimal Amount,
    DateOnly ReceivedOn,
    decimal RefundedAmount,
    DateOnly? RefundedOn,
    Guid? CashSessionId,
    string? Reference);

/// <summary>A closed till shift: the cash it sent to the bank and the difference found at the count.</summary>
/// <param name="Variance">Counted minus expected: negative is a shortfall.</param>
public sealed record CashSessionSource(
    Guid SiteId,
    string SiteCode,
    Guid CashSessionId,
    string OperatorName,
    DateOnly OpenedOn,
    DateOnly ClosedOn,
    decimal Withdrawals,
    decimal Variance);

/// <summary>A closed payroll month of a site, summed over its payslips.</summary>
/// <param name="EmployerCharges">CNSS, family allowances, training tax and AMO, the employer's share.</param>
/// <param name="EmployeeContributions">CNSS and AMO withheld from the employees.</param>
public sealed record PayrollMonthSource(
    Guid SiteId,
    string SiteCode,
    int Year,
    int Month,
    int PayslipCount,
    decimal Gross,
    decimal EmployerCharges,
    decimal EmployeeContributions,
    decimal IncomeTax,
    decimal Advances,
    decimal OtherDeductions,
    decimal NetPay)
{
    public DateOnly LastDay => new DateOnly(Year, Month, 1).AddMonths(1).AddDays(-1);
}

/// <summary>Everything the sales and receipts journals are built from, for one period.</summary>
public sealed record SalesAccountingSources(
    IReadOnlyList<SalesDocumentSource> Documents,
    IReadOnlyList<SalesPaymentSource> Payments,
    IReadOnlyList<PayerShortfallSource> Shortfalls,
    IReadOnlyList<DepositSource> Deposits,
    IReadOnlyList<CashSessionSource> CashSessions)
{
    public static SalesAccountingSources Empty { get; } = new([], [], [], [], []);
}
