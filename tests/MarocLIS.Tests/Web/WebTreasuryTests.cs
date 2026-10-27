using System.Text;
using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Treasury;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Treasury;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using BankReconciliation = MarocLIS.Web.Components.Screens.Finance.BankReconciliation;
using CashForecast = MarocLIS.Web.Components.Screens.Finance.CashForecast;
using Rules = MarocLIS.Tests.Desktop.TreasuryScreenRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Rapprochement bancaire » and « Prévision de trésorerie » in the browser: the statement against
/// the movements in DH, a card batch paid net of its commission matched with its reason, a movement
/// the wrong way refused, a match undone, a statement sent by the browser previewed then imported,
/// a RIB typed wrong refused in its box, the weeks below zero in red and downloaded, a flow typed
/// wrong refused in its box — and a reader of the treasury who sees it all with every action
/// greyed — through the workstation's TreasuryService.
/// </summary>
public sealed class WebTreasuryTests : WebScreenContext
{
    private readonly Fake<ITreasuryQueries> _queries = Fake<ITreasuryQueries>.Create();
    private readonly Fake<IRepository<BankAccount>> _accounts = Fake<IRepository<BankAccount>>.Create();
    private readonly Fake<IRepository<BankStatementLine>> _lines = Fake<IRepository<BankStatementLine>>.Create();
    private readonly Fake<IRepository<BankMatch>> _matches = Fake<IRepository<BankMatch>>.Create();
    private readonly Fake<IRepository<TreasuryForecastItem>> _items = Fake<IRepository<TreasuryForecastItem>>.Create();
    private readonly Fake<IUnitOfWork> _work = Fake<IUnitOfWork>.Create();

    private readonly BankAccountRow _account = Rules.Account("Compte courant — agence Maârif");
    private readonly StatementLineRow _cardLine = Rules.Line(4_740.20m, "REMISE CMI TPE 14/10");
    private readonly StatementLineRow _charges = Rules.Line(-55m, "FRAIS TENUE DE COMPTE");
    private readonly StatementLineRow _matched = Rules.Line(1_250m, "VIR RECU CNOPS", matches: 1, matched: 1_250m, automatic: true,
                                                            TreasuryMovementKind.PayerSettlement);
    private readonly TreasuryMovement _cardBatch = Rules.Movement(4_812.40m);

    private static readonly string[] Manager = [Permissions.Treasury.View, Permissions.Treasury.Manage];

    public WebTreasuryTests()
    {
        Services.AddSingleton(_queries.Object);
        Services.AddSingleton(_accounts.Object);
        Services.AddSingleton(_lines.Object);
        Services.AddSingleton(_matches.Object);
        Services.AddSingleton(_items.Object);
        Services.AddSingleton(_work.Object);
        Services.AddScoped<TreasuryService>();

        _queries.On(nameof(ITreasuryQueries.GetBankAccountsAsync), _ => new[] { _account });
        _queries.On(nameof(ITreasuryQueries.GetStatementLinesAsync), _ => new[] { _cardLine, _charges, _matched });
        _queries.On(nameof(ITreasuryQueries.GetUnmatchedMovementsAsync), _ => new[]
        {
            _cardBatch,
            Rules.Movement(-12_000m, TreasuryMovementKind.SupplierPayment, "SP-1", "Bio-Distribution")
        });
        _queries.On(nameof(ITreasuryQueries.GetForecastSourceAsync), _ => new CashForecastSource(
            [new ForecastOpeningBalance("BMCE Bank — Compte courant", 20_000m, new DateOnly(2026, 10, 15))],
            [], [],
            [new SupplierInvoiceDue("Bio-Distribution", "FA-2026-118", new DateOnly(2026, 10, 28), 65_000m, true)],
            0m, null, 0m));
        _queries.On(nameof(ITreasuryQueries.GetForecastItemsAsync), _ => new[]
        {
            new ForecastItemRow(Guid.NewGuid(), "Loyer du local", -38_000m, ForecastRecurrence.Monthly, new DateOnly(2026, 11, 5), null, true)
        });
    }

    private static string Mad(decimal amount) => Money.Of(amount).Format(AppLanguage.French, MarocLIS.Localization.CultureManager.Build(AppLanguage.French));

