using MarocLIS.Domain.Common;
using MarocLIS.Domain.Treasury;

namespace MarocLIS.Application.Treasury;

/// <summary>
/// A movement the application recorded that should appear on the bank statement, signed as the
/// bank sees it: money in positive, money out negative.
/// </summary>
/// <param name="ClearsLate">
/// A cheque or bill the laboratory wrote: the supplier cashes it when it pleases, so the bank may
/// book it weeks after <paramref name="Date"/> (<see cref="ReconciliationOptions.ChequeDaysAfter"/>)
/// where a card batch or a transfer takes a few days.
/// </param>
public sealed record TreasuryMovement(
    TreasuryMovementKind Kind,
    string Key,
    DateOnly Date,
    decimal AmountMad,
    string Label,
    string? Reference,
    bool ClearsLate);

/// <summary>A statement line as the reconciliation reads it.</summary>
public sealed record StatementLineRow(
    Guid Id,
    DateOnly OperationDate,
    DateOnly? ValueDate,
    string Label,
    string? Reference,
    decimal AmountMad,
    decimal MatchedAmountMad,
    int MatchCount,
    bool IsAutomatic,
    IReadOnlyList<TreasuryMovementKind> MatchKinds,
    string? MatchComment)
{
    public bool IsReconciled => MatchCount > 0;

    /// <summary>What the matches leave unexplained: bank charges deducted from a card batch, typically.</summary>
    public decimal DifferenceMad => IsReconciled ? Money.Round(AmountMad - MatchedAmountMad) : 0m;
}

public sealed record AutoMatchProposal(Guid StatementLineId, TreasuryMovement Movement, bool ByReference);

public sealed record ReconciliationOptions
{
    /// <summary>Days the bank may book a movement before the date the application recorded.</summary>
    public int DaysBefore { get; init; } = 2;

    /// <summary>Days after, for every movement but the cheques the laboratory wrote.</summary>
    public int DaysAfter { get; init; } = 5;

    /// <summary>Days after, for a cheque the laboratory wrote to a supplier.</summary>
    public int ChequeDaysAfter { get; init; } = 45;

    public static ReconciliationOptions Default { get; } = new();
}

/// <summary>The balances of an account over a period, as the reconciliation screen shows them.</summary>
public sealed record ReconciliationSummary(
    decimal OpeningBalanceMad,
    DateOnly OpeningBalanceDate,
    decimal StatementBalanceMad,
    decimal ReconciledBalanceMad,
    decimal UnmatchedStatementMad,
    int UnmatchedStatementCount,
    decimal UnmatchedMovementsMad,
    int UnmatchedMovementCount)
{
    /// <summary>
    /// The statement balance less what the laboratory has not recorded, plus what it recorded and
    /// the bank has not booked yet: what the account would show were everything in step.
    /// </summary>
    public decimal AdjustedBalanceMad => Money.Round(StatementBalanceMad - UnmatchedStatementMad + UnmatchedMovementsMad);

    /// <summary>The statement balance less the reconciled balance: the lines still to explain.</summary>
    public decimal DifferenceMad => Money.Round(StatementBalanceMad - ReconciledBalanceMad);
}

