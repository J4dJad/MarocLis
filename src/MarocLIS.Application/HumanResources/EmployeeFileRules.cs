using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Morocco;
using MarocLIS.Domain.Patients;

namespace MarocLIS.Application.HumanResources;

/// <summary>The parts of a personnel file: who, the post, the contract, the pay, and the documents.</summary>
public enum EmployeeFileTab
{
    Identity = 0,
    Position = 1,
    Contract = 2,
    Pay = 3,
    Documents = 4
}

/// <summary>A personnel file as typed. The pay is held as typed: an amount that is no number is refused on its box.</summary>
public sealed class EmployeeForm
{
    public string LastName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string? ArabicName { get; set; }
    public Gender Gender { get; set; } = Gender.Unknown;
    public DateOnly? BirthDate { get; set; }
    public string? Cin { get; set; }
    public string? Cnss { get; set; }
    public FamilyStatus FamilyStatus { get; set; } = FamilyStatus.Unmarried;
    public int Children { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? City { get; set; }
    public string? Address { get; set; }

    public EmployeeJob Job { get; set; } = EmployeeJob.Technician;
    public string? JobTitle { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? UserId { get; set; }
    public bool Exposed { get; set; } = Employee.IsExposedByDefault(EmployeeJob.Technician);
    public DateOnly? ConfidentialityClauseSignedOn { get; set; }

    public ContractType ContractType { get; set; } = ContractType.Permanent;
    public DateOnly HireDate { get; set; }
    public DateOnly? ContractEndDate { get; set; }
    public DateOnly? TrialPeriodEndDate { get; set; }
    public decimal WeeklyHours { get; set; } = EmployeeFileRules.DefaultWeeklyHours;

    public string? Salary { get; set; }
    public string? Rib { get; set; }
    public string? Bank { get; set; }
}

/// <summary>
/// The rules of the personnel file that need no window: which parts a user sees (the personal data
/// to whoever may edit the file, the pay to whoever holds the salary right too), the form opened on
/// a file or a new one, what the job and the contract type change in it, and the check before it is
/// sent — the CIN and the CNSS number in their form, the salary a number, the RIB with its key —
/// each refusal next to its box. The workstation's EmployeeEditorDialog and the web's file apply the
/// same ones; what is saved is PersonnelService's.
/// </summary>
public static class EmployeeFileRules
{
    public const string LastNameField = "employee-last-name";
    public const string FirstNameField = "employee-first-name";
    public const string ArabicNameField = "employee-arabic-name";
    public const string CinField = "employee-cin";
    public const string CnssField = "employee-cnss";
    public const string ChildrenField = "employee-children";
    public const string PhoneField = "employee-phone";
    public const string EmailField = "employee-email";
    public const string CityField = "employee-city";
    public const string AddressField = "employee-address";
    public const string JobField = "employee-job";
    public const string JobTitleField = "employee-job-title";
    public const string UserField = "employee-user";
    public const string HireDateField = "employee-hire-date";
    public const string ContractEndField = "employee-contract-end";
    public const string TrialEndField = "employee-trial-end";
    public const string WeeklyHoursField = "employee-weekly-hours";
    public const string SalaryField = "employee-salary";
    public const string RibField = "employee-rib";
    public const string BankField = "employee-bank";

    // The lengths of the columns: a longer value was refused by the database with an unexpected
    // error, and the form was lost with it.
    public const int NameMaxLength = 100;
    public const int ArabicNameMaxLength = 200;
    public const int CinMaxLength = 20;
    public const int CnssMaxLength = 20;
    public const int PhoneMaxLength = 30;
    public const int EmailMaxLength = 200;
    public const int CityMaxLength = 100;
    public const int AddressMaxLength = 300;
    public const int JobTitleMaxLength = 150;
    public const int RibMaxLength = 30;
    public const int BankMaxLength = 100;
    public const int SalaryMaxLength = TypedNumber.AmountMaxLength;

    public const int MaxChildren = 20;
    public const decimal MaxWeeklyHours = 60m;
    public const decimal DefaultWeeklyHours = 44m;

    /// <summary>The CNSS immatriculation as the BDS file declares it: nine digits at most.</summary>
    public const int CnssMaxDigits = 9;

    /// <summary>
    /// The parts <paramref name="has"/> may open: the identity, the post, the contract and the pay are
    /// the file's editor, the workstation's « Modifier le dossier », for whoever may edit files (the
    /// pay for whoever also holds the salary right); the documents for whoever may read the files.
    /// </summary>
    public static IReadOnlyList<EmployeeFileTab> Tabs(Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(has);
        var tabs = new List<EmployeeFileTab>(5);
        if (has(Permissions.HumanResources.Manage))
        {
            tabs.AddRange([EmployeeFileTab.Identity, EmployeeFileTab.Position, EmployeeFileTab.Contract]);
            if (has(Permissions.HumanResources.Salary)) tabs.Add(EmployeeFileTab.Pay);
        }
        if (has(Permissions.HumanResources.View)) tabs.Add(EmployeeFileTab.Documents);
        return tabs;
    }

    public static string TabKey(EmployeeFileTab tab) => tab switch
    {
        EmployeeFileTab.Position => "Personnel.Position",
        EmployeeFileTab.Contract => "Personnel.Contract",
        EmployeeFileTab.Pay => "Personnel.Pay",
        EmployeeFileTab.Documents => "Personnel.Documents",
        _ => "Personnel.Identity"
    };

    /// <summary>The part that holds <paramref name="field"/>, opened when a refusal names it.</summary>
    public static EmployeeFileTab TabOf(string field) => field switch
    {
        JobField or JobTitleField or UserField => EmployeeFileTab.Position,
        HireDateField or ContractEndField or TrialEndField or WeeklyHoursField => EmployeeFileTab.Contract,
        SalaryField or RibField or BankField => EmployeeFileTab.Pay,
        _ => EmployeeFileTab.Identity
    };

    /// <summary>The file's title: « Nouveau dossier », or « Modifier le dossier ».</summary>
    public static string TitleKey(bool isNew) => isNew ? "Personnel.New" : "Personnel.Edit";

    /// <summary>« 0042 BOUZID Hanane », under the title of a file opened.</summary>
    public static string? Subtitle(EmployeeDetail? detail) =>
        detail is null ? null : $"{detail.Number} {detail.Request.LastName} {detail.Request.FirstName}";

    /// <summary>The form of a file opened, or of a new one hired today and on a permanent contract.</summary>
    public static EmployeeForm FormFor(EmployeeDetail? detail, DateOnly today)
    {
        if (detail?.Request is not { } r)
            return new EmployeeForm { HireDate = today };

        return new EmployeeForm
        {
            LastName = r.LastName,
            FirstName = r.FirstName,
            ArabicName = r.FullNameArabic,
            Gender = r.Gender,
            BirthDate = r.BirthDate,
            Cin = r.Cin,
            Cnss = r.CnssNumber,
            FamilyStatus = r.FamilyStatus,
            Children = r.DependentChildren,
            Phone = r.Phone,
            Email = r.Email,
            City = r.City,
            Address = r.Address,
            Job = r.Job,
            JobTitle = r.JobTitle,
            DepartmentId = r.DepartmentId,
            UserId = r.UserId,
            Exposed = r.IsExposedToBiologicalRisk,
            ConfidentialityClauseSignedOn = r.ConfidentialityClauseSignedOn,
            ContractType = r.ContractType,
            HireDate = r.HireDate == default ? today : r.HireDate,
            ContractEndDate = r.ContractEndDate,
            TrialPeriodEndDate = r.TrialPeriodEndDate,
            WeeklyHours = r.WeeklyHours,
            Salary = r.BaseSalary?.ToString("0.00", CultureInfo.InvariantCulture),
            Rib = r.Rib,
            Bank = r.BankName
        };
    }

    /// <summary>
    /// A new file follows the job: picking « agent d'entretien » ticks the exposure box. An existing
    /// file keeps what HR decided.
    /// </summary>
    public static void JobChanged(EmployeeForm form, bool isNew)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (isNew) form.Exposed = Employee.IsExposedByDefault(form.Job);
    }

    /// <summary>The contract types that end on a date (CDD, ANAPEC, Tahfiz, stage) say so beside the end, and get one.</summary>
    public static bool ShowsEndNotice(ContractType type) => Employee.RequiresEndDate(type);

    /// <summary>A contract type that must end on a date gets one: the day it is chosen, to be corrected.</summary>
    public static void ContractChanged(EmployeeForm form, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (Employee.RequiresEndDate(form.ContractType)) form.ContractEndDate ??= today;
    }

    /// <summary>
    /// The form checked before it is sent: the name, the CIN (one or two letters and five or six
    /// digits), the CNSS number (digits, nine at most), the salary a number and the RIB its 24
    /// digits with a right key. The salary and the bank details travel only with the salary right.
    /// </summary>
    public static (FieldRefusal? Refusal, EmployeeRequest? Request) Check(EmployeeForm form, Guid? employeeId, bool canSeeSalary)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (string.IsNullOrWhiteSpace(form.LastName))
            return (new FieldRefusal(LastNameField, "Employee_NameRequired"), null);
        if (string.IsNullOrWhiteSpace(form.FirstName))
            return (new FieldRefusal(FirstNameField, "Employee_NameRequired"), null);

        if (!string.IsNullOrWhiteSpace(form.Cin) && !MoroccanIdentifiers.IsValidCin(form.Cin))
            return (new FieldRefusal(CinField, "Employee_CinInvalid", form.Cin.Trim()), null);

        if (!string.IsNullOrWhiteSpace(form.Cnss) && !IsCnssNumber(form.Cnss))
            return (new FieldRefusal(CnssField, "Employee_CnssInvalid", form.Cnss.Trim()), null);

        if (form.Children is < 0 or > MaxChildren)
            return (new FieldRefusal(ChildrenField, "Employee_ChildrenInvalid"), null);

        if (form.WeeklyHours is <= 0m or > MaxWeeklyHours)
            return (new FieldRefusal(WeeklyHoursField, "Employee_WeeklyHoursInvalid"), null);

        decimal? salary = null;
        if (canSeeSalary && !string.IsNullOrWhiteSpace(form.Salary))
        {
            salary = TypedNumber.Parse(form.Salary);
            if (salary is null or < 0m) return (new FieldRefusal(SalaryField, "Employee_SalaryInvalid"), null);
        }

        if (canSeeSalary && !string.IsNullOrWhiteSpace(form.Rib) && !Employee.IsValidRib(form.Rib))
            return (new FieldRefusal(RibField, "Employee_RibInvalid"), null);

        return (null, new EmployeeRequest
        {
            EmployeeId = employeeId,
            LastName = form.LastName.Trim(),
            FirstName = form.FirstName.Trim(),
            FullNameArabic = Blank(form.ArabicName),
            Gender = form.Gender,
            BirthDate = form.BirthDate,
            Cin = Blank(form.Cin),
            CnssNumber = Blank(form.Cnss),
            FamilyStatus = form.FamilyStatus,
            DependentChildren = form.Children,
            Address = Blank(form.Address),
            City = Blank(form.City),
            Phone = Blank(form.Phone),
            Email = Blank(form.Email),
            Job = form.Job,
            JobTitle = Blank(form.JobTitle),
            DepartmentId = form.DepartmentId,
            UserId = form.UserId,
            IsExposedToBiologicalRisk = form.Exposed,
            ConfidentialityClauseSignedOn = form.ConfidentialityClauseSignedOn,
            ContractType = form.ContractType,
            HireDate = form.HireDate,
            ContractEndDate = form.ContractEndDate,
            TrialPeriodEndDate = form.TrialPeriodEndDate,
            WeeklyHours = form.WeeklyHours,
            BaseSalary = canSeeSalary ? salary : null,
            Rib = canSeeSalary ? Blank(form.Rib) : null,
            BankName = canSeeSalary ? Blank(form.Bank) : null
        });
    }

