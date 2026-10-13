using MarocLIS.Application.Administration;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Quality;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Quality;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Persistence.Demo;

/// <summary>
/// The quality side of the demonstration: internal control with a Levey-Jennings history and a
/// rejection awaiting review, non-conformities, controlled documents, external quality assessment
/// and purchasing.
/// </summary>
public sealed partial class DemoDataSeeder
{
    // ---- Internal quality control --------------------------------------------------------------

    private sealed record DemoQcTarget(string Lot, string Parameter, decimal Mean, decimal Sd,
                                       decimal MaxCv);

    private static readonly DemoQcTarget[] QcTargets =
    [
        new("DEMO-QCB-2611-N1", "GLY", 1.00m, 0.03m, 5m),
        new("DEMO-QCB-2611-N2", "GLY", 2.60m, 0.08m, 5m),
        new("DEMO-QCB-2611-N1", "CREAT", 12.0m, 0.45m, 6m),
        new("DEMO-QCB-2611-N2", "CREAT", 45.0m, 1.6m, 6m)
    ];

    /// <summary>
    /// Z-scores of the nineteen past runs, oldest first: scattered around the mean the way a stable
    /// method scatters, with one excursion past 2 SD on the glucose normal level so the chart shows
    /// a warning point as well as accepted ones.
    /// </summary>
    private static readonly decimal[] QcHistory =
        [0.4m, -0.6m, 0.9m, -0.3m, 1.2m, -0.8m, 0.2m, -1.1m, 0.7m, 0.1m,
         -0.5m, 1.6m, -0.4m, 0.3m, -0.9m, 0.8m, -0.2m, 0.5m, -0.7m];

    /// <summary>
    /// Two control levels on the biochemistry analyser, a target per level for glucose and
    /// creatinine, and nineteen days of history behind each.
    /// <para>
    /// The history is written directly — the control service can only record a run "now", and a
    /// Levey-Jennings chart with one point is not a chart. Each past point is nonetheless evaluated
    /// by the same <see cref="WestgardEvaluator"/> the service uses, against the points before it,
    /// so the outcomes on the chart are the ones the application would have given. Today's runs go
    /// through the service, in <see cref="SeedTodaysQcRunsAsync"/>.
    /// </para>
    /// </summary>
    private async Task SeedQualityControlAsync(CancellationToken ct)
    {
        var instruments = await InstrumentIdsAsync(ct);
        if (!instruments.TryGetValue("DEMO-AU480", out var au480)) return;

        var parameters = await ParameterIdsAsync(["GLY", "CREAT"], ct);
        if (parameters.Count == 0) return;

        var today = clock.Today;

        (string Lot, string Name, QcLevel Level)[] materials =
        [
            ("DEMO-QCB-2611-N1", "Contrôle biochimie niveau 1 (démo)", QcLevel.Normal),
            ("DEMO-QCB-2611-N2", "Contrôle biochimie niveau 2 (démo)", QcLevel.High)
        ];

        var materialIds = await QcMaterialIdsAsync(ct);

        foreach (var m in materials.Where(m => !materialIds.ContainsKey(m.Lot)))
        {
            Check(await AsAsync<MasterDataService, Result<Guid>>(DemoActor.QualityManager,
                      s => s.SaveQcMaterialAsync(new QcMaterialRequest
                      {
                          LotNumber = m.Lot,
                          Name = new MultilingualText(m.Name),
                          Level = m.Level,
                          ExpiryDate = today.AddDays(200),
                          Manufacturer = "Bio-Rad",
                          CatalogueReference = "DEMO-694",
                          OpenedOn = today.AddDays(-3),
                          StabilityAfterOpeningDays = 7
                      }, ct), ct),
                  $"Matériel de contrôle {m.Lot}");
        }

        materialIds = await QcMaterialIdsAsync(ct);

        foreach (var t in QcTargets)
        {
            if (!materialIds.TryGetValue(t.Lot, out var materialId)
                || !parameters.TryGetValue(t.Parameter, out var parameterId))
                continue;

            var targetId = await QueryAsync(db => db.QcTargets.AsNoTracking()
                .Where(x => x.QcMaterialId == materialId && x.TestParameterId == parameterId
                            && x.InstrumentId == au480)
                .Select(x => (Guid?)x.Id)
                .FirstOrDefaultAsync(ct));

            if (targetId is null)
            {
                var saved = await AsAsync<MasterDataService, Result<Guid>>(DemoActor.QualityManager,
                    s => s.SaveQcTargetAsync(new QcTargetRequest
                    {
                        QcMaterialId = materialId,
                        TestParameterId = parameterId,
                        InstrumentId = au480,
                        AssignedMean = t.Mean,
                        AssignedSd = t.Sd,
                        MaxAcceptableCvPercent = t.MaxCv,
                        ActiveRules = WestgardRule.OneThreeS | WestgardRule.TwoTwoS | WestgardRule.R4S
                                      | WestgardRule.FourOneS | WestgardRule.TenX | WestgardRule.OneTwoS
                    }, ct), ct);

                if (!Check(saved, $"Cible de contrôle {t.Parameter} {t.Lot}")) continue;

                targetId = saved.Value;
            }

            await SeedQcHistoryAsync(targetId.Value, t, ct);
        }
    }

