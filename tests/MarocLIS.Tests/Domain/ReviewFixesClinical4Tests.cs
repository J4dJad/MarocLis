using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Clinical fixes from the third verification of 2026-10-15 that need no database: a repeated
/// analysis waits for its re-run (V3), the pipeline interprets only what changed with one history
/// lookup (V6), and age bands whose unit is to confirm (V7). The stale screen (V1, V5), the
/// analyser identity (V2) and the merge (V4) are in
/// <see cref="Persistence.ReviewFixesClinical4DatabaseTests"/>.
/// </summary>
public sealed class ReviewFixesClinical4Tests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static TestParameter Numeric(string code, decimal? low = null, decimal? high = null)
    {
        var parameter = new TestParameter(code, new MultilingualText(code), ResultValueType.Numeric);
        if (low is not null || high is not null) parameter.AddReferenceRange(ReferenceRange.Numeric(low, high));
        return parameter;
    }

    private static LabOrderItem ReceivedItem(LabOrder order)
    {
        var item = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);
        order.AddItem(item);
        item.MarkCollected(Now.AddHours(-2));
        item.MarkReceived(Now.AddHours(-2), null);
        return item;
    }

    // ---- V3: a repeated analysis waits for its re-run ------------------------------------------

    [Fact]
    public void V3_ARepeatedAnalysis_IsNotCompletedAgainByItsFirstRunsValues()
    {
        var order = new LabOrder("2610-000020", Guid.NewGuid(), Guid.NewGuid(), Now.AddHours(-3), Guid.NewGuid());
        var chemistry = ReceivedItem(order);
        var k = Numeric("K");
        var na = Numeric("NA");
        var results = new List<TestResult>
        {
            new(order.Id, chemistry.Id, k.Id, order.SiteId),
            new(order.Id, chemistry.Id, na.Id, order.SiteId)
        };
        results[0].EnterNumeric(k, 6.9m, Now.AddMinutes(-40), Guid.NewGuid());
        results[1].EnterNumeric(na, 140m, Now.AddMinutes(-40), Guid.NewGuid());
        chemistry.MarkResultEntered(Now.AddMinutes(-40));
        chemistry.MarkTechnicallyValidated(Now.AddMinutes(-35), Guid.NewGuid());

        // K looks implausible: the panel goes back to the bench, its values kept as the record.
        chemistry.Repeat("Potassium invraisemblable", Now.AddMinutes(-30)).IsSuccess.Should().BeTrue();
        foreach (var r in results) r.ReopenForRepeat();

        // Something else on the order is saved or imported: nothing new on the chemistry.
        ResultPostEntryPipeline.AdvanceCompletedItems(order, results, Now.AddMinutes(-20));

        chemistry.Status.Should().Be(LabOrderItemStatus.Received, "the re-run has not been done");
        ResultPostEntryPipeline.AwaitsRerun(chemistry, results).Should().BeTrue();

        // The re-run's potassium arrives.
        results[0].EnterNumeric(k, 4.3m, Now.AddMinutes(-10), Guid.NewGuid(), fromInstrument: true);
        ResultPostEntryPipeline.AdvanceCompletedItems(order, results, Now.AddMinutes(-10));

        chemistry.Status.Should().Be(LabOrderItemStatus.ResultEntered);
        chemistry.RepeatedAt.Should().Be(Now.AddMinutes(-30));
    }

    [Fact]
    public void V3_AnAnalysisNeverRepeated_IsCompletedAsBefore()
    {
        var order = new LabOrder("2610-000021", Guid.NewGuid(), Guid.NewGuid(), Now.AddHours(-3), Guid.NewGuid());
        var item = ReceivedItem(order);
        var hb = Numeric("HB");
        var result = new TestResult(order.Id, item.Id, hb.Id, order.SiteId);
        result.EnterNumeric(hb, 13m, Now.AddMinutes(-5), Guid.NewGuid());

        ResultPostEntryPipeline.AdvanceCompletedItems(order, [result], Now);

        item.Status.Should().Be(LabOrderItemStatus.ResultEntered);
    }

    // ---- V6: the pipeline on an analyser import -------------------------------------------------

    [Fact]
    public async Task V6_AnImportedTube_IsInterpretedAlone_WithOneHistoryLookup()
    {
        var patient = new Patient("P-0020", "BENNANI", "Karim", new DateOnly(1985, 5, 5), Gender.Male, Guid.NewGuid());
        var order = new LabOrder("2610-000022", patient.Id, patient.SiteId, Now.AddHours(-1), Guid.NewGuid());
        var serumItem = ReceivedItem(order);
        var edtaItem = ReceivedItem(order);

        var k = Numeric("K", 3.5m, 5.0m);
        var na = Numeric("NA", 135m, 145m);
        var hb = Numeric("HB", 13m, 17m);

        var kRow = new TestResult(order.Id, serumItem.Id, k.Id, order.SiteId);
        var naRow = new TestResult(order.Id, serumItem.Id, na.Id, order.SiteId);
        var hbRow = new TestResult(order.Id, edtaItem.Id, hb.Id, order.SiteId);
        kRow.EnterNumeric(k, 6.0m, Now, Guid.NewGuid(), fromInstrument: true);
        naRow.EnterNumeric(na, 150m, Now, Guid.NewGuid(), fromInstrument: true);
        // Stored earlier from the other tube, and interpreted then.
        hbRow.EnterNumeric(hb, 10m, Now.AddMinutes(-30), Guid.NewGuid(), fromInstrument: true);

        var lookups = new List<IReadOnlyCollection<Guid>>();
        var pipeline = new ResultPostEntryPipeline(
            Stub.Of<IRepository<AnalysisTest>>(),
            Stub.Of<IRepository<Patient>>(),
            Stub.Of<IRepository<Sample>>(),
            Stub.Of<IResultQueries>(new()
            {
                ["GetPreviousValuesAsync"] = args =>
                {
                    lookups.Add((IReadOnlyCollection<Guid>)args![1]!);
                    return Task.FromResult<IReadOnlyDictionary<Guid, PreviousValue>>(
                        new Dictionary<Guid, PreviousValue>());
                }
            }),
            Clock());

        var catalogue = new ResultCatalogue(
            new Dictionary<Guid, TestParameter> { [k.Id] = k, [na.Id] = na, [hb.Id] = hb },
            []);

        await pipeline.RunAsync(order, patient, [kRow, naRow, hbRow], catalogue, Guid.NewGuid(),
                                AppLanguage.French, changed: [kRow, naRow]);

        lookups.Should().ContainSingle("one history lookup for the tube, not one per value");
        lookups[0].Should().BeEquivalentTo([k.Id, na.Id]);
        kRow.Flags.Should().HaveFlag(ResultFlag.High);
        naRow.Flags.Should().HaveFlag(ResultFlag.High);
        hbRow.Interpretation.Should().NotBe(ResultInterpretation.Low, "the other tube's value was not gone over again");
    }

    // ---- V7: age bands whose unit is to confirm --------------------------------------------------

    [Theory]
    [InlineData(0, 30, null, true)]       // "0–30 jours" or "0–1 mois"
    [InlineData(30, 180, null, true)]     // "1–6 mois" or "30–180 jours"
    [InlineData(null, 60, null, true)]
    [InlineData(0, 30, 1, false)]         // saved with its unit
    [InlineData(0, 30, 30, false)]
    [InlineData(0, 14, null, false)]      // only days fit
    [InlineData(0, 18250, null, false)]   // only years fit
    [InlineData(null, null, null, false)] // no age band
    public void V7_ABandSavedWithoutItsUnit_IsToConfirm_OnlyWhenDaysAndMonthsBothFit(
        int? from, int? to, int? unit, bool toConfirm)
    {
        ReferenceRange.HasUnconfirmedAgeUnit(from, to, unit).Should().Be(toConfirm);

        new ReferenceRangeRow(Guid.NewGuid(), Guid.NewGuid(), Gender.Unknown, from, to, null, null, null, null,
                              1m, 2m, null, null, false, null, null, Today, null, true, 1, unit)
            .HasUnconfirmedAgeUnit.Should().Be(toConfirm);
    }

    // ---- Harness -------------------------------------------------------------------------------

    private static IClock Clock() => Stub.Of<IClock>(new()
    {
        ["get_Now"] = _ => Now,
        ["get_Today"] = _ => Today
    });

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

            var type = targetMethod.ReturnType;
            if (type == typeof(void)) return null;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner)
                                   .Invoke(null, [inner.IsValueType ? Activator.CreateInstance(inner) : null]);
            }
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
