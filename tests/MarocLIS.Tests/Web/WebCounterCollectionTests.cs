using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Reports;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Samples;
using MarocLIS.Web.Components.Screens.Billing;
using MarocLIS.Web.Components.Screens.Orders;
using MarocLIS.Web.Components.Screens.Reports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static MarocLIS.Tests.Desktop.OrderFileRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Encaissement » and « À encaisser » in the browser: the patient's share and what is due, the
/// method preselected, the change on cash, F9 / F10 on the buttons, the refusals said before the
/// service, the till offered when none is open, the receipt downloaded, the « Encaisser » buttons of
/// the file and the report counter — and none of it for whoever does not take payments.
/// </summary>
public sealed class WebCounterCollectionTests : WebFinanceContext
{
    private readonly Fake<ICounterCollectionQueries> _collect = Fake<ICounterCollectionQueries>.Create();
    private readonly Guid _orderId = Guid.NewGuid();

    private static readonly string[] Reception =
        [Permissions.Orders.View, Permissions.Orders.Create, Permissions.Patients.View, Permissions.Billing.View];

    public WebCounterCollectionTests()
    {
        Services.AddSingleton(_collect.Object);
        Services.AddScoped<CounterCollectionService>();
    }

    private CounterBalance Balance(decimal due = 672.30m, Guid? invoice = null, decimal paid = 0m, string? number = null) =>
        new(_orderId, "2610-000041", Guid.NewGuid(), "ALAOUI Salma", TestClock.Now.AddMinutes(-5),
            invoice is null ? new OrderPayment(OrderPaymentState.NotInvoiced, due)
                            : new OrderPayment(due > 0m ? OrderPaymentState.PatientShareDue : OrderPaymentState.Paid, due, number),
            invoice, 672.30m, 0m, 74.70m, paid);

    private void Answer(CounterBalance balance, PaymentMethod? last = null) =>
        _collect.On(nameof(ICounterCollectionQueries.GetBalanceAsync), _ => balance)
                .On(nameof(ICounterCollectionQueries.GetLastMethodAsync), _ => last);

    private IRenderedComponent<CollectPanel> Panel(Action<CollectionDone?>? closed = null) =>
        Render<CollectPanel>(p => p.Add(x => x.OrderId, _orderId)
                                   .Add(x => x.OnClosed, (CollectionDone? done) => closed?.Invoke(done)));

    // ---- The panel ---------------------------------------------------------------------------------

    [Fact]
    public void ThePanel_ShowsTheShareDue_OnTheLastMethod_WithItsKeys()
    {
        SignIn(AppLanguage.French, Cashier);
        Answer(Balance(), PaymentMethod.Card);

        var panel = Panel();

        panel.Find("[data-modal=collect] .modal-title").TextContent.Should().Contain("2610-000041").And.Contain("ALAOUI Salma");
        panel.Find("[data-state=due]").TextContent.Should().Be(Mad(672.30m));
        panel.Find("[data-state=figures]").TextContent.Should().Contain(Mad(74.70m), "the discount granted is shown");
        panel.Find("[data-field=method]").QuerySelector("option[selected]")!.GetAttribute("value").Should().Be("Card",
            "the method the user took their last payment by");
        panel.Find("[data-field=deposit-amount]").GetAttribute("value").Should().Be("672,30");
        panel.Find("[data-action='Collect.PayAll']").GetAttribute("data-shortcut").Should().Be("F9");
        panel.Find("[data-action='Collect.Deposit']").GetAttribute("data-shortcut").Should().Be("F10");
        panel.Find("[data-action='Collect.Later']").Should().NotBeNull();
        panel.FindAll("[data-field=tendered]").Should().BeEmpty("a card takes no cash");
        panel.Find("[data-state=invoice]").TextContent.Should().Contain("Tout payer");
    }

