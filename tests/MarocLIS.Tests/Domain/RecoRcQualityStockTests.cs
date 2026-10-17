using FluentAssertions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Quality;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// The owner's decisions on quality, stock and purchasing, at the domain level: a file's pilot by
/// default, a version scheduled for a later date, decommissioning as final, a QC reviewed once, a
/// quotation awarded again once its order was cancelled, a cancellation that keeps the notes, and
/// a note typed only in Arabic kept.
/// </summary>
public sealed class RecoRcQualityStockTests
{
    private static readonly DateOnly Today = new(2026, 10, 16);
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ANonConformity_IsPilotedByWhoeverRaisedIt_UntilReassigned()
    {
        var raisedBy = Guid.NewGuid();
        var file = new NonConformity("NC-2026-0001", Guid.NewGuid(), NonConformityCategory.Analytical,
                                     NonConformitySeverity.Major, MultilingualText.FromFrench("CIQ rejeté"), Now, raisedBy);

        file.AssignedToUserId.Should().Be(raisedBy);

        var pilot = Guid.NewGuid();
        new NonConformity("NC-2026-0002", Guid.NewGuid(), NonConformityCategory.Analytical,
                          NonConformitySeverity.Minor, MultilingualText.FromFrench("Écart"), Now, raisedBy)
        {
            AssignedToUserId = pilot
        }.AssignedToUserId.Should().Be(pilot);
    }

