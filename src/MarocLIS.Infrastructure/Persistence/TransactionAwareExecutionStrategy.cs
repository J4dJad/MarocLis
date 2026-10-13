using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MarocLIS.Infrastructure.Persistence;

/// <summary>
/// SQL Server's retrying strategy, except inside a transaction the application opened itself.
/// <para>
/// The stock retrying strategy refuses to run at all once a user transaction is open — EF throws
/// "does not support user-initiated transactions" on the first save — because replaying one save
/// in the middle of a transaction would not replay the statements before it. Every use case that
/// takes a gapless number (an accession number, an invoice, a non-conformity) opens such a
/// transaction, so with the stock strategy none of them could save on a real server.
/// </para>
/// <para>
/// Here a save outside a transaction keeps its retries, and a save inside one runs once: a
/// transient failure surfaces, the transaction is rolled back when its scope is disposed, and the
/// operator repeats the action — which is the only correct retry for a multi-statement unit.
/// </para>
/// </summary>
public sealed class TransactionAwareExecutionStrategy(ExecutionStrategyDependencies dependencies,
                                                      int maxRetryCount)
    : SqlServerRetryingExecutionStrategy(dependencies, maxRetryCount, TimeSpan.FromSeconds(5), null)
{
    private bool InUserTransaction => Dependencies.CurrentContext.Context.Database.CurrentTransaction is not null;

    public override TResult Execute<TState, TResult>(
        TState state, Func<DbContext, TState, TResult> operation,
        Func<DbContext, TState, ExecutionResult<TResult>>? verifySucceeded) =>
        InUserTransaction
            ? operation(Dependencies.CurrentContext.Context, state)
            : base.Execute(state, operation, verifySucceeded);

    public override Task<TResult> ExecuteAsync<TState, TResult>(
        TState state, Func<DbContext, TState, CancellationToken, Task<TResult>> operation,
        Func<DbContext, TState, CancellationToken, Task<ExecutionResult<TResult>>>? verifySucceeded,
        CancellationToken cancellationToken = default) =>
        InUserTransaction
            ? operation(Dependencies.CurrentContext.Context, state, cancellationToken)
            : base.ExecuteAsync(state, operation, verifySucceeded, cancellationToken);
}
