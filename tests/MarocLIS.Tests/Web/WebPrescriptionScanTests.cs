using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Tests.Domain;
using MarocLIS.Web.Components.Screens.Orders;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;
using static MarocLIS.Tests.Desktop.OrderFileRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// The scanned prescription in the browser: « Joindre l'ordonnance » and « Photographier
/// l'ordonnance » offered to whom may change the file, a file chosen and attached as one PDF, the
/// PDF shown in « Dossier » and offered to download, and the same on « Nouvelle demande » before the
/// request exists.
/// </summary>
public sealed class WebPrescriptionScanTests : WebScreenContext
{
    private readonly LabOrder _order = new("2610-000042", Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now, Guid.NewGuid());
    private readonly Fake<IUnitOfWork> _unitOfWork = Fake<IUnitOfWork>.Create();

    public WebPrescriptionScanTests()
    {
        var orders = Fake<IRepository<LabOrder>>.Create()
            .On(nameof(IRepository<LabOrder>.GetByIdAsync), args => (Guid)args[0]! == _order.Id ? _order : null);
        Services.AddScoped(sp => new PrescriptionScanService(orders.Object, PrescriptionFiles.Composer(), _unitOfWork.Object,
                                                             sp.GetRequiredService<IPermissionGuard>(), DataAudit));

        var file = Detail(items: [Item(LabOrderItemStatus.Pending, code: "GLY")]) with { OrderId = _order.Id };
        IReadOnlyList<OrderListRow> rows =
            [new(file.OrderId, file.AccessionNumber, file.PatientName, TestClock.Now.AddDays(-1), file.Status, file.Priority, 1)];
        Orders.On(nameof(IOrderQueries.SearchAsync), _ => rows)
              .On(nameof(IOrderQueries.GetDetailAsync), args => (Guid)args[0]! == _order.Id
                  ? file with { HasPrescriptionScan = _order.PrescriptionScan is not null }
                  : null);
    }

    private IRenderedComponent<OrderFile> OpenFile()
    {
        var page = Render<OrderFile>(p => p.Add(x => x.OrderId, _order.Id));
        page.Find("[data-tab=Prescription]").Click();
        return page;
    }

    private static InputFileContent Photo(string name = "ordonnance.jpg") =>
        InputFileContent.CreateFromBinary(PrescriptionFiles.Photo(1200, 1600), name, contentType: "image/jpeg");

    // ---- Dossier -----------------------------------------------------------------------------------

    [Fact]
    public void WithOnlyTheRightToSeeTheFile_NothingCanBeAttached()
    {
        SignIn(AppLanguage.French, Permissions.Orders.View);

        var page = OpenFile();

        page.Find("[data-state=prescription]").TextContent.Should().Contain("Aucune ordonnance jointe");
        page.FindAll("[data-action='Order.AttachPrescription']").Should().BeEmpty();
        page.FindAll("[data-action='Order.PhotographPrescription']").Should().BeEmpty();
        page.FindAll("[data-action='Order.RemovePrescription']").Should().BeEmpty();
    }

    [Fact]
    public void WithTheRightToEdit_ThePhotoIsAttached_ShownAndTraced()
    {
        SignIn(AppLanguage.French, Permissions.Orders.View, Permissions.Orders.Edit);
        var page = OpenFile();

        var files = page.Find("[data-input=prescription-files]");
        files.GetAttribute("accept").Should().Be("application/pdf,image/jpeg,image/png");
        files.HasAttribute("multiple").Should().BeTrue("several files are several pages");
        page.Find("[data-input=prescription-camera]").GetAttribute("capture").Should().Be("environment",
            "a phone or a tablet offers its camera");

        page.FindComponents<InputFile>()[0].UploadFiles(Photo("recto.jpg"), Photo("verso.jpg"));

        page.WaitForAssertion(() =>
            page.Find("[data-state=prescription]").TextContent.Should().Contain("Ordonnance jointe").And.Contain("2 pages"));
        _order.PrescriptionScanContentType.Should().Be("application/pdf");
        PdfPageCounter.Count(_order.PrescriptionScan!).Should().Be(2);
        _unitOfWork.Calls.Should().Contain(c => c.Method == "set_AuditReason"
                                                && ((string)c.Arguments[0]!).StartsWith("Ordonnance numérisée jointe au dossier"));
        page.Find("[data-state=status]").TextContent.Should().Contain("Ordonnance enregistrée");

        // Shown in the page, from the PDF read for it, and the reading is a consultation of the file.
        page.Find("iframe[data-preview=prescription]");
        JSInterop.VerifyInvoke("marocLis.preview");
        DataAudit.Entries.Should().Contain(e => e.Kind == DataAccessKind.ViewOrder && e.Context == "PrescriptionScan");

        page.Find("[data-action='Order.ViewPrescription']").Click();
        JSInterop.VerifyInvoke("marocLis.download").Arguments[0].Should().Be("Ordonnance-2610-000042.pdf");

        page.FindAll("[data-action='Order.AddPrescriptionPages']").Should().ContainSingle();
        page.Find("[data-action='Order.AttachPrescription']").TextContent.Should().Contain("Remplacer");
    }