    private static ControlledDocument ApprovedVersions(int count)
    {
        var document = new ControlledDocument("PR-001", MultilingualText.FromFrench("Hygiène"), DocumentKind.Procedure,
                                              Guid.NewGuid());
        for (var i = 0; i < count; i++)
        {
            var version = document.AddDraft(null, Guid.NewGuid());
            version.Content = [37, 80, 68, 70];
            version.SubmitForReview().IsSuccess.Should().BeTrue();
            version.Approve(Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        }

        return document;
    }

    [Fact]
    public void AVersionPutInForceForALaterDate_IsScheduled_AndItsPredecessorStaysInForceUntilThen()
    {
        var document = ApprovedVersions(2);
        document.MakeEffective(1, Today, Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        document.MakeEffective(2, Today.AddDays(10), Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();

        var (v1, v2) = (document.Versions.Single(v => v.VersionNumber == 1), document.Versions.Single(v => v.VersionNumber == 2));

        document.VersionInForce(Today).Should().BeSameAs(v1);
        v1.StatusOn(Today).Should().Be(DocumentVersionStatus.Effective);
        v2.StatusOn(Today).Should().Be(DocumentVersionStatus.Scheduled);

        document.VersionInForce(Today.AddDays(9)).Should().BeSameAs(v1);
        document.VersionInForce(Today.AddDays(10)).Should().BeSameAs(v2);
        v1.StatusOn(Today.AddDays(10)).Should().Be(DocumentVersionStatus.Superseded);
        v2.StatusOn(Today.AddDays(10)).Should().Be(DocumentVersionStatus.Effective);
    }

    [Fact]
    public void AReplacementStartingBeforeAScheduledVersion_EndsThePreviousOneThenToo()
    {
        var document = ApprovedVersions(3);
        document.MakeEffective(1, Today, Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        document.MakeEffective(2, Today.AddDays(30), Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        document.MakeEffective(3, Today.AddDays(5), Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();

        // Never two versions in force on the same day: v1 until day 4, v3 from day 5, v2 never.
        for (var day = 0; day <= 40; day++)
            document.Versions.Count(v => v.IsInForceOn(Today.AddDays(day))).Should().Be(1);
        document.VersionInForce(Today.AddDays(4))!.VersionNumber.Should().Be(1);
        document.VersionInForce(Today.AddDays(35))!.VersionNumber.Should().Be(3);
    }

    [Fact]
    public void ADecommissionedInstrument_CannotBeReturnedToService()
    {
        var instrument = new Instrument("AUT-1", MultilingualText.FromFrench("Automate"), EquipmentKind.Analyser, Guid.NewGuid());
        instrument.Decommission(Today, "Remplacé");

        instrument.ReturnToService(Today).FirstError.ResourceKey.Should().Be("Equipment_DecommissionedIsFinal");
        instrument.Status.Should().Be(EquipmentStatus.Decommissioned);
    }

    [Fact]
    public void AControlResult_IsReviewedOnce()
    {
        var result = new QcResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100m, Now, Guid.NewGuid());
        var reviewer = Guid.NewGuid();

        result.Review(Now, reviewer, "Recalibration").IsSuccess.Should().BeTrue();
        result.Review(Now.AddHours(1), Guid.NewGuid(), "Autre action").FirstError.ResourceKey.Should().Be("Qc_AlreadyReviewed");

        result.ReviewedByUserId.Should().Be(reviewer);
        result.CorrectiveActionTaken.Should().Be("Recalibration");
    }

    [Fact]
    public void AnAwardedQuote_IsAwardedAgainOnlyOnceItsOrderIsCancelled()
    {
        var quote = new QuoteRequest("DDV-2026-0001", Guid.NewGuid(), Today, Guid.NewGuid());
        var supplier = Guid.NewGuid();
        quote.AddItem(Guid.NewGuid(), 5m, null).IsSuccess.Should().BeTrue();
        quote.AddSupplier(supplier).IsSuccess.Should().BeTrue();
        var offer = quote.RecordOffer(supplier, Today, [new SupplierQuoteLineInput(quote.Items.Single().Id, 10m, 0.20m, 0m)]).Value;

        quote.Award(offer.Id, Today, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        quote.Award(offer.Id, Today, Guid.NewGuid()).FirstError.ResourceKey.Should().Be("Quote_NotOpen");
        quote.Award(offer.Id, Today, Guid.NewGuid(), orderCancelled: true).IsSuccess.Should().BeTrue();
        quote.Status.Should().Be(QuoteRequestStatus.Awarded);

        // A cancelled request stays closed whatever happened to an order.
        var cancelled = new QuoteRequest("DDV-2026-0002", Guid.NewGuid(), Today, Guid.NewGuid());
        cancelled.Cancel("Plus nécessaire").IsSuccess.Should().BeTrue();
        cancelled.Award(Guid.NewGuid(), Today, Guid.NewGuid(), orderCancelled: true)
                 .FirstError.ResourceKey.Should().Be("Quote_NotOpen");
    }

    [Fact]
    public void CancellingAnOrder_KeepsItsNotes_AndRecordsTheReasonApart()
    {
        var order = new PurchaseOrder("BC-2026-0001", Guid.NewGuid(), Guid.NewGuid(), Today, Guid.NewGuid())
        {
            Notes = "Livrer au laboratoire de biochimie"
        };

        order.Cancel("Fournisseur en rupture").IsSuccess.Should().BeTrue();

        order.Notes.Should().Be("Livrer au laboratoire de biochimie");
        order.CancellationReason.Should().Be("Fournisseur en rupture");
        order.Status.Should().Be(PurchaseOrderStatus.Cancelled);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("  ", " ", null)]
    public void AnOptionalTextLeftBlank_IsNoText(string? french, string? arabic, string? english) =>
        MultilingualText.FromOptional(french, arabic, english).Should().BeNull();

    [Fact]
    public void AnOptionalTextTypedOnlyInArabic_IsKept_AndStandsAsTheFrenchFallback()
    {
        var note = MultilingualText.FromOptional("  ", " تم التحقق من المعايرة ")!;
        note.Ar.Should().Be("تم التحقق من المعايرة");
        note.Fr.Should().Be("تم التحقق من المعايرة");
        note.For(AppLanguage.Arabic).Should().Be("تم التحقق من المعايرة");

        var both = MultilingualText.FromOptional(" Vérifié ", "تم")!;
        (both.Fr, both.Ar, both.En).Should().Be(("Vérifié", "تم", null));
    }
}
