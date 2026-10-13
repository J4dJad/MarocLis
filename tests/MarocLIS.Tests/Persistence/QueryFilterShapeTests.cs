using FluentAssertions;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// Guards the interaction between the global query filters and required navigations.
/// <para>
/// When a globally filtered entity is the required end of a relationship, and the dependent carries
/// no matching filter, EF turns the navigation into an inner join. Filtering the principal out then
/// takes its dependents with it — rows still in the database, silently absent from every query that
/// resolves the navigation. There is no error, no gap in a sequence, just less data than there
/// should be, which is the worst failure mode available in a clinical system.
/// </para>
/// <para>
/// Two situations produce that shape and they are not the same thing:
/// </para>
/// <list type="bullet">
/// <item><b>Cross-aggregate reference</b> — an order item pointing at a catalogue analysis, a
/// coverage pointing at a payer. Here the behaviour is a defect: retiring master data must never
/// remove the historical records that used it. This is what the test exists to prevent.</item>
/// <item><b>Aggregate parent and its own child</b> — an order and its items. Here the behaviour is
/// what is wanted: a soft-deleted order's items should disappear with it.</item>
/// </list>
/// <para>
/// The distinction is not something EF can draw, so the accepted cases are listed below with the
/// reason each is acceptable. Anything else fails this test by name.
/// </para>
/// <para>
/// This also serves as the only check that the model builds at all. It would have caught the
/// site-scope filter's <c>Count</c> lookup — an exception thrown during model construction, which
/// took down the whole context on every machine — without needing a database.
/// </para>
/// </summary>
public sealed class QueryFilterShapeTests
{
    /// <summary>
    /// Relationships where a filtered principal legitimately takes its required dependents with
    /// it, because the dependent is part of that same aggregate and has no independent life.
    /// </summary>
    private static readonly HashSet<string> AcceptedParentChildRelationships = new(StringComparer.Ordinal)
    {
        // An order and the analyses requested on it. An item without its order is not a meaningful
        // record, so a query that resolves the navigation — anything doing Include(i => i.LabOrder)
        // — dropping the items of a soft-deleted order is the behaviour wanted.
        //
        // One residual, stated rather than glossed: querying LabOrderItems *without* touching the
        // navigation applies no filter, so the items of a soft-deleted order would still come back
        // from db.LabOrderItems directly. That is currently unreachable — nothing in the
        // application calls IRepository.SoftDelete, and LabOrder.Cancel sets a status rather than
        // the delete flag — but it becomes real the day a purge path is added, and the fix then is
        // to give LabOrderItem a filter that follows its order rather than to widen this list.
        "LabOrder -> LabOrderItem"
    };

    private static LisDbContext NewContext()
    {
        // No connection is opened. Building and validating the model is pure metadata work, which
        // is why this test runs on a machine with no SQL Server on it.
        var options = new DbContextOptionsBuilder<LisDbContext>()
            .UseSqlServer("Server=.;Database=MarocLIS;Integrated Security=true")
            .Options;

        return new LisDbContext(options);
    }

    [Fact]
    public void TheModelBuilds()
    {
        using var context = NewContext();

        context.Model.GetEntityTypes().Should().NotBeEmpty();
    }

    [Fact]
    public void NoFilteredPrincipalTakesACrossAggregateDependentWithIt()
    {
        using var context = NewContext();

        var offenders = new List<string>();

        foreach (var principal in context.Model.GetEntityTypes())
        {
            // GetDeclaredQueryFilters is what EF itself consults — EF 10 made filters a named collection,
            // out from under a dependent.
            if (principal.GetDeclaredQueryFilters() is null or { Count: 0 }) continue;

            foreach (var foreignKey in principal.GetReferencingForeignKeys())
            {
                if (!foreignKey.IsRequired) continue;

                // No navigation means no join to inner-join: the dependent only ever holds the
                // foreign key value, and nothing resolves it.
                if (foreignKey.DependentToPrincipal is null) continue;

                var dependent = foreignKey.DeclaringEntityType;

                // A dependent with its own filter matches the principal's, which is the second
                // remedy EF suggests and is already correct.
                if (dependent.GetDeclaredQueryFilters() is { Count: > 0 }) continue;

                var relationship = $"{principal.ClrType.Name} -> {dependent.ClrType.Name}";

                if (!AcceptedParentChildRelationships.Contains(relationship))
                    offenders.Add(relationship);
            }
        }

        offenders.Distinct().Should().BeEmpty(
            "a globally filtered entity must not be the required end of a navigation from an " +
            "unfiltered entity in another aggregate — retiring it would remove the historical " +
            "records that reference it from every query. Either stop filtering the principal " +
            "(retirement is a business state such as IsActive, not a deletion), or add the " +
            "relationship to AcceptedParentChildRelationships with the reason it is safe.");
    }

    /// <summary>
    /// The four types this defect was found on must stay unfiltered.
    /// <para>
    /// Named individually rather than left to the structural test above, because the structural
    /// test passes just as well if somebody adds them to the allow-list — and for these four the
    /// allow-list would be the wrong answer. Each is referenced by records that must outlive it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("AnalysisTest")]
    [InlineData("Department")]
    [InlineData("AppUser")]
    [InlineData("Payer")]
    public void ReferencedMasterData_IsNotGloballyFiltered(string typeName)
    {
        using var context = NewContext();

        var entity = context.Model.GetEntityTypes()
            .SingleOrDefault(e => e.ClrType.Name == typeName);

        entity.Should().NotBeNull($"{typeName} should be mapped");

        entity!.GetDeclaredQueryFilters().Should().BeNullOrEmpty(
            $"{typeName} is referenced by records that must outlive its retirement; it is " +
            "deactivated through IsActive, not deleted");
    }
}
