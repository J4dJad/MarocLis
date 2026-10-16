using FluentAssertions;
using MarocLIS.Domain.Billing;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// The counts on the status chips of the billing screen (direction A): one per status, over the same
/// ledger the list shows — same site, drafts left out, same search.
/// </summary>
public sealed class DirAFinanceDatabaseTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));

    [SqlServerFact]
    public async Task TheStatusCounts_MatchTheLedgerForEachStatus()
    {
        using var database = new ScratchDatabase();
        var siteId = Guid.NewGuid();

        var open1 = Issued(500m, "FAC/CASA/2026/000801", siteId, "ALAOUI Fatima");
        var open2 = Issued(400m, "FAC/CASA/2026/000802", siteId, "BENNANI Karim");
        var partial = Issued(1_000m, "FAC/CASA/2026/000803", siteId, "ALAOUI Fatima");
        partial.RecordPayment(new Payment(300m, PaymentMethod.Cash, Today, Guid.NewGuid(), siteId))
               .IsSuccess.Should().BeTrue();
        var paid = Issued(200m, "FAC/CASA/2026/000804", siteId, "ALAOUI Fatima");
        paid.RecordPayment(new Payment(200m, PaymentMethod.Card, Today, Guid.NewGuid(), siteId))
            .IsSuccess.Should().BeTrue();
        var draft = Draft(siteId, "ALAOUI Fatima");
        var elsewhere = Issued(900m, "FAC/RABAT/2026/000001", Guid.NewGuid(), "ALAOUI Fatima");

        await using (var db = database.Context())
        {
            db.Invoices.AddRange(open1, open2, partial, paid, draft, elsewhere);
            await db.SaveChangesAsync();
        }

        await using (var db = database.Context())
        {
            var queries = new BillingQueries(db);

            var counts = await queries.GetInvoiceStatusCountsAsync(siteId, null);
            counts.Should().BeEquivalentTo(new Dictionary<InvoiceStatus, int>
            {
                [InvoiceStatus.Issued] = 2,
                [InvoiceStatus.PartiallyPaid] = 1,
                [InvoiceStatus.Paid] = 1
            });

            // Each chip promises what the list then shows.
            foreach (var (status, count) in counts)
                (await queries.GetInvoicesAsync(siteId, status, null, 1, 50, Today)).TotalCount.Should().Be(count);
            (await queries.GetInvoicesAsync(siteId, null, null, 1, 50, Today)).TotalCount
                .Should().Be(counts.Values.Sum());

            var searched = await queries.GetInvoiceStatusCountsAsync(siteId, "alaoui");
            searched.Should().BeEquivalentTo(new Dictionary<InvoiceStatus, int>
            {
                [InvoiceStatus.Issued] = 1,
                [InvoiceStatus.PartiallyPaid] = 1,
                [InvoiceStatus.Paid] = 1
            });

            (await queries.GetInvoiceStatusCountsAsync(siteId, "000802")).Should().ContainSingle()
                .Which.Should().Be(new KeyValuePair<InvoiceStatus, int>(InvoiceStatus.Issued, 1));
        }
    }

    private static Invoice Draft(Guid siteId, string name) =>
        new(string.Empty, siteId, InvoiceKind.Invoice, Today, Guid.NewGuid())
        {
            BilledToName = name,
            PayerId = Guid.NewGuid()
        };

    private static Invoice Issued(decimal price, string number, Guid siteId, string name)
    {
        var invoice = Draft(siteId, name);
        invoice.AddLine(new InvoiceLine("Analyse", price)).IsSuccess.Should().BeTrue();
        invoice.Issue(number, Now, null).IsSuccess.Should().BeTrue();
        return invoice;
    }

    /// <summary>
    /// A database of its own, <c>MarocLIS_DirAFinance</c>, brought up by migration and dropped at the
    /// end. An existing database of that name is refused rather than reused.
    /// </summary>
    private sealed class ScratchDatabase : IDisposable
    {
        private const string Name = "MarocLIS_DirAFinance";
        private readonly string _connectionString;

        public ScratchDatabase()
        {
            _connectionString = new SqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable)) { InitialCatalog = Name }
                .ConnectionString;

            using var context = Context();
            if (context.Database.CanConnect())
                throw new InvalidOperationException($"Database {Name} already exists.");

            context.Database.Migrate();
        }

        public LisDbContext Context() =>
            new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(_connectionString).Options);

        public void Dispose()
        {
            SqlConnection.ClearAllPools();
            using var context = Context();
            context.Database.EnsureDeleted();
        }
    }
}
