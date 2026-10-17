using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Infrastructure.Security;

/// <summary>
/// BCrypt password hashing. Chosen over a bare SHA family because it is deliberately slow and
/// salts each hash, which is what makes a stolen <c>Users</c> table useless. The work factor is
/// configurable so it can be raised as workstations get faster without invalidating old hashes.
/// </summary>
public sealed class BCryptPasswordHasher(int workFactor = 12) : IPasswordHasher
{
    private readonly int _workFactor = Math.Clamp(workFactor, 10, 16);

    public string Hash(string password) =>
        BCrypt.Net.BCrypt.EnhancedHashPassword(password, _workFactor);

    public bool Verify(string password, string hash)
    {
        try
        {
            return BCrypt.Net.BCrypt.EnhancedVerify(password, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            // A malformed stored hash must fail closed, not throw at the login screen.
            return false;
        }
    }

    public bool NeedsRehash(string hash)
    {
        // BCrypt hashes carry their cost as "$2a$NN$…"; re-hash transparently when it is below policy.
        var parts = hash.Split('$');
        return parts.Length < 4
               || !int.TryParse(parts[2], out var cost)
               || cost < _workFactor;
    }
}

/// <summary>
/// The signed-in operator, held for the lifetime of the desktop process.
/// <para>
/// A mutable singleton is the right shape here and not a compromise: a workstation has exactly one
/// operator at a time, the session must survive across every form, and the site can be switched
/// mid-shift. The <see cref="LisDbContext"/> site filter reads this, so a stale value would widen
/// data visibility — which is why the setters are internal to the authentication service.
/// </para>
/// </summary>
public sealed class CurrentUserAccessor : ICurrentUser
{
    private static readonly IReadOnlySet<string> NoPermissions =
        new HashSet<string>(StringComparer.Ordinal);

    public bool IsAuthenticated { get; private set; }

    public Guid UserId { get; private set; }
    public string UserName { get; private set; } = string.Empty;
    public string FullName { get; private set; } = string.Empty;

    public Guid SiteId { get; private set; }
    public string SiteCode { get; private set; } = string.Empty;

    public AppLanguage Language { get; set; } = AppLanguage.French;

    public ProfessionalQualification Qualification { get; private set; }

    public IReadOnlyList<Guid> AccessibleSiteIds { get; private set; } = [];
    public IReadOnlySet<string> Permissions { get; private set; } = NoPermissions;
    public IReadOnlySet<string> AuthorizedDepartmentCodes { get; private set; } = NoPermissions;

    public string Workstation { get; } = Environment.MachineName;

    public Guid? PrescriberId { get; private set; }
    public bool IsPrescriberScoped { get; private set; }

    /// <summary>The account's security stamp when the session was opened; see <see cref="SessionValidator"/>.</summary>
    internal string? SecurityStamp { get; private set; }

    public bool Has(string permission) => Permissions.Contains(permission);

    internal void Apply(AuthenticatedSession session)
    {
        IsAuthenticated = true;
        UserId = session.UserId;
        UserName = session.UserName;
        FullName = session.FullName;
        SiteId = session.SiteId;
        SiteCode = session.SiteCode;
        Language = session.Language;
        Qualification = session.Qualification;
        Permissions = session.Permissions;
        AccessibleSiteIds = session.AccessibleSiteIds;
        AuthorizedDepartmentCodes = session.AuthorizedDepartmentCodes;
        PrescriberId = session.PrescriberId;
        IsPrescriberScoped = session.IsPrescriberScoped;
        SecurityStamp = session.SecurityStamp;
        PendingSecurityStamp = null;
    }

    /// <summary>
    /// Takes the stamp the operator's own password change just produced, so changing one's own
    /// password does not read, a minute later, as a session revoked by somebody else.
    /// </summary>
    internal void RefreshSecurityStamp(string stamp)
    {
        SecurityStamp = stamp;
        PendingSecurityStamp = null;
    }

