using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Quality;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using EquipmentScreen = MarocLIS.Web.Components.Screens.Equipment.EquipmentScreen;
using Rules = MarocLIS.Tests.Desktop.EquipmentScreenRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Équipements » in the browser: the register opened on what is overdue, a lapsed calibration in
/// the alarm's colours and said on the instrument's card, an intervention whose description is
/// missing refused in its box, a failed check after an intervention taking the instrument out of
/// service and opening its non-conformity, a passing calibration without its calibrator lot refused,
/// a return to service refused while the calibration is lapsed, nothing recorded on a decommissioned
/// instrument and each action kept by its right — through the workstation's EquipmentService.
/// </summary>
public sealed class WebEquipmentTests : WebScreenContext
{
    private readonly Fake<IInventoryQueries> _queries = Fake<IInventoryQueries>.Create();
    private readonly Fake<IRepository<Instrument>> _instruments = Fake<IRepository<Instrument>>.Create();
    private readonly Fake<IUnitOfWork> _work = Fake<IUnitOfWork>.Create();
    private readonly Fake<IQualityQueries> _quality = Fake<IQualityQueries>.Create();
    private readonly Fake<IRepository<NonConformity>> _files = Fake<IRepository<NonConformity>>.Create();
    private readonly Fake<ISequenceAllocator> _sequences = Fake<ISequenceAllocator>.Create();

    private static readonly string[] Everything =
    [
        Permissions.Equipment.View, Permissions.Equipment.Manage, Permissions.Equipment.RecordMaintenance, Permissions.Equipment.Calibrate,
        Permissions.Quality.ReportNonConformity
    ];

    private readonly EquipmentRow _lapsed, _late, _retired;
    private readonly Instrument _analyser;

    public WebEquipmentTests()
    {
        Services.AddSingleton(_queries.Object);
        Services.AddSingleton(_instruments.Object);
        Services.AddSingleton(_work.Object);
        Services.AddSingleton(_quality.Object);
        Services.AddSingleton(_files.Object);
        Services.AddSingleton(_sequences.Object);
        Services.AddScoped<EquipmentService>();
        Services.AddScoped<NonConformityService>();

        _analyser = new Instrument("AUT-01", new MultilingualText("Automate hématologie", "جهاز الدم"), EquipmentKind.Analyser, Guid.NewGuid())
        {
            CalibrationIntervalDays = 30,
            LastCalibrationOn = new DateOnly(2026, 9, 1)
        };
        _lapsed = Rules.Instrument("AUT-01", calibrationOverdue: true) with { InstrumentId = _analyser.Id };
        _late = Rules.Instrument("CEN-02", maintenanceOverdue: true);
        _retired = Rules.Instrument("BAL-07", EquipmentStatus.Decommissioned, reason: "Remplacée");
        _queries.On(nameof(IInventoryQueries.GetEquipmentAsync), _ => new[] { _lapsed, _late, _retired });
        _instruments.On(nameof(IRepository<Instrument>.GetByIdWithDetailsAsync), _ => _analyser);
    }

    private string? AuditReason => _work.Calls.LastOrDefault(c => c.Method == "set_AuditReason").Arguments is [string reason] ? reason : null;

    [Fact]
    public void TheRegister_OpensOnWhatIsOverdue_ALapsedCalibrationInTheAlarmsColours()
    {
        SignIn(AppLanguage.French, Everything);

        var page = Render<EquipmentScreen>();

        _queries.Calls.Single(c => c.Method == nameof(IInventoryQueries.GetEquipmentAsync)).Arguments[1].Should().Be(true);
        page.Find("[data-chip=only-due]").GetAttribute("aria-pressed").Should().Be("true");
        page.Find("[data-state=count]").TextContent.Should().Be("3 équipements · Échéance dépassée 2");

        var lapsed = page.Find("[data-grid=equipment] [data-row='AUT-01']");
        lapsed.ClassName.Should().Contain("stock-alarm");
        lapsed.TextContent.Should().Contain("! 01/10/26");
        var late = page.Find("[data-grid=equipment] [data-row='CEN-02']");
        late.ClassName.Should().NotContain("stock-alarm");
        late.QuerySelectorAll("td")[4].ClassName.Should().Contain("abnormal", "a late maintenance is said on its date");
        page.Find("[data-grid=equipment] [data-row='BAL-07']").ClassName.Should().Contain("is-tone-muted");

        page.Find("[data-state=instrument-warning]").TextContent.Should().Contain("Étalonnage échu");
        page.Find("[data-state=instrument-warning]").ClassName.Should().Contain("is-critical");

        page.Find("[data-chip=only-due]").Click();
        _queries.Calls.Last(c => c.Method == nameof(IInventoryQueries.GetEquipmentAsync)).Arguments[1].Should().Be(false);

        page.Find("[data-field=equipment-search]").Input("SN-12345");
        page.FindAll("[data-grid=equipment] tbody tr").Should().HaveCount(3);
        page.Find("[data-field=equipment-search]").Input("CEN-02");
        page.FindAll("[data-grid=equipment] tbody tr").Should().ContainSingle();
    }

