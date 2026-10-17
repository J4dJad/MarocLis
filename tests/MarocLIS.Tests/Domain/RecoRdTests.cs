using FluentAssertions;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Localization;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// The owner's decisions, lot rd (HR, finance, administration and the cross-cutting texts): the
/// checks that need no database.
/// </summary>
public sealed class RecoRdTests
{
    private static readonly JsonLocalizer Localizer = new(new LocalizationOptions { MarkMissingKeys = true });

    // ---- 1. A refusal names the status in the operator's language ----------------------------------

    [Theory]
    [InlineData(AppLanguage.French, "Validation impossible depuis le statut « Validé biologiquement ».")]
    [InlineData(AppLanguage.English, "Validated")]
    public void ARefusalCarryingAnOrderLineStatus_ShowsItsLabel(AppLanguage language, string expected)
    {
        var text = Localizer.Describe(Error.Conflict("Item_CannotValidateFromStatus", LabOrderItemStatus.Validated), language);

        if (language == AppLanguage.French) text.Should().Be(expected);
        else text.Should().Contain(Localizer.Get("OrderItemStatus.Validated", language));
    }

    [Fact]
    public void ARefusalCarryingAStatus_IsTranslatedInArabicToo()
    {
        var text = Localizer.Describe(Error.Conflict("Po_NotEditable", PurchaseOrderStatus.Sent), AppLanguage.Arabic);

        text.Should().Contain(Localizer.Get("PurchaseOrderStatus.Sent", AppLanguage.Arabic)).And.NotContain("Sent");
    }

    [Fact]
    public void AnEnumWithNoLabel_KeepsItsName() =>
        Localizer.Format(AppLanguage.French, "Item_CannotValidateFromStatus", DayOfWeek.Monday)
                 .Should().Contain("Monday");

    // ---- 2. Counts agree with their number -----------------------------------------------------------

    [Theory]
    [InlineData(0, 0, "0 contrôle · 0 rejet à revoir")]
    [InlineData(1, 1, "1 contrôle · 1 rejet à revoir")]
    [InlineData(2, 5, "2 contrôles · 5 rejets à revoir")]
    public void AFrenchCount_TakesTheSingularForZeroAndOne(int controls, int rejections, string expected) =>
        Localizer.Format(AppLanguage.French, "QualityHr.Qc.Count", controls, rejections).Should().Be(expected);

    [Theory]
    [InlineData(1, "1 order")]
    [InlineData(0, "0 orders")]
    [InlineData(3, "3 orders")]
    public void AnEnglishCount_TakesTheSingularForOneOnly(int orders, string expected) =>
        Localizer.Format(AppLanguage.English, "StocksUi.Orders", orders).Should().Be(expected);

    [Fact]
    public void AnAdjectiveFurtherOn_AgreesWithItsOwnCount() =>
        Localizer.Format(AppLanguage.French, "QualityHr.Deadlines.Count", 3, 1)
                 .Should().Be("3 points à traiter · 1 en retard ou manquant");

    [Fact]
    public void ADecimalBalance_IsWrittenWithoutTrailingZeros() =>
        Localizer.Format(AppLanguage.French, "Leave_InsufficientBalance", 5m, 1.50m)
                 .Should().Be("Solde insuffisant : 5 jours demandés, 1,5 disponible.");

    // ---- 3. A concurrency conflict says so on every screen -------------------------------------------

    [Fact]
    public void AConflict_HasItsOwnMessage_EvenWrapped()
    {
        var conflict = new ConcurrencyConflictException("Message.ConcurrencyConflict", new InvalidOperationException());

        FailureMessages.KeyFor(conflict).Should().Be("Message.ConcurrencyConflict");
        FailureMessages.KeyFor(new AggregateException(conflict)).Should().Be("Message.ConcurrencyConflict");
        FailureMessages.KeyFor(new InvalidOperationException("x", conflict)).Should().Be("Message.ConcurrencyConflict");
        Localizer.Get(FailureMessages.KeyFor(conflict), AppLanguage.French).Should().Contain("modifié par un autre poste");
    }

    [Fact]
    public void AnyOtherFailure_StaysUnexpected()
    {
        FailureMessages.KeyFor(new InvalidOperationException()).Should().Be("Message.UnexpectedError");
        FailureMessages.KeyFor(null).Should().Be("Message.UnexpectedError");
    }

    // ---- 4 and 8. The shared reader refuses the exponent notation; empty is zero where it must be ----

    [Theory]
    [InlineData("1e5")]
    [InlineData("1E5")]
    [InlineData("2e-3")]
    [InlineData("1,5e2")]
    public void TheExponentNotation_IsUnreadable(string text)
    {
        EditorValues.ParseDecimal(text).Should().BeNull();
        EditorValues.IsUnreadable(text).Should().BeTrue();
        EditorValues.TryParseOrZero(text, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("", "0")]
    [InlineData("  ", "0")]
    [InlineData("1 250,50", "1250.50")]
    [InlineData("1.250,50", "1250.50")]
    [InlineData("1\u00A0250,50", "1250.50")]
    [InlineData("12.5", "12.5")]
    public void ThePayrollAndSettlementBoxes_ReadLikeTheOtherEditors(string text, string expected)
    {
        EditorValues.TryParseOrZero(text, out var value).Should().BeTrue();
        value.Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("10 %")]
    [InlineData("abc")]
    [InlineData("1000000000000")]
    public void ThePayrollAndSettlementBoxes_RefuseWhatIsNoNumber(string text) =>
        EditorValues.TryParseOrZero(text, out _).Should().BeFalse();

    // ---- 5. A holiday entered later is recounted on the leave ----------------------------------------

    [Fact]
    public void ALeave_RecountedWithANewHoliday_CountsOneDayFewer()
    {
        var monday = new DateOnly(2026, 11, 2);
        var leave = new LeaveRequest(Guid.NewGuid(), LeaveType.Annual, monday, monday.AddDays(5), false, false,
                                     6m, null, false, DateTimeOffset.UtcNow, Guid.NewGuid());

        leave.RecountDays(new HashSet<DateOnly> { monday.AddDays(4) }).Should().Be(1m);
        leave.Days.Should().Be(5m);

        // Counted again with the same calendar: nothing moves a second time.
        leave.RecountDays(new HashSet<DateOnly> { monday.AddDays(4) }).Should().Be(0m);
        leave.Days.Should().Be(5m);
    }

    [Fact]
    public void ALeaveCountedInCalendarDays_IsNotRecounted()
    {
        var start = new DateOnly(2026, 11, 2);
        var leave = new LeaveRequest(Guid.NewGuid(), LeaveType.Maternity, start, start.AddDays(97), false, false,
                                     98m, null, true, DateTimeOffset.UtcNow, Guid.NewGuid());

        leave.RecountDays(new HashSet<DateOnly> { start.AddDays(10) }).Should().Be(0m);
        leave.Days.Should().Be(98m);
    }
}
