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
public sealed class UserScopes(IServiceScopeFactory scopes, SessionContext context)
{
    public AsyncServiceScope Create()
    {
        var scope = scopes.CreateAsyncScope();
        if (context.Session is { } session)
            scope.ServiceProvider.GetRequiredService<SessionContext>().Attach(session);
        return scope;
    }
}
