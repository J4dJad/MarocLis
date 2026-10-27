using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Treasury;

namespace MarocLIS.Application.Treasury;

/// <summary>
/// What « Rapprochement bancaire » offers, by right and by the line selected — the workstation's
/// PermissionGate on its buttons and RefreshButtons: every action needs the right to manage the
/// treasury; reading the statement and the balances only the right to see it.
/// </summary>
public sealed record BankReconciliationActions(
    bool ManageRight, bool Accounts, bool Import, bool AutoMatch, bool Match, bool Justify, bool Unmatch, bool WithdrawImport);

/// <summary>The ink of a treasury line: a statement line reconciled in full, one leaving a difference, a week below zero.</summary>
public enum TreasuryInk
{
    Plain,
    Muted,
    Normal,
    Abnormal,
    Critical
}

/// <summary>One balance under the statement: its caption's key and its amount.</summary>
public sealed record ReconciliationBalance(string Key, decimal Amount);

/// <summary>What a manual match leaves: refused (a movement going the other way), or the difference a reason must explain.</summary>
public sealed record MatchCheck(string? RefusalKey, decimal Difference)
{
    public bool NeedsReason => RefusalKey is null && Difference != 0m;
}

/// <summary>A bank account as typed in its editor, the opening balance as the operator writes it.</summary>
public sealed class BankAccountForm
{
    public Guid? Id { get; set; }
    public string? BankName { get; set; }
    public string? Label { get; set; }
    public string? Rib { get; set; }
    public string? AccountingAccount { get; set; }
    public string? Opening { get; set; }
    public DateOnly OpeningDate { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Where each field sits in the bank's file, as the import dialog's boxes hold it: the separators
/// as the index of their choice (0 « Automatique »), the columns numbered from 1, 0 for absent.
/// </summary>
public sealed class StatementMappingForm
{
    public int Delimiter { get; set; }
    public int SkipLines { get; set; }
    public bool HasHeader { get; set; } = true;
    public int DateColumn { get; set; }
    public int ValueDateColumn { get; set; }
    public int LabelColumn { get; set; }
    public int ReferenceColumn { get; set; }
    public int AmountColumn { get; set; }
    public int DebitColumn { get; set; }
    public int CreditColumn { get; set; }
    public string? DateFormat { get; set; }
    public int DecimalSeparator { get; set; }
}

/// <summary>What the import says under its preview, and whether that is a refusal.</summary>
public sealed record ImportSummaryLine(string Text, bool Critical);

/// <summary>
/// The rules of « Rapprochement bancaire » that need no window: what is offered by which right and
/// for which line, the period the screen opens on, the lines kept by « non rapprochées seulement »,
/// how a line and a movement read, the balances under them, a manual match checked before the
/// reason is asked for, the questions put before undoing; a statement file taken or refused by its
/// size, its columns picked against the preview and the import offered only for what the preview on
/// screen was read with; a bank account typed and refused box by box. The workstation's
/// BankReconciliationView and its dialogs and the web's screen apply the same ones; every decision
/// stays TreasuryService's.
/// </summary>
public static class BankReconciliationScreenRules
{
    public const string FileField = "statement-file";
    public const string BankNameField = "bank-name";
    public const string LabelField = "bank-label";
    public const string RibField = "bank-rib";
    public const string AccountingField = "bank-accounting";
    public const string OpeningField = "bank-opening";

    /// <summary>The statement files the import reads: a bank's CSV export, sometimes saved as text.</summary>
    public const string FileAccept = ".csv,.txt";

    /// <summary>A statement of more than ten megabytes is no month of one account: refused before it is read.</summary>
    public const long MaximumFileBytes = 10 * 1024 * 1024;

    /// <summary>The RIB box: twenty-four digits, with the spaces a bank prints them with.</summary>
    public const int RibMaxLength = 40;

    public const int AccountingMaxLength = 12;

    /// <summary>The date format box, kept to what the account's mapping column takes.</summary>
    public const int DateFormatMaxLength = 30;

    /// <summary>The highest column number and lines to skip the import offers.</summary>
    public const int MaximumColumn = 60;
    public const int MaximumSkipLines = 100;

    public static BankReconciliationActions Actions(Func<string, bool> has, ReconciliationData? data, StatementLineRow? line,
                                                    int movementsSelected)
    {
        ArgumentNullException.ThrowIfNull(has);
        var manage = has(Permissions.Treasury.Manage);
        return new BankReconciliationActions(
            manage, manage, manage, manage && data is not null,
            manage && line is { IsReconciled: false } && movementsSelected > 0,
            manage && line is { IsReconciled: false },
            manage && line is { IsReconciled: true },
            manage && line is not null);
    }

    /// <summary>The period the screen opens on: from the first of last month to today.</summary>
    public static (DateOnly From, DateOnly To) DefaultPeriod(DateOnly today) =>
        (new DateOnly(today.Year, today.Month, 1).AddMonths(-1), today);

    /// <summary>The account kept selected after a reload: the one asked for, else the one shown, else the first.</summary>
    public static BankAccountRow? Keep(IReadOnlyList<BankAccountRow> accounts, Guid? kept)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        return accounts.FirstOrDefault(a => a.Id == kept) ?? accounts.FirstOrDefault();
    }

    /// <summary>
    /// What the screen shows once a reload answers: the reconciliation read, or nothing when it was
    /// refused — a reversed period kept the previous period's lines and balances under the new
    /// dates, and a line could still be matched from them.
    /// </summary>
    public static ReconciliationData? AfterLoad(Result<ReconciliationData> loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        return loaded.IsSuccess ? loaded.Value : null;
    }

    /// <summary>The statement's lines shown: all of them, or only those still open.</summary>
    public static IReadOnlyList<StatementLineRow> Visible(ReconciliationData? data, bool openOnly) =>
        data is null ? [] : [.. data.Lines.Where(l => !openOnly || !l.IsReconciled)];

    public static int OpenCount(ReconciliationData? data) => data?.Lines.Count(l => !l.IsReconciled) ?? 0;

    /// <summary>« Lignes non rapprochées seulement · 3 ».</summary>
    public static string OpenOnlyChip(ReconciliationData? data, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return data is null ? text("Treasury.OpenOnly") : $"{text("Treasury.OpenOnly")} · {OpenCount(data)}";
    }

    /// <summary>
    /// The line kept selected after a reload: the same one, or — hidden now by « non rapprochées
    /// seulement » once matched — the one that took its place, so the list does not jump back to its top.
    /// </summary>
    public static StatementLineRow? KeepLine(IReadOnlyList<StatementLineRow> visible, Guid? kept, int keptIndex)
    {
        ArgumentNullException.ThrowIfNull(visible);
        if (visible.Count == 0) return null;
        if (visible.FirstOrDefault(l => l.Id == kept) is { } same) return same;
        return kept is null ? null : visible[Math.Clamp(keptIndex, 0, visible.Count - 1)];
    }

    public static string KindKey(TreasuryMovementKind kind) => $"TreasuryMovementKind.{kind}";

    public static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// What the status column says: « Non rapprochée », or what the line was matched with —
    /// « Remise carte (auto) — écart -12,50 DH ».
    /// </summary>
    public static string LineStatus(StatementLineRow line, ScreenText text, Func<decimal, string> amount)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        if (!line.IsReconciled) return text("Treasury.Unreconciled");

        return string.Join(", ", line.MatchKinds.Select(k => text(KindKey(k))))
               + (line.IsAutomatic ? $" ({text("Treasury.Automatic")})" : string.Empty)
               + (line.DifferenceMad != 0m ? $" — {text("Treasury.Difference")} {amount(line.DifferenceMad)}" : string.Empty);
    }

