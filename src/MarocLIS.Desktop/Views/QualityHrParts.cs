using System.Drawing.Drawing2D;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

// The pieces the quality and personnel screens are laid out with, in the "LIS · A" family of the
// patient list (P3): a page title with its count and the actions on the right, a filter row, white
// cards holding the grids, an aside with the selected item's facts and actions, and the dialogs'
// title, sentence and footer. Kept beside these screens rather than in the shared theme, which
// other screens are restyled against at the same time.

/// <summary>The colour pairs of the design system's status pills: soft fill, matching ink.</summary>
internal static class QhTone
{
    public static readonly (Color Back, Color Fore) Ok = (UiTheme.NormalBackground, UiTheme.Normal);
    public static readonly (Color Back, Color Fore) Warn = (UiTheme.AbnormalBackground, UiTheme.Abnormal);
    public static readonly (Color Back, Color Fore) Crit = (UiTheme.CriticalBackground, UiTheme.CriticalInk);
    public static readonly (Color Back, Color Fore) Info = (UiTheme.InfoBackground, UiTheme.Info);
    public static readonly (Color Back, Color Fore) Wait = (UiTheme.SurfaceSunken, UiTheme.TextPrimary);
    public static readonly (Color Back, Color Fore) Muted = (UiTheme.SurfaceSunken, UiTheme.TextMuted);
}

/// <summary>Builders for the page frame of a list screen.</summary>
internal static class QhPage
{
    /// <summary>The 17-pixel semibold of a dialog's title and of the aside's heading.</summary>
    public static readonly Font Heading17 = EntryPaint.Resized(UiTheme.BodyBold, 12.75f);

    /// <summary>The padding of the work surface around a page: 20 px top, 24 px either side.</summary>
    public static readonly Padding PagePadding = new(24, 18, 24, 14);

    /// <summary>
    /// The page's frame: one column, a row per part, the part given <paramref name="fill"/> taking
    /// what is left of the height. Laid out by the table, so it mirrors itself in Arabic.
    /// </summary>
    public static TableLayoutPanel Frame(int fill, params Control[] rows)
    {
        var frame = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = rows.Length,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiTheme.Surface
        };
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        for (var i = 0; i < rows.Length; i++)
        {
            frame.RowStyles.Add(i == fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            rows[i].Dock = DockStyle.Fill;
            frame.Controls.Add(rows[i], 0, i);
        }

        return frame;
    }

    /// <summary>Styles a label as the page's title (22 px semibold).</summary>
    public static Label Title(Label label)
    {
        label.Font = UiTheme.PageTitle;
        label.ForeColor = UiTheme.TextPrimary;
        label.AutoSize = true;
        label.Margin = new Padding(0, 0, 12, 0);
        label.Anchor = AnchorStyles.Left;
        return label;
    }

    /// <summary>Styles a label as the muted count beside the title.</summary>
    public static Label Count(Label label)
    {
        label.Font = UiTheme.Body;
        label.ForeColor = UiTheme.TextSecondary;
        label.AutoSize = true;
        label.Margin = new Padding(0, 9, 12, 0);
        label.Anchor = AnchorStyles.Left;
        return label;
    }

    /// <summary>
    /// Title and count on the left, the actions on the right, in the order given (the primary last,
    /// at the far end).
    /// </summary>
    public static TableLayoutPanel Header(Label title, Label? count, params Control[] actions)
    {
        var header = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 12),
            Padding = Padding.Empty,
            BackColor = UiTheme.Surface
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        header.Controls.Add(Title(title), 0, 0);
        if (count is not null) header.Controls.Add(Count(count), 1, 0);

        var bar = Actions(actions);
        bar.Anchor = AnchorStyles.Right;
        header.Controls.Add(bar, 2, 0);

