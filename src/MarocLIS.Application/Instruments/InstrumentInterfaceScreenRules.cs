using System.Globalization;
using System.Text;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Instruments;

/// <summary>How a line of « Interfaces automates » reads: the workstation's cell colours and pills.</summary>
public enum InterfaceTone
{
    Plain,

    /// <summary>Switched off or ignored: grey.</summary>
    Muted,

    /// <summary>Still in progress (a link starting, a frame received but not yet read).</summary>
    Info,

    /// <summary>Up, or imported: said in the normal colour.</summary>
    Ok,

    /// <summary>Somebody has to look: said in the warning's colour.</summary>
    Warning,

    /// <summary>Down, unusable or dangerous: filled in the alarm's colours.</summary>
    Critical
}

/// <summary>
/// What « Interfaces automates » offers on the selection, by right — the workstation's RefreshActions.
/// Disabled is a hint, not the control: the service re-checks each right and the sink each status.
/// </summary>
/// <param name="ToggleKey">« Activer » or « Désactiver », as the selected link stands.</param>
public sealed record InterfaceActions(
    bool NewLink, bool EditLink, bool EditMappings, bool TestFrame, bool Toggle, string ToggleKey,
    bool ViewRaw, bool Replay, bool PurgeLog);

/// <summary>
/// The rules of « Interfaces automates » that need no window: what is offered by which right, how a
/// link, a mapping and a logged frame read (their tone, their flags, their counts), how a frame is
/// shown verbatim with its control characters named, what a purge says before it runs, and how a
/// « Tester une trame » line is written. The workstation's InstrumentInterfaceView and the web's
/// screen apply the same ones; every decision on saving, replaying and purging stays
/// InstrumentInterfaceService's, the sink's and the previewer's.
/// </summary>
public static class InstrumentInterfaceScreenRules
{
    /// <summary>How many logged frames the screen reads for the selected link.</summary>
    public const int MessagesShown = 200;

    /// <summary>How far back « Charger la dernière trame » and the INTEGRA sample look.</summary>
    public const int RecentMessages = 50;

    /// <summary>The longest reason a link is switched on or off with.</summary>
    public const int ToggleReasonMaxLength = 500;

    /// <summary>The longest reason a purge is recorded with in the security journal.</summary>
    public const int PurgeReasonMaxLength = 1000;

    /// <summary>
    /// Configuring is one right and starting or stopping a link another; reading a raw frame or
    /// testing one also takes the results right, since both name patients and their values. The
    /// purge acts on the whole log by age, so it needs no selected line.
    /// </summary>
    public static InterfaceActions Actions(ICurrentUser user, InterfaceRow? link, MessageLogRow? message)
    {
        ArgumentNullException.ThrowIfNull(user);

        var configure = user.Has(Permissions.Instruments.Configure);
        var results = user.Has(Permissions.Results.View);

        return new InterfaceActions(
            NewLink: configure,
            EditLink: configure && link is not null,
            EditMappings: configure && link is not null,
            TestFrame: configure && link is not null && results,
            Toggle: link is not null && user.Has(Permissions.Instruments.StartStop),
            ToggleKey: link is { IsEnabled: true } ? "Interface.Disable" : "Interface.Enable",
            ViewRaw: message is not null && results,
            Replay: message is { CanReplay: true } && user.Has(Permissions.Instruments.ReplayMessages),
            PurgeLog: user.Has(Permissions.Administration.RunMaintenance));
    }

