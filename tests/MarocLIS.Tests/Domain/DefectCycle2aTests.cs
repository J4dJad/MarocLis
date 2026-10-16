using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Instruments;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Defect-hunt cycle 2 (lot a), the analyser interfaces: a conversion figure too large for its
/// column is refused with a message instead of failing the save of the whole table.
/// </summary>
public sealed class DefectCycle2aTests
{
    [Theory]
    [InlineData("10000000000", null)]
    [InlineData(null, "-10000000000")]
    [InlineData("123456789012", "0")]
    public async Task AConversionFigureTheColumnCannotHold_IsRefused(string? factor, string? offset)
    {
        var (service, link, saved) = Interfaces();

        var outcome = await service.SaveMappingsAsync(link.Id,
            [new MappingRequest("GLU", Guid.NewGuid(), "mg/dL", Parse(factor), Parse(offset), null, true)]);

        outcome.FirstError.ResourceKey.Should().Be("Interface_ConversionOutOfRange");
        saved().Should().BeFalse();
        link.Mappings.Should().BeEmpty();
    }

    [Fact]
    public async Task AConversionWithinTheColumn_IsSaved()
    {
        var (service, link, saved) = Interfaces();

        var outcome = await service.SaveMappingsAsync(link.Id,
            [new MappingRequest("GLU", Guid.NewGuid(), "mg/dL", 9_999_999_999.99999999m, -0.0555m, null, true)]);

        outcome.IsSuccess.Should().BeTrue();
        saved().Should().BeTrue();
        link.Mappings.Should().ContainSingle().Which.ConversionFactor.Should().Be(9_999_999_999.99999999m);
    }

    private static decimal? Parse(string? text) =>
        text is null ? null : decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    private static (InstrumentInterfaceService Service, InstrumentInterface Link, Func<bool> Saved) Interfaces()
    {
        var siteId = Guid.NewGuid();
        var instrument = new Instrument("AU480", new MultilingualText("AU480"), EquipmentKind.Analyser, siteId);
        var link = new InstrumentInterface(instrument.Id, siteId, InstrumentProtocol.AstmE1394, InstrumentTransportKind.Tcp);
        var saved = false;

        var service = new InstrumentInterfaceService(
            Stub.Of<IRepository<InstrumentInterface>>(new()
            {
                ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<InstrumentInterface?>(link)
            }),
            Stub.Of<IRepository<Instrument>>(),
            Stub.Of<IInstrumentMessageLogStore>(),
            Stub.Of<IInstrumentResultReplayer>(),
            Stub.Of<IInstrumentMessagePreviewer>(),
            new InstrumentHostOptions(),
            Stub.Of<ISecurityAuditor>(),
            Stub.Of<IInstrumentInterfaceQueries>(),
            Stub.Of<IUnitOfWork>(new() { ["SaveChangesAsync"] = _ => { saved = true; return Task.FromResult(1); } }),
            Stub.Of<IPermissionGuard>(),
            Stub.Of<ICurrentUser>(),
            Stub.Of<IClock>());

        return (service, link, () => saved);
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
