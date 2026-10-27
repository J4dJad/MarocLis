using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Billing;

/// <summary>
/// A sales invoice (facture). Moroccan tax law requires invoices to be numbered in an unbroken
/// chronological series that cannot be altered after issue, and to carry the issuer's legal
/// identifiers. Consequently: numbers are allocated by a gapless per-site, per-year sequence, an
/// issued invoice is never edited or deleted, and a correction is always a credit note
/// (facture d'avoir) referring back to the original.
/// </summary>
public sealed class Invoice : AuditableAggregateRoot, ISiteScoped
{
    private readonly List<InvoiceLine> _lines = [];
    private readonly List<Payment> _payments = [];
    private readonly List<PayerShortfallTransfer> _shortfallTransfers = [];

    private Invoice() { }

    public Invoice(string number, Guid siteId, InvoiceKind kind, DateOnly issueDate,
                   Guid issuedByUserId)
    {
        Number = number;
        SiteId = siteId;
        Kind = kind;
        IssueDate = issueDate;
        IssuedByUserId = issuedByUserId;
        Status = InvoiceStatus.Draft;
    }

    /// <summary>
    /// Sequential number, e.g. "FAC/CASA/2026/000318". Allocated at issue, never at draft
    /// creation, so an abandoned draft cannot leave a hole in the series.
    /// </summary>
    public string Number { get; private set; } = string.Empty;

    public Guid SiteId { get; set; }
    public InvoiceKind Kind { get; private set; }
    public InvoiceStatus Status { get; private set; }

    public DateOnly IssueDate { get; private set; }
    public DateTimeOffset? IssuedAt { get; private set; }
    public Guid IssuedByUserId { get; private set; }

    // ---- Customer ----------------------------------------------------------------------------

    /// <summary>Set when the invoice is addressed to a patient.</summary>
    public Guid? PatientId { get; set; }

    /// <summary>Set when it is addressed to a payer under a third-party agreement.</summary>
    public Guid? PayerId { get; set; }

    /// <summary>
    /// Billing identity frozen at issue. An invoice must keep showing the name and ICE that were
    /// on it when it was issued, even if the customer record is corrected afterwards.
    /// </summary>
    public string BilledToName { get; set; } = string.Empty;
    public string? BilledToIce { get; set; }
    public string? BilledToFiscalId { get; set; }
    public string? BilledToAddress { get; set; }
    public string? BilledToCity { get; set; }

    /// <summary>Orders covered. A payer invoice typically groups a month of orders.</summary>
    public string? CoveredOrderNumbersCsv { get; set; }

    // ---- Amounts, all in dirham ---------------------------------------------------------------

    /// <summary>Sum of the line amounts before discount.</summary>
    public decimal GrossAmount { get; private set; }

    public decimal DiscountAmount { get; private set; }
    public string? DiscountReason { get; set; }

    /// <summary>Taxable base after discount.</summary>
    public decimal NetAmount { get; private set; }

    /// <summary>
    /// VAT. Medical biology acts are exempt in Morocco, so this is normally zero; it is still
    /// computed because the same invoice may carry non-exempt items such as certificates.
    /// </summary>
    public decimal VatAmount { get; private set; }

    public decimal TotalAmount { get; private set; }

    /// <summary>Share the payer is expected to settle under the coverage.</summary>
    public decimal PayerShareAmount { get; private set; }

    /// <summary>Share the patient owes — ticket modérateur plus any excess over the reference tariff.</summary>
    public decimal PatientShareAmount { get; private set; }

    /// <summary>
    /// What closes the receivable: every settled line, write-offs included. Use
    /// <see cref="CollectedAmount"/> for what was actually paid.
    /// </summary>
    public decimal PaidAmount { get; private set; }

    /// <summary>
    /// The part of <see cref="PaidAmount"/> that was written off: it closes the balance, but nobody
    /// paid it, so a receipt or the ledger must not show it as paid.
    /// </summary>
    public decimal WrittenOffAmount =>
        Money.Round(_payments.Where(p => p.Status == PaymentStatus.Settled
                                         && p.Method == PaymentMethod.WriteOff)
                             .Sum(p => p.Amount));

