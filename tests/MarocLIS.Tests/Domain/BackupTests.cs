using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Licensing;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Sequencing;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Localization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Paramètres › Sauvegardes without SQL Server: the schedule the server's task follows, the names
/// and the retention it shares with the script, the text of the SQL commands, and the guards of the
/// service in front of a restore. The commands themselves run in <c>BackupDatabaseTests</c>.
/// </summary>
public sealed class BackupTests
{
    // ---- Settings -----------------------------------------------------------------------------------

    [Fact]
    public void Settings_absent_keep_their_defaults()
    {
        var settings = BackupSettings.FromValues(new Dictionary<string, string?>());

        settings.Should().Be(BackupSettings.Default);
        settings.Enabled.Should().BeTrue("an installation that never opened the tab is still backed up");
        settings.Folder.Should().Be(@"C:\MarocLIS\Sauvegardes");
        settings.RetentionDays.Should().Be(14);
        settings.Times.Should().Equal(new TimeOnly(22, 0));
        settings.Days.Should().BeEmpty("empty means every day");
    }

    [Fact]
    public void Settings_round_trip_through_their_stored_form()
    {
        var settings = new BackupSettings
        {
            Enabled = false,
            Folder = @"\\nas\maroclis",
            RetentionDays = 30,
            Times = [new TimeOnly(13, 0), new TimeOnly(22, 30)],
            Days = [DayOfWeek.Monday, DayOfWeek.Friday]
        };

        var values = settings.ToValues().ToDictionary(v => v.Key, v => (string?)v.Value);

        values[BackupSettings.TimesKey].Should().Be("13:00,22:30");
        values[BackupSettings.DaysKey].Should().Be("Monday,Friday");
        values[BackupSettings.EnabledKey].Should().Be("false");
        BackupSettings.FromValues(values).Should().Be(settings);
    }

    [Fact]
    public void Settings_damaged_by_hand_keep_what_can_be_read()
    {
        var settings = BackupSettings.FromValues(new Dictionary<string, string?>
        {
            [BackupSettings.EnabledKey] = "peut-être",
            [BackupSettings.RetentionDaysKey] = "0",
            [BackupSettings.TimesKey] = "25:00, 7:5, 08:15 ;  08:15",
            [BackupSettings.DaysKey] = "Lundi,Tuesday,3"
        });

        settings.Enabled.Should().BeTrue();
        settings.RetentionDays.Should().Be(BackupSettings.DefaultRetentionDays);
        settings.Times.Should().Equal(new TimeOnly(8, 15));
        settings.Days.Should().Equal(DayOfWeek.Tuesday);
    }

    [Fact]
    public void Settings_with_no_readable_time_fall_back_to_the_default_time()
    {
        BackupSettings.FromValues(new Dictionary<string, string?> { [BackupSettings.TimesKey] = "midi" })
            .Times.Should().Equal(BackupSettings.DefaultTime);
    }

