using System.Drawing.Drawing2D;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

// The pieces the administration screens are laid out with — users, audit trail, catalogue,
// instruments, reference data and settings — following the « LIS · A Fluent clair » pages: a page
// title with a muted line, a flat tab strip, rows of filters, white cards holding the grids with
// their actions in the card's head, and dialogs with a title, one sentence and a footer bar.
// Kept beside those screens rather than in the shared theme, which other screens are rebuilt
// against at the same time.

/// <summary>Page, row, card and tab helpers for the administration screens.</summary>
internal static class AdminLayout
{
    /// <summary>The page's margins inside the shell, as on the rebuilt screens.</summary>
    public static readonly Padding Page = new(24, 12, 24, 8);

    /// <summary>Height of a row of buttons or filters: 34 px controls with a little air.</summary>
    public const int RowHeight = 44;

    /// <summary>Space between two cards.</summary>
    public const int Gap = 12;

    public const int ControlHeight = 32;

    /// <summary>
    /// A row: <paramref name="leading"/> at the start, free space, <paramref name="trailing"/> at the
    /// end. A table layout, so it mirrors with the screen in Arabic like the action bars of the
    /// rebuilt screens.
    /// </summary>
    public static TableLayoutPanel Row(int height, IEnumerable<Control> leading, IEnumerable<Control> trailing,
                                       Color? back = null)
    {
        var colour = back ?? UiTheme.Surface;
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = height,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = colour,
            Tag = UiTags.OwnLayout
        };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var column = 0;

        void Put(Control control, ColumnStyle style)
        {
            row.ColumnStyles.Add(style);
            row.Controls.Add(control, column++, 0);
        }

        foreach (var control in leading) Put(control, new ColumnStyle(SizeType.AutoSize));

        // A label, not a panel: a white panel on the work surface is taken for a card.
        Put(new Label { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = colour, Text = string.Empty },
            new ColumnStyle(SizeType.Percent, 100));

        foreach (var control in trailing) Put(control, new ColumnStyle(SizeType.AutoSize));

