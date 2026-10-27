using Bunit;
using FluentAssertions;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Reporting;
using MarocLIS.Web.Components.Screens.Samples;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>« Sous-traitance » in the browser: the stages, the scanner, the dispatch and its slip, the partner's reference and the return, by right.</summary>
public sealed class WebSubcontractingTests : WebBenchContext
{
    private readonly Fake<ISubcontractQueries> _queries = Fake<ISubcontractQueries>.Create();
    private readonly Guid _partner = Guid.NewGuid();
    private readonly Guid _lateOrder = Guid.NewGuid();
    private readonly List<SubcontractRow> _rows;

    private sealed class PdfStub : IListPdfRenderer
    {
        public List<ListDocument> Rendered { get; } = [];

        public byte[] Render(ListDocument document)
        {
            Rendered.Add(document);
            return "%PDF-1.7"u8.ToArray();
        }
    }

    private readonly PdfStub _pdf = new();

    public WebSubcontractingTests()
    {
        _rows =
        [
            Act("2610-000051", "T-0051", null, null),
            Act("2610-000052", "T-0052", null, null, OrderPriority.Urgent),
            Act("2610-000040", "T-0040", TestClock.Now.AddDays(-6), TestClock.Now.AddDays(-1), order: _lateOrder, reference: "PART-26-04512"),
            Act("2610-000044", "T-0044", TestClock.Now.AddHours(-20), TestClock.Now.AddDays(3), slip: "ST/2026/00002")
        ];

        _queries.On(nameof(ISubcontractQueries.GetWorklistAsync), _ => (IReadOnlyList<SubcontractRow>)[.. _rows])
                .On(nameof(ISubcontractQueries.GetPartnersAsync), _ => (IReadOnlyList<SubcontractPartner>)
                    [new SubcontractPartner(_partner, "BIOLAB", new MultilingualText("Biolab Rabat", "بيولاب الرباط", "Biolab Rabat"))])
                .On(nameof(ISubcontractQueries.GetSlipAsync), args => new SubcontractSlip((string)args[1]!,
                    new SubcontractPartner(_partner, "BIOLAB", new MultilingualText("Biolab Rabat")), TestClock.Now.AddDays(-6), "Karim Tahiri",
                    [.. _rows.Where(r => r.DispatchNumber == (string)args[1]!)]));

        Services.AddSingleton(_queries.Object);
        Services.AddSingleton<IListPdfRenderer>(_pdf);
        Services.AddScoped<SubcontractingService>();
    }

    private SubcontractRow Act(string accession, string barcode, DateTimeOffset? sent, DateTimeOffset? due,
                               OrderPriority priority = OrderPriority.Routine, Guid? order = null, string? reference = null,
                               string slip = "ST/2026/00001") =>
        new(Guid.NewGuid(), order ?? Guid.NewGuid(), accession, "BERRADA Nawal", Gender.Female, new DateOnly(1980, 3, 2), priority,
            "VITD", new MultilingualText("25-OH vitamine D", "فيتامين د 25-OH", "25-OH vitamin D"), barcode, new MultilingualText("Tube sec"),
            TestClock.Now.AddDays(-7), TestClock.Now.AddDays(-7), _partner, "BIOLAB", new MultilingualText("Biolab Rabat", "بيولاب الرباط", "Biolab Rabat"),
            sent, sent is null ? null : slip, reference, due);

    private static readonly string[] Sender = [Permissions.Samples.View, Permissions.Samples.Subcontract];

    [Fact]
    public void TheActs_AreListedByStage_TheLateOnesInRed_WithTheirCounts()
    {
        SignIn(AppLanguage.French, Sender);
        var page = Render<Subcontracting>();

        page.FindAll("[data-grid=subcontracting] tbody tr").Should().HaveCount(4);
        page.Find("[data-row='2610-000040-VITD']").ClassList.Should().Contain("is-tone-critical-fill");
        page.Find("[data-row='2610-000040-VITD']").TextContent.Should().Contain("! En retard").And.Contain("PART-26-04512");
        page.Find("[data-row='2610-000052-VITD']").ClassList.Should().Contain("is-tone-critical", "an urgent act to send");
        page.Find("[data-row='2610-000044-VITD']").TextContent.Should().Contain("◐ En attente de retour");
        page.Find("[data-state=count]").TextContent.Should().Be("2 à envoyer · 2 en attente de retour · 1 en retard");

        page.Find("[data-filter=late]").Click();
        page.FindAll("[data-grid=subcontracting] tbody tr").Should().ContainSingle();
    }

