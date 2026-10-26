using Bunit;
using FluentAssertions;
using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Web.Components.Screens.HumanResources;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Dossiers du personnel », « Habilitations » and « Échéances RH » in the browser: the staff and
/// their files, the pay kept to whoever holds the salary right, the papers, the departure, the
/// habilitation matrix and the deadlines, by right.
/// </summary>
public sealed class WebHumanResourcesTests : WebHumanResourcesContext
{
    private readonly Employee _youssef, _abdelilah, _omar;

    public WebHumanResourcesTests()
    {
        var today = TestClock.Today;
        _youssef = Hire("M0002", "AÏT OUAHMANE", "Youssef", EmployeeJob.Technician, new DateOnly(2021, 3, 1));
        _youssef.AddCredential(new EmployeeCredential(CredentialKind.HepatitisBVaccination, "Rappel hépatite B")
        {
            ObtainedOn = new DateOnly(2021, 3, 10), ExpiresOn = today.AddDays(-3)
        });
        _youssef.AddCredential(new EmployeeCredential(CredentialKind.Diploma, "DTS Analyses biomédicales") { ObtainedOn = new DateOnly(2019, 7, 1) });
        _youssef.Grant(HabilitationActivity.ManualTechnique, Haematology, HabilitationLevel.Authorized, today.AddYears(-1),
                       today.AddDays(-5), Guid.NewGuid(), "Lecture de frottis — 30 lames");

        _abdelilah = Hire("M0007", "EL OUAZZANI TOUHAMI", "Abdelilah", EmployeeJob.Technician, new DateOnly(2025, 10, 1),
                          ContractType.FixedTerm, today.AddDays(-12));
        _omar = Hire("M0008", "BENJELLOUN", "Omar", EmployeeJob.Courier, new DateOnly(2022, 4, 1));
        _omar.RecordExit(today.AddDays(18), ExitReason.Resignation, "Préavis en cours");
    }

    // ---- Dossiers du personnel ---------------------------------------------------------------------

    [Fact]
    public void TheStaff_IsListed_TheContractOverInRed_TheDepartureUnderTheName_AndTheCardWithoutPersonalData()
    {
        SignIn(AppLanguage.French, HrManager);
        var page = Render<PersonnelScreen>();

        page.FindAll("[data-grid=personnel] tbody tr").Should().HaveCount(3);
        page.Find("[data-row=M0007] [data-state=contract-end]").TextContent.Should().Be("fin le 06/10/2026");
        page.Find("[data-row=M0008] [data-state=departure]").TextContent.Should().Be("sortie prévue le 05/11/2026");
        page.Find("[data-state=count]").TextContent.Should().Be("3 en poste · 3 affichés");

        page.Find("[data-row=M0002]").Click();
        var card = page.Find("[data-card=employee]").TextContent;
        card.Should().Contain("AÏT OUAHMANE Youssef").And.Contain("M0002");
        card.Should().NotContain("BE902117").And.NotContain("184 552 907").And.NotContain("7400").And.NotContain("Maârif");

        page.Find("[data-field=personnel-search]").Input("benj");
        page.FindAll("[data-grid=personnel] tbody tr").Should().ContainSingle().Which.GetAttribute("data-row").Should().Be("M0008");
    }

    [Fact]
    public void TheDepartureAction_FollowsTheFile_RecordForOneOnStaff_CorrectOrCancelForOneLeaving()
    {
        SignIn(AppLanguage.French, HrManager);
        var page = Render<PersonnelScreen>();

        page.Find("[data-row=M0002]").Click();
        page.FindAll(".action-bar [data-action='Personnel.RecordExit']").Should().ContainSingle();
        page.FindAll(".action-bar [data-action='Personnel.CancelExit']").Should().BeEmpty();

        page.Find("[data-row=M0008]").Click();
        page.FindAll(".action-bar [data-action='Personnel.RecordExit']").Should().BeEmpty();
        page.FindAll(".action-bar [data-action='Personnel.CorrectExit']").Should().ContainSingle();
        page.FindAll(".action-bar [data-action='Personnel.CancelExit']").Should().ContainSingle();
    }

