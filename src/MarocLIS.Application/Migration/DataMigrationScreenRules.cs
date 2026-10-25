using System.Globalization;
using System.Text;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Migration;

namespace MarocLIS.Application.Migration;

/// <summary>Where a value of a coded column stands: settled, suggested and to confirm, still missing, or its rows set aside.</summary>
public enum CorrespondenceState
{
    Settled,
    Suggested,
    Missing,
    Ignored
}

/// <summary>A choice of a drop-down of « Reprise de données »: the value kept, the caption read.</summary>
public sealed record MigrationChoice(string Value, string Display);

/// <summary>A line of the « Rapport d'anomalies », written in the operator's language.</summary>
public sealed record MigrationReportLine(string Row, string Field, MigrationSeverity Severity, string Problem, string Suggestion);

/// <summary>A line of « Ancien n° → patient »: the patient, and one antériorité reprise when it has any.</summary>
public sealed record OldNumberLine(string Record, string Patient, string Birth, string OldNumber, string Date, string Analysis,
                                   string Value, string Range, string Accession);

/// <summary>
/// The rules of « Reprise de données » that need no window: which file is a workbook, how the columns
/// are matched from a profile and automatically, the state and the choices of a correspondence, what
/// is refused before a « Simulation » or an « Importer », the questions asked before an import and an
/// undo, how the report, the journal, the old numbers and the balances read, and how a profile is
/// drawn from the screen. The workstation's DataMigrationView and the web's screen apply the same
/// ones; reading, planning, writing, undoing and journalling stay DataMigrationService's.
/// </summary>
public static class DataMigrationScreenRules
{
    /// <summary>A field not taken from the source; a correspondence not chosen.</summary>
    public const string NotMapped = "";

    /// <summary>The most report lines shown; the export carries them all.</summary>
    public const int ReportLinesShown = 5000;

    /// <summary>A long operation runs with the migration right; the kind, the profile and the templates can be looked at without it.</summary>
    public static bool CanRun(ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Has(Permissions.Administration.Migration);
    }

    // ---- Source --------------------------------------------------------------------------------

    /// <summary>An .xlsx is read sheet by sheet; anything else as delimited text.</summary>
    public static bool IsWorkbook(string fileName) =>
        string.Equals(Path.GetExtension(fileName), ".xlsx", StringComparison.OrdinalIgnoreCase);

    /// <summary>The extensions a source file may have.</summary>
    public static readonly IReadOnlyList<string> SourceExtensions = [".xlsx", ".csv", ".txt"];

    /// <summary>
    /// The largest source file the web takes from a browser: the import reads at most
    /// <see cref="DataMigrationService.MaxRows"/> rows, which no export of that many rows exceeds.
    /// </summary>
    public const long MaxSourceFileBytes = 200L * 1024 * 1024;

    /// <summary>An .xlsx, a .csv or a .txt, whatever the case: the files « Parcourir… » offers.</summary>
    public static bool IsSourceFile(string fileName) =>
        SourceExtensions.Contains(Path.GetExtension(fileName ?? string.Empty), StringComparer.OrdinalIgnoreCase);

    /// <summary>A source file sent from a browser is read only within <see cref="MaxSourceFileBytes"/>.</summary>
    public static bool IsReadableSourceSize(long bytes) => bytes is > 0 and <= MaxSourceFileBytes;

    /// <summary>« patients.xlsx › Feuil1 », or the file's name for a text file.</summary>
    public static string FileLabel(string fileName, string? sheet) =>
        sheet is null ? Path.GetFileName(fileName) : $"{Path.GetFileName(fileName)} › {sheet}";

    /// <summary>The sheet a profile read this kind from, when the workbook has it; the first one otherwise.</summary>
    public static string? PreferredSheet(IReadOnlyList<string> sheets, MigrationProfile? profile, DataMigrationKind kind)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        if (sheets.Count == 0) return null;

