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
using MarocLIS.Domain.Sequencing;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Instruments;
using MarocLIS.Instruments.Hosting;
using MarocLIS.Instruments.Protocol;
using MarocLIS.Tests.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Clinical fixes of review cycle 5 (2026-10-15): analyser QC found from what the ASTM and HL7
/// parsers really put in the control identifier (K13), the new lot credited only with the controls
/// of an explicitly started evaluation run (K14), and a lot named in the QC screen applied to one
/// analyser's rows only (K15). The evaluation query is checked against SQL Server in
/// <see cref="ReviewFixesCycle3ClinicalDatabaseTests"/>.
/// </summary>
public sealed class ReviewFixesCycle3ClinicalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static readonly IReadOnlyList<string> ArchitectControl =
    [
        "H|\\^&|||ARCHITECT|||||||P|1|20261015103000",
        "P|1",
        "O|1|QC-LOT-882||^^^GLY|R|20261015100000|||||Q|||20261015095000|CONTROL-NORMAL",
        "R|1|^^^GLY|1.02|g/L|||N||F",
        "L|1|N"
    ];

    // OBR-15, the specimen source, is the whole control label: never a bare lot number.
    private const string Hl7Control =
        "MSH|^~\\&|BC6800|LAB|MAROCLIS|LAB|20261015103000||ORU^R01|MSG9|P|2.5\r" +
        "OBR|1|QC1|QC-SAMPLE-1|GLY^Glycémie^L|||20261015093000||||||||CONTROL LOT 882 HIGH\r" +
        "OBX|1|NM|GLY^Glycémie^L||1.02|g/L|||N|||F\r";

    // ---- K13: the target found from what the parsers really send ---------------------------------

    [Fact]
    public void K13_TheLotNumber_IsMatchedAsAWholeToken()
    {
        ControlLotMatcher.Names("QC-LOT-882", "882").Should().BeTrue();
        ControlLotMatcher.Names("lot882", "LOT882").Should().BeTrue();
        ControlLotMatcher.Names("LOT882", "882").Should().BeTrue("a letter then a digit start a new token");
        ControlLotMatcher.Names("QC-LOT-8821", "882").Should().BeFalse();
        ControlLotMatcher.Names("QC-LOT-882", "88").Should().BeFalse();
        ControlLotMatcher.Names("CONTROL-NORMAL", "882").Should().BeFalse();

        ControlLotMatcher.LevelNamed("CONTROL-NORMAL").Should().Be(QcLevel.Normal);
        ControlLotMatcher.LevelNamed("CONTROL LOT 882 HIGH").Should().Be(QcLevel.High);
        ControlLotMatcher.LevelNamed("QC-LOT-882").Should().BeNull();
    }

    [Fact]
    public async Task K13_AnAstmControl_WithTheDescriptorInTheIdentifier_IsStoredOnTheOnlyTarget()
    {
        var bench = new ControlBench(("882", QcLevel.Normal));

        var message = AstmMessageParser.Parse(ArchitectControl);
        message.Specimens.Single().ControlIdentifier.Should().Be("CONTROL-NORMAL");

        await bench.SendAsync(message);

        bench.Stored.Should().ContainSingle().Which.QcTargetId.Should().Be(bench.Targets[0].Id);
    }

    [Fact]
    public async Task K13_AnAstmControl_GoesToTheLevelWhoseLotIsInTheSampleIdentifier()
    {
        var bench = new ControlBench(("881", QcLevel.Low), ("882", QcLevel.Normal), ("883", QcLevel.High));

        await bench.SendAsync(AstmMessageParser.Parse(ArchitectControl));

        bench.Stored.Should().ContainSingle().Which.QcTargetId.Should().Be(bench.Targets[1].Id);
    }

    [Fact]
    public async Task K13_OneLotForSeveralLevels_IsToldApartByTheLevelTheDescriptorNames()
    {
        var bench = new ControlBench(("882", QcLevel.Low), ("882", QcLevel.Normal));

        await bench.SendAsync(AstmMessageParser.Parse(ArchitectControl));

        bench.Stored.Should().ContainSingle().Which.QcTargetId.Should().Be(bench.Targets[1].Id);
    }

    [Fact]
    public async Task K13_AnHl7Control_IsMatchedFromTheSpecimenSource()
    {
        var message = Hl7ResultParser.Parse(Hl7Control);
        message.Specimens.Single().IsControl.Should().BeTrue();
        message.Specimens.Single().ControlIdentifier.Should().Contain("882");

        var single = new ControlBench(("882", QcLevel.High));
        await single.SendAsync(message);
        single.Stored.Should().ContainSingle();

        var levels = new ControlBench(("881", QcLevel.Low), ("882", QcLevel.High));
        await levels.SendAsync(Hl7ResultParser.Parse(Hl7Control));
        levels.Stored.Should().ContainSingle().Which.QcTargetId.Should().Be(levels.Targets[1].Id);
    }

    [Fact]
    public async Task K13_SeveralTargetsLeft_StoreNothing_AndSaySoInTheirOwnWords()
    {
        var bench = new ControlBench(("A1", QcLevel.Low), ("B2", QcLevel.High));

        await bench.SendAsync(AstmMessageParser.Parse(ArchitectControl));

        bench.Stored.Should().BeEmpty("the level would be a guess");
        bench.Issues.Should().Contain("Interface_QcTargetAmbiguous").And.NotContain("Interface_QcTargetNotFound");
    }

    // ---- K14: the evaluation run is started explicitly -------------------------------------------

    [Fact]
    public void K14_TheEvaluationRun_IsStartedStoppedAndEndedExplicitly()
    {
        var analyser = Guid.NewGuid();
        var lot = new ReagentLot("GLU-L2", Today.AddDays(300), 4m, Today);

        lot.StartEvaluationOn(analyser).Should().BeTrue();
        lot.EvaluationStartedAt.Should().BeNull("knowing where the lot is loaded starts no run");

        lot.StartEvaluationOn(analyser, Now).Should().BeTrue();
        lot.EvaluationStartedAt.Should().Be(Now);

        lot.StartEvaluationOn(analyser).Should().BeTrue();
        lot.EvaluationStartedAt.Should().Be(Now, "a run typed on the same analyser keeps the run started");

        lot.StartEvaluationOn(Guid.NewGuid()).Should().BeTrue();
        lot.EvaluationStartedAt.Should().BeNull("a run started on another analyser does not follow the lot");

        lot.StartEvaluationOn(analyser, Now);
        lot.StopEvaluation();
        lot.EvaluationInstrumentId.Should().BeNull();
        lot.EvaluationStartedAt.Should().BeNull();

        lot.StartEvaluationOn(analyser, Now);
        lot.Release(Today, Guid.NewGuid(), "CQ conforme").IsSuccess.Should().BeTrue();
        lot.EvaluationStartedAt.Should().BeNull();
        lot.StartEvaluationOn(analyser, Now).Should().BeFalse();
    }

    [Fact]
    public async Task K14_TheQcScreen_StartsAndStopsTheRun_WhileATypedRunStartsNothing()
    {
        var qc = new QcBench();
        var lot = new ReagentLot("GLU-L2", Today.AddDays(300), 4m, Today);
        qc.NamedLot = lot;

        await qc.Service.SubmitAsync([new QcEntry(qc.A1Level.Id, 101m, lot.Id)]);
        lot.EvaluationInstrumentId.Should().Be(qc.A1Level.InstrumentId);
        lot.EvaluationStartedAt.Should().BeNull();

        (await qc.Service.StartLotEvaluationAsync(lot.Id, qc.A1Level.InstrumentId)).IsSuccess.Should().BeTrue();
        lot.EvaluationStartedAt.Should().Be(Now);

        (await qc.Service.StopLotEvaluationAsync(lot.Id)).IsSuccess.Should().BeTrue();
        lot.EvaluationStartedAt.Should().BeNull();

        lot.Reject("Colis chaud");
        (await qc.Service.StartLotEvaluationAsync(lot.Id, qc.A1Level.InstrumentId))
            .FirstError.ResourceKey.Should().Be("Lot_NotAwaitingRelease");
    }

    [Fact]
    public async Task K14_TheSink_CreditsTheNewLot_OnlyWithTheLevelsTheRunStillExpects()
    {
        var bench = new ControlBench(("881", QcLevel.Low), ("883", QcLevel.High));
        var newLot = Guid.NewGuid();
        bench.UnderEvaluation[bench.Targets[0].Id] = newLot;

        await bench.SendAsync(Control("881", "1.00"));
        await bench.SendAsync(Control("883", "1.00"));

        bench.Stored[0].ReagentLotId.Should().Be(newLot);
        bench.Stored[0].IsLotEvaluation.Should().BeTrue();
        bench.Stored[1].ReagentLotId.Should().Be(bench.LotInUse, "this level is routine QC of the lot in use");
        bench.Stored[1].IsLotEvaluation.Should().BeFalse();
        bench.Stored[1].Outcome.Should().Be(QcOutcome.Pending);
    }

    // ---- K15: a named lot applies to one analyser's rows -----------------------------------------

    [Fact]
    public async Task K15_ANamedLotOnTheRowsOfTwoAnalysers_IsRefused()
    {
        var qc = new QcBench();
        var lot = new ReagentLot("GLU-L2", Today.AddDays(300), 4m, Today);
        qc.NamedLot = lot;

        var submitted = await qc.Service.SubmitAsync(
            [new QcEntry(qc.A1Level.Id, 101m, lot.Id), new QcEntry(qc.A2Level.Id, 134m, lot.Id)]);

        submitted.IsFailure.Should().BeTrue();
        submitted.FirstError.ResourceKey.Should().Be("Qc_NamedLotOneAnalyser");
        qc.Stored.Should().BeEmpty();
        lot.EvaluationInstrumentId.Should().BeNull();
    }

    [Fact]
    public async Task K15_TheOtherAnalysersRows_StayRoutineQc_AndTheirRejectionFlagsPatientResults()
    {
        var qc = new QcBench();
        var lot = new ReagentLot("GLU-L2", Today.AddDays(300), 4m, Today);
        qc.NamedLot = lot;

        // A2's level 2 at +3.4 SD, typed in the same submission as A1's run on the new lot.
        var submitted = await qc.Service.SubmitAsync(
            [new QcEntry(qc.A1Level.Id, 101m, lot.Id), new QcEntry(qc.A2Level.Id, 134m)]);

        submitted.IsSuccess.Should().BeTrue();
        var onA1 = qc.Stored.Single(r => r.InstrumentId == qc.A1Level.InstrumentId);
        var onA2 = qc.Stored.Single(r => r.InstrumentId == qc.A2Level.InstrumentId);

        onA1.ReagentLotId.Should().Be(lot.Id);
        onA1.IsLotEvaluation.Should().BeTrue();
        onA2.ReagentLotId.Should().Be(qc.LotInUse);
        onA2.IsLotEvaluation.Should().BeFalse();
        onA2.IsRejected.Should().BeTrue();
        qc.FlagCalls.Should().Equal(qc.A2Level.InstrumentId);
        lot.EvaluationInstrumentId.Should().Be(qc.A1Level.InstrumentId);
    }

    // ---- Benches ---------------------------------------------------------------------------------

    private static InstrumentMessage Control(string identifier, string value) => new()
    {
        RawText = "test",
        Specimens =
        [
            new InstrumentSpecimenResults
            {
                SpecimenId = "QC",
                IsControl = true,
                ControlIdentifier = identifier,
                Results = [new InstrumentResult { InstrumentTestCode = "GLY", Value = value, MeasuredAt = Now }]
            }
        ]
    };

    /// <summary>A <see cref="QualityControlService"/> over stubs: GLU on analysers A1 and A2.</summary>
    private sealed class QcBench
    {
        public QcTarget A1Level { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) { AssignedMean = 100m, AssignedSd = 10m };
        public QcTarget A2Level { get; }
        public Guid LotInUse { get; } = Guid.NewGuid();
        public ReagentLot? NamedLot { get; set; }
        public List<QcResult> Stored { get; } = [];
        public List<Guid> FlagCalls { get; } = [];
        public QualityControlService Service { get; }

        public QcBench()
        {
            A2Level = new QcTarget(Guid.NewGuid(), A1Level.TestParameterId, Guid.NewGuid()) { AssignedMean = 100m, AssignedSd = 10m };
            var self = this;
            var siteId = Guid.NewGuid();

            Service = new QualityControlService(
                Stub.Of<IRepository<QcTarget>>(new()
                {
                    ["GetByIdAsync"] = args => Task.FromResult<QcTarget?>(
                        new[] { self.A1Level, self.A2Level }.FirstOrDefault(t => t.Id == (Guid)args![0]!))
                }),
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
                    ["GetZScoreHistoryAsync"] = _ => Task.FromResult<IReadOnlyList<QcPoint>>([]),
                    ["FlagResultsCoveredByRejectedQcAsync"] = args =>
                    {
                        self.FlagCalls.Add((Guid)args![1]!);
                        return Task.FromResult(1);
                    }
                }),
                Stub.Of<ISequenceAllocator>(),
                Stub.Of<IUnitOfWork>(),
                Stub.Of<IPermissionGuard>(),
                Stub.Of<ICurrentUser>(new()
                {
                    ["get_SiteId"] = _ => siteId,
                    ["get_UserId"] = _ => Guid.Parse("22222222-2222-2222-2222-222222222222")
                }),
                Stub.Of<IClock>(new() { ["get_Now"] = _ => Now, ["get_Today"] = _ => Today }));
        }
    }

    /// <summary>
    /// An <see cref="InstrumentMessageSink"/> routing the controls of one analyser: GLY with one
    /// target per (control lot, level) given, each 1.00 ± 0.05 g/L.
    /// </summary>
    private sealed class ControlBench
    {
        public List<QcTarget> Targets { get; } = [];
        public Guid LotInUse { get; } = Guid.NewGuid();
        public Dictionary<Guid, Guid> UnderEvaluation { get; } = [];
        public List<QcResult> Stored { get; } = [];
        public string Issues { get; private set; } = string.Empty;

        private readonly InstrumentInterface _link;
        private readonly InstrumentMessageSink _sink;

        public ControlBench(params (string Lot, QcLevel Level)[] controls)
        {
            var siteId = Guid.NewGuid();
            var instrument = new Instrument("ARCH", new MultilingualText("Architect"), EquipmentKind.Analyser, siteId);
            var parameter = new TestParameter("GLY", new MultilingualText("Glycémie"), ResultValueType.Numeric);

            _link = new InstrumentInterface(instrument.Id, siteId, InstrumentProtocol.AstmE1394,
                                            InstrumentTransportKind.Tcp)
            {
                AutoAcceptResults = true
            };
            _link.AddMapping(new InstrumentTestMapping("GLY", parameter.Id));

            List<QcMaterial> materials = [];
            foreach (var (lot, level) in controls)
            {
                var material = new QcMaterial(lot, new MultilingualText("Contrôle"), level, Today.AddDays(90));
                materials.Add(material);
                Targets.Add(new QcTarget(material.Id, parameter.Id, instrument.Id) { AssignedMean = 1.00m, AssignedSd = 0.05m });
            }

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
                    ((IReadOnlyDictionary<Guid, Guid>)args![0]!).Keys
                        .Where(self.UnderEvaluation.ContainsKey)
                        .ToDictionary(id => id, id => self.UnderEvaluation[id]))
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
                        [.. self.Targets.Where(((Expression<Func<QcTarget, bool>>)args![0]!).Compile())])
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
                    ["GetZScoreHistoryAsync"] = _ => Task.FromResult<IReadOnlyList<QcPoint>>([]),
                    ["GetMaterialLevelAsync"] = _ => Task.FromResult(QcLevel.Low)
                }),
                Stub.Of<IRepository<Instrument>>(new()
                {
                    ["GetByIdAsync"] = _ => Task.FromResult<Instrument?>(instrument)
                }),
                new ResultPostEntryPipeline(analyses, patients, samples, results, clock),
                Stub.Of<IInstrumentMessageLogStore>(new()
                {
                    ["RecordAsync"] = _ => Task.FromResult(Guid.NewGuid()),
                    ["MarkProcessedAsync"] = args => { self.Issues += (string?)args![4]; return Task.CompletedTask; }
                }),
                Stub.Of<IUnitOfWork>(),
                clock,
                Stub.Of<IInstrumentServiceAccount>(new()
                {
                    ["get_UserId"] = _ => Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    ["get_IsResolved"] = _ => true,
                    ["EnsureResolvedAsync"] = _ => Task.FromResult(true)
                }));
        }

        public Task<bool> SendAsync(InstrumentMessage message) => _sink.HandleAsync(_link.Id, message);
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