        return header;
    }

    /// <summary>A row of buttons sized to their captions, 34 px high, 8 px apart.</summary>
    public static FlowLayoutPanel Actions(params Control[] actions)
    {
        var bar = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiTheme.Surface
        };

        foreach (var action in actions)
        {
            if (action is Button button) Fit(button);
            action.Margin = new Padding(8, 0, 0, 0);
            bar.Controls.Add(action);
        }

        return bar;
    }

    /// <summary>A button sized to its caption and its icon, on the 34-pixel line of the design system.</summary>
    public static T Fit<T>(T button) where T : Button
    {
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(0, 34);
        button.Height = 34;
        if (button.Padding.Left < 10) button.Padding = new Padding(10, 0, 10, 0);
        return button;
    }

    /// <summary>
    /// The filter row under the title: the search box, the chips, the drop-down filters. Wraps onto a
    /// second line on a narrow window rather than pushing a filter out of sight.
    /// </summary>
    public static FlowLayoutPanel Toolbar(params Control[] items)
    {
        var bar = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 0, 0, 12),
            Padding = Padding.Empty,
            BackColor = UiTheme.Surface
        };

        foreach (var item in items)
        {
            if (item.Margin == new Padding(3)) item.Margin = new Padding(0, 0, 8, 0);
            bar.Controls.Add(item);
        }

        return bar;
    }

    /// <summary>A small muted caption placed before a filter in the toolbar ("Année").</summary>
    public static Label Caption(Label label)
    {
        label.Font = UiTheme.SmallBold;
        label.ForeColor = UiTheme.TextSecondary;
        label.AutoSize = true;
        label.Margin = new Padding(4, 9, 6, 0);
        return label;
    }

    /// <summary>A drop-down filter on the toolbar's line.</summary>
    public static T Filter<T>(T control, int width) where T : Control
    {
        control.Width = width;
        control.Margin = new Padding(0, 4, 8, 0);
        return control;
    }

    /// <summary>A white card with the line border, holding <paramref name="content"/> (a grid) edge to edge.</summary>
    public static Panel Card(Control content)
    {
        var card = new Panel
        {
            BackColor = UiTheme.SurfaceRaised,
            Padding = new Padding(1),
            Margin = Padding.Empty
        };
        content.Dock = DockStyle.Fill;
        card.Controls.Add(content);
        return UiChrome.AsCard(card);
    }

    /// <summary>
    /// A white card with a heading line (title, muted count, actions at the far end) above its
    /// content.
    /// </summary>
    public static Panel TitledCard(Label title, Label? count, Control content, params Control[] actions)
    {
        var heading = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            Padding = new Padding(14, 10, 14, 10),
            BackColor = UiTheme.SurfaceRaised
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        title.Font = UiTheme.SectionTitle;
        title.ForeColor = UiTheme.TextPrimary;
        title.AutoSize = true;
        title.Anchor = AnchorStyles.Left;
        title.Margin = new Padding(0, 4, 10, 4);
        heading.Controls.Add(title, 0, 0);

        if (count is not null)
        {
            count.Font = UiTheme.Small;
            count.ForeColor = UiTheme.TextSecondary;
            count.AutoSize = true;
            count.Anchor = AnchorStyles.Left;
            count.Margin = new Padding(0, 6, 10, 4);
            heading.Controls.Add(count, 1, 0);
        }

        if (actions.Length > 0)
        {
            var bar = Actions(actions);
            bar.BackColor = UiTheme.SurfaceRaised;
            bar.Anchor = AnchorStyles.Right;
            heading.Controls.Add(bar, 2, 0);
        }

        var card = new Panel
        {
            BackColor = UiTheme.SurfaceRaised,
            Padding = new Padding(1),
            Margin = Padding.Empty
        };

        // The content first, so the heading docked above it is laid out before it.
        content.Dock = DockStyle.Fill;
        card.Controls.Add(content);
        card.Controls.Add(new QhRule { Dock = DockStyle.Top });
        card.Controls.Add(heading);
        return UiChrome.AsCard(card);
    }

    /// <summary>
    /// The card and its aside side by side: the card takes the width that is left, the aside
    /// <paramref name="asideWidth"/> pixels, 16 apart.
    /// </summary>
    public static TableLayoutPanel WithAside(Control main, Control aside, int asideWidth = 320)
    {
        var body = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiTheme.Surface
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, asideWidth + 16));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        main.Dock = DockStyle.Fill;
        main.Margin = Padding.Empty;
        aside.Dock = DockStyle.Fill;
        aside.Margin = new Padding(16, 0, 0, 0);
        body.Controls.Add(main, 0, 0);
        body.Controls.Add(aside, 1, 0);
        return body;
    }

    /// <summary>A line of feedback under the page ("Enregistré", a refusal): muted, small, on one line.</summary>
    public static Label StatusLine(Label status)
    {
        status.AutoSize = false;
        status.AutoEllipsis = true;
        status.Height = 22;
        status.Font = UiTheme.Small;
        if (status.ForeColor == Control.DefaultForeColor) status.ForeColor = UiTheme.TextSecondary;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.Margin = new Padding(0, 6, 0, 0);
        return status;
    }

    /// <summary>
    /// A tick box shown as a filter chip: outlined when off, accent-soft when on. The box stays the
    /// source of truth — the screen reads and handles it as before — and is kept out of sight.
    /// </summary>
    public static ChipButton Chip(CheckBox box)
    {
        var chip = new ChipButton { Text = box.Text, Selected = box.Checked };
        chip.Height = 30;
        chip.Margin = new Padding(0, 2, 8, 0);
        chip.Click += (_, _) => box.Checked = !box.Checked;
        box.CheckedChanged += (_, _) => chip.Selected = box.Checked;
        box.TextChanged += (_, _) => chip.Text = box.Text;
        box.Visible = false;
        return chip;
    }

    /// <summary>
    /// The search box of the design system: 34 px, a magnifier inside, the box itself borderless
    /// within. <paramref name="box"/> keeps its handlers and is still the first box of the screen,
    /// so Ctrl+F still lands in it.
    /// </summary>
    public static Panel Search(TextBox box, int width = 340)
    {
        var host = new Panel
        {
            BackColor = UiTheme.SurfaceRaised,
            Size = new Size(width, 34),
            Margin = new Padding(0, 0, 8, 0),
            Tag = UiTags.OwnLayout
        };

        box.BorderStyle = BorderStyle.None;
        box.Font = UiTheme.Body;
        box.BackColor = UiTheme.SurfaceRaised;
        box.ForeColor = UiTheme.TextPrimary;
        host.Controls.Add(box);

        void Place()
        {
            var rtl = host.RightToLeft == RightToLeft.Yes;
            var x = rtl ? 10 : 34;
            box.Bounds = new Rectangle(x, (host.Height - box.PreferredHeight) / 2 + 1,
                                       Math.Max(40, host.Width - 44), box.PreferredHeight);
            host.Invalidate();
        }

        host.Paint += (_, e) =>
        {
            var focused = box.Focused;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var behind = new SolidBrush(host.Parent?.BackColor ?? UiTheme.Surface))
                g.FillRectangle(behind, host.ClientRectangle);
            using (var path = UiChrome.RoundedRectangle(new Rectangle(0, 0, host.Width - 1, host.Height - 1), UiTheme.RadiusControl))
            {
                using (var fill = new SolidBrush(UiTheme.SurfaceRaised)) g.FillPath(fill, path);
                using var pen = new Pen(focused ? UiTheme.Accent : UiTheme.BorderStrong);
                g.DrawPath(pen, path);
            }

            var iconX = host.RightToLeft == RightToLeft.Yes ? host.Width - 26 : 10;
            if (Icons.Render(Glyph.Search, UiTheme.TextSecondary, 16) is { } icon)
                g.DrawImage(icon, iconX, (host.Height - 16) / 2, 16, 16);
        };

        host.Resize += (_, _) => Place();
        host.RightToLeftChanged += (_, _) => Place();
        box.GotFocus += (_, _) => host.Invalidate();
        box.LostFocus += (_, _) => host.Invalidate();
        Place();
        return host;
    }

    /// <summary>
    /// « Plus d'actions ▾ »: a secondary button that drops <paramref name="menu"/> under itself, the
    /// chevron drawn after the caption.
    /// </summary>
    public static Button MoreButton(Button button, ContextMenuStrip menu)
    {
        Fit(button);
        menu.Font = UiTheme.Body;

        void Pad() => button.Padding = button.RightToLeft == RightToLeft.Yes
            ? new Padding(26, 0, 12, 0)
            : new Padding(12, 0, 26, 0);

        Pad();
        button.RightToLeftChanged += (_, _) => Pad();
        button.Paint += (_, e) =>
        {
            var x = button.RightToLeft == RightToLeft.Yes ? 12 : button.Width - 20;
            var y = button.Height / 2 - 2;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(button.Enabled ? UiTheme.TextPrimary : UiTheme.TextMuted, 1.6f);
            e.Graphics.DrawLines(pen, [new Point(x, y), new Point(x + 4, y + 4), new Point(x + 8, y)]);
        };
        button.Click += (_, _) =>
        {
            var at = button.RightToLeft == RightToLeft.Yes
                ? new Point(button.Width, button.Height)
                : new Point(0, button.Height);
            menu.Show(button, at);
        };
        return button;
    }
}

