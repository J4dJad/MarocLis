using FluentAssertions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Common;
using Xunit;

namespace MarocLIS.Tests.Billing;

/// <summary>
/// Tests for the amount-in-words rendering carried on every invoice.
/// <para>
/// This is not decoration. The mention in words is a fraud control — a figure on a printed invoice
/// can be altered, the words much less easily — and it is what a DGI inspector reads. A wrong
/// rendering is therefore a defect on a legal document, not a cosmetic one.
/// </para>
/// <para>
/// French number words are the part that breaks: the vigesimal tens (<c>soixante-dix</c>,
/// <c>quatre-vingt-dix-sept</c>), the "et un" of 21 but not of 81, and the plural that appears on
/// <c>cents</c> and <c>quatre-vingts</c> only when nothing follows. Each of those has its own case
/// below, because each is a rule somebody will eventually simplify.
/// </para>
/// </summary>
public sealed class AmountInWordsTests
{
    private static string Fr(decimal amount) => AmountInWords.Render(amount, AppLanguage.French);

    // ---- The plain range ---------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "Zéro dirham")]
    [InlineData(1, "Un dirham")]
    [InlineData(2, "Deux dirhams")]
    [InlineData(16, "Seize dirhams")]
    [InlineData(19, "Dix-neuf dirhams")]
    [InlineData(20, "Vingt dirhams")]
    public void Renders_small_amounts(int amount, string expected) =>
        Fr(amount).Should().Be(expected);

    /// <summary>
    /// The singular on "dirham" follows the amount, so one dirham is not "un dirhams". Zero takes
    /// the singular too, which is the French convention.
    /// </summary>
    [Fact]
    public void Uses_the_singular_for_one_dirham_only()
    {
        Fr(1m).Should().EndWith("dirham");
        Fr(0m).Should().EndWith("dirham");
        Fr(2m).Should().EndWith("dirhams");
    }

    // ---- The vigesimal tens -------------------------------------------------------------------------

    /// <summary>
    /// Seventy to ninety-nine are built on <c>soixante</c> and <c>quatre-vingt</c> rather than on
    /// words of their own. <c>Units</c> has to reach nineteen for this to work at all, which is the
    /// bug this case was first written to catch.
    /// </summary>
    [Theory]
    [InlineData(70, "Soixante-dix dirhams")]
    [InlineData(71, "Soixante et onze dirhams")]
    [InlineData(72, "Soixante-douze dirhams")]
    [InlineData(77, "Soixante-dix-sept dirhams")]
    [InlineData(79, "Soixante-dix-neuf dirhams")]
    [InlineData(80, "Quatre-vingts dirhams")]
    [InlineData(81, "Quatre-vingt-un dirhams")]
    [InlineData(90, "Quatre-vingt-dix dirhams")]
    [InlineData(91, "Quatre-vingt-onze dirhams")]
    [InlineData(97, "Quatre-vingt-dix-sept dirhams")]
    [InlineData(99, "Quatre-vingt-dix-neuf dirhams")]
    public void Renders_the_vigesimal_tens(int amount, string expected) =>
        Fr(amount).Should().Be(expected);

    /// <summary>
    /// "et un" applies to 21 through 61 and to 71, but never to 81 — a distinction no rule derived
    /// from "ends in one" will produce.
    /// </summary>
    [Theory]
    [InlineData(21, "Vingt et un dirhams")]
    [InlineData(31, "Trente et un dirhams")]
    [InlineData(41, "Quarante et un dirhams")]
    [InlineData(51, "Cinquante et un dirhams")]
    [InlineData(61, "Soixante et un dirhams")]
    [InlineData(71, "Soixante et onze dirhams")]
    [InlineData(81, "Quatre-vingt-un dirhams")]
    public void Applies_et_un_everywhere_except_eighty_one(int amount, string expected) =>
        Fr(amount).Should().Be(expected);

    /// <summary>
    /// <c>quatre-vingts</c> takes its s only when nothing follows it: eighty is plural, eighty-two
    /// is not.
    /// </summary>
    [Fact]
    public void Pluralises_quatre_vingts_only_when_it_ends_the_number()
    {
        Fr(80m).Should().Be("Quatre-vingts dirhams");
        Fr(82m).Should().Be("Quatre-vingt-deux dirhams");
        Fr(180m).Should().Be("Cent quatre-vingts dirhams");
    }

    // ---- Hundreds ------------------------------------------------------------------------------------

    /// <summary>
    /// One hundred is "cent", not "un cent"; and <c>cents</c> takes its s only when nothing
    /// follows — "deux cents" but "deux cent dix".
    /// </summary>
    [Theory]
    [InlineData(100, "Cent dirhams")]
    [InlineData(101, "Cent un dirhams")]
    [InlineData(180, "Cent quatre-vingts dirhams")]
    [InlineData(200, "Deux cents dirhams")]
    [InlineData(210, "Deux cent dix dirhams")]
    [InlineData(999, "Neuf cent quatre-vingt-dix-neuf dirhams")]
    public void Renders_hundreds(int amount, string expected) =>
        Fr(amount).Should().Be(expected);

    // ---- Thousands and millions ---------------------------------------------------------------------

    /// <summary>
    /// One thousand is "mille", never "un mille", and <c>mille</c> is invariable — it takes no s
    /// however many there are.
    /// </summary>
    [Theory]
    [InlineData(1_000, "Mille dirhams")]
    [InlineData(1_001, "Mille un dirhams")]
    [InlineData(1_200, "Mille deux cents dirhams")]
    [InlineData(2_000, "Deux mille dirhams")]
    [InlineData(999_999, "Neuf cent quatre-vingt-dix-neuf mille neuf cent quatre-vingt-dix-neuf dirhams")]
    public void Renders_thousands(int amount, string expected) =>
        Fr(amount).Should().Be(expected);

