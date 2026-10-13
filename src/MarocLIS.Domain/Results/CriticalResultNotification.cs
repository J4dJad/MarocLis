using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Results;

/// <summary>
/// The record of a critical result being communicated. ISO 15189 §5.9.1 requires the laboratory to
/// keep, for each panic value, who was informed, by whom, when, and — crucially — confirmation
/// that the recipient read the value back correctly. Without this record the notification did not
/// happen as far as an auditor is concerned, which is why validation of a critical result is
/// blocked until one exists.
/// </summary>
public sealed class CriticalResultNotification : AuditableAggregateRoot, ISiteScoped
{
    private CriticalResultNotification() { }

    public CriticalResultNotification(Guid testResultId, Guid labOrderId, Guid siteId,
                                      string reportedValue, DateTimeOffset attemptedAt,
                                      Guid notifiedByUserId)
    {
        TestResultId = testResultId;
        LabOrderId = labOrderId;
        SiteId = siteId;
        ReportedValue = reportedValue;
        AttemptedAt = attemptedAt;
        NotifiedByUserId = notifiedByUserId;
        Outcome = NotificationOutcome.Pending;
    }

    public Guid TestResultId { get; private set; }
    public Guid LabOrderId { get; private set; }
    public Guid SiteId { get; set; }

    /// <summary>The value as it was dictated, kept verbatim including its unit.</summary>
    public string ReportedValue { get; private set; } = string.Empty;

    /// <summary>Parameter name as dictated, so the record is readable without joining the catalogue.</summary>
    public string? ParameterName { get; set; }

    public DateTimeOffset AttemptedAt { get; private set; }

    /// <summary>The laboratory member who made the call.</summary>
    public Guid NotifiedByUserId { get; private set; }

    public NotificationChannel Channel { get; set; } = NotificationChannel.Telephone;

    /// <summary>Number or address actually used.</summary>
    public string? ContactUsed { get; set; }

    // ---- Recipient ---------------------------------------------------------------------------

    /// <summary>Set when the person reached is the registered prescriber.</summary>
    public Guid? PrescriberId { get; set; }

    /// <summary>Name of the person who actually took the call — often not the prescriber.</summary>
    public string? RecipientName { get; set; }

    /// <summary>Their role: médecin, infirmier de garde, secrétaire, patient.</summary>
    public string? RecipientRole { get; set; }

    // ---- Outcome -----------------------------------------------------------------------------

    public NotificationOutcome Outcome { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    /// Whether the recipient repeated the value back correctly. A notification without read-back
    /// counts as incomplete and keeps the escalation open.
    /// </summary>
    public bool ReadBackConfirmed { get; private set; }

    /// <summary>What the recipient read back, when it differed and had to be corrected.</summary>
    public string? ReadBackValue { get; set; }

    /// <summary>Attempt number — repeated failures escalate to the biologist on call.</summary>
    public int AttemptNumber { get; set; } = 1;

    /// <summary>Set when the call was escalated because the prescriber could not be reached.</summary>
    public Guid? EscalatedToUserId { get; set; }

    public string? Notes { get; set; }

    /// <summary>Delay between validation-readiness and successful communication — a quality indicator.</summary>
    public TimeSpan? CommunicationDelay =>
        CompletedAt is { } completed ? completed - AttemptedAt : null;

    public bool IsComplete => Outcome == NotificationOutcome.Acknowledged && ReadBackConfirmed;

    /// <summary>Records a successful communication with read-back.</summary>
    public Result Acknowledge(DateTimeOffset at, string recipientName, string? recipientRole,
                              bool readBackConfirmed, string? readBackValue)
    {
        if (string.IsNullOrWhiteSpace(recipientName))
            return Result.Failure(Error.Validation("Notification_RecipientNameRequired"));
        if (!readBackConfirmed)
            return Result.Failure(Error.Regulatory("Notification_ReadBackRequired"));

        RecipientName = recipientName;
        RecipientRole = recipientRole;
        ReadBackConfirmed = true;
        ReadBackValue = readBackValue;
        CompletedAt = at;
        Outcome = NotificationOutcome.Acknowledged;
        return Result.Success();
    }

    public void MarkUnreachable(DateTimeOffset at, string? notes)
    {
        Outcome = NotificationOutcome.Unreachable;
        CompletedAt = at;
        Notes = notes;
    }

    public void MarkRefused(DateTimeOffset at, string? notes)
    {
        Outcome = NotificationOutcome.Refused;
        CompletedAt = at;
        Notes = notes;
    }
}

public enum NotificationChannel
{
    Telephone = 0,
    /// <summary>Told in person at the laboratory.</summary>
    InPerson = 1,
    Sms = 2,
    Email = 3,
    /// <summary>Pushed into the prescriber's software over the integration interface.</summary>
    ElectronicInterface = 4,
    Fax = 5
}

public enum NotificationOutcome
{
    /// <summary>Raised but not yet communicated — appears on the critical-results worklist.</summary>
    Pending = 0,

    /// <summary>Communicated and read back.</summary>
    Acknowledged = 1,

    /// <summary>Nobody could be reached after the configured number of attempts.</summary>
    Unreachable = 2,

    /// <summary>The recipient declined to take the result.</summary>
    Refused = 3,

    /// <summary>Handed over to another laboratory member or to the biologist on call.</summary>
    Escalated = 4
}
