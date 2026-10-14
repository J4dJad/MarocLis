using FluentAssertions;
using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.HumanResources;
using Xunit;

namespace MarocLIS.Tests.Domain;

public sealed class PersonnelTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);

    private static Employee Technician() =>
        new("M0001", "ALAMI", "Sara", EmployeeJob.Technician, new DateOnly(2024, 3, 1), Guid.NewGuid());

    [Theory]
    [InlineData("181330000123456789012345", false)]
    [InlineData("1234", false)]
    [InlineData("011780000012345678901235", false)]
    public void A_rib_must_have_24_digits_and_a_correct_key(string rib, bool valid)
    {
        Employee.IsValidRib(rib).Should().Be(valid);
    }

    [Fact]
    public void A_rib_whose_key_makes_it_divisible_by_97_is_accepted()
    {
        // Build one: 22 digits, then the key that makes the whole number a multiple of 97.
        const string body = "0117800000123456789012";
        var remainder = body.Aggregate(0, (r, c) => (r * 10 + (c - '0')) % 97);
        var key = (97 - remainder * 100 % 97) % 97;

        Employee.IsValidRib(body + key.ToString("00")).Should().BeTrue();
        Employee.IsValidRib(body + ((key + 1) % 97).ToString("00")).Should().BeFalse();
    }

    [Fact]
    public void A_fixed_term_contract_needs_an_end_date()
    {
        var e = Technician();
        e.ContractType = ContractType.FixedTerm;

        e.Validate().FirstError.ResourceKey.Should().Be("Employee_ContractEndRequired");

        e.ContractEndDate = new DateOnly(2027, 2, 28);
        e.Validate().IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Exposed_posts_follow_the_job()
    {
        Employee.IsExposedByDefault(EmployeeJob.Cleaner).Should().BeTrue();
        Employee.IsExposedByDefault(EmployeeJob.Courier).Should().BeTrue();
        Employee.IsExposedByDefault(EmployeeJob.Secretary).Should().BeFalse();
    }

    [Fact]
    public void A_new_level_closes_the_previous_decision_instead_of_overwriting_it()
    {
        var e = Technician();
        var analyser = Guid.NewGuid();
        var biologist = Guid.NewGuid();

        e.Grant(HabilitationActivity.InstrumentOperation, analyser, HabilitationLevel.InTraining,
                Today.AddMonths(-2), null, biologist, "Formation constructeur").IsSuccess.Should().BeTrue();
        e.Grant(HabilitationActivity.InstrumentOperation, analyser, HabilitationLevel.Authorized,
                Today, Today.AddYears(1), biologist, "Évaluation pratique").IsSuccess.Should().BeTrue();

        e.Habilitations.Should().HaveCount(2);
        e.Habilitations.Count(h => h.IsActive).Should().Be(1);
        e.Habilitations.Single(h => h.IsActive).Level.Should().Be(HabilitationLevel.Authorized);
    }

    [Fact]
    public void An_analyser_habilitation_must_name_the_analyser()
    {
        Technician().Grant(HabilitationActivity.InstrumentOperation, null, HabilitationLevel.Authorized,
                           Today, null, Guid.NewGuid(), null)
                    .FirstError.ResourceKey.Should().Be("Habilitation_SubjectRequired");
    }

    [Fact]
    public void A_departure_ends_every_habilitation_and_blocks_new_ones()
    {
        var e = Technician();
        e.Grant(HabilitationActivity.AdultCollection, null, HabilitationLevel.Authorized, Today.AddYears(-1),
                null, Guid.NewGuid(), null);

        e.RecordExit(Today, ExitReason.Resignation, null).IsSuccess.Should().BeTrue();

        e.Habilitations.Should().OnlyContain(h => !h.IsActive);
        e.Grant(HabilitationActivity.AdultCollection, null, HabilitationLevel.Authorized, Today, null,
                Guid.NewGuid(), null).FirstError.ResourceKey.Should().Be("Habilitation_EmployeeExited");
        e.RecordExit(Today, ExitReason.Resignation, null).FirstError.ResourceKey.Should().Be("Employee_AlreadyExited");
    }

    private static EmployeeDeadlineFacts Facts(
        EmployeeJob job = EmployeeJob.Technician, bool exposed = true, DateOnly? clause = null,
        DateOnly? contractEnd = null,
        params (CredentialKind, string, DateOnly?, MedicalFitness?, DateOnly?)[] credentials) =>
        new(Guid.NewGuid(), "M0001", "ALAMI Sara", job, exposed, clause ?? Today.AddYears(-1),
            ContractType.Permanent, contractEnd, null, credentials, []);

    [Fact]
    public void An_exposed_employee_without_hepatitis_b_or_hiring_visit_shows_both_as_missing()
    {
        var rows = PersonnelDeadlines.Compute([Facts()], Today, 60);

        rows.Select(r => r.Kind).Should().BeEquivalentTo(
            [DeadlineKind.MissingHepatitisB, DeadlineKind.MissingMedicalVisit]);
        rows.Should().OnlyContain(r => r.IsOverdue && r.DueOn == null);
    }

    [Fact]
    public void A_renewed_document_hides_the_expired_one()
    {
        var rows = PersonnelDeadlines.Compute(
        [
            Facts(EmployeeJob.Courier, credentials:
            [
                (CredentialKind.HepatitisBVaccination, "Vaccination hépatite B", Today.AddYears(-1), null, null),
                (CredentialKind.HepatitisBVaccination, "Vaccination hépatite B", Today.AddYears(4), null, null),
                (CredentialKind.DrivingLicence, "Permis B", Today.AddDays(20), null, null),
                (CredentialKind.MedicalVisit, "Visite d'embauche", Today.AddDays(200), MedicalFitness.Fit, Today.AddDays(-165))
            ])
        ], Today, 60);

        rows.Should().ContainSingle();
        rows[0].Kind.Should().Be(DeadlineKind.CredentialExpiry);
        rows[0].Subject.Should().Be("Permis B");
        rows[0].IsOverdue.Should().BeFalse();
    }

    [Fact]
    public void A_biologist_must_be_registered_with_the_order_and_an_unfit_decision_is_flagged()
    {
        var rows = PersonnelDeadlines.Compute(
        [
            Facts(EmployeeJob.Biologist, credentials:
            [
                (CredentialKind.HepatitisBVaccination, "Vaccination hépatite B", null, null, null),
                (CredentialKind.MedicalVisit, "Visite périodique", null, MedicalFitness.Unfit, Today.AddDays(-3))
            ])
        ], Today, 60);

        rows.Select(r => r.Kind).Should().BeEquivalentTo(
            [DeadlineKind.MissingOrderRegistration, DeadlineKind.MedicallyUnfit]);
    }
}
