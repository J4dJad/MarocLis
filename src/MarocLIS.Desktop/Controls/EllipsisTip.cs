namespace MarocLIS.Desktop.Controls;

/// <summary>
/// Shows the whole text of a label that ends in « … » as its tooltip.
/// <para>
/// The page headers let the count beside the title shrink rather than push an action out of
/// sight, so at 1366×768 a long count (Accidents: the year's, the open ones, the late ones) was cut
/// with nothing to read the rest. The tooltip appears only while the text is actually cut.
/// </para>
/// </summary>
internal static class EllipsisTip
{
    public static void Attach(Label label)
    {
        var tip = new ToolTip();
        void Refresh()
        {
            var cut = label.Text.Length > 0
                      && TextRenderer.MeasureText(label.Text, label.Font).Width
                         > label.ClientSize.Width - label.Padding.Horizontal;
            tip.SetToolTip(label, cut ? label.Text : null);
        }

        label.TextChanged += (_, _) => Refresh();
        label.SizeChanged += (_, _) => Refresh();
        label.FontChanged += (_, _) => Refresh();
        label.Disposed += (_, _) => tip.Dispose();
    }
}
