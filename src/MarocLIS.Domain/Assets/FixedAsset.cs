using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;

namespace MarocLIS.Domain.Assets;

/// <summary>
/// An item of the fixed-assets register (registre des immobilisations): what the laboratory bought
/// to keep and use over several years — an analyser, a centrifuge, a computer, a desk, a vehicle,
/// the fitting-out of its premises — with what it cost and how it is depreciated.
/// <para>
/// The register is not the general ledger: the laboratory keeps its accounts in its accounting
/// software or with its accounting firm. It gives them the gross values, the depreciation schedule
/// of each asset and the monthly depreciation entries (dotations aux amortissements), and keeps the
/// link with the equipment the quality system follows.
/// </para>
/// <para>
/// An analyser placed by a supplier (mise à disposition, comodat, lease) stays the supplier's
/// property and is never capitalised; the service refuses it, and the register lists it apart.
/// </para>
/// </summary>
public sealed class FixedAsset : AuditableAggregateRoot, ISiteScoped
{
    private FixedAsset() { }

    public FixedAsset(string code, string label, FixedAssetCategory category, Guid siteId)
    {
        Code = Normalise(code);
        Label = label.Trim();
        Category = category;
        SiteId = siteId;
    }

    /// <summary>Inventory number of the asset, printed on its label and used as the entry's piece.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
    public FixedAssetCategory Category { get; set; }
    public Guid SiteId { get; set; }

    /// <summary>Date of the purchase (invoice or delivery): the start of a declining-balance plan.</summary>
    public DateOnly AcquisitionDate { get; set; }

    /// <summary>Date the asset was put into service: the start of a straight-line plan.</summary>
    public DateOnly InServiceDate { get; set; }

    /// <summary>Purchase cost excluding VAT, with the incidental costs (transport, installation).</summary>
    public decimal CostExcludingVatMad { get; set; }

    /// <summary>VAT invoiced on the purchase.</summary>
    public decimal VatAmountMad { get; set; }

    /// <summary>
    /// Whether that VAT is deducted. A laboratory whose analyses are exempt (article 91 of the CGI)
    /// usually cannot deduct it, and the VAT then belongs to the cost that is depreciated.
    /// </summary>
    public bool VatRecoverable { get; set; }

    public Guid? SupplierId { get; set; }

    /// <summary>The supplier's invoice number, also when the invoice is not in the purchasing module.</summary>
    public string? InvoiceReference { get; set; }

    public Guid? SupplierInvoiceId { get; set; }

    /// <summary>The equipment record of the asset, when it is laboratory equipment.</summary>
    public Guid? InstrumentId { get; set; }

    public DepreciationMethod Method { get; set; }
    public int UsefulLifeYears { get; set; }

    /// <summary>Value expected at the end of use, not depreciated. Usually zero.</summary>
    public decimal ResidualValueMad { get; set; }

    public DateOnly? DisposalDate { get; private set; }
    public decimal? DisposalPriceMad { get; private set; }
    public string? DisposalReason { get; private set; }

    public string? Notes { get; set; }

    public bool IsDisposed => DisposalDate is not null;

    /// <summary>The value depreciated: the cost, plus the VAT when it is not recovered.</summary>
    public decimal GrossValueMad => GrossValue(CostExcludingVatMad, VatAmountMad, VatRecoverable);

    public static decimal GrossValue(decimal costExcludingVat, decimal vat, bool vatRecoverable) =>
        Money.Round(costExcludingVat + (vatRecoverable ? 0m : vat));

    public DepreciationTerms Terms =>
        new(GrossValueMad, ResidualValueMad, Method, UsefulLifeYears, AcquisitionDate, InServiceDate, DisposalDate);

    public void ChangeCode(string code) => Code = Normalise(code);

