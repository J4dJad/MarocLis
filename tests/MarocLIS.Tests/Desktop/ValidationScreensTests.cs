using FluentAssertions;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Reports;
using MarocLIS.Domain.Results;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>The rules of the « Validation biologique » and « Comptes rendus » screens that need no window.</summary>
public sealed class ValidationScreensTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 10, 0, 0, TimeSpan.FromHours(1));

    private static ValidationWorklistEntry Entry(OrderPriority priority = OrderPriority.Routine, int abnormal = 0,
                                                 int critical = 0, int uncommunicated = 0, bool qc = false,
                                                 bool overdue = false) => new()
    {
        LabOrderId = Guid.NewGuid(),
        AccessionNumber = "2610-000001",
        PatientName = "IDRISSI Yassine",
        Priority = priority,
        AbnormalCount = abnormal,
        CriticalCount = critical,
        UncommunicatedCriticalCount = uncommunicated,
        HasQcFailure = qc,
        IsOverdue = overdue
    };

    private static ResultEntryRow Row(TestResultStatus status,
                                      ResultInterpretation interpretation = ResultInterpretation.Normal,
                                      ResultFlag flags = ResultFlag.None, bool communicated = false,
                                      ResultValueType type = ResultValueType.Numeric) => new()
    {
        TestResultId = Guid.NewGuid(),
        LabOrderItemId = Guid.NewGuid(),
        TestParameterId = Guid.NewGuid(),
        AnalysisCode = "NFS",
        AnalysisName = new MultilingualText("NFS"),
        ParameterCode = "HB",
        ParameterName = new MultilingualText("Hémoglobine"),
        ValueType = type,
        Status = status,
        Interpretation = interpretation,
        Flags = flags,
        IsCriticalCommunicated = communicated
    };

    [Fact]
    public void PreTicked_AreTheReleasableRows_AsOnTheWorkstationAndThePhone()
    {
        ValidationScreenRules.IsPreTicked(Row(TestResultStatus.TechnicallyValidated)).Should().BeTrue();
        ValidationScreenRules.IsPreTicked(Row(TestResultStatus.Entered)).Should().BeFalse();
        ValidationScreenRules.IsPreTicked(Row(TestResultStatus.Validated)).Should().BeFalse();
        ValidationScreenRules.IsPreTicked(Row(TestResultStatus.TechnicallyValidated, type: ResultValueType.Heading))
            .Should().BeFalse();
    }

    [Fact]
    public void Indicator_ShowsTheFlagsWhenThereAreAny_OtherwiseTheArrowOfTheInterpretation()
    {
        ValidationScreenRules.Indicator(Row(TestResultStatus.Entered, ResultInterpretation.CriticalHigh)).Should().Be("↑↑");
        ValidationScreenRules.Indicator(Row(TestResultStatus.Entered, ResultInterpretation.Low)).Should().Be("↓");
        ValidationScreenRules.Indicator(Row(TestResultStatus.Entered, ResultInterpretation.Abnormal)).Should().Be("*");
        ValidationScreenRules.Indicator(Row(TestResultStatus.Entered)).Should().BeEmpty();

        ValidationScreenRules.Indicator(Row(TestResultStatus.Entered, ResultInterpretation.High,
                                            ResultFlag.QcOutOfControl | ResultFlag.DeltaCheckFailed))
            .Should().Be("CIQ Δ");
        ValidationScreenRules.FlagGlyphs(ResultFlag.Diluted | ResultFlag.Corrected | ResultFlag.Subcontracted
                                         | ResultFlag.OutsideMeasuringRange | ResultFlag.SampleDefect)
            .Should().Be("! × R ST ≷");
    }

    [Fact]
    public void Chips_FilterTheLoadedList_UrgentBeingTheFormerServerTest()
    {
        var routine = Entry();
        var urgent = Entry(OrderPriority.Urgent);
        var vital = Entry(OrderPriority.VitalEmergency, critical: 1);
        var qc = Entry(abnormal: 2, qc: true);

        IReadOnlyList<ValidationWorklistEntry> all = [routine, urgent, vital, qc];

        ValidationScreenRules.Count(all, ValidationFilter.All).Should().Be(4);
        all.Where(e => ValidationScreenRules.Matches(e, ValidationFilter.Urgent)).Should().Equal(urgent, vital);
        all.Where(e => ValidationScreenRules.Matches(e, ValidationFilter.Abnormal)).Should().Equal(vital, qc);
        all.Where(e => ValidationScreenRules.Matches(e, ValidationFilter.QcBlocked)).Should().Equal(qc);
    }

    [Fact]
    public void Signals_PutTheUncommunicatedCriticalFirst_AndKeepEveryFormerSignal()
    {
        var entry = Entry(critical: 2, uncommunicated: 1, qc: true, overdue: true) with
        {
            HasDeltaCheckFailure = true,
            HasSampleDefect = true
        };

        var alerts = ValidationScreenRules.Alerts(entry);

        alerts.Select(a => a.Key).Should().Equal("Validation.AlertCriticalToCall", "Validation.AlertQc",
                                                  "Order.Overdue", "Validation.AlertDelta", "Validation.AlertSample");
        alerts[0].Tone.Should().Be(ValidationTone.Critical);
        alerts[0].Args.Should().Equal(1);

        // Every critical value called: still shown, as a warning.
        ValidationScreenRules.Alerts(Entry(critical: 2)).Single().Should()
            .Match<ValidationAlert>(a => a.Key == "Validation.AlertCritical" && a.Tone == ValidationTone.Warning);
    }

    [Fact]
    public void Lines_AreTickableOnlyWhenTechnicallyValidated_OrForWithdrawalOnRequest()
    {
        var technical = Row(TestResultStatus.TechnicallyValidated);
        var entered = Row(TestResultStatus.Entered);
        var validated = Row(TestResultStatus.Validated);
        var reported = Row(TestResultStatus.Reported);
        var heading = Row(TestResultStatus.Awaited, type: ResultValueType.Heading);

        ValidationScreenRules.IsReleasable(technical).Should().BeTrue();
        ValidationScreenRules.IsReleasable(entered).Should().BeFalse();
        ValidationScreenRules.IsListed(entered, false).Should().BeTrue();

        ValidationScreenRules.IsListed(validated, false).Should().BeFalse();
        ValidationScreenRules.IsListed(validated, true).Should().BeTrue();
        ValidationScreenRules.IsWithdrawable(validated, true).Should().BeTrue();
        ValidationScreenRules.IsWithdrawable(validated, false).Should().BeFalse();

        // Already on a report: un-validation is refused by the domain, so it is not offered here.
        ValidationScreenRules.IsListed(reported, true).Should().BeFalse();
        ValidationScreenRules.IsReleased(reported).Should().BeTrue();

        ValidationScreenRules.IsListed(heading, false).Should().BeTrue();
        ValidationScreenRules.IsReleasable(heading).Should().BeFalse();
    }

    [Fact]
    public void CompteRendu_IsOfferedOnlyOnceAValueIsValidatedAndNotYetReported()
    {
        // As the report service needs: a validated value not on a report yet (owner's decision).
        ValidationScreenRules.CanIssueReport([]).Should().BeFalse();
        ValidationScreenRules.CanIssueReport([Row(TestResultStatus.Entered), Row(TestResultStatus.TechnicallyValidated)])
            .Should().BeFalse();
        ValidationScreenRules.CanIssueReport([Row(TestResultStatus.Reported), Row(TestResultStatus.TechnicallyValidated)])
            .Should().BeFalse();
        ValidationScreenRules.CanIssueReport([Row(TestResultStatus.Validated, type: ResultValueType.Heading)])
            .Should().BeFalse();
        ValidationScreenRules.CanIssueReport([Row(TestResultStatus.TechnicallyValidated), Row(TestResultStatus.Validated)])
            .Should().BeTrue();
    }

    [Fact]
    public void Context_NamesAbnormalLateCriticalAndQc()
    {
        IReadOnlyCollection<ResultEntryRow> rows =
        [
            Row(TestResultStatus.TechnicallyValidated, ResultInterpretation.High),
            Row(TestResultStatus.TechnicallyValidated, ResultInterpretation.CriticalHigh, ResultFlag.CriticalHigh),
            Row(TestResultStatus.TechnicallyValidated, flags: ResultFlag.QcOutOfControl),
            Row(TestResultStatus.Awaited, type: ResultValueType.Heading)
        ];

        var lines = ValidationScreenRules.Context(rows, OrderPriority.Urgent, Now.AddHours(-1), Now);

        lines.Select(l => l.Key).Should().Equal("Validation.ContextAbnormal", "Validation.ContextUrgentLate",
                                                 "Validation.ContextCriticalToCall", "Validation.ContextQcFailed");
        lines[0].Args.Should().Equal(2);
        lines[2].Args.Should().Equal(1);
        lines.Skip(1).Should().OnlyContain(l => l.Tone == ValidationTone.Critical);
    }

    [Fact]
    public void Context_OfAQuietFile_IsAllClear()
    {
        IReadOnlyCollection<ResultEntryRow> rows =
        [
            Row(TestResultStatus.TechnicallyValidated),
            Row(TestResultStatus.TechnicallyValidated, ResultInterpretation.CriticalLow, ResultFlag.CriticalLow,
                communicated: true)
        ];

        var lines = ValidationScreenRules.Context(rows, OrderPriority.Routine, Now.AddHours(2), Now);

        // The communicated critical value is abnormal, and its call is acknowledged.
        lines.Select(l => l.Key).Should().Equal("Validation.ContextAbnormal", "Validation.ContextOnTime",
                                                 "Validation.ContextCriticalCalled", "Validation.ContextQcOk");

        ValidationScreenRules.Context([Row(TestResultStatus.TechnicallyValidated)], OrderPriority.Routine, null, Now)
            .Select(l => l.Key).Should().Equal("Validation.ContextNoAbnormal", "Validation.ContextNoCritical",
                                               "Validation.ContextQcOk");
    }

    private static ReportRegisterRow Report(bool final = true, bool delivered = false, bool critical = false,
                                            bool superseded = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "2610-000010", "SQUALLI Hamza", null, null, 1,
            final ? ReportCompleteness.Final : ReportCompleteness.Partial, ReportAmendmentKind.None, null,
            AppLanguage.French, Now, "Dr ALAMI", critical, false, false, true, true, 2,
            superseded ? Guid.NewGuid() : null, delivered ? 1 : 0, delivered ? Now : null, "Dr BENNANI",
            ReportDeliveryPreference.PatientCollects);

    private static PendingReportRow Pending(int awaiting, int released) =>
        new(Guid.NewGuid(), "2610-000004", "TAZI Imane", null, OrderPriority.Routine, 25, awaiting, Now, released,
            "Dr BENNANI");

    [Fact]
    public void Tabs_ToDeliverIsTheFormerUndeliveredTickBox_AndTheRegisterIsTheDefault()
    {
        var waiting = Report();
        var urgent = Report(critical: true);
        var handed = Report(delivered: true);
        var partial = Report(final: false);
        var old = Report(superseded: true);

        IReadOnlyList<ReportRegisterRow> all = [waiting, urgent, handed, partial, old];

        ReportRegisterRules.DefaultTab.Should().Be(ReportTab.Issued);
        ReportRegisterRules.RegisterRows(all, ReportTab.Issued).Should().Equal(all);
        ReportRegisterRules.RegisterRows(all, ReportTab.ToDeliver).Should().Equal(waiting, urgent);

        IReadOnlyList<PendingReportRow> pending = [Pending(0, 0), Pending(2, 1)];
        ReportRegisterRules.Count(pending, all, ReportTab.ToIssue).Should().Be(2);
        ReportRegisterRules.Count(pending, all, ReportTab.Issued).Should().Be(5);
        ReportRegisterRules.Count(pending, all, ReportTab.ToDeliver).Should().Be(2);

        ReportRegisterRules.IsUrgent(all, ReportTab.ToDeliver).Should().BeTrue();
        ReportRegisterRules.IsUrgent([waiting], ReportTab.ToDeliver).Should().BeFalse();
    }

    [Fact]
    public void NextRelease_IsFinalWhenNothingIsAwaited_AndNumberedAfterTheReleasedOnes()
    {
        ReportRegisterRules.NextRelease(Pending(0, 0)).Should().Be((true, 1));
        ReportRegisterRules.NextRelease(Pending(3, 1)).Should().Be((false, 2));
    }

    [Theory]
    [InlineData(ReportDeliveryPreference.Email, DeliveryChannel.Email)]
    [InlineData(ReportDeliveryPreference.Fax, DeliveryChannel.Fax)]
    [InlineData(ReportDeliveryPreference.SmsLink, DeliveryChannel.SmsLink)]
    [InlineData(ReportDeliveryPreference.Courier, DeliveryChannel.Courier)]
    [InlineData(ReportDeliveryPreference.ElectronicInterface, DeliveryChannel.ElectronicInterface)]
    [InlineData(ReportDeliveryPreference.PatientCollects, DeliveryChannel.PrintedAtCounter)]
    public void Delivery_IsOfferedOnThePrescribersPreferredChannel(ReportDeliveryPreference preference,
                                                                   DeliveryChannel channel) =>
        ReportRegisterRules.DefaultChannel(preference).Should().Be(channel);
}
