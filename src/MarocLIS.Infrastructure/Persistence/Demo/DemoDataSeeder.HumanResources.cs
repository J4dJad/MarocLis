using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Patients;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Persistence.Demo;

public sealed partial class DemoDataSeeder
{
    // ---- Human resources -------------------------------------------------------------------------

    /// <summary>
    /// Six people on file, four of them the demonstration users, so "Congés" opens on one's own
    /// balance when signed in as one of them. Their papers, habilitations, a few leave requests, this
    /// week's planning, last month's closed payroll and this month's open one.
    /// <para>
    /// The CIN numbers start with "ZD9", which no prefecture issues: they mark the files as
    /// demonstration ones and are what a later run looks for.
    /// </para>
    /// </summary>
    private async Task SeedHumanResourcesAsync(CancellationToken ct)
    {
        var existing = await QueryAsync(db => db.Employees.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(e => e.Cin != null && e.Cin.StartsWith("ZD9"), ct));
        if (existing) return;

        var lookups = await AsAsync<PersonnelService, Result<PersonnelLookups>>(
            DemoActor.Administrator, s => s.GetLookupsAsync(null, ct), ct);
        if (!Check(lookups, "Personnel : listes")) return;

        var department = lookups.Value.Departments.FirstOrDefault()?.Id;
        var instrument = lookups.Value.Instruments.FirstOrDefault()?.Id;
        var today = clock.Today;

        EmployeeRequest Person(string last, string first, EmployeeJob job, string cin, DateOnly hired,
                               decimal salary, Gender gender, DemoActor? account) => new()
        {
            LastName = last, FirstName = first, Job = job, Cin = cin, Gender = gender,
            HireDate = hired, ContractType = ContractType.Permanent, WeeklyHours = 44m,
            BaseSalary = salary, IsExposedToBiologicalRisk = Employee.IsExposedByDefault(job),
            ConfidentialityClauseSignedOn = hired, DepartmentId = job == EmployeeJob.Technician ? department : null,
            UserId = account is { } a && _actors.TryGetValue(a, out var user) ? user : null,
            City = "Casablanca", BankName = "Attijariwafa bank",
            Rib = Rib($"0077800000{cin[3..]}{cin[3..]}00")
        };

        EmployeeRequest[] people =
        [
            Person("Alaoui Belghiti", "Yasmine", EmployeeJob.Biologist, "ZD900001", new(2015, 9, 1), 32000m,
                   Gender.Female, DemoActor.Biologist) with { JobTitle = "Biologiste responsable", FamilyStatus = FamilyStatus.Married, DependentChildren = 2 },
            Person("Aït Ouahmane", "Mehdi", EmployeeJob.Technician, "ZD900002", new(2021, 3, 15), 7800m,
                   Gender.Male, DemoActor.Technician),
            Person("Bouzid", "Hanane", EmployeeJob.Secretary, "ZD900003", new(2019, 1, 7), 5200m,
                   Gender.Female, DemoActor.Secretary) with { FamilyStatus = FamilyStatus.Married, DependentChildren = 1 },
            Person("Kettani", "Rachida", EmployeeJob.Support, "ZD900004", new(2017, 6, 1), 18000m,
                   Gender.Female, DemoActor.QualityManager) with { JobTitle = "Responsable qualité" },
            Person("Tazi", "Nadia", EmployeeJob.Nurse, "ZD900005", new(2023, 10, 2), 6200m, Gender.Female, null),
            Person("Idrissi", "Fatima", EmployeeJob.Cleaner, "ZD900006", new(2024, 2, 1), 3300m, Gender.Female, null)
                with { ConfidentialityClauseSignedOn = null }
        ];

        var ids = new List<Guid>();
        foreach (var person in people)
        {
            var saved = await AsAsync<PersonnelService, Result<Guid>>(
                DemoActor.Administrator, s => s.SaveEmployeeAsync(person, ct), ct);
            if (!Check(saved, $"Salarié {person.LastName}")) return;
            ids.Add(saved.Value);
        }

        var (bio, tech, secretary, _, nurse, cleaner) = (ids[0], ids[1], ids[2], ids[3], ids[4], ids[5]);

        // Papers: one medical visit due soon and one hepatitis B booster overdue, so the deadline
        // list has something to say.
        (Guid Who, CredentialRequest What)[] papers =
        [
            (bio, new() { Kind = CredentialKind.OrderRegistration, Title = "Inscription à l'Ordre des médecins", Reference = "CNOM-DEMO-48213", ObtainedOn = new(2015, 7, 1) }),
            (bio, new() { Kind = CredentialKind.HepatitisBVaccination, Title = "Vaccination hépatite B", ObtainedOn = new(2010, 1, 15) }),
            (tech, new() { Kind = CredentialKind.Diploma, Title = "Diplôme de technicien de laboratoire", IssuedBy = "ISPITS Casablanca", ObtainedOn = new(2020, 7, 1) }),
            (tech, new() { Kind = CredentialKind.MedicalVisit, Title = "Visite périodique", ObtainedOn = today.AddMonths(-11), ExpiresOn = today.AddDays(20), Fitness = MedicalFitness.Fit }),
            (tech, new() { Kind = CredentialKind.HepatitisBVaccination, Title = "Rappel hépatite B", ObtainedOn = today.AddYears(-5), ExpiresOn = today.AddDays(-10) }),
            (nurse, new() { Kind = CredentialKind.Diploma, Title = "Diplôme d'infirmière polyvalente", IssuedBy = "ISPITS Rabat", ObtainedOn = new(2022, 7, 1) }),
            (nurse, new() { Kind = CredentialKind.MedicalVisit, Title = "Visite d'embauche", ObtainedOn = new(2023, 10, 5), ExpiresOn = today.AddMonths(4), Fitness = MedicalFitness.Fit })
        ];

        foreach (var (who, what) in papers)
            Check(await AsAsync<PersonnelService, Result>(DemoActor.Administrator,
                      s => s.AddCredentialAsync(who, what, ct), ct), $"Pièce {what.Title}");

        // Habilitations are signed by the biologist, as the standard requires.
        if (instrument is { } analyser)
            Check(await AsAsync<PersonnelService, Result>(DemoActor.Biologist, s => s.GrantAsync(tech,
                      HabilitationActivity.InstrumentOperation, analyser, HabilitationLevel.Authorized,
                      today.AddMonths(-6), today.AddMonths(6), "Évaluation pratique (démo)", ct), ct), "Habilitation automate");
        if (department is { } bench)
        {
            Check(await AsAsync<PersonnelService, Result>(DemoActor.Biologist, s => s.GrantAsync(tech,
                      HabilitationActivity.TechnicalValidation, bench, HabilitationLevel.Authorized,
                      today.AddYears(-1), today.AddDays(15), "Réévaluation annuelle", ct), ct), "Habilitation validation technique");
            Check(await AsAsync<PersonnelService, Result>(DemoActor.Biologist, s => s.GrantAsync(bio,
                      HabilitationActivity.BiologicalValidation, bench, HabilitationLevel.Authorized,
                      today.AddYears(-1), today.AddYears(1), null, ct), ct), "Habilitation validation biologique");
        }
        Check(await AsAsync<PersonnelService, Result>(DemoActor.Biologist, s => s.GrantAsync(nurse,
                  HabilitationActivity.AdultCollection, null, HabilitationLevel.Trainer,
                  today.AddMonths(-3), today.AddYears(2), null, ct), ct), "Habilitation prélèvement");
        Check(await AsAsync<PersonnelService, Result>(DemoActor.Biologist, s => s.GrantAsync(nurse,
                  HabilitationActivity.PaediatricCollection, null, HabilitationLevel.InTraining,
                  today.AddDays(-20), null, "Formation en cours", ct), ct), "Habilitation prélèvement pédiatrique");

        // The balances start when the file is opened; the days earned before come in as a carry-over.
        foreach (var (who, days) in new[] { (bio, 24m), (tech, 14m), (secretary, 19m), (ids[3], 21m), (nurse, 9m), (cleaner, 6m) })
            Check(await AsAsync<LeaveService, Result>(DemoActor.Administrator,
                      s => s.AdjustAsync(who, days, "Reprise du solde à l'ouverture du dossier", ct), ct), "Reprise du solde de congés");

        // Leave: one approved, one waiting for the biologist, one the technician asked for himself.
        var nextMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(1);
        var approved = await AsAsync<LeaveService, Result<Guid>>(DemoActor.Administrator, s => s.SubmitAsync(new LeaveSubmission
        {
            EmployeeId = secretary, Type = LeaveType.Annual, StartDate = nextMonth.AddDays(7), EndDate = nextMonth.AddDays(11),
            Reason = "Congé annuel"
        }, ct), ct);
        if (Check(approved, "Congé de la secrétaire"))
            Check(await AsAsync<LeaveService, Result>(DemoActor.Biologist, s => s.ApproveAsync(approved.Value, null, ct), ct),
                  "Validation du congé de la secrétaire");

        Check(await AsAsync<LeaveService, Result<Guid>>(DemoActor.Technician, s => s.SubmitAsync(new LeaveSubmission
        {
            Type = LeaveType.Annual, StartDate = nextMonth.AddDays(14), EndDate = nextMonth.AddDays(18), Reason = "Vacances"
        }, ct), ct), "Demande de congé du technicien");

        Check(await AsAsync<LeaveService, Result<Guid>>(DemoActor.Administrator, s => s.SubmitAsync(new LeaveSubmission
        {
            EmployeeId = nurse, Type = LeaveType.Sick, StartDate = today.AddDays(-3), EndDate = today.AddDays(-2),
            Reason = "Certificat médical à fournir"
        }, ct), ct), "Arrêt maladie de l'infirmière");

        // This week's planning, Monday to Saturday.
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        for (var day = 0; day < 6; day++)
        {
            var date = monday.AddDays(day);
            var saturday = day == 5;
            foreach (var (who, kind) in new[] { (tech, saturday ? ShiftKind.Saturday : ShiftKind.Day),
                                                (nurse, saturday ? ShiftKind.Saturday : ShiftKind.Morning),
                                                (cleaner, ShiftKind.Morning) })
            {
                // A day the nurse is on sick leave is refused, as it would be on the screen.
                var shift = await AsAsync<PlanningService, Result<Guid>>(DemoActor.Administrator,
                    s => s.AssignAsync(new ShiftRequest { EmployeeId = who, Date = date, Kind = kind }, ct), ct);
                if (shift.IsFailure && who != nurse) Check(shift, $"Planning du {date:dd/MM}");
            }
        }
        Check(await AsAsync<PlanningService, Result<Guid>>(DemoActor.Administrator, s => s.AssignAsync(
                  new ShiftRequest { EmployeeId = bio, Date = monday.AddDays(6), Kind = ShiftKind.OnCall }, ct), ct),
              "Astreinte du dimanche");

        // Payroll: last month closed, this month open with its payslips to review.
        var last = today.AddMonths(-1);
        var lastPeriod = await AsAsync<PayrollService, Result<Guid>>(DemoActor.Administrator,
            s => s.OpenAsync(last.Year, last.Month, ct), ct);
        if (Check(lastPeriod, "Paie du mois dernier"))
            Check(await AsAsync<PayrollService, Result>(DemoActor.Administrator, s => s.CloseAsync(lastPeriod.Value, ct), ct),
                  "Clôture de la paie du mois dernier");

        Check(await AsAsync<PayrollService, Result<Guid>>(DemoActor.Administrator,
                  s => s.OpenAsync(today.Year, today.Month, ct), ct), "Paie du mois");
    }

