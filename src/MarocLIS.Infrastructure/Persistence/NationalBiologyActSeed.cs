using System.Globalization;
using System.Text;
using MarocLIS.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Persistence;

/// <summary>
/// The official nomenclature of medical biology acts, as shipped with the application: an embedded
/// copy of <c>nomenclature-biologie.csv</c> (492 acts, B100…B591, from arrêtés 1796-03, 827-12 and
/// 1968-16).
/// <para>
/// One source for every path that needs the list — the start-up seeder, a migration that wants to
/// insert it, and the tests — so a new install and an upgraded one end up with the same rows.
/// </para>
/// </summary>
public static class NationalBiologyActSeed
{
    public const string ResourceName = "MarocLIS.NationalBiologyActs.csv";

    /// <summary>One line of the shipped file.</summary>
    public sealed record Row(string Code, string Label, decimal Coefficient, string Chapter,
                             string? SubChapter, int DecreeYear);

    private static readonly Lazy<IReadOnlyList<Row>> Cached = new(Read);

    /// <summary>The acts of the shipped file, in code order.</summary>
    public static IReadOnlyList<Row> Rows => Cached.Value;

    private static IReadOnlyList<Row> Read()
    {
        using var stream = typeof(NationalBiologyActSeed).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var rows = new List<Row>();
        var header = true;

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            if (header) { header = false; continue; }

            var fields = Split(line);
            if (fields.Count != 6)
                throw new InvalidDataException($"{ResourceName}: 6 fields expected, {fields.Count} in « {line} ».");

            rows.Add(new Row(
                fields[0].Trim(),
                fields[1].Trim(),
                decimal.Parse(fields[2], NumberStyles.Number, CultureInfo.InvariantCulture),
                fields[3].Trim(),
                string.IsNullOrWhiteSpace(fields[4]) ? null : fields[4].Trim(),
                int.Parse(fields[5], CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    /// <summary>A « ; » separated line, with « " » quoting and « "" » for a quote inside a field.</summary>
    public static List<string> Split(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ';') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }

        fields.Add(current.ToString());
        return fields;
    }

    /// <summary>
    /// Inserts the acts a database lacks and brings the official fields of the others to the
    /// shipped text. Never deletes, and leaves <see cref="NationalBiologyAct.IsActive"/> alone.
    /// Idempotent: a second run changes nothing.
    /// </summary>
    /// <returns>The number of acts inserted or updated.</returns>
    public static async Task<int> SynchroniseAsync(LisDbContext context, CancellationToken ct = default)
    {
        var existing = await context.NationalBiologyActs.ToDictionaryAsync(a => a.Code, ct);
        var changed = 0;

        foreach (var row in Rows)
        {
            if (!existing.TryGetValue(row.Code, out var act))
            {
                context.NationalBiologyActs.Add(new NationalBiologyAct(row.Code, row.Label, row.Coefficient, row.DecreeYear)
                {
                    Chapter = row.Chapter,
                    SubChapter = row.SubChapter
                });
                changed++;
                continue;
            }

            if (act.Label == row.Label && act.Coefficient == row.Coefficient && act.Chapter == row.Chapter
                && act.SubChapter == row.SubChapter && act.DecreeYear == row.DecreeYear
                && act.LetterKey == NationalBiologyAct.BiologyLetterKey)
                continue;

            act.Label = row.Label;
            act.Coefficient = row.Coefficient;
            act.Chapter = row.Chapter;
            act.SubChapter = row.SubChapter;
            act.DecreeYear = row.DecreeYear;
            act.LetterKey = NationalBiologyAct.BiologyLetterKey;
            changed++;
        }

        if (changed > 0) await context.SaveChangesAsync(ct);

        return changed;
    }
}