        var preferred = profile?.For(kind).Sheet;
        return preferred is not null && sheets.Contains(preferred) ? preferred : sheets[0];
    }

    /// <summary>Where the rows come from, as the journal keeps it: cut at its column's length.</summary>
    public static string SourceLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        return label.Length > MigrationBatch.SourceLabelMaxLength ? label[..MigrationBatch.SourceLabelMaxLength] : label;
    }

    /// <summary>« 120 ligne(s), 9 colonne(s) lues ».</summary>
    public static string RowsRead(SourceTable? source, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return source is null ? string.Empty : text("Migration.RowsRead", source.Rows.Count, source.Columns.Count);
    }

    /// <summary>The first rows of the source, as the « Aperçu » shows them.</summary>
    public static IEnumerable<string?[]> Preview(SourceTable source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Rows.Take(DataMigrationService.PreviewRows);
    }

    // ---- Columns -------------------------------------------------------------------------------

    /// <summary>
    /// The saved matching of the profile where its columns exist, the automatic one for the rest; a
    /// field the profile saved as « non reprise » stays so.
    /// </summary>
    public static Dictionary<string, string> MatchColumns(DataMigrationKind kind, SourceTable? source, MigrationProfile? profile)
    {
        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source is null) return columns;

        var automatic = ColumnMatcher.AutoMatch(kind, source.Columns);
        var saved = profile?.For(kind).Columns ?? [];

        foreach (var field in MigrationSchema.Fields(kind))
        {
            if (saved.TryGetValue(field.Key, out var column) && source.IndexOf(column) >= 0) columns[field.Key] = source.Columns[source.IndexOf(column)];
            else if (saved.ContainsKey(field.Key) && string.IsNullOrEmpty(saved[field.Key])) continue;
            else if (automatic.TryGetValue(field.Key, out var matched)) columns[field.Key] = matched;
        }

        return columns;
    }

    /// <summary>Sets or clears the column a field is read from.</summary>
    public static void SetColumn(Dictionary<string, string> columns, string fieldKey, string? column)
    {
        ArgumentNullException.ThrowIfNull(columns);

        if (string.IsNullOrEmpty(column)) columns.Remove(fieldKey);
        else columns[fieldKey] = column;
    }

    /// <summary>The first values of a column, for the operator to recognise it.</summary>
    public static string Sample(SourceTable? source, string? column)
    {
        if (source is null || string.IsNullOrEmpty(column)) return string.Empty;
        var index = source.IndexOf(column);
        if (index < 0) return string.Empty;

        var values = source.Rows.Select(r => index < r.Length ? r[index] : null)
                           .Where(v => !string.IsNullOrWhiteSpace(v))
                           .Take(3)
                           .ToList();
        return string.Join(" · ", values);
    }

    /// <summary>The columns a field can be read from: « Non reprise », then the source's.</summary>
    public static IReadOnlyList<MigrationChoice> ColumnChoices(SourceTable? source, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var choices = new List<MigrationChoice> { new(NotMapped, text("Migration.NotMapped")) };
        if (source is not null) choices.AddRange(source.Columns.Select(c => new MigrationChoice(c, c)));
        return choices;
    }

    /// <summary>A required field read from no column: said in the alarm's colour.</summary>
    public static bool IsMissing(MigrationField field, IReadOnlyDictionary<string, string> columns)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(columns);
        return field.Required && string.IsNullOrEmpty(columns.GetValueOrDefault(field.Key));
    }

    /// <summary>« 8 champ(s) repris sur 18 ».</summary>
    public static string ColumnsMapped(DataMigrationKind kind, IReadOnlyDictionary<string, string> columns, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(text);
        return text("Migration.ColumnsMapped", columns.Count, MigrationSchema.Fields(kind).Count);
    }

    // ---- Correspondences -----------------------------------------------------------------------

    /// <summary>The target a value is shown with: the one held or suggested, a unit kept as it is otherwise.</summary>
    public static string InitialTarget(CorrespondenceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Target ?? (row.List == MigrationCodeList.Unit ? CorrespondenceTables.KeepAsIs : NotMapped);
    }

    /// <summary>
    /// A value with no target is missing — but for a unit, which is then kept as written; a value
    /// whose rows are set aside is ignored; a suggestion stays to confirm until chosen.
    /// </summary>
    public static CorrespondenceState State(MigrationCodeList list, string target, bool suggested)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.Length == 0) return list == MigrationCodeList.Unit ? CorrespondenceState.Settled : CorrespondenceState.Missing;
        if (target == CorrespondenceTables.Ignore) return CorrespondenceState.Ignored;
        return suggested ? CorrespondenceState.Suggested : CorrespondenceState.Settled;
    }

    /// <summary>
    /// What a value of <paramref name="list"/> can become, in the operator's language: « Choisir… »,
    /// the MarocLIS values, « Garder tel quel » for a unit and « Ignorer ces lignes » for an analysis
    /// or a payer — and the current target when it is none of them, so it is not lost.
    /// </summary>
    public static IReadOnlyList<MigrationChoice> TargetChoices(
        MigrationCodeList list, IReadOnlyDictionary<MigrationCodeList, IReadOnlyList<CodeTarget>> targets,
        string? current, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(text);

        var choices = new List<MigrationChoice> { new(NotMapped, text("Migration.ChooseTarget")) };

        foreach (var target in targets.GetValueOrDefault(list) ?? [])
        {
            var display = list switch
            {
                MigrationCodeList.Sex => text($"Migration.Sex.{target.Value}"),
                MigrationCodeList.Specialty => text($"PrescriberKind.{target.Value}"),
                _ => target.Label
            };
            choices.Add(new MigrationChoice(target.Value, display));
        }

        if (list == MigrationCodeList.Unit) choices.Add(new MigrationChoice(CorrespondenceTables.KeepAsIs, text("Migration.KeepAsIs")));
        if (list is MigrationCodeList.Analysis or MigrationCodeList.Payer)
            choices.Add(new MigrationChoice(CorrespondenceTables.Ignore, text("Migration.IgnoreRows")));

        if (!string.IsNullOrEmpty(current) && choices.All(c => c.Value != current)) choices.Add(new MigrationChoice(current, current));
        return choices;
    }

    /// <summary>
    /// The correspondences the request is made with: the tables, and every target shown, the
    /// suggestions accepted as they stand.
    /// </summary>
    public static CorrespondenceTables Settled(CorrespondenceTables tables,
                                               IEnumerable<(MigrationCodeList List, string SourceValue, string? Target)> shown)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(shown);

        var settled = tables.Clone();
        foreach (var (list, source, target) in shown)
        {
            if (string.IsNullOrEmpty(target)) continue;
            settled.Set(list, source, target);
        }
        return settled;
    }

    /// <summary>« 6 valeur(s) · 2 sans correspondance », or that no coded column is read.</summary>
    public static string CorrespondencesCount(int rows, int missing, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return rows == 0 ? text("Migration.NoCodedValues") : text("Migration.CorrespondencesCount", rows, missing);
    }

    // ---- Simulation and import -----------------------------------------------------------------

    /// <summary>
    /// What is refused before a simulation or an import, as a resource key: a source read, and the
    /// old application named, its name being part of every source key.
    /// </summary>
    public static string? RequestRefusal(SourceTable? source, string? sourceSystem)
    {
        if (source is null) return "Migration.ReadSourceFirst";
        if (string.IsNullOrWhiteSpace(sourceSystem)) return "Migration_SourceSystemRequired";
        return null;
    }

    /// <summary>The request of a simulation or an import, from what the screen holds.</summary>
    public static MigrationRequest Request(DataMigrationKind kind, string sourceSystem, string sourceLabel, SourceTable source,
                                           IReadOnlyDictionary<string, string> columns, CorrespondenceTables settled,
                                           bool linkByNameAndBirthDate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(columns);

        return new MigrationRequest
        {
            Kind = kind,
            SourceSystem = sourceSystem.Trim(),
            SourceLabel = sourceLabel,
            Table = source,
            Columns = new Dictionary<string, string>(columns, StringComparer.Ordinal),
            Correspondences = settled,
            LinkByNameAndBirthDate = linkByNameAndBirthDate
        };
    }

    public static int Blocking(MigrationAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return analysis.Anomalies.Count(a => a.Severity == MigrationSeverity.Blocking);
    }

    /// <summary>
    /// Why « Importer » is refused before asking: no simulation of the settings as they stand, or a
    /// simulation that found a source value with no correspondence. Null when it may be asked.
    /// </summary>
    public static string? ImportRefusal(MigrationAnalysis? analysis, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (analysis is null) return text("Migration.SimulateFirst");
        if (analysis.IsBlocked) return text("Migration_ImportBlocked", Blocking(analysis));
        return null;
    }

    /// <summary>« Importer » is offered after a simulation that is not blocked and would write something.</summary>
    public static bool CanImport(ICurrentUser user, MigrationAnalysis? analysis)
    {
        ArgumentNullException.ThrowIfNull(user);
        return analysis is { IsBlocked: false } a && a.ToCreate + a.ToUpdate > 0 && CanRun(user);
    }

    /// <summary>The question asked before the import, with what the simulation counted.</summary>
    public static string ConfirmImport(MigrationAnalysis analysis, DataMigrationKind kind, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(text);

        return text("Migration.ConfirmImport", analysis.ToCreate, analysis.ToUpdate, analysis.Skipped, analysis.InError,
                    text($"MigrationKind.{kind}"));
    }

    /// <summary>What the import did, or that it was stopped between two batches.</summary>
    public static string SummaryMessage(MigrationSummary summary, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(text);

        return text(summary.Interrupted ? "Migration.SummaryInterrupted" : "Migration.Summary",
                    text($"MigrationKind.{summary.Kind}"), summary.Created, summary.Updated, summary.Skipped, summary.Failed);
    }

    /// <summary>« Lecture… 1 200 / 5 000 »: the step and how far it is.</summary>
    public static string Progress(MigrationProgress progress, ScreenText text, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(text);

        var format = culture ?? CultureInfo.CurrentCulture;
        return $"{text(progress.StepKey)} {progress.Done.ToString("N0", format)} / {progress.Total.ToString("N0", format)}";
    }

    /// <summary>How far a step is, in thousandths.</summary>
    public static int Thousandths(MigrationProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return progress.Total == 0 ? 0 : (int)Math.Clamp(1000L * progress.Done / progress.Total, 0, 1000);
    }

    /// <summary>
    /// The line under the screen when nothing runs: read a source; simulate; the import is blocked;
    /// ready, with what it would create and update; imported. Critical while blocked.
    /// </summary>
    public static (string Text, bool Critical) Status(SourceTable? source, MigrationAnalysis? analysis, MigrationSummary? summary,
                                                      ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (source is null) return (text("Migration.StatusStart"), false);
        if (summary is not null) return (text("Migration.StatusImported"), false);
        if (analysis is null) return (text("Migration.StatusSimulate"), false);
        if (analysis.IsBlocked) return (text("Migration_ImportBlocked", Blocking(analysis)), true);
        return (text("Migration.StatusReady", analysis.ToCreate, analysis.ToUpdate), false);
    }

    // ---- Report --------------------------------------------------------------------------------

    /// <summary>The anomalies shown: the import's when it ran, the simulation's otherwise.</summary>
    public static IReadOnlyList<MigrationAnomaly> Anomalies(MigrationSummary? summary, MigrationAnalysis? analysis) =>
        summary?.Anomalies ?? analysis?.Anomalies ?? [];

    /// <summary>The report's lines, by row and the most serious first, at most <see cref="ReportLinesShown"/>.</summary>
    public static IReadOnlyList<MigrationReportLine> ReportLines(IEnumerable<MigrationAnomaly> anomalies, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(anomalies);
        ArgumentNullException.ThrowIfNull(text);

        return [.. anomalies.OrderBy(a => a.Row).ThenByDescending(a => a.Severity).Take(ReportLinesShown)
                            .Select(a => new MigrationReportLine(
                                a.Row == 0 ? "—" : a.Row.ToString(CultureInfo.InvariantCulture),
                                a.FieldKey is null ? "—" : text($"Migration.Field.{a.FieldKey}"),
                                a.Severity, Problem(a, text), Suggestion(a, text)))];
    }

    /// <summary>« 12 à créer · 3 à mettre à jour… » after a simulation, what was written after an import.</summary>
    public static (string Text, bool Critical) ReportCounts(MigrationSummary? summary, MigrationAnalysis? analysis, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (summary is not null)
            return (text("Migration.ImportCounts", summary.Created, summary.Updated, summary.Skipped, summary.Failed), summary.Interrupted);

        if (analysis is not null)
            return (text("Migration.SimulationCounts", analysis.ToCreate, analysis.ToUpdate, analysis.Skipped, analysis.InError,
                         Blocking(analysis)), analysis.IsBlocked);

        return (text("Migration.NoSimulation"), false);
    }

    public static string Problem(MigrationAnomaly anomaly, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(anomaly);
        ArgumentNullException.ThrowIfNull(text);
        return text(anomaly.ProblemKey, Args(anomaly.ProblemArgs, text));
    }

    public static string Suggestion(MigrationAnomaly anomaly, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(anomaly);
        ArgumentNullException.ThrowIfNull(text);
        return anomaly.SuggestionKey is null ? string.Empty : text(anomaly.SuggestionKey, Args(anomaly.SuggestionArgs, text));
    }

    /// <summary>Arguments naming a field or a list are resource keys: shown in the operator's language; amounts as money.</summary>
    private static object?[] Args(object?[]? args, ScreenText text) =>
    [
        .. (args ?? []).Select(a => a is string s && (s.StartsWith("Migration.Field.", StringComparison.Ordinal)
                                                      || s.StartsWith("MigrationCodeList.", StringComparison.Ordinal))
            ? text(s)
            : a is decimal d ? d.ToString("N2", CultureInfo.CurrentCulture) : a)
    ];

    /// <summary>The anomaly report as a « ; » CSV a French Excel opens: row, field, severity, problem, suggestion.</summary>
    public static byte[] ReportCsv(IReadOnlyList<MigrationAnomaly> anomalies, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(anomalies);
        ArgumentNullException.ThrowIfNull(text);

        var csv = new StringBuilder();
        csv.Append(CsvSource.Line([text("Migration.ColRow"), text("Migration.ColField"), text("Migration.ColSeverity"),
                                   text("Migration.ColProblem"), text("Migration.ColSuggestion")])).Append("\r\n");
        foreach (var anomaly in anomalies.OrderBy(a => a.Row))
            csv.Append(CsvSource.Line([
                anomaly.Row == 0 ? string.Empty : anomaly.Row.ToString(CultureInfo.InvariantCulture),
                anomaly.FieldKey is null ? string.Empty : text($"Migration.Field.{anomaly.FieldKey}"),
                text($"MigrationSeverity.{anomaly.Severity}"),
                Problem(anomaly, text),
                Suggestion(anomaly, text)
            ])).Append("\r\n");
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    /// <summary>« rapport-anomalies-patients-20261025-1430.csv ».</summary>
    public static string ReportFileName(DataMigrationKind kind, DateTime now) =>
        $"rapport-anomalies-{kind.ToString().ToLowerInvariant()}-{now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.csv";

    // ---- Profiles ------------------------------------------------------------------------------

    /// <summary>The name a profile is saved under: the current one's, or the old application's.</summary>
    public static string? SuggestedProfileName(MigrationProfile? current, string? sourceSystem) =>
        current?.Name ?? (sourceSystem?.Trim() is { Length: > 0 } system ? system : null);

    /// <summary>« profil-reprise-LABOWIN.json ».</summary>
    public static string ProfileFileName(MigrationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return $"profil-reprise-{profile.Name}.json";
    }

    /// <summary>A profile file far larger than any profile is not read.</summary>
    public static bool IsReadableProfileSize(long bytes) => bytes <= MigrationProfile.MaxFileBytes;

    /// <summary>
    /// The screen as a profile: the old application, the correspondences settled, the connection
    /// without its password, and for the kind shown every field's column — mapped or not, an empty
    /// one saying « non reprise » next time too — and the sheet or the table it was read from.
    /// </summary>
    public static MigrationProfile ToProfile(MigrationProfile? current, string name, DataMigrationKind kind, string sourceSystem,
                                             bool linkByNameAndBirthDate, CorrespondenceTables settled,
                                             IReadOnlyDictionary<string, string> columns, bool sourceRead,
                                             string? sheet, bool fromFile,
                                             SourceConnectionSettings? connection = null, SourceTableInfo? table = null,
                                             string? query = null)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var profile = current is not null && current.Name == name ? current : new MigrationProfile();
        profile.Name = name;
        profile.SourceSystem = sourceSystem.Trim();
        profile.LinkByNameAndBirthDate = linkByNameAndBirthDate;
        profile.Correspondences = settled;

        if (connection is not null)
        {
            profile.Connection = connection.Clone();
            profile.Connection.Password = null;
        }

        var saved = profile.For(kind);
        if (sourceRead)
            saved.Columns = MigrationSchema.Fields(kind)
                .ToDictionary(f => f.Key, f => columns.GetValueOrDefault(f.Key) ?? NotMapped, StringComparer.Ordinal);

        if (fromFile) saved.Sheet = sheet;
        if (connection is not null)
        {
            saved.TableSchema = table?.Schema;
            saved.TableName = table?.Name;
            saved.TableIsView = table?.IsView ?? false;
            saved.Query = table is null ? query : null;
        }

        return profile;
    }

    // ---- Templates -----------------------------------------------------------------------------

    /// <summary>
    /// Every template, in Excel and in CSV, under its file name: the « Tous les modèles » the
    /// workstation writes into a folder. <paramref name="workbook"/> writes the Excel ones.
    /// </summary>
    public static IReadOnlyList<(string FileName, Func<byte[]> Content)> AllTemplates(Func<DataMigrationKind, byte[]> workbook)
    {
        ArgumentNullException.ThrowIfNull(workbook);

        return [.. Enum.GetValues<DataMigrationKind>().SelectMany(kind => new (string, Func<byte[]>)[]
        {
            (MigrationTemplates.FileName(kind, ".xlsx"), () => workbook(kind)),
            (MigrationTemplates.FileName(kind, ".csv"), () => MigrationTemplates.Csv(kind))
        })];
    }

    // ---- Journal, old numbers, balances --------------------------------------------------------

    /// <summary>A run is undone, interrupted, still running or done: the key of its state.</summary>
    public static string BatchState(MigrationBatchRow batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var state = batch.Undone ? "Undone" : batch.Interrupted ? "Interrupted" : batch.CompletedAt is null ? "Running" : "Done";
        return $"Migration.Batch{state}";
    }

    /// <summary>« Annuler la dernière reprise » is offered while a finished run created something and is not undone.</summary>
    public static bool CanUndo(ICurrentUser user, IEnumerable<MigrationBatchRow> batches)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(batches);
        return batches.Any(b => !b.Undone && b.CompletedAt is not null && b.Created > 0) && CanRun(user);
    }

    /// <summary>The run an undo would take back: the newest finished one not undone.</summary>
    public static MigrationBatchRow? LastUndoable(IEnumerable<MigrationBatchRow> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        return batches.FirstOrDefault(b => !b.Undone && b.CompletedAt is not null);
    }

    /// <summary>The question asked before an undo: the kind, when it ran, how many records it created.</summary>
    public static string ConfirmUndo(MigrationBatchRow batch, ScreenText text, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(text);

        return text("Migration.ConfirmUndo", text($"MigrationKind.{batch.Kind}"),
                    batch.StartedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", culture ?? CultureInfo.CurrentCulture), batch.Created);
    }

    /// <summary>The patients an old number leads to, one line per antériorité reprise, or one for a patient with none.</summary>
    public static IReadOnlyList<OldNumberLine> OldNumberLines(IEnumerable<OldNumberMatch> matches, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(matches);

        var format = culture ?? CultureInfo.CurrentCulture;
        var lines = new List<OldNumberLine>();

        foreach (var match in matches)
        {
            var birth = match.DateOfBirth?.ToString("dd/MM/yyyy", format) ?? string.Empty;
            if (match.History.Count == 0)
            {
                lines.Add(new OldNumberLine(match.RecordNumber, match.FullName, birth, match.OldNumber ?? string.Empty,
                                            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty));
                continue;
            }

            foreach (var result in match.History)
                lines.Add(new OldNumberLine(match.RecordNumber, match.FullName, birth, match.OldNumber ?? string.Empty,
                                            result.Date.ToLocalTime().ToString("dd/MM/yyyy", format),
                                            $"{result.ParameterCode} — {result.ParameterName}",
                                            result.Unit is null ? result.Value : $"{result.Value} {result.Unit}",
                                            result.ReferenceRange ?? string.Empty, result.OldAccessionNumber ?? string.Empty));
        }

        return lines;
    }

    /// <summary>« 3 solde(s) · 1 250,00 ».</summary>
    public static string BalancesTotal(IReadOnlyList<OpeningBalanceRow> balances, ScreenText text, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(balances);
        ArgumentNullException.ThrowIfNull(text);

        return text("Migration.BalancesTotal", balances.Count,
                    balances.Sum(b => b.Amount).ToString("N2", culture ?? CultureInfo.CurrentCulture));
    }
}
