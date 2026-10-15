using System.Globalization;
using System.Text;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The period bar the relations and steering screens share: a preset (this month, last month,
/// this year, last year, custom), the two dates, a compute button and an export button.
/// </summary>
internal sealed class PeriodBar : Panel
{
    private enum Preset { ThisMonth, LastMonth, ThisYear, LastYear, Custom }

    private readonly ILocalizer _localizer;
    private readonly Label _fromLabel = new();
    private readonly Label _toLabel = new();
    private readonly DateTimePicker _from = new();
    private readonly DateTimePicker _to = new();
    private readonly ComboBox _preset = new();
    private bool _applying;

    internal Label Heading { get; } = new();
    internal Button Compute { get; } = UiTheme.PrimaryButton(string.Empty);
    internal Button Export { get; } = UiTheme.SecondaryButton(string.Empty);

    /// <summary>Further buttons, placed after the export button.</summary>
    internal FlowLayoutPanel Extra { get; } = new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = UiTheme.Surface
    };

    internal DateOnly From => DateOnly.FromDateTime(_from.Value.Date);
    internal DateOnly To => DateOnly.FromDateTime(_to.Value.Date);

    internal PeriodBar(ILocalizer localizer, bool withPeriod = true)
    {
        _localizer = localizer;
        Dock = DockStyle.Top;
        Height = 92;
        BackColor = UiTheme.Surface;

        Heading.Font = UiTheme.PageTitle;
        Heading.ForeColor = UiTheme.TextPrimary;
        Heading.AutoSize = true;
        Heading.Location = new Point(0, 0);

        foreach (var label in new[] { _fromLabel, _toLabel })
        {
            label.Font = UiTheme.Small;
            label.ForeColor = UiTheme.TextSecondary;
            label.AutoSize = true;
        }

        _preset.DropDownStyle = ComboBoxStyle.DropDownList;
        _preset.FlatStyle = FlatStyle.Flat;
        _preset.Bounds = new Rectangle(0, 52, 180, UiTheme.InputHeight);
        _fromLabel.Location = new Point(196, 34);
        _from.Format = DateTimePickerFormat.Short;
        _from.Bounds = new Rectangle(196, 52, 130, UiTheme.InputHeight);
        _toLabel.Location = new Point(338, 34);
        _to.Format = DateTimePickerFormat.Short;
        _to.Bounds = new Rectangle(338, 52, 130, UiTheme.InputHeight);

        var x = withPeriod ? 484 : 0;
        Compute.Bounds = new Rectangle(x, 50, 140, UiTheme.ButtonHeight);
        Export.Bounds = new Rectangle(x + 150, 50, 160, UiTheme.ButtonHeight);
        Extra.Location = new Point(x + 320, 48);
        // On a narrow window the extra buttons would run off the bar: they move up beside the title.
        Layout += (_, _) =>
        {
            var fits = x + 320 + Extra.Width <= ClientSize.Width;
            Extra.Location = fits
                ? new Point(x + 320, 48)
                : new Point(Math.Max(Heading.Right + 16, ClientSize.Width - Extra.Width), 0);
        };

        Controls.Add(Heading);
        if (withPeriod) Controls.AddRange([_preset, _fromLabel, _from, _toLabel, _to]);
        Controls.AddRange([Compute, Export, Extra]);

        _preset.SelectedIndexChanged += (_, _) => ApplyPreset();
        _from.ValueChanged += (_, _) => MarkCustom();
        _to.ValueChanged += (_, _) => MarkCustom();
    }

    internal void ApplyTexts()
    {
        _fromLabel.Text = _localizer["Field.From"];
        _toLabel.Text = _localizer["Field.To"];
        Compute.Text = _localizer["Statistics.Compute"];
        Export.Text = _localizer["Statistics.ExportCsv"];

        var selected = (_preset.SelectedItem as EnumChoice)?.Value ?? (int)Preset.ThisMonth;
        var first = _preset.Items.Count == 0;
        _applying = true;
        _preset.Items.Clear();
        foreach (var preset in Enum.GetValues<Preset>())
        {
            var index = _preset.Items.Add(new EnumChoice((int)preset, _localizer[$"Statistics.Preset{preset}"]));
            if ((int)preset == selected) _preset.SelectedIndex = index;
        }
        _applying = false;
        if (first) ApplyPreset();
    }

    private void ApplyPreset()
    {
        if (_applying || _preset.SelectedItem is not EnumChoice { Value: var value }) return;

        var today = DateTime.Today;
        var month = new DateTime(today.Year, today.Month, 1);
        (DateTime From, DateTime To)? range = (Preset)value switch
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
        if (_applying) return;
        var custom = _preset.Items.Cast<EnumChoice>().FirstOrDefault(c => c.Value == (int)Preset.Custom);
        if (custom is not null && !Equals(_preset.SelectedItem, custom))
        {
            _applying = true;
            _preset.SelectedItem = custom;
            _applying = false;
        }
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
        if (grid is null || grid.Rows.Count == 0)
        {
            dialogs.Info(localizer["Message.NothingSelected"], title);
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
/// A plain editor dialog built field by field: a label above each input, in one column, with Save
/// and Cancel at the bottom. The relations editors are short forms; a designer file each would be
/// more code than the forms themselves.
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
        Padding = new Padding(16, 12, 16, 0)
    };

    private readonly Func<bool>? _validate;

    internal FieldDialog(ILocalizer localizer, string title, Func<bool>? validate = null, int height = 560)
    {
        _validate = validate;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(Width_ + 52, height);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;
        RightToLeft = localizer.CurrentLanguage == AppLanguage.Arabic ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = RightToLeft == RightToLeft.Yes;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(16, 10, 16, 10)
        };

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Size = new Size(140, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;
        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Size = new Size(160, UiTheme.ButtonHeight);
        ok.Click += (_, _) =>
        {
            if (_validate is null || _validate())
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        };
        buttons.Controls.AddRange([cancel, ok]);

        Controls.Add(_body);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private T Add<T>(string label, T control, int height = 0) where T : Control
    {
        _body.Controls.Add(new Label
        {
            Text = label,
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 2)
        });
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
