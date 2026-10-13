using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Instruments;

/// <summary>What the interface screen captures for one analyser link.</summary>
public sealed record InterfaceRequest
{
    public Guid? Id { get; init; }

    public required Guid InstrumentId { get; init; }

    public InstrumentProtocol Protocol { get; init; }
    public InstrumentTransportKind Transport { get; init; }
    public InstrumentDirection Direction { get; init; } = InstrumentDirection.Unidirectional;
    public bool IsEnabled { get; init; }

    public string? SerialPortName { get; init; }
    public int BaudRate { get; init; } = 9600;
    public int DataBits { get; init; } = 8;
    public string Parity { get; init; } = "None";
    public string StopBits { get; init; } = "One";
    public string Handshake { get; init; } = "None";

    public string? Host { get; init; }
    public int? TcpPort { get; init; }
    public bool ListenAsServer { get; init; }

    public string? InboundDirectory { get; init; }
    public string? OutboundDirectory { get; init; }
    public string? FilePattern { get; init; } = "*.txt";
    public string? ProcessedDirectory { get; init; }

    public string Encoding { get; init; } = "ISO-8859-1";
    public int AcknowledgementTimeoutSeconds { get; init; } = 15;
    public int MaxRetries { get; init; } = 3;

    public bool AutoAcceptResults { get; init; }
    public bool RaiseIssueOnUnmappedCode { get; init; } = true;
    public bool RouteControlResultsToQc { get; init; } = true;
}

/// <summary>One analyser-code-to-parameter translation.</summary>
public sealed record MappingRequest(string InstrumentTestCode, Guid TestParameterId,
                                     string? InstrumentUnit, decimal? ConversionFactor,
                                     decimal? ConversionOffset, string? CodedValueMap,
                                     bool IsEnabled);

public sealed record InterfaceRow(
    Guid Id, Guid InstrumentId, string InstrumentCode, MultilingualText InstrumentName,
    InstrumentProtocol Protocol, InstrumentTransportKind Transport, InstrumentDirection Direction,
    bool IsEnabled, string Endpoint, string Encoding,
    int AcknowledgementTimeoutSeconds, int MaxRetries,
    bool AutoAcceptResults, bool RaiseIssueOnUnmappedCode, bool RouteControlResultsToQc,
    DateTimeOffset? LastConnectedAt, DateTimeOffset? LastMessageAt, string? LastError,
    int MappingCount, int EnabledMappingCount, int MessagesToday, int FailedToday,
    string? SerialPortName, int BaudRate, int DataBits, string Parity, string StopBits,
    string Handshake, string? Host, int? TcpPort, bool ListenAsServer,
    string? InboundDirectory, string? OutboundDirectory, string? FilePattern,
    string? ProcessedDirectory)
{
    /// <summary>
    /// Enabled, bidirectional and with no mappings: the analyser will query for a worklist and get
    /// an empty one, so every tube comes back with no results and nobody can see why.
    /// </summary>
    public bool IsUnusable =>
        IsEnabled && (EnabledMappingCount == 0
                      || (Direction != InstrumentDirection.Unidirectional && MappingCount == 0));

    public bool HasRecentFailures => FailedToday > 0;
}

public sealed record MappingRow(
    Guid Id, Guid InstrumentInterfaceId, string InstrumentTestCode, Guid TestParameterId,
    string ParameterCode, MultilingualText ParameterName, string AnalysisCode,
    ResultValueType ValueType, string? CatalogueUnit, string? InstrumentUnit,
    decimal? ConversionFactor, decimal? ConversionOffset, string? CodedValueMap, bool IsEnabled)
{
    /// <summary>
    /// The analyser reports a different unit and no conversion is configured. A hundredfold error
    /// on a report starts here, and it is the one mapping fault that produces a plausible number.
    /// </summary>
    public bool UnitMismatchWithoutConversion =>
        InstrumentUnit is { Length: > 0 } instrument
        && CatalogueUnit is { Length: > 0 } catalogue
        && !string.Equals(instrument, catalogue, StringComparison.OrdinalIgnoreCase)
        && ConversionFactor is null;

    /// <summary>A coded parameter with no value translation takes the analyser's own vocabulary.</summary>
    public bool CodedWithoutMap =>
        ValueType is ResultValueType.Coded or ResultValueType.SemiQuantitative
        && string.IsNullOrWhiteSpace(CodedValueMap);
}

