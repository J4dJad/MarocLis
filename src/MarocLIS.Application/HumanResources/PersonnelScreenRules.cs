using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.HumanResources;

/// <summary>How a line of the staff list reads: a departure taken effect muted, a contract over in the alarm's colours.</summary>
public enum PersonnelInk
{
    Plain = 0,
    Departed = 1,
    ContractOver = 2
}

/// <summary>
/// What the departure action does on the file selected: record an exit; change one not reached yet
/// (correct or withdraw it); re-hire once it has taken effect, the only time a re-hire can succeed.
/// </summary>
public enum ExitStep
{
    Record = 0,
    Change = 1,
    Rehire = 2
}

/// <summary>A state as a pill: its words and its tone (Ok, Info, Warning, Critical, Muted, Waiting).</summary>
public sealed record HrPill(string Text, string Tone);

/// <summary>One fact of a card: its label, its value, and whether the value is a code or a date (set in the fixed font).</summary>
public sealed record HrFact(string Label, string Value, bool Mono);

/// <summary>
/// What « Dossiers du personnel » offers on the file selected, by right and by file: the file
/// itself, its documents, its departure, the letters and the final settlement.
/// </summary>
public sealed record PersonnelActions(
    bool New,
    bool Edit,
    bool Documents,
    bool Exit,
    ExitStep ExitStep,
    bool Letters,
    bool EmploymentLetter,
    bool SalaryLetter,
    bool Certificate,
    bool Settlement)
{
    /// <summary>The departure step's caption (and, on both clients, its icon).</summary>
    public string ExitKey => PersonnelScreenRules.ExitKey(ExitStep);
}

/// <summary>
/// The rules of « Dossiers du personnel » that need no window: the search, how a line and the
/// selected file read, the actions offered by right and by file, the departure's step, and what a
/// departure did to the linked LIS account. The workstation's PersonnelView and the web's screen
/// apply the same ones; what is done is PersonnelService's, HrDocumentService's and SettlementService's.
/// </summary>
public static class PersonnelScreenRules
{
    /// <summary>The search box: a name, a number or a job title.</summary>
    public const int SearchMaxLength = 100;

    /// <summary>The reason a departure is withdrawn with, kept in the audit trail.</summary>
    public const int CancelExitReasonMaxLength = 500;

    /// <summary>A date of these screens, as the workstation writes it whatever the language.</summary>
    public static string Date(DateOnly? date) =>
        date?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>A date of a list or a card, a dash when there is none.</summary>
    public static string DateOrDash(DateOnly? date) => date is null ? "—" : Date(date);

    /// <summary>On the staff on <paramref name="today"/>: no departure, or one not reached yet (the notice being served).</summary>
    public static bool OnStaff(EmployeeRow row, DateOnly today) => PersonnelRules.OnStaff(row, today);

