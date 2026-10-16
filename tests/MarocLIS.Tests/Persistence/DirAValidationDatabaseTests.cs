using FluentAssertions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// A scratch database of its own, <c>MarocLIS_DirAValidation</c>, created from the model and dropped
/// at the end, foreign keys switched off like the other scratch databases.
/// </summary>
public sealed class DirAValidationScratchDatabase : IDisposable
{
    public const string Name = "MarocLIS_DirAValidation";

    private readonly bool _created;

    public string ConnectionString { get; }

    public DirAValidationScratchDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            ConnectionString = "";
            return;
        }

        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = Name }.ConnectionString;

        using var context = Context();
        if (context.Database.CanConnect())
            throw new InvalidOperationException(
                $"Database {Name} already exists; the tests create and drop their own and will not touch an existing one.");

        context.Database.EnsureCreated();
        _created = true;

        context.Database.ExecuteSqlRaw("EXEC sp_MSforeachtable 'ALTER TABLE ? NOCHECK CONSTRAINT ALL'");
    }

    public LisDbContext Context() =>
        new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(ConnectionString).Options);

    public void Dispose()
    {
        if (!_created) return;
        SqlConnection.ClearAllPools();
        using var context = Context();
        context.Database.EnsureDeleted();
    }
}

/// <summary>The query the « Comptes rendus » screen's « Aperçu » reads for a file not yet issued.</summary>
public sealed class DirAValidationDatabaseTests(DirAValidationScratchDatabase database)
    : IClassFixture<DirAValidationScratchDatabase>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 0, 0, TimeSpan.FromHours(1));

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static (LabOrder Order, TestResult Result) ValidatedOrder(Guid patientId, Guid siteId)
    {
        var order = new LabOrder(Unique("A"), patientId, siteId, Now, Guid.NewGuid());
        var item = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);
        order.AddItem(item);

        var parameter = new MarocLIS.Domain.Catalog.TestParameter("HB", new MultilingualText("HB"),
                                                         MarocLIS.Domain.Catalog.ResultValueType.Numeric);
        var result = new TestResult(order.Id, item.Id, parameter.Id, siteId);
        result.EnterNumeric(parameter, 13m, Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        result.TechnicallyValidate(Now, Guid.NewGuid());
        result.BiologicallyValidate(Now, Guid.NewGuid(), false, true).IsSuccess.Should().BeTrue();

        return (order, result);
    }

    [SqlServerFact]
    public async Task PendingRelease_NamesThePrescriber_AsTheRegisterDoes()
    {
        var siteId = Guid.NewGuid();
        var patient = new Patient(Unique("P"), "TAZI", "Imane", new DateOnly(1990, 3, 3), Gender.Female, siteId);

        var doctor = new Prescriber("BENNANI", "Omar", PrescriberKind.GeneralPractitioner);
        var clinic = new Prescriber("ALAOUI", "Karim", PrescriberKind.Institution) { InstitutionName = "Clinique Atlas" };

        var (byDoctor, first) = ValidatedOrder(patient.Id, siteId);
        byDoctor.PrescriberId = doctor.Id;

        var (byClinic, second) = ValidatedOrder(patient.Id, siteId);
        byClinic.PrescriberId = clinic.Id;

        var (freeText, third) = ValidatedOrder(patient.Id, siteId);
        freeText.PrescriberFreeText = "Dr Hors annuaire";

        await using (var context = database.Context())
        {
            context.Patients.Add(patient);
            context.Prescribers.AddRange(doctor, clinic);
            context.LabOrders.AddRange(byDoctor, byClinic, freeText);
            context.TestResults.AddRange(first, second, third);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var pending = await new ReportQueries(read).GetPendingReleaseAsync(siteId, null);

        pending.Single(r => r.LabOrderId == byDoctor.Id).PrescriberName.Should().Be("BENNANI Omar");
        pending.Single(r => r.LabOrderId == byClinic.Id).PrescriberName.Should().Be("Clinique Atlas");
        pending.Single(r => r.LabOrderId == freeText.Id).PrescriberName.Should().Be("Dr Hors annuaire");
    }
}
