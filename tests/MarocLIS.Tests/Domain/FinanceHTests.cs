using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Relations;
using MarocLIS.Application.Statistics;
using MarocLIS.Application.Steering;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Relations;
using MarocLIS.Infrastructure.Persistence;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Lot H, commercial relations and steering: prescriber statistics, visits and conventions, the
/// wording and planning of patient follow-up messages, the survey replies, the reagent cost and
/// margin, the receivables ageing and the payroll ratio of the steering dashboard.
/// </summary>
public sealed class FinanceHTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(1);
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static DateTimeOffset At(int year, int month, int day) => new(year, month, day, 10, 0, 0, Offset);

    // ---- Prescriber statistics ---------------------------------------------------------------------

    [Fact]
    public void PreviousPeriod_OfWholeMonths_IsTheSameNumberOfMonths_OtherwiseTheSameNumberOfDays()
    {
        PrescriberStatisticsService.PreviousPeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31))
            .Should().Be((new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)));
        PrescriberStatisticsService.PreviousPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30))
            .Should().Be((new DateOnly(2025, 7, 1), new DateOnly(2025, 12, 31)));
        PrescriberStatisticsService.PreviousPeriod(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 19))
            .Should().Be((new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 9)));
    }

    [Fact]
    public void PrescriberStatistics_CountTrendsNewAndLostPrescribers_AndGroupInstitutions()
    {
        var alami = Guid.NewGuid();
        var bennani = Guid.NewGuid();
        var chraibi = Guid.NewGuid();
        var site = Guid.NewGuid();

        var labels = new Dictionary<Guid, PrescriberLabel>
        {
            [alami] = new(alami, "Dr ALAMI Said", "Clinique Al Amal", PrescriberKind.Specialist, "Rabat"),
            [bennani] = new(bennani, "Dr BENNANI Leila", "CLINIQUE AL AMAL ", PrescriberKind.GeneralPractitioner, "Rabat"),
            [chraibi] = new(chraibi, "Dr CHRAIBI Omar", null, PrescriberKind.GeneralPractitioner, "Salé")
        };

        PrescriberOrderRow Order(Guid? prescriber, DateTimeOffset at, int tests, decimal revenue) =>
            new(Guid.NewGuid(), site, prescriber, at, tests, revenue);

        var current = new[]
        {
            Order(alami, At(2026, 3, 2), 3, 300m),
            Order(alami, At(2026, 3, 20), 2, 200m),
            Order(bennani, At(2026, 3, 5), 1, 100m),
            Order(null, At(2026, 3, 6), 1, 50m)
        };
        var previous = new[]
        {
            Order(alami, At(2026, 2, 3), 2, 250m),
            Order(chraibi, At(2026, 2, 10), 4, 400m)
        };
        var lastYear = new[] { Order(alami, At(2025, 3, 4), 1, 100m) };

        var stats = PrescriberStatisticsService.Compute(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31),
                                                        current, previous, lastYear,
                                                        new HashSet<Guid> { alami, chraibi }, labels, Offset);

        stats.Total.Should().Be(new VolumeFigures(4, 7, 650m));

        var a = stats.Prescribers.Single(p => p.PrescriberId == alami);
        a.Current.Should().Be(new VolumeFigures(2, 5, 500m));
        a.OrdersVsPrevious.Should().Be(100.0, "two orders against one");
        a.RevenueVsPrevious.Should().Be(100.0, "500 against 250");
        a.OrdersVsLastYear.Should().Be(100.0);
        a.IsNew.Should().BeFalse();

        // Bennani sent nothing in the year before: new. Chraibi sent in February, not in March: lost.
        stats.NewPrescribers.Select(p => p.PrescriberId).Should().Equal(bennani);
        stats.LostPrescribers.Select(p => p.PrescriberId).Should().Equal(chraibi);
        stats.Prescribers.Single(p => p.PrescriberId is null).Current.Orders.Should().Be(1, "walk-ins have their own line");

        // The two spellings of the clinic add up.
        var clinic = stats.Institutions.Single(i => i.Institution == "Clinique Al Amal");
        clinic.Current.Should().Be(new VolumeFigures(3, 6, 600m));

        var march = stats.Months.Single();
        march.Month.Should().Be(new DateOnly(2026, 3, 1));
        march.ActivePrescribers.Should().Be(2);
        march.NewPrescribers.Should().Be(1);
        march.LastYear.Orders.Should().Be(1);

        stats.Detail.Should().Contain(d => d.PrescriberId == alami && d.Figures.Orders == 2);
    }

    [Fact]
    public void Change_IsNullWithoutABase()
    {
        PrescriberStatisticsService.Change(10m, 0m).Should().BeNull();
        PrescriberStatisticsService.Change(5m, 10m).Should().Be(-50.0);
    }

    // ---- Visits and conventions --------------------------------------------------------------------

    [Fact]
    public void AVisit_IsRecordedOnceMade_AndItsNextDateFollowsIt()
    {
        var visit = new DelegateVisit(Guid.NewGuid(), Today, Guid.NewGuid(), Guid.NewGuid(), VisitPurpose.ServiceReview)
        {
            NextVisitOn = Today.AddMonths(2)
        };
        visit.Validate(Today).IsSuccess.Should().BeTrue();
        visit.IsNextVisitDue(Today).Should().BeFalse();
        visit.IsNextVisitDue(Today.AddMonths(2)).Should().BeTrue();

        visit.VisitDate = Today.AddDays(1);
        visit.Validate(Today).Errors.Select(e => e.ResourceKey).Should().Contain("Visit_DateInFuture");

        visit.VisitDate = Today;
        visit.NextVisitOn = Today;
        visit.Validate(Today).Errors.Select(e => e.ResourceKey).Should().Contain("Visit_NextBeforeVisit");
    }

    [Fact]
    public void AConvention_IsInForceBetweenItsDates_AndFlaggedBeforeItEnds()
    {
        var convention = new Partnership(Guid.NewGuid(), "Clinique Al Amal", PartnerKind.Clinic,
                                         PartnershipKind.CareConvention, new DateOnly(2026, 1, 1))
        {
            EndDate = new DateOnly(2026, 12, 1),
            DiscountPercent = 10m
        };

        convention.Validate().IsSuccess.Should().BeTrue();
        convention.IsInForce(Today).Should().BeTrue();
        convention.IsEndingSoon(Today).Should().BeTrue("47 days are left");
        convention.IsInForce(new DateOnly(2026, 12, 2)).Should().BeFalse();

        convention.EndDate = new DateOnly(2025, 12, 31);
        convention.DiscountPercent = 120m;
        convention.Validate().Errors.Select(e => e.ResourceKey).Should()
            .Contain(["Partnership_EndBeforeStart", "Partnership_DiscountOutOfRange"]);
    }

    // ---- Wording ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("Profitez d'une remise sur votre bilan {date}", "remise")]
    [InlineData("Bilan complet à -20 % ce mois-ci", "%")]
    [InlineData("Analyses GRATUITES pour vos proches", "gratuit")]
    [InlineData("Nouvelle OFFRE du laboratoire", "offre")]
    [InlineData("عرض خاص على التحاليل", "عرض")]
    public void APromotionalWording_IsRefused(string template, string term)
    {
        var result = MessageWording.Validate(template);
        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Messaging_AdvertisingWording");
        result.FirstError.Args.Should().Contain(term);
    }

    [Fact]
    public void TheDefaultTemplates_AreFollowUpWording_AndUnknownPlaceholdersAreRefused()
    {
        MessageWording.Validate(MessagingSettings.DefaultReminderFr).IsSuccess.Should().BeTrue();
        MessageWording.Validate(MessagingSettings.DefaultReminderAr).IsSuccess.Should().BeTrue();
        MessageWording.Validate(MessagingSettings.Default.SurveyTemplateFr).IsSuccess.Should().BeTrue();
        MessageWording.Validate(MessagingSettings.Default.SurveyTemplateAr).IsSuccess.Should().BeTrue();
        // "salle" is not "sale", "approximatif" holds no "prix".
        MessageWording.Validate("Merci de passer en salle de prélèvement").IsSuccess.Should().BeTrue();

        MessageWording.Validate("Bonjour {nom}").FirstError.ResourceKey.Should().Be("Messaging_UnknownPlaceholder");
        MessageWording.Validate(" ").FirstError.ResourceKey.Should().Be("Messaging_TemplateRequired");
    }

    [Fact]
    public void Render_FillsThePlaceholders_AndCountsTheSmsParts()
    {
        var text = MessageWording.Render("Bonjour {patient}, dosage {analyse} du {date}. {labo} {telephone}",
            new Dictionary<string, string?>
            {
                [MessageWording.Patient] = "ALAOUI Samira",
                [MessageWording.Analysis] = "HbA1c",
                [MessageWording.Date] = "15/07/2026",
                [MessageWording.Laboratory] = "Labo Atlas",
                [MessageWording.Phone] = null
            });

        text.Should().Be("Bonjour ALAOUI Samira, dosage HbA1c du 15/07/2026. Labo Atlas");
        MessageWording.Segments(text).Should().Be(1);
        MessageWording.Segments(new string('a', 161)).Should().Be(2);
        MessageWording.Segments("مرحبا " + new string('ب', 70)).Should().Be(2, "Arabic is sent in 70-character parts");
    }

    // ---- Surveys ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("k7pq2m 4 accueil très correct", "K7PQ2M", 4, "accueil très correct")]
    [InlineData("K7PQ2M:5", "K7PQ2M", 5, null)]
    [InlineData("K7PQ2M - 3/5 attente longue", "K7PQ2M", 3, "attente longue")]
    public void AReply_GivesTheCodeTheScoreAndTheComment(string line, string code, int score, string? comment)
    {
        SurveyReplyParser.Parse(line).Should().Be(new SurveyReply(code, score, comment));
    }

    [Theory]
    [InlineData("merci beaucoup")]
    [InlineData("K7PQ2M 6")]
    [InlineData("K7PQ2M 45")]
    [InlineData("K7PO2M 4")]
    public void ALineThatIsNotAReply_IsNotRead(string line) =>
        SurveyReplyParser.Parse(line).Should().BeNull();

    [Fact]
    public void ASurvey_IsAnsweredOnce_WithAScoreFromOneToFive()
    {
        var code = SurveyCode.New();
        SurveyCode.Normalise(code.ToLowerInvariant()).Should().Be(code);

        var survey = new SurveyInvitation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), code, At(2026, 10, 1));
        survey.RecordAnswer(0, null, SurveyAnswerChannel.Manual, At(2026, 10, 2), Guid.NewGuid())
              .FirstError.ResourceKey.Should().Be("Survey_ScoreOutOfRange");
        survey.RecordAnswer(4, "  bien  ", SurveyAnswerChannel.ReplyCode, At(2026, 10, 2), Guid.NewGuid())
              .IsSuccess.Should().BeTrue();
        survey.Comment.Should().Be("bien");
        survey.RecordAnswer(5, null, SurveyAnswerChannel.Manual, At(2026, 10, 3), Guid.NewGuid())
              .FirstError.ResourceKey.Should().Be("Survey_AlreadyAnswered");
    }

    [Fact]
    public void SurveyResults_RateTheAnswersOverTheDeliveredInvitations()
    {
        var rows = new[]
        {
            new SurveyAnswerRow(At(2026, 10, 1), true, 5, "Très bien", At(2026, 10, 2), Guid.NewGuid()),
            new SurveyAnswerRow(At(2026, 10, 1), true, 3, null, At(2026, 10, 2), Guid.NewGuid()),
            new SurveyAnswerRow(At(2026, 10, 1), true, null, null, null, Guid.NewGuid()),
            new SurveyAnswerRow(At(2026, 10, 1), false, null, null, null, Guid.NewGuid())
        };

        var results = PatientMessagingService.SummariseSurveys(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), rows, Offset);

        results.Invited.Should().Be(4);
        results.Delivered.Should().Be(3);
        results.Answered.Should().Be(2);
        results.ResponseRatePercent.Should().Be(66.7);
        results.AverageScore.Should().Be(4.0);
        results.Distribution.Should().Equal(0, 0, 1, 0, 1);
        results.Comments.Should().ContainSingle().Which.Comment.Should().Be("Très bien");
    }

    // ---- Reminders ---------------------------------------------------------------------------------

    private static ReminderRule HbA1cRule() =>
        new(Guid.NewGuid(), null, 3, new MultilingualText(MessagingSettings.DefaultReminderFr)) { WindowDays = 60 };

    [Fact]
    public void AReminderRule_NamesAnAnalysisOrAPanel_AndADelay()
    {
        HbA1cRule().Validate().IsSuccess.Should().BeTrue();

        var neither = new ReminderRule(null, null, 0, new MultilingualText("Bonjour {patient}"));
        neither.Validate().Errors.Select(e => e.ResourceKey).Should()
            .Contain(["Reminder_AnalysisOrPanel", "Reminder_DelayOutOfRange"]);

        var both = new ReminderRule(Guid.NewGuid(), Guid.NewGuid(), 3, new MultilingualText("Promo HbA1c"));
        both.Validate().Errors.Select(e => e.ResourceKey).Should()
            .Contain(["Reminder_AnalysisOrPanel", "Messaging_AdvertisingWording"]);
    }

    [Fact]
    public void Reminders_FollowTheLatestResult_AndStopWhenThePatientCameBackOrWasReminded()
    {
        var rule = HbA1cRule();
        var due = Guid.NewGuid();
        var back = Guid.NewGuid();
        var tooLate = Guid.NewGuid();
        var reminded = Guid.NewGuid();
        var dueOrder = Guid.NewGuid();
        var remindedOrder = Guid.NewGuid();

        var sources = new[]
        {
            // Two results: the July one starts the delay (due 10 October), the April one is history.
            new ReminderSourceRow(due, Guid.NewGuid(), At(2026, 4, 2), At(2026, 4, 3)),
            new ReminderSourceRow(due, dueOrder, At(2026, 7, 9), At(2026, 7, 10)),
            // Came back in October: an order registered after the result, even not yet validated.
            new ReminderSourceRow(back, Guid.NewGuid(), At(2026, 7, 1), At(2026, 7, 1)),
            new ReminderSourceRow(back, Guid.NewGuid(), At(2026, 10, 12), null),
            // Due in May: the window of 60 days after the due date has passed.
            new ReminderSourceRow(tooLate, Guid.NewGuid(), At(2026, 2, 1), At(2026, 2, 1)),
            new ReminderSourceRow(reminded, remindedOrder, At(2026, 7, 1), At(2026, 7, 2))
        };

        var planned = ReminderPlanner.Plan(rule, sources, new HashSet<(Guid, Guid)> { (rule.Id, remindedOrder) },
                                           Today, Offset);

        planned.Should().ContainSingle();
        planned[0].PatientId.Should().Be(due);
        planned[0].OrderId.Should().Be(dueOrder);
        planned[0].ResultDate.Should().Be(new DateOnly(2026, 7, 10));
        planned[0].DueOn.Should().Be(new DateOnly(2026, 10, 10));

        ReminderPlanner.EarliestResultDate(rule, Today).Should().BeBefore(new DateOnly(2026, 5, 18));
    }

    [Fact]
    public void TheFollowUpConsent_IsItsOwnPurpose_AndNeedsNoWrittenForm()
    {
        Enum.IsDefined(ConsentPurpose.FollowUpMessages).Should().BeTrue();
        PatientConsent.RequiresWrittenEvidence(ConsentPurpose.FollowUpMessages).Should().BeFalse();
        ((int)ConsentPurpose.FollowUpMessages).Should().Be(9, "the value is stored: it must not move");
    }

    // ---- Cost and margin ---------------------------------------------------------------------------

    private static readonly Guid Site = Guid.NewGuid();
    private static readonly Guid Analyser = Guid.NewGuid();

    [Fact]
    public void TheReagentCost_TakesTheContractCostPerTest_ElseTheLotPriceOverTheTestsPerPack()
    {
        var hba1c = new CostReagentItem(Guid.NewGuid(), Site, "R-HBA", "HBA1C", "HBA1C=2", 100, 20m, 900m, 1000m);
        var glucose = new CostReagentItem(Guid.NewGuid(), Site, "R-GLU", "GLU;UREE", null, 500, null, 400m, null);
        var tsh = new CostReagentItem(Guid.NewGuid(), Site, "R-TSH", "TSH", null, 100, null, null, null);
        var lot = Guid.NewGuid();

        var model = new ReagentCostModel(
            [hba1c, glucose, tsh],
            [new CostContractReagent(Site, glucose.StockItemId, [Analyser], 450m, 0.75m)],
            new Dictionary<Guid, (Guid, decimal?)> { [lot] = (hba1c.StockItemId, 800m) });

        // Lot used: 800 / 100 tests × 2 per result / 80 % usable = 20.
        model.CostOf(Site, "hba1c", Analyser, lot).Should().Be(new TestCost(20m, CostBasis.LotPrice));
        // No lot on the result: the last lot received, 1000 / 100 × 2 / 0.8 = 25.
        model.CostOf(Site, "HBA1C", null, null).Should().Be(new TestCost(25m, CostBasis.LotPrice));
        // The contract states 0.75 per test.
        model.CostOf(Site, "UREE", Analyser, null).Should().Be(new TestCost(0.75m, CostBasis.ContractPerTest));
        // No price anywhere: unknown, not zero passed off as a cost.
        model.CostOf(Site, "TSH", null, null).Basis.Should().Be(CostBasis.Unknown);
        model.CostOf(Site, "CRP", null, null).Basis.Should().Be(CostBasis.Unknown);
        model.CostOf(Guid.NewGuid(), "HBA1C", null, null).Basis.Should().Be(CostBasis.Unknown, "reagents are per site");
    }

    [Fact]
    public void TheMargin_IsTheNetPriceLessReagentsAndAllocation_PerAnalysisSiteAndAnalyser()
    {
        var hba = Guid.NewGuid();
        var glu = Guid.NewGuid();
        var model = new ReagentCostModel(
            [new CostReagentItem(Guid.NewGuid(), Site, "R-HBA", "HBA1C", null, 100, null, 1500m, null),
             new CostReagentItem(Guid.NewGuid(), Site, "R-GLU", "GLU", null, 500, null, 250m, null)],
            [], new Dictionary<Guid, (Guid, decimal?)>());

        var activity = new[]
        {
            new CostActivityRow(Site, hba, "HBA1C", Analyser, null, 120m),
            new CostActivityRow(Site, hba, "HBA1C", Analyser, null, 100m),
            new CostActivityRow(Site, glu, "GLU", null, null, 30m)
        };

        var labels = new CostLabels(
            new Dictionary<Guid, (string, MultilingualText)> { [hba] = ("HBA1C", new("Hémoglobine glyquée")), [glu] = ("GLU", new("Glycémie")) },
            new Dictionary<Guid, (string, MultilingualText)> { [Site] = ("RBT", new("Rabat")) },
            new Dictionary<Guid, (string, MultilingualText)> { [Analyser] = ("D10", new("D-10")) });

        var margin = CostMarginService.Compute(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), activity, model,
                                               allocationPerTest: 2m, labels, AppLanguage.French);

        var a = margin.ByAnalysis.Single(r => r.Code == "HBA1C");
        a.Tests.Should().Be(2);
        a.Revenue.Should().Be(220m);
        a.AverageNetPrice.Should().Be(110m);
        a.ReagentCost.Should().Be(30m);
        a.Allocation.Should().Be(4m);
        a.CostPerTest.Should().Be(17m);
        a.MarginPerTest.Should().Be(93m);
        a.Margin.Should().Be(186m);

        margin.ByInstrument.Select(r => r.Code).Should().BeEquivalentTo(["D10", "—"]);
        margin.BySite.Single().Tests.Should().Be(3);
        margin.Totals.Margin.Should().Be(186m + 30m - 0.5m - 2m);
        margin.Totals.TestsWithoutCost.Should().Be(0);
    }

    // ---- Steering ----------------------------------------------------------------------------------

    [Fact]
    public void Receivables_AreAgedFromTheirDueDate_CreditNotesAndPaidInvoicesLeftOut()
    {
        SteeringService.BandOf(null, Today).Should().Be(AgeingBand.NotDue);
        SteeringService.BandOf(Today, Today).Should().Be(AgeingBand.NotDue);
        SteeringService.BandOf(Today.AddDays(-30), Today).Should().Be(AgeingBand.Days1To30);
        SteeringService.BandOf(Today.AddDays(-31), Today).Should().Be(AgeingBand.Days31To60);
        SteeringService.BandOf(Today.AddDays(-181), Today).Should().Be(AgeingBand.Over180);

        var ageing = SteeringService.Age(
        [
            new ReceivableRow(Site, InvoiceKind.Invoice, Today.AddDays(-100), Today.AddDays(-70), 1000m, 200m, 100m),
            new ReceivableRow(Site, InvoiceKind.Invoice, Today.AddDays(-10), Today.AddDays(20), 500m, 0m, 0m),
            new ReceivableRow(Site, InvoiceKind.Invoice, Today.AddDays(-10), Today.AddDays(-5), 300m, 300m, 0m),
            new ReceivableRow(Site, InvoiceKind.CreditNote, Today.AddDays(-10), Today.AddDays(-5), 300m, 0m, 0m)
        ], Today);

        ageing.Single(a => a.Band == AgeingBand.Days61To90).Should().Be(new AgeingRow(AgeingBand.Days61To90, 1, 700m));
        ageing.Single(a => a.Band == AgeingBand.NotDue).Amount.Should().Be(500m);
        ageing.Sum(a => a.Invoices).Should().Be(2);
    }

    [Fact]
    public void TheDashboard_ComparesPayrollWithTheRevenueOfTheSameClosedMonths_AndHidesItWithoutTheRight()
    {
        var payer = Guid.NewGuid();
        var prescriber = Guid.NewGuid();

        SteeringActivityRow Item(DateTimeOffset at, decimal price, LabOrderItemStatus status = LabOrderItemStatus.Validated) =>
            new(Site, prescriber, new ActivityItem(Guid.NewGuid(), Guid.NewGuid(), at, payer, Guid.NewGuid(), Guid.NewGuid(),
                                                    status, at.AddHours(1), at.AddHours(5), at.AddHours(6), price, price, 0m));

        var activity = new[]
        {
            Item(At(2026, 8, 3), 10000m),
            Item(At(2026, 9, 3), 20000m),
            Item(At(2026, 9, 4), 999m, LabOrderItemStatus.Cancelled)
        };

        var payroll = new[]
        {
            new PayrollMonthRow(Site, 2026, 8, 5, 4000m, 5000m),
            new PayrollMonthRow(Site, 2026, 9, 5, 4000m, 5000m)
        };

        var quality = new QualityCounts(40, 2, 3, 1, new Dictionary<EqaOutcome, int> { [EqaOutcome.Satisfactory] = 4 });
        var labels = new SteeringLabels(new Dictionary<Guid, string> { [Site] = "RBT — Rabat" },
                                        new Dictionary<Guid, MultilingualText> { [payer] = new("CNSS") },
                                        new Dictionary<Guid, string> { [prescriber] = "Dr ALAMI Said" });
        var margin = new MarginTotals(2, 30000m, 300m, 0m, 29700m, 99.0, 0);

        // 15 August to 30 September: only September is a whole month inside the period.
        var dashboard = SteeringService.Compute(new DateOnly(2026, 8, 15), new DateOnly(2026, 9, 30), 1, activity,
                                                (31000m, 1000m, 25000m), margin, [], payroll, quality, labels, Today,
                                                Offset, AppLanguage.French);

        dashboard.Revenue.Should().Be(30000m, "the cancelled test is not revenue");
        dashboard.Payroll!.SiteMonths.Should().Be(1);
        dashboard.Payroll.EmployerCost.Should().Be(5000m);
        dashboard.Payroll.Revenue.Should().Be(20000m);
        dashboard.Payroll.EmployerCostToRevenuePercent.Should().Be(25.0);
        dashboard.ByPayer.Single().Label.Should().Be("CNSS");
        dashboard.ByPrescriber.Single().Revenue.Should().Be(30000m);
        dashboard.ByDay.Should().HaveCount(47, "every day of the period is listed");
        dashboard.Quality.QcRejectedPercent.Should().Be(5.0);
        dashboard.Quality.EqaSatisfactory.Should().Be(4);
        dashboard.AverageTurnaroundHours.Should().Be(4.0);

        var hidden = SteeringService.Compute(new DateOnly(2026, 8, 15), new DateOnly(2026, 9, 30), 1, activity,
                                             (0m, 0m, 0m), margin, [], null, quality, labels, Today, Offset,
                                             AppLanguage.French);
        hidden.Payroll.Should().BeNull();
    }

    // ---- Permissions -------------------------------------------------------------------------------

    [Fact]
    public void TheNewPermissions_AreShipped_ToTheDirection_AndTheSteeringStaysWithIt()
    {
        Permissions.All.Should().Contain(["relations.view", "relations.manage", "messaging.campaign",
                                          "steering.view", "steering.costs.manage"]);

        var director = ShippedRole(SystemRoles.ResponsibleBiologist);
        director.Should().Contain([Permissions.Relations.View, Permissions.Relations.Manage,
                                   Permissions.Messaging.Campaign, Permissions.Steering.View,
                                   Permissions.Steering.ManageCosts]);

        ShippedRole(SystemRoles.Accountant).Should().Contain(Permissions.Relations.View)
            .And.NotContain(Permissions.Steering.View);
        ShippedRole(SystemRoles.Receptionist).Should().NotContain(Permissions.Messaging.Campaign);
        ShippedRole(SystemRoles.Administrator).Should().Contain(Permissions.Steering.View);
    }

    private static string[] ShippedRole(string roleCode)
    {
        var type = typeof(LisDbContext).Assembly.GetType("MarocLIS.Infrastructure.Persistence.RolePermissionSets")!;
        var definitions = (System.Collections.IEnumerable)type
            .GetMethod("Build", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

        foreach (var definition in definitions)
        {
            var tuple = definition.GetType();
            if ((string)tuple.GetField("Item1")!.GetValue(definition)! == roleCode)
                return (string[])tuple.GetField("Item3")!.GetValue(definition)!;
        }

        throw new InvalidOperationException(roleCode);
    }
}
