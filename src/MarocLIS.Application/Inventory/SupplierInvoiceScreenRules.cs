using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;

namespace MarocLIS.Application.Inventory;

/// <summary>
/// What « Factures fournisseurs » offers, by right and by the state of the invoice selected — the
/// workstation's PermissionGate calls and RefreshButtons. SupplierInvoiceService stays the judge.
/// </summary>
/// <param name="New">An invoice entered against the order lines (entering invoices).</param>
/// <param name="Rematch">The three-way match run again on a blocked invoice.</param>
/// <param name="Unblock">A blocked invoice released for payment with a justification (approving payments).</param>
/// <param name="UnblockOwn">The blocked invoice was entered by the operator: its release is someone else's.</param>
/// <param name="Pay">A payment recorded on an invoice good to pay (approving payments).</param>
/// <param name="Attach">The invoice's scan attached or replaced (entering invoices).</param>
/// <param name="ViewScan">The scan opened.</param>
/// <param name="Cancel">An open invoice nothing was paid on cancelled (entering invoices).</param>
/// <param name="LatePayments">The late-payment declaration of loi 69-21.</param>
/// <param name="Export">The accounting export (exporting accounting).</param>
/// <param name="Settings">The tolerance, rates and accounts (managing settings).</param>
public sealed record SupplierInvoiceScreenActions(
    bool New, bool Rematch, bool Unblock, bool UnblockOwn, bool Pay, bool Attach, bool ViewScan, bool Cancel,
    bool LatePayments, bool Export, bool Settings);

/// <summary>One order line of an invoice being entered: ticked or not, what is billed and at what price.</summary>
public sealed class SupplierInvoiceLineForm
{
    public required InvoiceableOrderLine Line { get; init; }
    public bool Included { get; set; }
    public string? Quantity { get; set; }
    public string? UnitPrice { get; set; }
    public string? VatPercent { get; set; }
}

/// <summary>An invoice as typed: the workstation's SupplierInvoiceEntryDialog.</summary>
public sealed class SupplierInvoiceForm
{
    public Guid? SupplierId { get; set; }
    public string? InvoiceNumber { get; set; }
    public DateOnly InvoiceDate { get; set; }
    public DateOnly ReceivedOn { get; set; }
    public string? Notes { get; set; }
    public List<SupplierInvoiceLineForm> Lines { get; set; } = [];
}

/// <summary>A payment as typed: the workstation's SupplierPaymentDialog.</summary>
public sealed class SupplierPaymentForm
{
    public DateOnly PaidOn { get; set; }
    public string? Amount { get; set; }
    public SupplierPaymentMethod Method { get; set; } = SupplierPaymentMethod.Transfer;
    public string? Reference { get; set; }
    public DateOnly MaturityDate { get; set; }
}

/// <summary>A period, and for the accounting export the file layout: the workstation's PurchasingPeriodDialog.</summary>
public sealed class PurchasingPeriodForm
{
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public AccountingExportLayout Layout { get; set; } = AccountingExportLayout.Sage;
}

/// <summary>The purchasing parameters as typed: the workstation's PurchasingSettingsDialog.</summary>
public sealed class PurchasingSettingsForm
{
    public string? TolerancePercent { get; set; }
    public string? ToleranceMad { get; set; }
    public string? KeyRate { get; set; }
    public string? PenaltyMargin { get; set; }
    public string? FineFirstMonth { get; set; }
    public string? FineNextMonths { get; set; }
    public string? PurchaseJournal { get; set; }
    public string? BankJournal { get; set; }
    public string? CashJournal { get; set; }
    public string? SupplierAccount { get; set; }
    public string? VatAccount { get; set; }
    public string? BankAccount { get; set; }
    public string? CashAccount { get; set; }
    public Dictionary<StockItemKind, string?> ChargeAccounts { get; init; } = [];
}

