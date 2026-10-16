using System.Drawing.Drawing2D;
using MarocLIS.Desktop.Infrastructure;

namespace MarocLIS.Desktop.Views;

// The pieces the steering, relations and work-list screens are built from in direction A: the page
// head (title, count, actions), the search field, the white card that holds a grid, the card of the
// selected row, the figure tiles, the status pills of a grid, the « Plus d'actions » button and the
// frame of a dialog. Kept beside these screens rather than in the shared theme, which other screens
// are restyled against; the chips, pills and key hints of the reference screens are reused as they are.

/// <summary>Small factories shared by the screens of this file.</summary>
internal static class Pilotage
{
    /// <summary>The title of a dialog: 17 px semibold.</summary>
    public static readonly Font DialogTitle = EntryPaint.Resized(UiTheme.SectionTitle, 12.75f);

    /// <summary>The title of a card whose name is an identifier (a tube): 17 px fixed-pitch bold.</summary>
    public static readonly Font CodeTitle = EntryPaint.Resized(UiTheme.MonospaceBold, 12.75f);

    /// <summary>The status pairs of the design system, as soft fill and ink.</summary>
    public static readonly (Color Back, Color Fore) Ok = (UiTheme.NormalBackground, UiTheme.Normal);

    /// <summary>A field caption: 12 px semibold, muted.</summary>
    public static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = UiTheme.SmallBold,
        ForeColor = UiTheme.TextSecondary,
        Margin = new Padding(0, 8, 0, 3)
    };

    /// <summary>An empty band between two docked parts.</summary>
    public static Panel Gap(int height) =>
        new() { Dock = DockStyle.Top, Height = height, BackColor = UiTheme.Surface, Tag = UiTags.OwnLayout };

    /// <summary>A tab page holding one grid in a white card, a little below the tab strip.</summary>
    public static TabPage CardPage(DataGridView grid)
    {
        var page = new TabPage { BackColor = UiTheme.Surface, Padding = new Padding(0, 10, 0, 0) };
        page.Controls.Add(new ListCard(grid));
        return page;
    }

    /// <summary>The grid a tab page (or any container) shows, wherever it sits inside it.</summary>
    public static DataGridView? GridOf(Control? container)
    {
        if (container is null) return null;
        foreach (Control child in container.Controls)
        {
            if (child is DataGridView grid) return grid;
            if (GridOf(child) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>
    /// A secondary button that drops <paramref name="menu"/> below itself, its chevron drawn after
    /// the caption (a font may lack the ▾ glyph). Hidden by <see cref="SyncMore"/> when no entry is
    /// available.
    /// </summary>
    public static Button MoreButton(ContextMenuStrip menu)
    {
        var button = UiTheme.SecondaryButton(string.Empty);
        button.AutoSize = true;
        button.Tag = UiTags.NoMnemonic;
        button.Padding = new Padding(12, 0, 26, 0);
        button.RightToLeftChanged += (_, _) =>
            button.Padding = button.RightToLeft == RightToLeft.Yes ? new Padding(26, 0, 12, 0) : new Padding(12, 0, 26, 0);
        button.Paint += (_, e) =>
        {
            var rtl = button.RightToLeft == RightToLeft.Yes;
            var x = rtl ? 12 : button.Width - 20;
            var y = button.Height / 2 - 2;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(button.Enabled ? UiTheme.TextPrimary : UiTheme.TextMuted, 1.6f);
            e.Graphics.DrawLines(pen, [new Point(x, y), new Point(x + 4, y + 4), new Point(x + 8, y)]);
        };
        button.Click += (_, _) =>
        {
            menu.RightToLeft = button.RightToLeft;
            menu.Show(button, new Point(button.RightToLeft == RightToLeft.Yes ? button.Width : 0, button.Height));
        };
        menu.Font = UiTheme.Body;
        return button;
    }

    /// <summary>
    /// An entry of a « Plus d'actions » menu, with the icon its caption had as a button: the
    /// catalogue's icon for <paramref name="iconKey"/>, when there is one.
    /// </summary>
    public static ToolStripMenuItem MenuItem(string? iconKey = null)
    {
        var item = new ToolStripMenuItem();
        if (iconKey is not null && IconCatalog.Actions.TryGetValue(iconKey, out var glyph))
        {
            item.Image = Icons.Render(glyph, UiTheme.TextPrimary, 16);
            item.EnabledChanged += (_, _) =>
                item.Image = Icons.Render(glyph, item.Enabled ? UiTheme.TextPrimary : UiTheme.TextMuted, 16);
        }
        return item;
    }

    /// <summary>Shows the « Plus d'actions » button only while one of its entries is available.</summary>
    public static void SyncMore(Button more, ContextMenuStrip menu) =>
        more.Visible = menu.Items.Cast<ToolStripItem>().Any(i => i.Available);

    /// <summary>
    /// Draws the cells of <paramref name="column"/> as status pills: the glyph and the text of the
    /// cell on the soft fill and ink <paramref name="style"/> gives for the row, or as plain text
    /// when it gives none. The cell's value is unchanged, so the export and the print keep the text.
    /// </summary>
    public static void Pills(DataGridView grid, string column,
                             Func<DataGridViewRow, (Color Back, Color Fore, string Glyph)?> style)
    {
        grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.Graphics is null) return;
            if (grid.Columns[e.ColumnIndex].Name != column) return;
            if (e.FormattedValue is not string { Length: > 0 } value) return;
            if (style(grid.Rows[e.RowIndex]) is not { } pill) return;

            e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);

            var text = pill.Glyph.Length > 0 ? $"{pill.Glyph} {value.Trim('⁦', '⁨', '⁩')}" : value;
            var font = UiTheme.SmallBold;
            var size = EntryPaint.MeasurePill(text, font, 8);
            var width = Math.Min(size.Width, e.CellBounds.Width - 12);
            var rtl = grid.RightToLeft == RightToLeft.Yes;
            var x = rtl ? e.CellBounds.Right - 8 - width : e.CellBounds.Left + 8;
            var bounds = new Rectangle(x, e.CellBounds.Top + (e.CellBounds.Height - size.Height) / 2, width, size.Height);
            EntryPaint.DrawPill(e.Graphics, bounds, text, font, (pill.Back, pill.Fore), rtl);
            e.Handled = true;
        };
    }
}

