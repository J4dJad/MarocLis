using FluentAssertions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Encaissement » and « À encaisser », shared by the workstation and the web: who gets
/// the panel, when it opens by itself after « Enregistrer », the method it opens on, what it refuses
/// before the service, the change on cash, and the till offered at the start of the shift.
/// </summary>
public sealed class CounterCollectionRulesTests
{
    private static Func<string, bool> Holding(params string[] rights) => rights.Contains;

    private static readonly Func<string, bool> Cashier = Holding(Permissions.Billing.View, Permissions.Billing.RecordPayment);
    private static readonly Func<string, bool> Reception = Holding(Permissions.Orders.Create, Permissions.Billing.View);

    private static CollectionDone Done(Guid? invoice = null, bool issued = false, string? deposit = null, decimal left = 0m,
                                       CollectionKind kind = CollectionKind.Full) =>
        new(Guid.NewGuid(), "2610-000041", kind, 120m, PaymentMethod.Cash, invoice, invoice is null ? null : "FAC-2026-000012",
            issued, deposit, left);

    [Fact]
    public void The_panel_is_the_payment_right_s()
    {
        CounterCollectionRules.CanCollect(Cashier).Should().BeTrue();
        CounterCollectionRules.CanCollect(Reception).Should().BeFalse("registering is not taking money: billing.payment.record");
    }

    [Fact]
    public void Encaisser_is_offered_while_the_patient_owes_something()
    {
        var due = new OrderPayment(OrderPaymentState.PatientShareDue, 80m, "FAC-2026-000012");
        var notInvoiced = new OrderPayment(OrderPaymentState.NotInvoiced, 250m);

        CounterCollectionRules.Offers(due, Cashier).Should().BeTrue();
        CounterCollectionRules.Offers(notInvoiced, Cashier).Should().BeTrue();
        CounterCollectionRules.Offers(due, Reception).Should().BeFalse();
        CounterCollectionRules.Offers(new OrderPayment(OrderPaymentState.Paid, 0m), Cashier).Should().BeFalse();
        CounterCollectionRules.Offers(OrderPayment.NothingDue, Cashier).Should().BeFalse();
        CounterCollectionRules.Offers(new OrderPayment(OrderPaymentState.NotInvoiced, 0m), Cashier)
            .Should().BeFalse("deposits held cover it: nothing is left to take");
        CounterCollectionRules.Offers(null, Cashier).Should().BeFalse();
    }

    [Fact]
    public void After_registering_the_panel_opens_only_for_a_cashier_and_a_share_to_pay()
    {
        CounterCollectionRules.OpensAfterRegistration(Cashier, Money.Of(120m)).Should().BeTrue();
        CounterCollectionRules.OpensAfterRegistration(Cashier, Money.Zero).Should().BeFalse("all of it is the payer's");
        CounterCollectionRules.OpensAfterRegistration(Reception, Money.Of(120m)).Should().BeFalse("« Plus tard »: the billing desk takes it");
    }

    [Fact]
    public void The_method_is_the_user_s_last_one_or_cash()
    {
        CounterCollectionRules.DefaultMethod(null).Should().Be(PaymentMethod.Cash);
        CounterCollectionRules.DefaultMethod(PaymentMethod.Card).Should().Be(PaymentMethod.Card);
        CounterCollectionRules.DefaultMethod(PaymentMethod.Cheque).Should().Be(PaymentMethod.Cheque);
        CounterCollectionRules.DefaultMethod(PaymentMethod.ThirdPartyPayer).Should().Be(PaymentMethod.Cash, "not a counter method");
        CounterCollectionRules.DefaultMethod(PaymentMethod.WriteOff).Should().Be(PaymentMethod.Cash);
    }

    [Fact]
    public void A_cheque_a_bill_and_a_transfer_need_their_reference()
    {
        CounterCollectionRules.MissingReference(PaymentMethod.Cheque, " ").Should().Be("Payment_ReferenceRequired");
        CounterCollectionRules.MissingReference(PaymentMethod.BillOfExchange, null).Should().Be("Payment_ReferenceRequired");
        CounterCollectionRules.MissingReference(PaymentMethod.BankTransfer, null).Should().Be("Payment_TransferReferenceRequired");
        CounterCollectionRules.MissingReference(PaymentMethod.Cheque, "CHQ 0012345").Should().BeNull();
        CounterCollectionRules.MissingReference(PaymentMethod.Cash, null).Should().BeNull();
        CounterCollectionRules.MissingReference(PaymentMethod.Card, null).Should().BeNull();
        CounterCollectionRules.MissingReference(PaymentMethod.MobilePayment, null).Should().BeNull();
    }

