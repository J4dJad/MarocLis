using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Order registration — the reception desk's main screen.
/// <para>
/// Laid out in the order the conversation actually happens: who is the patient, what did the
/// prescriber ask for, what does it cost. The running total is visible throughout, because the
/// first question a Moroccan patient asks at the counter is what they will pay today — and with
/// AMO that is not the same as what the analyses cost.
/// </para>
/// <para>
/// The price breakdown separates the payer's share from the patient's, and shows any amount
/// charged above the national reference tariff as its own line. That excess is the figure a clerk
/// is most often asked to explain, and burying it inside a total guarantees an argument.
/// </para>
/// </summary>
public sealed class OrderRegistrationView : LocalizedView
{
    private readonly OrderRegistrationService _registration;
    private readonly IPatientQueries _patientQueries;
    private readonly Application.Administration.IMasterDataQueries _masterData;
    private readonly ICatalogQueries _catalogQueries;
    private readonly ISampleLabelRenderer _labelRenderer;
    private readonly ILabelPrinter _labelPrinter;
    private readonly IDocumentOutput _documents;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IUserDialogs _dialogs;
    private readonly Composition.PatientEditorFactory _patientEditor;
    private readonly ILogger<OrderRegistrationView>? _logger;

    // Patient
    private readonly TextBox _patientSearch = UiTheme.Input(300);
    private readonly Label _patientSearchLabel = new();
    private readonly ComboBox _patientPicker = new();
    private readonly Label _patientSummary = new();
    private readonly Button _newPatient = UiTheme.SecondaryButton(string.Empty);

    // Request
    private readonly TextBox _catalogSearch = UiTheme.Input(300);
    private readonly Label _catalogSearchLabel = new();
    private readonly DataGridView _catalog = UiTheme.Grid();
    private readonly DataGridView _basket = UiTheme.Grid();
    private readonly Label _basketHeading = new();

    // Context
    private readonly ComboBox _priority = new();
    private readonly ComboBox _origin = new();
    private readonly Label _priorityLabel = new();
    private readonly Label _originLabel = new();
    private readonly ComboBox _prescriber = new();
    private readonly Label _prescriberLabel = new();
    private readonly TextBox _clinicalInfo = UiTheme.Input(420);
    private readonly Label _clinicalInfoLabel = new();
    private readonly CheckBox _fasted = new();
    private readonly CheckBox _selfPay = new();

    // Totals and actions
    private readonly Label _totals = new();
    private readonly Button _register = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _clear = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new();

