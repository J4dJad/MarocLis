using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Identity;

/// <summary>
/// A named bundle of permissions. Built-in roles are flagged so the UI can stop an administrator
/// from deleting the role that holds <c>admin.*</c> and locking everyone out.
/// </summary>
public sealed class Role : AuditableAggregateRoot
{
    private readonly List<RolePermission> _permissions = [];

    private Role() { }

    public Role(string code, MultilingualText name, bool isBuiltIn = false)
    {
        Code = code.Trim().ToUpperInvariant();
        Name = name;
        IsBuiltIn = isBuiltIn;
    }

    public string Code { get; private set; } = string.Empty;
    public MultilingualText Name { get; set; } = new();
    public MultilingualText? Description { get; set; }

    /// <summary>Shipped with the product; cannot be renamed or deleted, only re-permissioned.</summary>
    public bool IsBuiltIn { get; private set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The shipped permissions the reference-data seeder has already offered this built-in role,
    /// separated by <c>';'</c>. Lets a later version grant only the permissions it introduces,
    /// so a permission an administrator deliberately removed is not silently granted back.
    /// <c>null</c> on a role created before this was recorded, or on a custom role.
    /// </summary>
    public string? SeededPermissions { get; private set; }

    public IReadOnlyCollection<RolePermission> Permissions => _permissions.AsReadOnly();

    public void Grant(string permission)
    {
        if (!Identity.Permissions.Exists(permission))
            throw new DomainException($"Unknown permission '{permission}'.");
        if (_permissions.Any(p => p.Permission == permission)) return;
        _permissions.Add(new RolePermission(Id, permission));
    }

    public void GrantRange(IEnumerable<string> permissions)
    {
        foreach (var p in permissions) Grant(p);
    }

    public void Revoke(string permission)
    {
        var existing = _permissions.FirstOrDefault(p => p.Permission == permission);
        if (existing is not null) _permissions.Remove(existing);
    }

    public void ReplacePermissions(IEnumerable<string> permissions)
    {
        _permissions.Clear();
        GrantRange(permissions);
    }

    public bool Has(string permission) => _permissions.Any(p => p.Permission == permission);

    /// <summary>
    /// Brings a built-in role up to date with the permission set shipped by this version.
    /// <para>
    /// Only permissions that were never offered before are granted; a shipped permission that was
    /// offered earlier and is now missing was removed by an administrator and stays removed. When
    /// nothing has been recorded yet (a role seeded by a version that did not keep track), the
    /// current state is taken as the administrator's intent: the shipped set is recorded as
    /// offered and nothing is granted.
    /// </para>
    /// </summary>
    /// <returns>The permissions granted by this call.</returns>
    public IReadOnlyList<string> ApplyShippedPermissions(IEnumerable<string> shipped)
    {
        var shippedSet = shipped.Distinct(StringComparer.Ordinal).ToList();
        var granted = new List<string>();

        if (SeededPermissions is not null)
        {
            var offered = ParseSeeded(SeededPermissions);
            foreach (var permission in shippedSet.Where(p => !offered.Contains(p) && !Has(p)))
            {
                Grant(permission);
                granted.Add(permission);
            }
        }

        RecordSeeded(shippedSet);
        return granted;
    }

    /// <summary>
    /// Withdraws, once, a shipped permission that a later version replaced with another, granting
    /// the replacement in its place.
    /// <para>
    /// <see cref="ApplyShippedPermissions"/> never revokes, by design, so a permission dropped from
    /// the shipped set stays on every existing installation. When the permission was a defect rather
    /// than a choice, that is not good enough. "Once" is tracked through the replacement: until it
    /// has been offered, the swap has not run. Must therefore be followed by
    /// <see cref="ApplyShippedPermissions"/> with a shipped set containing the replacement, which
    /// records it as offered; from then on an administrator who deliberately grants the old
    /// permission back keeps it.
    /// </para>
    /// </summary>
    /// <returns>Whether the old permission was withdrawn by this call.</returns>
    public bool ReplaceShippedPermissionOnce(string withdrawn, string replacement)
    {
        if (SeededPermissions is not null && ParseSeeded(SeededPermissions).Contains(replacement))
            return false;

        if (!Has(withdrawn)) return false;

        Revoke(withdrawn);
        Grant(replacement);
        return true;
    }

    /// <summary>Grants the full shipped set to a role the seeder has just created and records it.</summary>
    public void SeedShippedPermissions(IEnumerable<string> shipped)
    {
        var shippedSet = shipped.Distinct(StringComparer.Ordinal).ToList();
        GrantRange(shippedSet);
        RecordSeeded(shippedSet);
    }

    private void RecordSeeded(IEnumerable<string> shipped)
    {
        // Keep what was offered before too, so a permission dropped from one shipped set and
        // re-added in a later one is not treated as new and forced back on.
        var all = SeededPermissions is null ? new HashSet<string>(StringComparer.Ordinal)
                                            : ParseSeeded(SeededPermissions);
        all.UnionWith(shipped);
        SeededPermissions = string.Join(';', all.OrderBy(p => p, StringComparer.Ordinal));
    }

    private static HashSet<string> ParseSeeded(string value) =>
        new(value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);
}

public sealed class RolePermission
{
    private RolePermission() { }
    public RolePermission(Guid roleId, string permission) { RoleId = roleId; Permission = permission; }

    public Guid RoleId { get; private set; }
    public string Permission { get; private set; } = string.Empty;
    public Role? Role { get; private set; }
}