    /// <summary>Money that actually came in on this invoice.</summary>
    public decimal CollectedAmount => Money.Round(PaidAmount - WrittenOffAmount);

    /// <summary>
    /// Sum of the credit notes issued against this invoice. A credit note withdraws part of the
    /// charge, so it reduces what is still owed exactly as a settlement does, without being one.
    /// </summary>
    public decimal CreditedAmount { get; private set; }

    /// <summary>
    /// What is still owed: the total less what was paid and what was credited. A credit note owes
    /// nothing — it is a reversal, settled by its own issue.
    /// </summary>
    public decimal OutstandingAmount => ComputeOutstanding(Kind, TotalAmount, PaidAmount, CreditedAmount);

    /// <summary>
    /// What the patient may still pay at the counter. On an invoice whose payer share is claimed
    /// from the payer, that share is settled through the claim until the payer has answered: taken
    /// from the patient as well, it was claimed again and the payer's payment could not be entered.
    /// </summary>
    public decimal PatientPayableAmount =>
        ComputePatientPayable(OutstandingAmount, ClaimStatus, PayerShareAmount, PayerAcceptedAmount);

    /// <summary>
    /// The patient-payable rule, shared with the ledger rows. The payer's share stays reserved
    /// while a claim is prepared or pending; once the payer's answer is recorded, its payment is in
    /// and the shortfall it left is the patient's to pay.
    /// </summary>
    public static decimal ComputePatientPayable(decimal outstanding, ClaimStatus claimStatus,
                                                decimal payerShare, decimal? payerAccepted) =>
        claimStatus == ClaimStatus.NotApplicable || payerAccepted is not null
            ? outstanding
            : Math.Max(0m, Money.Round(outstanding - payerShare));

    /// <summary>
    /// Whether the claim forbids a credit note: sent to the payer or answered by it, the same acts
    /// would be claimed again on the invoice that replaces this one. A rejected claim does not.
    /// </summary>
    public static bool ClaimBlocksCreditNote(ClaimStatus claimStatus) =>
        claimStatus is ClaimStatus.Submitted or ClaimStatus.Resubmitted or ClaimStatus.Accepted
                    or ClaimStatus.PartiallyAccepted or ClaimStatus.Settled;

    /// <summary>
    /// The part of the payer's share the credit notes took off: they come off the patient's share
    /// first, as the accounts take them, and off the payer's only beyond it. The payer no longer
    /// owes it, so it is neither its to settle nor a shortfall to leave to the patient.
    /// </summary>
    public decimal PayerCreditedAmount =>
        Math.Min(PayerShareAmount, Math.Max(0m, Money.Round(CreditedAmount - (TotalAmount - PayerShareAmount))));

    /// <summary>What the payer still owes on its share before its answer: the share less what the credit notes took off it.</summary>
    public decimal PayerDueAmount => ComputePayerDue(TotalAmount, PayerShareAmount, CreditedAmount);

    /// <summary>The payer-due rule, shared with the ledger rows that read the stored columns.</summary>
    public static decimal ComputePayerDue(decimal total, decimal payerShare, decimal credited) =>
        Money.Round(payerShare - Math.Min(payerShare, Math.Max(0m, Money.Round(credited - (total - payerShare)))));

    /// <summary>What a further credit note may still reverse.</summary>
    public decimal CreditableAmount =>
        Kind == InvoiceKind.Invoice ? Math.Max(0m, Money.Round(TotalAmount - CreditedAmount)) : 0m;

    /// <summary>True once credit notes have reversed the whole total.</summary>
    public bool IsFullyCredited => CreditedAmount > 0m && CreditedAmount >= TotalAmount;

