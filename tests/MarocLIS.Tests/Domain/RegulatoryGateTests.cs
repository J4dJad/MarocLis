using FluentAssertions;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Morocco;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Domain.Sequencing;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Tests for the regulatory gates.
/// <para>
/// These are the invariants that exist because of a legal or normative requirement rather than a
/// product decision, so each test names the requirement it protects. A future change that makes one
/// of these fail is not a broken test — it is a compliance regression, and the test is there to say
/// so before it ships.
/// </para>
/// </summary>
public sealed class BiologistValidationGateTests
{
    private static readonly DateOnly Today = new(2026, 9, 15);

    private static AppUser Biologist(ProfessionalQualification qualification
                                         = ProfessionalQualification.MedicalBiologist,
                                     string? orderNumber = "MED-12345",
                                     DateOnly? validUntil = null,
                                     string? departments = null)
    {
        var user = new AppUser("biologiste", "Dr Benali", "hash")
        {
            Qualification = qualification,
            ProfessionalOrderNumber = orderNumber,
            QualificationValidUntil = validUntil,
            AuthorizedDepartmentCodesCsv = departments
        };
        return user;
    }

    [Theory]
    [InlineData(ProfessionalQualification.MedicalBiologist)]
    [InlineData(ProfessionalQualification.PharmacistBiologist)]
    public void AQualifiedBiologist_MayValidate(ProfessionalQualification qualification) =>
        Biologist(qualification).CanBiologicallyValidate("BIOCH", Today)
                                .IsSuccess.Should().BeTrue();

    [Theory]
    [InlineData(ProfessionalQualification.LaboratoryTechnician)]
    [InlineData(ProfessionalQualification.Nurse)]
    [InlineData(ProfessionalQualification.Administrative)]
    [InlineData(ProfessionalQualification.None)]
    public void ANonBiologist_MayNotValidate(ProfessionalQualification qualification)
    {
        // Loi n° 12-01 reserves the release of medical biology results to a qualified biologist.
        // This is enforced structurally, not by a permission grant an administrator could hand out.
        var result = Biologist(qualification).CanBiologicallyValidate("BIOCH", Today);

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Validation_RequiresBiologistQualification");
        result.FirstError.Code.Should().Be("regulatory");
    }

    [Fact]
    public void ABiologistWithNoOrderRegistration_MayNotValidate()
    {
        var result = Biologist(orderNumber: null).CanBiologicallyValidate("BIOCH", Today);

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Validation_RequiresOrderRegistration");
    }

    [Fact]
    public void ALapsedHabilitation_BlocksValidation()
    {
        // An habilitation can expire mid-shift, which is why the check is by date rather than by
        // a boolean somebody has to remember to clear.
        var result = Biologist(validUntil: Today.AddDays(-1))
            .CanBiologicallyValidate("BIOCH", Today);

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Validation_QualificationExpired");
    }

    [Fact]
    public void AnHabilitationExpiringToday_IsStillValid() =>
        Biologist(validUntil: Today).CanBiologicallyValidate("BIOCH", Today)
                                    .IsSuccess.Should().BeTrue();

    [Fact]
    public void ADepartmentOutsideTheValidationScope_IsRefused()
    {
        var result = Biologist(departments: "BIOCH,HEMA")
            .CanBiologicallyValidate("BACTE", Today);

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Validation_DepartmentOutOfScope");
    }

