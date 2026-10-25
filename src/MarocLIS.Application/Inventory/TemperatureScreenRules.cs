using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;

namespace MarocLIS.Application.Inventory;

/// <summary>
/// What « Températures » offers, by right and by the unit selected — the workstation's
/// PermissionGate calls and RefreshButtons. Readings go only to a unit in service; an inactive one
/// is listed to be reactivated or to print its past sheets. TemperatureMonitoringService stays the
/// judge.
/// </summary>
public sealed record TemperatureScreenActions(bool Record, bool NewUnit, bool EditUnit, bool Sheet);

/// <summary>A reading as typed: the value, the minimum and maximum of the period when the thermometer gives them.</summary>
public sealed class TemperatureReadingForm
{
    public string? Value { get; set; }
    public string? Minimum { get; set; }
    public string? Maximum { get; set; }
    public string? Comment { get; set; }
}

/// <summary>A monitored fridge, freezer or room as typed, with its acceptable range.</summary>
public sealed class StorageUnitForm
{
    public string? Name { get; set; }
    public StorageUnitKind Kind { get; set; } = StorageUnitKind.Refrigerator;
    public string? Minimum { get; set; }
    public string? Maximum { get; set; }
    public int ReadingsPerDay { get; set; } = 2;
    public string? Location { get; set; }
    public string? SerialNumber { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// The rules of « Températures » that need no window: what is offered by which right and for which
/// unit, how a unit and a reading read (the range, today's readings against those expected, the
/// state and their inks and pills), the monthly sheet for the ISO 15189 file, and how a reading and
/// a unit are typed and refused box by box — a reading out of range asks for a comment, the first
/// corrective action, and opens a non-conformity on its own. The workstation's TemperatureView and
/// its dialogs and the web's screen apply the same ones; every decision stays
/// TemperatureMonitoringService's.
/// </summary>
public static class TemperatureScreenRules
{
    public const int CommentMaxLength = 500;
    public const int ValueMaxLength = 8;
    public const int NameMaxLength = 100;
    public const int LocationMaxLength = 100;
    public const int SerialMaxLength = 60;
    public const int ReadingsPerDayMax = 24;

    public const string ValueField = "temperature-value";
    public const string MinimumField = "temperature-min";
    public const string MaximumField = "temperature-max";
    public const string CommentField = "temperature-comment";
    public const string NameField = "unit-name";
    public const string RangeField = "unit-min";
    public const string PerDayField = "unit-per-day";

    public static TemperatureScreenActions Actions(ICurrentUser user, StorageUnitRow? unit)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Actions(user.Has, unit);
    }

    public static TemperatureScreenActions Actions(Func<string, bool> has, StorageUnitRow? unit)
    {
        ArgumentNullException.ThrowIfNull(has);
        var manage = has(Permissions.Inventory.ManageStorageUnits);
        return new TemperatureScreenActions(
            Record: unit is { IsActive: true } && has(Permissions.Inventory.RecordTemperatures),
            NewUnit: manage,
            EditUnit: manage && unit is not null,
            // The month's sheet is that of the unit selected: with none, the buttons did nothing.
            Sheet: unit is not null);
    }

    /// <summary>The unit kept selected after a reload: the one worked on, else the first.</summary>
    public static StorageUnitRow? Keep(IReadOnlyList<StorageUnitRow> rows, Guid? kept)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.FirstOrDefault(r => r.StorageUnitId == kept) ?? (rows.Count > 0 ? rows[0] : null);
    }

    // ---- The units -----------------------------------------------------------------------------------

    /// <summary>« 4,2 °C »; nothing when there is no value.</summary>
    public static string Celsius(decimal? value, IFormatProvider? provider = null) =>
        value is { } v ? v.ToString("0.0", provider ?? CultureInfo.CurrentCulture) + " °C" : string.Empty;