    /// <summary>
    /// The stamp the operator's own password change is saving, accepted alongside
    /// <see cref="SecurityStamp"/> while the save is in flight. The shell re-checks the session on
    /// a timer that keeps running during those awaits; between the moment the new stamp reaches
    /// the database and the moment the session takes it, the check would otherwise read the
    /// operator's own change as a revocation and sign them out.
    /// </summary>
    internal string? PendingSecurityStamp { get; private set; }

    internal void ExpectSecurityStamp(string stamp) => PendingSecurityStamp = stamp;

    internal void ForgetExpectedSecurityStamp() => PendingSecurityStamp = null;

    internal void SetSite(Guid siteId, string siteCode)
    {
        SiteId = siteId;
        SiteCode = siteCode;
    }

    internal void Clear()
    {
        IsAuthenticated = false;
        UserId = Guid.Empty;
        UserName = string.Empty;
        FullName = string.Empty;
        SiteId = Guid.Empty;
        SiteCode = string.Empty;
        Qualification = ProfessionalQualification.None;
        Permissions = NoPermissions;
        AccessibleSiteIds = [];
        AuthorizedDepartmentCodes = NoPermissions;
        PrescriberId = null;
        IsPrescriberScoped = false;
        SecurityStamp = null;
        PendingSecurityStamp = null;
    }
}

/// <summary>
/// Permission enforcement, plus the qualification check that loi n° 12-01 requires for releasing
/// results. Both must pass: a permission grant alone cannot make a technician a biologist, and a
/// qualification alone does not grant access the administrator has not given.
/// </summary>
public sealed class PermissionGuard(
    ICurrentUser currentUser,
    LisDbContext context,
    IClock clock,
    ISecurityAuditor securityAuditor,
    ILogger<PermissionGuard>? logger = null) : IPermissionGuard
{
    public Result Require(string permission)
    {
        if (!currentUser.IsAuthenticated)
            return Deny("Message.SessionExpired", permission);

        return currentUser.Has(permission)
            ? Result.Success()
            : Deny("Message.PermissionDenied", permission);
    }

    public Result RequireAny(params string[] permissions)
    {
        if (!currentUser.IsAuthenticated)
            return Deny("Message.SessionExpired", string.Join(',', permissions));

        return permissions.Any(currentUser.Has)
            ? Result.Success()
            : Deny("Message.PermissionDenied", string.Join(',', permissions));
    }

    public Result RequireAll(params string[] permissions)
    {
        if (!currentUser.IsAuthenticated)
            return Deny("Message.SessionExpired", string.Join(',', permissions));

        var missing = permissions.Where(p => !currentUser.Has(p)).ToArray();
        return missing.Length == 0
            ? Result.Success()
            : Deny("Message.PermissionDenied", string.Join(',', missing));
    }

    public async Task<Result> RequireBiologicalValidationAsync(string departmentCode,
                                                               CancellationToken ct = default)
    {
        var granted = Require(Permissions.Results.BiologicalValidate);
        if (granted.IsFailure) return granted;

        // Re-read the user rather than trusting the session: a qualification can lapse mid-shift,
        // and the expiry date is the kind of thing an inspection checks against the release log.
        var user = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == currentUser.UserId, ct);

        if (user is null) return Deny("Message.SessionExpired", departmentCode);

        var qualified = user.CanBiologicallyValidate(departmentCode, clock.Today);

        if (qualified.IsFailure)
        {
            await securityAuditor.LogAsync(SecurityEventKind.AuthorizationDenied, false,
                failureReasonKey: qualified.FirstError.ResourceKey,
                details: $"Biological validation refused for department {departmentCode}", ct: ct);
        }

        return qualified;
    }