    /// <summary>
    /// The analysers a new link can be for: an analyser, with no link yet, by code. Chosen before the
    /// link is described, so an empty choice is said before a whole frame configuration is typed.
    /// </summary>
    public static IReadOnlyList<InstrumentMasterRow> FreeInstruments(IEnumerable<InstrumentMasterRow> instruments)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        return [.. instruments.Where(i => !i.HasInterfaceConfigured && i.Kind == EquipmentKind.Analyser)
                              .OrderBy(i => i.Code, StringComparer.Ordinal)];
    }

    // ---- Links ---------------------------------------------------------------------------------

    /// <summary>
    /// The live state of a link, as a key: « Non piloté ici » where this process does not drive the
    /// analysers — a stopped link there means "not driven from here", not "not working" — then
    /// « Désactivée », « Démarrage… » before the host has reported, and the host's own state.
    /// </summary>
    /// <param name="drivenHere">Whether this process runs the instrument host.</param>
    /// <param name="liveStatus">The host's state of the link (Stopped, Running, Failed, Misconfigured), null before it reported.</param>
    public static string LiveKey(bool drivenHere, InterfaceRow row, string? liveStatus)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!drivenHere) return "Interface.NotDrivenHere";
        if (!row.IsEnabled) return "Interface.Disabled";

        return liveStatus is null ? "Interface.Starting" : $"InstrumentLinkStatus.{liveStatus}";
    }

    /// <summary>The pill of the live state: up, down, waiting, or still starting.</summary>
    public static InterfaceTone LiveTone(bool drivenHere, InterfaceRow row, string? liveStatus)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!drivenHere || !row.IsEnabled) return InterfaceTone.Muted;

        return liveStatus switch
        {
            "Running" => InterfaceTone.Ok,
            "Failed" or "Misconfigured" => InterfaceTone.Critical,
            "Stopped" => InterfaceTone.Muted,
            _ => InterfaceTone.Info
        };
    }

    /// <summary>
    /// A link down, unusable or whose analyser may not produce results is filled in the alarm's
    /// colours; recent failures or a recorded error are said in the warning's; a running link in the
    /// normal one; a disabled link is grey whatever else is true of it.
    /// </summary>
    public static InterfaceTone Tone(InterfaceRow row, string? liveStatus)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!row.IsEnabled) return InterfaceTone.Muted;

        if (liveStatus is "Failed" or "Misconfigured" || row.IsUnusable || row.EquipmentBlockKey is not null)
            return InterfaceTone.Critical;

        if (row.HasRecentFailures || row.LastError is { Length: > 0 }) return InterfaceTone.Warning;

        return liveStatus == "Running" ? InterfaceTone.Ok : InterfaceTone.Plain;
    }

    /// <summary>What is wrong or worth knowing about a link: blocked analyser, off, no mappings, auto-accepting, an error.</summary>
    public static IReadOnlyList<string> Flags(InterfaceRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);

        var flags = new List<string>(5);

        if (row.EquipmentBlockKey is { } blocked) flags.Add(text(blocked, row.InstrumentCode));
        if (!row.IsEnabled) flags.Add(text("Interface.Disabled"));
        if (row.IsUnusable) flags.Add(text("Interface.NoMappings"));
        if (row.AutoAcceptResults) flags.Add(text("Interface.AutoAccept"));
        if (row.LastError is { Length: > 0 }) flags.Add(text("Interface.HasError"));

        return flags;
    }

    /// <summary>« 7 », or « 5/7 » when some codes are switched off.</summary>
    public static string MappingCount(InterfaceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row.EnabledMappingCount == row.MappingCount
            ? row.MappingCount.ToString(CultureInfo.InvariantCulture)
            : $"{row.EnabledMappingCount}/{row.MappingCount}";
    }

    /// <summary>« 12 », or « 12 (3) » with today's failed frames.</summary>
    public static string MessagesToday(InterfaceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row.FailedToday > 0
            ? $"{row.MessagesToday} ({row.FailedToday})"
            : row.MessagesToday.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>When the last frame arrived, in local time; « — » before the first.</summary>
    public static string LastMessage(InterfaceRow row, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.LastMessageAt?.ToLocalTime().ToString("dd/MM HH:mm", culture ?? CultureInfo.CurrentCulture) ?? "—";
    }

    // ---- Mappings ------------------------------------------------------------------------------

    /// <summary>A switched-off code, a unit mismatch with no conversion, a coded parameter with no value map.</summary>
    public static IReadOnlyList<string> Flags(MappingRow mapping, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(text);

        var flags = new List<string>(3);

        if (!mapping.IsEnabled) flags.Add(text("Interface.MappingDisabled"));
        if (mapping.UnitMismatchWithoutConversion) flags.Add(text("Interface.UnitMismatch"));
        if (mapping.CodedWithoutMap) flags.Add(text("Interface.NoCodeMap"));

        return flags;
    }

    /// <summary>
    /// The unit mismatch with no conversion is filled in the alarm's colours: the one mapping fault
    /// that produces a plausible wrong number rather than an error.
    /// </summary>
    public static InterfaceTone Tone(MappingRow mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        if (!mapping.IsEnabled) return InterfaceTone.Muted;
        if (mapping.UnitMismatchWithoutConversion) return InterfaceTone.Critical;
        return mapping.CodedWithoutMap ? InterfaceTone.Warning : InterfaceTone.Plain;
    }

    /// <summary>The catalogue's unit, or « mmol/L → g/L » when the analyser reports another.</summary>
    public static string Units(MappingRow mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        return mapping.InstrumentUnit is { Length: > 0 } instrumentUnit
               && !string.Equals(instrumentUnit, mapping.CatalogueUnit, StringComparison.OrdinalIgnoreCase)
            ? $"{instrumentUnit} → {mapping.CatalogueUnit ?? "—"}"
            : mapping.CatalogueUnit ?? string.Empty;
    }

    /// <summary>« ×0,0555 », « ×1 +2,5 »: the conversion applied to the analyser's value.</summary>
    public static string Conversion(MappingRow mapping, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        if (mapping.ConversionFactor is not { } factor) return string.Empty;

        var offset = mapping.ConversionOffset is { } value and not 0m
            ? " " + value.ToString("+0.###;-0.###", culture ?? CultureInfo.CurrentCulture)
            : string.Empty;

        return "×" + factor.ToString("0.####", culture ?? CultureInfo.CurrentCulture) + offset;
    }

    /// <summary>« GLY/GLY »: the analysis and the parameter a code feeds.</summary>
    public static string Parameter(MappingRow mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return $"{mapping.AnalysisCode}/{mapping.ParameterCode}";
    }

    // ---- Message log ---------------------------------------------------------------------------

    /// <summary>A failed frame in the alarm's colours, a partial or held one in the warning's, an imported one normal, an ignored one grey.</summary>
    public static InterfaceTone Tone(MessageLogRow message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message.Status switch
        {
            InstrumentMessageProcessingStatus.Failed => InterfaceTone.Critical,
            InstrumentMessageProcessingStatus.PartiallyImported
                or InstrumentMessageProcessingStatus.AwaitingAcceptance => InterfaceTone.Warning,
            InstrumentMessageProcessingStatus.Imported => InterfaceTone.Ok,
            InstrumentMessageProcessingStatus.Ignored => InterfaceTone.Muted,
            _ => InterfaceTone.Plain
        };
    }

    /// <summary>The pill of a frame's status: a frame received but not yet read is still in progress.</summary>
    public static InterfaceTone StatusTone(MessageLogRow message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message.Status switch
        {
            InstrumentMessageProcessingStatus.Received => InterfaceTone.Info,
            InstrumentMessageProcessingStatus.Ignored => InterfaceTone.Muted,
            _ => Tone(message)
        };
    }

    /// <summary>The frame's status, « · Rejouée » after a replay.</summary>
    public static string Status(MessageLogRow message, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(text);

        return text($"InstrumentMessageProcessingStatus.{message.Status}")
               + (message.WasReplayed ? $" · {text("Interface.Replayed")}" : string.Empty);
    }

    /// <summary>« 1/2 »: results imported out of those the frame carried; empty for a frame with none.</summary>
    public static string Counts(MessageLogRow message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.ResultCount is { } total ? $"{message.ImportedCount ?? 0}/{total}" : string.Empty;
    }

    /// <summary>When the frame went over the wire, in local time, to the second.</summary>
    public static string When(MessageLogRow message, string format = "dd/MM HH:mm:ss", IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.OccurredAt.ToLocalTime().ToString(format, culture ?? CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// The frames somebody can still act on — failed, partially imported or held for acceptance —
    /// the rejected and unmatched results a replay can still bring into a patient file.
    /// </summary>
    public static IReadOnlyList<MessageLogRow> ToDealWith(IEnumerable<MessageLogRow> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return [.. messages.Where(m => m.CanReplay)];
    }

    /// <summary>
    /// The issues of a frame in the operator's language. They are stored as resource keys with a
    /// tab-separated detail, so the log reads in whichever language the operator uses rather than
    /// the one the sink wrote in. A message that names its value takes the detail in place; the
    /// others get it after, in brackets.
    /// </summary>
    public static string Issues(string? issues, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (string.IsNullOrWhiteSpace(issues)) return string.Empty;

        var lines = issues.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                var parts = line.Trim().Split('\t', 2);
                var said = text(parts[0]);

                if (parts.Length < 2) return said;

                return said.Contains("{0}", StringComparison.Ordinal)
                    ? said.Replace("{0}", parts[1], StringComparison.Ordinal)
                    : $"{said} ({parts[1]})";
            });

        return string.Join(" · ", lines);
    }

    /// <summary>
    /// The heading of the verbatim view: direction, status, results imported, tubes. The frame
    /// itself is never rewritten; this line is the only thing said about it.
    /// </summary>
    public static string RawHeader(MessageLogRow message, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(text);

        return text($"MessageDirection.{message.Direction}")
               + $"   ·   {text($"InstrumentMessageProcessingStatus.{message.Status}")}"
               + (message.ResultCount is { } total
                   ? $"   ·   {message.ImportedCount ?? 0}/{total} {text("Result.Plural").ToLowerInvariant()}"
                   : string.Empty)
               + (message.SpecimenIds is { Length: > 0 } specimens ? $"   ·   {specimens}" : string.Empty);
    }

    /// <summary>
    /// The frame exactly as it went over the wire, with the ASTM and MLLP control characters named
    /// rather than stripped: half of all interface faults are a framing problem, and an &lt;ENQ&gt;
    /// that never got its &lt;ACK&gt; is the diagnosis. A record ends with &lt;CR&gt; and a line
    /// break, a transmission with &lt;EOT&gt; and one.
    /// </summary>
    public static string MakeControlCharactersVisible(string raw, string? newLine = null)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var lineBreak = newLine ?? Environment.NewLine;
        var builder = new StringBuilder(raw.Length + 32);

        foreach (var character in raw)
        {
            builder.Append(character switch
            {
                '\x05' => "<ENQ>",
                '\x06' => "<ACK>",
                '\x15' => "<NAK>",
                '\x04' => "<EOT>" + lineBreak,
                '\x02' => "<STX>",
                '\x03' => "<ETX>",
                '\x17' => "<ETB>",
                '\x0B' => "<VT>",
                '\x1C' => "<FS>",
                '\r' => "<CR>" + lineBreak,
                '\n' => string.Empty,
                _ => character.ToString()
            });
        }

        return builder.ToString();
    }

    // ---- Replay and purge ----------------------------------------------------------------------

    /// <summary>
    /// What a replay says: zero stored is not a failure — the frame may have been re-read and every
    /// value still refused — and saying "done" without the count would hide that.
    /// </summary>
    public static string ReplayOutcome(int stored, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return stored == 0 ? text("Interface.ReplayNothing") : text("Interface.ReplayDone", stored);
    }

    /// <summary>Nothing past the retention: said plainly rather than shown as a no-op.</summary>
    public static bool NothingToPurge(MessageLogCensus census)
    {
        ArgumentNullException.ThrowIfNull(census);
        return census.Purgeable == 0;
    }

    /// <summary>
    /// The question asked before a purge, with the cut-off date and the frames kept back because
    /// they are still replayable: a purge that reports its scope only afterwards is a purge nobody
    /// could have consented to.
    /// </summary>
    public static string PurgeScope(MessageLogCensus census, ScreenText text, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(census);
        ArgumentNullException.ThrowIfNull(text);

        var cutoff = census.OldestPurgeable is { } oldest
            ? oldest.ToLocalTime().ToString("dd/MM/yyyy", culture ?? CultureInfo.CurrentCulture)
            : string.Empty;

        return text("Interface.PurgeLogScope", census.Purgeable, cutoff, census.RetainedReplayable);
    }

    /// <summary>
    /// The frames held back whatever their age, said after the purge or its refusal: unfinished work
    /// the laboratory has accumulated, usually the interesting number. Null when there are none.
    /// </summary>
    public static string? RetainedWarning(MessageLogCensus census, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(census);
        ArgumentNullException.ThrowIfNull(text);

        return census.RetainedReplayable > 0 ? text("Interface.PurgeLogRetained", census.RetainedReplayable) : null;
    }

    // ---- « Tester une trame » -----------------------------------------------------------------

    /// <summary>The window's title: the link and the protocol tried.</summary>
    public static string FrameTestSubtitle(InterfaceRow link, InstrumentProtocol protocol, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(text);
        return $"{link.InstrumentCode} · {text($"InstrumentProtocol.{protocol}")}";
    }

    /// <summary>The parameter codes of the mapping table, by parameter: the preview names a line's parameter by them.</summary>
    public static IReadOnlyDictionary<Guid, string> ParameterLabels(IEnumerable<MappingRow> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        return mappings.GroupBy(m => m.TestParameterId).ToDictionary(g => g.Key, g => g.First().ParameterCode);
    }

    /// <summary>A selected inbound frame is loaded and read at once — the usual question: why did this one not import?</summary>
    public static bool OpensOn(MessageLogRow? message) => message is { Direction: MessageDirection.Inbound };

    /// <summary>« Charger la dernière trame »: the newest inbound frame that carried results.</summary>
    public static MessageLogRow? LastInboundWithResults(IEnumerable<MessageLogRow> recent)
    {
        ArgumentNullException.ThrowIfNull(recent);
        return recent.FirstOrDefault(m => m.Direction == MessageDirection.Inbound && (m.ResultCount ?? 0) > 0);
    }

    /// <summary>
    /// The tube the INTEGRA's sample transcript is written for: the first one of the link's last
    /// inbound transmission, so the mapping can be tried on a real tube before the analyser is cabled.
    /// </summary>
    public static string SampleTube(IEnumerable<MessageLogRow> recent)
    {
        ArgumentNullException.ThrowIfNull(recent);

        return recent.Where(m => m.Direction == MessageDirection.Inbound)
                     .Select(m => m.SpecimenIds?.Split(',')[0].Trim())
                     .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
               ?? "TUBE-0001";
    }

    /// <summary>The link's own test numbers for the sample transcript: the first three enabled; null when none reads as one.</summary>
    public static IReadOnlyList<int>? SampleTests(IEnumerable<MappingRow> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        var tests = mappings.Where(m => m.IsEnabled)
                            .Select(m => InstrumentInterfaceService.IntegraTestNumber(m.InstrumentTestCode))
                            .OfType<int>()
                            .Distinct()
                            .Take(3)
                            .ToList();

        return tests.Count > 0 ? tests : null;
    }

    /// <summary>Only the INTEGRA's protocol has a sample: nobody has a frame of it before the analyser is cabled.</summary>
    public static bool HasSample(InstrumentProtocol protocol) => protocol == InstrumentProtocol.RocheIntegra;

    /// <summary>A stored frame keeps CR between records; a text box needs a full line break.</summary>
    public static string Displayable(string? frame, string? newLine = null) =>
        (frame ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)
                               .Replace('\r', '\n')
                               .Replace("\n", newLine ?? Environment.NewLine, StringComparison.Ordinal);

    /// <summary>The tube of a preview line, « Contrôle … » for a control.</summary>
    public static string Specimen(PreviewLine line, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(text);

        return line.IsControl ? $"{text("Interface.TestFrameControl")} {line.SpecimenId}" : line.SpecimenId ?? "—";
    }

    /// <summary>The file and the patient the tube belongs to.</summary>
    public static string Order(PreviewLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return string.Join(" · ", new[] { line.AccessionNumber, line.PatientName }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>The parameter a line maps to: the previewer's code, or the mapping table's.</summary>
    public static string Parameter(PreviewLine line, IReadOnlyDictionary<Guid, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(parameters);

        return line.ParameterCode
               ?? (line.TestParameterId is { } id && parameters.TryGetValue(id, out var known) ? known : string.Empty);
    }

    /// <summary>The value received with its unit.</summary>
    public static string Value(PreviewLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return ((line.Value ?? string.Empty) + " " + (line.Unit ?? string.Empty)).Trim();
    }

    /// <summary>What the import would do with the line, with its detail in brackets.</summary>
    public static string Outcome(PreviewLine line, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(text);

        return text($"PreviewOutcome.{line.Outcome}") + (line.Detail is { Length: > 0 } detail ? $" ({detail})" : string.Empty);
    }

    /// <summary>Stored is normal, set aside on purpose grey, everything else an alarm.</summary>
    public static InterfaceTone OutcomeTone(PreviewLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.Outcome switch
        {
            PreviewOutcome.WouldStore or PreviewOutcome.WouldStoreControl or PreviewOutcome.WouldMarkNotDone => InterfaceTone.Ok,
            PreviewOutcome.ExcludedType or PreviewOutcome.IgnoredNonNumeric => InterfaceTone.Muted,
            _ => InterfaceTone.Critical
        };
    }

    /// <summary>« 2 résultat(s) seraient enregistrés sur 3 lus », then the frame's issues.</summary>
    public static string Summary(MessagePreview preview, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(text);

        var issues = string.Join("  ·  ", preview.Issues.Select(i => PreviewIssue(i, text)));

        return text("Interface.TestFrameSummary", preview.WouldStore, preview.Lines.Count(l => l.Code is not null))
               + (issues.Length > 0 ? $"  ·  {issues}" : string.Empty);
    }

    /// <summary>Normal when something would be stored, the warning's colour otherwise.</summary>
    public static InterfaceTone SummaryTone(MessagePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return preview.WouldStore > 0 ? InterfaceTone.Ok : InterfaceTone.Warning;
    }

    /// <summary>One issue of a preview: its message, its detail in place or after a colon.</summary>
    public static string PreviewIssue(string stored, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(text);

        var tab = stored.IndexOf('\t', StringComparison.Ordinal);
        if (tab < 0) return text(stored);

        var said = text(stored[..tab]);
        var detail = stored[(tab + 1)..];

        return said.Contains("{0}", StringComparison.Ordinal)
            ? string.Format(CultureInfo.CurrentCulture, said, detail)
            : $"{said} : {detail}";
    }
}
