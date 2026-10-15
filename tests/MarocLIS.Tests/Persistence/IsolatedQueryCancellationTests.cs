using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

public interface ICancelledLoadQueries
{
    Task<int> LoadAsync(CancellationTokenSource cancel, CancellationToken ct = default);
    Task LoadVoidAsync(CancellationTokenSource cancel, CancellationToken ct = default);
}

public sealed class CancelledLoadQueries : ICancelledLoadQueries
{
    // SQL Server answers a cancelled command with a SqlException, not a cancellation.
    public async Task<int> LoadAsync(CancellationTokenSource cancel, CancellationToken ct = default)
    {
        await Task.Yield();
        await cancel.CancelAsync();
        throw new InvalidOperationException("Opération annulée par l'utilisateur.");
    }

    public async Task LoadVoidAsync(CancellationTokenSource cancel, CancellationToken ct = default) =>
        await LoadAsync(cancel, ct);
}

/// <summary>
/// A load the operator abandons by moving to another screen fails as a cancellation, which the
/// screens ignore, and not as the "unexpected error" the payroll screen showed.
/// </summary>
public sealed class IsolatedQueryCancellationTests(FinanceTreasuryScratchDatabase database)
    : IClassFixture<FinanceTreasuryScratchDatabase>
{
    private sealed class Operator : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "paie";
        public string FullName => "Paie";
        public Guid SiteId { get; } = Guid.NewGuid();
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.None;
        public IReadOnlyList<Guid> AccessibleSiteIds => [SiteId];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => true;
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "PAIE-1";
    }

    private ServiceProvider Services()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator());
        services.AddIsolatedQueries<ICancelledLoadQueries, CancelledLoadQueries>();
        return services.BuildServiceProvider();
    }

    [SqlServerFact]
    public async Task A_load_failing_after_its_screen_cancelled_it_is_a_cancellation()
    {
        await using var provider = Services();
        using var scope = provider.CreateScope();
        var queries = scope.ServiceProvider.GetRequiredService<ICancelledLoadQueries>();

        using var first = new CancellationTokenSource();
        await FluentActions.Awaiting(() => queries.LoadAsync(first, first.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        using var second = new CancellationTokenSource();
        await FluentActions.Awaiting(() => queries.LoadVoidAsync(second, second.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        // Without a cancellation the failure is still reported as it is.
        using var unrelated = new CancellationTokenSource();
        await FluentActions.Awaiting(() => queries.LoadAsync(new CancellationTokenSource(), unrelated.Token))
            .Should().ThrowAsync<InvalidOperationException>();
    }
}
