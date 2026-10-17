using System.Drawing.Drawing2D;
using MarocLIS.Desktop.Controls;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

// The pieces the finance screens (billing, till, claims, tills to sign off, accounting, fixed
// assets, treasury, cost and margin) are laid out with, in the direction A look: a page title with
// its count in grey and the actions at the far end, a filter row of a search box and chips, white
// cards holding the grids, a bar of actions with the primary at the far end, and dialogs with a
// title, a sentence and a footer. Kept beside these screens rather than in the shared theme, which
// other screens are restyled against at the same time; the pills, chips and strips of the result
// entry screen are reused as they are.

/// <summary>Layout helpers of the finance screens.</summary>
internal static class FinanceUi
{
    /// <summary>The height of a control in the design system (control-height).</summary>
    public const int ControlHeight = 34;

    /// <summary>
    /// The page header: the title (screen-title), its count in grey beside it, and the actions at
    /// the far end. A table, so that it mirrors by itself in Arabic.
    /// </summary>
    public static TableLayoutPanel PageHeader(Label title, Label? count, params Control[] actions)
    {
        title.Font = UiTheme.PageTitle;
        title.ForeColor = UiTheme.TextPrimary;
        title.AutoSize = true;
        title.Margin = new Padding(0, 0, 12, 0);
        title.Anchor = AnchorStyles.Left;

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 12),
            BackColor = UiTheme.Surface
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        header.Controls.Add(title, 0, 0);

        if (count is not null)
        {
            Muted(count);
            // Takes what the actions leave and ends in "…" rather than pushing them off the page.
            count.AutoSize = false;
            count.AutoEllipsis = true;
            count.Dock = DockStyle.Fill;
            count.Height = 30;
            count.TextAlign = ContentAlignment.MiddleLeft;
            count.Margin = new Padding(0, 4, 12, 0);
            EllipsisTip.Attach(count);
            header.Controls.Add(count, 1, 0);
        }

