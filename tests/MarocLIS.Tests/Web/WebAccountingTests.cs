using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Accounting;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Administration;
using MarocLIS.Reporting;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AccountingExport = MarocLIS.Web.Components.Screens.Finance.AccountingExport;
using AccountingSettingsScreen = MarocLIS.Web.Components.Screens.Finance.AccountingSettings;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Export comptable » and « Comptes et journaux » in the browser: September checked, its
/// depreciation journal summed in DH and the file downloaded; an entry on a wrong account listed and
/// the export refused; no site ticked refused on the sites' box; a reader without the right offered
/// nothing; an account typed wrong refused on its own line, then saved — through the workstation's
/// AccountingExportService.
/// </summary>
public sealed class WebAccountingTests : WebScreenContext
{
    private readonly Fake<IAccountingQueries> _queries = Fake<IAccountingQueries>.Create();
    private readonly Fake<ISupplierInvoiceQueries> _supplier = Fake<ISupplierInvoiceQueries>.Create();
    private readonly Fake<IConfigurationWriter> _writer = Fake<IConfigurationWriter>.Create();
    private readonly Fake<IUnitOfWork> _work = Fake<IUnitOfWork>.Create();
    private readonly Depreciations _depreciation = new();

    private sealed class Depreciations : IDepreciationEntrySource
    {
        public string ChargeAccount { get; set; } = "61932";

        public Task<IReadOnlyList<DepreciationEntry>> GetEntriesAsync(Guid siteId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DepreciationEntry>>(
            [
                new(new DateOnly(2026, 9, 30), "IMM-2024-002", "Centrifugeuse réfrigérée", ChargeAccount, "28340", 1_416.67m),
                new(new DateOnly(2026, 9, 30), "IMM-2018-001", "Automate d'hémostase", "61932", "28340", 2_000m)
            ]);
    }

    private sealed class PdfStub : IListPdfRenderer
    {
        public byte[] Render(ListDocument document) => "%PDF-1.7"u8.ToArray();
    }

    public WebAccountingTests()
    {
        Services.AddSingleton(_queries.Object);
        Services.AddSingleton(_supplier.Object);
        Services.AddSingleton(_writer.Object);
        Services.AddSingleton(_work.Object);
        Services.AddSingleton<IDepreciationEntrySource>(_depreciation);
        Services.AddSingleton<IListPdfRenderer>(new PdfStub());
        Services.AddScoped<AccountingExportService>();

        // Whatever sites the user may see are answered as Casablanca: the service asks for its accessible ones.
        _queries.On(nameof(IAccountingQueries.GetSitesAsync), args =>
            ((IReadOnlyCollection<Guid>)args[0]!).Select(id => new AccountingSiteOption(id, "CAS", "Casablanca Centre")).ToList());
        _queries.On(nameof(IAccountingQueries.GetSalesSourcesAsync), _ => SalesAccountingSources.Empty);
    }

    private static string Mad(decimal amount) => Money.Of(amount).Format(AppLanguage.French, MarocLIS.Localization.CultureManager.Build(AppLanguage.French));

    // ---- Export comptable ------------------------------------------------------------------------------

    [Fact]
    public void September_IsChecked_ItsDepreciationSummedInDh_AndTheFileDownloaded()
    {
        SignIn(AppLanguage.French, Permissions.Accounting.Export);
        var page = Render<AccountingExport>();

        page.Find("[data-field=export-from]").GetAttribute("value").Should().Be("2026-09-01");
        page.Find($"[data-field={AccountingExportScreenRules.PeriodField}]").GetAttribute("value").Should().Be("2026-09-30");
        page.Find("[data-action='Action.Export']").HasAttribute("disabled").Should().BeTrue("nothing is exported before it is checked");

        page.Find("[data-action='Accounting.Check']").Click();

        var line = page.Find("[data-grid=journals] [data-row='OD-Depreciation']");
        line.TextContent.Should().Contain(Mad(3_416.67m));
        page.Find("[data-state=no-problem]").Should().NotBeNull();
        page.Find("[data-action='Action.Export']").HasAttribute("disabled").Should().BeFalse();

        page.Find("[data-action='Action.Export']").Click();
        page.WaitForAssertion(() => JSInterop.Invocations.Should().Contain(i => i.Identifier == "marocLis.download"
                                                                               && (string)i.Arguments[0]! == "export-comptable-sage-20260901-20260930.csv"));
        DataAudit.Entries.Should().ContainSingle(e => e.Context == "Accounting/Export");
    }