    /// <summary>
    /// <c>cent</c> and <c>quatre-vingt</c> lose their plural s in front of <c>mille</c>, which is
    /// not a noun — "deux cent mille", never "deux cents mille". They keep it in front of
    /// <c>millions</c>, which is.
    /// </summary>
    [Theory]
    [InlineData(80_000, "Quatre-vingt mille dirhams")]
    [InlineData(200_000, "Deux cent mille dirhams")]
    [InlineData(280_000, "Deux cent quatre-vingt mille dirhams")]
    [InlineData(200_000_000, "Deux cents millions de dirhams")]
    public void Drops_the_plural_before_mille_but_keeps_it_before_millions(
        long amount, string expected) => Fr(amount).Should().Be(expected);

    /// <summary>
    /// <c>million</c> is a noun: it takes a plural, and it takes "de" before the currency — but
    /// only when it is the last word. "Un million de dirhams", yet "un million un dirhams" with no
    /// "de" once something follows it.
    /// <para>
    /// The currency itself stays plural throughout: it agrees with the amount, which is well over
    /// one, not with the number word next to it. Same as <c>Renders_thousands</c>' "Mille un
    /// dirhams" — this case read "dirham" and was the one assertion in the file disagreeing with
    /// that.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1_000_000, "Un million de dirhams")]
    [InlineData(2_000_000, "Deux millions de dirhams")]
    [InlineData(1_000_001, "Un million un dirhams")]
    [InlineData(1_000_200, "Un million deux cents dirhams")]
    public void Renders_millions(long amount, string expected) =>
        Fr(amount).Should().Be(expected);

    // ---- Centimes -------------------------------------------------------------------------------------

    /// <summary>
    /// Centimes are appended only when there are any: an invoice for a round amount should not read
    /// "et zéro centimes", which looks like a rendering fault to whoever reads it.
    /// </summary>
    [Fact]
    public void Omits_centimes_when_the_amount_is_round()
    {
        Fr(250m).Should().Be("Deux cent cinquante dirhams");
        Fr(250m).Should().NotContain("centime");
    }

    [Theory]
    [InlineData(250.50, "Deux cent cinquante dirhams et cinquante centimes")]
    [InlineData(250.01, "Deux cent cinquante dirhams et un centime")]
    [InlineData(0.75, "Zéro dirham et soixante-quinze centimes")]
    public void Renders_centimes(decimal amount, string expected) =>
        Fr(amount).Should().Be(expected);

    /// <summary>
    /// The centime is rounded away from zero, the same convention <see cref="Money"/> applies to the
    /// figure itself. If the two disagreed, an invoice could show 12,35 in figures and "trente-quatre
    /// centimes" in words — a discrepancy on a tax document.
    /// </summary>
    [Fact]
    public void Rounds_the_centime_away_from_zero_like_the_figure()
    {
        Fr(12.345m).Should().Be("Douze dirhams et trente-cinq centimes");
        Fr(12.344m).Should().Be("Douze dirhams et trente-quatre centimes");
    }

    /// <summary>
    /// A credit note is held as a negative amount in places, but the words on the document describe
    /// a sum, not a direction — the sign belongs to the document type, not to the wording.
    /// </summary>
    [Fact]
    public void Renders_the_absolute_value()
    {
        Fr(-250m).Should().Be("Deux cent cinquante dirhams");
        Fr(-250m).Should().NotContain("moins");
    }

    /// <summary>The rendering starts with a capital: it appears as a sentence on the invoice.</summary>
    [Fact]
    public void Starts_with_a_capital_letter() =>
        Fr(42m).Should().StartWith("Quarante");

    // ---- The other two languages ---------------------------------------------------------------------

    /// <summary>
    /// English and Arabic render the figure with its noun rather than spelling the number out.
    /// <para>
    /// That is deliberate for Arabic: agreement there involves dual forms and case endings that vary
    /// with the counted noun, and a half-correct spelling-out on a legal document is worse than a
    /// figure nobody can misread. The French rendering is the one the regulation cares about, since
    /// French is the language Moroccan invoices are issued in.
    /// </para>
    /// </summary>
    [Fact]
    public void Renders_english_and_arabic_with_the_figure_and_its_noun()
    {
        AmountInWords.Render(1_250.50m, AppLanguage.English)
                     .Should().Be("1,250 dirhams and 50 centimes");

        AmountInWords.Render(1m, AppLanguage.English).Should().Be("1 dirham");

        var arabic = AmountInWords.Render(1_250.50m, AppLanguage.Arabic);

        arabic.Should().Contain("1,250").And.Contain("درهم");
        arabic.Should().Contain("50").And.Contain("سنتيم");
    }

    /// <summary>
    /// A sanity sweep: no amount in the range an invoice can plausibly reach may render empty, with
    /// a doubled space, or with a stray hyphen at either end. Those are the shapes a mistake in the
    /// concatenation takes, and they would all pass a check that only tested a handful of values.
    /// </summary>
    [Fact]
    public void Never_renders_a_malformed_string()
    {
        for (var amount = 0; amount <= 2_500; amount++)
        {
            var text = Fr(amount);

            text.Should().NotBeNullOrWhiteSpace($"amount {amount} must render");
            text.Should().NotContain("  ", $"amount {amount} has a doubled space");
            text.Should().NotContain("--", $"amount {amount} has a doubled hyphen");
            text.Should().NotEndWith("-", $"amount {amount} ends with a hyphen");
            text.Should().NotContain(" -", $"amount {amount} has a loose hyphen");
            text.Should().NotContain("- ", $"amount {amount} has a loose hyphen");
        }
    }
}
