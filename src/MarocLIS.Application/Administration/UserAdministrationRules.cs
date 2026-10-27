using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Administration;

/// <summary>How an account reads in the list and in its pill: the same reading as the row's colours.</summary>
public enum AccountTone
{
    /// <summary>Deactivated: muted, nothing to act on.</summary>
    Inactive,
    Ok,

    /// <summary>A credential to renew: the password expired or to change, the qualification lapsed.</summary>
    Warning,

    /// <summary>Locked out, or a biologist who may not sign: the defects this list exists to expose.</summary>
    Critical
}

/// <summary>What « Utilisateurs » offers on the selected account, by right and by the account's state.</summary>
/// <param name="Reactivates">The deactivation button reads « Réactiver » on an inactive account.</param>
/// <param name="OwnAccount">The operator's own active account, which cannot be deactivated.</param>
public sealed record UserActions(bool Create, bool Edit, bool AssignRoles, bool SiteAccess, bool ResetPassword,
                                 bool Unlock, bool ToggleActive, bool Reactivates, bool OwnAccount);

/// <summary>The specific defect of an account, named so the administrator can act on it.</summary>
/// <param name="Argument">The date the message quotes (the lapse of the qualification, the end of the lockout, the age of the password).</param>
public sealed record UserNotice(string Key, bool Critical, object? Argument = null);

/// <summary>How a security event reads among four hundred routine sign-ins.</summary>
public enum SecurityEventEmphasis
{
    None,

    /// <summary>A privilege changed: the entries a security review is looking for.</summary>
    Privilege,

    /// <summary>A refusal.</summary>
    Failed
}

/// <summary>What the roles tab offers on the selected role.</summary>
/// <param name="Activates">The button reads « Activer le rôle » on an inactive role.</param>
/// <param name="EditPermissions">The tree of rights can be ticked; it stays readable without the right.</param>
public sealed record RoleActions(bool Create, bool Toggle, bool Activates, bool SavePermissions, bool EditPermissions);

/// <summary>The rights a save would grant and withdraw, counted for the confirmation.</summary>
public sealed record PermissionChange(int Added, int Removed)
{
    public bool IsNone => Added == 0 && Removed == 0;
}

/// <summary>The personnel record as typed in the editor, before it becomes a request.</summary>
public sealed record UserForm
{
    public string UserName { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string? FullNameArabic { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public ProfessionalQualification Qualification { get; init; }
    public string? OrderNumber { get; init; }
    public string? Inpe { get; init; }

    /// <summary>The end of the qualification's validity; null when it does not lapse.</summary>
    public DateOnly? QualificationValidUntil { get; init; }

    public AppLanguage Language { get; init; } = AppLanguage.French;

    /// <summary>The department codes as typed, separated by commas or semicolons.</summary>
    public string? Departments { get; init; }

    public Guid? PrescriberId { get; init; }
}

/// <summary>
/// The rules of « Utilisateurs » that need no window: how an account reads (its state, its pill,
/// its flags and the defect it is told about), what is offered on it, how a security event is
/// emphasised, what the editor refuses before the service is asked and the request it sends, the
/// prescribers its picker offers, what a role grants that matters beyond the application, and what
/// a change of rights comes to. The workstation's view and the web's screen apply the same ones;
/// the decisions themselves — who may be deactivated, granted validation, given a site — stay the
/// service's.
/// </summary>
/// <summary>The groups of buttons of the Utilisateurs screen a user is shown.</summary>
public sealed record UserScreenButtons(bool Accounts, bool Roles);

public static class UserAdministrationRules
{
    // The lengths of the columns: a longer value was refused by the database with an unexpected
    // error, and the form was lost with it.
    public const int UserNameMaxLength = 60;
    public const int NameMaxLength = 200;
    public const int PhoneMaxLength = 30;
    public const int OrderNumberMaxLength = 40;
    public const int InpeMaxLength = 20;
    public const int DepartmentsMaxLength = 500;
    public const int UserNameMinLength = 3;

    /// <summary>A role's code and name (code 40, name 300): a longer one failed the save with an unexpected error.</summary>
    public const int RoleCodeMaxLength = 40;
    public const int RoleNameMaxLength = 300;

    /// <summary>How many security events the users screen lists.</summary>
    public const int RecentEventCount = 300;

    /// <summary>The largest signature kept: scaled to what a signature block holds, as a PNG.</summary>
    public const int SignatureMaxWidth = 600;
    public const int SignatureMaxHeight = 300;

    public static bool IsBiologist(ProfessionalQualification qualification) =>
        qualification is ProfessionalQualification.MedicalBiologist or ProfessionalQualification.PharmacistBiologist;

    /// <summary>A validating qualification on a record that does not satisfy loi n° 12-01: flagged as strongly as a lockout.</summary>
    public static bool IsUnqualifiedBiologist(UserRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return IsBiologist(row.Qualification) && !row.QualifiedToValidate;
    }

    public static AccountTone Tone(UserRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!row.IsActive) return AccountTone.Inactive;
        if (row.IsLockedOut) return AccountTone.Critical;
        if (IsUnqualifiedBiologist(row)) return AccountTone.Critical;
        if (row.PasswordExpired || row.MustChangePassword || row.QualificationExpired) return AccountTone.Warning;
        return AccountTone.Ok;
    }

    /// <summary>
    /// The state's key — « Inactif », « Verrouillé », « Actif » — and, on an active account with
    /// failed sign-ins, how many (shown after it in brackets).
    /// </summary>
    public static (string Key, int? FailedAttempts) State(UserRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!row.IsActive) return ("Admin.Inactive", null);
        if (row.IsLockedOut) return ("Admin.LockedOut", null);
        return ("Admin.Active", row.FailedLoginAttempts > 0 ? row.FailedLoginAttempts : null);
    }

