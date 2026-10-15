using System.Text;
using FluentAssertions;
using MarocLIS.Application.Treasury;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Treasury;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Review 6 (finance), treasury: supplier bills dated on their due date, the statement import's
/// decimal separator, duplicate check and encodings, text lengths, and till withdrawals kept one
/// by one.
/// </summary>
public sealed class ReviewFixesFinanceTreasuryTests
{
    // ---- T1: a supplier bill reaches the bank on its due date ------------------------------------

    [Fact]
    public void T1_a_supplier_bill_is_banked_on_its_maturity_and_a_transfer_on_its_payment_day()
    {
        var paid = new DateOnly(2026, 8, 1);
        var due = new DateOnly(2026, 10, 30);

        var bill = new SupplierPayment(paid, 48_000m, SupplierPaymentMethod.BillOfExchange, "LCN-1", DateTimeOffset.Now,
                                       Guid.NewGuid(), due);
        bill.MaturityDate.Should().Be(due);
        bill.BankDate.Should().Be(due);

        var transfer = new SupplierPayment(paid, 1_000m, SupplierPaymentMethod.Transfer, "VIR-1", DateTimeOffset.Now,
                                           Guid.NewGuid(), due);
        transfer.MaturityDate.Should().BeNull("a transfer leaves the account on the day");
        transfer.BankDate.Should().Be(paid);
    }

    // ---- T4: the decimal separator is never guessed into a hundredfold amount --------------------

    [Theory]
    [InlineData("1500.00", ',')]
    [InlineData("-1500.00", ',')]
    [InlineData("1,500.00", ',')]
    [InlineData("1500,00", '.')]
    [InlineData("12.5", ',')]
    public void T4_a_thousands_separator_followed_by_cents_is_unreadable_rather_than_multiplied(string text, char separator) =>
        BankStatementCsv.ParseAmount(text, separator).Should().BeNull();

    [Theory]
    [InlineData("1.500,00", ',', 1500)]
    [InlineData("1.234.567,89", ',', 1234567.89)]
    [InlineData("1,500.00", '.', 1500)]
    [InlineData("1,500", '.', 1500)]
    public void T4_genuine_thousands_separators_still_read(string text, char separator, double expected) =>
        BankStatementCsv.ParseAmount(text, separator).Should().Be((decimal)expected);

    [Fact]
    public void T4_the_default_mapping_reads_the_decimal_separator_from_the_file()
    {
        BankStatementCsvMapping.Default.DecimalSeparator.Should().Be('\0');
        var mapping = new BankStatementCsvMapping { DateColumn = 1, LabelColumn = 2, AmountColumn = 3 };

        var dots = BankStatementCsv.Parse("Date;Libellé;Montant\n01/10/2026;CHQ 1;-1500.00\n02/10/2026;VIR;1,250.50\n", mapping);
        dots.Select(l => l.Amount).Should().Equal(-1500m, 1250.50m);

        var commas = BankStatementCsv.Parse("Date;Libellé;Montant\n01/10/2026;CHQ 1;-1 500,00\n02/10/2026;VIR;1.250,50\n", mapping);
        commas.Select(l => l.Amount).Should().Equal(-1500m, 1250.50m);

        BankStatementCsv.DetectDecimalSeparator(["1500", "20"]).Should().Be(',', "nothing tells, the French layout");
    }

    // ---- T5: an overlapping statement read with another layout brings nothing in twice ----------

    private static ParsedStatementLine Parsed(int number, int day, decimal amount, string label, string? reference = null) =>
        new(number, new DateOnly(2026, 10, day), null, label, reference, amount, null, []);

    private static ExistingStatementLine Existing(int day, decimal amount, string label, string? reference = null,
                                                  bool deleted = false, string? fingerprint = null) =>
        new(new DateOnly(2026, 10, day), amount, label, reference, fingerprint ?? Guid.NewGuid().ToString("N"), deleted);

