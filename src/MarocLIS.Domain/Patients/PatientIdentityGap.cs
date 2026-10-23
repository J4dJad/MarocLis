namespace MarocLIS.Domain.Patients;

/// <summary>
/// The parts of an identity a file can still be missing. A file opened in an emergency, or typed
/// from a phone call, is completed later; the patient list shows which ones are still waiting.
/// </summary>
[Flags]
public enum PatientIdentityGap
{
    None = 0,

    /// <summary>Opened as "X-URG" before the patient could be identified.</summary>
    Provisional = 1,

    Gender = 2,

    DateOfBirth = 4
}
