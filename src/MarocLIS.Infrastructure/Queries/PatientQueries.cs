using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Morocco;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

/// <summary>One patient as the reception search grid shows them.</summary>
public sealed record PatientSearchRow(
    Guid Id,
    string MedicalRecordNumber,
    string FullName,
    string? FullNameArabic,
    Gender Gender,
    DateOnly? DateOfBirth,
    int? AgeYears,
    string? Cin,
    string? MobilePhone,
    string? City,
    string? PayerName,
    DateTimeOffset? LastOrderAt,
    int OrderCount,
    bool HasCriticalAlert)
{
    /// <summary>What the file's identity still lacks; <see cref="PatientIdentityGap.None"/> when complete.</summary>
    public PatientIdentityGap IdentityGaps { get; init; }

    /// <summary>The file was anonymised: kept as the trace of an erasure, closed to any change.</summary>
    public bool IsAnonymized { get; init; }
}

/// <summary>Which files the patient list shows, besides the search term.</summary>
public enum PatientListScope
{
    All = 0,

    /// <summary>Files whose identity is provisional or lacks the sex or the date of birth.</summary>
    IdentityIncomplete = 1,

    /// <summary>Files with an order registered today at the signed-in site.</summary>
    SeenToday = 2
}

/// <summary>The patient list's filters: the search term, the scope chip and the payer.</summary>
public sealed record PatientListQuery(
    string? Term,
    Guid SiteId,
    PatientListScope Scope = PatientListScope.All,
    Guid? PayerId = null);

/// <summary>How many files each scope chip would show for the same term and payer.</summary>
public sealed record PatientListCounts(int All, int IdentityIncomplete, int SeenToday);

/// <summary>An order in the patient's history panel.</summary>
public sealed record PatientOrderRow(
    Guid Id,
    string AccessionNumber,
    DateTimeOffset RegisteredAt,
    LabOrderStatus Status,
    OrderPriority Priority,
    int AnalysisCount,
    string? PrescriberName);

/// <summary>A payer as the coverage panel offers it.</summary>
public sealed record PayerOption(
    Guid Id,
    string Code,
    string Name,
    PayerKind Kind,
    decimal DefaultReimbursementRate,
    bool UsesNationalReferenceTariff,
    bool RequiresPrescription);

/// <summary>An insurance entitlement as the patient file shows it.</summary>
public sealed record CoverageRow(
    Guid CoverageId,
    Guid PayerId,
    string PayerName,
    PayerKind Kind,
    string MembershipNumber,
    BeneficiaryRelationship Relationship,
    string? InsuredPersonName,
    DateOnly ValidFrom,
    DateOnly? ValidUntil,
    bool IsPrimary,
    bool IsChronicDiseaseScheme,
    string? PriorAgreementNumber,
    decimal EffectiveRate,
    bool IsValidToday);

/// <summary>A consent on file, with whatever evidence backs it.</summary>
/// <summary>One clinical or logistic flag on a file.</summary>
public sealed record AlertRow(
    Guid AlertId,
    PatientAlertSeverity Severity,
    MultilingualText Message,
    DateOnly? ExpiresOn)
{
    public bool IsActive(DateOnly today) => ExpiresOn is null || ExpiresOn >= today;

    /// <summary>
    /// A critical alert blocks registration until it is acknowledged, so it is the one an operator
    /// has to be able to see at a glance in a list of five.
    /// </summary>
    public bool IsBlocking => Severity == PatientAlertSeverity.Critical;
}

public sealed record ConsentRow(
    Guid ConsentId,
    ConsentPurpose Purpose,
    ConsentChannel Channel,
    DateTimeOffset GivenAt,
    string? SignedByName,
    string? SignedByRelationship,
    DateOnly? ExpiresOn,
    bool HasEvidenceDocument,
    DateTimeOffset? WithdrawnAt,
    bool RequiresWrittenEvidence);

