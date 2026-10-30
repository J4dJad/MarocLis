using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Tests.Domain;
using MarocLIS.Tests.Web;
using Xunit;
using static MarocLIS.Tests.Desktop.OrderFileRulesTests;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// « Numériser l'ordonnance » on the workstation, with a scanner that is not there: the pages
/// scanned one after the other, the scanner closed, missing or failing, and what the screens offer
/// to whom. Also the icons of the new buttons, in the sheet and for the browser.
/// </summary>
public sealed class PrescriptionScanDesktopTests
{
    /// <summary>A scanner that gives what it is told to, in order.</summary>
    private sealed class FakeScanner(params ScanOutcome[] outcomes) : IDocumentScanner
    {
        private readonly Queue<ScanOutcome> _outcomes = new(outcomes);
        public int Calls { get; private set; }

        public ScanOutcome AcquirePage()
        {
            Calls++;
            return _outcomes.Count > 0 ? _outcomes.Dequeue() : ScanOutcome.Cancelled;
        }
    }

    private sealed class ThrowingScanner : IDocumentScanner
    {
        public ScanOutcome AcquirePage() => throw new System.Runtime.InteropServices.COMException("Bourrage papier");
    }

    [Fact]
    public void ThePages_AreScannedOneAfterTheOther_UntilTheOperatorSaysThatIsAll()
    {
        var scanner = new FakeScanner(ScanOutcome.Page(PrescriptionFiles.Photo(60, 80)),
                                      ScanOutcome.Page(PrescriptionFiles.Photo(60, 80, seed: 2)),
                                      ScanOutcome.Page(PrescriptionFiles.Photo(60, 80, seed: 3)));
        var asked = new List<int>();

        var run = new PrescriptionScanFlow(scanner).Scan(n => { asked.Add(n); return n < 2; });

        run.Problem.Should().BeNull();
        run.Pages.Select(p => p.FileName).Should().Equal("scan-1.jpg", "scan-2.jpg");
        asked.Should().Equal(1, 2);
        scanner.Calls.Should().Be(2);
    }

    [Fact]
    public void ClosingTheScannerWindow_IsNotAnError()
    {
        var run = new PrescriptionScanFlow(new FakeScanner(ScanOutcome.Cancelled)).Scan(_ => true);

        run.Pages.Should().BeEmpty();
        run.Problem.Should().BeNull();
    }

    [Fact]
    public void WithoutAScanner_TheMessageSaysSo_AndPointsToAttachingAFile()
    {
        var run = new PrescriptionScanFlow(new FakeScanner(ScanOutcome.Unavailable)).Scan(_ => true);

        run.Pages.Should().BeEmpty();
        run.Problem!.ResourceKey.Should().Be("Prescription_ScannerUnavailable");
        run.SuggestsAttachingAFile.Should().BeTrue();

        var localizer = new MarocLIS.Localization.JsonLocalizer(new MarocLIS.Localization.LocalizationOptions());
        foreach (var language in new[] { AppLanguage.French, AppLanguage.English, AppLanguage.Arabic })
        {
            var message = localizer.Get("Prescription_ScannerUnavailable", language);
            var attach = localizer.Get("Order.AttachPrescriptionFile", language);
            message.Should().Contain(attach, "the message names the button to use instead, in {0}", language);
        }
    }

    [Fact]
    public void AScannerThatGoesAway_KeepsThePagesAlreadyScanned()
    {
        var run = new PrescriptionScanFlow(new FakeScanner(ScanOutcome.Page(PrescriptionFiles.SmallPng()), ScanOutcome.Unavailable))
            .Scan(_ => true);

        run.Pages.Should().ContainSingle();
        run.Problem.Should().BeNull();
    }

    [Fact]
    public void AScannerError_IsShownWithItsDetail()
    {
        var run = new PrescriptionScanFlow(new ThrowingScanner()).Scan(_ => true);

        run.Problem!.ResourceKey.Should().Be("Prescription_ScanFailed");
        run.Problem.Args.Should().Contain("Bourrage papier");
        run.SuggestsAttachingAFile.Should().BeFalse();
    }

    [Fact]
    public void ThePageLimit_StopsTheQuestion()
    {
        var pages = Enumerable.Range(0, 5).Select(i => ScanOutcome.Page(PrescriptionFiles.Photo(20, 20, seed: i))).ToArray();

        var run = new PrescriptionScanFlow(new FakeScanner(pages)).Scan(_ => true, pagesAlready: PrescriptionScanPolicy.MaxPages - 2);

        run.Pages.Should().HaveCount(2);
    }

