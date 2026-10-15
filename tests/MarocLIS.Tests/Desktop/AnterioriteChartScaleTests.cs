using FluentAssertions;
using MarocLIS.Desktop.Controls;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>The value axis of the antériorité chart: round ticks that cover every value.</summary>
public sealed class AnterioriteChartScaleTests
{
    [Theory]
    [InlineData(0.7, 1.0)]
    [InlineData(1.8, 2.0)]
    [InlineData(2.2, 2.5)]
    [InlineData(3.1, 5.0)]
    [InlineData(7.0, 10.0)]
    [InlineData(0.034, 0.05)]
    [InlineData(120.0, 200.0)]
    public void NiceStep_RoundsUpToOneTwoTwoAndAHalfOrFive(double rough, double expected) =>
        ChartScale.NiceStep((decimal)rough).Should().Be((decimal)expected);

    [Fact]
    public void NiceTicks_CoverTheRange_OnRoundValues()
    {
        // A glycaemia between 0.82 and 1.37 g/L, interval 0.70–1.10.
        var ticks = ChartScale.NiceTicks(0.70m, 1.37m);

        ticks[0].Should().BeLessThanOrEqualTo(0.70m);
        ticks[^1].Should().BeGreaterThanOrEqualTo(1.37m);
        ticks.Count.Should().BeInRange(3, 8);

        var step = ticks[1] - ticks[0];
        step.Should().Be(0.2m);
        ticks.Should().Equal(0.6m, 0.8m, 1.0m, 1.2m, 1.4m);
        ticks.Zip(ticks.Skip(1), (a, b) => b - a).Should().AllSatisfy(d => d.Should().Be(step));
        ticks.Should().AllSatisfy(t => (t % step).Should().Be(0m));
    }

    [Fact]
    public void NiceTicks_OfASingleValue_SpanAroundIt()
    {
        var ticks = ChartScale.NiceTicks(5m, 5m);

        ticks[0].Should().BeLessThan(5m);
        ticks[^1].Should().BeGreaterThan(5m);
    }

    [Fact]
    public void NiceTicks_OfZero_SpanAroundZero()
    {
        var ticks = ChartScale.NiceTicks(0m, 0m);

        ticks[0].Should().BeLessThan(0m);
        ticks[^1].Should().BeGreaterThan(0m);
    }

    [Fact]
    public void NiceTicks_AcceptTheBoundsReversed() =>
        ChartScale.NiceTicks(140m, 3m).Should().Equal(ChartScale.NiceTicks(3m, 140m));

    [Theory]
    [InlineData(1.0, 0)]
    [InlineData(20.0, 0)]
    [InlineData(0.5, 1)]
    [InlineData(0.25, 2)]
    [InlineData(0.002, 3)]
    public void DecimalsFor_ShowsTheStepExactly(double step, int expected) =>
        ChartScale.DecimalsFor((decimal)step).Should().Be(expected);
}
