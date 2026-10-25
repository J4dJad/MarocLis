using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using InstrumentContracts = MarocLIS.Web.Components.Screens.Equipment.InstrumentContracts;
using Rules = MarocLIS.Tests.Desktop.EquipmentScreenRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Contrats automates » in the browser: the dashboard with a commitment at risk on the alarm's fill
/// and a contract ending within 90 days in the warning's colours, the interventions of the contract
/// with a delay exceeded marked on its cell, a contract refused in its box then saved, the signed copy
/// downloaded and replaced only once confirmed, an arrival before the call refused in its box then
/// recorded, and each action kept by its right — through the workstation's InstrumentContractService.
/// </summary>
public sealed class WebInstrumentContractTests : WebScreenContext
{
    private readonly Fake<IInstrumentContractQueries> _queries = Fake<IInstrumentContractQueries>.Create();
    private readonly Fake<IRepository<InstrumentContract>> _contracts = Fake<IRepository<InstrumentContract>>.Create();
    private readonly Fake<IRepository<Instrument>> _instruments = Fake<IRepository<Instrument>>.Create();
    private readonly Fake<IRepository<PurchasingDocument>> _documents = Fake<IRepository<PurchasingDocument>>.Create();
    private readonly Fake<IUnitOfWork> _work = Fake<IUnitOfWork>.Create();

    private static readonly string[] Everything =
    [
        Permissions.Equipment.ViewContracts, Permissions.Equipment.ManageContracts, Permissions.Equipment.RecordMaintenance
    ];

    private readonly InstrumentContractRow _atRisk, _ending;
    private readonly Instrument _analyser;
    private readonly MaintenanceRecord _repair;
    private readonly ContractInterventionRow _late;
    private readonly Guid _supplierId = Guid.NewGuid();

    public WebInstrumentContractTests()
    {
        Services.AddSingleton(_queries.Object);
        Services.AddSingleton(_contracts.Object);
        Services.AddSingleton(_instruments.Object);
        Services.AddSingleton(_documents.Object);
        Services.AddSingleton(_work.Object);
        Services.AddScoped<InstrumentContractService>();

        _analyser = new Instrument("AUT-01", new MultilingualText("Automate hématologie", "جهاز الدم"), EquipmentKind.Analyser, Guid.NewGuid());
        _repair = _analyser.AddMaintenance(new MaintenanceRecord(MaintenanceKind.Corrective, new DateOnly(2026, 10, 12),
                                                                 new MultilingualText("Pompe remplacée")));
        _late = Rules.Intervention(exceeded: true) with { InstrumentId = _analyser.Id, MaintenanceRecordId = _repair.Id };

        _atRisk = Rules.Contract("CTR-2024-07", atRisk: true, delays: 1, file: "contrat-signe.pdf");
        _ending = Rules.Contract("CMD-2023-02", endingSoon: true, daysToEnd: 40);
        _queries.On(nameof(IInstrumentContractQueries.GetContractsAsync), _ => new[] { _atRisk, _ending });
        _queries.On(nameof(IInstrumentContractQueries.GetInterventionsAsync), args => (Guid)args[0]! == _atRisk.Id ? new[] { _late } : Array.Empty<ContractInterventionRow>());
        _queries.On(nameof(IInstrumentContractQueries.GetOptionsAsync), _ => new ContractOptions(
            [new ContractLookup(_supplierId, "BIO", new MultilingualText("Biosystems Maroc"))],
            [new ContractLookup(_analyser.Id, "AUT-01", new MultilingualText("Automate hématologie", "جهاز الدم"))],
            []));
        _queries.On(nameof(IInstrumentContractQueries.GetDocumentAsync), _ => ((string, byte[])?)("contrat-signe.pdf", [1, 2, 3]));
        _contracts.On(nameof(IRepository<InstrumentContract>.GetByIdAsync), _ =>
            new InstrumentContract("CTR-2024-07", _supplierId, Guid.NewGuid(), InstrumentContractKind.Placement, new DateOnly(2024, 1, 1), 36));
        _instruments.On(nameof(IRepository<Instrument>.GetByIdWithDetailsAsync), _ => _analyser);
    }