    [Fact]
    public void AnEmptyScope_MeansEveryDepartment()
    {
        // Only sensible for the responsible biologist, but it must work: an empty list is "all",
        // not "none", otherwise a fresh installation can validate nothing.
        Biologist(departments: null).CanBiologicallyValidate("BACTE", Today)
                                    .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void ADeactivatedUser_MayNotValidate()
    {
        var user = Biologist();
        user.Deactivate();

        user.CanBiologicallyValidate("BIOCH", Today).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void AdministratorPermissions_DoNotConferTheAbilityToValidate()
    {
        // An administrator holds every permission, including results.validate_biological, but has
        // no clinical qualification — so the structural gate still refuses. This is the whole point
        // of checking both.
        var admin = Biologist(ProfessionalQualification.Administrative);

        Permissions.All.Should().Contain(Permissions.Results.BiologicalValidate);
        admin.CanBiologicallyValidate("BIOCH", Today).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void TheTechnicianRole_DoesNotCarryTheBiologicalValidationPermission()
    {
        // Defence in depth: the role does not grant it and the qualification would refuse it.
        var technicianPermissions = RoleSetFor(SystemRoles.Technician);

        technicianPermissions.Should().Contain(Permissions.Results.TechnicalValidate);
        technicianPermissions.Should().NotContain(Permissions.Results.BiologicalValidate);
    }

    /// <summary>
    /// Reads a shipped role's permission set. Reflection into the internal seeder keeps the test
    /// honest about what actually ships rather than restating it.
    /// </summary>
    private static IReadOnlyCollection<string> RoleSetFor(string roleCode)
    {
        var type = typeof(MarocLIS.Infrastructure.Persistence.LisDbContext).Assembly
            .GetType("MarocLIS.Infrastructure.Persistence.RolePermissionSets");

        type.Should().NotBeNull("the shipped role definitions must remain discoverable");

        var build = type!.GetMethod("Build",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static);

        build.Should().NotBeNull();

        var definitions = (System.Collections.IEnumerable)build!.Invoke(null, null)!;

        foreach (var definition in definitions)
        {
            var tuple = definition.GetType();
            var code = (string)tuple.GetField("Item1")!.GetValue(definition)!;
            if (code != roleCode) continue;
            return (string[])tuple.GetField("Item3")!.GetValue(definition)!;
        }

        return [];
    }
}

/// <summary>Result release gates: quality control and critical-result communication.</summary>
public sealed class ResultReleaseGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 10, 0, 0, TimeSpan.FromHours(1));

    private static TestResult NewResult() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void AResultWithNoValue_CannotBeValidated()
    {
        var result = NewResult();

        result.BiologicallyValidate(Now, Guid.NewGuid(), false, false)
              .FirstError.ResourceKey.Should().Be("Result_NoValueToValidate");
    }

    [Fact]
    public void AnOutOfControlQc_BlocksReleaseWithoutAJustifiedOverride()
    {
        // ISO 15189 §5.6: patient results covered by an out-of-control run may not be released
        // without documented justification.
        var result = NewResult();
        var parameter = Parameter();

        result.EnterNumeric(parameter, 5.0m, Now, Guid.NewGuid());
        result.TechnicallyValidate(Now, Guid.NewGuid());
        result.FlagQcFailure();

        var refused = result.BiologicallyValidate(Now, Guid.NewGuid(),
                                                   qcOverrideJustified: false,
                                                   criticalCommunicated: true);

        refused.IsFailure.Should().BeTrue();
        refused.FirstError.ResourceKey.Should().Be("Result_QcOutOfControlBlocksValidation");

        var allowed = result.BiologicallyValidate(Now, Guid.NewGuid(),
                                                   qcOverrideJustified: true,
                                                   criticalCommunicated: true);

        allowed.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void AnUncommunicatedCriticalResult_CannotBeReleased()
    {
        // ISO 15189 §5.9.1: a panic value must reach the prescriber before the report does.
        var parameter = Parameter(criticalHigh: 4.00m);
        var result = NewResult();

        result.EnterNumeric(parameter, 5.50m, Now, Guid.NewGuid());
        result.ApplyInterpretation(parameter, null, AppLanguage.French, "g/L");
        result.TechnicallyValidate(Now, Guid.NewGuid());

        result.IsCritical.Should().BeTrue();

        var refused = result.BiologicallyValidate(Now, Guid.NewGuid(), true,
                                                   criticalCommunicated: false);

        refused.IsFailure.Should().BeTrue();
        refused.FirstError.ResourceKey.Should().Be("Result_CriticalMustBeCommunicated");
    }

    [Fact]
    public void AValidatedResult_CannotBeOverwrittenByAPlainEntry()
    {
        var parameter = Parameter();
        var result = NewResult();

        result.EnterNumeric(parameter, 1.00m, Now, Guid.NewGuid());
        result.TechnicallyValidate(Now, Guid.NewGuid());
        result.BiologicallyValidate(Now, Guid.NewGuid(), true, true);

        var refused = result.EnterNumeric(parameter, 9.99m, Now, Guid.NewGuid());

        refused.IsFailure.Should().BeTrue();
        refused.FirstError.ResourceKey.Should().Be("Result_ValidatedRequiresRevision");
        result.NumericValue.Should().Be(1.00m, "the released value must survive the attempt");
    }

    [Fact]
    public void RevisingAValidatedResult_KeepsTheOldValueAndRequiresAReason()
    {
        var parameter = Parameter();
        var result = NewResult();
        var biologist = Guid.NewGuid();

        result.EnterNumeric(parameter, 1.00m, Now, Guid.NewGuid());
        result.TechnicallyValidate(Now, Guid.NewGuid());
        result.BiologicallyValidate(Now, biologist, true, true);

        result.Revise(9.99m, null, null, Now, Guid.NewGuid(), reason: string.Empty)
              .FirstError.ResourceKey.Should().Be("Result_RevisionReasonRequired");

        var revised = result.Revise(9.99m, null, null, Now, Guid.NewGuid(),
                                    "Erreur de dilution détectée au contrôle");

        revised.IsSuccess.Should().BeTrue();

        var revision = result.Revisions.Should().ContainSingle().Subject;
        revision.PreviousNumericValue.Should().Be(1.00m);
        revision.WasValidated.Should().BeTrue();
        revision.PreviousValidatedByUserId.Should().Be(biologist);
        revision.Reason.Should().NotBeEmpty();

        // The revised value has to be validated again before it can be reported.
        result.Status.Should().Be(TestResultStatus.Entered);
        result.IsValidated.Should().BeFalse();
        result.Flags.Should().HaveFlag(ResultFlag.Corrected);
    }

    [Fact]
    public void AValueOutsideTheMeasuringRange_IsCensoredRatherThanReportedAsANumber()
    {
        var parameter = Parameter();
        parameter.MeasuringRangeLow = 0.10m;
        parameter.MeasuringRangeHigh = 8.00m;

        var result = NewResult();
        result.EnterNumeric(parameter, 12.00m, Now, Guid.NewGuid());

        result.NumericValue.Should().Be(8.00m);
        result.ValueOperator.Should().Be(">");
        result.FormatValue(AppLanguage.French).Should().StartWith(">");
    }

    [Fact]
    public void ATechnicallyImpossibleValue_IsRefusedOutright()
    {
        var parameter = Parameter();
        parameter.TechnicalLimitHigh = 12.00m;

        var result = NewResult();
        var refused = result.EnterNumeric(parameter, 900m, Now, Guid.NewGuid());

        refused.IsFailure.Should().BeTrue();
        refused.FirstError.ResourceKey.Should().Be("Result_TechnicallyImpossible");
        result.HasValue.Should().BeFalse();
    }

    [Fact]
    public void ANewValue_InvalidatesTheEarlierTechnicalCheck()
    {
        var parameter = Parameter();
        var result = NewResult();

        result.EnterNumeric(parameter, 1.00m, Now, Guid.NewGuid());
        result.TechnicallyValidate(Now, Guid.NewGuid());
        result.Status.Should().Be(TestResultStatus.TechnicallyValidated);

        result.EnterNumeric(parameter, 2.00m, Now, Guid.NewGuid());

        result.Status.Should().Be(TestResultStatus.Entered);
        result.TechnicallyValidatedAt.Should().BeNull();
    }

    private static MarocLIS.Domain.Catalog.TestParameter Parameter(decimal? criticalHigh = null)
    {
        var parameter = new MarocLIS.Domain.Catalog.TestParameter(
            "GLY", new MultilingualText("Glycémie"),
            MarocLIS.Domain.Catalog.ResultValueType.Numeric)
        {
            Decimals = 2,
            CriticalHigh = criticalHigh
        };
        return parameter;
    }
}

/// <summary>Order workflow gates.</summary>
public sealed class OrderWorkflowGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 9, 0, 0, TimeSpan.FromHours(1));

