using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Patients;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Identity;
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
/// <para>
/// Laid out as the direction A list: title and count with the actions on the right, the search
/// with its scope chips and payer below, then the list in a card and the selected patient in an
/// aside card with their facts, their actions and their orders.
/// </para>
/// </summary>
public sealed class PatientSearchView : LocalizedView
{
    private const int CardGap = 16;

    private readonly IPatientQueries _queries;
    private readonly IDataAccessAuditor _accessAuditor;
    private readonly ICurrentUser _currentUser;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<PatientSearchView>? _logger;

    private readonly Composition.PatientEditorFactory _editor;
    private readonly Application.Patients.PatientRegistrationService _service;
    private readonly INavigationService _navigation;

    // Title row.
    private readonly Label _title = new();
    private readonly Label _resultCount = new();
    private readonly Label _status = new();
    private readonly Button _editPatient = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _more = UiTheme.SecondaryButton(string.Empty);
    private readonly ContextMenuStrip _moreMenu = new();
    private readonly ToolStripMenuItem _merge = new();
    private readonly ToolStripMenuItem _anonymize = new();
    private readonly Button _newPatient = UiTheme.PrimaryButton(string.Empty);

    // Filters.
    private readonly TextBox _search = new();
    private readonly PillButton _scopeAll = new(string.Empty);
    private readonly PillButton _scopeIncomplete = new(string.Empty);
    private readonly PillButton _scopeToday = new(string.Empty);
    private readonly ComboBox _payer = new();

    // List card.
    private readonly DataGridView _results = UiTheme.Grid();
    private readonly Label _footer = new();

