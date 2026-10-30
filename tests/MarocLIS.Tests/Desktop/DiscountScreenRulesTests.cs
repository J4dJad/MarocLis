using System.Globalization;
using System.Text;
using FluentAssertions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of the discount screens, shared by the workstation and the web: « Remise… » at the
/// counter, « Profils de remise », the negotiated prices of a convention and « Remises accordées ».
/// </summary>
public sealed class DiscountScreenRulesTests
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly Guid Staff = Guid.NewGuid();
    private static readonly Guid Student = Guid.NewGuid();

    private static readonly IReadOnlyList<DiscountProfileRow> Profiles =
    [
        new(Staff, "Personnel", 50m, true, null, null, true),
        new(Student, "Étudiant", 10m, true, new DateOnly(2026, 9, 1), new DateOnly(2027, 6, 30), true)
    ];

    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : key + "(" + string.Join("|", args) + ")";

    private static string Dh(decimal value) => value.ToString("N2", Fr) + " DH";

    private static Func<string, bool> Holding(params string[] rights) => rights.Contains;

    private static DiscountReportRow Row(DiscountSource source, decimal amount, decimal gross, string? approvedBy = null,
                                         string reference = "2610-000021", string patient = "ALAOUI Fatima Zahra",
                                         string? reason = "Personnel du laboratoire", decimal? percent = 10m) =>
        new(Guid.NewGuid(), new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.FromHours(1)), false, reference, patient, "P-000042",
            source, source == DiscountSource.Convention ? "Clinique Al Amal" : null, percent, amount, gross, reason, "Salma", approvedBy);

    [Fact]
    public void The_controls_follow_the_rights()
    {
        DiscountScreenRules.CanGrant(Holding(Permissions.Billing.ApplyDiscount)).Should().BeTrue();
        DiscountScreenRules.CanGrant(Holding(Permissions.Billing.View)).Should().BeFalse("« Remise… » is hidden or greyed without billing.discount");

        DiscountScreenRules.CanManage(Holding(Permissions.Billing.ManageTariffs)).Should().BeTrue();
        DiscountScreenRules.CanManage(Holding(Permissions.Billing.ApplyDiscount)).Should().BeFalse();

        DiscountScreenRules.CanSeePrices(Holding(Permissions.Relations.View)).Should().BeTrue();
        DiscountScreenRules.CanSeePrices(Holding(Permissions.Billing.ApplyDiscount)).Should().BeFalse();

        DiscountScreenRules.Actions(Holding(Permissions.Steering.View), null).Should().Be(new DiscountReportActions(true, false));
        DiscountScreenRules.Actions(Holding(Permissions.Billing.ApproveDiscount), [Row(DiscountSource.Manual, 10m, 100m)])
            .Should().Be(new DiscountReportActions(true, true));
        DiscountScreenRules.Actions(Holding(Permissions.Billing.ApplyDiscount), [Row(DiscountSource.Manual, 10m, 100m)])
            .Should().Be(new DiscountReportActions(false, false));
    }

    [Fact]
    public void The_counters_total_says_the_source_and_the_rate()
    {
        var convention = new AppliedDiscount(DiscountSource.Convention, 10m, "Clinique Al Amal", Money.Of(30m), Guid.NewGuid(),
                                             "Clinique Al Amal", false, Money.Of(30m));
        DiscountScreenRules.CounterLine(convention, Text, Fr)
            .Should().Be("Discounts.CounterLine(Discounts.Source.Convention Clinique Al Amal (10 %))");

        var negotiated = convention with { Percent = null };
        DiscountScreenRules.CounterLine(negotiated, Text, Fr)
            .Should().Be("Discounts.CounterLine(Discounts.Source.Convention Clinique Al Amal (Discounts.NegotiatedPrices))");

        var profile = new AppliedDiscount(DiscountSource.Profile, 50m, "Personnel", Money.Of(150m), null, null, true, Money.Of(30m));
        DiscountScreenRules.CounterLine(profile, Text, Fr)
            .Should().Be("Discounts.CounterLine(Discounts.Source.Profile Personnel (50 %)) · Discounts.ApprovalRequired");

        DiscountScreenRules.CounterLine(null, Text, Fr).Should().BeNull();
        DiscountScreenRules.CounterLine(profile with { Amount = Money.Zero }, Text, Fr).Should().BeNull();
    }

    [Fact]
    public void The_counters_dialog_asks_a_reason_and_beyond_the_ceiling_an_approver()
    {
        var cap = DiscountCapSettings.Default; // 10 %

        var (none, noRefusal) = DiscountScreenRules.ReadCounter(DiscountScreenRules.AutomaticConvention, "", Profiles, null,
                                                                 null, null, null, 300m, cap);
        noRefusal.Should().BeNull();
        none.Should().Be(CounterDiscount.None);
        none!.IsEmpty.Should().BeTrue();

        DiscountScreenRules.ReadCounter("auto", Staff.ToString(), Profiles, null, " ", null, null, 300m, cap)
            .Refusal.Should().Be(new Application.Input.FieldRefusal(DiscountScreenRules.CounterReasonField, "Order_DiscountReasonRequired"));

        DiscountScreenRules.ReadCounter("auto", DiscountScreenRules.ManualProfile, Profiles, "120", "Geste", null, null, 300m, cap)
            .Refusal!.Field.Should().Be(DiscountScreenRules.CounterPercentField);

        DiscountScreenRules.ReadCounter("auto", Staff.ToString(), Profiles, null, "Personnel", null, null, 300m, cap)
            .Refusal.Should().Be(new Application.Input.FieldRefusal(DiscountScreenRules.ApproverField, "Approval_CredentialsRequired"));
        DiscountScreenRules.ReadCounter("auto", Staff.ToString(), Profiles, null, "Personnel", "dr.bennani", "", 300m, cap)
            .Refusal!.Field.Should().Be(DiscountScreenRules.ApproverPasswordField);

        var (approved, _) = DiscountScreenRules.ReadCounter("auto", Staff.ToString(), Profiles, null, " Personnel ", " dr.bennani ",
                                                            "secret", 300m, cap);
        approved.Should().Be(new CounterDiscount(null, false, Staff, null, "Personnel", new DiscountApproval("dr.bennani", "secret")));

        var (typed, _) = DiscountScreenRules.ReadCounter(DiscountScreenRules.NoConvention, DiscountScreenRules.ManualProfile, Profiles,
                                                         "7,5 %", "Fidélité", null, null, 300m, cap);
        typed.Should().Be(new CounterDiscount(null, true, null, 7.5m, "Fidélité", null), "7,5 % of 300 stays within the ceiling");

        var convention = Guid.NewGuid();
        var (chosen, _) = DiscountScreenRules.ReadCounter(convention.ToString(), "", Profiles, null, null, null, null, 300m, cap);
        chosen!.PartnershipId.Should().Be(convention);
        chosen.GrantsDiscount.Should().BeFalse();
        DiscountScreenRules.ConventionChoice(chosen).Should().Be(convention.ToString());
        DiscountScreenRules.ConventionChoice(typed!).Should().Be(DiscountScreenRules.NoConvention);
        DiscountScreenRules.ProfileChoice(typed!).Should().Be(DiscountScreenRules.ManualProfile);
        DiscountScreenRules.ProfileChoice(approved!).Should().Be(Staff.ToString());

        DiscountScreenRules.CounterPercent(Staff.ToString(), Profiles, null).Should().Be(50m);
        DiscountScreenRules.CounterPercent(DiscountScreenRules.ManualProfile, Profiles, "12").Should().Be(12m);
        DiscountScreenRules.NeedsApproval(10m, 300m, cap).Should().BeFalse();
        DiscountScreenRules.NeedsApproval(10.5m, 300m, cap).Should().BeTrue();
        DiscountScreenRules.ProfileOption(Profiles[0], Fr).Should().Be("Personnel — 50 %");
        DiscountScreenRules.ConventionOption(new ConventionOption(convention, "Clinique Al Amal", 15m, null), Fr)
            .Should().Be("Clinique Al Amal — 15 %");
    }

    [Fact]
    public void A_profile_is_checked_before_it_is_saved()
    {
        DiscountScreenRules.Check(new DiscountProfileForm { Name = " ", Percent = "10" }, null).Refusal!.Field
            .Should().Be(DiscountScreenRules.ProfileNameField);
        DiscountScreenRules.Check(new DiscountProfileForm { Name = "Personnel", Percent = "0" }, null).Refusal!.Key
            .Should().Be("DiscountProfile_PercentOutOfRange");
        DiscountScreenRules.Check(new DiscountProfileForm { Name = "Personnel", Percent = "101" }, null).Refusal!.Key
            .Should().Be("DiscountProfile_PercentOutOfRange");
        DiscountScreenRules.Check(new DiscountProfileForm
        {
            Name = "Étudiant", Percent = "10", ValidFrom = new DateOnly(2026, 9, 1), ValidUntil = new DateOnly(2026, 8, 31)
        }, null).Refusal!.Key.Should().Be("DiscountProfile_UntilBeforeFrom");

        var id = Guid.NewGuid();
        var (refusal, request) = DiscountScreenRules.Check(new DiscountProfileForm { Name = " Personnel ", Percent = "12,5 %", IsActive = false }, id);
        refusal.Should().BeNull();
        request.Should().Be(new SaveDiscountProfileRequest { Id = id, Name = "Personnel", Percent = 12.5m, IsActive = false });

        DiscountScreenRules.FormFor(Profiles[1], Fr).Should().Be(new DiscountProfileForm
        {
            Name = "Étudiant", Percent = "10", IsActive = true, ValidFrom = new DateOnly(2026, 9, 1), ValidUntil = new DateOnly(2027, 6, 30)
        });
        DiscountScreenRules.FieldOf("DiscountProfile_NameTaken").Should().Be(DiscountScreenRules.ProfileNameField);
        DiscountScreenRules.FieldOf("PartnershipPrice_Overlap").Should().Be(DiscountScreenRules.PriceAnalysisField);

        var rows = DiscountScreenRules.ProfileRows(
            [.. Profiles, new DiscountProfileRow(Guid.NewGuid(), "Ancien", 5m, false, null, null, false)], Text, Fr);
        rows[0].Cells.Should().Equal("Personnel", "50 %", "Discounts.Always", "Discounts.State.Offered");
        rows[1].Cells[2].Should().Be("01/09/2026 → 30/06/2027");
        rows[2].Cells[3].Should().Be("Discounts.State.Inactive");
        rows[2].Ink.Should().Be(StockInk.Muted);
    }

    [Fact]
    public void A_negotiated_price_is_a_price_or_a_rate_with_its_dates()
    {
        var partnership = Guid.NewGuid();
        var analysis = Guid.NewGuid();
        var from = new DateOnly(2026, 10, 1);

        DiscountScreenRules.Check(new PartnershipPriceForm { Value = "50", ValidFrom = from }, partnership, null).Refusal!.Key
            .Should().Be("PartnershipPrice_AnalysisRequired");
        DiscountScreenRules.Check(new PartnershipPriceForm { AnalysisTestId = analysis, Value = "abc", ValidFrom = from }, partnership, null)
            .Refusal!.Key.Should().Be("PartnershipPrice_PriceOutOfRange");
        DiscountScreenRules.Check(new PartnershipPriceForm { AnalysisTestId = analysis, IsFixedPrice = false, Value = "150", ValidFrom = from },
                                  partnership, null).Refusal!.Key.Should().Be("PartnershipPrice_PercentOutOfRange");
        DiscountScreenRules.Check(new PartnershipPriceForm
        {
            AnalysisTestId = analysis, Value = "50", ValidFrom = from, ValidUntil = from.AddDays(-1)
        }, partnership, null).Refusal!.Field.Should().Be(DiscountScreenRules.PriceUntilField);

        DiscountScreenRules.Check(new PartnershipPriceForm { AnalysisTestId = analysis, Value = "45,50", ValidFrom = from }, partnership, null)
            .Request.Should().Be(new SavePartnershipPriceRequest
            {
                PartnershipId = partnership, AnalysisTestId = analysis, FixedPrice = 45.50m, ValidFrom = from
            });
        DiscountScreenRules.Check(new PartnershipPriceForm { AnalysisTestId = analysis, IsFixedPrice = false, Value = "20 %", ValidFrom = from },
                                  partnership, null).Request!.DiscountPercent.Should().Be(20m);

        var row = new PartnershipPriceRow(Guid.NewGuid(), partnership, analysis, "GLY", MultilingualText.FromOptional("Glycémie", "سكر الدم", "Glucose")!,
                                          30m, null, 20m, from, null, true);
        row.NegotiatedPrice.Should().Be(24m);
        var cells = DiscountScreenRules.PriceRows([row], Text, Dh, AppLanguage.French, Fr)[0].Cells;
        cells.Should().Equal("GLY", "Glycémie", Dh(30m), "−20 %", Dh(24m), "Discounts.Since(01/10/2026)");

        DiscountScreenRules.PriceFormFor(row, from, Fr).Should().Be(new PartnershipPriceForm
        {
            AnalysisTestId = analysis, IsFixedPrice = false, Value = "20", ValidFrom = from
        });
        DiscountScreenRules.PriceFormFor(null, from, Fr).ValidFrom.Should().Be(from);
    }

    [Fact]
    public void The_report_filters_by_source_and_text_and_sums_by_source()
    {
        IReadOnlyList<DiscountReportRow> rows =
        [
            Row(DiscountSource.Convention, 30m, 300m, reason: "Convention : Clinique Al Amal"),
            Row(DiscountSource.Profile, 150m, 300m, approvedBy: "Dr Bennani", reference: "2610-000022", patient: "EL IDRISSI Hamid"),
            Row(DiscountSource.Manual, 20m, 400m, reference: "2610-000023", reason: "Geste commercial", percent: 5m)
        ];

        DiscountScreenRules.Filter(rows, DiscountSource.Profile, null).Should().ContainSingle().Which.Reference.Should().Be("2610-000022");
        DiscountScreenRules.Filter(rows, null, "idrissi").Should().ContainSingle();
        DiscountScreenRules.Filter(rows, null, "GESTE").Should().ContainSingle().Which.Source.Should().Be(DiscountSource.Manual);
        DiscountScreenRules.Filter(rows, null, "benn").Should().ContainSingle("the approver is searched too");
        DiscountScreenRules.Filter(rows, null, "").Should().HaveCount(3);
        DiscountScreenRules.Filter(null, null, null).Should().BeEmpty();

        var tiles = DiscountScreenRules.Tiles(rows, Text, Dh, Fr);
        tiles.Select(t => t.Key).Should().Equal("total", "count", "approved", "agreed", "granted");
        tiles[0].Value.Should().Be(Dh(200m));
        tiles[0].Sub.Should().Be("Discounts.Tile.OfGross(20 %)");
        tiles[1].Value.Should().Be("3");
        tiles[2].Value.Should().Be("1");
        tiles[3].Value.Should().Be(Dh(30m));
        tiles[4].Value.Should().Be(Dh(170m));

        var table = DiscountScreenRules.Rows(rows, Text, Dh, Fr);
        table[1].Cells.Should().Equal("20/10/2026 09:30", "2610-000022", "EL IDRISSI Hamid (P-000042)", "Discounts.Source.Profile", "",
                                      "10 %", Dh(150m), Dh(300m), "Personnel du laboratoire", "Salma", "Dr Bennani");
        table[1].Ink.Should().Be(StockInk.Abnormal, "a discount approved beyond the ceiling stands out");
        table[0].Cells[4].Should().Be("Clinique Al Amal");
        DiscountScreenRules.Columns.Should().HaveCount(table[0].Cells.Count);

        var csv = Encoding.UTF8.GetString(DiscountScreenRules.Csv(rows, Text, Dh, Fr)!);
        csv.Should().Contain("Discounts.Column.approvedBy").And.Contain("Geste commercial").And.Contain("Dr Bennani");
        DiscountScreenRules.FileName(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)).Should().Be("remises-20261001-20261031.csv");
        DiscountScreenRules.FilterKey(null).Should().Be("Discounts.AllSources");
        DiscountScreenRules.FilterKey(DiscountSource.Manual).Should().Be("Discounts.Source.Manual");
    }
}
