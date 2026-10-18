using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;

namespace MarocLIS.Application.Billing;

/// <summary>How a patient invoice is laid out on paper. The invoice itself is the same.</summary>
public enum InvoicePrintFormat
{
    /// <summary>The invoice as it has always printed: amount, payer and patient share per line.</summary>
    Standard = 0,

    /// <summary>
    /// The coded presentation many laboratories hand out: national act code, label and the
    /// coefficient of each act (cotation B), the total in B, the collection fee and the amount in
    /// dirhams.
    /// </summary>
    CotationB = 1
}

/// <summary>
/// The invoice print settings, held as installation settings: which format the print button uses,
/// and the few display options of each format. None of them changes an amount: they choose what is
/// shown of the invoice as it was issued, on a new print as on a reprint.
/// </summary>
public sealed record InvoicePrintSettings
{
    public const string Prefix = "Billing.InvoicePrint.";
    public const string DefaultFormatKey = Prefix + "DefaultFormat";
    public const string StandardNationalCodeKey = Prefix + "Standard.ShowNationalActCode";
    public const string StandardCoefficientKey = Prefix + "Standard.ShowCoefficient";
    public const string StandardWordsKey = Prefix + "Standard.ShowAmountInWords";
    public const string CotationLinePriceKey = Prefix + "CotationB.ShowLinePrice";
    public const string CotationZeroFeeKey = Prefix + "CotationB.ShowCollectionFeeWhenZero";
    public const string CotationWordsKey = Prefix + "CotationB.ShowAmountInWords";
    public const string CotationLetterValueKey = Prefix + "CotationB.ShowLetterKeyValue";

    /// <summary>
    /// The defaults reproduce the invoice printed until now, and the coded invoice the laboratory
    /// hands out (the owner's model): no price per line, the collection fee line even at zero.
    /// </summary>
    public static InvoicePrintSettings Default { get; } = new();

    public InvoicePrintFormat DefaultFormat { get; init; } = InvoicePrintFormat.Standard;

    public bool StandardShowNationalActCode { get; init; }
    public bool StandardShowCoefficient { get; init; }
    public bool StandardShowAmountInWords { get; init; } = true;

    public bool CotationShowLinePrice { get; init; }
    public bool CotationShowCollectionFeeWhenZero { get; init; } = true;
    public bool CotationShowAmountInWords { get; init; } = true;
    public bool CotationShowLetterKeyValue { get; init; }

    /// <summary>The format the print button uses for this document: a credit note keeps the standard one.</summary>
    public static InvoicePrintFormat EffectiveFormat(InvoiceKind kind, InvoicePrintFormat requested) =>
        kind == InvoiceKind.Invoice ? requested : InvoicePrintFormat.Standard;

    public static InvoicePrintSettings FromValues(IReadOnlyDictionary<string, string?> values)
    {
        bool Flag(string key, bool fallback) =>
            values.TryGetValue(key, out var text) && bool.TryParse(text?.Trim(), out var value) ? value : fallback;

        var defaults = Default;

        return new InvoicePrintSettings
        {
            // An unknown value (typed by hand in the settings list) falls back to the standard format.
            DefaultFormat = values.TryGetValue(DefaultFormatKey, out var format)
                            && Enum.TryParse<InvoicePrintFormat>(format?.Trim(), ignoreCase: true, out var parsed)
                            && Enum.IsDefined(parsed)
                ? parsed
                : defaults.DefaultFormat,
            StandardShowNationalActCode = Flag(StandardNationalCodeKey, defaults.StandardShowNationalActCode),
            StandardShowCoefficient = Flag(StandardCoefficientKey, defaults.StandardShowCoefficient),
            StandardShowAmountInWords = Flag(StandardWordsKey, defaults.StandardShowAmountInWords),
            CotationShowLinePrice = Flag(CotationLinePriceKey, defaults.CotationShowLinePrice),
            CotationShowCollectionFeeWhenZero = Flag(CotationZeroFeeKey, defaults.CotationShowCollectionFeeWhenZero),
            CotationShowAmountInWords = Flag(CotationWordsKey, defaults.CotationShowAmountInWords),
            CotationShowLetterKeyValue = Flag(CotationLetterValueKey, defaults.CotationShowLetterKeyValue)
        };
    }

    public IReadOnlyList<(string Key, string Value)> ToValues() =>
    [
        (DefaultFormatKey, DefaultFormat.ToString()),
        (StandardNationalCodeKey, Text(StandardShowNationalActCode)),
        (StandardCoefficientKey, Text(StandardShowCoefficient)),
        (StandardWordsKey, Text(StandardShowAmountInWords)),
        (CotationLinePriceKey, Text(CotationShowLinePrice)),
        (CotationZeroFeeKey, Text(CotationShowCollectionFeeWhenZero)),
        (CotationWordsKey, Text(CotationShowAmountInWords)),
        (CotationLetterValueKey, Text(CotationShowLetterKeyValue))
    ];

