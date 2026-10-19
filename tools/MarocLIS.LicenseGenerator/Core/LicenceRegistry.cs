using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarocLIS.Domain.Licensing;

namespace MarocLIS.LicenseGenerator.Core;

/// <summary>A licence as the generator issued it: its terms, its file, and what became of it.</summary>
public sealed record IssuedLicence
{
    public required LicenceTerms Terms { get; init; }

    /// <summary>The signed file, kept so it can be sent again without signing anew.</summary>
    public required string Content { get; init; }

    public DateTimeOffset IssuedAt { get; init; }

    /// <summary>Who to call for the renewal: a name, a telephone number.</summary>
    public string? Contact { get; init; }

    public DateOnly? RevokedOn { get; init; }
    public string? RevocationReason { get; init; }

    [JsonIgnore] public string Number => Terms.Number;
    [JsonIgnore] public int Version => Terms.Version;
    [JsonIgnore] public bool IsRevoked => RevokedOn is not null;

    /// <summary>The day the laboratory needs a new licence: the end, or the end of the updates for a perpetual one.</summary>
    [JsonIgnore]
    public DateOnly? RenewalDue => Terms.Kind == LicenceKind.Perpetual ? Terms.UpdatesUntil : Terms.EndsOn;
}

/// <summary>
/// The register of the licences issued, one JSON file beside the key. It numbers the licences,
/// replaces one by a new version, revokes one and lists those to renew.
/// </summary>
public sealed class LicenceRegistry
{
    public const string FileName = "registre-licences.json";

    /// <summary>The renewal follow-up looks this far ahead.</summary>
    public const int RenewalHorizonDays = 45;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public List<IssuedLicence> Licences { get; init; } = [];

    public static LicenceRegistry Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<LicenceRegistry>(File.ReadAllText(path), Json) ?? new LicenceRegistry()
            : new LicenceRegistry();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        KeyVault.WriteAtomically(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>« LIC-2026-0043 »: the year's next number, after every number already issued that year.</summary>
    public string NextNumber(int year)
    {
        var prefix = $"LIC-{year}-";
        var last = Licences.Select(l => l.Number)
                           .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
                           .Select(n => int.TryParse(n.AsSpan(prefix.Length), NumberStyles.None,
                                                     CultureInfo.InvariantCulture, out var i) ? i : 0)
                           .DefaultIfEmpty(0)
                           .Max();
        return prefix + (last + 1).ToString("0000", CultureInfo.InvariantCulture);
    }

    /// <summary>The highest version of each number: the licence the laboratory runs on.</summary>
    public IEnumerable<IssuedLicence> Current() =>
        Licences.GroupBy(l => l.Number, StringComparer.Ordinal)
                .Select(g => g.MaxBy(l => l.Version)!);

    public bool IsCurrent(IssuedLicence licence) =>
        !Licences.Any(l => l.Number == licence.Number && l.Version > licence.Version);

    /// <summary>
    /// The terms of the version that replaces <paramref name="licence"/>: the same number, the next
    /// version, issued today. MarocLIS takes a higher version of the number it has in place of it,
    /// and refuses the older one afterwards.
    /// </summary>
    public LicenceTerms Replacement(IssuedLicence licence, DateOnly today) =>
        licence.Terms with
        {
            Version = Licences.Where(l => l.Number == licence.Number).Max(l => l.Version) + 1,
            IssuedOn = today
        };

    /// <summary>
    /// The replacement that renews <paramref name="licence"/>: an annual one runs a year more from the
    /// day after its end (or from today if it has already lapsed), a perpetual one covers a year more
    /// of updates, a trial gets another trial period.
    /// </summary>
    public LicenceTerms Renewal(IssuedLicence licence, DateOnly today)
    {
        var next = Replacement(licence, today);
        switch (next.Kind)
        {
            case LicenceKind.Annual:
            {
                var start = next.EndsOn is { } end && end >= today ? end.AddDays(1) : today;
                return next with { StartsOn = start, EndsOn = start.AddYears(1).AddDays(-1) };
            }
            case LicenceKind.Perpetual:
            {
                var from = next.UpdatesUntil is { } until && until >= today ? until : today;
                return next with { UpdatesUntil = from.AddYears(1) };
            }
            case LicenceKind.Trial:
                return next with { StartsOn = today, EndsOn = today.AddDays(LicenceTerms.DefaultTrialDays) };
            default:
                return next;
        }
    }

    public void Add(IssuedLicence licence)
    {
        if (Licences.Any(l => l.Number == licence.Number && l.Version == licence.Version))
            throw new InvalidOperationException($"La licence {licence.Number} version {licence.Version} est déjà au registre.");
        Licences.Add(licence);
    }

    public IssuedLicence Revoke(IssuedLicence licence, DateOnly on, string reason)
    {
        var index = Licences.IndexOf(licence);
        if (index < 0) throw new InvalidOperationException("Cette licence n'est pas au registre.");
        return Licences[index] = licence with { RevokedOn = on, RevocationReason = reason };
    }

    /// <summary>
    /// The line to add to <c>licence-revoked.txt</c> in MarocLIS's source; the next version of
    /// MarocLIS refuses that licence. One version only, or every version of the number.
    /// </summary>
    public static string RevocationLine(IssuedLicence licence, bool everyVersion = false)
    {
        var entry = everyVersion ? licence.Number : $"{licence.Number}/{licence.Version}";
        var why = string.IsNullOrWhiteSpace(licence.RevocationReason) ? "révoquée" : licence.RevocationReason.Trim();
        return $"{entry}  # {licence.Terms.Laboratory} — {why} ({licence.RevokedOn:yyyy-MM-dd})";
    }

    /// <summary>
    /// The licences in force that end — or, perpetual, whose updates end — within the horizon, and
    /// those already ended and not yet renewed: the laboratories to call. A training licence never
    /// ends; a revoked one is not renewed.
    /// </summary>
    public IReadOnlyList<IssuedLicence> Renewals(DateOnly today, int horizonDays = RenewalHorizonDays) =>
    [
        .. Current().Where(l => !l.IsRevoked && l.RenewalDue is { } due && due <= today.AddDays(horizonDays))
                    .OrderBy(l => l.RenewalDue)
    ];

    /// <summary>The renewals as a CSV file for a spreadsheet: semicolons, as Excel opens them in French.</summary>
    public static string RenewalsCsv(IEnumerable<IssuedLicence> licences, DateOnly today)
    {
        var csv = new StringBuilder("Numéro;Version;Laboratoire;Site;Type;Échéance;Jours restants;Postes;Contact\r\n");
        foreach (var l in licences)
        {
            var due = l.RenewalDue!.Value;
            csv.Append(string.Join(';',
                Cell(l.Number), l.Version.ToString(CultureInfo.InvariantCulture), Cell(l.Terms.Laboratory),
                Cell(l.Terms.Site), Cell(GeneratorTexts.Kind(l.Terms.Kind)),
                due.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                (due.DayNumber - today.DayNumber).ToString(CultureInfo.InvariantCulture),
                l.Terms.Workstations.ToString(CultureInfo.InvariantCulture), Cell(l.Contact))).Append("\r\n");
        }

        return csv.ToString();
    }

    // A cell beginning with = + - @ is a formula to a spreadsheet; a laboratory's name is not.
    private static string Cell(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && "=+-@".Contains(text[0], StringComparison.Ordinal)) text = "'" + text;
        return text.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }
}
