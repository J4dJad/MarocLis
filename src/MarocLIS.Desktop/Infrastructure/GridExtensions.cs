namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// Grid helpers shared by every view.
/// </summary>
public static class GridExtensions
{
    /// <summary>
    /// The column called <paramref name="name"/>, or an exception naming it.
    /// <para>
    /// <see cref="DataGridViewColumnCollection"/>'s string indexer returns null for a name it does
    /// not hold, so <c>grid.Columns["payer"].HeaderText = …</c> — the shape every
    /// <c>ApplyTexts</c> override uses to translate its headers — is a null dereference waiting on a
    /// typo or on a column that was renamed in one place and not the other. There were 263 of them,
    /// and the failure they produce is a <see cref="NullReferenceException"/> from inside a language
    /// switch, with nothing in it to say which column or which screen.
    /// </para>
    /// <para>
    /// Throwing rather than returning null and letting the caller skip: a header that silently keeps
    /// its design-time text is a screen that stays untranslated in production, which on an Arabic
    /// workstation is the defect the whole localisation layer exists to prevent. A missing column is
    /// a programming error, it surfaces the first time the screen is opened, and the message says
    /// which name was asked for.
    /// </para>
    /// </summary>
    public static DataGridViewColumn Column(this DataGridView grid, string name) =>
        grid.Columns[name]
        ?? throw new ArgumentException(
            $"The grid has no column named '{name}'. The column names set up in the view's grid " +
            "construction and the ones its ApplyTexts override translates have diverged.",
            nameof(name));
}
