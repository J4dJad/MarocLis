using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Abstractions;

/// <summary>
/// The operator on whose behalf the current work is being done. Injected everywhere rather than
/// passed as a parameter, because every audit entry and every clinical record needs it and an
/// optional parameter is a parameter somebody forgets.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid UserId { get; }
    string UserName { get; }
    string FullName { get; }

    /// <summary>Site the operator is working on now; scopes every query and every new record.</summary>
    Guid SiteId { get; }
    string SiteCode { get; }

    AppLanguage Language { get; }

    ProfessionalQualification Qualification { get; }

    /// <summary>Sites the operator may switch to.</summary>
    IReadOnlyList<Guid> AccessibleSiteIds { get; }

    IReadOnlySet<string> Permissions { get; }

    /// <summary>Departments the operator may biologically validate for; empty means all.</summary>
    IReadOnlySet<string> AuthorizedDepartmentCodes { get; }

    bool Has(string permission);

    /// <summary>Machine name, recorded on audit entries and sample events.</summary>
    string Workstation { get; }

    /// <summary>The prescriber the account is linked to, if any.</summary>
    Guid? PrescriberId { get; }

    /// <summary>
    /// The operator is an outside prescriber and sees only the orders naming
    /// <see cref="PrescriberId"/>, and those orders' patients — nothing at all when unlinked.
    /// Decided at sign-in by <see cref="Domain.Identity.PrescriberScope"/> and enforced by the
    /// data layer's query filters.
    /// </summary>
    bool IsPrescriberScoped { get; }
}

/// <summary>
/// Checks permissions and raises the audit record when one is refused.
/// <para>
/// Enforcement happens in the application layer rather than in the UI. Hiding a button is a
/// usability measure; refusing the operation is the control an auditor can test.
/// </para>
/// </summary>
public interface IPermissionGuard
{
    /// <summary>Returns a failure result rather than throwing, so a use case can report it cleanly.</summary>
    Result Require(string permission);

    Result RequireAny(params string[] permissions);

    Result RequireAll(params string[] permissions);

    /// <summary>
    /// Checks that the current user may perform the legally binding biological validation for a
    /// department. Combines the permission grant with the qualification check that loi n° 12-01
    /// imposes, so neither can be satisfied on its own.
    /// </summary>
    Task<Result> RequireBiologicalValidationAsync(string departmentCode, CancellationToken ct = default);
}

/// <summary>Signs a user in and out, and enforces the password policy.</summary>
public interface IAuthenticationService
{
    Task<Result<AuthenticatedSession>> SignInAsync(string userName, string password, Guid? siteId,
                                                   CancellationToken ct = default);

    /// <summary>
    /// Ends the session and records why: <see cref="SecurityEventKind.Logout"/> for a sign-out the
    /// operator asked for, <see cref="SecurityEventKind.SessionTimeout"/> for the idle lock.
    /// </summary>
    Task SignOutAsync(SecurityEventKind reason = SecurityEventKind.Logout, string? details = null,
                      CancellationToken ct = default);

    Task<Result> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword,
                                     CancellationToken ct = default);

    /// <summary>Administrative reset; the new password must be changed at next sign-in.</summary>
    Task<Result> ResetPasswordAsync(Guid userId, string newPassword, CancellationToken ct = default);

    /// <summary>Switches the working site without a full re-authentication.</summary>
    Task<Result> SwitchSiteAsync(Guid siteId, CancellationToken ct = default);
}

public sealed record AuthenticatedSession(
    Guid UserId,
    string UserName,
    string FullName,
    Guid SiteId,
    string SiteCode,
    AppLanguage Language,
    ProfessionalQualification Qualification,
    IReadOnlySet<string> Permissions,
    IReadOnlyList<Guid> AccessibleSiteIds,
    IReadOnlySet<string> AuthorizedDepartmentCodes,
    bool MustChangePassword,
    Guid? PrescriberId = null,
    bool IsPrescriberScoped = false,
    // The account's security stamp at sign-in; a session whose stamp has moved is ended.
    string? SecurityStamp = null);

/// <summary>
/// Re-checks an open session against the account as it is now.
/// <para>
/// A session is built once, at sign-in. Without this, an operator dismissed and deactivated at ten
/// o'clock, or whose role was withdrawn, kept every right they had until they chose to sign out.
/// The desktop shell calls it periodically and ends the session when it answers false.
/// </para>
/// </summary>
public interface ISessionValidator
{
    /// <summary>
    /// False when the account was deactivated, its security stamp rotated (password reset, role
    /// change, prescriber link) or its effective permissions or sites changed since sign-in.
    /// </summary>
    Task<bool> IsStillValidAsync(CancellationToken ct = default);
}

/// <summary>
/// The password policy. Values come from configuration so a laboratory can align it with its own
/// information-security procedure, which ISO 15189 §5.10 expects to exist and be documented.
/// </summary>
public sealed class PasswordPolicy
{
    public int MinimumLength { get; set; } = 10;
    public bool RequireUppercase { get; set; } = true;
    public bool RequireLowercase { get; set; } = true;
    public bool RequireDigit { get; set; } = true;
    public bool RequireNonAlphanumeric { get; set; } = true;

    /// <summary>Zero disables expiry.</summary>
    public int ExpiryDays { get; set; } = 180;

    /// <summary>Number of previous passwords that may not be reused. Zero disables the check.</summary>
    public int HistoryLength { get; set; } = 5;

    public int MaxFailedAttempts { get; set; } = 5;
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Inactivity after which the workstation locks — a shared-counter reality.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(15);

    public Result Validate(string password)
    {
        var errors = new List<Error>();

        if (string.IsNullOrEmpty(password) || password.Length < MinimumLength ||
            (RequireUppercase && !password.Any(char.IsUpper)) ||
            (RequireLowercase && !password.Any(char.IsLower)) ||
            (RequireDigit && !password.Any(char.IsDigit)) ||
            (RequireNonAlphanumeric && password.All(char.IsLetterOrDigit)))
            errors.Add(Error.Validation("Auth_PasswordTooWeak", MinimumLength));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }
}

/// <summary>
/// Records consultations of personal health data. Separate from change auditing because reading a
/// file leaves no trace on the file, and loi n° 08-09 makes the laboratory accountable for who
/// consulted what.
/// </summary>
public interface IDataAccessAuditor
{
    Task LogAsync(DataAccessKind kind, Guid? patientId = null, Guid? orderId = null,
                  string? context = null, int? recordCount = null, string? queryDescription = null,
                  CancellationToken ct = default);

    /// <summary>Records an access outside any care relationship, with its justification.</summary>
    Task LogBreakGlassAsync(Guid patientId, string justification, CancellationToken ct = default);
}

/// <summary>Records security events: sign-ins, lockouts, privilege changes.</summary>
public interface ISecurityAuditor
{
    Task LogAsync(SecurityEventKind kind, bool succeeded, string? attemptedUserName = null,
                  string? failureReasonKey = null, Guid? targetUserId = null,
                  string? details = null, CancellationToken ct = default);
}
