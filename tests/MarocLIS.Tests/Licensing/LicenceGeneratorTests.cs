using FluentAssertions;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Licensing;
using MarocLIS.LicenseGenerator.Core;
using Xunit;
using static MarocLIS.Tests.Licensing.LicenceTestKit;

namespace MarocLIS.Tests.Licensing;

/// <summary>
/// The licence generator's own logic: the key kept encrypted and its backup, the drafts' defaults,
/// the register's numbering, replacement, revocation and renewal follow-up.
/// </summary>
public sealed class LicenceGeneratorTests : IDisposable
{
    private const string Passphrase = "phrase de passe solide";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "maroclis-generateur-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void TheKey_IsCreatedOnce_KeptEncrypted_AndUnlockedOnlyWithItsPassphrase()
    {
        var vault = new KeyVault(_folder);

        var fingerprint = vault.Create(Passphrase);

        vault.Fingerprint().Should().Be(fingerprint);
        File.ReadAllText(vault.PrivateKeyFile).Should().Contain("BEGIN ENCRYPTED PRIVATE KEY")
            .And.NotContain("BEGIN PRIVATE KEY");
        vault.Unlock("une autre phrase de passe").Should().BeNull();
        using var key = vault.Unlock(Passphrase);
        LicenceKeys.Fingerprint(key!).Should().Be(fingerprint);

        vault.Invoking(v => v.Create(Passphrase)).Should().Throw<InvalidOperationException>("a second key would orphan every licence");
        vault.Invoking(v => new KeyVault(_folder + "2").Create("court")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ThePublicKeyFile_IsWhatMarocLisEmbeds_AndVerifiesTheGeneratorsLicences()
    {
        var vault = new KeyVault(_folder);
        vault.Create(Passphrase);
        using var key = vault.Unlock(Passphrase)!;

        var text = LicenceFile.Sign(Annual(), key);

        using var embedded = LicenceKeys.ImportPublicKey(File.ReadAllText(vault.PublicKeyFile));
        LicenceFile.Verify(LicenceFile.Parse(text).Value, embedded).Should().BeTrue();
        File.ReadAllText(vault.PublicKeyFile).Should().Contain(LicenceKeys.Fingerprint(key));
    }

    [Fact]
    public void TheBackup_IsEncryptedWithItsOwnPassphrase_AndRestoresTheSameKey()
    {
        var vault = new KeyVault(_folder);
        var fingerprint = vault.Create(Passphrase);
        using var key = vault.Unlock(Passphrase)!;
        var backup = Path.Combine(_folder, "sauvegarde" + KeyVault.BackupExtension);
        KeyVault.ExportBackup(key, "phrase de la sauvegarde", backup);

        File.ReadAllText(backup).Should().Contain("BEGIN ENCRYPTED PRIVATE KEY").And.Contain(fingerprint);

        var elsewhere = new KeyVault(Path.Combine(_folder, "autre-ordinateur"));
        elsewhere.RestoreBackup(backup, Passphrase, "nouvelle phrase locale").Should().BeNull("the backup has its own passphrase");
        elsewhere.RestoreBackup(backup, "phrase de la sauvegarde", "nouvelle phrase locale").Should().Be(fingerprint);
        elsewhere.Unlock("nouvelle phrase locale").Should().NotBeNull();

        var other = new KeyVault(Path.Combine(_folder, "deja-une-cle"));
        other.Create(Passphrase);
        other.Invoking(v => v.RestoreBackup(backup, "phrase de la sauvegarde", Passphrase))
             .Should().Throw<InvalidOperationException>("a computer that holds another key keeps it");
    }

    [Theory]
    [InlineData(LicenceKind.Annual, 364, true)]
    [InlineData(LicenceKind.Trial, 30, false)]
    public void ADraft_TakesTheDesignsProposals(LicenceKind kind, int days, bool bound)
    {
        var draft = LicenceDrafts.For(kind, "LIC-2026-0001", Today, LicencePacks.Professional);

        draft.EndsOn.Should().Be(Today.AddDays(days));
        draft.WarningDays.Should().Be(30);
        draft.GraceDays.Should().Be(15);
        draft.BenchClearingDays.Should().Be(7);
        draft.Workstations.Should().Be(5);
        draft.Modules.Should().Equal(LicencePacks.Professional.Modules);
        LicenceDrafts.BindsByDefault(kind).Should().Be(bound);
    }

    [Fact]
    public void APerpetualOrTrainingDraft_HasNoEnd()
    {
        var perpetual = LicenceDrafts.For(LicenceKind.Perpetual, "LIC-2026-0001", Today, LicencePacks.Complete);
        perpetual.EndsOn.Should().BeNull();
        perpetual.UpdatesUntil.Should().Be(Today.AddYears(1));

        var training = LicenceDrafts.For(LicenceKind.Training, "LIC-2026-0002", Today, LicencePacks.Complete);
        training.EndsOn.Should().BeNull();
        training.Workstations.Should().Be(1);
        (training with { Laboratory = "Démonstration" }).Problems().Should().BeEmpty();
        LicenceDrafts.BindsByDefault(LicenceKind.Training).Should().BeFalse();
    }

    [Fact]
    public void TheRegister_NumbersByYear_ReplacesByVersion_AndRevokes()
    {
        var registry = new LicenceRegistry();
        registry.NextNumber(2026).Should().Be("LIC-2026-0001");

        var first = Issue(registry, Annual() with { Number = "LIC-2026-0007" });
        registry.NextNumber(2026).Should().Be("LIC-2026-0008");
        registry.NextNumber(2027).Should().Be("LIC-2027-0001");

        var replacement = registry.Replacement(first, Today.AddDays(5));
        replacement.Version.Should().Be(2);
        replacement.IssuedOn.Should().Be(Today.AddDays(5));
        var second = Issue(registry, replacement);
        registry.IsCurrent(first).Should().BeFalse();
        registry.Current().Should().ContainSingle().Which.Should().Be(second);
        registry.Invoking(r => r.Add(second)).Should().Throw<InvalidOperationException>();

        var revoked = registry.Revoke(first, Today, "fichier perdu");
        LicenceRegistry.RevocationLine(revoked).Should().StartWith("LIC-2026-0007/1  # ").And.Contain("fichier perdu");
        LicenceRegistry.RevocationLine(revoked, everyVersion: true).Should().StartWith("LIC-2026-0007  #");

        // The line is what MarocLIS's revocation list reads.
        LicenceTrust.ParseRevoked(LicenceRegistry.RevocationLine(revoked))
            .Should().Contain(e => e.Contains("LIC-2026-0007", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRegister_SurvivesItsFile()
    {
        var registry = new LicenceRegistry();
        Issue(registry, Annual(365, LicenceModules.Quality) with { Number = "LIC-2026-0003" }, "M. Alami 0661");
        var path = Path.Combine(_folder, LicenceRegistry.FileName);

        registry.Save(path);
        var back = LicenceRegistry.Load(path);

        back.Licences.Should().ContainSingle();
        back.Licences[0].Terms.Should().BeEquivalentTo(registry.Licences[0].Terms);
        back.Licences[0].Contact.Should().Be("M. Alami 0661");
        LicenceFile.Parse(back.Licences[0].Content).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void TheRenewals_AreTheLicencesEndingWithin45Days_AndThoseLapsed()
    {
        var registry = new LicenceRegistry();
        Issue(registry, Annual(44) with { Number = "LIC-A" });
        Issue(registry, Annual(46) with { Number = "LIC-B" });
        Issue(registry, Annual(-3) with { Number = "LIC-C" });
        Issue(registry, Annual() with { Number = "LIC-D", Kind = LicenceKind.Perpetual, EndsOn = null, UpdatesUntil = Today.AddDays(10) });
        Issue(registry, Annual() with { Number = "LIC-E", Kind = LicenceKind.Training, EndsOn = null, Workstations = 1 });
        var renewed = Issue(registry, Annual(20) with { Number = "LIC-F" });
        Issue(registry, registry.Renewal(renewed, Today));

        registry.Renewals(Today).Select(l => l.Number).Should().Equal("LIC-C", "LIC-D", "LIC-A");

        var csv = LicenceRegistry.RenewalsCsv(registry.Renewals(Today), Today);
        csv.Should().StartWith("Numéro;Version;Laboratoire");
        csv.Should().Contain("LIC-C;1;Laboratoire Al Amal;Casablanca;Annuelle;").And.Contain(";-3;");
    }

    [Fact]
    public void ARenewal_ContinuesFromTheEnd_OrFromTodayOnceLapsed()
    {
        var registry = new LicenceRegistry();
        var running = Issue(registry, Annual(20) with { Number = "LIC-A" });
        var lapsed = Issue(registry, Annual(-40) with { Number = "LIC-B" });

        var next = registry.Renewal(running, Today);
        next.Version.Should().Be(2);
        next.StartsOn.Should().Be(Today.AddDays(21));
        next.EndsOn.Should().Be(Today.AddDays(21).AddYears(1).AddDays(-1));

        registry.Renewal(lapsed, Today).StartsOn.Should().Be(Today);
    }

    [Fact]
    public void ACsvCell_IsNeverAFormula()
    {
        var registry = new LicenceRegistry();
        Issue(registry, Annual(10) with { Number = "LIC-X", Laboratory = "=HYPERLINK(\"x\")" });

        LicenceRegistry.RenewalsCsv(registry.Renewals(Today), Today).Should().Contain("\"'=HYPERLINK(\"\"x\"\")\"");
    }

    private static IssuedLicence Issue(LicenceRegistry registry, LicenceTerms terms, string? contact = null)
    {
        var licence = new IssuedLicence
        {
            Terms = LicenceFile.Normalised(terms),
            Content = Sign(terms),
            IssuedAt = DateTimeOffset.Now,
            Contact = contact
        };
        registry.Add(licence);
        return licence;
    }
}
