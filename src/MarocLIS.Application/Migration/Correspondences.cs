namespace MarocLIS.Application.Migration;

/// <summary>One value a coded field can take in MarocLIS: a payer's code, an analysis parameter…</summary>
/// <param name="Value">What is stored in a correspondence table: a code, stable between databases.</param>
/// <param name="Label">What the operator reads.</param>
/// <param name="Aliases">Other codes the value is known by (an analysis's NABM act, a unit's symbol).</param>
public sealed record CodeTarget(string Value, string Label, IReadOnlyList<string>? Aliases = null);

/// <summary>A distinct source value of a coded column, how often it occurs, and where it goes.</summary>
public sealed record CorrespondenceRow(MigrationCodeList List, string SourceValue, int Count, string? Target,
                                       bool Suggested);

/// <summary>
/// The « Correspondances »: for each coded list, the old application's values and the MarocLIS
/// value each one becomes. Keyed by the normalised source value, so « m », « M » and « M. » are one
/// entry. Saved with the mapping profile, so the final import reuses what the trial settled.
/// </summary>
public sealed class CorrespondenceTables
{
    /// <summary>Target of a value whose rows are skipped (an old internal code, a test entry).</summary>
    public const string Ignore = "*IGNORER*";

    /// <summary>Target of a unit kept as the old application wrote it.</summary>
    public const string KeepAsIs = "*TEL_QUEL*";

    public Dictionary<MigrationCodeList, Dictionary<string, string>> Tables { get; set; } = [];

    public string? TargetOf(MigrationCodeList list, string? sourceValue)
    {
        var key = MigrationSchema.Normalize(sourceValue);
        if (key.Length == 0) return null;
        return Tables.TryGetValue(list, out var table) && table.TryGetValue(key, out var target)
                                                       && !string.IsNullOrWhiteSpace(target)
            ? target
            : null;
    }

    public void Set(MigrationCodeList list, string sourceValue, string? target)
    {
        var key = MigrationSchema.Normalize(sourceValue);
        if (key.Length == 0) return;

        if (!Tables.TryGetValue(list, out var table)) Tables[list] = table = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(target)) table.Remove(key);
        else table[key] = target;
    }

    public CorrespondenceTables Clone() => new()
    {
        Tables = Tables.ToDictionary(t => t.Key, t => new Dictionary<string, string>(t.Value, StringComparer.Ordinal))
    };
}

/// <summary>Proposes the MarocLIS value for a source value the tables do not cover yet.</summary>
public static class CorrespondenceSuggester
{
    private static readonly string[] Male = ["M", "H", "1", "MASCULIN", "MASC", "HOMME", "MALE", "MAN", "GARCON", "MR", "MONSIEUR", "SIEUR", "ذكر"];
    private static readonly string[] Female = ["F", "2", "FEMININ", "FEM", "FEMME", "FEMALE", "WOMAN", "FILLE", "MME", "MLLE", "MADAME", "MADEMOISELLE", "انثى", "أنثى"];
    private static readonly string[] Unknown = ["I", "0", "X", "U", "INCONNU", "INDETERMINE", "NC", "ND", "AUTRE"];

    /// <summary>The value <paramref name="sourceValue"/> most likely means, or null when nothing fits well.</summary>
    public static string? Suggest(MigrationCodeList list, string sourceValue, IReadOnlyList<CodeTarget> targets)
    {
        var source = MigrationSchema.Normalize(sourceValue);
        if (source.Length == 0) return null;

        switch (list)
        {
            case MigrationCodeList.Sex:
                if (Male.Select(MigrationSchema.Normalize).Contains(source)) return "M";
                if (Female.Select(MigrationSchema.Normalize).Contains(source)) return "F";
                if (Unknown.Select(MigrationSchema.Normalize).Contains(source)) return "U";
                return null;

            case MigrationCodeList.Specialty:
                return SuggestSpecialty(source, targets);
        }

        // A code, then a label, then an alias, equal once normalised.
        var exact = targets.FirstOrDefault(t => MigrationSchema.Normalize(t.Value) == source)
                    ?? targets.FirstOrDefault(t => MigrationSchema.Normalize(t.Label) == source)
                    ?? targets.FirstOrDefault(t => t.Aliases?.Any(a => MigrationSchema.Normalize(a) == source) == true);
        if (exact is not null) return exact.Value;

        if (list == MigrationCodeList.Analysis)
        {
            // « GLY » for « GLY/GLY »: the analysis code alone, when it has a single parameter.
            var byAnalysis = targets.Where(t => MigrationSchema.Normalize(t.Value.Split('/')[0]) == source).ToList();
            if (byAnalysis.Count == 1) return byAnalysis[0].Value;
        }

        if (list == MigrationCodeList.Payer)
        {
            // « AMO CNSS », « CNSS - AMO »: a payer whose code appears whole in the old label.
            var contained = targets.Where(t => MigrationSchema.Normalize(t.Value) is { Length: >= 3 } code
                                               && source.Contains(code, StringComparison.Ordinal)).ToList();
            if (contained.Count == 1) return contained[0].Value;
        }

        // A close label, unambiguous.
        var scored = targets.Select(t => (Target: t, Score: Math.Max(
                                  ColumnMatcher.Similarity(MigrationSchema.Normalize(t.Label), source),
                                  ColumnMatcher.Similarity(MigrationSchema.Normalize(t.Value), source))))
                            .Where(x => x.Score >= 0.8)
                            .OrderByDescending(x => x.Score)
                            .Take(2)
                            .ToList();

        if (scored.Count == 1 || (scored.Count == 2 && scored[0].Score - scored[1].Score >= 0.1))
            return scored[0].Target.Value;

        return null;
    }

