using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Sequencing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MarocLIS.Infrastructure.Persistence.Configurations;

public sealed class LabSiteConfiguration : IEntityTypeConfiguration<LabSite>
{
    public void Configure(EntityTypeBuilder<LabSite> builder)
    {
        builder.ToTable("Sites", "org");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Code).AsCode(20);
        builder.HasIndex(e => e.Code).IsUnique().HasDatabaseName("UX_Sites_Code");

        builder.MapMultilingual(e => e.Name, "Name");
        builder.MapMultilingual(e => e.AddressLine, "Address", frenchRequired: false, maxLength: 400);

        builder.Property(e => e.LegalName).HasMaxLength(250);
        builder.Property(e => e.Ice).HasMaxLength(15);
        builder.Property(e => e.FiscalId).HasMaxLength(20);
        builder.Property(e => e.CommercialRegisterNumber).HasMaxLength(40);
        builder.Property(e => e.CommercialRegisterCourt).HasMaxLength(100);
        builder.Property(e => e.ProfessionalTaxNumber).HasMaxLength(40);
        builder.Property(e => e.CnssAffiliationNumber).HasMaxLength(20);
        builder.Property(e => e.Inpe).HasMaxLength(20);
        builder.Property(e => e.HealthMinistryAuthorizationNumber).HasMaxLength(60);
        builder.Property(e => e.AccreditationNumber).HasMaxLength(60);
        builder.Property(e => e.City).HasMaxLength(100);
        builder.Property(e => e.PostalCode).HasMaxLength(10);
        builder.Property(e => e.LogoPng).HasColumnType("varbinary(max)");

        // The ICE appears on every invoice; a duplicate would mean two sites claiming one legal
        // identity, which the tax administration would treat as an inconsistency.
        builder.HasIndex(e => e.Ice)
               .IsUnique()
               .HasFilter("[Ice] IS NOT NULL AND [IsDeleted] = 0")
               .HasDatabaseName("UX_Sites_Ice");
    }
}

public sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("Users", "sec");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.UserName).AsCode(60);
        builder.HasIndex(e => e.UserName).IsUnique().HasDatabaseName("UX_Users_UserName");

        builder.Property(e => e.FullName).AsSearchable(200);
        builder.Property(e => e.FullNameArabic).AsSearchable(200);
        builder.Property(e => e.PasswordHash).HasMaxLength(200).IsRequired();
        builder.Property(e => e.SecurityStamp).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Email).HasMaxLength(200);
        builder.Property(e => e.Phone).HasMaxLength(30);
        builder.Property(e => e.ProfessionalOrderNumber).HasMaxLength(40);
        builder.Property(e => e.Inpe).HasMaxLength(20);
        builder.Property(e => e.AuthorizedDepartmentCodesCsv).HasMaxLength(500);
        builder.Property(e => e.LastLoginWorkstation).HasMaxLength(100);
        builder.Property(e => e.SignaturePng).HasColumnType("varbinary(max)");

        builder.HasMany(e => e.Roles)
               .WithOne(r => r.User!)
               .HasForeignKey(r => r.UserId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(e => e.SiteAccess)
               .WithOne()
               .HasForeignKey(a => a.UserId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(e => e.Roles).AutoInclude();
        builder.Navigation(e => e.SiteAccess).AutoInclude();
    }
}

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles", "sec");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Code).AsCode(40);
        builder.HasIndex(e => e.Code).IsUnique().HasDatabaseName("UX_Roles_Code");

        builder.MapMultilingual(e => e.Name, "Name");
        builder.MapMultilingual(e => e.Description, "Description", frenchRequired: false, maxLength: 500);

        // Permission codes are short and the shipped set is a few dozen; nvarchar(max) keeps a
        // future larger catalogue from ever truncating the record.
        builder.Property(e => e.SeededPermissions);

        builder.HasMany(e => e.Permissions)
               .WithOne(p => p.Role!)
               .HasForeignKey(p => p.RoleId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(e => e.Permissions).AutoInclude();
    }
}

public sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("UserRoles", "sec");
        builder.HasKey(e => new { e.UserId, e.RoleId });
    }
}

public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("RolePermissions", "sec");
        builder.HasKey(e => new { e.RoleId, e.Permission });
        builder.Property(e => e.Permission).AsCode(80);
    }
}

public sealed class UserSiteAccessConfiguration : IEntityTypeConfiguration<UserSiteAccess>
{
    public void Configure(EntityTypeBuilder<UserSiteAccess> builder)
    {
        builder.ToTable("UserSiteAccess", "sec");
        builder.HasKey(e => new { e.UserId, e.SiteId });
    }
}

public sealed class PasswordHistoryConfiguration : IEntityTypeConfiguration<PasswordHistoryEntry>
{
    public void Configure(EntityTypeBuilder<PasswordHistoryEntry> builder)
    {
        builder.ToTable("PasswordHistory", "sec");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.PasswordHash).HasMaxLength(200).IsRequired();
        builder.HasIndex(e => new { e.UserId, e.ChangedAt }).HasDatabaseName("IX_PasswordHistory_User");
    }
}

public sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.ToTable("Departments", "cat");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).AsCode(20);
        builder.HasIndex(e => e.Code).IsUnique().HasDatabaseName("UX_Departments_Code");
        builder.MapMultilingual(e => e.Name, "Name");
    }
}

public sealed class SpecimenTypeConfiguration : IEntityTypeConfiguration<SpecimenType>
{
    public void Configure(EntityTypeBuilder<SpecimenType> builder)
    {
        builder.ToTable("SpecimenTypes", "cat");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).AsCode(30);
        builder.HasIndex(e => e.Code).IsUnique().HasDatabaseName("UX_SpecimenTypes_Code");
        builder.MapMultilingual(e => e.Name, "Name");
        builder.MapMultilingual(e => e.CollectionInstructions, "CollectionInstructions",
                                frenchRequired: false, maxLength: 1000);
        builder.MapMultilingual(e => e.PatientInstructions, "PatientInstructions",
                                frenchRequired: false, maxLength: 1000);
    }
}

public sealed class ContainerTypeConfiguration : IEntityTypeConfiguration<ContainerType>
{
    public void Configure(EntityTypeBuilder<ContainerType> builder)
    {
        builder.ToTable("ContainerTypes", "cat");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).AsCode(30);
        builder.HasIndex(e => e.Code).IsUnique().HasDatabaseName("UX_ContainerTypes_Code");
        builder.Property(e => e.CapColourHex).HasMaxLength(9);
        builder.MapMultilingual(e => e.Name, "Name");
        builder.MapMultilingual(e => e.Additive, "Additive", frenchRequired: false, maxLength: 200);
    }
}

public sealed class MeasurementUnitConfiguration : IEntityTypeConfiguration<MeasurementUnit>
{
    public void Configure(EntityTypeBuilder<MeasurementUnit> builder)
    {
        builder.ToTable("Units", "cat");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).AsCode(30);
        builder.HasIndex(e => e.Code).IsUnique().HasDatabaseName("UX_Units_Code");
        builder.Property(e => e.DisplaySymbol).HasMaxLength(40).IsRequired();
        builder.Property(e => e.DisplaySymbolArabic).HasMaxLength(40);
        builder.Property(e => e.UcumCode).HasMaxLength(40);
        builder.Property(e => e.SiUnitCode).HasMaxLength(30);
    }
}

public sealed class AnalysisTestConfiguration : IEntityTypeConfiguration<AnalysisTest>
{
    public void Configure(EntityTypeBuilder<AnalysisTest> builder)
    {
        builder.ToTable("AnalysisTests", "cat");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Code).AsCode(30);
        builder.HasIndex(e => e.Code).IsUnique().HasDatabaseName("UX_AnalysisTests_Code");

