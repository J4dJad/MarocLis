using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Web.Components.Screens.Purchasing;
using MarocLIS.Web.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Factures fournisseurs » in the browser: an overdue invoice in the alarm's colours with the
/// due-date dashboard in dirhams, the variances of the three-way match said with their prices, an
/// invoice its author cannot release while another approver does with a justification kept, a bill
/// of exchange refused without its due date, an entry without its number and a period refused in their box, and each
/// operation kept by its right — through the workstation's SupplierInvoiceService.
/// </summary>
public sealed class WebSupplierInvoiceTests : WebScreenContext
{
    private readonly Fake<ISupplierInvoiceQueries> _queries = Fake<ISupplierInvoiceQueries>.Create();
    private readonly Fake<IRepository<SupplierInvoice>> _invoices = Fake<IRepository<SupplierInvoice>>.Create();
    private readonly Fake<IUnitOfWork> _work = Fake<IUnitOfWork>.Create();

    private static readonly Guid Biosystems = Guid.NewGuid();

    public WebSupplierInvoiceTests()
    {
        Services.AddSingleton(_queries.Object);
        Services.AddSingleton(_invoices.Object);
        Services.AddSingleton(_work.Object);
        Services.AddSingleton(Fake<IRepository<SupplierInvoiceFile>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<Supplier>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<LatePaymentDeclaration>>.Create().Object);
        Services.AddSingleton(Fake<IConfigurationWriter>.Create().Object);
        Services.AddScoped<SupplierInvoiceService>();
    }

    private static readonly string[] Accountant = [Permissions.Inventory.View, Permissions.Inventory.EnterInvoices, Permissions.Inventory.ApprovePayments];

    private string? AuditReason => _work.Calls.LastOrDefault(c => c.Method == "set_AuditReason").Arguments is [string reason] ? reason : null;

    private Guid UserId => Services.GetRequiredService<SessionContext>().User.UserId;

