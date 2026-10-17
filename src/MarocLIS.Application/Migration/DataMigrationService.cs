using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Migration;

namespace MarocLIS.Application.Migration;

/// <summary>The database side of the take-over: what the planner compares with, and the writes.</summary>
public interface IDataMigrationStore
{
    Task<MigrationContext> LoadContextAsync(DataMigrationKind kind, string sourceSystem, CancellationToken ct = default);

    /// <summary>Opens the journal entry of a run.</summary>
    Task<Guid> StartBatchAsync(DataMigrationKind kind, string sourceSystem, string sourceLabel, int rowCount,
                               CancellationToken ct = default);

    /// <summary>
    /// Writes <paramref name="rows"/> (none in error) in one transaction: the records, and their
    /// source keys. All or nothing for the chunk; the counts of what was created and updated.
    /// </summary>
    Task<(int Created, int Updated)> WriteChunkAsync(Guid batchId, DataMigrationKind kind,
                                                     IReadOnlyList<PlannedRow> rows, CancellationToken ct = default);

    /// <summary>Closes the journal entry with its counts, and writes its audit line.</summary>
    Task CompleteBatchAsync(Guid batchId, int created, int updated, int skipped, int failed, bool interrupted,
                            CancellationToken ct = default);

    Task<IReadOnlyList<MigrationBatchRow>> GetBatchesAsync(CancellationToken ct = default);

    /// <summary>Removes what the last run created and nothing has used since; null when there is nothing to undo.</summary>
    Task<MigrationUndoOutcome?> UndoLastAsync(CancellationToken ct = default);

    Task<IReadOnlyList<OldNumberMatch>> FindByOldNumberAsync(string number, CancellationToken ct = default);

    Task<IReadOnlyList<OpeningBalanceRow>> GetOpeningBalancesAsync(CancellationToken ct = default);
}