    [Fact]
    public void AnExit_IsRecorded_AndTheStatusSaysSo()
    {
        SignIn(AppLanguage.French, HrManager);
        var page = Render<PersonnelScreen>();

        page.Find("[data-row=M0002]").Click();
        page.Find(".action-bar [data-action='Personnel.RecordExit']").Click();
        page.Find("[data-modal=employee-exit]").TextContent.Should().Contain("Enregistrer la sortie — M0002 AÏT OUAHMANE Youssef");
        page.Find("[data-field=exit-date]").Change(TestClock.Today.AddDays(30).ToString("yyyy-MM-dd"));
        page.Find("[data-field=exit-comment]").Input("Mutation à Rabat");
        page.Find("[data-modal=employee-exit] [data-action='Action.Save']").Click();

        page.FindAll("[data-modal=employee-exit]").Should().BeEmpty();
        _youssef.ExitDate.Should().Be(TestClock.Today.AddDays(30));
        _youssef.ExitComment.Should().Be("Mutation à Rabat");
        page.Find("[data-state=status]").TextContent.Should().Contain("Sortie de M0002 AÏT OUAHMANE Youssef enregistrée");
    }

    [Fact]
    public void TheFile_OpensOnItsIdentity_AndAMalformedCinIsRefusedBesideItsBox_NothingSaved()
    {
        SignIn(AppLanguage.French, HrManager);
        var page = Render<PersonnelScreen>();

        page.Find("[data-row=M0002]").Click();
        page.Find(".action-bar [data-action='Personnel.Edit']").Click();

        page.Find("[data-modal=employee-file]").TextContent.Should().Contain("Modifier le dossier — M0002 AÏT OUAHMANE Youssef");
        page.FindAll("[data-tabs=employee-file] [role=tab]").Select(t => t.GetAttribute("data-tab"))
            .Should().Equal("Identity", "Position", "Contract", "Pay", "Documents");
        page.Find("[data-field=employee-cin]").GetAttribute("value").Should().Be("BE902117");

        page.Find("[data-field=employee-cin]").Input("4821B");
        page.Find("[data-modal=employee-file] [data-action='Action.Save']").Click();

        page.Find("[data-refusal=employee-cin]").TextContent.Should().Contain("La CIN « 4821B » n'est pas valide");
        _youssef.Cin.Should().Be("BE902117");

        page.Find("[data-field=employee-cin]").Input("BE902117");
        page.Find("[data-field=employee-cnss]").Input("18455290A");
        page.Find("[data-modal=employee-file] [data-action='Action.Save']").Click();
        page.Find("[data-refusal=employee-cnss]").TextContent.Should().Contain("Le numéro CNSS « 18455290A » n'est pas valide");
    }

    [Fact]
    public void TheFile_IsSaved_AndTheListSaysSo()
    {
        SignIn(AppLanguage.French, HrManager);
        var page = Render<PersonnelScreen>();

        page.Find("[data-row=M0002]").Click();
        page.Find(".action-bar [data-action='Personnel.Edit']").Click();
        page.Find("[data-field=employee-phone]").Input("0661 42 18 90");
        page.Find("[role=tab][data-tab=Pay]").Click();
        page.Find("[data-field=employee-salary]").GetAttribute("value").Should().Be("7400.00");
        page.Find("[data-field=employee-salary]").Input("7 650");
        page.Find("[data-modal=employee-file] [data-action='Action.Save']").Click();

        page.FindAll("[data-modal=employee-file]").Should().BeEmpty();
        _youssef.Phone.Should().Be("0661 42 18 90");
        _youssef.BaseSalary.Should().Be(7650m);
        page.Find("[data-state=status]").TextContent.Should().Be("Dossier enregistré.");
    }

