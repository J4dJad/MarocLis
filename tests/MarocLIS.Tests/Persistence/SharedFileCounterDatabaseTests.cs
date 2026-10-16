using FluentAssertions;
using MarocLIS.Domain.Sequencing;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Persistence.Migrations;
using MarocLIS.Infrastructure.Sequencing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// File numbers and subcontracting slips come from one counter for all sites: the second site's
/// first file took the main site's 2610-000001 and its registration failed.
/// </summary>
public sealed class SharedFileCounterDatabaseTests(ScratchDatabase database) : IClassFixture<ScratchDatabase>
{
    private sealed class FixedClock : MarocLIS.Domain.Common.IClock
    {
        public DateTimeOffset Now => new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
        public DateOnly Today => new(2026, 10, 15);
    }

    private static async Task<string> AllocateAsync(LisDbContext context, SequencePurpose purpose, Guid site, int year)
    {
        var unitOfWork = new UnitOfWork(context);
        await using var transaction = await unitOfWork.BeginTransactionAsync();
        var number = await new SequenceAllocator(context, new FixedClock(), null).NextAsync(purpose, site, year);
        await unitOfWork.CommitAsync();
        return number.Value;
    }

    [SqlServerFact]
    public async Task Two_sites_draw_their_file_and_slip_numbers_from_one_counter()
    {
        await using var context = database.Context();
        Guid main = Guid.NewGuid(), second = Guid.NewGuid();

        (await AllocateAsync(context, SequencePurpose.Accession, main, 2201)).Should().Be("0110-000001");
        (await AllocateAsync(context, SequencePurpose.Accession, second, 2201)).Should().Be("0110-000002");
        (await AllocateAsync(context, SequencePurpose.Accession, main, 2201)).Should().Be("0110-000003");

        (await AllocateAsync(context, SequencePurpose.SubcontractDispatch, second, 2201)).Should().Be("ST/2201/00001");
        (await AllocateAsync(context, SequencePurpose.SubcontractDispatch, main, 2201)).Should().Be("ST/2201/00002");

        // Invoices stay per site.
        (await AllocateAsync(context, SequencePurpose.Receipt, main, 2201)).Should().EndWith("000001");
        (await AllocateAsync(context, SequencePurpose.Receipt, second, 2201)).Should().EndWith("000001");

        (await context.NumberSequences.AsNoTracking()
            .Where(s => s.Year == 2201 && (s.Purpose == SequencePurpose.Accession
                                           || s.Purpose == SequencePurpose.SubcontractDispatch))
            .Select(s => s.SiteId).ToListAsync())
            .Should().HaveCount(2).And.OnlyContain(site => site == null);
    }

    [SqlServerFact]
    public async Task The_migration_continues_after_the_highest_number_any_site_issued()
    {
        Guid main = Guid.NewGuid(), second = Guid.NewGuid();

        await using (var context = database.Context())
        {
            var busy = new NumberSequence(SequencePurpose.Accession, main, 2202, "M{SEQ:6}");
            for (var i = 0; i < 14; i++) busy.Allocate(DateTimeOffset.Now, "A");
            var quiet = new NumberSequence(SequencePurpose.Accession, second, 2202, "Q{SEQ:6}");
            for (var i = 0; i < 3; i++) quiet.Allocate(DateTimeOffset.Now, "B");
            var slips = new NumberSequence(SequencePurpose.SubcontractDispatch, second, 2202, "ST/{YEAR}/{SEQ:5}");
            slips.Allocate(DateTimeOffset.Now, "B");
            var invoices = new NumberSequence(SequencePurpose.Invoice, second, 2202, "FAC/{SITE}/{YEAR}/{SEQ:6}");
            context.NumberSequences.AddRange(busy, quiet, slips, invoices);
            await context.SaveChangesAsync();

            foreach (var sql in new CompteurDossiersCommun().UpOperations.OfType<SqlOperation>())
                await context.Database.ExecuteSqlRawAsync(sql.Sql);
        }

        await using (var context = database.Context())
        {
            var rows = await context.NumberSequences.AsNoTracking().Where(s => s.Year == 2202).ToListAsync();

            rows.Should().ContainSingle(s => s.Purpose == SequencePurpose.Accession)
                .Which.Should().Match<NumberSequence>(s => s.SiteId == null && s.NextValue == 15 && s.Format == "M{SEQ:6}");
            rows.Should().ContainSingle(s => s.Purpose == SequencePurpose.SubcontractDispatch)
                .Which.SiteId.Should().BeNull();
            rows.Should().ContainSingle(s => s.Purpose == SequencePurpose.Invoice)
                .Which.SiteId.Should().Be(second);

            (await AllocateAsync(context, SequencePurpose.Accession, second, 2202)).Should().Be("M000015");
        }
    }
}
