using Microsoft.Extensions.DependencyInjection;

namespace MarocLIS.Mobile.Services;

public static class MobileServiceCollectionExtensions
{
    /// <summary>
    /// The application's services. Scoped, which in the browser means one instance for the page's
    /// life: one session, one idle clock, one language for every screen.
    /// </summary>
    public static IServiceCollection AddMobileServices(this IServiceCollection services)
    {
        services.AddScoped<Texts>();
        services.AddScoped<SessionState>();
        services.AddScoped<Workflow>();
        services.AddScoped<Browser>();
        services.AddScoped<ApiClient>();
        return services;
    }
}
