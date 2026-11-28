using FocusPlus.Data;

namespace FocusPlus.Tests;

/// <summary>
/// Retention contract: newest 30 daily payloads, the newest payload of each of the last 12 calendar
/// months, safety copies younger than 7 days, prune only after today's backup succeeded, payload-only
/// deletion with metadata rows retained as <see cref="BackupStatus.Pruned"/>, legacy slot files and
/// orphan reconciliation.
/// </summary>
public sealed class BackupRetentionTests
{
    private static readonly DateTime Today = new(2026, 11, 28);

    [Fact]
    public void Prune_KeepsNewestThirtyDailies_AndDeletesOlderPayloads()
    {
        using var fixture = new RetentionFixture(Today);
        var start = Today.AddDays(-39);
        for (var index = 0; index < 40; index++)
        {
            fixture.CreateDaily(start.AddDays(index));
        }

        Assert.Equal(40, fixture.Backups.ListBackupRecords(BackupStatus.Present).Count);

        var result = fixture.Backups.PruneBackups(30, 12);

        Assert.Empty(result.FailedFiles);
        Assert.Equal(10, result.DeletedFiles);
        Assert.True(result.BytesFreed > 0);
        Assert.Equal(30, result.RetainedFiles.Count);

        for (var index = 0; index < 10; index++)
        {
            var date = start.AddDays(index);
            Assert.False(File.Exists(fixture.PayloadPath(date)), $"{date:yyyy-MM-dd} should have been pruned");
        }

        var present = fixture.Backups.ListBackupRecords(BackupStatus.Present);
        Assert.Equal(30, present.Count);
        Assert.Equal(Today, present.Max(r => Parse(r.LocalDate)));

        var pruned = fixture.Backups.ListBackupRecords(BackupStatus.Pruned);
        Assert.Equal(10, pruned.Count);
        Assert.All(pruned, row =>
        {
            Assert.Equal(BackupKind.Daily, row.Kind);
            Assert.False(row.IsRetentionAnchor);
            Assert.NotNull(row.PrunedAtUtc);
            Assert.False(File.Exists(row.FullPath));
        });
        Assert.All(present, row => Assert.True(File.Exists(row.FullPath)));
    }

    [Fact]
    public void Prune_KeepsTwelveMonthlyAnchors_AndDropsOlderMonths()
    {
        using var fixture = new RetentionFixture(Today);

        var oldMonths = new[]
        {
            new DateTime(2024, 1, 1),
            new DateTime(2025, 6, 1),
            new DateTime(2025, 12, 5),
            new DateTime(2026, 1, 20),
            new DateTime(2026, 2, 10),
            new DateTime(2026, 3, 15),
            new DateTime(2026, 4, 1),
            new DateTime(2026, 5, 5),
            new DateTime(2026, 6, 30),
            new DateTime(2026, 7, 10),
            new DateTime(2026, 8, 20),
            new DateTime(2026, 9, 15)
        };

        foreach (var date in oldMonths)
        {
            fixture.CreateDaily(date);
        }

        for (var index = 31; index >= 0; index--)
        {
            fixture.CreateDaily(Today.AddDays(-index));
        }

        Assert.Equal(44, fixture.Backups.ListBackupRecords(BackupStatus.Present).Count);

        var result = fixture.Backups.PruneBackups(30, 12);

        Assert.Empty(result.FailedFiles);
        Assert.Equal(4, result.DeletedFiles);

        // Outside the 30-day window and outside the last 12 months.
        Assert.False(File.Exists(fixture.PayloadPath(new DateTime(2024, 1, 1))));
        Assert.False(File.Exists(fixture.PayloadPath(new DateTime(2025, 6, 1))));
        // Inside October: the monthly anchor is the 31st, so these two are pruned.
        Assert.False(File.Exists(fixture.PayloadPath(new DateTime(2026, 10, 28))));
        Assert.False(File.Exists(fixture.PayloadPath(new DateTime(2026, 10, 29))));

        for (var month = 0; month < 10; month++)
        {
            var anchorDate = oldMonths[2 + month];
            Assert.True(File.Exists(fixture.PayloadPath(anchorDate)), $"anchor {anchorDate:yyyy-MM-dd} should be kept");
        }

        var present = fixture.Backups.ListBackupRecords(BackupStatus.Present);
        Assert.Equal(40, present.Count);

        // Every retained row is flagged as an anchor; the monthly ones are what survives the 30-day window.
        var anchors = present.Where(r => r.IsRetentionAnchor).ToList();
        Assert.Equal(present.Count, anchors.Count);
        Assert.Contains(anchors, row => row.LocalDate == "2025-12-05");
        Assert.Contains(anchors, row => row.LocalDate == "2026-09-15");
        Assert.Contains(anchors, row => row.LocalDate == "2026-10-31");
        Assert.Contains(anchors, row => row.LocalDate == "2026-11-28");

        var pruned = fixture.Backups.ListBackupRecords(BackupStatus.Pruned);
        Assert.Equal(4, pruned.Count);
        Assert.All(pruned, row => Assert.NotNull(row.PrunedAtUtc));
        Assert.Contains(pruned, row => row.LocalDate == "2026-10-28");
    }

