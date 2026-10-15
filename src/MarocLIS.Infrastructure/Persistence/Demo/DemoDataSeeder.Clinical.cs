using System.Globalization;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Morocco;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Reports;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Persistence.Demo;

/// <summary>
/// The clinical side of the demonstration: thirteen patients and one order each, every order left
/// at a different point of the pre-analytical, analytical and post-analytical path so that each
/// worklist has rows on it.
/// </summary>
public sealed partial class DemoDataSeeder
{
    // ---- Patients ------------------------------------------------------------------------------

    private sealed record DemoPatient(
        string Number, string LastName, string FirstName, string LastNameArabic,
        string FirstNameArabic, DateOnly DateOfBirth, Gender Gender, string? Cin, string City,
        MoroccanRegion Region, string Address, string? PayerCode = null, string? Membership = null,
        Action<Patient>? Complete = null, string? Alert = null);

    /// <summary>
    /// Invented people, with well-formed but invented identifiers. The phone numbers are all in
    /// the +2126000000xx block so a mistaken SMS from a demonstration database goes nowhere.
    /// </summary>
    private List<DemoPatient> DemoPatients()
    {
        var today = clock.Today;

        return
        [
            new("DEMO-000001", "Benani", "Fatima Zahra", "بناني", "فاطمة الزهراء",
                new DateOnly(1979, 4, 12), Gender.Female, "BE123456", "Casablanca",
                MoroccanRegion.CasablancaSettat, "12, rue Ibn Batouta, Maârif", "CNSS", "CN-8841203"),

            new("DEMO-000002", "El Amrani", "Youssef", "العمراني", "يوسف",
                new DateOnly(1992, 11, 3), Gender.Male, "EA987654", "Rabat",
                MoroccanRegion.RabatSaleKenitra, "45, avenue Hassan II, Agdal"),

            // A minor: registration requires a guardian, and the paediatric reference intervals
            // are the ones a demonstration most often needs to show.
            new("DEMO-000003", "Ouazzani", "Salma", "الوزاني", "سلمى",
                today.AddYears(-6), Gender.Female, null, "Fès",
                MoroccanRegion.FesMeknes, "8, derb Bennis, Médina",
                Complete: p =>
                {
                    p.GuardianFullName = "Ouazzani Karim";
                    p.GuardianCin = "OU445566";
                    p.GuardianRelationship = "Père";
                    p.PreferredLanguage = AppLanguage.Arabic;
                }),

            new("DEMO-000004", "Tazi", "Imane", "التازي", "إيمان",
                new DateOnly(1996, 2, 19), Gender.Female, "TA112233", "Marrakech",
                MoroccanRegion.MarrakechSafi, "23, rue Moulay Ismaïl, Guéliz", "CNOPS", "CO-5527741",
                Complete: p =>
                {
                    p.IsPregnant = true;
                    p.LastMenstrualPeriod = today.AddDays(-126);
                }),

            new("DEMO-000005", "Alaoui", "Mohammed", "العلوي", "محمد",
                today.AddYears(-67).AddDays(-41), Gender.Male, "A245781", "Casablanca",
                MoroccanRegion.CasablancaSettat, "5, rue Jbel Tazekka, Hay Hassani", "CNSS", "CN-1290457",
                Complete: p => p.CurrentTreatment = "Acénocoumarol (Sintrom) 4 mg, 1/2 cp le soir",
                Alert: "Patient sous anticoagulant oral : compression prolongée après prélèvement."),

            new("DEMO-000006", "Berrada", "Khadija", "برادة", "خديجة",
                today.AddYears(-45).AddDays(-120), Gender.Female, "BK330198", "Settat",
                MoroccanRegion.CasablancaSettat, "Résidence Al Amal, imm. 4, apt 12", "CNOPS", "CO-7781320"),

            new("DEMO-000007", "Chraibi", "Abdelkader", "الشرايبي", "عبد القادر",
                today.AddYears(-58).AddDays(-75), Gender.Male, "C401122", "Casablanca",
                MoroccanRegion.CasablancaSettat, "18, boulevard Ghandi", "CNSS", "CN-5512984",
                Complete: p =>
                {
                    p.CurrentTreatment = "Metformine 850 mg × 2/j";
                    p.WeightKg = 86m;
                    p.HeightCm = 172m;
                }),

            new("DEMO-000008", "Idrissi", "Yassine", "الإدريسي", "ياسين",
                today.AddYears(-31).AddDays(-200), Gender.Male, "ID778812", "Casablanca",
                MoroccanRegion.CasablancaSettat, "7, rue Assaad Ibnou Zarara, Maârif"),

            new("DEMO-000009", "Lahlou", "Nadia", "لحلو", "نادية",
                today.AddYears(-52).AddDays(-14), Gender.Female, "LN552210", "Mohammédia",
                MoroccanRegion.CasablancaSettat, "33, avenue Mohammed VI", "DEMO-MUT-ATLAS", "MAS-2026-04471"),

            new("DEMO-000010", "Squalli", "Hamza", "السقلي", "حمزة",
                today.AddYears(-40).AddDays(-300), Gender.Male, "SH119087", "Casablanca",
                MoroccanRegion.CasablancaSettat, "2, rue des Hôpitaux, Bourgogne", "CNSS", "CN-6631078"),

            new("DEMO-000011", "Fassi Fihri", "Leila", "الفاسي الفهري", "ليلى",
                today.AddYears(-29).AddDays(-60), Gender.Female, "FL880341", "Rabat",
                MoroccanRegion.RabatSaleKenitra, "14, rue Oued Ziz, Agdal",
                Complete: p => p.KnownAllergies = "Pénicilline (urticaire)"),

            new("DEMO-000012", "Bennani", "Omar", "بناني", "عمر",
                today.AddYears(-73).AddDays(-9), Gender.Male, "BO051276", "Casablanca",
                MoroccanRegion.CasablancaSettat, "60, rue Moussa Ibnou Noussair", "CNSS", "CN-0457712"),

            new("DEMO-000013", "Ziani", "Amina", "الزياني", "أمينة",
                today.AddYears(-36).AddDays(-180), Gender.Female, "ZA662905", "Kénitra",
                MoroccanRegion.RabatSaleKenitra, "9, avenue Mohammed Diouri", "CNOPS", "CO-3398815")
        ];
    }

