using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Reports;

namespace MarocLIS.Application.Reports;

/// <summary>The tabs of the « Comptes rendus » screen.</summary>
public enum ReportTab
{
    /// <summary>Files with validated results that no report carries yet.</summary>
    ToIssue = 0,

    /// <summary>Every version released in the period: the register.</summary>
    Issued = 1,

    /// <summary>The current final versions never handed over: the former « non remis » tick box.</summary>
    ToDeliver = 2
}

/// <summary>What « Comptes rendus » offers on the file or version selected.</summary>
public sealed record ReportRegisterActions(bool Issue, bool Reprint, bool RecordDelivery, bool Amend)
{
    public static readonly ReportRegisterActions None = new(false, false, false, false);
}

/// <summary>
/// A message and the values it names: <see cref="Amount"/> is written by each screen in its own
/// language; <see cref="InvoiceNumber"/> and <see cref="CoveredOrders"/> are quoted as they are.
/// </summary>
public sealed record PaymentNotice(string Key, decimal? Amount = null, string? InvoiceNumber = null, int CoveredOrders = 1);

/// <summary>
/// The rules of the « Comptes rendus » screen that need no window. They live in the application
/// layer so every screen that lists the register — the workstation today, the web client next —
/// applies the same ones.
/// </summary>
public static class ReportRegisterRules
{
    /// <summary>
    /// The tab the screen opens on: the register, as before the redesign. The register was the
    /// screen's main list, and the one Ctrl+P printed; « À émettre » stays one click away, its
    /// count shown on the tab.
    /// </summary>
    public const ReportTab DefaultTab = ReportTab.Issued;

    /// <summary>
    /// The register rows a tab lists. « À remettre » is the former « Uniquement les comptes rendus
    /// non remis » tick box, which the query answered with exactly this test.
    /// </summary>
    public static IReadOnlyList<ReportRegisterRow> RegisterRows(IReadOnlyList<ReportRegisterRow> all, ReportTab tab) =>
        tab == ReportTab.ToDeliver ? [.. all.Where(r => r.AwaitingDelivery)] : all;

    public static int Count(IReadOnlyList<PendingReportRow> pending, IReadOnlyList<ReportRegisterRow> register,
                            ReportTab tab) => tab switch
    {
        ReportTab.ToIssue => pending.Count,
        ReportTab.ToDeliver => register.Count(r => r.AwaitingDelivery),
        _ => register.Count
    };

    /// <summary>Whether a tab carries a report somebody has to act on today: a critical value not handed over.</summary>
    public static bool IsUrgent(IReadOnlyList<ReportRegisterRow> register, ReportTab tab) =>
        tab == ReportTab.ToDeliver && register.Any(r => r.IsUrgentToDeliver);

    /// <summary>
    /// What a release of a waiting file will be: final once no analysis is awaited, partial
    /// otherwise, and its version number after the ones already released.
    /// </summary>
    public static (bool Final, int NextVersion) NextRelease(PendingReportRow row) =>
        (row.AwaitingAnalyses == 0, row.ReleasedVersions + 1);

    /// <summary>Maps the prescriber's preference onto the channel that carries it.</summary>
    public static DeliveryChannel DefaultChannel(ReportDeliveryPreference preference) =>
        preference switch
        {
            ReportDeliveryPreference.Email => DeliveryChannel.Email,
            ReportDeliveryPreference.Fax => DeliveryChannel.Fax,
            ReportDeliveryPreference.SmsLink => DeliveryChannel.SmsLink,
            ReportDeliveryPreference.Courier => DeliveryChannel.Courier,
            ReportDeliveryPreference.ElectronicInterface => DeliveryChannel.ElectronicInterface,
            _ => DeliveryChannel.PrintedAtCounter
        };

    /// <summary>
    /// What may be done on the selection: issue a waiting file (reports.print), reprint the stored
    /// document of a version (reports.reprint), record a hand-over of a current version (print or
    /// send), and rectify the current version (reports.amend). A version replaced by a newer one is
    /// neither handed over nor amended any more: the service refuses both.
    /// </summary>
    public static ReportRegisterActions Actions(PendingReportRow? pending, ReportRegisterRow? version, ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new ReportRegisterActions(
            Issue: pending is not null && user.Has(Permissions.Reports.Print),
            Reprint: version is { HasStoredPdf: true } && user.Has(Permissions.Reports.ReprintValidated),
            RecordDelivery: version is { IsSuperseded: false }
                            && (user.Has(Permissions.Reports.Print) || user.Has(Permissions.Reports.Send)),
            Amend: version is { IsSuperseded: false } && user.Has(Permissions.Reports.Amend));
    }

    /// <summary>
    /// The patient's payment status in a cell: « Payé », « Reste 120,00 DH », « Non facturé »; to
    /// a user without billing.view, who gets no amount, « Payé » or « Non payé ». Null for « — ».
    /// </summary>
    public static PaymentNotice? PaymentStatus(OrderPayment? payment) => payment switch
    {
        { State: OrderPaymentState.Paid } => new PaymentNotice("Report.PaymentPaid"),
        { State: OrderPaymentState.PatientShareDue, PatientDue: { } due } => new PaymentNotice("Report.PaymentDue", due),
        { State: OrderPaymentState.NotInvoiced, PatientDue: not null } => new PaymentNotice("Report.PaymentNotInvoiced"),
        { IsSettled: false } => new PaymentNotice("Report.PaymentUnpaid"),
        _ => null
    };

    /// <summary>
    /// What is said before a report goes out on an order whose patient share is not settled, or
    /// null when it is: a warning, never a refusal — a report owed to the prescriber goes to them
    /// whatever the patient paid — and the answer is logged with the act by the service.
    /// </summary>
    public static PaymentNotice? UnpaidWarning(OrderPayment? payment) => payment switch
    {
        null or { IsSettled: true } => null,
        { State: OrderPaymentState.NotInvoiced, PatientDue: { } estimate } =>
            new PaymentNotice("Report.UnpaidNotInvoiced", estimate),
        { PatientDue: { } due, InvoiceNumber: { } number, CoveredOrders: > 1 } =>
            new PaymentNotice("Report.UnpaidDueShared", due, number, payment.CoveredOrders),
        { State: OrderPaymentState.PatientShareDue, PatientDue: { } due } =>
            new PaymentNotice("Report.UnpaidDue", due),
        _ => new PaymentNotice("Report.UnpaidNoAmount")
    };

    /// <summary>
    /// The flags of a version, as resource keys, in the order the register prints them: replaced,
    /// rectified (and how), critical, with reservations, subcontracted, partly outside the scope of
    /// accreditation.
    /// </summary>
    public static IReadOnlyList<string> Flags(ReportRegisterRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var flags = new List<string>(4);
        if (row.IsSuperseded) flags.Add("Report.Superseded");
        if (row.IsAmendment) flags.Add($"ReportAmendmentKind.{row.AmendmentKind}");
        if (row.ContainsCriticalResults) flags.Add("Flag.Critical");
        if (row.ContainsReservations) flags.Add("Report.WithReservations");
        if (row.ContainsSubcontractedResults) flags.Add("Catalog.Subcontracted");
        if (!row.AllResultsAccredited) flags.Add("Report.PartlyOutsideScope");
        return flags;
    }
}
