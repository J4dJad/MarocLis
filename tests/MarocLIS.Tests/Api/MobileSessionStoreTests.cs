using FluentAssertions;
using MarocLIS.Api;
using MarocLIS.Api.Security;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using Xunit;

namespace MarocLIS.Tests.Api;

/// <summary>How long a phone stays signed in: the idle timeout of the password policy, and a ceiling.</summary>
public sealed class MobileSessionStoreTests
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 8, 0, 0, TimeSpan.FromHours(1));
        public DateOnly Today => DateOnly.FromDateTime(Now.Date);
    }

    private static readonly AuthenticatedSession Session = new(
        Guid.NewGuid(), "dr.alami", "Dr Karim ALAMI", Guid.NewGuid(), "PRINCIPAL", AppLanguage.French,
        ProfessionalQualification.MedicalBiologist, new HashSet<string>(), [], new HashSet<string>(), false);

    private static (MobileSessionStore Store, Clock Clock) Store()
    {
        var clock = new Clock();
        var store = new MobileSessionStore(new PasswordPolicy { IdleTimeout = TimeSpan.FromMinutes(15) },
                                           new MobileApiOptions { MaxSessionLifetime = TimeSpan.FromHours(12) }, clock);
        return (store, clock);
    }

    [Fact]
    public void ASessionUsedWithinTheIdleTimeout_Lives_AndEachUseRestartsTheDelay()
    {
        var (store, clock) = Store();
        var opened = store.Open(Session, "Mobile · test");

        for (var i = 0; i < 8; i++)
        {
            clock.Now = clock.Now.AddMinutes(14);
            store.Find(opened.Id).End.Should().Be(SessionEnd.None);
        }
    }

    [Fact]
    public void ASessionLeftIdle_Ends_AndIsForgotten()
    {
        var (store, clock) = Store();
        var opened = store.Open(Session, "Mobile · test");

        clock.Now = clock.Now.AddMinutes(16);
        store.Find(opened.Id).End.Should().Be(SessionEnd.Idle);
        store.Find(opened.Id).End.Should().Be(SessionEnd.Unknown);
        store.Count.Should().Be(0);
    }

    [Fact]
    public void ABusySession_StillEndsAtItsMaximumLifetime()
    {
        var (store, clock) = Store();
        var opened = store.Open(Session, "Mobile · test");

        var end = SessionEnd.None;
        for (var minutes = 0; minutes <= 12 * 60 && end == SessionEnd.None; minutes += 10)
        {
            clock.Now = clock.Now.AddMinutes(10);
            end = store.Find(opened.Id).End;
        }

        end.Should().Be(SessionEnd.Expired);
    }

    [Fact]
    public void SigningOut_ForgetsTheSession_AndIdentifiersAreNotGuessable()
    {
        var (store, _) = Store();
        var first = store.Open(Session, "Mobile · test");
        var second = store.Open(Session, "Mobile · test");

        first.Id.Should().NotBe(second.Id);
        first.Id.Length.Should().BeGreaterThanOrEqualTo(43, "256 random bits");

        store.Remove(first.Id);
        store.Find(first.Id).Session.Should().BeNull();
        store.Find(second.Id).End.Should().Be(SessionEnd.None);
    }
}
