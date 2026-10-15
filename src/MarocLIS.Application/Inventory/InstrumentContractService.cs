using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;

namespace MarocLIS.Application.Inventory;

/// <summary>One contract on the dashboard, with where it stands.</summary>
public sealed record InstrumentContractRow(
    Guid Id,
    string Reference,
    Guid SupplierId,
    string SupplierName,
    InstrumentContractKind Kind,
    DateOnly StartDate,
    DateOnly EndDate,
    int DaysToEnd,
    bool IsActive,
    bool IsEndingSoon,
    string Instruments,
    CommitmentStatus Commitment,
    int Interventions,
    decimal DowntimeHours,
    int DelaysExceeded,
    int? MaxInterventionDelayHours,
    string? FileName);

/// <summary>A contract as the editor shows it.</summary>
public sealed record InstrumentContractDetail(
    Guid Id,
    string Reference,
    Guid SupplierId,
    InstrumentContractKind Kind,
    DateOnly StartDate,
    int DurationMonths,
    CommitmentBasis CommitmentBasis,
    decimal CommittedVolume,
    string? CountedAnalysisCodesCsv,
    int? PreventiveVisitsPerYear,
    int? MaxInterventionDelayHours,
    string? MaintenanceTerms,
    string? TrainingTerms,
    string? ExitConditions,
    string? PenaltyTerms,
    decimal? PenaltyAmountMad,
    string? Notes,
    bool IsActive,
    IReadOnlyList<Guid> InstrumentIds,
    IReadOnlyList<InstrumentContractReagentInput> Reagents);

/// <summary>An intervention on one of a contract's analysers, with its delay against the contract.</summary>
public sealed record ContractInterventionRow(
    Guid MaintenanceRecordId,
    Guid InstrumentId,
    string InstrumentCode,
    DateOnly PerformedOn,
    MaintenanceKind Kind,
    string Description,
    decimal? DowntimeHours,
    DateTimeOffset? RequestedAt,
    DateTimeOffset? ArrivedAt,
    decimal? DelayHours,
    bool DelayExceeded,
    string? Technician);

public sealed record ContractLookup(Guid Id, string Code, string Name);

/// <summary>What the contract editor offers to pick from.</summary>
public sealed record ContractOptions(
    IReadOnlyList<ContractLookup> Suppliers,
    IReadOnlyList<ContractLookup> Instruments,
    IReadOnlyList<ContractLookup> StockItems);

public sealed record SaveInstrumentContractRequest
{
    public Guid? Id { get; init; }
    public required string Reference { get; init; }
    public required Guid SupplierId { get; init; }
    public InstrumentContractKind Kind { get; init; }
    public required DateOnly StartDate { get; init; }
    public required int DurationMonths { get; init; }
    public CommitmentBasis CommitmentBasis { get; init; }
    public decimal CommittedVolume { get; init; }
    public string? CountedAnalysisCodesCsv { get; init; }
    public int? PreventiveVisitsPerYear { get; init; }
    public int? MaxInterventionDelayHours { get; init; }
    public string? MaintenanceTerms { get; init; }
    public string? TrainingTerms { get; init; }
    public string? ExitConditions { get; init; }
    public string? PenaltyTerms { get; init; }
    public decimal? PenaltyAmountMad { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; } = true;
    public IReadOnlyList<Guid> InstrumentIds { get; init; } = [];
    public IReadOnlyList<InstrumentContractReagentInput> Reagents { get; init; } = [];
}