/// <summary>
/// The full identity of one file, as the editor loads it. A flat projection rather than the
/// aggregate: the editor must not be able to mutate a tracked entity behind the service's back.
/// </summary>
public sealed record PatientDetail(
    Guid Id,
    string MedicalRecordNumber,
    string LastName,
    string FirstName,
    string? LastNameArabic,
    string? FirstNameArabic,
    Gender Gender,
    DateOnly? DateOfBirth,
    int? DeclaredAgeYears,
    string? PlaceOfBirth,
    string? Cin,
    string? PassportNumber,
    string NationalityCode,
    string? GuardianFullName,
    string? GuardianCin,
    string? GuardianRelationship,
    string? AddressLine,
    string? City,
    MoroccanRegion Region,
    string? PostalCode,
    string? MobilePhone,
    string? AlternatePhone,
    string? Email,
    AppLanguage PreferredLanguage,
    bool IsPregnant,
    DateOnly? LastMenstrualPeriod,
    decimal? WeightKg,
    decimal? HeightCm,
    BloodGroup BloodGroup,
    string? CurrentTreatment,
    string? KnownAllergies,
    bool OptedOutOfSecondaryUse,
    bool IsAnonymized,
    DateOnly? RetentionUntil,
    Guid? MergedIntoPatientId,
    bool IsActive);

public interface IPatientQueries
{
    /// <summary>
    /// Free-text patient search. One box, because a reception clerk types whatever the patient
    /// said — a name fragment, a file number, a CIN or a phone number — and should not have to
    /// choose a field first.
    /// </summary>
    Task<PagedResult<PatientSearchRow>> SearchAsync(string? term, Guid siteId, int page = 1,
                                                     int pageSize = 50, CancellationToken ct = default);

    /// <summary>
    /// The patient list: the same search, narrowed to a scope and to the payer of the file's
    /// current primary coverage.
    /// </summary>
    Task<PagedResult<PatientSearchRow>> ListAsync(PatientListQuery query, int page = 1, int pageSize = 50,
                                                   CancellationToken ct = default);

    /// <summary>The counts shown on the list's scope chips, for the query's term and payer.</summary>
    Task<PatientListCounts> CountListAsync(PatientListQuery query, CancellationToken ct = default);

    Task<IReadOnlyList<PatientOrderRow>> GetOrderHistoryAsync(Guid patientId, int take = 30,
                                                               CancellationToken ct = default);

    /// <summary>Active payers, for the coverage panel.</summary>
    Task<IReadOnlyList<PayerOption>> GetPayersAsync(CancellationToken ct = default);

    /// <summary>The patient's insurance entitlements, most relevant first.</summary>
    Task<IReadOnlyList<CoverageRow>> GetCoveragesAsync(Guid patientId,
                                                        CancellationToken ct = default);

    Task<IReadOnlyList<ConsentRow>> GetConsentsAsync(Guid patientId,
                                                      CancellationToken ct = default);

    /// <summary>
    /// The file's alerts, expired ones included. An expired alert is history the next clinician may
    /// need — "difficult venous access, 2024" does not stop being true because the flag lapsed —
    /// so it is shown greyed rather than filtered away.
    /// </summary>
    Task<IReadOnlyList<AlertRow>> GetAlertsAsync(Guid patientId, CancellationToken ct = default);

    /// <summary>The identity the editor loads when an existing file is opened.</summary>
    Task<PatientDetail?> GetDetailAsync(Guid patientId, CancellationToken ct = default);

    /// <summary>Builds the accent- and case-insensitive key stored on the patient for searching.</summary>
    static string BuildSearchKey(string lastName, string firstName) =>
        PatientSearchKey.Build(lastName, firstName);

    /// <summary>
    /// Strips diacritics, case, spaces and punctuation.
    /// <para>
    /// Delegates to <see cref="PatientSearchKey"/> in the domain rather than implementing it here:
    /// the registration path needs the identical function to write the key, and the application
    /// layer cannot reference this assembly. Two copies that drifted apart would make patients
    /// unfindable by name.
    /// </para>
    /// </summary>
    static string NormaliseForSearch(string? value) => PatientSearchKey.Normalise(value);
}

public sealed class PatientQueries(LisDbContext db, IClock clock, IPermissionGuard guard) : IPatientQueries
{
    // Every read of a patient's file passes here. The screens call these queries directly, not
    // through a service, so without it anyone signed in could search and read every file at the
    // site; the menu hiding the patient screens was the only thing in the way.
    private LisDbContext PatientData()
    {
        var permitted = guard.Require(Permissions.Patients.View);
        if (permitted.IsFailure) throw new UnauthorizedAccessException(permitted.FirstError.ResourceKey);
        return db;
    }

