using System.Drawing.Drawing2D;
using System.Globalization;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

// The pieces the stock, purchasing and equipment screens are laid out with in direction A (the
// « liste » pattern of the Patients mock-up): a title line with its count and actions, a filter line
// with a search box and chips, white cards holding the grids, status pills, « Plus d'actions ▾ »
// menus and the dialog frame. Kept beside these screens rather than in the shared theme, which the
// other screens are restyled against.

/// <summary>A status as the design system draws it: a soft fill, its ink, and a symbol before the word.</summary>
internal readonly record struct StockPill((Color Back, Color Fore) Tone, string Symbol)
{
    public static readonly StockPill Waiting = new((UiTheme.SurfaceSunken, UiTheme.TextPrimary), "○");
    public static readonly StockPill Running = new((UiTheme.InfoBackground, UiTheme.Info), "◐");
    public static readonly StockPill Done = new((UiTheme.NormalBackground, UiTheme.Normal), "✓");
    public static readonly StockPill Warning = new((UiTheme.AbnormalBackground, UiTheme.Abnormal), "!");
    public static readonly StockPill Critical = new((UiTheme.CriticalBackground, UiTheme.CriticalInk), "✕");
}

internal static class StockGrid
{
    /// <summary>
    /// Draws the cells of <paramref name="column"/> as pills. The cell keeps its text, so printing
    /// and copying are unchanged; only the painting differs.
    /// </summary>
    public static void Pills(DataGridView grid, string column, Func<object?, StockPill?> pill)
    {
        grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.Graphics is null) return;
            if (!string.Equals(grid.Columns[e.ColumnIndex].Name, column, StringComparison.Ordinal)) return;

            var text = Convert.ToString(e.FormattedValue, CultureInfo.CurrentCulture)?
                .Replace("⁦", string.Empty).Replace("⁧", string.Empty)
                .Replace("⁨", string.Empty).Replace("⁩", string.Empty).Trim();
            if (string.IsNullOrEmpty(text) || pill(grid.Rows[e.RowIndex].Tag) is not { } p) return;

            e.Paint(e.ClipBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.ContentForeground);

            var label = $"{p.Symbol} {text}";
            var font = UiTheme.SmallBold;
            var size = EntryPaint.MeasurePill(label, font);
            var bounds = e.CellBounds;
            var rectangle = new Rectangle(bounds.X + 6, bounds.Y + (bounds.Height - size.Height) / 2,
                                          Math.Max(0, Math.Min(size.Width, bounds.Width - 12)), size.Height);
            var rtl = grid.RightToLeft == RightToLeft.Yes;

            EntryPaint.DrawPill(e.Graphics, EntryPaint.Mirror(rectangle, bounds, rtl), label, font, p.Tone, rtl);
            e.Handled = true;
        };
    }

    /// <summary>
    /// Shows only the rows with a cell containing <paramref name="text"/>, on screen: nothing is
    /// queried again. The current row moves to the first row still shown when it is hidden.
    /// </summary>
    public static void Filter(DataGridView grid, string? text)
    {
        var needle = (text ?? string.Empty).Trim();

        bool Matches(DataGridViewRow row) =>
            needle.Length == 0
            || row.Cells.Cast<DataGridViewCell>().Any(c =>
                Convert.ToString(c.Value, CultureInfo.CurrentCulture)?
                    .Contains(needle, StringComparison.CurrentCultureIgnoreCase) == true);

        var rows = grid.Rows.Cast<DataGridViewRow>().ToList();

        try
        {
            if (grid.CurrentRow is { } current && !Matches(current))
            {
                var next = rows.FirstOrDefault(Matches);
                grid.CurrentCell = next?.Cells.Cast<DataGridViewCell>().FirstOrDefault(c => c.Visible);
            }

            foreach (var row in rows)
            {
                var show = Matches(row);
                if (row.Visible != show) row.Visible = show;
            }
        }
        catch (InvalidOperationException)
        {
            // A grid in the middle of an edit refuses to move its current cell: left as it is.
        }
    }
}

