using System.Text.RegularExpressions;
using MarocLIS.Domain.Assets;

namespace MarocLIS.Application.Assets;

/// <summary>The three CGNC accounts of a category: the asset, its depreciation, the charge.</summary>
public sealed record CategoryAccounts(string Asset, string Depreciation, string Charge);

/// <summary>
/// The accounts of the fixed-assets register, held as database settings (<c>AppSetting</c>,
/// installation-wide) like the purchasing accounts. A key that is absent or blank falls back to the
/// default here, so a fresh installation works before anyone has opened the parameters.
/// <para>
/// Defaults under the CGNC: 2332 Matériel et outillage, 2355 Matériel informatique, 2351 Mobilier
/// de bureau, 2340 Matériel de transport, 2327 Agencements et aménagements des constructions and
/// 2380 Autres immobilisations corporelles, each with its 28xxx depreciation account, and 6193
/// Dotations d'exploitation aux amortissements des immobilisations corporelles for the charge.
/// The accounting firm's sub-accounts replace them where it uses others.
/// </para>
/// </summary>
public sealed record FixedAssetSettings
{
    public const string Prefix = "Assets.";

    public static string AssetAccountKey(FixedAssetCategory category) => $"Assets.Account.{category}.Asset";
    public static string DepreciationAccountKey(FixedAssetCategory category) => $"Assets.Account.{category}.Depreciation";
    public static string ChargeAccountKey(FixedAssetCategory category) => $"Assets.Account.{category}.Charge";

    public const string DefaultChargeAccount = "6193";

    public static CategoryAccounts Defaults(FixedAssetCategory category) => category switch
    {
        FixedAssetCategory.LaboratoryEquipment => new("2332", "28332", DefaultChargeAccount),
        FixedAssetCategory.ItEquipment => new("2355", "28355", DefaultChargeAccount),
        FixedAssetCategory.Furniture => new("2351", "28351", DefaultChargeAccount),
        FixedAssetCategory.Vehicles => new("2340", "28340", DefaultChargeAccount),
        FixedAssetCategory.Fittings => new("2327", "28327", DefaultChargeAccount),
        _ => new("2380", "28380", DefaultChargeAccount)
    };

    public IReadOnlyDictionary<FixedAssetCategory, CategoryAccounts> Accounts { get; init; } =
        Enum.GetValues<FixedAssetCategory>().ToDictionary(c => c, Defaults);

    public CategoryAccounts For(FixedAssetCategory category) =>
        Accounts.TryGetValue(category, out var accounts) ? accounts : Defaults(category);

    public static FixedAssetSettings Default { get; } = new();

    public static FixedAssetSettings FromValues(IReadOnlyDictionary<string, string?> values)
    {
        string Code(string key, string fallback) =>
            values.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : fallback;

        return new FixedAssetSettings
        {
            Accounts = Enum.GetValues<FixedAssetCategory>().ToDictionary(c => c, c =>
            {
                var d = Defaults(c);
                return new CategoryAccounts(Code(AssetAccountKey(c), d.Asset),
                                            Code(DepreciationAccountKey(c), d.Depreciation),
                                            Code(ChargeAccountKey(c), d.Charge));
            })
        };
    }

    public IReadOnlyDictionary<string, string> ToValues()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var category in Enum.GetValues<FixedAssetCategory>())
        {
            var accounts = For(category);
            values[AssetAccountKey(category)] = accounts.Asset;
            values[DepreciationAccountKey(category)] = accounts.Depreciation;
            values[ChargeAccountKey(category)] = accounts.Charge;
        }
        return values;
    }

    private static readonly Regex AccountPattern = new(@"^[1-8][0-9]{3,19}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Every account a general account of the CGNC (digits, four at least) of the right class: 2 for
    /// the asset, 28 for its depreciation, 6 for the charge.
    /// </summary>
    public bool IsValid() =>
        Enum.GetValues<FixedAssetCategory>().Select(For).All(a =>
            IsAssetAccount(a.Asset) && IsDepreciationAccount(a.Depreciation) && IsChargeAccount(a.Charge));

    /// <summary>An asset account: class 2, outside the 28 depreciation accounts.</summary>
    public static bool IsAssetAccount(string? account) =>
        account is not null && AccountPattern.IsMatch(account) && account.StartsWith('2')
        && !account.StartsWith("28", StringComparison.Ordinal);

    /// <summary>A depreciation account: 28….</summary>
    public static bool IsDepreciationAccount(string? account) =>
        account is not null && AccountPattern.IsMatch(account) && account.StartsWith("28", StringComparison.Ordinal);

    /// <summary>A charge account: class 6.</summary>
    public static bool IsChargeAccount(string? account) =>
        account is not null && AccountPattern.IsMatch(account) && account.StartsWith('6');
}
