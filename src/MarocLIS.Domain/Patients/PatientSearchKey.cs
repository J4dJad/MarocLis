using System.Globalization;
using System.Text;

namespace MarocLIS.Domain.Patients;

/// <summary>
/// Builds the normalised key a patient file is found by.
/// <para>
/// It lives in the domain, and in exactly one place, because two callers depend on it agreeing with
/// itself: the registration path writes <see cref="Patient.SearchKey"/> with it, and the reception
/// search normalises the typed term with it. If the two ever diverged, patients would become
/// unfindable by name — and an unfindable file is a file a clerk recreates as a duplicate, which
/// splits the clinical history and costs the next clinician the previous-result column that makes a
/// delta check mean anything.
/// </para>
/// </summary>
public static class PatientSearchKey
{
    /// <summary>The key stored on the patient record.</summary>
    public static string Build(string? lastName, string? firstName) =>
        Normalise(lastName + firstName);

    /// <summary>
    /// Strips diacritics, case, spaces and punctuation.
    /// <para>
    /// This is what makes "Bénani", "BENANI" and "ben ani" all find the same file. Dropping
    /// combining marks removes French accents and Arabic vowel points in the same pass, which
    /// matters because the same name is routinely written both ways on a CIN and on a prescription.
    /// </para>
    /// </summary>
    public static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