/// <summary>
/// The foot of a card: what is known of the selected line on the start side, its actions on the
/// end side with the main one at the far end, a line above. Laid out here rather than by a table:
/// the actions keep their natural width, wrap onto a further line on a narrow window, and the
/// whole is mirrored in Arabic.
/// </summary>
internal sealed class QhSelectionBar : Panel
{
    private const int Gap = 8;
    private readonly Label? _detail;
    private readonly List<Control> _actions = [];
    private bool _placing;

    public QhSelectionBar(Label? detail, params Control[] actions)
    {
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        Dock = DockStyle.Bottom;
        Margin = Padding.Empty;
        Padding = new Padding(14, 10, 14, 10);
        BackColor = UiTheme.SurfaceRaised;
        Tag = UiTags.OwnLayout;
        Height = 54;

        _detail = detail;
        if (detail is not null)
        {
            detail.AutoSize = false;
            detail.AutoEllipsis = true;
            detail.Font = UiTheme.Small;
            if (detail.ForeColor == DefaultForeColor) detail.ForeColor = UiTheme.TextSecondary;
            detail.TextAlign = ContentAlignment.MiddleLeft;
            detail.TextChanged += (_, _) => Place();
            Controls.Add(detail);
        }

        foreach (var action in actions)
        {
            if (action is Button button) QhPage.Fit(button);
            action.SizeChanged += (_, _) => Place();
            action.VisibleChanged += (_, _) => Place();
            _actions.Add(action);
            Controls.Add(action);
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        Place();
    }

    private void Place()
    {
        if (_placing || Width <= 0) return;
        _placing = true;
        try
        {
            var rtl = RightToLeft == RightToLeft.Yes;
            var inner = new Rectangle(Padding.Left, Padding.Top, Width - Padding.Horizontal, 0);
            var shown = _actions.Where(a => a.Visible || !IsHandleCreated).ToList();

            // The actions from the far end inwards, the last given at the far end.
            var lines = new List<List<Control>> { new() };
            var used = 0;
            var room = _detail is null ? inner.Width : Math.Max(160, inner.Width * 3 / 4);
            foreach (var action in Enumerable.Reverse(shown))
            {
                var width = action.Width;
                if (lines[^1].Count > 0 && used + Gap + width > room)
                {
                    lines.Add([]);
                    used = 0;
                }
                used += (lines[^1].Count > 0 ? Gap : 0) + width;
                lines[^1].Add(action);
            }

            var y = inner.Top;
            var widest = 0;
            foreach (var line in lines)
            {
                var x = inner.Right;
                var height = 0;
                foreach (var action in line)
                {
                    x -= action.Width;
                    var bounds = new Rectangle(x, y, action.Width, action.Height);
                    action.Location = rtl ? new Point(Width - bounds.Right, y) : bounds.Location;
                    x -= Gap;
                    height = Math.Max(height, action.Height);
                }
                widest = Math.Max(widest, inner.Right - x - Gap);
                if (line.Count > 0) y += height + 6;
            }

            var actionsHeight = Math.Max(0, y - inner.Top - 6);

            if (_detail is not null)
            {
                var width = Math.Max(80, inner.Width - (lines.Count == 1 ? widest + 16 : 0));
                var textHeight = string.IsNullOrEmpty(_detail.Text)
                    ? 0
                    : TextRenderer.MeasureText(_detail.Text, _detail.Font, new Size(width, 0),
                                               TextFormatFlags.WordBreak).Height;
                var detailTop = lines.Count == 1 ? inner.Top : inner.Top + actionsHeight + 6;
                var detailHeight = Math.Min(Math.Max(textHeight, Math.Min(34, Math.Max(actionsHeight, 18))), 48);
                if (lines.Count == 1) detailHeight = Math.Max(detailHeight, actionsHeight);
                var bounds = new Rectangle(inner.Left, detailTop, width, detailHeight);
                _detail.Bounds = rtl ? new Rectangle(Width - bounds.Right, bounds.Top, bounds.Width, bounds.Height) : bounds;
                actionsHeight = Math.Max(actionsHeight, _detail.Bottom - inner.Top);
            }

            var wanted = Padding.Vertical + Math.Max(actionsHeight, 20);
            if (Height != wanted) Height = wanted;
        }
        finally
        {
            _placing = false;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(UiTheme.Border);
        e.Graphics.DrawLine(pen, 0, 0, Width, 0);
    }
}

/// <summary>
/// The entries of a « Plus d'actions » menu that stand for buttons of the screen. The button keeps
/// its caption, its state and its permission gate — the screen goes on driving it — and the entry
/// follows it: same caption, same availability, same icon.
/// </summary>
internal static class QhMenu
{
    public static ToolStripMenuItem For(Button button, EventHandler click)
    {
        var item = new ToolStripMenuItem(button.Text) { Enabled = button.Enabled };
        button.TextChanged += (_, _) => item.Text = button.Text;
        button.EnabledChanged += (_, _) => item.Enabled = button.Enabled;
        item.Click += click;
        item.Paint += (_, _) =>
        {
            if (item.Image != button.Image) item.Image = button.Image;
        };
        return item;
    }

    /// <summary>
    /// A place for the buttons the menu stands for: in the screen, so the icons, the mnemonics and
    /// Arabic reach them, but never shown.
    /// </summary>
    public static Panel Holder(params Button[] buttons)
    {
        var holder = new Panel { Visible = false, Size = Size.Empty, Margin = Padding.Empty, Tag = UiTags.OwnLayout };
        holder.Controls.AddRange(buttons);
        return holder;
    }
}

/// <summary>A one-pixel line in the design system's border colour: under a card's heading, above a footer.</summary>
internal sealed class QhRule : Control
{
    public QhRule()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Height = 1;
        TabStop = false;
        BackColor = UiTheme.Border;
    }
}

/// <summary>
/// The aside of a list screen: the selected item's name and status, its key facts as label and
/// value, its actions, and what the last action said. A white card, laid out by a table so it
/// mirrors itself in Arabic.
/// </summary>
internal sealed class QhAside : Panel
{
    private readonly TableLayoutPanel _layout = new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        Margin = Padding.Empty,
        Padding = Padding.Empty,
        BackColor = UiTheme.SurfaceRaised,
        AutoScroll = true
    };

