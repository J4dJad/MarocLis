using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Samples;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Samples;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Sample reception — the bench that decides whether a tube is fit to analyse.
/// <para>
/// ISO 15189 §5.4 puts the acceptance decision here, and the screen is built so that the decision
/// is deliberate rather than reflexive:
/// </para>
/// <list type="bullet">
/// <item><b>The scan box always holds focus.</b> Reception is a scanning loop, not a form.</item>
/// <item><b>The stability verdict is computed and stated</b> — "delay exceeded" rather than two
/// timestamps and a table the operator has to reason about under pressure.</item>
/// <item><b>Accepting a defective tube is a separate button</b> from accepting a conforming one.
/// The domain refuses a silent acceptance, and the UI reflects that instead of hiding it behind a
/// checkbox somebody leaves ticked.</item>
/// <item><b>A rejection reason comes from a closed list.</b> The rejection rate by reason is a
/// mandatory quality indicator, and free text cannot be counted.</item>
/// </list>
/// </summary>
public sealed class SampleReceptionView : LocalizedView
{
    private readonly SampleWorkflowService _workflow;
    private readonly ISampleLabelRenderer _labels;
    private readonly ILabelPrinter _labelPrinter;
    private readonly IDocumentOutput _documents;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<SampleReceptionView>? _logger;

    // Scan
    private readonly TextBox _scan = UiTheme.Input(280);
    private readonly Label _scanLabel = new();

    // Candidate detail
    private readonly Panel _detail = new();
    private readonly Label _patientLine = new();
    private readonly Label _orderLine = new();
    private readonly Label _tubeLine = new();
    private readonly Label _delayLine = new();

    // Conformity
    private readonly GroupBox _defectBox = new();
    private readonly Dictionary<SampleDefect, CheckBox> _defectChecks = [];
    private readonly Label _temperatureLabel = new();
    private readonly TextBox _temperature = UiTheme.Input(80);
    private readonly CheckBox _coldChain = new();
    private readonly Label _storageLabel = new();
    private readonly TextBox _storage = UiTheme.Input(140);

    // Actions
    private readonly Button _accept = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _acceptReserved = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _reject = UiTheme.SecondaryButton(string.Empty);
    private readonly ComboBox _rejectReason = new();
    private readonly CheckBox _requestRecollection = new();
    private readonly Label _status = new();

    // Pending list
    private readonly DataGridView _awaiting = UiTheme.Grid();
    private readonly Label _awaitingHeading = new();

    private SampleReceptionCandidate? _candidate;

    // Set while an accept or reject for the displayed tube is being recorded. A scan arriving then
    // is held and shown once the decision is in, rather than replacing the tube being decided.
    private bool _deciding;
    private string? _queuedScan;

    public SampleReceptionView(SampleWorkflowService workflow, ISampleLabelRenderer labels, ILabelPrinter labelPrinter, IDocumentOutput documents,
                              IUserDialogs dialogs, ILocalizer localizer,
                              ICultureManager cultureManager,
                              ILogger<SampleReceptionView>? logger = null)
        : base(localizer, cultureManager)
    {
        _workflow = workflow;
        _labels = labels;
        _labelPrinter = labelPrinter;
        _documents = documents;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.SampleReception");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        // --- scan bar -----------------------------------------------------------------------------

        var scanBar = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = UiTheme.Surface };

        _scanLabel.Font = UiTheme.Small;
        _scanLabel.ForeColor = UiTheme.TextSecondary;
        _scanLabel.AutoSize = true;
        _scanLabel.Location = new Point(0, 4);

        _scan.Tag = UiTags.LatinValue;
        _scan.Font = new Font("Consolas", 12f);
        _scan.Location = new Point(0, 22);
        _scan.Height = 30;

        scanBar.Controls.AddRange([_scanLabel, _scan]);

        // --- candidate detail ----------------------------------------------------------------------

        _detail.Dock = DockStyle.Top;
        _detail.Height = 104;
        _detail.BackColor = UiTheme.SurfaceRaised;
        _detail.Padding = new Padding(UiTheme.Gutter, UiTheme.GutterTight, UiTheme.Gutter, 0);
        _detail.Visible = false;

