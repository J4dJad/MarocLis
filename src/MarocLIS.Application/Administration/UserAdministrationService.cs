using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Administration;

/// <summary>A personnel record with everything the administration screen needs to decide about it.</summary>
public sealed record UserRow(
    Guid UserId,
    string UserName,
    string FullName,
    string? FullNameArabic,
    string? Email,
    string? Phone,
    ProfessionalQualification Qualification,
    string? ProfessionalOrderNumber,
    string? Inpe,
    DateOnly? QualificationValidUntil,
    bool QualificationExpired,
    bool IsActive,
    bool IsLockedOut,
    DateTimeOffset? LockedOutUntil,
    int FailedLoginAttempts,
    bool MustChangePassword,
    DateTimeOffset? LastLoginAt,
    string? LastLoginWorkstation,
    DateTimeOffset? PasswordChangedAt,
    int? PasswordAgeDays,
    bool PasswordExpired,
    AppLanguage PreferredLanguage,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<string> RoleCodes,
    IReadOnlyList<string> AuthorizedDepartmentCodes,
    IReadOnlyList<Guid> SiteIds,
    Guid? DefaultSiteId,
    bool HasSignature)
{
    /// <summary>
    /// Whether the record satisfies loi n° 12-01 for biological validation. Shown in the list so an
    /// administrator can see that a biologist who cannot release results is mis-recorded, rather
    /// than finding out when the biologist is standing at the bench with a report to sign.
    /// </summary>
    public bool QualifiedToValidate =>
        Qualification is ProfessionalQualification.MedicalBiologist
                      or ProfessionalQualification.PharmacistBiologist
        && !string.IsNullOrWhiteSpace(ProfessionalOrderNumber)
        && !QualificationExpired;
}

public sealed record RoleRow(
    Guid RoleId,
    string Code,
    MultilingualText Name,
    MultilingualText? Description,
    bool IsBuiltIn,
    bool IsActive,
    int ActiveUserCount,
    IReadOnlyList<string> Permissions);

public sealed record SiteRow(Guid SiteId, string Code, MultilingualText Name, bool IsActive);

/// <summary>
/// One row of the security log, shared by the user-administration screen's recent-events panel and
/// by the audit-trail viewer.
/// <para>
/// One record for one table. The viewer needed the row's identity and the OS account, and defining
/// a second near-identical <c>SecurityEventRow</c> beside this one would have left two projections
/// of the same rows free to disagree about what a security event is.
/// </para>
/// </summary>
public sealed record SecurityEventRow(
    Guid SecurityEventId,
    DateTimeOffset OccurredAt,
    SecurityEventKind Kind,
    bool Succeeded,
    string? UserName,
    string? Workstation,
    /// <summary>Windows account the process ran under, which may differ from the LIS user.</summary>
    string? OsUser,
    string? FailureReasonKey,
    string? Details);

public sealed record CreateUserRequest
{
    public required string UserName { get; init; }
    public required string FullName { get; init; }
    public required string InitialPassword { get; init; }

    public string? FullNameArabic { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }

    public ProfessionalQualification Qualification { get; init; } = ProfessionalQualification.None;
    public string? ProfessionalOrderNumber { get; init; }
    public string? Inpe { get; init; }
    public DateOnly? QualificationValidUntil { get; init; }

    public AppLanguage PreferredLanguage { get; init; } = AppLanguage.French;

    public IReadOnlyList<Guid> RoleIds { get; init; } = [];
    public IReadOnlyList<Guid> SiteIds { get; init; } = [];
    public Guid? DefaultSiteId { get; init; }

    /// <summary>Empty means every department, which is only sensible for the responsible biologist.</summary>
    public IReadOnlyList<string> AuthorizedDepartmentCodes { get; init; } = [];
}

public sealed record UpdateUserRequest
{
    public required Guid UserId { get; init; }
    public required string FullName { get; init; }

    public string? FullNameArabic { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }

    public ProfessionalQualification Qualification { get; init; }
    public string? ProfessionalOrderNumber { get; init; }
    public string? Inpe { get; init; }
    public DateOnly? QualificationValidUntil { get; init; }

