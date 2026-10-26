using FluentAssertions;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Samples;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>The progress line of the "Consultation d'un dossier" screen, worked out from the file.</summary>
public sealed class OrderFileScreenTests
{
    private static readonly DateTimeOffset Registered = new(2026, 10, 15, 21, 31, 0, TimeSpan.FromHours(1));

    private static OrderItemDetail Item(LabOrderItemStatus status, int values, int entered,
                                        DateTimeOffset? validatedAt = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "CREAT", new MultilingualText("Créatininémie"), "BIOCH", status,
            OrderPriority.Routine, 7m, 4.9m, 2.1m, null, null, null, null, null, validatedAt, null, 1, null,
            status == LabOrderItemStatus.Cancelled ? Registered : null, null, false, values, entered);

    private static OrderSampleDetail Tube(SampleStatus status, DateTimeOffset? collected, DateTimeOffset? received) =>
        new(Guid.NewGuid(), Guid.NewGuid().ToString("N")[..10], new MultilingualText("Sérum"), new MultilingualText("Tube sec"), "#C9A227",
            status, 1, collected, received, null, 1);

    private static OrderDetail File(LabOrderStatus status, IReadOnlyList<OrderItemDetail> items,
                                    IReadOnlyList<OrderSampleDetail> tubes) =>
        new(Guid.NewGuid(), "2610-000013", status, OrderPriority.Routine, OrderOrigin.Walkin, Registered, "Accueil",
            null, Guid.NewGuid(), "ZIANI Amina", null, new DateOnly(1990, 1, 1), Gender.Female, null, "DEMO-000013",
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            AppLanguage.French, null, null, status == LabOrderStatus.Cancelled ? Registered : null, null, items, tubes);

    [Fact]
    public void ReceivedFile_WaitsOnItsResults_WithTheValuesEnteredSoFar()
    {
        var collected = Registered.AddMinutes(5);
        var received = Registered.AddMinutes(40);
        var file = File(LabOrderStatus.Received,
                        [Item(LabOrderItemStatus.Received, 2, 0), Item(LabOrderItemStatus.Received, 1, 0),
                         Item(LabOrderItemStatus.Cancelled, 3, 0)],
                        [Tube(SampleStatus.Received, collected, received.AddMinutes(-10)),
                         Tube(SampleStatus.Received, collected, received),
                         Tube(SampleStatus.Rejected, collected, null)]);

        var steps = FileProgress.Compute(file, reportReleasedAt: null);

        steps.Select(s => s.Stage).Should().Equal(FileStage.Registered, FileStage.Collected, FileStage.Received,
                                                  FileStage.Results, FileStage.Validated, FileStage.Reported);
        steps.Select(s => s.State).Should().Equal(FileStageState.Done, FileStageState.Done, FileStageState.Done,
                                                  FileStageState.Current, FileStageState.Ahead, FileStageState.Ahead);

        steps[0].At.Should().Be(Registered);
        // The rejected tube does not hold the file back; the latest time of the others is shown.
        steps[1].At.Should().Be(collected);
        steps[2].At.Should().Be(received);
        // The cancelled analysis's values are not counted.
        (steps[3].Done, steps[3].Total).Should().Be((0, 3));
    }

    [Fact]
    public void ATubeStillToDraw_KeepsTheLaterStagesAhead_EvenWhenTheirFiguresAreComplete()
    {
        var file = File(LabOrderStatus.PartiallyCollected,
                        [Item(LabOrderItemStatus.Validated, 1, 1, Registered.AddHours(2))],
                        [Tube(SampleStatus.Collected, Registered, null), Tube(SampleStatus.AwaitingCollection, null, null)]);

        var steps = FileProgress.Compute(file, reportReleasedAt: null);

        steps[1].State.Should().Be(FileStageState.Current);
        (steps[1].Done, steps[1].Total).Should().Be((1, 2));
        steps[1].At.Should().BeNull();
        steps.Skip(2).Should().OnlyContain(s => s.State == FileStageState.Ahead);
    }