    private static SupplierInvoiceRow Row(string number, SupplierInvoiceStatus status, int daysUntilDue = 20, Guid? id = null,
                                          Guid? enteredBy = null, int variances = 0) =>
        new(id ?? Guid.NewGuid(), Biosystems, "Biosystems Maroc", number, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 3),
            new DateOnly(2026, 8, 28), new DateOnly(2026, 10, 18).AddDays(daysUntilDue), daysUntilDue, status, 1000m, 1200m, 0m,
            variances, false, "Nadia Alaoui", enteredBy ?? Guid.NewGuid());

    private void Invoices(params SupplierInvoiceRow[] rows)
    {
        _queries.On(nameof(ISupplierInvoiceQueries.GetInvoicesAsync), _ => rows);
        _queries.On(nameof(ISupplierInvoiceQueries.GetDetailAsync), c =>
        {
            var row = rows.Single(r => r.Id == (Guid)c[0]!);
            return new SupplierInvoiceDetail(row,
                [new SupplierInvoiceLineRow(1, "BC/2026/00010", "BIO-GLU", "Glucose GOD-PAP 4×100 mL", 10m, 105m, 0.20m, 1050m)],
                row.VarianceCount > 0 ? [new SupplierInvoiceVarianceRow(1, VarianceKind.PriceDiffers, 100m, 105m)] : [],
                [], 60, null, null, null, null, null);
        });
    }

    /// <summary>An invoice keyed by someone, its line priced above the order: blocked.</summary>
    private static SupplierInvoice Blocked(Guid enteredBy)
    {
        var invoice = new SupplierInvoice(Biosystems, Guid.NewGuid(), "FA-2026-0418", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 3), enteredBy);
        invoice.AddLine(new SupplierInvoiceLine(Guid.NewGuid(), Guid.NewGuid(), "Glucose", 10m, 105m, 0.20m)).IsSuccess.Should().BeTrue();
        invoice.SetPaymentTerms(new DateOnly(2026, 8, 28), 60);
        invoice.ApplyMatch([new MatchVariance(1, VarianceKind.PriceDiffers, 100m, 105m)]).IsSuccess.Should().BeTrue();
        return invoice;
    }

    [Fact]
    public void AnOverdueInvoice_IsInTheAlarmsColours_WithTheDueDatesInDirhams_AndItsVariancesSaid()
    {
        SignIn(AppLanguage.French, Accountant);
        var overdue = Row("FA-2026-0391", SupplierInvoiceStatus.ApprovedForPayment, daysUntilDue: -5);
        Invoices(overdue, Row("FA-2026-0418", SupplierInvoiceStatus.Blocked, variances: 1));

        var page = Render<SupplierInvoices>();

        page.Find("[data-grid=invoices] [data-row='FA-2026-0391']").ClassName.Should().Contain("stock-alarm");
        page.Find("[data-grid=invoices] [data-row='FA-2026-0391']").TextContent.Should().Contain("+5 j");
        var summary = page.Find("[data-state=due-summary]");
        summary.ClassName.Should().Contain("critical");
        Flat(summary.TextContent).Should().StartWith("En retard : 1 (1 200,00 DH)").And.EndWith("Bloquées : 1");
        page.Find("[data-chip=Overdue]").TextContent.Should().Be("En retard · 1");
        Flat(page.Find("[data-state=detail-info]").TextContent).Should().Contain("Total TTC 1 200,00 DH").And.Contain("Délai 60 jours");

        page.Find("[data-grid=invoices] [data-row='FA-2026-0418']").Click();
        page.Find("[data-tab=Variances]").Click();
        Flat(page.Find("[data-grid=invoice-variances] [data-row='1']").TextContent)
            .Should().Contain("Prix unitaire facturé 105,00 DH différent du prix commandé 100,00 DH");
        page.Markup.Should().NotContain("MAD");
    }

    [Fact]
    public void WhoeverKeyedTheInvoice_CannotReleaseIt_AnotherApproverDoes_WithAJustificationKept()
    {
        SignIn(AppLanguage.French, Accountant);
        var mine = Row("FA-2026-0418", SupplierInvoiceStatus.Blocked, enteredBy: UserId, variances: 1);
        Invoices(mine);

        var page = Render<SupplierInvoices>();
        page.Find("[data-action='SupplierInvoices.Unblock']").GetAttribute("title").Should().Contain("ne peut pas la débloquer");
        page.Find("[data-action='SupplierInvoices.Unblock']").Click();
        page.Find("[data-state=invoice-message]").TextContent.Should().Contain("ne peut pas la débloquer");
        page.FindAll("[data-modal=reason]").Should().BeEmpty();

        var invoice = Blocked(Guid.NewGuid());
        var theirs = Row("FA-2026-0418", SupplierInvoiceStatus.Blocked, id: invoice.Id, variances: 1);
        Invoices(theirs);
        _invoices.On(nameof(IRepository<SupplierInvoice>.GetByIdWithDetailsAsync), _ => invoice);
        page = Render<SupplierInvoices>();
        page.Find("[data-action='SupplierInvoices.Unblock']").Click();
        page.Find("[data-modal=reason]").TextContent.Should().Contain("présente 1 écart.");
        page.Find("[data-field=reason]").Input("Hausse tarifaire notifiée");
        page.Find("[data-modal=reason] [data-action='Action.Ok']").Click();

        invoice.Status.Should().Be(SupplierInvoiceStatus.ApprovedForPayment);
        AuditReason.Should().Be("Déblocage de la facture fournisseur FA-2026-0418 malgré 1 écart(s) : Hausse tarifaire notifiée");
    }

    [Fact]
    public void ABillOfExchange_WithoutItsDueDate_IsRefusedInItsBox_ThenPaid()
    {
        SignIn(AppLanguage.French, Accountant);
        var invoice = Blocked(Guid.NewGuid());
        invoice.Unblock(DateTimeOffset.Now, Guid.NewGuid(), "Hausse tarifaire notifiée").IsSuccess.Should().BeTrue();
        Invoices(Row("FA-2026-0418", SupplierInvoiceStatus.ApprovedForPayment, id: invoice.Id) with { TotalIncludingVat = 1260m });
        _invoices.On(nameof(IRepository<SupplierInvoice>.GetByIdWithDetailsAsync), _ => invoice);

        var page = Render<SupplierInvoices>();
        page.Find("[data-action='SupplierInvoices.RecordPayment']").Click();
        page.Find($"[data-field={SupplierInvoiceScreenRules.AmountField}]").GetAttribute("value").Should().Be("1260,00", "pre-filled with the decimal comma, as the other boxes");
        page.Find($"[data-field={SupplierInvoiceScreenRules.MaturityField}]").HasAttribute("disabled").Should().BeTrue("a transfer has no due date");

        page.Find("[data-field=payment-method]").Change(nameof(SupplierPaymentMethod.BillOfExchange));
        page.Find($"[data-field={SupplierInvoiceScreenRules.ReferenceField}]").Input("LCN 4471");
        page.Find("[data-modal=supplier-payment] [data-action='Action.Save']").Click();

        page.Find($"[data-refusal={SupplierInvoiceScreenRules.MaturityField}]").TextContent.Should().Contain("date d'échéance de la LCN");
        invoice.Payments.Should().BeEmpty();

        page.Find($"[data-field={SupplierInvoiceScreenRules.MaturityField}]").Change("2026-11-30");
        page.Find("[data-modal=supplier-payment] [data-action='Action.Save']").Click();

        invoice.Payments.Single().MaturityDate.Should().Be(new DateOnly(2026, 11, 30));
        invoice.Status.Should().Be(SupplierInvoiceStatus.Paid);
    }

    [Fact]
    public void AnEntryWithoutItsNumber_AndAPeriodTheWrongWayRound_AreRefusedInTheirBox()
    {
        SignIn(AppLanguage.French, Accountant);
        Invoices(Row("FA-2026-0391", SupplierInvoiceStatus.ApprovedForPayment));
        _queries.On(nameof(ISupplierInvoiceQueries.GetInvoicingSuppliersAsync), _ => new[] { new SupplierOption(Biosystems, "F-BIO", "Biosystems Maroc") });

        var page = Render<SupplierInvoices>();
        page.Find("[data-action='SupplierInvoices.New']").Click();
        page.Find("[data-modal=supplier-invoice] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={SupplierInvoiceScreenRules.NumberField}]").TextContent.Should().Contain("numéro de la facture du fournisseur est obligatoire");
        _invoices.CallsTo(nameof(IRepository<SupplierInvoice>.AddAsync)).Should().Be(0);
        page.Find("[data-modal=supplier-invoice] [data-action='Action.Cancel']").Click();

        page.Find("[data-action='SupplierInvoices.LatePayments']").Click();
        page.Find("[data-field=period-from]").Change("2026-09-30");
        page.Find($"[data-field={SupplierInvoiceScreenRules.PeriodField}]").Change("2026-07-01");
        page.Find("[data-modal=purchasing-period] [data-action='Action.Export']").Click();
        page.Find($"[data-refusal={SupplierInvoiceScreenRules.PeriodField}]").TextContent.Should().Contain("La date de début doit précéder la date de fin");
    }

    [Fact]
    public void WithoutTheApprovalRight_ReleasingAndPayingAreNotOffered()
    {
        SignIn(AppLanguage.French, Permissions.Inventory.View, Permissions.Inventory.EnterInvoices);
        Invoices(Row("FA-2026-0418", SupplierInvoiceStatus.Blocked, variances: 1));

        var page = Render<SupplierInvoices>();

        page.Find("[data-action='SupplierInvoices.New']").Should().NotBeNull();
        page.FindAll("[data-action='SupplierInvoices.Unblock']").Should().BeEmpty("releasing a blocked invoice is the approver's");
        page.FindAll("[data-action='SupplierInvoices.RecordPayment']").Should().BeEmpty();
        page.FindAll("[data-action='SupplierInvoices.Settings']").Should().BeEmpty();
        page.FindAll("[data-action='SupplierInvoices.AccountingExport']").Should().BeEmpty();
    }

    [Fact]
    public void WithoutTheEntryRight_KeyingAndCancellingAreNotOffered()
    {
        SignIn(AppLanguage.French, Permissions.Inventory.View, Permissions.Inventory.ApprovePayments);
        Invoices(Row("FA-2026-0418", SupplierInvoiceStatus.Blocked, variances: 1));

        var page = Render<SupplierInvoices>();

        page.FindAll("[data-action='SupplierInvoices.New']").Should().BeEmpty("keying an invoice is the entry clerk's");
        page.FindAll("[data-action='SupplierInvoices.Cancel']").Should().BeEmpty();
        page.Find("[data-action='SupplierInvoices.Unblock']").Should().NotBeNull();
    }

    [Fact]
    public void TheScreen_IsInArabic_ForAnArabicSession()
    {
        SignIn(AppLanguage.Arabic, Accountant);
        Invoices(Row("FA-2026-0391", SupplierInvoiceStatus.ApprovedForPayment, daysUntilDue: -5));

        var page = Render<SupplierInvoices>();

        page.Find("[data-state=due-summary]").TextContent.Should().Contain("د.م.").And.NotContain("DH");
        page.Markup.Should().NotContain("[[", "every text of the screen has its Arabic");
    }

    private static string Flat(string text) => text.Replace(' ', ' ').Replace(' ', ' ');
}
