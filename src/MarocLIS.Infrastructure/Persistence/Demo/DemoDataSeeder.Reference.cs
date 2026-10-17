using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.Instruments;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Morocco;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Persistence.Demo;

/// <summary>
/// The reference side of the demonstration: the site's legal identity, prescribers, a mutual,
/// suppliers, stock, equipment and the analyser interface.
/// </summary>
public sealed partial class DemoDataSeeder
{
    // ---- Site identity -------------------------------------------------------------------------

    /// <summary>
    /// Completes the site's legal identity, which the seeder leaves as "À COMPLÉTER" on purpose.
    /// <para>
    /// Without it nothing downstream is reachable: <c>ValidateForInvoicing</c> refuses every invoice
    /// and <c>ValidateForOperation</c> every report release, so the billing, claims and report
    /// screens would stay empty however many orders existed. Only what is missing or malformed is
    /// filled; an identifier the laboratory has already entered correctly is kept, so running the
    /// demonstration on a real configuration does not overwrite it. The identifiers are well-formed
    /// and obviously invented (the e-mail is on the reserved <c>.example</c> domain).
    /// </para>
    /// </summary>
    private async Task SeedSiteIdentityAsync(CancellationToken ct)
    {
        var site = await QueryAsync(db => db.Sites.IgnoreQueryFilters().AsNoTracking()
                                            .FirstAsync(s => s.Id == _siteId, ct));

        static bool Missing(string? value) =>
            string.IsNullOrWhiteSpace(value)
            || value.Trim().Equals("À COMPLÉTER", StringComparison.OrdinalIgnoreCase);

        var complete = !Missing(site.LegalName)
                       && MoroccanIdentifiers.IsValidIce(site.Ice)
                       && MoroccanIdentifiers.IsValidFiscalId(site.FiscalId)
                       && !Missing(site.AddressLine.Fr) && !Missing(site.City)
                       && !string.IsNullOrWhiteSpace(site.HealthMinistryAuthorizationNumber)
                       && site.ResponsibleBiologistUserId is not null
                       && site.LogoPng is { Length: > 0 };

        if (complete) return;

        var request = new SiteRequest
        {
            SiteId = site.Id,
            Code = site.Code,
            Name = site.Name,
            Kind = site.Kind,

            LegalName = Missing(site.LegalName)
                ? "LABORATOIRE D'ANALYSES MÉDICALES DÉMO SARL"
                : site.LegalName,
            Ice = MoroccanIdentifiers.IsValidIce(site.Ice) ? site.Ice : "001234567000089",
            FiscalId = MoroccanIdentifiers.IsValidFiscalId(site.FiscalId) ? site.FiscalId : "45128796",
            CommercialRegisterNumber = site.CommercialRegisterNumber ?? "398114",
            CommercialRegisterCourt = site.CommercialRegisterCourt ?? "Tribunal de commerce de Casablanca",
            ProfessionalTaxNumber = site.ProfessionalTaxNumber ?? "35702418",
            CnssAffiliationNumber = MoroccanIdentifiers.IsValidCnssNumber(site.CnssAffiliationNumber)
                ? site.CnssAffiliationNumber
                : "7412589",
            Inpe = MoroccanIdentifiers.IsValidInpe(site.Inpe) ? site.Inpe : "100200300",

            HealthMinistryAuthorizationNumber = string.IsNullOrWhiteSpace(site.HealthMinistryAuthorizationNumber)
                ? "AUT-LAM-DEMO-2019/0457"
                : site.HealthMinistryAuthorizationNumber,
            HealthMinistryAuthorizationDate = site.HealthMinistryAuthorizationDate ?? new DateOnly(2019, 6, 17),

            IsAccredited = site.IsAccredited,
            AccreditationNumber = site.AccreditationNumber,
            AccreditationValidUntil = site.AccreditationValidUntil,

            // The demonstration biologist answers for the site only when nobody does yet.
            ResponsibleBiologistUserId = site.ResponsibleBiologistUserId ?? _actors[DemoActor.Biologist],

            AddressLine = Missing(site.AddressLine.Fr)
                ? new MultilingualText("120, boulevard Zerktouni, 3e étage — Maârif",
                                       "120، شارع الزرقطوني، الطابق 3 — المعاريف",
                                       "120 Zerktouni Boulevard, 3rd floor — Maarif")
                : site.AddressLine,
            City = Missing(site.City) ? "Casablanca" : site.City,
            Region = site.Region == MoroccanRegion.Unspecified ? MoroccanRegion.CasablancaSettat : site.Region,
            PostalCode = site.PostalCode ?? "20330",
            Phone = site.Phone ?? "+212522000000",
            Fax = site.Fax ?? "+212522000001",
            Email = site.Email ?? "contact@labo-demo.example",
            Website = site.Website,

            LogoPng = site.LogoPng is { Length: > 0 } ? null : DemoAssets.CreateLogoPng(),
            IsActive = site.IsActive
        };

        var saved = await AsAsync<ConfigurationService, Result<SiteSaveOutcome>>(
            DemoActor.Administrator, s => s.SaveSiteAsync(request, ct), ct);

        if (!Check(saved, "Identité du site")) return;

        foreach (var blocker in saved.Value.InvoicingBlockers.Concat(saved.Value.OperationBlockers))
            _problems.Add($"Identité du site incomplète : {blocker}");
    }

    // ---- Prescribers and payers ------------------------------------------------------------------

    private async Task SeedPrescribersAndPayersAsync(CancellationToken ct)
    {
        var existing = await QueryAsync(db => db.Prescribers.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Notes == DemoMarker)
            .Select(p => p.LastName)
            .ToListAsync(ct));