    /// <summary>
    /// A needlestick of the nurse last week, declared, its baseline check done; a commuting accident
    /// of the secretary two days ago, not yet declared, with a short stoppage the laboratory advances
    /// for the insurer. Its own section, so a database seeded before the screen existed gets them too.
    /// </summary>
    private async Task SeedAccidentsAsync(CancellationToken ct)
    {
        var people = await QueryAsync(db => db.Employees.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Cin == "ZD900003" || e.Cin == "ZD900005")
            .Select(e => new { e.Id, e.Cin }).ToListAsync(ct));
        var secretary = people.FirstOrDefault(p => p.Cin == "ZD900003")?.Id;
        var nurse = people.FirstOrDefault(p => p.Cin == "ZD900005")?.Id;
        if (secretary is null || nurse is null) return;
        if (await QueryAsync(db => db.WorkAccidents.IgnoreQueryFilters().AnyAsync(a => a.EmployeeId == nurse || a.EmployeeId == secretary, ct)))
            return;

        var today = clock.Today;
        var exposure = await AsAsync<AccidentService, Result<Guid>>(DemoActor.Administrator, s => s.SaveAsync(new AccidentRequest
        {
            EmployeeId = nurse.Value, Kind = AccidentKind.BloodExposure, OccurredAt = today.AddDays(-8).ToDateTime(new TimeOnly(9, 40)),
            Place = "Salle de prélèvement", Exposure = ExposureType.Needlestick, SourceIdentified = true, FirstAidGiven = true,
            Circumstances = "Piqûre au recapuchonnage d'une aiguille après un prélèvement veineux",
            Injuries = "Piqûre superficielle de l'index gauche", ConsultedAt = today.AddDays(-8).ToDateTime(new TimeOnly(11, 0)),
            InsurerName = "Wafa Assurance", InsurerClaimNumber = "AT-DEMO-2210", DeclaredToInsurerOn = today.AddDays(-7)
        }, ct), ct);
        if (Check(exposure, "AES de l'infirmière"))
        {
            var detail = await AsAsync<AccidentService, Result<AccidentDetail>>(DemoActor.Administrator,
                s => s.GetAsync(exposure.Value, ct), ct);
            if (Check(detail, "Suivi de l'AES") && detail.Value.Checks.FirstOrDefault() is { } baseline)
                Check(await AsAsync<AccidentService, Result>(DemoActor.Administrator,
                          s => s.MarkCheckDoneAsync(exposure.Value, baseline.CheckId, today.AddDays(-8), ct), ct), "Bilan initial de l'AES");
        }