        row.ColumnCount = column;
        return row;
    }

    /// <summary>A button sized to its caption and icon, 32 px high, in the secondary or primary look.</summary>
    public static T Action<T>(T button, bool primary = false) where T : Button
    {
        if (primary) UiTheme.StylePrimary(button);
        else UiTheme.StyleSecondary(button);

        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(84, ControlHeight);
        button.Padding = new Padding(10, 0, 10, 0);
        button.Margin = new Padding(8, 0, 0, 0);
        button.Anchor = AnchorStyles.None;
        return button;
    }

    /// <summary>Several secondary buttons and, last, the one primary: the order of an action row.</summary>
    public static Control[] Actions(Button? primary, params Button[] secondary)
    {
        var list = new List<Control>();
        foreach (var button in secondary) list.Add(Action(button));
        if (primary is not null) list.Add(Action(primary, primary: true));
        return [.. list];
    }

    /// <summary>The page title, 22 px, with its muted line beside it.</summary>
    public static TableLayoutPanel PageHeader(Label title, Label caption, params Control[] trailing)
    {
        title.Font = UiTheme.PageTitle;
        title.ForeColor = UiTheme.TextPrimary;
        title.AutoSize = true;
        title.Anchor = AnchorStyles.Left;
        title.Margin = new Padding(0, 0, 12, 0);
        title.UseMnemonic = false;

        caption.Font = UiTheme.Body;
        caption.ForeColor = UiTheme.TextSecondary;
        caption.AutoSize = true;
        caption.Anchor = AnchorStyles.Left;
        caption.Margin = new Padding(0, 6, 0, 0);
        caption.UseMnemonic = false;

        var header = Row(48, [title, caption], trailing);
        header.Padding = new Padding(0, 0, 0, 6);
        return header;
    }

    /// <summary>A muted caption: a count, a hint at the end of a row.</summary>
    public static Label Muted(Label label, Font? font = null)
    {
        label.Font = font ?? UiTheme.Body;
        label.ForeColor = UiTheme.TextSecondary;
        label.AutoSize = true;
        label.Anchor = AnchorStyles.Left;
        label.Margin = new Padding(12, 0, 0, 0);
        label.UseMnemonic = false;
        return label;
    }

    /// <summary>A check box laid in a row of filters.</summary>
    public static CheckBox Filter(CheckBox box)
    {
        box.Font = UiTheme.Body;
        box.ForeColor = UiTheme.TextPrimary;
        box.AutoSize = true;
        box.Anchor = AnchorStyles.Left;
        box.Margin = new Padding(12, 0, 0, 0);
        return box;
    }

    /// <summary>A drop-down list laid in a row of filters, 30 px high like the chips beside it.</summary>
    public static ComboBox Filter(ComboBox combo, int width)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.Width = width;
        combo.Anchor = AnchorStyles.Left;
        combo.Margin = new Padding(8, 0, 0, 0);
        return combo;
    }

    /// <summary>A white card with the line border and rounded corners, holding <paramref name="body"/>.</summary>
    public static Panel Card(Control body, Padding? padding = null)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.SurfaceRaised,
            Padding = padding ?? new Padding(1, 1, 1, 6)
        };

        body.Dock = DockStyle.Fill;
        card.Controls.Add(body);
        UiChrome.AsCard(card);
        return card;
    }

    /// <summary>
    /// A card with a head: <paramref name="heading"/> (15 px) at the start, the card's actions at the
    /// end, then <paramref name="body"/> — a grid most often — filling the rest.
    /// </summary>
    public static Panel CardSection(Label heading, Control body, params Control[] actions) =>
        CardSection(heading, body, actions, footer: false);

    /// <summary>
    /// <see cref="CardSection(Label, Control, Control[])"/>, with the actions in a row along the
    /// card's foot when <paramref name="footer"/>: for a narrow card, whose head cannot hold its
    /// title and three buttons. The row wraps rather than hiding a button.
    /// </summary>
    public static Panel CardSection(Label heading, Control body, Control[] actions, bool footer) =>
        footer ? CardSection(heading, body, [], actions) : CardSection(heading, body, actions, []);

    /// <summary>
    /// A card with actions in its head and others along its foot: the head takes the action that
    /// fits beside the title, the foot the rest, so neither row has to wrap.
    /// </summary>
    public static Panel CardSection(Label heading, Control body, Control[] headActions, Control[] footActions)
    {
        heading.Font = UiTheme.SectionTitle;
        heading.ForeColor = UiTheme.TextPrimary;
        heading.AutoSize = true;
        heading.Anchor = AnchorStyles.Left;
        heading.Margin = Padding.Empty;
        heading.UseMnemonic = false;

        var head = Row(RowHeight + 4, [heading], headActions, UiTheme.SurfaceRaised);
        head.Padding = new Padding(15, 0, 11, 0);

        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.SurfaceRaised,
            Padding = new Padding(1, 1, 1, 6)
        };

        body.Dock = DockStyle.Fill;
        card.Controls.Add(body);

        if (footActions.Length > 0)
        {
            var foot = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                BackColor = UiTheme.SurfaceRaised,
                Padding = new Padding(11, 8, 11, 2),
                Tag = UiTags.OwnLayout
            };

            foreach (var action in footActions)
            {
                action.Margin = new Padding(0, 0, 8, 6);
                foot.Controls.Add(action);
            }

            card.Controls.Add(foot);
        }

        card.Controls.Add(head);
        UiChrome.AsCard(card);
        return card;
    }

    /// <summary>Space around a card inside a split panel or a tab, so two cards never touch.</summary>
    public static Panel Spaced(Control content, Padding padding)
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Padding = padding };
        content.Dock = DockStyle.Fill;
        host.Controls.Add(content);
        return host;
    }

    /// <summary>A muted line under a card: the rule a list carries, said once.</summary>
    public static Label Note(Label label, int height = 36)
    {
        label.Font = UiTheme.Small;
        if (label.ForeColor == Control.DefaultForeColor) label.ForeColor = UiTheme.TextSecondary;
        label.AutoSize = false;
        label.Dock = DockStyle.Bottom;
        label.Height = height;
        label.Padding = new Padding(2, 8, 0, 0);
        label.UseMnemonic = false;
        return label;
    }

    /// <summary>
    /// The flat tab strip: the tab control keeps its pages, captions and order, drawn by
    /// <see cref="UiChrome"/> with the accent line under the active tab; it sits in a host that it
    /// fills; a <see cref="FlatTabControl"/> lays its pages over the raised frame Windows draws round
    /// them, so only the strip shows.
    /// </summary>
    public static Panel FlatTabs(TabControl tabs)
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };

        tabs.Dock = DockStyle.Fill;
        tabs.Font = UiTheme.Body;
        tabs.Padding = new Point(16, 7);
        tabs.Appearance = TabAppearance.Normal;

        host.Controls.Add(tabs);

        foreach (TabPage page in tabs.TabPages) page.Padding = new Padding(4, 12, 4, 0);
        tabs.ControlAdded += (_, e) => { if (e.Control is TabPage page) page.Padding = new Padding(4, 12, 4, 0); };

        return host;
    }

    /// <summary>
    /// A status column drawn as a pill from the design system's colour pairs; the pair is chosen per
    /// row by <paramref name="pick"/>, null leaving the cell as text.
    /// </summary>
    public static void PillColumn(DataGridView grid, string column,
                                  Func<DataGridViewRow, (Color Back, Color Fore)?> pick)
    {
        grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.Graphics is null) return;
            if (grid.Columns[e.ColumnIndex].Name != column) return;

            var text = e.FormattedValue?.ToString();
            if (string.IsNullOrEmpty(text)) return;
            if (pick(grid.Rows[e.RowIndex]) is not { } colours) return;

            var rtl = grid.RightToLeft == RightToLeft.Yes;
            e.PaintBackground(e.ClipBounds, (e.State & DataGridViewElementStates.Selected) != 0);

            var font = UiTheme.SmallBold;
            var size = EntryPaint.MeasurePill(text, font, 8);
            var pill = EntryPaint.Mirror(
                new Rectangle(e.CellBounds.X + 8, e.CellBounds.Y + (e.CellBounds.Height - size.Height) / 2,
                              Math.Min(size.Width, e.CellBounds.Width - 14), size.Height),
                e.CellBounds, rtl);
            EntryPaint.DrawPill(e.Graphics, pill, text, font, colours, rtl);
            e.Handled = true;
        };
    }

    /// <summary>The colour pairs of the pills.</summary>
    public static readonly (Color Back, Color Fore) Ok = (UiTheme.NormalBackground, UiTheme.Normal);
    public static readonly (Color Back, Color Fore) Warn = EntryPaint.Warning;
    public static readonly (Color Back, Color Fore) Crit = EntryPaint.Critical;
    public static readonly (Color Back, Color Fore) Run = EntryPaint.Info;
    public static readonly (Color Back, Color Fore) Wait = EntryPaint.Waiting;

    /// <summary>A grid's header band and rows, as the rebuilt lists draw them.</summary>
    public static DataGridView ListGrid(DataGridView grid)
    {
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(12, 6, 12, 6);
        grid.DefaultCellStyle.Padding = new Padding(12, 0, 12, 0);
        grid.BorderStyle = BorderStyle.None;
        return grid;
    }
}

