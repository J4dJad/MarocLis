using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Inventory;

/// <summary>
/// A file kept with a purchasing record: the signed instrument contract, a supplier's quotation.
/// <para>
/// A row of its own rather than a column on the record, so that opening a contract or comparing
/// offers does not drag the scanned PDFs along. Never replaced in place: attaching a new file adds a
/// row and the latest one is the current file, so a contract's earlier version stays on record.
/// </para>
/// </summary>
public sealed class PurchasingDocument : AggregateRoot, IAuditable
{
    /// <summary>Large enough for a scanned contract, small enough not to bloat the database.</summary>
    public const int MaximumBytes = 15 * 1024 * 1024;

    private PurchasingDocument() { }

    public PurchasingDocument(PurchasingDocumentOwner ownerKind, Guid ownerId, string fileName,
                              string contentType, byte[] content, DateTimeOffset attachedAt,
                              Guid attachedByUserId)
    {
        OwnerKind = ownerKind;
        OwnerId = ownerId;
        FileName = fileName;
        ContentType = contentType;
        Content = content;
        SizeBytes = content.Length;
        AttachedAt = attachedAt;
        AttachedByUserId = attachedByUserId;
    }

    public PurchasingDocumentOwner OwnerKind { get; private set; }

    /// <summary>The contract or the supplier quotation the file belongs to.</summary>
    public Guid OwnerId { get; private set; }

    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public byte[] Content { get; private set; } = [];
    public int SizeBytes { get; private set; }

    public DateTimeOffset AttachedAt { get; private set; }
    public Guid AttachedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    /// <summary>The content type of an accepted file, or null for any other kind (see <see cref="SafeDocumentFile"/>).</summary>
    public static string? ContentTypeFor(string fileName) => SafeDocumentFile.ContentTypeFor(fileName);
}

public enum PurchasingDocumentOwner
{
    InstrumentContract = 0,
    SupplierQuote = 1
}