/// <summary>
/// The head of a page: the title in 22 px, a muted count or period beside it, and the page's
/// actions on the far side, the main one last. When the actions leave little room, the count goes
/// first, then the title is shortened.
/// </summary>
internal sealed class PageHead : Panel
{
    public Label Title { get; } = new()
    {
        Font = UiTheme.PageTitle, ForeColor = UiTheme.TextPrimary, AutoSize = false, AutoEllipsis = true,
        UseMnemonic = false
    };

    public Label Count { get; } = new()
    {
        Font = UiTheme.Body, ForeColor = UiTheme.TextSecondary, AutoSize = false, AutoEllipsis = true,
        UseMnemonic = false
    };

    /// <summary>The page's buttons, in reading order; the main action goes last.</summary>
    public FlowLayoutPanel Actions { get; } = new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
        BackColor = UiTheme.Surface, Margin = Padding.Empty, Padding = Padding.Empty
    };

    private readonly ToolTip _tips = new();

    public PageHead()
    {
        Dock = DockStyle.Top;
        Height = 44;
        BackColor = UiTheme.Surface;
        Tag = UiTags.OwnLayout;
        Controls.AddRange([Title, Count, Actions]);

        Title.TextChanged += (_, _) => { _tips.SetToolTip(Title, Title.Text); PerformLayout(); };
        Count.TextChanged += (_, _) => { _tips.SetToolTip(Count, Count.Text); PerformLayout(); };
        Actions.SizeChanged += (_, _) => PerformLayout();
    }

    /// <summary>Adds buttons to the actions, each 34 px high and sized to its caption.</summary>
    public void Add(params Control[] controls)
    {
        foreach (var control in controls)
        {
            if (control is Button button)
            {
                button.AutoSize = true;
                button.MinimumSize = new Size(0, 34);
                button.Height = 34;
            }
            control.Margin = new Padding(8, 0, 0, 0);
            Actions.Controls.Add(control);
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var width = ClientSize.Width;
        if (width <= 0) return;

        var actions = Actions.Controls.Cast<Control>().Any(c => c.Visible) ? Actions.PreferredSize : Size.Empty;
        Actions.Bounds = new Rectangle(width - actions.Width, (Height - actions.Height) / 2, actions.Width, actions.Height);

        var room = Math.Max(0, width - actions.Width - 16);
        var title = TextRenderer.MeasureText(Title.Text, Title.Font);
        var titleWidth = Math.Min(title.Width + 4, room);
        Title.Bounds = new Rectangle(0, (Height - title.Height) / 2, titleWidth, title.Height);

        var count = TextRenderer.MeasureText(Count.Text, Count.Font);
        // The count is shown whole or not at all: a cut figure ("0 à…") says less than none.
        var countWidth = count.Width + 4;
        Count.Bounds = new Rectangle(Title.Right + 12, Title.Bottom - count.Height - 4, countWidth, count.Height);
        Count.Visible = Count.Text.Length > 0 && countWidth <= room - titleWidth - 12;
    }
}