    private static LabOrder NewOrder() =>
        new("2609-000001", Guid.NewGuid(), Guid.NewGuid(), Now, Guid.NewGuid());

    [Fact]
    public void AnOrderWithAValidatedAnalysis_CannotBeCancelled()
    {
        // A released result has left the laboratory's sole control; making it disappear is not a
        // lawful correction. An amended report is.
        var order = NewOrder();
        var item = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);

        order.AddItem(item);
        item.MarkCollected(Now);
        item.MarkReceived(Now, null);
        item.MarkResultEntered(Now);
        item.MarkTechnicallyValidated(Now, Guid.NewGuid());
        item.MarkBiologicallyValidated(Now, Guid.NewGuid());

        var refused = order.Cancel(Now, Guid.NewGuid(), "Demande annulée par le patient");

        refused.IsFailure.Should().BeTrue();
        refused.FirstError.ResourceKey.Should().Be("Order_HasValidatedItemsCannotCancel");
    }

    [Fact]
    public void CancellingAnOrder_RequiresAReason()
    {
        var order = NewOrder();
        order.AddItem(new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine));

        order.Cancel(Now, Guid.NewGuid(), "  ")
             .FirstError.ResourceKey.Should().Be("Order_CancellationReasonRequired");
    }

    [Fact]
    public void BiologicalValidation_RequiresTheTechnicalCheckFirst()
    {
        var item = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);
        item.MarkCollected(Now);
        item.MarkReceived(Now, null);
        item.MarkResultEntered(Now);

        var refused = item.MarkBiologicallyValidated(Now, Guid.NewGuid());

        refused.IsFailure.Should().BeTrue();
        refused.FirstError.ResourceKey.Should().Be("Item_TechnicalValidationRequiredFirst");
    }

    [Fact]
    public void AnAnalysisCannotBeReported_BeforeItIsValidated()
    {
        var item = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);
        item.MarkCollected(Now);
        item.MarkReceived(Now, null);
        item.MarkResultEntered(Now);

        item.MarkReported(Now).FirstError.ResourceKey
            .Should().Be("Item_MustBeValidatedBeforeReporting");
    }

    [Fact]
    public void AReportedAnalysis_CannotBeUnvalidated()
    {
        var item = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);
        item.MarkCollected(Now);
        item.MarkReceived(Now, null);
        item.MarkResultEntered(Now);
        item.MarkTechnicallyValidated(Now, Guid.NewGuid());
        item.MarkBiologicallyValidated(Now, Guid.NewGuid());
        item.MarkReported(Now);

        item.Unvalidate("Erreur détectée")
            .FirstError.ResourceKey.Should().Be("Item_AlreadyReportedRequiresAmendment");
    }

    [Fact]
    public void OrderStatus_IsDerivedFromItsItemsAndNeverContradictsThem()
    {
        var order = NewOrder();
        var first = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);
        var second = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);

        order.AddItem(first);
        order.AddItem(second);

        foreach (var item in (LabOrderItem[])[first, second])
        {
            item.MarkCollected(Now);
            item.MarkReceived(Now, null);
            item.MarkResultEntered(Now);
            item.MarkTechnicallyValidated(Now, Guid.NewGuid());
        }

        first.MarkBiologicallyValidated(Now, Guid.NewGuid());
        order.RecomputeStatus();
        order.Status.Should().Be(LabOrderStatus.PartiallyValidated);

        second.MarkBiologicallyValidated(Now, Guid.NewGuid());
        order.RecomputeStatus();
        order.Status.Should().Be(LabOrderStatus.Validated);
        order.CompletionRatio.Should().Be(1m);
    }

    [Fact]
    public void ADuplicateAnalysis_IsRefused()
    {
        var order = NewOrder();
        var analysisId = Guid.NewGuid();

        order.AddItem(new LabOrderItem(analysisId, OrderPriority.Routine))
             .IsSuccess.Should().BeTrue();

        order.AddItem(new LabOrderItem(analysisId, OrderPriority.Routine))
             .FirstError.ResourceKey.Should().Be("Order_AnalysisAlreadyPresent");
    }

    [Fact]
    public void ADiscount_MustBeJustified()
    {
        var order = NewOrder();
        order.AddItem(new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine));

        // A prescriber, so the only thing left to fail on is the discount. Without it the second
        // assertion below was failing on Order_PrescriberRequired and saying nothing about discounts.
        order.PrescriberId = Guid.NewGuid();
        order.DiscountPercent = 20m;

        order.ValidateForRegistration().Errors
             .Should().Contain(e => e.ResourceKey == "Order_DiscountReasonRequired");

        order.DiscountReason = "Personnel du laboratoire";
        order.ValidateForRegistration().IsSuccess.Should().BeTrue();
    }
}

