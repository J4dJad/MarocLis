using System.Security.Claims;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Licensing;
using MarocLIS.Infrastructure.Security;
using MarocLIS.Web.Security;
using MarocLIS.Web.Session;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static MarocLIS.Tests.Licensing.LicenceTestKit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// One operator per browser tab: each circuit's scope holds its own session, so two people signed
/// in on the same server never see each other's rights; and each browser is one workstation of the
/// licence's count, given back when its last tab closes.
/// </summary>
public sealed class WebSessionTests : IDisposable
{
    private readonly MemoryRegistry _registry = new();
    private readonly ServiceProvider _services;

    public WebSessionTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(new Clock { Now = DateTimeOffset.Now });
        services.AddSingleton(new PasswordPolicy());
        services.AddSingleton(new WebDefaults(AppLanguage.French));
        services.AddSingleton<WebSessionStore>();
        services.AddSingleton<WebWorkstations>();
        services.AddSingleton<IWorkstationRegistry>(_registry);
        services.AddSingleton<ILicenceState>(new FixedLicenceState(FullStatus() with { WorkstationLimit = 2 }));
        services.AddScoped<WorkstationPresenceService>();
        services.AddScoped<SessionContext>();
        services.AddScoped<AuthenticationStateProvider, PrincipalState>();
        services.AddScoped<SessionCircuitHandler>();
        services.AddScoped<UserScopes>();
        services.UsePerRequestCurrentUser();
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public void Dispose() => _services.Dispose();

    private static AuthenticatedSession Account(string userName, params string[] permissions) =>
        new(Guid.NewGuid(), userName, userName.ToUpperInvariant(), Guid.NewGuid(), "A", AppLanguage.French,
            ProfessionalQualification.None, new HashSet<string>(permissions), [], new HashSet<string>(),
            MustChangePassword: false);

    private WebSession Open(AuthenticatedSession account, string workstation) =>
        _services.GetRequiredService<WebSessionStore>()
                 .Open(account, workstation, workstation + " (192.168.1.20)", AppLanguage.French, signInNotice: false);

    /// <summary>A tab opening: its scope, its cookie's session, the circuit handler.</summary>
    private async Task<AsyncServiceScope> OpenTabAsync(WebSession session)
    {
        var scope = _services.CreateAsyncScope();
        var state = (PrincipalState)scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        state.Principal = WebSessionCookie.PrincipalFor(session);

        await scope.ServiceProvider.GetRequiredService<SessionCircuitHandler>().OnCircuitOpenedAsync(null!, default);
        return scope;
    }

