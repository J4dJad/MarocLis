using MarocLIS.Application.Abstractions;
using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace MarocLIS.Tests.Web;

/// <summary>
/// The HR screens rendered in memory: PersonnelService, HrDocumentService and SettlementService over
/// fakes of their queries and repositories, and the staff files built from the domain itself, so
/// what the file shows and what a save changes is what the services really do.
/// </summary>
public abstract class WebHumanResourcesContext : WebScreenContext
{
    protected readonly Fake<IPersonnelQueries> PersonnelQueries = Fake<IPersonnelQueries>.Create();
    protected readonly Fake<IHrDocumentQueries> HrQueries = Fake<IHrDocumentQueries>.Create();
    protected readonly Fake<IPayrollQueries> PayrollQueries = Fake<IPayrollQueries>.Create();
    protected readonly Fake<IRepository<Employee>> EmployeeStore = Fake<IRepository<Employee>>.Create();
    protected readonly Fake<IDepartedAccounts> Accounts = Fake<IDepartedAccounts>.Create();
    protected readonly Fake<IUnitOfWork> Work = Fake<IUnitOfWork>.Create();

    /// <summary>The files the repository holds, by id.</summary>
    protected readonly Dictionary<Guid, Employee> Staff = [];

    protected WebHumanResourcesContext()
    {
        Services.AddSingleton(PersonnelQueries.Object);
        Services.AddSingleton(HrQueries.Object);
        Services.AddSingleton(PayrollQueries.Object);
        Services.AddSingleton(EmployeeStore.Object);
        Services.AddSingleton(Accounts.Object);
        Services.AddSingleton(Work.Object);
        Services.AddSingleton(Fake<IRepository<CredentialFile>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<FinalSettlement>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<LeaveLedgerEntry>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<LeaveRequest>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<PayrollPeriod>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<PayrollParameters>>.Create().Object);
        Services.AddSingleton(Fake<ISequenceAllocator>.Create().Object);
        Services.AddSingleton(Fake<IHrDocumentRenderer>.Create().Object);

        Services.AddScoped<PersonnelService>();
        Services.AddScoped<HrDocumentService>();
        Services.AddScoped<SettlementService>();

        EmployeeStore.On(nameof(IRepository<Employee>.GetByIdAsync), args => Staff.GetValueOrDefault((Guid)args[0]!))
                     .On(nameof(IRepository<Employee>.GetByIdWithDetailsAsync), args => Staff.GetValueOrDefault((Guid)args[0]!));
        PersonnelQueries.On(nameof(IPersonnelQueries.GetEmployeesAsync),
                            args => (IReadOnlyList<EmployeeRow>)[.. Staff.Values.Where(e => args[0] is true || OnStaff(e)).Select(RowOf)])
                        .On(nameof(IPersonnelQueries.GetLookupsAsync), _ => Lookups)
                        .On(nameof(IPersonnelQueries.GetMatrixAsync), _ => new HabilitationMatrix(
                            [.. Staff.Values.Where(OnStaff).Select(RowOf)],
                            [.. Staff.Values.SelectMany(e => e.Habilitations.Where(h => h.IsActive).Select(h => new HabilitationRow(
                                h.Id, e.Id, h.Activity, h.SubjectId, Lookups.Departments.Concat(Lookups.Instruments).FirstOrDefault(s => s.Id == h.SubjectId)?.Label,
                                h.Level, h.GrantedOn, "Dr Lahlou", h.ReviewDueOn, h.Evidence)))]));
    }

    protected static readonly Guid Haematology = Guid.NewGuid(), Cobas = Guid.NewGuid();

    /// <summary>The disciplines and analysers a habilitation names.</summary>
    protected static readonly PersonnelLookups Lookups = new(
        [new HabilitationSubject(Haematology, "Hématologie", false)],
        [new HabilitationSubject(Cobas, "Cobas c311", true)],
        []);

    /// <summary>The rights of HR: the files, their documents and the pay.</summary>
    protected static readonly string[] HrManager =
    [
        Permissions.HumanResources.View, Permissions.HumanResources.Manage, Permissions.HumanResources.Salary,
        Permissions.HumanResources.ManageHabilitations
    ];

    /// <summary>The files edited without the pay.</summary>
    protected static readonly string[] HrClerk = [Permissions.HumanResources.View, Permissions.HumanResources.Manage];

    protected Employee Hire(string number, string last, string first, EmployeeJob job, DateOnly hired,
                            ContractType contract = ContractType.Permanent, DateOnly? contractEnd = null)
    {
        var employee = new Employee(number, last, first, job, hired, Guid.NewGuid())
        {
            ContractType = contract, ContractEndDate = contractEnd, Cin = "BE902117", CnssNumber = "184 552 907",
            Address = "17, rue Ibnou Mounir, Maârif", City = "Casablanca", BaseSalary = 7400m, BankName = "Banque Populaire",
            JobTitle = "Technicien de laboratoire"
        };
        Staff[employee.Id] = employee;
        return employee;
    }

    /// <summary>On the staff today: no departure, or one not reached yet, as the queries keep them.</summary>
    protected bool OnStaff(Employee e) => e.ExitDate is null || e.ExitDate >= TestClock.Today;

    protected static EmployeeRow RowOf(Employee e) =>
        new(e.Id, e.Number, e.FullName, e.Job, e.JobTitle, null, e.ContractType, e.HireDate, e.ContractEndDate, e.Phone,
            null, e.ExitDate, e.ExitReason);
}
