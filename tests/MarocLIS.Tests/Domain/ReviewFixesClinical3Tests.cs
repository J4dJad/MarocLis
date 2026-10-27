using System.Linq.Expressions;
using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Instruments;
using MarocLIS.Instruments.Hosting;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Clinical fixes from the second review of 2026-10-15: analyser results through the post-entry
/// pipeline (K1), "not done" printed only once validated (K2), a critical call from a stale screen
/// (K3), merged duplicates (K4), recollection and the analyser (K5), repeating an analysis (K7),
/// partly printed analyses (K8), entry time of analyser values (K9) and age bands in days (K10).
/// K6 needs SQL Server and is in <see cref="Persistence.ReviewFixesClinical3DatabaseTests"/>.
/// </summary>
public sealed class ReviewFixesClinical3Tests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static TestParameter Numeric(string code, decimal? low = null, decimal? high = null)
    {
        var parameter = new TestParameter(code, new MultilingualText(code), ResultValueType.Numeric);
        if (low is not null || high is not null) parameter.AddReferenceRange(ReferenceRange.Numeric(low, high));
        return parameter;
    }

    private const string Friedewald = "if([TG]<4;round([CHOL]-[HDL]-[TG]/5;2);nd())";

    // ---- K1: analyser values go through the same steps as typed ones ---------------------------

    private static TestParameter Potassium()
    {
        var k = Numeric("K", 3.5m, 5.0m);
        k.DeltaCheckPercent = 20m;
        k.DeltaCheckWindowDays = 30;
        return k;
    }

    [Fact]
    public async Task K1_AnalyserValue_GetsItsPreviousValue_AndIsDeltaChecked()
    {
        var bench = new SinkBench();
        var k = bench.AddAnalysis(bench.Tube, Potassium())[0];
        bench.Previous = new PreviousValue(4.0m, Today.AddDays(-2), Guid.NewGuid());

        (await bench.SendAsync(bench.Tube, ("K", "6.5"))).Should().BeTrue();

        k.NumericValue.Should().Be(6.5m);
        k.PreviousNumericValue.Should().Be(4.0m);
        k.PreviousResultDate.Should().Be(Today.AddDays(-2));
        k.Flags.Should().HaveFlag(ResultFlag.DeltaCheckFailed);
        k.Flags.Should().HaveFlag(ResultFlag.High);
    }

    [Fact]
    public async Task K1_AnalyserRerun_KeepsADeltaFlagThatStillApplies_AndDropsOneThatNoLongerDoes()
    {
        var bench = new SinkBench();
        var k = bench.AddAnalysis(bench.Tube, Potassium())[0];
        bench.Previous = new PreviousValue(4.0m, Today.AddDays(-2), Guid.NewGuid());

        await bench.SendAsync(bench.Tube, ("K", "6.5"));
        k.Flags.Should().HaveFlag(ResultFlag.DeltaCheckFailed);

        // The re-run still differs from the history: interpreting clears the flag, the check
        // puts it back.
        await bench.SendAsync(bench.Tube, ("K", "6.4"));
        k.Flags.Should().HaveFlag(ResultFlag.DeltaCheckFailed);

        await bench.SendAsync(bench.Tube, ("K", "4.1"));
        k.Flags.Should().NotHaveFlag(ResultFlag.DeltaCheckFailed);
    }

    [Fact]
    public async Task K1_AnalyserLipids_ComputeTheLdl_AndCompleteTheAnalysis()
    {
        var bench = new SinkBench();
        var ldl = Numeric("LDL", null, 1.60m);
        ldl.CalculationFormula = Friedewald;
        var rows = bench.AddAnalysis(bench.Tube, Numeric("CHOL"), Numeric("HDL"), Numeric("TG"), ldl);

        await bench.SendAsync(bench.Tube, ("CHOL", "2.50"), ("HDL", "0.50"), ("TG", "1.50"));

        rows[3].NumericValue.Should().Be(1.70m);
        rows[3].Flags.Should().HaveFlag(ResultFlag.High);
        bench.Order.Items.Single().Status.Should().Be(LabOrderItemStatus.ResultEntered);
    }

    [Fact]
    public async Task K1_AnalyserValue_OnAHaemolysedTube_IsFlagged()
    {
        var bench = new SinkBench(SampleDefect.Haemolysed);
        var k = bench.AddAnalysis(bench.Tube, Potassium())[0];

        await bench.SendAsync(bench.Tube, ("K", "4.4"));

        k.Flags.Should().HaveFlag(ResultFlag.SampleDefect);
    }

    [Fact]
    public async Task K1_AnalyserValue_OnAMergedDuplicate_IsInterpretedWithTheSurvivorsDemographics()
    {
        // The duplicate was typed as male by mistake; the survivor is the woman she is.
        var bench = new SinkBench();
        var survivor = new Patient("P-0001", "ALAOUI", "Salma", new DateOnly(1990, 1, 1), Gender.Female, Guid.NewGuid());
        bench.Patients[survivor.Id] = survivor;
        bench.Patient.MarkMergedInto(survivor.Id);

        var hb = new TestParameter("HB", new MultilingualText("HB"), ResultValueType.Numeric);
        hb.AddReferenceRange(ReferenceRange.Numeric(13.0m, 17.0m, Gender.Male));
        hb.AddReferenceRange(ReferenceRange.Numeric(12.0m, 16.0m, Gender.Female));
        var row = bench.AddAnalysis(bench.Tube, hb)[0];

        await bench.SendAsync(bench.Tube, ("HB", "12.5"));

        row.Interpretation.Should().Be(ResultInterpretation.Normal);
    }

    // ---- K5: recollection ---------------------------------------------------------------------

    [Fact]
    public async Task K5_AfterARecollection_TheAnalyserValueGoesToTheReplacement_NotTheCancelledRow()
    {
        var bench = new SinkBench();
        var k = Potassium();
        var refusedTube = bench.Tube;
        var refused = bench.AddAnalysis(refusedTube, k)[0];

        // The refused tube's analysis is cancelled and replaced on a new tube, for the same
        // parameter; the cancelled row is listed first.
        var newTube = bench.AddTube("T-0002");
        var replacement = bench.AddAnalysis(newTube, bench.Order.Items.Single(), k)[0];
        bench.Order.Items.Should().ContainSingle(i => i.IsCancelled);

        await bench.SendAsync(newTube, ("K", "4.2"));

        replacement.NumericValue.Should().Be(4.2m);
        refused.HasValue.Should().BeFalse();
    }

    [Fact]
    public void K5_ARowBoundToAnotherTube_IsNotThisTubes()
    {
        var order = new LabOrder("2610-000001", Guid.NewGuid(), Guid.NewGuid(), Now, Guid.NewGuid());
        var item = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine) { SampleId = Guid.NewGuid() };
        order.AddItem(item);
        var parameterId = Guid.NewGuid();
        var row = new TestResult(order.Id, item.Id, parameterId, order.SiteId);

        InstrumentMessageSink.TargetFor(order, [row], Guid.NewGuid(), parameterId).Should().BeNull();
        InstrumentMessageSink.TargetFor(order, [row], item.SampleId!.Value, parameterId).Should().BeSameAs(row);
    }

    // ---- K9: analyser values are stamped when they reach the LIS -------------------------------

    [Fact]
    public async Task K9_AnalyserValue_IsEnteredWhenItArrives_AndMeasuredWhenTheAnalyserSaysSo()
    {
        var bench = new SinkBench();
        var k = bench.AddAnalysis(bench.Tube, Potassium())[0];
        var measured = Now.AddMinutes(-25);

        await bench.SendAsync(bench.Tube, measured, ("K", "6.4"));

        k.EnteredAt.Should().Be(Now);
        k.MeasuredAt.Should().Be(measured);

        // A re-run carries its own measurement time, not the first run's.
        await bench.SendAsync(bench.Tube, Now.AddMinutes(-5), ("K", "6.3"));
        k.MeasuredAt.Should().Be(Now.AddMinutes(-5));
    }

    // ---- An older frame never overwrites a newer value ------------------------------------------

    [Fact]
    public async Task AcceptingTheOlderOfTwoHeldTransmissionsLast_KeepsTheNewerValue_AndLogsTheOlderAsNotApplied()
    {
        var bench = new SinkBench();
        var k = bench.AddAnalysis(bench.Tube, Potassium())[0];
        bench.Link.AutoAcceptResults = false;

        bench.At = Now.AddHours(-1);                       // 09:00: haemolysed first run
        var older = await bench.SendFrameAsync(bench.Tube, ("K", "6.8"));
        bench.At = Now.AddMinutes(-30);                    // 09:30: the re-run
        var newer = await bench.SendFrameAsync(bench.Tube, ("K", "4.5"));

        bench.At = Now;                                    // The technician accepts the newer one first.
        (await bench.ReplayAsync(newer)).Value.Should().Be(1);
        k.NumericValue.Should().Be(4.5m);
        k.MeasuredAt.Should().Be(Now.AddMinutes(-30), "a frame without its own time was measured when it arrived");

        bench.At = Now.AddMinutes(1);
        (await bench.ReplayAsync(older)).Value.Should().Be(0);

        k.NumericValue.Should().Be(4.5m, "the older transmission does not overwrite the newer value");
        k.EnteredAt.Should().Be(Now);
        bench.Log[older].Status.Should().Be(InstrumentMessageProcessingStatus.Failed);
        bench.Log[older].Issues.Should().Contain("Interface_NewerValueStored");
    }

    [Fact]
    public async Task ReplayingAnOldFrame_LeavesAValueTypedSince_AndAValueAlreadyImported_AndAppliesOnlyWhatIsMissing()
    {
        var bench = new SinkBench();
        var results = bench.AddAnalysis(bench.Tube, Potassium(), Numeric("NA"));
        var (k, na) = (results[0], results[1]);
        var cl = Numeric("CL");

        bench.At = Now.AddHours(-1);                       // 09:00: CL is not mapped yet.
        var frame = await bench.SendFrameAsync(bench.Tube, ("K", "6.8"), ("NA", "140"), ("CL", "101"));
        bench.Log[frame].Status.Should().Be(InstrumentMessageProcessingStatus.PartiallyImported);
        var naEnteredAt = na.EnteredAt;

        // 09:20: the technician types the re-run's potassium.
        k.EnterNumeric(Potassium(), 4.5m, Now.AddMinutes(-40), Guid.NewGuid()).IsSuccess.Should().BeTrue();

        // 10:00: CL is mapped, and the frame replayed.
        var clRow = bench.AddAnalysis(bench.Tube, cl)[0];
        bench.At = Now;
        (await bench.ReplayAsync(frame)).IsSuccess.Should().BeTrue();

        clRow.NumericValue.Should().Be(101m);
        k.NumericValue.Should().Be(4.5m, "a value typed after the frame was received is the newer one");
        na.EnteredAt.Should().Be(naEnteredAt, "a value already imported from this frame is not imported again");
        bench.Log[frame].Status.Should().Be(InstrumentMessageProcessingStatus.PartiallyImported);
        bench.Log[frame].Issues.Should().Contain("Interface_NewerValueStored");

        // Once the newer value is removed, the frame can be replayed explicitly.
        k.ClearValue().IsSuccess.Should().BeTrue();
        await bench.ReplayAsync(frame);
        k.NumericValue.Should().Be(6.8m);
    }

    // ---- K2: "not done" is printed once a biologist validated its analysis ---------------------

    [Fact]
    public void K2_AnAutomaticNotDone_IsNotReleasable_UntilItsAnalysisIsValidated()
    {
        var order = new LabOrder("2610-000002", Guid.NewGuid(), Guid.NewGuid(), Now, Guid.NewGuid());
        var lipids = ReceivedItem(order);
        lipids.MarkResultEntered(Now);
        var ldl = new TestResult(order.Id, lipids.Id, Guid.NewGuid(), order.SiteId);
        ldl.MarkNotDone(ResultPostEntryPipeline.NotCalculableComment, Now, Guid.NewGuid());

        ReportReleaseService.SelectReleasable(order, [ldl]).Releasable.Should().BeEmpty();

        lipids.MarkTechnicallyValidated(Now, Guid.NewGuid());
        lipids.MarkBiologicallyValidated(Now, Guid.NewGuid());

        ReportReleaseService.SelectReleasable(order, [ldl]).Releasable.Should().ContainSingle();
    }

    // ---- K3: a critical call names the value the screen showed ---------------------------------

    [Fact]
    public async Task K3_ACriticalCall_FromAScreenLoadedBeforeARerun_IsRefused()
    {
        var result = new TestResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var k = Potassium();
        result.EnterNumeric(k, 7.2m, Now.AddMinutes(-10), Guid.NewGuid());
        var shown = result.EnteredAt;

        // The re-run lands while the file stands open.
        result.EnterNumeric(k, 2.1m, Now.AddMinutes(-2), Guid.NewGuid(), fromInstrument: true);

        var added = new List<CriticalResultNotification>();
        var service = new ValidationService(
            Stub.Of<IRepository<LabOrder>>(),
            Stub.Of<IRepository<AnalysisTest>>(),
            Stub.Of<IRepository<Instrument>>(),
            Stub.Of<IRepository<CriticalResultNotification>>(new()
            {
                ["AddAsync"] = args => { added.Add((CriticalResultNotification)args![0]!); return Task.CompletedTask; }
            }),
            Stub.Of<IResultQueries>(),
            Stub.Of<IValidationQueries>(new()
            {
                ["GetResultAsync"] = _ => Task.FromResult<TestResult?>(result)
            }),
            Stub.Of<IUnitOfWork>(),
            Stub.Of<IPermissionGuard>(),
            CurrentUser(),
            Stub.Of<IDataAccessAuditor>(),
            Clock());

        var stale = await service.RaiseCriticalNotificationAsync(
            result.Id, "K", "7,2 mmol/L", NotificationChannel.Telephone, null, null, shown);

        stale.IsFailure.Should().BeTrue();
        stale.FirstError.ResourceKey.Should().Be("Notification_ValueChangedSinceDisplayed");
        added.Should().BeEmpty();

        var current = await service.RaiseCriticalNotificationAsync(
            result.Id, "K", "2,1 mmol/L", NotificationChannel.Telephone, null, null, result.EnteredAt);

        current.IsSuccess.Should().BeTrue();
        added.Should().ContainSingle();
    }

    // ---- K4: the pipeline reads a merged duplicate's orders with the survivor ------------------

    [Fact]
    public async Task K4_TheOrderOfAMergedDuplicate_IsReadWithTheSurvivingFile()
    {
        var survivor = new Patient("P-0001", "ALAOUI", "Salma", new DateOnly(1990, 1, 1), Gender.Female, Guid.NewGuid());
        var duplicate = new Patient("P-0002", "ALAOUI", "Salma", new DateOnly(1990, 1, 1), Gender.Male, Guid.NewGuid());
        duplicate.MarkMergedInto(survivor.Id);
        var all = new[] { survivor, duplicate }.ToDictionary(p => p.Id);

        var pipeline = new ResultPostEntryPipeline(
            Stub.Of<IRepository<AnalysisTest>>(),
            Stub.Of<IRepository<Patient>>(new()
            {
                ["GetByIdAsync"] = args => Task.FromResult(all.GetValueOrDefault((Guid)args![0]!))
            }),
            Stub.Of<IRepository<Sample>>(),
            Stub.Of<IResultQueries>(),
            Clock());

        var order = new LabOrder("2610-000003", duplicate.Id, Guid.NewGuid(), Now, Guid.NewGuid());

        (await pipeline.PatientOfAsync(order)).Should().BeSameAs(survivor);
    }

    // ---- K7: repeating an analysis -------------------------------------------------------------

    [Fact]
    public void K7_AValidatedAnalysis_CannotBeRepeated()
    {
        var order = new LabOrder("2610-000004", Guid.NewGuid(), Guid.NewGuid(), Now, Guid.NewGuid());
        var item = ReceivedItem(order);
        item.MarkResultEntered(Now);
        item.MarkTechnicallyValidated(Now, Guid.NewGuid());
        item.MarkBiologicallyValidated(Now, Guid.NewGuid());

        item.Repeat("Valeur suspecte", Now).IsFailure.Should().BeTrue();
        item.Status.Should().Be(LabOrderItemStatus.Validated);
    }

    [Fact]
    public async Task K7_RepeatingAnAnalysisWithAReleasedValue_IsRefused()
    {
        var (service, order, item, results) = RepeatBench(validated: true);

        var repeated = await service.RepeatItemAsync(order.Id, item.Id, "Valeur suspecte");

        repeated.FirstError.ResourceKey.Should().Be("Item_RepeatRequiresUnvalidation");
        results.Should().OnlyContain(r => r.IsValidated);
    }

    [Fact]
    public async Task K7_RepeatingATechnicallyValidatedAnalysis_TakesItsValuesOffTheBiologistsList()
    {
        var (service, order, item, results) = RepeatBench(validated: false);

        (await service.RepeatItemAsync(order.Id, item.Id, "Valeur suspecte")).IsSuccess.Should().BeTrue();

        item.Status.Should().Be(LabOrderItemStatus.Received);
        results.Should().OnlyContain(r => r.Status == TestResultStatus.Entered && r.HasValue);
        results[0].CanBiologicallyValidate(false, true).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task K7_RepeatingATechnicallyValidatedAnalysis_NeedsTheRightToModify()
    {
        var (service, order, item, results) = RepeatBench(validated: false, canModify: false);

        var repeated = await service.RepeatItemAsync(order.Id, item.Id, "Valeur suspecte");

        repeated.FirstError.ResourceKey.Should().Be("Result_ModifyRequiresPermission");
        item.Status.Should().Be(LabOrderItemStatus.TechnicallyValidated);
        results.Should().OnlyContain(r => r.Status == TestResultStatus.TechnicallyValidated);
    }

    private static (OrderAmendmentService, LabOrder, LabOrderItem, List<TestResult>) RepeatBench(
        bool validated, bool canModify = true)
    {
        var order = new LabOrder("2610-000005", Guid.NewGuid(), Guid.NewGuid(), Now, Guid.NewGuid());
        var item = ReceivedItem(order);
        item.MarkResultEntered(Now);
        item.MarkTechnicallyValidated(Now, Guid.NewGuid());

        var hb = Numeric("HB");
        var plt = Numeric("PLT");
        var results = new List<TestResult>
        {
            new(order.Id, item.Id, hb.Id, order.SiteId),
            new(order.Id, item.Id, plt.Id, order.SiteId)
        };
        results[0].EnterNumeric(hb, 13.2m, Now, Guid.NewGuid());
        results[1].EnterNumeric(plt, 250m, Now, Guid.NewGuid());
        foreach (var r in results) r.TechnicallyValidate(Now, Guid.NewGuid());

        if (validated)
        {
            foreach (var r in results) r.BiologicallyValidate(Now, Guid.NewGuid(), false, true);
            item.MarkBiologicallyValidated(Now, Guid.NewGuid());
        }

        var service = new OrderAmendmentService(
            Stub.Of<IRepository<LabOrder>>(new() { ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<LabOrder?>(order) }),
            Stub.Of<IRepository<Patient>>(),
            Stub.Of<IRepository<Sample>>(),
            null!,
            Stub.Of<IOrderQueries>(),
            Stub.Of<IUnitOfWork>(),
            Stub.Of<IPermissionGuard>(),
            CurrentUser(canModify),
            Stub.Of<IDataAccessAuditor>(),
            Clock(),
            Stub.Of<IRepository<TestResult>>(new()
            {
                ["ListAsync"] = _ => Task.FromResult<IReadOnlyList<TestResult>>(results)
            }));

        return (service, order, item, results);
    }

    // ---- K8: an analysis partly printed is not cancelled ---------------------------------------

    [Fact]
    public void K8_AnAnalysisWithAPrintedResult_CannotBeCancelled()
    {
        var order = new LabOrder("2610-000006", Guid.NewGuid(), Guid.NewGuid(), Now, Guid.NewGuid());
        var blood = ReceivedItem(order);
        var other = ReceivedItem(order);
        blood.MarkResultEntered(Now);
        blood.MarkTechnicallyValidated(Now, Guid.NewGuid());

        // Only the haemoglobin was validated and released: the analysis stays technically
        // validated, and is recorded as printed.
        blood.MarkReported(Now).IsFailure.Should().BeTrue();
        blood.RecordPartlyReported(Now);

        order.CancelItem(blood.Id, Now, Guid.NewGuid(), "Erreur de saisie")
            .FirstError.ResourceKey.Should().Be("Order_ItemPartlyReportedCannotCancel");
        order.Cancel(Now, Guid.NewGuid(), "Erreur").IsFailure.Should().BeTrue();
        blood.IsCancelled.Should().BeFalse();

        order.CancelItem(other.Id, Now, Guid.NewGuid(), "Non demandé").IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void K8_APartlyPrintedAnalysis_CanStillHaveAnUnprintedValueWithdrawn()
    {
        var order = new LabOrder("2610-000007", Guid.NewGuid(), Guid.NewGuid(), Now, Guid.NewGuid());
        var item = ReceivedItem(order);
        item.MarkResultEntered(Now);
        item.MarkTechnicallyValidated(Now, Guid.NewGuid());
        item.RecordPartlyReported(Now);
        item.MarkBiologicallyValidated(Now, Guid.NewGuid());

        item.Unvalidate("Plaquettes à contrôler").IsSuccess.Should().BeTrue();
        item.Status.Should().Be(LabOrderItemStatus.TechnicallyValidated);
    }

    // ---- K10: age bands typed in days ----------------------------------------------------------

    [Fact]
    public void K10_ABandTypedInDays_IsComparedInDays()
    {
        var neonatal = ReferenceRange.Numeric(14m, 22m, ageFromDays: 0, ageToDays: 30);
        neonatal.AgeUnitDays = 1;

        var born = new DateOnly(2026, 1, 31);
        var baby = new Patient("P-0003", "BEBE", "Adam", born, Gender.Male, Guid.NewGuid());

        // 28 days old on 28 February: still inside "0–30 jours"; read as "0–1 mois" it was not.
        neonatal.Matches(ReferenceRangeContext.For(baby, new DateOnly(2026, 2, 28))).Should().BeTrue();
        neonatal.Matches(ReferenceRangeContext.For(baby, new DateOnly(2026, 3, 2))).Should().BeFalse();

        var month = ReferenceRange.Numeric(14m, 22m, ageFromDays: 0, ageToDays: 30);
        month.AgeUnitDays = 30;
        month.Matches(ReferenceRangeContext.For(baby, new DateOnly(2026, 2, 28))).Should().BeFalse();

        // A band saved before the unit was kept is read as before.
        ReferenceRange.Numeric(14m, 22m, ageFromDays: 0, ageToDays: 30).EffectiveAgeUnitDays.Should().Be(30);
    }

    // ---- Harnesses -------------------------------------------------------------------------------

    private static LabOrderItem ReceivedItem(LabOrder order)
    {
        var item = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);
        order.AddItem(item);
        item.MarkCollected(Now);
        item.MarkReceived(Now, null);
        return item;
    }

    private static ICurrentUser CurrentUser(bool canModify = true) => Stub.Of<ICurrentUser>(new()
    {
        ["get_UserId"] = _ => Guid.NewGuid(),
        ["get_Language"] = _ => AppLanguage.French,
        ["Has"] = args => canModify || (string)args![0]! != Permissions.Results.Modify
    });

    private static IClock Clock() => Stub.Of<IClock>(new()
    {
        ["get_Now"] = _ => Now,
        ["get_Today"] = _ => Today
    });

    /// <summary>
    /// An <see cref="InstrumentMessageSink"/> over in-memory stubs: one auto-accepting link on an
    /// operational analyser, one patient, one order and its tubes, analyses and results.
    /// </summary>
    private sealed class SinkBench
    {
        public Patient Patient { get; }
        public LabOrder Order { get; }
        public Sample Tube { get; }
        public Dictionary<Guid, Patient> Patients { get; } = [];
        public PreviousValue? Previous { get; set; }

        /// <summary>The bench's clock; fixed at <see cref="Now"/> unless a test moves it.</summary>
        public DateTimeOffset At { get; set; } = Now;

        /// <summary>The frames received, as the message log keeps them.</summary>
        public Dictionary<Guid, InstrumentMessageLog> Log { get; } = [];

        public InstrumentInterface Link => _link;

        private readonly InstrumentInterface _link;
        private readonly InstrumentMessageSink _sink;
        private readonly List<Sample> _tubes = [];
        private readonly List<TestResult> _results = [];
        private readonly Dictionary<Guid, AnalysisTest> _analyses = [];

        public SinkBench(SampleDefect defects = SampleDefect.None)
        {
            var siteId = Guid.NewGuid();
            Patient = new Patient("P-0002", "ALAOUI", "Salma", new DateOnly(1990, 1, 1), Gender.Male, siteId);
            Patients[Patient.Id] = Patient;
            Order = new LabOrder("2610-000010", Patient.Id, siteId, Now, Guid.NewGuid());
            Tube = AddTube("T-0001", defects);

            var instrument = new Instrument("AU480", new MultilingualText("AU480"), EquipmentKind.Analyser, siteId);
            _link = new InstrumentInterface(instrument.Id, siteId, InstrumentProtocol.AstmE1394,
                                            InstrumentTransportKind.Tcp)
            {
                AutoAcceptResults = true
            };

            var self = this;
            var clock = Stub.Of<IClock>(new()
            {
                ["get_Now"] = _ => self.At,
                ["get_Today"] = _ => DateOnly.FromDateTime(self.At.Date)
            });
            var analyses = Stub.Of<IRepository<AnalysisTest>>(new()
            {
                ["GetByIdWithDetailsAsync"] = args => Task.FromResult(self._analyses.GetValueOrDefault((Guid)args![0]!))
            });
            var patients = Stub.Of<IRepository<Patient>>(new()
            {
                ["GetByIdAsync"] = args => Task.FromResult(self.Patients.GetValueOrDefault((Guid)args![0]!))
            });
            var samples = Stub.Of<IRepository<Sample>>(new()
            {
                ["ListAsync"] = args => Task.FromResult<IReadOnlyList<Sample>>(
                    [.. self._tubes.Where(((Expression<Func<Sample, bool>>)args![0]!).Compile())]),
                ["GetByIdAsync"] = args => Task.FromResult(self._tubes.FirstOrDefault(t => t.Id == (Guid)args![0]!))
            });
            var queries = Stub.Of<IResultQueries>(new()
            {
                ["GetPreviousValuesAsync"] = args => Task.FromResult<IReadOnlyDictionary<Guid, PreviousValue>>(
                    self.Previous is { } previous
                        ? ((IReadOnlyCollection<Guid>)args![1]!).ToDictionary(id => id, _ => previous)
                        : new Dictionary<Guid, PreviousValue>())
            });

            _sink = new InstrumentMessageSink(
                Stub.Of<IRepository<InstrumentInterface>>(new()
                {
                    ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<InstrumentInterface?>(self._link)
                }),
                samples,
                Stub.Of<IRepository<LabOrder>>(new()
                {
                    ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<LabOrder?>(self.Order)
                }),
                analyses,
                Stub.Of<IRepository<TestResult>>(new()
                {
                    ["ListAsync"] = _ => Task.FromResult<IReadOnlyList<TestResult>>(self._results.ToList())
                }),
                patients,
                Stub.Of<IRepository<QcTarget>>(),
                Stub.Of<IRepository<QcResult>>(),
                Stub.Of<IRepository<QcMaterial>>(),
                Stub.Of<MarocLIS.Application.Quality.IQualityQueries>(),
                Stub.Of<IRepository<Instrument>>(new()
                {
                    ["GetByIdAsync"] = _ => Task.FromResult<Instrument?>(instrument)
                }),
                new ResultPostEntryPipeline(analyses, patients, samples, queries, clock),
                Stub.Of<IInstrumentMessageLogStore>(new()
                {
                    ["RecordAsync"] = args =>
                    {
                        var entry = (InstrumentMessageLog)args![0]!;
                        self.Log[entry.Id] = entry;
                        return Task.FromResult(entry.Id);
                    },
                    ["GetByIdAsync"] = args => Task.FromResult(self.Log.GetValueOrDefault((Guid)args![0]!)),
                    ["MarkProcessedAsync"] = args =>
                    {
                        if (self.Log.GetValueOrDefault((Guid)args![0]!) is { } entry)
                        {
                            entry.Status = (InstrumentMessageProcessingStatus)args[1]!;
                            entry.ImportedCount = (int?)args[3];
                            entry.Issues = (string?)args[4];
                        }
                        return Task.CompletedTask;
                    }
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

        public Sample AddTube(string barcode, SampleDefect defects = SampleDefect.None)
        {
            var tube = new Sample(barcode, Order.Id, Guid.NewGuid(), Order.SiteId);
            tube.Collect(Now, Guid.NewGuid(), null, null);
            tube.Receive(Now, Guid.NewGuid(), defects, defects != SampleDefect.None, null, null)
                .IsSuccess.Should().BeTrue();
            _tubes.Add(tube);
            return tube;
        }

        /// <summary>One analysis on the tube, its parameters mapped under their own code.</summary>
        public List<TestResult> AddAnalysis(Sample tube, params TestParameter[] parameters) =>
            AddAnalysis(tube, null, parameters);

        /// <summary>As above; <paramref name="replacing"/> is the analysis of a refused tube it replaces.</summary>
        public List<TestResult> AddAnalysis(Sample tube, LabOrderItem? replacing,
                                            params TestParameter[] parameters)
        {
            var analysis = new AnalysisTest("A" + _analyses.Count, new MultilingualText("Analyse"),
                                            Guid.NewGuid(), Guid.NewGuid());
            foreach (var parameter in parameters)
                if (!analysis.Parameters.Contains(parameter)) analysis.AddParameter(parameter);
            _analyses[analysis.Id] = analysis;

            var item = new LabOrderItem(analysis.Id, OrderPriority.Routine) { SampleId = tube.Id };
            if (replacing is null)
                Order.AddItem(item).IsSuccess.Should().BeTrue();
            else
                Order.ReplaceItems([(replacing.Id, item)], Now, Guid.NewGuid(), "Hémolysé")
                    .IsSuccess.Should().BeTrue();
            item.MarkCollected(Now);
            item.MarkReceived(Now, null);

            foreach (var parameter in parameters.Where(p => !p.IsCalculated))
                if (!_link.Mappings.Any(m => m.TestParameterId == parameter.Id))
                    _link.AddMapping(new InstrumentTestMapping(parameter.Code, parameter.Id));

            var created = parameters
                .Select(p => new TestResult(Order.Id, item.Id, p.Id, Order.SiteId))
                .ToList();
            _results.AddRange(created);
            return created;
        }

        public Task<bool> SendAsync(Sample tube, params (string Code, string Value)[] values) =>
            SendAsync(tube, null, values);

        /// <summary>An ASTM frame from the analyser, received now; returns its line in the log.</summary>
        public async Task<Guid> SendFrameAsync(Sample tube, params (string Code, string Value)[] values)
        {
            string[] lines =
            [
                @"H|\^&|||AU480^1|||||||P|1", "P|1", $"O|1|{tube.Barcode}||^^^{values[0].Code}|R",
                .. values.Select((v, i) => $"R|{i + 1}|^^^{v.Code}|{v.Value}|||N||F"), "L|1|N"
            ];

            var before = Log.Keys.ToHashSet();
            await _sink.HandleAsync(_link.Id, MarocLIS.Instruments.Protocol.AstmMessageParser.Parse(lines));
            return Log.Keys.Single(k => !before.Contains(k));
        }

        public Task<Result<int>> ReplayAsync(Guid logId) => _sink.ReplayAsync(logId, Guid.NewGuid());

        public Task<bool> SendAsync(Sample tube, DateTimeOffset? measuredAt,
                                    params (string Code, string Value)[] values) =>
            _sink.HandleAsync(_link.Id, new InstrumentMessage
            {
                RawText = "test",
                Specimens =
                [
                    new InstrumentSpecimenResults
                    {
                        SpecimenId = tube.Barcode,
                        Results = [.. values.Select(v => new InstrumentResult
                        {
                            InstrumentTestCode = v.Code,
                            Value = v.Value,
                            MeasuredAt = measuredAt
                        })]
                    }
                ]
            });
    }

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
                var value = Neutral(inner);
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                                   .MakeGenericMethod(inner).Invoke(null, [value]);
            }
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
