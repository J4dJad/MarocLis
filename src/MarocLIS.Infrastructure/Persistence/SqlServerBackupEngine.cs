using System.Globalization;
using System.Security.Cryptography;
using MarocLIS.Application.Administration;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Organization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Infrastructure.Persistence;

/// <summary>
/// The text of the backup and restore commands, apart from their execution so it can be tested.
/// Every path, name and description is a parameter: only the database name is written into the text,
/// bracket-quoted, because BACKUP and ALTER DATABASE take no variable for it in every position.
/// </summary>
public static class SqlBackupCommands
{
    public static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>
    /// A full backup, WITH CHECKSUM so every page is checked as it is read, and the file started
    /// afresh (FORMAT, INIT): a file holds one backup, which is what the retention deletes.
    /// COMPRESSION only where the edition has it: Express refuses the option outright.
    /// </summary>
    public static string Backup(string database, bool compress) =>
        $"BACKUP DATABASE {Quote(database)} TO DISK = @path WITH CHECKSUM, FORMAT, INIT, " +
        "NAME = @name, DESCRIPTION = @description" + (compress ? ", COMPRESSION" : string.Empty) + ";";

    public static string Verify(bool withChecksum) =>
        "RESTORE VERIFYONLY FROM DISK = @path" + (withChecksum ? " WITH CHECKSUM" : string.Empty) + ";";

    /// <summary>
    /// Disconnects everybody, replaces the database and opens it again.
    /// <para>
    /// SINGLE_USER WITH ROLLBACK IMMEDIATE ends every other connection, rolling back what they had
    /// open. It leaves one connection allowed, and this one is on master: a workstation or the API
    /// service reconnecting in the next millisecond would take that place and the RESTORE would be
    /// refused. OFFLINE closes the database to everybody, this connection included, and RESTORE
    /// WITH REPLACE accepts an offline database and brings it back online.
    /// </para>
    /// </summary>
    public static string Restore(string database, int moves, bool withChecksum)
    {
        var q = Quote(database);
        var moveClauses = string.Concat(Enumerable.Range(0, moves).Select(i => ", MOVE @logical" + i.ToString(CultureInfo.InvariantCulture)
                                                                              + " TO @physical" + i.ToString(CultureInfo.InvariantCulture)));

        return "ALTER DATABASE " + q + " SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
               + "ALTER DATABASE " + q + " SET OFFLINE WITH ROLLBACK IMMEDIATE; "
               + "RESTORE DATABASE " + q + " FROM DISK = @path WITH REPLACE, RECOVERY"
               + (withChecksum ? ", CHECKSUM" : string.Empty) + moveClauses + "; "
               + "ALTER DATABASE " + q + " SET MULTI_USER;";
    }

    /// <summary>What to run when a restore failed: the database open again to everybody, whatever state it was left in.</summary>
    public static string Reopen(string database) =>
        $"IF DATABASEPROPERTYEX(N'{database.Replace("'", "''", StringComparison.Ordinal)}', 'Status') = 'OFFLINE' " +
        $"ALTER DATABASE {Quote(database)} SET ONLINE; ALTER DATABASE {Quote(database)} SET MULTI_USER;";

    /// <summary>A file of a backup or of the database: its logical name, its path and whether it is the log.</summary>
    public sealed record DatabaseFile(string LogicalName, string PhysicalName, bool IsLog);

    /// <summary>
    /// Where each file of the backup goes: onto the database's file of the same logical name, else
    /// onto the database's file of the same kind in the same order, else beside the first data file.
    /// A backup of this very database maps name for name; one taken on another server, whose files
    /// lay elsewhere, still lands on this server's files instead of paths that do not exist here.
    /// </summary>
    public static IReadOnlyList<(string Logical, string Physical)> MapFiles(IReadOnlyList<DatabaseFile> backup,
                                                                           IReadOnlyList<DatabaseFile> current)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moves = new List<(string, string)>();
        var dataFolder = current.Where(f => !f.IsLog).Select(f => BackupNaming.FolderOf(f.PhysicalName)).FirstOrDefault();

        foreach (var file in backup)
        {
            // In the backup's order, each file takes the first free file of its kind: the same order.
            var free = current.Where(c => c.IsLog == file.IsLog && !used.Contains(c.PhysicalName)).ToList();
            var target = free.FirstOrDefault(c => string.Equals(c.LogicalName, file.LogicalName,
                                                                StringComparison.OrdinalIgnoreCase))
                         ?? free.FirstOrDefault();

            // A file this database does not have (a backup with an extra file group): next to the data.
            var physical = target?.PhysicalName
                           ?? (string.IsNullOrEmpty(dataFolder)
                               ? file.PhysicalName
                               : BackupNaming.Combine(dataFolder, BackupNaming.FileOf(file.PhysicalName)));

            used.Add(physical);
            moves.Add((file.LogicalName, physical));
        }

        return moves;
    }

    /// <summary>
    /// The resource key for what SQL Server answered: the first error number that says something,
    /// past the generic "BACKUP DATABASE is terminating abnormally" (3013) that ends every failure.
    /// </summary>
    public static string ErrorKey(IEnumerable<int> numbers)
    {
        foreach (var number in numbers)
        {
            switch (number)
            {
                case 3201 or 15240 or 3202 or 3266:
                    return "Backup_FolderNotWritable";
                case 262 or 229 or 230 or 3110 or 3127 or 916 or 5011:
                    return "Backup_PermissionDenied";
                case 3101 or 5061 or 5070:
                    return "Backup_RestoreExclusiveAccess";
                case 3241 or 3242 or 3203 or 3183 or 3189:
                    return "Backup_FileUnreadable";
                case 3013:
                    continue;
            }
        }

        return "Backup_SqlError";
    }
}