    public Task<PagedResult<PatientSearchRow>> SearchAsync(
        string? term, Guid siteId, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        ListAsync(new PatientListQuery(term, siteId), page, pageSize, ct);

    public async Task<PagedResult<PatientSearchRow>> ListAsync(
        PatientListQuery list, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        PatientData();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);

        var query = InScope(Matching(list.Term, list.PayerId), list.Scope, list.SiteId);

        var total = await query.CountAsync(ct);

        var today = clock.Today;

        var rows = await query
            .OrderBy(p => p.LastName).ThenBy(p => p.FirstName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new
            {
                p.Id,
                p.MedicalRecordNumber,
                p.LastName,
                p.FirstName,
                p.LastNameArabic,
                p.FirstNameArabic,
                p.Gender,
                p.DateOfBirth,
                p.DeclaredAgeYears,
                p.Cin,
                p.MobilePhone,
                p.City,
                p.IsProvisionalIdentity,
                p.IsAnonymized,
                PayerName = p.Coverages
                    .Where(c => c.IsPrimary
                                && c.ValidFrom <= today
                                && (c.ValidUntil == null || c.ValidUntil >= today))
                    .Select(c => c.Payer!.Name.Fr)
                    .FirstOrDefault(),
                LastOrderAt = db.LabOrders
                    .Where(o => o.PatientId == p.Id && o.Status != LabOrderStatus.Cancelled)
                    .Max(o => (DateTimeOffset?)o.RegisteredAt),
                OrderCount = db.LabOrders.Count(o => o.PatientId == p.Id
                                                     && o.Status != LabOrderStatus.Cancelled),
                HasCriticalAlert = p.Alerts.Any(a => a.Severity == PatientAlertSeverity.Critical
                                                     && (a.ExpiresOn == null || a.ExpiresOn >= today))
            })
            .ToListAsync(ct);

        var mapped = rows.Select(r => new PatientSearchRow(
            r.Id,
            r.MedicalRecordNumber,
            $"{r.LastName} {r.FirstName}".Trim(),
            string.IsNullOrWhiteSpace(r.LastNameArabic) && string.IsNullOrWhiteSpace(r.FirstNameArabic)
                ? null
                : $"{r.LastNameArabic} {r.FirstNameArabic}".Trim(),
            r.Gender,
            r.DateOfBirth,
            AgeInYears(r.DateOfBirth, today) ?? r.DeclaredAgeYears,
            r.Cin,
            r.MobilePhone,
            r.City,
            r.PayerName,
            r.LastOrderAt,
            r.OrderCount,
            r.HasCriticalAlert)
        {
            IsAnonymized = r.IsAnonymized,
            IdentityGaps = r.IsAnonymized
                ? PatientIdentityGap.None
                : (r.IsProvisionalIdentity ? PatientIdentityGap.Provisional : PatientIdentityGap.None)
                  | (r.Gender == Gender.Unknown ? PatientIdentityGap.Gender : PatientIdentityGap.None)
                  | (r.DateOfBirth is null ? PatientIdentityGap.DateOfBirth : PatientIdentityGap.None)
        }).ToList();

        return new PagedResult<PatientSearchRow>(mapped, total, page, pageSize);
    }

    public async Task<PatientListCounts> CountListAsync(PatientListQuery list, CancellationToken ct = default)
    {
        PatientData();
        var matching = Matching(list.Term, list.PayerId);

        return new PatientListCounts(
            await matching.CountAsync(ct),
            await InScope(matching, PatientListScope.IdentityIncomplete, list.SiteId).CountAsync(ct),
            await InScope(matching, PatientListScope.SeenToday, list.SiteId).CountAsync(ct));
    }