    /// <summary>The lines the search keeps: the name, the number or the job title holding the text typed.</summary>
    public static IReadOnlyList<EmployeeRow> Shown(IReadOnlyList<EmployeeRow> rows, string? search)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var term = search?.Trim() ?? string.Empty;
        return term.Length == 0
            ? rows
            : [.. rows.Where(r => r.FullName.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                                  || r.Number.Contains(term, StringComparison.OrdinalIgnoreCase)
                                  || (r.JobTitle?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false))];
    }

    /// <summary>A departure taken effect muted; a contract whose end has passed in the alarm's colours.</summary>
    public static PersonnelInk Ink(EmployeeRow row, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !OnStaff(row, today) ? PersonnelInk.Departed
            : row.ContractEndDate is { } end && end < today ? PersonnelInk.ContractOver
            : PersonnelInk.Plain;
    }

    /// <summary>The name in the list, with the departure when there is one: « BOUZID Hanane  (sortie prévue le 30/11/2026) ».</summary>
    public static string NameLine(EmployeeRow row, DateOnly today, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return row.ExitDate is { } left
            ? $"{row.FullName}  ({Departure(row, today, text)})"
            : row.FullName;
    }

    /// <summary>« sortie prévue le … » for a departure to come, « sorti le … » for one taken effect; empty without one.</summary>
    public static string Departure(EmployeeRow row, DateOnly today, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return row.ExitDate is { } left
            ? text(OnStaff(row, today) ? "Personnel.LeavingOn" : "Personnel.ExitedOn", Date(left))
            : string.Empty;
    }

    /// <summary>The head's count: the people on the staff, then the lines shown.</summary>
    public static string Count(IReadOnlyCollection<EmployeeRow> rows, int shown, DateOnly today, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        return text("QualityHr.Personnel.Count", rows.Count(r => OnStaff(r, today)), shown);
    }

    /// <summary>
    /// Where the file stands, as a pill: a departure to come (warning), one taken effect (muted), a
    /// contract over (alarm), else the contract (ok).
    /// </summary>
    public static HrPill Pill(EmployeeRow row, DateOnly today, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return row.ExitDate switch
        {
            { } exit when exit >= today => new HrPill(text("Personnel.LeavingOn", Date(exit)), "Warning"),
            { } exit => new HrPill(text("Personnel.ExitedOn", Date(exit)), "Muted"),
            _ when row.ContractEndDate is { } end && end < today =>
                new HrPill($"{text("Personnel.ContractEnd")} {Date(end)}", "Critical"),
            _ => new HrPill(text($"ContractType.{row.ContractType}"), "Ok")
        };
    }

    /// <summary>The line under the name: the job title and the department, when known.</summary>
    public static string Subtitle(EmployeeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return string.Join(" · ", new[] { row.JobTitle, row.DepartmentName }.Where(v => !string.IsNullOrWhiteSpace(v)));
    }

    /// <summary>The selected file's key facts: no personal data, which only the file itself shows to whoever may edit it.</summary>
    public static IReadOnlyList<HrFact> Facts(EmployeeRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            new(text("Personnel.Number"), row.Number, true),
            new(text("Personnel.Job"), text($"EmployeeJob.{row.Job}"), false),
            new(text("Personnel.ContractType"), text($"ContractType.{row.ContractType}"), false),
            new(text("Personnel.HireDate"), Date(row.HireDate), true),
            new(text("Personnel.ContractEnd"), DateOrDash(row.ContractEndDate), true),
            new(text("Personnel.LinkedUser"), row.UserName ?? "—", true)
        ];
    }

    /// <summary>The departure's step on <paramref name="row"/>: none yet, one not reached, or one taken effect.</summary>
    public static ExitStep StepOf(EmployeeRow? row, DateOnly today) => row?.ExitDate switch
    {
        null => ExitStep.Record,
        { } exit when exit >= today => ExitStep.Change,
        _ => ExitStep.Rehire
    };

    public static string ExitKey(ExitStep step) => step switch
    {
        ExitStep.Change => "Personnel.ChangeExit",
        ExitStep.Rehire => "Personnel.Rehire",
        _ => "Personnel.RecordExit"
    };

    /// <summary>
    /// By right and by file, as the workstation's gates and its menus: the file, its departure and
    /// the letters that state no salary need the personnel right; the salary attestation and the
    /// settlement the salary right. Serving a notice is still working here: the attestation de
    /// travail holds until the exit date, and the certificate can be prepared from the day the
    /// departure is known.
    /// </summary>
    public static PersonnelActions Actions(EmployeeRow? row, DateOnly today, Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(has);
        var manage = has(Permissions.HumanResources.Manage);
        var salary = has(Permissions.HumanResources.Salary);
        var chosen = row is not null;

        return new PersonnelActions(
            New: manage,
            Edit: manage && chosen,
            Documents: chosen && has(Permissions.HumanResources.View),
            Exit: manage && chosen,
            ExitStep: StepOf(row, today),
            Letters: (manage || salary) && chosen,
            EmploymentLetter: manage && row is not null && OnStaff(row, today),
            SalaryLetter: salary && chosen,
            Certificate: manage && row?.ExitDate is not null,
            Settlement: salary && row?.ExitDate is not null);
    }

    /// <summary>The prompt of a departure withdrawn: who, when, and what it undoes.</summary>
    public static string CancelExitPrompt(EmployeeRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return text("Personnel.CancelExitPrompt", row.FullName, Date(row.ExitDate));
    }

    /// <summary>« 0042 BOUZID Hanane »: who a departure, a re-hire or a settlement is about.</summary>
    public static string Who(EmployeeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"{row.Number} {row.FullName}";
    }

    /// <summary>
    /// What the departure did to the linked LIS account: deactivated now, to be on the exit date,
    /// left active as the last administrator (alarm), or still inactive (warning). Null when the file
    /// has no account.
    /// </summary>
    public static (string Text, string Tone)? AccountOutcome(DepartedAccountOutcome? outcome, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (outcome is null) return null;

        var date = Date(outcome.DeactivationDate);
        return outcome.State switch
        {
            DepartedAccountState.Deactivated => (text("Personnel.AccountDeactivated", outcome.UserName), "normal"),
            DepartedAccountState.Scheduled => (text("Personnel.AccountDeactivationScheduled", outcome.UserName, date), "normal"),
            DepartedAccountState.KeptLastAdministrator => (text("Personnel.AccountKeptLastAdministrator", outcome.UserName), "critical"),
            _ => (text("Personnel.AccountStillInactive", outcome.UserName), "abnormal")
        };
    }

    /// <summary>What saving a final settlement did to the payroll, and its tone (a closed month in the warning's).</summary>
    public static (string Text, string Tone) SettlementSaved(SettlementPayroll payroll, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return (text($"Settlement.Saved.{payroll}"), payroll == SettlementPayroll.Closed ? "abnormal" : "normal");
    }

    /// <summary>The PDF's name when the browser saves a letter: « 0042-attestation-de-travail.pdf ».</summary>
    public static string LetterFileName(EmployeeRow row, HrDocumentKind kind)
    {
        ArgumentNullException.ThrowIfNull(row);
        var what = kind switch
        {
            HrDocumentKind.SalaryAttestation => "attestation-salaire",
            HrDocumentKind.WorkCertificate => "certificat-travail",
            HrDocumentKind.SettlementReceipt => "recu-solde-tout-compte",
            _ => "attestation-travail"
        };
        return $"{row.Number}-{what}.pdf";
    }
}
