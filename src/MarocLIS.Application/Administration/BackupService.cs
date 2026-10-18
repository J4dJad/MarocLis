using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;

namespace MarocLIS.Application.Administration;

/// <summary>What SQL Server says about itself and about the login MarocLIS is connected with.</summary>
public sealed record BackupServerInfo(
    string DatabaseName,
    // The server's Windows name (SERVERPROPERTY ComputerNamePhysicalNetBIOS).
    string ServerMachine,
    string Edition,
    // Express writes no compressed backup; the editions above it do.
    bool SupportsCompression,
    string Login,
    // RESTORE VERIFYONLY and HEADERONLY need CREATE DATABASE, which db_owner does not give.
    bool CanVerify,
    // Replacing the database needs sysadmin, dbcreator or being its owner.
    bool CanRestore,
    // The SQL Server service account, the one that writes the files; null when not readable.
    string? ServiceAccount);

/// <summary>One backup of the database in SQL Server's history (msdb), which every login may read.</summary>
public sealed record BackupSetRow(
    long BackupSetId,
    string Path,
    DateTime StartedAt,
    DateTime FinishedAt,
    long SizeBytes,
    long? CompressedBytes,
    bool HasChecksums,
    bool IsDamaged,
    string? Name,
    string? Description,
    string? UserName,
    string? MachineName);

/// <summary>A connection to the database, from sys.dm_exec_sessions.</summary>
public sealed record DatabaseSessionRow(int SessionId, string? Host, string? Program, string? Login, DateTime LoginTime);

/// <summary>A backup as the screen lists it.</summary>
public sealed record BackupEntry(BackupSetRow Set, BackupKind Kind, BackupCheck Check)
{
    public string FileName => BackupNaming.FileOf(Set.Path);

    /// <summary>The bytes on disk: compressed when the edition compressed it.</summary>
    public long FileBytes => Set.CompressedBytes is > 0 ? Set.CompressedBytes.Value : Set.SizeBytes;

    /// <summary>Whether a restore may start from it: not known to be damaged, failed or deleted.</summary>
    public bool Restorable => !Set.IsDamaged && Check.Status is not (BackupCheckStatus.Failed or BackupCheckStatus.Deleted);
}

/// <summary>The scheduled task on the server, as its heartbeat shows it.</summary>
public enum BackupAgentStatus
{
    /// <summary>Never seen: the task is not installed (a workstation-only installation), or the schema was new.</summary>
    NeverSeen = 0,
    Running = 1,
    /// <summary>Seen once, silent for more than <see cref="BackupState.AgentSilentAfter"/>.</summary>
    Silent = 2
}

/// <summary>Everything the « Sauvegardes » tab shows.</summary>
public sealed record BackupOverview(
    BackupSettings Settings,
    BackupState State,
    BackupServerInfo Server,
    IReadOnlyList<BackupEntry> Backups,
    DateTime? LastSuccessAt,
    DateTime? NextRunAt,
    BackupAgentStatus Agent,
    bool IsStale,
    // The server's clock when read: the history's times are the server's.
    DateTime ServerNow);

/// <summary>The warning at startup, for an administrator.</summary>
public sealed record BackupHealth(DateTime? LastSuccessAt, bool IsStale);

/// <summary>A backup just written, and whether it could be verified at once.</summary>
public sealed record BackupOutcome(string Path, BackupCheckStatus Verification);

/// <summary>
/// Where the restore is asked from. Filled by the desktop: the service cannot see the machine it
/// runs on through SQL Server, and refuses a restore asked from anywhere but the server.
/// </summary>
public sealed record RestoreContext(bool IsServerMachine, bool IsLocalAdministrator);

public sealed record RestoreRequest(long BackupSetId, string TypedLaboratoryName, RestoreContext Context);

/// <summary>The steps of a restore, reported as they start.</summary>
public enum RestoreStep
{
    Checking = 0,
    SafetyBackup = 1,
    Restoring = 2,
    Upgrading = 3,
    Done = 4
}

