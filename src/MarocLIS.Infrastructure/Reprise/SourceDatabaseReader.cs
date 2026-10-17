using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using MarocLIS.Application.Migration;
using Microsoft.Data.SqlClient;
using MySqlConnector;

namespace MarocLIS.Infrastructure.Reprise;

/// <summary>An open connection to the old application's database, with what differs between providers.</summary>
public interface ISourceDatabase : IAsyncDisposable
{
    SourceProvider Provider { get; }

    DbConnection Connection { get; }

    /// <summary>A table, schema or view name quoted the way this database quotes identifiers.</summary>
    string Quote(string identifier);

    /// <summary>
    /// A transaction the reads run in and that is always rolled back; read-only where the database
    /// can say so (MySQL). Null when the driver offers none (some ODBC drivers): the SELECT-only
    /// guard is then the only protection, which is why it is applied to every typed query.
    /// </summary>
    Task<DbTransaction?> BeginReadOnlyAsync(CancellationToken ct);
}

public interface ISourceDatabaseFactory
{
    /// <summary>The connection string for <paramref name="settings"/>, password included: never logged or shown.</summary>
    string ConnectionString(SourceConnectionSettings settings);

    Task<ISourceDatabase> OpenAsync(SourceConnectionSettings settings, CancellationToken ct);
}

/// <summary>SQL Server (Express included) natively, MySQL and MariaDB through MySqlConnector, anything else through ODBC.</summary>
public sealed class SourceDatabaseFactory : ISourceDatabaseFactory
{
    public string ConnectionString(SourceConnectionSettings settings) => settings.Provider switch
    {
        SourceProvider.SqlServer => SqlServer(settings),
        SourceProvider.MySql => MySql(settings),
        _ => Odbc(settings)
    };

    private static string SqlServer(SourceConnectionSettings s)
    {
        var builder = new SqlConnectionStringBuilder
        {
            // « .\SQLEXPRESS », « PC-ACCUEIL\SQLEXPRESS », « 192.168.1.10,1433 » are all data sources.
            DataSource = string.IsNullOrWhiteSpace(s.Server) ? "." : s.Server.Trim(),
            IntegratedSecurity = s.IntegratedSecurity,
            ApplicationName = "MarocLIS - reprise de données",
            ApplicationIntent = ApplicationIntent.ReadOnly,
            // An old laboratory server rarely has a certificate, and often no TLS at all.
            Encrypt = SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = true,
            ConnectTimeout = Math.Clamp(s.ConnectTimeoutSeconds, 3, 120),
            Pooling = false
        };

        if (!string.IsNullOrWhiteSpace(s.Database)) builder.InitialCatalog = s.Database.Trim();

        if (!s.IntegratedSecurity)
        {
            builder.UserID = s.UserName?.Trim() ?? string.Empty;
            builder.Password = s.Password ?? string.Empty;
        }

        return builder.ConnectionString;
    }

