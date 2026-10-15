using System.Globalization;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Quality;

namespace MarocLIS.Domain.Inventory;

/// <summary>
/// What the storeman checked when a delivery arrived. Every field is optional so a caller that
/// records nothing (an import, an old screen) is not refused; the service decides what is required.
/// </summary>
public sealed record ReceptionCheck
{
    /// <summary>Temperature read in the parcel (probe or indicator), °C.</summary>
    public decimal? ArrivalTemperatureCelsius { get; init; }

    /// <summary>The goods match the purchase order and the delivery note.</summary>
    public bool? MatchesOrder { get; init; }

    public bool? PackagingIntact { get; init; }

    /// <summary>Label and instructions for use in French.</summary>
    public bool? FrenchLabelling { get; init; }

    public bool? CertificateOfAnalysisReceived { get; init; }
}

public enum TemperatureVerdict
{
    /// <summary>No temperature taken, and none was needed.</summary>
    NotRecorded = 0,
    Conform = 1,
    /// <summary>Ambient goods outside 15–25 °C: worth saying, not a non-conformity.</summary>
    Warning = 2,
    /// <summary>Cold-chain goods outside their range: the delivery is suspect.</summary>
    OutOfRange = 3,
    /// <summary>Cold-chain goods received without a temperature.</summary>
    Missing = 4
}

/// <summary>
/// The cold-chain rules applied at reception. The ranges are the usual transport tolerances for IVD
/// reagents: 2–8 °C for refrigerated goods, at or below −15 °C for "−20" goods (dry ice shipments
/// arrive colder, never warmer) and at or below −60 °C for "−80" goods.
/// </summary>
public static class ColdChain
{
    public static bool RequiresTemperature(StorageCondition condition) =>
        condition is StorageCondition.Refrigerated or StorageCondition.Frozen or StorageCondition.DeepFrozen;

    public static TemperatureVerdict Evaluate(StorageCondition condition, decimal? temperature)
    {
        if (temperature is not { } t)
            return RequiresTemperature(condition) ? TemperatureVerdict.Missing : TemperatureVerdict.NotRecorded;

        return condition switch
        {
            StorageCondition.Refrigerated => t is >= 2m and <= 8m ? TemperatureVerdict.Conform : TemperatureVerdict.OutOfRange,
            StorageCondition.Frozen => t <= -15m ? TemperatureVerdict.Conform : TemperatureVerdict.OutOfRange,
            StorageCondition.DeepFrozen => t <= -60m ? TemperatureVerdict.Conform : TemperatureVerdict.OutOfRange,
            // Ambient, protect-from-light and desiccated goods: 15–25 °C is advice, not a rule.
            _ => t is >= 15m and <= 25m ? TemperatureVerdict.Conform : TemperatureVerdict.Warning
        };
    }

    /// <summary>The acceptable range, as printed in a message or on a non-conformity.</summary>
    public static string RangeText(StorageCondition condition) => condition switch
    {
        StorageCondition.Refrigerated => "2 à 8 °C",
        StorageCondition.Frozen => "≤ −15 °C",
        StorageCondition.DeepFrozen => "≤ −60 °C",
        _ => "15 à 25 °C"
    };
}

/// <summary>A reception check that failed and has to be recorded as a non-conformity.</summary>
public enum ReceptionFinding
{
    TemperatureOutOfRange = 0,
    NotMatchingOrder = 1,
    PackagingDamaged = 2,
    LabellingNotInFrench = 3,
    CertificateOfAnalysisMissing = 4
}

/// <summary>
/// The verdict on a delivery's reception checks: the temperature against the item's storage
/// condition, and every checklist item explicitly answered "no".
/// </summary>
public sealed record ReceptionAssessment(TemperatureVerdict Temperature, IReadOnlyList<ReceptionFinding> Findings)
{
    public bool NeedsNonConformity => Findings.Count > 0;

    /// <summary>
    /// Major when the product itself may be damaged (temperature, packaging); minor for a paperwork
    /// defect, which is contained by asking the supplier for the missing document.
    /// </summary>
    public NonConformitySeverity Severity =>
        Findings.Any(f => f is ReceptionFinding.TemperatureOutOfRange or ReceptionFinding.PackagingDamaged)
            ? NonConformitySeverity.Major
            : NonConformitySeverity.Minor;

