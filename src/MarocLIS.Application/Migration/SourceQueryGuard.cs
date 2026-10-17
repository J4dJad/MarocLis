using System.Text;
using System.Text.RegularExpressions;
using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Migration;

/// <summary>
/// The rule a query typed against the old application's database must pass before it is sent:
/// one SELECT (or WITH … SELECT), nothing that writes, creates, runs a procedure or starts a second
/// statement. The connection is read inside a transaction that is always rolled back as well; the
/// guard is what stops a statement the driver would commit on its own (DDL on MySQL, a procedure).
/// <para>
/// Comments and quoted text are removed first, so a forbidden word hidden in a comment does not
/// pass and a value « 'DELETE' » in a WHERE clause is not refused.
/// </para>
/// </summary>
public static partial class SourceQueryGuard
{
    public const int MaxLength = 8000;

    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "UPSERT", "DROP", "ALTER", "CREATE", "TRUNCATE", "RENAME",
        "EXEC", "EXECUTE", "CALL", "DO", "GRANT", "REVOKE", "DENY", "INTO", "SET", "USE", "DECLARE",
        "BACKUP", "RESTORE", "SHUTDOWN", "KILL", "DBCC", "BULK", "LOAD", "HANDLER", "LOCK", "UNLOCK",
        "WAITFOR", "OPENROWSET", "OPENQUERY", "OPENDATASOURCE", "OPENXML", "COMMIT", "ROLLBACK",
        "SAVEPOINT", "BEGIN", "TRANSACTION", "ATTACH", "DETACH", "PRAGMA", "VACUUM", "REINDEX",
        "ANALYZE", "OPTIMIZE", "REPAIR", "FLUSH", "RESET", "PURGE", "INSTALL", "UNINSTALL", "COPY"
    };

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_$#@]*", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    /// <summary>Success when <paramref name="sql"/> is a single read-only query.</summary>
    public static Result Check(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return Result.Failure(Error.Validation("Migration_QueryRequired"));
        if (sql.Length > MaxLength) return Result.Failure(Error.Validation("Migration_QueryTooLong", MaxLength));

        string bare;
        try
        {
            bare = StripCommentsAndLiterals(sql);
        }
        catch (FormatException)
        {
            return Result.Failure(Error.Validation("Migration_QueryUnterminated"));
        }

        var trimmed = bare.Trim();

        // One statement: a semicolon is allowed only as the very last character.
        var semicolon = trimmed.IndexOf(';');
        if (semicolon >= 0 && semicolon < trimmed.TrimEnd(';', ' ', '\t', '\r', '\n').Length)
            return Result.Failure(Error.Validation("Migration_QuerySingleStatement"));

        var words = Word().Matches(trimmed).Select(m => m.Value).ToList();
        if (words.Count == 0) return Result.Failure(Error.Validation("Migration_QuerySelectOnly"));

        var first = words[0];
        if (!first.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
            && !first.Equals("WITH", StringComparison.OrdinalIgnoreCase))
            return Result.Failure(Error.Validation("Migration_QuerySelectOnly"));

        if (!words.Any(w => w.Equals("SELECT", StringComparison.OrdinalIgnoreCase)))
            return Result.Failure(Error.Validation("Migration_QuerySelectOnly"));

        foreach (var word in words)
        {
            if (Forbidden.Contains(word))
                return Result.Failure(Error.Validation("Migration_QueryForbiddenWord", word.ToUpperInvariant()));

            // System procedures and extended procedures, reachable without EXEC in some dialects.
            if (word.StartsWith("xp_", StringComparison.OrdinalIgnoreCase)
                || word.StartsWith("sp_", StringComparison.OrdinalIgnoreCase))
                return Result.Failure(Error.Validation("Migration_QueryForbiddenWord", word));
        }

        // « GO » on a line of its own separates batches in SQL Server tools.
        if (trimmed.Split('\n').Any(l => l.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase)))
            return Result.Failure(Error.Validation("Migration_QuerySingleStatement"));

        // SELECT … FOR UPDATE takes locks on the old application's rows.
        if (MyRegexForUpdate().IsMatch(trimmed))
            return Result.Failure(Error.Validation("Migration_QueryForbiddenWord", "FOR UPDATE"));

        return Result.Success();
    }

    [GeneratedRegex(@"\bFOR\s+UPDATE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MyRegexForUpdate();

    /// <summary>
    /// The query with its comments removed and its quoted strings emptied ('…', "…", [...] and `…`
    /// keep their delimiters so identifiers still separate words). Throws on an unterminated one.
    /// </summary>
    private static string StripCommentsAndLiterals(string sql)
    {
        var builder = new StringBuilder(sql.Length);
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                builder.Append(' ');
                continue;
            }

            if (c == '#')
            {
                // MySQL's line comment.
                while (i < sql.Length && sql[i] != '\n') i++;
                builder.Append(' ');
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new FormatException();
                i = end + 2;
                builder.Append(' ');
                continue;
            }

            if (c is '\'' or '"' or '`' or '[')
            {
                var close = c == '[' ? ']' : c;
                var j = i + 1;
                while (true)
                {
                    if (j >= sql.Length) throw new FormatException();
                    if (sql[j] == close)
                    {
                        if (close != ']' && j + 1 < sql.Length && sql[j + 1] == close) { j += 2; continue; }
                        break;
                    }
                    if (sql[j] == '\\' && c == '\'' ) { j += 2; continue; }
                    j++;
                }

                // Identifiers are kept as a placeholder word, literals as an empty string.
                builder.Append(c == '\'' ? "''" : " X ");
                i = j + 1;
                continue;
            }

            builder.Append(c);
            i++;
        }

        return builder.ToString();
    }
}
