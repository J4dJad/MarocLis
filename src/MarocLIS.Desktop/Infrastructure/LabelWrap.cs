namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// Keeps a label that wraps within its <see cref="Control.MaximumSize"/> broken at its spaces only
/// (<see cref="TextWrap"/>), whatever text it is given later: left to Windows, « Dr Yasmine Alaoui
/// Belghiti (démo) » came out as « … (d | émo) ».
/// </summary>
internal static class LabelWrap
{
    public static void Attach(Label label)
    {
        ArgumentNullException.ThrowIfNull(label);

        var wrapping = false;
        label.TextChanged += (_, _) =>
        {
            if (wrapping || label.MaximumSize.Width <= 0 || string.IsNullOrEmpty(label.Text)) return;

            var flags = TextFormatFlags.SingleLine | (label.UseMnemonic ? 0 : TextFormatFlags.NoPrefix);
            var wrapped = TextWrap.Wrap(label.Text,
                                        w => TextRenderer.MeasureText(w, label.Font, Size.Empty, flags).Width,
                                        label.MaximumSize.Width - label.Padding.Horizontal - 2);
            if (wrapped == label.Text) return;

            wrapping = true;
            try { label.Text = wrapped; }
            finally { wrapping = false; }
        };
    }
}
