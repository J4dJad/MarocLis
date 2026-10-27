using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Licensing;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.Billing;

/// <summary>An order awaiting invoicing, as the billing desk sees it.</summary>
public sealed record BillableOrderRow(
    Guid LabOrderId,
    string AccessionNumber,
    Guid PatientId,
    string PatientName,
    DateTimeOffset RegisteredAt,
    string? PayerName,
    Guid? PayerId,
    decimal Total,
    decimal PayerShare,
    decimal PatientShare,
    int AnalysisCount,
    LabOrderStatus Status,
    /// <summary>Deposits taken on the order and not yet turned into a settlement or refunded.</summary>
    decimal DepositHeld = 0m,
    /// <summary>The payer is billed directly for its share (frozen at registration).</summary>
    bool DirectBilling = false,
    /// <summary>
    /// Set when the order is already invoiced and listed only for the deposit left over to refund.
    /// </summary>
    Guid? InvoiceId = null,
    string? InvoiceNumber = null,
    /// <summary>
    /// Was on an invoice that credit notes have wholly reversed: it is waiting to be invoiced again.
    /// </summary>
    bool PreviouslyInvoiced = false,
    /// <summary>
    /// The order's payer has a third-party agreement in force: only then can an invoice be
    /// addressed to it and its share claimed.
    /// </summary>
    bool PayerAgreementValid = false)
{
    /// <summary>Whether the row can be put on a new invoice: neither cancelled nor invoiced.</summary>
    public bool IsInvoiceable => InvoiceId is null && Status != LabOrderStatus.Cancelled;

    /// <summary>
    /// What a further deposit may still cover: what the patient will owe less the deposits held.
    /// Under direct billing the payer's share is claimed from the payer, so only the patient's
    /// share can be taken in advance, as <see cref="BillingService.TakeDepositAsync"/> enforces.
    /// </summary>
    public decimal DepositCeiling =>
        ComputeDepositCeiling(Total, PatientShare, PayerId is not null && DirectBilling, DepositHeld);

    /// <summary>
    /// The deposit-ceiling rule — what the patient will owe on an order not invoiced yet, less the
    /// deposits held — shared with the report counter's payment status (<see cref="OrderPayment"/>).
    /// </summary>
    public static decimal ComputeDepositCeiling(decimal total, decimal patientShare, bool payerBilledDirectly,
                                                decimal depositHeld) =>
        Math.Max(0m, Money.Round((payerBilledDirectly ? patientShare : total) - depositHeld));
}

/// <summary>One invoice in the ledger view.</summary>
public sealed record InvoiceRow(
    Guid InvoiceId,
    string Number,
    InvoiceKind Kind,
    InvoiceStatus Status,
    DateOnly IssueDate,
    DateOnly? DueDate,
    string BilledToName,
    decimal Total,
    decimal Paid,
    decimal Outstanding,
    int DaysOverdue,
    ClaimStatus ClaimStatus,
    string? CoveredOrders,
    decimal PayerShare = 0m,
    /// <summary>What the payer agreed to pay on its share; null until the settlement is recorded.</summary>
    decimal? PayerAcceptedAmount = null,
    /// <summary>Total of the credit notes issued against the invoice.</summary>
    decimal Credited = 0m,
    /// <summary>
    /// Written off: closes the balance without having been paid, so it is not in <c>Paid</c>.
    /// </summary>
    decimal WrittenOff = 0m)
{
    /// <summary>What a further credit note may still reverse; zero for anything but an invoice.</summary>
    public decimal Creditable =>
        Kind == InvoiceKind.Invoice ? Math.Max(0m, Money.Round(Total - Credited)) : 0m;

    /// <summary>
    /// What the patient may still pay at the counter: the outstanding less the payer's share while
    /// it is claimed from the payer. The same rule as <see cref="Invoice.PatientPayableAmount"/>.
    /// </summary>
    public decimal PatientPayable =>
        Invoice.ComputePatientPayable(Outstanding, ClaimStatus, PayerShare, PayerAcceptedAmount);

    /// <summary>
    /// What the payer still owes on its share: the share less what credit notes took off it. The
    /// payer's settlement is entered and checked against this, not against the original share.
    /// </summary>
    public decimal PayerDue => Invoice.ComputePayerDue(Total, PayerShare, Credited);
}

public sealed record CashSessionSummary(
    Guid CashSessionId,
    Guid OperatorUserId,
    string OperatorName,
    DateTimeOffset OpenedAt,
    decimal OpeningFloat,
    decimal CashReceipts,
    decimal ChequeReceipts,
    decimal CardReceipts,
    decimal TransferReceipts,
    decimal BillOfExchangeReceipts,
    decimal MobileReceipts,
    decimal CashRefunds,
    decimal CashWithdrawals,
    decimal ExpectedClosingCash,
    int PaymentCount)
{
    /// <summary>
    /// The summary of an open shift, taken from the session's own running counters: the closing
    /// screen pre-fills and shows the variance on the same expected cash that
    /// <see cref="CashSession.Close"/> records it against.
    /// </summary>
    public static CashSessionSummary From(CashSession session, string operatorName, int paymentCount) =>
        new(session.Id,
            session.OperatorUserId,
            operatorName,
            session.OpenedAt,
            session.OpeningFloat,
            session.ExpectedCashReceipts,
            session.ChequeReceipts,
            session.CardReceipts,
            session.TransferReceipts,
            session.BillOfExchangeReceipts,
            session.MobileReceipts,
            session.CashRefunds,
            session.CashWithdrawals,
            session.ExpectedClosingCash,
            paymentCount);
}

public sealed record TakeDepositRequest(
    Guid LabOrderId,
    decimal Amount,
    PaymentMethod Method,
    string? Reference,
    string? BankName,
    DateOnly? MaturityDate);

public sealed record DepositRow(
    Guid DepositId,
    string Number,
    DateTimeOffset ReceivedAt,
    decimal Amount,
    decimal Remaining,
    PaymentMethod Method,
    DepositStatus Status);

/// <summary>
/// A claim batch as sent: its reference, how many invoices went into it, and the ones left out with
/// the reason for each. Some invoices refused do not undo the batch the others went into.
/// </summary>
public sealed record ClaimSubmission(string Reference, int Submitted, IReadOnlyList<Error> Skipped);

/// <summary>A payer's answer as recorded: on how many invoices, and the ones left out with why.</summary>
public sealed record ClaimOutcomeRecorded(int Updated, IReadOnlyList<Error> Skipped);

/// <summary>What happens to the part of the payer's share the payer did not accept.</summary>
public enum PayerShortfallHandling
{
    /// <summary>Left outstanding on the invoice, for the patient to settle at the counter.</summary>
    PatientPays = 0,

    /// <summary>Closed as a loss with a write-off line.</summary>
    WriteOff = 1
}

public sealed record RecordPaymentRequest(
    Guid InvoiceId,
    decimal Amount,
    PaymentMethod Method,
    string? Reference,
    string? BankName,
    DateOnly? MaturityDate,
    string? Notes);

