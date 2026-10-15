using System.Linq.Expressions;
using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Quality;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Persistence.Migrations;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Instruments;
using MarocLIS.Instruments.Hosting;
using MarocLIS.Tests.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Clinical fixes of review cycle 4 (2026-10-15): analyser QC on a lot under evaluation evaluated,
/// and credited to it only during the evaluation session (K8), the control level found from the
/// control lot the analyser sends (K9). The queries (K8, K10, K11, K12) are checked against SQL
/// Server in <see cref="ReviewFixesCycle2ClinicalDatabaseTests"/>.
/// </summary>
public sealed class ReviewFixesCycle2ClinicalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateOnly Today = new(2026, 10, 15);

    // ---- K8: an unevaluated point is no verdict; transmitted acceptance points are evaluated ------

    [Fact]
    public void K8_TheGate_TakesTheLastEvaluatedPointOfEachLevel()
    {
        var level = Guid.NewGuid();
        var run = Guid.NewGuid();
        LotQcPoint Point(QcOutcome outcome, int hour, Guid? runId) =>
            new(Guid.NewGuid(), level, runId, outcome, Now.AddHours(hour));

        var verdict = LotQcGate.Evaluate(true, [new RequiredQcLevel(level, "GLU · niveau normal")],
        [
            Point(QcOutcome.Accepted, 0, run),
            // Tuesday's transmission, never evaluated: it says nothing about the lot.
            Point(QcOutcome.Pending, 22, null)
        ]);

        verdict.Kind.Should().Be(LotQcVerdictKind.Conform);

        LotQcGate.Evaluate(true, [new RequiredQcLevel(level, "GLU")],
                [Point(QcOutcome.Accepted, 0, run), Point(QcOutcome.Rejected, 2, null), Point(QcOutcome.Pending, 3, null)])
            .Kind.Should().Be(LotQcVerdictKind.LevelsNotConform, "the last verdict on the level is a rejection");
    }

    [Fact]
    public async Task K8_AnAcceptancePointSentByTheAnalyser_IsEvaluatedOnTheLotsOwnSeries()
    {
        var bench = new ControlBench();
        var newLot = Guid.NewGuid();
        bench.UnderEvaluation = newLot;

        // +4 SD against the level-1 target (mean 0.90, SD 0.05).
        await bench.SendAsync("N123", "1.10");

        var point = bench.Stored.Single();
        point.ReagentLotId.Should().Be(newLot);
        point.IsLotEvaluation.Should().BeTrue();
        point.ZScore.Should().Be(4m);
        point.QcRunId.Should().NotBeNull();
        point.Outcome.Should().Be(QcOutcome.Rejected);
        bench.HistoryLots.Should().Equal(newLot);

        await bench.SendAsync("N123", "0.91");
        bench.Stored[1].Outcome.Should().Be(QcOutcome.Accepted);
    }

    [Fact]
    public async Task K8_RoutineQcOutsideAnEvaluationSession_StaysOnTheLotInUse_ForReview()
    {
        var bench = new ControlBench();

        await bench.SendAsync("N123", "1.10");

        var point = bench.Stored.Single();
        point.ReagentLotId.Should().Be(bench.LotInUse);
        point.IsLotEvaluation.Should().BeFalse();
        point.Outcome.Should().Be(QcOutcome.Pending, "routine transmitted QC is judged in the QC screen");
        bench.HistoryLots.Should().BeEmpty();
    }

    // ---- K9: the level is the one of the control lot sent ------------------------------------------

    [Fact]
    public async Task K9_EachControlLot_GoesToItsOwnLevel()
    {
        var bench = new ControlBench();

        await bench.SendAsync("P456", "2.48");
        await bench.SendAsync(" n123 ", "0.91");

        bench.Stored.Should().HaveCount(2);
        bench.Stored[0].QcTargetId.Should().Be(bench.Level2.Id);
        bench.Stored[0].TargetMean.Should().Be(2.50m);
        bench.Stored[1].QcTargetId.Should().Be(bench.Level1.Id);
    }

    [Fact]
    public async Task K9_AControlLotWithNoTarget_IsNotStoredAgainstAGuessedLevel()
    {
        var bench = new ControlBench();

        await bench.SendAsync("X999", "0.91");

        bench.Stored.Should().BeEmpty();
    }

    /// <summary>
    /// An <see cref="InstrumentMessageSink"/> routing controls: GLU on one analyser with two levels,
    /// level 1 on control lot N123 (0.90 ± 0.05 g/L) and level 2 on lot P456 (2.50 ± 0.10 g/L).
    /// </summary>
    private sealed class ControlBench
    {
        public QcTarget Level1 { get; }
        public QcTarget Level2 { get; }
        public Guid LotInUse { get; } = Guid.NewGuid();
        public Guid? UnderEvaluation { get; set; }
        public List<QcResult> Stored { get; } = [];
        public List<Guid?> HistoryLots { get; } = [];

        private readonly InstrumentInterface _link;
        private readonly InstrumentMessageSink _sink;

        public ControlBench()
        {
            var siteId = Guid.NewGuid();
            var instrument = new Instrument("C311", new MultilingualText("Cobas"), EquipmentKind.Analyser, siteId);
            var parameter = new TestParameter("GLU", new MultilingualText("Glucose"), ResultValueType.Numeric);

            _link = new InstrumentInterface(instrument.Id, siteId, InstrumentProtocol.AstmE1394,
                                            InstrumentTransportKind.Tcp)
            {
                AutoAcceptResults = true
            };
            _link.AddMapping(new InstrumentTestMapping("GLU", parameter.Id));

            var material1 = new QcMaterial("N123", new MultilingualText("PreciControl 1"), QcLevel.Low, Today.AddDays(90));
            var material2 = new QcMaterial("P456", new MultilingualText("PreciControl 2"), QcLevel.High, Today.AddDays(90));
            Level1 = new QcTarget(material1.Id, parameter.Id, instrument.Id) { AssignedMean = 0.90m, AssignedSd = 0.05m };
            Level2 = new QcTarget(material2.Id, parameter.Id, instrument.Id) { AssignedMean = 2.50m, AssignedSd = 0.10m };

            List<QcTarget> targets = [Level1, Level2];
            List<QcMaterial> materials = [material1, material2];
            var self = this;
            var clock = Stub.Of<IClock>(new() { ["get_Now"] = _ => Now, ["get_Today"] = _ => Today });
            var analyses = Stub.Of<IRepository<AnalysisTest>>();
            var patients = Stub.Of<IRepository<Patient>>();
            var samples = Stub.Of<IRepository<Sample>>();

            var results = Stub.Of<IResultQueries>(new()
            {
                ["GetReagentLotsInUseAsync"] = args => Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(
                    ((IReadOnlyCollection<Guid>)args![0]!).ToDictionary(id => id, _ => self.LotInUse)),
                ["GetReagentLotsUnderEvaluationAsync"] = args => Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(
                    self.UnderEvaluation is { } lot
                        ? ((IReadOnlyCollection<Guid>)args![0]!).ToDictionary(id => id, _ => lot)
                        : new Dictionary<Guid, Guid>())
            });

            _sink = new InstrumentMessageSink(
                Stub.Of<IRepository<InstrumentInterface>>(new()
                {
                    ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<InstrumentInterface?>(self._link)
                }),
                samples,
                Stub.Of<IRepository<LabOrder>>(),
                analyses,
                Stub.Of<IRepository<TestResult>>(),
                patients,
                Stub.Of<IRepository<QcTarget>>(new()
                {
                    ["ListAsync"] = args => Task.FromResult<IReadOnlyList<QcTarget>>(
                        [.. targets.Where(((Expression<Func<QcTarget, bool>>)args![0]!).Compile())])
                }),
                Stub.Of<IRepository<QcResult>>(new()
                {
                    ["AddAsync"] = args => { self.Stored.Add((QcResult)args![0]!); return Task.CompletedTask; }
                }),
                Stub.Of<IRepository<QcMaterial>>(new()
                {
                    ["ListAsync"] = args => Task.FromResult<IReadOnlyList<QcMaterial>>(
                        [.. materials.Where(((Expression<Func<QcMaterial, bool>>)args![0]!).Compile())])
                }),
                Stub.Of<IQualityQueries>(new()
                {
                    ["GetZScoreHistoryAsync"] = args =>
                    {
                        self.HistoryLots.Add((Guid?)args![2]);
                        return Task.FromResult<IReadOnlyList<QcPoint>>([]);
                    },
                    ["GetMaterialLevelAsync"] = _ => Task.FromResult(QcLevel.Low)
                }),
                Stub.Of<IRepository<Instrument>>(new()
                {
                    ["GetByIdAsync"] = _ => Task.FromResult<Instrument?>(instrument)
                }),
                new ResultPostEntryPipeline(analyses, patients, samples, results, clock),
                Stub.Of<IInstrumentMessageLogStore>(new() { ["RecordAsync"] = _ => Task.FromResult(Guid.NewGuid()) }),
                Stub.Of<IUnitOfWork>(),
                clock,
                Stub.Of<IInstrumentServiceAccount>(new()
                {
                    ["get_UserId"] = _ => Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    ["get_IsResolved"] = _ => true,
                    ["EnsureResolvedAsync"] = _ => Task.FromResult(true)
                }));
        }

        public Task<bool> SendAsync(string controlLot, string value) =>
            _sink.HandleAsync(_link.Id, new InstrumentMessage
            {
                RawText = "test",
                Specimens =
                [
                    new InstrumentSpecimenResults
                    {
                        SpecimenId = "QC",
                        IsControl = true,
                        ControlIdentifier = controlLot,
                        Results = [new InstrumentResult { InstrumentTestCode = "GLU", Value = value, MeasuredAt = Now }]
                    }
                ]
            });
    }

    /// <summary>Interface stub: named members answer from the table, the rest return a neutral value.</summary>
    private class Stub : DispatchProxy
    {
        private Dictionary<string, Func<object?[]?, object?>> _handlers = [];

        public static T Of<T>(Dictionary<string, Func<object?[]?, object?>>? handlers = null) where T : class
        {
            var proxy = Create<T, Stub>();
            ((Stub)(object)proxy)._handlers = handlers ?? [];
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (_handlers.TryGetValue(targetMethod.Name, out var handler)) return handler(args);
            return Neutral(targetMethod.ReturnType);
        }

        private static object? Neutral(Type type)
        {
            if (type == typeof(void)) return null;
            if (type == typeof(Result)) return Result.Success();
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                                   .MakeGenericMethod(inner).Invoke(null, [Neutral(inner)]);
            }
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}

