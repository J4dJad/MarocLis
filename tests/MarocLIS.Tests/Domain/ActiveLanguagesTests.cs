using FluentAssertions;
using MarocLIS.Api.Endpoints;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Sequencing;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Paramètres › Langues: French always on, Arabic and English as the laboratory chooses. The stored
/// value read back, a language switched off becoming French wherever one is assigned, the reading
/// kept a minute and replaced at once by a save, and the phone refused a language switched off.
/// </summary>
public sealed class ActiveLanguagesTests
{
    private static Dictionary<string, string?> Stored(string? value) =>
        new() { [LanguageSettings.ActiveLanguagesKey] = value };

    [Fact]
    public void AnAbsentOrBlankSetting_KeepsTheThreeLanguages()
    {
        LanguageSettings.FromValues(new Dictionary<string, string?>()).Active
            .Should().Equal(AppLanguage.French, AppLanguage.Arabic, AppLanguage.English);
        LanguageSettings.FromValues(Stored("  ")).Should().Be(LanguageSettings.Default);
        LanguageSettings.FromValues(Stored(null)).Should().Be(LanguageSettings.Default);
    }

    [Fact]
    public void FrenchIsAlwaysKept_WhateverIsStored_AndUnknownNamesAreIgnored()
    {
        LanguageSettings.FromValues(Stored("English")).Active.Should().Equal(AppLanguage.French, AppLanguage.English);
        LanguageSettings.FromValues(Stored("arabic; Klingon")).Active.Should().Equal(AppLanguage.French, AppLanguage.Arabic);
        LanguageSettings.FromValues(Stored("Klingon")).Active.Should().Equal(AppLanguage.French);
        LanguageSettings.FromValues(Stored("English,French")).Active
            .Should().Equal([AppLanguage.French, AppLanguage.English], "always in the order the lists show them");
        LanguageSettings.Of([]).Active.Should().Equal(AppLanguage.French);
    }

    [Fact]
    public void TheStoredValue_IsReadBack()
    {
        var settings = LanguageSettings.Of([AppLanguage.English]);
        var values = settings.ToValues().ToDictionary(v => v.Key, v => (string?)v.Value);

        values[LanguageSettings.ActiveLanguagesKey].Should().Be("French,English");
        LanguageSettings.FromValues(values).Should().Be(settings);
    }

    [Fact]
    public void Coerce_KeepsAnActiveLanguage_AndTurnsOneSwitchedOffIntoFrench()
    {
        var frenchAndEnglish = LanguageSettings.Of([AppLanguage.English]);

        frenchAndEnglish.Coerce(AppLanguage.English).Should().Be(AppLanguage.English);
        frenchAndEnglish.Coerce(AppLanguage.Arabic).Should().Be(AppLanguage.French);
        frenchAndEnglish.Coerce(AppLanguage.French).Should().Be(AppLanguage.French);
        LanguageSettings.Default.Coerce(AppLanguage.Arabic).Should().Be(AppLanguage.Arabic);
    }

    [Fact]
    public void ARegisteredFile_IsNeverGivenALanguageSwitchedOff()
    {
        var frenchOnly = LanguageSettings.Of([]);
        var frenchAndArabic = LanguageSettings.Of([AppLanguage.Arabic]);

        OrderRegistrationService.ReportLanguageFor(null, AppLanguage.Arabic, frenchAndArabic)
            .Should().Be(AppLanguage.Arabic, "the patient's language, when the laboratory works in it");
        OrderRegistrationService.ReportLanguageFor(null, AppLanguage.Arabic, frenchOnly).Should().Be(AppLanguage.French);
        OrderRegistrationService.ReportLanguageFor(AppLanguage.English, AppLanguage.Arabic, frenchAndArabic)
            .Should().Be(AppLanguage.French, "a language asked for but switched off");
        OrderRegistrationService.ReportLanguageFor(AppLanguage.Arabic, AppLanguage.French, LanguageSettings.Default)
            .Should().Be(AppLanguage.Arabic);
    }

    [Fact]
    public void TheSetting_IsOwnedByItsScreen_NotTheGenericList()
    {
        ConfigurationService.IsDedicated(LanguageSettings.ActiveLanguagesKey).Should().BeTrue();
    }

    [Fact]
    public async Task TheReading_IsKeptAMinute_ThenReadAgain()
    {
        var harness = new Harness("French,English");

        (await harness.Service.CoerceAsync(AppLanguage.Arabic)).Should().Be(AppLanguage.French);
        (await harness.Service.CoerceAsync(AppLanguage.English)).Should().Be(AppLanguage.English);
        harness.Queries.Reads.Should().Be(1);
        harness.Service.Current.Active.Should().Equal(AppLanguage.French, AppLanguage.English);

        harness.Queries.Value = "French,Arabic,English";
        harness.Clock.Now += ActiveLanguagesCache.MaxAge;

        (await harness.Service.CoerceAsync(AppLanguage.Arabic)).Should().Be(AppLanguage.Arabic);
        harness.Queries.Reads.Should().Be(2);
    }