/// <summary>
/// Instrument placement and comodat contracts, and the dashboard that follows them: committed
/// against consumed, the projection to the end of the period with its risk alert, the end of the
/// contract approaching, and the supplier's interventions against the delay the contract allows.
/// </summary>
public sealed class InstrumentContractService(
    IRepository<InstrumentContract> contracts,
    IRepository<Instrument> instruments,
    IRepository<PurchasingDocument> documents,
    IInstrumentContractQueries queries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<Result<IReadOnlyList<InstrumentContractRow>>> GetContractsAsync(
        bool includeInactive = false, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Equipment.ViewContracts);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<InstrumentContractRow>>(permitted.FirstError);

        return Result.Success(await queries.GetContractsAsync(currentUser.SiteId, clock.Today, includeInactive, ct));
    }

    public async Task<Result<InstrumentContractDetail>> GetContractAsync(Guid contractId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Equipment.ViewContracts);
        if (permitted.IsFailure) return Result.Failure<InstrumentContractDetail>(permitted.FirstError);

        var contract = await contracts.GetByIdWithDetailsAsync(contractId, ct);
        if (contract is null) return Result.Failure<InstrumentContractDetail>(Error.NotFound("Contract_NotFound"));

        return Result.Success(new InstrumentContractDetail(
            contract.Id, contract.Reference, contract.SupplierId, contract.Kind, contract.StartDate,
            contract.DurationMonths, contract.CommitmentBasis, contract.CommittedVolume,
            contract.CountedAnalysisCodesCsv, contract.PreventiveVisitsPerYear,
            contract.MaxInterventionDelayHours, contract.MaintenanceTerms, contract.TrainingTerms,
            contract.ExitConditions, contract.PenaltyTerms, contract.PenaltyAmountMad, contract.Notes,
            contract.IsActive,
            [.. contract.Instruments.Select(i => i.InstrumentId)],
            [.. contract.Reagents.Select(r => new InstrumentContractReagentInput(r.StockItemId, r.ContractPriceMad,
                                                                                 r.CostPerTestMad))]));
    }

    public async Task<Result<ContractOptions>> GetOptionsAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Equipment.ManageContracts);
        if (permitted.IsFailure) return Result.Failure<ContractOptions>(permitted.FirstError);

        return Result.Success(await queries.GetOptionsAsync(currentUser.SiteId, ct));
    }

    public async Task<Result<Guid>> SaveAsync(SaveInstrumentContractRequest request, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Equipment.ManageContracts);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var reference = request.Reference?.Trim() ?? string.Empty;

        if (await queries.ReferenceExistsAsync(request.SupplierId, reference, request.Id, ct))
            return Result.Failure<Guid>(Error.Conflict("Contract_ReferenceExists", reference));

        InstrumentContract contract;
        if (request.Id is { } id)
        {
            var existing = await contracts.GetByIdWithDetailsAsync(id, ct);
            if (existing is null) return Result.Failure<Guid>(Error.NotFound("Contract_NotFound"));
            contract = existing;
        }
        else
        {
            contract = new InstrumentContract(reference, request.SupplierId, currentUser.SiteId, request.Kind,
                                              request.StartDate, request.DurationMonths);
        }

        contract.Reference = reference;
        contract.SupplierId = request.SupplierId;
        contract.Kind = request.Kind;
        contract.StartDate = request.StartDate;
        contract.DurationMonths = request.DurationMonths;
        contract.CommitmentBasis = request.CommitmentBasis;
        contract.CommittedVolume = request.CommitmentBasis == CommitmentBasis.None ? 0m : request.CommittedVolume;
        contract.CountedAnalysisCodesCsv = Blank(request.CountedAnalysisCodesCsv);
        contract.PreventiveVisitsPerYear = request.PreventiveVisitsPerYear;
        contract.MaxInterventionDelayHours = request.MaxInterventionDelayHours;
        contract.MaintenanceTerms = Blank(request.MaintenanceTerms);
        contract.TrainingTerms = Blank(request.TrainingTerms);
        contract.ExitConditions = Blank(request.ExitConditions);
        contract.PenaltyTerms = Blank(request.PenaltyTerms);
        contract.PenaltyAmountMad = request.PenaltyAmountMad is { } penalty ? Money.Round(penalty) : null;
        contract.Notes = Blank(request.Notes);
        contract.IsActive = request.IsActive;
        contract.SetInstruments(request.InstrumentIds);
        contract.SetReagents(request.Reagents);

        var valid = contract.Validate();
        if (valid.IsFailure) return Result.Failure<Guid>(valid.Errors);

        if (request.Id is null) await contracts.AddAsync(contract, ct);
        else contracts.Update(contract);

        unitOfWork.AuditReason = request.Id is null
            ? $"Création du contrat automate {contract.Reference}"
            : $"Modification du contrat automate {contract.Reference}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(contract.Id);
    }

    public async Task<Result> AttachFileAsync(Guid contractId, string fileName, byte[] content,
                                              CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Equipment.ManageContracts);
        if (permitted.IsFailure) return permitted;

        if (content.Length == 0 || string.IsNullOrWhiteSpace(fileName))
            return Result.Failure(Error.Validation("Purchasing_FileEmpty"));
        if (content.Length > PurchasingDocument.MaximumBytes)
            return Result.Failure(Error.Validation("Purchasing_FileTooLarge", PurchasingDocument.MaximumBytes / (1024 * 1024)));

        var contract = await contracts.GetByIdAsync(contractId, ct);
        if (contract is null) return Result.Failure(Error.NotFound("Contract_NotFound"));

        var name = Path.GetFileName(fileName.Trim());
        await documents.AddAsync(new PurchasingDocument(PurchasingDocumentOwner.InstrumentContract, contract.Id,
                                                        name, PurchasingDocument.ContentTypeFor(name), content,
                                                        clock.Now, currentUser.UserId), ct);

        unitOfWork.AuditReason = $"Contrat {contract.Reference} : fichier {name} joint";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    public async Task<Result<(string FileName, byte[] Content)>> GetFileAsync(Guid contractId,
                                                                             CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Equipment.ViewContracts);
        if (permitted.IsFailure) return Result.Failure<(string, byte[])>(permitted.FirstError);

        var file = await queries.GetDocumentAsync(PurchasingDocumentOwner.InstrumentContract, contractId, ct);
        return file is { } found
            ? Result.Success(found)
            : Result.Failure<(string, byte[])>(Error.NotFound("Purchasing_FileNotFound"));
    }

    public async Task<Result<IReadOnlyList<ContractInterventionRow>>> GetInterventionsAsync(
        Guid contractId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Equipment.ViewContracts);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<ContractInterventionRow>>(permitted.FirstError);

        return Result.Success(await queries.GetInterventionsAsync(contractId, ct));
    }

    /// <summary>
    /// Records when the supplier was called and when its technician arrived, on an intervention
    /// already entered on the equipment screen. That is what the contract's maximum delay is
    /// measured on.
    /// </summary>
    public async Task<Result> RecordInterventionTimesAsync(Guid instrumentId, Guid maintenanceRecordId,
                                                           DateTimeOffset? requestedAt, DateTimeOffset? arrivedAt,
                                                           CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Equipment.RecordMaintenance);
        if (permitted.IsFailure) return permitted;

        if (requestedAt is { } requested && arrivedAt is { } arrived && arrived < requested)
            return Result.Failure(Error.Validation("Contract_ArrivalBeforeRequest"));
        if (requestedAt > clock.Now || arrivedAt > clock.Now)
            return Result.Failure(Error.Validation("Contract_InterventionTimeInFuture"));

        var instrument = await instruments.GetByIdWithDetailsAsync(instrumentId, ct);
        if (instrument is null) return Result.Failure(Error.NotFound("Equipment_NotFound"));

        var record = instrument.MaintenanceRecords.FirstOrDefault(m => m.Id == maintenanceRecordId);
        if (record is null) return Result.Failure(Error.NotFound("Contract_InterventionNotFound"));

        record.RequestedAt = requestedAt;
        record.ArrivedAt = arrivedAt;

        unitOfWork.AuditReason = $"Heures d'appel et d'arrivée du technicien sur {instrument.Code} ({record.PerformedOn:dd/MM/yyyy})";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Read-side queries for the contract dashboard.</summary>
