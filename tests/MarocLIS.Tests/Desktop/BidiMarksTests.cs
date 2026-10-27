using FluentAssertions;
using MarocLIS.Domain.Common;
using MarocLIS.Localization;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// A Latin value in an Arabic sentence: isolated (LRI … PDI) for the browser, the phone and the
/// PDF documents; between two left-to-right marks on the Windows workstation, whose controls drew
/// the isolates as boxes around every name, number and date.
/// </summary>
public sealed class BidiMarksTests
{
    [Fact]
    public void TheWorkstation_KeepsALatinValueApart_WithInvisibleMarks_NotIsolates()
    {
        var desktop = new JsonLocalizer(new LocalizationOptions { BidiStyle = BidiStyle.Marks });

        var text = desktop.Format(AppLanguage.Arabic, "App.LabelValue", "الموقع", "DESKTOP-FMPMN8G");

        text.Should().Contain("‎DESKTOP-FMPMN8G‎");
        text.Should().NotContain("⁦").And.NotContain("⁩");
    }

    [Fact]
    public void TheOtherClients_StillUseIsolates_AndFrenchIsUntouched()
    {
        var web = new JsonLocalizer(new LocalizationOptions());

        web.Format(AppLanguage.Arabic, "App.LabelValue", "الموقع", "PRINCIPAL").Should().Contain("⁦PRINCIPAL⁩");
        new JsonLocalizer(new LocalizationOptions { BidiStyle = BidiStyle.Marks })
            .Format(AppLanguage.French, "App.LabelValue", "Site", "PRINCIPAL").Should().NotContain("‎");
    }

    [Fact]
    public void AValueAlreadyMarked_IsNotMarkedTwice()
    {
        var marked = BidiText.Isolate("2610-000014", BidiStyle.Marks);

        marked.Should().Be("‎2610-000014‎");
        BidiText.NeedsIsolation(marked).Should().BeFalse("a grid formats its cells again at every paint");
        BidiText.Isolate("2610-000014", BidiStyle.Isolates).Should().Be("⁦2610-000014⁩");
    }
}
