using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Web.Components.Screens.Patients;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Patients » in the browser: the search, the file it points at with its history, the accesses
/// recorded as the workstation records them (loi 08-09), and the buttons its rights leave on.
/// </summary>
public sealed class WebPatientSearchTests : WebScreenContext
{
    private readonly PatientSearchRow _fatima = PatientRow();
    private readonly PatientSearchRow _youssef = PatientRow("BERRADA Youssef", orders: 0);

    public WebPatientSearchTests()
    {
        Patients.On(nameof(IPatientQueries.ListAsync), args =>
                {
                    var query = (PatientListQuery)args[0]!;
                    IReadOnlyList<PatientSearchRow> found = string.IsNullOrEmpty(query.Term)
                        ? [_fatima, _youssef]
                        : [.. new[] { _fatima, _youssef }.Where(p => p.FullName.Contains(query.Term, StringComparison.OrdinalIgnoreCase))];
                    return new PagedResult<PatientSearchRow>(found, found.Count, 1, 50);
                })
                .On(nameof(IPatientQueries.CountListAsync), _ => new PatientListCounts(2, 0, 1))
                .On(nameof(IPatientQueries.GetOrderHistoryAsync), _ => (IReadOnlyList<PatientOrderRow>)
                [
                    new PatientOrderRow(Guid.NewGuid(), "2610-000042", TestClock.Now.AddDays(-2), LabOrderStatus.InProgress,
                                        OrderPriority.Urgent, 3, "Dr Bennani")
                ]);
    }

    [Fact]
    public void ASearch_ListsTheFiles_AndIsRecorded_AsIsTheFileOpened()
    {
        SignIn(AppLanguage.French, Permissions.Patients.View, Permissions.Orders.View);
        var page = Render<PatientSearch>();

        page.Find("[data-field=search]").Input("alaoui");
        page.Find("[data-field=search]").KeyDown("Enter");

        page.WaitForAssertion(() => page.FindAll("[data-grid=patients] tbody tr").Should().ContainSingle(), TimeSpan.FromSeconds(10));
        DataAudit.Entries.Should().Contain(e => e.Kind == DataAccessKind.Search && e.Context == "PatientSearch"
                                                && e.Count == 1 && e.Query == "alaoui");
        DataAudit.Entries.Should().Contain(e => e.Kind == DataAccessKind.ViewPatient && e.PatientId == _fatima.Id);
        page.Find("[data-panel=patient]").TextContent.Should().Contain("2610-000042");
        page.FindAll("[data-scope]").Select(c => c.GetAttribute("aria-pressed")).Should().Contain("true").And.Contain("false");
    }

    [Fact]
    public void AShortTerm_IsNeitherSearchedNorRecorded()
    {
        SignIn(AppLanguage.French, Permissions.Patients.View);
        var page = Render<PatientSearch>();
        var before = Patients.CallsTo(nameof(IPatientQueries.ListAsync));

        page.Find("[data-field=search]").Input("al");
        page.Find("[data-field=search]").KeyDown("Enter");

        Patients.CallsTo(nameof(IPatientQueries.ListAsync)).Should().Be(before);
        DataAudit.Entries.Should().NotContain(e => e.Kind == DataAccessKind.Search);
    }

    [Fact]
    public void WithoutTheRights_TheButtonsAreOff()
    {
        SignIn(AppLanguage.French, Permissions.Patients.View);

        var page = Render<PatientSearch>();

        page.Find("[data-action='Patient.NewFile']").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-action='PatientList.NewOrderForPatient']").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-action='Patient.Merge']").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-action='Patient.Anonymize']").HasAttribute("disabled").Should().BeTrue();

        // Who may only read the file is offered to consult it, not to modify it.
        page.FindAll("[data-action='Action.Edit']").Should().BeEmpty();
        page.Find("[data-action='Action.Consult']").TextContent.Should().Contain("Consulter");
    }

    [Fact]
    public void ANewOrderForThePatient_OpensTheCounterWithThePatient()
    {
        SignIn(AppLanguage.French, Permissions.Patients.View, Permissions.Orders.Create, Permissions.Patients.Create);
        var page = Render<PatientSearch>();

        page.Find("[data-action='Patient.NewFile']").HasAttribute("disabled").Should().BeFalse();
        page.Find("[data-action='PatientList.NewOrderForPatient']").Click();

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith($"ecran/orders?patient={_fatima.Id}");
    }
}