    [Fact]
    public void AFailedCheck_TakesTheInstrumentOutOfService_AndOpensItsNonConformity()
    {
        SignIn(AppLanguage.French, Everything);
        _analyser.LastCalibrationOn = new DateOnly(2026, 10, 10);
        var operational = _lapsed with { CalibrationOverdue = false };
        _queries.On(nameof(IInventoryQueries.GetEquipmentAsync), _ => new[] { operational });
        _sequences.On(nameof(ISequenceAllocator.NextAsync), _ => Result.Success("NC-2026-0031"));

        var page = Render<EquipmentScreen>();
        page.Find("[data-action='Equipment.RecordMaintenance']").Click();
        page.Find("[data-modal=maintenance] [data-field=maintenance-kind]").GetAttribute("value").Should().Be("Corrective");

        page.Find("[data-modal=maintenance] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={EquipmentScreenRules.DescriptionField}]").TextContent.Should().Contain("description");
        _work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(0);

        page.Find($"[data-field={EquipmentScreenRules.DescriptionField}]").Input("Fuite sur la pompe à réactifs, joint remplacé");
        page.Find($"[data-field={EquipmentScreenRules.DowntimeField}]").Input("2 h");
        page.Find("[data-modal=maintenance] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={EquipmentScreenRules.DowntimeField}]").TextContent.Should().Contain("« 2 h »");

        page.Find($"[data-field={EquipmentScreenRules.DowntimeField}]").Input("2,5");
        page.Find("[data-field=maintenance-postcheck]").Change(false);
        page.Find("[data-modal=maintenance] [data-action='Action.Save']").Click();

        _analyser.Status.Should().Be(EquipmentStatus.OutOfService);
        _analyser.MaintenanceRecords.Single().DowntimeHours.Should().Be(2.5m);
        AuditReason.Should().Contain("contrôle non conforme, mis hors service");
        page.Find("[data-prompt=confirm]").TextContent.Should().Contain("mis hors service").And.Contain("non-conformité");

        page.Find("[data-modal=confirm] [data-action='Action.Yes']").Click();
        page.Find("[data-modal=nc-editor] [data-field=category]").GetAttribute("value").Should().Be("Equipment");
        page.Find("[data-modal=nc-editor] [data-field=severity]").GetAttribute("value").Should().Be("Major");
        page.Find($"[data-modal=nc-editor] [data-field={NonConformityScreenRules.DescriptionField}]").GetAttribute("value")
            .Should().Contain("Contrôle après intervention non conforme — AUT-01 Automate hématologie");
        page.Find("[data-modal=nc-editor] [data-action='Action.Save']").Click();

        _files.CallsTo(nameof(IRepository<NonConformity>.AddAsync)).Should().Be(1);
        var file = (NonConformity)_files.Calls.Single(c => c.Method == nameof(IRepository<NonConformity>.AddAsync)).Arguments[0]!;
        file.InstrumentId.Should().Be(_analyser.Id);
        page.Find("[data-state=equipment-message]").TextContent.Should().Contain("NC-2026-0031");
    }

    [Fact]
    public void WithoutTheRightToReport_AFailedCheckIsOnlySaid()
    {
        SignIn(AppLanguage.French, Permissions.Equipment.View, Permissions.Equipment.RecordMaintenance);
        _analyser.LastCalibrationOn = new DateOnly(2026, 10, 10);
        _queries.On(nameof(IInventoryQueries.GetEquipmentAsync), _ => new[] { _lapsed with { CalibrationOverdue = false } });

        var page = Render<EquipmentScreen>();
        page.Find("[data-action='Equipment.RecordMaintenance']").Click();
        page.Find($"[data-field={EquipmentScreenRules.DescriptionField}]").Input("Contrôle après nettoyage");
        page.Find("[data-field=maintenance-postcheck]").Change(false);
        page.Find("[data-modal=maintenance] [data-action='Action.Save']").Click();

        page.FindAll("[data-modal=confirm]").Should().BeEmpty();
        page.Find("[data-state=equipment-message]").TextContent.Should().Contain("mis hors service");
    }