        header.Controls.Add(Actions(actions), 2, 0);
        return header;
    }

    /// <summary>Grey text: a count, a summary beside a title.</summary>
    public static Label Muted(Label label)
    {
        label.Font = UiTheme.Body;
        label.ForeColor = UiTheme.TextSecondary;
        return label;
    }

    /// <summary>A row of buttons laid out by their own width, in order, the last at the far end.</summary>
    public static FlowLayoutPanel Actions(params Control[] controls)
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
            Anchor = AnchorStyles.Right,
            BackColor = UiTheme.Surface
        };

        foreach (var control in controls)
        {
            if (control is Button button) Fit(button);
            control.Margin = new Padding(8, 0, 0, 0);
            flow.Controls.Add(control);
        }

        return flow;
    }

    /// <summary>A button as tall as a field and as wide as its caption and icon.</summary>
    public static Button Fit(Button button)
    {
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(0, ControlHeight);
        button.Height = ControlHeight;
        if (button.Padding.Horizontal < 20) button.Padding = new Padding(10, 0, 10, 0);
        return button;
    }

    /// <summary>
    /// The filter row under the header: the search box, the chips, the period fields. Wraps onto
    /// a second line on a narrow window rather than hiding a filter.
    /// </summary>
    public static FlowLayoutPanel FilterRow(params Control[] items)
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 8),
            BackColor = UiTheme.Surface
        };

        foreach (var item in items)
        {
            if (item is Button button and not ChipButton) Fit(button);
            item.Margin = item is ChipButton ? new Padding(0, 4, 6, 4) : new Padding(0, 0, 10, 4);
            flow.Controls.Add(item);
        }

        return flow;
    }

    /// <summary>A caption and its field side by side, for a filter row: « Du [date] ».</summary>
    public static FlowLayoutPanel Inline(Label caption, Control field)
    {
        caption.Font = UiTheme.SmallBold;
        caption.ForeColor = UiTheme.TextSecondary;
        caption.AutoSize = true;
        caption.Margin = new Padding(0, 9, 6, 0);
        field.Margin = new Padding(0, 4, 0, 0);

        var flow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
            BackColor = UiTheme.Surface
        };
        flow.Controls.Add(caption);
        flow.Controls.Add(field);
        return flow;
    }

    /// <summary>A caption above its field, for a form laid out in a card.</summary>
    public static TableLayoutPanel Stacked(Label caption, Control field)
    {
        caption.Font = UiTheme.SmallBold;
        caption.ForeColor = UiTheme.TextSecondary;
        caption.AutoSize = true;
        caption.Margin = new Padding(0, 0, 0, 4);
        field.Margin = new Padding(0);

        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 16, 12),
            BackColor = UiTheme.SurfaceRaised
        };
        table.Controls.Add(caption, 0, 0);
        table.Controls.Add(field, 0, 1);
        return table;
    }

    /// <summary>
    /// A card's head: its title (heading), a grey count beside it, and the card's own actions at
    /// the far end.
    /// </summary>
    public static TableLayoutPanel CardHeader(Label title, Label? count, params Control[] actions)
    {
        title.Font = UiTheme.SectionTitle;
        if (title.ForeColor == SystemColors.ControlText || title.ForeColor == Color.Empty) title.ForeColor = UiTheme.TextPrimary;
        title.AutoSize = true;
        title.Margin = new Padding(0, 4, 10, 0);

        var titles = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        titles.Controls.Add(title);

        if (count is not null)
        {
            Muted(count);
            count.AutoSize = true;
            count.Margin = new Padding(0, 7, 0, 0);
            titles.Controls.Add(count);
        }

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(16, 10, 16, 10),
            BackColor = UiTheme.SurfaceRaised
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(titles, 0, 0);

        var buttons = Actions(actions);
        buttons.BackColor = UiTheme.SurfaceRaised;
        header.Controls.Add(buttons, 1, 0);
        return header;
    }

    /// <summary>
    /// The bar of actions at the foot of a card or a screen: the secondary actions first, the
    /// status line in the middle, the primary action at the far end.
    /// </summary>
    public static EntryStrip ActionBar(Label? status, Control[] start, params Control[] end)
    {
        var strip = new EntryStrip { Dock = DockStyle.Bottom, Height = 56, BackColor = UiTheme.SurfaceRaised };

        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = start.Length + 1 + end.Length,
            RowCount = 1,
            Padding = new Padding(16, 10, 16, 10),
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var column = 0;

        foreach (var control in start)
        {
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            if (control is Button button) Fit(button);
            control.Margin = new Padding(0, 0, 8, 0);
            bar.Controls.Add(control, column++, 0);
        }

        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        if (status is not null)
        {
            Status(status);
            status.Margin = new Padding(8, 0, 12, 0);
            bar.Controls.Add(status, column, 0);
        }
        else
        {
            bar.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = UiTheme.SurfaceRaised }, column, 0);
        }

        column++;

        foreach (var control in end)
        {
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            if (control is Button button) Fit(button);
            control.Margin = new Padding(8, 0, 0, 0);
            bar.Controls.Add(control, column++, 0);
        }

        strip.Controls.Add(bar);
        return strip;
    }

    private static readonly ToolTip StatusTips = new() { AutoPopDelay = 20_000 };

    /// <summary>
    /// A one-line status: small, grey unless the screen colours it, ending in "…" with the whole
    /// message on hover when it is longer than the room it has.
    /// </summary>
    public static Label Status(Label status)
    {
        status.AutoSize = false;
        status.AutoEllipsis = true;
        status.Dock = DockStyle.Fill;
        status.Font = UiTheme.Small;
        if (status.ForeColor == SystemColors.ControlText) status.ForeColor = UiTheme.TextSecondary;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.TextChanged -= TipStatus;
        status.TextChanged += TipStatus;
        return status;
    }

    private static void TipStatus(object? sender, EventArgs e)
    {
        if (sender is Label label) StatusTips.SetToolTip(label, label.Text);
    }

    /// <summary>
    /// The button of « Plus d'actions ▾ »: a secondary button with a chevron drawn after its
    /// caption, that drops <paramref name="menu"/> with one entry per action. The entries follow
    /// their action's caption, state and right each time the menu opens.
    /// </summary>
    public static Button MoreButton(ContextMenuStrip menu, ILocalizer localizer, params MenuAction[] actions)
    {
        var more = UiTheme.SecondaryButton(string.Empty);
        // Translated by itself on a language change: not every screen re-captions it.
        LocalizedCaptions.Bind(more, localizer, "ResultEntry.MoreActions");
        more.Tag = UiTags.NoMnemonic;
        more.UseMnemonic = false;
        Fit(more);

        void Pad() => more.Padding = more.RightToLeft == RightToLeft.Yes
            ? new Padding(26, 0, 12, 0)
            : new Padding(12, 0, 26, 0);

        Pad();
        more.RightToLeftChanged += (_, _) => Pad();

        more.Paint += (_, e) =>
        {
            var x = more.RightToLeft == RightToLeft.Yes ? 12 : more.Width - 20;
            var y = more.Height / 2 - 2;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(more.Enabled ? UiTheme.TextPrimary : UiTheme.TextMuted, 1.6f);
            e.Graphics.DrawLines(pen, [new Point(x, y), new Point(x + 4, y + 4), new Point(x + 8, y)]);
        };

        menu.Font = UiTheme.Body;
        menu.ShowItemToolTips = true;

        more.Click += (_, _) =>
        {
            menu.Items.Clear();

            foreach (var action in actions)
            {
                var item = new ToolStripMenuItem(action.Text)
                {
                    Enabled = action.Enabled,
                    Image = IconFor(action.Text, localizer, action.Enabled),
                    ToolTipText = action.Enabled ? null : action.DisabledHint
                };
                item.Click += (_, _) => action.Fire();
                menu.Items.Add(item);
            }

            menu.RightToLeft = more.RightToLeft;
            menu.Show(more, new Point(more.RightToLeft == RightToLeft.Yes ? more.Width : 0, more.Height));
        };

        return more;
    }

    /// <summary>The icon a button with this caption carries elsewhere, for a menu entry.</summary>
    private static Image? IconFor(string text, ILocalizer localizer, bool enabled)
    {
        var plain = Mnemonics.Plain(text).Trim();

        foreach (var (key, glyph) in IconCatalog.Actions)
        {
            if (string.Equals(localizer[key].Trim(), plain, StringComparison.CurrentCultureIgnoreCase))
                return Icons.Render(glyph, enabled ? UiTheme.TextPrimary : UiTheme.TextMuted, 16);
        }

        return null;
    }

    /// <summary>
    /// Draws a grid column's value as a status pill (soft fill, matching ink, a symbol and the
    /// word), as the design system's grids show a status. Rows the style returns nothing for keep
    /// the plain text.
    /// </summary>
    public static void PillColumn(DataGridView grid, string column,
                                  Func<DataGridViewRow, (Color Back, Color Fore, string Glyph)?> style)
    {
        grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.Graphics is null) return;
            if (grid.Columns[e.ColumnIndex].Name != column) return;
            if (style(grid.Rows[e.RowIndex]) is not { } pill) return;

            var text = e.FormattedValue?.ToString() ?? string.Empty;
            if (text.Length == 0) return;
            if (pill.Glyph.Length > 0) text = $"{pill.Glyph} {text}";

            e.PaintBackground(e.ClipBounds, (e.State & DataGridViewElementStates.Selected) != 0);

            var font = UiTheme.SmallBold;
            var size = EntryPaint.MeasurePill(text, font, 8);
            var cell = e.CellBounds;
            var width = Math.Min(size.Width, cell.Width - 12);
            var bounds = new Rectangle(cell.X + 8, cell.Y + (cell.Height - size.Height) / 2, width, size.Height);
            var rtl = grid.RightToLeft == RightToLeft.Yes;

            EntryPaint.DrawPill(e.Graphics, EntryPaint.Mirror(bounds, cell, rtl), text, font, (pill.Back, pill.Fore), rtl);
            e.Handled = true;
        };
    }

    /// <summary>The status pairs of the design system, with the symbol each pill carries.</summary>
    public static readonly (Color Back, Color Fore, string Glyph) PillWaiting = (UiTheme.SurfaceSunken, UiTheme.TextPrimary, "○");
    public static readonly (Color Back, Color Fore, string Glyph) PillRunning = (UiTheme.InfoBackground, UiTheme.Info, "●");
    public static readonly (Color Back, Color Fore, string Glyph) PillDone = (UiTheme.NormalBackground, UiTheme.Normal, "✓");
    public static readonly (Color Back, Color Fore, string Glyph) PillWarning = (UiTheme.AbnormalBackground, UiTheme.Abnormal, "!");
    public static readonly (Color Back, Color Fore, string Glyph) PillCritical = (UiTheme.CriticalBackground, UiTheme.CriticalInk, "!!");

    /// <summary>A grey line of text inside a card: a note, the method of a calculation.</summary>
    public static Label Note(Label label)
    {
        label.Font = UiTheme.Small;
        label.ForeColor = UiTheme.TextSecondary;
        return label;
    }
}

