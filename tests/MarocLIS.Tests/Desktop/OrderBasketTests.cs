using FluentAssertions;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The request the « Nouvelle demande » counter composes, moved out of the workstation's screen so
/// the web composes it the same way.
/// </summary>
public sealed class OrderBasketTests
{
    private static CatalogPickerItem Item(string code, bool panel = false, int? fasting = null,
                                          ConsentRequirement consent = ConsentRequirement.None,
                                          string? department = null, params Guid[] members) =>
        new(Guid.NewGuid(), panel, code, new MultilingualText(code), department, null, 10m, fasting, 4, true,
            consent, false, null, null, members.Length,
            [.. members.Select(m => new PickerComponent(m, 5m))]);

    private static CatalogPickerItem Analysis(Guid id, string code) =>
        new(id, false, code, new MultilingualText(code), "BIO", null, 10m, null, 4, true,
            ConsentRequirement.None, false, null, null, 0);

    [Fact]
    public void An_analysis_goes_in_once()
    {
        var basket = new OrderBasket();
        var glycemie = Item("GLY");

        basket.Add(glycemie).Outcome.Should().Be(BasketAddOutcome.Added);
        basket.Add(glycemie).Outcome.Should().Be(BasketAddOutcome.AlreadyPresent);
        basket.Count.Should().Be(1);
        basket.StateOf(glycemie).Should().Be(CatalogItemState.InRequest);
    }

    [Fact]
    public void A_panel_refuses_its_member_and_takes_out_the_members_chosen_alone()
    {
        var creat = Analysis(Guid.NewGuid(), "CREA");
        var uree = Analysis(Guid.NewGuid(), "UREE");
        var renal = Item("BILREN", panel: true, members: [creat.Id, uree.Id]);
        var basket = new OrderBasket();

        basket.Add(creat);
        basket.ChosenMembersOf(renal).Should().Equal("CREA");

        var merged = basket.Add(renal);
        merged.Outcome.Should().Be(BasketAddOutcome.MergedIntoPanel);
        merged.Absorbed.Should().Equal(creat);
        basket.Items.Should().Equal(renal);

        var refused = basket.Add(uree);
        refused.Outcome.Should().Be(BasketAddOutcome.AlreadyInPanel);
        refused.CoveringPanel.Should().Be(renal);
        refused.WentIn.Should().BeFalse();
        basket.StateOf(uree).Should().Be(CatalogItemState.InPanel);

        basket.AnalysisTestIds.Should().BeEmpty();
        basket.TestPanelIds.Should().Equal(renal.Id);
    }

    [Fact]
    public void Shared_members_of_two_panels_are_counted_on_the_second()
    {
        var shared = Guid.NewGuid();
        var first = Item("P1", panel: true, members: [shared, Guid.NewGuid()]);
        var second = Item("P2", panel: true, members: [shared]);
        var basket = new OrderBasket();

        basket.Add(first);
        basket.Add(second);

        basket.SharedCount(first).Should().Be(0);
        basket.SharedCount(second).Should().Be(1);
    }

    [Fact]
    public void A_repeat_is_reminded_until_kept_and_forgotten_with_the_patient()
    {
        var hba1c = Analysis(Guid.NewGuid(), "HBA1C");
        var panel = Item("DIAB", panel: true, members: [hba1c.Id]);
        var basket = new OrderBasket();
        basket.SetRecent([new RecentAnalysis(hba1c.Id, DateTimeOffset.Now.AddDays(-4), "2610-000015")]);

        basket.Add(panel);
        var repeat = basket.RepeatOf(panel, [hba1c, panel]);
        repeat!.MemberCode.Should().Be("HBA1C");
        repeat.Previous.AccessionNumber.Should().Be("2610-000015");

        basket.Keep(panel);
        basket.RepeatOf(panel, [hba1c, panel]).Should().BeNull();

        basket.ForgetPatient();
        basket.IsKept(panel).Should().BeFalse();
        basket.RepeatOf(panel, [hba1c, panel]).Should().BeNull("the next patient has no history yet");
    }

    [Fact]
    public void The_readiness_names_what_is_missing_and_the_longest_fast()
    {
        OrderBasket.MissingKeys(false, false, true).Should()
            .Equal("OrderEntry.MissingPatient", "OrderEntry.MissingAnalyses", "OrderEntry.MissingPrescriber");
        OrderBasket.MissingKeys(true, true, false).Should().BeEmpty();

        OrderBasket.PrescriberMissing(false, " ", OrderOrigin.Walkin).Should().BeTrue();
        OrderBasket.PrescriberMissing(false, "Dr Alami", OrderOrigin.Walkin).Should().BeFalse();
        OrderBasket.PrescriberMissing(false, null, OrderOrigin.PatientRequest).Should().BeFalse();
        OrderBasket.PrescriberMissing(true, null, OrderOrigin.Walkin).Should().BeFalse();

        var basket = new OrderBasket();
        basket.Add(Item("GLY", fasting: 8));
        basket.Add(Item("HIV", consent: ConsentRequirement.Hiv));
        basket.MaxFastingHours.Should().Be(8);
        basket.RequiresConsent.Should().BeTrue();
    }

    [Fact]
    public void The_filter_chips_the_prescriber_and_the_entries()
    {
        var bio = Item("GLY", department: "BIO");
        var hema = Item("NFS", department: "HEMA");
        var panel = Item("BILAN", panel: true);
        CatalogPickerItem[] all = [bio, hema, panel];

        OrderBasket.Filter(all, OrderBasket.FilterAll).Should().Equal(all);
        OrderBasket.Filter(all, OrderBasket.FilterPanels).Should().Equal(panel);
        OrderBasket.Filter(all, "HEMA").Should().Equal(hema);

        string[] doctors = ["Dr Alami — Cardiologie", "Dr Bennani"];
        OrderBasket.MatchPrescriber(doctors, d => d, " dr bennani ").Should().Be("Dr Bennani");
        OrderBasket.MatchPrescriber(doctors, d => d, "Dr Tazi").Should().BeNull();

        OrderBasket.HasEntries(false, 0, "", "  ").Should().BeFalse();
        OrderBasket.HasEntries(false, 0, "fièvre", null).Should().BeTrue();
        OrderBasket.LabelCopies(0).Should().Be(1);
        OrderBasket.LabelCopies(9).Should().Be(5);
    }

    [Fact]
    public void Removing_a_line_forgets_that_it_was_kept()
    {
        var gly = Item("GLY");
        var basket = new OrderBasket();
        basket.Add(gly);
        basket.Keep(gly);

        basket.Remove(gly);

        basket.IsEmpty.Should().BeTrue();
        basket.IsKept(gly).Should().BeFalse();
    }
}