    private async Task SeedQcHistoryAsync(Guid targetId, DemoQcTarget demo, CancellationToken ct)
    {
        var hasHistory = await QueryAsync(db => db.QcResults.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(r => r.QcTargetId == targetId, ct));

        if (hasHistory) return;

        var technician = _actors[DemoActor.Technician];
        var start = clock.Now.Date;

        await WriteAsync(async db =>
        {
            var target = await db.QcTargets.FirstAsync(x => x.Id == targetId, ct);
            var level = await db.QcMaterials.AsNoTracking()
                .Where(m => m.Id == target.QcMaterialId).Select(m => m.Level).FirstAsync(ct);

            var history = new List<QcPoint>();
            var days = QcHistory.Length;

            for (var i = 0; i < days; i++)
            {
                var z = QcHistory[i];

                // The warning point: glucose, normal level, past 2 SD but inside 3.
                if (i == 11 && demo is { Parameter: "GLY", Lot: "DEMO-QCB-2611-N1" }) z = 2.15m;

                var value = Math.Round(demo.Mean + z * demo.Sd, 3);
                var measuredAt = new DateTimeOffset(start.AddDays(i - days).AddHours(8).AddMinutes(10 + i),
                                                    clock.Now.Offset);

                var result = new QcResult(target.Id, target.TestParameterId, target.InstrumentId,
                                          _siteId, value, measuredAt, technician)
                {
                    QcRunId = Guid.CreateVersion7(),
                    TargetMean = target.EffectiveMean,
                    TargetSd = target.EffectiveSd,
                    ZScore = target.ZScore(value)
                };

                history.Add(new QcPoint(result.ZScore ?? 0m, measuredAt, result.QcRunId, level));

                var evaluation = WestgardEvaluator.Evaluate(target.ActiveRules,
                                                            history.TakeLast(12).ToList());
                result.ApplyEvaluation(evaluation);

                if (evaluation.Outcome is QcOutcome.Accepted or QcOutcome.Warning)
                    target.AccumulatePoint(value);

                db.QcResults.Add(result);
            }
        }, ct);
    }

