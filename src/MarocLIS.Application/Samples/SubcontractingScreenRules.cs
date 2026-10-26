using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;

namespace MarocLIS.Application.Samples;

/// <summary>The stage chips of « Sous-traitance »: everything, to send, awaiting return, late — the order of the work.</summary>
public enum SubcontractFilter
{
    All = 0,
    ToSend = 1,
    AwaitingReturn = 2,
    Late = 3
}

/// <summary>
/// How an act's line reads: late in the alarm's colours, an urgent act to send in red, one to send
/// with its stage in the accent, one awaiting its return muted.
/// </summary>
public enum SubcontractInk
{
    ToSend = 0,
    UrgentToSend = 1,
    Awaiting = 2,
    Late = 3
}

/// <summary>
/// What « Sous-traitance » offers on the acts ticked (or the one highlighted): sending those to
/// send, reprinting the slips of those sent, keeping the partner's reference, and — in the web
/// version — opening the result entry of the file whose partner results came back.
/// </summary>
public sealed record SubcontractActions(bool Send, bool Reprint, bool Reference, bool EnterResults)
{
    public static readonly SubcontractActions None = new(false, false, false, false);
}

/// <summary>A partner in the dispatch dialog's list: none for the catalogue's partner of each analysis.</summary>
public sealed record SubcontractPartnerChoice(Guid? Id, string Display);

/// <summary>
/// The rules of « Sous-traitance » that need no window: the stages, the chips and the search, how a
/// line reads, the acts an action applies to, the dispatch dialog, the slip as printed, and the
/// partner's reference. The workstation's SubcontractingView and its dialog and the web's screen
/// apply the same ones; what is done is SubcontractingService's.
/// </summary>
public static class SubcontractingScreenRules
{
    public const string ReferenceField = "partner-reference";

    /// <summary>The partner's own number for the request (the service refuses longer).</summary>
    public const int ReferenceMaxLength = 60;

    /// <summary>An act sent whose expected return is past, its results not entered.</summary>
    public static bool IsLate(SubcontractRow row, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Stage == SubcontractStage.AwaitingReturn && row.DueAt is { } due && due < now;
    }

    /// <summary>
    /// Whether a line is listed under a chip and the search: the file, the patient, the tube, the
    /// slip or the partner's reference containing the text typed (or scanned).
    /// </summary>
    public static bool Matches(SubcontractRow row, SubcontractFilter filter, string? term, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);
        var kept = filter switch
        {
            SubcontractFilter.ToSend => row.Stage == SubcontractStage.ToSend,
            SubcontractFilter.AwaitingReturn => row.Stage == SubcontractStage.AwaitingReturn,
            SubcontractFilter.Late => IsLate(row, now),
            _ => true
        };

        var text = term?.Trim() ?? string.Empty;

