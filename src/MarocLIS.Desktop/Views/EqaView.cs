using System.Globalization;
using MarocLIS.Application.Quality;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// External quality assessment: rounds registered, results sent, the organiser's verdicts.
/// <para>
/// One row per analyte per round, because that is the unit the organiser scores and the unit an
/// assessor samples. An unsatisfactory verdict opens a non-conformity on the spot; the screen
/// says which one, so the operator can go and fill in the investigation straight away.
/// </para>
/// </summary>
public sealed class EqaView : LocalizedView
{
    private readonly EqaService _eqa;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<EqaView>? _logger;

    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Label _heading = new();
    private readonly Label _count = new();
    private readonly Label _yearLabel = new();
    private readonly NumericUpDown _year = new();
    private readonly CheckBox _onlyPending = new();

    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _register = UiTheme.PrimaryButton(string.Empty);
    // Secondary: registering a round is the screen's one primary action.
    private readonly Button _submission = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _evaluation = UiTheme.SecondaryButton(string.Empty);

    private readonly Label _detail = new();

    private IReadOnlyList<EqaRow> _rows = [];

    public EqaView(EqaService eqa, IUserDialogs dialogs, ILocalizer localizer,
                   ICultureManager cultureManager, ILogger<EqaView>? logger = null)
        : base(localizer, cultureManager)
    {
        _eqa = eqa;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Eqa");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = QhPage.PagePadding;

        _year.Minimum = EqaScreenRules.YearMin;
        _year.Maximum = EqaScreenRules.YearMax;
        _year.Value = DateTime.Today.Year;

        BuildColumns();

        var header = QhPage.Header(_heading, _count, _refresh, _register);
        var toolbar = QhPage.Toolbar(QhPage.Caption(_yearLabel), QhPage.Filter(_year, 80),
                                     QhPage.Chip(_onlyPending), _onlyPending);

        // The selected participation's detail and its two steps at the foot of the list.
        var body = new Panel { BackColor = UiTheme.SurfaceRaised, Margin = Padding.Empty };
        _grid.Dock = DockStyle.Fill;
        body.Controls.Add(_grid);
        body.Controls.Add(new QhSelectionBar(_detail, _submission, _evaluation));

        Controls.Add(QhPage.Frame(2, header, toolbar, QhPage.Card(body)));
    }

