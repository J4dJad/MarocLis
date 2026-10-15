using FluentAssertions;
using MarocLIS.Application.Treasury;
using MarocLIS.Domain.Inventory;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Review 7 (finance cycle 2), treasury: a supplier bill always carries its due date, and the
/// forecast places a closed payroll on the pay day it uses.
/// </summary>
public sealed class ReviewFixesFinanceTreasuryCycle2Tests
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static SupplierInvoice Approved()
    {
        var invoice = new SupplierInvoice(Guid.NewGuid(), Guid.NewGuid(), "FA-1", Today.AddDays(-10), Today.AddDays(-9),
                                          Guid.NewGuid());
        invoice.AddLine(new SupplierInvoiceLine(Guid.NewGuid(), Guid.NewGuid(), "Réactif", 1m, 10_000m, 0m)).IsSuccess.Should().BeTrue();
        invoice.ApplyMatch([]).IsSuccess.Should().BeTrue();
        return invoice;
    }

    // ---- T1 (cycle 2): an LCN without its due date is refused ------------------------------------

    [Fact]
    public void T1_a_bill_of_exchange_is_refused_without_its_due_date()
    {
        var invoice = Approved();

        invoice.RecordPayment(new SupplierPayment(Today, 4_000m, SupplierPaymentMethod.BillOfExchange, "LCN-1",
                                                  DateTimeOffset.Now, Guid.NewGuid()))
               .FirstError.ResourceKey.Should().Be("SupplierPayment_MaturityRequired");

        invoice.RecordPayment(new SupplierPayment(Today, 4_000m, SupplierPaymentMethod.BillOfExchange, "LCN-1",
                                                  DateTimeOffset.Now, Guid.NewGuid(), Today.AddDays(60)))
               .IsSuccess.Should().BeTrue();

        // A cheque debited on the day still needs none.
        invoice.RecordPayment(new SupplierPayment(Today, 1_000m, SupplierPaymentMethod.Cheque, "CHQ-1",
                                                  DateTimeOffset.Now, Guid.NewGuid()))
               .IsSuccess.Should().BeTrue();
    }

    // ---- T11: the payroll on the forecast's pay day -----------------------------------------------

    [Theory]
    [InlineData(0, "2026-10-30", "2026-10-31")]   // month end
    [InlineData(5, "2026-10-30", "2026-11-05")]   // early in the next month
    [InlineData(25, "2026-10-24", "2026-10-25")]  // in the month itself
    [InlineData(31, "2026-10-24", "2026-10-31")]  // clamped to the month's last day
    [InlineData(0, "2026-11-03", "2026-11-03")]   // closed late: paid on closing
    [InlineData(5, "2026-11-08", "2026-11-08")]
    public void T11_a_closed_payroll_is_paid_on_the_pay_day_the_forecast_uses(int payrollDay, string closed, string expected)
    {
        var payDay = CashForecastCalculator.PayDayOf(new DateOnly(2026, 10, 1), DateOnly.Parse(closed), payrollDay);

        payDay.Should().Be(DateOnly.Parse(expected));
        if (payDay.ToString("yyyy-MM-dd") != closed)
            CashForecastCalculator.PayDays(payDay, payDay, payrollDay).Should().ContainSingle(
                "a payroll paid on time falls on one of the forecast's pay days, so it is counted once");
    }
}
