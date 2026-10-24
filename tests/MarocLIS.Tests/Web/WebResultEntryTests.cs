using Bunit;
using FluentAssertions;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Web.Components.Screens.Results;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Saisie des résultats » in the browser: the worklist, the sheet ResultEntryService builds, the
/// keys and the rights of the workstation, the values saved, and the antériorités.
/// </summary>
public sealed class WebResultEntryTests : WebBenchContext
{
    private readonly BenchFile _file = new BenchFile()
        .Analysis("GLY", "Glycémie", ("GLY", "Glycémie à jeun", 0.70m, 1.10m))
        .Analysis("IONO", "Ionogramme", ("NA", "Sodium", 135m, 145m), ("K", "Potassium", 3.5m, 5.0m))
        .Entered("GLY", 1.86m);

    private readonly BenchFile _other = new BenchFile("2610-000022", "BENNANI", "Youssef", OrderPriority.Urgent)
        .Analysis("CRP", "Protéine C réactive", ("CRP", "CRP", 0m, 5m));

    public WebResultEntryTests()
    {
        Serve(_file);
        Pending(_file, _other);
    }

    private IRenderedComponent<ResultEntryScreen> Open() => Render<ResultEntryScreen>();

    private static bool Disabled(IRenderedComponent<ResultEntryScreen> page, string action) =>
        page.FindAll($"[data-action='{action}']").All(b => b.HasAttribute("disabled"));

    [Fact]
    public void TheFirstFileWaiting_Opens_WithItsSectionsAndTheAbnormalValueFlagged()
    {
        SignIn(AppLanguage.French, Technician);

        var page = Open();

        page.FindAll("[data-list=worklist] li").Select(l => l.GetAttribute("data-order"))
            .Should().Equal(_file.Order.AccessionNumber, _other.Order.AccessionNumber);
        page.Find("[data-state=patient]").TextContent.Should().Contain("Khadija").And.Contain(_file.Order.AccessionNumber);
        page.FindAll("[data-grid=entry] tr.section-row").Select(r => r.TextContent.Trim())
            .Should().Equal("GLY · BIOCHIMIE", "IONO · BIOCHIMIE");

        var glucose = page.Find("[data-grid=entry] tr[data-row=GLY]");
        glucose.QuerySelector("[data-field=value]")!.GetAttribute("value").Should().Be("1,86");
        glucose.TextContent.Should().Contain("▲ Haut");
        page.Find("[data-state=remaining]").TextContent.Should().Contain("2");

        // Opening a file is a consultation, recorded as at the workstation.
        DataAudit.Entries.Should().Contain(e => e.Kind == DataAccessKind.ViewOrder && e.OrderId == _file.Order.Id
                                                && e.Context == "ResultEntry");
    }

    [Fact]
    public void TheKeysOfTheGrid_AreOnTheInputs_AndSaveCarriesCtrlS()
    {
        SignIn(AppLanguage.French, Technician);

        var page = Open();

        page.FindAll("[data-entry-value]").Should().HaveCount(3);
        page.Find("[data-action='Action.Save']").GetAttribute("data-shortcut").Should().Be("Ctrl+S");
        page.Find("[data-action='Result.TechnicalComment']").GetAttribute("data-shortcut").Should().Be("F3");
        page.Find("[data-state=key-hints]").TextContent.Should().Contain("Entrée").And.Contain("Maj+Entrée").And.Contain("F3");
        page.Find("[data-state=instruments]").TextContent.Should().Contain("poste Windows");
    }

    [Fact]
    public void AScannedFileNumber_EndingWithEnter_OpensThatFile()
    {
        SignIn(AppLanguage.French, Technician);
        var page = Open();

        var scan = page.Find("[data-field=scan]");
        scan.Input("2610-000099");
        scan.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        ResultQueries.Calls.Should().Contain(c => c.Method == nameof(IResultQueries.ResolveOrderIdAsync)
                                                  && (string)c.Arguments[0]! == "2610-000099");
        page.Find("[data-state=status]").TextContent.Should().Contain("2610-000099");
    }

    [Fact]
    public void WithoutTheRightToValidate_TechnicalValidationIsOff()
    {
        SignIn(AppLanguage.French, Permissions.Results.View, Permissions.Results.Enter);

        var page = Open();

        Disabled(page, "Validation.Technical").Should().BeTrue("technical validation needs its own right");
        Disabled(page, "Action.Save").Should().BeFalse();
        Disabled(page, "ResultEntry.CorrectReleased").Should().BeTrue();
    }