/// <summary>
/// A search box as the design system draws it: a 34 px white field with a line border, the
/// magnifying glass at its start, and a borderless box inside.
/// </summary>
/// <summary>
/// The tab control of the administration screens: its pages cover the few pixels of raised frame
/// Windows draws on the sides and at the bottom, so it can fill its host and still show only the
/// flat strip.
/// </summary>
internal sealed class FlatTabControl : TabControl
{
    private const int Frame = 4;

    public override Rectangle DisplayRectangle
    {
        get
        {
            var area = base.DisplayRectangle;
            return Rectangle.FromLTRB(Math.Max(0, area.Left - Frame), area.Top,
                                      Math.Min(ClientSize.Width, area.Right + Frame),
                                      Math.Min(ClientSize.Height, area.Bottom + Frame));
        }
    }
}

internal sealed class AdminSearchBox : Panel
{
    public TextBox Box { get; }

    public AdminSearchBox(TextBox box, int width)
    {
        Box = box;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = UiTheme.SurfaceRaised;
        Size = new Size(width, 34);
        Anchor = AnchorStyles.Left;
        Margin = Padding.Empty;

        box.BorderStyle = BorderStyle.None;
        box.Font = UiTheme.Body;
        box.BackColor = UiTheme.SurfaceRaised;
        box.ForeColor = UiTheme.TextPrimary;
        Controls.Add(box);

        box.GotFocus += (_, _) => Invalidate();
        box.LostFocus += (_, _) => Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var height = Box.PreferredHeight;
        Box.Bounds = new Rectangle(34, (Height - height) / 2 + 1, Math.Max(40, Width - 44), height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var focused = Box.Focused;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var path = UiChrome.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), UiTheme.RadiusControl))
        using (var pen = new Pen(focused ? UiTheme.Accent : UiTheme.BorderStrong, focused ? 2 : 1))
            g.DrawPath(pen, path);

        if (Icons.Render(Glyph.Search, UiTheme.TextSecondary, 16) is { } icon)
            g.DrawImage(icon, 11, (Height - 16) / 2, 16, 16);
    }
}

