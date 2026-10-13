using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Samples;
using MarocLIS.Domain.Sequencing;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Persistence;

/// <summary>
/// Seeds a handful of patients and orders so a demonstration or a training installation opens on
/// screens that have something in them.
/// <para>
/// Deliberately <b>not</b> reference data, and never seeded by default: these are invented people.
/// A laboratory that ran this against its production database would have fictitious patients in a
/// medical record it is legally obliged to keep for ten years, and no way to tell them from real
/// ones afterwards — so the caller has to ask for it explicitly, every patient's record number
/// carries the <c>DEMO-</c> prefix, and every order carries <see cref="DemoMarker"/> in its internal
/// note. The marker goes on the order's note and not on the patient's clinical fields: a note is
/// shown on worklists and never printed, whereas a marker written into a current-treatment field
/// would appear on a report as medication the patient is taking.
/// </para>
/// <para>
/// The orders are left at different stages on purpose — one awaiting collection, one received, one
/// with results entered, one fully validated — because a demonstration of a laboratory system that
/// only shows empty worklists demonstrates nothing, and the four stages are what the four screens
/// are for.
/// </para>
/// </summary>
public static class DemoDataSeeder
{
    /// <summary>
    /// Written into every demonstration order's internal note. Visible on the worklists and on the
    /// order file, so nobody has to query the database to tell a demonstration record from a real
    /// one — and never printed on a report.
    /// </summary>
    public const string DemoMarker = "JEU DE DÉMONSTRATION — NE PAS UTILISER EN PRODUCTION";

    public static async Task SeedAsync(LisDbContext context, IClock clock,
                                        CancellationToken ct = default)
    {
        // Idempotent on the patient table: the seeder runs on every start when the flag is set, and
        // a second pass must not create a second Fatima Zahra Benani.
        if (await context.Patients.IgnoreQueryFilters().AnyAsync(ct)) return;

        var site = await context.Sites.FirstOrDefaultAsync(ct);
        if (site is null) return;

        var user = await context.Users.IgnoreQueryFilters().FirstOrDefaultAsync(ct);
        if (user is null) return;

        var analyses = await context.AnalysisTests
            .Include(a => a.Parameters)
            .Include(a => a.Tariffs)
            .ToListAsync(ct);

        if (analyses.Count == 0) return;

        var payers = await context.Payers.ToDictionaryAsync(p => p.Code, ct);
        var patients = BuildPatients(site.Id, clock.Today, payers);

        context.Patients.AddRange(patients);
        await context.SaveChangesAsync(ct);

        // Four orders, one per patient, each taken to a different stage.
        var stages = new[]
        {
            OrderStage.AwaitingCollection,
            OrderStage.Received,
            OrderStage.ResultsEntered,
            OrderStage.Validated
        };

        for (var i = 0; i < patients.Count && i < stages.Length; i++)
        {
            await SeedOrderAsync(context, patients[i], stages[i], analyses, site.Id, site.Code,
                                  user.Id, clock, ct);
        }

        await context.SaveChangesAsync(ct);
    }

    private enum OrderStage
    {
        AwaitingCollection,
        Received,
        ResultsEntered,
        Validated
    }

    // ---- Patients ------------------------------------------------------------------------------

