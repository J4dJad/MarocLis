using Bunit;
using FluentAssertions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Web.Components.Pages;
using MarocLIS.Web.Components.Screens.Billing;
using MarocLIS.Domain.Orders;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Facturation » in the browser: the work to invoice and the ledger, the issue refused before the
/// dialog, the discount read as it is typed, the payment's fields by method, the credit note's
/// reason, the PDF handed to the browser — and what is offered by right.
/// </summary>
public sealed class WebBillingTests : WebFinanceContext
{
    private readonly Guid _patient = Guid.NewGuid();
    private readonly BillableOrderRow _first, _second, _other;
    private readonly InvoiceRow _open = Invoice("FA-2026-000045", total: 450m, paid: 90m);
    private readonly InvoiceRow _settled = Invoice("FA-2026-000044", InvoiceStatus.Paid, total: 300m, paid: 300m);

    public WebBillingTests()
    {
        var payer = Guid.NewGuid();
        _first = Billable("2610-000021", patientId: _patient, payerId: payer);
        _second = Billable("2610-000023", patientId: _patient, payerId: payer, total: 200m, payerShare: 160m);
        _other = Billable("2610-000024", "TAZI Mehdi", payer: null, total: 180m);

        BillingQueries.On(nameof(IBillingQueries.GetUninvoicedOrdersAsync), _ => (IReadOnlyList<BillableOrderRow>)[_first, _second, _other])
                      .On(nameof(IBillingQueries.GetInvoicesAsync), _ => new Application.Abstractions.PagedResult<InvoiceRow>([_open, _settled], 2, 1, 50))
                      .On(nameof(IBillingQueries.GetInvoiceStatusCountsAsync), _ => (IReadOnlyDictionary<InvoiceStatus, int>)
                          new Dictionary<InvoiceStatus, int> { [InvoiceStatus.PartiallyPaid] = 1, [InvoiceStatus.Paid] = 1 })
                      .On(nameof(IBillingQueries.GetInvoiceDocumentAsync), args => (Guid)args[0]! == _open.InvoiceId ? Document(_open) : null);
        InvoiceRenderer.On(nameof(IInvoiceRenderer.RenderInvoice), _ => new byte[] { 0x25, 0x50, 0x44, 0x46 })
                       .On(nameof(IInvoiceRenderer.RenderReceipt), _ => new byte[] { 0x25, 0x50, 0x44, 0x46 });
    }

    [Fact]
    public void TheWorkToInvoice_AndTheLedger_AreListed_WithTheirAmountsAndChips()
    {
        SignIn(AppLanguage.French, Cashier);
        var page = Render<BillingDesk>();

        page.FindAll("[data-grid=uninvoiced] tbody tr").Should().HaveCount(3);
        page.FindAll("[data-grid=ledger] tbody tr").Should().HaveCount(2);
        page.Find("[data-row='2610-000021']").TextContent.Should().Contain(Mad(450m)).And.Contain(Mad(360m));
        page.Find("[data-filter=all]").TextContent.Should().Contain("2");
        page.Find("[data-filter=paid]").GetAttribute("aria-pressed").Should().Be("false");
        page.Find("[data-state=status]").TextContent.Should().Contain(Mad(450m - 90m));
    }

    [Fact]
    public void TheActions_AreInTheCommonBar_TheCollectionAtItsEnd_TheCreditNoteSetApart()
    {
        SignIn(AppLanguage.French, Accounting);
        var page = Render<BillingDesk>();

        page.FindAll("[data-bar=actions]").Should().ContainSingle();
        page.Find("[data-bar=actions] .action-bar-end [data-action='Payment.Title']").ClassList.Should().Contain("btn-primary");
        page.Find("[data-bar=actions] .action-bar-rule + [data-action='Billing.CreditNote']").Should().NotBeNull();
        page.Find("[data-bar=actions] .more-menu [data-action='Payment.MarkBounced']").Should().NotBeNull();
        page.FindAll(".screen-body [data-action]").Select(b => b.GetAttribute("data-action")).Should()
            .NotContain(["Billing.IssueInvoice", "Payment.Title", "Deposit.Take"], "the actions are in the bar only");
    }