/// <summary>
/// Invoicing, settlement and the till.
/// <para>
/// Three constraints from Moroccan tax law shape every method here, and none of them is a
/// preference:
/// </para>
/// <list type="number">
/// <item>The invoice number is allocated at <b>issue</b>, from a gapless per-site per-year
/// sequence, inside the same transaction as the document. An abandoned draft therefore leaves no
/// hole in the series.</item>
/// <item>An issued invoice is immutable. There is no code path back to draft; a correction is a
/// credit note that references the original.</item>
/// <item>The site's legal identifiers must be complete before anything can be issued, because an
/// invoice without a valid ICE is not a lawful invoice.</item>
/// </list>
/// </summary>
public sealed class BillingService(
    IRepository<Invoice> invoices,
    IRepository<LabOrder> orders,
    IRepository<Patient> patients,
    IRepository<Payer> payers,
    IRepository<LabSite> sites,
    IRepository<AppUser> users,
    IRepository<CashSession> cashSessions,
    IRepository<Deposit> deposits,
    IBillingQueries queries,
    ISequenceAllocator sequences,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock,
    IInvoicePrintSettingsQueries? settingsQueries = null,
    IAuthenticationService? authentication = null,
    ILicenceState? licence = null)
{
    // ---- Reading -------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<BillableOrderRow>>> GetUninvoicedOrdersAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<BillableOrderRow>>(permitted.FirstError);

        return Result.Success(
            await queries.GetUninvoicedOrdersAsync(currentUser.SiteId, ct));
    }

    public async Task<Result<PagedResult<InvoiceRow>>> GetInvoicesAsync(
        InvoiceStatus? status = null, string? search = null, int page = 1,
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.View);
        if (permitted.IsFailure) return Result.Failure<PagedResult<InvoiceRow>>(permitted.FirstError);

        return Result.Success(
            await queries.GetInvoicesAsync(currentUser.SiteId, status, search, page, 50,
                                            clock.Today, ct));
    }

    /// <summary>The ledger's count per invoice status, for the same search.</summary>
    public async Task<Result<IReadOnlyDictionary<InvoiceStatus, int>>> GetInvoiceStatusCountsAsync(
        string? search = null, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyDictionary<InvoiceStatus, int>>(permitted.FirstError);

        return Result.Success(await queries.GetInvoiceStatusCountsAsync(currentUser.SiteId, search, ct));
    }

    // ---- Issuing -------------------------------------------------------------------------------

    /// <summary>
    /// Creates and issues an invoice for one or more orders in a single transaction.
    /// <para>
    /// Draft and issue are not separate operations exposed to the UI: a draft that is never issued
    /// would still have had to be explained, and holding a number across two user actions is how a
    /// gap appears in the series. The lines are built from the order items' <b>frozen</b> prices,
    /// not by re-pricing — a tariff revision must not alter what a patient was told they owed.
    /// </para>
    /// <para>
    /// A discount above the ceiling set in Paramètres › Factures (<see cref="DiscountCapSettings"/>)
    /// is refused unless <paramref name="approval"/> names another active user holding
    /// <c>billing.discount.approve</c>, with their password; who approved, and the ceiling in force,
    /// go into the audit trail of the invoice.
    /// </para>
    /// </summary>
    public async Task<Result<Guid>> IssueInvoiceAsync(
        IReadOnlyList<Guid> labOrderIds, Guid? payerIdOverride = null,
        decimal? discountAmount = null, string? discountReason = null,
        DiscountApproval? approval = null,
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.CreateInvoice);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        if (labOrderIds.Count == 0)
            return Result.Failure<Guid>(Error.Validation("Invoice_NoLines"));

        if (discountAmount is > 0m)
        {
            var allowed = guard.Require(Permissions.Billing.ApplyDiscount);
            if (allowed.IsFailure) return Result.Failure<Guid>(allowed.FirstError);

            if (string.IsNullOrWhiteSpace(discountReason))
                return Result.Failure<Guid>(Error.Validation("Invoice_DiscountReasonRequired"));
        }

        var site = await sites.GetByIdAsync(currentUser.SiteId, ct);
        if (site is null) return Result.Failure<Guid>(Error.NotFound("Report_SiteNotFound"));

        // ICE, IF and address must be present. An invoice without them is not lawful, so this is
        // checked before a number is burned rather than after the document exists.
        var invoiceable = site.ValidateForInvoicing();
        if (invoiceable.IsFailure) return Result.Failure<Guid>(invoiceable.Errors);

        var loaded = new List<LabOrder>(labOrderIds.Count);

        foreach (var orderId in labOrderIds)
        {
            var order = await orders.GetByIdWithDetailsAsync(orderId, ct);

            if (order is null)
                return Result.Failure<Guid>(Error.NotFound("Order_NotFound", orderId));

            if (order.InvoiceId is not null)
                return Result.Failure<Guid>(Error.Conflict("Invoice_OrderAlreadyInvoiced",
                                                            order.AccessionNumber));

            // Listed at the billing desk only for the deposit it still holds, which is refunded:
            // its analyses are all cancelled, and the refusal said the invoice had no line.
            if (order.Status == LabOrderStatus.Cancelled)
                return Result.Failure<Guid>(Error.Conflict("Invoice_OrderCancelled", order.AccessionNumber));

            loaded.Add(order);
        }

        // Grouping several orders onto one invoice only makes sense for a single customer. A request
        // registered on a file since merged as a duplicate is the surviving file's: refused as
        // « several patients » next to the survivor's own, it could never be invoiced with them.
        var patientIds = new List<Guid>();
        foreach (var patientId in loaded.Select(o => o.PatientId).Distinct())
        {
            var survivorId = await SurvivorOfAsync(patientId, ct);
            if (!patientIds.Contains(survivorId)) patientIds.Add(survivorId);
        }

        if (patientIds.Count > 1)
            return Result.Failure<Guid>(Error.Validation("Invoice_MultiplePatients"));

        // Nor for orders registered with different payers, self-pay included: each order's split
        // was frozen at registration with its own payer, and one invoice has one payer. Grouped,
        // the others were re-split under the first one's payer — a self-paid act claimed from the
        // payer, or a covered act put entirely on the patient. They are invoiced separately.
        // An order the payer is billed for directly and one the patient paid in full are not
        // grouped either: one invoice is addressed to the payer, the other to the patient.
        if (loaded.Select(o => (o.PayerId, o.DirectBillingAtRegistration)).Distinct().Count() > 1)
            return Result.Failure<Guid>(Error.Validation(
                "Invoice_MultiplePayers",
                string.Join(", ", loaded.Select(o => o.AccessionNumber))));

        var patient = await patients.GetByIdWithDetailsAsync(patientIds[0], ct);
        if (patient is null) return Result.Failure<Guid>(Error.NotFound("Order_PatientNotFound"));

        var payerId = payerIdOverride ?? loaded[0].PayerId;
        var payer = payerId is { } id ? await payers.GetByIdAsync(id, ct) : null;

        var today = clock.Today;

        // Addressed to the payer only under an agreement in force: without one there is no claim
        // to carry its share, which sat as a receivable nobody would ever be asked for, on an
        // invoice the patient could no longer use for their own reimbursement.
        if (payerIdOverride is not null
            && payer is not null
            && !(payer.ThirdPartyPaymentAgreement && payer.IsAgreementValid(today)))
            return Result.Failure<Guid>(Error.Validation("Invoice_PayerAgreementNotValid", payer.Name.Fr));

        // An order registered without third-party payment (box unticked on the coverage, or the
        // agreement expired) had its whole total asked of the patient at the counter. Its invoice
        // is the patient's, with no payer share to claim: addressed to the payer, the same acts
        // were claimed by the laboratory and reimbursed again on the patient's own feuille de soins.
        if (payerIdOverride is null && !loaded[0].DirectBillingAtRegistration)
            payer = null;

        // The discount ceiling, checked before the transaction: a wrong approver password is
        // counted towards that user's lockout, and the count must survive the refusal.
        var cap = DiscountCapSettings.Default;
        ApproverIdentity? approver = null;

        if (discountAmount is { } asked and > 0m)
        {
            cap = await DiscountCapAsync(ct);

            // The gross the invoice will have: the same lines as below, at their frozen prices.
            var expectedGross = Money.Round(loaded
                .SelectMany(o => o.Items)
                .Where(i => !i.IsCancelled && i.ChargedPrice > 0m)
                .Sum(i => Money.Round(i.ChargedPrice)));

            if (cap.Exceeds(asked, expectedGross))
            {
                var approved = await ApproveDiscountAsync(approval, cap, expectedGross, ct);
                if (approved.IsFailure) return Result.Failure<Guid>(approved.Errors);
                approver = approved.Value;
            }
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var invoice = new Invoice(string.Empty, currentUser.SiteId, InvoiceKind.Invoice,
                                   today, currentUser.UserId)
        {
            PatientId = patient.Id,
            PayerId = payer?.Id,
            CoveredOrderNumbersCsv = string.Join(", ", loaded.Select(o => o.AccessionNumber)),
            DueDate = payer is null ? today : today.AddDays(payer.PaymentTermsDays)
        };

        // Billing identity frozen at issue: a later correction to the payer or patient record must
        // not change what this document says.
        if (payer is not null)
        {
            invoice.BilledToName = payer.Name.Fr;
            invoice.BilledToIce = payer.Ice;
            invoice.BilledToAddress = payer.AddressLine;
            invoice.BilledToCity = payer.City;
        }
        else
        {
            invoice.BilledToName = patient.FullName;
            invoice.BilledToAddress = patient.AddressLine;
            invoice.BilledToCity = patient.City;
        }

        invoice.PrescriberInpe = loaded[0].Prescriber?.Inpe;
        invoice.PrescriberName = loaded[0].Prescriber?.FullName ?? loaded[0].PrescriberFreeText;
        invoice.ClaimStatus = payer is { ThirdPartyPaymentAgreement: true }
                              && (payerIdOverride is null || payer.IsAgreementValid(today))
            ? ClaimStatus.Prepared
            : ClaimStatus.NotApplicable;

        // The executing biologist's INPE, frozen onto the document.
        //
        // This line was missing, and its absence was a showstopper rather than an omission:
        // SubmitClaimBatchAsync refuses a claim whose BiologistInpe is blank whenever the payer
        // requires one, and every AMO scheme seeded by this application does — CNSS, CNOPS, Tadamon
        // and Achamil alike. So no AMO claim could ever be submitted on any installation, which is
        // where a Moroccan laboratory's cash actually comes from.
        //
        // Taken from the site's responsible biologist rather than from whoever is at the counter: an
        // AMO claim identifies the biologist accountable for the acts, and that is a standing
        // appointment under loi 12-01, not whichever clerk issued the invoice.
        invoice.BiologistInpe = await ResolveBiologistInpeAsync(site, ct);

        foreach (var order in loaded)
        {
            // The split agreed when the order was registered, not the patient's coverage on the day
            // of invoicing: a coverage that lapsed before month-end invoicing would otherwise put the
            // whole bill on the patient, and a secondary mutuelle would be priced at the primary rate.
            var rate = FrozenReimbursementRate(order, patient, payer);

            foreach (var item in order.Items.Where(i => !i.IsCancelled && i.ChargedPrice > 0m))
            {
                var line = new InvoiceLine(
                    item.AnalysisTest?.Name.Fr ?? item.BilledActCode ?? "-",
                    item.ChargedPrice)
                {
                    DescriptionArabic = item.AnalysisTest?.Name.Ar,
                    LabOrderId = order.Id,
                    LabOrderItemId = item.Id,
                    AnalysisTestId = item.AnalysisTestId,
                    AnalysisCode = item.AnalysisTest?.Code,
                    NationalActCode = item.BilledActCode,
                    LetterKey = item.BilledLetterKey,
                    Coefficient = item.BilledCoefficient,
                    ReferenceTariffBase = item.ReferenceTariffBase,
                    ServiceDate = DateOnly.FromDateTime(order.RegisteredAt.Date),
                    // As charged at registration: medical biology acts are VAT-exempt in Morocco,
                    // and an act whose tariff carries VAT was told to the patient with it. The
                    // line used to be exempt whatever the tariff said, so the counter took the VAT
                    // the invoice, the VAT statement and the 4455 journal never showed.
                    IsVatExempt = item.IsVatExempt,
                    VatRate = item.IsVatExempt ? 0m : item.VatRate
                };

                var added = invoice.AddLine(line);
                if (added.IsFailure) return Result.Failure<Guid>(added.Errors);

                if (payer is not null && rate > 0m)
                    line.SetCoverageSplit(rate, payer.UsesNationalReferenceTariff);
            }
        }

        if (discountAmount is { } discount and > 0m)
        {
            // Again on the invoice's own gross, which is what the ceiling is a share of.
            if (approver is null && cap.Exceeds(discount, invoice.GrossAmount))
                return Result.Failure<Guid>(DiscountApprovalRequired(cap, invoice.GrossAmount));

            var applied = invoice.ApplyDiscount(discount, discountReason!);
            if (applied.IsFailure) return Result.Failure<Guid>(applied.Errors);
        }

        var number = await sequences.NextAsync(
            SequencePurpose.Invoice, currentUser.SiteId, today.Year, ct);

        if (number.IsFailure) return Result.Failure<Guid>(number.FirstError);

        var issued = invoice.Issue(number.Value, clock.Now,
                                    AmountInWords.Render(invoice.TotalAmount, currentUser.Language));

        if (issued.IsFailure) return Result.Failure<Guid>(issued.Errors);

        await invoices.AddAsync(invoice, ct);

        foreach (var order in loaded) order.InvoiceId = invoice.Id;

        var depositsApplied = await ApplyDepositsAsync(invoice, loaded.Select(o => o.Id).ToList(), ct);
        if (depositsApplied.IsFailure) return Result.Failure<Guid>(depositsApplied.Errors);

        unitOfWork.AuditReason = depositsApplied.Value > 0m
            ? $"Émission de la facture {invoice.Number}, acomptes imputés : {depositsApplied.Value:N2} DH"
            : $"Émission de la facture {invoice.Number}";

        if (approver is not null)
            unitOfWork.AuditReason += DescribeApproval(approver, cap, invoice.DiscountAmount, invoice.GrossAmount);

        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return Result.Success(invoice.Id);
    }

    private async Task<DiscountCapSettings> DiscountCapAsync(CancellationToken ct) =>
        settingsQueries is null
            ? DiscountCapSettings.Default
            : DiscountCapSettings.FromValues(await settingsQueries.GetSettingValuesAsync(DiscountCapSettings.Prefix, ct));

    /// <summary>
    /// The approval of a discount above the ceiling: another user, active, with the right to
    /// approve, who typed their password. The operator's own name is refused before anything is
    /// checked, whatever their rights.
    /// </summary>
    private async Task<Result<ApproverIdentity>> ApproveDiscountAsync(
        DiscountApproval? approval, DiscountCapSettings cap, decimal gross, CancellationToken ct)
    {
        if (approval is null || string.IsNullOrWhiteSpace(approval.UserName) || string.IsNullOrEmpty(approval.Password))
            return Result.Failure<ApproverIdentity>(DiscountApprovalRequired(cap, gross));

        if (string.Equals(approval.UserName.Trim(), currentUser.UserName, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<ApproverIdentity>(Error.Forbidden("Approval_SelfNotAllowed"));

        if (authentication is null)
            return Result.Failure<ApproverIdentity>(DiscountApprovalRequired(cap, gross));

        var verified = await authentication.VerifyApproverAsync(
            approval.UserName, approval.Password, Permissions.Billing.ApproveDiscount, ct);

        if (verified.IsFailure) return verified;

        if (verified.Value.UserId == currentUser.UserId)
            return Result.Failure<ApproverIdentity>(Error.Forbidden("Approval_SelfNotAllowed"));

        return verified;
    }

    private Error DiscountApprovalRequired(DiscountCapSettings cap, decimal gross) =>
        Error.Forbidden("Invoice_DiscountApprovalRequired", Money.Of(cap.CapFor(gross)).Format(currentUser.Language));

    /// <summary>What the audit trail says of an approved discount: who, when, how much, under which ceiling.</summary>
    private string DescribeApproval(ApproverIdentity approver, DiscountCapSettings cap, decimal discount, decimal gross)
    {
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var ceiling = cap.MaxAmount is { } max
            ? $"{cap.MaxPercent.ToString("0.##", fr)} % du brut, maximum {max.ToString("N2", fr)} DH"
            : $"{cap.MaxPercent.ToString("0.##", fr)} % du brut";

        return $" ; remise de {discount.ToString("N2", fr)} DH au-delà du plafond de "
               + $"{cap.CapFor(gross).ToString("N2", fr)} DH ({ceiling}) approuvée par "
               + $"{approver.FullName} ({approver.UserName}) le {clock.Now.ToString("dd/MM/yyyy HH:mm", fr)}";
    }

    /// <summary>
    /// The reimbursement rate to split an order's lines with when it is invoiced to
    /// <paramref name="payer"/>.
    /// <para>
    /// When the order is invoiced to the payer it was registered with, the rate frozen at
    /// registration applies, whatever has happened to the coverage since: that is the split the
    /// patient was told about and paid on at the counter. Only an order invoiced to another payer
    /// than the registered one (an override) has no snapshot to use; it then takes the patient's
    /// coverage with that payer as it stood on the day the order was registered.
    /// </para>
    /// </summary>
    public static decimal FrozenReimbursementRate(LabOrder order, Patient patient, Payer? payer)
    {
        if (payer is null) return 0m;

        // Registered with this payer: a null rate means the coverage gave nothing at registration.
        if (order.PayerId == payer.Id)
            return order.ReimbursementRateAtRegistration ?? 0m;

        var registeredOn = DateOnly.FromDateTime(order.RegisteredAt.Date);
        var coverage = patient.Coverages
            .Where(c => c.PayerId == payer.Id && c.IsValidOn(registeredOn))
            .OrderByDescending(c => c.IsPrimary)
            .ThenByDescending(c => c.ValidFrom)
            .FirstOrDefault();

        return coverage?.EffectiveRate(registeredOn, payer.DefaultReimbursementRate) ?? 0m;
    }

    /// <summary>
    /// Issues a credit note against an invoice.
    /// <para>
    /// The only lawful way to correct an issued invoice. It gets its own gapless series, references
    /// the original by number as well as by identifier, and requires a reason — which is the audit
    /// trail for the reversal and the first thing an inspection asks for.
    /// </para>
    /// </summary>
    public async Task<Result<Guid>> IssueCreditNoteAsync(
        Guid originalInvoiceId, string reason, decimal? partialAmount = null,
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.CreditNote);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        // The screens' lengths, held here too: the reason is printed on the credit note, and one
        // longer than its column failed at the database instead of being refused.
        if (ReasonRules.Problem(reason, BillingScreenRules.CreditNoteReasonMinimumLength,
                                "Invoice_CorrectionReasonRequired",
                                BillingScreenRules.CorrectionReasonMaxLength) is { } badReason)
            return Result.Failure<Guid>(badReason);

        var original = await invoices.GetByIdWithDetailsAsync(originalInvoiceId, ct);
        if (original is null) return Result.Failure<Guid>(Error.NotFound("Invoice_NotFound"));

        // Checked against what earlier credit notes already reversed, before a number is drawn:
        // together the credit notes never reverse more than the invoice charged. A full reversal
        // credits what is left.
        var creditable = original.CreditableAmount;
        var allowed = original.CanApplyCreditNote(partialAmount ?? creditable);
        if (allowed.IsFailure) return Result.Failure<Guid>(allowed.Errors);

        var today = clock.Today;

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var creditNote = new Invoice(string.Empty, original.SiteId, InvoiceKind.CreditNote,
                                      today, currentUser.UserId)
        {
            PatientId = original.PatientId,
            PayerId = original.PayerId,
            BilledToName = original.BilledToName,
            BilledToIce = original.BilledToIce,
            BilledToFiscalId = original.BilledToFiscalId,
            BilledToAddress = original.BilledToAddress,
            BilledToCity = original.BilledToCity,
            CorrectsInvoiceId = original.Id,
            CorrectsInvoiceNumber = original.Number,
            CorrectionReason = reason,
            CoveredOrderNumbersCsv = original.CoveredOrderNumbersCsv
        };

        if (partialAmount is { } partial)
        {
            // A partial reversal is one line for the agreed amount, described by its reason.
            var added = creditNote.AddLine(new InvoiceLine(LineDescription($"Avoir partiel — {reason}"), partial));
            if (added.IsFailure) return Result.Failure<Guid>(added.Errors);
        }
        else if (original.CreditedAmount > 0m)
        {
            // Part of the invoice was already reversed: mirroring every line again would credit it
            // twice. The balance is credited as one line.
            var added = creditNote.AddLine(
                new InvoiceLine(LineDescription($"Avoir du solde de la facture {original.Number} — {reason}"), creditable));
            if (added.IsFailure) return Result.Failure<Guid>(added.Errors);
        }
        else
        {
            // A full reversal mirrors every line, and the original's discount with them, so the two
            // documents reconcile line by line and to the dirham.
            var mirrored = creditNote.MirrorLinesOf(original);
            if (mirrored.IsFailure) return Result.Failure<Guid>(mirrored.Errors);
        }

        // Refused here, before the number is drawn, should the mirrored lines not reconcile.
        var fits = original.CanApplyCreditNote(creditNote.TotalAmount);
        if (fits.IsFailure) return Result.Failure<Guid>(fits.Errors);

        var number = await sequences.NextAsync(
            SequencePurpose.CreditNote, original.SiteId, today.Year, ct);

        if (number.IsFailure) return Result.Failure<Guid>(number.FirstError);

        var issued = creditNote.Issue(number.Value, clock.Now,
            AmountInWords.Render(creditNote.TotalAmount, currentUser.Language));

        if (issued.IsFailure) return Result.Failure<Guid>(issued.Errors);

        await invoices.AddAsync(creditNote, ct);

        var applied = original.ApplyCreditNote(creditNote.TotalAmount);
        if (applied.IsFailure) return Result.Failure<Guid>(applied.Errors);

        // Wholly reversed, the invoice no longer bills its orders: they are freed, so they come
        // back to the billing desk and the correct invoice can be issued — to the patient, or to
        // the payer when the first one was addressed wrongly. Left attached to the reversed
        // invoice, they were refused as already invoiced and their acts stayed on no valid invoice.
        // Deposits still held follow the order and are applied to the new invoice. What the
        // patient had paid on the reversed invoice follows it too, carried over as deposits (see
        // CarryPaymentsOverAsync).
        var released = new List<string>();
        var releasedOrders = new List<LabOrder>();
        var carried = 0m;

        if (original.IsFullyCredited)
        {
            foreach (var listed in await orders.ListAsync(o => o.InvoiceId == original.Id, ct))
            {
                // Re-read through the repository, which refreshes an order this screen still tracks
                // from the invoice's issue: a list hands it back as it was then, and saving it over
                // a later change (results validated since) failed as a concurrency conflict.
                var order = await orders.GetByIdWithDetailsAsync(listed.Id, ct);
                if (order is null) continue;

                order.InvoiceId = null;
                orders.Update(order);
                released.Add(order.AccessionNumber);
                releasedOrders.Add(order);
            }

            var carriedOver = await CarryPaymentsOverAsync(original, releasedOrders, ct);
            if (carriedOver.IsFailure) return Result.Failure<Guid>(carriedOver.Errors);
            carried = carriedOver.Value;
        }

        unitOfWork.AuditReason =
            $"Avoir {creditNote.Number} sur la facture {original.Number} : {reason}"
            + (released.Count > 0 ? PluralText.French(" — {0:plural:dossier|dossiers} à refacturer : {1}", released.Count, string.Join(", ", released)) : string.Empty)
            + (carried > 0m ? $" — {carried:N2} DH déjà réglés reportés en acompte" : string.Empty);

        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return Result.Success(creditNote.Id);
    }

    /// <summary>
    /// Carries what the patient had paid on a wholly credited invoice over to the orders it billed,
    /// as deposits held on them, and returns the amount carried.
    /// <para>
    /// Left on the reversed invoice, the money was accounted for nowhere: the invoice read « Réglée »
    /// with nothing owed, no refund was asked for, and the invoice that replaced it asked the
    /// patient for their whole share again. As deposits, the next invoice of the order takes them
    /// as settlements, so it asks only for the remainder; an order not invoiced again has its
    /// deposit refunded like any other. Each payment keeps its method, so a refund goes back the
    /// way the money came. Spread over the orders up to what each owes the patient, the last one
    /// taking any remainder.
    /// </para>
    /// <para>
    /// The payments stay on the reversed invoice as they were made: the receipts and the till are
    /// unchanged. The deposit is no new money: it is not counted in a till, and the accounts move
    /// it from the patient's account to 4421, which leaves that account balanced.
    /// </para>
    /// </summary>
    private async Task<Result<decimal>> CarryPaymentsOverAsync(Invoice original, IReadOnlyList<LabOrder> released,
                                                               CancellationToken ct)
    {
        var paid = original.StandingPatientPayments().OrderBy(p => p.PaymentDate).ToList();
        if (paid.Count == 0 || released.Count == 0) return Result.Success(0m);

        // What each order would ask the patient on its next invoice: the patient's share when the
        // payer is billed directly, the whole order otherwise.
        var room = released
            .Select(o => Money.Round(o.Items.Where(i => !i.IsCancelled)
                                     .Sum(i => o.PayerId is not null && o.DirectBillingAtRegistration
                                                   ? i.PatientShare
                                                   : i.PatientShare + i.PayerShare)))
            .ToArray();

        var carried = 0m;

        foreach (var payment in paid)
        {
            var left = payment.Amount;

            for (var k = 0; k < released.Count && left > 0m; k++)
            {
                var take = k == released.Count - 1 ? left : Math.Min(left, Math.Max(0m, room[k]));
                if (take <= 0m) continue;

                var number = await sequences.NextAsync(SequencePurpose.Receipt, original.SiteId,
                                                       clock.Today.Year, ct);
                if (number.IsFailure) return Result.Failure<decimal>(number.FirstError);

                var order = released[k];
                var deposit = Deposit.CarriedOver(number.Value, original.SiteId, order.Id, order.PatientId, payment,
                                                  take, original.Id, original.Number, clock.Now, currentUser.UserId);
                await deposits.AddAsync(deposit, ct);

                room[k] = Money.Round(room[k] - take);
                left = Money.Round(left - take);
                carried = Money.Round(carried + take);
            }
        }

        return Result.Success(carried);
    }

    /// <summary>
    /// Whether a payment of <paramref name="invoice"/> was carried over to its orders by a credit
    /// note: it then lives on as their deposit, and is refunded from there — refunded here as well,
    /// or declared unpaid here, the money would leave twice.
    /// </summary>
    private async Task<Result> RefuseIfCarriedOverAsync(Invoice invoice, Payment payment, CancellationToken ct)
    {
        if (!invoice.IsFullyCredited || !PaymentRow.IsPatientPayment(payment.Method)) return Result.Success();

        var carried = await deposits.ListAsync(d => d.CarriedFromInvoiceId == invoice.Id, ct);
        return carried.Count > 0
            ? Result.Failure(Error.Conflict("Payment_CarriedToDeposit", invoice.Number))
            : Result.Success();
    }

    /// <summary>
    /// A credit note's line quotes its reason, which may run to 500 characters while a line holds
    /// 300: the longer reasons failed the credit note with an unexpected error. The line is cut;
    /// the reason stays whole on the credit note itself.
    /// </summary>
    private static string LineDescription(string text) =>
        text.Length <= InvoiceLineDescriptionMaxLength ? text : string.Concat(text.AsSpan(0, InvoiceLineDescriptionMaxLength - 1), "…");

    private const int InvoiceLineDescriptionMaxLength = 300;

    // ---- Deposits -------------------------------------------------------------------------------

    /// <summary>
    /// Takes an advance on a request that is not invoiced yet. Numbered in the receipt series and
    /// counted into the till now; it becomes a settlement on the invoice when the invoice is issued.
    /// </summary>
    public async Task<Result<string>> TakeDepositAsync(TakeDepositRequest request,
                                                       CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.RecordPayment);
        if (permitted.IsFailure) return Result.Failure<string>(permitted.FirstError);

        var order = await orders.GetByIdWithDetailsAsync(request.LabOrderId, ct);
        if (order is null) return Result.Failure<string>(Error.NotFound("Order_NotFound", request.LabOrderId));

        if (order.InvoiceId is not null)
            return Result.Failure<string>(Error.Conflict("Invoice_OrderAlreadyInvoiced", order.AccessionNumber));

        if (order.Status == LabOrderStatus.Cancelled)
            return Result.Failure<string>(Error.Conflict("Deposit_OrderCancelled", order.AccessionNumber));

        // Never more than the patient will owe, deposits already held included: an overpayment
        // would only have to be handed back. Under direct billing the payer's share is claimed
        // from the payer, so a deposit on it could never be applied to the invoice.
        var live = order.Items.Where(i => !i.IsCancelled).ToList();
        var total = order.PayerId is not null && order.DirectBillingAtRegistration
            ? live.Sum(i => i.PatientShare)
            : live.Sum(i => i.ChargedPrice);
        var held = (await deposits.ListAsync(d => d.LabOrderId == order.Id
                                                  && d.Status == DepositStatus.Held, ct))
                   .Sum(d => d.Remaining);

        if (Money.Round(held + request.Amount) > Money.Round(total))
            return Result.Failure<string>(Error.Validation("Deposit_ExceedsOrderTotal",
                                                           Money.Round(total - held)));

        // Every method goes on the operator's shift report: cash for the reconciliation, cheques,
        // cards and transfers for the takings. Only cash requires an open till.
        var (session, elsewhere) = await TillAsync(ct);

        if (elsewhere is not null && request.Method == PaymentMethod.Cash)
            return Result.Failure<string>(TillOpenElsewhere());

        if (session is null && request.Method == PaymentMethod.Cash)
            return Result.Failure<string>(Error.Validation("Payment_CashRequiresOpenSession"));

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var number = await sequences.NextAsync(SequencePurpose.Receipt, currentUser.SiteId,
                                               clock.Today.Year, ct);
        if (number.IsFailure)
        {
            await unitOfWork.RollbackAsync(ct);
            return Result.Failure<string>(number.FirstError);
        }

        var deposit = new Deposit(number.Value, currentUser.SiteId, order.Id, order.PatientId,
                                  request.Amount, request.Method, clock.Now, currentUser.UserId)
        {
            Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim(),
            BankName = string.IsNullOrWhiteSpace(request.BankName) ? null : request.BankName.Trim(),
            MaturityDate = request.MaturityDate,
            CashSessionId = session?.Id
        };

        var valid = deposit.Validate();
        if (valid.IsFailure)
        {
            await unitOfWork.RollbackAsync(ct);
            return Result.Failure<string>(valid.Errors);
        }

        await deposits.AddAsync(deposit, ct);
        session?.Accumulate(request.Method, request.Amount);

        unitOfWork.AuditReason =
            $"Acompte {deposit.Number} de {deposit.Amount:N2} DH ({deposit.Method}) sur {order.AccessionNumber}";

        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return Result.Success(deposit.Number);
    }

    public async Task<Result<IReadOnlyList<DepositRow>>> GetDepositsAsync(Guid labOrderId,
                                                                          CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<DepositRow>>(permitted.FirstError);

        var rows = await deposits.ListAsync(d => d.LabOrderId == labOrderId, ct);

        return Result.Success<IReadOnlyList<DepositRow>>(
        [
            .. rows.OrderBy(d => d.ReceivedAt)
                   .Select(d => new DepositRow(d.Id, d.Number, d.ReceivedAt, d.Amount, d.Remaining,
                                               d.Method, d.Status))
        ]);
    }

    /// <summary>
    /// Hands back what is still held on a deposit — the request was cancelled, or the invoice came
    /// to less. Cash goes out of the operator's open till; a cheque, card or transfer comes off the
    /// matching line of the shift that took it, while that shift is open.
    /// </summary>
    public async Task<Result> RefundDepositAsync(Guid depositId, string reason,
                                                 CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.CreditNote);
        if (permitted.IsFailure) return permitted;

        var deposit = await deposits.GetByIdAsync(depositId, ct);
        if (deposit is null) return Result.Failure(Error.NotFound("Deposit_NotFound"));

        var amount = deposit.Remaining;

        var (session, elsewhere) = await TillAsync(ct);

        if (elsewhere is not null && deposit.Method == PaymentMethod.Cash)
            return Result.Failure(TillOpenElsewhere());

        if (session is null && deposit.Method == PaymentMethod.Cash)
            return Result.Failure(Error.Validation("Payment_CashRequiresOpenSession"));

        if (deposit.Method == PaymentMethod.Cash && session is not null
            && Money.Round(amount) > session.ExpectedClosingCash)
            return Result.Failure(Error.Validation("CashSession_RefundExceedsCash",
                                                   session.ExpectedClosingCash));

        var refunded = deposit.Refund(clock.Now, reason);
        if (refunded.IsFailure) return refunded;

        if (deposit.Method == PaymentMethod.Cash && session is not null)
        {
            session.RecordCashRefund(amount);
            deposit.RefundSiteId = session.SiteId;
        }
        else await ReverseOnTakingSessionAsync(deposit.CashSessionId, deposit.Method, amount, ct);
        deposits.Update(deposit);

        unitOfWork.AuditReason = $"Remboursement de l'acompte {deposit.Number} : {amount:N2} DH — {reason}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Turns the deposits held on the invoiced orders into settlements on the new invoice, up to
    /// what the patient owes on it. Under a direct-billing agreement the payer's share is the
    /// payer's to settle, so a deposit never covers it. What is not absorbed stays held, to be
    /// refunded. Returns the amount applied.
    /// </summary>
    private async Task<Result<decimal>> ApplyDepositsAsync(Invoice invoice, IReadOnlyList<Guid> orderIds,
                                                           CancellationToken ct)
    {
        var held = await deposits.ListAsync(d => orderIds.Contains(d.LabOrderId)
                                                 && d.Status == DepositStatus.Held, ct);
        if (held.Count == 0) return Result.Success(0m);

        var payerPaysDirectly = invoice.ClaimStatus == ClaimStatus.Prepared;
        var room = Money.Round(invoice.OutstandingAmount
                               - (payerPaysDirectly ? invoice.PayerShareAmount : 0m));
        var applied = 0m;

        foreach (var deposit in held.OrderBy(d => d.ReceivedAt))
        {
            if (room <= 0m) break;

            var amount = Math.Min(deposit.Remaining, room);
            if (amount <= 0m) continue;

            var payment = new Payment(amount, deposit.Method, DateOnly.FromDateTime(deposit.ReceivedAt.Date),
                                      deposit.ReceivedByUserId, invoice.SiteId)
            {
                Reference = deposit.Reference ?? deposit.Number,
                BankName = deposit.BankName,
                MaturityDate = deposit.MaturityDate,
                CashSessionId = deposit.CashSessionId,
                DepositId = deposit.Id,
                Notes = $"Acompte {deposit.Number}"
            };

            // Not accumulated into a till: the money entered the till when the deposit was taken.
            var recorded = invoice.RecordPayment(payment);
            if (recorded.IsFailure) return Result.Failure<decimal>(recorded.Errors);

            var marked = deposit.ApplyTo(invoice.Id, amount, clock.Now);
            if (marked.IsFailure) return Result.Failure<decimal>(marked.Errors);

            deposits.Update(deposit);

            room = Money.Round(room - amount);
            applied = Money.Round(applied + amount);
        }

        return Result.Success(applied);
    }

    // ---- Settlement -----------------------------------------------------------------------------

    /// <summary>
    /// Records a settlement. Cash is bound to the operator's open till session, so the shift can be
    /// reconciled; a cash payment with no open session is refused by the domain.
    /// </summary>
    public async Task<Result> RecordPaymentAsync(RecordPaymentRequest request,
                                                  CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.RecordPayment);
        if (permitted.IsFailure) return permitted;

        // Only what the counter takes. A write-off or a payer's settlement closed the receivable
        // with no money in the till, on the cashier's right alone; each has its own guarded action.
        if (!BillingScreenRules.CounterMethods.Contains(request.Method))
            return Result.Failure(Error.Validation("Payment_MethodNotAllowed"));

        var invoice = await invoices.GetByIdWithDetailsAsync(request.InvoiceId, ct);
        if (invoice is null) return Result.Failure(Error.NotFound("Invoice_NotFound"));

        // Looked up for every method, not only cash: the cheque, card and transfer lines of the
        // shift report read the session's counters, and stayed at zero while only cash reached them.
        // Only cash requires an open till.
        var (session, elsewhere) = await TillAsync(ct);

        if (elsewhere is not null && request.Method == PaymentMethod.Cash)
            return Result.Failure(TillOpenElsewhere());

        if (session is null && request.Method == PaymentMethod.Cash)
            return Result.Failure(Error.Validation("Payment_CashRequiresOpenSession"));

        var payment = new Payment(request.Amount, request.Method, clock.Today,
                                   currentUser.UserId, currentUser.SiteId)
        {
            Reference = request.Reference,
            BankName = request.BankName,
            MaturityDate = request.MaturityDate,
            CashSessionId = session?.Id,
            Notes = request.Notes
        };

        var valid = payment.Validate();
        if (valid.IsFailure) return valid;

        var recorded = invoice.RecordPayment(payment);
        if (recorded.IsFailure) return recorded;

        session?.Accumulate(request.Method, request.Amount);

        unitOfWork.AuditReason =
            $"Règlement de {request.Amount:N2} DH ({request.Method}) sur {invoice.Number}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// The settlements recorded against an invoice, so a correction can be pointed at one of them.
    /// </summary>
    public async Task<Result<IReadOnlyList<PaymentRow>>> GetPaymentsAsync(
        Guid invoiceId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<PaymentRow>>(permitted.FirstError);

        return Result.Success(await queries.GetPaymentsAsync(invoiceId, ct));
    }

    /// <summary>
    /// The printable content of an invoice and of its payment receipt.
    /// <para>
    /// Guarded like the payment list: anyone who may see the ledger may reprint what is in it.
    /// A draft has no number and is not a document anybody can be handed, so it is refused.
    /// </para>
    /// </summary>
    public async Task<Result<InvoiceDocument>> GetInvoiceDocumentAsync(
        Guid invoiceId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.View);
        if (permitted.IsFailure) return Result.Failure<InvoiceDocument>(permitted.FirstError);

        var document = await queries.GetInvoiceDocumentAsync(invoiceId, ct);
        if (document is null) return Result.Failure<InvoiceDocument>(Error.NotFound("Invoice_NotFound"));

        if (document.Status == InvoiceStatus.Draft)
            return Result.Failure<InvoiceDocument>(Error.Conflict("Invoice_NotIssued"));

        return Result.Success(document);
    }

    /// <summary>
    /// Marks a cheque or a bill of exchange as returned unpaid.
    /// <para>
    /// The bank telling the laboratory a cheque bounced is an everyday event, and until this existed
    /// the only way to reflect it was a credit note — which says the laboratory withdrew the charge,
    /// not that the patient's payment failed. The two are different things to an accountant and to
    /// the patient, who still owes the money.
    /// </para>
    /// <para>
    /// The invoice's paid total is recomputed, so the balance goes back to outstanding and the
    /// receivables ageing picks it up again.
    /// </para>
    /// </summary>
    public async Task<Result> MarkPaymentBouncedAsync(Guid invoiceId, Guid paymentId, string reason,
                                                       CancellationToken ct = default)
    {
        // The accounts office's decision (Comptabilité, Administrateur): the counter takes the
        // cheque, but declaring it unpaid reopens the receivable and reverses the bank line.
        var permitted = guard.Require(Permissions.Billing.DeclareUnpaid);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Payment_BounceReasonRequired"));

        var invoice = await invoices.GetByIdWithDetailsAsync(invoiceId, ct);
        if (invoice is null) return Result.Failure(Error.NotFound("Invoice_NotFound"));

        var payment = invoice.Payments.FirstOrDefault(p => p.Id == paymentId);
        if (payment is null) return Result.Failure(Error.NotFound("Payment_NotFound"));

        var notCarried = await RefuseIfCarriedOverAsync(invoice, payment, ct);
        if (notCarried.IsFailure) return notCarried;

        var bounced = payment.MarkBounced(clock.Now, reason);
        if (bounced.IsFailure) return bounced;

        // A cheque returned while the shift it was taken in is still open never leaves with that
        // shift's remittance: it comes off its cheque line. Once the shift is closed, the bank
        // remittance was made with it and the bounce is the receivable's business only. A deposit
        // applied to the invoice carries the shift the deposit was taken in.
        if (payment.CashSessionId is { } sessionId
            && await cashSessions.GetByIdAsync(sessionId, ct) is { Status: CashSessionStatus.Open } taken)
            taken.ReverseNonCash(payment.Method, payment.Amount);

        invoice.Recalculate();

        unitOfWork.AuditReason =
            $"Impayé de {payment.Amount:N2} DH ({payment.Method}) sur {invoice.Number} : {reason}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Refunds a settled payment.
    /// <para>
    /// A cash refund also has to reach the till: the shift's expected cash is the float plus what
    /// came in minus what went out, and a refund paid from the drawer that the session never heard
    /// about shows up at closing as an unexplained shortfall. A cheque, card or transfer refund
    /// comes off the matching line of the shift that took it, while that shift is open, so the card line
    /// still agrees with the terminal's batch.
    /// </para>
    /// </summary>
    public async Task<Result> RefundPaymentAsync(Guid invoiceId, Guid paymentId, string reason,
                                                  CancellationToken ct = default)
    {
        // Giving money back is the credit-note holder's decision, not every cashier's.
        var permitted = guard.Require(Permissions.Billing.CreditNote);
        if (permitted.IsFailure) return permitted;

        var invoice = await invoices.GetByIdWithDetailsAsync(invoiceId, ct);
        if (invoice is null) return Result.Failure(Error.NotFound("Invoice_NotFound"));

        var payment = invoice.Payments.FirstOrDefault(p => p.Id == paymentId);
        if (payment is null) return Result.Failure(Error.NotFound("Payment_NotFound"));

        if (!PaymentRow.IsPatientPayment(payment.Method))
            return Result.Failure(Error.Validation("Payment_OnlyPatientPaymentsRefundable"));

        var notCarried = await RefuseIfCarriedOverAsync(invoice, payment, ct);
        if (notCarried.IsFailure) return notCarried;

        // The till is checked before the payment is touched. The refund used to be applied first,
        // so a refusal here left the tracked payment Refunded in a long-lived context, and the next
        // unrelated save wrote the refund to the database without any money leaving the drawer.
        var (session, elsewhere) = await TillAsync(ct);

        if (elsewhere is not null && payment.Method == PaymentMethod.Cash)
            return Result.Failure(TillOpenElsewhere());

        if (session is null && payment.Method == PaymentMethod.Cash)
            return Result.Failure(Error.Validation("Payment_CashRefundRequiresOpenSession"));

        // Never more than the drawer holds, as for a withdrawal: the money cannot leave a till that
        // does not have it, and the expected cash went negative and closed on a false surplus.
        if (payment.Method == PaymentMethod.Cash && session is not null
            && Money.Round(payment.Amount) > session.ExpectedClosingCash)
            return Result.Failure(Error.Validation("CashSession_RefundExceedsCash",
                                                   session.ExpectedClosingCash));

        var refunded = payment.Refund(clock.Now, reason);
        if (refunded.IsFailure) return refunded;

        if (payment.Method == PaymentMethod.Cash && session is not null)
        {
            session.RecordCashRefund(payment.Amount);
            // The cash leaves the refunder's drawer, which may be another site's: its cash account pays it.
            payment.RefundSiteId = session.SiteId;
        }
        else await ReverseOnTakingSessionAsync(payment.CashSessionId, payment.Method, payment.Amount, ct);

        invoice.Recalculate();

        unitOfWork.AuditReason =
            $"Remboursement de {payment.Amount:N2} DH ({payment.Method}) sur {invoice.Number} : {reason}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Takes a refunded cheque, card or transfer off the line of the shift that took it, while that
    /// shift is still open — not off the refunder's own shift, which never received it. A closed
    /// shift was remitted as it stood and is not touched, as for a bounced cheque.
    /// </summary>
    private async Task ReverseOnTakingSessionAsync(Guid? sessionId, PaymentMethod method, decimal amount,
                                                   CancellationToken ct)
    {
        if (sessionId is { } id
            && await cashSessions.GetByIdAsync(id, ct) is { Status: CashSessionStatus.Open } taken)
            taken.ReverseNonCash(method, amount);
    }

    /// <summary>
    /// Records cash taken out of the till mid-shift and banked.
    /// <para>
    /// A laboratory counter does not hold a day's takings in the drawer. Without this the expected
    /// cash at closing includes money that is already at the bank, and every shift that banked
    /// anything closes on a large unexplained variance.
    /// </para>
    /// </summary>
    public async Task<Result> RecordCashWithdrawalAsync(decimal amount, string reason,
                                                         CancellationToken ct = default)
    {
        // Taking cash out of the drawer belongs with closing it, not with taking payments.
        var permitted = guard.Require(Permissions.Billing.CloseCashSession);
        if (permitted.IsFailure) return permitted;

        if (amount <= 0m) return Result.Failure(Error.Validation("Payment_AmountMustBePositive"));

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Payment_WithdrawalReasonRequired"));

        var (session, elsewhere) = await TillAsync(ct);
        if (elsewhere is not null) return Result.Failure(TillOpenElsewhere());
        if (session is null) return Result.Failure(Error.Conflict("CashSession_NotOpen"));

        // Never more than the drawer should hold: a mistyped 5 000 for 500 left the expected cash
        // negative, and the shift closed on a surplus as large as the typing error.
        if (Money.Round(amount) > session.ExpectedClosingCash)
            return Result.Failure(Error.Validation("CashSession_WithdrawalExceedsCash",
                                                   session.ExpectedClosingCash));

        session.RecordWithdrawal(amount, clock.Now, reason);

        unitOfWork.AuditReason = $"Prélèvement de caisse de {amount:N2} DH : {reason}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Records what the payer answered on a submitted claim.
    /// <para>
    /// Until this existed <c>ClaimStatus</c> froze at <c>Submitted</c> for ever: the laboratory
    /// could send a claim and never record that it was paid, short-paid or refused. Since AMO
    /// reimbursement is where most of the revenue comes from, that made the receivables figure
    /// meaningless — everything submitted stayed outstanding on the books regardless of what the
    /// payer actually did.
    /// </para>
    /// </summary>
    public async Task<Result<ClaimOutcomeRecorded>> RecordClaimOutcomeAsync(
        IReadOnlyList<Guid> invoiceIds, ClaimStatus outcome, string? rejectionReason = null,
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.ExportAccounting);
        if (permitted.IsFailure) return Result.Failure<ClaimOutcomeRecorded>(permitted.FirstError);

        if (invoiceIds.Count == 0)
            return Result.Failure<ClaimOutcomeRecorded>(Error.Validation("Billing.SelectOneInvoice"));

        // Settled is refused: this records a status, not money, so an invoice marked settled here
        // still owed the payer's share. It is set by RecordPayerSettlementAsync with the amount.
        if (outcome == ClaimStatus.Settled)
            return Result.Failure<ClaimOutcomeRecorded>(
                Error.Validation("Claim_SettledRecordedPerInvoice"));

        if (outcome is not (ClaimStatus.Accepted or ClaimStatus.PartiallyAccepted
                         or ClaimStatus.Rejected or ClaimStatus.Resubmitted))
            return Result.Failure<ClaimOutcomeRecorded>(
                Error.Validation("Claim_OutcomeNotRecordable", outcome));

        // A refusal without its reason is unusable: the reason is what decides whether the claim
        // is corrected and resubmitted or written off, and the payer only states it once.
        if (outcome is ClaimStatus.Rejected or ClaimStatus.PartiallyAccepted
            && string.IsNullOrWhiteSpace(rejectionReason))
            return Result.Failure<ClaimOutcomeRecorded>(Error.Validation("Claim_RejectionReasonRequired"));

        var errors = new List<Error>();
        var updated = 0;

        foreach (var invoiceId in invoiceIds.Distinct())
        {
            var invoice = await invoices.GetByIdAsync(invoiceId, ct);

            if (invoice is null)
            {
                errors.Add(Error.NotFound("Invoice_NotFound"));
                continue;
            }

            if (invoice.ClaimStatus is ClaimStatus.NotApplicable or ClaimStatus.Prepared)
            {
                errors.Add(Error.Validation("Claim_NotSubmitted", invoice.Number));
                continue;
            }

            // Its own settlement is already recorded, payment and write-off included: the payer
            // answered for this invoice, and a batch-level answer does not override it. Overwritten,
            // a paid invoice showed as refused, and resending it reopened the paid receivable.
            if (invoice.PayerAcceptedAmount is not null)
            {
                errors.Add(Error.Conflict("Claim_OutcomeSkippedSettled", invoice.Number));
                continue;
            }

            invoice.ClaimStatus = outcome;

            invoice.ClaimRejectionReason =
                outcome is ClaimStatus.Rejected or ClaimStatus.PartiallyAccepted
                    ? rejectionReason
                    : null;

            invoices.Update(invoice);
            updated++;
        }

        if (updated == 0) return Result.Failure<ClaimOutcomeRecorded>(errors.Count > 0
            ? errors
            : [Error.Validation("Billing.SelectOneInvoice")]);

        unitOfWork.AuditReason = PluralText.French("Retour tiers payant {0} sur {1} {1:plural:facture|factures}", outcome, updated)
                                 + (rejectionReason is null ? string.Empty : $" : {rejectionReason}");

        await unitOfWork.SaveChangesAsync(ct);

        // Saved: a success, with the invoices left out and why, not a failure the screen would show
        // as "nothing recorded".
        return Result.Success(new ClaimOutcomeRecorded(updated, errors));
    }

    /// <summary>
    /// Records the amount the payer accepted on one invoice's payer share, and what becomes of the
    /// rest.
    /// <para>
    /// The accepted amount is entered as the payer's settlement. The shortfall is either left on the
    /// invoice for the patient to pay at the counter, or written off with its reason; the claim
    /// status follows: settled, partly accepted or rejected.
    /// </para>
    /// </summary>
    public async Task<Result> RecordPayerSettlementAsync(Guid invoiceId, decimal acceptedAmount,
                                                         PayerShortfallHandling shortfall,
                                                         string? reason, string? remittanceReference,
                                                         CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.ExportAccounting);
        if (permitted.IsFailure) return permitted;

        var invoice = await invoices.GetByIdWithDetailsAsync(invoiceId, ct);
        if (invoice is null) return Result.Failure(Error.NotFound("Invoice_NotFound"));

        if (invoice.ClaimStatus is ClaimStatus.NotApplicable or ClaimStatus.Prepared)
            return Result.Failure(Error.Validation("Claim_NotSubmitted", invoice.Number));

        if (invoice.PayerAcceptedAmount is not null)
            return Result.Failure(Error.Conflict("Claim_SettlementAlreadyRecorded", invoice.Number));

        var accepted = Money.Round(acceptedAmount);

        // Against what the payer still owes: a credit note already took its part off the share.
        if (accepted < 0m || accepted > invoice.PayerDueAmount)
            return Result.Failure(Error.Validation("Claim_AcceptedAmountInvalid",
                                                   invoice.PayerDueAmount));

        // What the payer refused: its share less what it accepted and what a credit note already took off it.
        var missing = Math.Max(0m, Money.Round(invoice.PayerDueAmount - accepted));

        if (missing > 0m && string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Claim_RejectionReasonRequired"));

        var today = clock.Today;

        if (accepted > 0m)
        {
            var settlement = new Payment(accepted, PaymentMethod.ThirdPartyPayer, today,
                                         currentUser.UserId, invoice.SiteId)
            {
                RemittanceReference = string.IsNullOrWhiteSpace(remittanceReference)
                    ? null : remittanceReference.Trim(),
                Notes = "Règlement tiers payant"
            };

            var paid = invoice.RecordPayment(settlement);
            if (paid.IsFailure) return paid;
        }

        if (missing > 0m && shortfall == PayerShortfallHandling.WriteOff)
        {
            // Never more than what is still owed: the patient may already have paid part of it.
            var loss = Math.Min(missing, invoice.OutstandingAmount);

            if (loss > 0m)
            {
                var writeOff = new Payment(loss, PaymentMethod.WriteOff, today, currentUser.UserId,
                                           invoice.SiteId)
                {
                    Notes = $"Perte sur refus du tiers payant : {reason}"
                };

                var written = invoice.RecordPayment(writeOff);
                if (written.IsFailure) return written;
            }
        }

        // Dated today: the share left to the patient moves in the accounts on the day of the answer.
        invoice.RecordPayerAnswer(accepted, today);
        invoice.ClaimStatus = missing == 0m
            ? ClaimStatus.Settled
            : accepted == 0m ? ClaimStatus.Rejected : ClaimStatus.PartiallyAccepted;
        invoice.ClaimRejectionReason = missing > 0m ? reason!.Trim() : null;

        invoices.Update(invoice);

        unitOfWork.AuditReason =
            $"Tiers payant sur {invoice.Number} : {accepted:N2} DH acceptés sur {invoice.PayerShareAmount:N2} DH" +
            (missing > 0m
                ? shortfall == PayerShortfallHandling.WriteOff
                    ? $", {missing:N2} DH passés en perte — {reason}"
                    : $", {missing:N2} DH laissés à la charge du patient — {reason}"
                : string.Empty);

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    // ---- Till -----------------------------------------------------------------------------------

    /// <summary>
    /// The operator's open till at the site they work on now, and a till of theirs still open at
    /// another site. Money taken here never goes into a drawer left open there: its receipts were
    /// booked to this site's cash account while its count, withdrawals and variance were the other
    /// site's, and both cash accounts drifted.
    /// </summary>
    private async Task<(CashSession? Here, CashSession? Elsewhere)> TillAsync(CancellationToken ct)
    {
        var open = await queries.GetOpenCashSessionAsync(currentUser.UserId, ct);

        return open is null ? (null, null)
             : open.SiteId == currentUser.SiteId ? (open, null)
             : (null, open);
    }

    private static Error TillOpenElsewhere() => Error.Conflict("CashSession_OpenOnAnotherSite");

    public async Task<Result<Guid>> OpenCashSessionAsync(decimal openingFloat,
                                                          CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.RecordPayment);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        // The screens refuse a negative float; so does the service, whoever calls it.
        if (openingFloat < 0m)
            return Result.Failure<Guid>(Error.Validation("Payment_AmountMustBePositive"));

        var (existing, elsewhere) = await TillAsync(ct);

        if (elsewhere is not null) return Result.Failure<Guid>(TillOpenElsewhere());

        if (existing is not null)
            return Result.Failure<Guid>(Error.Conflict("CashSession_AlreadyOpenForUser"));

        var session = new CashSession(currentUser.SiteId, currentUser.UserId,
                                       clock.Now, openingFloat)
        {
            Workstation = currentUser.Workstation
        };

        await cashSessions.AddAsync(session, ct);

        unitOfWork.AuditReason = $"Ouverture de caisse — fond de {openingFloat:N2} DH";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(session.Id);
    }

    public async Task<Result<CashSessionSummary>> GetOpenCashSessionAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.View);
        if (permitted.IsFailure) return Result.Failure<CashSessionSummary>(permitted.FirstError);

        var (_, elsewhere) = await TillAsync(ct);
        if (elsewhere is not null) return Result.Failure<CashSessionSummary>(TillOpenElsewhere());

        var summary = await queries.GetCashSessionSummaryAsync(currentUser.UserId, ct);

        return summary is null
            ? Result.Failure<CashSessionSummary>(Error.NotFound("CashSession_NotOpen"))
            : Result.Success(summary);
    }

    /// <summary>
    /// Closes the till.
    /// <para>
    /// A variance beyond tolerance must be explained before the session will close. An unexplained
    /// shortfall that can simply be clicked away is not a control, and the variance by operator is
    /// exactly what an internal audit looks at.
    /// </para>
    /// </summary>
    public async Task<Result<decimal>> CloseCashSessionAsync(
        decimal countedCash, string? explanation, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.CloseCashSession);
        if (permitted.IsFailure) return Result.Failure<decimal>(permitted.FirstError);

        var (open, elsewhere) = await TillAsync(ct);
        if (elsewhere is not null) return Result.Failure<decimal>(TillOpenElsewhere());
        if (open is null) return Result.Failure<decimal>(Error.NotFound("CashSession_NotOpen"));

        var session = await cashSessions.GetByIdAsync(open.Id, ct);
        if (session is null) return Result.Failure<decimal>(Error.NotFound("CashSession_NotOpen"));

        var closed = session.Close(clock.Now, currentUser.UserId, countedCash, explanation);
        if (closed.IsFailure) return Result.Failure<decimal>(closed.Errors);

        unitOfWork.AuditReason =
            $"Clôture de caisse — écart de {session.Variance:N2} DH" +
            (string.IsNullOrWhiteSpace(explanation) ? string.Empty : $" : {explanation}");

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(session.Variance ?? 0m);
    }

    /// <summary>
    /// The file a patient ends up in: itself, or the surviving file it was merged into, followed
    /// when the survivor was merged in turn. Bounded by the visited set against a corrupt cycle.
    /// </summary>
    private async Task<Guid> SurvivorOfAsync(Guid patientId, CancellationToken ct)
    {
        var current = patientId;
        var visited = new HashSet<Guid>();

        while (visited.Add(current)
               && await patients.GetByIdAsync(current, ct) is { MergedIntoPatientId: { } survivorId })
            current = survivorId;

        return current;
    }

    /// <summary>
    /// The INPE of the biologist accountable for the acts on an invoice.
    /// <para>
    /// Returns null rather than throwing when the site has no responsible biologist or that
    /// biologist has no INPE recorded. The invoice is still lawful without it — a self-paying
    /// patient's invoice needs no INPE at all — and refusing to issue one here would block the
    /// counter over a claim that may never be made. The refusal belongs at submission, which is
    /// where <see cref="SubmitClaimBatchAsync"/> puts it and where the operator can act on it.
    /// </para>
    /// </summary>
    private async Task<string?> ResolveBiologistInpeAsync(LabSite site, CancellationToken ct)
    {
        if (site.ResponsibleBiologistUserId is not { } biologistId) return null;

        var biologist = await users.GetByIdAsync(biologistId, ct);

        return string.IsNullOrWhiteSpace(biologist?.Inpe) ? null : biologist.Inpe;
    }

    // ---- Claims ----------------------------------------------------------------------------------

    /// <summary>
    /// The claim batches, newest first: what was sent to which payer, what it was worth, and what
    /// came back.
    /// <para>
    /// A batch is the unit a Moroccan laboratory chases. The payer answers per batch, settles per
    /// batch and rejects per invoice inside one, so "which batch is this invoice in and what
    /// happened to it?" is the question the accounts office asks every week — and it had no screen
    /// that could answer it.
    /// </para>
    /// </summary>
    public async Task<Result<IReadOnlyList<ClaimBatchRow>>> GetClaimBatchesAsync(
        int withinDays = 180, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<ClaimBatchRow>>(permitted.FirstError);

        return Result.Success(await queries.GetClaimBatchesAsync(
            currentUser.SiteId, clock.Today.AddDays(-Math.Max(1, withinDays)), clock.Today, ct));
    }

    /// <summary>
    /// The invoices inside one batch, with the reason for any that was refused. Limited to the
    /// batch row's payer: a reference shared by several payers (sent before a batch was held to
    /// one payer) must not let one payer's answer be recorded on another's invoices.
    /// </summary>
    public async Task<Result<IReadOnlyList<ClaimInvoiceRow>>> GetClaimInvoicesAsync(
        string claimReference, Guid? payerId = null, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<ClaimInvoiceRow>>(permitted.FirstError);

        return Result.Success(await queries.GetClaimInvoicesAsync(claimReference, payerId, ct));
    }

    /// <summary>
    /// Invoices ready to go into a batch, grouped by payer, with whatever still blocks each one.
    /// <para>
    /// The blockers are computed here rather than discovered at submission. A claim rejected by the
    /// payer for a missing INPE comes back weeks later as a letter, by which time the laboratory has
    /// done the work, spent the reagent and has nothing to invoice twice.
    /// </para>
    /// </summary>
    public async Task<Result<IReadOnlyList<ClaimCandidateRow>>> GetClaimCandidatesAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<ClaimCandidateRow>>(permitted.FirstError);

        return Result.Success(await queries.GetClaimCandidatesAsync(currentUser.SiteId, ct));
    }

    /// <summary>
    /// Marks a batch of invoices as submitted to a third-party payer.
    /// <para>
    /// A claim that omits the biologist's INPE is rejected by the payer, so the requirement is
    /// checked here and reported per invoice rather than discovered weeks later on a rejection
    /// notice.
    /// </para>
    /// </summary>
    public async Task<Result<ClaimSubmission>> SubmitClaimBatchAsync(
        IReadOnlyList<Guid> invoiceIds, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.ExportAccounting);
        if (permitted.IsFailure) return Result.Failure<ClaimSubmission>(permitted.FirstError);

        var licensed = licence.RequireModule(LicenceModules.ThirdPartyPayer);
        if (licensed.IsFailure) return Result.Failure<ClaimSubmission>(licensed.FirstError);

        if (invoiceIds.Count == 0)
            return Result.Failure<ClaimSubmission>(Error.Validation("Message.NothingSelected"));

        var today = clock.Today;
        var problems = new List<Error>();

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var reference = await sequences.NextAsync(
            SequencePurpose.ClaimBatch, currentUser.SiteId, today.Year, ct);

        if (reference.IsFailure) return Result.Failure<ClaimSubmission>(reference.FirstError);

        var submitted = 0;
        Guid? batchPayerId = null;

        foreach (var invoiceId in invoiceIds.Distinct())
        {
            // With its payments: a rejected claim's write-off is reversed on resubmission.
            var invoice = await invoices.GetByIdWithDetailsAsync(invoiceId, ct);

            if (invoice is null)
            {
                problems.Add(Error.NotFound("Invoice_NotFound"));
                continue;
            }

            // Re-read here, not trusted from the screen: an invoice already in a batch — a second
            // click on Submit, or two operators — must not be moved into a second one.
            if (invoice.ClaimStatus is not (ClaimStatus.Prepared or ClaimStatus.Rejected))
            {
                problems.Add(Error.Conflict("Claim_AlreadySubmitted", invoice.Number));
                continue;
            }

            if (invoice.IsFullyCredited)
            {
                problems.Add(Error.Conflict("Invoice_AlreadyFullyCredited", invoice.Number));
                continue;
            }

            if (invoice.PayerId is not { } payerId)
            {
                problems.Add(Error.Validation("Invoice_ClaimRequiresPayer", invoice.Number));
                continue;
            }

            // One batch, one payer, as on the claims screen: the payer answers for its batch as a
            // whole, and an answer recorded on a mixed batch landed on another payer's invoices.
            if (batchPayerId is { } first && first != payerId)
            {
                problems.Add(Error.Validation("Claim_MixedPayers", invoice.Number));
                continue;
            }

            var payer = await payers.GetByIdAsync(payerId, ct);

            if (payer?.RequiresInpeOnClaim == true && string.IsNullOrWhiteSpace(invoice.BiologistInpe))
            {
                problems.Add(Error.Regulatory("Invoice_ClaimRequiresInpe", invoice.Number));
                continue;
            }

            // The prescriber's INPE, which the claims screen also requires of such a payer: sent
            // without it from the invoice list, the claim was simply refused.
            if (payer?.RequiresPrescription == true && string.IsNullOrWhiteSpace(invoice.PrescriberInpe))
            {
                problems.Add(Error.Regulatory("Invoice_ClaimRequiresPrescriberInpe", invoice.Number));
                continue;
            }

            batchPayerId = payerId;

            // A resubmission after a rejection: the recorded refusal and its write-off are undone,
            // or the payer's payment on the corrected claim could never be entered.
            if (invoice.ClaimStatus == ClaimStatus.Rejected)
            {
                var reopened = invoice.ReopenRejectedClaim(
                    $"renvoi au tiers payant (dossier {reference.Value})", today);

                if (reopened.IsFailure)
                {
                    problems.AddRange(reopened.Errors);
                    continue;
                }
            }

            invoice.ClaimReference = reference.Value;
            invoice.ClaimSubmittedOn = today;
            invoice.ClaimStatus = ClaimStatus.Submitted;
            submitted++;
        }

        // Nothing went into the batch: rolled back, so the reference is not spent on an empty one.
        if (submitted == 0) return Result.Failure<ClaimSubmission>(problems);

        unitOfWork.AuditReason = $"Transmission du dossier tiers payant {reference.Value}";
        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        // Committed: a success carrying the batch reference, with the invoices left out and why.
        // Returned as a failure, the operator saw only the refusals and no reference, and believed
        // nothing had been sent.
        return Result.Success(new ClaimSubmission(reference.Value, submitted, problems));
    }
}

