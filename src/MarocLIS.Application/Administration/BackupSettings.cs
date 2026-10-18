using System.Globalization;
using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Administration;

/// <summary>
/// The backup schedule, held as installation settings (<c>AppSetting</c>, installation-wide) so it is
/// changed from any administrator's workstation in Paramètres › Sauvegardes.
/// <para>
/// SQL Server Express has no SQL Agent, so the backup itself is run by a Windows scheduled task on
/// the server (<c>scripts/maroclis-backup.ps1</c>, every 15 minutes, as SYSTEM). The task reads these
/// keys at each pass and backs up only when a slot is due: a workstation never needs the right to
/// edit a Windows scheduled task, and the task never needs a copy of the settings of its own. The
/// script reads the same keys with the same rules; a change to one side is a change to both.
/// </para>
/// <para>
/// A key that is absent keeps its default. The installer's values (wizard page « Sauvegardes ») are
/// written by the task the first time it finds the keys missing, never over an existing value, so an
/// upgrade keeps what the laboratory chose.
/// </para>
/// </summary>
public sealed record BackupSettings
{
    public const string Prefix = "Backup.";
    public const string EnabledKey = Prefix + "Enabled";
    public const string FolderKey = Prefix + "Folder";
    public const string RetentionDaysKey = Prefix + "RetentionDays";
    public const string TimesKey = Prefix + "Times";
    public const string DaysKey = Prefix + "Days";

    public const string DefaultFolder = @"C:\MarocLIS\Sauvegardes";
    public const int DefaultRetentionDays = 14;
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 365;

    /// <summary>Several backups a day are allowed; more than this is a log-shipping job, not this one.</summary>
    public const int MaxTimesPerDay = 6;

    /// <summary>The longest folder accepted: the file name and SQL Server's 260-character paths need the rest.</summary>
    public const int MaxFolderLength = 200;

    public static readonly TimeOnly DefaultTime = new(22, 0);

    public static BackupSettings Default { get; } = new();

    /// <summary>
    /// On by default: an installation that never opened the tab is backed up every evening, which is
    /// the reason the feature exists. Switching it off stops the scheduled backups only;
    /// « Sauvegarder maintenant » still works.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// A folder <em>on the server</em>, as SQL Server sees it: a local path (<c>D:\Sauvegardes</c>) or a
    /// share (<c>\\nas\maroclis</c>). Never a mapped drive letter: a service has none.
    /// </summary>
    public string Folder { get; init; } = DefaultFolder;

    public int RetentionDays { get; init; } = DefaultRetentionDays;

    /// <summary>The times of day, server time, in order; never empty.</summary>
    public IReadOnlyList<TimeOnly> Times { get; init; } = [DefaultTime];

    /// <summary>The days of the week a backup runs; empty means every day.</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];

    public bool RunsOn(DayOfWeek day) => Days.Count == 0 || Days.Contains(day);

    public static BackupSettings FromValues(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        string? Value(string key) => values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        var settings = Default;

        if (Value(EnabledKey) is { } enabled && bool.TryParse(enabled, out var on))
            settings = settings with { Enabled = on };

        if (Value(FolderKey) is { } folder)
            settings = settings with { Folder = folder };

        if (Value(RetentionDaysKey) is { } days
            && int.TryParse(days, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retention)
            && retention is >= MinRetentionDays and <= MaxRetentionDays)
            settings = settings with { RetentionDays = retention };

        // A time or a day typed wrongly by hand in the database is ignored rather than fatal: the task
        // keeps backing up at the times it can read, which is the safer reading of a damaged setting.
        if (Value(TimesKey) is { } times && ParseTimes(times) is { Count: > 0 } parsed)
            settings = settings with { Times = parsed };

        if (Value(DaysKey) is { } named)
            settings = settings with { Days = ParseDays(named) };

        return settings;
    }

    public IReadOnlyList<(string Key, string Value)> ToValues() =>
    [
        (EnabledKey, Enabled ? "true" : "false"),
        (FolderKey, Folder),
        (RetentionDaysKey, RetentionDays.ToString(CultureInfo.InvariantCulture)),
        (TimesKey, FormatTimes(Times)),
        (DaysKey, string.Join(",", Days.Order()))
    ];

    /// <summary>"22:00" or "08:00,13:00": the stored form, which the script parses as well.</summary>
    public static string FormatTimes(IEnumerable<TimeOnly> times) =>
        string.Join(",", times.Order().Select(t => t.ToString("HH\\:mm", CultureInfo.InvariantCulture)));

