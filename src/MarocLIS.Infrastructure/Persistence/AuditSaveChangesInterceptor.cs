using System.Text.Json;
using System.Text.Json.Nodes;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MarocLIS.Infrastructure.Persistence;

/// <summary>
/// Pay and bank details kept out of the audit trail.
/// <para>
/// The audit trail is read by the quality manager and the administrator, neither of whom is meant
/// to see salaries (<c>hr.salary</c> guards them everywhere else). The entry still says that the
/// salary or the RIB changed, when and by whom — which is what an audit asks — but not to what.
/// Rows written before this existed are masked when read, through <see cref="Mask"/>.
/// </para>
/// </summary>
public static class AuditRedaction
{
    /// <summary>What a redacted value is recorded as.</summary>
    public static readonly object Marker = new { changed = true, redacted = true };

    private static readonly Dictionary<string, HashSet<string>> Redacted = new(StringComparer.Ordinal)
    {
        [nameof(Domain.HumanResources.Employee)] = new(StringComparer.Ordinal)
        {
            nameof(Domain.HumanResources.Employee.BaseSalary),
            nameof(Domain.HumanResources.Employee.Rib),
            nameof(Domain.HumanResources.Employee.BankName)
        },
        [nameof(Domain.HumanResources.Payslip)] = new(StringComparer.Ordinal)
        {
            nameof(Domain.HumanResources.Payslip.BaseSalary),
            nameof(Domain.HumanResources.Payslip.Rib),
            nameof(Domain.HumanResources.Payslip.BankName),
            nameof(Domain.HumanResources.Payslip.Amounts),
            nameof(Domain.HumanResources.Payslip.Variables)
        },
        [nameof(Domain.HumanResources.FinalSettlement)] = new(StringComparer.Ordinal)
        {
            nameof(Domain.HumanResources.FinalSettlement.ReferenceSalary),
            nameof(Domain.HumanResources.FinalSettlement.LeaveIndemnity),
            nameof(Domain.HumanResources.FinalSettlement.NoticeIndemnity),
            nameof(Domain.HumanResources.FinalSettlement.SeverancePay)
        }
    };

    /// <summary>Entity names whose audit payload may carry pay or bank details.</summary>
    public static IReadOnlyCollection<string> EntityNames => Redacted.Keys;

    public static bool IsRedacted(string entityName, string propertyName) =>
        Redacted.TryGetValue(entityName, out var properties)
        && (properties.Contains(propertyName)
            // A complex property's columns are named "Amounts.NetPay" in some EF versions.
            || properties.Contains(propertyName.Split('.', 2)[0]));

    /// <summary>
    /// Masks the redacted values of a stored payload, for rows written before redaction existed.
    /// Leaves anything else, and anything that is not the JSON object the interceptor writes, as is.
    /// </summary>
    public static string? Mask(string entityName, string? changes)
    {
        if (changes is null || !Redacted.ContainsKey(entityName)) return changes;

        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(changes) as JsonObject;
        }
        catch (JsonException)
        {
            return changes;
        }

        if (payload is null) return changes;

        var masked = false;
        foreach (var key in payload.Select(p => p.Key).ToList())
        {
            if (!IsRedacted(entityName, key)) continue;
            payload[key] = JsonSerializer.SerializeToNode(Marker);
            masked = true;
        }

        return masked ? payload.ToJsonString() : changes;
    }
}

