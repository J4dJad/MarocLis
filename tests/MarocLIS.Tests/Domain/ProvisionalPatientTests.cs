using FluentAssertions;
using MarocLIS.Domain.Patients;
using Xunit;

namespace MarocLIS.Tests.Domain;

public sealed class ProvisionalPatientTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);

    [Fact]
    public void Provisional_file_is_named_after_its_number_and_valid_without_a_birth_date()
    {
        var patient = Patient.Provisional("P-2026-000431", Gender.Male, 40, Guid.NewGuid());

        patient.LastName.Should().Be("X-URG");
        patient.FirstName.Should().Be("000431");
        patient.IsProvisionalIdentity.Should().BeTrue();
        patient.DeclaredAgeYears.Should().Be(40);
        patient.Validate(Today).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Provisional_minor_needs_no_guardian_until_identified()
    {
        var patient = Patient.Provisional("P-2026-000432", Gender.Female, 8, Guid.NewGuid());

        // Age declared only: no birth date, so a minor is not detected from it either way; give a
        // birth date to exercise the guardian rule.
        patient.DateOfBirth = Today.AddYears(-8);

        patient.Validate(Today).IsSuccess.Should().BeTrue();

        patient.ConfirmIdentity();
        patient.LastName = "Alaoui";
        patient.FirstName = "Sara";

        patient.Validate(Today).Errors.Should().Contain(e => e.ResourceKey == "Patient_MinorRequiresGuardian");
    }
}