/// <summary>
/// A filter chip: a tick box drawn as a pill, outlined when off and in accent-soft with an accent
/// outline when on. Still a <see cref="CheckBox"/>, with its event and its Alt+letter.
/// </summary>
internal sealed class StockChip : CheckBox
{
    public StockChip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        Appearance = Appearance.Button;
        AutoSize = false;
        Height = 30;
        Font = UiTheme.Body;
        Cursor = Cursors.Hand;
        Margin = new Padding(0);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Width = TextRenderer.MeasureText(Mnemonics.Plain(Text), UiTheme.BodyBold, Size.Empty,
                                         TextFormatFlags.NoPadding).Width + 30;
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var behind = new SolidBrush(Parent?.BackColor ?? UiTheme.Surface))
            g.FillRectangle(behind, ClientRectangle);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);

        using (var path = UiChrome.RoundedRectangle(bounds, bounds.Height / 2))
        {
            using (var fill = new SolidBrush(Checked ? UiTheme.AccentSubtle : UiTheme.SurfaceRaised))
                g.FillPath(fill, path);
            using var pen = new Pen(Checked || Focused ? UiTheme.Accent : UiTheme.BorderStrong);
            g.DrawPath(pen, path);
        }

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | TextFormatFlags.NoPadding | TextFormatFlags.HidePrefix;
        if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;

        TextRenderer.DrawText(g, Text, Checked ? UiTheme.BodyBold : UiTheme.Body, bounds,
                              !Enabled ? UiTheme.TextMuted : Checked ? UiTheme.AccentInk : UiTheme.TextPrimary, flags);
    }

    /// <summary>
    /// Makes <paramref name="chips"/> behave as one choice: a click ticks the chip clicked and
    /// clears the others, and ticking the one already ticked changes nothing.
    /// </summary>
    public static void Exclusive(IReadOnlyList<StockChip> chips, Action<StockChip> chosen)
    {
        foreach (var chip in chips)
        {
            chip.AutoCheck = false;
            chip.Click += (_, _) =>
            {
                if (chip.Checked) return;
                foreach (var other in chips) other.Checked = other == chip;
                chosen(chip);
            };
        }
    }
}

/// <summary>The search box of a list: a magnifier and a borderless box in a 34-pixel frame.</summary>
internal sealed class StockSearchBox : Panel
{
    public TextBox Box { get; } = new() { BorderStyle = BorderStyle.None };

    public StockSearchBox()
    {
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = UiTheme.SurfaceRaised;
        Size = new Size(360, 34);

        Box.Font = UiTheme.Body;
        Box.BackColor = UiTheme.SurfaceRaised;
        Box.ForeColor = UiTheme.TextPrimary;
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Controls.Add(Box);
    }

    /// <summary>Raised as the operator types.</summary>
    public event EventHandler? Changed
    {
        add => Box.TextChanged += value;
        remove => Box.TextChanged -= value;
    }

    public string Query => Box.Text;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Placeholder
    {
        get => Box.PlaceholderText;
        set => Box.PlaceholderText = value;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var box = new Rectangle(34, (Height - Box.PreferredHeight) / 2 + 1, Math.Max(40, Width - 44),
                                Box.PreferredHeight);
        // Arabic: the magnifier on the right, where the reading starts.
        Box.Bounds = RightToLeft == RightToLeft.Yes ? box with { X = Width - box.Right } : box;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var focused = Box.Focused;
        using var pen = new Pen(focused ? UiTheme.Accent : UiTheme.BorderStrong, focused ? 2 : 1);
        var inset = focused ? 1 : 0;
        e.Graphics.DrawRectangle(pen, inset, inset, Width - 1 - inset * 2, Height - 1 - inset * 2);

        if (Icons.Render(Glyph.Search, UiTheme.TextSecondary, 16) is { } icon)
            e.Graphics.DrawImage(icon, RightToLeft == RightToLeft.Yes ? Width - 26 : 10, (Height - 16) / 2, 16, 16);
    }
}

/// <summary>
/// A line of controls: some from the start, the others gathered at the end (the actions, the
/// primary one last). When they do not fit, the line wraps and grows, rather than pushing a button
/// out of sight on a 1366-pixel screen. One start control may stretch over the room left.
/// </summary>
internal sealed class StockBar : Panel
{
    private readonly List<Control> _start = [];
    private readonly List<Control> _end = [];
    private bool _arranging;