    private Result Deny(string resourceKey, string detail)
    {
        logger?.LogWarning("Authorization denied for {User} on {Detail}.", currentUser.UserName, detail);

        // Fire-and-forget would lose the record on a crash; the auditor writes through the same
        // context and is awaited by callers that can, so here it is scheduled and not awaited to
        // keep Require() synchronous for UI binding. Failures are logged, never swallowed silently.
        _ = securityAuditor
            .LogAsync(SecurityEventKind.AuthorizationDenied, false,
                      failureReasonKey: resourceKey, details: detail)
            .ContinueWith(t =>
            {
                if (t.Exception is not null)
                    logger?.LogError(t.Exception, "Failed to record an authorization denial.");
            }, TaskScheduler.Default);

        return Result.Failure(Error.Forbidden(resourceKey, detail));
    }
}

/// <summary>
/// Re-reads the signed-in account and compares it with the session opened for it.
/// <para>
/// Through a short-lived context of its own, for the same reason as <see cref="SecurityAuditor"/>:
/// the shell calls it on a timer, and on a screen's context it would collide with whatever that
/// screen is loading. Ignores the site filter on purpose — the question is about the account, not
/// about the site the operator is working on.
/// </para>
/// </summary>
public sealed class SessionValidator(IServiceScopeFactory scopes, CurrentUserAccessor currentUser)
    : ISessionValidator
{
    public async Task<bool> IsStillValidAsync(CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated) return false;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

        var account = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == currentUser.UserId)
            .Select(u => new
            {
                u.IsActive,
                u.SecurityStamp,
                u.PrescriberId,
                u.Qualification,
                Permissions = u.Roles
                    .Where(r => r.Role!.IsActive)
                    .SelectMany(r => r.Role!.Permissions.Select(p => p.Permission))
                    .ToList(),
                Sites = u.SiteAccess.Select(a => a.SiteId).ToList()
            })
            .FirstOrDefaultAsync(ct);

        // The exit date of the employee behind the account passed while the workstation stays
        // signed in: the account is deactivated now and the session ends with it.
        if (account is { IsActive: true })
        {
            var today = (scope.ServiceProvider.GetService<IClock>() ?? new SystemClock()).Today;
            var departed = await db.Employees.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(e => e.UserId == currentUser.UserId && e.ExitDate != null && e.ExitDate < today, ct);

            if (departed
                && (await scope.ServiceProvider.GetRequiredService<Application.HumanResources.IDepartedAccounts>()
                        .DeactivateDueAsync(currentUser.UserId, ct)).Count > 0)
                return false;
        }

        return account is not null && Matches(currentUser, currentUser.SecurityStamp,
                                              account.IsActive, account.SecurityStamp,
                                              account.PrescriberId, account.Permissions, account.Sites,
                                              account.Qualification, currentUser.PendingSecurityStamp);
    }

    /// <summary>
    /// The comparison itself, separate so it can be tested without a database.
    /// <paramref name="qualification"/> is the account's, compared when given;
    /// <paramref name="pendingStamp"/> is a stamp the session's own password change is saving.
    /// </summary>
    public static bool Matches(ICurrentUser session, string? sessionStamp, bool isActive, string securityStamp,
                                 Guid? prescriberId, IEnumerable<string> permissions,
                                 IEnumerable<Guid> sites, ProfessionalQualification? qualification = null,
                                 string? pendingStamp = null)
    {
        if (!isActive) return false;

        // A session opened before stamps were recorded has nothing to compare against; the other
        // checks still apply to it.
        if (sessionStamp is not null
            && !string.Equals(sessionStamp, securityStamp, StringComparison.Ordinal)
            && !string.Equals(pendingStamp, securityStamp, StringComparison.Ordinal))
            return false;

        if (session.PrescriberId != prescriberId) return false;

        // The qualification is read from the session: it decides at sign-in whether the account is
        // confined to its prescriber's patients, and who may grant a habilitation. Changing it does
        // not move the stamp, so it is compared like the permissions.
        if (qualification is { } current && session.Qualification != current) return false;

        // Compared as sets: a role edited or switched off changes what the account may do without
        // touching the account itself, so its stamp does not move.
        if (!session.Permissions.SetEquals(permissions)) return false;

        return session.AccessibleSiteIds.ToHashSet().SetEquals(sites);
    }
}