/// <summary>
/// Renders a monetary amount in words.
/// <para>
/// A customary mention on a Moroccan invoice, and a real fraud control: the figure can be altered
/// on a printed document, the words are much harder to.
/// </para>
/// </summary>
public static class AmountInWords
{
    public static string Render(decimal amount, AppLanguage language)
    {
        var dirhams = (long)Math.Truncate(Math.Abs(amount));
        var centimes = (int)Math.Round((Math.Abs(amount) - dirhams) * 100m,
                                        MidpointRounding.AwayFromZero);

        return language switch
        {
            AppLanguage.Arabic => RenderArabic(dirhams, centimes),
            AppLanguage.English => RenderEnglish(dirhams, centimes),
            _ => RenderFrench(dirhams, centimes)
        };
    }

    private static string RenderFrench(long dirhams, int centimes)
    {
        var number = FrenchNumber(dirhams);

        // "un million de dirhams", but "un million deux cents dirhams" — the "de" appears only when
        // the multiplier noun is the last word. Worth getting right: this line is read aloud at a
        // counter and reproduced verbatim on a document an inspector examines.
        var separator = number.EndsWith("million", StringComparison.Ordinal)
                        || number.EndsWith("millions", StringComparison.Ordinal)
            ? " de "
            : " ";

        var text = number + separator + "dirham" + (dirhams > 1 ? "s" : string.Empty);

        if (centimes > 0)
            text += " et " + FrenchNumber(centimes) + " centime" + (centimes > 1 ? "s" : string.Empty);

        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string RenderEnglish(long dirhams, int centimes)
    {
        // Invariant culture, not the operator's: the wording on an issued invoice must not change
        // because the workstation's regional settings differ from the server's.
        var text = dirhams.ToString("N0", CultureInfo.InvariantCulture)
                   + " dirham" + (dirhams == 1 ? string.Empty : "s");

        if (centimes > 0) text += $" and {centimes} centime" + (centimes == 1 ? string.Empty : "s");

        return text;
    }

    private static string RenderArabic(long dirhams, int centimes)
    {
        // Arabic number agreement is genuinely complex (dual forms, case endings that vary with
        // the counted noun). Rendering the figure with the noun is unambiguous and correct, and
        // is what Moroccan invoices in Arabic commonly do.
        //
        // Western digits, as everywhere else in the application: a laboratory transcribes these
        // between systems, and Eastern Arabic numerals invite a transcription error.
        var text = dirhams.ToString("N0", CultureInfo.InvariantCulture) + " درهم";
        if (centimes > 0) text += $" و {centimes} سنتيم";
        return text;
    }

    /// <summary>
    /// Zero to nineteen, spelled out.
    /// <para>
    /// Runs to nineteen rather than sixteen because the vigesimal tens need it:
    /// <c>soixante-dix-sept</c> is built from "soixante" plus the word for seventeen, so
    /// <c>Units[17]</c> has to exist.
    /// </para>
    /// </summary>
    private static readonly string[] Units =
    [
        "zéro", "un", "deux", "trois", "quatre", "cinq", "six", "sept", "huit", "neuf",
        "dix", "onze", "douze", "treize", "quatorze", "quinze", "seize",
        "dix-sept", "dix-huit", "dix-neuf"
    ];

    private static readonly string[] Tens =
    [
        "", "dix", "vingt", "trente", "quarante", "cinquante",
        "soixante", "soixante", "quatre-vingt", "quatre-vingt"
    ];

    /// <summary>
    /// French number words, including the vigesimal forms (soixante-dix, quatre-vingts) and the
    /// plural-on-"cent"/"vingt" rules. Written out rather than taken from a library because the
    /// rules are short, fixed, and this is the only place they are needed.
    /// </summary>
    /// <param name="multiplierOfMille">
    /// True when this number is being rendered as the multiplier of <c>mille</c>. <c>cent</c> and
    /// <c>quatre-vingt</c> lose their plural s in that position — "deux cent mille", not "deux cents
    /// mille" — whereas before <c>millions</c>, which is an ordinary noun, they keep it.
    /// </param>
    private static string FrenchNumber(long value, bool multiplierOfMille = false)
    {
        if (value < 0) return "moins " + FrenchNumber(-value);
        if (value < Units.Length) return Units[value];

        if (value < 100)
        {
            var tens = (int)(value / 10);
            var remainder = (int)(value % 10);

            // 70-79 and 90-99 are built on soixante and quatre-vingt.
            if (tens is 7 or 9) return FrenchTensSeventyNinety(tens, remainder);

            var word = Tens[tens];

            if (remainder == 0)
                return tens == 8 && !multiplierOfMille ? word + "s" : word;   // quatre-vingts

            // "et un" for 21, 31 … 61; hyphen otherwise.
            return remainder == 1 && tens != 8
                ? word + " et un"
                : word + "-" + Units[remainder];
        }

        if (value < 1000)
        {
            var hundreds = (int)(value / 100);
            var remainder = value % 100;

            var word = hundreds == 1 ? "cent" : Units[hundreds] + " cent";

            // "cents" only when nothing follows, and never before mille.
            if (remainder == 0 && hundreds > 1 && !multiplierOfMille) word += "s";

            // The flag propagates into the remainder: in "deux cent quatre-vingt mille" it is the
            // quatre-vingt at the tail that must lose its s, not the cent.
            return remainder == 0
                ? word
                : word + " " + FrenchNumber(remainder, multiplierOfMille);
        }

        if (value < 1_000_000)
        {
            var thousands = value / 1000;
            var remainder = value % 1000;

            var word = thousands == 1
                ? "mille"
                : FrenchNumber(thousands, multiplierOfMille: true) + " mille";

            return remainder == 0 ? word : word + " " + FrenchNumber(remainder);
        }

        var millions = value / 1_000_000;
        var rest = value % 1_000_000;

        var millionWord = FrenchNumber(millions) + (millions == 1 ? " million" : " millions");

        return rest == 0 ? millionWord : millionWord + " " + FrenchNumber(rest);
    }

    private static string FrenchTensSeventyNinety(int tens, int remainder)
    {
        var baseWord = tens == 7 ? "soixante" : "quatre-vingt";
        var offset = remainder + 10;

        if (remainder == 0) return baseWord + "-dix";

        return remainder == 1 && tens == 7
            ? baseWord + " et onze"
            : baseWord + "-" + Units[offset];
    }
}

/// <summary>One settlement against an invoice, as the correction pickers list them.</summary>
public sealed record PaymentRow(
    Guid PaymentId,
    decimal Amount,
    PaymentMethod Method,
    PaymentStatus Status,
    DateOnly PaymentDate,
    string ReceivedByName,
    string? Reference,
    string? BankName,
    DateOnly? MaturityDate)
{
    /// <summary>Only a settled payment can be refunded or returned unpaid.</summary>
    public bool IsCorrectable => Status == PaymentStatus.Settled;

    /// <summary>
    /// A refund gives money back to the patient, so only what the patient paid can be refunded: a
    /// write-off brought no money in, and the payer's settlement is the payer's.
    /// </summary>
    public bool CanRefund => IsCorrectable && IsPatientPayment(Method);

    public static bool IsPatientPayment(PaymentMethod method) =>
        method is not (PaymentMethod.ThirdPartyPayer or PaymentMethod.WriteOff);

    /// <summary>A bounce only applies to an instrument that can be returned by a bank.</summary>
    public bool CanBounce =>
        IsCorrectable && Method is PaymentMethod.Cheque or PaymentMethod.BillOfExchange;
}

/// <summary>One claim batch sent to a third-party payer.</summary>
public sealed record ClaimBatchRow(
    string ClaimReference,
    Guid? PayerId,
    string PayerName,
    DateOnly SubmittedOn,
    int InvoiceCount,
    decimal TotalClaimed,
    decimal PayerShareClaimed,
    decimal Settled,
    int AcceptedCount,
    int RejectedCount,
    int PendingCount,
    DateOnly? LastOutcomeOn)
{
    /// <summary>Nothing has come back yet. After sixty days that is itself the finding.</summary>
    public bool IsAwaitingOutcome => PendingCount == InvoiceCount;

    public int DaysOutstanding(DateOnly today) => today.DayNumber - SubmittedOn.DayNumber;

    /// <summary>
    /// A batch with no answer after sixty days. Payers answer in weeks, so silence past that point
    /// usually means the batch was never received — and the laboratory has to ask rather than wait.
    /// </summary>
    public bool NeedsChasing(DateOnly today) =>
        IsAwaitingOutcome && DaysOutstanding(today) > 60;

    public decimal Unsettled => Math.Max(0m, PayerShareClaimed - Settled);
}

/// <summary>One invoice inside a claim batch.</summary>
public sealed record ClaimInvoiceRow(
    Guid InvoiceId,
    string Number,
    DateOnly IssueDate,
    string PatientName,
    string? PrescriberInpe,
    decimal Total,
    decimal PayerShare,
    decimal Paid,
    ClaimStatus ClaimStatus,
    string? ClaimRejectionReason,
    string? CoveredOrders)
{
    public bool WasRejected =>
        ClaimStatus is ClaimStatus.Rejected or ClaimStatus.PartiallyAccepted;

    /// <summary>
    /// Rejected and never resubmitted. This is money the laboratory has earned and abandoned, and
    /// it is the row the accounts office exists to find.
    /// </summary>
    public bool IsAbandoned => ClaimStatus == ClaimStatus.Rejected;
}

/// <summary>An invoice that could join a batch, with whatever still stops it.</summary>
public sealed record ClaimCandidateRow(
    Guid InvoiceId,
    string Number,
    DateOnly IssueDate,
    Guid PayerId,
    string PayerName,
    string PatientName,
    decimal Total,
    decimal PayerShare,
    bool RequiresInpe,
    bool HasBiologistInpe,
    bool RequiresPrescription,
    bool HasPrescriberInpe,
    int DaysSinceIssue)
{
    /// <summary>
    /// Why this invoice cannot be claimed yet, as resource keys. Returned rather than a boolean so
    /// the screen can say which field is missing instead of "not claimable".
    /// <para>
    /// These are short, parameter-free <c>Claim.*</c> keys, not the <c>Invoice_*</c> errors the
    /// submission path raises. The two are resolved differently and cannot be shared: the
    /// submission error is built with <c>Error.Regulatory(key, invoice.Number)</c> and its text
    /// carries a <c>{0}</c>, whereas the claims grid resolves a blocker through
    /// <c>Localizer[key]</c> with no arguments — so pointing the grid at the submission key printed
    /// a literal "{0}" in the blockers column. The other half of the pair,
    /// <c>Invoice_ClaimRequiresPrescriberInpe</c>, was never in any catalogue at all and rendered
    /// as its own raw key.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Blockers
    {
        get
        {
            var blockers = new List<string>(2);

            if (RequiresInpe && !HasBiologistInpe) blockers.Add("Claim.RequiresBiologistInpe");
            if (RequiresPrescription && !HasPrescriberInpe)
                blockers.Add("Claim.RequiresPrescriberInpe");

            return blockers;
        }
    }

    public bool IsClaimable => Blockers.Count == 0;
}

/// <summary>Read-side queries for the billing screens.</summary>
public interface IBillingQueries
{
    /// <summary>Claim batches submitted since a date, newest first.</summary>
    Task<IReadOnlyList<ClaimBatchRow>> GetClaimBatchesAsync(Guid siteId, DateOnly since,
                                                             DateOnly today,
                                                             CancellationToken ct = default);

