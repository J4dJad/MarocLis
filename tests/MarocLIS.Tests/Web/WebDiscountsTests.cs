using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Pricing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Relations;
using MarocLIS.Web.Components.Screens.Billing;
using MarocLIS.Web.Components.Screens.Orders;
using MarocLIS.Web.Components.Screens.Relations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// The discounts in the browser: « Remises » (the report, its filters and its CSV), « Profils de
/// remise », « Remise… » at the counter with its approval beyond the ceiling, and the negotiated
/// prices of a convention — each control hidden from whoever lacks its right.
/// </summary>
public sealed class WebDiscountsTests : WebScreenContext
{
    private readonly Fake<IDiscountQueries> _queries = Fake<IDiscountQueries>.Create();
    private readonly Fake<IRepository<DiscountProfile>> _profiles = Fake<IRepository<DiscountProfile>>.Create();
    private readonly Fake<IRepository<PartnershipPrice>> _prices = Fake<IRepository<PartnershipPrice>>.Create();
    private readonly Fake<IRepository<Partnership>> _partnerships = Fake<IRepository<Partnership>>.Create();
    private readonly Fake<IUnitOfWork> _work = Fake<IUnitOfWork>.Create();

    private static readonly string[] Counter =
        [Permissions.Orders.View, Permissions.Orders.Create, Permissions.Patients.Create, Permissions.Patients.View];

    public WebDiscountsTests()
    {
        Services.AddSingleton(_queries.Object);
        Services.AddSingleton(_profiles.Object);
        Services.AddSingleton(_prices.Object);
        Services.AddSingleton(_partnerships.Object);
        Services.AddSingleton(Fake<IRepository<AnalysisTest>>.Create().Object);
        Services.AddSingleton(_work.Object);
        Services.AddScoped<DiscountService>();

        Catalog.On(nameof(MarocLIS.Infrastructure.Queries.ICatalogQueries.SearchPickerAsync), _ =>
            (IReadOnlyList<Application.Catalog.CatalogPickerItem>)[Analysis("GLY", "Glycémie à jeun", 300m)]);
    }

    private static DiscountReportRow Row(DiscountSource source, decimal amount, string reference, string patient, string? approvedBy = null,
                                         string? reason = "Personnel du laboratoire") =>
        new(Guid.NewGuid(), new DateTimeOffset(2026, 10, 12, 10, 15, 0, TimeSpan.FromHours(1)), false, reference, patient, "P-2026-000123",
            source, source == DiscountSource.Convention ? "Clinique Al Amal" : null, 10m, amount, 300m, reason, "Nadia", approvedBy);

    private void Report() =>
        _queries.On(nameof(IDiscountQueries.GetDiscountsAsync), _ => (IReadOnlyList<DiscountReportRow>)
        [
            Row(DiscountSource.Convention, 30m, "2610-000041", "ALAOUI Fatima Zahra", reason: "Convention : Clinique Al Amal"),
            Row(DiscountSource.Profile, 150m, "2610-000042", "EL IDRISSI Hamid", approvedBy: "Dr Bennani"),
            Row(DiscountSource.Manual, 20m, "2610-000043", "TAZI Omar", reason: "Geste commercial")
        ]);

    // ---- Remises --------------------------------------------------------------------------------------

