using System.Globalization;
using System.Text;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The head the relations and steering screens share, in direction A: the page title with the
/// period computed beside it in grey, the actions on the far side (the screen's own, Export, then
/// Compute with its F5 key drawn on it, then the screen's main action), and under it the period as
/// chips (this month, last month, this year, last year, custom) with the two dates.
/// </summary>
internal sealed class PeriodBar : Panel
{
    private enum Preset { ThisMonth, LastMonth, ThisYear, LastYear, Custom }

    private readonly ILocalizer _localizer;
    private readonly PageHead _head = new();
    private readonly FilterRow _row = new();
    private readonly Label _fromLabel = new();
    private readonly Label _toLabel = new();
    private readonly DateTimePicker _from = new();
    private readonly DateTimePicker _to = new();
    private readonly Dictionary<Preset, ChipButton> _chips = [];
    private Preset _preset = Preset.ThisMonth;
    private bool _applying;
    private bool _texts;

    /// <summary>The page title.</summary>
    internal Label Heading => _head.Title;

    /// <summary>The muted line beside the title: the period computed, a count.</summary>
    internal Label Subtitle => _head.Count;

    internal Button Compute { get; }
    internal Button Export { get; } = UiTheme.SecondaryButton(string.Empty);

    /// <summary>Further buttons, placed before the export button.</summary>
    internal FlowLayoutPanel Extra { get; } = ButtonFlow();

    /// <summary>The screen's main action, at the far end after Compute.</summary>
    internal FlowLayoutPanel Lead { get; } = ButtonFlow();

    /// <summary>Further filters, placed after the dates.</summary>
    internal FlowLayoutPanel Filters { get; } = new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
        BackColor = UiTheme.Surface, Margin = new Padding(16, 0, 0, 0), Padding = Padding.Empty
    };

    internal DateOnly From => DateOnly.FromDateTime(_from.Value.Date);
    internal DateOnly To => DateOnly.FromDateTime(_to.Value.Date);

    /// <param name="primaryCompute">
    /// Compute is the page's main button, unless the screen has a main action of its own (send,
    /// new visit): then it is a secondary one, and the screen puts its action in <see cref="Lead"/>.
    /// </param>
    internal PeriodBar(ILocalizer localizer, bool withPeriod = true, bool primaryCompute = true)
    {
        _localizer = localizer;
        Dock = DockStyle.Top;
        BackColor = UiTheme.Surface;
        Tag = UiTags.OwnLayout;

        Compute = primaryCompute ? UiTheme.PrimaryButton(string.Empty) : UiTheme.SecondaryButton(string.Empty);
        // F5 of the main window reloads the screen, which is what Compute does.
        ShortcutHints.Attach(Compute, "F5");
        _head.Add(Extra, Export, Compute, Lead);
        Extra.Margin = Lead.Margin = Padding.Empty;

        foreach (var label in new[] { _fromLabel, _toLabel })
        {
            label.Font = UiTheme.SmallBold;
            label.ForeColor = UiTheme.TextSecondary;
            label.AutoSize = true;
            label.Margin = new Padding(8, 10, 6, 0);
        }

        foreach (var preset in Enum.GetValues<Preset>())
            _chips[preset] = _row.Chip((_, _) => Choose(preset));

        foreach (var picker in new[] { _from, _to })
        {
            picker.Format = DateTimePickerFormat.Short;
            picker.Width = 124;
            picker.Margin = new Padding(0, 5, 0, 0);
        }

        _row.Controls.AddRange([_fromLabel, _from, _toLabel, _to, Filters]);

        if (withPeriod)
        {
            Height = 44 + 10 + 34;
            Controls.Add(_row);
            Controls.Add(Pilotage.Gap(10));
        }
        else
        {
            Height = 44;
        }
        Controls.Add(_head);

        _from.ValueChanged += (_, _) => MarkCustom();
        _to.ValueChanged += (_, _) => MarkCustom();
    }

    private static FlowLayoutPanel ButtonFlow()
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            BackColor = UiTheme.Surface, Padding = Padding.Empty
        };

        // Buttons the screens add take the head's 34-pixel height and its spacing.
        flow.ControlAdded += (_, e) =>
        {
            if (e.Control is Button button)
            {
                button.AutoSize = true;
                button.MinimumSize = new Size(0, 34);
                button.Height = 34;
            }
            if (e.Control is not null) e.Control.Margin = e.Control is CheckBox
                ? new Padding(12, 8, 0, 0)
                : new Padding(8, 0, 0, 0);
        };
        return flow;
    }

    internal void ApplyTexts()
    {
        _fromLabel.Text = _localizer["Field.From"];
        _toLabel.Text = _localizer["Field.To"];
        Compute.Text = _localizer["Statistics.Compute"];
        Export.Text = _localizer["Statistics.ExportCsv"];

        foreach (var (preset, chip) in _chips)
        {
            chip.Text = _localizer[$"Statistics.Preset{preset}"];
            chip.Selected = preset == _preset;
        }

        if (_texts) return;
        _texts = true;
        ApplyPreset();
    }

    private void Choose(Preset preset)
    {
        _preset = preset;
        foreach (var (key, chip) in _chips) chip.Selected = key == preset;
        ApplyPreset();
    }

    private void ApplyPreset()
    {
        if (_applying) return;

        var today = DateTime.Today;
        var month = new DateTime(today.Year, today.Month, 1);
        (DateTime From, DateTime To)? range = _preset switch
        {
            Preset.ThisMonth => (month, today),
            Preset.LastMonth => (month.AddMonths(-1), month.AddDays(-1)),
            Preset.ThisYear => (new DateTime(today.Year, 1, 1), today),
            Preset.LastYear => (new DateTime(today.Year - 1, 1, 1), new DateTime(today.Year - 1, 12, 31)),
            _ => null
        };
        if (range is not { } r) return;

        _applying = true;
        try
        {
            _from.Value = r.From;
            _to.Value = r.To;
        }
        finally
        {
            _applying = false;
        }
    }

    private void MarkCustom()
    {
        if (_applying || _preset == Preset.Custom) return;
        _preset = Preset.Custom;
        foreach (var (key, chip) in _chips) chip.Selected = key == Preset.Custom;
    }
}

