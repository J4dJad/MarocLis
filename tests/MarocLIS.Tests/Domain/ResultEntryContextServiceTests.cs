using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// The entry screen's context: guarded like the worklist (enter) and the opened file (view), read
/// for the user's own site, and not asked of the database for an empty list.
/// </summary>
public sealed class ResultEntryContextServiceTests
{
    private static readonly Guid Site = Guid.NewGuid();

    private sealed class Calls
    {
        public List<string> Made { get; } = [];
        public Guid? SiteAsked { get; set; }
    }

    private static (ResultEntryContextService Service, Calls Calls) Build(params string[] granted)
    {
        var calls = new Calls();

        var queries = Stub.Of<IResultEntryContextQueries>(new()
        {
            ["GetProgressAsync"] = args =>
            {
                calls.Made.Add("progress");
                calls.SiteAsked = (Guid)args![0]!;
                return Task.FromResult<IReadOnlyList<ResultEntryProgress>>([new ResultEntryProgress("A1", 1, 3)]);
            },
            ["GetSectorsAsync"] = _ =>
            {
                calls.Made.Add("sectors");
                return Task.FromResult<IReadOnlyList<ResultEntrySector>>([new ResultEntrySector("HEM", new MultilingualText("Hématologie"))]);
            },
            ["GetOriginsAsync"] = _ =>
            {
                calls.Made.Add("origins");
                return Task.FromResult<IReadOnlyList<ResultEntryOrigin>>([]);
            }
        });

        var guard = Stub.Of<IPermissionGuard>(new()
        {
            ["Require"] = args => granted.Contains((string)args![0]!)
                ? Result.Success()
                : Result.Failure(Error.Forbidden("Auth_Forbidden"))
        });

        var user = Stub.Of<ICurrentUser>(new() { ["get_SiteId"] = _ => Site });

        return (new ResultEntryContextService(queries, guard, user), calls);
    }

    [Fact]
    public async Task Progress_IsReadForTheUsersSite_BySomeoneWhoEntersResults()
    {
        var (service, calls) = Build(Permissions.Results.Enter);

        var progress = await service.GetProgressAsync(["A1"]);

        progress.IsSuccess.Should().BeTrue();
        progress.Value.Should().ContainSingle().Which.Total.Should().Be(3);
        calls.SiteAsked.Should().Be(Site);
    }

    [Fact]
    public async Task Progress_AndSectors_AreRefused_WithoutTheRightToEnter()
    {
        var (service, calls) = Build(Permissions.Results.View);

        (await service.GetProgressAsync(["A1"])).IsFailure.Should().BeTrue();
        (await service.GetSectorsAsync(["HEM"])).IsFailure.Should().BeTrue();
        calls.Made.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyLists_DoNotQueryTheDatabase()
    {
        var (service, calls) = Build(Permissions.Results.Enter);

        (await service.GetProgressAsync([])).Value.Should().BeEmpty();
        (await service.GetSectorsAsync([])).Value.Should().BeEmpty();
        calls.Made.Should().BeEmpty();
    }

    [Fact]
    public async Task Origins_FollowTheRightToSeeResults()
    {
        var (allowed, allowedCalls) = Build(Permissions.Results.View);
        var (refused, refusedCalls) = Build();

        (await allowed.GetOriginsAsync(Guid.NewGuid())).IsSuccess.Should().BeTrue();
        (await refused.GetOriginsAsync(Guid.NewGuid())).IsFailure.Should().BeTrue();

        allowedCalls.Made.Should().Equal("origins");
        refusedCalls.Made.Should().BeEmpty();
    }

    private class Stub : DispatchProxy
    {
        private Dictionary<string, Func<object?[]?, object?>> _handlers = [];

        public static T Of<T>(Dictionary<string, Func<object?[]?, object?>>? handlers = null) where T : class
        {
            var proxy = Create<T, Stub>();
            ((Stub)(object)proxy)._handlers = handlers ?? [];
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (_handlers.TryGetValue(targetMethod.Name, out var handler)) return handler(args);
            return targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }
}
