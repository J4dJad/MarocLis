using FluentAssertions;
using MarocLIS.Domain.Billing;
using Xunit;

namespace MarocLIS.Tests.Domain;

public sealed class DepositTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.FromHours(1));

    private static Deposit Cash(decimal amount) =>
        new("R-2026-000101", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), amount,
            PaymentMethod.Cash, Now, Guid.NewGuid()) { CashSessionId = Guid.NewGuid() };

    [Fact]
    public void Partly_applied_deposit_stays_held_for_the_rest_then_refund_closes_it()
    {
        var deposit = Cash(500m);

        deposit.ApplyTo(Guid.NewGuid(), 320m, Now).IsSuccess.Should().BeTrue();
        deposit.Status.Should().Be(DepositStatus.Held);
        deposit.Remaining.Should().Be(180m);

        deposit.Refund(Now, "Facture inférieure à l'acompte").IsSuccess.Should().BeTrue();
        deposit.RefundedAmount.Should().Be(180m);
        deposit.Remaining.Should().Be(0m);
        deposit.Status.Should().Be(DepositStatus.Applied);
    }

    [Fact]
    public void Fully_applied_deposit_cannot_be_applied_or_refunded_again()
    {
        var deposit = Cash(200m);

        deposit.ApplyTo(Guid.NewGuid(), 200m, Now).IsSuccess.Should().BeTrue();
        deposit.Status.Should().Be(DepositStatus.Applied);

        deposit.ApplyTo(Guid.NewGuid(), 1m, Now).Errors.Should().Contain(e => e.ResourceKey == "Deposit_NotHeld");
        deposit.Refund(Now, "x").Errors.Should().Contain(e => e.ResourceKey == "Deposit_NotHeld");
    }

    [Fact]
    public void Cannot_apply_more_than_what_is_left()
    {
        var deposit = Cash(100m);

        deposit.ApplyTo(Guid.NewGuid(), 150m, Now).Errors
            .Should().Contain(e => e.ResourceKey == "Deposit_ApplyAmountInvalid");
    }

    [Fact]
    public void Untouched_deposit_refund_requires_a_reason_and_marks_it_refunded()
    {
        var deposit = Cash(300m);

        deposit.Refund(Now, " ").IsFailure.Should().BeTrue();
        deposit.Refund(Now, "Dossier annulé").IsSuccess.Should().BeTrue();
        deposit.Status.Should().Be(DepositStatus.Refunded);
    }

    [Fact]
    public void Validation_rejects_third_party_method_and_cash_without_till()
    {
        var thirdParty = new Deposit("R-1", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100m,
                                     PaymentMethod.ThirdPartyPayer, Now, Guid.NewGuid());
        thirdParty.Validate().Errors.Should().Contain(e => e.ResourceKey == "Deposit_MethodNotAllowed");

        var cash = new Deposit("R-2", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100m,
                               PaymentMethod.Cash, Now, Guid.NewGuid());
        cash.Validate().Errors.Should().Contain(e => e.ResourceKey == "Payment_CashRequiresOpenSession");

        Cash(100m).Validate().IsSuccess.Should().BeTrue();
    }
}
