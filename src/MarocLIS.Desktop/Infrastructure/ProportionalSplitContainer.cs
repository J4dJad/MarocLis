namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// A <see cref="SplitContainer"/> whose initial splitter position survives being created before
/// the screen has a size.
/// <para>
/// The screens build their splits in the constructor, where every control is still at its default
/// 150 x 100 pixels. The <c>SplitterDistance = 460</c> they ask for is clamped to what fits in that
/// box, and the container then keeps that accidental proportion as it grows: on a full-HD monitor
/// the billing screen gave its invoice list a strip at the bottom, and the collection screen
/// squeezed the tube list and its "Prélever" button into a narrow column at the far right.
/// </para>
/// <para>
/// The distance a screen asks for was chosen for a 1366 x 768 laptop, so it is kept as a share of
/// that layout and applied the first time the container has a real size. After that the ordinary
/// behaviour applies: the split keeps its proportion as the window is resized, and a splitter the
/// operator moves stays where they put it.
/// </para>
/// </summary>
public sealed class ProportionalSplitContainer : SplitContainer
{
    // The content area of the shell on a 1366 x 768 screen, once the navigation column, the
    // header and the view padding are taken off: the size the screens' distances were chosen for.
    private const int DesignWidth = 1120;
    private const int DesignHeight = 630;

    private double? _share;

    /// <summary>The distance as designed; stored as a share and applied once the size is real.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public new int SplitterDistance
    {
        get => base.SplitterDistance;
        set
        {
            var design = Orientation == Orientation.Vertical ? DesignWidth : DesignHeight;
            _share = Math.Clamp((double)value / design, 0.15, 0.85);
            TryApply();
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        TryApply();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TryApply();
    }

    private void TryApply()
    {
        if (_share is not { } share) return;

        var extent = Orientation == Orientation.Vertical ? Width : Height;

        // Still the constructor's placeholder size: wait for the real one.
        if (extent < 300) return;

        var wanted = (int)Math.Round(extent * share);
        var max = extent - SplitterWidth - Panel2MinSize;
        if (max <= Panel1MinSize) return;

        _share = null;
        base.SplitterDistance = Math.Clamp(wanted, Panel1MinSize, max);
    }
}
