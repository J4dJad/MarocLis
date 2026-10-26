using Bunit;
using FluentAssertions;
using MarocLIS.Application.HumanResources;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Web.Components.Screens.HumanResources;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Congés et absences », « Planning », « Paie » and « Accidents du travail » in the browser: what
/// each right offers, the refusals said beside their box, the amounts in dirhams, and what a decision
/// changes, through the services themselves.
/// </summary>
public sealed class WebLeaveAndPayTests : WebHumanResourcesContext
{
    private readonly Employee _youssef, _abdelilah, _omar;

    public WebLeaveAndPayTests()
    {
        _youssef = Hire("M0002", "AÏT OUAHMANE", "Youssef", EmployeeJob.Technician, new DateOnly(2021, 3, 1));
        _abdelilah = Hire("M0007", "EL OUAZZANI TOUHAMI", "Abdelilah", EmployeeJob.Technician, new DateOnly(2025, 10, 1));
        _omar = Hire("M0008", "BENJELLOUN", "Omar", EmployeeJob.Courier, new DateOnly(2022, 4, 1));
    }

    private static readonly string[] Approver =
        [Permissions.HumanResources.View, Permissions.HumanResources.Manage, Permissions.HumanResources.ApproveLeave];

    // ---- Congés et absences --------------------------------------------------------------------------

    private LeaveRequest _sick = null!;