    [Fact]
    public void APassingCalibration_WithoutItsCalibratorLot_IsRefusedInItsBox_ThenRecorded()
    {
        SignIn(AppLanguage.French, Everything);

        var page = Render<EquipmentScreen>();
        page.Find("[data-action='Equipment.RecordCalibration']").Click();
        page.Find("[data-state=lot-notice]").TextContent.Should().Contain("obligatoire");
        page.Find($"[data-field={EquipmentScreenRules.ValidUntilField}]").GetAttribute("value").Should().Be("2026-11-18");

        page.Find("[data-modal=calibration] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={EquipmentScreenRules.LotField}]").TextContent.Should().Contain("lot d'étalon");

        page.Find($"[data-field={EquipmentScreenRules.LotField}]").Input("CAL-2611");
        page.Find($"[data-field={EquipmentScreenRules.SlopeField}]").Input("1,02");
        page.Find("[data-modal=calibration] [data-action='Action.Save']").Click();

        var calibration = _analyser.Calibrations.Single();
        calibration.CalibratorLotNumber.Should().Be("CAL-2611");
        calibration.Slope.Should().Be(1.02m);
        calibration.ValidUntil.Should().Be(new DateOnly(2026, 11, 18));
        page.FindAll("[data-modal=calibration]").Should().BeEmpty();
    }

    [Fact]
    public void AReturnToService_IsRefusedWhileTheCalibrationIsLapsed_AndSaidInTheDialog()
    {
        SignIn(AppLanguage.French, Everything);
        _analyser.PlaceOutOfService("Panne");

        var page = Render<EquipmentScreen>();
        page.Find("[data-action='Equipment.ChangeStatus']").Click();
        page.Find("[data-state=status-notice]").TextContent.Should().Contain("étalonnage");

        page.Find($"[data-field={EquipmentScreenRules.ReasonField}]").Input("ok");
        page.Find("[data-modal=equipment-status] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={EquipmentScreenRules.ReasonField}]").TextContent.Should().Contain("motivé");

        page.Find($"[data-field={EquipmentScreenRules.ReasonField}]").Input("Réparation terminée");
        page.Find("[data-modal=equipment-status] [data-action='Action.Save']").Click();

        _analyser.Status.Should().Be(EquipmentStatus.OutOfService);
        page.Find("[data-modal=equipment-status] [data-state=refusal]").TextContent.Should().NotBeNullOrWhiteSpace();
        _work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(0);

        page.Find("[data-field=status-new]").Change("Decommissioned");
        page.Find("[data-state=status-notice]").TextContent.Should().Contain("définitive");
        page.Find("[data-modal=equipment-status] [data-action='Action.Save']").Click();
        _analyser.Status.Should().Be(EquipmentStatus.Decommissioned);
        AuditReason.Should().Be("AUT-01 → Decommissioned : Réparation terminée");
    }

    [Fact]
    public void ADecommissionedInstrument_TakesNothingMore_AndEachActionKeepsItsRight()
    {
        SignIn(AppLanguage.French, Everything);
        var page = Render<EquipmentScreen>();
        page.Find("[data-grid=equipment] [data-row='BAL-07']").Click();

        page.Find("[data-state=instrument-warning]").TextContent.Should().Contain("réformé");
        page.Find("[data-action='Equipment.RecordMaintenance']").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-action='Equipment.RecordCalibration']").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-action='Equipment.ChangeStatus']").HasAttribute("disabled").Should().BeTrue();

        var reader = new WebEquipmentTests();
        reader.SignIn(AppLanguage.French, Permissions.Equipment.View);
        var read = reader.Render<EquipmentScreen>();
        read.FindAll("[data-action='Equipment.RecordMaintenance']").Should().BeEmpty();
        read.FindAll("[data-action='Equipment.RecordCalibration']").Should().BeEmpty();
        read.FindAll("[data-action='Equipment.ChangeStatus']").Should().BeEmpty();
        read.FindAll("[data-grid=equipment] tbody tr").Should().HaveCount(3);
        reader.Dispose();
    }

    [Fact]
    public void TheScreen_IsInArabic_ForAnArabicSession()
    {
        SignIn(AppLanguage.Arabic, Everything);

        var page = Render<EquipmentScreen>();

        page.Find("[data-action='Equipment.RecordMaintenance']").TextContent.Should().Contain("تسجيل تدخل");
        page.Find("[data-grid=equipment] [data-row='AUT-01']").TextContent.Should().Contain("جهاز الدم");
        page.Markup.Should().NotContain("[[", "every text of the screen has its Arabic");
    }
}
