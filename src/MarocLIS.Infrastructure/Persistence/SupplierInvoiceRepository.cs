using MarocLIS.Domain.Common;
using MarocLIS.Domain.Inventory;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Persistence;

public sealed class SupplierInvoiceRepository(LisDbContext context, IClock clock)
    : Repository<SupplierInvoice>(context, clock)
{
    /// <summary>Lines, variances and payments: the match, the release and every payment work across them.</summary>
    protected override Task<SupplierInvoice?> LoadWithDetailsAsync(Guid id, CancellationToken ct = default) =>
        Context.SupplierInvoices
               .Include(i => i.Lines)
               .Include(i => i.Variances)
               .Include(i => i.Payments)
               .AsSplitQuery()
               .FirstOrDefaultAsync(i => i.Id == id, ct);
}