/// <summary>
/// Writes security events through a short-lived context of their own, in their own transaction.
/// <para>
/// Not the screen's context: an authorization denial is logged without being awaited, so it would
/// otherwise run alongside whatever the screen does next on that same context, which EF refuses,
/// and its save would also commit any edit the screen was still holding.
/// </para>
/// </summary>
public sealed class SecurityAuditor(IServiceScopeFactory scopes, IClock clock, ICurrentUser? currentUser)
    : ISecurityAuditor
{
    public async Task LogAsync(SecurityEventKind kind, bool succeeded, string? attemptedUserName = null,
                               string? failureReasonKey = null, Guid? targetUserId = null,
                               string? details = null, CancellationToken ct = default)
    {
        var entry = new SecurityEvent(kind, clock.Now, succeeded)
        {
            UserId = currentUser is { IsAuthenticated: true } ? currentUser.UserId : null,
            AttemptedUserName = attemptedUserName
                                ?? (currentUser is { IsAuthenticated: true } ? currentUser.UserName : null),
            Workstation = currentUser?.Workstation ?? Environment.MachineName,
            OsUser = Environment.UserName,
            SiteId = currentUser is { IsAuthenticated: true } && currentUser.SiteId != Guid.Empty
                ? currentUser.SiteId
                : null,
            FailureReasonKey = failureReasonKey,
            TargetUserId = targetUserId,
            // The column holds 2000 characters. The details quote what the operator typed (a
            // reason for closing a numbering series); written after the change it records, a
            // longer one failed with an unexpected error once the change was already saved.
            Details = details is { Length: > 2000 } ? string.Concat(details.AsSpan(0, 1999), "…") : details
        };

        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LisDbContext>();

        context.SecurityEvents.Add(entry);
        await context.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Records consultations of personal health data, which change auditing cannot see.
/// </summary>
/// <remarks>
/// Each entry is written through its own short-lived context, never the screen's. A screen often
/// loads twice in quick succession (a filter set during its first paint fires a second load), and
/// both loads log their read: on a shared context the second log arrives while the first is still
/// saving and EF refuses it. A separate context also keeps the log from sweeping up, or being swept
/// up by, whatever unsaved edits the screen happens to be holding.
/// </remarks>
public sealed class DataAccessAuditor(IServiceScopeFactory scopes, IClock clock,
                                     ICurrentUser currentUser) : IDataAccessAuditor
{
    private async Task SaveAsync(DataAccessLog entry, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

        db.DataAccessLogs.Add(entry);
        await db.SaveChangesAsync(ct);
    }

    public async Task LogAsync(DataAccessKind kind, Guid? patientId = null, Guid? orderId = null,
                               string? context = null, int? recordCount = null,
                               string? queryDescription = null, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated) return;

        await SaveAsync(new DataAccessLog(currentUser.UserId, kind, clock.Now)
        {
            UserName = currentUser.UserName,
            PatientId = patientId,
            LabOrderId = orderId,
            RecordCount = recordCount,
            QueryDescription = Truncate(queryDescription, 1000),
            Context = Truncate(context, 150),
            Workstation = currentUser.Workstation,
            SiteId = currentUser.SiteId
        }, ct);
    }

    public async Task LogBreakGlassAsync(Guid patientId, string justification,
                                         CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated) return;

        await SaveAsync(new DataAccessLog(currentUser.UserId, DataAccessKind.ViewPatient, clock.Now)
        {
            UserName = currentUser.UserName,
            PatientId = patientId,
            Workstation = currentUser.Workstation,
            SiteId = currentUser.SiteId,
            IsBreakGlass = true,
            BreakGlassJustification = Truncate(justification, 1000),
            Context = "BreakGlass"
        }, ct);
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
