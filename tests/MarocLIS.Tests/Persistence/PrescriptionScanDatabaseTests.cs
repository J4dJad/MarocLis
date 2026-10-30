using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Infrastructure;
using MarocLIS.Tests.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// The scanned prescription on SQL Server: kept in the order's own columns, read back whole, shown
/// on the file as « attached » without being loaded, and every change in the audit trail with its
/// reason and the document's size — never a copy of it.
/// </summary>
public sealed class PrescriptionScanDatabaseTests(OrderEntryScratchDatabase database)
    : IClassFixture<OrderEntryScratchDatabase>
{
    private sealed class Operator(Guid siteId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "accueil";
        public string FullName => "Accueil";
        public Guid SiteId => siteId;
        public string SiteCode => "S1";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.None;
        public IReadOnlyList<Guid> AccessibleSiteIds => [siteId];
        public IReadOnlySet<string> Permissions { get; } =
            new HashSet<string> { MarocLIS.Domain.Identity.Permissions.Orders.View, MarocLIS.Domain.Identity.Permissions.Orders.Edit };
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => Permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "ACCUEIL-1";
    }

    private ServiceProvider Services(Guid siteId)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(siteId));
        services.AddSingleton(PrescriptionFiles.Composer());
        services.AddIsolatedQueries<Application.Results.IResultQueries, MarocLIS.Infrastructure.Queries.ResultQueries>();
        services.AddIsolatedQueries<IOrderQueries, MarocLIS.Infrastructure.Queries.OrderQueries>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> On<T>(ServiceProvider provider, Func<PrescriptionScanService, Task<T>> action)
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<PrescriptionScanService>());
    }

    [SqlServerFact]
    public async Task AttachReplaceAndRemove_AreKeptAndTraced()
    {
        var siteId = Guid.NewGuid();
        var order = new LabOrder("PS-" + Guid.NewGuid().ToString("N")[..8], Guid.NewGuid(), siteId,
                                 DateTimeOffset.Now, Guid.NewGuid());
        await using (var context = database.Context())
        {
            context.LabOrders.Add(order);
            await context.SaveChangesAsync();
        }

        using var provider = Services(siteId);

        (await On(provider, s => s.AttachAsync(order.Id, [new PrescriptionPage("recto.jpg", PrescriptionFiles.Photo(1500, 2000))])))
            .IsSuccess.Should().BeTrue();
        (await On(provider, s => s.AttachAsync(order.Id, [new PrescriptionPage("verso.png", PrescriptionFiles.SmallPng())], append: true)))
            .Value.PageCount.Should().Be(2);

        var file = await On(provider, s => s.GetAsync(order.Id));
        file.Value.PageCount.Should().Be(2);
        file.Value.ContentType.Should().Be("application/pdf");

        using (var scope = provider.CreateScope())
        {
            var detail = await scope.ServiceProvider.GetRequiredService<IOrderQueries>().GetDetailAsync(order.Id, siteId);
            detail!.HasPrescriptionScan.Should().BeTrue();
        }

        (await On(provider, s => s.AttachAsync(order.Id, [new PrescriptionPage("clinique.pdf", PrescriptionFiles.Pdf(1))])))
            .Value.PageCount.Should().Be(1);
        (await On<Result>(provider, s => s.RemoveAsync(order.Id))).IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
        {
            var stored = await context.LabOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
            stored.PrescriptionScan.Should().BeNull();
            stored.PrescriptionScanContentType.Should().BeNull();

            var trail = await context.AuditEntries.AsNoTracking()
                .Where(a => a.EntityName == nameof(LabOrder) && a.EntityId == order.Id && a.Operation == AuditOperation.Update)
                .OrderBy(a => a.OccurredAt).ThenBy(a => a.Id)
                .ToListAsync();

            trail.Select(a => a.Reason).Should().Equal(
                $"Ordonnance numérisée jointe au dossier {order.AccessionNumber} (1 page, {trail[0].Reason!.Split(", ")[1]}",
                $"Ordonnance numérisée complétée sur le dossier {order.AccessionNumber} (2 pages, {trail[1].Reason!.Split(", ")[1]}",
                $"Ordonnance numérisée remplacée sur le dossier {order.AccessionNumber} (1 page, {trail[2].Reason!.Split(", ")[1]}",
                $"Ordonnance numérisée retirée du dossier {order.AccessionNumber}");
            trail.Should().OnlyContain(a => a.ChangedProperties!.Contains(nameof(LabOrder.PrescriptionScan)));
            trail.Should().OnlyContain(a => a.UserName == "accueil" && a.Workstation == "ACCUEIL-1");

            // The document is in the trail by its size, not as a copy.
            trail[0].Changes.Should().Contain("\"bytes\":").And.NotContain("JVBER", "no PDF is copied into the trail");
            trail[0].Changes!.Length.Should().BeLessThan(1000);

            // Opening it was a consultation of the file.
            (await context.DataAccessLogs.AsNoTracking()
                .AnyAsync(l => l.LabOrderId == order.Id && l.Kind == DataAccessKind.ViewOrder)).Should().BeTrue();
        }
    }
}