    [Fact]
    public void AFileThatIsNotAPrescription_IsRefusedByItsBytes()
    {
        SignIn(AppLanguage.French, Permissions.Orders.View, Permissions.Orders.Edit);
        var page = OpenFile();

        page.FindComponents<InputFile>()[0].UploadFiles(
            InputFileContent.CreateFromBinary(PrescriptionFiles.Program(), "ordonnance.pdf", contentType: "application/pdf"));

        page.WaitForAssertion(() => page.Find("[data-modal=file-message]").TextContent
                                        .Should().Contain("ordonnance.pdf").And.Contain("ni un PDF"));
        _order.PrescriptionScan.Should().BeNull();
    }

    [Fact]
    public void Removing_AsksFirst_ThenClearsIt()
    {
        _order.PrescriptionScan = PrescriptionFiles.Pdf(1);
        _order.PrescriptionScanContentType = "application/pdf";
        SignIn(AppLanguage.French, Permissions.Orders.View, Permissions.Orders.Edit);
        var page = OpenFile();

        page.Find("[data-action='Order.RemovePrescription']").Click();
        page.Find("[data-modal=confirm] [data-action='Action.Yes']").Click();

        page.WaitForAssertion(() => _order.PrescriptionScan.Should().BeNull());
        page.Find("[data-state=prescription]").TextContent.Should().Contain("Aucune ordonnance jointe");
    }

    [Fact]
    public void TheTab_IsInArabic_ForAnArabicSession()
    {
        SignIn(AppLanguage.Arabic, Permissions.Orders.View, Permissions.Orders.Edit);

        var page = OpenFile();

        page.Find("[data-action='Order.AttachPrescription']").TextContent.Should().Contain("إرفاق الوصفة");
        page.Find("[data-state=prescription]").TextContent.Should().MatchRegex("[؀-ۿ]");
    }

    // ---- Nouvelle demande --------------------------------------------------------------------------

    [Fact]
    public void OnANewRequest_TheButtonIsForWhoMayRegister()
    {
        SignIn(AppLanguage.French, Permissions.Orders.View);

        Render<OrderRegistration>().FindAll("[data-action='Order.AttachPrescription']").Should().BeEmpty();
    }

    [Fact]
    public void OnANewRequest_TheFilesBecomeOnePdf_SavedWithTheRequest()
    {
        SignIn(AppLanguage.French, Permissions.Orders.View, Permissions.Orders.Create);
        var page = Render<OrderRegistration>();

        page.FindComponents<InputFile>()[0].UploadFiles(
            Photo(), InputFileContent.CreateFromBinary(PrescriptionFiles.Pdf(2), "analyses.pdf", contentType: "application/pdf"));

        page.WaitForAssertion(() =>
            page.Find("[data-state=prescription]").TextContent.Should().Contain("Ordonnance jointe").And.Contain("3 pages"));
        _unitOfWork.Calls.Should().NotContain(c => c.Method == nameof(IUnitOfWork.SaveChangesAsync),
                                              "nothing is written before the request is registered");

        // More pages go after the ones there.
        page.FindComponents<InputFile>()[0].UploadFiles(Photo("verso.jpg"));
        page.WaitForAssertion(() => page.Find("[data-state=prescription]").TextContent.Should().Contain("4 pages"));

        page.Find("[data-action='Order.ViewPrescription']").Click();
        var download = JSInterop.VerifyInvoke("marocLis.download");
        download.Arguments[1].Should().Be("application/pdf");
        PdfPageCounter.Count((byte[])download.Arguments[2]!).Should().Be(4);
    }
}
