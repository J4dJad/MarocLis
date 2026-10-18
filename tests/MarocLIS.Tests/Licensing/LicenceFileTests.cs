using System.Text.Json.Nodes;
using FluentAssertions;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Licensing;
using Xunit;
using static MarocLIS.Tests.Licensing.LicenceTestKit;

namespace MarocLIS.Tests.Licensing;

/// <summary>
/// The licence file: written by the generator, signed with the vendor's key, read back and verified
/// by MarocLIS against the key it embeds — and refused once a single character of it is changed.
/// </summary>
public sealed class LicenceFileTests
{
    [Fact]
    public void ASignedLicence_IsReadBackWhole_AndVerifies()
    {
        var terms = Annual(365, LicenceModules.Instruments, LicenceModules.Arabic);
        var text = Sign(terms);

        var parsed = LicenceFile.Parse(text);

        parsed.IsSuccess.Should().BeTrue();
        parsed.Value.Terms.Should().BeEquivalentTo(LicenceFile.Normalised(terms));
        using var key = PublicKey();
        LicenceFile.Verify(parsed.Value, key).Should().BeTrue();
    }

    [Fact]
    public void TheFile_ListsEveryModuleByName_InFrench()
    {
        var json = JsonNode.Parse(Sign(Annual(365, LicenceModules.MobileValidation, LicenceModules.Instruments)))!;

        json["numero"]!.GetValue<string>().Should().Be("LIC-2026-0042");
        json["type"]!.GetValue<string>().Should().Be("Annuelle");
        json["modules"]!.AsArray().Select(m => m!.GetValue<string>())
            .Should().Equal("Instruments", "ValidationMobile");
        json["signature"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("postes", 50)]
    [InlineData("fin", "2099-12-31")]
    [InlineData("serveur", null)]
    [InlineData("laboratoire", "Autre laboratoire")]
    public void AChangedField_BreaksTheSignature(string field, object? value)
    {
        var json = JsonNode.Parse(Sign(Annual()))!.AsObject();
        json[field] = value is null ? null : JsonValue.Create(value);

        var status = Evaluate(Facts(json.ToJsonString()));

        status.State.Should().Be(LicenceState.Invalid);
        status.Problem!.ResourceKey.Should().Be("Licence_SignatureInvalid");
        status.Terms.Should().BeNull("terms whose signature does not hold are not shown as the vendor's");
        status.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void AModuleAddedByHand_BreaksTheSignature()
    {
        var json = JsonNode.Parse(Sign(Annual()))!.AsObject();
        json["modules"]!.AsArray().Add("Paie");

        Evaluate(Facts(json.ToJsonString())).Problem!.ResourceKey.Should().Be("Licence_SignatureInvalid");
    }

    [Fact]
    public void ALicenceSignedWithAnotherKey_IsRefused()
    {
        var status = Evaluate(Facts(SignWithStranger(Annual())));

        status.State.Should().Be(LicenceState.Invalid);
        status.Problem!.ResourceKey.Should().Be("Licence_SignatureInvalid");
    }

    [Fact]
    public void TheIndentationAndTheOrderOfTheFields_DoNotMatter()
    {
        var json = JsonNode.Parse(Sign(Annual(30, LicenceModules.Quality)))!.AsObject();
        var reordered = new JsonObject();
        foreach (var (name, value) in json.Reverse()) reordered[name] = value?.DeepClone();

        var status = Evaluate(Facts(reordered.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })));

        status.State.Should().Be(LicenceState.Valid, "the signature is over the terms, not over the file's bytes");
    }

    [Theory]
    [InlineData("", "Licence_FileEmpty")]
    [InlineData("pas du json", "Licence_FileUnreadable")]
    [InlineData("[1, 2]", "Licence_FileUnreadable")]
    public void AFileThatIsNotALicence_IsAFailure_NotAnException(string text, string key)
    {
        LicenceFile.Parse(text).FirstError.ResourceKey.Should().Be(key);
    }

    [Fact]
    public void AnUnknownField_IsRefused_RatherThanIgnored()
    {
        var json = JsonNode.Parse(Sign(Annual()))!.AsObject();
        json["illimite"] = true;

        LicenceFile.Parse(json.ToJsonString()).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void AMissingSignature_IsSaidSo()
    {
        var json = JsonNode.Parse(Sign(Annual()))!.AsObject();
        json.Remove("signature");

        LicenceFile.Parse(json.ToJsonString()).FirstError.ResourceKey.Should().Be("Licence_SignatureMissing");
    }

    [Fact]
    public void TheShippedKey_IsTheDevelopmentKey_WhosePrivateHalfIsTheTestFixture()
    {
        var trust = new LicenceTrust();

        trust.IsDevelopmentKey.Should().BeTrue("the repository ships the development key; a release replaces it");
        trust.Fingerprint.Should().Be(LicenceKeys.DevelopmentFingerprint);

        using var fixture = PrivateKey();
        LicenceKeys.Fingerprint(fixture).Should().Be(trust.Fingerprint);
    }

    [Fact]
    public void AKeyPair_ExportsAPublicHalf_ThatVerifiesWhatThePrivateOneSigned()
    {
        using var pair = LicenceKeys.CreatePair();
        var text = LicenceFile.Sign(Annual(), pair);

        using var reread = LicenceKeys.ImportPublicKey("Clé de l'éditeur\n" + LicenceKeys.ExportPublicKey(pair));
        LicenceFile.Verify(LicenceFile.Parse(text).Value, reread).Should().BeTrue();
        LicenceKeys.Fingerprint(reread).Should().Be(LicenceKeys.Fingerprint(pair)).And.MatchRegex("^([0-9A-F]{4}-){7}[0-9A-F]{4}$");
    }

    [Fact]
    public void TheRequestFile_CarriesTheServerAndTheSite_AndIsReadBack()
    {
        var request = new LicenceRequest
        {
            ServerId = ServerId,
            Site = "Laboratoire Al Amal",
            Laboratory = "LABO AL AMAL SARL",
            CurrentNumber = "LIC-2026-0042",
            CurrentVersion = 2,
            AppVersion = "1.4.0",
            CreatedAt = new DateTimeOffset(2026, 10, 18, 9, 30, 0, TimeSpan.FromHours(1))
        };

        var back = LicenceRequest.Parse(request.Write());

        back.IsSuccess.Should().BeTrue();
        back.Value.Should().BeEquivalentTo(request);
        LicenceRequest.Parse(Sign(Annual())).FirstError.ResourceKey.Should().Be("Licence_RequestUnreadable");
    }

    [Fact]
    public void TheServerId_DependsOnTheMachineTheInstanceAndTheDatabase()
    {
        var installation = Guid.NewGuid();
        var id = ServerIdentity.Compute("LABSRV", null, installation);

        id.Should().MatchRegex("^([0-9A-F]{4}-){4}[0-9A-F]{4}$");
        ServerIdentity.Compute("labsrv", "MSSQLSERVER", installation)
            .Should().Be(id, "the default instance and the case of the name change nothing");
        ServerIdentity.Compute("LABSRV2", null, installation).Should().NotBe(id, "the database copied to another server");
        ServerIdentity.Compute("LABSRV", "SQLEXPRESS", installation).Should().NotBe(id);
        ServerIdentity.Compute("LABSRV", null, Guid.NewGuid()).Should().NotBe(id, "another database on the same server");
        ServerIdentity.Normalise(" a1b2-c3d4-e5f6-0718-293a ").Should().Be(ServerId);
    }
}
