using MarocLIS.Application.Administration;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Morocco;
using MarocLIS.Domain.Organization;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Installation configuration: the site's legal identity, the document numbering, the settings.
/// <para>
/// The first tab is the one that matters on day one. The seeder creates the site with placeholder
/// text and no identifiers, and until they are completed the application refuses every invoice
/// (<c>ValidateForInvoicing</c>) and every report release (<c>ValidateForOperation</c>). The two
/// banners at the top of the tab name exactly what is still outstanding, because an administrator
/// who cannot invoice deserves to be told which field is responsible rather than left to guess.
/// </para>
/// </summary>
public sealed class SettingsView : LocalizedView
{
    private readonly ConfigurationService _configuration;
    private readonly UserAdministrationService _administration;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<SettingsView>? _logger;

    private readonly TabControl _tabs = new FlatTabControl();
    private readonly TabPage _siteTab = new();
    private readonly TabPage _numberingTab = new();
    private readonly TabPage _settingsTab = new();
    private readonly TabPage _printersTab = new();

    // ---- Site tab ----
    private readonly ComboBox _sitePicker = new();
    private readonly Button _newSite = UiTheme.SecondaryButton(string.Empty);
    private Guid? _selectAfterLoad;
    private readonly Label _invoicingBanner = new();
    private readonly Label _operationBanner = new();

    private readonly TextBox _code = UiTheme.Input(120);
    private readonly TextBox _nameFr = UiTheme.Input(240);
    private readonly TextBox _nameAr = UiTheme.Input(240);
    private readonly ComboBox _kind = new();
    private readonly TextBox _legalName = UiTheme.Input(360);
    private readonly TextBox _ice = UiTheme.Input(180);
    private readonly TextBox _fiscalId = UiTheme.Input(160);
    private readonly TextBox _rc = UiTheme.Input(160);
    private readonly TextBox _rcCourt = UiTheme.Input(180);
    private readonly TextBox _taxNumber = UiTheme.Input(160);
    private readonly TextBox _cnss = UiTheme.Input(160);
    private readonly TextBox _inpe = UiTheme.Input(160);
    private readonly TextBox _ministryAuth = UiTheme.Input(220);
    private readonly DateTimePicker _ministryDate = new();
    private readonly CheckBox _hasMinistryDate = new();
    private readonly ComboBox _responsible = new();
    private readonly CheckBox _accredited = new();
    private readonly TextBox _accreditationNumber = UiTheme.Input(180);
    private readonly DateTimePicker _accreditationUntil = new();
    private readonly TextBox _addressFr = UiTheme.Input(360);
    private readonly TextBox _addressAr = UiTheme.Input(360);
    private readonly TextBox _city = UiTheme.Input(180);
    private readonly ComboBox _region = new();
    private readonly TextBox _postalCode = UiTheme.Input(100);
    private readonly TextBox _phone = UiTheme.Input(160);
    private readonly TextBox _fax = UiTheme.Input(160);
    private readonly TextBox _email = UiTheme.Input(240);
    private readonly TextBox _website = UiTheme.Input(240);
    private readonly PictureBox _logo = new();
    private readonly Button _chooseLogo = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _removeLogo = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _logoHint = new();
    private byte[]? _pendingLogo;
    private bool _logoRemoved;
    private readonly Button _saveSite = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _reloadSite = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _siteStatus = new();

    // ---- Numbering tab ----
    private readonly DataGridView _sequences = UiTheme.Grid();
    private readonly Button _editFormat = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _toggleClosed = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _numberingNotice = new();

    // ---- Settings tab ----
    private readonly DataGridView _settings = UiTheme.Grid();
    private readonly Button _editSetting = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _addSetting = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _settingsNotice = new();

    // ---- Printers tab ----
    private readonly WorkstationPrinters? _printers;
    private readonly ILabelPrinter? _labelPrinter;
    private readonly Label _printersIntro = new();
    private readonly Label _documentPrinterLabel = new();
    private readonly ComboBox _documentPrinter = new();
    private readonly CheckBox _printDirectly = new();
    private readonly Label _printDirectlyHint = new();
    private readonly Label _labelPrinterLabel = new();
    private readonly ComboBox _labelPrinterPicker = new();
    private readonly Button _savePrinters = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _testLabel = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _reloadPrinters = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _printersStatus = new();

    private readonly Label _pageTitle = new();
    private readonly Label _pageCaption = new();
    private readonly Label _sequencesHeading = new();
    private readonly Label _settingsHeading = new();

    private IReadOnlyList<SiteDetail> _sites = [];
    private IReadOnlyList<UserRow> _biologists = [];
    private IReadOnlyList<SequenceRow> _sequenceRows = [];
    private IReadOnlyList<SettingRow> _settingRows = [];

    public SettingsView(ConfigurationService configuration,
                        UserAdministrationService administration,
                        IUserDialogs dialogs, ILocalizer localizer,
                        ICultureManager cultureManager,
                        ILogger<SettingsView>? logger = null,
                        WorkstationPrinters? printers = null,
                        ILabelPrinter? labelPrinter = null)
        : base(localizer, cultureManager)
    {
        _printers = printers;
        _labelPrinter = labelPrinter;
        _configuration = configuration;
        _administration = administration;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Settings");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = AdminLayout.Page;

        BuildSiteTab();
        BuildNumberingTab();
        BuildSettingsTab();
        BuildPrintersTab();

        _tabs.TabPages.AddRange([_siteTab, _numberingTab, _settingsTab, _printersTab]);
        Controls.Add(AdminLayout.FlatTabs(_tabs));
        Controls.Add(AdminLayout.PageHeader(_pageTitle, _pageCaption));
    }