    /// <summary>
    /// Four patients chosen to exercise the cases that behave differently: a covered adult, a
    /// self-paying adult, a child (whose reference intervals differ and who needs a guardian), and
    /// a pregnant patient (whose intervals differ again).
    /// </summary>
    private static List<Patient> BuildPatients(Guid siteId, DateOnly today,
                                                Dictionary<string, Payer> payers)
    {
        var patients = new List<Patient>(4);

        var benani = new Patient("DEMO-000001", "Benani", "Fatima Zahra",
                                  new DateOnly(1979, 4, 12), Gender.Female, siteId)
        {
            LastNameArabic = "بناني",
            FirstNameArabic = "فاطمة الزهراء",
            SearchKey = "BENANI FATIMA ZAHRA",
            Cin = "BE123456",
            City = "Casablanca",
            AddressLine = "12, rue Ibn Batouta, Maârif",
            MobilePhone = "+212600000001",
            PreferredLanguage = AppLanguage.French
        };

        if (payers.TryGetValue("CNSS", out var cnss))
        {
            benani.AddCoverage(new PatientCoverage(benani.Id, cnss.Id, "CN-8841203",
                                                    today.AddYears(-3))
            {
                IsPrimary = true,
                DirectBillingEnabled = true
            });
        }

        patients.Add(benani);

        patients.Add(new Patient("DEMO-000002", "El Amrani", "Youssef",
                                  new DateOnly(1992, 11, 3), Gender.Male, siteId)
        {
            LastNameArabic = "العمراني",
            FirstNameArabic = "يوسف",
            SearchKey = "EL AMRANI YOUSSEF",
            Cin = "EA987654",
            City = "Rabat",
            AddressLine = "45, avenue Hassan II, Agdal",
            MobilePhone = "+212600000002",
            PreferredLanguage = AppLanguage.French
        });

        // A minor: the registration rules require a guardian, and the paediatric reference
        // intervals are the ones a demonstration most often needs to show.
        patients.Add(new Patient("DEMO-000003", "Ouazzani", "Salma",
                                  today.AddYears(-6), Gender.Female, siteId)
        {
            LastNameArabic = "الوزاني",
            FirstNameArabic = "سلمى",
            SearchKey = "OUAZZANI SALMA",
            City = "Fès",
            AddressLine = "8, derb Bennis, Médina",
            GuardianFullName = "Ouazzani Karim",
            GuardianCin = "OU445566",
            GuardianRelationship = "Père",
            MobilePhone = "+212600000003",
            PreferredLanguage = AppLanguage.Arabic
        });

        var tazi = new Patient("DEMO-000004", "Tazi", "Imane",
                                new DateOnly(1996, 2, 19), Gender.Female, siteId)
        {
            LastNameArabic = "التازي",
            FirstNameArabic = "إيمان",
            SearchKey = "TAZI IMANE",
            Cin = "TA112233",
            City = "Marrakech",
            AddressLine = "23, rue Moulay Ismaïl, Guéliz",
            MobilePhone = "+212600000004",
            IsPregnant = true,
            LastMenstrualPeriod = today.AddDays(-126),
            PreferredLanguage = AppLanguage.French
        };

        if (payers.TryGetValue("CNOPS", out var cnops))
        {
            tazi.AddCoverage(new PatientCoverage(tazi.Id, cnops.Id, "CO-5527741",
                                                  today.AddYears(-1))
            {
                IsPrimary = true,
                DirectBillingEnabled = true
            });
        }

        patients.Add(tazi);

        return patients;
    }

    // ---- Orders --------------------------------------------------------------------------------

