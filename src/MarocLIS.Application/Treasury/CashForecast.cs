using System.Globalization;
using System.Text;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Treasury;

public enum ForecastCategory
{
    /// <summary>Claim batches the payers have not settled yet (bordereaux non réglés).</summary>
    PayerReceipts = 0,

    /// <summary>The counter's takings, at the average of the recent weeks, when asked for.</summary>
    PatientReceipts = 1,

    /// <summary>Approved supplier invoices, on their due date.</summary>
    SupplierPayments = 2,

    /// <summary>The net salaries, at the last closed month's figure, on pay day.</summary>
    Payroll = 3,

    /// <summary>The flows entered by hand: rent, leasing, CNSS and tax paid over, loans.</summary>
    Other = 4
}

/// <summary>One expected flow, signed: positive comes in, negative goes out.</summary>
public sealed record ForecastFlow(DateOnly Date, decimal AmountMad, ForecastCategory Category, string Label);

/// <param name="IsRecordedNotOnStatement">
/// Not an account: what the application recorded after <paramref name="AsOf"/> that no statement
/// line holds yet, added to the statements' balances so the forecast opens on the book balance.
/// </param>
public sealed record ForecastOpeningBalance(string Account, decimal BalanceMad, DateOnly? AsOf,
                                            bool IsRecordedNotOnStatement = false);

public sealed record ForecastWeek(
    int Number,
    DateOnly Start,
    DateOnly End,
    decimal OpeningMad,
    decimal PayerReceiptsMad,
    decimal PatientReceiptsMad,
    decimal SupplierPaymentsMad,
    decimal PayrollMad,
    decimal OtherMad)
{
    public decimal NetMad => Money.Round(PayerReceiptsMad + PatientReceiptsMad + SupplierPaymentsMad + PayrollMad + OtherMad);
    public decimal ClosingMad => Money.Round(OpeningMad + NetMad);
}

/// <summary>
/// The forecast and what it leaves out on purpose, so the screen can say so rather than hide it.
/// </summary>
public sealed record CashForecastResult(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<ForecastOpeningBalance> Openings,
    IReadOnlyList<ForecastWeek> Weeks,
    IReadOnlyList<ForecastFlow> Flows,
    decimal OverduePayerReceiptsMad,
    decimal UnapprovedSupplierInvoicesMad,
    decimal LastPayrollNetMad,
    decimal WeeklyPatientAverageMad)
{
    public decimal OpeningMad => Money.Round(Openings.Sum(o => o.BalanceMad));

    /// <summary>The lowest balance a week closes on: the figure a director looks at first.</summary>
    public decimal LowestClosingMad => Weeks.Count == 0 ? OpeningMad : Weeks.Min(w => w.ClosingMad);
}

/// <summary>What the forecast is asked to include beyond what it always counts.</summary>
public sealed record CashForecastOptions
{
    public int Weeks { get; init; } = 13;

    /// <summary>Adds the counter's average weekly takings of the last <see cref="PatientHistoryDays"/> days.</summary>
    public bool IncludePatientReceipts { get; init; }

    public int PatientHistoryDays { get; init; } = 56;

    /// <summary>Counts the batches already past their expected date in the first week, rather than apart.</summary>
    public bool IncludeOverduePayerReceipts { get; init; }

    /// <summary>Day of the month salaries are paid; 0 for the last day of the month.</summary>
    public int PayrollDay { get; init; }

    public static CashForecastOptions Default { get; } = new();
}

/// <summary>
/// The 90-day cash forecast, week by week. The rules are here, apart from the queries, so they can
/// be read and tested on their own.
/// </summary>
public static class CashForecastCalculator
{
    public static IReadOnlyList<ForecastWeek> BuildWeeks(DateOnly from, int weeks, decimal opening,
                                                         IEnumerable<ForecastFlow> flows)
    {
        var list = flows.ToList();
        var result = new List<ForecastWeek>();
        var balance = Money.Round(opening);

        for (var n = 0; n < weeks; n++)
        {
            var start = from.AddDays(7 * n);
            var end = start.AddDays(6);

            decimal Sum(ForecastCategory category) =>
                Money.Round(list.Where(f => f.Category == category && f.Date >= start && f.Date <= end)
                                .Sum(f => f.AmountMad));

            var week = new ForecastWeek(n + 1, start, end, balance,
                Sum(ForecastCategory.PayerReceipts), Sum(ForecastCategory.PatientReceipts),
                Sum(ForecastCategory.SupplierPayments), Sum(ForecastCategory.Payroll),
                Sum(ForecastCategory.Other));

            result.Add(week);
            balance = week.ClosingMad;
        }

        return result;
    }

