using FluentAssertions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.HumanResources;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Results;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The smaller rules the workstation's screens applied themselves, now in the shared core: which row
/// is urgent, overdue, on the staff or to complete, which line takes a value, which refusal is the
/// approver's.
/// </summary>
public sealed class SharedScreenRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(1));

    // ---- Rappels ---------------------------------------------------------------------------------

    private static RecollectionRow Recollection(OrderPriority priority, TimeSpan ago, int contacts) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "2610-000001", "ALAOUI Mohammed", null, priority,
            "B1", "B2", SampleRejectionReason.Haemolysed, null, Now - ago, "NFS", contacts, null, null);

    [Fact]
    public void Recollection_IsOverdueADayAfterTheRejectionWithoutAContact_AndPriorityWhenUrgentOrOverdue()
    {
        RecollectionRules.IsOverdue(Recollection(OrderPriority.Routine, TimeSpan.FromHours(25), 0), Now).Should().BeTrue();
        RecollectionRules.IsOverdue(Recollection(OrderPriority.Routine, TimeSpan.FromHours(24), 0), Now).Should().BeFalse();
        RecollectionRules.IsOverdue(Recollection(OrderPriority.Routine, TimeSpan.FromHours(30), 1), Now).Should().BeFalse();

        RecollectionRules.IsPriority(Recollection(OrderPriority.Urgent, TimeSpan.Zero, 0), Now).Should().BeTrue();
        RecollectionRules.IsPriority(Recollection(OrderPriority.Routine, TimeSpan.FromHours(25), 0), Now).Should().BeTrue();
        RecollectionRules.IsPriority(Recollection(OrderPriority.Routine, TimeSpan.FromHours(2), 0), Now).Should().BeFalse();
    }

    // ---- Réception -------------------------------------------------------------------------------

    private static SampleReceptionCandidate Tube(OrderPriority priority, bool stale) => new()
    {
        SampleId = Guid.NewGuid(),
        LabOrderId = Guid.NewGuid(),
        Barcode = "B1",
        AccessionNumber = "2610-000001",
        PatientName = "ALAOUI Mohammed",
        SpecimenTypeName = new MultilingualText("Sang"),
        Priority = priority,
        StabilityExceeded = stale
    };

    [Fact]
    public void ReceptionChips_UrgentIsNotRoutine_LongDelayIsPastStability_AllIsEverything()
    {
        var routine = Tube(OrderPriority.Routine, stale: false);
        var urgent = Tube(OrderPriority.VitalEmergency, stale: false);
        var stale = Tube(OrderPriority.Routine, stale: true);

        SampleReceptionRules.Matches(routine, AwaitingReceptionFilter.All).Should().BeTrue();
        SampleReceptionRules.Matches(urgent, AwaitingReceptionFilter.Urgent).Should().BeTrue();
        SampleReceptionRules.Matches(routine, AwaitingReceptionFilter.Urgent).Should().BeFalse();
        SampleReceptionRules.Matches(stale, AwaitingReceptionFilter.LongDelay).Should().BeTrue();
        SampleReceptionRules.Matches(urgent, AwaitingReceptionFilter.LongDelay).Should().BeFalse();
    }

    // ---- Commandes, personnel, catalogue ---------------------------------------------------------

    private static PurchaseOrderRow Order(PurchaseOrderStatus status, bool priority) =>
        new(Guid.NewGuid(), "BC-1", "Fournisseur", new DateOnly(2026, 10, 1), null, status, 1, 10m, 12m,
            "Acheteur", null, null, false, IsPriority: priority);

    [Fact]
    public void PurchaseOrder_IsAPendingPriority_UntilReceivedOrCancelled()
    {
        PurchaseOrderRules.IsPendingPriority(Order(PurchaseOrderStatus.Sent, priority: true)).Should().BeTrue();
        PurchaseOrderRules.IsPendingPriority(Order(PurchaseOrderStatus.Received, priority: true)).Should().BeFalse();
        PurchaseOrderRules.IsPendingPriority(Order(PurchaseOrderStatus.Cancelled, priority: true)).Should().BeFalse();
        PurchaseOrderRules.IsPendingPriority(Order(PurchaseOrderStatus.Sent, priority: false)).Should().BeFalse();
    }

    private static EmployeeRow Employee(DateOnly? exit) =>
        new(Guid.NewGuid(), "E1", "TAZI Imane", EmployeeJob.Biologist, null, null, default,
            new DateOnly(2020, 1, 1), null, null, null, exit, null);

    [Fact]
    public void Employee_IsOnTheStaff_UntilTheirExitDateIsPast()
    {
        var today = new DateOnly(2026, 10, 20);

        PersonnelRules.OnStaff(Employee(null), today).Should().BeTrue();
        PersonnelRules.OnStaff(Employee(today), today).Should().BeTrue("the notice is still being served");
        PersonnelRules.OnStaff(Employee(today.AddDays(-1)), today).Should().BeFalse();
    }

    private static AnalysisRow Analysis(bool active, int parameters, int tariffs, int unconfirmedBands = 0) =>
        new(Guid.NewGuid(), "NFS", new MultilingualText("NFS"), "HEMA", new MultilingualText("Hématologie"),
            Guid.NewGuid(), Guid.NewGuid(), new MultilingualText("Sang"), null, null, parameters, parameters, 1,
            tariffs, 80m, null, null, 4, false, false, ConsentRequirement.None, active, 0, unconfirmedBands);

    [Fact]
    public void Analysis_NeedsAttention_WhenItCannotBeOrdered_OrHasAgeBandsToConfirm()
    {
        CatalogRules.NeedsAttention(Analysis(active: true, parameters: 1, tariffs: 1)).Should().BeFalse();
        CatalogRules.NeedsAttention(Analysis(active: true, parameters: 1, tariffs: 0)).Should().BeTrue();
        CatalogRules.NeedsAttention(Analysis(active: true, parameters: 0, tariffs: 1)).Should().BeTrue();
        CatalogRules.NeedsAttention(Analysis(active: false, parameters: 1, tariffs: 1)).Should().BeTrue();
        CatalogRules.NeedsAttention(Analysis(active: true, parameters: 1, tariffs: 1, unconfirmedBands: 2)).Should().BeTrue();
    }

    // ---- Saisie des résultats --------------------------------------------------------------------

    private static ResultEntryRow Line(ResultValueType type = ResultValueType.Numeric,
                                       TestResultStatus status = TestResultStatus.Awaited,
                                       bool calculated = false, bool awaiting = false) => new()
    {
        TestResultId = Guid.NewGuid(),
        LabOrderItemId = Guid.NewGuid(),
        TestParameterId = Guid.NewGuid(),
        AnalysisCode = "GLY",
        AnalysisName = new MultilingualText("Glycémie"),
        ParameterCode = "GLY",
        ParameterName = new MultilingualText("Glycémie"),
        ValueType = type,
        Status = status,
        IsCalculated = calculated,
        IsAwaitingSample = awaiting
    };

    [Fact]
    public void Line_TakesAValue_WhenEditable_ItsTubeReceived_AndNotReleased()
    {
        ResultEntryRules.IsKeyable(Line()).Should().BeTrue();
        ResultEntryRules.IsKeyable(null).Should().BeFalse();
        ResultEntryRules.IsKeyable(Line(calculated: true)).Should().BeFalse();
        ResultEntryRules.IsKeyable(Line(type: ResultValueType.Heading)).Should().BeFalse();
        ResultEntryRules.IsKeyable(Line(awaiting: true)).Should().BeFalse();
        ResultEntryRules.IsKeyable(Line(status: TestResultStatus.Validated)).Should().BeFalse();
        ResultEntryRules.IsKeyable(Line(status: TestResultStatus.Reported)).Should().BeFalse();
    }

    [Fact]
    public void OnlyAMeasuredNumber_HasACurve()
    {
        ResultEntryRules.IsCharted(Line(ResultValueType.Numeric)).Should().BeTrue();
        ResultEntryRules.IsCharted(Line(ResultValueType.NumericWithOperator)).Should().BeTrue();
        ResultEntryRules.IsCharted(Line(ResultValueType.Text)).Should().BeFalse();
        ResultEntryRules.IsCharted(Line(ResultValueType.Heading)).Should().BeFalse();
        ResultEntryRules.IsCharted(null).Should().BeFalse();
    }

    [Theory]
    [InlineData("5,4", null, "5.4")]
    [InlineData(" < 0.5 ", "<", "0.5")]
    [InlineData(">=10", ">=", "10")]
    [InlineData("≤3", "≤", "3")]
    public void TypedValue_IsAnOptionalOperatorThenANumber(string raw, string? oper, string value)
    {
        ResultEntryRules.TryParseTyped(raw, out var parsedOperator, out var parsed).Should().BeTrue();
        parsedOperator.Should().Be(oper);
        parsed.Should().Be(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("positif")]
    public void TypedValue_ThatIsNotANumber_IsNotRead(string? raw) =>
        ResultEntryRules.TryParseTyped(raw, out _, out _).Should().BeFalse();

    [Fact]
    public void Sectors_AreTheFilesDepartmentCodes()
    {
        var order = new PendingEntryOrder("2610-000001", "ALAOUI Mohammed", OrderPriority.Routine, null, null, 2,
                                          " BIOCH  HEMA ");

        ResultEntryRules.SectorsOf(order).Should().Equal("BIOCH", "HEMA");
    }

    // ---- Caisse et facturation -------------------------------------------------------------------

    [Fact]
    public void CashVariance_NeedsAnExplanation_BeyondTenDirhamsEitherWay()
    {
        CashSession.VarianceToleranceMad.Should().Be(10m);
        CashSession.RequiresExplanation(10m).Should().BeFalse();
        CashSession.RequiresExplanation(-10m).Should().BeFalse();
        CashSession.RequiresExplanation(10.01m).Should().BeTrue();
        CashSession.RequiresExplanation(-10.01m).Should().BeTrue();
    }

    [Theory]
    [InlineData("Approval_SelfNotAllowed", true)]
    [InlineData("Approval_InvalidCredentials", true)]
    [InlineData("Invoice_DiscountApprovalRequired", true)]
    [InlineData("Auth_AccountLockedOut", true)]
    [InlineData("Invoice_DiscountReasonRequired", false)]
    [InlineData("Message.PermissionDenied", false)]
    public void ARefusal_OfTheApprover_AsksOnlyForTheApprovalAgain(string key, bool approval) =>
        DiscountApproval.IsRefusal(key).Should().Be(approval);
}