public sealed record MessageLogRow(
    Guid Id, Guid InstrumentInterfaceId, MessageDirection Direction, DateTimeOffset OccurredAt,
    InstrumentMessageProcessingStatus Status, string? SpecimenIds, int? ResultCount,
    int? ImportedCount, string? Issues, DateTimeOffset? ProcessedAt,
    DateTimeOffset? ReplayedAt, string? ReplayedByName, int RawLength)
{
    public bool CanReplay =>
        Direction == MessageDirection.Inbound
        && Status is InstrumentMessageProcessingStatus.Failed
                  or InstrumentMessageProcessingStatus.PartiallyImported
                  or InstrumentMessageProcessingStatus.AwaitingAcceptance;

    public bool WasReplayed => ReplayedAt is not null;

    public int DroppedCount => (ResultCount ?? 0) - (ImportedCount ?? 0);
}

/// <summary>
/// Configuration and supervision of the analyser links.
/// <para>
/// Separate from the host that runs them: this is what an operator changes, and the host picks the
/// change up on its next pass. Keeping them apart is what lets an interface be configured on any
/// workstation while exactly one drives the hardware.
/// </para>
/// </summary>
public sealed class InstrumentInterfaceService(
    IRepository<InstrumentInterface> interfaces,
    IRepository<Instrument> instruments,
    IInstrumentMessageLogStore messageLog,
    IInstrumentResultReplayer replayer,
    InstrumentHostOptions hostOptions,
    ISecurityAuditor securityAuditor,
    IInstrumentInterfaceQueries queries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<Result<IReadOnlyList<InterfaceRow>>> GetInterfacesAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Instruments.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<InterfaceRow>>(permitted.FirstError);

        return Result.Success(await queries.GetInterfacesAsync(currentUser.SiteId, clock.Today, ct));
    }

    public async Task<Result<IReadOnlyList<MappingRow>>> GetMappingsAsync(
        Guid interfaceId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Instruments.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<MappingRow>>(permitted.FirstError);

        return Result.Success(await queries.GetMappingsAsync(interfaceId, ct));
    }

    public async Task<Result<IReadOnlyList<MessageLogRow>>> GetMessagesAsync(
        Guid? interfaceId, int take = 200, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Instruments.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<MessageLogRow>>(permitted.FirstError);

        return Result.Success(await queries.GetMessagesAsync(interfaceId, take, ct));
    }

    /// <summary>The frame exactly as it went over the wire — the evidence, not a rendering of it.</summary>
    public async Task<Result<string>> GetRawMessageAsync(Guid logId, CancellationToken ct = default)
    {
        // The raw frame carries the patient's identity and results, not only the link's health.
        var permitted = guard.RequireAll(Permissions.Instruments.View, Permissions.Results.View);
        if (permitted.IsFailure) return Result.Failure<string>(permitted.FirstError);

        var entry = await messageLog.GetByIdAsync(logId, ct);

        return entry is null
            ? Result.Failure<string>(Error.NotFound("Interface_MessageNotFound"))
            : Result.Success(entry.RawContent);
    }

    /// <summary>
    /// Re-imports a logged frame, and reports how many values were stored.
    /// <para>
    /// A separate permission from <see cref="Permissions.Instruments.Configure"/> on purpose: a
    /// replay writes patient values, which is a bench decision, while configuring a link is an
    /// administrative one. The two are held by different people in most laboratories.
    /// </para>
    /// <para>
    /// This is the path that makes auto-acceptance optional. With it off, every transmission is held
    /// at <see cref="InstrumentMessageProcessingStatus.AwaitingAcceptance"/> and this is the only
    /// thing that releases it — so a laboratory can keep the analyser's values out of patient files
    /// until somebody has looked at them, which is the posture the configuration screen recommends.
    /// </para>
    /// </summary>
    public async Task<Result<int>> ReplayMessageAsync(Guid logId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Instruments.ReplayMessages);
        if (permitted.IsFailure) return Result.Failure<int>(permitted.FirstError);

        // Every precondition is re-checked inside the replayer. Nothing is trusted from the screen.
        return await replayer.ReplayAsync(logId, currentUser.UserId, ct);
    }

    // ---- Retention -----------------------------------------------------------------------------

    /// <summary>
    /// What a purge would remove and what it would keep, at the configured retention.
    /// <para>
    /// Read with the view permission, not the maintenance one: everybody who works with the log
    /// should be able to see how large it has grown and how much of it is unfinished work, and only
    /// the person who may act on that needs the right to do so.
    /// </para>
    /// </summary>
    public async Task<Result<MessageLogCensus>> GetMessageLogCensusAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Instruments.View);
        if (permitted.IsFailure) return Result.Failure<MessageLogCensus>(permitted.FirstError);

        return Result.Success(await messageLog.CensusAsync(Cutoff(), ct));
    }

    /// <summary>
    /// Deletes finished frames older than <see cref="InstrumentHostOptions.MessageLogRetentionDays"/>,
    /// and returns how many went.
    /// <para>
    /// The frame log is evidence — it is what says what an analyser actually transmitted when a
    /// clinician disputes a value — so it is not purged on a timer behind anybody's back. It is
    /// purged by a named person, with a stated reason, recorded as a
    /// <see cref="SecurityEventKind.DataPurged"/> event. That it also keeps the database under the
    /// Express size ceiling is a consequence, not the justification.
    /// </para>
    /// <para>
    /// Frames still replayable are never removed, whatever their age. A frame awaiting acceptance is
    /// pending work, and a failed or partial one is the last route by which its values can still
    /// reach a patient file; age does not make either of those disposable. The census reports them
    /// so a laboratory that has accumulated them is told, rather than quietly relieved of them.
    /// </para>
    /// </summary>
    public async Task<Result<int>> PurgeMessageLogAsync(string reason,
                                                         CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.RunMaintenance);
        if (permitted.IsFailure) return Result.Failure<int>(permitted.FirstError);

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
            return Result.Failure<int>(Error.Validation("Interface_PurgeReasonRequired"));

        // A retention of zero or less would delete the log the moment it was written, which is a
        // misconfiguration rather than an instruction.
        if (hostOptions.MessageLogRetentionDays < 1)
            return Result.Failure<int>(
                Error.Validation("Interface_RetentionNotConfigured",
                                  hostOptions.MessageLogRetentionDays));

        var cutoff = Cutoff();
        var purged = await messageLog.PurgeAsync(cutoff, ct);

        // Written after the delete and unconditionally, zero included: "the purge ran and removed
        // nothing" is a fact an auditor may need, and it is also how a laboratory discovers that its
        // retention is set longer than it believed.
        await securityAuditor.LogAsync(
            SecurityEventKind.DataPurged,
            succeeded: true,
            details: $"eqp.InstrumentMessageLog: {purged} frame(s) before "
                     + $"{cutoff:yyyy-MM-dd} ({hostOptions.MessageLogRetentionDays} d) - "
                     + reason.Trim(),
            ct: ct);

        return Result.Success(purged);
    }

    private DateTimeOffset Cutoff() =>
        clock.Now.AddDays(-Math.Max(hostOptions.MessageLogRetentionDays, 1));

    // ---- Configuration -------------------------------------------------------------------------

    public async Task<Result<Guid>> SaveInterfaceAsync(InterfaceRequest request,
                                                        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Instruments.Configure);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var instrument = await instruments.GetByIdAsync(request.InstrumentId, ct);
        if (instrument is null) return Result.Failure<Guid>(Error.NotFound("Instrument_NotFound"));

        InstrumentInterface link;

        if (request.Id is null)
        {
            // One link per analyser. Two links to the same instrument would both claim its results
            // and the second would store them against the first's mappings.
            var existing = await interfaces.ListAsync(i => i.InstrumentId == request.InstrumentId, ct);

            if (existing.Count > 0)
                return Result.Failure<Guid>(Error.Conflict("Interface_AlreadyExistsForInstrument",
                                                             instrument.Code));

            link = new InstrumentInterface(request.InstrumentId, currentUser.SiteId,
                                            request.Protocol, request.Transport);

            await interfaces.AddAsync(link, ct);
        }
        else
        {
            var found = await interfaces.GetByIdWithDetailsAsync(request.Id.Value, ct);
            if (found is null) return Result.Failure<Guid>(Error.NotFound("Interface_NotFound"));
            link = found;
        }

        Apply(link, request);

        // Validated before it can be enabled, not after. An enabled interface with no port named
        // retries forever and buries the reason in the log.
        if (request.IsEnabled)
        {
            var valid = link.Validate();
            if (valid.IsFailure) return Result.Failure<Guid>(valid.Errors);

            // Auto-acceptance means analyser values reach patient files with nobody looking. It is
            // a legitimate configuration for a well-commissioned analyser, but it must not be
            // possible while the code mappings are incomplete.
            if (link.AutoAcceptResults && !link.Mappings.Any(m => m.IsEnabled))
                return Result.Failure<Guid>(Error.Regulatory("Interface_AutoAcceptNeedsMappings"));
        }

        unitOfWork.AuditReason =
            $"{(request.Id is null ? "Création" : "Modification")} de l'interface automate "
            + $"{instrument.Code} ({request.Protocol}/{request.Transport})"
            + (request.IsEnabled ? " — activée" : " — désactivée");

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(link.Id);
    }

    private static void Apply(InstrumentInterface link, InterfaceRequest request)
    {
        link.Protocol = request.Protocol;
        link.Transport = request.Transport;
        link.Direction = request.Direction;
        link.IsEnabled = request.IsEnabled;

        link.SerialPortName = Trimmed(request.SerialPortName);
        link.BaudRate = request.BaudRate;
        link.DataBits = request.DataBits;
        link.Parity = request.Parity;
        link.StopBits = request.StopBits;
        link.Handshake = request.Handshake;

        link.Host = Trimmed(request.Host);
        link.TcpPort = request.TcpPort;
        link.ListenAsServer = request.ListenAsServer;

        link.InboundDirectory = Trimmed(request.InboundDirectory);
        link.OutboundDirectory = Trimmed(request.OutboundDirectory);
        link.FilePattern = Trimmed(request.FilePattern) ?? "*.txt";
        link.ProcessedDirectory = Trimmed(request.ProcessedDirectory);

        link.Encoding = string.IsNullOrWhiteSpace(request.Encoding)
            ? "ISO-8859-1"
            : request.Encoding.Trim();

        link.AcknowledgementTimeoutSeconds = Math.Clamp(request.AcknowledgementTimeoutSeconds, 1, 300);
        link.MaxRetries = Math.Clamp(request.MaxRetries, 0, 10);

        link.AutoAcceptResults = request.AutoAcceptResults;
        link.RaiseIssueOnUnmappedCode = request.RaiseIssueOnUnmappedCode;
        link.RouteControlResultsToQc = request.RouteControlResultsToQc;
    }

    /// <summary>
    /// Replaces the whole mapping table for one interface.
    /// <para>
    /// Wholesale rather than row by row because the table is read as a unit and an operator edits
    /// it as one. A mapping in use is never silently dropped, though: removing one that an inbound
    /// code still arrives under would turn every subsequent transmission of that code into an
    /// unmapped issue, so a removal disables the row and keeps it visible.
    /// </para>
    /// </summary>
    public async Task<Result> SaveMappingsAsync(Guid interfaceId,
                                                 IReadOnlyList<MappingRequest> mappings,
                                                 CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Instruments.Configure);
        if (permitted.IsFailure) return permitted;

        var link = await interfaces.GetByIdWithDetailsAsync(interfaceId, ct);
        if (link is null) return Result.Failure(Error.NotFound("Interface_NotFound"));

        var errors = new List<Error>();

        var codes = mappings.Select(m => m.InstrumentTestCode.Trim()).ToList();

        if (codes.Any(string.IsNullOrWhiteSpace))
            errors.Add(Error.Validation("Interface_MappingCodeRequired"));

        // Case-sensitive: some analysers distinguish "Na" from "NA", and collapsing them here would
        // make two different measurands share one mapping.
        if (codes.Distinct(StringComparer.Ordinal).Count() != codes.Count)
            errors.Add(Error.Validation("Interface_DuplicateMappingCode"));

        // One parameter, one analyser code. Two codes pointing at the same parameter means the
        // second transmission overwrites the first with no record of which the analyser sent.
        var parameterIds = mappings.Where(m => m.IsEnabled).Select(m => m.TestParameterId).ToList();

        if (parameterIds.Distinct().Count() != parameterIds.Count)
            errors.Add(Error.Validation("Interface_DuplicateMappingParameter"));

        if (mappings.Any(m => m.ConversionFactor is 0m))
            errors.Add(Error.Validation("Interface_ConversionFactorZero"));

        if (errors.Count > 0) return Result.Failure(errors);

        foreach (var existing in link.Mappings)
        {
            var wanted = mappings.FirstOrDefault(
                m => string.Equals(m.InstrumentTestCode.Trim(), existing.InstrumentTestCode,
                                    StringComparison.Ordinal));

            if (wanted is null)
            {
                // Dropped from the list: disabled, not deleted. The message log holds frames that
                // arrived under this code, and a replay has to be able to read them.
                existing.IsEnabled = false;
                continue;
            }

            existing.InstrumentUnit = Trimmed(wanted.InstrumentUnit);
            existing.ConversionFactor = wanted.ConversionFactor;
            existing.ConversionOffset = wanted.ConversionOffset;
            existing.CodedValueMap = Trimmed(wanted.CodedValueMap);
            existing.IsEnabled = wanted.IsEnabled;
        }

        foreach (var wanted in mappings)
        {
            var code = wanted.InstrumentTestCode.Trim();

            if (link.Mappings.Any(m => string.Equals(m.InstrumentTestCode, code,
                                                      StringComparison.Ordinal)))
                continue;

            link.AddMapping(new InstrumentTestMapping(code, wanted.TestParameterId)
            {
                InstrumentUnit = Trimmed(wanted.InstrumentUnit),
                ConversionFactor = wanted.ConversionFactor,
                ConversionOffset = wanted.ConversionOffset,
                CodedValueMap = Trimmed(wanted.CodedValueMap),
                IsEnabled = wanted.IsEnabled
            });
        }

        unitOfWork.AuditReason = $"Table de correspondance de l'interface mise à jour "
                                 + $"({mappings.Count} code(s))";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Enables or disables a link without opening the whole configuration.
    /// <para>
    /// Separate because it is the action an operator takes under pressure — an analyser is
    /// misbehaving and the interface has to come off the air now — and making them walk a
    /// twenty-field form to do it is how it gets done by pulling the cable instead.
    /// </para>
    /// </summary>
    public async Task<Result> SetEnabledAsync(Guid interfaceId, bool enabled, string reason,
                                               CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Instruments.StartStop);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Interface_StateChangeReasonRequired"));

        var link = await interfaces.GetByIdWithDetailsAsync(interfaceId, ct);
        if (link is null) return Result.Failure(Error.NotFound("Interface_NotFound"));

        if (enabled)
        {
            var valid = link.Validate();
            if (valid.IsFailure) return Result.Failure(valid.Errors);
        }

        link.IsEnabled = enabled;

        unitOfWork.AuditReason =
            $"Interface automate {(enabled ? "activée" : "désactivée")} : {reason.Trim()}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public interface IInstrumentInterfaceQueries
{
    Task<IReadOnlyList<InterfaceRow>> GetInterfacesAsync(Guid siteId, DateOnly today,
                                                          CancellationToken ct = default);

    Task<IReadOnlyList<MappingRow>> GetMappingsAsync(Guid interfaceId,
                                                      CancellationToken ct = default);

    Task<IReadOnlyList<MessageLogRow>> GetMessagesAsync(Guid? interfaceId, int take,
                                                         CancellationToken ct = default);

    /// <summary>
    /// Catalogue parameters an analyser code can point at, for the mapping editor. Flattened across
    /// analyses because an analyser's code list is flat — it knows "GLU", not "glycaemia, which
    /// belongs to the biochemistry profile".
    /// </summary>
    Task<IReadOnlyList<MappableParameter>> GetMappableParametersAsync(string? search,
                                                                       CancellationToken ct = default);
}

public sealed record MappableParameter(Guid TestParameterId, string AnalysisCode,
                                        string ParameterCode, MultilingualText ParameterName,
                                        ResultValueType ValueType, string? UnitSymbol);
