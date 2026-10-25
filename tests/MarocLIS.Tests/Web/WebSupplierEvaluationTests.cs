using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using EvaluationScreen = MarocLIS.Web.Components.Screens.Purchasing.SupplierEvaluation;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Évaluation fournisseurs » in the browser: a supplier whose registration lapsed or whose proposed
/// score is under 50 in the alarm's colours, a score typed that reads no number or is past 100
/// refused in its box, the score applied with the computed one kept in the audit trail, the year's
/// report downloaded as PDF and CSV, and the screen kept by the purchasing right — through the
/// workstation's SupplierEvaluationService.
/// </summary>
public sealed class WebSupplierEvaluationTests : WebScreenContext
{
    private readonly Fake<IPurchasingQualityQueries> _queries = Fake<IPurchasingQualityQueries>.Create();
    private readonly Fake<IRepository<Supplier>> _suppliers = Fake<IRepository<Supplier>>.Create();
    private readonly Fake<IUnitOfWork> _work = Fake<IUnitOfWork>.Create();
    private readonly PdfStub _pdf = new();

    private static readonly Guid Biosystems = Guid.NewGuid(), Sobomed = Guid.NewGuid();

    public WebSupplierEvaluationTests()
    {
        Services.AddSingleton(_queries.Object);
        Services.AddSingleton(_suppliers.Object);
        Services.AddSingleton(_work.Object);
        Services.AddSingleton<IListPdfRenderer>(_pdf);
        Services.AddScoped<SupplierEvaluationService>();

        var onTime = Enumerable.Range(1, 4).Select(m => new SupplierDelivery(new DateOnly(2026, m, 10), new DateOnly(2026, m, 12))).ToList();
        var late = Enumerable.Range(1, 3).Select(m => new SupplierDelivery(new DateOnly(2026, m, 20), new DateOnly(2026, m, 12))).ToList();
        _queries.On(nameof(IPurchasingQualityQueries.GetSupplierEvaluationInputsAsync), _ => new[]
        {
            new SupplierEvaluationInput(Biosystems, "F-BIO", "Biosystems Maroc", true, 80, new DateOnly(2025, 12, 15), "AMMPS-DM-2231",
                new DateOnly(2027, 6, 30), onTime, 0, 0),
            new SupplierEvaluationInput(Sobomed, "F-SOB", "Sobomed", true, 70, new DateOnly(2025, 12, 15), "AMMPS-DM-1904",
                new DateOnly(2026, 9, 30), late, 2, 3)
        });
    }

    private sealed class PdfStub : IListPdfRenderer
    {
        public ListDocument? Last { get; private set; }

        public byte[] Render(ListDocument document)
        {
            Last = document;
            return "%PDF-1.7"u8.ToArray();
        }
    }

    private string? AuditReason => _work.Calls.LastOrDefault(c => c.Method == "set_AuditReason").Arguments is [string reason] ? reason : null;

    [Fact]
    public void ALapsedRegistrationOrAPoorScore_IsInTheAlarmsColours()
    {
        SignIn(AppLanguage.French, Permissions.Inventory.View, Permissions.Inventory.ManagePurchaseOrders);

        var page = Render<EvaluationScreen>();

        page.Find("[data-field=year]").GetAttribute("value").Should().Be("2026");
        page.Find("[data-grid=evaluation] [data-row='F-BIO']").ClassName.Should().NotContain("stock-alarm");
        page.Find("[data-grid=evaluation] [data-row='F-BIO']").TextContent.Should().Contain("100 %").And.Contain("100");
        var sobomed = page.Find("[data-grid=evaluation] [data-row='F-SOB']");
        sobomed.ClassName.Should().Contain("stock-alarm");
        sobomed.QuerySelectorAll("td").Last().ClassName.Should().Contain("critical", "the lapsed registration is said on its date");
        page.Find("[data-state=formula]").TextContent.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AScoreThatReadsNoNumberOrIsPast100_IsRefusedInItsBox_ThenTheScoreIsApplied()
    {
        SignIn(AppLanguage.French, Permissions.Inventory.View, Permissions.Inventory.ManagePurchaseOrders);
        var supplier = new Supplier("F-BIO", "Biosystems Maroc");
        _suppliers.On(nameof(IRepository<Supplier>.GetByIdAsync), _ => supplier);

        var page = Render<EvaluationScreen>();
        page.Find("[data-action='SupplierEvaluation.Apply']").Click();
        page.Find("[data-modal=evaluation-score] [data-field=text]").GetAttribute("value").Should().Be("100", "the proposed score is offered");

        page.Find("[data-modal=evaluation-score] [data-field=text]").Input("abc");
        page.Find("[data-modal=evaluation-score] [data-action='Action.Ok']").Click();
        page.Find("[data-refusal=evaluation-score]").TextContent.Should().Contain("« abc »");

        page.Find("[data-modal=evaluation-score] [data-field=text]").Input("150");
        page.Find("[data-modal=evaluation-score] [data-action='Action.Ok']").Click();
        page.Find("[data-refusal=evaluation-score]").TextContent.Should().Contain("entre 0 et 100");
        _suppliers.CallsTo(nameof(IRepository<Supplier>.GetByIdAsync)).Should().Be(0);

        page.Find("[data-modal=evaluation-score] [data-field=text]").Input("95");
        page.Find("[data-modal=evaluation-score] [data-action='Action.Ok']").Click();

        supplier.EvaluationScore.Should().Be(95);
        AuditReason.Should().StartWith("Évaluation 2026 du fournisseur F-BIO : note 95/100 (note calculée 100/100");
        page.FindAll("[data-modal=evaluation-score]").Should().BeEmpty();
    }

    [Fact]
    public void TheYearsReport_IsDownloadedAsPdfAndCsv()
    {
        SignIn(AppLanguage.French, Permissions.Inventory.View, Permissions.Inventory.ManagePurchaseOrders);

        var page = Render<EvaluationScreen>();
        page.Find("[data-action='SupplierEvaluation.Pdf']").Click();
        page.Find("[data-action='SupplierEvaluation.Csv']").Click();

        _pdf.Last!.Rows.Should().HaveCount(2);
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "marocLis.download" && (string)i.Arguments[0]! == "evaluation-fournisseurs-2026.pdf");
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "marocLis.download" && (string)i.Arguments[0]! == "evaluation-fournisseurs-2026.csv");
    }

    [Fact]
    public void WithoutThePurchasingRight_NothingIsOffered()
    {
        SignIn(AppLanguage.French, Permissions.Inventory.View, Permissions.Inventory.ManageQuotes);

        var page = Render<EvaluationScreen>();

        page.FindAll("[data-action='SupplierEvaluation.Apply']").Should().BeEmpty();
        page.FindAll("[data-action='SupplierEvaluation.Pdf']").Should().BeEmpty();
        page.FindAll("[data-action='SupplierEvaluation.Csv']").Should().BeEmpty();
    }

    [Fact]
    public void TheScreen_IsInArabic_ForAnArabicSession()
    {
        SignIn(AppLanguage.Arabic, Permissions.Inventory.View, Permissions.Inventory.ManagePurchaseOrders);

        var page = Render<EvaluationScreen>();

        page.Find("[data-action='SupplierEvaluation.Apply']").TextContent.Should().NotContain("Appliquer");
        page.Markup.Should().NotContain("[[", "every text of the screen has its Arabic");
    }
}