    /// <summary>« 2,0 °C – 8,0 °C ».</summary>
    public static string Range(StorageUnitRow unit, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return $"{Celsius(unit.MinimumCelsius, provider)} – {Celsius(unit.MaximumCelsius, provider)}";
    }

    /// <summary>« 1 / 2 »: today's readings against those expected.</summary>
    public static string Today(StorageUnitRow unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return $"{unit.ReadingsToday.ToString(CultureInfo.InvariantCulture)} / {unit.ReadingsPerDay.ToString(CultureInfo.InvariantCulture)}";
    }

    public static string LastAt(StorageUnitRow unit) =>
        unit.LastReadAt?.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string StateKey(StorageUnitRow unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return !unit.IsActive ? "Temperatures.Inactive"
            : unit.LastOutOfRange ? "Temperatures.OutOfRange"
            : unit.MissingToday ? "Temperatures.MissingToday"
            : "Temperatures.InRange";
    }

    public static StockTone StateTone(StorageUnitRow unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit switch
        {
            { IsActive: false } => StockTone.Waiting,
            { LastOutOfRange: true } => StockTone.Critical,
            { MissingToday: true } => StockTone.Warning,
            _ => StockTone.Done
        };
    }

    /// <summary>A unit out of service muted, one whose last reading was out of range in the alarm's colours.</summary>
    public static StockInk RowInk(StorageUnitRow unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return !unit.IsActive ? StockInk.Muted : unit.LastOutOfRange ? StockInk.Critical : StockInk.Plain;
    }

    /// <summary>Readings still owed today: not urgent, said on today's count rather than on the whole row.</summary>
    public static StockInk TodayInk(StorageUnitRow unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit.IsActive && !unit.LastOutOfRange && (unit.MissingToday || unit.ReadingsToday < unit.ReadingsPerDay)
            ? StockInk.AbnormalBold
            : StockInk.Plain;
    }

    public static string Count(IReadOnlyCollection<StorageUnitRow> units, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(text);
        return text("StocksUi.Units", units.Count);
    }

    /// <summary>« 2 enceintes sans relevé aujourd'hui »; none when every unit has one.</summary>
    public static string? MissingNotice(IReadOnlyCollection<StorageUnitRow> units, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(text);
        var missing = units.Count(u => u.MissingToday);
        return missing > 0 ? text("Temperatures.MissingCount", missing) : null;
    }

    // ---- The readings --------------------------------------------------------------------------------