    /// <summary>
    /// Written directly: there is no service that creates a patient without an order, and the
    /// demonstration needs the <c>DEMO-</c> record numbers, which the registration sequence would
    /// not issue.
    /// </summary>
    private async Task SeedPatientsAsync(CancellationToken ct)
    {
        var demo = DemoPatients();
        var numbers = demo.Select(p => p.Number).ToList();

        var existing = await QueryAsync(db => db.Patients.IgnoreQueryFilters().AsNoTracking()
            .Where(p => numbers.Contains(p.MedicalRecordNumber))
            .Select(p => p.MedicalRecordNumber)
            .ToListAsync(ct));

        var missing = demo.Where(p => !existing.Contains(p.Number)).ToList();
        if (missing.Count == 0) return;

        var today = clock.Today;
        var phone = 1;

        await WriteAsync(async db =>
        {
            var payers = await db.Payers.IgnoreQueryFilters().AsNoTracking()
                .ToDictionaryAsync(p => p.Code, p => p.Id, ct);

            foreach (var d in demo)
            {
                var number = phone++;
                if (!missing.Contains(d)) continue;

                var patient = new Patient(d.Number, d.LastName, d.FirstName, d.DateOfBirth,
                                          d.Gender, _siteId)
                {
                    LastNameArabic = d.LastNameArabic,
                    FirstNameArabic = d.FirstNameArabic,
                    SearchKey = PatientSearchKey.Build(d.LastName, d.FirstName),
                    Cin = d.Cin,
                    City = d.City,
                    Region = d.Region,
                    AddressLine = d.Address,
                    MobilePhone = $"+2126000000{number:00}",
                    PreferredLanguage = AppLanguage.French
                };

                d.Complete?.Invoke(patient);

                if (d.PayerCode is { } code && payers.TryGetValue(code, out var payerId))
                {
                    patient.AddCoverage(new PatientCoverage(patient.Id, payerId, d.Membership!,
                                                            today.AddYears(-2))
                    {
                        IsPrimary = true,
                        DirectBillingEnabled = true
                    });
                }

                if (d.Alert is { } alert)
                {
                    patient.AddAlert(new PatientAlert(patient.Id, PatientAlertSeverity.Warning,
                                                      new MultilingualText(alert)));
                }

                db.Patients.Add(patient);
            }
        }, ct);
    }

