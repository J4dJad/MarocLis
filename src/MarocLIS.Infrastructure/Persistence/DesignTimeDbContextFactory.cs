using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace MarocLIS.Infrastructure.Persistence;

/// <summary>
/// Builds a <see cref="LisDbContext"/> for the <c>dotnet ef</c> tooling.
/// <para>
/// The tooling cannot use the application's own composition root: <c>Program.Main</c> is
/// <c>[STAThread]</c> and starts a WinForms message loop, and EF's design-time discovery looks for
/// either a host builder it can call or a factory like this one. Without it, every
/// <c>dotnet ef</c> command fails before it reads the model.
/// </para>
/// <para>
/// No connection is made here. <c>migrations add</c> and <c>migrations script</c> work entirely
/// from the model, so the connection string only has to be well-formed — which is why a laboratory
/// can generate its deployment script on a workstation that has no SQL Server on it at all.
/// <c>database update</c> is the one command that does connect, and it needs a real server.
/// </para>
/// <para>
/// The <c>UseSqlServer</c> options must stay in step with
/// <c>InfrastructureServiceCollectionExtensions.AddMarocLisInfrastructure</c>, in particular the
/// migrations-history table. If the tooling recorded applied migrations in <c>dbo.__EFMigrationsHistory</c>
/// while the application looked in <c>cfg.__MigrationsHistory</c>, each would believe the database
/// was unmigrated and the next startup would try to apply everything a second time.
/// </para>
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LisDbContext>
{
    /// <summary>
    /// Used when no <c>appsettings.json</c> is found beside the invocation. Points at a local
    /// instance so that <c>database update</c> does something predictable if someone runs it
    /// without configuring anything; it is not a deployment default.
    /// </summary>
    private const string FallbackConnectionString =
        "Server=localhost\\SQLEXPRESS;Database=MarocLIS;Integrated Security=true;" +
        "TrustServerCertificate=true";

    public LisDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<LisDbContext>();

        builder.UseSqlServer(ResolveConnectionString(args), sql =>
        {
            sql.MigrationsHistoryTable("__MigrationsHistory", "cfg");
            sql.MigrationsAssembly(typeof(LisDbContext).Assembly.FullName);
        });

        // No ICurrentUser: at design time there is no signed-in operator, and the site filter the
        // context would build from one plays no part in the schema.
        return new LisDbContext(builder.Options);
    }

    /// <summary>
    /// First an explicit <c>--connection</c> passed through by the tooling, then the desktop
    /// client's configuration, then <see cref="FallbackConnectionString"/>.
    /// </summary>
    private static string ResolveConnectionString(string[] args)
    {
        var explicitArgument = Array.IndexOf(args, "--connection");
        if (explicitArgument >= 0 && explicitArgument + 1 < args.Length)
            return args[explicitArgument + 1];

        var configured = new ConfigurationBuilder()
            .AddJsonFile(DesktopSettingsPath(), optional: true)
            .AddEnvironmentVariables("MAROCLIS_")
            .Build()
            .GetSection(DatabaseOptions.SectionName)["ConnectionString"];

        return string.IsNullOrWhiteSpace(configured) ? FallbackConnectionString : configured;
    }

    /// <summary>
    /// The desktop client's <c>appsettings.json</c>, so the tooling and the application agree on
    /// which database they are talking about. Located relative to this project rather than to the
    /// current directory, because <c>dotnet ef</c> may be invoked from anywhere in the tree.
    /// </summary>
    private static string DesktopSettingsPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MarocLIS.sln")))
            directory = directory.Parent;

        return directory is null
            ? "appsettings.json"
            : Path.Combine(directory.FullName, "src", "MarocLIS.Desktop", "appsettings.json");
    }
}
