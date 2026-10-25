using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Quality;

namespace MarocLIS.Application.Inventory;

/// <summary>
/// What « Stock » offers, by right and by the state of the article and of the lot selected — the
/// workstation's PermissionGate calls and RefreshLotActions. InventoryService, ReceptionService and
/// LotQualityService stay the judges: each refuses an operation the right or the state forbids.
/// </summary>
/// <param name="Receive">A lot of the selected article received (reception right).</param>
/// <param name="Issue">Stock of the selected article issued to the bench, FEFO (issue right).</param>
/// <param name="Alerts">The expiry levels and the stock-out forecast.</param>
/// <param name="Release">A quarantined lot released, with its evidence (reviewing QC).</param>
/// <param name="Reject">A lot refused: in quarantine at reception, or a released one by who may release.</param>
/// <param name="ClearRejection">A rejection recorded against the wrong lot withdrawn (adjusting stock).</param>
/// <param name="Recall">A manufacturer recall recorded (adjusting stock).</param>
/// <param name="WriteOff">What is left of the lot written off (adjusting stock).</param>
/// <param name="RecallList">The patient results produced with the lot (seeing results).</param>
/// <param name="Compare">The lot's QC against the previous lot's.</param>
/// <param name="Certificates">The certificates kept with the lot.</param>
/// <param name="AttachCertificate">A certificate attached to the lot (reception right).</param>
public sealed record InventoryScreenActions(
    bool Receive, bool Issue, bool Alerts, bool Release, bool Reject, bool ClearRejection, bool Recall,
    bool WriteOff, bool RecallList, bool Compare, bool Certificates, bool AttachCertificate);

/// <summary>How a stock line reads: the whole row in the alarm's colours, or the warning on its cells.</summary>
public enum StockRowTone
{
    Plain,

    /// <summary>A reorder point reached, an expiry within 30 days or a lot in quarantine: on the cells concerned.</summary>
    Warning,

    /// <summary>Below the safety stock, or a lot expired: the whole row.</summary>
    Critical,

    /// <summary>A critical article below its safety stock: the whole row, in bold — it stops an analysis.</summary>
    CriticalBold
}

/// <summary>A delivery as typed in the reception dialog: the workstation's ReceiveLotDialog and its checks.</summary>
public sealed class ReceiveLotForm
{
    public string? LotNumber { get; set; }
    public DateOnly ExpiryDate { get; set; }
    public string? Quantity { get; set; }
    public Guid? SupplierId { get; set; }
    public string? DeliveryNote { get; set; }
    public string? UnitPrice { get; set; }
    public string? StorageLocation { get; set; }
    public int OpenStabilityDays { get; set; }

    /// <summary>What the GS1 scanner typed in the scan box.</summary>
    public string? Scan { get; set; }

    public string? ArrivalTemperature { get; set; }

    // Each box of the checklist starts ticked: unticking one records the failure.
    public bool MatchesOrder { get; set; } = true;
    public bool PackagingIntact { get; set; } = true;
    public bool FrenchLabelling { get; set; } = true;
    public bool CertificateReceived { get; set; } = true;
}

/// <summary>
/// What the reception checks say about a delivery typed: refused on a box, or a request to send —
/// with an ambient temperature outside 15–25 °C to mention, and a non-conformity it will open to
/// confirm first.
/// </summary>
public sealed record ReceptionVerdict(FieldRefusal? Refusal, ReceiveLotRequest? Request, bool AmbientWarning,
                                      bool NeedsNonConformity);

/// <summary>The checks at the door: refused on the temperature, or the check to record — with an ambient warning and a non-conformity to confirm.</summary>
public sealed record ArrivalVerdict(FieldRefusal? Refusal, ReceptionCheck? Check, bool AmbientWarning, bool NeedsNonConformity);

/// <summary>The range line of the reception checks: required for the cold chain, advice otherwise.</summary>
public sealed record ReceptionRange(string Key, ColdChainRange Range, bool Required);

