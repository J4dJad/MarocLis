using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using MarocLIS.Api.Contracts;

namespace MarocLIS.Mobile.Services;

/// <summary>What an API call came back with: a value, a refusal in the biologist's language, or no network.</summary>
public sealed class ApiResult<T>
{
    public T? Value { get; private init; }

    public HttpStatusCode? Status { get; private init; }

    public ApiProblem? Problem { get; private init; }

    /// <summary>The request never reached the server: no network, or the laboratory's server is off.</summary>
    public bool Offline { get; private init; }

    public bool IsSuccess => Status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices && !Offline;

    /// <summary>A value changed since it was displayed, or a call no longer to be made: the file is reloaded.</summary>
    public bool IsConflict => Status == HttpStatusCode.Conflict;

    /// <summary>The first error's resource key, for the few refusals the phone reacts to by name.</summary>
    public string? ProblemKey => Problem?.Errors?.FirstOrDefault()?.Key;

    /// <summary>Every message of the refusal, one per line, as the server wrote them.</summary>
    public string Message =>
        Problem?.Errors is { Count: > 0 } errors
            ? string.Join("\n", errors.Select(e => e.Message).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct())
            : Problem?.Title ?? string.Empty;

    public static ApiResult<T> Success(T? value, HttpStatusCode status) => new() { Value = value, Status = status };

    public static ApiResult<T> Refused(HttpStatusCode status, ApiProblem? problem) => new() { Status = status, Problem = problem };

    public static ApiResult<T> NoNetwork() => new() { Offline = true };
}

/// <summary>
/// The API, as the screens use it. Every request carries <see cref="ApiProtocol.RequestHeader"/>
/// (the API refuses a state-changing one without it) and the language of the screen; every answer
/// touches the session's idle clock; a 401 on anything but the sign-in itself ends the session on
/// the phone too, whatever the reason the server gives.
/// </summary>
public sealed class ApiClient(HttpClient http, SessionState session, Texts texts, Browser browser)
{
    private static ApiJsonContext Json => ApiJsonContext.Default;

    public Task<ApiResult<SessionInfo>> SignInAsync(string userName, string password) =>
        SendAsync(HttpMethod.Post, ApiProtocol.SessionRoute, new SignInRequest(userName, password),
                  Json.SignInRequest, Json.SessionInfo, signingIn: true);

    public Task<ApiResult<SessionInfo>> CurrentSessionAsync() =>
        SendAsync<object, SessionInfo>(HttpMethod.Get, ApiProtocol.SessionRoute, null, null, Json.SessionInfo, quiet: true);

    public Task<ApiResult<object>> SignOutAsync() =>
        SendAsync<object, object>(HttpMethod.Delete, ApiProtocol.SessionRoute, null, null, null);

    public Task<ApiResult<SessionInfo>> ChangeLanguageAsync(UiLanguage language) =>
        SendAsync(HttpMethod.Put, ApiProtocol.LanguageRoute, new LanguageRequest(language.ToString()),
                  Json.LanguageRequest, Json.SessionInfo);

    public Task<ApiResult<WorklistResponse>> WorklistAsync(string filter) =>
        SendAsync<object, WorklistResponse>(HttpMethod.Get, $"{ApiProtocol.WorklistRoute}?filtre={Uri.EscapeDataString(filter)}",
                                            null, null, Json.WorklistResponse);

    public Task<ApiResult<OrderFile>> OrderAsync(Guid orderId) =>
        SendAsync<object, OrderFile>(HttpMethod.Get, $"/api/dossiers/{orderId}", null, null, Json.OrderFile);

    public Task<ApiResult<List<HistoryPoint>>> HistoryAsync(Guid orderId, Guid parameterId) =>
        SendAsync<object, List<HistoryPoint>>(HttpMethod.Get, $"/api/dossiers/{orderId}/parametres/{parameterId}/historique",
                                              null, null, Json.ListHistoryPoint);

    public Task<ApiResult<ValidationOutcomeResponse>> ValidateAsync(Guid orderId, ValidationRequest request) =>
        SendAsync(HttpMethod.Post, $"/api/dossiers/{orderId}/validation", request, Json.ValidationRequest,
                  Json.ValidationOutcomeResponse);

    public Task<ApiResult<RaisedCritical>> RaiseCriticalAsync(Guid resultId, RaiseCriticalRequest request) =>
        SendAsync(HttpMethod.Post, $"/api/critiques/{resultId}", request, Json.RaiseCriticalRequest, Json.RaisedCritical);

    public Task<ApiResult<object>> AcknowledgeCriticalAsync(Guid notificationId, AcknowledgeCriticalRequest request) =>
        SendAsync<AcknowledgeCriticalRequest, object>(HttpMethod.Post,
            $"/api/critiques/notifications/{notificationId}/acquittement", request, Json.AcknowledgeCriticalRequest, null);

    public Task<ApiResult<object>> UnreachableAsync(Guid notificationId, UnreachableRequest request) =>
        SendAsync<UnreachableRequest, object>(HttpMethod.Post,
            $"/api/critiques/notifications/{notificationId}/injoignable", request, Json.UnreachableRequest, null);

    public Task<ApiResult<ReleasedReportResponse>> ReleaseReportAsync(Guid orderId) =>
        SendAsync<object, ReleasedReportResponse>(HttpMethod.Post, $"/api/dossiers/{orderId}/compte-rendu", null, null,
                                                  Json.ReleasedReportResponse);

    /// <summary>
    /// The address of one preview page, for an <c>img</c>: the browser fetches it with the session
    /// cookie, the server sends it with no-store, and nothing is written to the phone's storage.
    /// </summary>
    public static string PreviewPageUrl(Guid reportId, int page) => $"api/comptes-rendus/{reportId}/apercu?page={page}";

    private async Task<ApiResult<TResponse>> SendAsync<TRequest, TResponse>(
        HttpMethod method, string uri, TRequest? body, JsonTypeInfo<TRequest>? requestInfo,
        JsonTypeInfo<TResponse>? responseInfo, bool signingIn = false, bool quiet = false)
    {
        using var request = new HttpRequestMessage(method, uri.TrimStart('/'));
        request.Headers.Add(ApiProtocol.RequestHeader, ApiProtocol.RequestHeaderValue);
        request.Headers.AcceptLanguage.ParseAdd(texts.LanguageTag);
        if (body is not null && requestInfo is not null) request.Content = JsonContent.Create(body, requestInfo);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request);
        }
        catch (HttpRequestException)
        {
            // Unreachable, whatever navigator.onLine believes: the screen shows « Hors connexion »
            // and its actions wait for the network.
            browser.SetOnline(false);
            return ApiResult<TResponse>.NoNetwork();
        }

        browser.SetOnline(true);

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                session.Touch();

                var value = responseInfo is not null && response.StatusCode != HttpStatusCode.NoContent
                    ? await response.Content.ReadFromJsonAsync(responseInfo)
                    : default;

                return ApiResult<TResponse>.Success(value, response.StatusCode);
            }

            var problem = await ReadProblemAsync(response);

            // The server ended the session — idle, revoked, another device, a restart. The phone
            // follows at once; the sign-in's own 401 is a wrong password, not an ended session.
            if (response.StatusCode == HttpStatusCode.Unauthorized && !signingIn)
                session.End(quiet ? SessionEndReason.NotSignedIn : SessionEndReason.Server,
                            problem?.Errors?.FirstOrDefault()?.Message ?? problem?.Title);

            return ApiResult<TResponse>.Refused(response.StatusCode, problem);
        }
    }

    private static async Task<ApiProblem?> ReadProblemAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(Json.ApiProblem);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}
