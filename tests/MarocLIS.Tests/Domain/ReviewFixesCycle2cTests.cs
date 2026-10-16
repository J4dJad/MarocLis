using FluentAssertions;
using MarocLIS.Application.Treasury;
using MarocLIS.Desktop.Infrastructure;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>Cycle 2, lot c2c (HR, finance, administration, steering): the checks that need no database.</summary>
public sealed class ReviewFixesCycle2cTests
{
    /// <summary>
    /// A statement mapped on the wrong column reads the RIB or a long reference as the amount: the
    /// line is refused as unreadable instead of failing the import on the decimal(18,2) column.
    /// </summary>
    [Theory]
    [InlineData("011780000012345678901234")]
    [InlineData("10000000000000000,00")]
    [InlineData("-99 999 999 999 999 999,99")]
    public void AnAmountBeyondTheColumn_IsUnreadable(string text) =>
        BankStatementCsv.ParseAmount(text, ',').Should().BeNull();

    [Fact]
    public void TheLargestAmountTheColumnHolds_IsStillRead() =>
        BankStatementCsv.ParseAmount("9 999 999 999 999 999,99", ',').Should().Be(9_999_999_999_999_999.99m);

    /// <summary>
    /// The longest figure a money box lets through, with or without its grouping spaces and
    /// decimals, still fits a decimal(18,2) column. Since cycle 3 the reader itself refuses twelve
    /// figures and beyond (<see cref="EditorValues.Limit"/>), so the longest accepted amount is
    /// "999 999 999 999".
    /// </summary>
    [Theory]
    [InlineData("999 999 999 999")]
    [InlineData("999999999999,99")]
    public void TheLongestAmountABoxTakes_FitsTheColumn(string text)
    {
        text.Length.Should().BeLessThanOrEqualTo(EditorValues.AmountMaxLength);

        var value = EditorValues.ParseDecimal(text);
        value.Should().NotBeNull();
        Math.Round(value!.Value, 2).Should().BeLessThan(10_000_000_000_000_000m);
    }
}