    /// <summary>
    /// A short glyph column rather than three more text columns: the administrator needs to see
    /// that something is wrong with the credential, then open the record to find out.
    /// </summary>
    public static IReadOnlyList<string> Flags(UserRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var flags = new List<string>(4);
        if (row.MustChangePassword) flags.Add("⚿");
        if (row.PasswordExpired) flags.Add("!");
        if (row.QualificationExpired) flags.Add("⌛");
        if (row.QualifiedToValidate) flags.Add("✓");
        return flags;
    }

    /// <summary>
    /// The defect the selected account is told about, by name — « invalide » tells an administrator
    /// nothing they can act on — or null when there is none.
    /// </summary>
    public static UserNotice? Notice(UserRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (IsUnqualifiedBiologist(row))
            return row.QualificationExpired && row.QualificationValidUntil is { } until
                ? new UserNotice("Admin.QualificationExpiredNotice", true, until)
                : new UserNotice("Admin.OrderNumberMissingNotice", true);

        if (row.IsLockedOut && row.LockedOutUntil is { } lockedUntil)
            return new UserNotice("Admin.LockedOutNotice", true, lockedUntil.ToLocalTime());

        if (row.PasswordExpired)
            return row.PasswordAgeDays is { } age
                ? new UserNotice("Admin.PasswordExpiredNotice", false, age)
                : new UserNotice("Admin.PasswordNeverChangedNotice", false);

        return null;
    }

    /// <summary>
    /// What is offered on <paramref name="row"/> (none selected: only the creation). Managing
    /// accounts and assigning roles are two rights; an inactive account is reactivated, not
    /// unlocked; one's own account cannot be deactivated — said up front rather than refused by
    /// the service once the reason has been typed.
    /// </summary>
    public static UserActions Actions(ICurrentUser user, UserRow? row)
    {
        ArgumentNullException.ThrowIfNull(user);

        var manage = user.Has(Permissions.Administration.ManageUsers);
        var roles = user.Has(Permissions.Administration.ManageRoles);
        var selected = row is not null;
        var own = row is { IsActive: true } && row.UserId == user.UserId;

        return new UserActions(
            Create: manage,
            Edit: manage && selected,
            AssignRoles: roles && selected,
            SiteAccess: manage && selected,
            ResetPassword: manage && selected,
            Unlock: manage && row is { IsActive: true } and ({ IsLockedOut: true } or { FailedLoginAttempts: > 0 }),
            ToggleActive: manage && selected && !own,
            Reactivates: row is { IsActive: false },
            OwnAccount: own);
    }

    /// <summary>
    /// Which groups of buttons the Utilisateurs screen shows at all, on every client: the accounts'
    /// (create, edit, sites, password, unlock, deactivate) with the right to manage accounts, the
    /// roles' (assign, create, deactivate, save the rights) with the right to manage roles. A user
    /// who may only look — the quality manager — sees the lists without buttons that do nothing.
    /// </summary>
    public static UserScreenButtons ButtonsShown(ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new UserScreenButtons(user.Has(Permissions.Administration.ManageUsers),
                                     user.Has(Permissions.Administration.ManageRoles));
    }

    /// <summary>The security log beside the accounts is the audit trail's, offered with its right.</summary>
    public static bool SecurityLogOffered(ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Has(Permissions.Administration.ViewAuditTrail);
    }

