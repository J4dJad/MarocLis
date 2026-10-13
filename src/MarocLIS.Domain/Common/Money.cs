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

    public string Format(AppLanguage language) => language switch
    {
        AppLanguage.Arabic => $"{Amount:N2} {SymbolAr}",
        AppLanguage.English => $"{Amount:N2} {CurrencyCode}",
        _ => $"{Amount:N2} {SymbolFr}"
    };

    public override string ToString() => Format(AppLanguage.French);
}
