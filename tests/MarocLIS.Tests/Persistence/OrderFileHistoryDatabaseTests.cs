using FluentAssertions;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Reports;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the "Consultation d'un dossier" screen, foreign keys off.</summary>
public sealed class OrderFileScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_ConsultationDossier_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public OrderFileScratchDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            ConnectionString = "";
            return;
        }

        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = Name }.ConnectionString;

        using var context = Context();
        context.Database.EnsureCreated();
        _created = true;

        context.Database.ExecuteSqlRaw("EXEC sp_MSforeachtable 'ALTER TABLE ? NOCHECK CONSTRAINT ALL'");
    }

    public LisDbContext Context() =>
        new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(ConnectionString).Options);

    public void Dispose()
    {
        if (!_created) return;
        SqlConnection.ClearAllPools();
        using var context = Context();
        context.Database.EnsureDeleted();
    }
}

/// <summary>
/// The history tab of the file screen: one line per save with a reason, across the file, its
/// analyses, its tubes and its results; the report's releases; nothing of another file or site.
/// </summary>
public sealed class OrderFileHistoryDatabaseTests(OrderFileScratchDatabase database)
    : IClassFixture<OrderFileScratchDatabase>
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 21, 31, 0, TimeSpan.Zero);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static AuditEntry Entry(string entity, Guid id, DateTimeOffset at, string? reason, Guid? correlation,
                                    string user = "accueil") =>
        new(entity, id, AuditOperation.Update, at)
        {
            Reason = reason,
            CorrelationId = correlation,
            UserName = user
        };

    [SqlServerFact]
    public async Task History_GroupsEachSave_AcrossTheFilesRows_AndAddsReportReleases_ForTheSiteOnly()
    {
        var site = Guid.NewGuid();
        var user = Guid.NewGuid();

        var order = new LabOrder(Unique("A"), Guid.NewGuid(), site, T0, user);
        var item = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);
        order.AddItem(item);
        var tube = new Sample(Unique("T"), order.Id, Guid.NewGuid(), site);
        var result = new TestResult(order.Id, item.Id, Guid.NewGuid(), site);

        var other = new LabOrder(Unique("A"), Guid.NewGuid(), site, T0, user);

        var registration = Guid.NewGuid();
        var amendment = Guid.NewGuid();
        var entry = Guid.NewGuid();

        var report = new LabReport(order.Id, site, 1, ReportCompleteness.Partial, AppLanguage.French,
                                   T0.AddHours(5), user) { SignatoryName = "Dr BIOLOGISTE" };

        await using (var context = database.Context())
        {
            context.LabOrders.AddRange(order, other);
            context.Samples.Add(tube);
            context.TestResults.Add(result);
            context.LabReports.Add(report);
            context.AuditEntries.AddRange(
                // One registration touching the file, its analysis and its tube: one line.
                Entry(nameof(LabOrder), order.Id, T0, "Enregistrement du dossier", registration),
                Entry(nameof(LabOrderItem), item.Id, T0.AddMilliseconds(5), "Enregistrement du dossier", registration),
                Entry(nameof(Sample), tube.Id, T0.AddMilliseconds(9), "Enregistrement du dossier", registration),
                Entry(nameof(LabOrder), order.Id, T0.AddHours(1), "Modification du dossier : prescripteur", amendment,
                      "secretaire"),
                Entry(nameof(TestResult), result.Id, T0.AddHours(2), "Saisie de résultats", entry, "technicien"),
                // A save with no correlation is a line of its own.
                Entry(nameof(Sample), tube.Id, T0.AddHours(3), "Réception de 1 tube(s)", null, "technicien"),
                // Without a reason, or on another file: not this file's history.
                Entry(nameof(LabOrder), order.Id, T0.AddHours(4), null, Guid.NewGuid()),
                Entry(nameof(LabOrder), other.Id, T0.AddHours(1), "Annulation du dossier", Guid.NewGuid()));
            await context.SaveChangesAsync();
        }

        await using (var context = database.Context())
        {
            var history = await new OrderFileQueries(context).GetHistoryAsync(order.Id, site);

            history.Select(h => (h.OccurredAt, h.Kind, h.UserName, h.Description, h.ReportVersion))
                .Should().Equal(
                    (T0, OrderFileEventKind.Change, "accueil", "Enregistrement du dossier", (int?)null),
                    (T0.AddHours(1), OrderFileEventKind.Change, "secretaire", "Modification du dossier : prescripteur", null),
                    (T0.AddHours(2), OrderFileEventKind.Change, "technicien", "Saisie de résultats", null),
                    (T0.AddHours(3), OrderFileEventKind.Change, "technicien", "Réception de 1 tube(s)", null),
                    (T0.AddHours(5), OrderFileEventKind.ReportReleased, "Dr BIOLOGISTE", null, 1));

            // Another site's operator sees nothing of the file.
            (await new OrderFileQueries(context).GetHistoryAsync(order.Id, Guid.NewGuid())).Should().BeEmpty();
        }
    }
}
