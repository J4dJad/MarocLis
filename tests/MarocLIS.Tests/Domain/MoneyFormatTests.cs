using System.Globalization;
using FluentAssertions;
using MarocLIS.Domain.Common;
using MarocLIS.Localization;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// An amount written for a page in the session's culture, whatever the server thread's: the
/// workstation's thread carries the operator's culture, a web server's does not.
/// </summary>
public sealed class MoneyFormatTests
{
    [Fact]
    public void TheAmount_IsWrittenInTheCultureGiven()
    {
        var amount = Money.Of(1234.5m);

        amount.Format(AppLanguage.French, CultureManager.Build(AppLanguage.French))
              .Should().Be(string.Format(CultureManager.Build(AppLanguage.French), "{0:N2} DH", 1234.5m))
              .And.Contain(",50 DH");
        amount.Format(AppLanguage.English, CultureInfo.InvariantCulture).Should().Be("1,234.50 MAD");
    }

    [Fact]
    public void WithoutACulture_TheThreadsIsUsed_AsBefore()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Money.Of(12m).Format(AppLanguage.French).Should().Be("12.00 DH");
            Money.Of(12m).Format(AppLanguage.Arabic).Should().Be("12.00 د.م.");
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
