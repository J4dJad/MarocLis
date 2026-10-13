namespace MarocLIS.Domain.Common;

/// <summary>
/// Base class for every persisted entity. Identity is a GUID generated client-side so that
/// reception desks on a LAN can create orders offline-ish without round-tripping to SQL Server
/// for an IDENTITY value, and so that merges between sites never collide.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();

    /// <summary>SQL Server <c>rowversion</c>. Guards against two workstations editing the same record.</summary>
    public byte[]? RowVersion { get; set; }

    private readonly List<IDomainEvent> _domainEvents = [];
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent e) => _domainEvents.Add(e);
    public void ClearDomainEvents() => _domainEvents.Clear();

    public override bool Equals(object? obj) =>
        obj is Entity other && GetType() == other.GetType() && Id == other.Id && Id != Guid.Empty;

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}

/// <summary>
/// Consistency boundary. Only aggregate roots are fetched by repositories; everything else is
/// reached through its root so invariants cannot be bypassed.
/// </summary>
public abstract class AggregateRoot : Entity;

public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// Who/when stamps written automatically by the EF Core <c>AuditSaveChangesInterceptor</c>.
/// Required by ISO 15189 §5.8 and by loi 08-09 traceability obligations.
/// </summary>
public interface IAuditable
{
    DateTimeOffset CreatedAt { get; set; }
    Guid? CreatedByUserId { get; set; }
    DateTimeOffset? ModifiedAt { get; set; }
    Guid? ModifiedByUserId { get; set; }
}

/// <summary>
/// Clinical and accounting records are never hard-deleted: Moroccan medical-record retention is
/// 10 years minimum, and DGI invoice numbering must stay unbroken. Deletion is logical only.
/// </summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; set; }
    DateTimeOffset? DeletedAt { get; set; }
    Guid? DeletedByUserId { get; set; }
    string? DeletionReason { get; set; }
}

/// <summary>Marks records that belong to a single laboratory site (multi-site / franchise support).</summary>
public interface ISiteScoped
{
    Guid SiteId { get; set; }
}

public abstract class AuditableAggregateRoot : AggregateRoot, IAuditable
{
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }
}

/// <summary>Abstracted so tests and audit stamping are deterministic.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
    DateOnly Today { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
    public DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
}

public sealed class DomainException(string message) : Exception(message);