/// <summary>
/// An action reached from « Plus d'actions ▾ » rather than shown as a button. It is still a
/// <see cref="Button"/>, never placed on screen: the screen keeps setting its caption and its
/// state, the permission gate keeps it disabled for a user without the right, and the menu entry
/// reads both each time it opens.
/// </summary>
internal sealed class MenuAction : Button
{
    public MenuAction()
    {
        Visible = false;
    }

    /// <summary>
    /// Why the entry is greyed when it is for good (no right to it): shown on hover over the menu
    /// entry. Null when the entry is only off for the current selection.
    /// </summary>
    public string? DisabledHint { get; set; }

    /// <summary>Runs what a click on the button would, when the action is enabled.</summary>
    public void Fire()
    {
        if (Enabled) OnClick(EventArgs.Empty);
    }
}

/// <summary>
/// A white card holding a grid or a form: an optional head, the body, an optional foot. Drawn with
/// the line border and rounded corners of the design system.
/// </summary>
internal sealed class FinanceCard : Panel
{
    public FinanceCard(Control body, Control? header = null, Control? footer = null)
    {
        BackColor = UiTheme.SurfaceRaised;
        Dock = DockStyle.Fill;
        Margin = new Padding(0);
        // One pixel for the border; a little more where a grid would otherwise run into a corner.
        Padding = new Padding(1, header is null ? 6 : 1, 1, footer is null ? 6 : 1);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        if (header is not null)
        {
            header.Dock = DockStyle.Fill;
            layout.Controls.Add(header, 0, 0);
        }

        body.Dock = DockStyle.Fill;
        body.Margin = new Padding(0);
        layout.Controls.Add(body, 0, 1);

        if (footer is not null)
        {
            footer.Dock = DockStyle.Fill;
            footer.Margin = new Padding(0);
            layout.Controls.Add(footer, 0, 2);
        }

        Controls.Add(layout);
        UiChrome.AsCard(this);
    }
}

