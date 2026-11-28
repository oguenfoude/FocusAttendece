using FocusPlus.Application;
using FocusPlus.Application.Services;
using FocusPlus.Data;

namespace FocusPlus.Tests;

/// <summary>
/// Scheduler contract: one daily run per Africa/Algiers day with startup catch-up, forced manual
/// replacement, and pruning only after the daily copy succeeded.
/// </summary>
public sealed class BackupSchedulerTests
{
    private static readonly DateTime Today = new(2026, 11, 28);

    [Fact]
    public void RunScheduledBackup_OncePerDay_IsSkippedOnTheSecondCall()
    {
        using var fixture = new SchedulerFixture(Today);

        var first = fixture.Scheduler.RunScheduledBackup();

        Assert.True(first.Success, $"{first.ErrorCode}: {first.ErrorMessage}");
        Assert.False(first.Skipped);
        Assert.Equal("2026-11-28", first.LocalDate);
        Assert.NotNull(first.Backup);
        Assert.False(first.Backup.AlreadyExisted);
        Assert.NotNull(first.Prune);
        Assert.Equal(first.Backup.Path, fixture.Scheduler.LastDailyPath);
        Assert.Equal(DateTime.SpecifyKind(first.Backup.CreatedUtc, DateTimeKind.Utc), fixture.Scheduler.LastBackupUtc);
        Assert.True(fixture.Scheduler.LastRunSucceeded);
        Assert.Null(fixture.Scheduler.LastErrorCode);
        Assert.Single(fixture.Backups.ListBackups());

        fixture.Clock.Now = Today.AddHours(21);
        var second = fixture.Scheduler.RunScheduledBackup();

        Assert.True(second.Success);
        Assert.True(second.Skipped);
        Assert.Null(second.Backup);
        Assert.Null(second.Prune);
        Assert.Single(fixture.Backups.ListBackups());
        Assert.Equal(first.Backup.Path, fixture.Scheduler.LastDailyPath);
    }

    [Fact]
    public void RunScheduledBackup_NextDay_RunsAgain()
    {
        using var fixture = new SchedulerFixture(Today);
        Assert.True(fixture.Scheduler.RunScheduledBackup().Success);

        fixture.Clock.Now = Today.AddDays(1).AddHours(9);
        var next = fixture.Scheduler.RunScheduledBackup();

        Assert.True(next.Success, $"{next.ErrorCode}: {next.ErrorMessage}");
        Assert.False(next.Skipped);
        Assert.Equal("2026-11-29", next.LocalDate);
        Assert.Equal(2, fixture.Backups.ListBackups().Count);
        Assert.Equal(2, fixture.Backups.ListBackupRecords(BackupStatus.Present).Count);
    }

    [Fact]
    public void RunManualBackup_ForcesSameDayReplacement()
    {
        using var fixture = new SchedulerFixture(Today);
        var scheduled = fixture.Scheduler.RunScheduledBackup();
        Assert.True(scheduled.Success, scheduled.ErrorMessage);

        SetLiveSetting(fixture.Database, "test.sentinel", "after-scheduled");
        fixture.Clock.Now = Today.AddHours(11);
        var manual = fixture.Scheduler.RunManualBackup();

        Assert.True(manual.Success, $"{manual.ErrorCode}: {manual.ErrorMessage}");
        Assert.False(manual.Skipped);
        Assert.NotNull(manual.Backup);
        Assert.False(manual.Backup.AlreadyExisted);
        Assert.Equal(scheduled.Backup!.Path, manual.Backup.Path);
        Assert.NotEqual(scheduled.Backup.Sha256, manual.Backup.Sha256);
        Assert.NotNull(manual.Prune);

        Assert.Single(fixture.Backups.ListBackups());
        var record = Assert.Single(fixture.Backups.ListBackupRecords());
        Assert.Equal(manual.Backup.Sha256, record.Sha256);
        Assert.True(manual.Backup.CreatedUtc > scheduled.Backup.CreatedUtc);
    }

    [Fact]
    public void RunScheduledBackup_AppliesRetentionPolicyAfterSuccess()
    {
        using var fixture = new SchedulerFixture(Today);
        fixture.Scheduler.KeepDays = 2;
        fixture.Scheduler.KeepMonthly = 0;
        for (var index = 4; index >= 1; index--)
        {
            fixture.CreateDaily(Today.AddDays(-index));
        }

        fixture.Clock.Now = Today.AddHours(9);
        var report = fixture.Scheduler.RunScheduledBackup();

        Assert.True(report.Success, $"{report.ErrorCode}: {report.ErrorMessage}");
        Assert.NotNull(report.Prune);
        Assert.Equal(3, report.Prune.DeletedFiles);
        Assert.Empty(report.Prune.FailedFiles);
        Assert.Equal(3, fixture.Scheduler.LastPrunedFiles);

        var present = fixture.Backups.ListBackupRecords(BackupStatus.Present);
        Assert.Equal(2, present.Count);
        Assert.Contains(present, row => row.LocalDate == "2026-11-28");
        Assert.Contains(present, row => row.LocalDate == "2026-11-27");
        Assert.Equal(2, fixture.Backups.ListBackups().Count);
        Assert.Equal(3, fixture.Backups.ListBackupRecords(BackupStatus.Pruned).Count);
    }

