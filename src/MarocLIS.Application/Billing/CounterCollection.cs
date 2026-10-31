using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Billing;

/// <summary>What « Encaissement » takes: the whole of what the patient owes, or a part of it.</summary>
public enum CollectionKind
{
    /// <summary>« Tout payer »: everything the patient still owes on the file.</summary>
    Full = 0,

    /// <summary>« Acompte »: part of it, the rest left to pay later.</summary>
    Deposit = 1
}

/// <summary>The operator's till as the counter needs to know it before any money is taken.</summary>
public enum TillState
{
    /// <summary>Open at the site the operator works at: cash can be taken.</summary>
    Open = 0,

    /// <summary>No till open: cash is refused until one is.</summary>
    NotOpen = 1,

    /// <summary>The operator's till is still open at another site: it is closed there first.</summary>
    OpenElsewhere = 2
}

/// <summary>
/// Where one file stands, as the « Encaissement » panel shows it: the patient's share after the
/// payer's coverage and the discounts, what was already taken on it (deposits, or settlements on its
/// invoice), and what is still due — the report counter's own reading (<see cref="OrderPayment"/>).
/// </summary>
/// <param name="Total">The live acts' charges, discounts already taken off.</param>
/// <param name="PayerShare">The payer's share when the payer is billed directly, otherwise zero.</param>
/// <param name="Discount">The discount granted at registration.</param>
/// <param name="AlreadyPaid">Deposits held, or what was collected on the invoice.</param>
public sealed record CounterBalance(
    Guid LabOrderId,
    string AccessionNumber,
    Guid PatientId,
    string PatientName,
    DateTimeOffset RegisteredAt,
    OrderPayment Payment,
    Guid? InvoiceId,
    decimal Total,
    decimal PayerShare,
    decimal Discount,
    decimal AlreadyPaid)
{
    /// <summary>What the patient still owes on the file: zero when nothing is.</summary>
    public decimal Due => Math.Max(0m, Payment.PatientDue ?? 0m);

    public bool IsInvoiced => InvoiceId is not null;

    public string? InvoiceNumber => Payment.InvoiceNumber;
}

/// <summary>One line of « À encaisser »: a file of today with something still due from the patient.</summary>
public sealed record CollectionQueueRow(
    Guid LabOrderId,
    string AccessionNumber,
    string PatientName,
    DateTimeOffset RegisteredAt,
    OrderPaymentState State,
    decimal Due,
    string? InvoiceNumber,
    decimal AlreadyPaid);

/// <summary>What the panel sends: how much, how, and what the operator saw as due.</summary>
/// <param name="Amount">
/// The amount taken. For « Tout payer », what the panel showed as due: if the file changed in between
/// (another counter took a payment), the service refuses rather than take a different sum.
/// </param>
public sealed record CollectRequest(
    Guid LabOrderId,
    CollectionKind Kind,
    decimal Amount,
    PaymentMethod Method,
    string? Reference = null,
    string? BankName = null,
    DateOnly? MaturityDate = null);

/// <summary>What was done: the money taken and where it went, for the receipt and the status line.</summary>
/// <param name="InvoiceIssued">The invoice was issued by this collection (« Tout payer » on a file not invoiced).</param>
/// <param name="DepositNumber">The receipt number of the deposit taken, when the money went into a deposit.</param>
public sealed record CollectionDone(
    Guid LabOrderId,
    string AccessionNumber,
    CollectionKind Kind,
    decimal Amount,
    PaymentMethod Method,
    Guid? InvoiceId,
    string? InvoiceNumber,
    bool InvoiceIssued,
    string? DepositNumber,
    decimal RemainingDue);

/// <summary>The reads « Encaissement » and « À encaisser » need, scoped to one site.</summary>
public interface ICounterCollectionQueries
{
    /// <summary>The file's balance, or null when it is not one of the site's (or does not exist).</summary>
    Task<CounterBalance?> GetBalanceAsync(Guid labOrderId, Guid siteId, CancellationToken ct = default);

    /// <summary>
    /// The site's files registered between <paramref name="from"/> and <paramref name="to"/> on which
    /// the patient still owes something, oldest first, cancelled and draft files left out.
    /// </summary>
    Task<IReadOnlyList<CollectionQueueRow>> GetQueueAsync(Guid siteId, DateTimeOffset from, DateTimeOffset to,
                                                          CancellationToken ct = default);

    /// <summary>The counter method the user took their last payment or deposit by; null when they never took one.</summary>
    Task<PaymentMethod?> GetLastMethodAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// The receipt of the deposits held on a file not invoiced yet, laid out as the invoice receipt
    /// is (<see cref="InvoiceKind.Receipt"/>, numbered by the last deposit's receipt number).
    /// </summary>
    Task<InvoiceDocument?> GetDepositReceiptAsync(Guid labOrderId, CancellationToken ct = default);
}

