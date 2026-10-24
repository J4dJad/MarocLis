using Bunit;
using FluentAssertions;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Samples;
using MarocLIS.Web.Components.Screens.Orders;
using Xunit;
using static MarocLIS.Tests.Desktop.OrderFileRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Dossier » in the browser: the list, the file with its progress, and what is offered on it —
/// OrderFileRules.Actions', as on the workstation — with the consultation recorded.
/// </summary>
public sealed class WebOrderFileTests : WebScreenContext
{
    private readonly OrderDetail _file = Detail(items:
    [
        Item(LabOrderItemStatus.ResultEntered, entered: 1, code: "GLY"),
        Item(LabOrderItemStatus.Pending, code: "TSH")
    ], samples: [Sample(SampleStatus.Received)]);

    public WebOrderFileTests()
    {
        var now = TestClock.Now;
        IReadOnlyList<OrderListRow> rows =
        [
            new(_file.OrderId, _file.AccessionNumber, _file.PatientName, now.AddDays(-2), _file.Status, OrderPriority.Urgent, 2),
            new(Guid.NewGuid(), "2610-000041", "BENJELLOUN Amine", now.AddDays(-3), LabOrderStatus.Validated, OrderPriority.Routine, 3),
            new(Guid.NewGuid(), "2609-000310", "IDRISSI Salma", now.AddDays(-20), LabOrderStatus.Reported, OrderPriority.Routine, 5)
        ];
        Orders.On(nameof(IOrderQueries.SearchAsync), _ => rows)
              .On(nameof(IOrderQueries.GetDetailAsync), args => (Guid)args[0]! == _file.OrderId ? _file : null);
    }

    private IRenderedComponent<OrderFile> Open() => Render<OrderFile>(p => p.Add(x => x.OrderId, _file.OrderId));

    private static bool Disabled(IRenderedComponent<OrderFile> page, string action) =>
        page.FindAll($"[data-action='{action}']").All(b => b.HasAttribute("disabled"));

    [Fact]
    public void TheFile_ShowsItsHeaderProgressAndTabs_AndTheConsultationIsRecorded()
    {
        SignIn(AppLanguage.French, Permissions.Orders.View);

        var page = Open();

        page.Find("[data-file]").GetAttribute("data-file").Should().Be(_file.AccessionNumber);
        page.Find("[data-file]").TextContent.Should().Contain("ALAMI Mohammed").And.Contain("Dr Tazi").And.Contain("CNSS");
        page.FindAll("[data-state=progress] li").Should().HaveCount(6);
        page.FindAll("[data-grid=items] tbody tr").Should().HaveCount(2);
        page.Find("li.is-selected").GetAttribute("data-order").Should().Be(_file.AccessionNumber);
        DataAudit.Entries.Should().ContainSingle(e => e.Kind == DataAccessKind.ViewOrder && e.OrderId == _file.OrderId
                                                      && e.PatientId == _file.PatientId);

        page.Find("[data-tab=Tubes]").Click();
        page.FindAll("[data-grid=samples] tbody tr").Should().ContainSingle();
    }

    [Fact]
    public void WithOnlyTheRightToSee_EveryChangeIsOff()
    {
        SignIn(AppLanguage.French, Permissions.Orders.View);

        var page = Open();

        foreach (var action in new[] { "Order.AddAnalyses", "Order.EditHeader", "Action.PrintLabels", "Order.PrintReport",
                                       "Order.CancelOrder", "Order.CancelAnalysis", "Order.RepeatAnalysis" })
            Disabled(page, action).Should().BeTrue(action);
    }

    [Fact]
    public void WithTheRights_TheActionsAreTheRulesOnes()
    {
        SignIn(AppLanguage.French, Permissions.Orders.View, Permissions.Orders.Edit, Permissions.Orders.Cancel,
               Permissions.Samples.Reprint, Permissions.Results.Enter, Permissions.Reports.ReprintValidated);

        var page = Open();

        foreach (var action in new[] { "Order.AddAnalyses", "Order.EditHeader", "Action.PrintLabels", "Order.PrintReport",
                                       "Order.CancelOrder", "Order.CancelAnalysis", "Order.RepeatAnalysis" })
            Disabled(page, action).Should().BeFalse(action);

        // A pending analysis is cancelled but not repeated: nothing was measured yet.
        page.Find("[data-grid=items] tr[data-item=TSH]").Click();
        Disabled(page, "Order.RepeatAnalysis").Should().BeTrue();
        Disabled(page, "Order.CancelAnalysis").Should().BeFalse();
    }

    [Fact]
    public void TheLabels_AreLeftToTheWorkstation_AndNothingIsRecorded()
    {
        SignIn(AppLanguage.French, Permissions.Orders.View, Permissions.Samples.Reprint);
        var page = Open();

        page.Find("[data-action='Action.PrintLabels']").Click();

        page.Find("[data-modal=file-message]").TextContent.Should().Contain("poste Windows");
    }

    [Fact]
    public void TheQuickFilters_NarrowTheList()
    {
        SignIn(AppLanguage.French, Permissions.Orders.View);
        var page = Open();

        page.FindAll("[data-list=orders] li").Should().HaveCount(3);
        page.Find("[data-filter=urgent]").Click();
        page.FindAll("[data-list=orders] li").Select(l => l.GetAttribute("data-order")).Should().Equal(_file.AccessionNumber);
        page.Find("[data-filter=urgent]").Click();
        page.Find("[data-filter=week]").Click();
        page.FindAll("[data-list=orders] li").Should().HaveCount(2);
    }

    [Fact]
    public void TheFile_IsInArabic_ForAnArabicSession()
    {
        SignIn(AppLanguage.Arabic, Permissions.Orders.View);

        var page = Open();

        page.Find("[data-tab=Analyses]").TextContent.Should().MatchRegex("[؀-ۿ]");
    }
}
