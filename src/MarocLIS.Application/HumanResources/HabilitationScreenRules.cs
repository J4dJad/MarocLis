using System.Globalization;
using System.Text;
using MarocLIS.Application.Input;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.HumanResources;

/// <summary>A column of the matrix: an activity, on an analyser or in a discipline when it concerns one.</summary>
public sealed record HabilitationColumn(HabilitationActivity Activity, Guid? SubjectId, string? Label);

/// <summary>How a cell of the matrix reads: the review overdue in the alarm's colours, in training in the warning's.</summary>
public enum HabilitationInk
{
    None = 0,
    Held = 1,
    InTraining = 2,
    ReviewOverdue = 3
}

/// <summary>A habilitation decision as typed: what, on which analyser or discipline, at what level, reviewed when.</summary>
public sealed class HabilitationForm
{
    public HabilitationActivity Activity { get; set; } = HabilitationActivity.AdultCollection;
    public Guid? SubjectId { get; set; }
    public HabilitationLevel Level { get; set; } = HabilitationLevel.Authorized;
    public DateOnly GrantedOn { get; set; }
    public DateOnly? ReviewDueOn { get; set; }
    public string? Evidence { get; set; }
}

/// <summary>A habilitation as sent to <see cref="PersonnelService.GrantAsync"/>.</summary>
public sealed record HabilitationGrant(HabilitationActivity Activity, Guid? SubjectId, HabilitationLevel Level,
                                       DateOnly GrantedOn, DateOnly? ReviewDueOn, string? Evidence);

/// <summary>
/// The rules of « Habilitations » that need no window: the matrix's columns (every activity anyone
/// holds, the collection ones always), what a cell says and how it reads, the count, the detail of
/// the cell selected, the grant's form and its check, the withdrawal's prompt, and the CSV export for
/// the accreditation file. The workstation's HabilitationView and HabilitationGrantDialog and the
/// web's screen apply the same ones; what is done is PersonnelService's.
/// </summary>
public static class HabilitationScreenRules
{
    public const string ActivityField = "habilitation-activity";
    public const string SubjectField = "habilitation-subject";
    public const string LevelField = "habilitation-level";
    public const string GrantedOnField = "habilitation-granted-on";
    public const string ReviewField = "habilitation-review";
    public const string EvidenceField = "habilitation-evidence";

    public const int EvidenceMaxLength = 500;

    /// <summary>The reason a habilitation is withdrawn with, kept with it.</summary>
    public const int WithdrawReasonMaxLength = 500;

    /// <summary>
    /// Every activity anyone holds gets a column; the collection activities are always there, as
    /// they are the ones a new préleveur is placed on first.
    /// </summary>
    public static IReadOnlyList<HabilitationColumn> Columns(HabilitationMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        return
        [
            .. matrix.Habilitations.Select(h => new HabilitationColumn(h.Activity, h.SubjectId, h.SubjectLabel))
                .Concat(new[] { HabilitationActivity.AdultCollection, HabilitationActivity.PaediatricCollection }
                            .Select(a => new HabilitationColumn(a, null, null)))
                .Distinct()
                .OrderBy(c => c.Activity).ThenBy(c => c.Label, StringComparer.CurrentCulture)
        ];
    }

    /// <summary>
    /// The activities of the matrix in their order, one column each where the analysers and the
    /// disciplines of an activity share its cell (the web's matrix, which fits its page however
    /// many analysers the laboratory runs).
    /// </summary>
    public static IReadOnlyList<HabilitationActivity> Activities(HabilitationMatrix matrix) =>
        [.. Columns(matrix).Select(c => c.Activity).Distinct()];

