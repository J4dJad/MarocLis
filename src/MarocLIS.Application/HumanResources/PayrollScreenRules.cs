using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.HumanResources;

/// <summary>
/// What « Paie » offers on the month shown and the payslip selected: the variables and the files'
/// update while the month is a draft, the payslips and the exports once there is a month, the
/// closing (or the reopening), and the bank transfer and the BDS once it is closed.
/// </summary>
public sealed record PayrollActions(
    bool Open,
    bool Settings,
    bool SimplIr,
    bool Variables,
    bool Update,
    bool Payslip,
    bool AllPayslips,
    bool Journal,
    bool Cnss,
    bool Close,
    string CloseKey,
    bool Transfer,
    bool Bds);

/// <summary>The month's variables of one payslip, as typed: each held as typed, so one that is no number is refused on its box.</summary>
public sealed class PayslipVariablesForm
{
    public string? ManualAbsenceDays { get; set; }
    public string? Overtime25 { get; set; }
    public string? Overtime50 { get; set; }
    public string? Overtime100 { get; set; }
    public string? TaxableBonus { get; set; }
    public string? Transport { get; set; }
    public string? OtherExempt { get; set; }
    public string? Advance { get; set; }
    public string? OtherDeduction { get; set; }
    public string? Note { get; set; }
}

/// <summary>One rate or amount of the year's parameters: its caption's key, its box, how it is read from and written to the year.</summary>
public sealed record PayrollRateField(string Key, string Field, Func<PayrollParameters, decimal> Get, Action<PayrollParameters, decimal> Set);

/// <summary>
/// The rules of « Paie » that need no window: the months and how one reads, the payslips' lines
/// (the alerts, the totals), the actions by state, the questions asked before closing and
/// reopening, the files' names, the month's variables and their check, the year's rates and their
/// check, and the Simpl-IR codes. The workstation's PayrollView and its dialogs and the web's
/// screen apply the same ones; the computation is PayrollCalculator's, what is done PayrollService's.
/// The whole screen is the salary right's.
/// </summary>
public static class PayrollScreenRules
{
    public const int AmountMaxLength = TypedNumber.AmountMaxLength;
    public const int NoteMaxLength = 300;
    public const int ScaleMaxLength = 300;

    /// <summary>The préétabli DAMANCOM issues for the month: a text file, read whole, a few hundred kilobytes at most.</summary>
    public const string PreestablishedAccept = ".txt";
    public const long PreestablishedMaxBytes = 5 * 1024 * 1024;

    public const string ManualAbsenceField = "payroll-absence";
    public const string Overtime25Field = "payroll-ot25";
    public const string Overtime50Field = "payroll-ot50";
    public const string Overtime100Field = "payroll-ot100";
    public const string BonusField = "payroll-bonus";
    public const string TransportField = "payroll-transport";
    public const string ExemptField = "payroll-exempt";
    public const string AdvanceField = "payroll-advance";
    public const string OtherDeductionField = "payroll-other";
    public const string NoteField = "payroll-note";
    public const string ScaleField = "payroll-scale";
    public const string MonthField = "payroll-month";
    public const string CommuneField = "simplir-commune";
    public const string LowRateField = "simplir-low";
    public const string HighRateField = "simplir-high";
    public const string YearField = "simplir-year";
    public const string PreestablishedField = "bds-preestablished";

    /// <summary>The years a Simpl-IR return can be made for.</summary>
    public const int FirstYear = 2000, LastYear = 2100;

    /// <summary>« Octobre 2026 », in the language's month names.</summary>
    public static string MonthName(int year, int month, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return culture.TextInfo.ToTitleCase(new DateTime(year, month, 1).ToString("MMMM yyyy", culture));
    }

    /// <summary>A month of the list: « Octobre 2026 — Brouillon ».</summary>
    public static string PeriodCaption(PayrollPeriodRow row, ScreenText text, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return $"{MonthName(row.Year, row.Month, culture)} — {text($"PayrollStatus.{row.Status}")}";
    }

    /// <summary>The head: the month, its state and its payslips; empty with no month.</summary>
    public static string Heading(PayrollPeriodDetail? detail, ScreenText text, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (detail is null) return string.Empty;
        return text("QualityHr.Payroll.Count", MonthName(detail.Period.Year, detail.Period.Month, culture),
                    text($"PayrollStatus.{detail.Period.Status}"), detail.Payslips.Count);
    }