    /// <summary>
    /// This morning's two control runs, through the service: creatinine in control, glucose
    /// rejected on its normal level by the 1-3s rule. The rejection is left unreviewed so the
    /// review screen has it, and — run after the orders — it flags the glucose result of the
    /// diabetes follow-up awaiting validation, which the validation screen then refuses to release
    /// without a justification.
    /// </summary>
    private async Task SeedTodaysQcRunsAsync(CancellationToken ct)
    {
        var instruments = await InstrumentIdsAsync(ct);
        if (!instruments.TryGetValue("DEMO-AU480", out var au480)) return;

        var materialIds = await QcMaterialIdsAsync(ct);
        var parameters = await ParameterIdsAsync(["GLY", "CREAT"], ct);
        var startOfDay = new DateTimeOffset(clock.Now.Date, clock.Now.Offset);

        // Glucose first, both levels in one run; then creatinine.
        (string Parameter, decimal NormalZ, decimal HighZ)[] runs = [("GLY", 3.4m, 0.3m), ("CREAT", 0.2m, -0.5m)];

        foreach (var run in runs)
        {
            if (!parameters.TryGetValue(run.Parameter, out var parameterId)) continue;

            var targets = await QueryAsync(db => db.QcTargets.AsNoTracking()
                .Where(t => t.TestParameterId == parameterId && t.InstrumentId == au480
                            && materialIds.Values.Contains(t.QcMaterialId))
                .Select(t => new { t.Id, t.QcMaterialId, t.AssignedMean, t.AssignedSd })
                .ToListAsync(ct));

            if (targets.Count == 0) continue;

            var targetIds = targets.Select(t => t.Id).ToList();

            var doneToday = await QueryAsync(db => db.QcResults.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(r => targetIds.Contains(r.QcTargetId) && r.MeasuredAt >= startOfDay, ct));

            if (doneToday) continue;

            var entries = targets
                .Select(t =>
                {
                    var isNormal = materialIds.TryGetValue("DEMO-QCB-2611-N1", out var n) && n == t.QcMaterialId;
                    var z = isNormal ? run.NormalZ : run.HighZ;
                    return new QcEntry(t.Id, Math.Round((t.AssignedMean ?? 0m) + z * (t.AssignedSd ?? 0m), 3),
                                       Comment: isNormal && z > 3m
                                           ? "Valeur hors limites — recherche de cause en cours."
                                           : null);
                })
                .ToList();

            var submitted = await AsAsync<QualityControlService, Result<QcSubmissionOutcome>>(
                DemoActor.Technician, s => s.SubmitAsync(entries, ct), ct);

            Check(submitted, $"Série de contrôle {run.Parameter}");
        }
    }

