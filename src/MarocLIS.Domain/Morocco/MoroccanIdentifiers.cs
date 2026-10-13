using System.Text.RegularExpressions;

namespace MarocLIS.Domain.Morocco;

/// <summary>
/// Validators for the Moroccan administrative identifiers a laboratory must capture and print.
/// Kept in the domain because an invalid ICE on an invoice, or a missing INPE on an AMO claim,
/// is a compliance failure and not a UI concern.
/// </summary>
public static partial class MoroccanIdentifiers
{
    /// <summary>
    /// CIN — Carte d'Identité Nationale. One or two letters followed by 5 or 6 digits
    /// (regional prefixes: A/AA Rabat, B/BE Casablanca, etc.). Case-insensitive, stored upper.
    /// </summary>
    [GeneratedRegex(@"^[A-Z]{1,2}\d{5,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex CinPattern();

    /// <summary>ICE — Identifiant Commun de l'Entreprise: exactly 15 digits. Mandatory on every invoice.</summary>
    [GeneratedRegex(@"^\d{15}$", RegexOptions.CultureInvariant)]
    private static partial Regex IcePattern();

    /// <summary>IF — Identifiant Fiscal: 7 to 8 digits.</summary>
    [GeneratedRegex(@"^\d{7,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex FiscalIdPattern();

    /// <summary>CNSS affiliation / immatriculation number: 7 to 10 digits.</summary>
    [GeneratedRegex(@"^\d{7,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex CnssPattern();

    /// <summary>INPE — Identifiant National des Professionnels et Établissements de santé (AMO claims).</summary>
    [GeneratedRegex(@"^\d{8,12}$", RegexOptions.CultureInvariant)]
    private static partial Regex InpePattern();

    /// <summary>Moroccan mobile/landline in national (0XXXXXXXXX) or E.164 (+212XXXXXXXXX) form.</summary>
    [GeneratedRegex(@"^(?:\+212|0)([5-7]\d{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();

    public static bool IsValidCin(string? value) =>
        !string.IsNullOrWhiteSpace(value) && CinPattern().IsMatch(Normalize(value));

    public static bool IsValidIce(string? value) =>
        !string.IsNullOrWhiteSpace(value) && IcePattern().IsMatch(Digits(value));

    public static bool IsValidFiscalId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && FiscalIdPattern().IsMatch(Digits(value));

    public static bool IsValidCnssNumber(string? value) =>
        !string.IsNullOrWhiteSpace(value) && CnssPattern().IsMatch(Digits(value));

    public static bool IsValidInpe(string? value) =>
        !string.IsNullOrWhiteSpace(value) && InpePattern().IsMatch(Digits(value));

    public static string NormalizeCin(string value) => Normalize(value);

    /// <summary>Returns the phone in E.164 (+212XXXXXXXXX) or null when it cannot be parsed.</summary>
    public static string? TryNormalizePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var compact = value.Replace(" ", string.Empty).Replace("-", string.Empty)
                           .Replace("(", string.Empty).Replace(")", string.Empty)
                           .Replace(".", string.Empty);
        var match = PhonePattern().Match(compact);
        return match.Success ? $"+212{match.Groups[1].Value}" : null;
    }

    private static string Normalize(string value) =>
        value.Replace(" ", string.Empty).Replace("-", string.Empty).ToUpperInvariant();

    private static string Digits(string value) => new(value.Where(char.IsDigit).ToArray());
}

/// <summary>
/// The 12 administrative regions of Morocco (2015 territorial division). Used for demographic
/// statistics and for the mandatory epidemiological declarations to the Ministry of Health.
/// </summary>
public enum MoroccanRegion
{
    Unspecified = 0,
    TangerTetouanAlHoceima = 1,
    LOriental = 2,
    FesMeknes = 3,
    RabatSaleKenitra = 4,
    BeniMellalKhenifra = 5,
    CasablancaSettat = 6,
    MarrakechSafi = 7,
    DraaTafilalet = 8,
    SoussMassa = 9,
    GuelmimOuedNoun = 10,
    LaayouneSakiaElHamra = 11,
    DakhlaOuedEdDahab = 12
}
