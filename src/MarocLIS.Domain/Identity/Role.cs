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
}

public sealed class RolePermission
{
    private RolePermission() { }
    public RolePermission(Guid roleId, string permission) { RoleId = roleId; Permission = permission; }

    public Guid RoleId { get; private set; }
    public string Permission { get; private set; } = string.Empty;
    public Role? Role { get; private set; }
}
