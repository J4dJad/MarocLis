using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Samples;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>The antériorité chart's service call: the right to read results, and the order's patient.</summary>
public sealed class AnterioriteServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));

    private static ResultEntryService Service(LabOrder? order, IResultQueries queries, IPermissionGuard guard,
                                              IDataAccessAuditor? auditor = null) =>
        new(Stub.Of<IRepository<LabOrder>>(new()
            {
                ["GetByIdAsync"] = args => Task.FromResult(order is not null && (Guid)args![0]! == order.Id ? order : null)
            }),
            Stub.Of<IRepository<AnalysisTest>>(),
            Stub.Of<IRepository<Patient>>(),
            Stub.Of<IRepository<Sample>>(),
            queries,
            Stub.Of<IUnitOfWork>(),
            guard,
            Stub.Of<ICurrentUser>(),
            auditor ?? Stub.Of<IDataAccessAuditor>(),
            Stub.Of<IClock>());

    [Fact]
    public async Task History_IsReadForTheOrdersPatient_CappedAndAudited()
    {
        var order = new LabOrder("2610-000001", Guid.NewGuid(), Guid.NewGuid(), Now, Guid.NewGuid());
        var parameterId = Guid.NewGuid();
        var point = new ResultHistoryPoint(Guid.NewGuid(), Now.AddDays(-30), 1.2m, null, "2609-000042");

        (Guid Patient, Guid Parameter, int Limit)? asked = null;
        var queries = Stub.Of<IResultQueries>(new()
        {
            ["GetValidatedHistoryAsync"] = args =>
            {
                asked = ((Guid)args![0]!, (Guid)args[1]!, (int)args[2]!);
                return Task.FromResult<IReadOnlyList<ResultHistoryPoint>>([point]);
            }
        });

        DataAccessKind? logged = null;
        var auditor = Stub.Of<IDataAccessAuditor>(new()
        {
            ["LogAsync"] = args => { logged = (DataAccessKind)args![0]!; return Task.CompletedTask; }
        });

        var history = await Service(order, queries, Stub.Of<IPermissionGuard>(), auditor)
            .GetHistoryAsync(order.Id, parameterId);

        history.IsSuccess.Should().BeTrue();
        history.Value.Should().ContainSingle().Which.Should().Be(point);
        asked.Should().Be((order.PatientId, parameterId, ResultEntryService.ResultHistoryLimit));
        logged.Should().Be(DataAccessKind.ViewOrder);
    }

    [Fact]
    public async Task History_WithoutTheRightToReadResults_IsRefused_AndNothingIsRead()
    {
        var order = new LabOrder("2610-000002", Guid.NewGuid(), Guid.NewGuid(), Now, Guid.NewGuid());
        var read = false;
        var queries = Stub.Of<IResultQueries>(new()
        {
            ["GetValidatedHistoryAsync"] = _ =>
            {
                read = true;
                return Task.FromResult<IReadOnlyList<ResultHistoryPoint>>([]);
            }
        });
        var guard = Stub.Of<IPermissionGuard>(new()
        {
            ["Require"] = _ => Result.Failure(Error.Forbidden("Permission_Denied"))
        });

        var history = await Service(order, queries, guard).GetHistoryAsync(order.Id, Guid.NewGuid());

        history.IsFailure.Should().BeTrue();
        history.FirstError.Code.Should().Be("forbidden");
        read.Should().BeFalse();
    }

    [Fact]
    public async Task History_OfAnUnknownOrder_IsNotFound()
    {
        var history = await Service(null, Stub.Of<IResultQueries>(), Stub.Of<IPermissionGuard>())
            .GetHistoryAsync(Guid.NewGuid(), Guid.NewGuid());

        history.IsFailure.Should().BeTrue();
        history.FirstError.ResourceKey.Should().Be("Order_NotFound");
    }

    /// <summary>
    /// Interface stub: named members answer from the handler table, everything else returns a
    /// neutral value — success for a <see cref="Result"/>, a completed task, or the default.
    /// </summary>
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