        return kept
               && (text.Length == 0
                   || row.AccessionNumber.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || row.PatientName.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || (row.Barcode?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                   || (row.DispatchNumber?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                   || (row.PartnerReference?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    public static IReadOnlyList<SubcontractRow> Shown(IEnumerable<SubcontractRow> rows, SubcontractFilter filter,
                                                      string? term, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return [.. rows.Where(r => Matches(r, filter, term, now))];
    }

    public static SubcontractInk Ink(SubcontractRow row, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);
        return IsLate(row, now) ? SubcontractInk.Late
            : row.Stage == SubcontractStage.ToSend
                ? row.Priority != OrderPriority.Routine ? SubcontractInk.UrgentToSend : SubcontractInk.ToSend
            : SubcontractInk.Awaiting;
    }

    /// <summary>The stage's words: late, to send, or awaiting return.</summary>
    public static string StageText(SubcontractRow row, DateTimeOffset now, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return IsLate(row, now) ? text("Subcontract.Late") : text($"Subcontract.Stage{row.Stage}");
    }

    /// <summary>The stage pill's sign, as the workstation draws it: ! late, → to send, ◐ awaiting return.</summary>
    public static string StageGlyph(SubcontractRow row, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);
        return IsLate(row, now) ? "!" : row.Stage == SubcontractStage.ToSend ? "→" : "◐";
    }

    /// <summary>The stage pill's tone: red when late, the accent's to send, grey awaiting return.</summary>
    public static string StageTone(SubcontractRow row, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);
        return IsLate(row, now) ? "Critical" : row.Stage == SubcontractStage.ToSend ? "Info" : "Waiting";
    }

    /// <summary>« NFS  Numération formule sanguine ».</summary>
    public static string Analysis(SubcontractRow row, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"{row.AnalysisCode}  {row.AnalysisName.For(language)}";
    }

    /// <summary>The partner laboratory, or the line saying none is designated.</summary>
    public static string Partner(SubcontractRow row, AppLanguage language, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return row.PartnerName?.For(language) ?? text("Subcontract.NoPartner");
    }

    /// <summary>The slip and when it left: « ST-2026-000012  24/10 15:40 ».</summary>
    public static string Sent(SubcontractRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.SentAt is { } sentAt
            ? $"{row.DispatchNumber}  {sentAt.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture)}"
            : string.Empty;
    }

    /// <summary>The expected return, for an act awaiting it only.</summary>
    public static string Due(SubcontractRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Stage == SubcontractStage.AwaitingReturn
            ? row.DueAt?.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;
    }

    /// <summary>The head's count: to send, awaiting return, late.</summary>
    public static string Count(IReadOnlyCollection<SubcontractRow> rows, DateTimeOffset now, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        return text("Subcontract.Count",
                    rows.Count(r => r.Stage == SubcontractStage.ToSend),
                    rows.Count(r => r.Stage == SubcontractStage.AwaitingReturn),
                    rows.Count(r => IsLate(r, now)));
    }

    /// <summary>The chips with their figures.</summary>
    public static IReadOnlyList<(SubcontractFilter Filter, string Caption)> Chips(
        IReadOnlyCollection<SubcontractRow> rows, DateTimeOffset now, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            (SubcontractFilter.All, $"{text("Subcontract.StageAll")} · {rows.Count}"),
            (SubcontractFilter.ToSend, $"{text("Subcontract.StageToSend")} · {rows.Count(r => r.Stage == SubcontractStage.ToSend)}"),
            (SubcontractFilter.AwaitingReturn, $"{text("Subcontract.StageAwaitingReturn")} · {rows.Count(r => r.Stage == SubcontractStage.AwaitingReturn)}"),
            (SubcontractFilter.Late, $"{text("Subcontract.Late")} · {rows.Count(r => IsLate(r, now))}")
        ];
    }

    // ---- What an action applies to ------------------------------------------------------------------

    /// <summary>The acts an action applies to: those ticked, or — nothing ticked — the one highlighted.</summary>
    public static IReadOnlyList<SubcontractRow> Targets(IReadOnlyList<SubcontractRow> ticked, SubcontractRow? current)
    {
        ArgumentNullException.ThrowIfNull(ticked);
        return ticked.Count > 0 ? ticked : current is null ? [] : [current];
    }

    /// <summary>
    /// Each action is offered for the acts it applies to. Sending and the partner's reference take
    /// the right to subcontract; reprinting a slip only needs an act already sent; entering the
    /// partner's results takes the right to enter results and an act awaiting its return.
    /// </summary>
    public static SubcontractActions Actions(IReadOnlyCollection<SubcontractRow> targets, Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(has);
        var subcontract = has(Permissions.Samples.Subcontract);
        return new SubcontractActions(
            Send: subcontract && targets.Any(r => r.Stage == SubcontractStage.ToSend),
            Reprint: targets.Any(r => r.DispatchNumber is not null),
            Reference: subcontract && targets.Any(r => r.Stage == SubcontractStage.AwaitingReturn),
            EnterResults: has(Permissions.Results.Enter) && targets.Any(r => r.Stage == SubcontractStage.AwaitingReturn));
    }

    /// <summary>The acts a send takes: those to send among the targets.</summary>
    public static IReadOnlyList<SubcontractRow> ToSend(IEnumerable<SubcontractRow> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return [.. targets.Where(r => r.Stage == SubcontractStage.ToSend)];
    }

    /// <summary>The slips a reprint prints, each once.</summary>
    public static IReadOnlyList<string> Slips(IEnumerable<SubcontractRow> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return [.. targets.Select(r => r.DispatchNumber).OfType<string>().Distinct()];
    }

    /// <summary>The act whose partner reference is typed: the first one awaiting its return.</summary>
    public static SubcontractRow? AwaitingReturn(IEnumerable<SubcontractRow> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return targets.FirstOrDefault(r => r.Stage == SubcontractStage.AwaitingReturn);
    }

    // ---- Sending ------------------------------------------------------------------------------------