    /// <summary>The times in <paramref name="text"/>, in order and without repeats; unreadable ones dropped.</summary>
    public static IReadOnlyList<TimeOnly> ParseTimes(string text) =>
    [
        .. text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Select(t => TimeOnly.TryParseExact(t, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture,
                                                   DateTimeStyles.None, out var time)
                   ? time
                   : (TimeOnly?)null)
               .OfType<TimeOnly>()
               // Minutes only: the task passes every 15 minutes and a backup "at 22:00:30" is 22:00.
               .Select(t => new TimeOnly(t.Hour, t.Minute))
               .Distinct()
               .Order()
    ];

    /// <summary>English day names ("Monday,Friday"), the form PowerShell's [DayOfWeek] reads as well.</summary>
    public static IReadOnlyList<DayOfWeek> ParseDays(string text) =>
    [
        .. text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Select(d => Enum.TryParse<DayOfWeek>(d, ignoreCase: true, out var day) && Enum.IsDefined(day)
                   && !int.TryParse(d, out _)
                   ? day
                   : (DayOfWeek?)null)
               .OfType<DayOfWeek>()
               .Distinct()
               .Order()
    ];

    /// <summary>What the laboratory may store: a usable folder, a retention, at least one time and day.</summary>
    public Result Validate()
    {
        var errors = new List<Error>();

        if (!IsAcceptableFolder(Folder))
            errors.Add(Error.Validation("Backup_FolderInvalid", Folder));

        if (RetentionDays is < MinRetentionDays or > MaxRetentionDays)
            errors.Add(Error.Validation("Backup_RetentionInvalid", MinRetentionDays, MaxRetentionDays));

        if (Times.Count == 0)
            errors.Add(Error.Validation("Backup_TimesRequired"));
        else if (Times.Count > MaxTimesPerDay)
            errors.Add(Error.Validation("Backup_TooManyTimes", MaxTimesPerDay));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }

    /// <summary>
    /// An absolute folder on a drive (<c>C:\…</c>) or a share (<c>\\server\share…</c>), or an absolute
    /// path on a Linux SQL Server. A relative path would be resolved against SQL Server's own folder,
    /// which is never what was meant.
    /// </summary>
    public static bool IsAcceptableFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;

        var path = folder.Trim();
        if (path.Length > MaxFolderLength) return false;

        // Characters Windows refuses in a path, and the quote, which no folder needs.
        if (path.IndexOfAny(['<', '>', '"', '|', '?', '*', '\'', '\0', '\r', '\n', '\t']) >= 0) return false;

        var drive = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';
        var share = path.StartsWith(@"\\", StringComparison.Ordinal) && path.TrimStart('\\').Contains('\\')
                    && !path.StartsWith(@"\\?\", StringComparison.Ordinal);
        var unix = path.StartsWith('/') && !path.Contains('\\');

        // A colon anywhere but after the drive letter (an alternate data stream) is not a folder.
        if (path.IndexOf(':', drive ? 2 : 0) >= 0) return false;

        return drive || share || unix;
    }

    // By value, not by list instance: two readings of the same settings are equal.
    public bool Equals(BackupSettings? other) =>
        other is not null && Enabled == other.Enabled
        && string.Equals(Folder, other.Folder, StringComparison.OrdinalIgnoreCase)
        && RetentionDays == other.RetentionDays && Times.SequenceEqual(other.Times) && Days.SequenceEqual(other.Days);

    public override int GetHashCode() =>
        HashCode.Combine(Enabled, Folder.ToUpperInvariant(), RetentionDays, FormatTimes(Times), string.Join(",", Days));
}

/// <summary>
/// When a scheduled backup is due. The script on the server applies the same rule
/// (<c>Get-DueSlot</c> in <c>scripts/maroclis-backup.ps1</c>); the screen uses it to show the next run.
/// <para>
/// Times are the server's local clock. A slot is a day and a time of the schedule; the task records
/// the last slot it handled, so the passes every 15 minutes after a slot do not back up again.
/// </para>
/// </summary>
public static class BackupSchedule
{
    /// <summary>
    /// How late a missed slot is still caught up: a server switched off at 22:00 and on again at
    /// 08:00 backs up at 08:00. A slot older than this is skipped rather than run days late, and one
    /// backup covers every slot missed.
    /// </summary>
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(24);

    /// <summary>The slots of <paramref name="settings"/> in (<paramref name="after"/>, <paramref name="until"/>], in order.</summary>
    public static IEnumerable<DateTime> SlotsBetween(BackupSettings settings, DateTime after, DateTime until)
    {
        ArgumentNullException.ThrowIfNull(settings);

        for (var day = after.Date; day <= until.Date; day = day.AddDays(1))
        {
            if (!settings.RunsOn(day.DayOfWeek)) continue;

            foreach (var time in settings.Times)
            {
                var slot = day + time.ToTimeSpan();
                if (slot > after && slot <= until) yield return slot;
            }
        }
    }

