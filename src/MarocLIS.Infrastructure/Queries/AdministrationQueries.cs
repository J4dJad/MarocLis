using MarocLIS.Application.Administration;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class AdministrationQueries(LisDbContext db) : IAdministrationQueries
{
    /// <summary>
    /// The personnel list.
    /// <para>
    /// Password age and qualification expiry are computed here rather than stored: both are
    /// functions of a date and a policy that can change, and a stored flag would be wrong from the
    /// moment somebody edited the expiry interval.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<UserRow>> GetUsersAsync(
        bool includeInactive, string? search, DateOnly today, int passwordExpiryDays,
        DateTimeOffset now, CancellationToken ct = default)
    {
        var query = db.Users.AsNoTracking();

        if (!includeInactive) query = query.Where(u => u.IsActive);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(u => EF.Functions.Like(u.UserName, $"%{term}%")
                                     || EF.Functions.Like(u.FullName, $"%{term}%")
                                     || (u.Email != null && EF.Functions.Like(u.Email, $"%{term}%")));
        }

        var rows = await query
            .OrderBy(u => u.FullName)
            .Select(u => new
            {
                u.Id,
                u.UserName,
                u.FullName,
                u.FullNameArabic,
                u.Email,
                u.Phone,
                u.Qualification,
                u.ProfessionalOrderNumber,
                u.Inpe,
                u.QualificationValidUntil,
                u.IsActive,
                u.LockedOutUntil,
                u.FailedLoginAttempts,
                u.MustChangePassword,
                u.LastLoginAt,
                u.LastLoginWorkstation,
                u.PasswordChangedAt,
                u.PreferredLanguage,
                u.AuthorizedDepartmentCodesCsv,
                u.PrescriberId,
                HasSignature = u.SignaturePng != null,
                RoleIds = u.Roles.Select(r => r.RoleId).ToList(),
                RoleCodes = u.Roles
                    .Select(r => db.Roles.Where(x => x.Id == r.RoleId)
                                          .Select(x => x.Code).FirstOrDefault() ?? string.Empty)
                    .ToList(),
                Sites = u.SiteAccess.Select(s => new { s.SiteId, s.IsDefaultSite }).ToList()
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(r =>
            {
                var passwordAge = r.PasswordChangedAt is { } changed
                    ? (int)(now - changed).TotalDays
                    : (int?)null;

                return new UserRow(
                    r.Id, r.UserName, r.FullName, r.FullNameArabic, r.Email, r.Phone,
                    r.Qualification, r.ProfessionalOrderNumber, r.Inpe, r.QualificationValidUntil,
                    r.QualificationValidUntil is { } until && until < today,
                    r.IsActive,
                    r.LockedOutUntil is { } lockedUntil && lockedUntil > now,
                    r.LockedOutUntil,
                    r.FailedLoginAttempts,
                    r.MustChangePassword,
                    r.LastLoginAt,
                    r.LastLoginWorkstation,
                    r.PasswordChangedAt,
                    passwordAge,
                    // A password that has never been changed counts as expired: it is still the one
                    // the administrator typed, and somebody other than its owner knows it.
                    passwordExpiryDays > 0
                        && (passwordAge is null || passwordAge >= passwordExpiryDays),
                    r.PreferredLanguage,
                    r.RoleIds,
                    [.. r.RoleCodes.Where(c => c.Length > 0).Order(StringComparer.Ordinal)],
                    Split(r.AuthorizedDepartmentCodesCsv),
                    [.. r.Sites.Select(s => s.SiteId)],
                    r.Sites.FirstOrDefault(s => s.IsDefaultSite)?.SiteId,
                    r.HasSignature,
                    r.PrescriberId);
            })
        ];
    }

    public async Task<IReadOnlyList<RoleRow>> GetRolesAsync(bool includeInactive,
                                                             CancellationToken ct = default)
    {
        var query = db.Roles.AsNoTracking();

        if (!includeInactive) query = query.Where(r => r.IsActive);

        var rows = await query
            .OrderByDescending(r => r.IsBuiltIn)
            .ThenBy(r => r.Code)
            .Select(r => new
            {
                r.Id,
                r.Code,
                r.Name,
                r.Description,
                r.IsBuiltIn,
                r.IsActive,
                // Active holders only: a role attached exclusively to leavers is effectively
                // unused, and showing it as in service hides that it can safely be re-permissioned.
                ActiveUserCount = db.Users.Count(u => u.IsActive && u.Roles.Any(x => x.RoleId == r.Id)),
                Permissions = r.Permissions.Select(p => p.Permission).ToList()
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(r => new RoleRow(
                r.Id, r.Code, r.Name, r.Description, r.IsBuiltIn, r.IsActive, r.ActiveUserCount,
                [.. r.Permissions.Order(StringComparer.Ordinal)]))
        ];
    }

    public async Task<IReadOnlyList<PrescriberOption>> GetPrescriberOptionsAsync(CancellationToken ct = default) =>
        await db.Prescribers.AsNoTracking()
            .OrderByDescending(p => p.IsActive)
            .ThenBy(p => p.LastName).ThenBy(p => p.FirstName)
            .Select(p => new PrescriberOption(
                p.Id,
                p.Kind == Domain.Orders.PrescriberKind.Institution
                || p.Kind == Domain.Orders.PrescriberKind.Laboratory
                    ? p.InstitutionName ?? p.LastName
                    : "Dr " + p.LastName + " " + p.FirstName,
                p.City, p.IsActive))
            .ToListAsync(ct);

    public Task<bool> PrescriberExistsAsync(Guid prescriberId, CancellationToken ct = default) =>
        db.Prescribers.AnyAsync(p => p.Id == prescriberId, ct);

    /// <summary>
    /// The sites an account may be given access to. A partner laboratory is left out: it is where
    /// samples are sent, and nobody signs in to work there.
    /// </summary>
    public async Task<IReadOnlyList<SiteRow>> GetSitesAsync(CancellationToken ct = default) =>
        await db.Sites.AsNoTracking()
            .Where(s => s.Kind != LabSiteKind.ReferralPartner)
            .OrderBy(s => s.Code)
            .Select(s => new SiteRow(s.Id, s.Code, s.Name, s.IsActive))
            .ToListAsync(ct);

    /// <summary>
    /// The recent security log, newest first. Capped by <paramref name="take"/> rather than paged:
    /// this is a review screen, and an administrator scrolling past two hundred events is looking
    /// for a report, not a list.
    /// </summary>
    public async Task<IReadOnlyList<SecurityEventRow>> GetRecentSecurityEventsAsync(
        Guid? userId, int take, CancellationToken ct = default)
    {
        var query = db.SecurityEvents.AsNoTracking();

        if (userId is { } id)
            query = query.Where(e => e.UserId == id || e.TargetUserId == id);

        var rows = await query
            .OrderByDescending(e => e.OccurredAt)
            .Take(Math.Clamp(take, 1, 2000))
            .Select(e => new
            {
                e.Id,
                e.OccurredAt,
                e.Kind,
                e.Succeeded,
                e.AttemptedUserName,
                // Fetched separately and coalesced below: the two name columns carry different
                // collations, and SQL Server refuses to merge them inside one CASE expression.
                AccountName = e.UserId == null
                    ? null
                    : db.Users.Where(u => u.Id == e.UserId).Select(u => u.UserName).FirstOrDefault(),
                e.Workstation,
                e.OsUser,
                e.FailureReasonKey,
                e.Details
            })
            .ToListAsync(ct);

        // The attempted name is kept even when no such account exists, which is the whole point of
        // recording it: a run of sign-ins against a name that was never valid is a different event
        // from a user mistyping their own password.
        return
        [
            .. rows.Select(e => new SecurityEventRow(
                e.Id, e.OccurredAt, e.Kind, e.Succeeded, e.AttemptedUserName ?? e.AccountName,
                e.Workstation, e.OsUser, e.FailureReasonKey, e.Details))
        ];
    }

    public Task<bool> UserNameExistsAsync(string userName, CancellationToken ct = default) =>
        // Deliberately ignores the soft-delete filter through IgnoreQueryFilters: a deactivated or
        // logically deleted account still owns its name, and reusing it would make two different
        // people indistinguishable in the audit trail.
        db.Users.IgnoreQueryFilters().AsNoTracking()
          .AnyAsync(u => u.UserName == userName, ct);

    public Task<bool> RoleCodeExistsAsync(string code, CancellationToken ct = default) =>
        db.Roles.AsNoTracking().AnyAsync(r => r.Code == code, ct);

    public async Task<IReadOnlySet<string>> GetPermissionsForRolesAsync(
        IReadOnlyList<Guid> roleIds, CancellationToken ct = default)
    {
        if (roleIds.Count == 0) return new HashSet<string>(StringComparer.Ordinal);

        var ids = roleIds.Distinct().ToList();

        var permissions = await db.Roles.AsNoTracking()
            .Where(r => ids.Contains(r.Id) && r.IsActive)
            .SelectMany(r => r.Permissions.Select(p => p.Permission))
            .Distinct()
            .ToListAsync(ct);

        return permissions.ToHashSet(StringComparer.Ordinal);
    }

    public Task<int> CountActiveUsersWithPermissionAsync(
        string permission, Guid? excludingUserId = null, Guid? excludingRoleId = null,
        CancellationToken ct = default)
    {
        var query = db.Users.AsNoTracking().Where(u => u.IsActive);

        if (excludingUserId is { } userId) query = query.Where(u => u.Id != userId);

        return query.CountAsync(
            u => u.Roles.Any(link =>
                (excludingRoleId == null || link.RoleId != excludingRoleId)
                && db.Roles.Any(r => r.Id == link.RoleId
                                     && r.IsActive
                                     && r.Permissions.Any(p => p.Permission == permission))),
            ct);
    }

    /// <summary>
    /// Holders of a role who do not satisfy loi n° 12-01 for biological validation. The three
    /// conditions are the same ones <see cref="AppUser.CanBiologicallyValidate"/> applies, restated
    /// as a query because the check has to run over every holder before the grant is allowed.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListUnqualifiedHoldersAsync(
        Guid roleId, DateOnly today, CancellationToken ct = default) =>
        await db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.Roles.Any(r => r.RoleId == roleId))
            .Where(u => (u.Qualification != ProfessionalQualification.MedicalBiologist
                         && u.Qualification != ProfessionalQualification.PharmacistBiologist)
                        || u.ProfessionalOrderNumber == null
                        || u.ProfessionalOrderNumber == string.Empty
                        || (u.QualificationValidUntil != null
                            && u.QualificationValidUntil < today))
            .OrderBy(u => u.UserName)
            .Select(u => u.UserName)
            .ToListAsync(ct);

    // ---- Installation configuration ------------------------------------------------------------

    public async Task<IReadOnlyList<SiteDetail>> GetSiteDetailsAsync(
        DateOnly today, CancellationToken ct = default)
    {
        // Loaded as entities rather than projected, because the two completeness verdicts are
        // domain logic (ValidateForInvoicing / ValidateForOperation) and duplicating those rules
        // in a projection is how the screen and the gate end up disagreeing.
        var sites = await db.Sites.AsNoTracking()
            .OrderBy(s => s.Code)
            .ToListAsync(ct);

        var biologistIds = sites.Where(s => s.ResponsibleBiologistUserId != null)
                                .Select(s => s.ResponsibleBiologistUserId!.Value)
                                .Distinct()
                                .ToList();

        var biologists = biologistIds.Count == 0
            ? []
            : await db.Users.AsNoTracking()
                .Where(u => biologistIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName })
                .ToListAsync(ct);

        return
        [
            .. sites.Select(s => new SiteDetail(
                s.Id, s.Code, s.Name, s.Kind, s.LegalName, s.Ice, s.FiscalId,
                s.CommercialRegisterNumber, s.CommercialRegisterCourt, s.ProfessionalTaxNumber,
                s.CnssAffiliationNumber, s.Inpe,
                s.HealthMinistryAuthorizationNumber, s.HealthMinistryAuthorizationDate,
                s.IsAccredited, s.AccreditationNumber, s.AccreditationValidUntil,
                s.ResponsibleBiologistUserId,
                biologists.FirstOrDefault(b => b.Id == s.ResponsibleBiologistUserId)?.FullName,
                s.AddressLine, s.City, s.Region, s.PostalCode,
                s.Phone, s.Fax, s.Email, s.Website,
                s.LogoPng != null, s.IsActive,
                [.. s.ValidateForInvoicing().Errors.Select(e => e.ResourceKey)],
                [.. s.ValidateForOperation(today).Errors.Select(e => e.ResourceKey)],
                s.LogoPng))
        ];
    }

    public async Task<bool> SiteHasNumberedDocumentsAsync(Guid siteId,
                                                           CancellationToken ct = default) =>
        await db.LabOrders.AsNoTracking().AnyAsync(o => o.SiteId == siteId, ct)
        || await db.Invoices.AsNoTracking().AnyAsync(i => i.SiteId == siteId, ct);

    public Task<bool> SiteCodeExistsAsync(string code, Guid excludingSiteId,
                                           CancellationToken ct = default)
    {
        // Upper-cased once, outside the expression tree, and invariantly. A site code is an
        // identifier: under the Turkish culture ToUpper() maps 'i' to 'İ', so a laboratory running
        // a tr-TR workstation would fail to find its own site "SIDI" and allocate a duplicate code
        // against a unique index.
        var normalised = code.Trim().ToUpperInvariant();

        // A deleted site still holds its code under the unique index, so it is looked for too:
        // otherwise the duplicate would surface as a database error instead of this refusal.
        return db.Sites.AsNoTracking().IgnoreQueryFilters()
                 .AnyAsync(s => s.Id != excludingSiteId && s.Code == normalised, ct);
    }

    public Task<bool> SiteHasUserAccessAsync(Guid siteId, CancellationToken ct = default) =>
        db.UserSiteAccess.AsNoTracking().AnyAsync(a => a.SiteId == siteId, ct);

    public Task<bool> AnyPartnerSiteAsync(IReadOnlyCollection<Guid> siteIds,
                                          CancellationToken ct = default)
    {
        var ids = siteIds.ToList();
        return db.Sites.AsNoTracking().IgnoreQueryFilters()
                 .AnyAsync(s => ids.Contains(s.Id) && s.Kind == LabSiteKind.ReferralPartner, ct);
    }

    public async Task<IReadOnlyList<SequenceRow>> GetSequencesAsync(
        DateTimeOffset now, CancellationToken ct = default)
    {
        var sequences = await db.NumberSequences.AsNoTracking().ToListAsync(ct);

        var siteCodes = await db.Sites.AsNoTracking()
            .Select(s => new { s.Id, s.Code })
            .ToListAsync(ct);

        return
        [
            .. sequences
                .Select(s =>
                {
                    var code = s.SiteId is { } id
                        ? siteCodes.FirstOrDefault(c => c.Id == id)?.Code
                        : null;

                    return new SequenceRow(
                        s.Id, s.Purpose, s.SiteId, code, s.Year, s.Format,
                        // Rendered against the value that would actually come next, so the
                        // administrator sees the real shape of the next document number.
                        s.Render(s.NextValue, now, code),
                        s.NextValue, s.LastAllocatedValue, s.LastAllocatedAt, s.IsClosed,
                        s.LastAllocatedValue > 0);
                })
                .OrderBy(s => s.Purpose)
                .ThenBy(s => s.SiteCode)
                .ThenBy(s => s.Year)
        ];
    }

    public async Task<IReadOnlyList<SettingRow>> GetSettingsAsync(CancellationToken ct = default) =>
        await db.Settings.AsNoTracking()
            .OrderBy(s => s.Key)
            .Select(s => new SettingRow(s.Id, s.Key, s.Value, s.SiteId, s.Description,
                                         s.IsSecuritySensitive))
            .ToListAsync(ct);

    private static IReadOnlyList<string> Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