    /// <summary>
    /// The outstanding rule, shared with the queries that read the stored columns and cannot call
    /// <see cref="OutstandingAmount"/> in SQL.
    /// </summary>
    public static decimal ComputeOutstanding(InvoiceKind kind, decimal total, decimal paid,
                                             decimal credited) =>
        kind == InvoiceKind.CreditNote
            ? 0m
            : Math.Max(0m, Money.Round(total - paid - credited));

    /// <summary>Total in words, a customary mention on Moroccan invoices.</summary>
    public string? AmountInWords { get; set; }

    public DateOnly? DueDate { get; set; }

    // ---- Credit note linkage ------------------------------------------------------------------

    /// <summary>For a credit note: the invoice it corrects.</summary>
    public Guid? CorrectsInvoiceId { get; set; }
    public string? CorrectsInvoiceNumber { get; set; }

    /// <summary>Why the credit note was issued — mandatory, it is the audit trail for the reversal.</summary>
    public string? CorrectionReason { get; set; }

    /// <summary>Set on the original when a credit note has reversed it, wholly or partly.</summary>
    public bool HasCreditNote { get; private set; }

    // ---- AMO claim ----------------------------------------------------------------------------

    /// <summary>Claim reference for a third-party payer submission.</summary>
    public string? ClaimReference { get; set; }
    public DateOnly? ClaimSubmittedOn { get; set; }
    public ClaimStatus ClaimStatus { get; set; } = ClaimStatus.NotApplicable;
    public string? ClaimRejectionReason { get; set; }

    /// <summary>
    /// What the payer actually agreed to pay on its share, once it has answered. Null until then.
    /// The difference with <see cref="PayerShareAmount"/> is either left for the patient to pay or
    /// written off, and the choice is recorded as a payment line either way.
    /// </summary>
    public decimal? PayerAcceptedAmount { get; set; }

    /// <summary>Biologist INPE carried on the claim, required by some payers.</summary>
    public string? BiologistInpe { get; set; }

    /// <summary>Prescriber identifiers required on the claim.</summary>
    public string? PrescriberInpe { get; set; }
    public string? PrescriberName { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// The rendered PDF as issued. An invoice is an accounting document; keeping the exact
    /// artefact removes any argument about what the customer was handed.
    /// </summary>
    public byte[]? RenderedPdf { get; set; }
    public string? ContentHash { get; set; }

    public IReadOnlyCollection<InvoiceLine> Lines => _lines.AsReadOnly();

    /// <summary>The patient's payments that stand: settled, neither the payer's nor a write-off.</summary>
    /// <remarks>A method, not a property: a collection property of payments would be mapped as a second relationship.</remarks>
    public IReadOnlyList<Payment> StandingPatientPayments() =>
    [
        .. _payments.Where(p => p.Status == PaymentStatus.Settled
                                && p.Method is not (PaymentMethod.ThirdPartyPayer or PaymentMethod.WriteOff))
    ];
    public IReadOnlyCollection<Payment> Payments => _payments.AsReadOnly();

    /// <summary>The payer's refused shares left to the patient, each dated on the payer's answer.</summary>
    public IReadOnlyCollection<PayerShortfallTransfer> ShortfallTransfers => _shortfallTransfers.AsReadOnly();

    public bool IsIssued => Status is not InvoiceStatus.Draft;

    /// <summary>An issued invoice is frozen. Nothing in the domain reopens it.</summary>
    public bool IsMutable => Status == InvoiceStatus.Draft;

    // ---- Composition --------------------------------------------------------------------------

    public Result AddLine(InvoiceLine line)
    {
        if (!IsMutable)
            return Result.Failure(Error.Regulatory("Invoice_IssuedIsImmutable"));

        line.AttachTo(Id);
        line.LineNumber = _lines.Count + 1;
        _lines.Add(line);
        Recalculate();
        return Result.Success();
    }

    public Result RemoveLine(Guid lineId)
    {
        if (!IsMutable)
            return Result.Failure(Error.Regulatory("Invoice_IssuedIsImmutable"));

        var line = _lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null) return Result.Failure(Error.NotFound("Invoice_LineNotFound"));

        _lines.Remove(line);
        // Keep line numbers contiguous while the invoice is still a draft.
        var number = 1;
        foreach (var remaining in _lines) remaining.LineNumber = number++;
        Recalculate();
        return Result.Success();
    }

