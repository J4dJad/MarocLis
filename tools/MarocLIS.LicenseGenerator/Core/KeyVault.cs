using System.Security.Cryptography;
using System.Text;
using MarocLIS.Domain.Licensing;

namespace MarocLIS.LicenseGenerator.Core;

/// <summary>
/// The vendor's key pair on this computer: created once, the private half kept only encrypted with
/// a passphrase, a backup that can be carried away, and the public half for MarocLIS to embed.
/// <para>
/// The private key is written as encrypted PKCS#8 (AES-256, the passphrase stretched by PBKDF2 over
/// SHA-256): the file alone, copied off the disk or out of a backup, signs nothing. Nothing here
/// keeps the passphrase; it is asked for each time the generator starts signing.
/// </para>
/// </summary>
public sealed class KeyVault(string folder)
{
    public const string PrivateKeyFileName = "cle-privee.pem";
    public const string PublicKeyFileName = "cle-publique.pem";
    public const string BackupExtension = ".cle-sauvegarde";

    /// <summary>Long enough that guessing it is not the easy way to the key.</summary>
    public const int MinimumPassphraseLength = 12;

    // 600 000 iterations: the current OWASP figure for PBKDF2-SHA256. Unlocking takes a fraction of a
    // second, once per session; each guess costs an attacker as much.
    private static readonly PbeParameters Encryption =
        new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);

    private const string BackupMarker = "MarocLIS - sauvegarde de la cle de licence";

    public string Folder { get; } = folder;
    public string PrivateKeyFile => Path.Combine(Folder, PrivateKeyFileName);
    public string PublicKeyFile => Path.Combine(Folder, PublicKeyFileName);

    public bool Exists => File.Exists(PrivateKeyFile);

    /// <summary>The fingerprint of the key on this computer, from its public half; null before one is created.</summary>
    public string? Fingerprint()
    {
        if (!File.Exists(PublicKeyFile)) return null;
        using var key = LicenceKeys.ImportPublicKey(File.ReadAllText(PublicKeyFile));
        return LicenceKeys.Fingerprint(key);
    }

    /// <summary>
    /// Creates the key pair. Refused when one exists: a second key would sign licences that no
    /// MarocLIS already installed accepts, so replacing it is a decision made by deleting the file.
    /// </summary>
    public string Create(string passphrase)
    {
        if (Exists) throw new InvalidOperationException("Une clé existe déjà sur cet ordinateur.");
        CheckPassphrase(passphrase);

        using var key = LicenceKeys.CreatePair();
        Write(key, passphrase);
        return LicenceKeys.Fingerprint(key);
    }

    /// <summary>The private key, or null when the passphrase is not the right one.</summary>
    public ECDsa? Unlock(string passphrase)
    {
        if (!Exists) throw new InvalidOperationException("Aucune clé n'a encore été créée sur cet ordinateur.");
        return Decrypt(File.ReadAllText(PrivateKeyFile), passphrase);
    }

    /// <summary>
    /// A backup of the key in one file, encrypted with <paramref name="backupPassphrase"/> — the
    /// same passphrase or another one, to keep with the backup in a safe place.
    /// </summary>
    public static void ExportBackup(ECDsa unlocked, string backupPassphrase, string path)
    {
        ArgumentNullException.ThrowIfNull(unlocked);
        CheckPassphrase(backupPassphrase);

        var text = new StringBuilder()
            .AppendLine(BackupMarker)
            .AppendLine($"Empreinte : {LicenceKeys.Fingerprint(unlocked)}")
            .AppendLine($"Sauvegardée le {DateTime.Now:yyyy-MM-dd HH:mm}")
            .AppendLine("Clé privée chiffrée : sans la phrase de passe de la sauvegarde, ce fichier ne signe rien.")
            .AppendLine()
            .AppendLine(Encrypted(unlocked, backupPassphrase))
            .ToString();

        WriteAtomically(path, text);
    }

    /// <summary>
    /// Puts a backup back on this computer, under <paramref name="passphrase"/>. Refused on a
    /// computer that already has another key; the same key restored again is harmless.
    /// Returns the fingerprint, or null when the backup's passphrase is wrong.
    /// </summary>
    public string? RestoreBackup(string path, string backupPassphrase, string passphrase)
    {
        CheckPassphrase(passphrase);

        var text = File.ReadAllText(path);
        if (!text.StartsWith(BackupMarker, StringComparison.Ordinal))
            throw new InvalidDataException("Ce fichier n'est pas une sauvegarde de clé du générateur.");

        using var key = Decrypt(text, backupPassphrase);
        if (key is null) return null;

        var fingerprint = LicenceKeys.Fingerprint(key);
        if (Exists && Fingerprint() is { } current && current != fingerprint)
            throw new InvalidOperationException(
                $"Cet ordinateur a déjà une autre clé ({current}). Déplacez-la avant de restaurer celle-ci.");

        Write(key, passphrase);
        return fingerprint;
    }

    /// <summary>
    /// The public key as MarocLIS embeds it: the text that replaces
    /// <c>src/MarocLIS.Application/Licensing/licence-public-key.pem</c> in the source.
    /// </summary>
    public static string PublicKeyFileText(ECDsa key) =>
        new StringBuilder()
            .AppendLine("Clé publique de l'éditeur qui vérifie les licences, intégrée à MarocLIS à la compilation.")
            .AppendLine($"Empreinte : {LicenceKeys.Fingerprint(key)}")
            .AppendLine("Sa clé privée reste chiffrée sur l'ordinateur du Générateur de licences et dans sa sauvegarde.")
            .AppendLine()
            .Append(LicenceKeys.ExportPublicKey(key)).AppendLine()
            .ToString();

    public static void CheckPassphrase(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < MinimumPassphraseLength)
            throw new ArgumentException(
                $"La phrase de passe doit compter au moins {MinimumPassphraseLength} caractères.", nameof(passphrase));
    }

    private void Write(ECDsa key, string passphrase)
    {
        Directory.CreateDirectory(Folder);
        WriteAtomically(PrivateKeyFile, Encrypted(key, passphrase) + Environment.NewLine);
        WriteAtomically(PublicKeyFile, PublicKeyFileText(key));
    }

    private static string Encrypted(ECDsa key, string passphrase) =>
        key.ExportEncryptedPkcs8PrivateKeyPem(passphrase, Encryption);

    private static ECDsa? Decrypt(string text, string passphrase)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromEncryptedPem(text, passphrase);
            return key;
        }
        catch (CryptographicException)
        {
            key.Dispose();
            return null;
        }
    }

    // A key half written when the disk fills or the power fails would be a key lost.
    internal static void WriteAtomically(string path, string text)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}
