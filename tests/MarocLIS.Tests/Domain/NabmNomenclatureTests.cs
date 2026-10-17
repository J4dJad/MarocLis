using FluentAssertions;
using MarocLIS.Application.Catalog;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Infrastructure.Persistence;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// The national nomenclature of biology acts (NABM): the shipped list, the code rules, the search
/// and the comparison of the catalogue with it.
/// </summary>
public sealed class NabmNomenclatureTests
{
    // ---- Shipped list ---------------------------------------------------------------------------

    [Fact]
    public void ShippedList_HoldsEveryCodeFromB100ToB591_Once_InOrder()
    {
        var codes = NationalBiologyActSeed.Rows.Select(r => r.Code).ToList();

        codes.Should().Equal(Enumerable.Range(100, 492).Select(i => $"B{i}"));
    }

    [Fact]
    public void ShippedList_SplitsByDecree_AsTheDocumentCountsThem()
    {
        var rows = NationalBiologyActSeed.Rows;

        rows.Count(r => r.DecreeYear == 2005).Should().Be(342);
        rows.Count(r => r.DecreeYear == 2012).Should().Be(134, "« liste des 134 actes … assimilés »");
        rows.Count(r => r.DecreeYear == 2016).Should().Be(16, "« liste des 16 actes … assimilés »");
        rows.Where(r => r.DecreeYear == 2012).Select(r => r.Code).Should().StartWith("B442").And.EndWith("B575");
    }

    [Fact]
    public void ShippedList_HasALabel_APositiveCoefficient_AndAChapter_OnEveryRow()
    {
        foreach (var row in NationalBiologyActSeed.Rows)
        {
            row.Label.Should().NotBeNullOrWhiteSpace(row.Code);
            row.Label.Length.Should().BeLessThanOrEqualTo(NationalBiologyAct.LabelMaxLength);
            row.Coefficient.Should().BePositive(row.Code);
            row.Chapter.Should().NotBeNullOrWhiteSpace(row.Code);
        }
    }

    [Theory]
    [InlineData("B118", "Glycémie", 30)]
    [InlineData("B163", "TSH", 250)]
    [InlineData("B164", "T S H ulta sensible", 250)]
    [InlineData("B216", "Numération Formule (Globules rouges/Blancs/plaquettes)", 80)]
    [InlineData("B370", "C R P (Protéine C réactive)", 100)]
    [InlineData("B443", "Osmolarité \"urine \" (Si Prescription isolée)", 100)]
    [InlineData("B527", "Identification des anticorps Anti-nucléaires (ANA) : SSA, SSB, Sm, RNP, Scl-70, Jo-1, dsDNA, Cent-B, Histone par technique Luminex ou équivalent", 400)]
    [InlineData("B591", "Recherche des stupéfiants dans les urines", 300)]
    public void ShippedList_ReadsLikeTheDecree(string code, string label, int coefficient)
    {
        var row = NationalBiologyActSeed.Rows.Single(r => r.Code == code);

        row.Label.Should().Be(label);
        row.Coefficient.Should().Be(coefficient);
    }

    [Fact]
    public void ShippedList_KeepsTheChapters()
    {
        var glycemie = NationalBiologyActSeed.Rows.Single(r => r.Code == "B118");
        glycemie.Chapter.Should().Be("I - Analyses biochimiques");
        glycemie.SubChapter.Should().Be("1 - Examens sanguins / a - Chimie courante");

        NationalBiologyActSeed.Rows.Single(r => r.Code == "B400").Chapter
            .Should().Be("Prescriptions pouvant faire l'objet de transmission à l'étranger");
    }

    [Fact]
    public void CsvLine_WithAQuotedField_IsSplitOnTheSeparatorsOutsideTheQuotes()
    {
        NationalBiologyActSeed.Split("B1;\"a;\"\"b\"\"\";3").Should().Equal("B1", "a;\"b\"", "3");
    }