    private readonly TableLayoutPanel _facts = new()
    {
        ColumnCount = 2,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Margin = new Padding(0, 0, 0, 12),
        Padding = Padding.Empty,
        BackColor = UiTheme.SurfaceRaised,
        Dock = DockStyle.Top
    };

    public Label Heading { get; } = new()
    {
        Font = QhPage.Heading17,
        ForeColor = UiTheme.TextPrimary,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 2)
    };

    public Label Subtitle { get; } = new()
    {
        Font = UiTheme.Body,
        ForeColor = UiTheme.TextSecondary,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 8)
    };

    public PillLabel Pill { get; } = new() { Margin = new Padding(0, 0, 0, 12), Visible = false };

    /// <summary>A sentence under the facts: a description, a warning.</summary>
    public Label Note { get; } = new()
    {
        Font = UiTheme.Body,
        ForeColor = UiTheme.TextSecondary,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 12),
        Visible = false
    };

    public FlowLayoutPanel ActionsPanel { get; } = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = true,
        FlowDirection = FlowDirection.LeftToRight,
        Margin = new Padding(0, 0, 0, 8),
        Padding = Padding.Empty,
        BackColor = UiTheme.SurfaceRaised,
        Dock = DockStyle.Top
    };

    public QhAside()
    {
        BackColor = UiTheme.SurfaceRaised;
        Padding = new Padding(16, 14, 10, 12);
        UiChrome.AsCard(this);

        _facts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        _facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var part in new Control[] { Heading, Subtitle, Pill, _facts, Note, ActionsPanel })
        {
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _layout.Controls.Add(part);
        }

        // Scrolls up and down when the item has more to say than the aside holds, never sideways.
        _layout.AutoScroll = false;
        _layout.HorizontalScroll.Enabled = false;
        _layout.HorizontalScroll.Visible = false;
        _layout.HorizontalScroll.Maximum = 0;
        _layout.AutoScroll = true;

        _layout.Resize += (_, _) => Wrap();
        Controls.Add(_layout);
    }

    /// <summary>The actions of the selected item, in the order given; they wrap within the aside.</summary>
    public void SetActions(params Control[] actions)
    {
        foreach (var action in actions)
        {
            if (action is Button button) QhPage.Fit(button);
            action.Margin = new Padding(0, 0, 8, 8);
            ActionsPanel.Controls.Add(action);
        }
    }

    /// <summary>Adds a control under the actions (the status line, a second list).</summary>
    public void AddBelow(Control control)
    {
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(0, 4, 0, 0);
        _layout.Controls.Add(control);
        Wrap();
    }

    /// <summary>Shows the item: its name, a line under it, a status pill and its facts.</summary>
    public void Show(string heading, string? subtitle, (string Text, (Color Back, Color Fore) Tone)? pill,
                     IEnumerable<(string Label, string Value, bool Mono)> facts, string? note = null,
                     Color? noteColour = null)
    {
        SuspendLayout();
        Heading.Text = heading;
        Heading.ForeColor = UiTheme.TextPrimary;
        Subtitle.Text = subtitle ?? string.Empty;
        Subtitle.Visible = !string.IsNullOrEmpty(subtitle);

        if (pill is { } p) Pill.Show(p.Text, p.Tone);
        else Pill.Visible = false;

        _facts.SuspendLayout();
        foreach (Control old in _facts.Controls.Cast<Control>().ToList())
        {
            _facts.Controls.Remove(old);
            old.Dispose();
        }
        _facts.RowStyles.Clear();

        var row = 0;
        foreach (var (label, value, mono) in facts)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            _facts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _facts.Controls.Add(new Label
            {
                Text = label, AutoSize = true, Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary,
                Margin = new Padding(0, 3, 8, 3), RightToLeft = RightToLeft
            }, 0, row);
            _facts.Controls.Add(new Label
            {
                Text = value, AutoSize = true, Font = mono ? UiTheme.Monospace : UiTheme.Body,
                ForeColor = UiTheme.TextPrimary, Margin = new Padding(0, 3, 0, 3), RightToLeft = RightToLeft,
                UseMnemonic = false
            }, 1, row);
            row++;
        }
        _facts.RowCount = Math.Max(1, row);
        _facts.Visible = row > 0;
        _facts.ResumeLayout(true);

        Note.Text = note ?? string.Empty;
        Note.ForeColor = noteColour ?? UiTheme.TextSecondary;
        Note.Visible = !string.IsNullOrWhiteSpace(note);

        Wrap();
        ResumeLayout(true);
    }

    /// <summary>Empties the aside: <paramref name="heading"/> alone, muted.</summary>
    public void Clear(string heading)
    {
        Show(heading, null, null, []);
        Heading.ForeColor = UiTheme.TextMuted;
    }

    // Long texts wrap at the aside's width rather than running under its border.
    private void Wrap()
    {
        var width = Math.Max(120, _layout.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
        foreach (var label in new[] { Heading, Subtitle, Note })
            label.MaximumSize = new Size(width, 0);
        foreach (Label value in _facts.Controls.OfType<Label>())
            value.MaximumSize = new Size(_facts.GetColumn(value) == 0 ? 108 : Math.Max(80, width - 116), 0);
        ActionsPanel.MaximumSize = new Size(width, 0);
    }
}

