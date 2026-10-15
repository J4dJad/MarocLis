using System.Globalization;
using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using MarocLIS.Localization;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// Second review, desktop side: U5 (negative bounds erased in the Arabic UI) and U4 (a prescriber
/// refused by validation left tracked and committed by the next save).
/// </summary>
public sealed class ReviewFixesUi3Tests
{
    // ---- U5 -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("‎-2,5")]      // LRM, as the Arabic culture writes the sign
    [InlineData("‏-2,5")]      // RLM
    [InlineData("؜-2,5")]      // Arabic letter mark
    [InlineData("‪-2,5‬")] // LRE ... PDF
    [InlineData("‫-2,5‬")] // RLE ... PDF
    [InlineData("‭-2,5‬")] // LRO ... PDF
    [InlineData("‮-2,5‬")] // RLO ... PDF
    [InlineData("⁦-2,5⁩")] // LRI ... PDI
    [InlineData("⁧-2,5⁩")] // RLI ... PDI
    [InlineData("⁨-2.5⁩")] // FSI ... PDI
    [InlineData("-‎2,5")]
    public void ParseDecimal_ignores_direction_marks(string text) =>
        EditorValues.ParseDecimal(text).Should().Be(-2.5m);

    [Fact]
    public void ParseDecimal_still_reads_grouped_and_plain_values()
    {
        EditorValues.ParseDecimal("‎-150 000,5").Should().Be(-150000.5m);
        EditorValues.ParseDecimal("3,75").Should().Be(3.75m);
        EditorValues.ParseDecimal("‎").Should().BeNull();
        EditorValues.ParseDecimal("abc").Should().BeNull();
    }

    [Theory]
    [InlineData(AppLanguage.Arabic)]
    [InlineData(AppLanguage.French)]
    [InlineData(AppLanguage.English)]
    public void Negative_bound_survives_formatting_and_reading_back(AppLanguage language)
    {
        var culture = CultureManager.Build(language);

        foreach (var value in new[] { -2.5m, -0.125m, -150000m, 0m, 42.75m })
        {
            var text = EditorValues.FormatForEditing(value, 2, culture);

            text.Any(EditorValues.IsDirectionMark).Should().BeFalse("the box is read back as typed");
            EditorValues.ParseDecimal(text).Should().Be(value, $"'{text}' in {language}");
        }
    }

    [Fact]
    public void Arabic_culture_text_with_its_mark_reads_back()
    {
        // What a box filled with the culture's own formatting holds, and what pasted text carries.
        var culture = CultureManager.Build(AppLanguage.Arabic);
        var raw = (-2.5m).ToString("0.00", culture);

        EditorValues.ParseDecimal(raw).Should().Be(-2.5m);
        EditorValues.ParseDecimal((-1.5m).ToString("0.######", culture)).Should().Be(-1.5m);
    }

    // ---- U4 -------------------------------------------------------------------------------------

    private sealed class Calls
    {
        public readonly List<object?> Added = [];
        public readonly List<object?> Updated = [];
        public int Saves;
    }

    private static (MasterDataService Service, Calls Calls) NewService(Prescriber? existing = null)
    {
        var calls = new Calls();

        var prescribers = Stub.Of<IRepository<Prescriber>>(new()
        {
            ["GetByIdAsync"] = _ => Task.FromResult(existing),
            ["AddAsync"] = args => { calls.Added.Add(args![0]); return Task.CompletedTask; },
            ["Update"] = args => { calls.Updated.Add(args![0]); return null; }
        });

        var unitOfWork = Stub.Of<IUnitOfWork>(new()
        {
            ["SaveChangesAsync"] = _ => { calls.Saves++; return Task.FromResult(1); }
        });

        var service = new MasterDataService(
            Stub.Of<IRepository<Instrument>>(), Stub.Of<IRepository<QcMaterial>>(),
            Stub.Of<IRepository<QcTarget>>(), Stub.Of<IRepository<StockItem>>(),
            Stub.Of<IRepository<Supplier>>(), prescribers, Stub.Of<IRepository<Payer>>(),
            Stub.Of<IMasterDataQueries>(), unitOfWork, Stub.Of<IPermissionGuard>(),
            Stub.Of<ICurrentUser>(), Stub.Of<IClock>());

        return (service, calls);
    }

    private static PrescriberRequest Request(decimal? discount, Guid? id = null) => new()
    {
        PrescriberId = id,
        LastName = "Alaoui",
        FirstName = "Karim",
        Kind = PrescriberKind.GeneralPractitioner,
        DiscountPercent = discount
    };

    [Fact]
    public async Task Refused_new_prescriber_never_reaches_the_context()
    {
        var (service, calls) = NewService();

        var result = await service.SavePrescriberAsync(Request(discount: 150m));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.ResourceKey == "Prescriber_DiscountOutOfRange");
        calls.Added.Should().BeEmpty("a refused prescriber left tracked was committed by the next save");
        calls.Updated.Should().BeEmpty();
        calls.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Valid_new_prescriber_is_added_once_and_saved()
    {
        var (service, calls) = NewService();

        var result = await service.SavePrescriberAsync(Request(discount: 10m));

        result.IsSuccess.Should().BeTrue();
        calls.Added.Should().ContainSingle().Which.Should().BeOfType<Prescriber>()
             .Which.Id.Should().Be(result.Value);
        calls.Saves.Should().Be(1);
    }

    [Fact]
    public async Task Refused_edit_leaves_the_tracked_prescriber_unchanged()
    {
        var existing = new Prescriber("Bennani", "Sara", PrescriberKind.Specialist) { DiscountPercent = 5m };
        var (service, calls) = NewService(existing);

        var result = await service.SavePrescriberAsync(Request(discount: 150m, id: existing.Id));

        result.IsFailure.Should().BeTrue();
        existing.LastName.Should().Be("Bennani");
        existing.DiscountPercent.Should().Be(5m);
        existing.Kind.Should().Be(PrescriberKind.Specialist);
        calls.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Accepted_edit_applies_the_request_to_the_tracked_prescriber()
    {
        var existing = new Prescriber("Bennani", "Sara", PrescriberKind.Specialist);
        var (service, calls) = NewService(existing);

        var result = await service.SavePrescriberAsync(Request(discount: 20m, id: existing.Id));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(existing.Id);
        existing.LastName.Should().Be("Alaoui");
        existing.DiscountPercent.Should().Be(20m);
        calls.Added.Should().BeEmpty();
        calls.Updated.Should().ContainSingle().Which.Should().BeSameAs(existing);
        calls.Saves.Should().Be(1);
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
