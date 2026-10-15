using System.Linq.Expressions;
using System.Text;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Sequencing;
using MarocLIS.Reporting;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Purchasing lot C: the purchase order as a PDF and by e-mail (C1), requests for quotation and the
/// comparison of offers (C2), and instrument placement contracts with their commitment dashboard (C3).
/// </summary>
public sealed class AchatsCommandesTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly Guid Site = Guid.NewGuid();
    private static readonly Guid Requester = Guid.NewGuid();

    // ---- C2: requests for quotation -----------------------------------------------------------

    private static (QuoteRequest Quote, Guid A, Guid B, Guid First, Guid Second) NewQuote()
    {
        var quote = new QuoteRequest("DP/2026/00001", Site, Today, Requester);
        var supplierA = Guid.NewGuid();
        var supplierB = Guid.NewGuid();
        quote.AddItem(Guid.NewGuid(), 10m, "TUBE — Tubes EDTA").IsSuccess.Should().BeTrue();
        quote.AddItem(Guid.NewGuid(), 4m, "GANT — Gants nitrile").IsSuccess.Should().BeTrue();
        quote.AddSupplier(supplierA).IsSuccess.Should().BeTrue();
        quote.AddSupplier(supplierB).IsSuccess.Should().BeTrue();
        var items = quote.Items.OrderBy(i => i.LineNumber).ToList();
        return (quote, supplierA, supplierB, items[0].Id, items[1].Id);
    }

    [Fact]
    public void RecordOffer_FromASupplierNotAsked_IsRefused()
    {
        var (quote, _, _, item1, _) = NewQuote();

        quote.RecordOffer(Guid.NewGuid(), Today, [new SupplierQuoteLineInput(item1, 5m)])
             .FirstError.ResourceKey.Should().Be("Quote_SupplierNotAsked");
    }

    [Fact]
    public void RecordOffer_Twice_ReplacesTheFirstAnswer()
    {
        var (quote, a, _, item1, item2) = NewQuote();

        quote.RecordOffer(a, Today, [new SupplierQuoteLineInput(item1, 5m)]).IsSuccess.Should().BeTrue();
        quote.RecordOffer(a, Today, [new SupplierQuoteLineInput(item1, 4m), new SupplierQuoteLineInput(item2, 9m)])
             .IsSuccess.Should().BeTrue();

        quote.Offers.Should().ContainSingle();
        quote.Offers.Single().Lines.Should().HaveCount(2);
    }

    [Fact]
    public void Comparison_MarksTheCheapestNetOfDiscount_AndTheFastest_IgnoringExpiredOffers()
    {
        var (quote, a, b, item1, item2) = NewQuote();

        // A: 10.00 list, 20 % off = 8.00 net, 10 days. B: 9.00 net, 3 days.
        quote.RecordOffer(a, Today, [new SupplierQuoteLineInput(item1, 10m, 0.20m, 0.20m),
                                     new SupplierQuoteLineInput(item2, 50m)], deliveryDelayDays: 10);
        quote.RecordOffer(b, Today, [new SupplierQuoteLineInput(item1, 9m)], deliveryDelayDays: 3);

        var grid = QuoteComparison.Build(quote, Today);
        var line1 = grid.Single(l => l.QuoteRequestItemId == item1);
        line1.Cells.Single(c => c.SupplierId == a).Should().Match<QuoteComparisonCell>(c => c.IsCheapest && !c.IsFastest);
        line1.Cells.Single(c => c.SupplierId == b).Should().Match<QuoteComparisonCell>(c => !c.IsCheapest && c.IsFastest);
        line1.Cells.Single(c => c.SupplierId == a).LineTotalExcludingVat.Should().Be(80m);

        // B left the second article out: its total is not comparable, and the count says so.
        QuoteComparison.Totals(quote, quote.Offers.Single(o => o.SupplierId == b)).MissingItems.Should().Be(1);

        // Once A's offer has expired it is still shown, but B is the only valid price left.
        quote.Offers.Single(o => o.SupplierId == a).ValidUntil = Today.AddDays(-1);
        var later = QuoteComparison.Build(quote, Today).Single(l => l.QuoteRequestItemId == item1);
        later.Cells.Single(c => c.SupplierId == a).Should().Match<QuoteComparisonCell>(c => c.IsExpired && !c.IsCheapest);
    }

    [Fact]
    public void Award_AnExpiredOffer_IsRefused_AndAnAwardedRequestIsClosed()
    {
        var (quote, a, b, item1, _) = NewQuote();
        var expired = quote.RecordOffer(a, Today.AddDays(-20), [new SupplierQuoteLineInput(item1, 5m)],
                                        validUntil: Today.AddDays(-1)).Value;
        var valid = quote.RecordOffer(b, Today, [new SupplierQuoteLineInput(item1, 6m)]).Value;

        quote.Award(expired.Id, Today, Requester).FirstError.ResourceKey.Should().Be("Quote_OfferExpired");

        quote.Award(valid.Id, Today, Requester).IsSuccess.Should().BeTrue();
        quote.Status.Should().Be(QuoteRequestStatus.Awarded);
        quote.AddItem(Guid.NewGuid(), 1m, null).FirstError.ResourceKey.Should().Be("Quote_NotOpen");
        quote.RecordOffer(a, Today, [new SupplierQuoteLineInput(item1, 1m)])
             .FirstError.ResourceKey.Should().Be("Quote_NotOpen");
    }

    [Fact]
    public async Task AwardAsync_DraftsAPurchaseOrderAtTheOfferPrices()
    {
        var (quote, a, _, item1, item2) = NewQuote();
        var offer = quote.RecordOffer(a, Today, [new SupplierQuoteLineInput(item1, 12.5m, 0.07m, 0.10m)],
                                      deliveryDelayDays: 5, supplierReference: "DEV-889").Value;

        var orders = new Repo<PurchaseOrder>();
        var service = Quotes(new Repo<QuoteRequest>(quote), orders, new QuoteQueriesFake(), approved: true);

        var number = await service.AwardAsync(quote.Id, offer.Id);

        number.IsSuccess.Should().BeTrue(number.IsFailure ? number.FirstError.ResourceKey : "");
        var order = orders.Items.Single();
        order.Status.Should().Be(PurchaseOrderStatus.Draft);
        order.SupplierId.Should().Be(a);
        order.ExpectedDeliveryDate.Should().Be(Today.AddDays(5));
        order.SupplierReference.Should().Be("DEV-889");
        // Only the article the supplier priced, at its price, discount and VAT.
        order.Lines.Should().ContainSingle().Which.Should().Match<PurchaseOrderLine>(l =>
            l.UnitPriceMad == 12.5m && l.DiscountRate == 0.10m && l.VatRate == 0.07m && l.QuantityOrdered == 10m);
        quote.Status.Should().Be(QuoteRequestStatus.Awarded);
        quote.PurchaseOrderId.Should().Be(order.Id);
        _ = item2;
    }

    [Fact]
    public async Task AwardAsync_ToASupplierNotApproved_IsRefused()
    {
        var (quote, a, _, item1, _) = NewQuote();
        var offer = quote.RecordOffer(a, Today, [new SupplierQuoteLineInput(item1, 1m)]).Value;

        var result = await Quotes(new Repo<QuoteRequest>(quote), new Repo<PurchaseOrder>(), new QuoteQueriesFake(),
                                  approved: false).AwardAsync(quote.Id, offer.Id);

        result.FirstError.ResourceKey.Should().Be("Po_SupplierNotApproved");
        quote.Status.Should().Be(QuoteRequestStatus.Open);
    }

    [Fact]
    public async Task CreateAsync_WithACaptiveReagent_IsRefused()
    {
        var item = Guid.NewGuid();
        var queries = new QuoteQueriesFake
        {
            Captive = [new CaptiveReagent(item, Guid.NewGuid(), "PLAC-2025-01", Guid.NewGuid(), 950m)]
        };

        var result = await Quotes(new Repo<QuoteRequest>(), new Repo<PurchaseOrder>(), queries, approved: true)
            .CreateAsync(new CreateQuoteRequest
            {
                Items = [new QuoteItemInput(item, 3m)],
                SupplierIds = [Guid.NewGuid()]
            });

        result.FirstError.ResourceKey.Should().Be("Quote_ItemOnContract");
        result.FirstError.Args.Should().Contain("PLAC-2025-01");
    }

    [Fact]
    public async Task CreateAsync_NumbersTheRequestFromItsOwnSeries()
    {
        var item = Guid.NewGuid();
        var requests = new Repo<QuoteRequest>();
        var sequences = new Sequences();

        var result = await Quotes(requests, new Repo<PurchaseOrder>(), new QuoteQueriesFake(), approved: true, sequences)
            .CreateAsync(new CreateQuoteRequest
            {
                Subject = "Consommables T4",
                Items = [new QuoteItemInput(item, 3m)],
                SupplierIds = [Guid.NewGuid(), Guid.NewGuid()]
            });

        result.IsSuccess.Should().BeTrue();
        sequences.Asked.Should().Contain(SequencePurpose.QuoteRequest);
        requests.Items.Single().Suppliers.Should().HaveCount(2);
    }

    // ---- C3: instrument contracts -------------------------------------------------------------

    private static InstrumentContract Contract(DateOnly start, int months, CommitmentBasis basis, decimal committed) =>
        new("PLAC-2025-01", Guid.NewGuid(), Site, InstrumentContractKind.Placement, start, months)
        {
            CommitmentBasis = basis,
            CommittedVolume = committed
        };

    [Fact]
    public void Period_OfAYearlyCommitment_IsTheCurrentContractYear()
    {
        var contract = Contract(new DateOnly(2025, 3, 1), 36, CommitmentBasis.TestsPerYear, 12_000m);

        contract.EndDate.Should().Be(new DateOnly(2028, 2, 29));
        ContractCommitment.Period(contract, Today)
                          .Should().Be((new DateOnly(2026, 3, 1), new DateOnly(2027, 2, 28)));

        var total = Contract(new DateOnly(2025, 3, 1), 36, CommitmentBasis.TestsTotal, 36_000m);
        ContractCommitment.Period(total, Today).Should().Be((new DateOnly(2025, 3, 1), new DateOnly(2028, 2, 29)));
    }

    [Fact]
    public void Evaluate_ProjectsLinearly_AndAlertsUnderNinetyPercent()
    {
        var contract = Contract(new DateOnly(2025, 3, 1), 36, CommitmentBasis.TestsPerYear, 12_000m);
        var halfway = new DateOnly(2026, 8, 30);   // day 183 of 365

        var behind = ContractCommitment.Evaluate(contract, halfway, 5_000m);
        behind.ExpectedToDate.Should().BeApproximately(6_016m, 1m);
        behind.Projected.Should().BeApproximately(9_973m, 1m);
        behind.ProjectedRatio.Should().BeLessThan(0.90m);
        behind.AtRisk.Should().BeTrue();

        var onCourse = ContractCommitment.Evaluate(contract, halfway, 5_500m);
        onCourse.ProjectedRatio.Should().BeGreaterThan(0.90m);
        onCourse.AtRisk.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_RaisesNoAlertInTheFirstMonthOfAPeriod()
    {
        var contract = Contract(new DateOnly(2025, 3, 1), 36, CommitmentBasis.TestsPerYear, 12_000m);

        ContractCommitment.Evaluate(contract, new DateOnly(2026, 3, 10), 10m).AtRisk.Should().BeFalse();
        ContractCommitment.Evaluate(contract, new DateOnly(2026, 4, 15), 10m).AtRisk.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_ALastContractYearCutShort_OwesTheCommitmentProRata()
    {
        // Eighteen months: the second contract year stops after six months.
        var contract = Contract(new DateOnly(2025, 1, 1), 18, CommitmentBasis.PurchaseAmountPerYear, 100_000m);

        var status = ContractCommitment.Evaluate(contract, new DateOnly(2026, 3, 1), 30_000m);

        status.PeriodStart.Should().Be(new DateOnly(2026, 1, 1));
        status.PeriodEnd.Should().Be(new DateOnly(2026, 6, 30));
        status.Committed.Should().BeApproximately(100_000m * 181 / 365, 0.01m);
    }

    [Fact]
    public void Contract_EndingWithinNinetyDays_IsFlagged()
    {
        var contract = Contract(new DateOnly(2021, 12, 1), 60, CommitmentBasis.None, 0m);

        contract.EndDate.Should().Be(new DateOnly(2026, 11, 30));
        contract.IsEndingSoon(Today).Should().BeTrue();
        contract.IsEndingSoon(new DateOnly(2026, 8, 1)).Should().BeFalse();
        contract.IsInForce(new DateOnly(2026, 12, 1)).Should().BeFalse();
    }

    [Fact]
    public void InterventionDelay_IsMeasuredFromTheCall_AndComparedWithTheContract()
    {
        var contract = Contract(new DateOnly(2025, 1, 1), 60, CommitmentBasis.None, 0m);
        contract.MaxInterventionDelayHours = 4;

        var record = new MaintenanceRecord(MaintenanceKind.Corrective, Today, new MultilingualText("Panne", "", ""))
        {
            RequestedAt = new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.FromHours(1)),
            ArrivedAt = new DateTimeOffset(2026, 10, 15, 14, 30, 0, TimeSpan.FromHours(1))
        };

        ContractCommitment.InterventionDelayHours(record).Should().Be(6.5m);
        ContractCommitment.IsDelayExceeded(contract, record).Should().BeTrue();

        record.ArrivedAt = record.RequestedAt!.Value.AddHours(3);
        ContractCommitment.IsDelayExceeded(contract, record).Should().BeFalse();
    }

    [Fact]
    public void SetReagents_KeepsTheRowOfAReagentStillOnTheContract()
    {
        var contract = Contract(new DateOnly(2025, 1, 1), 60, CommitmentBasis.TestsPerYear, 10_000m);
        var kept = Guid.NewGuid();
        contract.SetReagents([new(kept, 100m, null), new(Guid.NewGuid(), 50m, 2m)]);
        var row = contract.Reagents.Single(r => r.StockItemId == kept);

        contract.SetReagents([new(kept, 95m, null)]);

        contract.Reagents.Should().ContainSingle().Which.Should().BeSameAs(row);
        row.ContractPriceMad.Should().Be(95m);
    }

    [Fact]
    public void Validate_RequiresACommittedVolumeWhenThereIsACommitment()
    {
        var contract = Contract(new DateOnly(2025, 1, 1), 60, CommitmentBasis.TestsPerYear, 0m);

        contract.Validate().Errors.Select(e => e.ResourceKey).Should().Contain("Contract_CommitmentRequired");
    }

    // ---- C1: the purchase order document -----------------------------------------------------

    private static PurchaseOrderDocument Document(PurchaseOrderStatus status = PurchaseOrderStatus.Approved,
                                                  string? supplierEmail = "commandes@fournisseur.ma") => new(
        Guid.NewGuid(), "BC/2026/00042", status, Today, Today.AddDays(7),
        new InvoiceIssuer("Laboratoire Al Amal", "LABO AL AMAL SARL", "12, boulevard Zerktouni", "Casablanca",
                          "20000", "05 22 00 00 00", "contact@example.ma", "001234567000089", "12345678", "123456",
                          "Casablanca", "34567890", "1234567", null, null),
        new PurchaseOrderSupplierParty("BIOMA", "Bio Maroc SARL", "002345678000012", "87654321", "98765",
                                       "Zone industrielle", "Mohammedia", "M. Alaoui", "05 23 00 00 00", supplierEmail),
        "Laboratoire Al Amal — 12, boulevard Zerktouni — 20000 Casablanca", "PRO-77", "Livrer avant 11 h",
        "Acheteur", "Biologiste responsable", new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.FromHours(1)),
        [
            new PurchaseOrderDocumentLine(1, "REF-001", "Réactif glucose", "coffret", 3m, 1_000m, 0.10m, 0.20m),
            new PurchaseOrderDocumentLine(2, "TUBE", "Tubes EDTA", "boîte", 10m, 45.5m, 0m, 0.07m)
        ]);

    [Fact]
    public void Document_TotalsMatchTheOrderRules_AndBreakVatDownByRate()
    {
        var document = Document();

        document.TotalExcludingVat.Should().Be(2_700m + 455m);
        document.TotalVat.Should().Be(540m + 31.85m);
        document.TotalIncludingVat.Should().Be(3_726.85m);
        document.VatBreakdown.Should().HaveCount(2);
        document.FileName.Should().Be("BC_2026_00042.pdf");
    }

    [Theory]
    [InlineData(AppLanguage.French)]
    [InlineData(AppLanguage.Arabic)]
    [InlineData(AppLanguage.English)]
    public void Renderer_ProducesAPdf(AppLanguage language)
    {
        var renderer = new QuestPdfPurchaseOrderRenderer(new ReportingOptions
        {
            FontDirectory = Path.Combine(AppContext.BaseDirectory, "no-fonts-here")
        });

        var pdf = renderer.Render(Document(), language);

        Encoding.ASCII.GetString(pdf, 0, 4).Should().Be("%PDF");
    }

    [Fact]
    public async Task EmailAsync_WithoutSmtp_SaysSo_AndLeavesTheOrderApproved()
    {
        var (service, order, messages) = DocumentService(canEmail: false);

        var result = await service.EmailAsync(order.Id);

        result.FirstError.ResourceKey.Should().Be("Purchasing_EmailNotConfigured");
        order.Status.Should().Be(PurchaseOrderStatus.Approved);
        messages.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task EmailAsync_ToASupplierWithoutAddress_IsRefused()
    {
        var (service, order, _) = DocumentService(canEmail: true, supplierEmail: null);

        (await service.EmailAsync(order.Id)).FirstError.ResourceKey.Should().Be("Po_SupplierEmailMissing");
        order.Status.Should().Be(PurchaseOrderStatus.Approved);
    }

    [Fact]
    public async Task EmailAsync_WhenTheMailGoesOut_AttachesThePdf_AndMarksTheOrderSent()
    {
        var (service, order, messages) = DocumentService(canEmail: true);

        (await service.EmailAsync(order.Id)).IsSuccess.Should().BeTrue();

        order.Status.Should().Be(PurchaseOrderStatus.Sent);
        var mail = messages.Sent.Single();
        mail.To.Should().Be("commandes@fournisseur.ma");
        mail.Attachments.Single().FileName.Should().EndWith(".pdf");
        mail.Body.Should().Contain(order.Number);
    }

    [Fact]
    public async Task EmailAsync_WhenTheServerRefuses_LeavesTheOrderApproved()
    {
        var (service, order, messages) = DocumentService(canEmail: true);
        messages.Fail = true;

        (await service.EmailAsync(order.Id)).IsFailure.Should().BeTrue();
        order.Status.Should().Be(PurchaseOrderStatus.Approved);
    }

    [Fact]
    public async Task RenderAsync_ADraft_IsRefused()
    {
        var (service, order, _) = DocumentService(canEmail: true, status: PurchaseOrderStatus.Draft);

        (await service.RenderAsync(order.Id)).FirstError.ResourceKey.Should().Be("Po_DocumentRequiresApproval");
    }

    private static (PurchaseOrderDocumentService, PurchaseOrder, Messages) DocumentService(
        bool canEmail, string? supplierEmail = "commandes@fournisseur.ma",
        PurchaseOrderStatus status = PurchaseOrderStatus.Approved)
    {
        var order = new PurchaseOrder("BC/2026/00042", Guid.NewGuid(), Site, Today, Requester);
        order.AddLine(new PurchaseOrderLine(Guid.NewGuid(), 1m, 10m));
        if (status != PurchaseOrderStatus.Draft) order.Approve(DateTimeOffset.Now, Guid.NewGuid());

        var document = Document(order.Status, supplierEmail) with { PurchaseOrderId = order.Id, Number = order.Number };
        var messages = new Messages { CanSendEmail = canEmail };

        var service = new PurchaseOrderDocumentService(
            new Repo<PurchaseOrder>(order), new DocumentQueries(document), new BytesRenderer(), messages,
            new UnitOfWork(), new AllowAll(), new User(), new Clock());

        return (service, order, messages);
    }

    private static QuoteService Quotes(Repo<QuoteRequest> requests, Repo<PurchaseOrder> orders, QuoteQueriesFake queries,
                                       bool approved, Sequences? sequences = null) =>
        new(requests, orders, new Repo<PurchasingDocument>(), queries, new OrderQueries(approved),
            sequences ?? new Sequences(), new Messages(), new UnitOfWork(), new AllowAll(), new User(), new Clock());

    // ---- Fakes -------------------------------------------------------------------------------

    private sealed class Repo<T>(params T[] items) : IRepository<T> where T : AggregateRoot
    {
        public List<T> Items { get; } = [.. items];
        public Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
        public Task<T?> GetByIdWithDetailsAsync(Guid id, CancellationToken ct = default) => GetByIdAsync(id, ct);
        public Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.Any(i => i.Id == id));
        public Task<IReadOnlyList<T>> ListAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<T>>([.. Items.Where(predicate.Compile())]);
        public Task AddAsync(T entity, CancellationToken ct = default) { Items.Add(entity); return Task.CompletedTask; }
        public void Update(T entity) { }
        public void SoftDelete(T entity, Guid byUserId, string reason) => Items.Remove(entity);
    }

    private sealed class UnitOfWork : IUnitOfWork
    {
        public string? AuditReason { get; set; }
        public Task<int> SaveChangesAsync(CancellationToken ct = default) => Task.FromResult(1);
        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable>(new Nothing());
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;
        private sealed class Nothing : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    private sealed class AllowAll : IPermissionGuard
    {
        public Result Require(string permission) => Result.Success();
        public Result RequireAny(params string[] permissions) => Result.Success();
        public Result RequireAll(params string[] permissions) => Result.Success();
        public Task<Result> RequireBiologicalValidationAsync(string departmentCode, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset Now => new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));
        public DateOnly Today => AchatsCommandesTests.Today;
    }

    private sealed class User : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "acheteur";
        public string FullName => "Acheteur";
        public Guid SiteId => Site;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => default;
        public IReadOnlyList<Guid> AccessibleSiteIds => [Site];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => true;
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "TEST";
    }

    private sealed class Sequences : ISequenceAllocator
    {
        public List<SequencePurpose> Asked { get; } = [];
        private int _next;

        public Task<Result<string>> NextAsync(SequencePurpose purpose, Guid? siteId, int? year = null,
                                              CancellationToken ct = default)
        {
            Asked.Add(purpose);
            return Task.FromResult(Result.Success($"{purpose}/{++_next:00000}"));
        }

        public Task<string?> PeekAsync(SequencePurpose purpose, Guid? siteId, int? year = null,
                                       CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class Messages : IMessageSender
    {
        public bool CanSendEmail { get; set; } = true;
        public bool CanSendSms => false;
        public bool Fail { get; set; }
        public List<EmailMessage> Sent { get; } = [];

        public Task<Result> SendEmailAsync(EmailMessage message, CancellationToken ct = default)
        {
            if (Fail) return Task.FromResult(Result.Failure(Error.Validation("Messaging_EmailFailed", "refused")));
            Sent.Add(message);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> SendSmsAsync(string phoneNumber, string text, CancellationToken ct = default) =>
            Task.FromResult(Result.Failure(Error.Validation("Messaging_SmsNotConfigured")));
    }

    private sealed class BytesRenderer : IPurchaseOrderRenderer
    {
        public byte[] Render(PurchaseOrderDocument document, AppLanguage language) => Encoding.ASCII.GetBytes("%PDF-fake");
    }

    private sealed class DocumentQueries(PurchaseOrderDocument document) : IPurchaseOrderDocumentQueries
    {
        public Task<PurchaseOrderDocument?> GetDocumentAsync(Guid purchaseOrderId, CancellationToken ct = default) =>
            Task.FromResult<PurchaseOrderDocument?>(purchaseOrderId == document.PurchaseOrderId ? document : null);
    }

    private sealed class QuoteQueriesFake : IQuoteQueries
    {
        public IReadOnlyList<CaptiveReagent> Captive { get; init; } = [];

        public Task<IReadOnlyList<QuoteRequestRow>> GetRequestsAsync(Guid siteId, bool onlyOpen, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<QuoteRequestRow>>([]);
        public Task<QuoteRequestDetail?> GetDetailAsync(Guid requestId, DateOnly today, CancellationToken ct = default) =>
            Task.FromResult<QuoteRequestDetail?>(null);
        public Task<IReadOnlyList<QuotableItem>> GetQuotableItemsAsync(Guid siteId, DateOnly today, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<QuotableItem>>([]);
        public Task<IReadOnlyList<QuoteSupplierOption>> GetSuppliersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<QuoteSupplierOption>>([]);
        public Task<IReadOnlyDictionary<Guid, string>> GetItemCodesAsync(IReadOnlyList<Guid> stockItemIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(stockItemIds.ToDictionary(i => i, i => $"ART-{i.ToString()[..4]}"));
        public Task<IReadOnlyList<CaptiveReagent>> GetCaptiveReagentsAsync(Guid siteId, DateOnly today,
            IReadOnlyList<Guid>? stockItemIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CaptiveReagent>>(
                [.. Captive.Where(c => stockItemIds is null || stockItemIds.Contains(c.StockItemId))]);
        public Task<string> GetLaboratoryNameAsync(Guid siteId, CancellationToken ct = default) => Task.FromResult("Labo");
        public Task<(string FileName, byte[] Content)?> GetDocumentAsync(PurchasingDocumentOwner ownerKind, Guid ownerId,
                                                                         CancellationToken ct = default) =>
            Task.FromResult<(string, byte[])?>(null);
    }

    private sealed class OrderQueries(bool approved) : IPurchaseOrderQueries
    {
        public Task<IReadOnlyList<PurchaseOrderRow>> GetOrdersAsync(Guid siteId, bool onlyOpen, DateOnly today, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PurchaseOrderRow>>([]);
        public Task<IReadOnlyList<PurchaseOrderLineRow>> GetLinesAsync(Guid purchaseOrderId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PurchaseOrderLineRow>>([]);
        public Task<IReadOnlyList<SupplierOption>> GetSuppliersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SupplierOption>>([]);
        public Task<IReadOnlyList<OrderableItem>> GetOrderableItemsAsync(Guid siteId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OrderableItem>>([]);
        public Task<bool> IsSupplierApprovedAsync(Guid supplierId, CancellationToken ct = default) => Task.FromResult(approved);
        public Task<IReadOnlyList<ReorderCandidate>> GetReorderCandidatesAsync(Guid siteId, DateOnly today, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ReorderCandidate>>([]);
        public Task<Guid?> FindAutomaticDraftAsync(Guid siteId, Guid supplierId, CancellationToken ct = default) =>
            Task.FromResult<Guid?>(null);
    }
}
