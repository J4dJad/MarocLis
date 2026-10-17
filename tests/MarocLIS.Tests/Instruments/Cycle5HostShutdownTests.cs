using System.Linq.Expressions;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Identity;
using MarocLIS.Instruments.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Instruments;

/// <summary>
/// Closing the application while the host waits for its next configuration pass. The wait sat
/// outside the loop's try, so the cancellation escaped the service: Visual Studio stopped on it
/// as an unhandled exception every time the application was closed under the debugger.
/// </summary>
public sealed class Cycle5HostShutdownTests
{
    private sealed class Users(params AppUser[] users) : IRepository<AppUser>
    {
        public Task<AppUser?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(users.FirstOrDefault(u => u.Id == id));

        public Task<AppUser?> GetByIdWithDetailsAsync(Guid id, CancellationToken ct = default) =>
            GetByIdAsync(id, ct);

        public Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(users.Any(u => u.Id == id));

        public Task<IReadOnlyList<AppUser>> ListAsync(Expression<Func<AppUser, bool>> predicate,
                                                       CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AppUser>>(users.Where(predicate.Compile()).ToList());

        public Task AddAsync(AppUser entity, CancellationToken ct = default) => Task.CompletedTask;

        public void Update(AppUser entity) { }

        public void SoftDelete(AppUser entity, Guid byUserId, string reason) { }
    }

    [Fact]
    public async Task ClosingDuringThePollWait_EndsTheHostCleanly()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRepository<AppUser>>(_ => new Users(new AppUser("interface", "Interface", "x")));
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var options = new InstrumentHostOptions { EnableInstrumentHost = true, ConfigurationPollSeconds = 5 };
        var monitor = new InstrumentLinkMonitor();
        var host = new InstrumentHostedService(scopes, options, new InstrumentServiceAccount(scopes, options), monitor);

        await host.StartAsync(CancellationToken.None);

        // No interface repository is registered, so the pass fails and the host goes on to wait.
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (monitor.HostError is null && DateTime.UtcNow < deadline) await Task.Delay(20);
        monitor.HostError.Should().NotBeNull();
        await Task.Delay(100);

        await host.StopAsync(CancellationToken.None);

        host.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue(
            "closing the application is the normal way out of the wait, not a failure");
    }
}
