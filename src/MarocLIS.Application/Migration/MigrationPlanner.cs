using System.Globalization;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Migration;
using MarocLIS.Domain.Morocco;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;

namespace MarocLIS.Application.Migration;

/// <summary>
/// Reads, checks and plans every source row against what the database already holds: the
/// « Simulation ». Pure: it writes nothing, so the same plan is shown before « Importer » and
/// recomputed by it.
/// <para>
/// The rules, kind by kind, are those of the screen's report: a required value missing or
/// unreadable leaves the row out (« en erreur »); an optional value that cannot be read is left out
/// with a warning and the row goes in; a coded value with no correspondence blocks the import, so
/// nothing is half-mapped. A row already imported (its source key is known) or matched to a record
/// already here only fills what that record leaves blank: an import never overwrites what the
/// laboratory has entered in MarocLIS.
/// </para>
/// </summary>
public sealed class MigrationPlanner
{
    private readonly MigrationRequest _request;
    private readonly MigrationContext _context;
    private readonly List<MigrationAnomaly> _anomalies = [];
    private readonly Dictionary<string, int> _columnIndex = new(StringComparer.Ordinal);

    public MigrationPlanner(MigrationRequest request, MigrationContext context)
    {
        _request = request;
        _context = context;

        foreach (var (field, column) in request.Columns)
        {
            var index = request.Table.IndexOf(column);
            if (index >= 0) _columnIndex[field] = index;
        }
    }

    public static string KeyOf(string sourceSystem, DataMigrationKind kind, string id) =>
        $"{sourceSystem.Trim().ToUpperInvariant()}|{kind}|{id.Trim()}";

    /// <summary>The first line of a file holds the headers, so source row i is line i + 2.</summary>
    public static int LineOf(int rowIndex) => rowIndex + 2;

    public MigrationAnalysis Plan(IProgress<MigrationProgress>? progress = null, CancellationToken ct = default)
    {
        var rows = new List<PlannedRow>(_request.Table.Rows.Count);

        // Missing required columns first: every row would fail the same way.
        foreach (var field in MigrationSchema.Fields(_request.Kind).Where(f => f.Required && !_columnIndex.ContainsKey(f.Key)))
            Add(0, field.Key, MigrationSeverity.Blocking, "Migration_ColumnNotMapped", [Label(field.Key)],
                "Migration_SuggestMapColumn");

        if (_request.Kind is DataMigrationKind.History or DataMigrationKind.OpeningBalances
            && !_columnIndex.ContainsKey(MigrationSchema.PatientSourceId)
            && !_columnIndex.ContainsKey(MigrationSchema.PatientOldNumber)
            && !_columnIndex.ContainsKey(MigrationSchema.PatientCin)
            && (_request.Kind == DataMigrationKind.History || !_columnIndex.ContainsKey(MigrationSchema.PayerCode)))
            Add(0, MigrationSchema.PatientSourceId, MigrationSeverity.Blocking, "Migration_PatientColumnNotMapped", [],
                "Migration_SuggestMapColumn");

        if (_request.Kind == DataMigrationKind.Prescribers
            && !_columnIndex.ContainsKey(MigrationSchema.LastName) && !_columnIndex.ContainsKey(MigrationSchema.Institution))
            Add(0, MigrationSchema.LastName, MigrationSeverity.Blocking, "Migration_ColumnNotMapped", [Label(MigrationSchema.LastName)],
                "Migration_SuggestMapColumn");

        var seenKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenCins = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenNames = new Dictionary<string, int>(StringComparer.Ordinal);
        var unmapped = new Dictionary<(MigrationCodeList, string), int>();

        for (var i = 0; i < _request.Table.Rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i % 500 == 0) progress?.Report(new MigrationProgress(i, _request.Table.Rows.Count, "Migration.StepAnalyse"));

            var line = LineOf(i);
            var planned = new PlannedRow { Row = line };
            var before = _anomalies.Count;

            switch (_request.Kind)
            {
                case DataMigrationKind.Patients: PlanPatient(i, planned, seenCins, seenNames, unmapped); break;
                case DataMigrationKind.Prescribers: PlanPrescriber(i, planned, unmapped); break;
                case DataMigrationKind.Catalogue: PlanCatalogue(i, planned, unmapped); break;
                case DataMigrationKind.History: PlanHistory(i, planned, unmapped); break;
                case DataMigrationKind.OpeningBalances: PlanBalance(i, planned, unmapped); break;
            }

            if (planned.Outcome != MigrationOutcome.Error && planned.SourceKey.Length > 0)
            {
                if (planned.SourceKey.Length > MigrationRecord.SourceKeyMaxLength)
                {
                    Fail(planned, null, "Migration_SourceKeyTooLong", [MigrationRecord.SourceKeyMaxLength]);
                }
                else if (seenKeys.TryGetValue(planned.SourceKey, out var first))
                {
                    Fail(planned, MigrationSchema.SourceId, "Migration_DuplicateInSource", [first],
                         "Migration_SuggestRemoveDuplicate");
                }
                else
                {
                    seenKeys[planned.SourceKey] = line;
                }
            }

            // A row whose only problem is a missing correspondence is not « en erreur »: it waits.
            if (planned.Outcome != MigrationOutcome.Error
                && _anomalies.Skip(before).Any(a => a.Severity == MigrationSeverity.Error))
                planned.Outcome = MigrationOutcome.Error;

            rows.Add(planned);
        }

