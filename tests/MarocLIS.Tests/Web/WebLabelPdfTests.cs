using Bunit;
using FluentAssertions;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Samples;
using MarocLIS.Reporting;
using MarocLIS.Web.Components.Screens.Orders;
using MarocLIS.Web.Components.Screens.Samples;
using MarocLIS.Web.Components.Shared;
using MarocLIS.Web.Printing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MarocLIS.Application.Licensing;
using static MarocLIS.Tests.Licensing.LicenceTestKit;
using static MarocLIS.Tests.Desktop.OrderFileRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// The tube labels from the browser: the workstation's 50 × 25 mm label PDF (SampleLabelRenderer),
/// downloaded with « Étiquettes (PDF) » for the operator to print on the label printer at 100 %. A
/// reprint goes through the workstation's SampleWorkflowService.ReprintLabelsAsync — its right
/// (Samples.Reprint), its reason and its trace — and the labels of a save need the save's right.
/// </summary>
public sealed class WebLabelPdfTests : WebBenchContext
{
    private readonly Guid _order = Guid.NewGuid();

    public WebLabelPdfTests()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        Services.AddSingleton<ILicenceState>(new FixedLicenceState(FullStatus()));
        SampleQueries.On(nameof(ISampleQueries.GetLabelsForOrderAsync),
                         args => (Guid)args[0]! == _order ? (IReadOnlyList<SampleLabel>)[Label(1), Label(2)] : []);
    }

    private static SampleLabel Label(int sequence, string accession = "2610-000008") =>
        new(Guid.NewGuid(), $"T-2610-000{sequence}", "AMRANI Youssef", "عمراني يوسف", new DateOnly(1980, 1, 2), accession,
            "Tube sec", "#E74C3C", sequence, 2, ["GLY"], false, new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.FromHours(1)));

    /// <summary>The service as a screen reaches it: on a scope of its own, for the signed-in operator.</summary>
    private Task<Result<LabelPdf>> Run(Func<LabelPdfService, Task<Result<LabelPdf>>> work) =>
        Services.GetRequiredService<MarocLIS.Web.Session.UserScopes>().RunAsync(work);

    private Result<LabelPdf> Run(Func<LabelPdfService, Result<LabelPdf>> work) =>
        Run(s => Task.FromResult(work(s))).GetAwaiter().GetResult();

    // ---- The service ---------------------------------------------------------------------------------

    [Fact]
    public async Task AReprint_ReturnsTheWorkstationsLabels_AsOnePdfNamedAfterTheFile_AndIsRecorded()
    {
        SignIn(AppLanguage.French, Permissions.Samples.Reprint);

        var pdf = await Run(s => s.ReprintAsync(_order, "Étiquette abîmée", default));

        pdf.Errors.Should().BeEmpty();
        pdf.Value.FileName.Should().Be("etiquettes-2610-000008.pdf");
        pdf.Value.Count.Should().Be(2);
        System.Text.Encoding.ASCII.GetString(pdf.Value.Content, 0, 5).Should().Be("%PDF-");
        SampleQueries.CallsTo(nameof(ISampleQueries.GetLabelsForOrderAsync)).Should().Be(1, "the workstation's query gives the labels");
        Work.Calls.Should().Contain(c => c.Method == "set_AuditReason" && ((string)c.Arguments[0]!).Contains("Étiquette abîmée"),
                                    "a reprint is recorded with its reason, as at the workstation");
        Work.CallsTo(nameof(Application.Abstractions.IUnitOfWork.SaveChangesAsync)).Should().Be(1);
    }

    [Fact]
    public async Task AReprint_WithoutTheRight_IsRefused_AndNothingIsRenderedOrRecorded()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Orders.View);

        var pdf = await Run(s => s.ReprintAsync(_order, "Étiquette abîmée", default));

        pdf.IsFailure.Should().BeTrue();
        pdf.FirstError.Code.Should().Be("forbidden");
        SampleQueries.CallsTo(nameof(ISampleQueries.GetLabelsForOrderAsync)).Should().Be(0);
        Work.CallsTo(nameof(Application.Abstractions.IUnitOfWork.SaveChangesAsync)).Should().Be(0);
    }

    [Fact]
    public void TheLabelsOfASave_NeedTheSavesRight()
    {
        SignIn(AppLanguage.French, Permissions.Orders.View, Permissions.Samples.Reprint);

        foreach (var source in Enum.GetValues<NewLabels>())
            Run(s => s.ForNewTubes(source, [Label(1), Label(2)])).FirstError.Code.Should().Be("forbidden", source.ToString());

        LabelPdfService.RightFor(NewLabels.Registration).Should().Be(Permissions.Orders.Create);
        LabelPdfService.RightFor(NewLabels.AddedAnalyses).Should().Be(Permissions.Orders.Edit);
        LabelPdfService.RightFor(NewLabels.Replacement).Should().Be(Permissions.Samples.Reject);
    }

    [Fact]
    public void TheLabelsOfARegistration_AreOnePdfNamedAfterTheFile()
    {
        SignIn(AppLanguage.French, Permissions.Orders.Create);

        var pdf = Run(s => s.ForNewTubes(NewLabels.Registration, [Label(1), Label(2)]));

        pdf.Errors.Should().BeEmpty();
        pdf.Value.FileName.Should().Be("etiquettes-2610-000008.pdf");
        pdf.Value.Count.Should().Be(2);
        LabelPdfService.FileName(" 26/10 000008 ").Should().Be("etiquettes-26-10-000008.pdf");
    }

    // ---- The button after a save ---------------------------------------------------------------------

    [Fact]
    public void AfterASave_TheButtonDownloadsTheLabels_Once()
    {
        SignIn(AppLanguage.French, Permissions.Orders.Create);

        var button = Render<LabelsPdfButton>(p => p.Add(x => x.Source, NewLabels.Registration)
                                                     .Add(x => x.Labels, (IReadOnlyList<SampleLabel>)[Label(1), Label(2)]));

        var action = button.Find("[data-action='Action.LabelsPdf']");
        action.TextContent.Should().Contain("Étiquettes (PDF)");
        action.QuerySelector(".icon").Should().NotBeNull("the button keeps the labels' icon");
        action.Click();

        button.FindAll("[data-state=labels-failure]").Select(f => f.TextContent).Should().BeEmpty();
        var download = JSInterop.VerifyInvoke("marocLis.download");
        download.Arguments[0].Should().Be("etiquettes-2610-000008.pdf");
        download.Arguments[1].Should().Be("application/pdf");
        button.Find("[data-action='Action.LabelsPdf']").HasAttribute("disabled")
              .Should().BeTrue("a second copy is a reprint, with its reason, from the file");
    }

    [Fact]
    public void AfterASave_WithoutTheSavesRight_NoButtonIsShown()
    {
        SignIn(AppLanguage.French, Permissions.Orders.View);

        Render<LabelsPdfButton>(p => p.Add(x => x.Source, NewLabels.Registration)
                                       .Add(x => x.Labels, (IReadOnlyList<SampleLabel>)[Label(1)]))
            .FindAll("[data-action='Action.LabelsPdf']").Should().BeEmpty();
    }

    [Fact]
    public void TheButton_IsInArabic_ForAnArabicSession()
    {
        SignIn(AppLanguage.Arabic, Permissions.Orders.Create);

        Render<LabelsPdfButton>(p => p.Add(x => x.Source, NewLabels.Registration)
                                       .Add(x => x.Labels, (IReadOnlyList<SampleLabel>)[Label(1)]))
            .Find("[data-action='Action.LabelsPdf']").TextContent.Should().MatchRegex("[؀-ۿ]").And.Contain("PDF");
    }

    // ---- The reprint of the file and of the collection -----------------------------------------------

    private IRenderedComponent<OrderFile> OpenFile()
    {
        var file = Detail(items: [Item(LabOrderItemStatus.Pending, code: "GLY")], samples: [Sample(SampleStatus.Collected)])
                   with { OrderId = _order, AccessionNumber = "2610-000008" };
        Orders.On(nameof(IOrderQueries.SearchAsync), _ => (IReadOnlyList<OrderListRow>)[])
              .On(nameof(IOrderQueries.GetDetailAsync), _ => file);
        return Render<OrderFile>(p => p.Add(x => x.OrderId, _order));
    }

    [Fact]
    public void TheFile_OffersTheLabelsPdf_WithTheReprintRight_AskingTheReason_ThenDownloads()
    {
        SignIn(AppLanguage.French, Permissions.Orders.View, Permissions.Samples.Reprint);
        var page = OpenFile();

        page.FindAll("[data-action='Action.PrintLabels']").Should().BeEmpty("the workstation's message is gone");
        page.Find("[data-action='Action.LabelsPdf']").Click();
        page.Find("[data-modal=reason] [data-field=reason]").Input("Étiquette abîmée");
        page.Find("[data-modal=reason] [data-action='Action.Ok']").Click();

        page.WaitForAssertion(() => JSInterop.VerifyInvoke("marocLis.download").Arguments[0].Should().Be("etiquettes-2610-000008.pdf"));
        page.Find("[data-modal=file-message]").TextContent.Should().Contain("100 %").And.Contain("taille réelle");
        Work.Calls.Should().Contain(c => c.Method == "set_AuditReason");
    }

    [Fact]
    public void TheFile_WithoutTheReprintRight_ShowsNoLabelsButton()
    {
        SignIn(AppLanguage.French, Permissions.Orders.View);

        OpenFile().FindAll("[data-action='Action.LabelsPdf']").Should().BeEmpty();
    }

    private void ServeOneTube() =>
        SampleQueries.On(nameof(ISampleQueries.GetCollectionWorklistAsync), _ => (IReadOnlyList<CollectionWorklistEntry>)
        [
            new()
            {
                SampleId = Guid.NewGuid(), LabOrderId = _order, Barcode = "T-2610-0001", AccessionNumber = "2610-000008",
                PatientName = "AMRANI Youssef", SpecimenTypeName = new MultilingualText("Sang"), RegisteredAt = TestClock.Now,
                DrawOrder = 1, AnalysisCodes = ["GLY"]
            }
        ]);

    [Fact]
    public void TheCollection_WithoutTheReprintRight_ShowsNoLabelsButton()
    {
        ServeOneTube();
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Samples.Collect);

        Render<Collection>().FindAll("[data-action='Action.LabelsPdf']").Should().BeEmpty();
    }

    [Fact]
    public void TheCollection_OffersTheLabelsPdf_InArabic_OnTheWorkstationsKey()
    {
        ServeOneTube();
        SignIn(AppLanguage.Arabic, Permissions.Samples.View, Permissions.Samples.Collect, Permissions.Samples.Reprint);

        var button = Render<Collection>().Find("[data-action='Action.LabelsPdf']");

        button.GetAttribute("data-shortcut").Should().Be("F8", "the workstation's key for the labels");
        button.TextContent.Should().MatchRegex("[؀-ۿ]").And.Contain("PDF");
    }
}