    /// <summary>
    /// A refusal, a privilege changed, or a routine event: the privilege changes are the entries a
    /// security review is looking for, and should not read the same as the sign-ins around them.
    /// </summary>
    public static SecurityEventEmphasis Emphasis(SecurityEventRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!row.Succeeded) return SecurityEventEmphasis.Failed;

        return row.Kind is SecurityEventKind.RoleAssigned or SecurityEventKind.RoleRemoved
                        or SecurityEventKind.PermissionsChanged or SecurityEventKind.PasswordReset
                        or SecurityEventKind.UserDeactivated or SecurityEventKind.SecuritySettingChanged
            ? SecurityEventEmphasis.Privilege
            : SecurityEventEmphasis.None;
    }

    // ---- The editor ----------------------------------------------------------------------------

    /// <summary>
    /// The requirement the qualification brings, stated when it is chosen so the service's refusal
    /// is never a surprise; null when it brings none.
    /// </summary>
    public static string? QualificationNoticeKey(ProfessionalQualification qualification) => qualification switch
    {
        ProfessionalQualification.MedicalBiologist or ProfessionalQualification.PharmacistBiologist
            => "Admin.BiologistRequirementNotice",
        ProfessionalQualification.PrescribingPhysician => "Admin.PhysicianRequirementNotice",
        _ => null
    };

    /// <summary>
    /// Only a biologist's record carries a signature: it attests the biological validation the
    /// reports it is printed on went through.
    /// </summary>
    public static bool SignatureAllowed(ProfessionalQualification qualification) => IsBiologist(qualification);

    /// <summary>The department codes typed, upper case, each once; empty means every department.</summary>
    public static IReadOnlyList<string> Departments(string? typed) =>
        string.IsNullOrWhiteSpace(typed)
            ? []
            : [.. typed.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Select(d => d.ToUpperInvariant())
                       .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// What the editor refuses before the service is asked, on the field it names: the full name,
    /// and for a new account a user name of three characters at least and an initial password.
    /// </summary>
    public static FieldRefusal? Check(UserForm form, bool creating)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (string.IsNullOrWhiteSpace(form.FullName)) return new FieldRefusal("fullname", "User_FullNameRequired");
        if (!creating) return null;

        if (form.UserName.Trim().Length < UserNameMinLength)
            return new FieldRefusal("username", "User_NameTooShort", Typed: UserNameMinLength.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return string.IsNullOrEmpty(form.Password) ? new FieldRefusal("password", "Admin.InitialPasswordRequired") : null;
    }

    public static CreateUserRequest ToCreateRequest(UserForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        return new CreateUserRequest
        {
            UserName = form.UserName.Trim(),
            FullName = form.FullName.Trim(),
            InitialPassword = form.Password,
            FullNameArabic = Blank(form.FullNameArabic),
            Email = Blank(form.Email),
            Phone = Blank(form.Phone),
            Qualification = form.Qualification,
            ProfessionalOrderNumber = Blank(form.OrderNumber),
            Inpe = Blank(form.Inpe),
            QualificationValidUntil = form.QualificationValidUntil,
            PreferredLanguage = form.Language,
            AuthorizedDepartmentCodes = Departments(form.Departments),
            PrescriberId = form.PrescriberId
        };
    }

    public static UpdateUserRequest ToUpdateRequest(Guid userId, UserForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        return new UpdateUserRequest
        {
            UserId = userId,
            FullName = form.FullName.Trim(),
            FullNameArabic = Blank(form.FullNameArabic),
            Email = Blank(form.Email),
            Phone = Blank(form.Phone),
            Qualification = form.Qualification,
            ProfessionalOrderNumber = Blank(form.OrderNumber),
            Inpe = Blank(form.Inpe),
            QualificationValidUntil = form.QualificationValidUntil,
            PreferredLanguage = form.Language,
            AuthorizedDepartmentCodes = Departments(form.Departments),
            PrescriberId = form.PrescriberId
        };
    }

    /// <summary>The editor opened on an existing account: what it shows, as it was recorded.</summary>
    public static UserForm FormOf(UserRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new UserForm
        {
            UserName = row.UserName,
            FullName = row.FullName,
            FullNameArabic = row.FullNameArabic,
            Email = row.Email,
            Phone = row.Phone,
            Qualification = row.Qualification,
            OrderNumber = row.ProfessionalOrderNumber,
            Inpe = row.Inpe,
            QualificationValidUntil = row.QualificationValidUntil,
            Language = row.PreferredLanguage,
            Departments = string.Join(", ", row.AuthorizedDepartmentCodes),
            PrescriberId = row.PrescriberId
        };
    }

    /// <summary>
    /// The prescribers the editor's picker offers: the active ones, and a retired one only for the
    /// account already linked to it.
    /// </summary>
    public static IReadOnlyList<PrescriberOption> PickablePrescribers(IEnumerable<PrescriberOption> options, Guid? linked)
    {
        ArgumentNullException.ThrowIfNull(options);
        return [.. options.Where(o => o.IsActive || o.PrescriberId == linked)];
    }

    /// <summary>« Dr EL FASSI Omar — Rabat »: the city when it is known.</summary>
    public static string PrescriberDisplay(PrescriberOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        return string.IsNullOrWhiteSpace(option.City) ? option.FullName : $"{option.FullName} — {option.City}";
    }

    /// <summary>
    /// A linked prescriber missing from the list (it could not be read), kept as it is rather than
    /// shown as « aucun », which saving would turn into a silent unlink.
    /// </summary>
    public static string UnlistedPrescriberDisplay(Guid prescriberId) => $"#{prescriberId.ToString()[..8]}";

    // ---- Roles and sites -----------------------------------------------------------------------

    /// <summary>The roles an account can be given: the active ones.</summary>
    public static IReadOnlyList<RoleRow> AssignableRoles(IEnumerable<RoleRow> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return [.. roles.Where(r => r.IsActive)];
    }

    /// <summary>
    /// What a role grants, beyond its count of rights: the two grants with consequences outside the
    /// application — one reserved by law, the other the one that can lock everybody out.
    /// </summary>
    public static (int Count, bool GrantsValidation, bool GrantsUserManagement) Grants(RoleRow role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return (role.Permissions.Count,
                role.Permissions.Contains(Permissions.Results.BiologicalValidate, StringComparer.Ordinal),
                role.Permissions.Contains(Permissions.Administration.ManageUsers, StringComparer.Ordinal));
    }

    /// <summary>The sites an account can be given: the active ones.</summary>
    public static IReadOnlyList<SiteRow> AssignableSites(IEnumerable<SiteRow> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);
        return [.. sites.Where(s => s.IsActive)];
    }

    /// <summary>The default site among those ticked: the one preferred when ticked, else the first.</summary>
    public static Guid? DefaultSite(IReadOnlyList<Guid> ticked, Guid? preferred)
    {
        ArgumentNullException.ThrowIfNull(ticked);
        if (ticked.Count == 0) return null;
        return preferred is { } id && ticked.Contains(id) ? id : ticked[0];
    }

    /// <summary>A user works on one site at least.</summary>
    public static string? CheckSites(IReadOnlyCollection<Guid> ticked)
    {
        ArgumentNullException.ThrowIfNull(ticked);
        return ticked.Count == 0 ? "User_AtLeastOneSiteRequired" : null;
    }

    /// <summary>
    /// What the roles tab offers on <paramref name="role"/>. A built-in role cannot be deactivated —
    /// the seeded role that holds admin.* has to stay identifiable — so only a role the laboratory
    /// created is offered it.
    /// </summary>
    public static RoleActions RoleActions(ICurrentUser user, RoleRow? role)
    {
        ArgumentNullException.ThrowIfNull(user);

        var manage = user.Has(Permissions.Administration.ManageRoles);
        return new RoleActions(manage, manage && role is { IsBuiltIn: false }, role is { IsActive: false },
                               manage && role is not null, manage && role is not null);
    }

    /// <summary>Deactivating a role its holders still use is confirmed with their number first.</summary>
    public static bool DeactivationNeedsConfirmation(RoleRow role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return role.IsActive && role.ActiveUserCount > 0;
    }

    /// <summary>The rights a save of <paramref name="selected"/> would grant and withdraw.</summary>
    public static PermissionChange Change(RoleRow role, IReadOnlyCollection<string> selected)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(selected);

        return new PermissionChange(selected.Except(role.Permissions, StringComparer.Ordinal).Count(),
                                    role.Permissions.Except(selected, StringComparer.Ordinal).Count());
    }

    /// <summary>The catalogue of rights by module, in the module's order, as the role editor lists it.</summary>
    public static IReadOnlyList<(string Module, IReadOnlyList<string> Permissions)> Catalogue() =>
        [.. UserAdministrationService.GetPermissionCatalogue()
                                     .OrderBy(g => g.Key, StringComparer.Ordinal)
                                     .Select(g => (g.Key, g.Value))];

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
