using FluentAssertions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Quality;
using Xunit;

namespace MarocLIS.Tests.Quality;

/// <summary>
/// Tests for document control: version numbering, the separation of author and approver, and the
/// rule that a single version of a document is in force at a time.
/// </summary>
public sealed class DocumentControlTests
{
    private static readonly Guid Author = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 10, 0, 0, TimeSpan.FromHours(1));

    private static ControlledDocument NewDocument() =>
        new("PR-PRE-001", new MultilingualText("Prélèvement veineux"), DocumentKind.Procedure, Guid.NewGuid());

    private static DocumentVersion ApprovedVersion(ControlledDocument document)
    {
        var version = document.AddDraft(null, Author);
        version.Content = [1, 2, 3];
        version.SubmitForReview().IsSuccess.Should().BeTrue();
        version.Approve(Now, Approver).IsSuccess.Should().BeTrue();
        return version;
    }

    [Fact]
    public void AddDraft_NumbersVersionsConsecutively()
    {
        var document = NewDocument();

        document.AddDraft(null, Author).VersionNumber.Should().Be(1);
        document.AddDraft(null, Author).VersionNumber.Should().Be(2);
    }

    [Fact]
    public void SubmitForReview_WithoutContent_IsRefused()
    {
        var version = NewDocument().AddDraft(null, Author);

        version.SubmitForReview().FirstError.ResourceKey.Should().Be("Document_ContentRequired");
    }

    [Fact]
    public void Approve_ByTheAuthor_IsRefused()
    {
        var version = NewDocument().AddDraft(null, Author);
        version.Content = [1];
        version.SubmitForReview();

        version.Approve(Now, Author).FirstError.ResourceKey.Should().Be("Document_ApproverMustDifferFromAuthor");
    }

    [Fact]
    public void MakeEffective_WithdrawsThePreviousVersion_TheDayBefore()
    {
        var document = NewDocument();
        ApprovedVersion(document);
        document.MakeEffective(1, new DateOnly(2026, 10, 13), Now, Approver).IsSuccess.Should().BeTrue();

        ApprovedVersion(document);
        document.MakeEffective(2, new DateOnly(2026, 11, 1), Now, Approver).IsSuccess.Should().BeTrue();

        document.Versions.Count(v => v.Status == DocumentVersionStatus.Effective).Should().Be(1);
        document.EffectiveVersion!.VersionNumber.Should().Be(2);

        var first = document.Versions.Single(v => v.VersionNumber == 1);
        first.Status.Should().Be(DocumentVersionStatus.Superseded);
        first.WithdrawnOn.Should().Be(new DateOnly(2026, 10, 31));
    }

    [Fact]
    public void MakeEffective_OnAVersionNotApproved_IsRefused()
    {
        var document = NewDocument();
        document.AddDraft(null, Author);

        document.MakeEffective(1, new DateOnly(2026, 10, 13), Now, Approver)
                .FirstError.ResourceKey.Should().Be("Document_MustBeApprovedBeforeEffective");
    }

    [Fact]
    public void NextReviewDue_FollowsTheEffectiveDateAndPeriod()
    {
        var document = NewDocument();
        document.ReviewPeriodMonths = 12;
        ApprovedVersion(document);
        document.MakeEffective(1, new DateOnly(2026, 10, 13), Now, Approver);

        document.NextReviewDue.Should().Be(new DateOnly(2027, 10, 13));
        document.IsReviewOverdue(new DateOnly(2027, 10, 14)).Should().BeTrue();
    }
}
