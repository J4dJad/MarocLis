using FluentAssertions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Organization;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// One issued invoice, read back from SQL Server and printed in both formats: the coded format reads
/// the act code, letter-key and coefficient frozen on the lines, and every amount it states is the
/// invoice's own — the same figures the standard format prints.
/// </summary>
public sealed class FactureCotationDatabaseTests
{
    private static readonly DateOnly Today = new(2026, 10, 16);
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 10, 0, 0, TimeSpan.FromHours(1));

    [SqlServerFact]
    public async Task BothFormats_OfOneInvoice_StateTheSameAmounts()
    {
        using var database = new ScratchDatabase();
        var siteId = Guid.NewGuid();

        var invoice = new Invoice(string.Empty, siteId, InvoiceKind.Invoice, Today, Guid.NewGuid())
        {
            BilledToName = "EL ALAOUI Fatima"
        };

        InvoiceLine Coded(string code, string label, string letter, decimal? coefficient, decimal price) =>
            new(label, price)
            {
                AnalysisCode = code == "PC" ? "PDS" : "ECBU",
                NationalActCode = code,
                LetterKey = letter,
                Coefficient = coefficient,
                // Coefficient × letter value (1,00 DH) as the order froze it.
                ReferenceTariffBase = coefficient ?? 0m
            };

        invoice.AddLine(Coded("B241", "ECBU EXAMEN CYTOBACTERIOLOGIQUE DES URINES", "B", 90m, 72m)).IsSuccess.Should().BeTrue();
        invoice.AddLine(Coded("B242", "ANTIBIOGRAMME (CLASSEMENT PAR FAMILLE)", "B", 60m, 48m)).IsSuccess.Should().BeTrue();
        invoice.AddLine(Coded("PC", "Prise de sang", "PC", null, 20m)).IsSuccess.Should().BeTrue();
        invoice.AddLine(new InvoiceLine("Certificat", 30m)).IsSuccess.Should().BeTrue();
        invoice.ApplyDiscount(10m, "Geste commercial").IsSuccess.Should().BeTrue();
        invoice.Issue("FAC/CASA/2026/000900", Now, AmountInWords.Render(invoice.TotalAmount, AppLanguage.French))
               .IsSuccess.Should().BeTrue();
        invoice.RecordPayment(new Payment(100m, PaymentMethod.Cash, Today, Guid.NewGuid(), siteId))
               .IsSuccess.Should().BeTrue();

        await using (var db = database.Context())
        {
            db.Invoices.Add(invoice);
            db.Settings.Add(new AppSetting(InvoicePrintSettings.DefaultFormatKey, "CotationB"));
            db.Settings.Add(new AppSetting(InvoicePrintSettings.CotationLinePriceKey, "true"));
            await db.SaveChangesAsync();
        }

        await using (var db = database.Context())
        {
            var document = await new BillingQueries(db).GetInvoiceDocumentAsync(invoice.Id);
            document.Should().NotBeNull();

            var settings = InvoicePrintSettings.FromValues(
                await new InvoicePrintSettingsQueries(db).GetSettingValuesAsync(InvoicePrintSettings.Prefix));
            settings.DefaultFormat.Should().Be(InvoicePrintFormat.CotationB);
            settings.CotationShowLinePrice.Should().BeTrue();

            // The codes frozen on the lines, the letter value read back from the frozen base.
            var ecbu = document!.Lines.Single(l => l.NationalActCode == "B241");
            ecbu.LetterKey.Should().Be("B");
            ecbu.Coefficient.Should().Be(90m);
            ecbu.LetterKeyValue.Should().Be(1m);

            var layout = CotationInvoiceLayout.Build(document, settings, AppLanguage.French);

            layout.LetterTotals.Should().Equal(new CotationLetterTotal("B", 150m));
            layout.Rows.Should().HaveCount(3, "the two acts and the certificate; the fee has its own line");

            // Every dirham of the invoice is on the coded invoice, discount included: the acts and the
            // fee add up to the net the standard format prints, and « Montant en DH » is its total.
            (layout.Rows.Sum(r => r.Amount) + layout.CollectionFee!.Value)
                .Should().Be(document.Lines.Sum(l => l.NetAmount)).And.Be(document.NetAmount);
            layout.CollectionFee.Should().Be(document.Lines.Single(l => l.LetterKey == "PC").NetAmount);
            document.TotalAmount.Should().Be(160m);
            document.OutstandingAmount.Should().Be(60m);
            layout.Words.Should().Be(AmountInWords.Render(document.TotalAmount, AppLanguage.French)
                                                  .ToUpper(AppLanguage.French.ToCulture()));

            var renderer = new QuestPdfInvoiceRenderer(new ReportingOptions
            {
                FontDirectory = Path.Combine(AppContext.BaseDirectory, "no-fonts-here")
            }, new JsonLocalizer(new LocalizationOptions()));

            foreach (var format in new[] { InvoicePrintFormat.Standard, InvoicePrintFormat.CotationB })
                renderer.RenderInvoice(document, AppLanguage.French, format, settings).Should().NotBeEmpty();
        }
    }

    /// <summary>
    /// A database of its own, <c>MarocLIS_FactureCotation</c>, brought up by migration and dropped at
    /// the end. An existing database of that name is refused rather than reused.
    /// </summary>
    private sealed class ScratchDatabase : IDisposable
    {
        private const string Name = "MarocLIS_FactureCotation";
        private readonly string _connectionString;

        public ScratchDatabase()
        {
            _connectionString = new SqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable)) { InitialCatalog = Name }
                .ConnectionString;

            using var context = Context();
            if (context.Database.CanConnect())
                throw new InvalidOperationException($"Database {Name} already exists.");

            context.Database.Migrate();
        }

        public LisDbContext Context() =>
            new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(_connectionString).Options);

        public void Dispose()
        {
            SqlConnection.ClearAllPools();
            using var context = Context();
            context.Database.EnsureDeleted();
        }
    }
}
