using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;

namespace MarocLIS.Application.Inventory;

/// <summary>
/// What « Inventaires » offers, by right and by the state of the count selected — the workstation's
/// PermissionGate calls and UpdateButtons. Opening, starting, closing and cancelling a count is the
/// manager's (closing counts); counting the shelves takes the reception right. InventoryCountService
/// stays the judge.
/// </summary>
public sealed record InventoryCountScreenActions(
    bool New, bool Start, bool Close, bool Cancel, bool Sheet, bool Variances, bool Count);

/// <summary>A count opened, as typed: its date and what it covers.</summary>
public sealed class InventoryCountForm
{
    public DateOnly CountDate { get; set; }
    public InventoryCountScope Scope { get; set; } = InventoryCountScope.All;
    public string? StorageLocation { get; set; }
    public StockItemKind Category { get; set; } = StockItemKind.Reagent;
    public string? Notes { get; set; }
}

/// <summary>
/// The rules of « Inventaires » that need no window: what is offered by which right and in which
/// state, how a count and a line read (its scope, its progress, its variance), what a figure typed
/// is saved with — the time a paper sheet was filled in goes with a line's first figure only — how
/// the count sheet (without the expected quantities) and the variance report are written, and how a
/// count is opened and refused box by box. The workstation's InventoryCountView and its dialog and
/// the web's screen apply the same ones; every decision stays InventoryCountService's.
/// </summary>
public static class InventoryCountScreenRules
{
    public const int LocationMaxLength = 100;
    public const int NotesMaxLength = 1000;
    public const int CommentMaxLength = 500;
    public const int CountedMaxLength = TypedNumber.MeasureMaxLength;

    public const string DateField = "count-date";
    public const string LocationField = "count-location";
    public const string CategoryField = "count-category";

    /// <summary>The box of a line's counted figure, as the sheet names it.</summary>
    public static string CountedField(Guid lineId) => $"counted-{lineId:N}";

