using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Results;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using Microsoft.Extensions.DependencyInjection;

namespace MarocLIS.Tests.Web;

/// <summary>
/// The sample and result screens rendered in memory: the workstation's services over fakes of
/// their queries and repositories, and one patient's file built from the domain itself, so the
/// entry sheet the screens show is the one ResultEntryService really builds.
/// </summary>
public abstract class WebBenchContext : WebScreenContext
{
    protected readonly Fake<ISampleQueries> SampleQueries = Fake<ISampleQueries>.Create();
    protected readonly Fake<IRecollectionQueries> RecallQueries = Fake<IRecollectionQueries>.Create();
    protected readonly Fake<IResultQueries> ResultQueries = Fake<IResultQueries>.Create();
    protected readonly Fake<IValidationQueries> ValidationQueries = Fake<IValidationQueries>.Create();
    protected readonly Fake<IBenchWorksheetQueries> BenchQueries = Fake<IBenchWorksheetQueries>.Create();
    protected readonly Fake<IResultEntryContextQueries> ContextQueries = Fake<IResultEntryContextQueries>.Create();
    protected readonly Fake<IRepository<LabOrder>> OrderStore = Fake<IRepository<LabOrder>>.Create();
    protected readonly Fake<IRepository<AnalysisTest>> AnalysisStore = Fake<IRepository<AnalysisTest>>.Create();
    protected readonly Fake<IRepository<Patient>> PatientStore = Fake<IRepository<Patient>>.Create();
    protected readonly Fake<IRepository<Sample>> SampleStore = Fake<IRepository<Sample>>.Create();
    protected readonly Fake<IRepository<CriticalResultNotification>> NotificationStore =
        Fake<IRepository<CriticalResultNotification>>.Create();
    protected readonly Fake<IUnitOfWork> Work = Fake<IUnitOfWork>.Create();
    protected readonly Fake<IMessageSender> Messages = Fake<IMessageSender>.Create();
    protected readonly Fake<IBenchWorksheetRenderer> BenchRenderer = Fake<IBenchWorksheetRenderer>.Create();

    protected WebBenchContext()
    {
        Services.AddSingleton(SampleQueries.Object);
        Services.AddSingleton(RecallQueries.Object);
        Services.AddSingleton(ResultQueries.Object);
        Services.AddSingleton(ValidationQueries.Object);
        Services.AddSingleton(BenchQueries.Object);
        Services.AddSingleton(ContextQueries.Object);
        Services.AddSingleton(OrderStore.Object);
        Services.AddSingleton(AnalysisStore.Object);
        Services.AddSingleton(PatientStore.Object);
        Services.AddSingleton(SampleStore.Object);
        Services.AddSingleton(NotificationStore.Object);
        Services.AddSingleton(Fake<IRepository<TestResult>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<LabSite>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<Instrument>>.Create().Object);
        Services.AddSingleton(Fake<ISequenceAllocator>.Create().Object);
        Services.AddSingleton(Work.Object);
        Services.AddSingleton(Messages.Object);
        Services.AddSingleton(BenchRenderer.Object);
        Services.AddSingleton(new OrderRegistrationOptions());

        Services.AddScoped<SampleWorkflowService>();
        Services.AddScoped<RecollectionService>();
        Services.AddScoped<ResultEntryService>();
        Services.AddScoped<ResultEntryContextService>();
        Services.AddScoped<BenchWorksheetService>();
        Services.AddScoped<ValidationService>();

        // « Étiquettes (PDF) »: the workstation's label renderer, behind the service that checks the right.
        Services.AddSingleton<MarocLIS.Reporting.ISampleLabelRenderer>(new MarocLIS.Reporting.SampleLabelRenderer(new MarocLIS.Reporting.LabelOptions()));
        Services.AddScoped<MarocLIS.Web.Printing.LabelPdfService>();
    }

    /// <summary>The rights of a technician at the bench.</summary>
    protected static readonly string[] Technician =
    [
        Permissions.Results.View, Permissions.Results.Enter, Permissions.Results.TechnicalValidate
    ];

    /// <summary>The rights of a biologist validating.</summary>
    protected static readonly string[] Biologist =
    [
        Permissions.Results.View, Permissions.Results.BiologicalValidate, Permissions.Results.EnterCritical,
        Permissions.Results.Unvalidate, Permissions.Reports.Print, Permissions.Orders.View
    ];