    private static string Text(bool value) => value ? "true" : "false";
}

/// <summary>Reads the installation settings under a prefix.</summary>
public interface IInvoicePrintSettingsQueries
{
    Task<IReadOnlyDictionary<string, string?>> GetSettingValuesAsync(string prefix, CancellationToken ct = default);
}

/// <summary>Reads and saves the invoice print settings.</summary>
public sealed class InvoicePrintSettingsService(
    IInvoicePrintSettingsQueries queries,
    IConfigurationWriter settingsWriter,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ILicenceState? licence = null)
{
    /// <summary>Read by whoever prints an invoice, and by whoever manages the settings.</summary>
    public async Task<Result<InvoicePrintSettings>> GetAsync(CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Billing.View, Permissions.Administration.ManageSettings);
        if (permitted.IsFailure) return Result.Failure<InvoicePrintSettings>(permitted.FirstError);

        return Result.Success(InvoicePrintSettings.FromValues(
            await queries.GetSettingValuesAsync(InvoicePrintSettings.Prefix, ct)));
    }

    public async Task<Result> SaveAsync(InvoicePrintSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var permitted = guard.Require(Permissions.Administration.ManageSettings);
        if (permitted.IsFailure) return permitted;

        var licensed = licence.RequireSettingsChange();
        if (licensed.IsFailure) return licensed;

        if (!Enum.IsDefined(settings.DefaultFormat))
            return Result.Failure(Error.Validation("InvoicePrint_UnknownFormat"));

        var changed = await WriteAsync(settings.ToValues(),
                                       "Facturation : format d'impression des factures patients", ct);

        if (changed.Count == 0) return Result.Success();

        unitOfWork.AuditReason = $"Format d'impression des factures modifié : {string.Join(", ", changed)}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// The discount ceiling: read by whoever issues an invoice (the issue dialog says when a
    /// discount goes beyond it), and by whoever manages the settings.
    /// </summary>
    public async Task<Result<DiscountCapSettings>> GetDiscountCapAsync(CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Billing.View, Permissions.Administration.ManageSettings);
        if (permitted.IsFailure) return Result.Failure<DiscountCapSettings>(permitted.FirstError);

        return Result.Success(DiscountCapSettings.FromValues(
            await queries.GetSettingValuesAsync(DiscountCapSettings.Prefix, ct)));
    }

    /// <summary>Changes the discount ceiling: the right to manage settings, and traced with the values.</summary>
    public async Task<Result> SaveDiscountCapAsync(DiscountCapSettings cap, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cap);

        var permitted = guard.Require(Permissions.Administration.ManageSettings);
        if (permitted.IsFailure) return permitted;

        var licensed = licence.RequireSettingsChange();
        if (licensed.IsFailure) return licensed;

        var valid = cap.Validate();
        if (valid.IsFailure) return valid;

        var changed = await WriteAsync(cap.ToValues(), "Facturation : plafond des remises", ct);
        if (changed.Count == 0) return Result.Success();

        // Written in French figures, like the other reasons of the audit trail.
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var amount = cap.MaxAmount is { } max ? max.ToString("N2", fr) + " DH" : "aucun";
        unitOfWork.AuditReason =
            $"Plafond des remises modifié : {cap.MaxPercent.ToString("0.##", fr)} % du brut, montant maximal {amount}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    private async Task<List<string>> WriteAsync(IEnumerable<(string Key, string Value)> values, string description,
                                                CancellationToken ct)
    {
        var changed = new List<string>();
        foreach (var (key, value) in values)
        {
            var existing = await settingsWriter.GetSettingAsync(key, null, ct);
            if (existing is null)
            {
                await settingsWriter.AddSettingAsync(new AppSetting(key, value) { Description = description }, ct);
                changed.Add(key);
            }
            else if (existing.Value != value)
            {
                existing.Value = value;
                changed.Add(key);
            }
        }

        return changed;
    }
}

