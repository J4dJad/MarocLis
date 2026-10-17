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
        var options = configuration.GetSection(InstrumentHostOptions.SectionName)
                                    .Get<InstrumentHostOptions>()
                      ?? new InstrumentHostOptions();

        services.AddSingleton(options);

        // What the COBAS INTEGRA's own configuration decides (line end, block check, selector,
        // polling interval): per installation, in appsettings, until a trace has confirmed them.
        services.AddSingleton(configuration.GetSection(IntegraHostOptions.SectionName)
                                           .Get<IntegraHostOptions>()
                              ?? new IntegraHostOptions());

        // Singletons: both hold process-wide state the screen reads. The monitor answers "is this
        // link up right now?", and the service account is resolved once at start-up.
        services.AddSingleton<InstrumentLinkMonitor>();
        services.AddSingleton<InstrumentServiceAccount>();
        services.AddSingleton<IInstrumentServiceAccount>(
            sp => sp.GetRequiredService<InstrumentServiceAccount>());

        // Scoped: the sink writes through a database context, and each driver gets its own scope so
        // one analyser's failed import cannot roll back another's results.
        services.AddScoped<InstrumentMessageSink>();
        services.AddScoped<IInstrumentMessageSink>(
            sp => sp.GetRequiredService<InstrumentMessageSink>());

        // The replayer is the same object, registered under the abstraction the application layer
        // depends on. Registered unconditionally and independently of EnableInstrumentHost: a
        // technician accepting a held transmission is working at a bench, not at the one workstation
        // that drives the serial ports.
        services.AddScoped<IInstrumentResultReplayer>(
            sp => sp.GetRequiredService<InstrumentMessageSink>());

        // Read-only: the "Tester une trame" window, on whatever workstation configures the link.
        services.AddScoped<IInstrumentMessagePreviewer, InstrumentMessagePreviewer>();

        services.AddHostedService<InstrumentHostedService>();

        return services;
    }
}
