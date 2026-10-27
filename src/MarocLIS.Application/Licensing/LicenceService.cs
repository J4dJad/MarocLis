using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Licensing;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Application.Licensing;

/// <summary>
/// Paramètres › Licence: evaluating the licence in force, importing one, and the request file.
/// <para>
/// The evaluation runs when the workstation or the API starts and again with each heartbeat, so a
/// licence imported on one workstation reaches the others within minutes and an end date is
/// noticed on the day — the design's « once a day » at the least. It also keeps the two dates that
/// hold the clock forward: the latest day seen, and (read from the orders) the latest registration.
/// </para>
/// </summary>
public sealed class LicenceService(
    ILicenceStore store,
    LicenceStatusHolder holder,
    LicenceTrust trust,
    IClock clock,
    ICurrentUser currentUser,
    IPermissionGuard guard,
    ISecurityAuditor securityAuditor,
    ILogger<LicenceService>? logger = null)
{
    public LicenceStatus Current => holder.Current;

    public bool IsDevelopmentKey => trust.IsDevelopmentKey;
    public string KeyFingerprint => trust.Fingerprint;

    /// <summary>Evaluates the licence in force and publishes the result to the whole process.</summary>
    public async Task<LicenceStatus> RefreshAsync(CancellationToken ct = default)
    {
        var stored = await store.ReadAsync(ct);
        var status = Evaluate(stored, stored.Content);

        await KeepMarkersAsync(stored, status, ct);

        // Said once in the security log when a licence that was in force stops being valid here —
        // the server renamed, the database moved, a row edited by hand — so the administrator who
        // finds the laboratory in read-only mode can see when and why.
        if (status.State == LicenceState.Invalid && holder.Current.State is LicenceState.Valid or LicenceState.Grace)
            await securityAuditor.LogAsync(SecurityEventKind.LicenceRefused, succeeded: false,
                                           failureReasonKey: status.Problem?.ResourceKey,
                                           details: $"Licence en vigueur refusée : {stored.Number}", ct: ct);

        holder.Publish(status);
        return status;
    }

    /// <summary>
    /// Imports a licence file. Signed in, it takes <see cref="Permissions.Administration.License"/>;
    /// before anyone can sign in — the import screen of a new installation — only while no licence
    /// is in force, since nobody is there to answer for replacing one.
    /// <para>
    /// The file is evaluated exactly as it would be once in force, and refused for the reason it
    /// would fail: altered, for another server, revoked, already ended, a training licence on a
    /// working database. A version older than one already imported is refused, so a replaced
    /// licence cannot be put back. Every import and every refusal goes to the security log.
    /// </para>
    /// </summary>
    public async Task<Result<LicenceStatus>> ImportAsync(string text, string? fileName = null,
                                                         CancellationToken ct = default)
    {
        if (currentUser.IsAuthenticated)
        {
            var permitted = guard.Require(Permissions.Administration.License);
            if (permitted.IsFailure) return Result.Failure<LicenceStatus>(permitted.Errors);
        }
        else if (holder.Current.State is not (LicenceState.Missing or LicenceState.Pending))
        {
            return Result.Failure<LicenceStatus>(Error.Forbidden("Licence_ImportNeedsSignIn"));
        }

        var stored = await store.ReadAsync(ct);
        var candidate = Evaluate(stored, text);

        if (Refusal(candidate) is { } refusal) return await RefuseAsync(refusal, fileName, ct);

        var terms = candidate.Terms!;

        var highest = await store.HighestVersionAsync(terms.Number, ct);
        if (highest is { } previous)
        {
            if (terms.Version < previous.Version)
                return await RefuseAsync(Error.Conflict("Licence_OlderVersion", terms.Version, previous.Version),
                                         fileName, ct);

            if (terms.Version == previous.Version)
                return await RefuseAsync(Error.Conflict("Licence_AlreadyImported", terms.Number, terms.Version),
                                         fileName, ct);
        }

        var replacing = stored.Content is not null;

        await store.SaveAsync(new LicenceRecord(terms.Number, terms.Version, terms.Kind, text.Trim() + "\n", clock.Now)
        {
            ImportedByUserId = currentUser.IsAuthenticated ? currentUser.UserId : null,
            ImportedByUserName = currentUser.IsAuthenticated ? currentUser.UserName : null,
            Workstation = currentUser.IsAuthenticated ? currentUser.Workstation : Environment.MachineName
        }, ct);

        await securityAuditor.LogAsync(replacing ? SecurityEventKind.LicenceReplaced : SecurityEventKind.LicenceImported,
                                       succeeded: true,
                                       details: Describe(terms, fileName, replacing ? stored : null), ct: ct);

        logger?.LogInformation("Licence {Number} version {Version} imported.", terms.Number, terms.Version);

        // In force at once, on this workstation; the others see it at their next heartbeat.
        return Result.Success(await RefreshAsync(ct));
    }

    /// <summary>The request a laboratory sends for a licence bound to this server.</summary>
    public async Task<LicenceRequest> BuildRequestAsync(string? appVersion, CancellationToken ct = default)
    {
        var stored = await store.ReadAsync(ct);

        return new LicenceRequest
        {
            ServerId = ServerIdOf(stored),
            Site = stored.SiteName ?? string.Empty,
            Laboratory = stored.LegalName,
            Authorisation = stored.Authorisation,
            CurrentNumber = stored.Number,
            CurrentVersion = stored.Version,
            AppVersion = appVersion,
            CreatedAt = clock.Now
        };
    }

    public Task<IReadOnlyList<LicenceHistoryRow>> GetHistoryAsync(CancellationToken ct = default) =>
        store.HistoryAsync(ct);

    // ---- Evaluation ---------------------------------------------------------------------------

    private LicenceStatus Evaluate(StoredLicence stored, string? text) =>
        LicenceEvaluator.Evaluate(new LicenceFacts
        {
            LicenceText = text,
            ServerId = ServerIdOf(stored),
            Today = clock.Today,
            LatestDateSeen = stored.LatestDateSeen,
            LatestOrderOn = stored.LatestOrderOn,
            OrderCount = stored.OrderCount,
            HasPatients = stored.HasPatients,
            ReleaseDate = trust.ReleaseDate,
            RestrictedSince = stored.RestrictedSince,
            Revoked = trust.Revoked
        }, trust.PublicKey);

    private static string ServerIdOf(StoredLicence stored) =>
        ServerIdentity.Compute(stored.MachineName, stored.InstanceName, stored.InstallationId);

    /// <summary>
    /// The latest day seen moves forward only. The start of the read-only mode is noted the first
    /// day a missing or invalid licence is met on a database with data — it is what the days of
    /// bench clearing count from — and forgotten once a licence is valid again.
    /// </summary>
    private async Task KeepMarkersAsync(StoredLicence stored, LicenceStatus status, CancellationToken ct)
    {
        DateOnly? seen = stored.LatestDateSeen is { } last && last >= status.Today ? null : status.Today;

        DateOnly? since = null;
        var clear = false;

        if (status.Mode == LicenceMode.Full) clear = stored.RestrictedSince is not null;
        else if (status.State is LicenceState.Invalid or LicenceState.Missing && status.HasData
                 && stored.RestrictedSince is null)
            since = status.Today;

        if (seen is null && since is null && !clear) return;

        try
        {
            await store.SaveMarkersAsync(seen, since, clear, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Two workstations starting together both write the first marker; the second loses the
            // race and the value is the same. Nothing to do but carry on.
            logger?.LogWarning(ex, "The licence markers could not be saved.");
        }
    }

    private static Error? Refusal(LicenceStatus candidate) => candidate.State switch
    {
        LicenceState.Invalid => candidate.Problem ?? Error.Forbidden("Licence_SignatureInvalid"),
        LicenceState.Expired => Error.Forbidden("Licence_AlreadyExpired", candidate.Terms?.EndsOn),
        LicenceState.Missing or LicenceState.Pending => Error.Validation("Licence_FileEmpty"),
        _ => null
    };

    private async Task<Result<LicenceStatus>> RefuseAsync(Error refusal, string? fileName, CancellationToken ct)
    {
        await securityAuditor.LogAsync(SecurityEventKind.LicenceRefused, succeeded: false,
                                       failureReasonKey: refusal.ResourceKey,
                                       details: $"Import refusé{(fileName is null ? "" : $" : {Path.GetFileName(fileName)}")}",
                                       ct: ct);
        return Result.Failure<LicenceStatus>(refusal);
    }

    private static string Describe(LicenceTerms terms, string? fileName, StoredLicence? replaced)
    {
        var text = $"Licence {terms.Number} v{terms.Version} ({LicenceTerms.KindWord(terms.Kind)}, " +
                   PluralText.French("{0} {0:plural:poste|postes}", terms.Workstations) + $", modules : {(terms.Modules.Count == 0 ? "socle" : string.Join(", ", terms.Modules))})";

        if (replaced is { Number: { } number }) text += $" en remplacement de {number} v{replaced.Version}";
        if (fileName is not null) text += $" — fichier {Path.GetFileName(fileName)}";
        return text;
    }
}

/// <summary>
/// The workstation's place under the licence's count: asked for at sign-in, kept by the heartbeat,
/// given back at sign-out.
/// </summary>
public sealed class WorkstationPresenceService(IWorkstationRegistry registry, ILicenceState licence, IClock clock)
{
    /// <summary>
    /// A place for this workstation, or the refusal and the list of those signed in. Without a
    /// licence in force nothing is counted: the read-only mode is the limit then.
    /// </summary>
    public Task<WorkstationClaim> ClaimAsync(string machineName, string? userName, string? appVersion,
                                             CancellationToken ct = default) =>
        registry.ClaimAsync(Trim(machineName), userName, appVersion, licence.Current.WorkstationLimit, clock.Now, ct);

    public Task ReleaseAsync(string machineName, CancellationToken ct = default) =>
        registry.ReleaseAsync(Trim(machineName), clock.Now, ct);

    public Task<IReadOnlyList<ActiveWorkstation>> ActiveAsync(CancellationToken ct = default) =>
        registry.ActiveAsync(clock.Now, ct);

    private static string Trim(string machineName) =>
        machineName.Length > Workstation.MachineNameMaxLength ? machineName[..Workstation.MachineNameMaxLength] : machineName;
}
