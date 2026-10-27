using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// The medium and low points left by the total verification (round 87): each test is what the lab
/// user saw before, and must no longer see.
/// </summary>
public sealed class Fix87mTests
{
    private static readonly JsonLocalizer Localizer = new(new LocalizationOptions { MarkMissingKeys = true });

    private static string Fr(string key, params object?[] args) => Localizer.Format(AppLanguage.French, key, args);

    private static string En(string key, params object?[] args) => Localizer.Format(AppLanguage.English, key, args);

    private static string Ar(string key, params object?[] args) => Localizer.Format(AppLanguage.Arabic, key, args);

    [Fact]
    public void TheCashSessionHeader_CountsItsPayments_InTheSingularForOne()
    {
        Fr("Payment.SessionPaymentCount", 1).Should().Be("1 règlement");
        Fr("Payment.SessionPaymentCount", 0).Should().Be("0 règlement");
        Fr("Payment.SessionPaymentCount", 3).Should().Be("3 règlements");
        En("Payment.SessionPaymentCount", 1).Should().Be("1 payment");
        En("Payment.SessionPaymentCount", 2).Should().Be("2 payments");
        Ar("Payment.SessionPaymentCount", 2).Should().Contain("2").And.NotContain("Payment.");
    }

    [Fact]
    public void ThePayslip_WritesNoFamilyDeduction_AsZero_NotMinusZero()
    {
        QuestPdfPayslipRenderer.Deduction(0m).Should().Be("0,00");
        QuestPdfPayslipRenderer.Deduction(-0.00m).Should().Be("0,00");
        QuestPdfPayslipRenderer.Deduction(0.001m).Should().Be("0,00");
        QuestPdfPayslipRenderer.Deduction(500m).Should().Be("−500,00");
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
