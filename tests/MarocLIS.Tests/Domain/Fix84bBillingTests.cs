using System.Linq.Expressions;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.Input;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Pricing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Lot fix84b, billing: the VAT charged at registration is the VAT invoiced, and a tariff's rate is
/// typed in percent and bounded.
/// </summary>
public sealed class Fix84bBillingTests
{
    private static readonly DateOnly Today = new(2026, 10, 27);
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 10, 0, 0, TimeSpan.FromHours(1));

    // ---- VAT on a taxable tariff -------------------------------------------------------------------

    [Fact]
    public void A_taxable_act_is_frozen_on_the_order_with_its_VAT()
    {
        var pricing = ActPricingCalculator.Price(
        [
            new ActPricingInput
            {
                AnalysisTestId = Guid.NewGuid(), AnalysisCode = "CERT", Description = "Certificat",
                Quantity = 1, ChargedUnitPrice = 100m, IsVatExempt = false, VatRate = 0.20m,
                IsChargeable = true, ServiceDate = Today
            }
        ], CoverageContext.SelfPay);

        var analysis = new AnalysisTest("CERT", new MultilingualText("Certificat"), Guid.NewGuid(), Guid.NewGuid());
        var item = OrderComposer.BuildItem(pricing.Acts[0], new OrderComposer.SelectedAnalysis(analysis, null),
                                           OrderPriority.Routine);

        item.ChargedPrice.Should().Be(100m);
        item.PatientShare.Should().Be(120m);
        item.IsVatExempt.Should().BeFalse();
        item.VatRate.Should().Be(0.20m);
    }

    [Fact]
    public async Task The_invoice_line_carries_the_VAT_the_counter_charged()
    {
        var order = SelfPayOrder(price: 100m, vatExempt: false, vatRate: 0.20m, patientShare: 120m);
        var harness = new Harness(orders: [order], patients: [PatientOf(order)]);

        var issued = await harness.Service.IssueInvoiceAsync([order.Id]);

        issued.IsSuccess.Should().BeTrue(string.Join(", ", issued.Errors.Select(e => e.ResourceKey)));
        var invoice = harness.Invoices.Single();
        var line = invoice.Lines.Single();
        line.IsVatExempt.Should().BeFalse();
        line.VatRate.Should().Be(0.20m);
        line.VatAmount.Should().Be(20m);
        invoice.NetAmount.Should().Be(100m);
        invoice.VatAmount.Should().Be(20m);
        invoice.TotalAmount.Should().Be(120m);
        invoice.PatientShareAmount.Should().Be(order.Items.Sum(i => i.PatientShare),
            "the invoice asks the patient for what the counter announced");
    }

    [Fact]
    public async Task An_exempt_analysis_stays_exempt_on_the_invoice()
    {
        var order = SelfPayOrder(price: 30m, vatExempt: true, vatRate: 0m, patientShare: 30m);
        var harness = new Harness(orders: [order], patients: [PatientOf(order)]);

        (await harness.Service.IssueInvoiceAsync([order.Id])).IsSuccess.Should().BeTrue();

        var line = harness.Invoices.Single().Lines.Single();
        line.IsVatExempt.Should().BeTrue();
        line.VatAmount.Should().Be(0m);
        harness.Invoices.Single().TotalAmount.Should().Be(30m);
    }

    [Fact]
    public void The_tariff_VAT_rate_is_typed_in_percent_and_bounded()
    {
        var form = new TariffForm { Price = "100", LetterKey = "B", VatExempt = false, VatRate = "20" };

        CatalogEditorRules.Check(form).Should().BeNull();
        CatalogEditorRules.ToRequest(form, Guid.NewGuid()).VatRate.Should().Be(0.20m);
        CatalogEditorRules.ToRequest(form with { VatRate = "5,5" }, Guid.NewGuid()).VatRate.Should().Be(0.055m);

        CatalogEditorRules.Check(form with { VatRate = "0" })
            .Should().Be(new FieldRefusal("vatrate", "Tariff_VatRateRequired"));
        CatalogEditorRules.Check(form with { VatRate = "120" })
            .Should().Be(new FieldRefusal("vatrate", "Tariff_VatRateOutOfRange"));

        // Read back as it was typed: a stored 0.20 opens as « 20 ».
        var analysis = MarocLIS.Tests.Desktop.CatalogScreenRulesTests.Analysis(code: "CERT", price: 100m);
        var stored = new TariffRow(Guid.NewGuid(), analysis.Id, null, null, Today.AddYears(-1), null, "B", 0m, 1m,
                                   100m, false, 0.20m);
        CatalogEditorRules.FormOf(analysis, [stored], Today, stored, null).VatRate.Should().Be("20");
    }

    // ---- Helpers -------------------------------------------------------------------------------------

    private static LabOrder SelfPayOrder(decimal price, bool vatExempt, decimal vatRate, decimal patientShare)
    {
        var order = new LabOrder($"2026-{Random.Shared.Next(100_000, 999_999)}", Guid.NewGuid(),
                                 Guid.NewGuid(), Now.AddDays(-1), Guid.NewGuid());

        order.AddItem(new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine)
        {
            ChargedPrice = price,
            ReferenceTariffBase = 0m,
            PayerShare = 0m,
            PatientShare = patientShare,
            IsVatExempt = vatExempt,
            VatRate = vatRate
        }).IsSuccess.Should().BeTrue();

        return order;
    }

    private static Patient PatientOf(LabOrder order)
    {
        var patient = new Patient("MRN-84B", "ALAOUI", "Fatima", new DateOnly(1980, 1, 1), Gender.Female,
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
    /// A billing service over a few orders; the invoices it issues and the deposits it takes are
    /// kept, so a credit note can be issued on them and the next invoice finds the deposits.
    /// </summary>
    private sealed class Harness
    {
        private int _sequence;

        public Harness(LabOrder[] orders, Patient[] patients, Payer[]? payers = null)
        {
            var user = Guid.NewGuid();
            var siteId = Guid.NewGuid();
            var site = InvoiceableSite();
            payers ??= [];

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
                    ("ListAsync", args =>
                    {
                        var predicate = ((Expression<Func<Deposit, bool>>)args![0]!).Compile();
                        return Task.FromResult<IReadOnlyList<Deposit>>([.. Deposits.Where(predicate)]);
                    }),
                    ("AddAsync", args => { Deposits.Add((Deposit)args![0]!); return Task.CompletedTask; })),
                Stub<IBillingQueries>.Create(),
                Stub<ISequenceAllocator>.With(
                    ("NextAsync", _ => Task.FromResult(Result.Success($"N/CASA/2026/{++_sequence:000000}")))),
                Stub<IUnitOfWork>.Create(),
                Stub<IPermissionGuard>.Create(),
                Stub<ICurrentUser>.With(
                    ("get_UserId", _ => user),
                    ("get_SiteId", _ => siteId)),
                Clock());
        }

        public BillingService Service { get; }

        public List<Invoice> Invoices { get; } = [];

        public List<Deposit> Deposits { get; } = [];
    }

    private sealed class Stub<T> : ReviewFixesBilling2Tests.Stub<T> where T : class;
}