    [Fact]
    public void ValidatedAndReleased_FileIsDoneToTheReport()
    {
        var validated = Registered.AddHours(3);
        var released = Registered.AddHours(4);
        var file = File(LabOrderStatus.Validated,
                        [Item(LabOrderItemStatus.Validated, 2, 2, validated.AddMinutes(-5)),
                         Item(LabOrderItemStatus.Validated, 1, 1, validated)],
                        [Tube(SampleStatus.Received, Registered, Registered.AddHours(1))]);

        var withoutReport = FileProgress.Compute(file, reportReleasedAt: null);
        withoutReport[4].State.Should().Be(FileStageState.Done);
        withoutReport[4].At.Should().Be(validated);
        withoutReport[5].State.Should().Be(FileStageState.Current);

        var withReport = FileProgress.Compute(file, released);
        withReport.Should().OnlyContain(s => s.State == FileStageState.Done);
        withReport[5].At.Should().Be(released);
    }

    [Fact]
    public void CancelledFile_WaitsOnNothing()
    {
        var file = File(LabOrderStatus.Cancelled,
                        [Item(LabOrderItemStatus.Cancelled, 1, 0)],
                        [Tube(SampleStatus.Discarded, null, null)]);

        var steps = FileProgress.Compute(file, reportReleasedAt: null);

        steps.Should().NotContain(s => s.State == FileStageState.Current);
        steps[0].State.Should().Be(FileStageState.Done);
    }

    [Theory]
    [InlineData(LabOrderStatus.Draft, OrderStatusTone.Waiting)]
    [InlineData(LabOrderStatus.Registered, OrderStatusTone.Waiting)]
    [InlineData(LabOrderStatus.AwaitingCollection, OrderStatusTone.Waiting)]
    [InlineData(LabOrderStatus.PartiallyCollected, OrderStatusTone.Info)]
    [InlineData(LabOrderStatus.InProgress, OrderStatusTone.Info)]
    [InlineData(LabOrderStatus.PartiallyValidated, OrderStatusTone.Info)]
    [InlineData(LabOrderStatus.Validated, OrderStatusTone.Ok)]
    [InlineData(LabOrderStatus.Reported, OrderStatusTone.Ok)]
    [InlineData(LabOrderStatus.Cancelled, OrderStatusTone.Critical)]
    public void FileTone_WaitingGrey_UnderWayBlue_ReleasedGreen_CancelledRed(LabOrderStatus status, OrderStatusTone tone) =>
        OrderFileTones.ForOrder(status).Should().Be(tone);

    [Theory]
    [InlineData(LabOrderItemStatus.Pending, OrderStatusTone.Waiting)]
    [InlineData(LabOrderItemStatus.Received, OrderStatusTone.Info)]
    [InlineData(LabOrderItemStatus.TechnicallyValidated, OrderStatusTone.Info)]
    [InlineData(LabOrderItemStatus.Validated, OrderStatusTone.Ok)]
    [InlineData(LabOrderItemStatus.Reported, OrderStatusTone.Ok)]
    [InlineData(LabOrderItemStatus.Cancelled, OrderStatusTone.Critical)]
    public void AnalysisTone_IsOnTheSameScale(LabOrderItemStatus status, OrderStatusTone tone) =>
        OrderFileTones.ForItem(status).Should().Be(tone);

    [Theory]
    [InlineData(SampleStatus.AwaitingCollection, OrderStatusTone.Warning)]
    [InlineData(SampleStatus.Collected, OrderStatusTone.Info)]
    [InlineData(SampleStatus.Received, OrderStatusTone.Info)]
    [InlineData(SampleStatus.Stored, OrderStatusTone.Ok)]
    [InlineData(SampleStatus.Rejected, OrderStatusTone.Critical)]
    [InlineData(SampleStatus.Discarded, OrderStatusTone.Critical)]
    public void TubeTone_ToDrawAmber_RefusedRed(SampleStatus status, OrderStatusTone tone) =>
        OrderFileTones.ForSample(status).Should().Be(tone);
}