/// <summary>
/// A search box in the design system's 34-pixel frame with a magnifier on its leading side. The box
/// itself is borderless; the frame turns accent while it has the focus.
/// </summary>
internal sealed class SearchField : Panel
{
    public TextBox Box { get; } = new() { BorderStyle = BorderStyle.None };

    public SearchField(int width)
    {
        Size = new Size(width, 34);
        BackColor = UiTheme.SurfaceRaised;
        Margin = new Padding(0, 0, 12, 0);
        Box.Font = UiTheme.Body;
        Box.BackColor = UiTheme.SurfaceRaised;
        Box.ForeColor = UiTheme.TextPrimary;
        Controls.Add(Box);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);

        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Resize += (_, _) => PlaceBox();
        PlaceBox();
    }

    private void PlaceBox() =>
        Box.Bounds = new Rectangle(34, (Height - Box.PreferredHeight) / 2 + 1, Math.Max(40, Width - 44), Box.PreferredHeight);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var focused = Box.Focused;
        using (var pen = new Pen(focused ? UiTheme.Accent : UiTheme.BorderStrong, focused ? 2 : 1))
        {
            var inset = focused ? 1 : 0;
            e.Graphics.DrawRectangle(pen, inset, inset, Width - 1 - inset * 2, Height - 1 - inset * 2);
        }

        if (Icons.Render(Glyph.Search, UiTheme.TextSecondary, 16) is { } icon)
            e.Graphics.DrawImage(icon, 10, (Height - 16) / 2, 16, 16);
    }
}

/// <summary>
/// The line under a page head: the search field, then the filter chips, then anything else the
/// screen filters on.
/// </summary>
internal sealed class FilterRow : FlowLayoutPanel
{
    public FilterRow()
    {
        Dock = DockStyle.Top;
        Height = 34;
        WrapContents = false;
        BackColor = UiTheme.Surface;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
    }

    /// <summary>A filter chip, centred on the 34-pixel line.</summary>
    public ChipButton Chip(EventHandler click)
    {
        var chip = new ChipButton { Height = 30, Margin = new Padding(0, 2, 8, 2) };
        chip.Click += click;
        Controls.Add(chip);
        return chip;
    }
}

/// <summary>
/// The white card that holds a grid, with an optional line of muted text at its foot (the outcome
/// of the last action).
/// </summary>
internal sealed class ListCard : Panel
{
    public Label Footer { get; } = new()
    {
        Dock = DockStyle.Bottom, Height = 30, AutoSize = false, AutoEllipsis = true,
        Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary, TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(12, 0, 12, 0), Visible = false, UseMnemonic = false
    };

    private readonly ToolTip _tips = new();

