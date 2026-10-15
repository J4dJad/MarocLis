using System.Reflection;
using System.Text;
using FluentAssertions;
using MarocLIS.Application.Treasury;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Treasury;
using MarocLIS.Infrastructure.Persistence;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Lot F, treasury: bank accounts, the CSV statement import, the reconciliation rules, the tills'
/// sign-off and the cash forecast.
/// </summary>
public sealed class FinanceTreasuryTests
{
    /// <summary>A RIB of 22 digits completed with the key that makes the whole number divisible by 97.</summary>
    private static string Rib(string first22)
    {
        var remainder = 0;
        foreach (var c in first22 + "00") remainder = (remainder * 10 + (c - '0')) % 97;
        var key = (97 - remainder) % 97;
        return first22 + key.ToString("00");
    }

    private static readonly Guid Site = Guid.NewGuid();

    // ---- Bank accounts ---------------------------------------------------------------------------

    [Fact]
    public void A_bank_account_needs_a_valid_rib_and_a_treasury_account_of_class_51()
    {
        var rib = Rib("0117800000012345678901");
        var account = new BankAccount(Site, "BMCE Bank", "Compte courant", $"{rib[..3]} {rib[3..6]} {rib[6..]}", "51410001");

        account.Rib.Should().Be(rib, "spaces are taken out of the RIB");
        account.Validate().IsSuccess.Should().BeTrue();

        account.ChangeRib(rib[..^1] + (rib[^1] == '9' ? '0' : (char)(rib[^1] + 1)));
        account.Validate().Errors.Select(e => e.ResourceKey).Should().Contain("BankAccount_RibInvalid");

        account.ChangeRib(rib);
        account.AccountingAccount = "4411";
        account.Validate().Errors.Select(e => e.ResourceKey).Should().Contain("BankAccount_AccountingAccountInvalid");

        BankAccount.IsValidAccountingAccount("5141").Should().BeTrue();
        BankAccount.IsValidAccountingAccount("514100012").Should().BeTrue();
        BankAccount.IsValidAccountingAccount("5141A").Should().BeFalse();
        BankAccount.IsValidAccountingAccount("514").Should().BeFalse();
    }

    // ---- Statement import ------------------------------------------------------------------------

    private const string FrenchStatement =
        "BMCE BANK - Relevé de compte\n" +
        "Compte : 011 780 0000012345678901 23\n" +
        "Date;Libellé;Référence;Débit;Crédit\n" +
        "02/10/2026;\"REMISE CB 01/10; TPE 4455\";;;\"1 250,00\"\n" +
        "03/10/2026;VIR CNSS BORDEREAU AMO-2026-0042;AMO-2026-0042;;18 400,50\n" +
        "05/10/2026;CHQ 7654321 BIOMED MAROC;7654321;-3 600,00;\n" +
        "05/10/2026;FRAIS TENUE COMPTE;;25,00;\n" +
        "05/10/2026;FRAIS TENUE COMPTE;;25,00;\n" +
        "06/10/2026;LIGNE ILLISIBLE;;abc;\n" +
        ";SOLDE AU 06/10/2026;;;\n";

    private static readonly BankStatementCsvMapping Mapping = new()
    {
        SkipLines = 2,
        HasHeader = true,
        DateColumn = 1,
        LabelColumn = 2,
        ReferenceColumn = 3,
        DebitColumn = 4,
        CreditColumn = 5
    };

    [Fact]
    public void A_french_statement_is_read_with_its_debit_and_credit_columns()
    {
        var lines = BankStatementCsv.Parse(FrenchStatement, Mapping);

        // The balance line has neither date nor amount: left out, not reported.
        lines.Should().HaveCount(6);

        lines[0].Label.Should().Be("REMISE CB 01/10; TPE 4455", "a quoted cell keeps its semicolon");
        lines[0].Amount.Should().Be(1250m);
        lines[1].Reference.Should().Be("AMO-2026-0042");
        lines[1].Amount.Should().Be(18400.50m);
        lines[2].Amount.Should().Be(-3600m, "a debit printed negative is still a debit");
        lines[3].Amount.Should().Be(-25m, "a debit printed positive is a debit");
        lines[3].OperationDate.Should().Be(new DateOnly(2026, 10, 5));

        lines[5].IsValid.Should().BeFalse();
        lines[5].ErrorKey.Should().Be("BankStatement_AmountUnreadable");
    }

