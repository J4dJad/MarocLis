namespace MarocLIS.Domain.Common;

/// <summary>
/// Monetary amount in Moroccan dirham. Stored as <c>decimal(18,2)</c>; dirham accounting is to the
/// centime and rounding is half-away-from-zero, which is what the DGI expects on invoices.
/// </summary>
public readonly record struct Money(decimal Amount)
{
    public const string CurrencyCode = "MAD";
    public const string SymbolFr = "DH";
    public const string SymbolAr = "د.م.";

    public static readonly Money Zero = new(0m);

    public static Money Of(decimal amount) => new(Round(amount));

    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public Money Normalized => new(Round(Amount));

    public static Money operator +(Money a, Money b) => new(Round(a.Amount + b.Amount));
    public static Money operator -(Money a, Money b) => new(Round(a.Amount - b.Amount));
    public static Money operator *(Money a, decimal factor) => new(Round(a.Amount * factor));
    public static Money operator *(Money a, int factor) => new(Round(a.Amount * factor));

    public static bool operator >(Money a, Money b) => a.Amount > b.Amount;
    public static bool operator <(Money a, Money b) => a.Amount < b.Amount;
    public static bool operator >=(Money a, Money b) => a.Amount >= b.Amount;
    public static bool operator <=(Money a, Money b) => a.Amount <= b.Amount;

    public bool IsZero => Amount == 0m;
    public bool IsNegative => Amount < 0m;

    /// <summary>Splits an amount into <paramref name="parts"/> shares without losing a centime.</summary>
    public Money[] Allocate(int parts)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parts);

        var centimes = (long)Math.Round(Amount * 100m, MidpointRounding.AwayFromZero);
        var baseShare = centimes / parts;
        var remainder = centimes % parts;

        var result = new Money[parts];
        for (var i = 0; i < parts; i++)
        {
            var share = baseShare + (i < remainder ? 1 : 0);
            result[i] = new Money(share / 100m);
        }
        return result;
    }

    public string Format(AppLanguage language) => Format(language, System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>
    /// The amount in the language's currency notation, its digits in <paramref name="culture"/>: the
    /// workstation formats in its thread's culture, a web page in its session's, which the server's
    /// thread does not carry.
    /// </summary>
    public string Format(AppLanguage language, IFormatProvider culture) => language switch
    {
        AppLanguage.Arabic => string.Format(culture, "{0:N2} {1}", Amount, SymbolAr),
        AppLanguage.English => string.Format(culture, "{0:N2} {1}", Amount, CurrencyCode),
        _ => string.Format(culture, "{0:N2} {1}", Amount, SymbolFr)
    };

    public override string ToString() => Format(AppLanguage.French);
}