    public StockBar()
    {
        Tag = UiTags.OwnLayout;
        Dock = DockStyle.Top;
        BackColor = UiTheme.Surface;
        Height = 34;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int LineHeight { get; set; } = 34;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Gap { get; set; } = 8;

    /// <summary>A hairline along the top: the bar is the action bar at the foot of a card.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool TopLine { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!TopLine) return;
        using var pen = new Pen(UiTheme.Border);
        e.Graphics.DrawLine(pen, 0, 0, Width, 0);
    }

    /// <summary>The start control that takes the room left on its line, between the two widths.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Control? Stretch { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int StretchMinimum { get; set; } = 220;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int StretchMaximum { get; set; } = 520;

    public StockBar Start(params Control[] controls) => Add(_start, controls);

    public StockBar End(params Control[] controls) => Add(_end, controls);

    private StockBar Add(List<Control> list, Control[] controls)
    {
        foreach (var control in controls)
        {
            list.Add(control);
            Controls.Add(control);
            control.SizeChanged += (_, _) => Arrange();
            control.VisibleChanged += (_, _) => Arrange();
        }

        Arrange();
        return this;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        Arrange();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        Arrange();
    }

    protected override void OnRightToLeftChanged(EventArgs e)
    {
        base.OnRightToLeftChanged(e);
        Arrange();
    }

    // Visible reads false for every child while the screen is not shown yet: then each one counts.
    private bool Shown(Control control) => control.Visible || !Visible;

    private int Wanted(Control control) => control == Stretch ? StretchMinimum : control.Width;

    private int Used(List<Control> line) => line.Count == 0 ? 0 : line.Sum(Wanted) + Gap * (line.Count - 1);

    public void Arrange()
    {
        if (_arranging || ClientSize.Width <= 0) return;
        _arranging = true;

        try
        {
            var width = ClientSize.Width - Padding.Horizontal;
            // Arabic: the line reads from the right. Nested panels are not mirrored by the form, so
            // each position is mirrored here.
            var rtl = RightToLeft == RightToLeft.Yes;
            Rectangle Place(Rectangle r) => rtl ? r with { X = ClientSize.Width - r.Right } : r;
            var start = _start.Where(Shown).ToList();
            var end = _end.Where(Shown).ToList();

            var lines = new List<List<Control>> { new() };
            foreach (var control in start)
            {
                if (lines[^1].Count > 0 && Used(lines[^1]) + Gap + Wanted(control) > width) lines.Add([]);
                lines[^1].Add(control);
            }

            // The end group in lines of its own when it does not fit after the start controls.
            var endLines = new List<List<Control>> { new() };
            foreach (var control in end)
            {
                if (endLines[^1].Count > 0 && Used(endLines[^1]) + Gap + control.Width > width) endLines.Add([]);
                endLines[^1].Add(control);
            }

            var firstEndWidth = Used(endLines[0]);
            var noStart = lines.Count == 1 && lines[0].Count == 0;
            var sharesLine = end.Count == 0 || endLines.Count == 1
                             && Used(lines[^1]) + (lines[^1].Count > 0 ? 24 : 0) + firstEndWidth <= width;

            var y = Padding.Top;
            for (var i = 0; i < lines.Count && !noStart; i++)
            {
                var line = lines[i];
                var last = i == lines.Count - 1;
                var room = width - Used(line) - (last && sharesLine && firstEndWidth > 0 ? firstEndWidth + 24 : 0);
                var x = 0;

                foreach (var control in line)
                {
                    var w = Wanted(control);
                    if (control == Stretch)
                        w = Math.Clamp(w + Math.Max(0, room), StretchMinimum, Math.Max(StretchMinimum, StretchMaximum));
                    control.Bounds = Place(new Rectangle(Padding.Left + x, y + (LineHeight - control.Height) / 2, w, control.Height));
                    x += w + Gap;
                }

                if (!last || !sharesLine) y += LineHeight + Gap;
            }

            // Here y is the top of the line the end group starts on.
            var bottom = y + LineHeight;
            foreach (var line in endLines.Where(l => l.Count > 0))
            {
                var x = Padding.Left + width;
                for (var i = line.Count - 1; i >= 0; i--)
                {
                    var control = line[i];
                    x -= control.Width;
                    control.Location = Place(new Rectangle(x, y + (LineHeight - control.Height) / 2,
                                                           control.Width, control.Height)).Location;
                    x -= Gap;
                }

                bottom = y + LineHeight;
                y += LineHeight + Gap;
            }

            var height = bottom + Padding.Bottom;
            if (Height != height) Height = height;
        }
        finally
        {
            _arranging = false;
        }
    }
}

