using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Samples;

namespace MarocLIS.Application.Samples;

/// <summary>
/// The chips above the serum bank: every tube, the tubes past their retention (asked of the server
/// again), the tubes received and never shelved (filtered in the list loaded).
/// </summary>
public enum StorageFilter
{
    All = 0,
    PastRetention = 1,
    Unshelved = 2
}

/// <summary>How a tube's line reads: past retention in the alarm's colours, unshelved in the warning's, due within a week said in it.</summary>
public enum StorageInk
{
    Plain = 0,
    Soon = 1,
    Unshelved = 2,
    PastRetention = 3
}

/// <summary>What the serum bank offers on the tube highlighted, and the purge of the whole list.</summary>
public sealed record StorageActions(bool Shelve, bool Aliquot, bool Discard, bool Purge)
{
    public static readonly StorageActions None = new(false, false, false, false);
}

/// <summary>A state of the tube's card, as a pill: its sign, its words and its tone (Critical, Warning, Info).</summary>
public sealed record StoragePill(string Text, string Tone);

/// <summary>One entry of the storage places' list: the name written on the tube, and what the list shows.</summary>
public sealed record StoragePlaceChoice(string Value, string Display);

/// <summary>Shelving a tube as typed: the place, the position in it, and the retention when one is set.</summary>
public sealed class ShelveForm
{
    public string Location { get; set; } = string.Empty;
    public string? Position { get; set; }
    public bool HasKeepUntil { get; set; }
    public DateOnly KeepUntil { get; set; }
}

/// <summary>Shelving a tube as sent to <see cref="SampleWorkflowService.MoveToStorageAsync"/>.</summary>
public sealed record ShelveRequest(string Location, string? Position, DateOnly? KeepUntil);

/// <summary>An aliquot as typed: its volume, what it is for, and where it goes (none keeps the parent tube's place).</summary>
public sealed class AliquotForm
{
    public string? Volume { get; set; }
    public string? Purpose { get; set; }
    public string Location { get; set; } = string.Empty;
}

/// <summary>An aliquot as sent to <see cref="SampleWorkflowService.CreateAliquotAsync"/>.</summary>
public sealed record AliquotRequest(decimal? VolumeMl, string? Purpose, string? Location);

/// <summary>
/// The rules of « Sérothèque » that need no window: the chips and the search, how a tube's line and
/// card read, the actions offered by right and by tube, the storage places' list, the shelving and
/// aliquot forms and their refusals, and the purge's outcome. The workstation's SampleStorageView and
/// its dialogs and the web's screen apply the same ones; what is done is SampleWorkflowService's.
/// </summary>
public static class SampleStorageScreenRules
{
    public const string LocationField = "storage-location";
    public const string PositionField = "storage-position";
    public const string KeepUntilField = "storage-keep-until";
    public const string VolumeField = "aliquot-volume";
    public const string PurposeField = "aliquot-purpose";

    /// <summary>The position's column (a rack and a slot).</summary>
    public const int PositionMaxLength = 40;

    public const int PurposeMaxLength = 200;

    public const int VolumeMaxLength = TypedNumber.MeasureMaxLength;

    /// <summary>The reason of a discard or a purge, kept in the tube's history.</summary>
    public const int DiscardNoteMaxLength = Sample.EventDetailMaxLength;

    /// <summary>A tube kept for a week more or less is said in the warning's colour.</summary>
    public const int SoonDays = 7;

    /// <summary>The retention a tube with none is offered when the box is ticked: a week.</summary>
    public const int DefaultKeepDays = 7;

    /// <summary>The volume column holds 8 figures with 2 decimals: one million millilitres is refused.</summary>
    public const decimal VolumeLimit = 1_000_000m;

    // ---- The list ---------------------------------------------------------------------------------

    /// <summary>Only the past-retention chip asks the server again; the unshelved one filters what is loaded.</summary>
    public static bool AsksServer(StorageFilter filter) => filter == StorageFilter.PastRetention;

    /// <summary>The search as sent to the server: the text trimmed, none when empty.</summary>
    public static string? Search(string? typed) => string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();