    [Fact]
    public async Task Saving_NeedsTheRightToManageSettings_KeepsFrench_AndIsSeenAtOnce()
    {
        var refused = new Harness(null, allowed: false);
        (await refused.Service.SaveAsync([AppLanguage.English])).IsFailure.Should().BeTrue();
        refused.Writer.Added.Should().BeEmpty();

        var harness = new Harness(null);
        (await harness.Service.GetAsync()).Should().Be(LanguageSettings.Default);

        (await harness.Service.SaveAsync([AppLanguage.Arabic])).IsSuccess.Should().BeTrue();

        harness.Writer.Added.Should().ContainSingle(s => s.Key == LanguageSettings.ActiveLanguagesKey
                                                         && s.Value == "French,Arabic" && s.SiteId == null);
        harness.UnitOfWork.Saves.Should().Be(1);
        harness.UnitOfWork.AuditReason.Should().Contain("Arabic");

        // Within the minute the stored reading would still say all three: the save replaced it.
        harness.Service.Current.Active.Should().Equal(AppLanguage.French, AppLanguage.Arabic);
        (await harness.Service.CoerceAsync(AppLanguage.English)).Should().Be(AppLanguage.French);
    }

    [Fact]
    public void ThePhone_IsRefusedALanguageSwitchedOff_AndAnUnknownOne()
    {
        var frenchAndEnglish = LanguageSettings.Of([AppLanguage.English]);

        SessionEndpoints.ParseLanguage("Arabic", frenchAndEnglish).FirstError.ResourceKey
            .Should().Be("Mobile_LanguageNotActive");
        SessionEndpoints.ParseLanguage("Klingon", frenchAndEnglish).FirstError.ResourceKey
            .Should().Be("Mobile_UnknownLanguage");
        SessionEndpoints.ParseLanguage("english", frenchAndEnglish).Value.Should().Be(AppLanguage.English);
        SessionEndpoints.ParseLanguage("Arabic", LanguageSettings.Default).Value.Should().Be(AppLanguage.Arabic);
    }

    // ---- Fakes ------------------------------------------------------------------------------------

    private sealed class Harness
    {
        public Harness(string? stored, bool allowed = true)
        {
            Queries.Value = stored;
            Service = new ActiveLanguagesService(Queries, new ActiveLanguagesCache(), Writer, UnitOfWork,
                                                 new Guard(allowed), Clock);
        }

        public FakeQueries Queries { get; } = new();
        public FakeWriter Writer { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; } = new();
        public FakeClock Clock { get; } = new();
        public ActiveLanguagesService Service { get; }
    }

    private sealed class FakeQueries : ILanguageSettingsQueries
    {
        public string? Value { get; set; }
        public int Reads { get; private set; }

        public Task<IReadOnlyDictionary<string, string?>> GetSettingValuesAsync(string prefix, CancellationToken ct = default)
        {
            Reads++;
            IReadOnlyDictionary<string, string?> values = Value is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { [LanguageSettings.ActiveLanguagesKey] = Value };
            return Task.FromResult(values);
        }
    }

    private sealed class FakeWriter : IConfigurationWriter
    {
        public List<AppSetting> Added { get; } = [];

        public Task<NumberSequence?> GetSequenceAsync(Guid sequenceId, CancellationToken ct = default) =>
            Task.FromResult<NumberSequence?>(null);

        public Task<AppSetting?> GetSettingAsync(string key, Guid? siteId, CancellationToken ct = default) =>
            Task.FromResult(Added.FirstOrDefault(s => s.Key == key && s.SiteId == siteId));

        public Task AddSettingAsync(AppSetting setting, CancellationToken ct = default)
        {
            Added.Add(setting);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int Saves { get; private set; }
        public string? AuditReason { get; set; }

        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            Saves++;
            return Task.FromResult(1);
        }

        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Guard(bool allowed) : IPermissionGuard
    {
        private Result Answer(string permission) =>
            allowed ? Result.Success() : Result.Failure(Error.Forbidden("Message.PermissionDenied", permission));

        public Result Require(string permission) => Answer(permission);

        public Result RequireAny(params string[] permissions) => Answer(permissions[0]);

        public Result RequireAll(params string[] permissions) => Answer(permissions[0]);

        public Task<Result> RequireBiologicalValidationAsync(string departmentCode, CancellationToken ct = default) =>
            Task.FromResult(Answer(Permissions.Results.BiologicalValidate));
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 18, 9, 0, 0, TimeSpan.FromHours(1));
        public DateOnly Today => DateOnly.FromDateTime(Now.Date);
    }
}
