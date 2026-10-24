using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Results;
using MarocLIS.Web.Components.Screens.Results;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Validation biologique » in the browser: the worklist and its signals, the ticks, what the
/// bar offers by right, and the call of a critical value with its read-back.
/// </summary>
public sealed class WebValidationTests : WebBenchContext
{
    private readonly BenchFile _file;

    public WebValidationTests()
    {
        _file = new BenchFile(priority: OrderPriority.Urgent)
            .Analysis("GLY", "Glycémie", ("GLY", "Glycémie à jeun", 0.70m, 1.10m))
            .Analysis("IONO", "Ionogramme", ("NA", "Sodium", 135m, 145m), ("K", "Potassium", 3.5m, 5.0m));
        _file.Parameters["K"].CriticalHigh = 6.5m;
        _file.Entered("GLY", 0.92m, technicallyValidated: true)
             .Entered("NA", 139m, technicallyValidated: true)
             .Entered("K", 7.1m, technicallyValidated: true);

        Serve(_file);
        ValidationQueries.On(nameof(IValidationQueries.GetValidationWorklistAsync), _ => (IReadOnlyList<ValidationWorklistEntry>)
        [
            new()
            {
                LabOrderId = _file.Order.Id, AccessionNumber = _file.Order.AccessionNumber, PatientName = _file.Patient.FullName,
                AgeYears = 47, Gender = MarocLIS.Domain.Patients.Gender.Female, Priority = OrderPriority.Urgent,
                RegisteredAt = BenchFile.Now.AddHours(-2), AwaitingCount = 3, AbnormalCount = 1, CriticalCount = 1,
                UncommunicatedCriticalCount = 1
            },
            new()
            {
                LabOrderId = Guid.NewGuid(), AccessionNumber = "2610-000030", PatientName = "TAZI Omar",
                RegisteredAt = BenchFile.Now.AddHours(-3), AwaitingCount = 4
            }
        ]);
    }

    private IRenderedComponent<BiologicalValidation> Open() => Render<BiologicalValidation>();

    private static bool Disabled(IRenderedComponent<BiologicalValidation> page, string action) =>
        page.FindAll($"[data-action='{action}']").All(b => b.HasAttribute("disabled"));

    [Fact]
    public void TheFileWithACriticalValueToCall_IsSetApart_AndItsReleasableValuesComeTicked()
    {
        SignIn(AppLanguage.French, Biologist);

        var page = Open();

        var first = page.Find("[data-grid=worklist] tbody tr");
        first.ClassList.Should().Contain("is-critical-row");
        first.TextContent.Should().Contain("Urgent").And.Contain("F · 47");
        page.Find("[data-filter=urgent]").TextContent.Should().Contain("· 1");

        page.FindAll("[data-grid=results] [data-field=tick]").Should().OnlyContain(t => t.HasAttribute("checked"));
        page.Find("[data-action='Validation.ValidateSelection']").TextContent.Should().Contain("3");
        page.Find("[data-action='Validation.ValidateSelection']").GetAttribute("data-shortcut").Should().Be("F9");
        page.Find("[data-grid=results] tr[data-row=K]").ClassList.Should().Contain("is-critical-row");
        page.Find("[data-state=context]").TextContent.Should().Contain("1");
        page.Find("[data-action='Validation.KeyNextOrder']").GetAttribute("data-shortcut").Should().Be("F10");
    }

    [Fact]
    public void Unticking_ChangesTheCount_AndSelectAllTicksBack()
    {
        SignIn(AppLanguage.French, Biologist);
        var page = Open();

        page.Find("[data-grid=results] tr[data-row=NA] [data-field=tick]").Change(false);
        page.Find("[data-action='Validation.ValidateSelection']").TextContent.Should().Contain("2");
        page.Find("[data-state=selection]").TextContent.Should().Contain("2").And.Contain("3");

        page.Find("[data-action='Action.SelectAll']").Click();
        page.Find("[data-action='Validation.ValidateSelection']").TextContent.Should().Contain("3");
    }