        foreach (var ((list, value), count) in unmapped)
            Add(0, FieldOf(list), MigrationSeverity.Blocking, "Migration_ValueNotMapped", [value, count, ListLabel(list)],
                "Migration_SuggestCorrespondence");

        progress?.Report(new MigrationProgress(_request.Table.Rows.Count, _request.Table.Rows.Count, "Migration.StepAnalyse"));

        return new MigrationAnalysis
        {
            Kind = _request.Kind,
            Rows = rows,
            Anomalies = [.. _anomalies.OrderBy(a => a.Row).ThenByDescending(a => a.Severity)]
        };
    }

    // ---- Patients ---------------------------------------------------------------------------------

    private void PlanPatient(int i, PlannedRow row, Dictionary<string, int> seenCins, Dictionary<string, int> seenNames,
                             Dictionary<(MigrationCodeList, string), int> unmapped)
    {
        var lastName = RequiredText(i, row, MigrationSchema.LastName);
        var firstName = RequiredText(i, row, MigrationSchema.FirstName);
        if (row.Outcome == MigrationOutcome.Error) return;

        var birth = OptionalDate(i, row, MigrationSchema.BirthDate);
        if (birth is { } dob)
        {
            if (dob > _context.Today) { Warn(row, MigrationSchema.BirthDate, "Migration_BirthDateInFuture", [Raw(i, MigrationSchema.BirthDate)]); birth = null; }
            else if (dob.Year < 1900) { Warn(row, MigrationSchema.BirthDate, "Migration_BirthDateTooOld", [Raw(i, MigrationSchema.BirthDate)]); birth = null; }
        }

        var age = OptionalInteger(i, row, MigrationSchema.Age);
        if (age is < 0 or > 130) { Warn(row, MigrationSchema.Age, "Migration_AgeOutOfRange", [age]); age = null; }

        if (birth is null && age is null)
        {
            Fail(row, MigrationSchema.BirthDate, "Migration_BirthDateOrAgeRequired", [], "Migration_SuggestBirthDate");
            return;
        }

        var gender = Gender.Unknown;
        if (Coded(i, row, MigrationSchema.Sex, MigrationCodeList.Sex, unmapped) is { } sex)
            gender = sex switch { "M" => Gender.Male, "F" => Gender.Female, _ => Gender.Unknown };

        string? cin = null;
        if (OptionalText(i, row, MigrationSchema.Cin) is { } rawCin)
        {
            var normal = MoroccanIdentifiers.NormalizeCin(rawCin);
            if (MoroccanIdentifiers.IsValidCin(normal)) cin = normal;
            else Warn(row, MigrationSchema.Cin, "Migration_CinInvalid", [rawCin], "Migration_SuggestCinFormat");
        }

        var mobile = Phone(i, row, MigrationSchema.Mobile);
        var phone = Phone(i, row, MigrationSchema.Phone);
        var email = OptionalText(i, row, MigrationSchema.Email);
        if (email is not null && (!email.Contains('@', StringComparison.Ordinal) || email.Contains(' ', StringComparison.Ordinal)))
        {
            Warn(row, MigrationSchema.Email, "Migration_EmailInvalid", [email]);
            email = null;
        }

        var payerCode = Coded(i, row, MigrationSchema.PayerCode, MigrationCodeList.Payer, unmapped);
        var membership = OptionalText(i, row, MigrationSchema.MembershipNumber);
        Guid? payerId = null;
        if (payerCode is not null && payerCode != CorrespondenceTables.Ignore)
        {
            if (_context.PayerIds.TryGetValue(payerCode, out var id)) payerId = id;
            if (membership is null)
            {
                Warn(row, MigrationSchema.MembershipNumber, "Migration_CoverageWithoutNumber", [payerCode]);
                payerId = null;
            }
        }
        else if (membership is not null && payerCode is null && string.IsNullOrWhiteSpace(Raw(i, MigrationSchema.PayerCode)))
        {
            Warn(row, MigrationSchema.MembershipNumber, "Migration_NumberWithoutPayer", [membership]);
        }

        var guardian = OptionalText(i, row, MigrationSchema.Guardian);
        if (guardian is null && birth is { } b2 && AgeOn(b2, _context.Today) < 18)
            Note(row, MigrationSchema.Guardian, MigrationSeverity.Info, "Migration_MinorWithoutGuardian", []);

        var oldNumber = OptionalText(i, row, MigrationSchema.OldNumber);
        var sourceId = OptionalText(i, row, MigrationSchema.SourceId);

        row.Values[MigrationSchema.LastName] = lastName;
        row.Values[MigrationSchema.FirstName] = firstName;
        row.Values[MigrationSchema.LastNameArabic] = OptionalText(i, row, MigrationSchema.LastNameArabic);
        row.Values[MigrationSchema.FirstNameArabic] = OptionalText(i, row, MigrationSchema.FirstNameArabic);
        row.Values[MigrationSchema.BirthDate] = birth;
        row.Values[MigrationSchema.Age] = birth is null ? age : null;
        row.Values[MigrationSchema.Sex] = gender;
        row.Values[MigrationSchema.Cin] = cin;
        row.Values[MigrationSchema.Mobile] = mobile;
        row.Values[MigrationSchema.Phone] = phone;
        row.Values[MigrationSchema.Email] = email;
        row.Values[MigrationSchema.Address] = OptionalText(i, row, MigrationSchema.Address);
        row.Values[MigrationSchema.City] = OptionalText(i, row, MigrationSchema.City);
        row.Values[MigrationSchema.Guardian] = guardian;
        row.Values["PayerId"] = payerId;
        row.Values[MigrationSchema.MembershipNumber] = payerId is null ? null : membership;
        row.Values[MigrationSchema.OldNumber] = oldNumber;

        var nameKey = $"{PatientSearchKey.Build(lastName!, firstName!)}|{birth?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? "?"}";

        // The row's identity in the old application: its id, else its file number, else its CIN,
        // else name and birth date — the order in which those survive a second export unchanged.
        var identity = sourceId ?? (oldNumber is null ? null : "N:" + oldNumber) ?? (cin is null ? null : "C:" + cin)
                       ?? (birth is null ? null : "I:" + nameKey);
        if (identity is null)
        {
            Fail(row, MigrationSchema.SourceId, "Migration_SourceIdRequired", [], "Migration_SuggestSourceId");
            return;
        }

        row.SourceKey = KeyOf(_request.SourceSystem, DataMigrationKind.Patients, identity);

        // Duplicates inside the source.
        if (cin is not null)
        {
            if (seenCins.TryGetValue(cin, out var firstCin))
            {
                Fail(row, MigrationSchema.Cin, "Migration_DuplicateCinInSource", [cin, firstCin], "Migration_SuggestRemoveDuplicate");
                return;
            }
            seenCins[cin] = row.Row;
        }

        if (birth is not null)
        {
            if (seenNames.TryGetValue(nameKey, out var firstName2))
                Warn(row, MigrationSchema.LastName, "Migration_DuplicateNameInSource", [firstName2], "Migration_SuggestCheckDuplicate");
            else seenNames[nameKey] = row.Row;
        }

        // Against the patients already here.
        PatientSnapshot? existing = null;
        if (_context.Records.TryGetValue(row.SourceKey, out var known))
        {
            existing = _context.Patients.FirstOrDefault(p => p.Id == known);
            row.KnownSourceKey = true;
            row.ExistingId = known;
        }
        else
        {
            var byCin = cin is null ? [] : _context.Patients.Where(p => p.Cin == cin).ToList();
            if (byCin.Count > 1)
            {
                Fail(row, MigrationSchema.Cin, "Migration_SeveralPatientsMatch", [cin, string.Join(", ", byCin.Select(p => p.RecordNumber))],
                     "Migration_SuggestMergeFirst");
                return;
            }

            if (byCin.Count == 1)
            {
                existing = byCin[0];
                Note(row, MigrationSchema.Cin, MigrationSeverity.Info, "Migration_PatientMatchedByCin", [existing.RecordNumber, existing.LastName + " " + existing.FirstName]);
            }
            else if (birth is not null)
            {
                var key = PatientSearchKey.Build(lastName!, firstName!);
                var byName = _context.Patients.Where(p => p.DateOfBirth == birth
                                                          && PatientSearchKey.Build(p.LastName, p.FirstName) == key).ToList();
                if (byName.Count > 1)
                {
                    Fail(row, MigrationSchema.LastName, "Migration_SeveralPatientsMatchName",
                         [string.Join(", ", byName.Select(p => p.RecordNumber))], "Migration_SuggestMergeFirst");
                    return;
                }

                if (byName.Count == 1)
                {
                    if (!_request.LinkByNameAndBirthDate)
                    {
                        row.Outcome = MigrationOutcome.Skip;
                        Warn(row, MigrationSchema.LastName, "Migration_PatientMatchedByNameNotLinked", [byName[0].RecordNumber],
                             "Migration_SuggestLinkByName");
                        return;
                    }

                    existing = byName[0];
                    Warn(row, MigrationSchema.LastName, "Migration_PatientMatchedByName", [existing.RecordNumber],
                         "Migration_SuggestConfirmMatch");
                }
            }

            row.ExistingId = existing?.Id;
        }

        if (row.ExistingId is null) { row.Outcome = MigrationOutcome.Create; return; }

        if (existing is null)
        {
            // Known key whose patient is gone (merged, deleted): nothing to update.
            row.Outcome = MigrationOutcome.Skip;
            Note(row, null, MigrationSeverity.Warning, "Migration_RecordGone", []);
            return;
        }

        var fills = Blank(existing.Cin) && cin is not null
                    || Blank(existing.MobilePhone) && mobile is not null
                    || Blank(existing.AlternatePhone) && phone is not null
                    || Blank(existing.Email) && email is not null
                    || Blank(existing.AddressLine) && row.Text(MigrationSchema.Address) is not null
                    || Blank(existing.City) && row.Text(MigrationSchema.City) is not null
                    || Blank(existing.LastNameArabic) && row.Text(MigrationSchema.LastNameArabic) is not null
                    || Blank(existing.FirstNameArabic) && row.Text(MigrationSchema.FirstNameArabic) is not null
                    || Blank(existing.GuardianFullName) && guardian is not null
                    || existing.DateOfBirth is null && birth is not null
                    || existing.Gender == Gender.Unknown && gender != Gender.Unknown
                    || payerId is { } p && !existing.Coverages.Any(c => c.PayerId == p);

        // A known patient whose old number is not linked yet still needs its link.
        var newLink = !row.KnownSourceKey;

        row.Outcome = fills || newLink ? MigrationOutcome.Update : MigrationOutcome.Skip;
    }

    // ---- Prescribers ------------------------------------------------------------------------------

    private void PlanPrescriber(int i, PlannedRow row, Dictionary<(MigrationCodeList, string), int> unmapped)
    {
        var lastName = OptionalText(i, row, MigrationSchema.LastName);
        var institution = OptionalText(i, row, MigrationSchema.Institution);
        if (lastName is null && institution is null)
        {
            Fail(row, MigrationSchema.LastName, "Migration_PrescriberNameRequired", []);
            return;
        }

        var firstName = OptionalText(i, row, MigrationSchema.FirstName) ?? string.Empty;
        var kindText = Coded(i, row, MigrationSchema.Specialty, MigrationCodeList.Specialty, unmapped);
        var kind = kindText is not null && Enum.TryParse<PrescriberKind>(kindText, out var parsed) ? parsed
                   : lastName is null ? PrescriberKind.Institution : PrescriberKind.GeneralPractitioner;

        if (kind == PrescriberKind.Institution && institution is null) institution = lastName;

        string? inpe = null;
        if (OptionalText(i, row, MigrationSchema.Inpe) is { } rawInpe)
        {
            if (MoroccanIdentifiers.IsValidInpe(rawInpe)) inpe = rawInpe.Trim();
            else Warn(row, MigrationSchema.Inpe, "Migration_InpeInvalid", [rawInpe]);
        }

        var email = OptionalText(i, row, MigrationSchema.Email);
        if (email is not null && !email.Contains('@', StringComparison.Ordinal))
        {
            Warn(row, MigrationSchema.Email, "Migration_EmailInvalid", [email]);
            email = null;
        }

        row.Values[MigrationSchema.LastName] = lastName ?? institution;
        row.Values[MigrationSchema.FirstName] = firstName;
        row.Values["Kind"] = kind;
        row.Values[MigrationSchema.Specialty] = Raw(i, MigrationSchema.Specialty) is { Length: > 0 } s ? Truncate(s, 150) : null;
        row.Values[MigrationSchema.Inpe] = inpe;
        row.Values[MigrationSchema.OrderNumber] = OptionalText(i, row, MigrationSchema.OrderNumber);
        row.Values[MigrationSchema.Phone] = OptionalText(i, row, MigrationSchema.Phone);
        row.Values[MigrationSchema.Mobile] = Phone(i, row, MigrationSchema.Mobile);
        row.Values[MigrationSchema.Email] = email;
        row.Values[MigrationSchema.Address] = OptionalText(i, row, MigrationSchema.Address);
        row.Values[MigrationSchema.City] = OptionalText(i, row, MigrationSchema.City);
        row.Values[MigrationSchema.Institution] = institution;

        var nameKey = MigrationSchema.Normalize((lastName ?? institution) + " " + firstName);
        var identity = OptionalText(i, row, MigrationSchema.SourceId) ?? (inpe is null ? null : "INPE:" + inpe) ?? "NOM:" + nameKey;
        row.SourceKey = KeyOf(_request.SourceSystem, DataMigrationKind.Prescribers, identity);

        PrescriberSnapshot? existing = null;
        if (_context.Records.TryGetValue(row.SourceKey, out var known))
        {
            row.KnownSourceKey = true;
            row.ExistingId = known;
            existing = _context.Prescribers.FirstOrDefault(p => p.Id == known);
        }
        else
        {
            var matches = inpe is not null ? _context.Prescribers.Where(p => p.Inpe == inpe).ToList() : [];
            if (matches.Count == 0)
                matches = [.. _context.Prescribers.Where(p => MigrationSchema.Normalize(p.LastName + " " + p.FirstName) == nameKey)];

            if (matches.Count > 1)
            {
                Fail(row, MigrationSchema.LastName, "Migration_SeveralPrescribersMatch", [matches.Count]);
                return;
            }

            if (matches.Count == 1)
            {
                existing = matches[0];
                row.ExistingId = existing.Id;
                Note(row, MigrationSchema.LastName, MigrationSeverity.Info, "Migration_PrescriberMatched", [existing.LastName + " " + existing.FirstName]);
            }
        }

        if (row.ExistingId is null) { row.Outcome = MigrationOutcome.Create; return; }
        if (existing is null) { row.Outcome = MigrationOutcome.Skip; Note(row, null, MigrationSeverity.Warning, "Migration_RecordGone", []); return; }

        var fills = Blank(existing.Inpe) && inpe is not null
                    || Blank(existing.ProfessionalOrderNumber) && row.Text(MigrationSchema.OrderNumber) is not null
                    || Blank(existing.Phone) && row.Text(MigrationSchema.Phone) is not null
                    || Blank(existing.MobilePhone) && row.Text(MigrationSchema.Mobile) is not null
                    || Blank(existing.Email) && email is not null
                    || Blank(existing.AddressLine) && row.Text(MigrationSchema.Address) is not null
                    || Blank(existing.City) && row.Text(MigrationSchema.City) is not null
                    || Blank(existing.Speciality) && row.Text(MigrationSchema.Specialty) is not null;

        row.Outcome = fills || !row.KnownSourceKey ? MigrationOutcome.Update : MigrationOutcome.Skip;
    }

    // ---- Catalogue --------------------------------------------------------------------------------

    private void PlanCatalogue(int i, PlannedRow row, Dictionary<(MigrationCodeList, string), int> unmapped)
    {
        var code = RequiredText(i, row, MigrationSchema.AnalysisCode)?.ToUpperInvariant();
        if (code is null) return;

        var name = OptionalText(i, row, MigrationSchema.AnalysisName);
        var price = OptionalDecimal(i, row, MigrationSchema.Price);
        var coefficient = OptionalDecimal(i, row, MigrationSchema.Coefficient);
        if (price is < 0) { Warn(row, MigrationSchema.Price, "Migration_NegativeAmount", [price]); price = null; }
        if (coefficient is < 0) { Warn(row, MigrationSchema.Coefficient, "Migration_NegativeAmount", [coefficient]); coefficient = null; }

        var letter = OptionalText(i, row, MigrationSchema.LetterKey)?.ToUpperInvariant() ?? NationalBiologyAct.BiologyLetterKey;

        if (price is null && coefficient is { } c && _context.LetterKeyValue is { } b && letter == NationalBiologyAct.BiologyLetterKey)
        {
            price = Math.Round(c * b, 2);
            Note(row, MigrationSchema.Price, MigrationSeverity.Info, "Migration_PriceFromCoefficient", [c, b, price]);
        }

        string? act = null;
        if (OptionalText(i, row, MigrationSchema.ActCode) is { } rawAct)
        {
            var normal = NationalBiologyAct.Normalize(rawAct);
            if (normal is not null && _context.NationalActs.Contains(normal)) act = normal;
            else Warn(row, MigrationSchema.ActCode, "Migration_ActUnknown", [rawAct], "Migration_SuggestActNomenclature");
        }

        var department = Coded(i, row, MigrationSchema.Department, MigrationCodeList.Department, unmapped);
        var specimen = Coded(i, row, MigrationSchema.Specimen, MigrationCodeList.Specimen, unmapped);
        var unit = Coded(i, row, MigrationSchema.Unit, MigrationCodeList.Unit, unmapped);

        row.Values[MigrationSchema.AnalysisCode] = code;
        row.Values[MigrationSchema.AnalysisName] = name;
        row.Values[MigrationSchema.Price] = price;
        row.Values[MigrationSchema.Coefficient] = coefficient;
        row.Values[MigrationSchema.LetterKey] = letter;
        row.Values[MigrationSchema.ActCode] = act;
        row.Values["LetterKeyValue"] = _context.LetterKeyValue;
        row.SourceKey = KeyOf(_request.SourceSystem, DataMigrationKind.Catalogue, code);

        var existing = _context.Records.TryGetValue(row.SourceKey, out var known)
            ? _context.Analyses.FirstOrDefault(a => a.Id == known)
            : _context.Analyses.FirstOrDefault(a => string.Equals(a.Code, code, StringComparison.OrdinalIgnoreCase));
        row.KnownSourceKey = _context.Records.ContainsKey(row.SourceKey);

        if (existing is null)
        {
            if (name is null)
            {
                Fail(row, MigrationSchema.AnalysisName, "Migration_AnalysisNameRequired", [code]);
                return;
            }

            var departmentCode = department is null or CorrespondenceTables.Ignore ? _context.DefaultDepartmentCode : department;
            var specimenCode = specimen is null or CorrespondenceTables.Ignore ? _context.DefaultSpecimenCode : specimen;
            if (departmentCode is null || specimenCode is null)
            {
                Fail(row, MigrationSchema.Department, "Migration_NoDepartmentOrSpecimen", []);
                return;
            }

            if (department is null) Note(row, MigrationSchema.Department, MigrationSeverity.Info, "Migration_DefaultDepartment", [departmentCode]);
            if (specimen is null) Note(row, MigrationSchema.Specimen, MigrationSeverity.Info, "Migration_DefaultSpecimen", [specimenCode]);
            if (price is null) Warn(row, MigrationSchema.Price, "Migration_NoPrice", [code]);

            row.Values[MigrationSchema.Department] = departmentCode;
            row.Values[MigrationSchema.Specimen] = specimenCode;
            row.Values[MigrationSchema.Unit] = unit is CorrespondenceTables.Ignore or CorrespondenceTables.KeepAsIs ? null : unit;
            row.Outcome = MigrationOutcome.Create;
            return;
        }

        row.ExistingId = existing.Id;
        Note(row, MigrationSchema.AnalysisCode, MigrationSeverity.Info, "Migration_AnalysisExists", [existing.Code, existing.Name]);

        var newPrice = price is { } p && (existing.Price is null || existing.Price != p
                                          || (coefficient is { } k && existing.Coefficient != k));
        var fillAct = act is not null && Blank(existing.NationalActCode);
        if (act is not null && !Blank(existing.NationalActCode) && existing.NationalActCode != act)
            Warn(row, MigrationSchema.ActCode, "Migration_ActDiffers", [existing.NationalActCode, act]);

        row.Values["NewTariff"] = newPrice;
        row.Values["FillAct"] = fillAct;
        row.Outcome = newPrice || fillAct || !row.KnownSourceKey ? MigrationOutcome.Update : MigrationOutcome.Skip;
    }

    // ---- Antériorités -----------------------------------------------------------------------------

    private void PlanHistory(int i, PlannedRow row, Dictionary<(MigrationCodeList, string), int> unmapped)
    {
        var patientId = ResolvePatient(i, row, required: true);
        var at = RequiredDateTime(i, row, MigrationSchema.ResultDate);
        var value = RequiredText(i, row, MigrationSchema.Value);
        var parameter = Coded(i, row, MigrationSchema.AnalysisCode, MigrationCodeList.Analysis, unmapped);
        var rawCode = Raw(i, MigrationSchema.AnalysisCode)?.Trim();

        if (row.Outcome == MigrationOutcome.Error) return;
        if (parameter == CorrespondenceTables.Ignore)
        {
            row.Outcome = MigrationOutcome.Skip;
            Note(row, MigrationSchema.AnalysisCode, MigrationSeverity.Info, "Migration_IgnoredByCorrespondence", [rawCode]);
            return;
        }

        if (at is { } when && when.Date > _context.Today.ToDateTime(TimeOnly.MinValue))
        {
            Fail(row, MigrationSchema.ResultDate, "Migration_DateInFuture", [Raw(i, MigrationSchema.ResultDate)]);
            return;
        }

        var unitTarget = Coded(i, row, MigrationSchema.Unit, MigrationCodeList.Unit, unmapped);
        var rawUnit = OptionalText(i, row, MigrationSchema.Unit);
        var unit = unitTarget is null or CorrespondenceTables.KeepAsIs or CorrespondenceTables.Ignore
            ? rawUnit
            : _context.Targets.GetValueOrDefault(MigrationCodeList.Unit)?.FirstOrDefault(t => t.Value == unitTarget)?.Label ?? rawUnit;

        var parsed = ValueParsers.ParseResult(value);
        if (parsed is not null && ValueParsers.IsAmbiguousThousands(value))
            Warn(row, MigrationSchema.Value, "Migration_NumberAmbiguous", [Label(MigrationSchema.Value), value]);
        var oldAccession = OptionalText(i, row, MigrationSchema.OldAccession);
        var range = OptionalText(i, row, MigrationSchema.ReferenceRange);

        row.Values["PatientId"] = patientId;
        row.Values["Parameter"] = parameter;
        row.Values[MigrationSchema.ResultDate] = at;
        row.Values[MigrationSchema.Value] = value;
        row.Values["Numeric"] = parsed?.Value;
        row.Values["Operator"] = parsed?.Operator;
        row.Values[MigrationSchema.Unit] = unit is null ? null : Truncate(unit, HistoricalResult.UnitMaxLength);
        row.Values[MigrationSchema.ReferenceRange] = range;
        row.Values[MigrationSchema.OldAccession] = oldAccession;
        row.Values["SourceCode"] = rawCode is null ? null : Truncate(rawCode, HistoricalResult.CodeMaxLength);

        if (row.Outcome == MigrationOutcome.Error || patientId is null || at is null || parameter is null) return;

        var identity = OptionalText(i, row, MigrationSchema.SourceId)
                       ?? $"{PatientRef(i)}|{at.Value:yyyyMMddHHmm}|{MigrationSchema.Normalize(rawCode)}|{oldAccession}";
        row.SourceKey = KeyOf(_request.SourceSystem, DataMigrationKind.History, identity);

        if (_context.Records.TryGetValue(row.SourceKey, out var known))
        {
            row.KnownSourceKey = true;
            row.ExistingId = known;
            var current = _context.History.GetValueOrDefault(known);
            row.Outcome = current is not null
                          && current.ValueText == value && current.Unit == row.Text(MigrationSchema.Unit)
                          && current.ReferenceRange == range && current.OldAccessionNumber == oldAccession
                          && current.Date == AsInstant(at.Value)
                ? MigrationOutcome.Skip
                : MigrationOutcome.Update;
            return;
        }

        row.Outcome = MigrationOutcome.Create;
    }

    /// <summary>A history date at the time the source gave, or noon when it gave only a day.</summary>
    public static DateTimeOffset AsInstant(DateTime at)
    {
        var local = at.TimeOfDay == TimeSpan.Zero ? at.Date.AddHours(12) : at;
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified),
                                  TimeZoneInfo.Local.GetUtcOffset(local));
    }

    // ---- Créances ouvertes ------------------------------------------------------------------------

    private void PlanBalance(int i, PlannedRow row, Dictionary<(MigrationCodeList, string), int> unmapped)
    {
        var patientId = ResolvePatient(i, row, required: false);
        var payerCode = Coded(i, row, MigrationSchema.PayerCode, MigrationCodeList.Payer, unmapped);
        Guid? payerId = null;
        if (payerCode is not null && payerCode != CorrespondenceTables.Ignore && _context.PayerIds.TryGetValue(payerCode, out var pid))
            payerId = pid;

        var amount = RequiredDecimal(i, row, MigrationSchema.Amount);
        var asOf = OptionalDate(i, row, MigrationSchema.AsOf) ?? _context.Today;
        var invoiceNumber = OptionalText(i, row, MigrationSchema.OldInvoiceNumber);
        var invoiceDate = OptionalDate(i, row, MigrationSchema.OldInvoiceDate);

        if (row.Outcome == MigrationOutcome.Error) return;

        if (patientId is null && payerId is null && payerCode is null)
        {
            Fail(row, MigrationSchema.PatientSourceId, "Migration_BalanceDebtorRequired", []);
            return;
        }

        if (amount is < 0)
        {
            Fail(row, MigrationSchema.Amount, "Migration_NegativeBalance", [amount], "Migration_SuggestCreditNote");
            return;
        }

        if (amount == 0)
        {
            row.Outcome = MigrationOutcome.Skip;
            Note(row, MigrationSchema.Amount, MigrationSeverity.Info, "Migration_ZeroBalance", []);
            return;
        }

        if (asOf > _context.Today)
        {
            Fail(row, MigrationSchema.AsOf, "Migration_DateInFuture", [Raw(i, MigrationSchema.AsOf)]);
            return;
        }

        row.Values["PatientId"] = patientId;
        row.Values["PayerId"] = payerId;
        row.Values[MigrationSchema.Amount] = amount;
        row.Values[MigrationSchema.AsOf] = asOf;
        row.Values[MigrationSchema.OldInvoiceNumber] = invoiceNumber;
        row.Values[MigrationSchema.OldInvoiceDate] = invoiceDate;
        row.Values[MigrationSchema.Label] = OptionalText(i, row, MigrationSchema.Label);

        if (payerCode is not null && payerId is null) return; // waiting for its correspondence

        var identity = OptionalText(i, row, MigrationSchema.SourceId)
                       ?? (invoiceNumber is null ? null : $"F:{invoiceNumber}|{PatientRef(i)}|{payerCode}");
        if (identity is null)
        {
            Fail(row, MigrationSchema.OldInvoiceNumber, "Migration_BalanceIdRequired", [], "Migration_SuggestSourceId");
            return;
        }

        row.SourceKey = KeyOf(_request.SourceSystem, DataMigrationKind.OpeningBalances, identity);

        if (_context.Records.TryGetValue(row.SourceKey, out var known))
        {
            row.KnownSourceKey = true;
            row.ExistingId = known;
            var current = _context.Balances.GetValueOrDefault(known);
            row.Outcome = current is not null && current.Amount == amount && current.AsOf == asOf
                ? MigrationOutcome.Skip
                : MigrationOutcome.Update;
            return;
        }

        row.Outcome = MigrationOutcome.Create;
    }

    private string PatientRef(int i) =>
        Raw(i, MigrationSchema.PatientSourceId)?.Trim() is { Length: > 0 } id ? "P:" + id
        : Raw(i, MigrationSchema.PatientOldNumber)?.Trim() is { Length: > 0 } n ? "N:" + n
        : Raw(i, MigrationSchema.PatientCin)?.Trim() is { Length: > 0 } c ? "C:" + MoroccanIdentifiers.NormalizeCin(c)
        : string.Empty;

    /// <summary>The patient a history or balance row belongs to: by old id, old file number, or CIN.</summary>
    private Guid? ResolvePatient(int i, PlannedRow row, bool required)
    {
        var sourceId = Raw(i, MigrationSchema.PatientSourceId)?.Trim();
        var oldNumber = Raw(i, MigrationSchema.PatientOldNumber)?.Trim();
        var cin = Raw(i, MigrationSchema.PatientCin)?.Trim();

        if (string.IsNullOrEmpty(sourceId) && string.IsNullOrEmpty(oldNumber) && string.IsNullOrEmpty(cin))
        {
            if (required) Fail(row, MigrationSchema.PatientSourceId, "Migration_PatientReferenceRequired", []);
            return null;
        }

        if (!string.IsNullOrEmpty(sourceId)
            && _context.PatientRecords.TryGetValue(KeyOf(_request.SourceSystem, DataMigrationKind.Patients, sourceId), out var byId))
            return byId;

        if (!string.IsNullOrEmpty(oldNumber))
        {
            if (_context.OldNumbers.TryGetValue(MigrationSchema.Normalize(oldNumber), out var byNumber)) return byNumber;
            if (_context.PatientRecords.TryGetValue(KeyOf(_request.SourceSystem, DataMigrationKind.Patients, "N:" + oldNumber), out var byNumberKey))
                return byNumberKey;
        }

        if (!string.IsNullOrEmpty(cin))
        {
            var normal = MoroccanIdentifiers.NormalizeCin(cin);
            var matches = _context.Patients.Where(p => p.Cin == normal).ToList();
            if (matches.Count == 1) return matches[0].Id;
            if (matches.Count > 1)
            {
                Fail(row, MigrationSchema.PatientCin, "Migration_SeveralPatientsMatch", [normal, string.Join(", ", matches.Select(p => p.RecordNumber))]);
                return null;
            }
        }

        Fail(row, MigrationSchema.PatientSourceId, "Migration_PatientNotFound",
             [sourceId is { Length: > 0 } ? sourceId : oldNumber is { Length: > 0 } ? oldNumber : cin], "Migration_SuggestImportPatientsFirst");
        return null;
    }

    // ---- Reading values -----------------------------------------------------------------------------

    private string? Raw(int i, string field)
    {
        if (!_columnIndex.TryGetValue(field, out var index)) return null;
        var row = _request.Table.Rows[i];
        return index < row.Length ? row[index] : null;
    }

    private string? OptionalText(int i, PlannedRow row, string field)
    {
        var value = Raw(i, field)?.Trim();
        if (string.IsNullOrEmpty(value)) return null;

        var max = MigrationSchema.Field(_request.Kind, field)?.MaxLength ?? 0;
        if (max > 0 && value.Length > max)
        {
            if (field is MigrationSchema.SourceId or MigrationSchema.Cin or MigrationSchema.Inpe)
            {
                Fail(row, field, "Migration_TextTooLong", [Label(field), value.Length, max]);
                return null;
            }

            Warn(row, field, "Migration_TextTruncated", [Label(field), value.Length, max]);
            value = value[..max];
        }

        return value;
    }

    private string? RequiredText(int i, PlannedRow row, string field)
    {
        var value = Raw(i, field)?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            if (_columnIndex.ContainsKey(field)) Fail(row, field, "Migration_Required", [Label(field)]);
            else row.Outcome = MigrationOutcome.Error;
            return null;
        }

        var max = MigrationSchema.Field(_request.Kind, field)?.MaxLength ?? 0;
        if (max > 0 && value.Length > max)
        {
            Fail(row, field, "Migration_TextTooLong", [Label(field), value.Length, max]);
            return null;
        }

        return value;
    }

    private DateOnly? OptionalDate(int i, PlannedRow row, string field)
    {
        var raw = Raw(i, field);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var date = ValueParsers.ParseDate(raw, _context.Today);
        if (date is null) Warn(row, field, "Migration_DateUnreadable", [Label(field), raw], "Migration_SuggestDateFormat");
        return date;
    }

    private DateTime? RequiredDateTime(int i, PlannedRow row, string field)
    {
        var raw = Raw(i, field);
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (_columnIndex.ContainsKey(field)) Fail(row, field, "Migration_Required", [Label(field)]);
            else row.Outcome = MigrationOutcome.Error;
            return null;
        }

        var date = ValueParsers.ParseDateTime(raw, _context.Today);
        if (date is null) Fail(row, field, "Migration_DateUnreadable", [Label(field), raw], "Migration_SuggestDateFormat");
        else if (date.Value.Year < 1900) { Fail(row, field, "Migration_DateUnreadable", [Label(field), raw], "Migration_SuggestDateFormat"); return null; }
        return date;
    }

    private decimal? OptionalDecimal(int i, PlannedRow row, string field)
    {
        var raw = Raw(i, field);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = ValueParsers.ParseDecimal(raw);
        if (value is null) Warn(row, field, "Migration_NumberUnreadable", [Label(field), raw]);
        else if (Math.Abs(value.Value) >= 1_000_000_000_000m) { Warn(row, field, "Migration_NumberTooLarge", [Label(field), raw]); return null; }
        else if (ValueParsers.IsAmbiguousThousands(raw)) Warn(row, field, "Migration_NumberAmbiguous", [Label(field), raw]);
        return value;
    }

    private decimal? RequiredDecimal(int i, PlannedRow row, string field)
    {
        var raw = Raw(i, field);
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (_columnIndex.ContainsKey(field)) Fail(row, field, "Migration_Required", [Label(field)]);
            else row.Outcome = MigrationOutcome.Error;
            return null;
        }

        var value = ValueParsers.ParseDecimal(raw);
        if (value is null) Fail(row, field, "Migration_NumberUnreadable", [Label(field), raw]);
        else if (Math.Abs(value.Value) >= 1_000_000_000_000m) { Fail(row, field, "Migration_NumberTooLarge", [Label(field), raw]); return null; }
        else if (ValueParsers.IsAmbiguousThousands(raw)) Warn(row, field, "Migration_NumberAmbiguous", [Label(field), raw]);
        return value;
    }

    private int? OptionalInteger(int i, PlannedRow row, string field)
    {
        var raw = Raw(i, field);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = ValueParsers.ParseInteger(raw);
        if (value is null) Warn(row, field, "Migration_NumberUnreadable", [Label(field), raw]);
        return value;
    }

    private string? Phone(int i, PlannedRow row, string field)
    {
        var raw = OptionalText(i, row, field);
        if (raw is null) return null;
        var normal = MoroccanIdentifiers.TryNormalizePhone(raw);
        if (normal is null) Warn(row, field, "Migration_PhoneInvalid", [raw]);
        return normal;
    }

    /// <summary>
    /// The MarocLIS value of a coded cell: from the correspondence tables, else the suggestion.
    /// A value with neither is counted as unmapped (blocking), except a unit, kept as written.
    /// </summary>
    private string? Coded(int i, PlannedRow row, string field, MigrationCodeList list,
                          Dictionary<(MigrationCodeList, string), int> unmapped)
    {
        var raw = Raw(i, field)?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;

        var target = _request.Correspondences.TargetOf(list, raw)
                     ?? CorrespondenceSuggester.Suggest(list, raw, _context.Targets.GetValueOrDefault(list) ?? []);

        if (target is not null) return target;
        if (list == MigrationCodeList.Unit) return CorrespondenceTables.KeepAsIs;

        var key = (list, raw);
        var existing = unmapped.Keys.FirstOrDefault(k => k.Item1 == list && MigrationSchema.Normalize(k.Item2) == MigrationSchema.Normalize(raw));
        if (existing != default) unmapped[existing]++;
        else unmapped[key] = 1;

        Note(row, field, MigrationSeverity.Warning, "Migration_RowWaitsForCorrespondence", [raw]);
        return null;
    }

    // ---- Anomalies --------------------------------------------------------------------------------

    private void Add(int row, string? field, MigrationSeverity severity, string key, object?[] args,
                     string? suggestion = null, object?[]? suggestionArgs = null) =>
        _anomalies.Add(new MigrationAnomaly(row, field, severity, key, args, suggestion, suggestionArgs ?? []));

    private void Fail(PlannedRow row, string? field, string key, object?[] args, string? suggestion = null)
    {
        row.Outcome = MigrationOutcome.Error;
        Add(row.Row, field, MigrationSeverity.Error, key, args, suggestion);
    }

    private void Warn(PlannedRow row, string? field, string key, object?[] args, string? suggestion = null) =>
        Add(row.Row, field, MigrationSeverity.Warning, key, args, suggestion);

    private void Note(PlannedRow row, string? field, MigrationSeverity severity, string key, object?[] args) =>
        Add(row.Row, field, severity, key, args);

    /// <summary>A field named in a message: its resource key, which the screen shows in the operator's language.</summary>
    private static string Label(string field) => $"Migration.Field.{field}";

    private static string ListLabel(MigrationCodeList list) => $"MigrationCodeList.{list}";

    private string? FieldOf(MigrationCodeList list) =>
        MigrationSchema.Fields(_request.Kind).FirstOrDefault(f => f.CodeList == list)?.Key;

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;

    private static int AgeOn(DateOnly birth, DateOnly on)
    {
        var age = on.Year - birth.Year;
        if (on < birth.AddYears(age)) age--;
        return age;
    }
}
