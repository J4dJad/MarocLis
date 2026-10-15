using FluentAssertions;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Results;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// A scratch database of its own, <c>MarocLIS_FixClinical3</c>, created from the model and dropped
/// at the end. Its foreign keys are switched off: the queries under test read a handful of rows,
/// and seeding the sites, catalogue and users those rows point at would bury what is tested.
/// </summary>
public sealed class Clinical3ScratchDatabase : IDisposable
{
    public const string Name = "MarocLIS_FixClinical3";

    public string ConnectionString { get; }

    private readonly bool _created;

    public Clinical3ScratchDatabase()
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
/// The SQL side of the second clinical review: history across a merge (K4), the QC window per
/// parameter (K6), and the reports-to-issue list against cancelled analyses (K8).
/// </summary>
public sealed class ReviewFixesClinical3DatabaseTests(Clinical3ScratchDatabase database)
    : IClassFixture<Clinical3ScratchDatabase>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 14, 0, 0, TimeSpan.FromHours(1));

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static TestParameter Parameter(string code) =>
        new(code, new MultilingualText(code), ResultValueType.Numeric);

    private static TestResult Entered(LabOrder order, LabOrderItem item, TestParameter parameter,
                                      decimal value, DateTimeOffset at, Guid? instrumentId = null)
    {
        var result = new TestResult(order.Id, item.Id, parameter.Id, order.SiteId) { InstrumentId = instrumentId };
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

    [SqlServerFact]
    public async Task K4_AnOrderOnAMergedDuplicate_SeesTheSurvivorsHistory()
    {
        var siteId = Guid.NewGuid();
        var k = Parameter("K");

        var survivor = new Patient(Unique("P"), "ALAOUI", "Salma", new DateOnly(1980, 1, 1), Gender.Female, siteId);
        var duplicate = new Patient(Unique("P"), "ALAOUI", "Salma", new DateOnly(1980, 1, 1), Gender.Female, siteId);
        duplicate.MarkMergedInto(survivor.Id);

        var (past, pastItem) = Order(survivor.Id, siteId, Now.AddDays(-40));
        var history = Validated(past, pastItem, k, 4.0m, Now.AddDays(-40));

        var (today, _) = Order(duplicate.Id, siteId, Now);

        await using (var context = database.Context())
        {
            context.Patients.AddRange(survivor, duplicate);
            context.LabOrders.AddRange(past, today);
            context.TestResults.Add(history);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var previous = await new ResultQueries(read).GetPreviousValueAsync(duplicate.Id, k.Id, today.RegisteredAt);

        previous.Should().NotBeNull("the duplicate's pending order belongs to the survivor's history");
        previous!.Value.Should().Be(4.0m);
        previous.TestResultId.Should().Be(history.Id);
    }

    [SqlServerFact]
    public async Task K6_EachRejectedParameter_IsFlaggedFromItsOwnLastAcceptedControl()
    {
        var siteId = Guid.NewGuid();
        var instrumentId = Guid.NewGuid();
        var glucose = Parameter("GLU");
        var creatinine = Parameter("CREA");

        QcResult Accepted(TestParameter p, DateTimeOffset at)
        {
            var point = new QcResult(Guid.NewGuid(), p.Id, instrumentId, siteId, 1m, at, Guid.NewGuid());
            point.ApplyEvaluation(WestgardEvaluation.InControl);
            return point;
        }

        var (order, item) = Order(Guid.NewGuid(), siteId, Now.AddDays(-1));
        var (morning, morningItem) = Order(Guid.NewGuid(), siteId, Now);

        // Glucose last accepted yesterday 08:00, creatinine this morning 08:00.
        var yesterday8 = Now.Date.AddDays(-1).AddHours(8);
        var today8 = Now.Date.AddHours(8);
        var nightGlucose = Entered(order, item, glucose, 5.5m, new DateTimeOffset(yesterday8.AddHours(14), Now.Offset), instrumentId);
        var nightCreatinine = Entered(order, item, creatinine, 80m, new DateTimeOffset(yesterday8.AddHours(14), Now.Offset), instrumentId);
        var morningGlucose = Entered(morning, morningItem, glucose, 5.6m, new DateTimeOffset(today8.AddHours(2), Now.Offset), instrumentId);

        await using (var context = database.Context())
        {
            context.QcResults.AddRange(Accepted(glucose, new DateTimeOffset(yesterday8, Now.Offset)),
                                       Accepted(creatinine, new DateTimeOffset(today8, Now.Offset)));
            context.LabOrders.AddRange(order, morning);
            context.TestResults.AddRange(nightGlucose, nightCreatinine, morningGlucose);
            await context.SaveChangesAsync();
        }

        await using (var context = database.Context())
        {
            var flagged = await new QualityQueries(context).FlagResultsCoveredByRejectedQcAsync(
                [glucose.Id, creatinine.Id], instrumentId, Guid.NewGuid());
            await context.SaveChangesAsync();

            flagged.Should().Be(2);
        }

        await using var read = database.Context();
        var flags = await read.TestResults.AsNoTracking()
            .Where(r => r.LabOrderId == order.Id || r.LabOrderId == morning.Id)
            .ToDictionaryAsync(r => r.Id, r => r.Flags.HasFlag(ResultFlag.QcOutOfControl));

        flags[nightGlucose.Id].Should().BeTrue("glucose was last accepted yesterday morning");
        flags[morningGlucose.Id].Should().BeTrue();
        flags[nightCreatinine.Id].Should().BeFalse("creatinine was accepted again this morning");
    }

    [SqlServerFact]
    public async Task K8_AValidatedResultOfACancelledAnalysis_DoesNotKeepTheFileOnTheReportsToIssue()
    {
        var siteId = Guid.NewGuid();
        var patient = new Patient(Unique("P"), "BENNANI", "Karim", new DateOnly(1985, 5, 5), Gender.Male, siteId);
        var (order, item) = Order(patient.Id, siteId, Now);
        var other = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);
        order.AddItem(other);

        var stranded = Validated(order, item, Parameter("HB"), 13m, Now);
        order.CancelItem(item.Id, Now, Guid.NewGuid(), "Tube refusé").IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
        {
            context.Patients.Add(patient);
            context.LabOrders.Add(order);
            context.TestResults.Add(stranded);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var pending = await new ReportQueries(read).GetPendingReleaseAsync(siteId, null);

        pending.Should().NotContain(r => r.LabOrderId == order.Id);
    }
}
