using System.Globalization;
using System.Text;
using MarocLIS.Application.Migration;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Migration;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// « Reprise de données »: the patients, prescribers, catalogue and tariffs, antériorités and
/// open balances of the application the laboratory used before, read from an Excel or CSV file or
/// straight from its database (read only), matched column by column and value by value, checked
/// row by row in a « Simulation », then imported in batches that a second run updates rather than
/// duplicates.
/// <para>
/// The order of the kinds is the order of the work: patients and prescribers first, the catalogue,
/// then the antériorités and balances that point at them.
/// </para>
/// </summary>
public sealed class DataMigrationView : LocalizedView
{
    private const string NotMapped = DataMigrationScreenRules.NotMapped;

    private readonly DataMigrationService _migration;
    private readonly IMigrationWorkbook _workbook;
    private readonly IUserDialogs _dialogs;
    private readonly ICultureManager _cultureManager;
    private readonly ILogger<DataMigrationView>? _logger;
    private readonly MigrationProfileStore _profiles;

    // Header and source card.
    private readonly Label _heading = new(), _caption = new();
    private readonly ContextMenustripHolder _menu = new();
    private readonly Label _kindLabel = new(), _systemLabel = new(), _profileLabel = new(), _originLabel = new(),
                           _sheetLabel = new(), _tableLabel = new(), _sourceTitle = new();
    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 230 };
    private readonly TextBox _system = UiTheme.Input(180);
    private readonly ComboBox _profile = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 210 };
    private readonly Button _saveProfile = UiTheme.SecondaryButton(string.Empty);
    private readonly RadioButton _fromFile = new() { AutoSize = true, Checked = true };
    private readonly RadioButton _fromDatabase = new() { AutoSize = true };
    private readonly Button _browse = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _fileName = new();
    private readonly ComboBox _sheet = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 170 };
    private readonly Button _template = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _connect = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _connection = new();
    private readonly ComboBox _table = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 260 };
    private readonly Button _query = UiTheme.SecondaryButton(string.Empty);
    private readonly CheckBox _linkByName = new() { AutoSize = true, Checked = true };
    private readonly FlowLayoutPanel _fileRow = new(), _databaseRow = new();

    // Tabs.
    private readonly FlatTabControl _tabs = new();
    private readonly TabPage _previewPage = new(), _mappingPage = new(), _correspondencePage = new(),
                             _reportPage = new(), _journalPage = new(), _oldNumberPage = new(), _balancePage = new();
    private readonly DataGridView _preview = UiTheme.Grid();
    private readonly DataGridView _mapping = UiTheme.Grid();
    private readonly DataGridView _correspondences = UiTheme.Grid();
    private readonly DataGridView _report = UiTheme.Grid();
    private readonly DataGridView _journal = UiTheme.Grid();
    private readonly DataGridView _oldNumbers = UiTheme.Grid();
    private readonly DataGridView _balances = UiTheme.Grid();
    private readonly Label _previewTitle = new(), _previewCount = new(), _mappingTitle = new(), _mappingCount = new(),
                           _correspondenceTitle = new(), _correspondenceCount = new(), _reportTitle = new(),
                           _reportCount = new(), _journalTitle = new(), _oldNumberTitle = new(), _balanceTitle = new(),
                           _balanceCount = new(), _oldNumberLabel = new();
    private readonly Label _mappingNote = new(), _correspondenceNote = new();
    private readonly Button _exportReport = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _autoMatch = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _refreshJournal = UiTheme.SecondaryButton(string.Empty);
    private readonly TextBox _oldNumber = UiTheme.Input(220);
    private readonly Button _findOldNumber = UiTheme.SecondaryButton(string.Empty);

    // Action bar.
    private readonly ProgressBar _progress = new() { Width = 180, Height = 10, Visible = false, Maximum = 1000 };
    private readonly Button _stop = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new();
    private readonly Button _simulate = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _import = UiTheme.PrimaryButton(string.Empty);

    // « Plus d'actions ».
    private readonly MenuAction _allTemplates = new(), _undoLast = new(), _exportProfile = new(),
                                _importProfile = new(), _deleteProfile = new();

    private SourceTable? _source;
    private string _sourceLabel = string.Empty;
    private string? _filePath;
    private SourceConnectionSettings? _settings;
    private bool _rememberPassword;
    private IReadOnlyList<SourceTableInfo> _tables = [];
    private string? _customQuery;
    private Dictionary<string, string> _columns = new(StringComparer.Ordinal);
    private CorrespondenceTables _correspondenceTables = new();
    private IReadOnlyDictionary<MigrationCodeList, IReadOnlyList<CodeTarget>> _targets =
        new Dictionary<MigrationCodeList, IReadOnlyList<CodeTarget>>();
    private MigrationAnalysis? _analysis;
    private MigrationSummary? _summary;
    private MigrationProfile? _currentProfile;
    private CancellationTokenSource? _run;
    private bool _filling;
    private string? _deferredStatus;

    public DataMigrationView(DataMigrationService migration, IMigrationWorkbook workbook, IUserDialogs dialogs,
                             ILocalizer localizer, ICultureManager cultureManager,
                             ILogger<DataMigrationView>? logger = null)
        : base(localizer, cultureManager)
    {
        _migration = migration;
        _workbook = workbook;
        _dialogs = dialogs;
        _cultureManager = cultureManager;
        _logger = logger;
        _profiles = MigrationProfilePasswords.Store();

        BuildLayout();
        Wire();

        PermissionGate.Require([Permissions.Administration.Migration],
            _browse, _connect, _query, _simulate, _import, _saveProfile, _findOldNumber, _undoLast);
    }

    public override string Title => T("Nav.Migration");

    private DataMigrationKind Kind => _kind.SelectedIndex >= 0 ? Enum.GetValues<DataMigrationKind>()[_kind.SelectedIndex] : DataMigrationKind.Patients;

    private bool Running => _run is not null;

    // ---- Layout ---------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = AdminLayout.Page;

        var more = FinanceUi.MoreButton(_menu.Menu, Localizer, _allTemplates, _undoLast, _exportProfile, _importProfile,
                                        _deleteProfile);
        var header = AdminLayout.PageHeader(_heading, _caption, more);

        _system.MaxLength = MigrationBatch.SourceSystemMaxLength;
        _system.Tag = UiTags.LatinValue;
        _oldNumber.MaxLength = MigrationRecord.OldNumberMaxLength;
        _oldNumber.Tag = UiTags.LatinValue;

        var origin = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0),
            BackColor = UiTheme.SurfaceRaised
        };
        _fromFile.Margin = new Padding(0, 6, 16, 0);
        _fromDatabase.Margin = new Padding(0, 6, 0, 0);
        origin.Controls.Add(_fromFile);
        origin.Controls.Add(_fromDatabase);

        var first = Flow();
        first.Controls.Add(FinanceUi.Stacked(_kindLabel, _kind));
        first.Controls.Add(FinanceUi.Stacked(_systemLabel, _system));
        first.Controls.Add(FinanceUi.Stacked(_profileLabel, _profile));
        _saveProfile.Margin = new Padding(0, 20, 24, 0);
        first.Controls.Add(FinanceUi.Fit(_saveProfile));
        first.Controls.Add(FinanceUi.Stacked(_originLabel, origin));

        Configure(_fileRow);
        AdminLayout.Muted(_fileName);
        _fileName.AutoSize = false;
        _fileName.AutoEllipsis = true;
        _fileName.Size = new Size(300, 32);
        _fileName.TextAlign = ContentAlignment.MiddleLeft;
        AddTo(_fileRow, FinanceUi.Fit(_browse), _fileName, Inline(_sheetLabel, _sheet), FinanceUi.Fit(_template));

        Configure(_databaseRow);
        AdminLayout.Muted(_connection);
        _connection.AutoSize = false;
        _connection.AutoEllipsis = true;
        _connection.Size = new Size(300, 32);
        _connection.TextAlign = ContentAlignment.MiddleLeft;
        AddTo(_databaseRow, FinanceUi.Fit(_connect), _connection, Inline(_tableLabel, _table), FinanceUi.Fit(_query));
        _databaseRow.Visible = false;

        var second = Flow();
        second.Controls.Add(_fileRow);
        second.Controls.Add(_databaseRow);
        _linkByName.Font = UiTheme.Body;
        _linkByName.Margin = new Padding(8, 9, 0, 0);
        second.Controls.Add(_linkByName);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(16, 4, 16, 4),
            BackColor = UiTheme.SurfaceRaised
        };
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.Controls.Add(first, 0, 0);
        body.Controls.Add(second, 0, 1);

        var sourceCard = AdminLayout.Spaced(AdminLayout.CardSection(_sourceTitle, body), new Padding(0, 0, 0, AdminLayout.Gap));
        sourceCard.Dock = DockStyle.Top;
        sourceCard.Height = 196;

        // The first row wraps on a narrow window (and the labels are longer in some languages): the
        // card takes the height its rows need instead of clipping the file or database row.
        void FitSourceCard()
        {
            if (!sourceCard.IsHandleCreated || !body.IsHandleCreated) return;
            var top = sourceCard.PointToClient(body.PointToScreen(Point.Empty)).Y;
            var below = sourceCard.Height - top - body.Height;
            var needed = top + second.Bottom + second.Margin.Bottom + body.Padding.Bottom + below;
            if (needed > 0 && needed != sourceCard.Height) sourceCard.Height = needed;
        }
        first.SizeChanged += (_, _) => FitSourceCard();
        second.SizeChanged += (_, _) => FitSourceCard();
        second.LocationChanged += (_, _) => FitSourceCard();
        sourceCard.HandleCreated += (_, _) => BeginInvoke(FitSourceCard);

        BuildGrids();

        _tabs.TabPages.AddRange([_previewPage, _mappingPage, _correspondencePage, _reportPage, _journalPage,
                                 _oldNumberPage, _balancePage]);

        _previewPage.Controls.Add(AdminLayout.CardSection(_previewTitle, _preview, [_previewCount], []));

        AdminLayout.Note(_mappingNote);
        var mappingCard = AdminLayout.CardSection(_mappingTitle, _mapping, [_mappingCount, AdminLayout.Action(_autoMatch)], []);
        _mappingPage.Controls.Add(mappingCard);
        _mappingPage.Controls.Add(_mappingNote);

        AdminLayout.Note(_correspondenceNote);
        _correspondencePage.Controls.Add(AdminLayout.CardSection(_correspondenceTitle, _correspondences, [_correspondenceCount], []));
        _correspondencePage.Controls.Add(_correspondenceNote);

        _reportPage.Controls.Add(AdminLayout.CardSection(_reportTitle, _report, [_reportCount, AdminLayout.Action(_exportReport)], []));

        _journalPage.Controls.Add(AdminLayout.CardSection(_journalTitle, _journal, [AdminLayout.Action(_refreshJournal)], []));

        _oldNumberPage.Controls.Add(AdminLayout.CardSection(_oldNumberTitle, _oldNumbers,
            [Inline(_oldNumberLabel, _oldNumber), AdminLayout.Action(_findOldNumber)], []));

        _balancePage.Controls.Add(AdminLayout.CardSection(_balanceTitle, _balances, [_balanceCount], []));

        foreach (var count in new[] { _previewCount, _mappingCount, _correspondenceCount, _reportCount, _balanceCount })
            AdminLayout.Muted(count);

        var bar = FinanceUi.ActionBar(_status, [_progress, _stop], _simulate, _import);
        _progress.Anchor = AnchorStyles.Left;
        _stop.Enabled = false;
        _import.Enabled = false;

        var tabsHost = AdminLayout.FlatTabs(_tabs);

        Controls.Add(tabsHost);
        Controls.Add(bar);
        Controls.Add(sourceCard);
        Controls.Add(header);
    }

    private static FlowLayoutPanel Flow() => new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = true,
        Margin = new Padding(0),
        BackColor = UiTheme.SurfaceRaised
    };

    private static void Configure(FlowLayoutPanel row)
    {
        row.AutoSize = true;
        row.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        row.WrapContents = false;
        row.Margin = new Padding(0, 4, 0, 4);
        row.BackColor = UiTheme.SurfaceRaised;
    }

    private static void AddTo(FlowLayoutPanel row, params Control[] controls)
    {
        // No trailing gap after the last control: mirrored (Arabic), that empty strip is left unpainted.
        foreach (var control in controls)
        {
            control.Margin = new Padding(0, 0, control == controls[^1] ? 0 : 10, 0);
            row.Controls.Add(control);
        }
    }

    private static FlowLayoutPanel Inline(Label caption, Control field)
    {
        var inline = FinanceUi.Inline(caption, field);
        inline.BackColor = UiTheme.SurfaceRaised;
        return inline;
    }

    private void BuildGrids()
    {
        _mapping.ReadOnly = false;
        _mapping.EditMode = DataGridViewEditMode.EditOnEnter;
        _mapping.SelectionMode = DataGridViewSelectionMode.CellSelect;
        Column(_mapping, "field", 26);
        Column(_mapping, "required", 10);
        _mapping.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "column",
            FillWeight = 26,
            DisplayMember = nameof(Choice.Display),
            ValueMember = nameof(Choice.Value),
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
        });
        Column(_mapping, "sample", 38);
        _mapping.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_mapping.IsCurrentCellDirty) _mapping.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _mapping.CellValueChanged += (_, e) => { if (e.RowIndex >= 0) OnMappingChanged(e.RowIndex); };
        _mapping.DataError += (_, e) => e.ThrowException = false;

        _correspondences.ReadOnly = false;
        _correspondences.EditMode = DataGridViewEditMode.EditOnEnter;
        _correspondences.SelectionMode = DataGridViewSelectionMode.CellSelect;
        Column(_correspondences, "list", 16);
        Column(_correspondences, "value", 22);
        Column(_correspondences, "count", 9, mono: true);
        _correspondences.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "target",
            FillWeight = 38,
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
        });
        Column(_correspondences, "state", 15);
        _correspondences.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_correspondences.IsCurrentCellDirty) _correspondences.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _correspondences.CellValueChanged += (_, e) => { if (e.RowIndex >= 0) OnCorrespondenceChanged(e.RowIndex); };
        _correspondences.DataError += (_, e) => e.ThrowException = false;
        AdminLayout.PillColumn(_correspondences, "state", row => row.Tag switch
        {
            CorrespondenceState.Missing => AdminLayout.Crit,
            CorrespondenceState.Suggested => AdminLayout.Warn,
            CorrespondenceState.Ignored => AdminLayout.Wait,
            _ => AdminLayout.Ok
        });

        Column(_report, "row", 7, mono: true);
        Column(_report, "field", 15);
        Column(_report, "severity", 11);
        Column(_report, "problem", 40);
        Column(_report, "suggestion", 27);
        AdminLayout.PillColumn(_report, "severity", row => row.Tag switch
        {
            MigrationSeverity.Blocking => AdminLayout.Crit,
            MigrationSeverity.Error => AdminLayout.Crit,
            MigrationSeverity.Warning => AdminLayout.Warn,
            _ => AdminLayout.Run
        });
        _report.Columns["problem"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _report.Columns["suggestion"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _report.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;

        foreach (var (name, weight) in new[]
                 {
                     ("started", 12), ("kind", 12), ("system", 11), ("source", 19), ("user", 10), ("rows", 6),
                     ("created", 6), ("updated", 7), ("skipped", 6), ("failed", 6), ("state", 10)
                 })
            Column(_journal, name, weight, mono: name is "rows" or "created" or "updated" or "skipped" or "failed");
        AdminLayout.PillColumn(_journal, "state", row => row.Tag switch
        {
            MigrationBatchRow { Undone: true } => AdminLayout.Wait,
            MigrationBatchRow { Interrupted: true } => AdminLayout.Warn,
            MigrationBatchRow { CompletedAt: null } => AdminLayout.Run,
            _ => AdminLayout.Ok
        });

        foreach (var (name, weight) in new[]
                 {
                     ("record", 11), ("patient", 18), ("birth", 9), ("oldNumber", 11), ("date", 10), ("analysis", 17),
                     ("value", 9), ("range", 9), ("accession", 10)
                 })
            Column(_oldNumbers, name, weight, mono: name is "record" or "oldNumber" or "accession");

        foreach (var (name, weight) in new[]
                 {
                     ("asOf", 10), ("patient", 22), ("payer", 18), ("amount", 11), ("invoice", 13), ("invoiceDate", 10), ("label", 22)
                 })
            Column(_balances, name, weight, mono: name is "amount" or "invoice");
        _balances.Columns["amount"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;

        foreach (var grid in new[] { _preview, _mapping, _correspondences, _report, _journal, _oldNumbers, _balances })
            AdminLayout.ListGrid(grid);
    }

    private static void Column(DataGridView grid, string name, int weight, bool mono = false)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            FillWeight = weight,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };
        if (mono) column.DefaultCellStyle.Font = UiTheme.Monospace;
        grid.Columns.Add(column);
    }

    private void Wire()
    {
        _kind.SelectedIndexChanged += async (_, _) => { if (!_filling) await OnKindChangedAsync(); };
        _profile.SelectedIndexChanged += async (_, _) => { if (!_filling) await OnProfileChangedAsync(); };
        _fromFile.CheckedChanged += (_, _) => ShowOrigin();
        _system.TextChanged += (_, _) => Invalidate(analysisOnly: true);
        _linkByName.CheckedChanged += (_, _) => Invalidate(analysisOnly: true);

        _browse.Click += async (_, _) => await BrowseAsync();
        _sheet.SelectedIndexChanged += async (_, _) => { if (!_filling && _filePath is not null) await ReadFileAsync(_filePath, SelectedSheet()); };
        _template.Click += (_, _) => SaveTemplate(Kind);
        _connect.Click += async (_, _) => await ConnectAsync();
        _table.SelectedIndexChanged += async (_, _) => { if (!_filling) await ReadTableAsync(); };
        _query.Click += async (_, _) => await QueryAsync();
        _saveProfile.Click += (_, _) => SaveProfile();
        _autoMatch.Click += async (_, _) => await AutoMatchAsync();
        _exportReport.Click += (_, _) => ExportReport();
        _refreshJournal.Click += async (_, _) => await LoadJournalAsync();
        _findOldNumber.Click += async (_, _) => await FindOldNumberAsync();
        _oldNumber.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await FindOldNumberAsync();
        };

        _stop.Click += (_, _) => _run?.Cancel();
        _simulate.Click += async (_, _) => await SimulateAsync();
        _import.Click += async (_, _) => await RunBusyAsync(ImportAsync);

        _allTemplates.Click += (_, _) => SaveAllTemplates();
        _undoLast.Click += async (_, _) => await RunBusyAsync(UndoLastAsync);
        _exportProfile.Click += (_, _) => ExportProfile();
        _importProfile.Click += (_, _) => ImportProfile();
        _deleteProfile.Click += (_, _) => DeleteProfile();
    }

    // ---- Texts ----------------------------------------------------------------------------------------

    protected override void ApplyTexts()
    {
        _heading.Text = T("Nav.Migration");
        _caption.Text = T("Migration.Caption");
        _sourceTitle.Text = T("Migration.SourceTitle");
        _kindLabel.Text = T("Migration.Kind");
        _systemLabel.Text = T("Migration.SourceSystem");
        _system.PlaceholderText = T("Migration.SourceSystemHint");
        _profileLabel.Text = T("Migration.Profile");
        _saveProfile.Text = T("Migration.SaveProfile");
        _originLabel.Text = T("Migration.Origin");
        _fromFile.Text = T("Migration.FromFile");
        _fromDatabase.Text = T("Migration.FromDatabase");
        _browse.Text = T("Migration.Browse");
        _sheetLabel.Text = T("Migration.Sheet");
        _template.Text = T("Migration.DownloadTemplate");
        _connect.Text = T("Migration.ConnectButton");
        _tableLabel.Text = T("Migration.Table");
        _query.Text = T("Migration.QueryButton");
        _linkByName.Text = T("Migration.LinkByName");

        _previewPage.Text = T("Migration.TabPreview");
        _mappingPage.Text = T("Migration.TabColumns");
        _correspondencePage.Text = T("Migration.TabCorrespondences");
        _reportPage.Text = T("Migration.TabReport");
        _journalPage.Text = T("Migration.TabJournal");
        _oldNumberPage.Text = T("Migration.TabOldNumbers");
        _balancePage.Text = T("Migration.TabBalances");

        _previewTitle.Text = T("Migration.PreviewTitle");
        _mappingTitle.Text = T("Migration.ColumnsTitle");
        _mappingNote.Text = T("Migration.ColumnsNote");
        _autoMatch.Text = T("Migration.AutoMatch");
        _correspondenceTitle.Text = T("Migration.CorrespondencesTitle");
        _correspondenceNote.Text = T("Migration.CorrespondencesNote");
        _reportTitle.Text = T("Migration.ReportTitle");
        _exportReport.Text = T("Migration.ExportReport");
        _journalTitle.Text = T("Migration.JournalTitle");
        _refreshJournal.Text = T("Action.Refresh");
        _oldNumberTitle.Text = T("Migration.OldNumbersTitle");
        _oldNumberLabel.Text = T("Migration.OldNumber");
        _findOldNumber.Text = T("Action.Search");
        _balanceTitle.Text = T("Migration.BalancesTitle");

        _stop.Text = T("Migration.Stop");
        _simulate.Text = T("Migration.Simulate");
        _import.Text = T("Migration.Import");

        _allTemplates.Text = T("Migration.AllTemplates");
        _undoLast.Text = T("Migration.UndoLast");
        _exportProfile.Text = T("Migration.ExportProfile");
        _importProfile.Text = T("Migration.ImportProfile");
        _deleteProfile.Text = T("Migration.DeleteProfile");

        Headers(_mapping, "Migration.ColField", "Migration.ColRequired", "Migration.ColSourceColumn", "Migration.ColSample");
        Headers(_correspondences, "Migration.ColList", "Migration.ColSourceValue", "Migration.ColCount",
                "Migration.ColTarget", "Migration.ColState");
        Headers(_report, "Migration.ColRow", "Migration.ColField", "Migration.ColSeverity", "Migration.ColProblem",
                "Migration.ColSuggestion");
        Headers(_journal, "Migration.ColStarted", "Migration.Kind", "Migration.SourceSystem", "Migration.ColSource",
                "Migration.ColUser", "Migration.ColRows", "Migration.ColCreated", "Migration.ColUpdated",
                "Migration.ColSkipped", "Migration.ColFailed", "Migration.ColState");
        Headers(_oldNumbers, "Migration.ColRecord", "Migration.ColPatient", "Migration.ColBirth", "Migration.ColOldNumber",
                "Migration.ColDate", "Migration.ColAnalysis", "Migration.ColValue", "Migration.ColRange",
                "Migration.ColAccession");
        Headers(_balances, "Migration.ColAsOf", "Migration.ColPatient", "Migration.ColPayer", "Migration.ColAmount",
                "Migration.ColInvoice", "Migration.ColInvoiceDate", "Migration.ColLabel");

        _filling = true;
        try
        {
            var kind = _kind.SelectedIndex;
            _kind.Items.Clear();
            foreach (var value in Enum.GetValues<DataMigrationKind>()) _kind.Items.Add(T($"MigrationKind.{value}"));
            _kind.SelectedIndex = kind >= 0 ? kind : 0;
            FillProfiles(_currentProfile?.Name);
        }
        finally
        {
            _filling = false;
        }

        ShowSource();
        RenderMapping();
        RenderCorrespondences();
        RenderReport();
        ShowStatus();
    }

    private void Headers(DataGridView grid, params string[] keys)
    {
        for (var i = 0; i < keys.Length && i < grid.Columns.Count; i++) grid.Columns[i].HeaderText = T(keys[i]);
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            await LoadJournalAsync();
            await LoadBalancesAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the data migration journal failed.");
        }
    }

    public override bool ConfirmLeave()
    {
        if (!Running) return true;
        if (!_dialogs.Confirm(T("Migration.ConfirmStop"), Title)) return false;
        _run?.Cancel();
        return true;
    }

    // ---- Source ---------------------------------------------------------------------------------------

    private void ShowOrigin()
    {
        _fileRow.Visible = _fromFile.Checked;
        _databaseRow.Visible = !_fromFile.Checked;
    }

    private void ShowSource()
    {
        _fileName.Text = _filePath is null ? T("Migration.NoFile") : Path.GetFileName(_filePath);
        _connection.Text = _settings is null ? T("Migration.NotConnected") : _settings.Describe();
        _sheet.Enabled = _sheet.Items.Count > 1;
        _table.Enabled = _settings is not null;
        _query.Enabled = _settings is not null && PermissionGate.Allows(Permissions.Administration.Migration);

        _previewCount.Text = DataMigrationScreenRules.RowsRead(_source, T);
        _deleteProfile.Enabled = _exportProfile.Enabled = _currentProfile is not null;
    }

    private async Task BrowseAsync()
    {
        using var picker = new OpenFileDialog
        {
            Title = UserDialogs.CaptionOf(_browse.Text, _browse.Text),
            Filter = $"{T("Migration.SourceFiles")} (*.xlsx;*.csv;*.txt)|*.xlsx;*.csv;*.txt",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        _filling = true;
        try
        {
            _sheet.Items.Clear();
            if (IsWorkbook(picker.FileName))
            {
                try
                {
                    foreach (var name in _workbook.SheetNames(picker.FileName)) _sheet.Items.Add(name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                               or FormatException or ArgumentException)
                {
                    _dialogs.Warn(T("Migration.FileUnreadable", ex.Message), _browse.Text);
                    return;
                }

                var preferred = DataMigrationScreenRules.PreferredSheet([.. _sheet.Items.Cast<string>()], _currentProfile, Kind);
                _sheet.SelectedIndex = preferred is null ? -1 : _sheet.Items.IndexOf(preferred);
            }
        }
        finally
        {
            _filling = false;
        }

        await ReadFileAsync(picker.FileName, SelectedSheet());
    }

    private static bool IsWorkbook(string path) => DataMigrationScreenRules.IsWorkbook(path);

    private string? SelectedSheet() => _sheet.SelectedItem as string;

    /// <summary>Reads a file, as the harness of the screenshots does too.</summary>
    internal async Task ReadFileAsync(string path, string? sheet)
    {
        await Run(T("Migration.Reading"), async ct =>
        {
            var table = await Task.Run(() =>
            {
                if (IsWorkbook(path)) return _workbook.Read(path, sheet, DataMigrationService.MaxRows);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return CsvSource.Read(stream, DataMigrationService.MaxRows);
            }, ct);

            _filePath = path;
            await UseSourceAsync(table, DataMigrationScreenRules.FileLabel(path, sheet));
        }, onFailure: ex => ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException
                            or ArgumentException
            ? T("Migration.FileUnreadable", ex.Message is { } m && m.StartsWith("Migration_", StringComparison.Ordinal) ? T(m) : ex.Message)
            : null);
    }

    private async Task ConnectAsync()
    {
        var initial = _settings?.Clone() ?? _currentProfile?.Connection?.Clone();
        if (initial is not null && _currentProfile is not null && initial.Password is null)
            initial.Password = MigrationProfilePasswords.PasswordOf(_currentProfile);

        using var dialog = new SourceConnectionDialog(_migration, _dialogs, Localizer, _cultureManager, initial,
                                                      _rememberPassword || _currentProfile?.ProtectedPassword is not null);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _settings = dialog.Settings;
        _rememberPassword = dialog.RememberPassword;
        ShowSource();
        await LoadTablesAsync();
    }

    private async Task LoadTablesAsync()
    {
        if (_settings is null) return;

        await Run(T("Migration.ListingTables"), async ct =>
        {
            var tables = await _migration.ListTablesAsync(_settings, ct);
            if (tables.IsFailure) { _dialogs.ShowFailure(tables, _connect.Text); return; }

            _tables = tables.Value;
            _filling = true;
            try
            {
                _table.Items.Clear();
                foreach (var table in _tables)
                    _table.Items.Add(table.IsView ? $"{table.Display}  ({T("Migration.View")})" : table.Display);

                var saved = _currentProfile?.For(Kind);
                var index = saved?.TableName is null
                    ? -1
                    : _tables.ToList().FindIndex(t => string.Equals(t.Name, saved.TableName, StringComparison.OrdinalIgnoreCase)
                                                      && string.Equals(t.Schema ?? "", saved.TableSchema ?? "", StringComparison.OrdinalIgnoreCase));
                _table.SelectedIndex = index;
                _customQuery = saved?.Query;
            }
            finally
            {
                _filling = false;
            }

            ShowSource();
            ShowStatus(T("Migration.TablesListed", _tables.Count));
        });

        if (_table.SelectedIndex >= 0) await ReadTableAsync();
        else if (_customQuery is not null) await ReadSelectionAsync(new SourceSelection(null, _customQuery));
    }

    private async Task ReadTableAsync()
    {
        if (_settings is null || _table.SelectedIndex < 0 || _table.SelectedIndex >= _tables.Count) return;
        _customQuery = null;
        await ReadSelectionAsync(new SourceSelection(_tables[_table.SelectedIndex], null));
    }

    private async Task QueryAsync()
    {
        if (_settings is null) return;

        using var dialog = new SourceQueryDialog(Localizer, _cultureManager, _customQuery);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _customQuery = dialog.Query;
        _filling = true;
        _table.SelectedIndex = -1;
        _filling = false;
        await ReadSelectionAsync(new SourceSelection(null, _customQuery));
    }

    private async Task ReadSelectionAsync(SourceSelection selection)
    {
        if (_settings is null) return;
        var settings = _settings;

        await Run(T("Migration.Reading"), async ct =>
        {
            var read = await _migration.ReadSourceAsync(settings, selection, DataMigrationService.MaxRows, ct);
            if (read.IsFailure) { _dialogs.ShowFailure(read, _connect.Text); return; }

            _filePath = null;
            await UseSourceAsync(read.Value, $"{settings.Describe()} › {selection.Describe()}");
        });
    }

    /// <summary>A table is read: preview, automatic matching, correspondences.</summary>
    internal async Task UseSourceAsync(SourceTable table, string label)
    {
        _source = table;
        _sourceLabel = DataMigrationScreenRules.SourceLabel(label);

        if (table.Truncated)
            _dialogs.Warn(T("Migration_TooManyRows", DataMigrationService.MaxRows), Title);

        RenderPreview();
        MatchColumns();
        await LoadTargetsAsync();
        Invalidate(analysisOnly: false);
        ShowSource();
        _tabs.SelectedTab = _mappingPage;
        ShowStatus(T("Migration.SourceRead", table.Rows.Count));
    }

    private void RenderPreview()
    {
        _preview.SuspendLayout();
        _preview.Rows.Clear();
        _preview.Columns.Clear();

        if (_source is not null)
        {
            foreach (var column in _source.Columns)
                _preview.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = column,
                    ReadOnly = true,
                    SortMode = DataGridViewColumnSortMode.NotSortable,
                    MinimumWidth = 90,
                    FillWeight = 100
                });

            // Many columns scroll sideways rather than squeezing every one to a few pixels.
            _preview.AutoSizeColumnsMode = _source.Columns.Count > 8
                ? DataGridViewAutoSizeColumnsMode.None
                : DataGridViewAutoSizeColumnsMode.Fill;
            if (_source.Columns.Count > 8) foreach (DataGridViewColumn column in _preview.Columns) column.Width = 140;

            foreach (var row in DataMigrationScreenRules.Preview(_source))
                _preview.Rows.Add([.. row.Select(v => (object?)v ?? string.Empty)]);
        }

        _preview.ResumeLayout(performLayout: true);
    }

    // ---- Mapping ----------------------------------------------------------------------------------------

    /// <summary>The saved matching of the profile where its columns exist, the automatic one for the rest.</summary>
    private void MatchColumns()
    {
        _columns = DataMigrationScreenRules.MatchColumns(Kind, _source, _currentProfile);
        RenderMapping();
    }

    private async Task AutoMatchAsync()
    {
        if (_source is null) return;
        _columns = ColumnMatcher.AutoMatch(Kind, _source.Columns);
        RenderMapping();
        Invalidate(analysisOnly: false);
        await Task.CompletedTask;
    }

    private void RenderMapping()
    {
        _filling = true;
        try
        {
            _mapping.Rows.Clear();
            var choices = DataMigrationScreenRules.ColumnChoices(_source, T).Select(c => new Choice(c.Value, c.Display)).ToList();
            ((DataGridViewComboBoxColumn)_mapping.Columns["column"]!).DataSource = choices;

            foreach (var field in MigrationSchema.Fields(Kind))
            {
                var column = _columns.GetValueOrDefault(field.Key) ?? NotMapped;
                var index = _mapping.Rows.Add(T(field.LabelKey), field.Required ? T("Migration.Required") : string.Empty,
                                              column, Sample(column));
                _mapping.Rows[index].Tag = field;
                if (DataMigrationScreenRules.IsMissing(field, _columns))
                    _mapping.Rows[index].Cells["field"].Style.ForeColor = UiTheme.Critical;
            }

            _mappingCount.Text = DataMigrationScreenRules.ColumnsMapped(Kind, _columns, T);
        }
        finally
        {
            _filling = false;
        }
    }

    /// <summary>The first values of a column, for the operator to recognise it.</summary>
    private string Sample(string column) => DataMigrationScreenRules.Sample(_source, column);

    private void OnMappingChanged(int rowIndex)
    {
        if (_filling) return;
        var row = _mapping.Rows[rowIndex];
        if (row.Tag is not MigrationField field) return;

        var column = row.Cells["column"].Value as string ?? NotMapped;
        DataMigrationScreenRules.SetColumn(_columns, field.Key, column);

        _filling = true;
        row.Cells["sample"].Value = Sample(column);
        row.Cells["field"].Style.ForeColor = DataMigrationScreenRules.IsMissing(field, _columns) ? UiTheme.Critical : UiTheme.TextPrimary;
        _mappingCount.Text = DataMigrationScreenRules.ColumnsMapped(Kind, _columns, T);
        _filling = false;

        if (field.Type == MigrationFieldType.Coded) RenderCorrespondences();
        Invalidate(analysisOnly: true);
    }

    // ---- Correspondences --------------------------------------------------------------------------------

    private async Task LoadTargetsAsync()
    {
        try
        {
            var context = await _migration.LoadContextAsync(Kind, SourceSystem());
            if (context.IsFailure) { _dialogs.ShowFailure(context, Title); return; }
            _targets = context.Value.Targets;
        }
        catch (Exception ex) when (!IsDisposed)
        {
            _logger?.LogError(ex, "Loading the data migration targets failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), Title);
        }

        RenderCorrespondences();
    }

    private void RenderCorrespondences()
    {
        _filling = true;
        try
        {
            _correspondences.Rows.Clear();
            if (_source is null) { _correspondenceCount.Text = string.Empty; return; }

            var rows = CorrespondenceSuggester.Rows(_source, _columns, Kind, _correspondenceTables, _targets);
            var missing = 0;

            foreach (var row in rows)
            {
                var index = _correspondences.Rows.Add(T($"MigrationCodeList.{row.List}"), row.SourceValue,
                                                      row.Count.ToString(CultureInfo.CurrentCulture), null, string.Empty);
                var gridRow = _correspondences.Rows[index];

                var target = DataMigrationScreenRules.InitialTarget(row);
                var choices = Choices(row.List, target);

                var cell = new DataGridViewComboBoxCell
                {
                    DisplayMember = nameof(Choice.Display),
                    ValueMember = nameof(Choice.Value),
                    DataSource = choices,
                    FlatStyle = FlatStyle.Flat,
                    DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
                };
                gridRow.Cells["target"] = cell;
                cell.Value = target;
                gridRow.Cells["target"].ReadOnly = false;

                var state = DataMigrationScreenRules.State(row.List, target, row.Suggested);
                if (state == CorrespondenceState.Missing) missing++;
                gridRow.Tag = state;
                gridRow.Cells["state"].Value = T($"Migration.State{state}");
                gridRow.Cells["list"].Tag = row.List;
            }

            _correspondenceCount.Text = DataMigrationScreenRules.CorrespondencesCount(rows.Count, missing, T);
            _correspondenceCount.ForeColor = missing > 0 ? UiTheme.Critical : UiTheme.TextSecondary;
        }
        finally
        {
            _filling = false;
        }
    }

    /// <summary>What a value of <paramref name="list"/> can become, in the operator's language.</summary>
    private List<Choice> Choices(MigrationCodeList list, string current) =>
        [.. DataMigrationScreenRules.TargetChoices(list, _targets, current, T).Select(c => new Choice(c.Value, c.Display))];

    private void OnCorrespondenceChanged(int rowIndex)
    {
        if (_filling) return;
        var row = _correspondences.Rows[rowIndex];
        if (row.Cells["list"].Tag is not MigrationCodeList list) return;

        var source = row.Cells["value"].Value as string ?? string.Empty;
        var target = row.Cells["target"].Value as string ?? NotMapped;
        _correspondenceTables.Set(list, source, target.Length == 0 ? null : target);

        _filling = true;
        var state = DataMigrationScreenRules.State(list, target, suggested: false);
        row.Tag = state;
        row.Cells["state"].Value = T($"Migration.State{state}");
        _filling = false;
        _correspondences.InvalidateRow(rowIndex);

        var missing = _correspondences.Rows.Cast<DataGridViewRow>().Count(r => r.Tag is CorrespondenceState.Missing);
        _correspondenceCount.Text = DataMigrationScreenRules.CorrespondencesCount(_correspondences.Rows.Count, missing, T);
        _correspondenceCount.ForeColor = missing > 0 ? UiTheme.Critical : UiTheme.TextSecondary;
        Invalidate(analysisOnly: true);
    }

    /// <summary>The correspondences shown, the suggestions accepted as they stand, for the request.</summary>
    private CorrespondenceTables SettledCorrespondences() =>
        DataMigrationScreenRules.Settled(_correspondenceTables,
            _correspondences.Rows.Cast<DataGridViewRow>()
                .Where(r => r.Cells["list"].Tag is MigrationCodeList)
                .Select(r => ((MigrationCodeList)r.Cells["list"].Tag!, r.Cells["value"].Value as string ?? string.Empty,
                              r.Cells["target"].Value as string)));

    // ---- Simulation and import ------------------------------------------------------------------------

    private string SourceSystem() => _system.Text.Trim();

    private MigrationRequest? Request()
    {
        if (DataMigrationScreenRules.RequestRefusal(_source, SourceSystem()) is { } refused)
        {
            _dialogs.Warn(T(refused), _simulate.Text);
            if (_source is not null) _system.Focus();
            return null;
        }

        _mapping.EndEdit();
        _correspondences.EndEdit();

        return DataMigrationScreenRules.Request(Kind, SourceSystem(), _sourceLabel, _source!, _columns,
                                                SettledCorrespondences(), _linkByName.Checked);
    }

    private async Task SimulateAsync()
    {
        if (Request() is not { } request) return;

        await Run(T("Migration.StepAnalyse"), async ct =>
        {
            var analysis = await _migration.AnalyseAsync(request, Progress(), ct);
            if (analysis.IsFailure) { _dialogs.ShowFailure(analysis, _simulate.Text); return; }

            _analysis = analysis.Value;
            _summary = null;
            RenderReport();
            _tabs.SelectedTab = _reportPage;
            ShowStatus();
        });
    }

    private async Task ImportAsync()
    {
        if (DataMigrationScreenRules.ImportRefusal(_analysis, T) is { } refused) { _dialogs.Warn(refused, _import.Text); return; }
        if (Request() is not { } request) return;

        if (!_dialogs.Confirm(DataMigrationScreenRules.ConfirmImport(_analysis!, Kind, T), _import.Text)) return;

        await Run(T("Migration.StepImport"), async ct =>
        {
            var imported = await _migration.ImportAsync(request, Progress(), ct);
            if (imported.IsFailure) { _dialogs.ShowFailure(imported, _import.Text); return; }

            _summary = imported.Value;
            _analysis = null;
            RenderReport();
            ShowStatus();

            _dialogs.Info(DataMigrationScreenRules.SummaryMessage(_summary, T), _import.Text);
        });

        await LoadJournalAsync();
        if (Kind == DataMigrationKind.OpeningBalances) await LoadBalancesAsync();
    }

    private IProgress<MigrationProgress> Progress() => new Progress<MigrationProgress>(p =>
    {
        if (IsDisposed) return;
        _progress.Value = DataMigrationScreenRules.Thousandths(p);
        _status.Text = DataMigrationScreenRules.Progress(p, T);
    });

    /// <summary>Settings changed since the simulation: it no longer says what an import would do.</summary>
    private void Invalidate(bool analysisOnly)
    {
        if (_filling) return;
        if (!analysisOnly) RenderCorrespondences();
        if (_analysis is null) { ShowStatus(); return; }
        _analysis = null;
        RenderReport();
        ShowStatus();
    }

    private void RenderReport()
    {
        _report.SuspendLayout();
        _report.Rows.Clear();

        var anomalies = DataMigrationScreenRules.Anomalies(_summary, _analysis);
        foreach (var line in DataMigrationScreenRules.ReportLines(anomalies, T))
        {
            var index = _report.Rows.Add(line.Row, line.Field, T($"MigrationSeverity.{line.Severity}"), line.Problem, line.Suggestion);
            _report.Rows[index].Tag = line.Severity;
        }

        _report.ResumeLayout(performLayout: true);

        var (counts, critical) = DataMigrationScreenRules.ReportCounts(_summary, _analysis, T);
        _reportCount.Text = counts;
        _reportCount.ForeColor = critical ? UiTheme.Critical : UiTheme.TextSecondary;

        _exportReport.Enabled = anomalies.Count > 0;
        _import.Enabled = PermissionGate.User is { } user && DataMigrationScreenRules.CanImport(user, _analysis);
    }

    private void ShowStatus(string? message = null)
    {
        // Said once the operation has ended: the progress line holds the status meanwhile.
        if (Running) { _deferredStatus = message; return; }
        _progress.Visible = false;

        if (message is not null) { _status.Text = message; _status.ForeColor = UiTheme.TextSecondary; return; }

        var (text, critical) = DataMigrationScreenRules.Status(_source, _analysis, _summary, T);
        _status.Text = text;
        _status.ForeColor = critical ? UiTheme.Critical : UiTheme.TextSecondary;
    }

    private void ExportReport()
    {
        var anomalies = DataMigrationScreenRules.Anomalies(_summary, _analysis);
        if (anomalies.Count == 0) return;

        using var dialog = new SaveFileDialog
        {
            Title = UserDialogs.CaptionOf(_exportReport.Text, _exportReport.Text),
            FileName = DataMigrationScreenRules.ReportFileName(Kind, DateTime.Now),
            Filter = "CSV (*.csv)|*.csv",
            DefaultExt = "csv",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, ReportCsv(anomalies));
            _dialogs.Info(T("Migration.ReportSaved", dialog.FileName), _exportReport.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Warn($"{T(FailureMessages.KeyFor(ex))}\n{ex.Message}", _exportReport.Text);
        }
    }

    /// <summary>The anomaly report as a « ; » CSV a French Excel opens: row, field, severity, problem, suggestion.</summary>
    internal byte[] ReportCsv(IReadOnlyList<MigrationAnomaly> anomalies) => DataMigrationScreenRules.ReportCsv(anomalies, T);

    // ---- Kind and profile -----------------------------------------------------------------------------

    private async Task OnKindChangedAsync()
    {
        _summary = null;
        _analysis = null;
        MatchColumns();
        await LoadTargetsAsync();
        Invalidate(analysisOnly: false);
        RenderReport();

        // The table the profile read this kind from, when connected.
        if (_settings is not null && _currentProfile?.For(Kind) is { } saved && (saved.TableName is not null || saved.Query is not null))
        {
            var index = _tables.ToList().FindIndex(t => string.Equals(t.Name, saved.TableName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index != _table.SelectedIndex) { _table.SelectedIndex = index; return; }
        }

        ShowStatus();
    }

    private void FillProfiles(string? selected)
    {
        var wasFilling = _filling;
        _filling = true;
        try
        {
            _profile.Items.Clear();
            _profile.Items.Add(T("Migration.NoProfile"));
            IReadOnlyList<string> names;
            try { names = _profiles.Names(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { names = []; }
            foreach (var name in names) _profile.Items.Add(name);

            var index = selected is null ? 0 : _profile.Items.IndexOf(selected);
            _profile.SelectedIndex = Math.Max(0, index);
        }
        finally
        {
            _filling = wasFilling;
        }
    }

    private async Task OnProfileChangedAsync()
    {
        if (_profile.SelectedIndex <= 0) { _currentProfile = null; ShowSource(); return; }

        try
        {
            _currentProfile = _profiles.Load((string)_profile.SelectedItem!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _dialogs.Warn(T("Migration.ProfileUnreadable", ex.Message), _profileLabel.Text);
            _currentProfile = null;
        }

        if (_currentProfile is null) { ShowSource(); return; }
        await ApplyProfileAsync(_currentProfile);
    }

    private async Task ApplyProfileAsync(MigrationProfile profile)
    {
        _filling = true;
        _system.Text = profile.SourceSystem;
        _linkByName.Checked = profile.LinkByNameAndBirthDate;
        _filling = false;

        _correspondenceTables = profile.Correspondences.Clone();
        _rememberPassword = profile.ProtectedPassword is not null;

        if (profile.Connection is not null)
        {
            _settings = profile.Connection.Clone();
            _settings.Password = MigrationProfilePasswords.PasswordOf(profile);
            _fromDatabase.Checked = true;
        }

        if (_source is not null) MatchColumns();
        await LoadTargetsAsync();
        Invalidate(analysisOnly: false);
        ShowSource();

        // A profile with a password kept, or Windows authentication: its tables can be listed now.
        if (_settings is not null && (_settings.IntegratedSecurity || _settings.Password is not null
                                      || _settings.Provider == SourceProvider.Odbc))
            await LoadTablesAsync();
    }

    private MigrationProfile CurrentAsProfile(string name)
    {
        var table = _settings is not null && _table.SelectedIndex >= 0 && _table.SelectedIndex < _tables.Count
            ? _tables[_table.SelectedIndex]
            : null;

        return DataMigrationScreenRules.ToProfile(_currentProfile, name, Kind, SourceSystem(), _linkByName.Checked,
                                                  SettledCorrespondences(), _columns, _source is not null,
                                                  SelectedSheet(), _filePath is not null, _settings, table, _customQuery);
    }

    private void SaveProfile()
    {
        var suggested = DataMigrationScreenRules.SuggestedProfileName(_currentProfile, SourceSystem());
        var name = _dialogs.PromptForText(T("Migration.ProfileNamePrompt"), _saveProfile.Text, suggested,
                                          MigrationProfile.NameMaxLength);
        name = MigrationProfileStore.CleanName(name);
        if (name.Length == 0) return;

        try
        {
            var profile = CurrentAsProfile(name);
            _profiles.Save(profile, _settings?.Password, _rememberPassword);
            _currentProfile = profile;
            FillProfiles(profile.Name);
            ShowSource();
            ShowStatus(T("Migration.ProfileSaved", profile.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or System.Security.Cryptography.CryptographicException)
        {
            _dialogs.Warn(T("Migration.ProfileNotSaved", ex.Message.StartsWith("Migration_", StringComparison.Ordinal) ? T(ex.Message) : ex.Message),
                          _saveProfile.Text);
        }
    }

    private void DeleteProfile()
    {
        if (_currentProfile is null) return;
        if (!_dialogs.Confirm(T("Migration.ConfirmDeleteProfile", _currentProfile.Name), _deleteProfile.Text)) return;

        try
        {
            _profiles.Delete(_currentProfile.Name);
            _currentProfile = null;
            FillProfiles(null);
            ShowSource();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Warn(ex.Message, _deleteProfile.Text);
        }
    }

    private void ExportProfile()
    {
        if (_currentProfile is null) return;

        using var dialog = new SaveFileDialog
        {
            Title = UserDialogs.CaptionOf(_exportProfile.Text, _exportProfile.Text),
            FileName = DataMigrationScreenRules.ProfileFileName(_currentProfile),
            Filter = "JSON (*.json)|*.json",
            DefaultExt = "json",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, MigrationProfileStore.Export(_currentProfile));
            _dialogs.Info(T("Migration.ProfileExported", dialog.FileName), _exportProfile.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Warn(ex.Message, _exportProfile.Text);
        }
    }

    private void ImportProfile()
    {
        using var picker = new OpenFileDialog
        {
            Title = UserDialogs.CaptionOf(_importProfile.Text, _importProfile.Text),
            Filter = "JSON (*.json)|*.json",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            if (!DataMigrationScreenRules.IsReadableProfileSize(new FileInfo(picker.FileName).Length)) { _dialogs.Warn(T("Migration.ProfileUnreadable", "> 5 Mo"), _importProfile.Text); return; }

            var profile = MigrationProfileStore.Import(File.ReadAllBytes(picker.FileName));
            if (profile.Name.Length == 0) profile.Name = Path.GetFileNameWithoutExtension(picker.FileName);
            _profiles.Save(profile, null, rememberPassword: false);
            _currentProfile = null;
            FillProfiles(profile.Name);
            _ = OnProfileChangedAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or ArgumentException or NotSupportedException)
        {
            _dialogs.Warn(T("Migration.ProfileUnreadable", ex.Message), _importProfile.Text);
        }
    }

    // ---- Templates ------------------------------------------------------------------------------------

    private void SaveTemplate(DataMigrationKind kind)
    {
        using var dialog = new SaveFileDialog
        {
            Title = UserDialogs.CaptionOf(_template.Text, _template.Text),
            FileName = MigrationTemplates.FileName(kind, ".xlsx"),
            Filter = "Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
            DefaultExt = "xlsx",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            WriteTemplate(kind, dialog.FileName);
            _dialogs.Info(T("Migration.TemplateSaved", dialog.FileName), _template.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Warn($"{T(FailureMessages.KeyFor(ex))}\n{ex.Message}", _template.Text);
        }
    }

    private void WriteTemplate(DataMigrationKind kind, string path)
    {
        if (string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase))
            File.WriteAllBytes(path, MigrationTemplates.Csv(kind));
        else
            _workbook.WriteTemplate(kind, path);
    }

    private void SaveAllTemplates()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = T("Migration.TemplatesFolder"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            foreach (var kind in Enum.GetValues<DataMigrationKind>())
            {
                WriteTemplate(kind, Path.Combine(dialog.SelectedPath, MigrationTemplates.FileName(kind, ".xlsx")));
                WriteTemplate(kind, Path.Combine(dialog.SelectedPath, MigrationTemplates.FileName(kind, ".csv")));
            }

            _dialogs.Info(T("Migration.TemplatesSaved", dialog.SelectedPath), _allTemplates.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Warn($"{T(FailureMessages.KeyFor(ex))}\n{ex.Message}", _allTemplates.Text);
        }
    }

    // ---- Journal, old numbers, balances ---------------------------------------------------------------

    private async Task LoadJournalAsync()
    {
        var batches = await _migration.GetBatchesAsync();
        if (batches.IsFailure) return;

        var culture = CultureInfo.CurrentCulture;
        _journal.Rows.Clear();
        foreach (var batch in batches.Value)
        {
            var index = _journal.Rows.Add(
                batch.StartedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", culture),
                T($"MigrationKind.{batch.Kind}"), batch.SourceSystem, batch.SourceLabel, batch.UserName ?? string.Empty,
                batch.Rows.ToString("N0", culture), batch.Created.ToString("N0", culture), batch.Updated.ToString("N0", culture),
                batch.Skipped.ToString("N0", culture), batch.Failed.ToString("N0", culture), T(DataMigrationScreenRules.BatchState(batch)));
            _journal.Rows[index].Tag = batch;
        }

        _undoLast.Enabled = PermissionGate.User is { } user && DataMigrationScreenRules.CanUndo(user, batches.Value);
    }

    private async Task UndoLastAsync()
    {
        var batches = await _migration.GetBatchesAsync();
        if (batches.IsFailure) { _dialogs.ShowFailure(batches, _undoLast.Text); return; }

        var last = DataMigrationScreenRules.LastUndoable(batches.Value);
        if (last is null) { _dialogs.Info(T("Migration_NothingToUndo"), _undoLast.Text); return; }

        if (!_dialogs.Confirm(DataMigrationScreenRules.ConfirmUndo(last, T), _undoLast.Text)) return;

        try
        {
            var undone = await _migration.UndoLastAsync();
            if (undone.IsFailure) { _dialogs.ShowFailure(undone, _undoLast.Text); return; }

            _dialogs.Info(T("Migration.Undone", undone.Value.Removed, undone.Value.Kept), _undoLast.Text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Undoing the last data migration failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), _undoLast.Text);
        }

        await LoadJournalAsync();
        await LoadBalancesAsync();
    }

    private async Task FindOldNumberAsync()
    {
        var number = _oldNumber.Text.Trim();
        try
        {
            var found = await _migration.FindByOldNumberAsync(number);
            if (found.IsFailure) { _dialogs.ShowFailure(found, _findOldNumber.Text); return; }

            RenderOldNumbers(found.Value);
            if (found.Value.Count == 0) _dialogs.Info(T("Migration.OldNumberNotFound", number), _findOldNumber.Text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Looking up an old number failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), _findOldNumber.Text);
        }
    }

    internal void RenderOldNumbers(IReadOnlyList<OldNumberMatch> matches)
    {
        _oldNumbers.Rows.Clear();

        foreach (var line in DataMigrationScreenRules.OldNumberLines(matches))
            _oldNumbers.Rows.Add(line.Record, line.Patient, line.Birth, line.OldNumber, line.Date, line.Analysis,
                                 line.Value, line.Range, line.Accession);
    }

    private async Task LoadBalancesAsync()
    {
        var balances = await _migration.GetOpeningBalancesAsync();
        if (balances.IsFailure) return;

        var culture = CultureInfo.CurrentCulture;
        _balances.Rows.Clear();
        foreach (var balance in balances.Value)
            _balances.Rows.Add(
                balance.AsOf.ToString("dd/MM/yyyy", culture),
                balance.PatientRecordNumber is null ? string.Empty : $"{balance.PatientRecordNumber} — {balance.PatientName}",
                balance.PayerCode is null ? string.Empty : $"{balance.PayerCode} — {balance.PayerName}",
                balance.Amount.ToString("N2", culture), balance.OldInvoiceNumber ?? string.Empty,
                balance.OldInvoiceDate?.ToString("dd/MM/yyyy", culture) ?? string.Empty, balance.Label ?? string.Empty);

        _balanceCount.Text = DataMigrationScreenRules.BalancesTotal(balances.Value, T);
    }

    // ---- Long operations ------------------------------------------------------------------------------

    /// <summary>
    /// Runs a long operation with the progress bar and « Arrêter »: reading a large table, a
    /// simulation, an import. One at a time; the screen stays usable to look at the tabs.
    /// </summary>
    private async Task Run(string step, Func<CancellationToken, Task> work, Func<Exception, string?>? onFailure = null)
    {
        if (Running) return;

        using var run = new CancellationTokenSource();
        _run = run;
        BeginOperation();
        SetRunning(true, step);

        try
        {
            await work(run.Token);
        }
        catch (OperationCanceledException)
        {
            _deferredStatus = T("Migration.Stopped");
        }
        catch (Exception ex) when (!IsDisposed)
        {
            var message = onFailure?.Invoke(ex);
            if (message is null) _logger?.LogError(ex, "A data migration step failed.");
            _dialogs.Warn(message ?? T(FailureMessages.KeyFor(ex)), Title);
        }
        finally
        {
            _run = null;
            if (!IsDisposed) SetRunning(false, null);
            EndOperation();
        }
    }

    private void SetRunning(bool running, string? step)
    {
        _progress.Visible = running;
        _progress.Value = 0;
        _progress.Style = running ? ProgressBarStyle.Continuous : ProgressBarStyle.Blocks;
        _stop.Enabled = running;

        var allowed = PermissionGate.Allows(Permissions.Administration.Migration);
        foreach (var control in new Control[] { _browse, _connect, _simulate, _kind, _profile, _sheet, _table, _template })
            control.Enabled = !running && (allowed || control == _kind || control == _profile || control == _template);
        _query.Enabled = !running && allowed && _settings is not null;

        if (running)
        {
            _import.Enabled = false;
            _status.Text = step ?? string.Empty;
            _status.ForeColor = UiTheme.TextSecondary;
        }
        else
        {
            ShowSource();
            RenderReport();
            var message = _deferredStatus;
            _deferredStatus = null;
            ShowStatus(message);
        }
    }

    private sealed record Choice(string Value, string Display);

    /// <summary>The « Plus d'actions » menu, disposed with the view.</summary>
    private sealed class ContextMenustripHolder : IDisposable
    {
        public ContextMenuStrip Menu { get; } = new();

        public void Dispose() => Menu.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _run?.Cancel();
            _menu.Dispose();
        }

        base.Dispose(disposing);
    }
}