    /// <summary>
    /// The slot to back up for now, or null: the latest slot of the catch-up window that is later
    /// than the last one handled. Nothing is due while the schedule is switched off.
    /// </summary>
    public static DateTime? DueSlot(BackupSettings settings, DateTime? lastHandled, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled) return null;

        var from = now - CatchUpWindow;
        if (lastHandled is { } last && last > from) from = last;

        var slots = SlotsBetween(settings, from, now).ToList();
        return slots.Count == 0 ? null : slots[^1];
    }

    /// <summary>The next slot after <paramref name="now"/>, or null when switched off.</summary>
    public static DateTime? NextRun(BackupSettings settings, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled) return null;

        // Eight days always holds the next slot of a schedule with at least one day and one time.
        return SlotsBetween(settings, now, now.AddDays(8)).Cast<DateTime?>().FirstOrDefault();
    }
}

/// <summary>Who or what took a backup, carried in the backup set's name so SQL Server's history keeps it.</summary>
public enum BackupKind
{
    /// <summary>A backup MarocLIS did not write: SQL Server Management Studio, another tool.</summary>
    Other = 0,
    Scheduled = 1,
    Manual = 2,
    /// <summary>The safety backup taken automatically just before a restore.</summary>
    BeforeRestore = 3
}

/// <summary>
/// The names of the backup files and backup sets. Shared with the script, which writes the scheduled
/// ones: <c>MarocLIS_20261018_220000.bak</c>, and a suffix for the others so they read apart in a
/// folder listing. Retention deletes only files of this form.
/// </summary>
public static class BackupNaming
{
    public const string Extension = ".bak";

    private const string SetPrefix = "MarocLIS:";

    public static string SetName(BackupKind kind) => SetPrefix + kind switch
    {
        BackupKind.Scheduled => "scheduled",
        BackupKind.Manual => "manual",
        BackupKind.BeforeRestore => "pre-restore",
        _ => "other"
    };

    public static BackupKind KindOf(string? setName) => setName switch
    {
        SetPrefix + "scheduled" => BackupKind.Scheduled,
        SetPrefix + "manual" => BackupKind.Manual,
        SetPrefix + "pre-restore" => BackupKind.BeforeRestore,
        _ => BackupKind.Other
    };

    public static string FileName(string database, BackupKind kind, DateTime at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);

        var suffix = kind switch
        {
            BackupKind.Manual => "_manuelle",
            BackupKind.BeforeRestore => "_avant-restauration",
            _ => string.Empty
        };

        return $"{SafeName(database)}_{at.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}{suffix}{Extension}";
    }

    // The database name goes into a file name: anything a file name cannot hold becomes "_".
    private static string SafeName(string database) =>
        new([.. database.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);

    /// <summary>
    /// The folder and the file, with the folder's own separator: the path is SQL Server's, on the
    /// server, and the workstation building it may not use the same one.
    /// </summary>
    public static string Combine(string folder, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var separator = folder.Contains('/') && !folder.Contains('\\') ? '/' : '\\';
        return folder.Trim().TrimEnd('\\', '/') + separator + fileName;
    }

    /// <summary>The file name of a path written with either separator.</summary>
    public static string FileOf(string path) =>
        path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    /// <summary>The folder of a path written with either separator, without the trailing separator.</summary>
    public static string FolderOf(string path)
    {
        var at = path.LastIndexOfAny(['\\', '/']);
        return at <= 0 ? string.Empty : path[..at];
    }

    /// <summary>Whether <paramref name="fileName"/> is one MarocLIS wrote for <paramref name="database"/>.</summary>
    public static bool IsOwnFile(string database, string fileName)
    {
        return fileName.StartsWith(SafeName(database) + "_", StringComparison.OrdinalIgnoreCase)
               && fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
               && TakenAt(database, fileName) is not null;
    }

    /// <summary>The time written in a file name of ours, or null.</summary>
    public static DateTime? TakenAt(string database, string fileName)
    {
        var prefix = SafeName(database).Length + 1;
        var stamp = fileName.Length >= prefix + 15 ? fileName.Substring(prefix, 15) : string.Empty;
        return DateTime.TryParseExact(stamp, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None,
                                      out var at)
            ? at
            : null;
    }
}

/// <summary>
/// Which backup files the retention deletes: those older than the retention, except the most recent
/// one that verified. A schedule that has been failing for longer than the retention must not end
/// with the last good backup deleted by the very task that failed to replace it. The script applies
/// the same rule.
/// </summary>
public static class BackupRetention
{
    public static IReadOnlyList<T> Expired<T>(IEnumerable<T> backups, Func<T, DateTime> takenAt, Func<T, bool> verified,
                                              DateTime now, int retentionDays)
    {
        ArgumentNullException.ThrowIfNull(backups);

        var all = backups.ToList();
        var cutoff = now.AddDays(-retentionDays);
        var keep = all.Where(verified).OrderByDescending(takenAt).Take(1).ToHashSet();

        return [.. all.Where(b => takenAt(b) < cutoff && !keep.Contains(b)).OrderBy(takenAt)];
    }
}

