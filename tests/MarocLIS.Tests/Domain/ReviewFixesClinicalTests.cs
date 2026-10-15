using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Clinical-workflow fixes from the 2026-10-15 review: reference-interval validity (C1), critical
/// communication of the current value (C2), technical validation before biological release (C3),
/// and un-validation that keeps the value exactly (C4).
/// </summary>
public sealed class ReviewFixesClinicalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateOnly Today = new(2026, 10, 15);

    // ---- C1: reference interval validity window ------------------------------------------------

    private static (TestParameter Parameter, ReferenceRange Old, ReferenceRange New) RevisedGlucose()
    {
        var parameter = new TestParameter("GLY", new MultilingualText("Glycémie"), ResultValueType.Numeric);

        // As SaveRangeAsync leaves a revision: the predecessor closed the day before, still active.
        var old = ReferenceRange.Numeric(0.70m, 1.10m);
        old.ValidFrom = new DateOnly(2020, 1, 1);
        old.ValidUntil = Today.AddDays(-1);

        var successor = ReferenceRange.Numeric(0.74m, 1.06m);
        successor.ValidFrom = Today;

        parameter.AddReferenceRange(old);
        parameter.AddReferenceRange(successor);
        return (parameter, old, successor);
    }

    private static ReferenceRangeContext Adult(DateOnly? on) =>
        new(Gender.Male, 40 * 365, 40, false, null, null, on);

    [Fact]
    public void C1_ARevisedInterval_ReplacesItsPredecessorFromItsStartDate()
    {
        var (parameter, _, successor) = RevisedGlucose();

        parameter.ResolveRange(Adult(Today)).Should().BeSameAs(successor);
    }

    [Fact]
    public void C1_ASupersededInterval_StillAppliesToTheDaysItCovered()
    {
        var (parameter, old, _) = RevisedGlucose();

        parameter.ResolveRange(Adult(Today.AddDays(-1))).Should().BeSameAs(old);
    }

    [Fact]
    public void C1_AnIntervalNotYetInForce_DoesNotApply()
    {
        var parameter = new TestParameter("GLY", new MultilingualText("Glycémie"), ResultValueType.Numeric);
        var future = ReferenceRange.Numeric(0.74m, 1.06m);
        future.ValidFrom = Today.AddDays(10);
        parameter.AddReferenceRange(future);

        parameter.ResolveRange(Adult(Today)).Should().BeNull();
    }

    [Fact]
    public void C1_WithoutAReferenceDate_EquallySpecificIntervals_FavourTheMostRecent()
    {
        var (parameter, _, successor) = RevisedGlucose();

        parameter.ResolveRange(Adult(null)).Should().BeSameAs(successor);
    }

    [Fact]
    public void C1_TheContextBuiltForAPatient_CarriesTheDateItWasBuiltFor()
    {
        var patient = new Patient("P-000001", "ALAMI", "Karim", new DateOnly(1980, 5, 1), Gender.Male, Guid.NewGuid());

        ReferenceRangeContext.For(patient, Today).ReferenceDate.Should().Be(Today);
    }

    // ---- C2: critical communication covers the current value only ------------------------------

    private static TestParameter Potassium() =>
        new("K", new MultilingualText("Potassium"), ResultValueType.Numeric)
        {
            Decimals = 1,
            CriticalHigh = 6.0m
        };

    private static CriticalResultNotification Acknowledged(TestResult result, DateTimeOffset at)
    {
        var notification = new CriticalResultNotification(result.Id, result.LabOrderId, Guid.NewGuid(),
                                                          "7,2 mmol/L", at, Guid.NewGuid());
        notification.Acknowledge(at.AddMinutes(2), "Dr Bennani", "Médecin", true, null)
                    .IsSuccess.Should().BeTrue();
        return notification;
    }

    [Fact]
    public void C2_ANotificationMadeAfterEntry_CoversTheValue()
    {
        var result = new TestResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        result.EnterNumeric(Potassium(), 7.2m, Now, Guid.NewGuid());

        Acknowledged(result, Now.AddMinutes(5)).Covers(result).Should().BeTrue();
    }

    [Fact]
    public void C2_ANotificationOfAValueSinceReplaced_DoesNotCoverTheNewValue()
    {
        var result = new TestResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        result.EnterNumeric(Potassium(), 7.2m, Now, Guid.NewGuid());

        var call = Acknowledged(result, Now.AddMinutes(5));

        // Re-run on a fresh tube: a different critical value the prescriber has not been given.
        result.EnterNumeric(Potassium(), 8.4m, Now.AddMinutes(30), Guid.NewGuid());

        call.Covers(result).Should().BeFalse();
    }

    [Fact]
    public void C2_ARevisionToANewValue_RequiresANewCall()
    {
        var result = new TestResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        result.EnterNumeric(Potassium(), 7.2m, Now, Guid.NewGuid());
        var call = Acknowledged(result, Now.AddMinutes(5));

        result.Revise(8.4m, null, null, Now.AddHours(1), Guid.NewGuid(), "Erreur de transcription");

        result.EnteredAt.Should().Be(Now.AddHours(1));
        call.Covers(result).Should().BeFalse();
    }

    [Fact]
    public void C2_AnUnconfirmedOrForeignNotification_CoversNothing()
    {
        var result = new TestResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        result.EnterNumeric(Potassium(), 7.2m, Now, Guid.NewGuid());

        var pending = new CriticalResultNotification(result.Id, result.LabOrderId, Guid.NewGuid(),
                                                     "7,2", Now.AddMinutes(5), Guid.NewGuid());
        pending.Covers(result).Should().BeFalse();

        var other = new TestResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        other.EnterNumeric(Potassium(), 7.2m, Now, Guid.NewGuid());
        Acknowledged(other, Now.AddMinutes(5)).Covers(result).Should().BeFalse();
    }

    // ---- C3: technical validation before biological validation ---------------------------------

    [Fact]
    public void C3_AnEnteredResult_CannotBeBiologicallyValidated()
    {
        var result = new TestResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        result.EnterNumeric(Potassium(), 4.2m, Now, Guid.NewGuid());

        var refused = result.BiologicallyValidate(Now, Guid.NewGuid(), true, true);

        refused.IsFailure.Should().BeTrue();
        refused.FirstError.ResourceKey.Should().Be("Item_TechnicalValidationRequiredFirst");
        result.Status.Should().Be(TestResultStatus.Entered);
        result.ValidatedAt.Should().BeNull();
    }

    [Fact]
    public void C3_ATechnicallyValidatedResult_CanBeBiologicallyValidated_Once()
    {
        var result = new TestResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        result.EnterNumeric(Potassium(), 4.2m, Now, Guid.NewGuid());
        result.TechnicallyValidate(Now, Guid.NewGuid());

        result.BiologicallyValidate(Now, Guid.NewGuid(), true, true).IsSuccess.Should().BeTrue();
        result.BiologicallyValidate(Now, Guid.NewGuid(), true, true)
              .FirstError.ResourceKey.Should().Be("Result_AlreadyValidated");
    }

    [Fact]
    public async Task C3_Service_RefusesAnEnteredResult_AndLeavesTheOrderUntouched()
    {
        var harness = new Harness();
        var (item, result) = harness.AddAnalysis(technicallyValidated: false);

        var outcome = await harness.Service.ValidateAsync(
            harness.Order.Id, [new ValidationDecision(result.Id, true)]);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Validated.Should().Be(0);
        outcome.Value.Blocked.Select(e => e.ResourceKey)
               .Should().Contain("Item_TechnicalValidationRequiredFirst");
        result.Status.Should().Be(TestResultStatus.Entered);
        item.Status.Should().Be(LabOrderItemStatus.ResultEntered);
    }

    [Fact]
    public async Task C3_Service_WhenTheAnalysisRefuses_NoneOfItsResultsIsReleased()
    {
        // A result technically validated under an analysis that was not: the analysis refuses
        // biological validation, and its result must not be released on its own.
        var harness = new Harness();
        var (item, result) = harness.AddAnalysis(technicallyValidated: false);
        result.TechnicallyValidate(Now, Guid.NewGuid());

        var outcome = await harness.Service.ValidateAsync(
            harness.Order.Id, [new ValidationDecision(result.Id, true)]);

        outcome.Value.Validated.Should().Be(0);
        outcome.Value.Blocked.Select(e => e.ResourceKey)
               .Should().Contain("Item_TechnicalValidationRequiredFirst");
        result.Status.Should().Be(TestResultStatus.TechnicallyValidated);
        result.ValidatedAt.Should().BeNull();
        item.Status.Should().Be(LabOrderItemStatus.ResultEntered);
    }

    [Fact]
    public async Task C3_Service_ReleasesATechnicallyValidatedAnalysis()
    {
        var harness = new Harness();
        var (item, result) = harness.AddAnalysis(technicallyValidated: true);

        var outcome = await harness.Service.ValidateAsync(
            harness.Order.Id, [new ValidationDecision(result.Id, true)]);

        outcome.Value.Validated.Should().Be(1);
        outcome.Value.Blocked.Should().BeEmpty();
        result.Status.Should().Be(TestResultStatus.Validated);
        item.Status.Should().Be(LabOrderItemStatus.Validated);
    }

    // ---- C4: un-validation keeps the value exactly ----------------------------------------------

    [Fact]
    public void C4_WithdrawingAValidation_KeepsTheCensoredValueAndTheTitre()
    {
        var censored = new TestResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        censored.EnterCensoredNumeric(Potassium(), 100m, ">", Now, Guid.NewGuid());
        censored.TechnicallyValidate(Now, Guid.NewGuid());
        censored.BiologicallyValidate(Now, Guid.NewGuid(), true, true);

        var titre = new TestResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        titre.EnterTitre(320, Now, Guid.NewGuid());
        titre.TechnicallyValidate(Now, Guid.NewGuid());
        titre.BiologicallyValidate(Now, Guid.NewGuid(), true, true);

        censored.WithdrawValidation(Now.AddHours(1), Guid.NewGuid(), "Contrôle de cohérence")
                .IsSuccess.Should().BeTrue();
        titre.WithdrawValidation(Now.AddHours(1), Guid.NewGuid(), "Contrôle de cohérence")
             .IsSuccess.Should().BeTrue();

        censored.FormatValue(AppLanguage.French).Should().Be("> 100,0");
        censored.ValueOperator.Should().Be(">");
        titre.TitreDenominator.Should().Be(320);
        titre.FormatValue(AppLanguage.French).Should().Be("1/320");

        // Back to the biologist, not to entry: the technical check still stands.
        censored.Status.Should().Be(TestResultStatus.TechnicallyValidated);
        censored.ValidatedAt.Should().BeNull();
        censored.EnteredAt.Should().Be(Now, "the value was not re-entered");
    }

    [Fact]
    public void C4_WithdrawingAValidation_WritesTheAmendmentTrail()
    {
        var result = new TestResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var biologist = Guid.NewGuid();
        result.EnterCensoredNumeric(Potassium(), 100m, ">", Now, Guid.NewGuid());
        result.TechnicallyValidate(Now, Guid.NewGuid());
        result.BiologicallyValidate(Now, biologist, true, true);

        result.WithdrawValidation(Now, Guid.NewGuid(), " ")
              .FirstError.ResourceKey.Should().Be("Result_RevisionReasonRequired");

        result.WithdrawValidation(Now.AddHours(1), Guid.NewGuid(), "Contrôle de cohérence");

        var revision = result.Revisions.Should().ContainSingle().Subject;
        revision.PreviousNumericValue.Should().Be(100m);
        revision.PreviousValueOperator.Should().Be(">");
        revision.WasValidated.Should().BeTrue();
        revision.PreviousValidatedByUserId.Should().Be(biologist);
        result.Flags.Should().HaveFlag(ResultFlag.Corrected);
    }

    [Fact]
    public void C4_OnlyAValidatedResult_CanHaveItsValidationWithdrawn()
    {
        var result = new TestResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        result.EnterNumeric(Potassium(), 4.2m, Now, Guid.NewGuid());

        result.WithdrawValidation(Now, Guid.NewGuid(), "Contrôle de cohérence")
              .FirstError.ResourceKey.Should().Be("Item_NotValidated");
        result.Revisions.Should().BeEmpty();
    }

    [Fact]
    public void C4_RevisingToACensoredValue_KeepsTheOperator()
    {
        var result = new TestResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        result.EnterNumeric(Potassium(), 4.2m, Now, Guid.NewGuid());
        result.TechnicallyValidate(Now, Guid.NewGuid());
        result.BiologicallyValidate(Now, Guid.NewGuid(), true, true);

        result.Revise(100m, null, null, Now, Guid.NewGuid(), "Erreur de dilution",
                      newValueOperator: ">");

        result.FormatValue(AppLanguage.French).Should().Be("> 100,0");
    }

    [Fact]
    public async Task C4_Service_Unvalidation_KeepsOperatorAndTitre()
    {
        var harness = new Harness();
        var (censoredItem, censored) = harness.AddAnalysis(technicallyValidated: true,
            enter: r => r.EnterCensoredNumeric(Potassium(), 100m, ">", Now, Guid.NewGuid()));
        var (titreItem, titre) = harness.AddAnalysis(technicallyValidated: true,
            enter: r => r.EnterTitre(320, Now, Guid.NewGuid()));

        (await harness.Service.ValidateAsync(harness.Order.Id,
            [new ValidationDecision(censored.Id, true), new ValidationDecision(titre.Id, true)]))
            .Value.Validated.Should().Be(2);

        var withdrawn = await harness.Service.UnvalidateAsync(
            harness.Order.Id, [censored.Id, titre.Id], "Contrôle de cohérence demandé");

        withdrawn.IsSuccess.Should().BeTrue();
        censored.FormatValue(AppLanguage.French).Should().Be("> 100,0");
        titre.FormatValue(AppLanguage.French).Should().Be("1/320");
        censored.Revisions.Should().ContainSingle();
        titre.Revisions.Should().ContainSingle();
        censoredItem.Status.Should().Be(LabOrderItemStatus.TechnicallyValidated);
        titreItem.Status.Should().Be(LabOrderItemStatus.TechnicallyValidated);
    }

    // ---- Service harness ------------------------------------------------------------------------

    /// <summary>
    /// A <see cref="ValidationService"/> over in-memory stubs: one order, its items and results,
    /// every permission granted and every critical value communicated.
    /// </summary>
    private sealed class Harness
    {
        public LabOrder Order { get; } = new("2610-000001", Guid.NewGuid(), Guid.NewGuid(), Now, Guid.NewGuid());
        private readonly List<TestResult> _results = [];

        public ValidationService Service { get; }

        public Harness()
        {
            var order = Order;
            var results = _results;

            Service = new ValidationService(
                Stub.Of<IRepository<LabOrder>>(new()
                {
                    ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<LabOrder?>(order)
                }),
                Stub.Of<IRepository<AnalysisTest>>(),
                Stub.Of<IRepository<Instrument>>(),
                Stub.Of<IRepository<CriticalResultNotification>>(),
                Stub.Of<IResultQueries>(new()
                {
                    ["GetResultsForOrderAsync"] = _ =>
                        Task.FromResult<IReadOnlyList<TestResult>>(results.ToList())
                }),
                Stub.Of<IValidationQueries>(new()
                {
                    ["IsCriticalCommunicatedAsync"] = _ => Task.FromResult(true)
                }),
                Stub.Of<IUnitOfWork>(),
                Stub.Of<IPermissionGuard>(),
                Stub.Of<ICurrentUser>(new() { ["get_UserId"] = _ => Guid.NewGuid() }),
                Stub.Of<IDataAccessAuditor>(),
                Stub.Of<IClock>(new()
                {
                    ["get_Now"] = _ => Now,
                    ["get_Today"] = _ => Today
                }));
        }

        public (LabOrderItem Item, TestResult Result) AddAnalysis(
            bool technicallyValidated, Action<TestResult>? enter = null)
        {
            var item = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);
            Order.AddItem(item);
            item.MarkCollected(Now);
            item.MarkReceived(Now, null);
            item.MarkResultEntered(Now);

            var result = new TestResult(Order.Id, item.Id, Guid.NewGuid(), Order.SiteId);
            if (enter is null) result.EnterNumeric(Potassium(), 4.2m, Now, Guid.NewGuid());
            else enter(result);

            if (technicallyValidated)
            {
                result.TechnicallyValidate(Now, Guid.NewGuid());
                item.MarkTechnicallyValidated(Now, Guid.NewGuid());
            }

            _results.Add(result);
            return (item, result);
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