    /// <summary>« 3 analyse(s) de 2 dossier(s) à envoyer : ».</summary>
    public static string SendSummary(IReadOnlyCollection<SubcontractRow> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        return text("Subcontract.SendSummary", rows.Count, rows.Select(r => r.LabOrderId).Distinct().Count());
    }

    /// <summary>One act in the dispatch dialog: « 2610-000051  ·  TSH  ·  T-0042  →  BIOLAB ».</summary>
    public static string DispatchLine(SubcontractRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return $"{row.AccessionNumber}  ·  {row.AnalysisCode}  ·  {row.Barcode}  →  {row.PartnerCode ?? text("Subcontract.NoPartner")}";
    }

    /// <summary>The partners offered, the catalogue's partner of each analysis first (the default).</summary>
    public static IReadOnlyList<SubcontractPartnerChoice> PartnerChoices(IEnumerable<SubcontractPartner> partners,
                                                                         AppLanguage language, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(partners);
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            new SubcontractPartnerChoice(null, text("Subcontract.PartnerFromCatalogue")),
            .. partners.Select(p => new SubcontractPartnerChoice(p.Id, $"{p.Code}  {p.Name.For(language)}"))
        ];
    }

    /// <summary>The foot's line after a send: the slips numbered.</summary>
    public static string SentMessage(IEnumerable<SubcontractDispatch> dispatches, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(dispatches);
        ArgumentNullException.ThrowIfNull(text);
        return text("Subcontract.Sent", string.Join(", ", dispatches.Select(d => d.Number)));
    }

    // ---- The slip -----------------------------------------------------------------------------------

    public static string SlipTitle(SubcontractSlip slip, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(slip);
        ArgumentNullException.ThrowIfNull(text);
        return $"{text("Subcontract.SlipTitle")} {slip.Number}";
    }

    /// <summary>The site, the partner, when and by whom it left, and how many analyses.</summary>
    public static string SlipCaption(SubcontractSlip slip, string siteCode, AppLanguage language, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(slip);
        ArgumentNullException.ThrowIfNull(text);
        return text("Subcontract.SlipCaption", siteCode,
                    $"{slip.Partner.Code}  {slip.Partner.Name.For(language)}",
                    slip.SentAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
                    slip.SentByName, slip.Items.Count);
    }

    public static IReadOnlyList<string> SlipHeaders(ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            text("Order.AccessionNumber"), text("Patient.Title"), text("Subcontract.SexBirth"),
            text("Sample.Barcode"), text("Catalog.Analysis"), text("Subcontract.Container"),
            text("Sample.CollectedAt"), text("Subcontract.Priority")
        ];
    }

    /// <summary>
    /// One line per analysis, with what the partner needs to identify the tube and the patient, then
    /// a line for the courier's and the partner's signatures.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> SlipRows(SubcontractSlip slip, AppLanguage language, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(slip);
        ArgumentNullException.ThrowIfNull(text);
        var rows = slip.Items.Select(i => (IReadOnlyList<string>)
        [
            i.AccessionNumber,
            i.PatientName,
            $"{text($"Gender.{i.Gender}")} {i.DateOfBirth?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? string.Empty}".Trim(),
            i.Barcode ?? string.Empty,
            Analysis(i, language),
            i.ContainerName?.For(language) ?? string.Empty,
            i.CollectedAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) ?? string.Empty,
            i.Priority == OrderPriority.Routine ? string.Empty : text($"Priority.{i.Priority}")
        ]).ToList();

        rows.Add(["", "", "", "", "", "", "", ""]);
        rows.Add([text("Subcontract.SignatureCourier"), "", "", "", text("Subcontract.SignaturePartner"), "", "", ""]);
        return rows;
    }

    /// <summary>The name the slip is handed over as.</summary>
    public static string SlipFileName(string number)
    {
        ArgumentNullException.ThrowIfNull(number);
        var safe = new string([.. number.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
        return $"bordereau-{safe}.pdf";
    }

    // ---- The partner's reference --------------------------------------------------------------------

    public static string ReferencePrompt(SubcontractRow row, AppLanguage language, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return text("Subcontract.PartnerReferencePrompt", row.AccessionNumber, row.PartnerName?.For(language));
    }

    /// <summary>The reference as kept: trimmed, none when empty; refused beyond its column.</summary>
    public static (FieldRefusal? Refusal, string? Value) CheckReference(string? typed)
    {
        var value = string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();
        return value is { Length: > ReferenceMaxLength }
            ? (new FieldRefusal(ReferenceField, "Subcontract_ReferenceTooLong"), null)
            : (null, value);
    }
}