    private string? AuditReason => _work.Calls.LastOrDefault(c => c.Method == "set_AuditReason").Arguments is [string reason] ? reason : null;

    [Fact]
    public void TheDashboard_SaysACommitmentAtRisk_AndAContractEndingSoon_InTheirColours()
    {
        SignIn(AppLanguage.French, Everything);

        var page = Render<InstrumentContracts>();

        var risk = page.Find("[data-grid=contracts] [data-row='CTR-2024-07']");
        risk.ClassName.Should().Contain("stock-alarm");
        risk.QuerySelector(".pill")!.GetAttribute("data-tone").Should().Be("Critical");
        risk.TextContent.Should().Contain("Risque de non-atteinte").And.Contain("Biosystems Maroc");

        var ending = page.Find("[data-grid=contracts] [data-row='CMD-2023-02']");
        ending.ClassName.Should().NotContain("stock-alarm");
        ending.QuerySelector(".pill")!.GetAttribute("data-tone").Should().Be("Warning");
        ending.TextContent.Should().Contain("Fin proche");

        page.Find("[data-state=alert-rule]").TextContent.Should().Contain("90 %");

        var late = page.Find("[data-grid=interventions] tbody tr");
        late.QuerySelectorAll("td")[7].ClassName.Should().Contain("critical", "a delay beyond the contract's is marked on its cell");
        late.TextContent.Should().Contain("Pompe remplacée");

        page.Find("[data-grid=contracts] [data-row='CMD-2023-02']").Click();
        page.Find("[data-state=no-intervention]").Should().NotBeNull();

        page.Find("[data-field=contract-search]").Input("CTR");
        page.FindAll("[data-grid=contracts] tbody tr").Should().ContainSingle();
    }

    [Fact]
    public void ANewContract_IsRefusedInItsBox_ThenSaved()
    {
        SignIn(AppLanguage.French, Everything);
        var page = Render<InstrumentContracts>();

        page.Find("[data-action='Contracts.New']").Click();
        page.Find("[data-modal=instrument-contract] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={InstrumentContractScreenRules.ReferenceField}]").TextContent.Should().Contain("référence du contrat");

        page.Find($"[data-field={InstrumentContractScreenRules.ReferenceField}]").Input("CTR-2026-11");
        page.Find($"[data-field={InstrumentContractScreenRules.SupplierField}]").Change(_supplierId.ToString());
        page.Find("[data-field=contract-basis]").Change(nameof(CommitmentBasis.TestsPerYear));
        page.Find($"[data-field={InstrumentContractScreenRules.CommittedField}]").Input("4O 000");
        page.Find("[data-field=contract-instrument-AUT-01]").Change(true);
        page.Find("[data-modal=instrument-contract] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={InstrumentContractScreenRules.CommittedField}]").TextContent.Should().Contain("« 4O 000 »");
        _work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(0);

        page.Find($"[data-field={InstrumentContractScreenRules.CommittedField}]").Input("40 000");
        page.Find("[data-modal=instrument-contract] [data-action='Action.Save']").Click();

        var added = (InstrumentContract)_contracts.Calls.Single(c => c.Method == nameof(IRepository<InstrumentContract>.AddAsync)).Arguments[0]!;
        added.Reference.Should().Be("CTR-2026-11");
        added.CommittedVolume.Should().Be(40_000m);
        added.Instruments.Should().ContainSingle(i => i.InstrumentId == _analyser.Id);
        AuditReason.Should().Be("Création du contrat automate CTR-2026-11");
        page.FindAll("[data-modal=instrument-contract]").Should().BeEmpty();
    }

