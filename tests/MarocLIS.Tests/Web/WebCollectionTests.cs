using Bunit;
using FluentAssertions;
using MarocLIS.Application.Licensing;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Web.Components.Pages;
using MarocLIS.Web.Components.Screens.Samples;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static MarocLIS.Tests.Licensing.LicenceTestKit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Prélèvements » in the browser: the patients to draw, the tubes ticked by CollectionRules, the
/// scanner in the scan box, the fasting question before the draw, the labels left to the
/// workstation — and a préleveur who types the address of another screen.
/// </summary>
public sealed class WebCollectionTests : WebBenchContext
{
    private static readonly string[] Collector = [Permissions.Samples.View, Permissions.Samples.Collect];

    private readonly Guid _order = Guid.NewGuid();

    public WebCollectionTests()
    {
        Services.AddSingleton<ILicenceState>(new FixedLicenceState(FullStatus()));
        SampleQueries.On(nameof(ISampleQueries.GetCollectionWorklistAsync), _ => (IReadOnlyList<CollectionWorklistEntry>)
        [
            Tube(1, "T-2610-0001", fasting: 12),
            Tube(2, "T-2610-0002"),
            new()
            {
                SampleId = Guid.NewGuid(), LabOrderId = Guid.NewGuid(), Barcode = "T-2610-0009", AccessionNumber = "2610-000009",
                PatientName = "CHERKAOUI Imane", SpecimenTypeName = new MultilingualText("Sang"), RegisteredAt = TestClock.Now,
                DrawOrder = 1, AnalysisCodes = ["TSH"]
            }
        ]);
    }

    private CollectionWorklistEntry Tube(int draw, string barcode, int fasting = 0) => new()
    {
        SampleId = Guid.NewGuid(),
        LabOrderId = _order,
        Barcode = barcode,
        AccessionNumber = "2610-000008",
        PatientName = "AMRANI Youssef",
        SpecimenTypeName = new MultilingualText("Sang"),
        Priority = OrderPriority.Urgent,
        RegisteredAt = TestClock.Now.AddMinutes(-20),
        DrawOrder = draw,
        RequiredFastingHours = fasting,
        AnalysisCodes = ["GLY"]
    };

    [Fact]
    public void ThePatientsToDraw_AreListed_UrgentFirst_WithTheirTubesTicked()
    {
        SignIn(AppLanguage.French, Collector);

        var page = Render<Collection>();

        page.FindAll("[data-list=patients] li").Select(l => l.GetAttribute("data-order")).Should().Equal("2610-000008", "2610-000009");
        page.FindAll("[data-grid=tubes] [data-field=tick]").Should().HaveCount(2).And.OnlyContain(t => t.HasAttribute("checked"));
        page.Find("[data-state=fasting]").Should().NotBeNull();
        page.Find("[data-action='Collection.ValidateDraw']").GetAttribute("data-shortcut").Should().Be("F12");
    }

    [Fact]
    public void AScannedTube_EndingWithEnter_BringsItsPatientUp_AndTicksIt()
    {
        SignIn(AppLanguage.French, Collector);
        var page = Render<Collection>();

        var scan = page.Find("[data-field=scan]");
        scan.Input("T-2610-0009");
        scan.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        page.Find("[data-list=patients] li.is-selected").GetAttribute("data-order").Should().Be("2610-000009");
        page.Find("[data-grid=tubes] tr[data-tube='T-2610-0009'] [data-field=tick]").HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public void ADrawNeedingFasting_AsksFirst_AndRecordsNothingYet()
    {
        SignIn(AppLanguage.French, Collector);
        var page = Render<Collection>();

        page.Find("[data-action='Collection.ValidateDraw']").Click();

        page.Find("[data-modal=confirm]").TextContent.Should().Contain("12");
        SampleStore.Calls.Should().BeEmpty();
    }

    [Fact]
    public void TheLabels_AreLeftToTheWorkstation_AndNothingIsRecorded()
    {
        SignIn(AppLanguage.French, Collector.Append(Permissions.Samples.Reprint).ToArray());
        var page = Render<Collection>();

        page.Find("[data-action='Action.PrintLabels']").Click();
        page.Find("[data-modal=collection-message]").TextContent.Should().Contain("poste Windows");
        SampleStore.Calls.Should().BeEmpty();
    }

    [Fact]
    public void WithoutTheRightToDraw_NothingIsOffered()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View);

        var page = Render<Collection>();

        page.Find("[data-action='Collection.ValidateDraw']").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-action='Collection.DispatchToBench']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void APreleveur_TypingTheAddressOfEntryOrValidation_IsRefused_ButCollectionOpens()
    {
        SignIn(AppLanguage.French, Collector);

        foreach (var key in new[] { "entry", "validation", "reception" })
        {
            var refused = Render<Screen>(p => p.Add(x => x.Key, key));
            refused.Find("[data-state=refused]").TextContent.Should().Contain("Vous n'avez pas le droit", key);
            refused.FindAll("[data-screen]").Should().BeEmpty(key);
        }

        Render<Screen>(p => p.Add(x => x.Key, "collection")).Find("[data-screen=collection]").Should().NotBeNull();
    }

    [Fact]
    public void TheScreen_IsInArabic_ForAnArabicSession()
    {
        SignIn(AppLanguage.Arabic, Collector);

        Render<Collection>().Find("[data-action='Collection.ValidateDraw']").TextContent.Should().MatchRegex("[؀-ۿ]");
    }
}
