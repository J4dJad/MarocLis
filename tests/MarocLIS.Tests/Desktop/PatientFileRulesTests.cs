using FluentAssertions;
using MarocLIS.Application.Input;
using MarocLIS.Application.Patients;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Patients;
using MarocLIS.Tests.Licensing;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>The rules of the patient file and list, shared by the workstation and the web.</summary>
public sealed class PatientFileRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);

    [Fact]
    public void The_age_comes_from_the_birth_date_or_the_declared_age()
    {
        PatientFileRules.AgeYears(new DateOnly(2010, 10, 21), 0, Today).Should().Be(15);
        PatientFileRules.AgeYears(new DateOnly(2010, 10, 20), 0, Today).Should().Be(16);
        PatientFileRules.AgeYears(new DateOnly(2027, 1, 1), 0, Today).Should().BeNull();
        PatientFileRules.AgeYears(null, 40, Today).Should().Be(40);
        PatientFileRules.AgeYears(null, 0, Today).Should().BeNull("a declared 0 is no age");

        PatientFileRules.IsMinor(17).Should().BeTrue();
        PatientFileRules.IsMinor(18).Should().BeFalse();
        PatientFileRules.IsMinor(null).Should().BeFalse();
        PatientFileRules.PregnancyApplies(Gender.Female).Should().BeTrue();
        PatientFileRules.PregnancyApplies(Gender.Male).Should().BeFalse();
    }

    [Fact]
    public void The_body_surface_and_the_measures_as_typed()
    {
        PatientFileRules.BodySurfaceM2("72", "170").Should().Be(1.844m);
        PatientFileRules.BodySurfaceM2("72,5", "").Should().BeNull();
        PatientFileRules.Measure("0").Should().BeNull();
        PatientFileRules.Measure("72,5").Should().Be(72.5m);
    }

    [Fact]
    public void A_save_refuses_the_names_first_then_an_unreadable_or_negative_measure()
    {
        PatientFileRules.SaveProblem(" ", "Mohammed", "", "")!.Field.Should().Be("LastName");
        PatientFileRules.SaveProblem("ALAMI", "", "", "")!.Key.Should().Be("Patient_FirstNameRequired");

        var unreadable = PatientFileRules.SaveProblem("ALAMI", "Mohammed", "72 kg", "");
        unreadable!.Field.Should().Be("Weight");
        unreadable.Key.Should().Be("Result_NotANumber");
        unreadable.Arguments.Should().Equal("72 kg");

        var negative = PatientFileRules.SaveProblem("ALAMI", "Mohammed", "72", "-1");
        negative!.Field.Should().Be("Height");
        negative.Key.Should().Be("Patient_BodyMeasureOutOfRange");

        PatientFileRules.SaveProblem("ALAMI", "Mohammed", "72", "170").Should().BeNull();
    }

    [Fact]
    public void The_file_and_the_list_offer_what_the_rights_allow()
    {
        var clerk = new LicenceTestKit.User(true, Permissions.Patients.Create, Permissions.Patients.Edit,
                                            Permissions.Orders.Create, Permissions.Orders.View);
        var reader = new LicenceTestKit.User(true, Permissions.Patients.View);
        var dpo = new LicenceTestKit.User(true, Permissions.Patients.Anonymize, Permissions.Patients.Merge);

        PatientFileRules.Actions(true, false, clerk).Should().Be(new PatientFileActions(true, true, false));
        PatientFileRules.Actions(false, false, reader).Should().Be(new PatientFileActions(false, false, false));
        PatientFileRules.Actions(false, true, clerk).Should().Be(new PatientFileActions(false, false, true));

        PatientFileRules.ListActions(true, false, 3, clerk).Should()
            .Be(new PatientListActions(true, true, true, false, false, true));
        PatientFileRules.ListActions(true, false, 1, dpo).Merge.Should().BeFalse("merging needs two files");
        PatientFileRules.ListActions(true, false, 2, dpo).Merge.Should().BeTrue();
        PatientFileRules.ListActions(true, true, 2, dpo).Should()
            .Be(new PatientListActions(false, true, false, false, false, false) { OpenKey = "Action.Consult" });
        PatientFileRules.ListActions(false, false, 2, clerk).Edit.Should().BeFalse();
    }

    [Fact]
    public void Who_may_only_read_a_file_consults_it_and_is_not_offered_to_modify_it()
    {
        // Before: the technician double-clicked a patient and got « Modifier le dossier » with every
        // field open to typing and only « Enregistrer » greyed.
        var technician = new LicenceTestKit.User(true, Permissions.Patients.View);
        var clerk = new LicenceTestKit.User(true, Permissions.Patients.View, Permissions.Patients.Create,
                                            Permissions.Patients.Edit);

        PatientFileRules.ListActions(true, false, 1, technician).OpenKey.Should().Be("Action.Consult");
        PatientFileRules.ListActions(true, false, 1, clerk).OpenKey.Should().Be("Action.Edit");

        PatientFileRules.TitleKey(false, PatientFileRules.Actions(false, false, technician)).Should().Be("Patient.ViewFile");
        PatientFileRules.TitleKey(false, PatientFileRules.Actions(false, true, clerk)).Should().Be("Patient.ViewFile");
        PatientFileRules.TitleKey(false, PatientFileRules.Actions(false, false, clerk)).Should().Be("Patient.EditFile");
        PatientFileRules.TitleKey(true, PatientFileRules.Actions(true, false, clerk)).Should().Be("Patient.NewFile");

        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        (Guid Id, bool Anonymized, int Orders)[] shown = [(a, false, 1), (b, false, 5), (c, true, 9)];
        PatientFileRules.MergeSurvivors(shown, a, p => p.Id, p => p.Anonymized, p => p.Orders)
            .Select(p => p.Id).Should().Equal(b);

        PatientFileRules.IsLoggedSearch("al").Should().BeFalse();
        PatientFileRules.IsLoggedSearch("ala").Should().BeTrue();
        PatientFileRules.AnonymizationReference("PAT-2026-0001").Should().Be("PAT20260001");
        PatientFileRules.ChecksDuplicates(false, "ALAMI", "M").Should().BeFalse();
        PatientFileRules.ChecksDuplicates(true, "ALAMI", " ").Should().BeFalse();
        PatientFileRules.ChecksDuplicates(true, "ALAMI", "M").Should().BeTrue();
    }

    [Fact]
    public void A_coverage_rate_is_read_as_typed_off_the_card()
    {
        PatientFileRules.ReadRate("").Should().Be(new TypedRate(null, null, false));
        PatientFileRules.ReadRate("70").Should().Be(new TypedRate(0.7m, null, false));
        PatientFileRules.ReadRate("70 %").Should().Be(new TypedRate(0.7m, null, false));
        PatientFileRules.ReadRate("0,7").Should().Be(new TypedRate(0.7m, null, true));
        PatientFileRules.ReadRate("1").Should().Be(new TypedRate(1m, null, true));
        PatientFileRules.ReadRate("150").ProblemKey.Should().Be("Coverage_RateOutOfRange");
        PatientFileRules.ReadRate("-5").ProblemKey.Should().Be("Coverage_RateOutOfRange");
        PatientFileRules.ReadRate("soixante").ProblemKey.Should().Be("Result_NotANumber");

        PatientFileRules.CoverageProblem("", false, null).Should().Be("Coverage_MembershipNumberRequired");
        PatientFileRules.CoverageProblem("123", true, " ").Should().Be("Coverage_PriorAgreementRequired");
        PatientFileRules.CoverageProblem("123", true, "ALD-9").Should().BeNull();
        PatientFileRules.PendingRate(true, 0.7m, 0.8m).Should().Be(1m);
        PatientFileRules.PendingRate(false, null, 0.8m).Should().Be(0.8m);
        PatientFileRules.AsksInsuredPerson(BeneficiaryRelationship.Self).Should().BeFalse();
        PatientFileRules.PayerNoticeKeys(true, true).Should()
            .Equal("Coverage.PricedOnNationalTariff", "Coverage.PrescriptionRequired");
        PatientFileRules.CanEndCoverage(null).Should().BeTrue();
    }

    [Fact]
    public void Consents_alerts_and_reasons()
    {
        var written = Enum.GetValues<ConsentPurpose>().First(PatientConsent.RequiresWrittenEvidence);
        var plain = Enum.GetValues<ConsentPurpose>().First(p => !PatientConsent.RequiresWrittenEvidence(p));

        PatientFileRules.ConsentProblem(written, ConsentChannel.Verbal, false, null).Should().Be("Order_ConsentMustBeWritten");
        PatientFileRules.ConsentProblem(plain, ConsentChannel.Verbal, true, " ").Should().Be("Patient_MinorRequiresGuardian");
        PatientFileRules.ConsentProblem(plain, ConsentChannel.Verbal, true, "Mère").Should().BeNull();
        PatientFileRules.ConsentNoticeKey(plain, ConsentChannel.Verbal, true).Should().Be("Consent.MinorSignedByGuardian");
        PatientFileRules.ConsentNoticeKey(plain, ConsentChannel.Verbal, false).Should().BeNull();
        PatientFileRules.ConsentNoticeKey(written, ConsentChannel.Verbal, false).Should().Be("Order_ConsentMustBeWritten");
        PatientFileRules.ConsentStateKey(false, true, false).Should().Be("Consent.EvidenceMissing");
        PatientFileRules.ConsentStateKey(true, true, false).Should().Be("Consent.Withdrawn");

        PatientFileRules.AlertProblem(" ").Should().Be("Alert_MessageRequired");
        PatientFileRules.AlertNeedsConfirmation(PatientAlertSeverity.Critical).Should().BeTrue();
        PatientFileRules.AlertNoticeKey(PatientAlertSeverity.Warning).Should().Be("Patient.AlertWarningNotice");

        ReasonRules.IsSufficient("abcd").Should().BeFalse();
        ReasonRules.IsSufficient(" abcde ").Should().BeTrue();
        ReasonRules.IsSufficient("même nom", ReasonRules.JustificationMinimumLength).Should().BeFalse();
    }

    [Theory]
    [InlineData("150 000,5", 150000.5)]
    [InlineData("1.500.000", 1500000)]
    [InlineData("‎-2,5", -2.5)]
    public void A_typed_number_reads_the_same_on_both_clients(string typed, double expected) =>
        TypedNumber.Parse(typed).Should().Be((decimal)expected);

    [Fact]
    public void An_unreadable_number_is_said_so()
    {
        TypedNumber.Parse("1e5").Should().BeNull();
        TypedNumber.IsUnreadable("72 kg").Should().BeTrue();
        TypedNumber.IsUnreadable("").Should().BeFalse();
    }
}
