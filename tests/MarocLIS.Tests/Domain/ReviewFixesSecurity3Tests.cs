using System.Text.Json;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Infrastructure.Persistence.Demo;
using MarocLIS.Infrastructure.Security;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesSecurityTests;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Second security review: the shipped demonstration flag and the published demonstration
/// passwords (X1), built-in roles upgraded from a version that did not record what it offered
/// (X4), and what the periodic session check compares (X3, X6). The database side of X1 and X6
/// is in <c>Persistence/ReviewFixesSecurity3DatabaseTests</c>.
/// </summary>
public sealed class ReviewFixesSecurity3Tests
{
    // ---- X1: demonstration data and accounts ------------------------------------------------------

    [Fact]
    public void The_shipped_configuration_does_not_seed_demonstration_data()
    {
        var path = Path.Combine(SolutionDirectory(), "src", "MarocLIS.Desktop", "appsettings.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path),
                                            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

        json.RootElement.GetProperty("Application").GetProperty("SeedDemoDataOnFirstRun").GetBoolean()
            .Should().BeFalse();
    }

    [Fact]
    public void Only_a_published_demonstration_pair_counts_as_a_published_credential()
    {
        var biologist = DemoDataSeeder.Users.Single(u => u.UserName == "demo.biologiste");

        DemoAccountGuard.IsPublishedCredential("demo.biologiste", biologist.Password).Should().BeTrue();
        DemoAccountGuard.IsPublishedCredential("demo.biologiste", "Autre-Mot-2026!").Should().BeFalse();
        DemoAccountGuard.IsPublishedCredential("admin", biologist.Password).Should().BeFalse();
    }

    // ---- X4: upgrade of a role seeded before tracking -----------------------------------------

    [Fact]
    public void A_role_upgraded_from_before_tracking_receives_the_permissions_introduced_since()
    {
        // An installation from before the HR module: its administrator role has everything that
        // version shipped (it was re-granted at every start) and nothing of HR.
        var role = new Role(SystemRoles.Administrator, new MultilingualText("A", "A", "A"), isBuiltIn: true);
        role.Grant(Permissions.Administration.ManageUsers);

        var shipped = new[]
        {
            Permissions.Administration.ManageUsers,
            Permissions.HumanResources.View,
            Permissions.HumanResources.Salary
        };

        role.ApplyShippedPermissions(shipped)
            .Should().BeEquivalentTo([Permissions.HumanResources.View, Permissions.HumanResources.Salary]);
        role.Has(Permissions.HumanResources.View).Should().BeTrue();

        // Removed by the administrator after the upgrade: stays removed.
        role.Revoke(Permissions.HumanResources.Salary);
        role.ApplyShippedPermissions(shipped).Should().BeEmpty();
        role.Has(Permissions.HumanResources.Salary).Should().BeFalse();
    }

    // ---- X3: qualification -------------------------------------------------------------------------

    [Fact]
    public void A_session_whose_qualification_changed_is_ended()
    {
        var session = QualifiedSession(ProfessionalQualification.MedicalBiologist);

        SessionValidator.Matches(session, "stamp", true, "stamp", null, [], [],
                                 ProfessionalQualification.MedicalBiologist).Should().BeTrue();
        SessionValidator.Matches(session, "stamp", true, "stamp", null, [], [],
                                 ProfessionalQualification.LaboratoryTechnician).Should().BeFalse();
        SessionValidator.Matches(session, "stamp", true, "stamp", null, [], [],
                                 ProfessionalQualification.PrescribingPhysician).Should().BeFalse();
    }

    // ---- X6: own password change -------------------------------------------------------------------

    [Fact]
    public void The_stamp_being_saved_by_the_sessions_own_password_change_is_accepted()
    {
        var session = QualifiedSession(ProfessionalQualification.None);

        // The new stamp is in the database, the session still holds the old one.
        SessionValidator.Matches(session, "old", true, "new", null, [], [],
                                 ProfessionalQualification.None, pendingStamp: "new").Should().BeTrue();

        // Without that change in flight, the same difference still ends the session.
        SessionValidator.Matches(session, "old", true, "new", null, [], [],
                                 ProfessionalQualification.None).Should().BeFalse();
        SessionValidator.Matches(session, "old", true, "other", null, [], [],
                                 ProfessionalQualification.None, pendingStamp: "new").Should().BeFalse();
    }

    private static ICurrentUser QualifiedSession(ProfessionalQualification qualification) =>
        Stub<ICurrentUser>.Create(new()
        {
            ["get_" + nameof(ICurrentUser.IsAuthenticated)] = _ => true,
            ["get_" + nameof(ICurrentUser.Qualification)] = _ => qualification,
            ["get_" + nameof(ICurrentUser.Permissions)] = _ => (IReadOnlySet<string>)new HashSet<string>(),
            ["get_" + nameof(ICurrentUser.AccessibleSiteIds)] = _ => (IReadOnlyList<Guid>)[],
            ["get_" + nameof(ICurrentUser.PrescriberId)] = _ => null
        });

    private static string SolutionDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MarocLIS.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("MarocLIS.sln not found.");
    }
}