    public Result ApplyDiscount(decimal amount, string reason)
    {
        if (!IsMutable)
            return Result.Failure(Error.Regulatory("Invoice_IssuedIsImmutable"));
        if (amount < 0m)
            return Result.Failure(Error.Validation("Invoice_DiscountNegative"));
        if (amount > GrossAmount)
            return Result.Failure(Error.Validation("Invoice_DiscountExceedsGross"));
        if (amount > 0m && string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Invoice_DiscountReasonRequired"));

        DiscountAmount = Money.Round(amount);
        DiscountReason = reason;
        Recalculate();
        return Result.Success();
    }

    /// <summary>
    /// Recomputes every total from the lines. The discount is spread across the lines in proportion
    /// to their gross amount so that the VAT on the non-exempt lines stays correct, and the
    /// remainder from rounding is allocated rather than dropped.
    /// </summary>
    public void Recalculate()
    {
        GrossAmount = Money.Round(_lines.Sum(l => l.GrossAmount));

        if (DiscountAmount > GrossAmount) DiscountAmount = GrossAmount;

        NetAmount = Money.Round(GrossAmount - DiscountAmount);

        if (GrossAmount > 0m && DiscountAmount > 0m)
        {
            var allocated = 0m;
            for (var i = 0; i < _lines.Count; i++)
            {
                var line = _lines[i];
                var share = i == _lines.Count - 1
                    ? Money.Round(DiscountAmount - allocated)
                    : Money.Round(DiscountAmount * (line.GrossAmount / GrossAmount));
                allocated += share;
                line.SetAllocatedDiscount(share);
            }
        }
        else
        {
            foreach (var line in _lines) line.SetAllocatedDiscount(0m);
        }

        VatAmount = Money.Round(_lines.Sum(l => l.VatAmount));
        TotalAmount = Money.Round(NetAmount + VatAmount);

        // The coverage split is set before the discount is known. A payer never reimburses more
        // than what the line finally costs, so the share is re-capped on the discounted total —
        // otherwise the patient share goes negative.
        foreach (var line in _lines) line.CapPayerShareToTotal();

        PayerShareAmount = Money.Round(_lines.Sum(l => l.PayerShare));
        PatientShareAmount = Money.Round(TotalAmount - PayerShareAmount);
        PaidAmount = Money.Round(_payments.Where(p => p.Status == PaymentStatus.Settled)
                                          .Sum(p => p.Amount));

        DeriveStatus();
    }

    /// <summary>
    /// Settlement status from the amounts. Called on every recomputation, so a refund or a bounced
    /// cheque puts a paid invoice back to partly paid or unpaid. A draft, a cancelled draft and a
    /// write-off keep their status: those are decisions, not arithmetic.
    /// </summary>
    private void DeriveStatus()
    {
        if (Status is InvoiceStatus.Draft or InvoiceStatus.Cancelled or InvoiceStatus.WrittenOff)
            return;

        // A credit note is settled by its own issue; nobody pays it.
        if (Kind == InvoiceKind.CreditNote)
        {
            Status = InvoiceStatus.Paid;
            return;
        }

        // Reversed in full by credit notes: the invoice no longer charges anything. Shown as paid,
        // it told the counter the patient had settled it, whatever they had paid on it.
        if (IsFullyCredited)
        {
            Status = InvoiceStatus.Credited;
            return;
        }

        Status = OutstandingAmount <= 0m ? InvoiceStatus.Paid
            : PaidAmount > 0m ? InvoiceStatus.PartiallyPaid
            : InvoiceStatus.Issued;
    }

    /// <summary>
    /// Issues the invoice with its definitive number. After this the document is immutable; the
    /// only lawful correction is a credit note.
    /// </summary>
    public Result Issue(string allocatedNumber, DateTimeOffset now, string? amountInWords)
    {
        if (IsIssued)
            return Result.Failure(Error.Conflict("Invoice_AlreadyIssued"));
        if (_lines.Count == 0)
            return Result.Failure(Error.Validation("Invoice_NoLines"));
        if (string.IsNullOrWhiteSpace(BilledToName))
            return Result.Failure(Error.Regulatory("Invoice_BilledToRequired"));
        if (string.IsNullOrWhiteSpace(allocatedNumber))
            return Result.Failure(Error.Validation("Invoice_NumberRequired"));

        Number = allocatedNumber;
        IssuedAt = now;
        AmountInWords = amountInWords;
        Status = InvoiceStatus.Issued;

        // Recomputes the totals and derives the settlement status now that it is issued.
        Recalculate();
        return Result.Success();
    }

    // ---- Payments -----------------------------------------------------------------------------

    public Result RecordPayment(Payment payment)
    {
        if (!IsIssued)
            return Result.Failure(Error.Conflict("Invoice_MustBeIssuedBeforePayment"));
        if (Status == InvoiceStatus.Cancelled)
            return Result.Failure(Error.Conflict("Invoice_Cancelled"));
        // A credit note is money the laboratory owes back, and a quotation is not a charge: neither
        // is settled. A receipt documents a cash payment and does carry one.
        if (Kind is not (InvoiceKind.Invoice or InvoiceKind.Receipt))
            return Result.Failure(Error.Conflict("Payment_KindNotPayable"));
        if (payment.Amount <= 0m)
            return Result.Failure(Error.Validation("Payment_AmountMustBePositive"));
        if (payment.Amount > OutstandingAmount)
            return Result.Failure(Error.Validation("Payment_ExceedsOutstanding", Money.Of(OutstandingAmount)));
        // The payer's own settlement and the write-off of its refused share are what close that
        // share; anything else is paid by the patient, who owes only their part.
        if (payment.Method is not (PaymentMethod.ThirdPartyPayer or PaymentMethod.WriteOff)
            && payment.Amount > PatientPayableAmount)
            return Result.Failure(Error.Validation("Payment_ExceedsPatientShare", Money.Of(PatientPayableAmount)));

        payment.AttachTo(Id);
        _payments.Add(payment);
        Recalculate();

        return Result.Success();
    }

    /// <summary>
    /// Records the payer's answer on its share: what it accepted, its settlement and any write-off
    /// of the refusal being already on the invoice. What the payer refused and nobody wrote off is
    /// left to the patient: that transfer is recorded on <paramref name="on"/>, the day of the
    /// answer, so the accounts date it on the answer and not on the invoice's next change. The part
    /// of the share a credit note already took off is not refused: nobody owes it any more.
    /// </summary>
    public void RecordPayerAnswer(decimal accepted, DateOnly on)
    {
        PayerAcceptedAmount = Money.Round(accepted);

        var leftToPatient = Money.Round(PayerDueAmount - PayerAcceptedAmount.Value - WrittenOffAmount);
        if (leftToPatient > 0m)
            _shortfallTransfers.Add(new PayerShortfallTransfer(Id, leftToPatient, on));
    }

    /// <summary>
    /// Puts a rejected claim back in the state it was before the payer's answer was recorded, so
    /// it can be corrected and sent again.
    /// <para>
    /// A payer rejection followed by a corrected resubmission is routine with Moroccan payers. The
    /// recorded settlement used to stay behind: the accepted amount blocked the payer's eventual
    /// payment from being entered, and the write-off of the refused share closed the balance it
    /// was meant to pay. The write-off is cancelled — not deleted, it stays on the record — and
    /// the payer's answer is cleared.
    /// </para>
    /// <para>
    /// Refused while the patient has already paid the share left to them: the payer would be asked
    /// for money the laboratory holds, and its payment could not be entered on an invoice that is
    /// settled. The patient is refunded first, then the claim goes out again.
    /// </para>
    /// </summary>
    /// <param name="on">The day of the reopening: a refused share left to the patient goes back to the payer that day.</param>
    public Result ReopenRejectedClaim(string reason, DateOnly? on = null)
    {
        if (ClaimStatus != ClaimStatus.Rejected)
            return Result.Failure(Error.Conflict("Claim_AlreadySubmitted", Number));

        // The payer paid something on it: the refusal recorded over it was a batch-level answer
        // that did not concern this invoice, and reopening would undo a settlement that stands.
        if (PayerAcceptedAmount is > 0m
            || _payments.Any(p => p.Method == PaymentMethod.ThirdPartyPayer
                                  && p.Status == PaymentStatus.Settled))
            return Result.Failure(Error.Conflict("Claim_SettlementAlreadyRecorded", Number));

        // What would be owed once the write-offs are cancelled: the payer's share must fit in it.
        var reopenedOutstanding = Money.Round(OutstandingAmount
            + _payments.Where(p => p.Method == PaymentMethod.WriteOff && p.Status == PaymentStatus.Settled)
                       .Sum(p => p.Amount));

        if (reopenedOutstanding < PayerDueAmount)
            return Result.Failure(Error.Conflict("Claim_ShortfallPaidByPatient", Number,
                                                 Money.Round(PayerDueAmount - reopenedOutstanding)));

        foreach (var writeOff in _payments.Where(p => p.Method == PaymentMethod.WriteOff
                                                      && p.Status == PaymentStatus.Settled))
        {
            var cancelled = writeOff.CancelWriteOff(reason);
            if (cancelled.IsFailure) return cancelled;
        }

        // The refusal left to the patient is undone as well: the amount is the payer's again.
        var reopenedOn = on ?? DateOnly.FromDateTime(DateTime.Today);
        foreach (var transfer in _shortfallTransfers.Where(t => t.ReversedOn is null))
            transfer.Reverse(reopenedOn);

        PayerAcceptedAmount = null;
        Recalculate();

        return Result.Success();
    }

    // ---- Credit notes -------------------------------------------------------------------------

    /// <summary>
    /// Fills a draft credit note with a mirror of every line of <paramref name="original"/>, for a
    /// full reversal. The lines carry their gross price, so the original's discount is carried too:
    /// the credit note reverses what was charged, not the list price, and its total equals the
    /// original's.
    /// </summary>
    public Result MirrorLinesOf(Invoice original)
    {
        if (Kind != InvoiceKind.CreditNote)
            return Result.Failure(Error.Conflict("Invoice_CannotCreditACreditNote"));

        foreach (var line in original.Lines.OrderBy(l => l.LineNumber))
        {
            var mirrored = new InvoiceLine(line.Description, line.UnitPrice, line.Quantity)
            {
                DescriptionArabic = line.DescriptionArabic,
                LabOrderId = line.LabOrderId,
                LabOrderItemId = line.LabOrderItemId,
                AnalysisTestId = line.AnalysisTestId,
                AnalysisCode = line.AnalysisCode,
                NationalActCode = line.NationalActCode,
                LetterKey = line.LetterKey,
                Coefficient = line.Coefficient,
                ReferenceTariffBase = line.ReferenceTariffBase,
                ServiceDate = line.ServiceDate,
                IsVatExempt = line.IsVatExempt,
                VatRate = line.VatRate
            };

            var added = AddLine(mirrored);
            if (added.IsFailure) return added;
        }

        if (original.DiscountAmount > 0m)
        {
            var discounted = ApplyDiscount(
                original.DiscountAmount,
                string.IsNullOrWhiteSpace(original.DiscountReason)
                    ? $"Remise de la facture {original.Number}"
                    : original.DiscountReason!);
            if (discounted.IsFailure) return discounted;
        }

        return Result.Success();
    }

    /// <summary>
    /// Checks that a credit note of <paramref name="amount"/> may be issued against this invoice:
    /// the credit notes together never reverse more than the invoice charged.
    /// </summary>
    public Result CanApplyCreditNote(decimal amount)
    {
        if (Kind != InvoiceKind.Invoice)
            return Result.Failure(Error.Conflict("Invoice_CannotCreditACreditNote"));
        if (!IsIssued || Status == InvoiceStatus.Cancelled)
            return Result.Failure(Error.Conflict("Invoice_CreditNoteRequiresIssued"));
        if (CreditableAmount <= 0m)
            return Result.Failure(Error.Conflict("Invoice_AlreadyFullyCredited", Number));
        if (ClaimBlocksCreditNote(ClaimStatus))
            return Result.Failure(Error.Conflict("Invoice_CreditNoteClaimSent", Number));
        if (amount <= 0m)
            return Result.Failure(Error.Validation("Invoice_CreditNoteAmountOutOfRange", CreditableAmount));
        if (Money.Round(amount) > CreditableAmount)
            return Result.Failure(Error.Validation("Invoice_CreditNoteExceedsBalance", CreditableAmount));
        return Result.Success();
    }

    /// <summary>
    /// Records a credit note issued against this invoice. The amount reduces what is owed and the
    /// status follows: a fully credited invoice is « Credited », whatever was paid on it. Money
    /// already paid beyond the reduced total is not touched here: the billing service carries it
    /// over to the orders as deposits, or it is handed back as a refund.
    /// </summary>
    public Result ApplyCreditNote(decimal amount)
    {
        var allowed = CanApplyCreditNote(amount);
        if (allowed.IsFailure) return allowed;

        CreditedAmount = Money.Round(CreditedAmount + amount);
        HasCreditNote = true;
        Recalculate();
        return Result.Success();
    }

    /// <summary>
    /// Cancels a draft. An issued invoice cannot be cancelled this way — the accounting series must
    /// stay unbroken, so the reversal goes through a credit note instead.
    /// </summary>
    public Result CancelDraft(string reason)
    {
        if (IsIssued)
            return Result.Failure(Error.Regulatory("Invoice_IssuedRequiresCreditNote"));
        Status = InvoiceStatus.Cancelled;
        Notes = reason;
        return Result.Success();
    }

    public bool IsOverdue(DateOnly today) =>
        Status is InvoiceStatus.Issued or InvoiceStatus.PartiallyPaid &&
        DueDate is { } due && due < today;

    public int DaysOverdue(DateOnly today) =>
        IsOverdue(today) && DueDate is { } due ? today.DayNumber - due.DayNumber : 0;
}

public enum InvoiceKind
{
    /// <summary>Facture — a charge.</summary>
    Invoice = 0,