        PrescriberRequest[] prescribers =
        [
            new()
            {
                LastName = "Bennis", FirstName = "Karim", FullNameArabic = "كريم بنيس",
                Kind = PrescriberKind.GeneralPractitioner,
                Speciality = new MultilingualText("Médecine générale", "الطب العام", "General practice"),
                ProfessionalOrderNumber = "CNOM-DEMO-11872", Inpe = "102030405",
                Phone = "0522250101", MobilePhone = "0661000101",
                AddressLine = "25, rue Abou Hanifa, Maârif", City = "Casablanca",
                DeliveryPreference = ReportDeliveryPreference.PatientCollects,
                CriticalResultPhone = "0661000101", Notes = DemoMarker
            },
            new()
            {
                LastName = "El Ouafi", FirstName = "Samira", FullNameArabic = "سميرة الوافي",
                Kind = PrescriberKind.Specialist,
                Speciality = new MultilingualText("Gynécologie-obstétrique", "طب النساء والتوليد",
                                                  "Obstetrics and gynaecology"),
                ProfessionalOrderNumber = "CNOM-DEMO-20931", Inpe = "102030512",
                Phone = "0537710202", Email = "s.elouafi@cabinet-demo.example",
                AddressLine = "8, avenue Fal Ould Oumeir, Agdal", City = "Rabat",
                DeliveryPreference = ReportDeliveryPreference.Email, SendPartialReports = true,
                CriticalResultPhone = "0661000202", Notes = DemoMarker
            },
            new()
            {
                LastName = "Tahiri", FirstName = "Hicham", FullNameArabic = "هشام الطاهري",
                Kind = PrescriberKind.Specialist,
                Speciality = new MultilingualText("Endocrinologie-diabétologie", "أمراض الغدد والسكري",
                                                  "Endocrinology and diabetology"),
                ProfessionalOrderNumber = "CNOM-DEMO-17450", Inpe = "102030633",
                Phone = "0522470303", MobilePhone = "0661000303",
                AddressLine = "140, boulevard d'Anfa", City = "Casablanca",
                DeliveryPreference = ReportDeliveryPreference.SmsLink,
                CriticalResultPhone = "0661000303", Notes = DemoMarker
            },
            new()
            {
                LastName = "Moutawakil", FirstName = "Rachid", FullNameArabic = "رشيد المتوكل",
                Kind = PrescriberKind.Specialist,
                Speciality = new MultilingualText("Cardiologie", "أمراض القلب", "Cardiology"),
                ProfessionalOrderNumber = "CNOM-DEMO-15022", Inpe = "102030744",
                Phone = "0524430404", AddressLine = "12, rue de la Liberté, Guéliz",
                City = "Marrakech", DeliveryPreference = ReportDeliveryPreference.PatientCollects,
                Notes = DemoMarker
            },
            new()
            {
                LastName = "Clinique Les Orangers (démo)", FirstName = string.Empty,
                Kind = PrescriberKind.Institution,
                InstitutionName = "Clinique Les Orangers (démo)",
                Phone = "0522990505", AddressLine = "3, rue des Orangers, Racine",
                City = "Casablanca", DeliveryPreference = ReportDeliveryPreference.Courier,
                SendPartialReports = true, DiscountPercent = 10m, Notes = DemoMarker
            }
        ];

        foreach (var prescriber in prescribers.Where(p => !existing.Contains(p.LastName)))
        {
            var saved = await AsAsync<MasterDataService, Result<Guid>>(
                DemoActor.Biologist, s => s.SavePrescriberAsync(prescriber, ct), ct);

            Check(saved, $"Prescripteur {prescriber.LastName}");
        }

        // A mutual with a numbered third-party convention, next to the AMO payers the reference
        // data already ships, so the payer screen shows a complete row and the claims screen has a
        // second kind of third party.
        const string mutualCode = "DEMO-MUT-ATLAS";

        var mutualExists = await QueryAsync(db => db.Payers.IgnoreQueryFilters()
                                                    .AnyAsync(p => p.Code == mutualCode, ct));