/// <summary>
/// The matching rules of the bank reconciliation (pointage), kept free of the database so they can
/// be read and tested on their own.
/// </summary>
public static class BankReconciliation
{
    /// <summary>
    /// Proposes one-to-one matches between unreconciled statement lines and unreconciled movements:
    /// the same amount to the centime, the same direction, the line's date within the movement's
    /// window. Anything ambiguous is left to the operator, never guessed:
    /// <list type="number">
    /// <item>a line whose label or reference carries the movement's reference (a cheque number, a
    /// transfer or remittance reference) is matched when that movement is the only one it carries;</item>
    /// <item>then a line with exactly one candidate movement that has exactly one candidate line.</item>
    /// </list>
    /// Each round removes what it matched, so a pair that was ambiguous can become certain once its
    /// rivals have been paired by reference.
    /// </summary>
    public static IReadOnlyList<AutoMatchProposal> ProposeMatches(
        IEnumerable<StatementLineRow> lines, IEnumerable<TreasuryMovement> movements,
        ReconciliationOptions? options = null)
    {
        options ??= ReconciliationOptions.Default;

        var openLines = lines.Where(l => !l.IsReconciled).ToList();
        var openMovements = movements.ToList();
        var proposals = new List<AutoMatchProposal>();

        bool Fits(StatementLineRow line, TreasuryMovement movement)
        {
            if (Money.Round(line.AmountMad) != Money.Round(movement.AmountMad)) return false;

            var days = line.OperationDate.DayNumber - movement.Date.DayNumber;
            var after = movement.ClearsLate ? options.ChequeDaysAfter : options.DaysAfter;
            return days >= -options.DaysBefore && days <= after;
        }

        var changed = true;
        while (changed && openLines.Count > 0 && openMovements.Count > 0)
        {
            changed = false;

            // By reference first.
            foreach (var line in openLines.ToList())
            {
                var referenced = openMovements
                    .Where(m => Fits(line, m) && CarriesReference(line, m.Reference))
                    .ToList();
                if (referenced.Count != 1) continue;

                var movement = referenced[0];
                // The same reference on two lines of the same amount: leave it to the operator.
                if (openLines.Count(l => Fits(l, movement) && CarriesReference(l, movement.Reference)) != 1) continue;

                proposals.Add(new AutoMatchProposal(line.Id, movement, ByReference: true));
                openLines.Remove(line);
                openMovements.Remove(movement);
                changed = true;
            }

            // Then by amount and date, only when both sides are unique.
            foreach (var line in openLines.ToList())
            {
                var candidates = openMovements.Where(m => Fits(line, m)).ToList();
                if (candidates.Count != 1) continue;

                var movement = candidates[0];
                if (openLines.Count(l => Fits(l, movement)) != 1) continue;

                proposals.Add(new AutoMatchProposal(line.Id, movement, ByReference: false));
                openLines.Remove(line);
                openMovements.Remove(movement);
                changed = true;
            }
        }

        return proposals;
    }

    /// <summary>
    /// Whether the line's label or reference contains the movement's reference, compared on letters
    /// and digits only ("CHQ N° 1234567" carries "1234567"). A reference under four characters
    /// would match by accident and is ignored.
    /// </summary>
    public static bool CarriesReference(StatementLineRow line, string? reference)
    {
        var wanted = Compact(reference);
        if (wanted.Length < 4) return false;

        return Compact(line.Label).Contains(wanted, StringComparison.Ordinal)
               || Compact(line.Reference).Contains(wanted, StringComparison.Ordinal);
    }

    private static string Compact(string? text) =>
        new([.. (text ?? string.Empty).ToUpperInvariant().Where(char.IsLetterOrDigit)]);

    /// <summary>
    /// The balances over a period. The statement balance is the opening balance plus every line
    /// after the opening date up to <paramref name="to"/>; the reconciled balance counts only the
    /// lines matched or justified.
    /// </summary>
    public static ReconciliationSummary Summarise(decimal openingBalance, DateOnly openingDate,
                                                  IEnumerable<StatementLineRow> linesUpTo,
                                                  IEnumerable<TreasuryMovement> unmatchedMovements)
    {
        var counted = linesUpTo.Where(l => l.OperationDate > openingDate).ToList();
        var unmatchedLines = counted.Where(l => !l.IsReconciled).ToList();
        var movements = unmatchedMovements.ToList();

        var statement = Money.Round(openingBalance + counted.Sum(l => l.AmountMad));
        var reconciled = Money.Round(openingBalance + counted.Where(l => l.IsReconciled).Sum(l => l.AmountMad));

        return new ReconciliationSummary(
            Money.Round(openingBalance), openingDate, statement, reconciled,
            Money.Round(unmatchedLines.Sum(l => l.AmountMad)), unmatchedLines.Count,
            Money.Round(movements.Sum(m => m.AmountMad)), movements.Count);
    }

    /// <summary>
    /// Ranks the identical lines of an import so <see cref="BankStatementLine.ComputeFingerprint"/>
    /// tells the second of two identical card commissions from a duplicate of the first.
    /// </summary>
    public static IReadOnlyList<(ParsedStatementLine Line, string Fingerprint)> Fingerprints(
        Guid bankAccountId, IEnumerable<ParsedStatementLine> lines)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<(ParsedStatementLine, string)>();

        foreach (var line in lines.Where(l => l.IsValid))
        {
            var first = BankStatementLine.ComputeFingerprint(bankAccountId, line.OperationDate!.Value,
                                                             line.Amount!.Value, line.Label, line.Reference, 0);
            var occurrence = seen.TryGetValue(first, out var n) ? n + 1 : 0;
            seen[first] = occurrence;

            result.Add((line, occurrence == 0
                ? first
                : BankStatementLine.ComputeFingerprint(bankAccountId, line.OperationDate.Value, line.Amount.Value,
                                                       line.Label, line.Reference, occurrence)));
        }

        return result;
    }
}