/// <summary>Grid and CSV helpers of the relations and steering screens.</summary>
internal static class ReportGrids
{
    internal static void AddColumn(DataGridView grid, string name, int weight, bool numeric = false) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            FillWeight = weight,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = numeric ? UiTheme.Monospace : UiTheme.Body,
                Alignment = numeric ? DataGridViewContentAlignment.MiddleRight : DataGridViewContentAlignment.MiddleLeft
            }
        });

    internal static string Amount(decimal value) => value.ToString("N2", CultureInfo.CurrentCulture);
    internal static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
    internal static string Percent(double? value) => value is { } p ? $"{p:+0.0;-0.0;0.0} %" : "—";
    internal static string Share(double? value) => value is { } p ? $"{p:0.0} %" : "—";
    internal static string Date(DateOnly value) => value.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture);
    internal static string Date(DateOnly? value) => value is { } d ? Date(d) : string.Empty;

    /// <summary>
    /// The grid as CSV, semicolon-separated with a BOM, cells neutralised against spreadsheet
    /// formulas: what a French-locale Excel opens directly into columns.
    /// </summary>
    internal static async Task ExportAsync(DataGridView? grid, IDocumentOutput output, IUserDialogs dialogs,
                                           string title, ILocalizer localizer)
    {
        // An empty list has nothing to export; « Aucun élément sélectionné » sent the operator looking for a selection.
        if (grid is null || grid.Rows.Count == 0)
        {
            dialogs.Info(localizer["App.NoData"], title);
            return;
        }

        static string Cell(object? value) => CsvCell.Of(Convert.ToString(value, CultureInfo.CurrentCulture));

        var columns = grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex).ToList();
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(';', columns.Select(c => Cell(c.HeaderText))));
        foreach (DataGridViewRow row in grid.Rows)
            csv.AppendLine(string.Join(';', columns.Select(c => Cell(row.Cells[c.Index].Value))));

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        var opened = await output.OpenAsync(bytes, "csv");
        if (opened.IsFailure) dialogs.ShowFailure(opened, title);
    }

    /// <summary>A tab page holding one grid.</summary>
    internal static TabPage Page(DataGridView grid)
    {
        var page = new TabPage { BackColor = UiTheme.Surface };
        page.Controls.Add(grid);
        return page;
    }

    /// <summary>A row of key-figure tiles, as large as the statistics screen's.</summary>
    internal static TableLayoutPanel Tiles(Label[] tiles, int columns, int height)
    {
        var rows = (int)Math.Ceiling(tiles.Length / (double)columns);
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = height,
            ColumnCount = columns,
            RowCount = rows,
            BackColor = UiTheme.Surface,
            Padding = new Padding(0, 6, 0, 6)
        };
        for (var i = 0; i < columns; i++) panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        for (var i = 0; i < rows; i++) panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));

        for (var i = 0; i < tiles.Length; i++)
        {
            var label = tiles[i];
            label.Dock = DockStyle.Fill;

            // A direction-A tile draws itself: only its place in the row is set here.
            if (label is KpiTile)
            {
                panel.Padding = new Padding(0, 0, 0, 4);
                label.Margin = new Padding(0, 0, i % columns == columns - 1 ? 0 : 12, 12);
                label.ForeColor = UiTheme.TextPrimary;
                panel.Controls.Add(label, i % columns, i / columns);
                continue;
            }

            label.Font = UiTheme.SectionTitle;
            label.ForeColor = UiTheme.TextPrimary;
            label.BackColor = UiTheme.SurfaceRaised;
            label.Margin = new Padding(0, 0, 8, 6);
            label.Padding = new Padding(8, 4, 8, 4);
            label.TextAlign = ContentAlignment.MiddleLeft;
            panel.Controls.Add(label, i % columns, i / columns);
        }

        return panel;
    }
}