    public static string Normalise(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    public const int MaximumUsefulLifeYears = 50;

    /// <summary>The lengths of the database columns, which the dialogs and the checks share.</summary>
    public const int CodeMaxLength = 30, LabelMaxLength = 200, InvoiceReferenceMaxLength = 60, NotesMaxLength = 1000,
                     DisposalReasonMaxLength = 500;

    public Result Validate()
    {
        var errors = new List<Error>();
        if (string.IsNullOrWhiteSpace(Code)) errors.Add(Error.Validation("Asset_CodeRequired"));
        if (string.IsNullOrWhiteSpace(Label)) errors.Add(Error.Validation("Asset_LabelRequired"));
        if (Label.Length > LabelMaxLength) errors.Add(Error.Validation("Asset_LabelTooLong", LabelMaxLength));
        if (InvoiceReference?.Length > InvoiceReferenceMaxLength)
            errors.Add(Error.Validation("Asset_InvoiceReferenceTooLong", InvoiceReferenceMaxLength));
        if (Notes?.Length > NotesMaxLength) errors.Add(Error.Validation("Asset_NotesTooLong", NotesMaxLength));
        if (AcquisitionDate == default) errors.Add(Error.Validation("Asset_AcquisitionDateRequired"));
        if (InServiceDate != default && InServiceDate < AcquisitionDate)
            errors.Add(Error.Validation("Asset_InServiceBeforeAcquisition"));
        if (CostExcludingVatMad <= 0m) errors.Add(Error.Validation("Asset_CostRequired"));
        if (VatAmountMad < 0m) errors.Add(Error.Validation("Asset_AmountNegative"));
        if (UsefulLifeYears is < 1 or > MaximumUsefulLifeYears)
            errors.Add(Error.Validation("Asset_UsefulLifeOutOfRange", MaximumUsefulLifeYears));
        if (Method == DepreciationMethod.Declining && Depreciation.DecliningCoefficient(UsefulLifeYears) is null)
            errors.Add(Error.Validation("Asset_DecliningNeedsThreeYears"));
        if (ResidualValueMad < 0m || ResidualValueMad >= GrossValueMad && GrossValueMad > 0m)
            errors.Add(Error.Validation("Asset_ResidualOutOfRange"));
        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }

    /// <summary>
    /// Records the asset leaving the register: sold, scrapped, stolen or destroyed. Depreciation runs
    /// up to the month of the disposal.
    /// </summary>
    public Result RecordDisposal(DateOnly on, decimal price, string? reason)
    {
        if (IsDisposed) return Result.Failure(Error.Conflict("Asset_AlreadyDisposed"));
        if (on < AcquisitionDate) return Result.Failure(Error.Validation("Asset_DisposalBeforeAcquisition"));
        if (price < 0m) return Result.Failure(Error.Validation("Asset_AmountNegative"));
        if (string.IsNullOrWhiteSpace(reason)) return Result.Failure(Error.Validation("Asset_DisposalReasonRequired"));
        if (reason.Trim().Length > DisposalReasonMaxLength)
            return Result.Failure(Error.Validation("Asset_DisposalReasonTooLong", DisposalReasonMaxLength));

        DisposalDate = on;
        DisposalPriceMad = Money.Round(price);
        DisposalReason = reason.Trim();
        return Result.Success();
    }

    /// <summary>Undoes a disposal recorded by mistake.</summary>
    public Result CancelDisposal()
    {
        if (!IsDisposed) return Result.Failure(Error.Conflict("Asset_NotDisposed"));
        DisposalDate = null;
        DisposalPriceMad = null;
        DisposalReason = null;
        return Result.Success();
    }
}

/// <summary>
/// One month of an asset's depreciation as it went out in an accounting export file: the amount
/// the books received for that month, kept as it was even when the asset is corrected later.
/// <para>
/// The register recomputes the plan from the asset as it stands; these rows are what the
/// accounting software already holds. A later export books the difference between the two (a
/// catch-up, or a reversal after a backdated disposal) in its first month not yet exported, and
/// gives an exported month again exactly as it was.
/// </para>
/// </summary>
public sealed class FixedAssetDepreciationPosting : AggregateRoot
{
    private FixedAssetDepreciationPosting() { }

    public FixedAssetDepreciationPosting(Guid fixedAssetId, int year, int month, DateOnly date, decimal amountMad,
                                         DateTimeOffset exportedAt)
    {
        FixedAssetId = fixedAssetId;
        Year = year;
        Month = month;
        Date = date;
        AmountMad = Money.Round(amountMad);
        ExportedAt = exportedAt;
    }