    private static string MySql(SourceConnectionSettings s)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = string.IsNullOrWhiteSpace(s.Server) ? "localhost" : s.Server.Trim(),
            Port = (uint)(s.Port is > 0 and <= 65535 ? s.Port.Value : 3306),
            UserID = s.UserName?.Trim() ?? string.Empty,
            Password = s.Password ?? string.Empty,
            ConnectionTimeout = (uint)Math.Clamp(s.ConnectTimeoutSeconds, 3, 120),
            Pooling = false,
            SslMode = MySqlSslMode.Preferred,
            // MySQL 8's default authentication over a connection without TLS.
            AllowPublicKeyRetrieval = true,
            // « 0000-00-00 », which old MySQL applications store for « no date ».
            ConvertZeroDateTime = true,
            ApplicationName = "MarocLIS - reprise de données"
        };

        if (!string.IsNullOrWhiteSpace(s.Database)) builder.Database = s.Database.Trim();
        return builder.ConnectionString;
    }

    private static string Odbc(SourceConnectionSettings s)
    {
        var builder = new OdbcConnectionStringBuilder();

        if (!string.IsNullOrWhiteSpace(s.OdbcConnectionString))
            builder.ConnectionString = s.OdbcConnectionString.Trim();
        else if (!string.IsNullOrWhiteSpace(s.OdbcDsn))
            builder.Dsn = s.OdbcDsn.Trim();
        else
            throw new ArgumentException("Migration_OdbcSourceRequired");

        if (!string.IsNullOrWhiteSpace(s.UserName) && !builder.ContainsKey("UID")) builder["UID"] = s.UserName.Trim();
        if (!string.IsNullOrEmpty(s.Password) && !builder.ContainsKey("PWD")) builder["PWD"] = s.Password;

        return builder.ConnectionString;
    }

    public async Task<ISourceDatabase> OpenAsync(SourceConnectionSettings settings, CancellationToken ct)
    {
        DbConnection connection = settings.Provider switch
        {
            SourceProvider.SqlServer => new SqlConnection(SqlServer(settings)),
            SourceProvider.MySql => new MySqlConnection(MySql(settings)),
            _ => new OdbcConnection(Odbc(settings)) { ConnectionTimeout = Math.Clamp(settings.ConnectTimeoutSeconds, 3, 120) }
        };

        try
        {
            await connection.OpenAsync(ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return new SourceDatabase(settings.Provider, connection);
    }

    private sealed class SourceDatabase(SourceProvider provider, DbConnection connection) : ISourceDatabase
    {
        public SourceProvider Provider => provider;
        public DbConnection Connection => connection;

        public string Quote(string identifier) => provider switch
        {
            SourceProvider.SqlServer => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]",
            SourceProvider.MySql => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`",
            _ => QuoteOdbc(identifier)
        };

        private string QuoteOdbc(string identifier)
        {
            try
            {
                // The driver says which character it quotes with: « " » for most, « ` » for MySQL's, « [ » for Access.
                return new OdbcCommandBuilder().QuoteIdentifier(identifier, (OdbcConnection)connection);
            }
            catch (Exception ex) when (ex is OdbcException or InvalidOperationException or NotSupportedException)
            {
                return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            }
        }

        public async Task<DbTransaction?> BeginReadOnlyAsync(CancellationToken ct)
        {
            try
            {
                return provider == SourceProvider.MySql
                    ? await ((MySqlConnection)connection).BeginTransactionAsync(IsolationLevel.ReadCommitted, isReadOnly: true, ct)
                    : await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            }
            catch (Exception ex) when (provider == SourceProvider.Odbc && ex is OdbcException or InvalidOperationException or NotSupportedException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}

/// <summary>
/// Reads the old application's database for the take-over: lists its databases and tables, reads a
/// table or a SELECT. Nothing is ever written: each read runs in a transaction rolled back at the
/// end, and a typed query passes <see cref="SourceQueryGuard"/> here as well as in the service.
/// </summary>
public sealed class SourceDatabaseReader(ISourceDatabaseFactory factory) : IMigrationSourceReader
{
    /// <summary>A large table on an old server takes time; the import waits for it rather than fail at 30 s.</summary>
    public const int CommandTimeoutSeconds = 600;

    public async Task<string> TestAsync(SourceConnectionSettings settings, CancellationToken ct = default)
    {
        await using var source = await factory.OpenAsync(settings, ct);
        return source.Connection.ServerVersion;
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(SourceConnectionSettings settings, CancellationToken ct = default)
    {
        if (settings.Provider == SourceProvider.Odbc) return [];

        var copy = settings.Clone();
        copy.Database = null;

        var sql = settings.Provider == SourceProvider.SqlServer
            ? "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name"
            : "SELECT SCHEMA_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME NOT IN ('mysql', 'information_schema', 'performance_schema', 'sys') ORDER BY SCHEMA_NAME";

        await using var source = await factory.OpenAsync(copy, ct);
        var table = await QueryAsync(source, sql, int.MaxValue, ct);
        return [.. table.Rows.Select(r => r[0]).OfType<string>()];
    }

    public async Task<IReadOnlyList<SourceTableInfo>> ListTablesAsync(SourceConnectionSettings settings, CancellationToken ct = default)
    {
        await using var source = await factory.OpenAsync(settings, ct);

        if (source.Provider == SourceProvider.Odbc)
        {
            // Each driver has its catalogue; GetSchema is the one question they all answer.
            var schema = await source.Connection.GetSchemaAsync("Tables", ct);
            var list = new List<SourceTableInfo>();

            foreach (DataRow row in schema.Rows)
            {
                var name = Column(row, "TABLE_NAME");
                if (string.IsNullOrEmpty(name) || name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase)) continue;

                var type = Column(row, "TABLE_TYPE") ?? "TABLE";
                if (!type.Contains("TABLE", StringComparison.OrdinalIgnoreCase) && !type.Contains("VIEW", StringComparison.OrdinalIgnoreCase)) continue;
                if (type.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase)) continue;

                list.Add(new SourceTableInfo(Column(row, "TABLE_SCHEM") ?? Column(row, "TABLE_SCHEMA"), name,
                                             type.Contains("VIEW", StringComparison.OrdinalIgnoreCase)));
            }

            return [.. list.OrderBy(t => t.Display, StringComparer.OrdinalIgnoreCase)];
        }

        var sql = source.Provider == SourceProvider.SqlServer
            ? "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_SCHEMA, TABLE_NAME"
            : "SELECT NULL, TABLE_NAME, TABLE_TYPE FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME";

        var tables = await QueryAsync(source, sql, int.MaxValue, ct);
        return [.. tables.Rows.Select(r => new SourceTableInfo(
            r[0], r[1] ?? string.Empty, (r[2] ?? string.Empty).Contains("VIEW", StringComparison.OrdinalIgnoreCase)))];
    }

    private static string? Column(DataRow row, string name) =>
        row.Table.Columns.Contains(name) && row[name] is not DBNull ? row[name]?.ToString() : null;

    public async Task<SourceTable> ReadAsync(SourceConnectionSettings settings, SourceSelection selection, int maxRows,
                                             CancellationToken ct = default)
    {
        await using var source = await factory.OpenAsync(settings, ct);

        string sql;
        if (selection.Table is { } table)
        {
            sql = "SELECT * FROM " + (string.IsNullOrEmpty(table.Schema)
                ? source.Quote(table.Name)
                : source.Quote(table.Schema) + "." + source.Quote(table.Name));
        }
        else
        {
            var safe = SourceQueryGuard.Check(selection.Query);
            if (safe.IsFailure) throw new ArgumentException(safe.FirstError.ResourceKey);
            sql = selection.Query!.Trim().TrimEnd(';');
        }

        return await QueryAsync(source, sql, maxRows, ct);
    }

    private static async Task<SourceTable> QueryAsync(ISourceDatabase source, string sql, int maxRows, CancellationToken ct)
    {
        var transaction = await source.BeginReadOnlyAsync(ct);

        try
        {
            await using var command = source.Connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = CommandTimeoutSeconds;
            command.Transaction = transaction;

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, ct);

            var columns = new List<string>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                if (string.IsNullOrWhiteSpace(name)) name = $"Colonne{i + 1}";
                var unique = name;
                for (var n = 2; columns.Contains(unique, StringComparer.OrdinalIgnoreCase); n++) unique = $"{name}_{n}";
                columns.Add(unique);
            }

            var rows = new List<string?[]>();
            var truncated = false;

            while (await reader.ReadAsync(ct))
            {
                if (rows.Count >= maxRows) { truncated = true; break; }

                var values = new string?[columns.Count];
                for (var i = 0; i < columns.Count; i++)
                {
                    object? value;
                    try { value = reader.IsDBNull(i) ? null : reader.GetValue(i); }
                    // A value the driver cannot convert (a MySQL zero date it did not catch, an
                    // out-of-range decimal) is read as text rather than failing the whole read.
                    catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException)
                    {
                        value = TryText(reader, i);
                    }

                    values[i] = SourceTable.ToText(value)?.Trim() is { Length: > 0 } text ? text : null;
                }

                rows.Add(values);
            }

            return new SourceTable(columns, rows, truncated);
        }
        finally
        {
            if (transaction is not null)
            {
                // Never committed: whatever the statement did, it is undone.
                try { await transaction.RollbackAsync(CancellationToken.None); }
                catch (Exception ex) when (ex is DbException or InvalidOperationException) { }
                await transaction.DisposeAsync();
            }
        }
    }

    private static string? TryText(DbDataReader reader, int i)
    {
        try { return reader.GetString(i); }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException) { return null; }
    }
}
