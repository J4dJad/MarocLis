using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Treasury;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Web.Components.Screens.Billing;
using MarocLIS.Web.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static MarocLIS.Tests.Desktop.AdministrationScreenRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Visa des caisses » in the browser: the tills closed in the period, the variance in its colour,
/// a till that did not balance signed off only with a comment, one's own till never, and the
/// sign-off recorded with its reason by the service.
/// </summary>
public sealed class WebCashReviewTests : WebAdminContext
{
    private static readonly string[] Supervisor = [Permissions.Billing.View, Permissions.Billing.ReconcileCashSession];

    /// <summary>A till really closed, so the domain decides the sign-off.</summary>
    private CashSession ClosedTill(decimal counted)
    {
        var till = new CashSession(Guid.NewGuid(), Guid.NewGuid(), TestClock.Now.AddHours(-9), 200m);
        till.Close(TestClock.Now.AddHours(-1), till.OperatorUserId, counted, counted == 200m ? null : "Erreur de rendu de monnaie");
        TillStore.On(nameof(IRepository<CashSession>.GetByIdAsync), _ => till);
        return till;
    }

    [Fact]
    public void TheTillsOfThePeriod_AreListed_TheShortfallInRed_AndTheCountOfThoseToSign()
    {
        SignIn(AppLanguage.French, Supervisor);
        Treasury.On(nameof(ITreasuryQueries.GetClosedCashSessionsAsync), _ => new[] { Till(Guid.NewGuid(), -20m), Till(Guid.NewGuid(), 0m) });

        var page = Render<CashReview>();

        page.FindAll("[data-grid=sessions] tbody tr").Should().HaveCount(2);
        page.Find("[data-state=count]").TextContent.Should().Contain("2");
        page.Find("[data-state=variance]").ClassList.Should().Contain("critical");
        Treasury.Calls.Should().ContainSingle(c => c.Method == nameof(ITreasuryQueries.GetClosedCashSessionsAsync) && (bool)c.Arguments[3]! == true,
                                              "the list opens on the tills still to sign off");
    }

    [Fact]
    public void ATillThatDidNotBalance_IsSignedOffWithAReason_RecordedByTheService()
    {
        SignIn(AppLanguage.French, Supervisor);
        var till = ClosedTill(counted: 180m);
        Treasury.On(nameof(ITreasuryQueries.GetClosedCashSessionsAsync), _ => new[] { Till(till.OperatorUserId, -20m) with { Id = till.Id } });

        var page = Render<CashReview>();
        page.Find("[data-action='Treasury.ReconcileSession']").Click();

        page.Find("[data-modal=reason] [data-field=reason]").Input("Écart expliqué par la caissière, remboursé");
        page.Find("[data-modal=reason] [data-action='Action.Ok']").Click();

        till.ReconciledAt.Should().NotBeNull();
        Work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(1);
        AuditReason.Should().Contain("Visa de la caisse").And.Contain("remboursé");
        page.Find("[data-state=status]").TextContent.Should().Contain("EL IDRISSI Karim");
    }

    [Fact]
    public void OnesOwnTill_IsNotSignedOff_AndSaysWhy()
    {
        SignIn(AppLanguage.French, Supervisor);
        var self = Services.GetRequiredService<SessionContext>().User.UserId;
        Treasury.On(nameof(ITreasuryQueries.GetClosedCashSessionsAsync), _ => new[] { Till(self, 10m) });

        var page = Render<CashReview>();

        page.Find("[data-state=own-till]").Should().NotBeNull();
        page.Find("[data-action='Treasury.ReconcileSession']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void WithoutTheRightToSignOff_TheButtonIsNotOffered_AndTheServiceRefusesTheList()
    {
        SignIn(AppLanguage.French, Permissions.Billing.View);

        var page = Render<CashReview>();

        page.FindAll("[data-action='Treasury.ReconcileSession']").Should().BeEmpty();
        Treasury.Calls.Should().BeEmpty();
        page.Find("[data-state=status]").TextContent.Should().NotBeEmpty();
    }

    [Fact]
    public void InArabic_TheScreenIsMirrored()
    {
        SignIn(AppLanguage.Arabic, Supervisor);

        var page = Render<CashReview>();

        page.Find("[data-state=empty]").TextContent.Should().Be("لا توجد صناديق مغلقة في هذه الفترة.");
        Services.GetRequiredService<WebTexts>().Direction.Should().Be("rtl");
    }
}