    public ListCard(DataGridView grid)
    {
        Dock = DockStyle.Fill;
        BackColor = UiTheme.SurfaceRaised;
        Padding = new Padding(1);
        grid.Dock = DockStyle.Fill;
        Controls.Add(grid);
        Controls.Add(Footer);
        UiChrome.AsCard(this);

        Footer.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, 0, 0, Footer.Width, 0);
        };
        Footer.TextChanged += (_, _) =>
        {
            Footer.Visible = Footer.Text.Length > 0;
            _tips.SetToolTip(Footer, Footer.Text);
        };
    }
}

/// <summary>
/// The card of the selected row, on the far side of the list: its name, a muted line, its status
/// pills, its key facts as label / value pairs, and the actions that apply to it.
/// </summary>
internal sealed class AsideCard : Panel
{
    private const int Inner = 268;

    private readonly TableLayoutPanel _stack = new()
    {
        Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1,
        BackColor = UiTheme.SurfaceRaised, Margin = Padding.Empty, Padding = Padding.Empty
    };

    private readonly TableLayoutPanel _facts = new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2,
        BackColor = UiTheme.SurfaceRaised, Margin = new Padding(0, 12, 0, 0), Padding = Padding.Empty
    };

    public Label Heading { get; } = new()
    {
        AutoSize = true, MaximumSize = new Size(Inner, 0), Font = Pilotage.DialogTitle,
        ForeColor = UiTheme.TextPrimary, UseMnemonic = false, Margin = Padding.Empty
    };

    public Label Subtitle { get; } = new()
    {
        AutoSize = true, MaximumSize = new Size(Inner, 0), Font = UiTheme.Body,
        ForeColor = UiTheme.TextSecondary, UseMnemonic = false, Margin = new Padding(0, 2, 0, 0)
    };

    public FlowLayoutPanel Pills { get; } = new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size(Inner, 0),
        BackColor = UiTheme.SurfaceRaised, Margin = new Padding(0, 10, 0, 0), Padding = Padding.Empty
    };

    /// <summary>The actions on the selected row, under its facts.</summary>
    public FlowLayoutPanel Actions { get; } = new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size(Inner, 0),
        FlowDirection = FlowDirection.TopDown, WrapContents = false,
        BackColor = UiTheme.SurfaceRaised, Margin = new Padding(0, 14, 0, 0), Padding = Padding.Empty
    };

    public Label Empty { get; } = new()
    {
        Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(Inner, 0), Font = UiTheme.Body,
        ForeColor = UiTheme.TextSecondary, UseMnemonic = false
    };

    public AsideCard()
    {
        Dock = DockStyle.Right;
        Width = Inner + 34;
        BackColor = UiTheme.SurfaceRaised;
        Padding = new Padding(16);
        AutoScroll = true;
        UiChrome.AsCard(this);

        _facts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        _facts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Inner - 112));
        _stack.Controls.Add(Heading, 0, 0);
        _stack.Controls.Add(Subtitle, 0, 1);
        _stack.Controls.Add(Pills, 0, 2);
        _stack.Controls.Add(_facts, 0, 3);
        _stack.Controls.Add(Actions, 0, 4);
        Controls.Add(_stack);
        Controls.Add(Empty);
        ShowRow(false);
    }

    /// <summary>Shows the row's card, or the muted line that says nothing is selected.</summary>
    public void ShowRow(bool row)
    {
        _stack.Visible = row;
        Empty.Visible = !row;
    }

    /// <summary>Sets the status pills; an empty text is skipped.</summary>
    public void SetPills(params (string Text, (Color Back, Color Fore) Colours)[] pills)
    {
        foreach (var old in Pills.Controls.Cast<Control>().ToList()) old.Dispose();
        foreach (var (text, colours) in pills.Where(p => !string.IsNullOrEmpty(p.Text)))
        {
            var pill = new PillLabel { Margin = new Padding(0, 0, 6, 4) };
            pill.Show(text, colours);
            Pills.Controls.Add(pill);
        }
        Pills.Visible = Pills.Controls.Count > 0;
    }

    /// <summary>Sets the label / value pairs; a value in a fixed-pitch face when it is an identifier.</summary>
    public void SetFacts(params (string Label, string? Value, bool Mono)[] facts)
    {
        _facts.SuspendLayout();
        foreach (var old in _facts.Controls.Cast<Control>().ToList()) old.Dispose();
        _facts.RowStyles.Clear();
        _facts.RowCount = facts.Length;

        for (var i = 0; i < facts.Length; i++)
        {
            var (label, value, mono) = facts[i];
            var blank = string.IsNullOrWhiteSpace(value);
            _facts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _facts.Controls.Add(new Label
            {
                Text = label, AutoSize = true, MaximumSize = new Size(108, 0), Font = UiTheme.Body,
                ForeColor = UiTheme.TextSecondary, Margin = new Padding(0, 3, 4, 3), UseMnemonic = false
            }, 0, i);
            _facts.Controls.Add(new Label
            {
                Text = blank ? "—" : value, AutoSize = true, MaximumSize = new Size(Inner - 114, 0),
                Font = mono && !blank ? UiTheme.Monospace : UiTheme.Body,
                ForeColor = blank ? UiTheme.TextMuted : UiTheme.TextPrimary,
                Margin = new Padding(0, 3, 0, 3), UseMnemonic = false
            }, 1, i);
        }

        _facts.ResumeLayout(performLayout: true);
    }

    /// <summary>Adds the row's action buttons, one per line, the width of the card.</summary>
    public void AddActions(params Button[] buttons)
    {
        foreach (var button in buttons)
        {
            button.AutoSize = false;
            button.Size = new Size(Inner, 34);
            button.Margin = new Padding(0, 0, 0, 8);
            Actions.Controls.Add(button);
        }
    }
}