public sealed record RestoreOutcome(string RestoredPath, string SafetyBackupPath, bool SchemaUpgraded);

/// <summary>
/// The SQL Server side of the backups: the commands, SQL Server's history and the settings written
/// outside the audit trail. Implemented on its own connections, never the screen's context: a
/// backup runs for minutes and a restore disconnects every connection to the database.
/// </summary>
public interface IDatabaseBackupEngine
{
    Task<BackupServerInfo> GetServerInfoAsync(CancellationToken ct = default);

    /// <summary>Full backups of this database to disk since <paramref name="since"/> (server time), newest first.</summary>
    Task<IReadOnlyList<BackupSetRow>> GetHistoryAsync(DateTime since, CancellationToken ct = default);

    /// <summary>The finish time of the last good full backup of this database, server time.</summary>
    Task<DateTime?> GetLastBackupAsync(CancellationToken ct = default);

    /// <summary>The server's clock, for comparing with the history it wrote.</summary>
    Task<DateTime> GetServerTimeAsync(CancellationToken ct = default);

    Task<Result> BackupAsync(string path, BackupKind kind, string description, bool compress,
                             CancellationToken ct = default);

    Task<Result> VerifyAsync(string path, bool withChecksum, CancellationToken ct = default);

    /// <summary>Has the SQL Server service account write a small file into <paramref name="folder"/>.</summary>
    Task<Result> ProbeFolderAsync(string folder, CancellationToken ct = default);

    /// <summary>The other connections to this database: the workstations a restore would disconnect.</summary>
    Task<IReadOnlyList<DatabaseSessionRow>> GetSessionsAsync(CancellationToken ct = default);

    /// <summary>Single user, RESTORE … WITH REPLACE, multi user. Every connection to the database is cut.</summary>
    Task<Result> RestoreAsync(BackupSetRow backup, CancellationToken ct = default);

