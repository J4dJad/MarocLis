using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Treasury;

namespace MarocLIS.Application.Treasury;

// ---- Read models -------------------------------------------------------------------------------

public sealed record BankAccountRow(
    Guid Id,
    string BankName,
    string Label,
    string Rib,
    string AccountingAccount,
    bool IsActive,
    decimal OpeningBalanceMad,
    DateOnly OpeningBalanceDate,
    decimal StatementBalanceMad,
    DateOnly? LastLineDate,
    int UnreconciledCount,
    string? CsvMapping)
{
    public string Display => $"{BankName} — {Label}";
}

public sealed record BankAccountInput(
    Guid? Id,
    string BankName,
    string Label,
    string Rib,
    string AccountingAccount,
    decimal OpeningBalanceMad,
    DateOnly OpeningBalanceDate,
    bool IsActive);

public enum ImportLineStatus
{
    New = 0,
    Duplicate = 1,
    Invalid = 2
}

/// <param name="Fingerprint">What a new line is imported with; null for a duplicate or an unreadable line.</param>
public sealed record ImportPreviewLine(ParsedStatementLine Line, ImportLineStatus Status, string? Fingerprint = null);

public sealed record ImportPreview(
    IReadOnlyList<IReadOnlyList<string>> RawRows,
    IReadOnlyList<ImportPreviewLine> Lines)
{
    public int NewCount => Lines.Count(l => l.Status == ImportLineStatus.New);
    public int DuplicateCount => Lines.Count(l => l.Status == ImportLineStatus.Duplicate);
    public int InvalidCount => Lines.Count(l => l.Status == ImportLineStatus.Invalid);
    public decimal NewTotalMad => Money.Round(Lines.Where(l => l.Status == ImportLineStatus.New).Sum(l => l.Line.Amount ?? 0m));
}

public sealed record ImportResult(int Imported, int Duplicates, int Invalid);

public sealed record ReconciliationData(
    BankAccountRow Account,
    IReadOnlyList<StatementLineRow> Lines,
    IReadOnlyList<TreasuryMovement> UnmatchedMovements,
    ReconciliationSummary Summary);

public sealed record ForecastItemRow(
    Guid Id,
    string Label,
    decimal AmountMad,
    ForecastRecurrence Recurrence,
    DateOnly StartDate,
    DateOnly? EndDate,
    bool IsActive);

public sealed record ForecastItemInput(
    Guid? Id,
    string Label,
    decimal AmountMad,
    ForecastRecurrence Recurrence,
    DateOnly StartDate,
    DateOnly? EndDate,
    bool IsActive);

/// <summary>A claim batch the payer has not finished settling, with what is still expected of it.</summary>
public sealed record OpenClaimBatch(
    string Reference,
    Guid PayerId,
    string PayerName,
    int PayerTermsDays,
    DateOnly SubmittedOn,
    decimal ExpectedMad);

/// <summary>How many days a payer took between a batch's submission and its first settlement.</summary>
public sealed record PayerDelaySample(Guid PayerId, int Days);

public sealed record SupplierInvoiceDue(string SupplierName, string InvoiceNumber, DateOnly DueDate,
                                        decimal OutstandingMad, bool IsApproved);

public sealed record CashForecastSource(
    IReadOnlyList<ForecastOpeningBalance> Openings,
    IReadOnlyList<OpenClaimBatch> ClaimBatches,
    IReadOnlyList<PayerDelaySample> DelaySamples,
    IReadOnlyList<SupplierInvoiceDue> SupplierInvoices,
    decimal LastPayrollNetMad,
    string? LastPayrollMonth,
    decimal PatientCollectedMad);

public sealed record CashSessionReviewRow(
    Guid Id,
    string OperatorName,
    string? Workstation,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    decimal OpeningFloat,
    decimal ExpectedClosingCash,
    decimal? CountedCash,
    decimal? Variance,
    string? VarianceExplanation,
    decimal CashWithdrawals,
    decimal NonCashReceipts,
    bool IsReconciled,
    string? ReconciledByName,
    DateTimeOffset? ReconciledAt,
    string? ReconciliationComment,
    Guid OperatorUserId)
{
    /// <summary>
    /// Whether <paramref name="userId"/> may sign this till off: still to review, and not their own
    /// (the four-eyes rule of <see cref="Domain.Billing.CashSession.Reconcile"/>). The screen used
    /// to offer the button on one's own till and refuse only after the comment had been typed.
    /// </summary>
    public bool CanBeReconciledBy(Guid userId) => !IsReconciled && userId != OperatorUserId;
}