/// <summary>Invoice immutability and the unbroken numbering series.</summary>
public sealed class InvoiceIntegrityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 11, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateOnly Today = new(2026, 9, 15);

    private static Invoice Draft()
    {
        var invoice = new Invoice(string.Empty, Guid.NewGuid(), InvoiceKind.Invoice,
                                  Today, Guid.NewGuid())
        {
            BilledToName = "Patient Test"
        };
        invoice.AddLine(new InvoiceLine("Glycémie", 30m));
        return invoice;
    }

    [Fact]
    public void AnIssuedInvoice_IsImmutable()
    {
        // DGI: an issued invoice cannot be altered. Every mutation path refuses.
        var invoice = Draft();
        invoice.Issue("FAC/CASA/2026/000001", Now, "trente dirhams");

        invoice.AddLine(new InvoiceLine("Urée", 20m))
               .FirstError.ResourceKey.Should().Be("Invoice_IssuedIsImmutable");

        invoice.ApplyDiscount(5m, "Geste commercial")
               .FirstError.ResourceKey.Should().Be("Invoice_IssuedIsImmutable");

        invoice.RemoveLine(invoice.Lines.First().Id)
               .FirstError.ResourceKey.Should().Be("Invoice_IssuedIsImmutable");
    }

    [Fact]
    public void AnIssuedInvoice_CannotBeCancelled_OnlyCreditNoted()
    {
        // Cancelling would leave a hole in the chronological series. A credit note keeps it intact.
        var invoice = Draft();
        invoice.Issue("FAC/CASA/2026/000002", Now, null);

        var refused = invoice.CancelDraft("Erreur de saisie");

        refused.IsFailure.Should().BeTrue();
        refused.FirstError.ResourceKey.Should().Be("Invoice_IssuedRequiresCreditNote");
        refused.FirstError.Code.Should().Be("regulatory");
    }

    [Fact]
    public void ADraftInvoice_CanStillBeCancelled()
    {
        // An abandoned draft has no number, so cancelling it leaves no gap.
        var invoice = Draft();

        invoice.CancelDraft("Abandonné au comptoir").IsSuccess.Should().BeTrue();
        invoice.Status.Should().Be(InvoiceStatus.Cancelled);
    }

    [Fact]
    public void AnInvoice_CannotBeIssuedTwice()
    {
        var invoice = Draft();
        invoice.Issue("FAC/CASA/2026/000003", Now, null);

        invoice.Issue("FAC/CASA/2026/000004", Now, null)
               .FirstError.ResourceKey.Should().Be("Invoice_AlreadyIssued");
    }

    [Fact]
    public void AnEmptyInvoice_CannotBeIssued()
    {
        var invoice = new Invoice(string.Empty, Guid.NewGuid(), InvoiceKind.Invoice,
                                  Today, Guid.NewGuid()) { BilledToName = "X" };

        invoice.Issue("FAC/CASA/2026/000005", Now, null)
               .FirstError.ResourceKey.Should().Be("Invoice_NoLines");
    }

    [Fact]
    public void APaymentLargerThanTheBalance_IsRefused()
    {
        var invoice = Draft();
        invoice.Issue("FAC/CASA/2026/000006", Now, null);

        var payment = new Payment(50m, PaymentMethod.Cash, Today, Guid.NewGuid(), Guid.NewGuid());

        invoice.RecordPayment(payment)
               .FirstError.ResourceKey.Should().Be("Payment_ExceedsOutstanding");
    }

    [Fact]
    public void SettlingInFull_ClosesTheInvoice()
    {
        var invoice = Draft();
        invoice.Issue("FAC/CASA/2026/000007", Now, null);

        var payment = new Payment(30m, PaymentMethod.Cash, Today, Guid.NewGuid(), Guid.NewGuid());

        invoice.RecordPayment(payment).IsSuccess.Should().BeTrue();
        invoice.Status.Should().Be(InvoiceStatus.Paid);
        invoice.OutstandingAmount.Should().Be(0m);
    }

    [Fact]
    public void ALargeCashPayment_RaisesTheTaxThresholdWarning()
    {
        // Moroccan tax law penalises cash settlement above 20 000 MAD; the till warns before
        // the money is taken rather than after.
        var payment = new Payment(25_000m, PaymentMethod.Cash, Today, Guid.NewGuid(), Guid.NewGuid());

        payment.RequiresTraceableMethodWarning.Should().BeTrue();

        var byTransfer = new Payment(25_000m, PaymentMethod.BankTransfer, Today,
                                     Guid.NewGuid(), Guid.NewGuid());

        byTransfer.RequiresTraceableMethodWarning.Should().BeFalse();
    }

    [Fact]
    public void BiologyLines_AreVatExemptByDefault()
    {
        var line = new InvoiceLine("Glycémie", 30m);

        line.IsVatExempt.Should().BeTrue();
        line.VatAmount.Should().Be(0m);
        line.TotalAmount.Should().Be(30m);
    }

    [Fact]
    public void ThePayerShare_IsCappedAtTheLineTotal()
    {
        var line = new InvoiceLine("TSH", 20m) { ReferenceTariffBase = 30m };

        line.SetCoverageSplit(1.0m, payerUsesReferenceTariff: true);

        line.PayerShare.Should().Be(20m);
        line.PatientShare.Should().Be(0m);
    }
}

