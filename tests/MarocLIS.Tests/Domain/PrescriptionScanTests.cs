using System.Text;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Reporting;
using MarocLIS.Tests.Web;
using QuestPDF.Fluent;
using SkiaSharp;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>Files for the prescription tests: real photos, real PDFs, and a program in disguise.</summary>
internal static class PrescriptionFiles
{
    /// <summary>A photo as a phone takes it: large, and noisy enough not to compress to nothing.</summary>
    public static byte[] Photo(int width = 3000, int height = 4000, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg,
                               int seed = 1)
    {
        using var bitmap = new SKBitmap(width, height);
        var random = new Random(seed);
        var pixels = new SKColor[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            var grey = (byte)(200 + random.Next(55));
            pixels[i] = new SKColor(grey, grey, (byte)(grey - 10));
        }

        bitmap.Pixels = pixels;
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 95);
        return data.ToArray();
    }

    public static byte[] SmallPng() => Photo(400, 300, SKEncodedImageFormat.Png);

    /// <summary>A PDF of <paramref name="pages"/> pages, as a clinic sends one.</summary>
    public static byte[] Pdf(int pages = 2)
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        return Document.Create(container =>
        {
            for (var i = 1; i <= pages; i++)
            {
                var n = i;
                container.Page(page =>
                {
                    page.Size(QuestPDF.Helpers.PageSizes.A5);
                    page.Content().Text($"Ordonnance page {n}");
                });
            }
        }).GeneratePdf();
    }

    /// <summary>A Windows program renamed « ordonnance.pdf ».</summary>
    public static byte[] Program() => [.. "MZ"u8.ToArray(), .. new byte[2048]];

    public static IPrescriptionDocumentComposer Composer() =>
        new QuestPdfPrescriptionComposer(new ReportingOptions { FontDirectory = Path.Combine(Path.GetTempPath(), "no-fonts") });

    public static string Text(byte[] pdf) => Encoding.Latin1.GetString(pdf);
}

