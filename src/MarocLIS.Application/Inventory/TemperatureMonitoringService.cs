using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.Inventory;

/// <summary>A storage unit with today's state, as the daily entry screen lists it.</summary>
public sealed record StorageUnitRow(
    Guid StorageUnitId,
    string Name,
    StorageUnitKind Kind,
    string? Location,
    string? SerialNumber,
    decimal MinimumCelsius,
    decimal MaximumCelsius,
    int ReadingsPerDay,
    bool IsActive,
    int ReadingsToday,
    decimal? LastCelsius,
    DateTimeOffset? LastReadAt,
    bool LastOutOfRange)
{
    /// <summary>No reading yet today: the alert the screen and the dashboard raise.</summary>
    public bool MissingToday => IsActive && ReadingsToday == 0;
}

/// <summary>One reading of the monthly sheet.</summary>
public sealed record TemperatureReadingRow(
    Guid ReadingId,
    DateTimeOffset ReadAt,
    decimal Celsius,
    decimal? PeriodMinimumCelsius,
    decimal? PeriodMaximumCelsius,
    bool IsOutOfRange,
    string ByUserName,
    string? Comment,
    string? NonConformityReference);

public sealed record StorageUnitRequest
{
    public Guid? StorageUnitId { get; init; }
    public required string Name { get; init; }
    public StorageUnitKind Kind { get; init; }
    public string? Location { get; init; }
    public string? SerialNumber { get; init; }
    public decimal MinimumCelsius { get; init; }
    public decimal MaximumCelsius { get; init; }
    public int ReadingsPerDay { get; init; } = 2;
    public bool IsActive { get; init; } = true;
}

public sealed record TemperatureReadingRequest
{
    public required Guid StorageUnitId { get; init; }
    public required decimal Celsius { get; init; }
    public decimal? PeriodMinimumCelsius { get; init; }
    public decimal? PeriodMaximumCelsius { get; init; }
    public string? Comment { get; init; }
}

/// <summary>What recording a reading did: whether it was an excursion, and the non-conformity it opened.</summary>
public sealed record TemperatureReadingOutcome(bool OutOfRange, string? NonConformityReference);

public interface ITemperatureQueries
{
    Task<IReadOnlyList<StorageUnitRow>> GetUnitsAsync(Guid siteId, DateOnly today, bool includeInactive,
                                                      CancellationToken ct = default);

    Task<IReadOnlyList<TemperatureReadingRow>> GetReadingsAsync(Guid storageUnitId, DateOnly from, DateOnly to,
                                                                CancellationToken ct = default);

    Task<bool> NameExistsAsync(Guid siteId, string name, Guid? exceptUnitId, CancellationToken ct = default);
}

