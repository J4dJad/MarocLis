using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Desktop.Composition;
using MarocLIS.Desktop.Forms;
using MarocLIS.Domain.Common;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Instruments;
using MarocLIS.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace MarocLIS.Desktop;

internal static class Program
{
    private static IHost? _host;

    /// <summary>
    /// Set by the shell when the operator signs out or the session is ended: the process restarts
    /// on the login screen once the host is disposed and the log flushed.
    /// </summary>
    internal static bool RestartAfterExit { get; set; }

    /// <summary>Service provider for forms created outside the container, e.g. by a menu click.</summary>
    internal static IServiceProvider Services =>
        _host?.Services ?? throw new InvalidOperationException("The host has not been built.");

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Catch anything that escapes a UI event handler. A LIS that vanishes mid-entry loses a
        // technician's work and their trust; it must fail visibly and keep the log.
        System.Windows.Forms.Application.ThreadException += OnThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        try
        {
            _host = BuildHost();
            _host.Start();

            var configuration = _host.Services.GetRequiredService<IConfiguration>();
            var cultureManager = _host.Services.GetRequiredService<ICultureManager>();

            var defaultLanguage = Enum.TryParse<AppLanguage>(
                configuration["Application:DefaultLanguage"], out var parsed)
                ? parsed
                : AppLanguage.French;

            cultureManager.SetLanguage(defaultLanguage);

            if (!InitialiseDatabase(configuration)) return;

            // After the language is set, so the warning itself is readable, and before sign-in, so
            // whoever opens the laboratory sees it rather than whoever first prints a report.
            WarnAboutMissingFonts();

            // Sign-in happens before the shell exists: the shell's navigation is built from the
            // signed-in user's permissions, so there is nothing to build until they are known.
            using (var login = _host.Services.GetRequiredService<LoginForm>())
            {
                if (login.ShowDialog() != DialogResult.OK) return;
            }

            System.Windows.Forms.Application.Run(_host.Services.GetRequiredService<MainShellForm>());
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "The application failed to start.");
            MessageBox.Show(
                $"L'application n'a pas pu démarrer.\n\n{ex.Message}\n\nConsultez logs/maroclis-*.log.",
                "MarocLIS", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            // Disposing the host disposes the singletons, among them the document output, which
            // deletes the folder of patient PDFs viewed in this session.
            _host?.StopAsync().GetAwaiter().GetResult();
            _host?.Dispose();
            Log.CloseAndFlush();
        }

        if (RestartAfterExit) System.Windows.Forms.Application.Restart();
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Configuration
               .SetBasePath(AppContext.BaseDirectory)
               .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
               .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
               // Machine-level overrides let one workstation differ — the one that drives the
               // analysers, say — without a separate build.
               .AddEnvironmentVariables("MAROCLIS_");

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.WithProperty("Workstation", Environment.MachineName)
            .CreateLogger();

        builder.Services.AddSerilog();

        var language = Enum.TryParse<AppLanguage>(
            builder.Configuration["Application:DefaultLanguage"], out var parsed)
            ? parsed
            : AppLanguage.French;

        builder.Services.AddMarocLisLocalization(language);
        builder.Services.AddMarocLisInfrastructure(builder.Configuration);
        builder.Services.AddMarocLisApplication(builder.Configuration);
        builder.Services.AddInstrumentInterfacing(builder.Configuration);
        builder.Services.AddMarocLisDesktop(builder.Configuration);

        return builder.Build();
    }

    /// <summary>
    /// Creates or verifies the database before the login screen appears.
    /// <para>
    /// Done here rather than lazily on first query so a misconfigured connection string produces
    /// one clear message at startup instead of an obscure failure once somebody is mid-registration.
    /// </para>
    /// </summary>
    private static bool InitialiseDatabase(IConfiguration configuration)
    {
        using var scope = Services.CreateScope();
        var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        var localizer = scope.ServiceProvider.GetRequiredService<ILocalizer>();

        var seedDemo = configuration.GetValue<bool>("Application:SeedDemoDataOnFirstRun");

        var result = initializer.InitializeAsync(seedDemo).GetAwaiter().GetResult();

        if (result.IsSuccess) return true;

        MessageBox.Show(localizer.Describe(result.Errors),
                        localizer["App.Title"], MessageBoxButtons.OK, MessageBoxIcon.Error);
        return false;
    }

    /// <summary>
    /// Warns, once, when a configured report font is not installed.
    /// <para>
    /// A warning and not a refusal, because a laboratory with a missing Latin font can still work:
    /// the substituted face is ugly, not wrong. The Arabic case <em>is</em> refused, but further in
    /// — <c>ReportReleaseService</c> blocks the release of an Arabic report rather than producing
    /// one in isolated letters. This message exists so the condition is known at eight in the
    /// morning instead of discovered by the first patient who asks for an Arabic report.
    /// </para>
    /// </summary>
    private static void WarnAboutMissingFonts()
    {
        var fonts = Services.GetRequiredService<IFontAvailability>();
        var localizer = Services.GetRequiredService<ILocalizer>();

        var missing = fonts.MissingFrom([fonts.LatinFamily, fonts.ArabicFamily]);

        if (missing.Count == 0) return;

        Log.Error("Report fonts not installed: {Fonts}.", string.Join(", ", missing));

        var key = missing.Contains(fonts.ArabicFamily, StringComparer.OrdinalIgnoreCase)
            ? "Report_ArabicFontMissingWarning"
            : "Report_LatinFontMissingWarning";

        MessageBox.Show(localizer.Format(key, string.Join(", ", missing)),
                        localizer["App.Title"], MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static void OnThreadException(object sender, ThreadExceptionEventArgs e) =>
        ReportUnexpected(e.Exception);

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        ReportUnexpected(e.ExceptionObject as Exception);

    private static void ReportUnexpected(Exception? exception)
    {
        Log.Error(exception, "Unhandled exception reached the UI thread.");

        if (_host is null)
        {
            MessageBox.Show(exception?.Message ?? "Erreur inattendue.", "MarocLIS",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var localizer = _host.Services.GetRequiredService<ILocalizer>();

        // A concurrency conflict is an ordinary event on a shared reception counter, not a crash;
        // it gets its own explanatory message rather than the generic one.
        var key = exception is ConcurrencyConflictException conflict
            ? conflict.ResourceKey
            : "Message.UnexpectedError";

        MessageBox.Show(localizer[key], localizer["App.Title"],
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
