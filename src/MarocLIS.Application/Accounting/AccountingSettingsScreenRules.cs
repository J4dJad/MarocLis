using MarocLIS.Application.Input;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Accounting;

/// <summary>What « Comptes et journaux » offers by right: the workstation's PermissionGate on « Enregistrer » and « Actualiser ».</summary>
public sealed record AccountingSettingsActions(bool Save, bool Reload);

/// <summary>
/// The rules of « Comptes et journaux » that need no window: what each line says (its section, its
/// label with the department, site, payment method or kind of article it names), the patients'
/// account mode offered, and the values typed checked line by line — the check
/// AccountingExportService applies before anything is stored, each refusal said on its own line.
/// The workstation's AccountingSettingsView and the web's screen apply the same ones; the sheet and
/// the saving are AccountingExportService's.
/// </summary>
public static class AccountingSettingsScreenRules
{
    /// <summary>A journal code or a third-party account goes up to twenty characters, as the column.</summary>
    public const int ValueMaxLength = 20;

    public static AccountingSettingsActions Actions(Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(has);
        var right = has(Permissions.Accounting.Export);
        return new AccountingSettingsActions(right, right);
    }

    public static string SectionKey(AccountingSettingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"AccountingSection.{row.Section}";
    }

    /// <summary>« Produits du département BIO — Biochimie », « Encaissements par Chèque ».</summary>
    public static string Label(AccountingSettingRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        var argument = row.Argument is null ? null : row.ArgumentKey ? text(row.Argument) : row.Argument;
        return argument is null ? text(row.LabelKey) : text(row.LabelKey, argument);
    }

    /// <summary>The patients' account modes offered, with their caption's key.</summary>
    public static IReadOnlyList<(string Value, string Key)> ModeChoices =>
        [.. Enum.GetValues<PatientThirdPartyMode>().Select(m => (m.ToString(), $"PatientThirdPartyMode.{m}"))];

    /// <summary>The values as the sheet holds them, one per line.</summary>
    public static Dictionary<string, string?> Values(IEnumerable<AccountingSettingRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.ToDictionary(r => r.Key, r => (string?)r.Value, StringComparer.Ordinal);
    }

    /// <summary>The lines whose value differs from the one stored.</summary>
    public static int Changed(IEnumerable<AccountingSettingRow> rows, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(values);
        return rows.Count(r => values.TryGetValue(r.Key, out var v) && (v?.Trim() ?? string.Empty) != r.Value);
    }

    /// <summary>
    /// Every line refused, on its own line (the field is the setting's key): a value required left
    /// blank — its label quoted — or a value that is no account or code, quoted as typed. None when
    /// the sheet can be saved.
    /// </summary>
    public static IReadOnlyList<FieldRefusal> Check(IReadOnlyList<AccountingSettingRow> rows,
                                                    IReadOnlyDictionary<string, string?> values, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(text);

        var refusals = new List<FieldRefusal>();
        foreach (var row in rows)
        {
            if (!values.TryGetValue(row.Key, out var value)) continue;
            var checkedLine = AccountingExportService.Validate([row], new Dictionary<string, string?> { [row.Key] = value });
            if (checkedLine.IsSuccess) continue;

            var error = checkedLine.FirstError;
            refusals.Add(error.ResourceKey == "Accounting_SettingRequired"
                ? new FieldRefusal(row.Key, error.ResourceKey, Label(row, text))
                : new FieldRefusal(row.Key, error.ResourceKey, value?.Trim()));
        }
        return refusals;
    }
}
