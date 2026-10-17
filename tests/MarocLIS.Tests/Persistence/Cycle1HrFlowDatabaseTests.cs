using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.HumanResources;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the HR circuit, created from the model and dropped at the end.</summary>
public sealed class Cycle1HrScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_Cycle1Hr_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public Cycle1HrScratchDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            ConnectionString = "";
            return;
        }

        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = Name }.ConnectionString;

        using var context = Context();
        context.Database.EnsureCreated();
        _created = true;
    }

    public LisDbContext Context() =>
        new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(ConnectionString).Options);

    public void Dispose()
    {
        if (!_created) return;
        SqlConnection.ClearAllPools();
        using var context = Context();
        context.Database.EnsureDeleted();
    }
}

/// <summary>
/// The personnel circuit end to end, against SQL Server and the services as the desktop composes
/// them: hire, documents and habilitation with their deadlines, leave asked and approved, the
/// planning, a payroll month opened, changed and closed, then the departure with its final
/// settlement, the certificates and the closing of the month of departure; the work accident.
/// </summary>
public sealed class Cycle1HrFlowDatabaseTests(Cycle1HrScratchDatabase database)
    : IClassFixture<Cycle1HrScratchDatabase>
{
    /// <summary>A clock the test moves forward, as the days pass between two steps of the circuit.</summary>
    private sealed class MovableClock : IClock
    {
        public DateOnly Today { get; set; } = DateOnly.FromDateTime(DateTime.Today);
        public DateTimeOffset Now => new(Today.ToDateTime(new TimeOnly(10, 0)));
    }

    /// <summary>The HR manager, a biologist holding every right, signed in on the test's site.</summary>
    private sealed class Operator(Guid userId, Guid siteId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId => userId;
        public string UserName => "rh";
        public string FullName => "Dr Ressources Humaines";
        public Guid SiteId => siteId;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.MedicalBiologist;
        public IReadOnlyList<Guid> AccessibleSiteIds => [siteId];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>(MarocLIS.Domain.Identity.Permissions.All);
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => Permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "RH-1";
    }

    private sealed record Lab(Guid SiteId, Guid UserId, ServiceProvider Services, MovableClock Clock);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string Rib()
    {
        var first22 = string.Concat(Enumerable.Range(0, 22).Select(_ => Random.Shared.Next(10)));
        var remainder = 0;
        foreach (var c in first22 + "00") remainder = (remainder * 10 + (c - '0')) % 97;
        return first22 + ((97 - remainder) % 97).ToString("00");
    }

    private async Task<Lab> LabAsync()
    {
        var user = new AppUser(Unique("rh"), "Dr Ressources Humaines", "x")
        {
            Qualification = ProfessionalQualification.MedicalBiologist,
            ProfessionalOrderNumber = "CNOM-RH-1"
        };
        var site = new LabSite(Unique("L"), new MultilingualText("Laboratoire RH"), LabSiteKind.MainLaboratory)
        {
            LegalName = "LABORATOIRE RH SARL",
            Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(),
            FiscalId = "45128796",
            AddressLine = new MultilingualText("1, rue du Test"),
            City = "Casablanca",
            HealthMinistryAuthorizationNumber = "AUT-RH-1",
            ResponsibleBiologistUserId = user.Id
        };

        await using (var context = database.Context())
        {
            context.Users.Add(user);
            context.Sites.Add(site);
            await context.SaveChangesAsync();
        }

        var clock = new MovableClock();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(user.Id, site.Id));
        services.AddSingleton<IClock>(clock);
        services.AddIsolatedQueries<IPersonnelQueries, PersonnelQueries>();
        services.AddIsolatedQueries<ILeaveQueries, LeaveQueries>();
        services.AddIsolatedQueries<IPlanningQueries, PlanningQueries>();
        services.AddIsolatedQueries<IPayrollQueries, PayrollQueries>();
        services.AddIsolatedQueries<IHrDocumentQueries, HrDocumentQueries>();
        services.AddIsolatedQueries<IAccidentQueries, AccidentQueries>();
        return new Lab(site.Id, user.Id, services.BuildServiceProvider(), clock);
    }

    /// <summary>Runs one use case in a scope of its own, as a screen does.</summary>
    private static async Task<T> On<TService, T>(Lab lab, Func<TService, Task<T>> action) where TService : notnull
    {
        using var scope = lab.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static void Ok(Result result) =>
        result.IsSuccess.Should().BeTrue(string.Join(", ", result.Errors.Select(e => e.ResourceKey)));

    private static T Ok<T>(Result<T> result)
    {
        result.IsSuccess.Should().BeTrue(string.Join(", ", result.Errors.Select(e => e.ResourceKey)));
        return result.Value;
    }

    private static void Refused(Result result, string key) =>
        result.Errors.Select(e => e.ResourceKey).Should().Contain(key);

    private static EmployeeRequest Technician(string cin) => new()
    {
        LastName = "Alaoui",
        FirstName = "Salma",
        Gender = Gender.Female,
        BirthDate = new DateOnly(1990, 5, 4),
        Cin = cin,
        CnssNumber = "123456789",
        FamilyStatus = FamilyStatus.Married,
        DependentChildren = 1,
        Job = EmployeeJob.Technician,
        JobTitle = "Technicienne de laboratoire",
        IsExposedToBiologicalRisk = true,
        ConfidentialityClauseSignedOn = new DateOnly(2024, 3, 1),
        ContractType = ContractType.Permanent,
        HireDate = new DateOnly(2024, 3, 1),
        BaseSalary = 6_000m,
        Rib = Rib(),
        BankName = "Banque Test"
    };

    /// <summary>The Monday at least <paramref name="days"/> days after <paramref name="from"/>.</summary>
    private static DateOnly MondayAfter(DateOnly from, int days)
    {
        var day = from.AddDays(days);
        while (day.DayOfWeek != DayOfWeek.Monday) day = day.AddDays(1);
        return day;
    }

    private static async Task<Guid> HireAsync(Lab lab, string? cin = null) =>
        Ok(await On<PersonnelService, Result<Guid>>(lab, s => s.SaveEmployeeAsync(Technician(cin ?? Unique("BK")))));

    private static async Task<decimal> BalanceAsync(Lab lab, Guid employeeId) =>
        Ok(await On<LeaveService, Result<IReadOnlyList<LeaveBalanceRow>>>(lab, s => s.GetBalancesAsync()))
            .Single(b => b.EmployeeId == employeeId).Balance;

    private static async Task<Guid> ApprovedLeaveAsync(Lab lab, Guid employeeId, DateOnly start, DateOnly end)
    {
        var leaveId = Ok(await On<LeaveService, Result<Guid>>(lab, s => s.SubmitAsync(new LeaveSubmission
        {
            EmployeeId = employeeId, Type = LeaveType.Annual, StartDate = start, EndDate = end
        })));
        Ok(await On<LeaveService, Result>(lab, s => s.ApproveAsync(leaveId, null)));
        return leaveId;
    }

    [SqlServerFact]
    public async Task HireToDeparture_RunsEndToEnd()
    {
        var lab = await LabAsync();
        var today = lab.Clock.Today;

        // Hire, and the same CIN refused on a second file.
        var cin = Unique("BK");
        var employeeId = await HireAsync(lab, cin);
        var file = Ok(await On<PersonnelService, Result<EmployeeDetail>>(lab, s => s.GetEmployeeAsync(employeeId)));
        file.Number.Should().NotBeNullOrWhiteSpace();
        file.Request.LastName.Should().Be("ALAOUI");
        file.Request.BaseSalary.Should().Be(6_000m);
        Refused(await On<PersonnelService, Result<Guid>>(lab, s => s.SaveEmployeeAsync(Technician(cin.ToLowerInvariant()))),
                "Employee_CinAlreadyUsed");

        // A medical visit and a habilitation, each coming due within the month.
        Ok(await On<PersonnelService, Result>(lab, s => s.AddCredentialAsync(employeeId, new CredentialRequest
        {
            Kind = CredentialKind.MedicalVisit, Title = "Visite médicale", Fitness = MedicalFitness.Fit,
            ObtainedOn = today.AddYears(-1), ExpiresOn = today.AddDays(10)
        })));
        Ok(await On<PersonnelService, Result>(lab, s => s.AddCredentialAsync(employeeId, new CredentialRequest
        {
            Kind = CredentialKind.HepatitisBVaccination, Title = "Hépatite B", ObtainedOn = new DateOnly(2020, 1, 1)
        })));
        Ok(await On<PersonnelService, Result>(lab, s => s.GrantAsync(employeeId, HabilitationActivity.AdultCollection, null,
                                                                      HabilitationLevel.Authorized, today.AddDays(-5),
                                                                      today.AddDays(20), "Évaluation sur 20 prélèvements")));
        Refused(await On<PersonnelService, Result>(lab, s => s.GrantAsync(employeeId, HabilitationActivity.BiologicalValidation,
                                                                           null, HabilitationLevel.InTraining, today, null, null)),
                "Habilitation_BiologicalValidationBiologistOnly");
        Refused(await On<PersonnelService, Result>(lab, s => s.GrantAsync(employeeId, HabilitationActivity.SampleReception,
                                                                           null, HabilitationLevel.InTraining, today.AddDays(1),
                                                                           null, null)),
                "Habilitation_GrantInFuture");

        var deadlines = Ok(await On<PersonnelService, Result<IReadOnlyList<DeadlineRow>>>(lab, s => s.GetDeadlinesAsync(30)))
            .Where(d => d.EmployeeId == employeeId).ToList();
        deadlines.Should().Contain(d => d.Kind == DeadlineKind.CredentialExpiry && d.DueOn == today.AddDays(10));
        deadlines.Should().Contain(d => d.Kind == DeadlineKind.HabilitationReview && d.DueOn == today.AddDays(20)
                                        && d.Activity == HabilitationActivity.AdultCollection);
        deadlines.Should().NotContain(d => d.Kind == DeadlineKind.MissingHepatitisB);
        Ok(await On<PersonnelService, Result<HabilitationMatrix>>(lab, s => s.GetMatrixAsync()))
            .Habilitations.Should().ContainSingle(h => h.EmployeeId == employeeId);

        // Leave: the opening balance, a week asked and approved.
        Ok(await On<LeaveService, Result>(lab, s => s.AdjustAsync(employeeId, 18m, "Reliquat à la reprise du dossier")));
        var start = MondayAfter(today, 14);
        var days = Ok(await On<LeaveService, Result<decimal>>(lab, s => s.PreviewDaysAsync(LeaveType.Annual, start,
                                                                                             start.AddDays(4), false, false)));
        var before = await BalanceAsync(lab, employeeId);
        await ApprovedLeaveAsync(lab, employeeId, start, start.AddDays(4));
        (await BalanceAsync(lab, employeeId)).Should().Be(before - days);

        // Planning: not on a day of leave, a post on another day.
        Refused(await On<PlanningService, Result<Guid>>(lab, s => s.AssignAsync(new ShiftRequest
        {
            EmployeeId = employeeId, Date = start.AddDays(1), Kind = ShiftKind.Morning
        })), "Planning_OnLeave");
        var shiftDay = start.AddDays(-3);
        Ok(await On<PlanningService, Result<Guid>>(lab, s => s.AssignAsync(new ShiftRequest
        {
            EmployeeId = employeeId, Date = shiftDay, Kind = ShiftKind.Morning
        })));
        Ok(await On<PlanningService, Result<PlanningMonth>>(lab, s => s.GetMonthAsync(shiftDay.Year, shiftDay.Month)))
            .Shifts.Should().Contain(c => c.EmployeeId == employeeId && c.Date == shiftDay);

        // Payroll of the current month: opened, a bonus typed, closed, printed.
        var periodId = Ok(await On<PayrollService, Result<Guid>>(lab, s => s.OpenAsync(today.Year, today.Month)));
        var month = Ok(await On<PayrollService, Result<PayrollPeriodDetail>>(lab, s => s.GetPeriodAsync(periodId)));
        var slip = month.Payslips.Single(p => p.EmployeeId == employeeId);
        slip.BaseSalary.Should().Be(6_000m);
        Ok(await On<PayrollService, Result>(lab, s => s.UpdateVariablesAsync(periodId, slip.PayslipId,
                                                                              slip.Variables with { TaxableBonus = 500m })));
        var bonused = Ok(await On<PayrollService, Result<PayrollPeriodDetail>>(lab, s => s.GetPeriodAsync(periodId)))
            .Payslips.Single(p => p.EmployeeId == employeeId);
        bonused.Amounts.Gross.Should().Be(slip.Amounts.Gross + 500m);
        Ok(await On<PayrollService, Result>(lab, s => s.CloseAsync(periodId)));
        Refused(await On<PayrollService, Result>(lab, s => s.UpdateVariablesAsync(periodId, slip.PayslipId, slip.Variables)),
                "Payroll_Closed");
        var printed = Ok(await On<PayrollService, Result<IReadOnlyList<PayslipDocument>>>(lab, s => s.GetDocumentsAsync(periodId, null)));
        printed.Should().ContainSingle(d => d.Payslip.EmployeeId == employeeId && d.Status == PayrollStatus.Closed);
        printed[0].Payslip.Amounts.NetPay.Should().BeGreaterThan(0m);

        // The departure, after the leave: the settlement proposes what is left on the account.
        var exit = new DateOnly(start.AddMonths(1).Year, start.AddMonths(1).Month, 1).AddDays(-1);
        Ok(await On<PersonnelService, Result<DepartedAccountOutcome?>>(lab, s => s.RecordExitAsync(employeeId, exit, ExitReason.Resignation,
                                                                           "Démission, préavis effectué")));
        // The notice is worked with the habilitations: they end on the exit day, not before.
        Ok(await On<PersonnelService, Result<HabilitationMatrix>>(lab, s => s.GetMatrixAsync()))
            .Habilitations.Should().Contain(h => h.EmployeeId == employeeId);

        var settlement = Ok(await On<SettlementService, Result<SettlementDetail>>(lab, s => s.GetAsync(employeeId)));
        settlement.Suggested.LeaveDays.Should().BeGreaterThan(0m);
        settlement.Suggested.SeverancePay.Should().Be(0m, "a resignation carries no severance pay");
        Ok(await On<SettlementService, Result<SettlementPayroll>>(lab, s => s.SaveAsync(employeeId, settlement.Suggested)))
            .Should().Be(SettlementPayroll.NotOpened);

        Ok(await On<HrDocumentService, Result<HrDocument>>(lab, s => s.GetAsync(HrDocumentKind.WorkCertificate, employeeId)));
        Ok(await On<HrDocumentService, Result<HrDocument>>(lab, s => s.GetAsync(HrDocumentKind.EmploymentAttestation, employeeId)));
        Ok(await On<HrDocumentService, Result<HrDocument>>(lab, s => s.GetAsync(HrDocumentKind.SalaryAttestation, employeeId)))
            .SalaryMonths.Should().ContainSingle(m => m.Year == today.Year && m.Month == today.Month);

        // The month of departure, paid once it is over: the indemnity rides on its payslip and its
        // closing takes the days paid off the account.
        lab.Clock.Today = exit.AddDays(2);
        var exitMonthId = Ok(await On<PayrollService, Result<Guid>>(lab, s => s.OpenAsync(exit.Year, exit.Month)));
        var final = Ok(await On<PayrollService, Result<PayrollPeriodDetail>>(lab, s => s.GetPeriodAsync(exitMonthId)))
            .Payslips.Single(p => p.EmployeeId == employeeId);
        final.Variables.LeaveIndemnity.Should().Be(settlement.Suggested.LeaveIndemnity);
        Ok(await On<PayrollService, Result>(lab, s => s.CloseAsync(exitMonthId)));

        Ok(await On<SettlementService, Result>(lab, s => s.SignAsync(employeeId, lab.Clock.Today)));
        Refused(await On<SettlementService, Result<SettlementPayroll>>(lab, s => s.SaveAsync(employeeId, settlement.Suggested)),
                "Settlement_Signed");
        Refused(await On<HrDocumentService, Result<HrDocument>>(lab, s => s.GetAsync(HrDocumentKind.EmploymentAttestation, employeeId)),
                "HrDocument_EmployeeExited");
        Ok(await On<PersonnelService, Result<HabilitationMatrix>>(lab, s => s.GetMatrixAsync()))
            .Habilitations.Should().NotContain(h => h.EmployeeId == employeeId, "no habilitation outlives the exit day");

        await using var context = database.Context();
        var paidOff = await context.LeaveLedger.AsNoTracking()
            .Where(l => l.EmployeeId == employeeId && l.Reference == SettlementService.SettledLeaveReference(exit))
            .SumAsync(l => l.Days);
        paidOff.Should().Be(-settlement.Suggested.LeaveDays);
    }

    /// <summary>
    /// A departure recorded before a leave already approved or asked for: the leave would stay
    /// booked on days the employee no longer works, its days taken off the account the settlement
    /// pays. The exit is refused until the leave is withdrawn, after which the settlement pays the
    /// whole balance.
    /// </summary>
    [SqlServerFact]
    public async Task AnExit_BeforeALiveLeave_IsRefusedUntilTheLeaveIsWithdrawn()
    {
        var lab = await LabAsync();
        var today = lab.Clock.Today;
        var employeeId = await HireAsync(lab);
        Ok(await On<LeaveService, Result>(lab, s => s.AdjustAsync(employeeId, 18m, "Reliquat à la reprise du dossier")));
        var balance = await BalanceAsync(lab, employeeId);

        var start = MondayAfter(today, 21);
        var leaveId = await ApprovedLeaveAsync(lab, employeeId, start, start.AddDays(4));
        var exit = start.AddDays(-3);

        Refused(await On<PersonnelService, Result<DepartedAccountOutcome?>>(lab, s => s.RecordExitAsync(employeeId, exit, ExitReason.Resignation, null)),
                "Employee_LeaveAfterExit");

        // Moved to a date inside the leave, a correction is refused the same way.
        Ok(await On<PersonnelService, Result<DepartedAccountOutcome?>>(lab, s => s.RecordExitAsync(employeeId, start.AddDays(10), ExitReason.Resignation, null)));
        Refused(await On<PersonnelService, Result<DepartedAccountOutcome?>>(lab, s => s.CorrectExitAsync(employeeId, start.AddDays(2), ExitReason.Resignation, null)),
                "Employee_LeaveAfterExit");
        Ok(await On<PersonnelService, Result<DepartedAccountOutcome?>>(lab, s => s.CancelExitAsync(employeeId, "Saisie à refaire")));

        Ok(await On<LeaveService, Result>(lab, s => s.CancelAsync(leaveId, "Départ avant le congé")));
        Ok(await On<PersonnelService, Result<DepartedAccountOutcome?>>(lab, s => s.RecordExitAsync(employeeId, exit, ExitReason.Resignation, null)));

        var settlement = Ok(await On<SettlementService, Result<SettlementDetail>>(lab, s => s.GetAsync(employeeId)));
        settlement.Suggested.LeaveDays.Should().BeGreaterThanOrEqualTo(balance);
    }

    /// <summary>
    /// A blood exposure: numbered, due to the insurer within 48 hours, serology scheduled; declared,
    /// a stoppage that the planning then respects, and no closing while a check is pending.
    /// </summary>
    [SqlServerFact]
    public async Task ABloodExposure_IsDeclaredFollowedAndKeptOpenUntilItsChecks()
    {
        var lab = await LabAsync();
        var today = lab.Clock.Today;
        var employeeId = await HireAsync(lab);

        var occurred = today.AddDays(-1).ToDateTime(new TimeOnly(9, 30));
        var accidentId = Ok(await On<AccidentService, Result<Guid>>(lab, s => s.SaveAsync(new AccidentRequest
        {
            EmployeeId = employeeId, Kind = AccidentKind.BloodExposure, OccurredAt = occurred,
            Place = "Salle de prélèvement", Circumstances = "Piqûre en recapuchonnant une aiguille",
            Exposure = ExposureType.Needlestick, SourceIdentified = true, FirstAidGiven = true
        })));

        var detail = Ok(await On<AccidentService, Result<AccidentDetail>>(lab, s => s.GetAsync(accidentId)));
        detail.Number.Should().NotBeNullOrWhiteSpace();
        detail.Checks.Should().NotBeEmpty();
        Ok(await On<PersonnelService, Result<IReadOnlyList<DeadlineRow>>>(lab, s => s.GetDeadlinesAsync(400)))
            .Should().Contain(d => d.EmployeeId == employeeId && d.Kind == DeadlineKind.AccidentDeclaration);

        Ok(await On<AccidentService, Result<Guid>>(lab, s => s.SaveAsync(detail.Request with
        {
            InsurerName = "Assurance Test", InsurerClaimNumber = "SIN-1", DeclaredToInsurerOn = today
        })));
        Ok(await On<PersonnelService, Result<IReadOnlyList<DeadlineRow>>>(lab, s => s.GetDeadlinesAsync(400)))
            .Should().NotContain(d => d.EmployeeId == employeeId && d.Kind == DeadlineKind.AccidentDeclaration);

        Ok(await On<AccidentService, Result>(lab, s => s.AddStoppageAsync(accidentId, today.AddDays(1), today.AddDays(3), false)));
        Refused(await On<PlanningService, Result<Guid>>(lab, s => s.AssignAsync(new ShiftRequest
        {
            EmployeeId = employeeId, Date = today.AddDays(2), Kind = ShiftKind.Morning
        })), "Planning_OnLeave");

        Refused(await On<AccidentService, Result>(lab, s => s.CloseAsync(accidentId, null)), "Accident_ChecksPending");
    }
}
