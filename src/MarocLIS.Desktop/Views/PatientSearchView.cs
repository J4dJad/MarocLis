using MarocLIS.Application.Abstractions;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Audit;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Patient search and history.
/// <para>
/// One search box, not a form of fields. A reception clerk is told a name, or handed a CIN, or
/// reads a file number off an old report — and making them pick the right field first is friction
/// that ends in a duplicate file. The query matches all of them.
/// </para>
/// <para>
/// Searching is debounced rather than requiring a button: the clerk types while the patient is
/// still speaking, and a 300 ms pause is long enough to avoid a query per keystroke while short
/// enough to feel immediate.
/// </para>
/// </summary>
public sealed class PatientSearchView : LocalizedView
{
    private readonly IPatientQueries _queries;
    private readonly IDataAccessAuditor _accessAuditor;
    private readonly ICurrentUser _currentUser;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<PatientSearchView>? _logger;

    private readonly Composition.PatientEditorFactory _editor;
    private readonly Application.Patients.PatientRegistrationService _service;
    private readonly INavigationService _navigation;

    private readonly TextBox _search = UiTheme.Input(360);
    private readonly Label _searchLabel = new();
    private readonly Label _resultCount = new();
    private readonly Button _newPatient = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editPatient = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _merge = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _anonymize = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new();
    private readonly DataGridView _results = UiTheme.Grid();
    private readonly DataGridView _history = UiTheme.Grid();
    private readonly Label _historyHeading = new();
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };

    private PagedResult<PatientSearchRow> _page = PagedResult<PatientSearchRow>.Empty();

    public PatientSearchView(IPatientQueries queries, IDataAccessAuditor accessAuditor,
                             ICurrentUser currentUser, IUserDialogs dialogs,
                             Composition.PatientEditorFactory editor,
                             Application.Patients.PatientRegistrationService service,
                             INavigationService navigation,
                             ILocalizer localizer, ICultureManager cultureManager,
                             ILogger<PatientSearchView>? logger = null)
        : base(localizer, cultureManager)
    {
        _queries = queries;
        _accessAuditor = accessAuditor;
        _currentUser = currentUser;
        _dialogs = dialogs;
        _editor = editor;
        _service = service;
        _navigation = navigation;
        _logger = logger;

        BuildLayout();

        _newPatient.Click += async (_, _) => await CreatePatientAsync();
        _editPatient.Click += async (_, _) => await EditPatientAsync();
        _merge.Click += async (_, _) => await MergeAsync();
        _anonymize.Click += async (_, _) => await AnonymizeAsync();

        _results.SelectionChanged += (_, _) => RefreshActions();

        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await SearchAsync();
        };

        _search.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };

        _results.SelectionChanged += async (_, _) => await LoadHistoryAsync();

        // Double-click on a past order opens the file. The history grid is where an operator is
        // standing when they need it — "the patient is on the phone about the order from Tuesday" —
        // and making them copy the accession number into another screen is friction for nothing.
        _history.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex < 0 || _history.Rows[e.RowIndex].Tag is not Guid orderId) return;

            await _navigation.NavigateToAsync<OrderDetailView>(view => view.OrderId = orderId);
        };
    }

    public override string Title => T("Nav.Patients");

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var searchBar = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = UiTheme.Surface };

        _searchLabel.Font = UiTheme.Small;
        _searchLabel.ForeColor = UiTheme.TextSecondary;
        _searchLabel.AutoSize = true;
        _searchLabel.Location = new Point(0, 4);

        _search.Location = new Point(0, 22);
        _search.Font = UiTheme.Body;

        _resultCount.Font = UiTheme.Small;
        _resultCount.ForeColor = UiTheme.TextMuted;
        _resultCount.AutoSize = true;
        _resultCount.Location = new Point(378, 28);

        // The create button sits beside the search box on purpose: the clerk searches first and
        // registers only when the search comes back empty, which is the order that avoids a
        // duplicate file.
        _newPatient.Bounds = new Rectangle(500, 20, 200, UiTheme.ButtonHeight);
        _editPatient.Bounds = new Rectangle(710, 20, 150, UiTheme.ButtonHeight);
        _merge.Bounds = new Rectangle(870, 20, 190, UiTheme.ButtonHeight);
        _anonymize.Bounds = new Rectangle(1070, 20, 180, UiTheme.ButtonHeight);

        _status.Font = UiTheme.Small;
        _status.AutoSize = false;
        _status.Bounds = new Rectangle(1260, 26, 400, 20);

        searchBar.Controls.AddRange([_searchLabel, _search, _resultCount, _newPatient,
                                     _editPatient, _merge, _anonymize, _status]);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 320,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        BuildResultColumns();
        BuildHistoryColumns();

        split.Panel1.Controls.Add(_results);

        _historyHeading.Font = UiTheme.SmallBold;
        _historyHeading.ForeColor = UiTheme.TextSecondary;
        _historyHeading.Dock = DockStyle.Top;
        _historyHeading.Height = 24;

        var historyHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        historyHost.Controls.Add(_history);
        historyHost.Controls.Add(_historyHeading);

        split.Panel2.Controls.Add(historyHost);

        Controls.Add(split);
        Controls.Add(searchBar);
    }

    private void BuildResultColumns()
    {
        _results.Columns.Clear();

        void Add(string name, int weight, Font? font = null)
            => _results.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                FillWeight = weight,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body }
            });

        Add("record", 11, UiTheme.Monospace);
        Add("name", 22);
        Add("gender", 7);
        Add("age", 6);
        Add("dob", 10, UiTheme.Monospace);
        Add("cin", 10, UiTheme.Monospace);
        Add("phone", 11, UiTheme.Monospace);
        Add("payer", 13);
        Add("orders", 5);
        Add("last", 10, UiTheme.Monospace);
    }

    private void BuildHistoryColumns()
    {
        _history.Columns.Clear();

        void Add(string name, int weight, Font? font = null)
            => _history.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                FillWeight = weight,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body }
            });

        Add("accession", 16, UiTheme.Monospace);
        Add("registered", 16, UiTheme.Monospace);
        Add("status", 18);
        Add("priority", 12);
        Add("count", 10);
        Add("prescriber", 28);
    }

    protected override void ApplyTexts()
    {
        _searchLabel.Text = T("App.SearchPlaceholder");
        _historyHeading.Text = T("Patient.History");

        _newPatient.Text = T("Patient.NewFile");
        _editPatient.Text = T("Action.Edit");
        _merge.Text = T("Patient.Merge");
        _anonymize.Text = T("Patient.Anonymize");

        if (_results.Columns.Count > 0)
        {
            _results.Column("record").HeaderText = T("Patient.RecordNumber");
            _results.Column("name").HeaderText = T("Patient.Title");
            _results.Column("gender").HeaderText = T("Patient.Gender");
            _results.Column("age").HeaderText = T("Patient.Age");
            _results.Column("dob").HeaderText = T("Patient.DateOfBirth");
            _results.Column("cin").HeaderText = T("Patient.Cin");
            _results.Column("phone").HeaderText = T("Patient.MobilePhone");
            _results.Column("payer").HeaderText = T("Coverage.Payer");
            _results.Column("orders").HeaderText = T("Order.Plural");
            _results.Column("last").HeaderText = T("Field.Date");
        }

        if (_history.Columns.Count > 0)
        {
            _history.Column("accession").HeaderText = T("Order.AccessionNumber");
            _history.Column("registered").HeaderText = T("Order.RegisteredAt");
            _history.Column("status").HeaderText = T("Field.Status");
            _history.Column("priority").HeaderText = T("Field.Priority");
            _history.Column("count").HeaderText = T("Catalog.Analyses");
            _history.Column("prescriber").HeaderText = T("Order.Prescriber");
        }

        if (_page.Items.Count > 0) RenderResults();

        RefreshActions();
    }

    // ---- Patient file actions ----------------------------------------------------------------------

    private PatientSearchRow? Selected => _results.CurrentRow?.Tag as PatientSearchRow;

    private void RefreshActions()
    {
        var row = Selected;

        _editPatient.Enabled = row is not null;
        _anonymize.Enabled = row is not null;

        // Merging needs two files, so it is only offered once a search has produced more than one
        // candidate — which is exactly the situation a duplicate creates.
        _merge.Enabled = row is not null && _page.Items.Count > 1;
    }

    private async Task CreatePatientAsync()
    {
        var created = _editor.Show(FindForm());
        if (created is not { } id) return;

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Message.Saved");

        // Re-runs the search so the new file appears and is selected, which is what the clerk
        // needs next: they are about to register an order against it.
        await SearchAsync();
        SelectRow(id);
    }

    private async Task EditPatientAsync()
    {
        if (Selected is not { } row) return;

        var edited = _editor.Show(FindForm(), row.Id);
        if (edited is null) return;

        await SearchAsync();
        SelectRow(row.Id);
    }

    /// <summary>
    /// Merges the selected file into another one the clerk picks from the same result set.
    /// <para>
    /// The losing file is kept and pointed at the survivor — it is the actor on its own audit
    /// entries and may be referenced by an issued invoice — so this is a redirect, not a deletion.
    /// </para>
    /// </summary>
    private async Task MergeAsync()
    {
        if (Selected is not { } duplicate) return;

        var others = _page.Items.Where(p => p.Id != duplicate.Id).ToList();

        if (others.Count == 0)
        {
            _dialogs.Warn(T("Patient.MergeNeedsTwoFiles"), T("Patient.Merge"));
            return;
        }

        using var dialog = new Forms.PatientMergeDialog(Localizer, _dialogs, duplicate, others);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        var result = await _service.MergeAsync(duplicate.Id, dialog.SurvivingPatientId,
                                                dialog.Reason);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("Patient.Merge"));
            return;
        }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Patient.Merged");

        await SearchAsync();
    }

    /// <summary>
    /// The loi 08-09 right-to-erasure path. Pseudonymises rather than deletes, because the
    /// ten-year medical retention obligation and the unbroken invoice series both survive the
    /// request.
    /// </summary>
    private async Task AnonymizeAsync()
    {
        if (Selected is not { } row) return;

        if (!_dialogs.Confirm(T("Patient.ConfirmAnonymize", row.FullName,
                                 row.MedicalRecordNumber),
                               T("Patient.Anonymize")))
            return;

        var justification = _dialogs.PromptForReason(
            T("Patient.AnonymizeJustificationPrompt", row.MedicalRecordNumber),
            T("Patient.Anonymize"), minimumLength: 10);

        if (justification is null) return;

        var reference = row.MedicalRecordNumber.Replace("-", string.Empty);

        var result = await _service.AnonymizeAsync(row.Id, reference, justification);

        if (result.IsFailure)
        {
            // The commonest refusal is a live retention period. Offering the override here rather
            // than up front keeps the obligation as the default answer.
            if (result.Errors.Any(e => e.ResourceKey == "Patient_RetentionNotExpired"))
            {
                if (!_dialogs.Confirm(Describe(result) + Environment.NewLine + Environment.NewLine
                                       + T("Patient.OverrideRetentionQuestion"),
                                       T("Patient.Anonymize")))
                    return;

                var forced = await _service.AnonymizeAsync(row.Id, reference, justification,
                                                            overrideRetention: true);

                if (forced.IsFailure) { _dialogs.ShowFailure(forced); return; }
            }
            else
            {
                _dialogs.ShowFailure(result);
                return;
            }
        }

        _status.ForeColor = UiTheme.Abnormal;
        _status.Text = T("Patient.Anonymized");

        await SearchAsync();
    }

    private void SelectRow(Guid patientId)
    {
        foreach (DataGridViewRow row in _results.Rows)
        {
            if ((row.Tag as PatientSearchRow)?.Id != patientId) continue;
            row.Selected = true;
            _results.CurrentCell = row.Cells[0];
            return;
        }
    }

    public override Task ActivateAsync(CancellationToken ct = default)
    {
        _search.Focus();
        return Task.CompletedTask;
    }

    private async Task SearchAsync()
    {
        var term = _search.Text.Trim();

        // Below three characters the result set is meaninglessly broad, and each keystroke would
        // be a full-table scan. The clerk sees nothing rather than everything.
        if (term.Length is > 0 and < 3)
        {
            _page = PagedResult<PatientSearchRow>.Empty();
            RenderResults();
            return;
        }

        try
        {
            _page = await _queries.SearchAsync(term, _currentUser.SiteId);
            RenderResults();

            // Loi 08-09: a search over patient data is an access to it, and the number of records
            // returned is what distinguishes a lookup from an extraction.
            if (term.Length >= 3)
                await _accessAuditor.LogAsync(DataAccessKind.Search,
                                              context: "PatientSearch",
                                              recordCount: _page.TotalCount,
                                              queryDescription: term);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Patient search failed for '{Term}'.", term);
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private void RenderResults()
    {
        _results.SuspendLayout();
        _results.Rows.Clear();

        foreach (var row in _page.Items)
        {
            var index = _results.Rows.Add(
                row.MedicalRecordNumber,
                Localizer.CurrentLanguage == Domain.Common.AppLanguage.Arabic
                    && row.FullNameArabic is { Length: > 0 }
                        ? row.FullNameArabic
                        : row.FullName,
                T($"Gender.{row.Gender}"),
                row.AgeYears?.ToString() ?? string.Empty,
                row.DateOfBirth?.ToString("dd/MM/yyyy") ?? string.Empty,
                row.Cin ?? string.Empty,
                row.MobilePhone ?? string.Empty,
                row.PayerName ?? string.Empty,
                row.OrderCount.ToString(),
                row.LastOrderAt?.ToString("dd/MM/yy") ?? string.Empty);

            var gridRow = _results.Rows[index];

            // The whole row, not just the id: the action bar needs the name and file number to
            // compose its confirmation prompts, and re-querying for them would be wasteful.
            gridRow.Tag = row;

            // A critical patient alert — an unpaid balance, a biohazard, a known difficult draw —
            // has to be visible before the clerk opens the file, not after.
            if (row.HasCriticalAlert)
            {
                gridRow.DefaultCellStyle.BackColor = UiTheme.CriticalBackground;
                gridRow.DefaultCellStyle.ForeColor = UiTheme.Critical;
            }
        }

        _results.ResumeLayout(performLayout: true);

        _resultCount.Text = _page.TotalCount == 0
            ? T("App.NoData")
            : $"{_page.Items.Count} / {_page.TotalCount}";

        if (_results.Rows.Count > 0) _results.Rows[0].Selected = true;
        else _history.Rows.Clear();
    }

    private async Task LoadHistoryAsync()
    {
        if (_results.CurrentRow?.Tag is not PatientSearchRow { Id: var patientId })
        {
            _history.Rows.Clear();
            return;
        }

        try
        {
            var orders = await _queries.GetOrderHistoryAsync(patientId);

            _history.SuspendLayout();
            _history.Rows.Clear();

            foreach (var order in orders)
            {
                var index = _history.Rows.Add(
                    order.AccessionNumber,
                    order.RegisteredAt.ToString("dd/MM/yyyy HH:mm"),
                    T($"OrderStatus.{order.Status}"),
                    order.Priority == Domain.Orders.OrderPriority.Routine
                        ? string.Empty
                        : T($"Priority.{order.Priority}"),
                    order.AnalysisCount.ToString(),
                    order.PrescriberName ?? string.Empty);

                var gridRow = _history.Rows[index];
                gridRow.Tag = order.Id;
                gridRow.Cells["status"].Style.ForeColor = UiTheme.ForOrderStatus(order.Status);
            }

            _history.ResumeLayout(performLayout: true);

            // Opening a patient's file is itself a logged access under loi 08-09.
            await _accessAuditor.LogAsync(DataAccessKind.ViewPatient, patientId,
                                          context: "PatientHistory", recordCount: orders.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the order history for {PatientId} failed.", patientId);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _debounce.Dispose();
        base.Dispose(disposing);
    }
}