    /// <summary>The month's state as a pill: a draft in amber, a closed month in green.</summary>
    public static string Tone(PayrollStatus status) => status == PayrollStatus.Closed ? "Ok" : "Warning";

    /// <summary>The days paid: « 26 ».</summary>
    public static string Days(decimal days, IFormatProvider culture) => days.ToString("0.#", culture);

    /// <summary>The employee's contributions: CNSS and AMO together, as the list shows them.</summary>
    public static decimal Social(PayslipAmounts amounts)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        return amounts.CnssEmployee + amounts.AmoEmployee;
    }

    /// <summary>What to check on a payslip: its warnings, then the exemption it was computed under.</summary>
    public static string Warnings(PayslipRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return string.Join(" · ", row.Warnings.Select(w => text($"PayslipWarning.{w}"))
                                     .Concat(row.Amounts.Exemption == PayrollExemption.None
                                         ? [] : [text($"PayrollExemption.{row.Amounts.Exemption}")]));
    }

    /// <summary>A negative net in the alarm's ink; any other warning in the warning's.</summary>
    public static string? WarningTone(PayslipRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Warnings.Count == 0 ? null
             : row.Warnings.Contains(PayslipWarning.NegativeNet) ? "critical" : "abnormal";
    }

    /// <summary>
    /// The month's totals under the list: gross, contributions, tax, net and the employer's cost, each
    /// written by <paramref name="money"/>; « no month open » with none.
    /// </summary>
    public static string Totals(PayrollPeriodDetail? detail, string key, ScreenText text, Func<decimal, string> money)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(money);
        if (detail is null) return text("Payroll.NoPeriod");
        return text(key, money(detail.Total(a => a.Gross)), money(detail.Total(Social)), money(detail.Total(a => a.IncomeTax)),
                    money(detail.Total(a => a.NetPay)), money(detail.Total(a => a.EmployerCost)));
    }

    /// <summary>
    /// The actions on the month and the payslip selected. Only a closed month goes to the bank or to
    /// the CNSS: its amounts no longer change.
    /// </summary>
    public static PayrollActions Actions(PayrollPeriodDetail? detail, bool payslipSelected, Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(has);
        var salary = has(Permissions.HumanResources.Salary);
        var draft = detail?.Period.Status == PayrollStatus.Draft;
        var closed = detail?.Period.Status == PayrollStatus.Closed;
        var any = detail is not null;

        return new PayrollActions(
            Open: salary,
            Settings: salary,
            SimplIr: salary,
            Variables: salary && draft && payslipSelected,
            Update: salary && draft,
            Payslip: salary && any && payslipSelected,
            AllPayslips: salary && any,
            Journal: salary && any,
            Cnss: salary && any,
            Close: salary && any,
            CloseKey: closed ? "Payroll.Reopen" : "Payroll.Close",
            Transfer: salary && closed,
            Bds: salary && closed);
    }

    /// <summary>The month « Ouvrir un mois » proposes: the one after the last opened, else this one.</summary>
    public static DateOnly NextMonth(IReadOnlyList<PayrollPeriodRow> periods, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(periods);
        return periods.Count == 0
            ? new DateOnly(today.Year, today.Month, 1)
            : periods.Select(p => new DateOnly(p.Year, p.Month, 1)).Max().AddMonths(1);
    }

    public static string ClosePrompt(PayrollPeriodDetail detail, ScreenText text, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(text);
        return text("Payroll.ClosePrompt", MonthName(detail.Period.Year, detail.Period.Month, culture));
    }

    public static string ReopenPrompt(PayrollPeriodDetail detail, ScreenText text, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(text);
        return text("Payroll.ReopenPrompt", MonthName(detail.Period.Year, detail.Period.Month, culture));
    }

    /// <summary>
    /// Normally the month keeps the rates it was closed with; taking the year's current ones is
    /// only for a rate typed wrong and corrected since. « No » is the default answer.
    /// </summary>
    public static string ReopenRatesPrompt(PayrollPeriodDetail detail, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(text);
        return text("Payroll.ReopenCurrentRatesPrompt", detail.Period.Year);
    }

    // ---- The files -----------------------------------------------------------------------------------

    private static string Stamp(PayrollPeriodRow period) =>
        $"{period.Year.ToString("0000", CultureInfo.InvariantCulture)}-{period.Month.ToString("00", CultureInfo.InvariantCulture)}";

    /// <summary>« bulletins-paie-2026-10.pdf », or one employee's « bulletin-paie-2026-10-M0002.pdf ».</summary>
    public static string PayslipsFileName(PayrollPeriodRow period, PayslipRow? only)
    {
        ArgumentNullException.ThrowIfNull(period);
        return only is null ? $"bulletins-paie-{Stamp(period)}.pdf" : $"bulletin-paie-{Stamp(period)}-{Safe(only.Number)}.pdf";
    }

    public static string JournalFileName(PayrollPeriodRow period) => $"journal-paie-{Stamp(period)}.csv";

    public static string CnssFileName(PayrollPeriodRow period) => $"recapitulatif-cnss-{Stamp(period)}.csv";

    public static string TransferOrderFileName(PayrollPeriodRow period) => $"ordre-virement-{Stamp(period)}.pdf";

    public static string TransferFileName(PayrollPeriodRow period) => $"virement-salaires-{Stamp(period)}.csv";

    private static string Safe(string value) =>
        new([.. value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);

    /// <summary>The BDS saved: where, how many declared, how many entrants the préétabli did not hold.</summary>
    public static string BdsSaved(PayrollExportFile file, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(text);
        return text("Payroll.BdsSaved", file.FileName, file.Declared, file.Entrants);
    }

    public static string SimplIrSaved(PayrollExportFile file, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(text);
        return text("Payroll.SimplIrSaved", file.FileName, file.Declared);
    }

    // ---- The month's variables -----------------------------------------------------------------------

    private static string N(decimal d, IFormatProvider? culture) => d == 0 ? string.Empty : TypedNumber.ForEditing(d, 0, culture ?? CultureInfo.InvariantCulture);

    /// <summary>The variables as entered, a zero left empty. Only the days typed by hand: those from the leave are taken again at each update.</summary>
    public static PayslipVariablesForm FormFor(PayslipVariables v, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(v);
        return new PayslipVariablesForm
        {
            ManualAbsenceDays = N(v.ManualAbsenceDays, culture), Overtime25 = N(v.OvertimeHours25, culture), Overtime50 = N(v.OvertimeHours50, culture),
            Overtime100 = N(v.OvertimeHours100, culture), TaxableBonus = N(v.TaxableBonus, culture), Transport = N(v.TransportAllowance, culture),
            OtherExempt = N(v.OtherExemptAllowance, culture), Advance = N(v.AdvanceDeduction, culture), OtherDeduction = N(v.OtherDeduction, culture),
            Note = v.Note
        };
    }

    /// <summary>
    /// The variables typed, read: an empty box is zero; text that is no number, or a negative one, is
    /// refused on its box. The departure indemnities and the leave-derived days come from the files
    /// and are kept as they are.
    /// </summary>
    public static (FieldRefusal? Refusal, PayslipVariables? Variables) Check(PayslipVariablesForm form, PayslipVariables original)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(original);

        (string Field, string? Text)[] boxes =
        [
            (ManualAbsenceField, form.ManualAbsenceDays), (Overtime25Field, form.Overtime25), (Overtime50Field, form.Overtime50),
            (Overtime100Field, form.Overtime100), (BonusField, form.TaxableBonus), (TransportField, form.Transport),
            (ExemptField, form.OtherExempt), (AdvanceField, form.Advance), (OtherDeductionField, form.OtherDeduction)
        ];
        var values = new decimal[boxes.Length];
        for (var i = 0; i < boxes.Length; i++)
            if (!TryReadOrZero(boxes[i].Text, out values[i]) || values[i] < 0)
                return (new FieldRefusal(boxes[i].Field, "Payroll_NegativeVariable"), null);

        return (null, original with
        {
            ManualAbsenceDays = values[0], OvertimeHours25 = values[1], OvertimeHours50 = values[2], OvertimeHours100 = values[3],
            TaxableBonus = values[4], TransportAllowance = values[5], OtherExemptAllowance = values[6],
            AdvanceDeduction = values[7], OtherDeduction = values[8],
            Note = string.IsNullOrWhiteSpace(form.Note) ? null : form.Note.Trim()
        });
    }

    /// <summary>An empty box is zero; false only for text that is no number.</summary>
    public static bool TryReadOrZero(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (TypedNumber.Parse(text) is not { } read) return false;
        value = read;
        return true;
    }

    /// <summary>A service's refusal of the variables beside its box: the unpaid days, the overtime.</summary>
    public static string? FieldOf(string resourceKey) => resourceKey switch
    {
        "Payroll_TooManyAbsenceDays" => ManualAbsenceField,
        "Payroll_TooManyOvertimeHours" => Overtime25Field,
        "Payroll_NegativeVariable" => ManualAbsenceField,
        "Payroll_ScaleInvalid" => ScaleField,
        "Payroll_PeriodInvalid" or "Payroll_PeriodExists" => MonthField,
        "Payroll_SimplIrCodesRequired" => CommuneField,
        "Payroll_BdsFileInvalid" or "Payroll_BdsFileNoHeader" or "Payroll_BdsWrongPeriod" or "Payroll_BdsWrongAffiliate" => PreestablishedField,
        _ => null
    };

    /// <summary>
    /// The payslip as it stands, under the variables: gross, contributions, tax, net and the seniority
    /// rate, then the days the leave withheld, the prorated month, the departure indemnities and the
    /// accident stoppage, when there are any. Amounts written by <paramref name="money"/>; the sentences
    /// are those under <paramref name="keys"/> (« Payroll. », whose sentences carry the currency, or the
    /// web's « Web.Payroll », whose amounts carry it).
    /// </summary>
    public static string CurrentResult(PayslipRow row, ScreenText text, Func<decimal, string> money, IFormatProvider culture,
                                       string keys = "Payroll.")
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(money);
        var a = row.Amounts;
        var v = row.Variables;
        var said = text($"{keys}CurrentResult", money(a.Gross), money(a.CnssEmployee), money(a.AmoEmployee), money(a.IncomeTax),
                        money(a.NetPay), a.SeniorityRate.ToString("0.#", culture));
        if (v.AbsenceDays > 0) said += " " + text("Payroll.LeaveAbsenceIncluded", v.AbsenceDays.ToString("0.#", culture));
        if (v.IsProrated)
            said += " " + text("Payroll.ProratedMonth", v.ContractWorkingDays.ToString("0.#", culture), v.MonthWorkingDays.ToString("0.#", culture));
        var departure = v.LeaveIndemnity + v.NoticeIndemnity + v.SeverancePay;
        if (departure > 0) said += " " + text($"{keys}DepartureIncluded", money(departure));
        if (v.WorkAccidentDays > 0)
            said += " " + text($"{keys}AccidentIncluded", v.WorkAccidentDays.ToString("0.#", culture), money(v.WorkAccidentAllowance));
        return said;
    }

    /// <summary>« M0002 AÏT OUAHMANE Youssef ».</summary>
    public static string Who(PayslipRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"{row.Number} {row.FullName}";
    }

    // ---- The year's rates ----------------------------------------------------------------------------

    /// <summary>The year's rates and amounts, in the order the form shows them.</summary>
    public static IReadOnlyList<PayrollRateField> RateFields { get; } =
    [
        new("PayrollRate.CnssCeiling", "rate-cnss-ceiling", p => p.CnssCeiling, (p, v) => p.CnssCeiling = v),
        new("PayrollRate.CnssEmployee", "rate-cnss-employee", p => p.CnssEmployeeRate, (p, v) => p.CnssEmployeeRate = v),
        new("PayrollRate.CnssEmployer", "rate-cnss-employer", p => p.CnssEmployerRate, (p, v) => p.CnssEmployerRate = v),
        new("PayrollRate.FamilyAllowance", "rate-family-allowance", p => p.FamilyAllowanceRate, (p, v) => p.FamilyAllowanceRate = v),
        new("PayrollRate.TrainingTax", "rate-training-tax", p => p.TrainingTaxRate, (p, v) => p.TrainingTaxRate = v),
        new("PayrollRate.AmoEmployee", "rate-amo-employee", p => p.AmoEmployeeRate, (p, v) => p.AmoEmployeeRate = v),
        new("PayrollRate.AmoEmployer", "rate-amo-employer", p => p.AmoEmployerRate, (p, v) => p.AmoEmployerRate = v),
        new("PayrollRate.ExpensesLow", "rate-expenses-low", p => p.ProfessionalExpensesLowRate, (p, v) => p.ProfessionalExpensesLowRate = v),
        new("PayrollRate.ExpensesHigh", "rate-expenses-high", p => p.ProfessionalExpensesHighRate, (p, v) => p.ProfessionalExpensesHighRate = v),
        new("PayrollRate.ExpensesThreshold", "rate-expenses-threshold", p => p.ProfessionalExpensesThreshold, (p, v) => p.ProfessionalExpensesThreshold = v),
        new("PayrollRate.ExpensesCap", "rate-expenses-cap", p => p.ProfessionalExpensesAnnualCap, (p, v) => p.ProfessionalExpensesAnnualCap = v),
        new("PayrollRate.FamilyDeduction", "rate-family-deduction", p => p.FamilyDeductionPerPerson, (p, v) => p.FamilyDeductionPerPerson = v),
        new("PayrollRate.Smig", "rate-smig", p => p.SmigMonthly, (p, v) => p.SmigMonthly = v),
        new("PayrollRate.MonthlyHours", "rate-monthly-hours", p => p.MonthlyHours, (p, v) => p.MonthlyHours = v),
        new("PayrollRate.Transport", "rate-transport", p => p.TransportExemptionCap, (p, v) => p.TransportExemptionCap = v)
    ];

    /// <summary>A rate as the form shows it, with the decimal separator of the screen's language (« 4,48 » in French).</summary>
    public static string Rate(decimal value, IFormatProvider? culture = null) =>
        TypedNumber.ForEditing(value, 0, culture ?? CultureInfo.InvariantCulture);

    /// <summary>
    /// The rates typed, read: each a number, none negative; the scale rising ceilings
    /// (« 40000:0|60000:10|…|:37 »). A refusal names its box; the values come back in the order of
    /// <see cref="RateFields"/>.
    /// </summary>
    public static (FieldRefusal? Refusal, decimal[] Values) CheckRates(IReadOnlyList<string?> typed, string? scale)
    {
        ArgumentNullException.ThrowIfNull(typed);
        var values = new decimal[RateFields.Count];
        for (var i = 0; i < RateFields.Count; i++)
        {
            if (TypedNumber.Parse(i < typed.Count ? typed[i] : null) is not { } value || value < 0)
                return (new FieldRefusal(RateFields[i].Field, "Payroll_RateInvalid"), values);
            values[i] = value;
        }

        if (PayrollParameters.ParseScale(scale ?? string.Empty).Count == 0)
            return (new FieldRefusal(ScaleField, "Payroll_ScaleInvalid"), values);

        return (null, values);
    }

    /// <summary>The rates checked, written to the year.</summary>
    public static void Apply(PayrollParameters target, decimal[] values, string scale)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(values);
        for (var i = 0; i < RateFields.Count; i++) RateFields[i].Set(target, values[i]);
        target.IncomeTaxScale = scale.Trim();
    }

    // ---- Simpl-IR ------------------------------------------------------------------------------------

    /// <summary>The état 9421 is made in the new year for the one just ended.</summary>
    public static int SimplIrYear(DateOnly today) => Math.Clamp(today.Year - 1, FirstYear, LastYear);

    /// <summary>The codes typed last time, or the referential's rates for the ones never typed.</summary>
    public static SimplIrCodes Remembered(SimplIrCodes? stored) =>
        new(stored?.Commune ?? string.Empty,
            string.IsNullOrWhiteSpace(stored?.LowIncomeRateCode) ? SimplIrCodes.DefaultLowIncomeRateCode : stored.LowIncomeRateCode,
            string.IsNullOrWhiteSpace(stored?.HighIncomeRateCode) ? SimplIrCodes.DefaultHighIncomeRateCode : stored.HighIncomeRateCode);

    /// <summary>The three codes are all required; the year within the range a return can be made for.</summary>
    public static FieldRefusal? CheckSimplIr(int year, SimplIrCodes codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        if (year is < FirstYear or > LastYear) return new FieldRefusal(YearField, "Payroll_PeriodInvalid");
        if (string.IsNullOrWhiteSpace(codes.Commune)) return new FieldRefusal(CommuneField, "Payroll_SimplIrCodesRequired");
        if (string.IsNullOrWhiteSpace(codes.LowIncomeRateCode)) return new FieldRefusal(LowRateField, "Payroll_SimplIrCodesRequired");
        if (string.IsNullOrWhiteSpace(codes.HighIncomeRateCode)) return new FieldRefusal(HighRateField, "Payroll_SimplIrCodesRequired");
        return null;
    }

    /// <summary>The codes trimmed, as sent and remembered.</summary>
    public static SimplIrCodes Codes(string? commune, string? low, string? high) =>
        new((commune ?? string.Empty).Trim(), (low ?? string.Empty).Trim(), (high ?? string.Empty).Trim());
}
