using MarocLIS.Application.Abstractions;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// "Tester une trame": paste a frame, see what the link would do with every result in it.
/// <para>
/// Nothing is stored. The point is to settle a format question — which component is the code, which
/// field is the tube — before an analyser's real results start arriving, and to understand why a
/// logged frame did not import without replaying it.
/// </para>
/// </summary>
internal sealed class InstrumentFrameTestDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly Func<string, Task<Result<MessagePreview>>> _preview;
    private readonly Func<Task<string?>> _loadLast;
    private readonly IReadOnlyDictionary<Guid, string> _parameters;

    private readonly TextBox _frame;
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Label _summary;

    /// <param name="preview">Runs the preview with the protocol and format being tested.</param>
    /// <param name="loadLast">The last frame the link received, or null when there is none.</param>
    /// <param name="parameters">Parameter labels by id, from the mapping table.</param>
    /// <param name="sampleFrame">
    /// An example frame in the link's protocol, from the simulator, for a protocol whose frames a
    /// laboratory has no copy of before the analyser is connected (the COBAS INTEGRA). Null hides
    /// the button.
    /// </param>
    internal InstrumentFrameTestDialog(ILocalizer localizer, IUserDialogs dialogs, string subtitle,
                                       Func<string, Task<Result<MessagePreview>>> preview,
                                       Func<Task<string?>> loadLast,
                                       IReadOnlyDictionary<Guid, string> parameters,
                                       string? initialFrame = null,
                                       Func<Task<string>>? sampleFrame = null)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _preview = preview;
        _loadLast = loadLast;
        _parameters = parameters;

        Text = $"{localizer["Interface.TestFrame"].TrimEnd('…')} — {subtitle}";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(1180, 680);
        MinimumSize = new Size(820, 480);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var help = new Label
        {
            Text = localizer["Interface.TestFrameHelp"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            Dock = DockStyle.Top,
            Height = 36,
            Padding = new Padding(8, 6, 8, 0)
        };

        _frame = new TextBox
        {
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Top,
            Height = 200,
            Font = UiTheme.Monospace,
            Tag = UiTags.LatinValue,
            Text = Displayable(initialFrame)
        };

        var actions = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = UiTheme.SurfaceRaised };

        var run = UiTheme.PrimaryButton(localizer["Interface.TestFrameRun"]);
        run.Bounds = new Rectangle(8, 6, 200, UiTheme.ButtonHeight);
        run.Click += async (_, _) => await RunAsync();

        var last = UiTheme.SecondaryButton(localizer["Interface.TestFrameLoadLast"]);
        last.Bounds = new Rectangle(216, 6, 260, UiTheme.ButtonHeight);
        last.Click += async (_, _) =>
        {
            var frame = await _loadLast();
            if (frame is null) { _dialogs.Warn(_localizer["Interface.TestFrameNoMessage"], Text); return; }
            _frame.Text = Displayable(frame);
        };

        var summaryLeft = 490;
        Button? sample = null;

        if (sampleFrame is not null)
        {
            sample = UiTheme.SecondaryButton(localizer["Interface.TestFrameSample"]);
            sample.Bounds = new Rectangle(484, 6, 230, UiTheme.ButtonHeight);
            sample.Click += async (_, _) =>
            {
                _frame.Text = Displayable(await sampleFrame());
                await RunAsync();
            };
            summaryLeft = 722;
        }

        _summary = new Label
        {
            Font = UiTheme.SmallBold,
            AutoSize = false,
            Bounds = new Rectangle(summaryLeft, 6, 1150 - summaryLeft, UiTheme.ButtonHeight),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };

        actions.Controls.AddRange([run, last, _summary]);
        if (sample is not null) actions.Controls.Add(sample);

        // Sized from the bar itself: anchored to the right while the bar still had its default
        // width, the summary came out wider than the dialog.
        actions.Resize += (_, _) =>
            _summary.Width = Math.Max(80, actions.ClientSize.Width - _summary.Left - 8);

        BuildColumns();
        _grid.Dock = DockStyle.Fill;
        _grid.CellFormatting += Colour;

        var close = UiTheme.SecondaryButton(localizer["Action.Close"]);
        close.Dock = DockStyle.Bottom;
        close.Height = UiTheme.ButtonHeight + 8;
        close.DialogResult = DialogResult.OK;

        Controls.Add(_grid);
        Controls.Add(actions);
        Controls.Add(_frame);
        Controls.Add(help);
        Controls.Add(close);

        CancelButton = close;
        AdminDialogFrame.Attach(this, localizer, AdminDialogFrame.Lead(localizer, nameof(InstrumentFrameTestDialog)));

        if (!string.IsNullOrWhiteSpace(initialFrame))
            Shown += async (_, _) => await RunAsync();
    }

    /// <summary>A stored frame keeps CR between records; a text box needs a full line break.</summary>
    private static string Displayable(string? frame) =>
        (frame ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", Environment.NewLine);

    private void BuildColumns()
    {
        // « Code lu », « Valeur reçue » and « Type » get a little of the outcome's room: in Arabic their
        // headers were cut, the outcome column has room to spare.
        (string Name, string Key, int Weight, bool Mono)[] columns =
        [
            ("specimen", "Interface.TestFrameSpecimen", 14, true),
            ("order", "Interface.TestFrameOrder", 16, false),
            ("raw", "Interface.TestFrameRawTest", 12, true),
            ("code", "Interface.TestFrameCode", 10, true),
            ("parameter", "Interface.TestFrameParameter", 10, true),
            ("value", "Interface.TestFrameValue", 11, true),
            ("stored", "Interface.TestFrameStored", 9, true),
            ("flags", "Result.InstrumentFlags", 10, false),
            ("type", "Interface.TestFrameType", 8, true),
            ("outcome", "Interface.TestFrameOutcome", 24, false)
        ];

        _grid.Columns.Clear();

        foreach (var (name, key, weight, mono) in columns)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = _localizer[key],
                FillWeight = weight,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = mono ? UiTheme.Monospace : UiTheme.Small
                }
            });
        }
    }

    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(_frame.Text))
        {
            _dialogs.Warn(_localizer["Interface_RawFrameEmpty"], Text);
            return;
        }

        UseWaitCursor = true;

        try
        {
            var result = await _preview(_frame.Text);

            if (result.IsFailure) { _dialogs.ShowFailure(result, Text); return; }

            Render(result.Value);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void Render(MessagePreview preview)
    {
        _grid.Rows.Clear();

        foreach (var line in preview.Lines)
        {
            var specimen = line.IsControl
                ? $"{_localizer["Interface.TestFrameControl"]} {line.SpecimenId}"
                : line.SpecimenId ?? "—";

            var order = string.Join(" · ", new[] { line.AccessionNumber, line.PatientName }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            var parameter = line.ParameterCode
                            ?? (line.TestParameterId is { } id && _parameters.TryGetValue(id, out var known)
                                ? known
                                : string.Empty);

            var outcome = _localizer[$"PreviewOutcome.{line.Outcome}"]
                          + (line.Detail is { Length: > 0 } detail ? $" ({detail})" : string.Empty);

            var index = _grid.Rows.Add(specimen, order, line.RawTestId ?? string.Empty,
                                       line.Code ?? string.Empty, parameter,
                                       ((line.Value ?? string.Empty) + " " + (line.Unit ?? string.Empty)).Trim(),
                                       line.StoredValue ?? string.Empty, line.Flags ?? string.Empty,
                                       line.ResultType ?? string.Empty, outcome);

            _grid.Rows[index].Tag = line;
        }

        var issues = string.Join("  ·  ", preview.Issues.Select(Issue));

        _summary.ForeColor = preview.WouldStore > 0 ? UiTheme.Normal : UiTheme.Abnormal;
        _summary.Text = string.Format(_localizer["Interface.TestFrameSummary"],
                                      preview.WouldStore, preview.Lines.Count(l => l.Code is not null))
                        + (issues.Length > 0 ? $"  ·  {issues}" : string.Empty);
    }

    private string Issue(string stored)
    {
        var tab = stored.IndexOf('\t');
        if (tab < 0) return _localizer[stored];

        var text = _localizer[stored[..tab]];
        var detail = stored[(tab + 1)..];

        return text.Contains("{0}") ? string.Format(text, detail) : $"{text} : {detail}";
    }

    private void Colour(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not PreviewLine line) return;
        if (_grid.Columns[e.ColumnIndex].Name != "outcome") return;

        e.CellStyle!.ForeColor = line.Outcome switch
        {
            PreviewOutcome.WouldStore or PreviewOutcome.WouldStoreControl
                or PreviewOutcome.WouldMarkNotDone => UiTheme.Normal,
            PreviewOutcome.ExcludedType or PreviewOutcome.IgnoredNonNumeric => UiTheme.TextMuted,
            _ => UiTheme.Critical
        };
    }
}
