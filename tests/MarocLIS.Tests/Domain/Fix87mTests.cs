using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Results;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Samples;
using MarocLIS.Application.Reports;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Domain.Inventory;
using MarocLIS.Application.Inventory;
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

    // ---- A value already released keeps its analysis and its tube -------------------------------

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 10, 0, 0, TimeSpan.FromHours(1));

    /// <summary>A blood count on one tube, its haemoglobin released by a biologist, the rest awaiting.</summary>
    private static (LabOrder Order, Sample Tube, LabOrderItem Count, List<TestResult> Values) PartlyReleasedCount(bool release = true)
    {
        var siteId = Guid.NewGuid();
        var order = new LabOrder("2610-000087", Guid.NewGuid(), siteId, Now, Guid.NewGuid());
        var tube = new Sample("T-0087", order.Id, Guid.NewGuid(), siteId);
        tube.Collect(Now, Guid.NewGuid(), null, null);
        tube.Receive(Now, Guid.NewGuid(), SampleDefect.None, false, null, null).IsSuccess.Should().BeTrue();

        var count = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine) { SampleId = tube.Id };
        order.AddItem(count).IsSuccess.Should().BeTrue();
        count.MarkCollected(Now);
        count.MarkReceived(Now, null);

        var hb = new TestParameter("HB", new MultilingualText("HB"), ResultValueType.Numeric);
        var wbc = new TestParameter("GB", new MultilingualText("GB"), ResultValueType.Numeric);
        var hbValue = new TestResult(order.Id, count.Id, hb.Id, siteId);
        var wbcValue = new TestResult(order.Id, count.Id, wbc.Id, siteId);
        hbValue.EnterNumeric(hb, 13.5m, Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        wbcValue.EnterNumeric(wbc, 7.5m, Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        hbValue.TechnicallyValidate(Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        if (release) hbValue.BiologicallyValidate(Now, Guid.NewGuid(), false, true).IsSuccess.Should().BeTrue();

        return (order, tube, count, [hbValue, wbcValue]);
    }

    private static IRepository<TestResult> Results(List<TestResult> values) => Stub.Of<IRepository<TestResult>>(new()
    {
        ["ListAsync"] = a => Task.FromResult<IReadOnlyList<TestResult>>(
            [.. values.Where(((Expression<Func<TestResult, bool>>)a![0]!).Compile())])
    });

    [Fact]
    public async Task AnAnalysisOrAFile_WithAReleasedValue_CannotBeCancelled()
    {
        var (order, _, count, values) = PartlyReleasedCount();
        var service = new OrderAmendmentService(
            Stub.Of<IRepository<LabOrder>>(new() { ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<LabOrder?>(order) }),
            Stub.Of<IRepository<Patient>>(), Stub.Of<IRepository<Sample>>(), null!, Stub.Of<IOrderQueries>(),
            Stub.Of<IUnitOfWork>(), Stub.Of<IPermissionGuard>(), Stub.Of<ICurrentUser>(), Stub.Of<IDataAccessAuditor>(),
            Stub.Of<IClock>(new() { ["get_Now"] = _ => Now }), Results(values));

        count.Status.Should().NotBe(LabOrderItemStatus.Validated, "only one of its values is released");

        (await service.CancelItemAsync(order.Id, count.Id, "Erreur de saisie"))
            .FirstError.ResourceKey.Should().Be("Order_ItemHasReleasedValues");
        (await service.CancelOrderAsync(order.Id, "Erreur de saisie"))
            .FirstError.ResourceKey.Should().Be("Order_HasValidatedItemsCannotCancel");
        count.IsCancelled.Should().BeFalse();
    }

    [Fact]
    public async Task ATube_WithAReleasedValue_CannotBeRejected()
    {
        var (order, tube, count, values) = PartlyReleasedCount();
        var service = new SampleWorkflowService(
            Stub.Of<IRepository<Sample>>(new() { ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<Sample?>(tube) }),
            Stub.Of<IRepository<LabOrder>>(new() { ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<LabOrder?>(order) }),
            Stub.Of<IRepository<AnalysisTest>>(), Stub.Of<IRepository<Patient>>(), Results(values),
            Stub.Of<ISampleQueries>(), Stub.Of<ISequenceAllocator>(), Stub.Of<IUnitOfWork>(), Stub.Of<IPermissionGuard>(),
            Stub.Of<ICurrentUser>(), Stub.Of<IDataAccessAuditor>(), Stub.Of<IClock>(new() { ["get_Now"] = _ => Now }),
            new OrderRegistrationOptions());

        var outcome = await service.RejectAsync([new RejectSampleRequest(tube.Id, SampleRejectionReason.Haemolysed, null, true)]);

        var problems = outcome.IsFailure ? outcome.Errors : outcome.Value.Problems;
        problems.Select(e => e.ResourceKey).Should().Contain("Sample_RejectHasReleasedValues");
        tube.Status.Should().NotBe(SampleStatus.Rejected);
        count.IsCancelled.Should().BeFalse();
    }

    [Fact]
    public async Task AReplacementTube_AndItsRows_BelongToTheOrdersSite_NotTheOperators()
    {
        var (order, tube, count, _) = PartlyReleasedCount(release: false);
        var analysis = new AnalysisTest("NFS", new MultilingualText("NFS"), Guid.NewGuid(), Guid.NewGuid());
        analysis.AddParameter(new TestParameter("HB", new MultilingualText("HB"), ResultValueType.Numeric));
        typeof(LabOrderItem).GetProperty(nameof(LabOrderItem.AnalysisTestId))!.SetValue(count, analysis.Id);

        var centralLab = Guid.NewGuid();
        var added = new List<object>();
        var service = new SampleWorkflowService(
            Stub.Of<IRepository<Sample>>(new()
            {
                ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<Sample?>(tube),
                ["AddAsync"] = a => { added.Add(a![0]!); return Task.CompletedTask; }
            }),
            Stub.Of<IRepository<LabOrder>>(new() { ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<LabOrder?>(order) }),
            Stub.Of<IRepository<AnalysisTest>>(new() { ["GetByIdWithDetailsAsync"] = _ => Task.FromResult<AnalysisTest?>(analysis) }),
            Stub.Of<IRepository<Patient>>(),
            Stub.Of<IRepository<TestResult>>(new()
            {
                ["ListAsync"] = _ => Task.FromResult<IReadOnlyList<TestResult>>([]),
                ["AddAsync"] = a => { added.Add(a![0]!); return Task.CompletedTask; }
            }),
            Stub.Of<ISampleQueries>(),
            Stub.Of<ISequenceAllocator>(new() { ["NextAsync"] = _ => Task.FromResult(Result.Success("T-0088")) }),
            Stub.Of<IUnitOfWork>(), Stub.Of<IPermissionGuard>(),
            Stub.Of<ICurrentUser>(new() { ["get_SiteId"] = _ => centralLab }),
            Stub.Of<IDataAccessAuditor>(), Stub.Of<IClock>(new() { ["get_Now"] = _ => Now }),
            new OrderRegistrationOptions());

        var outcome = await service.RejectAsync([new RejectSampleRequest(tube.Id, SampleRejectionReason.Haemolysed, null, true)]);

        outcome.Value.ReplacementLabels.Should().ContainSingle();
        added.OfType<Sample>().Single().SiteId.Should().Be(order.SiteId, "the collection centre's worklist shows it");
        added.OfType<TestResult>().Should().NotBeEmpty().And.OnlyContain(r => r.SiteId == order.SiteId);
    }

    // ---- The texts the system writes go out in the report's language -----------------------------

    [Fact]
    public void TheAutomaticComments_AndTheReportMessages_AreWrittenInTheReportsLanguage()
    {
        var notCalculable = ResultPostEntryPipeline.NotCalculableComment;
        ResultPostEntryPipeline.PrintedComment(notCalculable, AppLanguage.French).Should().Be(notCalculable);
        ResultPostEntryPipeline.PrintedComment(notCalculable, AppLanguage.English).Should().StartWith("Not calculable");
        ResultPostEntryPipeline.PrintedComment(notCalculable, AppLanguage.Arabic).Should().Contain("غير قابل للحساب");
        ResultPostEntryPipeline.PrintedComment("Non fait", AppLanguage.English).Should().Be("Not done");
        ResultPostEntryPipeline.PrintedComment("Tube coagulé", AppLanguage.English).Should().Be("Tube coagulé",
            "what a person typed is printed as typed");

        ReportReleaseService.SmsText(AppLanguage.Arabic, "2610-000015", "مختبر", null)
            .Should().Contain("2610-000015").And.NotContain("compte rendu");
        ReportReleaseService.SmsText(AppLanguage.English, "2610-000015", "Lab", "0522")
            .Should().Be("Lab: your laboratory report (file 2610-000015) is ready. Tel. 0522");
        ReportReleaseService.SmsText(AppLanguage.French, "2610-000015", "Labo", null)
            .Should().Be("Labo : votre compte rendu d'analyses (dossier 2610-000015) est disponible.");
        ReportReleaseService.EmailSubject(AppLanguage.English, "2610-000015", "Lab").Should().StartWith("Laboratory report");
        ReportReleaseService.EmailBody(AppLanguage.Arabic, null, "2610-000015", 2, "مختبر", "0522")
            .Should().Contain("2610-000015").And.NotContain("Veuillez");
    }

    [Fact]
    public void Arabic_NamesTheEscapeKeyAsOnTheKeyboard_AndTheFormulaErrorHasItsText()
    {
        Ar("OrderEntry.EscapeKey").Should().Be("Esc");
        Fr("Formula_NotFinite").Should().NotContain("Formula_");
        En("Formula_NotFinite").Should().NotContain("Formula_");
        Ar("Formula_NotFinite").Should().NotContain("Formula_");
        var web = File.ReadAllText(Path.Combine(MarocLIS.Tests.Web.WebMenuTests.RepositoryRoot(), "src", "MarocLIS.Web",
                                                "Components", "Screens", "Quality", "QualityControl.razor"));
        web.Should().NotContain("<th class=\"num\">Z</th>").And.Contain("Texts[\"Quality.ZScore\"]");
    }

    // ---- An order drafted at the reorder point is nobody's own request ---------------------------

    [Fact]
    public void AnAutomaticReorderDraft_CanBeApprovedByWhoeverTriggeredIt_AManualOneStillNot()
    {
        var director = Guid.NewGuid();
        PurchaseOrder Draft(bool automatic)
        {
            var order = new PurchaseOrder("BC/2026/00087", Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 27), director)
            {
                RaisedAutomatically = automatic
            };
            order.AddLine(new PurchaseOrderLine(Guid.NewGuid(), 10m, 12m)).IsSuccess.Should().BeTrue();
            return order;
        }

        Draft(automatic: true).Approve(Now, director).IsSuccess.Should().BeTrue();
        Draft(automatic: false).Approve(Now, director).FirstError.ResourceKey.Should().Be("Po_ApproverMustDifferFromRequester");
    }

    [Fact]
    public async Task OpeningTheStockScreen_WithoutTheRightToSeeTheStock_DraftsNoOrder()
    {
        var looked = 0;
        var planner = new ReorderPlanner(
            Stub.Of<IRepository<PurchaseOrder>>(),
            Stub.Of<IPurchaseOrderQueries>(new()
            {
                ["GetReorderCandidatesAsync"] = _ => { looked++; return Task.FromResult<IReadOnlyList<ReorderCandidate>>([]); }
            }),
            Stub.Of<ISequenceAllocator>(), Stub.Of<IUnitOfWork>(), Stub.Of<ICurrentUser>(), Stub.Of<IClock>());

        InventoryService Service(bool maySeeStock) => new(
            Stub.Of<IRepository<StockItem>>(), Stub.Of<IRepository<StockMovement>>(), Stub.Of<IRepository<Supplier>>(),
            Stub.Of<IInventoryQueries>(), Stub.Of<IUnitOfWork>(),
            Stub.Of<IPermissionGuard>(new()
            {
                ["Require"] = _ => maySeeStock ? Result.Success() : Result.Failure(Error.Forbidden("Message.PermissionDenied"))
            }),
            Stub.Of<ICurrentUser>(), Stub.Of<IClock>(), planner);

        (await Service(maySeeStock: false).RaiseReorderDraftsAsync()).Should().Be(ReorderOutcome.None);
        looked.Should().Be(0, "nothing is drafted in the name of someone who may not see the stock");

        await Service(maySeeStock: true).RaiseReorderDraftsAsync();
        looked.Should().Be(1);
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