        if (!mutualExists)
        {
            var saved = await AsAsync<MasterDataService, Result<Guid>>(DemoActor.Secretary,
                s => s.SavePayerAsync(new PayerRequest
                {
                    Code = mutualCode,
                    Name = new MultilingualText("Mutuelle Atlas Santé (démo)", "تعاضدية أطلس الصحة (تجريبي)",
                                                "Atlas Santé mutual (demo)"),
                    Kind = PayerKind.Mutuelle,
                    DefaultReimbursementRate = 0.80m,
                    UsesNationalReferenceTariff = false,
                    ThirdPartyPaymentAgreement = true,
                    AgreementNumber = "CONV-DEMO-2026-014",
                    AgreementValidUntil = new DateOnly(clock.Today.Year + 1, 12, 31),
                    PaymentTermsDays = 45,
                    RequiresPrescription = true,
                    RequiresInpeOnClaim = false,
                    Ice = "002345678000091",
                    ContactName = "Service conventions",
                    Phone = "0522440606",
                    Email = "conventions@mutuelle-demo.example",
                    AddressLine = "45, avenue des FAR",
                    City = "Casablanca"
                }, ct), ct);

            Check(saved, "Organisme payeur démo");
        }
    }

    // ---- Suppliers and stock -------------------------------------------------------------------

    private sealed record DemoLot(string LotNumber, int ExpiryInDays, decimal Quantity,
                                  string? ReleaseEvidence, string? RejectionReason = null);

    private sealed record DemoStockItem(StockItemRequest Item, string SupplierCode, decimal UnitPrice,
                                        DemoLot[] Lots, decimal IssueOnReceipt = 0m);

    /// <summary>
    /// Three suppliers — two approved, one awaiting evaluation, so the purchase-order screen can
    /// show the refusal — and articles whose lots put every stock alert on screen: an article at its
    /// reorder point, one below its safety stock, a lot about to expire, a lot still in quarantine
    /// and a lot refused at reception.
    /// </summary>
    private async Task SeedSuppliersAndStockAsync(CancellationToken ct)
    {
        var today = clock.Today;

        SupplierRequest[] suppliers =
        [
            new()
            {
                Code = "DEMO-FRN-BIODIS", Name = "Bio-Distribution Maroc (démo)",
                Ice = "003456789000012", FiscalId = "40112233", CommercialRegisterNumber = "287441",
                ContactName = "Nabil Haddaoui", Phone = "0522360707",
                Email = "commandes@biodis-demo.example", AddressLine = "Zone industrielle Sidi Maârouf, lot 18",
                City = "Casablanca", Region = MoroccanRegion.CasablancaSettat,
                LeadTimeDays = 5, PaymentTermsDays = 60, IsApproved = true,
                LastEvaluationDate = today.AddDays(-60), EvaluationScore = 88,
                Notes = "Réactifs de biochimie et contrôles. Évaluation annuelle satisfaisante."
            },
            new()
            {
                Code = "DEMO-FRN-LABEQ", Name = "Labo Équipement Casablanca (démo)",
                Ice = "004567891000023", ContactName = "Sanaa Benjelloun", Phone = "0522480808",
                Email = "sav@labeq-demo.example", AddressLine = "77, rue Ibnou Mounir",
                City = "Casablanca", Region = MoroccanRegion.CasablancaSettat,
                LeadTimeDays = 10, PaymentTermsDays = 30, IsApproved = true,
                LastEvaluationDate = today.AddDays(-420), EvaluationScore = 74,
                Notes = "Consommables de prélèvement et maintenance des automates."
            },
            new()
            {
                Code = "DEMO-FRN-ATLAS", Name = "Fournitures Médicales Atlas (démo)",
                ContactName = "Youssef Amrani", Phone = "0528220909", City = "Agadir",
                Region = MoroccanRegion.SoussMassa, LeadTimeDays = 14, IsApproved = false,
                Notes = "Nouveau fournisseur — évaluation à réaliser avant toute commande."
            }
        ];

        var existingSuppliers = await SupplierIdsAsync(ct);

        foreach (var supplier in suppliers.Where(s => !existingSuppliers.ContainsKey(s.Code)))
        {
            var saved = await AsAsync<MasterDataService, Result<Guid>>(
                DemoActor.Administrator, s => s.SaveSupplierAsync(supplier, ct), ct);

            Check(saved, $"Fournisseur {supplier.Code}");
        }

        var supplierIds = await SupplierIdsAsync(ct);
        var departments = await QueryAsync(db => db.Departments.AsNoTracking()
            .ToDictionaryAsync(d => d.Code, d => d.Id, ct));

        Guid? Department(string code) => departments.TryGetValue(code, out var id) ? id : null;

        const string released = "Contrôle à réception conforme : CQ niveaux 1 et 2 dans les cibles.";

        DemoStockItem[] items =
        [
            new(new StockItemRequest
                {
                    Code = "DEMO-REA-GLU", Name = new MultilingualText("Glucose HK — coffret 4 × 50 mL"),
                    Kind = StockItemKind.Reagent, UnitOfMeasure = "coffret", TestsPerPack = 800,
                    Manufacturer = "Beckman Coulter", DepartmentId = Department(Domain.Catalog.Department.Codes.Biochemistry),
                    UsedByAnalysisCodesCsv = "GLY", ReorderPoint = 3, ReorderQuantity = 6,
                    MinimumStock = 1, MaximumStock = 12, StorageCondition = StorageCondition.Refrigerated,
                    DefaultStorageLocation = "Réfrigérateur réactifs R2", IsCritical = true
                },
                "DEMO-FRN-BIODIS", 1450m,
                [new("GLU-2604-118", 150, 2m, released)]),

            new(new StockItemRequest
                {
                    Code = "DEMO-REA-CREA", Name = new MultilingualText("Créatinine Jaffé — coffret 4 × 51 mL"),
                    Kind = StockItemKind.Reagent, UnitOfMeasure = "coffret", TestsPerPack = 600,
                    Manufacturer = "Beckman Coulter", DepartmentId = Department(Domain.Catalog.Department.Codes.Biochemistry),
                    UsedByAnalysisCodesCsv = "CREAT", ReorderPoint = 2, ReorderQuantity = 4,
                    MinimumStock = 1, StorageCondition = StorageCondition.Refrigerated,
                    DefaultStorageLocation = "Réfrigérateur réactifs R2", IsCritical = true
                },
                "DEMO-FRN-BIODIS", 1180m,
                [new("CRE-2609-044", 12, 3m, released), new("CRE-2702-071", 200, 4m, released)]),

            new(new StockItemRequest
                {
                    Code = "DEMO-REA-CRP", Name = new MultilingualText("CRP latex turbidimétrie — coffret 2 × 40 mL"),
                    Kind = StockItemKind.Reagent, UnitOfMeasure = "coffret", TestsPerPack = 300,
                    Manufacturer = "Beckman Coulter", DepartmentId = Department(Domain.Catalog.Department.Codes.Biochemistry),
                    UsedByAnalysisCodesCsv = "CRP", ReorderPoint = 2, ReorderQuantity = 4,
                    MinimumStock = 1, StorageCondition = StorageCondition.Refrigerated,
                    DefaultStorageLocation = "Réfrigérateur réactifs R2"
                },
                "DEMO-FRN-BIODIS", 980m,
                [
                    new("CRP-2605-310", 60, 3m, released),
                    // Received this morning, awaiting its acceptance check: the quarantine list.
                    new("CRP-2611-402", 240, 6m, null),
                    // Delivered warm: refused, kept on record, never usable.
                    new("CRP-2603-007", 90, 2m, null,
                        "Chaîne du froid rompue à la livraison (indicateur de température viré).")
                ]),

            new(new StockItemRequest
                {
                    Code = "DEMO-CTL-BIO", Name = new MultilingualText("Contrôle biochimie 2 niveaux — 12 × 5 mL"),
                    Kind = StockItemKind.Control, UnitOfMeasure = "boîte", Manufacturer = "Bio-Rad",
                    DepartmentId = Department(Domain.Catalog.Department.Codes.Biochemistry),
                    ReorderPoint = 2, ReorderQuantity = 4, MinimumStock = 1,
                    StorageCondition = StorageCondition.Refrigerated,
                    DefaultStorageLocation = "Réfrigérateur réactifs R2", IsCritical = true
                },
                "DEMO-FRN-BIODIS", 2350m,
                [new("QCB-2611-A", 180, 5m, released)]),

            new(new StockItemRequest
                {
                    Code = "DEMO-CONS-EDTA", Name = new MultilingualText("Tubes EDTA K2 4 mL — boîte de 100"),
                    Kind = StockItemKind.CollectionSupply, UnitOfMeasure = "boîte",
                    ReorderPoint = 10, ReorderQuantity = 20, MinimumStock = 5, MaximumStock = 60,
                    StorageCondition = StorageCondition.RoomTemperature,
                    DefaultStorageLocation = "Salle de prélèvement — armoire 1"
                },
                "DEMO-FRN-LABEQ", 85m,
                [new("EDT-2606-551", 300, 28m, "Contrôle visuel conforme, bouchons intacts.")],
                IssueOnReceipt: 4m),

            new(new StockItemRequest
                {
                    Code = "DEMO-CONS-AIG", Name = new MultilingualText("Aiguilles de prélèvement 21G — boîte de 100"),
                    Kind = StockItemKind.CollectionSupply, UnitOfMeasure = "boîte",
                    ReorderPoint = 5, ReorderQuantity = 10, MinimumStock = 2,
                    StorageCondition = StorageCondition.RoomTemperature,
                    DefaultStorageLocation = "Salle de prélèvement — armoire 1"
                },
                "DEMO-FRN-LABEQ", 60m,
                [new("AIG-2512-090", 400, 3m, "Contrôle visuel conforme.")]),

            new(new StockItemRequest
                {
                    Code = "DEMO-CONS-GANT", Name = new MultilingualText("Gants nitrile taille M — boîte de 100"),
                    Kind = StockItemKind.Consumable, UnitOfMeasure = "boîte",
                    ReorderPoint = 10, ReorderQuantity = 30, MinimumStock = 4,
                    StorageCondition = StorageCondition.RoomTemperature,
                    DefaultStorageLocation = "Réserve — étagère B"
                },
                "DEMO-FRN-LABEQ", 45m,
                [new("GNT-2701-233", 450, 2m, "Contrôle visuel conforme.")])
        ];

        foreach (var demo in items)
        {
            var itemId = await QueryAsync(db => db.StockItems.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.Code == demo.Item.Code && i.SiteId == _siteId)
                .Select(i => (Guid?)i.Id)
                .FirstOrDefaultAsync(ct));

            if (itemId is null)
            {
                var request = demo.Item with
                {
                    PreferredSupplierId = supplierIds.TryGetValue(demo.SupplierCode, out var s) ? s : null
                };

                var saved = await AsAsync<MasterDataService, Result<Guid>>(
                    DemoActor.Administrator, m => m.SaveStockItemAsync(request, ct), ct);

                if (!Check(saved, $"Article {demo.Item.Code}")) continue;

                itemId = saved.Value;
            }

            await SeedLotsAsync(itemId.Value, demo, supplierIds, ct);
        }
    }

    private async Task SeedLotsAsync(Guid itemId, DemoStockItem demo,
                                     Dictionary<string, Guid> supplierIds,
                                     CancellationToken ct)
    {
        var lots = await QueryAsync(db => db.ReagentLots.AsNoTracking()
            .Where(l => l.StockItemId == itemId)
            .Select(l => new { l.Id, l.LotNumber, l.IsQuarantined, l.IsRejected })
            .ToListAsync(ct));

        foreach (var lot in demo.Lots)
        {
            var existing = lots.FirstOrDefault(l => l.LotNumber == lot.LotNumber);
            Guid lotId;
            var quarantined = true;
            var rejected = false;

            if (existing is null)
            {
                var received = await AsAsync<InventoryService, Result<Guid>>(DemoActor.QualityManager,
                    s => s.ReceiveLotAsync(new ReceiveLotRequest
                    {
                        StockItemId = itemId,
                        LotNumber = lot.LotNumber,
                        ExpiryDate = clock.Today.AddDays(lot.ExpiryInDays),
                        Quantity = lot.Quantity,
                        SupplierId = supplierIds.TryGetValue(demo.SupplierCode, out var supplierId) ? supplierId : null,
                        DeliveryNoteReference = $"BL-DEMO-{lot.LotNumber}",
                        UnitPriceMad = demo.UnitPrice,
                        StabilityAfterOpeningDays = demo.Item.Kind == StockItemKind.Reagent ? 28 : null,
                        // The reception checklist: a cold-chain item comes with its arrival temperature.
                        Reception = new ReceptionCheck
                        {
                            ArrivalTemperatureCelsius = ColdChain.RequiresTemperature(demo.Item.StorageCondition)
                                ? 5.2m
                                : null
                        }
                    }, ct), ct);

                if (!Check(received, $"Lot {lot.LotNumber}")) continue;

                lotId = received.Value;
            }
            else
            {
                lotId = existing.Id;
                quarantined = existing.IsQuarantined;
                rejected = existing.IsRejected;
            }

            if (lot.RejectionReason is { } reason && !rejected)
            {
                Check(await AsAsync<InventoryService, Result>(DemoActor.QualityManager,
                          s => s.RejectLotAsync(lotId, reason, ct), ct),
                      $"Refus du lot {lot.LotNumber}");
            }
            else if (lot.ReleaseEvidence is { } evidence && quarantined && !rejected)
            {
                var released = await AsAsync<InventoryService, Result>(DemoActor.QualityManager,
                    s => s.ReleaseLotAsync(lotId, evidence, ct), ct);

                // The demo lots are seeded before any QC run on them: the biologist releases them
                // as a documented exception, as a laboratory starting on the software would.
                if (released.IsFailure
                    && released.FirstError.ResourceKey is "Lot_ReleaseNoQcRun" or "Lot_ReleaseQcRejected"
                                                          or "Lot_ReleaseQcLevelsMissing")
                {
                    released = await AsAsync<InventoryService, Result>(DemoActor.Biologist,
                        s => s.ReleaseLotOverridingQcAsync(lotId, evidence,
                            "Données de démonstration : lot libéré sans CQ sur le lot", ct), ct);
                }

                Check(released, $"Libération du lot {lot.LotNumber}");

                // A first consumption, so the movements history is not only receipts. Only on
                // the run that received the lot, or every start would draw the stock down again.
                if (existing is null && demo.IssueOnReceipt > 0m)
                {
                    Check(await AsAsync<InventoryService, Result<string>>(DemoActor.Technician,
                              s => s.IssueAsync(itemId, demo.IssueOnReceipt, reason: "Approvisionnement salle de prélèvement", ct: ct), ct),
                          $"Sortie de stock {demo.Item.Code}");
                }
            }
        }
    }

    private Task<Dictionary<string, Guid>> SupplierIdsAsync(CancellationToken ct) =>
        QueryAsync(db => db.Suppliers.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Code.StartsWith(DemoPrefix))
            .ToDictionaryAsync(s => s.Code, s => s.Id, ct));

    // ---- Equipment -----------------------------------------------------------------------------

    private sealed record DemoInstrument(InstrumentRequest Request, string? OutOfServiceReason = null,
                                         bool CalibrateToday = false, string? RoutineMaintenance = null);

    /// <summary>
    /// The equipment register of a mid-sized laboratory, arranged so that every indicator on the
    /// equipment screen has something to show: an analyser whose calibration is overdue, a
    /// centrifuge whose preventive maintenance is overdue, an analyser out of service, an expired
    /// maintenance contract, and the rest in order.
    /// </summary>
    private async Task SeedEquipmentAsync(CancellationToken ct)
    {
        var today = clock.Today;

        var departments = await QueryAsync(db => db.Departments.AsNoTracking()
            .ToDictionaryAsync(d => d.Code, d => d.Id, ct));

        Guid? Department(string code) => departments.TryGetValue(code, out var id) ? id : null;

        var suppliers = await SupplierIdsAsync(ct);
        var labeq = suppliers.TryGetValue("DEMO-FRN-LABEQ", out var l) ? l : (Guid?)null;

        DemoInstrument[] instruments =
        [
            new(new InstrumentRequest
                {
                    Code = "DEMO-AU480", Name = new MultilingualText("Automate de biochimie AU480"),
                    Kind = EquipmentKind.Analyser, Manufacturer = "Beckman Coulter", Model = "AU480",
                    SerialNumber = "2017-48011", SoftwareVersion = "3.2.1",
                    Location = "Plateau technique — biochimie",
                    DepartmentId = Department(Domain.Catalog.Department.Codes.Biochemistry),
                    PurchaseDate = new DateOnly(2019, 3, 1), CommissioningDate = new DateOnly(2019, 4, 15),
                    SupplierId = labeq, MaintenanceContractReference = "CTR-DEMO-AU-2026",
                    MaintenanceContractValidUntil = today.AddDays(200), SupportPhone = "0522303030",
                    PreventiveMaintenanceIntervalDays = 90, LastPreventiveMaintenanceOn = today.AddDays(-40),
                    CalibrationIntervalDays = 30, LastCalibrationOn = today.AddDays(-10),
                    VerificationIntervalDays = 180, LastVerificationOn = today.AddDays(-60)
                },
                CalibrateToday: true,
                RoutineMaintenance: "Maintenance journalière : lavage des cuvettes, contrôle des niveaux et de la lampe."),

            // Calibration overdue: the validation screen refuses results produced on it.
            new(new InstrumentRequest
                {
                    Code = "DEMO-XN550", Name = new MultilingualText("Automate d'hématologie XN-550"),
                    Kind = EquipmentKind.Analyser, Manufacturer = "Sysmex", Model = "XN-550",
                    SerialNumber = "XN-31877", Location = "Plateau technique — hématologie",
                    DepartmentId = Department(Domain.Catalog.Department.Codes.Haematology),
                    PurchaseDate = new DateOnly(2021, 9, 20), CommissioningDate = new DateOnly(2021, 10, 4),
                    SupplierId = labeq, MaintenanceContractReference = "CTR-DEMO-XN-2025",
                    MaintenanceContractValidUntil = today.AddDays(-15),
                    PreventiveMaintenanceIntervalDays = 90, LastPreventiveMaintenanceOn = today.AddDays(-30),
                    CalibrationIntervalDays = 180, LastCalibrationOn = today.AddDays(-210)
                }),

            // Second biochemistry analyser, on RS-232 with Roche's own host protocol.
            new(new InstrumentRequest
                {
                    Code = "DEMO-INTEGRA", Name = new MultilingualText("Automate de biochimie cobas INTEGRA 400 plus"),
                    Kind = EquipmentKind.Analyser, Manufacturer = "Roche Diagnostics", Model = "COBAS INTEGRA 400 plus",
                    SerialNumber = "I400-15022", SoftwareVersion = "3.6",
                    Location = "Plateau technique — biochimie",
                    DepartmentId = Department(Domain.Catalog.Department.Codes.Biochemistry),
                    PurchaseDate = new DateOnly(2016, 6, 1), CommissioningDate = new DateOnly(2016, 7, 4),
                    PreventiveMaintenanceIntervalDays = 180, LastPreventiveMaintenanceOn = today.AddDays(-60),
                    CalibrationIntervalDays = 30, LastCalibrationOn = today.AddDays(-5)
                }),

            new(new InstrumentRequest
                {
                    Code = "DEMO-E411", Name = new MultilingualText("Automate d'immunoanalyse cobas e 411"),
                    Kind = EquipmentKind.Analyser, Manufacturer = "Roche Diagnostics", Model = "cobas e 411",
                    SerialNumber = "E411-07733", Location = "Plateau technique — immunologie",
                    DepartmentId = Department(Domain.Catalog.Department.Codes.Hormonology),
                    PurchaseDate = new DateOnly(2020, 1, 10), CommissioningDate = new DateOnly(2020, 2, 3),
                    PreventiveMaintenanceIntervalDays = 180, LastPreventiveMaintenanceOn = today.AddDays(-100),
                    CalibrationIntervalDays = 28, LastCalibrationOn = today.AddDays(-7)
                }),

            new(new InstrumentRequest
                {
                    Code = "DEMO-CA600", Name = new MultilingualText("Automate d'hémostase CA-600"),
                    Kind = EquipmentKind.Analyser, Manufacturer = "Sysmex", Model = "CA-600",
                    SerialNumber = "CA6-20419", Location = "Plateau technique — hémostase",
                    DepartmentId = Department(Domain.Catalog.Department.Codes.Haemostasis),
                    PurchaseDate = new DateOnly(2018, 5, 2),
                    PreventiveMaintenanceIntervalDays = 120, LastPreventiveMaintenanceOn = today.AddDays(-50),
                    CalibrationIntervalDays = 90, LastCalibrationOn = today.AddDays(-20)
                },
                OutOfServiceReason: "Panne de la lampe halogène — intervention du fournisseur demandée, " +
                                    "TP/INR transmis au laboratoire partenaire."),

            // Preventive maintenance overdue.
            new(new InstrumentRequest
                {
                    Code = "DEMO-CENT-01", Name = new MultilingualText("Centrifugeuse Rotina 380"),
                    Kind = EquipmentKind.Centrifuge, Manufacturer = "Hettich", Model = "Rotina 380",
                    SerialNumber = "0012874-03", Location = "Salle de pré-analytique",
                    PreventiveMaintenanceIntervalDays = 90, LastPreventiveMaintenanceOn = today.AddDays(-130),
                    VerificationIntervalDays = 365, LastVerificationOn = today.AddDays(-200)
                }),

            new(new InstrumentRequest
                {
                    Code = "DEMO-FRIGO-R2", Name = new MultilingualText("Réfrigérateur réactifs R2 (+2/+8 °C)"),
                    Kind = EquipmentKind.Refrigerator, Manufacturer = "Liebherr", Model = "LKUv 1610",
                    SerialNumber = "LB-559102", Location = "Plateau technique",
                    PreventiveMaintenanceIntervalDays = 365, LastPreventiveMaintenanceOn = today.AddDays(-90),
                    VerificationIntervalDays = 30, LastVerificationOn = today.AddDays(-12)
                })
        ];

        var existing = await InstrumentIdsAsync(ct);

        foreach (var demo in instruments)
        {
            if (existing.ContainsKey(demo.Request.Code)) continue;

            var saved = await AsAsync<MasterDataService, Result<Guid>>(
                DemoActor.QualityManager, s => s.SaveInstrumentAsync(demo.Request, ct), ct);

            if (!Check(saved, $"Équipement {demo.Request.Code}")) continue;

            var id = saved.Value;

            if (demo.RoutineMaintenance is { } routine)
            {
                Check(await AsAsync<EquipmentService, Result>(DemoActor.Technician,
                          s => s.RecordMaintenanceAsync(id, MaintenanceKind.Routine,
                                                         new MultilingualText(routine), ct: ct), ct),
                      $"Maintenance {demo.Request.Code}");
            }

            if (demo.CalibrateToday)
            {
                Check(await AsAsync<EquipmentService, Result>(DemoActor.QualityManager,
                          s => s.RecordCalibrationAsync(id, null, CalibrationOutcome.Passed, "CAL-2611-A",
                                                         clock.Today.AddDays(30), 1.002m, -0.004m,
                                                         "Étalonnage multiparamétrique après changement de lot.", ct), ct),
                      $"Étalonnage {demo.Request.Code}");
            }

            if (demo.OutOfServiceReason is { } reason)
            {
                Check(await AsAsync<EquipmentService, Result>(DemoActor.Technician,
                          s => s.RecordMaintenanceAsync(id, MaintenanceKind.Corrective,
                                                         new MultilingualText("Diagnostic de panne : lampe halogène défectueuse, pièce commandée."),
                                                         downtimeHours: 6m, postCheckPassed: false,
                                                         externalTechnician: "Technicien Labo Équipement (démo)", ct: ct), ct),
                      $"Maintenance corrective {demo.Request.Code}");

                Check(await AsAsync<EquipmentService, Result>(DemoActor.QualityManager,
                          s => s.ChangeStatusAsync(id, EquipmentStatus.OutOfService, reason, ct), ct),
                      $"Mise hors service {demo.Request.Code}");
            }
        }
    }

    private Task<Dictionary<string, Guid>> InstrumentIdsAsync(CancellationToken ct) =>
        QueryAsync(db => db.Instruments.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.Code.StartsWith(DemoPrefix))
            .ToDictionaryAsync(i => i.Code, i => i.Id, ct));

    /// <summary>
    /// Active parameter ids by code, for the mappings and the control targets. The first active
    /// analysis carrying the code wins, so a laboratory that duplicated a parameter does not break
    /// the demonstration.
    /// </summary>
    private async Task<Dictionary<string, Guid>> ParameterIdsAsync(IReadOnlyCollection<string> codes,
                                                                   CancellationToken ct)
    {
        var rows = await QueryAsync(db => db.AnalysisTests.AsNoTracking()
            .Where(a => a.IsActive)
            .SelectMany(a => a.Parameters)
            .Where(p => p.IsActive && codes.Contains(p.Code))
            .Select(p => new { p.Code, p.Id })
            .ToListAsync(ct));

        return rows.GroupBy(r => r.Code).ToDictionary(g => g.Key, g => g.First().Id);
    }

    // ---- Analyser interface --------------------------------------------------------------------

    /// <summary>
    /// An ASTM link for the biochemistry analyser, with its code mappings and two transmissions in
    /// the message log — one imported, one with an unknown code — so the interface screen, the
    /// mapping grid and the raw-message viewer all have content.
    /// <para>
    /// Left <b>disabled</b>: the address is invented, and an enabled interface makes the instrument
    /// host try to connect to it every poll.
    /// </para>
    /// </summary>
    private async Task SeedInstrumentInterfaceAsync(CancellationToken ct)
    {
        var instruments = await InstrumentIdsAsync(ct);
        if (!instruments.TryGetValue("DEMO-AU480", out var instrumentId)) return;

        var interfaceId = await QueryAsync(db => db.InstrumentInterfaces.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.InstrumentId == instrumentId)
            .Select(i => (Guid?)i.Id)
            .FirstOrDefaultAsync(ct));

        if (interfaceId is null)
        {
            var saved = await AsAsync<InstrumentInterfaceService, Result<Guid>>(DemoActor.Biologist,
                s => s.SaveInterfaceAsync(new InterfaceRequest
                {
                    InstrumentId = instrumentId,
                    Protocol = InstrumentProtocol.AstmE1394,
                    Transport = InstrumentTransportKind.Tcp,
                    Direction = InstrumentDirection.Bidirectional,
                    IsEnabled = false,
                    Host = "192.168.10.48",
                    TcpPort = 5100,
                    ListenAsServer = false,
                    AutoAcceptResults = false,
                    RaiseIssueOnUnmappedCode = true,
                    RouteControlResultsToQc = true
                }, ct), ct);

            if (!Check(saved, "Interface DEMO-AU480")) return;

            interfaceId = saved.Value;

            (string Instrument, string Parameter)[] codes =
            [
                ("GLU", "GLY"), ("CRE", "CREAT"), ("URE", "UREE"),
                ("CHO", "CHOL"), ("TG", "TG"), ("HDL", "HDL"), ("CRP", "CRP")
            ];

            var parameters = await ParameterIdsAsync([.. codes.Select(c => c.Parameter)], ct);

            var mappings = codes
                .Where(c => parameters.ContainsKey(c.Parameter))
                .Select(c => new MappingRequest(c.Instrument, parameters[c.Parameter], null, null,
                                                null, null, IsEnabled: true))
                .ToList();

            if (mappings.Count > 0)
            {
                Check(await AsAsync<InstrumentInterfaceService, Result>(DemoActor.Biologist,
                          s => s.SaveMappingsAsync(interfaceId.Value, mappings, ct), ct),
                      "Correspondances DEMO-AU480");
            }
        }

        var hasTraffic = await QueryAsync(db => db.InstrumentMessageLog.IgnoreQueryFilters()
            .AnyAsync(m => m.InstrumentInterfaceId == interfaceId, ct));

        if (hasTraffic) return;

        await ActAsAsync(DemoActor.Administrator, ct);

        var now = clock.Now;
        var stamp = now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);

        var imported = new InstrumentMessageLog(interfaceId.Value, _siteId, MessageDirection.Inbound,
            $"H|\\^&|||AU480^3.2.1|||||||P|1|{stamp}\r" +
            "P|1||DEMO-000007\r" +
            "O|1|DEMO-TUBE||^^^GLU\\^^^CRE|R\r" +
            "R|1|^^^GLU|1.42|g/L||H||F\r" +
            "R|2|^^^CRE|9.8|mg/L||N||F\r" +
            "L|1|N\r", now.AddMinutes(-45))
        {
            SpecimenIds = "DEMO-TUBE"
        };

        var partial = new InstrumentMessageLog(interfaceId.Value, _siteId, MessageDirection.Inbound,
            $"H|\\^&|||AU480^3.2.1|||||||P|1|{stamp}\r" +
            "P|1||DEMO-000013\r" +
            "O|1|DEMO-TUBE-2||^^^URE\\^^^ALB|R\r" +
            "R|1|^^^URE|0.31|g/L||N||F\r" +
            "R|2|^^^ALB|42|g/L||N||F\r" +
            "L|1|N\r", now.AddMinutes(-20))
        {
            SpecimenIds = "DEMO-TUBE-2"
        };

        await AsAsync<IInstrumentMessageLogStore, bool>(DemoActor.Administrator, async store =>
        {
            var first = await store.RecordAsync(imported, ct);
            await store.MarkProcessedAsync(first, InstrumentMessageProcessingStatus.Imported, 2, 2,
                                           null, now.AddMinutes(-45), ct);

            var second = await store.RecordAsync(partial, ct);
            await store.MarkProcessedAsync(second, InstrumentMessageProcessingStatus.PartiallyImported,
                                           2, 1, "Code automate sans correspondance : ALB",
                                           now.AddMinutes(-20), ct);
            return true;
        }, ct);
    }

    /// <summary>
    /// COBAS INTEGRA 400 plus test numbers (the Test-ID "0-xxx" of Roche's control value sheets for
    /// the INTEGRA 400 plus, serum/plasma application) and the catalogue parameter each measures.
    /// The first parameter code the catalogue has is used, so the table follows the catalogue
    /// whatever its codes. A test number must be checked against the analyser's own test
    /// definitions before the link is enabled: the laboratory may run another application of the
    /// same test (with or without pyridoxal phosphate, urine, STAT).
    /// </summary>
    private static readonly (string TestNumber, string[] Parameters)[] IntegraTests =
    [
        ("031", ["GLY"]),                       // GLUC3 serum/plasma (0-031)
        ("003", ["UREE"]),                      // UREAL (0-003)
        ("445", ["CREAT"]),                     // CREJ2 Jaffé compensé (0-445)
        ("586", ["CHOL"]),                      // CHOL2 (0-586)
        ("010", ["TG"]),                        // TRIGL (0-010)
        ("494", ["ASAT"]),                      // ASTL sans phosphate de pyridoxal (0-494 ; avec : 0-594)
        ("495", ["ALAT"]),                      // ALTL sans phosphate de pyridoxal (0-495 ; avec : 0-595)
        ("551", ["PAL", "ALP"]),                // ALP2S (0-551 ; ALP2L : 0-550)
        ("498", ["GGT", "GAMMA_GT"]),           // GGT-2 IFCC (0-498 ; Szasz : 0-598)
        ("297", ["BILT", "BT", "BILI_T"]),      // BILT3 (0-297 ; autre application : 0-712)
        ("735", ["BILD", "BD", "BILI_D"]),      // BILD2 (0-735 ; BIL-D : 0-249)
        ("615", ["AU", "URIC", "AC_URIQUE"]),   // UA2 (0-615)
        ("042", ["CA", "CALC"]),                // CA2 (0-042)
        ("614", ["PHOS", "PHOSPHORE"]),         // PHOS2 (0-614)
        ("059", ["MG"]),                        // MG (0-059)
        ("592", ["ALB"]),                       // ALB2 (0-592)
        ("027", ["PROT", "PROT_T"]),            // TP2 (0-027 ; monochromatique : 0-227)
        ("596", ["FER", "FE"]),                 // IRON2 (0-596)
        ("609", ["AMY", "AMYL"]),               // AMYL2 sérum/plasma (0-609)
        ("052", ["LIP", "LIPASE"]),             // LIPC (0-052 ; autre application : 0-100)
        ("323", ["CK", "CPK"]),                 // CKL (0-323 ; CK : 0-045)
        ("607", ["LDH"]),                       // LDHI2 (0-607)
        ("501", ["NA"]),                        // Na-I ISE indirecte (0-501 ; directe : 0-401)
        ("502", ["K"]),                         // K-I (0-502 ; directe : 0-402)
        ("503", ["CL"])                         // CL-I (0-503 ; directe : 0-403)
        // HDL, LDL direct, CRP and HbA1c: no INTEGRA 400 plus test number in the sources at
        // hand. Left out rather than guessed; the laboratory adds them from its test definitions.
    ];

    /// <summary>
    /// The COBAS INTEGRA 400 plus link: RS-232, Roche's host protocol, the tubes' orders downloaded
    /// on host query, the test-number table above and one received transmission in the log, for a
    /// tube of the demonstration whose order holds a glucose, so "Tester une trame" has a real tube
    /// to show. Left <b>disabled</b>, like the ASTM one: COM1 is a guess.
    /// </summary>
    private async Task SeedIntegraInterfaceAsync(CancellationToken ct)
    {
        var instruments = await InstrumentIdsAsync(ct);
        if (!instruments.TryGetValue("DEMO-INTEGRA", out var instrumentId)) return;

        var interfaceId = await QueryAsync(db => db.InstrumentInterfaces.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.InstrumentId == instrumentId)
            .Select(i => (Guid?)i.Id)
            .FirstOrDefaultAsync(ct));

        if (interfaceId is null)
        {
            var saved = await AsAsync<InstrumentInterfaceService, Result<Guid>>(DemoActor.Biologist,
                s => s.SaveInterfaceAsync(new InterfaceRequest
                {
                    InstrumentId = instrumentId,
                    Protocol = InstrumentProtocol.RocheIntegra,
                    Transport = InstrumentTransportKind.Serial,
                    Direction = InstrumentDirection.HostQueryDownload,
                    IsEnabled = false,
                    SerialPortName = "COM1",
                    BaudRate = 9600,
                    DataBits = 8,
                    Parity = "None",
                    StopBits = "One",
                    Handshake = "XOnXOff",
                    Encoding = "ASCII",
                    // The manual's recommended host time-out.
                    AcknowledgementTimeoutSeconds = 180,
                    MaxRetries = 3,
                    AutoAcceptResults = false,
                    RaiseIssueOnUnmappedCode = true,
                    RouteControlResultsToQc = true
                }, ct), ct);

            if (!Check(saved, "Interface DEMO-INTEGRA")) return;

            interfaceId = saved.Value;

            var parameters = await ParameterIdsAsync([.. IntegraTests.SelectMany(t => t.Parameters)], ct);

            var mappings = IntegraTests
                .Select(t => (t.TestNumber, Parameter: t.Parameters.FirstOrDefault(parameters.ContainsKey)))
                .Where(t => t.Parameter is not null)
                .Select(t => new MappingRequest(t.TestNumber, parameters[t.Parameter!], null, null,
                                                null, null, IsEnabled: true))
                .ToList();

            if (mappings.Count > 0)
            {
                Check(await AsAsync<InstrumentInterfaceService, Result>(DemoActor.Biologist,
                          s => s.SaveMappingsAsync(interfaceId.Value, mappings, ct), ct),
                      "Correspondances DEMO-INTEGRA");
            }
        }

        var hasTraffic = await QueryAsync(db => db.InstrumentMessageLog.IgnoreQueryFilters()
            .AnyAsync(m => m.InstrumentInterfaceId == interfaceId, ct));

        if (hasTraffic) return;

        // A tube of the demonstration whose order holds the glucose.
        var glucose = await ParameterIdsAsync(["GLY"], ct);
        var tube = glucose.TryGetValue("GLY", out var glyId)
            ? await QueryAsync(db => db.LabOrderItems.AsNoTracking()
                .Where(i => i.SampleId != null
                            && db.AnalysisTests.Any(a => a.Id == i.AnalysisTestId
                                                         && a.Parameters.Any(p => p.Id == glyId)))
                .Join(db.Samples.IgnoreQueryFilters(), i => i.SampleId, s => s.Id, (i, s) => s.Barcode)
                .FirstOrDefaultAsync(ct))
            : null;

        tube ??= "DEMO-TUBE-INT";

        await ActAsAsync(DemoActor.Administrator, ct);

        var now = clock.Now;
        var date = now.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);

        // Block 04 as the message log stores it: header, then lines 53, 55 and 00, CR between them.
        var received = new InstrumentMessageLog(interfaceId.Value, _siteId, MessageDirection.Inbound,
            "14 COBAS INTEGRA400 04\r" +
            $"53 {tube,-15} {date} SER\r" +
            "55  31\r" +
            "00 +9.400000E-01 g/L      0   0   0   0 +0.000000E+00 +0.000000E+00", now.AddMinutes(-15))
        {
            SpecimenIds = tube
        };

        await AsAsync<IInstrumentMessageLogStore, bool>(DemoActor.Administrator, async store =>
        {
            var id = await store.RecordAsync(received, ct);
            await store.MarkProcessedAsync(id, InstrumentMessageProcessingStatus.AwaitingAcceptance, 1, 0,
                                           "Interface_HeldForAcceptance", now.AddMinutes(-15), ct);
            return true;
        }, ct);
    }

    // ---- Subcontracting -------------------------------------------------------------------------

    /// <summary>Code of the demonstration partner laboratory.</summary>
    private const string DemoPartnerCode = DemoPrefix + "PART";

    /// <summary>Code of the demonstration analysis sent to it.</summary>
    private const string DemoSubcontractedCode = DemoPrefix + "VITD";

    /// <summary>
    /// A partner laboratory and an analysis the laboratory sends to it, so the send-out circuit —
    /// list, slip, partner reference, return — can be tried: the 25-OH vitamin D, which most
    /// Moroccan laboratories without a chemiluminescence platform for it send out. The starter
    /// catalogue has no analysis that is commonly sent out, so this one is added, demonstration-coded.
    /// <para>
    /// The partner is written directly, without the settings screen's permission check; the analysis, its
    /// parameter, interval and price go through the catalogue screens' service. Each part is looked
    /// for first, so an interrupted run is completed rather than repeated.
    /// </para>
    /// </summary>
    private async Task SeedSubcontractingAsync(CancellationToken ct)
    {
        var partnerId = await QueryAsync(db => db.Sites.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Code == DemoPartnerCode)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct));

        if (partnerId is null)
        {
            var partner = new LabSite(DemoPartnerCode,
                                      new MultilingualText("Laboratoire partenaire (démo)",
                                                           "المختبر الشريك (عرض)",
                                                           "Partner laboratory (demo)"),
                                      LabSiteKind.ReferralPartner)
            {
                LegalName = "LABORATOIRE PARTENAIRE DÉMO SARL",
                AddressLine = new MultilingualText("45, avenue Hassan II — Rabat"),
                City = "Rabat",
                Phone = "+212537000000",
                Email = "envois@partenaire-demo.example"
            };

            await WriteAsync(db =>
            {
                db.Sites.Add(partner);
                return Task.CompletedTask;
            }, ct);

            partnerId = partner.Id;
        }

        var analysis = await QueryAsync(db => db.AnalysisTests.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Code == DemoSubcontractedCode)
            .Select(a => new
            {
                a.Id,
                Parameter = a.Parameters.Select(p => (Guid?)p.Id).FirstOrDefault(),
                HasRange = a.Parameters.Any(p => p.ReferenceRanges.Any()),
                HasTariff = a.Tariffs.Any()
            })
            .FirstOrDefaultAsync(ct));

        var analysisId = analysis?.Id;

        if (analysisId is null)
        {
            var nomenclature = await QueryAsync(async db => new
            {
                Department = await db.Departments.AsNoTracking()
                    .Where(d => d.Code == Department.Codes.Biochemistry).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct),
                Specimen = await db.SpecimenTypes.AsNoTracking()
                    .Where(t => t.Code == SpecimenType.Codes.SerumDryTube).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct),
                Container = await db.ContainerTypes.AsNoTracking()
                    .Where(c => c.Code == "TUBE_GEL").Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct)
            });

            if (nomenclature.Department is not { } departmentId || nomenclature.Specimen is not { } specimenId)
            {
                _problems.Add("Sous-traitance : discipline de biochimie ou sérum introuvable.");
                return;
            }

            var created = await AsAsync<CatalogMaintenanceService, Result<Guid>>(
                DemoActor.Administrator, s => s.SaveAnalysisAsync(new AnalysisRequest
                {
                    Code = DemoSubcontractedCode,
                    Name = new MultilingualText("25-OH vitamine D (démo)", "فيتامين د 25-OH (عرض)",
                                                "25-OH vitamin D (demo)"),
                    DepartmentId = departmentId,
                    SpecimenTypeId = specimenId,
                    ContainerTypeId = nomenclature.Container,
                    Method = new MultilingualText("Immunoessai par chimiluminescence (laboratoire partenaire)"),
                    IsSubcontracted = true,
                    SubcontractorSiteId = partnerId,
                    // The partner's turnaround and the courier both ways.
                    RoutineTurnaroundHours = 96,
                    IsAvailableUrgently = false
                }, ct), ct);

            if (!Check(created, "Sous-traitance — analyse")) return;

            analysisId = created.Value;
        }

        var parameterId = analysis?.Parameter;

        if (parameterId is null)
        {
            var units = await QueryAsync(db => db.Units.AsNoTracking()
                .Where(u => u.Code == "NG_ML").Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct));

            var parameter = await AsAsync<CatalogMaintenanceService, Result<Guid>>(
                DemoActor.Administrator, s => s.SaveParameterAsync(new ParameterRequest
                {
                    AnalysisTestId = analysisId.Value,
                    Code = DemoSubcontractedCode,
                    Name = new MultilingualText("25-OH vitamine D", "فيتامين د 25-OH", "25-OH vitamin D"),
                    ValueType = ResultValueType.Numeric,
                    UnitId = units,
                    Decimals = 1
                }, ct), ct);

            if (!Check(parameter, "Sous-traitance — paramètre")) return;

            parameterId = parameter.Value;
        }

        if (analysis is not { HasRange: true })
        {
            Check(await AsAsync<CatalogMaintenanceService, Result<Guid>>(
                      DemoActor.Administrator, s => s.SaveRangeAsync(new ReferenceRangeRequest
                      {
                          TestParameterId = parameterId.Value,
                          LowerLimit = 30m,
                          UpperLimit = 100m,
                          Source = "Valeurs du laboratoire partenaire (démo)",
                          ValidFrom = new DateOnly(clock.Today.Year, 1, 1)
                      }, ct), ct),
                  "Sous-traitance — intervalle");
        }

        if (analysis is not { HasTariff: true })
        {
            Check(await AsAsync<CatalogMaintenanceService, Result<Guid>>(
                      DemoActor.Administrator, s => s.AddTariffAsync(new TariffRequest
                      {
                          AnalysisTestId = analysisId.Value,
                          ValidFrom = new DateOnly(clock.Today.Year, 1, 1),
                          LetterKey = "B",
                          Coefficient = 250m,
                          LetterKeyValue = ReferenceDataSeeder.PlaceholderLetterKeyValueMad,
                          Price = 250m * ReferenceDataSeeder.PlaceholderLetterKeyValueMad
                      }, ct), ct),
                  "Sous-traitance — tarif");
        }
    }
}
