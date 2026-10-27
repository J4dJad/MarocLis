using FluentAssertions;
using MarocLIS.Application.Statistics;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using Xunit;

namespace MarocLIS.Tests.Statistics;

/// <summary>
/// Tests for the activity statistics. The definitions — what counts as a test, what "on time"
/// means, which day an order belongs to — are the part a management review relies on without
/// checking, so each one is pinned here.
/// </summary>
public sealed class StatisticsComputeTests
{
    private static readonly TimeSpan Morocco = TimeSpan.FromHours(1);
    private static readonly DateOnly Day1 = new(2026, 10, 1);
    private static readonly DateOnly Day3 = new(2026, 10, 3);

    private static readonly Guid Biochemistry = Guid.NewGuid();
    private static readonly Guid Haematology = Guid.NewGuid();
    private static readonly Guid Glucose = Guid.NewGuid();
    private static readonly Guid BloodCount = Guid.NewGuid();
    private static readonly Guid Cnops = Guid.NewGuid();

    private static readonly StatisticsLabels Labels = new(
        new Dictionary<Guid, (string, MultilingualText)>
        {
            [Glucose] = ("GLU", new MultilingualText("Glycémie")),
            [BloodCount] = ("NFS", new MultilingualText("Numération formule sanguine"))
        },
        new Dictionary<Guid, (string, MultilingualText)>
        {
            [Biochemistry] = ("BIO", new MultilingualText("Biochimie")),
            [Haematology] = ("HEM", new MultilingualText("Hématologie"))
        },
        new Dictionary<Guid, MultilingualText> { [Cnops] = new MultilingualText("CNOPS") });

    private static DateTimeOffset At(DateOnly day, int hour) =>
        new(day.ToDateTime(new TimeOnly(hour, 0)), Morocco);

    private static ActivityItem Item(Guid order, Guid patient, DateTimeOffset registered, Guid test,
                                     Guid department, LabOrderItemStatus status = LabOrderItemStatus.Validated,
                                     double? hoursToValidation = 2, double? dueHours = 4,
                                     decimal price = 100m, Guid? payer = null) =>
        new(order, patient, registered, payer, test, department, status,
            registered,
            hoursToValidation is { } h ? registered.AddHours(h) : null,
            dueHours is { } d ? registered.AddHours(d) : null,
            price, payer is null ? 0m : price * 0.8m, payer is null ? price : price * 0.2m);

    private static readonly FinanceTotals Finance = new(1000m, 100m, 800m);

    [Fact]
    public void Compute_CountsOrdersPatientsAndTests_AndExcludesCancelledFromVolumes()
    {
        var patient = Guid.NewGuid();
        var order = Guid.NewGuid();
        var registered = At(Day1, 9);

        var items = new[]
        {
            Item(order, patient, registered, Glucose, Biochemistry),
            Item(order, patient, registered, BloodCount, Haematology),
            Item(order, patient, registered, Glucose, Biochemistry, LabOrderItemStatus.Cancelled)
        };

        var stats = StatisticsService.Compute(Day1, Day3, items, Labels, Finance, Morocco);

        stats.Summary.OrderCount.Should().Be(1);
        stats.Summary.PatientCount.Should().Be(1);
        stats.Summary.TestCount.Should().Be(2);
        stats.Summary.CancelledTestCount.Should().Be(1);
        stats.Summary.ChargedAmount.Should().Be(200m);
        stats.Summary.InvoicedAmount.Should().Be(1000m);
        stats.Departments.Sum(d => d.TestCount).Should().Be(2);
    }

    [Fact]
    public void Compute_OnTime_CountsOnlyValidatedTestsThatHadADueTime()
    {
        var registered = At(Day1, 9);
        var items = new[]
        {
            Item(Guid.NewGuid(), Guid.NewGuid(), registered, Glucose, Biochemistry, hoursToValidation: 2, dueHours: 4),
            Item(Guid.NewGuid(), Guid.NewGuid(), registered, Glucose, Biochemistry, hoursToValidation: 6, dueHours: 4),
            Item(Guid.NewGuid(), Guid.NewGuid(), registered, Glucose, Biochemistry, hoursToValidation: 1, dueHours: null),
            Item(Guid.NewGuid(), Guid.NewGuid(), registered, Glucose, Biochemistry,
                 LabOrderItemStatus.InProgress, hoursToValidation: null, dueHours: 4)
        };

        var stats = StatisticsService.Compute(Day1, Day3, items, Labels, Finance, Morocco);

        stats.Summary.OnTimePercent.Should().Be(50.0);
        stats.Summary.AverageTurnaroundHours.Should().Be(3.0);
        stats.Summary.ValidatedTestCount.Should().Be(3);
    }

