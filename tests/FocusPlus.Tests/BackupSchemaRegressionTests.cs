using System.Security.Cryptography;
using FocusPlus.Data;
using Microsoft.Data.Sqlite;

namespace FocusPlus.Tests;

/// <summary>
/// Regression guard: a database file written before the Backups metadata table existed must be
/// upgraded by the backup service itself, so pre-migration and restore-safety copies are tracked
/// immediately instead of failing with <c>backup.metadata-write-failed: no such table: Backups</c>.
/// </summary>
public sealed class BackupSchemaRegressionTests
{
    [Fact]
    public void PreMigrationBackup_OnLegacyDatabase_EnsuresSchemaAndTracksRow()
    {
        using var fixture = new LegacyBackupFixture();
        fixture.DropBackupsTable();

        var result = fixture.Backups.CreateBackup("startup");

        Assert.True(File.Exists(result.Path));
        Assert.Equal(new DateTime(2026, 11, 15), BackupService.ParseDate(result.FileName!));

        var record = Assert.Single(fixture.Backups.ListBackupRecords());
        Assert.Equal(BackupKind.PreMigration, record.Kind);
        Assert.Equal(BackupStatus.Present, record.Status);
        Assert.Equal(result.FileName, record.FileName);
        Assert.Equal(result.Path, record.FullPath);
        Assert.Equal(new FileInfo(result.Path).Length, record.SizeBytes);
        Assert.Equal(Sha256Of(result.Path), record.Sha256);
        Assert.Equal(result.SizeBytes, record.SizeBytes);
        Assert.True(record.SizeBytes > 0);
    }

    [Fact]
    public void SafetyBackup_OnLegacyDatabase_EnsuresSchemaAndTracksRow()
    {
        using var fixture = new LegacyBackupFixture();
        fixture.DropBackupsTable();

        var result = fixture.Backups.CreateSafetyBackup("before-migration");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(result.ErrorCode);
        Assert.Equal(BackupKind.SafetyRestore, result.Kind);
        Assert.True(File.Exists(result.Path));

        var record = Assert.Single(fixture.Backups.ListBackupRecords());
        Assert.Equal(BackupKind.SafetyRestore, record.Kind);
        Assert.Equal(BackupStatus.Present, record.Status);
        Assert.Equal(result.FileName, record.FileName);
        Assert.Equal(result.Path, record.FullPath);
        Assert.Equal(new FileInfo(result.Path!).Length, record.SizeBytes);
        Assert.Equal(Sha256Of(result.Path!), record.Sha256);
        Assert.Equal(result.Sha256, record.Sha256);
        Assert.True(record.SizeBytes > 0);
    }

    private static string Sha256Of(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Seeded database file that predates the Backups metadata table until it is dropped.</summary>
    private sealed class LegacyBackupFixture : IDisposable
    {
        public LegacyBackupFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "focus-backup-schema-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Database = new Database(Path.Combine(Root, "focus-attendance.db"));
            Database.Initialize();
            Backups = new BackupService(Database, () => new DateTime(2026, 11, 15, 8, 0, 0, DateTimeKind.Utc));
        }

        public string Root { get; }

        public Database Database { get; }

        public BackupService Backups { get; }

        public void DropBackupsTable()
        {
            using var connection = Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE IF EXISTS Backups;";
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            Backups.Dispose();
            Database.Dispose();
            SqliteConnection.ClearAllPools();
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
                // Same.
            }
        }
    }
}
