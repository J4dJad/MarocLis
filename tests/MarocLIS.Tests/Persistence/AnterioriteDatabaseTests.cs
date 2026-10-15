using FluentAssertions;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// A scratch database of its own, <c>MarocLIS_Anteriorite</c>, created from the model and dropped
/// at the end, foreign keys off — the same arrangement as <see cref="Clinical3ScratchDatabase"/>.
/// </summary>
public sealed class AnterioriteScratchDatabase : IDisposable
{
    public const string Name = "MarocLIS_Anteriorite";

    public string ConnectionString { get; }

    private readonly bool _created;

    public AnterioriteScratchDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            ConnectionString = "";
            return;
        }

        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = Name }.ConnectionString;

        using var context = Context();
        if (context.Database.CanConnect())
            throw new InvalidOperationException(
                $"Database {Name} already exists; the tests create and drop their own and will not touch an existing one.");

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
/// The antériorité chart's query: one parameter's validated values for the patient, merged files
/// included, oldest first, capped to the latest.
/// </summary>
public sealed class AnterioriteDatabaseTests(AnterioriteScratchDatabase database)
    : IClassFixture<AnterioriteScratchDatabase>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 14, 0, 0, TimeSpan.FromHours(1));

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static TestParameter Parameter(string code) =>
        new(code, new MultilingualText(code), ResultValueType.Numeric);

    private static TestResult Entered(LabOrder order, LabOrderItem item, TestParameter parameter,
                                      decimal value, DateTimeOffset at)
    {
        var result = new TestResult(order.Id, item.Id, parameter.Id, order.SiteId);
        result.EnterNumeric(parameter, value, at, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        return result;
    }

    private static TestResult Validated(LabOrder order, LabOrderItem item, TestParameter parameter,
                                        decimal value, DateTimeOffset at)
    {
        var result = Entered(order, item, parameter, value, at);
        result.TechnicallyValidate(at, Guid.NewGuid());
        result.BiologicallyValidate(at, Guid.NewGuid(), false, true).IsSuccess.Should().BeTrue();
        return result;
    }

    private static (LabOrder Order, LabOrderItem Item) Order(Guid patientId, Guid siteId, DateTimeOffset at)
    {
        var order = new LabOrder(Unique("A"), patientId, siteId, at, Guid.NewGuid());
        var item = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);
        order.AddItem(item);
        return (order, item);
    }

    /// <summary>
    /// A survivor with a merged duplicate, each with past orders; a value entered but never
    /// validated; another parameter on the same order; another patient's creatinine; and today's
    /// order, validated.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        var siteId = Guid.NewGuid();
        var creatinine = Parameter("CREA");
        var urea = Parameter("UREE");

        var survivor = new Patient(Unique("P"), "ALAOUI", "Salma", new DateOnly(1960, 1, 1), Gender.Female, siteId);
        var duplicate = new Patient(Unique("P"), "ALAOUI", "Salma", new DateOnly(1960, 1, 1), Gender.Female, siteId);
        duplicate.MarkMergedInto(survivor.Id);
        var stranger = new Patient(Unique("P"), "BENNANI", "Karim", new DateOnly(1970, 1, 1), Gender.Male, siteId);

        var (yearAgo, yearAgoItem) = Order(survivor.Id, siteId, Now.AddDays(-365));
        var (onDuplicate, onDuplicateItem) = Order(duplicate.Id, siteId, Now.AddDays(-180));
        var (lastMonth, lastMonthItem) = Order(survivor.Id, siteId, Now.AddDays(-30));
        var (pending, pendingItem) = Order(survivor.Id, siteId, Now.AddDays(-10));
        var (today, todayItem) = Order(survivor.Id, siteId, Now.AddHours(-3));
        var (theirs, theirsItem) = Order(stranger.Id, siteId, Now.AddDays(-20));

        var first = Validated(yearAgo, yearAgoItem, creatinine, 80m, Now.AddDays(-364));
        var merged = Validated(onDuplicate, onDuplicateItem, creatinine, 95m, Now.AddDays(-179));
        var recent = Validated(lastMonth, lastMonthItem, creatinine, 110m, Now.AddDays(-29));
        var todays = Validated(today, todayItem, creatinine, 130m, Now.AddHours(-1));

        await using (var context = database.Context())
        {
            context.Patients.AddRange(survivor, duplicate, stranger);
            context.LabOrders.AddRange(yearAgo, onDuplicate, lastMonth, pending, today, theirs);
            context.TestResults.AddRange(
                // Inserted out of date order, so the order of the answer is the query's.
                todays, recent, first, merged,
                Entered(pending, pendingItem, creatinine, 500m, Now.AddDays(-10)),
                Validated(lastMonth, lastMonthItem, urea, 7m, Now.AddDays(-29)),
                Validated(theirs, theirsItem, creatinine, 60m, Now.AddDays(-19)));
            await context.SaveChangesAsync();
        }

        return new Seeded(survivor, duplicate, creatinine, today, [first, merged, recent, todays]);
    }

    private sealed record Seeded(Patient Survivor, Patient Duplicate, TestParameter Creatinine,
                                 LabOrder Today, IReadOnlyList<TestResult> Expected);

    [SqlServerFact]
    public async Task History_IsThePatientsValidatedValues_MergedFileIncluded_OldestFirst()
    {
        var seeded = await SeedAsync();

        await using var read = database.Context();
        var history = await new ResultQueries(read).GetValidatedHistoryAsync(
            seeded.Survivor.Id, seeded.Creatinine.Id, 30);

        // The duplicate's value is there, the unvalidated 500 and the other patient's 60 are not,
        // the urea is not, and today's validated value closes the curve.
        history.Select(p => p.TestResultId).Should().Equal(seeded.Expected.Select(r => r.Id));
        history.Select(p => p.Value).Should().Equal(80m, 95m, 110m, 130m);
        history.Should().BeInAscendingOrder(p => p.ValidatedAt);
        history[^1].AccessionNumber.Should().Be(seeded.Today.AccessionNumber);
    }

    [SqlServerFact]
    public async Task History_ReadFromTheMergedDuplicate_IsTheSameCurve()
    {
        var seeded = await SeedAsync();

        await using var read = database.Context();
        var history = await new ResultQueries(read).GetValidatedHistoryAsync(
            seeded.Duplicate.Id, seeded.Creatinine.Id, 30);

        history.Select(p => p.TestResultId).Should().Equal(seeded.Expected.Select(r => r.Id));
    }

    [SqlServerFact]
    public async Task History_IsCappedToTheLatestValues_StillOldestFirst()
    {
        var seeded = await SeedAsync();

        await using var read = database.Context();
        var history = await new ResultQueries(read).GetValidatedHistoryAsync(
            seeded.Survivor.Id, seeded.Creatinine.Id, 2);

        history.Select(p => p.Value).Should().Equal(110m, 130m);
    }
}
