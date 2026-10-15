using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Quality;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Purchasing quality at the door (achats A1–A3): the cold-chain check, the reception checklist,
/// the GS1 DataMatrix scan, the non-conformity a failed check opens against the supplier and the
/// refusal of a whole delivery.
/// </summary>
public sealed class AchatsReceptionTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));

    // ---- A1: cold chain ---------------------------------------------------------------------------

    [Theory]
    [InlineData(StorageCondition.Refrigerated, 2.0, TemperatureVerdict.Conform)]
    [InlineData(StorageCondition.Refrigerated, 8.0, TemperatureVerdict.Conform)]
    [InlineData(StorageCondition.Refrigerated, 8.5, TemperatureVerdict.OutOfRange)]
    [InlineData(StorageCondition.Refrigerated, 1.9, TemperatureVerdict.OutOfRange)]
    [InlineData(StorageCondition.Frozen, -15.0, TemperatureVerdict.Conform)]
    [InlineData(StorageCondition.Frozen, -10.0, TemperatureVerdict.OutOfRange)]
    [InlineData(StorageCondition.DeepFrozen, -70.0, TemperatureVerdict.Conform)]
    [InlineData(StorageCondition.DeepFrozen, -50.0, TemperatureVerdict.OutOfRange)]
    [InlineData(StorageCondition.RoomTemperature, 20.0, TemperatureVerdict.Conform)]
    [InlineData(StorageCondition.RoomTemperature, 31.0, TemperatureVerdict.Warning)]
    public void TheArrivalTemperature_IsJudgedAgainstTheStorageCondition(StorageCondition condition,
                                                                        double temperature,
                                                                        TemperatureVerdict expected)
    {
        ColdChain.Evaluate(condition, (decimal)temperature).Should().Be(expected);
    }

    [Fact]
    public void AColdChainItem_WithoutATemperature_IsMissing_AnAmbientOneIsNotRecorded()
    {
        ColdChain.Evaluate(StorageCondition.Frozen, null).Should().Be(TemperatureVerdict.Missing);
        ColdChain.Evaluate(StorageCondition.ProtectFromLight, null).Should().Be(TemperatureVerdict.NotRecorded);
        ColdChain.RangeText(StorageCondition.Refrigerated).Should().Be("2 à 8 °C");
    }

    [Fact]
    public void AReceptionOfARefrigeratedItem_WithoutTemperature_IsRefused_WithTheRange()
    {
        var assessment = ReceptionAssessment.Assess(StorageCondition.Refrigerated, new ReceptionCheck());

        assessment.FirstError.ResourceKey.Should().Be("Reception_TemperatureRequired");
        assessment.FirstError.Args.Single().Should().Be("2 à 8 °C");
    }

    [Fact]
    public void AnAmbientItem_WithNoCheckAtAll_IsAccepted_WithoutNonConformity()
    {
        var assessment = ReceptionAssessment.Assess(StorageCondition.RoomTemperature, null);

        assessment.IsSuccess.Should().BeTrue();
        assessment.Value.NeedsNonConformity.Should().BeFalse();
        assessment.Value.Temperature.Should().Be(TemperatureVerdict.NotRecorded);
    }

    [Fact]
    public void AnAmbientWarning_IsNotANonConformity()
    {
        var assessment = ReceptionAssessment.Assess(StorageCondition.RoomTemperature,
                                                    new ReceptionCheck { ArrivalTemperatureCelsius = 30m });

        assessment.Value.Temperature.Should().Be(TemperatureVerdict.Warning);
        assessment.Value.NeedsNonConformity.Should().BeFalse();
    }

    [Fact]
    public void AnOutOfRangeTemperature_IsAMajorFinding_AndIsDescribedInFrench()
    {
        var assessment = ReceptionAssessment.Assess(StorageCondition.Refrigerated,
                                                    new ReceptionCheck { ArrivalTemperatureCelsius = 12.5m });

        assessment.Value.Findings.Should().Equal(ReceptionFinding.TemperatureOutOfRange);
        assessment.Value.Severity.Should().Be(NonConformitySeverity.Major);
        assessment.Value.DescribeFr(StorageCondition.Refrigerated, 12.5m)
            .Should().Contain("12,5 °C").And.Contain("2 à 8 °C");
    }

    [Fact]
    public void APaperworkDefect_IsAMinorFinding_AndOnlyAnExplicitNoCounts()
    {
        var assessment = ReceptionAssessment.Assess(StorageCondition.Refrigerated, new ReceptionCheck
        {
            ArrivalTemperatureCelsius = 5m,
            MatchesOrder = true,
            PackagingIntact = null,
            FrenchLabelling = false,
            CertificateOfAnalysisReceived = false
        });

        assessment.Value.Findings.Should().Equal(ReceptionFinding.LabellingNotInFrench,
                                                 ReceptionFinding.CertificateOfAnalysisMissing);
        assessment.Value.Severity.Should().Be(NonConformitySeverity.Minor);
    }

    [Fact]
    public void DamagedPackaging_IsMajor()
    {
        var assessment = ReceptionAssessment.Assess(StorageCondition.RoomTemperature,
                                                    new ReceptionCheck { PackagingIntact = false });

        assessment.Value.Severity.Should().Be(NonConformitySeverity.Major);
    }

    [Fact]
    public void TheLot_KeepsWhatWasCheckedAtReception()
    {
        var lot = new ReagentLot("L1", Today.AddYears(1), 5m, Today);

        lot.RecordReception(new ReceptionCheck
        {
            ArrivalTemperatureCelsius = 4.2m, MatchesOrder = true, PackagingIntact = true,
            FrenchLabelling = false, CertificateOfAnalysisReceived = true
        });

        lot.ArrivalTemperatureCelsius.Should().Be(4.2m);
        lot.ReceptionMatchesOrder.Should().BeTrue();
        lot.ReceptionFrenchLabelling.Should().BeFalse();
        lot.CertificateOfAnalysisReceived.Should().BeTrue();
    }

    // ---- A2: GS1 DataMatrix -----------------------------------------------------------------------

    [Fact]
    public void AScan_WithGroupSeparators_GivesGtinExpiryLotAndSerial()
    {
        var parsed = Gs1Parser.Parse("]d2010950600013435217271231" + "10ABC123\u001d21SN0042");

        parsed.IsSuccess.Should().BeTrue();
        parsed.Value.Gtin.Should().Be("09506000134352");
        parsed.Value.ExpiryDate.Should().Be(new DateOnly(2027, 12, 31));
        parsed.Value.LotNumber.Should().Be("ABC123");
        parsed.Value.SerialNumber.Should().Be("SN0042");
    }

    [Fact]
    public void AScan_TypedAsHumanReadableText_IsReadToo()
    {
        var parsed = Gs1Parser.Parse("(01)09506000134352(17)270600(10)LOT-7");

        parsed.Value.Gtin.Should().Be("09506000134352");
        // Day 00: the last day of the month.
        parsed.Value.ExpiryDate.Should().Be(new DateOnly(2027, 6, 30));
        parsed.Value.LotNumber.Should().Be("LOT-7");
    }

    [Fact]
    public void AScanner_ThatTypesTheSeparatorAsText_IsUnderstood()
    {
        var parsed = Gs1Parser.Parse("0109506000134352" + "10LOT9<GS>17280115");

        parsed.Value.LotNumber.Should().Be("LOT9");
        parsed.Value.ExpiryDate.Should().Be(new DateOnly(2028, 1, 15));
    }

    [Fact]
    public void AProductionDate_IsReadButNotTakenForTheExpiry()
    {
        var parsed = Gs1Parser.Parse("01095060001343521126010110L1");

        parsed.Value.ProductionDate.Should().Be(new DateOnly(2026, 1, 1));
        parsed.Value.ExpiryDate.Should().BeNull();
        parsed.Value.LotNumber.Should().Be("L1");
    }

    [Theory]
    [InlineData("", "Gs1_Empty")]
    [InlineData("   ", "Gs1_Empty")]
    [InlineData("0109506000134353", "Gs1_GtinInvalid")]
    [InlineData("01095060001343", "Gs1_Truncated")]
    [InlineData("0109506000134352171313", "Gs1_Truncated")]
    [InlineData("0109506000134352" + "17271332", "Gs1_DateInvalid")]
    [InlineData("hello world", "Gs1_Unreadable")]
    public void ABadScan_IsRefusedWithItsReason(string raw, string key)
    {
        Gs1Parser.Parse(raw).FirstError.ResourceKey.Should().Be(key);
    }

    [Theory]
    [InlineData("4006381333931", "04006381333931")]
    [InlineData("96385074", "00000096385074")]
    [InlineData("012345678905", "00012345678905")]
    [InlineData(" 09506000134352 ", "09506000134352")]
    [InlineData("4006381333932", null)]
    [InlineData("40063813339", null)]
    [InlineData("4OO6381333931", null)]
    [InlineData(null, null)]
    public void AGtin_IsNormalisedToFourteenDigits_OrRefused(string? input, string? expected)
    {
        Gs1Parser.NormaliseGtin(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("270229", null)]
    [InlineData("280229", "2028-02-29")]
    [InlineData("270200", "2027-02-28")]
    [InlineData("27AB01", null)]
    public void AGs1Date_IsYearMonthDay(string raw, string? expected)
    {
        Gs1Parser.ParseDate(raw).Should().Be(expected is null ? null : DateOnly.Parse(expected));
    }

    // ---- A3: the non-conformity a failed check opens, and refused deliveries ---------------------

    [Fact]
    public async Task AReceiptOnAPurchaseOrder_OfAColdItemWithoutTemperature_IsRefused()
    {
        var harness = new PurchaseHarness(StorageCondition.Refrigerated);

        var result = await harness.ReceiveAsync(null);

        result.FirstError.ResourceKey.Should().Be("Reception_TemperatureRequired");
        harness.Item.Lots.Should().BeEmpty();
        harness.Order.Lines.Single().QuantityReceived.Should().Be(0m);
    }

    [Fact]
    public async Task AReceiptOnAPurchaseOrder_RecordsTheOrderOnTheLot_AndTheChecks()
    {
        var harness = new PurchaseHarness(StorageCondition.Refrigerated);

        (await harness.ReceiveAsync(new ReceptionCheck { ArrivalTemperatureCelsius = 4.8m, MatchesOrder = true }))
            .IsSuccess.Should().BeTrue();

        var lot = harness.Item.Lots.Single();
        lot.PurchaseOrderId.Should().Be(harness.Order.Id);
        lot.ArrivalTemperatureCelsius.Should().Be(4.8m);
        lot.IsQuarantined.Should().BeTrue();
        harness.NonConformities.Should().BeEmpty();
        harness.Commits.Should().Be(0);
    }

    [Fact]
    public async Task AWarmDelivery_IsReceivedInQuarantine_AndOpensAMajorNonConformityAgainstTheSupplier()
    {
        var harness = new PurchaseHarness(StorageCondition.Refrigerated);

        (await harness.ReceiveAsync(new ReceptionCheck { ArrivalTemperatureCelsius = 14m }))
            .IsSuccess.Should().BeTrue();

        var lot = harness.Item.Lots.Single();
        var nc = harness.NonConformities.Single();
        nc.Reference.Should().Be("NC-2026-0007");
        nc.SupplierId.Should().Be(harness.Order.SupplierId);
        nc.ReagentLotId.Should().Be(lot.Id);
        nc.Category.Should().Be(NonConformityCategory.ExternalProvider);
        nc.Severity.Should().Be(NonConformitySeverity.Major);
        nc.Description.Fr.Should().Contain("14 °C").And.Contain(lot.LotNumber);
        lot.IsQuarantined.Should().BeTrue();
        harness.Commits.Should().Be(1);
    }

    [Fact]
    public async Task ARefusedDelivery_IsRecordedWithItsNonConformity_AndNothingEntersTheStock()
    {
        var harness = new PurchaseHarness(StorageCondition.Refrigerated);

        var refused = await harness.Reception.RefuseDeliveryAsync(new RefuseDeliveryRequest
        {
            PurchaseOrderId = harness.Order.Id,
            StockItemId = harness.Item.Id,
            Reason = "Colis arrivé à température ambiante, indicateur viré",
            ArrivalTemperatureCelsius = 19m,
            DeliveryNoteReference = "BL-77"
        });

        refused.Value.Should().Be("NC-2026-0007");
        var refusal = harness.Refusals.Single();
        refusal.SupplierId.Should().Be(harness.Order.SupplierId);
        refusal.PurchaseOrderId.Should().Be(harness.Order.Id);
        refusal.RefusedOn.Should().Be(Today);
        refusal.ArrivalTemperatureCelsius.Should().Be(19m);
        refusal.NonConformityId.Should().Be(harness.NonConformities.Single().Id);
        harness.NonConformities.Single().SupplierId.Should().Be(harness.Order.SupplierId);
        harness.NonConformities.Single().Description.Fr.Should().Contain(harness.Order.Number);
        harness.Item.Lots.Should().BeEmpty();
        harness.Commits.Should().Be(1);
    }

    [Fact]
    public async Task ARefusal_NeedsAReason()
    {
        var harness = new PurchaseHarness(StorageCondition.RoomTemperature);

        (await harness.Reception.RefuseDeliveryAsync(new RefuseDeliveryRequest { Reason = "  " }))
            .FirstError.ResourceKey.Should().Be("Reception_RefusalReasonRequired");
        harness.Refusals.Should().BeEmpty();
    }

    [Fact]
    public async Task ARefusal_NeedsTheReceivePermission()
    {
        var harness = new PurchaseHarness(StorageCondition.RoomTemperature, denied: Permissions.Inventory.Receive);

        (await harness.Reception.RefuseDeliveryAsync(new RefuseDeliveryRequest { Reason = "Colis écrasé" }))
            .IsFailure.Should().BeTrue();
        harness.Refusals.Should().BeEmpty();
    }

    // ---- A5: supplier registration ----------------------------------------------------------------

    [Fact]
    public async Task AnExpiredRegistration_IsAWarning_AValidOneIsNot()
    {
        var expired = new PurchaseHarness(StorageCondition.RoomTemperature,
                                          registration: new SupplierRegistration("Biodis", "DMP-12", Today.AddDays(-1)));
        var valid = new PurchaseHarness(StorageCondition.RoomTemperature,
                                        registration: new SupplierRegistration("Biodis", "DMP-12", Today));

        var warning = await expired.Reception.RegistrationWarningAsync(Guid.NewGuid());
        warning!.ResourceKey.Should().Be("Supplier_RegistrationExpired");
        warning.Args.Should().Equal("Biodis", "14/10/2026");

        (await valid.Reception.RegistrationWarningAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public void ASupplierRegistration_ExpiresTheDayAfterItsEnd()
    {
        var supplier = new Supplier("BIO", "Biodis") { RegulatoryRegistrationValidUntil = Today };

        supplier.IsRegistrationExpired(Today).Should().BeFalse();
        supplier.IsRegistrationExpired(Today.AddDays(1)).Should().BeTrue();
        new Supplier("X", "Sans enregistrement").IsRegistrationExpired(Today).Should().BeFalse();
    }

    [Fact]
    public void ALotCertificate_AcceptsPdfAndImagesOnly()
    {
        LotCertificateFile.ContentTypeOf("CoA lot 2611.pdf").Should().Be("application/pdf");
        LotCertificateFile.ContentTypeOf("scan.JPG").Should().NotBeNull();
        LotCertificateFile.ContentTypeOf("macro.docm").Should().BeNull();
    }

    // ---- Harness ----------------------------------------------------------------------------------

    internal static IClock Clock() =>
        Stub<IClock>.With(("get_Now", _ => Now), ("get_Today", _ => Today));

    internal static IPermissionGuard Guard(params string[] denied) =>
        Stub<IPermissionGuard>.With(("Require", args =>
            denied.Contains((string)args![0]!)
                ? Result.Failure(Error.Forbidden("Forbidden"))
                : Result.Success()));

    internal sealed class Transaction : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A purchase order of one line, received through the services with stubbed storage.</summary>
    private sealed class PurchaseHarness
    {
        public PurchaseHarness(StorageCondition condition, string? denied = null,
                               SupplierRegistration? registration = null)
        {
            var siteId = Guid.NewGuid();
            Item = new StockItem("GLU-R1", new MultilingualText("Glucose R1"), StockItemKind.Reagent, "coffret", siteId)
            {
                StorageCondition = condition
            };
            Order = new PurchaseOrder("BC/2026/0042", Guid.NewGuid(), siteId, Today.AddDays(-10), Guid.NewGuid());
            Order.AddLine(new PurchaseOrderLine(Item.Id, 4m, 100m)).IsSuccess.Should().BeTrue();
            Order.Approve(Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
            Order.Send(Now).IsSuccess.Should().BeTrue();

            var guard = denied is null ? Guard() : Guard(denied);
            var user = Stub<ICurrentUser>.With(("get_SiteId", _ => siteId), ("get_UserId", _ => UserId));
            var unitOfWork = Stub<IUnitOfWork>.With(
                ("BeginTransactionAsync", _ => Task.FromResult<IAsyncDisposable>(new Transaction())),
                ("CommitAsync", _ => { Commits++; return Task.CompletedTask; }));
            var orders = Stub<IRepository<PurchaseOrder>>.With(
                ("GetByIdAsync", _ => Task.FromResult<PurchaseOrder?>(Order)),
                ("GetByIdWithDetailsAsync", _ => Task.FromResult<PurchaseOrder?>(Order)));
            var sequences = Stub<ISequenceAllocator>.With(
                ("NextAsync", _ => Task.FromResult(Result.Success("NC-2026-0007"))));

            Reception = new ReceptionService(
                Stub<IRepository<NonConformity>>.With(("AddAsync", args =>
                {
                    NonConformities.Add((NonConformity)args![0]!);
                    return Task.CompletedTask;
                })),
                Stub<IRepository<RefusedDelivery>>.With(("AddAsync", args =>
                {
                    Refusals.Add((RefusedDelivery)args![0]!);
                    return Task.CompletedTask;
                })),
                orders,
                Stub<IRepository<LotCertificateFile>>.Create(),
                Stub<IPurchasingQualityQueries>.With(
                    ("GetSupplierRegistrationAsync", _ => Task.FromResult(registration))),
                Stub<IInventoryQueries>.Create(),
                sequences, unitOfWork, guard, user, Clock());

            Orders = new PurchaseOrderService(
                orders,
                Stub<IRepository<StockItem>>.With(
                    ("GetByIdWithDetailsAsync", _ => Task.FromResult<StockItem?>(Item))),
                Stub<IRepository<StockMovement>>.Create(),
                Stub<IPurchaseOrderQueries>.Create(),
                sequences, unitOfWork, guard, user, Clock(), Reception);
        }

        public Guid UserId { get; } = Guid.NewGuid();
        public StockItem Item { get; }
        public PurchaseOrder Order { get; }
        public ReceptionService Reception { get; }
        public PurchaseOrderService Orders { get; }
        public List<NonConformity> NonConformities { get; } = [];
        public List<RefusedDelivery> Refusals { get; } = [];
        public int Commits { get; private set; }

        public Task<Result> ReceiveAsync(ReceptionCheck? check) =>
            Orders.ReceiveLineAsync(Order.Id, new ReceivePurchaseOrderLineRequest
            {
                LineId = Order.Lines.Single().Id,
                Quantity = 2m,
                LotNumber = "GLU-2611",
                ExpiryDate = Today.AddYears(1),
                Reception = check
            });
    }

    internal sealed class Stub<T> : ReviewFixesBilling2Tests.Stub<T> where T : class;
}
