using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Input;

/// <summary>
/// Why a form is not sent yet: the box to put the cursor back in, the message to say, and the one
/// value the message names — the text the operator typed, or an amount, which each screen writes in
/// its own language. The workstation's dialogs and the web's forms refuse the same entries with the
/// same words, from the same check.
/// </summary>
/// <param name="Field">The box at fault, as the forms name it (« amount », « reason », « bearer »).</param>
/// <param name="Key">The message's resource key.</param>
/// <param name="Typed">The text the message quotes, when it quotes one (« {0} n'est pas un nombre »).</param>
/// <param name="Amount">The amount the message names, when it names one (the most that can be taken).</param>
/// <param name="Argument">
/// A value the language files translate, when the message names one (a cold-chain range): passed to
/// the localizer as it is, it is said in the reader's language.
/// </param>
public sealed record FieldRefusal(string Field, string Key, string? Typed = null, decimal? Amount = null, Enum? Argument = null)
{
    /// <summary>What the message's placeholder takes, the amount aside: the text typed, or the value to translate.</summary>
    public object? Value => (object?)Typed ?? Argument;

    /// <summary>
    /// A service's refusal put next to its box: an amount it names stays an amount (written as money
    /// in the reader's language), a value to translate stays one, any other is quoted as it is.
    /// </summary>
    public static FieldRefusal Of(string field, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Args.FirstOrDefault() switch
        {
            null => new FieldRefusal(field, error.ResourceKey),
            Money money => new FieldRefusal(field, error.ResourceKey, Amount: money.Amount),
            Enum value => new FieldRefusal(field, error.ResourceKey, Argument: value),
            var other => new FieldRefusal(field, error.ResourceKey, other.ToString())
        };
    }
}
