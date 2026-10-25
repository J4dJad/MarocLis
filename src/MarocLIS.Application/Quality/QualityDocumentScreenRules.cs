using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;

namespace MarocLIS.Application.Quality;

/// <summary>
/// What « Documents qualité » offers, by right and by the state of the document and of the version
/// selected — the workstation's PermissionGate calls and RefreshButtons. Only the step that fits the
/// version's state is offered; DocumentControlService stays the judge (the author may not review
/// their own text, a step out of order is refused).
/// </summary>
/// <param name="NewDocument">A document created in the register (managing documents).</param>
/// <param name="NewVersion">A draft attached, once the last version is in force (managing documents).</param>
/// <param name="Submit">A draft submitted for review (managing documents).</param>
/// <param name="Review">A version under review reviewed (managing documents).</param>
/// <param name="Approve">A version under review or reviewed approved (approving documents).</param>
/// <param name="MakeEffective">An approved version put in force (approving documents).</param>
/// <param name="Open">The selected version's file opened — downloaded on the web.</param>
/// <param name="Acknowledge">The version in force acknowledged as read by the user.</param>
/// <param name="Withdraw">The document withdrawn, or reactivated (approving documents).</param>
/// <param name="WithdrawKey">« Retirer » or « Réactiver », by the document's state.</param>
public sealed record QualityDocumentScreenActions(
    bool NewDocument, bool NewVersion, bool Submit, bool Review, bool Approve, bool MakeEffective,
    bool Open, bool Acknowledge, bool Withdraw, string WithdrawKey);

/// <summary>A document as typed: the workstation's ControlledDocumentDialog.</summary>
public sealed class ControlledDocumentForm
{
    public string? Reference { get; set; }
    public DocumentKind Kind { get; set; } = DocumentKind.Procedure;
    public string? Process { get; set; }
    public string? TitleFr { get; set; }
    public string? TitleAr { get; set; }
    public string? TitleEn { get; set; }
    public int ReviewPeriodMonths { get; set; } = QualityDocumentScreenRules.DefaultReviewPeriodMonths;
    public bool RequiresAcknowledgement { get; set; }
}

/// <summary>The warning under the register: no version in force, or a review overdue.</summary>
public sealed record QualityDocumentWarning(string Key, bool Abnormal);

/// <summary>
/// The rules of « Documents qualité » that need no window: what is offered by which right and in
/// which state, how a document and a version read in the register (the version in force, the next
/// review, the newest version, the read mark, their inks and pills), how a document and a file are
/// typed and refused field by field, and what each step asks. The workstation's
/// QualityDocumentsView and the web's screen apply the same ones; every decision stays
/// DocumentControlService's and the domain's.
/// </summary>
public static class QualityDocumentScreenRules
{
    public const int ReferenceMaxLength = 40;
    public const int ProcessMaxLength = 30;
    public const int TitleMaxLength = 300;
    public const int SummaryMaxLength = 2000;

    public const int DefaultReviewPeriodMonths = 24;
    public const int ReviewPeriodMin = 1;
    public const int ReviewPeriodMax = 120;

    /// <summary>The largest file, in bytes: DocumentControlService's.</summary>
    public const int MaximumContentBytes = DocumentControlService.MaximumContentBytes;

    public const string ReferenceField = "doc-reference";
    public const string TitleFrField = "doc-title-fr";
    public const string FileField = "doc-file";
    public const string EffectiveFromField = "doc-effective-from";

    /// <summary>The kinds of file a version is accepted as, for a picker: « .pdf,.jpg,… ».</summary>
    public static string AcceptedExtensions =>
        string.Join(",", SafeDocumentFile.PickerPattern.Split(';').Select(p => p.TrimStart('*')));

    /// <summary>The versions still on their way into force: one at a time.</summary>
    private static bool InProgress(DocumentVersionStatus? status) =>
        status is DocumentVersionStatus.Draft or DocumentVersionStatus.UnderReview
               or DocumentVersionStatus.Reviewed or DocumentVersionStatus.Approved;