    public AppLanguage PreferredLanguage { get; init; }

    public IReadOnlyList<string> AuthorizedDepartmentCodes { get; init; } = [];
}

/// <summary>
/// User and role administration.
/// <para>
/// Three rules here are structural rather than cosmetic, and each exists because of a failure that
/// is expensive to undo:
/// </para>
/// <list type="bullet">
/// <item><b>Nobody is ever deleted.</b> A personnel record is the actor on thousands of audit
/// entries and the signature on released reports; removing it would orphan the traceability that
/// loi n° 12-01 and ISO 15189 §4.13 require be kept for years. Deactivation is the only
/// withdrawal.</item>
/// <item><b>The last administrator cannot be locked out.</b> Deactivating the only account holding
/// <c>admin.users.manage</c>, or stripping it of that role, is refused. Without this the recovery
/// path is a hand-written SQL update against a production database, which is exactly the kind of
/// unaudited change the audit trail exists to prevent.</item>
/// <item><b>Biological validation cannot be granted to an unqualified user.</b> Loi n° 12-01
/// reserves the release of medical biology results to a registered biologist, so a role carrying
/// <c>results.validate_biological</c> is refused for anyone whose personnel record does not show
/// the qualification and an Ordre registration number. The same check runs again at validation
/// time — this one exists so the mistake is caught when it is made, not months later.</item>
/// </list>
/// </summary>
public sealed class UserAdministrationService(
    IRepository<AppUser> users,
    IRepository<Role> roles,
    IAdministrationQueries queries,
    IAuthenticationService authentication,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    ISecurityAuditor securityAuditor,
    IPasswordHasher passwordHasher,
    PasswordPolicy passwordPolicy,
    IClock clock)
{
    // ---- Reads -------------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<UserRow>>> GetUsersAsync(
        bool includeInactive = false, string? search = null, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ViewUsers);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<UserRow>>(permitted.FirstError);

        return Result.Success(
            await queries.GetUsersAsync(includeInactive, search, clock.Today,
                                         passwordPolicy.ExpiryDays, clock.Now, ct));
    }

    public async Task<Result<IReadOnlyList<RoleRow>>> GetRolesAsync(bool includeInactive = false,
                                                                     CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Administration.ViewUsers,
                                          Permissions.Administration.ManageRoles);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<RoleRow>>(permitted.FirstError);

        return Result.Success(await queries.GetRolesAsync(includeInactive, ct));
    }

    public async Task<Result<IReadOnlyList<SiteRow>>> GetSitesAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ViewUsers);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<SiteRow>>(permitted.FirstError);

        return Result.Success(await queries.GetSitesAsync(ct));
    }

    /// <summary>
    /// The recent security log. Kept on this screen rather than behind a separate report because an
    /// administrator looking at accounts is the person who needs to notice a run of failed sign-ins.
    /// </summary>
    public async Task<Result<IReadOnlyList<SecurityEventRow>>> GetRecentSecurityEventsAsync(
        Guid? userId = null, int take = 200, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ViewAuditTrail);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<SecurityEventRow>>(permitted.FirstError);

        return Result.Success(await queries.GetRecentSecurityEventsAsync(userId, take, ct));
    }

    /// <summary>The permission catalogue, grouped by module, as the role editor renders it.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> GetPermissionCatalogue() =>
        Permissions.ByModule();

    // ---- Users -------------------------------------------------------------------------------------

    public async Task<Result<Guid>> CreateUserAsync(CreateUserRequest request,
                                                     CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageUsers);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var errors = new List<Error>();

        var userName = request.UserName?.Trim().ToLowerInvariant() ?? string.Empty;

        if (userName.Length < 3)
            errors.Add(Error.Validation("User_NameTooShort", 3));

        if (userName.Any(char.IsWhiteSpace))
            errors.Add(Error.Validation("User_NameCannotContainSpaces"));

        if (string.IsNullOrWhiteSpace(request.FullName))
            errors.Add(Error.Validation("User_FullNameRequired"));

        if (errors.Count > 0) return Result.Failure<Guid>(errors);

        if (await queries.UserNameExistsAsync(userName, ct))
            return Result.Failure<Guid>(Error.Conflict("User_NameAlreadyTaken", userName));

        var passwordValid = passwordPolicy.Validate(request.InitialPassword);
        if (passwordValid.IsFailure) return Result.Failure<Guid>(passwordValid.Errors);

        var qualification = ValidateQualification(request.Qualification,
                                                   request.ProfessionalOrderNumber,
                                                   request.QualificationValidUntil);
        if (qualification.IsFailure) return Result.Failure<Guid>(qualification.Errors);

        // Giving roles is the role administrator's decision (SetRolesAsync requires it too);
        // otherwise anyone who may create accounts could create one holding every permission.
        if (request.RoleIds.Count > 0)
        {
            var mayAssignRoles = guard.Require(Permissions.Administration.ManageRoles);
            if (mayAssignRoles.IsFailure) return Result.Failure<Guid>(mayAssignRoles.FirstError);
        }

        var roleCheck = await CheckRolesAgainstQualificationAsync(
            request.RoleIds, request.Qualification, request.ProfessionalOrderNumber, ct);
        if (roleCheck.IsFailure) return Result.Failure<Guid>(roleCheck.Errors);

        // Falls back to the administrator's own site: an account with no site access cannot sign in
        // anywhere, and the site panel is a separate step.
        IReadOnlyList<Guid> sites = request.SiteIds.Count > 0
            ? request.SiteIds
            : [currentUser.SiteId];

        var defaultSite = request.DefaultSiteId is { } preferred && sites.Contains(preferred)
            ? preferred
            : sites[0];

        var user = new AppUser(userName, request.FullName.Trim(),
                                passwordHasher.Hash(request.InitialPassword))
        {
            FullNameArabic = Blank(request.FullNameArabic),
            Email = Blank(request.Email),
            Phone = Blank(request.Phone),
            Qualification = request.Qualification,
            ProfessionalOrderNumber = Blank(request.ProfessionalOrderNumber),
            Inpe = Blank(request.Inpe),
            QualificationValidUntil = request.QualificationValidUntil,
            PreferredLanguage = request.PreferredLanguage,
            AuthorizedDepartmentCodesCsv = JoinDepartments(request.AuthorizedDepartmentCodes),

            // The administrator typed this password and therefore knows it. Forcing a change at
            // first sign-in is what makes the account the user's own rather than a shared secret,
            // which matters for every audit entry the account subsequently signs.
            MustChangePassword = true
        };

        foreach (var roleId in request.RoleIds.Distinct()) user.AssignRole(roleId);

        foreach (var siteId in sites.Distinct())
            user.GrantSiteAccess(siteId, siteId == defaultSite);

        await users.AddAsync(user, ct);

        unitOfWork.AuditReason = $"Création du compte {userName} ({request.FullName})";
        await unitOfWork.SaveChangesAsync(ct);

        await securityAuditor.LogAsync(SecurityEventKind.UserCreated, succeeded: true,
                                        targetUserId: user.Id,
                                        details: $"{userName} — {request.RoleIds.Count} rôle(s)",
                                        ct: ct);

        return Result.Success(user.Id);
    }

    public async Task<Result> UpdateUserAsync(UpdateUserRequest request,
                                               CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageUsers);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(request.FullName))
            return Result.Failure(Error.Validation("User_FullNameRequired"));

        var user = await users.GetByIdWithDetailsAsync(request.UserId, ct);
        if (user is null) return Result.Failure(Error.NotFound("User_NotFound"));

        var qualification = ValidateQualification(request.Qualification,
                                                   request.ProfessionalOrderNumber,
                                                   request.QualificationValidUntil);
        if (qualification.IsFailure) return qualification;

        // Downgrading a qualification while the user still holds a validating role would leave a
        // biologist role attached to someone the law no longer lets validate. Checked against the
        // roles the user actually has, not against the ones being set. Only when the qualification
        // or its registration actually changes: correcting the telephone number of the seeded
        // administrator, whose role carries every permission, must not be refused for a
        // qualification nobody touched. Validation itself re-checks the qualification regardless.
        var qualificationChanged = request.Qualification != user.Qualification
                                   || Blank(request.ProfessionalOrderNumber) != user.ProfessionalOrderNumber;

        if (qualificationChanged)
        {
            var roleCheck = await CheckRolesAgainstQualificationAsync(
                [.. user.Roles.Select(r => r.RoleId)], request.Qualification,
                request.ProfessionalOrderNumber, ct);
            if (roleCheck.IsFailure) return roleCheck;
        }

        user.FullName = request.FullName.Trim();
        user.FullNameArabic = Blank(request.FullNameArabic);
        user.Email = Blank(request.Email);
        user.Phone = Blank(request.Phone);
        user.Qualification = request.Qualification;
        user.ProfessionalOrderNumber = Blank(request.ProfessionalOrderNumber);
        user.Inpe = Blank(request.Inpe);
        user.QualificationValidUntil = request.QualificationValidUntil;
        user.PreferredLanguage = request.PreferredLanguage;
        user.AuthorizedDepartmentCodesCsv = JoinDepartments(request.AuthorizedDepartmentCodes);

        users.Update(user);

        unitOfWork.AuditReason = $"Modification du compte {user.UserName}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Replaces a user's roles.
    /// <para>
    /// Refuses the change if it would leave no active account able to manage users, and refuses to
    /// grant biological validation to a user whose personnel record does not support it.
    /// </para>
    /// </summary>
    public async Task<Result> SetRolesAsync(Guid userId, IReadOnlyList<Guid> roleIds,
                                             CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageRoles);
        if (permitted.IsFailure) return permitted;

        var user = await users.GetByIdWithDetailsAsync(userId, ct);
        if (user is null) return Result.Failure(Error.NotFound("User_NotFound"));

        var wanted = roleIds.Distinct().ToList();
        var existing = user.Roles.Select(r => r.RoleId).ToList();

        // Only the roles being attached are checked: a role the user already holds was checked
        // when it was given, and re-checking it would stop the seeded administrator — whose role
        // carries every permission — from ever having a role added or removed.
        var qualified = await CheckRolesAgainstQualificationAsync(
            [.. wanted.Except(existing)], user.Qualification, user.ProfessionalOrderNumber, ct);
        if (qualified.IsFailure) return qualified;

        var granted = await queries.GetPermissionsForRolesAsync(wanted, ct);

        var lockout = await CheckAdministratorSurvivesAsync(
            user, stillAdministrator: granted.Contains(Permissions.Administration.ManageUsers),
            stillActive: user.IsActive, ct);
        if (lockout.IsFailure) return lockout;

        foreach (var roleId in existing.Except(wanted)) user.RemoveRole(roleId);
        foreach (var roleId in wanted.Except(existing)) user.AssignRole(roleId);

        users.Update(user);

        unitOfWork.AuditReason = $"Rôles de {user.UserName} — {wanted.Count} rôle(s)";
        await unitOfWork.SaveChangesAsync(ct);

        // Logged separately from the entity audit: a privilege change is the event a security
        // review looks for, and finding it should not mean reading a diff of a join table.
        var added = wanted.Except(existing).Count();
        var removed = existing.Except(wanted).Count();

        if (added > 0)
        {
            await securityAuditor.LogAsync(SecurityEventKind.RoleAssigned, succeeded: true,
                                            targetUserId: user.Id,
                                            details: $"{added} rôle(s) attribué(s)", ct: ct);
        }

        if (removed > 0)
        {
            await securityAuditor.LogAsync(SecurityEventKind.RoleRemoved, succeeded: true,
                                            targetUserId: user.Id,
                                            details: $"{removed} rôle(s) retiré(s)", ct: ct);
        }

        return Result.Success();
    }

    public async Task<Result> SetSiteAccessAsync(Guid userId, IReadOnlyList<Guid> siteIds,
                                                  Guid? defaultSiteId,
                                                  CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageUsers);
        if (permitted.IsFailure) return permitted;

        if (siteIds.Count == 0)
            return Result.Failure(Error.Validation("User_AtLeastOneSiteRequired"));

        var user = await users.GetByIdWithDetailsAsync(userId, ct);
        if (user is null) return Result.Failure(Error.NotFound("User_NotFound"));

        var wanted = siteIds.Distinct().ToList();

        var preferred = defaultSiteId is { } candidate && wanted.Contains(candidate)
            ? candidate : wanted[0];

        // Site access is granted through the aggregate, which ignores duplicates; revocation is a
        // real removal, so it goes through the repository-tracked collection the same way roles do.
        foreach (var siteId in wanted) user.GrantSiteAccess(siteId, siteId == preferred);

        foreach (var access in user.SiteAccess.Where(a => !wanted.Contains(a.SiteId)).ToList())
            user.RevokeSiteAccess(access.SiteId);

        foreach (var access in user.SiteAccess)
            access.IsDefaultSite = access.SiteId == preferred;

        users.Update(user);

        unitOfWork.AuditReason = $"Sites accessibles pour {user.UserName} — {wanted.Count}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Activates or deactivates an account. Never deletes: the record is the actor on every audit
    /// entry it ever signed, and the retention obligation outlives the employment.
    /// </summary>
    public async Task<Result> SetActiveAsync(Guid userId, bool active, string reason,
                                              CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageUsers);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("User_DeactivationReasonRequired"));

        var user = await users.GetByIdWithDetailsAsync(userId, ct);
        if (user is null) return Result.Failure(Error.NotFound("User_NotFound"));

        if (!active)
        {
            // Deactivating yourself signs you out of the session you are doing it from, and if you
            // are the only administrator it cannot be undone from inside the application.
            if (userId == currentUser.UserId)
                return Result.Failure(Error.Validation("User_CannotDeactivateSelf"));

            var granted = await queries.GetPermissionsForRolesAsync(
                [.. user.Roles.Select(r => r.RoleId)], ct);

            var lockout = await CheckAdministratorSurvivesAsync(
                user, stillAdministrator: granted.Contains(Permissions.Administration.ManageUsers),
                stillActive: false, ct);
            if (lockout.IsFailure) return lockout;
        }

        if (active) user.Activate(); else user.Deactivate();

        users.Update(user);

        unitOfWork.AuditReason =
            $"{(active ? "Réactivation" : "Désactivation")} du compte {user.UserName} : {reason}";

        await unitOfWork.SaveChangesAsync(ct);

        if (!active)
        {
            await securityAuditor.LogAsync(SecurityEventKind.UserDeactivated, succeeded: true,
                                            targetUserId: user.Id, details: reason, ct: ct);
        }

        return Result.Success();
    }

    /// <summary>Clears a lockout after a forgotten password, without changing the password.</summary>
    public async Task<Result> UnlockAsync(Guid userId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageUsers);
        if (permitted.IsFailure) return permitted;

        var user = await users.GetByIdAsync(userId, ct);
        if (user is null) return Result.Failure(Error.NotFound("User_NotFound"));

        // Unlocking a deactivated account would be a reactivation without its reason or its
        // last-administrator check; reactivation goes through SetActiveAsync.
        if (!user.IsActive)
            return Result.Failure(Error.Validation("User_CannotUnlockInactive", user.UserName));

        if (!user.IsLockedOut(clock.Now) && user.FailedLoginAttempts == 0)
            return Result.Failure(Error.Validation("User_NotLockedOut"));

        user.ClearLockout();

        users.Update(user);

        unitOfWork.AuditReason = $"Déverrouillage du compte {user.UserName}";
        await unitOfWork.SaveChangesAsync(ct);

        await securityAuditor.LogAsync(SecurityEventKind.AccountUnlocked, succeeded: true,
                                        targetUserId: user.Id, ct: ct);

        return Result.Success();
    }

    /// <summary>
    /// Administrative password reset. Delegated to the authentication service so the policy check,
    /// the hash and the security-stamp rotation happen in exactly one place.
    /// </summary>
    public async Task<Result> ResetPasswordAsync(Guid userId, string newPassword,
                                                  CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageUsers);
        if (permitted.IsFailure) return permitted;

        var user = await users.GetByIdWithDetailsAsync(userId, ct);
        if (user is null) return Result.Failure(Error.NotFound("User_NotFound"));

        // Whoever sets an account's password can sign in as that account. A user manager must not
        // gain, through someone else's account, a permission they were not given themselves.
        var held = await queries.GetPermissionsForRolesAsync([.. user.Roles.Select(r => r.RoleId)], ct);
        if (held.Any(p => !currentUser.Has(p)))
            return Result.Failure(Error.Validation("User_ResetPasswordExceedsOwnPermissions", user.UserName));

        return await authentication.ResetPasswordAsync(userId, newPassword, ct);
    }

    // ---- Roles -------------------------------------------------------------------------------------

    public async Task<Result<Guid>> CreateRoleAsync(string code, MultilingualText name,
                                                     IReadOnlyList<string> permissions,
                                                     MultilingualText? description = null,
                                                     CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageRoles);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        if (string.IsNullOrWhiteSpace(code))
            return Result.Failure<Guid>(Error.Validation("Role_CodeRequired"));

        if (string.IsNullOrWhiteSpace(name.Fr))
            return Result.Failure<Guid>(Error.Validation("Role_NameRequired"));

        var normalized = code.Trim().ToUpperInvariant();

        if (await queries.RoleCodeExistsAsync(normalized, ct))
            return Result.Failure<Guid>(Error.Conflict("Role_CodeAlreadyTaken", normalized));

        var unknown = permissions.Where(p => !Permissions.Exists(p)).ToList();
        if (unknown.Count > 0)
            return Result.Failure<Guid>(Error.Validation("Role_UnknownPermission", unknown[0]));

        var role = new Role(normalized, name) { Description = description };
        role.GrantRange(permissions);

        await roles.AddAsync(role, ct);

        unitOfWork.AuditReason = $"Création du rôle {normalized} — {permissions.Count} permission(s)";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(role.Id);
    }

    /// <summary>
    /// Replaces a role's permissions. Built-in roles may be re-permissioned — a laboratory's idea of
    /// what a technician may do is its own — but they cannot be renamed or deactivated, so the
    /// seeded role that holds <c>admin.*</c> stays identifiable.
    /// </summary>
    public async Task<Result> SetRolePermissionsAsync(Guid roleId, IReadOnlyList<string> permissions,
                                                       CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageRoles);
        if (permitted.IsFailure) return permitted;

        var role = await roles.GetByIdWithDetailsAsync(roleId, ct);
        if (role is null) return Result.Failure(Error.NotFound("Role_NotFound"));

        var unknown = permissions.Where(p => !Permissions.Exists(p)).ToList();
        if (unknown.Count > 0)
            return Result.Failure(Error.Validation("Role_UnknownPermission", unknown[0]));

        var wanted = permissions.Distinct(StringComparer.Ordinal).ToList();

        // Revoking user management from a role can orphan the whole installation. Checked against
        // every account the role is attached to, not just the one on screen.
        if (role.Has(Permissions.Administration.ManageUsers)
            && !wanted.Contains(Permissions.Administration.ManageUsers, StringComparer.Ordinal))
        {
            var survivors = await queries.CountActiveUsersWithPermissionAsync(
                Permissions.Administration.ManageUsers, excludingRoleId: roleId, ct: ct);

            if (survivors == 0)
                return Result.Failure(Error.Validation("Role_WouldRemoveLastAdministrator"));
        }

        // A role that carries biological validation cannot be left attached to users who are not
        // qualified for it; the grant is refused rather than silently ineffective.
        if (wanted.Contains(Permissions.Results.BiologicalValidate, StringComparer.Ordinal)
            && !role.Has(Permissions.Results.BiologicalValidate))
        {
            var unqualified = await queries.ListUnqualifiedHoldersAsync(roleId, clock.Today, ct);

            if (unqualified.Count > 0)
            {
                return Result.Failure(Error.Regulatory(
                    "Role_HoldersNotQualifiedForValidation",
                    string.Join(", ", unqualified.Take(5))));
            }
        }

        role.ReplacePermissions(wanted);

        roles.Update(role);

        unitOfWork.AuditReason = $"Permissions du rôle {role.Code} — {wanted.Count}";
        await unitOfWork.SaveChangesAsync(ct);

        await securityAuditor.LogAsync(SecurityEventKind.PermissionsChanged, succeeded: true,
                                        details: $"{role.Code} — {wanted.Count} permission(s)",
                                        ct: ct);

        return Result.Success();
    }

    public async Task<Result> SetRoleActiveAsync(Guid roleId, bool active,
                                                  CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageRoles);
        if (permitted.IsFailure) return permitted;

        var role = await roles.GetByIdWithDetailsAsync(roleId, ct);
        if (role is null) return Result.Failure(Error.NotFound("Role_NotFound"));

        if (role.IsBuiltIn && !active)
            return Result.Failure(Error.Validation("Role_BuiltInCannotBeDeactivated", role.Code));

        // A deactivated role grants nothing, so switching off a role carrying user management can
        // orphan the installation exactly as revoking the permission would — same check.
        if (!active && role.IsActive && role.Has(Permissions.Administration.ManageUsers))
        {
            var survivors = await queries.CountActiveUsersWithPermissionAsync(
                Permissions.Administration.ManageUsers, excludingRoleId: roleId, ct: ct);

            if (survivors == 0)
                return Result.Failure(Error.Validation("Role_WouldRemoveLastAdministrator"));
        }

        role.IsActive = active;
        roles.Update(role);

        unitOfWork.AuditReason = $"{(active ? "Activation" : "Désactivation")} du rôle {role.Code}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    // ---- Guards -------------------------------------------------------------------------------------

    /// <summary>
    /// Refuses a change that would leave the installation with nobody able to manage users.
    /// <para>
    /// The recovery from that state is a hand-written UPDATE against production, which is precisely
    /// the unaudited change the whole audit design exists to make unnecessary.
    /// </para>
    /// </summary>
    private async Task<Result> CheckAdministratorSurvivesAsync(
        AppUser user, bool stillAdministrator, bool stillActive, CancellationToken ct)
    {
        if (stillAdministrator && stillActive) return Result.Success();

        var others = await queries.CountActiveUsersWithPermissionAsync(
            Permissions.Administration.ManageUsers, excludingUserId: user.Id, ct: ct);

        return others > 0
            ? Result.Success()
            : Result.Failure(Error.Validation("User_WouldRemoveLastAdministrator", user.UserName));
    }

    /// <summary>
    /// Checks the personnel record against loi n° 12-01 before a validating role is attached. The
    /// same rule is enforced at validation time; catching it here means the mistake surfaces while
    /// the administrator still remembers making it.
    /// </summary>
    private async Task<Result> CheckRolesAgainstQualificationAsync(
        IReadOnlyList<Guid> roleIds, ProfessionalQualification qualification,
        string? orderNumber, CancellationToken ct)
    {
        if (roleIds.Count == 0) return Result.Success();

        var granted = await queries.GetPermissionsForRolesAsync(roleIds, ct);

        if (!granted.Contains(Permissions.Results.BiologicalValidate)) return Result.Success();

        if (qualification is not (ProfessionalQualification.MedicalBiologist
                              or ProfessionalQualification.PharmacistBiologist))
            return Result.Failure(Error.Regulatory("Validation_RequiresBiologistQualification"));

        if (string.IsNullOrWhiteSpace(orderNumber))
            return Result.Failure(Error.Regulatory("Validation_RequiresOrderRegistration"));

        return Result.Success();
    }

    /// <summary>
    /// A qualification is only meaningful with the registration that backs it. Checked on the
    /// personnel record itself so an incomplete competence record cannot be saved and then quietly
    /// fail an assessment two years later.
    /// </summary>
    private Result ValidateQualification(ProfessionalQualification qualification,
                                          string? orderNumber, DateOnly? validUntil)
    {
        var errors = new List<Error>();

        var professional = qualification is ProfessionalQualification.MedicalBiologist
                                         or ProfessionalQualification.PharmacistBiologist
                                         or ProfessionalQualification.PrescribingPhysician;

        if (professional && string.IsNullOrWhiteSpace(orderNumber))
            errors.Add(Error.Validation("User_OrderNumberRequiredForQualification"));

        // An already-expired authorisation is almost always a typo in the year. Refused rather than
        // stored, because stored it means a biologist who cannot validate and does not know why.
        if (validUntil is { } expiry && expiry < clock.Today)
            errors.Add(Error.Validation("User_QualificationAlreadyExpired", expiry));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? JoinDepartments(IReadOnlyList<string> codes) =>
        codes.Count == 0
            ? null
            : string.Join(',', codes.Select(c => c.Trim().ToUpperInvariant())
                                    .Where(c => c.Length > 0)
                                    .Distinct(StringComparer.Ordinal));
}

