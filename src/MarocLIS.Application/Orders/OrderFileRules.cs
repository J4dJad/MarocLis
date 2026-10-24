using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Samples;

namespace MarocLIS.Application.Orders;

/// <summary>The quick filters of the file list: the last seven days, the files still open, the urgent ones.</summary>
public sealed record OrderListFilter(bool LastWeek = false, bool ActiveOnly = false, bool UrgentOnly = false);

/// <summary>How a file's state pill reads: its tone, and whether the priority is named beside the state.</summary>
public sealed record OrderPillLook(OrderStatusTone Tone, bool NamesPriority);

/// <summary>What the file screen offers on the file and on the analysis it points at.</summary>
public sealed record OrderFileActions(
    bool AddAnalyses,
    bool EditHeader,
    bool CancelOrder,
    bool PrintLabels,
    bool PrintReport,
    bool CancelItem,
    bool RepeatItem,
    bool MoreActions,
    bool ViewBilling,
    bool LoadPayments)
{
    public static readonly OrderFileActions None = new(false, false, false, false, false, false, false, false, false, false);
}

/// <summary>
/// The rules of the « Dossier » screen that need no window: which files a quick filter keeps, how a
/// state reads, and which action the operator is offered. They live in the application layer so the
/// workstation and the web client offer a file the same way; the services remain the authority.
/// </summary>
public static class OrderFileRules
{
    /// <summary>How far back « Cette semaine » reaches.</summary>
    public const int WeekDays = 7;

    /// <summary>The files the quick filters let through, in the list's order.</summary>
    public static IReadOnlyList<OrderListRow> Filter(IEnumerable<OrderListRow> rows, OrderListFilter filter,
                                                     DateTimeOffset now)
    {
        var since = now.AddDays(-WeekDays);

        return
        [
            .. rows.Where(r => (!filter.LastWeek || r.RegisteredAt >= since)
                               && (!filter.ActiveOnly || r.Status is not (LabOrderStatus.Validated
                                                                          or LabOrderStatus.Reported
                                                                          or LabOrderStatus.Cancelled))
                               && (!filter.UrgentOnly || r.Priority != OrderPriority.Routine))
        ];
    }

    /// <summary>
    /// A file's state pill: « Validé » in green, « En cours · Urgent » in amber. Urgent work still to
    /// do is flagged in the warning colours; done or cancelled, it no longer matters.
    /// </summary>
    public static OrderPillLook OrderPill(LabOrderStatus status, OrderPriority priority)
    {
        var released = status is LabOrderStatus.Validated or LabOrderStatus.Reported;

        if (priority != OrderPriority.Routine && !released && status != LabOrderStatus.Cancelled)
            return new OrderPillLook(OrderStatusTone.Warning, NamesPriority: true);

        return new OrderPillLook(OrderFileTones.ForOrder(status), NamesPriority: false);
    }

    /// <summary>The tone of the priority pill of the file's header.</summary>
    public static OrderStatusTone PriorityTone(OrderPriority priority) =>
        priority == OrderPriority.VitalEmergency ? OrderStatusTone.Critical : OrderStatusTone.Warning;

    /// <summary>
    /// An analysis's state pill: late, or on a second run not yet released, in the warning colours;
    /// otherwise the tone of its state.
    /// </summary>
    public static OrderStatusTone ItemTone(OrderItemDetail item, DateTimeOffset now)
    {
        var released = item.Status is LabOrderItemStatus.Validated or LabOrderItemStatus.Reported;

        if (item.IsOverdueAt(now) || (item.RunCount > 1 && !item.IsCancelled && !released))
            return OrderStatusTone.Warning;

        return OrderFileTones.ForItem(item.Status);
    }

    /// <summary>The file's tubes still in use: the refused and the discarded ones left out.</summary>
    public static IReadOnlyList<OrderSampleDetail> LiveSamples(OrderDetail detail) =>
        [.. detail.Samples.Where(s => s.Status is not (SampleStatus.Rejected or SampleStatus.Discarded))];

