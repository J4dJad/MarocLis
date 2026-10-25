using FluentAssertions;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Tests.Licensing;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Stock », moved out of the workstation's InventoryView and its reception dialog,
/// now shared with the web.
/// </summary>
public sealed class InventoryScreenRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 18);

    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    internal static StockRow Item(string code = "BIO-GLU", decimal onHand = 12m, bool reorder = false, bool belowMinimum = false,
                                  bool critical = false, int lots = 2, int quarantined = 0, int? days = 140,
                                  StorageCondition condition = StorageCondition.Refrigerated, string name = "Glucose GOD-PAP 4×100 mL") =>
        new(Guid.NewGuid(), code, new MultilingualText(name, "غلوكوز"), StockItemKind.Reagent, "coffret", onHand, 5m, 2m,
            reorder, belowMinimum, critical, "Biomedical Maroc", condition, lots, quarantined,
            days is { } d ? Today.AddDays(d) : null, days);

    internal static LotRow Lot(string number = "GLU-2611", bool quarantined = false, bool rejected = false, bool recalled = false,
                               bool expired = false, bool beyond = false, decimal remaining = 4m, int days = 140, Guid? itemId = null,
                               string item = "BIO-GLU") =>
        new(Guid.NewGuid(), itemId ?? Guid.NewGuid(), item, new MultilingualText("Glucose GOD-PAP 4×100 mL", "غلوكوز"), number,
            Today.AddDays(days), days, 6m, remaining, Today.AddDays(-20), quarantined ? null : Today.AddDays(-5),
            quarantined, rejected, recalled, expired, beyond, !(quarantined || rejected || recalled || expired || beyond),
            "Réfrigérateur 1", "Biomedical Maroc");

    private static InventoryScreenActions Of(StockRow? item, LotRow? lot, params string[] rights) =>
        InventoryScreenRules.Actions(new LicenceTestKit.User(true, rights), item, lot);

    [Fact]
    public void Each_lot_operation_takes_its_right_and_fits_the_lots_state()
    {
        var item = Item();
        var quarantined = Lot(quarantined: true);
        var released = Lot();

        var storeman = Of(item, quarantined, Permissions.Inventory.View, Permissions.Inventory.Receive);
        storeman.Should().Match<InventoryScreenActions>(a => a.Receive && a.Reject && !a.Release && !a.Issue && !a.WriteOff && a.AttachCertificate);
        Of(item, released, Permissions.Inventory.Receive).Reject.Should().BeFalse("a released lot is refused only by who may release");

        var biologist = Of(item, quarantined, Permissions.Quality.ReviewQc);
        biologist.Release.Should().BeTrue();
        Of(item, released, Permissions.Quality.ReviewQc).Should().Match<InventoryScreenActions>(a => !a.Release && a.Reject);

        var manager = Of(item, Lot(rejected: true), Permissions.Inventory.Adjust);
        manager.Should().Match<InventoryScreenActions>(a => a.ClearRejection && a.Recall && a.WriteOff);
        Of(item, Lot(rejected: true, recalled: true), Permissions.Inventory.Adjust).Should()
            .Match<InventoryScreenActions>(a => !a.ClearRejection && !a.Recall, "a recall is not a clerical error");
        Of(item, Lot(remaining: 0m), Permissions.Inventory.Adjust).WriteOff.Should().BeFalse("nothing is left to write off");

        Of(item, released, Permissions.Inventory.View).RecallList.Should().BeFalse("the patient results take the right to see them");
        Of(item, released, Permissions.Results.View).RecallList.Should().BeTrue();
        Of(null, null, Permissions.Inventory.Receive, Permissions.Inventory.Issue).Should()
            .Match<InventoryScreenActions>(a => !a.Receive && !a.Issue && !a.Compare && !a.Certificates);
    }

    [Theory]
    [InlineData(MarocLIS.Domain.Common.AppLanguage.French, "27/10/26 (9j)", "3 (1Q)", "Q ⏳")]
    [InlineData(MarocLIS.Domain.Common.AppLanguage.English, "27/10/26 (9d)", "3 (1Q)", "Q ⏳")]
    [InlineData(MarocLIS.Domain.Common.AppLanguage.Arabic, "27/10/26 (9ي)", "3 (1ح)", "ح ⏳")]
    public void The_day_and_quarantine_abbreviations_are_the_language_s_own(MarocLIS.Domain.Common.AppLanguage language,
                                                                            string expiry, string lots, string glyphs)
    {
        // The workstation and the browser both take them from the translation files: « (450j) »
        // read in Arabic is a French word in an Arabic screen.
        var localizer = new MarocLIS.Localization.JsonLocalizer(new MarocLIS.Localization.LocalizationOptions { MarkMissingKeys = true });
        string Lang(string key, params object?[] args) => localizer.Format(language, key, args);
        var row = Item(lots: 3, quarantined: 1, days: 9);

        InventoryScreenRules.Expiry(row, Lang).Should().Be(expiry);
        InventoryScreenRules.Lots(row, Lang).Should().Be(lots);
        InventoryScreenRules.AlertGlyphs(row, Lang).Should().Be(glyphs);
        InventoryScreenRules.AlertMeanings(row, Lang)[0].Should().StartWith(glyphs.Split(' ')[0] + " ");
    }

    [Fact]
    public void A_stock_line_says_what_is_wrong_in_glyphs_and_in_its_colours()
    {
        InventoryScreenRules.AlertGlyphs(Item(belowMinimum: true, quarantined: 1, days: 12, critical: true), Text).Should().Be("!! Inventory.QuarantinedShort ⏳ ★");
        InventoryScreenRules.AlertGlyphs(Item(reorder: true), Text).Should().Be("!");
        InventoryScreenRules.AlertGlyphs(Item(), Text).Should().BeEmpty();
        InventoryScreenRules.AlertMeanings(Item(reorder: true, quarantined: 1), Text).Should()
            .Equal("! Inventory.ReorderPoint", "Inventory.QuarantinedShort Inventory.Quarantined");

        InventoryScreenRules.Tone(Item(belowMinimum: true, critical: true)).Should().Be(StockRowTone.CriticalBold);
        InventoryScreenRules.Tone(Item(belowMinimum: true)).Should().Be(StockRowTone.Critical);
        InventoryScreenRules.Tone(Item(days: -2)).Should().Be(StockRowTone.Critical, "a lot has expired");
        InventoryScreenRules.Tone(Item(quarantined: 1)).Should().Be(StockRowTone.Warning);
        InventoryScreenRules.Tone(Item()).Should().Be(StockRowTone.Plain);
        InventoryScreenRules.IsWarningCell("expiry").Should().BeTrue();
        InventoryScreenRules.IsWarningCell("name").Should().BeFalse();

        var row = Item(lots: 3, quarantined: 1, days: 9);
        InventoryScreenRules.Lots(row, Text).Should().Be("3 (1Inventory.QuarantinedShort)");
        InventoryScreenRules.Expiry(row, Text).Should().Be("27/10/26 (9Inventory.DaysShort)");
        InventoryScreenRules.OnHand(row, System.Globalization.CultureInfo.InvariantCulture).Should().Be("12.00 coffret");
        InventoryScreenRules.Count([Item(), Item(reorder: true)], Text).Should().Be("StocksUi.Items(2) · 1 inventory.belowminimum");
    }

    [Fact]
    public void A_lot_reads_out_of_use_waiting_or_usable()
    {
        InventoryScreenRules.LotStateKey(Lot(rejected: true, quarantined: true)).Should().Be("Lot_Rejected");
        InventoryScreenRules.LotStateKey(Lot(recalled: true)).Should().Be("Inventory.Recalled");
        InventoryScreenRules.LotStateKey(Lot(quarantined: true)).Should().Be("Inventory.Quarantined");
        InventoryScreenRules.LotStateKey(Lot()).Should().Be("Inventory.Usable");

        InventoryScreenRules.LotTone(Lot(expired: true)).Should().Be(StockTone.Critical);
        InventoryScreenRules.LotTone(Lot(beyond: true)).Should().Be(StockTone.Warning);
        InventoryScreenRules.LotTone(Lot()).Should().Be(StockTone.Done);
        StockTones.Label(StockTone.Done, "Utilisable").Should().Be("✓ Utilisable");

        InventoryScreenRules.LotInk(Lot(recalled: true), "lot").Should().Be(StockInk.Critical);
        InventoryScreenRules.LotInk(Lot(days: 20), "expiry").Should().Be(StockInk.AbnormalBold);
        InventoryScreenRules.LotInk(Lot(days: 20), "lot").Should().Be(StockInk.Plain);

        var lots = new[] { Lot(quarantined: true), Lot(quarantined: true, rejected: true), Lot() };
        InventoryScreenRules.LotNote(Item(), lots, false, Text).Should().Be("BIO-GLU · 3 · 1 inventory.quarantined");
        InventoryScreenRules.LotNote(Item(), lots, true, Text).Should().StartWith("3");
    }

    [Fact]
    public void An_issue_takes_a_number_above_zero_and_says_the_lots_drawn_from()
    {
        InventoryScreenRules.ReadIssue("2,5").Quantity.Should().Be(2.5m);
        InventoryScreenRules.ReadIssue("0").Refusal!.Key.Should().Be("Result_NotANumber");
        InventoryScreenRules.ReadIssue("deux").Refusal.Should().Be(new Application.Input.FieldRefusal(InventoryScreenRules.IssueField, "Result_NotANumber", "deux"));
        InventoryScreenRules.Issued("GLU-2611 (2)", Text).Should().Be("Inventory.Issue — inventory.lot GLU-2611 (2)");
        InventoryScreenRules.ReorderMessage(ReorderOutcome.None, Text).Should().BeNull();
        InventoryScreenRules.ReorderMessage(new ReorderOutcome(["BIO-GLU"], ["BC-2026-0042"], []), Text)
            .Should().Be("Inventory.ReorderDrafted(BC-2026-0042,BIO-GLU)");
    }

    [Fact]
    public void A_release_refused_for_its_QC_is_offered_by_derogation_to_a_biologist_only()
    {
        var noQc = Error.Validation("Lot_ReleaseNoQcRun");
        InventoryScreenRules.OffersQcOverride(noQc, p => p == Permissions.Results.BiologicalValidate).Should().BeTrue();
        InventoryScreenRules.OffersQcOverride(noQc, _ => false).Should().BeFalse();
        InventoryScreenRules.OffersQcOverride(Error.Validation("Lot_Rejected"), _ => true).Should().BeFalse();
        InventoryScreenRules.OverridePrompt("Aucun CQ", Lot(), Text).Should().StartWith("Aucun CQ\n\nLot.QcOverrideJustificationPrompt(GLU-2611,15)");

        InventoryScreenRules.WriteOffKind(Lot(expired: true)).Should().Be(StockMovementKind.ExpiryWriteOff);
        InventoryScreenRules.WriteOffKind(Lot()).Should().Be(StockMovementKind.DamageWriteOff);
    }

    [Fact]
    public void A_delivery_is_refused_on_its_box_and_a_cold_chain_article_needs_its_temperature()
    {
        var itemId = Guid.NewGuid();
        ReceiveLotForm Form() => new()
        {
            LotNumber = " GLU-2701 ", ExpiryDate = Today.AddMonths(10), Quantity = "6", UnitPrice = "1 250,50",
            ArrivalTemperature = "5,2", StorageLocation = "Réfrigérateur 1", OpenStabilityDays = 30
        };

        InventoryScreenRules.CheckReception(new ReceiveLotForm { ExpiryDate = Today }, itemId, StorageCondition.Refrigerated, Today)
            .Refusal!.Field.Should().Be(InventoryScreenRules.LotNumberField);

        var past = Form();
        past.ExpiryDate = Today.AddDays(-1);
        InventoryScreenRules.CheckReception(past, itemId, StorageCondition.Refrigerated, Today).Refusal!.Key.Should().Be("Lot_AlreadyExpired");

        var zero = Form();
        zero.Quantity = "0";
        InventoryScreenRules.CheckReception(zero, itemId, StorageCondition.Refrigerated, Today).Refusal!.Field.Should().Be(InventoryScreenRules.QuantityField);

        var currency = Form();
        currency.UnitPrice = "120 DH";
        InventoryScreenRules.CheckReception(currency, itemId, StorageCondition.Refrigerated, Today).Refusal.Should()
            .Be(new Application.Input.FieldRefusal(InventoryScreenRules.PriceField, "Result_NotANumber", "120 DH"));

        var missing = Form();
        missing.ArrivalTemperature = null;
        var refused = InventoryScreenRules.CheckReception(missing, itemId, StorageCondition.Refrigerated, Today).Refusal!;
        refused.Field.Should().Be(InventoryScreenRules.TemperatureField);
        refused.Key.Should().Be("Reception_TemperatureRequired");
        refused.Argument.Should().Be(ColdChainRange.Refrigerated);

        var ok = InventoryScreenRules.CheckReception(Form(), itemId, StorageCondition.Refrigerated, Today);
        ok.Refusal.Should().BeNull();
        ok.NeedsNonConformity.Should().BeFalse();
        ok.Request!.LotNumber.Should().Be("GLU-2701");
        ok.Request.UnitPriceMad.Should().Be(1250.50m);
        ok.Request.StabilityAfterOpeningDays.Should().Be(30);
        ok.Request.Reception!.ArrivalTemperatureCelsius.Should().Be(5.2m);

        var warm = Form();
        warm.ArrivalTemperature = "14";
        warm.PackagingIntact = false;
        var suspect = InventoryScreenRules.CheckReception(warm, itemId, StorageCondition.Refrigerated, Today);
        suspect.NeedsNonConformity.Should().BeTrue("the cold chain broke and the box is damaged");
        suspect.Request.Should().NotBeNull("an excursion is asked about, not refused");

        var ambient = Form();
        ambient.ArrivalTemperature = "31";
        InventoryScreenRules.CheckReception(ambient, itemId, StorageCondition.RoomTemperature, Today).AmbientWarning.Should().BeTrue();

        InventoryScreenRules.Range(StorageCondition.Refrigerated).Should().Be(new ReceptionRange("Reception.RangeRequired", ColdChainRange.Refrigerated, true));
        InventoryScreenRules.Range(StorageCondition.RoomTemperature).Required.Should().BeFalse();
    }

    [Fact]
    public void A_scan_fills_the_lot_and_the_expiry_and_a_refusal_keeps_what_was_typed()
    {
        var form = InventoryScreenRules.NewReception(Today);
        form.ExpiryDate.Should().Be(Today.AddMonths(12));
        form.MatchesOrder.Should().BeTrue();

        InventoryScreenRules.ApplyScan(form, new Gs1Data { LotNumber = "GLU-2702", ExpiryDate = Today.AddMonths(8) }, Today).Should().BeNull();
        form.LotNumber.Should().Be("GLU-2702");
        form.ExpiryDate.Should().Be(Today.AddMonths(8));
        InventoryScreenRules.ApplyScan(form, new Gs1Data { ExpiryDate = Today.AddDays(-3) }, Today)!.Field.Should().Be(InventoryScreenRules.ExpiryField);
        form.ExpiryDate.Should().Be(Today.AddMonths(8), "an expired date is not taken");

        form.DeliveryNote = "BL-88";
        form.ArrivalTemperature = "12";
        var refusal = InventoryScreenRules.Refusal(form, Guid.Empty, "Colis chaud à l'arrivée");
        refusal.Should().Match<RefuseDeliveryRequest>(r => r.LotNumber == "GLU-2702" && r.DeliveryNoteReference == "BL-88" && r.ArrivalTemperatureCelsius == 12m);

        InventoryScreenRules.FieldOf(Error.Conflict("Lot_ReceiveIntoRefusedLot")).Should().Be(InventoryScreenRules.LotNumberField);
        InventoryScreenRules.FieldOf(Error.Validation("Reception_TemperatureRequired")).Should().Be(InventoryScreenRules.TemperatureField);
        InventoryScreenRules.FieldOf(Error.Validation("Stock_InsufficientUsableStock")).Should().BeNull();
    }

    [Fact]
    public void The_alerts_comparison_recall_and_certificates_read_as_on_the_workstation()
    {
        InventoryScreenRules.ExpiryTone(ExpiryAlertLevel.Expired).Should().Be(StockTone.Critical);
        InventoryScreenRules.ExpiryTone(ExpiryAlertLevel.Warning).Should().Be(StockTone.Warning);
        InventoryScreenRules.Difference(2.4m, System.Globalization.CultureInfo.InvariantCulture).Should().Be("+2.4 %");
        InventoryScreenRules.Difference(null).Should().Be("—");
        InventoryScreenRules.ComparisonNoticeKey(new LotComparisonView("BIO-GLU", "GLU-2701", null, [])).Should().Be("LotComparison.NoData");
        InventoryScreenRules.RecallHeaders(Text).Should().HaveCount(7);
        InventoryScreenRules.RecallFileName("GLU/2611", Today).Should().Be("rappel-GLU-2611-20261018.csv");

        InventoryScreenRules.CheckCertificate("coa.pdf", 1200).Should().BeNull();
        InventoryScreenRules.CheckCertificate("coa.exe", 1200)!.Key.Should().Be("Credential_FileType");
        InventoryScreenRules.CheckCertificate("coa.pdf", 0)!.Key.Should().Be("Credential_FileEmpty");
        InventoryScreenRules.CheckCertificate("coa.tif", LotCertificateFile.MaximumBytes + 1L)!.Typed.Should().Be("10");
        InventoryScreenRules.CertificateAccept.Should().Contain(".pdf").And.Contain(".tiff");
    }
}
