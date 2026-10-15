using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Quality;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Domain.Sequencing;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Tests.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Clinical fixes of review cycle 3 (2026-10-15): a new lot's acceptance QC kept apart from routine
/// QC (K1) and credited to that lot when the analyser sends it (K2), the lot stamp of an unchanged
/// value (K4) and of a subcontracted one (K5), on-board stability from the unit on the bench (K6),
/// and who may reject a released lot (S3). The queries (K1, K2, K3, K7) are checked against SQL
/// Server in <see cref="ReviewFixesCycle1ClinicalDatabaseTests"/>.
/// </summary>
public sealed class ReviewFixesCycle1ClinicalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateOnly Today = new(2026, 10, 15);

    // ---- K1: QC on a lot under evaluation is not routine QC -------------------------------------

    [Fact]
    public async Task K1_ARejectedControlOnANewLot_FlagsNoPatientResult_AndRunsOnItsOwnSeries()
    {
        var qc = new QcBench();
        var newLot = QuarantinedLot("GLU-L2");
        qc.NamedLot = newLot;

        // +4 SD: 1-3s rejects the point.
        var outcome = await qc.Service.SubmitAsync([new QcEntry(qc.Target.Id, 140m, newLot.Id)]);

        outcome.Value.Results.Single().Outcome.Should().Be(QcOutcome.Rejected);
        qc.Stored.Single().IsLotEvaluation.Should().BeTrue();
        qc.Stored.Single().ReagentLotId.Should().Be(newLot.Id);
        qc.FlagCalls.Should().BeEmpty("the patient results were produced with the lot in use");
        qc.HistoryLots.Should().Equal(newLot.Id);
    }

    [Fact]
    public async Task K1_AnAcceptedControlOnANewLot_DoesNotShiftTheLaboratoryStatistics()
    {
        var qc = new QcBench();
        var newLot = QuarantinedLot("GLU-L2");
        qc.NamedLot = newLot;

        await qc.Service.SubmitAsync([new QcEntry(qc.Target.Id, 101m, newLot.Id)]);

        qc.Stored.Single().Outcome.Should().Be(QcOutcome.Accepted);
        qc.Target.LaboratoryPointCount.Should().Be(0);
    }

    [Fact]
    public async Task K1_RoutineQc_StillAccumulatesAndFlags()
    {
        var qc = new QcBench();

        await qc.Service.SubmitAsync([new QcEntry(qc.Target.Id, 101m)]);
        await qc.Service.SubmitAsync([new QcEntry(qc.Target.Id, 140m)]);

        qc.Stored.Should().OnlyContain(r => !r.IsLotEvaluation);
        qc.Target.LaboratoryPointCount.Should().Be(1);
        qc.FlagCalls.Should().ContainSingle();
        qc.HistoryLots.Should().Equal(new Guid?[] { null, null });
    }

    [Fact]
    public async Task K1_ANamedLotAlreadyReleased_IsRoutineQc()
    {
        var qc = new QcBench();
        var released = QuarantinedLot("GLU-L1");
        released.Release(Today.AddDays(-5), Guid.NewGuid(), "CQ conforme").IsSuccess.Should().BeTrue();
        qc.NamedLot = released;

        await qc.Service.SubmitAsync([new QcEntry(qc.Target.Id, 140m, released.Id)]);

        qc.Stored.Single().IsLotEvaluation.Should().BeFalse();
        qc.FlagCalls.Should().ContainSingle();
    }

    // ---- K2: the analyser's controls go to the lot under evaluation -----------------------------

    [Fact]
    public async Task K2_AManualRunOnANewLot_PutsItUnderEvaluationOnThatAnalyser()
    {
        var qc = new QcBench();
        var newLot = QuarantinedLot("GLU-L2");
        qc.NamedLot = newLot;

        await qc.Service.SubmitAsync([new QcEntry(qc.Target.Id, 101m, newLot.Id)]);

        newLot.EvaluationInstrumentId.Should().Be(qc.Target.InstrumentId);
    }

    [Fact]
    public void K2_OnlyALotAwaitingRelease_IsUnderEvaluation_AndReleaseOrRejectionEndsIt()
    {
        var analyser = Guid.NewGuid();
        var lot = QuarantinedLot("GLU-L2");

        lot.StartEvaluationOn(analyser).Should().BeTrue();
        lot.EvaluationInstrumentId.Should().Be(analyser);

        lot.Release(Today, Guid.NewGuid(), "CQ conforme").IsSuccess.Should().BeTrue();
        lot.EvaluationInstrumentId.Should().BeNull();
        lot.StartEvaluationOn(analyser).Should().BeFalse("a released lot is the lot in use, not under evaluation");

        var other = QuarantinedLot("GLU-L3");
        other.StartEvaluationOn(analyser);
        other.Reject("Colis chaud").IsSuccess.Should().BeTrue();
        other.EvaluationInstrumentId.Should().BeNull();
    }

    // ---- K4 / K5: the lot stamp at result entry ---------------------------------------------------

    [Fact]
    public async Task K4_EditingOnlyTheComment_KeepsTheLotTheValueWasMeasuredWith()
    {
        var bench = new EntryBench();
        var (k, result) = bench.Add("K", subcontracted: false);
        var mondayLot = Guid.NewGuid();
        result.EnterNumeric(k, 6.1m, Now.AddDays(-2), Guid.NewGuid(), fromInstrument: true).IsSuccess.Should().BeTrue();
        result.ReagentLotId = mondayLot;

        var outcome = await bench.Service.SubmitAsync(bench.Order.Id,
            [new ResultEntry(result.Id, "6.1", "hémolyse légère")]);

        outcome.Value.Accepted.Should().Be(1);
        result.TechnicalComment.Should().Be("hémolyse légère");
        result.ReagentLotId.Should().Be(mondayLot);
    }

    [Fact]
    public async Task K4_ANewValue_GetsTheLotInUse()
    {
        var bench = new EntryBench();
        var (k, result) = bench.Add("K", subcontracted: false);
        result.EnterNumeric(k, 6.1m, Now.AddDays(-2), Guid.NewGuid(), fromInstrument: true);
        result.ReagentLotId = Guid.NewGuid();

        await bench.Service.SubmitAsync(bench.Order.Id, [new ResultEntry(result.Id, "4.2")]);

        result.ReagentLotId.Should().Be(bench.LotInUse);
    }

    [Fact]
    public async Task K5_ASubcontractedValue_GetsNoLocalLot()
    {
        var bench = new EntryBench();
        var (_, result) = bench.Add("TSH", subcontracted: true);

        await bench.Service.SubmitAsync(bench.Order.Id, [new ResultEntry(result.Id, "2.3")]);

        result.Flags.Should().HaveFlag(ResultFlag.Subcontracted);
        result.ReagentLotId.Should().BeNull("the partner's sample never touched the laboratory's lot");
    }

    // ---- K6: on-board stability from the unit on the bench -------------------------------------

    [Fact]
    public void K6_AFreshKitOfALotOpenedAMonthAgo_IsIssued_AndTheLotIsInUse()
    {
        var item = new StockItem("GLU-R1", new MultilingualText("Glucose R1"), StockItemKind.Reagent, "coffret",
                                 Guid.NewGuid()) { UsedByAnalysisCodesCsv = "GLU" };
        var lot = item.AddLot(new ReagentLot("GLU-L1", Today.AddYears(1), 6m, Today.AddDays(-40))
        {
            StabilityAfterOpeningDays = 28
        });
        lot.Release(Today.AddDays(-40), Guid.NewGuid(), "CQ conforme").IsSuccess.Should().BeTrue();

        lot.Consume(1m, Today.AddDays(-30)).IsSuccess.Should().BeTrue();   // kit 1
        lot.IsUsable(Today).Should().BeFalse("kit 1 is past its 28 days on board");
        item.NextLotToUse(Today).Should().BeSameAs(lot, "the kits left on the shelf are sealed");

        lot.Consume(1m, Today).IsSuccess.Should().BeTrue();                // kit 2, sealed until today

        lot.OpenedOn.Should().Be(Today.AddDays(-30));
        lot.LastOpenedOn.Should().Be(Today);
        lot.IsUsable(Today).Should().BeTrue();
        ReagentLotSelector.LotInUse([item], "GLU", Today).Should().BeSameAs(lot);
        lot.IsUsable(Today.AddDays(29)).Should().BeFalse("kit 2 runs out of stability in its turn");
    }

    // ---- S3: rejecting a released lot is a quality decision -------------------------------------

    [Fact]
    public async Task S3_ReceiveAlone_RejectsALotInQuarantine_ButNotAReleasedOne()
    {
        var quarantined = QuarantinedLot("GLU-L2");
        (await Inventory(quarantined, denied: Permissions.Quality.ReviewQc).RejectLotAsync(quarantined.Id, "Colis chaud"))
            .IsSuccess.Should().BeTrue();
        quarantined.IsRejected.Should().BeTrue();

        var released = QuarantinedLot("GLU-L1");
        released.Release(Today.AddDays(-7), Guid.NewGuid(), "CQ conforme");

        var refused = await Inventory(released, denied: Permissions.Quality.ReviewQc)
            .RejectLotAsync(released.Id, "Mauvaise ligne");

        refused.IsFailure.Should().BeTrue();
        refused.FirstError.Code.Should().Be("forbidden");
        released.IsRejected.Should().BeFalse();
        released.IsQuarantined.Should().BeFalse();

        (await Inventory(released, denied: Permissions.Inventory.Receive).RejectLotAsync(released.Id, "Dérive du CQ"))
            .IsSuccess.Should().BeTrue("whoever may release a lot may withdraw it");
        released.IsRejected.Should().BeTrue();
    }

    // ---- Harness -------------------------------------------------------------------------------

    private static ReagentLot QuarantinedLot(string number) =>
        new(number, Today.AddYears(1), 4m, Today.AddDays(-10));

    private static InventoryService Inventory(ReagentLot lot, params string[] denied) =>
        new(Stub.Of<IRepository<StockItem>>(),
            Stub.Of<IRepository<StockMovement>>(),
            Stub.Of<IRepository<Supplier>>(),
            Stub.Of<IInventoryQueries>(new() { ["GetLotEntityAsync"] = _ => Task.FromResult<ReagentLot?>(lot) }),
            Stub.Of<IUnitOfWork>(),
            Guard(denied),
            CurrentUser(),
            Clock());

    private static IPermissionGuard Guard(params string[] denied) => Stub.Of<IPermissionGuard>(new()
    {
        ["Require"] = args => denied.Contains((string)args![0]!)
            ? Result.Failure(Error.Forbidden("Forbidden"))
            : Result.Success(),
        ["RequireAny"] = args => ((string[])args![0]!).Any(p => !denied.Contains(p))
            ? Result.Success()
            : Result.Failure(Error.Forbidden("Forbidden"))
    });

    private static ICurrentUser CurrentUser() => Stub.Of<ICurrentUser>(new()
    {
        ["get_UserId"] = _ => Guid.NewGuid(),
        ["get_SiteId"] = _ => Guid.NewGuid(),
        ["get_Language"] = _ => AppLanguage.French,
        ["Has"] = _ => true
    });

    private static IClock Clock() => Stub.Of<IClock>(new()
    {
        ["get_Now"] = _ => Now,
        ["get_Today"] = _ => Today
    });

    /// <summary>A <see cref="QualityControlService"/> over stubs with one target and one lot in use.</summary>
    private sealed class QcBench
    {
        public QcTarget Target { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            AssignedMean = 100m,
            AssignedSd = 10m
        };

        public Guid LotInUse { get; } = Guid.NewGuid();
        public ReagentLot? NamedLot { get; set; }
        public List<QcResult> Stored { get; } = [];
        public List<Guid?> HistoryLots { get; } = [];
        public List<Guid> FlagCalls { get; } = [];
        public QualityControlService Service { get; }

        public QcBench()
        {
            var self = this;

            Service = new QualityControlService(
                Stub.Of<IRepository<QcTarget>>(new() { ["GetByIdAsync"] = _ => Task.FromResult<QcTarget?>(self.Target) }),
                Stub.Of<IRepository<QcResult>>(new()
                {
                    ["AddAsync"] = args => { self.Stored.Add((QcResult)args![0]!); return Task.CompletedTask; }
                }),
                Stub.Of<IRepository<QcMaterial>>(),
                Stub.Of<IRepository<NonConformity>>(),
                Stub.Of<IQualityQueries>(new()
                {
                    ["GetReagentLotsForQcAsync"] = args => Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(
                        ((IReadOnlyCollection<Guid>)args![0]!).ToDictionary(
                            id => id, _ => (Guid?)args[1] is { } named ? named : self.LotInUse)),
                    ["GetReagentLotAsync"] = args =>
                        Task.FromResult(self.NamedLot?.Id == (Guid)args![0]! ? self.NamedLot : null),
                    ["GetZScoreHistoryAsync"] = args =>
                    {
                        self.HistoryLots.Add((Guid?)args![2]);
                        return Task.FromResult<IReadOnlyList<QcPoint>>([]);
                    },
                    ["FlagResultsCoveredByRejectedQcAsync"] = args =>
                    {
                        self.FlagCalls.Add((Guid)args![1]!);
                        return Task.FromResult(1);
                    }
                }),
                Stub.Of<ISequenceAllocator>(),
                Stub.Of<IUnitOfWork>(),
                Stub.Of<IPermissionGuard>(),
                CurrentUser(),
                Clock());
        }
    }

    /// <summary>A <see cref="ResultEntryService"/> over stubs: one order, the lot in use answered by the stock.</summary>
    private sealed class EntryBench
    {
        public LabOrder Order { get; }
        public ResultEntryService Service { get; }
        public Guid LotInUse { get; } = Guid.NewGuid();

        private readonly Patient _patient = new("P-0001", "BENNANI", "Karim", new DateOnly(1985, 5, 5),
                                                 Gender.Male, Guid.NewGuid());
        private readonly List<TestResult> _results = [];
        private readonly Dictionary<Guid, AnalysisTest> _analyses = [];

        public EntryBench()
        {
            Order = new LabOrder("2610-000100", _patient.Id, Guid.NewGuid(), Now.AddHours(-3), Guid.NewGuid());

            var order = Order;
            var self = this;

            Service = new ResultEntryService(
                Stub.Of<IRepository<LabOrder>>(new()
                {
                    ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<LabOrder?>(order),
                    ["ListAsync"] = _ => Task.FromResult<IReadOnlyList<LabOrder>>([order])
                }),
                Stub.Of<IRepository<AnalysisTest>>(new()
                {
                    ["GetByIdWithDetailsAsync"] = args =>
                        Task.FromResult(self._analyses.GetValueOrDefault((Guid)args![0]!))
                }),
                Stub.Of<IRepository<Patient>>(new() { ["GetByIdAsync"] = _ => Task.FromResult<Patient?>(self._patient) }),
                Stub.Of<IRepository<Sample>>(),
                Stub.Of<IResultQueries>(new()
                {
                    ["GetResultsForOrderAsync"] = _ => Task.FromResult<IReadOnlyList<TestResult>>(self._results.ToList()),
                    ["GetActiveReflexRulesAsync"] = _ => Task.FromResult<IReadOnlyList<ReflexRule>>([]),
                    ["GetPreviousValuesAsync"] = _ => Task.FromResult<IReadOnlyDictionary<Guid, PreviousValue>>(
                        new Dictionary<Guid, PreviousValue>()),
                    ["GetReagentLotsInUseAsync"] = args => Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(
                        ((IReadOnlyCollection<Guid>)args![0]!).ToDictionary(id => id, _ => self.LotInUse))
                }),
                Stub.Of<IUnitOfWork>(),
                Stub.Of<IPermissionGuard>(),
                CurrentUser(),
                Stub.Of<IDataAccessAuditor>(),
                Clock());
        }

        public (TestParameter Parameter, TestResult Result) Add(string code, bool subcontracted)
        {
            var parameter = new TestParameter(code, new MultilingualText(code), ResultValueType.Numeric);
            var analysis = new AnalysisTest(code, new MultilingualText(code), Guid.NewGuid(), Guid.NewGuid());
            analysis.AddParameter(parameter);
            _analyses[analysis.Id] = analysis;

            var item = new LabOrderItem(analysis.Id, OrderPriority.Routine) { IsSubcontracted = subcontracted };
            Order.AddItem(item);

            var result = new TestResult(Order.Id, item.Id, parameter.Id, Order.SiteId);
            _results.Add(result);
            return (parameter, result);
        }
    }

    /// <summary>
    /// Interface stub: named members answer from the handler table, everything else returns a
    /// neutral value: success for a <see cref="Result"/>, a completed task, or the default.
    /// </summary>
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

