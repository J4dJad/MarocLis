using FluentAssertions;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Defect-hunt cycle 3 (lot d): a drawn volume, a value or a raw analyser answer that its column
/// cannot hold is refused (or, for the raw trace, cut) instead of failing the whole save.
/// </summary>
public sealed class DefectCycle3dTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    private static TestParameter Numeric() => new("GLY", new MultilingualText("Glycémie"), ResultValueType.Numeric)
    {
        Decimals = 2
    };

    [Theory]
    [InlineData("5000000")]
    [InlineData("1000000")]
    [InlineData("0")]
    [InlineData("-3")]
    public void ADrawnVolumeOutOfRange_IsRefused_AndTheTubeIsNotDrawn(string volume)
    {
        var tube = new Sample("0000000042", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var drawn = tube.Collect(Now, Guid.NewGuid(), decimal.Parse(volume), null);

        drawn.FirstError.ResourceKey.Should().Be("Sample_CollectedVolumeInvalid");
        drawn.FirstError.Args.Should().Contain("0000000042");
        tube.Status.Should().Be(SampleStatus.AwaitingCollection);
        tube.CollectedAt.Should().BeNull();
        tube.Events.Should().BeEmpty();
    }

    [Fact]
    public void ADrawnVolumeInRange_OrNone_IsRecorded()
    {
        var tube = new Sample("0000000043", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        tube.Collect(Now, Guid.NewGuid(), 999_999.99m, null).IsSuccess.Should().BeTrue();

        var other = new Sample("0000000044", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        other.Collect(Now, Guid.NewGuid(), null, null).IsSuccess.Should().BeTrue();
        other.VolumeMl.Should().BeNull();
    }

    [Theory]
    [InlineData("1000000000000")]
    [InlineData("-1000000000000")]
    [InlineData("100000000000000000")]
    public void AFigureOfThirteenDigits_IsTechnicallyImpossible_WhateverTheParameterSays(string value)
    {
        var result = new TestResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var entered = result.EnterNumeric(Numeric(), decimal.Parse(value), Now, Guid.NewGuid(), fromInstrument: true);

        entered.FirstError.ResourceKey.Should().Be("Result_TechnicallyImpossible");
        result.NumericValue.Should().BeNull();
        result.EnteredAt.Should().BeNull();
    }

    [Fact]
    public void TheLargestFigureTheColumnHolds_IsAValue()
    {
        var result = new TestResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        result.EnterNumeric(Numeric(), 999_999_999_999.99m, Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        result.NumericValue.Should().Be(999_999_999_999.99m);
    }

    [Fact]
    public void ATextLongerThanItsColumn_IsRefused()
    {
        var result = new TestResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        result.EnterText(new string('x', TestResult.TextValueMaxLength + 1), Now, Guid.NewGuid(), fromInstrument: true)
              .FirstError.ResourceKey.Should().Be("Result_TextTooLong");
        result.TextValue.Should().BeNull();

        result.EnterText(new string('x', TestResult.TextValueMaxLength), Now, Guid.NewGuid())
              .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void TheRawValueKept_IsCutToItsColumn()
    {
        var result = new TestResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var raw = new string('0', 250) + "5";

        result.EnterNumeric(Numeric(), 5m, Now, Guid.NewGuid(), rawValue: raw).IsSuccess.Should().BeTrue();

        result.RawInstrumentValue.Should().HaveLength(TestResult.RawValueMaxLength);
        TestResult.KeepRaw("5.4").Should().Be("5.4");
        TestResult.KeepRaw(null).Should().BeNull();
    }
}