    [Fact]
    public void TheSignedCopy_IsDownloaded_AndReplacedOnlyOnceConfirmed_ARefusedKindSaidInItsBox()
    {
        SignIn(AppLanguage.French, Everything);
        var page = Render<InstrumentContracts>();

        page.Find("[data-action='Contracts.OpenFile']").Click();
        page.WaitForAssertion(() => JSInterop.Invocations.Should().Contain(i => i.Identifier == "marocLis.download"
                                                                               && (string)i.Arguments[0]! == "contrat-signe.pdf"));

        page.Find("[data-action='Contracts.AttachFile']").Click();
        page.Find("[data-prompt=confirm]").TextContent.Should().Contain("contrat-signe.pdf").And.Contain("archivé");
        page.Find("[data-modal=confirm] [data-action='Action.Yes']").Click();

        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("@echo off", "contrat.bat"));
        page.Find($"[data-refusal={InstrumentContractScreenRules.FileField}]").TextContent.Should().NotBeNullOrWhiteSpace();
        _documents.CallsTo(nameof(IRepository<PurchasingDocument>.AddAsync)).Should().Be(0);

        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([0x25, 0x50, 0x44, 0x46], "avenant-2026.pdf"));
        page.WaitForAssertion(() => _documents.CallsTo(nameof(IRepository<PurchasingDocument>.AddAsync)).Should().Be(1));
        AuditReason.Should().Be("Contrat CTR-2024-07 : fichier avenant-2026.pdf joint");
    }

    [Fact]
    public void AnArrivalBeforeTheCall_IsRefusedInItsBox_ThenTheTimesAreRecorded()
    {
        SignIn(AppLanguage.French, Everything);
        var page = Render<InstrumentContracts>();
        page.Find("[data-grid=interventions] tbody tr").Click();

        page.Find("[data-action='Contracts.InterventionTimes']").Click();
        page.Find("[data-field=times-has-requested]").Change(true);
        page.Find("[data-field=times-has-arrived]").Change(true);
        page.Find($"[data-field={InstrumentContractScreenRules.RequestedField}]").Change("2026-10-12T14:00");
        page.Find($"[data-field={InstrumentContractScreenRules.ArrivedField}]").Change("2026-10-12T09:00");
        page.Find("[data-modal=intervention-times] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={InstrumentContractScreenRules.ArrivedField}]").TextContent.Should().Contain("ne peut pas précéder");
        _work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(0);

        page.Find($"[data-field={InstrumentContractScreenRules.ArrivedField}]").Change("2026-10-12T16:30");
        page.Find("[data-modal=intervention-times] [data-action='Action.Save']").Click();

        _repair.ArrivedAt!.Value.LocalDateTime.Should().Be(new DateTime(2026, 10, 12, 16, 30, 0));
        _repair.RequestedAt!.Value.LocalDateTime.Should().Be(new DateTime(2026, 10, 12, 14, 0, 0));
        AuditReason.Should().StartWith("Heures d'appel et d'arrivée du technicien sur AUT-01");
        page.FindAll("[data-modal=intervention-times]").Should().BeEmpty();
    }

    [Fact]
    public void AReader_SeesTheDashboard_AndOpensTheSignedCopy_ButChangesNothing()
    {
        SignIn(AppLanguage.French, Permissions.Equipment.ViewContracts);

        var page = Render<InstrumentContracts>();

        page.FindAll("[data-grid=contracts] tbody tr").Should().HaveCount(2);
        page.FindAll("[data-action='Contracts.New']").Should().BeEmpty();
        page.FindAll("[data-action='Action.Edit']").Should().BeEmpty();
        page.FindAll("[data-action='Contracts.AttachFile']").Should().BeEmpty();
        page.FindAll("[data-action='Contracts.InterventionTimes']").Should().BeEmpty();
        page.Find("[data-action='Contracts.OpenFile']").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void TheScreen_IsInArabic_ForAnArabicSession()
    {
        SignIn(AppLanguage.Arabic, Everything);

        var page = Render<InstrumentContracts>();

        page.Find("[data-action='Contracts.New']").TextContent.Should().Contain("عقد جديد");
        page.Find("[data-grid=contracts] [data-row='CTR-2024-07']").TextContent.Should().Contain("خطر عدم بلوغ الالتزام");
        page.Markup.Should().NotContain("[[", "every text of the screen has its Arabic");
    }
}
