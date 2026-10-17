namespace MarocLIS.Api.Contracts;

/// <summary>
/// Opens the record of a telephone call about a critical value. The value and the parameter are
/// read by the server from the file, so what is recorded as dictated is what the file holds.
/// </summary>
/// <param name="OrderId">The file the result belongs to.</param>
/// <param name="DisplayedEnteredAt">The row's <c>enteredAt</c> as displayed: a value stored since refuses the call.</param>
/// <param name="ContactUsed">The number or the person called, when the biologist notes it.</param>
public sealed record RaiseCriticalRequest(Guid OrderId, DateTimeOffset? DisplayedEnteredAt, string? ContactUsed = null);

public sealed record RaisedCritical(Guid NotificationId, string ParameterName, string Value);

/// <summary>Closes a call: who took it, and the value they read back. Without the read-back it stays open.</summary>
public sealed record AcknowledgeCriticalRequest(string RecipientName, string? RecipientRole, string ReadBackValue);

/// <summary>The call could not be made, or was abandoned; the value stays unreleasable.</summary>
public sealed record UnreachableRequest(string? Notes);

/// <summary>A report version just issued.</summary>
/// <param name="Partial">Results are still to come; the report says so on its face.</param>
/// <param name="PageCount">Pages of the preview, fetched one by one as PNG images.</param>
public sealed record ReleasedReportResponse(Guid ReportId, int Version, bool Partial, int PageCount);
