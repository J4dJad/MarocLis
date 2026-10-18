using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Licensing;

/// <summary>
/// A licence imported into the database, the current one and those it replaced.
/// <para>
/// In the database rather than on each workstation, so every workstation and the API read the same
/// licence and an import reaches them all. The file's text is kept whole and verified at every
/// reading: a row edited by hand in SQL no longer matches its signature and is refused like an
/// altered file. The other columns are copies for the lists, never trusted.
/// </para>
/// </summary>
public sealed class LicenceRecord : Entity
{
    private LicenceRecord() { }

    public LicenceRecord(string number, int version, LicenceKind kind, string content, DateTimeOffset importedAt)
    {
        Number = number;
        Version = version;
        Kind = kind;
        Content = content;
        ImportedAt = importedAt;
    }

    public const int NumberMaxLength = 60;

    public string Number { get; private set; } = string.Empty;
    public int Version { get; private set; }
    public LicenceKind Kind { get; private set; }

    /// <summary>The licence file as imported, signature included.</summary>
    public string Content { get; private set; } = string.Empty;

    public DateTimeOffset ImportedAt { get; private set; }
    public Guid? ImportedByUserId { get; set; }
    public string? ImportedByUserName { get; set; }
    public string? Workstation { get; set; }

    /// <summary>When another licence took its place; null for the current one.</summary>
    public DateTimeOffset? ReplacedAt { get; private set; }

    public void Replace(DateTimeOffset at) => ReplacedAt ??= at;
}

/// <summary>
/// A workstation signed in to MarocLIS, as seen through its heartbeat.
/// <para>
/// A workstation is a computer, not a person: two operators on one PC take one place. Each signs
/// its presence every <see cref="HeartbeatInterval"/>; one silent for <see cref="SilenceTimeout"/>
/// gives its place up, so a PC switched off without signing out does not hold it for ever. The
/// phones go through the API and are never counted.
/// </para>
/// </summary>
public sealed class Workstation : Entity
{
    private Workstation() { }

    public Workstation(string machineName, DateTimeOffset seenAt)
    {
        MachineName = machineName;
        FirstSeenAt = seenAt;
        LastSeenAt = seenAt;
    }

    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SilenceTimeout = TimeSpan.FromMinutes(15);

    public const int MachineNameMaxLength = 64;

    public string MachineName { get; private set; } = string.Empty;
    public DateTimeOffset FirstSeenAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>The operator last signed in on it, for the list the extra workstation is shown.</summary>
    public string? LastUserName { get; set; }

    public string? AppVersion { get; set; }

    /// <summary>Signed out: the place is free at once rather than after the silence.</summary>
    public DateTimeOffset? ReleasedAt { get; private set; }

    public bool IsActive(DateTimeOffset now) => ReleasedAt is null && now - LastSeenAt < SilenceTimeout;

    public void Seen(DateTimeOffset at, string? userName)
    {
        // A first sign-in after a release or a silence starts a new presence.
        if (ReleasedAt is not null || at - LastSeenAt >= SilenceTimeout) FirstSeenAt = at;

        LastSeenAt = at;
        ReleasedAt = null;
        if (userName is not null) LastUserName = userName;
    }

    public void Release(DateTimeOffset at) => ReleasedAt = at;
}

/// <summary>A workstation in the list shown when the limit is reached.</summary>
public sealed record ActiveWorkstation(string MachineName, string? UserName, DateTimeOffset Since, DateTimeOffset LastSeenAt);

/// <summary>The answer to a workstation asking for a place.</summary>
public sealed record WorkstationClaim(bool Granted, int InUse, int? Limit, IReadOnlyList<ActiveWorkstation> Active)
{
    /// <summary>
    /// Decides on the rows as they are: a workstation already counted keeps its place, another one
    /// gets a place while fewer than <paramref name="limit"/> others are active.
    /// </summary>
    public static WorkstationClaim Decide(IEnumerable<Workstation> rows, string machineName, DateTimeOffset now, int? limit)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var active = rows.Where(w => w.IsActive(now)).ToList();
        var others = active.Where(w => !string.Equals(w.MachineName, machineName, StringComparison.OrdinalIgnoreCase)).ToList();

        var granted = limit is null || others.Count < limit;
        var listed = active.OrderBy(w => w.FirstSeenAt)
                           .Select(w => new ActiveWorkstation(w.MachineName, w.LastUserName, w.FirstSeenAt, w.LastSeenAt))
                           .ToList();

        return new WorkstationClaim(granted, granted ? others.Count + 1 : others.Count, limit, listed);
    }
}