    private BankStatementLine Stored(StatementLineRow row) =>
        new(_account.Id, Guid.NewGuid(), Guid.NewGuid(), row.OperationDate, null, row.Label, null, row.AmountMad, "fp-" + row.Label);

    private string? AuditReason => _work.Calls.LastOrDefault(c => c.Method == "set_AuditReason").Arguments is [string reason] ? reason : null;

    private static string Tick(TreasuryMovement m) => $"[data-grid=movements] [data-row='{BankReconciliationScreenRules.MovementId(m)}']";

    // ---- Rapprochement bancaire --------------------------------------------------------------------

    [Fact]
    public void TheReconciliation_SetsTheStatementAgainstTheMovements_InDh_WithTheBalancesUnder()
    {
        SignIn(AppLanguage.French, Manager);

        var page = Render<BankReconciliation>();

        page.Find("[data-field=bank-account]").TextContent.Should().Contain("Compte courant — agence Maârif");
        page.FindAll("[data-grid=lines] tbody tr").Should().HaveCount(3);
        var card = page.Find($"[data-grid=lines] [data-row='{_cardLine.Id}']");
        card.TextContent.Should().Contain(Mad(4_740.20m));
        card.GetAttribute("data-open").Should().Be("true");
        page.Find($"[data-grid=lines] [data-row='{_matched.Id}']").GetAttribute("data-open").Should().Be("false");
        page.Find(Tick(_cardBatch)).TextContent.Should().Contain(Mad(4_812.40m));
        page.Find("[data-card=balances]").TextContent.Should().Contain("DH");

        page.Find("[data-filter=open]").Click();
        page.FindAll("[data-grid=lines] tbody tr").Should().HaveCount(2, "the chip keeps the lines still to reconcile");
    }

    [Fact]
    public void ACardBatchPaidNetOfItsCommission_IsMatched_WithTheReasonForTheDifference()
    {
        SignIn(AppLanguage.French, Manager);
        _lines.On(nameof(IRepository<BankStatementLine>.GetByIdAsync), _ => Stored(_cardLine));
        var page = Render<BankReconciliation>();

        page.Find($"[data-grid=lines] [data-row='{_cardLine.Id}']").Click();
        page.Find(Tick(_cardBatch)).Click();
        page.Find("[data-state=ticked]").TextContent.Should().Contain(Mad(4_812.40m));
        page.Find("[data-action='Treasury.Match']").Click();

        page.Find("[data-modal=reason]").TextContent.Should().Contain(Mad(-72.20m));
        page.Find("[data-modal=reason] [data-field=reason]").Input("Commission CMI de 1,5 % retenue sur la remise");
        page.Find("[data-modal=reason] [data-action='Action.Ok']").Click();

        var match = (BankMatch)_matches.Calls.Single(c => c.Method == nameof(IRepository<BankMatch>.AddAsync)).Arguments[0]!;
        match.Comment.Should().Be("Commission CMI de 1,5 % retenue sur la remise");
        match.AmountMad.Should().Be(4_812.40m);
        AuditReason.Should().StartWith("Rapprochement manuel");
        page.FindAll("[data-modal=reason]").Should().BeEmpty();
    }

    [Fact]
    public void AMovementTheOtherWay_IsRefused_BeforeAnythingIsWritten()
    {
        SignIn(AppLanguage.French, Manager);
        var page = Render<BankReconciliation>();

        page.Find($"[data-grid=lines] [data-row='{_charges.Id}']").Click();
        page.Find(Tick(_cardBatch)).Click();
        page.Find("[data-action='Treasury.Match']").Click();

        page.Find("[data-state=recon-message]").TextContent.Should().Contain("Un encaissement ne peut pas expliquer un débit");
        _matches.CallsTo(nameof(IRepository<BankMatch>.AddAsync)).Should().Be(0);
    }