    private static Task CloseTabAsync(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<SessionCircuitHandler>().OnCircuitClosedAsync(null!, default);

    [Fact]
    public async Task TwoTabsOfTwoOperators_EachSeeTheirOwnOperator_InEveryServiceOfTheirScope()
    {
        var biller = Open(Account("factu", Permissions.Billing.View), "WEB-AAAAAAAAAA");
        var biologist = Open(Account("bio", Permissions.Results.BiologicalValidate), "WEB-BBBBBBBBBB");

        await using var first = await OpenTabAsync(biller);
        await using var second = await OpenTabAsync(biologist);

        var one = first.ServiceProvider.GetRequiredService<ICurrentUser>();
        var two = second.ServiceProvider.GetRequiredService<ICurrentUser>();

        one.UserName.Should().Be("factu");
        one.Has(Permissions.Billing.View).Should().BeTrue();
        one.Has(Permissions.Results.BiologicalValidate).Should().BeFalse();
        one.Workstation.Should().Be("WEB-AAAAAAAAAA (192.168.1.20)", "the audit names the browser and its address");

        two.UserName.Should().Be("bio");
        two.Has(Permissions.Results.BiologicalValidate).Should().BeTrue();
        two.Has(Permissions.Billing.View).Should().BeFalse();

        first.ServiceProvider.GetRequiredService<SessionContext>().Session!.Id.Should().Be(biller.Id);
        second.ServiceProvider.GetRequiredService<SessionContext>().Session!.Id.Should().Be(biologist.Id);
    }

    [Fact]
    public async Task ATabWithoutALiveSession_GetsNoOperator_AndAScopeServesOneSessionOnly()
    {
        var ended = Open(Account("parti"), "WEB-CCCCCCCCCC");
        _services.GetRequiredService<WebSessionStore>().Remove(ended.Id);

        await using var tab = await OpenTabAsync(ended);

        tab.ServiceProvider.GetRequiredService<SessionContext>().IsSignedIn.Should().BeFalse();
        tab.ServiceProvider.GetRequiredService<ICurrentUser>().IsAuthenticated.Should().BeFalse();

        await using var other = await OpenTabAsync(Open(Account("un"), "WEB-DDDDDDDDDD"));
        var context = other.ServiceProvider.GetRequiredService<SessionContext>();
        var intruder = Open(Account("deux"), "WEB-DDDDDDDDDD");

        context.Invoking(c => c.Attach(intruder)).Should().Throw<InvalidOperationException>();
        other.ServiceProvider.GetRequiredService<ICurrentUser>().UserName.Should().Be("un");
    }

    [Fact]
    public async Task ATabStillOpen_AfterSigningOutInAnotherTab_ActsAsNoOne_NotAsTheOperatorWhoLeft()
    {
        var session = Open(Account("bio", Permissions.Results.BiologicalValidate), "WEB-EEEEEEEEEE");
        await using var tab = await OpenTabAsync(session);
        var scopes = tab.ServiceProvider.GetRequiredService<UserScopes>();

        await using (var before = scopes.Create())
            before.ServiceProvider.GetRequiredService<ICurrentUser>().UserName.Should().Be("bio");

        // « Se déconnecter » in another tab of the browser: the session leaves the store at once,
        // this tab's monitor only notices at its next tick.
        _services.GetRequiredService<WebSessionStore>().Remove(session.Id);

        await using var after = scopes.Create();
        var user = after.ServiceProvider.GetRequiredService<ICurrentUser>();
        user.IsAuthenticated.Should().BeFalse("« Valider » clicked in the stale tab is refused, not recorded under bio");
        user.Has(Permissions.Results.BiologicalValidate).Should().BeFalse();
    }

    [Fact]
    public async Task AWebSession_TakesAWorkstationOfTheLicence_AsAWindowsWorkstationDoes()
    {
        var workstations = _services.GetRequiredService<WebWorkstations>();
        await using var scope = _services.CreateAsyncScope();

        (await workstations.ClaimAsync(scope.ServiceProvider, "WEB-AAAAAAAAAA", "factu"))!.Granted.Should().BeTrue();
        (await scope.ServiceProvider.GetRequiredService<WorkstationPresenceService>()
             .ClaimAsync("POSTE-ACCUEIL", "secretaire", "1.0")).Granted.Should().BeTrue();

        var third = await workstations.ClaimAsync(scope.ServiceProvider, "WEB-BBBBBBBBBB", "bio");

        third!.Granted.Should().BeFalse("the licence counts two workstations, browsers included");
        third.Active.Select(a => a.MachineName).Should().BeEquivalentTo(["WEB-AAAAAAAAAA", "POSTE-ACCUEIL"]);

        // The same browser signing in again keeps its place.
        (await workstations.ClaimAsync(scope.ServiceProvider, "WEB-AAAAAAAAAA", "factu"))!.Granted.Should().BeTrue();
    }

    [Fact]
    public async Task ABrowser_GivesItsPlaceBack_WhenItsLastTabCloses_NotBefore()
    {
        var workstations = _services.GetRequiredService<WebWorkstations>();
        await using (var scope = _services.CreateAsyncScope())
            await workstations.ClaimAsync(scope.ServiceProvider, "WEB-AAAAAAAAAA", "factu");

        var session = Open(Account("factu"), "WEB-AAAAAAAAAA");
        var tab1 = await OpenTabAsync(session);
        var tab2 = await OpenTabAsync(session);
        workstations.OpenTabs("WEB-AAAAAAAAAA").Should().Be(2);

        await CloseTabAsync(tab1);
        _registry.IsActive("WEB-AAAAAAAAAA").Should().BeTrue("another tab of the browser is still open");

        await CloseTabAsync(tab2);
        _registry.IsActive("WEB-AAAAAAAAAA").Should().BeFalse("the browser's last tab closed");
        workstations.OpenTabs("WEB-AAAAAAAAAA").Should().Be(0);

        await tab1.DisposeAsync();
        await tab2.DisposeAsync();
    }

    [Fact]
    public async Task SigningOut_GivesThePlaceBack_AtOnce()
    {
        var workstations = _services.GetRequiredService<WebWorkstations>();
        await using (var scope = _services.CreateAsyncScope())
            await workstations.ClaimAsync(scope.ServiceProvider, "WEB-AAAAAAAAAA", "factu");

        await workstations.ReleaseAsync("WEB-AAAAAAAAAA");

        _registry.IsActive("WEB-AAAAAAAAAA").Should().BeFalse();
    }

    [Fact]
    public void ABrowserIdentity_IsWellFormed_AndNamedAsAWebWorkstation()
    {
        var id = BrowserIdentity.NewId();

        BrowserIdentity.IsWellFormed(id).Should().BeTrue();
        BrowserIdentity.IsWellFormed("../etc").Should().BeFalse();
        BrowserIdentity.IsWellFormed(null).Should().BeFalse();
        (BrowserIdentity.Prefix + id).Length.Should().BeLessThanOrEqualTo(Workstation.MachineNameMaxLength);
        BrowserIdentity.AuditName("WEB-" + id, System.Net.IPAddress.Parse("192.168.1.20"))
            .Should().Be($"WEB-{id} (192.168.1.20)");
    }

    // ---- Fakes -------------------------------------------------------------------------------------

    private sealed class PrincipalState : AuthenticationStateProvider
    {
        public ClaimsPrincipal Principal { get; set; } = new(new ClaimsIdentity());

        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(Principal));
    }