/// <summary>
/// Draws one column of a grid as status pills: the cell's own text, in the colours the screen gives
/// for its row. The value and the printed list are unchanged; only the look of the cell is.
/// </summary>
internal static class QhGridPills
{
    public static void Attach(DataGridView grid, string column,
                              Func<DataGridViewRow, (Color Back, Color Fore)?> tone)
    {
        grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.Graphics is null) return;
            if (grid.Columns[e.ColumnIndex].Name != column) return;

            var text = e.FormattedValue as string;
            if (string.IsNullOrEmpty(text) || tone(grid.Rows[e.RowIndex]) is not { } colours) return;

            var selected = (e.State & DataGridViewElementStates.Selected) != 0;
            using (var back = new SolidBrush(selected ? e.CellStyle!.SelectionBackColor : e.CellStyle!.BackColor))
                e.Graphics.FillRectangle(back, e.CellBounds);
            e.Paint(e.CellBounds, DataGridViewPaintParts.Border);

            var font = UiTheme.SmallBold;
            var size = EntryPaint.MeasurePill(text, font, 8);
            var width = Math.Min(size.Width, e.CellBounds.Width - 12);
            var rtl = grid.RightToLeft == RightToLeft.Yes;
            var x = rtl ? e.CellBounds.Right - 8 - width : e.CellBounds.Left + 8;
            var bounds = new Rectangle(x, e.CellBounds.Top + (e.CellBounds.Height - size.Height) / 2, width, size.Height);
            EntryPaint.DrawPill(e.Graphics, bounds, text, font, colours, rtl);
            e.Handled = true;
        };
    }
}

