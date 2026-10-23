using FluentAssertions;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>The rules of the "Nouvelle demande" screen that do not need a window.</summary>
public sealed class OrderRegistrationScreenTests
{
    private static CatalogPickerItem Item(string code, bool panel = false, params Guid[] members) =>
        new(Guid.NewGuid(), panel, code, new MultilingualText(code), null, null, 10m, null, 4, true,
            ConsentRequirement.None, false, null, null, members.Length,
            [.. members.Select(m => new PickerComponent(m, 5m))]);

    [Fact]
    public void Enter_TakesTheExactCode_ThenACodePrefix_ThenTheFirstMatch()
    {
        var glyquee = Item("HBA1C");
        var glycemie = Item("GLY");
        var bilan = Item("GLYBILAN", panel: true);

        // The server's order: panels first, then by display order.
        IReadOnlyList<CatalogPickerItem> found = [bilan, glyquee, glycemie];

        OrderRegistrationRules.BestMatch(found, "gly").Should().Be(glycemie);
        OrderRegistrationRules.BestMatch(found, "glyb").Should().Be(bilan);
        OrderRegistrationRules.BestMatch(found, "glyquée").Should().Be(bilan);
        OrderRegistrationRules.BestMatch([], "gly").Should().BeNull();
        OrderRegistrationRules.Ranked(found, "gly").Should().Equal(glycemie, bilan, glyquee);
    }

    [Fact]
    public void RepeatReminder_FindsTheLatestOfTheAnalysisOrOfAPanelsMembers()
    {
        var hba1c = Guid.NewGuid();
        var creat = Guid.NewGuid();
        var now = DateTimeOffset.Now;

        IReadOnlyList<RecentAnalysis> recent =
        [
            new(hba1c, now.AddDays(-3), "2610-000015"),
            new(hba1c, now.AddDays(-20), "2609-000002"),
            new(creat, now.AddDays(-1), "2610-000020")
        ];

        var single = OrderRegistrationRules.RepeatOf(hba1c, false, [], recent);
        single!.Value.Previous.AccessionNumber.Should().Be("2610-000015");

        var panel = OrderRegistrationRules.RepeatOf(Guid.NewGuid(), true, [hba1c, creat], recent);
        panel!.Value.AnalysisId.Should().Be(creat);

        OrderRegistrationRules.RepeatOf(Guid.NewGuid(), false, [], recent).Should().BeNull();
    }

    [Fact]
    public void Initials_AreTheFirstLettersOfTheFirstTwoWords()
    {
        OrderRegistrationRules.Initials("ALAOUI Mohammed").Should().Be("AM");
        OrderRegistrationRules.Initials("X-URG  0001").Should().Be("X0");
        OrderRegistrationRules.Initials("BENNANI").Should().Be("B");
    }

    [Fact]
    public void Tubes_AreGroupedByContainer_AsRegistrationCreatesThem()
    {
        var specimen = Guid.NewGuid();
        var edta = Guid.NewGuid();

        AnalysisTest Test(string code, Guid? container) =>
            new(code, new MultilingualText(code), Guid.NewGuid(), specimen) { ContainerTypeId = container };

        var groups = OrderComposer.GroupByTube(
        [
            new(Test("POT", null), null),
            new(Test("NFS", edta), null),
            new(Test("HBA1C", edta), null)
        ]);

        // Containers known first, the one without a container last.
        groups.Should().HaveCount(2);
        groups[0].Select(a => a.Test.Code).Should().Equal("NFS", "HBA1C");
        groups[1].Single().Test.Code.Should().Be("POT");
    }

    [Fact]
    public void Save_PrintsTheLabelsAsBefore_UnlessSavedWithoutLabels()
    {
        OrderRegistrationRules.PrintsLabels(withoutLabels: false).Should().BeTrue();
        OrderRegistrationRules.PrintsLabels(withoutLabels: true).Should().BeFalse();
    }
}
