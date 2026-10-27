using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure.Persistence;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Gaps of the analytical phase (manual chapter 03): the correction of a released or printed value
/// with a reason, re-validated and named on the rectified report; "not done" and clearing a value
/// from result entry; and the default roles — accepting held analyser transmissions at the bench,
/// reprinting a validated report at reception, withdrawing a validation as a biologist.
/// </summary>
public sealed class ReviewFixesResultatsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 10, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateOnly Today = new(2026, 10, 16);

    private const string Friedewald = "if([TG]<4;round([CHOL]-[HDL]-[TG]/5;2);nd())";

    private static TestParameter Numeric(string code, decimal? low = null, decimal? high = null)
    {
        var parameter = new TestParameter(code, new MultilingualText(code), ResultValueType.Numeric);
        if (low is not null || high is not null) parameter.AddReferenceRange(ReferenceRange.Numeric(low, high));
        return parameter;
    }

    /// <summary>
    /// Enters, checks, releases and prints every value of the bench's order, the print an hour
    /// before the clock: what the correction then has to undo.
    /// </summary>
    private static async Task ReleaseAndPrintAsync(Bench bench, params (TestResult Result, string Value)[] values)
    {
        var submitted = await bench.Service.SubmitAsync(bench.Order.Id,
            [.. values.Select(v => new ResultEntry(v.Result.Id, v.Value))]);
        submitted.Value.Rejected.Should().BeEmpty();

        (await bench.Service.TechnicallyValidateAsync(bench.Order.Id, [])).IsSuccess.Should().BeTrue();

        foreach (var result in bench.Results.Where(r => r.HasValue))
            result.BiologicallyValidate(Now, Guid.NewGuid(), false, true).IsSuccess.Should().BeTrue();

        // The analyses whose every value was given; the others stay on the bench.
        foreach (var item in bench.Order.Items.Where(i => bench.Results
                     .Where(r => r.LabOrderItemId == i.Id).All(r => r.HasValue)))
        {
            item.MarkBiologicallyValidated(Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
            item.MarkReported(Now.AddHours(-1)).IsSuccess.Should().BeTrue();
        }

        foreach (var result in bench.Results.Where(r => r.HasValue)) result.MarkReported();
    }

    [Fact]
    public async Task TheValuesEnteredBeforeTheirTubeWasDiscarded_StillCountAsEntered_AndOnlyTheMissingOnesWaitForANewTube()
    {
        var bench = new Bench();
        var potassium = Numeric("K");
        var sodium = Numeric("NA");
        var results = bench.AddAnalysis(potassium, sodium);

        var tube = new Sample("2610260099", bench.Order.Id, Guid.NewGuid(), bench.Order.SiteId);
        bench.Samples.Add(tube);
        bench.Order.Items.Single().SampleId = tube.Id;

        results[0].EnterNumeric(potassium, 4.2m, Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        tube.Discard(Now, Guid.NewGuid(), "Tube jeté après analyse").IsSuccess.Should().BeTrue();

        var rows = (await bench.Service.OpenAsync(bench.Order.Id)).Value.Rows;
        var entered = rows.Single(r => r.TestResultId == results[0].Id);
        var missing = rows.Single(r => r.TestResultId == results[1].Id);

        entered.IsAwaitingSample.Should().BeFalse("the value was measured on the tube before it was thrown away");
        ResultEntryRules.EditRefusal(entered).Should().BeNull();
        missing.IsAwaitingSample.Should().BeTrue("nothing can be measured on a discarded tube any more");
        ResultEntryRules.Progress(rows).Should().Be((1, 1));

        // The screen and the save agree: the measured value can be corrected, the missing one not.
        var submitted = await bench.Service.SubmitAsync(bench.Order.Id,
            [new ResultEntry(results[0].Id, "4.3"), new ResultEntry(results[1].Id, "140")]);
        submitted.Value.Rejected.Should().ContainSingle().Which.ResourceKey.Should().Be("Result_SampleNotReceived");
        results[0].NumericValue.Should().Be(4.3m);
        results[1].HasValue.Should().BeFalse();
    }

    // ---- 1. Correcting a released or printed value ---------------------------------------------

    [Fact]
    public void ReopenForCorrection_TakesAPrintedAnalysisBackToEntry_AndKeepsItKnownAsPrinted()
    {
        var item = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);
        item.MarkCollected(Now);
        item.MarkReceived(Now, null);
        item.MarkResultEntered(Now);
        item.MarkTechnicallyValidated(Now, Guid.NewGuid());
        item.MarkBiologicallyValidated(Now, Guid.NewGuid());
        item.MarkReported(Now);

        item.ReopenForCorrection(" ").IsFailure.Should().BeTrue();

        item.ReopenForCorrection("Erreur de transcription du potassium").IsSuccess.Should().BeTrue();

        item.Status.Should().Be(LabOrderItemStatus.ResultEntered);
        item.ValidatedAt.Should().BeNull();
        item.TechnicallyValidatedAt.Should().BeNull();
        item.HasReportedResults.Should().BeTrue("a printed analysis stays known as printed");
    }

    [Fact]
    public void ReopenForCorrection_IsRefused_BeforeAnyResultWasEntered()
    {
        var item = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);
        item.MarkCollected(Now);
        item.MarkReceived(Now, null);

        item.ReopenForCorrection("Erreur de transcription").IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task CorrectingAPrintedValue_KeepsTheOldOne_AndSendsItBackThroughBothValidations()
    {
        var bench = new Bench();
        var potassium = bench.AddAnalysis(Numeric("K", 3.5m, 5.0m))[0];
        await ReleaseAndPrintAsync(bench, (potassium, "6.8"));

        var outcome = await bench.Service.CorrectReleasedAsync(
            bench.Order.Id, potassium.Id, "4.2", "Valeur d'un autre patient saisie par erreur");

        outcome.IsSuccess.Should().BeTrue();
        potassium.NumericValue.Should().Be(4.2m);
        potassium.Status.Should().Be(TestResultStatus.Entered);
        potassium.Interpretation.Should().Be(ResultInterpretation.Normal, "the corrected value is interpreted afresh");
        potassium.Flags.Should().HaveFlag(ResultFlag.Corrected);
        potassium.ValidatedAt.Should().BeNull();

        var revision = potassium.Revisions.Should().ContainSingle().Subject;
        revision.PreviousNumericValue.Should().Be(6.8m);
        revision.WasValidated.Should().BeTrue();
        revision.Reason.Should().Be("Valeur d'un autre patient saisie par erreur");

        var item = bench.Order.Items.Single();
        item.Status.Should().Be(LabOrderItemStatus.ResultEntered);
        item.HasReportedResults.Should().BeTrue();

        // Not releasable until checked again: biological validation still requires the technical one.
        potassium.CanBiologicallyValidate(false, true).IsFailure.Should().BeTrue();

        bench.AuditReasons.Last().Should().Contain("Correction").And.Contain("K");

        // Checking the whole file again is not refused because of the values already printed.
        (await bench.Service.TechnicallyValidateAsync(bench.Order.Id, [])).IsSuccess.Should().BeTrue();
        potassium.Status.Should().Be(TestResultStatus.TechnicallyValidated);
        item.Status.Should().Be(LabOrderItemStatus.TechnicallyValidated);
    }

    [Fact]
    public async Task ACorrection_NeedsAReason_AChangedValue_AndAReleasedResult()
    {
        var bench = new Bench();
        var results = new[] { bench.AddAnalysis(Numeric("K"))[0], bench.AddAnalysis(Numeric("NA"))[0] };
        await ReleaseAndPrintAsync(bench, (results[0], "6.8"));

        (await bench.Service.CorrectReleasedAsync(bench.Order.Id, results[0].Id, "4.2", "  "))
            .FirstError.ResourceKey.Should().Be("Result_RevisionReasonRequired");
        (await bench.Service.CorrectReleasedAsync(bench.Order.Id, results[0].Id, " ", "Erreur de saisie"))
            .FirstError.ResourceKey.Should().Be("Result_CorrectionValueRequired");
        (await bench.Service.CorrectReleasedAsync(bench.Order.Id, results[0].Id, "6,8", "Erreur de saisie"))
            .FirstError.ResourceKey.Should().Be("Result_CorrectionUnchanged");
        (await bench.Service.CorrectReleasedAsync(bench.Order.Id, results[1].Id, "140", "Erreur de saisie"))
            .FirstError.ResourceKey.Should().Be("Result_CorrectionOnlyForReleased");
        (await bench.Service.CorrectReleasedAsync(bench.Order.Id, results[0].Id, "abc", "Erreur de saisie"))
            .FirstError.ResourceKey.Should().Be("Result_NotANumber");

        results[0].NumericValue.Should().Be(6.8m);
        results[0].Status.Should().Be(TestResultStatus.Reported);
        results[0].Revisions.Should().BeEmpty();
    }

    [Fact]
    public async Task ACorrection_IsRefused_WithoutTheRightToWithdrawARelease()
    {
        var bench = new Bench(Stub.Of<IPermissionGuard>(new()
        {
            ["RequireAll"] = _ => Result.Failure(Error.Forbidden("Message.PermissionDenied",
                                                                 Permissions.Results.Unvalidate))
        }));
        var potassium = bench.AddAnalysis(Numeric("K"))[0];
        await ReleaseAndPrintAsync(bench, (potassium, "6.8"));

        var outcome = await bench.Service.CorrectReleasedAsync(bench.Order.Id, potassium.Id, "4.2",
                                                                "Erreur de saisie");

        outcome.IsFailure.Should().BeTrue();
        potassium.NumericValue.Should().Be(6.8m);
    }

    [Fact]
    public async Task CorrectingAnInput_ReopensAndRecomputesTheReleasedCalculatedValue()
    {
        var bench = new Bench();
        var ldl = Numeric("LDL");
        ldl.CalculationFormula = Friedewald;
        var parameters = new[] { Numeric("CHOL"), Numeric("HDL"), Numeric("TG"), ldl };
        var r = parameters.Zip(bench.AddAnalysis(parameters)).ToDictionary(x => x.First.Code, x => x.Second);

        await ReleaseAndPrintAsync(bench, (r["CHOL"], "2.50"), (r["HDL"], "0.50"), (r["TG"], "1.50"));
        r["LDL"].NumericValue.Should().Be(1.70m);
        r["LDL"].Status.Should().Be(TestResultStatus.Reported);

        (await bench.Service.CorrectReleasedAsync(bench.Order.Id, r["CHOL"].Id, "2.80",
                                                  "Erreur de transcription")).IsSuccess.Should().BeTrue();

        r["LDL"].NumericValue.Should().Be(2.00m);
        r["LDL"].Status.Should().Be(TestResultStatus.Entered);
        r["LDL"].Revisions.Should().ContainSingle().Which.PreviousNumericValue.Should().Be(1.70m);
    }

    [Fact]
    public async Task TheNextRelease_NamesTheCorrectedValue_AndMustBeARectification()
    {
        var bench = new Bench();
        var results = bench.AddAnalysis(Numeric("K"), Numeric("NA"));
        await ReleaseAndPrintAsync(bench, (results[0], "6.8"), (results[1], "140"));

        await bench.Service.CorrectReleasedAsync(bench.Order.Id, results[0].Id, "4.2", "Erreur de saisie");
        await bench.Service.TechnicallyValidateAsync(bench.Order.Id, []);
        results[0].BiologicallyValidate(Now, Guid.NewGuid(), false, true).IsSuccess.Should().BeTrue();
        bench.Order.Items.Single().MarkBiologicallyValidated(Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();

        var (releasable, _, _) = ReportReleaseService.SelectReleasable(bench.Order, bench.Results);
        releasable.Should().HaveCount(2);

        ReportReleaseService.CorrectedSinceReported(bench.Order, releasable)
            .Should().ContainSingle().Which.Should().BeSameAs(results[0]);
    }

    [Fact]
    public void AWithdrawnReleaseWithoutCorrection_IsNotNamedAsCorrected()
    {
        var order = new LabOrder("2610-000020", Guid.NewGuid(), Guid.NewGuid(), Now, Guid.NewGuid());
        var item = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);
        order.AddItem(item);
        item.MarkCollected(Now);
        item.MarkReceived(Now, null);
        item.MarkResultEntered(Now);
        item.RecordPartlyReported(Now.AddHours(-1));

        var result = new TestResult(order.Id, item.Id, Guid.NewGuid(), order.SiteId);
        result.EnterNumeric(Numeric("K"), 4.0m, Now.AddHours(-2), Guid.NewGuid());
        result.TechnicallyValidate(Now, Guid.NewGuid());
        result.BiologicallyValidate(Now, Guid.NewGuid(), false, true);
        result.WithdrawValidation(Now, Guid.NewGuid(), "Retrait avant impression").IsSuccess.Should().BeTrue();
        result.BiologicallyValidate(Now, Guid.NewGuid(), false, true);

        ReportReleaseService.CorrectedSinceReported(order, [result]).Should().BeEmpty();
    }

    // ---- 3. "Not done" and clearing a value from result entry ----------------------------------

    [Fact]
    public async Task NotDone_FromEntry_NeedsAReason_AndPrintsIt()
    {
        var bench = new Bench();
        var result = bench.AddAnalysis(Numeric("K"))[0];

        var refused = await bench.Service.SubmitAsync(bench.Order.Id,
            [new ResultEntry(result.Id, null, " ", Action: ResultEntryAction.NotDone)]);
        refused.Value.Rejected.Should().ContainSingle()
               .Which.ResourceKey.Should().Be("Result_NotDoneReasonRequired");
        result.Status.Should().Be(TestResultStatus.Awaited);

        // A value typed in the cell is ignored: the action says what the entry does.
        var outcome = await bench.Service.SubmitAsync(bench.Order.Id,
            [new ResultEntry(result.Id, "4.2", "Échantillon hémolysé", Action: ResultEntryAction.NotDone)]);

        outcome.Value.Accepted.Should().Be(1);
        result.Status.Should().Be(TestResultStatus.NotDone);
        result.HasValue.Should().BeFalse();
        result.TechnicalComment.Should().Be("Échantillon hémolysé");
        result.PrintTechnicalComment.Should().BeTrue();
        bench.Order.Items.Single().Status.Should().Be(LabOrderItemStatus.ResultEntered);
    }

    [Fact]
    public async Task Clear_FromEntry_WithdrawsAMistypedValue_OrANotDone()
    {
        var bench = new Bench();
        var results = bench.AddAnalysis(Numeric("K"), Numeric("NA"));

        await bench.Service.SubmitAsync(bench.Order.Id,
        [
            new ResultEntry(results[0].Id, "4.2"),
            new ResultEntry(results[1].Id, null, "Tube coagulé", Action: ResultEntryAction.NotDone)
        ]);

        var outcome = await bench.Service.SubmitAsync(bench.Order.Id,
        [
            new ResultEntry(results[0].Id, null, Action: ResultEntryAction.Clear),
            new ResultEntry(results[1].Id, null, Action: ResultEntryAction.Clear)
        ]);

        outcome.Value.Accepted.Should().Be(2);
        results[0].HasValue.Should().BeFalse();
        results[0].Status.Should().Be(TestResultStatus.Awaited);
        results[1].Status.Should().Be(TestResultStatus.Awaited);
        results[1].Flags.Should().NotHaveFlag(ResultFlag.NotDone);
        results[1].TechnicalComment.Should().BeNull("the not-done reason must not print under an awaited line");
        results[1].PrintTechnicalComment.Should().BeFalse();
    }

    [Fact]
    public async Task Clear_IsRefused_OnAReleasedValue()
    {
        var bench = new Bench();
        var result = bench.AddAnalysis(Numeric("K"))[0];
        await ReleaseAndPrintAsync(bench, (result, "4.2"));

        var outcome = await bench.Service.SubmitAsync(bench.Order.Id,
            [new ResultEntry(result.Id, null, Action: ResultEntryAction.Clear)]);

        outcome.Value.Rejected.Should().ContainSingle()
               .Which.ResourceKey.Should().Be("Result_ValidatedRequiresRevision");
        result.NumericValue.Should().Be(4.2m);
    }

    // ---- 2 and 4. Default roles ----------------------------------------------------------------

    [Fact]
    public void TheBench_CanAcceptHeldAnalyserTransmissions()
    {
        ShippedRole(SystemRoles.Technician).Should().Contain(Permissions.Instruments.ReplayMessages);
        ShippedRole(SystemRoles.Biologist).Should().Contain(Permissions.Instruments.ReplayMessages);

        // Configuring the link stays an administrative act.
        ShippedRole(SystemRoles.Technician).Should().NotContain(Permissions.Instruments.Configure);
    }

    [Fact]
    public void ReceptionAndBiologists_CanReprint_AndBiologistsCanWithdrawAValidation()
    {
        ShippedRole(SystemRoles.Receptionist).Should().Contain(Permissions.Reports.ReprintValidated);
        ShippedRole(SystemRoles.Biologist).Should().Contain(Permissions.Reports.ReprintValidated);
        ShippedRole(SystemRoles.Biologist).Should().Contain(Permissions.Results.Unvalidate);

        // The correction path needs both rights; a biologist holds them by default.
        ShippedRole(SystemRoles.Biologist).Should().Contain(Permissions.Results.Modify);

        // Reception still never amends nor releases a result.
        ShippedRole(SystemRoles.Receptionist).Should().NotContain(Permissions.Reports.Amend);
        ShippedRole(SystemRoles.Receptionist).Should().NotContain(Permissions.Results.View);
    }

    [Fact]
    public void AnExistingBuiltInRole_ReceivesTheNewShippedPermissionsOnStartUp()
    {
        var biologist = new Role(SystemRoles.Biologist, new MultilingualText("Biologiste"), isBuiltIn: true);
        biologist.SeedShippedPermissions(ShippedRole(SystemRoles.Biologist)
            .Except([Permissions.Results.Unvalidate, Permissions.Reports.ReprintValidated,
                     Permissions.Instruments.ReplayMessages]));

        var granted = biologist.ApplyShippedPermissions(ShippedRole(SystemRoles.Biologist));

        granted.Should().BeEquivalentTo([Permissions.Results.Unvalidate, Permissions.Reports.ReprintValidated,
                                         Permissions.Instruments.ReplayMessages]);
    }

    private static string[] ShippedRole(string roleCode)
    {
        var type = typeof(LisDbContext).Assembly.GetType("MarocLIS.Infrastructure.Persistence.RolePermissionSets")!;
        var definitions = (System.Collections.IEnumerable)type
            .GetMethod("Build", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

        foreach (var definition in definitions)
        {
            var tuple = definition.GetType();
            if ((string)tuple.GetField("Item1")!.GetValue(definition)! == roleCode)
                return (string[])tuple.GetField("Item3")!.GetValue(definition)!;
        }

        return [];
    }

    // ---- Test bench ----------------------------------------------------------------------------

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

    /// <summary>A <see cref="ResultEntryService"/> over in-memory stubs: one patient, one order.</summary>
    private sealed class Bench
    {
        public Patient Patient { get; }
        public LabOrder Order { get; }
        public ResultEntryService Service { get; }
        public List<string?> AuditReasons { get; } = [];
        public List<TestResult> Results { get; } = [];

        private readonly Dictionary<Guid, AnalysisTest> _analyses = [];

        public List<Sample> Samples { get; } = [];

        public Bench(IPermissionGuard? guard = null)
        {
            Patient = new Patient("P-0000", "BENNANI", "Karim", new DateOnly(1985, 5, 5),
                                  Gender.Male, Guid.NewGuid());
            Order = new LabOrder("2610-000010", Patient.Id, Guid.NewGuid(), Now, Guid.NewGuid());

            var order = Order;
            var self = this;
            string? pendingReason = null;

            Service = new ResultEntryService(
                Stub.Of<IRepository<LabOrder>>(new()
                {
                    ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<LabOrder?>(order)
                }),
                Stub.Of<IRepository<AnalysisTest>>(new()
                {
                    ["GetByIdWithDetailsAsync"] = args =>
                        Task.FromResult(self._analyses.GetValueOrDefault((Guid)args![0]!))
                }),
                Stub.Of<IRepository<Patient>>(new() { ["GetByIdAsync"] = _ => Task.FromResult<Patient?>(self.Patient) }),
                Stub.Of<IRepository<Sample>>(new()
                {
                    ["GetByIdAsync"] = args =>
                        Task.FromResult(self.Samples.FirstOrDefault(t => t.Id == (Guid)args![0]!))
                }),
                Stub.Of<IResultQueries>(new()
                {
                    ["GetResultsForOrderAsync"] = _ => Task.FromResult<IReadOnlyList<TestResult>>(self.Results.ToList()),
                    ["GetCurrentResultsForOrderAsync"] = _ => Task.FromResult<IReadOnlyList<TestResult>>(self.Results.ToList()),
                    ["GetActiveReflexRulesAsync"] = _ => Task.FromResult<IReadOnlyList<ReflexRule>>([]),
                    ["GetPreviousValuesAsync"] = _ => Task.FromResult<IReadOnlyDictionary<Guid, PreviousValue>>(
                        new Dictionary<Guid, PreviousValue>())
                }),
                Stub.Of<IUnitOfWork>(new()
                {
                    ["set_AuditReason"] = args => pendingReason = (string?)args![0],
                    ["get_AuditReason"] = _ => pendingReason,
                    ["SaveChangesAsync"] = _ =>
                    {
                        self.AuditReasons.Add(pendingReason);
                        pendingReason = null;
                        return Task.FromResult(1);
                    }
                }),
                guard ?? Stub.Of<IPermissionGuard>(),
                CurrentUser(),
                Stub.Of<IDataAccessAuditor>(),
                Clock());
        }

        /// <summary>One received analysis carrying the given parameters, with an awaited result for each.</summary>
        public List<TestResult> AddAnalysis(params TestParameter[] parameters)
        {
            var analysis = new AnalysisTest("A" + _analyses.Count, new MultilingualText("Analyse"),
                                            Guid.NewGuid(), Guid.NewGuid());
            foreach (var parameter in parameters) analysis.AddParameter(parameter);
            _analyses[analysis.Id] = analysis;

            var item = new LabOrderItem(analysis.Id, OrderPriority.Routine);
            Order.AddItem(item);
            item.MarkCollected(Now);
            item.MarkReceived(Now, null);

            var created = parameters
                .Select(p => new TestResult(Order.Id, item.Id, p.Id, Order.SiteId))
                .ToList();
            Results.AddRange(created);
            return created;
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