/// <summary>
/// A card: white, the line border with its rounded corners, a heading line (title, a muted note,
/// tools and actions), the body, and a muted footer that shows only when it has something to say.
/// </summary>
internal sealed class StockCard : Panel
{
    public StockCard(Control body, bool heading = true, StockFooter? footer = null)
    {
        Footer = footer ?? new StockFooter();

        BackColor = UiTheme.SurfaceRaised;
        Dock = DockStyle.Fill;
        Padding = new Padding(1, heading ? 4 : 6, 1, 6);
        UiChrome.AsCard(this);

        Title.Font = UiTheme.SectionTitle;
        Title.ForeColor = UiTheme.TextPrimary;
        Title.AutoSize = true;
        Title.UseMnemonic = false;

        Note.Font = UiTheme.Body;
        Note.ForeColor = UiTheme.TextSecondary;
        Note.AutoSize = true;
        Note.UseMnemonic = false;

        Heading.BackColor = UiTheme.SurfaceRaised;
        Heading.Padding = new Padding(15, 6, 15, 8);
        Heading.Start(Title, Note);

        body.Dock = DockStyle.Fill;
        Controls.Add(body);
        Controls.Add(Footer);
        if (heading) Controls.Add(Heading);
    }

    public StockBar Heading { get; } = new();
    public Label Title { get; } = new();
    public Label Note { get; } = new();
    public StockFooter Footer { get; }
}

/// <summary>The muted line at the foot of a card, under a hairline; hidden while empty.</summary>
internal sealed class StockFooter : Label
{
    public StockFooter()
    {
        Dock = DockStyle.Bottom;
        AutoSize = false;
        AutoEllipsis = true;
        Font = UiTheme.Small;
        ForeColor = UiTheme.TextSecondary;
        BackColor = UiTheme.SurfaceRaised;
        Padding = new Padding(14, 7, 14, 0);
        UseMnemonic = false;
        Height = 30;
        Visible = false;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Visible = !string.IsNullOrEmpty(Text);
        Fit();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Fit();
    }

    private bool _fitting;