    /// <summary>
    /// The bar along the foot of a form tab: what the last action said at the start, the actions at
    /// the end with the primary last — always in view, however far the form is scrolled.
    /// </summary>
    private static EntryStrip ActionBar(Label status, Button primary, params Button[] secondary)
    {
        AdminLayout.Muted(status, UiTheme.SmallBold);
        status.Margin = Padding.Empty;
        status.AutoSize = true;
        status.MaximumSize = new Size(640, 0);

        var row = AdminLayout.Row(AdminLayout.RowHeight + 12, [status], AdminLayout.Actions(primary, secondary),
                                  UiTheme.SurfaceRaised);
        row.Dock = DockStyle.Fill;

        var bar = new EntryStrip { Dock = DockStyle.Bottom, Height = AdminLayout.RowHeight + 13,
                                   BackColor = UiTheme.SurfaceRaised, Tag = UiTags.OwnLayout };
        bar.Padding = new Padding(16, 1, 16, 0);
        bar.Controls.Add(row);
        return bar;
    }

    private void BuildSiteTab()
    {
        _siteTab.BackColor = UiTheme.Surface;

        AdminLayout.Filter(_sitePicker, 380);
        _sitePicker.Margin = Padding.Empty;

        // Next to the picker: a partner laboratory for subcontracting, or a further site, is created
        // here and then completed with the form below like any other.
        AdminLayout.Action(_newSite);

        // Two banners rather than one: invoicing and report release are blocked by different
        // fields, and a laboratory can legitimately be able to do one and not the other.
        foreach (var banner in new[] { _invoicingBanner, _operationBanner })
        {
            banner.Font = UiTheme.SmallBold;
            banner.AutoSize = true;
            banner.Anchor = AnchorStyles.Left;
            banner.Margin = new Padding(16, 0, 0, 0);
            banner.UseMnemonic = false;
        }

        var header = AdminLayout.Row(AdminLayout.RowHeight, [_sitePicker, _newSite, _invoicingBanner], []);
        var banners = AdminLayout.Row(26, [_operationBanner], []);
        _operationBanner.Margin = Padding.Empty;
        banners.Padding = new Padding(0, 0, 0, 6);
        banners.Height = 30;

        var body = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceRaised,
                               AutoScroll = true, AutoScrollMargin = new Size(0, 16) };

        var y = 4;

        Field(body, "Field.Code", y, 0);
        _code.Location = new Point(0, y + 16);
        _code.Tag = UiTags.LatinValue;
        _code.Font = UiTheme.MonospaceBold;

        Field(body, "Field.NameFr", y, 140);
        _nameFr.Location = new Point(140, y + 16);

        Field(body, "Field.NameAr", y, 400);
        _nameAr.Location = new Point(400, y + 16);
        _nameAr.RightToLeft = RightToLeft.Yes;

        Field(body, "Site.Kind", y, 660);
        _kind.DropDownStyle = ComboBoxStyle.DropDownList;
        _kind.FlatStyle = FlatStyle.Flat;
        _kind.Bounds = new Rectangle(660, y + 16, 220, UiTheme.InputHeight);
        foreach (var k in Enum.GetValues<LabSiteKind>())
            _kind.Items.Add(new EnumChoice((int)k, T($"LabSiteKind.{k}")));

        y += 56;

        var legalHeading = new Label
        {
            Text = string.Empty, Name = "legalHeading",
            Font = UiTheme.SectionTitle, ForeColor = UiTheme.TextPrimary,
            AutoSize = false, Bounds = new Rectangle(0, y, 600, 22)
        };
        body.Controls.Add(legalHeading);

        y += 28;

        Field(body, "Site.LegalName", y, 0);
        _legalName.Location = new Point(0, y + 16);

        Field(body, "Site.Ice", y, 380);
        _ice.Location = new Point(380, y + 16);
        _ice.Tag = UiTags.LatinValue;
        _ice.Font = UiTheme.Monospace;

        Field(body, "Site.FiscalId", y, 580);
        _fiscalId.Location = new Point(580, y + 16);
        _fiscalId.Tag = UiTags.LatinValue;
        _fiscalId.Font = UiTheme.Monospace;

        Field(body, "Site.ProfessionalTax", y, 760);
        _taxNumber.Location = new Point(760, y + 16);
        _taxNumber.Tag = UiTags.LatinValue;

        y += 56;

        Field(body, "Site.CommercialRegister", y, 0);
        _rc.Location = new Point(0, y + 16);
        _rc.Tag = UiTags.LatinValue;

        Field(body, "Site.CommercialRegisterCourt", y, 180);
        _rcCourt.Location = new Point(180, y + 16);

        Field(body, "Site.Cnss", y, 380);
        _cnss.Location = new Point(380, y + 16);
        _cnss.Tag = UiTags.LatinValue;
        _cnss.Font = UiTheme.Monospace;

        Field(body, "Site.Inpe", y, 560);
        _inpe.Location = new Point(560, y + 16);
        _inpe.Tag = UiTags.LatinValue;
        _inpe.Font = UiTheme.Monospace;

        y += 56;

        var authHeading = new Label
        {
            Text = string.Empty, Name = "authHeading",
            Font = UiTheme.SectionTitle, ForeColor = UiTheme.TextPrimary,
            AutoSize = false, Bounds = new Rectangle(0, y, 600, 22)
        };
        body.Controls.Add(authHeading);

        y += 28;

        Field(body, "Site.MinistryAuthorization", y, 0);
        _ministryAuth.Location = new Point(0, y + 16);
        _ministryAuth.Tag = UiTags.LatinValue;

        _hasMinistryDate.AutoSize = true;
        // Raised a little: the tick box is taller than a caption and otherwise runs onto the picker.
        _hasMinistryDate.Location = new Point(240, y - 4);
        body.Controls.Add(_hasMinistryDate);

        _ministryDate.Bounds = new Rectangle(240, y + 16, 180, UiTheme.InputHeight);
        _ministryDate.Format = DateTimePickerFormat.Short;
        _ministryDate.MaxDate = DateTime.Today;
        _ministryDate.Enabled = false;

        Field(body, "Site.ResponsibleBiologist", y, 440);
        _responsible.DropDownStyle = ComboBoxStyle.DropDownList;
        _responsible.FlatStyle = FlatStyle.Flat;
        _responsible.Bounds = new Rectangle(440, y + 16, 320, UiTheme.InputHeight);

        y += 56;

        _accredited.AutoSize = true;
        _accredited.Location = new Point(0, y + 4);
        body.Controls.Add(_accredited);