    /// <summary>Answers the entry service's reads from <paramref name="file"/>.</summary>
    protected void Serve(BenchFile file)
    {
        OrderStore.On(nameof(IRepository<LabOrder>.GetByIdWithDetailsAsync), _ => file.Order)
                  .On(nameof(IRepository<LabOrder>.GetByIdAsync), _ => file.Order);
        AnalysisStore.On(nameof(IRepository<AnalysisTest>.GetByIdWithDetailsAsync),
                         args => file.Analyses.GetValueOrDefault((Guid)args[0]!));
        PatientStore.On(nameof(IRepository<Patient>.GetByIdAsync), _ => file.Patient);
        ResultQueries.On(nameof(IResultQueries.GetCurrentResultsForOrderAsync), _ => (IReadOnlyList<TestResult>)[.. file.Results])
                     .On(nameof(IResultQueries.GetResultsForOrderAsync), _ => (IReadOnlyList<TestResult>)[.. file.Results])
                     .On(nameof(IResultQueries.ResolveOrderIdAsync),
                         args => (string)args[0]! == file.Order.AccessionNumber ? file.Order.Id : (Guid?)null);
        ContextQueries.On(nameof(IResultEntryContextQueries.GetOriginsAsync),
                          _ => (IReadOnlyList<ResultEntryOrigin>)
                              [.. file.Results.Select(r => new ResultEntryOrigin(r.Id, "BIO", new MultilingualText("Biochimie"), null, null))]);
    }

    /// <summary>The entry worklist: the dossiers the bench sheet's query says are pending.</summary>
    protected void Pending(params BenchFile[] files) =>
        BenchQueries.On(nameof(IBenchWorksheetQueries.GetPendingWorkAsync), _ =>
            ((IReadOnlyList<BenchWorksheetLine>)[.. files.Select(f => f.PendingLine())], false));
}

/// <summary>One patient's file, built from the domain: analyses with their parameters and results.</summary>
public sealed class BenchFile
{
    public static readonly DateTimeOffset Now = new(2026, 10, 18, 8, 30, 0, TimeSpan.FromHours(1));

    public Patient Patient { get; }
    public LabOrder Order { get; }
    public Dictionary<Guid, AnalysisTest> Analyses { get; } = [];
    public List<TestResult> Results { get; } = [];
    public Dictionary<string, TestParameter> Parameters { get; } = new(StringComparer.Ordinal);

    private readonly Dictionary<string, ReferenceRange> _ranges = new(StringComparer.Ordinal);

    public BenchFile(string accession = "2610-000021", string last = "EL IDRISSI", string first = "Khadija",
                     OrderPriority priority = OrderPriority.Routine)
    {
        Patient = new Patient("P-2026-000210", last, first, new DateOnly(1979, 3, 2), Gender.Female, Guid.NewGuid());
        Order = new LabOrder(accession, Patient.Id, Guid.NewGuid(), Now.AddHours(-2), Guid.NewGuid()) { Priority = priority };
    }

    /// <summary>A received analysis, a numeric parameter for each (code, low, high), an awaited result each.</summary>
    public BenchFile Analysis(string code, string name, params (string Code, string Name, decimal Low, decimal High)[] parameters)
    {
        var analysis = new AnalysisTest(code, new MultilingualText(name), Guid.NewGuid(), Guid.NewGuid());
        var item = new LabOrderItem(analysis.Id, Order.Priority);
        Order.AddItem(item);
        item.MarkCollected(Now.AddHours(-1));
        item.MarkReceived(Now.AddMinutes(-40), null);

        foreach (var (pCode, pName, low, high) in parameters)
        {
            var parameter = new TestParameter(pCode, new MultilingualText(pName), ResultValueType.Numeric) { Decimals = 2 };
            var range = ReferenceRange.Numeric(low, high);
            parameter.AddReferenceRange(range);
            analysis.AddParameter(parameter);
            Parameters[pCode] = parameter;
            _ranges[pCode] = range;
            Results.Add(new TestResult(Order.Id, item.Id, parameter.Id, Order.SiteId));
        }

        Analyses[analysis.Id] = analysis;
        return this;
    }

    public TestResult Result(string code) => Results.Single(r => r.TestParameterId == Parameters[code].Id);

    /// <summary>A value entered and interpreted, as the bench would have stored it.</summary>
    public BenchFile Entered(string code, decimal value, bool technicallyValidated = false)
    {
        var result = Result(code);
        result.EnterNumeric(Parameters[code], value, Now.AddMinutes(-20), Guid.NewGuid());
        result.ApplyInterpretation(Parameters[code], _ranges[code], AppLanguage.French, null);
        if (technicallyValidated) result.TechnicallyValidate(Now.AddMinutes(-10), Guid.NewGuid());
        return this;
    }

    public BenchWorksheetLine PendingLine() => new()
    {
        LabOrderItemId = Order.Items.First().Id,
        AccessionNumber = Order.AccessionNumber,
        SampleBarcode = "T-" + Order.AccessionNumber,
        Parameters = [],
        PatientName = Patient.FullName,
        Priority = Order.Priority,
        ReceivedAt = Now.AddMinutes(-40),
        DepartmentCode = "BIO",
        AnalysisCode = Analyses.Values.First().Code,
        AnalysisName = Analyses.Values.First().Name
    };
}
