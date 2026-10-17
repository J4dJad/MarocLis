using FluentAssertions;
using MarocLIS.Desktop.Infrastructure;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>Cycle 3, lot c3b (HR, finance, administration, steering): the checks that need no database.</summary>
public sealed class DefectCycle3bTests
{
    /// <summary>
    /// A number of twelve figures or more fits no number column (decimal(18,6) holds twelve before
    /// the comma): the shared reader refuses it, however it is written, instead of letting the save
    /// fail with « Erreur inattendue ».
    /// </summary>
    [Theory]
    [InlineData("1e17")]
    [InlineData("1E12")]
    [InlineData("1000000000000")]
    [InlineData("999999999999999")]
    [InlineData("-1 000 000 000 000")]
    [InlineData("9e27")]
    public void ANumberTooLargeForAnyColumn_IsUnreadable(string text)
    {
        EditorValues.ParseDecimal(text).Should().BeNull();
        EditorValues.IsUnreadable(text).Should().BeTrue();
    }

    /// <summary>Just below the limit, and the ordinary notations, are still read as before.</summary>
    [Theory]
    [InlineData("999 999 999 999", "999999999999")]
    [InlineData("999999999999,999999", "999999999999.999999")]
    [InlineData("-999999999999", "-999999999999")]
    [InlineData("12,5", "12.5")]
    public void ANumberWithinTheColumns_IsStillRead(string text, string expected)
    {
        EditorValues.ParseDecimal(text).Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        EditorValues.IsUnreadable(text).Should().BeFalse();
    }

    /// <summary>
    /// What the editors check before saving: an empty box is "no value", text that is no number
    /// (a unit typed with the figure, a slip) is refused on its box rather than saved as empty.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("10 %", true)]
    [InlineData("12,5x", true)]
    [InlineData("abc", true)]
    [InlineData("‎-2,5", false)]
    public void AnUnreadableBox_IsTellableFromAnEmptyOne(string? text, bool unreadable) =>
        EditorValues.IsUnreadable(text).Should().Be(unreadable);
}
