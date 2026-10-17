using System.Text.Json;
using System.Text.Json.Serialization;
using MarocLIS.Api.Contracts;

namespace MarocLIS.Mobile.Services;

/// <summary>
/// The shapes read from and sent to the API, serialised by generated code. The published
/// application is trimmed, and reflection-based serialisation of a type the trimmer could not see
/// being used fails at the first tap on the phone rather than at build time.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SignInRequest))]
[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(LanguageRequest))]
[JsonSerializable(typeof(WorklistResponse))]
[JsonSerializable(typeof(OrderFile))]
[JsonSerializable(typeof(List<HistoryPoint>))]
[JsonSerializable(typeof(ValidationRequest))]
[JsonSerializable(typeof(ValidationOutcomeResponse))]
[JsonSerializable(typeof(RaiseCriticalRequest))]
[JsonSerializable(typeof(RaisedCritical))]
[JsonSerializable(typeof(AcknowledgeCriticalRequest))]
[JsonSerializable(typeof(UnreachableRequest))]
[JsonSerializable(typeof(ReleasedReportResponse))]
[JsonSerializable(typeof(ApiProblem))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;

/// <summary>The embedded catalogues: one flat object of strings per language.</summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class StringsJsonContext : JsonSerializerContext;

/// <summary>An RFC 9457 problem as the API writes it (ApiProblems), messages already in the biologist's language.</summary>
public sealed class ApiProblem
{
    public int? Status { get; set; }
    public string? Title { get; set; }
    public string? Detail { get; set; }
    public List<ApiProblemError>? Errors { get; set; }
}

public sealed class ApiProblemError
{
    public string? Code { get; set; }
    public string? Key { get; set; }
    public string? Message { get; set; }
}
