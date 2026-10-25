using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Web.Components.Screens.Admin;
using MarocLIS.Web.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static MarocLIS.Tests.Desktop.AdministrationScreenRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Utilisateurs » in the browser: the accounts with their state in colour, the roles of the one
/// selected, a password reset handed to the authentication service, a lockout cleared from « Plus
/// d'actions », each change recorded by the service — and the buttons a viewer has no right to
/// left out.
/// </summary>
public sealed class WebUserAdministrationTests : WebAdminContext
{
    /// <summary>An account in the store and its row in the list, under the same id.</summary>
    private (AppUser Account, UserRow Row) Account(string userName, bool locked = false, int failed = 0)
    {
        var account = new AppUser(userName, "BENNANI Salma", "hash");
        for (var i = 0; i < failed; i++) account.RegisterFailedLogin(TestClock.Now, 5, TimeSpan.FromMinutes(15));
        UserStore.On(nameof(IRepository<AppUser>.GetByIdAsync), _ => account);
        UserStore.On("GetByIdWithDetailsAsync", _ => account);
        return (account, User(account.Id, userName, locked: locked, failed: failed));
    }

    [Fact]
    public void TheAccounts_AreListed_TheLockedOneInRed_AndTheFirstOpenedUnderTheList()
    {
        SignIn(AppLanguage.French, Administrator);
        Admin.On(nameof(IAdministrationQueries.GetUsersAsync), _ => new[] { User(userName: "h.tahiri", locked: true, failed: 5), User(userName: "s.elfassi") });

        var page = Render<UserAdministration>();

        page.FindAll("[data-grid=accounts] tbody tr").Should().HaveCount(2);
        page.Find("[data-grid=accounts] tr[data-row='h.tahiri']").ClassList.Should().Contain("is-critical-row");
        page.Find("[data-state=count]").TextContent.Should().Contain("2");
        page.Find("[data-state=notice]").ClassList.Should().Contain("is-critical", "the lockout is said above the account's facts");
        page.Find("[data-state=facts]").TextContent.Should().Contain("s.bennani@alamal.ma");
        page.FindAll("[data-tab=Security]").Should().ContainSingle("the security log comes with the audit trail's right");
    }

    [Fact]
    public void TheRolesOfTheAccount_AreSavedByTheService_WhichLogsTheGrant()
    {
        SignIn(AppLanguage.French, Administrator);
        var (account, row) = Account("s.elfassi");
        var cashier = Role(rights: Permissions.Billing.View) with { Code = "CAISSE" };
        Admin.On(nameof(IAdministrationQueries.GetUsersAsync), _ => new[] { row });
        Admin.On(nameof(IAdministrationQueries.GetRolesAsync), _ => new[] { cashier });
        Admin.On(nameof(IAdministrationQueries.GetPermissionsForRolesAsync), _ => new HashSet<string> { Permissions.Billing.View });
        Admin.On(nameof(IAdministrationQueries.CountActiveUsersWithPermissionAsync), _ => 1);

        var page = Render<UserAdministration>();
        page.Find("[data-action='Admin.RolesButton']").Click();
        page.Find("[data-modal=roles] [data-role=CAISSE]").Change(true);
        page.Find("[data-modal=roles] [data-action='Action.Save']").Click();

        account.Roles.Should().ContainSingle(r => r.RoleId == cashier.RoleId);
        AuditReason.Should().Be("Rôles de s.elfassi — 1 rôle(s)");
        SecurityLog.Events.Should().ContainSingle(e => e.Kind == SecurityEventKind.RoleAssigned);
        page.Find("[data-state=status]").TextContent.Should().NotBeEmpty();
    }

    [Fact]
    public void APasswordTyped_IsHandedToTheAuthenticationService_AndTheHolderMustChangeIt()
    {
        SignIn(AppLanguage.French, Administrator);
        var (account, row) = Account("s.elfassi");
        Admin.On(nameof(IAdministrationQueries.GetUsersAsync), _ => new[] { row });
        Admin.On(nameof(IAdministrationQueries.GetPermissionsForRolesAsync), _ => new HashSet<string>());
        Authentication.On(nameof(IAuthenticationService.ResetPasswordAsync), _ => Result.Success());

        var page = Render<UserAdministration>();
        page.Find("[data-action='Admin.ResetPassword']").Click();
        page.Find("[data-modal=reset-password] [data-field=text]").Input("Provisoire-2026!");
        page.Find("[data-modal=reset-password] [data-action='Action.Ok']").Click();

        var reset = Authentication.Calls.Single(c => c.Method == nameof(IAuthenticationService.ResetPasswordAsync));
        reset.Arguments[0].Should().Be(account.Id);
        reset.Arguments[1].Should().Be("Provisoire-2026!");
        page.Find("[data-modal=users-message]").TextContent.Should().Contain("s.elfassi");
    }

    [Fact]
    public void ALockedAccount_IsUnlockedFromMoreActions_AndTheUnlockIsLogged()
    {
        SignIn(AppLanguage.French, Administrator);
        var (account, row) = Account("h.tahiri", locked: true, failed: 5);
        Admin.On(nameof(IAdministrationQueries.GetUsersAsync), _ => new[] { row });

        var page = Render<UserAdministration>();
        page.Find(".more-menu [data-action='Admin.Unlock']").Click();

        account.FailedLoginAttempts.Should().Be(0);
        account.IsLockedOut(TestClock.Now).Should().BeFalse();
        AuditReason.Should().Be("Déverrouillage du compte h.tahiri");
        SecurityLog.Events.Should().ContainSingle(e => e.Kind == SecurityEventKind.AccountUnlocked);
    }

    [Fact]
    public void AViewer_SeesTheAccounts_WithoutTheButtonsThatChangeThem()
    {
        SignIn(AppLanguage.French, Permissions.Administration.ViewUsers);
        Admin.On(nameof(IAdministrationQueries.GetUsersAsync), _ => new[] { User() });

        var page = Render<UserAdministration>();

        page.FindAll("[data-grid=accounts] tbody tr").Should().ContainSingle();
        foreach (var key in new[] { "Action.Edit", "Admin.RolesButton", "Admin.ResetPassword", "Admin.NewUser", "Admin.Deactivate", "Admin.Unlock" })
            page.FindAll($"[data-action='{key}']").Should().BeEmpty($"{key} needs the right to manage users");
        page.FindAll("[data-action='ResultEntry.MoreActions']").Should().BeEmpty();
        page.FindAll("[data-tab=Security]").Should().BeEmpty();
    }

    [Fact]
    public void WithoutTheRightToSeeAccounts_TheServiceRefusesTheList()
    {
        SignIn(AppLanguage.French, Permissions.Billing.View);

        var page = Render<UserAdministration>();

        Admin.Calls.Should().NotContain(c => c.Method == nameof(IAdministrationQueries.GetUsersAsync));
        page.Find("[data-state=status]").TextContent.Should().NotBeEmpty();
    }

    [Fact]
    public void InArabic_TheCountIsWorded_AndTheScreenIsMirrored()
    {
        SignIn(AppLanguage.Arabic, Administrator);
        Admin.On(nameof(IAdministrationQueries.GetUsersAsync), _ => new[] { User() });

        var page = Render<UserAdministration>();

        page.Find("[data-state=count]").TextContent.Replace("\u2066", "").Replace("\u2069", "").Should().Be("1 حساب · 1 نشط",
            "the figures are isolated left-to-right inside the Arabic sentence");
        Services.GetRequiredService<WebTexts>().Direction.Should().Be("rtl");
    }
}