    [Fact]
    public void Cash_ShowsTheChange_AndAShortSumIsRefusedBeforeTheService()
    {
        SignIn(AppLanguage.French, Cashier);
        Answer(Balance(250m));

        var panel = Panel();

        panel.Find("[data-field=method]").QuerySelector("option[selected]")!.GetAttribute("value").Should().Be("Cash", "cash by default");
        panel.Find("[data-field=tendered]").Input("300");
        panel.Find("[data-state=change]").TextContent.Should().Contain(Mad(50m));

        panel.Find("[data-field=tendered]").Input("200");
        panel.Find("[data-action='Collect.PayAll']").Click();
        panel.Find("[data-refusal=tendered]").TextContent.Should().Contain(Mad(250m));
        _collect.CallsTo(nameof(ICounterCollectionQueries.GetBalanceAsync)).Should().Be(1, "nothing asked of the service");
    }

    [Fact]
    public void ADepositAboveWhatIsDue_AndAChequeWithoutItsNumber_AreRefusedBesideTheirBoxes()
    {
        SignIn(AppLanguage.French, Cashier);
        Answer(Balance(250m), PaymentMethod.Cheque);

        var panel = Panel();

        panel.Find("[data-action='Collect.PayAll']").Click();
        panel.Find("[data-refusal=reference]").Should().NotBeNull();

        panel.Find("[data-field=reference]").Input("CHQ 0012345");
        panel.Find("[data-field=deposit-amount]").Input("300");
        panel.Find("[data-action='Collect.Deposit']").Click();
        panel.Find("[data-refusal=amount]").TextContent.Should().Contain(Mad(250m));
    }

    [Fact]
    public void CashWithNoTillOpen_IsRefusedByTheService_AndTheTillIsOfferedInThePanel()
    {
        SignIn(AppLanguage.French, Cashier);
        Answer(Balance(250m));

        var panel = Panel();

        panel.Find("[data-state=till-closed]").TextContent.Should().Contain("Aucune caisse ouverte");
        panel.Find("[data-action='Collect.PayAll']").Click();
        panel.Find("[data-state=error]").TextContent.Should().Contain("session de caisse ouverte");
        InvoiceStore.Calls.Should().BeEmpty("refused before any invoice is touched");

        panel.Find("[data-field=till-float]").Input("200");
        panel.Find("[data-state=till-closed] [data-action='Payment.OpenSession']").Click();
        SessionStore.CallsTo(nameof(IRepository<CashSession>.AddAsync)).Should().Be(1);
        panel.Find("[data-state=till-opened]").TextContent.Should().Contain(Mad(200m));
    }

    [Fact]
    public void PayingEverythingOnAnInvoice_SettlesIt_AndTheReceiptIsDownloaded()
    {
        SignIn(AppLanguage.French, Cashier);
        var user = Services.GetRequiredService<MarocLIS.Web.Session.SessionContext>().User;
        var invoice = new Invoice(string.Empty, user.SiteId, InvoiceKind.Invoice, new DateOnly(2026, 10, 18), user.UserId)
        {
            BilledToName = "ALAOUI Salma"
        };
        invoice.AddLine(new InvoiceLine("Bilan", 672.30m)).IsSuccess.Should().BeTrue();
        invoice.Issue("FAC-2026-000012", TestClock.Now, null).IsSuccess.Should().BeTrue();
        InvoiceStore.On(nameof(IRepository<Invoice>.GetByIdWithDetailsAsync), _ => invoice);

        var reads = 0;
        _collect.On(nameof(ICounterCollectionQueries.GetBalanceAsync),
                    _ => reads++ < 2 ? Balance(672.30m, invoice.Id, number: invoice.Number) : Balance(0m, invoice.Id, 672.30m, invoice.Number))
                .On(nameof(ICounterCollectionQueries.GetLastMethodAsync), _ => (PaymentMethod?)PaymentMethod.Card);
        BillingQueries.On(nameof(IBillingQueries.GetInvoiceDocumentAsync), _ => Document(Invoice(invoice.Number, InvoiceStatus.Paid, 672.30m, 672.30m)));

        CollectionDone? closed = null;
        var panel = Panel(done => closed = done);
        panel.Find("[data-state=invoice]").TextContent.Should().Contain("FAC-2026-000012");

        panel.Find("[data-action='Collect.PayAll']").Click();

        panel.FindAll("[data-state=error]").Should().BeEmpty("the panel read the file, then the service read it again");
        panel.Find("[data-state=done]").TextContent.Should().Contain(Mad(672.30m)).And.Contain("FAC-2026-000012");
        invoice.Status.Should().Be(InvoiceStatus.Paid);
        invoice.Payments.Should().ContainSingle().Which.Notes.Should().Be(CounterCollectionService.CounterNote);
        Work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(1);

        panel.Find("[data-action='Collect.ReceiptPdf']").Click();
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "marocLis.download"
                                                    && (string)i.Arguments[0]! == "FAC-2026-000012-recu.pdf");

