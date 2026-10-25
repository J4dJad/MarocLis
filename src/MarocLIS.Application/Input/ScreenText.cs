namespace MarocLIS.Application.Input;

/// <summary>
/// A text of the screen in the operator's language: the workstation's <c>T(key, args)</c> or the
/// web's <c>Texts.T(key, args)</c>. A rule that writes a line of a list — the flags of a row, the
/// population of an interval — is handed it, so both clients write the same line in the same words
/// without either composing it.
/// </summary>
public delegate string ScreenText(string key, params object?[] args);