    /// <summary>The scanned pages go through the service and come back as one PDF the registration takes.</summary>
    [Fact]
    public async Task TheScannedPages_BecomeTheDraftThatTheRegistrationCarries()
    {
        var run = new PrescriptionScanFlow(new FakeScanner(ScanOutcome.Page(PrescriptionFiles.Photo(800, 1000)),
                                                           ScanOutcome.Page(PrescriptionFiles.Photo(800, 1000, seed: 2))))
            .Scan(_ => true);
        var service = new PrescriptionScanService(Fake<IRepository<LabOrder>>.Create().Object, PrescriptionFiles.Composer(),
                                                  Fake<IUnitOfWork>.Create().Object, new Guard(Permissions.Orders.Create),
                                                  new DataAuditor());
        var draft = new PrescriptionDraft();

        draft.Keep((await service.PrepareAsync(run.Pages)).Value);

        draft.PageCount.Should().Be(2);
        var request = draft.ApplyTo(new RegisterOrderRequest { PatientId = Guid.NewGuid() });
        request.PrescriptionScanContentType.Should().Be("application/pdf");
        PrescriptionScanPolicy.CheckAssembled(request.PrescriptionScan, request.PrescriptionScanContentType).IsSuccess.Should().BeTrue();

        draft.Clear();
        draft.ApplyTo(request).PrescriptionScan.Should().BeNull();
    }

    // ---- What is offered, to whom --------------------------------------------------------------------

    [Fact]
    public void TheButtons_FollowTheRightsOfTheOrder()
    {
        var file = Detail() with { HasPrescriptionScan = true };
        var reader = new Operator(Permissions.Orders.View);
        var editor = new Operator(Permissions.Orders.View, Permissions.Orders.Edit);
        var counter = new Operator(Permissions.Orders.Create);

        PrescriptionScanRules.CanAttachAtRegistration(counter).Should().BeTrue();
        PrescriptionScanRules.CanAttachAtRegistration(reader).Should().BeFalse();

        PrescriptionScanRules.CanView(file, reader).Should().BeTrue();
        PrescriptionScanRules.CanView(file with { HasPrescriptionScan = false }, reader).Should().BeFalse();
        PrescriptionScanRules.CanChange(file, reader).Should().BeFalse();
        PrescriptionScanRules.CanChange(file, editor).Should().BeTrue();
        PrescriptionScanRules.CanRemove(file, editor).Should().BeTrue();
        PrescriptionScanRules.CanRemove(file with { HasPrescriptionScan = false }, editor).Should().BeFalse();

        var cancelled = Detail(LabOrderStatus.Cancelled) with { HasPrescriptionScan = true };
        PrescriptionScanRules.CanChange(cancelled, editor).Should().BeFalse("a cancelled file is closed");
        PrescriptionScanRules.CanView(cancelled, reader).Should().BeTrue("its prescription can still be read");
    }

    [Fact]
    public void ThePageCount_IsSaidInTheRightNumber()
    {
        PrescriptionScanRules.Pages(null).Should().BeNull();
        PrescriptionScanRules.Pages(1).Should().Be(("Order.PrescriptionOnePage", 1));
        PrescriptionScanRules.Pages(3).Should().Be(("Order.PrescriptionPages", 3));
    }

    // ---- Icons ---------------------------------------------------------------------------------------

    [Fact]
    public void TheNewButtons_HaveTheirIcons_AndNoIconWasLost()
    {
        IconCatalog.Actions["Order.ScanPrescription"].Should().Be(Glyph.ScanLine);
        IconCatalog.Actions["Order.AttachPrescriptionFile"].Should().Be(Glyph.Paperclip);
        IconCatalog.Actions["Order.AttachPrescription"].Should().Be(Glyph.Paperclip);
        IconCatalog.Actions["Order.PhotographPrescription"].Should().Be(Glyph.Camera);

        var root = RepositoryRoot();
        var icons = Path.Combine(root, "src", "MarocLIS.Web", "wwwroot", "icons");
        Enum.GetValues<Glyph>().Where(g => !File.Exists(Path.Combine(icons, g + ".png")))
            .Should().BeEmpty("every glyph has its mask for the browser");

        // The workstation's sheet: one 64-pixel cell per glyph, sixteen to a row.
        using var sheet = SkiaSharp.SKBitmap.Decode(Path.Combine(root, "src", "MarocLIS.Desktop", "Resources", "Icons", "icons.png"));
        var glyphs = Enum.GetValues<Glyph>().Distinct().Count();
        sheet.Width.Should().Be(16 * 64);
        sheet.Height.Should().Be((glyphs + 15) / 16 * 64);

        // Each cell of the sheet is the glyph of the same name in the browser, in code-point order.
        var ordered = Enum.GetValues<Glyph>().Distinct().OrderBy(g => (int)g).ToList();
        foreach (var glyph in new[] { Glyph.Activity, Glyph.Paperclip, Glyph.ScanLine, Glyph.Camera, Glyph.X })
        {
            var index = ordered.IndexOf(glyph);
            using var png = SkiaSharp.SKBitmap.Decode(Path.Combine(icons, glyph + ".png"));
            var differing = 0;
            for (var y = 0; y < 64; y++)
                for (var x = 0; x < 64; x++)
                    if (sheet.GetPixel(index % 16 * 64 + x, index / 16 * 64 + y).Alpha != png.GetPixel(x, y).Alpha) differing++;
            differing.Should().Be(0, "the cell of {0} is its browser mask", glyph);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MarocLIS.sln")))
            directory = directory.Parent;
        return directory!.FullName;
    }

    private sealed class Operator(params string[] granted) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "nadia";
        public string FullName => "Nadia";
        public Guid SiteId { get; } = Guid.NewGuid();
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => default;
        public IReadOnlyList<Guid> AccessibleSiteIds => [SiteId];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>(granted);
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => Permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "ACCUEIL-1";
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
