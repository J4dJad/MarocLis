using FluentAssertions;
using MarocLIS.Desktop.Infrastructure;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>The discount box of « Émettre la facture » takes an amount or a percentage of the gross.</summary>
public sealed class InvoiceDiscountPercentTests
{
    [Theory]
    [InlineData("10%", 74.70)]
    [InlineData("10 %", 74.70)]
    [InlineData(" 10 % ", 74.70)]
    [InlineData("10٪", 74.70)]
    [InlineData("12,5 %", 93.38)]
    [InlineData("12.5%", 93.38)]
    [InlineData("100%", 747)]
    [InlineData("0%", 0)]
    public void A_percentage_becomes_the_amount_it_stands_for(string text, double expected) =>
        EditorValues.ParseDiscount(text, 747m).Should().Be((decimal)expected);

    [Theory]
    [InlineData("50", 50)]
    [InlineData("74,70", 74.70)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    public void An_amount_or_an_empty_box_reads_as_before(string text, double expected) =>
        EditorValues.ParseDiscount(text, 747m).Should().Be((decimal)expected);

    [Theory]
    [InlineData("101%")]
    [InlineData("-5%")]
    [InlineData("%")]
    [InlineData("dix %")]
    [InlineData("abc")]
    public void Anything_else_is_unreadable(string text) =>
        EditorValues.ParseDiscount(text, 747m).Should().BeNull();

    [Theory]
    [InlineData("10%", true)]
    [InlineData("10 ٪", true)]
    [InlineData("50", false)]
    [InlineData("", false)]
    public void Tells_a_percentage_from_an_amount(string text, bool expected) =>
        EditorValues.IsPercentDiscount(text).Should().Be(expected);
}
