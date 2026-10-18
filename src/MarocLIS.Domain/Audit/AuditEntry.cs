using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Audit;

/// <summary>
/// One recorded change to a persisted entity. Written automatically by the EF Core interceptor, so
/// no use case can forget to audit. Append-only: the table has no update or delete path, and the
/// application's own SQL login is granted only INSERT on it.
/// <para>
/// This is the record that answers "who changed this result, when, and from what to what" — the
/// first question in any dispute, and an explicit ISO 15189 §5.8 requirement for a LIS.
/// </para>
/// </summary>
public sealed class AuditEntry : Entity
{
    private AuditEntry() { }

    public AuditEntry(string entityName, Guid entityId, AuditOperation operation,
                      DateTimeOffset occurredAt)
    {
        EntityName = entityName;
        EntityId = entityId;
        Operation = operation;
        OccurredAt = occurredAt;
    }

    /// <summary>CLR type name of the entity, e.g. "TestResult".</summary>
    public string EntityName { get; private set; } = string.Empty;

    public Guid EntityId { get; private set; }

    public AuditOperation Operation { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    // ---- Actor ---------------------------------------------------------------------------------

    public Guid? UserId { get; set; }

    /// <summary>User name captured at the time, so the entry stays readable if the account is renamed.</summary>
    public string? UserName { get; set; }

    /// <summary>Machine the change came from — how a misuse investigation starts.</summary>
    public string? Workstation { get; set; }

    /// <summary>Windows account the process ran under, which may differ from the LIS user.</summary>
    public string? OsUser { get; set; }

    public Guid? SiteId { get; set; }

    /// <summary>Correlates every entry written by one use case, so a save is reconstructable.</summary>
    public Guid? CorrelationId { get; set; }

    // ---- Payload -------------------------------------------------------------------------------

    /// <summary>
    /// Changed columns as JSON: <c>{"NumericValue":{"old":5.4,"new":14.2}}</c>. Only modified
    /// properties are stored, which keeps the table usable on a laboratory-sized database.
    /// </summary>
    public string? Changes { get; set; }

    /// <summary>Comma-separated names of the changed columns, for cheap filtering.</summary>
    public string? ChangedProperties { get; set; }

    /// <summary>Primary key of the aggregate the entity belongs to, for "show me this order's history".</summary>
    public Guid? AggregateId { get; set; }
    public string? AggregateName { get; set; }

    /// <summary>
    /// Business justification, when the operation required one — a result revision, a validation
    /// withdrawal, a discount, an override of an out-of-control QC.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>Patient the change concerned, so a data-subject access request can be answered.</summary>
    public Guid? PatientId { get; set; }
}

public enum AuditOperation
{
    Insert = 0,
    Update = 1,
    /// <summary>A logical delete; physical deletion of clinical data is not a code path that exists.</summary>
    SoftDelete = 2,
    /// <summary>Physical delete, reachable only by a maintenance routine on non-clinical tables.</summary>
    Delete = 3
}

/// <summary>
/// A record of personal health data being read. Loi n° 08-09 makes the laboratory responsible for
/// the confidentiality of the files it holds, and the CNDP expects a controller to be able to show
/// who consulted a given patient's data. Change logging alone cannot show that: reading a file
/// leaves no trace on the file itself, so consultation is logged explicitly.
/// </summary>
public sealed class DataAccessLog : Entity
{
    private DataAccessLog() { }

    public DataAccessLog(Guid userId, DataAccessKind kind, DateTimeOffset occurredAt)
    {
        UserId = userId;
        Kind = kind;
        OccurredAt = occurredAt;
    }

