using MarocLIS.Domain.Common;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// Applies the current language to a WinForms control tree.
/// <para>
/// Arabic needs more than translated strings: the whole layout mirrors. WinForms does this through
/// <see cref="Control.RightToLeft"/> plus <see cref="Form.RightToLeftLayout"/>, and the two must be
/// set together — setting only the former reverses text alignment while leaving docking and control
/// order unmirrored, which produces a layout that is worse than either pure direction.
/// </para>
/// <para>
/// Applied recursively at runtime rather than per control at design time, so a screen added later
/// cannot forget to handle Arabic.
/// </para>
/// </summary>
public static class RtlLayout
{
    public static void Apply(Control root, AppLanguage language)
    {
        var rtl = language.IsRightToLeft();
        var direction = rtl ? RightToLeft.Yes : RightToLeft.No;

        ApplyRecursive(root, direction, rtl);
    }

    private static void ApplyRecursive(Control control, RightToLeft direction, bool rtl)
    {
        control.RightToLeft = direction;

        switch (control)
        {
            case Form form:
                form.RightToLeftLayout = rtl;
                break;

            // A mirrored grid still needs its columns reversed; WinForms handles the cells but not
            // the column order, so it is done explicitly.
            case DataGridView grid:
                grid.RightToLeft = direction;
                MirrorColumns(grid, rtl);
                IsolateLatinCells(grid, rtl);
                break;

            // Numeric and value inputs stay left-to-right even in Arabic: a result, a barcode or a
            // CIN is a Latin token, and mirroring it invites a transcription error.
            case TextBox { Tag: UiTags.LatinValue } input:
                input.RightToLeft = RightToLeft.No;
                input.TextAlign = HorizontalAlignment.Left;
                return;

            case NumericUpDown numeric:
                numeric.RightToLeft = RightToLeft.No;
                numeric.TextAlign = HorizontalAlignment.Right;
                return;
        }

        foreach (Control child in control.Controls) ApplyRecursive(child, direction, rtl);
    }

    /// <summary>
    /// Stops a Latin value from being re-ordered inside a mirrored grid.
    /// <para>
    /// This is a real defect, not a refinement. In a right-to-left paragraph the Unicode
    /// bidirectional algorithm lays out a run of neutral and left-to-right characters relative to
    /// the paragraph direction, so a cell reading <c>5 mL</c> renders as <c>mL 5</c>, <c>120/80</c>
    /// as <c>80/120</c>, and <c>9600-8-None-One</c> with its fields reversed. A reversed blood
    /// pressure or a reversed frame setting is not a cosmetic problem — one is a clinical value and
    /// the other is what a technician types into an analyser.
    /// </para>
    /// <para>
    /// Fixed at the grid rather than at the three hundred places that build such a string: every
    /// cell value that is entirely Latin, numeric or punctuation is wrapped in a first-strong
    /// isolate (U+2066 … U+2069), which tells the algorithm to lay that run out on its own and
    /// leave the surrounding paragraph alone. <see cref="UiTags.LatinValue"/> does the same job for
    /// a <see cref="TextBox"/>, and could not reach a cell.
    /// </para>
    /// </summary>
    private static void IsolateLatinCells(DataGridView grid, bool rtl)
    {
        // Detached first so a second Apply - which happens on every language change - does not
        // stack a second handler and isolate an already-isolated string.
        grid.CellFormatting -= LatinCellIsolator;

        if (rtl) grid.CellFormatting += LatinCellIsolator;
    }

    private static readonly DataGridViewCellFormattingEventHandler LatinCellIsolator =
        (_, e) =>
        {
            if (e.Value is string text && BidiText.NeedsIsolation(text))
                e.Value = BidiText.Isolate(text);
        };

    private static void MirrorColumns(DataGridView grid, bool rtl)
    {
        if (grid.Columns.Count == 0) return;

        var ordered = grid.Columns.Cast<DataGridViewColumn>()
                          .OrderBy(c => c.DisplayIndex)
                          .ToList();

        for (var i = 0; i < ordered.Count; i++)
            ordered[i].DisplayIndex = rtl ? ordered.Count - 1 - i : i;
    }
}