    public static IReadOnlyList<string> ReadingHeaders(ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            text("Field.Date"), text("Temperatures.Value"), text("Temperatures.PeriodMin"), text("Temperatures.PeriodMax"),
            text("Field.Status"), text("Temperatures.By"), text("Field.Comment"), text("Temperatures.NonConformity")
        ];
    }

    public static string ReadAt(TemperatureReadingRow reading) =>
        reading.ReadAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    public static string ReadingStateKey(TemperatureReadingRow reading) =>
        reading.IsOutOfRange ? "Temperatures.OutOfRange" : "Temperatures.InRange";

    public static StockTone ReadingTone(TemperatureReadingRow reading) =>
        reading.IsOutOfRange ? StockTone.Critical : StockTone.Done;

    /// <summary>The cells of a reading, shared by the grid, the CSV and the PDF.</summary>
    public static IReadOnlyList<string> ReadingCells(TemperatureReadingRow r, ScreenText text, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            ReadAt(r), Celsius(r.Celsius, provider), Celsius(r.PeriodMinimumCelsius, provider), Celsius(r.PeriodMaximumCelsius, provider),
            text(ReadingStateKey(r)), r.ByUserName, r.Comment ?? string.Empty, r.NonConformityReference ?? string.Empty
        ];
    }

    /// <summary>« Réfrigérateur réactifs · 46 »: whose month, how many readings.</summary>
    public static string ReadingsNote(StorageUnitRow? unit, int readings) =>
        unit is null ? string.Empty : $"{unit.Name} · {readings.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>« 10/2026 ».</summary>
    public static string Month(int year, int month) =>
        new DateOnly(year, month, 1).ToString("MM/yyyy", CultureInfo.InvariantCulture);

    public static string SheetTitle(StorageUnitRow unit, int year, int month, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(text);
        return text("Temperatures.SheetTitle", unit.Name, Month(year, month));
    }

    /// <summary>The kind, the range, the place, the readings and excursions, and the extremes of the month.</summary>
    public static string SheetCaption(StorageUnitRow unit, IReadOnlyList<TemperatureReadingRow> readings, ScreenText text,
                                      IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(text);
        return text("Temperatures.SheetCaption", text($"StorageUnitKind.{unit.Kind}"), Range(unit, provider), unit.Location ?? "—",
                    readings.Count, readings.Count(r => r.IsOutOfRange),
                    readings.Count > 0 ? Celsius(readings.Min(r => r.PeriodMinimumCelsius ?? r.Celsius), provider) : "—",
                    readings.Count > 0 ? Celsius(readings.Max(r => r.PeriodMaximumCelsius ?? r.Celsius), provider) : "—");
    }

    /// <summary>The name a month's sheet is handed over as.</summary>
    public static string FileName(StorageUnitRow unit, int year, int month, string extension) =>
        $"temperatures-{new string(unit.Name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())}-{year:0000}-{month:00}.{extension}";

    // ---- A reading -----------------------------------------------------------------------------------

    /// <summary>« Plage acceptable : 2,0 à 8,0 °C… »: the notice of the reading dialog.</summary>
    public static string ReadingNotice(StorageUnitRow unit, ScreenText text, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(text);
        var culture = provider ?? CultureInfo.CurrentCulture;
        return text("Temperatures.ReadingNotice", unit.MinimumCelsius.ToString("0.0", culture), unit.MaximumCelsius.ToString("0.0", culture));
    }

    /// <summary>
    /// Whether what is typed is an excursion: the value, the period's minimum or its maximum outside
    /// the unit's range. Said while it is typed, so the comment is asked for before « Enregistrer ».
    /// </summary>
    public static bool IsExcursion(TemperatureReadingForm form, StorageUnitRow unit)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(unit);
        var value = TypedNumber.Parse(form.Value);
        var min = TypedNumber.Parse(form.Minimum);
        var max = TypedNumber.Parse(form.Maximum);
        return value < unit.MinimumCelsius || value > unit.MaximumCelsius || min < unit.MinimumCelsius || max > unit.MaximumCelsius;
    }

    /// <summary>
    /// A reading refused on its box: a value is required, a minimum or maximum typed « -25 C » is no
    /// number (it was dropped without a word, and the excursion it showed was neither asked about
    /// nor recorded), and an excursion takes a comment.
    /// </summary>
    public static (FieldRefusal? Refusal, TemperatureReadingRequest? Request) Check(TemperatureReadingForm form, StorageUnitRow unit)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(unit);

        if (TypedNumber.IsUnreadable(form.Value)) return (new FieldRefusal(ValueField, "Result_NotANumber", form.Value!.Trim()), null);
        if (TypedNumber.Parse(form.Value) is not { } value) return (new FieldRefusal(ValueField, "Temperature_ValueRequired"), null);
        if (TypedNumber.IsUnreadable(form.Minimum)) return (new FieldRefusal(MinimumField, "Result_NotANumber", form.Minimum!.Trim()), null);
        if (TypedNumber.IsUnreadable(form.Maximum)) return (new FieldRefusal(MaximumField, "Result_NotANumber", form.Maximum!.Trim()), null);

        if (IsExcursion(form, unit) && string.IsNullOrWhiteSpace(form.Comment))
            return (new FieldRefusal(CommentField, "Temperature_CommentRequired"), null);

        return (null, new TemperatureReadingRequest
        {
            StorageUnitId = unit.StorageUnitId,
            Celsius = value,
            PeriodMinimumCelsius = TypedNumber.Parse(form.Minimum),
            PeriodMaximumCelsius = TypedNumber.Parse(form.Maximum),
            Comment = Blank(form.Comment)
        });
    }

    /// <summary>What an excursion recorded says: the unit and the non-conformity it opened.</summary>
    public static string? Excursion(TemperatureReadingOutcome outcome, StorageUnitRow unit, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(text);
        return outcome.OutOfRange ? text("Temperatures.ExcursionRecorded", unit.Name, outcome.NonConformityReference) : null;
    }

    // ---- A unit --------------------------------------------------------------------------------------

    /// <summary>« 2 », « -25 »: a bound written for its box.</summary>
    public static string Bound(decimal value, IFormatProvider? provider = null) =>
        TypedNumber.WithoutDirectionMarks(value.ToString("0.##", provider ?? CultureInfo.CurrentCulture));

    /// <summary>A new unit takes the usual range of its kind; an existing one keeps what was set.</summary>
    public static StorageUnitForm Form(StorageUnitRow? row, IFormatProvider? provider = null)
    {
        var kind = row?.Kind ?? StorageUnitKind.Refrigerator;
        var (min, max) = StorageUnit.DefaultRange(kind);
        return new StorageUnitForm
        {
            Name = row?.Name,
            Kind = kind,
            Minimum = Bound(row?.MinimumCelsius ?? min, provider),
            Maximum = Bound(row?.MaximumCelsius ?? max, provider),
            ReadingsPerDay = row?.ReadingsPerDay ?? 2,
            Location = row?.Location,
            SerialNumber = row?.SerialNumber,
            IsActive = row?.IsActive ?? true
        };
    }

    /// <summary>The kind changed on a new unit: its usual range put in the boxes.</summary>
    public static void KindChanged(StorageUnitForm form, bool isNew, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!isNew) return;
        var (min, max) = StorageUnit.DefaultRange(form.Kind);
        form.Minimum = Bound(min, provider);
        form.Maximum = Bound(max, provider);
    }

    public static (FieldRefusal? Refusal, StorageUnitRequest? Request) Check(StorageUnitForm form, Guid? unitId)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (string.IsNullOrWhiteSpace(form.Name)) return (new FieldRefusal(NameField, "StorageUnit_NameRequired"), null);
        if (TypedNumber.Parse(form.Minimum) is not { } min || TypedNumber.Parse(form.Maximum) is not { } max || min >= max)
            return (new FieldRefusal(RangeField, "StorageUnit_RangeInvalid"), null);

        return (null, new StorageUnitRequest
        {
            StorageUnitId = unitId,
            Name = form.Name.Trim(),
            Kind = form.Kind,
            Location = Blank(form.Location),
            SerialNumber = Blank(form.SerialNumber),
            MinimumCelsius = min,
            MaximumCelsius = max,
            ReadingsPerDay = Math.Clamp(form.ReadingsPerDay, 1, ReadingsPerDayMax),
            IsActive = form.IsActive
        });
    }

    /// <summary>The box of the unit dialog a refusal of TemperatureMonitoringService names.</summary>
    public static string? UnitFieldOf(Error? error) => error?.ResourceKey switch
    {
        "StorageUnit_NameRequired" or "StorageUnit_NameTaken" => NameField,
        "StorageUnit_RangeInvalid" or "Temperature_ValueImplausible" => RangeField,
        "StorageUnit_ReadingsPerDayInvalid" => PerDayField,
        _ => null
    };

    /// <summary>The box of the reading dialog a refusal of TemperatureMonitoringService names.</summary>
    public static string? ReadingFieldOf(Error? error) => error?.ResourceKey switch
    {
        "Temperature_ValueRequired" or "Temperature_ValueImplausible" => ValueField,
        "Temperature_MinAboveMax" or "Temperature_ReadingOutsideMinMax" => MinimumField,
        "Temperature_CommentRequired" => CommentField,
        _ => null
    };

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