    [Fact]
    public void The_report_lists_who_granted_what_filters_by_source_and_text_and_leaves_as_csv()
    {
        SignIn(AppLanguage.French, Permissions.Billing.ApproveDiscount);
        Report();

        var page = Render<DiscountsScreen>();

        page.WaitForAssertion(() => page.FindAll("[data-grid=discounts] tbody tr[data-row]").Should().HaveCount(3));
        page.Find("[data-tile=total]").TextContent.Should().MatchRegex(@"200,00\sDH");
        page.Find("[data-tile=approved]").TextContent.Should().Contain("1");
        page.Find("[data-grid=discounts] thead").TextContent.Should().Contain("Approuvée par");
        page.FindAll("[data-grid=discounts] tbody tr")[1].TextContent.Should().Contain("EL IDRISSI Hamid").And.Contain("Dr Bennani");

        page.Find("[data-chip=Profile]").Click();
        page.FindAll("[data-grid=discounts] tbody tr[data-row]").Should().ContainSingle()
            .Which.TextContent.Should().Contain("2610-000042");
        page.Find("[data-chip=all]").Click();
        page.Find("[data-field=discount-search]").Input("geste");
        page.FindAll("[data-grid=discounts] tbody tr[data-row]").Should().ContainSingle()
            .Which.TextContent.Should().Contain("TAZI Omar");

        page.Find(".action-bar [data-action='Statistics.ExportCsv']").Click();
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "marocLis.download"
                                                    && (string)i.Arguments[0]! == "remises-20261001-20261018.csv");
    }

    [Fact]
    public void The_report_speaks_arabic()
    {
        SignIn(AppLanguage.Arabic, Permissions.Steering.View);
        Report();
        var page = Render<DiscountsScreen>();
        page.WaitForAssertion(() => page.FindAll("[data-grid=discounts] tbody tr[data-row]").Should().HaveCount(3));
        page.Find("[data-chip=Convention]").TextContent.Should().MatchRegex(@"\p{IsArabic}");
        page.Find("[data-grid=discounts] thead").TextContent.Should().MatchRegex(@"\p{IsArabic}");
    }

    [Fact]
    public void The_report_is_closed_to_whoever_neither_approves_nor_steers()
    {
        SignIn(AppLanguage.French, Permissions.Billing.ApplyDiscount);
        Report();
        var page = Render<DiscountsScreen>();
        page.Find(".action-bar [data-action='Statistics.Compute']").HasAttribute("disabled").Should().BeTrue();
        _queries.CallsTo(nameof(IDiscountQueries.GetDiscountsAsync)).Should().Be(0);
    }

    // ---- Profils de remise -----------------------------------------------------------------------------

    [Fact]
    public void A_profile_is_created_its_rate_checked_beside_its_box()
    {
        SignIn(AppLanguage.French, Permissions.Billing.ManageTariffs);
        var staff = new DiscountProfile("Personnel", 50m);
        _profiles.On(nameof(IRepository<DiscountProfile>.ListAsync), _ => (IReadOnlyList<DiscountProfile>)[staff]);
        var page = Render<DiscountProfilesScreen>();

        page.WaitForAssertion(() => page.FindAll("[data-grid=discountprofiles] tbody tr[data-row]").Should().ContainSingle()
                                        .Which.TextContent.Should().Contain("Personnel").And.Contain("50 %"));

        page.Find(".action-bar [data-action='Discounts.NewProfile']").Click();
        page.Find("[data-field=profile-name]").Input("Étudiant");
        page.Find("[data-field=profile-percent]").Input("120");
        page.Find("[data-modal=discount-profile] [data-action='Action.Save']").Click();
        page.Find("[data-refusal=profile-percent]").Should().NotBeNull();
        _profiles.CallsTo(nameof(IRepository<DiscountProfile>.AddAsync)).Should().Be(0);

        _profiles.On(nameof(IRepository<DiscountProfile>.ListAsync), _ => (IReadOnlyList<DiscountProfile>)[]);
        page.Find("[data-field=profile-percent]").Input("10");
        page.Find("[data-modal=discount-profile] [data-action='Action.Save']").Click();

        page.WaitForAssertion(() => _profiles.CallsTo(nameof(IRepository<DiscountProfile>.AddAsync)).Should().Be(1));
        ((string?)_work.Calls.Last(c => c.Method == "set_AuditReason").Arguments[0]).Should().StartWith("Profil de remise créé : Étudiant 10 %");
        page.FindAll("[data-modal=discount-profile]").Should().BeEmpty();
    }

    [Fact]
    public void Without_the_tariffs_right_the_profiles_cannot_be_changed_the_buttons_hidden()
    {
        SignIn(AppLanguage.French, Permissions.Billing.ApplyDiscount);
        _profiles.On(nameof(IRepository<DiscountProfile>.ListAsync), _ => (IReadOnlyList<DiscountProfile>)[new DiscountProfile("Personnel", 50m)]);

        var page = Render<DiscountProfilesScreen>();

        page.WaitForAssertion(() => page.FindAll("[data-grid=discountprofiles] tbody tr[data-row]").Should().ContainSingle());
        page.FindAll("[data-action='Discounts.NewProfile']").Should().BeEmpty();
        page.FindAll("[data-action='Discounts.EditProfile']").Should().BeEmpty();
        page.FindAll("[data-grid=discountprofiles] tbody tr[data-row]")[0].DoubleClick();
        page.FindAll("[data-modal=discount-profile]").Should().BeEmpty();
    }

    // ---- Remise… at the counter -------------------------------------------------------------------------

    [Fact]
    public void Without_the_discount_right_the_counter_has_no_discount_button()
    {
        SignIn(AppLanguage.French, Counter);

        var page = Render<OrderRegistration>();

        page.FindAll("[data-action='Discounts.Grant']").Should().BeEmpty("the web hides a control its operator may not use");
    }

    [Fact]
    public void A_profile_above_the_ceiling_asks_for_an_approver_and_the_total_says_the_discount()
    {
        SignIn(AppLanguage.French, [.. Counter, Permissions.Billing.ApplyDiscount]);
        var staff = new DiscountProfile("Personnel", 50m);
        _profiles.On(nameof(IRepository<DiscountProfile>.ListAsync), _ => (IReadOnlyList<DiscountProfile>)[staff]);
        Quotes.On(nameof(IOrderQuotes.QuoteAsync), args =>
        {
            var request = (OrderQuoteRequest)args[0]!;
            var discount = request.DiscountProfileId is null ? 0m : 150m;
            return Result.Success(new OrderQuote(
                new OrderPricing
                {
                    Acts = [], Gross = Money.Of(300m), Discount = Money.Of(discount), RateDiscount = Money.Of(discount),
                    Total = Money.Of(300m - discount), DueAtCounter = Money.Of(300m - discount)
                },
                null, 1, [], [],
                discount == 0m ? null : new AppliedDiscount(DiscountSource.Profile, 50m, "Personnel", Money.Of(discount), null, null, true, Money.Of(30m))));
        });
        var page = Render<OrderRegistration>();

        page.Find("[data-action='Discounts.Grant']").HasAttribute("disabled").Should().BeTrue("there is nothing to discount yet");
        page.Find("[data-grid=catalogue] tr[data-code=GLY] [data-action=add]").Click();
        page.WaitForAssertion(() => page.Find("[data-total=due]").TextContent.Should().Contain("300,00"), TimeSpan.FromSeconds(10));

        page.Find("[data-action='Discounts.Grant']").Click();
        page.WaitForAssertion(() => page.Find("[data-modal=order-discount]").Should().NotBeNull());
        page.Find("[data-field=discount-profile]").Change(staff.Id.ToString());
        page.Find("[data-state=estimate]").TextContent.Should().MatchRegex(@"150,00\sDH");
        page.Find("[data-state=approval]").Should().NotBeNull("50 % of 300 DH goes beyond the 10 % ceiling");

        page.Find("[data-field=discount-reason]").Input("Personnel du laboratoire");
        page.Find("[data-modal=order-discount] [data-action='Action.Apply']").Click();
        page.Find("[data-refusal=approver]").Should().NotBeNull();

        page.Find("[data-field=approver]").Input("dr.bennani");
        page.Find("[data-field=approver-password]").Input("secret");
        page.Find("[data-modal=order-discount] [data-action='Action.Apply']").Click();

        page.WaitForAssertion(() => page.Find("[data-total=due]").TextContent.Should().Contain("150,00"));
        page.Find("[data-state=discount-line]").TextContent.Should().Contain("Profil Personnel (50 %)").And.Contain("approbation");
        page.Find("[data-total=gross]").TextContent.Should().Contain("300,00");
        ((OrderQuoteRequest)Quotes.Calls.Last().Arguments[0]!).DiscountProfileId.Should().Be(staff.Id);
    }

    // ---- Negotiated prices ---------------------------------------------------------------------------------

    [Fact]
    public void A_conventions_prices_are_read_with_the_conventions_the_buttons_hidden_without_the_tariffs()
    {
        var partnership = Guid.NewGuid();
        var analysis = Guid.NewGuid();
        _queries.On(nameof(IDiscountQueries.GetPartnershipPricesAsync), _ => (IReadOnlyList<PartnershipPriceRow>)
        [
            new(Guid.NewGuid(), partnership, analysis, "GLY", new MultilingualText("Glycémie à jeun"), 300m, 250m, null,
                new DateOnly(2026, 10, 1), null, true)
        ]);

        SignIn(AppLanguage.French, Permissions.Relations.View);
        var reader = Render<PartnershipPricesDialog>(p => p.Add(x => x.PartnershipId, partnership).Add(x => x.PartnerName, "Clinique Al Amal"));
        reader.WaitForAssertion(() => reader.FindAll("[data-grid=partnership-prices] tbody tr[data-row]").Should().ContainSingle()
                                            .Which.TextContent.Should().Contain("GLY").And.MatchRegex(@"250,00\sDH"));
        reader.FindAll("[data-action='Discounts.NewPrice']").Should().BeEmpty();
        reader.FindAll("[data-action='Discounts.RemovePrice']").Should().BeEmpty();
    }

    [Fact]
    public void A_negotiated_price_needs_its_analysis_before_it_is_saved()
    {
        SignIn(AppLanguage.French, Permissions.Billing.ManageTariffs);
        var manager = Render<PartnershipPricesDialog>(p => p.Add(x => x.PartnershipId, Guid.NewGuid()).Add(x => x.PartnerName, "Clinique Al Amal"));
        manager.Find("[data-action='Discounts.NewPrice']").Click();
        manager.Find("[data-field=price-value]").Input("45");
        manager.Find("[data-modal=partnership-price] [data-action='Action.Save']").Click();
        manager.Find("[data-refusal=price-analysis]").Should().NotBeNull("an analysis is chosen first");
        _prices.CallsTo(nameof(IRepository<PartnershipPrice>.AddAsync)).Should().Be(0);

        manager.Find("[data-field=price-analysis-search]").Input("GLY");
        manager.Find("[data-modal=partnership-price] [data-action='Action.Save']").Click();
        // The analysis found is chosen; the service is then asked, and refuses a convention it does not know.
        manager.WaitForAssertion(() => manager.Find("[data-modal=partnership-price] [data-state=refusal]").Should().NotBeNull());
        manager.FindAll("[data-refusal=price-analysis]").Should().BeEmpty();
        _partnerships.CallsTo(nameof(IRepository<Partnership>.GetByIdAsync)).Should().Be(1);
    }
}
