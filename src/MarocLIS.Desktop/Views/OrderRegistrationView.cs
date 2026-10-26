using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Catalog;
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
/// Order registration — the reception desk's main screen, « Nouvelle demande ».
/// <para>
/// Laid out in the order the conversation actually happens: who is the patient, what did the
/// prescriber ask for, what does it cost. The running total is visible throughout, because the
/// first question a Moroccan patient asks at the counter is what they will pay today — and with
/// AMO that is not the same as what the analyses cost. That total is a quote from the
/// registration's own pricing (<see cref="IOrderQuotes"/>), so the payer's share and the amount
/// left to the patient are shown before the save, not discovered after it.
/// </para>
/// <para>
/// After the save the price breakdown separates the payer's share from the patient's, and shows any
/// amount charged above the national reference tariff as its own line. That excess is the figure a
/// clerk is most often asked to explain, and burying it inside a total guarantees an argument.
/// </para>
/// </summary>
public sealed class OrderRegistrationView : LocalizedView
{
    private const int CardPadding = 10;
    private const int FieldHeight = 30;
    private const int SectionGap = 8;

    private readonly OrderRegistrationService _registration;
    private readonly IPatientQueries _patientQueries;
    private readonly Application.Administration.IMasterDataQueries _masterData;
    private readonly ICatalogQueries _catalogQueries;
    private readonly IOrderQuotes _quotes;
    private readonly IOrderEntryQueries _entryQueries;
    private readonly INavigationService _navigation;
    private readonly LabelPrintOptions _labelOptions;
    private readonly ISampleLabelRenderer _labelRenderer;
    private readonly ILabelPrinter _labelPrinter;
    private readonly IDocumentOutput _documents;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IUserDialogs _dialogs;
    private readonly Composition.PatientEditorFactory _patientEditor;
    private readonly Application.Administration.MasterDataService _masterDataService;
    private readonly ILogger<OrderRegistrationView>? _logger;

    // Frame
    private readonly Panel _content = new() { Dock = DockStyle.Fill, AutoScroll = true, Tag = UiTags.OwnLayout };
    private readonly Panel _header = new() { Dock = DockStyle.Top, Height = 36, Tag = UiTags.OwnLayout };
    private readonly Label _title = new() { AutoSize = true };
    private readonly Label _subtitle = new() { AutoSize = true };

