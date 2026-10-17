using MarocLIS.Api;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;

// MarocLIS.Api — the server side of « Validation à distance ».
//
// One executable for both ways it runs: as the Windows service the laboratory installs
// (scripts/install-api-service.ps1), and from a console for a test. As a service, its working
// directory is System32; the content root and the working directory are moved next to the
// executable so appsettings.json, the translations, the report fonts and the logs are found there.
var asService = WindowsServiceHelpers.IsWindowsService();
if (asService) Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = asService ? AppContext.BaseDirectory : null
});

builder.Host.UseWindowsService(service => service.ServiceName = "MarocLIS.Api");

// The same overrides as the workstation: MAROCLIS_Database__ConnectionString and so on.
builder.Configuration.AddEnvironmentVariables("MAROCLIS_");

builder.Services.AddSerilog((_, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.WithProperty("Workstation", Environment.MachineName));

builder.Services.AddMarocLisApi(builder.Configuration);

// Scope validation on in every environment, not only in Development: a singleton that captured
// the per-request operator would sign every biologist's work with the first one's name.
builder.Host.UseDefaultServiceProvider(provider =>
{
    provider.ValidateScopes = true;
    // The API resolves the validation use cases only; the other services the application layer
    // registers need read-side queries it deliberately does not register.
    provider.ValidateOnBuild = false;
});

var app = builder.Build();

app.UseMarocLisApi();
app.WarnIfReachableBeyondLoopback();

app.Run();

/// <summary>The entry point, public so the tests' WebApplicationFactory can start the API.</summary>
public partial class Program;
