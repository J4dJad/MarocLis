using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Licensing;

/// <summary>A licence as read from its file: the terms and the signature over them.</summary>
public sealed record SignedLicence(LicenceTerms Terms, byte[] Signature, string Text);

/// <summary>
/// The <c>licence.lic</c> file: readable JSON, every field of <see cref="LicenceTerms"/> under its
/// French name, then the signature.
/// <para>
/// What is signed is not the file's text but a canonical form of the terms — the same fields in a
/// fixed order, without spaces, the modules sorted — rebuilt from what was read. So indentation,
/// line ends or an editor's re-saving do not matter, and any change of a value does. A field this
/// version does not know is refused rather than ignored: it would be signed text whose meaning
/// nobody here checks.
/// </para>
/// <para>
/// ECDSA on P-256 with SHA-256 (the signature as the fixed 64 bytes r‖s, in base64): .NET has no
/// Ed25519 of its own, and this keeps the verification inside the framework with no package to
/// trust and keep up to date. The design's principle is unchanged: a private key that signs, kept
/// by the vendor, and a public key that verifies, built into MarocLIS.
/// </para>
/// </summary>
public static class LicenceFile
{
    public const string Extension = ".lic";

    /// <summary>
    /// Far above any licence (a few hundred bytes, a thousand with every module): a file larger than
    /// this is not one, and is refused before it is read.
    /// </summary>
    public const int MaxFileBytes = 64 * 1024;

    private static readonly string[] Fields =
    [
        "format", "numero", "version", "laboratoire", "autorisation", "site", "type", "emission",
        "debut", "fin", "misesAJourJusquau", "postes", "modules", "serveur", "preavisJours",
        "graceJours", "paillasseJours"
    ];

    private const string SignatureField = "signature";
    private const string DateFormat = "yyyy-MM-dd";