    /// <summary>A line still open shows as a warning pill; a reconciled one in green, or in orange when it leaves a difference.</summary>
    public static TreasuryInk LineInk(StatementLineRow line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!line.IsReconciled) return TreasuryInk.Plain;
        return line.DifferenceMad != 0m ? TreasuryInk.Abnormal : TreasuryInk.Normal;
    }

    /// <summary>A day's card batch says how many payments it holds; any other movement its own label.</summary>
    public static string MovementLabel(TreasuryMovement movement, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(movement);
        ArgumentNullException.ThrowIfNull(text);
        // A day's card batch carries its count of payments as its label: given as a number, « 1 paiement par carte ».
        if (movement.Kind != TreasuryMovementKind.CardBatch) return movement.Label;
        return int.TryParse(movement.Label, NumberStyles.None, CultureInfo.InvariantCulture, out var payments)
            ? text("Treasury.CardBatchLabel", payments)
            : text("Treasury.CardBatchLabel", movement.Label);
    }

    /// <summary>A movement as the screen ticks it: its kind and its key, unique among the movements.</summary>
    public static string MovementId(TreasuryMovement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);
        return $"{movement.Kind}:{movement.Key}";
    }

    /// <summary>The four balances under the statement, in the workstation's order.</summary>
    public static IReadOnlyList<ReconciliationBalance> Balances(ReconciliationSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return
        [
            new("Treasury.StatementBalance", summary.StatementBalanceMad),
            new("Treasury.ReconciledBalance", summary.ReconciledBalanceMad),
            new("Treasury.UnmatchedLines", summary.UnmatchedStatementMad),
            new("Treasury.UnmatchedMovementsSiteTotal", summary.UnmatchedMovementsMad)
        ];
    }

    /// <summary>« Solde d'ouverture au 30/09/2026 : 152 300,00 DH ».</summary>
    public static string OpeningLine(ReconciliationSummary summary, ScreenText text, Func<decimal, string> amount)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        return text("Treasury.OpeningAsOf", Date(summary.OpeningBalanceDate), amount(summary.OpeningBalanceMad));
    }

    /// <summary>« 3 lignes du relevé et 5 mouvements non rapprochés ».</summary>
    public static string CountsLine(ReconciliationSummary summary, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(text);
        return text("Treasury.UnmatchedCounts", summary.UnmatchedStatementCount, summary.UnmatchedMovementCount);
    }

    // ---- The actions on a line -------------------------------------------------------------------

    /// <summary>
    /// The movements ticked against an open line: refused when one goes the other way than the line
    /// (said before the difference is asked for, not after the operator has explained it), else the
    /// difference they leave, which a reason must then explain.
    /// </summary>
    public static MatchCheck CheckMatch(StatementLineRow line, IReadOnlyList<TreasuryMovement> movements)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(movements);
        if (movements.Count == 0) return new MatchCheck("BankMatch_NoMovement", 0m);
        if (movements.Any(m => Math.Sign(m.AmountMad) != Math.Sign(line.AmountMad)))
            return new MatchCheck("BankMatch_DirectionMismatch", 0m);
        return new MatchCheck(null, Money.Round(line.AmountMad - movements.Sum(m => m.AmountMad)));
    }

    public static string MatchDifferencePrompt(decimal difference, ScreenText text, Func<decimal, string> amount)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        return text("Treasury.MatchDifferencePrompt", amount(difference));
    }

    public static string JustifyPrompt(StatementLineRow line, ScreenText text, Func<decimal, string> amount)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        return text("Treasury.JustifyPrompt", line.Label, amount(line.AmountMad));
    }

    public static string UnmatchQuestion(StatementLineRow line, ScreenText text, Func<decimal, string> amount)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        return text("Treasury.UnmatchConfirm", line.Label, amount(line.AmountMad));
    }

    public static string WithdrawQuestion(StatementLineRow line, ScreenText text, Func<decimal, string> amount)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        return text("Treasury.WithdrawImportConfirm", Date(line.OperationDate), line.Label, amount(line.AmountMad));
    }

    /// <summary>The comment of a match or a justification: the column's length.</summary>
    public const int CommentMaxLength = BankMatch.CommentMaxLength;

    // ---- The statement's import ------------------------------------------------------------------

    /// <summary>A file refused before it is read: too large for a statement.</summary>
    public static FieldRefusal? CheckFile(long size) =>
        size > MaximumFileBytes ? new FieldRefusal(FileField, "BankStatement_FileTooLarge") : null;

    /// <summary>« Importer un relevé — BMCE — Compte courant — releve-octobre.csv ».</summary>
    public static string ImportTitle(BankAccountRow account, string fileName, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(text);
        return $"{text("Treasury.ImportStatement").TrimEnd('…', '.', ' ')} — {account.Display} — {Path.GetFileName(fileName)}";
    }

    /// <summary>The separators offered: « Automatique », « ; », « , », « Tabulation » (a key, or the sign itself).</summary>
    public static IReadOnlyList<(string Text, bool IsKey)> DelimiterChoices =>
        [("Treasury.DelimiterAuto", true), (";", false), (",", false), ("Treasury.DelimiterTab", true)];

    /// <summary>The decimal separators offered: « Automatique », « , », « . ».</summary>
    public static IReadOnlyList<(string Text, bool IsKey)> DecimalChoices =>
        [("Treasury.DelimiterAuto", true), (",", false), (".", false)];

    /// <summary>The boxes of the dialog, from the mapping kept with the account (or the default one).</summary>
    public static StatementMappingForm MappingForm(BankStatementCsvMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return new StatementMappingForm
        {
            Delimiter = mapping.Delimiter switch { ';' => 1, ',' => 2, '\t' => 3, _ => 0 },
            SkipLines = Math.Clamp(mapping.SkipLines, 0, MaximumSkipLines),
            HasHeader = mapping.HasHeader,
            DateColumn = Column(mapping.DateColumn),
            ValueDateColumn = Column(mapping.ValueDateColumn),
            LabelColumn = Column(mapping.LabelColumn),
            ReferenceColumn = Column(mapping.ReferenceColumn),
            AmountColumn = Column(mapping.AmountColumn),
            DebitColumn = Column(mapping.DebitColumn),
            CreditColumn = Column(mapping.CreditColumn),
            DateFormat = mapping.DateFormat,
            DecimalSeparator = mapping.DecimalSeparator switch { ',' => 1, '.' => 2, _ => 0 }
        };
    }

    /// <summary>The mapping the boxes say; an empty date format reads as « dd/MM/yyyy ».</summary>
    public static BankStatementCsvMapping Mapping(StatementMappingForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return new BankStatementCsvMapping
        {
            Delimiter = form.Delimiter switch { 1 => ';', 2 => ',', 3 => '\t', _ => '\0' },
            SkipLines = Math.Clamp(form.SkipLines, 0, MaximumSkipLines),
            HasHeader = form.HasHeader,
            DateColumn = Column(form.DateColumn),
            ValueDateColumn = Column(form.ValueDateColumn),
            LabelColumn = Column(form.LabelColumn),
            ReferenceColumn = Column(form.ReferenceColumn),
            AmountColumn = Column(form.AmountColumn),
            DebitColumn = Column(form.DebitColumn),
            CreditColumn = Column(form.CreditColumn),
            DateFormat = string.IsNullOrWhiteSpace(form.DateFormat) ? "dd/MM/yyyy" : form.DateFormat.Trim(),
            DecimalSeparator = form.DecimalSeparator switch { 1 => ',', 2 => '.', _ => '\0' }
        };
    }

    private static int Column(int value) => Math.Clamp(value, 0, MaximumColumn);

    /// <summary>How many columns the raw preview shows: its widest line.</summary>
    public static int RawWidth(ImportPreview? preview) =>
        preview is null || preview.RawRows.Count == 0 ? 0 : preview.RawRows.Max(r => r.Count);

    /// <summary>What the import will do with a line: « Nouvelle », « Déjà importée », or why it cannot be read.</summary>
    public static string LineStatusKey(ImportPreviewLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.Status switch
        {
            ImportLineStatus.New => "Treasury.LineNew",
            ImportLineStatus.Duplicate => "Treasury.LineDuplicate",
            _ => line.Line.ErrorKey ?? "BankStatement_DateUnreadable"
        };
    }

    /// <summary>An unreadable line in red, one already imported muted.</summary>
    public static TreasuryInk LineInk(ImportPreviewLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.Status switch
        {
            ImportLineStatus.Invalid => TreasuryInk.Critical,
            ImportLineStatus.Duplicate => TreasuryInk.Muted,
            _ => TreasuryInk.Plain
        };
    }

    /// <summary>
    /// What the import says under its preview: the mapping refused (a required column missing), or
    /// how many lines are new, for how much, already imported, unreadable.
    /// </summary>
    public static ImportSummaryLine ImportSummary(ImportPreview preview, BankStatementCsvMapping mapping,
                                                  Func<IReadOnlyList<Error>, string> describe, ScreenText text,
                                                  Func<decimal, string> amount)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(describe);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        var valid = mapping.Validate();
        return valid.IsFailure
            ? new ImportSummaryLine(describe(valid.Errors), true)
            : new ImportSummaryLine(text("Treasury.ImportSummary", preview.NewCount, preview.DuplicateCount, preview.InvalidCount,
                                         amount(preview.NewTotalMad)), false);
    }

    /// <summary>
    /// The import is offered only for the mapping the preview on screen was read with — a box
    /// changed since takes it away until the preview is redone — and only when it brings a line in.
    /// </summary>
    public static bool CanImport(ImportPreview? preview, BankStatementCsvMapping? previewed, BankStatementCsvMapping current) =>
        preview is not null && previewed is not null && previewed == current && previewed.Validate().IsSuccess
        && preview.NewCount > 0;

    /// <summary>« 2 lignes illisibles ne seront pas importées. Importer les autres ? », or nothing to ask.</summary>
    public static string? ImportWithInvalidQuestion(ImportPreview preview, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(text);
        return preview.InvalidCount > 0 ? text("Treasury.ImportWithInvalid", preview.InvalidCount) : null;
    }

    public static string Imported(ImportResult result, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(text);
        return text("Treasury.Imported", result.Imported, result.Duplicates, result.Invalid);
    }

    // ---- A bank account --------------------------------------------------------------------------

    /// <summary>The editor's boxes: an account's values, or a new one on 5141 opening today at zero.</summary>
    public static BankAccountForm AccountForm(BankAccountRow? row, DateOnly today, IFormatProvider? culture = null) => new()
    {
        Id = row?.Id,
        BankName = row?.BankName,
        Label = row?.Label,
        Rib = row?.Rib,
        AccountingAccount = row?.AccountingAccount ?? "5141",
        Opening = TypedNumber.ForEditing(row?.OpeningBalanceMad ?? 0m, 2, culture ?? CultureInfo.InvariantCulture),
        OpeningDate = row is { OpeningBalanceDate: var d } && d != default ? d : today,
        IsActive = row?.IsActive ?? true
    };

    /// <summary>
    /// The account as TreasuryService.SaveBankAccountAsync takes it, or the box at fault: the RIB
    /// checked as the domain checks it, ahead of the round trip, and an opening balance that is an amount.
    /// </summary>
    public static (FieldRefusal? Refusal, BankAccountInput? Input) Check(BankAccountForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!Domain.HumanResources.Employee.IsValidRib(BankAccount.NormalizeRib(form.Rib)))
            return (new FieldRefusal(RibField, "BankAccount_RibInvalid"), null);
        if (TypedNumber.IsUnreadable(form.Opening) || TypedNumber.Parse(form.Opening) is not { } opening)
            return (new FieldRefusal(OpeningField, "Result_NotANumber", form.Opening?.Trim() ?? string.Empty), null);

        return (null, new BankAccountInput(form.Id, form.BankName?.Trim() ?? string.Empty, form.Label?.Trim() ?? string.Empty,
                                           form.Rib ?? string.Empty, form.AccountingAccount?.Trim() ?? string.Empty, opening,
                                           form.OpeningDate, form.IsActive));
    }

    /// <summary>The box a refusal of TreasuryService names; any other is said in the dialog.</summary>
    public static string? FieldOf(Error? error) => error?.ResourceKey switch
    {
        "BankAccount_BankNameRequired" => BankNameField,
        "BankAccount_LabelRequired" => LabelField,
        "BankAccount_RibInvalid" or "BankAccount_RibInUse" => RibField,
        "BankAccount_AccountingAccountInvalid" => AccountingField,
        "BankStatement_FileTooLarge" => FileField,
        _ => null
    };

    /// <summary>The last statement line of an account, or a dash when none was imported.</summary>
    public static string LastLine(BankAccountRow account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.LastLineDate is { } d ? Date(d) : "—";
    }
}
