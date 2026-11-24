using FocusPlus.Data;

namespace FocusPlus.Tests;

public sealed class BackupLifecycleTests
{
    [Fact]
    public void SixSlotsPerDay_ThenRecycleTheOldest()
    {
        var root = TempDirectory();
        try
        {
            var clock = new MutableClock(new DateTime(2026, 11, 15, 8, 0, 0));
            var database = new Database(Path.Combine(root, "focus-attendance.db"));
            database.Initialize();
            var backups = new BackupService(database, () => clock.Now);

            for (var i = 0; i < BackupService.SlotsPerDay; i++)
            {
                clock.Now = clock.Now.AddMinutes(10);
                backups.CreateBackup("slot");
            }

            var today = backups.GetBackupsForDate(new DateTime(2026, 11, 15));
            Assert.Equal(6, today.Count);
            Assert.All(today, file => Assert.Matches(@"slot0[1-6]\.db$", file.FileName));
            Assert.All(today, file => Assert.True(file.SizeBytes > 0));

            // The seventh backup recycles the oldest slot instead of creating a seventh file.
            clock.Now = clock.Now.AddMinutes(10);
            var recycled = backups.CreateBackup("slot");
            var after = backups.GetBackupsForDate(new DateTime(2026, 11, 15));

            Assert.Equal(6, after.Count);
            Assert.Contains(after, f => f.FileName == recycled.FileName);
            Assert.Single(after, f => f.FileName.EndsWith("slot01.db", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PreviousDaysAreDeleted_AfterTodaysBackupSucceeds()
    {
        var root = TempDirectory();
        try
        {
            var clock = new MutableClock(new DateTime(2026, 11, 14, 9, 0, 0));
            var database = new Database(Path.Combine(root, "focus-attendance.db"));
            database.Initialize();
            var backups = new BackupService(database, () => clock.Now);

            backups.CreateBackup("yesterday");
            var yesterday = backups.GetBackupsForDate(new DateTime(2026, 11, 14));
            Assert.NotEmpty(yesterday);
            var oldPath = yesterday[0].Path;

            clock.Now = new DateTime(2026, 11, 15, 9, 0, 0);
            backups.CreateBackup("today");

            Assert.False(File.Exists(oldPath));
            Assert.Empty(backups.GetBackupsForDate(new DateTime(2026, 11, 14)));
            Assert.Single(backups.GetBackupsForDate(new DateTime(2026, 11, 15)));
            Assert.All(backups.ListBackups(), f => Assert.StartsWith("focus-attendance_2026-11-15_", f.FileName, StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FileNameFormat_IsTheDocumentedSlotPattern()
    {
        var root = TempDirectory();
        try
        {
            var clock = new MutableClock(new DateTime(2026, 11, 15, 7, 30, 0));
            var database = new Database(Path.Combine(root, "focus-attendance.db"));
            database.Initialize();
            var backups = new BackupService(database, () => clock.Now);

            var file = backups.CreateBackup("manual");

            Assert.Matches(@"^focus-attendance_2026-11-15_slot0[1-6]\.db$", file.FileName);
            Assert.Equal(new DateTime(2026, 11, 15), BackupService.ParseDate(file.FileName));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void SlotCountAndLatest_ReflectTheCurrentDay()
    {
        var root = TempDirectory();
        try
        {
            var clock = new MutableClock(new DateTime(2026, 11, 15, 8, 0, 0));
            var database = new Database(Path.Combine(root, "focus-attendance.db"));
            database.Initialize();
            var backups = new BackupService(database, () => clock.Now);

            Assert.Equal(0, backups.TodaySlotCount);
            Assert.Null(backups.LatestForToday());

            clock.Now = clock.Now.AddMinutes(5);
            var first = backups.CreateBackup();
            clock.Now = clock.Now.AddMinutes(5);
            var second = backups.CreateBackup();

            Assert.Equal(2, backups.TodaySlotCount);
            Assert.Equal(second.Path, backups.LatestForToday()!.Path);
            Assert.NotEqual(first.Path, second.Path);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Restore_WorksFromATodaySlot()
    {
        var root = TempDirectory();
        try
        {
            var clock = new MutableClock(new DateTime(2026, 11, 15, 8, 0, 0));
            var database = new Database(Path.Combine(root, "focus-attendance.db"));
            database.Initialize();
            var backups = new BackupService(database, () => clock.Now);
            var backup = backups.CreateBackup("before-restore");

            var safety = backups.Restore(backup.Path);

            Assert.True(File.Exists(safety.Path));
            Assert.True(database.IsHealthy(out _));
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "focus-backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Cleanup(string root)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class MutableClock(DateTime now)
    {
        public DateTime Now { get; set; } = now;
    }
}
