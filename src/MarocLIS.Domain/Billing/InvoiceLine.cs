using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Billing;

/// <summary>
/// One billed act. Every descriptive and tariff element is copied onto the line rather than
/// referenced, because an invoice has to reprint identically years later and because AMO claims are
/// checked line by line against the codes that were actually billed.
/// </summary>
public sealed class InvoiceLine : Entity
{
    private InvoiceLine() { }

    public InvoiceLine(string description, decimal unitPrice, int quantity = 1)
    {
        Description = description;
        UnitPrice = Money.Round(unitPrice);
        Quantity = quantity;
    }

    public Guid InvoiceId { get; private set; }
    internal void AttachTo(Guid invoiceId) => InvoiceId = invoiceId;

    public int LineNumber { get; set; }

    /// <summary>Label as printed. Snapshotted from the analysis name in the invoice's language.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Arabic label, for an invoice issued in Arabic.</summary>
    public string? DescriptionArabic { get; set; }

    // ---- What was billed ----------------------------------------------------------------------

    public Guid? LabOrderId { get; set; }
    public Guid? LabOrderItemId { get; set; }
    public Guid? AnalysisTestId { get; set; }

    /// <summary>Internal analysis code, printed so the laboratory can reconcile a query.</summary>
    public string? AnalysisCode { get; set; }

    /// <summary>Act code in the national nomenclature, as required on the claim.</summary>
    public string? NationalActCode { get; set; }

    /// <summary>Letter-key of the act — "B" for biology.</summary>
    public string? LetterKey { get; set; }

    /// <summary>Coefficient of the act.</summary>
    public decimal? Coefficient { get; set; }

    /// <summary>Value of the letter-key used, so <c>Coefficient × LetterKeyValue</c> is reproducible.</summary>
    public decimal? LetterKeyValue { get; set; }

    /// <summary>
    /// Reimbursement base under the national reference tariff. May be lower than
    /// <see cref="UnitPrice"/>: the excess is borne entirely by the patient.
    /// </summary>
    public decimal ReferenceTariffBase { get; set; }

    /// <summary>Date the act was performed, which can differ from the invoice date.</summary>
    public DateOnly? ServiceDate { get; set; }

    public int Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }

    /// <summary>Line total before any invoice-level discount.</summary>
    public decimal GrossAmount => Money.Round(UnitPrice * Quantity);

    /// <summary>Share of the invoice-level discount allocated to this line.</summary>
    public decimal AllocatedDiscount { get; private set; }

    internal void SetAllocatedDiscount(decimal amount) => AllocatedDiscount = Money.Round(amount);

    /// <summary>Taxable base for this line.</summary>
    public decimal NetAmount => Money.Round(GrossAmount - AllocatedDiscount);

    // ---- Tax ----------------------------------------------------------------------------------

    /// <summary>
    /// Medical biology acts are VAT-exempt in Morocco, so this is true for analysis lines. It is a
    /// per-line flag because the same invoice may bill non-exempt items.
    /// </summary>
    public bool IsVatExempt { get; set; } = true;

    /// <summary>VAT rate as a fraction (0.20 = 20 %). Ignored when the line is exempt.</summary>
    public decimal VatRate { get; set; }

    public decimal VatAmount => IsVatExempt ? 0m : Money.Round(NetAmount * VatRate);

    public decimal TotalAmount => Money.Round(NetAmount + VatAmount);

    // ---- Split between payer and patient -------------------------------------------------------

    /// <summary>Coverage rate applied, as a fraction of the reference base.</summary>
    public decimal? ReimbursementRate { get; set; }

    /// <summary>
    /// Amount the payer is expected to reimburse. Set by the pricing service via
    /// <see cref="SetCoverageSplit"/> rather than computed here, because the split depends on the
    /// order's frozen coverage snapshot rather than on the line alone.
    /// </summary>
    public decimal PayerShare { get; private set; }

    public decimal PatientShare => Money.Round(TotalAmount - PayerShare);

    /// <summary>
    /// Applies a coverage split. The payer never reimburses more than its share of the reference
    /// base, and never more than the line's own total — any excess charged above the national
    /// tariff falls to the patient, which is exactly how AMO settlement works.
    /// </summary>
    public void SetCoverageSplit(decimal reimbursementRate, bool payerUsesReferenceTariff)
    {
        ReimbursementRate = reimbursementRate;

        var basis = payerUsesReferenceTariff
            ? Money.Round(ReferenceTariffBase * Quantity)
            : NetAmount;

        var share = Money.Round(basis * reimbursementRate);
        PayerShare = Math.Clamp(share, 0m, TotalAmount);
    }

    public void ClearCoverage()
    {
        ReimbursementRate = null;
        PayerShare = 0m;
    }

    /// <summary>Amount charged above the reference tariff, entirely on the patient.</summary>
    public decimal ExcessOverReferenceTariff =>
        Money.Round(Math.Max(0m, NetAmount - ReferenceTariffBase * Quantity));
}
