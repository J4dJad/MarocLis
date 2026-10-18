using System.Security.Cryptography;

namespace MarocLIS.Domain.Licensing;

/// <summary>The key pair behind the licences: created, read and recognised.</summary>
public static class LicenceKeys
{
    /// <summary>
    /// The fingerprint of the development key shipped in the repository, whose private half is a
    /// test fixture anyone with the source can read. A MarocLIS built with it accepts licences
    /// anyone with the source can sign: the installer's build refuses it, and the licence tab says so.
    /// </summary>
    public const string DevelopmentFingerprint = "CDC1-D2A2-222A-4A92-B835-95FB-909A-18BB";

    /// <summary>A new P-256 key pair, the curve the licences are signed on.</summary>
    public static ECDsa CreatePair() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>Reads a public key in PEM (« -----BEGIN PUBLIC KEY----- »); text around the block is ignored.</summary>
    public static ECDsa ImportPublicKey(string pem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);

        var key = ECDsa.Create();
        key.ImportFromPem(pem);

        if (key.KeySize != 256)
        {
            key.Dispose();
            throw new CryptographicException("The licence key is not a P-256 key.");
        }

        return key;
    }

    /// <summary>The public half in PEM, for MarocLIS to embed.</summary>
    public static string ExportPublicKey(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.ExportSubjectPublicKeyInfoPem();
    }

    /// <summary>
    /// A short, readable digest of the public key (the first 16 bytes of the SHA-256 of its
    /// SubjectPublicKeyInfo): what the generator, the licence tab and the installer's build compare
    /// to know they speak of the same key.
    /// </summary>
    public static string Fingerprint(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var hash = SHA256.HashData(key.ExportSubjectPublicKeyInfo());
        var hex = Convert.ToHexString(hash, 0, 16);
        return string.Join("-", Enumerable.Range(0, 8).Select(i => hex.Substring(i * 4, 4)));
    }
}
