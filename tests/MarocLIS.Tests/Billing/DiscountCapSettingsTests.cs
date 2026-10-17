using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Infrastructure.Persistence;
using Xunit;

namespace MarocLIS.Tests.Billing;

/// <summary>
/// The discount ceiling of Paramètres › Factures: the lower of the percentage and the amount, its
/// stored values read back, an unreadable value keeping the default ceiling rather than lifting
/// it, and the new approval right shipped to the roles that approve.
/// </summary>
public sealed class DiscountCapSettingsTests
{
    [Fact]
    public void TheCeiling_IsTheLowerOfThePercentageAndTheAmount()
    {
        DiscountCapSettings.Default.CapFor(747m).Should().Be(74.70m);
        DiscountCapSettings.Default.Exceeds(74.70m, 747m).Should().BeFalse();
        DiscountCapSettings.Default.Exceeds(74.71m, 747m).Should().BeTrue();
        DiscountCapSettings.Default.AmountCapApplies(747m).Should().BeFalse();

        var capped = new DiscountCapSettings { MaxPercent = 10m, MaxAmount = 50m };
        capped.CapFor(747m).Should().Be(50m);
        capped.AmountCapApplies(747m).Should().BeTrue();
        capped.CapFor(300m).Should().Be(30m, "on a small invoice the percentage is lower");
        capped.AmountCapApplies(300m).Should().BeFalse();
    }

    [Fact]
    public void StoredValues_AreReadBack_AndUnreadableOnesKeepTheDefault()
    {
        var cap = new DiscountCapSettings { MaxPercent = 12.5m, MaxAmount = 200m };
        var values = cap.ToValues().ToDictionary(v => v.Key, v => (string?)v.Value);
        DiscountCapSettings.FromValues(values).Should().Be(cap);

        DiscountCapSettings.FromValues(new Dictionary<string, string?>()).Should().Be(DiscountCapSettings.Default);
        DiscountCapSettings.FromValues(new Dictionary<string, string?>
        {
            [DiscountCapSettings.MaxPercentKey] = "beaucoup",
            [DiscountCapSettings.MaxAmountKey] = ""
        }).Should().Be(DiscountCapSettings.Default);
        DiscountCapSettings.FromValues(new Dictionary<string, string?>
        {
            [DiscountCapSettings.MaxPercentKey] = "250"
        }).MaxPercent.Should().Be(10m);
    }

    [Fact]
    public void Validation_RefusesAPercentageOutsideZeroToHundred_AndANegativeAmount()
    {
        new DiscountCapSettings { MaxPercent = 101m }.Validate().FirstError.ResourceKey
            .Should().Be("DiscountCap_PercentOutOfRange");
        new DiscountCapSettings { MaxAmount = -0.01m }.Validate().FirstError.ResourceKey
            .Should().Be("DiscountCap_AmountOutOfRange");
        new DiscountCapSettings { MaxPercent = 0m, MaxAmount = 0m }.Validate().IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void ThePasswordOfAnApproval_IsNeverPrinted()
    {
        new DiscountApproval("drbennani", "secret").ToString().Should().NotContain("secret");
    }

    [Fact]
    public void TheApprovalRight_IsShippedToTheRolesThatApprove_AndGrantedToAnExistingRoleOnStartUp()
    {
        Permissions.All.Should().Contain(Permissions.Billing.ApproveDiscount);
        ShippedRole(SystemRoles.Administrator).Should().Contain(Permissions.Billing.ApproveDiscount);
        ShippedRole(SystemRoles.ResponsibleBiologist).Should().Contain(Permissions.Billing.ApproveDiscount);
        ShippedRole(SystemRoles.Accountant).Should().Contain(Permissions.Billing.ApproveDiscount);
        ShippedRole(SystemRoles.Receptionist).Should().NotContain(Permissions.Billing.ApproveDiscount);

        // A database created before the right existed: the built-in role receives it at the next start.
        var shipped = ShippedRole(SystemRoles.Accountant);
        var existing = new Role(SystemRoles.Accountant, new MultilingualText("Comptabilité"), isBuiltIn: true);
        existing.SeedShippedPermissions(shipped.Where(p => p != Permissions.Billing.ApproveDiscount));
        existing.Has(Permissions.Billing.ApproveDiscount).Should().BeFalse();
        existing.ApplyShippedPermissions(shipped);
        existing.Has(Permissions.Billing.ApproveDiscount).Should().BeTrue();
    }

    private static string[] ShippedRole(string roleCode)
    {
        var type = typeof(LisDbContext).Assembly.GetType("MarocLIS.Infrastructure.Persistence.RolePermissionSets")!;
        var definitions = (System.Collections.IEnumerable)type
            .GetMethod("Build", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

        foreach (var definition in definitions)
        {
            var tuple = definition.GetType();
            if ((string)tuple.GetField("Item1")!.GetValue(definition)! == roleCode)
                return (string[])tuple.GetField("Item3")!.GetValue(definition)!;
        }

        return [];
    }
}
