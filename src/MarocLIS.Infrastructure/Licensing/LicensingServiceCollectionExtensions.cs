using MarocLIS.Application.Licensing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MarocLIS.Infrastructure.Licensing;

public static class LicensingServiceCollectionExtensions
{
    /// <summary>
    /// The licence, for a host that enforces it: the workstation and the API. Kept out of
    /// <see cref="InfrastructureServiceCollectionExtensions.AddMarocLisInfrastructure"/> on purpose —
    /// the services take <see cref="ILicenceState"/> as optional, and a composition without it (the
    /// tests', a maintenance tool's) refuses nothing.
    /// </summary>
    public static IServiceCollection AddMarocLisLicensing(this IServiceCollection services)
    {
        // The key, the revocations and the release date are compiled in: read once.
        services.TryAddSingleton(_ => new LicenceTrust());

        // One evaluation for the whole process, replaced at each refresh.
        services.TryAddSingleton<LicenceStatusHolder>();
        services.TryAddSingleton<ILicenceState>(sp => sp.GetRequiredService<LicenceStatusHolder>());

        services.TryAddScoped<ILicenceStore, LicenceStore>();
        services.TryAddScoped<IWorkstationRegistry, WorkstationRegistry>();
        services.TryAddScoped<LicenceService>();
        services.TryAddScoped<WorkstationPresenceService>();

        return services;
    }
}