    // Accented letters and Arabic names written as they are, not as \u escapes: the file is meant
    // to be read by whoever receives it.
    private static readonly JsonWriterOptions Readable = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonWriterOptions Compact = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The bytes the signature is computed over.</summary>
    public static byte[] CanonicalPayload(LicenceTerms terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Compact))
        {
            writer.WriteStartObject();
            WriteTerms(writer, terms);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>The file's text for <paramref name="terms"/> signed with <paramref name="signature"/>.</summary>
    public static string Write(LicenceTerms terms, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(signature);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Readable))
        {
            writer.WriteStartObject();
            WriteTerms(writer, terms);
            writer.WriteString(SignatureField, Convert.ToBase64String(signature));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>Signs <paramref name="terms"/> and returns the file's text.</summary>
    public static string Sign(LicenceTerms terms, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);

        var signature = privateKey.SignData(CanonicalPayload(Normalised(terms)), HashAlgorithmName.SHA256,
                                            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Write(Normalised(terms), signature);
    }

    /// <summary>Whether <paramref name="licence"/> was signed by the holder of <paramref name="publicKey"/>.</summary>
    public static bool Verify(SignedLicence licence, ECDsa publicKey)
    {
        ArgumentNullException.ThrowIfNull(licence);
        ArgumentNullException.ThrowIfNull(publicKey);

        try
        {
            return publicKey.VerifyData(CanonicalPayload(licence.Terms), licence.Signature, HashAlgorithmName.SHA256,
                                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Reads a licence file. A file that is not one is a failure, never an exception.</summary>
    public static Result<SignedLicence> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Fail("Licence_FileEmpty");
        if (text.Length > MaxFileBytes) return Fail("Licence_FileUnreadable");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = false });
        }
        catch (JsonException)
        {
            return Fail("Licence_FileUnreadable");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("Licence_FileUnreadable");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name)) return Fail("Licence_FileUnreadable");
                if (property.Name != SignatureField && !Fields.Contains(property.Name, StringComparer.Ordinal))
                    return Fail("Licence_FormatUnknown");
            }

            try
            {
                if (Int(root, "format") != LicenceTerms.CurrentFormat) return Fail("Licence_FormatUnknown");

                var kind = LicenceTerms.KindOf(String(root, "type"));
                if (kind is null) return Fail("Licence_FileUnreadable");

                var modules = root.TryGetProperty("modules", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(m => m.ValueKind == JsonValueKind.String
                                                            ? m.GetString()!
                                                            : throw new FormatException()).ToList()
                    : throw new FormatException();

                var terms = new LicenceTerms
                {
                    Format = LicenceTerms.CurrentFormat,
                    Number = String(root, "numero") ?? throw new FormatException(),
                    Version = Int(root, "version"),
                    Laboratory = String(root, "laboratoire") ?? throw new FormatException(),
                    Authorisation = String(root, "autorisation"),
                    Site = String(root, "site"),
                    Kind = kind.Value,
                    IssuedOn = Date(root, "emission") ?? throw new FormatException(),
                    StartsOn = Date(root, "debut") ?? throw new FormatException(),
                    EndsOn = Date(root, "fin"),
                    UpdatesUntil = Date(root, "misesAJourJusquau"),
                    Workstations = Int(root, "postes"),
                    Modules = modules,
                    ServerId = String(root, "serveur"),
                    WarningDays = Int(root, "preavisJours"),
                    GraceDays = Int(root, "graceJours"),
                    BenchClearingDays = Int(root, "paillasseJours")
                };

                var signatureText = root.TryGetProperty(SignatureField, out _) ? String(root, SignatureField) : null;
                if (string.IsNullOrWhiteSpace(signatureText)) return Fail("Licence_SignatureMissing");

                byte[] signature;
                try
                {
                    signature = Convert.FromBase64String(signatureText);
                }
                catch (FormatException)
                {
                    return Fail("Licence_SignatureInvalid");
                }

                // The canonical form sorts the modules; a file listing them in another order says the
                // same thing, so it is read in that form too.
                return Result.Success(new SignedLicence(Normalised(terms), signature, text));
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or KeyNotFoundException)
            {
                return Fail("Licence_FileUnreadable");
            }
        }
    }

    /// <summary>The terms as they are signed: modules sorted, without repeats, names trimmed.</summary>
    public static LicenceTerms Normalised(LicenceTerms terms) => terms with
    {
        Number = terms.Number.Trim(),
        Laboratory = terms.Laboratory.Trim(),
        Authorisation = Blank(terms.Authorisation),
        Site = Blank(terms.Site),
        ServerId = Blank(terms.ServerId)?.ToUpperInvariant(),
        Modules = [.. terms.Modules.Select(m => m.Trim()).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal)
                                   .Order(StringComparer.Ordinal)]
    };

    private static void WriteTerms(Utf8JsonWriter writer, LicenceTerms terms)
    {
        writer.WriteNumber("format", terms.Format);
        writer.WriteString("numero", terms.Number);
        writer.WriteNumber("version", terms.Version);
        writer.WriteString("laboratoire", terms.Laboratory);
        WriteNullable(writer, "autorisation", terms.Authorisation);
        WriteNullable(writer, "site", terms.Site);
        writer.WriteString("type", LicenceTerms.KindWord(terms.Kind));
        writer.WriteString("emission", terms.IssuedOn.ToString(DateFormat, CultureInfo.InvariantCulture));
        writer.WriteString("debut", terms.StartsOn.ToString(DateFormat, CultureInfo.InvariantCulture));
        WriteNullable(writer, "fin", terms.EndsOn?.ToString(DateFormat, CultureInfo.InvariantCulture));
        WriteNullable(writer, "misesAJourJusquau", terms.UpdatesUntil?.ToString(DateFormat, CultureInfo.InvariantCulture));
        writer.WriteNumber("postes", terms.Workstations);
        writer.WriteStartArray("modules");
        foreach (var module in terms.Modules.Order(StringComparer.Ordinal)) writer.WriteStringValue(module);
        writer.WriteEndArray();
        WriteNullable(writer, "serveur", terms.ServerId);
        writer.WriteNumber("preavisJours", terms.WarningDays);
        writer.WriteNumber("graceJours", terms.GraceDays);
        writer.WriteNumber("paillasseJours", terms.BenchClearingDays);
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static string? String(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) throw new KeyNotFoundException(name);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new FormatException(name)
        };
    }

    private static int Int(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) throw new KeyNotFoundException(name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw new FormatException(name);
    }

    private static DateOnly? Date(JsonElement root, string name) =>
        String(root, name) is { } text
            ? DateOnly.ParseExact(text, DateFormat, CultureInfo.InvariantCulture)
            : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Result<SignedLicence> Fail(string key) => Result.Failure<SignedLicence>(Error.Validation(key));
}