    // The free-text match and the payer. Merged files are excluded: a search must land on the
    // surviving record, never on the one that was merged away.
    private IQueryable<Patient> Matching(string? term, Guid? payerId)
    {
        var query = db.Patients.AsNoTracking()
            .Where(p => p.MergedIntoPatientId == null);

        if (!string.IsNullOrWhiteSpace(term))
        {
            var raw = term.Trim();
            var normalised = IPatientQueries.NormaliseForSearch(raw);
            var digits = new string(raw.Where(char.IsDigit).ToArray());
            var upper = raw.Replace(" ", string.Empty).ToUpperInvariant();

            query = query.Where(p =>
                p.SearchKey.Contains(normalised)
                || p.MedicalRecordNumber.Contains(upper)
                || (p.Cin != null && p.Cin.Contains(upper))
                || (digits.Length >= 4 && p.MobilePhone != null && p.MobilePhone.Contains(digits))
                // The previous application's numbers (« reprise de données »): the old file number,
                // and the old accession number of an antériorité reprise. Exact, as typed or scanned.
                || db.MigrationRecords.Any(m => m.Kind == Domain.Migration.DataMigrationKind.Patients
                                                && m.EntityId == p.Id && m.OldNumber == raw)
                || db.HistoricalResults.Any(h => h.PatientId == p.Id && h.OldAccessionNumber == raw));
        }

        if (payerId is { } payer)
        {
            // The payer the list shows: the primary coverage valid today.
            var today = clock.Today;
            query = query.Where(p => p.Coverages.Any(c => c.IsPrimary
                                                          && c.PayerId == payer
                                                          && c.ValidFrom <= today
                                                          && (c.ValidUntil == null || c.ValidUntil >= today)));
        }

        return query;
    }

    private IQueryable<Patient> InScope(IQueryable<Patient> query, PatientListScope scope, Guid siteId)
    {
        switch (scope)
        {
            // The same three gaps the row reports; an anonymised file is closed, not incomplete.
            case PatientListScope.IdentityIncomplete:
                return query.Where(p => !p.IsAnonymized
                                        && (p.IsProvisionalIdentity
                                            || p.Gender == Gender.Unknown
                                            || p.DateOfBirth == null));

            // Today as the dashboard counts it: registered since midnight at this site, not cancelled.
            case PatientListScope.SeenToday:
                var now = clock.Now;
                var start = new DateTimeOffset(now.Date, now.Offset);
                var end = start.AddDays(1);
                return query.Where(p => db.LabOrders.Any(o => o.PatientId == p.Id
                                                              && o.SiteId == siteId
                                                              && o.RegisteredAt >= start
                                                              && o.RegisteredAt < end
                                                              && o.Status != LabOrderStatus.Cancelled));

            default:
                return query;
        }
    }

