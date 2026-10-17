using MarocLIS.Domain.Common;

namespace MarocLIS.Api;

/// <summary>The « MobileApi » section of appsettings.json.</summary>
public sealed class MobileApiOptions
{
    public const string SectionName = "MobileApi";

    /// <summary>
    /// The longest a phone stays signed in, however active: a session opened for the morning's
    /// rounds does not carry over into the night. Twelve hours covers a long shift.
    /// </summary>
    public TimeSpan MaxSessionLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>The language of a refusal answered before anyone is signed in, when the phone names none it knows.</summary>
    public AppLanguage DefaultLanguage { get; set; } = AppLanguage.French;
}