/// <summary>Read-side queries for the user and role administration screen.</summary>
public interface IAdministrationQueries
{
    Task<IReadOnlyList<UserRow>> GetUsersAsync(bool includeInactive, string? search, DateOnly today,
                                                int passwordExpiryDays, DateTimeOffset now,
                                                CancellationToken ct = default);

    Task<IReadOnlyList<RoleRow>> GetRolesAsync(bool includeInactive, CancellationToken ct = default);

    Task<IReadOnlyList<SiteRow>> GetSitesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<SecurityEventRow>> GetRecentSecurityEventsAsync(
        Guid? userId, int take, CancellationToken ct = default);

    Task<bool> UserNameExistsAsync(string userName, CancellationToken ct = default);

    Task<bool> RoleCodeExistsAsync(string code, CancellationToken ct = default);

    /// <summary>The union of the permissions carried by a set of roles.</summary>
    Task<IReadOnlySet<string>> GetPermissionsForRolesAsync(IReadOnlyList<Guid> roleIds,
                                                            CancellationToken ct = default);

    /// <summary>
    /// How many active users would still hold a permission if one user, or one role's grant of it,
    /// were removed. The lockout guard: zero means the change must be refused.
    /// </summary>
    Task<int> CountActiveUsersWithPermissionAsync(string permission,
                                                   Guid? excludingUserId = null,
                                                   Guid? excludingRoleId = null,
                                                   CancellationToken ct = default);

