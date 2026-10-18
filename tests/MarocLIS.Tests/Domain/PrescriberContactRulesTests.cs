using FluentAssertions;
using MarocLIS.Api.Contracts;
using MarocLIS.Api.Endpoints;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Results;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// The « Prescripteur » block of the validation screens: the name shown, the numbers listed and
/// their order, and the number a critical call is recorded against — on the workstation and in the
/// phone's dossier alike.
/// </summary>
public sealed class PrescriberContactRulesTests
{
    private static OrderPrescriber Doctor(string? critical = null, string? phone = null, string? mobile = null,
                                          string? email = null, string? fax = null) => new()
    {
        PrescriberId = Guid.NewGuid(),
        Kind = PrescriberKind.Specialist,
        LastName = "ALAOUI",
        FirstName = "Karim",
        FullNameArabic = "كريم العلوي",
        Speciality = new MultilingualText("Néphrologie", "أمراض الكلى", "Nephrology"),
        CriticalResultPhone = critical,
        Phone = phone,
        MobilePhone = mobile,
        Email = email,
        Fax = fax,
        City = "Casablanca"
    };

    [Fact]
    public void DisplayName_IsTheDoctorsName_TheArabicOneInArabic_AndTheEstablishmentsForAClinicOrALaboratory()
    {
        var doctor = Doctor();
        PrescriberContactRules.DisplayName(doctor, AppLanguage.French).Should().Be("ALAOUI Karim");
        PrescriberContactRules.DisplayName(doctor, AppLanguage.Arabic).Should().Be("كريم العلوي");
        PrescriberContactRules.DisplayName(doctor with { FullNameArabic = " " }, AppLanguage.Arabic).Should().Be("ALAOUI Karim");

        var clinic = doctor with { Kind = PrescriberKind.Institution, InstitutionName = "Clinique Al Amal", FullNameArabic = null };
        PrescriberContactRules.DisplayName(clinic, AppLanguage.French).Should().Be("Clinique Al Amal");

        var laboratory = doctor with { Kind = PrescriberKind.Laboratory, InstitutionName = "BIOLAB", FullNameArabic = null };
        PrescriberContactRules.DisplayName(laboratory, AppLanguage.English).Should().Be("BIOLAB");

        // A clinic recorded without its name still shows something.
        PrescriberContactRules.DisplayName(clinic with { InstitutionName = null }, AppLanguage.French).Should().Be("ALAOUI Karim");
    }

    [Fact]
    public void AFreeTextPrescriber_ShowsItsName_AndNoContact()
    {
        var typed = new OrderPrescriber { FreeText = "Dr Benani", Phone = "0522000000" };

        PrescriberContactRules.DisplayName(typed, AppLanguage.Arabic).Should().Be("Dr Benani");
        PrescriberContactRules.Contacts(typed).Should().BeEmpty();
        PrescriberContactRules.CriticalContact(typed).Should().BeNull();
        PrescriberContactRules.DisplayName(null, AppLanguage.French).Should().BeNull();
    }

    [Fact]
    public void Contacts_PutTheCriticalLineFirst_AndLeaveOutWhatIsNotRecorded()
    {
        var doctor = Doctor(critical: " 0522 11 22 33 ", phone: "0522 00 00 00", mobile: "  ", email: "k.alaoui@example.ma");

        PrescriberContactRules.Contacts(doctor).Should().Equal(
            new PrescriberContact(PrescriberContactKind.CriticalLine, "0522 11 22 33"),
            new PrescriberContact(PrescriberContactKind.Phone, "0522 00 00 00"),
            new PrescriberContact(PrescriberContactKind.Email, "k.alaoui@example.ma"));

        PrescriberContactRules.Contacts(Doctor()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("0522112233", "0661000000", "0522000000", "0522112233")]
    [InlineData(null, "0661000000", "0522000000", "0661000000")]
    [InlineData(" ", null, "0522000000", "0522000000")]
    [InlineData(null, null, null, null)]
    public void CriticalContact_IsTheCriticalLine_ElseTheMobile_ElseTheTelephone(
        string? critical, string? mobile, string? phone, string? expected)
    {
        PrescriberContactRules.CriticalContact(Doctor(critical: critical, phone: phone, mobile: mobile))
            .Should().Be(expected);
    }

    [Fact]
    public void ContactUsed_IsTheNumberDialled_ElseTheOneOffered_AndNoneForAValueToldInPerson()
    {
        var doctor = Doctor(critical: "0522112233", mobile: "0661000000");

        PrescriberContactRules.ContactUsed(" 0661000000 ", doctor, NotificationChannel.Telephone).Should().Be("0661000000");
        PrescriberContactRules.ContactUsed(null, doctor, NotificationChannel.Telephone).Should().Be("0522112233");
        PrescriberContactRules.ContactUsed("", doctor, NotificationChannel.InPerson).Should().BeNull();
        PrescriberContactRules.ContactUsed(null, null, NotificationChannel.Telephone).Should().BeNull();
    }

    [Fact]
    public void TheDossierSent_ToThePhone_CarriesTheBlockInTheBiologistsLanguage()
    {
        var doctor = Doctor(critical: "0522112233", phone: "0522000000", mobile: "0661000000",
                            email: "k.alaoui@example.ma", fax: "0522000001");

        ValidationEndpoints.Prescriber(doctor, AppLanguage.English).Should().Be(new PrescriberSummary(
            "ALAOUI Karim", "Nephrology", "Casablanca", "0522112233", "0522000000", "0661000000",
            "k.alaoui@example.ma", "0522000001", "0522112233"));

        ValidationEndpoints.Prescriber(new OrderPrescriber { FreeText = "Dr Benani" }, AppLanguage.French)
            .Should().Be(new PrescriberSummary("Dr Benani"));
        ValidationEndpoints.Prescriber(null, AppLanguage.French).Should().BeNull();
    }
}