    private static async Task SeedOrderAsync(LisDbContext context, Patient patient,
                                              OrderStage stage, List<AnalysisTest> analyses,
                                              Guid siteId, string siteCode, Guid userId,
                                              IClock clock, CancellationToken ct)
    {
        var now = clock.Now;
        var today = clock.Today;

        // Numbers come from the real sequences, not from a literal. A demonstration order carrying
        // a number the sequence has not issued would collide with the first real registration —
        // and in a gapless series that tax law requires unbroken, a collision is not recoverable.
        var accession = await AllocateAsync(context, SequencePurpose.Accession, siteId, today.Year,
                                             siteCode, now, ct);

        if (accession is null) return;

        var order = new LabOrder(accession, patient.Id, siteId, now.AddHours(-StageAgeHours(stage)),
                                  userId)
        {
            Priority = stage == OrderStage.Validated ? OrderPriority.Urgent : OrderPriority.Routine,
            Origin = OrderOrigin.Walkin,
            PatientFasted = true,
            DeclaredFastingHours = 12,
            ClinicalInformation = ClinicalContext(stage),
            ReportLanguage = patient.PreferredLanguage,
            InternalNote = DemoMarker,
            PayerId = patient.Coverages.FirstOrDefault()?.PayerId,
            PatientCoverageId = patient.Coverages.FirstOrDefault()?.Id
        };

        // Two or three analyses per order, from whatever the starter catalogue actually holds: the
        // seeder must not assume a code that a laboratory has since deactivated.
        var chosen = analyses
            .Where(a => a.IsActive && a.Parameters.Count > 0)
            .OrderBy(a => a.Code)
            .Skip(stage == OrderStage.Validated ? 1 : 0)
            .Take(stage == OrderStage.AwaitingCollection ? 2 : 3)
            .ToList();

        if (chosen.Count == 0) return;

        foreach (var analysis in chosen)
        {
            var tariff = analysis.TariffFor(order.PayerId, today);

            var item = new LabOrderItem(analysis.Id, order.Priority)
            {
                DepartmentId = analysis.DepartmentId,
                ChargedPrice = tariff?.Price ?? 0m,
                ReferenceTariffBase = (tariff?.Coefficient ?? 0m) * (tariff?.LetterKeyValue ?? 0m),
                BilledLetterKey = tariff?.LetterKey,
                BilledCoefficient = tariff?.Coefficient,
                BilledActCode = analysis.NationalActCode,
                PatientShare = tariff?.Price ?? 0m
            };

            order.AddItem(item);
        }

        order.DueAt = chosen.Select(a => a.ExpectedCompletion(now, order.IsUrgent)).Max();

        context.LabOrders.Add(order);

        // One tube per distinct container, exactly as registration does it.
        var groups = chosen.GroupBy(a => (a.SpecimenTypeId, a.ContainerTypeId)).ToList();
        var sequence = 1;

        foreach (var group in groups)
        {
            var barcode = await AllocateAsync(context, SequencePurpose.SampleBarcode, null, null,
                                               siteCode, now, ct);

            if (barcode is null) continue;

            var sample = new Sample(barcode, order.Id, group.Key.SpecimenTypeId, siteId)
            {
                ContainerTypeId = group.Key.ContainerTypeId,
                SequenceInOrder = sequence++,
                CollectionLocation = CollectionLocation.Laboratory,
                KeepUntil = today.AddDays(7)
            };

            sample.AddEvent(SampleEventKind.LabelPrinted, now, userId, $"Dossier {accession}");

            if (stage != OrderStage.AwaitingCollection)
            {
                sample.Collect(now.AddHours(-StageAgeHours(stage) + 1), userId, 5m, null);
                sample.Receive(now.AddHours(-StageAgeHours(stage) + 2), userId, SampleDefect.None,
                                acceptWithReservation: false, temperatureCelsius: null, note: null);
            }

            foreach (var analysis in group)
            {
                var item = order.Items.FirstOrDefault(i => i.AnalysisTestId == analysis.Id);
                if (item is not null) item.SampleId = sample.Id;
            }

            context.Samples.Add(sample);
        }

        // The awaited result rows, then values for the stages that have them.
        foreach (var item in order.Items)
        {
            var analysis = chosen.First(a => a.Id == item.AnalysisTestId);

            if (stage is OrderStage.Received or OrderStage.ResultsEntered or OrderStage.Validated)
                item.MarkCollected(now.AddHours(-StageAgeHours(stage) + 1));

            if (stage is OrderStage.Received or OrderStage.ResultsEntered or OrderStage.Validated)
                item.MarkReceived(now.AddHours(-StageAgeHours(stage) + 2), item.DueAt);

            foreach (var parameter in analysis.Parameters.Where(p => p.IsActive))
            {
                if (parameter.ValueType == ResultValueType.Heading) continue;

                var result = new Domain.Results.TestResult(order.Id, item.Id, parameter.Id, siteId)
                {
                    DepartmentId = analysis.DepartmentId,
                    AppliedDecimals = parameter.Decimals
                };

                if (stage is OrderStage.ResultsEntered or OrderStage.Validated)
                    EnterPlausibleValue(result, parameter, now.AddHours(-1), userId);

                context.TestResults.Add(result);
            }

            if (stage is OrderStage.ResultsEntered or OrderStage.Validated)
                item.MarkResultEntered(now.AddHours(-1));

            // Validated is left to the validation screen except on the last order, which is taken
            // all the way so the report screen and the billing list are not empty either.
            if (stage == OrderStage.Validated)
            {
                item.MarkTechnicallyValidated(now.AddMinutes(-30), userId);
                item.MarkBiologicallyValidated(now.AddMinutes(-15), userId);
            }
        }

        order.RecomputeStatus();
    }