    [Fact]
    public void WithoutTheSalaryRight_TheFileHasNoPayTab_SaysTheSalaryIsHidden_AndASaveKeepsThePay()
    {
        SignIn(AppLanguage.French, HrClerk);
        var page = Render<PersonnelScreen>();

        page.FindAll(".action-bar [data-action='Settlement.Compute']").Should().BeEmpty();
        page.FindAll(".action-bar [data-action='HrDocument.SalaryAttestation']").Should().BeEmpty();

        page.Find("[data-row=M0002]").Click();
        page.Find(".action-bar [data-action='Personnel.Edit']").Click();
        page.FindAll("[role=tab][data-tab=Pay]").Should().BeEmpty();
        page.Find("[role=tab][data-tab=Contract]").Click();
        page.Find("[data-state=salary-hidden]").TextContent.Should().Contain("Le salaire et le RIB ne sont visibles que des personnes autorisées à consulter les salaires");
        page.Markup.Should().NotContain("7400").And.NotContain("Banque Populaire");

        page.Find("[data-modal=employee-file] [data-action='Action.Save']").Click();
        _youssef.BaseSalary.Should().Be(7400m, "the pay not seen is not written");
        _youssef.BankName.Should().Be("Banque Populaire");
    }

    [Fact]
    public void AReaderOfTheFiles_SeesOnlyTheirDocuments_NoEditNoNewNoDeparture()
    {
        SignIn(AppLanguage.French, Permissions.HumanResources.View);
        var page = Render<PersonnelScreen>();

        page.FindAll(".action-bar [data-action='Personnel.Edit']").Should().BeEmpty();
        page.FindAll(".action-bar [data-action='Personnel.New']").Should().BeEmpty();
        page.FindAll(".action-bar [data-action='Personnel.RecordExit']").Should().BeEmpty();

        page.Find("[data-row=M0002]").Click();
        page.Find(".action-bar [data-action='Personnel.Documents']").Click();
        page.FindAll("[data-tabs=employee-file] [role=tab]").Select(t => t.GetAttribute("data-tab")).Should().Equal("Documents");
        page.Markup.Should().NotContain("BE902117").And.NotContain("Maârif");
        page.FindAll("[data-action='Credential.Add']").Should().BeEmpty();
        page.FindAll("[data-action='Credential.Remove']").Should().BeEmpty();
    }

    [Fact]
    public void TheDocuments_AreListed_TheExpiredInRed_AndOneIsAddedThroughItsEditor()
    {
        SignIn(AppLanguage.French, HrManager);
        var page = Render<PersonnelScreen>();

        page.Find("[data-row=M0002]").Click();
        page.Find(".action-bar [data-action='Personnel.Documents']").Click();

        page.FindAll("[data-grid=credentials] tbody tr").Should().HaveCount(2);
        page.Find("[data-grid=credentials] tr[data-row='Rappel hépatite B']").ClassList.Should().Contain("is-tone-critical-fill");

        page.Find("[data-modal=employee-file] [data-action='Credential.Add']").Click();
        page.Find("[data-field=credential-kind]").Change(nameof(CredentialKind.ContinuingEducation));
        page.Find("[data-field=credential-title]").Input("Formation automate d'hémostase");
        page.Find("[data-field=credential-issued-by]").Input("Sysmex Maroc");
        page.Find("[data-modal=credential] [data-action='Action.Save']").Click();

        page.FindAll("[data-modal=credential]").Should().BeEmpty();
        _youssef.Credentials.Should().Contain(c => c.Title == "Formation automate d'hémostase" && c.IssuedBy == "Sysmex Maroc");
        page.Find("[data-modal=employee-file]").Should().NotBeNull("the file stays open on its documents");
    }

    [Fact]
    public void ADocumentWithNoTitle_IsRefusedBesideItsBox()
    {
        SignIn(AppLanguage.French, HrManager);
        var page = Render<PersonnelScreen>();

        page.Find("[data-row=M0002]").Click();
        page.Find(".action-bar [data-action='Personnel.Documents']").Click();
        page.Find("[data-modal=employee-file] [data-action='Credential.Add']").Click();
        page.Find("[data-modal=credential] [data-action='Action.Save']").Click();

        page.Find("[data-refusal=credential-title]").Should().NotBeNull();
        _youssef.Credentials.Should().HaveCount(2);
    }

