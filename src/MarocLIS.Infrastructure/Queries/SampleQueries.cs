using MarocLIS.Application.Orders;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class SampleQueries(LisDbContext db) : ISampleQueries
{
    /// <summary>
    /// The collection worklist, ordered by priority then by age.
    /// <para>
    /// Grouped by tube rather than by analysis: the phlebotomist draws tubes, and the order of draw
    /// matters because additive carry-over between containers is a real pre-analytical error source.
    /// The fasting requirement shown is the strictest among the analyses on that tube — a bilan
    /// lipidique needing twelve hours governs the whole tube even if a glycaemia beside it needs
    /// eight.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<CollectionWorklistEntry>> GetCollectionWorklistAsync(
        Guid siteId, bool onlyUrgent, DateOnly today, CancellationToken ct = default)
    {
        var orders = db.LabOrders.AsNoTracking()
            .Where(o => o.SiteId == siteId
                        && o.Status != LabOrderStatus.Cancelled
                        && o.Status != LabOrderStatus.Reported);

        if (onlyUrgent) orders = orders.Where(o => o.Priority != OrderPriority.Routine);

        var rows = await db.Samples.AsNoTracking()
            // Drawn tubes stay on the list until they are sent or received: the collection point
            // still has to dispatch them, and a tube that vanished on "Prélever" left nothing for
            // "Expédier" to act on.
            .Where(s => s.SiteId == siteId
                        && (s.Status == SampleStatus.AwaitingCollection
                            || (s.Status == SampleStatus.Collected && s.DispatchedAt == null)))
            .Join(orders, s => s.LabOrderId, o => o.Id, (s, o) => new { Sample = s, Order = o })
            .Join(db.Patients.AsNoTracking(), x => x.Order.PatientId, p => p.Id,
                  (x, p) => new { x.Sample, x.Order, Patient = p })
            .Select(x => new
            {
                x.Sample.Id,
                x.Sample.Barcode,
                x.Sample.LabOrderId,
                x.Sample.SequenceInOrder,
                x.Sample.SpecimenTypeId,
                x.Sample.ContainerTypeId,
                x.Sample.CollectionLocation,
                x.Sample.CollectedAt,
                x.Order.AccessionNumber,
                x.Order.Priority,
                x.Order.RegisteredAt,
                x.Order.PatientFasted,
                x.Patient.LastName,
                x.Patient.FirstName,
                x.Patient.LastNameArabic,
                x.Patient.FirstNameArabic,
                x.Patient.Gender,
                x.Patient.DateOfBirth,
                x.Patient.DeclaredAgeYears,
                PatientId = x.Patient.Id,
                TotalSamplesInOrder = db.Samples.Count(t => t.LabOrderId == x.Sample.LabOrderId),
                // No null guard on the id: Sample.SpecimenTypeId is a non-nullable Guid assigned in
                // the constructor, so the guard that was here could never be true and the branch
                // below is the only one that ever ran. The sibling lookups do the same thing.
                SpecimenTypeName = db.SpecimenTypes.Where(t => t.Id == x.Sample.SpecimenTypeId)
                                                    .Select(t => t.Name).FirstOrDefault(),
                CollectionInstructions = db.SpecimenTypes
                    .Where(t => t.Id == x.Sample.SpecimenTypeId)
                    .Select(t => t.CollectionInstructions).FirstOrDefault(),
                SpecimenFasting = db.SpecimenTypes
                    .Where(t => t.Id == x.Sample.SpecimenTypeId)
                    .Select(t => (int?)t.FastingHours).FirstOrDefault(),
                Container = db.ContainerTypes
                    .Where(c => c.Id == x.Sample.ContainerTypeId)
                    .Select(c => new
                    {
                        c.Name,
                        c.CapColourHex,
                        c.DrawOrder,
                        c.NominalVolumeMl,
                        c.MinimumVolumeMl,
                        c.RequiresProtectionFromLight,
                        c.RequiresIceTransport
                    })
                    .FirstOrDefault()
            })
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.RegisteredAt)
            .Take(500)
            .ToListAsync(ct);

        var sampleIds = rows.Select(r => r.Id).ToList();
        var patientIds = rows.Select(r => r.PatientId).Distinct().ToList();

        // Analyses per tube, and the strictest fasting each tube implies.
        var itemsBySample = await db.LabOrderItems.AsNoTracking()
            .Where(i => i.SampleId != null && sampleIds.Contains(i.SampleId!.Value)
                        && i.Status != LabOrderItemStatus.Cancelled)
            .Join(db.AnalysisTests.AsNoTracking(), i => i.AnalysisTestId, a => a.Id,
                  (i, a) => new
                  {
                      SampleId = i.SampleId!.Value,
                      a.Code,
                      Fasting = a.FastingHoursOverride,
                      SpecimenFasting = a.SpecimenType!.FastingHours
                  })
            .ToListAsync(ct);

        var analysisLookup = itemsBySample
            .GroupBy(x => x.SampleId)
            .ToDictionary(
                g => g.Key,
                g => (Codes: (IReadOnlyList<string>)[.. g.Select(x => x.Code).Order()],
                      Fasting: g.Max(x => x.Fasting ?? x.SpecimenFasting)));

        // Active critical and warning alerts, so the phlebotomist sees them before the draw.
        var alerts = await db.PatientAlerts.AsNoTracking()
            .Where(a => patientIds.Contains(a.PatientId)
                        && a.Severity != Domain.Patients.PatientAlertSeverity.Information
                        && (a.ExpiresOn == null || a.ExpiresOn >= today))
            .Select(a => new { a.PatientId, a.Message })
            .ToListAsync(ct);

        var alertLookup = alerts
            .GroupBy(a => a.PatientId)
            .ToDictionary(g => g.Key,
                          g => (IReadOnlyList<Domain.Common.MultilingualText>)[.. g.Select(a => a.Message)]);

        return
        [
            .. rows.Select(r =>
            {
                var analysisInfo = analysisLookup.GetValueOrDefault(r.Id);

                return new CollectionWorklistEntry
                {
                    SampleId = r.Id,
                    LabOrderId = r.LabOrderId,
                    Barcode = r.Barcode,
                    AccessionNumber = r.AccessionNumber,
                    PatientName = $"{r.LastName} {r.FirstName}".Trim(),
                    PatientNameArabic =
                        string.IsNullOrWhiteSpace(r.LastNameArabic)
                        && string.IsNullOrWhiteSpace(r.FirstNameArabic)
                            ? null
                            : $"{r.LastNameArabic} {r.FirstNameArabic}".Trim(),
                    Gender = r.Gender,
                    DateOfBirth = r.DateOfBirth,
                    AgeYears = AgeInYears(r.DateOfBirth, today) ?? r.DeclaredAgeYears,
                    Priority = r.Priority,
                    RegisteredAt = r.RegisteredAt,
                    SequenceInOrder = r.SequenceInOrder,
                    TotalSamplesInOrder = r.TotalSamplesInOrder,
                    SpecimenTypeName = r.SpecimenTypeName ?? new Domain.Common.MultilingualText("-"),
                    ContainerName = r.Container?.Name,
                    ContainerColourHex = r.Container?.CapColourHex ?? "#FFFFFF",
                    DrawOrder = r.Container?.DrawOrder ?? 99,
                    NominalVolumeMl = r.Container?.NominalVolumeMl,
                    MinimumVolumeMl = r.Container?.MinimumVolumeMl,
                    RequiredFastingHours = analysisInfo.Fasting is > 0
                        ? analysisInfo.Fasting
                        : r.SpecimenFasting ?? 0,
                    PatientDeclaredFasted = r.PatientFasted,
                    CollectionInstructions = r.CollectionInstructions,
                    AnalysisCodes = analysisInfo.Codes ?? [],
                    PatientAlerts = alertLookup.GetValueOrDefault(r.PatientId, []),
                    RequiresLightProtection = r.Container?.RequiresProtectionFromLight ?? false,
                    RequiresIceTransport = r.Container?.RequiresIceTransport ?? false,
                    CollectionLocation = r.CollectionLocation,
                    CollectedAt = r.CollectedAt
                };
            })
            .OrderByDescending(e => e.Priority)
            .ThenBy(e => e.RegisteredAt)
            .ThenBy(e => e.DrawOrder)
        ];
    }

    public async Task<SampleReceptionCandidate?> GetReceptionCandidateAsync(
        string barcode, DateTimeOffset now, CancellationToken ct = default)
    {
        // Equality, not LIKE. A partial barcode match could bind one patient's tube to another
        // patient's order.
        var sampleId = await db.Samples.AsNoTracking()
            .Where(s => s.Barcode == barcode)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);

        if (sampleId is null) return null;

        var candidates = await BuildCandidatesAsync(
            db.Samples.AsNoTracking().Where(s => s.Id == sampleId), now, ct);

        return candidates.Count > 0 ? candidates[0] : null;
    }

    public async Task<IReadOnlyList<SampleReceptionCandidate>> GetAwaitingReceptionAsync(
        Guid siteId, DateTimeOffset now, CancellationToken ct = default) =>
        await BuildCandidatesAsync(
            db.Samples.AsNoTracking()
              .Where(s => s.SiteId == siteId
                          && (s.Status == SampleStatus.Collected
                              || s.Status == SampleStatus.InTransit)),
            now, ct);

    /// <summary>
    /// Projects samples into reception candidates, computing the stability verdict in one place.
    /// <para>
    /// The bench is told <em>whether</em> the stability window has been exceeded rather than being
    /// handed two timestamps and a table. Working that out per tube under pressure is exactly how a
    /// degraded sample gets accepted.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<SampleReceptionCandidate>> BuildCandidatesAsync(
        IQueryable<Sample> source, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await source
            .Join(db.LabOrders.AsNoTracking(), s => s.LabOrderId, o => o.Id,
                  (s, o) => new { Sample = s, Order = o })
            .Join(db.Patients.AsNoTracking(), x => x.Order.PatientId, p => p.Id,
                  (x, p) => new
                  {
                      x.Sample.Id,
                      x.Sample.Barcode,
                      x.Sample.LabOrderId,
                      x.Sample.Status,
                      x.Sample.CollectedAt,
                      x.Sample.DispatchedAt,
                      x.Sample.VolumeMl,
                      x.Sample.Defects,
                      x.Sample.SpecimenTypeId,
                      x.Sample.ContainerTypeId,
                      x.Order.AccessionNumber,
                      x.Order.Priority,
                      p.LastName,
                      p.FirstName,
                      p.Gender,
                      p.DateOfBirth,
                      SpecimenTypeName = db.SpecimenTypes
                          .Where(t => t.Id == x.Sample.SpecimenTypeId)
                          .Select(t => t.Name).FirstOrDefault(),
                      Container = db.ContainerTypes
                          .Where(c => c.Id == x.Sample.ContainerTypeId)
                          .Select(c => new
                          {
                              c.Name,
                              c.CapColourHex,
                              c.MinimumVolumeMl,
                              c.RequiresIceTransport
                          })
                          .FirstOrDefault(),
                      TotalSamplesInOrder = db.Samples.Count(t => t.LabOrderId == x.Sample.LabOrderId)
                  })
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.CollectedAt)
            .Take(500)
            .ToListAsync(ct);

        var sampleIds = rows.Select(r => r.Id).ToList();

        var stability = await db.LabOrderItems.AsNoTracking()
            .Where(i => i.SampleId != null && sampleIds.Contains(i.SampleId!.Value)
                        && i.Status != LabOrderItemStatus.Cancelled)
            .Join(db.AnalysisTests.AsNoTracking(), i => i.AnalysisTestId, a => a.Id,
                  (i, a) => new
                  {
                      SampleId = i.SampleId!.Value,
                      a.Code,
                      a.StabilityAtRoomTemperatureHours
                  })
            .ToListAsync(ct);

        var lookup = stability
            .GroupBy(x => x.SampleId)
            .ToDictionary(
                g => g.Key,
                g => (Codes: (IReadOnlyList<string>)[.. g.Select(x => x.Code).Order()],
                      // The shortest window governs: the tube is unusable for the least tolerant
                      // analysis on it well before it is unusable for the rest.
                      Shortest: g.Where(x => x.StabilityAtRoomTemperatureHours is > 0)
                                 .Select(x => x.StabilityAtRoomTemperatureHours)
                                 .DefaultIfEmpty(null)
                                 .Min()));

        return
        [
            .. rows.Select(r =>
            {
                var info = lookup.GetValueOrDefault(r.Id);
                var elapsed = r.CollectedAt is { } collected ? now - collected : (TimeSpan?)null;

                return new SampleReceptionCandidate
                {
                    SampleId = r.Id,
                    LabOrderId = r.LabOrderId,
                    Barcode = r.Barcode,
                    AccessionNumber = r.AccessionNumber,
                    PatientName = $"{r.LastName} {r.FirstName}".Trim(),
                    DateOfBirth = r.DateOfBirth,
                    Gender = r.Gender,
                    Priority = r.Priority,
                    Status = r.Status,
                    CollectedAt = r.CollectedAt,
                    DispatchedAt = r.DispatchedAt,
                    Elapsed = elapsed,
                    SpecimenTypeName = r.SpecimenTypeName ?? new Domain.Common.MultilingualText("-"),
                    ContainerName = r.Container?.Name,
                    ContainerColourHex = r.Container?.CapColourHex,
                    TotalSamplesInOrder = r.TotalSamplesInOrder,
                    VolumeMl = r.VolumeMl,
                    MinimumVolumeMl = r.Container?.MinimumVolumeMl,
                    ExistingDefects = r.Defects,
                    AnalysisCodes = info.Codes ?? [],
                    ShortestStabilityHours = info.Shortest,
                    StabilityExceeded = info.Shortest is { } hours && elapsed is { } age
                                        && age > TimeSpan.FromHours(hours),
                    RequiresColdChain = r.Container?.RequiresIceTransport ?? false
                };
            })
        ];
    }

    public async Task<IReadOnlyList<string>> GetStorageLocationsAsync(Guid siteId, CancellationToken ct = default)
    {
        var places = await db.Samples.AsNoTracking()
            .Where(s => s.SiteId == siteId && s.StorageLocation != null && s.StorageLocation != "")
            .Select(s => s.StorageLocation!)
            .Distinct()
            .OrderBy(l => l)
            .Take(100)
            .ToListAsync(ct);

        // Trailing spaces make two entries of one fridge in the database, not in the list.
        return [.. places.Select(p => p.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<IReadOnlyList<SampleLabel>> GetLabelsForOrderAsync(
        Guid labOrderId, DateTimeOffset now, CancellationToken ct = default)
    {
        var rows = await db.Samples.AsNoTracking()
            .Where(s => s.LabOrderId == labOrderId && s.Status != SampleStatus.Discarded)
            .Join(db.LabOrders.AsNoTracking(), s => s.LabOrderId, o => o.Id,
                  (s, o) => new { Sample = s, Order = o })
            .Join(db.Patients.AsNoTracking(), x => x.Order.PatientId, p => p.Id,
                  (x, p) => new
                  {
                      x.Sample.Id,
                      x.Sample.Barcode,
                      x.Sample.SequenceInOrder,
                      x.Sample.ContainerTypeId,
                      x.Order.AccessionNumber,
                      x.Order.Priority,
                      p.LastName,
                      p.FirstName,
                      p.LastNameArabic,
                      p.FirstNameArabic,
                      p.DateOfBirth,
                      ContainerName = db.ContainerTypes
                          .Where(c => c.Id == x.Sample.ContainerTypeId)
                          .Select(c => c.Name.Fr).FirstOrDefault(),
                      ContainerColour = db.ContainerTypes
                          .Where(c => c.Id == x.Sample.ContainerTypeId)
                          .Select(c => c.CapColourHex).FirstOrDefault()
                  })
            .OrderBy(x => x.SequenceInOrder)
            .ToListAsync(ct);

        if (rows.Count == 0) return [];

        var sampleIds = rows.Select(r => r.Id).ToList();

        var codes = await db.LabOrderItems.AsNoTracking()
            .Where(i => i.SampleId != null && sampleIds.Contains(i.SampleId!.Value)
                        && i.Status != LabOrderItemStatus.Cancelled)
            .Join(db.AnalysisTests.AsNoTracking(), i => i.AnalysisTestId, a => a.Id,
                  (i, a) => new { SampleId = i.SampleId!.Value, a.Code })
            .ToListAsync(ct);

        var codeLookup = codes
            .GroupBy(c => c.SampleId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(c => c.Code).Order()]);

        return
        [
            .. rows.Select(r => new SampleLabel(
                r.Id,
                r.Barcode,
                $"{r.LastName} {r.FirstName}".Trim(),
                string.IsNullOrWhiteSpace(r.LastNameArabic) && string.IsNullOrWhiteSpace(r.FirstNameArabic)
                    ? null
                    : $"{r.LastNameArabic} {r.FirstNameArabic}".Trim(),
                r.DateOfBirth,
                r.AccessionNumber,
                r.ContainerName ?? "-",
                r.ContainerColour ?? "#FFFFFF",
                r.SequenceInOrder,
                rows.Count,
                codeLookup.GetValueOrDefault(r.Id, []),
                r.Priority != OrderPriority.Routine,
                now))
        ];
    }

    public Task<decimal?> GetMinimumVolumeAsync(Guid sampleId, CancellationToken ct = default) =>
        db.Samples.AsNoTracking()
            .Where(s => s.Id == sampleId)
            .Join(db.ContainerTypes.AsNoTracking(), s => s.ContainerTypeId, c => c.Id,
                  (s, c) => c.MinimumVolumeMl)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The serum bank. Everything received and not yet discarded, whether shelved or not — an
    /// unshelved tube is precisely the one that needs finding, so excluding it would hide the
    /// problem the screen exists to surface.
    /// </summary>
    public async Task<IReadOnlyList<StoredSampleRow>> GetStoredSamplesAsync(
        Guid siteId, string? search, bool onlyExpired, DateOnly today,
        CancellationToken ct = default)
    {
        var query = db.Samples.AsNoTracking()
            .Where(s => s.SiteId == siteId
                        && s.Status != SampleStatus.Discarded
                        && s.ReceivedAt != null);

        if (onlyExpired) query = query.Where(s => s.KeepUntil != null && s.KeepUntil < today);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();

            query = query.Where(s => s.Barcode.Contains(term)
                                     || (s.StorageLocation != null
                                         && s.StorageLocation.Contains(term))
                                     || db.LabOrders.Any(o => o.Id == s.LabOrderId
                                                              && o.AccessionNumber.Contains(term)));
        }

        var rows = await query
            .OrderBy(s => s.KeepUntil == null)
            .ThenBy(s => s.KeepUntil)
            .ThenBy(s => s.StorageLocation)
            .Take(1000)
            .Select(s => new
            {
                s.Id,
                s.Barcode,
                s.Status,
                s.CollectedAt,
                s.ReceivedAt,
                s.StorageLocation,
                s.StoragePosition,
                s.KeepUntil,
                s.VolumeMl,
                s.Defects,
                s.IsAcceptedWithReservation,
                Accession = db.LabOrders.Where(o => o.Id == s.LabOrderId)
                              .Select(o => o.AccessionNumber).FirstOrDefault() ?? string.Empty,
                PatientName = db.LabOrders.Where(o => o.Id == s.LabOrderId)
                                .Select(o => o.Patient!.LastName + " " + o.Patient!.FirstName)
                                .FirstOrDefault() ?? string.Empty,
                Specimen = db.SpecimenTypes.Where(t => t.Id == s.SpecimenTypeId)
                             .Select(t => t.Name.Fr).FirstOrDefault() ?? string.Empty,
                AliquotCount = db.SampleAliquots.Count(a => a.ParentSampleId == s.Id)
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(s => new StoredSampleRow(
                s.Id, s.Barcode, s.Accession, s.PatientName, s.Specimen, s.Status,
                s.CollectedAt, s.ReceivedAt, s.StorageLocation, s.StoragePosition, s.KeepUntil,
                s.KeepUntil is { } keep ? keep.DayNumber - today.DayNumber : null,
                s.VolumeMl, s.AliquotCount,
                s.Defects != SampleDefect.None, s.IsAcceptedWithReservation))
        ];
    }

    public async Task<ContainerDescription?> GetContainerDescriptionAsync(
        Guid? containerTypeId, CancellationToken ct = default)
    {
        if (containerTypeId is not { } id) return null;

        return await db.ContainerTypes.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new ContainerDescription(c.Name.Fr, c.CapColourHex))
            .FirstOrDefaultAsync(ct);
    }

    private static int? AgeInYears(DateOnly? dateOfBirth, DateOnly on)
    {
        if (dateOfBirth is not { } dob) return null;
        var age = on.Year - dob.Year;
        if (on < dob.AddYears(age)) age--;
        return age < 0 ? null : age;
    }
}
