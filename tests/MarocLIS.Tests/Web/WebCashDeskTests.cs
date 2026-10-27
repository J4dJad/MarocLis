using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Web.Components.Screens.Billing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Caisse » in the browser: the opening float, the takings, the variance as the count is typed,
/// the explanation it calls for, the closing confirmed with its figures, the drawer left to the
/// workstation — and what is offered by right.
/// </summary>
public sealed class WebCashDeskTests : WebFinanceContext
{
    [Fact]
    public void WithNoTillOpen_TheFloatIsAsked_AndAWordIsRefusedBeforeTheService()
    {
        SignIn(AppLanguage.French, Cashier);
        var page = Render<CashDesk>();

        page.Find("[data-card=open-session]").Should().NotBeNull();
        page.Find("[data-state=hardware]").TextContent.Should().Contain("poste Windows");

        page.Find("[data-field=float]").Input("cinq cents");
        page.Find("[data-action='Payment.OpenSession']").Click();
        page.Find("[data-refusal=float]").Should().NotBeNull();
        SessionStore.Calls.Should().BeEmpty();

        page.Find("[data-field=float]").Input("500");
        page.Find("[data-action='Payment.OpenSession']").Click();
        SessionStore.CallsTo(nameof(IRepository<CashSession>.AddAsync)).Should().Be(1);
        Work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(1);
    }

    [Fact]
    public void AnOpenTill_ShowsItsTakings_AndAShortfallBeyondTheToleranceMustBeExplained()
    {
        SignIn(AppLanguage.French, Cashier);
        var summary = Session(opening: 500m, cash: 1450m, withdrawals: 1000m);
        // Only the cashier who opened the till closes it with their count.
        var user = Services.GetRequiredService<MarocLIS.Web.Session.SessionContext>().User;
        var cashier = user.UserId;
        // The till of the site the cashier works at: one open at another site is not this desk's.
        var session = new CashSession(user.SiteId, cashier, summary.OpenedAt, 950m);
        BillingQueries.On(nameof(IBillingQueries.GetCashSessionSummaryAsync), _ => summary)
                      .On(nameof(IBillingQueries.GetOpenCashSessionAsync), _ => session);
        SessionStore.On(nameof(IRepository<CashSession>.GetByIdAsync), _ => session);

        var page = Render<CashDesk>();

        page.Find("[data-line='Payment.Withdrawals']").TextContent.Should().Contain(Mad(-1000m));
        page.Find("[data-state=expected]").TextContent.Should().Contain(Mad(950m));
        page.Find("[data-field=counted]").GetAttribute("value").Should().Be("950,00", "pre-filled as the workstation writes it, with the decimal comma");
        page.Find("[data-state=variance]").ClassList.Should().Contain("normal");

        page.Find("[data-field=counted]").Input("900");
        page.Find("[data-state=variance]").ClassList.Should().Contain("critical");
        page.Find("[data-action='Payment.CloseSession']").Click();
        page.Find("[data-refusal=explanation]").TextContent.Should().Contain(Mad(-50m));

        page.Find("[data-field=explanation]").Input("Rendu de monnaie erroné, signalé à la responsable");
        page.Find("[data-action='Payment.CloseSession']").Click();
        page.Find("[data-prompt=confirm]").TextContent.Should().Contain(Mad(900m)).And.Contain(Mad(-50m));

        page.Find("[data-action='Action.Yes']").Click();
        page.Find("[data-modal=cash-message]").TextContent.Should().Contain(Mad(-50m), page.Markup);
        Work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(1);
        session.Variance.Should().Be(-50m);
    }

    [Fact]
    public void WithoutTheRightToCloseATill_NeitherTheClosingNorTheBankingIsOffered()
    {
        BillingQueries.On(nameof(IBillingQueries.GetCashSessionSummaryAsync), _ => Session());
        SignIn(AppLanguage.French, Permissions.Billing.View, Permissions.Billing.RecordPayment);
        var page = Render<CashDesk>();

        page.Find("[data-action='Payment.CloseSession']").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-action='Payment.RecordWithdrawal']").HasAttribute("disabled").Should().BeTrue();
    }
}