    // ---- Codes and search -----------------------------------------------------------------------

    [Theory]
    [InlineData(" b118 ", "B118")]
    [InlineData("B 118", "B118")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Code_IsNormalised(string? typed, string? expected) =>
        NationalBiologyAct.Normalize(typed).Should().Be(expected);

    private static NationalActRow Act(string code, string label, decimal coefficient) =>
        new(Guid.NewGuid(), code, label, coefficient, "B", "I - Analyses biochimiques", null, 2005, true);

    [Theory]
    [InlineData("118")]
    [InlineData("b118")]
    [InlineData("glycemie")]
    [InlineData("GLYCÉMIE")]
    [InlineData("biochimiques")]
    public void Search_FindsTheAct_ByCodeOrLabel_AccentsAndCaseIgnored(string search) =>
        NationalActSearch.Matches(Act("B118", "Glycémie", 30), search).Should().BeTrue();

    [Fact]
    public void Search_DoesNotMatchAnotherAct() =>
        NationalActSearch.Matches(Act("B118", "Glycémie", 30), "TSH").Should().BeFalse();

    // ---- Gaps -----------------------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, NationalActRow> Acts =
        new[] { Act("B118", "Glycémie", 30), Act("B111", "Créatinine", 30) }
            .ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);

    private static NomenclatureTariffFact T(Guid? payer, string key, decimal coefficient) => new(payer, key, coefficient);

    private static NomenclatureAnalysisFact Analysis(string code, string? act, params NomenclatureTariffFact[] tariffs) =>
        new(Guid.NewGuid(), code, new MultilingualText(code), act, tariffs);

    [Fact]
    public void Gaps_ListMissingAndUnknownCodes_AndTariffsAwayFromTheOfficialCoefficient()
    {
        var payer = Guid.NewGuid();

        var gaps = NomenclatureGaps.Compute(
        [
            Analysis("GLY", "B118", T(null, "B", 5m), T(payer, "B", 30m)),
            Analysis("CREAT", "B111", T(null, "B", 30m)),
            Analysis("ECBU", null, T(null, "B", 55m)),
            Analysis("FOO", "GLY", T(null, "B", 12m)),
            Analysis("KEY", "b111", T(null, "KB", 30m))
        ], Acts);

        gaps.Select(g => (g.AnalysisCode, g.Kind)).Should().Equal(
            ("ECBU", NomenclatureGapKind.MissingCode),
            ("FOO", NomenclatureGapKind.UnknownCode),
            ("GLY", NomenclatureGapKind.CoefficientDiffers),
            ("KEY", NomenclatureGapKind.CoefficientDiffers));

        var gly = gaps.Single(g => g.AnalysisCode == "GLY");
        gly.OfficialCoefficient.Should().Be(30m);
        gly.CurrentCoefficient.Should().Be(5m, "the laboratory list's coefficient is the one shown");
        gly.DifferingTariffCount.Should().Be(1, "the payer tariff already carries B 30");
        gly.CanApplyOfficialCoefficient.Should().BeTrue();

        gaps.Single(g => g.AnalysisCode == "FOO").CanApplyOfficialCoefficient.Should().BeFalse();
    }

    [Fact]
    public void Gaps_AnAnalysisOnTheOfficialCoefficient_IsNotListed() =>
        NomenclatureGaps.Compute([Analysis("CREAT", "B111", T(null, "B", 30m))], Acts).Should().BeEmpty();

    [Theory]
    [InlineData(5, 1.10, 5.50, 33.00)]   // followed the base: follows it
    [InlineData(5, 1.10, 40.00, 40.00)]  // set apart by the laboratory: kept
    public void Price_FollowsTheBaseOnlyWhenItDidBefore(decimal oldCoefficient, decimal keyValue,
                                                        decimal price, decimal expected) =>
        NomenclatureGaps.PriceWith(price, oldCoefficient, keyValue, 30m).Should().Be(expected);
}