    [Fact]
    public void AnEntryOnAWrongAccount_IsListed_AndTheExportRefused()
    {
        _depreciation.ChargeAccount = "342";
        SignIn(AppLanguage.French, Permissions.Accounting.Export);
        var page = Render<AccountingExport>();

        page.Find("[data-action='Accounting.Check']").Click();

        page.Find("[data-state=check]").TextContent.Should().Contain("Export refusé : 1 anomalie");
        page.Find("[data-grid=problems] tbody tr").TextContent.Should().Contain("IMM-2024-002");
        page.Find("[data-action='Action.Export']").HasAttribute("disabled").Should().BeTrue();
        JSInterop.Invocations.Should().NotContain(i => i.Identifier == "marocLis.download");
    }

    [Fact]
    public void NoSiteTicked_IsRefusedOnTheSitesBox_AndAChangeClearsTheCheck()
    {
        SignIn(AppLanguage.French, Permissions.Accounting.Export);
        var page = Render<AccountingExport>();
        page.Find("[data-action='Accounting.Check']").Click();
        page.FindAll("[data-grid=journals] tbody tr").Should().NotBeEmpty();

        page.Find("[data-site=CAS]").Change(false);
        page.FindAll("[data-grid=journals] tbody tr").Should().BeEmpty("what was checked no longer says what would be exported");

        page.Find("[data-action='Accounting.Check']").Click();
        page.Find($"[data-refusal={AccountingExportScreenRules.SitesField}]").TextContent.Should().Contain("Cochez au moins un site");
        _queries.CallsTo(nameof(IAccountingQueries.GetSettingValuesAsync)).Should().Be(1, "only the first check reached the service");
    }

    [Fact]
    public void TheVatStatement_IsDownloadedAsPdf()
    {
        SignIn(AppLanguage.French, Permissions.Accounting.Export);
        var page = Render<AccountingExport>();

        page.Find("[data-action='Accounting.VatStatementPdf']").Click();

        page.WaitForAssertion(() => JSInterop.Invocations.Should().Contain(i => i.Identifier == "marocLis.download"
                                                                               && ((string)i.Arguments[0]!).EndsWith(".pdf")));
    }

    [Fact]
    public void WithoutTheRight_NothingIsOffered()
    {
        SignIn(AppLanguage.French, Permissions.Billing.View);
        var page = Render<AccountingExport>();

        page.FindAll("[data-action='Accounting.Check']").Should().BeEmpty();
        page.FindAll("[data-action='Action.Export']").Should().BeEmpty();
        page.FindAll("[data-action='Accounting.VatStatementPdf']").Should().BeEmpty();
    }

    [Fact]
    public void TheExport_IsInArabic_ForAnArabicSession()
    {
        SignIn(AppLanguage.Arabic, Permissions.Accounting.Export);
        var page = Render<AccountingExport>();
        page.Find("[data-action='Accounting.Check']").Click();

        page.Find("[data-grid=journals] [data-row='OD-Depreciation']").TextContent.Should().Contain("د.م.");
        page.Markup.Should().NotContain("[[", "every text of the screen has its Arabic");
    }

    // ---- Comptes et journaux ---------------------------------------------------------------------------

    [Fact]
    public void AnAccountTypedWrong_IsRefusedOnItsLine_ThenSaved()
    {
        SignIn(AppLanguage.French, Permissions.Accounting.Export);
        var page = Render<AccountingSettingsScreen>();
        var revenue = $"[data-field='{AccountingSettings.DefaultRevenueKey}']";

        page.Find(revenue).Input("7O12");
        page.Find("[data-action='Action.Save']").Click();

        page.Find($"[data-row='{AccountingSettings.DefaultRevenueKey}']").ClassName.Should().Contain("stock-warn");
        page.Find($"[data-refusal='{AccountingSettings.DefaultRevenueKey}']").TextContent.Should().Contain("« 7O12 » n'est pas un compte valide");
        _work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(0);

        page.Find(revenue).Input("71240");
        page.Find("[data-action='Action.Save']").Click();

        page.WaitForAssertion(() => _work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(1));
        var added = _writer.Calls.Where(c => c.Method == nameof(IConfigurationWriter.AddSettingAsync))
                           .Select(c => (AppSetting)c.Arguments[0]!).ToList();
        added.Should().ContainSingle(a => a.Key == AccountingSettings.DefaultRevenueKey && a.Value == "71240");
        _work.Calls.Should().Contain(c => c.Method == "set_AuditReason" && ((string)c.Arguments[0]!).Contains(AccountingSettings.DefaultRevenueKey));
    }

    [Fact]
    public void TheSettings_AreReadOnly_WithoutTheRight_AndInArabic()
    {
        SignIn(AppLanguage.Arabic, Permissions.Billing.View);
        var page = Render<AccountingSettingsScreen>();

        page.FindAll("[data-action='Action.Save']").Should().BeEmpty();
        page.Markup.Should().NotContain("[[", "every text of the screen has its Arabic");
    }
}
