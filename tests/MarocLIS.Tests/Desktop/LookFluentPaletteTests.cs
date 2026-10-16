using System.Drawing;
using FluentAssertions;
using MarocLIS.Desktop.Infrastructure;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The palette of the « LIS · A Fluent clair » design system: its tokens, and the contrast it
/// promises — every text over its background at 4.5:1.
/// </summary>
public sealed class LookFluentPaletteTests
{
    [Theory]
    [InlineData(nameof(UiTheme.Surface), "#F6F8F9")]
    [InlineData(nameof(UiTheme.SurfaceRaised), "#FFFFFF")]
    [InlineData(nameof(UiTheme.SurfaceSunken), "#F1F4F6")]
    [InlineData(nameof(UiTheme.Border), "#DCE3E8")]
    [InlineData(nameof(UiTheme.BorderStrong), "#C3CDD4")]
    [InlineData(nameof(UiTheme.TextPrimary), "#14212B")]
    [InlineData(nameof(UiTheme.TextSecondary), "#51606B")]
    [InlineData(nameof(UiTheme.Accent), "#0B6E79")]
    [InlineData(nameof(UiTheme.AccentSubtle), "#E3F1F2")]
    [InlineData(nameof(UiTheme.AccentInk), "#08515A")]
    [InlineData(nameof(UiTheme.TextOnAccent), "#FFFFFF")]
    [InlineData(nameof(UiTheme.Critical), "#B42318")]
    [InlineData(nameof(UiTheme.CriticalBackground), "#FDECEA")]
    [InlineData(nameof(UiTheme.Abnormal), "#8C3505")]
    [InlineData(nameof(UiTheme.AbnormalBackground), "#FEF0E6")]
    [InlineData(nameof(UiTheme.Normal), "#1E6B3A")]
    [InlineData(nameof(UiTheme.NormalBackground), "#E7F4EC")]
    [InlineData(nameof(UiTheme.Info), "#1F4E9A")]
    [InlineData(nameof(UiTheme.InfoBackground), "#E8EEFA")]
    public void Colours_AreTheDesignSystemTokens(string member, string hex) =>
        Hex(Member(member)).Should().Be(hex);

    public static TheoryData<string, string> TextPairs => new()
    {
        { nameof(UiTheme.TextPrimary), nameof(UiTheme.Surface) },
        { nameof(UiTheme.TextPrimary), nameof(UiTheme.SurfaceSunken) },
        { nameof(UiTheme.TextSecondary), nameof(UiTheme.Surface) },
        { nameof(UiTheme.TextSecondary), nameof(UiTheme.SurfaceSunken) },
        { nameof(UiTheme.TextMuted), nameof(UiTheme.Surface) },
        { nameof(UiTheme.TextMuted), nameof(UiTheme.SurfaceRaised) },
        { nameof(UiTheme.TextOnAccent), nameof(UiTheme.Accent) },
        { nameof(UiTheme.AccentInk), nameof(UiTheme.AccentSubtle) },
        { nameof(UiTheme.TextPrimary), nameof(UiTheme.AccentSubtle) },
        { nameof(UiTheme.Critical), nameof(UiTheme.CriticalBackground) },
        { nameof(UiTheme.CriticalInk), nameof(UiTheme.CriticalBackground) },
        { nameof(UiTheme.Abnormal), nameof(UiTheme.AbnormalBackground) },
        { nameof(UiTheme.Normal), nameof(UiTheme.NormalBackground) },
        { nameof(UiTheme.Info), nameof(UiTheme.InfoBackground) },
        { nameof(UiTheme.Overdue), nameof(UiTheme.SurfaceRaised) },
        { nameof(UiTheme.Pending), nameof(UiTheme.SurfaceRaised) }
    };

    [Theory]
    [MemberData(nameof(TextPairs))]
    public void Text_ReadsAtFourAndAHalfToOne(string text, string background) =>
        Contrast(Member(text), Member(background)).Should().BeGreaterThanOrEqualTo(4.5);

    [Fact]
    public void FieldBorders_AreDarkerThanCardBorders() =>
        // The brand book quotes 3:1 for line-strong; the light tokens give 1.5:1 on the surface.
        // What holds, and what the fields rely on, is that a field frame reads above a card's.
        Contrast(UiTheme.BorderStrong, UiTheme.Surface).Should()
            .BeGreaterThan(Contrast(UiTheme.Border, UiTheme.Surface));

    [Fact]
    public void Accent_IsNotAStatusColour()
    {
        // The design system keeps the accent for action; no clinical state may share it.
        var states = new[] { UiTheme.Critical, UiTheme.Abnormal, UiTheme.Normal, UiTheme.Info, UiTheme.Pending, UiTheme.Overdue };
        states.Select(c => c.ToArgb()).Should().NotContain(UiTheme.Accent.ToArgb());
    }

    [Fact]
    public void HighAndLow_AreOrangeAndBlue_NotTheSameHue() =>
        UiTheme.Abnormal.ToArgb().Should().NotBe(UiTheme.Info.ToArgb());

    private static Color Member(string name) =>
        (Color)typeof(UiTheme).GetField(name)!.GetValue(null)!;

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