/// <summary>
/// A figure tile: a white card with the caption in muted 12 px and the value large under it. The
/// text keeps the screens' "Caption\nValue" form; what follows the first " · " of the value goes on
/// a smaller muted line, and a value too wide for the tile is drawn smaller rather than cut.
/// </summary>
internal sealed class KpiTile : Label
{
    private static readonly Font[] ValueFonts = [UiTheme.Kpi, UiTheme.PageTitle, UiTheme.SectionTitle, UiTheme.BodyBold];

    public KpiTile()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw
                 | ControlStyles.UserPaint, true);
        UseMnemonic = false;
        BackColor = UiTheme.SurfaceRaised;
    }

    /// <summary>A colour for the border and the value (an amount overdue); null for none.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color? Emphasis
    {
        get => _emphasis;
        set { _emphasis = value; Invalidate(); }
    }

    private Color? _emphasis;

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? UiTheme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = UiChrome.RoundedRectangle(bounds, UiTheme.RadiusCard))
        {
            using (var fill = new SolidBrush(UiTheme.SurfaceRaised)) g.FillPath(fill, path);
            using var pen = new Pen(_emphasis ?? UiTheme.Border);
            g.DrawPath(pen, path);
        }

        if (string.IsNullOrEmpty(Text)) return;

        var lines = Text.Split('\n', 2);
        var caption = lines[0];
        var value = lines.Length > 1 ? lines[1] : string.Empty;
        var sub = string.Empty;
        var dot = value.IndexOf(" · ", StringComparison.Ordinal);
        if (dot > 0)
        {
            sub = value[(dot + 3)..];
            value = value[..dot];

            // A caption in two parts ("Délai · dans les délais") names the value and the line
            // under it: its second part goes after that line, where it is read with its figure.
            var captionDot = caption.IndexOf(" · ", StringComparison.Ordinal);
            if (captionDot > 0)
            {
                sub = $"{sub} {caption[(captionDot + 3)..]}";
                caption = caption[..captionDot];
            }
        }

        var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPadding;
        if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft | TextFormatFlags.Right;

        var inner = new Rectangle(14, 0, Width - 28, Height);
        var captionHeight = TextRenderer.MeasureText(caption, UiTheme.SmallBold).Height;
        var valueFont = ValueFonts.FirstOrDefault(f => TextRenderer.MeasureText(value, f).Width <= inner.Width);
        var valueFlags = flags;
        int valueHeight;
        if (valueFont is null)
        {
            // A sentence rather than a figure ("no closed payroll month…"): on two lines.
            valueFont = UiTheme.BodyBold;
            valueFlags = (flags & ~TextFormatFlags.SingleLine) | TextFormatFlags.WordBreak;
            valueHeight = Math.Min(TextRenderer.MeasureText(value, valueFont, new Size(inner.Width, 0), valueFlags).Height,
                                   valueFont.Height * 2 + 2);
        }
        else
        {
            valueHeight = TextRenderer.MeasureText(value, valueFont).Height;
        }
        var subHeight = sub.Length > 0 ? TextRenderer.MeasureText(sub, UiTheme.Small).Height : 0;

        var top = Math.Max(6, (Height - captionHeight - 2 - valueHeight - subHeight) / 2);

        TextRenderer.DrawText(g, caption, UiTheme.SmallBold, new Rectangle(inner.X, top, inner.Width, captionHeight),
                              UiTheme.TextSecondary, flags);
        top += captionHeight + 2;
        TextRenderer.DrawText(g, value, valueFont, new Rectangle(inner.X, top, inner.Width, valueHeight),
                              _emphasis ?? ForeColor, valueFlags);
        top += valueHeight;
        if (sub.Length > 0)
            TextRenderer.DrawText(g, sub, UiTheme.Small, new Rectangle(inner.X, top, inner.Width, subHeight),
                                  UiTheme.TextSecondary, flags);
    }
}