        Field(body, "Site.AccreditationNumber", y, 240);
        _accreditationNumber.Location = new Point(240, y + 16);
        _accreditationNumber.Tag = UiTags.LatinValue;
        _accreditationNumber.Enabled = false;

        Field(body, "Site.AccreditationValidUntil", y, 440);
        _accreditationUntil.Bounds = new Rectangle(440, y + 16, 180, UiTheme.InputHeight);
        _accreditationUntil.Format = DateTimePickerFormat.Short;
        _accreditationUntil.Enabled = false;

        y += 56;

        var contactHeading = new Label
        {
            Text = string.Empty, Name = "contactHeading",
            Font = UiTheme.SectionTitle, ForeColor = UiTheme.TextPrimary,
            AutoSize = false, Bounds = new Rectangle(0, y, 600, 22)
        };
        body.Controls.Add(contactHeading);

        y += 28;

        Field(body, "Patient.Address", y, 0);
        _addressFr.Location = new Point(0, y + 16);

        Field(body, "Field.DescriptionAr", y, 380);
        _addressAr.Location = new Point(380, y + 16);
        _addressAr.RightToLeft = RightToLeft.Yes;

        y += 56;

        Field(body, "Patient.City", y, 0);
        _city.Location = new Point(0, y + 16);

        Field(body, "Patient.Region", y, 200);
        _region.DropDownStyle = ComboBoxStyle.DropDownList;
        _region.FlatStyle = FlatStyle.Flat;
        _region.Bounds = new Rectangle(200, y + 16, 260, UiTheme.InputHeight);
        foreach (var r in Enum.GetValues<MoroccanRegion>())
            _region.Items.Add(new EnumChoice((int)r, T($"MoroccanRegion.{r}")));

        Field(body, "Patient.PostalCode", y, 480);
        _postalCode.Location = new Point(480, y + 16);
        _postalCode.Tag = UiTags.LatinValue;

        Field(body, "Site.Phone", y, 600);
        _phone.Location = new Point(600, y + 16);
        _phone.Tag = UiTags.LatinValue;

        Field(body, "Site.Fax", y, 780);
        _fax.Location = new Point(780, y + 16);
        _fax.Tag = UiTags.LatinValue;

        y += 56;

        Field(body, "Field.Email", y, 0);
        _email.Location = new Point(0, y + 16);
        _email.Tag = UiTags.LatinValue;

        Field(body, "Site.Website", y, 260);
        _website.Location = new Point(260, y + 16);
        _website.Tag = UiTags.LatinValue;

        y += 62;

        // The logo is printed at the head of every report and invoice, beside the identity above.
        Field(body, "Site.Logo", y, 0);
        _logo.Bounds = new Rectangle(0, y + 18, 220, 90);
        _logo.SizeMode = PictureBoxSizeMode.Zoom;
        _logo.BorderStyle = BorderStyle.FixedSingle;
        _logo.BackColor = Color.White;
        _chooseLogo.Bounds = new Rectangle(232, y + 18, 200, UiTheme.ButtonHeight);
        _removeLogo.Bounds = new Rectangle(232, y + 56, 200, UiTheme.ButtonHeight);
        _logoHint.Font = UiTheme.Small;
        _logoHint.ForeColor = UiTheme.TextSecondary;
        _logoHint.AutoSize = false;
        _logoHint.Bounds = new Rectangle(444, y + 24, 520, 60);

        body.Controls.AddRange([_code, _nameFr, _nameAr, _kind, _legalName, _ice, _fiscalId,
                                _taxNumber, _rc, _rcCourt, _cnss, _inpe, _ministryAuth,
                                _ministryDate, _responsible, _accreditationNumber,
                                _accreditationUntil, _addressFr, _addressAr, _city, _region,
                                _postalCode, _phone, _fax, _email, _website,
                                _logo, _chooseLogo, _removeLogo, _logoHint]);

