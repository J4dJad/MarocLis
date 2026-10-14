using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.HumanResources;

// ---- Read models ---------------------------------------------------------------------------------

public sealed record EmployeeRow(
    Guid EmployeeId,
    string Number,
    string FullName,
    EmployeeJob Job,
    string? JobTitle,
    string? DepartmentName,
    ContractType ContractType,
    DateOnly HireDate,
    DateOnly? ContractEndDate,
    string? Phone,
    string? UserName,
    DateOnly? ExitDate,
    ExitReason? ExitReason);

/// <summary>Everything the personnel form edits. Salary and bank details are null without the right.</summary>
public sealed record EmployeeRequest
{
    public Guid? EmployeeId { get; init; }
    public required string LastName { get; init; }
    public required string FirstName { get; init; }
    public string? FullNameArabic { get; init; }
    public Gender Gender { get; init; }
    public DateOnly? BirthDate { get; init; }
    public string? Cin { get; init; }
    public string? CnssNumber { get; init; }
    public FamilyStatus FamilyStatus { get; init; }
    public int DependentChildren { get; init; }
    public string? Address { get; init; }
    public string? City { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }

    public EmployeeJob Job { get; init; }
    public string? JobTitle { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? UserId { get; init; }
    public bool IsExposedToBiologicalRisk { get; init; }
    public DateOnly? ConfidentialityClauseSignedOn { get; init; }

    public ContractType ContractType { get; init; }
    public DateOnly HireDate { get; init; }
    public DateOnly? ContractEndDate { get; init; }
    public DateOnly? TrialPeriodEndDate { get; init; }
    public decimal WeeklyHours { get; init; } = 44m;

    public decimal? BaseSalary { get; init; }
    public string? Rib { get; init; }
    public string? BankName { get; init; }
}

public sealed record CredentialRow(
    Guid CredentialId,
    CredentialKind Kind,
    string Title,
    string? Reference,
    string? IssuedBy,
    DateOnly? ObtainedOn,
    DateOnly? ExpiresOn,
    MedicalFitness? Fitness,
    string? Note);

public sealed record EmployeeDetail(
    EmployeeRequest Request,
    string Number,
    bool CanSeeSalary,
    DateOnly? ExitDate,
    ExitReason? ExitReason,
    string? ExitComment,
    IReadOnlyList<CredentialRow> Credentials);

public sealed record CredentialRequest
{
    public required CredentialKind Kind { get; init; }
    public required string Title { get; init; }
    public string? Reference { get; init; }
    public string? IssuedBy { get; init; }
    public DateOnly? ObtainedOn { get; init; }
    public DateOnly? ExpiresOn { get; init; }
    public MedicalFitness? Fitness { get; init; }
    public string? Note { get; init; }
}

public sealed record HabilitationRow(
    Guid HabilitationId,
    Guid EmployeeId,
    HabilitationActivity Activity,
    Guid? SubjectId,
    string? SubjectLabel,
    HabilitationLevel Level,
    DateOnly GrantedOn,
    string GrantedBy,
    DateOnly? ReviewDueOn,
    string? Evidence);

public sealed record HabilitationMatrix(
    IReadOnlyList<EmployeeRow> Employees,
    IReadOnlyList<HabilitationRow> Habilitations);

/// <summary>A department or an analyser a habilitation can be about.</summary>
public sealed record HabilitationSubject(Guid Id, string Label, bool IsInstrument);

public sealed record LinkableUser(Guid UserId, string UserName, string FullName);

public sealed record PersonnelLookups(
    IReadOnlyList<HabilitationSubject> Departments,
    IReadOnlyList<HabilitationSubject> Instruments,
    IReadOnlyList<LinkableUser> Users);

/// <summary>What the deadline list needs to know about one active employee.</summary>
public sealed record EmployeeDeadlineFacts(
    Guid EmployeeId,
    string Number,
    string FullName,
    EmployeeJob Job,
    bool IsExposedToBiologicalRisk,
    DateOnly? ConfidentialityClauseSignedOn,
    ContractType ContractType,
    DateOnly? ContractEndDate,
    DateOnly? TrialPeriodEndDate,
    IReadOnlyList<(CredentialKind Kind, string Title, DateOnly? ExpiresOn, MedicalFitness? Fitness, DateOnly? ObtainedOn)> Credentials,
    IReadOnlyList<(HabilitationActivity Activity, string? SubjectLabel, DateOnly? ReviewDueOn)> Habilitations);

public enum DeadlineKind
{
    CredentialExpiry = 0,
    HabilitationReview = 1,
    ContractEnd = 2,
    TrialPeriodEnd = 3,
    MissingHepatitisB = 4,
    MissingConfidentialityClause = 5,
    MissingOrderRegistration = 6,
    MissingMedicalVisit = 7,
    MedicallyUnfit = 8
}

/// <summary>One thing HR must act on. <see cref="DueOn"/> is null for something missing altogether.</summary>
public sealed record DeadlineRow(
    Guid EmployeeId,
    string Number,
    string FullName,
    EmployeeJob Job,
    DeadlineKind Kind,
    string? Subject,
    HabilitationActivity? Activity,
    DateOnly? DueOn,
    bool IsOverdue);

/// <summary>
/// The deadline rules, apart from the database so they can be tested and read in one place.
/// </summary>
public static class PersonnelDeadlines
{
    public static IReadOnlyList<DeadlineRow> Compute(IEnumerable<EmployeeDeadlineFacts> employees,
                                                     DateOnly today, int horizonDays)
    {
        var limit = today.AddDays(horizonDays);
        var rows = new List<DeadlineRow>();

        foreach (var e in employees)
        {
            void Add(DeadlineKind kind, string? subject, DateOnly? due, HabilitationActivity? activity = null) =>
                rows.Add(new DeadlineRow(e.EmployeeId, e.Number, e.FullName, e.Job, kind, subject, activity,
                                         due, due is null || due < today));

            // A renewed document replaces the old one: only the latest expiry of each document counts,
            // and a document with no expiry at all never comes due.
            foreach (var group in e.Credentials
                         .Where(c => c.Kind != CredentialKind.MedicalVisit)
                         .GroupBy(c => (c.Kind, Title: c.Title.Trim().ToUpperInvariant())))
            {
                if (group.Any(c => c.ExpiresOn is null)) continue;
                var latest = group.Max(c => c.ExpiresOn)!.Value;
                if (latest <= limit) Add(DeadlineKind.CredentialExpiry, group.First().Title, latest);
            }

            var visits = e.Credentials.Where(c => c.Kind == CredentialKind.MedicalVisit)
                                      .OrderByDescending(c => c.ObtainedOn ?? DateOnly.MinValue)
                                      .ToList();
            if (visits.Count == 0)
                Add(DeadlineKind.MissingMedicalVisit, null, null);
            else
            {
                var last = visits[0];
                if (last.Fitness == MedicalFitness.Unfit)
                    Add(DeadlineKind.MedicallyUnfit, last.Title, null);
                else if (last.ExpiresOn is { } next && next <= limit)
                    Add(DeadlineKind.CredentialExpiry, last.Title, next);
            }

            if (e.IsExposedToBiologicalRisk
                && !e.Credentials.Any(c => c.Kind == CredentialKind.HepatitisBVaccination))
                Add(DeadlineKind.MissingHepatitisB, null, null);

            if (e.ConfidentialityClauseSignedOn is null)
                Add(DeadlineKind.MissingConfidentialityClause, null, null);

            if (e.Job == EmployeeJob.Biologist
                && !e.Credentials.Any(c => c.Kind == CredentialKind.OrderRegistration))
                Add(DeadlineKind.MissingOrderRegistration, null, null);

            if (e.ContractEndDate is { } end && end <= limit)
                Add(DeadlineKind.ContractEnd, null, end);

            if (e.TrialPeriodEndDate is { } trial && trial <= limit && trial >= today.AddDays(-30))
                Add(DeadlineKind.TrialPeriodEnd, null, trial);

            foreach (var h in e.Habilitations)
                if (h.ReviewDueOn is { } due && due <= limit)
                    Add(DeadlineKind.HabilitationReview, h.SubjectLabel, due, h.Activity);
        }

        // Missing things and overdue ones first, then by date.
        return
        [
            .. rows.OrderByDescending(r => r.IsOverdue)
                   .ThenBy(r => r.DueOn ?? DateOnly.MinValue)
                   .ThenBy(r => r.FullName, StringComparer.CurrentCulture)
        ];
    }
}

// ---- Service ---------------------------------------------------------------------------------------

/// <summary>
/// The personnel files, their documents and the habilitation matrix.
/// <para>
/// Salary and bank details travel only with <see cref="Permissions.HumanResources.Salary"/>: a
/// biologist who keeps the habilitations up to date has no reason to read a colleague's pay, so
/// without the right those fields come back empty and a save leaves them as they were.
/// </para>
/// </summary>
public sealed class PersonnelService(
    IRepository<Employee> employees,
    IPersonnelQueries queries,
    ISequenceAllocator sequences,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    private bool CanSeeSalary => currentUser.Permissions.Contains(Permissions.HumanResources.Salary);

    public async Task<Result<IReadOnlyList<EmployeeRow>>> GetEmployeesAsync(bool includeExited,
                                                                             CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<EmployeeRow>>(permitted.FirstError);

        return Result.Success(await queries.GetEmployeesAsync(includeExited, ct));
    }

    public async Task<Result<PersonnelLookups>> GetLookupsAsync(Guid? forEmployeeId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.View);
        if (permitted.IsFailure) return Result.Failure<PersonnelLookups>(permitted.FirstError);

        return Result.Success(await queries.GetLookupsAsync(forEmployeeId, ct));
    }