/// <summary>Markers placed in <see cref="Control.Tag"/> to opt a control out of mirroring.</summary>
public static class UiTags
{
    /// <summary>A field holding a Latin value — a barcode, a result, an accession number, a CIN.</summary>
    public const string LatinValue = "latin-value";
}

/// <summary>
/// Base for every form. Handles theming, RTL and live language switching.
/// <para>
/// Language changes are applied in place rather than by restarting: a laboratory counter is shared,
/// and an operator who needs to read a field to an Arabic-speaking patient should not have to lose
/// a half-entered registration to do it.
/// </para>
/// </summary>
public abstract class LocalizedForm : Form
{
    private readonly ICultureManager _cultureManager;

    protected ILocalizer Localizer { get; }

    protected LocalizedForm(ILocalizer localizer, ICultureManager cultureManager)
    {
        Localizer = localizer;
        _cultureManager = cultureManager;

        Font = UiTheme.Body;
        BackColor = UiTheme.Surface;
        ForeColor = UiTheme.TextPrimary;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;

        _cultureManager.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Assigns every user-visible string. Called on load and on each language change.</summary>
    protected abstract void ApplyTexts();

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyTexts();
        RtlLayout.Apply(this, _cultureManager.CurrentLanguage);
    }

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e)
    {
        if (IsDisposed || Disposing) return;

        if (InvokeRequired)
        {
            BeginInvoke(() => OnLanguageChanged(sender, e));
            return;
        }

        // Suspend the layout so a mirrored form does not repaint through an intermediate state.
        SuspendLayout();
        try
        {
            ApplyTexts();
            RtlLayout.Apply(this, e.Language);
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }
    }

    protected string T(string key) => Localizer[key];

    protected string T(string key, params object?[] args) => Localizer.Format(key, args);

    /// <summary>Renders a domain failure in the operator's language.</summary>
    protected string Describe(Result result) => Localizer.Describe(result.Errors);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cultureManager.LanguageChanged -= OnLanguageChanged;
        base.Dispose(disposing);
    }
}

/// <summary>Base for the views hosted inside the shell. Same contract as <see cref="LocalizedForm"/>.</summary>
public abstract class LocalizedView : UserControl
{
    private readonly ICultureManager _cultureManager;

    protected ILocalizer Localizer { get; }

    protected LocalizedView(ILocalizer localizer, ICultureManager cultureManager)
    {
        Localizer = localizer;
        _cultureManager = cultureManager;

        Dock = DockStyle.Fill;
        Font = UiTheme.Body;
        BackColor = UiTheme.Surface;
        ForeColor = UiTheme.TextPrimary;

        _cultureManager.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Title shown in the shell's header while this view is active.</summary>
    public abstract string Title { get; }

    protected abstract void ApplyTexts();

    /// <summary>
    /// Loads the view's data. Called by the shell after the view is shown, not from a constructor:
    /// a database round trip in a constructor blocks the navigation click and makes the whole
    /// application feel as though it has hung.
    /// </summary>
    public virtual Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyTexts();
        RtlLayout.Apply(this, _cultureManager.CurrentLanguage);

        // After the texts: a strip is only too wide once its buttons carry their real labels.
        BarReflow.AttachAll(this);
    }

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e)
    {
        if (IsDisposed || Disposing) return;

        if (InvokeRequired)
        {
            BeginInvoke(() => OnLanguageChanged(sender, e));
            return;
        }

        SuspendLayout();
        try
        {
            ApplyTexts();
            RtlLayout.Apply(this, e.Language);
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }
    }

    protected string T(string key) => Localizer[key];

    protected string T(string key, params object?[] args) => Localizer.Format(key, args);

    protected string Describe(Result result) => Localizer.Describe(result.Errors);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cultureManager.LanguageChanged -= OnLanguageChanged;
        base.Dispose(disposing);
    }
}