        _patientLine.Font = UiTheme.SectionTitle;
        _patientLine.ForeColor = UiTheme.TextPrimary;
        _patientLine.Dock = DockStyle.Top;
        _patientLine.Height = 26;

        _orderLine.Font = UiTheme.Body;
        _orderLine.ForeColor = UiTheme.TextSecondary;
        _orderLine.Dock = DockStyle.Top;
        _orderLine.Height = 20;

        _tubeLine.Font = UiTheme.Body;
        _tubeLine.ForeColor = UiTheme.TextSecondary;
        _tubeLine.Dock = DockStyle.Top;
        _tubeLine.Height = 20;

        // The computed verdict, in its own emphasised band.
        _delayLine.Font = UiTheme.BodyBold;
        _delayLine.Dock = DockStyle.Top;
        _delayLine.Height = 22;

        _detail.Controls.Add(_delayLine);
        _detail.Controls.Add(_tubeLine);
        _detail.Controls.Add(_orderLine);
        _detail.Controls.Add(_patientLine);

        // --- conformity panel ----------------------------------------------------------------------

        var conformity = new Panel
        {
            Dock = DockStyle.Top, Height = 196,
            BackColor = UiTheme.Surface, Padding = new Padding(0, UiTheme.GutterTight, 0, 0)
        };

        _defectBox.Text = string.Empty;
        _defectBox.Font = UiTheme.Small;
        _defectBox.ForeColor = UiTheme.TextSecondary;
        _defectBox.Bounds = new Rectangle(0, 4, 520, 150);

        BuildDefectChecks();

        _temperatureLabel.Font = UiTheme.Small;
        _temperatureLabel.ForeColor = UiTheme.TextSecondary;
        _temperatureLabel.AutoSize = true;
        _temperatureLabel.Location = new Point(540, 10);

        _temperature.Location = new Point(540, 28);
        _temperature.Tag = UiTags.LatinValue;

        _coldChain.Font = UiTheme.Small;
        _coldChain.ForeColor = UiTheme.TextSecondary;
        _coldChain.AutoSize = true;
        _coldChain.Location = new Point(540, 62);
        _coldChain.Checked = true;

        _storageLabel.Font = UiTheme.Small;
        _storageLabel.ForeColor = UiTheme.TextSecondary;
        _storageLabel.AutoSize = true;
        _storageLabel.Location = new Point(540, 92);

        _storage.Location = new Point(540, 110);

        _accept.Bounds = new Rectangle(0, 160, 180, UiTheme.ButtonHeight);
        _acceptReserved.Bounds = new Rectangle(190, 160, 220, UiTheme.ButtonHeight);
        _reject.Bounds = new Rectangle(420, 160, 140, UiTheme.ButtonHeight);

        _rejectReason.DropDownStyle = ComboBoxStyle.DropDownList;
        _rejectReason.FlatStyle = FlatStyle.Flat;
        _rejectReason.Font = UiTheme.Small;
        _rejectReason.Bounds = new Rectangle(570, 160, 260, UiTheme.InputHeight);

        _requestRecollection.Font = UiTheme.Small;
        _requestRecollection.ForeColor = UiTheme.TextSecondary;
        _requestRecollection.AutoSize = true;
        _requestRecollection.Location = new Point(840, 164);
        _requestRecollection.Checked = true;

        conformity.Controls.AddRange([_defectBox, _temperatureLabel, _temperature, _coldChain,
                                      _storageLabel, _storage,
                                      _accept, _acceptReserved, _reject,
                                      _rejectReason, _requestRecollection]);

        // --- awaiting list --------------------------------------------------------------------------

        _awaitingHeading.Font = UiTheme.SmallBold;
        _awaitingHeading.ForeColor = UiTheme.TextSecondary;
        _awaitingHeading.Dock = DockStyle.Top;
        _awaitingHeading.Height = 24;

        BuildAwaitingColumns();

