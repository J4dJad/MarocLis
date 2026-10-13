using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Organization;

/// <summary>
/// A configuration value held in the database rather than in a file, so it is the same on every
/// workstation and a change to it is audited like any other record.
/// <para>
/// It lives in the domain and not beside the <c>DbContext</c>, where it started. An
/// application-layer interface — <c>IConfigurationWriter</c> — has to name this type in its
/// signatures, and the application layer cannot reference infrastructure; declaring the entity
/// there made that interface uncompilable. The same mistake, and the same fix, as
/// <see cref="Patients.PatientSearchKey"/>: a type two layers need belongs in the one they both
/// depend on.
/// </para>
/// </summary>
public sealed class AppSetting : Entity, IAuditable
{
    private AppSetting() { }

    public AppSetting(string key, string? value, Guid? siteId = null)
    {
        Key = key;
        Value = value;
        SiteId = siteId;
    }

    public string Key { get; private set; } = string.Empty;
    public string? Value { get; set; }

    /// <summary>Null for an installation-wide setting; set for a per-site override.</summary>
    public Guid? SiteId { get; private set; }

    public string? Description { get; set; }

    /// <summary>Marks settings that affect traceability or safety, so changes stand out in the audit.</summary>
    public bool IsSecuritySensitive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }
}