    // Patient
    private readonly Panel _patientCard = new() { Dock = DockStyle.Top, Tag = UiTags.OwnLayout };
    private readonly Panel _patientSearchHost = new() { Tag = UiTags.OwnLayout };
    private readonly TextBox _patientSearch = new() { BorderStyle = BorderStyle.None };
    private readonly SuggestionList _patientSuggestions = new(36);
    private readonly Button _newPatient = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _emergencyPatient = UiTheme.SecondaryButton(string.Empty);
    private readonly Panel _patientBand = new() { Tag = UiTags.OwnLayout, Visible = false };
    private readonly InitialsBadge _initials = new();
    private readonly Label _patientName = new() { AutoSize = true };
    private readonly Label _patientFacts = new() { AutoSize = false, AutoEllipsis = true };
    private readonly Label _patientIds = new() { AutoSize = false, AutoEllipsis = true };
    private readonly LinkLabel _patientHistory = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleRight };
    private readonly Button _changePatient = UiTheme.SecondaryButton(string.Empty);

    // Request
    private readonly Panel _requestCard = new() { Dock = DockStyle.Top, Tag = UiTags.OwnLayout };
    private readonly ComboBox _priority = new();
    private readonly ComboBox _origin = new();
    private readonly Label _priorityLabel = new();
    private readonly Label _originLabel = new();
    private readonly ComboBox _prescriber = new();
    private readonly Button _newPrescriber = UiTheme.SecondaryButton("+");
    private readonly ToolTip _tips = new();
    private readonly Label _prescriberLabel = new();
    private readonly Label _fastingLabel = new();
    private readonly Button[] _fastingChoices =
    [
        UiTheme.SecondaryButton(string.Empty), UiTheme.SecondaryButton(string.Empty),
        UiTheme.SecondaryButton(string.Empty)
    ];
    private readonly TextBox _clinicalInfo = UiTheme.Input(420);
    private readonly Label _clinicalInfoLabel = new();
    private readonly CheckBox _selfPay = new();

    // Catalogue
    private readonly Panel _work = new() { Dock = DockStyle.Fill, Tag = UiTags.OwnLayout };
    private readonly Panel _catalogCard = new() { Tag = UiTags.OwnLayout };
    private readonly Label _catalogTitle = new() { AutoSize = true };
    private readonly Label _catalogHint = new() { AutoSize = true };
    private readonly Label _frequentLabel = new() { AutoSize = true };
    private readonly List<PillButton> _frequentChips = [];
    private readonly Panel _catalogSearchHost = new() { Tag = UiTags.OwnLayout };
    private readonly TextBox _catalogSearch = new() { BorderStyle = BorderStyle.None };
    private readonly SuggestionList _catalogSuggestions = new(36);
    private readonly Label _filterLabel = new() { AutoSize = true };
    private readonly List<PillButton> _filterChips = [];
    private readonly DataGridView _catalog = UiTheme.Grid();

    // Basket
    private readonly Panel _basketCard = new() { Tag = UiTags.OwnLayout };
    private readonly Label _basketHeading = new() { AutoSize = true };
    private readonly CountBadge _basketCount = new();
    private readonly Button _removeAll = new();
    private readonly DataGridView _basket = UiTheme.Grid();
    private readonly Panel _tubes = new() { Tag = UiTags.OwnLayout };
    private readonly Label _tubesTitle = new() { AutoSize = true };
    private readonly FlowLayoutPanel _tubeList = new() { WrapContents = false, AutoSize = false };
    private readonly Label _tubesCount = new() { AutoSize = true };
    private readonly TableLayoutPanel _totals = new() { ColumnCount = 2, RowCount = 3 };
    private readonly Label _grossLabel = new() { AutoSize = true };
    private readonly Label _grossValue = new() { AutoSize = true };
    private readonly Label _payerLabel = new() { AutoSize = true };
    private readonly Label _payerValue = new() { AutoSize = true };
    private readonly Label _dueLabel = new() { AutoSize = true };
    private readonly Label _dueValue = new() { AutoSize = true };

    // Actions
    private readonly Panel _actionBar = new() { Dock = DockStyle.Bottom, Height = 56, Tag = UiTags.OwnLayout };
    private readonly Button _clear = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new() { AutoSize = false, AutoEllipsis = true };
    private readonly Button _registerWithoutLabels = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _register = UiTheme.PrimaryButton(string.Empty);

    // State
    private readonly List<CatalogPickerItem> _catalogItems = [];
    private readonly List<CatalogPickerItem> _fullCatalog = [];
    private readonly OrderBasket _basketItems = new();
    private IReadOnlyList<PatientSearchRow> _patients = [];
    private IReadOnlyList<DepartmentOption> _departments = [];
    private IReadOnlyList<FrequentPanel> _frequent = [];
    private PatientSearchRow? _patient;
    private bool? _fasting = true;
    private string _filter = OrderBasket.FilterAll;
    private OrderQuote? _quote;
    private (string Text, Color Colour)? _notice;
    private bool _settingPatientText;
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };
    private readonly System.Windows.Forms.Timer _quoteTimer = new() { Interval = 250 };

    private Guid? PatientId => _patient?.Id;

    public OrderRegistrationView(OrderRegistrationService registration,
                                 IPatientQueries patientQueries, ICatalogQueries catalogQueries,
                                 ISampleLabelRenderer labelRenderer, ILabelPrinter labelPrinter,
                                 IDocumentOutput documents, ICurrentUser currentUser,
                                 IClock clock, IUserDialogs dialogs,
                                 Composition.PatientEditorFactory patientEditor,
                                 Application.Administration.IMasterDataQueries masterData,
                                 Application.Administration.MasterDataService masterDataService,
                                 IOrderQuotes quotes, IOrderEntryQueries entryQueries,
                                 INavigationService navigation, LabelPrintOptions labelOptions,
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
        _masterDataService = masterDataService;
        _quotes = quotes;
        _entryQueries = entryQueries;
        _navigation = navigation;
        _labelOptions = labelOptions;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Orders");

    // ---- Layout -----------------------------------------------------------------------------------

    private static Label Caption(Label label)
    {
        label.Font = UiTheme.SmallBold;
        label.ForeColor = UiTheme.TextSecondary;
        label.AutoSize = true;
        return label;
    }

    private static Panel Card(Panel panel)
    {
        panel.BackColor = UiTheme.SurfaceRaised;
        UiChrome.AsCard(panel);
        return panel;
    }

    private static Panel Gap() => new() { Dock = DockStyle.Top, Height = SectionGap, Tag = UiTags.OwnLayout };

    /// <summary>A borderless box inside a framed host: the search fields are taller than a TextBox.</summary>
    private static void SearchHost(Panel host, TextBox box, string? glyphHint = null)
    {
        host.BackColor = UiTheme.SurfaceRaised;
        host.Height = 34;
        box.Font = UiTheme.Body;
        box.BackColor = UiTheme.SurfaceRaised;
        box.ForeColor = UiTheme.TextPrimary;
        host.Controls.Add(box);

        host.Paint += (_, e) =>
        {
            var focused = box.Focused;
            using var pen = new Pen(focused ? UiTheme.Accent : UiTheme.BorderStrong, focused ? 2 : 1);
            var inset = focused ? 1 : 0;
            e.Graphics.DrawRectangle(pen, inset, inset, host.Width - 1 - inset * 2 + (focused ? 0 : 0),
                                     host.Height - 1 - inset * 2);

            if (Icons.Render(Glyph.Search, UiTheme.TextSecondary, 16) is { } icon)
                e.Graphics.DrawImage(icon, 10, (host.Height - 16) / 2, 16, 16);
        };

        host.Resize += (_, _) =>
        {
            box.Bounds = new Rectangle(34, (host.Height - box.PreferredHeight) / 2 + 1,
                                       Math.Max(40, host.Width - 44), box.PreferredHeight);
            host.Invalidate();
        };

        box.GotFocus += (_, _) => host.Invalidate();
        box.LostFocus += (_, _) => host.Invalidate();
    }

    private void BuildLayout()
    {
        Padding = Padding.Empty;

        _content.BackColor = UiTheme.Surface;
        _content.Padding = new Padding(20, 8, 20, 8);

        // --- title ---------------------------------------------------------------------------------

        _title.Font = UiTheme.PageTitle;
        _title.ForeColor = UiTheme.TextPrimary;
        _title.Location = new Point(0, 0);
        _subtitle.Font = UiTheme.Body;
        _subtitle.ForeColor = UiTheme.TextSecondary;
        _header.Controls.AddRange([_title, _subtitle]);
        _header.Resize += (_, _) => LayoutHeader();

        // --- patient -------------------------------------------------------------------------------

        Card(_patientCard);
        _patientCard.TabIndex = 0;
        _patientSearchHost.TabIndex = 0;
        SearchHost(_patientSearchHost, _patientSearch);

        // A walk-in who has never been to the laboratory is registered here, at the counter, in
        // the same pass as their order. Sending the clerk to another screen is how a
        // half-registered file gets abandoned mid-queue.
        _newPatient.Height = 34;
        _newPatient.TabIndex = 1;

        // A patient who cannot be named yet — brought in unconscious, unaccompanied — still needs
        // the sample drawn now. A provisional "X-URG" file is opened in one click; the identity is
        // completed on the patient file later, and the audit trail records when.
        _emergencyPatient.Height = 34;
        _emergencyPatient.ForeColor = UiTheme.Abnormal;
        _emergencyPatient.TabIndex = 2;
        ButtonIcons.Set(_emergencyPatient, Glyph.TriangleAlert, always: true);

        _patientBand.BackColor = UiTheme.AccentSubtle;
        UiChrome.AsCard(_patientBand, UiTheme.AccentSubtle);
        _patientBand.TabIndex = 3;
        _patientName.Font = UiTheme.SectionTitle;
        _patientName.ForeColor = UiTheme.AccentInk;
        _patientFacts.Font = UiTheme.Body;
        _patientFacts.ForeColor = UiTheme.TextPrimary;
        _patientIds.Font = UiTheme.Body;
        _patientIds.ForeColor = Color.FromArgb(0x3E, 0x4C, 0x56);
        _patientHistory.Font = UiTheme.Body;
        _patientHistory.ForeColor = Color.FromArgb(0x3E, 0x4C, 0x56);
        _patientHistory.LinkColor = UiTheme.Accent;
        _patientHistory.ActiveLinkColor = UiTheme.AccentPressed;
        _patientHistory.BackColor = UiTheme.AccentSubtle;
        _changePatient.BackColor = UiTheme.AccentSubtle;
        _changePatient.ForeColor = UiTheme.AccentInk;
        _changePatient.FlatAppearance.BorderColor = Color.FromArgb(0x8F, 0xC3, 0xC8);
        _changePatient.Tag = UiTags.NoMnemonic;
        foreach (var label in new[] { _patientName, _patientFacts, _patientIds }) label.BackColor = UiTheme.AccentSubtle;
        _patientBand.Controls.AddRange([_initials, _patientName, _patientFacts, _patientIds, _patientHistory, _changePatient]);
        _patientBand.Resize += (_, _) => LayoutPatientBand();

        _patientCard.Controls.AddRange([_patientSearchHost, _newPatient, _emergencyPatient, _patientBand]);
        _patientCard.Resize += (_, _) => LayoutPatientCard();

        // --- request -------------------------------------------------------------------------------

        Card(_requestCard);
        _requestCard.TabIndex = 1;

        foreach (var combo in new[] { _priority, _origin })
        {
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.FlatStyle = FlatStyle.Flat;
            combo.Font = UiTheme.Body;
        }

        // The prescriber accepts a doctor from the list or a name typed freely: a walk-in often
        // brings a prescription from a doctor who is not yet in the directory, and registration
        // must not wait for someone to create the doctor's file first.
        _prescriber.DropDownStyle = ComboBoxStyle.DropDown;
        _prescriber.FlatStyle = FlatStyle.Flat;
        _prescriber.Font = UiTheme.Body;
        _prescriber.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _prescriber.AutoCompleteSource = AutoCompleteSource.ListItems;

        // A doctor met for the first time is created here, without leaving the file being typed.
        _newPrescriber.Padding = Padding.Empty;
        _newPrescriber.Font = UiTheme.SectionTitle;
        _newPrescriber.Tag = UiTags.NoMnemonic;

        foreach (var choice in _fastingChoices)
        {
            choice.Tag = UiTags.NoMnemonic;
            choice.Height = FieldHeight;
        }

        _clinicalInfo.Font = UiTheme.Body;
        // The length of its column: a longer text failed the registration with an unexpected error.
        _clinicalInfo.MaxLength = 2000;
        _prescriber.MaxLength = 200;
        _selfPay.Font = UiTheme.Body;
        _selfPay.ForeColor = UiTheme.TextPrimary;
        _selfPay.AutoSize = true;
        _selfPay.BackColor = UiTheme.SurfaceRaised;

        foreach (var label in new[] { _priorityLabel, _originLabel, _prescriberLabel, _fastingLabel, _clinicalInfoLabel })
        {
            Caption(label);
            label.BackColor = UiTheme.SurfaceRaised;
        }

        // Tab order follows the fields as they read, left to right.
        var order = 0;
        foreach (Control control in new Control[]
                 {
                     _priorityLabel, _priority, _originLabel, _origin, _prescriberLabel, _prescriber,
                     _newPrescriber, _fastingLabel, _fastingChoices[0], _fastingChoices[1],
                     _fastingChoices[2], _clinicalInfoLabel, _clinicalInfo, _selfPay
                 })
        {
            control.TabIndex = order++;
            _requestCard.Controls.Add(control);
        }

        _requestCard.Resize += (_, _) => LayoutRequestCard();

        // --- catalogue -----------------------------------------------------------------------------

        Card(_catalogCard);
        _catalogCard.TabIndex = 0;
        _catalogTitle.Font = UiTheme.SectionTitle;
        _catalogTitle.ForeColor = UiTheme.TextPrimary;
        _catalogHint.Font = UiTheme.Small;
        _catalogHint.ForeColor = UiTheme.TextSecondary;
        Caption(_frequentLabel).Font = UiTheme.Small;
        Caption(_filterLabel).Font = UiTheme.Small;
        foreach (var label in new[] { _catalogTitle, _catalogHint, _frequentLabel, _filterLabel })
            label.BackColor = UiTheme.SurfaceRaised;

        SearchHost(_catalogSearchHost, _catalogSearch);
        _catalogSearch.Font = new Font(UiTheme.Body.FontFamily, 10.5f);
        _catalogSearchHost.Height = 38;
        _catalogSearchHost.TabIndex = 0;

        BuildCatalogColumns();
        _catalog.RowTemplate.Height = 32;
        _catalog.Dock = DockStyle.None;
        _catalog.TabIndex = 1;

        _catalogSuggestions.DrawItem += DrawCatalogSuggestion;
        _patientSuggestions.DrawItem += DrawPatientSuggestion;

        _catalogCard.Controls.AddRange([_catalogTitle, _catalogHint, _frequentLabel, _catalogSearchHost,
                                        _filterLabel, _catalog, _catalogSuggestions]);
        _catalogCard.Resize += (_, _) => LayoutCatalogCard();

        // --- basket --------------------------------------------------------------------------------

        Card(_basketCard);
        _basketCard.TabIndex = 1;
        _basketHeading.Font = UiTheme.SectionTitle;
        _basketHeading.ForeColor = UiTheme.TextPrimary;
        _basketHeading.BackColor = UiTheme.SurfaceRaised;

        _removeAll.FlatStyle = FlatStyle.Flat;
        _removeAll.FlatAppearance.BorderSize = 0;
        _removeAll.FlatAppearance.MouseOverBackColor = UiTheme.SurfaceSunken;
        _removeAll.BackColor = UiTheme.SurfaceRaised;
        _removeAll.ForeColor = UiTheme.TextSecondary;
        _removeAll.Font = UiTheme.Body;
        _removeAll.Height = 28;
        _removeAll.Tag = UiTags.NoMnemonic;
        _removeAll.Cursor = Cursors.Hand;

        BuildBasketColumns();
        _basket.Dock = DockStyle.None;
        _basket.RowTemplate.Height = 44;

        _tubes.BackColor = UiTheme.SurfaceSunken;
        _tubesTitle.Font = UiTheme.BodyBold;
        _tubesTitle.BackColor = UiTheme.SurfaceSunken;
        _tubeList.BackColor = UiTheme.SurfaceSunken;
        _tubeList.Margin = Padding.Empty;
        _tubesCount.Font = UiTheme.Body;
        _tubesCount.ForeColor = UiTheme.TextSecondary;
        _tubesCount.BackColor = UiTheme.SurfaceSunken;
        _tubes.Controls.AddRange([_tubesTitle, _tubeList, _tubesCount]);
        _tubes.Resize += (_, _) => LayoutTubes();

        _totals.BackColor = UiTheme.SurfaceRaised;
        _totals.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _totals.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _totals.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        _totals.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        _totals.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        _totals.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, 0, 0, _totals.Width, 0);
        };

        foreach (var label in new[] { _grossLabel, _payerLabel })
        {
            label.Font = UiTheme.Body;
            label.ForeColor = UiTheme.TextSecondary;
        }

        _grossValue.Font = UiTheme.Monospace;
        _payerValue.Font = UiTheme.Monospace;
        _dueLabel.Font = UiTheme.SectionTitle;
        _dueValue.Font = UiTheme.Mono(14f, FontStyle.Bold);

        foreach (var label in new[] { _grossLabel, _grossValue, _payerLabel, _payerValue, _dueLabel, _dueValue })
        {
            label.Anchor = label == _grossLabel || label == _payerLabel || label == _dueLabel
                ? AnchorStyles.Left
                : AnchorStyles.Right;
            label.Margin = new Padding(0, 2, 0, 2);
        }

        _totals.Controls.Add(_grossLabel, 0, 0);
        _totals.Controls.Add(_grossValue, 1, 0);
        _totals.Controls.Add(_payerLabel, 0, 1);
        _totals.Controls.Add(_payerValue, 1, 1);
        _totals.Controls.Add(_dueLabel, 0, 2);
        _totals.Controls.Add(_dueValue, 1, 2);

        _basketCard.Controls.AddRange([_basketHeading, _basketCount, _removeAll, _basket, _tubes, _totals]);
        _basketCard.Resize += (_, _) => LayoutBasketCard();

        _work.BackColor = UiTheme.Surface;
        _work.TabIndex = 2;
        _work.Controls.AddRange([_catalogCard, _basketCard]);
        _work.Resize += (_, _) => LayoutWork();

        // --- action bar ----------------------------------------------------------------------------

        _actionBar.BackColor = UiTheme.SurfaceRaised;
        _actionBar.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, 0, 0, _actionBar.Width, 0);
        };

        _clear.ForeColor = UiTheme.CriticalInk;
        _clear.Height = 34;
        ButtonIcons.Set(_clear, Glyph.Eraser, always: true);
        _registerWithoutLabels.Height = 34;
        ButtonIcons.Set(_registerWithoutLabels, Glyph.Tag, always: true);
        _register.Height = 34;

        _status.Font = UiTheme.Body;
        _status.ForeColor = UiTheme.TextSecondary;
        _status.TextAlign = ContentAlignment.MiddleRight;
        _status.BackColor = UiTheme.SurfaceRaised;

        _actionBar.Controls.AddRange([_clear, _status, _registerWithoutLabels, _register]);
        _actionBar.Resize += (_, _) => LayoutActionBar();

        // --- assembly ------------------------------------------------------------------------------

        // Docked in reverse: the last one added sits at the top.
        _content.Controls.Add(_work);
        _content.Controls.Add(Gap());
        _content.Controls.Add(_requestCard);
        _content.Controls.Add(Gap());
        _content.Controls.Add(_patientCard);
        _content.Controls.Add(_header);
        _content.Controls.Add(_patientSuggestions);

        Controls.Add(_content);
        Controls.Add(_actionBar);
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
                    Font = font ?? UiTheme.GridBody, Alignment = align
                }
            });

        Add("code", 13, UiTheme.Monospace);
        Add("name", 34);
        Add("department", 13);
        Add("tube", 17);
        Add("turnaround", 7, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);
        Add("price", 9, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);
        Add("action", 10, UiTheme.SmallBold, DataGridViewContentAlignment.MiddleRight);
        _catalog.Columns["action"]!.MinimumWidth = 84;

        // Painted by the screen, or short sector names: sized by the grid's share, not by their
        // longest value, so the price and the "+" stay in view on a 1366-pixel screen.
        foreach (var wrapped in new[] { "name", "department", "tube" })
            _catalog.Columns[wrapped]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
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
                    Font = font ?? UiTheme.GridBody, Alignment = align
                }
            });

        Add("name", 58);
        Add("tube", 20);
        Add("price", 15, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);
        Add("remove", 7, UiTheme.Body, DataGridViewContentAlignment.MiddleCenter);
        _basket.Columns["name"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _basket.Columns["tube"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
    }

    // ---- Placement ---------------------------------------------------------------------------------

    private void LayoutHeader()
    {
        _title.Location = new Point(0, 0);
        _subtitle.Location = new Point(_title.Right + 12, _title.Bottom - _subtitle.Height - 3);
    }

    private static int ButtonWidth(Button button, string? hint = null, bool icon = false) =>
        TextRenderer.MeasureText(Mnemonics.Plain(button.Text), button.Font).Width + 36
        + (icon ? 28 : 0) + (hint is null ? 0 : ShortcutHints.Width(hint));

    private void LayoutPatientCard()
    {
        var width = _patientCard.ClientSize.Width;
        if (width <= 0) return;

        var x = CardPadding;
        var newWidth = ButtonWidth(_newPatient, icon: true);
        var emergencyWidth = ButtonWidth(_emergencyPatient, icon: true);
        var searchWidth = Math.Clamp(width - 2 * CardPadding - newWidth - emergencyWidth - 16, 200, 560);

        _patientSearchHost.Bounds = new Rectangle(x, CardPadding, searchWidth, 34);
        _newPatient.Bounds = new Rectangle(_patientSearchHost.Right + 8, CardPadding, newWidth, 34);
        _emergencyPatient.Bounds = new Rectangle(_newPatient.Right + 8, CardPadding, emergencyWidth, 34);

        var bandTop = CardPadding + 34 + 8;
        _patientBand.Bounds = new Rectangle(CardPadding, bandTop, width - 2 * CardPadding, 52);

        var height = _patientBand.Visible ? _patientBand.Bottom + CardPadding : CardPadding * 2 + 34;
        if (_patientCard.Height != height)
        {
            _patientCard.Height = height;
            UpdateScrollMinimum();
        }
    }

    private void LayoutPatientBand()
    {
        var width = _patientBand.ClientSize.Width;
        if (width <= 0) return;

        _initials.Size = new Size(36, 36);
        _initials.Location = new Point(14, 8);

        // The change button on the right, centred; the file's history at the end of the first
        // line; the identifiers along the second line, up to the button.
        var changeWidth = ButtonWidth(_changePatient);
        _changePatient.Bounds = new Rectangle(width - 14 - changeWidth, 11, changeWidth, 30);

        var left = 62;
        var right = _changePatient.Left - 14;

        // Line 1: name and facts, then the direct-payment box at the end when it lives here.
        // Line 2: identifiers, and the file's history at the end.
        _patientName.Location = new Point(left, 4);
        var factsRight = right;
        if (_selfPay.Parent == _patientBand)
        {
            var tick = _selfPay.PreferredSize;
            _selfPay.Bounds = new Rectangle(right - tick.Width, 7, tick.Width, tick.Height);
            factsRight = _selfPay.Left - 12;
        }

        _patientFacts.Bounds = new Rectangle(_patientName.Right + 10, 8, Math.Max(20, factsRight - _patientName.Right - 10), 20);

        // A link label is drawn wider than TextRenderer measures it: a little slack keeps its first
        // figure in view.
        var historyWidth = Math.Min(TextRenderer.MeasureText(_patientHistory.Text, _patientHistory.Font).Width + 50,
                                    Math.Max(120, (right - left) / 2));
        _patientHistory.Bounds = new Rectangle(right - historyWidth, 28, historyWidth, 20);

        var idsRight = _patientHistory.Left - 12;
        _patientIds.Bounds = new Rectangle(left, 28, Math.Max(40, idsRight - left), 20);
    }

    /// <summary>
    /// Places the request's fields in reading order and wraps them when the window is narrow, each
    /// caption above its field; the clinical information takes what is left of its row.
    /// </summary>
    private void LayoutRequestCard()
    {
        var width = _requestCard.ClientSize.Width - 2 * CardPadding;
        if (width <= 0) return;

        var fastingWidths = _fastingChoices.Select(b => ButtonWidth(b)).ToArray();
        var fastingWidth = fastingWidths.Sum();

        // (caption, minimum width, weight, placement)
        var fields = new (Label Caption, int Min, int Weight, Action<Rectangle> Place)[]
        {
            (_priorityLabel, 110, 1, r => _priority.Bounds = r with { Height = FieldHeight }),
            (_originLabel, 150, 1, r => _origin.Bounds = r with { Height = FieldHeight }),
            (_prescriberLabel, 220, 2, r =>
            {
                _prescriber.Bounds = new Rectangle(r.X, r.Y, r.Width - FieldHeight - 6, FieldHeight);
                _newPrescriber.Bounds = new Rectangle(r.Right - FieldHeight, r.Y, FieldHeight, FieldHeight);
            }),
            (_fastingLabel, fastingWidth, 0, r =>
            {
                var x = r.X;
                for (var i = 0; i < _fastingChoices.Length; i++)
                {
                    _fastingChoices[i].Bounds = new Rectangle(x, r.Y, fastingWidths[i] + 1, FieldHeight);
                    x += fastingWidths[i];
                }
            }),
            (_clinicalInfoLabel, 200, 4, r =>
            {
                _clinicalInfo.Bounds = new Rectangle(r.X, r.Y + (FieldHeight - _clinicalInfo.Height) / 2,
                                                     r.Width, _clinicalInfo.Height);
                // On the caption's line, at the end: the counter's one tick box costs no row of its
                // own. Where the field is too narrow for both, the box goes to the patient's band,
                // beside the payer it concerns.
                var tick = _selfPay.PreferredSize;
                if (r.Width >= _clinicalInfoLabel.PreferredWidth + 16 + tick.Width)
                {
                    MoveSelfPay(_requestCard, UiTheme.SurfaceRaised);
                    _selfPay.Bounds = new Rectangle(r.Right - tick.Width, r.Y - 4 - tick.Height + 1, tick.Width, tick.Height);
                }
                else
                {
                    MoveSelfPay(_patientBand, UiTheme.AccentSubtle);
                    LayoutPatientBand();
                }
            })
        };

        const int gap = 16;
        const int captionHeight = 18;
        var rows = new List<List<int>> { new() };
        var used = 0;

        for (var i = 0; i < fields.Length; i++)
        {
            var need = fields[i].Min + (rows[^1].Count > 0 ? gap : 0);
            if (rows[^1].Count > 0 && used + need > width)
            {
                rows.Add([]);
                used = 0;
                need = fields[i].Min;
            }

            rows[^1].Add(i);
            used += need;
        }

        var y = CardPadding;
        foreach (var row in rows)
        {
            var minimum = row.Sum(i => fields[i].Min) + gap * (row.Count - 1);
            var spare = Math.Max(0, width - minimum);
            var weights = row.Sum(i => fields[i].Weight);
            var x = CardPadding;

            foreach (var i in row)
            {
                var extra = weights == 0 ? 0 : spare * fields[i].Weight / weights;
                var fieldWidth = fields[i].Min + extra;
                fields[i].Caption.Location = new Point(x, y);
                fields[i].Place(new Rectangle(x, y + captionHeight + 4, fieldWidth, FieldHeight));
                x += fieldWidth + gap;
            }

            y += captionHeight + 4 + FieldHeight + SectionGap;
        }

        var height = y - SectionGap + CardPadding;
        if (_requestCard.Height != height)
        {
            _requestCard.Height = height;
            UpdateScrollMinimum();
        }
    }

    private void MoveSelfPay(Control parent, Color back)
    {
        if (_selfPay.Parent == parent) return;
        parent.Controls.Add(_selfPay);
        _selfPay.BackColor = back;
    }

    private void LayoutWork()
    {
        var width = _work.ClientSize.Width;
        var height = _work.ClientSize.Height;
        if (width <= 0) return;

        // The catalogue gets the larger share, as in the laboratory's mock-up (13 to 10).
        var left = (width - 12) * 13 / 23;
        _catalogCard.Bounds = new Rectangle(0, 0, left, height);
        _basketCard.Bounds = new Rectangle(left + 12, 0, width - left - 12, height);
    }

    private void LayoutCatalogCard()
    {
        var width = _catalogCard.ClientSize.Width - 2 * CardPadding;
        if (width <= 0) return;

        var y = CardPadding;
        _catalogTitle.Location = new Point(CardPadding, y);

        // A short window (1366 × 768) puts the frequent profiles on the title's line and drops the
        // hint, which the search box's own text repeats; a taller one lays them out as the mock-up.
        var compact = _catalogCard.ClientSize.Height < 380;
        if (compact)
        {
            _catalogHint.Visible = false;
            var chipsLeft = _catalogTitle.Right + 16;
            PlaceChips(_frequentLabel, _frequentChips, y - 2, width - (chipsLeft - CardPadding), chipsLeft);
            y = Math.Max(_catalogTitle.Bottom, y + 26) + 8;
        }
        else
        {
            _catalogHint.Location = new Point(CardPadding + width - _catalogHint.Width, y + 4);
            _catalogHint.Visible = _catalogHint.Left > _catalogTitle.Right + 12;
            y = _catalogTitle.Bottom + 8;
            y = PlaceChips(_frequentLabel, _frequentChips, y, width) + 8;
        }

        _catalogSearchHost.Bounds = new Rectangle(CardPadding, y, width, _catalogSearchHost.Height);
        y = _catalogSearchHost.Bottom + 8;

        y = PlaceChips(_filterLabel, _filterChips, y, width) + 8;

        // Under about 700 pixels the sector, which the filter chips above already name, gives its
        // room to the tube and the price rather than push the "+" behind a horizontal scroll bar.
        if (_catalog.Columns["department"] is { } department) department.Visible = width >= 700;
        _catalog.ScrollBars = ScrollBars.Vertical;
        _catalog.Bounds = new Rectangle(CardPadding, y, width,
                                        Math.Max(64, _catalogCard.ClientSize.Height - y - CardPadding));

        _catalogSuggestions.Bounds = new Rectangle(CardPadding, _catalogSearchHost.Bottom + 2, width,
                                                   _catalogSuggestions.Height);
        _catalogSuggestions.BringToFront();
    }

    /// <summary>One row of chips after its caption; the ones that do not fit are hidden.</summary>
    private static int PlaceChips(Label caption, List<PillButton> chips, int y, int width, int left = CardPadding)
    {
        caption.Location = new Point(left, y + 7);
        var x = caption.Right + 8;
        var right = left + width;

        foreach (var chip in chips)
        {
            var chipWidth = chip.PreferredWidth;
            chip.Visible = x + chipWidth <= right;
            chip.Bounds = new Rectangle(x, y, chipWidth, 28);
            if (chip.Visible) x += chipWidth + 6;
        }

        return y + 28;
    }

    private void LayoutBasketCard()
    {
        var width = _basketCard.ClientSize.Width - 2 * CardPadding;
        var height = _basketCard.ClientSize.Height;
        if (width <= 0) return;

        var y = CardPadding;
        _basketHeading.Location = new Point(CardPadding, y);
        _basketCount.Location = new Point(_basketHeading.Right + 6, y + (_basketHeading.Height - _basketCount.Height) / 2);
        var removeWidth = TextRenderer.MeasureText(_removeAll.Text, _removeAll.Font).Width + 20;
        _removeAll.Bounds = new Rectangle(CardPadding + width - removeWidth, y - 2, removeWidth, 28);
        y = _basketHeading.Bottom + 8;

        const int totalsHeight = 76;
        var tubesHeight = TubesHeight();

        var totalsTop = height - CardPadding - totalsHeight;
        _totals.Bounds = new Rectangle(CardPadding, totalsTop, width, totalsHeight);
        _tubes.Bounds = new Rectangle(CardPadding, totalsTop - 8 - tubesHeight, width, tubesHeight);
        _basket.Bounds = new Rectangle(CardPadding, y, width, Math.Max(48, _tubes.Top - 8 - y));
    }

    /// <summary>One line when the containers fit between the caption and the count, else two.</summary>
    private int TubesHeight()
    {
        var chips = _tubeList.Controls.Cast<Control>().Sum(c => c.Width + c.Margin.Horizontal);
        var room = _tubes.Width - 20 - _tubesTitle.PreferredWidth - _tubesCount.PreferredWidth - 24;
        return chips <= room ? 36 : 64;
    }

    private void LayoutTubes()
    {
        var width = _tubes.ClientSize.Width;
        var twoLines = _tubes.Height > 40;
        var lineHeight = 36;
        _tubesTitle.Location = new Point(10, (lineHeight - _tubesTitle.Height) / 2);
        _tubesCount.Location = new Point(width - 10 - _tubesCount.Width, (lineHeight - _tubesCount.Height) / 2);
        var left = twoLines ? 10 : _tubesTitle.Right + 12;
        var top = twoLines ? lineHeight - 6 : 5;
        var right = twoLines ? width - 10 : _tubesCount.Left - 12;
        _tubeList.Bounds = new Rectangle(left, top, Math.Max(0, right - left), 26);
    }

    private void LayoutActionBar()
    {
        var width = _actionBar.ClientSize.Width;
        if (width <= 0) return;

        var top = (_actionBar.Height - 34) / 2 + 1;
        _clear.Bounds = new Rectangle(20, top, ButtonWidth(_clear, EscapeHint, icon: true), 34);
        var registerWidth = ButtonWidth(_register, "F12", icon: true);
        _register.Bounds = new Rectangle(width - 20 - registerWidth, top, registerWidth, 34);
        var printWidth = ButtonWidth(_registerWithoutLabels, "F8", icon: true);
        _registerWithoutLabels.Bounds = new Rectangle(_register.Left - 8 - printWidth, top, printWidth, 34);
        _status.Bounds = new Rectangle(_clear.Right + 12, top, Math.Max(0, _registerWithoutLabels.Left - 12 - _clear.Right - 12), 34);
    }

    /// <summary>
    /// The page scrolls as a whole once the window is too short for the cards and a usable
    /// catalogue: at 1366 × 768 the lists keep their own scroll bars, below that the page scrolls.
    /// </summary>
    private void UpdateScrollMinimum()
    {
        var fixedHeight = _header.Height + _patientCard.Height + _requestCard.Height + 2 * SectionGap
                          + _content.Padding.Vertical;
        _content.AutoScrollMinSize = new Size(0, fixedHeight + 250);
    }

    // ---- Behaviour ---------------------------------------------------------------------------------

    private string EscapeHint => T("OrderEntry.EscapeKey");

    private void WireBehaviour()
    {
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await SearchPatientsAsync(showAll: false);
        };

        _quoteTimer.Tick += async (_, _) =>
        {
            _quoteTimer.Stop();
            await RefreshQuoteAsync();
        };

        _patientSearch.TextChanged += (_, _) =>
        {
            if (_settingPatientText) return;
            _debounce.Stop();
            _debounce.Start();
        };

        _patientSearch.KeyDown += async (_, e) =>
        {
            switch (e.KeyCode)
            {
                case Keys.Down:
                    e.SuppressKeyPress = true;
                    if (_patientSuggestions.Visible) _patientSuggestions.Step(1);
                    else await SearchPatientsAsync(showAll: true);
                    break;
                case Keys.Up:
                    e.SuppressKeyPress = true;
                    _patientSuggestions.Step(-1);
                    break;
                case Keys.Enter:
                    e.SuppressKeyPress = true;
                    if (!_patientSuggestions.Visible && _patientSearch.Text.Trim().Length >= 3)
                        await SearchPatientsAsync(showAll: false);
                    PickPatient(_patientSuggestions.SelectedItem as PatientSearchRow);
                    break;
            }
        };

        _patientSearch.LostFocus += (_, _) => BeginInvoke(() =>
        {
            if (!_patientSearch.Focused) _patientSuggestions.Visible = false;
        });

        _patientSuggestions.MouseDown += (_, e) =>
        {
            var index = _patientSuggestions.IndexFromPoint(e.Location);
            if (index >= 0) PickPatient(_patientSuggestions.Items[index] as PatientSearchRow);
        };

        _newPatient.Click += async (_, _) => await RegisterNewPatientAsync();
        _emergencyPatient.Click += async (_, _) => await RegisterEmergencyPatientAsync();
        _changePatient.Click += (_, _) => ClearPatient(focusSearch: true);
        _patientHistory.LinkClicked += async (_, _) => await OpenLatestOrderAsync();

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Patients.Create, _newPatient);
        PermissionGate.Require(Permissions.Patients.Create, _emergencyPatient);
        PermissionGate.Require(Permissions.Orders.Create, _register);
        PermissionGate.Require(Permissions.Orders.Create, _registerWithoutLabels);
        PermissionGate.Require([Permissions.Catalog.Manage, Permissions.Orders.Create], _newPrescriber);
        _newPrescriber.Click += async (_, _) => await CreatePrescriberAsync();

        _origin.SelectedIndexChanged += (_, _) => { ApplyPrescriberCaption(); Changed(); };
        _prescriber.TextChanged += (_, _) => Changed();
        _prescriber.SelectedIndexChanged += (_, _) => Changed();

        for (var i = 0; i < _fastingChoices.Length; i++)
        {
            bool? value = i switch { 0 => true, 1 => false, _ => null };
            _fastingChoices[i].Click += (_, _) => SetFasting(value);
        }

        _catalogSearch.TextChanged += async (_, _) =>
        {
            if (_notice is not null) Changed();
            await SearchCatalogueAsync();
        };

        // Typing a code or a name and pressing Enter adds the analysis without touching the mouse;
        // the arrows move through the suggestions, or into the list when none are shown.
        _catalogSearch.KeyDown += async (_, e) =>
        {
            switch (e.KeyCode)
            {
                case Keys.Down:
                    e.SuppressKeyPress = true;
                    if (_catalogSuggestions.Visible)
                    {
                        _catalogSuggestions.Step(1);
                    }
                    else if (_catalog.Rows.Count > 0)
                    {
                        _catalog.Focus();
                        _catalog.SelectFirstRow();
                    }
                    return;
                case Keys.Up:
                    e.SuppressKeyPress = true;
                    _catalogSuggestions.Step(-1);
                    return;
                case Keys.Enter:
                    e.SuppressKeyPress = true;
                    await AddFromSearchAsync();
                    return;
            }
        };

        _catalogSearch.GotFocus += (_, _) => ShowCatalogSuggestions();
        _catalogSearch.LostFocus += (_, _) => BeginInvoke(() =>
        {
            if (!_catalogSearch.Focused) _catalogSuggestions.Visible = false;
        });

        _catalogSuggestions.MouseDown += (_, e) =>
        {
            var index = _catalogSuggestions.IndexFromPoint(e.Location);
            if (index < 0 || _catalogSuggestions.Items[index] is not CatalogPickerItem item) return;
            AddItem(item);
            ClearCatalogSearchIfAdded(item);
        };

        // Double-click, Enter, or the "+" of the row adds to the basket. All three, because a clerk
        // working at speed uses the keyboard and one working with a patient at the counter uses
        // the mouse.
        _catalog.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) AddCatalogRow(e.RowIndex); };
        _catalog.CellClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == _catalog.Columns["action"]!.Index) AddCatalogRow(e.RowIndex);
        };
        _catalog.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || _catalog.CurrentRow is null) return;
            e.SuppressKeyPress = true;
            AddCatalogRow(_catalog.CurrentRow.Index);
        };
        _catalog.CellPainting += PaintCatalogCell;

        _basket.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) RemoveBasketRow(e.RowIndex); };
        _basket.CellClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == _basket.Columns["remove"]!.Index
                && _basket.Rows[e.RowIndex].Tag is BasketLine)
                RemoveBasketRow(e.RowIndex);
        };
        _basket.CellMouseClick += OnBasketMouseClick;
        _basket.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete || _basket.CurrentRow is null) return;
            RemoveBasketRow(_basket.CurrentRow.Index);
        };
        _basket.CellPainting += PaintBasketCell;
        _basket.RowPostPaint += PaintBasketWarning;
        _removeAll.Click += (_, _) => RemoveAll();

        _selfPay.CheckedChanged += (_, _) => { Changed(); RequestQuote(); };
        _register.Click += async (_, _) => await RunBusyAsync(() => RegisterAsync(OrderRegistrationRules.PrintsLabels(withoutLabels: false)));
        _registerWithoutLabels.Click += async (_, _) => await RunBusyAsync(() => RegisterAsync(OrderRegistrationRules.PrintsLabels(withoutLabels: true)));
        _clear.Click += (_, _) => CancelEntry();

        ShortcutHints.Attach(_register, "F12");
        ShortcutHints.Attach(_registerWithoutLabels, "F8");
    }

    /// <summary>
    /// The screen's own keys: F12 saves and prints the labels, as before; F8 saves
    /// without them; Échap abandons the entry
    /// (after asking, when something was typed), F4 goes to the analyses. Échap first closes an open
    /// suggestion list or drop-down.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F12:
                if (_register.Enabled) _register.PerformClick();
                return true;
            case Keys.F8:
                if (_registerWithoutLabels.Enabled) _registerWithoutLabels.PerformClick();
                return true;
            case Keys.F4:
                _catalogSearch.Focus();
                _catalogSearch.SelectAll();
                return true;
            case Keys.Escape:
                if (_patientSuggestions.Visible || _catalogSuggestions.Visible)
                {
                    _patientSuggestions.Visible = false;
                    _catalogSuggestions.Visible = false;
                    return true;
                }

                if (_priority.DroppedDown || _origin.DroppedDown || _prescriber.DroppedDown) break;

                CancelEntry();
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void ApplyTexts()
    {
        _title.Text = T("OrderEntry.Title");
        _subtitle.Text = T("OrderEntry.Subtitle");
        _patientSearch.PlaceholderText = T("OrderEntry.PatientSearchPlaceholder");
        _patientSearch.AccessibleName = T("OrderEntry.PatientSearchPlaceholder");
        _newPatient.Text = T("Patient.NewFile");
        _emergencyPatient.Text = T("OrderEntry.UnidentifiedPatient");
        _changePatient.Text = T("OrderEntry.ChangePatient");

        _priorityLabel.Text = T("Field.Priority");
        _originLabel.Text = T("Order.Origin");
        ApplyPrescriberCaption();
        _tips.SetToolTip(_newPrescriber, T("Order.NewPrescriber"));
        _newPrescriber.AccessibleName = T("Order.NewPrescriber");
        _fastingLabel.Text = T("Order.Fasted");
        _fastingChoices[0].Text = T("Action.Yes");
        _fastingChoices[1].Text = T("Action.No");
        _fastingChoices[2].Text = T("OrderEntry.FastingUnknown");
        _clinicalInfoLabel.Text = T("Order.ClinicalInformation");
        _clinicalInfo.PlaceholderText = T("OrderEntry.ClinicalPlaceholder");
        _selfPay.Text = T("OrderEntry.DirectPayment");

        _catalogTitle.Text = T("Order.AddAnalyses");
        _catalogHint.Text = T("OrderEntry.TypeHint");
        _frequentLabel.Text = T("OrderEntry.FrequentPanels");
        _filterLabel.Text = T("OrderEntry.Catalogue");
        _catalogSearch.PlaceholderText = T("Order.CatalogSearchPlaceholder");
        _catalogSearch.AccessibleName = T("Order.CatalogSearchPlaceholder");

        _basketHeading.Text = T("Order.Analyses");
        _removeAll.Text = T("OrderEntry.RemoveAll");
        _tubesTitle.Text = T("OrderEntry.TubesToDraw");
        _dueLabel.Text = T("OrderEntry.PatientRemainder");

        _clear.Text = T("OrderEntry.CancelEntry");
        _registerWithoutLabels.Text = T("OrderEntry.SaveWithoutLabels");
        _register.Text = T("Action.Save");
        _clear.Padding = new Padding(10, 0, ShortcutHints.Width(EscapeHint), 0);

        FillEnumCombo(_priority, Enum.GetValues<OrderPriority>(), p => T($"Priority.{p}"));
        FillEnumCombo(_origin, Enum.GetValues<OrderOrigin>(), o => T($"OrderOrigin.{o}"));

        if (_catalog.Columns.Count > 0)
        {
            _catalog.Column("code").HeaderText = T("Field.Code");
            _catalog.Column("name").HeaderText = T("Field.Name");
            _catalog.Column("department").HeaderText = T("Field.Department");
            _catalog.Column("tube").HeaderText = T("OrderEntry.Tube");
            _catalog.Column("turnaround").HeaderText = T("OrderEntry.Delay");
            _catalog.Column("price").HeaderText = T("OrderEntry.PriceDh");
            _catalog.Column("action").HeaderText = string.Empty;
        }

        if (_basket.Columns.Count > 0)
        {
            _basket.Column("name").HeaderText = T("Catalog.Analysis");
            _basket.Column("tube").HeaderText = T("OrderEntry.Tube");
            _basket.Column("price").HeaderText = T("OrderEntry.PriceDh");
            _basket.Column("remove").HeaderText = string.Empty;
        }

        BuildFilterChips();
        BuildFrequentChips();
        ApplyFasting();
        if (_patient is not null) ShowPatient(_patient);
        RenderCatalogue();
        RenderBasket();

        LayoutHeader();
        LayoutPatientCard();
        LayoutRequestCard();
        LayoutCatalogCard();
        LayoutBasketCard();
        LayoutActionBar();
    }

    private void ApplyPrescriberCaption()
    {
        var origin = _origin.SelectedItem is EnumChoice<OrderOrigin> o ? o.Value : OrderOrigin.Walkin;
        _prescriberLabel.Text = LabOrder.RequiresPrescriber(origin) ? T("Order.Prescriber") + " *" : T("Order.Prescriber");
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

    /// <summary>
    /// A patient to choose when the screen opens: set by "Nouvelle demande pour ce patient" on the
    /// patient list. Used once, so a refresh (F5) does not choose them again.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Guid? PatientToSelect { get; set; }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        await LoadCatalogueReferenceAsync(ct);
        await SearchCatalogueAsync(ct);
        if (_prescriber.Items.Count == 0) await LoadPrescribersAsync(ct);
        UpdateScrollMinimum();
        _patientSearch.Focus();

        if (PatientToSelect is { } patientId)
        {
            PatientToSelect = null;
            await SelectPatientAsync(patientId);
        }
    }

    /// <summary>
    /// The whole catalogue once (for the profiles offered in one click and the sector filter), the
    /// sectors, and the profiles the site orders most.
    /// </summary>
    private async Task LoadCatalogueReferenceAsync(CancellationToken ct)
    {
        try
        {
            var all = await _catalogQueries.SearchPickerAsync(null, payerId: null, _clock.Today, take: 1000, ct: ct);
            _fullCatalog.Clear();
            _fullCatalog.AddRange(all);

            _departments = await _catalogQueries.GetDepartmentsAsync(ct);
            _frequent = await _entryQueries.GetFrequentPanelsAsync(
                _currentUser.SiteId, _clock.Now.AddDays(-180), take: 5, ct);

            BuildFilterChips();
            BuildFrequentChips();
            LayoutCatalogCard();
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
        catch (Exception ex)
        {
            // Only the shortcuts are lost; the search still finds every analysis.
            _logger?.LogWarning(ex, "Loading the catalogue shortcuts failed.");
        }
    }

    private void BuildFrequentChips()
    {
        foreach (var chip in _frequentChips) { _catalogCard.Controls.Remove(chip); chip.Dispose(); }
        _frequentChips.Clear();

        foreach (var panel in _frequent)
        {
            var chip = new PillButton(panel.Name.For(Localizer.CurrentLanguage));
            _tips.SetToolTip(chip, panel.Code);
            var id = panel.PanelId;
            chip.Click += async (_, _) => await AddPanelAsync(id);
            _frequentChips.Add(chip);
            _catalogCard.Controls.Add(chip);
        }

        _frequentLabel.Visible = _frequentChips.Count > 0;
        if (IsHandleCreated) RtlLayout.Apply(_catalogCard, Localizer.CurrentLanguage);
    }

    private void BuildFilterChips()
    {
        foreach (var chip in _filterChips) { _catalogCard.Controls.Remove(chip); chip.Dispose(); }
        _filterChips.Clear();

        void Add(string key, string text)
        {
            var chip = new PillButton(text) { Pressed = _filter == key, Height = 28 };
            chip.Click += (_, _) => SetFilter(key);
            chip.Name = key;
            _filterChips.Add(chip);
            _catalogCard.Controls.Add(chip);
        }

        Add(OrderBasket.FilterAll, T("OrderEntry.All"));
        Add(OrderBasket.FilterPanels, T("Catalog.Panels"));

        // Only the sectors the catalogue actually has analyses in.
        var used = _fullCatalog.Select(i => i.DepartmentCode).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var department in _departments.Where(d => used.Contains(d.Code)))
            Add(department.Code, department.Name.For(Localizer.CurrentLanguage));

        if (IsHandleCreated) RtlLayout.Apply(_catalogCard, Localizer.CurrentLanguage);
    }

    private void SetFilter(string key)
    {
        _filter = key;
        foreach (var chip in _filterChips) chip.Pressed = chip.Name == key;
        LayoutCatalogCard();
        RenderCatalogue();
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

    private async Task CreatePrescriberAsync()
    {
        if (!_newPrescriber.Enabled) return;
        _newPrescriber.Enabled = false;
        try
        {
            // What was typed and matches nobody becomes the new doctor's name.
            var typed = ChosenPrescriber() is null ? _prescriber.Text : null;
            using var dialog = new PrescriberEditorDialog(Localizer, _dialogs, null, typed);

            while (dialog.ShowDialog(FindForm()) == DialogResult.OK && dialog.Request is { } request)
            {
                var saved = await _masterDataService.SavePrescriberAsync(request);
                if (saved.IsFailure)
                {
                    // The form stays open with what was typed, as in the directory screen.
                    _dialogs.ShowFailure(saved, dialog.Text);
                    continue;
                }

                await LoadPrescribersAsync(CancellationToken.None);
                var created = _prescriber.Items.OfType<PrescriberChoice>().FirstOrDefault(p => p.Id == saved.Value);
                if (created is not null) _prescriber.SelectedItem = created;
                return;
            }
        }
        finally
        {
            _newPrescriber.Enabled = true;
        }
    }

    // A doctor from the list, matched also when the name was typed out in full rather than picked.
    private PrescriberChoice? ChosenPrescriber()
    {
        if (_prescriber.SelectedItem is PrescriberChoice chosen) return chosen;

        return OrderBasket.MatchPrescriber(_prescriber.Items.OfType<PrescriberChoice>(), c => c.Display, _prescriber.Text);
    }

    // ---- Fasting -----------------------------------------------------------------------------------

    /// <summary>
    /// Oui, Non or Non renseigné: the order's <see cref="LabOrder.PatientFasted"/> is a yes/no that
    /// may be left unknown, and a clerk who did not ask should not have to claim either.
    /// </summary>
    private void SetFasting(bool? value)
    {
        _fasting = value;
        ApplyFasting();
        Changed();
    }

    private void ApplyFasting()
    {
        for (var i = 0; i < _fastingChoices.Length; i++)
        {
            bool? value = i switch { 0 => true, 1 => false, _ => null };
            var button = _fastingChoices[i];
            var pressed = value == _fasting;

            if (pressed)
            {
                UiTheme.StylePrimary(button);
            }
            else
            {
                UiTheme.StyleSecondary(button);
            }

            button.AccessibleDescription = pressed ? "pressed" : null;
        }
    }

    // ---- Patient ----------------------------------------------------------------------------------

    private async Task SearchPatientsAsync(bool showAll)
    {
        var term = _patientSearch.Text.Trim();

        // One or two characters match too much to be useful. An empty box lists the patients only
        // when asked (the down arrow), so nobody is chosen until the clerk picks one.
        if (term.Length is > 0 and < 3 || (term.Length == 0 && !showAll))
        {
            _patientSuggestions.Visible = false;
            return;
        }

        try
        {
            var page = await _patientQueries.SearchAsync(term, _currentUser.SiteId,
                                                          pageSize: term.Length == 0 ? 100 : 25);
            _patients = page.Items;

            if (!_patientSearch.Focused && !showAll) return;

            var at = OffsetIn(_patientSearchHost, _content);
            _patientSuggestions.Bounds = new Rectangle(at.X, at.Y + _patientSearchHost.Height + 2,
                                                       Math.Max(_patientSearchHost.Width, 520),
                                                       _patientSuggestions.Height);
            _patientSuggestions.Show([.. _patients], visibleRows: 8);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Patient lookup failed during registration.");
        }
    }

    private static Point OffsetIn(Control control, Control ancestor)
    {
        var point = Point.Empty;
        for (var c = control; c is not null && c != ancestor; c = c.Parent)
            point.Offset(c.Left, c.Top);
        return point;
    }

    private void PickPatient(PatientSearchRow? row)
    {
        _patientSuggestions.Visible = false;
        if (row is null) return;

        _settingPatientText = true;
        _patientSearch.Clear();
        _settingPatientText = false;

        ShowPatient(row);

        // A critical patient alert is stated before the clerk starts composing the order.
        if (row.HasCriticalAlert) _dialogs.Warn(T("Patient.Alerts"), T("Patient.Title"));

        _ = LoadRecentAnalysesAsync(row.Id);
        _priority.Focus();
    }

    private void ShowPatient(PatientSearchRow row)
    {
        _patient = row;
        _initials.Text = OrderRegistrationRules.Initials(row.FullName);
        _initials.Fill = row.HasCriticalAlert ? UiTheme.Critical : UiTheme.Accent;
        _initials.Invalidate();
        _patientName.Text = row.FullName;
        _patientName.ForeColor = row.HasCriticalAlert ? UiTheme.Critical : UiTheme.AccentInk;

        var facts = new List<string> { T($"Gender.{row.Gender}") };
        if (row.AgeYears is { } age) facts.Add($"{age} {T("Report.Years")}");
        if (row.DateOfBirth is { } born)
            facts.Add(T("OrderEntry.BornOn", born.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)));
        _patientFacts.Text = string.Join(" · ", facts);

        var ids = new List<string> { $"{T("Order.Title")} {row.MedicalRecordNumber}" };
        if (row.Cin is { Length: > 0 }) ids.Add($"{T("Patient.Cin")} {row.Cin}");
        if (row.MobilePhone is { Length: > 0 }) ids.Add($"{T("OrderEntry.PhoneShort")} {row.MobilePhone}");
        ids.Add($"{T("Statistics.Payer")} {row.PayerName ?? T("PayerKind.SelfPay")}");
        _patientIds.Text = string.Join("     ", ids);

        var view = T("OrderEntry.View");
        var history = row.OrderCount > 0 && row.LastOrderAt is { } last
            ? T("OrderEntry.PreviousOrders", row.OrderCount,
                last.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture))
            : T("OrderEntry.FirstOrder");

        if (row.OrderCount > 0 && PermissionGate.Allows(Permissions.Orders.View))
        {
            _patientHistory.Text = $"{history} {view}";
            _patientHistory.LinkArea = new LinkArea(history.Length + 1, view.Length);
        }
        else
        {
            _patientHistory.Text = history;
            _patientHistory.LinkArea = new LinkArea(0, 0);
        }

        _patientBand.Visible = true;
        LayoutPatientCard();
        LayoutPatientBand();
        RequestQuote();
        RefreshReadiness();
    }

    private void ClearPatient(bool focusSearch)
    {
        _patient = null;
        _basketItems.ForgetPatient();
        _patientBand.Visible = false;
        LayoutPatientCard();
        RenderBasket();

        if (focusSearch) _patientSearch.Focus();
    }

    private async Task LoadRecentAnalysesAsync(Guid patientId)
    {
        try
        {
            var recent = await _entryQueries.GetRecentAnalysesAsync(
                patientId, _clock.Now.AddDays(-OrderRegistrationRules.RepeatWindowDays));

            if (PatientId != patientId) return;
            _basketItems.SetRecent(recent);
            RenderBasket();
        }
        catch (Exception ex)
        {
            // The reminder is a help; registering does not depend on it.
            _logger?.LogWarning(ex, "Loading the patient's recent analyses failed.");
        }
    }

    /// <summary>"Voir": the patient's latest file, on the file consultation screen.</summary>
    private async Task OpenLatestOrderAsync()
    {
        if (_patient is not { } patient || !PermissionGate.Allows(Permissions.Orders.View)) return;

        try
        {
            var latest = await _patientQueries.GetOrderHistoryAsync(patient.Id, take: 1);
            if (latest.Count == 0) return;
            if (!ConfirmEntryLoss()) return;

            var orderId = latest[0].Id;
            await _navigation.NavigateToAsync<OrderDetailView>(view => view.OrderId = orderId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Opening the patient's latest file failed.");
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
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

        await SelectPatientAsync(id);
    }

    /// <summary>
    /// Opens a provisional file and selects it, with the priority set to urgent: a patient who
    /// cannot be named is, by construction, an emergency.
    /// </summary>
    private async Task RegisterEmergencyPatientAsync()
    {
        var created = await _patientEditor.CreateProvisionalAsync(FindForm());
        if (created is not { } id) return;

        await SelectPatientAsync(id);

        for (var i = 0; i < _priority.Items.Count; i++)
        {
            if (_priority.Items[i] is EnumChoice<OrderPriority> { Value: OrderPriority.Urgent })
            {
                _priority.SelectedIndex = i;
                break;
            }
        }
    }

    private async Task SelectPatientAsync(Guid id)
    {
        try
        {
            var detail = await _patientQueries.GetDetailAsync(id);
            if (detail is null) return;

            // Found again by its file number, so the band shows exactly what the search shows.
            var page = await _patientQueries.SearchAsync(detail.MedicalRecordNumber, _currentUser.SiteId, pageSize: 10);
            if (page.Items.FirstOrDefault(p => p.Id == id) is not { } row) return;

            PickPatient(row);
            Notice($"{detail.MedicalRecordNumber} — {detail.LastName} {detail.FirstName}", UiTheme.Normal);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Selecting the new patient failed.");
        }
    }

    // ---- Catalogue ---------------------------------------------------------------------------------

    private async Task AddFromSearchAsync()
    {
        var term = _catalogSearch.Text.Trim();
        if (term.Length == 0) return;

        // Enter can arrive before the search started by the last keystroke has answered.
        await SearchCatalogueAsync();

        var match = _catalogSuggestions.Visible && _catalogSuggestions.SelectedItem is CatalogPickerItem chosen
            ? chosen
            : OrderRegistrationRules.BestMatch(_catalogItems, term);

        if (match is null)
        {
            Notice(T("App.NoData"), UiTheme.Abnormal);
            return;
        }

        AddItem(match);
        ClearCatalogSearchIfAdded(match);
    }

    // Cleared only when the analysis went in, so a refusal ("already in the panel") leaves the
    // text for the clerk to correct.
    private void ClearCatalogSearchIfAdded(CatalogPickerItem item)
    {
        if (!_basketItems.Contains(item)) return;

        _catalogSearch.Clear();
        _catalogSuggestions.Visible = false;
        _catalogSearch.Focus();
    }

    private int _catalogSearchVersion;

    private async Task SearchCatalogueAsync(CancellationToken ct = default)
    {
        var version = ++_catalogSearchVersion;
        var term = _catalogSearch.Text;

        try
        {
            IReadOnlyList<CatalogPickerItem> items = string.IsNullOrWhiteSpace(term) && _fullCatalog.Count > 0
                ? _fullCatalog
                : await _catalogQueries.SearchPickerAsync(term, payerId: null, _clock.Today, ct: ct);

            // Each keystroke starts a search; only the latest one may fill the list.
            if (version != _catalogSearchVersion) return;

            _catalogItems.Clear();
            _catalogItems.AddRange(items);

            RenderCatalogue();
            ShowCatalogSuggestions();
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

    private void ShowCatalogSuggestions()
    {
        var term = _catalogSearch.Text.Trim();
        if (term.Length == 0 || !_catalogSearch.Focused)
        {
            _catalogSuggestions.Visible = false;
            return;
        }

        LayoutCatalogCard();
        _catalogSuggestions.Show([.. OrderRegistrationRules.Ranked(_catalogItems, term).Take(6)], visibleRows: 6);
    }

    private IEnumerable<CatalogPickerItem> Filtered() => OrderBasket.Filter(_catalogItems, _filter);

    private void RenderCatalogue()
    {
        if (_catalog.Columns.Count == 0) return;
        var language = Localizer.CurrentLanguage;

        _catalog.SuspendLayout();
        _catalog.Rows.Clear();

        foreach (var item in Filtered())
        {
            var index = _catalog.Rows.Add(
                item.Code,
                item.Name.For(language),
                item.IsPanel ? "—" : item.DepartmentName?.For(language) ?? string.Empty,
                item.IsPanel ? T("OrderEntry.SeveralTubes") : item.ContainerName?.For(Localizer.CurrentLanguage) ?? "—",
                T("Catalog.HoursValue", item.TurnaroundHours),
                Price(item.Price),
                string.Empty);

            var row = _catalog.Rows[index];
            row.Tag = item;

            // Panels are emphasised: they are what a prescriber most often writes, and picking
            // the panel rather than its components is both faster and less error-prone.
            if (item.IsPanel) row.Cells["name"].Style.Font = UiTheme.BodyBold;

            if (_basketItems.StateOf(item) != CatalogItemState.Free)
            {
                row.DefaultCellStyle.BackColor = UiTheme.Surface;
                row.DefaultCellStyle.ForeColor = UiTheme.TextSecondary;
            }

            // What the old "Notes" column said, kept as the row's tooltip.
            row.Cells["name"].ToolTipText = Notes(item);
        }

        _catalog.ResumeLayout(performLayout: true);
    }

    private string Notes(CatalogPickerItem item)
    {
        var notes = new List<string>();
        if (item.IsPanel) notes.Add(T("ResultEntry.AnalysisMany", item.ComponentCount));
        if (item.FastingHours is { } fasting) notes.Add($"{T("Order.Fasted")} {T("Catalog.HoursValue", fasting)}");
        if (item.ConsentRequirement != Domain.Catalog.ConsentRequirement.None) notes.Add(T("Consent.Required"));
        if (item.IsSubcontracted) notes.Add(T("Catalog.Subcontracted"));
        return string.Join(" · ", notes);
    }

    private static string Price(decimal amount) =>
        Money.Of(amount).Amount.ToString("N2", CultureInfo.CurrentCulture);

    private void AddCatalogRow(int rowIndex)
    {
        if (_catalog.Rows[rowIndex].Tag is CatalogPickerItem item) AddItem(item);
    }

    private async Task AddPanelAsync(Guid panelId)
    {
        var item = _fullCatalog.FirstOrDefault(i => i.IsPanel && i.Id == panelId);

        if (item is null)
        {
            var code = _frequent.FirstOrDefault(f => f.PanelId == panelId)?.Code;
            if (code is null) return;
            var found = await _catalogQueries.SearchPickerAsync(code, payerId: null, _clock.Today);
            item = found.FirstOrDefault(i => i.IsPanel && i.Id == panelId);
        }

        if (item is not null) AddItem(item);
    }

    private void AddItem(CatalogPickerItem item)
    {
        _notice = null;

        // The basket's rules: an analysis once, a profile taking out its own members chosen alone.
        var added = _basketItems.Add(item);
        var language = Localizer.CurrentLanguage;

        switch (added.Outcome)
        {
            case BasketAddOutcome.AlreadyPresent:
                Notice(T("Order_AnalysisAlreadyPresent"), UiTheme.Abnormal);
                return;

            case BasketAddOutcome.AlreadyInPanel:
                Notice(T("Order.AlreadyInPanel", item.Code, added.CoveringPanel!.Name.For(language)), UiTheme.Abnormal);
                return;
        }

        RenderBasket();
        RenderCatalogue();

        if (added.Outcome == BasketAddOutcome.MergedIntoPanel)
            Notice(T("Order.MergedIntoPanel", string.Join(", ", added.Absorbed!.Select(a => a.Code)), item.Name.For(language)),
                   UiTheme.Abnormal);
    }

    // ---- Basket ------------------------------------------------------------------------------------

    private sealed record BasketLine(CatalogPickerItem Item);

    private sealed record WarningLine(BasketRepeat Repeat)
    {
        public CatalogPickerItem Item => Repeat.Item;
        public RecentAnalysis Previous => Repeat.Previous;
        public string? MemberCode => Repeat.MemberCode;
    }

    private void RemoveBasketRow(int rowIndex)
    {
        var item = _basket.Rows[rowIndex].Tag switch
        {
            BasketLine line => line.Item,
            WarningLine warning => warning.Item,
            _ => null
        };

        if (item is null) return;
        Remove(item);
    }

    private void Remove(CatalogPickerItem item)
    {
        _notice = null;
        _basketItems.Remove(item);
        RenderBasket();
        RenderCatalogue();
    }

    private void RemoveAll()
    {
        if (_basketItems.IsEmpty) return;
        if (_basketItems.Count > 1 && !_dialogs.Confirm(T("OrderEntry.ConfirmRemoveAll", _basketItems.Count))) return;

        _notice = null;
        _basketItems.Clear();
        RenderBasket();
        RenderCatalogue();
    }

    private void RenderBasket()
    {
        if (_basket.Columns.Count == 0) return;
        var language = Localizer.CurrentLanguage;

        _basket.SuspendLayout();
        _basket.Rows.Clear();

        foreach (var item in _basketItems.Items)
        {
            var index = _basket.Rows.Add(item.Name.For(language),
                                         item.IsPanel ? T("OrderEntry.SeveralTubes") : item.ContainerName?.For(Localizer.CurrentLanguage) ?? "—",
                                         Price(item.Price), "✕");
            var row = _basket.Rows[index];
            row.Tag = new BasketLine(item);
            row.Height = 44;
            row.Cells["name"].ToolTipText = BasketNotes(item);

            if (_basketItems.RepeatOf(item, _fullCatalog) is not { } repeat) continue;

            row.DefaultCellStyle.BackColor = UiTheme.AbnormalBackground;
            row.DefaultCellStyle.SelectionBackColor = UiTheme.AbnormalBackground;

            var warningIndex = _basket.Rows.Add(string.Empty, string.Empty, string.Empty, string.Empty);
            var warning = _basket.Rows[warningIndex];
            var line = new WarningLine(repeat);
            warning.Tag = line;
            warning.Height = WarningFits(line) ? 36 : 60;
            warning.DefaultCellStyle.BackColor = UiTheme.AbnormalBackground;
            warning.DefaultCellStyle.SelectionBackColor = UiTheme.AbnormalBackground;
        }

        _basket.ResumeLayout(performLayout: true);

        _basketCount.Text = _basketItems.Count.ToString(CultureInfo.InvariantCulture);
        _basketCount.Visible = !_basketItems.IsEmpty;
        _removeAll.Enabled = !_basketItems.IsEmpty;
        LayoutBasketCard();

        RequestQuote();
        RefreshReadiness();
    }

    private string BasketNotes(CatalogPickerItem item)
    {
        var notes = new List<string>();
        if (item.IsPanel) notes.Add(T("Catalog.Panel"));

        // Two panels can share members; the shared ones are billed once, on the first.
        var shared = _basketItems.SharedCount(item);
        if (shared > 0) notes.Add(T("Order.SharedAnalyses", shared));
        if (item.FastingHours is { } fasting) notes.Add(T("Catalog.HoursValue", fasting));
        if (item.ConsentRequirement != Domain.Catalog.ConsentRequirement.None) notes.Add(T("Consent.Required"));
        return string.Join(" · ", notes);
    }

    // ---- Quote and readiness -----------------------------------------------------------------------

    private void RequestQuote()
    {
        _quoteTimer.Stop();
        _quoteTimer.Start();
    }

    private int _quoteVersion;

    /// <summary>
    /// Asks the registration's pricing for the request as it stands: the same resolution of the
    /// profiles, the same coverage and the same tariff the save will apply.
    /// </summary>
    private async Task RefreshQuoteAsync()
    {
        var version = ++_quoteVersion;

        if (_basketItems.IsEmpty)
        {
            _quote = null;
            ShowQuote();
            return;
        }

        try
        {
            var quoted = await _quotes.QuoteAsync(new OrderQuoteRequest
            {
                PatientId = PatientId,
                AnalysisTestIds = _basketItems.AnalysisTestIds,
                TestPanelIds = _basketItems.TestPanelIds,
                BillDirectlyToPatient = _selfPay.Checked
            });

            if (version != _quoteVersion) return;

            if (quoted.IsFailure)
            {
                _quote = null;
                ShowQuote();
                Notice(Localizer.Describe(quoted.Errors), UiTheme.Critical);
                return;
            }

            _quote = quoted.Value;
            ShowQuote();
            RefreshReadiness();
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Pricing the request failed.");
        }
    }

    private void ShowQuote()
    {
        var language = Localizer.CurrentLanguage;
        var quote = _quote;

        var total = quote?.Pricing.Total ?? Money.Zero;
        var due = quote?.Pricing.DueAtCounter ?? Money.Zero;
        var count = quote?.AnalysisCount ?? 0;

        _grossLabel.Text = T("OrderEntry.GrossFor", count);
        _grossValue.Text = total.Format(language);

        // The payer's line only when a payer covers the request: no payer, the patient pays it all.
        if (quote?.PayerName is { } payer)
        {
            _payerLabel.Text = T("OrderEntry.PayerShareOf", payer.For(language));
            _payerValue.Text = "− " + (total - due).Format(language);
            _payerLabel.Visible = _payerValue.Visible = true;
        }
        else
        {
            _payerLabel.Visible = _payerValue.Visible = false;
        }

        _dueValue.Text = due.Format(language);

        foreach (Control chip in _tubeList.Controls.Cast<Control>().ToList()) chip.Dispose();
        _tubeList.Controls.Clear();

        var tubes = quote?.Tubes ?? [];
        foreach (var group in tubes.GroupBy(t => (Name: t.Name.For(language), t.ColourHex, t.VolumeMl)))
        {
            var volume = group.Key.VolumeMl is { } ml ? $" {ml:0.#} mL" : string.Empty;
            _tubeList.Controls.Add(new TubeChip($"{group.Key.Name}{volume} ×{group.Count()}", group.Key.ColourHex));
        }

        var copies = OrderBasket.LabelCopies(_labelOptions.Copies);
        _tubesCount.Text = T("OrderEntry.TubeCount", tubes.Count, tubes.Count * copies);
        LayoutBasketCard();
        LayoutTubes();
    }

    /// <summary>
    /// What the bar says next to the buttons: ready, what is missing, or what the save would refuse.
    /// A message from the last action (a merge into a profile, the file just registered) stays until
    /// the next change.
    /// </summary>
    private void RefreshReadiness()
    {
        var hasPatient = _patient is not null;
        var hasAnalyses = !_basketItems.IsEmpty;
        _register.Enabled = hasPatient && hasAnalyses;
        _registerWithoutLabels.Enabled = hasPatient && hasAnalyses;

        if (_notice is { } notice)
        {
            _status.Text = notice.Text;
            _status.ForeColor = notice.Colour;
            return;
        }

        var missing = OrderBasket.MissingKeys(hasPatient, hasAnalyses, PrescriberMissing()).Select(T).ToList();

        if (missing.Count > 0)
        {
            _status.Text = T("OrderEntry.Missing", string.Join(", ", missing));
            _status.ForeColor = UiTheme.TextSecondary;
        }
        else if (_quote?.Problems is { Count: > 0 } problems)
        {
            _status.Text = Localizer.Describe(problems);
            _status.ForeColor = UiTheme.Critical;
        }
        else
        {
            // What the old summary under the basket said: the longest fast and a consent to collect.
            var reminders = new List<string> { T("OrderEntry.Ready") };
            var maxFasting = _basketItems.MaxFastingHours;
            if (maxFasting > 0) reminders.Add($"{T("Catalog.FastingRequired")} {T("Catalog.HoursValue", maxFasting)}");
            if (_basketItems.RequiresConsent)
                reminders.Add(T("Consent.Required"));
            _status.Text = string.Join(" · ", reminders);
            _status.ForeColor = UiTheme.Normal;
        }
    }

    private bool PrescriberMissing()
    {
        var origin = _origin.SelectedItem is EnumChoice<OrderOrigin> o ? o.Value : OrderOrigin.Walkin;
        return OrderBasket.PrescriberMissing(ChosenPrescriber() is not null, _prescriber.Text, origin);
    }

    /// <summary>The clerk changed something: last action's message gives way to the readiness.</summary>
    private void Changed()
    {
        _notice = null;
        RefreshReadiness();
    }

    private void Notice(string text, Color colour)
    {
        _notice = (text, colour);
        RefreshReadiness();
    }

    // ---- Painting ----------------------------------------------------------------------------------

    private const TextFormatFlags CellText = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                                             | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

    private TextFormatFlags Flags(TextFormatFlags flags) =>
        RightToLeft == RightToLeft.Yes ? flags | TextFormatFlags.RightToLeft : flags;

    private void PaintCatalogCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.Graphics is null || _catalog.Rows[e.RowIndex].Tag is not CatalogPickerItem item) return;
        var column = _catalog.Columns[e.ColumnIndex].Name;
        if (column is not ("name" or "tube" or "action")) return;

        e.PaintBackground(e.CellBounds, true);
        var g = e.Graphics;
        var bounds = Rectangle.Inflate(e.CellBounds, -8, 0);
        var state = _basketItems.StateOf(item);
        var ink = state == CatalogItemState.Free ? UiTheme.TextPrimary : UiTheme.TextSecondary;

        switch (column)
        {
            case "name":
            {
                var font = item.IsPanel ? UiTheme.BodyBold : UiTheme.GridBody;
                var name = item.Name.For(Localizer.CurrentLanguage);
                var badge = item.IsPanel ? T("OrderEntry.PanelBadgeShort", item.ComponentCount) : null;
                var badgeWidth = badge is null ? 0 : TextRenderer.MeasureText(badge, UiTheme.SmallBold).Width + 10;
                var nameWidth = Math.Min(TextRenderer.MeasureText(name, font).Width, bounds.Width - badgeWidth - 6);
                TextRenderer.DrawText(g, name, font, bounds with { Width = Math.Max(0, nameWidth + 4) }, ink, Flags(CellText));
                if (badge is not null && nameWidth > 0)
                    DrawBadge(g, badge, new Point(bounds.X + nameWidth + 8, bounds.Y + (bounds.Height - 18) / 2));
                break;
            }

            case "tube":
                if (item.IsPanel || item.ContainerName is null)
                {
                    TextRenderer.DrawText(g, item.IsPanel ? T("OrderEntry.SeveralTubes") : "—", UiTheme.GridBody,
                                          bounds, UiTheme.TextSecondary, Flags(CellText));
                }
                else
                {
                    TubeSwatch.Dot(g, bounds with { Width = 10 }, item.ContainerColourHex);
                    TextRenderer.DrawText(g, item.ContainerName.For(Localizer.CurrentLanguage), UiTheme.GridBody,
                                          new Rectangle(bounds.X + 16, bounds.Y, bounds.Width - 16, bounds.Height),
                                          ink, Flags(CellText));
                }
                break;

            case "action":
                if (state == CatalogItemState.Free)
                {
                    var box = new Rectangle(bounds.Right - 28, bounds.Y + (bounds.Height - 26) / 2, 26, 26);
                    using (var fill = new SolidBrush(UiTheme.SurfaceRaised)) g.FillRectangle(fill, box);
                    using (var pen = new Pen(UiTheme.BorderStrong)) g.DrawRectangle(pen, box);
                    TextRenderer.DrawText(g, "+", UiTheme.SectionTitle, box, UiTheme.TextPrimary,
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                else
                {
                    var text = state == CatalogItemState.InRequest ? T("OrderEntry.Added") : T("OrderEntry.InPanelShort");
                    TextRenderer.DrawText(g, text, UiTheme.SmallBold, bounds, UiTheme.Normal,
                                          Flags(CellText | TextFormatFlags.Right));
                }
                break;
        }

        e.Handled = true;
    }

    private static void DrawBadge(Graphics g, string text, Point at)
    {
        var size = TextRenderer.MeasureText(text, UiTheme.SmallBold);
        var box = new Rectangle(at.X, at.Y, size.Width + 8, 18);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = UiChrome.RoundedRectangle(box, 4))
        using (var fill = new SolidBrush(UiTheme.InfoBackground))
            g.FillPath(fill, path);
        TextRenderer.DrawText(g, text, UiTheme.SmallBold, box, UiTheme.Info,
                              TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void PaintBasketCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.Graphics is null) return;
        var tag = _basket.Rows[e.RowIndex].Tag;

        if (tag is WarningLine)
        {
            // The warning's text and buttons are drawn across the whole row afterwards.
            e.PaintBackground(e.CellBounds, false);
            e.Handled = true;
            return;
        }

        if (tag is not BasketLine { Item: var item }) return;
        var column = _basket.Columns[e.ColumnIndex].Name;
        if (column is not ("name" or "tube" or "remove")) return;

        e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
        var g = e.Graphics;
        var bounds = Rectangle.Inflate(e.CellBounds, -8, 0);

        switch (column)
        {
            case "name":
            {
                var top = new Rectangle(bounds.X, bounds.Y + 4, bounds.Width, 20);
                var bottom = new Rectangle(bounds.X, bounds.Y + 22, bounds.Width, 18);
                TextRenderer.DrawText(g, item.Name.For(Localizer.CurrentLanguage), UiTheme.BodyBold, top,
                                      UiTheme.TextPrimary, Flags(CellText));
                TextRenderer.DrawText(g, Subline(item), UiTheme.Mono(8.5f), bottom, UiTheme.TextSecondary,
                                      Flags(CellText));
                break;
            }

            case "tube":
                if (item.IsPanel || item.ContainerName is null)
                {
                    TextRenderer.DrawText(g, item.IsPanel ? T("OrderEntry.SeveralTubes") : "—", UiTheme.GridBody,
                                          bounds, UiTheme.TextSecondary, Flags(CellText));
                }
                else
                {
                    TubeSwatch.Dot(g, bounds with { Width = 10 }, item.ContainerColourHex);
                    TextRenderer.DrawText(g, item.ContainerName.For(Localizer.CurrentLanguage), UiTheme.GridBody,
                                          new Rectangle(bounds.X + 16, bounds.Y, bounds.Width - 16, bounds.Height),
                                          UiTheme.TextPrimary, Flags(CellText));
                }
                break;

            case "remove":
            {
                // Drawn rather than typed: the ✕ character is missing from some installed fonts.
                var centre = new Point(e.CellBounds.X + e.CellBounds.Width / 2, e.CellBounds.Y + e.CellBounds.Height / 2);
                using var pen = new Pen(UiTheme.TextSecondary, 1.5f);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawLine(pen, centre.X - 5, centre.Y - 5, centre.X + 5, centre.Y + 5);
                g.DrawLine(pen, centre.X + 5, centre.Y - 5, centre.X - 5, centre.Y + 5);
                break;
            }
        }

        e.Handled = true;
    }

    private string Subline(CatalogPickerItem item)
    {
        var parts = new List<string> { item.Code };
        parts.Add(item.IsPanel
            ? T("OrderEntry.PanelBadge", item.ComponentCount)
            : item.DepartmentName?.For(Localizer.CurrentLanguage) ?? "—");
        parts.Add(T("Catalog.HoursValue", item.TurnaroundHours));
        return string.Join(" · ", parts);
    }

    private readonly Dictionary<int, (Rectangle Keep, Rectangle Remove)> _warningButtons = [];

    private string WarningText(WarningLine warning)
    {
        var date = warning.Previous.RegisteredAt.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        return warning.MemberCode is { } code
            ? T("OrderEntry.AlreadyTestedMember", code, date, warning.Previous.AccessionNumber)
            : T("OrderEntry.AlreadyTested", date, warning.Previous.AccessionNumber);
    }

    private int WarningButtonsWidth() =>
        TextRenderer.MeasureText(T("OrderEntry.Keep"), UiTheme.Small).Width + 20
        + TextRenderer.MeasureText(T("Action.Remove"), UiTheme.Small).Width + 20 + 18;

    // On one line when the text and the two buttons fit the list's width; otherwise the buttons go
    // under the text, which is never cut.
    private bool WarningFits(WarningLine warning) =>
        TextRenderer.MeasureText(WarningText(warning), UiTheme.BodyBold).Width + WarningButtonsWidth() + 24
        <= Math.Max(_basket.ClientSize.Width - SystemInformation.VerticalScrollBarWidth, 200);

    private void PaintBasketWarning(object? sender, DataGridViewRowPostPaintEventArgs e)
    {
        if (_basket.Rows[e.RowIndex].Tag is not WarningLine warning) return;

        var g = e.Graphics;
        var bounds = e.RowBounds;
        var text = WarningText(warning);

        var keepText = T("OrderEntry.Keep");
        var removeText = T("Action.Remove");
        var keepWidth = TextRenderer.MeasureText(keepText, UiTheme.Small).Width + 20;
        var removeWidth = TextRenderer.MeasureText(removeText, UiTheme.Small).Width + 20;
        var twoLines = bounds.Height > 40;

        var x = bounds.X + 8;
        var textWidth = TextRenderer.MeasureText(text, UiTheme.BodyBold).Width + 4;
        var textArea = twoLines
            ? new Rectangle(x, bounds.Y + 2, bounds.Width - 16, 26)
            : new Rectangle(x, bounds.Y, Math.Min(textWidth, bounds.Width - 16 - keepWidth - removeWidth - 20), bounds.Height - 6);
        TextRenderer.DrawText(g, text, UiTheme.BodyBold, textArea, UiTheme.Abnormal,
                              Flags(twoLines ? TextFormatFlags.VerticalCenter | TextFormatFlags.WordEllipsis | TextFormatFlags.NoPrefix : CellText));

        var buttonsTop = twoLines ? bounds.Y + 30 : bounds.Y + 3;
        var keep = new Rectangle(twoLines ? x : textArea.Right + 10, buttonsTop, keepWidth, 24);
        var remove = new Rectangle(keep.Right + 8, buttonsTop, removeWidth, 24);

        foreach (var (box, caption) in new[] { (keep, keepText), (remove, removeText) })
        {
            using (var fill = new SolidBrush(UiTheme.SurfaceRaised)) g.FillRectangle(fill, box);
            using (var pen = new Pen(Color.FromArgb(0xE0, 0xB4, 0x8F))) g.DrawRectangle(pen, box);
            TextRenderer.DrawText(g, caption, UiTheme.Small, box, UiTheme.Abnormal,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        // Kept relative to the row, for the click.
        _warningButtons[e.RowIndex] = (keep with { Y = keep.Y - bounds.Y }, remove with { Y = remove.Y - bounds.Y });
    }

    private void OnBasketMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.RowIndex < 0 || _basket.Rows[e.RowIndex].Tag is not WarningLine warning) return;
        if (!_warningButtons.TryGetValue(e.RowIndex, out var buttons)) return;

        var cell = _basket.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
        var row = _basket.GetRowDisplayRectangle(e.RowIndex, false);
        var point = new Point(cell.X + e.X, cell.Y + e.Y - row.Y);

        if (buttons.Keep.Contains(point))
        {
            _basketItems.Keep(warning.Item);
            RenderBasket();
        }
        else if (buttons.Remove.Contains(point))
        {
            Remove(warning.Item);
        }
    }

    private void DrawCatalogSuggestion(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _catalogSuggestions.Items[e.Index] is not CatalogPickerItem item) return;

        var g = e.Graphics;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var state = _basketItems.StateOf(item);
        var containsAdded = _basketItems.ChosenMembersOf(item);

        using (var back = new SolidBrush(selected ? UiTheme.AccentSubtle : UiTheme.SurfaceRaised))
            g.FillRectangle(back, e.Bounds);
        if (e.Index > 0)
        {
            using var pen = new Pen(UiTheme.Border);
            g.DrawLine(pen, e.Bounds.Left, e.Bounds.Top, e.Bounds.Right, e.Bounds.Top);
        }

        var b = e.Bounds;
        var ink = state == CatalogItemState.Free ? UiTheme.TextPrimary : UiTheme.TextSecondary;
        var noteWidth = Math.Min(190, b.Width / 4);
        var priceWidth = 80;
        var departmentWidth = Math.Min(130, b.Width / 6);
        var codeWidth = 110;
        var nameWidth = Math.Max(40, b.Width - 24 - codeWidth - departmentWidth - priceWidth - noteWidth);

        var x = b.X + 12;
        TextRenderer.DrawText(g, item.Code, UiTheme.MonospaceBold, new Rectangle(x, b.Y, codeWidth, b.Height), ink, Flags(CellText));
        x += codeWidth;

        var nameArea = new Rectangle(x, b.Y, nameWidth, b.Height);
        var end = DrawHighlighted(g, item.Name.For(Localizer.CurrentLanguage), _catalogSearch.Text.Trim(), nameArea, ink);
        if (item.IsPanel && end + 20 < nameArea.Right)
            DrawBadge(g, T("OrderEntry.PanelBadge", item.ComponentCount), new Point(end + 6, b.Y + (b.Height - 18) / 2));
        x += nameWidth;

        TextRenderer.DrawText(g, item.IsPanel ? "—" : item.DepartmentName?.For(Localizer.CurrentLanguage) ?? "—",
                              UiTheme.GridBody, new Rectangle(x, b.Y, departmentWidth, b.Height), UiTheme.TextSecondary,
                              Flags(CellText));
        x += departmentWidth;

        TextRenderer.DrawText(g, Price(item.Price), UiTheme.Monospace, new Rectangle(x, b.Y, priceWidth, b.Height),
                              ink, Flags(CellText | TextFormatFlags.Right));
        x += priceWidth;

        var (note, colour, font) = state switch
        {
            CatalogItemState.InRequest => ("✓ " + T("OrderEntry.AlreadyInRequest"), UiTheme.TextSecondary, UiTheme.Small),
            CatalogItemState.InPanel => (T("OrderEntry.InPanel", _basketItems.CoveringPanel(item)!.Code),
                                     UiTheme.Abnormal, UiTheme.Small),
            _ when containsAdded.Count > 0 => (T("OrderEntry.ContainsAdded", string.Join(", ", containsAdded)),
                                                UiTheme.Abnormal, UiTheme.Small),
            _ when selected => (T("OrderEntry.EnterToAdd"), UiTheme.AccentInk, UiTheme.SmallBold),
            _ => (string.Empty, UiTheme.TextSecondary, UiTheme.Small)
        };

        TextRenderer.DrawText(g, note, font, new Rectangle(x, b.Y, noteWidth, b.Height), colour,
                              Flags(TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.WordBreak
                                    | TextFormatFlags.NoPrefix));
    }

    /// <summary>The text with what was typed in bold, as the clerk reads where it matched.</summary>
    private int DrawHighlighted(Graphics g, string text, string term, Rectangle area, Color ink)
    {
        var flags = Flags(CellText | TextFormatFlags.NoPadding);
        var at = term.Length == 0 ? -1 : text.IndexOf(term, StringComparison.OrdinalIgnoreCase);

        if (at < 0 || RightToLeft == RightToLeft.Yes)
        {
            TextRenderer.DrawText(g, text, UiTheme.GridBody, area, ink, flags);
            return area.X + Math.Min(area.Width, TextRenderer.MeasureText(text, UiTheme.GridBody, area.Size, flags).Width);
        }

        var x = area.X;
        foreach (var (part, font, colour) in new[]
                 {
                     (text[..at], UiTheme.GridBody, ink),
                     (text.Substring(at, term.Length), UiTheme.BodyBold, UiTheme.TextPrimary),
                     (text[(at + term.Length)..], UiTheme.GridBody, ink)
                 })
        {
            if (part.Length == 0 || x >= area.Right) continue;
            var width = TextRenderer.MeasureText(g, part, font, area.Size, flags).Width;
            TextRenderer.DrawText(g, part, font, new Rectangle(x, area.Y, area.Right - x, area.Height), colour, flags);
            x += width;
        }

        return Math.Min(x, area.Right);
    }

    private void DrawPatientSuggestion(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _patientSuggestions.Items[e.Index] is not PatientSearchRow row) return;

        var g = e.Graphics;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? UiTheme.AccentSubtle : UiTheme.SurfaceRaised))
            g.FillRectangle(back, e.Bounds);
        if (e.Index > 0)
        {
            using var pen = new Pen(UiTheme.Border);
            g.DrawLine(pen, e.Bounds.Left, e.Bounds.Top, e.Bounds.Right, e.Bounds.Top);
        }

        var b = e.Bounds;
        var nameWidth = Math.Min(TextRenderer.MeasureText(row.FullName, UiTheme.BodyBold).Width + 4, b.Width / 2);
        TextRenderer.DrawText(g, row.FullName, UiTheme.BodyBold, new Rectangle(b.X + 12, b.Y, nameWidth, b.Height),
                              row.HasCriticalAlert ? UiTheme.Critical : UiTheme.TextPrimary, Flags(CellText));

        var details = new List<string> { row.MedicalRecordNumber };
        if (row.AgeYears is { } age) details.Add($"{age} {T("Report.Years")}");
        if (row.Cin is { Length: > 0 }) details.Add(row.Cin);
        details.Add(row.PayerName ?? T("PayerKind.SelfPay"));

        var left = b.X + 12 + nameWidth + 10;
        TextRenderer.DrawText(g, string.Join(" · ", details), UiTheme.GridBody,
                              new Rectangle(left, b.Y, Math.Max(0, b.Right - left - 8), b.Height),
                              UiTheme.TextSecondary, Flags(CellText));
    }

    // ---- Registration -------------------------------------------------------------------------------

    private async Task RegisterAsync(bool printLabels)
    {
        if (PatientId is not { } patientId || _basketItems.IsEmpty) return;

        var origin = _origin.SelectedItem is EnumChoice<OrderOrigin> chosenOrigin
            ? chosenOrigin.Value : OrderOrigin.Walkin;
        var prescriber = ChosenPrescriber();
        var prescriberText = _prescriber.Text.Trim();

        // Checked here as well as on save so the clerk is taken to the field that is missing.
        if (OrderBasket.PrescriberMissing(prescriber is not null, prescriberText, origin))
        {
            _dialogs.Warn(T("Order_PrescriberRequired"));
            _prescriber.Focus();
            return;
        }

        var request = new RegisterOrderRequest
        {
            PatientId = patientId,
            AnalysisTestIds = _basketItems.AnalysisTestIds,
            TestPanelIds = _basketItems.TestPanelIds,
            Priority = _priority.SelectedItem is EnumChoice<OrderPriority> p
                ? p.Value : OrderPriority.Routine,
            Origin = origin,
            PrescriberId = prescriber?.Id,
            PrescriberFreeText = prescriber is null && prescriberText.Length > 0 ? prescriberText : null,
            ClinicalInformation = string.IsNullOrWhiteSpace(_clinicalInfo.Text)
                ? null : _clinicalInfo.Text.Trim(),
            PatientFasted = _fasting,
            BillDirectlyToPatient = _selfPay.Checked
        };

        _register.Enabled = false;
        _registerWithoutLabels.Enabled = false;
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
            if (printLabels) await PrintLabelsAsync(registered);

            ResetForm();

            var message = $"{T("Order.AccessionNumber")} {registered.AccessionNumber} — " +
                          T("OrderEntry.SampleCount", registered.Labels.Count);
            if (!printLabels && registered.Labels.Count > 0) message += " — " + T("OrderEntry.LabelsNotPrinted");
            Notice(message, UiTheme.Normal);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Order registration failed.");
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
        finally
        {
            Cursor = Cursors.Default;
            RefreshReadiness();
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
            $"{T("App.LabelValue", T("Order.AccessionNumber"), registered.AccessionNumber)}",
            string.Empty,
            $"{T("App.LabelValue", T("Billing.Gross"), pricing.Gross.Format(language))}"
        };

        if (!pricing.Discount.IsZero)
            lines.Add($"{T("App.LabelValue", T("Billing.Discount"), pricing.Discount.Format(language))}");

        lines.Add($"{T("App.LabelValue", T("Billing.TotalDue"), pricing.Total.Format(language))}");

        if (!pricing.PayerShare.IsZero)
        {
            lines.Add($"{T("App.LabelValue", T("Billing.PayerShare"), pricing.PayerShare.Format(language))}");
            lines.Add($"{T("App.LabelValue", T("Billing.PatientShare"), pricing.PatientShare.Format(language))}");
        }

        if (!pricing.ExcessOverReferenceTariff.IsZero)
        {
            lines.Add(string.Empty);
            lines.Add($"{T("Billing.ExcessOverTariff")} : " +
                      pricing.ExcessOverReferenceTariff.Format(language));
        }

        lines.Add(string.Empty);
        lines.Add($"{T("App.LabelValue", T("Payment.Title"), pricing.DueAtCounter.Format(language))}");

        _dialogs.Info(string.Join(Environment.NewLine, lines), T("OrderEntry.RegisteredTitle"));
    }

    /// <summary>
    /// Renders and opens the tube labels immediately. The labels have to be in the phlebotomist's
    /// hand before the patient leaves the counter, so « Enregistrer » (F12, Ctrl+S) prints them as it
    /// always has; « Enregistrer sans étiquettes » (F8) skips them, and they can be reprinted from the file.
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
            _dialogs.Warn(T("Action.PrintLabels") + " — " + T(FailureMessages.KeyFor(ex)));
        }
    }

    // ---- Abandoning the entry ----------------------------------------------------------------------

    private bool HasEntries =>
        OrderBasket.HasEntries(_patient is not null, _basketItems.Count, _clinicalInfo.Text, _prescriber.Text);

    private bool ConfirmEntryLoss() => !HasEntries || _dialogs.Confirm(T("OrderEntry.ConfirmCancelEntry"));

    /// <summary>« Annuler la saisie »: asks first when anything was typed, then starts afresh.</summary>
    private void CancelEntry()
    {
        if (!ConfirmEntryLoss()) return;
        ResetForm();
    }

    private void ResetForm()
    {
        _notice = null;
        _basketItems.Clear();

        _settingPatientText = true;
        _patientSearch.Clear();
        _settingPatientText = false;
        _patientSuggestions.Visible = false;
        _catalogSearch.Clear();
        _catalogSuggestions.Visible = false;
        ClearPatient(focusSearch: false);
        _clinicalInfo.Clear();
        SetFasting(true);
        _selfPay.Checked = false;

        if (_priority.Items.Count > 0) _priority.SelectedIndex = 0;
        if (_origin.Items.Count > 0) _origin.SelectedIndex = 0;
        _prescriber.SelectedIndex = -1;
        _prescriber.Text = string.Empty;

        RenderCatalogue();
        RenderBasket();
        _patientSearch.Focus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _debounce.Dispose(); _quoteTimer.Dispose(); _tips.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed record PrescriberChoice(Guid Id, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record EnumChoice<T>(T Value, string Display) where T : Enum
    {
        public override string ToString() => Display;
    }
}