    // Aside card: the selected patient.
    private readonly Panel _detail = new();
    private readonly Label _noSelection = new();
    private readonly InitialsBadge _initials = new();
    private readonly Label _detailName = new();
    private readonly Label _detailFacts = new();
    private readonly FlowLayoutPanel _pills = new();
    private readonly PillLabel _gapPill = new();
    private readonly PillLabel _alertPill = new();
    private readonly Label _recordCaption = new();
    private readonly Label _recordValue = new();
    private readonly Label _cinCaption = new();
    private readonly Label _cinValue = new();
    private readonly Label _phoneCaption = new();
    private readonly Label _phoneValue = new();
    private readonly Label _payerCaption = new();
    private readonly Label _payerValue = new();
    private readonly Button _newOrder = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _detailEdit = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _historyHeading = new();
    private readonly Label _historyCount = new();
    private readonly FlowLayoutPanel _history = new();

    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };

    private PagedResult<PatientSearchRow> _page = PagedResult<PatientSearchRow>.Empty();
    private PatientListCounts? _counts;
    private PatientListScope _scope = PatientListScope.All;
    private IReadOnlyList<PayerOption> _payers = [];
    private int _searchVersion;
    private int _historyVersion;

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
        _detailEdit.Click += async (_, _) => await EditPatientAsync();
        _merge.Click += async (_, _) => await MergeAsync();
        _anonymize.Click += async (_, _) => await AnonymizeAsync();
        _newOrder.Click += async (_, _) => await NewOrderAsync();

        _more.Click += (_, _) =>
        {
            _moreMenu.RightToLeft = RightToLeft;
            _moreMenu.Show(_more, new Point(RightToLeft == RightToLeft.Yes ? _more.Width : 0, _more.Height));
        };

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Patients.Create, _newPatient);
        PermissionGate.Require(Permissions.Orders.Create, _newOrder);

        _results.SelectionChanged += (_, _) => OnSelectionChanged();
        _results.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0) await EditPatientAsync();
        };

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

        // Down from the box goes to the list, where Enter opens the file.
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Down || _results.Rows.Count == 0) return;
            e.Handled = true;
            _results.Focus();
        };

        foreach (var (chip, scope) in Chips())
        {
            chip.Click += async (_, _) =>
            {
                if (_scope == scope) return;
                _scope = scope;
                RenderChips();
                await SearchAsync();
            };
        }

        _payer.SelectionChangeCommitted += async (_, _) => await SearchAsync();
    }

    public override string Title => T("Nav.Patients");

    private (PillButton Chip, PatientListScope Scope)[] Chips() =>
    [
        (_scopeAll, PatientListScope.All),
        (_scopeIncomplete, PatientListScope.IdentityIncomplete),
        (_scopeToday, PatientListScope.SeenToday)
    ];

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(20, 16, 20, 16);

        // --- title row: "Patients  13 patients", the last message, the actions -----------------------

        var titleRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = UiTheme.Surface,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _title.Font = UiTheme.PageTitle;
        _title.ForeColor = UiTheme.TextPrimary;
        _title.AutoSize = true;
        _title.Margin = new Padding(0, 2, 12, 0);

        _resultCount.Font = UiTheme.Body;
        _resultCount.ForeColor = UiTheme.TextSecondary;
        _resultCount.AutoSize = true;
        _resultCount.Margin = new Padding(0, 11, 0, 0);

        var heading = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            BackColor = UiTheme.Surface,
            Margin = Padding.Empty,
            Anchor = AnchorStyles.Left
        };
        heading.Controls.AddRange([_title, _resultCount]);

        _status.Font = UiTheme.Small;
        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleRight;
        _status.Margin = new Padding(12, 0, 12, 0);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            BackColor = UiTheme.Surface,
            Margin = Padding.Empty,
            Anchor = AnchorStyles.Right
        };

        foreach (var button in new[] { _editPatient, _more, _newPatient })
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(0, 34);
            button.Height = 34;
            button.Margin = new Padding(8, 0, 0, 0);
        }

        PatientPaint.KeyHint(_editPatient, () => KeyboardShortcuts.Format(Keys.Enter, Localizer));
        PatientPaint.KeyHint(_newPatient, () => KeyboardShortcuts.Format(Keys.Control | Keys.N, Localizer));
        PatientPaint.Chevron(_more);

        // The rare actions, one click further away: merging two files and the loi 08-09 erasure.
        // Each keeps the icon its button carried.
        _merge.Image = Icons.Render(Glyph.Merge, UiTheme.TextPrimary, 16);
        _anonymize.Image = Icons.Render(Glyph.VenetianMask, UiTheme.TextPrimary, 16);
        _moreMenu.Items.AddRange([_merge, _anonymize]);
        _moreMenu.Font = UiTheme.Body;

        // The create button sits beside the search on purpose: the clerk searches first and
        // registers only when the search comes back empty, which is the order that avoids a
        // duplicate file.
        actions.Controls.AddRange([_editPatient, _more, _newPatient]);

        titleRow.Controls.Add(heading, 0, 0);
        titleRow.Controls.Add(_status, 1, 0);
        titleRow.Controls.Add(actions, 2, 0);

        // --- filter row: search, scope chips with their counts, payer ----------------------------------

        var filters = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            WrapContents = false,
            BackColor = UiTheme.Surface,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        var searchHost = PatientPaint.SearchHost(_search);
        searchHost.Width = 360;
        searchHost.Margin = new Padding(0, 0, 8, 0);

        foreach (var (chip, _) in Chips())
        {
            chip.Height = 30;
            chip.Margin = new Padding(0, 2, 8, 0);
        }

        _payer.DropDownStyle = ComboBoxStyle.DropDownList;
        _payer.FlatStyle = FlatStyle.Flat;
        _payer.Font = UiTheme.Body;
        _payer.Width = 200;
        _payer.Margin = new Padding(0, 5, 0, 0);

        filters.Controls.AddRange([searchHost, _scopeAll, _scopeIncomplete, _scopeToday, _payer]);

        // --- body: the list card and the selected patient's card ---------------------------------------

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = UiTheme.Surface,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        // The aside keeps the width of its facts and buttons; the list takes the rest, which at
        // 1366 pixels is just what its seven columns need.
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        body.Controls.Add(BuildListCard(), 0, 0);
        body.Controls.Add(BuildDetailCard(), 1, 0);

        Controls.Add(body);
        Controls.Add(Spacer(CardGap));
        Controls.Add(filters);
        Controls.Add(Spacer(CardGap));
        Controls.Add(titleRow);
    }

    private static Panel Spacer(int height) =>
        new() { Dock = DockStyle.Top, Height = height, BackColor = UiTheme.Surface, Tag = UiTags.OwnLayout };

    private Panel BuildListCard()
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.SurfaceRaised,
            Padding = new Padding(1, 1, 1, 4),
            Margin = new Padding(0, 0, CardGap / 2, 0)
        };
        UiChrome.AsCard(card);

        _footer.Dock = DockStyle.Bottom;
        _footer.Height = 34;
        _footer.Font = UiTheme.Small;
        _footer.ForeColor = UiTheme.TextSecondary;
        _footer.BackColor = UiTheme.SurfaceRaised;
        _footer.TextAlign = ContentAlignment.MiddleLeft;
        _footer.Padding = new Padding(12, 0, 12, 0);
        PatientPaint.TopRule(_footer);

        BuildResultColumns();

        // Two lines in the patient cell: the name, then sex, age and date of birth.
        _results.RowTemplate.Height = 46;
        _results.CellPainting += PaintPatientCell;

        card.Controls.Add(_results);
        card.Controls.Add(_footer);
        return card;
    }

    private Panel BuildDetailCard()
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.SurfaceRaised,
            Padding = new Padding(16, 16, 16, 8),
            Margin = new Padding(CardGap / 2, 0, 0, 0)
        };
        UiChrome.AsCard(card);

        _noSelection.Dock = DockStyle.Top;
        _noSelection.Height = 40;
        _noSelection.ForeColor = UiTheme.TextSecondary;
        _noSelection.Font = UiTheme.Body;

        _detail.Dock = DockStyle.Fill;
        _detail.BackColor = UiTheme.SurfaceRaised;
        _detail.Tag = UiTags.OwnLayout;
        _detail.Visible = false;

        // --- head: initials, name, sex · age · born on ------------------------------------------------

        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 50,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = UiTheme.SurfaceRaised,
            Margin = Padding.Empty
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        head.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

        _initials.Size = new Size(44, 44);
        _initials.Margin = new Padding(0, 3, 0, 0);
        head.Controls.Add(_initials, 0, 0);
        head.SetRowSpan(_initials, 2);

        _detailName.Font = EntryPaint.Resized(UiTheme.SectionTitle, 12.75f);
        _detailName.ForeColor = UiTheme.TextPrimary;
        _detailName.Dock = DockStyle.Fill;
        _detailName.TextAlign = ContentAlignment.BottomLeft;
        _detailName.AutoEllipsis = true;
        _detailName.UseMnemonic = false;
        _detailName.Margin = Padding.Empty;

        _detailFacts.Font = UiTheme.Body;
        _detailFacts.ForeColor = UiTheme.TextSecondary;
        _detailFacts.Dock = DockStyle.Fill;
        _detailFacts.TextAlign = ContentAlignment.TopLeft;
        _detailFacts.AutoEllipsis = true;
        _detailFacts.Margin = Padding.Empty;

        head.Controls.Add(_detailName, 1, 0);
        head.Controls.Add(_detailFacts, 1, 1);

        // --- pills: an identity still to complete, a critical alert ---------------------------------

        _pills.Dock = DockStyle.Top;
        _pills.Height = 30;
        _pills.WrapContents = false;
        _pills.BackColor = UiTheme.SurfaceRaised;
        _pills.Padding = new Padding(0, 8, 0, 0);
        _gapPill.Margin = new Padding(0, 0, 6, 0);
        _alertPill.Margin = new Padding(0, 0, 6, 0);
        _pills.Controls.AddRange([_gapPill, _alertPill]);

        // --- key facts ---------------------------------------------------------------------------------

        var facts = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 4 * 24 + 12,
            ColumnCount = 2,
            RowCount = 4,
            BackColor = UiTheme.SurfaceRaised,
            Padding = new Padding(0, 8, 0, 4)
        };
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var pairs = new[]
        {
            (_recordCaption, _recordValue, true), (_cinCaption, _cinValue, true),
            (_phoneCaption, _phoneValue, true), (_payerCaption, _payerValue, false)
        };

        for (var i = 0; i < pairs.Length; i++)
        {
            var (caption, value, mono) = pairs[i];
            facts.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

            caption.Font = UiTheme.Body;
            caption.ForeColor = UiTheme.TextSecondary;
            caption.Dock = DockStyle.Fill;
            caption.TextAlign = ContentAlignment.MiddleLeft;
            caption.Margin = Padding.Empty;
            // One line, cut with an ellipsis: a caption longer than its column (« البطاقة الوطنية »
            // in Arabic) wrapped and lost its lower half in the 24-pixel row.
            caption.AutoEllipsis = true;

            value.Font = mono ? UiTheme.Monospace : UiTheme.Body;
            value.ForeColor = UiTheme.TextPrimary;
            value.Dock = DockStyle.Fill;
            value.TextAlign = ContentAlignment.MiddleLeft;
            value.AutoEllipsis = true;
            value.UseMnemonic = false;
            value.Margin = Padding.Empty;

            facts.Controls.Add(caption, 0, i);
            facts.Controls.Add(value, 1, i);
        }

        // --- the patient's actions -------------------------------------------------------------------

        // On two lines when the aside is too narrow for both: "Modifier" goes under the primary.
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            BackColor = UiTheme.SurfaceRaised,
            Padding = new Padding(0, 6, 0, 6)
        };

        foreach (var button in new[] { _newOrder, _detailEdit })
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(0, 34);
            button.Height = 34;
            button.Margin = new Padding(0, 0, 8, 6);
        }

        buttons.Controls.AddRange([_newOrder, _detailEdit]);

        // --- orders ------------------------------------------------------------------------------------

        var historyHead = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = UiTheme.SurfaceRaised,
            Padding = new Padding(0, 10, 0, 4)
        };
        historyHead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        historyHead.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        historyHead.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        PatientPaint.TopRule(historyHead);

        _historyHeading.Font = UiTheme.SectionTitle;
        _historyHeading.ForeColor = UiTheme.TextPrimary;
        _historyHeading.Dock = DockStyle.Fill;
        _historyHeading.TextAlign = ContentAlignment.MiddleLeft;
        _historyHeading.Margin = Padding.Empty;

        _historyCount.Font = UiTheme.Body;
        _historyCount.ForeColor = UiTheme.TextSecondary;
        _historyCount.AutoSize = true;
        _historyCount.Anchor = AnchorStyles.Right;
        _historyCount.Margin = Padding.Empty;

        historyHead.Controls.Add(_historyHeading, 0, 0);
        historyHead.Controls.Add(_historyCount, 1, 0);

        _history.Dock = DockStyle.Fill;
        _history.FlowDirection = FlowDirection.TopDown;
        _history.WrapContents = false;
        _history.AutoScroll = true;
        _history.BackColor = UiTheme.SurfaceRaised;
        _history.Padding = new Padding(0, 2, 0, 0);
        _history.Resize += (_, _) => FitHistoryCards();

        _detail.Controls.Add(_history);
        _detail.Controls.Add(historyHead);
        _detail.Controls.Add(buttons);
        _detail.Controls.Add(facts);
        _detail.Controls.Add(_pills);
        _detail.Controls.Add(head);

        card.Controls.Add(_detail);
        card.Controls.Add(_noSelection);
        return card;
    }

    private void BuildResultColumns()
    {
        _results.Columns.Clear();

        DataGridViewTextBoxColumn Add(string name, int weight, Font? font = null, bool printedOnly = false)
        {
            var column = new DataGridViewTextBoxColumn
            {
                Name = name,
                FillWeight = weight,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.GridBody }
            };

            // Shown in the patient cell's second line; still a column of its own on paper (Ctrl+P).
            if (printedOnly)
            {
                column.Visible = false;
                column.Tag = ListPrinting.PrintedWhenHidden;
            }

            _results.Columns.Add(column);
            return column;
        }

        // The list is in name order: the patient column says so with its arrow.
        // The patient and payer columns wrap (the first is painted on two lines anyway), so their
        // longest name does not set their width: it shares what the codes leave.
        var name = Add("name", 26);
        name.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        name.MinimumWidth = 130;
        name.SortMode = DataGridViewColumnSortMode.Programmatic;
        name.HeaderCell.SortGlyphDirection = SortOrder.Ascending;

        Add("gender", 7, printedOnly: true);
        Add("age", 6, printedOnly: true);
        Add("dob", 10, UiTheme.Monospace, printedOnly: true);
        Add("record", 13, UiTheme.Monospace);
        Add("cin", 10, UiTheme.Monospace);
        Add("phone", 14, UiTheme.Monospace);
        Add("payer", 14).DefaultCellStyle.WrapMode = DataGridViewTriState.True;

        var orders = Add("orders", 8);
        orders.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        orders.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;

        Add("last", 11, UiTheme.Monospace);
    }

    // ---- Texts -------------------------------------------------------------------------------------

    protected override void ApplyTexts()
    {
        _title.Text = T("Patient.Plural");
        _search.PlaceholderText = T("PatientList.SearchPlaceholder");
        _search.AccessibleName = T("PatientList.SearchPlaceholder");

        _newPatient.Text = T("Patient.NewFile");
        _editPatient.Text = T("Action.Edit");
        _detailEdit.Text = T("Action.Edit");
        _more.Text = T("ResultEntry.MoreActions");
        _merge.Text = T("Patient.Merge");
        _anonymize.Text = T("Patient.Anonymize");
        _newOrder.Text = T("PatientList.NewOrderForPatient");

        _noSelection.Text = T("PatientList.NoSelection");
        _recordCaption.Text = T("Patient.RecordNumber");
        _cinCaption.Text = T("Patient.Cin");
        _phoneCaption.Text = T("PatientList.Phone");
        _payerCaption.Text = T("PatientList.Payer");
        _historyHeading.Text = T("PatientList.Orders");

        if (_results.Columns.Count > 0)
        {
            _results.Column("name").HeaderText = T("Patient.Title");
            _results.Column("gender").HeaderText = T("Patient.Gender");
            _results.Column("age").HeaderText = T("Patient.Age");
            _results.Column("dob").HeaderText = T("Patient.DateOfBirth");
            _results.Column("record").HeaderText = T("Patient.RecordNumber");
            _results.Column("cin").HeaderText = T("Patient.Cin");
            _results.Column("phone").HeaderText = T("PatientList.Phone");
            _results.Column("payer").HeaderText = T("PatientList.Payer");
            _results.Column("orders").HeaderText = T("PatientList.Orders");
            _results.Column("last").HeaderText = T("PatientList.LastVisit");
        }

        RenderPayers();
        RenderChips();
        RenderCounts();

        if (_page.Items.Count > 0) RenderResults();
        else ShowDetail(null);

        RefreshActions();
    }

    private void RenderPayers()
    {
        var selected = SelectedPayerId;

        _payer.BeginUpdate();
        _payer.Items.Clear();
        _payer.Items.Add(new PayerChoice(null, T("PatientList.AllPayers")));
        foreach (var payer in _payers) _payer.Items.Add(new PayerChoice(payer.Id, payer.Name));
        _payer.SelectedIndex = 0;

        for (var i = 0; i < _payer.Items.Count; i++)
            if (_payer.Items[i] is PayerChoice { Id: { } id } && id == selected) _payer.SelectedIndex = i;

        _payer.EndUpdate();
    }

    private Guid? SelectedPayerId => (_payer.SelectedItem as PayerChoice)?.Id;

    private void RenderChips()
    {
        string Caption(string label, int? count) => count is { } n ? $"{label} · {n}" : label;

        _scopeAll.Text = Caption(T("OrderEntry.All"), _counts?.All);
        _scopeIncomplete.Text = Caption(T("PatientList.ScopeIncomplete"), _counts?.IdentityIncomplete);
        _scopeToday.Text = Caption(T("PatientList.ScopeSeenToday"), _counts?.SeenToday);

        foreach (var (chip, scope) in Chips())
        {
            chip.Pressed = _scope == scope;
            chip.Width = chip.PreferredWidth;
        }
    }

    private void RenderCounts()
    {
        _resultCount.Text = _counts is { } counts ? T("PatientList.Count", counts.All) : string.Empty;

        if (_page.TotalCount == 0)
        {
            _footer.Text = T("App.NoData");
            return;
        }

        _footer.Text = T("PatientList.Shown", _page.Items.Count, _page.TotalCount)
                       + (_page.Items.Count < _page.TotalCount ? " · " + T("PatientList.RefineForMore") : string.Empty);
    }

    // ---- Keys --------------------------------------------------------------------------------------

    /// <summary>
    /// Ctrl+N opens a new file; Enter in the list opens the selected one, as « Modifier » shows.
    /// Ctrl+F, F5 and Ctrl+P are the shell's.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.N:
                if (_newPatient.Enabled) _newPatient.PerformClick();
                return true;
            case Keys.Enter when _results.ContainsFocus:
                if (_editPatient.Enabled) _editPatient.PerformClick();
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---- Patient file actions ----------------------------------------------------------------------

    private PatientSearchRow? Selected => _results.CurrentRow?.Tag as PatientSearchRow;

    private void RefreshActions()
    {
        var row = Selected;

        _editPatient.Enabled = row is not null;
        _detailEdit.Enabled = row is not null;
        // An anonymised file is the trace of an erasure: it is not anonymised again, merged or
        // given a new request, all three of which the services refuse.
        var live = row is { IsAnonymized: false };

        _anonymize.Enabled = live && PermissionGate.Allows(Permissions.Patients.Anonymize);

        // Merging needs two files, so it is only offered once a search has produced more than one
        // candidate — which is exactly the situation a duplicate creates.
        _merge.Enabled = live && _page.Items.Count > 1 && PermissionGate.Allows(Permissions.Patients.Merge);

        if (!PermissionGate.IsBlocked(_newOrder)) _newOrder.Enabled = live;
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

    /// <summary>"Nouvelle demande pour ce patient": the order screen, with this patient already chosen.</summary>
    private async Task NewOrderAsync()
    {
        if (Selected is not { } row) return;
        await _navigation.NavigateToAsync<OrderRegistrationView>(view => view.PatientToSelect = row.Id);
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

        // An anonymised file is never a merge target: the service refuses it.
        var others = _page.Items.Where(p => p.Id != duplicate.Id && !p.IsAnonymized).ToList();

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

        // The surviving file stays selected, as after an edit: the list went back to its first row,
        // and the clerk's next action (a request, the history) went to another patient.
        await SearchAsync();
        SelectRow(dialog.SurvivingPatientId);
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
        SelectRow(row.Id);
    }

    private void SelectRow(Guid patientId)
    {
        foreach (DataGridViewRow row in _results.Rows)
        {
            if ((row.Tag as PatientSearchRow)?.Id != patientId) continue;
            row.Selected = true;
            _results.CurrentCell = row.Cells["name"];
            return;
        }
    }

    // ---- Loading -----------------------------------------------------------------------------------

    // The screen opens on the patient list (an empty search returns the first page) rather than on
    // an empty grid the clerk had to type into before anything appeared.
    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        _search.Focus();

        if (_payers.Count == 0)
        {
            try
            {
                _payers = await _queries.GetPayersAsync(ct);
                RenderPayers();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The list still works without its payer filter.
                _logger?.LogWarning(ex, "Loading the payers for the patient list failed.");
            }
        }

        // Refreshed (F5, or back from another screen), the list stays on the patient it was on.
        var kept = Selected?.Id;

        await SearchAsync();

        if (kept is { } id) SelectRow(id);
    }

    private async Task SearchAsync()
    {
        var term = _search.Text.Trim();
        var version = ++_searchVersion;

        if (!PatientListRules.IsSearchable(term))
        {
            _page = PagedResult<PatientSearchRow>.Empty();
            _counts = null;
            RenderResults();
            return;
        }

        try
        {
            var query = new PatientListQuery(term, _currentUser.SiteId, _scope, SelectedPayerId);
            var page = await _queries.ListAsync(query);
            var counts = await _queries.CountListAsync(query);

            // A later keystroke or chip has started another search: its answer is the one shown.
            if (version != _searchVersion) return;

            _page = page;
            _counts = counts;
            RenderResults();

            // Loi 08-09: a search over patient data is an access to it, and the number of records
            // returned is what distinguishes a lookup from an extraction.
            if (term.Length >= 3)
                await _accessAuditor.LogAsync(DataAccessKind.Search,
                                              context: "PatientSearch",
                                              recordCount: _page.TotalCount,
                                              queryDescription: term);
        }
        // Left before the search answered (the isolated query is cancelled with the screen, or
        // its emptied grid refuses the rows): the clerk, already on the next screen, was shown
        // « Erreur inattendue » for this one.
        catch (OperationCanceledException) { }
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Patient search failed for '{Term}'.", term);
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
    }

    private string DisplayName(PatientSearchRow row) =>
        Localizer.CurrentLanguage == Domain.Common.AppLanguage.Arabic && row.FullNameArabic is { Length: > 0 }
            ? row.FullNameArabic
            : row.FullName;

    private void RenderResults()
    {
        _results.SuspendLayout();
        _results.Rows.Clear();

        foreach (var row in _page.Items)
        {
            var index = _results.Rows.Add(
                DisplayName(row),
                T($"Gender.{row.Gender}"),
                row.AgeYears?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                row.DateOfBirth?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
                row.MedicalRecordNumber,
                row.Cin ?? "—",
                row.MobilePhone ?? "—",
                row.PayerName ?? T("PatientList.NoPayer"),
                row.OrderCount.ToString(CultureInfo.InvariantCulture),
                row.LastOrderAt?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "—");

            var gridRow = _results.Rows[index];

            // The whole row, not just the id: the action bar needs the name and file number to
            // compose its confirmation prompts, and re-querying for them would be wasteful.
            gridRow.Tag = row;

            if (row.PayerName is null) gridRow.Cells["payer"].Style.ForeColor = UiTheme.TextSecondary;
            foreach (var column in new[] { "cin", "phone", "last" })
                if (gridRow.Cells[column].Value is "—") gridRow.Cells[column].Style.ForeColor = UiTheme.TextSecondary;

            // A critical patient alert — an unpaid balance, a biohazard, a known difficult draw —
            // has to be visible before the clerk opens the file, not after.
            if (row.HasCriticalAlert)
            {
                gridRow.DefaultCellStyle.BackColor = UiTheme.CriticalBackground;
                gridRow.DefaultCellStyle.ForeColor = UiTheme.Critical;
            }
        }

        _results.ResumeLayout(performLayout: true);

        RenderChips();
        RenderCounts();

        if (_results.Rows.Count > 0) _results.SelectFirstRow();

        // The rows were added before their tags: the selection the grid reported while filling had
        // no patient behind it yet.
        OnSelectionChanged();
    }

    /// <summary>The patient cell: the name in semibold, then sex · age · born on, or what the identity lacks.</summary>
    private void PaintPatientCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != _results.Columns["name"]!.Index || e.Graphics is null) return;
        if (_results.Rows[e.RowIndex].Tag is not PatientSearchRow row) return;

        var selected = (e.State & DataGridViewElementStates.Selected) != 0;
        e.PaintBackground(e.CellBounds, selected);

        var rtl = _results.RightToLeft == RightToLeft.Yes;
        var bounds = new Rectangle(e.CellBounds.X + 10, e.CellBounds.Y + 4,
                                   e.CellBounds.Width - 20, e.CellBounds.Height - 8);
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.VerticalCenter;
        if (rtl) flags |= TextFormatFlags.Right | TextFormatFlags.RightToLeft;

        var half = bounds.Height / 2;
        var top = new Rectangle(bounds.X, bounds.Y, bounds.Width, half);
        var bottom = new Rectangle(bounds.X, bounds.Y + half, bounds.Width, bounds.Height - half);

        TextRenderer.DrawText(e.Graphics, DisplayName(row), UiTheme.BodyBold, top,
                              row.HasCriticalAlert ? UiTheme.Critical : UiTheme.TextPrimary, flags);

        if (PatientListRules.IdentityGapKey(row.IdentityGaps) is { } gap)
        {
            var pillText = "! " + T("PatientList.ScopeIncomplete");
            var size = EntryPaint.MeasurePill(pillText, UiTheme.SmallBold, 8);
            var pill = EntryPaint.Mirror(new Rectangle(bounds.X, bottom.Y + (bottom.Height - size.Height) / 2,
                                                       size.Width, size.Height), bounds, rtl);
            EntryPaint.DrawPill(e.Graphics, pill, pillText, UiTheme.SmallBold, EntryPaint.Warning, rtl);

            var rest = EntryPaint.Mirror(new Rectangle(bounds.X + size.Width + 6, bottom.Y,
                                                       Math.Max(0, bounds.Width - size.Width - 6), bottom.Height),
                                         bounds, rtl);
            TextRenderer.DrawText(e.Graphics, T(gap), UiTheme.Small, rest, UiTheme.TextSecondary, flags);
        }
        else
        {
            TextRenderer.DrawText(e.Graphics, Facts(row), UiTheme.Small, bottom, UiTheme.TextSecondary, flags);
        }

        e.Handled = true;
    }

    /// <summary>"Masculin · 67 ans · né le 04/09/1959".</summary>
    private string Facts(PatientSearchRow row)
    {
        var facts = new List<string> { T($"Gender.{row.Gender}") };
        if (row.AgeYears is { } age) facts.Add($"{age} {T("Report.Years")}");
        if (row.DateOfBirth is { } born)
            facts.Add(T(PatientListRules.BornOnKey(row.Gender), born.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)));
        return string.Join(" · ", facts);
    }

    private void OnSelectionChanged()
    {
        RefreshActions();
        ShowDetail(Selected);
        _ = LoadHistoryAsync();
    }

    private void ShowDetail(PatientSearchRow? row)
    {
        _detail.Visible = row is not null;
        _noSelection.Visible = row is null;
        if (row is null) return;

        _initials.Text = OrderRegistrationRules.Initials(row.FullName);
        _initials.Fill = row.HasCriticalAlert ? UiTheme.Critical : UiTheme.Accent;
        _initials.Invalidate();

        _detailName.Text = DisplayName(row);
        _detailName.ForeColor = row.HasCriticalAlert ? UiTheme.Critical : UiTheme.TextPrimary;
        _detailFacts.Text = Facts(row);

        var gap = PatientListRules.IdentityGapKey(row.IdentityGaps);
        _gapPill.Show(gap is null ? string.Empty : $"! {T("PatientList.ScopeIncomplete")} — {T(gap)}", EntryPaint.Warning);
        _alertPill.Show(row.HasCriticalAlert ? $"! {T("PatientList.CriticalAlert")}" : string.Empty, EntryPaint.Critical);
        _pills.Visible = gap is not null || row.HasCriticalAlert;

        _recordValue.Text = row.MedicalRecordNumber;
        _cinValue.Text = row.Cin ?? "—";
        _phoneValue.Text = row.MobilePhone ?? "—";
        _payerValue.Text = row.PayerName ?? T("PatientList.NoPayer");
        _payerValue.ForeColor = row.PayerName is null ? UiTheme.TextSecondary : UiTheme.TextPrimary;
    }

    private async Task LoadHistoryAsync()
    {
        var version = ++_historyVersion;

        if (Selected is not { Id: var patientId })
        {
            ClearHistory();
            return;
        }

        try
        {
            var orders = await _queries.GetOrderHistoryAsync(patientId);

            // The selection moved on while this was loading.
            if (version != _historyVersion) return;

            _history.SuspendLayout();
            ClearHistory();

            var opens = PermissionGate.Allows(Permissions.Orders.View);

            foreach (var order in orders)
            {
                var analyses = T("PatientList.AnalysisCount", order.AnalysisCount);
                var detail = order.PrescriberName is { Length: > 0 } prescriber ? $"{analyses} · {prescriber}" : analyses;
                var priority = order.Priority == Domain.Orders.OrderPriority.Routine
                    ? null
                    : T($"Priority.{order.Priority}");

                var card = new PatientOrderCard(order, T($"OrderStatus.{order.Status}"), priority, detail, opens)
                {
                    RightToLeft = RightToLeft
                };

                // A click on a past order opens the file. The history is where an operator is
                // standing when they need it — "the patient is on the phone about the order from
                // Tuesday" — and making them copy the accession number into another screen is
                // friction for nothing.
                var orderId = order.Id;
                card.Open += async (_, _) =>
                    await _navigation.NavigateToAsync<OrderDetailView>(view => view.OrderId = orderId);

                _history.Controls.Add(card);
            }

            if (orders.Count == 0)
                _history.Controls.Add(new Label
                {
                    Text = T("PatientList.NoOrders"),
                    AutoSize = true,
                    ForeColor = UiTheme.TextSecondary,
                    Font = UiTheme.Body,
                    Margin = new Padding(0, 4, 0, 0)
                });

            _historyCount.Text = orders.Count.ToString(CultureInfo.InvariantCulture);
            FitHistoryCards();
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

    private void ClearHistory()
    {
        var old = _history.Controls.Cast<Control>().ToList();
        _history.Controls.Clear();
        foreach (var control in old) control.Dispose();
        _historyCount.Text = string.Empty;
    }

    // The cards span the column; the scroll bar takes its share once they overflow.
    private void FitHistoryCards()
    {
        var width = _history.ClientSize.Width - 2;
        if (width <= 0) return;
        foreach (var card in _history.Controls.OfType<PatientOrderCard>()) card.Width = width;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _debounce.Dispose();
            _moreMenu.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed record PayerChoice(Guid? Id, string Display)
    {
        public override string ToString() => Display;
    }
}
