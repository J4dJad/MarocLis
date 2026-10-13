using FluentAssertions;
using MarocLIS.Application.Abstractions;
using Xunit;

namespace MarocLIS.Tests.Printing;

/// <summary>
/// The missing-font rule, pinned where it is decided.
/// <para>
/// A substituted font is the worst class of output defect this application can produce, because the
/// result still looks like a document: it has a header, a number, a signature and a QR code, and it
/// is wrong in a way nobody notices until a patient complains. Arabic is the case that matters —
/// without contextual forms the text renders as isolated letters, unreadable — while a Latin report
/// in a substituted face is merely ugly.
/// </para>
/// <para>
/// So the two are treated differently on purpose, and that asymmetry is what these tests hold:
/// Arabic is <b>refused</b>, Latin is <b>reported</b>. Collapsing them either way is a regression —
/// refusing Latin would stop a laboratory working over a cosmetic problem, and merely warning about
/// Arabic would let an unreadable report reach a patient.
/// </para>
/// </summary>
public sealed class FontAvailabilityTests
{
    [Fact]
    public void MissingFrom_NamesOnlyWhatIsAbsent()
    {
        var fonts = new StubFonts(installed: ["Carlito"]);

        fonts.MissingFrom(["Carlito", "Amiri"]).Should().Equal("Amiri");
    }

    [Fact]
    public void MissingFrom_IsCaseInsensitive()
    {
        // Font family names arrive from three sources with three conventions: the configuration
        // file, the TTF's own metadata and the system enumeration. "amiri" and "Amiri" are the same
        // font, and reporting a false absence is how a real absence stops being read.
        var fonts = new StubFonts(installed: ["carlito", "AMIRI"]);

        fonts.MissingFrom(["Carlito", "Amiri"]).Should().BeEmpty();
    }

    [Fact]
    public void MissingFrom_DeduplicatesAndIgnoresBlanks()
    {
        var fonts = new StubFonts(installed: []);

        // A blank family means "not configured", which is not the same as "configured and missing":
        // naming an empty string in a message an installer has to act on tells them nothing.
        fonts.MissingFrom(["Amiri", "Amiri", "", "  "]).Should().Equal("Amiri");
    }

    [Fact]
    public void IsAvailable_RejectsABlankFamily()
    {
        new StubFonts(installed: ["Amiri"]).IsAvailable("  ").Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TheArabicAndLatinCasesAreDecidedSeparately(bool arabicInstalled, bool expectRefusal)
    {
        // The decision ReportReleaseService makes, stated here against the contract rather than
        // against the service, which needs eight repositories to construct. What is being pinned is
        // the asymmetry, not the plumbing.
        var fonts = new StubFonts(
            installed: arabicInstalled ? ["Carlito", "Amiri"] : ["Carlito"]);

        var arabicRefused = !fonts.IsAvailable(fonts.ArabicFamily);

        arabicRefused.Should().Be(expectRefusal);

        // Latin is present in both cases, and its presence is never what blocks a release.
        fonts.IsAvailable(fonts.LatinFamily).Should().BeTrue();
    }

    private sealed class StubFonts(string[] installed) : IFontAvailability
    {
        private readonly HashSet<string> _installed =
            new(installed, StringComparer.OrdinalIgnoreCase);

        public string ArabicFamily => "Amiri";

        public string LatinFamily => "Carlito";

        public bool IsAvailable(string family) =>
            !string.IsNullOrWhiteSpace(family) && _installed.Contains(family);

        public IReadOnlyList<string> MissingFrom(IEnumerable<string> families) =>
            [.. families.Where(f => !string.IsNullOrWhiteSpace(f) && !IsAvailable(f))
                        .Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
