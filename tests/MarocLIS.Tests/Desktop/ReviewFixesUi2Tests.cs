using FluentAssertions;
using MarocLIS.Application.Results;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Results;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>E3 (typed values kept across a re-render) and E9 (critical rows still to communicate).</summary>
public sealed class ReviewFixesUi2Tests
{
    // ---- E3 -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("5,2", null, true)]
    [InlineData("5,2", "5,2", false)]
    [InlineData(" 5,2 ", "5,2", false)]
    [InlineData("", null, false)]
    [InlineData(null, "", false)]
    [InlineData("", "5,2", true)]
    [InlineData("5,3", "5,2", true)]
    public void Differs_ignores_blanks_and_compares_exactly(string? typed, string? stored, bool expected) =>
        TypedEdits<Guid>.Differs(typed, stored).Should().Be(expected);

    [Fact]
    public void Capture_keeps_only_cells_changed_from_what_was_loaded()
    {
        var changed = Guid.NewGuid();
        var untouched = Guid.NewGuid();
        var cleared = Guid.NewGuid();

        var edits = new TypedEdits<(Guid, string)>();
        edits.Capture((changed, "value"), "7,4", "6,1");
        edits.Capture((untouched, "value"), "6,1", "6,1");
        edits.Capture((cleared, "value"), "", "6,1");
        edits.Capture((changed, "comment"), "hémolysé", null);

        edits.Count.Should().Be(3);
        edits.IsEmpty.Should().BeFalse();
        edits.TryGet((changed, "value"), out var value).Should().BeTrue();
        value.Should().Be("7,4");
        edits.TryGet((untouched, "value"), out _).Should().BeFalse();
        edits.TryGet((cleared, "value"), out var blank).Should().BeTrue();
        blank.Should().BeEmpty();
        edits.TryGet((changed, "comment"), out var comment).Should().BeTrue();
        comment.Should().Be("hémolysé");
    }

    [Fact]
    public void Nothing_typed_means_nothing_to_confirm()
    {
        var edits = new TypedEdits<Guid>();
        edits.Capture(Guid.NewGuid(), null, null);
        edits.Capture(Guid.NewGuid(), "  ", null);

        edits.IsEmpty.Should().BeTrue();
    }

    // ---- E9 -------------------------------------------------------------------------------------

    private static ResultEntryRow Row(ResultFlag flags, bool communicated) => new()
    {
        TestResultId = Guid.NewGuid(),
        LabOrderItemId = Guid.NewGuid(),
        TestParameterId = Guid.NewGuid(),
        AnalysisCode = "K",
        AnalysisName = MultilingualText.FromFrench("Potassium"),
        ParameterCode = "K",
        ParameterName = MultilingualText.FromFrench("Potassium"),
        Flags = flags,
        IsCriticalCommunicated = communicated
    };

    [Theory]
    [InlineData(ResultFlag.CriticalHigh, false, true)]
    [InlineData(ResultFlag.CriticalLow, false, true)]
    [InlineData(ResultFlag.CriticalHigh, true, false)]
    [InlineData(ResultFlag.None, false, false)]
    [InlineData(ResultFlag.DeltaCheckFailed, false, false)]
    public void Only_critical_rows_not_yet_communicated_are_called_again(
        ResultFlag flags, bool communicated, bool expected) =>
        Row(flags, communicated).NeedsCriticalCommunication.Should().Be(expected);
}