/// <summary>
/// The rules of « Factures fournisseurs » that need no window: what is offered by which right and
/// in which state, how an invoice reads (overdue in the alarm's colours, blocked on its pill and
/// number, the days to its legal due date, the variances of the three-way match, its payments), the
/// due-date dashboard, how an invoice, a payment, a period and the purchasing parameters are typed
/// and refused box by box. The workstation's SupplierInvoiceView and its dialogs and the web's
/// screen apply the same ones; every decision stays SupplierInvoiceService's.
/// </summary>
public static class SupplierInvoiceScreenRules
{
    public const int NumberMaxLength = 60;
    public const int NotesMaxLength = 1000;
    public const int ReferenceMaxLength = 60;
    public const int CodeMaxLength = 20;
    public const int UnblockMinLength = 10;
    public const int UnblockMaxLength = 1000;
    public const int CancelReasonMaxLength = 500;
    public const int AmountMaxLength = TypedNumber.AmountMaxLength;
    public const int QuantityMaxLength = TypedNumber.MeasureMaxLength;

    public const string SupplierField = "invoice-supplier";
    public const string NumberField = "invoice-number";
    public const string InvoiceDateField = "invoice-date";
    public const string ReceivedField = "invoice-received";
    public const string LinesField = "invoice-lines";
    public const string ScanField = "invoice-scan";
    public const string PaidOnField = "payment-date";
    public const string AmountField = "payment-amount";
    public const string ReferenceField = "payment-reference";
    public const string MaturityField = "payment-maturity";
    public const string PeriodField = "period-to";

    /// <summary>The kinds of file a scan is kept as.</summary>
    public static readonly IReadOnlyList<string> ScanExtensions = [".pdf", ".jpg", ".jpeg", ".png", ".tif", ".tiff"];

    public static string ScanAccept => string.Join(",", ScanExtensions);

    /// <summary>A figure of an invoice line: « invoice-quantity-2 », « invoice-price-2 », « invoice-vat-2 ».</summary>
    public static string LineField(string column, int index) => $"invoice-{column}-{index.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A box of the parameters: « setting-key-rate ».</summary>
    public static string SettingField(string name) => $"setting-{name}";

    // ---- Rights and states ------------------------------------------------------------------------

