using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Catalog;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The analysis catalogue: analyses, their parameters, reference intervals, tariffs, panels, reflex
/// rules and the nomenclature lists underneath them.
/// <para>
/// Until this screen existed the catalogue was whatever the seeder wrote. A laboratory could not
/// add an analysis, correct a reference interval or enter a tariff revision without a developer
/// editing seed data and redeploying — and neither of those is an exceptional event: ISO 15189
/// §5.5.2 obliges the laboratory to review its intervals periodically, and the national reference
/// tariff is revised by decree.
/// </para>
/// <para>
/// The shape is master over detail rather than seven independent tabs. A parameter has no meaning
/// away from its analysis and an interval none away from its parameter, so selecting an analysis
/// drives the parameter list, and selecting a parameter drives the interval list. The operator's
/// question is always "what does <em>this</em> analysis do?", never "show me all intervals".
/// </para>
/// <para>
/// Three columns carry a warning rather than a value, and they are the reason the screen is worth
/// more than a set of forms: an analysis that cannot be ordered because it has no tariff or no
/// active parameter, a parameter that cannot be completed because it has no picker options, and a
/// reflex rule whose condition can never be met. All three look configured until somebody depends
/// on them.
/// </para>
/// </summary>
public sealed class CatalogView : LocalizedView
{
    private readonly SelectionLoads _selectionLoads = new();
    private readonly CatalogMaintenanceService _catalog;
    private readonly IUserDialogs _dialogs;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly ILogger<CatalogView>? _logger;

    private readonly TabControl _tabs = new();

    // Analyses tab
    private readonly TextBox _search = UiTheme.Input(260);
    private readonly ComboBox _departmentFilter = new();
    private readonly CheckBox _includeInactive = new();
    private readonly DataGridView _analyses = UiTheme.Grid();
    private readonly DataGridView _parameters = UiTheme.Grid();
    private readonly DataGridView _ranges = UiTheme.Grid();
    private readonly Label _analysisHeading = new();
    private readonly Label _parameterHeading = new();
    private readonly Label _rangeHeading = new();

    private readonly Button _newAnalysis = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editAnalysis = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _newParameter = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editParameter = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _newRange = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editRange = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _reviewRange = UiTheme.SecondaryButton(string.Empty);

    // Tariffs tab
    private readonly DataGridView _tariffs = UiTheme.Grid();
    private readonly Label _tariffHeading = new();
    private readonly Button _newTariff = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editTariff = UiTheme.SecondaryButton(string.Empty);

    // Panels tab
    private readonly DataGridView _panels = UiTheme.Grid();
    private readonly DataGridView _panelMembers = UiTheme.Grid();
    private readonly Button _newPanel = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editPanel = UiTheme.SecondaryButton(string.Empty);

    // Reflex tab
    private readonly DataGridView _reflex = UiTheme.Grid();
    private readonly Button _newReflex = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editReflex = UiTheme.SecondaryButton(string.Empty);

    // Nomenclature tab
    private readonly ComboBox _nomenclatureKind = new();
    private readonly DataGridView _nomenclature = UiTheme.Grid();
    private readonly Button _newNomenclature = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editNomenclature = UiTheme.SecondaryButton(string.Empty);

    private readonly Label _status = new();

    private IReadOnlyList<AnalysisRow> _analysisRows = [];
    private IReadOnlyList<ParameterRow> _parameterRows = [];
    private IReadOnlyList<ReferenceRangeRow> _rangeRows = [];
    private IReadOnlyList<PanelRow> _panelRows = [];
    private CatalogLookups _lookups = CatalogLookups.Empty;

    public CatalogView(CatalogMaintenanceService catalog, IUserDialogs dialogs,
                       ICurrentUser currentUser, IClock clock,
                       ILocalizer localizer, ICultureManager cultureManager,
                       ILogger<CatalogView>? logger = null)
        : base(localizer, cultureManager)
    {
        _catalog = catalog;
        _dialogs = dialogs;
        _currentUser = currentUser;
        _clock = clock;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Catalog");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        _tabs.Dock = DockStyle.Fill;
        _tabs.Font = UiTheme.Body;

        _tabs.TabPages.Add(BuildAnalysesTab());
        _tabs.TabPages.Add(BuildTariffsTab());
        _tabs.TabPages.Add(BuildPanelsTab());
        _tabs.TabPages.Add(BuildReflexTab());
        _tabs.TabPages.Add(BuildNomenclatureTab());

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 26, BackColor = UiTheme.Surface };

        _status.Font = UiTheme.Small;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;

        footer.Controls.Add(_status);

