using System.Globalization;
using FluentAssertions;
using MarocLIS.Desktop.Infrastructure;
using Xunit;

namespace MarocLIS.Tests.Desktop;

public sealed class ReviewFixesUiTests
{
    private static CultureInfo AppFrench()
    {
        // As CultureManager sets it up: comma decimal, space between thousands.
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("fr-MA").Clone();
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NumberGroupSeparator = " ";
        return culture;
    }

    [Theory]
    [InlineData("150 000", 150000)]
    [InlineData("150 000,5", 150000.5)]
    [InlineData("150 000,25", 150000.25)]
    [InlineData("1,5", 1.5)]
    [InlineData("1.5", 1.5)]
    [InlineData(" 12 ", 12)]
    [InlineData("1,500.25", 1500.25)]
    [InlineData("1.500,25", 1500.25)]
    [InlineData("1.500.000", 1500000)]
    [InlineData("-0,3", -0.3)]
    public void ParseDecimal_reads_grouped_and_comma_decimal_numbers(string text, double expected) =>
        EditorValues.ParseDecimal(text).Should().Be((decimal)expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public void ParseDecimal_returns_null_for_blank_or_garbage(string? text) =>
        EditorValues.ParseDecimal(text).Should().BeNull();

    [Fact]
    public void A_limit_above_a_thousand_survives_being_displayed_in_N_format_and_read_back()
    {
        // The defect: "N" in French groups thousands, and the box came back as null.
        foreach (var culture in new[] { AppFrench(), CultureInfo.GetCultureInfo("fr-FR") })
            EditorValues.ParseDecimal(150000m.ToString("N2", culture)).Should().Be(150000m);
    }

    [Theory]
    [InlineData(150000, 2, "150000,00")]
    [InlineData(3.5, 2, "3,50")]
    [InlineData(3.456, 1, "3,456")]
    [InlineData(12, 0, "12")]
    public void FormatForEditing_has_no_group_separator_and_keeps_extra_decimals(
        double value, int decimals, string expected)
    {
        var text = EditorValues.FormatForEditing((decimal)value, decimals, AppFrench());

        text.Should().Be(expected);
        EditorValues.ParseDecimal(text).Should().Be((decimal)value);
    }

    [Theory]
    [InlineData(365, 1095, 365)]     // 1 to 3 years — was shown as 12 to 36 months
    [InlineData(null, 6570, 365)]    // up to 18 years
    [InlineData(0, 365, 365)]
    [InlineData(30, 360, 30)]        // 1 to 12 months
    [InlineData(180, 730, 1)]        // 6 months to 2 years: no common unit but days
    [InlineData(0, 28, 1)]           // neonatal
    [InlineData(null, null, 365)]
    public void AgeUnitDays_is_the_largest_unit_holding_both_bounds_exactly(
        int? from, int? to, int expected)
    {
        var unit = EditorValues.AgeUnitDays(from, to);

        unit.Should().Be(expected);
        ((from ?? 0) / unit * unit).Should().Be(from ?? 0);
        ((to ?? 0) / unit * unit).Should().Be(to ?? 0);
    }
}