    [Theory]
    [InlineData("1 234,56", ',', 1234.56)]
    [InlineData("1.234,56", ',', 1234.56)]
    [InlineData("-120,00", ',', -120)]
    [InlineData("(120,00)", ',', -120)]
    [InlineData("120,00-", ',', -120)]
    [InlineData("1,234.56", '.', 1234.56)]
    [InlineData("250,00 DH", ',', 250)]
    [InlineData("", ',', 0)]
    public void Amounts_are_read_as_banks_print_them(string text, char separator, double expected) =>
        BankStatementCsv.ParseAmount(text, separator).Should().Be((decimal)expected);

    [Fact]
    public void An_amount_with_letters_is_unreadable_and_dates_accept_the_usual_variants()
    {
        BankStatementCsv.ParseAmount("12a,00", ',').Should().BeNull();
        BankStatementCsv.ParseDate("05/10/26", "dd/MM/yyyy").Should().Be(new DateOnly(2026, 10, 5));
        BankStatementCsv.ParseDate("2026-10-05", "dd/MM/yyyy").Should().Be(new DateOnly(2026, 10, 5));
        BankStatementCsv.ParseDate("31/02/2026", "dd/MM/yyyy").Should().BeNull();
    }

    [Fact]
    public void A_windows_1252_export_keeps_its_accents_and_the_delimiter_is_detected()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1252).GetBytes("Date;Libellé;Montant\n01/10/2026;Dépôt espèces;5000,00\n");

        var text = BankStatementCsv.Decode(bytes);
        text.Should().Contain("Libellé");

        var lines = BankStatementCsv.Parse(text, new BankStatementCsvMapping { DateColumn = 1, LabelColumn = 2, AmountColumn = 3 });
        lines.Should().ContainSingle().Which.Label.Should().Be("Dépôt espèces");
        lines[0].Amount.Should().Be(5000m);
    }

    [Fact]
    public void Identical_lines_of_one_file_are_two_lines_and_a_second_import_finds_them_all()
    {
        var account = Guid.NewGuid();
        var parsed = BankStatementCsv.Parse(FrenchStatement, Mapping);

        var first = BankReconciliation.Fingerprints(account, parsed);
        first.Should().HaveCount(5, "the unreadable line has no fingerprint");
        first.Select(f => f.Fingerprint).Distinct().Should().HaveCount(5,
            "the two bank charges of the same day are two lines, not a duplicate");

        var again = BankReconciliation.Fingerprints(account, BankStatementCsv.Parse(FrenchStatement, Mapping));
        again.Select(f => f.Fingerprint).Should().Equal(first.Select(f => f.Fingerprint));

        BankReconciliation.Fingerprints(Guid.NewGuid(), parsed).Select(f => f.Fingerprint)
            .Should().NotIntersectWith(first.Select(f => f.Fingerprint), "the account is part of the fingerprint");
    }

    [Fact]
    public void The_mapping_round_trips_through_its_json_and_a_broken_one_falls_back()
    {
        BankStatementCsvMapping.FromJson(Mapping.ToJson()).Should().Be(Mapping);
        BankStatementCsvMapping.FromJson("{not json").Should().Be(BankStatementCsvMapping.Default);
        new BankStatementCsvMapping { DebitColumn = 0, AmountColumn = 0 }.Validate().IsFailure.Should().BeTrue();
    }

    // ---- Reconciliation --------------------------------------------------------------------------

    private static StatementLineRow Line(int day, decimal amount, string label, string? reference = null) =>
        new(Guid.NewGuid(), new DateOnly(2026, 10, day), null, label, reference, amount, 0m, 0, false, [], null);

    private static TreasuryMovement Movement(TreasuryMovementKind kind, int day, decimal amount,
                                             string? reference = null, bool late = false) =>
        new(kind, Guid.NewGuid().ToString("N"), new DateOnly(2026, 10, day), amount, "x", reference, late);

    [Fact]
    public void A_reference_on_the_statement_settles_which_of_two_equal_cheques_it_is()
    {
        var line = Line(6, 800m, "REMISE CHQ 0012345");
        var right = Movement(TreasuryMovementKind.PatientPayment, 4, 800m, "0012345");
        var other = Movement(TreasuryMovementKind.PatientPayment, 5, 800m, "0099999");

        var proposals = BankReconciliation.ProposeMatches([line], [other, right]);

        proposals.Should().ContainSingle().Which.Movement.Should().Be(right);
        proposals[0].ByReference.Should().BeTrue();
    }

    [Fact]
    public void Without_a_reference_only_a_unique_pair_is_matched()
    {
        var card = Line(3, 1250m, "REMISE CB");
        var batch = Movement(TreasuryMovementKind.CardBatch, 2, 1250m);

        var feeA = Line(5, -25m, "FRAIS");
        var feeB = Line(5, -25m, "FRAIS");
        var payment = Movement(TreasuryMovementKind.SupplierPayment, 5, -25m);

        var proposals = BankReconciliation.ProposeMatches([card, feeA, feeB], [batch, payment]);

        proposals.Should().ContainSingle().Which.StatementLineId.Should().Be(card.Id,
            "two identical lines for one movement is a choice for the operator");
    }

    [Fact]
    public void The_date_window_and_the_direction_bound_the_automatic_match()
    {
        var transfer = Movement(TreasuryMovementKind.PayerSettlement, 1, 500m);
        BankReconciliation.ProposeMatches([Line(10, 500m, "VIR")], [transfer]).Should().BeEmpty("nine days is too late");
        BankReconciliation.ProposeMatches([Line(5, 500m, "VIR")], [transfer]).Should().ContainSingle();
        BankReconciliation.ProposeMatches([Line(2, -500m, "VIR")], [transfer]).Should().BeEmpty();

        // A cheque written to a supplier may be cashed weeks later.
        var cheque = Movement(TreasuryMovementKind.SupplierPayment, 1, -3600m, late: true);
        BankReconciliation.ProposeMatches([Line(30, -3600m, "CHQ")], [cheque]).Should().ContainSingle();
    }

    [Fact]
    public void A_line_matched_already_is_not_proposed_again_and_a_short_reference_is_ignored()
    {
        var matched = Line(3, 100m, "VIR 12") with { MatchCount = 1, MatchedAmountMad = 100m };
        BankReconciliation.ProposeMatches([matched], [Movement(TreasuryMovementKind.PatientPayment, 3, 100m)])
            .Should().BeEmpty();

        BankReconciliation.CarriesReference(Line(3, 100m, "VIR 12"), "12").Should().BeFalse();
        BankReconciliation.CarriesReference(Line(3, 100m, "CHQ N° 1234567"), "1234567").Should().BeTrue();
    }

    [Fact]
    public void The_reconciled_balance_counts_only_the_lines_explained()
    {
        var opening = new DateOnly(2026, 9, 30);
        var lines = new[]
        {
            Line(1, 1000m, "A") with { MatchCount = 1, MatchedAmountMad = 1000m },
            Line(2, -200m, "B"),
            Line(3, 300m, "C") with { MatchCount = 1, MatchedAmountMad = 300m }
        };
        var pending = new[] { Movement(TreasuryMovementKind.SupplierPayment, 3, -450m) };

        var s = BankReconciliation.Summarise(5000m, opening, lines, pending);

        s.StatementBalanceMad.Should().Be(6100m);
        s.ReconciledBalanceMad.Should().Be(6300m);
        s.DifferenceMad.Should().Be(-200m);
        s.UnmatchedStatementMad.Should().Be(-200m);
        s.UnmatchedMovementsMad.Should().Be(-450m);
        s.AdjustedBalanceMad.Should().Be(5850m);
    }

    [Fact]
    public void Unmatching_closes_the_match_once()
    {
        var match = new BankMatch(Site, Guid.NewGuid(), Guid.NewGuid(), TreasuryMovementKind.CardBatch, "2026-10-02",
                                  new DateOnly(2026, 10, 2), 1250m, true, null, DateTimeOffset.Now, Guid.NewGuid());

        match.IsActive.Should().BeTrue();
        match.Unmatch(DateTimeOffset.Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        match.IsActive.Should().BeFalse();
        match.Unmatch(DateTimeOffset.Now, Guid.NewGuid()).IsFailure.Should().BeTrue();
    }

    // ---- Cash session sign-off -------------------------------------------------------------------

    [Fact]
    public void A_till_is_signed_off_closed_by_someone_else_and_with_a_comment_when_it_did_not_balance()
    {
        var operatorId = Guid.NewGuid();
        var supervisor = Guid.NewGuid();
        var session = new CashSession(Site, operatorId, DateTimeOffset.Now.AddHours(-8), 500m);
        session.Accumulate(PaymentMethod.Cash, 1200m);

        session.Reconcile(DateTimeOffset.Now, supervisor, "ok").Errors.Single().ResourceKey.Should().Be("CashSession_NotClosed");

        session.Close(DateTimeOffset.Now, operatorId, 1695m, "erreur de rendu de monnaie").IsSuccess.Should().BeTrue();
        session.Variance.Should().Be(-5m);

        session.Reconcile(DateTimeOffset.Now, operatorId, "vu").Errors.Single().ResourceKey
            .Should().Be("CashSession_ReconcilerIsOperator");
        session.Reconcile(DateTimeOffset.Now, supervisor, " ").Errors.Single().ResourceKey
            .Should().Be("CashSession_ReconciliationCommentRequired");

        session.Reconcile(DateTimeOffset.Now, supervisor, "Écart accepté, rendu de monnaie").IsSuccess.Should().BeTrue();
        session.Status.Should().Be(CashSessionStatus.Reconciled);
        session.ReconciledByUserId.Should().Be(supervisor);
        session.ReconciliationComment.Should().Be("Écart accepté, rendu de monnaie");

        session.Reconcile(DateTimeOffset.Now, supervisor, "encore").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void A_balanced_till_needs_no_comment()
    {
        var operatorId = Guid.NewGuid();
        var session = new CashSession(Site, operatorId, DateTimeOffset.Now.AddHours(-8), 200m);
        session.Close(DateTimeOffset.Now, operatorId, 200m, null);

        session.Reconcile(DateTimeOffset.Now, Guid.NewGuid(), null).IsSuccess.Should().BeTrue();
        session.ReconciliationComment.Should().BeNull();
    }

    // ---- Forecast --------------------------------------------------------------------------------

    [Fact]
    public void A_monthly_item_on_the_31st_falls_on_the_last_day_of_short_months_without_drifting()
    {
        var rent = new TreasuryForecastItem(Site, "Loyer", -12000m, ForecastRecurrence.Monthly, new DateOnly(2026, 1, 31));

        rent.Occurrences(new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 30)).Should().Equal(
            new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 31), new DateOnly(2026, 4, 30));

        rent.EndDate = new DateOnly(2026, 3, 1);
        rent.Occurrences(new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 30)).Should().HaveCount(2);

        rent.IsActive = false;
        rent.Occurrences(new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 30)).Should().BeEmpty();

        var once = new TreasuryForecastItem(Site, "Dividende", -50000m, ForecastRecurrence.Once, new DateOnly(2026, 11, 15));
        once.Occurrences(new DateOnly(2026, 10, 15), new DateOnly(2027, 1, 12)).Should().ContainSingle();
        once.Occurrences(new DateOnly(2026, 11, 16), new DateOnly(2027, 1, 12)).Should().BeEmpty();

        new TreasuryForecastItem(Site, "", 0m, ForecastRecurrence.Once, new DateOnly(2026, 1, 1)).Validate()
            .Errors.Select(e => e.ResourceKey).Should().BeEquivalentTo(["TreasuryItem_LabelRequired", "TreasuryItem_AmountRequired"]);
    }

    [Fact]
    public void A_payer_is_expected_at_its_median_delay_once_it_has_a_history_and_at_its_terms_before()
    {
        CashForecastCalculator.ExpectedDelayDays([45, 50], 60).Should().Be(60, "two batches are not a history");
        CashForecastCalculator.ExpectedDelayDays([45, 50, 190], 60).Should().Be(50, "one lost batch does not move the median");
        CashForecastCalculator.ExpectedDelayDays([40, 50, 60, 70], 60).Should().Be(55);
    }

    [Fact]
    public void Pay_days_fall_on_the_chosen_day_or_the_last_day_of_the_month()
    {
        CashForecastCalculator.PayDays(new DateOnly(2026, 10, 15), new DateOnly(2027, 1, 12), 0)
            .Should().Equal(new DateOnly(2026, 10, 31), new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 31));

        CashForecastCalculator.PayDays(new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 28), 30)
            .Should().Equal(new DateOnly(2027, 2, 28));
    }

    [Fact]
    public void Each_week_opens_on_the_balance_the_previous_one_closed_on()
    {
        var from = new DateOnly(2026, 10, 15);
        ForecastFlow[] flows =
        [
            new(from, 10_000m, ForecastCategory.PayerReceipts, "CNSS"),
            new(from.AddDays(6), -4_000m, ForecastCategory.SupplierPayments, "Biomed"),
            new(from.AddDays(7), -30_000m, ForecastCategory.Payroll, "Paie"),
            new(from.AddDays(20), 1_000m, ForecastCategory.Other, "Divers"),
            new(from.AddDays(99), 1_000_000m, ForecastCategory.Other, "Hors horizon")
        ];

        var weeks = CashForecastCalculator.BuildWeeks(from, 13, 20_000m, flows);

        weeks.Should().HaveCount(13);
        weeks[0].NetMad.Should().Be(6_000m);
        weeks[0].ClosingMad.Should().Be(26_000m);
        weeks[1].OpeningMad.Should().Be(26_000m);
        weeks[1].PayrollMad.Should().Be(-30_000m);
        weeks[1].ClosingMad.Should().Be(-4_000m);
        weeks[2].OtherMad.Should().Be(1_000m);
        weeks[12].End.Should().Be(from.AddDays(90));
        weeks[12].ClosingMad.Should().Be(-3_000m);

        CashForecastCalculator.WeeklyAverage(56_000m, 56).Should().Be(7_000m);
    }

    // ---- Permissions -----------------------------------------------------------------------------

    [Fact]
    public void Treasury_and_the_till_sign_off_reach_the_direction_and_accounting_roles_only()
    {
        Permissions.All.Should().Contain([Permissions.Treasury.View, Permissions.Treasury.Manage,
                                          Permissions.Billing.ReconcileCashSession]);

        foreach (var role in new[] { SystemRoles.Administrator, SystemRoles.ResponsibleBiologist, SystemRoles.Accountant })
            ShippedRole(role).Should().Contain([Permissions.Treasury.View, Permissions.Treasury.Manage,
                                                Permissions.Billing.ReconcileCashSession], role);

        foreach (var role in new[] { SystemRoles.Receptionist, SystemRoles.Technician, SystemRoles.Biologist,
                                     SystemRoles.QualityManager, SystemRoles.Prescriber, SystemRoles.Phlebotomist })
            ShippedRole(role).Should().NotContain([Permissions.Treasury.View, Permissions.Treasury.Manage,
                                                   Permissions.Billing.ReconcileCashSession], role);
    }

    private static string[] ShippedRole(string roleCode)
    {
        var type = typeof(LisDbContext).Assembly.GetType("MarocLIS.Infrastructure.Persistence.RolePermissionSets")!;
        var definitions = (System.Collections.IEnumerable)type
            .GetMethod("Build", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

        foreach (var definition in definitions)
        {
            var tuple = definition.GetType();
            if ((string)tuple.GetField("Item1")!.GetValue(definition)! == roleCode)
                return (string[])tuple.GetField("Item3")!.GetValue(definition)!;
        }

        throw new InvalidOperationException(roleCode);
    }
}