    [Fact]
    public void RunScheduledBackup_DoesNotPruneWhenTheDailyBackupFails()
    {
        using var fixture = new SchedulerFixture(Today);
        for (var index = 35; index >= 1; index--)
        {
            fixture.CreateDaily(Today.AddDays(-index));
        }

        File.Delete(fixture.Database.DatabasePath);

        var failed = fixture.Scheduler.RunScheduledBackup();

        Assert.False(failed.Success);
        Assert.False(failed.Skipped);
        Assert.NotNull(failed.Backup);
        Assert.False(failed.Backup.Success);
        Assert.Null(failed.Backup.Path);
        Assert.Null(failed.Prune);
        Assert.Equal("backup.database-missing", failed.ErrorCode);
        Assert.False(fixture.Scheduler.LastRunSucceeded);
        Assert.Equal("backup.database-missing", fixture.Scheduler.LastErrorCode);
        Assert.Equal(0, fixture.Scheduler.LastPrunedFiles);
        Assert.Null(fixture.Scheduler.LastDailyPath);
        Assert.Equal(35, fixture.Backups.ListBackupRecords(BackupStatus.Present).Count);
        Assert.Empty(fixture.Backups.ListBackupRecords(BackupStatus.Pruned));
        Assert.Equal(35, fixture.Backups.ListBackups().Count);

        // A failed day is retried, not skipped.
        fixture.Clock.Now = Today.AddHours(12);
        var retried = fixture.Scheduler.RunScheduledBackup();

        Assert.False(retried.Success);
        Assert.False(retried.Skipped);
        Assert.Equal("backup.database-missing", retried.ErrorCode);
        Assert.Equal(35, fixture.Backups.ListBackups().Count);
    }

    [Fact]
    public async Task Start_PerformsStartupCatchUpInsideTheLoop()
    {
        using var fixture = new SchedulerFixture(Today);
        fixture.Scheduler.DailyTime = new TimeSpan(23, 59, 0);
        var todayPath = Path.Combine(fixture.Backups.BackupDirectory, "focus-attendance_2026-11-28_daily.db");

        try
        {
            fixture.Scheduler.Start();
            Assert.True(fixture.Scheduler.IsRunning);

            await WaitUntilAsync(() => fixture.Scheduler.LastRunLocalDate is not null, TimeSpan.FromSeconds(10));

            Assert.True(File.Exists(todayPath));
            Assert.Equal("2026-11-28", fixture.Scheduler.LastRunLocalDate);
            Assert.True(fixture.Scheduler.LastRunSucceeded);
            var record = Assert.Single(fixture.Backups.ListBackupRecords());
            Assert.Equal(BackupStatus.Present, record.Status);
        }
        finally
        {
            await fixture.Scheduler.StopAsync();
        }

        Assert.False(fixture.Scheduler.IsRunning);
    }

    [Fact]
    public void SchedulerProperties_RejectOutOfRangeValues()
    {
        using var fixture = new SchedulerFixture(Today);

        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Scheduler.DailyTime = TimeSpan.FromDays(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Scheduler.DailyTime = TimeSpan.FromSeconds(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Scheduler.KeepDays = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Scheduler.KeepDays = 3651);
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Scheduler.KeepMonthly = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Scheduler.KeepMonthly = 121);

        fixture.Scheduler.KeepDays = 30;
        fixture.Scheduler.KeepMonthly = 12;
        fixture.Scheduler.DailyTime = new TimeSpan(20, 0, 0);
        Assert.Equal(30, fixture.Scheduler.KeepDays);
        Assert.Equal(12, fixture.Scheduler.KeepMonthly);
        Assert.Equal(new TimeSpan(20, 0, 0), fixture.Scheduler.DailyTime);
    }

    private static void SetLiveSetting(Database database, string key, string value)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO AppSettings (Key, Value) VALUES (@key, @value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.ExecuteNonQuery();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

    private sealed class SchedulerFixture : IDisposable
    {
        public SchedulerFixture(DateTime localNow)
        {
            Root = Path.Combine(Path.GetTempPath(), "focus-backup-scheduler-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Clock = new MutableClock(localNow.Date.AddHours(9));
            Database = new Database(Path.Combine(Root, "focus-attendance.db"));
            Database.Initialize();
            Backups = new BackupService(Database, () => Clock.Now);
            Scheduler = new BackupSchedulerService(Backups, new AppLogger(Path.Combine(Root, "logs")), () => Clock.Now);
        }

        public string Root { get; }

        public MutableClock Clock { get; }

        public Database Database { get; }

        public BackupService Backups { get; }

        public BackupSchedulerService Scheduler { get; }

        public BackupResult CreateDaily(DateTime date)
        {
            Clock.Now = date.Date.AddHours(9);
            var result = Backups.CreateDailyBackup();
            Assert.True(result.Success, $"{date:yyyy-MM-dd}: {result.ErrorCode} {result.ErrorMessage}");
            return result;
        }

        public void Dispose()
        {
            Scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Backups.Dispose();
            Database.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: SQLite can keep a pooled handle briefly.
            }
            catch (UnauthorizedAccessException)
            {
                // Same: cleanup is best effort.
            }
        }
    }

    private sealed class MutableClock(DateTime now)
    {
        public DateTime Now { get; set; } = now;
    }
}
