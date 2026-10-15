using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Calculations;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Patients;
using MarocLIS.Application.Quality;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Application.Samples;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Reports;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Second batch of clinical fixes from the 2026-10-15 review: cancelled analyses on a report (C5),
/// per-result interpretation context (C6), Westgard peers and QC flagging per analyser (C7),
/// re-interpretation after a demographic correction (C8), calendar age bands (C9), receipt
/// validated before it records anything (C11), a Friedewald LDL that is not calculable rather
/// than 0 (C12), and the smaller C13 items.
/// </summary>
public sealed class ReviewFixesClinical2Tests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static TestParameter Numeric(string code, decimal? low = null, decimal? high = null)
    {
        var parameter = new TestParameter(code, new MultilingualText(code), ResultValueType.Numeric);
        if (low is not null || high is not null) parameter.AddReferenceRange(ReferenceRange.Numeric(low, high));
        return parameter;
    }

    private static LabOrderItem ValidatedItem(LabOrder order)
    {
        var item = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);
        order.AddItem(item);
        item.MarkCollected(Now);
        item.MarkReceived(Now, null);
        item.MarkResultEntered(Now);
        item.MarkTechnicallyValidated(Now, Guid.NewGuid());
        item.MarkBiologicallyValidated(Now, Guid.NewGuid());
        return item;
    }

    private static TestResult ValidatedResult(LabOrder order, LabOrderItem item, decimal value)
    {
        var result = new TestResult(order.Id, item.Id, Guid.NewGuid(), order.SiteId);
        result.EnterNumeric(Numeric("K"), value, Now, Guid.NewGuid());
        result.TechnicallyValidate(Now, Guid.NewGuid());
        result.BiologicallyValidate(Now, Guid.NewGuid(), true, true);
        return result;
    }

    // ---- C5: a cancelled analysis is not on the report -----------------------------------------

    [Fact]
    public void C5_AValidatedResultOfACancelledAnalysis_IsNeitherPrintedNorCounted()
    {
        var order = new LabOrder("2610-000001", Guid.NewGuid(), Guid.NewGuid(), Now, Guid.NewGuid());
        var glucose = ValidatedItem(order);
        var glucoseResult = ValidatedResult(order, glucose, 1.0m);

        // An ionogram whose potassium was validated, then the analysis cancelled (tube refused).
        var ionogram = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);
        order.AddItem(ionogram);
        ionogram.MarkCollected(Now);
        ionogram.MarkReceived(Now, null);
        ionogram.MarkResultEntered(Now);
        var potassium = ValidatedResult(order, ionogram, 6.8m);
        order.CancelItem(ionogram.Id, Now, Guid.NewGuid(), "Tube hémolysé refusé").IsSuccess.Should().BeTrue();

        var (releasable, included, completeness) =
            ReportReleaseService.SelectReleasable(order, [glucoseResult, potassium]);

        releasable.Should().ContainSingle().Which.Should().BeSameAs(glucoseResult);
        included.Should().Equal(glucose.Id);
        completeness.Should().Be(ReportCompleteness.Final);
    }

    // ---- C6: each result interpreted with its own instrument -----------------------------------

    [Fact]
    public async Task C6_ASubmitOnTheOrder_KeepsTheAnalyserSpecificIntervalOfAnInstrumentResult()
    {
        var analyser = Guid.NewGuid();
        var bench = new Bench();

        var tsh = Numeric("TSH", 0.40m, 4.00m);
        var specific = ReferenceRange.Numeric(0.30m, 5.00m);
        specific.InstrumentId = analyser;
        tsh.AddReferenceRange(specific);
        var ft4 = Numeric("FT4", 9m, 19m);

        var (tshResult, ft4Result) = bench.AddAnalysis(tsh, ft4) switch { var r => (r[0], r[1]) };

        // As the analyser interface stores it: its instrument, its method-specific interval.
        tshResult.EnterNumeric(tsh, 4.5m, Now, Guid.NewGuid(), fromInstrument: true);
        tshResult.InstrumentId = analyser;
        tshResult.ApplyInterpretation(tsh, tsh.ResolveRange(
            ReferenceRangeContext.For(bench.Patient, Today, analyser)), AppLanguage.French, null);
        tshResult.Interpretation.Should().Be(ResultInterpretation.Normal);

        var outcome = await bench.Service.SubmitAsync(bench.Order.Id, [new ResultEntry(ft4Result.Id, "12")]);

        outcome.IsSuccess.Should().BeTrue();
        tshResult.Interpretation.Should().Be(ResultInterpretation.Normal);
        tshResult.AppliedUpperLimit.Should().Be(5.00m);
        tshResult.Flags.Should().NotHaveFlag(ResultFlag.High);
    }

    // ---- C7: Westgard peers and flagging -------------------------------------------------------

    [Fact]
    public async Task C7_PointsOfDifferentAnalytes_AreNotPeersOfOneAnother()
    {
        var qc = new QcBench();
        var instrument = Guid.NewGuid();
        var glucose = qc.Target(Guid.NewGuid(), instrument);
        var creatinine = qc.Target(Guid.NewGuid(), instrument);

        var outcome = await qc.Service.SubmitAsync(
            [new QcEntry(glucose.Id, 125m), new QcEntry(creatinine.Id, 75m)]);

        outcome.Value.Results.Should().HaveCount(2).And.OnlyContain(e => e.Outcome != QcOutcome.Rejected);
        outcome.Value.Results.Should().OnlyContain(e => !e.ViolatedRules.HasFlag(WestgardRule.R4S));
    }

    [Fact]
    public async Task C7_TwoLevelsOfTheSameAnalyteOnTheSameAnalyser_StillFireR4S()
    {
        var qc = new QcBench();
        var instrument = Guid.NewGuid();
        var parameter = Guid.NewGuid();
        var normal = qc.Target(parameter, instrument);
        var pathological = qc.Target(parameter, instrument);

        var outcome = await qc.Service.SubmitAsync(
            [new QcEntry(normal.Id, 125m), new QcEntry(pathological.Id, 75m)]);

        outcome.Value.Results.Should().OnlyContain(e => e.ViolatedRules.HasFlag(WestgardRule.R4S));
    }

    [Fact]
    public async Task C7_RejectedControls_FlagPatientResultsOnTheirOwnAnalyser()
    {
        var qc = new QcBench();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var onFirst = qc.Target(Guid.NewGuid(), first);
        var onSecond = qc.Target(Guid.NewGuid(), second);

        await qc.Service.SubmitAsync([new QcEntry(onFirst.Id, 140m), new QcEntry(onSecond.Id, 140m)]);

        qc.FlagCalls.Should().HaveCount(2);
        qc.FlagCalls.Should().ContainSingle(c => c.Instrument == first)
            .Which.Parameters.Should().Equal(onFirst.TestParameterId);
        qc.FlagCalls.Should().ContainSingle(c => c.Instrument == second)
            .Which.Parameters.Should().Equal(onSecond.TestParameterId);
    }

    // ---- C8: a demographic correction re-interprets what is still on the bench -----------------

    [Fact]
    public async Task C8_CorrectingTheSex_ReinterpretsPendingResults_AndFlagsValidatedOnes()
    {
        var bench = new Bench(new Patient("P-0001", "ALAMI", "Samira", new DateOnly(1980, 3, 1),
                                           Gender.Male, Guid.NewGuid()));

        var haemoglobin = new TestParameter("HB", new MultilingualText("Hb"), ResultValueType.Numeric);
        haemoglobin.AddReferenceRange(ReferenceRange.Numeric(13m, 17m, Gender.Male));
        haemoglobin.AddReferenceRange(ReferenceRange.Numeric(12m, 16m, Gender.Female));

        var pending = bench.AddAnalysis(haemoglobin)[0];
        pending.EnterNumeric(haemoglobin, 12.5m, Now, Guid.NewGuid());
        pending.ApplyInterpretation(haemoglobin, haemoglobin.ResolveRange(
            ReferenceRangeContext.ForResult(bench.Patient, pending, Today)), AppLanguage.French, null);
        pending.Interpretation.Should().Be(ResultInterpretation.Low);

        var released = bench.AddAnalysis(haemoglobin)[0];
        released.EnterNumeric(haemoglobin, 12.5m, Now, Guid.NewGuid());
        released.ApplyInterpretation(haemoglobin, haemoglobin.ResolveRange(
            ReferenceRangeContext.ForResult(bench.Patient, released, Today)), AppLanguage.French, null);
        released.TechnicallyValidate(Now, Guid.NewGuid());
        released.BiologicallyValidate(Now, Guid.NewGuid(), true, true);

        var updated = await bench.Registration.UpdateAsync(bench.Patient.Id, new PatientIdentityRequest
        {
            LastName = "ALAMI",
            FirstName = "Samira",
            Gender = Gender.Female,
            DateOfBirth = new DateOnly(1980, 3, 1)
        });

        updated.IsSuccess.Should().BeTrue();
        pending.Interpretation.Should().Be(ResultInterpretation.Normal);
        pending.AppliedLowerLimit.Should().Be(12m);
        released.Interpretation.Should().Be(ResultInterpretation.Low);
        bench.AuditReasons.Should().ContainSingle()
             .Which.Should().Contain("1 résultat(s) déjà validé(s)").And.Contain("à revoir");
    }

    [Fact]
    public async Task C8_AnUpdateLeavingTheDemographicsAlone_DoesNotTouchResults()
    {
        var bench = new Bench(new Patient("P-0002", "ALAMI", "Omar", new DateOnly(1980, 3, 1),
                                           Gender.Male, Guid.NewGuid()));
        var haemoglobin = Numeric("HB", 13m, 17m);
        var pending = bench.AddAnalysis(haemoglobin)[0];
        pending.EnterNumeric(haemoglobin, 12.5m, Now, Guid.NewGuid());

        await bench.Registration.UpdateAsync(bench.Patient.Id, new PatientIdentityRequest
        {
            LastName = "ALAMI",
            FirstName = "Omar Ben",
            Gender = Gender.Male,
            DateOfBirth = new DateOnly(1980, 3, 1)
        });

        pending.Interpretation.Should().Be(ResultInterpretation.NotInterpretable);
        bench.AuditReasons.Should().ContainSingle().Which.Should().NotContain("résultat");
    }

    // ---- C9: age bands on the calendar ---------------------------------------------------------

    /// <summary>The seeded ESR bands: under 50 years, and 50 years and over.</summary>
    private static TestParameter Esr()
    {
        var esr = new TestParameter("VS1H", new MultilingualText("VS"), ResultValueType.Numeric);
        esr.AddReferenceRange(ReferenceRange.Numeric(null, 15m, Gender.Male, 0, 50 * 365));
        esr.AddReferenceRange(ReferenceRange.Numeric(null, 20m, Gender.Male, 50 * 365));
        return esr;
    }

    private static ReferenceRangeContext Born(DateOnly dob, DateOnly on, Gender gender = Gender.Male) =>
        ReferenceRangeContext.For(new Patient("P", "X", "Y", dob, gender, Guid.NewGuid()), on);

    [Fact]
    public void C9_AYearBand_DoesNotSwitchBeforeTheBirthday()
    {
        var dob = new DateOnly(1976, 10, 27);

        // Twelve days before the fiftieth birthday the age in days is already 50 × 365.
        var context = Born(dob, Today);
        context.AgeInDays.Should().Be(50 * 365);

        Esr().ResolveRange(context)!.UpperLimit.Should().Be(15m);
        Esr().ResolveRange(Born(dob, new DateOnly(2026, 10, 26)))!.UpperLimit.Should().Be(15m);
        Esr().ResolveRange(Born(dob, new DateOnly(2026, 10, 27)))!.UpperLimit.Should().Be(20m);
    }

    [Fact]
    public void C9_ABirthdayOnTheTwentyNinthOfFebruary_IsReachedOnTheTwentyEighthInACommonYear()
    {
        var adult = new TestParameter("X", new MultilingualText("X"), ResultValueType.Numeric);
        adult.AddReferenceRange(ReferenceRange.Numeric(1m, 2m, ageFromDays: 0, ageToDays: 18 * 365));
        adult.AddReferenceRange(ReferenceRange.Numeric(3m, 4m, ageFromDays: 18 * 365));

        var dob = new DateOnly(2008, 2, 29);

        adult.ResolveRange(Born(dob, new DateOnly(2026, 2, 27)))!.LowerLimit.Should().Be(1m);
        adult.ResolveRange(Born(dob, new DateOnly(2026, 2, 28)))!.LowerLimit.Should().Be(3m);
    }

    [Fact]
    public void C9_AMonthBand_IsComparedInCalendarMonths()
    {
        var infant = new TestParameter("X", new MultilingualText("X"), ResultValueType.Numeric);
        infant.AddReferenceRange(ReferenceRange.Numeric(1m, 2m, ageFromDays: 0, ageToDays: 6 * 30));
        infant.AddReferenceRange(ReferenceRange.Numeric(3m, 4m, ageFromDays: 6 * 30, ageToDays: 24 * 30));

        var dob = new DateOnly(2026, 1, 1);

        // 180 days old on 30 June, but six months only on 1 July.
        infant.ResolveRange(Born(dob, new DateOnly(2026, 6, 30)))!.LowerLimit.Should().Be(1m);
        infant.ResolveRange(Born(dob, new DateOnly(2026, 7, 1)))!.LowerLimit.Should().Be(3m);
    }

    [Fact]
    public void C9_ADayBand_StaysInDays()
    {
        var neonate = new TestParameter("X", new MultilingualText("X"), ResultValueType.Numeric);
        neonate.AddReferenceRange(ReferenceRange.Numeric(1m, 2m, ageFromDays: 0, ageToDays: 7));
        neonate.AddReferenceRange(ReferenceRange.Numeric(3m, 4m, ageFromDays: 7, ageToDays: 29));

        var dob = new DateOnly(2026, 10, 1);

        neonate.ResolveRange(Born(dob, new DateOnly(2026, 10, 7)))!.LowerLimit.Should().Be(1m);
        neonate.ResolveRange(Born(dob, new DateOnly(2026, 10, 8)))!.LowerLimit.Should().Be(3m);
    }

    [Fact]
    public void C9_ADeclaredAgeWithoutDateOfBirth_IsComparedInWholeYears()
    {
        var declared49 = new ReferenceRangeContext(Gender.Male, null, 49, false, null, null, Today);
        var declared50 = new ReferenceRangeContext(Gender.Male, null, 50, false, null, null, Today);

        Esr().ResolveRange(declared49)!.UpperLimit.Should().Be(15m);
        Esr().ResolveRange(declared50)!.UpperLimit.Should().Be(20m);
    }

    [Theory]
    [InlineData(0, 18250)]
    [InlineData(180, 720)]
    [InlineData(0, 7)]
    [InlineData(365, 400)]
    [InlineData(null, 1095)]
    public void C9_TheEditorReopensABand_InTheUnitMatchingReadsItIn(int? from, int? to) =>
        EditorValues.AgeUnitDays(from, to).Should().Be(ReferenceRange.AgeBandUnitDays(from, to));

    // ---- C11: a refused receipt records nothing ------------------------------------------------

    private static Sample CollectedTube()
    {
        var sample = new Sample("T0001", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        sample.Collect(Now, Guid.NewGuid(), null, null);
        return sample;
    }

    [Fact]
    public void C11_DefectsWithoutAnExplicitDecision_LeaveTheTubeAsItWas()
    {
        var sample = CollectedTube();

        var received = sample.Receive(Now, Guid.NewGuid(), SampleDefect.Haemolysed, false, 4m, null);

        received.FirstError.ResourceKey.Should().Be("Sample_DefectsRequireExplicitDecision");
        sample.ReceivedAt.Should().BeNull();
        sample.ReceivedByUserId.Should().BeNull();
        sample.ReceptionTemperatureCelsius.Should().BeNull();
        sample.Defects.Should().Be(SampleDefect.None);
        sample.Status.Should().Be(SampleStatus.Collected);
    }

    [Fact]
    public async Task C11_AReceptionThatTookNothingIn_DoesNotSave()
    {
        var sample = CollectedTube();
        var saves = 0;

        var service = new SampleWorkflowService(
            Stub.Of<IRepository<Sample>>(new() { ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<Sample?>(sample) }),
            Stub.Of<IRepository<LabOrder>>(),
            Stub.Of<IRepository<AnalysisTest>>(),
            Stub.Of<IRepository<Patient>>(),
            Stub.Of<IRepository<TestResult>>(),
            Stub.Of<ISampleQueries>(),
            Stub.Of<ISequenceAllocator>(),
            Stub.Of<IUnitOfWork>(new() { ["SaveChangesAsync"] = _ => Task.FromResult(++saves) }),
            Stub.Of<IPermissionGuard>(),
            CurrentUser(),
            Stub.Of<IDataAccessAuditor>(),
            Clock(),
            new OrderRegistrationOptions());

        var outcome = await service.ReceiveAsync(
            [new ReceiveSampleRequest { SampleId = sample.Id, ObservedDefects = SampleDefect.Haemolysed }]);

        outcome.Value.Received.Should().Be(0);
        outcome.Value.Problems.Should().ContainSingle();
        saves.Should().Be(0);
        sample.ReceivedAt.Should().BeNull();
    }

    // ---- C12: Friedewald above 4 g/L is not calculable ------------------------------------------

    private const string Friedewald = "if([TG]<4;round([CHOL]-[HDL]-[TG]/5;2);nd())";

    private static Dictionary<string, decimal> Lipids(decimal tg) =>
        new(StringComparer.Ordinal) { ["CHOL"] = 2.50m, ["HDL"] = 0.50m, ["TG"] = tg };

    [Fact]
    public void C12_Friedewald_IsNotApplicableAtOrAboveFourGramsOfTriglycerides()
    {
        FormulaEvaluator.Evaluate(Friedewald, Lipids(4.0m))
            .FirstError.ResourceKey.Should().Be(FormulaEvaluator.NotApplicableKey);
        FormulaEvaluator.Evaluate(Friedewald, Lipids(1.5m)).Value.Should().Be(1.70m);
    }

    [Fact]
    public void C12_IfOnlyEvaluatesTheBranchItTakes()
    {
        var values = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["X"] = 0m };

        FormulaEvaluator.Evaluate("if([X]>0;1/[X];0)", values).Value.Should().Be(0m);
        FormulaEvaluator.Evaluate("if(1;2;[ABSENT])", values).Value.Should().Be(2m);
        FormulaEvaluator.Evaluate("if(0;nd();3)", values).Value.Should().Be(3m);
        FormulaEvaluator.Evaluate("if(1;[ABSENT];3)", values)
            .FirstError.ResourceKey.Should().Be("Formula_MissingValue");
        FormulaEvaluator.Evaluate("if(1;2)", values)
            .FirstError.ResourceKey.Should().Be("Formula_WrongArgumentCount");
    }

    [Fact]
    public void C12_AFormulaUsingNd_PassesTheCatalogueCheck()
    {
        FormulaEvaluator.Validate(Friedewald, new HashSet<string> { "CHOL", "HDL", "TG" })
            .IsSuccess.Should().BeTrue();
        FormulaEvaluator.Validate("nd()", new HashSet<string>()).IsSuccess.Should().BeTrue();
        FormulaEvaluator.Validate("nd(1)", new HashSet<string>()).IsSuccess.Should().BeFalse();
    }

    private static (Bench Bench, Dictionary<string, TestResult> Results) LipidPanel()
    {
        var bench = new Bench();
        var ldl = Numeric("LDL", null, 1.60m);
        ldl.CalculationFormula = Friedewald;

        var parameters = new[] { Numeric("CHOL"), Numeric("HDL"), Numeric("TG"), ldl };
        var results = bench.AddAnalysis(parameters);

        return (bench, parameters.Zip(results).ToDictionary(x => x.First.Code, x => x.Second));
    }

    [Fact]
    public async Task C12_HighTriglycerides_LeaveTheLdlNotCalculable_NotAZero()
    {
        var (bench, r) = LipidPanel();

        await bench.Service.SubmitAsync(bench.Order.Id,
        [
            new ResultEntry(r["CHOL"].Id, "2.50"), new ResultEntry(r["HDL"].Id, "0.50"),
            new ResultEntry(r["TG"].Id, "5.00")
        ]);

        var ldl = r["LDL"];
        ldl.HasValue.Should().BeFalse();
        ldl.Status.Should().Be(TestResultStatus.NotDone);
        ldl.Interpretation.Should().NotBe(ResultInterpretation.Normal);
        ldl.TechnicalComment.Should().Contain("dosage direct");
        ldl.PrintTechnicalComment.Should().BeTrue();

        // Corrected triglycerides: the LDL is calculated, and the "not calculable" note goes.
        await bench.Service.SubmitAsync(bench.Order.Id, [new ResultEntry(r["TG"].Id, "1.50")]);

        ldl.NumericValue.Should().Be(1.70m);
        ldl.Status.Should().Be(TestResultStatus.Entered);
        ldl.Flags.Should().NotHaveFlag(ResultFlag.NotDone);
        ldl.TechnicalComment.Should().BeNull();
        ldl.FormatValue(AppLanguage.French).Should().Be("1,70");
    }

    // ---- C13 -----------------------------------------------------------------------------------

    [Fact]
    public async Task C13_ACalculatedValue_IsWithdrawnWhenAnInputLosesItsValue()
    {
        var (bench, r) = LipidPanel();

        await bench.Service.SubmitAsync(bench.Order.Id,
        [
            new ResultEntry(r["CHOL"].Id, "2.50"), new ResultEntry(r["HDL"].Id, "0.50"),
            new ResultEntry(r["TG"].Id, "1.50")
        ]);
        r["LDL"].NumericValue.Should().Be(1.70m);

        await bench.Service.SubmitAsync(bench.Order.Id, [new ResultEntry(r["HDL"].Id, "")]);

        r["HDL"].Status.Should().Be(TestResultStatus.NotDone);
        r["LDL"].HasValue.Should().BeFalse();
        r["LDL"].Status.Should().Be(TestResultStatus.Awaited);
        r["LDL"].Interpretation.Should().Be(ResultInterpretation.NotInterpretable);
    }

    [Fact]
    public async Task C13_AnUnchangedCalculatedValue_KeepsItsTechnicalValidation()
    {
        var (bench, r) = LipidPanel();

        await bench.Service.SubmitAsync(bench.Order.Id,
        [
            new ResultEntry(r["CHOL"].Id, "2.50"), new ResultEntry(r["HDL"].Id, "0.50"),
            new ResultEntry(r["TG"].Id, "1.50")
        ]);
        r["LDL"].TechnicallyValidate(Now, Guid.NewGuid());

        // An unrelated re-submission of the same TG: the LDL is still 1,70.
        await bench.Service.SubmitAsync(bench.Order.Id, [new ResultEntry(r["TG"].Id, "1.50")]);

        r["LDL"].Status.Should().Be(TestResultStatus.TechnicallyValidated);
    }

    [Fact]
    public void C13_AReportedAnalysis_CannotBeCancelled_NorItsOrder()
    {
        var order = new LabOrder("2610-000002", Guid.NewGuid(), Guid.NewGuid(), Now, Guid.NewGuid());
        var item = ValidatedItem(order);
        item.MarkReported(Now).IsSuccess.Should().BeTrue();

        order.CancelItem(item.Id, Now, Guid.NewGuid(), "Erreur de saisie")
             .FirstError.ResourceKey.Should().Be("Order_ItemValidatedCannotCancel");
        order.Cancel(Now, Guid.NewGuid(), "Erreur de saisie")
             .FirstError.ResourceKey.Should().Be("Order_HasValidatedItemsCannotCancel");
        item.IsCancelled.Should().BeFalse();
    }

    [Fact]
    public void C13_ACorrectedValue_LosesTheDeltaCheckFlagOfTheOldOne()
    {
        var parameter = Numeric("GLY", 0.70m, 1.10m);
        var result = new TestResult(Guid.NewGuid(), Guid.NewGuid(), parameter.Id, Guid.NewGuid());
        result.EnterNumeric(parameter, 3.0m, Now, Guid.NewGuid());
        result.FlagDeltaCheckFailure();

        result.EnterNumeric(parameter, 1.0m, Now, Guid.NewGuid());
        result.ApplyInterpretation(parameter, parameter.ReferenceRanges.First(), AppLanguage.French, null);

        result.Flags.Should().NotHaveFlag(ResultFlag.DeltaCheckFailed);
    }

    [Fact]
    public async Task C13_TheDeltaCheck_IsRecomputedAtEachSubmit()
    {
        var bench = new Bench();
        var glucose = Numeric("GLY", 0.70m, 1.10m);
        glucose.DeltaCheckPercent = 50m;
        glucose.DeltaCheckWindowDays = 30;
        var result = bench.AddAnalysis(glucose)[0];
        bench.Previous = new PreviousValue(1.00m, Today.AddDays(-3), Guid.NewGuid());

        await bench.Service.SubmitAsync(bench.Order.Id, [new ResultEntry(result.Id, "3.00")]);
        result.Flags.Should().HaveFlag(ResultFlag.DeltaCheckFailed);

        await bench.Service.SubmitAsync(bench.Order.Id, [new ResultEntry(result.Id, "1.05")]);
        result.Flags.Should().NotHaveFlag(ResultFlag.DeltaCheckFailed);
    }

    [Fact]
    public void AValueEnteredAfterNotDone_IsPrintedRatherThanNotDone()
    {
        var parameter = Numeric("GLY");
        var result = new TestResult(Guid.NewGuid(), Guid.NewGuid(), parameter.Id, Guid.NewGuid());
        result.MarkNotDone("Non fait", Now, Guid.NewGuid());

        result.EnterNumeric(parameter, 1.05m, Now, Guid.NewGuid());

        result.Flags.Should().NotHaveFlag(ResultFlag.NotDone);
        result.FormatValue(AppLanguage.French).Should().Be("1,05");
    }

    // ---- Harnesses -------------------------------------------------------------------------------

    private static ICurrentUser CurrentUser() => Stub.Of<ICurrentUser>(new()
    {
        ["get_UserId"] = _ => Guid.NewGuid(),
        ["get_Language"] = _ => AppLanguage.French,
        ["Has"] = _ => true
    });

    private static IClock Clock() => Stub.Of<IClock>(new()
    {
        ["get_Now"] = _ => Now,
        ["get_Today"] = _ => Today
    });

    /// <summary>
    /// A <see cref="ResultEntryService"/> and a <see cref="PatientRegistrationService"/> over
    /// in-memory stubs: one patient, one order, its analyses and results.
    /// </summary>
    private sealed class Bench
    {
        public Patient Patient { get; }
        public LabOrder Order { get; }
        public ResultEntryService Service { get; }
        public PatientRegistrationService Registration { get; }
        public List<string?> AuditReasons { get; } = [];
        public PreviousValue? Previous { get; set; }

        private readonly List<TestResult> _results = [];
        private readonly Dictionary<Guid, AnalysisTest> _analyses = [];

        public Bench(Patient? patient = null)
        {
            Patient = patient ?? new Patient("P-0000", "BENNANI", "Karim", new DateOnly(1985, 5, 5),
                                             Gender.Male, Guid.NewGuid());
            Order = new LabOrder("2610-000010", Patient.Id, Guid.NewGuid(), Now, Guid.NewGuid());

            var order = Order;
            var self = this;
            string? pendingReason = null;

            var orders = Stub.Of<IRepository<LabOrder>>(new()
            {
                ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<LabOrder?>(order),
                ["ListAsync"] = _ => Task.FromResult<IReadOnlyList<LabOrder>>([order])
            });

            var unitOfWork = Stub.Of<IUnitOfWork>(new()
            {
                ["set_AuditReason"] = args => pendingReason = (string?)args![0],
                ["get_AuditReason"] = _ => pendingReason,
                ["SaveChangesAsync"] = _ =>
                {
                    self.AuditReasons.Add(pendingReason);
                    pendingReason = null;
                    return Task.FromResult(1);
                }
            });

            Service = new ResultEntryService(
                orders,
                Stub.Of<IRepository<AnalysisTest>>(new()
                {
                    ["GetByIdWithDetailsAsync"] = args =>
                        Task.FromResult(self._analyses.GetValueOrDefault((Guid)args![0]!))
                }),
                Stub.Of<IRepository<Patient>>(new() { ["GetByIdAsync"] = _ => Task.FromResult<Patient?>(self.Patient) }),
                Stub.Of<IRepository<Sample>>(),
                Stub.Of<IResultQueries>(new()
                {
                    ["GetResultsForOrderAsync"] = _ => Task.FromResult<IReadOnlyList<TestResult>>(self._results.ToList()),
                    ["GetActiveReflexRulesAsync"] = _ => Task.FromResult<IReadOnlyList<ReflexRule>>([]),
                    ["GetPreviousValueAsync"] = _ => Task.FromResult(self.Previous)
                }),
                unitOfWork,
                Stub.Of<IPermissionGuard>(),
                CurrentUser(),
                Stub.Of<IDataAccessAuditor>(),
                Clock());

            Registration = new PatientRegistrationService(
                Stub.Of<IRepository<Patient>>(new()
                {
                    ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<Patient?>(self.Patient),
                    ["ListAsync"] = _ => Task.FromResult<IReadOnlyList<Patient>>([])
                }),
                Stub.Of<IPatientDuplicateFinder>(),
                Stub.Of<ISequenceAllocator>(),
                unitOfWork,
                Stub.Of<IPermissionGuard>(),
                CurrentUser(),
                Stub.Of<IDataAccessAuditor>(),
                Clock(),
                orders,
                Service);
        }

        /// <summary>One analysis carrying the given parameters, with an awaited result for each.</summary>
        public List<TestResult> AddAnalysis(params TestParameter[] parameters)
        {
            var analysis = new AnalysisTest("A" + _analyses.Count, new MultilingualText("Analyse"),
                                            Guid.NewGuid(), Guid.NewGuid());
            foreach (var parameter in parameters) analysis.AddParameter(parameter);
            _analyses[analysis.Id] = analysis;

            var item = new LabOrderItem(analysis.Id, OrderPriority.Routine);
            Order.AddItem(item);

            var created = parameters
                .Select(p => new TestResult(Order.Id, item.Id, p.Id, Order.SiteId))
                .ToList();
            _results.AddRange(created);
            return created;
        }
    }

    /// <summary>A <see cref="QualityControlService"/> over stubs, recording the flagging calls.</summary>
    private sealed class QcBench
    {
        private readonly Dictionary<Guid, QcTarget> _targets = [];

        public List<(IReadOnlyList<Guid> Parameters, Guid Instrument)> FlagCalls { get; } = [];
        public QualityControlService Service { get; }

        public QcBench()
        {
            var self = this;

            Service = new QualityControlService(
                Stub.Of<IRepository<QcTarget>>(new()
                {
                    ["GetByIdAsync"] = args => Task.FromResult(self._targets.GetValueOrDefault((Guid)args![0]!))
                }),
                Stub.Of<IRepository<QcResult>>(),
                Stub.Of<IRepository<QcMaterial>>(),
                Stub.Of<IRepository<NonConformity>>(),
                Stub.Of<IQualityQueries>(new()
                {
                    ["GetZScoreHistoryAsync"] = _ => Task.FromResult<IReadOnlyList<QcPoint>>([]),
                    ["FlagResultsCoveredByRejectedQcAsync"] = args =>
                    {
                        self.FlagCalls.Add(([.. (IEnumerable<Guid>)args![0]!], (Guid)args[1]!));
                        return Task.FromResult(1);
                    }
                }),
                Stub.Of<ISequenceAllocator>(),
                Stub.Of<IUnitOfWork>(),
                Stub.Of<IPermissionGuard>(),
                CurrentUser(),
                Clock());
        }

        public QcTarget Target(Guid parameterId, Guid instrumentId)
        {
            var target = new QcTarget(Guid.NewGuid(), parameterId, instrumentId)
            {
                AssignedMean = 100m,
                AssignedSd = 10m
            };
            _targets[target.Id] = target;
            return target;
        }
    }

    /// <summary>
    /// Interface stub: named members answer from the handler table, everything else returns a
    /// neutral value — success for a <see cref="Result"/>, a completed task, or the default.
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
                var value = Neutral(inner);
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                                   .MakeGenericMethod(inner).Invoke(null, [value]);
            }
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
