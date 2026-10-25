using System.Globalization;
using FluentAssertions;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Patients;
using MarocLIS.Tests.Licensing;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Catalogue » and of its editors, moved out of the workstation's CatalogView and
/// CatalogEditors, now shared with the web.
/// </summary>
public sealed class CatalogScreenRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);

    /// <summary>The text of a key, with its values: enough to see which key a rule chose.</summary>
    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    internal static AnalysisRow Analysis(string code = "GLY", bool active = true, int parameters = 1, int activeParameters = 1,
                                         int tariffs = 1, decimal? price = 30m, int unconfirmed = 0, bool accredited = false,
                                         Guid? id = null) =>
        new(id ?? Guid.NewGuid(), code, new MultilingualText("Glycémie à jeun", "سكر الدم"), "BIOCH",
            new MultilingualText("Biochimie"), Guid.NewGuid(), Guid.NewGuid(), new MultilingualText("Sérum"),
            null, null, parameters, activeParameters, 1, tariffs, price, 20m, "0102", 24, accredited, false,
            ConsentRequirement.None, active, 12, unconfirmed);

    internal static ParameterRow Parameter(string code = "GLY", ResultValueType type = ResultValueType.Numeric,
                                           bool active = true, int results = 0, string? formula = null, int options = 0) =>
        new(Guid.NewGuid(), Guid.NewGuid(), code, new MultilingualText("Glucose"), type, null, "g/L", 2,
            0.1m, 30m, null, null, 0.4m, 5m, 2.5m, 15m, 30, formula, false, true, 1, 0, active, 1, options, results);

    internal static ReferenceRangeRow Range(DateOnly? from = null, DateOnly? until = null, DateOnly? reviewed = null,
                                            int? ageFrom = null, int? ageTo = null, int? unit = 365,
                                            Gender gender = Gender.Unknown, bool? pregnant = null, int? trimester = null,
                                            decimal? low = 0.7m, decimal? high = 1.1m) =>
        new(Guid.NewGuid(), Guid.NewGuid(), gender, ageFrom, ageTo, pregnant, trimester, null, null, low, high, null,
            null, false, "Tietz 2018", reviewed ?? Today.AddMonths(-2), from ?? Today.AddYears(-1), until, true, 0, unit);

    internal static TariffRow Tariff(Guid? payer = null, DateOnly? from = null, DateOnly? until = null, decimal price = 30m,
                                     decimal coefficient = 30m, decimal keyValue = 1m) =>
        new(Guid.NewGuid(), Guid.NewGuid(), payer, payer is null ? null : "CNSS", from ?? Today.AddYears(-1), until, "B",
            coefficient, keyValue, price, true, 0m);

    private static NomenclatureGapRow Gap(NomenclatureGapKind kind, string code = "GLY", int differing = 1, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), code, new MultilingualText("Glycémie"), "0102", kind, "Glucose", 30m, 25m, differing);

    // ---- Actions -------------------------------------------------------------------------------

    [Fact]
    public void The_catalogue_offers_each_action_by_its_own_right_and_by_what_is_selected()
    {
        var manager = new LicenceTestKit.User(true, Permissions.Catalog.Manage);
        var none = CatalogScreenRules.Actions(manager, new CatalogSelection());

        none.NewAnalysis.Should().BeTrue();
        none.EditAnalysis.Should().BeFalse("nothing is selected");
        none.NewParameter.Should().BeFalse("a parameter has no meaning away from its analysis");
        none.NewTariff.Should().BeFalse("a tariff is a billing right");

        var selected = CatalogScreenRules.Actions(manager, new CatalogSelection(Analysis: true, Parameter: true, Range: true));
        selected.EditParameter.Should().BeTrue();
        selected.NewRange.Should().BeFalse("reference intervals are their own right");

        var biologist = new LicenceTestKit.User(true, Permissions.Catalog.ManageReferenceRanges, Permissions.Billing.ManageTariffs);
        var acts = CatalogScreenRules.Actions(biologist, new CatalogSelection(Analysis: true, Parameter: true, Range: true,
                                                                               Gap: Gap(NomenclatureGapKind.CoefficientDiffers)));
        acts.NewRange.Should().BeTrue();
        acts.ReviewRange.Should().BeTrue();
        acts.NewTariff.Should().BeTrue();
        acts.EditTariff.Should().BeFalse("no tariff is selected");
        acts.NewAnalysis.Should().BeFalse();
        acts.ApplyOfficial.Should().BeTrue();
        acts.EditGapAnalysis.Should().BeFalse("correcting the act code is a catalogue change");

        CatalogScreenRules.Actions(biologist, new CatalogSelection(Gap: Gap(NomenclatureGapKind.MissingCode)))
                          .ApplyOfficial.Should().BeFalse("only a coefficient gap is put right from the check");
    }

    // ---- Rows ----------------------------------------------------------------------------------

    [Fact]
    public void An_analysis_that_cannot_be_ordered_reads_as_a_warning_and_says_why()
    {
        CatalogScreenRules.Tone(Analysis(active: false)).Should().Be(CatalogTone.Muted);
        CatalogScreenRules.Tone(Analysis(tariffs: 0)).Should().Be(CatalogTone.Abnormal);
        CatalogScreenRules.Tone(Analysis()).Should().Be(CatalogTone.Normal);

        CatalogScreenRules.Flags(Analysis(activeParameters: 0, tariffs: 0, accredited: true, unconfirmed: 2), Text)
                          .Should().Equal("Catalog.NoParameter", "Catalog.NoTariff", "ISO", "Catalog.AgeUnitToConfirm (2)");

        CatalogScreenRules.ParameterCount(Analysis(parameters: 8, activeParameters: 7)).Should().Be("7/8");
        CatalogScreenRules.ParameterCount(Analysis(parameters: 8, activeParameters: 8)).Should().Be("8");
    }

    [Fact]
    public void The_to_complete_chip_keeps_the_analyses_somebody_has_to_come_back_to()
    {
        var rows = new[] { Analysis("GLY"), Analysis("NFS", tariffs: 0), Analysis("TSH", unconfirmed: 1) };

        CatalogScreenRules.Shown(rows, onlyToComplete: true).Select(a => a.Code).Should().Equal("NFS", "TSH");
        CatalogScreenRules.Shown(rows, onlyToComplete: false).Should().HaveCount(3);
        CatalogScreenRules.Heading(rows, Text).Should().Be("Catalog.Analyses — 3 · 1 catalog.notorderable · 1 catalog.ageunittoconfirm");
    }

    [Fact]
    public void A_reference_interval_reads_in_the_unit_a_human_would_use_and_in_the_order_the_resolver_ranks()
    {
        CatalogScreenRules.DescribeAgeBand(0, 28, Text).Should().Be("0 Catalog.Days – 28 Catalog.Days");
        CatalogScreenRules.DescribeAgeBand(365, 6570, Text).Should().Be("12 Catalog.Months – 18 Catalog.Years");
        CatalogScreenRules.DescribeAgeBand(null, 730, Text).Should().Be("< 24 Catalog.Months");

        CatalogScreenRules.DescribePopulation(Range(gender: Gender.Female, pregnant: true, trimester: 2), Text)
                          .Should().Be("Catalog.Trimester(2) · Gender.Female");
        CatalogScreenRules.DescribePopulation(Range(), Text).Should().Be("Catalog.AllPatients");

        CatalogScreenRules.FormatInterval(Range(low: 4m, high: 10m), 2, AppLanguage.French, CultureInfo.InvariantCulture)
                          .Should().Be("4.00 - 10.00");
        CatalogScreenRules.FormatInterval(Range(low: null, high: 5m), 1, AppLanguage.French, CultureInfo.InvariantCulture)
                          .Should().Be("< 5.0");
    }

    [Fact]
    public void An_interval_out_of_force_is_grey_and_one_due_for_review_is_a_warning()
    {
        CatalogScreenRules.Tone(Range(until: Today.AddDays(-1)), Today).Should().Be(CatalogTone.Muted);
        CatalogScreenRules.Tone(Range(reviewed: Today.AddYears(-3).AddDays(-1)), Today).Should().Be(CatalogTone.Abnormal);
        CatalogScreenRules.Flags(Range(reviewed: Today.AddYears(-3).AddDays(-1)), Today, Text).Should().Contain("Catalog.ReviewDue");
        CatalogScreenRules.Validity(new DateOnly(2024, 1, 1), null).Should().Be("01/01/24 →");
    }

    [Fact]
    public void A_reflex_rule_that_can_never_fire_is_the_worst_state_of_the_screen()
    {
        var never = new ReflexRuleRow(Guid.NewGuid(), Guid.NewGuid(), "AGHBS", new MultilingualText("Ag HBs"), "HBS",
                                      Guid.NewGuid(), "HBSCONF", new MultilingualText("Confirmation"),
                                      new MultilingualText("Confirmer"), ReflexCondition.Between, 10m, null, null, false, true, true);

        CatalogScreenRules.Tone(never).Should().Be(CatalogTone.Critical);
        CatalogScreenRules.Flags(never, Text).Should().Contain("Catalog.RuleCanNeverFire");
        CatalogScreenRules.DescribeCondition(never with { Condition = ReflexCondition.Outside, ThresholdUpper = 20m },
                                             CultureInfo.InvariantCulture).Should().Be("∉ [10 ; 20]");
        CatalogScreenRules.ModeKey(never).Should().Be("Catalog.NeedsConfirmation");
    }

    [Fact]
    public void The_national_acts_are_found_by_search_then_kept_by_decree_and_a_gap_names_its_neighbour()
    {
        var acts = new[]
        {
            new NationalActRow(Guid.NewGuid(), "0102", "Glucose", 30m, "B", "Biochimie", null, 2005, true),
            new NationalActRow(Guid.NewGuid(), "0104", "Glucose post-prandial", 40m, "B", "Biochimie", "Glucides", 2016, true),
            new NationalActRow(Guid.NewGuid(), "1201", "Hémogramme", 50m, "B", "Hématologie", null, 2016, false)
        };

        var (matching, shown) = CatalogScreenRules.Acts(acts, "glucose", 2016);
        matching.Should().HaveCount(2);
        shown.Select(a => a.Code).Should().Equal("0104");
        CatalogScreenRules.Chapter(acts[1]).Should().Be("Biochimie · Glucides");
        CatalogScreenRules.Coefficient(acts[1], CultureInfo.InvariantCulture).Should().Be("B 40");
        CatalogScreenRules.Pickable(acts, null).Should().HaveCount(2, "a repealed act is not offered");

        var first = Gap(NomenclatureGapKind.CoefficientDiffers, "GLY", differing: 2);
        var second = Gap(NomenclatureGapKind.UnknownCode, "NFS");
        CatalogScreenRules.Neighbour([first, second], first.AnalysisId).Should().Be(second);
        CatalogScreenRules.Neighbour([first, second], second.AnalysisId).Should().Be(first);
        CatalogScreenRules.GapText(first, Text).Should().Be("NabmGap.CoefficientDiffers (2)");
        CatalogScreenRules.Tone(second).Should().Be(CatalogTone.Critical);
        CatalogScreenRules.Applied(first, new OfficialCoefficientOutcome(1, 1, 1), Today, Text)
                          .Should().Be("Nabm.Applied(GLY,0102,2,20/10/2026) Nabm.SkippedScheduled(1)");
    }

    // ---- Editors -------------------------------------------------------------------------------

    [Fact]
    public void The_analysis_editor_refuses_on_the_field_at_fault_and_says_which_partner_problem_it_is()
    {
        var form = new AnalysisForm { Code = "GLY", NameFr = "Glycémie", DepartmentId = Guid.NewGuid(), SpecimenTypeId = Guid.NewGuid() };

        CatalogEditorRules.Check(form with { Price = "12,5 DH" }, 0).Should().Be(new FieldRefusal("price", "Result_NotANumber", "12,5 DH"));
        CatalogEditorRules.Check(form with { Code = " " }, 0)!.Field.Should().Be("code");
        CatalogEditorRules.Check(form with { DepartmentId = null }, 0)!.Key.Should().Be("Catalog_DepartmentAndSpecimenRequired");
        CatalogEditorRules.Check(form with { Subcontracted = true }, 0)!.Key.Should().Be("Catalog_NoPartnerSiteConfigured");
        CatalogEditorRules.Check(form with { Subcontracted = true }, 2)!.Key.Should().Be("Test_SubcontractorRequired");
        CatalogEditorRules.Check(form, 0).Should().BeNull();
    }

    [Fact]
    public void Picking_an_act_moves_a_price_that_followed_the_base_and_leaves_one_set_apart()
    {
        var act = new NationalActRow(Guid.NewGuid(), "0104", "Glucose", 40m, "B", "Biochimie", null, 2016, true);
        var following = new AnalysisForm { Coefficient = "30", LetterKeyValue = "1.1", Price = "33.00" };
        var apart = following with { Price = "45.00" };

        CatalogEditorRules.WithAct(following, act, canPrice: true, CultureInfo.InvariantCulture)
                          .Should().Match<AnalysisForm>(f => f.ActCode == "0104" && f.Coefficient == "40" && f.Price == "44.00");
        CatalogEditorRules.WithAct(apart, act, canPrice: true, CultureInfo.InvariantCulture).Price.Should().Be("45.00");
        CatalogEditorRules.WithAct(apart, act, canPrice: false, CultureInfo.InvariantCulture).Coefficient.Should().Be("30", "the operator may not set prices");

        CatalogEditorRules.Act("104", [act]).Kind.Should().Be(ActInfoKind.Unknown);
        CatalogEditorRules.Act("0104", [act]).Act.Should().Be(act);
        CatalogEditorRules.Act("", [act]).Kind.Should().Be(ActInfoKind.Hint);
        CatalogEditorRules.UnknownActCode(apart with { ActCode = "9999" }, [act]).Should().Be("9999");

        CatalogEditorRules.Computed("30", "1.1", "45").Should().Be((33m, 12m));
    }

    [Fact]
    public void The_laboratory_price_is_saved_only_when_it_changed_and_corrected_in_place_on_the_day_it_was_set()
    {
        var lab = Tariff(from: Today, price: 33m, coefficient: 30m, keyValue: 1.1m);
        var form = new AnalysisForm { LetterKey = "B", Coefficient = "30", LetterKeyValue = "1.1", Price = "33" };

        CatalogEditorRules.ReadPrice(form, canPrice: true, lab).Should().Be(((FieldRefusal?)null, (TariffRequest?)null));
        CatalogEditorRules.ReadPrice(form with { Price = "" }, canPrice: true, null).Request.Should().BeNull();
        CatalogEditorRules.ReadPrice(form with { Price = "-1" }, canPrice: true, lab).Refusal!.Key.Should().Be("Tariff_NegativePrice");

        var (_, request) = CatalogEditorRules.ReadPrice(form with { Price = "36" }, canPrice: true, lab);
        var analysis = Guid.NewGuid();
        var (revision, inPlace) = CatalogEditorRules.PriceRevision(request!, analysis, lab, Today);
        revision.Should().Match<TariffRequest>(r => r.AnalysisTestId == analysis && r.ValidFrom == Today && r.Price == 36m);
        inPlace.Should().BeTrue();
        CatalogEditorRules.PriceRevision(request!, analysis, lab with { ValidFrom = Today.AddDays(-3) }, Today).CorrectInPlace.Should().BeFalse();

        CatalogEditorRules.LabTariff([Tariff(payer: Guid.NewGuid()), lab, Tariff(from: Today.AddYears(-2), until: Today.AddYears(-1))], Today)
                          .Should().Be(lab);
    }

    [Fact]
    public void An_unchanged_french_text_keeps_its_translations_and_a_stored_zero_is_not_saved_as_unset()
    {
        var stored = new MultilingualText("Glycémie", "سكر", "Glucose");

        CatalogEditorRules.FrenchEdited(stored, "Glycémie").Should().BeSameAs(stored);
        CatalogEditorRules.FrenchEdited(stored, "Glycémie à jeun")!.Ar.Should().BeNull();
        CatalogEditorRules.FrenchEdited(stored, "  ").Should().BeNull();

        CatalogEditorRules.IntOrStored(0, 0, 48).Should().Be(0);
        CatalogEditorRules.IntOrStored(0, null, 48).Should().BeNull();
        CatalogEditorRules.IntOrStored(12, 0, 48).Should().Be(12);
    }

    [Fact]
    public void The_parameter_editor_reads_only_the_fields_its_value_type_uses_and_checks_the_formula_against_its_siblings()
    {
        CatalogEditorRules.Fields(ResultValueType.Coded).Should().Be(new ParameterFields(false, true, false));
        CatalogEditorRules.ValueTypeEditable(Parameter(results: 120)).Should().BeFalse("a hundred stored results freeze the type");

        var coded = new ParameterForm { Code = "AGHBS", NameFr = "Ag HBs", ValueType = ResultValueType.Coded, CriticalLow = "n/a" };
        CatalogEditorRules.Check(coded, []).Should().Be(new FieldRefusal("options", "Parameter_CodedNeedsOptions"),
                                                        "a box the type does not use is not read");
        CatalogEditorRules.Check(coded with { Options = CatalogEditorRules.NegativePositive(Text) }, []).Should().BeNull();

        var calculated = new ParameterForm { Code = "LDL", NameFr = "LDL", Formula = "[CT]-[HDL]-[TG]/5" };
        CatalogEditorRules.Check(calculated, ["CT", "HDL"]).Should().Be(new FieldRefusal("formula", "Parameter_FormulaUnknownReference", "TG"));
        CatalogEditorRules.Check(calculated, ["CT", "HDL", "tg"]).Should().BeNull();
    }

    [Fact]
    public void A_coded_answer_kept_unchanged_keeps_its_english_label()
    {
        var stored = new ParameterRequest
        {
            AnalysisTestId = Guid.NewGuid(), Code = "AGHBS", Name = new MultilingualText("Ag HBs"), ValueType = ResultValueType.Coded,
            CodedOptions = [new CodedOptionRequest("NEG", new MultilingualText("Négatif", "سالب", "Negative"), 1, false, false, true)]
        };

        var options = CatalogEditorRules.ReadOptions([new CodedOptionLine("neg", "Négatif", "سالب", ExpectedNormal: true),
                                                      new CodedOptionLine("", "sans code")], stored);

        options.Should().ContainSingle().Which.Label.En.Should().Be("Negative");
        options[0].Code.Should().Be("NEG");
    }

    [Fact]
    public void A_reference_interval_reopens_in_the_unit_it_was_typed_in_and_counts_what_makes_it_win()
    {
        CatalogEditorRules.FormOf(Range(ageFrom: 360, ageTo: 1080, unit: 30), 2, Today, CultureInfo.InvariantCulture)
                          .Should().Match<RangeForm>(f => f.AgeUnitDays == 30 && f.AgeFrom == 12 && f.AgeTo == 36);
        CatalogEditorRules.FormOf(Range(ageFrom: 365, ageTo: 1095, unit: null), 2, Today, CultureInfo.InvariantCulture)
                          .Should().Match<RangeForm>(f => f.AgeUnitDays == 365 && f.AgeFrom == 1 && f.AgeTo == 3);
        CatalogEditorRules.FormOf(null, 2, Today, CultureInfo.InvariantCulture).ValidFrom.Should().Be(Today);

        var form = new RangeForm { Gender = Gender.Female, Pregnancy = true, Lower = "11", Upper = "15", ValidFrom = Today };
        CatalogEditorRules.Specificity(form).Should().Be(6);
        CatalogEditorRules.Check(form with { Lower = "16" }).Should().Be(new FieldRefusal("lower", "Range_LowerAboveUpper"));
        CatalogEditorRules.Check(form with { Lower = "", Upper = "" })!.Key.Should().Be("Range_EmptyInterval");
        CatalogEditorRules.Check(form with { AgeFrom = 18, AgeTo = 10 })!.Field.Should().Be("agefrom");

        var request = CatalogEditorRules.ToRequest(form with { AgeFrom = 1, AgeTo = 3, AgeUnitDays = 365 }, null, Guid.NewGuid());
        (request.AgeFromDays, request.AgeToDays, request.AgeUnitDays).Should().Be((365, 1095, 365));
    }

    [Fact]
    public void A_tariff_revision_starts_after_the_one_it_closes_and_a_correction_keeps_its_date()
    {
        var current = Tariff(from: Today);
        CatalogEditorRules.ValidFrom(Today, current, correcting: false).Should().Be(Today.AddDays(1));
        CatalogEditorRules.ValidFrom(Today, current, correcting: true).Should().Be(Today);

        var payer = Guid.NewGuid();
        var switchedOff = Tariff(payer: payer) with { PayerName = "Mutuelle fermée" };
        var payers = CatalogEditorRules.Payers([new TariffPayerOption(Guid.NewGuid(), "CNSS", new MultilingualText("CNSS"))],
                                               [switchedOff], AppLanguage.French);
        payers.Select(p => p.Display).Should().Equal("CNSS — CNSS", "Mutuelle fermée");

        CatalogEditorRules.KnownLetterKeyValue(null, [Tariff(keyValue: 1.1m)], 1.2m).Should().Be(1.1m);
        CatalogEditorRules.KnownLetterKeyValue(null, [], 1.2m).Should().Be(1.2m);

        var form = new TariffForm { Price = "30", LetterKey = "B", VatExempt = false, VatRate = "" };
        CatalogEditorRules.Check(form).Should().Be(new FieldRefusal("vatrate", "Tariff_VatRateRequired"));
        CatalogEditorRules.ToRequest(form with { VatExempt = true, VatRate = "0.2", LetterKey = "b" }, Guid.NewGuid())
                          .Should().Match<TariffRequest>(r => r.VatRate == 0m && r.LetterKey == "B");
    }

    [Fact]
    public void A_panel_counts_its_members_without_the_optional_ones_and_refuses_a_bundle_of_options()
    {
        var gly = Analysis("GLY", price: 30m);
        var cholesterol = Analysis("CT", price: 40m);
        var urea = Analysis("URE", price: 25m, tariffs: 0);

        CatalogEditorRules.Candidates([gly, cholesterol, urea]).Should().Equal(gly, cholesterol);

        var form = new PanelForm
        {
            Code = "BILAN", NameFr = "Bilan", PackagePrice = "80",
            Members = [new PanelMemberRequest(gly.Id, false), new PanelMemberRequest(cholesterol.Id, true)]
        };

        CatalogEditorRules.Total([gly, cholesterol], form).Should().Be((2, 30m, true));
        CatalogEditorRules.Check(form with { Members = [new PanelMemberRequest(gly.Id, true)] })!.Key
                          .Should().Be("Panel_AllMembersOptional");
        CatalogEditorRules.Check(form with { Members = [] })!.Key.Should().Be("Panel_AtLeastOneAnalysisRequired");
    }

    [Fact]
    public void A_reflex_condition_reads_only_the_boxes_it_uses()
    {
        CatalogEditorRules.Fields(ReflexCondition.Between).Should().Be(new ReflexFields(true, true, false, "Catalog.ReflexBetweenNotice"));

        var form = new ReflexForm { Threshold = "10", ThresholdUpper = "20", CodedValue = "POS" };
        CatalogEditorRules.WithCondition(form, ReflexCondition.GreaterThan)
                          .Should().Match<ReflexForm>(f => f.Threshold == "10" && f.ThresholdUpper == "" && f.CodedValue == "");

        var ready = form with { TriggerParameterId = Guid.NewGuid(), TargetAnalysisId = Guid.NewGuid(), DescriptionFr = "Confirmer",
                                Condition = ReflexCondition.Outside, ThresholdUpper = "" };
        CatalogEditorRules.Check(ready)!.Key.Should().Be("Reflex_TwoThresholdsRequired");
        CatalogEditorRules.Check(ready with { TriggerParameterId = null })!.Field.Should().Be("parameter");

        var heading = Parameter("TITRE", ResultValueType.Heading);
        var kept = Parameter("AGHBS");
        CatalogEditorRules.TriggerParameters([heading, kept, Parameter("OLD", active: false)]).Should().Equal(kept);
        CatalogEditorRules.TriggerParameter([kept], Guid.NewGuid()).Should().Be(kept.Id);
    }

    [Fact]
    public void A_nomenclature_entry_asks_for_what_its_list_has()
    {
        CatalogEditorRules.Check(NomenclatureKind.MeasurementUnit, new NomenclatureForm { Code = "GL" })!.Key.Should().Be("Unit_SymbolRequired");
        CatalogEditorRules.Check(NomenclatureKind.ContainerType, new NomenclatureForm { Code = "EDTA", NameFr = "Violet" })!.Key
                          .Should().Be("Container_DrawOrderRequired");
        CatalogEditorRules.Check(NomenclatureKind.ContainerType, new NomenclatureForm { Code = "EDTA", NameFr = "Violet", DrawOrder = 4, NominalVolume = "4 mL" })!
                          .Field.Should().Be("nominalvolume");

        var unit = CatalogEditorRules.ToRequest(NomenclatureKind.MeasurementUnit, new NomenclatureForm { Code = "gl", Symbol = "g/L", NameFr = "ignoré" }, null);
        unit.Name.Fr.Should().Be("g/L", "a unit's symbol is its name");
        unit.Code.Should().Be("GL");
        CatalogEditorRules.CodeMaxLengthOf(NomenclatureKind.Department).Should().Be(20);
        CatalogEditorRules.Swatch("#7B1FA2").Should().Be("#7B1FA2");
        CatalogEditorRules.Swatch("violet").Should().BeNull();
    }
}
