using System.Globalization;
using MarocLIS.Application.Catalog;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Patients;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Creating or editing an analysis.
/// <para>
/// The code is entered once and then read-only. It is the join between the catalogue, the
/// instrument mappings, the worklists and every result ever stored, so renaming it would detach all
/// of them silently — the domain keeps the setter private and this dialog says why rather than
/// leaving the operator to wonder whether the field is broken.
/// </para>
/// </summary>
internal sealed class AnalysisEditorDialog : MasterDataEditor
{
    private readonly Guid? _id;
    private readonly bool _isNew;
    private readonly AnalysisRequest? _stored;

    private readonly TextBox _code, _nameFr, _nameAr, _nameEn, _shortName, _aliases,
                             _loinc, _actCode, _methodFr, _reagent, _volume, _clinicalInfo;
    private readonly ComboBox _department, _specimen, _container, _consent, _subcontractor;
    private readonly NumericUpDown _routine, _urgent, _fasting, _stabilityRoom, _stabilityFridge,
                                    _displayOrder;
    private readonly CheckBox _accredited, _subcontracted, _urgentAvailable, _notifiable, _active;
    private readonly TextBox _letterKey, _coefficient, _letterKeyValue, _price;
    private readonly Label _computed;
    private readonly TariffRow? _labTariff;
    private readonly bool _canPrice;
    private readonly int _partnerSiteCount;

    // The national nomenclature (NABM): the act is picked from it or typed, and said back here.
    private readonly IReadOnlyList<NationalActRow> _acts;
    private readonly Button _pickAct;
    private readonly Label _actInfo;

    internal AnalysisRequest? Request { get; private set; }

    /// <summary>
    /// The laboratory price-list tariff as entered, or null when the price was left alone (or the
    /// operator may not set prices). The analysis id is filled in by the caller once it is saved.
    /// </summary>
    internal TariffRequest? PriceRequest { get; private set; }

