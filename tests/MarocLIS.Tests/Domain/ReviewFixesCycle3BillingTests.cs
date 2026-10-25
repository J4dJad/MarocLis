using System.Linq.Expressions;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Tests.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Patient billing as corrected after the fifth October 2026 review (B1 to B6): the patient pays
/// only their share of a claimed invoice, no credit note over a claim already sent, the billing
/// desk lists the most recent orders, re-invoicing to a payer only under an agreement in force,
/// and a full credit note that frees an order changed since its invoice.
/// </summary>
public sealed class ReviewFixesCycle3BillingTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));

    // ---- B1: the patient pays only their share of a claimed invoice ---------------------------------

    [Theory]
    [InlineData(MarocLIS.Domain.Common.AppLanguage.French, "300,00 DH")]
    [InlineData(MarocLIS.Domain.Common.AppLanguage.English, "MAD")]
    [InlineData(MarocLIS.Domain.Common.AppLanguage.Arabic, "د.م.")]
    public async Task WhatThePatientStillOwes_IsWrittenInTheScreensCurrencyNotation(MarocLIS.Domain.Common.AppLanguage language, string expected)
    {
        var cnss = Payer("CNSS");
        var order = CoveredOrder(cnss, directBilling: true, price: 1_000m, payerShare: 700m);
        var harness = new Harness(orders: [order], payers: [cnss], patients: [PatientOf(order)]);
        (await harness.Service.IssueInvoiceAsync([order.Id])).IsSuccess.Should().BeTrue();
        var invoice = harness.Invoices.Single();

        var error = (await harness.Service.RecordPaymentAsync(
            new RecordPaymentRequest(invoice.Id, 1_000m, PaymentMethod.Card, "AUT-1", null, null, null))).FirstError;

        var localizer = new MarocLIS.Localization.JsonLocalizer(new MarocLIS.Localization.LocalizationOptions { MarkMissingKeys = true });
        var text = localizer.Format(language, error.ResourceKey, error.Args);
        text.Should().Contain(expected).And.NotContain("[");
        if (language != MarocLIS.Domain.Common.AppLanguage.English) text.Should().NotContain("MAD");
    }

    [Fact]
    public async Task OnADirectBilledInvoice_ThePatientPaysOnlyTheirShare_AndThePayerStillCan()
    {
        var cnss = Payer("CNSS");
        var order = CoveredOrder(cnss, directBilling: true, price: 1_000m, payerShare: 700m);
        var harness = new Harness(orders: [order], payers: [cnss], patients: [PatientOf(order)]);

        (await harness.Service.IssueInvoiceAsync([order.Id])).IsSuccess.Should().BeTrue();
        var invoice = harness.Invoices.Single();
        invoice.ClaimStatus.Should().Be(ClaimStatus.Prepared);
        invoice.PatientPayableAmount.Should().Be(300m);

        var excess = await harness.Service.RecordPaymentAsync(
            new RecordPaymentRequest(invoice.Id, 1_000m, PaymentMethod.Card, "AUT-1", null, null, null));

        excess.FirstError.ResourceKey.Should().Be("Payment_ExceedsPatientShare");
        excess.FirstError.Args.Single().Should().Be(MarocLIS.Domain.Common.Money.Of(300m));

        (await harness.Service.RecordPaymentAsync(
            new RecordPaymentRequest(invoice.Id, 300m, PaymentMethod.Card, "AUT-2", null, null, null)))
            .IsSuccess.Should().BeTrue();

        invoice.PatientPayableAmount.Should().Be(0m);
        invoice.OutstandingAmount.Should().Be(700m);

        // The payer's own settlement still fits.
        invoice.RecordPayment(new Payment(700m, PaymentMethod.ThirdPartyPayer, Today, Guid.NewGuid(), invoice.SiteId))
            .IsSuccess.Should().BeTrue();
        invoice.OutstandingAmount.Should().Be(0m);
    }

    [Fact]
    public void OnceThePayerHasAnswered_ItsShortfallIsThePatients()
    {
        Invoice.ComputePatientPayable(1_000m, ClaimStatus.NotApplicable, 700m, null).Should().Be(1_000m);
        Invoice.ComputePatientPayable(1_000m, ClaimStatus.Submitted, 700m, null).Should().Be(300m);
        Invoice.ComputePatientPayable(200m, ClaimStatus.Prepared, 700m, null).Should().Be(0m);
        // The payer accepted 500 of its 700, paid them, and left the rest to the patient.
        Invoice.ComputePatientPayable(500m, ClaimStatus.PartiallyAccepted, 700m, 500m).Should().Be(500m);

        var row = new InvoiceRow(Guid.NewGuid(), "FAC/CASA/2026/000001", InvoiceKind.Invoice, InvoiceStatus.Issued,
                                 Today, Today, "CNSS", 1_000m, 0m, 1_000m, 0, ClaimStatus.Prepared, null,
                                 PayerShare: 700m);
        row.PatientPayable.Should().Be(300m);
    }

    // ---- B2: no credit note over a claim already sent to the payer ----------------------------------

    [Fact]
    public async Task ACreditNote_IsRefusedOnceTheClaimIsSent_AndAcceptedAfterARejection()
    {
        var cnss = Payer("CNSS");
        var order = CoveredOrder(cnss, directBilling: true, price: 1_000m, payerShare: 700m);
        var harness = new Harness(orders: [order], payers: [cnss], patients: [PatientOf(order)]);

        (await harness.Service.IssueInvoiceAsync([order.Id])).IsSuccess.Should().BeTrue();
        var invoice = harness.Invoices.Single();

        foreach (var status in new[] { ClaimStatus.Submitted, ClaimStatus.Resubmitted, ClaimStatus.Accepted,
                                       ClaimStatus.PartiallyAccepted, ClaimStatus.Settled })
        {
            invoice.ClaimStatus = status;

            var refused = await harness.Service.IssueCreditNoteAsync(invoice.Id, "Erreur de saisie");

            refused.FirstError.ResourceKey.Should().Be("Invoice_CreditNoteClaimSent");
            refused.FirstError.Args.Single().Should().Be(invoice.Number);
        }

        harness.Invoices.Should().ContainSingle();
        order.InvoiceId.Should().Be(invoice.Id);

        invoice.ClaimStatus = ClaimStatus.Rejected;

        (await harness.Service.IssueCreditNoteAsync(invoice.Id, "Erreur de saisie")).IsSuccess.Should().BeTrue();
        order.InvoiceId.Should().BeNull();
    }

    // ---- B4: re-invoicing to a payer only under an agreement in force --------------------------------

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, -1)]
    public async Task AnInvoice_IsNotAddressedToAPayerWithoutAValidAgreement(bool agreement, int? validForDays)
    {
        var mutuelle = Payer("MUT");
        mutuelle.ThirdPartyPaymentAgreement = agreement;
        mutuelle.AgreementValidUntil = validForDays is { } days ? Today.AddDays(days) : null;

        var order = CoveredOrder(mutuelle, directBilling: false, price: 1_000m, payerShare: 700m);
        var harness = new Harness(orders: [order], payers: [mutuelle], patients: [PatientOf(order)]);

        var result = await harness.Service.IssueInvoiceAsync([order.Id], payerIdOverride: mutuelle.Id);

        result.FirstError.ResourceKey.Should().Be("Invoice_PayerAgreementNotValid");
        result.FirstError.Args.Single().Should().Be("MUT");
        harness.Invoices.Should().BeEmpty();
        order.InvoiceId.Should().BeNull();
    }

    // ---- B3 and B4: the billing desk lists the most recent orders and the payer's agreement --------

    [SqlServerFact]
    public async Task TheBillingDesk_ListsTheMostRecentOrders_AndWhetherThePayerCanBeBilled()
    {
        using var database = new ScratchDatabase();
        var site = InvoiceableSite();
        var patient = new Patient("MRN-0005", "ALAOUI", "Fatima", new DateOnly(1980, 1, 1), Gender.Female, site.Id);
        var department = new Department("BIO", new MultilingualText("Biochimie"));
        var specimen = new SpecimenType("SER", new MultilingualText("Sérum"));
        var analysis = new AnalysisTest("GLU", new MultilingualText("Glycémie"), department.Id, specimen.Id);

        var valid = Payer("CNSS");
        var expired = Payer("CNOPS");
        expired.AgreementValidUntil = Today.AddDays(-1);
        var none = Payer("MUT");
        none.ThirdPartyPaymentAgreement = false;

        // One more than the screen lists, the oldest first.
        var orders = Enumerable.Range(0, IBillingQueries.UninvoicedOrdersLimit + 1)
            .Select(n => OrderAt(site, patient, analysis, $"2026-{n:000000}", Now.AddMinutes(n - 1_000)))
            .ToList();

        orders[^1].PayerId = valid.Id;
        orders[^2].PayerId = expired.Id;
        orders[^3].PayerId = none.Id;

        // An invoice addressed to the payer whose agreement had expired: not a claim.
        var unclaimable = new Invoice("FAC/CASA/2026/000990", site.Id, InvoiceKind.Invoice, Today, Guid.NewGuid())
        {
            BilledToName = "CNOPS",
            PayerId = expired.Id,
            PatientId = patient.Id
        };
        var line = new InvoiceLine("Analyse", 1_000m);
        line.SetCoverageSplit(0.70m, payerUsesReferenceTariff: false);
        unclaimable.AddLine(line).IsSuccess.Should().BeTrue();
        unclaimable.Issue("FAC/CASA/2026/000990", Now, null).IsSuccess.Should().BeTrue();
        unclaimable.PayerShareAmount.Should().Be(700m);

        await using (var db = database.Context())
        {
            db.Add(site);
            db.Add(patient);
            db.AddRange(department, specimen, analysis);
            db.AddRange(valid, expired, none);
            db.AddRange(orders);
            db.Add(unclaimable);
            await db.SaveChangesAsync();
        }

        await using (var db = database.Context())
        {
            var queries = new BillingQueries(db);
            var rows = await queries.GetUninvoicedOrdersAsync(site.Id);

            rows.Should().HaveCount(IBillingQueries.UninvoicedOrdersLimit);
            rows[0].AccessionNumber.Should().Be(orders[^1].AccessionNumber);
            rows.Should().NotContain(r => r.AccessionNumber == orders[0].AccessionNumber);

            rows[0].PayerAgreementValid.Should().BeTrue();
            rows[1].PayerAgreementValid.Should().BeFalse();
            rows[2].PayerAgreementValid.Should().BeFalse();
            rows[3].PayerAgreementValid.Should().BeFalse();

            (await queries.GetClaimCandidatesAsync(site.Id)).Should().BeEmpty();
        }
    }

    // ---- B5: a full credit note frees an order changed since its invoice ----------------------------

    [SqlServerFact]
    public async Task AFullCreditNote_FreesAnOrderChangedElsewhereSinceItsInvoice()
    {
        using var database = new ScratchDatabase();
        var site = InvoiceableSite();
        var patient = new Patient("MRN-0006", "ALAOUI", "Fatima", new DateOnly(1980, 1, 1), Gender.Female, site.Id);
        var department = new Department("BIO", new MultilingualText("Biochimie"));
        var specimen = new SpecimenType("SER", new MultilingualText("Sérum"));
        var analysis = new AnalysisTest("GLU", new MultilingualText("Glycémie"), department.Id, specimen.Id);
        var order = OrderAt(site, patient, analysis, "2026-000123", Now.AddHours(-4));

        var invoice = new Invoice(string.Empty, site.Id, InvoiceKind.Invoice, Today, Guid.NewGuid())
        {
            BilledToName = "ALAOUI Fatima",
            PatientId = patient.Id
        };
        invoice.AddLine(new InvoiceLine("Analyse", 300m) { LabOrderId = order.Id }).IsSuccess.Should().BeTrue();
        invoice.Issue("FAC/CASA/2026/000123", Now.AddHours(-4), null).IsSuccess.Should().BeTrue();
        order.InvoiceId = invoice.Id;

        await using (var db = database.Context())
        {
            db.Add(site);
            db.Add(patient);
            db.AddRange(department, specimen, analysis);
            db.Add(order);
            db.Add(invoice);
            await db.SaveChangesAsync();
        }

        // The billing screen keeps its context, with the order tracked since the invoice's issue.
        await using var screen = database.Context();
        var clock = Clock();
        var orders = new LabOrderRepository(screen, clock);
        (await orders.GetByIdWithDetailsAsync(order.Id)).Should().NotBeNull();

        // The order changes on another workstation.
        await using (var other = database.Context())
        {
            var changed = await other.LabOrders.SingleAsync(o => o.Id == order.Id);
            changed.ClinicalInformation = "Résultats validés";
            await other.SaveChangesAsync();
        }

        var service = new BillingService(
            new InvoiceRepository(screen, clock), orders,
            Stub<IRepository<Patient>>.Create(), Stub<IRepository<Payer>>.Create(),
            Stub<IRepository<LabSite>>.Create(), Stub<IRepository<AppUser>>.Create(),
            Stub<IRepository<CashSession>>.Create(), Stub<IRepository<Deposit>>.Create(),
            Stub<IBillingQueries>.Create(),
            Stub<ISequenceAllocator>.With(("NextAsync", _ => Task.FromResult(Result.Success("AV/CASA/2026/000001")))),
            new UnitOfWork(screen), Stub<IPermissionGuard>.Create(),
            Stub<ICurrentUser>.With(("get_UserId", _ => Guid.NewGuid()), ("get_SiteId", _ => site.Id)),
            clock);

        var credited = await service.IssueCreditNoteAsync(invoice.Id, "Erreur de destinataire");

        credited.IsSuccess.Should().BeTrue();

        await using (var db = database.Context())
        {
            var saved = await db.LabOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
            saved.InvoiceId.Should().BeNull();
            saved.ClinicalInformation.Should().Be("Résultats validés");
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static LabOrder OrderAt(LabSite site, Patient patient, AnalysisTest analysis, string accession,
                                    DateTimeOffset registeredAt)
    {
        var order = new LabOrder(accession, patient.Id, site.Id, registeredAt, Guid.NewGuid());
        order.AddItem(new LabOrderItem(analysis.Id, OrderPriority.Routine)
        {
            ChargedPrice = 300m,
            PatientShare = 300m
        }).IsSuccess.Should().BeTrue();
        return order;
    }

    private static Payer Payer(string code) =>
        new(code, new MultilingualText(code), PayerKind.AmoCnss)
        {
            ThirdPartyPaymentAgreement = true,
            RequiresPrescription = false
        };

    private static LabOrder CoveredOrder(Payer payer, bool directBilling, decimal price, decimal payerShare,
                                         decimal rate = 0.70m)
    {
        var order = new LabOrder($"2026-{Random.Shared.Next(100_000, 999_999)}", Guid.NewGuid(),
                                 Guid.NewGuid(), Now.AddDays(-3), Guid.NewGuid())
        {
            PayerId = payer.Id,
            ReimbursementRateAtRegistration = rate,
            DirectBillingAtRegistration = directBilling
        };

        order.AddItem(new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine)
        {
            ChargedPrice = price,
            ReferenceTariffBase = price,
            PayerShare = payerShare,
            PatientShare = price - payerShare,
            BilledActCode = "B100"
        }).IsSuccess.Should().BeTrue();

        return order;
    }

    private static Patient PatientOf(LabOrder order)
    {
        var patient = new Patient("MRN-0004", "ALAOUI", "Fatima", new DateOnly(1980, 1, 1), Gender.Female,
                                  order.SiteId);
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(patient, order.PatientId);
        return patient;
    }

    private static LabSite InvoiceableSite() =>
        new("CASA", new MultilingualText("Casablanca"), LabSiteKind.MainLaboratory)
        {
            LegalName = "Laboratoire Test SARL",
            Ice = "003456789000012",
            FiscalId = "40112233",
            AddressLine = new MultilingualText("12 rue Test"),
            City = "Casablanca"
        };

    private static IClock Clock() =>
        Stub<IClock>.With(("get_Now", _ => Now), ("get_Today", _ => Today));

    /// <summary>
    /// A billing service over a few orders and payers; the invoices it issues are found again, so a
    /// payment or a credit note can be recorded on them.
    /// </summary>
    private sealed class Harness
    {
        private int _sequence;

        public Harness(LabOrder[] orders, Payer[] payers, Patient[] patients)
        {
            var user = Guid.NewGuid();
            var siteId = Guid.NewGuid();
            var site = InvoiceableSite();

            Task<Invoice?> FindInvoice(object?[]? args) =>
                Task.FromResult(Invoices.FirstOrDefault(i => i.Id == (Guid)args![0]!));

            Service = new BillingService(
                Stub<IRepository<Invoice>>.With(
                    ("GetByIdWithDetailsAsync", FindInvoice),
                    ("GetByIdAsync", FindInvoice),
                    ("AddAsync", args => { Invoices.Add((Invoice)args![0]!); return Task.CompletedTask; })),
                Stub<IRepository<LabOrder>>.With(
                    ("GetByIdWithDetailsAsync", args =>
                        Task.FromResult(orders.FirstOrDefault(o => o.Id == (Guid)args![0]!))),
                    ("ListAsync", args =>
                    {
                        var predicate = ((Expression<Func<LabOrder, bool>>)args![0]!).Compile();
                        return Task.FromResult<IReadOnlyList<LabOrder>>([.. orders.Where(predicate)]);
                    })),
                Stub<IRepository<Patient>>.With(("GetByIdWithDetailsAsync", args =>
                    Task.FromResult(patients.FirstOrDefault(p => p.Id == (Guid)args![0]!)))),
                Stub<IRepository<Payer>>.With(("GetByIdAsync", args =>
                    Task.FromResult(payers.FirstOrDefault(p => p.Id == (Guid)args![0]!)))),
                Stub<IRepository<LabSite>>.With(("GetByIdAsync", _ => Task.FromResult<LabSite?>(site))),
                Stub<IRepository<AppUser>>.Create(),
                Stub<IRepository<CashSession>>.Create(),
                Stub<IRepository<Deposit>>.With(
                    ("ListAsync", _ => Task.FromResult<IReadOnlyList<Deposit>>([]))),
                Stub<IBillingQueries>.Create(),
                Stub<ISequenceAllocator>.With(
                    ("NextAsync", _ => Task.FromResult(Result.Success($"FAC/CASA/2026/{++_sequence:000000}")))),
                Stub<IUnitOfWork>.Create(),
                Stub<IPermissionGuard>.Create(),
                Stub<ICurrentUser>.With(
                    ("get_UserId", _ => user),
                    ("get_SiteId", _ => siteId)),
                Clock());
        }

        public BillingService Service { get; }

        public List<Invoice> Invoices { get; } = [];
    }

    private sealed class Stub<T> : ReviewFixesBilling2Tests.Stub<T> where T : class;

    /// <summary>
    /// A database of its own, <c>MarocLIS_FixCycle3Billing</c>, created from the model and dropped at
    /// the end. An existing database of that name is refused rather than reused.
    /// </summary>
    private sealed class ScratchDatabase : IDisposable
    {
        private const string Name = "MarocLIS_FixCycle3Billing";
        private readonly string _connectionString;

        public ScratchDatabase()
        {
            _connectionString = new SqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable)) { InitialCatalog = Name }
                .ConnectionString;

            using var context = Context();
            if (context.Database.CanConnect())
                throw new InvalidOperationException($"Database {Name} already exists.");

            context.Database.EnsureCreated();
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
