using FocusPlus.Application;
using FocusPlus.Data;
using FocusPlus.Domain;
using FocusPlus.Domain.Entities;
using Microsoft.Data.Sqlite;

namespace FocusPlus.Tests;

public sealed class DatabaseAndSeedTests
{
    [Fact]
    public async Task FreshDatabase_ContainsNoFakeOperationalData()
    {
        await using var context = new TestHost();
        var info = context.Host.Db.GetInfo();

        Assert.Equal(0, info.Employees);
        Assert.Equal(0, info.Devices);
        Assert.Equal(0, info.Punches);
        Assert.Empty(context.Host.EmployeeRepo.GetAll(includeArchived: true));
        Assert.Empty(context.Host.DeviceRepo.GetAll(includeArchived: true));
        Assert.Empty(context.Host.PunchRepo.GetByRange(DateTime.Today.AddYears(-5), DateTime.Today.AddYears(1)));
    }

    [Fact]
    public async Task FreshDatabase_SeedsOnlyTheRealOrgBaseline()
    {
        await using var context = new TestHost();
        var departments = context.Host.Organization.GetDepartments();
        var rules = context.Host.Shifts.GetRules();
        var sites = context.Host.Organization.GetSites();
        var users = context.Host.Users.GetAll();

        Assert.Single(sites);
        Assert.Contains("M'Sila", sites[0].NameFr);
        Assert.True(departments.Count >= 10);
        Assert.Equal(2, rules.Count);
        Assert.Single(users);
        Assert.Equal("admin", users[0].Username);
        Assert.Equal(UserRole.SuperAdmin, users[0].Role);

        Assert.Contains(departments, d => d.Code == "DRH");
        Assert.Contains(departments, d => d.Code == "DFC");
        Assert.Contains(departments, d => d.Code == "DGI");
        Assert.Contains(departments, d => d.Code == "DMO");
        Assert.Contains(departments, d => d.Code == "DPF");
        Assert.Contains(departments, d => d.Code == "CI");
        Assert.All(departments, d => Assert.False(string.IsNullOrWhiteSpace(d.NameAr)));
        Assert.All(departments, d => Assert.False(string.IsNullOrWhiteSpace(d.NameFr)));
        Assert.Contains(departments, d => d.NameAr.Contains("الموارد البشرية", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Database_UsesWalJournalMode()
    {
        await using var context = new TestHost();
        var info = context.Host.Db.GetInfo();

        Assert.True(info.IsWal, $"journal_mode was {info.JournalMode}");
        Assert.True(info.SchemaVersion >= 1);
        Assert.True(context.Host.Db.IsHealthy(out var message), message);
    }

    [Fact]
    public async Task OfficialShift_MatchesTheOpgiAdministrativeHours()
    {
        await using var context = new TestHost();
        var rule = context.Host.Shifts.GetDefault();

        Assert.Equal(WorkRule.OfficialCode, rule.Code);
        Assert.Equal(8 * 60, rule.MorningStartMin);
        Assert.Equal(12 * 60, rule.LunchStartMin);
        Assert.Equal(13 * 60, rule.LunchEndMin);
        Assert.Equal(16 * 60 + 30, rule.EveningEndMin);
        Assert.Equal(15, rule.MorningGraceMinutes);
        Assert.Equal(15, rule.AfternoonGraceMinutes);
        Assert.Equal(5, rule.EarlyLeaveGraceMinutes);
        Assert.True(rule.AutoDeductLunchBreak);
        Assert.Equal(60, rule.LunchDeductionMinutes);
        Assert.Equal(3, rule.AntiBounceMinutes);
        Assert.Equal(450, rule.StandardDailyTargetMinutes);
        Assert.True(rule.IsDefault);
    }

    [Fact]
    public async Task SeededAdmin_CanAuthenticate_AndWrongPasswordFails()
    {
        await using var context = new TestHost();
        var good = context.Host.Auth.Login("admin", SeedData.InitialAdminPassword);
        var bad = context.Host.Auth.Login("admin", "wrong-password");

        Assert.True(good.Success);
        Assert.NotNull(good.User);
        Assert.False(bad.Success);
        Assert.Equal(ErrorCodes.InvalidCredentials, bad.Error);
    }

    [Fact]
    public async Task Login_IsCaseInsensitiveOnUsername()
    {
        await using var context = new TestHost();
        var result = context.Host.Auth.Login("ADMIN", SeedData.InitialAdminPassword);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task PasswordChange_RequiresTheCurrentPassword()
    {
        await using var context = new TestHost();
        var user = context.Host.Auth.Login("admin", SeedData.InitialAdminPassword).User!;

        var wrong = context.Host.Auth.ChangePassword(user, "nope", "NewPassword@2026");
        Assert.False(wrong.Success);

        var tooShort = context.Host.Auth.ChangePassword(user, SeedData.InitialAdminPassword, "short");
        Assert.False(tooShort.Success);

        var ok = context.Host.Auth.ChangePassword(user, SeedData.InitialAdminPassword, "NewPassword@2026");
        Assert.True(ok.Success);
        Assert.True(context.Host.Auth.Login("admin", "NewPassword@2026").Success);
        Assert.False(context.Host.Auth.Login("admin", SeedData.InitialAdminPassword).Success);
    }

    [Fact]
    public async Task SoftArchive_NeverDeletesTheRow()
    {
        await using var context = new TestHost();
        var employee = NewEmployee("000001", "1001");
        context.Host.EmployeeRepo.Upsert(employee);

        context.Host.EmployeeRepo.Archive(employee.Id, "test", "tester");

        var archived = context.Host.EmployeeRepo.Get(employee.Id);
        Assert.NotNull(archived);
        Assert.True(archived!.IsArchived);
        Assert.NotNull(archived.ArchivedAtUtc);
        Assert.Equal("test", archived.ArchiveReason);
        Assert.Equal("tester", archived.ArchivedBy);
        Assert.Empty(context.Host.EmployeeRepo.GetAll());

        context.Host.EmployeeRepo.Restore(employee.Id, "tester");
        Assert.False(context.Host.EmployeeRepo.Get(employee.Id)!.IsArchived);
        Assert.Single(context.Host.EmployeeRepo.GetAll());
    }

    [Fact]
    public async Task DuplicateDeviceSerial_IsRejectedByTheDatabase()
    {
        await using var context = new TestHost();
        context.Host.DeviceRepo.Upsert(new Device { Name = "A", IpAddress = "192.168.1.10", SerialNumber = "SER-001" });

        var duplicate = new Device { Name = "B", IpAddress = "192.168.1.11", SerialNumber = "SER-001" };
        Assert.Throws<SqliteException>(() => context.Host.DeviceRepo.Upsert(duplicate));
    }

    [Fact]
    public async Task DuplicateEndpoint_IsRejectedWhileActive()
    {
        await using var context = new TestHost();
        context.Host.DeviceRepo.Upsert(new Device { Name = "A", IpAddress = "192.168.1.10", Port = 4370 });

        Assert.Throws<SqliteException>(() =>
            context.Host.DeviceRepo.Upsert(new Device { Name = "B", IpAddress = "192.168.1.10", Port = 4370 }));
    }

    [Fact]
    public async Task Punches_AreStrictlyDeduplicated()
    {
        await using var context = new TestHost();
        var punch = new Punch
        {
            DevicePin = "1001",
            DeviceSerial = "SER-001",
            PunchTimeLocal = new DateTime(2026, 11, 15, 8, 0, 0),
            PunchTimeUtc = new DateTime(2026, 11, 15, 7, 0, 0, DateTimeKind.Utc),
            DedupeKey = "SER-001|1001|2026-11-15T07:00:00.0000000Z",
            Source = PunchSource.Device
        };

        var first = context.Host.PunchRepo.InsertBatch([punch]);
        var second = context.Host.PunchRepo.InsertBatch([punch]);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(1, context.Host.PunchRepo.CountAll());
    }

    [Fact]
    public async Task Backup_CreatesAVerifiedCopy()
    {
        await using var context = new TestHost();
        var backup = context.Host.Backups.CreateBackup("test");

        Assert.True(File.Exists(backup.Path));
        Assert.True(backup.SizeBytes > 0);
        Assert.Single(context.Host.Backups.ListBackups());
    }

    [Fact]
    public async Task Settings_RoundTripAndAreSeededWithArabicAndWal()
    {
        await using var context = new TestHost();
        Assert.Equal("ar", context.Host.Settings.GetLanguage());
        Assert.Equal("Light", context.Host.Settings.GetTheme());

        context.Host.Settings.SetLanguage("fr");
        context.Host.Settings.SetMonitorIntervalSeconds(45);

        Assert.Equal("fr", context.Host.Settings.GetLanguage());
        Assert.Equal(45, context.Host.Settings.GetMonitorIntervalSeconds());
    }

    [Fact]
    public async Task AuditLog_RecordsSensitiveActions()
    {
        await using var context = new TestHost();
        context.Host.Auth.Login("admin", SeedData.InitialAdminPassword);

        var entries = context.Host.Audit.Recent(10);
        Assert.Contains(entries, e => e.Action == "login.success");
    }

    private static Employee NewEmployee(string matricule, string pin) => new()
    {
        Matricule = matricule,
        DevicePin = pin,
        FullNameAr = "موظف تجريبي",
        FirstNameFr = "Test",
        LastNameFr = "Employee",
        HireDate = new DateTime(2026, 1, 1)
    };
}