/// <summary>The gapless numbering that the tax administration requires.</summary>
public sealed class NumberSequenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.FromHours(1));

    [Fact]
    public void Allocation_IsSequentialWithoutGaps()
    {
        var sequence = new NumberSequence(SequencePurpose.Invoice, Guid.NewGuid(), 2026,
                                          "FAC/{SITE}/{YEAR}/{SEQ:6}");

        var allocated = Enumerable.Range(0, 5)
            .Select(_ => sequence.Allocate(Now, "CASA").Value)
            .ToList();

        allocated.Should().Equal(
            "FAC/CASA/2026/000001", "FAC/CASA/2026/000002", "FAC/CASA/2026/000003",
            "FAC/CASA/2026/000004", "FAC/CASA/2026/000005");

        sequence.LastAllocatedValue.Should().Be(5);
        sequence.NextValue.Should().Be(6);
    }

    [Fact]
    public void AClosedSequence_RefusesFurtherAllocation()
    {
        // Once a financial year's accounts are filed, no further document may be numbered into it.
        var sequence = new NumberSequence(SequencePurpose.Invoice, Guid.NewGuid(), 2025, "{SEQ:6}");
        sequence.Close(Now);

        var refused = sequence.Allocate(Now, "CASA");

        refused.IsFailure.Should().BeTrue();
        refused.FirstError.ResourceKey.Should().Be("Sequence_Closed");
        refused.FirstError.Code.Should().Be("regulatory");
    }

    [Theory]
    [InlineData("{SEQ:6}", "000001")]
    [InlineData("{SEQ:4}", "0001")]
    [InlineData("{SEQ}", "000001")]
    [InlineData("{YY}{MM}-{SEQ:6}", "2609-000001")]
    [InlineData("NC-{YEAR}-{SEQ:4}", "NC-2026-0001")]
    public void FormatTokens_AreRenderedAsExpected(string format, string expected)
    {
        var sequence = new NumberSequence(SequencePurpose.Accession, null, 2026, format);

        sequence.Allocate(Now, null).Value.Should().Be(expected);
    }

    [Fact]
    public void Peeking_DoesNotConsumeANumber()
    {
        var sequence = new NumberSequence(SequencePurpose.Invoice, null, 2026, "{SEQ:6}");

        sequence.Render(sequence.NextValue, Now, null).Should().Be("000001");
        sequence.NextValue.Should().Be(1, "rendering a preview must not burn a number");
    }
}