    public Guid FixedAssetId { get; private set; }
    public int Year { get; private set; }
    public int Month { get; private set; }

    /// <summary>The date of the entry: the last day of the month, or the disposal date.</summary>
    public DateOnly Date { get; private set; }

    /// <summary>The amount exported, negative for a reversal.</summary>
    public decimal AmountMad { get; private set; }

    public DateTimeOffset ExportedAt { get; private set; }
}

/// <summary>The categories of the register, each with its CGNC accounts (configurable).</summary>
public enum FixedAssetCategory
{
    /// <summary>Analysers, centrifuges, fridges, microscopes: 2332 Matériel et outillage.</summary>
    LaboratoryEquipment = 0,
    /// <summary>Computers, printers, servers: 2355 Matériel informatique.</summary>
    ItEquipment = 1,
    /// <summary>2351 Mobilier de bureau.</summary>
    Furniture = 2,
    /// <summary>Collection and delivery vehicles: 2340 Matériel de transport.</summary>
    Vehicles = 3,
    /// <summary>Fitting-out of the premises: 2327 Agencements et aménagements des constructions.</summary>
    Fittings = 4,
    /// <summary>2380 Autres immobilisations corporelles.</summary>
    Other = 99
}

public static class FixedAssetCategories
{
    /// <summary>
    /// The useful life a new asset of the category starts with, from the rates commonly admitted in
    /// Morocco (matériel 20 %, informatique 20 %, mobilier 10 %, transport 20 %, agencements 10 %).
    /// A starting point the accountant confirms, not a rule.
    /// </summary>
    public static int DefaultUsefulLifeYears(FixedAssetCategory category) => category switch
    {
        FixedAssetCategory.Furniture or FixedAssetCategory.Fittings => 10,
        _ => 5
    };
}

public enum DepreciationMethod
{
    /// <summary>Amortissement linéaire: the same charge every year, from the month put into service.</summary>
    StraightLine = 0,

    /// <summary>
    /// Amortissement dégressif: the straight-line rate times 1.5, 2 or 3 according to the life, on the
    /// net book value, from the month of acquisition, switching to straight-line when that gives more.
    /// </summary>
    Declining = 1
}

/// <summary>What a depreciation plan is computed from.</summary>
public sealed record DepreciationTerms(
    decimal GrossValue,
    decimal ResidualValue,
    DepreciationMethod Method,
    int UsefulLifeYears,
    DateOnly AcquisitionDate,
    DateOnly InServiceDate,
    DateOnly? DisposalDate = null);

/// <summary>One month's depreciation.</summary>
/// <param name="Date">The last day of the month, or the disposal date in the month of the disposal.</param>
public sealed record DepreciationMonth(int Year, int Month, DateOnly Date, decimal Amount);

/// <summary>One financial year (calendar year) of the plan.</summary>
/// <param name="Opening">Depreciation accumulated at the start of the year.</param>
/// <param name="Rate">The rate applied this year, as a fraction (0.20 = 20 %).</param>
/// <param name="StraightLine">True when the year's charge is straight-line (always, or after the switch).</param>
public sealed record DepreciationYear(
    int Year,
    int Months,
    decimal Opening,
    decimal Charge,
    decimal Closing,
    decimal NetBookValue,
    decimal Rate,
    bool StraightLine);

/// <summary>An asset's depreciation plan, by year and by month.</summary>
public sealed record DepreciationSchedule(
    decimal GrossValue,
    IReadOnlyList<DepreciationYear> Years,
    IReadOnlyList<DepreciationMonth> Months)
{
    public static DepreciationSchedule Empty(decimal gross) => new(gross, [], []);

    /// <summary>Depreciation accumulated up to and including <paramref name="date"/>.</summary>
    public decimal AccumulatedAt(DateOnly date) => Money.Round(Months.Where(m => m.Date <= date).Sum(m => m.Amount));

    /// <summary>Depreciation charged between two dates, both included.</summary>
    public decimal ChargeBetween(DateOnly from, DateOnly to) =>
        Money.Round(Months.Where(m => m.Date >= from && m.Date <= to).Sum(m => m.Amount));

    public decimal NetBookValueAt(DateOnly date) => Money.Round(GrossValue - AccumulatedAt(date));

    public decimal Total => Money.Round(Months.Sum(m => m.Amount));
}

/// <summary>
/// The depreciation plan of an asset, as Moroccan practice computes it.
/// <para>
/// <b>Straight-line</b>: (gross value − residual value) ÷ life, pro rata temporis by month from the
/// month the asset was put into service; the last month takes the rounding so that the plan ends
/// exactly at the depreciable base.
/// </para>
/// <para>
/// <b>Declining balance</b> (article 10 of the CGI): the straight-line rate times a coefficient of
/// 1.5 for a life of three or four years, 2 for five or six years and 3 beyond six years, applied
/// to the net book value, pro rata by month from the month of acquisition in the first year. Each
/// year the charge is the greater of that and the straight-line charge on the remaining value over
/// the remaining years; once straight-line is the greater it stays so, and the plan ends with the
/// last of the <c>life</c> financial years. A life under three years cannot be declining.
/// </para>
/// <para>
/// Financial years are calendar years. A disposal stops the plan at the end of its month.
/// </para>
/// </summary>
public static class Depreciation
{
    public static decimal? DecliningCoefficient(int usefulLifeYears) => usefulLifeYears switch
    {
        < 3 => null,
        3 or 4 => 1.5m,
        5 or 6 => 2m,
        _ => 3m
    };

