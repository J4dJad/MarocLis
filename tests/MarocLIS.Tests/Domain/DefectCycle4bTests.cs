using FluentAssertions;
using MarocLIS.Desktop.Infrastructure;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>Cycle 4, lot c4b (reception, files, billing, till, claims): the checks that need no database.</summary>
public sealed class DefectCycle4bTests
{
    /// <summary>
    /// The settlement, deposit, discount, payer-answer and till boxes now read amounts with the
    /// shared reader. Their own reading took « 1 250,00 », written with the space French puts between
    /// thousands, for no number, and refused it as an amount that is not positive.
    /// </summary>
    [Theory]
    [InlineData("1 250,00", "1250")]
    [InlineData("1\u00A0250,00", "1250")]
    [InlineData("1\u202F250,00", "1250")]
    [InlineData("1.250,50", "1250.5")]
    [InlineData("1250.5", "1250.5")]
    [InlineData("300", "300")]
    [InlineData("0.00", "0")]
    public void AnAmountTypedAtTheCounter_IsRead(string text, string expected)
    {
        EditorValues.ParseDecimal(text).Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        EditorValues.IsUnreadable(text).Should().BeFalse();
    }

    /// <summary>
    /// A weight or a height with its unit, or a slip, is refused on its box: it was saved as no
    /// value without a word, and the calculations that use it (clearance, body surface) had none.
    /// </summary>
    [Theory]
    [InlineData("72 kg")]
    [InlineData("1m70")]
    [InlineData("72,5,1x")]
    [InlineData("cent")]
    public void AWeightOrHeightWithItsUnit_IsUnreadable(string text) =>
        EditorValues.IsUnreadable(text).Should().BeTrue();
}