    /// <summary>
    /// Digits only, nine at most — what the CNSS's BDS file takes — written as the card shows them,
    /// with spaces, dashes or dots between them (« 123 456 789 », « 123-456-789 »).
    /// </summary>
    public static bool IsCnssNumber(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var digits = CnssBdsFile.Digits(value).Length;
        return digits is > 0 and <= CnssMaxDigits
               && value.All(c => char.IsAsciiDigit(c) || char.IsWhiteSpace(c) || c is '-' or '.');
    }

    /// <summary>The box a refusal of the service names, when it names one: it is said beside it, on its tab.</summary>
    public static string? FieldOf(string errorKey) => errorKey switch
    {
        "Employee_NameRequired" => LastNameField,
        "Employee_CinAlreadyUsed" or "Employee_CinInvalid" => CinField,
        "Employee_CnssInvalid" => CnssField,
        "Employee_ChildrenInvalid" => ChildrenField,
        "Employee_UserAlreadyLinked" => UserField,
        "Employee_ContractEndRequired" or "Employee_ContractEndBeforeHire" => ContractEndField,
        "Employee_TrialBeforeHire" => TrialEndField,
        "Employee_WeeklyHoursInvalid" => WeeklyHoursField,
        "Employee_SalaryInvalid" => SalaryField,
        "Employee_RibInvalid" => RibField,
        _ => null
    };

    /// <summary>A refusal of the service put beside its box, a date it names written as the screens write dates.</summary>
    public static FieldRefusal? RefusalOf(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return FieldOf(error.ResourceKey) is { } field ? HrRefusals.Of(field, error) : null;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The refusals of the HR screens put beside their boxes.</summary>
public static class HrRefusals
{
    /// <summary>As <see cref="FieldRefusal.Of"/>, a date the message names written dd/MM/yyyy whatever the server's culture.</summary>
    public static FieldRefusal Of(string field, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Args.FirstOrDefault() switch
        {
            DateOnly date => new FieldRefusal(field, error.ResourceKey, PersonnelScreenRules.Date(date)),
            _ => FieldRefusal.Of(field, error)
        };
    }
}