    [Fact]
    public void Compute_ListsEveryDayOfThePeriod_IncludingEmptyOnes()
    {
        var items = new[] { Item(Guid.NewGuid(), Guid.NewGuid(), At(Day3, 10), Glucose, Biochemistry) };

        var stats = StatisticsService.Compute(Day1, Day3, items, Labels, Finance, Morocco);

        stats.Days.Select(d => d.Date).Should().Equal(Day1, Day1.AddDays(1), Day3);
        stats.Days.Select(d => d.TestCount).Should().Equal(0, 0, 1);
    }

    [Fact]
    public void Compute_PlacesAnOrderOnItsLocalDay_NotItsUtcDay()
    {
        // 00:30 local on the 2nd is still the 1st in UTC; the laboratory counts it on the 2nd.
        var registered = new DateTimeOffset(Day1.AddDays(1).ToDateTime(new TimeOnly(0, 30)), Morocco)
            .ToUniversalTime();
        var items = new[] { Item(Guid.NewGuid(), Guid.NewGuid(), registered, Glucose, Biochemistry) };

        var stats = StatisticsService.Compute(Day1, Day3, items, Labels, Finance, Morocco);

        stats.Days.Single(d => d.Date == Day1.AddDays(1)).OrderCount.Should().Be(1);
    }

    [Fact]
    public void Compute_GroupsByPayer_WithSelfPayingAsUnnamed()
    {
        var items = new[]
        {
            Item(Guid.NewGuid(), Guid.NewGuid(), At(Day1, 9), Glucose, Biochemistry, payer: Cnops),
            Item(Guid.NewGuid(), Guid.NewGuid(), At(Day1, 9), Glucose, Biochemistry)
        };

        var stats = StatisticsService.Compute(Day1, Day3, items, Labels, Finance, Morocco);

        stats.Payers.Should().HaveCount(2);
        stats.Payers.Single(p => p.Name is not null).PayerShare.Should().Be(80m);
        stats.Payers.Single(p => p.Name is null).PatientShare.Should().Be(100m);
    }

    [Fact]
    public void Compute_WithNoActivity_ReturnsZeroesAndNoAverages()
    {
        var stats = StatisticsService.Compute(Day1, Day1, [], Labels, new FinanceTotals(0m, 0m, 0m), Morocco);

        stats.Summary.TestCount.Should().Be(0);
        stats.Summary.AverageTurnaroundHours.Should().BeNull();
        stats.Summary.OnTimePercent.Should().BeNull();
        stats.Days.Should().ContainSingle();
    }

    [Fact]
    public void Compute_PlacesEachOrderOnItsLocalDay_OnBothSidesOfTheRamadanChangeOfHour()
    {
        var casablanca = TimeZoneInfo.FindSystemTimeZoneById("Africa/Casablanca");
        var duringRamadan = new DateOnly(2026, 3, 1);                     // UTC+0
        var after = new DateOnly(2026, 4, 1);                            // UTC+1

        var items = new[]
        {
            Item(Guid.NewGuid(), Guid.NewGuid(), new DateTimeOffset(2026, 3, 1, 23, 30, 0, TimeSpan.Zero), Glucose, Biochemistry),
            Item(Guid.NewGuid(), Guid.NewGuid(), new DateTimeOffset(2026, 4, 1, 23, 30, 0, Morocco), Glucose, Biochemistry)
        };

        var stats = StatisticsService.Compute(duringRamadan, after.AddDays(1), items, Labels, Finance, casablanca);

        stats.Days.Single(d => d.Date == duringRamadan).OrderCount.Should().Be(1, "23:30 in Ramadan is still the 1st of March");
        stats.Days.Single(d => d.Date == after).OrderCount.Should().Be(1);
        stats.Days.Sum(d => d.OrderCount).Should().Be(2);

        StatisticsService.LocalMidnight(duringRamadan, casablanca).Offset.Should().Be(TimeSpan.Zero);
        StatisticsService.LocalMidnight(after, casablanca).Offset.Should().Be(Morocco);
    }
}