    [Fact]
    public void WithoutTheRights_TheBarsButtonsAreOff_AsAtTheWorkstation()
    {
        SignIn(AppLanguage.French, Permissions.Results.View, Permissions.Results.BiologicalValidate);

        var page = Open();

        Disabled(page, "Validation.ValidateSelection").Should().BeFalse();
        Disabled(page, "Nav.CriticalResults").Should().BeTrue("calling a critical value needs its right");
        Disabled(page, "Report.Title").Should().BeTrue();
        Disabled(page, "Validation.Unvalidate").Should().BeTrue();
        page.FindAll("[data-action='Validation.OpenFullOrder']").Should().BeEmpty("the file needs the right to see orders");
    }

    [Fact]
    public void ACriticalValue_IsPhoned_ThenReadBack_AndTheCallRecorded()
    {
        SignIn(AppLanguage.French, Biologist);
        CriticalResultNotification? raised = null;
        ValidationQueries.On(nameof(IValidationQueries.GetResultAsync), _ => _file.Result("K"));
        NotificationStore.On(nameof(IRepository<CriticalResultNotification>.AddAsync), args =>
        {
            raised = (CriticalResultNotification)args[0]!;
            return null;
        }).On(nameof(IRepository<CriticalResultNotification>.GetByIdAsync), _ => raised);
        var page = Open();

        page.Find("[data-action='Nav.CriticalResults']").Click();

        raised.Should().NotBeNull();
        raised!.ReportedValue.Should().Be("7,10");
        page.Find("[data-modal=critical-recipient]").TextContent.Should().Contain("Potassium");
        page.Find("[data-modal=critical-recipient] [data-field=text]").Input("Dr Berrada");
        page.Find("[data-modal=critical-recipient] [data-action='Action.Ok']").Click();

        page.Find("[data-modal=critical-readback] [data-field=text]").Input("7,10");
        page.Find("[data-modal=critical-readback] [data-action='Action.Ok']").Click();

        raised.Outcome.Should().Be(NotificationOutcome.Acknowledged);
        raised.RecipientName.Should().Be("Dr Berrada");
        raised.ReadBackConfirmed.Should().BeTrue();
        page.FindAll("[data-modal]").Should().BeEmpty();
    }

    [Fact]
    public void ACallWithoutReadBack_IsClosedAsNotConfirmed_AndSaysWhy()
    {
        SignIn(AppLanguage.French, Biologist);
        CriticalResultNotification? raised = null;
        ValidationQueries.On(nameof(IValidationQueries.GetResultAsync), _ => _file.Result("K"));
        NotificationStore.On(nameof(IRepository<CriticalResultNotification>.AddAsync), args =>
        {
            raised = (CriticalResultNotification)args[0]!;
            return null;
        }).On(nameof(IRepository<CriticalResultNotification>.GetByIdAsync), _ => raised);
        var page = Open();

        page.Find("[data-action='Nav.CriticalResults']").Click();
        page.Find("[data-modal=critical-recipient] [data-field=text]").Input("Dr Berrada");
        page.Find("[data-modal=critical-recipient] [data-action='Action.Ok']").Click();
        page.Find("[data-modal=critical-readback] [data-action='Action.Cancel']").Click();

        raised!.Outcome.Should().NotBe(NotificationOutcome.Acknowledged);
        raised.Notes.Should().Be(CriticalCallNotes.ReadBackNotConfirmed);
        page.Find("[data-modal=validation-message]").Should().NotBeNull();
    }

    [Fact]
    public void TheActions_AreInOneBar_ThePrimaryAtTheEnd_AndUnvalidatingSetApart()
    {
        SignIn(AppLanguage.French, Biologist);

        var page = Open();

        page.FindAll("[data-bar=actions]").Should().ContainSingle();
        page.Find(".action-bar-end [data-action='Validation.ValidateSelection']").ClassList.Should().Contain("btn-primary");
        page.Find(".action-bar-start [data-action='Result.History']").Should().NotBeNull();
        page.Find(".more-menu [data-action='Validation.Unvalidate']").ClassList.Should().Contain("btn-danger");
        page.Find("[data-card=worklist] [data-action='Action.Refresh']").Should().NotBeNull("the refresh is at the top of the list");
    }

    [Fact]
    public void TheScreen_IsInArabic_ForAnArabicSession()
    {
        SignIn(AppLanguage.Arabic, Biologist);

        var page = Open();

        page.Find("[data-grid=worklist] thead").TextContent.Should().MatchRegex("[؀-ۿ]");
        page.Find("[data-action='Validation.ValidateSelection']").TextContent.Should().MatchRegex("[؀-ۿ]");
    }
}
