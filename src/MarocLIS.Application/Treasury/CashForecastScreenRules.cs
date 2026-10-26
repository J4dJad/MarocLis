using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Treasury;

namespace MarocLIS.Application.Treasury;

/// <summary>
/// What « Prévision de trésorerie » offers: the forecast, its flows entered by hand and its CSV to
/// anyone who may see the treasury; adding or changing a flow only with the right to manage it.
/// </summary>
public sealed record CashForecastActions(bool ManageRight, bool Items, bool Csv, bool NewItem, bool EditItem);

/// <summary>What the forecast is asked to count beyond what it always counts, as the boxes above it say.</summary>
public sealed class CashForecastForm
{
    public bool IncludePatientReceipts { get; set; }
    public bool IncludeOverduePayerReceipts { get; set; }
    public int PayrollDay { get; set; }
}

/// <summary>A flow entered by hand as typed in its editor: the amount as written, negative for money going out.</summary>
public sealed class ForecastItemForm
{
    public Guid? Id { get; set; }
    public string? Label { get; set; }
    public string? Amount { get; set; }
    public ForecastRecurrence Recurrence { get; set; } = ForecastRecurrence.Monthly;
    public DateOnly StartDate { get; set; }
    public bool HasEndDate { get; set; }
    public DateOnly EndDate { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// The rules of « Prévision de trésorerie » that need no window: what is offered by which right,
/// the options read from their boxes, the columns of the weekly table and of its CSV, a week that
/// closes below zero, the lowest balance, the opening balances and what the forecast leaves out; a
/// flow entered by hand typed and refused box by box. The workstation's CashForecastView and its
/// dialogs and the web's screen apply the same ones; the forecast itself is TreasuryService's.
/// </summary>
public static class CashForecastScreenRules
{
    public const string LabelField = "item-label";
    public const string AmountField = "item-amount";
    public const string EndField = "item-end";

    /// <summary>The pay day: 1 to 31, or 0 for the month's last day.</summary>
    public const int PayrollDayMaximum = 31;

    public static CashForecastActions Actions(Func<string, bool> has, CashForecastResult? forecast, ForecastItemRow? item)
    {
        ArgumentNullException.ThrowIfNull(has);
        var manage = has(Permissions.Treasury.Manage);
        var view = manage || has(Permissions.Treasury.View);
        return new CashForecastActions(manage, view, view && forecast is not null, manage, manage && item is not null);
    }

    public static CashForecastOptions Options(CashForecastForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return new CashForecastOptions
        {
            IncludePatientReceipts = form.IncludePatientReceipts,
            IncludeOverduePayerReceipts = form.IncludeOverduePayerReceipts,
            PayrollDay = Math.Clamp(form.PayrollDay, 0, PayrollDayMaximum)
        };
    }

    /// <summary>The weekly table's columns, in the screen's language: the CSV's headers too.</summary>
    public static IReadOnlyList<string> Headers(ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            text("Treasury.Week"), text("Field.From"), text("Field.To"), text("Treasury.Opening"), text("Treasury.PayerReceipts"),
            text("Treasury.PatientReceipts"), text("Treasury.SupplierPayments"), text("Treasury.Payroll"), text("Treasury.OtherFlows"),
            text("Treasury.Net"), text("Treasury.Closing")
        ];
    }

    /// <summary>The weekly table's amounts, in the columns' order after the week and its dates.</summary>
    public static IReadOnlyList<decimal> Amounts(ForecastWeek week)
    {
        ArgumentNullException.ThrowIfNull(week);
        return [week.OpeningMad, week.PayerReceiptsMad, week.PatientReceiptsMad, week.SupplierPaymentsMad, week.PayrollMad,
                week.OtherMad, week.NetMad, week.ClosingMad];
    }

    /// <summary>A week closing below zero: the whole line in the critical colours.</summary>
    public static bool IsShortfall(ForecastWeek week)
    {
        ArgumentNullException.ThrowIfNull(week);
        return week.ClosingMad < 0m;
    }

    public static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>« Solde le plus bas sur la période : -12 400,00 DH », in red when it is below zero.</summary>
    public static (string Text, bool Critical) LowestClosing(CashForecastResult forecast, ScreenText text, Func<decimal, string> amount)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        return ($"{text("Treasury.LowestClosing")} : {amount(forecast.LowestClosingMad)}", forecast.LowestClosingMad < 0m);
    }