    /// <summary>
    /// The analysis an action points at once the file is shown: the one acted on, when it is still
    /// on the file, else the first not cancelled — the cancelled ones are history.
    /// </summary>
    public static OrderItemDetail? DefaultItem(OrderDetail detail, Guid? kept) =>
        detail.Items.FirstOrDefault(i => kept is { } id && i.ItemId == id)
        ?? detail.Items.FirstOrDefault(i => !i.IsCancelled);

    /// <summary>
    /// What is offered. Off rather than refused afterwards: an invoiced or reported file takes no
    /// analysis (<see cref="OrderDetail.CanAddAnalyses"/>), a released analysis is not cancelled,
    /// a cancelled file is not corrected — and each needs its right.
    /// </summary>
    public static OrderFileActions Actions(OrderDetail? detail, OrderItemDetail? item, ICurrentUser user)
    {
        if (detail is null) return OrderFileActions.None with { ViewBilling = user.Has(Permissions.Billing.View) };

        var editable = !detail.IsCancelled;
        var billing = user.Has(Permissions.Billing.View);

        return new OrderFileActions(
            AddAnalyses: detail.CanAddAnalyses && user.Has(Permissions.Orders.Edit),
            EditHeader: editable && user.Has(Permissions.Orders.Edit),
            CancelOrder: detail.CanCancel && user.Has(Permissions.Orders.Cancel),
            PrintLabels: editable && user.Has(Permissions.Samples.Reprint),
            PrintReport: user.Has(Permissions.Reports.ReprintValidated),
            CancelItem: item is { CanCancel: true } && user.Has(Permissions.Orders.Cancel),
            RepeatItem: item is { CanRepeat: true } && user.Has(Permissions.Results.Enter),
            MoreActions: true,
            ViewBilling: billing,
            LoadPayments: billing && detail.InvoiceId is not null);
    }

    /// <summary>
    /// The latest date a prescription can bear: not after the file's registration, as the service
    /// requires, nor after today.
    /// </summary>
    public static DateOnly MaxPrescriptionDate(OrderDetail detail, DateOnly today)
    {
        var registered = DateOnly.FromDateTime(detail.RegisteredAt.Date);
        return registered < today ? registered : today;
    }

    /// <summary>
    /// The header as the correction sends it. A directory prescriber keeps its id and carries no
    /// free text: storing the name alongside the id would give the report two sources for one fact.
    /// </summary>
    public static AmendOrderHeaderRequest HeaderRequest(OrderDetail detail, string? prescriberText,
                                                        DateOnly? prescriptionDate, OrderPriority priority,
                                                        string? clinicalInformation, string? diagnosisCode,
                                                        string? wardOrUnit, string? roomOrBed,
                                                        string? internalNote, Domain.Common.AppLanguage reportLanguage) => new()
    {
        OrderId = detail.OrderId,
        PrescriberId = detail.PrescriberId,
        PrescriberFreeText = detail.PrescriberId is not null
            ? null
            : string.IsNullOrWhiteSpace(prescriberText) ? null : prescriberText.Trim(),
        PrescriptionDate = prescriptionDate,
        Priority = priority,
        ClinicalInformation = clinicalInformation,
        DiagnosisCode = diagnosisCode,
        WardOrUnit = wardOrUnit,
        RoomOrBed = roomOrBed,
        InternalNote = internalNote,
        ReportLanguage = reportLanguage
    };

    /// <summary>
    /// What a correction of the request lacks before it is sent, as text keys: the analyses to add
    /// (none for a header correction) and the reason ISO 15189 asks for.
    /// </summary>
    public static IReadOnlyList<string> AmendmentProblems(int chosen, string? reason, bool needsAnalyses = true)
    {
        var problems = new List<string>();
        if (needsAnalyses && chosen == 0) problems.Add("Order_AtLeastOneAnalysisRequired");
        if (string.IsNullOrWhiteSpace(reason)) problems.Add("Order_AmendmentReasonRequired");
        return problems;
    }

    /// <summary>The longest reason a cancellation or a repeat takes, as the order's column.</summary>
    public const int ReasonMaxLength = 500;
}