/// <summary>
/// A key figure in a card: its caption in grey above, the figure in the kpi size below — the
/// expected cash of a till, the margin of a period.
/// </summary>
internal sealed class FinanceKpiTile : Panel
{
    public Label Caption { get; } = new();
    public Label Value { get; } = new();

    public FinanceKpiTile(Label? value = null, Label? caption = null)
    {
        if (value is not null) Value = value;
        if (caption is not null) Caption = caption;

        BackColor = UiTheme.SurfaceRaised;
        Margin = new Padding(0, 0, 12, 0);
        Padding = new Padding(16, 12, 16, 12);
        Dock = DockStyle.Fill;

        Caption.Font = UiTheme.SmallBold;
        Caption.ForeColor = UiTheme.TextSecondary;
        Caption.Dock = DockStyle.Top;
        Caption.AutoSize = false;
        Caption.Height = 20;
        Caption.AutoEllipsis = true;

        Value.Font = UiTheme.Kpi;
        if (Value.ForeColor == SystemColors.ControlText) Value.ForeColor = UiTheme.TextPrimary;
        Value.Dock = DockStyle.Fill;
        Value.AutoSize = false;
        Value.AutoEllipsis = true;
        Value.TextAlign = ContentAlignment.MiddleLeft;

        Controls.Add(Value);
        Controls.Add(Caption);
        UiChrome.AsCard(this);
    }
}