/// <summary>
/// The frame every administration dialog gets: white, a 17 px title with one muted sentence above
/// the fields, field captions in semibold, and a footer bar along the bottom with Cancel and then
/// the primary action at the far end — the other buttons of that row at its start.
/// <para>
/// Applied when the dialog loads, after its constructor has placed every field, so each dialog
/// keeps its own layout: the fields only move down by the height of the title. A dialog taller than
/// the screen gets a scrolling body with the footer pinned, as the reference-data editors had.
/// </para>
/// </summary>
internal static class AdminDialogFrame
{
    private const int HeaderWithLead = 70;
    private const int HeaderPlain = 52;
    private const int FooterHeight = 58;

    private static readonly Font TitleFont = new(UiTheme.SectionTitle.FontFamily, 12.75f, UiTheme.SectionTitle.Style);

    /// <summary>Frames <paramref name="form"/> when it loads.</summary>
    /// <param name="title">The title; the window caption when null.</param>
    /// <param name="lead">The muted sentence under the title; none when null or empty.</param>
    public static void Attach(Form form, ILocalizer localizer, string? lead, Func<string>? title = null)
    {
        var done = false;
        form.Load += (_, _) =>
        {
            if (done) return;
            done = true;
            Apply(form, localizer, title?.Invoke() ?? form.Text, lead);
        };
    }

    /// <summary>The lead sentence of a dialog, from <c>AdminDialog.{name}</c> when the catalogue has one.</summary>
    public static string? Lead(ILocalizer localizer, string name)
    {
        var key = $"AdminDialog.{name}";
        var text = localizer[key];
        return string.IsNullOrWhiteSpace(text) || text == key ? null : text;
    }