    /// <summary>The lines listed under a chip; the past-retention chip's are the server's already.</summary>
    public static IReadOnlyList<StoredSampleRow> Shown(IReadOnlyList<StoredSampleRow> rows, StorageFilter filter)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return filter == StorageFilter.Unshelved ? [.. rows.Where(r => r.IsUnshelved)] : rows;
    }

    /// <summary>
    /// Past retention leads, because purging is the action the screen exists to enable; then the
    /// tube received but nowhere — the one that cannot be found when an add-on arrives; then a tube
    /// due within a week.
    /// </summary>
    public static StorageInk Ink(StoredSampleRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.IsPastRetention ? StorageInk.PastRetention
            : row.IsUnshelved ? StorageInk.Unshelved
            : row.DaysToKeepUntil is <= SoonDays ? StorageInk.Soon
            : StorageInk.Plain;
    }

    /// <summary>The signs of the last column: ? unshelved, ! a defect reported, R accepted with reservation.</summary>
    public static string Flags(StoredSampleRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var flags = new List<string>(3);
        if (row.IsUnshelved) flags.Add("?");
        if (row.HasDefects) flags.Add("!");
        if (row.IsAcceptedWithReservation) flags.Add("R");
        return string.Join(" ", flags);
    }

    /// <summary>What the signs of <see cref="Flags"/> mean, for the cell's tooltip.</summary>
    public static string FlagsMeaning(StoredSampleRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        var meanings = new List<string>(3);
        if (row.IsUnshelved) meanings.Add($"? {text("Sample.NotShelved")}");
        if (row.HasDefects) meanings.Add($"! {text("Sample.HasDefects")}");
        if (row.IsAcceptedWithReservation) meanings.Add($"R {text("Sample.WithReservation")}");
        return string.Join(" · ", meanings);
    }

    /// <summary>« Frigo 2 / R3-B14 », the place alone, or « Non rangé ».</summary>
    public static string Place(StoredSampleRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return row.StorageLocation is { Length: > 0 }
            ? row.StoragePosition is { Length: > 0 } ? $"{row.StorageLocation} / {row.StoragePosition}" : row.StorageLocation
            : text("Sample.NotShelved");
    }

    /// <summary>The reception in the list (« 24/10/26 09:15 ») or on the card (« 24/10/2026 09:15 »).</summary>
    public static string ReceivedAt(StoredSampleRow row, bool full = false)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.ReceivedAt?.ToLocalTime().ToString(full ? "dd/MM/yyyy HH:mm" : "dd/MM/yy HH:mm", CultureInfo.InvariantCulture)
               ?? string.Empty;
    }

    /// <summary>The retention in the list (« 31/10/26 ») or on the card; a dash when none is set.</summary>
    public static string KeepUntil(StoredSampleRow row, bool full = false)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.KeepUntil?.ToString(full ? "dd/MM/yyyy" : "dd/MM/yy", CultureInfo.InvariantCulture) ?? (full ? string.Empty : "—");
    }

    public static string DaysLeft(StoredSampleRow row, bool full = false)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.DaysToKeepUntil?.ToString(CultureInfo.InvariantCulture) ?? (full ? string.Empty : "—");
    }

    public static string Volume(StoredSampleRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.VolumeMl?.ToString("0.#", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public static string Aliquots(StoredSampleRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.AliquotCount > 0 ? row.AliquotCount.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    /// <summary>The head's count: the tubes listed, then those past retention and those not shelved when there are any.</summary>
    public static string Count(IReadOnlyCollection<StoredSampleRow> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        var expired = rows.Count(r => r.IsPastRetention);
        var unshelved = rows.Count(r => r.IsUnshelved);

        var parts = new List<string>(3) { rows.Count.ToString(CultureInfo.InvariantCulture) };
        if (expired > 0) parts.Add($"{expired} {text("Sample.PastRetention").ToLowerInvariant()}");
        if (unshelved > 0) parts.Add($"{unshelved} {text("Sample.NotShelved").ToLowerInvariant()}");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The chips' captions. While the past-retention chip is on, the list holds only those tubes: the
    /// unshelved figure counted among them read as the whole bank's, so it is not given.
    /// </summary>
    public static IReadOnlyList<(StorageFilter Filter, string Caption)> Chips(
        IReadOnlyCollection<StoredSampleRow> rows, StorageFilter filter, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        var expired = rows.Count(r => r.IsPastRetention);
        var unshelved = rows.Count(r => r.IsUnshelved);
        return
        [
            (StorageFilter.All, text("Sample.FilterAll")),
            (StorageFilter.PastRetention, $"{text("Sample.PastRetention")} · {expired}"),
            (StorageFilter.Unshelved, filter == StorageFilter.PastRetention
                ? text("Sample.NotShelved")
                : $"{text("Sample.NotShelved")} · {unshelved}")
        ];
    }

    // ---- The tube's card ----------------------------------------------------------------------------

    /// <summary>« Dupont Marie · Sérum ».</summary>
    public static string Subtitle(StoredSampleRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"{row.PatientName} · {row.SpecimenTypeName}";
    }

    /// <summary>The card's pills, in the workstation's order: past retention, unshelved, defect, reservation.</summary>
    public static IReadOnlyList<StoragePill> Pills(StoredSampleRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        var pills = new List<StoragePill>(4);
        if (row.IsPastRetention) pills.Add(new($"! {text("Sample.PastRetention")}", "Critical"));
        if (row.IsUnshelved) pills.Add(new($"? {text("Sample.NotShelved")}", "Warning"));
        if (row.HasDefects) pills.Add(new($"! {text("Sample.HasDefects")}", "Warning"));
        if (row.IsAcceptedWithReservation) pills.Add(new($"R {text("Sample.WithReservation")}", "Info"));
        return pills;
    }

    /// <summary>The card's facts: the file, the reception, the place, the retention, the days left, the volume and the aliquots.</summary>
    public static IReadOnlyList<(string Label, string Value)> Facts(StoredSampleRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            (text("Order.AccessionNumber"), row.AccessionNumber),
            (text("Sample.ReceivedAt"), ReceivedAt(row, full: true)),
            (text("Sample.Storage"), Place(row, text)),
            (text("Sample.KeepUntil"), KeepUntil(row, full: true)),
            (text("Sample.DaysLeft"), DaysLeft(row, full: true)),
            (text("Sample.Volume"), Volume(row)),
            (text("Sample.Aliquots"), Aliquots(row))
        ];
    }

    // ---- Actions ------------------------------------------------------------------------------------

    /// <summary>
    /// Shelving and aliquoting take the right to receive, discarding and purging the right to
    /// reject — destroying a specimen is final. Aliquoting needs a tube actually present and not
    /// refused; the purge, a tube past its retention in the list.
    /// </summary>
    public static StorageActions Actions(StoredSampleRow? row, IReadOnlyCollection<StoredSampleRow> rows, Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(has);
        var receive = has(Permissions.Samples.Receive);
        var reject = has(Permissions.Samples.Reject);
        return new StorageActions(
            Shelve: receive && row is not null,
            Aliquot: receive && row is { Status: not SampleStatus.Rejected },
            Discard: reject && row is not null,
            Purge: reject && rows.Any(r => r.IsPastRetention));
    }

    /// <summary>The tubes a purge discards: those past their retention, in the list's order.</summary>
    public static IReadOnlyList<StoredSampleRow> PurgeCandidates(IEnumerable<StoredSampleRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return [.. rows.Where(r => r.IsPastRetention)];
    }

    /// <summary>The foot's line after a purge, and whether some tubes failed (the first five named).</summary>
    public static (string Text, bool Partial) PurgeOutcome(int discarded, IReadOnlyCollection<string> failures, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(text);
        return failures.Count == 0
            ? (text("Sample.PurgeDone", discarded), false)
            : (text("Sample.PurgePartial", discarded, string.Join(", ", failures.Take(5))), true);
    }

    /// <summary>The foot's line after a shelving: « T-0042 — Frigo 2 ».</summary>
    public static string Shelved(StoredSampleRow row, string location)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"{row.Barcode} — {location}";
    }

    /// <summary>The foot's line after a discard: « T-0042 — Éliminé ».</summary>
    public static string Discarded(StoredSampleRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return $"{row.Barcode} — {text("SampleStatus.Discarded")}";
    }

    // ---- The storage places -------------------------------------------------------------------------

    /// <summary>
    /// The closed list of the site's active storage places, the empty entry first. A tube already
    /// recorded in a place that is not, or no longer, in the list keeps it: the place is added,
    /// marked, so opening the tube and saving it again never silently moves it somewhere else.
    /// </summary>
    /// <param name="emptyText">The first entry, meaning no place.</param>
    /// <param name="outOfList">How a kept place missing from the list is shown, {0} being its name.</param>
    public static IReadOnlyList<StoragePlaceChoice> PlaceChoices(IReadOnlyList<string> places, string? current,
                                                                 string emptyText, Func<string, string> outOfList)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(outOfList);
        var choices = new List<StoragePlaceChoice> { new(string.Empty, emptyText) };
        choices.AddRange(places.Select(p => new StoragePlaceChoice(p, p)));

        var wanted = current?.Trim() ?? string.Empty;
        if (wanted.Length > 0 && SelectedIndex(choices, wanted) < 0)
            choices.Add(new StoragePlaceChoice(wanted, outOfList(wanted)));

        return choices;
    }

    /// <summary>The entry to select for <paramref name="value"/>: the empty one for none, -1 when it is not listed.</summary>
    public static int SelectedIndex(IReadOnlyList<StoragePlaceChoice> choices, string? value)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var wanted = value?.Trim() ?? string.Empty;
        if (wanted.Length == 0) return 0;

        for (var i = 1; i < choices.Count; i++)
        {
            if (string.Equals(choices[i].Value, wanted, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }

    /// <summary>The place a list's value stands for: none for the empty entry.</summary>
    public static string? Chosen(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // ---- Shelving -----------------------------------------------------------------------------------

    /// <summary>« Ranger dans la sérothèque — T-0042 ».</summary>
    public static string ShelveTitle(StoredSampleRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return $"{text("Sample.Shelve")} — {row.Barcode}";
    }

    /// <summary>The tube under the dialog's title, so the shelf is typed against the right one.</summary>
    public static string ShelveSubtitle(StoredSampleRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"{row.PatientName}   ·   {row.AccessionNumber}   ·   {row.SpecimenTypeName}";
    }

    /// <summary>The dialog as it opens: the tube's place, position and retention, a week from today when it has none.</summary>
    public static ShelveForm ShelveFormFor(StoredSampleRow row, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new ShelveForm
        {
            Location = row.StorageLocation?.Trim() ?? string.Empty,
            Position = row.StoragePosition,
            HasKeepUntil = row.KeepUntil is not null,
            KeepUntil = row.KeepUntil ?? today.AddDays(DefaultKeepDays)
        };
    }

    /// <summary>
    /// A place is required — with none in the list, the operator is told where places are created.
    /// A date already over would put the tube straight into the purge list (the service refuses it
    /// too); the date the tube already carries may stay as it is.
    /// </summary>
    public static (FieldRefusal? Refusal, ShelveRequest? Request) CheckShelve(
        ShelveForm form, StoredSampleRow row, int placeCount, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(row);

        if (Chosen(form.Location) is not { } location)
        {
            var key = placeCount == 0 && string.IsNullOrWhiteSpace(row.StorageLocation)
                ? "Sample.StorageListEmpty"
                : "Sample_StorageLocationRequired";
            return (new FieldRefusal(LocationField, key), null);
        }

        DateOnly? keepUntil = form.HasKeepUntil ? form.KeepUntil : null;

        if (keepUntil is { } until && until < today && until != row.KeepUntil)
            return (new FieldRefusal(KeepUntilField, "Sample_KeepUntilInPast"), null);

        var position = string.IsNullOrWhiteSpace(form.Position) ? null : form.Position.Trim();
        return (null, new ShelveRequest(location, position, keepUntil));
    }

    // ---- Aliquots -----------------------------------------------------------------------------------

    /// <summary>« Créer une aliquote — T-0042 ».</summary>
    public static string AliquotTitle(StoredSampleRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return $"{text("Sample.CreateAliquot")} — {row.Barcode}";
    }

    /// <summary>The tube and what it holds, its volume written with the unit in the reader's language.</summary>
    public static string AliquotSubtitle(StoredSampleRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return $"{row.PatientName}   ·   {row.SpecimenTypeName}"
               + (row.VolumeMl is { } volume
                   ? $"   ·   {text("Sample.VolumeValue", volume.ToString("0.#", CultureInfo.InvariantCulture))}"
                   : string.Empty);
    }

    /// <summary>
    /// The volume is optional, but one typed and unreadable (or not positive) is said: it used to
    /// be dropped without a word and the aliquot recorded with no volume.
    /// </summary>
    public static (FieldRefusal? Refusal, AliquotRequest? Request) CheckAliquot(AliquotForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        decimal? volume = null;

        if (!string.IsNullOrWhiteSpace(form.Volume))
        {
            volume = TypedNumber.Parse(form.Volume);
            if (volume is not (> 0m and < VolumeLimit))
                return (new FieldRefusal(VolumeField, "Sample_AliquotVolumeInvalid"), null);
        }

        var purpose = string.IsNullOrWhiteSpace(form.Purpose) ? null : form.Purpose.Trim();
        return (null, new AliquotRequest(volume, purpose, Chosen(form.Location)));
    }

    /// <summary>The service's refusal of a shelving or an aliquot put next to its box, when it names one.</summary>
    public static string? FieldOf(string resourceKey) => resourceKey switch
    {
        "Sample_StorageLocationRequired" or "Sample_StorageLocationNotInList" => LocationField,
        "Sample_KeepUntilInPast" => KeepUntilField,
        "Sample_AliquotVolumeInvalid" or "Sample_AliquotVolumeExceedsTube" => VolumeField,
        _ => null
    };
}