    /// <summary>Applies the migrations a restored, older database lacks (DatabaseInitializer).</summary>
    Task<Result<bool>> UpgradeSchemaAsync(CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, string?>> ReadValuesAsync(string prefix, CancellationToken ct = default);

    /// <summary>Writes an installation setting directly, outside the audit trail: state, not a decision.</summary>
    Task WriteValueAsync(string key, string? value, string description, CancellationToken ct = default);

    /// <summary>The French name of the main laboratory, typed to confirm a restore.</summary>
    Task<string> GetLaboratoryNameAsync(CancellationToken ct = default);
}

/// <summary>
/// Paramètres › Sauvegardes: the schedule the server's task follows, « Sauvegarder maintenant »,
/// the list of backups and the restore. Every entry point requires <see cref="Permissions.Administration.Backup"/>.
/// </summary>
public sealed class BackupService(
    IDatabaseBackupEngine engine,
    IConfigurationWriter settingsWriter,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ISecurityAuditor securityAuditor,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>The age past which the last backup is reported to the administrators at startup.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(48);

    /// <summary>How far back the history is listed beyond the retention: a backup kept by hand stays visible a while.</summary>
    private const int ListMarginDays = 7;

    private Result Allowed() => guard.Require(Permissions.Administration.Backup);

    public async Task<Result<BackupOverview>> GetOverviewAsync(CancellationToken ct = default)
    {
        var permitted = Allowed();
        if (permitted.IsFailure) return Result.Failure<BackupOverview>(permitted.FirstError);

        var values = await engine.ReadValuesAsync(BackupSettings.Prefix, ct);
        var settings = BackupSettings.FromValues(values);
        var state = BackupState.FromValues(values);
        var server = await engine.GetServerInfoAsync(ct);
        var serverNow = await engine.GetServerTimeAsync(ct);

        var history = await engine.GetHistoryAsync(serverNow.AddDays(-(settings.RetentionDays + ListMarginDays)), ct);

        var backups = history
            .Select(set => new BackupEntry(set, BackupNaming.KindOf(set.Name),
                                           values.TryGetValue(BackupCheck.KeyFor(BackupNaming.FileOf(set.Path)), out var check)
                                               ? BackupCheck.Parse(check)
                                               : BackupCheck.Unknown))
            // A file the retention deleted is no longer a backup, whatever SQL Server's history says.
            .Where(b => b.Check.Status != BackupCheckStatus.Deleted)
            .ToList();

        var lastSuccess = await engine.GetLastBackupAsync(ct);

        var agent = state.AgentSeenAt is not { } seen
            ? BackupAgentStatus.NeverSeen
            : clock.Now - seen > BackupState.AgentSilentAfter ? BackupAgentStatus.Silent : BackupAgentStatus.Running;

        return Result.Success(new BackupOverview(
            settings, state, server, backups, lastSuccess,
            BackupSchedule.NextRun(settings, serverNow), agent,
            IsStale(lastSuccess, serverNow), serverNow));
    }

    /// <summary>
    /// The age of the last backup, for the warning shown to the administrators when they sign in.
    /// Null for a user without the right: the warning is theirs to act on, not the counter's.
    /// </summary>
    public async Task<BackupHealth?> GetHealthAsync(CancellationToken ct = default)
    {
        if (!currentUser.Has(Permissions.Administration.Backup)) return null;

        var last = await engine.GetLastBackupAsync(ct);
        return new BackupHealth(last, IsStale(last, await engine.GetServerTimeAsync(ct)));
    }

    public static bool IsStale(DateTime? lastSuccess, DateTime serverNow) =>
        lastSuccess is not { } last || serverNow - last > StaleAfter;

    /// <summary>
    /// Saves the schedule. A new folder is first tried by SQL Server itself, since it is SQL Server's
    /// service account that writes there and not the administrator at the workstation; the folder
    /// check can be skipped for a folder the server's task is to create (it creates a missing local
    /// folder and gives the service account its rights at its next pass).
    /// </summary>
    public async Task<Result> SaveSettingsAsync(BackupSettings settings, bool checkFolder = true,
                                                CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var permitted = Allowed();
        if (permitted.IsFailure) return permitted;

        settings = settings with { Folder = settings.Folder.Trim() };
        var valid = settings.Validate();
        if (valid.IsFailure) return valid;

        var current = BackupSettings.FromValues(await engine.ReadValuesAsync(BackupSettings.Prefix, ct));
        var folderChanged = !string.Equals(current.Folder, settings.Folder, StringComparison.OrdinalIgnoreCase);

        if (folderChanged && checkFolder)
        {
            var probe = await engine.ProbeFolderAsync(settings.Folder, ct);
            if (probe.IsFailure) return probe;
        }

        var changed = new List<string>();
        foreach (var (key, value) in settings.ToValues())
        {
            var existing = await settingsWriter.GetSettingAsync(key, null, ct);
            if (existing is null)
            {
                await settingsWriter.AddSettingAsync(new AppSetting(key, value)
                {
                    Description = "Sauvegarde de la base (tâche planifiée du serveur, Paramètres › Sauvegardes)"
                }, ct);
                changed.Add(key);
            }
            else if (existing.Value != value)
            {
                existing.Value = value;
                changed.Add(key);
            }
        }

        if (changed.Count == 0) return Result.Success();

        var summary = Describe(settings);
        unitOfWork.AuditReason = $"Sauvegardes modifiées : {summary}";
        await unitOfWork.SaveChangesAsync(ct);

        await securityAuditor.LogAsync(SecurityEventKind.BackupSettingsChanged, succeeded: true,
                                       details: summary, ct: ct);
        return Result.Success();
    }

    /// <summary>The check behind the folder field's « Tester » button.</summary>
    public async Task<Result> TestFolderAsync(string folder, CancellationToken ct = default)
    {
        var permitted = Allowed();
        if (permitted.IsFailure) return permitted;

        if (!BackupSettings.IsAcceptableFolder(folder))
            return Result.Failure(Error.Validation("Backup_FolderInvalid", folder));

        return await engine.ProbeFolderAsync(folder.Trim(), ct);
    }

    /// <summary>
    /// « Sauvegarder maintenant »: a full backup into the folder, verified at once when the login may
    /// verify, left to the server's task otherwise. Allowed from any administrator's workstation:
    /// db_owner, which the workstations have, may back up.
    /// </summary>
    public async Task<Result<BackupOutcome>> BackupNowAsync(CancellationToken ct = default)
    {
        var permitted = Allowed();
        if (permitted.IsFailure) return Result.Failure<BackupOutcome>(permitted.FirstError);

        var settings = BackupSettings.FromValues(await engine.ReadValuesAsync(BackupSettings.Prefix, ct));
        return await BackupAsync(settings.Folder, BackupKind.Manual, ct);
    }

    private async Task<Result<BackupOutcome>> BackupAsync(string folder, BackupKind kind, CancellationToken ct)
    {
        var server = await engine.GetServerInfoAsync(ct);
        var serverNow = await engine.GetServerTimeAsync(ct);
        var path = BackupNaming.Combine(folder, BackupNaming.FileName(server.DatabaseName, kind, serverNow));
        var description = kind == BackupKind.BeforeRestore
            ? $"Avant restauration, par {currentUser.FullName} ({currentUser.UserName}) sur {currentUser.Workstation}"
            : $"Par {currentUser.FullName} ({currentUser.UserName}) depuis {currentUser.Workstation}";

        var written = await engine.BackupAsync(path, kind, Truncate(description, 255), server.SupportsCompression, ct);

        if (written.IsFailure)
        {
            await RecordResultAsync(new BackupCheck(BackupCheckStatus.Failed, clock.Now, written.FirstError.ResourceKey), ct);
            await securityAuditor.LogAsync(SecurityEventKind.BackupFailed, succeeded: false,
                                           failureReasonKey: written.FirstError.ResourceKey,
                                           details: $"{path} : {string.Join(" ", written.FirstError.Args)}", ct: ct);
            return Result.Failure<BackupOutcome>(written.Errors);
        }

        var check = new BackupCheck(BackupCheckStatus.Pending, clock.Now, null);
        if (server.CanVerify)
        {
            var verified = await engine.VerifyAsync(path, withChecksum: true, ct);
            check = verified.IsSuccess
                ? new BackupCheck(BackupCheckStatus.Verified, clock.Now, null)
                : new BackupCheck(BackupCheckStatus.Failed, clock.Now, verified.FirstError.ResourceKey);
        }

        await engine.WriteValueAsync(BackupCheck.KeyFor(BackupNaming.FileOf(path)), check.Format(),
                                     "Vérification de la sauvegarde", ct);

        if (check.Status == BackupCheckStatus.Failed)
        {
            await RecordResultAsync(new BackupCheck(BackupCheckStatus.Failed, clock.Now, "Backup_VerifyFailed"), ct);
            await securityAuditor.LogAsync(SecurityEventKind.BackupFailed, succeeded: false,
                                           failureReasonKey: "Backup_VerifyFailed", details: path, ct: ct);
            return Result.Failure<BackupOutcome>(Error.Conflict("Backup_VerifyFailed", path));
        }

        await engine.WriteValueAsync(BackupState.LastSuccessAtKey, BackupState.FormatMoment(clock.Now),
                                     "Dernière sauvegarde réussie", ct);
        await RecordResultAsync(new BackupCheck(BackupCheckStatus.Verified, clock.Now, BackupNaming.FileOf(path)), ct);
        await securityAuditor.LogAsync(SecurityEventKind.BackupCompleted, succeeded: true,
                                       details: $"{BackupNaming.SetName(kind)} {path}"
                                                + (check.Status == BackupCheckStatus.Verified ? " (vérifiée)" : " (vérification par le serveur)"),
                                       ct: ct);

        return Result.Success(new BackupOutcome(path, check.Status));
    }

    private Task RecordResultAsync(BackupCheck result, CancellationToken ct) =>
        engine.WriteValueAsync(BackupState.LastResultKey, result.Format(), "Résultat de la dernière sauvegarde", ct);

    /// <summary>The workstations connected to the database, shown before a restore disconnects them.</summary>
    public async Task<Result<IReadOnlyList<DatabaseSessionRow>>> GetSessionsAsync(CancellationToken ct = default)
    {
        var permitted = Allowed();
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<DatabaseSessionRow>>(permitted.FirstError);

        return Result.Success(await engine.GetSessionsAsync(ct));
    }

    /// <summary>
    /// Whether a restore may be asked for from here, before anything is shown: the right, the server
    /// itself with a local administrator, and SQL Server rights to replace the database.
    /// </summary>
    public async Task<Result> CanRestoreAsync(RestoreContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var permitted = Allowed();
        if (permitted.IsFailure) return permitted;

        // On the server only: the restore cuts every workstation off, and whoever does it must be
        // able to see the server's state and restart its service if it goes wrong. A workstation
        // that asks is told where to go rather than refused without a reason.
        if (!context.IsServerMachine || !context.IsLocalAdministrator)
            return Result.Failure(Error.Forbidden("Backup_RestoreServerOnly"));

        var server = await engine.GetServerInfoAsync(ct);
        if (!server.CanRestore)
            return Result.Failure(Error.Forbidden("Backup_RestoreNoSqlRights", server.Login));

        return Result.Success();
    }

    /// <summary>The name to type to confirm a restore.</summary>
    public async Task<Result<string>> GetConfirmationNameAsync(CancellationToken ct = default)
    {
        var permitted = Allowed();
        if (permitted.IsFailure) return Result.Failure<string>(permitted.FirstError);

        return Result.Success(await engine.GetLaboratoryNameAsync(ct));
    }

    public static bool ConfirmationMatches(string expected, string? typed)
    {
        static string Normalise(string? s) =>
            string.Join(' ', (s ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        // Case and accents aside: the point is that the name was read and typed, not spelled
        // exactly on a keyboard without French accents.
        var wanted = Normalise(expected);
        return wanted.Length > 0
               && CultureInfo.GetCultureInfo("fr-FR").CompareInfo.Compare(
                   wanted, Normalise(typed), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0;
    }

    /// <summary>
    /// Replaces the database with a backup: a safety backup of the database as it is, single user,
    /// RESTORE WITH REPLACE, multi user, then the migrations that bring an older backup to the current
    /// schema. The caller restarts the application afterwards: every connection it held is gone.
    /// <para>
    /// The security log is the database's own, so it is replaced too. The start is written to the
    /// database about to be replaced, which keeps it in the safety backup; the outcome is written to
    /// the restored one afterwards, naming the backup restored and the safety backup.
    /// </para>
    /// </summary>
    public async Task<Result<RestoreOutcome>> RestoreAsync(RestoreRequest request, IProgress<RestoreStep>? progress = null,
                                                           CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        progress?.Report(RestoreStep.Checking);

        var allowed = await CanRestoreAsync(request.Context, ct);
        if (allowed.IsFailure) return Result.Failure<RestoreOutcome>(allowed.Errors);

        var expected = await engine.GetLaboratoryNameAsync(ct);
        if (!ConfirmationMatches(expected, request.TypedLaboratoryName))
            return Result.Failure<RestoreOutcome>(Error.Validation("Backup_RestoreConfirmationMismatch", expected));

        var overview = await GetOverviewAsync(ct);
        if (overview.IsFailure) return Result.Failure<RestoreOutcome>(overview.Errors);

        var backup = overview.Value.Backups.FirstOrDefault(b => b.Set.BackupSetId == request.BackupSetId);
        if (backup is null)
            return Result.Failure<RestoreOutcome>(Error.NotFound("Backup_RestoreBackupNotFound"));
        if (!backup.Restorable)
            return Result.Failure<RestoreOutcome>(Error.Conflict("Backup_RestoreBackupUnusable", backup.FileName));

        // Read before anything is replaced: a damaged file found now costs nothing.
        if (overview.Value.Server.CanVerify)
        {
            var verified = await engine.VerifyAsync(backup.Set.Path, backup.Set.HasChecksums, ct);
            if (verified.IsFailure)
                return Result.Failure<RestoreOutcome>(Error.Conflict("Backup_RestoreBackupUnusable", backup.FileName));
        }

        progress?.Report(RestoreStep.SafetyBackup);

        // Into the folder of the backup restored when the schedule's folder is not writable from
        // here would be a guess; the schedule's folder is the one SQL Server was shown to write to.
        var safety = await BackupAsync(overview.Value.Settings.Folder, BackupKind.BeforeRestore, ct);
        if (safety.IsFailure)
            return Result.Failure<RestoreOutcome>(Error.Conflict("Backup_RestoreSafetyBackupFailed",
                                                                 Describe(safety.FirstError)));

        var details = $"Restauration de {backup.Set.Path} (sauvegarde du {backup.Set.FinishedAt:dd/MM/yyyy HH:mm}) ; "
                      + $"sauvegarde de sécurité {safety.Value.Path}";

        await securityAuditor.LogAsync(SecurityEventKind.RestoreStarted, succeeded: true, details: details, ct: ct);

        progress?.Report(RestoreStep.Restoring);

        var restored = await engine.RestoreAsync(backup.Set, ct);
        if (restored.IsFailure)
        {
            await securityAuditor.LogAsync(SecurityEventKind.RestoreFailed, succeeded: false,
                                           failureReasonKey: restored.FirstError.ResourceKey,
                                           details: $"{details} : {Describe(restored.FirstError)}", ct: ct);
            return Result.Failure<RestoreOutcome>(Error.Conflict("Backup_RestoreFailed", Describe(restored.FirstError),
                                                                 safety.Value.Path));
        }

        progress?.Report(RestoreStep.Upgrading);

        var upgraded = await engine.UpgradeSchemaAsync(ct);
        if (upgraded.IsFailure)
        {
            await securityAuditor.LogAsync(SecurityEventKind.RestoreFailed, succeeded: false,
                                           failureReasonKey: upgraded.FirstError.ResourceKey,
                                           details: $"{details} : migrations non appliquées", ct: ct);
            return Result.Failure<RestoreOutcome>(Error.Conflict("Backup_RestoreUpgradeFailed", safety.Value.Path));
        }

        await securityAuditor.LogAsync(SecurityEventKind.RestoreCompleted, succeeded: true,
                                       details: details + (upgraded.Value ? " ; schéma mis à jour" : string.Empty),
                                       ct: ct);

        progress?.Report(RestoreStep.Done);
        return Result.Success(new RestoreOutcome(backup.Set.Path, safety.Value.Path, upgraded.Value));
    }

    private static string Describe(Error error) =>
        error.Args.Length == 0 ? error.ResourceKey : $"{error.ResourceKey} ({string.Join(", ", error.Args)})";

    private static string Describe(BackupSettings settings)
    {
        var days = settings.Days.Count == 0 ? "tous les jours" : string.Join(", ", settings.Days);
        return $"{(settings.Enabled ? "activée" : "désactivée")}, {BackupSettings.FormatTimes(settings.Times)} ({days}), "
               + $"dossier {settings.Folder}, conservation {settings.RetentionDays} jours";
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];
}