/// <summary>A scratch database for the cycle-3 clinical queries, created from the model and dropped.</summary>
public sealed class Cycle3ClinicalScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_T_c3clinical_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public Cycle3ClinicalScratchDatabase()
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

/// <summary>K14's evaluation query against SQL Server.</summary>
public sealed class ReviewFixesCycle3ClinicalDatabaseTests(Cycle3ClinicalScratchDatabase database)
    : IClassFixture<Cycle3ClinicalScratchDatabase>
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [SqlServerFact]
    public async Task K14_AStartedRun_TakesTheFirstControlOfEachLevel_AndNothingAfter()
    {
        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sérum"));
        var analysis = new AnalysisTest(Unique("GLU"), new MultilingualText("Glycémie"), department.Id, specimen.Id);
        var parameter = new TestParameter(Unique("G"), new MultilingualText("Glucose"), ResultValueType.Numeric);
        analysis.AddParameter(parameter);

        var siteId = Guid.NewGuid();
        var analyser = Guid.NewGuid();
        var item = new StockItem(Unique("R"), new MultilingualText("Glucose R1"), StockItemKind.Reagent, "coffret", siteId)
        {
            UsedByAnalysisCodesCsv = analysis.Code
        };
        var newLot = item.AddLot(new ReagentLot("GLU-L2", Today.AddDays(300), 4m, Today.AddDays(-1)));
        newLot.StartEvaluationOn(analyser, Now.AddHours(-1)).Should().BeTrue();
        var yesterday = item.AddLot(new ReagentLot("GLU-L3", Today.AddDays(300), 4m, Today.AddDays(-1)));

        var level1 = new QcTarget(Guid.NewGuid(), parameter.Id, analyser);
        var level2 = new QcTarget(Guid.NewGuid(), parameter.Id, analyser);
        var levels = new Dictionary<Guid, Guid> { [level1.Id] = parameter.Id, [level2.Id] = parameter.Id };

        await using (var context = database.Context())
        {
            context.Departments.Add(department);
            context.SpecimenTypes.Add(specimen);
            context.AnalysisTests.Add(analysis);
            context.StockItems.Add(item);
            await context.SaveChangesAsync();
        }

        async Task<IReadOnlyDictionary<Guid, Guid>> UnderEvaluationAsync(Guid on)
        {
            await using var read = database.Context();
            return await new ResultQueries(read).GetReagentLotsUnderEvaluationAsync(levels, siteId, on, Today);
        }

        (await UnderEvaluationAsync(analyser)).Should().Equal(
            new Dictionary<Guid, Guid> { [level1.Id] = newLot.Id, [level2.Id] = newLot.Id });
        (await UnderEvaluationAsync(Guid.NewGuid())).Should().BeEmpty("the run was started on another analyser");

        // Level 1 of the run arrives; a transmission received before the start counts for nothing.
        await using (var context = database.Context())
        {
            context.QcResults.AddRange(
                Received(level1, siteId, newLot.Id, Now.AddMinutes(5)),
                Received(level2, siteId, newLot.Id, Now.AddHours(-2)));
            await context.SaveChangesAsync();
        }

        (await UnderEvaluationAsync(analyser)).Should().Equal(
            new Dictionary<Guid, Guid> { [level2.Id] = newLot.Id },
            "the afternoon's level-1 control is routine QC of the lot in use");

        // A run started yesterday and never stopped takes nothing today; a stopped run neither.
        await using (var context = database.Context())
        {
            var lots = await context.ReagentLots.Where(l => l.StockItemId == item.Id).ToListAsync();
            lots.Single(l => l.Id == newLot.Id).StopEvaluation();
            lots.Single(l => l.Id == yesterday.Id).StartEvaluationOn(analyser, Now.AddDays(-1)).Should().BeTrue();
            await context.SaveChangesAsync();
        }

        (await UnderEvaluationAsync(analyser)).Should().BeEmpty();

        await using var check = database.Context();
        (await check.ReagentLots.AsNoTracking().SingleAsync(l => l.Id == yesterday.Id))
            .EvaluationStartedAt.Should().Be(Now.AddDays(-1));
    }

    private static QcResult Received(QcTarget target, Guid siteId, Guid lot, DateTimeOffset at)
    {
        var point = new QcResult(target.Id, target.TestParameterId, target.InstrumentId, siteId, 1m, at, Guid.NewGuid())
        {
            ReagentLotId = lot,
            IsLotEvaluation = true,
            IsAutoTransmitted = true,
            CreatedAt = at
        };
        return point;
    }
}