    [Fact]
    public void T5_the_same_lines_with_a_reference_column_and_full_labels_are_duplicates()
    {
        var account = Guid.NewGuid();
        // Imported on the 15th from the web banking: no reference, labels cut.
        var existing = new[]
        {
            Existing(3, 18_400.50m, "VIR CNSS BORDEREAU"),
            Existing(5, -25m, "FRAIS TENUE"),
            Existing(5, -25m, "FRAIS TENUE")
        };

        // The month's statement: a reference column, full labels, and a new line after the 15th.
        var lines = new[]
        {
            Parsed(1, 3, 18_400.50m, "VIR CNSS BORDEREAU AMO-2026-0042", "AMO-2026-0042"),
            Parsed(2, 5, -25m, "FRAIS TENUE COMPTE"),
            Parsed(3, 5, -25m, "FRAIS TENUE COMPTE"),
            Parsed(4, 20, -3_600m, "CHQ 7654321", "7654321")
        };

        var outcome = BankReconciliation.Deduplicate(account, lines, existing);

        outcome.Where(o => o.IsDuplicate).Select(o => o.Line.LineNumber).Should().Equal(1, 2, 3);
        outcome.Single(o => !o.IsDuplicate).Fingerprint.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void T5_a_label_from_another_column_is_still_a_duplicate_by_date_and_amount_count()
    {
        var existing = new[] { Existing(5, -25m, "COMMISSION") };
        var lines = new[] { Parsed(1, 5, -25m, "FRAIS"), Parsed(2, 5, -25m, "FRAIS") };

        var outcome = BankReconciliation.Deduplicate(Guid.NewGuid(), lines, existing);

        outcome.Count(o => o.IsDuplicate).Should().Be(1, "the account has one such line, the file two");
        outcome.Count(o => !o.IsDuplicate).Should().Be(1);
    }

    [Fact]
    public void T5_a_withdrawn_line_is_not_a_duplicate_but_keeps_its_fingerprint()
    {
        var account = Guid.NewGuid();
        var day = new DateOnly(2026, 10, 5);
        var taken = BankStatementLine.ComputeFingerprint(account, day, -25m, 0);
        var existing = new[] { Existing(5, -25m, "FRAIS", deleted: true, fingerprint: taken) };

        var outcome = BankReconciliation.Deduplicate(account, [Parsed(1, 5, -25m, "FRAIS")], existing);

        outcome.Single().IsDuplicate.Should().BeFalse();
        outcome.Single().Fingerprint.Should().Be(BankStatementLine.ComputeFingerprint(account, day, -25m, 1));
    }

    // ---- T8 / S4: texts longer than their columns ------------------------------------------------

    [Fact]
    public void T8_a_long_label_reference_and_file_name_are_cut_to_their_columns()
    {
        var line = new BankStatementLine(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 1), null,
                                         new string('L', 320), new string('R', 150), 10m, "fp")
        {
            SourceFileName = new string('f', 250) + ".csv"
        };

        line.Label.Should().HaveLength(BankStatementLine.LabelMaxLength);
        line.Reference.Should().HaveLength(BankStatementLine.ReferenceMaxLength);
        line.SourceFileName.Should().HaveLength(BankStatementLine.SourceFileNameMaxLength);
    }

    [Fact]
    public void S4_names_labels_and_sign_off_comments_are_bounded_by_their_columns()
    {
        var account = new BankAccount(Guid.NewGuid(), new string('B', 121), "Compte", "011780000001234567890123", "5141");
        account.Validate().Errors.Select(e => e.ResourceKey).Should().Contain("Treasury_TextTooLong");

        var item = new TreasuryForecastItem(Guid.NewGuid(), new string('x', 201), -1000m, ForecastRecurrence.Monthly,
                                            new DateOnly(2026, 10, 1));
        item.Validate().Errors.Select(e => e.ResourceKey).Should().Contain("Treasury_TextTooLong");

        var operatorId = Guid.NewGuid();
        var session = new CashSession(Guid.NewGuid(), operatorId, DateTimeOffset.Now.AddHours(-8), 0m);
        session.Close(DateTimeOffset.Now, operatorId, 0m, null).IsSuccess.Should().BeTrue();
        session.Reconcile(DateTimeOffset.Now, Guid.NewGuid(), new string('c', 1001))
               .FirstError.ResourceKey.Should().Be("Treasury_TextTooLong");
        session.Reconcile(DateTimeOffset.Now, Guid.NewGuid(), "Vu").IsSuccess.Should().BeTrue();
    }

    // ---- T9: a UTF-16 export is decoded ------------------------------------------------------------

    [Fact]
    public void T9_a_utf16_export_with_its_byte_order_mark_is_decoded()
    {
        const string csv = "Date;Libellé;Montant\n01/10/2026;Dépôt espèces;5000,00\n";
        var mapping = new BankStatementCsvMapping { DateColumn = 1, LabelColumn = 2, AmountColumn = 3 };

        foreach (var encoding in new Encoding[] { Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(csv)];
            var lines = BankStatementCsv.Parse(BankStatementCsv.Decode(bytes), mapping);

            lines.Should().ContainSingle().Which.IsValid.Should().BeTrue();
            lines[0].Label.Should().Be("Dépôt espèces");
            lines[0].Amount.Should().Be(5000m);
        }
    }

    // ---- T6: each withdrawal of a shift is kept on its own ----------------------------------------

    [Fact]
    public void T6_each_withdrawal_is_kept_with_its_time_and_the_total_still_counts()
    {
        var session = new CashSession(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now.AddHours(-8), 500m);
        session.RecordWithdrawal(5_000m, DateTimeOffset.Now.AddHours(-5), "Versement 1");
        session.RecordWithdrawal(8_000m, DateTimeOffset.Now.AddHours(-1), "Versement 2");

        session.CashWithdrawals.Should().Be(13_000m);
        session.Withdrawals.Select(w => w.Amount).Should().Equal(5_000m, 8_000m);
        session.Withdrawals.Should().OnlyContain(w => w.CashSessionId == session.Id);
    }
}