/// <summary>
/// The search box of a list: a magnifier, then the text, in a field of the design system's
/// height. The box itself is borderless; the field draws the border, in the accent while typing.
/// </summary>
internal sealed class FinanceSearchField : Panel
{
    private readonly TextBox _box;

    public FinanceSearchField(TextBox box, int width)
    {
        _box = box;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint, true);

        Size = new Size(width, FinanceUi.ControlHeight);
        BackColor = UiTheme.SurfaceRaised;
        Cursor = Cursors.IBeam;

        box.BorderStyle = BorderStyle.None;
        box.Font = UiTheme.Body;
        box.BackColor = UiTheme.SurfaceRaised;
        box.Dock = DockStyle.Fill;
        box.Margin = new Padding(0);
        box.GotFocus += (_, _) => Invalidate();
        box.LostFocus += (_, _) => Invalidate();

        Controls.Add(box);
        Pad();
        RightToLeftChanged += (_, _) => Pad();
        Click += (_, _) => box.Focus();
    }

    private void Pad() => Padding = RightToLeft == RightToLeft.Yes
        ? new Padding(10, 9, 34, 4)
        : new Padding(34, 9, 10, 4);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var back = new SolidBrush(Parent?.BackColor ?? UiTheme.Surface))
            g.FillRectangle(back, ClientRectangle);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = UiChrome.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), UiTheme.RadiusControl))
        {
            using (var fill = new SolidBrush(UiTheme.SurfaceRaised)) g.FillPath(fill, path);
            using var pen = new Pen(_box.ContainsFocus ? UiTheme.Accent : UiTheme.BorderStrong);
            g.DrawPath(pen, path);
        }

        if (Icons.Render(Glyph.Search, UiTheme.TextSecondary, 16) is { } icon)
        {
            var x = RightToLeft == RightToLeft.Yes ? Width - 11 - 16 : 11;
            g.DrawImage(icon, x, (Height - 16) / 2, 16, 16);
        }
    }
}

/// <summary>
/// The direction A frame of a finance dialog: the title in 17 px and one grey sentence at the top,
/// the fields below as they were laid out, and a footer bar with the other actions at the start,
/// then « Annuler » and the main action at the far end.
/// <para>
/// The dialog's controls stay where they are in its control tree — moved down, never re-parented —
/// so a dialog that finds a field by name, or an editor that moves its fields into a scrolling body
/// on a small screen, keeps working.
/// </para>
/// </summary>
internal static class FinanceDialogFrame
{
    private const int Margin = 20;
    private const int FooterHeight = 60;
    private const int ButtonGap = 8;

    private static readonly Font TitleFont = EntryPaint.Resized(UiTheme.SectionTitle, 12.75f);