    /// <summary>
    /// User names of the role's holders who do not satisfy loi n° 12-01 for biological validation.
    /// Returned as names rather than a count because the administrator has to go and fix them.
    /// </summary>
    Task<IReadOnlyList<string>> ListUnqualifiedHoldersAsync(Guid roleId, DateOnly today,
                                                             CancellationToken ct = default);

    // ---- Installation configuration ------------------------------------------------------------

    /// <summary>
    /// Every site with its legal identity and the list of what still blocks invoicing and report
    /// release, so the configuration screen can show the administrator exactly what is outstanding.
    /// </summary>
    Task<IReadOnlyList<SiteDetail>> GetSiteDetailsAsync(DateOnly today,
                                                         CancellationToken ct = default);

    /// <summary>
    /// Whether any numbered document already carries this site's code. The code is a prefix in
    /// accession and invoice numbers, so renaming it afterwards splits one series into two.
    /// </summary>
    Task<bool> SiteHasNumberedDocumentsAsync(Guid siteId, CancellationToken ct = default);

    Task<bool> SiteCodeExistsAsync(string code, Guid excludingSiteId,
                                    CancellationToken ct = default);

    Task<IReadOnlyList<SequenceRow>> GetSequencesAsync(DateTimeOffset now,
                                                        CancellationToken ct = default);

    Task<IReadOnlyList<SettingRow>> GetSettingsAsync(CancellationToken ct = default);
}