    private void BuildColumns()
    {
        _grid.Columns.Clear();

        // The scheme and the parameter wrap rather than claim their longest value as a minimum
        // width: together they pushed « Verdict » behind a horizontal scroll at 1366×768.
        void Add(string name, int weight, Font? font = null,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body, Alignment = align,
                    WrapMode = name is "scheme" or "parameter" ? DataGridViewTriState.True : DataGridViewTriState.NotSet
                }
            });

        const DataGridViewContentAlignment right = DataGridViewContentAlignment.MiddleRight;

        Add("scheme", 12);
        Add("round", 9, UiTheme.Monospace);
        Add("date", 8, UiTheme.Monospace);
        Add("parameter", 14);
        Add("instrument", 8, UiTheme.Monospace);
        Add("deadline", 8, UiTheme.Monospace);
        Add("reported", 8, UiTheme.Monospace, right);
        Add("assigned", 8, UiTheme.Monospace, right);
        Add("z", 6, UiTheme.MonospaceBold, right);
        Add("bias", 7, UiTheme.Monospace, right);
        Add("outcome", 10, UiTheme.SmallBold);
        Add("nc", 9, UiTheme.Monospace);
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();
        _onlyPending.CheckedChanged += async (_, _) => await LoadAsync();
        _year.ValueChanged += async (_, _) => await LoadAsync();

        _grid.SelectionChanged += (_, _) => ShowDetail();
        _grid.CellFormatting += OnCellFormatting;
        QhGridPills.Attach(_grid, "outcome", r => r.Tag is EqaRow row ? QhTone.Of(EqaScreenRules.OutcomeTone(row)) : null);

        _register.Click += async (_, _) => await RegisterRoundAsync();
        _submission.Click += async (_, _) => await RecordSubmissionAsync();
        _evaluation.Click += async (_, _) => await RecordEvaluationAsync();

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Quality.ReviewQc, _register, _evaluation);
        PermissionGate.Require(Permissions.Quality.EnterQc, _submission);
    }

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
        if (_grid.Rows[e.RowIndex].Tag is not EqaRow row) return;

        // The verdict is a pill; a submission past its deadline keeps its date in the warning ink,
        // and a bad z-score its value in the critical one.
        var ink = _grid.Columns[e.ColumnIndex].Name switch
        {
            "deadline" => EqaScreenRules.DeadlineInk(row),
            "z" => EqaScreenRules.ZInk(row),
            _ => QualityInk.Plain
        };
        if (QhTone.InkOf(ink) is { } colour) e.CellStyle!.ForeColor = colour;
    }

    protected override void ApplyTexts()
    {
        _yearLabel.Text = T("Eqa.Year");
        _onlyPending.Text = T("Eqa.OnlyPending");
        _refresh.Text = T("Action.Refresh");
        _register.Text = T("Eqa.RegisterRound");
        _submission.Text = T("Eqa.RecordSubmission");
        _evaluation.Text = T("Eqa.RecordEvaluation");

        if (_grid.Columns.Count > 0)
        {
            _grid.Column("scheme").HeaderText = T("Quality.Scheme");
            _grid.Column("round").HeaderText = T("Quality.Round");
            _grid.Column("date").HeaderText = T("Field.Date");
            _grid.Column("parameter").HeaderText = T("Eqa.Parameter");
            _grid.Column("instrument").HeaderText = T("Eqa.Instrument");
            _grid.Column("deadline").HeaderText = T("Eqa.Deadline");
            _grid.Column("reported").HeaderText = T("Eqa.ReportedValue");
            _grid.Column("assigned").HeaderText = T("Quality.AssignedValue");
            _grid.Column("z").HeaderText = "Z";
            _grid.Column("bias").HeaderText = T("Quality.Bias");
            _grid.Column("outcome").HeaderText = T("Quality.Outcome");
            _grid.Column("nc").HeaderText = T("Eqa.NonConformity");
        }

        RenderHeading();
        if (_rows.Count > 0) Render((_grid.CurrentRow?.Tag as EqaRow)?.Id);
        ShowDetail();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override Task ActivateAsync(CancellationToken ct = default) => LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct = default)
    {
        // The participation just worked on stays the current one: the list went back to its first
        // row after each result or evaluation, away from the line whose verdict was to be read.
        var keep = (_grid.CurrentRow?.Tag as EqaRow)?.Id;

        try
        {
            var result = await _eqa.GetParticipationsAsync((int)_year.Value, _onlyPending.Checked, ct);
            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            _rows = result.Value;
            Render(keep);
        }
        catch (OperationCanceledException) { }
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading EQA participations failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)));
        }
    }

    // Shown in the operator's culture, as on the QC screen: « 1.32 » here beside « 1,32 » there.
    private static string Number(decimal? value) => EqaScreenRules.Number(value, CultureInfo.CurrentCulture);

    private void Render(Guid? keep = null)
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var row in _rows)
        {
            var index = _grid.Rows.Add(
                row.SchemeName,
                row.RoundReference,
                EqaScreenRules.Date(row.RoundDate),
                EqaScreenRules.Parameter(row, Localizer.CurrentLanguage),
                row.InstrumentCode ?? string.Empty,
                EqaScreenRules.Deadline(row),
                Number(row.ReportedValue),
                EqaScreenRules.Assigned(row, CultureInfo.CurrentCulture),
                EqaScreenRules.ZScore(row.ZScore, CultureInfo.CurrentCulture),
                EqaScreenRules.Bias(row.BiasPercent, CultureInfo.CurrentCulture),
                T($"EqaOutcome.{row.Outcome}"),
                row.NonConformityReference ?? string.Empty);

            _grid.Rows[index].Tag = row;
        }

        _grid.ResumeLayout(performLayout: true);
        RenderHeading();

        var kept = _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => keep is not null && r.Tag is EqaRow row && row.Id == keep);
        if (kept is { Index: > 0 }) _grid.MakeCurrent(kept);
        else if (_grid.Rows.Count > 0) _grid.SelectFirstRow();
        ShowDetail();
    }

    private void RenderHeading()
    {
        _heading.Text = T("Quality.Eqa");
        _count.Text = EqaScreenRules.Count(_rows, T);
    }

    private void ShowDetail()
    {
        var row = _grid.CurrentRow?.Tag as EqaRow;

        var actions = EqaScreenRules.Actions(p => PermissionGate.Allows(p), row);
        _submission.Enabled = actions.Submission;
        _evaluation.Enabled = actions.Evaluation;

        _detail.Text = string.Join("  ·  ", EqaScreenRules.Detail(row, Localizer.CurrentLanguage, T, CultureInfo.CurrentCulture));
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private async Task RegisterRoundAsync()
    {
        try
        {
            var parameters = await _eqa.GetParameterOptionsAsync();
            if (parameters.IsFailure) { _dialogs.ShowFailure(parameters, _register.Text); return; }

            var instruments = await _eqa.GetInstrumentOptionsAsync();
            if (instruments.IsFailure) { _dialogs.ShowFailure(instruments, _register.Text); return; }

            using var dialog = new EqaRoundDialog(Localizer, _dialogs, parameters.Value,
                                                  instruments.Value);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;

            var result = await _eqa.RegisterRoundAsync(dialog.Request);
            if (result.IsFailure) { _dialogs.ShowFailure(result, _register.Text); return; }

            _dialogs.Info(T("Eqa.RoundRegistered", result.Value), _register.Text);

            if (EqaScreenRules.YearAfterRegistering(dialog.Request) is var year && year != (int)_year.Value)
                _year.Value = year;   // Reloads through ValueChanged.
            else
                await LoadAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Registering an EQA round failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), _register.Text);
        }
    }

    private async Task RecordSubmissionAsync()
    {
        if (_grid.CurrentRow?.Tag is not EqaRow row) return;

        using var dialog = new EqaSubmissionDialog(Localizer, _dialogs, row);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var result = await _eqa.RecordSubmissionAsync(row.Id, dialog.ReportedValue, dialog.SubmittedOn);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _submission.Text); return; }

        await LoadAsync();
    }

    private async Task RecordEvaluationAsync()
    {
        if (_grid.CurrentRow?.Tag is not EqaRow row) return;

        using var dialog = new EqaEvaluationDialog(Localizer, _dialogs, row);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Evaluation is null) return;

        var result = await _eqa.RecordEvaluationAsync(row.Id, dialog.Evaluation);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _evaluation.Text); return; }

        // The non-conformity was opened by the service; the operator is told which one, since the
        // investigation is now theirs to write.
        if (EqaScreenRules.NonConformityOpened(result.Value, T) is { } opened)
            _dialogs.Warn(opened, _evaluation.Text);

        await LoadAsync();
    }
}