public interface ITreasuryQueries
{
    Task<IReadOnlyList<BankAccountRow>> GetBankAccountsAsync(Guid siteId, bool includeInactive,
                                                             CancellationToken ct = default);

    Task<bool> RibInUseAsync(string rib, Guid? exceptId, CancellationToken ct = default);

    /// <summary>
    /// The account's lines dated between the two days, both included, withdrawn ones too (flagged):
    /// what the duplicate check of an import compares the file with.
    /// </summary>
    Task<IReadOnlyList<ExistingStatementLine>> GetExistingLinesAsync(Guid bankAccountId, DateOnly from, DateOnly to,
                                                                     CancellationToken ct = default);

    /// <summary>The account's lines dated between the two days, both included, with their active matches.</summary>
    Task<IReadOnlyList<StatementLineRow>> GetStatementLinesAsync(Guid bankAccountId, DateOnly from, DateOnly to,
                                                                 CancellationToken ct = default);

    /// <summary>
    /// The movements of the site dated between the two days that no active match holds: patient
    /// cheques, transfers and bills, the day's card batches, payer remittances, supplier payments,
    /// banked till withdrawals, payroll transfers and returned cheques. A supplier bill recorded
    /// without its due date is read back from its payment date, but never from before
    /// <paramref name="booksFrom"/> (the day after the accounts' opening balance, which holds it).
    /// </summary>
    Task<IReadOnlyList<TreasuryMovement>> GetUnmatchedMovementsAsync(Guid siteId, DateOnly from, DateOnly to,
                                                                     DateOnly booksFrom, CancellationToken ct = default);

    Task<bool> IsMovementMatchedAsync(TreasuryMovementKind kind, string key, CancellationToken ct = default);

    Task<IReadOnlyList<ForecastItemRow>> GetForecastItemsAsync(Guid siteId, CancellationToken ct = default);

    Task<CashForecastSource> GetForecastSourceAsync(Guid siteId, DateOnly today, DateOnly historyFrom,
                                                    DateOnly patientHistoryFrom, CancellationToken ct = default);

    Task<IReadOnlyList<CashSessionReviewRow>> GetClosedCashSessionsAsync(Guid siteId, DateOnly from, DateOnly to,
                                                                         bool pendingOnly, CancellationToken ct = default);
}

