using MarocLIS.Web.Security;

namespace MarocLIS.Web.Session;

/// <summary>
/// Short-lived scopes working for this browser's operator.
/// <para>
/// A circuit lives as long as its tab, and its own scope's database context with it; a timer of the
/// layout using that context while a page loads would collide with it, as the workstation's screens
/// did before they ran each query on a context of its own (<c>IsolatedQueries</c>). Background work
/// of a circuit — the heartbeat, the session check — runs in a scope of its own instead, into which
/// the session is replayed first: a new scope's accessor is empty, and an empty operator is no one.
/// </para>
/// </summary>
public sealed class UserScopes(IServiceScopeFactory scopes, SessionContext context, WebSessionStore store)
{
    /// <summary>
    /// A scope for the tab's operator, as long as the session is still open: signed out in another
    /// tab, it has left the store at once, while this tab's monitor only notices at its next tick.
    /// Until then a click here ran — and was audited — under the operator who had left; now the
    /// scope has no operator, so every use case refuses it, and the monitor sends the tab away.
    /// </summary>
    public AsyncServiceScope Create()
    {
        var scope = scopes.CreateAsyncScope();
        if (context.Session is { } session && store.Get(session.Id) is { } current)
            scope.ServiceProvider.GetRequiredService<SessionContext>().Attach(current);
        return scope;
    }

    /// <summary>
    /// Runs one operation of a screen — a query, a use case — on a scope of its own, as each query of
    /// a workstation screen runs on a context of its own: two operations of one page never share a
    /// database context, and none sees entities another left tracked.
    /// </summary>
    public async Task<T> RunAsync<TService, T>(Func<TService, Task<T>> work) where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var scope = Create();
        return await work(scope.ServiceProvider.GetRequiredService<TService>());
    }

    /// <summary>
    /// <see cref="RunAsync{TService,T}"/> for an operation that reads through two services, as the
    /// workstation reads a list and its context beside it in one screen load.
    /// </summary>
    public async Task<T> RunAsync<TFirst, TSecond, T>(Func<TFirst, TSecond, Task<T>> work)
        where TFirst : notnull where TSecond : notnull
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var scope = Create();
        return await work(scope.ServiceProvider.GetRequiredService<TFirst>(),
                          scope.ServiceProvider.GetRequiredService<TSecond>());
    }

    /// <summary><see cref="RunAsync{TService,T}"/> for an operation that answers nothing.</summary>
    public async Task RunAsync<TService>(Func<TService, Task> work) where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var scope = Create();
        await work(scope.ServiceProvider.GetRequiredService<TService>());
    }
}
