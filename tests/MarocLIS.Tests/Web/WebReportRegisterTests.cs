using Bunit;
using FluentAssertions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Reports;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Reports;
using MarocLIS.Web.Components.Pages;
using MarocLIS.Web.Components.Screens.Reports;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Comptes rendus » in the browser: the three tabs and their counts, the payment warned about
/// before a report goes out, the hand-over form's checks, and what is offered by right.
/// </summary>
public sealed class WebReportRegisterTests : WebFinanceContext
{
    private readonly ReportRegisterRow _critical = Version("2610-000021", critical: true);
    private readonly ReportRegisterRow _unpaid = Version("2610-000022", "TAZI Mehdi", delivered: true,
                                                         payment: new OrderPayment(OrderPaymentState.PatientShareDue, 120m, "FA-2026-000045"));

    public WebReportRegisterTests()
    {
        ReportQueries.On(nameof(IReportQueries.GetPendingReleaseAsync), _ => (IReadOnlyList<PendingReportRow>)[Waiting()])
                     .On(nameof(IReportQueries.GetRegisterAsync), _ => (IReadOnlyList<ReportRegisterRow>)[_critical, _unpaid]);
    }

    [Fact]
    public void TheRegister_OpensOnTheIssuedTab_WithEachTabsCount_AndTheCriticalHandOverInRed()
    {
        SignIn(AppLanguage.French, Cashier);
        var page = Render<ReportRegister>();

        page.Find("[data-tab=Issued]").GetAttribute("aria-selected").Should().Be("true");
        page.FindAll("[data-grid=register] tbody tr").Should().HaveCount(2);
        page.Find("[data-tab=ToIssue]").TextContent.Should().Contain("1");
        page.Find("[data-tab=ToDeliver] .pill").GetAttribute("data-tone").Should().Be("Critical");

        page.Find("[data-tab=ToDeliver]").Click();
        page.FindAll("[data-grid=register] tbody tr").Should().ContainSingle()
            .Which.ClassList.Should().Contain("is-critical-row");
    }

    [Fact]
    public void FromTheDashboardsReportsToIssue_TheRegisterOpensOnToIssue_WhereThoseFilesAre()
    {
        SignIn(AppLanguage.French, Cashier);
        var page = Render<ReportRegister>(p => p.Add(x => x.Tab, "ToIssue"));

        page.Find("[data-tab=ToIssue]").GetAttribute("aria-selected").Should().Be("true");
        page.FindAll("[data-grid=pending] tbody tr").Should().ContainSingle();

        Render<ReportRegister>(p => p.Add(x => x.Tab, "nimporte")).Find("[data-tab=Issued]")
            .GetAttribute("aria-selected").Should().Be("true", "a tab unknown leaves the screen on its own first tab");
    }

    [Fact]
    public void AReprint_OfAnUnpaidFile_IsWarnedAbout_ThenAskedOfTheService()
    {
        SignIn(AppLanguage.French, Cashier);
        var page = Render<ReportRegister>();

        page.Find("[data-row='2610-000022-v1']").Click();
        page.Find("[data-action='Report.Reprint']").Click();

        page.Find("[data-prompt=confirm]").TextContent.Should().Contain(Mad(120m)).And.Contain("2610-000022");
        ReportStore.Calls.Should().BeEmpty("nothing is read before the operator answers");

        page.Find("[data-action='Action.Yes']").Click();
        ReportStore.CallsTo(nameof(Application.Abstractions.IRepository<LabReport>.GetByIdAsync)).Should().Be(1);
    }

    [Fact]
    public void TheHandOverForm_FollowsTheChannel_AndRefusesAThirdPartyWithoutConsent()
    {
        SignIn(AppLanguage.French, Cashier);
        var page = Render<ReportRegister>();

        page.Find("[data-action='Report.RecordDelivery']").Click();
        var form = page.Find("[data-modal=delivery]");

        // The prescriber asked for e-mail: the patient's address is filled in for the patient.
        page.Find("[data-field=channel] option[selected]").GetAttribute("value").Should().Be(nameof(DeliveryChannel.Email));
        page.Find("[data-field=identifier]").GetAttribute("value").Should().Be("k.elidrissi@example.ma");
        page.Find("[data-field=bearer]").HasAttribute("disabled").Should().BeTrue("an e-mail has no bearer");

        page.Find("[data-field=recipient-kind]").Change(nameof(ReportRecipientKind.AuthorizedThirdParty));
        page.Find("[data-field=recipient]").Input("ZIANI Rachid");
        page.Find("[data-field=identifier]").Input("r.ziani@example.ma");
        page.Find("[data-modal=delivery] [data-action='Report.RecordDelivery']").Click();

        page.Find("[data-refusal=consent]").Should().NotBeNull();
        ReportStore.Calls.Should().BeEmpty();
        form.Should().NotBeNull();
    }

    [Fact]
    public void WithTheRightToReadOnly_NothingIsIssuedReprintedOrHandedOver()
    {
        SignIn(AppLanguage.French, Permissions.Reports.View);
        var page = Render<ReportRegister>();

        page.FindAll("[data-action='Report.Issue']").Should().BeEmpty();
        page.FindAll("[data-action='Report.Reprint']").Should().BeEmpty();
        page.FindAll("[data-action='Report.RecordDelivery']").Should().BeEmpty();
        page.FindAll("[data-action='Report.IssueAmendment']").Should().BeEmpty();
    }

    [Fact]
    public void IssuingAWaitingFile_AsksFirst_NamingTheFile()
    {
        SignIn(AppLanguage.French, Cashier);
        var page = Render<ReportRegister>();

        page.Find("[data-tab=ToIssue]").Click();
        page.Find("[data-action='Report.Issue']").GetAttribute("data-shortcut").Should().Be("Ctrl+E");
        page.Find("[data-action='Report.Issue']").Click();

        page.Find("[data-prompt=confirm]").TextContent.Should().Contain("2610-000031");
    }

    [Fact]
    public void TheScreen_IsInArabic_ForAnArabicSession()
    {
        SignIn(AppLanguage.Arabic, Cashier);

        Render<ReportRegister>().Find("[data-tab=ToIssue]").TextContent.Should().MatchRegex("[؀-ۿ]");
    }

    [Fact]
    public void OpenedFromTheMenu_TheRegisterIsNotSearched()
    {
        SignIn(AppLanguage.French, Cashier);

        var page = Render<Screen>(p => p.Add(x => x.Key, "reports"));

        page.Find("[data-field=report-search]").GetAttribute("value").Should().BeNullOrEmpty();
        page.FindAll("[data-grid=register] tbody tr").Should().HaveCount(2);
        ReportQueries.Calls.Should().Contain(c => c.Method == nameof(IReportQueries.GetRegisterAsync) && c.Arguments[1] == null);
    }

    [Fact]
    public void AnOperatorWithoutTheRightToReadReports_TypingItsAddress_IsRefused()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Samples.Collect);

        var refused = Render<Screen>(p => p.Add(x => x.Key, "reports"));
        refused.Find("[data-state=refused]").TextContent.Should().Contain("Vous n'avez pas le droit");
        refused.FindAll("[data-screen]").Should().BeEmpty();
    }
}
