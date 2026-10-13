using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace MarocLIS.Desktop.Composition;

/// <summary>
/// Runs every read-side query call on its own short-lived <see cref="LisDbContext"/>.
/// <para>
/// A screen's handlers are <c>async void</c>: a grid's SelectionChanged fires while the screen's
/// own load is still awaiting, a search debounce ticks while a modal editor saves. Sharing the
/// screen's single DbContext between them throws "A second operation was started on this context
/// instance". Queries are read-only and untracked, so giving each call its own scope costs a
/// connection from the pool and removes the overlap entirely.
/// </para>
/// <para>
/// The one exception is a call made while the screen's context holds an open transaction: a
/// service reading inside its own transaction must see its uncommitted rows, and a second
/// connection would block on the very locks that transaction holds. Those calls stay on the
/// shared context, as do members that do not return a Task.
/// </para>
/// </summary>
public class IsolatedQueryProxy<TQueries> : DispatchProxy where TQueries : class
{
    private static readonly ConcurrentDictionary<Type, MethodInfo> Awaiters = new();

    private IServiceProvider _scoped = null!;
    private IServiceScopeFactory _scopes = null!;
    private Func<IServiceProvider, TQueries> _factory = null!;
    private TQueries? _sharedInstance;

    public static TQueries Create(IServiceProvider scoped, Func<IServiceProvider, TQueries> factory)
    {
        var proxy = Create<TQueries, IsolatedQueryProxy<TQueries>>();
        var self = (IsolatedQueryProxy<TQueries>)(object)proxy;

        self._scoped = scoped;
        self._scopes = scoped.GetRequiredService<IServiceScopeFactory>();
        self._factory = factory;

        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        var returnType = targetMethod.ReturnType;
        var isAsync = returnType == typeof(Task)
                      || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>));

        LisDbContext shared;

        try
        {
            shared = _scoped.GetRequiredService<LisDbContext>();
        }
        catch (ObjectDisposedException)
        {
            // The screen this proxy belongs to was closed while one of its loads was still running
            // (the operator moved to another screen). Reported as a cancellation, which every
            // screen already ignores, rather than as an "unexpected error" about a screen that is
            // no longer there.
            throw new OperationCanceledException();
        }

        if (!isAsync || shared.Database.CurrentTransaction is not null || ReturnsEntity(returnType))
        {
            return Call(targetMethod, _sharedInstance ??= _factory(_scoped), args);
        }

        var scope = _scopes.CreateScope();

        object? result;

        try
        {
            result = Call(targetMethod, _factory(scope.ServiceProvider), args);
        }
        catch
        {
            scope.Dispose();
            throw;
        }

        if (returnType == typeof(Task)) return DisposeAfter((Task)result!, scope);

        var awaiter = Awaiters.GetOrAdd(returnType.GetGenericArguments()[0], t =>
            typeof(IsolatedQueryProxy<TQueries>)
                .GetMethod(nameof(DisposeAfterTyped), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(t));

        return awaiter.Invoke(null, [result, scope]);
    }

    // A query that hands back a domain entity loads it for a service to change and save. Loaded on
    // a context of its own, the entity was saved by nobody: releasing a reagent lot from
    // quarantine reported success and the lot stayed in quarantine. Such calls use the screen's
    // context, the one the service saves.
    private static bool ReturnsEntity(Type returnType)
    {
        if (!returnType.IsGenericType) return false;

        var result = returnType.GetGenericArguments()[0];
        if (result.IsGenericType && result.GetGenericArguments() is [var element]) result = element;

        return result.Namespace?.StartsWith("MarocLIS.Domain", StringComparison.Ordinal) == true
               && !result.IsEnum && !result.IsValueType;
    }

    private static object? Call(MethodInfo method, TQueries target, object?[]? args)
    {
        try
        {
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
    }

    private static async Task DisposeAfter(Task task, IServiceScope scope)
    {
        try { await task; }
        finally { scope.Dispose(); }
    }

    private static async Task<T> DisposeAfterTyped<T>(Task<T> task, IServiceScope scope)
    {
        try { return await task; }
        finally { scope.Dispose(); }
    }
}

public static class IsolatedQueryRegistration
{
    /// <summary>Registers <typeparamref name="TImplementation"/> behind an <see cref="IsolatedQueryProxy{TQueries}"/>.</summary>
    public static IServiceCollection AddIsolatedQueries<TQueries, TImplementation>(this IServiceCollection services)
        where TQueries : class
        where TImplementation : class, TQueries
    {
        services.AddScoped<TImplementation>();
        services.AddScoped<TQueries>(sp => IsolatedQueryProxy<TQueries>.Create(
            sp, provider => provider.GetRequiredService<TImplementation>()));

        return services;
    }
}
