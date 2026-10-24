using MarocLIS.Domain.Audit;

namespace MarocLIS.Application.Administration;

/// <summary>How a row of the audit trail stands out: the entries a review looks for first.</summary>
public enum AuditEmphasis
{
    None,

    /// <summary>A change that needed a reason, data leaving the laboratory, a privilege changed.</summary>
    Notable,

    /// <summary>A deletion of clinical data, a break-glass access, a refusal.</summary>
    Critical
}

/// <summary>The three tabs of « Journal d'audit », in their order.</summary>
public enum AuditTab
{
    Changes,
    Consultations,
    Security
}

/// <summary>
/// The rules of « Journal d'audit » that need no window: the filter the boxes come to, when the
/// consultations become one patient's complete history, how each row stands out, what a row shows
/// opened, and the count — said truncated when the ceiling cut it. The workstation's view and the
/// web's screen apply the same ones. Read-only, both: the tables refuse any change themselves.
/// </summary>
public static class AuditTrailRules
{
    /// <summary>The separator of the parts of a row opened, wide enough to read in a monospaced line.</summary>
    public const string DetailSeparator = "   ·   ";

    /// <summary>The filter of the boxes: the period, and the user and the text when typed.</summary>
    public static AuditTrailFilter Filter(DateOnly from, DateOnly to, string? userName, string? search) => new()
    {
        From = from,
        To = to,
        UserName = string.IsNullOrWhiteSpace(userName) ? null : userName.Trim(),
        Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim()
    };

    /// <summary>
    /// The file number typed, when there is one: it turns the consultation tab into a
    /// subject-access answer, scoped to that patient and complete rather than capped, because a
    /// right of access under loi n° 08-09 is not satisfied by the first five hundred rows.
    /// </summary>
    public static string? PatientRecordNumber(string? typed) =>
        string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();

    public static AuditEmphasis Emphasis(AuditEntryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        // A deletion of clinical data is the entry a review looks for first; a change that needed
        // a justification is a change somebody had to account for.
        if (IsDeletion(row)) return AuditEmphasis.Critical;
        return row.Reason is { Length: > 0 } ? AuditEmphasis.Notable : AuditEmphasis.None;
    }

    public static bool IsDeletion(AuditEntryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Operation is AuditOperation.SoftDelete or AuditOperation.Delete;
    }

    public static AuditEmphasis Emphasis(DataAccessRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.IsBreakGlass) return AuditEmphasis.Critical;

        // Data leaving the laboratory, as distinct from being looked at inside it.
        return row.Kind is DataAccessKind.Export or DataAccessKind.Transmit or DataAccessKind.SubjectAccessRequest
            ? AuditEmphasis.Notable
            : AuditEmphasis.None;
    }

    public static AuditEmphasis Emphasis(SecurityEventRow row) => UserAdministrationRules.Emphasis(row) switch
    {
        SecurityEventEmphasis.Failed => AuditEmphasis.Critical,
        SecurityEventEmphasis.Privilege => AuditEmphasis.Notable,
        _ => AuditEmphasis.None
    };

    /// <summary>« Result (LabOrder) »: the aggregate when it is another entity than the one changed.</summary>
    public static string Entity(AuditEntryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.AggregateName is { Length: > 0 } && row.AggregateName != row.EntityName
            ? $"{row.EntityName} ({row.AggregateName})"
            : row.EntityName;
    }

    /// <summary>
    /// The selected row's full payload — a change's before-and-after, a break-glass justification —
    /// too long for a cell and the whole point of opening the row.
    /// </summary>
    public static string Detail(AuditEntryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Join(row.Changes, row.Reason);
    }

    public static string Detail(DataAccessRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Join(row.QueryDescription, row.BreakGlassJustification);
    }

    public static string Detail(SecurityEventRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Join(row.Details, row.OsUser);
    }

    /// <summary>
    /// The count of a tab, and its key: said out loud when the ceiling cut it short. An audit
    /// extract that stops at five hundred rows while looking complete is the one failure of this
    /// screen that would mislead — somebody would conclude an access did not happen.
    /// </summary>
    public static (string Key, int Count, bool Truncated) Status(int count, bool truncated) =>
        (truncated ? "Audit.Truncated" : "Audit.RowCount", count, truncated);

    private static string Join(params string?[] parts) =>
        string.Join(DetailSeparator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