    [Fact]
    public void AMatch_IsUndone_AfterTheQuestion()
    {
        SignIn(AppLanguage.French, Manager);
        var held = new BankMatch(Guid.NewGuid(), _account.Id, _matched.Id, TreasuryMovementKind.PayerSettlement, "CNOPS-1",
                                 _matched.OperationDate, 1_250m, isAutomatic: true, null, TestClock.Now, Guid.NewGuid());
        _matches.On(nameof(IRepository<BankMatch>.ListAsync), _ => new[] { held });
        var page = Render<BankReconciliation>();

        page.Find($"[data-grid=lines] [data-row='{_matched.Id}']").Click();
        page.Find("[data-action='Treasury.Match']").HasAttribute("disabled").Should().BeTrue("a line already matched is undone first");
        page.Find("[data-action='Treasury.Unmatch']").Click();
        page.Find("[data-modal=confirm]").TextContent.Should().Contain(Mad(1_250m));
        page.Find("[data-modal=confirm] [data-action='Action.Yes']").Click();

        held.UnmatchedAt.Should().NotBeNull();
        AuditReason.Should().StartWith("Annulation du rapprochement");
    }

    [Fact]
    public void AReaderOfTheTreasury_SeesTheReconciliation_WithEveryActionGreyed()
    {
        SignIn(AppLanguage.French, Permissions.Treasury.View);

        var page = Render<BankReconciliation>();
        page.Find($"[data-grid=lines] [data-row='{_charges.Id}']").Click();

        page.FindAll("[data-grid=lines] tbody tr").Should().HaveCount(3);
        foreach (var key in new[] { "Treasury.AutoMatch", "Treasury.Match", "Treasury.Justify", "Treasury.ImportStatement",
                                    "Treasury.BankAccounts", "Treasury.WithdrawImport", "Treasury.Unmatch" })
        {
            var button = page.Find($"[data-action='{key}']");
            button.HasAttribute("disabled").Should().BeTrue(key);
            button.GetAttribute("title").Should().Contain("droit", key);
        }
    }

    [Fact]
    public void AStatementSentByTheBrowser_IsPreviewed_ThenImported()
    {
        SignIn(AppLanguage.French, Manager);
        var stored = new BankAccount(Guid.NewGuid(), "BMCE Bank", "Compte courant", Rules.ValidRib, "514110");
        _accounts.On(nameof(IRepository<BankAccount>.GetByIdAsync), _ => stored);
        var page = Render<BankReconciliation>();

        page.Find("[data-action='Treasury.ImportStatement']").Click();
        var csv = "Date;Libellé;Débit;Crédit\n15/10/2026;REMISE CMI TPE 14/10;;4740,20\n16/10/2026;FRAIS TENUE DE COMPTE;55,00;\n31/02/2026;LIGNE ILLISIBLE;10,00;\n";
        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(Encoding.UTF8.GetBytes(csv), "releve-octobre.csv"));

        page.WaitForAssertion(() => page.FindAll("[data-grid=parsed] tbody tr").Should().HaveCount(3));
        page.FindAll("[data-grid=parsed] [data-status=New]").Should().HaveCount(2);
        page.FindAll("[data-grid=parsed] [data-status=Invalid]").Should().HaveCount(1);
        page.Find("[data-modal=statement-import] .modal-title").TextContent.Should().Contain("releve-octobre.csv");
        page.Find("[data-state=import-summary]").TextContent.Should().NotBeEmpty();

        page.Find("[data-modal=statement-import] [data-action='Action.Import']").Click();
        page.Find("[data-modal=confirm] [data-action='Action.Yes']").Click();