        var commuting = await AsAsync<AccidentService, Result<Guid>>(DemoActor.Administrator, s => s.SaveAsync(new AccidentRequest
        {
            EmployeeId = secretary.Value, Kind = AccidentKind.Commuting, OccurredAt = today.AddDays(-2).ToDateTime(new TimeOnly(7, 50)),
            Place = "Boulevard Zerktouni, sur le trajet du domicile", Circumstances = "Chute de scooter en venant au laboratoire",
            Injuries = "Entorse de la cheville droite", Witnesses = "Agent de circulation", Subrogation = true,
            InsurerName = "Wafa Assurance"
        }, ct), ct);
        if (Check(commuting, "Accident de trajet de la secrétaire"))
            Check(await AsAsync<AccidentService, Result>(DemoActor.Administrator,
                      s => s.AddStoppageAsync(commuting.Value, today.AddDays(-1), today.AddDays(3), false, ct), ct),
                  "Arrêt de la secrétaire");
    }

    /// <summary>A RIB with its two check digits, so the file passes the bank-account check.</summary>
    private static string Rib(string body)
    {
        var remainder = body.Aggregate(0, (r, c) => (r * 10 + (c - '0')) % 97);
        return body + ((97 - remainder * 100 % 97) % 97).ToString("00");
    }
}
