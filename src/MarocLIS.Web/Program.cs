using MarocLIS.Web.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;

namespace MarocLIS.Web;

/// <summary>
/// MarocLIS Web — the laboratory's second interface on the same core as the Windows application,
/// served to the browsers of its local network.
/// <para>
/// One executable for both ways it runs: as a Windows service on the laboratory's server, and from
/// a console for a test. As a service, its working directory is System32; the content root and the
/// working directory are moved next to the executable so appsettings.json, the translations and
/// the logs are found there.
/// </para>
/// </summary>
public sealed class Program
{
    private Program() { }

    public static async Task Main(string[] args)
    {
        var asService = WindowsServiceHelpers.IsWindowsService();
        if (asService) Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = asService ? AppContext.BaseDirectory : null
        });

        builder.Host.UseWindowsService(service => service.ServiceName = "MarocLIS.Web");

        // The same overrides as the workstation: MAROCLIS_Database__ConnectionString and so on.
        builder.Configuration.AddEnvironmentVariables("MAROCLIS_");

        builder.Services.AddSerilog((_, logger) => logger
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.WithProperty("Workstation", Environment.MachineName));

        builder.Services.AddMarocLisWeb(builder.Configuration);

        // Scope validation on in every environment: a singleton that captured the per-circuit
        // operator would sign every browser's work with the first one's name.
        builder.Host.UseDefaultServiceProvider(provider =>
        {
            provider.ValidateScopes = true;
            // The application layer registers services whose read-side queries arrive with the
            // screens that use them; they are resolved, and checked, when those screens are.
            provider.ValidateOnBuild = false;
        });

        var app = builder.Build();

        await app.InitializeDatabaseAsync();

        app.UseMarocLisWeb();

        await app.RunAsync();
    }
}