/// <summary>
/// The dialogs of these screens in the family's form: a 17-pixel title and one muted sentence at
/// the top, the field captions in 12-pixel semibold, and a footer line with Cancel then the
/// confirming button at the far end. The fields keep their places, moved down under the title; the
/// dialog grows by as much. Arabic mirrors the dialog, as it does the screens.
/// </summary>
internal static class QhDialog
{
    private const int Side = 20;

    /// <summary>
    /// Dresses <paramref name="form"/>. Called at the end of its constructor, once its fields and
    /// buttons are in place. <paramref name="sentenceKey"/> names the sentence under the title.
    /// </summary>
    public static void Dress(Form form, ILocalizer localizer, string? sentenceKey)
    {
        form.SuspendLayout();

        var width = form.ClientSize.Width;
        var sentence = sentenceKey is null ? null : localizer[sentenceKey];

        var title = new Label
        {
            Text = form.Text,
            Font = QhPage.Heading17,
            ForeColor = UiTheme.TextPrimary,
            AutoSize = false,
            AutoEllipsis = true,
            UseMnemonic = false,
            Bounds = new Rectangle(Side, 14, width - 2 * Side, 26)
        };

        var shift = 50;
        Label? line = null;

        if (!string.IsNullOrWhiteSpace(sentence))
        {
            var height = TextRenderer.MeasureText(sentence, UiTheme.Body, new Size(width - 2 * Side, 0),
                                                  TextFormatFlags.WordBreak).Height;
            height = Math.Min(Math.Max(height, 18), 40);
            line = new Label
            {
                Text = sentence,
                Font = UiTheme.Body,
                ForeColor = UiTheme.TextSecondary,
                AutoSize = false,
                AutoEllipsis = true,
                Bounds = new Rectangle(Side, 40, width - 2 * Side, height)
            };
            shift = 40 + height + 14;
        }

        var accept = form.AcceptButton as Button;
        var cancel = form.CancelButton as Button;

        foreach (Control control in form.Controls)
        {
            control.Top += shift;

            // The captions over the fields: 12 px semibold, in the muted ink.
            if (control is Label { AutoSize: true } caption && caption.Font.Equals(UiTheme.Small))
                caption.Font = UiTheme.SmallBold;
        }

        form.Controls.Add(title);
        if (line is not null) form.Controls.Add(line);

        // The footer: the confirming button at the far end, Cancel before it, a line above them.
        var footerButtons = new[] { accept, cancel }.Where(b => b is not null).Cast<Button>().ToList();
        var height2 = form.ClientSize.Height + shift;

        if (footerButtons.Count > 0)
        {
            var top = footerButtons.Min(b => b.Top);

            // Only when the two buttons share the last line: a list dialog keeps its own row.
            if (accept is not null && cancel is not null && accept.Top == cancel.Top)
            {
                var acceptWidth = Width(accept);
                var cancelWidth = Width(cancel);
                accept.Bounds = new Rectangle(width - Side - acceptWidth, top, acceptWidth, 34);
                cancel.Bounds = new Rectangle(accept.Left - 8 - cancelWidth, top, cancelWidth, 34);
                accept.Anchor = cancel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            }

            var rule = new QhRule { Bounds = new Rectangle(0, top - 13, width, 1), BackColor = UiTheme.Border };
            form.Controls.Add(rule);
            height2 = Math.Max(height2, top + 34 + 14);
        }

        form.ClientSize = new Size(width, height2);

        if (localizer.CurrentLanguage.IsRightToLeft())
            RtlLayout.Apply(form, localizer.CurrentLanguage);

        UiChrome.Apply(form);
        form.ResumeLayout(true);
    }

    private static int Width(Button button) =>
        Math.Max(110, TextRenderer.MeasureText(Mnemonics.Plain(button.Text), button.Font).Width + 40);
}