    // Two lines when the sentence needs them (the due-date summary, the alert rule), never more.
    private void Fit()
    {
        if (_fitting || Width <= 40) return;
        _fitting = true;
        try
        {
            var text = TextRenderer.MeasureText(Text, Font, new Size(Width - Padding.Horizontal, 0),
                                                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            var height = Math.Clamp(text.Height + Padding.Vertical + 8, 30, 50);
            if (Height != height) Height = height;
        }
        finally
        {
            _fitting = false;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(UiTheme.Border);
        e.Graphics.DrawLine(pen, 0, 0, Width, 0);
    }
}

/// <summary>The key facts of a selected item: a muted caption and its value, line after line.</summary>
internal sealed class StockFacts : TableLayoutPanel
{
    public StockFacts()
    {
        ColumnCount = 2;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = UiTheme.SurfaceRaised;
        Margin = new Padding(0);
        Padding = new Padding(0);
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    }

    public void Show(IEnumerable<(string Caption, string Value, bool Mono)> facts)
    {
        SuspendLayout();
        Controls.Clear();
        RowStyles.Clear();

        var row = 0;
        foreach (var (caption, value, mono) in facts)
        {
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(new Label
            {
                Text = caption, AutoSize = true, UseMnemonic = false, Font = UiTheme.Body,
                ForeColor = UiTheme.TextSecondary, Margin = new Padding(0, 3, 8, 3)
            }, 0, row);
            Controls.Add(new Label
            {
                Text = string.IsNullOrWhiteSpace(value) ? "—" : value, AutoSize = true, UseMnemonic = false,
                Font = mono ? UiTheme.Monospace : UiTheme.Body,
                ForeColor = string.IsNullOrWhiteSpace(value) ? UiTheme.TextMuted : UiTheme.TextPrimary,
                Margin = new Padding(0, 3, 0, 3), MaximumSize = new Size(220, 0)
            }, 1, row);
            row++;
        }

        RowCount = row;
        ResumeLayout(performLayout: true);
    }
}

/// <summary>
/// A button kept off the screen whose action runs from a « Plus d'actions ▾ » entry. It keeps its
/// caption, its permission gate and the enabled state the screen gives it; only where it is shown
/// changes.
/// </summary>
internal sealed class StockMenuButton : Button
{
    public StockMenuButton() => UiTheme.StyleSecondary(this);

    /// <summary>Runs the button's action when it is enabled, wherever the button is (or is not) shown.</summary>
    public void Run()
    {
        if (Enabled) OnClick(EventArgs.Empty);
    }
}

internal static class StockMenu
{
    /// <summary>
    /// The « Plus d'actions ▾ » button: a secondary button with a chevron that opens
    /// <paramref name="menu"/> under it. Each entry's caption, icon and enabled state are read
    /// from its button when the menu opens.
    /// </summary>
    public static Button More(ContextMenuStrip menu, ILocalizer localizer)
    {
        var button = UiTheme.SecondaryButton(string.Empty);
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(0, 34);
        button.Height = 34;
        button.Padding = new Padding(10, 0, 26, 0);
        button.RightToLeftChanged += (_, _) =>
            button.Padding = button.RightToLeft == RightToLeft.Yes ? new Padding(26, 0, 10, 0) : new Padding(10, 0, 26, 0);
        button.Paint += (_, e) =>
        {
            var x = button.RightToLeft == RightToLeft.Yes ? 12 : button.Width - 20;
            var y = button.Height / 2 - 2;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(button.Enabled ? UiTheme.TextPrimary : UiTheme.TextMuted, 1.6f);
            e.Graphics.DrawLines(pen, [new Point(x, y), new Point(x + 4, y + 4), new Point(x + 8, y)]);
        };

        menu.Font = UiTheme.Body;
        menu.ShowImageMargin = true;
        menu.Opening += (_, _) => Refresh(menu, localizer);
        button.Click += (_, _) =>
            menu.Show(button, new Point(button.RightToLeft == RightToLeft.Yes ? button.Width : 0, button.Height));

        return button;
    }

    /// <summary>An entry that runs <paramref name="button"/>'s action.</summary>
    public static ToolStripMenuItem Entry(StockMenuButton button)
    {
        var item = new ToolStripMenuItem { Tag = button };
        item.Click += (_, _) => button.Run();
        return item;
    }

    /// <summary>Copies each button's caption, enabled state and icon onto its entry.</summary>
    public static void Refresh(ContextMenuStrip menu, ILocalizer localizer)
    {
        menu.RightToLeft = localizer.CurrentLanguage.IsRightToLeft() ? RightToLeft.Yes : RightToLeft.No;

        foreach (var item in menu.Items.OfType<ToolStripMenuItem>())
        {
            if (item.Tag is not Button button) continue;

            var caption = Mnemonics.Plain(button.Text).Trim();
            item.Text = caption;
            item.Enabled = button.Enabled;
            item.Image = IconFor(caption, localizer) is { } glyph
                ? Icons.Render(glyph, item.Enabled ? UiTheme.TextPrimary : UiTheme.TextMuted, 16)
                : null;
        }
    }

    private static Glyph? IconFor(string caption, ILocalizer localizer)
    {
        foreach (var (key, glyph) in IconCatalog.Actions)
            if (string.Equals(localizer[key].Trim(), caption, StringComparison.CurrentCultureIgnoreCase))
                return glyph;

        return null;
    }
}

internal static class StockPage
{
    /// <summary>The page title, 22 px.</summary>
    public static Label Title() => new()
    {
        Font = UiTheme.PageTitle, ForeColor = UiTheme.TextPrimary, AutoSize = true, UseMnemonic = false
    };

    /// <summary>The muted count after the title.</summary>
    public static Label Count() => new()
    {
        Font = UiTheme.Body, ForeColor = UiTheme.TextSecondary, AutoSize = true, UseMnemonic = false,
        Padding = new Padding(0, 6, 0, 0)
    };

    /// <summary>A caption before a field in a filter line ("Du", "Année").</summary>
    public static Label Caption(Label label)
    {
        label.Font = UiTheme.SmallBold;
        label.ForeColor = UiTheme.TextSecondary;
        label.AutoSize = true;
        return label;
    }

    /// <summary>
    /// An action button of a title, card or action line: 34 pixels high, as wide as its caption
    /// and icon, so the icon is never dropped for lack of room.
    /// </summary>
    public static T Action<T>(T button) where T : Button
    {
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(0, 34);
        button.Height = 34;
        button.Margin = new Padding(0);
        return button;
    }

    /// <summary>The page header: title and count at the start, the actions at the end.</summary>
    public static StockBar Header(Label title, Label count, params Control[] actions)
    {
        var bar = new StockBar { LineHeight = 40, Padding = new Padding(0, 0, 0, 10) };
        bar.Start(title, count);
        foreach (var action in actions.OfType<Button>()) Action(action);
        bar.End(actions);
        return bar;
    }

    /// <summary>The filter line: a search box that stretches, then chips and fields.</summary>
    public static StockBar Filters(StockSearchBox? search, params Control[] filters)
    {
        var bar = new StockBar { Padding = new Padding(0, 0, 0, 12), StretchMinimum = 260, StretchMaximum = 520 };
        if (search is not null)
        {
            bar.Stretch = search;
            bar.Start(search);
        }

        bar.Start(filters);
        return bar;
    }

    /// <summary>The action bar at the foot of a card: white, a hairline above, the primary last.</summary>
    public static StockBar ActionBar(params Control[] actions)
    {
        var bar = new StockBar
        {
            Dock = DockStyle.Bottom, BackColor = UiTheme.SurfaceRaised, TopLine = true,
            Padding = new Padding(15, 10, 15, 10)
        };
        foreach (var action in actions.OfType<Button>()) Action(action);
        bar.End(actions);
        return bar;
    }

    /// <summary>A small caption over a grid inside a card ("Fournisseurs consultés").</summary>
    public static Label GridCaption(Label label)
    {
        label.Dock = DockStyle.Top;
        label.Height = 30;
        label.Font = UiTheme.SmallBold;
        label.ForeColor = UiTheme.TextSecondary;
        label.BackColor = UiTheme.SurfaceRaised;
        label.Padding = new Padding(14, 8, 14, 0);
        label.UseMnemonic = false;
        label.AutoSize = false;
        return label;
    }

    /// <summary>Two cards one above the other, with the gap of the design system between them.</summary>
    public static ProportionalSplitContainer Stack(Control top, Control bottom, int topHeight)
    {
        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 12,
            BackColor = UiTheme.Surface
        };
        split.Panel1.BackColor = split.Panel2.BackColor = UiTheme.Surface;
        split.Panel1.Controls.Add(top);
        split.Panel2.Controls.Add(bottom);
        split.SplitterDistance = topHeight;
        return split;
    }

    /// <summary>Two cards side by side.</summary>
    public static ProportionalSplitContainer Beside(Control first, Control second, int firstWidth)
    {
        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 12,
            BackColor = UiTheme.Surface
        };
        split.Panel1.BackColor = split.Panel2.BackColor = UiTheme.Surface;
        split.Panel1.Controls.Add(first);
        split.Panel2.Controls.Add(second);
        split.SplitterDistance = firstWidth;
        return split;
    }