    public static InventoryCountScreenActions Actions(ICurrentUser user, InventoryCountRow? count, InventoryCountDetail? detail)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Actions(user.Has, count, detail);
    }

    public static InventoryCountScreenActions Actions(Func<string, bool> has, InventoryCountRow? count, InventoryCountDetail? detail)
    {
        ArgumentNullException.ThrowIfNull(has);
        var manage = has(Permissions.Inventory.CloseCounts);
        var status = count?.Status;
        var lines = detail is { Lines.Count: > 0 };

        return new InventoryCountScreenActions(
            New: manage,
            Start: manage && status == InventoryCountStatus.Draft,
            Close: manage && status == InventoryCountStatus.Counting,
            Cancel: manage && status is InventoryCountStatus.Draft or InventoryCountStatus.Counting,
            Sheet: lines,
            Variances: lines,
            Count: MayCount(has, detail));
    }

    /// <summary>A sheet is typed in while it is being counted, by who receives stock or who closes counts.</summary>
    public static bool MayCount(Func<string, bool> has, InventoryCountDetail? detail)
    {
        ArgumentNullException.ThrowIfNull(has);
        return detail?.Count.Status == InventoryCountStatus.Counting
               && (has(Permissions.Inventory.Receive) || has(Permissions.Inventory.CloseCounts));
    }

    /// <summary>The count kept selected after a reload: the one worked on, else the first.</summary>
    public static InventoryCountRow? Keep(IReadOnlyList<InventoryCountRow> rows, Guid? kept)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.FirstOrDefault(r => r.InventoryCountId == kept) ?? (rows.Count > 0 ? rows[0] : null);
    }

    public static StockTone StatusTone(InventoryCountStatus status) => status switch
    {
        InventoryCountStatus.Counting => StockTone.Running,
        InventoryCountStatus.Closed => StockTone.Done,
        _ => StockTone.Waiting
    };

    /// <summary>« Emplacement : Chambre froide », « Catégorie : Réactif » or « Tout le stock ».</summary>
    public static string Scope(InventoryCountRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return row.Scope switch
        {
            InventoryCountScope.StorageLocation => $"{text("InventoryCountScope.StorageLocation")} : {row.StorageLocation}",
            InventoryCountScope.Category => $"{text("InventoryCountScope.Category")} : {text($"StockItemKind.{row.Category}")}",
            _ => text("InventoryCountScope.All")
        };
    }

    public static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>« 18/10/2026 09:40 », in the server's time zone as the workstation writes it.</summary>
    public static string When(DateTimeOffset? at) =>
        at?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string Quantity(decimal? value, IFormatProvider? provider = null) =>
        value?.ToString("0.###", provider ?? CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>« INV-2026-0004 · 12/40 »: the count and its progress.</summary>
    public static string Note(InventoryCountRow count)
    {
        ArgumentNullException.ThrowIfNull(count);
        return $"{count.Number} · {count.Counted.ToString(CultureInfo.InvariantCulture)}/{count.Lines.ToString(CultureInfo.InvariantCulture)}";
    }

    public static string CountLabel(IReadOnlyCollection<InventoryCountRow> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        return text("StocksUi.Counts", rows.Count);
    }

    /// <summary>What to do while a sheet is counted, short; the paper sheet's explanation is on the sheet time's tooltip.</summary>
    public static string? CountingNotice(InventoryCountDetail? detail, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return detail?.Count.Status == InventoryCountStatus.Counting
            ? $"{text("InventoryCount.CountingNotice")} {text("InventoryCount.SheetTimeShort")}"
            : null;
    }

    /// <summary>A line whose count differs from the stock is said in the alarm's ink.</summary>
    public static StockInk LineInk(InventoryCountLineRow line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.Variance is { } v && v != 0m ? StockInk.Critical : StockInk.Plain;
    }

    // ---- Counting ------------------------------------------------------------------------------------

    /// <summary>
    /// A figure typed on a line: nothing to save when the box is empty, refused when it is no
    /// number, quoted in the refusal.
    /// </summary>
    public static (decimal? Counted, FieldRefusal? Refusal) ReadCount(Guid lineId, string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return (null, null);
        return TypedNumber.Parse(typed) is { } counted
            ? (counted, null)
            : (null, new FieldRefusal(CountedField(lineId), "Result_NotANumber", typed.Trim()));
    }

    /// <summary>A figure and a comment as they already stand are not saved again.</summary>
    public static bool Changed(InventoryCountLineRow line, decimal counted, string? comment)
    {
        ArgumentNullException.ThrowIfNull(line);
        return counted != line.CountedQuantity || (comment ?? string.Empty) != (line.Comment ?? string.Empty);
    }

    /// <summary>
    /// The sheet's time goes with the first figure of a line. A comment, or a figure corrected on a
    /// line already counted (perhaps from an earlier sheet), stays on the time of that count: the
    /// time stays set for the whole sheet and must not re-base lines keyed before it.
    /// </summary>
    public static DateTimeOffset? ShelfTime(InventoryCountLineRow line, DateTimeOffset? sheetTime)
    {
        ArgumentNullException.ThrowIfNull(line);
        return sheetTime is { } time && line.CountedQuantity is null ? time : null;
    }

    // ---- The sheet and the variances -----------------------------------------------------------------

    /// <summary>
    /// The variance is counted less the stock when the shelf was counted: that is the column next to
    /// it. The quantity frozen at the start stays for information, labelled as such. The printed
    /// sheet leaves both out: a counter who sees what the system expects tends to find it.
    /// </summary>
    public static IReadOnlyList<string> SheetHeaders(bool withExpected, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            "#", text("Sample.Storage"), text("Field.Code"), text("Inventory.Item"), text("Inventory.LotNumber"), text("Inventory.Expiry"),
            .. withExpected ? new[] { text("InventoryCount.ExpectedAtStart"), text("InventoryCount.StockAtCount") } : [],
            text("InventoryCount.Counted"),
            text("InventoryCount.CountedAt"),
            .. withExpected ? new[] { text("InventoryCount.Variance") } : [],
            text("Field.Comment")
        ];
    }

    /// <summary>The blank sheet to fill in on paper: no expected quantity, empty boxes for the figure, the time and the comment.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> SheetRows(InventoryCountDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return [.. detail.Lines.Select(l => (IReadOnlyList<string>)
        [
            l.LineNumber.ToString(CultureInfo.InvariantCulture), l.StorageLocation ?? string.Empty, l.ItemCode,
            l.ItemName, l.LotNumber, Date(l.ExpiryDate), string.Empty, string.Empty, string.Empty
        ])];
    }

    /// <summary>The lots whose count differs from the stock, with the difference — written as the French report writes numbers.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> VarianceRows(InventoryCountDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        return [.. detail.Lines.Where(l => l.Variance is not null and not 0m).Select(l => (IReadOnlyList<string>)
        [
            l.LineNumber.ToString(CultureInfo.InvariantCulture), l.StorageLocation ?? string.Empty, l.ItemCode,
            l.ItemName, l.LotNumber, Date(l.ExpiryDate),
            Quantity(l.ExpectedQuantity, fr), Quantity(l.StockAtCount, fr), Quantity(l.CountedQuantity, fr), When(l.CountedAt),
            Quantity(l.Variance, fr), l.Comment ?? string.Empty
        ])];
    }

    public static string SheetTitle(InventoryCountRow count, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(count);
        ArgumentNullException.ThrowIfNull(text);
        return text("InventoryCount.SheetTitle", count.Number, Date(count.CountDate));
    }

    public static string SheetCaption(InventoryCountRow count, ScreenText text) =>
        $"{Scope(count, text)} — {text("InventoryCount.SheetCaption")}";

    public static string VarianceTitle(InventoryCountRow count, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(count);
        ArgumentNullException.ThrowIfNull(text);
        return text("InventoryCount.VarianceTitle", count.Number, Date(count.CountDate));
    }

    public static string VarianceCaption(InventoryCountDetail detail, int varianceRows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(text);
        return $"{Scope(detail.Count, text)} — {text($"InventoryCountStatus.{detail.Count.Status}")} — "
               + text("InventoryCount.VarianceCaption", varianceRows, detail.Lines.Count);
    }

    /// <summary>The name a sheet or a report is handed over as.</summary>
    public static string FileName(InventoryCountRow count, string what, string extension) =>
        $"{new string(count.Number.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray())}-{what}.{extension}";

    // ---- A new count ---------------------------------------------------------------------------------

    public static InventoryCountForm NewCount(DateOnly today) => new() { CountDate = today };

    /// <summary>Only the box the scope uses is offered: a place for a place, a category for a category.</summary>
    public static (bool Location, bool Category) ScopeBoxes(InventoryCountScope scope) =>
        (scope == InventoryCountScope.StorageLocation, scope == InventoryCountScope.Category);

    /// <summary>Today or earlier — the service refuses a count dated in the future — and a place for a place's count.</summary>
    public static FieldRefusal? Check(InventoryCountForm form, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.CountDate > today) return new FieldRefusal(DateField, "InventoryCount_DateInFuture");
        if (form.Scope == InventoryCountScope.StorageLocation && string.IsNullOrWhiteSpace(form.StorageLocation))
            return new FieldRefusal(LocationField, "InventoryCount_LocationRequired");
        return null;
    }

    public static CreateInventoryCountRequest ToRequest(InventoryCountForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return new CreateInventoryCountRequest
        {
            CountDate = form.CountDate,
            Scope = form.Scope,
            StorageLocation = form.Scope == InventoryCountScope.StorageLocation ? form.StorageLocation?.Trim() : null,
            Category = form.Scope == InventoryCountScope.Category ? form.Category : null,
            Notes = string.IsNullOrWhiteSpace(form.Notes) ? null : form.Notes.Trim()
        };
    }

    /// <summary>The box a refusal of InventoryCountService names.</summary>
    public static string? FieldOf(Error? error) => error?.ResourceKey switch
    {
        "InventoryCount_DateInFuture" => DateField,
        "InventoryCount_LocationRequired" => LocationField,
        "InventoryCount_CategoryRequired" => CategoryField,
        _ => null
    };
}