    [Fact]
    public void WithoutTheRightToReadResults_TheHistoryIsHidden_AndNoFileOpens()
    {
        SignIn(AppLanguage.French, Permissions.Results.Enter);

        var page = Open();

        page.FindAll("[data-action='Result.History']").Should().BeEmpty("the history is hidden without the right to read results");
        page.FindAll("[data-grid=entry]").Should().BeEmpty();
        Disabled(page, "BenchSheet.Open").Should().BeFalse();
    }

    [Fact]
    public void ValuesTyped_AreSaved_OnlyWhatChanged_AndTheSheetReloaded()
    {
        SignIn(AppLanguage.French, Technician);
        var page = Open();

        page.Find("[data-grid=entry] tr[data-row=NA] [data-field=value]").Input("141");
        page.Find("[data-grid=entry] tr[data-row=K] [data-field=value]").Input("6,8");
        page.Find("[data-action='Action.Save']").Click();

        _file.Result("NA").NumericValue.Should().Be(141m);
        _file.Result("K").NumericValue.Should().Be(6.8m);
        _file.Result("GLY").NumericValue.Should().Be(1.86m);
        Work.CallsTo(nameof(MarocLIS.Application.Abstractions.IUnitOfWork.SaveChangesAsync)).Should().BeGreaterThan(0);
        page.Find("[data-state=status]").TextContent.Should().Contain("2 · Enregistré");
        page.Find("[data-grid=entry] tr[data-row=K]").TextContent.Should().Contain("▲ Haut");
    }

    [Fact]
    public void AnotherFile_IsOpenedOnlyOnceTheOperatorAgreesToLoseWhatIsTyped()
    {
        SignIn(AppLanguage.French, Technician);
        var page = Open();

        var resolved = ResultQueries.CallsTo(nameof(IResultQueries.ResolveOrderIdAsync));
        page.Find("[data-grid=entry] tr[data-row=NA] [data-field=value]").Input("141");
        page.Find($"[data-list=worklist] li[data-order='{_other.Order.AccessionNumber}']").Click();

        page.Find("[data-modal=confirm]").TextContent.Should().Contain("modifications n'ont pas été enregistrées");
        ResultQueries.CallsTo(nameof(IResultQueries.ResolveOrderIdAsync)).Should().Be(resolved, "nothing is opened before the answer");

        page.Find("[data-modal=confirm] [data-action='Action.No']").Click();
        page.Find("[data-grid=entry] tr[data-row=NA] [data-field=value]").GetAttribute("value").Should().Be("141");
    }

    [Fact]
    public void TheHistory_DrawsTheValidatedValues_AndTheValueOnScreenAsTheCurrentPoint()
    {
        SignIn(AppLanguage.French, Technician);
        ResultQueries.On(nameof(IResultQueries.GetValidatedHistoryAsync), _ => (IReadOnlyList<ResultHistoryPoint>)
        [
            new(Guid.NewGuid(), BenchFile.Now.AddDays(-90), 0.95m, null, "2607-000310"),
            new(Guid.NewGuid(), BenchFile.Now.AddDays(-30), 1.32m, null, "2609-000118")
        ]);
        var page = Open();

        page.Find("[data-grid=entry] tr[data-row=GLY]").DoubleClick();

        var dialog = page.Find("[data-modal=history]");
        dialog.QuerySelectorAll(".trend-point").Should().HaveCount(1);
        dialog.QuerySelectorAll(".trend-outside").Should().HaveCount(1, "1,32 is above the interval");
        dialog.QuerySelectorAll(".trend-current").Should().HaveCount(1, "1,86 is not released yet");
        dialog.QuerySelectorAll("[data-grid=history-values] tbody tr").Should().HaveCount(3);
        DataAudit.Entries.Should().Contain(e => e.Context == "ResultHistory");
    }

    [Fact]
    public void TheScreen_IsInArabic_ForAnArabicSession()
    {
        SignIn(AppLanguage.Arabic, Technician);

        var page = Open();

        page.Find("[data-action='Action.Save']").TextContent.Should().MatchRegex("[؀-ۿ]");
        page.Find("[data-grid=entry] thead").TextContent.Should().MatchRegex("[؀-ۿ]");
    }
}
