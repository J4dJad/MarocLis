using System.Text;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Inventory;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// The computed supplier evaluation (achats A7): punctuality, refusals, non-conformities, the
/// proposed score, its application to the supplier and the annual report's CSV.
/// </summary>
public sealed class AchatsSupplierEvaluationTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static SupplierDelivery Delivery(int receivedDay, int? dueDay) =>
        new(new DateOnly(2026, 3, receivedDay), dueDay is { } d ? new DateOnly(2026, 3, d) : null);

    [Fact]
    public void ADelivery_IsOnTimeUpToItsDueDate_AndLateByTheDaysAfter()
    {
        Delivery(10, 10).IsOnTime.Should().BeTrue();
        Delivery(13, 10).IsOnTime.Should().BeFalse();
        Delivery(13, 10).DelayDays.Should().Be(3);
        Delivery(5, 10).DelayDays.Should().Be(0);
        Delivery(5, null).IsOnTime.Should().BeFalse();
    }

    [Fact]
    public void APerfectYear_Scores100()
    {
        var kpis = SupplierEvaluation.Compute([Delivery(1, 2), Delivery(9, 10)], refused: 0, nonConformities: 0);

        kpis.Deliveries.Should().Be(2);
        kpis.OnTimeRatePercent.Should().Be(100m);
        kpis.AverageDelayDays.Should().Be(0m);
        kpis.ProposedScore.Should().Be(100);
    }

    [Fact]
    public void TheScore_FollowsTheFormula()
    {
        // 4 dated deliveries, 3 on time (75 %), delays 0,0,0,4 → 1 day; 1 refused of 5; 2 NC.
        var kpis = SupplierEvaluation.Compute(
            [Delivery(1, 2), Delivery(5, 5), Delivery(6, 8), Delivery(14, 10)], refused: 1, nonConformities: 2);

        kpis.OnTimeDeliveries.Should().Be(3);
        kpis.OnTimeRatePercent.Should().Be(75m);
        kpis.AverageDelayDays.Should().Be(1m);
        // 50 × 0.75 + 30 × (1 − 1/5) + (20 − 10) = 37.5 + 24 + 10 = 71.5 → 72
        kpis.ProposedScore.Should().Be(72);
    }

    [Fact]
    public void ManyNonConformities_CostAtMostTwentyPoints()
    {
        SupplierEvaluation.Compute([Delivery(1, 2)], refused: 0, nonConformities: 9)
            .ProposedScore.Should().Be(80);
    }

    [Fact]
    public void ReceiptsWithoutAnOrderDate_DoNotCountAsLate()
    {
        var kpis = SupplierEvaluation.Compute([Delivery(1, null), Delivery(2, null)], 0, 0);

        kpis.DatedDeliveries.Should().Be(0);
        kpis.OnTimeRatePercent.Should().BeNull();
        kpis.AverageDelayDays.Should().BeNull();
        kpis.ProposedScore.Should().Be(100);
    }

    [Fact]
    public void NoDeliveryAndNoRefusal_ProposesNoScore_ButOnlyRefusalsDo()
    {
        SupplierEvaluation.Compute([], 0, 1).ProposedScore.Should().BeNull();
        // Nothing accepted, one refusal: 50 + 0 + 20.
        SupplierEvaluation.Compute([], 1, 0).ProposedScore.Should().Be(70);
    }

    private static SupplierEvaluationInput Input(string name, DateOnly? registrationUntil = null,
                                                 params SupplierDelivery[] deliveries) =>
        new(Guid.NewGuid(), name[..3].ToUpperInvariant(), name, true, 60, new DateOnly(2025, 12, 1),
            "DMP-001", registrationUntil, deliveries, 0, 0);

    [Fact]
    public void TheRows_AreSortedByName_AndFlagAnExpiredRegistration()
    {
        var rows = SupplierEvaluationService.Evaluate(
            [Input("Sothema", Today.AddDays(-1), Delivery(1, 2)), Input("Biodis", Today)], Today);

        rows.Select(r => r.Name).Should().Equal("Biodis", "Sothema");
        rows[0].RegistrationExpired.Should().BeFalse();
        rows[1].RegistrationExpired.Should().BeTrue();
        rows[1].Kpis.ProposedScore.Should().Be(100);
    }

    [Fact]
    public void TheCsv_IsSemicolonSeparated_WithABom_AndFrenchNumbers()
    {
        var rows = SupplierEvaluationService.Evaluate(
            [Input("Bio; Lab", new DateOnly(2027, 1, 31), Delivery(1, 2), Delivery(5, 3), Delivery(4, 4))], Today);

        var bytes = PurchasingCsv.SupplierReport(rows, ["Code", "Fournisseur"]);

        bytes.Take(3).Should().Equal(Encoding.UTF8.GetPreamble());
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
                            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Be("Code;Fournisseur");
        lines[1].Should().StartWith("BIO;\"Bio; Lab\";3;66,7 %;0,7;0;0;");
        lines[1].Should().EndWith(";60;DMP-001;31/01/2027");
    }

    [Fact]
    public async Task ApplyingAScore_SetsTheSupplierEvaluation_AndTheAuditKeepsTheProposedOne()
    {
        var supplier = new Supplier("BIO", "Biodis") { EvaluationScore = 40 };
        string? reason = null;

        var service = new SupplierEvaluationService(
            AchatsReceptionTests.Stub<IRepository<Supplier>>.With(
                ("GetByIdAsync", _ => Task.FromResult<Supplier?>(supplier))),
            AchatsReceptionTests.Stub<IPurchasingQualityQueries>.Create(),
            AchatsReceptionTests.Stub<IUnitOfWork>.With(("set_AuditReason", args => { reason = (string?)args![0]; return null; })),
            AchatsReceptionTests.Guard(),
            AchatsReceptionTests.Clock());

        (await service.ApplyScoreAsync(supplier.Id, 75, 72, 2026)).IsSuccess.Should().BeTrue();

        supplier.EvaluationScore.Should().Be(75);
        supplier.LastEvaluationDate.Should().Be(Today);
        reason.Should().Contain("75/100").And.Contain("72/100");

        (await service.ApplyScoreAsync(supplier.Id, 120, 72, 2026))
            .FirstError.ResourceKey.Should().Be("Supplier_ScoreOutOfRange");
    }

    [Fact]
    public async Task TheEvaluation_NeedsThePurchasingPermission()
    {
        var service = new SupplierEvaluationService(
            AchatsReceptionTests.Stub<IRepository<Supplier>>.Create(),
            AchatsReceptionTests.Stub<IPurchasingQualityQueries>.Create(),
            AchatsReceptionTests.Stub<IUnitOfWork>.Create(),
            AchatsReceptionTests.Guard(MarocLIS.Domain.Identity.Permissions.Inventory.ManagePurchaseOrders),
            AchatsReceptionTests.Clock());

        (await service.EvaluateAsync(2026)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void TheRecallCsv_ListsOneLinePerResult()
    {
        var bytes = PurchasingCsv.Recall(
        [
            new RecallResultRow(Guid.NewGuid(), Guid.NewGuid(), "ALAOUI Salma", "P-0001", Guid.NewGuid(),
                                "2026-000123", "GLU", "GLU", new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero), true)
        ], ["Patient", "Dossier", "Demande", "Analyse", "Paramètre", "Date", "Validé"]);

        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        text.Should().Contain("ALAOUI Salma;P-0001;2026-000123;GLU;GLU;01/10/2026 09:30;oui");
    }
}