    /// <summary>Hours ago the order was registered, so the four sit at plausible distances.</summary>
    private static int StageAgeHours(OrderStage stage) => stage switch
    {
        OrderStage.AwaitingCollection => 1,
        OrderStage.Received => 4,
        OrderStage.ResultsEntered => 8,
        _ => 26
    };

    private static string ClinicalContext(OrderStage stage) => stage switch
    {
        OrderStage.AwaitingCollection => "Bilan de contrôle annuel.",
        OrderStage.Received => "Asthénie depuis trois semaines.",
        OrderStage.ResultsEntered => "Suivi de diabète de type 2.",
        _ => "Bilan préopératoire, intervention programmée."
    };

    /// <summary>
    /// Puts a value in the middle of whatever the parameter declares, so demonstration results read
    /// as normal.
    /// <para>
    /// Deliberately never abnormal and never critical. A critical value raises the
    /// critical-result procedure, which obliges somebody to telephone a prescriber and log the
    /// call — a demonstration database that manufactures those hands the laboratory a queue of
    /// notifications for patients who do not exist.
    /// </para>
    /// </summary>
    private static void EnterPlausibleValue(Domain.Results.TestResult result,
                                             TestParameter parameter,
                                             DateTimeOffset at, Guid userId)
    {
        switch (parameter.ValueType)
        {
            case ResultValueType.Numeric or ResultValueType.NumericWithOperator:
            {
                var range = parameter.ReferenceRanges.FirstOrDefault(
                    r => r.IsActive && (r.LowerLimit is not null || r.UpperLimit is not null));

                var value = (range?.LowerLimit, range?.UpperLimit) switch
                {
                    ({ } low, { } high) => low + (high - low) / 2m,
                    (null, { } high) => high * 0.6m,
                    ({ } low, null) => low * 1.2m,
                    _ => MidMeasuringRange(parameter)
                };

                result.EnterNumeric(parameter, value, at, userId);
                break;
            }

            case ResultValueType.Coded or ResultValueType.SemiQuantitative:
            {
                var expected = parameter.CodedOptions.FirstOrDefault(o => o.IsExpectedNormal)
                               ?? parameter.CodedOptions.FirstOrDefault(o => !o.IsCritical);

                if (expected is not null) result.EnterCoded(parameter, expected.Code, at, userId);
                break;
            }

            case ResultValueType.Text:
                result.EnterText("Aspect normal.", at, userId);
                break;
        }
    }

    private static decimal MidMeasuringRange(TestParameter parameter) =>
        (parameter.MeasuringRangeLow, parameter.MeasuringRangeHigh) switch
        {
            ({ } low, { } high) => low + (high - low) / 2m,
            (null, { } high) => high / 2m,
            ({ } low, null) => low + 1m,
            _ => 1m
        };

    // ---- Numbering -----------------------------------------------------------------------------

    /// <summary>
    /// Takes a number from the real sequence. Returns null when the sequence is absent or closed,
    /// in which case the record is skipped rather than given an invented number.
    /// </summary>
    private static async Task<string?> AllocateAsync(LisDbContext context, SequencePurpose purpose,
                                                      Guid? siteId, int? year, string siteCode,
                                                      DateTimeOffset now, CancellationToken ct)
    {
        // Most specific first. A per-site, per-year series and an installation-wide one can both
        // exist for the same purpose, and taking the wrong one would number the demonstration
        // order out of the series the laboratory actually uses.
        var sequence = await context.NumberSequences
            .Where(s => s.Purpose == purpose
                        && (s.SiteId == siteId || s.SiteId == null)
                        && (s.Year == year || s.Year == null))
            .OrderByDescending(s => s.SiteId == siteId ? 1 : 0)
            .ThenByDescending(s => s.Year == year ? 1 : 0)
            .FirstOrDefaultAsync(ct);

        if (sequence is null) return null;

        var allocated = sequence.Allocate(now, siteCode);

        return allocated.IsSuccess ? allocated.Value : null;
    }
}