    private void Requests()
    {
        _sick = new LeaveRequest(_youssef.Id, LeaveType.Sick, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 22), false, false, 3m,
                                 "Grippe", false, TestClock.Now, Guid.NewGuid());
        LeaveStore.On(nameof(IRepository<LeaveRequest>.GetByIdAsync), args => (Guid)args[0]! == _sick.Id ? _sick : null);
        LeaveQueries.On(nameof(ILeaveQueries.GetRequestsAsync), _ => (IReadOnlyList<LeaveRow>)
        [
            new LeaveRow(_sick.Id, _youssef.Id, "M0002", "AÏT OUAHMANE Youssef", "Hématologie", LeaveType.Sick, _sick.StartDate, _sick.EndDate,
                         false, false, 3m, "Grippe", false, _sick.Status, TestClock.Now, null, null, 1),
            new LeaveRow(Guid.NewGuid(), _abdelilah.Id, "M0007", "EL OUAZZANI TOUHAMI Abdelilah", "Hématologie", LeaveType.Annual,
                         new DateOnly(2026, 10, 27), new DateOnly(2026, 10, 29), false, false, 3m, "Mariage d'un frère à Fès", false,
                         LeaveStatus.Refused, TestClock.Now, "Dr Lahlou", "Inventaire annuel des réactifs cette semaine-là", 0)
        ]);
    }

    [Fact]
    public void TheRequests_AreListed_TheMissingCertificateInRed_AndTheRefusalWithTheReasonTheEmployeeWillRead()
    {
        Requests();
        SignIn(AppLanguage.French, Approver);
        var page = Render<LeaveScreen>();

        page.FindAll("[data-grid=leave] tbody tr").Should().HaveCount(2);
        page.Find("[data-row=M0002] [data-state=proof]").ClassList.Should().Contain("critical");
        page.Find("[data-row=M0007][data-status=Refused] [data-state=decision]").TextContent
            .Should().Be("Dr Lahlou · Inventaire annuel des réactifs cette semaine-là");
        page.Find("[data-row=M0007]").ClassList.Should().Contain("is-tone-muted");
    }

    [Fact]
    public void AnEmployeeAsksForTheirLeave_ButDecidesNothing()
    {
        Requests();
        SignIn(AppLanguage.French, Permissions.HumanResources.RequestLeave);
        var page = Render<LeaveScreen>();

        page.FindAll(".action-bar [data-action='Leave.New']").Should().ContainSingle();
        page.FindAll(".action-bar [data-action='Leave.Approve']").Should().BeEmpty();
        page.FindAll(".action-bar [data-action='Leave.Refuse']").Should().BeEmpty();
        page.FindAll(".action-bar [data-action='Leave.ProofReceived']").Should().BeEmpty();
    }

    [Fact]
    public void ARequestRefused_TakesTheReasonGiven_AndTheCertificateIsAskedOnlyForAKindThatNeedsOne()
    {
        Requests();
        SignIn(AppLanguage.French, Approver);
        var page = Render<LeaveScreen>();

        page.Find("[data-row=M0007]").Click();
        page.Find(".action-bar [data-action='Leave.Refuse']").HasAttribute("disabled").Should().BeTrue("it is decided already");

        page.Find("[data-row=M0002]").Click();
        page.Find(".action-bar [data-action='Leave.ProofReceived']").HasAttribute("disabled").Should().BeFalse();
        page.Find(".action-bar [data-action='Leave.Refuse']").Click();
        page.Find("[data-modal=reason]").TextContent.Should().Contain("AÏT OUAHMANE Youssef");
        page.Find("[data-modal=reason] [data-field=reason]").Input("Certificat médical non fourni");
        page.Find("[data-modal=reason] [data-action='Action.Ok']").Click();

        page.WaitForAssertion(() => _sick.Status.Should().Be(LeaveStatus.Refused));
        _sick.DecisionComment.Should().Be("Certificat médical non fourni");
        page.FindAll("[data-modal=reason]").Should().BeEmpty();
    }

    // ---- Planning ------------------------------------------------------------------------------------

    private void Month(params ShiftCell[] shifts)
    {
        PlanningQueries.On(nameof(IPlanningQueries.GetEmployeesAsync), _ => (IReadOnlyList<PlanningEmployee>)
                [new PlanningEmployee(_omar.Id, "M0008", "BENJELLOUN Omar", EmployeeJob.Courier, null, _omar.HireDate, null, false)])
                       .On(nameof(IPlanningQueries.GetShiftsAsync), _ => (IReadOnlyList<ShiftCell>)shifts);
    }

    private ShiftCell Post(int day, ShiftKind kind)
    {
        var (start, end) = ShiftRules.DefaultHours(kind);
        return new ShiftCell(Guid.NewGuid(), _omar.Id, new DateOnly(2026, 10, day), kind, start, end, null);
    }

    [Fact]
    public void TheMonth_ShowsEachPersonsDays_AndARestTooShortIsAPointToCheck_NotAnActionWithoutTheRight()
    {
        Month(Post(14, ShiftKind.Night), Post(15, ShiftKind.Morning));
        SignIn(AppLanguage.French, Permissions.HumanResources.View);
        var page = Render<PlanningScreen>();

        page.FindAll("[data-grid=planning] tbody tr").Should().ContainSingle();
        page.FindAll("[data-grid=planning] tbody tr td[data-cell]").Should().HaveCount(31);
        page.Find("td[data-cell='M0008-14']").GetAttribute("data-ink").Should().Be(nameof(PlanningInk.ShiftUnusual));
        page.Find("td[data-cell='M0008-15']").GetAttribute("data-ink").Should().Be(nameof(PlanningInk.ShiftFlagged));
        page.FindAll("[data-grid=planning-issues] tbody tr[data-issue=RestTooShort]").Should().NotBeEmpty();

        page.Find("td[data-cell='M0008-20']").Click();
        page.FindAll(".action-bar [data-action='Planning.Assign']").Should().BeEmpty();
        page.FindAll(".action-bar [data-action='Planning.Clear']").Should().BeEmpty();
        page.FindAll(".action-bar [data-action='Planning.CopyWeek']").Should().BeEmpty();
    }

    [Fact]
    public void APost_IsAssignedToTheDaySelected_WithThePlanningRight()
    {
        Month();
        SignIn(AppLanguage.French, Permissions.HumanResources.View, Permissions.HumanResources.ManagePlanning);
        var page = Render<PlanningScreen>();

        page.Find("td[data-cell='M0008-20']").Click();
        page.Find(".action-bar [data-action='Planning.Clear']").HasAttribute("disabled").Should().BeTrue("the day holds no post");
        page.Find(".action-bar [data-action='Planning.Assign']").Click();

        page.Find("[data-modal=planning-shift]").TextContent.Should().Contain("20/10/2026 — BENJELLOUN Omar");
        page.Find("[data-modal=planning-shift] [data-action='Action.Save']").Click();

        page.WaitForAssertion(() => ShiftStore.CallsTo(nameof(IRepository<ShiftAssignment>.AddAsync)).Should().Be(1));
        var shift = (ShiftAssignment)ShiftStore.Calls.Single(c => c.Method == nameof(IRepository<ShiftAssignment>.AddAsync)).Arguments[0]!;
        shift.EmployeeId.Should().Be(_omar.Id);
        shift.Date.Should().Be(new DateOnly(2026, 10, 20));
    }

    // ---- Paie ----------------------------------------------------------------------------------------

    private PayrollPeriod Payroll(bool closed = false)
    {
        var period = new PayrollPeriod(Guid.NewGuid(), 2026, 10);
        var slip = new Payslip(_youssef.Id) { Number = "M0002", FullName = "AÏT OUAHMANE Youssef", JobTitle = "Technicien", BaseSalary = 7400m, HireDate = _youssef.HireDate };
        slip.Recompute(new PayslipVariables(), new PayrollParameters(2026), period.LastDay);
        period.Add(slip);
        if (closed) period.Close(TestClock.Now, Guid.NewGuid());

        PeriodStore.On(nameof(IRepository<PayrollPeriod>.GetByIdWithDetailsAsync), _ => period);
        PayrollQueries.On(nameof(IPayrollQueries.GetPeriodsAsync), _ => (IReadOnlyList<PayrollPeriodRow>)
            [new PayrollPeriodRow(period.Id, 2026, 10, period.Status, 1, 0m, 0m, 0m, null, null)]);
        return period;
    }

    [Fact]
    public void TheMonth_SaysItsAmountsInDirhams_AndANegativeVariableIsRefusedBesideItsBox()
    {
        var period = Payroll();
        SignIn(AppLanguage.French, HrManager);
        var page = Render<PayrollScreen>();

        page.Find("[data-row=M0002] [data-state=net]").TextContent.Should().EndWith("DH");
        page.Find("[data-state=totals]").TextContent.Should().Contain("DH");

        page.Find("[data-row=M0002]").Click();
        page.Find(".action-bar [data-action='Payroll.Variables']").Click();
        page.Find("[data-modal=payroll-variables] [data-field=payroll-bonus]").Change("-500");
        page.Find("[data-modal=payroll-variables] [data-action='Action.Save']").Click();

        page.Find("[data-refusal=payroll-bonus]").Should().NotBeNull();
        period.Payslips.Single().Variables.TaxableBonus.Should().Be(0m);

        page.Find("[data-modal=payroll-variables] [data-field=payroll-bonus]").Change("500");
        page.Find("[data-modal=payroll-variables] [data-field=payroll-absence]").Change("40");
        page.Find("[data-modal=payroll-variables] [data-action='Action.Save']").Click();
        page.WaitForAssertion(() => page.Find("[data-refusal=payroll-absence]").Should().NotBeNull(), TimeSpan.FromSeconds(5));

        page.Find("[data-modal=payroll-variables] [data-field=payroll-absence]").Change("");
        page.Find("[data-modal=payroll-variables] [data-action='Action.Save']").Click();
        page.WaitForAssertion(() => page.FindAll("[data-modal=payroll-variables]").Should().BeEmpty());
        period.Payslips.Single().Variables.TaxableBonus.Should().Be(500m);
    }

    [Fact]
    public void AClosedMonth_NoLongerTakesVariables_ButGoesToTheBankAndIsReopened()
    {
        Payroll(closed: true);
        SignIn(AppLanguage.French, HrManager);
        var page = Render<PayrollScreen>();

        page.Find("[data-row=M0002]").Click();
        page.Find(".action-bar [data-action='Payroll.Variables']").HasAttribute("disabled").Should().BeTrue();
        page.Find(".action-bar [data-action='Payroll.UpdateFromFiles']").HasAttribute("disabled").Should().BeTrue();
        page.Find(".action-bar [data-action='Payroll.TransferFile']").HasAttribute("disabled").Should().BeFalse();
        page.Find(".action-bar [data-action='Payroll.BdsFile']").HasAttribute("disabled").Should().BeFalse();
        page.FindAll(".action-bar [data-action='Payroll.Reopen']").Should().ContainSingle();
        page.FindAll(".action-bar [data-action='Payroll.Close']").Should().BeEmpty();
    }

    // ---- Accidents du travail ------------------------------------------------------------------------

    private WorkAccident Accidents()
    {
        var cut = new WorkAccident("AT-2026-0003", _abdelilah.Id, Guid.NewGuid(), AccidentKind.WorkAccident, new DateTime(2026, 10, 12, 9, 15, 0))
        {
            Place = "Paillasse d'hématologie", Circumstances = "Coupure en ouvrant un tube bloqué", InsurerName = "Wafa Assurance"
        };
        AccidentStore.On(nameof(IRepository<WorkAccident>.GetByIdWithDetailsAsync), _ => cut);
        AccidentQueries.On(nameof(IAccidentQueries.GetAccidentsAsync), _ => (IReadOnlyList<AccidentRow>)
        [
            new AccidentRow(cut.Id, cut.Number, _abdelilah.Id, "M0007", "EL OUAZZANI TOUHAMI Abdelilah", "Hématologie", cut.Kind,
                            cut.OccurredAt, cut.Place, null, 0m, null, null, false)
        ]);
        return cut;
    }

    [Fact]
    public void ADeclarationLate_IsInTheAlarmsColours_AndTheFileIsRead_ButNotFollowedWithoutTheAccidentRight()
    {
        Accidents();
        SignIn(AppLanguage.French, Permissions.HumanResources.View);
        var page = Render<AccidentScreen>();

        page.FindAll("[data-row=AT-2026-0003] td.is-alarm").Should().NotBeEmpty();
        page.Find("[data-state=summary]").TextContent.Should().Contain("AT-2026-0003").And.Contain("Wafa Assurance");
        page.Find("[data-state=circumstances]").TextContent.Should().Contain("Coupure en ouvrant un tube bloqué");

        page.FindAll(".action-bar [data-action='Accident.Declaration']").Should().ContainSingle();
        page.FindAll(".action-bar [data-action='Accident.New']").Should().BeEmpty();
        page.FindAll(".action-bar [data-action='Accident.Edit']").Should().BeEmpty();
        page.FindAll(".action-bar [data-action='Accident.Close']").Should().BeEmpty();
    }

    [Fact]
    public void ARateOverAHundred_IsRefusedBesideItsBox_AndNothingIsDeclared()
    {
        Accidents();
        SignIn(AppLanguage.French, Permissions.HumanResources.View, Permissions.HumanResources.Accidents);
        var page = Render<AccidentScreen>();

        page.Find(".action-bar [data-action='Accident.New']").Click();
        page.Find("[data-modal=accident] [data-field=accident-circumstances]").Input("Piqûre en recapuchonnant une aiguille");
        page.Find("[data-modal=accident] [data-field=accident-rate]").Input("140");
        page.Find("[data-modal=accident] [data-action='Action.Save']").Click();

        page.Find("[data-refusal=accident-rate]").Should().NotBeNull();
        AccidentStore.CallsTo(nameof(IRepository<WorkAccident>.AddAsync)).Should().Be(0);
        Work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(0);
    }
}
