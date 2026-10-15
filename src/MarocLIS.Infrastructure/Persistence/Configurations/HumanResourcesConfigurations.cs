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

// ---- Leave and planning ------------------------------------------------------------------------

public sealed class LeaveRequestConfiguration : IEntityTypeConfiguration<LeaveRequest>
{
    public void Configure(EntityTypeBuilder<LeaveRequest> builder)
    {
        builder.ToTable("LeaveRequests", "hr");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Days).HasColumnType("decimal(6,2)");
        builder.Property(e => e.Reason).HasMaxLength(500);
        builder.Property(e => e.DecisionComment).HasMaxLength(500);
        builder.Ignore(e => e.IsLive);

        builder.HasOne<Employee>().WithMany().HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.EmployeeId, e.StartDate }).HasDatabaseName("IX_LeaveRequests_Employee_Start");
        builder.HasIndex(e => new { e.Status, e.StartDate }).HasDatabaseName("IX_LeaveRequests_Status_Start");
    }
}

public sealed class LeaveLedgerEntryConfiguration : IEntityTypeConfiguration<LeaveLedgerEntry>
{
    public void Configure(EntityTypeBuilder<LeaveLedgerEntry> builder)
    {
        builder.ToTable("LeaveLedger", "hr");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Days).HasColumnType("decimal(6,2)");
        builder.Property(e => e.Reference).HasMaxLength(40).IsRequired();
        builder.Property(e => e.Comment).HasMaxLength(500);

        builder.HasOne<Employee>().WithMany().HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        // A month is credited once, whoever runs the accrual and however often.
        builder.HasIndex(e => new { e.EmployeeId, e.Reference }).IsUnique()
               .HasFilter($"[Kind] = {(int)LeaveLedgerKind.Accrual}")
               .HasDatabaseName("UX_LeaveLedger_Accrual");
        builder.HasIndex(e => new { e.EmployeeId, e.Date }).HasDatabaseName("IX_LeaveLedger_Employee_Date");
    }
}

public sealed class PublicHolidayConfiguration : IEntityTypeConfiguration<PublicHoliday>
{
    public void Configure(EntityTypeBuilder<PublicHoliday> builder)
    {
        builder.ToTable("PublicHolidays", "hr");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(150).IsRequired();
        builder.Property(e => e.DeletionReason).HasMaxLength(500);
        builder.HasIndex(e => e.Date).IsUnique().HasFilter("[IsDeleted] = 0")
               .HasDatabaseName("UX_PublicHolidays_Date");
    }
}

public sealed class ShiftAssignmentConfiguration : IEntityTypeConfiguration<ShiftAssignment>
{
    public void Configure(EntityTypeBuilder<ShiftAssignment> builder)
    {
        builder.ToTable("ShiftAssignments", "hr");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Note).HasMaxLength(300);
        builder.Property(e => e.DeletionReason).HasMaxLength(500);
        builder.Ignore(e => e.Hours);
        builder.Ignore(e => e.StartsAt);
        builder.Ignore(e => e.EndsAt);

        builder.HasOne<Employee>().WithMany().HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.SiteId, e.Date }).HasDatabaseName("IX_ShiftAssignments_Site_Date");
        builder.HasIndex(e => new { e.EmployeeId, e.Date }).HasDatabaseName("IX_ShiftAssignments_Employee_Date");
    }
}