/// <summary>
/// <see cref="IDatabaseBackupEngine"/> on SQL Server, through connections of its own built from the
/// application's connection string: the screen's context stays untouched while a backup runs for
/// minutes, and a restore, which disconnects every connection to the database, runs from master.
/// </summary>
public sealed class SqlServerBackupEngine(LisDbContext db, IServiceScopeFactory scopes,
                                         ILogger<SqlServerBackupEngine>? logger = null) : IDatabaseBackupEngine
{
    /// <summary>A backup or a restore of a laboratory database takes minutes, never hours.</summary>
    private const int LongCommandSeconds = 3 * 60 * 60;

    private string ConnectionString =>
        db.Database.GetConnectionString()
        ?? throw new InvalidOperationException("The database connection string is not configured.");

    private string DatabaseName => new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

    private async Task<SqlConnection> OpenAsync(bool onMaster, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        if (onMaster)
        {
            builder.InitialCatalog = "master";
            // Out of the pool: the restore's connection must not come back later pointing at a
            // session SQL Server ended.
            builder.Pooling = false;
        }

        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static SqlCommand Command(SqlConnection connection, string text, int timeout = 60, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = timeout;
        foreach (var (name, value) in parameters)
            command.Parameters.Add(new SqlParameter(name, value ?? DBNull.Value));
        return command;
    }

    private static Error Failure(SqlException ex, params object?[] context)
    {
        var numbers = ex.Errors.Cast<SqlError>().Select(e => e.Number).ToList();
        var message = string.Join(" ", ex.Errors.Cast<SqlError>()
                                         .Where(e => e.Number != 3013)
                                         .Select(e => e.Message)
                                         .DefaultIfEmpty(ex.Message));
        return Error.Conflict(SqlBackupCommands.ErrorKey(numbers), [.. context, message]);
    }

    public async Task<BackupServerInfo> GetServerInfoAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(onMaster: false, ct);

        const string sql = """
            SELECT DB_NAME(),
                   CONVERT(nvarchar(128), SERVERPROPERTY('ComputerNamePhysicalNetBIOS')),
                   CONVERT(nvarchar(128), SERVERPROPERTY('Edition')),
                   CONVERT(int, SERVERPROPERTY('EngineEdition')),
                   SUSER_SNAME(),
                   HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE'),
                   IS_SRVROLEMEMBER('sysadmin'),
                   IS_SRVROLEMEMBER('dbcreator'),
                   CASE WHEN (SELECT owner_sid FROM sys.databases WHERE database_id = DB_ID()) = SUSER_SID()
                        THEN 1 ELSE 0 END;
            """;

        await using var command = Command(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        var engineEdition = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
        var createAny = !reader.IsDBNull(5) && reader.GetInt32(5) == 1;
        var sysadmin = !reader.IsDBNull(6) && reader.GetInt32(6) == 1;
        var dbcreator = !reader.IsDBNull(7) && reader.GetInt32(7) == 1;
        var owner = reader.GetInt32(8) == 1;

        var info = new BackupServerInfo(
            DatabaseName: reader.GetString(0),
            ServerMachine: reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            Edition: reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            // 2 Standard, 3 Enterprise and Developer, 8 Managed Instance compress; 4 Express does not.
            SupportsCompression: engineEdition is 2 or 3 or 8,
            Login: reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            CanVerify: createAny || sysadmin || dbcreator,
            CanRestore: sysadmin || dbcreator || owner,
            ServiceAccount: null);

        await reader.CloseAsync();

        // The service account is shown in the message about a folder SQL Server cannot write to. Only
        // a login with VIEW SERVER STATE reads it; the others are told "the SQL Server service".
        try
        {
            await using var account = Command(connection,
                "SELECT TOP (1) service_account FROM sys.dm_server_services WHERE filename LIKE N'%sqlservr.exe%';");
            if (await account.ExecuteScalarAsync(ct) is string name) info = info with { ServiceAccount = name };
        }
        catch (SqlException) { }

        return info;
    }

    public async Task<IReadOnlyList<BackupSetRow>> GetHistoryAsync(DateTime since, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(onMaster: false, ct);

        // msdb's history is readable by every login (public), unlike the folder on the server's disk
        // and RESTORE HEADERONLY: it is how a workstation lists the backups. A backup striped over
        // several files is SQL Server Management Studio's, not ours, and is left out of the restore.
        const string sql = """
            SELECT TOP (500) b.backup_set_id, f.physical_device_name, b.backup_start_date, b.backup_finish_date,
                   CONVERT(bigint, b.backup_size), CONVERT(bigint, b.compressed_backup_size),
                   b.has_backup_checksums, b.is_damaged, b.name, b.description, b.user_name, b.machine_name
            FROM msdb.dbo.backupset AS b
            JOIN msdb.dbo.backupmediafamily AS f ON f.media_set_id = b.media_set_id
            WHERE b.database_name = DB_NAME() AND b.type = 'D' AND f.device_type IN (2, 102)
              AND b.backup_finish_date >= @since
              AND NOT EXISTS (SELECT 1 FROM msdb.dbo.backupmediafamily AS o
                              WHERE o.media_set_id = b.media_set_id AND o.family_sequence_number > 1)
            ORDER BY b.backup_finish_date DESC, b.backup_set_id DESC;
            """;

        await using var command = Command(connection, sql, 60, ("@since", since));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<BackupSetRow>();
        while (await reader.ReadAsync(ct))
        {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

            rows.Add(new BackupSetRow(
                reader.GetInt32(0), reader.GetString(1), reader.GetDateTime(2), reader.GetDateTime(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetInt64(5),
                !reader.IsDBNull(6) && reader.GetBoolean(6), !reader.IsDBNull(7) && reader.GetBoolean(7),
                Text(8), Text(9), Text(10), Text(11)));
        }

        return rows;
    }

    public async Task<DateTime?> GetLastBackupAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(onMaster: false, ct);
        await using var command = Command(connection,
            "SELECT MAX(backup_finish_date) FROM msdb.dbo.backupset " +
            "WHERE database_name = DB_NAME() AND type = 'D' AND is_damaged = 0;");
        return await command.ExecuteScalarAsync(ct) is DateTime at ? at : null;
    }

    public async Task<DateTime> GetServerTimeAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(onMaster: false, ct);
        await using var command = Command(connection, "SELECT GETDATE();");
        return (DateTime)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<Result> BackupAsync(string path, BackupKind kind, string description, bool compress,
                                          CancellationToken ct = default)
    {
        try
        {
            await using var connection = await OpenAsync(onMaster: false, ct);
            await using var command = Command(connection, SqlBackupCommands.Backup(DatabaseName, compress), LongCommandSeconds,
                                              ("@path", path), ("@name", BackupNaming.SetName(kind)),
                                              ("@description", description));
            await command.ExecuteNonQueryAsync(ct);
            logger?.LogInformation("Database backed up to {Path} ({Kind}).", path, kind);
            return Result.Success();
        }
        catch (SqlException ex)
        {
            logger?.LogError(ex, "Backup to {Path} failed.", path);
            return Result.Failure(Failure(ex, BackupNaming.FolderOf(path)));
        }
    }

    public async Task<Result> VerifyAsync(string path, bool withChecksum, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await OpenAsync(onMaster: false, ct);
            await using var command = Command(connection, SqlBackupCommands.Verify(withChecksum), LongCommandSeconds,
                                              ("@path", path));
            await command.ExecuteNonQueryAsync(ct);
            return Result.Success();
        }
        catch (SqlException ex)
        {
            logger?.LogError(ex, "Verification of {Path} failed.", path);
            return Result.Failure(Failure(ex, path));
        }
    }

    /// <summary>
    /// Has SQL Server write into the folder, as its own service account, without backing the whole
    /// database up: a certificate of the database (public part only, no private key) is created,
    /// written to a file of a few hundred bytes and dropped. db_owner may do all three, so the check
    /// works from any administrator's workstation; the server's task deletes the file later. The same
    /// error SQL Server would give a real backup into a folder it cannot write to comes back here.
    /// </summary>
    public async Task<Result> ProbeFolderAsync(string folder, CancellationToken ct = default)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        var certificate = SqlBackupCommands.Quote("MarocLIS_FolderProbe_" + token);
        var file = BackupNaming.Combine(folder, $"MarocLIS-test-ecriture-{token}.cer").Replace("'", "''", StringComparison.Ordinal);
        // Never used again: the certificate is dropped as soon as its file is written.
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "aA1!";

        try
        {
            await using var connection = await OpenAsync(onMaster: false, ct);
            await using (var create = Command(connection,
                $"CREATE CERTIFICATE {certificate} ENCRYPTION BY PASSWORD = N'{password.Replace("'", "''", StringComparison.Ordinal)}' " +
                "WITH SUBJECT = N'MarocLIS - test d''ecriture du dossier de sauvegarde';"))
            {
                await create.ExecuteNonQueryAsync(ct);
            }

            try
            {
                await using var write = Command(connection, $"BACKUP CERTIFICATE {certificate} TO FILE = N'{file}';");
                await write.ExecuteNonQueryAsync(ct);
                return Result.Success();
            }
            finally
            {
                await using var drop = Command(connection, $"DROP CERTIFICATE {certificate};");
                await drop.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
        catch (SqlException ex)
        {
            logger?.LogWarning(ex, "SQL Server cannot write into {Folder}.", folder);
            return Result.Failure(Failure(ex, folder));
        }
    }

    public async Task<IReadOnlyList<DatabaseSessionRow>> GetSessionsAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(onMaster: false, ct);

        // Other logins' sessions are visible with VIEW SERVER STATE only, which the server's
        // administrator has: the only place a restore is offered. This process's own pooled
        // connections (same machine, same client process) are left out: they are not another
        // workstation, and the application restarts after the restore anyway.
        await using var command = Command(connection, """
            SELECT s.session_id, s.host_name, s.program_name, s.login_name, s.login_time
            FROM sys.dm_exec_sessions AS s
            CROSS JOIN (SELECT host_name, host_process_id FROM sys.dm_exec_sessions WHERE session_id = @@SPID) AS me
            WHERE s.database_id = DB_ID() AND s.session_id <> @@SPID AND s.is_user_process = 1
              AND NOT (s.host_name = me.host_name AND s.host_process_id = me.host_process_id)
            ORDER BY s.host_name, s.login_time;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<DatabaseSessionRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new DatabaseSessionRow(reader.GetInt16(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                                            reader.IsDBNull(2) ? null : reader.GetString(2),
                                            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetDateTime(4)));
        }

        return rows;
    }

    public async Task<Result> RestoreAsync(BackupSetRow backup, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(backup);

        var database = DatabaseName;
        IReadOnlyList<(string Logical, string Physical)> moves;

        try
        {
            // Read before anything is touched: the files of the backup from msdb, which every login
            // reads (RESTORE FILELISTONLY needs CREATE DATABASE), and this database's own files.
            await using var connection = await OpenAsync(onMaster: false, ct);

            var backupFiles = new List<SqlBackupCommands.DatabaseFile>();
            await using (var command = Command(connection,
                "SELECT logical_name, physical_name, file_type FROM msdb.dbo.backupfile " +
                "WHERE backup_set_id = @id AND file_type IN ('D', 'L') ORDER BY file_number;", 60, ("@id", backup.BackupSetId)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    backupFiles.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2) == "L"));
            }

            var currentFiles = new List<SqlBackupCommands.DatabaseFile>();
            await using (var command = Command(connection,
                "SELECT name, physical_name, type FROM sys.database_files WHERE type IN (0, 1) ORDER BY file_id;"))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    currentFiles.Add(new(reader.GetString(0), reader.GetString(1), reader.GetByte(2) == 1));
            }

            moves = SqlBackupCommands.MapFiles(backupFiles, currentFiles);
        }
        catch (SqlException ex)
        {
            return Result.Failure(Failure(ex, backup.Path));
        }

        try
        {
            await using var master = await OpenAsync(onMaster: true, ct);
            var parameters = new List<(string, object?)> { ("@path", backup.Path) };
            for (var i = 0; i < moves.Count; i++)
            {
                parameters.Add(("@logical" + i.ToString(CultureInfo.InvariantCulture), moves[i].Logical));
                parameters.Add(("@physical" + i.ToString(CultureInfo.InvariantCulture), moves[i].Physical));
            }

            await using var command = Command(master, SqlBackupCommands.Restore(database, moves.Count, backup.HasChecksums),
                                              LongCommandSeconds, [.. parameters]);

            logger?.LogWarning("Restoring database {Database} from {Path}.", database, backup.Path);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (SqlException ex)
        {
            logger?.LogError(ex, "Restore of {Database} from {Path} failed.", database, backup.Path);

            try
            {
                await using var master = await OpenAsync(onMaster: true, CancellationToken.None);
                await using var reopen = Command(master, SqlBackupCommands.Reopen(database));
                await reopen.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (SqlException reopenFailure)
            {
                logger?.LogError(reopenFailure, "Database {Database} could not be opened again after the failed restore.", database);
            }

            SqlConnection.ClearAllPools();
            return Result.Failure(Failure(ex, backup.Path));
        }

        // Every pooled connection to the database was ended by SQL Server: the next query must not
        // pick one of them up and fail with "a transport-level error".
        SqlConnection.ClearAllPools();
        return Result.Success();
    }

    public async Task<Result<bool>> UpgradeSchemaAsync(CancellationToken ct = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<LisDbContext>();
            var pending = (await context.Database.GetPendingMigrationsAsync(ct)).Any();

            var initialized = await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(ct: ct);
            return initialized.IsSuccess ? Result.Success(pending) : Result.Failure<bool>(initialized.Errors);
        }
        catch (SqlException ex)
        {
            logger?.LogError(ex, "The restored database could not be brought to the current schema.");
            return Result.Failure<bool>(Failure(ex, DatabaseName));
        }
    }

    public async Task<IReadOnlyDictionary<string, string?>> ReadValuesAsync(string prefix, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(onMaster: false, ct);
        await using var command = Command(connection,
            "SELECT [Key], [Value] FROM cfg.Settings WHERE SiteId IS NULL AND LEFT([Key], LEN(@prefix)) = @prefix;",
            60, ("@prefix", prefix));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
            values[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);

        return values;
    }

    public async Task WriteValueAsync(string key, string? value, string description, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(onMaster: false, ct);
        await using var command = Command(connection, """
            UPDATE cfg.Settings SET [Value] = @value, ModifiedAt = SYSDATETIMEOFFSET()
            WHERE [Key] = @key AND SiteId IS NULL;
            IF @@ROWCOUNT = 0
                INSERT INTO cfg.Settings (Id, [Key], [Value], SiteId, Description, IsSecuritySensitive, CreatedAt)
                VALUES (NEWID(), @key, @value, NULL, @description, 0, SYSDATETIMEOFFSET());
            """, 60, ("@key", key), ("@value", value), ("@description", description));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<string> GetLaboratoryNameAsync(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LisDbContext>();

        // The main laboratory, else the first site created: the name every report heads with.
        var sites = await context.Sites.AsNoTracking().IgnoreQueryFilters()
                                 .OrderBy(s => s.Kind == LabSiteKind.MainLaboratory ? 0 : 1)
                                 .ThenBy(s => s.CreatedAt)
                                 .Select(s => s.Name.Fr)
                                 .FirstOrDefaultAsync(ct);
        return sites ?? string.Empty;
    }
}
