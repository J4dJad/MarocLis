using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Quality;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Treasury;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Samples;
using MarocLIS.Localization;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Small defects found while writing the user manual: a cancelled corrective action blocking the
/// closure of a non-conformity, reservations printed with the defects' internal names, the QC
/// derogation prompt stating the wrong minimum, the batch "Settled" answer that recorded no money
/// and the till sign-off offered on one's own till.
/// </summary>
public sealed class ReviewFixesPetitsDefautsTests
{
    private static readonly DateOnly Today = new(2026, 10, 16);
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 10, 0, 0, TimeSpan.FromHours(1));

    // ---- Non-conformities: a cancelled action counts as finished --------------------------------

    private static NonConformity MinorFile() =>
        new("NC-2026-0001", Guid.NewGuid(), NonConformityCategory.Analytical, NonConformitySeverity.Minor,
            MultilingualText.FromFrench("Écart"), Now.AddDays(-3), Guid.NewGuid());

    private static CorrectiveAction Action() =>
        new(ActionKind.Correction, MultilingualText.FromFrench("Action"), Guid.NewGuid(), Today.AddDays(7));

    [Fact]
    public void ACancelledAction_NoLongerBlocksTheClosure()
    {
        var file = MinorFile();
        var done = file.AddAction(Action());
        var wrong = file.AddAction(Action());

        done.Complete(Today, Guid.NewGuid(), null).IsSuccess.Should().BeTrue();
        wrong.Cancel("Saisie en double");

        file.AllActionsFinished.Should().BeTrue();
        file.Close(Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        file.Status.Should().Be(NonConformityStatus.Closed);
    }

    [Fact]
    public void APendingAction_StillBlocksTheClosure()
    {
        var file = MinorFile();
        file.AddAction(Action()).Complete(Today, Guid.NewGuid(), null);
        file.AddAction(Action());

        file.Close(Now, Guid.NewGuid()).FirstError.ResourceKey.Should().Be("Nc_AllActionsMustBeFinished");
    }

    [Fact]
    public void AFileWhoseActionsWereAllCancelled_CannotClose()
    {
        var file = MinorFile();
        file.AddAction(Action()).Cancel("Erreur");

        file.Close(Now, Guid.NewGuid()).FirstError.ResourceKey
            .Should().Be("Nc_CompletedActionRequiredBeforeClosure");
        file.IsOpen.Should().BeTrue();
    }

    [Fact]
    public async Task CancellingTheLastPendingAction_MovesTheFileToVerification()
    {
        var file = MinorFile();
        file.AddAction(Action()).Complete(Today, Guid.NewGuid(), null);
        var pending = file.AddAction(Action());

        var service = Build<NonConformityService>(
            (typeof(IRepository<NonConformity>), Stub<IRepository<NonConformity>>.With(
                ("GetByIdWithDetailsAsync", args => Task.FromResult<NonConformity?>(
                    (Guid)args![0]! == file.Id ? file : null)))));

        (await service.CancelActionAsync(file.Id, pending.Id, "Remplacée")).IsSuccess.Should().BeTrue();

        file.Status.Should().Be(NonConformityStatus.AwaitingVerification);
    }

    [Fact]
    public async Task CancellingTheOnlyAction_LeavesTheFileInProgress()
    {
        var file = MinorFile();
        var only = file.AddAction(Action());

        var service = Build<NonConformityService>(
            (typeof(IRepository<NonConformity>), Stub<IRepository<NonConformity>>.With(
                ("GetByIdWithDetailsAsync", _ => Task.FromResult<NonConformity?>(file)))));

        (await service.CancelActionAsync(file.Id, only.Id, "Erreur")).IsSuccess.Should().BeTrue();

        file.Status.Should().Be(NonConformityStatus.InProgress);
    }

    // ---- Report: reservations named in the report's language ------------------------------------

    [Fact]
    public void AReservation_NamesEachDefectThroughTheLabels()
    {
        var reservation = new ReportReservation("T0001", SampleDefect.Haemolysed | SampleDefect.Icteric);

        reservation.Describe(key => $"<{key}>")
            .Should().Be("T0001 : <SampleDefect.Haemolysed>, <SampleDefect.Icteric>");
    }

    [Theory]
    [InlineData(AppLanguage.French, "T0001 : Hémolysé, Ictérique")]
    [InlineData(AppLanguage.English, "T0001 : Haemolysed, Icteric")]
    public void AReservation_PrintsTheLocalisedDefectNames(AppLanguage language, string expected)
    {
        var localizer = new JsonLocalizer(new LocalizationOptions { MarkMissingKeys = true });
        var reservation = new ReportReservation("T0001", SampleDefect.Haemolysed | SampleDefect.Icteric);

        reservation.Describe(key => localizer.Get(key, language)).Should().Be(expected);
    }

    // ---- Stock: the derogation minimum is the one the prompt states -----------------------------

    [Fact]
    public void TheQcDerogation_NeedsTheStatedMinimumLength()
    {
        var lot = new ReagentLot("L1", Today.AddYears(1), 5m, Today);

        lot.ReleaseOverridingQc(Today, Guid.NewGuid(), "CQ fournisseur", "  Trop court  ")
           .FirstError.ResourceKey.Should().Be("Lot_QcOverrideJustificationTooShort");
        lot.IsQuarantined.Should().BeTrue();

        lot.ReleaseOverridingQc(Today, Guid.NewGuid(), "CQ fournisseur",
                                new string('x', ReagentLot.QcOverrideJustificationMinLength))
           .IsSuccess.Should().BeTrue();

        ReagentLot.QcOverrideJustificationMinLength.Should().Be(15);
    }

    // ---- Billing: "Settled" is not a batch answer -----------------------------------------------

    [Fact]
    public async Task ABatchAnswer_RefusesSettled_WhichWouldRecordNoMoney()
    {
        var service = Build<BillingService>();

        var result = await service.RecordClaimOutcomeAsync([Guid.NewGuid()], ClaimStatus.Settled);

        result.FirstError.ResourceKey.Should().Be("Claim_SettledRecordedPerInvoice");
    }

    // ---- Till sign-off: not offered on one's own till -------------------------------------------

    private static CashSessionReviewRow Row(Guid operatorId, bool reconciled = false) =>
        new(Guid.NewGuid(), "Caissier", null, Now.AddHours(-8), Now, 500m, 1_200m, 1_200m, 0m, null,
            0m, 0m, reconciled, null, null, null, operatorId);

    [Fact]
    public void ATill_CannotBeSignedOffByItsOperator()
    {
        var cashier = Guid.NewGuid();
        var supervisor = Guid.NewGuid();

        Row(cashier).CanBeReconciledBy(cashier).Should().BeFalse();
        Row(cashier).CanBeReconciledBy(supervisor).Should().BeTrue();
        Row(cashier, reconciled: true).CanBeReconciledBy(supervisor).Should().BeFalse();
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private sealed class Stub<T> : ReviewFixesBilling2Tests.Stub<T> where T : class;

    /// <summary>
    /// Builds a service with a stub for every dependency (permissions granted, nothing found), the
    /// given ones excepted.
    /// </summary>
    private static T Build<T>(params (Type Type, object Instance)[] overrides)
    {
        var constructor = typeof(T).GetConstructors().Single();

        var args = constructor.GetParameters().Select(p =>
        {
            var given = overrides.FirstOrDefault(o => o.Type == p.ParameterType);
            if (given.Instance is not null) return given.Instance;

            if (p.ParameterType == typeof(IClock)) return new FixedClock();

            return typeof(ReviewFixesBilling2Tests.Stub<>).MakeGenericType(p.ParameterType)
                       .GetMethod("Create")!.Invoke(null, null);
        }).ToArray();

        return (T)constructor.Invoke(args);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => ReviewFixesPetitsDefautsTests.Now;
        public DateOnly Today => ReviewFixesPetitsDefautsTests.Today;
    }
}