    // ---- Rights and states ------------------------------------------------------------------------

    public static QualityDocumentScreenActions Actions(ICurrentUser user, ControlledDocumentRow? document, DocumentVersionRow? version)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Actions(user.Has, document, version);
    }

    /// <summary>
    /// A retired document is kept for reference only: its versions no longer move until it is
    /// reactivated. One version is in progress at a time, so a new one waits for the last to be put
    /// in force. Reading and acknowledging take only the right to see the quality screens.
    /// </summary>
    public static QualityDocumentScreenActions Actions(Func<string, bool> has, ControlledDocumentRow? document, DocumentVersionRow? version)
    {
        ArgumentNullException.ThrowIfNull(has);

        var manage = has(Permissions.Quality.ManageDocuments);
        var approve = has(Permissions.Quality.ApproveDocument);
        var active = document is { IsActive: true };
        var status = version?.Status;

        return new QualityDocumentScreenActions(
            NewDocument: manage,
            NewVersion: manage && active && !InProgress(document!.LatestStatus),
            Submit: manage && active && status == DocumentVersionStatus.Draft,
            Review: manage && active && status == DocumentVersionStatus.UnderReview,
            Approve: approve && active && status is DocumentVersionStatus.UnderReview or DocumentVersionStatus.Reviewed,
            MakeEffective: approve && active && status == DocumentVersionStatus.Approved,
            Open: version is { HasContent: true },
            Acknowledge: document is { IsActive: true, EffectiveVersion: not null, AcknowledgedByCurrentUser: false },
            Withdraw: approve && document is not null,
            WithdrawKey: WithdrawKey(document));
    }

    public static string WithdrawKey(ControlledDocumentRow? document) =>
        document is { IsActive: false } ? "Documents.Reactivate" : "Documents.Withdraw";

    /// <summary>The version the steps act on: the one selected, else the newest — the one work happens on.</summary>
    public static DocumentVersionRow? SelectedVersion(IReadOnlyList<DocumentVersionRow> versions, Guid? selected)
    {
        ArgumentNullException.ThrowIfNull(versions);
        return versions.FirstOrDefault(v => v.Id == selected) ?? (versions.Count > 0 ? versions[^1] : null);
    }

    /// <summary>The document kept selected after a reload: the one worked on, else the first.</summary>
    public static ControlledDocumentRow? Keep(IReadOnlyList<ControlledDocumentRow> rows, Guid? kept)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.FirstOrDefault(r => r.Id == kept) ?? (rows.Count > 0 ? rows[0] : null);
    }

    // ---- The register -------------------------------------------------------------------------------

    public static string Date(DateOnly? date) =>
        date?.ToString("dd/MM/yy", CultureInfo.InvariantCulture) ?? "—";

    public static string Date(DateTimeOffset date) => Date(DateOnly.FromDateTime(date.LocalDateTime));

    /// <summary>« v3 », or « — » when nothing is in force.</summary>
    public static string Effective(ControlledDocumentRow row) => row.EffectiveVersion is { } v ? $"v{v}" : "—";

    public static string EffectiveFrom(ControlledDocumentRow row) => Date(row.EffectiveFrom);

    /// <summary>The next review, « ! » before it once overdue.</summary>
    public static string NextReview(ControlledDocumentRow row) => row.NextReviewDue is { } due
        ? row.IsReviewOverdue ? $"! {Date(due)}" : Date(due)
        : "—";

    /// <summary>« v4 · En revue »: the newest version, when it is not the one in force.</summary>
    public static string Latest(ControlledDocumentRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return row.LatestVersion is { } latest && latest != row.EffectiveVersion
            ? $"v{latest} · {text($"DocumentVersionStatus.{row.LatestStatus}")}"
            : string.Empty;
    }

    /// <summary>« ✓ » read, « ! » to read; nothing when the document asks no acknowledgement or nothing is in force.</summary>
    public static string Read(ControlledDocumentRow row) =>
        !row.RequiresAcknowledgement || row.EffectiveVersion is null ? string.Empty
        : row.AcknowledgedByCurrentUser ? "✓" : "!";

    /// <summary>
    /// The ink of a cell: a retired document muted; an overdue review on its review date, nothing in
    /// force on the version in force and its date, and either on the reference — in the warning ink.
    /// </summary>
    /// <param name="column">reference, effective, effectiveFrom, review — any other is plain.</param>
    public static QualityInk Ink(ControlledDocumentRow row, string column)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.IsActive) return QualityInk.Muted;

        var warn = column switch
        {
            "review" => row.IsReviewOverdue,
            "effective" or "effectiveFrom" => row.EffectiveVersion is null,
            "reference" => row.IsReviewOverdue || row.EffectiveVersion is null,
            _ => false
        };
        return warn ? QualityInk.Abnormal : QualityInk.Plain;
    }

    public static QualityTone VersionTone(DocumentVersionStatus status) => status switch
    {
        DocumentVersionStatus.Effective => QualityTone.Ok,
        DocumentVersionStatus.Approved or DocumentVersionStatus.Reviewed or DocumentVersionStatus.Scheduled => QualityTone.Info,
        DocumentVersionStatus.UnderReview => QualityTone.Warning,
        DocumentVersionStatus.Draft => QualityTone.Waiting,
        _ => QualityTone.Muted
    };

    /// <summary>« 12 documents · Revue en retard 2 · À lire 1 ».</summary>
    public static string Count(IReadOnlyCollection<ControlledDocumentRow> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);

        var overdue = rows.Count(r => r.IsActive && r.IsReviewOverdue);
        var unread = rows.Count(r => r.RequiresAcknowledgement && r.EffectiveVersion is not null && !r.AcknowledgedByCurrentUser);

        var line = text("QualityHr.Documents.Count", rows.Count);
        if (overdue > 0) line += $" · {text("Documents.ReviewOverdue")} {overdue.ToString(CultureInfo.InvariantCulture)}";
        if (unread > 0) line += $" · {text("Documents.ToRead")} {unread.ToString(CultureInfo.InvariantCulture)}";
        return line;
    }

    /// <summary>Why the selected document needs attention: staff are working from a text the laboratory has not currently vouched for.</summary>
    public static QualityDocumentWarning? Warning(ControlledDocumentRow? document) => document switch
    {
        null => null,
        { EffectiveVersion: null } => new QualityDocumentWarning("Documents.NoEffectiveVersionWarning", true),
        { IsReviewOverdue: true } => new QualityDocumentWarning("Documents.ReviewOverdueWarning", true),
        _ => null
    };

    /// <summary>« Versions — PR-PRE-001 ».</summary>
    public static string VersionsHeading(ControlledDocumentRow? document, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return document is null ? text("Documents.Versions") : $"{text("Documents.Versions")} — {document.Reference}";
    }

    // ---- A new document --------------------------------------------------------------------------------

    public static FieldRefusal? Check(ControlledDocumentForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (string.IsNullOrWhiteSpace(form.Reference)) return new FieldRefusal(ReferenceField, "Document_ReferenceRequired");
        if (string.IsNullOrWhiteSpace(form.TitleFr)) return new FieldRefusal(TitleFrField, "Document_TitleRequired");
        return null;
    }

    public static CreateControlledDocumentRequest ToRequest(ControlledDocumentForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return new CreateControlledDocumentRequest
        {
            Reference = form.Reference!.Trim(),
            Title = new MultilingualText(form.TitleFr!.Trim(), Blank(form.TitleAr), Blank(form.TitleEn)),
            Kind = form.Kind,
            ProcessCode = Blank(form.Process),
            ReviewPeriodMonths = Math.Clamp(form.ReviewPeriodMonths, ReviewPeriodMin, ReviewPeriodMax),
            RequiresAcknowledgement = form.RequiresAcknowledgement
        };
    }

    /// <summary>The box a refusal of DocumentControlService names.</summary>
    public static string? FieldOf(Error error) => error?.ResourceKey switch
    {
        "Document_ReferenceRequired" or "Document_ReferenceAlreadyTaken" => ReferenceField,
        "Document_TitleRequired" => TitleFrField,
        "Document_FileType" or "Document_ContentRequired" or "Document_ContentTooLarge" => FileField,
        "Document_EffectiveDateInPast" => EffectiveFromField,
        _ => null
    };

    // ---- A new version ---------------------------------------------------------------------------------

    /// <summary>
    /// The file of a draft: one is chosen, of a kind the laboratory keeps (PDF, images, Word,
    /// Excel), and not past the size the service accepts.
    /// </summary>
    /// <param name="fileName">The name of the file chosen, null when none is.</param>
    /// <param name="length">Its size in bytes.</param>
    public static FieldRefusal? CheckFile(string? fileName, long length)
    {
        if (string.IsNullOrWhiteSpace(fileName) || length <= 0) return new FieldRefusal(FileField, "Document_ContentRequired");
        if (SafeDocumentFile.ContentTypeFor(fileName) is null) return new FieldRefusal(FileField, "Document_FileType");
        if (length > MaximumContentBytes)
            return new FieldRefusal(FileField, "Document_ContentTooLarge",
                                    (MaximumContentBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture));
        return null;
    }

    /// <summary>What changed, in French and in Arabic; none is an empty summary.</summary>
    public static MultilingualText? ChangeSummary(string? french, string? arabic) =>
        MultilingualText.FromOptional(french, arabic);

    /// <summary>A new document has no text yet: the next thing to do is attach its first draft.</summary>
    public static bool AsksForFirstVersion(QualityDocumentScreenActions actions) => actions?.NewVersion == true;

    // ---- Steps -----------------------------------------------------------------------------------------

    /// <summary>The earliest date a version comes into force: today, never back-dated.</summary>
    public static DateOnly EarliestEffective(DateOnly today) => today;

    /// <summary>« Remplace la version 2. » before the notice when a version is in force.</summary>
    public static string EffectiveNotice(int? replacedVersion, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var notice = text("Documents.MakeEffectiveNotice");
        return replacedVersion is { } previous ? text("Documents.ReplacesVersion", previous) + " " + notice : notice;
    }

    /// <summary>« PR-PRE-001 v3 »: what a step acts on.</summary>
    public static string Subject(ControlledDocumentRow document, DocumentVersionRow version) =>
        $"{document.Reference} v{version.VersionNumber}";

    public static FieldRefusal? CheckEffective(DateOnly effectiveFrom, DateOnly today) =>
        effectiveFrom < today ? new FieldRefusal(EffectiveFromField, "Document_EffectiveDateInPast") : null;

    /// <summary>The prompt of a withdrawal or a reactivation, by the document's state.</summary>
    public static string WithdrawPromptKey(ControlledDocumentRow document) =>
        document.IsActive ? "Documents.WithdrawPrompt" : "Documents.ReactivatePrompt";

    /// <summary>The name a version's file is handed over as: its own, else the reference and version with the stored type's extension.</summary>
    public static string DownloadName(ControlledDocumentRow document, DocumentVersionRow version, DocumentContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return string.IsNullOrWhiteSpace(content.FileName)
            ? $"{document.Reference}-v{version.VersionNumber}.bin"
            : Path.GetFileName(content.FileName);
    }

    /// <summary>The type the file is handed over with: the stored one, else the one its name says.</summary>
    public static string ContentType(DocumentContent content) =>
        content.ContentType ?? SafeDocumentFile.ContentTypeFor(content.FileName) ?? "application/octet-stream";

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