    /// <summary>
    /// Assesses a reception. Refused only when a cold-chain item comes without its temperature:
    /// the check is the point of receiving it at all. Everything else is accepted and reported.
    /// </summary>
    public static Result<ReceptionAssessment> Assess(StorageCondition condition, ReceptionCheck? check)
    {
        var verdict = ColdChain.Evaluate(condition, check?.ArrivalTemperatureCelsius);

        if (verdict == TemperatureVerdict.Missing)
            return Result.Failure<ReceptionAssessment>(
                Error.Validation("Reception_TemperatureRequired", ColdChain.RangeText(condition)));

        var findings = new List<ReceptionFinding>();
        if (verdict == TemperatureVerdict.OutOfRange) findings.Add(ReceptionFinding.TemperatureOutOfRange);
        if (check?.MatchesOrder == false) findings.Add(ReceptionFinding.NotMatchingOrder);
        if (check?.PackagingIntact == false) findings.Add(ReceptionFinding.PackagingDamaged);
        if (check?.FrenchLabelling == false) findings.Add(ReceptionFinding.LabellingNotInFrench);
        if (check?.CertificateOfAnalysisReceived == false) findings.Add(ReceptionFinding.CertificateOfAnalysisMissing);

        return Result.Success(new ReceptionAssessment(verdict, findings));
    }

    /// <summary>The findings in French, for the non-conformity description.</summary>
    public string DescribeFr(StorageCondition condition, decimal? temperature) =>
        string.Join(" ; ", Findings.Select(f => f switch
        {
            ReceptionFinding.TemperatureOutOfRange =>
                $"température à l'arrivée {Format(temperature)} °C hors de la plage {ColdChain.RangeText(condition)}",
            ReceptionFinding.NotMatchingOrder => "livraison non conforme à la commande ou au bon de livraison",
            ReceptionFinding.PackagingDamaged => "emballage endommagé",
            ReceptionFinding.LabellingNotInFrench => "étiquetage ou notice non disponible en français",
            _ => "certificat d'analyse non reçu"
        }));

    public static string Format(decimal? temperature) =>
        temperature?.ToString("0.#", CultureInfo.GetCultureInfo("fr-FR")) ?? "—";
}

/// <summary>
/// A delivery refused as a whole at the door. Nothing enters the stock, but the refusal is kept:
/// it is evidence for the supplier's evaluation and for the non-conformity it opens.
/// </summary>
public sealed class RefusedDelivery : AggregateRoot, IAuditable, ISiteScoped
{
    private RefusedDelivery() { }

    public RefusedDelivery(Guid siteId, Guid? supplierId, Guid? purchaseOrderId, DateOnly refusedOn,
                           DateTimeOffset refusedAt, Guid refusedByUserId, string reason)
    {
        SiteId = siteId;
        SupplierId = supplierId;
        PurchaseOrderId = purchaseOrderId;
        RefusedOn = refusedOn;
        RefusedAt = refusedAt;
        RefusedByUserId = refusedByUserId;
        Reason = reason;
    }

    public Guid SiteId { get; set; }
    public Guid? SupplierId { get; private set; }
    public Guid? PurchaseOrderId { get; private set; }

    /// <summary>The article concerned, when the refusal was made from the stock screen.</summary>
    public Guid? StockItemId { get; set; }

    public DateOnly RefusedOn { get; private set; }
    public DateTimeOffset RefusedAt { get; private set; }
    public Guid RefusedByUserId { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public decimal? ArrivalTemperatureCelsius { get; set; }
    public string? DeliveryNoteReference { get; set; }
    public string? LotNumber { get; set; }

    public Guid? NonConformityId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }
}

/// <summary>
/// A certificate kept with a lot: certificate of analysis, CE declaration of conformity. Stored in
/// the database like the scans of the personnel file, and loaded only when opened.
/// </summary>
public sealed class LotCertificateFile : AuditableAggregateRoot
{
    public const int MaximumBytes = 10 * 1024 * 1024;

    private LotCertificateFile() { }

    public LotCertificateFile(Guid reagentLotId, string fileName, string contentType, byte[] content)
    {
        ReagentLotId = reagentLotId;
        FileName = fileName;
        ContentType = contentType;
        Content = content;
    }

    public Guid ReagentLotId { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public byte[] Content { get; private set; } = [];

    /// <summary>PDF and the usual picture formats, as for the personnel scans.</summary>
    public static string? ContentTypeOf(string fileName) => Path.GetExtension(fileName).ToUpperInvariant() switch
    {
        ".PDF" => "application/pdf",
        ".JPG" or ".JPEG" => "image/jpeg",
        ".PNG" => "image/png",
        ".TIF" or ".TIFF" => "image/tiff",
        _ => null
    };
}