    [Fact]
    public void TwoPatientsTicked_AreRefusedOneInvoice_BeforeTheDialog()
    {
        SignIn(AppLanguage.French, Cashier);
        var page = Render<BillingDesk>();

        page.Find("[data-action='Billing.IssueInvoice']").Click();
        page.Find("[data-state=status]").TextContent.Should().NotBeEmpty("nothing ticked is said, not shouted");
        page.FindAll("[data-modal]").Should().BeEmpty();

        page.Find("[data-tick='2610-000021']").Change(true);
        page.Find("[data-tick='2610-000024']").Change(true);
        page.Find("[data-action='Billing.IssueInvoice']").Click();

        page.Find("[data-modal=billing-message]").Should().NotBeNull();
        page.FindAll("[data-modal=issue-invoice]").Should().BeEmpty();
    }

    [Fact]
    public void TheDiscount_IsReadAsItIsTyped_AndBeyondTheCeilingAsksForAnApprover()
    {
        SignIn(AppLanguage.French, Accounting);
        var page = Render<BillingDesk>();

        page.Find("[data-tick='2610-000021']").Change(true);
        page.Find("[data-tick='2610-000023']").Change(true);
        page.Find("[data-action='Billing.IssueInvoice']").Click();

        page.Find("[data-state=gross]").TextContent.Should().Contain(Mad(650m));
        page.Find("[data-field=discount]").Input("5 %");
        page.Find("[data-state=net]").TextContent.Should().Contain(Mad(32.5m)).And.Contain(Mad(617.5m));
        page.FindAll("[data-field=approver]").Should().BeEmpty();

        page.Find("[data-field=discount]").Input("200");
        page.Find("[data-field=approver]").Should().NotBeNull("200 DH is beyond 10 % of 650 DH");

        page.Find("[data-modal=issue-invoice] [data-action='Billing.IssueInvoice']").Click();
        page.Find("[data-refusal=reason]").Should().NotBeNull("a discount carries its reason");
        InvoiceStore.Calls.Should().BeEmpty();
    }

    [Fact]
    public void APayment_TakesTheFieldsOfItsMethod_AndAChequeWithoutItsNumberIsRefused()
    {
        SignIn(AppLanguage.French, Cashier);
        var page = Render<BillingDesk>();

        page.Find("[data-tick='FA-2026-000044']").Change(true);
        page.Find("[data-action='Payment.Title']").Click();
        page.Find("[data-modal=billing-message]").TextContent.Should().Contain("FA-2026-000044");
        page.Find("[data-modal=billing-message] [data-action='Action.Ok']").Click();

        page.Find("[data-tick='FA-2026-000044']").Change(false);
        page.Find("[data-tick='FA-2026-000045']").Change(true);
        page.Find("[data-action='Payment.Title']").Click();

        page.Find("[data-field=amount]").GetAttribute("value").Should().Be("360.00");
        page.FindAll("[data-field=reference]").Should().BeEmpty("cash needs no reference");
        page.Find("[data-field=method]").Change(nameof(PaymentMethod.Cheque));
        page.Find("[data-field=reference]").Should().NotBeNull();

        page.Find("[data-modal=payment] [data-action='Action.Save']").Click();
        page.Find("[data-refusal=reference]").Should().NotBeNull();
    }

    [Fact]
    public void ACreditNote_AsksItsReason_ThenGoesToTheService()
    {
        SignIn(AppLanguage.French, Accounting);
        var page = Render<BillingDesk>();

        page.Find("[data-tick='FA-2026-000045']").Change(true);
        page.Find("[data-action='Billing.CreditNote']").ClassList.Should().Contain("btn-danger");
        page.Find("[data-action='Billing.CreditNote']").Click();

        page.Find("[data-field=reason]").Input("Erreur de cotation sur la NFS");
        page.Find("[data-modal=reason] [data-action='Action.Ok']").Click();

        InvoiceStore.Calls.Should().NotBeEmpty("the credit note is the service's");
    }

