using System.Text.Json.Serialization;

namespace MarocLIS.Application.Migration;

/// <summary>The kinds of database the old application may keep its data in.</summary>
public enum SourceProvider
{
    /// <summary>SQL Server or SQL Server Express (« .\SQLEXPRESS », « PC\SQLEXPRESS »).</summary>
    SqlServer = 0,

    /// <summary>MySQL or MariaDB, through the MySqlConnector driver.</summary>
    MySql = 1,

    /// <summary>Anything else with an ODBC driver installed on the workstation (Access, Oracle, Firebird…).</summary>
    Odbc = 2
}

/// <summary>
/// How to reach the old application's database. The password is held for the session only; a
/// saved profile keeps it encrypted for the Windows user (DPAPI), never in clear.
/// </summary>
public sealed class SourceConnectionSettings
{
    public SourceProvider Provider { get; set; } = SourceProvider.SqlServer;

    /// <summary>Server name or address; for SQL Server Express the instance too (« .\SQLEXPRESS »).</summary>
    public string? Server { get; set; }

    /// <summary>MySQL's port; 3306 when empty.</summary>
    public int? Port { get; set; }

    public string? Database { get; set; }

    /// <summary>SQL Server: Windows authentication rather than a SQL login.</summary>
    public bool IntegratedSecurity { get; set; }

    public string? UserName { get; set; }

    [JsonIgnore]
    public string? Password { get; set; }

    /// <summary>ODBC: a data source name declared on the workstation…</summary>
    public string? OdbcDsn { get; set; }

    /// <summary>… or a full connection string (« Driver={…};Dbq=C:\…\labo.mdb; »), typed by the operator.</summary>
    public string? OdbcConnectionString { get; set; }

    /// <summary>Seconds to wait for the server.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 15;

    public SourceConnectionSettings Clone() => (SourceConnectionSettings)MemberwiseClone();

    /// <summary>Where the rows come from, for the journal and the report: no password, ever.</summary>
    public string Describe()
    {
        return Provider switch
        {
            SourceProvider.SqlServer => $"SQL Server {Server}/{Database}",
            SourceProvider.MySql => $"MySQL {Server}:{Port ?? 3306}/{Database}",
            _ => string.IsNullOrWhiteSpace(OdbcDsn) ? $"ODBC {RedactedOdbc()}" : $"ODBC DSN={OdbcDsn}"
        };
    }

    private string RedactedOdbc()
    {
        if (string.IsNullOrWhiteSpace(OdbcConnectionString)) return string.Empty;

        var parts = OdbcConnectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.TrimStart().StartsWith("PWD", StringComparison.OrdinalIgnoreCase)
                        && !p.TrimStart().StartsWith("PASSWORD", StringComparison.OrdinalIgnoreCase));
        return string.Join(';', parts);
    }
}

/// <summary>A table or view of the old database.</summary>
public sealed record SourceTableInfo(string? Schema, string Name, bool IsView)
{
    public string Display => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
}

/// <summary>What to read: a table or view, or a SELECT the operator wrote.</summary>
public sealed record SourceSelection(SourceTableInfo? Table, string? Query)
{
    public string Describe() => Table is not null ? Table.Display : "SELECT…";
}

/// <summary>
/// Reads the old application's database, never writing to it: every read runs in a transaction
/// that is rolled back, and a typed query must pass <see cref="SourceQueryGuard"/> first.
/// </summary>
public interface IMigrationSourceReader
{
    /// <summary>Opens and closes a connection; the server's version on success.</summary>
    Task<string> TestAsync(SourceConnectionSettings settings, CancellationToken ct = default);

    /// <summary>The databases the login may open (SQL Server, MySQL); empty for ODBC, where the DSN names one.</summary>
    Task<IReadOnlyList<string>> ListDatabasesAsync(SourceConnectionSettings settings, CancellationToken ct = default);

    Task<IReadOnlyList<SourceTableInfo>> ListTablesAsync(SourceConnectionSettings settings, CancellationToken ct = default);

    Task<SourceTable> ReadAsync(SourceConnectionSettings settings, SourceSelection selection, int maxRows,
                                CancellationToken ct = default);
}

/// <summary>Reads an .xlsx workbook's sheets, and writes the templates.</summary>
public interface IMigrationWorkbook
{
    IReadOnlyList<string> SheetNames(string path);

    SourceTable Read(string path, string? sheet, int maxRows = int.MaxValue);

    /// <summary>The template of a kind: its French headers on the first row, an example row under them.</summary>
    void WriteTemplate(Domain.Migration.DataMigrationKind kind, string path);
}