    public async Task<IReadOnlyList<PatientOrderRow>> GetOrderHistoryAsync(
        Guid patientId, int take = 30, CancellationToken ct = default) =>
        await PatientData().LabOrders.AsNoTracking()
            .Where(o => o.PatientId == patientId)
            .OrderByDescending(o => o.RegisteredAt)
            .Take(take)
            .Select(o => new PatientOrderRow(
                o.Id,
                o.AccessionNumber,
                o.RegisteredAt,
                o.Status,
                o.Priority,
                o.Items.Count(i => i.Status != LabOrderItemStatus.Cancelled),
                o.Prescriber != null
                    ? o.Prescriber.LastName + " " + o.Prescriber.FirstName
                    : o.PrescriberFreeText))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<PayerOption>> GetPayersAsync(CancellationToken ct = default) =>
        await db.Payers.AsNoTracking()
            .Where(p => p.IsActive)
            // SelfPay first — it is the commonest case at a private laboratory counter, and the
            // one the clerk reaches for when the patient has no card on them.
            .OrderBy(p => p.Kind == PayerKind.SelfPay ? 0 : 1)
            .ThenBy(p => p.Name.Fr)
            .Select(p => new PayerOption(p.Id, p.Code, p.Name.Fr, p.Kind,
                                          p.DefaultReimbursementRate,
                                          p.UsesNationalReferenceTariff,
                                          p.RequiresPrescription))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<CoverageRow>> GetCoveragesAsync(
        Guid patientId, CancellationToken ct = default)
    {
        PatientData();
        var today = clock.Today;

        var rows = await db.Patients.AsNoTracking()
            .Where(p => p.Id == patientId)
            .SelectMany(p => p.Coverages)
            .Select(c => new
            {
                c.Id,
                c.PayerId,
                PayerName = c.Payer!.Name.Fr,
                c.Payer!.Kind,
                PayerDefaultRate = c.Payer!.DefaultReimbursementRate,
                c.MembershipNumber,
                c.Relationship,
                c.InsuredPersonName,
                c.ValidFrom,
                c.ValidUntil,
                c.IsPrimary,
                c.IsChronicDiseaseScheme,
                c.PriorAgreementNumber,
                c.PriorAgreementValidUntil,
                c.ReimbursementRateOverride
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(r =>
            {
                // The effective rate is recomputed the way PatientCoverage.EffectiveRate does:
                // a live ALD agreement takes the file to 100 %, otherwise the override or the
                // payer default applies. Shown because it is the number that decides what the
                // patient actually pays at the counter.
                var ald = r.IsChronicDiseaseScheme
                          && !string.IsNullOrWhiteSpace(r.PriorAgreementNumber)
                          && (r.PriorAgreementValidUntil is null
                              || r.PriorAgreementValidUntil >= today);

                var rate = ald ? 1.0m : r.ReimbursementRateOverride ?? r.PayerDefaultRate;

                return new CoverageRow(
                    r.Id, r.PayerId, r.PayerName, r.Kind, r.MembershipNumber, r.Relationship,
                    r.InsuredPersonName, r.ValidFrom, r.ValidUntil, r.IsPrimary,
                    r.IsChronicDiseaseScheme, r.PriorAgreementNumber, rate,
                    r.ValidFrom <= today && (r.ValidUntil is null || r.ValidUntil >= today));
            })
            .OrderByDescending(r => r.IsValidToday)
            .ThenByDescending(r => r.IsPrimary)
            .ThenByDescending(r => r.ValidFrom)
        ];
    }

    public async Task<IReadOnlyList<ConsentRow>> GetConsentsAsync(
        Guid patientId, CancellationToken ct = default)
    {
        var rows = await PatientData().Patients.AsNoTracking()
            .Where(p => p.Id == patientId)
            .SelectMany(p => p.Consents)
            .Select(c => new
            {
                c.Id,
                c.Purpose,
                c.Channel,
                c.GivenAt,
                c.SignedByName,
                c.SignedByRelationship,
                c.ExpiresOn,
                HasEvidence = c.EvidenceDocument != null,
                c.WithdrawnAt
            })
            .ToListAsync(ct);

        return
        [
            .. rows
                .Select(r => new ConsentRow(
                    r.Id, r.Purpose, r.Channel, r.GivenAt, r.SignedByName,
                    r.SignedByRelationship, r.ExpiresOn, r.HasEvidence, r.WithdrawnAt,
                    PatientConsent.RequiresWrittenEvidence(r.Purpose)))
                .OrderBy(r => r.WithdrawnAt != null)
                .ThenByDescending(r => r.GivenAt)
        ];
    }

    public async Task<IReadOnlyList<AlertRow>> GetAlertsAsync(
        Guid patientId, CancellationToken ct = default)
    {
        var rows = await PatientData().Patients.AsNoTracking()
            .Where(p => p.Id == patientId)
            .SelectMany(p => p.Alerts)
            .Select(a => new AlertRow(a.Id, a.Severity, a.Message, a.ExpiresOn))
            .ToListAsync(ct);

        var today = clock.Today;

        return
        [
            // Active first, most severe first. A critical alert blocks registration, so it has to
            // be the row an operator sees without reading the list.
            .. rows.OrderByDescending(a => a.IsActive(today))
                   .ThenByDescending(a => a.Severity)
                   .ThenBy(a => a.ExpiresOn)
        ];
    }

    public Task<PatientDetail?> GetDetailAsync(Guid patientId, CancellationToken ct = default) =>
        PatientData().Patients.AsNoTracking()
            .Where(p => p.Id == patientId)
            .Select(p => new PatientDetail(
                p.Id, p.MedicalRecordNumber, p.LastName, p.FirstName,
                p.LastNameArabic, p.FirstNameArabic, p.Gender, p.DateOfBirth,
                p.DeclaredAgeYears, p.PlaceOfBirth, p.Cin, p.PassportNumber,
                p.NationalityCode, p.GuardianFullName, p.GuardianCin, p.GuardianRelationship,
                p.AddressLine, p.City, p.Region, p.PostalCode,
                p.MobilePhone, p.AlternatePhone, p.Email, p.PreferredLanguage,
                p.IsPregnant, p.LastMenstrualPeriod, p.WeightKg, p.HeightCm,
                p.BloodGroup, p.CurrentTreatment, p.KnownAllergies,
                p.OptedOutOfSecondaryUse, p.IsAnonymized, p.RetentionUntil,
                p.MergedIntoPatientId, p.IsActive))
            .FirstOrDefaultAsync(ct);

    private static int? AgeInYears(DateOnly? dateOfBirth, DateOnly on)
    {
        if (dateOfBirth is not { } dob) return null;
        var age = on.Year - dob.Year;
        if (on < dob.AddYears(age)) age--;
        return age < 0 ? null : age;
    }
}