    [Fact]
    public void ATubeScanned_TicksItsActs_AndTheSendDialogListsThemWithThePartners()
    {
        SignIn(AppLanguage.French, Sender);
        var page = Render<Subcontracting>();

        foreach (var barcode in new[] { "T-0051", "t-0052" })
        {
            page.Find("[data-field=subcontract-search]").Input(barcode);
            page.Find("[data-field=subcontract-search]").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        }

        page.FindAll("[data-field=tick]").Count(t => t.HasAttribute("checked")).Should().Be(2);
        page.Find("[data-field=subcontract-search]").GetAttribute("value").Should().BeEmpty("the box is emptied for the next tube");

        page.Find(".action-bar [data-action='Subcontract.Send']").Click();
        var dialog = page.Find("[data-modal=dispatch]");
        dialog.TextContent.Should().Contain("2 analyses de 2 dossiers à envoyer")
              .And.Contain("2610-000051  ·  VITD  ·  T-0051  →  BIOLAB");
        page.FindAll("[data-field=dispatch-partner] option").Select(o => o.TextContent)
            .Should().Equal("Laboratoire prévu au catalogue pour chaque analyse", "BIOLAB  Biolab Rabat");

        // Nothing found to send by the service: said in the dialog, nothing printed.
        page.Find("[data-modal=dispatch] [data-action='Subcontract.SendAndPrint']").Click();
        page.Find("[data-modal=dispatch] [data-state=refusal]").TextContent.Should().Contain("Cochez au moins une analyse");
        _pdf.Rendered.Should().BeEmpty();
    }

    [Fact]
    public void ASlip_IsDownloadedAgain_AsTheWorkstationPrintsIt()
    {
        SignIn(AppLanguage.French, Sender);
        var page = Render<Subcontracting>();

        page.Find("[data-row='2610-000040-VITD']").Click();
        page.Find(".action-bar [data-action='Subcontract.ReprintSlip']").Click();

        JSInterop.Invocations.Should().Contain(i => i.Identifier == "marocLis.download"
                                                    && (string)i.Arguments[0]! == "bordereau-ST-2026-00001.pdf");
        _pdf.Rendered.Single().Title.Should().Be("Bordereau d'envoi en sous-traitance ST/2026/00001");
        _pdf.Rendered.Single().Rows.Should().HaveCount(3, "one analysis, a blank line and the signatures");
        page.Find("[data-state=status]").TextContent.Should().Contain("bordereau-ST-2026-00001.pdf");
    }

    [Fact]
    public void ThePartnersReference_IsAskedForAnActAwaitingItsReturn_AndTheReturnOpensTheResultEntry()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Samples.Subcontract, Permissions.Results.Enter);
        var page = Render<Subcontracting>();

        page.Find("[data-row='2610-000051-VITD']").Click();
        page.Find(".action-bar [data-action='Subcontract.PartnerReference']").HasAttribute("disabled").Should().BeTrue("an act not sent has no partner number");
        page.Find(".action-bar [data-action='Subcontract.EnterResults']").HasAttribute("disabled").Should().BeTrue();

        page.Find("[data-row='2610-000040-VITD']").Click();
        page.Find(".action-bar [data-action='Subcontract.PartnerReference']").Click();
        page.Find("[data-modal=partner-reference]").TextContent.Should().Contain("Numéro donné par Biolab Rabat au dossier 2610-000040");
        page.Find("[data-modal=partner-reference]").QuerySelector("input, textarea")!.GetAttribute("maxlength").Should().Be("60");
        page.Find("[data-modal=partner-reference] [data-action='Action.Cancel']").Click();

        page.Find(".action-bar [data-action='Subcontract.EnterResults']").Click();
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith($"ecran/entry?dossier={_lateOrder}");
    }

    [Fact]
    public void WithoutTheRightToSubcontract_SendingAndTheReferenceAreNotOffered_ButASlipStillDownloads()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View);
        var page = Render<Subcontracting>();

        page.FindAll("[data-action='Subcontract.Send']").Should().BeEmpty();
        page.FindAll("[data-action='Subcontract.PartnerReference']").Should().BeEmpty();
        page.FindAll("[data-action='Subcontract.EnterResults']").Should().BeEmpty();
        page.Find("[data-row='2610-000044-VITD']").Click();
        page.Find("[data-action='Subcontract.ReprintSlip']").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void InArabic_TheStagesAndThePartnerSpeakArabic()
    {
        SignIn(AppLanguage.Arabic, Sender);
        var page = Render<Subcontracting>();

        page.Find("[data-row='2610-000040-VITD']").TextContent.Should().Contain("متأخرة").And.Contain("بيولاب الرباط");
        page.Find("[data-action='Subcontract.Send']").TextContent.Should().Contain("إرسال إلى المختبر الشريك");
        page.Markup.Should().NotContain("[[");
    }
}