    /// <summary>The content of a list screen sits 20 pixels from the edges of the work area.</summary>
    public static readonly Padding ContentPadding = new(20, 14, 20, 14);
}

/// <summary>
/// The direction-A frame of a dialog: a white header with the title at 17 px and one muted sentence,
/// field captions in semibold, and a footer under a hairline with the dialog's other buttons at the
/// start, then « Annuler » and the primary button at the far end. The dialog keeps its fields, its
/// checks and its Enter / Esc buttons; only their place and look change.
/// </summary>
internal static class StockDialog
{
    private const int FooterHeight = 60;
    private const int ButtonHeight = 34;
    private static readonly Font TitleFont = EntryPaint.Resized(UiTheme.SectionTitle, 12.75f);

    /// <param name="form">The dialog, once its constructor has placed everything.</param>
    /// <param name="sentenceKey">The muted sentence under the title; none when null.</param>
    /// <param name="start">Buttons that go to the start of the footer (refuse, attach, open).</param>
    public static void Frame(Form form, ILocalizer localizer, string? sentenceKey, params Button[] start)
    {
        form.SuspendLayout();
        form.BackColor = UiTheme.SurfaceRaised;
        form.Padding = new Padding(0);

        var header = Header(form, localizer, sentenceKey);
        var docked = form.Controls.Cast<Control>().Any(c => c.Dock == DockStyle.Fill);

        if (docked) FrameDocked(form, localizer, header, start);
        else FrameAbsolute(form, header, start);

        Captions(form);

        // The dialogs are plain forms: they get the icons of the catalogue and the Arabic mirroring
        // here, as the screens get them from their base class.
        ButtonIcons.Apply(form, localizer);
        form.Load += (_, _) => RtlLayout.Apply(form, localizer.CurrentLanguage);

        form.ResumeLayout(performLayout: true);
    }