    /// <summary>The workstations' table, in memory, deciding as the real one does.</summary>
    private sealed class MemoryRegistry : IWorkstationRegistry
    {
        private readonly List<Workstation> _rows = [];
        private readonly Lock _gate = new();

        public bool IsActive(string machineName)
        {
            lock (_gate) return _rows.Any(r => r.MachineName == machineName && r.IsActive(DateTimeOffset.Now));
        }

        public Task<WorkstationClaim> ClaimAsync(string machineName, string? userName, string? appVersion, int? limit,
                                                 DateTimeOffset now, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var claim = WorkstationClaim.Decide(_rows, machineName, now, limit);
                if (!claim.Granted) return Task.FromResult(claim);

                var row = _rows.FirstOrDefault(r => r.MachineName == machineName);
                if (row is null || !row.IsActive(now))
                {
                    if (row is not null) _rows.Remove(row);
                    row = new Workstation(machineName, now);
                    _rows.Add(row);
                }

                row.Seen(now, userName);
                return Task.FromResult(WorkstationClaim.Decide(_rows, machineName, now, limit));
            }
        }

        public Task ReleaseAsync(string machineName, DateTimeOffset now, CancellationToken ct = default)
        {
            lock (_gate) _rows.FirstOrDefault(r => r.MachineName == machineName)?.Release(now);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ActiveWorkstation>> ActiveAsync(DateTimeOffset now, CancellationToken ct = default)
        {
            lock (_gate)
                return Task.FromResult<IReadOnlyList<ActiveWorkstation>>(
                    [.. _rows.Where(r => r.IsActive(now))
                             .Select(r => new ActiveWorkstation(r.MachineName, r.LastUserName, r.FirstSeenAt, r.LastSeenAt))]);
        }
    }
}
