using System.Globalization;
using FluentAssertions;
using MarocLIS.Application.HumanResources;
using MarocLIS.Application.Input;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Congés et absences », « Planning », « Paie » and « Accidents du travail », moved out
/// of the workstation's views and dialogs, now shared with the web.
/// </summary>
public sealed class HumanResourcesLot9bRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 26);
    private static readonly DateTime Now = new(2026, 10, 26, 10, 30, 0);
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    private static Func<string, bool> Rights(params string[] rights) => rights.Contains;

    // ---- Congés et absences --------------------------------------------------------------------------

    private static LeaveRow Leave(LeaveType type = LeaveType.Annual, LeaveStatus status = LeaveStatus.Pending, DateOnly? start = null,
                                  bool proof = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "M0002", "AÏT OUAHMANE Youssef", "Hématologie", type, start ?? Today.AddDays(9),
            (start ?? Today.AddDays(9)).AddDays(2), false, false, 3m, null, proof, status, DateTimeOffset.Now, null, null, 0);

    [Fact]
    public void Deciding_is_the_approvers_on_a_request_waiting_and_a_certificate_is_received_by_hr()
    {
        var hr = Rights(Permissions.HumanResources.Manage, Permissions.HumanResources.ApproveLeave);
        var staff = Rights(Permissions.HumanResources.RequestLeave);

        var sick = LeaveScreenRules.Actions(Leave(LeaveType.Sick), Today, hr);
        sick.Should().Match<LeaveActions>(a => a.New && a.Approve && a.Refuse && a.ProofReceived && a.Cancel && a.Adjust && a.ManageHolidays);
        LeaveScreenRules.Actions(Leave(LeaveType.Annual), Today, hr).ProofReceived.Should().BeFalse("an annual leave needs no certificate");
        LeaveScreenRules.Actions(Leave(LeaveType.Sick, proof: true), Today, hr).ProofReceived.Should().BeFalse();

        var own = LeaveScreenRules.Actions(Leave(), Today, staff);
        own.Should().Match<LeaveActions>(a => a.New && !a.Approve && !a.Refuse && !a.ProofReceived && a.Cancel && !a.Adjust && !a.ManageHolidays);

        LeaveScreenRules.Actions(Leave(status: LeaveStatus.Approved, start: Today.AddDays(-1)), Today, staff).Cancel
            .Should().BeFalse("a leave begun is no longer cancelled");
        LeaveScreenRules.Actions(Leave(status: LeaveStatus.Approved), Today, staff).Cancel.Should().BeTrue();
        LeaveScreenRules.Actions(Leave(status: LeaveStatus.Refused), Today, hr).Should()
            .Match<LeaveActions>(a => !a.Approve && !a.Cancel);
        LeaveScreenRules.Actions(null, Today, Rights(Permissions.HumanResources.View)).New.Should().BeFalse();
    }

    [Fact]
    public void The_request_counts_its_days_beside_the_balance_and_an_overdraft_alarms()
    {
        var person = new LeaveBalanceRow(Guid.NewGuid(), "M0002", "AÏT OUAHMANE Youssef", 18m, 12m, 6m, 3m, 6m);

        LeaveScreenRules.Preview(LeaveType.Annual, 5m, person, Text, Invariant)
            .Should().Be(new LeavePreview("Leave.WorkingDays(5,5)", "Leave.AvailableBalance(3)", true));
        LeaveScreenRules.Preview(LeaveType.Annual, 2m, person, Text, Invariant).Over.Should().BeFalse();
        LeaveScreenRules.Preview(LeaveType.Birth, 3m, person, Text, Invariant)
            .Should().Be(new LeavePreview("Leave.WorkingDays(3,3)", "Leave.Entitlement(3)", false));
        LeaveScreenRules.Preview(LeaveType.Sick, 4m, null, Text, Invariant)
            .Should().Be(new LeavePreview("Leave.CalendarDays(4,4)", string.Empty, false));

        LeaveScreenRules.Types(hrTypes: false).Should().NotContain(LeaveType.UnjustifiedAbsence);
        LeaveScreenRules.Types(hrTypes: true).Should().Contain(LeaveType.UnjustifiedAbsence);
        LeaveScreenRules.EndAfter(Today.AddDays(5), Today).Should().Be(Today.AddDays(5));
        LeaveScreenRules.EndAfter(Today, Today.AddDays(5)).Should().Be(Today.AddDays(5));
        LeaveScreenRules.Submission(null, LeaveType.Annual, Today, Today, false, false, "   ", false).Reason.Should().BeNull();
        LeaveScreenRules.Submission(null, LeaveType.Annual, Today, Today, false, false, " Fès ", false).Reason.Should().Be("Fès");
    }

    [Fact]
    public void A_refusal_of_the_dates_a_correction_or_a_holiday_is_said_beside_its_box()
    {
        LeaveScreenRules.FieldOf("Leave_EndBeforeStart").Should().Be(LeaveScreenRules.EndField);
        LeaveScreenRules.FieldOf("Leave_Overlaps").Should().Be(LeaveScreenRules.StartField);
        LeaveScreenRules.FieldOf("Holiday_AlreadyExists").Should().Be(LeaveScreenRules.HolidayDateField);
        LeaveScreenRules.FieldOf("Leave_NotFound").Should().BeNull();

        LeaveScreenRules.CheckAdjustment("0").Refusal!.Field.Should().Be(LeaveScreenRules.AdjustDaysField);
        LeaveScreenRules.CheckAdjustment("deux").Refusal!.Key.Should().Be("Leave_AdjustmentInvalid");
        LeaveScreenRules.CheckAdjustment("-2,5").Should().Be(((FieldRefusal?)null, -2.5m));

        LeaveScreenRules.CheckHoliday(2026, new DateOnly(2026, 11, 18), " ")
            .Should().Be(new FieldRefusal(LeaveScreenRules.HolidayNameField, "Holiday_NameRequired"));
        LeaveScreenRules.CheckHoliday(2026, new DateOnly(2027, 1, 11), "Manifeste de l'indépendance")
            .Should().Be(new FieldRefusal(LeaveScreenRules.HolidayDateField, "Holiday_OutsideYear", "2026"));
        LeaveScreenRules.CheckHoliday(2026, new DateOnly(2026, 11, 18), "Fête de l'indépendance").Should().BeNull();
        LeaveScreenRules.HolidayDates(2027, Today).Initial.Should().Be(new DateOnly(2027, 1, 1));
        LeaveScreenRules.HolidayDates(2026, Today).Initial.Should().Be(Today);
        LeaveScreenRules.Recounted([], Text, Invariant).Should().BeNull();
    }

    // ---- Planning ------------------------------------------------------------------------------------

    private static readonly PlanningEmployee Courier =
        new(Guid.NewGuid(), "M0008", "BENJELLOUN Omar", EmployeeJob.Courier, null, new DateOnly(2026, 10, 5), null, false);

    private static ShiftCell Shift(DateOnly day, ShiftKind kind, string? note = null)
    {
        var (start, end) = ShiftRules.DefaultHours(kind);
        return new ShiftCell(Guid.NewGuid(), Courier.EmployeeId, day, kind, start, end, note);
    }

    [Fact]
    public void The_grid_reads_each_day_its_contract_its_leave_its_posts_and_the_points_to_check()
    {
        var night = Shift(new DateOnly(2026, 10, 28), ShiftKind.Night);
        var afternoon = Shift(new DateOnly(2026, 10, 29), ShiftKind.Afternoon, "Tournée des cliniques d'Anfa");
        var month = new PlanningMonth(2026, 10, [Courier], [night, afternoon],
            [new LeaveSpan(Courier.EmployeeId, LeaveType.Annual, LeaveStatus.Approved, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 13)),
             new LeaveSpan(Courier.EmployeeId, LeaveType.Annual, LeaveStatus.Pending, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 20))],
            [new HolidayRow(Guid.NewGuid(), new DateOnly(2026, 10, 7), "Fête", false)],
            [new PlanningIssue(PlanningIssueKind.RestTooShort, afternoon.Date, Courier.EmployeeId, Courier.FullName, 9m)]);

        var line = PlanningScreenRules.Lines(month, Text, Invariant).Should().ContainSingle().Subject;
        line.Days.Should().HaveCount(31);
        line.Name.Should().Be("BENJELLOUN Omar · EmployeeJobShort.Courier");
        line.Hours.Should().Be(PlanningScreenRules.Hours(night.Hours + afternoon.Hours, Invariant));

        PlanningDay On(int day) => line.Days[day - 1];
        PlanningScreenRules.Ink(On(2)).Should().Be(PlanningInk.NotEmployed, "hired on the 5th");
        PlanningScreenRules.Shaded(On(2)).Should().BeTrue();
        PlanningScreenRules.Shaded(On(7)).Should().BeTrue("a holiday");
        PlanningScreenRules.Shaded(On(11)).Should().BeTrue("a Sunday");
        PlanningScreenRules.Ink(On(12)).Should().Be(PlanningInk.LeaveApproved);
        PlanningScreenRules.Ink(On(20)).Should().Be(PlanningInk.LeavePending);
        PlanningScreenRules.Code(On(12), Text).Should().Be("LeaveTypeShort.Annual");
        PlanningScreenRules.Ink(On(28)).Should().Be(PlanningInk.ShiftUnusual, "a night");
        PlanningScreenRules.Ink(On(29)).Should().Be(PlanningInk.ShiftFlagged);
        PlanningScreenRules.Ink(On(30)).Should().Be(PlanningInk.Empty);
        PlanningScreenRules.Code(On(28), Text).Should().Be("ShiftKindShort.Night");
        PlanningScreenRules.Detail([On(29)], Text).Should().Contain("(Tournée des cliniques d'Anfa)").And.StartWith("BENJELLOUN Omar — Weekday.Thursday 29/10/2026");

        PlanningScreenRules.IssueText(month.Issues[0], Text, Invariant).Should().Be("PlanningIssue.RestTooShort(9)");
        PlanningScreenRules.IsCritical(month.Issues[0]).Should().BeFalse();
        PlanningScreenRules.IsCritical(month.Issues[0] with { Kind = PlanningIssueKind.NoBiologist }).Should().BeTrue();
        PlanningScreenRules.Count(month, Text).Should().Be("QualityHr.Planning.Count(1,2,1)");
    }

    [Fact]
    public void The_selection_is_changed_only_with_the_planning_right_and_a_week_is_copied_from_the_one_before()
    {
        var line = PlanningScreenRules.Lines(new PlanningMonth(2026, 10, [Courier], [Shift(new DateOnly(2026, 10, 28), ShiftKind.Morning)], [], [], []),
                                             Text, Invariant)[0];
        IReadOnlyList<PlanningDay> empty = [line.Days[26]], held = [line.Days[27], line.Days[1]];

        PlanningScreenRules.Actions(held, Rights(Permissions.HumanResources.View)).Should().Be(new PlanningActions(false, false, false));
        PlanningScreenRules.Actions(empty, Rights(Permissions.HumanResources.ManagePlanning)).Should().Be(new PlanningActions(true, false, true));
        PlanningScreenRules.Actions(held, Rights(Permissions.HumanResources.ManagePlanning)).Should().Be(new PlanningActions(true, true, true));
        PlanningScreenRules.Actions([], Rights(Permissions.HumanResources.ManagePlanning)).Should().Be(new PlanningActions(false, false, false));

        PlanningScreenRules.Assignable(held).Should().ContainSingle("the 2nd is before the hiring").Which.Date.Day.Should().Be(28);
        PlanningScreenRules.Removable(held).Should().ContainSingle();
        PlanningScreenRules.Current(PlanningScreenRules.Assignable(held))!.Kind.Should().Be(ShiftKind.Morning);
        PlanningScreenRules.Detail(held, Text).Should().Be("Planning.CellsSelected(2)");
        PlanningScreenRules.Detail([], Text).Should().Be("Planning.Hint");

        PlanningScreenRules.CopyWeek(new DateOnly(2026, 10, 29)).Should().Be((new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 26)));
        PlanningScreenRules.CopyWeekPrompt(new DateOnly(2026, 11, 1), Text).Should().Be("Planning.CopyWeekPrompt(19/10,26/10)");
        PlanningScreenRules.InitialDay(2026, 10, Today).Should().Be(Today);
        PlanningScreenRules.InitialDay(2026, 11, Today).Should().Be(new DateOnly(2026, 11, 1));
    }

    [Fact]
    public void A_post_takes_its_kinds_hours_the_on_call_none_and_its_note_trimmed()
    {
        var day = PlanningScreenRules.Lines(new PlanningMonth(2026, 10, [Courier], [], [], [], []), Text, Invariant)[0].Days[27];
        var own = new ShiftCell(Guid.NewGuid(), Courier.EmployeeId, day.Date, ShiftKind.Morning, new TimeOnly(7, 30), new TimeOnly(13, 30), null);

        PlanningScreenRules.HoursOf(ShiftKind.Morning, own).Should().Be((new TimeOnly(7, 30), new TimeOnly(13, 30)));
        PlanningScreenRules.HoursOf(ShiftKind.Night, own).Should().Be(ShiftRules.DefaultHours(ShiftKind.Night));
        PlanningScreenRules.TimesEditable(ShiftKind.OnCall).Should().BeFalse();
        PlanningScreenRules.TimesEditable(ShiftKind.Night).Should().BeTrue();
        PlanningScreenRules.ShiftText(own, Text).Should().Be("ShiftKind.Morning 07:30-13:30");
        PlanningScreenRules.ShiftText(own with { Kind = ShiftKind.OnCall }, Text).Should().Be("ShiftKind.OnCall");

        var request = PlanningScreenRules.Request(day, ShiftKind.Afternoon, new TimeOnly(14, 0), new TimeOnly(20, 0), "  Anfa ");
        request.Should().Match<ShiftRequest>(r => r.EmployeeId == Courier.EmployeeId && r.Date == day.Date && r.Note == "Anfa");
        PlanningScreenRules.Request(day, ShiftKind.Afternoon, new TimeOnly(14, 0), new TimeOnly(20, 0), " ").Note.Should().BeNull();
        PlanningScreenRules.RefusedLine(day, "Repos insuffisant").Should().Be("BENJELLOUN Omar 28/10 : Repos insuffisant");
    }

    // ---- Paie ----------------------------------------------------------------------------------------

    private static PayrollPeriodDetail Month(PayrollStatus status, params PayslipRow[] payslips) =>
        new(new PayrollPeriodRow(Guid.NewGuid(), 2026, 10, status, payslips.Length, 0m, 0m, 0m, null, null), payslips);

    private static PayslipRow Payslip(PayslipVariables? variables = null, PayslipAmounts? amounts = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "M0002", "AÏT OUAHMANE Youssef", "Technicien", ContractType.Permanent, 7400m,
            variables ?? new PayslipVariables(), amounts ?? new PayslipAmounts(), []);

    [Fact]
    public void The_pay_is_the_salary_rights_and_only_a_closed_month_goes_to_the_bank_and_the_cnss()
    {
        var salary = Rights(Permissions.HumanResources.View, Permissions.HumanResources.Salary);

        var draft = PayrollScreenRules.Actions(Month(PayrollStatus.Draft, Payslip()), payslipSelected: true, salary);
        draft.Should().Match<PayrollActions>(a => a.Open && a.Variables && a.Update && a.Payslip && a.Close && !a.Transfer && !a.Bds
                                                  && a.CloseKey == "Payroll.Close");
        PayrollScreenRules.Actions(Month(PayrollStatus.Draft), payslipSelected: false, salary).Should()
            .Match<PayrollActions>(a => !a.Variables && !a.Payslip && a.AllPayslips);

        var closed = PayrollScreenRules.Actions(Month(PayrollStatus.Closed, Payslip()), true, salary);
        closed.Should().Match<PayrollActions>(a => !a.Variables && !a.Update && a.Transfer && a.Bds && a.CloseKey == "Payroll.Reopen");

        var none = PayrollScreenRules.Actions(Month(PayrollStatus.Closed, Payslip()), true, Rights(Permissions.HumanResources.View));
        none.Should().Match<PayrollActions>(a => !a.Open && !a.Settings && !a.SimplIr && !a.Payslip && !a.Journal && !a.Cnss
                                                 && !a.Close && !a.Transfer && !a.Bds);
        PayrollScreenRules.Actions(null, false, salary).Should().Match<PayrollActions>(a => a.Open && !a.Close && !a.Journal);
    }

    [Fact]
    public void A_variable_that_is_no_number_or_negative_is_refused_on_its_box_and_an_empty_one_is_zero()
    {
        var original = new PayslipVariables { AbsenceDays = 2m, LeaveIndemnity = 1200m };

        PayrollScreenRules.Check(new PayslipVariablesForm { TaxableBonus = "-500" }, original).Refusal
            .Should().Be(new FieldRefusal(PayrollScreenRules.BonusField, "Payroll_NegativeVariable"));
        PayrollScreenRules.Check(new PayslipVariablesForm { Overtime50 = "quatre" }, original).Refusal
            .Should().Be(new FieldRefusal(PayrollScreenRules.Overtime50Field, "Payroll_VariableUnreadable"),
                         "text that is no number is not said to be negative");

        var (refusal, read) = PayrollScreenRules.Check(new PayslipVariablesForm
        {
            Overtime25 = "6", Overtime50 = "4,5", TaxableBonus = "500", Advance = " ", Note = "  Garde du 14 "
        }, original);
        refusal.Should().BeNull();
        read!.Should().Match<PayslipVariables>(v => v.OvertimeHours25 == 6m && v.OvertimeHours50 == 4.5m && v.TaxableBonus == 500m
                                                    && v.AdvanceDeduction == 0m && v.Note == "Garde du 14");
        read.AbsenceDays.Should().Be(2m, "the leave's days come from the files");
        read.LeaveIndemnity.Should().Be(1200m);

        PayrollScreenRules.FormFor(read).Should().Match<PayslipVariablesForm>(f => f.Overtime50 == "4.5" && f.Advance == string.Empty);
        PayrollScreenRules.FieldOf("Payroll_TooManyAbsenceDays").Should().Be(PayrollScreenRules.ManualAbsenceField);
        PayrollScreenRules.FieldOf("Payroll_BdsWrongPeriod").Should().Be(PayrollScreenRules.PreestablishedField);
    }

    [Fact]
    public void The_month_s_variables_and_the_rates_are_prefilled_with_the_decimal_comma_of_the_screen()
    {
        var fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
        PayrollScreenRules.Rate(4.48m, fr).Should().Be("4,48", "the dot was shown whatever the language");
        PayrollScreenRules.Rate(4.48m).Should().Be("4.48");
    }

    [Fact]
    public void The_payslip_as_it_stands_says_its_amounts_in_the_currency_given_and_names_the_departure_and_the_accident()
    {
        var row = Payslip(new PayslipVariables { LeaveIndemnity = 1000m, NoticeIndemnity = 500m, WorkAccidentDays = 3m, WorkAccidentAllowance = 600m },
                          new PayslipAmounts { Gross = 8200m, CnssEmployee = 220m, AmoEmployee = 186m, IncomeTax = 640m, NetPay = 7154m, SeniorityRate = 5m });
        string Dh(decimal d) => d.ToString("0.00", Invariant) + " DH";

        PayrollScreenRules.CurrentResult(row, Text, Dh, Invariant, "Web.Payroll").Should().Be(
            "Web.PayrollCurrentResult(8200.00 DH,220.00 DH,186.00 DH,640.00 DH,7154.00 DH,5) " +
            "Web.PayrollDepartureIncluded(1500.00 DH) Web.PayrollAccidentIncluded(3,600.00 DH)");
        PayrollScreenRules.CurrentResult(row, Text, d => d.ToString("0.00", Invariant), Invariant)
            .Should().StartWith("Payroll.CurrentResult(").And.Contain("Payroll.AccidentIncluded(3,600.00)");

        var month = Month(PayrollStatus.Draft, row, row);
        PayrollScreenRules.Totals(month, "Web.PayrollTotals", Text, Dh).Should().StartWith("Web.PayrollTotals(16400.00 DH,");
        PayrollScreenRules.Totals(null, "Web.PayrollTotals", Text, Dh).Should().Be("Payroll.NoPeriod");
        PayrollScreenRules.PayslipsFileName(month.Period, row).Should().Be("bulletin-paie-2026-10-M0002.pdf");
        PayrollScreenRules.PayslipsFileName(month.Period, null).Should().Be("bulletins-paie-2026-10.pdf");
        PayrollScreenRules.NextMonth([], Today).Should().Be(new DateOnly(2026, 10, 1));
        PayrollScreenRules.NextMonth([month.Period], Today).Should().Be(new DateOnly(2026, 11, 1));
    }

    [Fact]
    public void A_rate_typed_wrong_or_a_scale_that_does_not_rise_is_refused_on_its_box()
    {
        var parameters = new PayrollParameters(2026);
        var typed = PayrollScreenRules.RateFields.Select(f => (string?)PayrollScreenRules.Rate(f.Get(parameters))).ToList();

        var (ok, values) = PayrollScreenRules.CheckRates(typed, PayrollParameters.DefaultScale);
        ok.Should().BeNull();
        values.Should().HaveCount(PayrollScreenRules.RateFields.Count);

        typed[2] = "-1";
        PayrollScreenRules.CheckRates(typed, PayrollParameters.DefaultScale).Refusal
            .Should().Be(new FieldRefusal(PayrollScreenRules.RateFields[2].Field, "Payroll_RateInvalid"));
        typed[2] = "21,09";
        PayrollScreenRules.CheckRates(typed, "barème").Refusal!.Field.Should().Be(PayrollScreenRules.ScaleField);

        var target = new PayrollParameters(2026);
        var (_, corrected) = PayrollScreenRules.CheckRates(typed, PayrollParameters.DefaultScale);
        PayrollScreenRules.Apply(target, corrected, " " + PayrollParameters.DefaultScale + " ");
        PayrollScreenRules.RateFields[2].Get(target).Should().Be(21.09m);
        target.IncomeTaxScale.Should().Be(PayrollParameters.DefaultScale);
    }

    [Fact]
    public void Simpl_ir_is_made_for_the_year_just_ended_with_its_three_codes_remembered()
    {
        PayrollScreenRules.SimplIrYear(Today).Should().Be(2025);
        PayrollScreenRules.Remembered(null).LowIncomeRateCode.Should().Be(SimplIrCodes.DefaultLowIncomeRateCode);
        PayrollScreenRules.Remembered(new SimplIrCodes("141", " ", "TPP.38")).Should()
            .Be(new SimplIrCodes("141", SimplIrCodes.DefaultLowIncomeRateCode, "TPP.38"));
        PayrollScreenRules.Codes(" 141 ", "a ", " b").Should().Be(new SimplIrCodes("141", "a", "b"));

        var codes = PayrollScreenRules.Remembered(null);
        PayrollScreenRules.CheckSimplIr(2025, codes).Should().Be(new FieldRefusal(PayrollScreenRules.CommuneField, "Payroll_SimplIrCodesRequired"));
        PayrollScreenRules.CheckSimplIr(1999, codes with { Commune = "141" })!.Field.Should().Be(PayrollScreenRules.YearField);
        PayrollScreenRules.CheckSimplIr(2025, codes with { Commune = "141", HighIncomeRateCode = "" })!.Field.Should().Be(PayrollScreenRules.HighRateField);
        PayrollScreenRules.CheckSimplIr(2025, codes with { Commune = "141" }).Should().BeNull();
    }

    // ---- Accidents du travail ------------------------------------------------------------------------

    private static AccidentRow Accident(DateTime occurred, DateOnly? declared = null, DateOnly? nextCheck = null, bool closed = false) =>
        new(Guid.NewGuid(), "AT-2026-0003", Guid.NewGuid(), "M0009", "EL OUAZZANI TOUHAMI Abdelilah", null, AccidentKind.WorkAccident,
            occurred, "Paillasse", declared, 0m, null, nextCheck, closed);

    private static AccidentDetail File(bool closed = false, IReadOnlyList<StoppageRow>? stoppages = null, IReadOnlyList<SerologyCheckRow>? checks = null,
                                       AccidentKind kind = AccidentKind.WorkAccident) =>
        new(new AccidentRequest { EmployeeId = Guid.NewGuid(), Kind = kind, OccurredAt = new DateTime(2026, 10, 12, 9, 15, 0), Circumstances = "Coupure" },
            "AT-2026-0003", "M0009", "EL OUAZZANI TOUHAMI Abdelilah", null, closed, [], checks ?? [], stoppages ?? []);

    [Fact]
    public void The_declaration_is_due_within_48_hours_and_said_late_once_they_are_over()
    {
        var fresh = Accident(Now.AddHours(-2));
        var late = Accident(Now.AddHours(-50));

        AccidentScreenRules.DeclarationLate(fresh, Now).Should().BeFalse();
        AccidentScreenRules.Declared(fresh, Now, Text).Should().Be("Accident.DeclareBefore(28/10 08:30)");
        AccidentScreenRules.DeclarationLate(late, Now).Should().BeTrue();
        AccidentScreenRules.Declared(late, Now, Text).Should().Be("Accident.DeclarationLate");
        AccidentScreenRules.DeclarationLate(late with { DeclaredToInsurerOn = Today }, Now).Should().BeFalse();
        AccidentScreenRules.Declared(late with { DeclaredToInsurerOn = Today }, Now, Text).Should().Be("26/10/2026");

        AccidentScreenRules.CheckOverdue(fresh with { NextCheckDue = Today.AddDays(-1) }, Today).Should().BeTrue();
        AccidentScreenRules.CheckOverdue(fresh with { NextCheckDue = Today }, Today).Should().BeFalse();
        AccidentScreenRules.Heading([fresh, late, late with { IsClosed = true, DeclaredToInsurerOn = Today }], 2026, Now, Text)
            .Should().Be("Accident.Heading(3,2,1)");
        AccidentScreenRules.State(late with { IsClosed = true }, Text).Should().Be(new HrPill("Accident.Closed", "Muted"));
    }

    [Fact]
    public void The_file_is_followed_with_the_accident_right_and_its_declaration_printed_by_whoever_sees_it()
    {
        var may = Rights(Permissions.HumanResources.View, Permissions.HumanResources.Accidents);
        var approved = new StoppageRow(Guid.NewGuid(), new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 19), 7m, LeaveStatus.Approved);
        var pending = new SerologyCheckRow(Guid.NewGuid(), "M1", new DateOnly(2026, 11, 12), null);

        AccidentScreenRules.Actions(null, may).Should().Be(new AccidentActions(true, false, false, false, false, false, false, false));
        AccidentScreenRules.Actions(File(), may).Should().Be(new AccidentActions(true, true, true, true, false, true, false, true));
        AccidentScreenRules.Actions(File(stoppages: [approved], checks: [pending]), may)
            .Should().Match<AccidentActions>(a => a.Return && a.CheckDone);
        AccidentScreenRules.Actions(File(closed: true, stoppages: [approved], checks: [pending]), may)
            .Should().Match<AccidentActions>(a => !a.Return && !a.CheckDone && !a.Close && a.Stoppage);
        AccidentScreenRules.Actions(File(), Rights(Permissions.HumanResources.View))
            .Should().Be(new AccidentActions(false, false, true, false, false, false, false, false));
    }

    [Fact]
    public void An_accident_typed_wrong_is_refused_beside_its_box_and_a_rate_over_a_hundred_too()
    {
        var form = new AccidentForm { EmployeeId = Guid.NewGuid(), OccurredAt = Now.AddHours(-2), Circumstances = "Coupure", IncapacityRate = "140" };
        AccidentScreenRules.Check(form, null, Now).Refusal.Should().Be(new FieldRefusal(AccidentScreenRules.RateField, "Accident_IncapacityInvalid"));
        form.IncapacityRate = "dix";
        AccidentScreenRules.Check(form, null, Now).Refusal!.Field.Should().Be(AccidentScreenRules.RateField);
        form.IncapacityRate = "12,5";

        AccidentScreenRules.Check(new AccidentForm { OccurredAt = Now, Circumstances = "x" }, null, Now).Refusal!.Field
            .Should().Be(AccidentScreenRules.EmployeeField);
        AccidentScreenRules.Check(new AccidentForm { EmployeeId = form.EmployeeId, OccurredAt = Now, Circumstances = " " }, null, Now).Refusal!.Field
            .Should().Be(AccidentScreenRules.CircumstancesField);
        AccidentScreenRules.Check(new AccidentForm { EmployeeId = form.EmployeeId, OccurredAt = Now.AddDays(1), Circumstances = "x" }, null, Now)
            .Refusal!.Field.Should().Be(AccidentScreenRules.OccurredField);

        form.ConsultedOn = Today;
        form.Exposure = ExposureType.MucousSplash;
        form.SourceIdentified = true;
        var (refusal, request) = AccidentScreenRules.Check(form, null, Now);
        refusal.Should().BeNull();
        request!.IncapacityRate.Should().Be(12.5m);
        request.ConsultedAt.Should().Be(new DateTime(2026, 10, 26, 8, 30, 0), "seen the same day: at the accident's hour");
        request.Exposure.Should().BeNull("only a blood exposure has a type");
        request.SourceIdentified.Should().BeFalse();

        form.ConsultedOn = Today.AddDays(-1);
        form.OccurredAt = Now.AddDays(-2);
        form.Kind = AccidentKind.BloodExposure;
        var (_, blood) = AccidentScreenRules.Check(form, null, Now);
        blood!.ConsultedAt.Should().Be(new DateTime(2026, 10, 25, 9, 0, 0));
        blood.Exposure.Should().Be(ExposureType.MucousSplash);

        AccidentScreenRules.FieldOf("Accident_DeclaredBeforeAccident").Should().Be(AccidentScreenRules.DeclaredField);
        AccidentScreenRules.FieldOf("Accident_ReturnDuringStoppage").Should().Be(AccidentScreenRules.DateField);
    }

    [Fact]
    public void A_stoppage_follows_the_last_one_a_return_the_end_of_it_and_a_check_marked_is_the_first_due()
    {
        var approved = new StoppageRow(Guid.NewGuid(), new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 19), 7m, LeaveStatus.Approved);
        AccidentScreenRules.StoppageDefaults(File())
            .Should().Be((new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 19), false));
        AccidentScreenRules.StoppageDefaults(File(closed: true, stoppages: [approved]))
            .Should().Be((new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 26), true));
        AccidentScreenRules.CheckStoppage(File(), new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 14))!.Field
            .Should().Be(AccidentScreenRules.FromField);
        AccidentScreenRules.CheckStoppage(File(), new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 13))
            .Should().Be(new FieldRefusal(AccidentScreenRules.ToField, "Leave_EndBeforeStart"));
        AccidentScreenRules.ReturnDefaults(File(stoppages: [approved])).Should().Be((new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 12)));

        var done = new SerologyCheckRow(Guid.NewGuid(), "J0", new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 12));
        var m1 = new SerologyCheckRow(Guid.NewGuid(), "M1", new DateOnly(2026, 11, 12), null);
        var m3 = new SerologyCheckRow(Guid.NewGuid(), "M3", new DateOnly(2027, 1, 12), null);
        var file = File(checks: [done, m3, m1], kind: AccidentKind.BloodExposure);
        AccidentScreenRules.CheckToMark(file, m3.CheckId).Should().Be(m3);
        AccidentScreenRules.CheckToMark(file, done.CheckId).Should().Be(m1);

        AccidentScreenRules.CheckDate(Today, Today.AddDays(1), null, "Before", "After").Should().Be(new FieldRefusal(AccidentScreenRules.DateField, "Before"));
        AccidentScreenRules.CheckDate(Today, Today.AddDays(-9), Today.AddDays(-1), "Before", "After")!.Key.Should().Be("After");
        AccidentScreenRules.CheckLog(AccidentEventKind.InsurerReminder, null).Should().BeNull();
        AccidentScreenRules.CheckLog(AccidentEventKind.InsurerResponse, " ")!.Field.Should().Be(AccidentScreenRules.EventTextField);
    }
}
