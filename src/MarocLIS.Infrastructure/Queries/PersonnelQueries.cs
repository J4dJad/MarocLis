using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class PersonnelQueries(LisDbContext db, IClock clock) : IPersonnelQueries
{
    // Gone from the day after the exit date; a habilitation ends on its withdrawal date, which on
    // a departure recorded ahead is the exit date.

    public async Task<IReadOnlyList<EmployeeRow>> GetEmployeesAsync(bool includeExited, CancellationToken ct = default)
    {
        var today = clock.Today;
        var query = db.Employees.AsNoTracking();
        if (!includeExited) query = query.Where(e => e.ExitDate == null || e.ExitDate >= today);

        var rows = await (
                from e in query
                join d in db.Departments.AsNoTracking() on e.DepartmentId equals d.Id into dj
                from d in dj.DefaultIfEmpty()
                join u in db.Users.AsNoTracking() on e.UserId equals u.Id into uj
                from u in uj.DefaultIfEmpty()
                select new
                {
                    e.Id, e.Number, e.LastName, e.FirstName, e.Job, e.JobTitle,
                    Department = d == null ? null : d.Name,
                    e.ContractType, e.HireDate, e.ContractEndDate, e.Phone,
                    UserName = u == null ? null : u.UserName,
                    e.ExitDate, e.ExitReason
                })
            .ToListAsync(ct);

        return
        [
            .. rows.OrderBy(r => r.ExitDate != null)
                   .ThenBy(r => r.LastName, StringComparer.CurrentCulture)
                   .ThenBy(r => r.FirstName, StringComparer.CurrentCulture)
                   .Select(r => new EmployeeRow(r.Id, r.Number, $"{r.LastName} {r.FirstName}".Trim(), r.Job,
                                                r.JobTitle, r.Department?.Fr, r.ContractType, r.HireDate,
                                                r.ContractEndDate, r.Phone, r.UserName, r.ExitDate, r.ExitReason))
        ];
    }

    public async Task<PersonnelLookups> GetLookupsAsync(Guid? forEmployeeId, CancellationToken ct = default)
    {
        var departments = await db.Departments.AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.DisplayOrder)
            .Select(d => new { d.Id, d.Code, d.Name })
            .ToListAsync(ct);

        var instruments = await db.Instruments.AsNoTracking()
            .Where(i => i.DecommissioningDate == null)
            .OrderBy(i => i.Code)
            .Select(i => new { i.Id, i.Code, i.Name })
            .ToListAsync(ct);

        // Accounts not yet tied to another personnel file, plus the one this file already holds.
        var taken = db.Employees.AsNoTracking()
            .Where(e => e.UserId != null && e.Id != forEmployeeId)
            .Select(e => e.UserId!.Value);

        var users = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && !taken.Contains(u.Id))
            .OrderBy(u => u.FullName)
            .Select(u => new LinkableUser(u.Id, u.UserName, u.FullName))
            .ToListAsync(ct);

        return new PersonnelLookups(
            [.. departments.Select(d => new HabilitationSubject(d.Id, $"{d.Code} — {d.Name.Fr}", false))],
            [.. instruments.Select(i => new HabilitationSubject(i.Id, $"{i.Code} — {i.Name.Fr}", true))],
            users);
    }

    public Task<bool> UserLinkedElsewhereAsync(Guid userId, Guid? excludingEmployeeId, CancellationToken ct = default) =>
        db.Employees.IgnoreQueryFilters().AsNoTracking()
          .AnyAsync(e => e.UserId == userId && e.Id != excludingEmployeeId, ct);

    public Task<bool> CinUsedElsewhereAsync(string cin, Guid? excludingEmployeeId, CancellationToken ct = default) =>
        db.Employees.IgnoreQueryFilters().AsNoTracking()
          .AnyAsync(e => e.Cin == cin && e.Id != excludingEmployeeId, ct);

    public async Task<HabilitationMatrix> GetMatrixAsync(CancellationToken ct = default)
    {
        var today = clock.Today;
        var employees = await GetEmployeesAsync(includeExited: false, ct);
        var ids = employees.Select(e => e.EmployeeId).ToList();

        var habilitations = await (
                from h in db.EmployeeHabilitations.AsNoTracking()
                join u in db.Users.AsNoTracking() on h.GrantedByUserId equals u.Id into uj
                from u in uj.DefaultIfEmpty()
                where (h.WithdrawnOn == null || h.WithdrawnOn > today) && ids.Contains(h.EmployeeId)
                select new
                {
                    h.Id, h.EmployeeId, h.Activity, h.SubjectId, h.Level, h.GrantedOn,
                    GrantedBy = u == null ? null : u.FullName,
                    h.ReviewDueOn, h.Evidence
                })
            .ToListAsync(ct);

        var labels = await SubjectLabelsAsync(habilitations.Where(h => h.SubjectId != null)
                                                           .Select(h => h.SubjectId!.Value), ct);

        return new HabilitationMatrix(
            employees,
            [
                .. habilitations.Select(h => new HabilitationRow(
                    h.Id, h.EmployeeId, h.Activity, h.SubjectId,
                    h.SubjectId is { } s ? labels.GetValueOrDefault(s) : null,
                    h.Level, h.GrantedOn, h.GrantedBy ?? string.Empty, h.ReviewDueOn, h.Evidence))
            ]);
    }

    public async Task<IReadOnlyList<EmployeeDeadlineFacts>> GetDeadlineFactsAsync(CancellationToken ct = default)
    {
        var today = clock.Today;
        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.ExitDate == null || e.ExitDate >= today)
            .Select(e => new
            {
                e.Id, e.Number, e.LastName, e.FirstName, e.Job, e.IsExposedToBiologicalRisk,
                e.ConfidentialityClauseSignedOn, e.ContractType, e.ContractEndDate, e.TrialPeriodEndDate
            })
            .ToListAsync(ct);

        var ids = employees.Select(e => e.Id).ToList();

        var credentials = await db.EmployeeCredentials.AsNoTracking()
            .Where(c => ids.Contains(c.EmployeeId))
            .Select(c => new { c.EmployeeId, c.Kind, c.Title, c.ExpiresOn, c.Fitness, c.ObtainedOn })
            .ToListAsync(ct);

        var habilitations = await db.EmployeeHabilitations.AsNoTracking()
            .Where(h => (h.WithdrawnOn == null || h.WithdrawnOn > today) && ids.Contains(h.EmployeeId))
            .Select(h => new { h.EmployeeId, h.Activity, h.SubjectId, h.ReviewDueOn })
            .ToListAsync(ct);

        var labels = await SubjectLabelsAsync(habilitations.Where(h => h.SubjectId != null)
                                                           .Select(h => h.SubjectId!.Value), ct);

        return
        [
            .. employees.Select(e => new EmployeeDeadlineFacts(
                e.Id, e.Number, $"{e.LastName} {e.FirstName}".Trim(), e.Job, e.IsExposedToBiologicalRisk,
                e.ConfidentialityClauseSignedOn, e.ContractType, e.ContractEndDate, e.TrialPeriodEndDate,
                [.. credentials.Where(c => c.EmployeeId == e.Id)
                               .Select(c => (c.Kind, c.Title, c.ExpiresOn, c.Fitness, c.ObtainedOn))],
                [.. habilitations.Where(h => h.EmployeeId == e.Id)
                                 .Select(h => (h.Activity,
                                               h.SubjectId is { } s ? labels.GetValueOrDefault(s) : null,
                                               h.ReviewDueOn))]))
        ];
    }

    public async Task<IReadOnlyList<AccidentDeadlineFacts>> GetAccidentDeadlineFactsAsync(CancellationToken ct = default)
    {
        var today = clock.Today;
        var open = await (from a in db.WorkAccidents.AsNoTracking()
                          join e in db.Employees.AsNoTracking() on a.EmployeeId equals e.Id
                          where !a.IsClosed && (e.ExitDate == null || e.ExitDate >= today)
                          select new
                          {
                              e.Id, e.Number, e.LastName, e.FirstName, e.Job, AccidentId = a.Id,
                              AccidentNumber = a.Number, a.OccurredAt, Declared = a.DeclaredToInsurerOn != null
                          })
            .ToListAsync(ct);
        if (open.Count == 0) return [];

        var ids = open.Select(a => a.AccidentId).ToList();
        var checks = await db.WorkAccidents.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .SelectMany(a => a.Checks.Where(c => c.DoneOn == null)
                                     .Select(c => new { AccidentId = a.Id, c.Label, c.DueOn }))
            .ToListAsync(ct);

        return
        [
            .. open.Select(a => new AccidentDeadlineFacts(
                a.Id, a.Number, $"{a.LastName} {a.FirstName}".Trim(), a.Job, a.AccidentNumber, a.OccurredAt, a.Declared,
                [.. checks.Where(c => c.AccidentId == a.AccidentId).OrderBy(c => c.DueOn).Select(c => (c.Label, c.DueOn))]))
        ];
    }

    private async Task<Dictionary<Guid, string>> SubjectLabelsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return [];

        var departments = await db.Departments.AsNoTracking()
            .Where(d => wanted.Contains(d.Id))
            .Select(d => new { d.Id, d.Code, d.Name })
            .ToListAsync(ct);

        var instruments = await db.Instruments.IgnoreQueryFilters().AsNoTracking()
            .Where(i => wanted.Contains(i.Id))
            .Select(i => new { i.Id, i.Code, i.Name })
            .ToListAsync(ct);

        var labels = departments.ToDictionary(d => d.Id, d => $"{d.Code} — {d.Name.Fr}");
        foreach (var i in instruments) labels[i.Id] = $"{i.Code} — {i.Name.Fr}";
        return labels;
    }
}