    // ---- Orders --------------------------------------------------------------------------------

    /// <summary>How far an order is taken. Each stage includes the ones before it.</summary>
    private enum DemoStage
    {
        Registered,
        Collected,
        Dispatched,
        Received,
        Entered,
        TechnicallyValidated,
        Validated,
        Reported,
        Delivered
    }

    private sealed record DemoOrder(
        string Patient, DemoStage Stage, string[] Panels, string[] Analyses, string? Prescriber,
        string Clinical, OrderPriority Priority = OrderPriority.Routine,
        OrderOrigin Origin = OrderOrigin.Walkin, bool? Fasted = null, int? FastingHours = null,
        Dictionary<string, string>? Values = null, string? StoragePosition = null,
        int KeepUntilDays = 7, string? RejectTubeOf = null, string? Conclusion = null);

    private static List<DemoOrder> DemoOrders() =>
    [
        new("DEMO-000001", DemoStage.Registered, ["BILAN_STD"], [], "Bennis",
            "Bilan de contrôle annuel.", Fasted: true, FastingHours: 12),

        new("DEMO-000002", DemoStage.Received, [], ["NFS", "CRP", "VS"], "Bennis",
            "Asthénie depuis trois semaines, fébricule vespérale."),

        new("DEMO-000003", DemoStage.Entered, [], ["NFS", "CRP", "GROUPAGE"], "Bennis",
            "Angines récidivantes, bilan avant amygdalectomie."),

        new("DEMO-000004", DemoStage.Delivered, ["BILAN_PRENAT"], [], "El Ouafi",
            "Grossesse de 18 SA, bilan du deuxième trimestre.", Fasted: true, FastingHours: 10,
            StoragePosition: "Rack A — position 04",
            Conclusion: "Bilan prénatal sans anomalie. Antigène HBs négatif."),

        new("DEMO-000005", DemoStage.Registered, [], ["NFS", "TP_INR", "IONO", "CREAT"], "Moutawakil",
            "Surveillance AVK, épistaxis depuis ce matin.", Priority: OrderPriority.Urgent),

        new("DEMO-000006", DemoStage.Dispatched, ["BILAN_LIP"], ["TSH"], "Tahiri",
            "Asthénie et prise de poids, dyslipidémie connue.", Origin: OrderOrigin.CollectionCentre,
            Fasted: true, FastingHours: 12),

        new("DEMO-000007", DemoStage.TechnicallyValidated, ["BILAN_DIAB"], [], "Tahiri",
            "Diabète de type 2 sous metformine, contrôle trimestriel.", Fasted: true, FastingHours: 12,
            Values: new() { ["GLY"] = "1.42", ["HBA1C"] = "7.9" }),

        new("DEMO-000008", DemoStage.TechnicallyValidated, [], ["NFS", "CRP", "TRANSAM"], "Clinique Les Orangers (démo)",
            "Douleur de la fosse iliaque droite, suspicion d'appendicite.", Priority: OrderPriority.Urgent,
            Origin: OrderOrigin.Institution, Values: new() { ["CRP"] = "38", ["GB"] = "13.6" }),

        new("DEMO-000009", DemoStage.Reported, ["BILAN_LIP_CV"], [], "Moutawakil",
            "Bilan de prévention cardiovasculaire, antécédents familiaux.", Fasted: true, FastingHours: 12,
            Values: new() { ["CHOL"] = "2.35", ["TG"] = "1.20", ["HDL"] = "0.52" },
            StoragePosition: "Rack A — position 07",
            Conclusion: "Hypercholestérolémie modérée, à confronter au risque cardiovasculaire global."),

        new("DEMO-000010", DemoStage.Delivered, ["BILAN_PREOP"], [], "Clinique Les Orangers (démo)",
            "Bilan préopératoire, cholécystectomie programmée.", Priority: OrderPriority.Urgent,
            Origin: OrderOrigin.Institution, Fasted: true, FastingHours: 8,
            StoragePosition: "Rack A — position 01", KeepUntilDays: -1),

        new("DEMO-000011", DemoStage.Validated, [], ["ECBU", "CHIM_URIN"], "El Ouafi",
            "Brûlures mictionnelles, contrôle après traitement.",
            Values: new()
            {
                ["CULTURE"] = "Culture stérile après 24 h d'incubation.",
                ["NUM_GERMES"] = "<1000",
                ["ANTIBIOGRAMME"] = "Non réalisé (culture stérile)."
            }),

        new("DEMO-000012", DemoStage.Received, [], ["NFS", "IONO", "CREAT"], "Bennis",
            "Altération de l'état général, déshydratation.", RejectTubeOf: "NFS"),

        // The vitamin D is sent out: received, it waits on the subcontracting list to leave.
        new("DEMO-000013", DemoStage.Received, [], ["GLY", "UREE", "CREAT", DemoSubcontractedCode], "Bennis",
            "Bilan rénal de contrôle, insuffisance rénale chronique.", Fasted: true, FastingHours: 10)
    ];