    /// <param name="form">The dialog, its fields already placed.</param>
    /// <param name="sentence">What the dialog does, in one sentence.</param>
    /// <param name="start">Footer actions at the start (an export, an edit).</param>
    /// <param name="cancel">The cancel or close button, before the main action.</param>
    /// <param name="primary">The main action, at the far end; none for a dialog that only shows.</param>
    public static void Apply(Form form, string sentence, Button[] start, Button? cancel, Button? primary)
    {
        form.SuspendLayout();

        var footer = start.Concat(new[] { cancel, primary }.OfType<Button>()).ToList();
        var docked = form.Controls.Cast<Control>().Any(c => c.Dock != DockStyle.None && !footer.Contains(c));

        var width = form.ClientSize.Width;

        var title = new Label
        {
            Text = form.Text,
            Font = TitleFont,
            ForeColor = UiTheme.TextPrimary,
            AutoSize = false,
            AutoEllipsis = true,
            Bounds = new Rectangle(Margin, 16, width - 2 * Margin, 28),
            BackColor = Color.Transparent
        };

        var sentenceHeight = TextRenderer.MeasureText(sentence, UiTheme.Body, new Size(width - 2 * Margin, 0),
                                                      TextFormatFlags.WordBreak).Height;
        var line = new Label
        {
            Text = sentence,
            Font = UiTheme.Body,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(Margin, 46, width - 2 * Margin, sentenceHeight + 2),
            BackColor = Color.Transparent
        };

        var headerBottom = line.Bottom + 14;

        // Field captions in the label style: 12 px semibold, grey.
        foreach (var label in Descendants(form).OfType<Label>())
            if (ReferenceEquals(label.Font, UiTheme.Small) && label.AutoSize) label.Font = UiTheme.SmallBold;

        foreach (var button in footer) SizeButton(button);

        if (docked)
        {
            // The grids and panels fill what is left under the header; the bar of buttons becomes
            // the footer.
            form.Padding = new Padding(0, headerBottom, 0, 0);

            var bar = footer.Select(b => b.Parent).OfType<Panel>().FirstOrDefault();

            if (bar is not null)
            {
                bar.Height = Math.Max(bar.Height, FooterHeight);
                bar.BackColor = UiTheme.Surface;
                bar.Paint += (_, e) =>
                {
                    using var pen = new Pen(UiTheme.Border);
                    e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0);
                };

                // Centred in the bar, or level with where they were when the bar holds a note above them.
                var centred = (bar.Height - FinanceUi.ControlHeight) / 2;
                var was = footer.Max(b => b.Top) - 2;
                var top = was > centred && was + FinanceUi.ControlHeight <= bar.Height - 6 ? was : centred;

                Place(bar.ClientSize.Width, top, start, cancel, primary);
                bar.Resize += (_, _) => Place(bar.ClientSize.Width, top, start, cancel, primary);
            }
        }
        else
        {
            var content = form.Controls.Cast<Control>().Where(c => !footer.Contains(c)).ToList();
            var shift = content.Count == 0 ? 0 : headerBottom - content.Min(c => c.Top);
            foreach (var control in content) control.Top += shift;

            var contentBottom = content.Count == 0 ? headerBottom : content.Max(c => c.Bottom);
            var footerTop = contentBottom + 16;

            // Anchored controls would stretch or slide with the new height: keep where they were put.
            var placed = form.Controls.Cast<Control>().ToDictionary(c => c, c => c.Bounds);
            form.ClientSize = new Size(width, footerTop + FooterHeight);
            foreach (var (control, bounds) in placed) control.Bounds = bounds;
            Place(width, footerTop + (FooterHeight - FinanceUi.ControlHeight) / 2, start, cancel, primary);

            // The footer band, drawn behind the buttons: they stay children of the form.
            form.Paint += (_, e) =>
            {
                var buttons = footer.Where(b => b.Parent == form).ToList();
                if (buttons.Count == 0) return;

                var bandTop = buttons.Min(b => b.Top) - (FooterHeight - FinanceUi.ControlHeight) / 2;
                using (var band = new SolidBrush(UiTheme.Surface))
                    e.Graphics.FillRectangle(band, 0, bandTop, form.ClientSize.Width, form.ClientSize.Height - bandTop);
                using var pen = new Pen(UiTheme.Border);
                e.Graphics.DrawLine(pen, 0, bandTop, form.ClientSize.Width, bandTop);
            };
        }

        form.Controls.Add(title);
        form.Controls.Add(line);

        form.ResumeLayout(performLayout: true);
    }

    /// <summary>For an editor whose Save and Cancel are its AcceptButton and CancelButton.</summary>
    public static void Apply(Form form, string sentence) =>
        Apply(form, sentence, [], form.CancelButton as Button, form.AcceptButton as Button);

    private static void SizeButton(Button button)
    {
        button.AutoSize = false;
        button.Height = FinanceUi.ControlHeight;
        button.Width = Math.Max(110, TextRenderer.MeasureText(button.Text, button.Font).Width + 40);
    }

    private static void Place(int width, int top, Button[] start, Button? cancel, Button? primary)
    {
        var x = Margin;
        foreach (var button in start)
        {
            button.Location = new Point(x, top);
            x += button.Width + ButtonGap;
        }

        var right = width - Margin;
        foreach (var button in new[] { primary, cancel }.OfType<Button>())
        {
            button.Location = new Point(right - button.Width, top);
            right -= button.Width + ButtonGap;
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
