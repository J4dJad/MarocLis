using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Administration;

/// <summary>How a backup's check reads in its pill.</summary>
public enum BackupCheckTone
{
    Waiting,
    Running,
    Ok,
    Critical
}

/// <summary>What « Sauvegardes » offers while nothing runs: the backup, the schedule saved, the folder tried, the restore of a sound backup.</summary>
public sealed record BackupActions(bool BackupNow, bool Save, bool TestFolder, bool Restore);

/// <summary>A sentence of the tab: its key and what it is formatted with.</summary>
public sealed record BackupLine(string Key, params object?[] Arguments);

/// <summary>
/// The rules of « Paramètres › Sauvegardes » that need no window: the week as the boxes list it,
/// the schedule typed, the banner of the last backup's age, the scheduled task's line, the next
/// run and the last attempt, each backup's origin, size and check, and what may be done while
/// nothing runs. The workstation's tab and the web's apply the same ones; the backups themselves,
/// the folder test and the restore are BackupService's, which records each.
/// <para>
/// The restore is the server's: <see cref="BackupService.CanRestoreAsync"/> refuses it from
/// anywhere but the server machine, by a Windows administrator of it, and a browser is neither.
/// </para>
/// </summary>
public static class BackupScreenRules
{
    /// <summary>Monday first, as a French calendar is printed; the stored names are English (BackupSettings).</summary>
    public static IReadOnlyList<DayOfWeek> WeekDays { get; } =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday,
        DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    /// <summary>
    /// The schedule typed, or what is wrong with it. Every piece typed must be a time — « 22:00,
    /// 2h » would otherwise quietly drop the second — and a day at least is ticked; every day
    /// ticked is stored as « every day », which a day added to the week would keep.
    /// </summary>
    public static Result<BackupSettings> Read(bool enabled, string? timesText, IEnumerable<DayOfWeek> days, int retentionDays,
                                              string? folder)
    {
        ArgumentNullException.ThrowIfNull(days);

        var text = timesText ?? string.Empty;
        var typed = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var times = BackupSettings.ParseTimes(text);

        if (typed.Length == 0 || times.Count != typed.Distinct().Count())
            return Result.Failure<BackupSettings>(Error.Validation("Backup_TimesInvalid", text));

        var ticked = days.Distinct().ToList();
        if (ticked.Count == 0) return Result.Failure<BackupSettings>(Error.Validation("Backup_DaysRequired"));

        return Result.Success(new BackupSettings
        {
            Enabled = enabled,
            Times = times,
            Days = ticked.Count == 7 ? [] : ticked,
            RetentionDays = retentionDays,
            Folder = (folder ?? string.Empty).Trim()
        });
    }

    /// <summary>The retention as the box holds it: within what the schedule accepts.</summary>
    public static int Retention(int days) => Math.Clamp(days, BackupSettings.MinRetentionDays, BackupSettings.MaxRetentionDays);