    /// <param name="labTariff">The laboratory price-list tariff in force, if any.</param>
    /// <param name="knownLetterKeyValue">The latest letter-key value known, to prefill a first tariff.</param>
    /// <param name="canPrice">Whether the operator may set tariffs; the price fields are read-only otherwise.</param>
    /// <param name="stored">
    /// The whole stored analysis when editing. The fields are filled from it, and the request saved
    /// is built on it, so whatever the dialog does not show is carried through as stored.
    /// </param>
    /// <param name="acts">
    /// The acts of the national nomenclature, to pick the act code from; empty when they could not
    /// be read, and the code is then typed as before, without the check.
    /// </param>
    internal AnalysisEditorDialog(ILocalizer localizer, IUserDialogs dialogs,
                                  CatalogLookups lookups, AnalysisRow? existing,
                                  TariffRow? labTariff = null, decimal? knownLetterKeyValue = null,
                                  bool canPrice = false, AnalysisRequest? stored = null,
                                  IReadOnlyList<NationalActRow>? acts = null)
        : base(localizer, dialogs, existing is null ? "Catalog.NewAnalysis" : "Catalog.EditAnalysis",
               existing?.Code, 760, 764)
    {
        _labTariff = labTariff;
        _canPrice = canPrice;
        _acts = acts ?? [];
        _id = existing?.Id;
        _isNew = existing is null;
        _stored = existing is null ? null : stored;
        _partnerSiteCount = lookups.PartnerSites.Count;

        var language = localizer.CurrentLanguage;
        var form = CatalogEditorRules.FormOf(existing, stored, labTariff, knownLetterKeyValue, CultureInfo.CurrentCulture);

        // --- identity --------------------------------------------------------------------------

        _code = TextField("Field.Code", 20, 16, 140, form.Code, latin: true, mono: true);
        _code.ReadOnly = !_isNew;
        if (!_isNew) _code.BackColor = UiTheme.SurfaceSunken;
        _code.CharacterCasing = CharacterCasing.Upper;
        _code.MaxLength = CatalogEditorRules.CodeMaxLength;

        Notice(170, 34, 560, 26,
               _isNew ? "Catalog.CodeSetOnce" : "Catalog.CodeImmutableNotice",
               _isNew ? UiTheme.TextSecondary : UiTheme.Abnormal);

        _nameFr = TextField("Field.NameFr", 20, 60, 340, form.NameFr);
        _nameAr = TextField("Field.NameAr", 372, 60, 340, form.NameAr, arabic: true);
        _nameEn = TextField("Field.NameEn", 20, 104, 340, form.NameEn);
        _shortName = TextField("Catalog.ShortName", 372, 104, 160, form.ShortName);
        _aliases = TextField("Catalog.SearchAliases", 20, 148, 692, form.Aliases);
        // The lengths of the columns: a longer value was refused by the database with an unexpected
        // error, and the form was lost with it.
        _nameFr.MaxLength = _nameAr.MaxLength = _nameEn.MaxLength = CatalogEditorRules.NameMaxLength;
        _shortName.MaxLength = CatalogEditorRules.ShortNameMaxLength;
        _aliases.MaxLength = CatalogEditorRules.AliasesMaxLength;

        // --- classification --------------------------------------------------------------------

        _department = Lookup("Field.Department", 20, 192, 220,
                             lookups.Departments.Where(d => d.IsActive)
                                    .Select(d => (d.Id, d.Name.For(language))),
                             form.DepartmentId, allowNone: false);

        _specimen = Lookup("Sample.SpecimenType", 252, 192, 220,
                           lookups.SpecimenTypes.Where(s => s.IsActive)
                                  .Select(s => (s.Id, s.Name.For(language))),
                           form.SpecimenTypeId, allowNone: false);

        _container = Lookup("Sample.ContainerType", 484, 192, 228,
                            lookups.ContainerTypes.Where(c => c.IsActive)
                                   .Select(c => (c.Id, c.Name.For(language))),
                            form.ContainerTypeId);

        _volume = TextField("Catalog.RequiredVolume", 20, 236, 120, form.Volume,
                            latin: true, right: true);
        Numeric(EditorValues.MeasureMaxLength, _volume);
        _loinc = TextField("Catalog.Loinc", 152, 236, 160, form.Loinc, latin: true, mono: true);
        // The national act is a second code beside the laboratory's own: picked from the
        // nomenclature (the button, or F4), or typed. It never replaces the internal code above.
        _actCode = TextField("Catalog.NationalActCode", 324, 236, 120, form.ActCode,
                        latin: true, mono: true);
        _loinc.MaxLength = CatalogEditorRules.LoincMaxLength;
        _actCode.MaxLength = CatalogEditorRules.CodeMaxLength;
        _actCode.CharacterCasing = CharacterCasing.Upper;

        _pickAct = UiTheme.SecondaryButton("…");
        // 36 px: the ellipsis needs 33 px in the button font, and 28 px cut it.
        _pickAct.Bounds = new Rectangle(448, 254, 36, UiTheme.InputHeight);
        _pickAct.Padding = Padding.Empty;
        _pickAct.Tag = UiTags.NoMnemonic;
        _pickAct.AccessibleName = localizer["Nabm.PickAct"];
        _pickActTip.SetToolTip(_pickAct, localizer["Nabm.PickAct"]);
        _pickAct.Enabled = _acts.Count > 0;
        _pickAct.Click += (_, _) => PickAct();
        Controls.Add(_pickAct);

        _actCode.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.F4 || _acts.Count == 0) return;
            e.SuppressKeyPress = true;
            PickAct();
        };

        _consent = Choice("Catalog.ConsentRequired", 496, 236, 216,
                          form.Consent, "ConsentRequirement");

        // --- method ----------------------------------------------------------------------------

        _methodFr = TextField("Catalog.Method", 20, 280, 340, form.Method);
        _reagent = TextField("Catalog.ReagentReference", 372, 280, 340, form.Reagent);
        _methodFr.MaxLength = _reagent.MaxLength = CatalogEditorRules.MethodMaxLength;

        _accredited = Check("Catalog.Accredited", 20, 334, form.Accredited);
        _subcontracted = Check("Catalog.Subcontracted", 20, 358, form.Subcontracted);
        _notifiable = Check("Catalog.Notifiable", 20, 382, form.Notifiable);

        _subcontractor = Lookup("Catalog.SubcontractorSite", 372, 334, 340,
                                lookups.PartnerSites.Select(s => (s.Id, $"{s.Code}  {s.Name.For(language)}")),
                                form.SubcontractorSiteId);

        // The picker follows the tick box: an analysis marked subcontracted with no partner site is
        // refused by the domain, and a laboratory with no partner configured needs to be told that
        // rather than left to guess which field the refusal is about.
        _subcontracted.CheckedChanged += (_, _) => RefreshSubcontracting();

        // --- turnaround ------------------------------------------------------------------------

        _routine = Number("Catalog.RoutineTurnaround", 20, 412, 140,
                          form.RoutineTurnaroundHours, max: CatalogEditorRules.TurnaroundHoursMax);

        _urgent = Number("Catalog.UrgentTurnaround", 180, 412, 140,
                         form.UrgentTurnaroundMinutes, max: CatalogEditorRules.UrgentMinutesMax);

        _urgentAvailable = Check("Catalog.AvailableUrgently", 340, 432, form.AvailableUrgently);

        Notice(20, 460, 692, 20, "Catalog.TurnaroundNotice");

        // --- pre-analytical --------------------------------------------------------------------

        _fasting = Number("Catalog.FastingOverride", 20, 488, 140,
                          form.FastingHours, max: CatalogEditorRules.FastingHoursMax);
        _stabilityRoom = Number("Catalog.StabilityRoom", 180, 488, 140,
                                form.StabilityRoomHours, max: CatalogEditorRules.StabilityHoursMax);
        _stabilityFridge = Number("Catalog.StabilityFridge", 340, 488, 140,
                                  form.StabilityFridgeHours, max: CatalogEditorRules.StabilityHoursMax);
        _displayOrder = Number("Field.DisplayOrder", 500, 488, 120, form.DisplayOrder,
                               max: CatalogEditorRules.DisplayOrderMax);

        _clinicalInfo = TextField("Catalog.RequiredClinicalInformation", 20, 540, 692,
                                  form.ClinicalInformation);
        _clinicalInfo.MaxLength = CatalogEditorRules.ClinicalInformationMaxLength;

        // --- price ------------------------------------------------------------------------------
        // The laboratory's own price list (patients paying themselves, and the fallback for any
        // payer without a tariff of its own). Payer tariffs stay on the Tarifs tab.

        Notice(20, 588, 692, 20, "Catalog.LabPriceSection", UiTheme.Accent);

        _letterKey = TextField("Billing.LetterKey", 20, 612, 70, form.LetterKey,
                               latin: true, mono: true);
        _letterKey.MaxLength = CatalogEditorRules.LetterKeyMaxLength;
        _letterKey.CharacterCasing = CharacterCasing.Upper;

        _coefficient = TextField("Billing.Coefficient", 102, 612, 100, form.Coefficient,
                                 latin: true, right: true, mono: true);
        Numeric(EditorValues.MeasureMaxLength, _coefficient);

        _letterKeyValue = TextField("Catalog.LetterKeyValue", 214, 612, 120, form.LetterKeyValue,
                                    latin: true, right: true, mono: true);
        Numeric(EditorValues.MeasureMaxLength, _letterKeyValue);

        _price = TextField("Catalog.LabPrice", 346, 612, 130, form.Price,
                           latin: true, right: true, mono: true);
        Numeric(EditorValues.AmountMaxLength, _price);

        _computed = Notice(488, 626, 224, 44, "Catalog.TariffComputedNotice", UiTheme.Accent);

        foreach (var box in new[] { _letterKey, _coefficient, _letterKeyValue, _price })
        {
            box.TextChanged += (_, _) => RefreshComputed();

            if (canPrice) continue;
            box.ReadOnly = true;
            box.BackColor = UiTheme.SurfaceSunken;
        }

        RefreshComputed();

        _active = Check("Field.Active", 20, 670, form.Active, bold: true);

        _actInfo = Notice(140, 672, 572, 20, "Catalog.NationalActCode");
        _actCode.TextChanged += (_, _) => RefreshActInfo();
        RefreshActInfo();

        // Editing opens on the whole stored analysis (see CatalogMaintenanceService
        // .GetAnalysisForEditAsync), not on the grid row: the row is a summary, and an editor
        // filled from it saved blanks over the LOINC code, the stabilities and the partner site.

        RefreshSubcontracting();

        Buttons(710, 760);
        UseAdminFrame();
    }

    private readonly ToolTip _pickActTip = new();

    /// <summary>The analysis as typed, for the rules shared with the web.</summary>
    private AnalysisForm Form() => new()
    {
        Code = _code.Text,
        NameFr = _nameFr.Text,
        NameAr = _nameAr.Text,
        NameEn = _nameEn.Text,
        ShortName = _shortName.Text,
        Aliases = _aliases.Text,
        DepartmentId = SelectedId(_department),
        SpecimenTypeId = SelectedId(_specimen),
        ContainerTypeId = SelectedId(_container),
        Volume = _volume.Text,
        Loinc = _loinc.Text,
        ActCode = _actCode.Text,
        Consent = Selected<ConsentRequirement>(_consent),
        Method = _methodFr.Text,
        Reagent = _reagent.Text,
        Accredited = _accredited.Checked,
        Subcontracted = _subcontracted.Checked,
        Notifiable = _notifiable.Checked,
        SubcontractorSiteId = SelectedId(_subcontractor),
        RoutineTurnaroundHours = (int)_routine.Value,
        UrgentTurnaroundMinutes = (int)_urgent.Value,
        AvailableUrgently = _urgentAvailable.Checked,
        FastingHours = (int)_fasting.Value,
        StabilityRoomHours = (int)_stabilityRoom.Value,
        StabilityFridgeHours = (int)_stabilityFridge.Value,
        DisplayOrder = (int)_displayOrder.Value,
        ClinicalInformation = _clinicalInfo.Text,
        Active = _active.Checked,
        LetterKey = _letterKey.Text,
        Coefficient = _coefficient.Text,
        LetterKeyValue = _letterKeyValue.Text,
        Price = _price.Text
    };

    /// <summary>Says which act the code is, with its official coefficient, or that the list does not know it.</summary>
    private void RefreshActInfo()
    {
        var info = CatalogEditorRules.Act(_actCode.Text, _acts);

        (_actInfo.Text, _actInfo.ForeColor) = info switch
        {
            { Kind: ActInfoKind.Known, Act: { } act } =>
                (Localizer.Format("Nabm.ActInfo", act.Code, act.Label, act.Coefficient.ToString("0.##")), UiTheme.Accent),
            { Kind: ActInfoKind.Unknown } => (Localizer.Format("Nabm.CodeNotInList", info.Code!), UiTheme.Abnormal),
            { Kind: ActInfoKind.Hint } => (Localizer["Nabm.NoActHint"], UiTheme.TextSecondary),
            _ => (string.Empty, UiTheme.TextSecondary)
        };
    }

    /// <summary>
    /// Picks the act from the nomenclature: fills the act code and proposes the official coefficient
    /// for the laboratory price (<see cref="CatalogEditorRules.WithAct"/>).
    /// </summary>
    private void PickAct()
    {
        using var picker = new NationalActPickerDialog(Localizer, Dialogs, _acts, _actCode.Text);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Chosen is not { } act) return;

        var picked = CatalogEditorRules.WithAct(Form(), act, _canPrice, CultureInfo.CurrentCulture);

        _actCode.Text = picked.ActCode;
        _letterKey.Text = picked.LetterKey;
        _coefficient.Text = picked.Coefficient;
        _price.Text = picked.Price;

        RefreshComputed();
    }

    private void RefreshComputed()
    {
        var (reference, excess) = CatalogEditorRules.Computed(_coefficient.Text, _letterKeyValue.Text, _price.Text);
        var language = Localizer.CurrentLanguage;

        _computed.ForeColor = excess > 0m ? UiTheme.Abnormal : UiTheme.Accent;
        _computed.Text = Localizer.Format("Catalog.TariffComputedNotice",
                                          Money.Of(reference).Format(language),
                                          Money.Of(excess).Format(language));
    }

    private void RefreshSubcontracting()
    {
        _subcontractor.Enabled = CatalogEditorRules.SubcontractorEnabled(Form(), _partnerSiteCount);

        _subcontractor.BackColor = _subcontractor.Enabled
            ? UiTheme.SurfaceRaised
            : UiTheme.SurfaceSunken;
    }

    protected override void Accept()
    {
        var form = Form();

        if (Refused(CatalogEditorRules.Check(form, _partnerSiteCount),
                    ("volume", _volume), ("coefficient", _coefficient), ("letterkeyvalue", _letterKeyValue),
                    ("price", _price), ("code", _code), ("namefr", _nameFr), ("subcontractor", _subcontractor)))
            return;

        // A code typed by hand that the nomenclature does not know: said before saving; the
        // operator may still keep it.
        if (CatalogEditorRules.UnknownActCode(form, _acts) is { } unknown
            && !Dialogs.Confirm(Localizer.Format("Nabm.CodeNotInListConfirm", unknown), Text))
        {
            _actCode.Focus();
            return;
        }

        var (refusal, priceRequest) = CatalogEditorRules.ReadPrice(form, _canPrice, _labTariff);
        if (Refused(refusal, ("price", _price), ("letterkey", _letterKey))) return;

        PriceRequest = priceRequest;
        Request = CatalogEditorRules.ToRequest(form, _id, _stored);

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Creating or editing a parameter, with its allowed-answer list.
/// <para>
/// The value type governs which of these fields mean anything, so the irrelevant ones are disabled
/// as it changes rather than left to be filled in and ignored. It is also frozen once a result
/// exists, and the notice says so with the count: an operator who cannot change it deserves to
/// know that the reason is a hundred stored results, not a bug.
/// </para>
/// </summary>
internal sealed class ParameterEditorDialog : MasterDataEditor
{
    private readonly Guid? _id;
    private readonly Guid _analysisId;
    private readonly bool _isNew;
    private readonly IReadOnlyList<ParameterRow> _siblings;
    private readonly ParameterRequest? _stored;

    private readonly TextBox _code, _nameFr, _nameAr, _nameEn, _shortName, _loinc,
                             _measuringLow, _measuringHigh, _technicalLow, _technicalHigh,
                             _criticalLow, _criticalHigh, _cv, _delta, _formula;
    private readonly ComboBox _valueType, _unit;
    private readonly NumericUpDown _decimals, _deltaWindow, _displayOrder, _indent;
    private readonly CheckBox _internalOnly, _alwaysShow, _active;
    private readonly DataGridView _options = UiTheme.Grid();
    private readonly Label _typeNotice, _formulaNotice;
    private readonly Button _addOption, _removeOption;

    internal ParameterRequest? Request { get; private set; }

    /// <param name="stored">
    /// The whole stored parameter when editing, allowed answers included. The fields and the answer
    /// list are filled from it, and the request saved is built on it.
    /// </param>
    internal ParameterEditorDialog(ILocalizer localizer, IUserDialogs dialogs,
                                   CatalogLookups lookups, AnalysisRow analysis,
                                   ParameterRow? existing, IReadOnlyList<ParameterRow> siblings,
                                   ParameterRequest? stored = null)
        : base(localizer, dialogs,
               existing is null ? "Catalog.NewParameter" : "Catalog.EditParameter",
               $"{analysis.Code} {(existing is null ? string.Empty : "/ " + existing.Code)}".Trim(),
               780, 680)
    {
        _id = existing?.Id;
        _analysisId = analysis.Id;
        _isNew = existing is null;
        _siblings = siblings;
        _stored = existing is null ? null : stored;

        var form = CatalogEditorRules.FormOf(existing, stored, CultureInfo.CurrentCulture);

        _code = TextField("Field.Code", 20, 16, 140, form.Code, latin: true, mono: true);
        _code.ReadOnly = !_isNew;
        if (!_isNew) _code.BackColor = UiTheme.SurfaceSunken;
        _code.CharacterCasing = CharacterCasing.Upper;
        _code.MaxLength = CatalogEditorRules.CodeMaxLength;

        _valueType = Choice("Catalog.ValueType", 172, 16, 200, form.ValueType, "ResultValueType");

        _valueType.Enabled = CatalogEditorRules.ValueTypeEditable(existing);

        _unit = Lookup("Field.Unit", 384, 16, 160,
                       lookups.Units.Where(u => u.IsActive)
                              .Select(u => (u.Id, u.DisplaySymbol)),
                       form.UnitId);

        _decimals = Number("Catalog.Decimals", 556, 16, 80, form.Decimals, max: CatalogEditorRules.DecimalsMax);

        _typeNotice = Notice(20, 62, 712, 20,
                             existing is { HasResults: true }
                                 ? "Catalog.ValueTypeFrozenNotice"
                                 : "Catalog.ValueTypeNotice",
                             existing is { HasResults: true } ? UiTheme.Abnormal
                                                               : UiTheme.TextSecondary);

        if (existing is { HasResults: true })
            _typeNotice.Text = localizer.Format("Catalog.ValueTypeFrozenNotice",
                                                 existing.ResultCount);

        _nameFr = TextField("Field.NameFr", 20, 86, 230, form.NameFr);
        _nameAr = TextField("Field.NameAr", 262, 86, 230, form.NameAr, arabic: true);
        _nameEn = TextField("Field.NameEn", 504, 86, 228, form.NameEn);

        _shortName = TextField("Catalog.ShortName", 20, 130, 160, form.ShortName);
        _loinc = TextField("Catalog.Loinc", 192, 130, 160, form.Loinc,
                           latin: true, mono: true);
        // The lengths of the columns: a longer value was refused by the database with an unexpected
        // error, and the form was lost with it.
        _nameFr.MaxLength = _nameAr.MaxLength = _nameEn.MaxLength = CatalogEditorRules.NameMaxLength;
        _shortName.MaxLength = CatalogEditorRules.ShortNameMaxLength;
        _loinc.MaxLength = CatalogEditorRules.LoincMaxLength;
        _cv = TextField("Catalog.AnalyticalCv", 364, 130, 100, form.Cv, latin: true, right: true);
        Numeric(EditorValues.MeasureMaxLength, _cv);

        _displayOrder = Number("Field.DisplayOrder", 476, 130, 100,
                               form.DisplayOrder, max: CatalogEditorRules.DisplayOrderMax);
        _indent = Number("Catalog.IndentLevel", 588, 130, 80, form.IndentLevel, max: CatalogEditorRules.IndentMax);

        // --- limits ----------------------------------------------------------------------------

        Label("Catalog.MeasuringRange", 20, 182);
        _measuringLow = Bare(20, 200, 100, form.MeasuringLow);
        Numeric(EditorValues.MeasureMaxLength, _measuringLow);
        _measuringHigh = Bare(128, 200, 100, form.MeasuringHigh);
        Numeric(EditorValues.MeasureMaxLength, _measuringHigh);

        Label("Catalog.TechnicalLimits", 244, 182);
        _technicalLow = Bare(244, 200, 100, form.TechnicalLow);
        Numeric(EditorValues.MeasureMaxLength, _technicalLow);
        _technicalHigh = Bare(352, 200, 100, form.TechnicalHigh);
        Numeric(EditorValues.MeasureMaxLength, _technicalHigh);

        Label("Catalog.CriticalLimits", 468, 182);
        _criticalLow = Bare(468, 200, 100, form.CriticalLow);
        Numeric(EditorValues.MeasureMaxLength, _criticalLow);
        _criticalHigh = Bare(576, 200, 100, form.CriticalHigh);
        Numeric(EditorValues.MeasureMaxLength, _criticalHigh);

        Notice(20, 234, 712, 26, "Catalog.LimitsNotice");

        // --- delta check -----------------------------------------------------------------------

        _delta = TextField("Catalog.DeltaCheck", 20, 262, 100, form.Delta, latin: true, right: true);
        Numeric(EditorValues.MeasureMaxLength, _delta);

        _deltaWindow = Number("Catalog.DeltaWindow", 132, 262, 100,
                              form.DeltaWindowDays, max: CatalogEditorRules.DeltaWindowMax);

        Notice(244, 280, 488, 34, "Catalog.DeltaNotice");

        // --- formula ---------------------------------------------------------------------------

        _formula = TextField("Catalog.Formula", 20, 320, 712, form.Formula,
                        latin: true, mono: true);
        _formula.MaxLength = CatalogEditorRules.FormulaMaxLength;

        _formulaNotice = Notice(20, 366, 712, 34, "Catalog.FormulaNotice");

        // --- coded options ---------------------------------------------------------------------

        Label("Catalog.CodedOptions", 20, 406);

        _options.Dock = DockStyle.None;
        _options.Bounds = new Rectangle(20, 424, 712, 150);
        _options.AllowUserToAddRows = true;
        _options.AllowUserToDeleteRows = true;
        _options.EditMode = DataGridViewEditMode.EditOnEnter;

        BuildOptionColumns();

        // The stored answers, so a coded parameter is not saved with a list to retype from memory:
        // the list is replaced as a whole on save, and an empty grid was refused or dropped them.
        foreach (var option in form.Options)
            _options.Rows.Add(option.Code, option.LabelFr, option.LabelAr,
                              option.ExpectedNormal, option.Abnormal, option.Critical);

        Controls.Add(_options);

        _addOption = UiTheme.SecondaryButton(localizer["Catalog.AddNegativePositive"]);
        _addOption.Bounds = new Rectangle(20, 580, 220, UiTheme.ButtonHeight - 4);
        _addOption.Click += (_, _) => AddPreset();

        _removeOption = UiTheme.SecondaryButton(localizer["Action.Remove"]);
        _removeOption.Bounds = new Rectangle(250, 580, 140, UiTheme.ButtonHeight - 4);
        _removeOption.Click += (_, _) => RemoveOption();

        _internalOnly = Check("Catalog.InternalOnly", 404, 584, form.InternalOnly);
        _alwaysShow = Check("Catalog.AlwaysShowOnReport", 556, 584, form.AlwaysShowOnReport);
        _active = Check("Field.Active", 20, 616, form.Active, bold: true);

        Controls.AddRange([_addOption, _removeOption]);

        _valueType.SelectedIndexChanged += (_, _) => RefreshEnablement();

        RefreshEnablement();

        Buttons(644, 780);
        UseAdminFrame();
    }

    private TextBox Bare(int x, int y, int width, string value)
    {
        var box = UiTheme.Input(width);
        box.Location = new Point(x, y);
        box.Tag = UiTags.LatinValue;
        box.TextAlign = HorizontalAlignment.Right;
        box.Font = UiTheme.Monospace;
        box.Text = value;

        Controls.Add(box);
        return box;
    }

    private void BuildOptionColumns()
    {
        _options.Columns.Clear();

        _options.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "code", HeaderText = Localizer["Field.Code"], FillWeight = 16,
            DefaultCellStyle = new DataGridViewCellStyle { Font = UiTheme.Monospace }
        });

        _options.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "label", HeaderText = Localizer["Field.NameFr"], FillWeight = 30
        });

        _options.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "labelAr", HeaderText = Localizer["Field.NameAr"], FillWeight = 24
        });

        _options.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "normal", HeaderText = Localizer["Catalog.ExpectedNormal"], FillWeight = 10
        });

        _options.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "abnormal", HeaderText = Localizer["Flag.Abnormal"], FillWeight = 10
        });

        _options.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "critical", HeaderText = Localizer["Catalog.CriticalOption"], FillWeight = 10
        });
    }

    /// <summary>
    /// The list a qualitative parameter almost always needs. Offered as a preset because typing
    /// "NEG / Négatif / سالب" by hand for every serology is how a laboratory ends up with three
    /// spellings of the same answer and a report that cannot be read consistently.
    /// </summary>
    private void AddPreset()
    {
        if (_options.Rows.Cast<DataGridViewRow>().Any(r => !r.IsNewRow)) return;

        foreach (var option in CatalogEditorRules.NegativePositive((key, _) => Localizer[key]))
            _options.Rows.Add(option.Code, option.LabelFr, option.LabelAr, option.ExpectedNormal, option.Abnormal, option.Critical);
    }

    private void RemoveOption()
    {
        if (_options.CurrentRow is { IsNewRow: false } row) _options.Rows.Remove(row);
    }

    /// <summary>
    /// Disables the fields the chosen value type cannot use. Disabled rather than hidden so the
    /// layout does not jump, and so an operator switching type sees what they have just lost.
    /// </summary>
    private void RefreshEnablement()
    {
        var (numeric, coded, heading) = CatalogEditorRules.Fields(Selected<ResultValueType>(_valueType));

        foreach (var box in new[] { _measuringLow, _measuringHigh, _technicalLow, _technicalHigh,
                                    _criticalLow, _criticalHigh, _cv, _delta })
        {
            box.Enabled = numeric;
            box.BackColor = numeric ? UiTheme.SurfaceRaised : UiTheme.SurfaceSunken;
        }

        _deltaWindow.Enabled = numeric;
        _unit.Enabled = numeric;
        _decimals.Enabled = numeric;

        _formula.Enabled = numeric;
        _formula.BackColor = numeric ? UiTheme.SurfaceRaised : UiTheme.SurfaceSunken;

        _options.Enabled = coded;
        _addOption.Enabled = coded;
        _removeOption.Enabled = coded;

        _alwaysShow.Enabled = !heading;

        _formulaNotice.ForeColor = numeric ? UiTheme.TextSecondary : UiTheme.TextMuted;
    }

    /// <summary>The parameter as typed, its answers in the order listed, for the rules shared with the web.</summary>
    private ParameterForm Form() => new()
    {
        Code = _code.Text,
        ValueType = Selected<ResultValueType>(_valueType),
        UnitId = SelectedId(_unit),
        Decimals = (int)_decimals.Value,
        NameFr = _nameFr.Text,
        NameAr = _nameAr.Text,
        NameEn = _nameEn.Text,
        ShortName = _shortName.Text,
        Loinc = _loinc.Text,
        Cv = _cv.Text,
        DisplayOrder = (int)_displayOrder.Value,
        IndentLevel = (int)_indent.Value,
        MeasuringLow = _measuringLow.Text,
        MeasuringHigh = _measuringHigh.Text,
        TechnicalLow = _technicalLow.Text,
        TechnicalHigh = _technicalHigh.Text,
        CriticalLow = _criticalLow.Text,
        CriticalHigh = _criticalHigh.Text,
        Delta = _delta.Text,
        DeltaWindowDays = (int)_deltaWindow.Value,
        Formula = _formula.Text,
        InternalOnly = _internalOnly.Checked,
        AlwaysShowOnReport = _alwaysShow.Checked,
        Active = _active.Checked,
        Options = [.. _options.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => new CodedOptionLine(
            r.Cells["code"].Value?.ToString() ?? string.Empty,
            r.Cells["label"].Value?.ToString() ?? string.Empty,
            r.Cells["labelAr"].Value?.ToString() ?? string.Empty,
            r.Cells["normal"].Value is true, r.Cells["abnormal"].Value is true, r.Cells["critical"].Value is true))]
    };

    protected override void Accept()
    {
        var form = Form();

        // The formula is checked against the sibling codes here as well as in the service, so the
        // operator is told which code is wrong while the formula is still in front of them.
        if (Refused(CatalogEditorRules.Check(form, _siblings.Select(s => s.Code)),
                    ("cv", _cv), ("measuringlow", _measuringLow), ("measuringhigh", _measuringHigh),
                    ("technicallow", _technicalLow), ("technicalhigh", _technicalHigh),
                    ("criticallow", _criticalLow), ("criticalhigh", _criticalHigh), ("delta", _delta),
                    ("code", _code), ("namefr", _nameFr), ("formula", _formula)))
            return;

        Request = CatalogEditorRules.ToRequest(form, _id, _analysisId, _stored);

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Creating or revising a reference interval.
/// <para>
/// The population fields are what make an interval usable: a haemoglobin interval that ignores sex
/// flags half the healthy population as anaemic. The specificity shown at the bottom is the number
/// the resolver uses to pick between two intervals that both match, so an operator building a
/// pregnancy interval on top of a female one can see that it will win.
/// </para>
/// <para>
/// When the interval is already in force and has interpreted results, changing its limits opens a
/// revision instead of rewriting it. The notice says so before the operator saves, because
/// discovering it afterwards from a second row appearing in the list looks like a bug.
/// </para>
/// </summary>
internal sealed class ReferenceRangeEditorDialog : MasterDataEditor
{
    private readonly Guid? _id;
    private readonly Guid _parameterId;

    private readonly ComboBox _gender, _pregnancy, _trimester, _instrument;
    private readonly TextBox _lower, _upper, _displayFr, _displayAr, _expectedCoded, _source;
    private readonly NumericUpDown _ageFrom, _ageTo;
    private readonly ComboBox _ageUnit;
    private readonly CheckBox _decisionThreshold, _active;
    private readonly (CheckBox Toggle, DateTimePicker Picker) _validFrom, _validUntil, _reviewed;
    private readonly Label _specificity;

    internal ReferenceRangeRequest? Request { get; private set; }

    internal ReferenceRangeEditorDialog(ILocalizer localizer, IUserDialogs dialogs,
                                        CatalogLookups lookups, ParameterRow parameter,
                                        ReferenceRangeRow? existing, DateOnly today)
        : base(localizer, dialogs,
               existing is null ? "Catalog.NewRange" : "Catalog.EditRange",
               parameter.Code, 700, 560)
    {
        _id = existing?.Id;
        _parameterId = parameter.Id;

        var form = CatalogEditorRules.FormOf(existing, parameter.Decimals, today, CultureInfo.CurrentCulture);

        _gender = Choice("Field.Gender", 20, 16, 180, form.Gender,
                         "Gender", [Gender.Unknown, Gender.Male, Gender.Female]);

        _pregnancy = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Bounds = new Rectangle(212, 34, 180, UiTheme.InputHeight)
        };

        Label("Catalog.Pregnancy", 212, 16);

        _pregnancy.Items.Add(new EnumChoice(0, localizer["Catalog.PregnancyAny"]));
        _pregnancy.Items.Add(new EnumChoice(1, localizer["Patient.Pregnant"]));
        _pregnancy.Items.Add(new EnumChoice(2, localizer["Catalog.NotPregnant"]));

        _pregnancy.SelectedIndex = form.Pregnancy switch
        {
            true => 1,
            false => 2,
            _ => 0
        };

        Controls.Add(_pregnancy);

        _trimester = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Bounds = new Rectangle(404, 34, 140, UiTheme.InputHeight)
        };

        Label("Catalog.TrimesterLabel", 404, 16);

        _trimester.Items.Add(new EnumChoice(0, "—"));
        for (var t = 1; t <= 3; t++)
            _trimester.Items.Add(new EnumChoice(t, localizer.Format("Catalog.Trimester", t)));

        _trimester.SelectedIndex = form.Trimester ?? 0;

        Controls.Add(_trimester);

        // --- age band ---------------------------------------------------------------------------

        Label("Catalog.AgeBand", 20, 74);

        _ageFrom = new NumericUpDown
        {
            Bounds = new Rectangle(20, 92, 100, UiTheme.InputHeight), Maximum = CatalogEditorRules.AgeMax
        };

        _ageTo = new NumericUpDown
        {
            Bounds = new Rectangle(128, 92, 100, UiTheme.InputHeight), Maximum = CatalogEditorRules.AgeMax
        };

        _ageUnit = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Bounds = new Rectangle(236, 92, 140, UiTheme.InputHeight)
        };

        // Entered in the unit the interval is written in, stored in days. A neonatal interval is
        // published in days and an adult one in years, and forcing the operator to convert is how
        // a paediatric band ends up out by a factor of 365.
        string[] units = ["Catalog.Days", "Catalog.Months", "Catalog.Years"];
        for (var i = 0; i < units.Length; i++)
            _ageUnit.Items.Add(new EnumChoice(CatalogEditorRules.AgeUnits[i], localizer[units[i]]));

        // The unit it was typed in; for a band saved before the unit was kept, the largest unit
        // that holds both bounds exactly, or the band would be re-saved truncated.
        _ageUnit.SelectedIndex = Math.Max(0, CatalogEditorRules.AgeUnits.ToList().IndexOf(form.AgeUnitDays));
        _ageFrom.Value = form.AgeFrom;
        _ageTo.Value = form.AgeTo;

        // Saved before the unit was kept: shown in the unit it is read in, which may not be the one
        // it was written in. Saving confirms the unit chosen here.
        if (existing is { HasUnconfirmedAgeUnit: true })
            Notice(20, 118, 356, 36, "Catalog.AgeUnitToConfirmNotice", UiTheme.Abnormal);

        Controls.AddRange([_ageFrom, _ageTo, _ageUnit]);

        _instrument = Lookup("Result.Instrument", 388, 74, 280,
                             lookups.Instruments.Select(
                                 i => (i.Id, $"{i.Code}  {i.Name.For(localizer.CurrentLanguage)}")),
                             form.InstrumentId);

        Notice(388, 118, 280, 38, "Catalog.InstrumentRangeNotice");

        // --- the interval -----------------------------------------------------------------------

        Label("Result.ReferenceRange", 20, 158);
        _lower = Bare(20, 176, 120, form.Lower);
        Numeric(EditorValues.MeasureMaxLength, _lower);
        _upper = Bare(148, 176, 120, form.Upper);
        Numeric(EditorValues.MeasureMaxLength, _upper);

        _expectedCoded = TextField("Catalog.ExpectedCodedValue", 284, 158, 180, form.ExpectedCoded);
        _expectedCoded.MaxLength = CatalogEditorRules.CodedValueMaxLength;

        _decisionThreshold = Check("Catalog.DecisionThreshold", 480, 176, form.DecisionThreshold);

        _displayFr = TextField("Catalog.PrintedInterval", 20, 212, 320, form.DisplayFr);
        _displayAr = TextField("Field.NameAr", 352, 212, 316, form.DisplayAr, arabic: true);
        // The lengths of the columns: a longer value was refused by the database with an unexpected
        // error, and the form was lost with it.
        _displayFr.MaxLength = _displayAr.MaxLength = CatalogEditorRules.PrintedIntervalMaxLength;

        Notice(20, 256, 648, 20, "Catalog.PrintedIntervalNotice");

        // --- provenance and validity -------------------------------------------------------------

        _source = TextField("Catalog.RangeSource", 20, 284, 400, form.Source);
        _source.MaxLength = CatalogEditorRules.SourceMaxLength;

        _reviewed = OptionalDate("Catalog.LastReviewed", 436, 302, form.LastReviewedOn,
                                  max: today.ToDateTime(TimeOnly.MinValue));

        _validFrom = OptionalDate("Catalog.ValidFrom", 20, 350, form.ValidFrom);
        _validFrom.Toggle.Checked = true;
        _validFrom.Toggle.Enabled = false;
        _validFrom.Picker.Enabled = true;

        _validUntil = OptionalDate("Catalog.ValidUntil", 236, 350, form.ValidUntil);

        _active = Check("Field.Active", 452, 352, form.Active, bold: true);

        _specificity = Notice(20, 400, 648, 20, "Catalog.SpecificityNotice", UiTheme.Accent);

        Notice(20, 424, 648, 44, "Catalog.RangeRevisionNotice", UiTheme.Abnormal);

        foreach (var control in new Control[] { _gender, _pregnancy, _trimester, _ageFrom, _ageTo })
            control.TextChanged += (_, _) => RefreshSpecificity();

        _gender.SelectedIndexChanged += (_, _) => RefreshSpecificity();
        _pregnancy.SelectedIndexChanged += (_, _) => RefreshSpecificity();
        _trimester.SelectedIndexChanged += (_, _) => RefreshSpecificity();
        _instrument.SelectedIndexChanged += (_, _) => RefreshSpecificity();

        RefreshSpecificity();

        Buttons(500, 700);
        UseAdminFrame();
    }

    private TextBox Bare(int x, int y, int width, string value)
    {
        var box = UiTheme.Input(width);
        box.Location = new Point(x, y);
        box.Tag = UiTags.LatinValue;
        box.TextAlign = HorizontalAlignment.Right;
        box.Font = UiTheme.Monospace;
        box.Text = value;

        Controls.Add(box);
        return box;
    }

    private int AgeUnitDays => (_ageUnit.SelectedItem as EnumChoice)?.Value ?? 365;

    private bool? Pregnancy => (_pregnancy.SelectedItem as EnumChoice)?.Value switch
    {
        1 => true,
        2 => false,
        _ => null
    };

    private int? Trimester => (_trimester.SelectedItem as EnumChoice)?.Value is { } value and > 0
        ? value : null;

    /// <summary>The interval as typed, for the rules shared with the web.</summary>
    private RangeForm Form() => new()
    {
        Gender = Selected<Gender>(_gender),
        Pregnancy = Pregnancy,
        Trimester = Trimester,
        AgeFrom = (int)_ageFrom.Value,
        AgeTo = (int)_ageTo.Value,
        AgeUnitDays = AgeUnitDays,
        InstrumentId = SelectedId(_instrument),
        Lower = _lower.Text,
        Upper = _upper.Text,
        ExpectedCoded = _expectedCoded.Text,
        DecisionThreshold = _decisionThreshold.Checked,
        DisplayFr = _displayFr.Text,
        DisplayAr = _displayAr.Text,
        Source = _source.Text,
        LastReviewedOn = DateOrNull(_reviewed.Toggle, _reviewed.Picker),
        ValidFrom = DateOnly.FromDateTime(_validFrom.Picker.Value.Date),
        ValidUntil = DateOrNull(_validUntil.Toggle, _validUntil.Picker),
        Active = _active.Checked
    };

    private void RefreshSpecificity() =>
        _specificity.Text = Localizer.Format("Catalog.SpecificityNotice", CatalogEditorRules.Specificity(Form()));

    protected override void Accept()
    {
        var form = Form();

        if (Refused(CatalogEditorRules.Check(form), ("lower", _lower), ("upper", _upper), ("agefrom", _ageFrom))) return;

        Request = CatalogEditorRules.ToRequest(form, _id, _parameterId);

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Records a periodic review of a reference interval that left it unchanged.
/// <para>
/// A separate dialog from the editor because it is a separate act. ISO 15189 §5.5.2 obliges the
/// laboratory to review its intervals, and "reviewed, unchanged" is the commonest outcome — routing
/// it through the editor would make the operator open a form full of limits they must not touch.
/// </para>
/// </summary>
internal sealed class RangeReviewDialog : Form
{
    private readonly TextBox _source = UiTheme.Input(440);

    internal string? Source => string.IsNullOrWhiteSpace(_source.Text) ? null : _source.Text.Trim();

    internal RangeReviewDialog(ILocalizer localizer, ReferenceRangeRow range, DateOnly today)
    {
        Text = localizer["Catalog.RecordReview"];
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(500, 230);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var current = new Label
        {
            Text = range.LastReviewedOn is { } reviewed
                ? localizer.Format("Catalog.LastReviewedOn", reviewed.ToString("dd/MM/yyyy"))
                : localizer["Catalog.NeverReviewed"],
            Font = UiTheme.BodyBold,
            ForeColor = range.NeedsReview(today) ? UiTheme.Abnormal : UiTheme.TextPrimary,
            AutoSize = false,
            Bounds = new Rectangle(20, 16, 460, 22)
        };

        var notice = new Label
        {
            Text = localizer["Catalog.ReviewNotice"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(20, 44, 460, 44)
        };

        var sourceLabel = UiTheme.FieldLabel(localizer["Catalog.RangeSource"]);
        sourceLabel.Location = new Point(20, 98);

        _source.Location = new Point(20, 116);
        _source.Text = range.Source ?? string.Empty;
        _source.MaxLength = 300;

        var stamp = new Label
        {
            Text = localizer.Format("Catalog.ReviewWillBeStamped", today.ToString("dd/MM/yyyy")),
            Font = UiTheme.Small,
            ForeColor = UiTheme.Accent,
            AutoSize = false,
            Bounds = new Rectangle(20, 150, 460, 20)
        };

        var ok = UiTheme.PrimaryButton(localizer["Catalog.RecordReview"]);
        ok.Bounds = new Rectangle(20, 182, 220, UiTheme.ButtonHeight);
        ok.DialogResult = DialogResult.OK;

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(260, 182, 220, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([current, notice, sourceLabel, _source, stamp, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;
        AdminDialogFrame.Attach(this, localizer, AdminDialogFrame.Lead(localizer, nameof(RangeReviewDialog)));
    }
}

/// <summary>
/// Entering a tariff revision.
/// <para>
/// There is no edit here, only a new version, and the dialog says so. A price is versioned because
/// an invoice issued last year must reprint with the price it charged: the CGI makes an issued
/// invoice immutable, and a catalogue that overwrote its prices would turn every reprint into a
/// different document.
/// </para>
/// <para>
/// The reimbursement base is computed live from the coefficient and the letter-key value, and the
/// excess the patient bears is shown beside it. That excess is the figure a reception clerk is most
/// often asked to explain, and a laboratory setting a price above the TNR should see it while it
/// makes the decision.
/// </para>
/// </summary>
internal sealed class TariffEditorDialog : MasterDataEditor
{
    private readonly Guid _analysisId;
    private readonly IReadOnlyList<TariffRow> _existing;
    private readonly TariffRow? _editing;

    private readonly ComboBox _payer;
    private readonly TextBox _letterKey, _coefficient, _letterKeyValue, _price, _vatRate;
    private readonly CheckBox _vatExempt;
    private readonly DateTimePicker _validFrom;
    private readonly Label _computed, _currentTariff;

    internal TariffRequest? Request { get; private set; }

    /// <summary>The tariff being corrected, or null when a new one is entered.</summary>
    internal Guid? EditingId => _editing?.Id;

    internal TariffEditorDialog(ILocalizer localizer, IUserDialogs dialogs,
                                CatalogLookups lookups, AnalysisRow analysis,
                                IReadOnlyList<TariffRow> existing, DateOnly today,
                                IReadOnlyList<TariffPayerOption> payerOptions,
                                TariffRow? editing = null, decimal? nationalLetterKeyValue = null)
        : base(localizer, dialogs, editing is null ? "Catalog.NewTariff" : "Catalog.EditTariff",
               analysis.Code, 620, 470)
    {
        _analysisId = analysis.Id;
        _existing = existing;
        _editing = editing;

        var form = CatalogEditorRules.FormOf(analysis, existing, today, editing, nationalLetterKeyValue,
                                             CultureInfo.CurrentCulture);

        // Every active payer, plus any this analysis already has a tariff for (one since switched
        // off still has its history here).
        var payers = CatalogEditorRules.Payers(payerOptions, existing, localizer.CurrentLanguage);

        _payer = Lookup("Coverage.Payer", 20, 16, 280, payers, form.PayerId);
        _payer.Items[0] = new LookupChoice(null, localizer["Catalog.DefaultPriceList"]);
        if (editing is { PayerId: null }) _payer.SelectedIndex = 0;

        Label("Catalog.ValidFrom", 312, 16);

        _validFrom = new DateTimePicker
        {
            Bounds = new Rectangle(312, 34, 180, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            Value = form.ValidFrom.ToDateTime(TimeOnly.MinValue)
        };

        Controls.Add(_validFrom);

        _currentTariff = Notice(20, 66, 580, 34, "Catalog.NoCurrentTariff", UiTheme.TextSecondary);

        // The letter-key value is national, the same for every analysis
        // (CatalogEditorRules.KnownLetterKeyValue), so a new tariff does not start with a
        // reimbursement base of zero and the whole price shown as excess.
        _letterKey = TextField("Billing.LetterKey", 20, 110, 80, form.LetterKey,
                               latin: true, mono: true);
        _letterKey.MaxLength = CatalogEditorRules.LetterKeyMaxLength;
        _letterKey.CharacterCasing = CharacterCasing.Upper;

        _coefficient = TextField("Billing.Coefficient", 112, 110, 120, form.Coefficient,
                            latin: true, right: true, mono: true);
        Numeric(EditorValues.MeasureMaxLength, _coefficient);

        _letterKeyValue = TextField("Catalog.LetterKeyValue", 244, 110, 120, form.LetterKeyValue,
                               latin: true, right: true, mono: true);
        Numeric(EditorValues.MeasureMaxLength, _letterKeyValue);

        _price = TextField("Field.Price", 376, 110, 140, form.Price,
                      latin: true, right: true, mono: true);
        Numeric(EditorValues.AmountMaxLength, _price);

        _computed = Notice(20, 158, 580, 44, "Catalog.TariffComputedNotice", UiTheme.Accent);

        // The rate box sits clear of the check box's caption, which is long and bold.
        _vatExempt = Check("Billing.VatExempt", 20, 214, form.VatExempt, bold: true);
        _vatRate = TextField("Billing.Vat", 440, 206, 100, form.VatRate, latin: true, right: true);
        Numeric(EditorValues.MeasureMaxLength, _vatRate);

        Notice(20, 244, 580, 44, "Catalog.VatNotice");

        Notice(20, 296, 580, 60,
               editing is null ? "Catalog.TariffVersioningNotice" : "Catalog.TariffCorrectionNotice",
               UiTheme.Abnormal);

        // A correction keeps its payer and its dates: a new price from another day is a new tariff.
        if (editing is not null)
        {
            _payer.Enabled = false;
            _validFrom.Enabled = false;
        }

        _vatExempt.CheckedChanged += (_, _) => RefreshVat();
        _payer.SelectedIndexChanged += (_, _) => RefreshCurrent();

        foreach (var box in new[] { _coefficient, _letterKeyValue, _price })
            box.TextChanged += (_, _) => RefreshComputed();

        RefreshVat();
        RefreshCurrent();
        RefreshComputed();

        Buttons(400, 620);
        UseAdminFrame();
    }

    private void RefreshVat()
    {
        _vatRate.Enabled = !_vatExempt.Checked;
        _vatRate.BackColor = _vatExempt.Checked ? UiTheme.SurfaceSunken : UiTheme.SurfaceRaised;
        if (_vatExempt.Checked) _vatRate.Text = string.Empty;
    }

    /// <summary>
    /// States the tariff this revision will close, so the operator can see what they are
    /// superseding and that backdating behind it will be refused.
    /// </summary>
    private void RefreshCurrent()
    {
        var current = CatalogEditorRules.CurrentTariff(_editing, _existing, SelectedId(_payer));

        if (current is null)
        {
            _currentTariff.ForeColor = UiTheme.TextSecondary;
            _currentTariff.Text = Localizer["Catalog.NoCurrentTariff"];
            return;
        }

        _currentTariff.ForeColor = UiTheme.TextPrimary;

        _currentTariff.Text = Localizer.Format(
            "Catalog.CurrentTariffNotice",
            Money.Of(current.Price).Format(Localizer.CurrentLanguage),
            current.ValidFrom.ToString("dd/MM/yyyy"),
            $"{current.LetterKey} {current.Coefficient:0.##}");

        var typed = DateOnly.FromDateTime(_validFrom.Value.Date);
        var from = CatalogEditorRules.ValidFrom(typed, current, correcting: _editing is not null);
        if (from != typed) _validFrom.Value = from.ToDateTime(TimeOnly.MinValue);
    }

    /// <summary>The tariff as typed, for the rules shared with the web.</summary>
    private TariffForm Form() => new()
    {
        PayerId = SelectedId(_payer),
        ValidFrom = DateOnly.FromDateTime(_validFrom.Value.Date),
        LetterKey = _letterKey.Text,
        Coefficient = _coefficient.Text,
        LetterKeyValue = _letterKeyValue.Text,
        Price = _price.Text,
        VatExempt = _vatExempt.Checked,
        VatRate = _vatRate.Text
    };

    private void RefreshComputed()
    {
        var (reference, excess) = CatalogEditorRules.Computed(_coefficient.Text, _letterKeyValue.Text, _price.Text);
        var language = Localizer.CurrentLanguage;

        _computed.ForeColor = excess > 0m ? UiTheme.Abnormal : UiTheme.Accent;

        _computed.Text = Localizer.Format(
            "Catalog.TariffComputedNotice",
            Money.Of(reference).Format(language),
            Money.Of(excess).Format(language));
    }

    protected override void Accept()
    {
        var form = Form();

        if (Refused(CatalogEditorRules.Check(form), ("coefficient", _coefficient), ("letterkeyvalue", _letterKeyValue),
                    ("price", _price), ("letterkey", _letterKey), ("vatrate", _vatRate)))
            return;

        Request = CatalogEditorRules.ToRequest(form, _analysisId);

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Creating or editing a panel.
/// <para>
/// Only orderable analyses are offered as members. A panel built on an analysis with no tariff or no
/// active parameter fails at registration, on a component the operator did not choose and cannot
/// see — so the list is filtered here rather than letting the failure surface at the counter.
/// </para>
/// <para>
/// The component total is shown against the package price because the package price is a discount
/// in practice. A package above the sum of its parts is almost always a typing slip, and seeing
/// both numbers together is what catches it.
/// </para>
/// </summary>
internal sealed class PanelEditorDialog : MasterDataEditor
{
    private readonly Guid? _id;
    private readonly bool _isNew;

    private readonly TextBox _code, _nameFr, _nameAr, _nameEn, _aliases, _packagePrice;
    private readonly NumericUpDown _displayOrder;
    private readonly CheckBox _itemize, _groupOnReport, _active;
    private readonly DataGridView _candidates = UiTheme.Grid();
    private readonly Label _total;
    private readonly IReadOnlyList<AnalysisRow> _available;

    internal PanelRequest? Request { get; private set; }

    internal PanelEditorDialog(ILocalizer localizer, IUserDialogs dialogs,
                               IReadOnlyList<AnalysisRow> candidates, PanelRow? existing)
        : base(localizer, dialogs, existing is null ? "Catalog.NewPanel" : "Catalog.EditPanel",
               existing?.Code, 780, 620)
    {
        _id = existing?.Id;
        _isNew = existing is null;
        _available = CatalogEditorRules.Candidates(candidates);

        var language = localizer.CurrentLanguage;
        var form = CatalogEditorRules.FormOf(existing, CultureInfo.CurrentCulture);

        _code = TextField("Field.Code", 20, 16, 140, form.Code, latin: true, mono: true);
        _code.ReadOnly = !_isNew;
        if (!_isNew) _code.BackColor = UiTheme.SurfaceSunken;
        _code.CharacterCasing = CharacterCasing.Upper;

        _nameFr = TextField("Field.NameFr", 172, 16, 240, form.NameFr);
        _nameAr = TextField("Field.NameAr", 424, 16, 160, form.NameAr, arabic: true);
        _nameEn = TextField("Field.NameEn", 596, 16, 160, form.NameEn);

        _aliases = TextField("Catalog.SearchAliases", 20, 60, 392, form.Aliases);

        // Each box holds no more than its column: a longer entry otherwise ends in « Erreur inattendue » at the save.
        _code.MaxLength = CatalogEditorRules.CodeMaxLength;
        _nameFr.MaxLength = _nameAr.MaxLength = _nameEn.MaxLength = CatalogEditorRules.NameMaxLength;
        _aliases.MaxLength = CatalogEditorRules.AliasesMaxLength;

        _packagePrice = TextField("Catalog.PackagePrice", 424, 60, 160, form.PackagePrice,
                             latin: true, right: true, mono: true);
        Numeric(EditorValues.AmountMaxLength, _packagePrice);

        _displayOrder = Number("Field.DisplayOrder", 596, 60, 100,
                               form.DisplayOrder, max: CatalogEditorRules.DisplayOrderMax);

        _itemize = Check("Catalog.ItemizeOnInvoice", 20, 108, form.ItemizeOnInvoice);
        _groupOnReport = Check("Catalog.GroupOnReport", 260, 108, form.GroupOnReport);
        _active = Check("Field.Active", 500, 108, form.Active, bold: true);

        Notice(20, 134, 736, 20, "Catalog.ItemizeNotice");

        // --- members ----------------------------------------------------------------------------

        Label("Catalog.Analyses", 20, 162);

        _candidates.Dock = DockStyle.None;
        _candidates.Bounds = new Rectangle(20, 180, 736, 320);

        _candidates.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "included", HeaderText = string.Empty, FillWeight = 6
        });

        _candidates.Columns.Add(new DataGridViewCheckBoxColumn
        {
            // Wide enough for its caption: « facultative » was cut to « facultativ ».
            Name = "optional", HeaderText = localizer["Catalog.Optional"], FillWeight = 13
        });

        foreach (var (name, header, weight, font, right) in new[]
                 {
                     ("code", localizer["Field.Code"], 14, UiTheme.MonospaceBold, false),
                     ("name", localizer["Field.Name"], 35, UiTheme.Body, false),
                     ("department", localizer["Field.Department"], 14, UiTheme.Small, false),
                     ("price", localizer["Field.Price"], 18, UiTheme.Monospace, true)
                 })
        {
            _candidates.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font,
                    Alignment = right ? DataGridViewContentAlignment.MiddleRight
                                      : DataGridViewContentAlignment.MiddleLeft
                }
            });
        }

        foreach (var candidate in _available)
        {
            var member = form.Members.FirstOrDefault(m => m.AnalysisTestId == candidate.Id);

            var index = _candidates.Rows.Add(
                member is not null,
                member?.IsOptional ?? false,
                candidate.Code,
                candidate.Name.For(language),
                candidate.DepartmentCode,
                candidate.CurrentPrice is { } price
                    ? Money.Of(price).Format(language)
                    : "—");

            _candidates.Rows[index].Tag = candidate;
        }

        Controls.Add(_candidates);

        _total = Notice(20, 508, 736, 20, "Catalog.ComponentTotalNotice", UiTheme.Accent);

        _candidates.CellValueChanged += (_, _) => RefreshTotal();
        _candidates.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_candidates.IsCurrentCellDirty)
                _candidates.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        _packagePrice.TextChanged += (_, _) => RefreshTotal();

        RefreshTotal();

        Buttons(548, 780);
        UseAdminFrame();
    }

    /// <summary>The panel as typed, its ticked members in the order listed, for the rules shared with the web.</summary>
    private PanelForm Form() => new()
    {
        Code = _code.Text,
        NameFr = _nameFr.Text,
        NameAr = _nameAr.Text,
        NameEn = _nameEn.Text,
        Aliases = _aliases.Text,
        PackagePrice = _packagePrice.Text,
        DisplayOrder = (int)_displayOrder.Value,
        ItemizeOnInvoice = _itemize.Checked,
        GroupOnReport = _groupOnReport.Checked,
        Active = _active.Checked,
        Members = [.. _candidates.Rows.Cast<DataGridViewRow>()
                                 .Where(r => r.Cells["included"].Value is true && r.Tag is AnalysisRow)
                                 .Select(r => new PanelMemberRequest(((AnalysisRow)r.Tag!).Id, r.Cells["optional"].Value is true))]
    };

    /// <summary>
    /// The members ticked and what they cost bought one by one, the optional ones left out, against
    /// the package price (<see cref="CatalogEditorRules.Total"/>).
    /// </summary>
    private void RefreshTotal()
    {
        var language = Localizer.CurrentLanguage;
        var form = Form();
        var (count, total, packageAbove) = CatalogEditorRules.Total(_available, form);
        var package = DecimalOrNull(_packagePrice);

        _total.ForeColor = packageAbove ? UiTheme.Critical : UiTheme.Accent;

        _total.Text = Localizer.Format("Catalog.ComponentTotalNotice", count,
                                        Money.Of(total).Format(language),
                                        package is { } value
                                            ? Money.Of(value).Format(language)
                                            : "—");
    }

    protected override void Accept()
    {
        var form = Form();

        if (Refused(CatalogEditorRules.Check(form), ("package", _packagePrice), ("code", _code), ("namefr", _nameFr),
                    ("members", _candidates)))
            return;

        Request = CatalogEditorRules.ToRequest(form, _id);

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Creating or editing a reflex rule (analyse en cascade).
/// <para>
/// The trigger parameter is picked through its analysis, because that is how a biologist thinks of
/// it: "a positive HBs antigen triggers a confirmation", not "parameter 3f2a…". The condition
/// fields change with the condition, so a "greater than" cannot be saved with an upper bound it
/// will never read.
/// </para>
/// <para>
/// Whether the reflex act is billed is an explicit choice with no default that hides it. A reflex
/// analysis added silently and charged to a patient who never asked for it is a complaint the
/// laboratory cannot answer; one absorbed by the laboratory is a cost it should have decided on.
/// </para>
/// </summary>
internal sealed class ReflexRuleEditorDialog : MasterDataEditor
{
    private readonly Guid? _id;
    private readonly CatalogMaintenanceService _catalog;

    private readonly ComboBox _triggerAnalysis, _triggerParameter, _target, _condition;
    private readonly TextBox _threshold, _thresholdUpper, _codedValue, _descriptionFr, _descriptionAr;
    private readonly CheckBox _automatic, _billable, _active;
    private readonly Label _conditionNotice;

    private Guid _selectedParameterId;

    internal ReflexRuleRequest? Request { get; private set; }

    internal ReflexRuleEditorDialog(ILocalizer localizer, IUserDialogs dialogs,
                                    CatalogMaintenanceService catalog,
                                    IReadOnlyList<AnalysisRow> analyses, ReflexRuleRow? existing)
        : base(localizer, dialogs,
               existing is null ? "Catalog.NewReflexRule" : "Catalog.EditReflexRule",
               existing?.Description.Fr, 660, 520)
    {
        _id = existing?.Id;
        _catalog = catalog;
        _selectedParameterId = existing?.TriggerParameterId ?? Guid.Empty;

        var language = localizer.CurrentLanguage;
        var form = CatalogEditorRules.FormOf(existing, analyses, CultureInfo.CurrentCulture);

        var options = analyses.Select(a => (a.Id, $"{a.Code}  {a.Name.For(language)}")).ToList();

        _triggerAnalysis = Lookup("Catalog.TriggerAnalysis", 20, 16, 300, options,
                                   form.TriggerAnalysisId, allowNone: false);

        _triggerParameter = Lookup("Catalog.TriggerParameter", 332, 16, 288, [], null,
                                    allowNone: false);

        _condition = Choice("Catalog.ReflexCondition", 20, 64, 220, form.Condition, "ReflexCondition");

        _threshold = TextField("Catalog.Threshold", 252, 64, 120, form.Threshold,
                          latin: true, right: true, mono: true);
        Numeric(EditorValues.MeasureMaxLength, _threshold);

        _thresholdUpper = TextField("Catalog.ThresholdUpper", 384, 64, 120, form.ThresholdUpper,
                               latin: true, right: true, mono: true);
        Numeric(EditorValues.MeasureMaxLength, _thresholdUpper);

        _codedValue = TextField("Catalog.TriggerCodedValue", 516, 64, 104,
                           form.CodedValue, latin: true, mono: true);

        _conditionNotice = Notice(20, 112, 600, 34, "Catalog.ReflexConditionNotice");

        _target = Lookup("Catalog.ReflexAdds", 20, 154, 600, options, form.TargetAnalysisId, allowNone: false);

        _descriptionFr = TextField("Field.DescriptionFr", 20, 202, 300, form.DescriptionFr);
        _descriptionAr = TextField("Field.DescriptionAr", 332, 202, 288, form.DescriptionAr,
                              arabic: true);
        _descriptionFr.MaxLength = _descriptionAr.MaxLength = CatalogEditorRules.DescriptionMaxLength;
        _codedValue.MaxLength = CatalogEditorRules.CodedValueMaxLength;

        _automatic = Check("Catalog.AddAutomatically", 20, 250, form.AddAutomatically);

        _billable = Check("Catalog.ReflexBillable", 260, 250, form.Billable);
        _active = Check("Field.Active", 480, 250, form.Active, bold: true);

        Notice(20, 278, 600, 44, "Catalog.ReflexBillingNotice", UiTheme.Abnormal);
        Notice(20, 330, 600, 44, "Catalog.ReflexAutomaticNotice");

        _condition.SelectedIndexChanged += (_, _) => RefreshCondition();
        _triggerAnalysis.SelectedIndexChanged += async (_, _) => await LoadParametersAsync();

        _triggerParameter.SelectedIndexChanged += (_, _) =>
            _selectedParameterId = SelectedId(_triggerParameter) ?? Guid.Empty;

        RefreshCondition();

        Shown += async (_, _) => await LoadParametersAsync(existing?.TriggerParameterId);

        Buttons(440, 660);
        UseAdminFrame();
    }

    private async Task LoadParametersAsync(Guid? preselect = null)
    {
        if (SelectedId(_triggerAnalysis) is not { } analysisId) return;

        var result = await _catalog.GetParametersAsync(analysisId);

        if (result.IsFailure) return;

        var language = Localizer.CurrentLanguage;

        _triggerParameter.Items.Clear();

        // Headings carry no result and cannot trigger anything, so they are not offered.
        var offered = CatalogEditorRules.TriggerParameters(result.Value);
        var kept = CatalogEditorRules.TriggerParameter(offered, preselect ?? _selectedParameterId);

        foreach (var parameter in offered)
        {
            var index = _triggerParameter.Items.Add(
                new LookupChoice(parameter.Id,
                                  $"{parameter.Code}  {parameter.Name.For(language)}"));

            if (parameter.Id == kept) _triggerParameter.SelectedIndex = index;
        }

        _selectedParameterId = SelectedId(_triggerParameter) ?? Guid.Empty;
    }

    private void RefreshCondition()
    {
        var fields = CatalogEditorRules.Fields(Selected<ReflexCondition>(_condition));

        Enable(_threshold, fields.Threshold);
        Enable(_thresholdUpper, fields.ThresholdUpper);
        Enable(_codedValue, fields.CodedValue);

        _conditionNotice.Text = Localizer[fields.NoticeKey];

        static void Enable(TextBox box, bool enabled)
        {
            box.Enabled = enabled;
            box.BackColor = enabled ? UiTheme.SurfaceRaised : UiTheme.SurfaceSunken;
            if (!enabled) box.Text = string.Empty;
        }
    }

    /// <summary>The rule as typed, for the rules shared with the web.</summary>
    private ReflexForm Form() => new()
    {
        TriggerAnalysisId = SelectedId(_triggerAnalysis),
        TriggerParameterId = _selectedParameterId,
        Condition = Selected<ReflexCondition>(_condition),
        Threshold = _threshold.Text,
        ThresholdUpper = _thresholdUpper.Text,
        CodedValue = _codedValue.Text,
        TargetAnalysisId = SelectedId(_target),
        DescriptionFr = _descriptionFr.Text,
        DescriptionAr = _descriptionAr.Text,
        AddAutomatically = _automatic.Checked,
        Billable = _billable.Checked,
        Active = _active.Checked
    };

    protected override void Accept()
    {
        var form = Form();

        if (Refused(CatalogEditorRules.Check(form), ("threshold", _threshold), ("thresholdupper", _thresholdUpper),
                    ("descriptionfr", _descriptionFr), ("codedvalue", _codedValue)))
            return;

        Request = CatalogEditorRules.ToRequest(form, _id);

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Creating or editing one of the lists the catalogue is built on.
/// <para>
/// One dialog for four lists, showing only the fields the chosen list has. The alternative — four
/// near-identical dialogs — is four places to forget the immutable code or the in-use check, and
/// the operator is performing the same act in each.
/// </para>
/// </summary>
internal sealed class NomenclatureEditorDialog : MasterDataEditor
{
    private readonly NomenclatureKind _kind;
    private readonly Guid? _id;
    private readonly bool _isNew;

    private readonly TextBox _code, _nameFr, _nameAr, _nameEn;
    private readonly CheckBox _active;

    // Department
    private readonly NumericUpDown? _displayOrder;
    private readonly CheckBox? _accreditationScope;

    // SpecimenType
    private readonly TextBox? _collectionInstructions, _patientInstructions;
    private readonly NumericUpDown? _fastingHours;

    // ContainerType
    private readonly TextBox? _capColour, _additive, _nominalVolume, _minimumVolume;
    private readonly NumericUpDown? _drawOrder;
    private readonly CheckBox? _protectFromLight, _iceTransport;
    private readonly Panel? _colourSwatch;

    // MeasurementUnit
    private readonly TextBox? _symbol, _symbolArabic, _ucum, _factor, _siUnit;

    internal NomenclatureRequest? Request { get; private set; }

    internal NomenclatureEditorDialog(ILocalizer localizer, IUserDialogs dialogs,
                                      NomenclatureKind kind, NomenclatureRow? existing)
        : base(localizer, dialogs,
               existing is null ? "Action.Add" : "Action.Edit",
               localizer[$"NomenclatureKind.{kind}"]
               + (existing is null ? string.Empty : $" — {existing.Code}"),
               620, kind == NomenclatureKind.ContainerType ? 480
                    : kind == NomenclatureKind.SpecimenType ? 420
                    : kind == NomenclatureKind.MeasurementUnit ? 360 : 330)
    {
        _kind = kind;
        _id = existing?.Id;
        _isNew = existing is null;

        var form = CatalogEditorRules.FormOf(existing, CultureInfo.CurrentCulture);

        _code = TextField("Field.Code", 20, 16, 160, form.Code, latin: true, mono: true);
        _code.ReadOnly = !_isNew;
        if (!_isNew) _code.BackColor = UiTheme.SurfaceSunken;
        _code.CharacterCasing = CharacterCasing.Upper;

        Notice(192, 34, 408, 26,
               _isNew ? "Catalog.CodeSetOnce" : "Catalog.CodeImmutableNotice",
               _isNew ? UiTheme.TextSecondary : UiTheme.Abnormal);

        var y = 60;

        if (kind == NomenclatureKind.MeasurementUnit)
        {
            // A unit has no translated name: "mg/dL" is the same in every language, and the Arabic
            // field exists only for the few units Moroccan practice writes in Arabic script.
            _symbol = TextField("Catalog.UnitSymbol", 20, y, 160, form.Symbol,
                           latin: true, mono: true);
            _symbolArabic = TextField("Catalog.UnitSymbolArabic", 192, y, 160,
                                 form.SymbolArabic, arabic: true);
            _ucum = TextField("Catalog.Ucum", 364, y, 160, form.Ucum, latin: true, mono: true);

            y += 48;

            _factor = TextField("Catalog.FactorToSi", 20, y, 160, form.FactorToSi,
                           latin: true, right: true, mono: true);
            Numeric(EditorValues.MeasureMaxLength, _factor);

            _siUnit = TextField("Catalog.SiUnitCode", 192, y, 160, form.SiUnit,
                           latin: true, mono: true);

            y += 48;

            Notice(20, y, 580, 34, "Catalog.ConversionNotice");
            y += 42;

            // The three name fields point at the symbol boxes so the shared validation and the
            // request build need no special case for units.
            _nameFr = _symbol!;
            _nameAr = _symbolArabic!;
            _nameEn = _symbol!;
        }
        else
        {
            _nameFr = TextField("Field.NameFr", 20, y, 180, form.NameFr);
            _nameAr = TextField("Field.NameAr", 212, y, 180, form.NameAr, arabic: true);
            _nameEn = TextField("Field.NameEn", 404, y, 180, form.NameEn);

            y += 48;
        }

        // Each box holds no more than its column: a longer entry otherwise ends in « Erreur inattendue » at the save.
        _code.MaxLength = CatalogEditorRules.CodeMaxLengthOf(kind);
        _nameFr.MaxLength = _nameAr.MaxLength = _nameEn.MaxLength = CatalogEditorRules.NameMaxLengthOf(kind);
        if (_ucum is not null) _ucum.MaxLength = 40;
        if (_siUnit is not null) _siUnit.MaxLength = 30;

        switch (kind)
        {
            case NomenclatureKind.Department:
                _displayOrder = Number("Field.DisplayOrder", 20, y, 100,
                                        form.DisplayOrder, max: CatalogEditorRules.DepartmentOrderMax);

                _accreditationScope = Check("Catalog.InAccreditationScope", 140, y + 20,
                                             form.InAccreditationScope);

                y += 48;

                Notice(20, y, 580, 34, "Catalog.AccreditationScopeNotice");
                y += 42;
                break;

            case NomenclatureKind.SpecimenType:
                _fastingHours = Number("Catalog.FastingHours", 20, y, 100,
                                        form.FastingHours, max: CatalogEditorRules.FastingHoursMax);
                y += 48;

                _collectionInstructions = TextField("Catalog.CollectionInstructions", 20, y, 580,
                                                form.CollectionInstructions);
                _collectionInstructions.MaxLength = 1000;
                y += 48;

                _patientInstructions = TextField("Catalog.PatientInstructions", 20, y, 580,
                                             form.PatientInstructions);
                _patientInstructions.MaxLength = 1000;
                y += 48;

                Notice(20, y, 580, 20, "Catalog.PatientInstructionsNotice");
                y += 28;
                break;

            case NomenclatureKind.ContainerType:
                _capColour = TextField("Catalog.CapColour", 20, y, 110,
                                   form.CapColour, latin: true, mono: true);
                _capColour.MaxLength = 9;

                _colourSwatch = new Panel
                {
                    Bounds = new Rectangle(138, y + 18, 26, UiTheme.InputHeight),
                    BorderStyle = BorderStyle.FixedSingle
                };

                Controls.Add(_colourSwatch);

                _additive = TextField("Catalog.Additive", 176, y, 200, form.Additive);
                _additive.MaxLength = 200;

                _drawOrder = Number("Sample.DrawOrder", 388, y, 90, form.DrawOrder,
                                     max: CatalogEditorRules.DrawOrderMax);

                y += 48;

                _nominalVolume = TextField("Catalog.NominalVolume", 20, y, 110, form.NominalVolume,
                                       latin: true, right: true);
                Numeric(EditorValues.MeasureMaxLength, _nominalVolume);

                _minimumVolume = TextField("Catalog.MinimumVolume", 142, y, 110, form.MinimumVolume,
                                       latin: true, right: true);
                Numeric(EditorValues.MeasureMaxLength, _minimumVolume);

                _protectFromLight = Check("Collection.ProtectFromLight", 264, y + 20, form.ProtectFromLight);

                _iceTransport = Check("Collection.TransportOnIce", 440, y + 20, form.IceTransport);

                y += 48;

                Notice(20, y, 580, 34, "Catalog.DrawOrderNotice");
                y += 42;

                _capColour.TextChanged += (_, _) => RefreshSwatch();
                RefreshSwatch();
                break;
        }

        _active = Check("Field.Active", 20, y, form.Active, bold: true);

        if (existing is { UsageCount: > 0 })
            Notice(160, y - 4, 440, 30, "Catalog.NomenclatureInUseNotice", UiTheme.Abnormal)
                .Text = Localizer.Format("Catalog.NomenclatureInUseNotice", existing.UsageCount);

        Buttons(ClientSize.Height - 54, 620);
        UseAdminFrame();
    }

    /// <summary>The text of a box this dialog may not have created at all: four lists share one form.</summary>
    private static string Optional(TextBox? box) => box?.Text ?? string.Empty;

    /// <summary>The entry as typed, for the rules shared with the web.</summary>
    private NomenclatureForm Form() => new()
    {
        Code = _code.Text,
        NameFr = _kind == NomenclatureKind.MeasurementUnit ? string.Empty : _nameFr.Text,
        NameAr = _kind == NomenclatureKind.MeasurementUnit ? string.Empty : _nameAr.Text,
        NameEn = _kind == NomenclatureKind.MeasurementUnit ? string.Empty : _nameEn.Text,
        Active = _active.Checked,
        DisplayOrder = _displayOrder is null ? 0 : (int)_displayOrder.Value,
        InAccreditationScope = _accreditationScope?.Checked ?? false,
        FastingHours = _fastingHours is null ? 0 : (int)_fastingHours.Value,
        CollectionInstructions = Optional(_collectionInstructions),
        PatientInstructions = Optional(_patientInstructions),
        CapColour = _capColour?.Text ?? "#FFFFFF",
        Additive = Optional(_additive),
        NominalVolume = Optional(_nominalVolume),
        MinimumVolume = Optional(_minimumVolume),
        DrawOrder = _drawOrder is null ? 0 : (int)_drawOrder.Value,
        ProtectFromLight = _protectFromLight?.Checked ?? false,
        IceTransport = _iceTransport?.Checked ?? false,
        Symbol = Optional(_symbol),
        SymbolArabic = Optional(_symbolArabic),
        Ucum = Optional(_ucum),
        FactorToSi = Optional(_factor),
        SiUnit = Optional(_siUnit)
    };

    private void RefreshSwatch()
    {
        if (_capColour is null || _colourSwatch is null) return;

        try
        {
            _colourSwatch.BackColor = ColorTranslator.FromHtml(_capColour.Text.Trim());
        }
        catch (Exception)
        {
            // An unparseable colour shows as the hatched default rather than throwing while the
            // operator is still typing the third character of "#E1".
            _colourSwatch.BackColor = UiTheme.SurfaceSunken;
        }
    }

    protected override void Accept()
    {
        var form = Form();

        if (Refused(CatalogEditorRules.Check(_kind, form), ("factor", _factor), ("nominalvolume", _nominalVolume),
                    ("minimumvolume", _minimumVolume), ("code", _code), ("symbol", _symbol), ("namefr", _nameFr),
                    ("draworder", _drawOrder)))
            return;

        Request = CatalogEditorRules.ToRequest(_kind, form, _id);

        DialogResult = DialogResult.OK;
        Close();
    }
}