    public static decimal StraightLineRate(int usefulLifeYears) =>
        usefulLifeYears <= 0 ? 0m : Math.Round(1m / usefulLifeYears, 6);

    /// <summary>The rate a declining plan starts from: straight-line rate × coefficient.</summary>
    public static decimal? DecliningRate(int usefulLifeYears) =>
        DecliningCoefficient(usefulLifeYears) is { } coefficient
            ? Math.Round(coefficient / usefulLifeYears, 6)
            : null;

    public static DepreciationSchedule Compute(DepreciationTerms terms)
    {
        var gross = Money.Round(terms.GrossValue);
        var depreciable = Money.Round(gross - terms.ResidualValue);
        if (depreciable <= 0m || terms.UsefulLifeYears <= 0) return DepreciationSchedule.Empty(gross);

        var planned = terms.Method == DepreciationMethod.Declining && DecliningRate(terms.UsefulLifeYears) is not null
            ? Declining(terms, gross, depreciable)
            : StraightLine(terms, depreciable);

        var months = Cut(planned.Months, terms.DisposalDate);
        return new DepreciationSchedule(gross, Years(months, planned.YearRates, gross), months);
    }

    private sealed record Plan(List<DepreciationMonth> Months, Dictionary<int, (decimal Rate, bool StraightLine)> YearRates);

    private static Plan StraightLine(DepreciationTerms terms, decimal depreciable)
    {
        var start = FirstOfMonth(terms.InServiceDate == default ? terms.AcquisitionDate : terms.InServiceDate);
        var total = terms.UsefulLifeYears * 12;
        var rate = StraightLineRate(terms.UsefulLifeYears);

        var months = new List<DepreciationMonth>(total);
        var rates = new Dictionary<int, (decimal, bool)>();
        for (var m = 1; m <= total; m++)
        {
            // Cumulative rounding: every month is the difference of two rounded running totals, so
            // the plan adds up to the base to the centime.
            var amount = Money.Round(depreciable * m / total) - Money.Round(depreciable * (m - 1) / total);
            var month = start.AddMonths(m - 1);
            months.Add(new DepreciationMonth(month.Year, month.Month, LastOfMonth(month), amount));
            rates[month.Year] = (rate, true);
        }

        return new Plan(months, rates);
    }

