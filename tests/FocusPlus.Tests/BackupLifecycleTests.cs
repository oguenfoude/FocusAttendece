using System.Security.Cryptography;
using FocusPlus.Data;
using Microsoft.Data.Sqlite;

namespace FocusPlus.Tests;

/// <summary>
/// Daily backup contract: one canonical <c>focus-attendance_yyyy-MM-dd_daily.db</c> per local date,
/// idempotent re-runs, forced same-day replacement, hash/size metadata and a safe restore that never
/// replaces the live database with an unverified payload.
/// </summary>
public sealed class BackupLifecycleTests
{
    [Fact]
    public void DailyBackup_WritesCanonicalFileNameAndMetadata()
    {
        using var fixture = new BackupFixture(new DateTime(2026, 11, 15, 8, 0, 0));

        var result = fixture.Backups.CreateDailyBackup();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(result.ErrorCode);
        Assert.False(result.AlreadyExisted);
        Assert.Equal(BackupKind.Daily, result.Kind);
        Assert.Equal("focus-attendance_2026-11-15_daily.db", result.FileName);
        Assert.Equal(Path.Combine(fixture.Backups.BackupDirectory, "focus-attendance_2026-11-15_daily.db"), result.Path);
        Assert.True(File.Exists(result.Path));

        var record = Assert.Single(fixture.Backups.ListBackupRecords());
        Assert.Equal(BackupKind.Daily, record.Kind);
        Assert.Equal("2026-11-15", record.DailyKey);
        Assert.Equal("2026-11-15", record.LocalDate);
        Assert.Equal(BackupStatus.Present, record.Status);
        Assert.False(record.IsRetentionAnchor);
        Assert.Equal(result.FileName, record.FileName);
        Assert.Equal(result.Path, record.FullPath);
        Assert.Equal(new FileInfo(result.Path!).Length, record.SizeBytes);
        Assert.Equal(Sha256Of(result.Path!), record.Sha256);
        Assert.Equal(result.Sha256, record.Sha256);
        Assert.True(record.SizeBytes > 0);

        var files = fixture.Backups.GetBackupsForDate(new DateTime(2026, 11, 15));
        Assert.Single(files);
        Assert.Equal(result.Path, files[0].Path);
        Assert.Equal(new DateTime(2026, 11, 15), BackupService.ParseDate(result.FileName!));
    }

    [Fact]
    public void DailyBackup_SecondCallSameDay_IsIdempotent()
    {
        using var fixture = new BackupFixture(new DateTime(2026, 11, 15, 8, 0, 0));

        var first = fixture.Backups.CreateDailyBackup();
        Assert.True(first.Success, first.ErrorMessage);

        fixture.Clock.Now = new DateTime(2026, 11, 15, 9, 30, 0);
        var second = fixture.Backups.CreateDailyBackup();

        Assert.True(second.Success, second.ErrorMessage);
        Assert.True(second.AlreadyExisted);
        Assert.Null(second.ErrorCode);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.CreatedUtc, second.CreatedUtc);
        Assert.Equal(first.SizeBytes, second.SizeBytes);