/// <summary>Moroccan administrative identifier validation.</summary>
public sealed class MoroccanIdentifierTests
{
    [Theory]
    [InlineData("A123456")]
    [InlineData("BE12345")]
    [InlineData("a123456")]      // case-insensitive
    [InlineData("BE 12345")]     // spaces tolerated
    [InlineData("BE-12345")]
    public void ValidCin_IsAccepted(string cin) =>
        MoroccanIdentifiers.IsValidCin(cin).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("1234567")]      // no letter prefix
    [InlineData("ABC12345")]     // three letters
    [InlineData("A1234")]        // too few digits
    [InlineData("A1234567")]     // too many digits
    public void InvalidCin_IsRejected(string cin) =>
        MoroccanIdentifiers.IsValidCin(cin).Should().BeFalse();

    [Fact]
    public void Ice_MustBeExactlyFifteenDigits()
    {
        MoroccanIdentifiers.IsValidIce("001234567000089").Should().BeTrue();
        MoroccanIdentifiers.IsValidIce("00123456700008").Should().BeFalse();
        MoroccanIdentifiers.IsValidIce("0012345670000891").Should().BeFalse();
    }

    [Theory]
    [InlineData("0612345678", "+212612345678")]
    [InlineData("+212612345678", "+212612345678")]
    [InlineData("06 12 34 56 78", "+212612345678")]
    [InlineData("06-12-34-56-78", "+212612345678")]
    [InlineData("0522123456", "+212522123456")]
    public void MoroccanPhones_AreNormalisedToE164(string input, string expected) =>
        MoroccanIdentifiers.TryNormalizePhone(input).Should().Be(expected);

    [Theory]
    [InlineData("0412345678")]   // invalid leading digit
    [InlineData("061234567")]    // too short
    [InlineData("+33612345678")] // not Moroccan
    public void NonMoroccanOrMalformedPhones_ReturnNull(string input) =>
        MoroccanIdentifiers.TryNormalizePhone(input).Should().BeNull();
}

/// <summary>Sample conformity decisions, which drive the pre-analytical quality indicators.</summary>
public sealed class SampleConformityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 8, 0, 0, TimeSpan.FromHours(1));

    private static Sample NewSample() =>
        new("0000000001", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void ADefectiveSample_CannotBeReceivedWithoutAnExplicitDecision()
    {
        // ISO 15189 §5.4: acceptance criteria are documented and a non-conforming sample requires
        // a recorded decision, not a silent acceptance.
        var sample = NewSample();
        sample.Collect(Now, Guid.NewGuid(), 4m, 2m);

        var refused = sample.Receive(Now, Guid.NewGuid(), SampleDefect.Haemolysed,
                                      acceptWithReservation: false, 4m, null);

        refused.IsFailure.Should().BeTrue();
        refused.FirstError.ResourceKey.Should().Be("Sample_DefectsRequireExplicitDecision");
    }

    [Fact]
    public void AcceptingWithReservation_RecordsTheDefectForTheReport()
    {
        var sample = NewSample();
        sample.Collect(Now, Guid.NewGuid(), 4m, 2m);

        sample.Receive(Now, Guid.NewGuid(), SampleDefect.Haemolysed,
                       acceptWithReservation: true, 4m, "Hémolyse légère")
              .IsSuccess.Should().BeTrue();

        sample.IsAcceptedWithReservation.Should().BeTrue();
        sample.Defects.Should().HaveFlag(SampleDefect.Haemolysed);
        sample.Status.Should().Be(SampleStatus.Received);
    }

    [Fact]
    public void AnUnderfilledTube_IsFlaggedAtCollection()
    {
        var sample = NewSample();

        sample.Collect(Now, Guid.NewGuid(), volumeMl: 1m, minimumVolumeMl: 2.5m);

        sample.Defects.Should().HaveFlag(SampleDefect.InsufficientVolume);
    }

    [Fact]
    public void ASampleCannotBeDispatched_BeforeItIsCollected()
    {
        NewSample().Dispatch(Now, Guid.NewGuid(), null)
                   .FirstError.ResourceKey.Should().Be("Sample_MustBeCollectedBeforeDispatch");
    }

    [Fact]
    public void EveryMovement_IsRecordedInTheChainOfCustody()
    {
        var sample = NewSample();
        var user = Guid.NewGuid();

        sample.Collect(Now, user, 5m, 2m);
        sample.Dispatch(Now.AddMinutes(10), user, null);
        sample.Receive(Now.AddMinutes(40), user, SampleDefect.None, false, 6m, null);
        sample.MoveToStorage(Now.AddHours(2), user, "Frigo 1", "A3", null);

        sample.Events.Select(e => e.Kind).Should().Contain(
        [
            SampleEventKind.Collected,
            SampleEventKind.Dispatched,
            SampleEventKind.Received,
            SampleEventKind.Stored
        ]);

        sample.TransportDelay.Should().Be(TimeSpan.FromMinutes(40));
    }

    [Fact]
    public void RejectingASample_RecordsAReasonFromTheControlledList()
    {
        var sample = NewSample();
        sample.Collect(Now, Guid.NewGuid(), 5m, 2m);

        sample.Reject(Now, Guid.NewGuid(), SampleRejectionReason.IdentificationMismatch,
                      "Nom discordant avec l'étiquette", requestRecollection: true)
              .IsSuccess.Should().BeTrue();

        sample.Status.Should().Be(SampleStatus.Rejected);
        sample.RejectionReason.Should().Be(SampleRejectionReason.IdentificationMismatch);
        sample.RecollectionRequested.Should().BeTrue();
    }
}

