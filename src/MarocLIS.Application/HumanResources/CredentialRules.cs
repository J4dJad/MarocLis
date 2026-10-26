using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.HumanResources;

/// <summary>A document of a personnel file as typed: a diploma, a training, a vaccination, a medical visit.</summary>
public sealed class CredentialForm
{
    public CredentialKind Kind { get; set; } = CredentialKind.Diploma;
    public MedicalFitness Fitness { get; set; } = MedicalFitness.Fit;
    public string Title { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? IssuedBy { get; set; }
    public DateOnly? ObtainedOn { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public string? Note { get; set; }

    /// <summary>The title the kind proposed last: replaced when the kind changes, unlike one typed.</summary>
    public string Suggested { get; set; } = string.Empty;
}

/// <summary>What the documents of a file offer on the document selected.</summary>
public sealed record CredentialActions(bool Add, bool Edit, bool Attach, bool Open, bool Remove);

/// <summary>
/// The rules of a file's documents that need no window: how a document reads (expired or unfit in
/// the alarm's colours), the actions by right and by document, the form and its title proposed by
/// the kind, the fitness only for a medical visit, the check before it is sent, and the scan's kind
/// and size. The workstation's CredentialsDialog and CredentialEditorDialog and the web's documents
/// tab apply the same ones; what is saved is PersonnelService's.
/// </summary>
public static class CredentialRules
{
    public const string KindField = "credential-kind";
    public const string FitnessField = "credential-fitness";
    public const string TitleField = "credential-title";
    public const string ReferenceField = "credential-reference";
    public const string IssuedByField = "credential-issued-by";
    public const string ObtainedField = "credential-obtained";
    public const string ExpiresField = "credential-expires";
    public const string NoteField = "credential-note";
    public const string ScanField = "credential-scan";

    public const int TitleMaxLength = 200;
    public const int IssuedByMaxLength = 200;
    public const int ReferenceMaxLength = 60;
    public const int NoteMaxLength = 500;

    /// <summary>The reason a document is taken off the file with, kept in the audit trail.</summary>
    public const int RemoveReasonMaxLength = 500;

    /// <summary>What a scanner or a phone produces: the extensions the file picker offers.</summary>
    public const string ScanAccept = ".pdf,.jpg,.jpeg,.png,.tif,.tiff";

    /// <summary>An expired document, or a medical visit that found the employee unfit.</summary>
    public static bool IsAlarm(CredentialRow row, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.ExpiresOn is { } until && until < today || row.Fitness == MedicalFitness.Unfit;
    }

    public static string Kind(CredentialRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return text($"CredentialKind.{row.Kind}");
    }

    /// <summary>« Joint » when a scan is kept with the document, a dash otherwise.</summary>
    public static string Scan(CredentialRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return row.FileName is null ? "—" : text("Credential.HasScan");
    }

    /// <summary>The note, the fitness of a medical visit first: « Apte · revoir en mars ».</summary>
    public static string Note(CredentialRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return row.Fitness is { } fitness
            ? $"{text($"MedicalFitness.{fitness}")}{(row.Note is null ? string.Empty : " · " + row.Note)}"
            : row.Note ?? string.Empty;
    }

    /// <summary>
    /// The documents are changed by whoever may edit files, and only one at a time; a scan is opened
    /// by whoever may read them.
    /// </summary>
    public static CredentialActions Actions(CredentialRow? row, bool busy, Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(has);
        var manage = has(Permissions.HumanResources.Manage) && !busy;
        return new CredentialActions(
            Add: manage,
            Edit: manage && row is not null,
            Attach: manage && row is not null,
            Open: !busy && row?.FileName is not null && has(Permissions.HumanResources.View),
            Remove: manage && row is not null);
    }

    public static string TitleKey(bool isNew) => isNew ? "Credential.Add" : "Credential.Edit";

    /// <summary>A new document: a diploma obtained today; or the one selected, as it is.</summary>
    public static CredentialForm FormFor(CredentialRow? existing, DateOnly today, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (existing is null)
        {
            var form = new CredentialForm { ObtainedOn = today };
            KindChanged(form, text);
            return form;
        }

        return new CredentialForm
        {
            Kind = existing.Kind,
            Fitness = existing.Fitness ?? MedicalFitness.Fit,
            Title = existing.Title,
            Reference = existing.Reference,
            IssuedBy = existing.IssuedBy,
            ObtainedOn = existing.ObtainedOn,
            ExpiresOn = existing.ExpiresOn,
            Note = existing.Note,
            Suggested = existing.Title
        };
    }

    /// <summary>The fitness decision belongs to a medical visit only.</summary>
    public static bool AsksFitness(CredentialKind kind) => kind == CredentialKind.MedicalVisit;

    /// <summary>
    /// Proposes a title for the kind (« Vaccination hépatite B »), without overwriting one the
    /// operator typed; a diploma, a training or another document has its own title to type.
    /// </summary>
    public static void KindChanged(CredentialForm form, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(text);

        var suggestion = form.Kind is CredentialKind.Other or CredentialKind.Diploma or CredentialKind.ContinuingEducation
            ? string.Empty
            : text($"CredentialKind.{form.Kind}");

        if (form.Title.Length == 0 || form.Title == form.Suggested) form.Title = suggestion;
        form.Suggested = suggestion;
    }

    /// <summary>The title required; the fitness sent only for a medical visit.</summary>
    public static (FieldRefusal? Refusal, CredentialRequest? Request) Check(CredentialForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (string.IsNullOrWhiteSpace(form.Title))
            return (new FieldRefusal(TitleField, "Credential_TitleRequired"), null);

        if (form.ObtainedOn is { } obtained && form.ExpiresOn is { } expires && expires < obtained)
            return (new FieldRefusal(ExpiresField, "Credential_ExpiryBeforeIssue"), null);

        return (null, new CredentialRequest
        {
            Kind = form.Kind,
            Title = form.Title.Trim(),
            Reference = Blank(form.Reference),
            IssuedBy = Blank(form.IssuedBy),
            ObtainedOn = form.ObtainedOn,
            ExpiresOn = form.ExpiresOn,
            Fitness = AsksFitness(form.Kind) ? form.Fitness : null,
            Note = Blank(form.Note)
        });
    }

    /// <summary>A scan picked: a PDF or a picture, no larger than the database keeps.</summary>
    public static FieldRefusal? CheckScan(string fileName, long size)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (size <= 0) return new FieldRefusal(ScanField, "Credential_FileEmpty");
        if (CredentialFile.ContentTypeOf(fileName) is null) return new FieldRefusal(ScanField, "Credential_FileType");
        if (size > CredentialFile.MaximumBytes)
            return new FieldRefusal(ScanField, "Credential_FileTooLarge", (CredentialFile.MaximumBytes / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return null;
    }

    /// <summary>The box a refusal of the service names, when it names one.</summary>
    public static string? FieldOf(string errorKey) => errorKey switch
    {
        "Credential_TitleRequired" => TitleField,
        "Credential_ExpiryBeforeIssue" => ExpiresField,
        "Credential_FitnessRequired" => FitnessField,
        "Credential_FileEmpty" or "Credential_FileType" or "Credential_FileTooLarge" => ScanField,
        _ => null
    };

    public static FieldRefusal? RefusalOf(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return FieldOf(error.ResourceKey) is { } field ? HrRefusals.Of(field, error) : null;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
