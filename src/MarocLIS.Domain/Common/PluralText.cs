using System.Globalization;
using System.Text.RegularExpressions;

namespace MarocLIS.Domain.Common;

/// <summary>
/// The plural token of the catalogues: <c>{0:plural:commande|commandes}</c> becomes
/// <c>commande</c> or <c>commandes</c> by the count in argument 0 and the language's rule — in
/// French 0 and 1 take the singular (« 1 commande », « 0 rejet »), in English and Arabic only 1.
/// The count itself is written by its own placeholder: <c>{0} {0:plural:commande|commandes}</c>,
/// and an adjective further on agrees the same way (« {1} en retard ou {1:plural:manquant|manquants} »).
/// <para>
/// Read by the localizer for every catalogue text, and by the few texts that are not in a
/// catalogue — the audit trail's reasons, written in French, and the standard invoice's own
/// French words — so that they agree with their count too instead of reading « 3 tube(s) ».
/// A template without a token is left as it is; an argument that is not a number takes the last form.
/// </para>
/// </summary>
public static class PluralText
{
    private static readonly Regex Token = new(@"\{(\d+):plural:([^{}]*)\}", RegexOptions.CultureInvariant);

    /// <summary>The template with its plural tokens resolved, its other placeholders left for <see cref="string.Format(IFormatProvider, string, object[])"/>.</summary>
    public static string Resolve(string template, object?[] args, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(args);
        if (!template.Contains(":plural:", StringComparison.Ordinal)) return template;

        return Token.Replace(template, match =>
        {
            var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var forms = match.Groups[2].Value.Split('|');
            var singular = index < args.Length && args[index] is { } arg && IsSingular(arg, language);
            return (singular ? forms[0] : forms[^1]).Replace("{", "{{", StringComparison.Ordinal)
                                                     .Replace("}", "}}", StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A French text agreed with its counts and formatted: « Réception de 1 tube », « Réception de
    /// 3 tubes » from <c>"Réception de {0} {0:plural:tube|tubes}"</c>. For the texts written in
    /// French whatever the session's language — the audit trail's reasons. Numbers, amounts and
    /// dates are written in the current culture, as the interpolated texts it replaces wrote them.
    /// </summary>
    public static string French(string template, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Resolve(template, args, AppLanguage.French), args);

    /// <summary>Whether a count takes the singular: below 2 in French, exactly 1 in English and Arabic.</summary>
    public static bool IsSingular(object value, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(value);
        decimal count;
        try
        {
            if (value is string or bool or Enum or not IConvertible) return false;
            count = Math.Abs(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }

        return language == AppLanguage.French ? count < 2m : count == 1m;
    }
}
