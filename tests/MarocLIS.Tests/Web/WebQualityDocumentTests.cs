using System.Text;
using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Quality;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;
using MarocLIS.Web.Components.Screens.Quality;
using MarocLIS.Web.Session;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Rules = MarocLIS.Tests.Desktop.QualityDocumentScreenRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Documents qualité » in the browser: the register and the versions of the document open, a
/// reference already taken refused on its box, a file of a kind not kept refused before it is sent,
/// a reviewed version approved, the text in force handed to the browser, each step kept by the right
/// the workstation asks — all through the workstation's DocumentControlService.
/// </summary>
public sealed class WebQualityDocumentTests : WebScreenContext
{
    private readonly Fake<IDocumentControlQueries> _queries = Fake<IDocumentControlQueries>.Create();
    private readonly Fake<IRepository<ControlledDocument>> _documents = Fake<IRepository<ControlledDocument>>.Create();
    private readonly Fake<IUnitOfWork> _work = Fake<IUnitOfWork>.Create();

    public WebQualityDocumentTests()
    {
        Services.AddSingleton(_queries.Object);
        Services.AddSingleton(_documents.Object);
        Services.AddSingleton(_work.Object);
        Services.AddScoped<DocumentControlService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static readonly string[] Approver =
    [
        Permissions.Quality.View, Permissions.Quality.ManageDocuments, Permissions.Quality.ApproveDocument
    ];

    private static readonly string[] Writer = [Permissions.Quality.View, Permissions.Quality.ManageDocuments];
    private static readonly string[] Staff = [Permissions.Quality.View];

    private ControlledDocument? _created;

    private string? AuditReason => _work.Calls.LastOrDefault(c => c.Method == "set_AuditReason").Arguments is [string reason] ? reason : null;

    /// <summary>The sampling procedure, its version 3 reviewed and awaiting approval, beside a form overdue for review.</summary>
    private (ControlledDocument Document, DocumentVersion Version) Register()
    {
        var document = new ControlledDocument("PR-PRE-001", new MultilingualText("Procédure de prélèvement sanguin"), DocumentKind.Procedure, Guid.NewGuid());
        var author = Guid.NewGuid();
        document.AddDraft(null, author);
        var version = document.AddDraft(new MultilingualText("Ajout du tube citraté"), author);
        version.Content = Encoding.UTF8.GetBytes("%PDF-1.7");
        version.SubmitForReview();
        version.Review(TestClock.Now, Guid.NewGuid());

        var row = Rules.Document(effective: null, latest: version.VersionNumber, latestStatus: DocumentVersionStatus.Reviewed) with { Id = document.Id };
        var form = Rules.Document("EN-ANA-004", overdue: true, latest: 2, latestStatus: DocumentVersionStatus.Effective,
                                  title: "Fiche de suivi des températures");
        _queries.On(nameof(IDocumentControlQueries.GetDocumentsAsync), _ => _created is { } created
            ? new[] { row, form, Rules.Document(created.Reference, effective: null, latest: null, latestStatus: null) with { Id = created.Id } }
            : new[] { row, form });
        _documents.On(nameof(IRepository<ControlledDocument>.AddAsync), args => _created = (ControlledDocument)args[0]!);
        _queries.On(nameof(IDocumentControlQueries.GetVersionsAsync), args => (Guid)args[0]! == document.Id
            ? new[] { Rules.Version(1, DocumentVersionStatus.Draft, content: false, file: null), Rules.Version(version.VersionNumber, DocumentVersionStatus.Reviewed) }
            : new[] { Rules.Version(2, DocumentVersionStatus.Effective, file: "EN-ANA-004-v2.pdf") });
        _documents.On(nameof(IRepository<ControlledDocument>.GetByIdWithDetailsAsync), _ => document);
        _documents.On(nameof(IRepository<ControlledDocument>.GetByIdAsync), _ => document);
        return (document, version);
    }

    [Fact]
    public void TheRegister_AndTheVersionsOfTheDocumentOpen_AreOnOneScreen()
    {
        SignIn(AppLanguage.French, Approver);
        Register();

        var page = Render<QualityDocuments>();

        page.FindAll("[data-grid=documents] tbody tr").Should().HaveCount(2);
        page.Find("[data-state=versions-heading]").TextContent.Should().Contain("PR-PRE-001");
        page.FindAll("[data-grid=versions] tbody tr").Should().HaveCount(2);
        page.Find("[data-state=warning]").ClassName.Should().Contain("abnormal", "no version is in force yet");

        page.Find("[data-row='EN-ANA-004']").Click();
        page.Find("[data-state=warning]").TextContent.Should().NotBeNullOrWhiteSpace();
        page.Find("[data-grid=versions] [data-row=v2]").TextContent.Should().Contain("EN-ANA-004-v2.pdf");

        page.Find("[data-chip=include-inactive]").Click();
        _queries.Calls.Last(c => c.Method == nameof(IDocumentControlQueries.GetDocumentsAsync)).Arguments[2].Should().Be(true);
    }

    [Fact]
    public void AReferenceAlreadyTaken_IsRefusedOnItsBox_AndNothingIsCreated()
    {
        SignIn(AppLanguage.French, Approver);
        Register();
        _queries.On(nameof(IDocumentControlQueries.ReferenceExistsAsync), _ => true);

        var page = Render<QualityDocuments>();
        page.Find("[data-action='Documents.NewDocument']").Click();
        page.Find($"[data-field={QualityDocumentScreenRules.ReferenceField}]").Input("pr-pre-001");
        page.Find($"[data-field={QualityDocumentScreenRules.TitleFrField}]").Input("Procédure de prélèvement capillaire");
        page.Find("[data-modal=doc-editor] [data-action='Action.Save']").Click();

        page.Find($"[data-refusal={QualityDocumentScreenRules.ReferenceField}]").TextContent.Should().Contain("PR-PRE-001").And.Contain("déjà utilisée");
        _documents.CallsTo(nameof(IRepository<ControlledDocument>.AddAsync)).Should().Be(0);
    }

    [Fact]
    public void ANewDocument_IsCreated_AndItsFirstDraftAsked()
    {
        SignIn(AppLanguage.French, Approver);
        Register();

        var page = Render<QualityDocuments>();
        page.Find("[data-action='Documents.NewDocument']").Click();
        page.Find($"[data-field={QualityDocumentScreenRules.ReferenceField}]").Input("pr-pre-002");
        page.Find($"[data-field={QualityDocumentScreenRules.TitleFrField}]").Input("Procédure de prélèvement capillaire");
        page.Find("[data-modal=doc-editor] [data-action='Action.Save']").Click();

        _documents.CallsTo(nameof(IRepository<ControlledDocument>.AddAsync)).Should().Be(1);
        AuditReason.Should().Be("Création du document PR-PRE-002");
        page.Find("[data-row='PR-PRE-002']").ClassName.Should().Contain("is-selected", "the document created is the one open");
        page.Find("[data-prompt=confirm]").TextContent.Should().Contain("version");
        page.Find("[data-action='Action.Yes']").Click();
        page.Find("[data-modal=doc-version]").TextContent.Should().Contain("PR-PRE-002");
    }

    [Fact]
    public void AFileOfAKindNotKept_IsRefusedOnItsBox_BeforeItIsSent()
    {
        SignIn(AppLanguage.French, Writer);

        // The draft dialog itself refuses an executable before anything is read.
        var editor = Render<DocumentVersionEditor>(p => p.Add(x => x.Reference, "PR-PRE-001"));
        editor.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("@echo off", "procedure.bat"));
        editor.Find($"[data-refusal={QualityDocumentScreenRules.FileField}]").TextContent.Should().Contain("PDF");
        editor.Find("[data-state=file-name]").TextContent.Should().Contain("procedure.bat");
    }

