using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Samples;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The collection worklist.
/// <para>
/// Organised around one fact: the phlebotomist works on <b>one patient at a time</b>, draws all
/// their tubes together, and needs the order of draw right. So selecting any tube selects that
/// patient's whole set, the set is sorted by draw order rather than alphabetically, and the cap
/// colour of each is shown — because additive carry-over between containers drawn out of order is
/// a real pre-analytical error, and the colour is what the operator actually matches against the
/// rack.
/// </para>
/// <para>
/// Fasting and patient alerts are stated <em>before</em> the draw, at the top of the panel. Asking
/// afterwards is useless: a biologist interpreting a glycaemia needs to know whether the patient
/// had eaten, and the only person who can honestly ask is the one at the chair.
/// </para>
/// </summary>
public sealed class CollectionView : LocalizedView
{
    private const string ColSelected = "selected";
    private const string ColDrawOrder = "draworder";
    private const string ColTube = "tube";
    private const string ColContainer = "container";
    private const string ColSpecimen = "specimen";
    private const string ColBarcode = "barcode";
    private const string ColVolume = "volume";
    private const string ColAnalyses = "analyses";
    private const string ColState = "state";

    private readonly SampleWorkflowService _workflow;
    private readonly ISampleLabelRenderer _labels;
    private readonly ILabelPrinter _labelPrinter;
    private readonly IDocumentOutput _documents;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<CollectionView>? _logger;

    private readonly DataGridView _patients = UiTheme.Grid();
    private readonly DataGridView _tubes = UiTheme.Grid();
    private readonly Label _patientsHeading = new();
    private readonly Label _patientBanner = new();
    private readonly Label _alertBanner = new();
    private readonly Label _fastingBanner = new();
    private readonly Panel _bannerHost = new();
    private readonly CheckBox _onlyUrgent = new();
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _record = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _dispatch = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _reprint = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new();

    private IReadOnlyList<CollectionWorklistEntry> _entries = [];
    private IReadOnlyList<CollectionWorklistEntry> _currentSet = [];

    public CollectionView(SampleWorkflowService workflow, ISampleLabelRenderer labels, ILabelPrinter labelPrinter, IDocumentOutput documents,
                          IUserDialogs dialogs, ILocalizer localizer, ICultureManager cultureManager,
                          ILogger<CollectionView>? logger = null)
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

    public override string Title => T("Nav.Collection");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 540,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        // --- left: patients awaiting a draw -------------------------------------------------------

