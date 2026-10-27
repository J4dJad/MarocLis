using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Results;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using System.Linq.Expressions;
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

    // ---- A user manager cannot open sites they do not work on ------------------------------------

    [Fact]
    public async Task AUserManager_CannotChangeTheirOwnSites_NorOpenASiteTheyDoNotWorkOn()
    {
        var siteA = Guid.NewGuid();
        var siteB = Guid.NewGuid();
        var manager = new AppUser("secb", "Secrétaire B", "hash");
        var managerId = manager.Id;
        manager.GrantSiteAccess(siteB, true);
        var colleague = new AppUser("techb", "Technicien B", "hash");
        colleague.GrantSiteAccess(siteB, true);
        var travelling = new AppUser("bio", "Biologiste", "hash");
        travelling.GrantSiteAccess(siteA, true);

        var accounts = new Dictionary<Guid, AppUser> { [managerId] = manager, [colleague.Id] = colleague, [travelling.Id] = travelling };
        var saves = 0;
        var service = new UserAdministrationService(
            Stub.Of<IRepository<AppUser>>(new()
            {
                ["GetByIdWithDetailsAsync"] = a => Task.FromResult(accounts.GetValueOrDefault((Guid)a![0]!)),
                ["AddAsync"] = a => { var u = (AppUser)a![0]!; accounts[u.Id] = u; return Task.CompletedTask; }
            }),
            Stub.Of<IRepository<Role>>(),
            Stub.Of<IAdministrationQueries>(new()
            {
                ["UserNameExistsAsync"] = _ => Task.FromResult(false),
                ["AnyPartnerSiteAsync"] = _ => Task.FromResult(false)
            }),
            Stub.Of<IAuthenticationService>(),
            Stub.Of<IUnitOfWork>(new() { ["SaveChangesAsync"] = _ => { saves++; return Task.FromResult(1); } }),
            Stub.Of<IPermissionGuard>(),
            Stub.Of<ICurrentUser>(new()
            {
                ["get_UserId"] = _ => managerId,
                ["get_SiteId"] = _ => siteB,
                ["get_AccessibleSiteIds"] = _ => (IReadOnlyList<Guid>)[siteB]
            }),
            Stub.Of<ISecurityAuditor>(), Stub.Of<IPasswordHasher>(), new PasswordPolicy(), Stub.Of<IClock>());

        // Her own row: refused, even for a site she has.
        (await service.SetSiteAccessAsync(managerId, [siteA, siteB], siteA))
            .FirstError.ResourceKey.Should().Be("User_CannotEditOwnSites");
        manager.SiteAccess.Select(a => a.SiteId).Should().Equal(siteB);

        // A colleague: site A is not hers to open.
        (await service.SetSiteAccessAsync(colleague.Id, [siteA, siteB], siteB))
            .FirstError.ResourceKey.Should().Be("User_SiteNotYoursToGrant");
        colleague.SiteAccess.Select(a => a.SiteId).Should().Equal(siteB);

        // An account that already works on site A keeps it when she adds her own site B.
        (await service.SetSiteAccessAsync(travelling.Id, [siteA, siteB], siteA)).IsSuccess.Should().BeTrue();
        travelling.SiteAccess.Select(a => a.SiteId).Should().BeEquivalentTo([siteA, siteB]);

        // Nor can she create an account on site A.
        (await service.CreateUserAsync(new CreateUserRequest
        {
            UserName = "seca", FullName = "Compte A", InitialPassword = "Motdepasse#2026", SiteIds = [siteA]
        })).FirstError.ResourceKey.Should().Be("User_SiteNotYoursToGrant");
        saves.Should().Be(1);
    }

    // ---- Web and Desktop validate the value that was on screen ----------------------------------

    [Fact]
    public void AValidation_FromTheScreens_CarriesTheEntryTimeOfTheValueShown()
    {
        var shownAt = new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.FromHours(1));
        var row = new ResultEntryRow
        {
            TestResultId = Guid.NewGuid(), LabOrderItemId = Guid.NewGuid(), TestParameterId = Guid.NewGuid(),
            AnalysisCode = "IONO", AnalysisName = new MultilingualText("Ionogramme"),
            ParameterCode = "K", ParameterName = new MultilingualText("Potassium"),
            Status = MarocLIS.Domain.Results.TestResultStatus.TechnicallyValidated,
            EnteredAt = shownAt
        };

        var decision = ValidationScreenRules.Decisions([row], _ => true, _ => null).Single();

        decision.DisplayedEnteredAt.Should().Be(shownAt,
            "a value re-run while the screen stood open is refused instead of released unseen");
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