    /// <summary>
    /// What the forecast opens on, account by account — « BMCE — Compte courant : 152 300,00 DH
    /// (30/09/2026) » — and what the application recorded and no statement holds yet.
    /// </summary>
    public static IReadOnlyList<string> Openings(CashForecastResult forecast, ScreenText text, Func<decimal, string> amount)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        if (forecast.Openings.Count == 0) return [text("Treasury.NoBankAccount")];

        return [.. forecast.Openings.Select(o => o.IsRecordedNotOnStatement
            ? text("Treasury.OpeningRecordedNotOnStatement", Date(o.AsOf ?? forecast.From), amount(o.BalanceMad))
            : $"{o.Account} : {amount(o.BalanceMad)}" + (o.AsOf is { } asOf ? $" ({Date(asOf)})" : string.Empty))];
    }

    /// <summary>What the forecast leaves out on purpose, and the figures it took: said rather than hidden.</summary>
    public static string Notes(CashForecastResult forecast, ScreenText text, Func<decimal, string> amount)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        return text("Treasury.ForecastNotes", amount(forecast.OverduePayerReceiptsMad), amount(forecast.UnapprovedSupplierInvoicesMad),
                    amount(forecast.LastPayrollNetMad), amount(forecast.WeeklyPatientAverageMad));
    }

    /// <summary>« prevision-tresorerie-20261026.csv ».</summary>
    public static string FileName(CashForecastResult forecast)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        return $"prevision-tresorerie-{forecast.From.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.csv";
    }

    public static byte[] Csv(CashForecastResult forecast, ScreenText text) => CashForecastCalculator.ToCsv(forecast, Headers(text));

    // ---- A flow entered by hand ------------------------------------------------------------------

    public static string RecurrenceKey(ForecastRecurrence recurrence) => $"ForecastRecurrence.{recurrence}";

    public static IReadOnlyList<ForecastRecurrence> RecurrenceChoices => Enum.GetValues<ForecastRecurrence>();

    /// <summary>The editor's boxes: a flow's values, or a new monthly one starting today.</summary>
    public static ForecastItemForm ItemForm(ForecastItemRow? row, DateOnly today, IFormatProvider? culture = null) => new()
    {
        Id = row?.Id,
        Label = row?.Label,
        Amount = TypedNumber.ForEditing(row?.AmountMad ?? 0m, 2, culture ?? CultureInfo.InvariantCulture),
        Recurrence = row?.Recurrence ?? ForecastRecurrence.Monthly,
        StartDate = row?.StartDate ?? today,
        HasEndDate = row?.EndDate is not null,
        EndDate = row?.EndDate ?? row?.StartDate ?? today,
        IsActive = row?.IsActive ?? true
    };

    /// <summary>
    /// The flow as TreasuryService.SaveForecastItemAsync takes it, or the box at fault: an amount
    /// that is a number other than zero (negative for money going out).
    /// </summary>
    public static (FieldRefusal? Refusal, ForecastItemInput? Input) Check(ForecastItemForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (TypedNumber.Parse(form.Amount) is not { } amount || amount == 0m)
            return (new FieldRefusal(AmountField, "TreasuryItem_AmountRequired"), null);

        return (null, new ForecastItemInput(form.Id, form.Label?.Trim() ?? string.Empty, amount, form.Recurrence, form.StartDate,
                                            form.HasEndDate ? form.EndDate : null, form.IsActive));
    }

    /// <summary>The box a refusal of TreasuryService names; any other is said in the dialog.</summary>
    public static string? FieldOf(Error? error) => error?.ResourceKey switch
    {
        "TreasuryItem_LabelRequired" or "Treasury_TextTooLong" => LabelField,
        "TreasuryItem_AmountRequired" => AmountField,
        "TreasuryItem_EndBeforeStart" => EndField,
        _ => null
    };
}
