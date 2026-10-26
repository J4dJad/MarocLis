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

        // The states are said as « true » / « false », as a screen reader expects them, not as a bare attribute.
        page.Find("[data-tab=Tubes]").GetAttribute("aria-selected").Should().Be("true");
        page.Find("[data-tab=Analyses]").GetAttribute("aria-selected").Should().Be("false");
        page.FindAll("[data-filter]").Select(c => c.GetAttribute("aria-pressed")).Should().OnlyContain(v => v == "true" || v == "false");
    }

    /// <summary>
    /// « ?recherche= » (a file's number, from another screen) opens that file, not the first of the
    /// day with the list filtered beside it.
    /// </summary>
    [Fact]
    public void ASearchGivenToTheScreen_OpensThatFile()
    {
        SignIn(AppLanguage.French, Permissions.Orders.View);
        var other = Detail(items: [Item(LabOrderItemStatus.Pending, code: "NFS")], samples: [Sample(SampleStatus.Received)])
                    with { OrderId = Guid.NewGuid(), AccessionNumber = "2610-000041" };
        Orders.On(nameof(IOrderQueries.ResolveOrderIdAsync), args => (string)args[0]! == "2610-000041" ? other.OrderId : (Guid?)null)
              .On(nameof(IOrderQueries.GetDetailAsync), args => (Guid)args[0]! == other.OrderId ? other
                                                                : (Guid)args[0]! == _file.OrderId ? _file : null);

        var page = Render<OrderFile>(p => p.Add(x => x.Search, "2610-000041"));

        page.Find("[data-file]").GetAttribute("data-file").Should().Be("2610-000041");
        page.Find("[data-field=order-search]").GetAttribute("value").Should().Be("2610-000041");
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
    public void TheActions_AreInOneBar_ThePrimaryAtTheEnd_AndTheCancellationSetApart()
    {
        SignIn(AppLanguage.French, Permissions.Orders.View, Permissions.Orders.Edit, Permissions.Orders.Cancel);

        var page = Open();

        page.FindAll("[data-bar=actions]").Should().ContainSingle();
        page.Find(".action-bar-end [data-action='Order.AddAnalyses']").ClassList.Should().Contain("btn-primary");
        page.Find(".more-menu [data-action='Order.CancelOrder']").ClassList.Should().Contain("btn-danger");
        page.FindAll("[data-action='Order.EditHeader']").Should().ContainSingle("an action is offered once");
    }

    [Fact]
    public void TheFile_IsInArabic_ForAnArabicSession()
    {
        SignIn(AppLanguage.Arabic, Permissions.Orders.View);

        var page = Open();

        page.Find("[data-tab=Analyses]").TextContent.Should().MatchRegex("[؀-ۿ]");
        page.FindAll(".fact-value").Should().NotBeEmpty().And.OnlyContain(v => v.QuerySelector("bdi") != null,
            "names and clinical information typed in French keep their own direction in Arabic");

        // The tube is named in Arabic too, not « Tube EDTA » as typed in French.
        page.Find("[data-tab=Tubes]").Click();
        page.Find("[data-grid=samples] tbody tr").TextContent.Should().Contain("أنبوب EDTA").And.NotContain("Tube EDTA");
    }
}
