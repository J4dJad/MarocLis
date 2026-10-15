using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Instruments;
using MarocLIS.Instruments.Drivers;
using MarocLIS.Instruments.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Instruments;

/// <summary>
/// Analyser imports run under the interface service account and read every site, whoever is
/// signed in at the workstation that drives the analysers.
/// </summary>
public sealed class ReviewFixesInfra2HostIdentityTests
{
    private static readonly Guid SiteA = Guid.NewGuid();
    private static readonly Guid ServiceAccountId = Guid.NewGuid();

    private sealed class Operator : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "technicien";
        public string FullName => "Technicien de paillasse";
        public Guid SiteId => SiteA;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.None;
        public IReadOnlyList<Guid> AccessibleSiteIds => [SiteA];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => false;
        public string Workstation => "PAILLASSE-1";
    }

    private static readonly ICurrentUser ServiceAccount =
        new ServiceAccountUser(ServiceAccountId, "interface", "Interface automates (compte de service)");

    private static ServiceProvider Services(Action<IServiceCollection>? more = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Server=localhost;Database=MarocLIS_NoConnection"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddSingleton<ICurrentUser, Operator>();
        more?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public void OrdinaryScope_KeepsTheOperatorsSiteFilter()
    {
        using var provider = Services();
        using var scope = provider.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<LisDbContext>();

        context.IgnoreSiteScope.Should().BeFalse();
        context.ScopedSiteIds.Should().Equal(SiteA);
    }

    [Fact]
    public void ScopeRunningAsTheServiceAccount_ReadsEverySite()
    {
        using var provider = Services();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ScopeIdentity>().RunAs(ServiceAccount, ignoreSiteScope: true);
        var context = scope.ServiceProvider.GetRequiredService<LisDbContext>();

        context.IgnoreSiteScope.Should().BeTrue();
        context.ScopedSiteIds.Should().BeEmpty("a tube from another site is not unknown to the analyser host");
    }

    [Fact]
    public async Task ScopeRunningAsTheServiceAccount_IsAuditedUnderIt()
    {
        using var provider = Services();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ScopeIdentity>().RunAs(ServiceAccount, ignoreSiteScope: true);
        var context = scope.ServiceProvider.GetRequiredService<LisDbContext>();
        var interceptor = scope.ServiceProvider.GetRequiredService<AuditSaveChangesInterceptor>();

        context.Departments.Add(new Department("TEST", new MultilingualText("Test", "Test", "Test")));

        // The interceptor is called as EF calls it before a save; nothing reaches a database.
        await interceptor.SavingChangesAsync(new DbContextEventData(null!, null!, context), default);

        var entries = context.ChangeTracker.Entries<AuditEntry>().Select(e => e.Entity).ToList();
        entries.Should().NotBeEmpty();
        entries.Should().OnlyContain(e => e.UserId == ServiceAccountId);
        entries.Should().OnlyContain(e => e.UserName == "interface", "not the technician signed in at the bench");
        entries.Should().OnlyContain(e => e.SiteId == null, "the service account works for no site in particular");
    }

    [Fact]
    public async Task HostSink_HandsEachMessageToAScopeRunningAsTheServiceAccount()
    {
        using var provider = Services(s => s.AddScoped<IInstrumentMessageSink, ContextProbe>());

        var sink = new ScopedInstrumentMessageSink(provider.GetRequiredService<IServiceScopeFactory>(),
                                                   identity: ServiceAccount);

        (await sink.HandleAsync(Guid.NewGuid(), new InstrumentMessage())).Should().BeTrue();

        ContextProbe.Seen.Should().NotBeNull();
        ContextProbe.Seen!.Value.IgnoreSiteScope.Should().BeTrue();
        ContextProbe.Seen!.Value.User.Should().BeSameAs(ServiceAccount);
    }

    private sealed class ContextProbe(LisDbContext context, ScopeIdentity identity) : IInstrumentMessageSink
    {
        public static (bool IgnoreSiteScope, ICurrentUser? User)? Seen { get; private set; }

        public Task<bool> HandleAsync(Guid interfaceId, InstrumentMessage message, CancellationToken ct = default)
        {
            Seen = (context.IgnoreSiteScope, identity.User);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<InstrumentOrderDownload>> ResolveQueryAsync(
            Guid interfaceId, IReadOnlyList<string> specimenIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InstrumentOrderDownload>>([]);

        public Task LogRawAsync(Guid interfaceId, MessageDirection direction, string content,
                                CancellationToken ct = default) => Task.CompletedTask;

        public Task ReportConnectionStateAsync(Guid interfaceId, bool connected, string? error,
                                               CancellationToken ct = default) => Task.CompletedTask;
    }
}
