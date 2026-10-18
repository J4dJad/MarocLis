using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Domain.Common;
using MarocLIS.Localization;
using Microsoft.AspNetCore.Mvc;

namespace MarocLIS.Api;

/// <summary>
/// Turns the application's refusals into RFC 9457 problem details, in the biologist's language.
/// <para>
/// The services already say what went wrong as resource keys with arguments — the same ones the
/// workstation shows in its message boxes. They are rendered here with the same catalogues, so a
/// refusal on the phone reads exactly as it would at the bench, in French, Arabic or English, and
/// the key travels alongside for a client that wants to react to a particular one.
/// </para>
/// </summary>
public static class ApiProblems
{
    /// <summary>
    /// The language of the request: the signed-in account's, else what the phone asks for — French
    /// when that is a language the laboratory has switched off.
    /// </summary>
    public static AppLanguage LanguageOf(HttpContext context)
    {
        // The last reading: refreshed by the session's endpoints, which every phone calls first.
        var active = context.RequestServices.GetService<IActiveLanguages>()?.Current ?? LanguageSettings.Default;
        return active.Coerce(RequestedLanguageOf(context));
    }

    private static AppLanguage RequestedLanguageOf(HttpContext context)
    {
        var user = context.RequestServices.GetService<ICurrentUser>();
        if (user is { IsAuthenticated: true }) return user.Language;

        var accepted = context.Request.Headers.AcceptLanguage.ToString();
        foreach (var part in accepted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tag = part.Split(';')[0].Trim();
            if (tag.StartsWith("ar", StringComparison.OrdinalIgnoreCase)) return AppLanguage.Arabic;
            if (tag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return AppLanguage.English;
            if (tag.StartsWith("fr", StringComparison.OrdinalIgnoreCase)) return AppLanguage.French;
        }

        return context.RequestServices.GetRequiredService<MobileApiOptions>().DefaultLanguage;
    }

    /// <summary>The errors, each rendered once, in <paramref name="language"/>.</summary>
    public static IReadOnlyList<string> Describe(ILocalizer localizer, IEnumerable<Error> errors, AppLanguage language) =>
    [
        .. errors.Select(e => localizer.Describe(e, language))
                 .Where(text => text.Length > 0)
                 .Distinct(StringComparer.Ordinal)
    ];

    /// <summary>A refused operation, with the HTTP status its first error calls for.</summary>
    public static IResult From(HttpContext context, IReadOnlyList<Error> errors, int? status = null)
    {
        var localizer = context.RequestServices.GetRequiredService<ILocalizer>();
        var language = LanguageOf(context);
        var first = errors.Count > 0 ? errors[0] : Error.None;

        var messages = Describe(localizer, errors, language);

        // The title is the first refusal, readable as it stands; the detail lists them all.
        var problem = new ProblemDetails
        {
            Status = status ?? StatusFor(first),
            Title = messages.Count > 0 ? messages[0] : first.ResourceKey,
            Detail = string.Join(Environment.NewLine, messages)
        };

        problem.Extensions["errors"] = errors
            .Select(e => new { code = e.Code, key = e.ResourceKey, message = localizer.Describe(e, language) })
            .ToList();

        return Results.Problem(problem);
    }

    public static IResult From(HttpContext context, Result result, int? status = null) =>
        From(context, result.Errors, status);

    /// <summary>A refusal the API itself decides, on one resource key.</summary>
    public static IResult From(HttpContext context, Error error, int? status = null) =>
        From(context, [error], status);

    /// <summary>
    /// The first of <paramref name="texts"/> longer than its column, as a refusal; null when all fit.
    /// Checked here because the columns are what refuses a longer text, at the save, as an
    /// unexpected error after the rest of the operation was already decided — the workstation
    /// bounds its input boxes for the same reason.
    /// </summary>
    public static Error? TooLong(params (string? Text, int Max)[] texts) =>
        texts.Where(t => t.Text is { } text && text.Trim().Length > t.Max)
             .Select(t => Error.Validation("Mobile_TextTooLong", t.Max))
             .FirstOrDefault();

    /// <summary>
    /// The status of a refusal by its error code: not found 404, conflict 409 (the phone reloads),
    /// forbidden 403, a regulatory gate 422 (the request was understood and the law refuses it),
    /// anything else 400.
    /// </summary>
    public static int StatusFor(Error error) => error.Code switch
    {
        "not_found" => StatusCodes.Status404NotFound,
        "conflict" => StatusCodes.Status409Conflict,
        "forbidden" => StatusCodes.Status403Forbidden,
        "regulatory" => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status400BadRequest
    };

    /// <summary>Writes a problem straight to the response, for the middleware and the cookie events.</summary>
    public static Task WriteAsync(HttpContext context, Error error, int status)
    {
        context.Response.StatusCode = status;
        return From(context, error, status).ExecuteAsync(context);
    }
}
