using MarocLIS.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Localization;

public static class LocalizationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the trilingual localisation services. The culture manager is a singleton because
    /// the language is a property of the running workstation, not of a request.
    /// </summary>
    public static IServiceCollection AddMarocLisLocalization(
        this IServiceCollection services,
        AppLanguage initialLanguage = AppLanguage.French,
        Action<LocalizationOptions>? configure = null)
    {
        var options = new LocalizationOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<ICultureManager>(_ => new CultureManager(initialLanguage));
        services.TryAddSingleton<ILocalizer>(provider => new JsonLocalizer(
            provider.GetRequiredService<LocalizationOptions>(),
            provider.GetRequiredService<ICultureManager>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<JsonLocalizer>()));

        return services;
    }
}