    /// <summary>Parameters measured on the demonstration biochemistry analyser.</summary>
    private static readonly HashSet<string> Au480Parameters =
        ["GLY", "CREAT", "UREE", "CHOL", "TG", "HDL", "CRP"];

    /// <summary>
    /// Takes every demonstration order as far as its stage, resuming where a previous run stopped.
    /// <para>
    /// Each step is done by the user whose job it is: the secretary registers, the technician
    /// collects, receives, enters and technically validates, the biologist validates and releases,
    /// and the secretary hands the report over. An order whose step is refused stays where it is,
    /// the refusal is reported, and the next order is tried.
    /// </para>
    /// </summary>
    private async Task SeedOrdersAsync(CancellationToken ct)
    {
        var patients = await QueryAsync(db => db.Patients.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.MedicalRecordNumber.StartsWith(DemoPrefix) && p.SiteId == _siteId)
            .Select(p => new { p.MedicalRecordNumber, p.Id, p.LastName, p.FirstName, p.Cin })
            .ToListAsync(ct));

        var prescribers = await QueryAsync(db => db.Prescribers.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Notes == DemoMarker)
            .Select(p => new { p.LastName, p.Id })
            .ToListAsync(ct));

        var analyses = await QueryAsync(db => db.AnalysisTests.AsNoTracking()
            .Where(a => a.IsActive)
            .Select(a => new { a.Code, a.Id })
            .ToListAsync(ct));

        var panels = await QueryAsync(db => db.TestPanels.AsNoTracking()
            .Select(p => new { p.Code, p.Id })
            .ToListAsync(ct));

        var instruments = await InstrumentIdsAsync(ct);
        var au480 = instruments.TryGetValue("DEMO-AU480", out var a) ? a : (Guid?)null;

        foreach (var plan in DemoOrders())
        {
            var patient = patients.FirstOrDefault(p => p.MedicalRecordNumber == plan.Patient);
            if (patient is null) continue;

            var name = $"{patient.LastName} {patient.FirstName}";

            try
            {
                var orderId = await QueryAsync(db => db.LabOrders.IgnoreQueryFilters().AsNoTracking()
                    .Where(o => o.PatientId == patient.Id && o.InternalNote == DemoMarker)
                    .Select(o => (Guid?)o.Id)
                    .FirstOrDefaultAsync(ct));

                if (orderId is null)
                {
                    var analysisIds = plan.Analyses
                        .Select(code => analyses.FirstOrDefault(x => x.Code == code)?.Id)
                        .OfType<Guid>().ToList();

                    var panelIds = plan.Panels
                        .Select(code => panels.FirstOrDefault(x => x.Code == code)?.Id)
                        .OfType<Guid>().ToList();

                    // A laboratory that retired the starter analyses still gets an order, on
                    // whatever it does offer.
                    if (analysisIds.Count == 0 && panelIds.Count == 0)
                        analysisIds = [.. analyses.OrderBy(x => x.Code).Take(2).Select(x => x.Id)];

                    if (analysisIds.Count == 0 && panelIds.Count == 0) continue;

                    var prescriber = prescribers.FirstOrDefault(p => p.LastName == plan.Prescriber);

                    var registered = await AsAsync<OrderRegistrationService, Result<RegisteredOrder>>(
                        DemoActor.Secretary, s => s.RegisterAsync(new RegisterOrderRequest
                        {
                            PatientId = patient.Id,
                            PrescriberId = prescriber?.Id,
                            PrescriberFreeText = prescriber is null ? plan.Prescriber : null,
                            PrescriptionDate = clock.Today.AddDays(-1),
                            AnalysisTestIds = analysisIds,
                            TestPanelIds = panelIds,
                            Priority = plan.Priority,
                            Origin = plan.Origin,
                            ClinicalInformation = plan.Clinical,
                            PatientFasted = plan.Fasted,
                            DeclaredFastingHours = plan.FastingHours,
                            InternalNote = DemoMarker
                        }, ct), ct);

                    if (!Check(registered, $"Dossier {plan.Patient}")) continue;

                    orderId = registered.Value.OrderId;
                }

                await AdvanceOrderAsync(orderId.Value, plan, name, patient.Cin, au480, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _problems.Add($"Dossier {plan.Patient} : {ex.GetType().Name} — {ex.GetBaseException().Message}");
            }
        }
    }

    private sealed record SampleState(Guid Id, SampleStatus Status, bool IsReplacement,
                                      DateTimeOffset? RejectedAt, string? StorageLocation);

    private async Task AdvanceOrderAsync(Guid orderId, DemoOrder plan, string patientName,
                                         string? patientCin, Guid? au480, CancellationToken ct)
    {
        var step = $"Dossier {plan.Patient}";

        async Task<List<SampleState>> SamplesAsync() =>
            await QueryAsync(async db =>
            {
                var rows = await db.Samples.IgnoreQueryFilters().AsNoTracking()
                    .Where(s => s.LabOrderId == orderId && !s.IsDeleted)
                    .Select(s => new { s.Id, s.Status, s.RejectedAt, s.StorageLocation, s.ReplacementSampleId })
                    .ToListAsync(ct);

                var replacements = rows.Where(r => r.ReplacementSampleId is not null)
                                       .Select(r => r.ReplacementSampleId!.Value).ToHashSet();

                return rows.Select(r => new SampleState(r.Id, r.Status, replacements.Contains(r.Id),
                                                        r.RejectedAt, r.StorageLocation)).ToList();
            });

        if (plan.Stage < DemoStage.Collected) return;

        // --- Collection --------------------------------------------------------------------------

        var samples = await SamplesAsync();

        // A tube that replaces a rejected one stays on the collection list: that is the
        // recollection the demonstration is there to show.
        var toCollect = samples.Where(s => s.Status == SampleStatus.AwaitingCollection && !s.IsReplacement)
                               .Select(s => new RecordCollectionRequest
                               {
                                   SampleId = s.Id,
                                   CollectionSite = "Pli du coude gauche",
                                   PatientFasted = plan.Fasted,
                                   DeclaredFastingHours = plan.FastingHours
                               })
                               .ToList();

        if (toCollect.Count > 0)
        {
            if (!Check(await AsAsync<SampleWorkflowService, Result>(DemoActor.Technician,
                           s => s.RecordCollectionAsync(toCollect, ct), ct), $"{step} — prélèvement"))
                return;
        }

        if (plan.Stage == DemoStage.Collected) return;

        // --- Dispatch from the collection centre --------------------------------------------------

        if (plan.Origin == OrderOrigin.CollectionCentre)
        {
            samples = await SamplesAsync();
            var toDispatch = samples.Where(s => s.Status == SampleStatus.Collected).Select(s => s.Id).ToList();

            if (toDispatch.Count > 0)
            {
                // The administrator: the technician role does not carry the dispatch grant, which
                // belongs to the collection centre's staff.
                if (!Check(await AsAsync<SampleWorkflowService, Result>(DemoActor.Administrator,
                               s => s.DispatchAsync(toDispatch, ct: ct), ct), $"{step} — envoi"))
                    return;
            }
        }

        if (plan.Stage == DemoStage.Dispatched) return;

        // --- Rejection of one tube, with a recollection -----------------------------------------

        if (plan.RejectTubeOf is { } rejectCode)
        {
            samples = await SamplesAsync();

            if (samples.All(s => s.RejectedAt is null))
            {
                var tube = await QueryAsync(db => db.LabOrderItems.AsNoTracking()
                    .Where(i => i.LabOrderId == orderId && i.AnalysisTest!.Code == rejectCode)
                    .Select(i => i.SampleId)
                    .FirstOrDefaultAsync(ct));

                if (tube is { } tubeId && samples.Any(s => s.Id == tubeId && s.Status == SampleStatus.Collected))
                {
                    var rejected = await AsAsync<SampleWorkflowService, Result<ReceptionOutcome>>(
                        DemoActor.Technician,
                        s => s.RejectAsync([new RejectSampleRequest(tubeId, SampleRejectionReason.Clotted,
                                               "Caillot visible dans le tube EDTA à la réception.", true)], ct), ct);

                    Check(rejected, $"{step} — refus du tube");
                }
            }
        }

        // --- Reception ---------------------------------------------------------------------------

        samples = await SamplesAsync();

        var toReceive = samples
            .Where(s => s.Status is SampleStatus.Collected or SampleStatus.InTransit && !s.IsReplacement)
            .Select(s => new ReceiveSampleRequest { SampleId = s.Id, TemperatureCelsius = 21.5m })
            .ToList();

        if (toReceive.Count > 0)
        {
            var received = await AsAsync<SampleWorkflowService, Result<ReceptionOutcome>>(
                DemoActor.Technician, s => s.ReceiveAsync(toReceive, ct), ct);

            if (!Check(received, $"{step} — réception")) return;

            foreach (var problem in received.Value.Problems)
                _problems.Add($"{step} — réception : {problem.ResourceKey}");
        }

        if (plan.Stage == DemoStage.Received) return;

        // --- Result entry ------------------------------------------------------------------------

        var sheet = await AsAsync<ResultEntryService, Result<ResultEntrySheet>>(
            DemoActor.Technician, s => s.OpenAsync(orderId, ct: ct), ct);

        if (!Check(sheet, $"{step} — feuille de saisie")) return;

        var entries = sheet.Value.Rows
            .Where(r => r.IsEditable && r.Status == TestResultStatus.Awaited)
            .Select(r => (Row: r, Value: DemoValue(r, plan.Values)))
            .Where(x => x.Value is not null)
            .Select(x => new ResultEntry(x.Row.TestResultId, x.Value,
                                         InstrumentId: Au480Parameters.Contains(x.Row.ParameterCode) ? au480 : null))
            .ToList();

        if (entries.Count > 0)
        {
            var submitted = await AsAsync<ResultEntryService, Result<ResultEntryOutcome>>(
                DemoActor.Technician, s => s.SubmitAsync(orderId, entries, ct), ct);

            if (!Check(submitted, $"{step} — saisie")) return;

            foreach (var rejected in submitted.Value.Rejected)
                _problems.Add($"{step} — valeur refusée : {rejected.ResourceKey}({string.Join("; ", rejected.Args)})");
        }

        if (plan.Stage == DemoStage.Entered) return;

        // --- Technical validation ----------------------------------------------------------------

        sheet = await AsAsync<ResultEntryService, Result<ResultEntrySheet>>(
            DemoActor.Technician, s => s.OpenAsync(orderId, ct: ct), ct);

        if (!Check(sheet, $"{step} — feuille de saisie")) return;

        if (sheet.Value.Rows.Any(r => r.Status == TestResultStatus.Entered))
        {
            if (!Check(await AsAsync<ResultEntryService, Result>(DemoActor.Technician,
                           s => s.TechnicallyValidateAsync(orderId, [], ct), ct),
                       $"{step} — validation technique"))
                return;
        }

        if (plan.Stage == DemoStage.TechnicallyValidated) return;

        // --- Biological validation ---------------------------------------------------------------

        sheet = await AsAsync<ResultEntryService, Result<ResultEntrySheet>>(
            DemoActor.Biologist, s => s.OpenAsync(orderId, ct: ct), ct);

        if (!Check(sheet, $"{step} — feuille de validation")) return;

        var decisions = sheet.Value.Rows
            .Where(r => r.Status == TestResultStatus.TechnicallyValidated)
            .Select(r => new ValidationDecision(r.TestResultId, true))
            .ToList();

        if (decisions.Count > 0)
        {
            var validated = await AsAsync<ValidationService, Result<ValidationOutcome>>(
                DemoActor.Biologist, s => s.ValidateAsync(orderId, decisions, ct: ct), ct);

            if (!Check(validated, $"{step} — validation biologique")) return;

            foreach (var blocked in validated.Value.Blocked)
                _problems.Add($"{step} — validation bloquée : {blocked.ResourceKey}({string.Join("; ", blocked.Args)})");
        }

        if (plan.Stage == DemoStage.Validated) return;

        // --- Release -----------------------------------------------------------------------------

        var reportId = await QueryAsync(db => db.LabReports.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.LabOrderId == orderId)
            .OrderByDescending(r => r.VersionNumber)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct));

        if (reportId is null)
        {
            var conclusion = plan.Conclusion is null ? null : new MultilingualText(plan.Conclusion);

            var released = await AsAsync<ReportReleaseService, Result<ReleasedReport>>(
                DemoActor.Biologist,
                s => s.ReleaseAsync(orderId, AppLanguage.French, conclusion, ct: ct), ct);

            if (!Check(released, $"{step} — édition du compte rendu")) return;

            reportId = released.Value.ReportId;
        }

        // --- Serum bank --------------------------------------------------------------------------

        if (plan.StoragePosition is { } position)
        {
            samples = await SamplesAsync();

            foreach (var sample in samples.Where(s => s.StorageLocation is null
                                                      && s.Status is SampleStatus.Received or SampleStatus.InAnalysis))
            {
                Check(await AsAsync<SampleWorkflowService, Result>(DemoActor.Technician,
                          s => s.MoveToStorageAsync(sample.Id, "Sérothèque — réfrigérateur S1", position,
                                                    clock.Today.AddDays(plan.KeepUntilDays), ct), ct),
                      $"{step} — sérothèque");
            }
        }

        if (plan.Stage == DemoStage.Reported) return;

        // --- Hand-over ---------------------------------------------------------------------------

        var delivered = await QueryAsync(db => db.ReportDeliveries.AsNoTracking()
            .AnyAsync(d => d.LabReportId == reportId, ct));

        if (!delivered)
        {
            Check(await AsAsync<ReportReleaseService, Result<DeliveryOutcome>>(DemoActor.Secretary,
                      s => s.RecordDeliveryAsync(reportId.Value, DeliveryChannel.PrintedAtCounter,
                                                 ReportRecipientKind.Patient, patientName, patientCin,
                                                 ct: ct), ct),
                  $"{step} — remise du compte rendu");
        }
    }

    /// <summary>
    /// A plausible value for one result row: the planned value when the scenario names one,
    /// otherwise the middle of the row's own reference interval, so the result reads as normal for
    /// that patient's age and sex.
    /// <para>
    /// Never a critical value unless the scenario asks for one: a critical result obliges somebody
    /// to telephone a prescriber, and a demonstration database that manufactures those hands the
    /// laboratory a queue of calls about people who do not exist.
    /// </para>
    /// </summary>
    private static string? DemoValue(ResultEntryRow row, Dictionary<string, string>? planned)
    {
        if (planned is not null && planned.TryGetValue(row.ParameterCode, out var value)) return value;

        switch (row.ValueType)
        {
            case ResultValueType.Numeric or ResultValueType.NumericWithOperator:
            {
                decimal? number = (row.LowerLimit, row.UpperLimit) switch
                {
                    ({ } low, { } high) => low + (high - low) / 2m,
                    (null, { } high) => high * 0.6m,
                    ({ } low, null) => low * 1.2m,
                    _ => null
                };

                return number is { } n
                    ? Math.Round(n, row.Decimals, MidpointRounding.AwayFromZero)
                          .ToString(CultureInfo.InvariantCulture)
                    : "1";
            }

            case ResultValueType.Coded or ResultValueType.SemiQuantitative:
                return (row.CodedOptions.FirstOrDefault(o => !o.IsAbnormal && !o.IsCritical)
                        ?? (row.CodedOptions.Count > 0 ? row.CodedOptions[0] : null))?.Code;

            case ResultValueType.Text:
                return "Aspect normal.";

            default:
                return null;
        }
    }
}