/// <summary>
/// Stamps who/when onto auditable entities and writes an <see cref="AuditEntry"/> for every change,
/// inside the same transaction as the change itself.
/// <para>
/// Placing this in an interceptor rather than in each use case is the whole point: ISO 15189 §5.8
/// and loi 08-09 both require the laboratory to be able to say who altered a record and when, and
/// a rule that depends on every developer remembering to call a logger is a rule that will be
/// broken. Here it is structurally impossible to save a change without auditing it.
/// </para>
/// </summary>
public sealed class AuditSaveChangesInterceptor(
    ICurrentUser? currentUser,
    IClock clock) : SaveChangesInterceptor
{
    /// <summary>
    /// Columns never copied into the audit payload. Password hashes and stored documents are
    /// excluded on principle: the audit trail records that they changed, not their contents.
    /// </summary>
    private static readonly HashSet<string> RedactedProperties = new(StringComparer.Ordinal)
    {
        nameof(Domain.Identity.AppUser.PasswordHash),
        nameof(Domain.Identity.AppUser.SecurityStamp),
        "RowVersion"
    };

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData,
                                                          InterceptionResult<int> result)
    {
        if (eventData.Context is not null) Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null) Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext context)
    {
        var now = clock.Now;
        var userId = currentUser is { IsAuthenticated: true } ? currentUser.UserId : (Guid?)null;
        var userName = currentUser is { IsAuthenticated: true } ? currentUser.UserName : null;
        var workstation = currentUser?.Workstation ?? Environment.MachineName;
        // A service account (the analyser host) works for no site in particular: its entries take
        // the site of the record they touch, or none.
        var siteId = currentUser is { IsAuthenticated: true } && currentUser.SiteId != Guid.Empty
            ? currentUser.SiteId
            : (Guid?)null;
        var reason = (context as LisDbContext) is not null ? PendingReason : null;

        // One correlation identifier per save, so every entry written by a single use case can be
        // pulled back together — a result revision touches the result, the item and the order.
        var correlationId = Guid.CreateVersion7();

        context.ChangeTracker.DetectChanges();

        var audited = new List<AuditEntry>();

        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is AuditEntry or DataAccessLog or SecurityEvent) continue;
            if (entry.State is EntityState.Detached or EntityState.Unchanged) continue;

            StampAuditableFields(entry, now, userId);

            var operation = ResolveOperation(entry);
            if (operation is null) continue;

            var changes = CaptureChanges(entry, operation.Value);

            // An update that touched nothing but the row version is not worth a row.
            if (operation == AuditOperation.Update && changes.Count == 0) continue;

            var entity = entry.Entity;

            audited.Add(new AuditEntry(entry.Metadata.ClrType.Name, ExtractId(entry), operation.Value, now)
            {
                UserId = userId,
                UserName = userName,
                Workstation = workstation,
                OsUser = Environment.UserName,
                SiteId = entity is ISiteScoped scoped ? scoped.SiteId : siteId,
                CorrelationId = correlationId,
                Reason = reason,
                PatientId = ResolvePatientId(entity),
                AggregateId = ResolveAggregateId(entity),
                AggregateName = ResolveAggregateName(entity),
                ChangedProperties = changes.Count == 0 ? null : string.Join(',', changes.Keys),
                Changes = changes.Count == 0 ? null : JsonSerializer.Serialize(changes, JsonOptions)
            });
        }

        if (audited.Count > 0) context.Set<AuditEntry>().AddRange(audited);
    }

    /// <summary>
    /// Business justification for the current save, set by use cases the regulation requires a
    /// reason for. Held in an async-local so it travels with the logical operation without every
    /// repository method growing a parameter.
    /// </summary>
    private static readonly System.Threading.AsyncLocal<string?> ReasonSlot = new();

    public static string? PendingReason
    {
        get => ReasonSlot.Value;
        set => ReasonSlot.Value = value;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static void StampAuditableFields(EntityEntry entry, DateTimeOffset now, Guid? userId)
    {
        if (entry.Entity is not IAuditable auditable) return;

        switch (entry.State)
        {
            case EntityState.Added:
                auditable.CreatedAt = auditable.CreatedAt == default ? now : auditable.CreatedAt;
                auditable.CreatedByUserId ??= userId;
                break;

            case EntityState.Modified:
                auditable.ModifiedAt = now;
                auditable.ModifiedByUserId = userId;
                // Creation stamps are not re-writable; EF would otherwise send them on every update.
                entry.Property(nameof(IAuditable.CreatedAt)).IsModified = false;
                entry.Property(nameof(IAuditable.CreatedByUserId)).IsModified = false;
                break;
        }
    }

    private static AuditOperation? ResolveOperation(EntityEntry entry) => entry.State switch
    {
        EntityState.Added => AuditOperation.Insert,
        EntityState.Deleted => AuditOperation.Delete,
        EntityState.Modified when IsSoftDeletion(entry) => AuditOperation.SoftDelete,
        EntityState.Modified => AuditOperation.Update,
        _ => null
    };

    private static bool IsSoftDeletion(EntityEntry entry)
    {
        if (entry.Entity is not ISoftDeletable) return false;

        var property = entry.Properties.FirstOrDefault(
            p => p.Metadata.Name == nameof(ISoftDeletable.IsDeleted));

        return property is { IsModified: true, CurrentValue: true };
    }

    /// <summary>
    /// Captures the before/after of each changed column. Only modified properties are stored: a
    /// full row snapshot per change would outgrow the clinical data itself on SQL Server Express.
    /// </summary>
    private static Dictionary<string, object?> CaptureChanges(EntityEntry entry, AuditOperation operation)
    {
        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;

            if (RedactedProperties.Contains(name)) continue;

            // Pay and bank details: the change is recorded, never the amount or the account.
            if (AuditRedaction.IsRedacted(entry.Metadata.ClrType.Name, name))
            {
                if (operation == AuditOperation.Update && !property.IsModified) continue;
                if (operation == AuditOperation.Update && Equals(property.OriginalValue, property.CurrentValue)) continue;
                if (operation == AuditOperation.Insert && property.CurrentValue is null) continue;
                changes[name] = AuditRedaction.Marker;
                continue;
            }

            // Large binary payloads are recorded as a size, not a copy.
            if (property.Metadata.ClrType == typeof(byte[]))
            {
                if (operation == AuditOperation.Update && !property.IsModified) continue;
                var length = (property.CurrentValue as byte[])?.Length ?? 0;
                changes[name] = new { changed = true, bytes = length };
                continue;
            }

            switch (operation)
            {
                case AuditOperation.Insert:
                    if (property.CurrentValue is not null)
                        changes[name] = property.CurrentValue;
                    break;

                case AuditOperation.Delete:
                    changes[name] = property.OriginalValue;
                    break;

                default:
                    if (!property.IsModified) continue;
                    if (Equals(property.OriginalValue, property.CurrentValue)) continue;
                    changes[name] = new { old = property.OriginalValue, @new = property.CurrentValue };
                    break;
            }
        }

        // Complex properties — a payslip's computed amounts and monthly variables — are not in
        // entry.Properties. Those that are redacted are recorded as changed, as a whole.
        foreach (var complex in entry.ComplexProperties)
        {
            var name = complex.Metadata.Name;
            if (!AuditRedaction.IsRedacted(entry.Metadata.ClrType.Name, name)) continue;

            if (operation == AuditOperation.Update
                && !complex.Properties.Any(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)))
                continue;

            changes[name] = AuditRedaction.Marker;
        }

        return changes;
    }

    private static Guid ExtractId(EntityEntry entry) =>
        entry.Entity is Entity typed ? typed.Id : Guid.Empty;

    /// <summary>
    /// Denormalises the patient an entry concerns, so a data-subject access request is one indexed
    /// query rather than a walk across six tables.
    /// </summary>
    private static Guid? ResolvePatientId(object entity) => entity switch
    {
        Patient patient => patient.Id,
        PatientCoverage coverage => coverage.PatientId,
        PatientConsent consent => consent.PatientId,
        PatientAlert alert => alert.PatientId,
        LabOrder order => order.PatientId,
        _ => null
    };

    private static Guid? ResolveAggregateId(object entity) => entity switch
    {
        LabOrderItem item => item.LabOrderId,
        TestResult result => result.LabOrderId,
        ResultRevision revision => revision.TestResultId,
        SampleEvent sampleEvent => sampleEvent.SampleId,
        Domain.Billing.InvoiceLine line => line.InvoiceId,
        Domain.Billing.Payment payment => payment.InvoiceId,
        _ => entity is AggregateRoot root ? root.Id : null
    };

    private static string? ResolveAggregateName(object entity) => entity switch
    {
        LabOrderItem or TestResult => nameof(LabOrder),
        ResultRevision => nameof(TestResult),
        SampleEvent => nameof(Sample),
        Domain.Billing.InvoiceLine or Domain.Billing.Payment => nameof(Domain.Billing.Invoice),
        _ => entity.GetType().Name
    };
}