    Task<IReadOnlyList<ClaimInvoiceRow>> GetClaimInvoicesAsync(string claimReference,
                                                                Guid? payerId = null,
                                                                CancellationToken ct = default);

    /// <summary>Issued, payer-covered invoices not yet in a batch.</summary>
    Task<IReadOnlyList<ClaimCandidateRow>> GetClaimCandidatesAsync(Guid siteId,
                                                                    CancellationToken ct = default);

    /// <summary>
    /// How many rows the billing desk lists at most, the most recent first. A list that reaches it
    /// is truncated, and the screen says so.
    /// </summary>
    const int UninvoicedOrdersLimit = 500;

    Task<IReadOnlyList<BillableOrderRow>> GetUninvoicedOrdersAsync(
        Guid siteId, CancellationToken ct = default);

    Task<PagedResult<InvoiceRow>> GetInvoicesAsync(
        Guid siteId, InvoiceStatus? status, string? search, int page, int pageSize,
        DateOnly today, CancellationToken ct = default);

    /// <summary>
    /// How many invoices of the site each status holds, for the same search as
    /// <see cref="GetInvoicesAsync"/> and without its status filter: the counts on the ledger's
    /// status chips. Drafts are left out, as in the ledger.
    /// </summary>
    Task<IReadOnlyDictionary<InvoiceStatus, int>> GetInvoiceStatusCountsAsync(
        Guid siteId, string? search, CancellationToken ct = default);

    /// <summary>
    /// The settlements recorded against one invoice, so a refund or a returned cheque can be
    /// pointed at the right one. An invoice is frequently settled in two or three parts — part
    /// cash at the counter, the balance by transfer — so "the payment" is not a single thing.
    /// </summary>
    Task<IReadOnlyList<PaymentRow>> GetPaymentsAsync(Guid invoiceId, CancellationToken ct = default);

    /// <summary>
    /// Everything needed to print one invoice or its payment receipt, or null when the invoice
    /// does not exist (or is outside the operator's site).
    /// </summary>
    Task<InvoiceDocument?> GetInvoiceDocumentAsync(Guid invoiceId, CancellationToken ct = default);

    Task<CashSession?> GetOpenCashSessionAsync(Guid operatorUserId, CancellationToken ct = default);

    Task<CashSessionSummary?> GetCashSessionSummaryAsync(Guid operatorUserId,
                                                          CancellationToken ct = default);
}
