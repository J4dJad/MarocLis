using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Migration;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Migration;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Sequencing;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Reprise;

/// <summary>
/// The database side of « Reprise de données ». Reads what the planner compares with, across every
/// site (a patient registered at a collection centre is still the same person), and writes the
/// planned rows chunk by chunk, each chunk in its own transaction with its source keys, so a run
/// interrupted between two chunks is resumed, not duplicated, by running it again.
/// </summary>
public sealed class DataMigrationStore(
    LisDbContext db,
    ISequenceAllocator sequences,
    ICurrentUser currentUser,
    IClock clock) : IDataMigrationStore
{
    // ---- Context ----------------------------------------------------------------------------------

    public Task<MigrationContext> LoadContextAsync(DataMigrationKind kind, string sourceSystem, CancellationToken ct = default) =>
        db.RunExclusiveAsync(() => LoadAsync(kind, sourceSystem, ct), ct);

    private async Task<MigrationContext> LoadAsync(DataMigrationKind kind, string sourceSystem, CancellationToken ct)
    {
        var prefix = sourceSystem.Trim().ToUpperInvariant() + "|";
        var today = clock.Today;

        var records = await db.MigrationRecords.AsNoTracking()
            .Where(r => r.Kind == kind && r.SourceKey.StartsWith(prefix))
            .Select(r => new { r.SourceKey, r.EntityId })
            .ToListAsync(ct);
        var recordMap = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records) recordMap[r.SourceKey] = r.EntityId;

        var needsPatients = kind is DataMigrationKind.Patients or DataMigrationKind.History or DataMigrationKind.OpeningBalances;

        IReadOnlyList<PatientSnapshot> patients = [];
        var patientRecords = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var oldNumbers = new Dictionary<string, Guid>(StringComparer.Ordinal);

        if (needsPatients)
        {
            patients = await LoadPatientsAsync(ct);

            var links = await db.MigrationRecords.AsNoTracking()
                .Where(r => r.Kind == DataMigrationKind.Patients)
                .Select(r => new { r.SourceKey, r.EntityId, r.OldNumber })
                .ToListAsync(ct);

            foreach (var link in links)
            {
                if (link.SourceKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) patientRecords[link.SourceKey] = link.EntityId;
                if (link.OldNumber is { Length: > 0 } number) oldNumbers.TryAdd(MigrationSchema.Normalize(number), link.EntityId);
            }
        }

        IReadOnlyList<PrescriberSnapshot> prescribers = [];
        if (kind == DataMigrationKind.Prescribers)
        {
            prescribers = await db.Prescribers.IgnoreQueryFilters().AsNoTracking()
                .Where(p => !p.IsDeleted)
                .Select(p => new PrescriberSnapshot(p.Id, p.LastName, p.FirstName, p.Inpe, p.ProfessionalOrderNumber,
                                                    p.Phone, p.MobilePhone, p.Email, p.AddressLine, p.City,
                                                    p.InstitutionName, p.Speciality == null ? null : p.Speciality.Fr))
                .ToListAsync(ct);
        }

        IReadOnlyList<AnalysisSnapshot> analyses = [];
        var acts = new HashSet<string>(StringComparer.Ordinal);
        decimal? letterKeyValue = null;
        string? defaultDepartment = null, defaultSpecimen = null;

        if (kind == DataMigrationKind.Catalogue)
        {
            var rows = await db.AnalysisTests.AsNoTracking()
                .Select(a => new
                {
                    a.Id, a.Code, Name = a.Name.Fr, a.NationalActCode,
                    Tariff = a.Tariffs.Where(t => t.PayerId == null && t.ValidFrom <= today && (t.ValidUntil == null || t.ValidUntil >= today))
                                      .OrderByDescending(t => t.ValidFrom)
                                      .Select(t => new { t.Price, t.Coefficient, t.LetterKey })
                                      .FirstOrDefault()
                })
                .ToListAsync(ct);
            analyses = [.. rows.Select(a => new AnalysisSnapshot(a.Id, a.Code, a.Name, a.NationalActCode,
                                                                 a.Tariff?.Price, a.Tariff?.Coefficient, a.Tariff?.LetterKey))];

            foreach (var code in await db.NationalBiologyActs.AsNoTracking().Select(a => a.Code).ToListAsync(ct))
                acts.Add(NationalBiologyAct.Normalize(code)!);

            letterKeyValue = await db.Tariffs.AsNoTracking()
                .Where(t => t.LetterKey == NationalBiologyAct.BiologyLetterKey && t.LetterKeyValue > 0m)
                .OrderByDescending(t => t.ValidFrom)
                .Select(t => (decimal?)t.LetterKeyValue)
                .FirstOrDefaultAsync(ct);

            // An analysis created without a section or specimen goes where most of the catalogue is.
            defaultDepartment = await db.Departments.AsNoTracking().Where(d => d.IsActive)
                .OrderByDescending(d => db.AnalysisTests.Count(a => a.DepartmentId == d.Id)).ThenBy(d => d.Code)
                .Select(d => d.Code).FirstOrDefaultAsync(ct);
            defaultSpecimen = await db.SpecimenTypes.AsNoTracking().Where(s => s.IsActive)
                .OrderByDescending(s => db.AnalysisTests.Count(a => a.SpecimenTypeId == s.Id)).ThenBy(s => s.Code)
                .Select(s => s.Code).FirstOrDefaultAsync(ct);
        }

        var history = new Dictionary<Guid, HistorySnapshot>();
        if (kind == DataMigrationKind.History && recordMap.Count > 0)
        {
            var ids = recordMap.Values.ToHashSet();
            foreach (var chunk in ids.Chunk(2000))
            {
                var part = await db.HistoricalResults.IgnoreQueryFilters().AsNoTracking()
                    .Where(h => chunk.Contains(h.Id))
                    .Select(h => new HistorySnapshot(h.Id, h.ResultDate, h.ValueText, h.Unit, h.ReferenceRange, h.OldAccessionNumber))
                    .ToListAsync(ct);
                foreach (var h in part) history[h.Id] = h;
            }
        }

        var balances = new Dictionary<Guid, BalanceSnapshot>();
        if (kind == DataMigrationKind.OpeningBalances && recordMap.Count > 0)
        {
            var part = await db.OpeningBalances.IgnoreQueryFilters().AsNoTracking()
                .Select(b => new BalanceSnapshot(b.Id, b.Amount, b.AsOf))
                .ToListAsync(ct);
            foreach (var b in part) balances[b.Id] = b;
        }

        var payers = await db.Payers.AsNoTracking()
            .Where(p => p.IsActive)
            .Select(p => new { p.Id, p.Code, Name = p.Name.Fr, p.Kind })
            .ToListAsync(ct);

        var targets = new Dictionary<MigrationCodeList, IReadOnlyList<CodeTarget>>
        {
            [MigrationCodeList.Sex] = [new("M", "M"), new("F", "F"), new("U", "U")],
            [MigrationCodeList.Specialty] = [.. Enum.GetNames<PrescriberKind>().Select(n => new CodeTarget(n, n))],
            [MigrationCodeList.Payer] = [.. payers.OrderBy(p => p.Code).Select(p => new CodeTarget(p.Code, $"{p.Code} — {p.Name}", [p.Name]))]
        };

        if (kind is DataMigrationKind.History or DataMigrationKind.Catalogue)
        {
            targets[MigrationCodeList.Unit] = [.. (await db.Units.AsNoTracking().Select(u => new { u.Code, u.DisplaySymbol }).ToListAsync(ct))
                .OrderBy(u => u.DisplaySymbol).Select(u => new CodeTarget(u.Code, u.DisplaySymbol, [u.Code]))];
        }

        if (kind == DataMigrationKind.History)
        {
            var parameters = await db.TestParameters.AsNoTracking()
                .Where(p => p.IsActive)
                .Select(p => new
                {
                    AnalysisCode = p.AnalysisTest!.Code, AnalysisName = p.AnalysisTest.Name.Fr, Act = p.AnalysisTest.NationalActCode,
                    p.Code, Name = p.Name.Fr,
                    Siblings = p.AnalysisTest.Parameters.Count()
                })
                .ToListAsync(ct);

            targets[MigrationCodeList.Analysis] = [.. parameters
                .OrderBy(p => p.AnalysisCode).ThenBy(p => p.Code)
                .Select(p => new CodeTarget(
                    $"{p.AnalysisCode}/{p.Code}",
                    p.Siblings == 1 ? $"{p.AnalysisCode} — {p.Name}" : $"{p.AnalysisCode}/{p.Code} — {p.AnalysisName} : {p.Name}",
                    p.Siblings == 1
                        ? [p.Code, p.Name, p.AnalysisName, .. (p.Act is null ? Array.Empty<string>() : [p.Act])]
                        : [p.Code, p.Name]))];
        }

        if (kind == DataMigrationKind.Catalogue)
        {
            targets[MigrationCodeList.Department] = [.. (await db.Departments.AsNoTracking().Where(d => d.IsActive)
                .Select(d => new { d.Code, Name = d.Name.Fr }).ToListAsync(ct))
                .OrderBy(d => d.Code).Select(d => new CodeTarget(d.Code, $"{d.Code} — {d.Name}", [d.Name]))];
            targets[MigrationCodeList.Specimen] = [.. (await db.SpecimenTypes.AsNoTracking().Where(s => s.IsActive)
                .Select(s => new { s.Code, Name = s.Name.Fr }).ToListAsync(ct))
                .OrderBy(s => s.Code).Select(s => new CodeTarget(s.Code, $"{s.Code} — {s.Name}", [s.Name]))];
        }

        return new MigrationContext
        {
            Records = recordMap,
            PatientRecords = patientRecords,
            OldNumbers = oldNumbers,
            Patients = patients,
            Prescribers = prescribers,
            Analyses = analyses,
            History = history,
            Balances = balances,
            NationalActs = acts,
            LetterKeyValue = letterKeyValue,
            PayerIds = payers.ToDictionary(p => p.Code, p => p.Id, StringComparer.OrdinalIgnoreCase),
            Targets = targets,
            DefaultDepartmentCode = defaultDepartment,
            DefaultSpecimenCode = defaultSpecimen,
            Today = today
        };
    }

    private async Task<IReadOnlyList<PatientSnapshot>> LoadPatientsAsync(CancellationToken ct)
    {
        // Every site, merged files excluded: a match must land on the surviving file.
        var patients = await db.Patients.IgnoreQueryFilters().AsNoTracking()
            .Where(p => !p.IsDeleted && p.MergedIntoPatientId == null)
            .Select(p => new
            {
                p.Id, p.MedicalRecordNumber, p.LastName, p.FirstName, p.DateOfBirth, p.Gender, p.Cin, p.MobilePhone,
                p.AlternatePhone, p.Email, p.AddressLine, p.City, p.LastNameArabic, p.FirstNameArabic, p.GuardianFullName
            })
            .ToListAsync(ct);

        var coverages = (await db.PatientCoverages.AsNoTracking()
                .Select(c => new { c.PatientId, c.PayerId, c.MembershipNumber })
                .ToListAsync(ct))
            .ToLookup(c => c.PatientId);

        return [.. patients.Select(p => new PatientSnapshot(
            p.Id, p.MedicalRecordNumber, p.LastName, p.FirstName, p.DateOfBirth, p.Gender, p.Cin, p.MobilePhone,
            p.AlternatePhone, p.Email, p.AddressLine, p.City, p.LastNameArabic, p.FirstNameArabic, p.GuardianFullName,
            [.. coverages[p.Id].Select(c => (c.PayerId, c.MembershipNumber))]))];
    }

    // ---- Journal ----------------------------------------------------------------------------------

    public Task<Guid> StartBatchAsync(DataMigrationKind kind, string sourceSystem, string sourceLabel, int rowCount,
                                      CancellationToken ct = default) =>
        db.RunExclusiveAsync(async () =>
        {
            var batch = new MigrationBatch(kind, sourceSystem.Trim().ToUpperInvariant(), sourceLabel, clock.Now,
                                           currentUser.IsAuthenticated ? currentUser.UserId : null,
                                           currentUser.IsAuthenticated ? currentUser.UserName : null)
            {
                RowCount = rowCount
            };

            db.MigrationBatches.Add(batch);
            AuditSaveChangesInterceptor.PendingReason =
                $"Reprise de données : début de l'import {Describe(kind)} depuis {Short(sourceLabel)} " + PluralText.French("({0} {0:plural:ligne|lignes})", rowCount);
            try { await db.SaveChangesAsync(ct); }
            finally { AuditSaveChangesInterceptor.PendingReason = null; }

            db.ChangeTracker.Clear();
            return batch.Id;
        }, ct);

    public Task CompleteBatchAsync(Guid batchId, int created, int updated, int skipped, int failed, bool interrupted,
                                   CancellationToken ct = default) =>
        db.RunExclusiveAsync(async () =>
        {
            db.ChangeTracker.Clear();
            var batch = await db.MigrationBatches.FirstAsync(b => b.Id == batchId, ct);
            batch.Created = created;
            batch.Updated = updated;
            batch.Skipped = skipped;
            batch.Failed = failed;
            batch.Complete(clock.Now, interrupted);

            // The audit line of the run: who (the interceptor stamps the operator), when, from where,
            // and the counts.
            AuditSaveChangesInterceptor.PendingReason =
                $"Reprise de données {Describe(batch.Kind)} depuis {Short(batch.SourceLabel)} ({batch.SourceSystem}) : " +
                PluralText.French("{0} {0:plural:créé|créés}, {1} mis à jour, {2} {2:plural:ignoré|ignorés}, {3} en erreur", created, updated, skipped, failed) +
                (interrupted ? " — interrompue" : string.Empty);
            try { await db.SaveChangesAsync(ct); }
            finally { AuditSaveChangesInterceptor.PendingReason = null; }

            db.ChangeTracker.Clear();
            return 0;
        }, ct);

    public Task<IReadOnlyList<MigrationBatchRow>> GetBatchesAsync(CancellationToken ct = default) =>
        db.RunExclusiveAsync<IReadOnlyList<MigrationBatchRow>>(async () =>
            await db.MigrationBatches.AsNoTracking()
                .OrderByDescending(b => b.StartedAt)
                .Take(500)
                .Select(b => new MigrationBatchRow(b.Id, b.Kind, b.SourceSystem, b.SourceLabel, b.StartedAt, b.CompletedAt,
                                                   b.UserName, b.RowCount, b.Created, b.Updated, b.Skipped, b.Failed,
                                                   b.IsInterrupted, b.IsUndone, b.UndoneAt))
                .ToListAsync(ct), ct);

    private static string Describe(DataMigrationKind kind) => kind switch
    {
        DataMigrationKind.Patients => "des patients",
        DataMigrationKind.Prescribers => "des prescripteurs",
        DataMigrationKind.Catalogue => "du catalogue et des tarifs",
        DataMigrationKind.History => "des antériorités",
        DataMigrationKind.OpeningBalances => "des créances ouvertes",
        _ => kind.ToString()
    };

    private static string Short(string label) => label.Length > 300 ? label[..300] + "…" : label;

    // ---- Writing ----------------------------------------------------------------------------------

    public Task<(int Created, int Updated)> WriteChunkAsync(Guid batchId, DataMigrationKind kind,
                                                            IReadOnlyList<PlannedRow> rows, CancellationToken ct = default) =>
        db.RunExclusiveAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            AuditSaveChangesInterceptor.PendingReason = $"Reprise de données {Describe(kind)} (lot {batchId:N})";

            try
            {
                int created = 0, updated = 0;
                var records = await LoadRecordsAsync(kind, rows, ct);

                foreach (var row in rows)
                {
                    var record = records.GetValueOrDefault(row.SourceKey);
                    var wasCreate = await ApplyAsync(batchId, kind, row, record, ct);
                    if (wasCreate) created++; else updated++;
                }

                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return (created, updated);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
            finally
            {
                AuditSaveChangesInterceptor.PendingReason = null;
                db.ChangeTracker.Clear();
            }
        }, ct);

    private async Task<Dictionary<string, MigrationRecord>> LoadRecordsAsync(DataMigrationKind kind, IReadOnlyList<PlannedRow> rows,
                                                                            CancellationToken ct)
    {
        var keys = rows.Select(r => r.SourceKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var found = await db.MigrationRecords.Where(r => r.Kind == kind && keys.Contains(r.SourceKey)).ToListAsync(ct);
        return found.ToDictionary(r => r.SourceKey, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Applies one row; true when it created the record, false when it updated or linked one.</summary>
    private async Task<bool> ApplyAsync(Guid batchId, DataMigrationKind kind, PlannedRow row, MigrationRecord? record,
                                        CancellationToken ct)
    {
        var existingId = record?.EntityId ?? row.ExistingId;
        Guid entityId;
        bool created;

        switch (kind)
        {
            case DataMigrationKind.Patients:
                (entityId, created) = await ApplyPatientAsync(row, existingId, ct);
                break;
            case DataMigrationKind.Prescribers:
                (entityId, created) = await ApplyPrescriberAsync(row, existingId, ct);
                break;
            case DataMigrationKind.Catalogue:
                (entityId, created) = await ApplyAnalysisAsync(row, existingId, ct);
                break;
            case DataMigrationKind.History:
                (entityId, created) = await ApplyHistoryAsync(batchId, row, existingId, ct);
                break;
            case DataMigrationKind.OpeningBalances:
                (entityId, created) = await ApplyBalanceAsync(batchId, row, existingId, ct);
                break;
            default:
                throw new InvalidOperationException($"Unknown kind {kind}.");
        }

        if (record is null)
        {
            record = new MigrationRecord(kind, row.SourceKey, entityId, batchId, created, clock.Now);
            db.MigrationRecords.Add(record);
        }
        else
        {
            record.LastBatchId = batchId;
        }

        if (kind == DataMigrationKind.Patients && row.Text(MigrationSchema.OldNumber) is { } oldNumber
            && string.IsNullOrEmpty(record.OldNumber))
            record.OldNumber = oldNumber.Length > MigrationRecord.OldNumberMaxLength ? oldNumber[..MigrationRecord.OldNumberMaxLength] : oldNumber;

        return created;
    }

    private async Task<(Guid, bool)> ApplyPatientAsync(PlannedRow row, Guid? existingId, CancellationToken ct)
    {
        var payerId = row.Get<Guid?>("PayerId") ?? (row.Values.GetValueOrDefault("PayerId") as Guid?);
        var membership = row.Text(MigrationSchema.MembershipNumber);

        if (existingId is { } id)
        {
            var patient = await db.Patients.IgnoreQueryFilters().Include(p => p.Coverages).FirstAsync(p => p.Id == id, ct);

            // Only what the file leaves blank: what the laboratory entered in MarocLIS stays.
            patient.Cin ??= row.Text(MigrationSchema.Cin);
            patient.MobilePhone ??= row.Text(MigrationSchema.Mobile);
            patient.AlternatePhone ??= row.Text(MigrationSchema.Phone);
            patient.Email ??= row.Text(MigrationSchema.Email);
            patient.AddressLine ??= row.Text(MigrationSchema.Address);
            patient.City ??= row.Text(MigrationSchema.City);
            patient.LastNameArabic ??= row.Text(MigrationSchema.LastNameArabic);
            patient.FirstNameArabic ??= row.Text(MigrationSchema.FirstNameArabic);
            patient.GuardianFullName ??= row.Text(MigrationSchema.Guardian);
            if (patient.DateOfBirth is null && row.Values.GetValueOrDefault(MigrationSchema.BirthDate) is DateOnly dob)
            {
                patient.DateOfBirth = dob;
                patient.DeclaredAgeYears = null;
            }
            if (patient.Gender == Gender.Unknown && row.Values.GetValueOrDefault(MigrationSchema.Sex) is Gender gender)
                patient.Gender = gender;

            if (payerId is { } payer && membership is not null && patient.Coverages.All(c => c.PayerId != payer))
                patient.AddCoverage(new PatientCoverage(patient.Id, payer, membership, clock.Today)
                {
                    IsPrimary = patient.Coverages.Count == 0
                });

            return (patient.Id, false);
        }

        var number = await sequences.NextAsync(SequencePurpose.MedicalRecord, siteId: null, ct: ct);
        if (number.IsFailure) throw new DataMigrationException(number.FirstError.ResourceKey, number.FirstError.Args);

        var lastName = row.Text(MigrationSchema.LastName)!;
        var firstName = row.Text(MigrationSchema.FirstName)!;

        var created = new Patient(number.Value, lastName, firstName,
                                  row.Values.GetValueOrDefault(MigrationSchema.BirthDate) as DateOnly?,
                                  row.Values.GetValueOrDefault(MigrationSchema.Sex) is Gender g ? g : Gender.Unknown,
                                  currentUser.SiteId)
        {
            SearchKey = PatientSearchKey.Build(lastName, firstName),
            DeclaredAgeYears = row.Values.GetValueOrDefault(MigrationSchema.Age) as int?,
            LastNameArabic = row.Text(MigrationSchema.LastNameArabic),
            FirstNameArabic = row.Text(MigrationSchema.FirstNameArabic),
            Cin = row.Text(MigrationSchema.Cin),
            MobilePhone = row.Text(MigrationSchema.Mobile),
            AlternatePhone = row.Text(MigrationSchema.Phone),
            Email = row.Text(MigrationSchema.Email),
            AddressLine = row.Text(MigrationSchema.Address),
            City = row.Text(MigrationSchema.City),
            GuardianFullName = row.Text(MigrationSchema.Guardian)
        };

        if (payerId is { } payerToAdd && membership is not null)
            created.AddCoverage(new PatientCoverage(created.Id, payerToAdd, membership, clock.Today) { IsPrimary = true });

        db.Patients.Add(created);
        return (created.Id, true);
    }

    private async Task<(Guid, bool)> ApplyPrescriberAsync(PlannedRow row, Guid? existingId, CancellationToken ct)
    {
        var speciality = row.Text(MigrationSchema.Specialty);

        if (existingId is { } id)
        {
            var prescriber = await db.Prescribers.IgnoreQueryFilters().FirstAsync(p => p.Id == id, ct);
            prescriber.Inpe ??= row.Text(MigrationSchema.Inpe);
            prescriber.ProfessionalOrderNumber ??= row.Text(MigrationSchema.OrderNumber);
            prescriber.Phone ??= row.Text(MigrationSchema.Phone);
            prescriber.MobilePhone ??= row.Text(MigrationSchema.Mobile);
            prescriber.Email ??= row.Text(MigrationSchema.Email);
            prescriber.AddressLine ??= row.Text(MigrationSchema.Address);
            prescriber.City ??= row.Text(MigrationSchema.City);
            prescriber.InstitutionName ??= row.Text(MigrationSchema.Institution);
            if (prescriber.Speciality is null && speciality is not null) prescriber.Speciality = new MultilingualText(speciality);
            return (prescriber.Id, false);
        }

        var lastName = row.Text(MigrationSchema.LastName) ?? string.Empty;
        var firstName = row.Text(MigrationSchema.FirstName) ?? string.Empty;
        var kind = row.Values.GetValueOrDefault("Kind") is PrescriberKind k ? k : PrescriberKind.GeneralPractitioner;

        var created = new Prescriber(lastName, firstName, kind)
        {
            SearchKey = PatientSearchKey.Build(lastName, firstName),
            Speciality = speciality is null ? null : new MultilingualText(speciality),
            Inpe = row.Text(MigrationSchema.Inpe),
            ProfessionalOrderNumber = row.Text(MigrationSchema.OrderNumber),
            Phone = row.Text(MigrationSchema.Phone),
            MobilePhone = row.Text(MigrationSchema.Mobile),
            Email = row.Text(MigrationSchema.Email),
            AddressLine = row.Text(MigrationSchema.Address),
            City = row.Text(MigrationSchema.City),
            InstitutionName = row.Text(MigrationSchema.Institution)
        };

        db.Prescribers.Add(created);
        return (created.Id, true);
    }

    private async Task<(Guid, bool)> ApplyAnalysisAsync(PlannedRow row, Guid? existingId, CancellationToken ct)
    {
        var price = row.Values.GetValueOrDefault(MigrationSchema.Price) as decimal?;
        var coefficient = row.Values.GetValueOrDefault(MigrationSchema.Coefficient) as decimal?;
        var letter = row.Text(MigrationSchema.LetterKey) ?? NationalBiologyAct.BiologyLetterKey;
        var letterValue = row.Values.GetValueOrDefault("LetterKeyValue") as decimal?;
        var act = row.Text(MigrationSchema.ActCode);

        AnalysisTestTariff Tariff(Guid analysisId) => new(analysisId, null, clock.Today)
        {
            Price = price!.Value,
            LetterKey = letter,
            Coefficient = coefficient ?? 0m,
            LetterKeyValue = coefficient is > 0 ? letterValue ?? Math.Round(price.Value / coefficient.Value, 4) : 0m
        };

        if (existingId is { } id)
        {
            var analysis = await db.AnalysisTests.Include(a => a.Tariffs).FirstAsync(a => a.Id == id, ct);
            if (string.IsNullOrWhiteSpace(analysis.NationalActCode) && act is not null) analysis.NationalActCode = act;

            if (row.Values.GetValueOrDefault("NewTariff") is true && price is not null)
            {
                // From today on: the price of a request already registered stays the one it was quoted.
                var sameDay = analysis.Tariffs.FirstOrDefault(t => t.PayerId == null && t.ValidFrom == clock.Today && t.ValidUntil == null);
                if (sameDay is not null)
                {
                    sameDay.Price = price.Value;
                    sameDay.Coefficient = coefficient ?? sameDay.Coefficient;
                    sameDay.LetterKey = letter;
                }
                else
                {
                    var tariff = Tariff(analysis.Id);
                    analysis.AddTariff(tariff);
                    db.Tariffs.Add(tariff);
                }
            }

            return (analysis.Id, false);
        }

        var code = row.Text(MigrationSchema.AnalysisCode)!;
        var name = row.Text(MigrationSchema.AnalysisName)!;
        var departmentCode = row.Text(MigrationSchema.Department)!;
        var specimenCode = row.Text(MigrationSchema.Specimen)!;
        var unitCode = row.Text(MigrationSchema.Unit);

        var departmentId = await db.Departments.Where(d => d.Code == departmentCode).Select(d => d.Id).FirstAsync(ct);
        var specimenId = await db.SpecimenTypes.Where(s => s.Code == specimenCode).Select(s => s.Id).FirstAsync(ct);
        var unitId = unitCode is null ? null
            : await db.Units.Where(u => u.Code == unitCode).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);

        var created = new AnalysisTest(code, new MultilingualText(name), departmentId, specimenId)
        {
            NationalActCode = act
        };

        // One numeric parameter carrying the analysis's code: what a result is entered against. A
        // panel of several parameters is completed in the catalogue screen.
        created.AddParameter(new TestParameter(code, new MultilingualText(name), ResultValueType.Numeric) { UnitId = unitId });

        if (price is not null) created.AddTariff(Tariff(created.Id));

        db.AnalysisTests.Add(created);
        return (created.Id, true);
    }

    private async Task<(Guid, bool)> ApplyHistoryAsync(Guid batchId, PlannedRow row, Guid? existingId, CancellationToken ct)
    {
        var parameterId = await ParameterIdAsync(row.Text("Parameter")!, ct);
        var at = MigrationPlanner.AsInstant((DateTime)row.Values[MigrationSchema.ResultDate]!);

        HistoricalResult result;
        var created = existingId is null;

        if (existingId is { } id && await db.HistoricalResults.IgnoreQueryFilters().FirstOrDefaultAsync(h => h.Id == id, ct) is { } found)
        {
            result = found;
        }
        else
        {
            result = new HistoricalResult((Guid)row.Values["PatientId"]!, parameterId, at, row.Text(MigrationSchema.Value)!,
                                          currentUser.SiteId, batchId) { ImportedAt = clock.Now };
            db.HistoricalResults.Add(result);
            created = true;
        }

        result.PatientId = (Guid)row.Values["PatientId"]!;
        result.TestParameterId = parameterId;
        result.ResultDate = at;
        result.ValueText = row.Text(MigrationSchema.Value)!;
        result.NumericValue = row.Values.GetValueOrDefault("Numeric") as decimal?;
        result.ValueOperator = row.Text("Operator");
        result.Unit = row.Text(MigrationSchema.Unit);
        result.ReferenceRange = row.Text(MigrationSchema.ReferenceRange);
        result.OldAccessionNumber = row.Text(MigrationSchema.OldAccession);
        result.SourceCode = row.Text("SourceCode");

        return (result.Id, created);
    }

    private readonly Dictionary<string, Guid> _parameterIds = new(StringComparer.OrdinalIgnoreCase);

    private async Task<Guid> ParameterIdAsync(string target, CancellationToken ct)
    {
        if (_parameterIds.TryGetValue(target, out var known)) return known;

        var parts = target.Split('/', 2);
        var analysisCode = parts[0];
        var parameterCode = parts.Length > 1 ? parts[1] : parts[0];

        var id = await db.TestParameters.AsNoTracking()
            .Where(p => p.Code == parameterCode && p.AnalysisTest!.Code == analysisCode)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct)
            ?? throw new DataMigrationException("Migration_ParameterGone", target);

        _parameterIds[target] = id;
        return id;
    }

    private async Task<(Guid, bool)> ApplyBalanceAsync(Guid batchId, PlannedRow row, Guid? existingId, CancellationToken ct)
    {
        var amount = (decimal)row.Values[MigrationSchema.Amount]!;
        var asOf = (DateOnly)row.Values[MigrationSchema.AsOf]!;

        if (existingId is { } id && await db.OpeningBalances.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Id == id, ct) is { } found)
        {
            found.Amount = amount;
            found.AsOf = asOf;
            found.OldInvoiceNumber ??= row.Text(MigrationSchema.OldInvoiceNumber);
            found.OldInvoiceDate ??= row.Values.GetValueOrDefault(MigrationSchema.OldInvoiceDate) as DateOnly?;
            found.Label ??= row.Text(MigrationSchema.Label);
            return (found.Id, false);
        }

        var balance = new OpeningBalance(row.Values.GetValueOrDefault("PatientId") as Guid?, row.Values.GetValueOrDefault("PayerId") as Guid?,
                                         amount, asOf, currentUser.SiteId, batchId)
        {
            OldInvoiceNumber = row.Text(MigrationSchema.OldInvoiceNumber),
            OldInvoiceDate = row.Values.GetValueOrDefault(MigrationSchema.OldInvoiceDate) as DateOnly?,
            Label = row.Text(MigrationSchema.Label)
        };

        db.OpeningBalances.Add(balance);
        return (balance.Id, true);
    }

    // ---- Undo -------------------------------------------------------------------------------------

    public Task<MigrationUndoOutcome?> UndoLastAsync(CancellationToken ct = default) =>
        db.RunExclusiveAsync(async () =>
        {
            db.ChangeTracker.Clear();

            var batch = await db.MigrationBatches
                .Where(b => !b.IsUndone && b.CompletedAt != null)
                .OrderByDescending(b => b.StartedAt).ThenByDescending(b => b.CompletedAt)
                .FirstOrDefaultAsync(ct);
            if (batch is null) return null;

            var completed = batch.CompletedAt!.Value;
            var records = await db.MigrationRecords
                .Where(r => r.FirstBatchId == batch.Id && r.CreatedByBatch)
                .ToListAsync(ct);

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            AuditSaveChangesInterceptor.PendingReason =
                $"Annulation de la reprise de données {Describe(batch.Kind)} du {batch.StartedAt:dd/MM/yyyy HH:mm}";

            try
            {
                var removed = 0;
                var now = clock.Now;

                foreach (var record in records)
                {
                    if (!await RemoveIfUnusedAsync(batch.Kind, record.EntityId, completed, now, ct)) continue;
                    db.MigrationRecords.Remove(record);
                    removed++;
                }

                batch.MarkUndone(now, removed);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return new MigrationUndoOutcome(batch.Id, batch.Kind, removed, records.Count - removed);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
            finally
            {
                AuditSaveChangesInterceptor.PendingReason = null;
                db.ChangeTracker.Clear();
            }
        }, ct);

    /// <summary>
    /// Removes a record the run created, unless something has used or changed it since: a patient
    /// with a request, an invoice or another run's antériorités; a prescriber on a request; an analysis
    /// ordered or carrying results. Patients and prescribers are soft-deleted, like any removal of
    /// clinical data; history rows and balances, which nothing else refers to, are deleted.
    /// </summary>
    private async Task<bool> RemoveIfUnusedAsync(DataMigrationKind kind, Guid id, DateTimeOffset completed,
                                                 DateTimeOffset now, CancellationToken ct)
    {
        switch (kind)
        {
            case DataMigrationKind.Patients:
            {
                var patient = await db.Patients.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == id, ct);
                if (patient is null || patient.IsDeleted) return true;
                if (patient.ModifiedAt is { } modified && modified > completed) return false;
                if (patient.MergedIntoPatientId is not null) return false;
                if (await db.LabOrders.IgnoreQueryFilters().AnyAsync(o => o.PatientId == id, ct)) return false;
                if (await db.Invoices.IgnoreQueryFilters().AnyAsync(i => i.PatientId == id, ct)) return false;
                if (await db.HistoricalResults.IgnoreQueryFilters().AnyAsync(h => h.PatientId == id, ct)) return false;
                if (await db.OpeningBalances.IgnoreQueryFilters().AnyAsync(b => b.PatientId == id, ct)) return false;
                if (await db.Patients.IgnoreQueryFilters().AnyAsync(p => p.MergedIntoPatientId == id, ct)) return false;

                patient.IsDeleted = true;
                patient.DeletedAt = now;
                patient.DeletedByUserId = currentUser.IsAuthenticated ? currentUser.UserId : null;
                patient.DeletionReason = "Annulation de la reprise de données";
                patient.IsActive = false;
                return true;
            }

            case DataMigrationKind.Prescribers:
            {
                var prescriber = await db.Prescribers.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == id, ct);
                if (prescriber is null || prescriber.IsDeleted) return true;
                if (prescriber.ModifiedAt is { } modified && modified > completed) return false;
                if (await db.LabOrders.IgnoreQueryFilters().AnyAsync(o => o.PrescriberId == id, ct)) return false;

                prescriber.IsDeleted = true;
                prescriber.DeletedAt = now;
                prescriber.DeletedByUserId = currentUser.IsAuthenticated ? currentUser.UserId : null;
                prescriber.DeletionReason = "Annulation de la reprise de données";
                prescriber.IsActive = false;
                return true;
            }

            case DataMigrationKind.Catalogue:
            {
                var analysis = await db.AnalysisTests.Include(a => a.Parameters).Include(a => a.Tariffs)
                                       .FirstOrDefaultAsync(a => a.Id == id, ct);
                if (analysis is null) return true;
                if (analysis.ModifiedAt is { } modified && modified > completed) return false;

                var parameterIds = analysis.Parameters.Select(p => p.Id).ToList();
                if (await db.LabOrderItems.IgnoreQueryFilters().AnyAsync(i => i.AnalysisTestId == id, ct)) return false;
                if (await db.TestPanelItems.AnyAsync(i => i.AnalysisTestId == id, ct)) return false;
                if (await db.TestResults.IgnoreQueryFilters().AnyAsync(r => parameterIds.Contains(r.TestParameterId), ct)) return false;
                if (await db.HistoricalResults.IgnoreQueryFilters().AnyAsync(h => parameterIds.Contains(h.TestParameterId), ct)) return false;
                if (await db.InstrumentTestMappings.AnyAsync(m => parameterIds.Contains(m.TestParameterId), ct)) return false;
                if (await db.QcTargets.AnyAsync(q => parameterIds.Contains(q.TestParameterId), ct)) return false;

                db.AnalysisTests.Remove(analysis);
                return true;
            }

            case DataMigrationKind.History:
            {
                var row = await db.HistoricalResults.IgnoreQueryFilters().FirstOrDefaultAsync(h => h.Id == id, ct);
                if (row is not null) db.HistoricalResults.Remove(row);
                return true;
            }

            case DataMigrationKind.OpeningBalances:
            {
                var row = await db.OpeningBalances.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Id == id, ct);
                if (row is not null)
                {
                    if (row.ModifiedAt is { } modified && modified > completed) return false;
                    db.OpeningBalances.Remove(row);
                }
                return true;
            }

            default:
                return false;
        }
    }

    // ---- Look-ups ---------------------------------------------------------------------------------

    public Task<IReadOnlyList<OldNumberMatch>> FindByOldNumberAsync(string number, CancellationToken ct = default) =>
        db.RunExclusiveAsync<IReadOnlyList<OldNumberMatch>>(async () =>
        {
            var byFile = await db.MigrationRecords.AsNoTracking()
                .Where(r => r.Kind == DataMigrationKind.Patients && r.OldNumber == number)
                .Select(r => new { r.EntityId, r.OldNumber })
                .ToListAsync(ct);

            var byAccession = await db.HistoricalResults.AsNoTracking()
                .Where(h => h.OldAccessionNumber == number)
                .Select(h => h.PatientId)
                .Distinct()
                .ToListAsync(ct);

            var patientIds = byFile.Select(f => f.EntityId).Concat(byAccession).Distinct().ToList();
            if (patientIds.Count == 0) return [];

            var patients = await db.Patients.AsNoTracking()
                .Where(p => patientIds.Contains(p.Id))
                .Select(p => new { p.Id, p.MedicalRecordNumber, p.LastName, p.FirstName, p.DateOfBirth })
                .ToListAsync(ct);

            var oldNumbers = (await db.MigrationRecords.AsNoTracking()
                    .Where(r => r.Kind == DataMigrationKind.Patients && patientIds.Contains(r.EntityId) && r.OldNumber != null)
                    .Select(r => new { r.EntityId, r.OldNumber })
                    .ToListAsync(ct))
                .GroupBy(r => r.EntityId)
                .ToDictionary(g => g.Key, g => g.First().OldNumber);

            var accessionOnly = byFile.Count == 0;

            var history = await db.HistoricalResults.AsNoTracking()
                .Where(h => patientIds.Contains(h.PatientId) && (!accessionOnly || h.OldAccessionNumber == number))
                .OrderByDescending(h => h.ResultDate)
                .Take(500)
                .Join(db.TestParameters.AsNoTracking(), h => h.TestParameterId, p => p.Id,
                      (h, p) => new
                      {
                          h.PatientId,
                          Row = new HistoricalResultRow(h.Id, h.ResultDate, p.AnalysisTest!.Code + "/" + p.Code, p.Name.Fr,
                                                        h.ValueText, h.Unit, h.ReferenceRange, h.OldAccessionNumber)
                      })
                .ToListAsync(ct);

            return [.. patients.Select(p => new OldNumberMatch(
                p.Id, p.MedicalRecordNumber, $"{p.LastName} {p.FirstName}".Trim(), p.DateOfBirth,
                oldNumbers.GetValueOrDefault(p.Id),
                [.. history.Where(h => h.PatientId == p.Id).Select(h => h.Row)]))];
        }, ct);

    public Task<IReadOnlyList<OpeningBalanceRow>> GetOpeningBalancesAsync(CancellationToken ct = default) =>
        db.RunExclusiveAsync<IReadOnlyList<OpeningBalanceRow>>(async () =>
            await db.OpeningBalances.AsNoTracking()
                .OrderBy(b => b.AsOf).ThenBy(b => b.OldInvoiceNumber)
                .Take(5000)
                .Select(b => new OpeningBalanceRow(
                    b.Id, b.AsOf,
                    db.Patients.IgnoreQueryFilters().Where(p => p.Id == b.PatientId).Select(p => p.MedicalRecordNumber).FirstOrDefault(),
                    db.Patients.IgnoreQueryFilters().Where(p => p.Id == b.PatientId).Select(p => p.LastName + " " + p.FirstName).FirstOrDefault(),
                    db.Payers.Where(p => p.Id == b.PayerId).Select(p => p.Code).FirstOrDefault(),
                    db.Payers.Where(p => p.Id == b.PayerId).Select(p => p.Name.Fr).FirstOrDefault(),
                    b.Amount, b.OldInvoiceNumber, b.OldInvoiceDate, b.Label))
                .ToListAsync(ct), ct);
}
