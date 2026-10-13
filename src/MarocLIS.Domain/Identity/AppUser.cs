using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Identity;

/// <summary>
/// An operator of the system. Personnel records double as the competence record required by
/// ISO 15189 §5.1 (qualification, authorisation scope, training) and as the identity used for every
/// audit entry, so a user is never physically deleted — only deactivated.
/// </summary>
public sealed class AppUser : AuditableAggregateRoot
{
    private readonly List<UserRole> _roles = [];
    private readonly List<UserSiteAccess> _siteAccess = [];

    private AppUser() { }

    public AppUser(string userName, string fullName, string passwordHash)
    {
        UserName = userName.Trim().ToLowerInvariant();
        FullName = fullName;
        PasswordHash = passwordHash;
        SecurityStamp = Guid.NewGuid().ToString("N");
    }

    public string UserName { get; private set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    /// <summary>Name in Arabic script, printed on Arabic reports and signature blocks.</summary>
    public string? FullNameArabic { get; set; }

    public string? Email { get; set; }
    public string? Phone { get; set; }

    /// <summary>BCrypt hash. The plain password never leaves the login form.</summary>
    public string PasswordHash { get; private set; } = string.Empty;

    /// <summary>Rotated on every credential or role change; invalidates open sessions.</summary>
    public string SecurityStamp { get; private set; } = string.Empty;

    /// <summary>Forces a password change at next logon — set for seeded and reset accounts.</summary>
    public bool MustChangePassword { get; set; }

    public DateTimeOffset? PasswordChangedAt { get; private set; }

    public bool IsActive { get; private set; } = true;

    /// <summary>Preferred UI language; the operator can switch at any time without restarting.</summary>
    public AppLanguage PreferredLanguage { get; set; } = AppLanguage.French;

    // ---- Professional qualification (loi 12-01 + ISO 15189 competence record) ----------------

    public ProfessionalQualification Qualification { get; set; } = ProfessionalQualification.None;

    /// <summary>Registration number with the Ordre National des Médecins / des Pharmaciens.</summary>
    public string? ProfessionalOrderNumber { get; set; }

    /// <summary>INPE — needed when this biologist signs AMO-reimbursable reports.</summary>
    public string? Inpe { get; set; }

    /// <summary>Expiry of the professional authorisation; a lapsed biologist cannot validate.</summary>
    public DateOnly? QualificationValidUntil { get; set; }

    /// <summary>Handwritten signature scan, stamped on validated PDF reports.</summary>
    public byte[]? SignaturePng { get; set; }

    /// <summary>
    /// Departments this user is authorised to validate for (biochemistry, haematology, …).
    /// Empty means "all departments", which is only sensible for the responsible biologist.
    /// </summary>
    public string? AuthorizedDepartmentCodesCsv { get; set; }

    // ---- Login hardening --------------------------------------------------------------------

    public int FailedLoginAttempts { get; private set; }
    public DateTimeOffset? LockedOutUntil { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public string? LastLoginWorkstation { get; private set; }

    public IReadOnlyCollection<UserRole> Roles => _roles.AsReadOnly();
    public IReadOnlyCollection<UserSiteAccess> SiteAccess => _siteAccess.AsReadOnly();

    // Not ISoftDeletable. An operator who leaves is deactivated — <see cref="IsActive"/> — and must
    // remain resolvable forever, because their name is on every result they validated and every
    // audit entry they caused. Deleting an account, even softly, is not an operation a traceable
    // system can offer.
    //
    // It also carried the defect: UserRole holds a required reference here and has no filter of its
    // own, so a global "not deleted" filter on the principal filtered its own role rows out from
    // under it — which is an authorization decision being made by a delete flag.

    // ---- Behaviour --------------------------------------------------------------------------

    public void SetPassword(string newHash, DateTimeOffset now, bool mustChangeAtNextLogon = false)
    {
        PasswordHash = newHash;
        PasswordChangedAt = now;
        MustChangePassword = mustChangeAtNextLogon;
        SecurityStamp = Guid.NewGuid().ToString("N");
        FailedLoginAttempts = 0;
        LockedOutUntil = null;
    }

    public bool IsLockedOut(DateTimeOffset now) => LockedOutUntil is { } until && until > now;

    public void RegisterFailedLogin(DateTimeOffset now, int maxAttempts, TimeSpan lockoutDuration)
    {
        FailedLoginAttempts++;
        if (FailedLoginAttempts >= maxAttempts)
            LockedOutUntil = now.Add(lockoutDuration);
    }

    public void RegisterSuccessfulLogin(DateTimeOffset now, string? workstation)
    {
        FailedLoginAttempts = 0;
        LockedOutUntil = null;
        LastLoginAt = now;
        LastLoginWorkstation = workstation;
    }

    public void Deactivate() => IsActive = false;

    public void Activate()
    {
        IsActive = true;
        FailedLoginAttempts = 0;
        LockedOutUntil = null;
    }

    public void AssignRole(Guid roleId)
    {
        if (_roles.Any(r => r.RoleId == roleId)) return;
        _roles.Add(new UserRole(Id, roleId));
        SecurityStamp = Guid.NewGuid().ToString("N");
    }

    public void RemoveRole(Guid roleId)
    {
        var existing = _roles.FirstOrDefault(r => r.RoleId == roleId);
        if (existing is null) return;
        _roles.Remove(existing);
        SecurityStamp = Guid.NewGuid().ToString("N");
    }

    public void GrantSiteAccess(Guid siteId, bool isDefault = false)
    {
        if (_siteAccess.Any(s => s.SiteId == siteId)) return;
        _siteAccess.Add(new UserSiteAccess(Id, siteId, isDefault));
    }

    /// <summary>
    /// Withdraws access to a site. Unlike a role, site access is a plain scoping fact rather than a
    /// privilege record, so it is removed outright; the audit trail of the change is written by the
    /// interceptor.
    /// </summary>
    public void RevokeSiteAccess(Guid siteId)
    {
        var existing = _siteAccess.FirstOrDefault(s => s.SiteId == siteId);
        if (existing is not null) _siteAccess.Remove(existing);
    }

    public IReadOnlySet<string> AuthorizedDepartmentCodes =>
        string.IsNullOrWhiteSpace(AuthorizedDepartmentCodesCsv)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : AuthorizedDepartmentCodesCsv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this user may perform the legally binding biological validation of a result.
    /// Loi n° 12-01 reserves the release of medical biology results to a qualified biologist; the
    /// LIS enforces it structurally rather than relying on a permission grant alone.
    /// </summary>
    public Result CanBiologicallyValidate(string departmentCode, DateOnly today)
    {
        if (!IsActive)
            return Result.Failure(Error.Forbidden("User_Inactive"));

        if (Qualification is not (ProfessionalQualification.MedicalBiologist
                              or ProfessionalQualification.PharmacistBiologist))
            return Result.Failure(Error.Regulatory("Validation_RequiresBiologistQualification"));

        if (string.IsNullOrWhiteSpace(ProfessionalOrderNumber))
            return Result.Failure(Error.Regulatory("Validation_RequiresOrderRegistration"));

        if (QualificationValidUntil is { } until && until < today)
            return Result.Failure(Error.Regulatory("Validation_QualificationExpired", until));

        var scope = AuthorizedDepartmentCodes;
        if (scope.Count > 0 && !scope.Contains(departmentCode))
            return Result.Failure(Error.Forbidden("Validation_DepartmentOutOfScope", departmentCode));

        return Result.Success();
    }
}

/// <summary>Qualification categories recognised for Moroccan medical biology laboratories.</summary>
public enum ProfessionalQualification
{
    None = 0,

    /// <summary>Médecin biologiste.</summary>
    MedicalBiologist = 1,

    /// <summary>Pharmacien biologiste.</summary>
    PharmacistBiologist = 2,

    /// <summary>Technicien de laboratoire (diplôme d'état / licence).</summary>
    LaboratoryTechnician = 3,

    /// <summary>Infirmier / préleveur habilité.</summary>
    Nurse = 4,

    /// <summary>Personnel administratif.</summary>
    Administrative = 5,

    /// <summary>Médecin prescripteur (external consultation access).</summary>
    PrescribingPhysician = 6
}

public sealed class UserRole
{
    private UserRole() { }
    public UserRole(Guid userId, Guid roleId) { UserId = userId; RoleId = roleId; }

    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }
    public AppUser? User { get; private set; }
    public Role? Role { get; private set; }
}

public sealed class UserSiteAccess
{
    private UserSiteAccess() { }
    public UserSiteAccess(Guid userId, Guid siteId, bool isDefault)
    {
        UserId = userId; SiteId = siteId; IsDefaultSite = isDefault;
    }

    public Guid UserId { get; private set; }
    public Guid SiteId { get; private set; }
    public bool IsDefaultSite { get; set; }
}