    [Fact]
    public void Prune_KeepsSafetyCopiesYoungerThanSevenDays()
    {
        using var fixture = new RetentionFixture(Today);
        fixture.CreateDaily(Today);

        fixture.Clock.Now = Today.AddDays(-10).AddHours(9);
        var stale = fixture.Backups.CreateSafetyBackup("before-restore", "tester");
        Assert.True(stale.Success, stale.ErrorMessage);

        fixture.Clock.Now = Today.AddDays(-4).AddHours(9);
        var fresh = fixture.Backups.CreateSafetyBackup("before-restore", "tester");
        Assert.True(fresh.Success, fresh.ErrorMessage);

        fixture.Clock.Now = Today.AddHours(9);
        var result = fixture.Backups.PruneBackups(30, 12);

        Assert.Empty(result.FailedFiles);
        Assert.Equal(1, result.DeletedFiles);
        Assert.False(File.Exists(stale.Path));
        Assert.True(File.Exists(fresh.Path));

        var records = fixture.Backups.ListBackupRecords();
        Assert.Single(records, r => r.FileName == Path.GetFileName(stale.Path) && r.Status == BackupStatus.Pruned);
        Assert.Single(records, r => r.FileName == Path.GetFileName(fresh.Path!) && r.Status == BackupStatus.Present);
    }

    [Fact]
    public void Prune_DoesNotRunBeforeTodaysDailyBackupExists()
    {
        using var fixture = new RetentionFixture(Today);
        for (var index = 35; index >= 1; index--)
        {
            fixture.CreateDaily(Today.AddDays(-index));
        }

        fixture.Clock.Now = Today.AddHours(9);
        var skipped = fixture.Backups.PruneBackups(30, 12);

        Assert.Equal(0, skipped.DeletedFiles);
        Assert.Equal(0, skipped.BytesFreed);
        Assert.Equal(35, fixture.Backups.ListBackupRecords(BackupStatus.Present).Count);
        Assert.Empty(fixture.Backups.ListBackupRecords(BackupStatus.Pruned));
        Assert.All(fixture.Backups.ListBackupRecords(), row => Assert.True(File.Exists(row.FullPath)));

        fixture.CreateDaily(Today);
        var result = fixture.Backups.PruneBackups(30, 12);

        Assert.Equal(6, result.DeletedFiles);
        Assert.Equal(30, fixture.Backups.ListBackupRecords(BackupStatus.Present).Count);
        Assert.Equal(6, fixture.Backups.ListBackupRecords(BackupStatus.Pruned).Count);
    }

