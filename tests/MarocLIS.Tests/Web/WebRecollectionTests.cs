using Bunit;
using FluentAssertions;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Samples;
using MarocLIS.Web.Components.Screens.Samples;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>« Re-prélèvements » in the browser: the patients to call back, and what is offered by right and channel.</summary>
public sealed class WebRecollectionTests : WebBenchContext
{
    public WebRecollectionTests()
    {
        RecallQueries.On(nameof(IRecollectionQueries.GetPendingAsync), _ => (IReadOnlyList<RecollectionRow>)
        [
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "2610-000051", "BERRADA Nawal", "+212661112233", OrderPriority.Urgent,
                "T-OLD-51", "T-NEW-51", SampleRejectionReason.Haemolysed, null, TestClock.Now.AddHours(-3), "K NA", 0, null, null),
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "2610-000052", "SQALLI Hamza", null, OrderPriority.Routine,
                "T-OLD-52", "T-NEW-52", SampleRejectionReason.Clotted, null, TestClock.Now.AddHours(-5), "NFS", 1,
                TestClock.Now.AddHours(-1), "Messagerie")
        ]);
    }

    [Fact]
    public void ThePatientsToCallBack_AreListed_AndTheCallIsAsked()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Orders.Edit, Permissions.Samples.Reject);
        var page = Render<Recollections>();

        page.FindAll("[data-grid=recollections] tbody tr").Should().HaveCount(2);
        page.Find("[data-action='Recollection.RecordCall']").Click();

        page.Find("[data-modal=call]").TextContent.Should().Contain("BERRADA Nawal").And.Contain("+212661112233");
    }

    [Fact]
    public void WithoutTheRightToEditTheFile_OrWithoutAnSmsChannel_TheActionsAreOff()
    {
        SignIn(AppLanguage.French, Permissions.Samples.View, Permissions.Samples.Reject);
        var page = Render<Recollections>();

        page.Find("[data-action='Recollection.RecordCall']").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-action='Recollection.SendSms']").HasAttribute("disabled").Should().BeTrue("no SMS channel is configured");
    }
}
