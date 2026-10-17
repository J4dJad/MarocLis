using MarocLIS.Domain.Migration;

namespace MarocLIS.Application.Migration;

/// <summary>
/// Proposes a source column for each MarocLIS field: the template's own header first, then the
/// names other applications use, then the closest name. Each column is proposed once, to the field
/// it fits best; the operator corrects the rest on screen.
/// </summary>
public static class ColumnMatcher
{
    /// <summary>The least similarity (Dice over letter pairs) at which a name counts as « similar ».</summary>
    public const double SimilarityThreshold = 0.72;

    public static Dictionary<string, string> AutoMatch(DataMigrationKind kind, IReadOnlyList<string> columns)
    {
        var fields = MigrationSchema.Fields(kind);
        var normalizedColumns = columns.Select(MigrationSchema.Normalize).ToList();

        // Every (field, column) pair with its score; the best pairs are taken first.
        var candidates = new List<(MigrationField Field, int Column, double Score)>();

        foreach (var field in fields)
        {
            var names = new[] { field.Header, field.Key }.Concat(field.Synonyms)
                                                        .Select(MigrationSchema.Normalize)
                                                        .Where(n => n.Length > 0)
                                                        .Distinct()
                                                        .ToList();

            for (var c = 0; c < normalizedColumns.Count; c++)
            {
                var column = normalizedColumns[c];
                if (column.Length == 0) continue;

                double score;
                if (column == MigrationSchema.Normalize(field.Header)) score = 3;
                else if (names.Contains(column)) score = 2;
                else score = names.Max(n => Similarity(n, column));

                if (score >= SimilarityThreshold) candidates.Add((field, c, score));
            }
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedColumns = new HashSet<int>();

        foreach (var (field, column, _) in candidates.OrderByDescending(c => c.Score)
                                                     .ThenBy(c => Array.IndexOf([.. fields], c.Field)))
        {
            if (result.ContainsKey(field.Key) || !usedColumns.Add(column)) continue;
            result[field.Key] = columns[column];
        }

        return result;
    }

    /// <summary>Sørensen–Dice coefficient over the letter pairs of two normalised names.</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        if (a.Length < 2 || b.Length < 2) return 0;

        var pairs = new Dictionary<int, int>();
        for (var i = 0; i < a.Length - 1; i++)
        {
            var key = (a[i] << 16) | a[i + 1];
            pairs[key] = pairs.GetValueOrDefault(key) + 1;
        }

        var shared = 0;
        for (var i = 0; i < b.Length - 1; i++)
        {
            var key = (b[i] << 16) | b[i + 1];
            if (pairs.TryGetValue(key, out var count) && count > 0)
            {
                shared++;
                pairs[key] = count - 1;
            }
        }

        return 2.0 * shared / (a.Length - 1 + b.Length - 1);
    }
}