    [Fact]
    public void A_deposit_is_an_amount_within_what_is_due()
    {
        CounterCollectionRules.Check(CollectionKind.Deposit, "cent", 250m, false, PaymentMethod.Cash, null, null)
            .Should().Be(new Application.Input.FieldRefusal("amount", "Result_NotANumber", Typed: "cent"));
        CounterCollectionRules.Check(CollectionKind.Deposit, "0", 250m, false, PaymentMethod.Cash, null, null)!
            .Key.Should().Be("Payment_AmountMustBePositive");
        CounterCollectionRules.Check(CollectionKind.Deposit, "300", 250m, false, PaymentMethod.Cash, null, null)
            .Should().Be(new Application.Input.FieldRefusal("amount", "Deposit_ExceedsOrderTotal", Amount: 250m));
        CounterCollectionRules.Check(CollectionKind.Deposit, "300", 250m, true, PaymentMethod.Cash, null, null)!
            .Key.Should().Be("Payment_ExceedsOutstanding", "on an invoice it is a part payment");
        CounterCollectionRules.Check(CollectionKind.Deposit, "100,50", 250m, false, PaymentMethod.Cash, null, null).Should().BeNull();
    }

    [Fact]
    public void Paying_everything_ignores_the_amount_box_but_not_the_reference()
    {
        CounterCollectionRules.Check(CollectionKind.Full, "n'importe", 250m, false, PaymentMethod.Card, null, null).Should().BeNull();
        CounterCollectionRules.Check(CollectionKind.Full, null, 250m, false, PaymentMethod.Cheque, null, null)
            .Should().Be(new Application.Input.FieldRefusal("reference", "Payment_ReferenceRequired"));
        CounterCollectionRules.Amount(CollectionKind.Full, "12", 250m).Should().Be(250m);
        CounterCollectionRules.Amount(CollectionKind.Deposit, "100,50", 250m).Should().Be(100.50m);
        CounterCollectionRules.Amount(CollectionKind.Deposit, "beaucoup", 250m).Should().BeNull();
    }

    [Fact]
    public void Cash_tendered_must_cover_the_amount_and_the_change_is_given_back()
    {
        CounterCollectionRules.Change(PaymentMethod.Cash, "300", 250m).Should().Be(50m);
        CounterCollectionRules.Change(PaymentMethod.Cash, "1 000,00", 672.30m).Should().Be(327.70m);
        CounterCollectionRules.Change(PaymentMethod.Cash, "250", 250m).Should().Be(0m);
        CounterCollectionRules.Change(PaymentMethod.Cash, "200", 250m).Should().BeNull("it does not cover it");
        CounterCollectionRules.Change(PaymentMethod.Cash, "", 250m).Should().BeNull();
        CounterCollectionRules.Change(PaymentMethod.Card, "300", 250m).Should().BeNull("only cash is handed back");

        CounterCollectionRules.Check(CollectionKind.Full, null, 250m, false, PaymentMethod.Cash, null, "200")
            .Should().Be(new Application.Input.FieldRefusal("tendered", "Collect_TenderedShort", Amount: 250m));
        CounterCollectionRules.Check(CollectionKind.Full, null, 250m, false, PaymentMethod.Cash, null, "deux cents")
            .Should().Be(new Application.Input.FieldRefusal("tendered", "Result_NotANumber", Typed: "deux cents"));
        CounterCollectionRules.Check(CollectionKind.Full, null, 250m, false, PaymentMethod.Cash, null, "")
            .Should().BeNull("left empty, the exact sum is handed over");
        CounterCollectionRules.Check(CollectionKind.Full, null, 250m, false, PaymentMethod.Card, null, "200")
            .Should().BeNull("a card takes no cash");
    }

