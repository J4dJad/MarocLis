using MarocLIS.Api.Contracts;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Results;

namespace MarocLIS.Api.Endpoints;

/// <summary>
/// The telephone call about a critical value, made from the phone: opened, then closed with the
/// recipient's read-back, or recorded as unreachable. The rules are the validation service's — a
/// value released only once its call is read back — and the steps are the workstation's.
/// </summary>
public static class CriticalEndpoints
{
    public static void MapCriticalEndpoints(this IEndpointRouteBuilder app)
    {
        var critical = app.MapGroup("/api/critiques").RequireAuthorization();

        critical.MapPost("/{resultId:guid}", RaiseAsync);
        critical.MapPost("/notifications/{notificationId:guid}/acquittement", AcknowledgeAsync);
        critical.MapPost("/notifications/{notificationId:guid}/injoignable", UnreachableAsync);
    }

    /// <summary>
    /// Opens the call. The parameter and the value recorded as dictated are read from the file, in
    /// the biologist's language, rather than taken from the phone: with the entry time the phone
    /// sends, the service refuses the call if the value changed since it was displayed, so what is
    /// recorded is what the biologist read out.
    /// </summary>
    private static async Task<IResult> RaiseAsync(
        Guid resultId, RaiseCriticalRequest request, HttpContext http, ResultEntryService entry,
        ValidationService validation, ICurrentUser user, CancellationToken ct)
    {
        if (ApiProblems.TooLong((request.ContactUsed, 100)) is { } tooLong)
            return ApiProblems.From(http, tooLong);

        // Told by telephone or in person: the two channels that end with the recipient reading the
        // value back, which is what closes the call.
        NotificationChannel? channel = request.Channel switch
        {
            null or "" or ApiProtocol.Channels.Telephone => NotificationChannel.Telephone,
            ApiProtocol.Channels.InPerson => NotificationChannel.InPerson,
            _ => null
        };
        if (channel is null)
            return ApiProblems.From(http, Error.Validation("Mobile_UnknownChannel", request.Channel));

        var opened = await entry.OpenAsync(request.OrderId, ct: ct);
        if (opened.IsFailure) return ApiProblems.From(http, opened);

        var row = opened.Value.Rows.FirstOrDefault(r => r.TestResultId == resultId);
        if (row is null) return ApiProblems.From(http, Error.NotFound("Result_NotFound", resultId));

        // Only a value nobody has been told about: one already read back must not be called again.
        if (!row.NeedsCriticalCommunication)
            return ApiProblems.From(http, Error.Conflict("Mobile_NoCriticalToCommunicate"));

        var parameter = row.ParameterName.For(user.Language);
        var value = $"{row.CurrentValue} {row.UnitSymbol}".Trim();

        // The number the phone dialled when it says so, else the one the dossier header offers for
        // a critical call; the call is recorded against the order's prescriber either way.
        var prescriber = opened.Value.Prescriber;
        var contactUsed = PrescriberContactRules.ContactUsed(request.ContactUsed, prescriber, channel.Value);

        var raised = await validation.RaiseCriticalNotificationAsync(
            resultId, parameter, value, channel.Value, contactUsed,
            prescriber?.PrescriberId, request.DisplayedEnteredAt, ct);

        if (raised.IsFailure) return ApiProblems.From(http, raised);

        return Results.Ok(new RaisedCritical(raised.Value, parameter, value));
    }

    /// <summary>Closes the call with the recipient's name and the value they read back, both required.</summary>
    private static async Task<IResult> AcknowledgeAsync(
        Guid notificationId, AcknowledgeCriticalRequest request, HttpContext http, ValidationService validation,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RecipientName))
            return ApiProblems.From(http, Error.Validation("Notification_RecipientNameRequired"));

        if (string.IsNullOrWhiteSpace(request.ReadBackValue))
            return ApiProblems.From(http, Error.Validation("Notification_ReadBackRequired"));

        if (ApiProblems.TooLong((request.RecipientName, 200), (request.RecipientRole, 100),
                                (request.ReadBackValue, 100)) is { } tooLong)
            return ApiProblems.From(http, tooLong);

        var acknowledged = await validation.AcknowledgeCriticalNotificationAsync(
            notificationId, request.RecipientName.Trim(),
            string.IsNullOrWhiteSpace(request.RecipientRole) ? null : request.RecipientRole.Trim(),
            readBackConfirmed: true, request.ReadBackValue.Trim(), ct);

        return acknowledged.IsFailure ? ApiProblems.From(http, acknowledged) : Results.NoContent();
    }

    /// <summary>The call could not be completed; the value stays unreleasable until another one is.</summary>
    private static async Task<IResult> UnreachableAsync(
        Guid notificationId, UnreachableRequest? request, HttpContext http, ValidationService validation,
        CancellationToken ct)
    {
        if (ApiProblems.TooLong((request?.Notes, 1000)) is { } tooLong)
            return ApiProblems.From(http, tooLong);

        var notes = string.IsNullOrWhiteSpace(request?.Notes)
            ? "Interlocuteur injoignable (validation à distance)"
            : request.Notes.Trim();

        var marked = await validation.MarkCriticalUnreachableAsync(notificationId, notes, ct);
        return marked.IsFailure ? ApiProblems.From(http, marked) : Results.NoContent();
    }
}