    [Fact]
    public void Prune_ReportsLockedOrReadOnlyFilesWithoutDeletingThem()
    {
        if (OperatingSystem.IsWindows())
        {
            // Directory unlink permissions are a POSIX concept; the guard keeps the test portable.
            return;
        }

        using var fixture = new RetentionFixture(Today);
        fixture.CreateDaily(Today.AddDays(-2));
        fixture.CreateDaily(Today.AddDays(-1));
        fixture.CreateDaily(Today);

        var backupsDirectory = fixture.Backups.BackupDirectory;
        var original = File.GetUnixFileMode(backupsDirectory);
        Assert.True(File.Exists(fixture.PayloadPath(Today.AddDays(-2))));
        Assert.True(File.Exists(fixture.PayloadPath(Today.AddDays(-1))));

        PruneResult result;
        try
        {
            File.SetUnixFileMode(backupsDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            result = fixture.Backups.PruneBackups(1, 0);
        }
        finally
        {
            File.SetUnixFileMode(backupsDirectory, original);
        }

        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(0, result.BytesFreed);
        Assert.Equal(2, result.FailedFiles.Count);
        Assert.Contains("focus-attendance_2026-11-26_daily.db", result.FailedFiles);
        Assert.Contains("focus-attendance_2026-11-27_daily.db", result.FailedFiles);
        Assert.Equal("focus-attendance_2026-11-28_daily.db", Assert.Single(result.RetainedFiles));

        Assert.True(File.Exists(fixture.PayloadPath(Today.AddDays(-2))));
        Assert.True(File.Exists(fixture.PayloadPath(Today.AddDays(-1))));
        Assert.Equal(3, fixture.Backups.ListBackupRecords(BackupStatus.Present).Count);
        Assert.Empty(fixture.Backups.ListBackupRecords(BackupStatus.Pruned));
    }

    [Fact]
    public void Prune_HandlesLegacySlotFiles()
    {
        using var fixture = new RetentionFixture(Today);
        var daily = fixture.CreateDaily(Today);

        var slotOne = Path.Combine(fixture.Backups.BackupDirectory, "focus-attendance_2020-01-01_slot01.db");
        var slotTwo = Path.Combine(fixture.Backups.BackupDirectory, "focus-attendance_2020-01-01_slot02.db");
        File.Copy(daily.Path!, slotOne);
        File.Copy(daily.Path!, slotTwo);
        var legacyStamp = new DateTime(2020, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(slotOne, legacyStamp);
        File.SetLastWriteTimeUtc(slotTwo, legacyStamp);

        var result = fixture.Backups.PruneBackups(1, 0);

        Assert.Empty(result.FailedFiles);
        Assert.Equal(2, result.DeletedFiles);
        Assert.False(File.Exists(slotOne));
        Assert.False(File.Exists(slotTwo));
        Assert.True(File.Exists(daily.Path));

        var legacyRows = fixture.Backups.ListBackupRecords(BackupStatus.Pruned);
        Assert.Equal(2, legacyRows.Count);
        Assert.All(legacyRows, row =>
        {
            Assert.Equal(BackupKind.Daily, row.Kind);
            Assert.Equal("2020-01-01", row.LocalDate);
            Assert.NotNull(row.PrunedAtUtc);
        });
        Assert.Single(fixture.Backups.ListBackupRecords(BackupStatus.Present));
    }

    [Fact]
    public void Prune_ReconcilesMissingPayloadsAsMissing()
    {
        using var fixture = new RetentionFixture(Today);
        var yesterday = fixture.CreateDaily(Today.AddDays(-1));
        var today = fixture.CreateDaily(Today);

        File.Delete(yesterday.Path!);

        var result = fixture.Backups.PruneBackups(30, 12);

        Assert.Empty(result.FailedFiles);
        Assert.Equal(0, result.DeletedFiles);

        var missing = fixture.Backups.ListBackupRecords(BackupStatus.Missing);
        var row = Assert.Single(missing);
        Assert.Equal(Path.GetFileName(yesterday.Path), row.FileName);
        Assert.Equal("backup.file-missing", row.FailureCode);
        Assert.Single(fixture.Backups.ListBackupRecords(BackupStatus.Present));
        Assert.True(File.Exists(today.Path));
    }

    [Fact]
    public void Prune_AdoptsUntrackedPayloadFiles()
    {
        using var fixture = new RetentionFixture(Today);
        var daily = fixture.CreateDaily(Today);

        var orphanDate = new DateTime(2026, 11, 10);
        var orphan = Path.Combine(fixture.Backups.BackupDirectory, "focus-attendance_2026-11-10_daily.db");
        File.Copy(daily.Path!, orphan);

        var result = fixture.Backups.PruneBackups(30, 12);

        Assert.Empty(result.FailedFiles);
        var adopted = Assert.Single(fixture.Backups.ListBackupRecords(), r => r.FileName == Path.GetFileName(orphan));
        Assert.Equal(BackupStatus.Present, adopted.Status);
        Assert.Equal(BackupKind.Daily, adopted.Kind);
        Assert.Equal("2026-11-10", adopted.DailyKey);
        Assert.Equal("2026-11-10", adopted.LocalDate);
        Assert.Equal("reconcile", adopted.CreatedBy);
        Assert.True(adopted.SizeBytes > 0);
        Assert.False(string.IsNullOrWhiteSpace(adopted.Sha256));
        Assert.True(File.Exists(orphan));

        fixture.Clock.Now = orphanDate.AddHours(9);
        var second = fixture.Backups.PruneBackups(30, 12);
        Assert.Equal(0, second.DeletedFiles);
    }

    [Fact]
    public void Prune_RejectsInvalidRetentionArguments()
    {
        using var fixture = new RetentionFixture(Today);
        fixture.CreateDaily(Today);

        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Backups.PruneBackups(0, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Backups.PruneBackups(30, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Backups.PruneBackups(30, 121));
    }

    private static DateTime Parse(string localDate) =>
        DateTime.ParseExact(localDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class RetentionFixture : IDisposable
    {
        public RetentionFixture(DateTime localNow)
        {
            Root = Path.Combine(Path.GetTempPath(), "focus-backup-retention-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Clock = new MutableClock(localNow.Date.AddHours(9));
            Database = new Database(Path.Combine(Root, "focus-attendance.db"));
            Database.Initialize();
            Backups = new BackupService(Database, () => Clock.Now);
        }

        public string Root { get; }

        public MutableClock Clock { get; }

        public Database Database { get; }

        public BackupService Backups { get; }

        public BackupResult CreateDaily(DateTime date)
        {
            Clock.Now = date.Date.AddHours(9);
            var result = Backups.CreateDailyBackup();
            Assert.True(result.Success, $"{date:yyyy-MM-dd}: {result.ErrorCode} {result.ErrorMessage}");
            return result;
        }

        public string PayloadPath(DateTime date) =>
            Path.Combine(Backups.BackupDirectory, $"focus-attendance_{date:yyyy-MM-dd}_daily.db");

        public void Dispose()
        {
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
                // Same: a permission test may leave a read-only directory behind.
            }
        }
    }

    private sealed class MutableClock(DateTime now)
    {
        public DateTime Now { get; set; } = now;
    }
}
