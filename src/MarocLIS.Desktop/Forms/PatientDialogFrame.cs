using MarocLIS.Desktop.Infrastructure;

namespace MarocLIS.Desktop.Forms;

/// <summary>
/// The direction A frame of the patient dialogs: a white dialog with its title in 17 px semibold
/// and one muted sentence above the fields, field captions in semibold, and a footer bar along the
/// bottom with « Annuler » then the primary action at the far end.
/// <para>
/// Applied after a dialog has laid its fields out in absolute positions: the fields keep their
/// places relative to each other and move down under the header; the footer buttons leave the
/// field area for the bar. Nothing is renamed, rewired or resized but the buttons, which size to
/// their caption and icon.
/// </para>
/// </summary>
internal sealed class PatientDialogFrame
{
    private const int Side = 20;
    private const int FooterHeight = 58;

    private static readonly Font TitleFont = new(UiTheme.SectionTitle.FontFamily, 12.75f, UiTheme.SectionTitle.Style);

    private readonly Form _form;
    private readonly Label _title = new();
    private readonly Label _sentence;
    private readonly Panel _footer = new FooterBar();
    private readonly Button[] _buttons;
    private readonly Control? _status;
    private int _contentTop;

    private PatientDialogFrame(Form form, Label? sentence, Control? status, Button[] buttons)
    {
        _form = form;
        _sentence = sentence ?? new Label();
        _status = status;
        _buttons = buttons;
    }

    /// <summary>
    /// Frames <paramref name="form"/>. <paramref name="buttons"/> go to the footer in reading order,
    /// the primary last; <paramref name="status"/>, when given, takes the footer's free start.
    /// <paramref name="sentenceLabel"/>, when given, is a label of the dialog that becomes the
    /// sentence (and that the dialog may go on updating).
    /// </summary>
    public static PatientDialogFrame Apply(Form form, string title, string sentence, Label? sentenceLabel,
                                           Control? status, params Button[] buttons)
    {
        var frame = new PatientDialogFrame(form, sentenceLabel, status, buttons);
        frame.Build(title, sentence);
        return frame;
    }

    /// <summary>Changes the title and the sentence, after a language change or once the file is loaded.</summary>
    public void SetTexts(string title, string? sentence = null)
    {
        _title.Text = title;
        if (sentence is not null) _sentence.Text = sentence;
        PlaceHeader();
    }

    private void Build(string title, string sentence)
    {
        var form = _form;
        form.BackColor = UiTheme.SurfaceRaised;

        var moved = new HashSet<Control>(_buttons);
        if (_status is not null) moved.Add(_status);
        moved.Add(_sentence);

        var content = form.Controls.Cast<Control>().Where(c => !moved.Contains(c)).ToList();
        var top = content.Count == 0 ? 0 : content.Min(c => c.Top);
        var bottom = content.Count == 0 ? 0 : content.Max(c => c.Bottom);

        foreach (Control control in form.Controls) RestyleCaptions(control);

        _title.Text = title;
        _title.Font = TitleFont;
        _title.ForeColor = UiTheme.TextPrimary;
        _title.AutoSize = false;
        _title.AutoEllipsis = true;
        _title.UseMnemonic = false;

        if (_sentence.Parent is not null) _sentence.Parent.Controls.Remove(_sentence);
        _sentence.Text = sentence;
        _sentence.Font = UiTheme.Body;
        _sentence.ForeColor = UiTheme.TextSecondary;
        _sentence.AutoSize = false;
        _sentence.UseMnemonic = false;

        PlaceHeader();

        var shift = _contentTop - top;
        foreach (var control in content) control.Top += shift;

        // --- footer ----------------------------------------------------------------------------------

        // Taking a button off the form clears it as the form's default or cancel button: Enter did
        // nothing in « Alerte patient » and the other framed dialogs. Put back once in the footer.
        var accept = form.AcceptButton;
        var cancel = form.CancelButton;

        foreach (var button in _buttons)
        {
            button.Parent?.Controls.Remove(button);
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowOnly;
            button.MinimumSize = new Size(96, 34);
            button.Size = new Size(96, 34);
            _footer.Controls.Add(button);
        }

        if (_status is not null)
        {
            _status.Parent?.Controls.Remove(_status);
            _footer.Controls.Add(_status);
        }

        _footer.Dock = DockStyle.Bottom;
        _footer.Height = FooterHeight;
        _footer.BackColor = UiTheme.SurfaceRaised;
        _footer.Tag = UiTags.OwnLayout;
        _footer.Layout += (_, _) => PlaceFooter();
        _footer.RightToLeftChanged += (_, _) => PlaceFooter();

        form.Controls.Add(_title);
        form.Controls.Add(_sentence);
        form.Controls.Add(_footer);

        form.AcceptButton = accept;
        form.CancelButton = cancel;

        form.ClientSize = new Size(form.ClientSize.Width, _contentTop + (bottom - top) + 16 + FooterHeight);
    }

    private void PlaceHeader()
    {
        var width = _form.ClientSize.Width - 2 * Side;
        _title.Bounds = new Rectangle(Side, 16, width, 28);

        // At least one line, so a sentence filled in later (the file's name once loaded) shows.
        var line = TextRenderer.MeasureText("Ag", _sentence.Font).Height;
        var height = Math.Max(line, TextRenderer.MeasureText(_sentence.Text, _sentence.Font, new Size(width, 0),
                                                             TextFormatFlags.WordBreak).Height);
        _sentence.Bounds = new Rectangle(Side, 46, width, height);

        // The fields start under the header; a later, longer sentence keeps the place first given,
        // as the fields were already moved.
        if (_contentTop == 0) _contentTop = 46 + height + 16;
    }

    // Laid out left to right, then mirrored in Arabic: the form's mirroring does not reach the
    // children of a panel, and the bar kept « Annuler » then the primary from left to right.
    private void PlaceFooter()
    {
        var width = _footer.ClientSize.Width;
        var rtl = _footer.RightToLeft == RightToLeft.Yes;

        Rectangle Mirrored(Rectangle r) => rtl ? r with { X = width - r.Right } : r;

        var x = width - Side;
        foreach (var button in _buttons.Reverse())
        {
            x -= button.Width;
            button.Bounds = Mirrored(new Rectangle(x, (FooterHeight - button.Height) / 2 + 1,
                                                   button.Width, button.Height));
            x -= 8;
        }

        if (_status is not null)
            _status.Bounds = Mirrored(new Rectangle(Side, 4, Math.Max(0, x - Side - 4), FooterHeight - 8));
    }

    // Field captions (UiTheme.FieldLabel: 11 px, muted, sized to their text) become 12 px semibold.
    private static void RestyleCaptions(Control control)
    {
        if (control is Label { AutoSize: true } label && ReferenceEquals(label.Font, UiTheme.Small)
            && label.ForeColor.ToArgb() == UiTheme.TextSecondary.ToArgb())
            label.Font = UiTheme.SmallBold;

        foreach (Control child in control.Controls) RestyleCaptions(child);
    }

    /// <summary>The footer: white, with the line colour along its top.</summary>
    private sealed class FooterBar : Panel
    {
        public FooterBar() => SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, 0, 0, Width, 0);
        }
    }
}