    /// <summary>The times as their box shows them: « 13:00, 22:00 ».</summary>
    public static string TimesText(BackupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return string.Join(", ", settings.Times.Select(t => t.ToString("HH\\:mm", System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>A folder SQL Server cannot write to yet may be saved anyway: the server's task creates a missing local folder and opens it.</summary>
    public static bool MaySaveAnyway(Result saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return saved.IsFailure && saved.FirstError.ResourceKey == "Backup_FolderNotWritable";
    }

    /// <summary>
    /// The banner, the age of the last backup first — it is what the tab is opened for: none at
    /// all, or older than 48 hours, in the critical colours. The moment is the server's.
    /// </summary>
    public static (bool Critical, BackupLine Line) Banner(BackupOverview overview)
    {
        ArgumentNullException.ThrowIfNull(overview);

        if (overview.LastSuccessAt is not { } last) return (true, new BackupLine("Backup.NoneYet"));

        var age = Age(overview.ServerNow - last);
        return overview.IsStale
            ? (true, new BackupLine("Backup.StaleBanner", last, age))
            : (false, new BackupLine("Backup.LastSuccess", last, age));
    }

    /// <summary>An age in minutes under an hour and a half, in hours under two days, in days beyond.</summary>
    public static BackupLine Age(TimeSpan age) =>
        age.TotalMinutes < 90 ? new BackupLine("Backup.AgeMinutes", Math.Max(0, (int)age.TotalMinutes))
        : age.TotalHours < 48 ? new BackupLine("Backup.AgeHours", (int)age.TotalHours)
        : new BackupLine("Backup.AgeDays", (int)age.TotalDays);

    /// <summary>The server's task: running, silent since its last passage, or never seen. Only the first is not a warning.</summary>
    public static (bool Warning, BackupLine Line) Agent(BackupOverview overview)
    {
        ArgumentNullException.ThrowIfNull(overview);

        var host = overview.State.AgentHost ?? overview.Server.ServerMachine;
        return overview.Agent switch
        {
            BackupAgentStatus.Running => (false, new BackupLine("Backup.AgentRunning", host, overview.State.AgentSeenAt!.Value.LocalDateTime)),
            BackupAgentStatus.Silent => (true, new BackupLine("Backup.AgentSilent", host, overview.State.AgentSeenAt!.Value.LocalDateTime)),
            _ => (true, new BackupLine("Backup.AgentNeverSeen", overview.Server.ServerMachine))
        };
    }

    /// <summary>The next automatic backup, or the schedule said off (a warning).</summary>
    public static (bool Warning, BackupLine Line) Next(BackupOverview overview)
    {
        ArgumentNullException.ThrowIfNull(overview);
        return overview.NextRunAt is { } next
            ? (false, new BackupLine("Backup.NextRun", next))
            : (true, new BackupLine("Backup.ScheduleOff"));
    }

    /// <summary>
    /// The last attempt, by the task or a workstation, when one is known: failed with its reason —
    /// a resource key naming the schedule's folder — or done with its file.
    /// </summary>
    public static (bool Failed, DateTime? At, string? Message)? LastResult(BackupOverview overview)
    {
        ArgumentNullException.ThrowIfNull(overview);

        if (overview.State.LastResult is not { Status: not BackupCheckStatus.Unknown } result) return null;
        return (result.Status == BackupCheckStatus.Failed, result.At?.LocalDateTime, result.Message);
    }

    /// <summary>The failure recorded as a resource key, as an error naming the schedule's folder.</summary>
    public static Error? Reason(string? key, string? folder) =>
        string.IsNullOrWhiteSpace(key) ? null : Error.Conflict(key, folder ?? string.Empty, string.Empty);

    /// <summary>
    /// Who took a backup: the server for a scheduled one, SQL Server's login for one MarocLIS did
    /// not write, the description we wrote for a manual one.
    /// </summary>
    public static (string KindKey, string? Who) Origin(BackupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var who = entry.Kind switch
        {
            BackupKind.Scheduled => entry.Set.MachineName,
            BackupKind.Other => entry.Set.UserName,
            _ => entry.Set.Description
        };
        return ($"BackupKind.{entry.Kind}", string.IsNullOrWhiteSpace(who) ? null : who);
    }

    /// <summary>A file's size, in gigabytes from one, in megabytes (one at least) below.</summary>
    public static (string Key, double Value, string Format) Size(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? ("Backup.SizeGb", bytes / (1024d * 1024 * 1024), "0.0")
            : ("Backup.SizeMb", Math.Max(1, bytes / (1024 * 1024)), "N0");

    public static string CheckKey(BackupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Set.IsDamaged ? "Backup.Damaged" : $"BackupCheckStatus.{entry.Check.Status}";
    }

    public static BackupCheckTone CheckTone(BackupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Set.IsDamaged) return BackupCheckTone.Critical;
        return entry.Check.Status switch
        {
            BackupCheckStatus.Verified => BackupCheckTone.Ok,
            BackupCheckStatus.Failed => BackupCheckTone.Critical,
            BackupCheckStatus.Pending => BackupCheckTone.Running,
            _ => BackupCheckTone.Waiting
        };
    }

    /// <summary>Nothing while a backup or a restore runs; the backup and the schedule once the tab has read SQL Server.</summary>
    public static BackupActions Actions(bool busy, BackupOverview? overview, BackupEntry? selected) =>
        new(!busy && overview is not null, !busy && overview is not null, !busy, !busy && selected is { Restorable: true });

    /// <summary>The restore is offered on the server itself, to a Windows administrator of it; elsewhere it is said where it is done.</summary>
    public static bool RestoreOfferedHere(RestoreContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context is { IsServerMachine: true, IsLocalAdministrator: true };
    }

    /// <summary>The backup just written: verified at once, or left to the server's task to verify.</summary>
    public static string DoneKey(BackupOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Verification == BackupCheckStatus.Verified ? "Backup.DoneVerified" : "Backup.DonePending";
    }
}