/// <summary>
/// Fridge, freezer and room temperatures (ISO 15189 §6.3.3, storage conditions): the units and
/// their ranges, the daily readings, and the non-conformity an excursion opens on its own — the
/// same way a failed reception check opens one, so it is followed in the same register.
/// </summary>
public sealed class TemperatureMonitoringService(
    IRepository<StorageUnit> units,
    IRepository<TemperatureReading> readings,
    IRepository<NonConformity> nonConformities,
    ITemperatureQueries queries,
    ISequenceAllocator sequences,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    private static readonly string[] ReadRights =
    [
        Permissions.Inventory.RecordTemperatures, Permissions.Inventory.ManageStorageUnits, Permissions.Quality.View
    ];

    public async Task<Result<IReadOnlyList<StorageUnitRow>>> GetUnitsAsync(bool includeInactive = false,
                                                                          CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(ReadRights);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<StorageUnitRow>>(permitted.FirstError);

        return Result.Success(await queries.GetUnitsAsync(currentUser.SiteId, clock.Today, includeInactive, ct));
    }

    /// <summary>The readings of a unit over a calendar month, oldest first, for the monthly sheet.</summary>
    public async Task<Result<IReadOnlyList<TemperatureReadingRow>>> GetMonthAsync(Guid storageUnitId, int year,
                                                                                 int month, CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(ReadRights);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<TemperatureReadingRow>>(permitted.FirstError);

        if (month is < 1 or > 12 || year is < 2000 or > 2100)
            return Result.Failure<IReadOnlyList<TemperatureReadingRow>>(Error.Validation("Stock_PeriodInvalid"));

        var first = new DateOnly(year, month, 1);
        return Result.Success(await queries.GetReadingsAsync(storageUnitId, first, first.AddMonths(1).AddDays(-1), ct));
    }

    public async Task<Result<Guid>> SaveUnitAsync(StorageUnitRequest request, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.ManageStorageUnits);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var name = request.Name?.Trim() ?? string.Empty;

        StorageUnit unit;
        if (request.StorageUnitId is { } id)
        {
            var existing = await units.GetByIdAsync(id, ct);
            if (existing is null) return Result.Failure<Guid>(Error.NotFound("StorageUnit_NotFound"));
            unit = existing;
        }
        else
        {
            unit = new StorageUnit(name, request.Kind, currentUser.SiteId);
        }

        unit.Name = name;
        unit.Kind = request.Kind;
        unit.Location = Blank(request.Location);
        unit.SerialNumber = Blank(request.SerialNumber);
        unit.MinimumCelsius = request.MinimumCelsius;
        unit.MaximumCelsius = request.MaximumCelsius;
        unit.ReadingsPerDay = request.ReadingsPerDay;
        unit.IsActive = request.IsActive;

        var valid = unit.Validate();
        if (valid.IsFailure) return Result.Failure<Guid>(valid.Errors);

        if (await queries.NameExistsAsync(currentUser.SiteId, name, request.StorageUnitId, ct))
            return Result.Failure<Guid>(Error.Conflict("StorageUnit_NameTaken", name));

        if (request.StorageUnitId is null) await units.AddAsync(unit, ct);
        else units.Update(unit);

        unitOfWork.AuditReason = request.StorageUnitId is null
            ? $"Création de l'enceinte {name} ({unit.MinimumCelsius:0.#} à {unit.MaximumCelsius:0.#} °C)"
            : $"Modification de l'enceinte {name} ({unit.MinimumCelsius:0.#} à {unit.MaximumCelsius:0.#} °C)";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(unit.Id);
    }

    /// <summary>
    /// Records a reading. Outside the unit's range — the reading itself or the minimum/maximum of
    /// the period — it needs a comment and opens a non-conformity, whose reference is returned.
    /// </summary>
    public async Task<Result<TemperatureReadingOutcome>> RecordReadingAsync(TemperatureReadingRequest request,
                                                                           CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.RecordTemperatures);
        if (permitted.IsFailure) return Result.Failure<TemperatureReadingOutcome>(permitted.FirstError);

        var unit = await units.GetByIdAsync(request.StorageUnitId, ct);
        if (unit is null || !unit.IsActive)
            return Result.Failure<TemperatureReadingOutcome>(Error.NotFound("StorageUnit_NotFound"));

        var reading = new TemperatureReading(unit.Id, unit.SiteId, clock.Now, request.Celsius, currentUser.UserId)
        {
            PeriodMinimumCelsius = request.PeriodMinimumCelsius,
            PeriodMaximumCelsius = request.PeriodMaximumCelsius,
            Comment = Blank(request.Comment)
        };

        var outOfRange = reading.Evaluate(unit);

        var valid = TemperatureReading.ValidateInput(request.Celsius, request.PeriodMinimumCelsius,
                                                     request.PeriodMaximumCelsius, outOfRange, request.Comment);
        if (valid.IsFailure) return Result.Failure<TemperatureReadingOutcome>(valid.Errors);

        if (!outOfRange)
        {
            await readings.AddAsync(reading, ct);
            unitOfWork.AuditReason = $"Relevé de température {unit.Name} : {request.Celsius:0.0} °C";
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success(new TemperatureReadingOutcome(false, null));
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var reference = await sequences.NextAsync(SequencePurpose.NonConformity, null, clock.Today.Year, ct);
        if (reference.IsFailure) return Result.Failure<TemperatureReadingOutcome>(reference.Errors);

        var measured = Describe(request);
        var range = $"{unit.MinimumCelsius:0.#} à {unit.MaximumCelsius:0.#} °C";

        var nonConformity = new NonConformity(reference.Value, unit.SiteId, NonConformityCategory.Equipment,
                                              NonConformitySeverity.Major,
                                              new MultilingualText(
                                                  $"Température hors limites — {unit.Name} : {measured} (plage {range})",
                                                  $"درجة حرارة خارج الحدود — {unit.Name} : {measured}",
                                                  $"Temperature out of range — {unit.Name}: {measured} (range {range})"),
                                              clock.Now, currentUser.UserId)
        {
            DetectionSource = DetectionSource.StaffObservation,
            ImmediateAction = new MultilingualText(reading.Comment ?? string.Empty),
            DueDate = clock.Today.AddDays(7)
        };

        await nonConformities.AddAsync(nonConformity, ct);
        reading.NonConformityId = nonConformity.Id;
        await readings.AddAsync(reading, ct);

        unitOfWork.AuditReason =
            $"Relevé de température hors limites {unit.Name} : {measured}, non-conformité {nonConformity.Reference}";
        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return Result.Success(new TemperatureReadingOutcome(true, nonConformity.Reference));
    }

    private static string Describe(TemperatureReadingRequest r)
    {
        var text = $"{r.Celsius:0.0} °C";
        if (r.PeriodMinimumCelsius is { } min) text += $", min. {min:0.0} °C";
        if (r.PeriodMaximumCelsius is { } max) text += $", max. {max:0.0} °C";
        return text;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