    public async Task<Result<EmployeeDetail>> GetEmployeeAsync(Guid employeeId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.View);
        if (permitted.IsFailure) return Result.Failure<EmployeeDetail>(permitted.FirstError);

        var e = await employees.GetByIdWithDetailsAsync(employeeId, ct);
        if (e is null) return Result.Failure<EmployeeDetail>(Error.NotFound("Employee_NotFound"));

        var salary = CanSeeSalary;

        var request = new EmployeeRequest
        {
            EmployeeId = e.Id,
            LastName = e.LastName,
            FirstName = e.FirstName,
            FullNameArabic = e.FullNameArabic,
            Gender = e.Gender,
            BirthDate = e.BirthDate,
            Cin = e.Cin,
            CnssNumber = e.CnssNumber,
            FamilyStatus = e.FamilyStatus,
            DependentChildren = e.DependentChildren,
            Address = e.Address,
            City = e.City,
            Phone = e.Phone,
            Email = e.Email,
            Job = e.Job,
            JobTitle = e.JobTitle,
            DepartmentId = e.DepartmentId,
            UserId = e.UserId,
            IsExposedToBiologicalRisk = e.IsExposedToBiologicalRisk,
            ConfidentialityClauseSignedOn = e.ConfidentialityClauseSignedOn,
            ContractType = e.ContractType,
            HireDate = e.HireDate,
            ContractEndDate = e.ContractEndDate,
            TrialPeriodEndDate = e.TrialPeriodEndDate,
            WeeklyHours = e.WeeklyHours,
            BaseSalary = salary ? e.BaseSalary : null,
            Rib = salary ? e.Rib : null,
            BankName = salary ? e.BankName : null
        };