/// <summary>A scratch database for the cycle-1 clinical queries, created from the model and dropped.</summary>
public sealed class Cycle1ClinicalScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_T_c1clinical_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public Cycle1ClinicalScratchDatabase()
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

/// <summary>The queries behind K1, K2, K3 and K7, against SQL Server.</summary>
public sealed class ReviewFixesCycle1ClinicalDatabaseTests(Cycle1ClinicalScratchDatabase database)
    : IClassFixture<Cycle1ClinicalScratchDatabase>
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
        new(Unique("A"), new MultilingualText("Cobas"), EquipmentKind.Analyser, siteId);

    [SqlServerFact]
    public async Task K3_TheLevelsRequired_AreThoseOfTheLotsSite_OnAnalysersInService()
    {
        var (analysis, parameter) = await CatalogueAsync();
        var siteA = Guid.NewGuid();
        var siteB = Guid.NewGuid();
        var item = new StockItem(Unique("R"), new MultilingualText("Glucose R1"), StockItemKind.Reagent, "coffret", siteA)
        {
            UsedByAnalysisCodesCsv = analysis.Code
        };
        var lot = item.AddLot(new ReagentLot("GLU-NEW", Today.AddDays(300), 4m, Today));
        var material = new QcMaterial(Unique("CTRL"), new MultilingualText("Contrôle"), QcLevel.Normal, Today.AddDays(90));

        var a1 = Analyser(siteA);
        var b1 = Analyser(siteB);
        var retired = Analyser(siteA);
        retired.Decommission(Today.AddDays(-30), "Remplacé");

        var onA1 = new QcTarget(material.Id, parameter.Id, a1.Id);
        var onB1 = new QcTarget(material.Id, parameter.Id, b1.Id);
        var onRetired = new QcTarget(material.Id, parameter.Id, retired.Id);

        await using (var context = database.Context())
        {
            context.StockItems.Add(item);
            context.Instruments.AddRange(a1, b1, retired);
            context.QcMaterials.Add(material);
            context.QcTargets.AddRange(onA1, onB1, onRetired);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var evidence = await new PurchasingQualityQueries(read).GetLotQcEvidenceAsync(lot.Id, Today);

        evidence!.RequiredLevels.Select(l => l.QcTargetId).Should().Equal(onA1.Id);
    }

    [SqlServerFact]
    public async Task K2_K7_TheLotUnderEvaluationOnTheAnalyser_AndTheLotInUse_AreFound()
    {
        var (analysis, parameter) = await CatalogueAsync();
        var siteId = Guid.NewGuid();
        var analyser = Guid.NewGuid();
        var item = new StockItem(Unique("R"), new MultilingualText("Glucose R1"), StockItemKind.Reagent, "coffret", siteId)
        {
            UsedByAnalysisCodesCsv = analysis.Code
        };

        var inUse = item.AddLot(new ReagentLot("GLU-L1", Today.AddDays(100), 4m, Today.AddDays(-20)));
        inUse.Release(Today.AddDays(-20), Guid.NewGuid(), "CQ conforme");
        inUse.Consume(1m, Today.AddDays(-2)).IsSuccess.Should().BeTrue();

        // Years of history that can no longer answer: exhausted and expired, rejected.
        var old = item.AddLot(new ReagentLot("GLU-L0", Today.AddDays(-10), 1m, Today.AddDays(-400)));
        old.Release(Today.AddDays(-400), Guid.NewGuid(), "CQ conforme");
        old.OpenedOn = Today.AddDays(-300);
        var refused = item.AddLot(new ReagentLot("GLU-LX", Today.AddDays(200), 1m, Today.AddDays(-5)));
        refused.Reject("Colis chaud");

        var newLot = item.AddLot(new ReagentLot("GLU-L2", Today.AddDays(300), 4m, Today.AddDays(-1)));
        // Its evaluation run started on the analyser this morning (cycle 3 K14: explicitly).
        newLot.StartEvaluationOn(analyser, Now.AddHours(-1)).Should().BeTrue();

        // The run entered on it today in the QC screen.
        var run = new QcResult(Guid.NewGuid(), parameter.Id, analyser, siteId, 100m, Now, Guid.NewGuid())
        {
            ReagentLotId = newLot.Id,
            IsLotEvaluation = true
        };

        await using (var context = database.Context())
        {
            context.StockItems.Add(item);
            context.QcResults.Add(run);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var queries = new ResultQueries(read);

        (await queries.GetReagentLotsInUseAsync([parameter.Id], siteId, Today))
            .Should().Equal(new Dictionary<Guid, Guid> { [parameter.Id] = inUse.Id });
        var level = Guid.NewGuid();
        (await queries.GetReagentLotsUnderEvaluationAsync(new Dictionary<Guid, Guid> { [level] = parameter.Id }, siteId, analyser, Today))
            .Should().Equal(new Dictionary<Guid, Guid> { [level] = newLot.Id });
        (await queries.GetReagentLotsUnderEvaluationAsync(new Dictionary<Guid, Guid> { [level] = parameter.Id }, siteId, Guid.NewGuid(), Today))
            .Should().BeEmpty("the new lot is loaded on another analyser only");

        // The QC screen naming the new lot still finds it, although it is neither opened nor released.
        (await new QualityQueries(read).GetReagentLotsForQcAsync([parameter.Id], newLot.Id, siteId, Today))
            .Should().Equal(new Dictionary<Guid, Guid> { [parameter.Id] = newLot.Id });
    }

    [SqlServerFact]
    public async Task K1_TheRoutineSeriesAndTheFlaggingWindow_IgnoreANewLotsAcceptanceRun()
    {
        var siteId = Guid.NewGuid();
        var instrument = Guid.NewGuid();
        var parameter = Guid.NewGuid();
        var target = new QcTarget(Guid.NewGuid(), parameter, instrument);
        var newLot = Guid.NewGuid();

        QcResult Point(int hoursAgo, decimal z, bool evaluation)
        {
            var point = new QcResult(target.Id, parameter, instrument, siteId, 100m, Now.AddHours(-hoursAgo), Guid.NewGuid())
            {
                ZScore = z,
                IsLotEvaluation = evaluation,
                ReagentLotId = evaluation ? newLot : null
            };
            point.ApplyEvaluation(new WestgardEvaluation(WestgardRule.None, QcOutcome.Accepted, []));
            return point;
        }

        // L1 accepted at 08:00 (8 h ago), the new lot's acceptance run accepted at 13:00 (3 h ago).
        var routine = Point(8, 0.5m, evaluation: false);
        var evaluation = Point(3, 3.4m, evaluation: true);

        // A patient result measured with L1 at 10:00, between the two.
        var patientResult = new TestResult(Guid.NewGuid(), Guid.NewGuid(), parameter, siteId)
        {
            InstrumentId = instrument,
            MeasuredAt = Now.AddHours(-6)
        };

        await using (var context = database.Context())
        {
            context.QcTargets.Add(target);
            context.QcResults.AddRange(routine, evaluation);
            context.TestResults.Add(patientResult);
            await context.SaveChangesAsync();
        }

        await using (var read = database.Context())
        {
            var queries = new QualityQueries(read);
            (await queries.GetZScoreHistoryAsync(target.Id, 12)).Select(p => p.ZScore).Should().Equal(0.5m);
            (await queries.GetZScoreHistoryAsync(target.Id, 12, newLot)).Select(p => p.ZScore).Should().Equal(3.4m);
        }

        // L1's control is rejected now: the 10:00 result is covered, the new lot's run does not bound it.
        await using (var write = database.Context())
        {
            var flagged = await new QualityQueries(write).FlagResultsCoveredByRejectedQcAsync(
                [parameter], instrument, Guid.NewGuid());
            await write.SaveChangesAsync();
            flagged.Should().Be(1);
        }
    }
}
