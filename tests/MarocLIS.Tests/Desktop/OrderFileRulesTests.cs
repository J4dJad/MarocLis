using FluentAssertions;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Samples;
using MarocLIS.Tests.Licensing;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>The rules of the « Dossier » screen, shared by the workstation and the web.</summary>
public sealed class OrderFileRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(1));

    internal static OrderItemDetail Item(LabOrderItemStatus status, int entered = 0, int runs = 1,
                                         DateTimeOffset? due = null, string code = "GLY") =>
        new(Guid.NewGuid(), Guid.NewGuid(), code, new MultilingualText(code), "BIO", status, OrderPriority.Routine,
            50m, 35m, 15m, "B", 30m, null, null, due, null, null, runs, runs > 1 ? "Contrôle" : null, null, null,
            false, 1, entered);

    internal static OrderSampleDetail Sample(SampleStatus status) =>
        new(Guid.NewGuid(), "T-0001", new MultilingualText("Sang"), new MultilingualText("Tube EDTA", "أنبوب EDTA"), "#8E44AD", status, 1, null, null, null, 1);

    internal static OrderDetail Detail(LabOrderStatus status = LabOrderStatus.Registered, Guid? invoice = null,
                                       Guid? prescriber = null, IReadOnlyList<OrderItemDetail>? items = null,
                                       IReadOnlyList<OrderSampleDetail>? samples = null) =>
        new(Guid.NewGuid(), "2610-000042", status, OrderPriority.Urgent, OrderOrigin.Walkin, Now.AddDays(-2), "Nadia",
            Now.AddHours(4), Guid.NewGuid(), "ALAMI Mohammed", null, new DateOnly(1980, 3, 2), Gender.Male, "AB123456",
            "PAT-0001", "+212612345678", prescriber, prescriber is null ? null : "Dr Bennani", null,
            prescriber is null ? "Dr Tazi" : null, null, "CNSS", 0.7m, null, null, null, true, null, null, null, null,
            AppLanguage.French, invoice, invoice is null ? null : "F-2026-0001", null, null,
            items ?? [Item(LabOrderItemStatus.Pending)], samples ?? [Sample(SampleStatus.AwaitingCollection)]);

    [Fact]
    public void The_quick_filters_keep_the_week_the_open_and_the_urgent_files()
    {
        OrderListRow Row(int daysAgo, LabOrderStatus status, OrderPriority priority) =>
            new(Guid.NewGuid(), "A", "P", Now.AddDays(-daysAgo), status, priority, 1);

        var old = Row(10, LabOrderStatus.Registered, OrderPriority.Routine);
        var done = Row(1, LabOrderStatus.Validated, OrderPriority.Routine);
        var urgent = Row(1, LabOrderStatus.InProgress, OrderPriority.Urgent);
        OrderListRow[] rows = [old, done, urgent];

        OrderFileRules.Filter(rows, new OrderListFilter(), Now).Should().Equal(rows);
        OrderFileRules.Filter(rows, new OrderListFilter(LastWeek: true), Now).Should().Equal(done, urgent);
        OrderFileRules.Filter(rows, new OrderListFilter(ActiveOnly: true), Now).Should().Equal(old, urgent);
        OrderFileRules.Filter(rows, new OrderListFilter(UrgentOnly: true), Now).Should().Equal(urgent);
    }

    [Fact]
    public void Urgent_work_still_to_do_is_in_the_warning_colours()
    {
        OrderFileRules.OrderPill(LabOrderStatus.InProgress, OrderPriority.Urgent)
            .Should().Be(new OrderPillLook(OrderStatusTone.Warning, true));
        OrderFileRules.OrderPill(LabOrderStatus.Validated, OrderPriority.Urgent)
            .Should().Be(new OrderPillLook(OrderStatusTone.Ok, false));
        OrderFileRules.OrderPill(LabOrderStatus.Cancelled, OrderPriority.VitalEmergency)
            .Should().Be(new OrderPillLook(OrderStatusTone.Critical, false));
        OrderFileRules.PriorityTone(OrderPriority.VitalEmergency).Should().Be(OrderStatusTone.Critical);

        OrderFileRules.ItemTone(Item(LabOrderItemStatus.InProgress, due: Now.AddHours(-1)), Now)
            .Should().Be(OrderStatusTone.Warning, "it is late");
        OrderFileRules.ItemTone(Item(LabOrderItemStatus.ResultEntered, entered: 1, runs: 2), Now)
            .Should().Be(OrderStatusTone.Warning, "it is on a second run");
        OrderFileRules.ItemTone(Item(LabOrderItemStatus.Validated, entered: 1, runs: 2), Now)
            .Should().Be(OrderStatusTone.Ok);
    }

    [Fact]
    public void Each_action_needs_its_right_and_a_file_that_takes_it()
    {
        var everything = new LicenceTestKit.User(true, Permissions.Orders.Edit, Permissions.Orders.Cancel,
                                                 Permissions.Samples.Reprint, Permissions.Reports.ReprintValidated,
                                                 Permissions.Results.Enter, Permissions.Billing.View);
        var entered = Item(LabOrderItemStatus.ResultEntered, entered: 1);
        var open = Detail(items: [entered]);

        var all = OrderFileRules.Actions(open, entered, everything);
        all.Should().Be(new OrderFileActions(true, true, true, true, true, true, true, true, true, false));

        var invoiced = OrderFileRules.Actions(Detail(invoice: Guid.NewGuid(), items: [entered]), entered, everything);
        invoiced.AddAnalyses.Should().BeFalse("an invoiced file takes no analysis");
        invoiced.LoadPayments.Should().BeTrue();

        var cancelled = OrderFileRules.Actions(Detail(LabOrderStatus.Cancelled), null, everything);
        cancelled.EditHeader.Should().BeFalse();
        cancelled.PrintLabels.Should().BeFalse();
        cancelled.CancelOrder.Should().BeFalse();
        cancelled.PrintReport.Should().BeTrue();

        var reader = OrderFileRules.Actions(open, entered, new LicenceTestKit.User(true, Permissions.Orders.View));
        reader.Should().Be(new OrderFileActions(false, false, false, false, false, false, false, true, false, false));

        OrderFileRules.Actions(null, null, everything).Should().Be(OrderFileActions.None with { ViewBilling = true });
    }

    [Fact]
    public void Tubes_items_dates_and_the_header_request()
    {
        var detail = Detail(samples: [Sample(SampleStatus.Received), Sample(SampleStatus.Rejected), Sample(SampleStatus.Discarded)],
                            items: [Item(LabOrderItemStatus.Cancelled), Item(LabOrderItemStatus.Pending, code: "NFS")]);

        OrderFileRules.LiveSamples(detail).Should().HaveCount(1);
        OrderFileRules.DefaultItem(detail, null)!.Code.Should().Be("NFS");
        OrderFileRules.DefaultItem(detail, detail.Items[0].ItemId).Should().Be(detail.Items[0]);

        var today = DateOnly.FromDateTime(Now.Date);
        OrderFileRules.MaxPrescriptionDate(detail, today).Should().Be(DateOnly.FromDateTime(Now.AddDays(-2).Date));

        var free = OrderFileRules.HeaderRequest(detail, "  Dr Idrissi ", null, OrderPriority.Routine, "", "", "", "", "",
                                                AppLanguage.Arabic);
        free.PrescriberId.Should().BeNull();
        free.PrescriberFreeText.Should().Be("Dr Idrissi");
        free.ReportLanguage.Should().Be(AppLanguage.Arabic);

        var directory = Detail(prescriber: Guid.NewGuid());
        OrderFileRules.HeaderRequest(directory, "Dr Bennani", null, OrderPriority.Urgent, null, null, null, null, null,
                                     AppLanguage.French).PrescriberFreeText.Should().BeNull();

        OrderFileRules.AmendmentProblems(0, " ").Should()
            .Equal("Order_AtLeastOneAnalysisRequired", "Order_AmendmentReasonRequired");
        OrderFileRules.AmendmentProblems(0, "Appel du médecin", needsAnalyses: false).Should().BeEmpty();
    }
}