        // The form in a white card, its fields where they were; Save and Reload in the bar at
        // the card's foot rather than at the end of a page that has to be scrolled to reach them.
        var card = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceRaised,
                               Padding = new Padding(20, 14, 2, 1) };
        card.Controls.Add(body);
        card.Controls.Add(ActionBar(_siteStatus, _saveSite, _reloadSite));
        UiChrome.AsCard(card);

        _siteTab.Controls.Add(AdminLayout.Spaced(card, new Padding(0, 2, 0, 0)));
        _siteTab.Controls.Add(banners);
        _siteTab.Controls.Add(header);
    }

    private void BuildNumberingTab()
    {
        _numberingTab.BackColor = UiTheme.Surface;

        _sequences.Columns.Clear();
        void Add(string name, int w, Font? f = null, string align = "")
            => _sequences.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = w, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = f ?? UiTheme.Body,
                    Alignment = align == "r" ? DataGridViewContentAlignment.MiddleRight
                                             : DataGridViewContentAlignment.MiddleLeft
                }
            });

        Add("purpose", 20);
        Add("site", 8, UiTheme.Monospace);
        Add("year", 7, UiTheme.Monospace, "r");
        Add("format", 20, UiTheme.MonospaceBold);
        Add("preview", 18, UiTheme.Monospace);
        Add("next", 9, UiTheme.Monospace, "r");
        Add("used", 9, UiTheme.Monospace, "r");
        Add("state", 9, UiTheme.SmallBold);

        AdminLayout.ListGrid(_sequences);
        _numberingTab.Controls.Add(AdminLayout.CardSection(_sequencesHeading, _sequences,
                                                           AdminLayout.Actions(_editFormat, _toggleClosed)));
        _numberingTab.Controls.Add(AdminLayout.Note(_numberingNotice, 40));
    }

    private void BuildSettingsTab()
    {
        _settingsTab.BackColor = UiTheme.Surface;

        _settings.Columns.Clear();
        void Add(string name, int w, Font? f = null)
            => _settings.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = w, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = f ?? UiTheme.Body }
            });

        Add("key", 24, UiTheme.Monospace);
        Add("value", 24, UiTheme.Monospace);
        Add("site", 8, UiTheme.Monospace);
        Add("description", 34, UiTheme.Small);
        Add("sensitive", 10, UiTheme.SmallBold);

        AdminLayout.ListGrid(_settings);
        _settingsTab.Controls.Add(AdminLayout.CardSection(_settingsHeading, _settings,
                                                          AdminLayout.Actions(_editSetting, _addSetting)));
        _settingsTab.Controls.Add(AdminLayout.Note(_settingsNotice, 40));
    }

    private void BuildPrintersTab()
    {
        _printersTab.BackColor = UiTheme.Surface;

        Label Text(Label label, Font font, Color colour, Rectangle bounds)
        {
            label.Font = font;
            label.ForeColor = colour;
            label.AutoSize = false;
            label.Bounds = bounds;
            return label;
        }

        ComboBox Picker(ComboBox combo, int y)
        {
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.FlatStyle = FlatStyle.Flat;
            combo.Bounds = new Rectangle(0, y, 420, UiTheme.InputHeight);
            return combo;
        }

        Text(_printersIntro, UiTheme.Small, UiTheme.TextSecondary, new Rectangle(0, 0, 760, 36));

        Text(_documentPrinterLabel, UiTheme.BodyBold, UiTheme.TextPrimary, new Rectangle(0, 48, 760, 22));
        Picker(_documentPrinter, 72);

        _printDirectly.Font = UiTheme.Body;
        _printDirectly.AutoSize = false;
        _printDirectly.Bounds = new Rectangle(0, 106, 760, 24);

        Text(_printDirectlyHint, UiTheme.Small, UiTheme.TextSecondary, new Rectangle(20, 130, 740, 36));

        Text(_labelPrinterLabel, UiTheme.BodyBold, UiTheme.TextPrimary, new Rectangle(0, 182, 760, 22));
        Picker(_labelPrinterPicker, 206);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceRaised, AutoScroll = true };
        body.Controls.AddRange(
        [
            _printersIntro, _documentPrinterLabel, _documentPrinter, _printDirectly, _printDirectlyHint,
            _labelPrinterLabel, _labelPrinterPicker
        ]);

        var card = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceRaised,
                               Padding = new Padding(20, 16, 2, 1) };
        card.Controls.Add(body);
        card.Controls.Add(ActionBar(_printersStatus, _savePrinters, _testLabel, _reloadPrinters));
        UiChrome.AsCard(card);

        _printersTab.Controls.Add(card);

        _printDirectly.CheckedChanged += (_, _) => UpdatePrinterControls();
        _documentPrinter.SelectedIndexChanged += (_, _) => UpdatePrinterControls();
        _savePrinters.Click += (_, _) => SavePrinters();
        _testLabel.Click += (_, _) => PrintTestLabel();
        _reloadPrinters.Click += (_, _) => PopulatePrinters();
    }

    // The first entry of each list is "none": the Windows default printer for documents, a PDF for
    // labels. A printer that was chosen earlier but is no longer installed stays in the list,
    // marked, so the operator sees why it no longer prints instead of a silently changed choice.
    private void PopulatePrinters()
    {
        var installed = WorkstationPrinters.Installed();

        void Fill(ComboBox combo, string none, string current)
        {
            combo.Items.Clear();
            combo.Items.Add(new PrinterChoice(string.Empty, none));

            foreach (var name in installed) combo.Items.Add(new PrinterChoice(name, name));

            if (current.Length > 0 && !installed.Contains(current, StringComparer.OrdinalIgnoreCase))
                combo.Items.Add(new PrinterChoice(current, $"{current} — {T("Printers.Missing")}"));

            combo.SelectedIndex = Math.Max(0, combo.Items.Cast<PrinterChoice>().ToList()
                .FindIndex(c => c.Name.Equals(current, StringComparison.OrdinalIgnoreCase)));
        }

        var defaultPrinter = WorkstationPrinters.DefaultPrinter();
        var documentNone = defaultPrinter is null
            ? T("Printers.WindowsDefault")
            : $"{T("Printers.WindowsDefault")} ({defaultPrinter})";

        Fill(_documentPrinter, documentNone, _printers?.DocumentPrinter ?? string.Empty);
        Fill(_labelPrinterPicker, T("Printers.LabelsAsPdf"), _printers?.LabelPrinter ?? string.Empty);

        _printDirectly.Checked = _printers?.PrintDocumentsDirectly ?? false;

        _printersStatus.ForeColor = UiTheme.TextSecondary;
        _printersStatus.Text = installed.Count == 0 ? T("Printers.NoneInstalled") : string.Empty;

        UpdatePrinterControls();
    }

    private void UpdatePrinterControls()
    {
        var hasDocumentPrinter = _documentPrinter.SelectedItem is PrinterChoice { Name.Length: > 0 };

        _printDirectly.Enabled = hasDocumentPrinter && _printers is not null;
        if (!hasDocumentPrinter) _printDirectly.Checked = false;

        _savePrinters.Enabled = _printers is not null;
        _testLabel.Enabled = _labelPrinter is not null;
    }

    private void SavePrinters()
    {
        if (_printers is null) return;

        var document = (_documentPrinter.SelectedItem as PrinterChoice)?.Name ?? string.Empty;
        var label = (_labelPrinterPicker.SelectedItem as PrinterChoice)?.Name ?? string.Empty;

        if (_printers.Save(document, _printDirectly.Checked, label))
        {
            _printersStatus.ForeColor = UiTheme.Normal;
            _printersStatus.Text = T("Printers.Saved");
        }
        else
        {
            _printersStatus.ForeColor = UiTheme.Critical;
            _printersStatus.Text = T("Printers.SaveFailed");
        }
    }

    private void PrintTestLabel()
    {
        if (_labelPrinter is null) return;

        // Saved first: the test must print on the printer shown, not on the one saved earlier.
        SavePrinters();

        var sample = new SampleLabel(
            Guid.Empty, "0000000000", "TEST ÉTIQUETTE", null,
            new DateOnly(2000, 1, 1), "0000-000000", "Test", "#888888",
            1, 1, ["TEST"], false, DateTimeOffset.Now);

        var result = _labelPrinter.Print([sample]);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("Printers.TestLabel"));
            return;
        }

        _printersStatus.ForeColor = UiTheme.Normal;
        _printersStatus.Text = T("Printers.TestSent", _labelPrinter.ConfiguredPrinter);
    }

    private sealed record PrinterChoice(string Name, string Display)
    {
        public override string ToString() => Display;
    }

    private void Field(Control parent, string key, int top, int left)
    {
        var label = UiTheme.FieldLabel(T(key));
        label.Font = UiTheme.SmallBold;
        label.Name = "lbl_" + key;
        label.Location = new Point(left, top);
        parent.Controls.Add(label);
    }

    private void WireBehaviour()
    {
        _sitePicker.SelectedIndexChanged += (_, _) => PopulateSite();

        _hasMinistryDate.CheckedChanged += (_, _) =>
            _ministryDate.Enabled = _hasMinistryDate.Checked;

        _accredited.CheckedChanged += (_, _) =>
        {
            _accreditationNumber.Enabled = _accredited.Checked;
            _accreditationUntil.Enabled = _accredited.Checked;
        };

        _saveSite.Click += async (_, _) => await SaveSiteAsync();
        _newSite.Click += async (_, _) => await CreateSiteAsync();
        _chooseLogo.Click += (_, _) => ChooseLogo();
        _removeLogo.Click += (_, _) =>
        {
            _pendingLogo = null;
            _logoRemoved = true;
            ShowLogo(null);
        };
        _reloadSite.Click += async (_, _) => await ActivateAsync();

        _sequences.SelectionChanged += (_, _) => RefreshSequenceActions();
        _editFormat.Click += async (_, _) => await EditFormatAsync();
        _toggleClosed.Click += async (_, _) => await ToggleClosedAsync();

        _settings.SelectionChanged += (_, _) => RefreshSettingActions();
        _editSetting.Click += async (_, _) => await EditSettingAsync();
        _addSetting.Click += async (_, _) => await AddSettingAsync();

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Administration.ManageSites, _saveSite, _chooseLogo, _removeLogo, _newSite);
        PermissionGate.Require(Permissions.Administration.ManageSettings, _editFormat, _toggleClosed, _editSetting, _addSetting);

        _sequences.CellFormatting += OnSequenceFormatting;
        _settings.CellFormatting += OnSettingFormatting;
    }

    private void OnSequenceFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _sequences.Rows.Count) return;
        if (_sequences.Rows[e.RowIndex].Tag is not SequenceRow row) return;

        if (row.IsClosed)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.HasBeenUsed)
        {
            // Not an error — just no longer editable. Muted so the editable rows stand out.
            e.CellStyle!.ForeColor = UiTheme.TextSecondary;
        }
        else
        {
            e.CellStyle!.ForeColor = UiTheme.Accent;
        }
    }

    private void OnSettingFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _settings.Rows.Count) return;
        if (_settings.Rows[e.RowIndex].Tag is not SettingRow row) return;

        if (row.IsSecuritySensitive)
        {
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
    }

    protected override void ApplyTexts()
    {
        _pageTitle.Text = T("Nav.Settings");
        _pageCaption.Text = T("AdminA.SettingsLead");
        _sequencesHeading.Text = T("Admin.Sequences");
        _settingsHeading.Text = T("Nav.Settings");

        _siteTab.Text = T("Site.Title");
        _numberingTab.Text = T("Admin.Sequences");
        _settingsTab.Text = T("Nav.Settings");
        _printersTab.Text = T("Printers.Title");

        _printersIntro.Text = T("Printers.Intro");
        _documentPrinterLabel.Text = T("Printers.Documents");
        _printDirectly.Text = T("Printers.PrintDirectly");
        _printDirectlyHint.Text = T("Printers.PrintDirectlyHint");
        _labelPrinterLabel.Text = T("Printers.Labels");
        _savePrinters.Text = T("Action.Save");
        _testLabel.Text = T("Printers.TestLabel");
        _reloadPrinters.Text = T("Action.Refresh");
        PopulatePrinters();

        SetText(_siteTab, "legalHeading", T("Site.LegalIdentity"));
        SetText(_siteTab, "authHeading", T("Site.Authorization"));
        SetText(_siteTab, "contactHeading", T("Patient.Contact"));

        _hasMinistryDate.Text = T("Site.MinistryAuthorizationDate");
        _accredited.Text = T("Site.IsAccredited");

        _saveSite.Text = T("Action.Save");
        _newSite.Text = T("Site.New");
        _chooseLogo.Text = T("Site.ChooseLogo");
        _removeLogo.Text = T("Site.RemoveLogo");
        _logoHint.Text = T("Site.LogoHint");
        _reloadSite.Text = T("Action.Refresh");

        _editFormat.Text = T("Admin.EditSequenceFormat");
        _toggleClosed.Text = T("Admin.CloseSequence");
        _numberingNotice.Text = T("Admin.SequenceFormatNotice");

        _editSetting.Text = T("Action.Edit");
        _addSetting.Text = T("Action.New");
        _settingsNotice.Text = T("Admin.SettingsNotice");

        foreach (var key in new[]
                 {
                     "Field.Code", "Field.NameFr", "Field.NameAr", "Site.Kind", "Site.LegalName",
                     "Site.Ice", "Site.FiscalId", "Site.ProfessionalTax",
                     "Site.CommercialRegister", "Site.CommercialRegisterCourt", "Site.Cnss",
                     "Site.Inpe", "Site.MinistryAuthorization", "Site.ResponsibleBiologist",
                     "Site.AccreditationNumber", "Site.AccreditationValidUntil",
                     "Patient.Address", "Field.DescriptionAr", "Patient.City", "Patient.Region",
                     "Patient.PostalCode", "Site.Phone", "Site.Fax", "Field.Email", "Site.Website",
                     "Site.Logo"
                 })
        {
            SetText(_siteTab, "lbl_" + key, T(key));
        }

        if (_sequences.Columns.Count > 0)
        {
            _sequences.Column("purpose").HeaderText = T("Admin.SequencePurpose");
            _sequences.Column("site").HeaderText = T("App.Site");
            _sequences.Column("year").HeaderText = T("Field.Date");
            _sequences.Column("format").HeaderText = T("Admin.SequenceFormat");
            _sequences.Column("preview").HeaderText = T("Admin.SequencePreview");
            _sequences.Column("next").HeaderText = T("Admin.SequenceNext");
            _sequences.Column("used").HeaderText = T("Admin.SequenceLastUsed");
            _sequences.Column("state").HeaderText = T("Field.Status");
        }

        if (_settings.Columns.Count > 0)
        {
            _settings.Column("key").HeaderText = T("Admin.SettingKey");
            _settings.Column("value").HeaderText = T("Admin.SettingValue");
            _settings.Column("site").HeaderText = T("App.Site");
            _settings.Column("description").HeaderText = T("Field.Description");
            _settings.Column("sensitive").HeaderText = T("Admin.SettingSensitive");
        }

        if (_sites.Count > 0) PopulateSite();
        RenderSequences();
        RenderSettings();
    }

    private static void SetText(Control parent, string name, string text)
    {
        var found = parent.Controls.Find(name, searchAllChildren: true);
        if (found.Length > 0) found[0].Text = text;
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var users = await _administration.GetUsersAsync(includeInactive: false, ct: ct);

            if (users.IsSuccess)
            {
                // Only a qualified biologist may be named responsible for the site: loi n° 12-01
                // reserves the direction of a medical biology laboratory to one, and the service
                // refuses anyone else.
                _biologists = [.. users.Value.Where(u => u.QualifiedToValidate)];
            }

            var sites = await _configuration.GetSitesAsync(ct);

            if (sites.IsFailure)
            {
                _dialogs.ShowFailure(sites);
                return;
            }

            _sites = sites.Value;

            var selected = _selectAfterLoad ?? (_sitePicker.SelectedItem as SiteChoice)?.Id;
            _selectAfterLoad = null;

            _sitePicker.Items.Clear();
            foreach (var site in _sites)
                _sitePicker.Items.Add(new SiteChoice(site.Id, SiteDisplay(site)));

            if (_sitePicker.Items.Count > 0)
            {
                var index = selected is { } id
                    ? _sitePicker.Items.Cast<SiteChoice>().ToList().FindIndex(c => c.Id == id)
                    : 0;

                _sitePicker.SelectedIndex = Math.Max(0, index);
            }

            await LoadSequencesAsync(ct);
            await LoadSettingsAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the configuration failed.");
            _siteStatus.ForeColor = UiTheme.Critical;
            _siteStatus.Text = T("Message.UnexpectedError");
        }
    }

    /// <summary>
    /// The code, the legal name (the site's name while it has none, as a site just created) and
    /// the kind, so a partner laboratory is not mistaken for one of the laboratory's own sites.
    /// </summary>
    private string SiteDisplay(SiteDetail site) =>
        $"{site.Code} — {(string.IsNullOrWhiteSpace(site.LegalName) ? site.Name.Fr : site.LegalName)}"
        + $" ({T($"LabSiteKind.{site.Kind}")})";

    private SiteDetail? Current =>
        _sitePicker.SelectedItem is SiteChoice choice
            ? _sites.FirstOrDefault(s => s.Id == choice.Id)
            : null;

    private void PopulateSite()
    {
        if (Current is not { } site) return;

        _code.Text = site.Code;
        _nameFr.Text = site.Name.Fr;
        _nameAr.Text = site.Name.Ar ?? string.Empty;
        Select(_kind, (int)site.Kind);
        _legalName.Text = site.LegalName;
        _ice.Text = site.Ice ?? string.Empty;
        _fiscalId.Text = site.FiscalId ?? string.Empty;
        _rc.Text = site.CommercialRegisterNumber ?? string.Empty;
        _rcCourt.Text = site.CommercialRegisterCourt ?? string.Empty;
        _taxNumber.Text = site.ProfessionalTaxNumber ?? string.Empty;
        _cnss.Text = site.CnssAffiliationNumber ?? string.Empty;
        _inpe.Text = site.Inpe ?? string.Empty;
        _ministryAuth.Text = site.HealthMinistryAuthorizationNumber ?? string.Empty;

        if (site.HealthMinistryAuthorizationDate is { } authDate)
        {
            _hasMinistryDate.Checked = true;
            _ministryDate.Value = authDate.ToDateTime(TimeOnly.MinValue);
        }
        else
        {
            _hasMinistryDate.Checked = false;
        }

        _responsible.Items.Clear();
        _responsible.Items.Add(new SiteChoice(Guid.Empty, "—"));
        foreach (var b in _biologists)
            _responsible.Items.Add(new SiteChoice(b.UserId, $"{b.FullName} ({b.ProfessionalOrderNumber})"));

        var responsibleIndex = site.ResponsibleBiologistUserId is { } rid
            ? _responsible.Items.Cast<SiteChoice>().ToList().FindIndex(c => c.Id == rid)
            : 0;

        _responsible.SelectedIndex = Math.Max(0, responsibleIndex);

        _accredited.Checked = site.IsAccredited;
        _accreditationNumber.Text = site.AccreditationNumber ?? string.Empty;
        if (site.AccreditationValidUntil is { } until)
            _accreditationUntil.Value = until.ToDateTime(TimeOnly.MinValue);

        _addressFr.Text = site.AddressLine?.Fr ?? string.Empty;
        _addressAr.Text = site.AddressLine?.Ar ?? string.Empty;
        _city.Text = site.City ?? string.Empty;
        Select(_region, (int)site.Region);
        _postalCode.Text = site.PostalCode ?? string.Empty;
        _phone.Text = site.Phone ?? string.Empty;
        _fax.Text = site.Fax ?? string.Empty;
        _email.Text = site.Email ?? string.Empty;
        _website.Text = site.Website ?? string.Empty;

        _pendingLogo = null;
        _logoRemoved = false;
        ShowLogo(site.LogoPng);

        // The site code is a prefix inside accession and invoice numbers, so once documents exist
        // it is frozen — renaming it would split one series into two that no longer sort together.
        _code.Enabled = true;

        RenderBanners(site.InvoicingBlockers, site.OperationBlockers);
    }

    private void ChooseLogo()
    {
        using var dialog = new OpenFileDialog
        {
            Title = T("Site.ChooseLogo"),
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif"
        };

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        var png = LogoImage.ToPng(dialog.FileName);

        if (png is null)
        {
            _dialogs.Warn(T("Site.LogoInvalid"), T("Site.Logo"));
            return;
        }

        _pendingLogo = png;
        _logoRemoved = false;
        ShowLogo(png);
    }

    private void ShowLogo(byte[]? png)
    {
        var previous = _logo.Image;
        _logo.Image = png is { Length: > 0 } ? LogoImage.Load(png) : null;
        previous?.Dispose();
        _removeLogo.Enabled = _logo.Image is not null;
    }

    /// <summary>
    /// Names what is still blocking invoicing and report release, by resource key, so the
    /// administrator can see the remaining work rather than discovering it one refusal at a time.
    /// </summary>
    private void RenderBanners(IReadOnlyList<string> invoicing, IReadOnlyList<string> operation)
    {
        // A partner laboratory neither invoices nor releases reports here: only its identity and
        // contact details are used, on the dispatch slips. Its blockers would only be noise.
        if ((_kind.SelectedItem as EnumChoice)?.Value == (int)LabSiteKind.ReferralPartner)
        {
            _invoicingBanner.ForeColor = UiTheme.TextSecondary;
            _invoicingBanner.Text = T("Site.PartnerNotice");
            _operationBanner.Text = string.Empty;
            return;
        }

        if (invoicing.Count == 0)
        {
            _invoicingBanner.ForeColor = UiTheme.Normal;
            _invoicingBanner.Text = "✓ " + T("Site.InvoicingReady");
        }
        else
        {
            _invoicingBanner.ForeColor = UiTheme.Critical;
            _invoicingBanner.Text = "⚠ " + T("Site.InvoicingBlocked") + " — "
                                    + string.Join("   ·   ", invoicing.Select(T));
        }

        if (operation.Count == 0)
        {
            _operationBanner.ForeColor = UiTheme.Normal;
            _operationBanner.Text = "✓ " + T("Site.ReportingReady");
        }
        else
        {
            _operationBanner.ForeColor = UiTheme.Critical;
            _operationBanner.Text = "⚠ " + T("Site.ReportingBlocked") + " — "
                                    + string.Join("   ·   ", operation.Select(T));
        }
    }

    private static void Select(ComboBox combo, int value)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is EnumChoice c && c.Value == value) { combo.SelectedIndex = i; return; }
        }
    }

    // ---- Site save -----------------------------------------------------------------------------------

    private async Task SaveSiteAsync()
    {
        if (Current is not { } site) return;

        var responsible = (_responsible.SelectedItem as SiteChoice)?.Id;

        var request = new SiteRequest
        {
            SiteId = site.Id,
            Code = _code.Text,
            Name = new MultilingualText(_nameFr.Text.Trim(), _nameAr.Text.Trim(), site.Name.En),
            Kind = (LabSiteKind)((_kind.SelectedItem as EnumChoice)?.Value ?? 0),
            LegalName = _legalName.Text,
            Ice = _ice.Text,
            FiscalId = _fiscalId.Text,
            CommercialRegisterNumber = _rc.Text,
            CommercialRegisterCourt = _rcCourt.Text,
            ProfessionalTaxNumber = _taxNumber.Text,
            CnssAffiliationNumber = _cnss.Text,
            Inpe = _inpe.Text,
            HealthMinistryAuthorizationNumber = _ministryAuth.Text,
            HealthMinistryAuthorizationDate = _hasMinistryDate.Checked
                ? DateOnly.FromDateTime(_ministryDate.Value.Date) : null,
            IsAccredited = _accredited.Checked,
            AccreditationNumber = _accreditationNumber.Text,
            AccreditationValidUntil = _accredited.Checked
                ? DateOnly.FromDateTime(_accreditationUntil.Value.Date) : null,
            ResponsibleBiologistUserId = responsible == Guid.Empty ? null : responsible,
            AddressLine = new MultilingualText(_addressFr.Text.Trim(), _addressAr.Text.Trim(),
                                                site.AddressLine?.En),
            City = _city.Text,
            Region = (MoroccanRegion)((_region.SelectedItem as EnumChoice)?.Value ?? 0),
            PostalCode = _postalCode.Text,
            Phone = _phone.Text,
            Fax = _fax.Text,
            Email = _email.Text,
            Website = _website.Text,
            LogoPng = _pendingLogo,
            RemoveLogo = _logoRemoved && _pendingLogo is null,
            IsActive = site.IsActive
        };

        var result = await _configuration.SaveSiteAsync(request);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("Site.Title"));
            return;
        }

        var outcome = result.Value;

        RenderBanners(outcome.InvoicingBlockers, outcome.OperationBlockers);

        _siteStatus.ForeColor = outcome.CanInvoice && outcome.CanReleaseReports
            ? UiTheme.Normal : UiTheme.Abnormal;

        _siteStatus.Text = outcome.CanInvoice && outcome.CanReleaseReports
            ? T("Site.ConfigurationComplete")
            : T("Message.Saved");

        await ActivateAsync();
    }

    // ---- Site creation -------------------------------------------------------------------------------

    /// <summary>
    /// Asks for what a site cannot exist without — code, name, kind — creates it, and selects it so
    /// the rest of its identity is filled in with the form. Asked again with the same values when
    /// refused, so a taken code costs one correction rather than retyping everything.
    /// </summary>
    private async Task CreateSiteAsync()
    {
        string? code = null, nameFr = null, nameAr = null;
        var kind = LabSiteKind.ReferralPartner;

        while (true)
        {
            using var dialog = new FieldDialog(Localizer, T("Site.New"), height: 400);
            dialog.Note(T("Site.NewHelp"));
            var codeBox = dialog.TextField(T("Field.Code"), code, ConfigurationService.MaxSiteCodeLength);
            codeBox.CharacterCasing = CharacterCasing.Upper;
            codeBox.Tag = UiTags.LatinValue;
            var frBox = dialog.TextField(T("Field.NameFr"), nameFr);
            var arBox = dialog.TextField(T("Field.NameAr"), nameAr);
            arBox.RightToLeft = RightToLeft.Yes;
            var kindBox = dialog.Choice(T("Site.Kind"),
                Enum.GetValues<LabSiteKind>().Select(k => (k, T($"LabSiteKind.{k}"))), kind);

            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            code = codeBox.Text.Trim();
            nameFr = frBox.Text.Trim();
            nameAr = arBox.Text.Trim();
            kind = FieldDialog.Selected<LabSiteKind>(kindBox);

            var result = await _configuration.CreateSiteAsync(new NewSiteRequest
            {
                Code = code,
                Name = new MultilingualText(nameFr, nameAr.Length == 0 ? null : nameAr),
                Kind = kind
            });

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result, T("Site.New"));
                continue;
            }

            _selectAfterLoad = result.Value;
            await ActivateAsync();

            _siteStatus.ForeColor = UiTheme.Normal;
            _siteStatus.Text = T("Site.Created", code.ToUpperInvariant());
            return;
        }
    }

    // ---- Numbering -----------------------------------------------------------------------------------

    private async Task LoadSequencesAsync(CancellationToken ct = default)
    {
        var result = await _configuration.GetSequencesAsync(ct);
        if (result.IsFailure) return;

        _sequenceRows = result.Value;
        RenderSequences();
    }

    private void RenderSequences()
    {
        if (_sequences.Columns.Count == 0) return;

        _sequences.Rows.Clear();

        foreach (var row in _sequenceRows)
        {
            var index = _sequences.Rows.Add(
                T($"SequencePurpose.{row.Purpose}"),
                row.SiteCode ?? "—",
                row.Year?.ToString() ?? "—",
                row.Format,
                row.Preview,
                row.NextValue.ToString("N0"),
                row.HasBeenUsed ? row.LastAllocatedValue.ToString("N0") : "—",
                row.IsClosed ? T("Admin.SequenceClosed")
                             : row.HasBeenUsed ? T("Admin.SequenceInUse")
                                               : T("Admin.SequenceEditable"));

            _sequences.Rows[index].Tag = row;
        }

        RefreshSequenceActions();
    }

    private void RefreshSequenceActions()
    {
        var row = _sequences.CurrentRow?.Tag as SequenceRow;

        // Editable only before the first number is handed out. After that the DGI's requirement
        // for an unbroken chronological series makes a format change unreconcilable.
        _editFormat.Enabled = row is { HasBeenUsed: false, IsClosed: false };
        _toggleClosed.Enabled = row is not null;

        _toggleClosed.Text = row is { IsClosed: true }
            ? T("Admin.ReopenSequence")
            : T("Admin.CloseSequence");
    }

    private async Task EditFormatAsync()
    {
        if (_sequences.CurrentRow?.Tag is not SequenceRow row) return;

        var answer = _dialogs.PromptForText(
            T("Admin.SequenceFormatPrompt", T($"SequencePurpose.{row.Purpose}")),
            T("Admin.EditSequenceFormat"), row.Format);

        if (string.IsNullOrWhiteSpace(answer) || answer.Trim() == row.Format) return;

        var result = await _configuration.SetSequenceFormatAsync(row.SequenceId, answer.Trim());

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("Admin.EditSequenceFormat"));
            return;
        }

        await LoadSequencesAsync();
    }

    private async Task ToggleClosedAsync()
    {
        if (_sequences.CurrentRow?.Tag is not SequenceRow row) return;

        var closing = !row.IsClosed;

        var reason = _dialogs.PromptForReason(
            closing
                ? T("Admin.CloseSequencePrompt", T($"SequencePurpose.{row.Purpose}"))
                : T("Admin.ReopenSequencePrompt", T($"SequencePurpose.{row.Purpose}")),
            closing ? T("Admin.CloseSequence") : T("Admin.ReopenSequence"));

        if (reason is null) return;

        var result = await _configuration.SetSequenceClosedAsync(row.SequenceId, closing, reason);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        await LoadSequencesAsync();
    }

    // ---- Settings ------------------------------------------------------------------------------------

    private async Task LoadSettingsAsync(CancellationToken ct = default)
    {
        var result = await _configuration.GetSettingsAsync(ct);
        if (result.IsFailure) return;

        _settingRows = result.Value;
        RenderSettings();
    }

    private void RenderSettings()
    {
        if (_settings.Columns.Count == 0) return;

        _settings.Rows.Clear();

        foreach (var row in _settingRows)
        {
            var index = _settings.Rows.Add(
                row.Key,
                // A sensitive value is not shown: some of these hold credentials, and this screen
                // is used at a shared administrative workstation.
                row.IsSecuritySensitive ? "••••••••" : row.Value ?? string.Empty,
                row.SiteId is null ? "—" : _sites.FirstOrDefault(s => s.Id == row.SiteId)?.Code ?? "?",
                row.Description ?? string.Empty,
                row.IsSecuritySensitive ? T("Admin.SettingSensitive") : string.Empty);

            _settings.Rows[index].Tag = row;
        }

        RefreshSettingActions();
    }

    private void RefreshSettingActions() =>
        _editSetting.Enabled = _settings.CurrentRow?.Tag is SettingRow;

    private async Task EditSettingAsync()
    {
        if (_settings.CurrentRow?.Tag is not SettingRow row) return;

        var answer = _dialogs.PromptForText(
            T("Admin.SettingValuePrompt", row.Key),
            T("Action.Edit"), row.IsSecuritySensitive ? string.Empty : row.Value);

        if (answer is null) return;

        var result = await _configuration.SetSettingAsync(row.Key, answer, row.SiteId);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        await LoadSettingsAsync();
    }

    private async Task AddSettingAsync()
    {
        var key = _dialogs.PromptForText(T("Admin.SettingKeyPrompt"), T("Action.New"));
        if (string.IsNullOrWhiteSpace(key)) return;

        var value = _dialogs.PromptForText(T("Admin.SettingValuePrompt", key.Trim()),
                                            T("Action.New"));
        if (value is null) return;

        var result = await _configuration.SetSettingAsync(key.Trim(), value);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        await LoadSettingsAsync();
    }

    private sealed record SiteChoice(Guid Id, string Display)
    {
        public override string ToString() => Display;
    }
}