/// <summary>
/// The scanned prescription of an order: attached, replaced, completed, viewed and removed, with the
/// rights of the order, the audit trail's reason for each change, and the checks every page goes
/// through (its real kind, its size) before the pages are assembled into one PDF.
/// </summary>
public sealed class PrescriptionScanTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(1));

    private readonly LabOrder _order = new("2610-000042", Guid.NewGuid(), Guid.NewGuid(), Now, Guid.NewGuid());
    private readonly Fake<IUnitOfWork> _unitOfWork = Fake<IUnitOfWork>.Create();
    private readonly DataAuditor _audit = new();

    private PrescriptionScanService Service(params string[] granted)
    {
        var orders = Fake<IRepository<LabOrder>>.Create()
            .On(nameof(IRepository<LabOrder>.GetByIdAsync), args => (Guid)args[0]! == _order.Id ? _order : null);
        return new PrescriptionScanService(orders.Object, PrescriptionFiles.Composer(), _unitOfWork.Object,
                                           new Guard(granted), _audit);
    }

    private IReadOnlyList<string?> Reasons() =>
        [.. _unitOfWork.Calls.Where(c => c.Method == "set_AuditReason").Select(c => (string?)c.Arguments[0])];

    private int Saves => _unitOfWork.CallsTo(nameof(IUnitOfWork.SaveChangesAsync));

    private static PrescriptionPage Page(byte[] content, string name = "page.jpg") => new(name, content);

    // ---- Attach, replace, complete, remove ---------------------------------------------------------

    [Fact]
    public async Task Attaching_KeepsOnePdfOnTheOrder_AndTheTrailSaysSo()
    {
        var service = Service(Permissions.Orders.Edit);

        var result = await service.AttachAsync(_order.Id, [Page(PrescriptionFiles.Photo())]);

        result.IsSuccess.Should().BeTrue(string.Join(", ", result.Errors.Select(e => e.ResourceKey)));
        result.Value.PageCount.Should().Be(1);
        _order.PrescriptionScanContentType.Should().Be("application/pdf");
        SafeDocumentFile.ContentTypeFromSignature(_order.PrescriptionScan).Should().Be("application/pdf");
        Saves.Should().Be(1);
        Reasons().Should().ContainSingle().Which.Should().StartWith("Ordonnance numérisée jointe au dossier 2610-000042 (1 page");
    }

    [Fact]
    public async Task Attaching_AgainReplacesIt_AndAddingPagesCompletesIt()
    {
        var service = Service(Permissions.Orders.Edit);
        (await service.AttachAsync(_order.Id, [Page(PrescriptionFiles.SmallPng(), "a.png")])).IsSuccess.Should().BeTrue();

        var replaced = await service.AttachAsync(_order.Id, [Page(PrescriptionFiles.Pdf(3), "clinique.pdf")]);
        replaced.Value.PageCount.Should().Be(3);

        var completed = await service.AttachAsync(_order.Id, [Page(PrescriptionFiles.SmallPng(), "verso.png")], append: true);
        completed.Value.PageCount.Should().Be(4, "the new page goes after the three already there");

        Reasons().Should().HaveCount(3);
        Reasons()[1].Should().StartWith("Ordonnance numérisée remplacée sur le dossier 2610-000042 (3 pages");
        Reasons()[2].Should().StartWith("Ordonnance numérisée complétée sur le dossier 2610-000042 (4 pages");
    }

    [Fact]
    public async Task Removing_ClearsItAndIsTraced_AndRemovingNothingIsRefused()
    {
        var service = Service(Permissions.Orders.Edit);
        await service.AttachAsync(_order.Id, [Page(PrescriptionFiles.SmallPng(), "a.png")]);

        (await service.RemoveAsync(_order.Id)).IsSuccess.Should().BeTrue();

        _order.PrescriptionScan.Should().BeNull();
        _order.PrescriptionScanContentType.Should().BeNull();
        Reasons()[^1].Should().Be("Ordonnance numérisée retirée du dossier 2610-000042");

        (await service.RemoveAsync(_order.Id)).FirstError.ResourceKey.Should().Be("Prescription_NotAttached");
        Saves.Should().Be(2);
    }

    [Fact]
    public async Task Viewing_HandsOutThePdfWithItsPages_AndIsLoggedAsAConsultation()
    {
        await Service(Permissions.Orders.Edit).AttachAsync(_order.Id, [Page(PrescriptionFiles.Pdf(2), "o.pdf")]);

        var file = await Service(Permissions.Orders.View).GetAsync(_order.Id);

        file.Value.FileName.Should().Be("Ordonnance-2610-000042.pdf");
        file.Value.ContentType.Should().Be("application/pdf");
        file.Value.PageCount.Should().Be(2);
        _audit.Entries.Should().ContainSingle(e => e.Kind == DataAccessKind.ViewOrder && e.OrderId == _order.Id
                                                   && e.PatientId == _order.PatientId && e.Context == "PrescriptionScan");
    }

    [Fact]
    public async Task ACancelledFile_TakesNoChange()
    {
        var service = Service(Permissions.Orders.Edit);
        await service.AttachAsync(_order.Id, [Page(PrescriptionFiles.SmallPng(), "a.png")]);
        _order.Cancel(Now, Guid.NewGuid(), "Patient parti").IsSuccess.Should().BeTrue();

        (await service.AttachAsync(_order.Id, [Page(PrescriptionFiles.SmallPng(), "b.png")]))
            .FirstError.ResourceKey.Should().Be("Order_CancelledCannotAmend");
        (await service.RemoveAsync(_order.Id)).FirstError.ResourceKey.Should().Be("Order_CancelledCannotAmend");
        _order.PrescriptionScan.Should().NotBeNull();
        Saves.Should().Be(1);
    }

    // ---- Rights ------------------------------------------------------------------------------------

    [Fact]
    public async Task Changing_NeedsTheRightToEditTheOrder_AndViewing_TheRightToSeeIt()
    {
        var reader = Service(Permissions.Orders.View, Permissions.Orders.Create);

        (await reader.AttachAsync(_order.Id, [Page(PrescriptionFiles.SmallPng(), "a.png")]))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
        (await reader.RemoveAsync(_order.Id)).FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
        (await Service(Permissions.Orders.Edit).GetAsync(_order.Id))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
        Saves.Should().Be(0);
        _order.PrescriptionScan.Should().BeNull();
    }

    [Fact]
    public async Task Preparing_ForANewRequest_NeedsTheRightToRegisterOrEdit()
    {
        (await Service(Permissions.Orders.View).PrepareAsync([Page(PrescriptionFiles.SmallPng(), "a.png")]))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");

        var prepared = await Service(Permissions.Orders.Create).PrepareAsync([Page(PrescriptionFiles.SmallPng(), "a.png")]);
        var more = await Service(Permissions.Orders.Create).PrepareAsync([Page(PrescriptionFiles.Pdf(2), "b.pdf")], prepared.Value.Content);

        more.Value.PageCount.Should().Be(3);
        Saves.Should().Be(0, "nothing is written before the request is registered");
    }

    // ---- What is accepted --------------------------------------------------------------------------

    [Fact]
    public async Task AFileIsJudgedByItsBytes_NotByItsName()
    {
        var service = Service(Permissions.Orders.Edit);

        var disguised = await service.AttachAsync(_order.Id, [Page(PrescriptionFiles.Program(), "ordonnance.pdf")]);
        disguised.FirstError.ResourceKey.Should().Be("Prescription_UnsupportedFile");
        disguised.FirstError.Args.Should().Contain("ordonnance.pdf");

        // A photo named like a PDF is still a photo, and is taken.
        (await service.AttachAsync(_order.Id, [Page(PrescriptionFiles.SmallPng(), "scan.pdf")])).IsSuccess.Should().BeTrue();

        // A TIFF or a Word file are documents the laboratory keeps elsewhere, not a prescription.
        byte[] tiff = [0x49, 0x49, 0x2A, 0x00, .. new byte[64]];
        (await service.AttachAsync(_order.Id, [Page(tiff, "scan.tif")])).FirstError.ResourceKey.Should().Be("Prescription_UnsupportedFile");
    }

    [Fact]
    public async Task ATooLargeFile_IsRefusedWithTheLimit()
    {
        var service = Service(Permissions.Orders.Edit);
        var huge = new byte[PrescriptionScanPolicy.MaxFileBytes + 1];
        "%PDF-1.7"u8.CopyTo(huge);

        var result = await service.AttachAsync(_order.Id, [Page(huge, "gros.pdf")]);

        result.FirstError.ResourceKey.Should().Be("Prescription_FileTooLarge");
        result.FirstError.Args.Should().Equal("gros.pdf", PrescriptionScanPolicy.MaxFileMegabytes);
        _order.PrescriptionScan.Should().BeNull();
    }

    [Fact]
    public async Task TooManyPages_AreRefused()
    {
        var pages = Enumerable.Range(0, PrescriptionScanPolicy.MaxPages + 1)
                              .Select(i => Page(PrescriptionFiles.Photo(60, 80, seed: i), $"p{i}.jpg")).ToList();

        (await Service(Permissions.Orders.Edit).AttachAsync(_order.Id, pages))
            .FirstError.ResourceKey.Should().Be("Prescription_TooManyPages");
    }

    [Fact]
    public async Task NoPage_IsRefused()
    {
        (await Service(Permissions.Orders.Edit).AttachAsync(_order.Id, []))
            .FirstError.ResourceKey.Should().Be("Prescription_NoPage");
    }

    // ---- Assembly ----------------------------------------------------------------------------------

    [Fact]
    public async Task PhotosAndPdfs_AreMergedIntoOnePdf_InTheOrderGiven_WithThePhotosDownscaled()
    {
        var photo = PrescriptionFiles.Photo(3000, 4000);
        var png = PrescriptionFiles.Photo(2400, 1800, SKEncodedImageFormat.Png, seed: 2);
        var pdf = PrescriptionFiles.Pdf(2);

        var result = await Service(Permissions.Orders.Edit).AttachAsync(
            _order.Id, [Page(photo, "recto.jpg"), Page(pdf, "bilan.pdf"), Page(png, "verso.png")]);

        result.IsSuccess.Should().BeTrue(string.Join(", ", result.Errors.Select(e => e.ResourceKey)));
        result.Value.PageCount.Should().Be(4);
        PdfPageCounter.Count(_order.PrescriptionScan!).Should().Be(4);

        // Long side brought to 2000 pixels and recompressed: a page is a few hundred kilobytes.
        var text = PrescriptionFiles.Text(_order.PrescriptionScan!);
        text.Should().Contain("/Width 1500").And.Contain("/Height 2000").And.NotContain("/Width 3000");
        text.Should().Contain("/Width 2000").And.Contain("/Height 1500");
        _order.PrescriptionScan!.Length.Should().BeLessThan(photo.Length + png.Length,
                                                            "the photos are recompressed, not copied");
        (_order.PrescriptionScan!.Length - pdf.Length).Should().BeLessThan(2 * 900 * 1024);
    }

    /// <summary>
    /// A phone held upright stores the photo on its side and says so in the EXIF orientation: the
    /// page comes out upright. Left half red, right half blue, as stored.
    /// </summary>
    [Theory]
    [InlineData((ushort)6, true)]
    [InlineData((ushort)8, false)]
    [InlineData((ushort)1, null)]
    public void APhotoTakenUpright_IsTurnedUpright(ushort orientation, bool? redOnTop)
    {
        using var bitmap = new SKBitmap(400, 200);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Blue);
            using var red = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(0, 0, 200, 200, red);
        }

        using var encoded = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Jpeg, 95);
        var jpeg = encoded.ToArray();
        byte[] exif =
        [
            0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0x00, 0x2A, 0, 0, 0, 8,
            0x00, 0x01, 0x01, 0x12, 0x00, 0x03, 0, 0, 0, 1, (byte)(orientation >> 8), (byte)orientation, 0, 0,
            0, 0, 0, 0
        ];
        byte[] tagged = [.. jpeg[..2], .. exif, .. jpeg[2..]];

        var picture = QuestPdfPrescriptionComposer.Downscale(tagged)!.Value;
        using var upright = SKBitmap.Decode(picture.Jpeg);

        if (redOnTop is null)
        {
            (picture.Width, picture.Height).Should().Be((400, 200));
            upright.GetPixel(20, 100).Red.Should().BeGreaterThan(200);
            return;
        }

        (picture.Width, picture.Height).Should().Be((200, 400));
        var top = upright.GetPixel(100, 20);
        var bottom = upright.GetPixel(100, 380);
        (redOnTop.Value ? top : bottom).Red.Should().BeGreaterThan(200);
        (redOnTop.Value ? bottom : top).Blue.Should().BeGreaterThan(200);
    }

    [Fact]
    public void ADamagedPhoto_IsNamedInTheRefusal()
    {
        byte[] damaged = [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[200]];

        var result = PrescriptionScanPolicy.Assemble(PrescriptionFiles.Composer(), [Page(damaged, "flou.jpg")]);

        result.FirstError.ResourceKey.Should().Be("Prescription_UnreadableFile");
        result.FirstError.Args.Should().Contain("flou.jpg");
    }

    [Fact]
    public void ARegistration_TakesOnlyAnAssembledPdf()
    {
        PrescriptionScanPolicy.CheckAssembled(null, null).IsSuccess.Should().BeTrue();
        PrescriptionScanPolicy.CheckAssembled(PrescriptionFiles.Pdf(1), "application/pdf").IsSuccess.Should().BeTrue();
        PrescriptionScanPolicy.CheckAssembled(PrescriptionFiles.SmallPng(), "image/png")
            .FirstError.ResourceKey.Should().Be("Prescription_UnsupportedFile");
        PrescriptionScanPolicy.CheckAssembled(PrescriptionFiles.Program(), "application/pdf")
            .FirstError.ResourceKey.Should().Be("Prescription_UnsupportedFile");
    }

    [Fact]
    public void TheSignatures_AreRead()
    {
        SafeDocumentFile.ContentTypeFromSignature("%PDF-1.4"u8).Should().Be("application/pdf");
        SafeDocumentFile.ContentTypeFromSignature(PrescriptionFiles.Photo(10, 10)).Should().Be("image/jpeg");
        SafeDocumentFile.ContentTypeFromSignature(PrescriptionFiles.SmallPng()).Should().Be("image/png");
        SafeDocumentFile.ContentTypeFromSignature(PrescriptionFiles.Program()).Should().BeNull();
        SafeDocumentFile.ContentTypeFromSignature([]).Should().BeNull();
    }

    private sealed class Guard(params string[] granted) : IPermissionGuard
    {
        private static readonly Error Denied = Error.Forbidden("Message.PermissionDenied");
        public Result Require(string permission) => granted.Contains(permission) ? Result.Success() : Result.Failure(Denied);
        public Result RequireAny(params string[] permissions) =>
            permissions.Any(granted.Contains) ? Result.Success() : Result.Failure(Denied);
        public Result RequireAll(params string[] permissions) =>
            permissions.All(granted.Contains) ? Result.Success() : Result.Failure(Denied);
        public Task<Result> RequireBiologicalValidationAsync(string departmentCode, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }
}
