using Bunit;
using FluentAssertions;
using MarocLIS.Application.Administration;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Web.Components.Screens.Admin;
using MarocLIS.Web.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static MarocLIS.Tests.Desktop.AdministrationScreenRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Journal d'audit » in the browser: the thirty days the workstation opens on, the filters as
/// typed, a file number turning the consultations into that patient's history, the row opened
/// under the list, a deletion in red — and reading the trail itself recorded as a consultation.
/// No export, as on the workstation.
/// </summary>
public sealed class WebAuditTrailTests : WebAdminContext
{
    private static AuditEntryRow Change(AuditOperation operation = AuditOperation.Update, string entity = "Invoice") =>
        new(Guid.NewGuid(), new DateTimeOffset(2026, 10, 17, 11, 5, 0, TimeSpan.FromHours(1)), entity, Guid.NewGuid(), operation,
            "s.bennani", "ACCUEIL-01", "Status,PaidAmount", "{\"Status\":{\"old\":1,\"new\":2}}", "Paiement en espèces",
            null, "P-00000123", "Invoice", null);

    [Fact]
    public void TheTrail_OpensOnTheLastThirtyDays_AndItsReadingIsItselfRecorded()
    {
        SignIn(AppLanguage.French, Permissions.Administration.ViewAuditTrail);
        AuditQueries.On(nameof(IAuditQueries.GetChangesAsync), _ => new[] { Change(), Change(AuditOperation.Delete) });

        var page = Render<AuditTrail>();

        page.Find("[data-field=from]").GetAttribute("value").Should().Be("2026-09-18");
        page.Find("[data-field=to]").GetAttribute("value").Should().Be("2026-10-18");
        page.FindAll("[data-grid=changes] tbody tr").Should().HaveCount(2);
        page.FindAll("[data-grid=changes] tbody tr")[1].ClassList.Should().Contain("is-critical-row", "a deletion stands out");
        DataAudit.Entries.Should().Contain(e => e.Kind == DataAccessKind.Search && e.Query!.StartsWith("Journal des modifications"));
        page.FindAll("[data-action*=Export]").Should().BeEmpty("the workstation offers no export of the trail");
    }

    [Fact]
    public void TheFilters_AreSentAsTyped_AndAFileNumberAsksForThatPatientsHistory()
    {
        SignIn(AppLanguage.French, Permissions.Administration.ViewAuditTrail);
        var page = Render<AuditTrail>();

        page.Find("[data-field=user]").Change("s.bennani");
        page.Find("[data-field=search]").Change("Invoice");
        page.Find("[data-field=record]").Change(" P-00000123 ");
        page.Find("[data-action='Action.Apply']").Click();

        var filter = (AuditTrailFilter)AuditQueries.Calls.Last(c => c.Method == nameof(IAuditQueries.GetChangesAsync)).Arguments[0]!;
        filter.UserName.Should().Be("s.bennani");
        filter.Search.Should().Be("Invoice");
        var access = (AuditTrailFilter)AuditQueries.Calls.Last(c => c.Method == nameof(IAuditQueries.GetDataAccessAsync)).Arguments[0]!;
        access.PatientRecordNumber.Should().Be("P-00000123");
        DataAudit.Entries.Should().Contain(e => e.Kind == DataAccessKind.SubjectAccessRequest);
    }

    [Fact]
    public void ARowSelected_IsOpenedUnderTheList()
    {
        SignIn(AppLanguage.French, Permissions.Administration.ViewAuditTrail);
        var change = Change();
        AuditQueries.On(nameof(IAuditQueries.GetChangesAsync), _ => new[] { change });
        AuditQueries.On(nameof(IAuditQueries.GetSecurityEventsAsync), _ => new[] { Event(SecurityEventKind.LoginFailed, succeeded: false) });

        var page = Render<AuditTrail>();
        page.Find($"[data-grid=changes] tr[data-row='{change.AuditEntryId}']").Click();
        // The change read one field a line, its value before and after, never the raw payload; the reason and the
        // workstation (a column of little use in the list) beside it.
        var line = page.Find("[data-state=detail] tbody tr");
        line.QuerySelectorAll("td").Select(c => c.TextContent.Trim()).Should().Equal("Status", "1", "2");
        page.Find("[data-state=detail]").TextContent.Should().NotContain("{");
        page.Find("[data-state=reason]").TextContent.Should().Be("Paiement en espèces");
        page.Find("[data-state=workstation]").TextContent.Should().Be("ACCUEIL-01");
        page.Find("[data-grid=changes] td[title='Status,PaidAmount']").TextContent.Should().Be("Status,PaidAmount");

        page.Find("[data-tab=Security]").Click();
        page.FindAll("[data-grid=security] tbody tr").Should().ContainSingle();
    }

    [Fact]
    public void WithoutTheRight_TheTrailIsRefusedByTheService()
    {
        SignIn(AppLanguage.French, Permissions.Administration.ViewUsers);

        var page = Render<AuditTrail>();

        AuditQueries.Calls.Should().BeEmpty();
        page.Find("[data-state=status]").TextContent.Should().NotBeEmpty();
    }

    [Fact]
    public void InArabic_TheEmptyTrailSaysSo()
    {
        SignIn(AppLanguage.Arabic, Permissions.Administration.ViewAuditTrail);

        var page = Render<AuditTrail>();

        page.Find("[data-state=empty]").TextContent.Should().Be("لا يوجد أي قيد يطابق عوامل التصفية.");
        Services.GetRequiredService<WebTexts>().Direction.Should().Be("rtl");
    }
}
