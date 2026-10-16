using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Sequencing;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesSecurityTests;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// The numbering format of a series: a token left unclosed was accepted, and every document of the
/// series then carried the same number, with the format frozen at the first one.
/// </summary>
public sealed class Cycle1SequenceFormatTests
{
    private static (ConfigurationService Service, NumberSequence Sequence) Build()
    {
        var sequence = new NumberSequence(SequencePurpose.Invoice, null, 2026, "FAC/{YEAR}/{SEQ:6}");

        var guard = Stub<IPermissionGuard>.Create(new()
        {
            [nameof(IPermissionGuard.Require)] = _ => Result.Success()
        });
        var writer = Stub<IConfigurationWriter>.Create(new()
        {
            [nameof(IConfigurationWriter.GetSequenceAsync)] = _ => Task.FromResult<NumberSequence?>(sequence)
        });
        var unitOfWork = Stub<IUnitOfWork>.Create(new()
        {
            [nameof(IUnitOfWork.SaveChangesAsync)] = _ => Task.FromResult(1)
        });
        var auditor = Stub<ISecurityAuditor>.Create(new()
        {
            [nameof(ISecurityAuditor.LogAsync)] = _ => Task.CompletedTask
        });

        var service = new ConfigurationService(Stub<IRepository<LabSite>>.Create(), Stub<IRepository<AppUser>>.Create(),
                                               Stub<IAdministrationQueries>.Create(new()
                                               {
                                                   // The format is now measured against the longest site code.
                                                   [nameof(IAdministrationQueries.GetSiteDetailsAsync)] =
                                                       _ => Task.FromResult<IReadOnlyList<SiteDetail>>([])
                                               }), writer, unitOfWork, guard,
                                               auditor, Stub<IClock>.Create());
        return (service, sequence);
    }

    [Theory]
    [InlineData("FAC-{SEQ")]
    [InlineData("FAC-{SEQ:6")]
    [InlineData("FAC-{YEAR}")]
    public async Task AFormatWithoutAWholeSequenceToken_IsRefused(string format)
    {
        var (service, sequence) = Build();

        var result = await service.SetSequenceFormatAsync(Guid.NewGuid(), format);

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Sequence_FormatMustContainSeq");
        sequence.Format.Should().Be("FAC/{YEAR}/{SEQ:6}");
    }

    [Theory]
    [InlineData("FAC-{SEQ}", "FAC-000001")]
    [InlineData("FAC/{YY}/{SEQ:4}", "FAC/26/0001")]
    public async Task AFormatWithTheToken_IsKept_AndNumbersFromIt(string format, string first)
    {
        var (service, sequence) = Build();

        var result = await service.SetSequenceFormatAsync(Guid.NewGuid(), format);

        result.IsSuccess.Should().BeTrue();
        sequence.Allocate(new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero), null).Value.Should().Be(first);
    }
}
