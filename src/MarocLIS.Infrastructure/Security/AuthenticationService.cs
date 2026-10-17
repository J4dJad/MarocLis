using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Persistence.Demo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Infrastructure.Security;

/// <summary>
/// Signs operators in and enforces the password policy.
/// <para>
/// Two behaviours are deliberate. First, a failed sign-in returns the same generic error whether
/// the account exists or not, so the login screen cannot be used to enumerate staff — but the
/// security log records exactly which it was, because an administrator investigating an incident
/// needs the distinction. Second, the lockout counter is persisted rather than kept in memory:
/// a laboratory runs several workstations against one database, and an in-memory counter would let
/// an attacker get five attempts per machine.
/// </para>
/// </summary>
public sealed class AuthenticationService(
    LisDbContext db,
    IPasswordHasher passwordHasher,
    PasswordPolicy policy,
    CurrentUserAccessor currentUser,
    ISecurityAuditor securityAuditor,
    IClock clock,
    ILogger<AuthenticationService>? logger = null) : IAuthenticationService
{
    public async Task<Result<AuthenticatedSession>> SignInAsync(string userName, string password,
                                                                Guid? siteId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
            return await FailAsync(userName, "Auth_InvalidCredentials", ct);

        var normalised = userName.Trim().ToLowerInvariant();

        // Accounts of employees whose exit date is past are switched off before anyone signs
        // in, the one being typed included: a departure recorded ahead takes effect on its day
        // without anybody having to remember it. Never in the way of signing in.
        try
        {
            await new DepartedAccountDeactivator(db, securityAuditor, clock).DeactivateDueAsync(ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "The accounts of departed employees could not be checked at sign-in.");
        }

        // The site filter must not apply while authenticating: nobody is signed in yet, so there
        // is no scope. IgnoreQueryFilters also lets a deactivated account be found and reported
        // properly instead of silently behaving like a wrong password.
        var user = await db.Users
            .IgnoreQueryFilters()
            .Include(u => u.Roles).ThenInclude(r => r.Role).ThenInclude(r => r!.Permissions)
            .Include(u => u.SiteAccess)
            .FirstOrDefaultAsync(u => u.UserName == normalised, ct);

        // The password is checked first, for every outcome, unknown names included. Answering
        // "locked out" or "inactive" before it, or answering an unknown name without spending the
        // time a hash costs, tells whoever is typing which names are real accounts.
        if (user is null)
        {
            passwordHasher.Verify(password, DummyHash);
            return await FailAsync(normalised, "Auth_InvalidCredentials", ct);
        }

        var passwordCorrect = passwordHasher.Verify(password, user.PasswordHash);

        // IsActive alone: an account is deactivated, never deleted. AppUser stopped being
        // ISoftDeletable because UserRole depends on it as a required navigation, and the two
        // checks were always the same decision anyway.
        if (!user.IsActive)
            return await FailAsync(normalised, "Auth_AccountInactive", ct);

        if (user.IsLockedOut(clock.Now))
        {
            // Only the account holder learns that the account is locked: with the right password
            // the reason is theirs to know; with a wrong one the answer is the same as for a name
            // that does not exist. The counter is not advanced while locked, so guessing cannot
            // extend the lockout of somebody else's account indefinitely.
            await securityAuditor.LogAsync(SecurityEventKind.LoginFailed, false, normalised,
                                           "Auth_AccountLockedOut",
                                           details: passwordCorrect ? "Correct password" : null, ct: ct);

            return Result.Failure<AuthenticatedSession>(
                LockedOutAnswer(passwordCorrect, user.LockedOutUntil!.Value));
        }

        // A demonstration account still on its published password, on a database that holds real
        // data: switched off here rather than at the next start, which may be days after the first
        // real patient. Said to the holder, since the password was right.
        if (passwordCorrect && DemoAccountGuard.IsPublishedCredential(normalised, password)
            && await DemoAccountGuard.FindRealDataAsync(db, ct) is { } realData)
        {
            user.Deactivate();
            await db.SaveChangesAsync(ct);

            await securityAuditor.LogAsync(SecurityEventKind.LoginFailed, false, normalised,
                                           "Auth_DemoAccountDisabled",
                                           details: $"Demonstration account deactivated: {realData}", ct: ct);

            return Result.Failure<AuthenticatedSession>(Error.Forbidden("Auth_DemoAccountDisabled"));
        }

        if (!passwordCorrect)
        {
            user.RegisterFailedLogin(clock.Now, policy.MaxFailedAttempts, policy.LockoutDuration);
            await db.SaveChangesAsync(ct);

            var lockedOutNow = user.IsLockedOut(clock.Now);

            await securityAuditor.LogAsync(
                lockedOutNow ? SecurityEventKind.AccountLockedOut : SecurityEventKind.LoginFailed,
                false, normalised, "Auth_InvalidCredentials",
                details: $"Attempt {user.FailedLoginAttempts} of {policy.MaxFailedAttempts}", ct: ct);

            // The same answer as an unknown name, even on the attempt that locks the account; the
            // holder learns of the lockout on their next attempt with the right password.
            return Result.Failure<AuthenticatedSession>(Error.Forbidden("Auth_InvalidCredentials"));
        }

        // Resolve the working site: the one asked for, the user's default, or their only one.
        var accessibleSiteIds = user.SiteAccess.Select(a => a.SiteId).ToList();

        if (accessibleSiteIds.Count == 0)
            return await FailAsync(normalised, "Auth_SiteNotAuthorized", ct, discloseReason: true);

        var resolvedSiteId = siteId
            ?? user.SiteAccess.FirstOrDefault(a => a.IsDefaultSite)?.SiteId
            ?? accessibleSiteIds[0];

        if (!accessibleSiteIds.Contains(resolvedSiteId))
            return await FailAsync(normalised, "Auth_SiteNotAuthorized", ct, discloseReason: true);

        var site = await db.Sites
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == resolvedSiteId && !s.IsDeleted, ct);

        // A partner laboratory is where samples are sent, not a place to work: an access row to one
        // (granted before the user administration refused it) opens nothing.
        if (site is null || !site.IsActive || site.Kind == LabSiteKind.ReferralPartner)
            return await FailAsync(normalised, "Auth_SiteNotAuthorized", ct, discloseReason: true);

        // Transparently upgrade a hash produced under an older work factor. The hash only: the
        // password itself did not change, so neither does its age — SetPassword would have reset
        // it here, before IsPasswordExpired below reads it, and an expired password would have
        // gone unnoticed for another full period.
        if (passwordHasher.NeedsRehash(user.PasswordHash))
        {
            user.ReplacePasswordHash(passwordHasher.Hash(password));
            logger?.LogInformation("Rehashed the password for {UserName} at the current work factor.",
                                   normalised);
        }

        user.RegisterSuccessfulLogin(clock.Now, currentUser.Workstation);
        await db.SaveChangesAsync(ct);

        var activeRoles = user.Roles
            .Select(r => r.Role)
            .Where(r => r is { IsActive: true })
            .ToList();

        var permissions = activeRoles
            .SelectMany(r => r!.Permissions.Select(p => p.Permission))
            .ToHashSet(StringComparer.Ordinal);

        var prescriberScoped = PrescriberScope.Applies(
            user.PrescriberId, user.Qualification, activeRoles.Select(r => r!.Code), permissions);

        var session = new AuthenticatedSession(
            user.Id,
            user.UserName,
            user.FullName,
            site.Id,
            site.Code,
            user.PreferredLanguage,
            user.Qualification,
            permissions,
            accessibleSiteIds,
            user.AuthorizedDepartmentCodes,
            user.MustChangePassword || IsPasswordExpired(user),
            user.PrescriberId,
            prescriberScoped,
            user.SecurityStamp);

        currentUser.Apply(session);

        await securityAuditor.LogAsync(SecurityEventKind.LoginSucceeded, true, normalised, ct: ct);

        return Result.Success(session);
    }

    public async Task SignOutAsync(SecurityEventKind reason = SecurityEventKind.Logout,
                                   string? details = null, CancellationToken ct = default)
    {
        if (currentUser.IsAuthenticated)
            await securityAuditor.LogAsync(reason, true, currentUser.UserName, details: details, ct: ct);

        currentUser.Clear();
    }

    public async Task<Result> ChangePasswordAsync(Guid userId, string currentPassword,
                                                  string newPassword, CancellationToken ct = default)
    {
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Result.Failure(Error.NotFound("Auth_InvalidCredentials"));

        if (!passwordHasher.Verify(currentPassword, user.PasswordHash))
        {
            await securityAuditor.LogAsync(SecurityEventKind.PasswordChanged, false, user.UserName,
                                           "Auth_InvalidCredentials", ct: ct);
            return Result.Failure(Error.Forbidden("Auth_InvalidCredentials"));
        }

        var strength = policy.Validate(newPassword);
        if (strength.IsFailure) return strength;

        var reuse = await CheckReuseAsync(user, newPassword, ct);
        if (reuse.IsFailure) return reuse;

        // The operator's own change rotates the stamp of the session they are in; it stays theirs.
        var ownSession = currentUser.IsAuthenticated && currentUser.UserId == user.Id;

        await ApplyNewPasswordAsync(user, newPassword, mustChangeAtNextLogon: false, ct, ownSession);

        await securityAuditor.LogAsync(SecurityEventKind.PasswordChanged, true, user.UserName,
                                       targetUserId: user.Id, ct: ct);
        return Result.Success();
    }

    public async Task<Result> ResetPasswordAsync(Guid userId, string newPassword,
                                                 CancellationToken ct = default)
    {
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Result.Failure(Error.NotFound("Auth_InvalidCredentials"));

        var strength = policy.Validate(newPassword);
        if (strength.IsFailure) return strength;

        // An administrative reset always forces a change at next sign-in, so the administrator
        // never holds a working credential for someone else's account.
        await ApplyNewPasswordAsync(user, newPassword, mustChangeAtNextLogon: true, ct);

        await securityAuditor.LogAsync(SecurityEventKind.PasswordReset, true,
                                       targetUserId: user.Id,
                                       details: $"Reset for {user.UserName}", ct: ct);
        return Result.Success();
    }

    public async Task<Result> SwitchSiteAsync(Guid siteId, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated)
            return Result.Failure(Error.Forbidden("Message.SessionExpired"));

        if (!currentUser.AccessibleSiteIds.Contains(siteId))
            return Result.Failure(Error.Forbidden("Auth_SiteNotAuthorized"));

        var site = await db.Sites.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == siteId && !s.IsDeleted && s.IsActive
                                      && s.Kind != LabSiteKind.ReferralPartner, ct);

        if (site is null) return Result.Failure(Error.NotFound("Auth_SiteNotAuthorized"));

        currentUser.SetSite(site.Id, site.Code);
        return Result.Success();
    }

    /// <summary>
    /// What a locked account answers: the lockout itself only to whoever typed the right password,
    /// the generic refusal to anyone else, so a locked account cannot be told from a missing one.
    /// </summary>
    public static Error LockedOutAnswer(bool passwordCorrect, DateTimeOffset lockedOutUntil) =>
        passwordCorrect
            ? Error.Forbidden("Auth_AccountLockedOut", lockedOutUntil)
            : Error.Forbidden("Auth_InvalidCredentials");

    /// <summary>
    /// A hash of a random value, verified against when the user name is unknown so that answer
    /// costs the same time as a wrong password for a real account. Computed once per process.
    /// </summary>
    private string DummyHash
    {
        get
        {
            if (_dummyHash is { } cached) return cached;
            var hash = passwordHasher.Hash(Guid.NewGuid().ToString("N"));
            Interlocked.CompareExchange(ref _dummyHash, hash, null);
            return _dummyHash!;
        }
    }

    private static string? _dummyHash;

    private bool IsPasswordExpired(AppUser user) =>
        policy.ExpiryDays > 0 &&
        (user.PasswordChangedAt is null ||
         user.PasswordChangedAt.Value.AddDays(policy.ExpiryDays) < clock.Now);

    private async Task<Result> CheckReuseAsync(AppUser user, string newPassword, CancellationToken ct)
    {
        if (policy.HistoryLength <= 0) return Result.Success();

        if (passwordHasher.Verify(newPassword, user.PasswordHash))
            return Result.Failure(Error.Validation("Auth_PasswordReused"));

        var history = await db.PasswordHistory
            .AsNoTracking()
            .Where(h => h.UserId == user.Id)
            .OrderByDescending(h => h.ChangedAt)
            .Take(policy.HistoryLength)
            .ToListAsync(ct);

        return history.Any(h => passwordHasher.Verify(newPassword, h.PasswordHash))
            ? Result.Failure(Error.Validation("Auth_PasswordReused"))
            : Result.Success();
    }

    /// <param name="ownSession">
    /// The change is the signed-in operator's own: the session takes the new stamp. It is announced
    /// to the session before the save and taken right after it, before anything else is awaited,
    /// so the shell's periodic session check — which runs on the same thread during these awaits —
    /// never sees the new stamp in the database against the old one in the session.
    /// </param>
    private async Task ApplyNewPasswordAsync(AppUser user, string newPassword,
                                             bool mustChangeAtNextLogon, CancellationToken ct,
                                             bool ownSession = false)
    {
        if (policy.HistoryLength > 0 && !string.IsNullOrEmpty(user.PasswordHash))
            db.PasswordHistory.Add(new PasswordHistoryEntry(user.Id, user.PasswordHash, clock.Now));

        user.SetPassword(passwordHasher.Hash(newPassword), clock.Now, mustChangeAtNextLogon);

        if (ownSession) currentUser.ExpectSecurityStamp(user.SecurityStamp);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            if (ownSession) currentUser.ForgetExpectedSecurityStamp();
            throw;
        }

        if (ownSession) currentUser.RefreshSecurityStamp(user.SecurityStamp);

        // Keep the history bounded; there is no value in retaining hashes beyond the policy window.
        var stale = await db.PasswordHistory
            .Where(h => h.UserId == user.Id)
            .OrderByDescending(h => h.ChangedAt)
            .Skip(policy.HistoryLength)
            .ToListAsync(ct);

        if (stale.Count > 0)
        {
            db.PasswordHistory.RemoveRange(stale);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Records a failed sign-in and produces the error the caller sees.
    /// <para>
    /// Before the password has been verified the caller is told only "invalid credentials",
    /// whatever the real reason, so the login screen cannot be used to discover which accounts
    /// exist; the real reason goes to the security log. Once the password <em>has</em> been
    /// verified the person at the keyboard is the account holder, and withholding the reason from
    /// them helps nobody — pass <paramref name="discloseReason"/> there.
    /// </para>
    /// </summary>
    private async Task<Result<AuthenticatedSession>> FailAsync(string userName, string reasonKey,
                                                               CancellationToken ct,
                                                               bool discloseReason = false)
    {
        await securityAuditor.LogAsync(SecurityEventKind.LoginFailed, false, userName, reasonKey, ct: ct);

        return Result.Failure<AuthenticatedSession>(
            Error.Forbidden(discloseReason ? reasonKey : "Auth_InvalidCredentials"));
    }
}
