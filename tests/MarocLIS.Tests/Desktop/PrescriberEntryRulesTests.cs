using FluentAssertions;
using MarocLIS.Application.Administration;
using MarocLIS.Domain.Orders;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The prescriber form's rules, moved out of the workstation's dialog so the web counter creates a
/// doctor the same way.
/// </summary>
public sealed class PrescriberEntryRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_prescriber_needs_a_last_name(string? lastName) =>
        PrescriberEntryRules.Problem(lastName).Should().Be("Prescriber_LastNameRequired");

    [Fact]
    public void A_last_name_is_enough() => PrescriberEntryRules.Problem("Bennani").Should().BeNull();

    [Theory]
    [InlineData(PrescriberKind.GeneralPractitioner, null, true)]
    [InlineData(PrescriberKind.Specialist, " ", true)]
    [InlineData(PrescriberKind.Specialist, "123456789", false)]
    public void The_inpe_warning_is_for_physicians(PrescriberKind kind, string? inpe, bool warns) =>
        PrescriberEntryRules.WarnsOfMissingInpe(kind, inpe).Should().Be(warns);

    [Fact]
    public void The_inpe_warning_spares_the_other_kinds()
    {
        foreach (var kind in Enum.GetValues<PrescriberKind>()
                     .Where(k => k is not (PrescriberKind.GeneralPractitioner or PrescriberKind.Specialist)))
            PrescriberEntryRules.WarnsOfMissingInpe(kind, null).Should().BeFalse();
    }

    [Fact]
    public void The_typed_name_splits_at_its_first_space()
    {
        PrescriberEntryRules.SplitTypedName("  Alaoui Mohamed Amine ").Should().Be(("Alaoui", "Mohamed Amine"));
        PrescriberEntryRules.SplitTypedName("Tazi").Should().Be(("Tazi", (string?)null));
        PrescriberEntryRules.SplitTypedName(null).Should().Be((string.Empty, (string?)null));
    }
}