    private Task<Dictionary<string, Guid>> QcMaterialIdsAsync(CancellationToken ct) =>
        QueryAsync(db => db.QcMaterials.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.LotNumber.StartsWith(DemoPrefix) && !m.IsDeleted)
            .ToDictionaryAsync(m => m.LotNumber, m => m.Id, ct));

    // ---- Non-conformities ----------------------------------------------------------------------

    private async Task SeedNonConformitiesAsync(CancellationToken ct)
    {
        var today = clock.Today;

        // A pre-analytical one, on the tube rejected at reception.
        var order = await QueryAsync(db => db.LabOrders.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.InternalNote == DemoMarker && o.Patient!.MedicalRecordNumber == "DEMO-000012")
            .Select(o => new { o.Id, o.AccessionNumber })
            .FirstOrDefaultAsync(ct));

        if (order is not null
            && !await QueryAsync(db => db.NonConformities.IgnoreQueryFilters().AnyAsync(n => n.LabOrderId == order.Id, ct)))
        {
            var sampleId = await QueryAsync(db => db.Samples.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.LabOrderId == order.Id && s.RejectedAt != null)
                .Select(s => (Guid?)s.Id)
                .FirstOrDefaultAsync(ct));

            await OpenNonConformityAsync(new CreateNonConformityRequest
            {
                Category = NonConformityCategory.PreAnalytical,
                Severity = NonConformitySeverity.Minor,
                Description = new MultilingualText(
                    $"Tube EDTA coagulé reçu pour le dossier {order.AccessionNumber} : numération impossible, " +
                    "nouveau prélèvement nécessaire."),
                DetectionSource = DetectionSource.StaffObservation,
                LabOrderId = order.Id,
                SampleId = sampleId,
                ImmediateAction = new MultilingualText("Tube refusé, patient rappelé pour un nouveau prélèvement."),
                AssignedToUserId = _actors[DemoActor.QualityManager],
                DueDate = today.AddDays(15)
            },
            new(ActionKind.Correction,
                "Rappel au personnel de prélèvement : retourner le tube EDTA 8 à 10 fois dès le prélèvement.",
                DemoActor.Technician, 7, Start: true), ct);
        }

        // An equipment one, on the analyser out of service.
        var instruments = await InstrumentIdsAsync(ct);

        if (instruments.TryGetValue("DEMO-CA600", out var ca600)
            && !await QueryAsync(db => db.NonConformities.IgnoreQueryFilters().AnyAsync(n => n.InstrumentId == ca600, ct)))
        {
            await OpenNonConformityAsync(new CreateNonConformityRequest
            {
                Category = NonConformityCategory.Equipment,
                Severity = NonConformitySeverity.Major,
                Description = new MultilingualText(
                    "Panne de la lampe halogène de l'automate d'hémostase CA-600 : TP/INR impossibles sur site."),
                DetectionSource = DetectionSource.InstrumentAlarm,
                InstrumentId = ca600,
                ImmediateAction = new MultilingualText(
                    "Automate mis hors service ; TP/INR transmis au laboratoire partenaire."),
                AssignedToUserId = _actors[DemoActor.QualityManager],
                DueDate = today.AddDays(10)
            },
            new(ActionKind.CorrectiveAction,
                "Remplacement de la lampe par le fournisseur puis vérification des performances (CQ deux niveaux).",
                DemoActor.QualityManager, 10, Start: false), ct);
        }
    }

    private sealed record DemoAction(ActionKind Kind, string Description, DemoActor Responsible,
                                     int DueInDays, bool Start);

    private async Task OpenNonConformityAsync(CreateNonConformityRequest request, DemoAction action,
                                              CancellationToken ct)
    {
        var opened = await AsAsync<NonConformityService, Result<string>>(DemoActor.QualityManager,
            s => s.OpenAsync(request, ct), ct);

        if (!Check(opened, "Non-conformité")) return;

        var ncId = await QueryAsync(db => db.NonConformities.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.Reference == opened.Value)
            .Select(n => n.Id)
            .FirstAsync(ct));

        var added = await AsAsync<NonConformityService, Result>(DemoActor.QualityManager,
            s => s.AddActionAsync(new AddActionRequest(ncId, action.Kind, new MultilingualText(action.Description),
                                                       _actors[action.Responsible], clock.Today.AddDays(action.DueInDays)), ct), ct);

        if (!Check(added, $"Action {opened.Value}") || !action.Start) return;

        var actionId = await QueryAsync(db => db.CorrectiveActions.AsNoTracking()
            .Where(a => a.NonConformityId == ncId)
            .Select(a => a.Id)
            .FirstAsync(ct));

        Check(await AsAsync<NonConformityService, Result>(DemoActor.QualityManager,
                  s => s.StartActionAsync(ncId, actionId, ct), ct),
              $"Démarrage de l'action {opened.Value}");
    }

    // ---- Controlled documents ------------------------------------------------------------------

    private sealed record DemoDocument(string Reference, string Title, DocumentKind Kind, string Process,
                                       bool Acknowledge, bool MakeEffective, string[] Body);

    private static readonly DemoDocument[] Documents =
    [
        new("DEMO-MQ-001", "Manuel qualité du laboratoire", DocumentKind.QualityManual, "MQ",
            false, true,
            [
                "1. Présentation du laboratoire et de ses activités.",
                "2. Politique qualité et engagements de la direction.",
                "3. Organisation, responsabilités et autorités.",
                "4. Maîtrise documentaire et enregistrements.",
                "5. Processus pré-analytiques, analytiques et post-analytiques.",
                "6. Non-conformités, actions correctives et amélioration continue."
            ]),

        new("DEMO-PR-PRE-001", "Procédure de prélèvement sanguin", DocumentKind.Procedure, "PRE",
            true, true,
            [
                "Objet : décrire le prélèvement veineux au pli du coude.",
                "Identification du patient : nom, prénom et date de naissance demandés au patient.",
                "Ordre des tubes : hémoculture, citrate, sec, héparine, EDTA, fluorure.",
                "Retourner chaque tube 8 à 10 fois immédiatement après le prélèvement.",
                "Étiqueter les tubes devant le patient, jamais à l'avance."
            ]),

        new("DEMO-MO-AU480", "Mode opératoire de l'automate AU480", DocumentKind.WorkInstruction, "ANA",
            false, false,
            [
                "Mise en route quotidienne et contrôle des niveaux de réactifs.",
                "Passage des contrôles internes deux niveaux avant toute série patient.",
                "Conduite à tenir en cas de contrôle rejeté (règles de Westgard).",
                "Maintenance journalière, hebdomadaire et mensuelle."
            ])
    ];

    /// <summary>
    /// Three documents at different points of their life: the quality manual and the collection
    /// procedure in force (the procedure requiring acknowledgement, and acknowledged by the
    /// technician), and an analyser instruction still under review. Written by the quality
    /// manager, reviewed and approved by the biologist — never the author, which the domain
    /// enforces.
    /// </summary>
    private async Task SeedDocumentsAsync(CancellationToken ct)
    {
        var existing = await QueryAsync(db => db.ControlledDocuments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.Reference.StartsWith(DemoPrefix))
            .Select(d => d.Reference)
            .ToListAsync(ct));

        foreach (var doc in Documents.Where(d => !existing.Contains(d.Reference)))
        {
            var created = await AsAsync<DocumentControlService, Result<Guid>>(DemoActor.QualityManager,
                s => s.CreateAsync(new CreateControlledDocumentRequest
                {
                    Reference = doc.Reference,
                    Title = new MultilingualText(doc.Title + " (démo)"),
                    Kind = doc.Kind,
                    ProcessCode = doc.Process,
                    ReviewPeriodMonths = 24,
                    RequiresAcknowledgement = doc.Acknowledge
                }, ct), ct);

            if (!Check(created, $"Document {doc.Reference}")) continue;

            var id = created.Value;
            var pdf = DemoAssets.CreateDocumentPdf($"{doc.Reference} — {doc.Title}", doc.Body);

            var draft = await AsAsync<DocumentControlService, Result<int>>(DemoActor.QualityManager,
                s => s.AddDraftAsync(id, new MultilingualText("Création du document."), pdf,
                                     $"{doc.Reference}.pdf", "application/pdf", ct), ct);

            if (!Check(draft, $"Version {doc.Reference}")) continue;

            var version = draft.Value;

            if (!Check(await AsAsync<DocumentControlService, Result>(DemoActor.QualityManager,
                           s => s.SubmitForReviewAsync(id, version, ct), ct), $"Soumission {doc.Reference}"))
                continue;

            if (!doc.MakeEffective) continue;

            if (!Check(await AsAsync<DocumentControlService, Result>(DemoActor.Biologist,
                           s => s.ReviewAsync(id, version, ct), ct), $"Revue {doc.Reference}")
                || !Check(await AsAsync<DocumentControlService, Result>(DemoActor.Biologist,
                              s => s.ApproveAsync(id, version, ct), ct), $"Approbation {doc.Reference}")
                || !Check(await AsAsync<DocumentControlService, Result>(DemoActor.Biologist,
                              s => s.MakeEffectiveAsync(id, version, clock.Today, ct), ct),
                          $"Mise en application {doc.Reference}"))
                continue;

            if (doc.Acknowledge)
            {
                Check(await AsAsync<DocumentControlService, Result>(DemoActor.Technician,
                          s => s.AcknowledgeAsync(id, ct), ct), $"Prise de connaissance {doc.Reference}");
            }
        }
    }

    // ---- External quality assessment -----------------------------------------------------------

    /// <summary>
    /// Two rounds of an invented scheme: last month's, submitted and evaluated satisfactory, and
    /// this month's, registered and awaiting the laboratory's results before its deadline.
    /// </summary>
    private static readonly string[] EqaPastParameters = ["GLY", "CREAT"];
    private static readonly string[] EqaCurrentParameters = ["HB", "TSH"];

    private async Task SeedEqaAsync(CancellationToken ct)
    {
        var today = clock.Today;
        const string scheme = "Programme national d'évaluation externe (démo)";

        var instruments = await InstrumentIdsAsync(ct);
        var au480 = instruments.TryGetValue("DEMO-AU480", out var a) ? a : (Guid?)null;

        var parameters = await ParameterIdsAsync(["GLY", "CREAT", "HB", "TSH"], ct);

        var rounds = await QueryAsync(db => db.EqaParticipations.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.RoundReference.StartsWith(DemoPrefix))
            .Select(p => p.RoundReference)
            .Distinct()
            .ToListAsync(ct));

        var past = $"DEMO-{today.AddMonths(-1):yyyy-MM}";
        var current = $"DEMO-{today:yyyy-MM}";

        if (!rounds.Contains(past))
        {
            Guid[] ids = [.. EqaPastParameters.Where(parameters.ContainsKey).Select(c => parameters[c])];

            if (ids.Length > 0)
            {
                var registered = await AsAsync<EqaService, Result<int>>(DemoActor.QualityManager,
                    s => s.RegisterRoundAsync(new RegisterEqaRoundRequest
                    {
                        SchemeName = scheme,
                        RoundReference = past,
                        RoundDate = today.AddDays(-40),
                        SubmissionDeadline = today.AddDays(-26),
                        InstrumentId = au480,
                        TestParameterIds = ids
                    }, ct), ct);

                if (Check(registered, $"Évaluation externe {past}"))
                {
                    var participations = await QueryAsync(db => db.EqaParticipations.IgnoreQueryFilters().AsNoTracking()
                        .Where(p => p.RoundReference == past)
                        .Select(p => new { p.Id, p.TestParameterId })
                        .ToListAsync(ct));

                    foreach (var p in participations)
                    {
                        var isGlucose = parameters.TryGetValue("GLY", out var g) && g == p.TestParameterId;

                        var (reported, assigned, mean, sd) = isGlucose
                            ? (1.02m, 1.00m, 1.01m, 0.04m)
                            : (11.6m, 12.0m, 11.9m, 0.5m);

                        if (!Check(await AsAsync<EqaService, Result>(DemoActor.QualityManager,
                                       s => s.RecordSubmissionAsync(p.Id, reported, today.AddDays(-30), ct), ct),
                                   $"Résultat EEQ {past}"))
                            continue;

                        Check(await AsAsync<EqaService, Result<string?>>(DemoActor.QualityManager,
                                  s => s.RecordEvaluationAsync(p.Id, new EqaEvaluation
                                  {
                                      AssignedValue = assigned,
                                      PeerGroupMean = mean,
                                      PeerGroupSd = sd
                                  }, ct), ct),
                              $"Évaluation EEQ {past}");
                    }
                }
            }
        }

        if (!rounds.Contains(current))
        {
            Guid[] ids = [.. EqaCurrentParameters.Where(parameters.ContainsKey).Select(c => parameters[c])];

            if (ids.Length > 0)
            {
                Check(await AsAsync<EqaService, Result<int>>(DemoActor.QualityManager,
                          s => s.RegisterRoundAsync(new RegisterEqaRoundRequest
                          {
                              SchemeName = scheme,
                              RoundReference = current,
                              RoundDate = today.AddDays(-3),
                              SubmissionDeadline = today.AddDays(11),
                              TestParameterIds = ids
                          }, ct), ct),
                      $"Évaluation externe {current}");
            }
        }
    }

    // ---- Purchasing ----------------------------------------------------------------------------

    /// <summary>
    /// A reagent order raised by the accountant, approved by the administrator (never the person
    /// who raised it), sent and partly delivered; and a consumables order still in draft.
    /// </summary>
    private async Task SeedPurchaseOrdersAsync(CancellationToken ct)
    {
        var suppliers = await SupplierIdsAsync(ct);

        var items = await QueryAsync(db => db.StockItems.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.Code.StartsWith(DemoPrefix) && i.SiteId == _siteId)
            .ToDictionaryAsync(i => i.Code, i => i.Id, ct));

        var existing = await QueryAsync(db => db.PurchaseOrders.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Notes == DemoMarker)
            .Select(p => p.SupplierId)
            .ToListAsync(ct));

        (string Supplier, (string Item, decimal Quantity, decimal Price)[] Lines, bool Process)[] orders =
        [
            ("DEMO-FRN-BIODIS", [("DEMO-REA-GLU", 6m, 1450m), ("DEMO-REA-CRP", 4m, 980m)], true),
            ("DEMO-FRN-LABEQ", [("DEMO-CONS-EDTA", 20m, 85m), ("DEMO-CONS-GANT", 30m, 45m)], false)
        ];

        foreach (var po in orders)
        {
            if (!suppliers.TryGetValue(po.Supplier, out var supplierId) || existing.Contains(supplierId))
                continue;

            var created = await AsAsync<PurchaseOrderService, Result<string>>(DemoActor.Secretary,
                s => s.CreateAsync(new CreatePurchaseOrderRequest
                {
                    SupplierId = supplierId,
                    ExpectedDeliveryDate = clock.Today.AddDays(5),
                    SupplierReference = $"DEVIS-{po.Supplier}-2026",
                    Notes = DemoMarker
                }, ct), ct);

            if (!Check(created, $"Bon de commande {po.Supplier}")) continue;

            var poId = await QueryAsync(db => db.PurchaseOrders.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.Number == created.Value)
                .Select(p => p.Id)
                .FirstAsync(ct));

            foreach (var line in po.Lines.Where(l => items.ContainsKey(l.Item)))
            {
                Check(await AsAsync<PurchaseOrderService, Result>(DemoActor.Secretary,
                          s => s.AddLineAsync(poId, new AddPurchaseOrderLineRequest
                          {
                              StockItemId = items[line.Item],
                              Quantity = line.Quantity,
                              UnitPriceMad = line.Price,
                              VatRate = 0.20m
                          }, ct), ct),
                      $"Ligne {line.Item}");
            }

            if (!po.Process) continue;

            if (!Check(await AsAsync<PurchaseOrderService, Result>(DemoActor.Administrator,
                           s => s.ApproveAsync(poId, ct), ct), $"Approbation {created.Value}")
                || !Check(await AsAsync<PurchaseOrderService, Result>(DemoActor.Secretary,
                              s => s.SendAsync(poId, ct), ct), $"Envoi {created.Value}"))
                continue;

            // Half the glucose delivered: the order shows as partly received, and the new lot
            // joins the quarantine list until its acceptance check.
            if (!items.TryGetValue("DEMO-REA-GLU", out var glucoseItem)) continue;

            var glucoseLine = await QueryAsync(db => db.PurchaseOrderLines.AsNoTracking()
                .Where(l => l.PurchaseOrderId == poId && l.StockItemId == glucoseItem)
                .Select(l => (Guid?)l.Id)
                .FirstOrDefaultAsync(ct));

            if (glucoseLine is { } lineId)
            {
                Check(await AsAsync<PurchaseOrderService, Result>(DemoActor.QualityManager,
                          s => s.ReceiveLineAsync(poId, new ReceivePurchaseOrderLineRequest
                          {
                              LineId = lineId,
                              Quantity = 3m,
                              LotNumber = "GLU-2611-207",
                              ExpiryDate = clock.Today.AddDays(300),
                              DeliveryNoteReference = "BL-DEMO-BIODIS-0412",
                              StorageLocation = "Réfrigérateur réactifs R2"
                          }, ct), ct),
                      $"Réception {created.Value}");
            }
        }
    }
}