        panel.Find("[data-action='Action.Ok']").Click();
        closed.Should().NotBeNull();
        closed!.InvoiceNumber.Should().Be("FAC-2026-000012");
    }

    [Fact]
    public void Later_ClosesWithoutTakingAnything()
    {
        SignIn(AppLanguage.French, Cashier);
        Answer(Balance());
        var closedCalled = false;
        CollectionDone? closed = null;

        var panel = Panel(done => { closedCalled = true; closed = done; });
        panel.Find("[data-action='Collect.Later']").Click();

        closedCalled.Should().BeTrue();
        closed.Should().BeNull();
        InvoiceStore.Calls.Should().BeEmpty();
    }

    [Fact]
    public void ThePanel_SpeaksArabic()
    {
        SignIn(AppLanguage.Arabic, Cashier);
        Answer(Balance());

        var panel = Panel();

        panel.Find("[data-action='Collect.PayAll']").TextContent.Should().MatchRegex(@"\p{IsArabic}");
        panel.Find("[data-action='Collect.Deposit']").TextContent.Should().MatchRegex(@"\p{IsArabic}");
        panel.Find("[data-state=figures]").TextContent.Should().MatchRegex(@"\p{IsArabic}");
    }

    // ---- « À encaisser » -----------------------------------------------------------------------------

    [Fact]
    public void TheQueue_ListsTodaysFilesStillOwing_AndOneClickOpensThePanel()
    {
        SignIn(AppLanguage.French, Cashier);
        var at = TestClock.Now.AddHours(-1);
        _collect.On(nameof(ICounterCollectionQueries.GetQueueAsync), _ => (IReadOnlyList<CollectionQueueRow>)
        [
            new(_orderId, "2610-000041", "ALAOUI Salma", at, OrderPaymentState.NotInvoiced, 672.30m, null, 0m),
            new(Guid.NewGuid(), "2610-000042", "TAZI Omar", at.AddMinutes(10), OrderPaymentState.PatientShareDue, 80m, "FAC-2026-000013", 20m)
        ]);
        Answer(Balance());

        var page = Render<CollectionQueue>();

        page.FindAll("[data-grid=tocollect] tbody tr").Should().HaveCount(2);
        page.Find("[data-state=queue-total]").TextContent.Should().Contain("2 dossiers").And.Contain(Mad(752.30m));
        page.FindAll("[data-grid=tocollect] tbody tr")[1].TextContent.Should().Contain("FAC-2026-000013").And.Contain(Mad(20m));

        page.Find("[data-row='2610-000041']").Click();
        page.Find("[data-modal=collect] [data-state=due]").TextContent.Should().Be(Mad(672.30m));
    }

    [Fact]
    public void AnEmptyQueue_SaysSo_AndTheTillIsOfferedAtTheStart()
    {
        SignIn(AppLanguage.French, Cashier);

        var page = Render<CollectionQueue>();

        page.Find("[data-state=queue-empty]").TextContent.Should().Contain("Rien à encaisser");
        page.Find("[data-state=till-closed]").Should().NotBeNull("no till is open: it is offered before the first patient");
    }

    // ---- Where the patient is -------------------------------------------------------------------------

    [Fact]
    public void TheFile_OffersEncaisser_ToACashierOnly()
    {
        var file = Detail(items: [Item(LabOrderItemStatus.Pending, code: "GLY")], samples: [Sample(SampleStatus.Received)]) with { OrderId = _orderId };
        Orders.On(nameof(IOrderQueries.SearchAsync), _ => (IReadOnlyList<OrderListRow>)
                  [new(_orderId, file.AccessionNumber, file.PatientName, TestClock.Now, file.Status, OrderPriority.Routine, 1)])
              .On(nameof(IOrderQueries.GetDetailAsync), _ => file);
        Answer(Balance());

        SignIn(AppLanguage.French, [.. Cashier, Permissions.Orders.View]);
        var page = Render<OrderFile>(p => p.Add(x => x.OrderId, _orderId));
        page.Find(".action-bar [data-action='Collect.Action']").Click();
        page.Find("[data-modal=collect] [data-state=due]").TextContent.Should().Be(Mad(672.30m));
    }

    [Fact]
    public void WithoutThePaymentRight_TheFileHidesEncaisser()
    {
        var file = Detail(items: [Item(LabOrderItemStatus.Pending, code: "GLY")], samples: [Sample(SampleStatus.Received)]) with { OrderId = _orderId };
        Orders.On(nameof(IOrderQueries.SearchAsync), _ => (IReadOnlyList<OrderListRow>)[])
              .On(nameof(IOrderQueries.GetDetailAsync), _ => file);

        SignIn(AppLanguage.French, Reception);
        var page = Render<OrderFile>(p => p.Add(x => x.OrderId, _orderId));
        page.WaitForAssertion(() => page.Find("[data-file]").Should().NotBeNull());
        page.FindAll("[data-action='Collect.Action']").Should().BeEmpty();
    }

    [Fact]
    public void TheReportCounter_OffersEncaisser_OnAFileStillOwing()
    {
        var owing = Waiting(payment: new OrderPayment(OrderPaymentState.PatientShareDue, 120m, "FAC-2026-000012")) with { LabOrderId = _orderId };
        ReportQueries.On(nameof(IReportQueries.GetPendingReleaseAsync), _ => (IReadOnlyList<PendingReportRow>)[owing]);
        Answer(Balance(120m, Guid.NewGuid(), number: "FAC-2026-000012"));

        SignIn(AppLanguage.French, Cashier);
        var page = Render<ReportRegister>(p => p.Add(x => x.Tab, "ToIssue"));
        page.Find("[data-row='2610-000031']").Click();
        page.Find("[data-card=preview] [data-action='Collect.Action']").Click();
        page.Find("[data-modal=collect] [data-state=due]").TextContent.Should().Be(Mad(120m));
    }

    [Fact]
    public void TheReportCounter_HidesEncaisser_FromWhoeverDoesNotTakePayments()
    {
        var owing = Waiting("2610-000032", payment: new OrderPayment(OrderPaymentState.PatientShareDue, 120m, "FAC-2026-000013"));
        ReportQueries.On(nameof(IReportQueries.GetPendingReleaseAsync), _ => (IReadOnlyList<PendingReportRow>)[owing]);

        SignIn(AppLanguage.French, Permissions.Reports.View, Permissions.Reports.Print, Permissions.Billing.View);
        var page = Render<ReportRegister>(p => p.Add(x => x.Tab, "ToIssue"));
        page.Find("[data-row='2610-000032']").Click();
        page.FindAll("[data-action='Collect.Action']").Should().BeEmpty("no billing.payment.record");
    }

    [Fact]
    public void TheReportCounter_HidesEncaisser_OnAFilePaid()
    {
        var paid = Waiting(payment: new OrderPayment(OrderPaymentState.Paid, 0m, "FAC-2026-000012"));
        ReportQueries.On(nameof(IReportQueries.GetPendingReleaseAsync), _ => (IReadOnlyList<PendingReportRow>)[paid]);

        SignIn(AppLanguage.French, Cashier);
        var page = Render<ReportRegister>(p => p.Add(x => x.Tab, "ToIssue"));
        page.Find("[data-row='2610-000031']").Click();
        page.FindAll("[data-action='Collect.Action']").Should().BeEmpty("settled");
    }

    [Fact]
    public void TheRegistrationScreen_OffersTheTill_ToACashier()
    {
        SignIn(AppLanguage.French, [.. Reception, Permissions.Billing.RecordPayment]);
        Render<OrderRegistration>().Find("[data-state=till-closed]").Should().NotBeNull();
    }

    [Fact]
    public void TheRegistrationScreen_SaysNothingOfTheTill_ToReception()
    {
        SignIn(AppLanguage.French, Reception);
        Render<OrderRegistration>().FindAll("[data-state=till-closed]").Should().BeEmpty();
    }
}
