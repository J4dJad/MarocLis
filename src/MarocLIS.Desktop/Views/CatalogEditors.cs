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
    internal AnalysisEditorDialog(ILocalizer localizer, IUserDialogs dialogs,
                                  CatalogLookups lookups, AnalysisRow? existing,
                                  TariffRow? labTariff = null, decimal? knownLetterKeyValue = null,
                                  bool canPrice = false, AnalysisRequest? stored = null)
        : base(localizer, dialogs, existing is null ? "Catalog.NewAnalysis" : "Catalog.EditAnalysis",
               existing?.Code, 760, 764)
    {
        _labTariff = labTariff;
        _canPrice = canPrice;
        _id = existing?.Id;
        _isNew = existing is null;
        _stored = existing is null ? null : stored;

        var language = localizer.CurrentLanguage;

        // --- identity --------------------------------------------------------------------------

        _code = TextField("Field.Code", 20, 16, 140, existing?.Code, latin: true, mono: true);
        _code.ReadOnly = !_isNew;
        if (!_isNew) _code.BackColor = UiTheme.SurfaceSunken;
        _code.CharacterCasing = CharacterCasing.Upper;

        Notice(170, 34, 560, 26,
               _isNew ? "Catalog.CodeSetOnce" : "Catalog.CodeImmutableNotice",
               _isNew ? UiTheme.TextSecondary : UiTheme.Abnormal);

        _nameFr = TextField("Field.NameFr", 20, 60, 340, existing?.Name.Fr);
        _nameAr = TextField("Field.NameAr", 372, 60, 340, existing?.Name.Ar, arabic: true);
        _nameEn = TextField("Field.NameEn", 20, 104, 340, existing?.Name.En);
        _shortName = TextField("Catalog.ShortName", 372, 104, 160, _stored?.ShortName?.Fr);
        _aliases = TextField("Catalog.SearchAliases", 20, 148, 692, _stored?.SearchAliases);

        // --- classification --------------------------------------------------------------------

        _department = Lookup("Field.Department", 20, 192, 220,
                             lookups.Departments.Where(d => d.IsActive)
                                    .Select(d => (d.Id, d.Name.For(language))),
                             existing?.DepartmentId, allowNone: false);

        _specimen = Lookup("Sample.SpecimenType", 252, 192, 220,
                           lookups.SpecimenTypes.Where(s => s.IsActive)
                                  .Select(s => (s.Id, s.Name.For(language))),
                           existing?.SpecimenTypeId, allowNone: false);

        _container = Lookup("Sample.ContainerType", 484, 192, 228,
                            lookups.ContainerTypes.Where(c => c.IsActive)
                                   .Select(c => (c.Id, c.Name.For(language))),
                            existing?.ContainerTypeId);

        _volume = TextField("Catalog.RequiredVolume", 20, 236, 120, Editable(_stored?.RequiredVolumeMl),
                            latin: true, right: true);
        _loinc = TextField("Catalog.Loinc", 152, 236, 160, _stored?.LoincCode, latin: true, mono: true);
        _actCode = TextField("Catalog.NationalActCode", 324, 236, 160, existing?.NationalActCode,
                        latin: true, mono: true);

        _consent = Choice("Catalog.ConsentRequired", 496, 236, 216,
                          existing?.ConsentRequirement ?? ConsentRequirement.None,
                          "ConsentRequirement");

        // --- method ----------------------------------------------------------------------------

        _methodFr = TextField("Catalog.Method", 20, 280, 340, _stored?.Method?.Fr);
        _reagent = TextField("Catalog.ReagentReference", 372, 280, 340, _stored?.ReagentReference);

        _accredited = Check("Catalog.Accredited", 20, 334, existing?.IsAccredited ?? false);
        _subcontracted = Check("Catalog.Subcontracted", 20, 358, existing?.IsSubcontracted ?? false);
        _notifiable = Check("Catalog.Notifiable", 20, 382, _stored?.IsNotifiableDisease ?? false);

        _subcontractor = Lookup("Catalog.SubcontractorSite", 372, 334, 340,
                                lookups.PartnerSites.Select(s => (s.Id, $"{s.Code}  {s.Name.For(language)}")),
                                _stored?.SubcontractorSiteId);

        // The picker follows the tick box: an analysis marked subcontracted with no partner site is
        // refused by the domain, and a laboratory with no partner configured needs to be told that
        // rather than left to guess which field the refusal is about.
        _subcontracted.CheckedChanged += (_, _) => RefreshSubcontracting();

        // --- turnaround ------------------------------------------------------------------------

        _routine = Number("Catalog.RoutineTurnaround", 20, 412, 140,
                          existing?.RoutineTurnaroundHours ?? 24, max: 8760);

        _urgent = Number("Catalog.UrgentTurnaround", 180, 412, 140,
                         _stored?.UrgentTurnaroundMinutes ?? 120, max: 10080);

        _urgentAvailable = Check("Catalog.AvailableUrgently", 340, 432,
                                 _stored?.IsAvailableUrgently ?? true);

        Notice(20, 460, 692, 20, "Catalog.TurnaroundNotice");

        // --- pre-analytical --------------------------------------------------------------------

        _fasting = Number("Catalog.FastingOverride", 20, 488, 140,
                          _stored?.FastingHoursOverride, max: 48);
        _stabilityRoom = Number("Catalog.StabilityRoom", 180, 488, 140,
                                _stored?.StabilityAtRoomTemperatureHours, max: 8760);
        _stabilityFridge = Number("Catalog.StabilityFridge", 340, 488, 140,
                                  _stored?.StabilityRefrigeratedHours, max: 8760);
        _displayOrder = Number("Field.DisplayOrder", 500, 488, 120, _stored?.DisplayOrder ?? 0,
                               max: 9999);

        _clinicalInfo = TextField("Catalog.RequiredClinicalInformation", 20, 540, 692,
                                  _stored?.RequiredClinicalInformation?.Fr);

        // --- price ------------------------------------------------------------------------------
        // The laboratory's own price list (patients paying themselves, and the fallback for any
        // payer without a tariff of its own). Payer tariffs stay on the Tarifs tab.

        Notice(20, 588, 692, 20, "Catalog.LabPriceSection", UiTheme.Accent);

        _letterKey = TextField("Billing.LetterKey", 20, 612, 70, labTariff?.LetterKey ?? "B",
                               latin: true, mono: true);
        _letterKey.CharacterCasing = CharacterCasing.Upper;

        _coefficient = TextField("Billing.Coefficient", 102, 612, 100,
                                 labTariff?.Coefficient.ToString("0.##"),
                                 latin: true, right: true, mono: true);

        _letterKeyValue = TextField("Catalog.LetterKeyValue", 214, 612, 120,
                                    (labTariff is { LetterKeyValue: > 0m }
                                         ? labTariff.LetterKeyValue
                                         : knownLetterKeyValue)?.ToString("0.####"),
                                    latin: true, right: true, mono: true);

        _price = TextField("Catalog.LabPrice", 346, 612, 130,
                           labTariff?.Price.ToString("0.00"),
                           latin: true, right: true, mono: true);

        _computed = Notice(488, 626, 224, 44, "Catalog.TariffComputedNotice", UiTheme.Accent);

        foreach (var box in new[] { _letterKey, _coefficient, _letterKeyValue, _price })
        {
            box.TextChanged += (_, _) => RefreshComputed();

            if (canPrice) continue;
            box.ReadOnly = true;
            box.BackColor = UiTheme.SurfaceSunken;
        }

        RefreshComputed();

        _active = Check("Field.Active", 20, 670, existing?.IsActive ?? true, bold: true);

        // Editing opens on the whole stored analysis (see CatalogMaintenanceService
        // .GetAnalysisForEditAsync), not on the grid row: the row is a summary, and an editor
        // filled from it saved blanks over the LOINC code, the stabilities and the partner site.

        RefreshSubcontracting();

        Buttons(710, 760);
    }

    private void RefreshComputed()
    {
        var reference = Math.Round((DecimalOrNull(_coefficient) ?? 0m)
                                   * (DecimalOrNull(_letterKeyValue) ?? 0m), 2);
        var excess = Math.Max(0m, (DecimalOrNull(_price) ?? 0m) - reference);

        var language = Localizer.CurrentLanguage;

        _computed.ForeColor = excess > 0m ? UiTheme.Abnormal : UiTheme.Accent;
        _computed.Text = Localizer.Format("Catalog.TariffComputedNotice",
                                          Money.Of(reference).Format(language),
                                          Money.Of(excess).Format(language));
    }

    /// <summary>
    /// Reads the price fields. False when they are invalid (the operator has been told); true with
    /// a null request when there is nothing to save.
    /// </summary>
    private bool TryReadPrice(out TariffRequest? request)
    {
        request = null;

        if (!_canPrice) return true;

        var price = DecimalOrNull(_price);

        // Left blank on an analysis that has no price yet: nothing to record.
        if (price is null && string.IsNullOrWhiteSpace(_price.Text) && _labTariff is null) return true;

        if (price is null or < 0m)
        {
            Dialogs.Warn(Localizer["Tariff_NegativePrice"], Text);
            _price.Focus();
            return false;
        }

        if (string.IsNullOrWhiteSpace(_letterKey.Text))
        {
            Dialogs.Warn(Localizer["Tariff_LetterKeyRequired"], Text);
            _letterKey.Focus();
            return false;
        }

        var letterKey = _letterKey.Text.Trim().ToUpperInvariant();
        var coefficient = DecimalOrNull(_coefficient) ?? 0m;
        var letterKeyValue = DecimalOrNull(_letterKeyValue) ?? 0m;

        if (_labTariff is not null
            && _labTariff.LetterKey == letterKey
            && _labTariff.Coefficient == coefficient
            && _labTariff.LetterKeyValue == letterKeyValue
            && _labTariff.Price == price.Value)
            return true;

        request = new TariffRequest
        {
            AnalysisTestId = _id ?? Guid.Empty,
            PayerId = null,
            ValidFrom = DateOnly.MinValue,
            LetterKey = letterKey,
            Coefficient = coefficient,
            LetterKeyValue = letterKeyValue,
            Price = price.Value,
            IsVatExempt = _labTariff?.IsVatExempt ?? true,
            VatRate = _labTariff?.VatRate ?? 0m
        };

        return true;
    }

    private void RefreshSubcontracting()
    {
        _subcontractor.Enabled = _subcontracted.Checked && _subcontractor.Items.Count > 1;

        _subcontractor.BackColor = _subcontractor.Enabled
            ? UiTheme.SurfaceRaised
            : UiTheme.SurfaceSunken;
    }

    protected override void Accept()
    {
        if (string.IsNullOrWhiteSpace(_code.Text))
        {
            Dialogs.Warn(Localizer["Test_CodeRequired"], Text);
            _code.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_nameFr.Text))
        {
            Dialogs.Warn(Localizer["Test_NameFrenchRequired"], Text);
            _nameFr.Focus();
            return;
        }

        if (SelectedId(_department) is null || SelectedId(_specimen) is null)
        {
            Dialogs.Warn(Localizer["Catalog_DepartmentAndSpecimenRequired"], Text);
            return;
        }

        // Refused here rather than by the service, so the operator is told which of the two
        // problems they have: no partner chosen, or no partner site configured at all.
        if (_subcontracted.Checked && SelectedId(_subcontractor) is null)
        {
            Dialogs.Warn(Localizer[_subcontractor.Items.Count > 1
                                       ? "Test_SubcontractorRequired"
                                       : "Catalog_NoPartnerSiteConfigured"],
                          Text);
            return;
        }

        if (!TryReadPrice(out var priceRequest)) return;

        PriceRequest = priceRequest;

        // Built on the stored analysis, so a field this dialog does not show (the execution days)
        // is saved as it was rather than reset.
        var basis = _stored ?? new AnalysisRequest
        {
            Code = string.Empty,
            Name = new MultilingualText(),
            DepartmentId = Guid.Empty,
            SpecimenTypeId = Guid.Empty
        };

        Request = basis with
        {
            Id = _id,
            Code = _code.Text.Trim().ToUpperInvariant(),
            Name = new MultilingualText(_nameFr.Text.Trim(), Blank(_nameAr), Blank(_nameEn)),
            ShortName = FrenchEdited(_stored?.ShortName, Blank(_shortName)),
            SearchAliases = Blank(_aliases),
            DepartmentId = SelectedId(_department)!.Value,
            SpecimenTypeId = SelectedId(_specimen)!.Value,
            ContainerTypeId = SelectedId(_container),
            RequiredVolumeMl = DecimalOrNull(_volume),
            LoincCode = Blank(_loinc),
            NationalActCode = Blank(_actCode),
            Method = FrenchEdited(_stored?.Method, Blank(_methodFr)),
            ReagentReference = Blank(_reagent),
            IsAccredited = _accredited.Checked,
            IsSubcontracted = _subcontracted.Checked,
            SubcontractorSiteId = _subcontracted.Checked ? SelectedId(_subcontractor) : null,
            RoutineTurnaroundHours = (int)_routine.Value,
            UrgentTurnaroundMinutes = (int)_urgent.Value,
            IsAvailableUrgently = _urgentAvailable.Checked,
            FastingHoursOverride = IntOrStored(_fasting, _stored?.FastingHoursOverride),
            StabilityAtRoomTemperatureHours =
                IntOrStored(_stabilityRoom, _stored?.StabilityAtRoomTemperatureHours),
            StabilityRefrigeratedHours =
                IntOrStored(_stabilityFridge, _stored?.StabilityRefrigeratedHours),
            RequiredClinicalInformation =
                FrenchEdited(_stored?.RequiredClinicalInformation, Blank(_clinicalInfo)),
            ConsentRequirement = Selected<ConsentRequirement>(_consent),
            IsNotifiableDisease = _notifiable.Checked,
            IsActive = _active.Checked,
            DisplayOrder = (int)_displayOrder.Value
        };

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

        var language = localizer.CurrentLanguage;

        _code = TextField("Field.Code", 20, 16, 140, existing?.Code, latin: true, mono: true);
        _code.ReadOnly = !_isNew;
        if (!_isNew) _code.BackColor = UiTheme.SurfaceSunken;
        _code.CharacterCasing = CharacterCasing.Upper;

        _valueType = Choice("Catalog.ValueType", 172, 16, 200,
                            existing?.ValueType ?? ResultValueType.Numeric, "ResultValueType");

        _valueType.Enabled = existing is null || !existing.HasResults;

        _unit = Lookup("Field.Unit", 384, 16, 160,
                       lookups.Units.Where(u => u.IsActive)
                              .Select(u => (u.Id, u.DisplaySymbol)),
                       existing?.UnitId);

        _decimals = Number("Catalog.Decimals", 556, 16, 80, existing?.Decimals ?? 2, max: 6);

        _typeNotice = Notice(20, 62, 712, 20,
                             existing is { HasResults: true }
                                 ? "Catalog.ValueTypeFrozenNotice"
                                 : "Catalog.ValueTypeNotice",
                             existing is { HasResults: true } ? UiTheme.Abnormal
                                                               : UiTheme.TextSecondary);

        if (existing is { HasResults: true })
            _typeNotice.Text = localizer.Format("Catalog.ValueTypeFrozenNotice",
                                                 existing.ResultCount);

        _nameFr = TextField("Field.NameFr", 20, 86, 230, existing?.Name.Fr);
        _nameAr = TextField("Field.NameAr", 262, 86, 230, existing?.Name.Ar, arabic: true);
        _nameEn = TextField("Field.NameEn", 504, 86, 228, existing?.Name.En);

        _shortName = TextField("Catalog.ShortName", 20, 130, 160, _stored?.ShortName?.Fr);
        _loinc = TextField("Catalog.Loinc", 192, 130, 160, _stored?.LoincCode,
                           latin: true, mono: true);
        _cv = TextField("Catalog.AnalyticalCv", 364, 130, 100,
                   existing?.AnalyticalCvPercent?.ToString("0.##"), latin: true, right: true);

        _displayOrder = Number("Field.DisplayOrder", 476, 130, 100,
                               existing?.DisplayOrder ?? 0, max: 9999);
        _indent = Number("Catalog.IndentLevel", 588, 130, 80, existing?.IndentLevel ?? 0, max: 5);

        // --- limits ----------------------------------------------------------------------------

        Label("Catalog.MeasuringRange", 20, 182);
        _measuringLow = Bare(20, 200, 100, existing?.MeasuringRangeLow);
        _measuringHigh = Bare(128, 200, 100, existing?.MeasuringRangeHigh);

        Label("Catalog.TechnicalLimits", 244, 182);
        _technicalLow = Bare(244, 200, 100, existing?.TechnicalLimitLow);
        _technicalHigh = Bare(352, 200, 100, existing?.TechnicalLimitHigh);

        Label("Catalog.CriticalLimits", 468, 182);
        _criticalLow = Bare(468, 200, 100, existing?.CriticalLow);
        _criticalHigh = Bare(576, 200, 100, existing?.CriticalHigh);

        Notice(20, 234, 712, 26, "Catalog.LimitsNotice");

        // --- delta check -----------------------------------------------------------------------

        _delta = TextField("Catalog.DeltaCheck", 20, 262, 100,
                      existing?.DeltaCheckPercent?.ToString("0.##"), latin: true, right: true);

        _deltaWindow = Number("Catalog.DeltaWindow", 132, 262, 100,
                              existing?.DeltaCheckWindowDays, max: 3650);

        Notice(244, 280, 488, 34, "Catalog.DeltaNotice");

        // --- formula ---------------------------------------------------------------------------

        _formula = TextField("Catalog.Formula", 20, 320, 712, existing?.CalculationFormula,
                        latin: true, mono: true);

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
        foreach (var option in _stored?.CodedOptions ?? [])
            _options.Rows.Add(option.Code, option.Label.Fr, option.Label.Ar ?? string.Empty,
                              option.IsExpectedNormal, option.IsAbnormal, option.IsCritical);

        Controls.Add(_options);

        _addOption = UiTheme.SecondaryButton(localizer["Catalog.AddNegativePositive"]);
        _addOption.Bounds = new Rectangle(20, 580, 220, UiTheme.ButtonHeight - 4);
        _addOption.Click += (_, _) => AddPreset();

        _removeOption = UiTheme.SecondaryButton(localizer["Action.Remove"]);
        _removeOption.Bounds = new Rectangle(250, 580, 140, UiTheme.ButtonHeight - 4);
        _removeOption.Click += (_, _) => RemoveOption();

        _internalOnly = Check("Catalog.InternalOnly", 404, 584, existing?.IsInternalOnly ?? false);
        _alwaysShow = Check("Catalog.AlwaysShowOnReport", 556, 584,
                            existing?.AlwaysShowOnReport ?? true);
        _active = Check("Field.Active", 20, 616, existing?.IsActive ?? true, bold: true);

        Controls.AddRange([_addOption, _removeOption]);

        _valueType.SelectedIndexChanged += (_, _) => RefreshEnablement();

        RefreshEnablement();

        Buttons(644, 780);
    }

    private TextBox Bare(int x, int y, int width, decimal? value)
    {
        var box = UiTheme.Input(width);
        box.Location = new Point(x, y);
        box.Tag = UiTags.LatinValue;
        box.TextAlign = HorizontalAlignment.Right;
        box.Font = UiTheme.Monospace;
        // Through the editing format: the culture's own text for a negative limit carries a
        // direction mark in Arabic, and "0.###" also rounded the stored value on the next save.
        box.Text = value is { } number ? EditorValues.FormatForEditing(number, 0) : string.Empty;

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

        _options.Rows.Add("NEG", Localizer["Catalog.OptionNegative"], "سالب", true, false, false);
        _options.Rows.Add("POS", Localizer["Catalog.OptionPositive"], "موجب", false, true, false);
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
        var type = Selected<ResultValueType>(_valueType);

        var numeric = type is ResultValueType.Numeric or ResultValueType.NumericWithOperator;
        var coded = type is ResultValueType.Coded or ResultValueType.SemiQuantitative;
        var heading = type == ResultValueType.Heading;

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

    protected override void Accept()
    {
        var type = Selected<ResultValueType>(_valueType);

        if (string.IsNullOrWhiteSpace(_code.Text))
        {
            Dialogs.Warn(Localizer["Parameter_CodeRequired"], Text);
            _code.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_nameFr.Text))
        {
            Dialogs.Warn(Localizer["Parameter_NameFrenchRequired"], Text);
            _nameFr.Focus();
            return;
        }

        var options = ReadOptions();

        if (type is ResultValueType.Coded or ResultValueType.SemiQuantitative && options.Count == 0)
        {
            Dialogs.Warn(Localizer["Parameter_CodedNeedsOptions"], Text);
            return;
        }

        // Checked here as well as in the service so the operator is told which code is wrong while
        // the formula is still in front of them.
        if (Blank(_formula) is { } formula)
        {
            var known = _siblings.Select(s => s.Code)
                                 .Append(_code.Text.Trim().ToUpperInvariant())
                                 .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var unknown = System.Text.RegularExpressions.Regex
                .Matches(formula, @"\[([A-Za-z0-9_]+)\]")
                .Select(m => m.Groups[1].Value)
                .Where(code => !known.Contains(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (unknown.Count > 0)
            {
                Dialogs.Warn(
                    Localizer.Format("Parameter_FormulaUnknownReference", string.Join(", ", unknown)),
                    Text);
                _formula.Focus();
                return;
            }
        }

        // Built on the stored parameter, so a field this dialog does not show is saved as it was.
        var basis = _stored ?? new ParameterRequest
        {
            AnalysisTestId = _analysisId,
            Code = string.Empty,
            Name = new MultilingualText(),
            ValueType = type
        };

        Request = basis with
        {
            Id = _id,
            AnalysisTestId = _analysisId,
            Code = _code.Text.Trim().ToUpperInvariant(),
            Name = new MultilingualText(_nameFr.Text.Trim(), Blank(_nameAr), Blank(_nameEn)),
            ShortName = FrenchEdited(_stored?.ShortName, Blank(_shortName)),
            ValueType = type,
            LoincCode = Blank(_loinc),
            UnitId = SelectedId(_unit),
            Decimals = (int)_decimals.Value,
            MeasuringRangeLow = DecimalOrNull(_measuringLow),
            MeasuringRangeHigh = DecimalOrNull(_measuringHigh),
            TechnicalLimitLow = DecimalOrNull(_technicalLow),
            TechnicalLimitHigh = DecimalOrNull(_technicalHigh),
            CriticalLow = DecimalOrNull(_criticalLow),
            CriticalHigh = DecimalOrNull(_criticalHigh),
            AnalyticalCvPercent = DecimalOrNull(_cv),
            DeltaCheckPercent = DecimalOrNull(_delta),
            DeltaCheckWindowDays = IntOrNull(_deltaWindow),
            CalculationFormula = Blank(_formula),
            IsInternalOnly = _internalOnly.Checked,
            AlwaysShowOnReport = _alwaysShow.Checked,
            DisplayOrder = (int)_displayOrder.Value,
            IndentLevel = (int)_indent.Value,
            IsActive = _active.Checked,
            CodedOptions = options
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    private List<CodedOptionRequest> ReadOptions()
    {
        var options = new List<CodedOptionRequest>();
        var order = 1;

        foreach (DataGridViewRow row in _options.Rows)
        {
            if (row.IsNewRow) continue;

            var code = row.Cells["code"].Value?.ToString();
            var label = row.Cells["label"].Value?.ToString();

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(label)) continue;

            var arabic = row.Cells["labelAr"].Value?.ToString();
            code = code.Trim().ToUpperInvariant();

            var text = new MultilingualText(label.Trim(),
                                            string.IsNullOrWhiteSpace(arabic) ? null : arabic.Trim());

            // The grid has no English column: a stored answer whose French and Arabic are unchanged
            // keeps its English label rather than losing it on save.
            if (_stored?.CodedOptions.FirstOrDefault(
                    o => string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase)) is { } stored
                && stored.Label.Fr == text.Fr && stored.Label.Ar == text.Ar)
                text = stored.Label;

            options.Add(new CodedOptionRequest(
                code,
                text,
                order++,
                row.Cells["abnormal"].Value is true,
                row.Cells["critical"].Value is true,
                row.Cells["normal"].Value is true));
        }

        return options;
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

        _gender = Choice("Field.Gender", 20, 16, 180, existing?.AppliesToGender ?? Gender.Unknown,
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

        _pregnancy.SelectedIndex = existing?.AppliesToPregnancy switch
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

        _trimester.SelectedIndex = existing?.PregnancyTrimester ?? 0;

        Controls.Add(_trimester);

        // --- age band ---------------------------------------------------------------------------

        Label("Catalog.AgeBand", 20, 74);

        _ageFrom = new NumericUpDown
        {
            Bounds = new Rectangle(20, 92, 100, UiTheme.InputHeight), Maximum = 40_000
        };

        _ageTo = new NumericUpDown
        {
            Bounds = new Rectangle(128, 92, 100, UiTheme.InputHeight), Maximum = 40_000
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
        _ageUnit.Items.Add(new EnumChoice(1, localizer["Catalog.Days"]));
        _ageUnit.Items.Add(new EnumChoice(30, localizer["Catalog.Months"]));
        _ageUnit.Items.Add(new EnumChoice(365, localizer["Catalog.Years"]));
        _ageUnit.SelectedIndex = 2;

        if (existing is not null)
        {
            // The unit it was typed in; for a band saved before the unit was kept, the largest
            // unit that holds both bounds exactly, or the band would be re-saved truncated.
            var divisor = existing.AgeUnitDays is { } typed and (1 or 30 or 365)
                          && (existing.AgeFromDays ?? 0) % typed == 0
                          && (existing.AgeToDays ?? 0) % typed == 0
                ? typed
                : EditorValues.AgeUnitDays(existing.AgeFromDays, existing.AgeToDays);

            _ageUnit.SelectedIndex = divisor switch { 1 => 0, 30 => 1, _ => 2 };
            _ageFrom.Value = (existing.AgeFromDays ?? 0) / divisor;
            _ageTo.Value = (existing.AgeToDays ?? 0) / divisor;

            // Saved before the unit was kept: shown in the unit it is read in, which may not be
            // the one it was written in. Saving confirms the unit chosen here.
            if (existing.HasUnconfirmedAgeUnit)
                Notice(20, 118, 356, 36, "Catalog.AgeUnitToConfirmNotice", UiTheme.Abnormal);
        }

        Controls.AddRange([_ageFrom, _ageTo, _ageUnit]);

        _instrument = Lookup("Result.Instrument", 388, 74, 280,
                             lookups.Instruments.Select(
                                 i => (i.Id, $"{i.Code}  {i.Name.For(localizer.CurrentLanguage)}")),
                             existing?.InstrumentId);

        Notice(388, 118, 280, 38, "Catalog.InstrumentRangeNotice");

        // --- the interval -----------------------------------------------------------------------

        Label("Result.ReferenceRange", 20, 158);
        _lower = Bare(20, 176, 120, existing?.LowerLimit, parameter.Decimals);
        _upper = Bare(148, 176, 120, existing?.UpperLimit, parameter.Decimals);

        _expectedCoded = TextField("Catalog.ExpectedCodedValue", 284, 158, 180,
                              existing?.ExpectedCodedValue);

        _decisionThreshold = Check("Catalog.DecisionThreshold", 480, 176,
                                   existing?.IsDecisionThreshold ?? false);

        _displayFr = TextField("Catalog.PrintedInterval", 20, 212, 320,
                          existing?.DisplayText?.Fr);
        _displayAr = TextField("Field.NameAr", 352, 212, 316, existing?.DisplayText?.Ar, arabic: true);

        Notice(20, 256, 648, 20, "Catalog.PrintedIntervalNotice");

        // --- provenance and validity -------------------------------------------------------------

        _source = TextField("Catalog.RangeSource", 20, 284, 400, existing?.Source);

        _reviewed = OptionalDate("Catalog.LastReviewed", 436, 302, existing?.LastReviewedOn,
                                  max: today.ToDateTime(TimeOnly.MinValue));

        _validFrom = OptionalDate("Catalog.ValidFrom", 20, 350, existing?.ValidFrom ?? today);
        _validFrom.Toggle.Checked = true;
        _validFrom.Toggle.Enabled = false;
        _validFrom.Picker.Enabled = true;

        _validUntil = OptionalDate("Catalog.ValidUntil", 236, 350, existing?.ValidUntil);

        _active = Check("Field.Active", 452, 352, existing?.IsActive ?? true, bold: true);

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
    }

    private TextBox Bare(int x, int y, int width, decimal? value, int decimals)
    {
        var box = UiTheme.Input(width);
        box.Location = new Point(x, y);
        box.Tag = UiTags.LatinValue;
        box.TextAlign = HorizontalAlignment.Right;
        box.Font = UiTheme.Monospace;
        // No thousands separator: the box is read back as typed, and "150 000" did not survive it.
        box.Text = value is { } number ? EditorValues.FormatForEditing(number, decimals) : string.Empty;

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

    private void RefreshSpecificity()
    {
        var score = (Trimester is not null ? 8 : 0)
                    + (Pregnancy is not null ? 4 : 0)
                    + (SelectedId(_instrument) is not null ? 3 : 0)
                    + (Selected<Gender>(_gender) != Gender.Unknown ? 2 : 0)
                    + (_ageFrom.Value > 0 || _ageTo.Value > 0 ? 1 : 0);

        _specificity.Text = Localizer.Format("Catalog.SpecificityNotice", score);
    }

    protected override void Accept()
    {
        var lower = DecimalOrNull(_lower);
        var upper = DecimalOrNull(_upper);

        if (lower is null && upper is null
            && string.IsNullOrWhiteSpace(_expectedCoded.Text)
            && string.IsNullOrWhiteSpace(_displayFr.Text))
        {
            Dialogs.Warn(Localizer["Range_EmptyInterval"], Text);
            _lower.Focus();
            return;
        }

        if (lower is { } low && upper is { } high && low > high)
        {
            Dialogs.Warn(Localizer["Range_LowerAboveUpper"], Text);
            _lower.Focus();
            return;
        }

        var unit = AgeUnitDays;

        var ageFrom = _ageFrom.Value > 0 ? (int)_ageFrom.Value * unit : (int?)null;
        var ageTo = _ageTo.Value > 0 ? (int)_ageTo.Value * unit : (int?)null;

        if (ageFrom is { } from && ageTo is { } to && from >= to)
        {
            Dialogs.Warn(Localizer["Range_InvalidAgeBand"], Text);
            _ageFrom.Focus();
            return;
        }

        Request = new ReferenceRangeRequest
        {
            Id = _id,
            TestParameterId = _parameterId,
            AppliesToGender = Selected<Gender>(_gender),
            AgeFromDays = ageFrom,
            AgeToDays = ageTo,
            AgeUnitDays = unit,
            AppliesToPregnancy = Pregnancy,
            PregnancyTrimester = Trimester,
            InstrumentId = SelectedId(_instrument),
            LowerLimit = lower,
            UpperLimit = upper,
            DisplayText = Blank(_displayFr) is { } printed
                ? new MultilingualText(printed, Blank(_displayAr)) : null,
            ExpectedCodedValue = Blank(_expectedCoded),
            IsDecisionThreshold = _decisionThreshold.Checked,
            Source = Blank(_source),
            LastReviewedOn = DateOrNull(_reviewed.Toggle, _reviewed.Picker),
            ValidFrom = DateOnly.FromDateTime(_validFrom.Picker.Value.Date),
            ValidUntil = DateOrNull(_validUntil.Toggle, _validUntil.Picker),
            IsActive = _active.Checked
        };

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

        // Every active payer, plus any this analysis already has a tariff for (one since switched
        // off still has its history here). Offering only the latter meant the first tariff for a
        // payer could never be entered.
        var language = localizer.CurrentLanguage;
        var payers = payerOptions
            .Select(p => (p.Id, $"{p.Code} — {p.Name.For(language)}"))
            .Concat(existing.Where(t => t.PayerId is not null
                                        && payerOptions.All(p => p.Id != t.PayerId))
                            .GroupBy(t => t.PayerId!.Value)
                            .Select(g => (g.Key, g.First().PayerName ?? g.Key.ToString())))
            .ToList();

        _payer = Lookup("Coverage.Payer", 20, 16, 280, payers, editing?.PayerId);
        _payer.Items[0] = new LookupChoice(null, localizer["Catalog.DefaultPriceList"]);
        if (editing is { PayerId: null }) _payer.SelectedIndex = 0;

        Label("Catalog.ValidFrom", 312, 16);

        _validFrom = new DateTimePicker
        {
            Bounds = new Rectangle(312, 34, 180, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            Value = (editing?.ValidFrom ?? today).ToDateTime(TimeOnly.MinValue)
        };

        Controls.Add(_validFrom);

        _currentTariff = Notice(20, 66, 580, 34, "Catalog.NoCurrentTariff", UiTheme.TextSecondary);

        // The letter-key value is national, the same for every analysis: it is taken from the
        // most recent tariff that has one, so a new tariff does not start with a reimbursement
        // base of zero and the whole price shown as excess.
        var knownLetterKeyValue = editing?.LetterKeyValue
            ?? existing.Where(t => t.LetterKeyValue > 0m)
                       .OrderByDescending(t => t.ValidFrom)
                       .Select(t => (decimal?)t.LetterKeyValue)
                       .FirstOrDefault()
            ?? nationalLetterKeyValue;

        _letterKey = TextField("Billing.LetterKey", 20, 110, 80, editing?.LetterKey ?? "B",
                               latin: true, mono: true);
        _letterKey.CharacterCasing = CharacterCasing.Upper;

        _coefficient = TextField("Billing.Coefficient", 112, 110, 120,
                            (editing?.Coefficient ?? analysis.CurrentCoefficient)?.ToString("0.##"),
                            latin: true, right: true, mono: true);

        _letterKeyValue = TextField("Catalog.LetterKeyValue", 244, 110, 120,
                               knownLetterKeyValue?.ToString("0.####"),
                               latin: true, right: true, mono: true);

        _price = TextField("Field.Price", 376, 110, 140,
                      (editing?.Price ?? analysis.CurrentPrice)?.ToString("0.00"),
                      latin: true, right: true, mono: true);

        _computed = Notice(20, 158, 580, 44, "Catalog.TariffComputedNotice", UiTheme.Accent);

        // The rate box sits clear of the check box's caption, which is long and bold.
        _vatExempt = Check("Billing.VatExempt", 20, 214, editing?.IsVatExempt ?? true, bold: true);
        _vatRate = TextField("Billing.Vat", 440, 206, 100,
                             editing is { IsVatExempt: false } ? editing.VatRate.ToString("0.####") : null,
                             latin: true, right: true);

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
        var payerId = SelectedId(_payer);

        var current = _editing
                      ?? _existing.FirstOrDefault(t => t.PayerId == payerId && t.ValidUntil is null);

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

        if (_editing is null
            && _validFrom.Value.Date <= current.ValidFrom.ToDateTime(TimeOnly.MinValue))
            _validFrom.Value = current.ValidFrom.AddDays(1).ToDateTime(TimeOnly.MinValue);
    }

    private void RefreshComputed()
    {
        var coefficient = DecimalOrNull(_coefficient) ?? 0m;
        var letterKeyValue = DecimalOrNull(_letterKeyValue) ?? 0m;
        var price = DecimalOrNull(_price) ?? 0m;

        var reference = Math.Round(coefficient * letterKeyValue, 2);
        var excess = Math.Max(0m, price - reference);

        var language = Localizer.CurrentLanguage;

        _computed.ForeColor = excess > 0m ? UiTheme.Abnormal : UiTheme.Accent;

        _computed.Text = Localizer.Format(
            "Catalog.TariffComputedNotice",
            Money.Of(reference).Format(language),
            Money.Of(excess).Format(language));
    }

    protected override void Accept()
    {
        var price = DecimalOrNull(_price);

        if (price is null or < 0m)
        {
            Dialogs.Warn(Localizer["Tariff_NegativePrice"], Text);
            _price.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_letterKey.Text))
        {
            Dialogs.Warn(Localizer["Tariff_LetterKeyRequired"], Text);
            _letterKey.Focus();
            return;
        }

        if (!_vatExempt.Checked && DecimalOrNull(_vatRate) is null or <= 0m)
        {
            Dialogs.Warn(Localizer["Tariff_VatRateRequired"], Text);
            _vatRate.Focus();
            return;
        }

        Request = new TariffRequest
        {
            AnalysisTestId = _analysisId,
            PayerId = SelectedId(_payer),
            ValidFrom = DateOnly.FromDateTime(_validFrom.Value.Date),
            LetterKey = _letterKey.Text.Trim().ToUpperInvariant(),
            Coefficient = DecimalOrNull(_coefficient) ?? 0m,
            LetterKeyValue = DecimalOrNull(_letterKeyValue) ?? 0m,
            Price = price.Value,
            IsVatExempt = _vatExempt.Checked,
            VatRate = _vatExempt.Checked ? 0m : DecimalOrNull(_vatRate) ?? 0m
        };

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
        _available = [.. candidates.Where(c => c.IsOrderable)];

        var language = localizer.CurrentLanguage;

        _code = TextField("Field.Code", 20, 16, 140, existing?.Code, latin: true, mono: true);
        _code.ReadOnly = !_isNew;
        if (!_isNew) _code.BackColor = UiTheme.SurfaceSunken;
        _code.CharacterCasing = CharacterCasing.Upper;

        _nameFr = TextField("Field.NameFr", 172, 16, 240, existing?.Name.Fr);
        _nameAr = TextField("Field.NameAr", 424, 16, 160, existing?.Name.Ar, arabic: true);
        _nameEn = TextField("Field.NameEn", 596, 16, 160, existing?.Name.En);

        _aliases = TextField("Catalog.SearchAliases", 20, 60, 392, null);

        _packagePrice = TextField("Catalog.PackagePrice", 424, 60, 160,
                             existing?.PackagePrice?.ToString("0.00"),
                             latin: true, right: true, mono: true);

        _displayOrder = Number("Field.DisplayOrder", 596, 60, 100,
                               existing?.DisplayOrder ?? 0, max: 9999);

        _itemize = Check("Catalog.ItemizeOnInvoice", 20, 108, existing?.ItemizeOnInvoice ?? true);
        _groupOnReport = Check("Catalog.GroupOnReport", 260, 108, existing?.GroupOnReport ?? false);
        _active = Check("Field.Active", 500, 108, existing?.IsActive ?? true, bold: true);

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
            Name = "optional", HeaderText = localizer["Catalog.Optional"], FillWeight = 10
        });

        foreach (var (name, header, weight, font, right) in new[]
                 {
                     ("code", localizer["Field.Code"], 14, UiTheme.MonospaceBold, false),
                     ("name", localizer["Field.Name"], 38, UiTheme.Body, false),
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
            var member = existing?.Members.FirstOrDefault(m => m.AnalysisTestId == candidate.Id);

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
    }

    private void RefreshTotal()
    {
        var language = Localizer.CurrentLanguage;

        var total = 0m;
        var count = 0;

        foreach (DataGridViewRow row in _candidates.Rows)
        {
            if (row.Cells["included"].Value is not true) continue;
            if (row.Tag is not AnalysisRow analysis) continue;

            count++;

            // The optional members are excluded from the total: they are not added when the panel
            // is selected, so they are not part of what the bundle costs.
            if (row.Cells["optional"].Value is true) continue;

            total += analysis.CurrentPrice ?? 0m;
        }

        var package = DecimalOrNull(_packagePrice);

        _total.ForeColor = package is { } price && price > total ? UiTheme.Critical : UiTheme.Accent;

        _total.Text = Localizer.Format("Catalog.ComponentTotalNotice", count,
                                        Money.Of(total).Format(language),
                                        package is { } value
                                            ? Money.Of(value).Format(language)
                                            : "—");
    }

    protected override void Accept()
    {
        if (string.IsNullOrWhiteSpace(_code.Text))
        {
            Dialogs.Warn(Localizer["Panel_CodeRequired"], Text);
            _code.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_nameFr.Text))
        {
            Dialogs.Warn(Localizer["Panel_NameFrenchRequired"], Text);
            _nameFr.Focus();
            return;
        }

        var members = new List<PanelMemberRequest>();

        foreach (DataGridViewRow row in _candidates.Rows)
        {
            if (row.Cells["included"].Value is not true) continue;
            if (row.Tag is not AnalysisRow analysis) continue;

            members.Add(new PanelMemberRequest(analysis.Id, row.Cells["optional"].Value is true));
        }

        if (members.Count == 0)
        {
            Dialogs.Warn(Localizer["Panel_AtLeastOneAnalysisRequired"], Text);
            return;
        }

        if (members.All(m => m.IsOptional))
        {
            Dialogs.Warn(Localizer["Panel_AllMembersOptional"], Text);
            return;
        }

        Request = new PanelRequest
        {
            Id = _id,
            Code = _code.Text.Trim().ToUpperInvariant(),
            Name = new MultilingualText(_nameFr.Text.Trim(), Blank(_nameAr), Blank(_nameEn)),
            SearchAliases = Blank(_aliases),
            PackagePrice = DecimalOrNull(_packagePrice),
            ItemizeOnInvoice = _itemize.Checked,
            GroupOnReport = _groupOnReport.Checked,
            DisplayOrder = (int)_displayOrder.Value,
            IsActive = _active.Checked,
            Members = members
        };

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

        var options = analyses.Select(a => (a.Id, $"{a.Code}  {a.Name.For(language)}")).ToList();

        _triggerAnalysis = Lookup("Catalog.TriggerAnalysis", 20, 16, 300, options,
                                   existing is null
                                       ? null
                                       : analyses.FirstOrDefault(
                                             a => a.Code == existing.TriggerAnalysisCode)?.Id,
                                   allowNone: false);

        _triggerParameter = Lookup("Catalog.TriggerParameter", 332, 16, 288, [], null,
                                    allowNone: false);

        _condition = Choice("Catalog.ReflexCondition", 20, 64, 220,
                            existing?.Condition ?? ReflexCondition.GreaterThan, "ReflexCondition");

        _threshold = TextField("Catalog.Threshold", 252, 64, 120,
                          existing?.Threshold?.ToString("0.###"),
                          latin: true, right: true, mono: true);

        _thresholdUpper = TextField("Catalog.ThresholdUpper", 384, 64, 120,
                               existing?.ThresholdUpper?.ToString("0.###"),
                               latin: true, right: true, mono: true);

        _codedValue = TextField("Catalog.TriggerCodedValue", 516, 64, 104,
                           existing?.TriggerCodedValue, latin: true, mono: true);

        _conditionNotice = Notice(20, 112, 600, 34, "Catalog.ReflexConditionNotice");

        _target = Lookup("Catalog.ReflexAdds", 20, 154, 600, options,
                         existing is null
                             ? null
                             : analyses.FirstOrDefault(
                                   a => a.Code == existing.AnalysisToAddCode)?.Id,
                         allowNone: false);

        _descriptionFr = TextField("Field.DescriptionFr", 20, 202, 300, existing?.Description.Fr);
        _descriptionAr = TextField("Field.DescriptionAr", 332, 202, 288, existing?.Description.Ar,
                              arabic: true);

        _automatic = Check("Catalog.AddAutomatically", 20, 250,
                           existing?.AddAutomatically ?? false);

        _billable = Check("Catalog.ReflexBillable", 260, 250, existing?.IsBillable ?? true);
        _active = Check("Field.Active", 480, 250, existing?.IsActive ?? true, bold: true);

        Notice(20, 278, 600, 44, "Catalog.ReflexBillingNotice", UiTheme.Abnormal);
        Notice(20, 330, 600, 44, "Catalog.ReflexAutomaticNotice");

        _condition.SelectedIndexChanged += (_, _) => RefreshCondition();
        _triggerAnalysis.SelectedIndexChanged += async (_, _) => await LoadParametersAsync();

        _triggerParameter.SelectedIndexChanged += (_, _) =>
            _selectedParameterId = SelectedId(_triggerParameter) ?? Guid.Empty;

        RefreshCondition();

        Shown += async (_, _) => await LoadParametersAsync(existing?.TriggerParameterId);

        Buttons(440, 660);
    }

    private async Task LoadParametersAsync(Guid? preselect = null)
    {
        if (SelectedId(_triggerAnalysis) is not { } analysisId) return;

        var result = await _catalog.GetParametersAsync(analysisId);

        if (result.IsFailure) return;

        var language = Localizer.CurrentLanguage;

        _triggerParameter.Items.Clear();

        // Headings carry no result and cannot trigger anything, so they are not offered.
        foreach (var parameter in result.Value.Where(
                     p => p.IsActive && p.ValueType != ResultValueType.Heading))
        {
            var index = _triggerParameter.Items.Add(
                new LookupChoice(parameter.Id,
                                  $"{parameter.Code}  {parameter.Name.For(language)}"));

            if (parameter.Id == (preselect ?? _selectedParameterId))
                _triggerParameter.SelectedIndex = index;
        }

        if (_triggerParameter.SelectedIndex < 0 && _triggerParameter.Items.Count > 0)
            _triggerParameter.SelectedIndex = 0;

        _selectedParameterId = SelectedId(_triggerParameter) ?? Guid.Empty;
    }

    private void RefreshCondition()
    {
        var condition = Selected<ReflexCondition>(_condition);

        var numeric = condition != ReflexCondition.EqualsCoded;
        var twoBounds = condition is ReflexCondition.Between or ReflexCondition.Outside;

        Enable(_threshold, numeric);
        Enable(_thresholdUpper, twoBounds);
        Enable(_codedValue, !numeric);

        _conditionNotice.Text = Localizer[condition switch
        {
            ReflexCondition.GreaterThan => "Catalog.ReflexGreaterNotice",
            ReflexCondition.LessThan => "Catalog.ReflexLessNotice",
            ReflexCondition.Between => "Catalog.ReflexBetweenNotice",
            ReflexCondition.Outside => "Catalog.ReflexOutsideNotice",
            _ => "Catalog.ReflexCodedNotice"
        }];

        static void Enable(TextBox box, bool enabled)
        {
            box.Enabled = enabled;
            box.BackColor = enabled ? UiTheme.SurfaceRaised : UiTheme.SurfaceSunken;
            if (!enabled) box.Text = string.Empty;
        }
    }

    protected override void Accept()
    {
        var condition = Selected<ReflexCondition>(_condition);

        if (_selectedParameterId == Guid.Empty)
        {
            Dialogs.Warn(Localizer["Catalog_TriggerParameterRequired"], Text);
            return;
        }

        if (SelectedId(_target) is not { } target)
        {
            Dialogs.Warn(Localizer["Catalog_ReflexTargetRequired"], Text);
            return;
        }

        if (string.IsNullOrWhiteSpace(_descriptionFr.Text))
        {
            Dialogs.Warn(Localizer["Reflex_DescriptionRequired"], Text);
            _descriptionFr.Focus();
            return;
        }

        var threshold = DecimalOrNull(_threshold);
        var upper = DecimalOrNull(_thresholdUpper);

        switch (condition)
        {
            case ReflexCondition.GreaterThan or ReflexCondition.LessThan when threshold is null:
                Dialogs.Warn(Localizer["Reflex_ThresholdRequired"], Text);
                _threshold.Focus();
                return;

            case ReflexCondition.Between or ReflexCondition.Outside
                when threshold is null || upper is null:
                Dialogs.Warn(Localizer["Reflex_TwoThresholdsRequired"], Text);
                _threshold.Focus();
                return;

            case ReflexCondition.EqualsCoded when string.IsNullOrWhiteSpace(_codedValue.Text):
                Dialogs.Warn(Localizer["Reflex_CodedValueRequired"], Text);
                _codedValue.Focus();
                return;
        }

        Request = new ReflexRuleRequest
        {
            Id = _id,
            TriggerParameterId = _selectedParameterId,
            AnalysisToAddId = target,
            Description = new MultilingualText(_descriptionFr.Text.Trim(), Blank(_descriptionAr)),
            Condition = condition,
            Threshold = threshold,
            ThresholdUpper = upper,
            TriggerCodedValue = Blank(_codedValue),
            AddAutomatically = _automatic.Checked,
            IsBillable = _billable.Checked,
            IsActive = _active.Checked
        };

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

        _code = TextField("Field.Code", 20, 16, 160, existing?.Code, latin: true, mono: true);
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
            _symbol = TextField("Catalog.UnitSymbol", 20, y, 160, existing?.DisplaySymbol,
                           latin: true, mono: true);
            _symbolArabic = TextField("Catalog.UnitSymbolArabic", 192, y, 160,
                                 existing?.DisplaySymbolArabic, arabic: true);
            _ucum = TextField("Catalog.Ucum", 364, y, 160, existing?.UcumCode, latin: true, mono: true);

            y += 48;

            _factor = TextField("Catalog.FactorToSi", 20, y, 160,
                           existing?.FactorToSi?.ToString("0.######"),
                           latin: true, right: true, mono: true);

            _siUnit = TextField("Catalog.SiUnitCode", 192, y, 160, existing?.SiUnitCode,
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
            _nameFr = TextField("Field.NameFr", 20, y, 180, existing?.Name.Fr);
            _nameAr = TextField("Field.NameAr", 212, y, 180, existing?.Name.Ar, arabic: true);
            _nameEn = TextField("Field.NameEn", 404, y, 180, existing?.Name.En);

            y += 48;
        }

        switch (kind)
        {
            case NomenclatureKind.Department:
                _displayOrder = Number("Field.DisplayOrder", 20, y, 100,
                                        existing?.DisplayOrder ?? 0, max: 999);

                _accreditationScope = Check("Catalog.InAccreditationScope", 140, y + 20,
                                             existing?.IsInAccreditationScope ?? false);

                y += 48;

                Notice(20, y, 580, 34, "Catalog.AccreditationScopeNotice");
                y += 42;
                break;

            case NomenclatureKind.SpecimenType:
                _fastingHours = Number("Catalog.FastingHours", 20, y, 100,
                                        existing?.FastingHours ?? 0, max: 48);
                y += 48;

                _collectionInstructions = TextField("Catalog.CollectionInstructions", 20, y, 580,
                                                existing?.CollectionInstructions?.Fr);
                y += 48;

                _patientInstructions = TextField("Catalog.PatientInstructions", 20, y, 580,
                                             existing?.PatientInstructions?.Fr);
                y += 48;

                Notice(20, y, 580, 20, "Catalog.PatientInstructionsNotice");
                y += 28;
                break;

            case NomenclatureKind.ContainerType:
                _capColour = TextField("Catalog.CapColour", 20, y, 110,
                                   existing?.CapColourHex ?? "#FFFFFF", latin: true, mono: true);

                _colourSwatch = new Panel
                {
                    Bounds = new Rectangle(138, y + 18, 26, UiTheme.InputHeight),
                    BorderStyle = BorderStyle.FixedSingle
                };

                Controls.Add(_colourSwatch);

                _additive = TextField("Catalog.Additive", 176, y, 200, existing?.Additive?.Fr);

                _drawOrder = Number("Sample.DrawOrder", 388, y, 90, existing?.DrawOrder ?? 0,
                                     max: 99);

                y += 48;

                _nominalVolume = TextField("Catalog.NominalVolume", 20, y, 110,
                                       existing?.NominalVolumeMl?.ToString("0.#"),
                                       latin: true, right: true);

                _minimumVolume = TextField("Catalog.MinimumVolume", 142, y, 110,
                                       existing?.MinimumVolumeMl?.ToString("0.#"),
                                       latin: true, right: true);

                _protectFromLight = Check("Collection.ProtectFromLight", 264, y + 20,
                                           existing?.RequiresProtectionFromLight ?? false);

                _iceTransport = Check("Collection.TransportOnIce", 440, y + 20,
                                       existing?.RequiresIceTransport ?? false);

                y += 48;

                Notice(20, y, 580, 34, "Catalog.DrawOrderNotice");
                y += 42;

                _capColour.TextChanged += (_, _) => RefreshSwatch();
                RefreshSwatch();
                break;
        }

        _active = Check("Field.Active", 20, y, existing?.IsActive ?? true, bold: true);

        if (existing is { UsageCount: > 0 })
            Notice(160, y - 4, 440, 30, "Catalog.NomenclatureInUseNotice", UiTheme.Abnormal)
                .Text = Localizer.Format("Catalog.NomenclatureInUseNotice", existing.UsageCount);

        Buttons(ClientSize.Height - 54, 620);
    }

    /// <summary>
    /// <see cref="MasterDataEditor.Blank"/> for a field this dialog may not have created at all:
    /// four lists share one form, so most of its boxes are null on any given opening.
    /// </summary>
    private static string? Optional(TextBox? box) => box is null ? null : Blank(box);

    private static decimal? OptionalDecimal(TextBox? box) => box is null ? null : DecimalOrNull(box);

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
        if (string.IsNullOrWhiteSpace(_code.Text))
        {
            Dialogs.Warn(Localizer["Nomenclature_CodeRequired"], Text);
            _code.Focus();
            return;
        }

        if (_kind == NomenclatureKind.MeasurementUnit)
        {
            if (string.IsNullOrWhiteSpace(_symbol?.Text))
            {
                Dialogs.Warn(Localizer["Unit_SymbolRequired"], Text);
                _symbol?.Focus();
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(_nameFr.Text))
        {
            Dialogs.Warn(Localizer["Nomenclature_NameFrenchRequired"], Text);
            _nameFr.Focus();
            return;
        }

        if (_kind == NomenclatureKind.ContainerType && _drawOrder is { Value: <= 0 })
        {
            Dialogs.Warn(Localizer["Container_DrawOrderRequired"], Text);
            _drawOrder.Focus();
            return;
        }

        Request = new NomenclatureRequest
        {
            Id = _id,
            Code = _code.Text.Trim().ToUpperInvariant(),
            Name = _kind == NomenclatureKind.MeasurementUnit
                ? new MultilingualText(_symbol?.Text.Trim() ?? string.Empty, Optional(_symbolArabic))
                : new MultilingualText(_nameFr.Text.Trim(), Blank(_nameAr), Blank(_nameEn)),

            DisplayOrder = _displayOrder is null ? 0 : (int)_displayOrder.Value,
            IsInAccreditationScope = _accreditationScope?.Checked ?? false,
            SupervisorUserId = null,

            CollectionInstructions = Optional(_collectionInstructions) is { } collection
                ? new MultilingualText(collection) : null,
            PatientInstructions = Optional(_patientInstructions) is { } patient
                ? new MultilingualText(patient) : null,
            FastingHours = _fastingHours is null ? 0 : (int)_fastingHours.Value,

            CapColourHex = _capColour is null
                ? "#FFFFFF"
                : _capColour.Text.Trim().ToUpperInvariant(),
            Additive = Optional(_additive) is { } additive ? new MultilingualText(additive) : null,
            NominalVolumeMl = OptionalDecimal(_nominalVolume),
            MinimumVolumeMl = OptionalDecimal(_minimumVolume),
            DrawOrder = _drawOrder is null ? 0 : (int)_drawOrder.Value,
            RequiresProtectionFromLight = _protectFromLight?.Checked ?? false,
            RequiresIceTransport = _iceTransport?.Checked ?? false,

            DisplaySymbol = _symbol?.Text.Trim() ?? string.Empty,
            DisplaySymbolArabic = Optional(_symbolArabic),
            UcumCode = Optional(_ucum),
            FactorToSi = OptionalDecimal(_factor),
            SiUnitCode = Optional(_siUnit),

            IsActive = _active.Checked
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}