/// <summary>
/// A plain editor dialog built field by field, in direction A: the title in 17 px with an optional
/// sentence under it, a caption above each input in one column, and Cancel then Save at the far end
/// of the foot. The relations editors are short forms; a designer file each would be more code than
/// the forms themselves.
/// </summary>
internal sealed class FieldDialog : Form
{
    private const int Width_ = 460;
    private readonly FlowLayoutPanel _body = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        BackColor = UiTheme.SurfaceRaised,
        Padding = new Padding(20, 8, 12, 12)
    };

    private readonly Func<bool>? _validate;

    /// <param name="intro">The muted sentence under the title; none when null.</param>
    internal FieldDialog(ILocalizer localizer, string title, Func<bool>? validate = null, int height = 560,
                         string? intro = null)
    {
        _validate = validate;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;
        RightToLeft = localizer.CurrentLanguage == AppLanguage.Arabic ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = RightToLeft == RightToLeft.Yes;

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.DialogResult = DialogResult.Cancel;
        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Click += (_, _) =>
        {
            if (_validate is null || _validate())
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        };

        var head = DialogFrame.Head(title, intro, Width_ + 52);
        // The fields keep the room they had: the head is added to the height asked for.
        ClientSize = new Size(Width_ + 52, height + head.Height);

        Controls.Add(_body);
        Controls.Add(DialogFrame.Foot(cancel, ok));
        Controls.Add(head);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>
    /// A form taller than the screen (the convention, at 1366 × 768) is brought back within it; its
    /// fields then scroll between the fixed head and foot.
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var chrome = Height - ClientSize.Height;
        var available = Screen.FromControl(Owner ?? (Control)this).WorkingArea.Height - chrome;
        if (ClientSize.Height > available) ClientSize = new Size(ClientSize.Width, available);

        // A form shorter than the height its caller reserved does not keep a blank band above
        // its foot.
        if (_body.Controls.Count > 0 && _body.VerticalScroll.Visible is false)
        {
            var last = _body.Controls[_body.Controls.Count - 1];
            var content = last.Bottom + last.Margin.Bottom + _body.Padding.Bottom + 8;
            if (content < _body.ClientSize.Height)
                ClientSize = new Size(ClientSize.Width, ClientSize.Height - (_body.ClientSize.Height - content));
        }
    }

    private T Add<T>(string label, T control, int height = 0) where T : Control
    {
        _body.Controls.Add(Pilotage.Caption(label));
        control.Width = Width_;
        if (height > 0) control.Height = height;
        control.Margin = new Padding(0, 0, 0, 4);
        _body.Controls.Add(control);
        return control;
    }

    internal TextBox TextField(string label, string? value, int maxLength = 200, bool multiline = false)
    {
        var box = UiTheme.Input(Width_);
        box.MaxLength = maxLength;
        box.Text = value ?? string.Empty;
        if (multiline)
        {
            box.Multiline = true;
            box.ScrollBars = ScrollBars.Vertical;
            box.AcceptsReturn = true;
        }
        return Add(label, box, multiline ? 90 : 0);
    }

    internal DateTimePicker Date(string label, DateOnly? value, bool optional = false)
    {
        var picker = new DateTimePicker { Format = DateTimePickerFormat.Short, ShowCheckBox = optional };
        picker.Value = (value ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue);
        if (optional) picker.Checked = value is not null;
        return Add(label, picker);
    }

    internal ComboBox Choice<TValue>(string label, IEnumerable<(TValue Value, string Display)> items, TValue? selected)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
        foreach (var (value, display) in items)
        {
            var index = combo.Items.Add(new DialogChoice(value, display));
            if (Equals(value, selected)) combo.SelectedIndex = index;
        }
        return Add(label, combo);
    }

    internal NumericUpDown Number(string label, decimal? value, decimal min, decimal max, int decimals = 0)
    {
        var number = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, TextAlign = HorizontalAlignment.Right };
        number.Value = Math.Clamp(value ?? min, min, max);
        return Add(label, number);
    }

    internal CheckBox Check(string label, bool value)
    {
        var box = new CheckBox { Text = label, Checked = value, AutoSize = true, Margin = new Padding(0, 8, 0, 4) };
        _body.Controls.Add(box);
        return box;
    }

    internal Label Note(string text, Color? color = null)
    {
        var note = new Label
        {
            Text = text,
            Font = UiTheme.Small,
            ForeColor = color ?? UiTheme.TextSecondary,
            AutoSize = false,
            Width = Width_,
            Height = 48,
            Margin = new Padding(0, 6, 0, 2)
        };
        _body.Controls.Add(note);
        return note;
    }

    internal static TValue? Selected<TValue>(ComboBox combo) =>
        combo.SelectedItem is DialogChoice { Value: TValue value } ? value : default;

    internal static DateOnly? Value(DateTimePicker picker) =>
        picker.ShowCheckBox && !picker.Checked ? null : DateOnly.FromDateTime(picker.Value.Date);

    internal static string? Value(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
}

internal sealed record DialogChoice(object? Value, string Display)
{
    public override string ToString() => Display;
}
