using System.Globalization;
using FluentAssertions;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Localization;
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

    /// <summary>
    /// The registered order's message fits the two lines the action bar gives it at 1366 px: on the
    /// web it ran « … Étiquettes à imprimer depuis le poste Windows du laboratoire (le navigateur
    /// n'accède pas… » and was cut; the reason stays in the summary that opens with it.
    /// </summary>
    [Fact]
    public void The_registered_order_s_message_says_where_the_labels_are_printed_in_two_lines_at_most()
    {
        var localizer = new JsonLocalizer(new LocalizationOptions { MarkMissingKeys = true });

        foreach (var language in new[] { AppLanguage.French, AppLanguage.English, AppLanguage.Arabic })
        {
            string Text(string key, params object?[] args) => localizer.Format(language, key, args);

            var web = OrderRegistrationRules.RegisteredStatus(Text, "2610-000015", 3, printLabels: true, labelsFromWorkstation: true);
            web.Should().Contain("2610-000015").And.Contain(localizer.Format(language, "OrderEntry.LabelsAtWorkstation"));
            web.Length.Should().BeLessThanOrEqualTo(90, $"two lines of the bar in {language}: « {web} »");
            OrderRegistrationRules.RegisteredStatus(Text, "2610-000015", 3, printLabels: false, labelsFromWorkstation: true)
                .Should().EndWith(localizer.Format(language, "OrderEntry.LabelsNotPrinted"));
            OrderRegistrationRules.RegisteredStatus(Text, "2610-000015", 3, printLabels: true, labelsFromWorkstation: false)
                .Should().Be($"{Text("Order.AccessionNumber")} 2610-000015 — {Text("OrderEntry.SampleCount", 3)}", "the workstation prints them: nothing more to say");
        }

        OrderRegistrationRules.RegisteredStatus((key, args) => localizer.Format(AppLanguage.French, key, args), "2610-000015", 1,
                                                printLabels: true, labelsFromWorkstation: true)
            .Should().Be("N° de dossier 2610-000015 — 1 échantillon — Étiquettes à imprimer au poste Windows");
    }

    /// <summary>
    /// « Dossier enregistré … Règlement : 27,00 DH » made the secretary believe a payment was
    /// recorded; the registration takes none. The line says what is to be collected.
    /// </summary>
    [Fact]
    public void The_registered_order_s_breakdown_says_what_is_to_collect_not_a_payment()
    {
        var localizer = new JsonLocalizer(new LocalizationOptions { MarkMissingKeys = true });
        var pricing = new Application.Pricing.OrderPricing
        {
            Acts = [],
            Gross = Money.Of(40m),
            Total = Money.Of(40m),
            PayerShare = Money.Of(13m),
            PatientShare = Money.Of(27m),
            DueAtCounter = Money.Of(27m)
        };

        foreach (var language in new[] { AppLanguage.French, AppLanguage.English, AppLanguage.Arabic })
        {
            string Text(string key, params object?[] args) => localizer.Format(language, key, args);

            var lines = OrderRegistrationRules.PricingLines(Text, "2610-000014", pricing, m => string.Create(CultureInfo.InvariantCulture, $"{m.Amount:0.00} DH"));

            lines.Should().NotContain(l => l.Contains("[[", StringComparison.Ordinal), $"every key exists in {language}");
            lines[^1].Should().Be(Text("OrderEntry.ToCollect", "27.00 DH"));
            var payment = Text("Payment.Title");
            lines.Should().NotContain(l => l.StartsWith(payment, StringComparison.Ordinal));
        }

        var french = OrderRegistrationRules.PricingLines((k, a) => localizer.Format(AppLanguage.French, k, a), "2610-000014",
                                                         pricing, m => string.Create(CultureInfo.InvariantCulture, $"{m.Amount:0.00} DH"));
        french[^1].Should().Be("À encaisser au guichet : 27.00 DH — aucun règlement n'est encore enregistré");

        var free = pricing with { DueAtCounter = Money.Zero };
        OrderRegistrationRules.PricingLines((k, a) => localizer.Format(AppLanguage.French, k, a), "2610-000014", free,
                                            m => string.Create(CultureInfo.InvariantCulture, $"{m.Amount:0.00} DH"))[^1]
            .Should().Be("Rien à encaisser au guichet");
    }
}