/// <summary>
/// The frame of a dialog in direction A: a white head with the title in 17 px semibold and one muted
/// sentence, and a foot strip with Cancel then the main button at the far end.
/// </summary>
internal static class DialogFrame
{
    /// <summary>The head band, docked at the top; its height follows the sentence.</summary>
    public static Panel Head(string title, string? sentence, int width)
    {
        var head = new Panel { Dock = DockStyle.Top, BackColor = UiTheme.SurfaceRaised, Tag = UiTags.OwnLayout };

        var heading = new Label
        {
            Text = title, Font = Pilotage.DialogTitle, ForeColor = UiTheme.TextPrimary, AutoSize = true,
            MaximumSize = new Size(width - 40, 0), Location = new Point(20, 16), UseMnemonic = false
        };
        head.Controls.Add(heading);

        var bottom = 16 + heading.PreferredHeight;
        if (!string.IsNullOrWhiteSpace(sentence))
        {
            var line = new Label
            {
                Text = sentence, Font = UiTheme.Body, ForeColor = UiTheme.TextSecondary, AutoSize = true,
                MaximumSize = new Size(width - 40, 0), Location = new Point(20, bottom + 4), UseMnemonic = false
            };
            head.Controls.Add(line);
            bottom += 4 + line.PreferredHeight;
        }

        head.Height = bottom + 14;
        head.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, 0, head.Height - 1, head.Width, head.Height - 1);
        };
        return head;
    }

    /// <summary>
    /// The foot strip: <paramref name="cancel"/> then <paramref name="primary"/>, the main action at
    /// the far end. Either may be null.
    /// </summary>
    public static Panel Foot(Button? cancel, Button? primary)
    {
        var strip = new EntryStrip { Dock = DockStyle.Bottom, Height = 58, BackColor = UiTheme.SurfaceRaised };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            Padding = new Padding(16, 12, 8, 12), BackColor = UiTheme.SurfaceRaised
        };

        foreach (var button in new[] { primary, cancel })
        {
            if (button is null) continue;
            button.AutoSize = true;
            button.MinimumSize = new Size(120, 34);
            button.Height = 34;
            button.Margin = new Padding(8, 0, 0, 0);
            flow.Controls.Add(button);
        }

        strip.Controls.Add(flow);
        return strip;
    }
}
