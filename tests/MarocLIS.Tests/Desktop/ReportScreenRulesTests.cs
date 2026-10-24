using FluentAssertions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Reports;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Reports;
using MarocLIS.Tests.Licensing;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Comptes rendus », of its hand-over form and of its rectification form, moved out
/// of the workstation's view and dialogs, now shared with the web.
/// </summary>
public sealed class ReportScreenRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(1));

    internal static ReportRegisterRow Version(bool superseded = false, bool stored = true, bool critical = false,
                                              ReportAmendmentKind amendment = ReportAmendmentKind.None,
                                              OrderPayment? payment = null, string? prescriber = "Dr BENNANI Karim") =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "2610-000010", "ALAMI Mohammed", "العلمي محمد", "+212662345678", 1,
            ReportCompleteness.Final, amendment, null, AppLanguage.French, Now, "Dr Leila Berrada", critical, true, false, false,
            stored, 2, superseded ? Guid.NewGuid() : null, 0, null, prescriber, ReportDeliveryPreference.Email,
            "m.alami@example.ma", "dr.bennani@example.ma", payment);

    internal static PendingReportRow Waiting(OrderPayment? payment = null) =>
        new(Guid.NewGuid(), "2610-000011", "BERRADA Youssef", null, OrderPriority.Urgent, 5, 0, Now, 0, "Dr TAZI Omar", payment);

    [Fact]
    public void The_actions_follow_the_rights_and_a_replaced_version_is_neither_handed_over_nor_amended()
    {
        var everything = new LicenceTestKit.User(true, Permissions.Reports.Print, Permissions.Reports.ReprintValidated,
                                                 Permissions.Reports.Amend);
        var sender = new LicenceTestKit.User(true, Permissions.Reports.Send);
        var viewer = new LicenceTestKit.User(true, Permissions.Reports.View);

        ReportRegisterRules.Actions(Waiting(), null, everything).Should().Be(new ReportRegisterActions(true, false, false, false));
        ReportRegisterRules.Actions(null, Version(), everything).Should().Be(new ReportRegisterActions(false, true, true, true));
        ReportRegisterRules.Actions(null, Version(superseded: true), everything)
                           .Should().Be(new ReportRegisterActions(false, true, false, false));
        ReportRegisterRules.Actions(null, Version(stored: false), everything).Reprint.Should().BeFalse();
        ReportRegisterRules.Actions(null, Version(), sender).Should().Be(new ReportRegisterActions(false, false, true, false));
        ReportRegisterRules.Actions(Waiting(), Version(), viewer).Should().Be(ReportRegisterActions.None);
    }

    [Fact]
    public void The_payment_status_and_the_warning_before_a_hand_over_name_what_the_patient_owes()
    {
        ReportRegisterRules.PaymentStatus(null).Should().BeNull();
        ReportRegisterRules.PaymentStatus(OrderPayment.NothingDue).Should().BeNull();
        ReportRegisterRules.PaymentStatus(new OrderPayment(OrderPaymentState.Paid, 0m, "FA-2026-000012"))!.Key.Should().Be("Report.PaymentPaid");
        ReportRegisterRules.PaymentStatus(new OrderPayment(OrderPaymentState.PatientShareDue, 120m, "FA-2026-000012"))
                           .Should().Be(new PaymentNotice("Report.PaymentDue", 120m));
        ReportRegisterRules.PaymentStatus(new OrderPayment(OrderPaymentState.NotInvoiced, 80m))!.Key.Should().Be("Report.PaymentNotInvoiced");
        ReportRegisterRules.PaymentStatus(new OrderPayment(OrderPaymentState.PatientShareDue, 120m).WithoutAmounts())!
                           .Key.Should().Be("Report.PaymentUnpaid", "without billing.view no amount is said");

        ReportRegisterRules.UnpaidWarning(new OrderPayment(OrderPaymentState.Paid, 0m)).Should().BeNull();
        ReportRegisterRules.UnpaidWarning(new OrderPayment(OrderPaymentState.NotInvoiced, 80m))
                           .Should().Be(new PaymentNotice("Report.UnpaidNotInvoiced", 80m));
        ReportRegisterRules.UnpaidWarning(new OrderPayment(OrderPaymentState.PatientShareDue, 120m, "FA-2026-000012", 2))
                           .Should().Be(new PaymentNotice("Report.UnpaidDueShared", 120m, "FA-2026-000012", 2));
        ReportRegisterRules.UnpaidWarning(new OrderPayment(OrderPaymentState.PatientShareDue, 120m, "FA-2026-000012"))
                           .Should().Be(new PaymentNotice("Report.UnpaidDue", 120m));
        ReportRegisterRules.UnpaidWarning(new OrderPayment(OrderPaymentState.PatientShareDue, 120m).WithoutAmounts())!
                           .Key.Should().Be("Report.UnpaidNoAmount");
    }

    [Fact]
    public void The_flags_of_a_version_come_in_the_registers_order()
    {
        ReportRegisterRules.Flags(Version(superseded: true, critical: true, amendment: ReportAmendmentKind.ResultCorrection))
                           .Should().Equal("Report.Superseded", "ReportAmendmentKind.ResultCorrection", "Flag.Critical",
                                           "Report.WithReservations", "Report.PartlyOutsideScope");
    }

    [Fact]
    public void The_hand_over_form_fills_the_recipient_and_the_address_the_channel_needs()
    {
        var row = Version();

        ReportDeliveryRules.RecipientName(ReportRecipientKind.Patient, row).Should().Be("ALAMI Mohammed");
        ReportDeliveryRules.RecipientName(ReportRecipientKind.Prescriber, row).Should().Be("Dr BENNANI Karim");
        ReportDeliveryRules.RecipientName(ReportRecipientKind.AuthorizedThirdParty, row).Should().BeEmpty();

        ReportDeliveryRules.Contact(DeliveryChannel.Email, ReportRecipientKind.Prescriber, row).Should().Be("dr.bennani@example.ma");
        ReportDeliveryRules.Contact(DeliveryChannel.SmsLink, ReportRecipientKind.Patient, row).Should().Be("+212662345678");
        ReportDeliveryRules.Contact(DeliveryChannel.Fax, ReportRecipientKind.Prescriber, row).Should().BeNull();

        ReportDeliveryRules.TakesBearer(DeliveryChannel.PrintedAtCounter).Should().BeTrue();
        ReportDeliveryRules.TakesBearer(DeliveryChannel.Email).Should().BeFalse();
        ReportDeliveryRules.ChannelNoticeKey(DeliveryChannel.Email, emailReady: false, smsReady: true)
                           .Should().Be("Report.EmailChannelNotConfigured");
        ReportDeliveryRules.ChannelNoticeKey(DeliveryChannel.SmsLink, emailReady: false, smsReady: true)
                           .Should().Be("Report.SmsChannelLive");
    }

    [Fact]
    public void The_hand_over_form_refuses_what_the_record_could_not_answer_for()
    {
        var consent = Guid.NewGuid();

        ReportDeliveryRules.Check(DeliveryChannel.PrintedAtCounter, ReportRecipientKind.Patient, " ", null, null, null, null)
                           .Refusal!.Key.Should().Be("Report_RecipientNameRequired");
        ReportDeliveryRules.Check(DeliveryChannel.Email, ReportRecipientKind.Prescriber, "Dr BENNANI", "", null, null, null)
                           .Refusal!.Field.Should().Be("identifier");
        ReportDeliveryRules.Check(DeliveryChannel.PrintedAtCounter, ReportRecipientKind.AuthorizedThirdParty, "ALAMI Rachid",
                                  null, null, null, "pas un identifiant").Refusal!.Key.Should().Be("Report_ThirdPartyRequiresConsent");

        var noCin = ReportDeliveryRules.Check(DeliveryChannel.PrintedAtCounter, ReportRecipientKind.AuthorizedThirdParty,
                                              "ALAMI Rachid", null, "ALAMI Rachid", " ", consent.ToString());
        noCin.Refusal!.Key.Should().Be("Report_BearerRequired");
        noCin.ConsentId.Should().Be(consent);

        ReportDeliveryRules.Check(DeliveryChannel.PrintedAtCounter, ReportRecipientKind.AuthorizedThirdParty, "ALAMI Rachid",
                                  null, "ALAMI Rachid", "BK112233", $"{{{consent}}}")
                           .Should().Be(((Application.Input.FieldRefusal?)null, (Guid?)consent));
        ReportDeliveryRules.Check(DeliveryChannel.PrintedAtCounter, ReportRecipientKind.Patient, "ALAMI Mohammed",
                                  null, null, null, null).Should().Be(((Application.Input.FieldRefusal?)null, (Guid?)null));
    }

    [Fact]
    public void A_rectification_says_what_it_is_and_an_identity_correction_is_confirmed()
    {
        ReportDeliveryRules.AmendmentKinds.Should().NotContain(ReportAmendmentKind.None);
        ReportDeliveryRules.AmendmentNoticeKey(ReportAmendmentKind.Addition).Should().Be("Report.AdditionNotice");
        ReportDeliveryRules.AmendmentNeedsConfirmation(ReportAmendmentKind.IdentityCorrection).Should().BeTrue();
        ReportDeliveryRules.AmendmentNeedsConfirmation(ReportAmendmentKind.Retraction).Should().BeTrue();
        ReportDeliveryRules.AmendmentNeedsConfirmation(ReportAmendmentKind.ResultCorrection).Should().BeFalse();
        ReportDeliveryRules.CheckAmendment("  ")!.Key.Should().Be("Report_AmendmentReasonRequired");
        ReportDeliveryRules.CheckAmendment("Kaliémie corrigée après recontrôle").Should().BeNull();
    }
}