        Controls.Add(_tabs);
        Controls.Add(footer);
    }

    private TabPage BuildAnalysesTab()
    {
        var page = new TabPage { BackColor = UiTheme.Surface, Padding = new Padding(8) };

        var bar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = UiTheme.Surface };

        _search.Location = new Point(0, 4);

        _departmentFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _departmentFilter.FlatStyle = FlatStyle.Flat;
        _departmentFilter.Bounds = new Rectangle(270, 4, 220, UiTheme.InputHeight);

        _includeInactive.Font = UiTheme.Small;
        _includeInactive.ForeColor = UiTheme.TextSecondary;
        _includeInactive.AutoSize = true;
        _includeInactive.Location = new Point(504, 8);
        _includeInactive.Checked = true;

        bar.Controls.AddRange([_search, _departmentFilter, _includeInactive]);

        // Analyses on the left, the selected analysis's parameters top right, the selected
        // parameter's intervals bottom right.
        var outer = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 640,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        var inner = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 230,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        outer.Panel1.Controls.Add(Section(_analysisHeading, _analyses,
                                           [_newAnalysis, _editAnalysis]));

        inner.Panel1.Controls.Add(Section(_parameterHeading, _parameters,
                                           [_newParameter, _editParameter]));

        inner.Panel2.Controls.Add(Section(_rangeHeading, _ranges,
                                           [_newRange, _editRange, _reviewRange]));

        outer.Panel2.Controls.Add(inner);

        BuildAnalysisColumns();
        BuildParameterColumns();
        BuildRangeColumns();

        page.Controls.Add(outer);
        page.Controls.Add(bar);

        return page;
    }

    private TabPage BuildTariffsTab()
    {
        var page = new TabPage { BackColor = UiTheme.Surface, Padding = new Padding(8) };

        BuildTariffColumns();

        var notice = new Label
        {
            Text = T("Catalog.TariffNotice"),
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            Dock = DockStyle.Bottom,
            Height = 34
        };

        notice.Name = "tariffNotice";

        page.Controls.Add(Section(_tariffHeading, _tariffs, [_newTariff, _editTariff]));
        page.Controls.Add(notice);

        return page;
    }

    private TabPage BuildPanelsTab()
    {
        var page = new TabPage { BackColor = UiTheme.Surface, Padding = new Padding(8) };

        BuildPanelColumns();

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 640,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        split.Panel1.Controls.Add(Section(new Label(), _panels, [_newPanel, _editPanel]));
        split.Panel2.Controls.Add(Section(new Label(), _panelMembers, []));

        page.Controls.Add(split);

        return page;
    }

    private TabPage BuildReflexTab()
    {
        var page = new TabPage { BackColor = UiTheme.Surface, Padding = new Padding(8) };

        BuildReflexColumns();

        var notice = new Label
        {
            Text = T("Catalog.ReflexNotice"),
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            Dock = DockStyle.Bottom,
            Height = 34,
            Name = "reflexNotice"
        };

        page.Controls.Add(Section(new Label(), _reflex, [_newReflex, _editReflex]));
        page.Controls.Add(notice);

        return page;
    }

    private TabPage BuildNomenclatureTab()
    {
        var page = new TabPage { BackColor = UiTheme.Surface, Padding = new Padding(8) };

        var bar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = UiTheme.Surface };

        _nomenclatureKind.DropDownStyle = ComboBoxStyle.DropDownList;
        _nomenclatureKind.FlatStyle = FlatStyle.Flat;
        _nomenclatureKind.Bounds = new Rectangle(0, 4, 260, UiTheme.InputHeight);

        bar.Controls.Add(_nomenclatureKind);

        BuildNomenclatureColumns();

        page.Controls.Add(Section(new Label(), _nomenclature,
                                   [_newNomenclature, _editNomenclature]));
        page.Controls.Add(bar);

        return page;
    }

    /// <summary>A heading, a grid and a row of buttons — the shape every section of this screen has.</summary>
    private static Panel Section(Label heading, DataGridView grid, Button[] buttons)
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };

        heading.Font = UiTheme.SmallBold;
        heading.ForeColor = UiTheme.TextSecondary;
        heading.Dock = DockStyle.Top;
        heading.Height = heading.Text.Length == 0 && buttons.Length == 0 ? 0 : 22;

        grid.Dock = DockStyle.Fill;

        host.Controls.Add(grid);
        host.Controls.Add(heading);

        if (buttons.Length > 0)
        {
            var actions = new Panel
            {
                Dock = DockStyle.Bottom, Height = 40, BackColor = UiTheme.Surface
            };

            var x = 0;

            foreach (var button in buttons)
            {
                button.Bounds = new Rectangle(x, 6, 170, UiTheme.ButtonHeight);
                actions.Controls.Add(button);
                x += 178;
            }

            host.Controls.Add(actions);
        }

        return host;
    }

    private static void AddColumns(DataGridView grid,
                                    params (string Name, int Weight, Font? Font, bool Right)[] columns)
    {
        grid.Columns.Clear();

        foreach (var (name, weight, font, right) in columns)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body,
                    Alignment = right ? DataGridViewContentAlignment.MiddleRight
                                      : DataGridViewContentAlignment.MiddleLeft
                }
            });
        }
    }

    private void BuildAnalysisColumns() => AddColumns(_analyses,
        ("code", 12, UiTheme.MonospaceBold, false),
        ("name", 30, null, false),
        ("department", 10, UiTheme.Small, false),
        ("specimen", 14, UiTheme.Small, false),
        ("parameters", 8, UiTheme.Monospace, true),
        ("price", 10, UiTheme.Monospace, true),
        ("turnaround", 8, UiTheme.Monospace, true),
        ("flags", 12, UiTheme.SmallBold, false));

    private void BuildParameterColumns() => AddColumns(_parameters,
        ("code", 12, UiTheme.MonospaceBold, false),
        ("name", 26, null, false),
        ("type", 14, UiTheme.Small, false),
        ("unit", 8, UiTheme.Monospace, false),
        ("range", 16, UiTheme.Monospace, false),
        ("critical", 12, UiTheme.Monospace, false),
        ("delta", 7, UiTheme.Monospace, true),
        ("flags", 12, UiTheme.SmallBold, false));

    private void BuildRangeColumns() => AddColumns(_ranges,
        ("population", 26, UiTheme.Small, false),
        ("interval", 16, UiTheme.MonospaceBold, false),
        ("validity", 18, UiTheme.Monospace, false),
        ("reviewed", 12, UiTheme.Monospace, false),
        ("source", 18, UiTheme.Small, false),
        ("flags", 10, UiTheme.SmallBold, false));

    private void BuildTariffColumns() => AddColumns(_tariffs,
        ("payer", 22, null, false),
        ("validity", 18, UiTheme.Monospace, false),
        ("letterkey", 8, UiTheme.Monospace, false),
        ("coefficient", 10, UiTheme.Monospace, true),
        ("base", 12, UiTheme.Monospace, true),
        ("price", 12, UiTheme.MonospaceBold, true),
        ("excess", 12, UiTheme.Monospace, true),
        ("vat", 8, UiTheme.Small, false));

    private void BuildPanelColumns()
    {
        AddColumns(_panels,
            ("code", 12, UiTheme.MonospaceBold, false),
            ("name", 32, null, false),
            ("members", 10, UiTheme.Monospace, true),
            ("components", 14, UiTheme.Monospace, true),
            ("package", 14, UiTheme.MonospaceBold, true),
            ("flags", 18, UiTheme.SmallBold, false));

        AddColumns(_panelMembers,
            ("code", 16, UiTheme.MonospaceBold, false),
            ("name", 44, null, false),
            ("price", 16, UiTheme.Monospace, true),
            ("flags", 24, UiTheme.SmallBold, false));
    }

    private void BuildReflexColumns() => AddColumns(_reflex,
        ("trigger", 20, UiTheme.Monospace, false),
        ("condition", 22, UiTheme.Monospace, false),
        ("adds", 16, UiTheme.MonospaceBold, false),
        ("description", 26, UiTheme.Small, false),
        ("mode", 10, UiTheme.Small, false),
        ("flags", 12, UiTheme.SmallBold, false));

    private void BuildNomenclatureColumns() => AddColumns(_nomenclature,
        ("code", 14, UiTheme.MonospaceBold, false),
        ("name", 34, null, false),
        ("detail", 30, UiTheme.Small, false),
        ("usage", 10, UiTheme.Monospace, true),
        ("flags", 12, UiTheme.SmallBold, false));

    private void WireBehaviour()
    {
        _search.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await LoadAnalysesAsync();
        };

        _departmentFilter.SelectedIndexChanged += async (_, _) => await LoadAnalysesAsync();
        _includeInactive.CheckedChanged += async (_, _) => await LoadAnalysesAsync();

        _analyses.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => OnAnalysisSelectedAsync());
        _parameters.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadRangesAsync());
        _panels.SelectionChanged += (_, _) => RenderPanelMembers();

        _analyses.CellFormatting += OnAnalysisFormatting;
        _parameters.CellFormatting += OnParameterFormatting;
        _ranges.CellFormatting += OnRangeFormatting;
        _reflex.CellFormatting += OnReflexFormatting;
        _panels.CellFormatting += OnPanelFormatting;

        // A double-click does what the Edit button beside the grid does, and only when it may.
        _analyses.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && _editAnalysis.Enabled) await EditAnalysisAsync(); };
        _parameters.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && _editParameter.Enabled) await EditParameterAsync(); };
        _ranges.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && _editRange.Enabled) await EditRangeAsync(); };
        _panels.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && _editPanel.Enabled) await EditPanelAsync(); };
        _reflex.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && _editReflex.Enabled) await EditReflexAsync(); };
        _nomenclature.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && _editNomenclature.Enabled) await EditNomenclatureAsync(); };

        _newAnalysis.Click += async (_, _) => await EditAnalysisAsync(create: true);
        _editAnalysis.Click += async (_, _) => await EditAnalysisAsync();
        _newParameter.Click += async (_, _) => await EditParameterAsync(create: true);
        _editParameter.Click += async (_, _) => await EditParameterAsync();
        _newRange.Click += async (_, _) => await EditRangeAsync(create: true);
        _editRange.Click += async (_, _) => await EditRangeAsync();
        _reviewRange.Click += async (_, _) => await ReviewRangeAsync();
        _newTariff.Click += async (_, _) => await AddTariffAsync();
        _editTariff.Click += async (_, _) => await AddTariffAsync(SelectedTariff);
        _tariffs.SelectionChanged += (_, _) => RefreshActions();
        _tariffs.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && _editTariff.Enabled) await AddTariffAsync(SelectedTariff);
        };
        _newPanel.Click += async (_, _) => await EditPanelAsync(create: true);
        _editPanel.Click += async (_, _) => await EditPanelAsync();
        _newReflex.Click += async (_, _) => await EditReflexAsync(create: true);
        _editReflex.Click += async (_, _) => await EditReflexAsync();
        _newNomenclature.Click += async (_, _) => await EditNomenclatureAsync(create: true);
        _editNomenclature.Click += async (_, _) => await EditNomenclatureAsync();

        _nomenclatureKind.SelectedIndexChanged += async (_, _) => await LoadNomenclatureAsync();

        _tabs.SelectedIndexChanged += async (_, _) => await LoadSelectedTabAsync();
    }

    protected override void ApplyTexts()
    {
        if (_tabs.TabPages.Count == 5)
        {
            _tabs.TabPages[0].Text = T("Catalog.Analyses");
            _tabs.TabPages[1].Text = T("Catalog.Tariffs");
            _tabs.TabPages[2].Text = T("Catalog.Panels");
            _tabs.TabPages[3].Text = T("Catalog.ReflexRules");
            _tabs.TabPages[4].Text = T("Catalog.Nomenclature");
        }

        _search.PlaceholderText = T("Order.CatalogSearchPlaceholder");
        _includeInactive.Text = T("Catalog.IncludeInactive");

        _newAnalysis.Text = T("Catalog.NewAnalysis");
        _editAnalysis.Text = T("Catalog.EditAnalysis");
        _newParameter.Text = T("Catalog.NewParameter");
        _editParameter.Text = T("Catalog.EditParameter");
        _newRange.Text = T("Catalog.NewRange");
        _editRange.Text = T("Catalog.EditRange");
        _reviewRange.Text = T("Catalog.RecordReview");
        _newTariff.Text = T("Catalog.NewTariff");
        _editTariff.Text = T("Catalog.EditTariff");
        _newPanel.Text = T("Catalog.NewPanel");
        _editPanel.Text = T("Catalog.EditPanel");
        _newReflex.Text = T("Catalog.NewReflexRule");
        _editReflex.Text = T("Catalog.EditReflexRule");
        _newNomenclature.Text = T("Action.Add");
        _editNomenclature.Text = T("Action.Edit");

        ApplyHeaders();
        RefreshDepartmentFilter();
        RefreshNomenclatureKinds();

        if (_analysisRows.Count > 0) RenderAnalyses();
        if (_panelRows.Count > 0) RenderPanels();

        RefreshActions();
    }

    private void ApplyHeaders()
    {
        void Header(DataGridView grid, string column, string key)
        {
            if (grid.Columns.Contains(column)) grid.Column(column).HeaderText = T(key);
        }

        Header(_analyses, "code", "Field.Code");
        Header(_analyses, "name", "Field.Name");
        Header(_analyses, "department", "Field.Department");
        Header(_analyses, "specimen", "Sample.SpecimenType");
        Header(_analyses, "parameters", "Catalog.Parameters");
        Header(_analyses, "price", "Field.Price");
        Header(_analyses, "turnaround", "Catalog.Turnaround");
        Header(_analyses, "flags", "Field.Status");

        Header(_parameters, "code", "Field.Code");
        Header(_parameters, "name", "Field.Name");
        Header(_parameters, "type", "Catalog.ValueType");
        Header(_parameters, "unit", "Field.Unit");
        Header(_parameters, "range", "Catalog.MeasuringRange");
        Header(_parameters, "critical", "Catalog.CriticalLimits");
        Header(_parameters, "delta", "Catalog.DeltaCheck");
        Header(_parameters, "flags", "Field.Status");

        Header(_ranges, "population", "Catalog.Population");
        Header(_ranges, "interval", "Result.ReferenceRange");
        Header(_ranges, "validity", "Catalog.Validity");
        Header(_ranges, "reviewed", "Catalog.LastReviewed");
        Header(_ranges, "source", "Catalog.RangeSource");
        Header(_ranges, "flags", "Field.Status");

        Header(_tariffs, "payer", "Coverage.Payer");
        Header(_tariffs, "validity", "Catalog.Validity");
        Header(_tariffs, "letterkey", "Billing.LetterKey");
        Header(_tariffs, "coefficient", "Billing.Coefficient");
        Header(_tariffs, "base", "Billing.ReferenceTariff");
        Header(_tariffs, "price", "Field.Price");
        Header(_tariffs, "excess", "Billing.ExcessOverTariff");
        Header(_tariffs, "vat", "Billing.Vat");

        Header(_panels, "code", "Field.Code");
        Header(_panels, "name", "Field.Name");
        Header(_panels, "members", "Catalog.Analyses");
        Header(_panels, "components", "Catalog.ComponentTotal");
        Header(_panels, "package", "Catalog.PackagePrice");
        Header(_panels, "flags", "Field.Status");

        Header(_panelMembers, "code", "Field.Code");
        Header(_panelMembers, "name", "Field.Name");
        Header(_panelMembers, "price", "Field.Price");
        Header(_panelMembers, "flags", "Field.Status");

        Header(_reflex, "trigger", "Catalog.ReflexTrigger");
        Header(_reflex, "condition", "Catalog.ReflexCondition");
        Header(_reflex, "adds", "Catalog.ReflexAdds");
        Header(_reflex, "description", "Field.Description");
        Header(_reflex, "mode", "Catalog.ReflexMode");
        Header(_reflex, "flags", "Field.Status");

        Header(_nomenclature, "code", "Field.Code");
        Header(_nomenclature, "name", "Field.Name");
        Header(_nomenclature, "detail", "Field.Description");
        Header(_nomenclature, "usage", "Catalog.UsedBy");
        Header(_nomenclature, "flags", "Field.Status");
    }

    private void RefreshDepartmentFilter()
    {
        var selected = (_departmentFilter.SelectedItem as GuidChoice)?.Value;

        _departmentFilter.Items.Clear();
        _departmentFilter.Items.Add(new GuidChoice(null, T("Catalog.AllDepartments")));

        foreach (var department in _lookups.Departments)
            _departmentFilter.Items.Add(new GuidChoice(department.Id,
                                                        department.Name.For(Localizer.CurrentLanguage)));

        _departmentFilter.SelectedIndex = 0;

        if (selected is not { } id) return;

        for (var i = 0; i < _departmentFilter.Items.Count; i++)
        {
            if (_departmentFilter.Items[i] is GuidChoice { Value: { } candidate } && candidate == id)
            {
                _departmentFilter.SelectedIndex = i;
                return;
            }
        }
    }

    private void RefreshNomenclatureKinds()
    {
        var selected = _nomenclatureKind.SelectedIndex;

        _nomenclatureKind.Items.Clear();

        foreach (NomenclatureKind kind in Enum.GetValues<NomenclatureKind>())
            _nomenclatureKind.Items.Add(new EnumChoice((int)kind, T($"NomenclatureKind.{kind}")));

        _nomenclatureKind.SelectedIndex = selected >= 0 ? selected : 0;
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            _lookups = await CatalogLookups.LoadAsync(_catalog, ct);

            RefreshDepartmentFilter();

            await LoadAnalysesAsync(ct);
            await LoadSelectedTabAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the catalogue failed.");
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T("Message.UnexpectedError");
        }
    }

    private async Task LoadSelectedTabAsync(CancellationToken ct = default)
    {
        switch (_tabs.SelectedIndex)
        {
            case 1: await LoadTariffsAsync(ct); break;
            case 2: await LoadPanelsAsync(ct); break;
            case 3: await LoadReflexAsync(ct); break;
            case 4: await LoadNomenclatureAsync(ct); break;
        }
    }

    private async Task LoadAnalysesAsync(CancellationToken ct = default)
    {
        var department = (_departmentFilter.SelectedItem as GuidChoice)?.Value;

        var result = await _catalog.GetAnalysesAsync(
            string.IsNullOrWhiteSpace(_search.Text) ? null : _search.Text.Trim(),
            department, _includeInactive.Checked, ct);

        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        _analysisRows = result.Value;
        RenderAnalyses();

        await OnAnalysisSelectedAsync(ct);
    }

    private void RenderAnalyses()
    {
        var language = Localizer.CurrentLanguage;
        var selected = SelectedAnalysis?.Id;

        _analyses.SuspendLayout();
        _analyses.Rows.Clear();

        foreach (var row in _analysisRows)
        {
            var flags = new List<string>(4);

            if (!row.IsActive) flags.Add(T("Catalog.Inactive"));
            if (row.ActiveParameterCount == 0) flags.Add(T("Catalog.NoParameter"));
            if (row.TariffCount == 0) flags.Add(T("Catalog.NoTariff"));
            if (row.IsAccredited) flags.Add("ISO");
            if (row.IsSubcontracted) flags.Add(T("Catalog.Subcontracted"));
            if (row.ConsentRequirement != ConsentRequirement.None) flags.Add(T("Consent.Required"));
            // Age bands saved before their unit was kept, which may be read in months although
            // they were typed in days: listed here so the biologist can find and re-save them.
            if (row.UnconfirmedAgeBandCount > 0)
                flags.Add($"{T("Catalog.AgeUnitToConfirm")} ({row.UnconfirmedAgeBandCount})");

            var index = _analyses.Rows.Add(
                row.Code,
                row.Name.For(language),
                row.DepartmentCode,
                row.SpecimenTypeName.For(language),
                row.ActiveParameterCount == row.ParameterCount
                    ? row.ParameterCount.ToString()
                    : $"{row.ActiveParameterCount}/{row.ParameterCount}",
                row.CurrentPrice is { } price ? Money.Of(price).Format(language) : "—",
                $"{row.RoutineTurnaroundHours} h",
                string.Join(" · ", flags));

            _analyses.Rows[index].Tag = row;
        }

        _analyses.ResumeLayout(performLayout: true);

        RestoreSelection(_analyses, selected, r => (r as AnalysisRow)?.Id);

        _analysisHeading.Text =
            $"{T("Catalog.Analyses")} — {_analysisRows.Count}"
            + (_analysisRows.Count(a => !a.IsOrderable) is var blocked and > 0
                ? $" · {blocked} {T("Catalog.NotOrderable").ToLowerInvariant()}"
                : string.Empty)
            + (_analysisRows.Count(a => a.UnconfirmedAgeBandCount > 0) is var unconfirmed and > 0
                ? $" · {unconfirmed} {T("Catalog.AgeUnitToConfirm").ToLowerInvariant()}"
                : string.Empty);
    }

    private async Task OnAnalysisSelectedAsync(CancellationToken ct = default)
    {
        if (SelectedAnalysis is not { } analysis)
        {
            _parameterRows = [];
            _rangeRows = [];
            _parameters.Rows.Clear();
            _ranges.Rows.Clear();
            _tariffs.Rows.Clear();
            RefreshActions();
            return;
        }

        var result = await _catalog.GetParametersAsync(analysis.Id, ct);

        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        _parameterRows = result.Value;
        RenderParameters();

        await LoadRangesAsync(ct);

        if (_tabs.SelectedIndex == 1) await LoadTariffsAsync(ct);
    }

    private void RenderParameters()
    {
        var language = Localizer.CurrentLanguage;
        var selected = SelectedParameter?.Id;

        _parameters.SuspendLayout();
        _parameters.Rows.Clear();

        foreach (var row in _parameterRows)
        {
            var flags = new List<string>(4);

            if (!row.IsActive) flags.Add(T("Catalog.Inactive"));
            if (row.IsCalculated) flags.Add("ƒ");
            if (row.IsInternalOnly) flags.Add(T("Catalog.InternalOnly"));
            if (row.IsUnusable) flags.Add(T("Catalog.Unusable"));
            if (row.UnconfirmedAgeBandCount > 0) flags.Add(T("Catalog.AgeUnitToConfirm"));
            if (row.HasResults) flags.Add($"{row.ResultCount}");

            var index = _parameters.Rows.Add(
                new string(' ', row.IndentLevel * 2) + row.Code,
                row.Name.For(language),
                T($"ResultValueType.{row.ValueType}"),
                row.UnitSymbol ?? string.Empty,
                Interval(row.MeasuringRangeLow, row.MeasuringRangeHigh),
                Interval(row.CriticalLow, row.CriticalHigh),
                row.DeltaCheckPercent is { } delta ? $"{delta:0.#} %" : string.Empty,
                string.Join(" · ", flags));

            _parameters.Rows[index].Tag = row;
        }

        _parameters.ResumeLayout(performLayout: true);

        RestoreSelection(_parameters, selected, r => (r as ParameterRow)?.Id);

        _parameterHeading.Text = SelectedAnalysis is { } analysis
            ? $"{T("Catalog.Parameters")} — {analysis.Code}"
            : T("Catalog.Parameters");
    }

    private async Task LoadRangesAsync(CancellationToken ct = default)
    {
        if (SelectedParameter is not { } parameter)
        {
            _rangeRows = [];
            _ranges.Rows.Clear();
            RefreshActions();
            return;
        }

        var result = await _catalog.GetRangesAsync(parameter.Id, ct);

        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        _rangeRows = result.Value;
        RenderRanges();
    }

    private void RenderRanges()
    {
        var language = Localizer.CurrentLanguage;
        var today = _clock.Today;
        var decimals = SelectedParameter?.Decimals ?? 2;

        _ranges.SuspendLayout();
        _ranges.Rows.Clear();

        foreach (var row in _rangeRows)
        {
            var flags = new List<string>(3);

            if (!row.IsInForce(today)) flags.Add(T("Catalog.Superseded"));
            if (row.NeedsReview(today)) flags.Add(T("Catalog.ReviewDue"));
            if (row.HasUnconfirmedAgeUnit) flags.Add(T("Catalog.AgeUnitToConfirm"));
            if (row.IsDecisionThreshold) flags.Add(T("Catalog.DecisionThreshold"));

            var index = _ranges.Rows.Add(
                DescribePopulation(row, language),
                FormatInterval(row, decimals),
                row.ValidUntil is { } until
                    ? $"{row.ValidFrom:dd/MM/yy} → {until:dd/MM/yy}"
                    : $"{row.ValidFrom:dd/MM/yy} →",
                row.LastReviewedOn?.ToString("dd/MM/yy") ?? "—",
                row.Source ?? string.Empty,
                string.Join(" · ", flags));

            _ranges.Rows[index].Tag = row;
        }

        _ranges.ResumeLayout(performLayout: true);

        if (_ranges.Rows.Count > 0) _ranges.SelectFirstRow();

        _rangeHeading.Text = SelectedParameter is { } parameter
            ? $"{T("Result.ReferenceRange")} — {parameter.Code}"
            : T("Result.ReferenceRange");

        RefreshActions();
    }

    private async Task LoadTariffsAsync(CancellationToken ct = default)
    {
        if (SelectedAnalysis is not { } analysis)
        {
            _tariffs.Rows.Clear();
            _tariffHeading.Text = T("Catalog.Tariffs");
            RefreshActions();
            return;
        }

        var result = await _catalog.GetTariffsAsync(analysis.Id, ct);

        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        var language = Localizer.CurrentLanguage;
        var today = _clock.Today;

        _tariffs.SuspendLayout();
        _tariffs.Rows.Clear();

        foreach (var row in result.Value)
        {
            var index = _tariffs.Rows.Add(
                row.PayerName ?? T("Catalog.DefaultPriceList"),
                row.ValidUntil is { } until
                    ? $"{row.ValidFrom:dd/MM/yy} → {until:dd/MM/yy}"
                    : $"{row.ValidFrom:dd/MM/yy} →",
                row.LetterKey,
                row.Coefficient.ToString("0.##"),
                Money.Of(row.NationalReferenceBase).Format(language),
                Money.Of(row.Price).Format(language),
                row.ExcessOverReference > 0m
                    ? Money.Of(row.ExcessOverReference).Format(language)
                    : string.Empty,
                row.IsVatExempt ? T("Billing.VatExempt") : $"{row.VatRate:P0}");

            var grid = _tariffs.Rows[index];
            grid.Tag = row;

            // In force is the one that matters; the history is grey behind it.
            if (!row.IsInForce(today))
                grid.DefaultCellStyle.ForeColor = UiTheme.TextMuted;
        }

        _tariffs.ResumeLayout(performLayout: true);

        _tariffHeading.Text = $"{T("Catalog.Tariffs")} — {analysis.Code} "
                              + analysis.Name.For(language);

        RefreshActions();
    }

    private async Task LoadPanelsAsync(CancellationToken ct = default)
    {
        var result = await _catalog.GetPanelsAsync(_includeInactive.Checked, ct);

        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        _panelRows = result.Value;
        RenderPanels();
    }

    private void RenderPanels()
    {
        var language = Localizer.CurrentLanguage;
        var selected = SelectedPanel?.Id;

        _panels.SuspendLayout();
        _panels.Rows.Clear();

        foreach (var row in _panelRows)
        {
            var flags = new List<string>(4);

            if (!row.IsActive) flags.Add(T("Catalog.Inactive"));
            if (row.InactiveMemberCount > 0)
                flags.Add(T("Catalog.PanelHasInactiveMember", row.InactiveMemberCount));
            if (row.PackageAboveComponents) flags.Add(T("Catalog.PackageAboveComponents"));
            if (!row.ItemizeOnInvoice) flags.Add(T("Catalog.NotItemized"));

            var index = _panels.Rows.Add(
                row.Code,
                row.Name.For(language),
                row.OptionalCount > 0
                    ? $"{row.MemberCount - row.OptionalCount}+{row.OptionalCount}"
                    : row.MemberCount.ToString(),
                Money.Of(row.ComponentTotal).Format(language),
                row.PackagePrice is { } package ? Money.Of(package).Format(language) : "—",
                string.Join(" · ", flags));

            _panels.Rows[index].Tag = row;
        }

        _panels.ResumeLayout(performLayout: true);

        RestoreSelection(_panels, selected, r => (r as PanelRow)?.Id);
        RenderPanelMembers();
    }

    private void RenderPanelMembers()
    {
        var language = Localizer.CurrentLanguage;

        _panelMembers.SuspendLayout();
        _panelMembers.Rows.Clear();

        if (SelectedPanel is { } panel)
        {
            foreach (var member in panel.Members)
            {
                var flags = new List<string>(2);

                if (member.IsOptional) flags.Add(T("Catalog.Optional"));
                if (!member.IsAnalysisActive) flags.Add(T("Catalog.Inactive"));

                var index = _panelMembers.Rows.Add(
                    member.Code,
                    member.Name.For(language),
                    Money.Of(member.Price).Format(language),
                    string.Join(" · ", flags));

                _panelMembers.Rows[index].Tag = member;
            }
        }

        _panelMembers.ResumeLayout(performLayout: true);
        RefreshActions();
    }

    private async Task LoadReflexAsync(CancellationToken ct = default)
    {
        var result = await _catalog.GetReflexRulesAsync(ct);

        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        var language = Localizer.CurrentLanguage;

        _reflex.SuspendLayout();
        _reflex.Rows.Clear();

        foreach (var row in result.Value)
        {
            var flags = new List<string>(3);

            if (!row.IsActive) flags.Add(T("Catalog.Inactive"));
            if (row.CanNeverFire) flags.Add(T("Catalog.RuleCanNeverFire"));
            if (!row.IsBillable) flags.Add(T("Catalog.NotBilled"));

            var index = _reflex.Rows.Add(
                $"{row.TriggerAnalysisCode}/{row.TriggerParameterCode}",
                DescribeCondition(row),
                row.AnalysisToAddCode,
                row.Description.For(language),
                row.AddAutomatically ? T("Catalog.Automatic") : T("Catalog.NeedsConfirmation"),
                string.Join(" · ", flags));

            _reflex.Rows[index].Tag = row;
        }

        _reflex.ResumeLayout(performLayout: true);

        if (_reflex.Rows.Count > 0 && _reflex.CurrentRow is null) _reflex.SelectFirstRow();

        RefreshActions();
    }

    private async Task LoadNomenclatureAsync(CancellationToken ct = default)
    {
        var kind = SelectedNomenclatureKind;

        var result = await _catalog.GetNomenclatureAsync(kind, ct);

        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        var language = Localizer.CurrentLanguage;

        _nomenclature.SuspendLayout();
        _nomenclature.Rows.Clear();

        foreach (var row in result.Value)
        {
            var flags = new List<string>(3);
            if (!row.IsActive) flags.Add(T("Catalog.Inactive"));

            var detail = kind switch
            {
                NomenclatureKind.Department =>
                    (row.IsInAccreditationScope ? "ISO 15189" : string.Empty)
                    + (row.SupervisorName is { Length: > 0 } supervisor ? $" · {supervisor}" : string.Empty),

                NomenclatureKind.SpecimenType =>
                    (row.FastingHours > 0 ? $"{T("Order.Fasted")} {row.FastingHours} h" : string.Empty)
                    + (row.CollectionInstructions is { } instructions
                        ? $" · {instructions.For(language)}"
                        : string.Empty),

                NomenclatureKind.ContainerType =>
                    $"{T("Sample.DrawOrder")} {row.DrawOrder}"
                    + (row.Additive is { } additive ? $" · {additive.For(language)}" : string.Empty)
                    + (row.NominalVolumeMl is { } volume ? $" · {volume:0.#} mL" : string.Empty)
                    + (row.RequiresProtectionFromLight ? $" · {T("Collection.ProtectFromLight")}" : string.Empty)
                    + (row.RequiresIceTransport ? $" · {T("Collection.TransportOnIce")}" : string.Empty),

                _ => row.UcumCode is { Length: > 0 } ucum ? $"UCUM {ucum}" : string.Empty
            };

            if (kind == NomenclatureKind.ContainerType && !IsRenderableColour(row.CapColourHex))
                flags.Add(T("Catalog.InvalidColour"));

            var index = _nomenclature.Rows.Add(
                row.Code,
                kind == NomenclatureKind.MeasurementUnit
                    ? row.DisplaySymbol
                    : row.Name.For(language),
                detail.Trim(' ', '·'),
                row.UsageCount == 0 ? string.Empty : row.UsageCount.ToString(),
                string.Join(" · ", flags));

            _nomenclature.Rows[index].Tag = row;
        }

        _nomenclature.ResumeLayout(performLayout: true);

        if (_nomenclature.Rows.Count > 0 && _nomenclature.CurrentRow is null)
            _nomenclature.SelectFirstRow();

        RefreshActions();
    }

    // ---- Formatting ----------------------------------------------------------------------------

    private void OnAnalysisFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _analyses.Rows.Count) return;
        if (_analyses.Rows[e.RowIndex].Tag is not AnalysisRow row) return;

        if (!row.IsActive) e.CellStyle!.ForeColor = UiTheme.TextMuted;
        else if (!row.IsOrderable)
        {
            // Active but not orderable: it appears in the picker and fails at the counter.
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
    }

    private void OnParameterFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _parameters.Rows.Count) return;
        if (_parameters.Rows[e.RowIndex].Tag is not ParameterRow row) return;

        if (!row.IsActive) e.CellStyle!.ForeColor = UiTheme.TextMuted;
        else if (row.IsUnusable)
        {
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
    }

    private void OnRangeFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _ranges.Rows.Count) return;
        if (_ranges.Rows[e.RowIndex].Tag is not ReferenceRangeRow row) return;

        if (!row.IsInForce(_clock.Today)) e.CellStyle!.ForeColor = UiTheme.TextMuted;
        else if (row.HasUnconfirmedAgeUnit)
        {
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
        else if (row.NeedsReview(_clock.Today)) e.CellStyle!.ForeColor = UiTheme.Abnormal;
    }

    private void OnReflexFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _reflex.Rows.Count) return;
        if (_reflex.Rows[e.RowIndex].Tag is not ReflexRuleRow row) return;

        if (!row.IsActive) e.CellStyle!.ForeColor = UiTheme.TextMuted;
        else if (row.CanNeverFire)
        {
            // Active and impossible: the worst of the three states, because somebody is relying on
            // a confirmation that will never be ordered.
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
    }

    private void OnPanelFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _panels.Rows.Count) return;
        if (_panels.Rows[e.RowIndex].Tag is not PanelRow row) return;

        if (!row.IsActive) e.CellStyle!.ForeColor = UiTheme.TextMuted;
        else if (row.InactiveMemberCount > 0)
        {
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
    }

    // ---- Selection -----------------------------------------------------------------------------

    private AnalysisRow? SelectedAnalysis => _analyses.CurrentRow?.Tag as AnalysisRow;
    private ParameterRow? SelectedParameter => _parameters.CurrentRow?.Tag as ParameterRow;
    private ReferenceRangeRow? SelectedRange => _ranges.CurrentRow?.Tag as ReferenceRangeRow;

    private TariffRow? SelectedTariff => _tariffs.CurrentRow?.Tag as TariffRow;
    private PanelRow? SelectedPanel => _panels.CurrentRow?.Tag as PanelRow;
    private ReflexRuleRow? SelectedReflex => _reflex.CurrentRow?.Tag as ReflexRuleRow;
    private NomenclatureRow? SelectedNomenclature => _nomenclature.CurrentRow?.Tag as NomenclatureRow;

    private NomenclatureKind SelectedNomenclatureKind =>
        (NomenclatureKind)((_nomenclatureKind.SelectedItem as EnumChoice)?.Value ?? 0);

    private static void RestoreSelection(DataGridView grid, Guid? id,
                                          Func<object?, Guid?> identify)
    {
        if (id is null)
        {
            if (grid.Rows.Count > 0) grid.SelectFirstRow();
            return;
        }

        foreach (DataGridViewRow row in grid.Rows)
        {
            if (identify(row.Tag) == id) { row.Selected = true; return; }
        }

        if (grid.Rows.Count > 0) grid.SelectFirstRow();
    }

    private void RefreshActions()
    {
        var manage = _currentUser.Has(Permissions.Catalog.Manage);
        var ranges = _currentUser.Has(Permissions.Catalog.ManageReferenceRanges);
        var tariffs = _currentUser.Has(Permissions.Billing.ManageTariffs);

        _newAnalysis.Enabled = manage;
        _editAnalysis.Enabled = manage && SelectedAnalysis is not null;

        _newParameter.Enabled = manage && SelectedAnalysis is not null;
        _editParameter.Enabled = manage && SelectedParameter is not null;

        _newRange.Enabled = ranges && SelectedParameter is not null;
        _editRange.Enabled = ranges && SelectedRange is not null;
        _reviewRange.Enabled = ranges && SelectedRange is not null;

        _newTariff.Enabled = tariffs && SelectedAnalysis is not null;
        _editTariff.Enabled = tariffs && SelectedAnalysis is not null && SelectedTariff is not null;

        _newPanel.Enabled = manage;
        _editPanel.Enabled = manage && SelectedPanel is not null;

        _newReflex.Enabled = manage;
        _editReflex.Enabled = manage && SelectedReflex is not null;

        _newNomenclature.Enabled = manage;
        _editNomenclature.Enabled = manage && SelectedNomenclature is not null;
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private async Task EditAnalysisAsync(bool create = false)
    {
        var existing = create ? null : SelectedAnalysis;

        if (!create && existing is null) return;

        var today = _clock.Today;
        var canPrice = _currentUser.Has(Permissions.Billing.ManageTariffs);

        // The laboratory price-list tariff in force is edited here with the analysis; the letter-key
        // value is national, so a first tariff starts from the latest one known on any analysis.
        TariffRow? labTariff = null;
        decimal? knownLetterKeyValue = null;

        if (existing is not null)
        {
            var tariffs = await _catalog.GetTariffsAsync(existing.Id);
            if (tariffs.IsFailure) { _dialogs.ShowFailure(tariffs); return; }

            labTariff = tariffs.Value.Where(t => t.PayerId is null && t.IsInForce(today))
                                     .MaxBy(t => t.ValidFrom);
        }

        if (labTariff is not { LetterKeyValue: > 0m })
        {
            var known = await _catalog.GetLatestLetterKeyValueAsync(labTariff?.LetterKey ?? "B");
            if (known.IsSuccess) knownLetterKeyValue = known.Value;
        }

        using var dialog = new AnalysisEditorDialog(Localizer, _dialogs, _lookups, existing,
                                                    labTariff, knownLetterKeyValue, canPrice);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Request is null) return;

        var result = await _catalog.SaveAnalysisAsync(dialog.Request);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, dialog.Text);
            return;
        }

        Saved(dialog.Request.Code);

        if (dialog.PriceRequest is { } price)
        {
            var request = price with { AnalysisTestId = existing?.Id ?? result.Value, ValidFrom = today };

            // A price set earlier today is corrected in place; otherwise the new price starts today
            // and the previous one closes yesterday, so orders already billed keep their price.
            Result priced = labTariff is not null && labTariff.ValidFrom == today
                ? await _catalog.UpdateTariffAsync(labTariff.Id, request)
                : await _catalog.AddTariffAsync(request);

            if (priced.IsFailure)
            {
                _dialogs.ShowFailure(priced, dialog.Text);
            }
            else
            {
                _status.ForeColor = UiTheme.Normal;
                _status.Text = T("Catalog.AnalysisPriceSaved", dialog.Request.Code,
                                 today.ToString("dd/MM/yyyy"));
            }
        }

        await LoadAnalysesAsync();
    }

    private async Task EditParameterAsync(bool create = false)
    {
        if (SelectedAnalysis is not { } analysis) return;

        var existing = create ? null : SelectedParameter;

        if (!create && existing is null) return;

        // The sibling codes are handed to the editor so a calculation formula can be checked
        // against them as it is typed, rather than refused on save.
        using var dialog = new ParameterEditorDialog(
            Localizer, _dialogs, _lookups, analysis, existing,
            [.. _parameterRows.Where(p => existing is null || p.Id != existing.Id)]);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Request is null) return;

        var result = await _catalog.SaveParameterAsync(dialog.Request);

        if (result.IsFailure) { _dialogs.ShowFailure(result, dialog.Text); return; }

        Saved(dialog.Request.Code);

        await OnAnalysisSelectedAsync();
    }

    private async Task EditRangeAsync(bool create = false)
    {
        if (SelectedParameter is not { } parameter) return;

        var existing = create ? null : SelectedRange;

        if (!create && existing is null) return;

        using var dialog = new ReferenceRangeEditorDialog(
            Localizer, _dialogs, _lookups, parameter, existing, _clock.Today);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Request is null) return;

        var result = await _catalog.SaveRangeAsync(dialog.Request);

        if (result.IsFailure) { _dialogs.ShowFailure(result, dialog.Text); return; }

        // A supersession returns a different id from the one edited, which is the operator's cue
        // that the change opened a revision rather than rewriting the interval.
        var superseded = existing is not null && result.Value != existing.Id;

        _status.ForeColor = superseded ? UiTheme.Abnormal : UiTheme.Normal;
        _status.Text = superseded ? T("Catalog.RangeSuperseded") : T("Message.Saved");

        await LoadRangesAsync();
        await OnAnalysisSelectedAsync();
    }

    private async Task ReviewRangeAsync()
    {
        if (SelectedParameter is not { } parameter || SelectedRange is not { } range) return;

        using var dialog = new RangeReviewDialog(Localizer, range, _clock.Today);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        var result = await _catalog.RecordRangeReviewAsync(parameter.Id, range.Id, dialog.Source);

        if (result.IsFailure) { _dialogs.ShowFailure(result, T("Catalog.RecordReview")); return; }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Catalog.ReviewRecorded", _clock.Today.ToString("dd/MM/yyyy"));

        await LoadRangesAsync();
    }

    /// <summary>Enters a new tariff, or corrects <paramref name="editing"/> in place.</summary>
    private async Task AddTariffAsync(TariffRow? editing = null)
    {
        if (SelectedAnalysis is not { } analysis) return;

        var current = await _catalog.GetTariffsAsync(analysis.Id);
        if (current.IsFailure) { _dialogs.ShowFailure(current); return; }

        var payers = await _catalog.GetPayerOptionsAsync();
        if (payers.IsFailure) { _dialogs.ShowFailure(payers); return; }

        var national = await _catalog.GetLatestLetterKeyValueAsync("B");

        using var dialog = new TariffEditorDialog(Localizer, _dialogs, _lookups, analysis,
                                                   current.Value, _clock.Today, payers.Value,
                                                   editing, national.IsSuccess ? national.Value : null);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Request is null) return;

        if (dialog.EditingId is { } tariffId)
        {
            var corrected = await _catalog.UpdateTariffAsync(tariffId, dialog.Request);
            if (corrected.IsFailure) { _dialogs.ShowFailure(corrected, dialog.Text); return; }

            _status.ForeColor = UiTheme.Normal;
            _status.Text = T("Catalog.TariffCorrected");

            await LoadTariffsAsync();
            await LoadAnalysesAsync();
            return;
        }

        var result = await _catalog.AddTariffAsync(dialog.Request);

        if (result.IsFailure) { _dialogs.ShowFailure(result, dialog.Text); return; }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Catalog.TariffAdded", dialog.Request.ValidFrom.ToString("dd/MM/yyyy"));

        await LoadTariffsAsync();
        await LoadAnalysesAsync();
    }

    private async Task EditPanelAsync(bool create = false)
    {
        var existing = create ? null : SelectedPanel;

        if (!create && existing is null) return;

        // Only orderable analyses are offered as members: a panel built on an analysis with no
        // tariff or no parameter fails at registration, on a component the operator did not pick.
        var candidates = await _catalog.GetAnalysesAsync(includeInactive: false);

        if (candidates.IsFailure) { _dialogs.ShowFailure(candidates); return; }

        using var dialog = new PanelEditorDialog(Localizer, _dialogs, candidates.Value, existing);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Request is null) return;

        var result = await _catalog.SavePanelAsync(dialog.Request);

        if (result.IsFailure) { _dialogs.ShowFailure(result, dialog.Text); return; }

        Saved(dialog.Request.Code);

        await LoadPanelsAsync();
    }

    private async Task EditReflexAsync(bool create = false)
    {
        var existing = create ? null : SelectedReflex;

        if (!create && existing is null) return;

        var analyses = await _catalog.GetAnalysesAsync(includeInactive: false);

        if (analyses.IsFailure) { _dialogs.ShowFailure(analyses); return; }

        using var dialog = new ReflexRuleEditorDialog(Localizer, _dialogs, _catalog,
                                                       analyses.Value, existing);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Request is null) return;

        var result = await _catalog.SaveReflexRuleAsync(dialog.Request);

        if (result.IsFailure) { _dialogs.ShowFailure(result, dialog.Text); return; }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Message.Saved");

        await LoadReflexAsync();
    }

    private async Task EditNomenclatureAsync(bool create = false)
    {
        var kind = SelectedNomenclatureKind;
        var existing = create ? null : SelectedNomenclature;

        if (!create && existing is null) return;

        using var dialog = new NomenclatureEditorDialog(Localizer, _dialogs, kind, existing);

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Request is null) return;

        var result = await _catalog.SaveNomenclatureAsync(kind, dialog.Request);

        if (result.IsFailure) { _dialogs.ShowFailure(result, dialog.Text); return; }

        Saved(dialog.Request.Code);

        // The lookups the other editors offer come from these lists, so they are reloaded rather
        // than left stale — a new specimen type invisible in the analysis editor is the kind of
        // thing that gets reported as "it did not save".
        _lookups = await CatalogLookups.LoadAsync(_catalog);

        RefreshDepartmentFilter();
        await LoadNomenclatureAsync();
    }

    private void Saved(string code)
    {
        _status.ForeColor = UiTheme.Normal;
        _status.Text = $"{code} — {T("Message.Saved")}";
    }

    // ---- Presentation helpers ------------------------------------------------------------------

    private static string Interval(decimal? low, decimal? high) => (low, high) switch
    {
        ({ } l, { } h) => $"{l:0.###} – {h:0.###}",
        (null, { } h) => $"≤ {h:0.###}",
        ({ } l, null) => $"≥ {l:0.###}",
        _ => string.Empty
    };

    private string FormatInterval(ReferenceRangeRow row, int decimals)
    {
        if (row.DisplayText is { } text && text.For(Localizer.CurrentLanguage).Length > 0)
            return text.For(Localizer.CurrentLanguage);

        if (row.ExpectedCodedValue is { Length: > 0 } coded) return coded;

        var format = "N" + decimals;

        return (row.LowerLimit, row.UpperLimit) switch
        {
            ({ } low, { } high) => $"{low.ToString(format)} - {high.ToString(format)}",
            (null, { } high) => $"< {high.ToString(format)}",
            ({ } low, null) => $"> {low.ToString(format)}",
            _ => "—"
        };
    }

    /// <summary>
    /// Who the interval applies to, in the order that decides which of two overlapping intervals
    /// wins — trimester, pregnancy, instrument, sex, age. The same order as the resolver's.
    /// </summary>
    private string DescribePopulation(ReferenceRangeRow row, AppLanguage language)
    {
        var parts = new List<string>(5);

        if (row.PregnancyTrimester is { } trimester)
            parts.Add(T("Catalog.Trimester", trimester));
        else if (row.AppliesToPregnancy is { } pregnant)
            parts.Add(pregnant ? T("Patient.Pregnant") : T("Catalog.NotPregnant"));

        if (row.InstrumentName is { Length: > 0 } instrument) parts.Add(instrument);

        if (row.AppliesToGender != Domain.Patients.Gender.Unknown)
            parts.Add(T($"Gender.{row.AppliesToGender}"));

        if (row.AgeFromDays is not null || row.AgeToDays is not null)
            parts.Add(DescribeAgeBand(row.AgeFromDays, row.AgeToDays));

        return parts.Count == 0 ? T("Catalog.AllPatients") : string.Join(" · ", parts);
    }

    /// <summary>
    /// An age band in the unit a human would use for it. Neonatal intervals are written in days and
    /// adult ones in years, and printing "0 – 10950 jours" for an adult band is how a reviewer
    /// stops reading the column.
    /// </summary>
    private string DescribeAgeBand(int? fromDays, int? toDays)
    {
        var from = fromDays is { } f ? Age(f) : null;
        var to = toDays is { } t ? Age(t) : null;

        return (from, to) switch
        {
            ({ } a, { } b) => $"{a} – {b}",
            (null, { } b) => $"< {b}",
            ({ } a, null) => $"≥ {a}",
            _ => string.Empty
        };

        string Age(int days) => days switch
        {
            < 31 => $"{days} {T("Catalog.Days")}",
            < 731 => $"{days / 30} {T("Catalog.Months")}",
            _ => $"{days / 365} {T("Catalog.Years")}"
        };
    }

    private static string DescribeCondition(ReflexRuleRow row) => row.Condition switch
    {
        ReflexCondition.GreaterThan => $"> {row.Threshold:0.###}",
        ReflexCondition.LessThan => $"< {row.Threshold:0.###}",
        ReflexCondition.Between => $"{row.Threshold:0.###} – {row.ThresholdUpper:0.###}",
        ReflexCondition.Outside => $"∉ [{row.Threshold:0.###} ; {row.ThresholdUpper:0.###}]",
        ReflexCondition.EqualsCoded => $"= {row.TriggerCodedValue}",
        _ => string.Empty
    };

    private static bool IsRenderableColour(string? value) =>
        value is { Length: 7 } colour && colour[0] == '#' && colour[1..].All(Uri.IsHexDigit);
}

