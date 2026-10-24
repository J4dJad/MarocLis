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
public sealed record FieldRefusal(string Field, string Key, string? Typed = null, decimal? Amount = null);