/// <summary>
/// The rules of « Stock » that need no window: what is offered by which right and in which state,
/// how an article and a lot read (the quantity, the alert glyphs, the expiry, the lot's state and
/// their inks and pills), how a delivery is typed, checked and refused box by box, what each lot
/// operation asks, and how the alerts, the recall list, the lot comparison and the certificates
/// read. The workstation's InventoryView and its dialogs and the web's screen apply the same ones;
/// every decision stays the services' and the domain's.
/// </summary>
public static class InventoryScreenRules
{
    // ---- The boxes, bounded to their columns: beyond, the save failed with « Erreur inattendue ».
    public const int LotNumberMaxLength = 60;
    public const int DeliveryNoteMaxLength = 60;
    public const int StorageMaxLength = 100;
    public const int QuantityMaxLength = TypedNumber.MeasureMaxLength;
    public const int PriceMaxLength = TypedNumber.AmountMaxLength;
    public const int TemperatureMaxLength = 8;
    public const int OpenStabilityMax = 3650;

    // ---- What each lot operation asks.
    public const int ReleaseEvidenceMinLength = 10;
    public const int ReleaseEvidenceMaxLength = 1000;
    public const int OverrideMaxLength = 1000;
    public const int RejectMaxLength = 500;
    public const int ClearRejectionMinLength = 10;
    public const int RecallReferenceMaxLength = 100;
    public const int WriteOffMaxLength = 500;
    public const int RefusalMinLength = 10;
    public const int RefusalMaxLength = 1000;

    /// <summary>A lot expiring within this many days is said in the warning's ink.</summary>
    public const int ExpirySoonDays = 30;

    public const string LotNumberField = "lot-number";
    public const string ExpiryField = "lot-expiry";
    public const string QuantityField = "lot-quantity";
    public const string PriceField = "lot-price";
    public const string ScanField = "gs1-scan";
    public const string TemperatureField = "arrival-temperature";
    public const string IssueField = "issue-quantity";
    public const string CertificateField = "certificate-file";

    /// <summary>The kinds of file a certificate is kept as: a PDF or a scan.</summary>
    public static readonly IReadOnlyList<string> CertificateExtensions = [".pdf", ".jpg", ".jpeg", ".png", ".tif", ".tiff"];

    /// <summary>The kinds of certificate file, for a picker: « .pdf,.jpg,… ».</summary>
    public static string CertificateAccept => string.Join(",", CertificateExtensions);

    /// <summary>The refusals of the release that a biologist may override with a justification.</summary>
    private static readonly HashSet<string> QcRefusals = ["Lot_ReleaseNoQcRun", "Lot_ReleaseQcRejected", "Lot_ReleaseQcLevelsMissing"];

    // ---- Rights and states ------------------------------------------------------------------------