/// <summary>
/// « Reprise de données »: takes the laboratory's patients, prescribers, catalogue, antériorités
/// and open balances over from its previous application, from files or from that application's
/// database. Every entry point requires <see cref="Permissions.Administration.Migration"/>.
/// <para>
/// The flow is the screen's: read, map, « Simulation » (the plan, with the anomaly report and the
/// counts), then « Importer », which plans again against the database as it is at that moment and
/// writes the rows in transactions of <see cref="MigrationRequest.BatchSize"/>. A run stopped half
/// way leaves the chunks already written, each with its source key, so running it again carries on
/// where it stopped instead of duplicating.
/// </para>
/// </summary>
public sealed class DataMigrationService(
    IDataMigrationStore store,
    IMigrationSourceReader sourceReader,
    IPermissionGuard guard,
    IClock clock)
{
    /// <summary>Rows a preview shows.</summary>
    public const int PreviewRows = 200;

    /// <summary>The most rows one import reads: past it, the source is split (by year, by table).</summary>
    public const int MaxRows = 500_000;

    private Result Allowed() => guard.Require(Permissions.Administration.Migration);

    // ---- The old application's database --------------------------------------------------------------

    public async Task<Result<string>> TestConnectionAsync(SourceConnectionSettings settings, CancellationToken ct = default)
    {
        if (Allowed() is { IsFailure: true } refused) return Result.Failure<string>(refused.Errors);
        return await Guarded(() => sourceReader.TestAsync(settings, ct));
    }

    public async Task<Result<IReadOnlyList<string>>> ListDatabasesAsync(SourceConnectionSettings settings,
                                                                         CancellationToken ct = default)
    {
        if (Allowed() is { IsFailure: true } refused) return Result.Failure<IReadOnlyList<string>>(refused.Errors);
        return await Guarded(() => sourceReader.ListDatabasesAsync(settings, ct));
    }

    public async Task<Result<IReadOnlyList<SourceTableInfo>>> ListTablesAsync(SourceConnectionSettings settings,
                                                                              CancellationToken ct = default)
    {
        if (Allowed() is { IsFailure: true } refused) return Result.Failure<IReadOnlyList<SourceTableInfo>>(refused.Errors);
        return await Guarded(() => sourceReader.ListTablesAsync(settings, ct));
    }

    /// <summary>Reads a table, a view or a SELECT; <paramref name="maxRows"/> for a preview.</summary>
    public async Task<Result<SourceTable>> ReadSourceAsync(SourceConnectionSettings settings, SourceSelection selection,
                                                           int maxRows = MaxRows, CancellationToken ct = default)
    {
        if (Allowed() is { IsFailure: true } refused) return Result.Failure<SourceTable>(refused.Errors);

        if (selection.Table is null)
        {
            var safe = SourceQueryGuard.Check(selection.Query);
            if (safe.IsFailure) return Result.Failure<SourceTable>(safe.Errors);
        }

        return await Guarded(() => sourceReader.ReadAsync(settings, selection, Math.Min(maxRows, MaxRows), ct));
    }

    private static async Task<Result<T>> Guarded<T>(Func<Task<T>> read)
    {
        try
        {
            return Result.Success(await read());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex) when (ex.ParamName is null && ex.Message.StartsWith("Migration_", StringComparison.Ordinal))
        {
            return Result.Failure<T>(Error.Validation(ex.Message));
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException
                                       or ArgumentException or TimeoutException or System.ComponentModel.Win32Exception
                                       or PlatformNotSupportedException or DllNotFoundException or TypeInitializationException)
        {
            // The driver's own message says what is wrong (unknown server, refused login, missing
            // table) and the operator needs it as is to fix the connection.
            return Result.Failure<T>(Error.Validation("Migration_SourceFailed", FirstLine(ex)));
        }
    }

    private static string FirstLine(Exception ex)
    {
        var message = (ex is TypeInitializationException && ex.InnerException is not null ? ex.InnerException : ex).Message;
        var line = message.Split('\n')[0].Trim();
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    // ---- Simulation and import ----------------------------------------------------------------------

    public async Task<Result<MigrationContext>> LoadContextAsync(DataMigrationKind kind, string sourceSystem,
                                                                 CancellationToken ct = default)
    {
        if (Allowed() is { IsFailure: true } refused) return Result.Failure<MigrationContext>(refused.Errors);
        return Result.Success(await store.LoadContextAsync(kind, Normalise(sourceSystem), ct));
    }

    /// <summary>« Simulation »: what « Importer » would do, row by row, without writing anything.</summary>
    public async Task<Result<MigrationAnalysis>> AnalyseAsync(MigrationRequest request,
                                                              IProgress<MigrationProgress>? progress = null,
                                                              CancellationToken ct = default)
    {
        if (Allowed() is { IsFailure: true } refused) return Result.Failure<MigrationAnalysis>(refused.Errors);

        var checkedRequest = Check(request);
        if (checkedRequest.IsFailure) return Result.Failure<MigrationAnalysis>(checkedRequest.Errors);

        var context = await store.LoadContextAsync(request.Kind, Normalise(request.SourceSystem), ct);
        return Result.Success(await Task.Run(() => new MigrationPlanner(Normalised(request), context).Plan(progress, ct), ct));
    }

    /// <summary>
    /// « Importer »: plans again, refuses while a source value has no correspondence, then writes
    /// the rows not in error, chunk by chunk, each in its own transaction.
    /// </summary>
    public async Task<Result<MigrationSummary>> ImportAsync(MigrationRequest request,
                                                            IProgress<MigrationProgress>? progress = null,
                                                            CancellationToken ct = default)
    {
        if (Allowed() is { IsFailure: true } refused) return Result.Failure<MigrationSummary>(refused.Errors);

        var checkedRequest = Check(request);
        if (checkedRequest.IsFailure) return Result.Failure<MigrationSummary>(checkedRequest.Errors);

        request = Normalised(request);
        var context = await store.LoadContextAsync(request.Kind, request.SourceSystem, ct);
        var analysis = await Task.Run(() => new MigrationPlanner(request, context).Plan(progress, ct), ct);

        if (analysis.IsBlocked)
            return Result.Failure<MigrationSummary>(Error.Validation("Migration_ImportBlocked",
                analysis.Anomalies.Count(a => a.Severity == MigrationSeverity.Blocking)));

        var toWrite = analysis.Rows.Where(r => r.Outcome is MigrationOutcome.Create or MigrationOutcome.Update).ToList();
        var batchId = await store.StartBatchAsync(request.Kind, request.SourceSystem, request.SourceLabel,
                                                  request.Table.Rows.Count, ct);

        int created = 0, updated = 0, done = 0;
        var interrupted = false;
        var size = Math.Clamp(request.BatchSize, 1, 2000);

        try
        {
            foreach (var chunk in toWrite.Chunk(size))
            {
                // Checked between chunks only: a chunk is one transaction, written whole or not at all.
                if (ct.IsCancellationRequested) { interrupted = true; break; }

                var (c, u) = await store.WriteChunkAsync(batchId, request.Kind, chunk, CancellationToken.None);
                created += c;
                updated += u;
                done += chunk.Length;
                progress?.Report(new MigrationProgress(done, toWrite.Count, "Migration.StepImport"));
            }
        }
        catch (DataMigrationException ex)
        {
            // A chunk the store abandoned (a closed numbering series, a parameter removed meanwhile):
            // the chunks before it stay written, the run is journalled as interrupted.
            await store.CompleteBatchAsync(batchId, created, updated, analysis.Skipped, analysis.InError,
                                           interrupted: true, CancellationToken.None);
            return Result.Failure<MigrationSummary>(Error.Validation(ex.ResourceKey, ex.Args));
        }
        catch
        {
            await store.CompleteBatchAsync(batchId, created, updated, analysis.Skipped, analysis.InError,
                                           interrupted: true, CancellationToken.None);
            throw;
        }

        await store.CompleteBatchAsync(batchId, created, updated, analysis.Skipped, analysis.InError, interrupted,
                                       CancellationToken.None);

        return Result.Success(new MigrationSummary(batchId, request.Kind, request.Table.Rows.Count, created, updated,
                                                   analysis.Skipped, analysis.InError, interrupted, analysis.Anomalies));
    }

    public async Task<Result<IReadOnlyList<MigrationBatchRow>>> GetBatchesAsync(CancellationToken ct = default)
    {
        if (Allowed() is { IsFailure: true } refused) return Result.Failure<IReadOnlyList<MigrationBatchRow>>(refused.Errors);
        return Result.Success(await store.GetBatchesAsync(ct));
    }

    /// <summary>« Annuler la dernière reprise »: only what that run created and nothing has used since.</summary>
    public async Task<Result<MigrationUndoOutcome>> UndoLastAsync(CancellationToken ct = default)
    {
        if (Allowed() is { IsFailure: true } refused) return Result.Failure<MigrationUndoOutcome>(refused.Errors);

        var outcome = await store.UndoLastAsync(ct);
        return outcome is null
            ? Result.Failure<MigrationUndoOutcome>(Error.NotFound("Migration_NothingToUndo"))
            : Result.Success(outcome);
    }

    /// <summary>« Ancien n° → patient »: an old file or accession number, and what it leads to.</summary>
    public async Task<Result<IReadOnlyList<OldNumberMatch>>> FindByOldNumberAsync(string number, CancellationToken ct = default)
    {
        if (Allowed() is { IsFailure: true } refused) return Result.Failure<IReadOnlyList<OldNumberMatch>>(refused.Errors);
        if (string.IsNullOrWhiteSpace(number))
            return Result.Failure<IReadOnlyList<OldNumberMatch>>(Error.Validation("Migration_OldNumberRequired"));
        return Result.Success(await store.FindByOldNumberAsync(number.Trim(), ct));
    }

    public async Task<Result<IReadOnlyList<OpeningBalanceRow>>> GetOpeningBalancesAsync(CancellationToken ct = default)
    {
        if (Allowed() is { IsFailure: true } refused) return Result.Failure<IReadOnlyList<OpeningBalanceRow>>(refused.Errors);
        return Result.Success(await store.GetOpeningBalancesAsync(ct));
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private static Result Check(MigrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceSystem))
            return Result.Failure(Error.Validation("Migration_SourceSystemRequired"));
        if (request.SourceSystem.Trim().Length > MigrationBatch.SourceSystemMaxLength)
            return Result.Failure(Error.Validation("Migration_SourceSystemTooLong", MigrationBatch.SourceSystemMaxLength));
        if (request.SourceSystem.Contains('|', StringComparison.Ordinal))
            return Result.Failure(Error.Validation("Migration_SourceSystemInvalid"));
        if (request.Table.Rows.Count == 0)
            return Result.Failure(Error.Validation("Migration_NoRows"));
        // A truncated read stopped at the ceiling: the source holds more rows than one run takes.
        if (request.Table.Rows.Count > MaxRows || request.Table.Truncated)
            return Result.Failure(Error.Validation("Migration_TooManyRows", MaxRows));
        return Result.Success();
    }

    private static string Normalise(string sourceSystem) => sourceSystem.Trim().ToUpperInvariant();

    private static MigrationRequest Normalised(MigrationRequest request) => new()
    {
        Kind = request.Kind,
        SourceSystem = Normalise(request.SourceSystem),
        SourceLabel = request.SourceLabel,
        Table = request.Table,
        Columns = request.Columns,
        Correspondences = request.Correspondences,
        LinkByNameAndBirthDate = request.LinkByNameAndBirthDate,
        BatchSize = request.BatchSize
    };

    /// <summary>The day the planner treats as today.</summary>
    public DateOnly Today => clock.Today;
}