        builder.MapMultilingual(e => e.Name, "Name");
        builder.MapMultilingual(e => e.ShortName, "ShortName", frenchRequired: false, maxLength: 60);
        builder.MapMultilingual(e => e.Method, "Method", frenchRequired: false, maxLength: 200);
        builder.MapMultilingual(e => e.RequiredClinicalInformation, "ClinicalInfoPrompt",
                                frenchRequired: false, maxLength: 500);

        builder.Property(e => e.SearchAliases).AsSearchable(500);
        builder.Property(e => e.LoincCode).HasMaxLength(20);
        builder.Property(e => e.NationalActCode).HasMaxLength(30);
        builder.Property(e => e.ReagentReference).HasMaxLength(200);
        builder.Property(e => e.ScheduledDaysCsv).HasMaxLength(30);

        builder.HasOne(e => e.Department).WithMany().HasForeignKey(e => e.DepartmentId)
               .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.SpecimenType).WithMany().HasForeignKey(e => e.SpecimenTypeId)
               .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.ContainerType).WithMany().HasForeignKey(e => e.ContainerTypeId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(e => e.Parameters)
               .WithOne(p => p.AnalysisTest!)
               .HasForeignKey(p => p.AnalysisTestId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(e => e.Tariffs)
               .WithOne()
               .HasForeignKey(t => t.AnalysisTestId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.NationalActCode).HasDatabaseName("IX_AnalysisTests_ActCode");
    }
}

public sealed class TestParameterConfiguration : IEntityTypeConfiguration<TestParameter>
{
    public void Configure(EntityTypeBuilder<TestParameter> builder)
    {
        builder.ToTable("TestParameters", "cat");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Code).AsCode(30);
        builder.MapMultilingual(e => e.Name, "Name");
        builder.MapMultilingual(e => e.ShortName, "ShortName", frenchRequired: false, maxLength: 60);

        builder.Property(e => e.LoincCode).HasMaxLength(20);
        builder.Property(e => e.CalculationFormula).HasMaxLength(1000);

        builder.HasOne(e => e.Unit).WithMany().HasForeignKey(e => e.UnitId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(e => e.ReferenceRanges)
               .WithOne()
               .HasForeignKey(r => r.TestParameterId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(e => e.CodedOptions)
               .WithOne()
               .HasForeignKey(o => o.TestParameterId)
               .OnDelete(DeleteBehavior.Cascade);

        // A parameter code must be unique within its analysis: instrument mappings and calculation
        // formulas both address parameters by code, so an ambiguous code silently mis-routes values.
        builder.HasIndex(e => new { e.AnalysisTestId, e.Code })
               .IsUnique()
               .HasDatabaseName("UX_TestParameters_Analysis_Code");
    }
}

public sealed class ReferenceRangeConfiguration : IEntityTypeConfiguration<ReferenceRange>
{
    public void Configure(EntityTypeBuilder<ReferenceRange> builder)
    {
        builder.ToTable("ReferenceRanges", "cat");
        builder.HasKey(e => e.Id);
        builder.MapMultilingual(e => e.DisplayText, "DisplayText", frenchRequired: false, maxLength: 200);
        builder.Property(e => e.ExpectedCodedValue).HasMaxLength(60);
        builder.Property(e => e.Source).HasMaxLength(300);
        builder.HasIndex(e => new { e.TestParameterId, e.IsActive })
               .HasDatabaseName("IX_ReferenceRanges_Parameter");
    }
}

public sealed class CodedResultOptionConfiguration : IEntityTypeConfiguration<CodedResultOption>
{
    public void Configure(EntityTypeBuilder<CodedResultOption> builder)
    {
        builder.ToTable("CodedResultOptions", "cat");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).AsCode(40);
        builder.MapMultilingual(e => e.Label, "Label");
        builder.HasIndex(e => new { e.TestParameterId, e.Code })
               .IsUnique()
               .HasDatabaseName("UX_CodedResultOptions_Parameter_Code");
    }
}

