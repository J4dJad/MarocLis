using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Inventory;

/// <summary>
/// A supplier invoice whose purchases entry went out in an accounting export file, and when it
/// first did. An asset linked to the invoice after that keeps the invoice's entry as it was
/// exported: the next export reclassifies the asset's share from the charges to its asset account.
/// </summary>
public sealed class SupplierInvoiceExport : AggregateRoot
{
    private SupplierInvoiceExport() { }

    public SupplierInvoiceExport(Guid supplierInvoiceId, DateTimeOffset exportedAt)
    {
        SupplierInvoiceId = supplierInvoiceId;
        ExportedAt = exportedAt;
    }

    public Guid SupplierInvoiceId { get; private set; }
    public DateTimeOffset ExportedAt { get; private set; }
}
