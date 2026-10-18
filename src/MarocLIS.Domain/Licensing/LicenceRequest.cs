using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Licensing;

/// <summary>
/// « Exporter la demande »: the small file a laboratory sends to obtain a licence bound to its
/// server. Nothing in it is secret or signed; the generator reads it to fill the licence's fields.
/// </summary>
public sealed record LicenceRequest
{
    public const string Extension = ".demande";
    private const string Marker = "MarocLIS-demande-de-licence";

    public required string ServerId { get; init; }
    public required string Site { get; init; }
    public string? Laboratory { get; init; }
    public string? Authorisation { get; init; }

    /// <summary>The licence in place when the request was made, for a renewal or a replacement.</summary>
    public string? CurrentNumber { get; init; }
    public int? CurrentVersion { get; init; }

    public string? AppVersion { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public string Write()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = true,
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
               }))
        {
            writer.WriteStartObject();
            writer.WriteString("type", Marker);
            writer.WriteString("serveur", ServerId);
            writer.WriteString("site", Site);
            Nullable(writer, "laboratoire", Laboratory);
            Nullable(writer, "autorisation", Authorisation);
            Nullable(writer, "licenceActuelle", CurrentNumber);
            if (CurrentVersion is { } version) writer.WriteNumber("versionActuelle", version);
            else writer.WriteNull("versionActuelle");
            Nullable(writer, "versionMarocLIS", AppVersion);
            writer.WriteString("date", CreatedAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    public static Result<LicenceRequest> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result.Failure<LicenceRequest>(Error.Validation("Licence_RequestUnreadable"));

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || Text(root, "type") != Marker
                || Text(root, "serveur") is not { } server || Text(root, "site") is not { } site)
                return Result.Failure<LicenceRequest>(Error.Validation("Licence_RequestUnreadable"));

            return Result.Success(new LicenceRequest
            {
                ServerId = ServerIdentity.Normalise(server)!,
                Site = site,
                Laboratory = Text(root, "laboratoire"),
                Authorisation = Text(root, "autorisation"),
                CurrentNumber = Text(root, "licenceActuelle"),
                CurrentVersion = root.TryGetProperty("versionActuelle", out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetInt32()
                    : null,
                AppVersion = Text(root, "versionMarocLIS"),
                CreatedAt = Text(root, "date") is { } date
                            && DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                    ? at
                    : default
            });
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return Result.Failure<LicenceRequest>(Error.Validation("Licence_RequestUnreadable"));
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static void Nullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }
}