    /// <summary>
    /// How long a payer takes to settle a batch: the median of the delays it actually took over the
    /// last year, once there are <paramref name="minimumSamples"/> of them; until then, the terms
    /// recorded on the payer. The median rather than the mean, so one batch lost for six months
    /// does not push every forecast receipt back.
    /// </summary>
    public static int ExpectedDelayDays(IEnumerable<int> observedDelays, int termsDays, int minimumSamples = 3)
    {
        var sorted = observedDelays.Where(d => d >= 0).Order().ToList();
        if (sorted.Count < minimumSamples) return Math.Max(0, termsDays);

        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (int)Math.Round((sorted[middle - 1] + sorted[middle]) / 2m, MidpointRounding.AwayFromZero);
    }

    /// <summary>The pay days of the months between the two dates, both included.</summary>
    public static IEnumerable<DateOnly> PayDays(DateOnly from, DateOnly to, int payrollDay)
    {
        for (var month = new DateOnly(from.Year, from.Month, 1); month <= to; month = month.AddMonths(1))
        {
            var last = DateTime.DaysInMonth(month.Year, month.Month);
            var day = payrollDay is >= 1 and <= 31 ? Math.Min(payrollDay, last) : last;
            var date = new DateOnly(month.Year, month.Month, day);
            if (date >= from && date <= to) yield return date;
        }
    }

    /// <summary>
    /// The day the salaries of <paramref name="month"/> leave the bank, on the pay day of
    /// <see cref="PayDays"/>: the last day of the month for 0; a day from the 15th on is in the
    /// month itself (salaries paid at month end), an earlier one in the next month (paid early in
    /// the following month). Never before the month was closed: a late closing is paid that day.
    /// </summary>
    public static DateOnly PayDayOf(DateOnly month, DateOnly? closedOn, int payrollDay)
    {
        var paidIn = payrollDay is >= 1 and < 15
            ? new DateOnly(month.Year, month.Month, 1).AddMonths(1)
            : new DateOnly(month.Year, month.Month, 1);
        var last = DateTime.DaysInMonth(paidIn.Year, paidIn.Month);
        var day = payrollDay is >= 1 and <= 31 ? Math.Min(payrollDay, last) : last;
        var payDay = new DateOnly(paidIn.Year, paidIn.Month, day);
        return closedOn is { } closed && closed > payDay ? closed : payDay;
    }

    /// <summary>The counter's takings per week, from what was collected over the last days.</summary>
    public static decimal WeeklyAverage(decimal collected, int days) =>
        days <= 0 ? 0m : Money.Round(collected / days * 7m);

    /// <summary>The weekly table as CSV for a spreadsheet: semicolons, French number format, UTF-8 with BOM.</summary>
    public static byte[] ToCsv(CashForecastResult forecast, IReadOnlyList<string> headers)
    {
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        string N(decimal value) => value.ToString("0.00", fr);

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(';', headers.Select(CsvCell.Of)));

        foreach (var w in forecast.Weeks)
        {
            csv.AppendLine(string.Join(';', new[]
            {
                w.Number.ToString(CultureInfo.InvariantCulture),
                w.Start.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                w.End.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                N(w.OpeningMad), N(w.PayerReceiptsMad), N(w.PatientReceiptsMad), N(w.SupplierPaymentsMad),
                N(w.PayrollMad), N(w.OtherMad), N(w.NetMad), N(w.ClosingMad)
            }.Select(CsvCell.Of)));
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }
}