    public static SupplierInvoiceScreenActions Actions(ICurrentUser user, SupplierInvoiceRow? row)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Actions(user.Has, user.UserId, row);
    }

    /// <summary>
    /// A blocked invoice is matched again or released — never by whoever entered it —, an invoice
    /// good to pay is paid, an open one nothing was paid on is cancelled, a scan is attached to any
    /// that is not cancelled.
    /// </summary>
    public static SupplierInvoiceScreenActions Actions(Func<string, bool> has, Guid userId, SupplierInvoiceRow? row)
    {
        ArgumentNullException.ThrowIfNull(has);
        var enter = has(Permissions.Inventory.EnterInvoices);
        var approve = has(Permissions.Inventory.ApprovePayments);
        var blocked = row is { Status: SupplierInvoiceStatus.Blocked };

        return new SupplierInvoiceScreenActions(
            New: enter,
            Rematch: (enter || approve) && blocked,
            Unblock: approve && blocked,
            UnblockOwn: blocked && row!.EnteredByUserId == userId,
            Pay: approve && row?.Status is SupplierInvoiceStatus.ApprovedForPayment or SupplierInvoiceStatus.PartiallyPaid,
            Attach: enter && row is not null && row.Status != SupplierInvoiceStatus.Cancelled,
            ViewScan: row is { HasScan: true },
            Cancel: enter && row is { IsOpen: true, AmountPaid: 0m },
            LatePayments: enter || approve,
            Export: has(Permissions.Billing.ExportAccounting),
            Settings: has(Permissions.Administration.ManageSettings));
    }

    public static SupplierInvoiceRow? Keep(IReadOnlyList<SupplierInvoiceRow> rows, Guid? kept)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.FirstOrDefault(r => r.Id == kept) ?? (rows.Count > 0 ? rows[0] : null);
    }

    // ---- How an invoice reads ----------------------------------------------------------------------

    public static StockTone StatusTone(SupplierInvoiceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row switch
        {
            { IsOverdue: true } => StockTone.Critical,
            { Status: SupplierInvoiceStatus.Blocked } => StockTone.Warning,
            { Status: SupplierInvoiceStatus.Cancelled } => StockTone.Waiting,
            { IsOpen: false } => StockTone.Done,
            { Status: SupplierInvoiceStatus.PartiallyPaid } => StockTone.Running,
            _ => StockTone.Waiting
        };
    }

    public static string StatusKey(SupplierInvoiceStatus status) => $"SupplierInvoiceStatus.{status}";

    /// <summary>An overdue invoice in the alarm's colours on the whole row; a closed one muted.</summary>
    public static StockInk RowInk(SupplierInvoiceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsOverdue) return StockInk.Critical;
        return row.Status != SupplierInvoiceStatus.Blocked && !row.IsOpen ? StockInk.Muted : StockInk.Plain;
    }

    /// <summary>A blocked invoice is not urgent: its pill and its number say it, not the whole row.</summary>
    public static StockInk NumberInk(SupplierInvoiceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !row.IsOverdue && row.Status == SupplierInvoiceStatus.Blocked ? StockInk.AbnormalBold : StockInk.Plain;
    }

    public static string Date(DateOnly? date) => date?.ToString("dd/MM/yy", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>« J-12 » to the due date, « +5 » days past it; nothing once the invoice is closed.</summary>
    public static string Days(SupplierInvoiceRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        if (!row.IsOpen) return string.Empty;
        return row.DaysUntilDue < 0
            ? text("SupplierInvoices.DaysLate", (-row.DaysUntilDue).ToString(CultureInfo.InvariantCulture))
            : text("SupplierInvoices.DaysLeft", row.DaysUntilDue.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A filter's chip: the due ones with their count from the dashboard.</summary>
    public static string Chip(SupplierInvoiceFilter filter, SupplierDueSummary? due, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int? count = due is null ? null : filter switch
        {
            SupplierInvoiceFilter.Overdue => due.Overdue,
            SupplierInvoiceFilter.DueWithin7Days => due.DueWithin7Days,
            SupplierInvoiceFilter.DueWithin15Days => due.DueWithin15Days,
            SupplierInvoiceFilter.Blocked => due.Blocked,
            _ => null
        };
        var label = text($"SupplierInvoiceFilter.{filter}");
        return count is { } n ? $"{label} · {n.ToString(CultureInfo.InvariantCulture)}" : label;
    }

    /// <summary>The due-date dashboard: overdue, due within 7 and 15 days, blocked — in the alarm's ink when one is overdue.</summary>
    /// <param name="amount">How the screen writes an amount.</param>
    /// <param name="key">The workstation's line names its currency; the web's writes it with the amounts.</param>
    public static (string Text, bool Alarm) DueSummary(SupplierDueSummary? due, ScreenText text, Func<decimal, string> amount,
                                                      string key = "SupplierInvoices.DueSummary")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        if (due is null) return (string.Empty, false);
        return (text(key, due.Overdue, amount(due.OverdueAmount), due.DueWithin7Days, amount(due.DueWithin7DaysAmount),
                     due.DueWithin15Days, amount(due.DueWithin15DaysAmount), due.Blocked), due.Overdue > 0);
    }

    /// <summary>« INV-778 · Biosystems Maroc · Bloquée ».</summary>
    public static string DetailNote(SupplierInvoiceDetail? detail, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (detail is null) return string.Empty;
        var h = detail.Header;
        return $"{h.InvoiceNumber} · {h.SupplierName} · {text(StatusKey(h.Status))}";
    }

    /// <summary>The totals, the terms and the due date, who entered it; who released it and why; why it was cancelled.</summary>
    public static IReadOnlyList<string> DetailInfo(SupplierInvoiceDetail detail, ScreenText text, Func<decimal, string> amount,
                                                   string key = "SupplierInvoices.DetailInfo")
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        var h = detail.Header;
        var info = new List<string>
        {
            text(key, amount(h.TotalExcludingVat), amount(h.TotalIncludingVat - h.TotalExcludingVat), amount(h.TotalIncludingVat),
                 detail.PaymentTermsDays, Date(h.DueDate), h.EnteredByName)
        };
        if (detail.UnblockJustification is not null)
            info.Add(text("SupplierInvoices.UnblockedInfo", detail.UnblockedByName ?? "?",
                          detail.UnblockedAt?.ToLocalTime().ToString("dd/MM/yy HH:mm", CultureInfo.InvariantCulture) ?? string.Empty,
                          detail.UnblockJustification));
        if (detail.CancellationReason is not null) info.Add(text("SupplierInvoices.CancelledInfo", detail.CancellationReason));
        return info;
    }

    /// <summary>A variance of the three-way match, said with what was ordered or received against what was invoiced.</summary>
    public static string Variance(SupplierInvoiceVarianceRow variance, ScreenText text, Func<decimal, string> amount,
                                  IFormatProvider? provider = null, string priceKey = "VarianceKind.PriceDiffers")
    {
        ArgumentNullException.ThrowIfNull(variance);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        var culture = provider ?? CultureInfo.CurrentCulture;
        return variance.Kind switch
        {
            VarianceKind.PriceDiffers => text(priceKey, amount(variance.Expected), amount(variance.Actual)),
            VarianceKind.VatRateDiffers => text("VarianceKind.VatRateDiffers", Percent(variance.Expected, culture), Percent(variance.Actual, culture)),
            _ => text("VarianceKind.QuantityExceedsReceived", Quantity(variance.Expected, culture), Quantity(variance.Actual, culture))
        };
    }

    public static string Percent(decimal rate, IFormatProvider? provider = null) =>
        (rate * 100m).ToString("0.#", provider ?? CultureInfo.CurrentCulture) + "%";

    public static string Quantity(decimal value, IFormatProvider? provider = null) =>
        value.ToString("0.###", provider ?? CultureInfo.CurrentCulture);

    public static string Amount(decimal value, IFormatProvider? provider = null) =>
        value.ToString("N2", provider ?? CultureInfo.CurrentCulture);

    /// <summary>The variances' tab, with their count when there are some.</summary>
    public static string VariancesTab(SupplierInvoiceDetail? detail, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return detail is { Variances.Count: > 0 } d
            ? $"{text("SupplierInvoices.Variances")} ({d.Variances.Count.ToString(CultureInfo.InvariantCulture)})"
            : text("SupplierInvoices.Variances");
    }

    /// <summary>What the match said of an invoice just entered or matched again: blocked with its variances, or good to pay.</summary>
    public static (string Text, bool Blocked) Outcome(SupplierInvoiceOutcome outcome, ScreenText text, bool rematch = false)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(text);
        if (outcome.Status != SupplierInvoiceStatus.Blocked) return (text("SupplierInvoices.CreatedApproved", Date(outcome.DueDate)), false);
        return rematch
            ? (text("SupplierInvoices.StillBlocked", outcome.VarianceCount), true)
            : (text("SupplierInvoices.CreatedBlocked", outcome.VarianceCount, Date(outcome.DueDate)), true);
    }

    public static string UnblockPrompt(SupplierInvoiceRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return text("SupplierInvoices.UnblockPrompt", row.InvoiceNumber, row.VarianceCount);
    }

    // ---- A new invoice ------------------------------------------------------------------------------

    public static SupplierInvoiceForm NewInvoice(DateOnly today) => new() { InvoiceDate = today, ReceivedOn = today };

    /// <summary>
    /// The supplier's order lines to tick: those received and not yet invoiced ticked, with that
    /// quantity — the rest of what was ordered otherwise —, at the ordered net price and VAT.
    /// </summary>
    public static List<SupplierInvoiceLineForm> Lines(IEnumerable<InvoiceableOrderLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        static string N(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        return
        [
            .. lines.Select(l => new SupplierInvoiceLineForm
            {
                Line = l,
                Included = l.ReceivedNotInvoiced > 0m,
                Quantity = N(l.ReceivedNotInvoiced > 0m ? l.ReceivedNotInvoiced : l.QuantityOrdered - l.QuantityInvoiced),
                UnitPrice = l.NetUnitPriceMad.ToString("0.00", CultureInfo.InvariantCulture),
                VatPercent = N(l.VatRate * 100m)
            })
        ];
    }

    /// <summary>The totals of the lines ticked, as typed so far: without VAT, the VAT, and with it.</summary>
    public static (decimal ExcludingVat, decimal Vat, decimal IncludingVat) Totals(SupplierInvoiceForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        decimal ht = 0m, vat = 0m;
        foreach (var line in form.Lines.Where(l => l.Included))
        {
            var lineHt = Money.Round((TypedNumber.Parse(line.Quantity) ?? 0m) * Money.Round(TypedNumber.Parse(line.UnitPrice) ?? 0m));
            ht += lineHt;
            vat += Money.Round(lineHt * (TypedNumber.Parse(line.VatPercent) ?? 0m) / 100m);
        }

        return (ht, vat, ht + vat);
    }

    /// <summary>
    /// An invoice as the dialog checks it: the supplier and its number, an invoice date not in the
    /// future, received between that date and today; each line ticked billed above zero, at a price
    /// not negative, a VAT within 0–100 % — a VAT of 120 % was reported as a negative price —, and
    /// at least one line.
    /// </summary>
    public static (FieldRefusal? Refusal, CreateSupplierInvoiceRequest? Request) CheckInvoice(SupplierInvoiceForm form, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.SupplierId is not { } supplierId) return (new FieldRefusal(SupplierField, "Po_SupplierRequired"), null);
        if (string.IsNullOrWhiteSpace(form.InvoiceNumber)) return (new FieldRefusal(NumberField, "SupplierInvoice_NumberRequired"), null);
        if (form.InvoiceDate > today) return (new FieldRefusal(InvoiceDateField, "SupplierInvoice_DateInFuture"), null);
        if (form.ReceivedOn > today || form.ReceivedOn < form.InvoiceDate)
            return (new FieldRefusal(ReceivedField, "SupplierInvoice_ReceivedOnInvalid"), null);

        var lines = new List<SupplierInvoiceLineRequest>();
        for (var i = 0; i < form.Lines.Count; i++)
        {
            var line = form.Lines[i];
            if (!line.Included) continue;

            foreach (var (column, typed) in new[] { ("quantity", line.Quantity), ("price", line.UnitPrice), ("vat", line.VatPercent) })
            {
                if (TypedNumber.IsUnreadable(typed)) return (new FieldRefusal(LineField(column, i), "Result_NotANumber", typed!.Trim()), null);
            }

            if (TypedNumber.Parse(line.Quantity) is not { } quantity || quantity <= 0m)
                return (new FieldRefusal(LineField("quantity", i), "SupplierInvoice_QuantityMustBePositive"), null);
            if (TypedNumber.Parse(line.UnitPrice) is not { } price || price < 0m)
                return (new FieldRefusal(LineField("price", i), "SupplierInvoice_PriceMustNotBeNegative"), null);
            if (TypedNumber.Parse(line.VatPercent) is not { } vat || vat is < 0m or > 100m)
                return (new FieldRefusal(LineField("vat", i), "SupplierInvoice_VatRateOutOfRange"), null);

            lines.Add(new SupplierInvoiceLineRequest(line.Line.PurchaseOrderLineId, quantity, price, vat / 100m));
        }

        if (lines.Count == 0) return (new FieldRefusal(LinesField, "SupplierInvoice_NoLines"), null);

        return (null, new CreateSupplierInvoiceRequest
        {
            SupplierId = supplierId,
            InvoiceNumber = form.InvoiceNumber.Trim(),
            InvoiceDate = form.InvoiceDate,
            ReceivedOn = form.ReceivedOn,
            Notes = Blank(form.Notes),
            Lines = lines
        });
    }

    /// <summary>A scan chosen: a PDF or an image, not empty, not past the size the service keeps.</summary>
    public static FieldRefusal? CheckScan(string? fileName, long length)
    {
        var megabytes = (SupplierInvoiceFile.MaximumBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(fileName) || length <= 0) return new FieldRefusal(ScanField, "SupplierInvoice_ScanTooLarge", megabytes);
        if (!ScanExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant())) return new FieldRefusal(ScanField, "SupplierInvoice_ScanType");
        return length > SupplierInvoiceFile.MaximumBytes ? new FieldRefusal(ScanField, "SupplierInvoice_ScanTooLarge", megabytes) : null;
    }

    /// <summary>The box a refusal of the service names.</summary>
    public static string? FieldOf(Error? error) => error?.ResourceKey switch
    {
        "SupplierInvoice_NumberRequired" or "SupplierInvoice_NumberAlreadyEntered" => NumberField,
        "SupplierInvoice_DateInFuture" => InvoiceDateField,
        "SupplierInvoice_ReceivedOnInvalid" => ReceivedField,
        "SupplierInvoice_NoLines" or "SupplierInvoice_OrderLineNotFound" or "SupplierInvoice_OrderLineTwice" => LinesField,
        "SupplierInvoice_ScanTooLarge" or "SupplierInvoice_ScanType" => ScanField,
        "SupplierPayment_AmountMustBePositive" or "SupplierPayment_ExceedsOutstanding" => AmountField,
        "SupplierPayment_BeforeInvoice" or "SupplierPayment_DateInFuture" => PaidOnField,
        "SupplierPayment_ReferenceRequired" => ReferenceField,
        "SupplierPayment_MaturityBeforePayment" or "SupplierPayment_MaturityRequired" => MaturityField,
        _ => null
    };

    // ---- A payment ----------------------------------------------------------------------------------

    /// <summary>Paid today, the outstanding amount, by transfer.</summary>
    public static SupplierPaymentForm NewPayment(SupplierInvoiceRow row, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new SupplierPaymentForm
        {
            PaidOn = today,
            MaturityDate = today,
            Amount = row.AmountOutstanding.ToString("0.00", CultureInfo.InvariantCulture)
        };
    }

    /// <summary>An LCN or a post-dated cheque reaches the bank on its due date, which the bank reconciliation looks for it on.</summary>
    public static bool HasMaturity(SupplierPaymentMethod method) =>
        method is SupplierPaymentMethod.Cheque or SupplierPaymentMethod.BillOfExchange;

    /// <summary>
    /// A payment as the dialog checks it: an amount above zero, a reference unless in cash, a
    /// maturity not before the payment — and for an LCN after it: left on the payment date, its due
    /// date was not entered, and the bank reconciliation and the forecast would look for it on the
    /// wrong day.
    /// </summary>
    public static (FieldRefusal? Refusal, RecordSupplierPaymentRequest? Request) CheckPayment(SupplierPaymentForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (TypedNumber.IsUnreadable(form.Amount)) return (new FieldRefusal(AmountField, "Result_NotANumber", form.Amount!.Trim()), null);
        if (TypedNumber.Parse(form.Amount) is not { } amount || amount <= 0m)
            return (new FieldRefusal(AmountField, "SupplierPayment_AmountMustBePositive"), null);
        if (form.Method != SupplierPaymentMethod.Cash && string.IsNullOrWhiteSpace(form.Reference))
            return (new FieldRefusal(ReferenceField, "SupplierPayment_ReferenceRequired"), null);

        var withMaturity = HasMaturity(form.Method);
        var maturity = form.MaturityDate;
        if (withMaturity && maturity < form.PaidOn)
            return (new FieldRefusal(MaturityField, "SupplierPayment_MaturityBeforePayment"), null);

        var bill = form.Method == SupplierPaymentMethod.BillOfExchange;
        if (bill && maturity <= form.PaidOn) return (new FieldRefusal(MaturityField, "SupplierPayment_MaturityRequired"), null);

        return (null, new RecordSupplierPaymentRequest(form.PaidOn, amount, form.Method, Blank(form.Reference),
                                                       withMaturity && (bill || maturity > form.PaidOn) ? maturity : null));
    }

    // ---- A period -----------------------------------------------------------------------------------

    /// <summary>The last calendar quarter: the declaration of late payments is quarterly.</summary>
    public static PurchasingPeriodForm DefaultPeriod(DateOnly today)
    {
        var quarterStart = new DateOnly(today.Year, (today.Month - 1) / 3 * 3 + 1, 1).AddMonths(-3);
        return new PurchasingPeriodForm { From = quarterStart, To = quarterStart.AddMonths(3).AddDays(-1) };
    }

    public static FieldRefusal? CheckPeriod(PurchasingPeriodForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return form.From > form.To ? new FieldRefusal(PeriodField, "Purchasing_PeriodReversed") : null;
    }

    /// <summary>« retards-paiement-20260701-20260930.csv ».</summary>
    public static string LatePaymentsFileName(PurchasingPeriodForm period)
    {
        ArgumentNullException.ThrowIfNull(period);
        return $"retards-paiement-{period.From:yyyyMMdd}-{period.To:yyyyMMdd}.csv";
    }

    /// <summary>« export-achats-sage-20260701-20260930.csv ».</summary>
    public static string ExportFileName(PurchasingPeriodForm period)
    {
        ArgumentNullException.ThrowIfNull(period);
        return $"export-achats-{period.Layout.ToString().ToLowerInvariant()}-{period.From:yyyyMMdd}-{period.To:yyyyMMdd}.csv";
    }

    // ---- The purchasing parameters ------------------------------------------------------------------

    public static PurchasingSettingsForm SettingsForm(PurchasingSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        static string N(decimal v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        return new PurchasingSettingsForm
        {
            TolerancePercent = N(current.PriceTolerancePercent),
            ToleranceMad = N(current.PriceToleranceMad),
            KeyRate = N(current.KeyRatePercent),
            PenaltyMargin = N(current.PenaltyMarginPoints),
            FineFirstMonth = N(current.FineFirstMonthPercent),
            FineNextMonths = N(current.FineAdditionalMonthPercent),
            PurchaseJournal = current.PurchaseJournal,
            BankJournal = current.BankJournal,
            CashJournal = current.CashJournal,
            SupplierAccount = current.SupplierAccount,
            VatAccount = current.VatAccount,
            BankAccount = current.BankAccount,
            CashAccount = current.CashAccount,
            ChargeAccounts = Enum.GetValues<StockItemKind>().ToDictionary(k => k, k => (string?)current.ChargeAccountFor(k))
        };
    }

    /// <summary>
    /// The parameters as the dialog checks them: each rate a positive number, the percentages at
    /// most 100 — refused here, on the box, rather than by the service after the dialog had closed
    /// and lost what was typed —; a code left blank keeps the current one.
    /// </summary>
    public static (FieldRefusal? Refusal, PurchasingSettings? Settings) CheckSettings(PurchasingSettingsForm form, PurchasingSettings current)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(current);

        decimal? Rate(string? typed, decimal max) => TypedNumber.Parse(typed) is { } v && v >= 0m && v <= max ? v : null;

        var rates = new (string Name, string? Typed, decimal Max)[]
        {
            ("tolerance-percent", form.TolerancePercent, 100m), ("tolerance-mad", form.ToleranceMad, decimal.MaxValue),
            ("key-rate", form.KeyRate, 100m), ("penalty-margin", form.PenaltyMargin, 100m),
            ("fine-first", form.FineFirstMonth, 100m), ("fine-next", form.FineNextMonths, 100m)
        };
        foreach (var (name, typed, max) in rates)
        {
            if (Rate(typed, max) is null) return (new FieldRefusal(SettingField(name), "Purchasing_SettingOutOfRange"), null);
        }

        string Code(string? typed, string fallback) => Blank(typed) ?? fallback;

        return (null, current with
        {
            PriceTolerancePercent = Rate(form.TolerancePercent, 100m)!.Value,
            PriceToleranceMad = Rate(form.ToleranceMad, decimal.MaxValue)!.Value,
            KeyRatePercent = Rate(form.KeyRate, 100m)!.Value,
            PenaltyMarginPoints = Rate(form.PenaltyMargin, 100m)!.Value,
            FineFirstMonthPercent = Rate(form.FineFirstMonth, 100m)!.Value,
            FineAdditionalMonthPercent = Rate(form.FineNextMonths, 100m)!.Value,
            PurchaseJournal = Code(form.PurchaseJournal, current.PurchaseJournal),
            BankJournal = Code(form.BankJournal, current.BankJournal),
            CashJournal = Code(form.CashJournal, current.CashJournal),
            SupplierAccount = Code(form.SupplierAccount, current.SupplierAccount),
            VatAccount = Code(form.VatAccount, current.VatAccount),
            BankAccount = Code(form.BankAccount, current.BankAccount),
            CashAccount = Code(form.CashAccount, current.CashAccount),
            ChargeAccounts = Enum.GetValues<StockItemKind>().ToDictionary(
                k => k, k => Code(form.ChargeAccounts.GetValueOrDefault(k), current.ChargeAccountFor(k)))
        });
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