    /// <summary>What <paramref name="employeeId"/> holds in <paramref name="activity"/>: one line per analyser or discipline.</summary>
    public static IReadOnlyList<HabilitationRow> HeldIn(HabilitationMatrix matrix, Guid employeeId, HabilitationActivity activity)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        return [.. matrix.Habilitations.Where(h => h.EmployeeId == employeeId && h.Activity == activity)
                                       .OrderBy(h => h.SubjectLabel ?? string.Empty, StringComparer.CurrentCulture)];
    }

    /// <summary>The column a grant opens on: the activity, and the subject of the habilitation selected in it.</summary>
    public static HabilitationColumn ColumnOf(HabilitationActivity activity, HabilitationRow? held) =>
        held?.Activity == activity
            ? new HabilitationColumn(activity, held.SubjectId, held.SubjectLabel)
            : new HabilitationColumn(activity, null, null);

    /// <summary>The habilitation <paramref name="employeeId"/> holds in <paramref name="column"/>, if any.</summary>
    public static HabilitationRow? Held(HabilitationMatrix matrix, Guid employeeId, HabilitationColumn column)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(column);
        return matrix.Habilitations.FirstOrDefault(h => h.EmployeeId == employeeId && h.Activity == column.Activity
                                                        && h.SubjectId == column.SubjectId);
    }

    /// <summary>The column's heading: the activity, and the analyser or the discipline on the line under it.</summary>
    public static (string Activity, string? Subject) Header(HabilitationColumn column, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(text);
        return (text($"HabilitationActivity.{column.Activity}"), column.Label);
    }

    /// <summary>The heading on one text, its two parts on two lines (the workstation's grid header).</summary>
    public static string HeaderText(HabilitationColumn column, ScreenText text)
    {
        var (activity, subject) = Header(column, text);
        return subject is null ? activity : $"{activity}\n{subject}";
    }

    /// <summary>The level in a narrow cell (« H · 10/27 »), the workstation's grid.</summary>
    public static string ShortCell(HabilitationRow h, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentNullException.ThrowIfNull(text);
        return $"{text($"HabilitationLevelShort.{h.Level}")}{(h.ReviewDueOn is { } due ? $" · {due.ToString("MM/yy", CultureInfo.InvariantCulture)}" : string.Empty)}";
    }

    /// <summary>The level in words, as the web's cell says it.</summary>
    public static string Level(HabilitationRow h, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentNullException.ThrowIfNull(text);
        return text($"HabilitationLevel.{h.Level}");
    }

    /// <summary>The review date of a cell, empty when none is set.</summary>
    public static string ReviewDue(HabilitationRow h)
    {
        ArgumentNullException.ThrowIfNull(h);
        return PersonnelScreenRules.Date(h.ReviewDueOn);
    }

    /// <summary>The review overdue leads; then a person still in training; else the habilitation held.</summary>
    public static HabilitationInk Ink(HabilitationRow? h, DateOnly today) =>
        h is null ? HabilitationInk.None
        : h.ReviewDueOn is { } due && due < today ? HabilitationInk.ReviewOverdue
        : h.Level == HabilitationLevel.InTraining ? HabilitationInk.InTraining
        : HabilitationInk.Held;

    /// <summary>The cell's pill tone: Critical, Warning or Ok.</summary>
    public static string Tone(HabilitationInk ink) => ink switch
    {
        HabilitationInk.ReviewOverdue => "Critical",
        HabilitationInk.InTraining => "Warning",
        _ => "Ok"
    };

    /// <summary>The habilitations whose review is due before <paramref name="today"/>.</summary>
    public static int Overdue(HabilitationMatrix matrix, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        return matrix.Habilitations.Count(h => h.ReviewDueOn is { } d && d < today);
    }

    /// <summary>The head's count: the employees, the habilitations and the reviews overdue.</summary>
    public static string Count(HabilitationMatrix matrix, DateOnly today, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(text);
        return text("QualityHr.Habilitations.Count", matrix.Employees.Count, matrix.Habilitations.Count, Overdue(matrix, today));
    }

    /// <summary>
    /// What the cell selected says: its level, since when and signed by whom, the review and the
    /// evidence; on an empty matrix, how to start one.
    /// </summary>
    public static string Detail(HabilitationMatrix? matrix, HabilitationRow? h, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (h is not null)
            return text("Habilitation.Detail", text($"HabilitationLevel.{h.Level}"), PersonnelScreenRules.Date(h.GrantedOn),
                        h.GrantedBy, PersonnelScreenRules.DateOrDash(h.ReviewDueOn), h.Evidence ?? "—");
        return matrix is { Habilitations.Count: 0 } ? text("Habilitation.Empty") : string.Empty;
    }

    /// <summary>Granting needs the habilitation right and an employee; withdrawing, a habilitation selected too.</summary>
    public static (bool Grant, bool Withdraw, bool Export) Actions(HabilitationMatrix? matrix, EmployeeRow? employee,
                                                                   HabilitationRow? held, Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(has);
        var may = has(Permissions.HumanResources.ManageHabilitations);
        return (may && employee is not null, may && employee is not null && held is not null,
                matrix is { Employees.Count: > 0 });
    }

    /// <summary>The prompt of a withdrawal: which activity, of whom.</summary>
    public static string WithdrawPrompt(HabilitationRow h, EmployeeRow employee, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentNullException.ThrowIfNull(employee);
        ArgumentNullException.ThrowIfNull(text);
        return text("Habilitation.WithdrawPrompt", text($"HabilitationActivity.{h.Activity}"), employee.FullName);
    }

    // ---- The grant ----------------------------------------------------------------------------------

    /// <summary>
    /// The grant opened on the column selected (adult collection from the name columns), habilitated
    /// from today, reviewed in a year.
    /// </summary>
    public static HabilitationForm FormFor(HabilitationColumn? column, PersonnelLookups lookups, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(lookups);
        var form = new HabilitationForm
        {
            Activity = column?.Activity ?? HabilitationActivity.AdultCollection,
            SubjectId = column?.SubjectId,
            GrantedOn = today,
            ReviewDueOn = today.AddYears(1)
        };
        ActivityChanged(form, lookups);
        return form;
    }

    /// <summary>An activity exercised on one analyser or in one discipline names it; the others name none.</summary>
    public static bool AsksSubject(HabilitationActivity activity) => EmployeeHabilitation.RequiresSubject(activity);

    /// <summary>The analysers for the use of one, the disciplines for the other activities that name a subject.</summary>
    public static IReadOnlyList<HabilitationSubject> Subjects(HabilitationActivity activity, PersonnelLookups lookups)
    {
        ArgumentNullException.ThrowIfNull(lookups);
        if (!AsksSubject(activity)) return [];
        return EmployeeHabilitation.SubjectIsInstrument(activity) ? lookups.Instruments : lookups.Departments;
    }

    /// <summary>The subject kept when it is one of the activity's, else the first offered.</summary>
    public static void ActivityChanged(HabilitationForm form, PersonnelLookups lookups)
    {
        ArgumentNullException.ThrowIfNull(form);
        var subjects = Subjects(form.Activity, lookups);
        form.SubjectId = subjects.Count == 0 ? null
            : subjects.Any(s => s.Id == form.SubjectId) ? form.SubjectId
            : subjects[0].Id;
    }

    /// <summary>The subject required where the activity names one, the decision not in the future, the review after it.</summary>
    public static (FieldRefusal? Refusal, HabilitationGrant? Grant) Check(HabilitationForm form, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (AsksSubject(form.Activity) && form.SubjectId is null)
            return (new FieldRefusal(SubjectField, "Habilitation_SubjectRequired"), null);
        if (form.GrantedOn > today)
            return (new FieldRefusal(GrantedOnField, "Habilitation_GrantInFuture"), null);
        if (form.ReviewDueOn is { } review && review < form.GrantedOn)
            return (new FieldRefusal(ReviewField, "Habilitation_ReviewBeforeGrant"), null);

        return (null, new HabilitationGrant(form.Activity, AsksSubject(form.Activity) ? form.SubjectId : null, form.Level,
                                            form.GrantedOn, form.ReviewDueOn,
                                            string.IsNullOrWhiteSpace(form.Evidence) ? null : form.Evidence.Trim()));
    }

    /// <summary>The box a refusal of the service names, when it names one.</summary>
    public static string? FieldOf(string errorKey) => errorKey switch
    {
        "Habilitation_SubjectRequired" => SubjectField,
        "Habilitation_GrantInFuture" or "Habilitation_BeforeHire" => GrantedOnField,
        "Habilitation_ReviewBeforeGrant" => ReviewField,
        "Habilitation_BiologistSignatureRequired" => LevelField,
        "Habilitation_BiologicalValidationBiologistOnly" => ActivityField,
        _ => null
    };

    public static FieldRefusal? RefusalOf(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return FieldOf(error.ResourceKey) is { } field ? HrRefusals.Of(field, error) : null;
    }

    // ---- The export ---------------------------------------------------------------------------------

    /// <summary>
    /// The matrix as CSV, one line per habilitation, for the accreditation file: UTF-8 with its mark
    /// so a spreadsheet reads the accents, and every cell typed by a user neutralised for Excel.
    /// </summary>
    public static byte[] ExportCsv(HabilitationMatrix matrix, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(text);

        static string Csv(string? value) => CsvCell.Of(value);

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(';', new[]
        {
            text("Personnel.Number"), text("Field.Name"), text("Personnel.Job"), text("Habilitation.Activity"),
            text("Habilitation.Subject"), text("Habilitation.Level"), text("Habilitation.GrantedOn"),
            text("Habilitation.GrantedBy"), text("Habilitation.ReviewDue"), text("Habilitation.Evidence")
        }.Select(Csv)));

        foreach (var employee in matrix.Employees)
        foreach (var h in matrix.Habilitations.Where(x => x.EmployeeId == employee.EmployeeId).OrderBy(x => x.Activity))
        {
            csv.AppendLine(string.Join(';', new[]
            {
                employee.Number, employee.FullName, text($"EmployeeJob.{employee.Job}"),
                text($"HabilitationActivity.{h.Activity}"), h.SubjectLabel, text($"HabilitationLevel.{h.Level}"),
                PersonnelScreenRules.Date(h.GrantedOn), h.GrantedBy, PersonnelScreenRules.Date(h.ReviewDueOn), h.Evidence
            }.Select(Csv)));
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    /// <summary>The export's name when the browser saves it: « habilitations-2026-10-26.csv ».</summary>
    public static string ExportFileName(DateOnly today) =>
        $"habilitations-{today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.csv";
}