    [Fact]
    public void InArabic_TheStaffFilesSpeakArabic()
    {
        SignIn(AppLanguage.Arabic, HrManager);
        var page = Render<PersonnelScreen>();

        page.Find("[data-action='Personnel.New']").TextContent.Should().Contain("ملف جديد");
        page.Find("[data-row=M0007] [data-state=contract-end]").TextContent.Should().StartWith("ينتهي في").And.Contain("06/10/2026");
        page.Markup.Should().NotContain("[[", "every text has its Arabic translation");

        page.Find("[data-row=M0002]").Click();
        page.Find(".action-bar [data-action='Personnel.Edit']").Click();
        page.Markup.Should().NotContain("[[");
    }

    // ---- Habilitations -----------------------------------------------------------------------------

    [Fact]
    public void TheMatrix_ShowsAColumnPerActivity_TheOverdueReviewInRed_AndTheCellSaysWhatIsHeld()
    {
        SignIn(AppLanguage.French, HrManager);
        var page = Render<HabilitationsScreen>();

        page.FindAll("[data-grid=habilitations] tbody tr").Should().HaveCount(3);
        page.FindAll("[data-grid=habilitations] thead th").Select(t => t.TextContent).Should().Contain("Techniques manuelles");
        page.Find("[data-state=count]").TextContent.Should().Be("3 salariés · 1 habilitation · 1 réévaluation en retard");

        var entry = page.Find("tr[data-row=M0002] td[data-activity=ManualTechnique] .hab-entry");
        entry.TextContent.Should().Contain("Hématologie").And.Contain(TestClock.Today.AddDays(-5).ToString("dd/MM/yyyy"));
        entry.ClassList.Should().Contain("hab-cell-overdue");
        entry.Click();
        page.Find("[data-state=detail]").TextContent.Should().Contain("Lecture de frottis — 30 lames");
    }

    [Fact]
    public void AGrant_InTraining_IsSaved_ButTheAuthorisationByANonBiologist_IsRefusedBesideTheLevel()
    {
        SignIn(AppLanguage.French, HrManager);
        var page = Render<HabilitationsScreen>();

        page.Find("tr[data-row=M0007] td[data-activity=AdultCollection]").Click();
        page.Find(".action-bar [data-action='Habilitation.Grant']").Click();
        page.Find("[data-modal=habilitation-grant]").TextContent.Should().Contain("Accorder une habilitation — M0007 EL OUAZZANI TOUHAMI Abdelilah");
        page.Find("[data-field=habilitation-activity]").GetAttribute("value").Should().Be(nameof(HabilitationActivity.AdultCollection));

        page.Find("[data-modal=habilitation-grant] [data-action='Action.Save']").Click();
        page.Find("[data-refusal=habilitation-level]").TextContent.Should().Contain("Seul un biologiste peut habiliter");
        _abdelilah.Habilitations.Should().BeEmpty();

        page.Find("[data-field=habilitation-level]").Change(nameof(HabilitationLevel.InTraining));
        page.Find("[data-field=habilitation-evidence]").Input("Compagnonnage avec Mme Tazi");
        page.Find("[data-modal=habilitation-grant] [data-action='Action.Save']").Click();

        page.FindAll("[data-modal=habilitation-grant]").Should().BeEmpty();
        _abdelilah.Habilitations.Should().ContainSingle(h => h.Level == HabilitationLevel.InTraining && h.Activity == HabilitationActivity.AdultCollection);
        page.Find("[data-state=status]").TextContent.Should().Contain("accordée à EL OUAZZANI TOUHAMI Abdelilah");
        page.Find("tr[data-row=M0007] td[data-activity=AdultCollection] .hab-entry").TextContent.Should().Contain("En formation");
    }

    [Fact]
    public void AWithdrawal_AsksItsReason()
    {
        SignIn(AppLanguage.French, HrManager);
        var page = Render<HabilitationsScreen>();

        page.Find("tr[data-row=M0002] td[data-activity=ManualTechnique] .hab-entry").Click();
        page.Find(".action-bar [data-action='Habilitation.Withdraw']").Click();
        page.Find("[data-modal=reason]").TextContent.Should().Contain("Retirer l'habilitation « Techniques manuelles » de AÏT OUAHMANE Youssef");
        page.Find("[data-field=reason]").Input("Réévaluation non concluante");
        page.Find("[data-modal=reason] [data-action='Action.Ok']").Click();

        _youssef.Habilitations.Single().IsActive.Should().BeFalse();
        page.FindAll("tr[data-row=M0002] td[data-activity=ManualTechnique] .hab-entry").Should().BeEmpty();
    }