/// <summary>Reference-interval selection, which decides what is reported as abnormal.</summary>
public sealed class ReferenceRangeSelectionTests
{
    private static readonly DateOnly Today = new(2026, 9, 15);

    private static MarocLIS.Domain.Catalog.TestParameter Haemoglobin()
    {
        var parameter = new MarocLIS.Domain.Catalog.TestParameter(
            "HB", new MultilingualText("Hémoglobine"),
            MarocLIS.Domain.Catalog.ResultValueType.Numeric) { Decimals = 1 };

        parameter.AddReferenceRange(
            MarocLIS.Domain.Catalog.ReferenceRange.Numeric(13.0m, 17.0m, Gender.Male));
        parameter.AddReferenceRange(
            MarocLIS.Domain.Catalog.ReferenceRange.Numeric(12.0m, 15.0m, Gender.Female));
        parameter.AddReferenceRange(
            MarocLIS.Domain.Catalog.ReferenceRange.Numeric(14.0m, 22.0m, ageFromDays: 0, ageToDays: 14));

        var pregnancy = MarocLIS.Domain.Catalog.ReferenceRange.Numeric(11.0m, 14.0m, Gender.Female);
        pregnancy.AppliesToPregnancy = true;
        parameter.AddReferenceRange(pregnancy);

        return parameter;
    }

    [Fact]
    public void AMale_GetsTheMaleInterval()
    {
        var range = Haemoglobin().ResolveRange(
            new MarocLIS.Domain.Catalog.ReferenceRangeContext(
                Gender.Male, 35 * 365, 35, false, null, null));

        range!.LowerLimit.Should().Be(13.0m);
    }

    [Fact]
    public void APregnantWoman_GetsTheGestationalInterval_NotTheGenericFemaleOne()
    {
        // The pregnancy interval is more specific, so it must win. Applying the non-pregnant
        // interval would report a normal gestational haemoglobin as anaemic.
        var range = Haemoglobin().ResolveRange(
            new MarocLIS.Domain.Catalog.ReferenceRangeContext(
                Gender.Female, 30 * 365, 30, true, 2, null));

        range!.LowerLimit.Should().Be(11.0m);
    }

    [Fact]
    public void ANeonate_GetsTheAgeBandedInterval()
    {
        // Without it, every newborn's physiologically high haemoglobin would flag as critical.
        var range = Haemoglobin().ResolveRange(
            new MarocLIS.Domain.Catalog.ReferenceRangeContext(
                Gender.Male, AgeInDays: 3, AgeInYears: 0, false, null, null));

        range!.UpperLimit.Should().Be(22.0m);
    }

    [Fact]
    public void Trimester_IsComputedFromTheLastMenstrualPeriod()
    {
        var lmp = Today.AddDays(-70);   // ten weeks

        MarocLIS.Domain.Catalog.ReferenceRangeContext.TrimesterFrom(lmp, Today).Should().Be(1);
        MarocLIS.Domain.Catalog.ReferenceRangeContext.TrimesterFrom(Today.AddDays(-140), Today)
            .Should().Be(2);
        MarocLIS.Domain.Catalog.ReferenceRangeContext.TrimesterFrom(Today.AddDays(-250), Today)
            .Should().Be(3);
        MarocLIS.Domain.Catalog.ReferenceRangeContext.TrimesterFrom(Today.AddDays(5), Today)
            .Should().BeNull("a future LMP is not interpretable");
    }

    [Fact]
    public void AOneSidedInterval_OnlyFlagsOnTheSideItDefines()
    {
        var range = MarocLIS.Domain.Catalog.ReferenceRange.Numeric(null, 2.00m);

        range.Interpret(3.00m).Should().Be(MarocLIS.Domain.Catalog.ResultInterpretation.High);
        range.Interpret(0.01m).Should().Be(MarocLIS.Domain.Catalog.ResultInterpretation.Normal);
    }

    [Fact]
    public void AnInvalidRange_IsRejectedAtConfigurationTime()
    {
        var inverted = MarocLIS.Domain.Catalog.ReferenceRange.Numeric(10m, 5m);
        inverted.Validate().Errors.Should().Contain(e => e.ResourceKey == "Range_LowerAboveUpper");

        var contradictory = MarocLIS.Domain.Catalog.ReferenceRange.Numeric(1m, 2m, Gender.Male);
        contradictory.AppliesToPregnancy = true;
        contradictory.Validate().Errors
            .Should().Contain(e => e.ResourceKey == "Range_PregnancyRequiresFemale");
    }

