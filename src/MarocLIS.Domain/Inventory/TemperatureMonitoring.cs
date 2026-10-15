using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Inventory;

/// <summary>
/// A refrigerator, freezer or room whose temperature the laboratory monitors. ISO 15189 §6.3.3
/// asks for the storage conditions of reagents and samples to be monitored and recorded; the
/// record is the daily reading, and a reading outside the range is a non-conformity.
/// </summary>
public sealed class StorageUnit : AuditableAggregateRoot, ISiteScoped
{
    private StorageUnit() { }

    public StorageUnit(string name, StorageUnitKind kind, Guid siteId)
    {
        Name = name.Trim();
        Kind = kind;
        SiteId = siteId;
        (MinimumCelsius, MaximumCelsius) = DefaultRange(kind);
    }

    public string Name { get; set; } = string.Empty;
    public StorageUnitKind Kind { get; set; }
    public Guid SiteId { get; set; }

    /// <summary>Where it stands: room, bench.</summary>
    public string? Location { get; set; }

    /// <summary>Asset or serial number, to tie the readings to the equipment file.</summary>
    public string? SerialNumber { get; set; }

    public decimal MinimumCelsius { get; set; }
    public decimal MaximumCelsius { get; set; }

    /// <summary>Readings expected each day; two (morning and evening) is the usual practice.</summary>
    public int ReadingsPerDay { get; set; } = 2;

    public bool IsActive { get; set; } = true;

    /// <summary>The usual acceptable range of each kind of unit.</summary>
    public static (decimal Minimum, decimal Maximum) DefaultRange(StorageUnitKind kind) => kind switch
    {
        StorageUnitKind.Refrigerator => (2m, 8m),
        StorageUnitKind.Freezer20 => (-25m, -15m),
        StorageUnitKind.Freezer80 => (-90m, -70m),
        _ => (15m, 25m)
    };

    public bool IsInRange(decimal celsius) => celsius >= MinimumCelsius && celsius <= MaximumCelsius;

    public Result Validate()
    {
        var errors = new List<Error>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add(Error.Validation("StorageUnit_NameRequired"));
        if (MinimumCelsius >= MaximumCelsius) errors.Add(Error.Validation("StorageUnit_RangeInvalid"));
        if (ReadingsPerDay is < 1 or > 24) errors.Add(Error.Validation("StorageUnit_ReadingsPerDayInvalid"));
        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }
}

public enum StorageUnitKind
{
    /// <summary>2–8 °C.</summary>
    Refrigerator = 0,
    /// <summary>−20 °C.</summary>
    Freezer20 = 1,
    /// <summary>−80 °C.</summary>
    Freezer80 = 2,
    /// <summary>Room or ambient storage, 15–25 °C.</summary>
    Room = 3
}

/// <summary>
/// One temperature reading of a storage unit, with the minimum and maximum the thermometer
/// recorded since the previous reading when it has them. Never edited: a wrong reading is
/// followed by a corrected one with a comment.
/// </summary>
public sealed class TemperatureReading : AggregateRoot, IAuditable, ISiteScoped
{
    private TemperatureReading() { }

    public TemperatureReading(Guid storageUnitId, Guid siteId, DateTimeOffset readAt, decimal celsius,
                              Guid byUserId)
    {
        StorageUnitId = storageUnitId;
        SiteId = siteId;
        ReadAt = readAt;
        Celsius = celsius;
        ByUserId = byUserId;
    }

    public Guid StorageUnitId { get; private set; }
    public Guid SiteId { get; set; }

    public DateTimeOffset ReadAt { get; private set; }
    public decimal Celsius { get; private set; }

    /// <summary>Minimum and maximum since the previous reading, from a min/max thermometer.</summary>
    public decimal? PeriodMinimumCelsius { get; init; }
    public decimal? PeriodMaximumCelsius { get; init; }

    public Guid ByUserId { get; private set; }
    public string? Comment { get; init; }

    /// <summary>Set when the reading or the period's extremes left the unit's range.</summary>
    public bool IsOutOfRange { get; private set; }

    /// <summary>The non-conformity the excursion opened.</summary>
    public Guid? NonConformityId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    /// <summary>
    /// Checks the reading against the unit's range. A reading back in range with a period maximum
    /// above it is still an excursion: the reagents spent that time warm.
    /// </summary>
    public bool Evaluate(StorageUnit unit)
    {
        IsOutOfRange = !unit.IsInRange(Celsius)
                       || PeriodMinimumCelsius is { } low && low < unit.MinimumCelsius
                       || PeriodMaximumCelsius is { } high && high > unit.MaximumCelsius;
        return IsOutOfRange;
    }

    public static Result ValidateInput(decimal celsius, decimal? periodMinimum, decimal? periodMaximum,
                                       bool outOfRange, string? comment)
    {
        if (celsius is < -200m or > 100m) return Result.Failure(Error.Validation("Temperature_ValueImplausible"));
        if (periodMinimum is { } low && periodMaximum is { } high && low > high)
            return Result.Failure(Error.Validation("Temperature_MinAboveMax"));
        if (periodMinimum is { } min && min > celsius || periodMaximum is { } max && max < celsius)
            return Result.Failure(Error.Validation("Temperature_ReadingOutsideMinMax"));

        // An excursion is recorded with what was done about it: the non-conformity opened on it
        // needs that much to be worked on.
        if (outOfRange && string.IsNullOrWhiteSpace(comment))
            return Result.Failure(Error.Validation("Temperature_CommentRequired"));

        return Result.Success();
    }
}