    private static void Apply(Form form, ILocalizer localizer, string title, string? lead)
    {
        form.SuspendLayout();

        form.BackColor = UiTheme.SurfaceRaised;
        form.Padding = Padding.Empty;

        var declaredAccept = form.AcceptButton;
        var declaredCancel = form.CancelButton;
        var accept = form.AcceptButton as Button;
        var cancel = form.CancelButton as Button;
        var anchor = accept ?? cancel;

        // The buttons on the row of the confirming one go to the footer; a button docked along the
        // bottom (the "Close" of the viewers) too.
        var footerButtons = new List<Button>();
        if (anchor is not null)
        {
            footerButtons.AddRange(form.Controls.OfType<Button>()
                .Where(b => b.Dock == DockStyle.None && anchor.Dock == DockStyle.None
                                                     && Math.Abs(b.Top - anchor.Top) < 4));
            foreach (var b in new[] { accept, cancel })
                if (b is not null && b.Parent == form && !footerButtons.Contains(b)) footerButtons.Add(b);
        }

        // A dialog without an accept button still has its primary action: it ends the footer.
        accept ??= footerButtons.FirstOrDefault(IsPrimary);

        var content = form.Controls.Cast<Control>().Where(c => !footerButtons.Contains(c)).ToList();
        var absolute = content.All(c => c.Dock == DockStyle.None);

        var headerHeight = string.IsNullOrWhiteSpace(lead) ? HeaderPlain : HeaderWithLead;
        var removed = footerButtons.Where(b => b.Dock is DockStyle.Bottom).Sum(b => b.Height);

        var width = form.ClientSize.Width;
        int height;

        if (absolute && content.Count > 0)
        {
            // Anchors are set aside while everything moves, or growing the form would carry the
            // bottom-anchored fields down a second time.
            var anchors = content.ToDictionary(c => c, c => c.Anchor);
            foreach (var control in content) control.Anchor = AnchorStyles.Top | AnchorStyles.Left;

            var bottom = content.Where(c => c.Visible || c.Height > 0).Select(c => c.Bottom).DefaultIfEmpty(0).Max();
            foreach (var control in content) control.Top += headerHeight;

            height = headerHeight + bottom + 14 + FooterHeight;
            form.ClientSize = new Size(width, height);

            foreach (var (control, value) in anchors) control.Anchor = value;
        }
        else
        {
            height = form.ClientSize.Height + headerHeight + FooterHeight - removed;
            form.ClientSize = new Size(width, height);
        }

        foreach (var label in Descendants(form).OfType<Label>())
        {
            // A field caption: semibold, as the design system writes them.
            if (label.AutoSize && ReferenceEquals(label.Font, UiTheme.Small)
                && label.ForeColor.ToArgb() == UiTheme.TextSecondary.ToArgb())
                label.Font = UiTheme.SmallBold;
        }

        var header = new DialogHeader(title, lead) { Dock = DockStyle.Top, Height = headerHeight };
        var footer = BuildFooter(footerButtons, accept, cancel);

        // Added last so they dock first, outside whatever the dialog docked itself.
        form.Controls.Add(header);
        form.Controls.Add(footer);

        // Moving a button out of the form clears the form's AcceptButton and CancelButton: Enter no
        // longer saved and Escape no longer closed the framed dialogs (password change, users,
        // catalogue and reference-data editors).
        form.AcceptButton = declaredAccept;
        form.CancelButton = declaredCancel;

        form.ResumeLayout(performLayout: true);

        if (localizer.CurrentLanguage.IsRightToLeft())
            RtlLayout.Apply(form, localizer.CurrentLanguage);

        ButtonIcons.Apply(form, localizer);

        FitToScreen(form, header, footer, absolute);
        Recenter(form);
    }

    /// <summary>
    /// The frame makes a dialog taller after Windows has placed it: centre it again on its owner
    /// and keep it inside the working area, so its foot is never below the screen.
    /// </summary>
    private static void Recenter(Form form)
    {
        var area = Screen.FromControl(form.Owner ?? (Control)form).WorkingArea;
        var around = form.StartPosition == FormStartPosition.CenterParent && form.Owner is { } owner
            ? owner.Bounds
            : area;

        var x = around.Left + (around.Width - form.Width) / 2;
        var y = around.Top + (around.Height - form.Height) / 2;
        x = Math.Max(area.Left, Math.Min(x, area.Right - form.Width));
        y = Math.Max(area.Top, Math.Min(y, area.Bottom - form.Height));

        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(x, y);
    }

