using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Infrastructure.Persistence;

/// <summary>
/// Brings the database up on first run and keeps the reference data current.
/// <para>
/// The schema is applied by running the EF migrations, not by <c>EnsureCreated</c>. The two are not
/// interchangeable and mixing them is a trap: <c>EnsureCreated</c> builds the tables but writes
/// nothing to <c>cfg.__MigrationsHistory</c>, so a database stood up that way looks completely
/// unmigrated — the next <c>dotnet ef database update</c> tries to create all 65 tables again and
/// fails, and the site has no upgrade path without hand-writing the history rows. Since
/// <c>docs/DEPLOYMENT.md</c> tells a production site to deploy by migration, the first-run path has
/// to agree with it.
/// </para>
/// <para>
/// <c>Migrate</c> also covers both cases in one call — a new database and one already at an earlier
/// migration — which is what removes the need for the "created or mismatched" branch this used to
/// carry.
/// </para>
/// <para>
/// The append-only triggers on the traceability tables, which EF cannot express, now live in the
/// migration that creates those tables. They were previously applied here, which meant only
/// databases created by this code path ever got them.
/// </para>
/// </summary>
public sealed class DatabaseInitializer(
    LisDbContext context,
    IPasswordHasher passwordHasher,
    IClock clock,
    ILogger<DatabaseInitializer>? logger = null)
{
    /// <summary>
    /// Ensures the database exists, has the current schema, and holds the reference data the
    /// application cannot start without.
    /// </summary>
    public async Task<Result> InitializeAsync(bool seedDemoData = false, CancellationToken ct = default)
    {
        try
        {
            // Materialised once: GetPendingMigrationsAsync returns a lazy sequence, and asking it
            // for Any() and then Count() enumerated it twice.
            var pending = (await context.Database.GetPendingMigrationsAsync(ct)).ToList();

            if (pending.Count > 0)
            {
                logger?.LogInformation("Applying {Count} migration(s) to the MarocLIS database.",
                                        pending.Count);
                await context.Database.MigrateAsync(ct);
            }

            // Not a migration concern: a database that answers but holds somebody else's schema is
            // a misconfigured connection string, and saying so is more use than the foreign-key
            // error the seeder would otherwise raise.
            if (!await CanConnectAndHasSchemaAsync(ct))
                return Result.Failure(Error.Conflict("Db_SchemaMismatch"));

            await WarnIfTraceabilityUnprotectedAsync(ct);

            await ReferenceDataSeeder.SeedAsync(context, passwordHasher, clock, ct);

            if (seedDemoData)
                await DemoDataSeeder.SeedAsync(context, clock, ct);

            return Result.Success();
        }
        catch (SqlException ex)
        {
            logger?.LogError(ex, "Could not initialise the database.");
            return Result.Failure(Error.Conflict("Db_InitializationFailed", ex.Message));
        }
    }

    public Task<bool> CanConnectAsync(CancellationToken ct = default) =>
        context.Database.CanConnectAsync(ct);

    private async Task<bool> CanConnectAndHasSchemaAsync(CancellationToken ct)
    {
        if (!await context.Database.CanConnectAsync(ct)) return false;

        // A cheap probe: if the users table is absent, this is not a MarocLIS database.
        var count = await context.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value]
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = 'sec' AND t.name = 'Users'
                """)
            .FirstOrDefaultAsync(ct);

        return count > 0;
    }

    /// <summary>
    /// Logs a warning naming any traceability table whose append-only trigger is missing or
    /// disabled.
    /// <para>
    /// A warning and not a refusal to start. The triggers are installed by the initial migration,
    /// but a <c>db_owner</c> may legitimately disable one to make a correction, and the thing that
    /// goes wrong afterwards is forgetting to put it back — leaving an audit trail the application
    /// itself could rewrite, with nothing on screen to say so. Halting a laboratory's LIS over it
    /// would stop patient care to protect a log, which is the wrong trade; naming the table in the
    /// startup log is what lets the next audit find it.
    /// </para>
    /// </summary>
    private async Task WarnIfTraceabilityUnprotectedAsync(CancellationToken ct)
    {
        var unprotected = await GetUnprotectedTraceabilityTablesAsync(ct);

        if (unprotected.Count > 0)
        {
            logger?.LogWarning(
                "Append-only protection is missing or disabled on: {Triggers}. The traceability " +
                "tables can be modified until it is restored.",
                string.Join(", ", unprotected));
        }
    }

    /// <summary>
    /// The append-only triggers that should be guarding the traceability tables and are not.
    /// Empty when the installation is intact.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetUnprotectedTraceabilityTablesAsync(
        CancellationToken ct = default)
    {
        var expected = new[]
        {
            "aud.TR_AuditEntries_NoUpdate",
            "aud.TR_DataAccessLog_NoUpdate",
            "aud.TR_SecurityEvents_NoUpdate",
            "res.TR_ResultRevisions_NoUpdate"
        };

        // The schema comes from the parent table: sys.triggers has no schema_id of its own, because
        // a DML trigger lives in whatever schema owns the table it is attached to.
        var present = await context.Database
            .SqlQuery<string>($"""
                SELECT SCHEMA_NAME(o.schema_id) + '.' + tr.name AS [Value]
                FROM sys.triggers tr
                JOIN sys.objects o ON o.object_id = tr.parent_id
                WHERE tr.parent_class = 1 AND tr.is_disabled = 0
                """)
            .ToListAsync(ct);

        return [.. expected.Except(present, StringComparer.OrdinalIgnoreCase)];
    }
}
