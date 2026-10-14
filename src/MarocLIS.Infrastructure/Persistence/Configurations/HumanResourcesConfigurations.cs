using MarocLIS.Domain.HumanResources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MarocLIS.Infrastructure.Persistence.Configurations;

// ---- Personnel ---------------------------------------------------------------------------------

public sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.ToTable("Employees", "hr");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Number).AsCode(20);
        builder.HasIndex(e => e.Number).IsUnique().HasDatabaseName("UX_Employees_Number");

        builder.Property(e => e.LastName).IsRequired().AsSearchable(100);
        builder.Property(e => e.FirstName).IsRequired().AsSearchable(100);
        builder.Property(e => e.FullNameArabic).HasMaxLength(200);
        builder.Ignore(e => e.FullName);
        builder.Ignore(e => e.IsActive);

        builder.Property(e => e.Cin).HasMaxLength(20);
        builder.Property(e => e.CnssNumber).HasMaxLength(20);
        builder.Property(e => e.Address).HasMaxLength(300);
        builder.Property(e => e.City).HasMaxLength(100);
        builder.Property(e => e.Phone).HasMaxLength(30);
        builder.Property(e => e.Email).HasMaxLength(200);
        builder.Property(e => e.JobTitle).HasMaxLength(150);
        builder.Property(e => e.WeeklyHours).HasColumnType("decimal(5,2)");
        builder.Property(e => e.BaseSalary).HasColumnType("decimal(18,2)");
        builder.Property(e => e.Rib).HasMaxLength(30);
        builder.Property(e => e.BankName).HasMaxLength(100);
        builder.Property(e => e.ExitComment).HasMaxLength(500);

        // One personnel file per LIS account.
        builder.HasIndex(e => e.UserId).IsUnique().HasFilter("[UserId] IS NOT NULL")
               .HasDatabaseName("UX_Employees_User");
        builder.HasIndex(e => e.Cin).HasDatabaseName("IX_Employees_Cin");
        builder.HasIndex(e => new { e.SiteId, e.ExitDate }).HasDatabaseName("IX_Employees_Site_Exit");

        builder.HasMany(e => e.Credentials).WithOne()
               .HasForeignKey(c => c.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.Habilitations).WithOne()
               .HasForeignKey(h => h.EmployeeId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class EmployeeCredentialConfiguration : IEntityTypeConfiguration<EmployeeCredential>
{
    public void Configure(EntityTypeBuilder<EmployeeCredential> builder)
    {
        builder.ToTable("EmployeeCredentials", "hr");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Title).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Reference).HasMaxLength(60);
        builder.Property(e => e.IssuedBy).HasMaxLength(200);
        builder.Property(e => e.Note).HasMaxLength(500);
        builder.HasIndex(e => e.ExpiresOn).HasDatabaseName("IX_EmployeeCredentials_Expiry");
    }
}

public sealed class EmployeeHabilitationConfiguration : IEntityTypeConfiguration<EmployeeHabilitation>
{
    public void Configure(EntityTypeBuilder<EmployeeHabilitation> builder)
    {
        builder.ToTable("EmployeeHabilitations", "hr");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Evidence).HasMaxLength(500);
        builder.Property(e => e.WithdrawalReason).HasMaxLength(500);
        builder.Ignore(e => e.IsActive);
        builder.HasIndex(e => new { e.EmployeeId, e.Activity, e.SubjectId })
               .HasDatabaseName("IX_EmployeeHabilitations_Activity");
    }
}
