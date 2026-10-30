using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Orders;

/// <summary>
/// What the screens offer around the scanned prescription, the same on the workstation and in the
/// browser. The service checks the same rights again; these only keep a button from being offered
/// and then refused.
/// </summary>
public static class PrescriptionScanRules
{
    /// <summary>« Numériser l'ordonnance » and « Joindre un fichier » on « Nouvelle demande ».</summary>
    public static bool CanAttachAtRegistration(ICurrentUser user) => user.Has(Permissions.Orders.Create);

    /// <summary>« Voir » on the file: there is a scan, and the user may read the file.</summary>
    public static bool CanView(OrderDetail? detail, ICurrentUser user) =>
        detail is { HasPrescriptionScan: true } && user.Has(Permissions.Orders.View);

    /// <summary>Attach, replace, add pages, remove: the right to edit the file, on a file not cancelled.</summary>
    public static bool CanChange(OrderDetail? detail, ICurrentUser user) =>
        detail is not null && !detail.IsCancelled && user.Has(Permissions.Orders.Edit);

    /// <summary>« Retirer », and « Ajouter des pages »: as <see cref="CanChange"/>, with a scan there.</summary>
    public static bool CanRemove(OrderDetail? detail, ICurrentUser user) =>
        CanChange(detail, user) && detail!.HasPrescriptionScan;

    /// <summary>
    /// The text key and argument of the page count shown beside « Ordonnance jointe »: « 1 page »,
    /// « 3 pages », or nothing when the PDF's pages could not be counted.
    /// </summary>
    public static (string Key, int Count)? Pages(int? count) => count switch
    {
        null or <= 0 => null,
        1 => ("Order.PrescriptionOnePage", 1),
        _ => ("Order.PrescriptionPages", count.Value)
    };
}

/// <summary>
/// The prescription being put together on « Nouvelle demande », before the request exists: the
/// assembled PDF and its page count, handed to the registration when the request is saved.
/// </summary>
public sealed class PrescriptionDraft
{
    public byte[]? Content { get; private set; }
    public int? PageCount { get; private set; }

    public bool HasDocument => Content is { Length: > 0 };

    /// <summary>Keeps what <see cref="PrescriptionScanService.PrepareAsync"/> assembled.</summary>
    public void Keep(PrescriptionScanFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Content = file.Content;
        PageCount = file.PageCount;
    }

    public void Clear()
    {
        Content = null;
        PageCount = null;
    }

    /// <summary>What the registration request carries.</summary>
    public RegisterOrderRequest ApplyTo(RegisterOrderRequest request) => request with
    {
        PrescriptionScan = Content,
        PrescriptionScanContentType = HasDocument ? PrescriptionScanPolicy.PdfContentType : null
    };
}
