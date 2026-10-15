using MarocLIS.Domain.Treasury;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MarocLIS.Infrastructure.Persistence.Configurations;

// ---- Treasury: bank accounts, statements, reconciliation, forecast items ------------------------

public sealed class BankAccountConfiguration : IEntityTypeConfiguration<BankAccount>
{
    public void Configure(EntityTypeBuilder<BankAccount> builder)
    {
        builder.ToTable("BankAccounts", "bil");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.BankName).HasMaxLength(120).IsRequired();
        builder.Property(e => e.Label).HasMaxLength(120).IsRequired();
        builder.Property(e => e.Rib).HasMaxLength(24).IsUnicode(false).IsRequired();
        builder.Property(e => e.AccountingAccount).HasMaxLength(12).IsUnicode(false).IsRequired();
        builder.Property(e => e.OpeningBalanceMad).HasColumnType("decimal(18,2)");
        builder.Property(e => e.CsvMapping).HasMaxLength(1000);

        // One account per RIB: two records of the same account would split its statement.
        builder.HasIndex(e => e.Rib).IsUnique().HasDatabaseName("UX_BankAccounts_Rib");
        builder.HasIndex(e => new { e.SiteId, e.IsActive }).HasDatabaseName("IX_BankAccounts_Site_Active");
    }
}

public sealed class BankStatementLineConfiguration : IEntityTypeConfiguration<BankStatementLine>
{
    public void Configure(EntityTypeBuilder<BankStatementLine> builder)
    {
        builder.ToTable("BankStatementLines", "bil");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Label).HasMaxLength(300).IsRequired();
        builder.Property(e => e.Reference).HasMaxLength(100);
        builder.Property(e => e.SourceFileName).HasMaxLength(200);
        builder.Property(e => e.AmountMad).HasColumnType("decimal(18,2)");
        builder.Property(e => e.Fingerprint).HasMaxLength(40).IsUnicode(false).IsRequired();

        builder.HasOne<BankAccount>().WithMany().HasForeignKey(e => e.BankAccountId)
               .OnDelete(DeleteBehavior.Restrict);

        // The duplicate check of every import, and a guarantee behind it.
        builder.HasIndex(e => new { e.BankAccountId, e.Fingerprint })
               .IsUnique()
               .HasDatabaseName("UX_BankStatementLines_Account_Fingerprint");

        // The reconciliation reads an account's lines over a period.
        builder.HasIndex(e => new { e.BankAccountId, e.OperationDate })
               .HasDatabaseName("IX_BankStatementLines_Account_Date");
    }
}

public sealed class BankMatchConfiguration : IEntityTypeConfiguration<BankMatch>
{
    public void Configure(EntityTypeBuilder<BankMatch> builder)
    {
        builder.ToTable("BankMatches", "bil");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.SourceKey).HasMaxLength(120).IsUnicode(false).IsRequired();
        builder.Property(e => e.AmountMad).HasColumnType("decimal(18,2)");
        builder.Property(e => e.Comment).HasMaxLength(500);
        builder.Ignore(e => e.IsActive);

        builder.HasOne<BankStatementLine>().WithMany().HasForeignKey(e => e.BankStatementLineId)
               .OnDelete(DeleteBehavior.Restrict);

        // A movement explains one statement line at a time; an undone match frees it.
        builder.HasIndex(e => new { e.Kind, e.SourceKey })
               .IsUnique()
               .HasFilter("[UnmatchedAt] IS NULL")
               .HasDatabaseName("UX_BankMatches_ActiveSource");

        builder.HasIndex(e => e.BankStatementLineId).HasDatabaseName("IX_BankMatches_Line");
    }
}

public sealed class TreasuryForecastItemConfiguration : IEntityTypeConfiguration<TreasuryForecastItem>
{
    public void Configure(EntityTypeBuilder<TreasuryForecastItem> builder)
    {
        builder.ToTable("TreasuryForecastItems", "bil");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Label).HasMaxLength(200).IsRequired();
        builder.Property(e => e.AmountMad).HasColumnType("decimal(18,2)");
        builder.HasIndex(e => new { e.SiteId, e.IsActive }).HasDatabaseName("IX_TreasuryForecastItems_Site_Active");
    }
}