    [Theory]
    [InlineData(@"D:\Sauvegardes\MarocLIS", true)]
    [InlineData(@"E:\", true)]
    [InlineData(@"\\nas\maroclis\lis", true)]
    [InlineData("/var/opt/mssql/backup", true)]
    [InlineData(@"Sauvegardes", false)]
    [InlineData(@"..\Sauvegardes", false)]
    [InlineData(@"C:Sauvegardes", false)]
    [InlineData(@"\\nas", false)]
    [InlineData(@"\\?\C:\Sauvegardes", false)]
    [InlineData(@"C:\Sauvegardes\fichier.bak:flux", false)]
    [InlineData(@"C:\Sauve'gardes", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void Folder_must_be_absolute_on_the_server(string folder, bool acceptable)
    {
        BackupSettings.IsAcceptableFolder(folder).Should().Be(acceptable);
    }

    [Fact]
    public void Folder_longer_than_the_limit_is_refused()
    {
        BackupSettings.IsAcceptableFolder(@"C:\" + new string('a', BackupSettings.MaxFolderLength)).Should().BeFalse();
    }

    [Fact]
    public void Validate_reports_every_problem_by_its_key()
    {
        var settings = new BackupSettings
        {
            Folder = "relatif",
            RetentionDays = 400,
            Times = [.. Enumerable.Range(0, BackupSettings.MaxTimesPerDay + 1).Select(h => new TimeOnly(h, 0))]
        };

        var result = settings.Validate();

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.ResourceKey).Should()
            .BeEquivalentTo("Backup_FolderInvalid", "Backup_RetentionInvalid", "Backup_TooManyTimes");
        (BackupSettings.Default with { Times = [] }).Validate().FirstError.ResourceKey.Should().Be("Backup_TimesRequired");
        BackupSettings.Default.Validate().IsSuccess.Should().BeTrue();
    }

    // ---- Schedule -----------------------------------------------------------------------------------

    private static readonly DateTime Sunday = new(2026, 10, 18);

    [Fact]
    public void Slot_is_due_once_its_time_has_passed_and_only_once()
    {
        var settings = BackupSettings.Default;

        BackupSchedule.DueSlot(settings, null, Sunday.AddHours(21).AddMinutes(50)).Should()
            .Be(Sunday.AddDays(-1).AddHours(22), "yesterday's 22:00 is within the catch-up window");
        BackupSchedule.DueSlot(settings, Sunday.AddDays(-1).AddHours(22), Sunday.AddHours(21).AddMinutes(50)).Should().BeNull();

        var due = BackupSchedule.DueSlot(settings, Sunday.AddDays(-1).AddHours(22), Sunday.AddHours(22).AddMinutes(5));
        due.Should().Be(Sunday.AddHours(22));
        BackupSchedule.DueSlot(settings, due, Sunday.AddHours(22).AddMinutes(20)).Should().BeNull("the next pass must not back up again");
    }

    [Fact]
    public void Server_off_at_the_slot_catches_up_once_in_the_morning()
    {
        var settings = BackupSettings.Default with { Times = [new TimeOnly(13, 0), new TimeOnly(22, 0)] };
        var lastHandled = Sunday.AddDays(-1).AddHours(13);

        // Off from 18:00 to 08:00: the 22:00 slot is caught up at 08:00, once.
        BackupSchedule.DueSlot(settings, lastHandled, Sunday.AddHours(8)).Should().Be(Sunday.AddDays(-1).AddHours(22));
    }

    [Fact]
    public void Slot_older_than_the_window_is_skipped_rather_than_run_days_late()
    {
        var settings = BackupSettings.Default with { Days = [DayOfWeek.Friday] };

        // Friday 22:00 was missed; on Sunday evening it is more than a day old.
        BackupSchedule.DueSlot(settings, null, Sunday.AddHours(23)).Should().BeNull();
    }

    [Fact]
    public void Nothing_is_due_or_next_while_switched_off()
    {
        var off = BackupSettings.Default with { Enabled = false };

        BackupSchedule.DueSlot(off, null, Sunday.AddHours(23)).Should().BeNull();
        BackupSchedule.NextRun(off, Sunday).Should().BeNull();
    }

    [Fact]
    public void Next_run_follows_the_chosen_days()
    {
        var settings = BackupSettings.Default with
        {
            Days = [DayOfWeek.Monday, DayOfWeek.Thursday],
            Times = [new TimeOnly(12, 30), new TimeOnly(20, 0)]
        };

        BackupSchedule.NextRun(settings, Sunday.AddHours(23)).Should().Be(Sunday.AddDays(1).AddHours(12).AddMinutes(30));
        BackupSchedule.NextRun(settings, Sunday.AddDays(1).AddHours(13)).Should().Be(Sunday.AddDays(1).AddHours(20));
        BackupSchedule.NextRun(settings, Sunday.AddDays(1).AddHours(21)).Should().Be(Sunday.AddDays(4).AddHours(12).AddMinutes(30));
    }

    // ---- Names, retention, verification record ----------------------------------------------------

    [Fact]
    public void File_names_say_when_and_why()
    {
        var at = new DateTime(2026, 10, 18, 22, 0, 5);

        BackupNaming.FileName("MarocLIS", BackupKind.Scheduled, at).Should().Be("MarocLIS_20261018_220005.bak");
        BackupNaming.FileName("MarocLIS", BackupKind.Manual, at).Should().Be("MarocLIS_20261018_220005_manuelle.bak");
        BackupNaming.FileName("MarocLIS", BackupKind.BeforeRestore, at).Should().Be("MarocLIS_20261018_220005_avant-restauration.bak");
        BackupNaming.FileName("Labo Atlas", BackupKind.Scheduled, at).Should().Be("Labo_Atlas_20261018_220005.bak");
    }

    [Theory]
    [InlineData(BackupKind.Scheduled)]
    [InlineData(BackupKind.Manual)]
    [InlineData(BackupKind.BeforeRestore)]
    public void Set_name_carries_the_kind(BackupKind kind)
    {
        BackupNaming.KindOf(BackupNaming.SetName(kind)).Should().Be(kind);
    }

    [Fact]
    public void Set_name_of_another_tool_reads_as_other()
    {
        BackupNaming.KindOf("MarocLIS-Full Database Backup").Should().Be(BackupKind.Other);
        BackupNaming.KindOf(null).Should().Be(BackupKind.Other);
    }

    [Fact]
    public void Paths_keep_the_separator_of_the_server()
    {
        BackupNaming.Combine(@"D:\Sauvegardes\", "a.bak").Should().Be(@"D:\Sauvegardes\a.bak");
        BackupNaming.Combine(@"E:\", "a.bak").Should().Be(@"E:\a.bak");
        BackupNaming.Combine("/var/opt/mssql/backup/", "a.bak").Should().Be("/var/opt/mssql/backup/a.bak");
        BackupNaming.FileOf(@"\\nas\lis\a.bak").Should().Be("a.bak");
        BackupNaming.FolderOf("/var/backup/a.bak").Should().Be("/var/backup");
    }

    [Fact]
    public void Only_our_own_files_are_ours_to_delete()
    {
        BackupNaming.IsOwnFile("MarocLIS", "MarocLIS_20261018_220000.bak").Should().BeTrue();
        BackupNaming.IsOwnFile("MarocLIS", "marocLIS_20261018_220000_manuelle.BAK").Should().BeTrue();
        BackupNaming.IsOwnFile("MarocLIS", "MarocLIS_avant_migration.bak").Should().BeFalse();
        BackupNaming.IsOwnFile("MarocLIS", "MarocLIS_20261018_220000.bak.zip").Should().BeFalse();
        BackupNaming.IsOwnFile("MarocLIS", "Compta_20261018_220000.bak").Should().BeFalse();
        BackupNaming.TakenAt("MarocLIS", "MarocLIS_20261018_220000.bak").Should().Be(new DateTime(2026, 10, 18, 22, 0, 0));
    }

    private sealed record BakFile(string Name, DateTime At, bool Verified);

    [Fact]
    public void Retention_deletes_the_old_files_but_never_the_last_good_one()
    {
        var now = new DateTime(2026, 10, 18, 22, 0, 0);
        var files = new[]
        {
            new BakFile("a", now.AddDays(-30), true),
            new BakFile("b", now.AddDays(-20), true),
            new BakFile("c", now.AddDays(-16), false),
            new BakFile("d", now.AddDays(-2), true),
        };

        BackupRetention.Expired(files, f => f.At, f => f.Verified, now, 14).Select(f => f.Name).Should().Equal("a", "b", "c");

        // A schedule failing for a month: every recent file failed, so the last good one stays.
        var failing = new[] { files[0], files[1], new BakFile("e", now.AddDays(-1), false) };
        BackupRetention.Expired(failing, f => f.At, f => f.Verified, now, 14).Select(f => f.Name).Should().Equal("a");
    }

    [Fact]
    public void Verification_record_round_trips()
    {
        var at = new DateTimeOffset(2026, 10, 18, 22, 1, 5, TimeSpan.FromHours(1));

        var failed = new BackupCheck(BackupCheckStatus.Failed, at, "Backup_FileUnreadable; détail");
        failed.Format().Should().Be("failed;2026-10-18T22:01:05+01:00;Backup_FileUnreadable, détail");
        BackupCheck.Parse(failed.Format()).Should().Be(failed with { Message = "Backup_FileUnreadable, détail" });

        BackupCheck.Parse("ok;2026-10-18T22:01:05+01:00").Should().Be(new BackupCheck(BackupCheckStatus.Verified, at, null));
        BackupCheck.Parse("pending;").Status.Should().Be(BackupCheckStatus.Pending);
        BackupCheck.Parse(null).Should().Be(BackupCheck.Unknown);
        BackupCheck.Parse("n'importe quoi").Status.Should().Be(BackupCheckStatus.Unknown);
    }

    [Fact]
    public void State_reads_what_the_task_wrote()
    {
        var state = BackupState.FromValues(new Dictionary<string, string?>
        {
            [BackupState.AgentSeenAtKey] = "2026-10-18T22:15:00+01:00",
            [BackupState.AgentHostKey] = "SRV-LABO",
            [BackupState.LastSlotKey] = "2026-10-18T22:00",
            [BackupState.LastResultKey] = "ok;2026-10-18T22:02:00+01:00;MarocLIS_20261018_220000.bak"
        });

        state.AgentHost.Should().Be("SRV-LABO");
        state.LastSlot.Should().Be(new DateTime(2026, 10, 18, 22, 0, 0));
        state.AgentSeenAt.Should().Be(new DateTimeOffset(2026, 10, 18, 22, 15, 0, TimeSpan.FromHours(1)));
        state.LastResult!.Message.Should().Be("MarocLIS_20261018_220000.bak");
    }

    // ---- SQL text -----------------------------------------------------------------------------------

    [Fact]
    public void Backup_command_checks_every_page_and_compresses_only_when_asked()
    {
        SqlBackupCommands.Backup("MarocLIS", compress: false).Should()
            .Be("BACKUP DATABASE [MarocLIS] TO DISK = @path WITH CHECKSUM, FORMAT, INIT, NAME = @name, DESCRIPTION = @description;");
        SqlBackupCommands.Backup("MarocLIS", compress: true).Should().EndWith(", COMPRESSION;");
        SqlBackupCommands.Backup("Mal]veillant", compress: false).Should().StartWith("BACKUP DATABASE [Mal]]veillant] ");
        SqlBackupCommands.Verify(true).Should().Be("RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM;");
        SqlBackupCommands.Verify(false).Should().Be("RESTORE VERIFYONLY FROM DISK = @path;");
    }

    [Fact]
    public void Restore_command_disconnects_replaces_and_reopens_in_that_order()
    {
        var sql = SqlBackupCommands.Restore("MarocLIS", moves: 2, withChecksum: true);

        sql.Should().Be(
            "ALTER DATABASE [MarocLIS] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
            + "ALTER DATABASE [MarocLIS] SET OFFLINE WITH ROLLBACK IMMEDIATE; "
            + "RESTORE DATABASE [MarocLIS] FROM DISK = @path WITH REPLACE, RECOVERY, CHECKSUM, "
            + "MOVE @logical0 TO @physical0, MOVE @logical1 TO @physical1; "
            + "ALTER DATABASE [MarocLIS] SET MULTI_USER;");
        SqlBackupCommands.Restore("MarocLIS", 0, false).Should().Contain("WITH REPLACE, RECOVERY; ");
        SqlBackupCommands.Reopen("O'Neil").Should().Contain("N'O''Neil'").And.EndWith("SET MULTI_USER;");
    }

    [Fact]
    public void Files_of_a_backup_land_on_the_files_of_this_database()
    {
        var current = new SqlBackupCommands.DatabaseFile[]
        {
            new("MarocLIS", @"D:\Data\MarocLIS.mdf", false),
            new("MarocLIS_log", @"L:\Logs\MarocLIS_log.ldf", true)
        };

        // Same names: name for name.
        SqlBackupCommands.MapFiles(
            [new("MarocLIS", @"C:\Ancien\MarocLIS.mdf", false), new("MarocLIS_log", @"C:\Ancien\MarocLIS_log.ldf", true)], current)
            .Should().Equal((@"MarocLIS", @"D:\Data\MarocLIS.mdf"), ("MarocLIS_log", @"L:\Logs\MarocLIS_log.ldf"));

        // Another server's names: kind for kind; an extra data file beside the data.
        SqlBackupCommands.MapFiles(
            [new("LIS_Data", @"C:\X\a.mdf", false), new("LIS_Extra", @"C:\X\b.ndf", false), new("LIS_Log", @"C:\X\c.ldf", true)], current)
            .Should().Equal(("LIS_Data", @"D:\Data\MarocLIS.mdf"), ("LIS_Extra", @"D:\Data\b.ndf"), ("LIS_Log", @"L:\Logs\MarocLIS_log.ldf"));
    }

    [Theory]
    [InlineData(new[] { 3201, 3013 }, "Backup_FolderNotWritable")]
    [InlineData(new[] { 15240 }, "Backup_FolderNotWritable")]
    [InlineData(new[] { 262 }, "Backup_PermissionDenied")]
    [InlineData(new[] { 3013, 3110 }, "Backup_PermissionDenied")]
    [InlineData(new[] { 3101, 3013 }, "Backup_RestoreExclusiveAccess")]
    [InlineData(new[] { 3241 }, "Backup_FileUnreadable")]
    [InlineData(new[] { 3013 }, "Backup_SqlError")]
    [InlineData(new[] { 1105 }, "Backup_SqlError")]
    public void Sql_errors_become_a_message_that_says_what_to_do(int[] numbers, string key)
    {
        SqlBackupCommands.ErrorKey(numbers).Should().Be(key);
    }

    // ---- Permission and texts -----------------------------------------------------------------------

    [Fact]
    public void Backup_permission_exists_and_the_administrator_holds_it_alone()
    {
        Permissions.All.Should().Contain("admin.backup");
        Permissions.Administration.Backup.Should().Be("admin.backup");

        var type = typeof(LisDbContext).Assembly.GetType("MarocLIS.Infrastructure.Persistence.RolePermissionSets")!;
        var roles = (System.Collections.IEnumerable)type
            .GetMethod("Build", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;

        var holders = new List<string>();
        foreach (var role in roles)
        {
            var tuple = role.GetType();
            if (((string[])tuple.GetField("Item3")!.GetValue(role)!).Contains("admin.backup"))
                holders.Add((string)tuple.GetField("Item1")!.GetValue(role)!);
        }

        holders.Should().Equal(SystemRoles.Administrator);
    }

    [Theory]
    [InlineData(AppLanguage.French)]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Arabic)]
    public void Every_state_and_step_shown_has_a_label(AppLanguage language)
    {
        var localizer = new JsonLocalizer(new LocalizationOptions { MarkMissingKeys = true });

        var keys = Enum.GetValues<BackupKind>().Select(v => $"BackupKind.{v}")
            .Concat(Enum.GetValues<BackupCheckStatus>().Select(v => $"BackupCheckStatus.{v}"))
            .Concat(Enum.GetValues<RestoreStep>().Select(v => $"RestoreStep.{v}"))
            .Concat(new[] { SecurityEventKind.BackupCompleted, SecurityEventKind.BackupFailed, SecurityEventKind.RestoreStarted,
                            SecurityEventKind.RestoreCompleted, SecurityEventKind.RestoreFailed,
                            SecurityEventKind.BackupSettingsChanged }.Select(v => $"SecurityEventKind.{v}"));

        foreach (var key in keys)
            localizer.Has(key, language).Should().BeTrue($"{key} is shown in {language}");
    }

    [Fact]
    public void Folder_message_names_the_folder_and_the_server_answer()
    {
        var localizer = new JsonLocalizer(new LocalizationOptions { MarkMissingKeys = true });

        var text = localizer.Describe(Error.Conflict("Backup_FolderNotWritable", @"E:\Sauvegardes", "Operating system error 5(Access is denied.)."),
                                      AppLanguage.French);

        text.Should().Contain(@"E:\Sauvegardes").And.Contain("Access is denied").And.Contain("compte de service");
    }

    [Fact]
    public void Security_event_numbers_stay_clear_of_the_licensing_ones()
    {
        ((int)SecurityEventKind.BackupCompleted).Should().Be(30);
        ((int)SecurityEventKind.BackupSettingsChanged).Should().Be(35);
    }

    // ---- Confirmation -------------------------------------------------------------------------------

    [Theory]
    [InlineData("Laboratoire Atlas d'Analyses Médicales", "laboratoire atlas d'analyses medicales", true)]
    [InlineData("Laboratoire Atlas", "  Laboratoire   Atlas ", true)]
    [InlineData("Laboratoire Atlas", "Laboratoire", false)]
    [InlineData("Laboratoire Atlas", "", false)]
    [InlineData("Laboratoire Atlas", null, false)]
    [InlineData("", "", false)]
    public void Restore_is_confirmed_by_the_laboratory_name(string expected, string? typed, bool matches)
    {
        BackupService.ConfirmationMatches(expected, typed).Should().Be(matches);
    }

    [Fact]
    public void Last_backup_older_than_48_hours_is_stale()
    {
        var now = new DateTime(2026, 10, 18, 9, 0, 0);
        BackupService.IsStale(now.AddHours(-47), now).Should().BeFalse();
        BackupService.IsStale(now.AddHours(-49), now).Should().BeTrue();
        BackupService.IsStale(null, now).Should().BeTrue();
    }

    // ---- The service, against a fake engine ---------------------------------------------------------

    private static readonly RestoreContext OnServer = new(IsServerMachine: true, IsLocalAdministrator: true);

    [Fact]
    public async Task Without_the_permission_nothing_is_read_or_written()
    {
        var (service, engine, events) = Service(granted: false);

        (await service.GetOverviewAsync()).FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
        (await service.BackupNowAsync()).IsFailure.Should().BeTrue();
        (await service.SaveSettingsAsync(BackupSettings.Default)).IsFailure.Should().BeTrue();
        (await service.TestFolderAsync(@"D:\B")).IsFailure.Should().BeTrue();
        (await service.CanRestoreAsync(OnServer)).IsFailure.Should().BeTrue();
        (await service.RestoreAsync(new RestoreRequest(1, "Laboratoire Atlas", OnServer))).IsFailure.Should().BeTrue();
        (await service.GetHealthAsync()).Should().BeNull("the startup warning is for those who can act on it");

        engine.Calls.Should().BeEmpty();
        events.Should().BeEmpty();
    }

    [Fact]
    public async Task Restore_is_refused_away_from_the_server_with_the_reason()
    {
        var (service, engine, _) = Service();

        (await service.CanRestoreAsync(new RestoreContext(false, true))).FirstError.ResourceKey.Should().Be("Backup_RestoreServerOnly");
        (await service.CanRestoreAsync(new RestoreContext(true, false))).FirstError.ResourceKey.Should().Be("Backup_RestoreServerOnly");

        engine.Server = engine.Server with { CanRestore = false, Login = @"LABO\secretaire" };
        var refused = await service.CanRestoreAsync(OnServer);
        refused.FirstError.ResourceKey.Should().Be("Backup_RestoreNoSqlRights");
        refused.FirstError.Args.Should().Equal(@"LABO\secretaire");

        (await service.RestoreAsync(new RestoreRequest(1, "Laboratoire Atlas", OnServer))).FirstError.ResourceKey
            .Should().Be("Backup_RestoreNoSqlRights");
        engine.Calls.Should().NotContain(c => c.StartsWith("Backup", StringComparison.Ordinal) || c.StartsWith("Restore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restore_with_the_wrong_name_touches_nothing()
    {
        var (service, engine, events) = Service();

        var result = await service.RestoreAsync(new RestoreRequest(1, "Labo", OnServer));

        result.FirstError.ResourceKey.Should().Be("Backup_RestoreConfirmationMismatch");
        engine.Calls.Should().NotContain(c => c.StartsWith("Backup", StringComparison.Ordinal) || c.StartsWith("Restore", StringComparison.Ordinal));
        events.Should().BeEmpty();
    }

    [Fact]
    public async Task Damaged_or_failed_backup_is_not_restored()
    {
        var (service, engine, _) = Service();
        engine.History.Add(engine.Set(2, "MarocLIS_20261017_220000.bak") with { IsDamaged = true });
        engine.History.Add(engine.Set(3, "MarocLIS_20261016_220000.bak"));
        engine.Values[BackupCheck.KeyFor("MarocLIS_20261016_220000.bak")] = "failed;2026-10-16T22:05:00+01:00;Backup_FileUnreadable";

        (await service.RestoreAsync(new RestoreRequest(2, "Laboratoire Atlas", OnServer))).FirstError.ResourceKey
            .Should().Be("Backup_RestoreBackupUnusable");
        (await service.RestoreAsync(new RestoreRequest(3, "Laboratoire Atlas", OnServer))).FirstError.ResourceKey
            .Should().Be("Backup_RestoreBackupUnusable");
        (await service.RestoreAsync(new RestoreRequest(99, "Laboratoire Atlas", OnServer))).FirstError.ResourceKey
            .Should().Be("Backup_RestoreBackupNotFound");

        engine.Calls.Should().NotContain(c => c.StartsWith("Restore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreadable_file_found_before_the_restore_costs_nothing()
    {
        var (service, engine, _) = Service();
        engine.History.Add(engine.Set(1, "MarocLIS_20261017_220000.bak"));
        engine.VerifyResult = Result.Failure(Error.Conflict("Backup_FileUnreadable", "x", "y"));

        (await service.RestoreAsync(new RestoreRequest(1, "laboratoire atlas", OnServer))).FirstError.ResourceKey
            .Should().Be("Backup_RestoreBackupUnusable");
        engine.Calls.Should().NotContain(c => c.StartsWith("Backup:", StringComparison.Ordinal) || c.StartsWith("Restore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restore_takes_a_safety_backup_first_then_upgrades_and_logs_both_ends()
    {
        var (service, engine, events) = Service();
        engine.History.Add(engine.Set(1, "MarocLIS_20261017_220000.bak"));
        var steps = new List<RestoreStep>();

        var result = await service.RestoreAsync(new RestoreRequest(1, "Laboratoire  ATLAS", OnServer), new SyncProgress(steps));

        result.IsSuccess.Should().BeTrue();
        result.Value.RestoredPath.Should().Be(@"D:\Sauvegardes\MarocLIS_20261017_220000.bak");
        result.Value.SafetyBackupPath.Should().Be(@"D:\Sauvegardes\MarocLIS_20261018_093000_avant-restauration.bak");

        engine.Calls.Where(c => !c.StartsWith("Read", StringComparison.Ordinal) && !c.StartsWith("Write", StringComparison.Ordinal))
            .Should().Equal(
                @"Verify:D:\Sauvegardes\MarocLIS_20261017_220000.bak",
                @"Backup:BeforeRestore:D:\Sauvegardes\MarocLIS_20261018_093000_avant-restauration.bak",
                @"Verify:D:\Sauvegardes\MarocLIS_20261018_093000_avant-restauration.bak",
                "Restore:1",
                "Upgrade");
        steps.Should().Equal(RestoreStep.Checking, RestoreStep.SafetyBackup, RestoreStep.Restoring, RestoreStep.Upgrading, RestoreStep.Done);
        events.Select(e => e.Kind).Should().Equal(SecurityEventKind.BackupCompleted, SecurityEventKind.RestoreStarted,
                                                  SecurityEventKind.RestoreCompleted);
    }

    [Fact]
    public async Task Failed_safety_backup_stops_the_restore()
    {
        var (service, engine, events) = Service();
        engine.History.Add(engine.Set(1, "MarocLIS_20261017_220000.bak"));
        engine.BackupResult = Result.Failure(Error.Conflict("Backup_FolderNotWritable", @"D:\Sauvegardes", "denied"));

        var result = await service.RestoreAsync(new RestoreRequest(1, "Laboratoire Atlas", OnServer));

        result.FirstError.ResourceKey.Should().Be("Backup_RestoreSafetyBackupFailed");
        engine.Calls.Should().NotContain(c => c.StartsWith("Restore", StringComparison.Ordinal));
        events.Select(e => e.Kind).Should().Equal(SecurityEventKind.BackupFailed);
    }

    [Fact]
    public async Task Failed_restore_names_the_safety_backup_and_is_logged()
    {
        var (service, engine, events) = Service();
        engine.History.Add(engine.Set(1, "MarocLIS_20261017_220000.bak"));
        engine.RestoreResult = Result.Failure(Error.Conflict("Backup_RestoreExclusiveAccess", "p", "in use"));

        var result = await service.RestoreAsync(new RestoreRequest(1, "Laboratoire Atlas", OnServer));

        result.FirstError.ResourceKey.Should().Be("Backup_RestoreFailed");
        result.FirstError.Args[1].Should().Be(@"D:\Sauvegardes\MarocLIS_20261018_093000_avant-restauration.bak");
        engine.Calls.Should().NotContain("Upgrade");
        events.Select(e => e.Kind).Should().Equal(SecurityEventKind.BackupCompleted, SecurityEventKind.RestoreStarted,
                                                  SecurityEventKind.RestoreFailed);
        events[^1].FailureKey.Should().Be("Backup_RestoreExclusiveAccess");
    }

    [Fact]
    public async Task Backup_from_a_workstation_that_cannot_verify_is_left_to_the_server()
    {
        var (service, engine, events) = Service();
        engine.Server = engine.Server with { CanVerify = false };

        var result = await service.BackupNowAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Verification.Should().Be(BackupCheckStatus.Pending);
        result.Value.Path.Should().Be(@"D:\Sauvegardes\MarocLIS_20261018_093000_manuelle.bak");
        engine.Calls.Should().NotContain(c => c.StartsWith("Verify", StringComparison.Ordinal));
        BackupCheck.Parse(engine.Values[BackupCheck.KeyFor("MarocLIS_20261018_093000_manuelle.bak")]).Status
            .Should().Be(BackupCheckStatus.Pending);
        engine.Values.Should().ContainKey(BackupState.LastSuccessAtKey);
        events.Single().Kind.Should().Be(SecurityEventKind.BackupCompleted);
    }

    [Fact]
    public async Task Backup_that_fails_its_verification_is_reported_as_a_failure()
    {
        var (service, engine, events) = Service();
        engine.VerifyResult = Result.Failure(Error.Conflict("Backup_FileUnreadable", "x", "y"));

        var result = await service.BackupNowAsync();

        result.FirstError.ResourceKey.Should().Be("Backup_VerifyFailed");
        engine.Values.Should().NotContainKey(BackupState.LastSuccessAtKey);
        BackupCheck.Parse(engine.Values[BackupState.LastResultKey]).Status.Should().Be(BackupCheckStatus.Failed);
        events.Single().Kind.Should().Be(SecurityEventKind.BackupFailed);
    }

    [Fact]
    public async Task Compression_is_asked_only_of_an_edition_that_has_it()
    {
        var (service, engine, _) = Service();

        await service.BackupNowAsync();
        engine.Server = engine.Server with { SupportsCompression = true };
        await service.BackupNowAsync();

        engine.Compressed.Should().Equal(false, true);
    }

    [Fact]
    public async Task New_folder_is_tried_by_SQL_Server_before_it_is_saved()
    {
        var (service, engine, events) = Service();
        engine.ProbeResult = Result.Failure(Error.Conflict("Backup_FolderNotWritable", @"E:\B", "denied"));
        var settings = BackupSettings.Default with { Folder = @"E:\B" };

        (await service.SaveSettingsAsync(settings)).FirstError.ResourceKey.Should().Be("Backup_FolderNotWritable");
        engine.Writer.Saved.Should().Be(0);

        // Saved anyway when asked: the server's task creates a missing local folder.
        (await service.SaveSettingsAsync(settings, checkFolder: false)).IsSuccess.Should().BeTrue();
        engine.Writer.Settings[BackupSettings.FolderKey].Value.Should().Be(@"E:\B");
        engine.Writer.Saved.Should().Be(1);
        events.Single().Kind.Should().Be(SecurityEventKind.BackupSettingsChanged);

        // Unchanged: nothing saved and nothing logged again.
        (await service.SaveSettingsAsync(settings, checkFolder: false)).IsSuccess.Should().BeTrue();
        engine.Writer.Saved.Should().Be(1);
    }

    [Fact]
    public async Task Invalid_settings_are_refused_before_any_probe()
    {
        var (service, engine, _) = Service();

        (await service.SaveSettingsAsync(BackupSettings.Default with { Folder = "relatif" })).FirstError.ResourceKey
            .Should().Be("Backup_FolderInvalid");
        engine.Calls.Should().NotContain(c => c.StartsWith("Probe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Overview_hides_deleted_files_and_marks_a_silent_task()
    {
        var (service, engine, _) = Service();
        engine.History.Add(engine.Set(1, "MarocLIS_20261017_220000.bak"));
        engine.History.Add(engine.Set(2, "MarocLIS_20261001_220000.bak"));
        engine.Values[BackupCheck.KeyFor("MarocLIS_20261001_220000.bak")] = "deleted;2026-10-16T22:05:00+01:00";
        engine.Values[BackupCheck.KeyFor("MarocLIS_20261017_220000.bak")] = "ok;2026-10-17T22:05:00+01:00";
        engine.Values[BackupState.AgentSeenAtKey] = "2026-10-18T07:00:00+00:00";

        var overview = (await service.GetOverviewAsync()).Value;

        overview.Backups.Should().ContainSingle().Which.Check.Status.Should().Be(BackupCheckStatus.Verified);
        overview.Agent.Should().Be(BackupAgentStatus.Silent);
        overview.NextRunAt.Should().Be(new DateTime(2026, 10, 18, 22, 0, 0));
        overview.IsStale.Should().BeFalse();
    }

    // ---- Licence in read-only mode ------------------------------------------------------------------

    [Fact]
    public async Task Read_only_licence_leaves_backup_restore_and_schedule_open()
    {
        // The licence's read-only mode refuses changes to the settings (except the licence), but a
        // laboratory in read-only mode still holds its patients' records and must be able to protect
        // them: the backups stay open. The service is composed the way the container builds it, with
        // the read-only licence registered, so a licence gate added to it later would be handed that
        // state and fail here.
        var readOnly = new LicenceStatus { State = LicenceState.Expired, Mode = LicenceMode.ReadOnly };
        readOnly.RequireSettingsChange().FirstError.ResourceKey.Should().Be("Licence_ReadOnlySettings",
            "this is the gate the backups are kept out of");

        var holder = new LicenceStatusHolder();
        holder.Publish(readOnly);
        var engine = new FakeEngine();
        var events = new List<Logged>();
        var user = new Admin(true);
        using var provider = new ServiceCollection()
            .AddSingleton(holder)
            .AddSingleton<ILicenceState>(holder)
            .AddSingleton<IDatabaseBackupEngine>(engine)
            .AddSingleton<IConfigurationWriter>(engine.Writer)
            .AddSingleton<IUnitOfWork>(engine.Writer)
            .AddSingleton<IPermissionGuard>(new Guard(user))
            .AddSingleton<ISecurityAuditor>(new Auditor(events))
            .AddSingleton<ICurrentUser>(user)
            .AddSingleton<IClock>(new Clock())
            .BuildServiceProvider();
        var service = ActivatorUtilities.CreateInstance<BackupService>(provider);
        engine.History.Add(engine.Set(1, "MarocLIS_20261017_220000.bak"));

        (await service.GetOverviewAsync()).IsSuccess.Should().BeTrue();
        (await service.SaveSettingsAsync(BackupSettings.Default with { Folder = @"E:\B", RetentionDays = 30 }))
            .IsSuccess.Should().BeTrue("the backup schedule is not a setting the licence closes");
        engine.Writer.Settings[BackupSettings.FolderKey].Value.Should().Be(@"E:\B");
        (await service.TestFolderAsync(@"E:\B")).IsSuccess.Should().BeTrue();
        (await service.BackupNowAsync()).IsSuccess.Should().BeTrue("« Sauvegarder maintenant » stays open");
        (await service.CanRestoreAsync(OnServer)).IsSuccess.Should().BeTrue();
        (await service.RestoreAsync(new RestoreRequest(1, "Laboratoire Atlas", OnServer))).IsSuccess
            .Should().BeTrue("the restore stays open");
        (await service.GetHealthAsync()).Should().NotBeNull();

        events.Select(x => x.Kind).Should().Contain([SecurityEventKind.BackupSettingsChanged, SecurityEventKind.BackupCompleted,
                                                     SecurityEventKind.RestoreCompleted]);
        events.Should().OnlyContain(x => x.Succeeded);
    }

    // ---- Fakes --------------------------------------------------------------------------------------

    private sealed record Logged(SecurityEventKind Kind, bool Succeeded, string? FailureKey);

    private static (BackupService Service, FakeEngine Engine, List<Logged> Events) Service(bool granted = true)
    {
        var engine = new FakeEngine();
        var events = new List<Logged>();
        var user = new Admin(granted);
        var service = new BackupService(engine, engine.Writer, engine.Writer, new Guard(user), new Auditor(events), user, new Clock());
        return (service, engine, events);
    }

    private sealed class SyncProgress(List<RestoreStep> steps) : IProgress<RestoreStep>
    {
        public void Report(RestoreStep value) => steps.Add(value);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset Now => new(2026, 10, 18, 9, 30, 0, TimeSpan.FromHours(1));
        public DateOnly Today => new(2026, 10, 18);
    }

    private sealed class FakeEngine : IDatabaseBackupEngine
    {
        public static readonly DateTime ServerNow = new(2026, 10, 18, 9, 30, 0);

        public BackupServerInfo Server { get; set; } =
            new("MarocLIS", "SRV-LABO", "Express Edition", false, @"LABO\admin", CanVerify: true, CanRestore: true, null);

        public List<string> Calls { get; } = [];
        public List<bool> Compressed { get; } = [];
        public List<BackupSetRow> History { get; } = [];
        public Dictionary<string, string?> Values { get; } = new()
        {
            [BackupSettings.FolderKey] = @"D:\Sauvegardes"
        };
        public FakeWriter Writer { get; } = new();

        public Result BackupResult { get; set; } = Result.Success();
        public Result VerifyResult { get; set; } = Result.Success();
        public Result ProbeResult { get; set; } = Result.Success();
        public Result RestoreResult { get; set; } = Result.Success();

        public BackupSetRow Set(long id, string file) =>
            new(id, @"D:\Sauvegardes\" + file, ServerNow.AddHours(-12), ServerNow.AddHours(-11.9), 50_000_000, null,
                HasChecksums: true, IsDamaged: false, BackupNaming.SetName(BackupKind.Scheduled), null, "NT AUTHORITY\\SYSTEM", "SRV-LABO");

        public Task<BackupServerInfo> GetServerInfoAsync(CancellationToken ct = default) => Task.FromResult(Server);

        public Task<IReadOnlyList<BackupSetRow>> GetHistoryAsync(DateTime since, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BackupSetRow>>(History);

        public Task<DateTime?> GetLastBackupAsync(CancellationToken ct = default) =>
            Task.FromResult(History.Where(h => !h.IsDamaged).Select(h => (DateTime?)h.FinishedAt).Max());

        public Task<DateTime> GetServerTimeAsync(CancellationToken ct = default) => Task.FromResult(ServerNow);

        public Task<Result> BackupAsync(string path, BackupKind kind, string description, bool compress, CancellationToken ct = default)
        {
            Calls.Add($"Backup:{kind}:{path}");
            Compressed.Add(compress);
            return Task.FromResult(BackupResult);
        }

        public Task<Result> VerifyAsync(string path, bool withChecksum, CancellationToken ct = default)
        {
            Calls.Add($"Verify:{path}");
            return Task.FromResult(VerifyResult);
        }

        public Task<Result> ProbeFolderAsync(string folder, CancellationToken ct = default)
        {
            Calls.Add($"Probe:{folder}");
            return Task.FromResult(ProbeResult);
        }

        public Task<IReadOnlyList<DatabaseSessionRow>> GetSessionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DatabaseSessionRow>>([]);

        public Task<Result> RestoreAsync(BackupSetRow backup, CancellationToken ct = default)
        {
            Calls.Add($"Restore:{backup.BackupSetId}");
            return Task.FromResult(RestoreResult);
        }

        public Task<Result<bool>> UpgradeSchemaAsync(CancellationToken ct = default)
        {
            Calls.Add("Upgrade");
            return Task.FromResult(Result.Success(false));
        }

        public Task<IReadOnlyDictionary<string, string?>> ReadValuesAsync(string prefix, CancellationToken ct = default)
        {
            Calls.Add($"Read:{prefix}");
            IReadOnlyDictionary<string, string?> values = Values
                .Concat(Writer.Settings.Values.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
                .Where(v => v.Key.StartsWith(prefix, StringComparison.Ordinal))
                .GroupBy(v => v.Key).ToDictionary(g => g.Key, g => g.Last().Value);
            return Task.FromResult(values);
        }

        public Task WriteValueAsync(string key, string? value, string description, CancellationToken ct = default)
        {
            Calls.Add($"Write:{key}");
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task<string> GetLaboratoryNameAsync(CancellationToken ct = default) => Task.FromResult("Laboratoire Atlas");
    }

    private sealed class FakeWriter : IConfigurationWriter, IUnitOfWork
    {
        public Dictionary<string, AppSetting> Settings { get; } = [];
        public int Saved { get; private set; }
        public string? AuditReason { get; set; }

        public Task<NumberSequence?> GetSequenceAsync(Guid sequenceId, CancellationToken ct = default) =>
            Task.FromResult<NumberSequence?>(null);

        public Task<AppSetting?> GetSettingAsync(string key, Guid? siteId, CancellationToken ct = default) =>
            Task.FromResult(Settings.GetValueOrDefault(key));

        public Task AddSettingAsync(AppSetting setting, CancellationToken ct = default)
        {
            Settings[setting.Key] = setting;
            return Task.CompletedTask;
        }

        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            Saved++;
            return Task.FromResult(1);
        }

        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task CommitAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task RollbackAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Guard(ICurrentUser user) : IPermissionGuard
    {
        public Result Require(string permission) =>
            user.Has(permission) ? Result.Success() : Result.Failure(Error.Forbidden("Message.PermissionDenied", permission));

        public Result RequireAny(params string[] permissions) => throw new NotSupportedException();
        public Result RequireAll(params string[] permissions) => throw new NotSupportedException();
        public Task<Result> RequireBiologicalValidationAsync(string departmentCode, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class Auditor(List<Logged> events) : ISecurityAuditor
    {
        public Task LogAsync(SecurityEventKind kind, bool succeeded, string? attemptedUserName = null, string? failureReasonKey = null,
                             Guid? targetUserId = null, string? details = null, CancellationToken ct = default)
        {
            events.Add(new Logged(kind, succeeded, failureReasonKey));
            return Task.CompletedTask;
        }
    }

    private sealed class Admin(bool granted) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "admin";
        public string FullName => "Administrateur";
        public Guid SiteId => Guid.Empty;
        public string SiteCode => "LAB";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => default;
        public IReadOnlyList<Guid> AccessibleSiteIds => [];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => granted;
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "SRV-LABO";
    }
}
