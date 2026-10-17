using MarocLIS.Application.Abstractions;
using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Security;

/// <summary>
/// Deactivates the account of an employee whose exit date is past, from the day after it (<see cref="IDepartedAccounts"/>).
/// <para>
/// Runs on the context it is given: the personnel screen's, right after the departure was saved, or
/// the sign-in's. The last active account able to manage the users is never deactivated this way:
/// an installation without an administrator cannot be repaired from inside the application, so it
/// stays active, the security log says why, and the screen asks for another administrator first.
/// </para>
/// </summary>
public sealed class DepartedAccountDeactivator(LisDbContext db, ISecurityAuditor securityAuditor, IClock clock)
    : IDepartedAccounts
{
    public async Task<DepartedAccountOutcome?> ApplyExitAsync(Guid employeeId, CancellationToken ct = default)
    {
        var employee = await db.Employees.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Id == employeeId)
            .Select(e => new { e.UserId, e.ExitDate })
            .FirstOrDefaultAsync(ct);
        if (employee?.UserId is not { } userId) return null;

        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return null;

        if (!user.IsActive) return new DepartedAccountOutcome(user.UserName, DepartedAccountState.Inactive, employee.ExitDate);
        if (employee.ExitDate is not { } exit) return null;
        if (exit >= clock.Today) return new DepartedAccountOutcome(user.UserName, DepartedAccountState.Scheduled, exit);

        if ((await DeactivateDueAsync(userId, ct)).Count > 0)
            return new DepartedAccountOutcome(user.UserName, DepartedAccountState.Deactivated, exit);

        await securityAuditor.LogAsync(SecurityEventKind.UserDeactivated, succeeded: false,
                                       failureReasonKey: "User_WouldRemoveLastAdministrator",
                                       targetUserId: user.Id,
                                       details: $"Compte {user.UserName} laissé actif malgré la sortie du " +
                                                $"{exit:dd/MM/yyyy} : dernier administrateur",
                                       ct: ct);
        return new DepartedAccountOutcome(user.UserName, DepartedAccountState.KeptLastAdministrator, exit);
    }

    public async Task<IReadOnlyList<string>> DeactivateDueAsync(Guid? userId = null, CancellationToken ct = default)
    {
        var today = clock.Today;

        var due = await (from e in db.Employees.IgnoreQueryFilters()
                         join u in db.Users.IgnoreQueryFilters() on e.UserId equals (Guid?)u.Id
                         where u.IsActive && e.ExitDate != null && e.ExitDate < today
                               && (userId == null || u.Id == userId)
                         select new { UserId = u.Id, e.Number, e.LastName, e.FirstName, e.ExitDate })
            .AsNoTracking()
            .ToListAsync(ct);

        var deactivated = new List<string>();

        foreach (var row in due)
        {
            var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == row.UserId, ct);
            if (!user.IsActive) continue;

            var departure = $"sortie de {row.Number} ({$"{row.LastName} {row.FirstName}".Trim()}) " +
                            $"le {row.ExitDate:dd/MM/yyyy}";

            // Left active and said once, by ApplyExitAsync when the departure is recorded: the
            // sign-in and the session check run this again and again.
            if (await IsLastAdministratorAsync(user.Id, ct)) continue;

            user.Deactivate();

            var reason = $"Désactivation du compte {user.UserName} : {departure}";
            AuditSaveChangesInterceptor.PendingReason = reason;
            try
            {
                await db.RunExclusiveAsync(() => db.SaveChangesAsync(ct), ct);
            }
            finally
            {
                AuditSaveChangesInterceptor.PendingReason = null;
            }

            await securityAuditor.LogAsync(SecurityEventKind.UserDeactivated, succeeded: true,
                                           targetUserId: user.Id, details: reason, ct: ct);
            deactivated.Add(user.UserName);
        }

        return deactivated;
    }

    /// <summary>Whether the account manages the users and no other active account does.</summary>
    private async Task<bool> IsLastAdministratorAsync(Guid userId, CancellationToken ct)
    {
        const string manage = Permissions.Administration.ManageUsers;

        var administrators = db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.IsActive
                        && u.Roles.Any(link => db.Roles.Any(r => r.Id == link.RoleId && r.IsActive
                                                                 && r.Permissions.Any(p => p.Permission == manage))));

        return await administrators.AnyAsync(u => u.Id == userId, ct)
               && !await administrators.AnyAsync(u => u.Id != userId, ct);
    }
}
