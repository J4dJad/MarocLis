using FluentAssertions;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Defect-hunt cycle 3, lot c3c (reception, files, billing): a refused till closing leaves nothing
/// on the open session, and « Ajouter des analyses » is offered only where the service accepts it.
/// </summary>
public sealed class DefectCycle3cReceptionBillingTests
{
    private static readonly DateTimeOffset Opened = new(2026, 10, 16, 8, 0, 0, TimeSpan.FromHours(1));

    [Fact]
    public void AClosingRefusedForWantOfAnExplanation_LeavesTheCountOffTheOpenSession()
    {
        var session = new CashSession(Guid.NewGuid(), Guid.NewGuid(), Opened, 200m);
        session.Accumulate(PaymentMethod.Cash, 50m);

        var refused = session.Close(Opened.AddHours(8), session.OperatorUserId, 230m, "  ");

        refused.FirstError.ResourceKey.Should().Be("CashSession_VarianceExplanationRequired");
        refused.FirstError.Args.Should().ContainSingle().Which.Should().Be(-20m);
        session.Status.Should().Be(CashSessionStatus.Open);
        session.CountedCash.Should().BeNull();
        session.Variance.Should().BeNull();

        session.Close(Opened.AddHours(8), session.OperatorUserId, 230m, "Monnaie rendue en trop").IsSuccess.Should().BeTrue();
        session.CountedCash.Should().Be(230m);
        session.Variance.Should().Be(-20m);
        session.Status.Should().Be(CashSessionStatus.Closed);
    }

    [Fact]
    public void AClosingWithinTolerance_NeedsNoExplanation()
    {
        var session = new CashSession(Guid.NewGuid(), Guid.NewGuid(), Opened, 100m);

        session.Close(Opened.AddHours(8), session.OperatorUserId, 90m, null).IsSuccess.Should().BeTrue();
        session.Variance.Should().Be(-10m);
    }

    private static OrderDetail File(LabOrderStatus status, Guid? invoiceId = null) =>
        new(Guid.NewGuid(), "2610-000013", status, OrderPriority.Routine, OrderOrigin.Walkin, Opened, "Accueil",
            null, Guid.NewGuid(), "ZIANI Amina", null, new DateOnly(1990, 1, 1), Gender.Female, null, "DEMO-000013",
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            AppLanguage.French, invoiceId, invoiceId is null ? null : "FAC/PRINCIPAL/2026/000001",
            status == LabOrderStatus.Cancelled ? Opened : null, null, [], []);

    [Theory]
    [InlineData(LabOrderStatus.Registered, false, true)]
    [InlineData(LabOrderStatus.InProgress, false, true)]
    [InlineData(LabOrderStatus.Validated, false, true)]
    [InlineData(LabOrderStatus.Registered, true, false)]
    [InlineData(LabOrderStatus.Reported, false, false)]
    [InlineData(LabOrderStatus.Cancelled, false, false)]
    public void AnalysesCanBeAdded_OnlyToALiveFileNotYetInvoicedNorReported(LabOrderStatus status, bool invoiced,
                                                                             bool expected)
    {
        File(status, invoiced ? Guid.NewGuid() : null).CanAddAnalyses.Should().Be(expected);
    }
}