    public static InventoryScreenActions Actions(ICurrentUser user, StockRow? item, LotRow? lot)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Actions(user.Has, item, lot);
    }

    /// <summary>
    /// Only the operations that make sense for the selected lot's state: a lot still in quarantine
    /// may be refused at reception, a released one only by whoever may release lots; a rejection is
    /// withdrawn only from a rejected lot and never from a recalled one — a manufacturer recall is
    /// not a clerical error.
    /// </summary>
    public static InventoryScreenActions Actions(Func<string, bool> has, StockRow? item, LotRow? lot)
    {
        ArgumentNullException.ThrowIfNull(has);

        var receive = has(Permissions.Inventory.Receive);
        var reviewQc = has(Permissions.Quality.ReviewQc);
        var adjust = has(Permissions.Inventory.Adjust);

        return new InventoryScreenActions(
            Receive: receive && item is not null,
            Issue: has(Permissions.Inventory.Issue) && item is not null,
            Alerts: has(Permissions.Inventory.View),
            Release: reviewQc && lot is { IsQuarantined: true, IsRejected: false, IsRecalled: false },
            Reject: (receive || reviewQc) && lot is { IsRejected: false } && (lot.IsQuarantined || reviewQc),
            ClearRejection: adjust && lot is { IsRejected: true, IsRecalled: false },
            Recall: adjust && lot is { IsRecalled: false },
            WriteOff: adjust && lot is { QuantityRemaining: > 0m },
            RecallList: lot is not null && has(Permissions.Results.View),
            Compare: lot is not null,
            Certificates: lot is not null,
            AttachCertificate: receive);
    }

    /// <summary>The article kept selected after a reload: the one worked on, else the first.</summary>
    public static StockRow? Keep(IReadOnlyList<StockRow> rows, Guid? kept)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.FirstOrDefault(r => r.StockItemId == kept) ?? (rows.Count > 0 ? rows[0] : null);
    }

    /// <summary>The lot kept selected after a reload: the one just released, refused or recalled, its new state in view.</summary>
    public static LotRow? KeepLot(IReadOnlyList<LotRow> lots, Guid? kept)
    {
        ArgumentNullException.ThrowIfNull(lots);
        return lots.FirstOrDefault(l => l.ReagentLotId == kept);
    }

    // ---- The stock -----------------------------------------------------------------------------------

    /// <summary>
    /// Glyphs rather than colour alone: the alert column says what is wrong in one or two characters
    /// that survive a monochrome print — « !! » below the safety stock, « ! » at the reorder point,
    /// « Q » a lot in quarantine, « ⏳ » an expiry within 30 days, « ★ » a critical article.
    /// </summary>
    public static string AlertGlyphs(StockRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        var alerts = new List<string>();
        if (row.BelowMinimum) alerts.Add("!!");
        else if (row.NeedsReorder) alerts.Add("!");
        if (row.QuarantinedLotCount > 0) alerts.Add(text("Inventory.QuarantinedShort"));
        if (row.DaysToEarliestExpiry is <= ExpirySoonDays) alerts.Add("⏳");
        if (row.IsCritical) alerts.Add("★");
        return string.Join(" ", alerts);
    }

    /// <summary>What each alert glyph of a line means, for its tooltip.</summary>
    public static IReadOnlyList<string> AlertMeanings(StockRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        var meanings = new List<string>();
        if (row.BelowMinimum) meanings.Add("!! " + text("Inventory.BelowMinimum"));
        else if (row.NeedsReorder) meanings.Add("! " + text("Inventory.ReorderPoint"));
        if (row.QuarantinedLotCount > 0) meanings.Add(text("Inventory.QuarantinedShort") + " " + text("Inventory.Quarantined"));
        if (row.DaysToEarliestExpiry is <= ExpirySoonDays) meanings.Add("⏳ " + text("Inventory.Expiry"));
        if (row.IsCritical) meanings.Add("★ " + text("Web.StockCritical"));
        return meanings;
    }

    /// <summary>
    /// A critical article below its safety stock leads the colouring: it stops an analysis being
    /// offered at all, which has to be declared to prescribers.
    /// </summary>
    public static StockRowTone Tone(StockRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsCritical && row.BelowMinimum) return StockRowTone.CriticalBold;
        if (row.BelowMinimum || row.DaysToEarliestExpiry is < 0) return StockRowTone.Critical;
        if (row.NeedsReorder || row.DaysToEarliestExpiry is <= ExpirySoonDays || row.QuarantinedLotCount > 0)
            return StockRowTone.Warning;
        return StockRowTone.Plain;
    }

    /// <summary>The cells that carry a warning, coloured on their own: a whole row in colour is kept for the critical cases.</summary>
    public static bool IsWarningCell(string column) => column is "onhand" or "lots" or "expiry" or "alert";

    /// <summary>« 12,50 boîte »: the usable quantity and its unit.</summary>
    public static string OnHand(StockRow row, IFormatProvider? provider = null) =>
        $"{row.QuantityOnHand.ToString("N2", provider ?? CultureInfo.CurrentCulture)} {row.UnitOfMeasure}";

    public static string ReorderPoint(StockRow row, IFormatProvider? provider = null) =>
        row.ReorderPoint.ToString("N2", provider ?? CultureInfo.CurrentCulture);

    /// <summary>« 3 (1Q) »: the lots, and how many of them are in quarantine.</summary>
    /// <summary>« 3 (1Q) »: the lots and, among them, the quarantined ones; the letter is the language's.</summary>
    public static string Lots(StockRow row, ScreenText text) =>
        row.QuarantinedLotCount > 0
            ? $"{row.LotCount.ToString(CultureInfo.InvariantCulture)} ({row.QuarantinedLotCount.ToString(CultureInfo.InvariantCulture)}{text("Inventory.QuarantinedShort")})"
            : row.LotCount.ToString(CultureInfo.InvariantCulture);

    /// <summary>« 12/03/27 (140j) »: the earliest expiry and the days left, in the language's day abbreviation.</summary>
    public static string Expiry(StockRow row, ScreenText text) =>
        row.EarliestExpiry is { } expiry
            ? Date(expiry) + (row.DaysToEarliestExpiry is { } days ? $" ({days.ToString(CultureInfo.InvariantCulture)}{text("Inventory.DaysShort")})" : string.Empty)
            : string.Empty;

    public static string Date(DateOnly date) => date.ToString("dd/MM/yy", CultureInfo.InvariantCulture);

    public static string Date(DateOnly? date) => date is { } d ? Date(d) : string.Empty;

    /// <summary>« 42 articles · 3 sous le stock minimum ».</summary>
    public static string Count(IReadOnlyCollection<StockRow> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        var alerts = rows.Count(r => r.BelowMinimum || r.NeedsReorder);
        return text("StocksUi.Items", rows.Count)
               + (alerts > 0 ? $" · {alerts.ToString(CultureInfo.InvariantCulture)} {text("Inventory.BelowMinimum").ToLowerInvariant()}" : string.Empty);
    }

    // ---- The lots ------------------------------------------------------------------------------------

    /// <summary>The lot's state: out of use, waiting for a decision, or usable — in that order of precedence.</summary>
    public static string LotStateKey(LotRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.IsRejected ? "Lot_Rejected"
            : row.IsRecalled ? "Inventory.Recalled"
            : row.IsExpired ? "Inventory.Expired"
            : row.IsBeyondOpenStability ? "Inventory.BeyondOpenStability"
            : row.IsQuarantined ? "Inventory.Quarantined"
            : "Inventory.Usable";
    }

    public static StockTone LotTone(LotRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.IsRejected || row.IsRecalled || row.IsExpired ? StockTone.Critical
            : row.IsBeyondOpenStability || row.IsQuarantined ? StockTone.Warning
            : StockTone.Done;
    }

    /// <summary>
    /// A lot out of use in the alarm's colours; one waiting for a decision, or expiring within 30
    /// days, said on its expiry and its days left.
    /// </summary>
    /// <param name="column">expiry or days carry the warning; any other is plain.</param>
    public static StockInk LotInk(LotRow row, string column)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsRejected || row.IsRecalled || row.IsExpired) return StockInk.Critical;
        if ((row.IsQuarantined || row.IsBeyondOpenStability || row.DaysToExpiry <= ExpirySoonDays) && column is "expiry" or "days")
            return StockInk.AbnormalBold;
        return StockInk.Plain;
    }

    public static string Quantity(decimal value, IFormatProvider? provider = null) =>
        value.ToString("N2", provider ?? CultureInfo.CurrentCulture);

    /// <summary>« BIO-GLU · 4 · 1 quarantaine »: whose lots, how many, and how many wait for release.</summary>
    public static string LotNote(StockRow? item, IReadOnlyCollection<LotRow> lots, bool onlyQuarantined, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(lots);
        ArgumentNullException.ThrowIfNull(text);
        var quarantined = lots.Count(l => l.IsQuarantined && !l.IsRejected);
        var code = onlyQuarantined ? null : item?.Code;
        return (code is null ? string.Empty : $"{code} · ") + lots.Count.ToString(CultureInfo.InvariantCulture)
               + (quarantined > 0 ? $" · {quarantined.ToString(CultureInfo.InvariantCulture)} {text("Inventory.Quarantined").ToLowerInvariant()}" : string.Empty);
    }

    /// <summary>Says which priority purchase order the articles that reached their reorder point went on; none when nothing was drafted.</summary>
    public static string? ReorderMessage(ReorderOutcome outcome, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(text);
        return outcome.Any
            ? text("Inventory.ReorderDrafted", string.Join(", ", outcome.PurchaseOrders), string.Join(", ", outcome.DraftedItems))
            : null;
    }

    /// <summary>
    /// Names the lots drawn from: FEFO means the operator did not choose them, and lot traceability
    /// means they need to know which went to the bench.
    /// </summary>
    public static string Issued(string lots, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return $"{text("Inventory.Issue")} — {text("Inventory.Lot").ToLowerInvariant()} {lots}";
    }

    // ---- An issue ------------------------------------------------------------------------------------

    /// <summary>The question of an issue: the article, its unit and what is on hand.</summary>
    public static string IssuePrompt(StockRow item, ScreenText text, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(text);
        return text("Inventory.IssueQuantityPrompt", item.Code, item.UnitOfMeasure,
                    item.QuantityOnHand.ToString("N2", provider ?? CultureInfo.CurrentCulture));
    }

    /// <summary>The quantity issued: a number above zero, else the text typed quoted in the refusal.</summary>
    public static (decimal? Quantity, FieldRefusal? Refusal) ReadIssue(string? typed) =>
        TypedNumber.Parse(typed) is { } quantity && quantity > 0m
            ? (quantity, null)
            : (null, new FieldRefusal(IssueField, "Result_NotANumber", typed?.Trim() ?? string.Empty));

    // ---- The lot's operations ------------------------------------------------------------------------

    /// <summary>
    /// A release refused for want of conforming QC on the lot: a biologist may release it by
    /// derogation, with a justification that stays on the lot and in the audit trail.
    /// </summary>
    public static bool OffersQcOverride(Error? refusal, Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(has);
        return refusal is not null && QcRefusals.Contains(refusal.ResourceKey)
               && has(Permissions.Results.BiologicalValidate);
    }

    /// <summary>The question of a derogation: why the release was refused, then what to justify.</summary>
    public static string OverridePrompt(string refusal, LotRow lot, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(lot);
        ArgumentNullException.ThrowIfNull(text);
        return $"{refusal}\n\n" + text("Lot.QcOverrideJustificationPrompt", lot.LotNumber, ReagentLot.QcOverrideJustificationMinLength);
    }

    /// <summary>An expired lot is written off as an expiry; any other as damage.</summary>
    public static StockMovementKind WriteOffKind(LotRow lot)
    {
        ArgumentNullException.ThrowIfNull(lot);
        return lot.IsExpired ? StockMovementKind.ExpiryWriteOff : StockMovementKind.DamageWriteOff;
    }

    public static string WriteOffPrompt(LotRow lot, ScreenText text, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(lot);
        ArgumentNullException.ThrowIfNull(text);
        return text("Inventory.WriteOffPrompt", lot.LotNumber, lot.QuantityRemaining.ToString("N2", provider ?? CultureInfo.CurrentCulture));
    }

    /// <summary>« GLU-2611 — Utilisable »: what a release leaves the lot as.</summary>
    public static string StateAfter(LotRow lot, string stateKey, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(lot);
        ArgumentNullException.ThrowIfNull(text);
        return $"{lot.LotNumber} — {text(stateKey)}";
    }

    // ---- A reception ---------------------------------------------------------------------------------

    /// <summary>A new delivery's form: expiring in a year, the checklist ticked.</summary>
    public static ReceiveLotForm NewReception(DateOnly today) => new() { ExpiryDate = today.AddMonths(12) };

    /// <summary>The range line of the checks, by the article's storage condition.</summary>
    public static ReceptionRange Range(StorageCondition condition) =>
        ColdChain.RequiresTemperature(condition)
            ? new ReceptionRange("Reception.RangeRequired", ColdChain.Range(condition), true)
            : new ReceptionRange("Reception.RangeAdvisory", ColdChain.Range(condition), false);

    /// <summary>
    /// Checks a delivery as the dialog does before it closes: the lot number, a quantity above zero,
    /// a price that reads as one (typed with its currency it was saved as no price), an expiry not
    /// already past, a temperature that reads as one and is there for a cold-chain article. An
    /// out-of-range temperature or a box unticked is not refused: the delivery may still be taken
    /// in, quarantined, with its non-conformity — which is asked about first.
    /// </summary>
    public static ReceptionVerdict CheckReception(ReceiveLotForm form, Guid stockItemId, StorageCondition condition, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (string.IsNullOrWhiteSpace(form.LotNumber))
            return Refused(new FieldRefusal(LotNumberField, "Lot_NumberRequired"));
        if (form.ExpiryDate < today)
            return Refused(new FieldRefusal(ExpiryField, "Lot_AlreadyExpired", form.ExpiryDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)));
        if (TypedNumber.Parse(form.Quantity) is not { } quantity || quantity <= 0m)
            return Refused(new FieldRefusal(QuantityField, "Lot_QuantityMustBePositive"));
        if (TypedNumber.IsUnreadable(form.UnitPrice))
            return Refused(new FieldRefusal(PriceField, "Result_NotANumber", form.UnitPrice!.Trim()));

        var arrival = CheckArrival(form, condition);
        if (arrival.Refusal is not null) return Refused(arrival.Refusal);

        var request = new ReceiveLotRequest
        {
            Reception = arrival.Check,
            StockItemId = stockItemId,
            LotNumber = form.LotNumber.Trim(),
            ExpiryDate = form.ExpiryDate,
            Quantity = quantity,
            SupplierId = form.SupplierId,
            DeliveryNoteReference = Blank(form.DeliveryNote),
            UnitPriceMad = TypedNumber.Parse(form.UnitPrice),
            StorageLocation = Blank(form.StorageLocation),
            StabilityAfterOpeningDays = form.OpenStabilityDays > 0 ? Math.Min(form.OpenStabilityDays, OpenStabilityMax) : null
        };

        return new ReceptionVerdict(null, request, arrival.AmbientWarning, arrival.NeedsNonConformity);
    }

    /// <summary>
    /// The checks at the door alone — the temperature and the checklist, shared by the direct
    /// receipt and the purchase-order receipt: a temperature that reads as one, present for a
    /// cold-chain article; the rest is accepted and reported.
    /// </summary>
    public static ArrivalVerdict CheckArrival(ReceiveLotForm form, StorageCondition condition)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (TypedNumber.IsUnreadable(form.ArrivalTemperature))
            return new ArrivalVerdict(new FieldRefusal(TemperatureField, "Result_NotANumber", form.ArrivalTemperature!.Trim()), null, false, false);

        var check = Check(form);
        var assessment = ReceptionAssessment.Assess(condition, check);
        if (assessment.IsFailure)
            return new ArrivalVerdict(new FieldRefusal(TemperatureField, assessment.FirstError.ResourceKey, Argument: ColdChain.Range(condition)),
                                      null, false, false);

        return new ArrivalVerdict(null, check, assessment.Value.Temperature == TemperatureVerdict.Warning,
                                  assessment.Value.NeedsNonConformity);
    }

    private static ReceptionVerdict Refused(FieldRefusal refusal) => new(refusal, null, false, false);

    /// <summary>The checks as ticked: the temperature read as the editors read numbers.</summary>
    public static ReceptionCheck Check(ReceiveLotForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return new ReceptionCheck
        {
            ArrivalTemperatureCelsius = TypedNumber.Parse(form.ArrivalTemperature),
            MatchesOrder = form.MatchesOrder,
            PackagingIntact = form.PackagingIntact,
            FrenchLabelling = form.FrenchLabelling,
            CertificateOfAnalysisReceived = form.CertificateReceived
        };
    }

    /// <summary>
    /// A DataMatrix read: the lot and the expiry go into their boxes. An expiry already past is not
    /// taken, and said on its box.
    /// </summary>
    public static FieldRefusal? ApplyScan(ReceiveLotForm form, Gs1Data data, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(data);
        if (data.LotNumber is { } lot) form.LotNumber = lot;
        if (data.ExpiryDate is not { } expiry) return null;
        if (expiry < today) return new FieldRefusal(ExpiryField, "Lot_AlreadyExpired", expiry.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
        form.ExpiryDate = expiry;
        return null;
    }

    /// <summary>A delivery refused as a whole at the door, with what was typed of it.</summary>
    public static RefuseDeliveryRequest Refusal(ReceiveLotForm form, Guid stockItemId, string reason)
    {
        ArgumentNullException.ThrowIfNull(form);
        return new RefuseDeliveryRequest
        {
            SupplierId = form.SupplierId,
            StockItemId = stockItemId,
            Reason = reason,
            ArrivalTemperatureCelsius = TypedNumber.Parse(form.ArrivalTemperature),
            DeliveryNoteReference = Blank(form.DeliveryNote),
            LotNumber = Blank(form.LotNumber)
        };
    }

    /// <summary>The box a refusal of the reception names.</summary>
    public static string? FieldOf(Error? error) => error?.ResourceKey switch
    {
        "Lot_NumberRequired" or "Lot_ReceiveIntoRefusedLot" or "Lot_ReceiveNonConformingIntoReleased" => LotNumberField,
        "Lot_AlreadyExpired" or "Lot_ReceiveExpiryDiffers" => ExpiryField,
        "Lot_QuantityMustBePositive" => QuantityField,
        "Reception_TemperatureRequired" => TemperatureField,
        "Credential_FileTooLarge" or "Credential_FileType" or "Credential_FileEmpty" => CertificateField,
        _ => null
    };

    // ---- The alerts ----------------------------------------------------------------------------------

    /// <summary>A lot expiring at the critical level or expired is urgent: the alarm's colours; else the warning's.</summary>
    public static StockTone ExpiryTone(ExpiryAlertLevel level) =>
        level is ExpiryAlertLevel.Critical or ExpiryAlertLevel.Expired ? StockTone.Critical : StockTone.Warning;

    public static string AlertsNotice(StockAlertSummary alerts, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(text);
        return text("Stock.AlertsNotice", alerts.Settings.ExpiryWarningDays, alerts.Settings.ExpiryCriticalDays,
                    alerts.Settings.ConsumptionWindowDays);
    }

    public static string Amount(decimal value, IFormatProvider? provider = null) =>
        value.ToString("0.###", provider ?? CultureInfo.CurrentCulture);

    public static string LongDate(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    // ---- The recall list -----------------------------------------------------------------------------

    public static IReadOnlyList<string> RecallHeaders(ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            text("Recall.Patient"), text("Recall.MedicalRecord"), text("Recall.Order"), text("Recall.Analysis"),
            text("Recall.Parameter"), text("Recall.ResultDate"), text("Recall.Validated")
        ];
    }

    public static string ResultDate(RecallResultRow row) =>
        row.ResultDate?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string Validated(RecallResultRow row, ScreenText text) => row.IsValidated ? text("Recall.Yes") : text("Recall.No");

    /// <summary>The name the recall list is handed over as.</summary>
    public static string RecallFileName(string lotNumber, DateOnly today) =>
        $"rappel-{SafeName(lotNumber)}-{today:yyyyMMdd}.csv";

    // ---- The lot comparison --------------------------------------------------------------------------

    public static string Statistic(decimal? value, IFormatProvider? provider = null) =>
        value?.ToString("0.###", provider ?? CultureInfo.CurrentCulture) ?? "—";

    /// <summary>« +2,4 % »: the shift of the mean, signed.</summary>
    public static string Difference(decimal? percent, IFormatProvider? provider = null) =>
        percent is { } d ? d.ToString("+0.##;-0.##;0", provider ?? CultureInfo.CurrentCulture) + " %" : "—";

    public static string ComparisonTitle(LotComparisonView comparison, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(text);
        return text("LotComparison.Title", comparison.ItemCode, comparison.NewLotNumber, comparison.PreviousLotNumber ?? "—");
    }

    public static string ComparisonNoticeKey(LotComparisonView comparison) =>
        comparison.Rows.Count == 0 ? "LotComparison.NoData" : "LotComparison.Notice";

    // ---- The certificates ----------------------------------------------------------------------------

    /// <summary>« certificat.pdf · 12/10/2026 ».</summary>
    public static string CertificateLine(LotCertificateRow row) =>
        $"{row.FileName}  ·  {row.AddedAt.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}";

    /// <summary>A certificate chosen: a PDF or a scan, not empty, not past the size the service keeps.</summary>
    public static FieldRefusal? CheckCertificate(string? fileName, long length)
    {
        if (string.IsNullOrWhiteSpace(fileName) || length <= 0) return new FieldRefusal(CertificateField, "Credential_FileEmpty");
        if (!CertificateExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant()))
            return new FieldRefusal(CertificateField, "Credential_FileType");
        if (length > LotCertificateFile.MaximumBytes)
            return new FieldRefusal(CertificateField, "Credential_FileTooLarge",
                                    (LotCertificateFile.MaximumBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture));
        return null;
    }

    private static string SafeName(string text) =>
        new(text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
