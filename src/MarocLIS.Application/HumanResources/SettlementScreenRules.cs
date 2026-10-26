using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Domain.HumanResources;

namespace MarocLIS.Application.HumanResources;

/// <summary>The final settlement as typed: every amount held as typed, so one that is no number is refused on its box.</summary>
public sealed class SettlementForm
{
    public string? ReferenceSalary { get; set; }
    public string? LeaveDays { get; set; }
    public string? LeaveIndemnity { get; set; }
    public bool NoticeNotWorked { get; set; }
    public string? NoticeIndemnity { get; set; }
    public string? Severance { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// The rules of the final settlement that need no window: the law's figures proposed (or the ones
/// saved), each amount put back to the law's when its basis changes, the total, the sentences that
/// explain them, the check before saving, and the date the receipt was signed. The workstation's
/// SettlementDialog and SettlementSignedDialog and the web's dialogs apply the same ones; what is
/// done is SettlementService's.
/// </summary>
public static class SettlementScreenRules
{
    public const string ReferenceField = "settlement-reference";
    public const string LeaveDaysField = "settlement-leave-days";
    public const string LeaveField = "settlement-leave";
    public const string NoticeNotWorkedField = "settlement-notice-not-worked";
    public const string NoticeField = "settlement-notice";
    public const string SeveranceField = "settlement-severance";
    public const string NoteField = "settlement-note";
    public const string SignedOnField = "settlement-signed-on";

    public const int AmountMaxLength = TypedNumber.AmountMaxLength;

    /// <summary>A decimal(9,2) column: seven figures before the comma.</summary>
    public const int LeaveDaysMaxLength = 7;

    public const int NoteMaxLength = 500;

    private static string N(decimal value, IFormatProvider? culture) => TypedNumber.ForEditing(value, 0, culture ?? CultureInfo.InvariantCulture);

    /// <summary>The amounts saved, or the law's when none are.</summary>
    public static SettlementForm FormFor(SettlementDetail detail, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var a = detail.Saved ?? detail.Suggested;
        return new SettlementForm
        {
            ReferenceSalary = N(a.ReferenceSalary, culture),
            LeaveDays = N(a.LeaveDays, culture),
            LeaveIndemnity = N(a.LeaveIndemnity, culture),
            NoticeNotWorked = a.NoticeNotWorked,
            NoticeIndemnity = N(a.NoticeIndemnity, culture),
            Severance = N(a.SeverancePay, culture),
            Note = a.Note
        };
    }

    /// <summary>Once the receipt is signed, the settlement is final: nothing can be changed.</summary>
    public static bool IsReadOnly(SettlementDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return detail.SignedOn is not null;
    }

    /// <summary>The notice indemnity is typed only when the notice was not worked.</summary>
    public static bool NoticeEditable(SettlementForm form, SettlementDetail detail)
    {
        ArgumentNullException.ThrowIfNull(form);
        return form.NoticeNotWorked && !IsReadOnly(detail);
    }

    /// <summary>A box of the settlement: empty is zero, text that is no number is refused (false).</summary>
    public static bool TryRead(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (TypedNumber.Parse(text) is not { } read) return false;
        value = read;
        return true;
    }

    /// <summary>
    /// Puts the law's figure back in each amount whose basis just changed: the reference salary
    /// moves all three, the leave days the leave indemnity, the notice box the notice indemnity.
    /// </summary>
    public static void Recompute(SettlementForm form, SettlementDetail d, bool leave = false, bool notice = false, bool severance = false,
                                 IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(d);
        if (!TryRead(form.ReferenceSalary, out var reference)) return;

        if (leave && TryRead(form.LeaveDays, out var days))
            form.LeaveIndemnity = N(SettlementCalculator.LeaveIndemnity(days, reference, d.MonthlyWorkingDays), culture);
        if (notice)
            form.NoticeIndemnity = N(form.NoticeNotWorked && d.NoticeApplies
                ? SettlementCalculator.NoticeIndemnity(d.Notice, reference, d.MonthlyWorkingDays)
                : 0m, culture);
        if (severance && d.SeveranceDue)
            form.Severance = N(SettlementCalculator.Severance(d.ServiceYears, reference, d.MonthlyHours), culture);
    }

    /// <summary>The indemnities added up, a box that is no number counted as nothing.</summary>
    public static decimal Total(SettlementForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        TryRead(form.LeaveIndemnity, out var leave);
        TryRead(form.NoticeIndemnity, out var notice);
        TryRead(form.Severance, out var severance);
        return leave + notice + severance;
    }

    /// <summary>« Total des indemnités : 12 345,67 DH », the figure in <paramref name="culture"/>.</summary>
    public static string TotalText(SettlementForm form, ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text("Settlement.Total", Total(form).ToString("N2", culture));
    }

    /// <summary>The file in one sentence: hired, left and why, the service and the category.</summary>
    public static string Summary(SettlementDetail d, ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(text);
        return text("Settlement.Summary", PersonnelScreenRules.Date(d.HireDate), PersonnelScreenRules.Date(d.ExitDate),
                    text($"ExitReason.{d.ExitReason}"), d.ServiceYears.ToString("0.##", culture),
                    text($"StaffCategory.{d.Category}"));
    }

    /// <summary>Where the reference salary comes from: the average of the last full months, or the base salary.</summary>
    public static string ReferenceHint(SettlementDetail d, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(text);
        return d.ReferenceMonths > 0 ? text("Settlement.ReferenceFromMonths", d.ReferenceMonths) : text("Settlement.ReferenceFromBase");
    }

    /// <summary>The legal notice of this departure, or that none is due for its reason.</summary>
    public static string NoticeHint(SettlementDetail d, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(text);
        var period = d.Notice.Months > 0
            ? text("Settlement.NoticeMonths", d.Notice.Months)
            : text("Settlement.NoticeDays", d.Notice.Days);
        return d.NoticeApplies ? text("Settlement.NoticeLegal", period) : text("Settlement.NoticeNotDue");
    }

    /// <summary>The severance due in hours of salary, or why none is.</summary>
    public static string SeveranceHint(SettlementDetail d, ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(text);
        return d.SeveranceDue
            ? text("Settlement.SeveranceDue", d.SeveranceHours.ToString("0.##", culture))
            : text("Settlement.SeveranceNotDue");
    }

    /// <summary>
    /// The sentence at the foot: signed on a date (nothing more can change), or where the amounts go —
    /// the payslip of the month of departure, to be reopened when it is closed (the warning's tone).
    /// </summary>
    public static (string Text, bool Warning) Notice(SettlementDetail d, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(text);
        if (d.SignedOn is { } signed) return (text("Settlement.Signed", PersonnelScreenRules.Date(signed)), true);
        return d.Payroll == SettlementPayroll.Closed
            ? (text("Settlement.NoticePayrollClosed"), true)
            : (text("Settlement.NoticePayroll"), false);
    }

    /// <summary>Every amount a number, none negative; the notice indemnity only when the notice was not worked.</summary>
    public static (FieldRefusal? Refusal, SettlementAmounts? Amounts) Check(SettlementForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        (string Field, string? Text)[] boxes =
        [
            (ReferenceField, form.ReferenceSalary), (LeaveDaysField, form.LeaveDays), (LeaveField, form.LeaveIndemnity),
            (NoticeField, form.NoticeIndemnity), (SeveranceField, form.Severance)
        ];

        var values = new decimal[boxes.Length];
        for (var i = 0; i < boxes.Length; i++)
            if (!TryRead(boxes[i].Text, out values[i]) || values[i] < 0)
                return (new FieldRefusal(boxes[i].Field, "Settlement_NegativeAmount"), null);

        return (null, new SettlementAmounts(values[0], values[1], values[2], form.NoticeNotWorked,
                                            form.NoticeNotWorked ? values[3] : 0m, values[4],
                                            string.IsNullOrWhiteSpace(form.Note) ? null : form.Note.Trim()));
    }

    // ---- The receipt signed -----------------------------------------------------------------------

    /// <summary>The earliest date the receipt can carry: the exit date, or today when the exit is still ahead.</summary>
    public static DateOnly EarliestSigning(DateOnly exit, DateOnly today) => exit > today ? today : exit;

    /// <summary>Not before the exit date, not in the future.</summary>
    public static FieldRefusal? CheckSigned(DateOnly on, DateOnly exit, DateOnly today) =>
        on > today ? new FieldRefusal(SignedOnField, "Settlement_SignedInFuture")
        : on < EarliestSigning(exit, today) ? new FieldRefusal(SignedOnField, "Settlement_SignedBeforeExit")
        : null;

    /// <summary>The box a refusal of the service names, when it names one.</summary>
    public static string? FieldOf(string errorKey) => errorKey switch
    {
        "Settlement_NegativeAmount" => ReferenceField,
        "Settlement_NoticeWorked" => NoticeField,
        "Settlement_SignedInFuture" or "Settlement_SignedBeforeExit" => SignedOnField,
        _ => null
    };
}