    private static Plan Declining(DepreciationTerms terms, decimal gross, decimal depreciable)
    {
        var life = terms.UsefulLifeYears;
        var rate = DecliningRate(life)!.Value;
        var start = FirstOfMonth(terms.AcquisitionDate);

        var months = new List<DepreciationMonth>();
        var rates = new Dictionary<int, (decimal, bool)>();
        var accumulated = 0m;
        var switched = false;

        for (var k = 0; k < life; k++)
        {
            var year = start.Year + k;
            var firstMonth = k == 0 ? start.Month : 1;
            var count = 12 - firstMonth + 1;
            var remaining = Money.Round(depreciable - accumulated);
            if (remaining <= 0m) break;

            // Straight-line over what is left: the remaining value over the remaining years, the
            // first year counting as one whatever its number of months. In the last year it is the
            // whole remainder.
            var straight = Money.Round(remaining / (life - k) * (k == 0 ? count / 12m : 1m));
            var declining = Money.Round((gross - accumulated) * rate * count / 12m);

            if (!switched && (straight >= declining || k == life - 1)) switched = true;
            var charge = Math.Min(switched ? straight : declining, remaining);

            rates[year] = (switched ? Math.Round(1m / (life - k), 6) : rate, switched);

            for (var i = 1; i <= count; i++)
            {
                var amount = Money.Round(charge * i / count) - Money.Round(charge * (i - 1) / count);
                var month = new DateOnly(year, firstMonth + i - 1, 1);
                months.Add(new DepreciationMonth(year, month.Month, LastOfMonth(month), amount));
            }

            accumulated = Money.Round(accumulated + charge);
        }

        return new Plan(months, rates);
    }

    private static List<DepreciationMonth> Cut(List<DepreciationMonth> months, DateOnly? disposal)
    {
        if (disposal is not { } on) return months;

        var cut = new List<DepreciationMonth>();
        foreach (var month in months)
        {
            var first = new DateOnly(month.Year, month.Month, 1);
            if (first > on) break;
            cut.Add(month.Date > on ? month with { Date = on } : month);
        }

        return cut;
    }

    private static List<DepreciationYear> Years(IReadOnlyList<DepreciationMonth> months,
                                                Dictionary<int, (decimal Rate, bool StraightLine)> rates,
                                                decimal gross)
    {
        var years = new List<DepreciationYear>();
        var accumulated = 0m;

        foreach (var group in months.GroupBy(m => m.Year).OrderBy(g => g.Key))
        {
            var charge = Money.Round(group.Sum(m => m.Amount));
            var closing = Money.Round(accumulated + charge);
            var (rate, straight) = rates.GetValueOrDefault(group.Key);
            years.Add(new DepreciationYear(group.Key, group.Count(), accumulated, charge, closing,
                                           Money.Round(gross - closing), rate, straight));
            accumulated = closing;
        }

        return years;
    }

    private static DateOnly FirstOfMonth(DateOnly date) => new(date.Year, date.Month, 1);

    private static DateOnly LastOfMonth(DateOnly date) =>
        new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));
}

/// <summary>A contract that places an instrument in the laboratory, as the capitalisation check needs it.</summary>
public sealed record ProvidingContract(string Reference, InstrumentContractKind Kind, bool IsActive,
                                       DateOnly StartDate, DateOnly EndDate);

/// <summary>
/// Equipment the supplier provides — placement (mise à disposition), comodat or lease — remains the
/// supplier's property. The laboratory does not capitalise or depreciate it; only what it bought
/// (including an analyser bought out at the end of such a contract) enters the register.
/// </summary>
public static class SupplierProvidedEquipment
{
    public static bool IsProvidedKind(InstrumentContractKind kind) =>
        kind is InstrumentContractKind.Placement or InstrumentContractKind.Comodat or InstrumentContractKind.Lease;

    /// <summary>
    /// The active placement, comodat or lease contract that still covered the instrument on
    /// <paramref name="on"/> (normally the asset's acquisition date), or null when the instrument may
    /// be capitalised.
    /// </summary>
    public static ProvidingContract? Blocking(IEnumerable<ProvidingContract> contracts, DateOnly on) =>
        contracts.Where(c => IsProvidedKind(c.Kind) && c.IsActive && c.EndDate >= on)
                 .OrderByDescending(c => c.StartDate)
                 .FirstOrDefault();
}
