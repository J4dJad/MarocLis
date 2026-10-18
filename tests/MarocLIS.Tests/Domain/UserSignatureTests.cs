using System.Buffers.Binary;
using System.IO.Compression;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesSecurityTests;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// The handwritten signature printed on released reports: only a biologist's record carries one,
/// and only a whole PNG of a signature's size is stored.
/// </summary>
public sealed class UserSignatureTests
{
    [Fact]
    public async Task A_signature_is_refused_for_an_account_that_is_not_a_biologist()
    {
        var fixture = new Fixture();
        var technician = fixture.Add(ProfessionalQualification.None);

        var result = await fixture.Service.SetSignatureAsync(technician.Id, Png(300, 100));

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("User_SignatureRequiresBiologist");
        result.FirstError.Code.Should().Be("regulatory");
        technician.SignaturePng.Should().BeNull();
        fixture.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Bytes_that_are_not_a_whole_png_are_refused()
    {
        var fixture = new Fixture();
        var biologist = fixture.Add(ProfessionalQualification.MedicalBiologist);

        var jpegHeader = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46 }
            .Concat(new byte[200]).ToArray();
        var truncated = Png(300, 100)[..^20];

        foreach (var bytes in new[] { jpegHeader, truncated, [], new byte[64] })
        {
            var result = await fixture.Service.SetSignatureAsync(biologist.Id, bytes);

            result.IsFailure.Should().BeTrue();
            result.FirstError.ResourceKey.Should().Be("User_SignatureNotPng");
        }

        biologist.SignaturePng.Should().BeNull();
    }

    [Fact]
    public async Task A_signature_too_large_or_of_the_wrong_size_is_refused()
    {
        var fixture = new Fixture();
        var biologist = fixture.Add(ProfessionalQualification.PharmacistBiologist);

        // Noise does not compress: 1000 × 600 grey pixels stay above 500 KB.
        var heavy = Png(1000, 600, noise: true);
        heavy.Length.Should().BeGreaterThan(UserAdministrationService.MaxSignatureBytes);

        (await fixture.Service.SetSignatureAsync(biologist.Id, heavy))
            .FirstError.ResourceKey.Should().Be("User_SignatureTooLarge");

        (await fixture.Service.SetSignatureAsync(biologist.Id, Png(10, 10)))
            .FirstError.ResourceKey.Should().Be("User_SignatureBadDimensions");

        (await fixture.Service.SetSignatureAsync(biologist.Id, Png(3000, 40)))
            .FirstError.ResourceKey.Should().Be("User_SignatureBadDimensions");

        biologist.SignaturePng.Should().BeNull();
    }

    [Fact]
    public async Task A_biologist_signature_is_set_then_removed_and_both_are_in_the_security_log()
    {
        var fixture = new Fixture();
        var biologist = fixture.Add(ProfessionalQualification.MedicalBiologist);
        var png = Png(400, 120);

        var set = await fixture.Service.SetSignatureAsync(biologist.Id, png);

        set.IsSuccess.Should().BeTrue();
        biologist.SignaturePng.Should().Equal(png);

        var read = await fixture.Service.GetSignatureAsync(biologist.Id);
        read.Value.Should().Equal(png);

        var removed = await fixture.Service.RemoveSignatureAsync(biologist.Id);

        removed.IsSuccess.Should().BeTrue();
        biologist.SignaturePng.Should().BeNull();
        fixture.Saves.Should().Be(2);
        fixture.Logged.Should().Equal(SecurityEventKind.SignatureChanged, SecurityEventKind.SignatureChanged);
    }

    [Fact]
    public async Task Without_the_user_management_permission_nothing_is_changed()
    {
        var fixture = new Fixture(Permissions.Administration.ViewUsers);
        var biologist = fixture.Add(ProfessionalQualification.MedicalBiologist);

        (await fixture.Service.SetSignatureAsync(biologist.Id, Png(400, 120))).IsFailure.Should().BeTrue();
        (await fixture.Service.RemoveSignatureAsync(biologist.Id)).IsFailure.Should().BeTrue();
        biologist.SignaturePng.Should().BeNull();
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    /// <summary>A real 8-bit greyscale PNG, white or noisy, with valid chunk CRCs.</summary>
    internal static byte[] Png(int width, int height, bool noise = false)
    {
        var random = new Random(12);
        var raw = new byte[(width + 1) * height];

        for (var y = 0; y < height; y++)
        {
            var row = y * (width + 1);
            raw[row] = 0;   // filter: none
            for (var x = 0; x < width; x++)
                raw[row + 1 + x] = noise ? (byte)random.Next(256) : (byte)255;
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(raw);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;   // bit depth
        header[9] = 0;   // greyscale

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(typed);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>The user administration service over in-memory stand-ins.</summary>
    private sealed class Fixture
    {
        public Dictionary<Guid, AppUser> Users { get; } = [];
        public List<SecurityEventKind> Logged { get; } = [];
        public int Saves { get; private set; }

        public UserAdministrationService Service { get; }

        public Fixture(params string[] callerPermissions)
        {
            var granted = callerPermissions.Length > 0
                ? callerPermissions.ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal)
                  {
                      Permissions.Administration.ManageUsers, Permissions.Administration.ViewUsers
                  };

            var guard = Stub<IPermissionGuard>.Create(new()
            {
                [nameof(IPermissionGuard.Require)] = a => granted.Contains((string)a[0]!)
                    ? Result.Success()
                    : Result.Failure(Error.Forbidden("Forbidden"))
            });

            var users = Stub<IRepository<AppUser>>.Create(new()
            {
                [nameof(IRepository<AppUser>.GetByIdAsync)] = a => Task.FromResult(Users.GetValueOrDefault((Guid)a[0]!)),
                [nameof(IRepository<AppUser>.GetByIdWithDetailsAsync)] = a => Task.FromResult(Users.GetValueOrDefault((Guid)a[0]!))
            });

            var unitOfWork = Stub<IUnitOfWork>.Create(new()
            {
                [nameof(IUnitOfWork.SaveChangesAsync)] = _ => { Saves++; return Task.FromResult(1); }
            });

            var auditor = Stub<ISecurityAuditor>.Create(new()
            {
                [nameof(ISecurityAuditor.LogAsync)] = a => { Logged.Add((SecurityEventKind)a[0]!); return Task.CompletedTask; }
            });

            Service = new UserAdministrationService(
                users, Stub<IRepository<Role>>.Create(), Stub<IAdministrationQueries>.Create(),
                Stub<IAuthenticationService>.Create(), unitOfWork, guard, Stub<ICurrentUser>.Create(),
                auditor, Stub<IPasswordHasher>.Create(), new PasswordPolicy(), Stub<IClock>.Create());
        }

        public AppUser Add(ProfessionalQualification qualification)
        {
            var user = new AppUser($"user{Users.Count}", "Dr Test", "hash")
            {
                Qualification = qualification,
                ProfessionalOrderNumber = qualification == ProfessionalQualification.None ? null : "ON-1"
            };
            Users[user.Id] = user;
            return user;
        }
    }
}