public interface IInstrumentContractQueries
{
    Task<IReadOnlyList<InstrumentContractRow>> GetContractsAsync(Guid siteId, DateOnly today, bool includeInactive,
                                                                CancellationToken ct = default);

    Task<IReadOnlyList<ContractInterventionRow>> GetInterventionsAsync(Guid contractId, CancellationToken ct = default);

    Task<ContractOptions> GetOptionsAsync(Guid siteId, CancellationToken ct = default);

    Task<bool> ReferenceExistsAsync(Guid supplierId, string reference, Guid? exceptContractId,
                                    CancellationToken ct = default);

    /// <summary>
    /// Tests produced on the contract's analysers in a period: one per ordered analysis measured,
    /// whatever the number of parameters it reports, restricted to the counted analyses if the
    /// contract lists them.
    /// </summary>
    Task<int> CountTestsAsync(IReadOnlyList<Guid> instrumentIds, IReadOnlyList<string> analysisCodes,
                              DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>
    /// Amount excluding VAT of captive reagents ordered from the contract's supplier in a period, on
    /// purchase orders that were approved (a draft or a cancelled order commits nothing).
    /// </summary>
    Task<decimal> SumPurchasesAsync(Guid supplierId, IReadOnlyList<Guid> stockItemIds, DateOnly from, DateOnly to,
                                    CancellationToken ct = default);

    Task<(string FileName, byte[] Content)?> GetDocumentAsync(PurchasingDocumentOwner ownerKind, Guid ownerId,
                                                              CancellationToken ct = default);
}