/// <summary>
/// What the coded invoice (« cotation B ») shows, computed from the invoice as issued.
/// <para>
/// Kept apart from the PDF so the rules can be tested: the acts with the code, letter-key and
/// coefficient frozen on each line (never the current catalogue), the total of the coefficients per
/// letter-key, the collection fee, and the invoice's own stored amounts. Nothing here recomputes an
/// amount: « Montant en DH » is the invoice total, the same figure the standard format prints.
/// </para>
/// </summary>
public sealed record CotationInvoiceLayout(
    IReadOnlyList<CotationInvoiceRow> Rows,
    bool ShowLinePrice,
    IReadOnlyList<CotationLetterTotal> LetterTotals,
    decimal? CollectionFee,
    IReadOnlyList<CotationLetterTotal> LetterValues,
    string? Words)
{
    /// <summary>The letter-key of a biology act.</summary>
    public const string BiologyLetter = "B";

    /// <summary>
    /// The letter-key a collection fee (« prise de sang », PC) is billed under. The fee is an act of
    /// its own in the catalogue when the laboratory charges one; its lines are printed on the
    /// « Prise de sang (PC) » line, in dirhams, rather than among the coded acts.
    /// </summary>
    public const string CollectionFeeLetter = "PC";

    public static CotationInvoiceLayout Build(InvoiceDocument doc, InvoicePrintSettings settings,
                                              AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(settings);

        var fees = doc.Lines.Where(IsCollectionFee).ToList();
        var acts = doc.Lines.Where(l => !IsCollectionFee(l)).ToList();

        var rows = acts.Select(l => new CotationInvoiceRow(
            l.LineNumber,
            Blank(l.NationalActCode) ?? l.Code,
            language == AppLanguage.Arabic && Blank(l.DescriptionArabic) is { } arabic ? arabic : l.Description,
            l.Quantity,
            Letter(l),
            // The coefficient of the act times the number billed: what « 90 » stands for on the line.
            // A coefficient of 0 is an act priced in dirhams only (outside the nomenclature, the
            // letter « B » being the tariff's default): shown « 0 » in B, its price hidden, it was
            // charged without a figure on its line.
            l.Coefficient is { } coefficient && coefficient > 0m && Letter(l) is not null
                ? coefficient * l.Quantity
                : null,
            l.NetAmount)).ToList();

        // A line that is not a coded biology act has no coefficient in B to show: its amount in
        // dirhams is then the only figure it has, so the price column appears.
        var showPrice = settings.CotationShowLinePrice || rows.Any(r => !r.IsBiologyAct);

        var totals = rows
            .Where(r => r.Letter is not null && r.Coefficient is not null)
            .GroupBy(r => r.Letter!)
            .Select(g => new CotationLetterTotal(g.Key, g.Sum(r => r.Coefficient!.Value)))
            .OrderBy(t => t.Letter == BiologyLetter ? 0 : 1).ThenBy(t => t.Letter, StringComparer.Ordinal)
            .ToList();

        // « Total du (B) » is the line the model always has, even on an invoice of uncoded acts.
        if (totals.Count == 0) totals.Add(new CotationLetterTotal(BiologyLetter, 0m));

        var fee = Money.Round(fees.Sum(l => l.NetAmount));

        var values = acts
            .Where(l => Letter(l) is not null && l.LetterKeyValue is > 0m)
            .GroupBy(l => (Letter(l)!, l.LetterKeyValue!.Value))
            .Select(g => new CotationLetterTotal(g.Key.Item1, g.Key.Item2))
            .OrderBy(t => t.Letter == BiologyLetter ? 0 : 1).ThenBy(t => t.Letter, StringComparer.Ordinal)
            .ThenBy(t => t.Value)
            .ToList();

        var words = settings.CotationShowAmountInWords
            ? AmountInWords.Render(doc.TotalAmount, language).ToUpper(language.ToCulture())
            : null;

        return new CotationInvoiceLayout(
            rows,
            showPrice,
            totals,
            fee > 0m || settings.CotationShowCollectionFeeWhenZero ? fee : null,
            settings.CotationShowLetterKeyValue ? values : [],
            words);
    }

    private static bool IsCollectionFee(InvoiceDocumentLine line) =>
        string.Equals(Letter(line), CollectionFeeLetter, StringComparison.Ordinal);

    private static string? Letter(InvoiceDocumentLine line) =>
        Blank(line.LetterKey)?.ToUpperInvariant();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One act on the coded invoice.</summary>
public sealed record CotationInvoiceRow(
    int LineNumber,
    string? Code,
    string Description,
    int Quantity,
    string? Letter,
    decimal? Coefficient,
    decimal Amount)
{
    /// <summary>A biology act with its coefficient: the only kind the « Cotation (B) » column states alone.</summary>
    public bool IsBiologyAct => Letter == CotationInvoiceLayout.BiologyLetter && Coefficient is not null;
}

/// <summary>A figure per letter-key: the total of the coefficients, or the value of the letter used.</summary>
public sealed record CotationLetterTotal(string Letter, decimal Value)
{
    /// <summary>A coefficient as written on the invoice: 150, 2.5 — no trailing zeros.</summary>
    public static string Coefficient(decimal value, CultureInfo culture) => value.ToString("0.##", culture);
}