    private readonly List<CatalogPickerItem> _catalogItems = [];
    private readonly List<CatalogPickerItem> _selected = [];
    private IReadOnlyList<PatientSearchRow> _patients = [];
    private Guid? _patientId;
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };

    public OrderRegistrationView(OrderRegistrationService registration,
                                 IPatientQueries patientQueries, ICatalogQueries catalogQueries,
                                 ISampleLabelRenderer labelRenderer, ILabelPrinter labelPrinter,
                                 IDocumentOutput documents, ICurrentUser currentUser,
                                 IClock clock, IUserDialogs dialogs,
                                 Composition.PatientEditorFactory patientEditor,
                                 Application.Administration.IMasterDataQueries masterData,
                                 ILocalizer localizer, ICultureManager cultureManager,
                                 ILogger<OrderRegistrationView>? logger = null)
        : base(localizer, cultureManager)
    {
        _registration = registration;
        _patientQueries = patientQueries;
        _catalogQueries = catalogQueries;
        _labelRenderer = labelRenderer;
        _labelPrinter = labelPrinter;
        _documents = documents;
        _currentUser = currentUser;
        _clock = clock;
        _dialogs = dialogs;
        _patientEditor = patientEditor;
        _masterData = masterData;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Orders");

    // ---- Layout -----------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);
        AutoScroll = true;

        // --- patient -----------------------------------------------------------------------------

        var patientPanel = new Panel
        {
            Dock = DockStyle.Top, Height = 96,
            BackColor = UiTheme.SurfaceRaised, Padding = new Padding(UiTheme.Gutter)
        };

        _patientSearchLabel.Font = UiTheme.Small;
        _patientSearchLabel.ForeColor = UiTheme.TextSecondary;
        _patientSearchLabel.AutoSize = true;
        _patientSearchLabel.Location = new Point(UiTheme.Gutter, 6);

        _patientSearch.Location = new Point(UiTheme.Gutter, 24);

        _patientPicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _patientPicker.FlatStyle = FlatStyle.Flat;
        _patientPicker.Font = UiTheme.Body;
        _patientPicker.Bounds = new Rectangle(UiTheme.Gutter + 312, 24, 420, UiTheme.InputHeight);

        _patientSummary.Font = UiTheme.SmallBold;
        _patientSummary.ForeColor = UiTheme.Accent;
        _patientSummary.AutoSize = false;
        _patientSummary.Bounds = new Rectangle(UiTheme.Gutter, 58, 900, 24);

        // A walk-in who has never been to the laboratory is registered here, at the counter, in
        // the same pass as their order. Sending the clerk to another screen is how a
        // half-registered file gets abandoned mid-queue.
        _newPatient.Bounds = new Rectangle(UiTheme.Gutter + 744, 24, 190, UiTheme.ButtonHeight);

        patientPanel.Controls.AddRange(
            [_patientSearchLabel, _patientSearch, _patientPicker, _patientSummary, _newPatient]);

        // --- context -----------------------------------------------------------------------------

        var contextPanel = new Panel
        {
            Dock = DockStyle.Top, Height = 118,
            BackColor = UiTheme.Surface, Padding = new Padding(0, UiTheme.GutterTight, 0, 0),
            // Two fixed rows, each label above its field; the generic reflow separated the labels
            // from their fields once the prescriber was added.
            Tag = UiTags.OwnLayout
        };

        _priorityLabel.Font = UiTheme.Small;
        _priorityLabel.ForeColor = UiTheme.TextSecondary;
        _priorityLabel.AutoSize = true;
        _priorityLabel.Location = new Point(0, 4);

        _priority.DropDownStyle = ComboBoxStyle.DropDownList;
        _priority.FlatStyle = FlatStyle.Flat;
        _priority.Bounds = new Rectangle(0, 22, 180, UiTheme.InputHeight);

        _originLabel.Font = UiTheme.Small;
        _originLabel.ForeColor = UiTheme.TextSecondary;
        _originLabel.AutoSize = true;
        _originLabel.Location = new Point(192, 4);

        _origin.DropDownStyle = ComboBoxStyle.DropDownList;
        _origin.FlatStyle = FlatStyle.Flat;
        _origin.Bounds = new Rectangle(192, 22, 220, UiTheme.InputHeight);

        // The prescriber accepts a doctor from the list or a name typed freely: a walk-in often
        // brings a prescription from a doctor who is not yet in the directory, and registration
        // must not wait for someone to create the doctor's file first.
        _prescriberLabel.Font = UiTheme.Small;
        _prescriberLabel.ForeColor = UiTheme.TextSecondary;
        _prescriberLabel.AutoSize = true;
        _prescriberLabel.Location = new Point(424, 4);

        _prescriber.DropDownStyle = ComboBoxStyle.DropDown;
        _prescriber.FlatStyle = FlatStyle.Flat;
        _prescriber.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _prescriber.AutoCompleteSource = AutoCompleteSource.ListItems;
        _prescriber.Bounds = new Rectangle(424, 22, 280, UiTheme.InputHeight);

        _clinicalInfoLabel.Font = UiTheme.Small;
        _clinicalInfoLabel.ForeColor = UiTheme.TextSecondary;
        _clinicalInfoLabel.AutoSize = true;
        _clinicalInfoLabel.Location = new Point(0, 58);

        _clinicalInfo.Location = new Point(0, 76);

        _fasted.Font = UiTheme.Small;
        _fasted.ForeColor = UiTheme.TextSecondary;
        _fasted.AutoSize = true;
        _fasted.Location = new Point(436, 80);
        _fasted.Checked = true;

        _selfPay.Font = UiTheme.Small;
        _selfPay.ForeColor = UiTheme.TextSecondary;
        _selfPay.AutoSize = true;
        _selfPay.Location = new Point(600, 80);

        contextPanel.Controls.AddRange([_priorityLabel, _priority, _originLabel, _origin,
                                        _prescriberLabel, _prescriber,
                                        _clinicalInfoLabel, _clinicalInfo, _fasted, _selfPay]);

        // --- catalogue and basket -----------------------------------------------------------------

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 560,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        _catalogSearchLabel.Font = UiTheme.Small;
        _catalogSearchLabel.ForeColor = UiTheme.TextSecondary;
        _catalogSearchLabel.Dock = DockStyle.Top;
        _catalogSearchLabel.Height = 18;

        _catalogSearch.Dock = DockStyle.Top;

        BuildCatalogColumns();
        BuildBasketColumns();

        var catalogHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        catalogHost.Controls.Add(_catalog);
        catalogHost.Controls.Add(_catalogSearch);
        catalogHost.Controls.Add(_catalogSearchLabel);

        _basketHeading.Font = UiTheme.SmallBold;
        _basketHeading.ForeColor = UiTheme.TextSecondary;
        _basketHeading.Dock = DockStyle.Top;
        _basketHeading.Height = 22;

        var basketHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        basketHost.Controls.Add(_basket);
        basketHost.Controls.Add(_basketHeading);

        split.Panel1.Controls.Add(catalogHost);
        split.Panel2.Controls.Add(basketHost);

        // --- totals and actions -------------------------------------------------------------------

        // Totals on the left, the two actions side by side on the right, the message between them:
        // one line, so the buttons stay aligned whatever the width of the window.
        var footer = new Panel
        {
            Dock = DockStyle.Bottom, Height = 92,
            BackColor = UiTheme.SurfaceRaised, Padding = new Padding(UiTheme.Gutter, 8, UiTheme.Gutter, 8)
        };

        _totals.Font = UiTheme.Monospace;
        _totals.ForeColor = UiTheme.TextPrimary;
        _totals.AutoSize = false;
        _totals.Dock = DockStyle.Left;
        _totals.Width = 420;

        var actions = new Panel { Dock = DockStyle.Right, Width = 400, Tag = UiTags.OwnLayout };

        _clear.Bounds = new Rectangle(0, 18, 150, UiTheme.ButtonHeight + 6);
        _register.Bounds = new Rectangle(162, 18, 238, UiTheme.ButtonHeight + 6);
        _register.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        actions.Controls.AddRange([_clear, _register]);

        _status.Dock = DockStyle.Fill;
        _status.Padding = new Padding(UiTheme.Gutter, 0, UiTheme.Gutter, 0);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Font = UiTheme.Small;
        _status.ForeColor = UiTheme.TextSecondary;
        _status.AutoSize = false;

        // Fill first, so it docks last and takes what the two sides leave.
        footer.Controls.AddRange([_status, actions, _totals]);

        Controls.Add(split);
        Controls.Add(footer);
        Controls.Add(contextPanel);
        Controls.Add(patientPanel);
    }

    private void BuildCatalogColumns()
    {
        _catalog.Columns.Clear();

        void Add(string name, int weight, Font? font = null,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
            => _catalog.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body, Alignment = align
                }
            });

        Add("code", 12, UiTheme.Monospace);
        Add("name", 40);
        Add("department", 14, UiTheme.Small);
        Add("price", 12, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);
        Add("turnaround", 10, UiTheme.Small, DataGridViewContentAlignment.MiddleRight);
        Add("notes", 12, UiTheme.Small);
    }

    private void BuildBasketColumns()
    {
        _basket.Columns.Clear();

        void Add(string name, int weight, Font? font = null,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
            => _basket.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body, Alignment = align
                }
            });

        Add("code", 16, UiTheme.Monospace);
        Add("name", 50);
        Add("price", 20, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);
        Add("notes", 14, UiTheme.Small);
    }

    private void WireBehaviour()
    {
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await SearchPatientsAsync();
        };

        _patientSearch.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _patientPicker.SelectedIndexChanged += (_, _) => OnPatientSelected();
        _newPatient.Click += async (_, _) => await RegisterNewPatientAsync();

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Patients.Create, _newPatient);
        PermissionGate.Require(Permissions.Orders.Create, _register);

        _catalogSearch.TextChanged += async (_, _) => await SearchCatalogueAsync();

        // Typing a code or a name and pressing Enter adds the analysis without touching the mouse;
        // the down arrow moves into the list to choose another match.
        _catalogSearch.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Down && _catalog.Rows.Count > 0)
            {
                e.SuppressKeyPress = true;
                _catalog.Focus();
                _catalog.SelectFirstRow();
                return;
            }

            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await AddFromSearchAsync();
        };

        // Double-click, or Enter, adds to the basket. Both, because a clerk working at speed uses
        // the keyboard and one working with a patient at the counter uses the mouse.
        _catalog.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) AddToBasket(e.RowIndex); };
        _catalog.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || _catalog.CurrentRow is null) return;
            e.SuppressKeyPress = true;
            AddToBasket(_catalog.CurrentRow.Index);
        };

        _basket.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) RemoveFromBasket(e.RowIndex); };
        _basket.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete || _basket.CurrentRow is null) return;
            RemoveFromBasket(_basket.CurrentRow.Index);
        };

        _selfPay.CheckedChanged += (_, _) => RefreshTotals();
        _register.Click += async (_, _) => await RegisterAsync();
        _clear.Click += (_, _) => ResetForm();
    }

    protected override void ApplyTexts()
    {
        _patientSearchLabel.Text = T("App.SearchPlaceholder");
        _newPatient.Text = T("Patient.NewFile");
        _catalogSearchLabel.Text = $"{T("Catalog.Analyses")} / {T("Catalog.Panels")}";
        _basketHeading.Text = T("Order.Analyses");
        _priorityLabel.Text = T("Field.Priority");
        _originLabel.Text = T("Order.Origin");
        _prescriberLabel.Text = T("Order.Prescriber");
        _clinicalInfoLabel.Text = T("Order.ClinicalInformation");
        _fasted.Text = T("Order.Fasted");
        _selfPay.Text = T("PayerKind.SelfPay");
        _register.Text = T("Action.Save");
        _clear.Text = T("Action.Clear");

        FillEnumCombo(_priority, Enum.GetValues<OrderPriority>(), p => T($"Priority.{p}"));
        FillEnumCombo(_origin, Enum.GetValues<OrderOrigin>(), o => T($"OrderOrigin.{o}"));

        if (_catalog.Columns.Count > 0)
        {
            _catalog.Column("code").HeaderText = T("Field.Code");
            _catalog.Column("name").HeaderText = T("Field.Name");
            _catalog.Column("department").HeaderText = T("Field.Department");
            _catalog.Column("price").HeaderText = T("Field.Price");
            _catalog.Column("turnaround").HeaderText = T("Catalog.Turnaround");
            _catalog.Column("notes").HeaderText = T("Field.Notes");
        }

        if (_basket.Columns.Count > 0)
        {
            _basket.Column("code").HeaderText = T("Field.Code");
            _basket.Column("name").HeaderText = T("Field.Name");
            _basket.Column("price").HeaderText = T("Field.Price");
            _basket.Column("notes").HeaderText = T("Field.Notes");
        }

        RefreshTotals();
    }

    private static void FillEnumCombo<T>(ComboBox combo, T[] values, Func<T, string> label) where T : Enum
    {
        var selected = combo.SelectedIndex;
        combo.BeginUpdate();
        combo.Items.Clear();
        foreach (var value in values) combo.Items.Add(new EnumChoice<T>(value, label(value)));
        combo.SelectedIndex = selected >= 0 ? selected : 0;
        combo.EndUpdate();
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        await SearchCatalogueAsync(ct);
        if (_prescriber.Items.Count == 0) await LoadPrescribersAsync(ct);
        if (_patientId is null && _patientPicker.Items.Count == 0) await SearchPatientsAsync();
        _patientSearch.Focus();
    }

    private async Task LoadPrescribersAsync(CancellationToken ct)
    {
        try
        {
            var prescribers = await _masterData.GetPrescribersAsync(null, includeInactive: false, ct);

            _prescriber.BeginUpdate();
            _prescriber.Items.Clear();
            foreach (var p in prescribers)
            {
                var speciality = p.Speciality?.For(Localizer.CurrentLanguage);
                _prescriber.Items.Add(new PrescriberChoice(
                    p.PrescriberId,
                    string.IsNullOrWhiteSpace(speciality) ? p.FullName : $"{p.FullName} — {speciality}"));
            }
            _prescriber.EndUpdate();
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
        catch (Exception ex)
        {
            // The list only helps typing; a name typed freely is still accepted without it.
            _logger?.LogWarning(ex, "Loading the prescribers failed.");
        }
    }

    // A doctor from the list, matched also when the name was typed out in full rather than picked.
    private PrescriberChoice? ChosenPrescriber()
    {
        if (_prescriber.SelectedItem is PrescriberChoice chosen) return chosen;

        var text = _prescriber.Text.Trim();
        return _prescriber.Items.OfType<PrescriberChoice>()
            .FirstOrDefault(c => string.Equals(c.Display, text, StringComparison.CurrentCultureIgnoreCase));
    }

    // ---- Patient ----------------------------------------------------------------------------------

    private async Task SearchPatientsAsync()
    {
        var term = _patientSearch.Text.Trim();

        // One or two characters match too much to be useful; an empty box lists the patients so the
        // picker is never an empty drop-down, but nobody is selected until the clerk chooses.
        if (term.Length is > 0 and < 3)
        {
            _patientPicker.Items.Clear();
            return;
        }

        try
        {
            var page = await _patientQueries.SearchAsync(term, _currentUser.SiteId,
                                                          pageSize: term.Length == 0 ? 100 : 25);
            _patients = page.Items;

            _patientPicker.BeginUpdate();
            _patientPicker.Items.Clear();

            foreach (var patient in _patients)
            {
                var age = patient.AgeYears is { } years ? $", {years} {T("Report.Years")}" : string.Empty;
                _patientPicker.Items.Add(new PatientChoice(
                    patient.Id,
                    $"{patient.MedicalRecordNumber} — {patient.FullName}{age}" +
                    (patient.PayerName is { Length: > 0 } ? $"  [{patient.PayerName}]" : string.Empty)));
            }

            if (_patientPicker.Items.Count > 0 && term.Length > 0) _patientPicker.SelectedIndex = 0;
            _patientPicker.EndUpdate();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Patient lookup failed during registration.");
        }
    }

    /// <summary>
    /// Registers a new file and selects it, without leaving the order screen.
    /// <para>
    /// The editor may hand back an <em>existing</em> patient rather than a new one: its duplicate
    /// check lets the clerk recognise somebody already on file. Either way the returned id is the
    /// one the order should be raised against, which is why this makes no distinction between them.
    /// </para>
    /// </summary>
    private async Task RegisterNewPatientAsync()
    {
        var created = _patientEditor.Show(FindForm());
        if (created is not { } id) return;

        var detail = await _patientQueries.GetDetailAsync(id);
        if (detail is null) return;

        // Re-runs the search on the file number so the picker holds exactly this patient, rather
        // than leaving a stale list from whatever the clerk had typed before.
        _patientSearch.Text = detail.MedicalRecordNumber;
        await SearchPatientsAsync();

        for (var i = 0; i < _patientPicker.Items.Count; i++)
        {
            if (_patientPicker.Items[i] is PatientChoice c && c.Id == id)
            {
                _patientPicker.SelectedIndex = i;
                break;
            }
        }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = $"{detail.MedicalRecordNumber} — {detail.LastName} {detail.FirstName}";
    }

    private void OnPatientSelected()
    {
        if (_patientPicker.SelectedItem is not PatientChoice choice)
        {
            _patientId = null;
            _patientSummary.Text = string.Empty;
            return;
        }

        _patientId = choice.Id;

        var patient = _patients.FirstOrDefault(p => p.Id == choice.Id);
        if (patient is null) return;

        var parts = new List<string>
        {
            patient.FullName,
            T($"Gender.{patient.Gender}")
        };

        if (patient.AgeYears is { } age) parts.Add($"{age} {T("Report.Years")}");
        if (patient.Cin is { Length: > 0 }) parts.Add($"{T("Patient.Cin")} {patient.Cin}");
        parts.Add(patient.PayerName ?? T("PayerKind.SelfPay"));

        _patientSummary.Text = string.Join("   ·   ", parts);

        // A critical patient alert is stated before the clerk starts composing the order.
        if (patient.HasCriticalAlert)
        {
            _patientSummary.ForeColor = UiTheme.Critical;
            _dialogs.Warn(T("Patient.Alerts"), T("Patient.Title"));
        }
        else
        {
            _patientSummary.ForeColor = UiTheme.Accent;
        }

        RefreshTotals();
    }

    // ---- Catalogue ---------------------------------------------------------------------------------

    private async Task AddFromSearchAsync()
    {
        var term = _catalogSearch.Text.Trim();
        if (term.Length == 0) return;

        // Enter can arrive before the search started by the last keystroke has answered.
        await SearchCatalogueAsync();

        // An exact code wins ("NFS" is the blood count, not every analysis whose name holds "nfs"),
        // then a code that starts with what was typed, then the first match by name.
        var match =
            _catalogItems.FirstOrDefault(i => string.Equals(i.Code, term, StringComparison.OrdinalIgnoreCase))
            ?? _catalogItems.FirstOrDefault(i => i.Code.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            ?? _catalogItems.FirstOrDefault();

        if (match is null)
        {
            _status.ForeColor = UiTheme.Abnormal;
            _status.Text = T("App.NoData");
            return;
        }

        var row = _catalog.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is Guid id && id == match.Id);
        if (row is null) return;

        AddToBasket(row.Index);

        // Cleared only when the analysis went in, so a refusal ("already in the panel") leaves
        // the text for the clerk to correct.
        if (_selected.Any(x => x.Id == match.Id && x.IsPanel == match.IsPanel))
        {
            _catalogSearch.Clear();
            _catalogSearch.Focus();
        }
    }

    private int _catalogSearchVersion;

    private async Task SearchCatalogueAsync(CancellationToken ct = default)
    {
        var version = ++_catalogSearchVersion;

        try
        {
            var items = await _catalogQueries.SearchPickerAsync(
                _catalogSearch.Text, payerId: null, _clock.Today, ct: ct);

            // Each keystroke starts a search; only the latest one may fill the list.
            if (version != _catalogSearchVersion) return;

            _catalogItems.Clear();
            _catalogItems.AddRange(items);

            _catalog.SuspendLayout();
            _catalog.Rows.Clear();

            foreach (var item in _catalogItems)
            {
                var notes = new List<string>();
                if (item.IsPanel) notes.Add($"{item.ComponentCount} {T("Catalog.Analyses").ToLowerInvariant()}");
                if (item.FastingHours is { } fasting) notes.Add($"{T("Order.Fasted")} {fasting} h");
                if (item.ConsentRequirement != Domain.Catalog.ConsentRequirement.None)
                    notes.Add(T("Consent.Required"));
                if (item.IsSubcontracted) notes.Add(T("Catalog.Subcontracted"));

                var index = _catalog.Rows.Add(
                    item.Code,
                    item.Name.For(Localizer.CurrentLanguage),
                    item.DepartmentName?.For(Localizer.CurrentLanguage) ?? string.Empty,
                    Money.Of(item.Price).Format(Localizer.CurrentLanguage),
                    $"{item.TurnaroundHours} h",
                    string.Join(" · ", notes));

                var row = _catalog.Rows[index];
                row.Tag = item.Id;

                // Panels are emphasised: they are what a prescriber most often writes, and picking
                // the panel rather than its components is both faster and less error-prone.
                if (item.IsPanel)
                {
                    row.Cells["name"].Style.Font = UiTheme.BodyBold;
                    row.Cells["code"].Style.ForeColor = UiTheme.Accent;
                }
            }

            _catalog.ResumeLayout(performLayout: true);
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the catalogue picker failed.");
        }
    }

    private void AddToBasket(int catalogRowIndex)
    {
        if (_catalog.Rows[catalogRowIndex].Tag is not Guid id) return;

        var item = _catalogItems.FirstOrDefault(i => i.Id == id);
        if (item is null) return;

        if (_selected.Any(s => s.Id == item.Id && s.IsPanel == item.IsPanel))
        {
            _status.ForeColor = UiTheme.Abnormal;
            _status.Text = T("Order_AnalysisAlreadyPresent");
            return;
        }

        // The order holds each analysis once, however many times it was chosen. An analysis
        // already inside a chosen panel is therefore not added again, and choosing a panel takes
        // out the analyses chosen on their own that it contains — otherwise the list showed them
        // twice and the counter total ran above the amount registered and invoiced.
        if (!item.IsPanel
            && _selected.FirstOrDefault(s => s.IsPanel && Members(s).Contains(item.Id)) is { } covering)
        {
            _status.ForeColor = UiTheme.Abnormal;
            _status.Text = T("Order.AlreadyInPanel", item.Code, covering.Name.For(Localizer.CurrentLanguage));
            return;
        }

        _status.Text = string.Empty;

        if (item.IsPanel)
        {
            var members = Members(item);
            var absorbed = _selected.Where(s => !s.IsPanel && members.Contains(s.Id)).ToList();

            if (absorbed.Count > 0)
            {
                foreach (var analysis in absorbed) _selected.Remove(analysis);

                _status.ForeColor = UiTheme.Abnormal;
                _status.Text = T("Order.MergedIntoPanel",
                                 string.Join(", ", absorbed.Select(a => a.Code)),
                                 item.Name.For(Localizer.CurrentLanguage));
            }
        }

        _selected.Add(item);
        RenderBasket();
    }

    private static HashSet<Guid> Members(CatalogPickerItem panel) =>
        [.. (panel.Components ?? []).Select(c => c.AnalysisTestId)];

    private void RemoveFromBasket(int basketRowIndex)
    {
        if (_basket.Rows[basketRowIndex].Tag is not Guid id) return;

        var item = _selected.FirstOrDefault(s => s.Id == id);
        if (item is null) return;

        _selected.Remove(item);
        RenderBasket();
    }

    private void RenderBasket()
    {
        _basket.SuspendLayout();
        _basket.Rows.Clear();

        var counted = new HashSet<Guid>();

        foreach (var item in _selected)
        {
            var notes = new List<string>();
            if (item.IsPanel) notes.Add(T("Catalog.Panel"));

            // Two panels can share members; the shared ones are billed once, on the first.
            var members = item.IsPanel ? Members(item) : [item.Id];
            var shared = members.Count(id => !counted.Add(id));
            if (shared > 0) notes.Add(T("Order.SharedAnalyses", shared));
            if (item.FastingHours is { } fasting) notes.Add($"{fasting} h");
            if (item.ConsentRequirement != Domain.Catalog.ConsentRequirement.None)
                notes.Add(T("Consent.Required"));

            var index = _basket.Rows.Add(
                item.Code,
                item.Name.For(Localizer.CurrentLanguage),
                Money.Of(item.Price).Format(Localizer.CurrentLanguage),
                string.Join(" · ", notes));

            _basket.Rows[index].Tag = item.Id;
        }

        _basket.ResumeLayout(performLayout: true);
        RefreshTotals();
    }

    /// <summary>
    /// Shows the counter total.
    /// <para>
    /// This is an indicative figure from the catalogue price list. The authoritative split between
    /// the payer and the patient is computed server-side at registration by the pricing engine,
    /// against the coverage frozen onto the order — so the registration result is what gets
    /// displayed afterwards, not this estimate.
    /// </para>
    /// </summary>
    private void RefreshTotals()
    {
        // Counted per analysis, as the order is priced: two panels sharing a glycaemia bill it once.
        var prices = new Dictionary<Guid, decimal>();

        foreach (var item in _selected)
        {
            if (!item.IsPanel) prices[item.Id] = item.Price;
            else
                foreach (var member in item.Components ?? [])
                    prices.TryAdd(member.AnalysisTestId, member.Price);
        }

        var gross = Money.Of(prices.Values.Sum());

        var maxFasting = _selected.Select(s => s.FastingHours ?? 0).DefaultIfEmpty(0).Max();
        var needsConsent = _selected.Any(
            s => s.ConsentRequirement != Domain.Catalog.ConsentRequirement.None);

        var lines = new List<string>
        {
            $"{T("Billing.Gross"),-28} {gross.Format(Localizer.CurrentLanguage),16}",
            $"{T("Catalog.Analyses"),-28} {prices.Count,16}"
        };

        if (maxFasting > 0)
            lines.Add($"{T("Catalog.FastingRequired"),-28} {maxFasting + " h",16}");

        if (needsConsent) lines.Add(T("Consent.Required"));

        _totals.Text = string.Join(Environment.NewLine, lines);

        _register.Enabled = _patientId is not null && _selected.Count > 0;
    }

    // ---- Registration -------------------------------------------------------------------------------

    private async Task RegisterAsync()
    {
        if (_patientId is not { } patientId || _selected.Count == 0) return;

        var origin = _origin.SelectedItem is EnumChoice<OrderOrigin> chosenOrigin
            ? chosenOrigin.Value : OrderOrigin.Walkin;
        var prescriber = ChosenPrescriber();
        var prescriberText = _prescriber.Text.Trim();

        // Checked here as well as on save so the clerk is taken to the field that is missing.
        if (prescriber is null && prescriberText.Length == 0 && origin != OrderOrigin.PatientRequest)
        {
            _dialogs.Warn(T("Order_PrescriberRequired"));
            _prescriber.Focus();
            return;
        }

        var request = new RegisterOrderRequest
        {
            PatientId = patientId,
            AnalysisTestIds = [.. _selected.Where(s => !s.IsPanel).Select(s => s.Id)],
            TestPanelIds = [.. _selected.Where(s => s.IsPanel).Select(s => s.Id)],
            Priority = _priority.SelectedItem is EnumChoice<OrderPriority> p
                ? p.Value : OrderPriority.Routine,
            Origin = origin,
            PrescriberId = prescriber?.Id,
            PrescriberFreeText = prescriber is null && prescriberText.Length > 0 ? prescriberText : null,
            ClinicalInformation = string.IsNullOrWhiteSpace(_clinicalInfo.Text)
                ? null : _clinicalInfo.Text.Trim(),
            PatientFasted = _fasted.Checked,
            BillDirectlyToPatient = _selfPay.Checked
        };

        _register.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var result = await _registration.RegisterAsync(request);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            var registered = result.Value;

            // Warnings are advisory — a non-fasting sample a clinician knowingly accepts, say —
            // so they are reported but do not undo the registration.
            if (registered.Warnings.Count > 0)
                _dialogs.Warn(Localizer.Describe(registered.Warnings), T("Order.Title"));

            ShowPricing(registered);
            await PrintLabelsAsync(registered);

            _status.ForeColor = UiTheme.Normal;
            _status.Text = $"{T("Order.AccessionNumber")} {registered.AccessionNumber} — " +
                           $"{registered.Labels.Count} {T("Sample.Plural").ToLowerInvariant()}";

            ResetForm();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Order registration failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
        finally
        {
            Cursor = Cursors.Default;
            _register.Enabled = true;
        }
    }

    /// <summary>
    /// Shows the authoritative price breakdown from the pricing engine: the payer's share, the
    /// patient's share, and any excess over the national reference tariff stated separately.
    /// </summary>
    private void ShowPricing(RegisteredOrder registered)
    {
        var pricing = registered.Pricing;
        var language = Localizer.CurrentLanguage;

        var lines = new List<string>
        {
            $"{T("Order.AccessionNumber")} : {registered.AccessionNumber}",
            string.Empty,
            $"{T("Billing.Gross")} : {pricing.Gross.Format(language)}"
        };

        if (!pricing.Discount.IsZero)
            lines.Add($"{T("Billing.Discount")} : {pricing.Discount.Format(language)}");

        lines.Add($"{T("Billing.TotalDue")} : {pricing.Total.Format(language)}");

        if (!pricing.PayerShare.IsZero)
        {
            lines.Add($"{T("Billing.PayerShare")} : {pricing.PayerShare.Format(language)}");
            lines.Add($"{T("Billing.PatientShare")} : {pricing.PatientShare.Format(language)}");
        }

        if (!pricing.ExcessOverReferenceTariff.IsZero)
        {
            lines.Add(string.Empty);
            lines.Add($"{T("Billing.ExcessOverTariff")} : " +
                      pricing.ExcessOverReferenceTariff.Format(language));
        }

        lines.Add(string.Empty);
        lines.Add($"{T("Payment.Title")} : {pricing.DueAtCounter.Format(language)}");

        _dialogs.Info(string.Join(Environment.NewLine, lines), T("Billing.Invoice"));
    }

    /// <summary>
    /// Renders and opens the tube labels immediately. The labels have to be in the phlebotomist's
    /// hand before the patient leaves the counter, so this is not a separate deliberate step.
    /// </summary>
    private async Task PrintLabelsAsync(RegisteredOrder registered)
    {
        if (registered.Labels.Count == 0) return;

        try
        {
            var emitted = await LabelOutput.EmitAsync(_labelPrinter, _documents, _labelRenderer.Render,
                                                       registered.Labels);

            if (emitted.IsFailure) _dialogs.ShowFailure(emitted, T("Action.PrintLabels"));
        }
        catch (Exception ex)
        {
            // A label failure must not lose the registration: the order exists, and the labels can
            // be reprinted from the order screen.
            _logger?.LogError(ex, "Rendering the sample labels failed.");
            _dialogs.Warn(T("Action.PrintLabels") + " — " + T("Message.UnexpectedError"));
        }
    }

    private void ResetForm()
    {
        _selected.Clear();
        RenderBasket();

        _patientSearch.Clear();
        _patientPicker.Items.Clear();
        _patientSummary.Text = string.Empty;
        _patientId = null;
        _clinicalInfo.Clear();
        _fasted.Checked = true;
        _selfPay.Checked = false;

        if (_priority.Items.Count > 0) _priority.SelectedIndex = 0;
        if (_origin.Items.Count > 0) _origin.SelectedIndex = 0;
        _prescriber.SelectedIndex = -1;
        _prescriber.Text = string.Empty;

        _patientSearch.Focus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _debounce.Dispose();
        base.Dispose(disposing);
    }

    private sealed record PrescriberChoice(Guid Id, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record PatientChoice(Guid Id, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record EnumChoice<T>(T Value, string Display) where T : Enum
    {
        public override string ToString() => Display;
    }
}