public sealed class AnalysisTestTariffConfiguration : IEntityTypeConfiguration<AnalysisTestTariff>
{
    public void Configure(EntityTypeBuilder<AnalysisTestTariff> builder)
    {
        builder.ToTable("Tariffs", "cat");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.LetterKey).HasMaxLength(5).IsRequired();
        builder.Property(e => e.Price).HasColumnType("decimal(18,2)");
        builder.Property(e => e.LetterKeyValue).HasColumnType("decimal(18,4)");
        builder.Property(e => e.Coefficient).HasColumnType("decimal(18,4)");
        builder.Property(e => e.VatRate).HasColumnType("decimal(6,4)");

        builder.HasIndex(e => new { e.AnalysisTestId, e.PayerId, e.ValidFrom })
               .HasDatabaseName("IX_Tariffs_Analysis_Payer_From");
    }
}

public sealed class TestPanelConfiguration : IEntityTypeConfiguration<TestPanel>
{
    public void Configure(EntityTypeBuilder<TestPanel> builder)
    {
        builder.ToTable("TestPanels", "cat");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).AsCode(30);
        builder.HasIndex(e => e.Code).IsUnique().HasDatabaseName("UX_TestPanels_Code");
        builder.MapMultilingual(e => e.Name, "Name");
        builder.Property(e => e.SearchAliases).AsSearchable(500);
        builder.Property(e => e.PackagePrice).HasColumnType("decimal(18,2)");

        builder.HasMany(e => e.Items)
               .WithOne()
               .HasForeignKey(i => i.TestPanelId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class TestPanelItemConfiguration : IEntityTypeConfiguration<TestPanelItem>
{
    public void Configure(EntityTypeBuilder<TestPanelItem> builder)
    {
        builder.ToTable("TestPanelItems", "cat");
        builder.HasKey(e => e.Id);
        builder.HasOne(e => e.AnalysisTest).WithMany().HasForeignKey(e => e.AnalysisTestId)
               .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.TestPanelId, e.AnalysisTestId })
               .IsUnique()
               .HasDatabaseName("UX_TestPanelItems_Panel_Analysis");
    }
}

public sealed class ReflexRuleConfiguration : IEntityTypeConfiguration<ReflexRule>
{
    public void Configure(EntityTypeBuilder<ReflexRule> builder)
    {
        builder.ToTable("ReflexRules", "cat");
        builder.HasKey(e => e.Id);
        builder.MapMultilingual(e => e.Description, "Description", maxLength: 500);
        builder.Property(e => e.TriggerCodedValue).HasMaxLength(60);
        builder.HasIndex(e => new { e.TriggerParameterId, e.IsActive })
               .HasDatabaseName("IX_ReflexRules_Trigger");
    }
}

public sealed class NumberSequenceConfiguration : IEntityTypeConfiguration<NumberSequence>
{
    public void Configure(EntityTypeBuilder<NumberSequence> builder)
    {
        builder.ToTable("NumberSequences", "cfg");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Format).HasMaxLength(100).IsRequired();

        // One sequence per purpose, site and year. The unique index is what makes the gapless
        // allocation safe: two workstations racing to create the same sequence cannot both win.
        builder.HasIndex(e => new { e.Purpose, e.SiteId, e.Year })
               .IsUnique()
               .HasDatabaseName("UX_NumberSequences_Scope");

        // No row version: the allocator takes an UPDLOCK and expects to serialise, not to retry.
        builder.Ignore(e => e.RowVersion);
    }
}

public sealed class AppSettingConfiguration : IEntityTypeConfiguration<AppSetting>
{
    public void Configure(EntityTypeBuilder<AppSetting> builder)
    {
        builder.ToTable("Settings", "cfg");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Key).AsCode(120);
        builder.Property(e => e.Value).HasMaxLength(2000);
        builder.Property(e => e.Description).HasMaxLength(500);
        builder.HasIndex(e => new { e.Key, e.SiteId }).IsUnique().HasDatabaseName("UX_Settings_Key_Site");
    }
}