/// <summary>A scratch database for the cycle-2 clinical queries, created from the model and dropped.</summary>
public sealed class Cycle2ClinicalScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_T_c2clinical_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public Cycle2ClinicalScratchDatabase()
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

/// <summary>The queries behind K8, K10, K11 and K12, against SQL Server.</summary>
public sealed class ReviewFixesCycle2ClinicalDatabaseTests(Cycle2ClinicalScratchDatabase database)
    : IClassFixture<Cycle2ClinicalScratchDatabase>
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private async Task<(AnalysisTest Analysis, TestParameter Parameter)> CatalogueAsync()
    {
        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sérum"));
        var analysis = new AnalysisTest(Unique("GLU"), new MultilingualText("Glycémie"), department.Id, specimen.Id);
        var parameter = new TestParameter(Unique("G"), new MultilingualText("Glucose"), ResultValueType.Numeric);
        analysis.AddParameter(parameter);

        await using var context = database.Context();
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.AnalysisTests.Add(analysis);
        await context.SaveChangesAsync();

        return (analysis, parameter);
    }

    private static Instrument Analyser(Guid siteId) =>
        new(Unique("A"), new MultilingualText("Analyseur"), EquipmentKind.Analyser, siteId);

    private static QcResult Point(QcTarget target, Guid siteId, DateTimeOffset at, Guid? lot, bool evaluation,
                                  bool transmitted = false, QcOutcome outcome = QcOutcome.Accepted)
    {
        var point = new QcResult(target.Id, target.TestParameterId, target.InstrumentId, siteId, 100m, at, Guid.NewGuid())
        {
            ZScore = 0.1m,
            ReagentLotId = lot,
            IsLotEvaluation = evaluation,
            IsAutoTransmitted = transmitted
        };
        point.ApplyEvaluation(new WestgardEvaluation(WestgardRule.None, outcome, []));
        return point;
    }

    [SqlServerFact]
    public async Task K8_TheAnalyserCreditsTheNewLot_OnlyOnTheDayARunOnItWasEnteredInTheQcScreen()
    {
        var (analysis, parameter) = await CatalogueAsync();
        var siteId = Guid.NewGuid();
        var analyser = Analyser(siteId);
        var item = new StockItem(Unique("R"), new MultilingualText("Glucose R1"), StockItemKind.Reagent, "coffret", siteId)
        {
            UsedByAnalysisCodesCsv = analysis.Code
        };
        var monday = item.AddLot(new ReagentLot("GLU-L2", Today.AddDays(300), 4m, Today.AddDays(-3)));
        monday.StartEvaluationOn(analyser.Id).Should().BeTrue();
        var today = item.AddLot(new ReagentLot("GLU-L3", Today.AddDays(300), 4m, Today.AddDays(-3)));
        today.StartEvaluationOn(analyser.Id).Should().BeTrue();

        var target = new QcTarget(Guid.NewGuid(), parameter.Id, analyser.Id);

        await using (var context = database.Context())
        {
            context.StockItems.Add(item);
            context.QcTargets.Add(target);
            context.QcResults.AddRange(
                // GLU-L2: entered yesterday, and transmitted this morning (which starts nothing).
                Point(target, siteId, Now.AddDays(-1), monday.Id, evaluation: true),
                Point(target, siteId, Now.AddHours(-2), monday.Id, evaluation: true, transmitted: true));
            await context.SaveChangesAsync();
        }

        await using (var read = database.Context())
        {
            (await new ResultQueries(read).GetReagentLotsUnderEvaluationAsync([parameter.Id], siteId, analyser.Id, Today))
                .Should().BeEmpty("no run on a lot under evaluation was entered on this analyser today");
        }

        await using (var context = database.Context())
        {
            context.QcResults.Add(Point(target, siteId, Now.AddHours(-1), today.Id, evaluation: true));
            await context.SaveChangesAsync();
        }

        await using var again = database.Context();
        (await new ResultQueries(again).GetReagentLotsUnderEvaluationAsync([parameter.Id], siteId, analyser.Id, Today))
            .Should().Equal(new Dictionary<Guid, Guid> { [parameter.Id] = today.Id });
    }

    [SqlServerFact]
    public async Task K11_TheLevelsRequired_AreThoseOfTheAnalysersRunningTheLotsArticle()
    {
        var (analysis, parameter) = await CatalogueAsync();
        var siteId = Guid.NewGuid();
        var cobas = Analyser(siteId);
        var mindray = Analyser(siteId);
        var material = new QcMaterial(Unique("CTRL"), new MultilingualText("Contrôle"), QcLevel.Normal, Today.AddDays(90));
        var onCobas = new QcTarget(material.Id, parameter.Id, cobas.Id);
        var onMindray = new QcTarget(material.Id, parameter.Id, mindray.Id);

        StockItem Article(string name) =>
            new(Unique("R"), new MultilingualText(name), StockItemKind.Reagent, "coffret", siteId)
            {
                UsedByAnalysisCodesCsv = analysis.Code
            };

        var roche = Article("Glucose Roche");
        var previousRoche = roche.AddLot(new ReagentLot("RO-L1", Today.AddDays(100), 4m, Today.AddDays(-60)));
        var newRoche = roche.AddLot(new ReagentLot("RO-L2", Today.AddDays(300), 4m, Today.AddDays(-1)));
        var mindrayArticle = Article("Glucose Mindray");
        var mindrayLot = mindrayArticle.AddLot(new ReagentLot("MR-L1", Today.AddDays(100), 4m, Today.AddDays(-60)));
        var fresh = Article("Glucose nouveau fournisseur");
        var freshLot = fresh.AddLot(new ReagentLot("NF-L1", Today.AddDays(300), 4m, Today.AddDays(-1)));

        await using (var context = database.Context())
        {
            context.StockItems.AddRange(roche, mindrayArticle, fresh);
            context.Instruments.AddRange(cobas, mindray);
            context.QcMaterials.Add(material);
            context.QcTargets.AddRange(onCobas, onMindray);
            context.QcResults.AddRange(
                Point(onCobas, siteId, Now.AddDays(-30), previousRoche.Id, evaluation: false),
                Point(onMindray, siteId, Now.AddDays(-30), mindrayLot.Id, evaluation: false));
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var queries = new PurchasingQualityQueries(read);

        (await queries.GetLotQcEvidenceAsync(newRoche.Id, Today))!.RequiredLevels
            .Select(l => l.QcTargetId).Should().Equal(onCobas.Id);

        // An article no analyser has run QC with yet keeps every analyser of the site.
        (await queries.GetLotQcEvidenceAsync(freshLot.Id, Today))!.RequiredLevels
            .Select(l => l.QcTargetId).Should().BeEquivalentTo([onCobas.Id, onMindray.Id]);
    }

    [SqlServerFact]
    public async Task K12_TheRoutineChartAndTheEntrySheet_LeaveOutANewLotsAcceptancePoints()
    {
        var (_, parameter) = await CatalogueAsync();
        var siteId = Guid.NewGuid();
        var analyser = Analyser(siteId);
        var material = new QcMaterial(Unique("CTRL"), new MultilingualText("Contrôle"), QcLevel.Normal, Today.AddDays(90));
        var target = new QcTarget(material.Id, parameter.Id, analyser.Id) { AssignedMean = 100m, AssignedSd = 5m };

        var routine = Point(target, siteId, Now.AddHours(-5), Guid.NewGuid(), evaluation: false);
        var acceptance = Point(target, siteId, Now.AddHours(-1), Guid.NewGuid(), evaluation: true,
                               outcome: QcOutcome.Rejected);

        await using (var context = database.Context())
        {
            context.Instruments.Add(analyser);
            context.QcMaterials.Add(material);
            context.QcTargets.Add(target);
            context.QcResults.AddRange(routine, acceptance);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var queries = new QualityQueries(read);

        (await queries.GetLeveyJenningsAsync(target.Id, Now.AddDays(-30)))!.Points
            .Select(p => p.QcResultId).Should().Equal(routine.Id);

        var row = (await queries.GetQcEntrySheetAsync(siteId, analyser.Id, null, Today)).Single();
        row.LastMeasuredAt.Should().Be(routine.MeasuredAt);
        row.LastOutcome.Should().Be(QcOutcome.Accepted);
    }

    [SqlServerFact]
    public async Task K10_TheMigration_MarksTheQcOfALotAwaitingRelease_AsItsAcceptanceQc()
    {
        var siteId = Guid.NewGuid();
        var target = new QcTarget(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var item = new StockItem(Unique("R"), new MultilingualText("Glucose R1"), StockItemKind.Reagent, "coffret", siteId);

        var awaiting = item.AddLot(new ReagentLot("L-WAIT", Today.AddDays(300), 4m, Today.AddDays(-5)));
        var released = item.AddLot(new ReagentLot("L-REL", Today.AddDays(300), 4m, Today.AddDays(-20)));
        released.Release(Today.AddDays(-10), Guid.NewGuid(), "CQ conforme");

        var onAwaiting = Point(target, siteId, Now.AddDays(-3), awaiting.Id, evaluation: false);
        var beforeRelease = Point(target, siteId, Now.AddDays(-12), released.Id, evaluation: false);
        var releaseDayBeforeIssue = Point(target, siteId, Now.AddDays(-10).AddHours(-2), released.Id, evaluation: false);
        var afterIssue = Point(target, siteId, Now.AddDays(-10).AddHours(4), released.Id, evaluation: false);
        var later = Point(target, siteId, Now.AddDays(-2), released.Id, evaluation: false);
        var issue = new StockMovement(item.Id, released.Id, StockMovementKind.Issue, 1m, Now.AddDays(-10), Guid.NewGuid(), siteId);

        await using (var context = database.Context())
        {
            context.StockItems.Add(item);
            context.QcTargets.Add(target);
            context.QcResults.AddRange(onAwaiting, beforeRelease, releaseDayBeforeIssue, afterIssue, later);
            context.StockMovements.Add(issue);
            await context.SaveChangesAsync();

            var backfill = new RevueCycle1().UpOperations.OfType<SqlOperation>()
                .Single(o => o.Sql.Contains("IsLotEvaluation = 1"));
            await context.Database.ExecuteSqlRawAsync(backfill.Sql);
        }

        await using var read = database.Context();
        var ids = new[] { onAwaiting.Id, beforeRelease.Id, releaseDayBeforeIssue.Id, afterIssue.Id, later.Id };
        var marked = await read.QcResults.AsNoTracking().IgnoreQueryFilters()
            .Where(r => ids.Contains(r.Id) && r.IsLotEvaluation).Select(r => r.Id).ToListAsync();

        marked.Should().BeEquivalentTo([onAwaiting.Id, beforeRelease.Id, releaseDayBeforeIssue.Id]);
    }
}