    public Guid UserId { get; private set; }
    public string? UserName { get; set; }
    public DataAccessKind Kind { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    public Guid? PatientId { get; set; }
    public Guid? LabOrderId { get; set; }
    public Guid? LabReportId { get; set; }

    /// <summary>Number of records returned, which is what distinguishes a lookup from an extraction.</summary>
    public int? RecordCount { get; set; }

    /// <summary>The search terms used, for a list access. Never stores the results themselves.</summary>
    public string? QueryDescription { get; set; }

    /// <summary>Screen or report the access came from.</summary>
    public string? Context { get; set; }

    public string? Workstation { get; set; }
    public Guid? SiteId { get; set; }

    /// <summary>
    /// Set when the user had no care relationship with the patient — a break-glass access. These
    /// are the rows a confidentiality review actually looks at.
    /// </summary>
    public bool IsBreakGlass { get; set; }
    public string? BreakGlassJustification { get; set; }
}

public enum DataAccessKind
{
    /// <summary>Opened a patient file.</summary>
    ViewPatient = 0,
    /// <summary>Opened an order or its results.</summary>
    ViewOrder = 1,
    /// <summary>Viewed or reprinted a report.</summary>
    ViewReport = 2,
    /// <summary>Printed a document containing personal data.</summary>
    Print = 3,
    /// <summary>Exported data to a file.</summary>
    Export = 4,
    /// <summary>Ran a search returning a list of patients.</summary>
    Search = 5,
    /// <summary>Sent a report or result outside the laboratory.</summary>
    Transmit = 6,
    /// <summary>Answered a data-subject access request.</summary>
    SubjectAccessRequest = 7
}

/// <summary>
/// A security-relevant event on the application itself: logins, lockouts, permission changes.
/// Separate from <see cref="AuditEntry"/> because a failed login has no entity to attach to, and
/// because these rows are what an administrator watches day to day.
/// </summary>
public sealed class SecurityEvent : Entity
{
    private SecurityEvent() { }

    public SecurityEvent(SecurityEventKind kind, DateTimeOffset occurredAt, bool succeeded)
    {
        Kind = kind;
        OccurredAt = occurredAt;
        Succeeded = succeeded;
    }

    public SecurityEventKind Kind { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public bool Succeeded { get; private set; }

    public Guid? UserId { get; set; }

    /// <summary>The name that was typed — kept even when no such account exists.</summary>
    public string? AttemptedUserName { get; set; }

    public string? Workstation { get; set; }
    public string? OsUser { get; set; }
    public Guid? SiteId { get; set; }

    /// <summary>Resource key describing the failure, e.g. "Auth_InvalidCredentials".</summary>
    public string? FailureReasonKey { get; set; }

    /// <summary>The user a privilege change was applied to, when different from the actor.</summary>
    public Guid? TargetUserId { get; set; }
    public string? Details { get; set; }
}

public enum SecurityEventKind
{
    LoginSucceeded = 0,
    LoginFailed = 1,
    Logout = 2,
    AccountLockedOut = 3,
    AccountUnlocked = 4,
    PasswordChanged = 5,
    PasswordReset = 6,
    RoleAssigned = 7,
    RoleRemoved = 8,
    PermissionsChanged = 9,
    UserCreated = 10,
    UserDeactivated = 11,
    /// <summary>An operation was refused for lack of permission.</summary>
    AuthorizationDenied = 12,
    /// <summary>A session expired through inactivity.</summary>
    SessionTimeout = 13,
    /// <summary>Database backup or restore.</summary>
    BackupOperation = 14,
    /// <summary>A setting affecting security or traceability was changed.</summary>
    SecuritySettingChanged = 15,

    /// <summary>
    /// Retained data was deleted under a retention policy.
    /// <para>
    /// A security event rather than an audit entry because the operations it covers are bulk: a
    /// retention purge removes months of rows, and one audit entry per row would cost more storage
    /// than the purge reclaims. The event carries the operator, the cutoff, the count and the stated
    /// reason, which is what an auditor actually asks for.
    /// </para>
    /// </summary>
    DataPurged = 16,

    /// <summary>A biologist's handwritten signature, printed on released reports, was set or removed.</summary>
    SignatureChanged = 17
}
