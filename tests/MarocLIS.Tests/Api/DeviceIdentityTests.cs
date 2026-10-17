using System.Net;
using FluentAssertions;
using MarocLIS.Api.Security;
using Xunit;

namespace MarocLIS.Tests.Api;

/// <summary>The « Poste » the audit trail records for a request from a phone.</summary>
public sealed class DeviceIdentityTests
{
    private static readonly IPAddress Loopback = IPAddress.Loopback;
    private static readonly IPAddress Tailnet = IPAddress.Parse("100.64.0.7");
    private static readonly IPAddress Lan = IPAddress.Parse("192.168.1.50");

    [Fact]
    public void ThroughTailscaleServe_ThePhoneIsNamedByItsTailnetLoginAndAddress()
    {
        DeviceIdentity.Describe(true, "dr.alami@example.ma", "Karim Alami", Tailnet, Loopback)
            .Should().Be("Mobile · dr.alami@example.ma (100.64.0.7)");

        // The display name when the login is missing; the address alone when both are.
        DeviceIdentity.Describe(true, null, "Karim Alami", Tailnet, Loopback).Should().Be("Mobile · Karim Alami (100.64.0.7)");
        DeviceIdentity.Describe(true, " ", null, Tailnet, Loopback).Should().Be("Mobile · 100.64.0.7");
        DeviceIdentity.Describe(true, null, null, null, Loopback).Should().Be("Mobile · 127.0.0.1");
    }

    [Fact]
    public void FromAnywhereElse_TheHeadersAreIgnored_AndThePeerAddressNamesTheDevice()
    {
        DeviceIdentity.Describe(false, "directeur@example.ma", "Le directeur", Tailnet, Lan)
            .Should().Be("Mobile · 192.168.1.50");
        DeviceIdentity.Describe(false, null, null, null, null).Should().Be("Mobile · ?");
    }

    [Fact]
    public void AHeaderCannotBreakALineOfTheLog_NorFillItsColumn()
    {
        DeviceIdentity.Describe(true, "a\r\nb\u0007c", null, null, Loopback).Should().Be("Mobile · abc");
        DeviceIdentity.Describe(true, new string('x', 300), null, Tailnet, Loopback).Length.Should().BeLessThan(100);
    }
}