    [Fact]
    public void AReviewedVersion_IsApproved_ByWhoMayApprove()
    {
        SignIn(AppLanguage.French, Approver);
        var (_, version) = Register();

        var page = Render<QualityDocuments>();
        page.Find("[data-action='Documents.Approve']").HasAttribute("disabled").Should().BeFalse();
        page.Find("[data-action='Documents.Approve']").Click();

        version.Status.Should().Be(DocumentVersionStatus.Approved);
        AuditReason.Should().Be("Approbation de PR-PRE-001 v2");
        page.Find("[data-state=status]").TextContent.Should().Contain("Enregistr");
    }

    [Fact]
    public void TheFile_IsHandedToTheBrowser_UnderItsOwnName()
    {
        SignIn(AppLanguage.French, Staff);
        Register();
        _queries.On(nameof(IDocumentControlQueries.GetContentAsync), _ => new DocumentContent(Encoding.UTF8.GetBytes("%PDF-1.7"), "PR-PRE-001-v3.pdf", "application/pdf"));

        var page = Render<QualityDocuments>();
        page.Find("[data-action='Documents.OpenFile']").Click();

        var download = JSInterop.Invocations.Should().ContainSingle(i => i.Identifier == "marocLis.download").Subject;
        download.Arguments[0].Should().BeOfType<string>().Which.Should().EndWith(".pdf");
        download.Arguments[1].Should().Be("application/pdf");
        page.Find("[data-state=status]").TextContent.Should().Contain("PR-PRE-001");
    }

    [Fact]
    public void WhoMayOnlyRead_IsOfferedTheFileAndTheAcknowledgement_AndNoStepOfTheVersions()
    {
        SignIn(AppLanguage.French, Staff);
        Register();

        var page = Render<QualityDocuments>();
        page.Find("[data-row='EN-ANA-004']").Click();

        page.Find("[data-action='Documents.Acknowledge']").HasAttribute("disabled").Should().BeFalse();
        page.Find("[data-action='Documents.OpenFile']").HasAttribute("disabled").Should().BeFalse();
        foreach (var step in new[] { "NewDocument", "NewVersion", "Submit", "Review", "Approve", "MakeEffective", "Withdraw" })
            page.Find($"[data-action='Documents.{step}']").HasAttribute("disabled").Should().BeTrue(step);
    }

    [Fact]
    public void InArabic_TheScreenIsArabic_AndThePageRunsRightToLeft()
    {
        SignIn(AppLanguage.Arabic, Approver);
        Register();

        var page = Render<QualityDocuments>();

        page.Find("[data-screen=documents]").TextContent.Should().NotContain("Documents.").And.NotContain("Web.").And.NotContain("[");
        page.Find("[data-row='PR-PRE-001']").TextContent.Should().Contain("إجراء سحب الدم");
        Services.GetRequiredService<WebTexts>().Direction.Should().Be("rtl");
    }
}