        var left = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };

        _patientsHeading.Font = UiTheme.SectionTitle;
        _patientsHeading.ForeColor = UiTheme.TextPrimary;
        _patientsHeading.Dock = DockStyle.Top;
        _patientsHeading.Height = 26;

        _onlyUrgent.Font = UiTheme.Small;
        _onlyUrgent.ForeColor = UiTheme.TextSecondary;
        _onlyUrgent.Dock = DockStyle.Top;
        _onlyUrgent.Height = 22;

        BuildPatientColumns();

        var patientHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        patientHost.Controls.Add(_patients);

        left.Controls.Add(patientHost);
        left.Controls.Add(_onlyUrgent);
        left.Controls.Add(_patientsHeading);

        // --- right: the selected patient's tubes --------------------------------------------------

        var right = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };

        _bannerHost.Dock = DockStyle.Top;
        _bannerHost.Height = 100;
        _bannerHost.BackColor = UiTheme.SurfaceRaised;
        _bannerHost.Padding = new Padding(UiTheme.Gutter, UiTheme.GutterTight, UiTheme.Gutter, 0);

        _patientBanner.Font = UiTheme.SectionTitle;
        _patientBanner.ForeColor = UiTheme.TextPrimary;
        _patientBanner.Dock = DockStyle.Top;
        _patientBanner.Height = 48;   // two lines: the accession number must not be cut off

        // Fasting and alerts are their own full-width bands so they cannot be skimmed past.
        _fastingBanner.Font = UiTheme.BodyBold;
        _fastingBanner.Dock = DockStyle.Top;
        _fastingBanner.Height = 22;
        _fastingBanner.Visible = false;

        _alertBanner.Font = UiTheme.BodyBold;
        _alertBanner.ForeColor = UiTheme.Critical;
        _alertBanner.Dock = DockStyle.Top;
        _alertBanner.Height = 22;
        _alertBanner.Visible = false;

        _bannerHost.Controls.Add(_alertBanner);
        _bannerHost.Controls.Add(_fastingBanner);
        _bannerHost.Controls.Add(_patientBanner);

        BuildTubeColumns();

        var tubeHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            Padding = new Padding(0, UiTheme.GutterTight, 0, 0)
        };
        tubeHost.Controls.Add(_tubes);

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = UiTheme.Surface };

        _record.Bounds = new Rectangle(0, 10, 220, UiTheme.ButtonHeight);
        _dispatch.Bounds = new Rectangle(230, 10, 210, UiTheme.ButtonHeight);
        _reprint.Bounds = new Rectangle(450, 10, 190, UiTheme.ButtonHeight);
        _refresh.Bounds = new Rectangle(650, 10, 110, UiTheme.ButtonHeight);

        _status.Bounds = new Rectangle(770, 16, 520, 20);
        _status.Font = UiTheme.Small;
        _status.ForeColor = UiTheme.TextSecondary;
        _status.AutoSize = false;

        actions.Controls.AddRange([_record, _dispatch, _reprint, _refresh, _status]);

        right.Controls.Add(tubeHost);
        right.Controls.Add(actions);
        right.Controls.Add(_bannerHost);

        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);

        Controls.Add(split);

        SetActionsEnabled(false);
    }

    private void BuildPatientColumns()
    {
        _patients.Columns.Clear();

        void Add(string name, int weight, Font? font = null,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
            => _patients.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body, Alignment = align
                }
            });

        Add("accession", 20, UiTheme.Monospace);
        Add("patient", 34);
        Add("age", 8, UiTheme.Body, DataGridViewContentAlignment.MiddleRight);
        Add("priority", 14);
        Add("tubes", 8, UiTheme.Body, DataGridViewContentAlignment.MiddleRight);
        Add("state", 16, UiTheme.SmallBold);
        Add("waiting", 16, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);

        // The name is what the operator reads aloud to identify the patient: never just "Bena…".
        _patients.Columns["patient"]!.MinimumWidth = 140;
        _patients.Columns["state"]!.MinimumWidth = 100;
    }

    private void BuildTubeColumns()
    {
        _tubes.Columns.Clear();
        _tubes.ReadOnly = false;
        _tubes.EditMode = DataGridViewEditMode.EditOnEnter;

        _tubes.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = ColSelected, FillWeight = 5,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        _tubes.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColDrawOrder, FillWeight = 6, ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = UiTheme.MonospaceBold,
                Alignment = DataGridViewContentAlignment.MiddleCenter
            }
        });

        // A narrow colour swatch: the fastest check the operator makes against the rack.
        _tubes.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = ColContainer, FillWeight = 4, ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        void Add(string name, int weight, Font? font = null, bool readOnly = true,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
            => _tubes.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = readOnly,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body, Alignment = align
                }
            });

        Add(ColTube, 20);
        Add(ColSpecimen, 18, UiTheme.Small);
        Add(ColBarcode, 16, UiTheme.Monospace);
        Add(ColState, 14, UiTheme.Small);
        Add(ColVolume, 11, UiTheme.MonospaceBold, readOnly: false,
            DataGridViewContentAlignment.MiddleRight);
        Add(ColAnalyses, 20, UiTheme.Small);

        // Drawn or not, and which tube, must be readable without widening anything.
        _tubes.Columns[ColState]!.MinimumWidth = 105;
        _tubes.Columns[ColTube]!.MinimumWidth = 110;
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();
        _onlyUrgent.CheckedChanged += async (_, _) => await ActivateAsync();

        _patients.SelectionChanged += (_, _) => ShowSelectedPatient();

        // Writes run through RunBusyAsync, as on the billing screens: a double-click records one
        // action, and leaving the screen mid-write no longer disposes its context under it.
        _record.Click += async (_, _) => await RunBusyAsync(() => RecordAsync(dispatch: false));
        _dispatch.Click += async (_, _) => await RunBusyAsync(() => RecordAsync(dispatch: true));
        _reprint.Click += async (_, _) => await RunBusyAsync(ReprintAsync);

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Samples.Collect, _record);
        PermissionGate.Require(Permissions.Samples.Dispatch, _dispatch);
        PermissionGate.Require(Permissions.Samples.Reprint, _reprint);

        _tubes.CellPainting += OnTubeCellPainting;
        _patients.CellFormatting += OnPatientFormatting;
    }

    /// <summary>
    /// Paints the cap-colour swatch. Drawn rather than coloured text, because the colour <em>is</em>
    /// the information here and a coloured glyph would be lost on a monochrome or low-contrast screen
    /// — the tube name in the adjacent column carries the same fact in words.
    /// </summary>
    private void OnTubeCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_tubes.Columns[e.ColumnIndex].Name != ColContainer) return;
        if (_tubes.Rows[e.RowIndex].Tag is not Guid id) return;

        var entry = _currentSet.FirstOrDefault(x => x.SampleId == id);
        if (entry is null) return;

        e.PaintBackground(e.CellBounds, true);

        var colour = ParseColour(entry.ContainerColourHex);
        var box = new Rectangle(e.CellBounds.X + 6, e.CellBounds.Y + 4,
                                Math.Max(8, e.CellBounds.Width - 12),
                                Math.Max(6, e.CellBounds.Height - 8));

        using var brush = new SolidBrush(colour);
        using var pen = new Pen(UiTheme.BorderStrong);

        e.Graphics!.FillRectangle(brush, box);
        e.Graphics.DrawRectangle(pen, box);

        e.Handled = true;
    }

    private static Color ParseColour(string hex)
    {
        try
        {
            return ColorTranslator.FromHtml(hex);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // A malformed colour in the catalogue must not stop a draw.
            return UiTheme.SurfaceSunken;
        }
    }

    private void OnPatientFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _patients.Rows.Count) return;
        if (_patients.Rows[e.RowIndex].Tag is not Guid orderId) return;

        var set = _entries.Where(x => x.LabOrderId == orderId).ToList();
        if (set.Count == 0) return;

        if (set[0].Priority != OrderPriority.Routine)
        {
            e.CellStyle!.ForeColor = UiTheme.ForPriority(set[0].Priority);
            e.CellStyle.Font = new Font(e.CellStyle.Font ?? UiTheme.Body, FontStyle.Bold);
        }

        if (set.Any(x => x.PatientAlerts.Count > 0))
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
    }

    protected override void ApplyTexts()
    {
        _patientsHeading.Text = T("Nav.Collection");
        _onlyUrgent.Text = T("Priority.Urgent");
        _refresh.Text = T("Action.Refresh");
        _record.Text = T("Action.Collect");
        _dispatch.Text = T("Action.Dispatch");
        _reprint.Text = T("Action.PrintLabels");

        if (_patients.Columns.Count > 0)
        {
            _patients.Column("accession").HeaderText = T("Order.AccessionNumber");
            _patients.Column("patient").HeaderText = T("Patient.Title");
            _patients.Column("age").HeaderText = T("Patient.Age");
            _patients.Column("priority").HeaderText = T("Field.Priority");
            _patients.Column("tubes").HeaderText = T("Collection.TubeCount");
            _patients.Column("state").HeaderText = T("Field.Status");
            _patients.Column("waiting").HeaderText = T("Collection.Registered");
        }

        if (_tubes.Columns.Count > 0)
        {
            _tubes.Column(ColSelected).HeaderText = string.Empty;
            _tubes.Column(ColDrawOrder).HeaderText = "#";
            _tubes.Column(ColContainer).HeaderText = string.Empty;
            _tubes.Column(ColTube).HeaderText = T("Sample.ContainerType");
            _tubes.Column(ColSpecimen).HeaderText = T("Sample.SpecimenType");
            _tubes.Column(ColBarcode).HeaderText = T("Sample.Barcode");
            _tubes.Column(ColState).HeaderText = T("Field.Status");
            _tubes.Column(ColVolume).HeaderText = T("Sample.Volume");
            _tubes.Column(ColAnalyses).HeaderText = T("Catalog.Analyses");
        }

        if (_entries.Count > 0) RenderPatients();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _workflow.GetCollectionWorklistAsync(_onlyUrgent.Checked, ct);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            _entries = result.Value;
            RenderPatients();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer navigation.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the collection worklist failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private void RenderPatients()
    {
        var previous = SelectedOrderId();

        _patients.SuspendLayout();
        _patients.Rows.Clear();

        // One row per order, not per tube: the operator picks a patient, not a tube.
        var byOrder = _entries
            .GroupBy(e => e.LabOrderId)
            .OrderByDescending(g => g.First().Priority)
            .ThenBy(g => g.First().RegisteredAt)
            .ToList();

        foreach (var group in byOrder)
        {
            var first = group.First();

            var index = _patients.Rows.Add(
                first.AccessionNumber,
                Localizer.CurrentLanguage == Domain.Common.AppLanguage.Arabic
                    && first.PatientNameArabic is { Length: > 0 }
                        ? first.PatientNameArabic
                        : first.PatientName,
                first.AgeYears?.ToString() ?? string.Empty,
                first.Priority == OrderPriority.Routine
                    ? string.Empty
                    : T($"Priority.{first.Priority}"),
                group.Count().ToString(),
                string.Empty,
                first.RegisteredAt.ToString("HH:mm"));

            var patientRow = _patients.Rows[index];
            patientRow.Tag = first.LabOrderId;

            var toDraw = group.Count(e => !e.IsCollected);
            var stateCell = patientRow.Cells["state"];

            if (toDraw > 0)
            {
                stateCell.Value = T("Collection.ToDraw", toDraw);
                stateCell.Style.ForeColor = UiTheme.Abnormal;
            }
            else
            {
                stateCell.Value = T("Collection.ReadyToDispatch");
                stateCell.Style.ForeColor = UiTheme.Normal;
                patientRow.DefaultCellStyle.BackColor = UiTheme.NormalBackground;
            }
        }

        _patients.ResumeLayout(performLayout: true);

        _status.ForeColor = UiTheme.TextSecondary;
        _status.Text = $"{byOrder.Count} {T("Patient.Plural").ToLowerInvariant()} · " +
                       $"{_entries.Count} {T("Sample.Plural").ToLowerInvariant()}";

        if (_patients.Rows.Count == 0)
        {
            ClearPatientPanel();
            return;
        }

        // Keep the patient being drawn selected across a refresh; otherwise the first one. Setting
        // Selected alone highlighted the row without making it current, so the tube panel stayed
        // empty and "Prélever" disabled until the operator clicked the row again.
        var row = _patients.Rows.Cast<DataGridViewRow>()
                      .FirstOrDefault(r => r.Tag is Guid id && id == previous)
                  ?? _patients.Rows[0];

        _patients.CurrentCell = row.Cells[0];
        row.Selected = true;
        ShowSelectedPatient();
    }

    private Guid? SelectedOrderId() =>
        (_patients.CurrentRow ?? _patients.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault())?.Tag as Guid?;

    private void ShowSelectedPatient()
    {
        if (SelectedOrderId() is not { } orderId)
        {
            ClearPatientPanel();
            return;
        }

        // Selecting a tube selects the patient's whole set: they are drawn together, in draw order.
        _currentSet = [.. _entries.Where(e => e.LabOrderId == orderId).OrderBy(e => e.DrawOrder)];

        if (_currentSet.Count == 0)
        {
            ClearPatientPanel();
            return;
        }

        var first = _currentSet[0];

        var identity = new List<string>
        {
            first.PatientName,
            T($"Gender.{first.Gender}")
        };

        if (first.AgeYears is { } age) identity.Add($"{age} {T("Report.Years")}");
        if (first.DateOfBirth is { } dob) identity.Add(dob.ToString("dd/MM/yyyy"));
        identity.Add(first.AccessionNumber);

        _patientBanner.Text = string.Join("   ·   ", identity);

        // Fasting, stated before the draw.
        var fasting = _currentSet.Max(e => e.RequiredFastingHours);

        if (fasting > 0)
        {
            var declared = first.PatientDeclaredFasted;

            _fastingBanner.Text = declared switch
            {
                true => $"{T("Catalog.FastingRequired")} : {fasting} h — {T("Order.Fasted")} ✓",
                false => $"{T("Catalog.FastingRequired")} : {fasting} h — " +
                         T("SampleDefect.FastingNotRespected"),
                _ => $"{T("Catalog.FastingRequired")} : {fasting} h — {T("Collection.ConfirmFasting")}"
            };

            _fastingBanner.ForeColor = declared is false ? UiTheme.Critical : UiTheme.Abnormal;
            _fastingBanner.Visible = true;
        }
        else
        {
            _fastingBanner.Visible = false;
        }

        // Patient alerts — biohazard, difficult draw, unpaid balance.
        var alerts = _currentSet.SelectMany(e => e.PatientAlerts)
                                .Select(a => a.For(Localizer.CurrentLanguage))
                                .Distinct()
                                .ToList();

        if (alerts.Count > 0)
        {
            _alertBanner.Text = $"⚠ {string.Join("   ·   ", alerts)}";
            _alertBanner.Visible = true;
        }
        else
        {
            _alertBanner.Visible = false;
        }

        RenderTubes();
    }

    private void RenderTubes()
    {
        _tubes.SuspendLayout();
        _tubes.Rows.Clear();

        var position = 1;
        var anyAwaiting = _currentSet.Any(e => !e.IsCollected);

        foreach (var entry in _currentSet)
        {
            var handling = new List<string>();
            if (entry.RequiresLightProtection) handling.Add(T("Collection.ProtectFromLight"));
            if (entry.RequiresIceTransport) handling.Add(T("Collection.TransportOnIce"));

            // Tubes still to draw are ticked; once they are all drawn, the drawn ones are ticked
            // instead, ready for "Expédier".
            var ticked = anyAwaiting ? !entry.IsCollected : true;

            var index = _tubes.Rows.Add(
                ticked,
                position.ToString(),
                string.Empty,                           // painted swatch
                entry.ContainerName?.For(Localizer.CurrentLanguage) ?? "-",
                entry.SpecimenTypeName.For(Localizer.CurrentLanguage),
                entry.Barcode,
                entry.CollectedAt is { } drawn
                    ? $"{T("SampleStatus.Collected")} {drawn.ToLocalTime():HH:mm}"
                    : T("SampleStatus.AwaitingCollection"),
                entry.NominalVolumeMl?.ToString("N1") ?? string.Empty,
                string.Join(" ", entry.AnalysisCodes));

            var row = _tubes.Rows[index];
            row.Tag = entry.SampleId;

            // Drawn or not is the first thing read off the row: green band once drawn, amber
            // label while it is still to draw.
            if (entry.IsCollected)
            {
                row.DefaultCellStyle.BackColor = UiTheme.NormalBackground;
                row.Cells[ColState].Style.ForeColor = UiTheme.Normal;
                row.Cells[ColState].Style.Font = UiTheme.SmallBold;
                row.Cells[ColVolume].ReadOnly = true;
            }
            else
            {
                row.Cells[ColState].Style.ForeColor = UiTheme.Abnormal;
                row.Cells[ColState].Style.Font = UiTheme.SmallBold;
            }

            var tooltip = new List<string>();

            if (entry.CollectionInstructions is { } instructions)
                tooltip.Add(instructions.For(Localizer.CurrentLanguage));

            if (handling.Count > 0) tooltip.Add(string.Join(" · ", handling));

            if (entry.MinimumVolumeMl is { } minimum)
                tooltip.Add($"{T("Sample.Volume")} ≥ {minimum:N1} mL");

            if (tooltip.Count > 0)
                row.Cells[ColTube].ToolTipText = string.Join(Environment.NewLine, tooltip);

            // Special handling is flagged in the row itself, not only in a tooltip nobody hovers.
            if (handling.Count > 0)
            {
                row.Cells[ColSpecimen].Style.ForeColor = UiTheme.Abnormal;
                row.Cells[ColSpecimen].Value =
                    $"{entry.SpecimenTypeName.For(Localizer.CurrentLanguage)} — {string.Join(" · ", handling)}";
            }

            position++;
        }

        _tubes.ResumeLayout(performLayout: true);

        SetActionsEnabled(_tubes.Rows.Count > 0);
        _record.Enabled = anyAwaiting;
    }

    private void ClearPatientPanel()
    {
        _currentSet = [];
        _tubes.Rows.Clear();
        _patientBanner.Text = T("App.NoData");
        _fastingBanner.Visible = false;
        _alertBanner.Visible = false;
        SetActionsEnabled(false);
    }

    private void SetActionsEnabled(bool enabled)
    {
        _record.Enabled = enabled;
        _dispatch.Enabled = enabled;
        _reprint.Enabled = enabled;
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private List<Guid> SelectedSampleIds() =>
    [
        .. _tubes.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells[ColSelected].Value is true && r.Tag is Guid)
            .Select(r => (Guid)r.Tag!)
    ];

    /// <summary>
    /// "Prélever" records the draw of the ticked tubes; "Expédier" sends the ticked tubes, drawing
    /// first any that were not drawn yet. Drawn tubes stay on the list until they are sent or
    /// received, so the collection point can draw a series of patients and dispatch them together.
    /// </summary>
    private async Task RecordAsync(bool dispatch)
    {
        _tubes.EndEdit();

        var selected = SelectedSampleIds();

        if (selected.Count == 0)
        {
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Message.NothingSelected");
            return;
        }

        var collected = _currentSet.Where(e => e.IsCollected).Select(e => e.SampleId).ToHashSet();
        var toDraw = selected.Where(id => !collected.Contains(id)).ToList();

        if (!dispatch && toDraw.Count == 0)
        {
            _status.ForeColor = UiTheme.TextSecondary;
            _status.Text = T("Collection.AlreadyCollected");
            return;
        }

        // Where fasting matters and the patient has not been asked, ask now — before the draw is
        // recorded, because afterwards the answer cannot honestly be obtained.
        bool? fasted = null;
        var requiredFasting = _currentSet.Max(e => e.RequiredFastingHours);

        if (toDraw.Count > 0 && requiredFasting > 0 && _currentSet[0].PatientDeclaredFasted is null)
        {
            fasted = _dialogs.Confirm(
                T("Collection.FastingQuestion", requiredFasting),
                T("Catalog.FastingRequired"));
        }

        var requests = new List<RecordCollectionRequest>(selected.Count);

        foreach (DataGridViewRow row in _tubes.Rows)
        {
            if (row.Tag is not Guid id || !toDraw.Contains(id)) continue;

            var typed = row.Cells[ColVolume].Value?.ToString()?.Trim();

            decimal? volume = decimal.TryParse(
                typed?.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;

            requests.Add(new RecordCollectionRequest
            {
                SampleId = id,
                VolumeMl = volume,
                PatientFasted = fasted,
                DeclaredFastingHours = fasted == true ? requiredFasting : null
            });
        }

        _record.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            if (requests.Count > 0)
            {
                var result = await _workflow.RecordCollectionAsync(requests);

                if (result.IsFailure)
                {
                    _dialogs.ShowFailure(result);
                    return;
                }

                _status.ForeColor = UiTheme.Normal;
                _status.Text = $"{requests.Count} {T("SampleStatus.Collected").ToLowerInvariant()}";
            }

            if (dispatch)
            {
                var sent = await _workflow.DispatchAsync(selected);

                if (sent.IsFailure)
                {
                    _dialogs.ShowFailure(sent);
                }
                else
                {
                    _status.ForeColor = UiTheme.Normal;
                    _status.Text = $"{selected.Count} {T("SampleStatus.InTransit").ToLowerInvariant()}";
                }
            }

            await ActivateAsync();
        }
        finally
        {
            Cursor = Cursors.Default;
            _record.Enabled = _tubes.Rows.Count > 0;
        }
    }


    /// <summary>
    /// Reprints the patient's labels.
    /// <para>
    /// A reason is required and recorded: two tubes bearing one barcode is the mechanism by which
    /// one patient's sample gets reported under another patient's name, so a reprint is a logged
    /// event rather than a convenience.
    /// </para>
    /// </summary>
    private async Task ReprintAsync()
    {
        if (_currentSet.Count == 0) return;

        var reason = _dialogs.PromptForReason(
            T("Sample.ReprintReasonPrompt"), T("Action.PrintLabels"));

        if (reason is null) return;

        try
        {
            var result = await _workflow.ReprintLabelsAsync(_currentSet[0].LabOrderId, reason);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            var emitted = await LabelOutput.EmitAsync(_labelPrinter, _documents, _labels.Render,
                                                       result.Value);

            if (emitted.IsFailure) { _dialogs.ShowFailure(emitted, T("Action.PrintLabels")); return; }

            _status.ForeColor = UiTheme.Normal;
            _status.Text = emitted.Value.Destination == LabelDestination.Printer
                ? T("Sample.LabelsPrinted", emitted.Value.Count)
                : T("Sample.LabelsAsPdf", emitted.Value.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Reprinting labels failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }
}