        var credentials = e.Credentials
            .OrderBy(c => c.Kind).ThenByDescending(c => c.ObtainedOn ?? DateOnly.MinValue)
            .Select(c => new CredentialRow(c.Id, c.Kind, c.Title, c.Reference, c.IssuedBy,
                                           c.ObtainedOn, c.ExpiresOn, c.Fitness, c.Note))
            .ToList();

        return Result.Success(new EmployeeDetail(request, e.Number, salary, e.ExitDate, e.ExitReason,
                                                 e.ExitComment, credentials));
    }

    public async Task<Result<Guid>> SaveEmployeeAsync(EmployeeRequest request, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.Manage);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        if (request.UserId is { } userId && await queries.UserLinkedElsewhereAsync(userId, request.EmployeeId, ct))
            return Result.Failure<Guid>(Error.Conflict("Employee_UserAlreadyLinked"));

        if (!string.IsNullOrWhiteSpace(request.Cin)
            && await queries.CinUsedElsewhereAsync(request.Cin.Trim().ToUpperInvariant(), request.EmployeeId, ct))
            return Result.Failure<Guid>(Error.Conflict("Employee_CinAlreadyUsed", request.Cin.Trim()));

        if (request.EmployeeId is { } id)
        {
            var employee = await employees.GetByIdAsync(id, ct);
            if (employee is null) return Result.Failure<Guid>(Error.NotFound("Employee_NotFound"));

            employee.HireDate = request.HireDate;
            Apply(employee, request);

            var valid = employee.Validate();
            if (valid.IsFailure) return Result.Failure<Guid>(valid.FirstError);

            employees.Update(employee);
            unitOfWork.AuditReason = $"Modification du dossier du personnel {employee.Number}";
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success(employee.Id);
        }

        // The matricule is taken inside the transaction that writes the file, so a refused file
        // gives its number back rather than leaving a hole in the series.
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var number = await sequences.NextAsync(SequencePurpose.EmployeeNumber, null, ct: ct);
        if (number.IsFailure) return Result.Failure<Guid>(number.FirstError);

        var created = new Employee(number.Value, request.LastName, request.FirstName, request.Job,
                                   request.HireDate, currentUser.SiteId);
        Apply(created, request);

        var check = created.Validate();
        if (check.IsFailure) return Result.Failure<Guid>(check.FirstError);

        await employees.AddAsync(created, ct);
        unitOfWork.AuditReason = $"Création du dossier du personnel {created.Number} ({created.FullName})";
        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return Result.Success(created.Id);
    }

    private void Apply(Employee employee, EmployeeRequest request)
    {
        employee.LastName = request.LastName.Trim().ToUpperInvariant();
        employee.FirstName = request.FirstName.Trim();
        employee.FullNameArabic = Clean(request.FullNameArabic);
        employee.Gender = request.Gender;
        employee.BirthDate = request.BirthDate;
        employee.Cin = Clean(request.Cin)?.ToUpperInvariant();
        employee.CnssNumber = Clean(request.CnssNumber);
        employee.FamilyStatus = request.FamilyStatus;
        employee.DependentChildren = request.DependentChildren;
        employee.Address = Clean(request.Address);
        employee.City = Clean(request.City);
        employee.Phone = Clean(request.Phone);
        employee.Email = Clean(request.Email);
        employee.Job = request.Job;
        employee.JobTitle = Clean(request.JobTitle);
        employee.DepartmentId = request.DepartmentId;
        employee.UserId = request.UserId;
        employee.IsExposedToBiologicalRisk = request.IsExposedToBiologicalRisk;
        employee.ConfidentialityClauseSignedOn = request.ConfidentialityClauseSignedOn;
        employee.ContractType = request.ContractType;
        employee.ContractEndDate = request.ContractEndDate;
        employee.TrialPeriodEndDate = request.TrialPeriodEndDate;
        employee.WeeklyHours = request.WeeklyHours;

        if (CanSeeSalary)
        {
            employee.BaseSalary = request.BaseSalary;
            employee.Rib = Clean(request.Rib)?.Replace(" ", string.Empty);
            employee.BankName = Clean(request.BankName);
        }
    }

    public async Task<Result> RecordExitAsync(Guid employeeId, DateOnly on, ExitReason reason, string? comment,
                                              CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.Manage);
        if (permitted.IsFailure) return permitted;

        var employee = await employees.GetByIdWithDetailsAsync(employeeId, ct);
        if (employee is null) return Result.Failure(Error.NotFound("Employee_NotFound"));

        var done = employee.RecordExit(on, reason, comment);
        if (done.IsFailure) return done;

        employees.Update(employee);
        unitOfWork.AuditReason = $"Sortie de {employee.Number} ({employee.FullName}) le {on:dd/MM/yyyy} : {reason}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> RehireAsync(Guid employeeId, DateOnly on, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.Manage);
        if (permitted.IsFailure) return permitted;

        var employee = await employees.GetByIdAsync(employeeId, ct);
        if (employee is null) return Result.Failure(Error.NotFound("Employee_NotFound"));

        var done = employee.Rehire(on);
        if (done.IsFailure) return done;

        employees.Update(employee);
        unitOfWork.AuditReason = $"Réintégration de {employee.Number} ({employee.FullName}) le {on:dd/MM/yyyy}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ---- Documents -------------------------------------------------------------------------------

    public async Task<Result> AddCredentialAsync(Guid employeeId, CredentialRequest request,
                                                 CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.Manage);
        if (permitted.IsFailure) return permitted;

        var employee = await employees.GetByIdWithDetailsAsync(employeeId, ct);
        if (employee is null) return Result.Failure(Error.NotFound("Employee_NotFound"));

        if (request.Kind == CredentialKind.MedicalVisit && request.Fitness is null)
            return Result.Failure(Error.Validation("Credential_FitnessRequired"));

        var added = employee.AddCredential(new EmployeeCredential(request.Kind, request.Title)
        {
            Reference = Clean(request.Reference),
            IssuedBy = Clean(request.IssuedBy),
            ObtainedOn = request.ObtainedOn,
            ExpiresOn = request.ExpiresOn,
            Fitness = request.Kind == CredentialKind.MedicalVisit ? request.Fitness : null,
            Note = Clean(request.Note)
        });
        if (added.IsFailure) return added;

        employees.Update(employee);
        unitOfWork.AuditReason = $"Pièce ajoutée au dossier {employee.Number} : {request.Kind} {request.Title.Trim()}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> RemoveCredentialAsync(Guid employeeId, Guid credentialId, string reason,
                                                    CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.Manage);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Credential_RemoveReasonRequired"));

        var employee = await employees.GetByIdWithDetailsAsync(employeeId, ct);
        if (employee is null) return Result.Failure(Error.NotFound("Employee_NotFound"));

        var removed = employee.RemoveCredential(credentialId);
        if (removed.IsFailure) return removed;

        employees.Update(employee);
        unitOfWork.AuditReason = $"Pièce retirée du dossier {employee.Number} : {reason.Trim()}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ---- Habilitations ---------------------------------------------------------------------------

    public async Task<Result<HabilitationMatrix>> GetMatrixAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.View);
        if (permitted.IsFailure) return Result.Failure<HabilitationMatrix>(permitted.FirstError);

        return Result.Success(await queries.GetMatrixAsync(ct));
    }

    /// <summary>
    /// Records a habilitation decision.
    /// <para>
    /// Anyone holding the right may put a colleague "in training". Authorising them to work alone,
    /// or to train others, is the responsible biologist's signature in ISO 15189 terms, so it is
    /// refused to a user who is not a biologist. Biological validation itself can only ever be
    /// granted to a biologist: loi 12-01 reserves the release of results to them.
    /// </para>
    /// </summary>
    public async Task<Result> GrantAsync(Guid employeeId, HabilitationActivity activity, Guid? subjectId,
                                         HabilitationLevel level, DateOnly grantedOn, DateOnly? reviewDueOn,
                                         string? evidence, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.ManageHabilitations);
        if (permitted.IsFailure) return permitted;

        if (level != HabilitationLevel.InTraining && !IsBiologist(currentUser.Qualification))
            return Result.Failure(Error.Regulatory("Habilitation_BiologistSignatureRequired"));

        if (grantedOn > clock.Today)
            return Result.Failure(Error.Validation("Habilitation_GrantInFuture"));

        var employee = await employees.GetByIdWithDetailsAsync(employeeId, ct);
        if (employee is null) return Result.Failure(Error.NotFound("Employee_NotFound"));

        if (activity == HabilitationActivity.BiologicalValidation && employee.Job != EmployeeJob.Biologist)
            return Result.Failure(Error.Regulatory("Habilitation_BiologicalValidationBiologistOnly"));

        var granted = employee.Grant(activity, subjectId, level, grantedOn, reviewDueOn,
                                     currentUser.UserId, evidence);
        if (granted.IsFailure) return granted;

        employees.Update(employee);
        unitOfWork.AuditReason = $"Habilitation {activity} ({level}) accordée à {employee.Number} ({employee.FullName})";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> WithdrawAsync(Guid employeeId, Guid habilitationId, string reason,
                                            CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.ManageHabilitations);
        if (permitted.IsFailure) return permitted;

        var employee = await employees.GetByIdWithDetailsAsync(employeeId, ct);
        if (employee is null) return Result.Failure(Error.NotFound("Employee_NotFound"));

        var activity = employee.Habilitations.FirstOrDefault(h => h.Id == habilitationId)?.Activity;

        var withdrawn = employee.Withdraw(habilitationId, clock.Today, reason);
        if (withdrawn.IsFailure) return withdrawn;

        employees.Update(employee);
        unitOfWork.AuditReason = $"Habilitation {activity} retirée à {employee.Number} : {reason.Trim()}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ---- Deadlines -------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<DeadlineRow>>> GetDeadlinesAsync(int horizonDays,
                                                                            CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<DeadlineRow>>(permitted.FirstError);

        var facts = await queries.GetDeadlineFactsAsync(ct);
        return Result.Success(PersonnelDeadlines.Compute(facts, clock.Today, horizonDays));
    }

    private static bool IsBiologist(ProfessionalQualification qualification) =>
        qualification is ProfessionalQualification.MedicalBiologist or ProfessionalQualification.PharmacistBiologist;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Read side of the personnel files.</summary>
public interface IPersonnelQueries
{
    Task<IReadOnlyList<EmployeeRow>> GetEmployeesAsync(bool includeExited, CancellationToken ct = default);

    Task<PersonnelLookups> GetLookupsAsync(Guid? forEmployeeId, CancellationToken ct = default);

    Task<bool> UserLinkedElsewhereAsync(Guid userId, Guid? excludingEmployeeId, CancellationToken ct = default);

    Task<bool> CinUsedElsewhereAsync(string cin, Guid? excludingEmployeeId, CancellationToken ct = default);

    /// <summary>Active employees and their active habilitations.</summary>
    Task<HabilitationMatrix> GetMatrixAsync(CancellationToken ct = default);

    Task<IReadOnlyList<EmployeeDeadlineFacts>> GetDeadlineFactsAsync(CancellationToken ct = default);
}
