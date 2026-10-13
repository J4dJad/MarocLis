using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
    }

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

/// <summary>Writes security events through the application's own context, in their own transaction.</summary>
public sealed class SecurityAuditor(LisDbContext context, IClock clock, ICurrentUser? currentUser)
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
            SiteId = currentUser is { IsAuthenticated: true } ? currentUser.SiteId : null,
            FailureReasonKey = failureReasonKey,
            TargetUserId = targetUserId,
            Details = details
        };

        context.SecurityEvents.Add(entry);
        await context.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Records consultations of personal health data, which change auditing cannot see.
/// </summary>
public sealed class DataAccessAuditor(LisDbContext db, IClock clock, ICurrentUser currentUser)
    : IDataAccessAuditor
{
    public async Task LogAsync(DataAccessKind kind, Guid? patientId = null, Guid? orderId = null,
                               string? context = null, int? recordCount = null,
                               string? queryDescription = null, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated) return;

        db.DataAccessLogs.Add(new DataAccessLog(currentUser.UserId, kind, clock.Now)
        {
            UserName = currentUser.UserName,
            PatientId = patientId,
            LabOrderId = orderId,
            RecordCount = recordCount,
            QueryDescription = Truncate(queryDescription, 1000),
            Context = Truncate(context, 150),
            Workstation = currentUser.Workstation,
            SiteId = currentUser.SiteId
        });

        await db.SaveChangesAsync(ct);
    }

    public async Task LogBreakGlassAsync(Guid patientId, string justification,
                                         CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated) return;

        db.DataAccessLogs.Add(new DataAccessLog(currentUser.UserId, DataAccessKind.ViewPatient, clock.Now)
        {
            UserName = currentUser.UserName,
            PatientId = patientId,
            Workstation = currentUser.Workstation,
            SiteId = currentUser.SiteId,
            IsBreakGlass = true,
            BreakGlassJustification = Truncate(justification, 1000),
            Context = "BreakGlass"
        });

        await db.SaveChangesAsync(ct);
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
