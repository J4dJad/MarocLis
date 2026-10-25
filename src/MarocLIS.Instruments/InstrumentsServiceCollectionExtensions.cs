using MarocLIS.Application.Abstractions;
using MarocLIS.Instruments.Drivers;
using MarocLIS.Instruments.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarocLIS.Instruments;

public static class InstrumentsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the analyser interfacing stack: the message sink, the live-state monitor and the
    /// background host.
    /// <para>
    /// The host is registered unconditionally and decides for itself whether to run, rather than
    /// being registered only when enabled. The interface screen has to be able to say <em>why</em>
    /// nothing is connected — disabled by configuration, or a missing service account — and a
    /// service that was never registered cannot answer.
    /// </para>
    /// </summary>
    public static IServiceCollection AddInstrumentInterfacing(this IServiceCollection services,
                                                               IConfiguration configuration)
    {
        services.AddInstrumentConfiguration(configuration);

        // Singleton: it holds process-wide state the screen reads — "is this link up right now?".
        services.AddSingleton<InstrumentLinkMonitor>();

        // Scoped: the sink writes through a database context, and each driver gets its own scope so
        // one analyser's failed import cannot roll back another's results.
        services.AddScoped<IInstrumentMessageSink>(
            sp => sp.GetRequiredService<InstrumentMessageSink>());

        services.AddHostedService<InstrumentHostedService>();

        return services;
    }

    /// <summary>
    /// What configuring the links needs without driving any analyser: the options, the service
    /// account a replay attributes its values to, the replayer and the « Tester une trame »
    /// previewer. The web server registers only this — the analysers are driven by one Windows
    /// workstation, never by it — so its interface screen shows and configures the links, replays a
    /// held transmission and tries a frame through the same services, and opens no port.
    /// </summary>
    public static IServiceCollection AddInstrumentConfiguration(this IServiceCollection services,
                                                                 IConfiguration configuration)
    {
        var options = configuration.GetSection(InstrumentHostOptions.SectionName)
                                    .Get<InstrumentHostOptions>()
                      ?? new InstrumentHostOptions();

        services.AddSingleton(options);

        // What the COBAS INTEGRA's own configuration decides (line end, block check, selector,
        // polling interval): per installation, in appsettings, until a trace has confirmed them.
        services.AddSingleton(configuration.GetSection(IntegraHostOptions.SectionName)
                                           .Get<IntegraHostOptions>()
                              ?? new IntegraHostOptions());

        // Singleton: the service account is resolved once, at start-up or at the first replay.
        services.AddSingleton<InstrumentServiceAccount>();
        services.AddSingleton<IInstrumentServiceAccount>(
            sp => sp.GetRequiredService<InstrumentServiceAccount>());

        services.AddScoped<InstrumentMessageSink>();

        // The replayer is the sink, registered under the abstraction the application layer
        // depends on. Registered unconditionally and independently of EnableInstrumentHost: a
        // technician accepting a held transmission is working at a bench, not at the one workstation
        // that drives the serial ports.
        services.AddScoped<IInstrumentResultReplayer>(
            sp => sp.GetRequiredService<InstrumentMessageSink>());

        // Read-only: the "Tester une trame" window, on whatever workstation configures the link.
        services.AddScoped<IInstrumentMessagePreviewer, InstrumentMessagePreviewer>();

        return services;
    }
}
