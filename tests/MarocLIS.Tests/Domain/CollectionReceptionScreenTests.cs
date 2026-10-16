using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Domain.Sequencing;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// The storage places offered at reception (« Lieu de conservation »): read for the operator's own
/// site, and only with the right to see samples.
/// </summary>
public sealed class CollectionReceptionScreenTests
{
    private static readonly Guid Site = Guid.NewGuid();

    private static SampleWorkflowService Service(ISampleQueries queries, bool allowed) =>
        new(
            Stub.Of<IRepository<Sample>>(),
            Stub.Of<IRepository<LabOrder>>(),
            Stub.Of<IRepository<AnalysisTest>>(),
            Stub.Of<IRepository<Patient>>(),
            Stub.Of<IRepository<TestResult>>(),
            queries,
            Stub.Of<ISequenceAllocator>(),
            Stub.Of<IUnitOfWork>(),
            Stub.Of<IPermissionGuard>(new()
            {
                ["Require"] = args => allowed && (string)args![0]! == Permissions.Samples.View
                    ? Result.Success()
                    : Result.Failure(Error.Forbidden("Forbidden"))
            }),
            Stub.Of<ICurrentUser>(new()
            {
                ["get_UserId"] = _ => Guid.NewGuid(),
                ["get_SiteId"] = _ => Site,
                ["get_Language"] = _ => AppLanguage.French,
                ["Has"] = _ => allowed
            }),
            Stub.Of<IDataAccessAuditor>(),
            Stub.Of<IClock>(new() { ["get_Now"] = _ => DateTimeOffset.Now }),
            new OrderRegistrationOptions());

    [Fact]
    public async Task StorageLocations_AreReadForTheOperatorsSite()
    {
        Guid? askedFor = null;
        var queries = Stub.Of<ISampleQueries>(new()
        {
            ["GetStorageLocationsAsync"] = args =>
            {
                askedFor = (Guid)args![0]!;
                return Task.FromResult<IReadOnlyList<string>>(["Paillasse hémostase", "Réfrigérateur R1"]);
            }
        });

        var places = await Service(queries, allowed: true).GetStorageLocationsAsync();

        places.IsSuccess.Should().BeTrue();
        places.Value.Should().Equal("Paillasse hémostase", "Réfrigérateur R1");
        askedFor.Should().Be(Site);
    }

    [Fact]
    public async Task StorageLocations_AreRefusedWithoutTheRightToSeeSamples()
    {
        var asked = false;
        var queries = Stub.Of<ISampleQueries>(new()
        {
            ["GetStorageLocationsAsync"] = _ =>
            {
                asked = true;
                return Task.FromResult<IReadOnlyList<string>>([]);
            }
        });

        var places = await Service(queries, allowed: false).GetStorageLocationsAsync();

        places.IsFailure.Should().BeTrue();
        asked.Should().BeFalse();
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
            return Neutral(targetMethod.ReturnType);
        }

        private static object? Neutral(Type type)
        {
            if (type == typeof(void)) return null;
            if (type == typeof(Result)) return Result.Success();
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                var value = Neutral(inner);
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                                   .MakeGenericMethod(inner).Invoke(null, [value]);
            }
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