    private static Panel BuildFooter(List<Button> buttons, Button? accept, Button? cancel)
    {
        var start = buttons.Where(b => b != accept && b != cancel)
                           .OrderBy(b => b.Left).ToList();

        var end = new List<Button>();
        if (cancel is not null && cancel != accept) end.Add(cancel);
        if (accept is not null) end.Add(accept);

        foreach (var button in buttons)
        {
            // The confirming button is the primary one, unless it only closes a viewer.
            var primary = (button == accept && accept != cancel) || IsPrimary(button);
            button.Dock = DockStyle.None;
            AdminLayout.Action(button, primary);
            button.MinimumSize = new Size(110, 34);
        }

        foreach (var button in start) button.Margin = new Padding(0, 0, 8, 0);

        var row = AdminLayout.Row(FooterHeight, start, end, UiTheme.SurfaceRaised);
        row.Dock = DockStyle.Fill;

        var footer = new FooterStrip { Dock = DockStyle.Bottom, Height = FooterHeight, BackColor = UiTheme.SurfaceRaised };
        footer.Padding = new Padding(20, 1, 20, 0);
        footer.Controls.Add(row);
        return footer;
    }

    private static bool IsPrimary(Button button) =>
        button.FlatStyle == FlatStyle.Flat && button.BackColor.ToArgb() == UiTheme.Accent.ToArgb();

    /// <summary>
    /// A dialog taller than the working area: the fields go into a scrolling body and the footer
    /// stays in view. At 1366×768 the analysis editor's Save button otherwise sat below the taskbar.
    /// </summary>
    private static void FitToScreen(Form form, Control header, Control footer, bool absolute)
    {
        var chrome = form.Height - form.ClientSize.Height;
        var available = Screen.FromControl(form.Owner ?? (Control)form).WorkingArea.Height - chrome;

        if (form.ClientSize.Height <= available) return;

        form.SuspendLayout();

        var width = form.ClientSize.Width;

        if (absolute)
        {
            // Wider by the scroll bar: the fields were drawn to the dialog's full width, and the bar
            // took its last pixels, which added a horizontal bar and hid the edge of a column (in
            // Arabic, the whole layout opened scrolled sideways).
            width += SystemInformation.VerticalScrollBarWidth;

            var body = new Panel
            {
                AutoScroll = true,
                AutoScrollMargin = new Size(0, 12),
                BackColor = form.BackColor,
                Dock = DockStyle.Fill
            };

            foreach (var control in form.Controls.Cast<Control>().Where(c => c != header && c != footer).ToList())
            {
                control.Top -= header.Height;
                body.Controls.Add(control);
            }

            form.Controls.Add(body);
            body.BringToFront();
        }

        var drawn = form.ClientSize.Width;
        form.ClientSize = new Size(width, available);

        // In Arabic the window mirrors its own children, not those of a panel inside it: moved
        // into the body, every field went back to the left-to-right layout. They are mirrored
        // here instead, across the width they were drawn on.
        if (absolute && form.RightToLeft == RightToLeft.Yes && form.RightToLeftLayout)
            foreach (var body in form.Controls.OfType<Panel>().Where(p => p.AutoScroll && p.Dock == DockStyle.Fill))
                foreach (Control control in body.Controls) control.Left = drawn - control.Right;

        form.ResumeLayout(performLayout: true);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>The title and its sentence, painted, so a long title ends in "…" rather than wrapping.</summary>
    private sealed class DialogHeader : Control
    {
        private readonly string _lead;

        public DialogHeader(string title, string? lead)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Text = title;
            _lead = lead ?? string.Empty;
            BackColor = UiTheme.SurfaceRaised;
            TabStop = false;
            AccessibleRole = AccessibleRole.TitleBar;
            AccessibleName = title;
            AccessibleDescription = _lead;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, ClientRectangle);

            var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft | TextFormatFlags.Right;

            TextRenderer.DrawText(g, Text, TitleFont, new Rectangle(20, 16, Width - 40, 26),
                                  UiTheme.TextPrimary, flags);

            if (_lead.Length > 0)
                TextRenderer.DrawText(g, _lead, UiTheme.Body, new Rectangle(20, 42, Width - 40, 20),
                                      UiTheme.TextSecondary, flags);
        }
    }

    /// <summary>The footer: white, the line of the design system along its top.</summary>
    private sealed class FooterStrip : Panel
    {
        public FooterStrip()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
            Tag = UiTags.OwnLayout;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, 0, 0, Width, 0);
        }
    }
}