    [Fact]
    public void TheInvoice_IsDownloadedAsAPdf()
    {
        SignIn(AppLanguage.French, Cashier);
        var page = Render<BillingDesk>();

        page.Find("[data-action='Billing.PrintInvoice']").Click();
        page.WaitForAssertion(() => JSInterop.Invocations.Should().Contain(i => i.Identifier == "marocLis.download"
                                                                             && (string)i.Arguments[0]! == "FA-2026-000045.pdf"));
        page.FindAll("[data-modal=billing-message]").Should().BeEmpty(page.Markup);
    }

    [Fact]
    public void WithTheRightToReadOnly_NothingIsInvoicedCollectedOrCorrected()
    {
        SignIn(AppLanguage.French, Permissions.Billing.View);
        var page = Render<BillingDesk>();

        foreach (var action in new[] { "Billing.IssueInvoice", "Payment.Title", "Billing.CreditNote", "Deposit.Take", "Deposit.Refund",
                                       "Payment.Refund", "Payment.MarkBounced", "Billing.SubmitClaim" })
            page.Find($"[data-action='{action}']").HasAttribute("disabled").Should().BeTrue(action);

        page.Find("[data-action='Payment.MarkBounced']").GetAttribute("title").Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void OpenedFromAFile_TheLedgerIsSearchedOnIt_AndItsRequestTicked()
    {
        SignIn(AppLanguage.French, Cashier);
        var page = Render<BillingDesk>(p => p.Add(x => x.Search, "2610-000023"));

        page.Find("[data-field=invoice-search]").GetAttribute("value").Should().Be("2610-000023");
        page.Find("[data-tick='2610-000023']").HasAttribute("checked").Should().BeTrue();
        BillingQueries.Calls.Should().Contain(c => c.Method == nameof(IBillingQueries.GetInvoicesAsync) && (string?)c.Arguments[2] == "2610-000023");
    }

    [Fact]
    public void OpenedFromTheMenu_TheLedgerIsNotSearched()
    {
        SignIn(AppLanguage.French, Cashier);

        var page = Render<Screen>(p => p.Add(x => x.Key, "billing"));

        page.Find("[data-field=invoice-search]").GetAttribute("value").Should().BeNullOrEmpty();
        BillingQueries.Calls.Should().Contain(c => c.Method == nameof(IBillingQueries.GetInvoicesAsync) && string.IsNullOrEmpty((string?)c.Arguments[2]));
    }

    [Fact]
    public void TheFile_OpensBillingAndTheReports_OnItsNumber()
    {
        var file = Desktop.OrderFileRulesTests.Detail(LabOrderStatus.Reported);
        Orders.On(nameof(Application.Orders.IOrderQueries.GetDetailAsync), _ => file);
        FileQueries.On(nameof(Application.Orders.IOrderFileQueries.GetHistoryAsync), _ => (IReadOnlyList<Application.Orders.OrderFileEvent>)
            [new(TestClock.Now.AddHours(-1), Application.Orders.OrderFileEventKind.ReportReleased, "Dr AMRANI Salma", null, 1)]);
        SignIn(AppLanguage.French, [.. Cashier, Permissions.Orders.View]);
        var page = Render<MarocLIS.Web.Components.Screens.Orders.OrderFile>(p => p.Add(x => x.OrderId, file.OrderId));
        var navigation = Services.GetRequiredService<NavigationManager>();

        page.Find("[data-tab=History]").Click();
        page.Find("[data-action='OrderFile.OpenReports']").GetAttribute("href").Should().Be($"ecran/reports?recherche={file.AccessionNumber}");

        page.Find("[data-tab=Billing]").Click();
        page.Find("[data-action='OrderFile.OpenBilling']").Click();
        navigation.Uri.Should().EndWith($"ecran/billing?recherche={file.AccessionNumber}");
    }

    [Fact]
    public void ATechnician_TypingTheAddressOfBillingOrTheTill_IsRefused()
    {
        SignIn(AppLanguage.French, Permissions.Results.View, Permissions.Results.Enter);

        foreach (var key in new[] { "billing", "cash", "claims" })
        {
            var refused = Render<Screen>(p => p.Add(x => x.Key, key));
            refused.Find("[data-state=refused]").TextContent.Should().Contain("Vous n'avez pas le droit", key);
            refused.FindAll("[data-screen]").Should().BeEmpty(key);
        }
    }
}