/// <summary>
/// « Encaissement » at the counter: the patient's share taken where the patient is — at the end of
/// the registration, on the file, at the report counter, from « À encaisser » — without switching to
/// « Facturation ».
/// <para>
/// Nothing here is a payment path of its own. « Tout payer » on a file not invoiced issues its
/// invoice through <see cref="BillingService.IssueInvoiceAsync"/> (deposits held applied, discount
/// approvals and the series as ever) and records the balance through
/// <see cref="BillingService.RecordPaymentAsync"/>; « Acompte » on it is
/// <see cref="BillingService.TakeDepositAsync"/>, applied when the invoice is issued; on a file
/// already invoiced either is a settlement of its invoice. The till, the audit trail, the takings
/// and the accounting exports therefore see exactly what the billing desk would have recorded.
/// </para>
/// <para>
/// A user who may take payments but not issue invoices gets « Tout payer » as a deposit of the whole
/// amount: the money is taken now, and becomes a settlement when the invoice is issued.
/// </para>
/// </summary>
public sealed class CounterCollectionService(
    BillingService billing,
    ICounterCollectionQueries queries,
    IBillingQueries billingQueries,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>The note a settlement taken at the counter carries, as the payment list shows it.</summary>
    public const string CounterNote = "Encaissement au guichet";

    /// <summary>Where the file stands: what the patient owes and what was already taken.</summary>
    public async Task<Result<CounterBalance>> GetBalanceAsync(Guid labOrderId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.RecordPayment);
        if (permitted.IsFailure) return Result.Failure<CounterBalance>(permitted.FirstError);

        var balance = await queries.GetBalanceAsync(labOrderId, currentUser.SiteId, ct);
        return balance is null
            ? Result.Failure<CounterBalance>(Error.NotFound("Order_NotFound", labOrderId))
            : Result.Success(balance);
    }

    /// <summary>« À encaisser »: today's files of the site with something still due, oldest first.</summary>
    public async Task<Result<IReadOnlyList<CollectionQueueRow>>> GetQueueAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.RecordPayment);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<CollectionQueueRow>>(permitted.FirstError);

        var (from, to) = TodayBounds(clock);
        return Result.Success(await queries.GetQueueAsync(currentUser.SiteId, from, to, ct));
    }

    /// <summary>The method the panel opens on: the one this user took their last payment by, else cash.</summary>
    public async Task<PaymentMethod> GetPreferredMethodAsync(CancellationToken ct = default)
    {
        if (!currentUser.Has(Permissions.Billing.RecordPayment)) return PaymentMethod.Cash;
        return CounterCollectionRules.DefaultMethod(await queries.GetLastMethodAsync(currentUser.UserId, ct));
    }

    /// <summary>The operator's till, for the counter to offer opening it before the first patient pays.</summary>
    public Task<Result<TillState>> GetTillStateAsync(CancellationToken ct = default) => billing.GetTillStateAsync(ct);

    /// <summary>
    /// Takes the money. Checked before anything is written — the amount against what is due, the
    /// method, the reference it needs, the till for cash — so a refusal never leaves an invoice
    /// issued for a payment that could not be taken.
    /// </summary>
    public async Task<Result<CollectionDone>> CollectAsync(CollectRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var permitted = guard.Require(Permissions.Billing.RecordPayment);
        if (permitted.IsFailure) return Result.Failure<CollectionDone>(permitted.FirstError);

        if (!BillingScreenRules.CounterMethods.Contains(request.Method))
            return Result.Failure<CollectionDone>(Error.Validation("Payment_MethodNotAllowed"));

        var balance = await queries.GetBalanceAsync(request.LabOrderId, currentUser.SiteId, ct);
        if (balance is null)
            return Result.Failure<CollectionDone>(Error.NotFound("Order_NotFound", request.LabOrderId));

        var due = balance.Due;
        if (due <= 0m) return Result.Failure<CollectionDone>(Error.Conflict("Collect_NothingDue", balance.AccessionNumber));

        var amount = Money.Round(request.Amount);
        if (amount <= 0m) return Result.Failure<CollectionDone>(Error.Validation("Payment_AmountMustBePositive"));

        if (request.Kind == CollectionKind.Full && amount != due)
            return Result.Failure<CollectionDone>(Error.Conflict("Collect_DueChanged", due));

        if (amount > due)
            return Result.Failure<CollectionDone>(Error.Validation(
                balance.IsInvoiced ? "Payment_ExceedsOutstanding" : "Deposit_ExceedsOrderTotal", due));

        if (CounterCollectionRules.MissingReference(request.Method, request.Reference) is { } missing)
            return Result.Failure<CollectionDone>(Error.Validation(missing));

        if (request.Method == PaymentMethod.Cash)
        {
            var till = await billing.GetTillStateAsync(ct);
            if (till.IsFailure) return Result.Failure<CollectionDone>(till.Errors);
            if (till.Value == TillState.OpenElsewhere)
                return Result.Failure<CollectionDone>(Error.Conflict("CashSession_OpenOnAnotherSite"));
            if (till.Value == TillState.NotOpen)
                return Result.Failure<CollectionDone>(Error.Validation("Payment_CashRequiresOpenSession"));
        }

        var reference = Clean(request.Reference);
        var bank = Clean(request.BankName);

        // Already invoiced: a settlement of the invoice, the whole or a part.
        if (balance.InvoiceId is { } invoiceId)
        {
            var paid = await billing.RecordPaymentAsync(new RecordPaymentRequest(
                invoiceId, amount, request.Method, reference, bank, request.MaturityDate, CounterNote), ct);
            if (paid.IsFailure) return Result.Failure<CollectionDone>(paid.Errors);

            return Result.Success(await DoneAsync(balance, request, amount, invoiceId, balance.InvoiceNumber, false, null, ct));
        }

        // « Tout payer » with the right to invoice: the invoice is issued, the deposits held applied
        // to it, and what is left settled on it.
        if (request.Kind == CollectionKind.Full && currentUser.Has(Permissions.Billing.CreateInvoice))
        {
            var issued = await billing.IssueInvoiceAsync([balance.LabOrderId], ct: ct);
            if (issued.IsFailure) return Result.Failure<CollectionDone>(issued.Errors);

            var invoiced = await queries.GetBalanceAsync(balance.LabOrderId, currentUser.SiteId, ct);
            var number = invoiced?.InvoiceNumber;
            var left = invoiced?.Due ?? 0m;

            // What the invoice leaves to the patient, which is what was shown unless the invoice
            // came to less (a deposit held absorbed more of it): never more than the operator took.
            var settle = Math.Min(left, amount);
            if (settle > 0m)
            {
                var paid = await billing.RecordPaymentAsync(new RecordPaymentRequest(
                    issued.Value, settle, request.Method, reference, bank, request.MaturityDate, CounterNote), ct);
                if (paid.IsFailure) return Result.Failure<CollectionDone>(paid.Errors);
            }

            return Result.Success(await DoneAsync(balance, request, settle, issued.Value, number, true, null, ct));
        }

        // « Acompte », or « Tout payer » by a user who does not invoice: a deposit, applied when
        // the invoice is issued.
        var deposit = await billing.TakeDepositAsync(new TakeDepositRequest(
            balance.LabOrderId, amount, request.Method, reference, bank, request.MaturityDate), ct);
        if (deposit.IsFailure) return Result.Failure<CollectionDone>(deposit.Errors);

        return Result.Success(await DoneAsync(balance, request, amount, null, null, false, deposit.Value, ct));
    }

    /// <summary>
    /// The receipt of what was just taken: the invoice's payment receipt, or for a deposit the
    /// receipt of the deposits held on the file. Readable by whoever took the money.
    /// </summary>
    public async Task<Result<InvoiceDocument>> GetReceiptAsync(CollectionDone done, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(done);

        var permitted = guard.Require(Permissions.Billing.RecordPayment);
        if (permitted.IsFailure) return Result.Failure<InvoiceDocument>(permitted.FirstError);

        var document = done.InvoiceId is { } invoiceId
            ? await billingQueries.GetInvoiceDocumentAsync(invoiceId, ct)
            : await queries.GetDepositReceiptAsync(done.LabOrderId, ct);

        return document is null
            ? Result.Failure<InvoiceDocument>(Error.NotFound("Invoice_NotFound"))
            : Result.Success(document);
    }

    /// <summary>Today in the clock's own offset: from midnight to the next.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) TodayBounds(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var from = new DateTimeOffset(clock.Today.ToDateTime(TimeOnly.MinValue), clock.Now.Offset);
        return (from, from.AddDays(1));
    }

    private async Task<CollectionDone> DoneAsync(CounterBalance before, CollectRequest request, decimal amount,
                                                 Guid? invoiceId, string? invoiceNumber, bool issued,
                                                 string? depositNumber, CancellationToken ct)
    {
        var after = await queries.GetBalanceAsync(before.LabOrderId, currentUser.SiteId, ct);
        return new CollectionDone(before.LabOrderId, before.AccessionNumber, request.Kind, amount, request.Method,
                                  invoiceId, invoiceNumber ?? after?.InvoiceNumber, issued, depositNumber,
                                  after?.Due ?? Math.Max(0m, Money.Round(before.Due - amount)));
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