/// <summary>The outcome of the verification of one backup file (« RESTORE VERIFYONLY »).</summary>
public enum BackupCheckStatus
{
    /// <summary>Nothing recorded: a backup taken by another tool, or before MarocLIS kept the record.</summary>
    Unknown = 0,
    Verified = 1,
    Failed = 2,
    /// <summary>
    /// Written from a workstation, whose login may back up but not verify (that needs CREATE
    /// DATABASE): the task on the server verifies it at its next pass.
    /// </summary>
    Pending = 3,
    /// <summary>Deleted by the retention.</summary>
    Deleted = 4
}

/// <summary>
/// The record of a backup file's verification, one setting per file (<c>Backup.Check.&lt;file&gt;</c>):
/// SQL Server's own history keeps the backup but not whether it was verified, and only the server
/// can read the folder.
/// </summary>
public sealed record BackupCheck(BackupCheckStatus Status, DateTimeOffset? At, string? Message)
{
    public const string Prefix = BackupSettings.Prefix + "Check.";

    public static string KeyFor(string fileName) => Prefix + fileName;

    public static BackupCheck Unknown { get; } = new(BackupCheckStatus.Unknown, null, null);

    /// <summary>"ok;2026-10-18T22:01:05+01:00", "failed;…;message", "pending;…", "deleted;…".</summary>
    public string Format() =>
        string.Join(";", new[]
        {
            Status switch
            {
                BackupCheckStatus.Verified => "ok",
                BackupCheckStatus.Failed => "failed",
                BackupCheckStatus.Pending => "pending",
                BackupCheckStatus.Deleted => "deleted",
                _ => "unknown"
            },
            At?.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture) ?? string.Empty
        }.Concat(string.IsNullOrEmpty(Message) ? [] : [Message.Replace(';', ',')]));

    public static BackupCheck Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Unknown;

        var parts = value.Split(';', 3);
        var status = parts[0].Trim().ToLowerInvariant() switch
        {
            "ok" => BackupCheckStatus.Verified,
            "failed" => BackupCheckStatus.Failed,
            "pending" => BackupCheckStatus.Pending,
            "deleted" => BackupCheckStatus.Deleted,
            _ => BackupCheckStatus.Unknown
        };
        DateTimeOffset? at = parts.Length > 1 && DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture,
                                                                         DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
        return new BackupCheck(status, at, parts.Length > 2 ? parts[2] : null);
    }
}

/// <summary>
/// What the task on the server and the backups taken from a workstation last recorded
/// (<c>Backup.State.*</c>): written outside the audit trail, because a heartbeat every 15 minutes
/// is not a change anybody made.
/// </summary>
public sealed record BackupState
{
    public const string Prefix = BackupSettings.Prefix + "State.";
    public const string AgentSeenAtKey = Prefix + "AgentSeenAt";
    public const string AgentHostKey = Prefix + "AgentHost";
    public const string LastSlotKey = Prefix + "LastSlot";
    public const string LastSuccessAtKey = Prefix + "LastSuccessAt";
    public const string LastResultKey = Prefix + "LastResult";

    /// <summary>The task passes every 15 minutes; not seen for an hour, it is not running.</summary>
    public static readonly TimeSpan AgentSilentAfter = TimeSpan.FromHours(1);

    public DateTimeOffset? AgentSeenAt { get; init; }
    public string? AgentHost { get; init; }
    public DateTime? LastSlot { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }

    /// <summary>The last backup attempted, by the task or a workstation: "ok" or "failed", when, and the file or the error.</summary>
    public BackupCheck? LastResult { get; init; }

    public static BackupState FromValues(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        string? Value(string key) => values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        static DateTimeOffset? Moment(string? text) =>
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;

        return new BackupState
        {
            AgentSeenAt = Moment(Value(AgentSeenAtKey)),
            AgentHost = Value(AgentHostKey),
            LastSlot = DateTime.TryParseExact(Value(LastSlotKey), "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture,
                                              DateTimeStyles.None, out var slot)
                ? slot
                : null,
            LastSuccessAt = Moment(Value(LastSuccessAtKey)),
            LastResult = Value(LastResultKey) is { } result ? BackupCheck.Parse(result) : null
        };
    }

    public static string FormatMoment(DateTimeOffset at) =>
        at.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
}
