using System.Globalization;
using FluentAssertions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Input;
using MarocLIS.Application.Licensing;
using MarocLIS.Application.Treasury;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Licensing;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Sequencing;
using MarocLIS.Tests.Licensing;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Utilisateurs », « Journal d'audit », « Paramètres » and « Visa des caisses »,
/// moved out of the workstation's views and dialogs, now shared with the web.
/// </summary>
public sealed class AdministrationScreenRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(1));

    internal static UserRow User(Guid? id = null, string userName = "s.bennani", bool active = true, bool locked = false,
                                 int failed = 0, bool mustChange = false, bool expired = false, int? age = 120,
                                 ProfessionalQualification qualification = ProfessionalQualification.LaboratoryTechnician,
                                 string? order = null, bool qualificationExpired = false, DateOnly? until = null) =>
        new(id ?? Guid.NewGuid(), userName, "BENNANI Salma", "سلمى بناني", "s.bennani@alamal.ma", "0661234567",
            qualification, order, null, until, qualificationExpired, active, locked, locked ? Now.AddMinutes(15) : null,
            failed, mustChange, Now.AddDays(-1), "ACCUEIL-01", Now.AddDays(-30), age, expired, AppLanguage.French,
            [], ["TECH"], ["BIOCH", "HEMA"], [], null, false);

    internal static RoleRow Role(bool builtIn = false, bool active = true, int users = 2, params string[] rights) =>
        new(Guid.NewGuid(), "TECH", new MultilingualText("Technicien", "تقني"), null, builtIn, active, users, rights);

    internal static SecurityEventRow Event(SecurityEventKind kind, bool succeeded = true) =>
        new(Guid.NewGuid(), Now, kind, succeeded, "admin", "POSTE-01", "LABO\\admin", null, "détails");

    internal static CashSessionReviewRow Till(Guid operatorId, decimal? variance = 0m, bool reconciled = false) =>
        new(Guid.NewGuid(), "EL IDRISSI Karim", "CAISSE-01", Now.AddHours(-9), Now, 200m, 3450m, 3450m + (variance ?? 0m),
            variance, null, 0m, 1200m, reconciled, reconciled ? "TAZI Nadia" : null, reconciled ? Now : null, null, operatorId);

    // ---- Utilisateurs --------------------------------------------------------------------------

    [Fact]
    public void An_account_reads_locked_and_unqualified_biologist_as_critical_and_a_credential_to_renew_as_a_warning()
    {
        UserAdministrationRules.Tone(User(active: false, locked: true)).Should().Be(AccountTone.Inactive);
        UserAdministrationRules.Tone(User(locked: true)).Should().Be(AccountTone.Critical);
        UserAdministrationRules.Tone(User(qualification: ProfessionalQualification.MedicalBiologist)).Should().Be(AccountTone.Critical,
            "a biologist without an order number cannot sign under loi n° 12-01");
        UserAdministrationRules.Tone(User(mustChange: true)).Should().Be(AccountTone.Warning);
        UserAdministrationRules.Tone(User(qualification: ProfessionalQualification.MedicalBiologist, order: "CNOM-4521"))
                               .Should().Be(AccountTone.Ok);

        UserAdministrationRules.State(User(failed: 2)).Should().Be(("Admin.Active", (int?)2));
        UserAdministrationRules.State(User(locked: true)).Should().Be(("Admin.LockedOut", (int?)null));
        UserAdministrationRules.Flags(User(mustChange: true, expired: true)).Should().Equal("⚿", "!");
    }

    [Fact]
    public void The_notice_names_the_defect_the_first_that_matters_first()
    {
        var lapsed = new DateOnly(2026, 9, 30);
        UserAdministrationRules.Notice(User(qualification: ProfessionalQualification.PharmacistBiologist, order: "CNOP-77",
                                            qualificationExpired: true, until: lapsed))
                               .Should().Be(new UserNotice("Admin.QualificationExpiredNotice", true, lapsed));
        UserAdministrationRules.Notice(User(qualification: ProfessionalQualification.MedicalBiologist))!.Key
                               .Should().Be("Admin.OrderNumberMissingNotice");
        UserAdministrationRules.Notice(User(locked: true))!.Key.Should().Be("Admin.LockedOutNotice");
        UserAdministrationRules.Notice(User(expired: true, age: 200)).Should().Be(new UserNotice("Admin.PasswordExpiredNotice", false, 200));
        UserAdministrationRules.Notice(User(expired: true, age: null))!.Key.Should().Be("Admin.PasswordNeverChangedNotice");
        UserAdministrationRules.Notice(User()).Should().BeNull();
    }

    [Fact]
    public void The_actions_follow_the_two_rights_and_ones_own_account_is_not_deactivated()
    {
        var admin = new LicenceTestKit.User(true, Permissions.Administration.ManageUsers);
        var roles = new LicenceTestKit.User(true, Permissions.Administration.ManageRoles);

        UserAdministrationRules.Actions(admin, null).Should().Be(
            new UserActions(true, false, false, false, false, false, false, false, false));
        UserAdministrationRules.Actions(admin, User(failed: 3)).Should().Be(
            new UserActions(true, true, false, true, true, true, true, false, false));
        UserAdministrationRules.Actions(admin, User(active: false, locked: true)).Should().Be(
            new UserActions(true, true, false, true, true, false, true, true, false), "an inactive account is reactivated, not unlocked");
        UserAdministrationRules.Actions(admin, User(id: admin.UserId)).Should().Be(
            new UserActions(true, true, false, true, true, false, false, false, true));
        UserAdministrationRules.Actions(roles, User()).AssignRoles.Should().BeTrue();
        UserAdministrationRules.Actions(roles, User()).Edit.Should().BeFalse();

        UserAdministrationRules.SecurityLogOffered(admin).Should().BeFalse();
        UserAdministrationRules.SecurityLogOffered(new LicenceTestKit.User(true, Permissions.Administration.ViewAuditTrail)).Should().BeTrue();
    }

    [Fact]
    public void A_privilege_changed_stands_out_among_the_sign_ins_and_a_refusal_more()
    {
        UserAdministrationRules.Emphasis(Event(SecurityEventKind.PermissionsChanged)).Should().Be(SecurityEventEmphasis.Privilege);
        UserAdministrationRules.Emphasis(Event(SecurityEventKind.LoginSucceeded)).Should().Be(SecurityEventEmphasis.None);
        UserAdministrationRules.Emphasis(Event(SecurityEventKind.LoginFailed, succeeded: false)).Should().Be(SecurityEventEmphasis.Failed);
    }

    [Fact]
    public void The_editor_refuses_on_its_field_and_sends_the_departments_once_in_capitals()
    {
        var form = new UserForm { UserName = " ab ", FullName = "TAZI Nadia", Password = "x" };

        UserAdministrationRules.Check(form with { FullName = "  " }, creating: false)!.Field.Should().Be("fullname");
        UserAdministrationRules.Check(form, creating: false).Should().BeNull("the user name is not edited");
        UserAdministrationRules.Check(form, creating: true).Should().Be(new FieldRefusal("username", "User_NameTooShort", Typed: "3"));
        UserAdministrationRules.Check(form with { UserName = "n.tazi", Password = "" }, creating: true)!.Key
                               .Should().Be("Admin.InitialPasswordRequired");

        var request = UserAdministrationRules.ToCreateRequest(form with
        {
            UserName = " n.tazi ", Email = "  ", Departments = "bioch; hema, BIOCH", Qualification = ProfessionalQualification.MedicalBiologist
        });
        request.UserName.Should().Be("n.tazi");
        request.Email.Should().BeNull();
        request.AuthorizedDepartmentCodes.Should().Equal("BIOCH", "HEMA");

        UserAdministrationRules.FormOf(User()).Departments.Should().Be("BIOCH, HEMA");
        UserAdministrationRules.QualificationNoticeKey(ProfessionalQualification.PrescribingPhysician).Should().Be("Admin.PhysicianRequirementNotice");
        UserAdministrationRules.SignatureAllowed(ProfessionalQualification.LaboratoryTechnician).Should().BeFalse();
    }

    [Fact]
    public void Roles_and_sites_offer_the_active_ones_and_a_built_in_role_is_not_deactivated()
    {
        var manager = new LicenceTestKit.User(true, Permissions.Administration.ManageRoles);

        UserAdministrationRules.RoleActions(manager, Role(builtIn: true)).Toggle.Should().BeFalse();
        UserAdministrationRules.RoleActions(manager, Role(active: false)).Should().Be(new RoleActions(true, true, true, true, true));
        UserAdministrationRules.RoleActions(new LicenceTestKit.User(true, Permissions.Administration.ViewUsers), Role())
                               .Should().Be(new RoleActions(false, false, false, false, false));

        UserAdministrationRules.DeactivationNeedsConfirmation(Role(users: 3)).Should().BeTrue();
        UserAdministrationRules.DeactivationNeedsConfirmation(Role(users: 0)).Should().BeFalse();
        UserAdministrationRules.Change(Role(rights: ["a", "b"]), ["b", "c", "d"]).Should().Be(new PermissionChange(2, 1));
        UserAdministrationRules.Change(Role(rights: ["a"]), ["a"]).IsNone.Should().BeTrue();
        UserAdministrationRules.Grants(Role(rights: [Permissions.Results.BiologicalValidate])).Should().Be((1, true, false));
        UserAdministrationRules.AssignableRoles([Role(), Role(active: false)]).Should().HaveCount(1);

        var site = Guid.NewGuid();
        var other = Guid.NewGuid();
        UserAdministrationRules.DefaultSite([site, other], other).Should().Be(other);
        UserAdministrationRules.DefaultSite([site], other).Should().Be(site);
        UserAdministrationRules.CheckSites([]).Should().Be("User_AtLeastOneSiteRequired");
        UserAdministrationRules.Catalogue().Select(m => m.Module).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    // ---- Journal d'audit -----------------------------------------------------------------------

    [Fact]
    public void The_audit_trail_shows_deletions_break_glass_and_refusals_first_and_says_when_it_was_cut()
    {
        AuditEntryRow Change(AuditOperation op, string? reason = null, string? aggregate = null) =>
            new(Guid.NewGuid(), Now, "Result", Guid.NewGuid(), op, "admin", "POSTE-01", "Value", "4,2 → 5,1", reason,
                null, "P-2026-0001", aggregate, null);

        AuditTrailRules.Emphasis(Change(AuditOperation.SoftDelete)).Should().Be(AuditEmphasis.Critical);
        AuditTrailRules.Emphasis(Change(AuditOperation.Update, "Erreur de saisie")).Should().Be(AuditEmphasis.Notable);
        AuditTrailRules.Emphasis(Change(AuditOperation.Insert)).Should().Be(AuditEmphasis.None);
        AuditTrailRules.Entity(Change(AuditOperation.Update, aggregate: "LabOrder")).Should().Be("Result (LabOrder)");
        AuditTrailRules.Entity(Change(AuditOperation.Update, aggregate: "Result")).Should().Be("Result");
        AuditTrailRules.Detail(Change(AuditOperation.Update, "Erreur de saisie")).Should().Be("4,2 → 5,1   ·   Erreur de saisie");

        DataAccessRow Access(DataAccessKind kind, bool breakGlass = false) =>
            new(Guid.NewGuid(), Now, "admin", kind, null, null, 1, "Dossier", null, null, breakGlass, breakGlass ? "Urgence" : null);
        AuditTrailRules.Emphasis(Access(DataAccessKind.Export)).Should().Be(AuditEmphasis.Notable);
        AuditTrailRules.Emphasis(Access(DataAccessKind.ViewPatient, breakGlass: true)).Should().Be(AuditEmphasis.Critical);
        AuditTrailRules.Emphasis(Event(SecurityEventKind.LoginFailed, succeeded: false)).Should().Be(AuditEmphasis.Critical);

        AuditTrailRules.Status(500, truncated: true).Should().Be(("Audit.Truncated", 500, true));
        AuditTrailRules.Status(12, truncated: false).Key.Should().Be("Audit.RowCount");

        var filter = AuditTrailRules.Filter(new DateOnly(2026, 9, 20), new DateOnly(2026, 10, 20), "  ", " résultat ");
        filter.UserName.Should().BeNull();
        filter.Search.Should().Be("résultat");
        AuditTrailRules.PatientRecordNumber("  ").Should().BeNull();
        AuditTrailRules.PatientRecordNumber(" P-2026-0001 ").Should().Be("P-2026-0001");
    }

    // ---- Visa des caisses ----------------------------------------------------------------------

    [Fact]
    public void A_till_is_signed_off_by_another_with_the_right_and_a_variance_needs_a_comment()
    {
        var supervisor = new LicenceTestKit.User(true, Permissions.Billing.ReconcileCashSession);
        var own = Till(supervisor.UserId);
        var other = Till(Guid.NewGuid(), variance: -15m);

        CashReviewRules.IsOwnTill(supervisor, own).Should().BeTrue();
        CashReviewRules.CanReconcile(supervisor, own).Should().BeFalse("four eyes");
        CashReviewRules.CanReconcile(supervisor, other).Should().BeTrue();
        CashReviewRules.CanReconcile(new LicenceTestKit.User(true, Permissions.Billing.View), other).Should().BeFalse();
        CashReviewRules.CanReconcile(supervisor, Till(Guid.NewGuid(), reconciled: true)).Should().BeFalse();

        CashReviewRules.CommentRequired(other).Should().BeTrue();
        CashReviewRules.CommentRequired(Till(Guid.NewGuid())).Should().BeFalse();
        CashReviewRules.Reading(-15m).Should().Be(VarianceReading.BeyondTolerance);
        CashReviewRules.Reading(5m).Should().Be(VarianceReading.WithinTolerance);
        CashReviewRules.Reading(null).Should().Be(VarianceReading.None);
        CashReviewRules.PendingCount([own, other, Till(Guid.NewGuid(), reconciled: true)]).Should().Be(2);
        CashReviewRules.DefaultPeriod(new DateOnly(2026, 10, 20)).Should().Be((new DateOnly(2026, 9, 20), new DateOnly(2026, 10, 20)));
    }

    // ---- Paramètres ----------------------------------------------------------------------------

    [Fact]
    public void The_licence_right_alone_opens_the_licence_tab_and_the_backups_beside_it()
    {
        var licence = new LicenceTestKit.User(true, Permissions.Administration.License, Permissions.Administration.Backup);
        var settings = new LicenceTestKit.User(true, Permissions.Administration.ManageSites);
        var all = new LicenceTestKit.User(true, Permissions.Administration.ManageSettings, Permissions.Administration.License,
                                          Permissions.Administration.Backup);

        SettingsScreenRules.Tabs(licence).Should().Equal(SettingsTab.Backups, SettingsTab.Licence);
        SettingsScreenRules.Tabs(settings).Should().Equal(SettingsTab.Site, SettingsTab.Numbering, SettingsTab.Settings,
                                                          SettingsTab.InvoicePrint, SettingsTab.Languages, SettingsTab.Printers);
        SettingsScreenRules.Tabs(all).Should().HaveCount(9);
        SettingsScreenRules.Tabs(all, licenceAvailable: false, backupsAvailable: false).Should().NotContain([SettingsTab.Licence, SettingsTab.Backups]);
        SettingsScreenRules.HasSettingsRight(licence).Should().BeFalse();
    }

    [Fact]
    public void A_series_format_is_edited_before_its_first_number_and_a_fiscal_ones_start_never_moves_back()
    {
        var manager = new LicenceTestKit.User(true, Permissions.Administration.ManageSettings);
        SequenceRow Series(bool used, bool closed = false, SequencePurpose purpose = SequencePurpose.Invoice) =>
            new(Guid.NewGuid(), purpose, null, null, 2026, "FA-{yyyy}-{n:6}", "FA-2026-000001", used ? 32 : 1, used ? 31 : 0,
                null, closed, used);

        SettingsScreenRules.Actions(manager, Series(used: false)).Should().Be(new SequenceActions(true, true, true, true));
        SettingsScreenRules.Actions(manager, Series(used: true)).Should().Be(new SequenceActions(false, true, false, true));
        SettingsScreenRules.Actions(manager, Series(used: true, closed: true)).Should().Be(new SequenceActions(false, true, false, false));
        SettingsScreenRules.Actions(new LicenceTestKit.User(true, Permissions.Administration.ManageSites), Series(used: false))
                           .Should().Be(new SequenceActions(false, false, false, true));
        SettingsScreenRules.StateKey(Series(used: true)).Should().Be("Admin.SequenceInUse");
        SettingsScreenRules.ParseStart(" 120 ").Should().Be(120);
        SettingsScreenRules.ParseStart("-3").Should().BeNull();

        var secret = new SettingRow(Guid.NewGuid(), "Smtp.Password", "s3cret", null, null, true);
        SettingsScreenRules.DisplayValue(secret).Should().Be(SettingsScreenRules.MaskedValue);
        SettingsScreenRules.EditInitial(secret).Should().BeEmpty();
    }

    [Fact]
    public void The_discount_ceiling_is_refused_on_its_box_and_reads_back_as_shown()
    {
        SettingsScreenRules.ReadDiscountCap("120", "").Refusal!.Field.Should().Be("cap-percent");
        SettingsScreenRules.ReadDiscountCap("15", "-1").Refusal!.Field.Should().Be("cap-amount");
        SettingsScreenRules.ReadDiscountCap("15", " ").Cap.Should().Be(new DiscountCapSettings { MaxPercent = 15m, MaxAmount = null });

        var texts = SettingsScreenRules.DiscountCapTexts(new DiscountCapSettings { MaxPercent = 12.5m, MaxAmount = 300m },
                                                         CultureInfo.GetCultureInfo("fr-MA"));
        texts.Should().Be(("12,5", "300,00"));
        SettingsScreenRules.ReadDiscountCap(texts.Percent, texts.Amount).Cap!.MaxAmount.Should().Be(300m);
    }

    [Fact]
    public void A_site_save_keeps_the_english_names_and_drops_the_accreditation_end_once_not_accredited()
    {
        SettingsScreenRules.ShowsBlockers(LabSiteKind.ReferralPartner).Should().BeFalse();
        SettingsScreenRules.SavedStatus(new SiteSaveOutcome(Guid.NewGuid(), [], [])).Should().Be(("Site.ConfigurationComplete", true));
        SettingsScreenRules.SavedStatus(new SiteSaveOutcome(Guid.NewGuid(), ["Site.IceMissing"], [])).Key.Should().Be("Message.Saved");
        SettingsScreenRules.NewSite(" CASA ", " Labo Casa ", " ", LabSiteKind.ReferralPartner).Name.Ar.Should().BeNull();
        SettingsScreenRules.ActiveLanguages(arabic: false, english: true).Should().Equal(AppLanguage.French, AppLanguage.English);
        SettingsScreenRules.ArabicLicensed(null).Should().BeTrue();
        SettingsScreenRules.ArabicLicensed(LicenceTestKit.FullStatus()).Should().BeTrue();
    }

    [Fact]
    public void The_backup_schedule_typed_is_refused_on_a_piece_that_is_no_time_and_every_day_is_stored_as_such()
    {
        BackupScreenRules.Read(true, "22:00, 2h", BackupScreenRules.WeekDays, 30, @"D:\Sauvegardes").FirstError.ResourceKey
                         .Should().Be("Backup_TimesInvalid");
        BackupScreenRules.Read(true, "22:00", [], 30, @"D:\Sauvegardes").FirstError.ResourceKey.Should().Be("Backup_DaysRequired");

        var read = BackupScreenRules.Read(true, "13:00; 22:00", BackupScreenRules.WeekDays, 30, @" D:\Sauvegardes ").Value;
        read.Days.Should().BeEmpty("every day ticked is « every day »");
        read.Folder.Should().Be(@"D:\Sauvegardes");
        BackupScreenRules.TimesText(read).Should().Be("13:00, 22:00");

        BackupScreenRules.Age(TimeSpan.FromMinutes(45)).Should().BeEquivalentTo(new BackupLine("Backup.AgeMinutes", 45));
        BackupScreenRules.Age(TimeSpan.FromHours(30)).Should().BeEquivalentTo(new BackupLine("Backup.AgeHours", 30));
        BackupScreenRules.Age(TimeSpan.FromDays(3)).Should().BeEquivalentTo(new BackupLine("Backup.AgeDays", 3));
        BackupScreenRules.Size(3L * 1024 * 1024 * 1024).Key.Should().Be("Backup.SizeGb");
        BackupScreenRules.Size(1000).Should().Be(("Backup.SizeMb", 1d, "N0"));
        BackupScreenRules.MaySaveAnyway(Result.Failure(Error.Validation("Backup_FolderNotWritable"))).Should().BeTrue();

        BackupScreenRules.RestoreOfferedHere(new RestoreContext(IsServerMachine: true, IsLocalAdministrator: false)).Should().BeFalse();
        BackupScreenRules.DoneKey(new BackupOutcome(@"D:\x.bak", BackupCheckStatus.Pending)).Should().Be("Backup.DonePending");

        var set = new BackupSetRow(1, @"D:\Sauvegardes\MarocLIS.bak", Now.DateTime, Now.DateTime, 10, null, true, false, null,
                                   "Manuelle — admin", null, "SRV-LABO");
        var entry = new BackupEntry(set, BackupKind.Scheduled, new BackupCheck(BackupCheckStatus.Verified, Now, null));
        BackupScreenRules.Origin(entry).Should().Be(("BackupKind.Scheduled", "SRV-LABO"));
        BackupScreenRules.CheckTone(entry).Should().Be(BackupCheckTone.Ok);
        BackupScreenRules.Actions(busy: false, overview: null, entry).Should().Be(new BackupActions(false, false, true, true));
        BackupScreenRules.Actions(busy: true, overview: null, entry).Should().Be(new BackupActions(false, false, false, false));
    }

    [Fact]
    public void The_licence_tab_says_why_it_is_not_in_force_and_names_the_request_after_the_site()
    {
        var readOnly = LicenceTestKit.ReadOnlyStatus(benchUntil: LicenceTestKit.Today.AddDays(5));
        LicenceTabRules.SaysReadOnly(readOnly).Should().BeTrue();
        LicenceTabRules.Tone(readOnly).Should().Be(LicenceBannerLevel.Critical);
        LicenceTabRules.Notes(readOnly).Should().ContainSingle(n => n.Key == "Licence.BenchUntil");

        var missing = LicenceTestKit.Evaluate(LicenceTestKit.Facts(null));
        LicenceTabRules.Notes(missing).Should().ContainSingle(n => n.Key == "Licence.Cause.Missing" && n.AsSentence);
        LicenceTabRules.Sentence("aucune licence", CultureInfo.GetCultureInfo("fr-MA")).Should().Be("Aucune licence.");

        var valid = LicenceTestKit.Evaluate(LicenceTestKit.Facts(LicenceTestKit.Sign(LicenceTestKit.Annual(200, LicenceModules.Arabic))));
        LicenceTabRules.Binding(valid).Should().Be(LicenceBinding.BoundHere);
        LicenceTabRules.Remaining(valid).Should().Be(("Licence.DaysRemaining", (object?)200));
        LicenceTabRules.WorkstationLimit(valid).Should().Be(5);
        LicenceTabRules.Modules(valid).Open.Should().Equal(LicenceModules.Arabic);

        LicenceTabRules.RequestFileName(new LicenceRequest { ServerId = LicenceTestKit.ServerId, Site = "LABO CENTRAL" })
                       .Should().Be("demande-licence-LABO-CENTRAL" + LicenceRequest.Extension);
        LicenceTabRules.IsReadableSize(LicenceFile.MaxFileBytes + 1).Should().BeFalse();
    }
}
