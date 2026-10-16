using FluentAssertions;
using MarocLIS.Desktop.Views;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure.Queries;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>The rules of the patient screen that do not need a window.</summary>
public sealed class PatientListScreenTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("a", false)]
    [InlineData("al", false)]
    [InlineData("ala", true)]
    public void A_term_is_searched_when_empty_or_three_characters_long(string term, bool searched) =>
        PatientListRules.IsSearchable(term).Should().Be(searched);

    [Fact]
    public void The_identity_gap_says_what_is_missing_and_provisional_comes_first()
    {
        PatientListRules.IdentityGapKey(PatientIdentityGap.None).Should().BeNull();
        PatientListRules.IdentityGapKey(PatientIdentityGap.Gender).Should().Be("PatientList.GapGender");
        PatientListRules.IdentityGapKey(PatientIdentityGap.DateOfBirth).Should().Be("PatientList.GapDateOfBirth");
        PatientListRules.IdentityGapKey(PatientIdentityGap.Gender | PatientIdentityGap.DateOfBirth)
            .Should().Be("PatientList.GapGenderAndDateOfBirth");
        PatientListRules.IdentityGapKey(PatientIdentityGap.Provisional | PatientIdentityGap.DateOfBirth)
            .Should().Be("PatientList.GapProvisional");
    }

    [Fact]
    public void Born_on_agrees_with_the_sex()
    {
        PatientListRules.BornOnKey(Gender.Male).Should().Be("PatientList.BornOnMale");
        PatientListRules.BornOnKey(Gender.Female).Should().Be("PatientList.BornOnFemale");
        PatientListRules.BornOnKey(Gender.Unknown).Should().Be("OrderEntry.BornOn");
    }

    [Fact]
    public void Order_pills_follow_the_status_and_the_priority()
    {
        PatientListRules.StatusTone(LabOrderStatus.Registered).Should().Be(PatientListTone.Waiting);
        PatientListRules.StatusTone(LabOrderStatus.PartiallyCollected).Should().Be(PatientListTone.Running);
        PatientListRules.StatusTone(LabOrderStatus.Reported).Should().Be(PatientListTone.Done);
        PatientListRules.StatusTone(LabOrderStatus.Cancelled).Should().Be(PatientListTone.Waiting);

        PatientListRules.PriorityTone(OrderPriority.Routine).Should().Be(PatientListTone.None);
        PatientListRules.PriorityTone(OrderPriority.Urgent).Should().Be(PatientListTone.Warning);
        PatientListRules.PriorityTone(OrderPriority.VitalEmergency).Should().Be(PatientListTone.Critical);
    }
}
