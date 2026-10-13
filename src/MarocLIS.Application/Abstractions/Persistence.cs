using System.Linq.Expressions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.Abstractions;

/// <summary>
/// Read/write access to one aggregate type. Deliberately narrow: query composition that a screen
/// needs lives in a purpose-built query interface, not here, so the repository does not become a
/// second, weaker copy of LINQ.
/// </summary>
public interface IRepository<T> where T : AggregateRoot
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads with the related data a use case needs, e.g. an order with its items.</summary>
    Task<T?> GetByIdWithDetailsAsync(Guid id, CancellationToken ct = default);

    Task<bool> ExistsAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<T>> ListAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);

    Task AddAsync(T entity, CancellationToken ct = default);

    void Update(T entity);

    /// <summary>
    /// Logical delete for aggregates that implement <see cref="ISoftDeletable"/>. There is no
    /// physical delete on clinical or accounting aggregates: retention obligations and the
    /// unbroken invoice series both forbid it.
    /// </summary>
    void SoftDelete(T entity, Guid byUserId, string reason);
}

/// <summary>
/// Transaction boundary. One use case, one <see cref="SaveChangesAsync"/>: the audit interceptor
/// stamps and logs everything in the same transaction, so a change and its audit entry cannot
/// diverge.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Opens an explicit transaction. Needed where a use case must both allocate a gapless number
    /// and write the document that uses it, so that a rollback returns the number.
    /// </summary>
    Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default);

    Task CommitAsync(CancellationToken ct = default);

    Task RollbackAsync(CancellationToken ct = default);

    /// <summary>
    /// Business justification attached to every audit entry written by the current save. Set by
    /// use cases whose operation the regulation requires a reason for — a result revision, a
    /// discount, a QC override.
    /// </summary>
    string? AuditReason { get; set; }
}

/// <summary>
/// Hands out the next value of a gapless sequence.
/// <para>
/// Implemented with a pessimistic row lock rather than a database sequence or IDENTITY, because
/// Moroccan tax law requires an unbroken chronological invoice series and both of those mechanisms
/// burn values on rollback. Callers must already be inside a transaction.
/// </para>
/// </summary>
public interface ISequenceAllocator
{
    Task<Result<string>> NextAsync(SequencePurpose purpose, Guid? siteId, int? year = null,
                                   CancellationToken ct = default);

    /// <summary>Peeks at what the next value would render as, without consuming it.</summary>
    Task<string?> PeekAsync(SequencePurpose purpose, Guid? siteId, int? year = null,
                            CancellationToken ct = default);
}

/// <summary>Hashing and verification of user passwords. Implemented with BCrypt.</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string hash);

    /// <summary>
    /// True when the stored hash used a lower work factor than the current policy, so the password
    /// can be transparently re-hashed at the next successful sign-in.
    /// </summary>
    bool NeedsRehash(string hash);
}

/// <summary>
/// A page of results plus the total, so a grid can show "1–50 of 3 412" without a second query
/// round-trip from the UI.
/// </summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public static PagedResult<T> Empty(int pageSize = 50) => new([], 0, 1, pageSize);

    public int PageCount => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < PageCount;
}