    [Fact]
    public void The_till_is_offered_at_the_start_to_whoever_takes_payments()
    {
        CounterCollectionRules.OffersTillOpening(TillState.NotOpen, Cashier).Should().BeTrue();
        CounterCollectionRules.OffersTillOpening(TillState.Open, Cashier).Should().BeFalse();
        CounterCollectionRules.OffersTillOpening(TillState.OpenElsewhere, Cashier).Should().BeFalse("closed on the other site first");
        CounterCollectionRules.OffersTillOpening(TillState.NotOpen, Reception).Should().BeFalse();
    }

    [Fact]
    public void What_was_done_is_said_for_what_it_is()
    {
        CounterCollectionRules.DoneKey(Done(Guid.NewGuid(), issued: true)).Should().Be("Collect.DoneInvoiced");
        CounterCollectionRules.DoneKey(Done(Guid.NewGuid(), left: 30m, kind: CollectionKind.Deposit)).Should().Be("Collect.DonePartly");
        CounterCollectionRules.DoneKey(Done(Guid.NewGuid())).Should().Be("Collect.DonePaid");
        CounterCollectionRules.DoneKey(Done(deposit: "REC-2026-000031", left: 50m, kind: CollectionKind.Deposit)).Should().Be("Collect.DoneDeposit");
    }

    [Fact]
    public void The_queue_reads_its_states_and_its_total()
    {
        CounterCollectionRules.StateKey(OrderPaymentState.NotInvoiced).Should().Be("Collect.StateNotInvoiced");
        CounterCollectionRules.StateKey(OrderPaymentState.PatientShareDue).Should().Be("Collect.StateDue");

        var at = new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.FromHours(1));
        CounterCollectionRules.QueueTotal(
        [
            new(Guid.NewGuid(), "2610-000041", "ALAOUI Salma", at, OrderPaymentState.NotInvoiced, 250m, null, 0m),
            new(Guid.NewGuid(), "2610-000042", "TAZI Omar", at.AddMinutes(5), OrderPaymentState.PatientShareDue, 80.50m, "FAC-1", 20m)
        ]).Should().Be(330.50m);
    }

    [Fact]
    public void The_shortcuts_are_the_workstation_s()
    {
        CounterCollectionRules.FullShortcut.Should().Be("F9");
        CounterCollectionRules.DepositShortcut.Should().Be("F10");
    }

    [Fact]
    public void The_workstation_collects_from_every_counter_and_offers_the_till()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MarocLIS.sln"))) root = root.Parent;
        var desktop = Path.Combine(root!.FullName, "src", "MarocLIS.Desktop");
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([desktop, .. parts]));

        Read("Forms", "MainShellForm.cs").Should().Contain("[\"tocollect\"] = n => n.NavigateToAsync<CollectionQueueView>()");
        Read("Composition", "DesktopServiceCollectionExtensions.cs")
            .Should().Contain("AddTransient<CollectionQueueView>()").And.Contain("AddTransient<CounterCollectionFlow>()");

        // F9 and F10 in the dialog, the money through CounterCollectionService, the receipt to the viewer.
        var dialog = Read("Views", "CollectDialog.cs");
        dialog.Should().Contain("case Keys.F9:").And.Contain("case Keys.F10:")
              .And.Contain("_collection.CollectAsync(request)").And.Contain("_documents.OpenAsync(bytes, \"pdf\")")
              .And.Contain("CounterCollectionRules.DefaultMethod(lastMethod)").And.Contain("_billing.OpenCashSessionAsync(opening)")
              .And.NotContain("ILabelPrinter");

        // After « Enregistrer », by the rule; « Encaisser » on the file and at the report counter, by the right.
        Read("Views", "OrderRegistrationView.cs").Should().Contain("CounterCollectionRules.OpensAfterRegistration(_currentUser.Has")
                                                  .And.Contain("_collection.OfferTillAsync()");
        Read("Views", "OrderDetailView.cs").Should().Contain("_collect.Visible = _collection.CanCollect");
        Read("Views", "ReportRegisterView.cs").Should().Contain("_collection.Offers(SelectedPending?.Payment ?? Selected?.Payment)");
        Read("Views", "CollectionQueueView.cs").Should().Contain("PermissionGate.Require(Permissions.Billing.RecordPayment, _collect)");
    }
}