    [Fact]
    public void DeltaCheck_FiresOnALargeChangeWithinTheWindow()
    {
        var parameter = new MarocLIS.Domain.Catalog.TestParameter(
            "K", new MultilingualText("Potassium"),
            MarocLIS.Domain.Catalog.ResultValueType.Numeric)
        {
            Decimals = 1,
            DeltaCheckPercent = 25m,
            DeltaCheckWindowDays = 3
        };

        parameter.FailsDeltaCheck(6.0m, 4.0m, 1).Should().BeTrue();
        parameter.FailsDeltaCheck(4.2m, 4.0m, 1).Should().BeFalse();

        // Outside the window the values are no longer comparable.
        parameter.FailsDeltaCheck(6.0m, 4.0m, 30).Should().BeFalse();

        // A previous value of zero makes a percentage change undefined.
        parameter.FailsDeltaCheck(6.0m, 0m, 1).Should().BeFalse();
    }
}

/// <summary>Patient-level obligations under loi 08-09.</summary>
public sealed class PatientDataProtectionTests
{
    private static readonly DateOnly Today = new(2026, 9, 15);
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 14, 0, 0, TimeSpan.FromHours(1));

    private static Patient NewPatient(DateOnly? dob = null) =>
        new("P-00000001", "Benani", "Youssef", dob ?? new DateOnly(1985, 3, 15),
            Gender.Male, Guid.NewGuid());

    [Fact]
    public void Anonymisation_ClearsIdentifiersButKeepsTheAgeBandAndTheFile()
    {
        // The right to erasure has to coexist with a ten-year medical retention obligation and an
        // unalterable invoice series, so the file is pseudonymised rather than deleted.
        var patient = NewPatient();
        patient.Cin = "A123456";
        patient.MobilePhone = "+212612345678";
        patient.AddressLine = "12 rue X";

        patient.Anonymize(Now, "0001");

        patient.IsAnonymized.Should().BeTrue();
        patient.Cin.Should().BeNull();
        patient.MobilePhone.Should().BeNull();
        patient.AddressLine.Should().BeNull();
        patient.LastName.Should().Be("ANONYME-0001");

        // The year of birth survives, because reference-range selection needs an age band.
        patient.DateOfBirth.Should().Be(new DateOnly(1985, 1, 1));
        patient.AgeInYears(Today).Should().Be(41);
    }

    [Fact]
    public void RetentionHorizon_IsPushedOutByEachNewAct()
    {
        var patient = NewPatient();

        patient.ExtendRetention(new DateOnly(2020, 1, 1), 10);
        patient.RetentionUntil.Should().Be(new DateOnly(2030, 1, 1));

        patient.ExtendRetention(new DateOnly(2026, 9, 15), 10);
        patient.RetentionUntil.Should().Be(new DateOnly(2036, 9, 15));

        // An older act must not pull the horizon back in.
        patient.ExtendRetention(new DateOnly(2019, 1, 1), 10);
        patient.RetentionUntil.Should().Be(new DateOnly(2036, 9, 15));
    }

    [Fact]
    public void AMinor_RequiresAGuardianOnFile()
    {
        var minor = NewPatient(Today.AddYears(-10));

        minor.Validate(Today).Errors
             .Should().Contain(e => e.ResourceKey == "Patient_MinorRequiresGuardian");

        minor.GuardianFullName = "Benani Fatima";
        minor.Validate(Today).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void WithdrawingAConsent_IsAppendOnly()
    {
        var patient = NewPatient();
        var consent = new PatientConsent(patient.Id, ConsentPurpose.GeneticTesting,
                                          ConsentChannel.WrittenForm, Now, Guid.NewGuid());
        patient.RecordConsent(consent);

        patient.HasConsent(ConsentPurpose.GeneticTesting).Should().BeTrue();

        consent.Withdraw(Now.AddDays(1), Guid.NewGuid(), "Retrait à la demande du patient");

        patient.HasConsent(ConsentPurpose.GeneticTesting).Should().BeFalse();
        patient.Consents.Should().ContainSingle("the record is kept as evidence, not deleted");
        consent.WithdrawnAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData(ConsentPurpose.GeneticTesting)]
    [InlineData(ConsentPurpose.HivSerology)]
    [InlineData(ConsentPurpose.PrenatalScreening)]
    [InlineData(ConsentPurpose.ResearchUse)]
    public void SensitivePurposes_RequireWrittenEvidence(ConsentPurpose purpose) =>
        PatientConsent.RequiresWrittenEvidence(purpose).Should().BeTrue();

    [Fact]
    public void TheGeneralDiagnosticConsent_MayBeVerbal() =>
        PatientConsent.RequiresWrittenEvidence(ConsentPurpose.Diagnostic).Should().BeFalse();

    [Fact]
    public void AgeInYears_HandlesTheBirthdayBoundary()
    {
        var patient = NewPatient(new DateOnly(2000, 9, 16));

        patient.AgeInYears(new DateOnly(2026, 9, 15)).Should().Be(25, "the day before the birthday");
        patient.AgeInYears(new DateOnly(2026, 9, 16)).Should().Be(26, "on the birthday");
    }
}
