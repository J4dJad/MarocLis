using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Orders;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// Defect-hunt cycle 5, lot c5d, the front-desk path on SQL Server with the two invoice formats: a
/// request with a coded act, an act priced in dirhams only and an insurer, the catalogue changed
/// after registration, the invoice paid in cash and by card, the print format chosen in Paramètres
/// (by whom, and traced), the invoice printed in both formats from what was frozen, and a credit
/// note that keeps the standard format.
/// </summary>
public sealed class Cycle5FrontDeskDatabaseTests(OrderEntryScratchDatabase database)
    : IClassFixture<OrderEntryScratchDatabase>
{
    private sealed class Operator(Guid siteId, IReadOnlySet<string>? permissions = null) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "accueil";
        public string FullName => "Accueil";
        public Guid SiteId => siteId;
        public string SiteCode { get; } = "S" + ((uint)siteId.GetHashCode()).ToString("X8");
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.None;
        public IReadOnlyList<Guid> AccessibleSiteIds => [siteId];
        public IReadOnlySet<string> Permissions { get; } = permissions ?? new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => permissions is null || permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "ACCUEIL-1";
    }

    private static readonly HashSet<string> Receptionist =
    [
        Permissions.Patients.View, Permissions.Patients.Create, Permissions.Patients.Edit,
        Permissions.Orders.View, Permissions.Orders.Create, Permissions.Orders.Edit,
        Permissions.Orders.Cancel, Permissions.Billing.View, Permissions.Billing.CreateInvoice,
        Permissions.Billing.RecordPayment, Permissions.Billing.CloseCashSession
    ];

    private static readonly HashSet<string> Accountant =
    [
        Permissions.Patients.View, Permissions.Orders.View, Permissions.Billing.View,
        Permissions.Billing.CreateInvoice, Permissions.Billing.CreditNote, Permissions.Billing.RecordPayment
    ];

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private ServiceProvider Services(Guid siteId, IReadOnlySet<string>? permissions = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = database.ConnectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(siteId, permissions));
        services.AddIsolatedQueries<IBillingQueries, MarocLIS.Infrastructure.Queries.BillingQueries>();
        services.AddIsolatedQueries<IOrderQueries, MarocLIS.Infrastructure.Queries.OrderQueries>();
        services.AddIsolatedQueries<IInvoicePrintSettingsQueries, MarocLIS.Infrastructure.Queries.InvoicePrintSettingsQueries>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> On<TService, T>(ServiceProvider provider, Func<TService, Task<T>> action)
        where TService : notnull
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static string Keys(IEnumerable<Error> errors) => string.Join(", ", errors.Select(e => e.ResourceKey));

    private sealed record Lab(Guid SiteId, Patient Insured, AnalysisTest Glucose, AnalysisTest Nfs, AnalysisTest Fee);

    private async Task<Lab> LabAsync()
    {
        var validFrom = DateOnly.FromDateTime(DateTime.Today).AddYears(-1);

        var site = new LabSite(Unique("S"), new MultilingualText("Principal"), LabSiteKind.MainLaboratory)
        {
            LegalName = "Laboratoire Test SARL",
            Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
            FiscalId = "40112233",
            AddressLine = new MultilingualText("12 rue Test"),
            City = "Casablanca"
        };

        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sang"));
        var tube = new ContainerType(Unique("SEC"), new MultilingualText("Tube sec"), "#C8A000");

        // A coded act priced on its coefficient (B × 1,10 DH), or an act priced in dirhams only.
        AnalysisTest Analysis(string code, string? nationalCode, decimal coefficient, decimal price)
        {
            var analysis = new AnalysisTest(Unique(code), new MultilingualText(code, null, "تحليل " + code),
                                            department.Id, specimen.Id)
            {
                ContainerTypeId = tube.Id,
                RoutineTurnaroundHours = 4,
                NationalActCode = nationalCode
            };
            var parameter = new TestParameter(Unique(code), new MultilingualText(code), ResultValueType.Numeric);
            parameter.AddReferenceRange(ReferenceRange.Numeric(1m, 100m));
            analysis.AddParameter(parameter);
            analysis.AddTariff(new AnalysisTestTariff(analysis.Id, payerId: null, validFrom)
            {
                LetterKey = "B", Coefficient = coefficient, LetterKeyValue = coefficient > 0m ? 1.10m : 0m,
                Price = price, IsVatExempt = true
            });
            return analysis;
        }

        var glucose = Analysis("GLY", "B118", 30m, 33m);
        var nfs = Analysis("NFS", "B216", 50m, 55m);
        var fee = Analysis("FRAIS", null, 0m, 20m);

        var amo = new Payer(Unique("AMO"), new MultilingualText("AMO — CNSS"), PayerKind.AmoCnss)
        {
            DefaultReimbursementRate = 0.70m,
            ThirdPartyPaymentAgreement = true,
            RequiresPrescription = false,
            RequiresInpeOnClaim = false
        };

        var insured = new Patient(Unique("P"), "SQUALLI", "Hamza", new DateOnly(1986, 2, 1), Gender.Male, site.Id);
        insured.AddCoverage(new PatientCoverage(insured.Id, amo.Id, "123456789", validFrom));

        await using var context = database.Context();
        context.Add(site);
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.ContainerTypes.Add(tube);
        context.AnalysisTests.AddRange(glucose, nfs, fee);
        context.Payers.Add(amo);
        context.Patients.Add(insured);
        await context.SaveChangesAsync();

        return new Lab(site.Id, insured, glucose, nfs, fee);
    }

    private static QuestPdfInvoiceRenderer Renderer() =>
        new(new ReportingOptions { FontDirectory = Path.Combine(AppContext.BaseDirectory, "no-fonts-here") },
            new JsonLocalizer(new LocalizationOptions()));

    [SqlServerFact]
    public async Task ACodedRequest_PaidInCashAndByCard_PrintsInBothFormats_WhatWasFrozenAtRegistration()
    {
        var lab = await LabAsync();
        using var reception = Services(lab.SiteId, Receptionist);
        using var accounts = Services(lab.SiteId, Accountant);
        using var admin = Services(lab.SiteId);

        var registered = await On<OrderRegistrationService, Result<RegisteredOrder>>(reception, s => s.RegisterAsync(
            new RegisterOrderRequest
            {
                PatientId = lab.Insured.Id,
                AnalysisTestIds = [lab.Glucose.Id, lab.Nfs.Id, lab.Fee.Id],
                PrescriberFreeText = "Dr BENNIS Karim"
            }));
        registered.IsSuccess.Should().BeTrue(Keys(registered.Errors));

        // The catalogue changes after the counter: another act code, another coefficient. The
        // invoice, and both its prints, keep what the request was registered with.
        await using (var context = database.Context())
        {
            var glucose = await context.AnalysisTests.Include(a => a.Tariffs).SingleAsync(a => a.Id == lab.Glucose.Id);
            glucose.NationalActCode = "B119";
            glucose.Tariffs.Single().Coefficient = 60m;
            glucose.Tariffs.Single().Price = 66m;
            await context.SaveChangesAsync();
        }

        var issued = await On<BillingService, Result<Guid>>(reception, s => s.IssueInvoiceAsync([registered.Value.OrderId]));
        issued.IsSuccess.Should().BeTrue(Keys(issued.Errors));
        var invoiceId = issued.Value;

        // The patient's share, part in cash at the till, the rest by card.
        (await On<BillingService, Result<Guid>>(reception, s => s.OpenCashSessionAsync(100m))).IsSuccess.Should().BeTrue();
        var document = (await On<BillingService, Result<InvoiceDocument>>(reception, s => s.GetInvoiceDocumentAsync(invoiceId))).Value;
        document.TotalAmount.Should().Be(108m);
        var patientShare = document.PatientShareAmount;
        patientShare.Should().BeGreaterThan(10m);

        (await On<BillingService, Result>(reception, s => s.RecordPaymentAsync(
            new RecordPaymentRequest(invoiceId, 10m, PaymentMethod.Cash, null, null, null, null)))).IsSuccess.Should().BeTrue();
        (await On<BillingService, Result>(reception, s => s.RecordPaymentAsync(
            new RecordPaymentRequest(invoiceId, patientShare - 10m, PaymentMethod.Card, "AUTH-4521", null, null, null))))
            .IsSuccess.Should().BeTrue();
        var till = (await On<BillingService, Result<CashSessionSummary>>(reception, s => s.GetOpenCashSessionAsync())).Value;
        till.CashReceipts.Should().Be(10m);
        till.CardReceipts.Should().Be(patientShare - 10m);

        document = (await On<BillingService, Result<InvoiceDocument>>(reception, s => s.GetInvoiceDocumentAsync(invoiceId))).Value;
        document.OutstandingAmount.Should().Be(document.PayerShareAmount, "what is left is the insurer's");

        var glucoseLine = document.Lines.Single(l => l.Code == lab.Glucose.Code);
        glucoseLine.NationalActCode.Should().Be("B118");
        glucoseLine.Coefficient.Should().Be(30m);
        glucoseLine.LetterKeyValue.Should().Be(1.10m);
        glucoseLine.NetAmount.Should().Be(33m);

        // The format: read by the counter, chosen only with the right to manage settings, traced.
        (await On<InvoicePrintSettingsService, Result<InvoicePrintSettings>>(reception, s => s.GetAsync()))
            .Value.DefaultFormat.Should().Be(InvoicePrintFormat.Standard);
        var cotation = InvoicePrintSettings.Default with { DefaultFormat = InvoicePrintFormat.CotationB };
        (await On<InvoicePrintSettingsService, Result>(reception, s => s.SaveAsync(cotation)))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
        (await On<InvoicePrintSettingsService, Result>(admin, s => s.SaveAsync(cotation))).IsSuccess.Should().BeTrue();
        (await On<InvoicePrintSettingsService, Result>(admin, s => s.SaveAsync(cotation))).IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
        {
            var traced = await context.AuditEntries.AsNoTracking()
                .Where(a => a.EntityName == nameof(AppSetting) && a.Reason != null
                            && a.Reason.StartsWith("Format d'impression des factures"))
                .ToListAsync();
            traced.Should().NotBeEmpty();
            traced.Should().OnlyContain(a => a.Reason!.Contains(InvoicePrintSettings.DefaultFormatKey)
                                             || traced.Count > 1, "the second save changed nothing and is not traced");
            traced.Select(a => a.OccurredAt).Distinct().Should().HaveCount(1, "only the save that changed something is traced");
        }

        var settings = (await On<InvoicePrintSettingsService, Result<InvoicePrintSettings>>(reception, s => s.GetAsync())).Value;
        settings.DefaultFormat.Should().Be(InvoicePrintFormat.CotationB);

        // The coded invoice: the frozen code and coefficient; the act priced in dirhams only shows
        // its price, not « 0 » in B, and so does every line once one line has no coefficient.
        var layout = CotationInvoiceLayout.Build(document, settings, AppLanguage.French);
        layout.Rows.Single(r => r.Description == lab.Glucose.Name.Fr).Should()
              .Match<CotationInvoiceRow>(r => r.Code == "B118" && r.Coefficient == 30m && r.IsBiologyAct);
        var fee = layout.Rows.Single(r => r.Description == lab.Fee.Name.Fr);
        fee.Coefficient.Should().BeNull();
        fee.IsBiologyAct.Should().BeFalse();
        fee.Amount.Should().Be(20m);
        layout.ShowLinePrice.Should().BeTrue();
        layout.LetterTotals.Should().Equal(new CotationLetterTotal("B", 80m));
        layout.Rows.Sum(r => r.Amount).Should().Be(document.TotalAmount);

        var renderer = Renderer();
        foreach (var language in new[] { AppLanguage.French, AppLanguage.Arabic })
        foreach (var format in new[] { InvoicePrintFormat.Standard, settings.DefaultFormat })
            renderer.RenderInvoice(document, language, format, settings).Should().NotBeEmpty();

        // A partial credit note: printed in the standard format whatever the setting says, and the
        // invoice still prints as it was issued.
        var credited = await On<BillingService, Result<Guid>>(accounts, s => s.IssueCreditNoteAsync(invoiceId, "Frais de dossier offerts", 20m));
        credited.IsSuccess.Should().BeTrue(Keys(credited.Errors));
        var creditNote = (await On<BillingService, Result<InvoiceDocument>>(accounts, s => s.GetInvoiceDocumentAsync(credited.Value))).Value;
        creditNote.Kind.Should().Be(InvoiceKind.CreditNote);
        InvoicePrintSettings.EffectiveFormat(creditNote.Kind, settings.DefaultFormat).Should().Be(InvoicePrintFormat.Standard);
        renderer.RenderInvoice(creditNote, AppLanguage.French, settings.DefaultFormat, settings).Should().NotBeEmpty();

        var reprinted = (await On<BillingService, Result<InvoiceDocument>>(reception, s => s.GetInvoiceDocumentAsync(invoiceId))).Value;
        CotationInvoiceLayout.Build(reprinted, settings, AppLanguage.French).LetterTotals
            .Should().Equal(new CotationLetterTotal("B", 80m));
        reprinted.TotalAmount.Should().Be(108m);

        // Back to the format the laboratory had, for the next test of the class.
        (await On<InvoicePrintSettingsService, Result>(admin, s => s.SaveAsync(InvoicePrintSettings.Default))).IsSuccess.Should().BeTrue();
    }
}
