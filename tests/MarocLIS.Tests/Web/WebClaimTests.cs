using Bunit;
using FluentAssertions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Web.Components.Screens.Billing;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Tiers payant » in the browser: what blocks a claim, one payer's ready invoices ticked, a
/// mixed selection refused, the batches to chase — and what is offered by right.
/// </summary>
public sealed class WebClaimTests : WebFinanceContext
{
    private readonly Guid _cnops = Guid.NewGuid(), _amo = Guid.NewGuid();

    public WebClaimTests()
    {
        BillingQueries.On(nameof(IBillingQueries.GetClaimCandidatesAsync), _ => (IReadOnlyList<ClaimCandidateRow>)
        [
            Candidate("FA-2026-000040", _cnops), Candidate("FA-2026-000041", _cnops, blocked: true),
            Candidate("FA-2026-000042", _amo, "AMO CNSS", days: 75)
        ])
        .On(nameof(IBillingQueries.GetClaimBatchesAsync), _ => (IReadOnlyList<ClaimBatchRow>)
        [
            Batch("BT-CNOPS-2026-0007", pending: 3, ageDays: 75), Batch("BT-CNOPS-2026-0006", pending: 0, rejected: 1)
        ]);
    }

    [Fact]
    public void TheClaimable_AreTickedForOnePayer_AndAMixedSelectionCannotBeSent()
    {
        SignIn(AppLanguage.French, Permissions.Billing.View, Permissions.Billing.ExportAccounting);
        var page = Render<ClaimBatches>();

        page.Find("[data-row='FA-2026-000041']").ClassList.Should().Contain("is-critical-row");
        page.Find("[data-row='FA-2026-000042']").ClassList.Should().Contain("is-abnormal", "past sixty days a claim is late");

        page.Find("[data-action='Claim.SelectClaimable']").Click();
        page.Find("[data-tick='FA-2026-000040']").HasAttribute("checked").Should().BeTrue();
        page.Find("[data-tick='FA-2026-000041']").HasAttribute("checked").Should().BeFalse("a blocked claim is not ticked");
        page.Find("[data-tick='FA-2026-000042']").HasAttribute("checked").Should().BeFalse("another payer's");
        page.Find("[data-action='Billing.SubmitClaim']").HasAttribute("disabled").Should().BeFalse();

        page.Find("[data-tick='FA-2026-000042']").Change(true);
        page.Find("[data-action='Billing.SubmitClaim']").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-state=status]").ClassList.Should().Contain("is-critical");
    }

    [Fact]
    public void ABatchUnansweredAfterSixtyDays_IsToChase_AndItsInvoicesAreRead()
    {
        SignIn(AppLanguage.French, Permissions.Billing.View, Permissions.Billing.ExportAccounting);
        var page = Render<ClaimBatches>();

        page.Find("[data-row='BT-CNOPS-2026-0007'] .pill").GetAttribute("data-tone").Should().Be("Critical");
        page.Find("[data-row='BT-CNOPS-2026-0006'] .pill").GetAttribute("data-tone").Should().Be("Warning");
        BillingQueries.Calls.Should().Contain(c => c.Method == nameof(IBillingQueries.GetClaimInvoicesAsync)
                                                   && (string)c.Arguments[0]! == "BT-CNOPS-2026-0007");
    }

    [Fact]
    public void WithoutTheRightToClaim_NothingIsTickedSentOrAnswered()
    {
        SignIn(AppLanguage.French, Permissions.Billing.View);
        var page = Render<ClaimBatches>();

        page.Find("[data-action='Claim.SelectClaimable']").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-action='Billing.SubmitClaim']").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-action='Billing.RecordClaimOutcome']").HasAttribute("disabled").Should().BeTrue();
    }
}