    /// <summary>Facture d'avoir — a credit note reversing all or part of an invoice.</summary>
    CreditNote = 1,

    /// <summary>Devis — a quotation, carrying no accounting number.</summary>
    Quotation = 2,

    /// <summary>Reçu — a receipt for a cash payment with no itemised invoice.</summary>
    Receipt = 3
}

public enum InvoiceStatus
{
    Draft = 0,
    Issued = 1,
    PartiallyPaid = 2,
    Paid = 3,
    /// <summary>Written off as uncollectable, with an accounting entry behind it.</summary>
    WrittenOff = 4,
    /// <summary>
    /// Reversed in full by one or more credit notes (« Annulée par avoir »). What the patient had
    /// paid on it is carried over to the orders it billed, as deposits.
    /// </summary>
    Credited = 5,
    /// <summary>Only reachable for a draft.</summary>
    Cancelled = 9
}

public enum ClaimStatus
{
    NotApplicable = 0,
    /// <summary>Ready to submit to the payer.</summary>
    Prepared = 1,
    Submitted = 2,
    /// <summary>Accepted in full.</summary>
    Accepted = 3,
    /// <summary>Accepted for less than claimed.</summary>
    PartiallyAccepted = 4,
    Rejected = 5,
    /// <summary>Resubmitted after a rejection.</summary>
    Resubmitted = 6,
    Settled = 7
}