        page.WaitForAssertion(() => _lines.CallsTo(nameof(IRepository<BankStatementLine>.AddAsync)).Should().Be(2));
        stored.CsvMapping.Should().NotBeNull("the mapping is kept with the account for the next statement");
        page.WaitForAssertion(() => page.Find("[data-state=recon-message]").TextContent.Should().Contain("2 lignes importées"));
    }

    [Fact]
    public void ARibTypedWrong_IsRefusedInItsBox()
    {
        SignIn(AppLanguage.French, Manager);
        var page = Render<BankReconciliation>();

        page.Find("[data-action='Treasury.BankAccounts']").Click();
        page.Find("[data-modal=bank-accounts] [data-action='Action.New']").Click();
        page.Find($"[data-field={BankReconciliationScreenRules.BankNameField}]").Input("CIH Bank");
        page.Find($"[data-field={BankReconciliationScreenRules.LabelField}]").Input("Compte de dépôt");
        page.Find($"[data-field={BankReconciliationScreenRules.RibField}]").Input("230780000012345678901225");
        page.Find($"[data-field={BankReconciliationScreenRules.AccountingField}]").Input("514120");
        page.Find("[data-modal=bank-account] [data-action='Action.Save']").Click();

        page.Find($"[data-refusal={BankReconciliationScreenRules.RibField}]").TextContent.Should().Contain("RIB invalide");
        page.Find($"[data-field={BankReconciliationScreenRules.RibField}]").ClassName.Should().Contain("is-refused");
        _work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(0);
    }

    // ---- Prévision de trésorerie ---------------------------------------------------------------------

    [Fact]
    public void TheForecast_ShowsTheWeeksBelowZeroInRed_AndIsDownloaded()
    {
        SignIn(AppLanguage.French, Manager);

        var page = Render<CashForecast>();

        page.FindAll("[data-grid=weeks] tbody tr").Should().HaveCount(13);
        page.Find("[data-grid=weeks] [data-row='1']").GetAttribute("data-shortfall").Should().Be("false");
        var short2 = page.Find("[data-grid=weeks] [data-row='2']");
        short2.GetAttribute("data-shortfall").Should().Be("true");
        short2.TextContent.Should().Contain(Mad(-45_000m));
        page.Find("[data-state=lowest]").ClassName.Should().Contain("critical");
        page.Find("[data-state=openings]").TextContent.Should().Contain("BMCE Bank").And.Contain(Mad(20_000m));

        page.Find("[data-action='Treasury.Csv']").Click();
        var download = JSInterop.Invocations.Single(i => i.Identifier == "marocLis.download");
        download.Arguments[0].Should().Be("prevision-tresorerie-20261018.csv");
    }

    [Fact]
    public void AFlowTypedWrong_IsRefusedInItsBox_ThenSaved()
    {
        SignIn(AppLanguage.French, Manager);
        var page = Render<CashForecast>();

        page.Find("[data-action='Treasury.ForecastItems']").Click();
        page.Find("[data-grid=forecast-items]").TextContent.Should().Contain("Loyer du local").And.Contain(Mad(-38_000m));
        page.Find("[data-modal=forecast-items] [data-action='Action.New']").Click();
        page.Find($"[data-field={CashForecastScreenRules.LabelField}]").Input("Échéance d'emprunt");
        page.Find($"[data-field={CashForecastScreenRules.AmountField}]").Input("cent soixante mille");
        page.Find("[data-modal=forecast-item] [data-action='Action.Save']").Click();

        page.Find($"[data-refusal={CashForecastScreenRules.AmountField}]").TextContent.Should().Contain("montant non nul");
        _items.CallsTo(nameof(IRepository<TreasuryForecastItem>.AddAsync)).Should().Be(0);

        page.Find($"[data-field={CashForecastScreenRules.AmountField}]").Input("-165 000");
        page.Find("[data-modal=forecast-item] [data-action='Action.Save']").Click();

        var added = (TreasuryForecastItem)_items.Calls.Single(c => c.Method == nameof(IRepository<TreasuryForecastItem>.AddAsync)).Arguments[0]!;
        added.AmountMad.Should().Be(-165_000m);
        added.Label.Should().Be("Échéance d'emprunt");
        page.FindAll("[data-modal=forecast-item]").Should().BeEmpty();
    }

    [Fact]
    public void AReaderOfTheTreasury_ReadsTheFlows_ButCannotAddOne()
    {
        SignIn(AppLanguage.French, Permissions.Treasury.View);
        var page = Render<CashForecast>();

        page.Find("[data-action='Treasury.ForecastItems']").Click();

        page.Find("[data-grid=forecast-items]").TextContent.Should().Contain("Loyer du local");
        page.Find("[data-modal=forecast-items] [data-action='Action.New']").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-modal=forecast-items] [data-action='Action.Edit']").HasAttribute("disabled").Should().BeTrue();
    }

    [Theory]
    [InlineData(AppLanguage.Arabic)]
    [InlineData(AppLanguage.English)]
    public void BothScreens_AreTranslated(AppLanguage language)
    {
        SignIn(language, Manager);

        Render<BankReconciliation>().Markup.Should().NotContain("[[");
        Render<CashForecast>().Markup.Should().NotContain("[[");
    }
}