/// <summary>
/// Treasury: the laboratory's bank accounts, their statements imported from the bank's CSV and
/// reconciled with what the application recorded, and the 90-day cash forecast.
/// <para>
/// The accounting itself stays with the laboratory's accounting software or firm: nothing here
/// posts an entry. The reconciliation tells the accountant which statement lines the application
/// explains and which it does not (bank charges, a payment nobody recorded), and the forecast
/// tells the director whether the account will hold over the next three months.
/// </para>
/// </summary>
public sealed class TreasuryService(
    IRepository<BankAccount> accounts,
    IRepository<BankStatementLine> lines,
    IRepository<BankMatch> matches,
    IRepository<TreasuryForecastItem> forecastItems,
    ITreasuryQueries queries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    private static readonly string[] ReadRights = [Permissions.Treasury.View, Permissions.Treasury.Manage];

    // ---- Bank accounts ---------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<BankAccountRow>>> GetBankAccountsAsync(bool includeInactive = false,
                                                                                  CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(ReadRights);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<BankAccountRow>>(permitted.FirstError);

        return Result.Success(await queries.GetBankAccountsAsync(currentUser.SiteId, includeInactive, ct));
    }

    public async Task<Result<Guid>> SaveBankAccountAsync(BankAccountInput input, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Treasury.Manage);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        BankAccount account;
        if (input.Id is { } id)
        {
            var existing = await accounts.GetByIdAsync(id, ct);
            if (existing is null) return Result.Failure<Guid>(Error.NotFound("BankAccount_NotFound"));

            account = existing;
            account.BankName = input.BankName.Trim();
            account.Label = input.Label.Trim();
            account.ChangeRib(input.Rib);
            account.AccountingAccount = input.AccountingAccount.Trim();
            account.IsActive = input.IsActive;
        }
        else
        {
            account = new BankAccount(currentUser.SiteId, input.BankName, input.Label, input.Rib, input.AccountingAccount)
            {
                IsActive = input.IsActive
            };
        }

        account.SetOpeningBalance(input.OpeningBalanceMad, input.OpeningBalanceDate);

        var valid = account.Validate();
        if (valid.IsFailure) return Result.Failure<Guid>(valid.Errors);

        if (await queries.RibInUseAsync(account.Rib, input.Id, ct))
            return Result.Failure<Guid>(Error.Conflict("BankAccount_RibInUse"));

        if (input.Id is null) await accounts.AddAsync(account, ct);

        unitOfWork.AuditReason = $"Compte bancaire {account.BankName} — {account.Label}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(account.Id);
    }

    // ---- Statement import ------------------------------------------------------------------------

    /// <summary>
    /// Reads the file with the mapping and says, line by line, what an import would do: bring it
    /// in, skip it as already imported, or refuse it as unreadable. Nothing is written.
    /// </summary>
    public async Task<Result<ImportPreview>> PreviewImportAsync(Guid bankAccountId, byte[] content,
                                                                BankStatementCsvMapping mapping,
                                                                CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Treasury.Manage);
        if (permitted.IsFailure) return Result.Failure<ImportPreview>(permitted.FirstError);

        var account = await accounts.GetByIdAsync(bankAccountId, ct);
        if (account is null) return Result.Failure<ImportPreview>(Error.NotFound("BankAccount_NotFound"));

        var text = BankStatementCsv.Decode(content);
        var raw = BankStatementCsv.Preview(text, mapping);

        var validMapping = mapping.Validate();
        if (validMapping.IsFailure) return Result.Success(new ImportPreview(raw, []));

        return Result.Success(new ImportPreview(raw, await ClassifyAsync(account.Id, text, mapping, ct)));
    }

    /// <summary>
    /// Imports the new lines of a statement in one save, and keeps the mapping with the account for
    /// the next one. Lines already imported are skipped (see <see cref="BankReconciliation.Deduplicate"/>),
    /// so importing an overlapping statement brings nothing in twice, whatever its layout.
    /// </summary>
    public async Task<Result<ImportResult>> ImportAsync(Guid bankAccountId, byte[] content, string? fileName,
                                                        BankStatementCsvMapping mapping, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Treasury.Manage);
        if (permitted.IsFailure) return Result.Failure<ImportResult>(permitted.FirstError);

        var validMapping = mapping.Validate();
        if (validMapping.IsFailure) return Result.Failure<ImportResult>(validMapping.Errors);

        var account = await accounts.GetByIdAsync(bankAccountId, ct);
        if (account is null) return Result.Failure<ImportResult>(Error.NotFound("BankAccount_NotFound"));
        if (!account.IsActive) return Result.Failure<ImportResult>(Error.Conflict("BankAccount_Inactive"));

        var text = BankStatementCsv.Decode(content);
        var classified = await ClassifyAsync(account.Id, text, mapping, ct);
        var fresh = classified.Where(l => l.Status == ImportLineStatus.New).ToList();

        if (fresh.Count == 0)
            return Result.Failure<ImportResult>(Error.Validation("BankStatement_NothingToImport"));

        var importId = Guid.CreateVersion7();

        foreach (var item in fresh)
        {
            // Label, reference and file name longer than their columns are cut by the line itself.
            var line = item.Line;
            await lines.AddAsync(new BankStatementLine(account.Id, account.SiteId, importId, line.OperationDate!.Value,
                                                       line.ValueDate, line.Label, line.Reference, line.Amount!.Value,
                                                       item.Fingerprint!)
            {
                SourceFileName = string.IsNullOrWhiteSpace(fileName) ? null : Path.GetFileName(fileName)
            }, ct);
        }

        account.CsvMapping = mapping.ToJson();

        unitOfWork.AuditReason = $"Import du relevé {Path.GetFileName(fileName)} — {fresh.Count} ligne(s), compte {account.Label}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new ImportResult(
            fresh.Count,
            classified.Count(l => l.Status == ImportLineStatus.Duplicate),
            classified.Count(l => l.Status == ImportLineStatus.Invalid)));
    }

    private async Task<IReadOnlyList<ImportPreviewLine>> ClassifyAsync(Guid accountId, string text,
                                                                       BankStatementCsvMapping mapping,
                                                                       CancellationToken ct)
    {
        var parsed = BankStatementCsv.Parse(text, mapping);
        var dated = parsed.Where(l => l.IsValid).Select(l => l.OperationDate!.Value).ToList();
        var existing = dated.Count == 0
            ? []
            : await queries.GetExistingLinesAsync(accountId, dated.Min(), dated.Max(), ct);
        var outcome = BankReconciliation.Deduplicate(accountId, parsed, existing)
                                        .ToDictionary(d => d.Line.LineNumber);

        return
        [
            .. parsed.Select(l => !l.IsValid || !outcome.TryGetValue(l.LineNumber, out var d)
                ? new ImportPreviewLine(l, ImportLineStatus.Invalid)
                : d.IsDuplicate
                    ? new ImportPreviewLine(l, ImportLineStatus.Duplicate)
                    : new ImportPreviewLine(l, ImportLineStatus.New, d.Fingerprint))
        ];
    }

    /// <summary>
    /// Withdraws the import a line came in with: every line of that file, read with a wrong
    /// mapping (decimal separator, debit and credit swapped, wrong account) and so wrong in the
    /// balance for good otherwise. Refused while any of its lines is reconciled: the matches are
    /// undone first, on purpose. The lines are soft-deleted, so the audit trail keeps what was
    /// imported, and the same file read correctly can be imported again.
    /// </summary>
    public async Task<Result<int>> WithdrawImportAsync(Guid statementLineId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Treasury.Manage);
        if (permitted.IsFailure) return Result.Failure<int>(permitted.FirstError);

        var line = await lines.GetByIdAsync(statementLineId, ct);
        if (line is null) return Result.Failure<int>(Error.NotFound("BankStatement_LineNotFound"));

        var batch = await lines.ListAsync(l => l.BankAccountId == line.BankAccountId && l.ImportId == line.ImportId, ct);
        var ids = batch.Select(l => l.Id).ToList();

        var held = await matches.ListAsync(m => ids.Contains(m.BankStatementLineId) && m.UnmatchedAt == null, ct);
        if (held.Count > 0)
            return Result.Failure<int>(Error.Conflict("BankStatement_ImportHasMatches", held.Select(m => m.BankStatementLineId).Distinct().Count()));

        var reason = $"Import du relevé {line.SourceFileName ?? line.ImportId.ToString("N")[..8]} annulé";
        foreach (var l in batch) lines.SoftDelete(l, currentUser.UserId, reason);

        unitOfWork.AuditReason = $"{reason} — {batch.Count} ligne(s), {Money.Round(batch.Sum(l => l.AmountMad)):N2} DH";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(batch.Count);
    }

    // ---- Reconciliation --------------------------------------------------------------------------

    /// <summary>
    /// The account's lines over the period, the movements no line holds yet, and the balances. The
    /// movements are read from a little before the period, so a cheque written at the end of the
    /// previous month and cashed in this one is offered.
    /// </summary>
    public async Task<Result<ReconciliationData>> GetReconciliationAsync(Guid bankAccountId, DateOnly from, DateOnly to,
                                                                         CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(ReadRights);
        if (permitted.IsFailure) return Result.Failure<ReconciliationData>(permitted.FirstError);

        if (to < from) return Result.Failure<ReconciliationData>(Error.Validation("Treasury_PeriodInvalid"));

        var account = (await queries.GetBankAccountsAsync(currentUser.SiteId, includeInactive: true, ct))
            .FirstOrDefault(a => a.Id == bankAccountId);
        if (account is null) return Result.Failure<ReconciliationData>(Error.NotFound("BankAccount_NotFound"));

        var periodLines = await queries.GetStatementLinesAsync(bankAccountId, from, to, ct);
        var movements = await MovementsForAsync(account, from, to, ct);

        var linesUpTo = from > account.OpeningBalanceDate.AddDays(1)
            ? await queries.GetStatementLinesAsync(bankAccountId, account.OpeningBalanceDate.AddDays(1), to, ct)
            : periodLines;

        // The movements are read a few days past the period, for the matching; what the balance
        // adjusts for is only what was recorded by its last day, as the statement balance is.
        var summary = BankReconciliation.Summarise(account.OpeningBalanceMad, account.OpeningBalanceDate,
                                                   linesUpTo, movements.Where(m => m.Date <= to));

        return Result.Success(new ReconciliationData(account, periodLines, movements, summary));
    }

    private async Task<IReadOnlyList<TreasuryMovement>> MovementsForAsync(BankAccountRow account, DateOnly from,
                                                                         DateOnly to, CancellationToken ct)
    {
        var options = ReconciliationOptions.Default;
        var earliest = from.AddDays(-options.ChequeDaysAfter);
        if (earliest <= account.OpeningBalanceDate) earliest = account.OpeningBalanceDate.AddDays(1);

        return await queries.GetUnmatchedMovementsAsync(currentUser.SiteId, earliest, to.AddDays(options.DaysBefore),
                                                        account.OpeningBalanceDate.AddDays(1), ct);
    }

    /// <summary>Matches what the rules find certain (see <see cref="BankReconciliation.ProposeMatches"/>).</summary>
    public async Task<Result<int>> AutoMatchAsync(Guid bankAccountId, DateOnly from, DateOnly to,
                                                  CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Treasury.Manage);
        if (permitted.IsFailure) return Result.Failure<int>(permitted.FirstError);

        var data = await GetReconciliationAsync(bankAccountId, from, to, ct);
        if (data.IsFailure) return Result.Failure<int>(data.Errors);

        var proposals = BankReconciliation.ProposeMatches(data.Value.Lines, data.Value.UnmatchedMovements);
        if (proposals.Count == 0) return Result.Success(0);

        foreach (var proposal in proposals)
        {
            var m = proposal.Movement;
            await matches.AddAsync(new BankMatch(currentUser.SiteId, bankAccountId, proposal.StatementLineId, m.Kind,
                                                 m.Key, m.Date, m.AmountMad, isAutomatic: true, comment: null,
                                                 clock.Now, currentUser.UserId), ct);
        }

        unitOfWork.AuditReason = $"Rapprochement automatique — {proposals.Count} ligne(s), compte {data.Value.Account.Label}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(proposals.Count);
    }

    /// <summary>
    /// Matches a line with one or several movements by hand: a cheque remittance of five cheques, a
    /// card batch the acquirer paid net of its commission. When the movements do not add up to the
    /// line, the difference must be explained in the comment.
    /// </summary>
    public async Task<Result> MatchAsync(Guid statementLineId, IReadOnlyList<TreasuryMovement> movements,
                                         string? comment, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Treasury.Manage);
        if (permitted.IsFailure) return permitted;

        if (movements.Count == 0) return Result.Failure(Error.Validation("BankMatch_NoMovement"));
        if (movements.Any(m => m.Kind == TreasuryMovementKind.Justified))
            return Result.Failure(Error.Validation("BankMatch_NoMovement"));
        if (movements.Select(m => (m.Kind, m.Key)).Distinct().Count() != movements.Count)
            return Result.Failure(Error.Validation("BankMatch_MovementTwice"));

        var line = await lines.GetByIdAsync(statementLineId, ct);
        if (line is null) return Result.Failure(Error.NotFound("BankStatement_LineNotFound"));

        if ((await matches.ListAsync(m => m.BankStatementLineId == line.Id && m.UnmatchedAt == null, ct)).Count > 0)
            return Result.Failure(Error.Conflict("BankMatch_LineAlreadyMatched"));

        // Every movement the same way as the line: a supplier payment cannot explain a credit.
        if (movements.Any(m => Math.Sign(m.AmountMad) != Math.Sign(line.AmountMad)))
            return Result.Failure(Error.Validation("BankMatch_DirectionMismatch"));

        var difference = Money.Round(line.AmountMad - movements.Sum(m => m.AmountMad));
        if (difference != 0m && string.IsNullOrWhiteSpace(comment))
            return Result.Failure(Error.Validation("BankMatch_DifferenceNeedsComment", difference));
        if (comment is not null && comment.Trim().Length > BankMatch.CommentMaxLength)
            return Result.Failure(Error.Validation("Treasury_TextTooLong", BankMatch.CommentMaxLength));

        foreach (var m in movements)
        {
            if (await queries.IsMovementMatchedAsync(m.Kind, m.Key, ct))
                return Result.Failure(Error.Conflict("BankMatch_MovementAlreadyMatched", m.Label));

            await matches.AddAsync(new BankMatch(line.SiteId, line.BankAccountId, line.Id, m.Kind, m.Key, m.Date,
                                                 m.AmountMad, isAutomatic: false, comment, clock.Now,
                                                 currentUser.UserId), ct);
        }

        unitOfWork.AuditReason = $"Rapprochement manuel de la ligne du {line.OperationDate:dd/MM/yyyy} ({line.AmountMad:N2} DH)" +
                                 (difference != 0m ? $" — écart {difference:N2} DH : {comment}" : string.Empty);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// Explains a line no movement of the application accounts for: bank charges, interest, a
    /// stamp duty, an AMO payment the payer sent without a remittance. The comment is what the
    /// accountant books it from.
    /// </summary>
    public async Task<Result> JustifyAsync(Guid statementLineId, string comment, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Treasury.Manage);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(comment))
            return Result.Failure(Error.Validation("BankMatch_JustificationRequired"));
        if (comment.Trim().Length > BankMatch.CommentMaxLength)
            return Result.Failure(Error.Validation("Treasury_TextTooLong", BankMatch.CommentMaxLength));

        var line = await lines.GetByIdAsync(statementLineId, ct);
        if (line is null) return Result.Failure(Error.NotFound("BankStatement_LineNotFound"));

        if ((await matches.ListAsync(m => m.BankStatementLineId == line.Id && m.UnmatchedAt == null, ct)).Count > 0)
            return Result.Failure(Error.Conflict("BankMatch_LineAlreadyMatched"));

        await matches.AddAsync(new BankMatch(line.SiteId, line.BankAccountId, line.Id, TreasuryMovementKind.Justified,
                                             line.Id.ToString("N"), line.OperationDate, line.AmountMad,
                                             isAutomatic: false, comment, clock.Now, currentUser.UserId), ct);

        unitOfWork.AuditReason = $"Ligne de relevé justifiée : {comment}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Undoes every active match of a line; the rows stay, closed, for the audit trail.</summary>
    public async Task<Result> UnmatchAsync(Guid statementLineId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Treasury.Manage);
        if (permitted.IsFailure) return permitted;

        var active = await matches.ListAsync(m => m.BankStatementLineId == statementLineId && m.UnmatchedAt == null, ct);
        if (active.Count == 0) return Result.Failure(Error.Conflict("BankMatch_LineNotMatched"));

        foreach (var match in active)
        {
            var undone = match.Unmatch(clock.Now, currentUser.UserId);
            if (undone.IsFailure) return undone;
        }

        unitOfWork.AuditReason = $"Annulation du rapprochement — {active.Count} mouvement(s)";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ---- Cash forecast ---------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<ForecastItemRow>>> GetForecastItemsAsync(CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(ReadRights);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<ForecastItemRow>>(permitted.FirstError);

        return Result.Success(await queries.GetForecastItemsAsync(currentUser.SiteId, ct));
    }

    public async Task<Result<Guid>> SaveForecastItemAsync(ForecastItemInput input, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Treasury.Manage);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        TreasuryForecastItem item;
        if (input.Id is { } id)
        {
            var existing = await forecastItems.GetByIdAsync(id, ct);
            if (existing is null) return Result.Failure<Guid>(Error.NotFound("TreasuryItem_NotFound"));

            item = existing;
            item.Label = input.Label.Trim();
            item.ChangeAmount(input.AmountMad);
            item.Recurrence = input.Recurrence;
            item.StartDate = input.StartDate;
        }
        else
        {
            item = new TreasuryForecastItem(currentUser.SiteId, input.Label, input.AmountMad, input.Recurrence,
                                            input.StartDate);
        }

        item.EndDate = input.EndDate;
        item.IsActive = input.IsActive;

        var valid = item.Validate();
        if (valid.IsFailure) return Result.Failure<Guid>(valid.Errors);

        if (input.Id is null) await forecastItems.AddAsync(item, ct);

        unitOfWork.AuditReason = $"Prévision de trésorerie : {item.Label} ({item.AmountMad:N2} DH)";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(item.Id);
    }

    /// <summary>
    /// The cash forecast of the site, week by week from today:
    /// <list type="bullet">
    /// <item>opening: the statement balance of each active account, as of its last imported line,
    /// plus what the application recorded after that day and no statement line holds yet (a
    /// supplier transfer, a payer's remittance, a card batch); a movement dated later than today
    /// (an LCN or a post-dated cheque not due yet) is a flow on its due date instead;</item>
    /// <item>payer receipts: each unsettled claim batch, for what the payer still owes on it, on its
    /// submission date plus the delay the payer actually takes (median of the last year, else its
    /// terms). A batch already past that date is shown apart unless asked for in the first week:
    /// money that is late is the least certain of all;</item>
    /// <item>patient receipts, when asked for: the counter's average weekly takings;</item>
    /// <item>supplier payments: approved invoices for what is left to pay, on their due date, an
    /// overdue one in the first week (it is owed now);</item>
    /// <item>payroll: the last closed month's net salaries on each pay day;</item>
    /// <item>the items entered by hand on their dates.</item>
    /// </list>
    /// </summary>
    public async Task<Result<CashForecastResult>> GetForecastAsync(CashForecastOptions? options = null,
                                                                   CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(ReadRights);
        if (permitted.IsFailure) return Result.Failure<CashForecastResult>(permitted.FirstError);

        options ??= CashForecastOptions.Default;
        var weeks = Math.Clamp(options.Weeks, 1, 26);
        var today = clock.Today;
        var to = today.AddDays(7 * weeks - 1);

        var source = await queries.GetForecastSourceAsync(currentUser.SiteId, today, today.AddYears(-1),
                                                          today.AddDays(-Math.Max(1, options.PatientHistoryDays)), ct);
        var items = await queries.GetForecastItemsAsync(currentUser.SiteId, ct);

        var flows = new List<ForecastFlow>();
        decimal overdue = 0m;

        static ForecastCategory CategoryOf(TreasuryMovementKind kind) => kind switch
        {
            TreasuryMovementKind.PayerSettlement => ForecastCategory.PayerReceipts,
            TreasuryMovementKind.PatientPayment or TreasuryMovementKind.PatientDeposit or TreasuryMovementKind.CardBatch
                or TreasuryMovementKind.PatientRefund or TreasuryMovementKind.ReturnedCheque => ForecastCategory.PatientReceipts,
            TreasuryMovementKind.SupplierPayment => ForecastCategory.SupplierPayments,
            TreasuryMovementKind.Payroll => ForecastCategory.Payroll,
            _ => ForecastCategory.Other
        };

        var delays = source.DelaySamples.GroupBy(s => s.PayerId)
                           .ToDictionary(g => g.Key, g => g.Select(s => s.Days).ToList());

        foreach (var batch in source.ClaimBatches.Where(b => b.ExpectedMad > 0m))
        {
            var delay = CashForecastCalculator.ExpectedDelayDays(
                delays.GetValueOrDefault(batch.PayerId) ?? [], batch.PayerTermsDays);
            var expected = batch.SubmittedOn.AddDays(delay);

            if (expected < today && !options.IncludeOverduePayerReceipts)
            {
                overdue += batch.ExpectedMad;
                continue;
            }

            flows.Add(new ForecastFlow(expected < today ? today : expected, batch.ExpectedMad,
                                       ForecastCategory.PayerReceipts, $"{batch.PayerName} — {batch.Reference}"));
        }

        var weeklyPatients = CashForecastCalculator.WeeklyAverage(source.PatientCollectedMad,
                                                                  Math.Max(1, options.PatientHistoryDays));
        if (options.IncludePatientReceipts && weeklyPatients > 0m)
        {
            // Spread per day so a partial last week takes its share, not a whole week.
            var daily = weeklyPatients / 7m;
            for (var day = today; day <= to; day = day.AddDays(1))
                flows.Add(new ForecastFlow(day, daily, ForecastCategory.PatientReceipts, "Patients"));
        }

        foreach (var invoice in source.SupplierInvoices.Where(i => i.IsApproved && i.OutstandingMad > 0m))
        {
            flows.Add(new ForecastFlow(invoice.DueDate < today ? today : invoice.DueDate, -invoice.OutstandingMad,
                                       ForecastCategory.SupplierPayments,
                                       $"{invoice.SupplierName} — {invoice.InvoiceNumber}"));
        }

        if (source.LastPayrollNetMad > 0m)
        {
            foreach (var payDay in CashForecastCalculator.PayDays(today, to, options.PayrollDay))
                flows.Add(new ForecastFlow(payDay, -source.LastPayrollNetMad, ForecastCategory.Payroll,
                                           $"Paie ({source.LastPayrollMonth})"));
        }

        foreach (var item in items.Where(i => i.IsActive))
        {
            var entity = new TreasuryForecastItem(currentUser.SiteId, item.Label, item.AmountMad, item.Recurrence,
                                                  item.StartDate) { EndDate = item.EndDate };
            foreach (var date in entity.Occurrences(today, to))
                flows.Add(new ForecastFlow(date, item.AmountMad, ForecastCategory.Other, item.Label));
        }

        // What was recorded since the last statement and is on none yet: the movements no match
        // holds, from the day after the earliest account's last line. Movements are not tied to an
        // account, so with several accounts the earliest date is taken: a movement already on a
        // later account's statement but not reconciled would count twice, so reconcile first.
        // A cheque or bill written to a supplier before that day may not be cashed yet, so those are
        // read further back, as the reconciliation does; nothing from before the accounts' opening
        // balance, which holds it.
        var openings = source.Openings.ToList();
        if (openings.Count > 0)
        {
            var asOf = openings.Min(o => o.AsOf ?? today);
            var accounts = await queries.GetBankAccountsAsync(currentUser.SiteId, includeInactive: false, ct);
            var booksFrom = accounts.Count == 0 ? asOf.AddDays(1) : accounts.Min(a => a.OpeningBalanceDate).AddDays(1);
            var readFrom = asOf.AddDays(1 - ReconciliationOptions.Default.ChequeDaysAfter);
            if (readFrom < booksFrom) readFrom = booksFrom;

            var pending = await queries.GetUnmatchedMovementsAsync(currentUser.SiteId, readFrom, to, booksFrom, ct);
            var payDays = CashForecastCalculator.PayDays(today, to, options.PayrollDay).ToHashSet();

            // A supplier cheque already cashed but not reconciled yet is on the statement: a debit
            // of its amount on or after its date, that no match holds, is taken to be it.
            var unreconciledDebits = new List<StatementLineRow>();
            if (readFrom <= asOf)
            {
                foreach (var account in accounts)
                    unreconciledDebits.AddRange((await queries.GetStatementLinesAsync(account.Id, readFrom, asOf, ct))
                                                .Where(l => !l.IsReconciled && l.AmountMad < 0m));
            }

            bool Cashed(TreasuryMovement m)
            {
                var line = unreconciledDebits.Where(l => l.AmountMad == m.AmountMad && l.OperationDate >= m.Date)
                                             .OrderBy(l => l.OperationDate).FirstOrDefault();
                if (line is null) return false;
                unreconciledDebits.Remove(line);
                return true;
            }

            var inTransit = 0m;
            foreach (var m in pending)
            {
                var date = m.Date;
                if (m.Kind == TreasuryMovementKind.Payroll)
                {
                    // On the pay day the forecast uses, not at month end: one still to come is the
                    // payroll line's, one already paid is in transit.
                    if (m.PayrollMonth is { } month)
                        date = CashForecastCalculator.PayDayOf(month, m.PayrollClosedOn, options.PayrollDay);
                    if (payDays.Contains(date)) continue;
                }

                // Up to the last line it is on the statement, but for a supplier's cheque or bill.
                if (date <= asOf && !(m.Kind == TreasuryMovementKind.SupplierPayment && m.ClearsLate && !Cashed(m))) continue;

                if (date <= today)
                {
                    inTransit += m.AmountMad;
                }
                else if (date <= to)
                {
                    flows.Add(new ForecastFlow(date, m.AmountMad, CategoryOf(m.Kind),
                                               string.IsNullOrWhiteSpace(m.Reference) ? m.Label : $"{m.Label} ({m.Reference})"));
                }
            }

            openings.Add(new ForecastOpeningBalance(string.Empty, Money.Round(inTransit), asOf, IsRecordedNotOnStatement: true));
        }

        var opening = Money.Round(openings.Sum(o => o.BalanceMad));
        var table = CashForecastCalculator.BuildWeeks(today, weeks, opening, flows);

        return Result.Success(new CashForecastResult(
            today, to, openings, table, [.. flows.OrderBy(f => f.Date)],
            Money.Round(overdue),
            Money.Round(source.SupplierInvoices.Where(i => !i.IsApproved).Sum(i => i.OutstandingMad)),
            source.LastPayrollNetMad, weeklyPatients));
    }
}
