using System.Collections;
using System.Reflection;
using Bunit;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Infrastructure.Security;
using MarocLIS.Localization;
using MarocLIS.Web.Security;
using MarocLIS.Web.Session;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static MarocLIS.Tests.Licensing.LicenceTestKit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// A query interface in memory: each method answers what the test set for it, or nothing (an empty
/// list, a null, a completed task). The calls are kept, so a test can say what the screen asked.
/// </summary>
public class Fake<T> : DispatchProxy where T : class
{
    private readonly Dictionary<string, Func<object?[], object?>> _answers = new(StringComparer.Ordinal);

    public List<(string Method, object?[] Arguments)> Calls { get; } = [];

    public T Object => (T)(object)this;

    public static Fake<T> Create() => (Fake<T>)(object)DispatchProxy.Create<T, Fake<T>>();

    /// <summary>What <paramref name="method"/> answers, as the value its task carries.</summary>
    public Fake<T> On(string method, Func<object?[], object?> answer)
    {
        _answers[method] = answer;
        return this;
    }

    public int CallsTo(string method) => Calls.Count(c => c.Method == method);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];
        Calls.Add((targetMethod.Name, args));

        var returns = targetMethod.ReturnType;
        var value = _answers.TryGetValue(targetMethod.Name, out var answer) ? answer(args) : null;

        if (returns == typeof(void)) return null;
        if (returns == typeof(Task)) return Task.CompletedTask;
        if (returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var carried = returns.GetGenericArguments()[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(carried)
                               .Invoke(null, [value ?? Empty(carried)]);
        }

        return value ?? Empty(returns);
    }

    private static object? Empty(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() is var generic
            && (generic == typeof(IReadOnlyList<>) || generic == typeof(IReadOnlyCollection<>)))
            return Array.CreateInstance(type.GetGenericArguments()[0], 0);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
            return Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(type.GetGenericArguments()));
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PagedResult<>))
            return type.GetMethod("Empty")!.Invoke(null, [50]);
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}

/// <summary>The accesses to medical data the screens record (loi 08-09), in memory.</summary>
public sealed class DataAuditor : IDataAccessAuditor
{
    public List<(DataAccessKind Kind, Guid? PatientId, Guid? OrderId, string? Context, int? Count, string? Query)> Entries { get; } = [];

    public Task LogAsync(DataAccessKind kind, Guid? patientId = null, Guid? orderId = null, string? context = null,
                         int? recordCount = null, string? queryDescription = null, CancellationToken ct = default)
    {
        Entries.Add((kind, patientId, orderId, context, recordCount, queryDescription));
        return Task.CompletedTask;
    }

    public Task LogBreakGlassAsync(Guid patientId, string justification, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// A screen of the web rendered in memory, with the session of an operator and the queries of the
/// workstation answered by fakes.
/// </summary>
public abstract class WebScreenContext : BunitContext
{
    protected readonly DataAuditor DataAudit = new();
    protected readonly IClock TestClock = new Clock() { Now = new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.FromHours(1)) };
    protected readonly Fake<IPatientQueries> Patients = Fake<IPatientQueries>.Create();
    protected readonly Fake<ICatalogQueries> Catalog = Fake<ICatalogQueries>.Create();
    protected readonly Fake<IOrderEntryQueries> Entry = Fake<IOrderEntryQueries>.Create();
    protected readonly Fake<Application.Administration.IMasterDataQueries> MasterData =
        Fake<Application.Administration.IMasterDataQueries>.Create();
    protected readonly Fake<IOrderQuotes> Quotes = Fake<IOrderQuotes>.Create();
    protected readonly Fake<IOrderQueries> Orders = Fake<IOrderQueries>.Create();
    protected readonly Fake<IOrderFileQueries> FileQueries = Fake<IOrderFileQueries>.Create();

    protected WebScreenContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddSingleton<ILocalizer>(new JsonLocalizer(new LocalizationOptions { MarkMissingKeys = true }));
        Services.AddSingleton<IClock>(TestClock);
        Services.AddSingleton(new PasswordPolicy());
        Services.AddSingleton(new WebDefaults(AppLanguage.French));
        Services.AddSingleton<WebSessionStore>();
        Services.AddSingleton<ISecurityAuditor>(new Auditor());
        Services.AddSingleton<IDataAccessAuditor>(DataAudit);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        Services.AddScoped<IPermissionGuard>(sp => new Guard(sp.GetRequiredService<ICurrentUser>()));
        Services.AddScoped<SessionContext>();
        Services.AddScoped<WebTexts>();
        Services.AddScoped<UserScopes>();
        Services.UsePerRequestCurrentUser();

        Services.AddSingleton(Patients.Object);
        Services.AddSingleton(Catalog.Object);
        Services.AddSingleton(Entry.Object);
        Services.AddSingleton(MasterData.Object);
        Services.AddSingleton(Quotes.Object);
        Services.AddSingleton(Orders.Object);
        Services.AddSingleton(FileQueries.Object);

        // The file screen's reads go through the service, which checks the right and records the
        // consultation; its writes are not exercised here.
        Services.AddScoped(sp => new OrderAmendmentService(
            null!, null!, null!, null!, sp.GetRequiredService<IOrderQueries>(), null!,
            sp.GetRequiredService<IPermissionGuard>(), sp.GetRequiredService<ICurrentUser>(),
            sp.GetRequiredService<IDataAccessAuditor>(), sp.GetRequiredService<IClock>(), null!));
    }

    protected void SignIn(AppLanguage language, params string[] permissions)
    {
        var account = new AuthenticatedSession(Guid.NewGuid(), "nadia", "Nadia", Guid.NewGuid(), "A", language,
                                               ProfessionalQualification.None, new HashSet<string>(permissions), [],
                                               new HashSet<string>(), MustChangePassword: false);
        var session = Services.GetRequiredService<WebSessionStore>()
                              .Open(account, "WEB-AAAAAAAAAA", "WEB-AAAAAAAAAA (192.168.1.20)", language, false);
        Services.GetRequiredService<SessionContext>().Attach(session);
    }

    // ---- Data --------------------------------------------------------------------------------------

    internal static CatalogPickerItem Analysis(string code, string name, decimal price = 50m, string department = "BIO",
                                               int? fasting = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), false, code, new MultilingualText(name), department, new MultilingualText("Biochimie"),
            price, fasting, 4, true, ConsentRequirement.None, false, "Sec", "#E74C3C", 0);

    internal static CatalogPickerItem Panel(string code, string name, decimal price, params CatalogPickerItem[] members) =>
        new(Guid.NewGuid(), true, code, new MultilingualText(name), null, null, price, null, 24, true,
            ConsentRequirement.None, false, null, null, members.Length,
            [.. members.Select(m => new PickerComponent(m.Id, m.Price))]);

    internal static PatientSearchRow PatientRow(string name = "ALAOUI Fatima Zahra", bool critical = false, int orders = 2) =>
        new(Guid.NewGuid(), "P-2026-000123", name, "علوي فاطمة الزهراء", Gender.Female, new DateOnly(1988, 5, 14), 38,
            "BE123456", "+212661234567", "Rabat", "CNOPS", orders > 0 ? new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.FromHours(1)) : null,
            orders, critical);

    internal static PatientDetail PatientDetailOf(PatientSearchRow row) =>
        new(row.Id, row.MedicalRecordNumber, "ALAOUI", "Fatima Zahra", null, null, row.Gender, row.DateOfBirth, null, null,
            row.Cin, null, "MA", null, null, null, null, row.City, default, null, row.MobilePhone, null, null,
            AppLanguage.French, false, null, null, null, default, null, null, false, false, null, null, true);
}