        Assert.Single(fixture.Backups.ListBackups());
        var record = Assert.Single(fixture.Backups.ListBackupRecords());
        Assert.Equal(first.Sha256, record.Sha256);
        Assert.Equal(first.CreatedUtc, record.CreatedUtc);
    }

    [Fact]
    public void DailyBackup_Force_ReplacesSameDayPayloadAndMetadata()
    {
        using var fixture = new BackupFixture(new DateTime(2026, 11, 15, 8, 0, 0));
        var first = fixture.Backups.CreateDailyBackup();
        Assert.True(first.Success, first.ErrorMessage);
        Assert.Null(ReadPayloadSetting(first.Path!, "test.sentinel"));

        SetLiveSetting(fixture.Database, "test.sentinel", "after-first-backup");
        fixture.Clock.Now = new DateTime(2026, 11, 15, 10, 0, 0);
        var forced = fixture.Backups.CreateDailyBackup(force: true, actor: "tester");

        Assert.True(forced.Success, forced.ErrorMessage);
        Assert.False(forced.AlreadyExisted);
        Assert.Null(forced.ErrorCode);
        Assert.Equal(first.Path, forced.Path);
        Assert.NotEqual(first.Sha256, forced.Sha256);
        Assert.Equal("after-first-backup", ReadPayloadSetting(forced.Path!, "test.sentinel"));

        Assert.Single(fixture.Backups.ListBackups());
        var record = Assert.Single(fixture.Backups.ListBackupRecords());
        Assert.Equal(BackupStatus.Present, record.Status);
        Assert.Equal(forced.Sha256, record.Sha256);
        Assert.Equal(new FileInfo(forced.Path!).Length, record.SizeBytes);
        Assert.Equal("tester", record.CreatedBy);
    }

    [Fact]
    public void DailyBackup_WhenPayloadWasTampered_ReplacesItAndRecordsTheReason()
    {
        using var fixture = new BackupFixture(new DateTime(2026, 11, 15, 8, 0, 0));
        var first = fixture.Backups.CreateDailyBackup();
        Assert.True(first.Success, first.ErrorMessage);

        using (var stream = new FileStream(first.Path!, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            stream.Write("tampered"u8);
        }

        var second = fixture.Backups.CreateDailyBackup();

        Assert.True(second.Success, second.ErrorMessage);
        Assert.False(second.AlreadyExisted);
        Assert.Equal("backup.metadata-mismatch", second.ErrorCode);
        Assert.Equal(first.Path, second.Path);
        Assert.NotEqual(first.Sha256, second.Sha256);

        var record = Assert.Single(fixture.Backups.ListBackupRecords());
        Assert.Equal(second.Sha256, record.Sha256);
        Assert.Equal(Sha256Of(second.Path!), record.Sha256);
        Assert.Equal(new FileInfo(second.Path!).Length, record.SizeBytes);
    }

    [Fact]
    public void DailyBackup_WhenLiveDatabaseIsMissing_FailsWithTypedError()
    {
        using var fixture = new BackupFixture(new DateTime(2026, 11, 15, 8, 0, 0));
        File.Delete(fixture.Database.DatabasePath);

        var result = fixture.Backups.CreateDailyBackup();

        Assert.False(result.Success);
        Assert.Equal("backup.database-missing", result.ErrorCode);
        Assert.Null(result.Path);
        Assert.Empty(Directory.Exists(fixture.Backups.BackupDirectory)
            ? Directory.GetFiles(fixture.Backups.BackupDirectory, "*.db")
            : []);
    }

    [Fact]
    public void DailyBackup_RejectsBlankActor()
    {
        using var fixture = new BackupFixture(new DateTime(2026, 11, 15, 8, 0, 0));

        Assert.Throws<ArgumentException>(() => fixture.Backups.CreateDailyBackup(force: false, actor: "  "));
        Assert.Throws<ArgumentException>(() => fixture.Backups.CreateSafetyBackup("reason", actor: " "));
        Assert.Throws<ArgumentException>(() => fixture.Backups.Restore("whatever.db", actor: " "));
    }

    [Fact]
    public void Restore_ReplacesLiveDatabaseAndWritesSafetyCopy()
    {
        using var fixture = new BackupFixture(new DateTime(2026, 11, 15, 8, 0, 0));
        SetLiveSetting(fixture.Database, "test.sentinel", "before-backup");
        var backup = fixture.Backups.CreateDailyBackup();
        Assert.True(backup.Success, backup.ErrorMessage);

        SetLiveSetting(fixture.Database, "test.sentinel", "after-backup");
        var result = fixture.Backups.Restore(backup.Path!, "tester");

        Assert.True(result.Success, $"{result.ErrorCode}: {result.ErrorMessage}");
        Assert.Null(result.ErrorCode);
        Assert.False(result.RollbackPerformed);
        Assert.Equal(Path.GetFullPath(backup.Path!), result.RestoredFrom);
        Assert.NotNull(result.SafetyBackupPath);
        Assert.True(File.Exists(result.SafetyBackupPath));
        Assert.Equal("before-backup", ReadLiveSetting(fixture.Database, "test.sentinel"));
        Assert.True(fixture.Database.IsHealthy(out _));

        // A successful restore reconciles metadata immediately: the restored snapshot carries its own
        // Backups table, so the safety copy is re-adopted from disk with its real size and hash.
        var records = fixture.Backups.ListBackupRecords();
        var safety = Assert.Single(records, r => r.Kind == BackupKind.SafetyRestore);
        Assert.Equal(BackupStatus.Present, safety.Status);
        Assert.Equal(Path.GetFileName(result.SafetyBackupPath), safety.FileName);
        Assert.Equal(result.SafetyBackupPath, safety.FullPath);
        Assert.Equal(new FileInfo(result.SafetyBackupPath!).Length, safety.SizeBytes);
        Assert.True(safety.SizeBytes > 0);
        Assert.Equal(Sha256Of(result.SafetyBackupPath!), safety.Sha256);

        var restoredDaily = Assert.Single(records, r => r.Kind == BackupKind.Daily && r.Status == BackupStatus.Present);
        Assert.Equal(Path.GetFileName(backup.Path), restoredDaily.FileName);
        Assert.Equal(Sha256Of(backup.Path!), restoredDaily.Sha256);
        Assert.Equal(new FileInfo(backup.Path!).Length, restoredDaily.SizeBytes);

        // No row may describe a payload that is not on disk, and no on-disk payload is left unlisted.
        Assert.Equal(2, records.Count);
        Assert.All(records, row => Assert.True(File.Exists(row.FullPath), row.FullPath));
        Assert.Equal(
            fixture.Backups.ListBackups().Select(f => f.FileName).OrderBy(n => n, StringComparer.Ordinal),
            records.Select(r => r.FileName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.True(File.Exists(result.SafetyBackupPath));
    }

    [Fact]
    public void Restore_WithHashMismatch_LeavesLiveDatabaseIntact()
    {
        using var fixture = new BackupFixture(new DateTime(2026, 11, 15, 8, 0, 0));
        var backup = fixture.Backups.CreateDailyBackup();
        Assert.True(backup.Success, backup.ErrorMessage);

        SetLiveSetting(fixture.Database, "test.sentinel", "live-value");
        using (var stream = new FileStream(backup.Path!, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            stream.Write("corruption"u8);
        }

        var result = fixture.Backups.Restore(backup.Path!, "tester");

        Assert.False(result.Success);
        Assert.Equal("restore.hash-mismatch", result.ErrorCode);
        Assert.NotNull(result.SafetyBackupPath);
        Assert.True(File.Exists(result.SafetyBackupPath));
        Assert.False(result.RollbackPerformed);
        Assert.Equal("live-value", ReadLiveSetting(fixture.Database, "test.sentinel"));
        Assert.True(fixture.Database.IsHealthy(out _));

        var audit = Assert.Single(ReadRestoreAudits(fixture.Database));
        Assert.Equal("tester", audit.Actor);
        Assert.Contains("result=restore.hash-mismatch", audit.Details, StringComparison.Ordinal);
        Assert.Contains($"safety={result.SafetyBackupPath}", audit.Details, StringComparison.Ordinal);
        Assert.Contains($"source={Path.GetFullPath(backup.Path!)}", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_FromCorruptSourceWithoutMetadata_LeavesLiveDatabaseIntact()
    {
        using var fixture = new BackupFixture(new DateTime(2026, 11, 15, 8, 0, 0));
        var backup = fixture.Backups.CreateDailyBackup();
        Assert.True(backup.Success, backup.ErrorMessage);
        SetLiveSetting(fixture.Database, "test.sentinel", "live-value");

        var corrupt = Path.Combine(fixture.Root, "focus-attendance_2026-11-14_daily.db");
        File.WriteAllBytes(corrupt, [.. Enumerable.Range(0, 4096).Select(i => (byte)(i % 251))]);

        var result = fixture.Backups.Restore(corrupt, "tester");

        Assert.False(result.Success);
        Assert.Equal("restore.integrity-check-failed", result.ErrorCode);
        Assert.False(result.RollbackPerformed);
        Assert.NotNull(result.SafetyBackupPath);
        Assert.True(File.Exists(result.SafetyBackupPath));
        Assert.Equal("live-value", ReadLiveSetting(fixture.Database, "test.sentinel"));
        Assert.True(fixture.Database.IsHealthy(out _));

        var leftovers = Directory.GetFiles(fixture.Backups.BackupDirectory, "*.tmp");
        Assert.Empty(leftovers);
    }

    [Fact]
    public void Restore_RejectsTheLiveDatabasePath()
    {
        using var fixture = new BackupFixture(new DateTime(2026, 11, 15, 8, 0, 0));
        SetLiveSetting(fixture.Database, "test.sentinel", "live-value");

        var result = fixture.Backups.Restore(fixture.Database.DatabasePath, "tester");

        Assert.False(result.Success);
        Assert.Equal("restore.source-is-live", result.ErrorCode);
        Assert.Equal("live-value", ReadLiveSetting(fixture.Database, "test.sentinel"));
    }

    [Fact]
    public void Restore_WithMissingSource_ReturnsTypedError()
    {
        using var fixture = new BackupFixture(new DateTime(2026, 11, 15, 8, 0, 0));
        var missing = Path.Combine(fixture.Root, "focus-attendance_2026-11-14_daily.db");

        var result = fixture.Backups.Restore(missing, "tester");

        Assert.False(result.Success);
        Assert.Equal("restore.source-missing", result.ErrorCode);
        Assert.NotNull(result.SafetyBackupPath);
        Assert.True(File.Exists(result.SafetyBackupPath));

        var safety = Assert.Single(fixture.Backups.ListBackupRecords());
        Assert.Equal(BackupKind.SafetyRestore, safety.Kind);
        Assert.Equal(BackupStatus.Present, safety.Status);

        var audit = Assert.Single(ReadRestoreAudits(fixture.Database));
        Assert.Equal("tester", audit.Actor);
        Assert.Contains("result=restore.source-missing", audit.Details, StringComparison.Ordinal);
        Assert.Contains($"safety={result.SafetyBackupPath}", audit.Details, StringComparison.Ordinal);
    }

    private static List<(string Actor, string Details)> ReadRestoreAudits(Database database)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Actor, Details FROM AuditLogs
             WHERE Action = 'backup.restored'
             ORDER BY TimestampUtc;
            """;

        var rows = new List<(string Actor, string Details)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
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

    private static string? ReadLiveSetting(Database database, string key)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM AppSettings WHERE Key = @key;";
        command.Parameters.AddWithValue("@key", key);
        return command.ExecuteScalar() as string;
    }

    private static string? ReadPayloadSetting(string path, string key)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM AppSettings WHERE Key = @key;";
        command.Parameters.AddWithValue("@key", key);
        return command.ExecuteScalar() as string;
    }

    private static string Sha256Of(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed class BackupFixture : IDisposable
    {
        public BackupFixture(DateTime localNow)
        {
            Root = Path.Combine(Path.GetTempPath(), "focus-backup-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Clock = new MutableClock(localNow);
            Database = new Database(Path.Combine(Root, "focus-attendance.db"));
            Database.Initialize();
            Backups = new BackupService(Database, () => Clock.Now);
        }

        public string Root { get; }

        public MutableClock Clock { get; }

        public Database Database { get; }

        public BackupService Backups { get; }

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
                // The SQLite pool can hold a temp handle briefly; cleanup is best effort.
            }
            catch (UnauthorizedAccessException)
            {
                // Same: read-only leftovers from a permission test.
            }
        }
    }

    private sealed class MutableClock(DateTime now)
    {
        public DateTime Now { get; set; } = now;
    }
}