        var awaitingHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        awaitingHost.Controls.Add(_awaiting);
        awaitingHost.Controls.Add(_awaitingHeading);

        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = UiTheme.Surface };
        _status.Dock = DockStyle.Fill;
        _status.Font = UiTheme.Small;
        _status.ForeColor = UiTheme.TextSecondary;
        statusBar.Controls.Add(_status);

        Controls.Add(awaitingHost);
        Controls.Add(statusBar);
        Controls.Add(conformity);
        Controls.Add(_detail);
        Controls.Add(scanBar);

        SetActionsEnabled(false);
    }

    /// <summary>
    /// One checkbox per observable defect.
    /// <para>
    /// Every member of the flags enum is offered, because the rejection and reservation statistics
    /// are only meaningful if the operator can record what they actually saw rather than picking the
    /// nearest of three options.
    /// </para>
    /// </summary>
    private void BuildDefectChecks()
    {
        _defectChecks.Clear();
        _defectBox.Controls.Clear();

        var defects = Enum.GetValues<SampleDefect>()
                          .Where(d => d != SampleDefect.None)
                          .ToList();

        var column = 0;
        var row = 0;

        foreach (var defect in defects)
        {
            var check = new CheckBox
            {
                Tag = defect,
                AutoSize = true,
                Font = UiTheme.Small,
                ForeColor = UiTheme.TextPrimary,
                Location = new Point(12 + column * 250, 20 + row * 22)
            };

            check.CheckedChanged += (_, _) => RefreshActionAvailability();

            _defectChecks[defect] = check;
            _defectBox.Controls.Add(check);

            row++;
            if (row >= 6) { row = 0; column++; }
        }
    }

    private void BuildAwaitingColumns()
    {
        _awaiting.Columns.Clear();

        void Add(string name, int weight, Font? font = null,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
            => _awaiting.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body, Alignment = align
                }
            });

        Add("barcode", 13, UiTheme.Monospace);
        Add("accession", 13, UiTheme.Monospace);
        Add("patient", 22);
        Add("specimen", 14, UiTheme.Small);
        Add("priority", 9);
        Add("collected", 10, UiTheme.Monospace);
        Add("elapsed", 9, UiTheme.MonospaceBold, DataGridViewContentAlignment.MiddleRight);
        Add("verdict", 10, UiTheme.SmallBold);
    }

    private void WireBehaviour()
    {
        // A scanner terminates its scan with Enter.
        _scan.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await ScanAsync(_scan.Text);
        };

        // Writes run through RunBusyAsync, as on the billing screens: a double-click records one
        // action, and leaving the screen mid-write no longer disposes its context under it.
        _accept.Click += async (_, _) => await RunBusyAsync(() => AcceptAsync(withReservation: false));
        _acceptReserved.Click += async (_, _) => await RunBusyAsync(() => AcceptAsync(withReservation: true));
        _reject.Click += async (_, _) => await RunBusyAsync(RejectAsync);

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Samples.Receive, _accept, _acceptReserved);
        PermissionGate.Require(Permissions.Samples.Reject, _reject);

        _awaiting.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex < 0) return;
            var barcode = _awaiting.Rows[e.RowIndex].Cells["barcode"].Value?.ToString();
            if (!string.IsNullOrWhiteSpace(barcode)) await ScanAsync(barcode);
        };

        _awaiting.CellFormatting += OnAwaitingFormatting;
    }

    private void OnAwaitingFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _awaiting.Rows.Count) return;
        if (_awaiting.Rows[e.RowIndex].Tag is not SampleReceptionCandidate candidate) return;

        if (candidate.StabilityExceeded)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (candidate.ExistingDefects != SampleDefect.None)
        {
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
        else if (candidate.Priority != OrderPriority.Routine)
        {
            e.CellStyle!.ForeColor = UiTheme.ForPriority(candidate.Priority);
        }
    }

    protected override void ApplyTexts()
    {
        _scanLabel.Text = T("Sample.ScanPrompt");
        _defectBox.Text = T("Sample.Defects");
        _temperatureLabel.Text = T("Sample.Temperature");
        _coldChain.Text = T("Sample.ColdChain");
        _storageLabel.Text = T("Sample.Storage");
        _accept.Text = T("Action.Accept");
        _acceptReserved.Text = T("Action.AcceptWithReservation");
        _reject.Text = T("Action.Reject");
        _requestRecollection.Text = T("Sample.RequestRecollection");
        _awaitingHeading.Text = T("Sample.AwaitingReception");

        foreach (var (defect, check) in _defectChecks)
            check.Text = T($"SampleDefect.{defect}");

        var selectedReason = _rejectReason.SelectedItem as ReasonChoice;

        _rejectReason.BeginUpdate();
        _rejectReason.Items.Clear();

        foreach (var reason in Enum.GetValues<SampleRejectionReason>())
            _rejectReason.Items.Add(new ReasonChoice(reason, T($"RejectionReason.{reason}")));

        // No reason preselected: the reason is counted monthly as a quality indicator, and a
        // refusal confirmed too quickly was filed under the first one (identity mismatch).
        _rejectReason.SelectedIndex = -1;

        if (selectedReason is not null)
        {
            for (var i = 0; i < _rejectReason.Items.Count; i++)
                if (_rejectReason.Items[i] is ReasonChoice choice
                    && choice.Value == selectedReason.Value)
                    _rejectReason.SelectedIndex = i;
        }

        _rejectReason.EndUpdate();

        if (_awaiting.Columns.Count > 0)
        {
            _awaiting.Column("barcode").HeaderText = T("Sample.Barcode");
            _awaiting.Column("accession").HeaderText = T("Order.AccessionNumber");
            _awaiting.Column("patient").HeaderText = T("Patient.Title");
            _awaiting.Column("specimen").HeaderText = T("Sample.SpecimenType");
            _awaiting.Column("priority").HeaderText = T("Field.Priority");
            _awaiting.Column("collected").HeaderText = T("Sample.CollectedAt");
            _awaiting.Column("elapsed").HeaderText = T("Sample.TransportDelay");
            _awaiting.Column("verdict").HeaderText = T("Field.Status");
        }

        if (_candidate is not null) ShowCandidate(_candidate);
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        await RefreshAwaitingAsync(ct);
        _scan.Focus();
    }

    private async Task RefreshAwaitingAsync(CancellationToken ct = default)
    {
        try
        {
            var awaiting = await _workflow.GetAwaitingReceptionAsync(ct);

            if (awaiting.IsFailure) return;

            _awaiting.SuspendLayout();
            _awaiting.Rows.Clear();

            foreach (var candidate in awaiting.Value)
            {
                var index = _awaiting.Rows.Add(
                    candidate.Barcode,
                    candidate.AccessionNumber,
                    candidate.PatientName,
                    candidate.SpecimenTypeName.For(Localizer.CurrentLanguage),
                    candidate.Priority == OrderPriority.Routine
                        ? string.Empty
                        : T($"Priority.{candidate.Priority}"),
                    candidate.CollectedAt?.ToString("dd/MM HH:mm") ?? string.Empty,
                    FormatElapsed(candidate.Elapsed),
                    candidate.StabilityExceeded
                        ? T("SampleDefect.ExcessiveDelay")
                        : candidate.ExistingDefects != SampleDefect.None
                            ? T("Sample.Defects")
                            : T("SampleStatus." + candidate.Status));

                _awaiting.Rows[index].Tag = candidate;
            }

            _awaiting.ResumeLayout(performLayout: true);

            _awaitingHeading.Text =
                $"{T("Sample.AwaitingReception")} — {awaiting.Value.Count}";
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the reception list failed.");
        }
    }

    private static string FormatElapsed(TimeSpan? elapsed) =>
        elapsed is not { } value
            ? string.Empty
            : value.TotalHours >= 1
                ? $"{(int)value.TotalHours}h{value.Minutes:D2}"
                : $"{value.Minutes} min";

    // ---- Scan ----------------------------------------------------------------------------------------

    private async Task ScanAsync(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return;

        if (_deciding)
        {
            _queuedScan = barcode.Trim();
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Sample.ScanQueued", _queuedScan);
            _scan.SelectAll();
            return;
        }

        var result = await _workflow.ScanAsync(barcode);

        if (result.IsFailure)
        {
            ClearCandidate();

            _status.ForeColor = UiTheme.Critical;
            _status.Text = Describe(result);

            _scan.SelectAll();
            _scan.Focus();
            return;
        }

        _candidate = result.Value;

        // Every tube starts from blank inputs: the ticks, temperature and storage place entered for
        // the previous one say nothing about this one.
        ResetTubeInputs();
        ShowCandidate(_candidate);

        _scan.Clear();
        _scan.Focus();
    }

    private void ShowCandidate(SampleReceptionCandidate candidate)
    {
        _detail.Visible = true;

        var identity = new List<string>
        {
            candidate.PatientName,
            T($"Gender.{candidate.Gender}")
        };

        if (candidate.DateOfBirth is { } dob) identity.Add(dob.ToString("dd/MM/yyyy"));

        _patientLine.Text = string.Join("   ·   ", identity);

        var orderInfo = new List<string> { candidate.AccessionNumber };

        if (candidate.Priority != OrderPriority.Routine)
            orderInfo.Add(T($"Priority.{candidate.Priority}").ToUpperInvariant());

        orderInfo.Add(string.Join(" ", candidate.AnalysisCodes));

        _orderLine.Text = string.Join("   ·   ", orderInfo);

        var tubeInfo = new List<string>
        {
            candidate.Barcode,
            candidate.SpecimenTypeName.For(Localizer.CurrentLanguage)
        };

        if (candidate.ContainerName is { } container)
            tubeInfo.Add(container.For(Localizer.CurrentLanguage));

        if (candidate.VolumeMl is { } volume) tubeInfo.Add($"{volume:N1} mL");

        _tubeLine.Text = string.Join("   ·   ", tubeInfo);

        // The verdict, computed rather than left to the operator to infer.
        if (candidate.StabilityExceeded)
        {
            _delayLine.Text = $"⚠ {T("SampleDefect.ExcessiveDelay")} — " +
                              $"{FormatElapsed(candidate.Elapsed)} > {candidate.ShortestStabilityHours} h";
            _delayLine.ForeColor = UiTheme.Critical;

            // Pre-tick the defect: the operator still decides, but is not left to notice it.
            if (_defectChecks.TryGetValue(SampleDefect.ExcessiveDelay, out var check))
                check.Checked = true;
        }
        else if (candidate.Elapsed is { } elapsed)
        {
            _delayLine.Text = $"{T("Sample.TransportDelay")} : {FormatElapsed(elapsed)}" +
                              (candidate.ShortestStabilityHours is { } hours
                                  ? $" / {hours} h"
                                  : string.Empty);
            _delayLine.ForeColor = UiTheme.Normal;
        }
        else
        {
            _delayLine.Text = T("Sample_NotCollected");
            _delayLine.ForeColor = UiTheme.Abnormal;
        }

        // Volume already recorded as insufficient at the draw.
        if (candidate.VolumeMl is { } drawn && candidate.MinimumVolumeMl is { } minimum
            && drawn < minimum
            && _defectChecks.TryGetValue(SampleDefect.InsufficientVolume, out var volumeCheck))
            volumeCheck.Checked = true;

        // Defects already recorded upstream are shown as ticked, so reception cannot unknowingly
        // accept a tube the phlebotomist already flagged.
        foreach (var (defect, check) in _defectChecks)
            if (candidate.ExistingDefects.HasFlag(defect)) check.Checked = true;

        _coldChain.Visible = candidate.RequiresColdChain;
        _temperature.Visible = candidate.RequiresColdChain;
        _temperatureLabel.Visible = candidate.RequiresColdChain;

        SetActionsEnabled(true);
        RefreshActionAvailability();

        // Scanned again after reception or after a refusal: say where the tube stands.
        if (candidate.Status is not (SampleStatus.Collected or SampleStatus.InTransit
                                     or SampleStatus.AwaitingCollection))
        {
            _status.ForeColor = UiTheme.Abnormal;
            _status.Text = $"{candidate.Barcode} — {T("SampleStatus." + candidate.Status)}";
        }
    }

    private void ClearCandidate()
    {
        _candidate = null;
        _detail.Visible = false;

        ResetTubeInputs();

        SetActionsEnabled(false);
    }

    /// <summary>
    /// Puts the per-tube inputs back to their defaults. Not done in <see cref="ShowCandidate"/>,
    /// which also re-renders the current tube after a language change and must keep what the
    /// operator has entered for it.
    /// </summary>
    private void ResetTubeInputs()
    {
        foreach (var check in _defectChecks.Values) check.Checked = false;

        _temperature.Clear();
        _coldChain.Checked = true;
        _storage.Clear();
        _requestRecollection.Checked = true;

        _rejectReason.SelectedIndex = -1;
    }

    /// <summary>
    /// Ends a decision: re-enables the actions if the tube is still displayed (the decision was
    /// refused), then shows a tube scanned in the meantime.
    /// </summary>
    private async Task EndDecisionAsync(SampleReceptionCandidate decided)
    {
        _deciding = false;

        if (ReferenceEquals(_candidate, decided))
        {
            SetActionsEnabled(true);
            RefreshActionAvailability();
        }

        if (_queuedScan is { } queued)
        {
            _queuedScan = null;
            await ScanAsync(queued);
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        _accept.Enabled = enabled;
        _acceptReserved.Enabled = enabled;
        _reject.Enabled = enabled;
        _rejectReason.Enabled = enabled;
    }

    /// <summary>
    /// Keeps the two acceptance buttons honest: a tube with a recorded defect cannot be accepted by
    /// the plain button, and a conforming tube does not offer the reservation path.
    /// </summary>
    private void RefreshActionAvailability()
    {
        if (_candidate is null) return;

        var hasDefects = SelectedDefects() != SampleDefect.None;

        _accept.Enabled = !hasDefects;
        _acceptReserved.Enabled = hasDefects;

        _acceptReserved.ForeColor = hasDefects ? UiTheme.Abnormal : UiTheme.TextSecondary;
        _acceptReserved.Font = hasDefects ? UiTheme.BodyBold : UiTheme.Body;

        // A tube already on the bench can still be refused (a clot found after centrifugation),
        // but not received a second time; a refused or discarded tube admits neither.
        if (_candidate.Status is SampleStatus.Received or SampleStatus.InAnalysis or SampleStatus.Stored)
        {
            _accept.Enabled = false;
            _acceptReserved.Enabled = false;
        }
        else if (_candidate.Status is SampleStatus.Rejected or SampleStatus.Discarded)
        {
            SetActionsEnabled(false);
        }
    }

    private SampleDefect SelectedDefects()
    {
        var defects = SampleDefect.None;

        foreach (var (defect, check) in _defectChecks)
            if (check.Checked) defects |= defect;

        return defects;
    }

    // ---- Decisions -------------------------------------------------------------------------------------

    private async Task AcceptAsync(bool withReservation)
    {
        // The tube being decided, held locally: a scan may replace _candidate before this returns.
        if (_deciding || _candidate is not { } candidate) return;

        var defects = SelectedDefects();

        if (defects != SampleDefect.None && !withReservation)
        {
            // Should be unreachable given RefreshActionAvailability, but the domain refuses it too
            // and the operator deserves the explanation rather than a silent no-op.
            _dialogs.Warn(T("Sample_DefectsRequireExplicitDecision", defects));
            return;
        }

        decimal? temperature = decimal.TryParse(
            _temperature.Text.Replace(',', '.'),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;

        var request = new ReceiveSampleRequest
        {
            SampleId = candidate.SampleId,
            ObservedDefects = defects,
            AcceptWithReservation = withReservation,
            TemperatureCelsius = temperature,
            ColdChainRespected = candidate.RequiresColdChain ? _coldChain.Checked : null,
            StorageLocation = string.IsNullOrWhiteSpace(_storage.Text) ? null : _storage.Text.Trim()
        };

        _deciding = true;
        SetActionsEnabled(false);
        Cursor = Cursors.WaitCursor;

        try
        {
            var result = await _workflow.ReceiveAsync([request]);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            if (result.Value.Problems.Count > 0)
            {
                _status.ForeColor = UiTheme.Abnormal;
                _status.Text = Localizer.Describe(result.Value.Problems);
            }
            else
            {
                _status.ForeColor = withReservation ? UiTheme.Abnormal : UiTheme.Normal;
                _status.Text = withReservation
                    ? $"{candidate.Barcode} — {T("Action.AcceptWithReservation")}"
                    : $"{candidate.Barcode} — {T("SampleStatus.Received")}";
            }

            if (ReferenceEquals(_candidate, candidate)) ClearCandidate();
            await RefreshAwaitingAsync();
        }
        finally
        {
            Cursor = Cursors.Default;
            _scan.Focus();
            await EndDecisionAsync(candidate);
        }
    }

    private async Task RejectAsync()
    {
        // The tube being decided, held locally: a scan may replace _candidate before this returns.
        if (_deciding || _candidate is not { } candidate) return;

        if (_rejectReason.SelectedItem is not ReasonChoice reason)
        {
            _dialogs.Warn(T("Sample_RejectionReasonRequired"), T("Action.Reject"));
            _rejectReason.Focus();
            return;
        }

        var comment = _dialogs.PromptForText(
            $"{candidate.Barcode} — {reason.Display}\n\n{T("Field.Comment")}",
            T("Action.Reject"));

        if (comment is null) return;

        if (!_dialogs.Confirm(
                T("Sample.ConfirmRejection", candidate.Barcode, reason.Display),
                T("Action.Reject")))
            return;

        _deciding = true;
        SetActionsEnabled(false);
        Cursor = Cursors.WaitCursor;

        try
        {
            var result = await _workflow.RejectAsync(
            [
                new RejectSampleRequest(candidate.SampleId, reason.Value, comment,
                                        _requestRecollection.Checked)
            ]);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            // Refused by the rules (already refused, nothing left to redraw): nothing was recorded.
            if (result.Value.Rejected == 0)
            {
                _dialogs.Warn(Localizer.Describe(result.Value.Problems), T("Action.Reject"));
                return;
            }

            // A recollection produces a fresh tube with a new barcode — never a reuse of the
            // rejected one — so its label is printed immediately.
            if (result.Value.ReplacementLabels.Count > 0)
            {
                await PrintReplacementLabelsAsync(result.Value.ReplacementLabels);

                _status.ForeColor = UiTheme.Abnormal;
                _status.Text = $"{candidate.Barcode} — {T("SampleStatus.Rejected")} · " +
                               T("Sample.ReplacementIssued");
            }
            else
            {
                _status.ForeColor = UiTheme.Abnormal;
                _status.Text = $"{candidate.Barcode} — {T("SampleStatus.Rejected")}";
            }

            if (result.Value.Problems.Count > 0)
                _dialogs.Warn(Localizer.Describe(result.Value.Problems));

            if (ReferenceEquals(_candidate, candidate)) ClearCandidate();
            await RefreshAwaitingAsync();
        }
        finally
        {
            Cursor = Cursors.Default;
            _scan.Focus();
            await EndDecisionAsync(candidate);
        }
    }

    private async Task PrintReplacementLabelsAsync(
        IReadOnlyList<Application.Orders.SampleLabel> labels)
    {
        try
        {
            var emitted = await LabelOutput.EmitAsync(_labelPrinter, _documents, _labels.Render, labels);

            if (emitted.IsFailure) _dialogs.ShowFailure(emitted, T("Action.PrintLabels"));
        }
        catch (Exception ex)
        {
            // The rejection and the replacement tube are already recorded; a label failure must
            // not undo them. The label can be reprinted from the collection screen.
            _logger?.LogError(ex, "Printing the replacement label failed.");
            _dialogs.Warn(T("Action.PrintLabels") + " — " + T("Message.UnexpectedError"));
        }
    }

    private sealed record ReasonChoice(SampleRejectionReason Value, string Display)
    {
        public override string ToString() => Display;
    }
}