    private static string? SuggestSpecialty(string source, IReadOnlyList<CodeTarget> targets)
    {
        string? kind =
            source.Contains("GENERAL", StringComparison.Ordinal) || source is "MG" or "GP" ? "GeneralPractitioner"
            : source.Contains("DENT", StringComparison.Ordinal) ? "Dentist"
            : source.Contains("SAGEFEMME", StringComparison.Ordinal) || source.Contains("MIDWIFE", StringComparison.Ordinal) ? "Midwife"
            : source.Contains("LABO", StringComparison.Ordinal) ? "Laboratory"
            : source.Contains("TRAVAIL", StringComparison.Ordinal) ? "OccupationalPhysician"
            : source.Contains("CLINIQUE", StringComparison.Ordinal) || source.Contains("HOPITAL", StringComparison.Ordinal)
              || source.Contains("CENTRE", StringComparison.Ordinal) || source.Contains("CHU", StringComparison.Ordinal)
              || source.Contains("CABINET", StringComparison.Ordinal) ? "Institution"
            : source.Length >= 3 ? "Specialist"
            : null;

        if (kind is null) return null;
        return targets.Any(t => t.Value == kind) || targets.Count == 0 ? kind : null;
    }

    /// <summary>
    /// The distinct values of the coded columns of <paramref name="table"/>, each with its count and
    /// its target: the one the tables hold, or a suggestion.
    /// </summary>
    public static IReadOnlyList<CorrespondenceRow> Rows(
        SourceTable table, IReadOnlyDictionary<string, string> columns, Domain.Migration.DataMigrationKind kind,
        CorrespondenceTables tables, IReadOnlyDictionary<MigrationCodeList, IReadOnlyList<CodeTarget>> targets)
    {
        var rows = new List<CorrespondenceRow>();

        foreach (var field in MigrationSchema.Fields(kind).Where(f => f.Type == MigrationFieldType.Coded))
        {
            if (!columns.TryGetValue(field.Key, out var column)) continue;
            var index = table.IndexOf(column);
            if (index < 0) continue;

            var counts = new Dictionary<string, (string Display, int Count)>(StringComparer.Ordinal);
            foreach (var row in table.Rows)
            {
                var value = index < row.Length ? row[index]?.Trim() : null;
                var key = MigrationSchema.Normalize(value);
                if (key.Length == 0) continue;
                counts[key] = counts.TryGetValue(key, out var seen) ? (seen.Display, seen.Count + 1) : (value!, 1);
            }

            var list = targets.GetValueOrDefault(field.CodeList) ?? [];

            foreach (var (_, (display, count)) in counts.OrderByDescending(c => c.Value.Count).ThenBy(c => c.Key, StringComparer.Ordinal))
            {
                if (rows.Any(r => r.List == field.CodeList && MigrationSchema.Normalize(r.SourceValue) == MigrationSchema.Normalize(display)))
                    continue;

                var target = tables.TargetOf(field.CodeList, display);
                var suggested = false;
                if (target is null)
                {
                    target = Suggest(field.CodeList, display, list);
                    suggested = target is not null;
                }

                rows.Add(new CorrespondenceRow(field.CodeList, display, count, target, suggested));
            }
        }

        return rows;
    }
}