    private static Panel Header(Form form, ILocalizer localizer, string? sentenceKey)
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = sentenceKey is null ? 54 : 74,
            BackColor = UiTheme.SurfaceRaised,
            Tag = UiTags.OwnLayout
        };

        var title = new Label
        {
            Font = TitleFont, ForeColor = UiTheme.TextPrimary, AutoSize = false, AutoEllipsis = true,
            UseMnemonic = false, Text = form.Text
        };
        var sentence = new Label
        {
            Font = UiTheme.Body, ForeColor = UiTheme.TextSecondary, AutoSize = false, AutoEllipsis = true,
            UseMnemonic = false, Text = sentenceKey is null ? string.Empty : localizer[sentenceKey],
            Visible = sentenceKey is not null
        };

        void Place()
        {
            var width = Math.Max(40, header.ClientSize.Width - 40);
            title.Bounds = new Rectangle(20, 16, width, 26);
            sentence.Bounds = new Rectangle(20, 44, width, 20);
        }

        header.Resize += (_, _) => Place();
        form.TextChanged += (_, _) => title.Text = form.Text;
        header.Controls.AddRange([title, sentence]);
        Place();
        return header;
    }

    private static int Width(Button button) =>
        TextRenderer.MeasureText(Mnemonics.Plain(button.Text), button.Font).Width + 22
        + button.Padding.Horizontal + 30;

    private static (Button? Primary, Button? Cancel, List<Button> Others) Sort(IEnumerable<Button> buttons, Form form)
    {
        var list = buttons.Distinct().ToList();
        var cancel = form.CancelButton is Button c && list.Contains(c) ? c : null;
        var primary = form.AcceptButton is Button a && list.Contains(a)
            ? a
            : list.FirstOrDefault(b => b != cancel && b.BackColor.ToArgb() == UiTheme.Accent.ToArgb());
        return (primary, cancel, list.Where(b => b != primary && b != cancel).ToList());
    }

    // Others at the start; cancel, then the primary at the far end.
    private static void PlaceFooter(int width, int top, Button? primary, Button? cancel, IEnumerable<Button> others)
    {
        var y = top + (FooterHeight - ButtonHeight) / 2;

        var x = 20;
        foreach (var button in others)
        {
            button.Bounds = new Rectangle(x, y, Width(button), ButtonHeight);
            x = button.Right + 8;
        }

        var right = width - 20;
        if (primary is not null)
        {
            var w = Width(primary);
            primary.Bounds = new Rectangle(right - w, y, w, ButtonHeight);
            right = primary.Left - 8;
        }

        if (cancel is not null)
        {
            var w = Width(cancel);
            cancel.Bounds = new Rectangle(right - w, y, w, ButtonHeight);
        }
    }

    private static void FrameAbsolute(Form form, Panel header, Button[] start)
    {
        var children = form.Controls.Cast<Control>().ToList();
        var accept = form.AcceptButton as Button;
        var cancel = form.CancelButton as Button;
        var rowTop = accept?.Top ?? cancel?.Top;

        var footer = children.OfType<Button>()
            .Where(b => b == accept || b == cancel || start.Contains(b) || rowTop is { } t && b.Top == t)
            .ToList();
        footer.AddRange(start.Where(b => !footer.Contains(b)));

        var content = children.Where(c => c is not Button b || !footer.Contains(b)).ToList();
        foreach (var control in content.Where(c => c.Dock == DockStyle.None)) control.Top += header.Height;

        var bottom = content.Where(c => c.Dock == DockStyle.None).Select(c => c.Bottom).DefaultIfEmpty(header.Height).Max();
        var footerTop = bottom + 16;

        var (primary, dismiss, others) = Sort(footer, form);

        // Wide enough for the footer's buttons side by side, with their icons.
        var needed = 40 + footer.Sum(Width) + 8 * Math.Max(0, footer.Count - 1)
                     + (others.Count > 0 && (primary ?? dismiss) is not null ? 16 : 0);
        form.ClientSize = new Size(Math.Max(form.ClientSize.Width, needed), footerTop + FooterHeight);
        foreach (var button in start.Where(b => b.Parent != form)) form.Controls.Add(button);
        form.Controls.Add(header);

        foreach (var button in footer) button.Height = ButtonHeight;
        PlaceFooter(form.ClientSize.Width, footerTop, primary, dismiss, others);

        // The hairline over the footer follows the buttons, which the editors' fit-to-screen may move.
        var anchor = dismiss ?? primary ?? footer.FirstOrDefault();
        form.Paint += (_, e) =>
        {
            if (anchor is null) return;
            var y = anchor.Top - (FooterHeight - ButtonHeight) / 2;
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, 0, y, form.ClientSize.Width, y);
        };
    }

    private static void FrameDocked(Form form, ILocalizer localizer, Panel header, Button[] start)
    {
        var bar = form.Controls.OfType<Panel>()
            .FirstOrDefault(p => p.Dock == DockStyle.Bottom && p.Controls.OfType<Button>().Any());

        if (bar is null)
        {
            bar = new Panel { Dock = DockStyle.Bottom };
            if (form.CancelButton is null)
            {
                var close = UiTheme.SecondaryButton(localizer["Action.Close"]);
                close.DialogResult = DialogResult.Cancel;
                bar.Controls.Add(close);
                form.CancelButton = close;
            }
        }
        else
        {
            form.Controls.Remove(bar);
        }

        foreach (var button in start.Where(b => b.Parent != bar)) bar.Controls.Add(button);

        bar.Height = FooterHeight;
        bar.BackColor = UiTheme.SurfaceRaised;
        bar.Tag = UiTags.OwnLayout;
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0);
        };

        var (primary, cancel, others) = Sort(bar.Controls.OfType<Button>(), form);
        others = [.. start.Where(others.Contains), .. others.Where(b => !start.Contains(b))];
        void Place() => PlaceFooter(bar.ClientSize.Width, 0, primary, cancel, others);
        bar.Resize += (_, _) => Place();

        // Docked last, so the footer and the header take the edges before the grids fill the rest.
        form.Controls.Add(bar);
        form.Controls.Add(header);
        Place();
    }

    // The captions above the fields: 11 px semibold in ink-muted, as on the rebuilt screens.
    private static void Captions(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is DataGridView) continue;

            if (child is Label { AutoSize: true } label && label.Font.Equals(UiTheme.Small)
                && label.ForeColor.ToArgb() == UiTheme.TextSecondary.ToArgb())
                label.Font = UiTheme.SmallBold;

            Captions(child);
        }
    }
}
