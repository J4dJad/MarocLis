using FluentAssertions;
using MarocLIS.Application.Input;
using MarocLIS.Application.Quality;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;
using MarocLIS.Tests.Licensing;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Documents qualité », moved out of the workstation's QualityDocumentsView and its
/// dialogs, now shared with the web.
/// </summary>
public sealed class QualityDocumentScreenRulesTests
{
    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    internal static ControlledDocumentRow Document(string reference = "PR-PRE-001", bool active = true, int? effective = 2,
                                                   int? latest = 3, DocumentVersionStatus? latestStatus = DocumentVersionStatus.UnderReview,
                                                   bool overdue = false, bool acknowledgement = true, bool read = false,
                                                   string title = "Procédure de prélèvement sanguin") =>
        new(Guid.NewGuid(), reference, new MultilingualText(title, "إجراء سحب الدم"), DocumentKind.Procedure, "PRE", "Nadia Bennani", 24,
            acknowledgement, active, effective, effective is null ? null : new DateOnly(2025, 3, 1),
            effective is null ? null : new DateOnly(2027, 3, 1), overdue, latest, latestStatus, read);

    internal static DocumentVersionRow Version(int number = 3, DocumentVersionStatus status = DocumentVersionStatus.UnderReview,
                                               bool content = true, string? file = "PR-PRE-001-v3.pdf") =>
        new(Guid.NewGuid(), number, status, new MultilingualText("Ajout du tube citraté"), file, "Karim Tahiri",
            new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.FromHours(1)), null, null, null, null,
            status == DocumentVersionStatus.Effective ? new DateOnly(2025, 3, 1) : null, null, content);

    private static QualityDocumentScreenActions Of(ControlledDocumentRow? document, DocumentVersionRow? version, params string[] rights) =>
        QualityDocumentScreenRules.Actions(new LicenceTestKit.User(true, rights), document, version);

    [Fact]
    public void Drafting_and_approving_are_separate_rights_and_each_step_fits_the_versions_state()
    {
        var document = Document();
        const string manage = Permissions.Quality.ManageDocuments;
        const string approve = Permissions.Quality.ApproveDocument;

        var reader = Of(document, Version(), Permissions.Quality.View);
        reader.Should().Be(new QualityDocumentScreenActions(false, false, false, false, false, false, true, true, false, "Documents.Withdraw"));

        var author = Of(document, Version(), manage);
        author.Should().Match<QualityDocumentScreenActions>(a => a.NewDocument && a.Review && !a.Submit && !a.Approve && !a.Withdraw);
        author.NewVersion.Should().BeFalse("a version is already in progress");

        var approver = Of(document, Version(), approve);
        approver.Should().Match<QualityDocumentScreenActions>(a => a.Approve && !a.Review && !a.MakeEffective && a.Withdraw && !a.NewDocument);

        Of(document, Version(status: DocumentVersionStatus.Draft), manage).Submit.Should().BeTrue();
        Of(document, Version(status: DocumentVersionStatus.Reviewed), approve).Approve.Should().BeTrue();
        Of(document, Version(status: DocumentVersionStatus.Approved), approve).MakeEffective.Should().BeTrue();
        Of(Document(latest: 2, latestStatus: DocumentVersionStatus.Effective), Version(2, DocumentVersionStatus.Effective), manage)
            .NewVersion.Should().BeTrue("the last version is in force");
        Of(document, Version(content: false), manage).Open.Should().BeFalse();
        Of(Document(read: true), Version()).Acknowledge.Should().BeFalse("already read");
        Of(Document(effective: null), Version()).Acknowledge.Should().BeFalse("nothing in force to read");

        var retired = Of(Document(active: false, latestStatus: DocumentVersionStatus.Effective), Version(status: DocumentVersionStatus.Draft), manage, approve);
        retired.Should().Match<QualityDocumentScreenActions>(a => !a.NewVersion && !a.Submit && a.Withdraw && a.WithdrawKey == "Documents.Reactivate");
    }

    [Fact]
    public void The_steps_act_on_the_version_selected_else_the_newest()
    {
        var v1 = Version(1, DocumentVersionStatus.Superseded);
        var v2 = Version(2, DocumentVersionStatus.Effective);

        QualityDocumentScreenRules.SelectedVersion([v1, v2], v1.Id).Should().Be(v1);
        QualityDocumentScreenRules.SelectedVersion([v1, v2], null).Should().Be(v2);
        QualityDocumentScreenRules.SelectedVersion([], null).Should().BeNull();
        QualityDocumentScreenRules.Keep([Document("A"), Document("B")], null)!.Reference.Should().Be("A");
    }

    [Fact]
    public void A_document_reads_its_version_in_force_its_review_and_its_read_mark()
    {
        var document = Document(overdue: true);
        QualityDocumentScreenRules.Effective(document).Should().Be("v2");
        QualityDocumentScreenRules.EffectiveFrom(document).Should().Be("01/03/25");
        QualityDocumentScreenRules.NextReview(document).Should().Be("! 01/03/27");
        QualityDocumentScreenRules.Latest(document, Text).Should().Be("v3 · DocumentVersionStatus.UnderReview");
        QualityDocumentScreenRules.Latest(Document(latest: 2), Text).Should().BeEmpty("the newest is the one in force");
        QualityDocumentScreenRules.Read(document).Should().Be("!");
        QualityDocumentScreenRules.Read(Document(read: true)).Should().Be("✓");
        QualityDocumentScreenRules.Read(Document(acknowledgement: false)).Should().BeEmpty();

        var none = Document(effective: null);
        QualityDocumentScreenRules.Effective(none).Should().Be("—");
        QualityDocumentScreenRules.NextReview(none).Should().Be("—");

        QualityDocumentScreenRules.Ink(document, "review").Should().Be(QualityInk.Abnormal);
        QualityDocumentScreenRules.Ink(document, "reference").Should().Be(QualityInk.Abnormal);
        QualityDocumentScreenRules.Ink(document, "effective").Should().Be(QualityInk.Plain);
        QualityDocumentScreenRules.Ink(none, "effectiveFrom").Should().Be(QualityInk.Abnormal);
        QualityDocumentScreenRules.Ink(Document(active: false, overdue: true), "review").Should().Be(QualityInk.Muted);

        QualityDocumentScreenRules.VersionTone(DocumentVersionStatus.Effective).Should().Be(QualityTone.Ok);
        QualityDocumentScreenRules.VersionTone(DocumentVersionStatus.Scheduled).Should().Be(QualityTone.Info);
        QualityDocumentScreenRules.VersionTone(DocumentVersionStatus.UnderReview).Should().Be(QualityTone.Warning);
        QualityDocumentScreenRules.VersionTone(DocumentVersionStatus.Draft).Should().Be(QualityTone.Waiting);
        QualityDocumentScreenRules.VersionTone(DocumentVersionStatus.Superseded).Should().Be(QualityTone.Muted);

        QualityDocumentScreenRules.Count([document, Document(read: true), none], Text)
            .Should().Be("QualityHr.Documents.Count(3) · Documents.ReviewOverdue 1 · Documents.ToRead 1");

        QualityDocumentScreenRules.Warning(none)!.Key.Should().Be("Documents.NoEffectiveVersionWarning");
        QualityDocumentScreenRules.Warning(document)!.Key.Should().Be("Documents.ReviewOverdueWarning");
        QualityDocumentScreenRules.Warning(Document()).Should().BeNull();
        QualityDocumentScreenRules.VersionsHeading(Document(), Text).Should().Be("Documents.Versions — PR-PRE-001");
    }

    [Fact]
    public void A_document_needs_its_reference_and_french_title()
    {
        var form = new ControlledDocumentForm();
        QualityDocumentScreenRules.Check(form).Should().Be(new FieldRefusal(QualityDocumentScreenRules.ReferenceField, "Document_ReferenceRequired"));
        form.Reference = " MO-HEM-004 ";
        QualityDocumentScreenRules.Check(form)!.Field.Should().Be(QualityDocumentScreenRules.TitleFrField);
        form.TitleFr = "Mode opératoire XN-550";
        form.TitleAr = " ";
        form.ReviewPeriodMonths = 0;
        QualityDocumentScreenRules.Check(form).Should().BeNull();

        var request = QualityDocumentScreenRules.ToRequest(form);
        request.Reference.Should().Be("MO-HEM-004");
        request.Title.Ar.Should().BeNull();
        request.ReviewPeriodMonths.Should().Be(1);
        request.Kind.Should().Be(DocumentKind.Procedure);

        QualityDocumentScreenRules.FieldOf(new Error("Conflict", "Document_ReferenceAlreadyTaken")).Should().Be(QualityDocumentScreenRules.ReferenceField);
    }

    [Fact]
    public void A_file_is_chosen_of_a_kind_kept_and_within_the_size()
    {
        QualityDocumentScreenRules.CheckFile(null, 0)!.Key.Should().Be("Document_ContentRequired");
        QualityDocumentScreenRules.CheckFile("procedure.exe", 1000).Should().Be(new FieldRefusal(QualityDocumentScreenRules.FileField, "Document_FileType"));
        QualityDocumentScreenRules.CheckFile("procedure.pdf", QualityDocumentScreenRules.MaximumContentBytes + 1)
            .Should().Be(new FieldRefusal(QualityDocumentScreenRules.FileField, "Document_ContentTooLarge", "20"));
        QualityDocumentScreenRules.CheckFile("procedure.PDF", 1000).Should().BeNull();
        QualityDocumentScreenRules.AcceptedExtensions.Should().StartWith(".pdf,.jpg");

        var content = new DocumentContent([1, 2], "PR-PRE-001-v3.pdf", null);
        QualityDocumentScreenRules.ContentType(content).Should().Be("application/pdf");
        QualityDocumentScreenRules.DownloadName(Document(), Version(), content).Should().Be("PR-PRE-001-v3.pdf");
    }

    [Fact]
    public void A_version_comes_into_force_today_or_later_and_says_what_it_replaces()
    {
        var today = new DateOnly(2026, 10, 18);
        QualityDocumentScreenRules.CheckEffective(today.AddDays(-1), today)!.Field.Should().Be(QualityDocumentScreenRules.EffectiveFromField);
        QualityDocumentScreenRules.CheckEffective(today, today).Should().BeNull();
        QualityDocumentScreenRules.EffectiveNotice(2, Text).Should().Be("Documents.ReplacesVersion(2) Documents.MakeEffectiveNotice");
        QualityDocumentScreenRules.EffectiveNotice(null, Text).Should().Be("Documents.MakeEffectiveNotice");
        QualityDocumentScreenRules.Subject(Document(), Version()).Should().Be("PR-PRE-001 v3");
        QualityDocumentScreenRules.WithdrawPromptKey(Document(active: false)).Should().Be("Documents.ReactivatePrompt");
    }
}
