using Bunit;
using FluentAssertions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Pricing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Web.Components.Screens.Orders;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Nouvelle demande » in the browser: the workstation's counter, its basket rules (OrderBasket),
/// its quote (IOrderQuotes) and its gates (PermissionGate: off, not hidden).
/// </summary>
public sealed class WebOrderRegistrationTests : WebScreenContext
{
    private static readonly string[] Counter =
        [Permissions.Orders.View, Permissions.Orders.Create, Permissions.Patients.Create, Permissions.Patients.View];

    private readonly CatalogPickerItem _glucose = Analysis("GLY", "Glycémie à jeun", 40m, fasting: 8);
    private readonly CatalogPickerItem _cholesterol = Analysis("CHOL", "Cholestérol total", 45m);
    private readonly CatalogPickerItem _triglycerides = Analysis("TG", "Triglycérides", 45m);
    private readonly CatalogPickerItem _lipids;

    public WebOrderRegistrationTests()
    {
        _lipids = Panel("BILIP", "Bilan lipidique", 160m, _cholesterol, _triglycerides);
        Catalog.On(nameof(ICatalogQueries.SearchPickerAsync), args =>
        {
            IReadOnlyList<CatalogPickerItem> all = [_glucose, _cholesterol, _triglycerides, _lipids];
            var term = args[0] as string;
            return string.IsNullOrWhiteSpace(term)
                ? all
                : all.Where(i => i.Code.Contains(term, StringComparison.OrdinalIgnoreCase)
                                 || i.Name.Fr.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        });
        MasterData.On(nameof(IMasterDataQueries.GetPrescribersAsync), _ => (IReadOnlyList<PrescriberRow>)
        [
            new PrescriberRow(Guid.NewGuid(), "BENNANI Karim", null, PrescriberKind.Specialist, new MultilingualText("Cardiologie"),
                              null, "123456789", null, null, null, "Rabat", ReportDeliveryPreference.PatientCollects, false, null, true, 12)
        ]);
    }

    private IRenderedComponent<OrderRegistration> Open(Guid? patient = null) =>
        Render<OrderRegistration>(p => p.Add(x => x.PatientToSelect, patient));

    private static bool Disabled(IRenderedComponent<OrderRegistration> page, string action) =>
        page.Find($"[data-action='{action}']").HasAttribute("disabled");

    [Fact]
    public void TheCounter_ListsTheCatalogue_AndSaysWhatIsMissing()
    {
        SignIn(AppLanguage.French, Counter);

        var page = Open();

        page.FindAll("[data-grid=catalogue] tbody tr").Should().HaveCount(4);
        page.Find("[data-state=status]").TextContent.Should().Contain("patient").And.Contain("analyse");
        page.Find("label[for=order-prescriber]").TextContent.Should().EndWith("*", "a walk-in order requires its prescriber");
        page.FindAll("#order-prescribers option").Should().ContainSingle(o => o.GetAttribute("value") == "BENNANI Karim — Cardiologie");
        Disabled(page, "Action.Save").Should().BeTrue("there is neither a patient nor an analysis yet");
    }

    [Fact]
    public void AProfile_TakesOutTheMembersChosenAlone_AsOnTheWorkstation()
    {
        SignIn(AppLanguage.French, Counter);
        var page = Open();

        page.Find("[data-grid=catalogue] tr[data-code=CHOL] [data-action=add]").Click();
        page.Find("[data-grid=catalogue] tr[data-code=BILIP] [data-action=add]").Click();

        page.FindAll("[data-grid=basket] tbody tr[data-code]").Select(r => r.GetAttribute("data-code"))
            .Should().Equal("BILIP");
        page.Find("[data-state=status]").TextContent.Should().Contain("CHOL");

        // The member of a chosen profile is not added a second time.
        page.Find("[data-grid=catalogue] tr[data-code=TG]").DoubleClick();
        page.FindAll("[data-grid=basket] tbody tr[data-code]").Should().HaveCount(1);
        page.Find("[data-state=basket-count]").TextContent.Should().Be("1");
    }

    [Fact]
    public void TheTotal_IsTheQuoteOfTheRegistration_WithThePayersShare()
    {
        Quotes.On(nameof(IOrderQuotes.QuoteAsync), _ => Result.Success(new OrderQuote(
            new OrderPricing { Acts = [], Gross = Money.Of(40m), Total = Money.Of(40m), DueAtCounter = Money.Of(12m) },
            new MultilingualText("CNOPS"), 1,
            [new TubeToDraw(new MultilingualText("Tube sec"), "#E74C3C", 5m, ["GLY"])], [])));
        SignIn(AppLanguage.French, Counter);
        var page = Open();

        page.Find("[data-grid=catalogue] tr[data-code=GLY] [data-action=add]").Click();

        page.WaitForAssertion(() => page.Find("[data-total=due]").TextContent.Should().Contain("12,00"), TimeSpan.FromSeconds(10));
        page.Find("[data-total=gross]").TextContent.Should().Contain("40,00");
        page.Find("[data-total=payer]").TextContent.Should().Contain("28,00");
        page.Find("[data-state=tubes]").TextContent.Should().Contain("Tube sec 5 mL ×1");
        Quotes.CallsTo(nameof(IOrderQuotes.QuoteAsync)).Should().Be(1, "the quote waits for the clerk to pause");
    }

    [Fact]
    public void APatientChosenFromTheList_IsShown_AndACriticalAlertStated()
    {
        var row = PatientRow(critical: true);
        Patients.On(nameof(IPatientQueries.GetDetailAsync), _ => PatientDetailOf(row))
                .On(nameof(IPatientQueries.SearchAsync), _ => new Application.Abstractions.PagedResult<PatientSearchRow>([row], 1, 1, 10));
        SignIn(AppLanguage.French, Counter);

        var page = Open(row.Id);

        page.Find("[data-state=patient]").GetAttribute("data-patient").Should().Be(row.MedicalRecordNumber);
        page.Find("[data-modal=alerts]").TextContent.Should().NotBeEmpty();
        Entry.CallsTo(nameof(IOrderEntryQueries.GetRecentAnalysesAsync)).Should().Be(1, "the repeats are checked against the patient's analyses");

        page.Find("[data-grid=catalogue] tr[data-code=GLY] [data-action=add]").Click();
        page.Find("[data-field=prescriber]").Input("BENNANI Karim — Cardiologie");
        Disabled(page, "Action.Save").Should().BeFalse();
        page.WaitForAssertion(() => page.Find("[data-state=status]").TextContent.Should().Contain("8 h", "the longest fast is recalled"), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void WithoutTheRights_TheButtonsAreOff_NotHidden()
    {
        var row = PatientRow();
        Patients.On(nameof(IPatientQueries.GetDetailAsync), _ => PatientDetailOf(row))
                .On(nameof(IPatientQueries.SearchAsync), _ => new Application.Abstractions.PagedResult<PatientSearchRow>([row], 1, 1, 10));
        SignIn(AppLanguage.French, Permissions.Orders.View);

        var page = Open();
        Disabled(page, "Patient.NewFile").Should().BeTrue();
        Disabled(page, "OrderEntry.UnidentifiedPatient").Should().BeTrue();
        Disabled(page, "Order.NewPrescriber").Should().BeTrue();

        var withPatient = Open(row.Id);
        withPatient.Find("[data-grid=catalogue] tr[data-code=GLY] [data-action=add]").Click();
        Disabled(withPatient, "Action.Save").Should().BeTrue("registering needs Orders.Create");
        Disabled(withPatient, "OrderEntry.SaveWithoutLabels").Should().BeTrue();
    }

    [Fact]
    public void TheCounter_SpeaksArabic()
    {
        SignIn(AppLanguage.Arabic, Counter);

        var page = Open();

        page.Find("[data-action='Action.Save']").TextContent.Should().MatchRegex("[؀-ۿ]");
        page.Find("[data-field=patient-search]").GetAttribute("placeholder").Should().MatchRegex("[؀-ۿ]");
    }

    /// <summary>
    /// In Arabic the tube was named in French (« Tube gel séparateur ») and the turnaround read
    /// « h 4 »: both now follow the screen's language, as the collection screen already did.
    /// </summary>
    [Fact]
    public void InArabic_TheBasketNamesTheTubeAndTheHoursInArabic()
    {
        SignIn(AppLanguage.Arabic, Counter);
        var page = Open();

        page.Find("[data-grid=catalogue] tr[data-code=GLY] [data-action=add]").Click();

        var line = page.Find("[data-grid=basket] tbody tr[data-code=GLY]").TextContent;
        line.Should().Contain("أنبوب جاف").And.NotContain("Sec");
        line.Should().MatchRegex("4\u2069? سا").And.NotContain("4 h");
        page.Find("[data-grid=catalogue] tr[data-code=GLY]").TextContent.Should().Contain("أنبوب جاف").And.MatchRegex("4\u2069? سا");
        page.Find("[data-state=status]").TextContent.Should().NotContain(" h");
    }
}