/// <summary>A nullable-Guid choice for a combo box — "all departments" needs the null.</summary>
internal sealed record GuidChoice(Guid? Value, string Display)
{
    public override string ToString() => Display;
}

/// <summary>
/// The lists every catalogue editor picks from, loaded once per activation.
/// <para>
/// Loaded together rather than per dialog: opening the analysis editor would otherwise make four
/// round trips before the operator sees a field, and the four lists are small enough that keeping
/// them is cheaper than fetching them again.
/// </para>
/// </summary>
internal sealed record CatalogLookups(
    IReadOnlyList<NomenclatureRow> Departments,
    IReadOnlyList<NomenclatureRow> SpecimenTypes,
    IReadOnlyList<NomenclatureRow> ContainerTypes,
    IReadOnlyList<NomenclatureRow> Units,
    IReadOnlyList<PartnerSiteOption> PartnerSites,
    IReadOnlyList<InstrumentOption> Instruments)
{
    internal static CatalogLookups Empty { get; } = new([], [], [], [], [], []);

    internal static async Task<CatalogLookups> LoadAsync(CatalogMaintenanceService catalog,
                                                          CancellationToken ct = default)
    {
        var departments = await catalog.GetNomenclatureAsync(NomenclatureKind.Department, ct);
        var specimens = await catalog.GetNomenclatureAsync(NomenclatureKind.SpecimenType, ct);
        var containers = await catalog.GetNomenclatureAsync(NomenclatureKind.ContainerType, ct);
        var units = await catalog.GetNomenclatureAsync(NomenclatureKind.MeasurementUnit, ct);
        var partners = await catalog.GetPartnerSitesAsync(ct);
        var instruments = await catalog.GetInstrumentOptionsAsync(ct);

        return new CatalogLookups(
            departments.IsSuccess ? departments.Value : [],
            specimens.IsSuccess ? specimens.Value : [],
            containers.IsSuccess ? containers.Value : [],
            units.IsSuccess ? units.Value : [],
            partners.IsSuccess ? partners.Value : [],
            instruments.IsSuccess ? instruments.Value : []);
    }
}
