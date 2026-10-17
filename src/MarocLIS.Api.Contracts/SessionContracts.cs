namespace MarocLIS.Api.Contracts;

/// <summary>The sign-in form of the phone. The site is the account's default one.</summary>
public sealed record SignInRequest(string UserName, string Password);

/// <summary>The signed-in biologist, as the phone shows it in its header.</summary>
/// <param name="Qualification">The professional qualification's name (MedicalBiologist, PharmacistBiologist…).</param>
/// <param name="Language">French, Arabic or English: the account's language, which the phone adopts.</param>
/// <param name="IdleTimeoutMinutes">
/// Inactivity after which the server ends the session (the password policy's idle timeout), so the
/// phone can lock its screen at the same moment instead of failing on the next tap.
/// </param>
/// <param name="Workstation">What the audit trail records as the device: « Mobile · … ».</param>
public sealed record SessionInfo(
    string UserName,
    string FullName,
    string Qualification,
    string SiteCode,
    string Language,
    int IdleTimeoutMinutes,
    string Workstation);