    [Fact]
    public void WithoutTheHabilitationRight_TheMatrixIsReadOnly_ButCanBeExported()
    {
        SignIn(AppLanguage.French, HrClerk);
        var page = Render<HabilitationsScreen>();

        page.FindAll(".action-bar [data-action='Habilitation.Grant']").Should().BeEmpty();
        page.FindAll(".action-bar [data-action='Habilitation.Withdraw']").Should().BeEmpty();
        page.Find(".action-bar [data-action='Habilitation.Export']").HasAttribute("disabled").Should().BeFalse();
    }

    // ---- Échéances RH ------------------------------------------------------------------------------

    [Fact]
    public void TheDeadlines_AreListed_TheOverdueFirst_WrittenInFull_AndALineOpensTheDocuments()
    {
        var today = TestClock.Today;
        PersonnelQueries.On(nameof(IPersonnelQueries.GetDeadlineFactsAsync), _ => (IReadOnlyList<EmployeeDeadlineFacts>)
        [
            new(_abdelilah.Id, "M0007", "EL OUAZZANI TOUHAMI Abdelilah", EmployeeJob.Technician, true, today.AddYears(-1),
                ContractType.FixedTerm, today.AddDays(-12), null,
                [(CredentialKind.HepatitisBVaccination, "Vaccination hépatite B", null, null, new DateOnly(2018, 3, 2))], []),
            new(_youssef.Id, "M0002", "AÏT OUAHMANE Youssef", EmployeeJob.Technician, false, today.AddYears(-2),
                ContractType.Permanent, null, null, [(CredentialKind.MedicalVisit, "Visite périodique", today.AddDays(12), MedicalFitness.Fit, today.AddYears(-1))],
                [(HabilitationActivity.ManualTechnique, "Hématologie", today.AddDays(-5))])
        ]);
        SignIn(AppLanguage.French, HrManager);
        var page = Render<PersonnelDeadlinesScreen>();

        var rows = page.FindAll("[data-grid=hrdeadlines] tbody tr");
        rows.Should().NotBeEmpty();
        rows.TakeWhile(r => r.TextContent.Contains("En retard") || r.TextContent.Contains("Manquant")).Should().HaveCount(3,
            "the contract over, the review overdue and the vaccination missing come first");
        page.Find("tr[data-row=M0007][data-kind=ContractEnd]").TextContent.Should().Contain("En retard").And.Contain("Fin de contrat");
        page.Find("tr[data-row=M0002][data-kind=CredentialExpiry]").TextContent.Should().Contain("Dans 12 jours");
        page.Find("tr[data-row=M0002][data-kind=HabilitationReview]").TextContent.Should().Contain("Techniques manuelles — Hématologie");

        page.Find("tr[data-row=M0002][data-kind=CredentialExpiry]").DoubleClick();
        page.Find("[data-modal=employee-file] [role=tab].is-selected").GetAttribute("data-tab").Should().Be("Documents");
    }

    [Fact]
    public void AReaderOfTheDeadlines_OpensTheDocuments_ButNotTheFile()
    {
        PersonnelQueries.On(nameof(IPersonnelQueries.GetDeadlineFactsAsync), _ => (IReadOnlyList<EmployeeDeadlineFacts>)
        [
            new(_youssef.Id, "M0002", "AÏT OUAHMANE Youssef", EmployeeJob.Technician, false, null, ContractType.Permanent, null, null, [], [])
        ]);
        SignIn(AppLanguage.French, Permissions.HumanResources.View);
        var page = Render<PersonnelDeadlinesScreen>();

        page.FindAll(".action-bar [data-action='Personnel.Edit']").Should().BeEmpty();
        page.Find(".action-bar [data-action='Personnel.Documents']").HasAttribute("disabled").Should().BeFalse();
    }
}