/// <summary>Registering a round: the scheme, the round and the analytes its samples cover.</summary>
internal sealed class EqaRoundDialog : MasterDataEditor
{
    private readonly TextBox _scheme, _round;
    private readonly DateTimePicker _roundDate;
    private readonly (CheckBox Toggle, DateTimePicker Picker) _deadline;
    private readonly ComboBox _instrument;
    private readonly CheckedListBox _parameters = new();
    private readonly IReadOnlyList<EqaOption> _parameterOptions;

    internal RegisterEqaRoundRequest? Request { get; private set; }

    internal EqaRoundDialog(ILocalizer localizer, IUserDialogs dialogs,
                            IReadOnlyList<EqaOption> parameters, IReadOnlyList<EqaOption> instruments)
        : base(localizer, dialogs, "Eqa.RegisterRound", null, 560, 560)
    {
        _parameterOptions = parameters;

        _scheme = TextField("Quality.Scheme", 20, 14, 300);
        _round = TextField("Quality.Round", 340, 14, 200, null, latin: true, mono: true);
        // Bounded to the columns: beyond, the save failed with « Erreur inattendue ».
        _scheme.MaxLength = EqaScreenRules.SchemeMaxLength;
        _round.MaxLength = EqaScreenRules.RoundMaxLength;

        Label("Eqa.RoundDate", 20, 68);
        _roundDate = new DateTimePicker
        {
            Bounds = new Rectangle(20, 86, 180, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            Value = DateTime.Today
        };
        Controls.Add(_roundDate);

        _deadline = OptionalDate("Eqa.Deadline", 240, 68, EqaScreenRules.NewRound(DateOnly.FromDateTime(DateTime.Today)).Deadline);

        var language = localizer.CurrentLanguage;
        _instrument = Lookup("Eqa.Instrument", 20, 122, 520,
                             instruments.Select(i => (i.Id, EqaScreenRules.Option(i, language))), null);

        Label("Eqa.Parameters", 20, 176);
        _parameters.Bounds = new Rectangle(20, 194, 520, 250);
        _parameters.CheckOnClick = true;
        _parameters.IntegralHeight = false;
        foreach (var parameter in parameters)
            _parameters.Items.Add(EqaScreenRules.Option(parameter, language));
        Controls.Add(_parameters);

        Notice(20, 452, 520, 46, "Eqa.RegisterNotice");

        Buttons(512, 560);
        QhDialog.Dress(this, localizer, "QualityHr.Hint.EqaRound");
    }

    protected override void Accept()
    {
        var deadline = DateOrNull(_deadline.Toggle, _deadline.Picker);
        var form = new EqaRoundForm
        {
            Scheme = _scheme.Text,
            Round = _round.Text,
            RoundDate = DateOnly.FromDateTime(_roundDate.Value.Date),
            HasDeadline = deadline is not null,
            Deadline = deadline ?? default,
            InstrumentId = SelectedId(_instrument)
        };
        form.ParameterIds.AddRange(_parameters.CheckedIndices.Cast<int>().Select(i => _parameterOptions[i].Id));

        if (Refused(EqaScreenRules.Check(form),
                    (EqaScreenRules.SchemeField, _scheme), (EqaScreenRules.RoundField, _round),
                    (EqaScreenRules.ParametersField, _parameters), (EqaScreenRules.DeadlineField, _deadline.Picker)))
            return;

        Request = EqaScreenRules.ToRequest(form);

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>The value sent to the organiser, as sent.</summary>
internal sealed class EqaSubmissionDialog : MasterDataEditor
{
    private readonly TextBox _value;
    private readonly DateTimePicker _date;

    internal decimal ReportedValue { get; private set; }
    internal DateOnly SubmittedOn => DateOnly.FromDateTime(_date.Value.Date);

    internal EqaSubmissionDialog(ILocalizer localizer, IUserDialogs dialogs, EqaRow row)
        : base(localizer, dialogs, "Eqa.RecordSubmission",
               $"{row.RoundReference} · {row.ParameterCode}", 420, 200)
    {
        _value = TextField("Eqa.ReportedValue", 20, 14, 160,
                           row.ReportedValue?.ToString(CultureInfo.InvariantCulture),
                           latin: true, mono: true, right: true);
        Numeric(EditorValues.MeasureMaxLength, _value);

        Label("Eqa.SubmittedOn", 220, 14);
        _date = new DateTimePicker
        {
            Bounds = new Rectangle(220, 32, 180, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            MaxDate = DateTime.Today,
            Value = DateTime.Today
        };
        Controls.Add(_date);

        Notice(20, 76, 380, 46, "Eqa.SubmissionNotice");

        Buttons(146, 420);
        QhDialog.Dress(this, localizer, "QualityHr.Hint.EqaSubmission");
    }

    protected override void Accept()
    {
        var (value, refusal) = EqaScreenRules.Read(new EqaSubmissionForm { Value = _value.Text, SubmittedOn = SubmittedOn },
                                                   DateOnly.FromDateTime(DateTime.Today));
        if (Refused(refusal, (EqaScreenRules.ValueField, _value), (EqaScreenRules.SubmittedOnField, _date))) return;

        ReportedValue = value!.Value;
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>The organiser's report for one analyte.</summary>
internal sealed class EqaEvaluationDialog : MasterDataEditor
{
    private const int FromScore = -1;

    private readonly TextBox _assigned, _peerMean, _peerSd, _z, _notesFr, _notesAr;
    private readonly ComboBox _outcome = new();

    internal EqaEvaluation? Evaluation { get; private set; }

    internal EqaEvaluationDialog(ILocalizer localizer, IUserDialogs dialogs, EqaRow row)
        : base(localizer, dialogs, "Eqa.RecordEvaluation",
               $"{row.RoundReference} · {row.ParameterCode}", 560, 420)
    {
        var form = EqaScreenRules.EvaluationOf(row);

        _assigned = TextField("Quality.AssignedValue", 20, 14, 120, form.Assigned,
                              latin: true, mono: true, right: true);
        _peerMean = TextField("Eqa.PeerMean", 150, 14, 120, form.PeerMean,
                              latin: true, mono: true, right: true);
        _peerSd = TextField("Eqa.PeerSd", 280, 14, 120, form.PeerSd,
                            latin: true, mono: true, right: true);
        _z = TextField("Quality.ZScore", 410, 14, 130, form.ZScore,
                       latin: true, mono: true, right: true);

        // A figure typed with its unit (« 12,5 g/l ») was saved as no figure, and the score
        // worked out without it.
        Numeric(EditorValues.MeasureMaxLength, _assigned, _peerMean, _peerSd, _z);

        Notice(20, 64, 520, 46, "Eqa.ScoreNotice");

        Label("Quality.Outcome", 20, 114);
        _outcome.DropDownStyle = ComboBoxStyle.DropDownList;
        _outcome.FlatStyle = FlatStyle.Flat;
        _outcome.Bounds = new Rectangle(20, 132, 300, UiTheme.InputHeight);
        _outcome.Items.Add(new EnumChoice(FromScore, localizer["Eqa.OutcomeFromScore"]));
        foreach (var outcome in EqaScreenRules.OverridableOutcomes)
        {
            var index = _outcome.Items.Add(new EnumChoice((int)outcome, localizer[$"EqaOutcome.{outcome}"]));
            if (form.Outcome == outcome) _outcome.SelectedIndex = index;
        }
        if (_outcome.SelectedIndex < 0) _outcome.SelectedIndex = 0;
        Controls.Add(_outcome);

        // A note written only in Arabic is stored with itself as the French fallback: the French
        // box opens empty for it rather than showing the Arabic text twice.
        _notesFr = TextField("Eqa.InvestigationFr", 20, 176, 520, form.NotesFr);
        _notesAr = TextField("Eqa.InvestigationAr", 20, 230, 520, form.NotesAr, arabic: true);
        _notesFr.MaxLength = _notesAr.MaxLength = EqaScreenRules.NotesMaxLength;

        Notice(20, 290, 520, 60, "Eqa.UnsatisfactoryNotice", UiTheme.Abnormal);

        Buttons(372, 560);
        QhDialog.Dress(this, localizer, "QualityHr.Hint.EqaEvaluation");
    }

    protected override void Accept()
    {
        var choice = (_outcome.SelectedItem as EnumChoice)?.Value ?? FromScore;

        var reading = EqaScreenRules.Read(new EqaEvaluationForm
        {
            Assigned = _assigned.Text,
            PeerMean = _peerMean.Text,
            PeerSd = _peerSd.Text,
            ZScore = _z.Text,
            Outcome = choice == FromScore ? null : (EqaOutcome)choice,
            NotesFr = _notesFr.Text,
            NotesAr = _notesAr.Text
        });
        if (Refused(reading.Refusal, (EqaScreenRules.AssignedField, _assigned), (EqaScreenRules.PeerMeanField, _peerMean),
                    (EqaScreenRules.PeerSdField, _peerSd), (EqaScreenRules.ZScoreField, _z)))
            return;

        Evaluation = reading.Value;

        DialogResult = DialogResult.OK;
        Close();
    }
}
