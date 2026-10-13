using FluentAssertions;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using Xunit;

namespace MarocLIS.Tests.Pricing;

/// <summary>
/// Panels sold at a package price, and analyses with no tariff in force.
/// <para>
/// The package price was entered in the catalogue and then ignored: a lipid profile at 200 DH was
/// billed as the sum of its four analyses. An analysis with no tariff in force went through at
/// nothing. Both are invoices that look right and are not.
/// </para>
/// </summary>
public sealed class PackagePricingTests
{
    private static readonly DateOnly Today = new(2026, 10, 13);

    private static AnalysisTest Analysis(string code, decimal? price, Guid? payerId = null)
    {
        var test = new AnalysisTest(code, new MultilingualText(code), Guid.NewGuid(), Guid.NewGuid());

        if (price is { } p)
            test.AddTariff(new AnalysisTestTariff(test.Id, payerId, Today.AddYears(-1))
            {
                Coefficient = 10m,
                LetterKeyValue = 1.10m,
                Price = p
            });

        return test;
    }

    [Fact]
    public void PackagePrice_IsSpreadOverItsMembers_AndAddsUpExactly()
    {
        var panel = Guid.NewGuid();
        var selected = new List<OrderComposer.SelectedAnalysis>
        {
            new(Analysis("CHOL", 60m), panel, 200m),
            new(Analysis("TG", 60m), panel, 200m),
            new(Analysis("HDL", 80m), panel, 200m),
            new(Analysis("GLY", 40m), null)
        };

        var inputs = OrderComposer.BuildPricingInputs(selected, null, Today);

        inputs.Where(i => i.TestPanelId == panel).Sum(i => i.ChargedUnitPrice).Should().Be(200m);
        inputs.Single(i => i.AnalysisCode == "HDL").ChargedUnitPrice.Should().Be(80m);
        inputs.Single(i => i.AnalysisCode == "GLY").ChargedUnitPrice.Should().Be(40m,
            "an analysis ordered on its own keeps its own tariff");

        // The payer still reimburses each act on the nomenclature.
        inputs.Should().OnlyContain(i => i.Coefficient == 10m && i.LetterKeyValue == 1.10m);
    }

    [Fact]
    public void PackagePrice_WithUnevenShares_PutsTheRoundingOnTheLastMember()
    {
        var panel = Guid.NewGuid();
        var selected = new List<OrderComposer.SelectedAnalysis>
        {
            new(Analysis("A", 10m), panel, 100m),
            new(Analysis("B", 10m), panel, 100m),
            new(Analysis("C", 10m), panel, 100m)
        };

        var prices = OrderComposer.BuildPricingInputs(selected, null, Today)
                                  .Select(i => i.ChargedUnitPrice).ToList();

        prices.Should().Equal(33.33m, 33.33m, 33.34m);
    }

    [Fact]
    public void AnalysisWithoutTariff_IsRefused_UnlessAPackagePaysForIt()
    {
        var panel = Guid.NewGuid();
        var selected = new List<OrderComposer.SelectedAnalysis>
        {
            new(Analysis("NOPRICE", null), null),
            new(Analysis("INPANEL", null), panel, 150m),
            new(Analysis("PRICED", 50m), null)
        };

        var errors = OrderComposer.CheckTariffs(selected, null, Today);

        errors.Should().ContainSingle()
              .Which.ResourceKey.Should().Be("Order_AnalysisWithoutTariff");
        errors[0].Args.Should().Contain("NOPRICE");
    }

    [Fact]
    public void PayerWithoutItsOwnTariff_FallsBackToTheLaboratoryList()
    {
        var selected = new List<OrderComposer.SelectedAnalysis> { new(Analysis("GLY", 40m), null) };

        OrderComposer.CheckTariffs(selected, Guid.NewGuid(), Today).Should().BeEmpty();
    }
}
