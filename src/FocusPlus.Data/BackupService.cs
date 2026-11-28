using System.Globalization;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.Sqlite;

namespace FocusPlus.Data;

public enum BackupKind
{
    Daily = 0,
    Manual = 1,
    SafetyRestore = 2,
    PreMigration = 3
}

public enum BackupStatus
{
    Present = 0,
    Pruned = 1,
    Failed = 2,
    Missing = 3
}

public sealed record BackupResult(
    bool Success,
    BackupKind Kind,
    string? Path,
    DateTime CreatedUtc,
    long SizeBytes,
    string? Sha256,
    bool AlreadyExisted,
    string? ErrorCode,
    string? ErrorMessage)
{
    public string? FileName => Path is null ? null : System.IO.Path.GetFileName(Path);
}

public sealed record PruneResult(
    int DeletedFiles,
    long BytesFreed,
    IReadOnlyList<string> RetainedFiles,
    IReadOnlyList<string> FailedFiles);

public sealed record RestoreResult(
    bool Success,
    string? RestoredFrom,
    string? SafetyBackupPath,
    bool RollbackPerformed,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>Append-only backup metadata row. Payload deletion is recorded as <see cref="BackupStatus.Pruned"/>.</summary>
public sealed record BackupRecord
{
    public string Id { get; init; } = string.Empty;
    public BackupKind Kind { get; init; }
    public string? DailyKey { get; init; }
    public string LocalDate { get; init; } = string.Empty;
    public DateTime CreatedUtc { get; init; }
    public string FileName { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public BackupStatus Status { get; init; }
    public bool IsRetentionAnchor { get; init; }
    public DateTime? PrunedAtUtc { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
    public string? CreatedBy { get; init; }
    public string? Note { get; init; }
    public string FullPath { get; init; } = string.Empty;
}

/// <summary>Payload file on disk (filesystem view; used by legacy callers).</summary>
public sealed record BackupFileInfo(string Path, DateTime CreatedUtc, long SizeBytes)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public double SizeMb => Math.Round(SizeBytes / 1024d / 1024d, 2);
}

/// <summary>
/// Daily backup lifecycle: one canonical <c>focus-attendance_yyyy-MM-dd_daily.db</c> per local date
/// (Africa/Algiers), retention of the newest 30 daily copies plus monthly anchors, and a safe restore.
/// Legacy <c>focus-attendance_yyyy-MM-dd_slotNN.db</c> files are still read and pruned.
/// </summary>
public sealed class BackupService : IDisposable
{
    public const int DefaultKeepDays = 30;
    public const int DefaultKeepMonthly = 12;
    public const int SafetyWindowDays = 7;
    public const long MinimumFreeSpaceBytes = 64L * 1024 * 1024;

    private const string Prefix = "focus-attendance_";
    private const string Suffix = ".db";
    private const string DailyTag = "_daily";
    private const string SafetyTag = "_safety";
    private const string PreMigrationTag = "_premigration";
    private const string LegacySlotTag = "_slot";
    private const string DateFormat = "yyyy-MM-dd";

    private const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS Backups (
            Id                  TEXT PRIMARY KEY NOT NULL,
            Kind                INTEGER NOT NULL,
            DailyKey            TEXT NULL,
            LocalDate           TEXT NOT NULL,
            CreatedUtc          TEXT NOT NULL,
            FileName            TEXT NOT NULL,
            SizeBytes           INTEGER NOT NULL,
            Sha256              TEXT NOT NULL,
            Status              INTEGER NOT NULL DEFAULT 0,
            IsRetentionAnchor   INTEGER NOT NULL DEFAULT 0,
            PrunedAtUtc         TEXT NULL,
            FailureCode         TEXT NULL,
            FailureMessage      TEXT NULL,
            CreatedBy           TEXT NULL,
            Note                TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS UX_Backups_FileName ON Backups(FileName);
        CREATE UNIQUE INDEX IF NOT EXISTS UX_Backups_DailyKey
            ON Backups(DailyKey) WHERE Kind = 0 AND Status IN (0, 1);
        CREATE INDEX IF NOT EXISTS IX_Backups_LocalDate ON Backups(LocalDate, CreatedUtc);
        CREATE INDEX IF NOT EXISTS IX_Backups_Status ON Backups(Status);
        """;

    private static readonly TimeZoneInfo Algiers = ResolveAlgiers();

    private readonly Database _database;
    private readonly Func<DateTime> _clock;
    private readonly Action<string, Exception> _logError;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _schemaReady;
    private bool _disposed;

    public BackupService(
        Database database,
        Func<DateTime>? clock = null,
        Action<string, Exception>? logError = null)
    {
        _database = database;
        _clock = clock ?? (() => DateTime.UtcNow);
        _logError = logError ?? ((message, exception) => Console.Error.WriteLine($"{message} {exception}"));
    }

    public string BackupDirectory => _database.BackupDirectory;

    /// <summary>Wall-clock time in Africa/Algiers derived from the injected clock.</summary>
    public DateTime LocalNow() => ReadClock().Local;

    /// <summary>
    /// One canonical daily snapshot per local date. <paramref name="force"/> replaces the same-day
    /// file atomically instead of returning <c>AlreadyExisted</c>.
    /// </summary>
    public BackupResult CreateDailyBackup(bool force = false) => CreateDailyBackup(force, "system");

    public BackupResult CreateDailyBackup(bool force, string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException("An actor is required.", nameof(actor));
        }

        _gate.Wait();
        try
        {
            var (local, _) = ReadClock();
            var localDate = local.Date;
            var dailyKey = localDate.ToString(DateFormat, CultureInfo.InvariantCulture);
            var fileName = Prefix + dailyKey + DailyTag + Suffix;
            var target = Path.Combine(_database.BackupDirectory, fileName);

            if (!File.Exists(_database.DatabasePath))
            {
                return Failure(BackupKind.Daily, "backup.database-missing",
                    "The live database file does not exist; no backup was written.");
            }

            var databaseSize = new FileInfo(_database.DatabasePath).Length;
            if (databaseSize == 0)
            {
                return Failure(BackupKind.Daily, "backup.database-empty",
                    "The live database file is empty; no backup was written.");
            }

            if (!TryEnsureSchema())
            {
                return Failure(BackupKind.Daily, "backup.database-unavailable",
                    "The backup metadata table is not reachable in the live database.");
            }

            var existing = FindDailyRow(dailyKey);
            var mismatch = false;
            if (!force && existing is { Status: BackupStatus.Present } && File.Exists(target))
            {
                var actual = ComputeSha256(target);
                if (string.Equals(actual, existing.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new BackupResult(
                        Success: true,
                        Kind: existing.Kind,
                        Path: target,
                        CreatedUtc: DateTime.SpecifyKind(existing.CreatedUtc, DateTimeKind.Utc),
                        SizeBytes: new FileInfo(target).Length,
                        Sha256: actual,
                        AlreadyExisted: true,
                        ErrorCode: null,
                        ErrorMessage: null);
                }

                mismatch = true;
            }
            else if (!force && existing is { Status: BackupStatus.Present })
            {
                mismatch = true;
            }

            var snapshot = CreateSnapshotLocked(BackupKind.Daily, fileName, note: null);
            if (!snapshot.Success)
            {
                return Failure(BackupKind.Daily, snapshot.ErrorCode!, snapshot.ErrorMessage!);
            }

            try
            {
                SaveDailyRow(existing, dailyKey, snapshot, actor);
            }
            catch (SqliteException ex)
            {
                // The payload file is kept on disk; startup reconciliation adopts it by hash.
                return Failure(BackupKind.Daily, "backup.metadata-write-failed",
                    $"The backup file was written but its metadata could not be recorded: {ex.Message}", snapshot);
            }

            return new BackupResult(
                Success: true,
                Kind: BackupKind.Daily,
                Path: snapshot.Path,
                CreatedUtc: snapshot.CreatedUtc,
                SizeBytes: snapshot.SizeBytes,
                Sha256: snapshot.Sha256,
                AlreadyExisted: false,
                ErrorCode: mismatch ? "backup.metadata-mismatch" : null,
                ErrorMessage: mismatch
                    ? "The existing daily file did not match its metadata; it was replaced and the metadata updated."
                    : null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Restore safety copy, written before the live database is swapped.</summary>
    public BackupResult CreateSafetyBackup(string reason) => CreateSafetyBackup(reason, "system");

    public BackupResult CreateSafetyBackup(string reason, string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException("An actor is required.", nameof(actor));
        }

        var note = string.IsNullOrWhiteSpace(reason) ? "before-restore" : reason.Trim();
        Directory.CreateDirectory(_database.BackupDirectory);

        _gate.Wait();
        try
        {
            if (!TryEnsureSchema())
            {
                return Failure(BackupKind.SafetyRestore, "backup.database-unavailable",
                    "The backup metadata table is not reachable in the live database.");
            }

            return CreateSafetyBackupLocked(note, actor);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Pre-migration copy; retained by the safety window and never treated as a daily.</summary>
    public BackupResult CreatePreMigrationBackup(string reason)
    {
        _gate.Wait();
        try
        {
            Directory.CreateDirectory(_database.BackupDirectory);
            if (!TryEnsureSchema())
            {
                return Failure(BackupKind.PreMigration, "backup.database-unavailable",
                    "The backup metadata table is not reachable in the live database.");
            }

            var (local, _) = ReadClock();
            var fileName = UniqueNameIfExists(_database.BackupDirectory, UniqueName(local, PreMigrationTag));
            if (fileName is null)
            {
                return Failure(BackupKind.PreMigration, "backup.name-exhausted",
                    "No free pre-migration file name is available in the backup folder.");
            }

            var snapshot = CreateSnapshotLocked(BackupKind.PreMigration, fileName, reason);
            if (!snapshot.Success)
            {
                return Failure(BackupKind.PreMigration, snapshot.ErrorCode!, snapshot.ErrorMessage!);
            }

            try
            {
                InsertRow(snapshot, BackupKind.PreMigration, dailyKey: null, reason ?? "pre-migration", "system");
            }
            catch (SqliteException ex)
            {
                return Failure(BackupKind.PreMigration, "backup.metadata-write-failed",
                    $"The backup file was written but its metadata could not be recorded: {ex.Message}", snapshot);
            }

            return new BackupResult(true, BackupKind.PreMigration, snapshot.Path, snapshot.CreatedUtc,
                snapshot.SizeBytes, snapshot.Sha256, false, null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Keeps the newest <paramref name="keepDays"/> daily copies, the newest copy of each of the last
    /// <paramref name="keepMonthly"/> calendar months, and safety/pre-migration copies younger than
    /// <see cref="SafetyWindowDays"/>. Only payload files are deleted; metadata rows become Pruned.
    /// </summary>
    public PruneResult PruneBackups(int keepDays = DefaultKeepDays, int keepMonthly = DefaultKeepMonthly)
    {
        if (keepDays < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(keepDays), keepDays, "At least one daily backup must be retained.");
        }

        if (keepMonthly < 0 || keepMonthly > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(keepMonthly), keepMonthly, "Monthly retention must be between 0 and 120.");
        }

        _gate.Wait();
        try
        {
            if (!TryEnsureSchema())
            {
                return new PruneResult(0, 0, [], []);
            }

            ReconcileLocked();

            var (local, utc) = ReadClock();
            var today = local.Date;
            var todayKey = today.ToString(DateFormat, CultureInfo.InvariantCulture);
            var records = LoadRecordsLocked();
            var present = records.Where(r => r.Status == BackupStatus.Present).ToList();

            // Prune only after today's daily backup succeeded (file present and recorded).
            if (!present.Any(r => r.Kind == BackupKind.Daily && string.Equals(r.DailyKey, todayKey, StringComparison.Ordinal)))
            {
                return new PruneResult(0, 0, present.Select(r => r.FileName).ToList(), []);
            }

            var retain = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in present
                         .Where(r => r.Kind == BackupKind.Daily)
                         .OrderByDescending(r => r.CreatedUtc)
                         .Take(keepDays))
            {
                retain.Add(row.Id);
            }

            var monthCursor = new DateTime(today.Year, today.Month, 1);
            for (var index = 0; index < keepMonthly; index++)
            {
                var month = monthCursor.AddMonths(-index);
                var anchor = present
                    .Where(r => r.Kind == BackupKind.Daily && InMonth(r.LocalDate, month))
                    .OrderByDescending(r => r.CreatedUtc)
                    .FirstOrDefault();
                if (anchor is not null)
                {
                    retain.Add(anchor.Id);
                }
            }

            var safetyCutoff = today.AddDays(-SafetyWindowDays);
            foreach (var row in present.Where(r =>
                         (r.Kind is BackupKind.SafetyRestore or BackupKind.PreMigration) &&
                         ParseLocalDate(r.LocalDate) is { } date &&
                         date > safetyCutoff))
            {
                retain.Add(row.Id);
            }

            var deletable = present.Where(r => !retain.Contains(r.Id)).ToList();
            if (deletable.Count > 0 && deletable.Count == present.Count)
            {
                // Never delete the last remaining Present backup in a single run.
                var keep = deletable.OrderByDescending(r => r.CreatedUtc).First();
                deletable.Remove(keep);
                retain.Add(keep.Id);
            }

            UpdateAnchorsLocked(present, retain);

            var deleted = 0;
            long bytesFreed = 0;
            var failed = new List<string>();
            foreach (var row in deletable)
            {
                var path = Path.Combine(_database.BackupDirectory, row.FileName);
                try
                {
                    if (File.Exists(path))
                    {
                        var size = new FileInfo(path).Length;
                        File.Delete(path);
                        bytesFreed += size;
                    }

                    deleted++;
                }
                catch (IOException)
                {
                    // Locked by a reader: left in place, reported, retried on the next run.
                    failed.Add(row.FileName);
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    // Same: permissions blocked the delete; the file stays Present.
                    failed.Add(row.FileName);
                    continue;
                }

                try
                {
                    using var connection = _database.Open();
                    connection.Execute(
                        """
                        UPDATE Backups
                           SET Status = @status, PrunedAtUtc = @prunedAtUtc, IsRetentionAnchor = 0
                         WHERE Id = @id;
                        """,
                        new { status = (int)BackupStatus.Pruned, prunedAtUtc = utc, id = row.Id });
                }
                catch (SqliteException ex)
                {
                    // The payload is gone: its metadata must never stay Present.
                    LogError($"Backup payload '{row.FileName}' was deleted but its row could not be marked Pruned.", ex);
                    if (!TryMarkPrunedMissingLocked(row.Id, utc))
                    {
                        failed.Add(row.FileName);
                    }
                }
            }

            try
            {
                using var connection = _database.Open();
                InsertAudit(connection, null, "system", "backup.pruned", null,
                    $"deleted={deleted}; bytes={bytesFreed}; failed={failed.Count}; kept={retain.Count}");
            }
            catch (SqliteException)
            {
                failed.Add("audit:backup.pruned");
            }

            var retainedFiles = present
                .Where(r => retain.Contains(r.Id))
                .OrderByDescending(r => r.CreatedUtc)
                .Select(r => r.FileName)
                .ToList();

            return new PruneResult(deleted, bytesFreed, retainedFiles, failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Restore with a forced safety copy, source hash verification, temp integrity check and rollback.
    /// The live database is never replaced by an unverified candidate.
    /// </summary>
    public RestoreResult Restore(string backupPath, string actor)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new ArgumentException("A backup path is required.", nameof(backupPath));
        }

        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException("An actor is required.", nameof(actor));
        }

        _gate.Wait();
        try
        {
            string source;
            try
            {
                source = Path.GetFullPath(backupPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                LogError($"Restore path '{backupPath}' could not be resolved.", ex);
                RecordRestoreAudit(actor, backupPath, null, "restore.io-failed",
                    $"The backup path is invalid: {ex.Message}");
                return new RestoreResult(false, backupPath, null, false, "restore.io-failed",
                    "The selected backup path is not a valid file path.");
            }

            // The safety copy is written before any source or hash validation, so every restore
            // attempt leaves a recoverable copy of the live database alongside its audit row.
            var safety = CreateSafetyBackupLocked("before-restore", actor);
            if (!safety.Success)
            {
                var failureMessage = $"The safety copy failed ({safety.ErrorCode}); the live database was not touched.";
                RecordRestoreAudit(actor, source, null, "restore.safety-failed", failureMessage);
                return new RestoreResult(false, source, null, false, "restore.safety-failed", failureMessage);
            }

            if (!File.Exists(source))
            {
                const string code = "restore.source-missing";
                const string message = "The selected backup file does not exist.";
                RecordRestoreAudit(actor, source, safety.Path, code, message);
                return new RestoreResult(false, source, safety.Path, false, code, message);
            }

            if (string.Equals(source, _database.DatabasePath, StringComparison.Ordinal))
            {
                const string code = "restore.source-is-live";
                const string message = "The live database cannot be restored over itself.";
                RecordRestoreAudit(actor, source, safety.Path, code, message);
                return new RestoreResult(false, source, safety.Path, false, code, message);
            }

            BackupRecord? metadata;
            try
            {
                metadata = TryEnsureSchema()
                    ? LoadRecordsLocked().FirstOrDefault(r => string.Equals(r.FileName, Path.GetFileName(source), StringComparison.Ordinal))
                    : null;
            }
            catch (SqliteException ex)
            {
                LogError("Restore metadata could not be read.", ex);
                var metadataMessage = $"The backup metadata could not be read: {ex.Message}";
                RecordRestoreAudit(actor, source, safety.Path, "restore.io-failed", metadataMessage);
                return new RestoreResult(false, source, safety.Path, false, "restore.io-failed", metadataMessage);
            }

            if (metadata is not null && !string.IsNullOrWhiteSpace(metadata.Sha256))
            {
                string actual;
                try
                {
                    actual = ComputeSha256(source);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogError("Restore source could not be hashed.", ex);
                    var hashMessage = $"The selected backup file could not be read: {ex.Message}";
                    RecordRestoreAudit(actor, source, safety.Path, "restore.io-failed", hashMessage);
                    return new RestoreResult(false, source, safety.Path, false, "restore.io-failed", hashMessage);
                }

                if (!string.Equals(actual, metadata.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    const string code = "restore.hash-mismatch";
                    const string message = "The selected file does not match the hash recorded at backup time.";
                    RecordRestoreAudit(actor, source, safety.Path, code, message);
                    return new RestoreResult(false, source, safety.Path, false, code, message);
                }
            }

            var temp = TempPath(Path.GetFileName(source));
            try
            {
                File.Copy(source, temp, overwrite: true);
                var (checkOk, checkMessage) = QuickCheckFile(temp);
                if (!checkOk)
                {
                    var message = $"The selected file failed its integrity check: {checkMessage}";
                    RecordRestoreAudit(actor, source, safety.Path, "restore.integrity-check-failed", message);
                    return new RestoreResult(false, source, safety.Path, false, "restore.integrity-check-failed", message);
                }

                SqliteConnection.ClearAllPools();
                File.Copy(temp, _database.DatabasePath, overwrite: true);
                DeleteIfExists(_database.DatabasePath + "-wal");
                DeleteIfExists(_database.DatabasePath + "-shm");

                var postSwap = VerifyLiveLocked();
                _schemaReady = false;
                if (!postSwap.Ok)
                {
                    SqliteConnection.ClearAllPools();
                    try
                    {
                        File.Copy(safety.Path!, _database.DatabasePath, overwrite: true);
                        DeleteIfExists(_database.DatabasePath + "-wal");
                        DeleteIfExists(_database.DatabasePath + "-shm");
                        _database.Initialize();
                        var rolledBack = VerifyLiveLocked();
                        _schemaReady = false;
                        if (!rolledBack.Ok)
                        {
                            var rollbackMessage =
                                $"Rollback restored the safety copy but it failed verification: {rolledBack.Message}";
                            RecordRestoreAudit(actor, source, safety.Path, "restore.rollback-failed", rollbackMessage);
                            return new RestoreResult(false, source, safety.Path, true, "restore.rollback-failed",
                                rollbackMessage);
                        }
                    }
                    catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        LogError("Restore rollback could not complete.", ex);
                        var rollbackMessage =
                            $"Rollback could not complete; the safety file is kept at {safety.Path}: {ex.Message}";
                        RecordRestoreAudit(actor, source, safety.Path, "restore.rollback-failed", rollbackMessage);
                        return new RestoreResult(false, source, safety.Path, true, "restore.rollback-failed",
                            rollbackMessage);
                    }

                    var reconcile = ReconcileAfterRestoreLocked();
                    var message = $"The restored file failed its post-swap check ({postSwap.Message}); the safety copy was restored."
                        + (reconcile.Message is null ? string.Empty : $" Metadata reconciliation warning: {reconcile.Message}");
                    RecordRestoreAudit(actor, source, safety.Path, "rollback-completed", message);
                    return new RestoreResult(false, source, safety.Path, true, "restore.rollback-completed", message);
                }

                var postRestore = ReconcileAfterRestoreLocked();
                if (postRestore.Code is not null)
                {
                    RecordRestoreAudit(actor, source, safety.Path, postRestore.Code, postRestore.Message);
                    return new RestoreResult(false, source, safety.Path, false, postRestore.Code,
                        $"{postRestore.Message} The database file itself was replaced with the selected backup.");
                }

                RecordRestoreAudit(actor, source, safety.Path, "completed", null);
                return new RestoreResult(true, source, safety.Path, false, null, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                LogError("Restore could not replace the live database.", ex);
                var message = $"The database could not be replaced: {ex.Message}";
                RecordRestoreAudit(actor, source, safety.Path, "restore.io-failed", message);
                return new RestoreResult(false, source, safety.Path, false, "restore.io-failed", message);
            }
            finally
            {
                DeleteIfExists(temp);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Metadata rows, newest first. Rows are never deleted; pruned payloads keep their row.</summary>
    public IReadOnlyList<BackupRecord> ListBackupRecords(BackupStatus? status = null)
    {
        _gate.Wait();
        try
        {
            if (!TryEnsureSchema())
            {
                return [];
            }

            var records = LoadRecordsLocked();
            var filtered = status is null ? records : records.Where(r => r.Status == status).ToList();
            return filtered.Select(WithFullPath).OrderByDescending(r => r.CreatedUtc).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Legacy filesystem view of payload files (existing files only).</summary>
    public IReadOnlyList<BackupFileInfo> ListBackups()
    {
        if (!Directory.Exists(_database.BackupDirectory))
        {
            return [];
        }

        return new DirectoryInfo(_database.BackupDirectory)
            .GetFiles(Prefix + "*" + Suffix)
            .Where(f => ParseDate(f.Name) is not null)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new BackupFileInfo(f.FullName, f.LastWriteTimeUtc, f.Length))
            .ToList();
    }

    public IReadOnlyList<BackupFileInfo> GetBackupsForDate(DateTime date) =>
        ListBackups().Where(f => ParseDate(f.FileName) == date.Date).ToList();

    /// <summary>
    /// Legacy entry point kept for callers that only want "a verified copy now".
    /// Startup/pre-migration labels create a PreMigration copy; every other label forces today's daily file.
    /// </summary>
    public BackupFileInfo CreateBackup(string label = "scheduled")
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("A label is required.", nameof(label));
        }

        var isPreMigration = label.Contains("startup", StringComparison.OrdinalIgnoreCase)
            || label.Contains("migration", StringComparison.OrdinalIgnoreCase);

        var result = isPreMigration ? CreatePreMigrationBackup(label) : CreateDailyBackup(force: true);
        if (!result.Success || result.Path is null)
        {
            throw new InvalidOperationException($"{result.ErrorCode}: {result.ErrorMessage}");
        }

        return new BackupFileInfo(result.Path, result.CreatedUtc, result.SizeBytes);
    }

    internal static DateTime? ParseDate(string fileName)
    {
        var name = System.IO.Path.GetFileName(fileName);
        if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = name[Prefix.Length..];
        var datePart = rest.Split('_')[0];
        return DateTime.TryParseExact(datePart, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.Date
            : null;
    }

    private BackupResult CreateSafetyBackupLocked(string note, string actor)
    {
        if (!File.Exists(_database.DatabasePath))
        {
            return Failure(BackupKind.SafetyRestore, "backup.database-missing",
                "The live database file does not exist; no safety copy was written.");
        }

        var (local, _) = ReadClock();
        var fileName = UniqueNameIfExists(_database.BackupDirectory, UniqueName(local, SafetyTag));
        if (fileName is null)
        {
            return Failure(BackupKind.SafetyRestore, "backup.name-exhausted",
                "No free safety-copy file name is available in the backup folder.");
        }

        var snapshot = CreateSnapshotLocked(BackupKind.SafetyRestore, fileName, note);
        if (!snapshot.Success)
        {
            return Failure(BackupKind.SafetyRestore, snapshot.ErrorCode!, snapshot.ErrorMessage!);
        }

        try
        {
            InsertRow(snapshot, BackupKind.SafetyRestore, dailyKey: null, note, actor);
        }
        catch (SqliteException ex)
        {
            return Failure(BackupKind.SafetyRestore, "backup.metadata-write-failed",
                $"The safety copy was written but its metadata could not be recorded: {ex.Message}", snapshot);
        }

        return new BackupResult(true, BackupKind.SafetyRestore, snapshot.Path, snapshot.CreatedUtc,
            snapshot.SizeBytes, snapshot.Sha256, false, null, null);
    }

    /// <summary>
    /// VACUUM INTO a temp file, quick_check it, hash it, flush it, then rename it into place.
    /// The temp file never matches the payload glob, so a crash cannot expose a half copy.
    /// </summary>
    private SnapshotOutcome CreateSnapshotLocked(BackupKind kind, string fileName, string? note)
    {
        if (!File.Exists(_database.DatabasePath))
        {
            return SnapshotOutcome.Error("backup.database-missing", "The live database file does not exist.");
        }

        var databaseSize = new FileInfo(_database.DatabasePath).Length;
        if (databaseSize == 0)
        {
            return SnapshotOutcome.Error("backup.database-empty", "The live database file is empty.");
        }

        // Snapshot callers write a metadata row immediately afterwards; a legacy database that predates
        // the Backups table must be upgraded here or the payload would survive untracked.
        if (!TryEnsureSchema())
        {
            return SnapshotOutcome.Error("backup.database-unavailable",
                "The backup metadata table is not reachable in the live database.");
        }

        Directory.CreateDirectory(_database.BackupDirectory);
        if (!HasFreeSpace(_database.BackupDirectory, databaseSize + MinimumFreeSpaceBytes))
        {
            return SnapshotOutcome.Error("backup.disk-full",
                "Free disk space is below the database size plus the 64 MiB safety margin.");
        }

        var target = Path.Combine(_database.BackupDirectory, fileName);
        var temp = TempPath(fileName);
        try
        {
            VacuumInto(temp);

            var info = new FileInfo(temp);
            if (!info.Exists || info.Length == 0)
            {
                return SnapshotOutcome.Error("backup.empty-copy", "VACUUM INTO produced an empty file; the copy was rejected.");
            }

            var (checkOk, checkMessage) = QuickCheckFile(temp);
            if (!checkOk)
            {
                return SnapshotOutcome.Error("backup.integrity-check-failed",
                    $"The copy failed PRAGMA quick_check: {checkMessage}");
            }

            FlushToDisk(temp);
            var sha256 = ComputeSha256(temp);
            var (local, utc) = ReadClock();

            // Same volume rename: atomic replacement of a same-day file when forced.
            File.Move(temp, target, overwrite: true);

            return new SnapshotOutcome(
                Success: true,
                Path: target,
                SizeBytes: new FileInfo(target).Length,
                Sha256: sha256,
                CreatedUtc: utc,
                LocalDate: local.Date.ToString(DateFormat, CultureInfo.InvariantCulture),
                Note: note);
        }
        catch (SqliteException ex)
        {
            return SnapshotOutcome.Error("backup.vacuum-failed", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return SnapshotOutcome.Error("backup.access-denied", ex.Message);
        }
        catch (IOException ex)
        {
            return SnapshotOutcome.Error("backup.io-failed", ex.Message);
        }
        finally
        {
            DeleteIfExists(temp);
        }
    }

    /// <summary>
    /// Aligns metadata with the payload folder. Normal pruning only checks file presence; a restore
    /// additionally verifies size/SHA-256 and re-adopts rows whose payload exists again, because the
    /// restored snapshot predates the files written since it was taken.
    /// </summary>
    private void ReconcileLocked(bool verifyPayloads = false)
    {
        var records = LoadRecordsLocked();
        var byName = records.ToDictionary(r => r.FileName, StringComparer.OrdinalIgnoreCase);
        var usedKeys = records
            .Where(r => r.Kind == BackupKind.Daily && r.DailyKey is not null && r.Status is BackupStatus.Present or BackupStatus.Pruned)
            .Select(r => r.DailyKey!)
            .ToHashSet(StringComparer.Ordinal);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var row in records)
        {
            var path = Path.Combine(_database.BackupDirectory, row.FileName);
            var exists = File.Exists(path);

            if (row.Status == BackupStatus.Present)
            {
                if (!exists)
                {
                    connection.Execute(
                        """
                        UPDATE Backups
                           SET Status = @status, FailureCode = @code,
                               FailureMessage = 'Payload file was not found during reconciliation.'
                         WHERE Id = @id;
                        """,
                        new { status = (int)BackupStatus.Missing, code = "backup.file-missing", id = row.Id },
                        transaction);
                    continue;
                }

                if (!verifyPayloads)
                {
                    continue;
                }

                var actual = new FileInfo(path);
                var sha256 = ComputeSha256(path);
                if (actual.Length != row.SizeBytes || !string.Equals(sha256, row.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    connection.Execute(
                        """
                        UPDATE Backups
                           SET SizeBytes = @sizeBytes, Sha256 = @sha256
                         WHERE Id = @id;
                        """,
                        new { sizeBytes = actual.Length, sha256, id = row.Id },
                        transaction);
                }

                continue;
            }

            if (!verifyPayloads || !exists || row.Status is not (BackupStatus.Missing or BackupStatus.Failed))
            {
                continue;
            }

            // The payload is on disk again (restored snapshot): promote the row with its real hash/size.
            string? dailyKey = row.DailyKey;
            if (row.Kind == BackupKind.Daily)
            {
                dailyKey ??= ParseDate(row.FileName)?.ToString(DateFormat, CultureInfo.InvariantCulture);
                if (dailyKey is not null && !usedKeys.Add(dailyKey))
                {
                    // Another Present/Pruned row already owns this date; keep the unique key intact.
                    continue;
                }
            }

            var info = new FileInfo(path);
            connection.Execute(
                """
                UPDATE Backups
                   SET Status = @status, DailyKey = @dailyKey, SizeBytes = @sizeBytes, Sha256 = @sha256,
                       FailureCode = NULL, FailureMessage = NULL, PrunedAtUtc = NULL
                 WHERE Id = @id;
                """,
                new
                {
                    status = (int)BackupStatus.Present,
                    dailyKey,
                    sizeBytes = info.Length,
                    sha256 = ComputeSha256(path),
                    id = row.Id
                },
                transaction);
        }

        if (Directory.Exists(_database.BackupDirectory))
        {
            foreach (var file in Directory.GetFiles(_database.BackupDirectory, Prefix + "*" + Suffix)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                if (byName.ContainsKey(name) || ParseDate(name) is null)
                {
                    continue;
                }

                var kind = KindFromName(name);
                var localDate = ParseDate(name)!.Value.Date;
                var localDateText = localDate.ToString(DateFormat, CultureInfo.InvariantCulture);
                string? dailyKey = null;
                if (kind == BackupKind.Daily && usedKeys.Add(localDateText))
                {
                    dailyKey = localDateText;
                }

                var info = new FileInfo(file);
                connection.Execute(
                    """
                    INSERT INTO Backups (Id, Kind, DailyKey, LocalDate, CreatedUtc, FileName, SizeBytes, Sha256,
                                         Status, IsRetentionAnchor, CreatedBy, Note)
                    VALUES (@id, @kind, @dailyKey, @localDate, @createdUtc, @fileName, @sizeBytes, @sha256,
                            @status, 0, 'reconcile', 'Adopted from the backup folder');
                    """,
                    new
                    {
                        id = Guid.NewGuid().ToString("N"),
                        kind = (int)kind,
                        dailyKey,
                        localDate = localDateText,
                        createdUtc = info.LastWriteTimeUtc,
                        fileName = name,
                        sizeBytes = info.Length,
                        sha256 = ComputeSha256(file),
                        status = (int)BackupStatus.Present
                    },
                    transaction);
            }
        }

        transaction.Commit();
    }

    private void UpdateAnchorsLocked(IReadOnlyList<BackupRecord> present, HashSet<string> retained)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var row in present)
        {
            var anchor = retained.Contains(row.Id) ? 1 : 0;
            if (row.IsRetentionAnchor == (anchor == 1))
            {
                continue;
            }

            connection.Execute(
                "UPDATE Backups SET IsRetentionAnchor = @anchor WHERE Id = @id;",
                new { anchor, id = row.Id },
                transaction);
        }

        transaction.Commit();
    }

    private List<BackupRecord> LoadRecordsLocked()
    {
        using var connection = _database.Open();
        return connection.Query<BackupRecord>(
            """
            SELECT Id, Kind, DailyKey, LocalDate, CreatedUtc, FileName, SizeBytes, Sha256,
                   Status, IsRetentionAnchor, PrunedAtUtc, FailureCode, FailureMessage, CreatedBy, Note
              FROM Backups;
            """).ToList();
    }

    private BackupRecord? FindDailyRow(string dailyKey)
    {
        using var connection = _database.Open();
        return connection.QuerySingleOrDefault<BackupRecord>(
            """
            SELECT * FROM Backups
             WHERE Kind = 0 AND DailyKey = @dailyKey
             ORDER BY CreatedUtc DESC
             LIMIT 1;
            """,
            new { dailyKey });
    }

    private void SaveDailyRow(BackupRecord? existing, string dailyKey, SnapshotOutcome snapshot, string actor)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        if (existing is null)
        {
            var id = Guid.NewGuid().ToString("N");
            connection.Execute(
                """
                INSERT INTO Backups (Id, Kind, DailyKey, LocalDate, CreatedUtc, FileName, SizeBytes, Sha256,
                                     Status, IsRetentionAnchor, CreatedBy)
                VALUES (@id, 0, @dailyKey, @localDate, @createdUtc, @fileName, @sizeBytes, @sha256, 0, 0, @actor);
                """,
                new
                {
                    id,
                    dailyKey,
                    localDate = snapshot.LocalDate,
                    createdUtc = snapshot.CreatedUtc,
                    fileName = Path.GetFileName(snapshot.Path!),
                    sizeBytes = snapshot.SizeBytes,
                    sha256 = snapshot.Sha256,
                    actor
                },
                transaction);
            InsertAudit(connection, transaction, actor, "backup.created", id,
                $"kind=Daily; date={dailyKey}; file={Path.GetFileName(snapshot.Path!)}; sha256={snapshot.Sha256}");
        }
        else
        {
            connection.Execute(
                """
                UPDATE Backups
                   SET Kind = 0, DailyKey = @dailyKey, LocalDate = @localDate, CreatedUtc = @createdUtc,
                       FileName = @fileName, SizeBytes = @sizeBytes, Sha256 = @sha256, Status = 0,
                       IsRetentionAnchor = 0, PrunedAtUtc = NULL, FailureCode = NULL, FailureMessage = NULL,
                       CreatedBy = @actor, Note = @note
                 WHERE Id = @id;
                """,
                new
                {
                    dailyKey,
                    localDate = snapshot.LocalDate,
                    createdUtc = snapshot.CreatedUtc,
                    fileName = Path.GetFileName(snapshot.Path!),
                    sizeBytes = snapshot.SizeBytes,
                    sha256 = snapshot.Sha256,
                    actor,
                    note = snapshot.Note,
                    id = existing.Id
                },
                transaction);
            InsertAudit(connection, transaction, actor, "backup.created", existing.Id,
                $"kind=Daily; date={dailyKey}; file={Path.GetFileName(snapshot.Path!)}; sha256={snapshot.Sha256}; replaced=1");
        }

        transaction.Commit();
    }

    private void InsertRow(SnapshotOutcome snapshot, BackupKind kind, string? dailyKey, string? note, string actor)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var id = Guid.NewGuid().ToString("N");
        connection.Execute(
            """
            INSERT INTO Backups (Id, Kind, DailyKey, LocalDate, CreatedUtc, FileName, SizeBytes, Sha256,
                                 Status, IsRetentionAnchor, CreatedBy, Note)
            VALUES (@id, @kind, @dailyKey, @localDate, @createdUtc, @fileName, @sizeBytes, @sha256, 0, 0, @actor, @note);
            """,
            new
            {
                id,
                kind = (int)kind,
                dailyKey,
                localDate = snapshot.LocalDate,
                createdUtc = snapshot.CreatedUtc,
                fileName = Path.GetFileName(snapshot.Path!),
                sizeBytes = snapshot.SizeBytes,
                sha256 = snapshot.Sha256,
                actor,
                note
            },
            transaction);

        InsertAudit(connection, transaction, actor,
            kind == BackupKind.SafetyRestore ? "backup.safety-created" : "backup.created", id,
            $"kind={kind}; file={Path.GetFileName(snapshot.Path!)}; sha256={snapshot.Sha256}; note={note}");
        transaction.Commit();
    }

    /// <summary>
    /// Adopts the payload folder into the metadata of the freshly restored database (the restored
    /// snapshot predates the safety copy and any daily file written after it was taken).
    /// </summary>
    private (string? Code, string? Message) ReconcileAfterRestoreLocked()
    {
        try
        {
            if (!TryEnsureSchema())
            {
                return ("restore.metadata-write-failed",
                    "The restored database has no reachable backup metadata table; the safety copy is on disk but could not be listed.");
            }

            ReconcileLocked(verifyPayloads: true);
            return (null, null);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return ("restore.metadata-write-failed",
                $"Backup metadata could not be reconciled with the payload folder: {ex.Message}");
        }
    }

    private bool TryMarkPrunedMissingLocked(string id, DateTime prunedAtUtc)
    {
        try
        {
            using var connection = _database.Open();
            connection.Execute(
                """
                UPDATE Backups
                   SET Status = @status, PrunedAtUtc = @prunedAtUtc, IsRetentionAnchor = 0,
                       FailureCode = 'backup.prune-metadata-failed',
                       FailureMessage = 'The payload was deleted but the row could not be marked Pruned.'
                 WHERE Id = @id;
                """,
                new { status = (int)BackupStatus.Missing, prunedAtUtc, id });
            return true;
        }
        catch (SqliteException ex)
        {
            LogError("A pruned backup row could not be marked Missing after its payload was deleted.", ex);
            return false;
        }
    }

    private void RecordRestoreAudit(string actor, string source, string? safety, string result, string? detail)
    {
        try
        {
            if (!TryEnsureSchema())
            {
                LogError($"Restore audit skipped ({result}): the backup metadata table is unreachable.",
                    new InvalidOperationException("Backups schema unavailable."));
                return;
            }

            var (_, utc) = ReadClock();
            using var connection = _database.Open();
            InsertAudit(connection, null, actor, "backup.restored", null,
                $"source={source}; safety={safety ?? "none"}; result={result}; detail={detail ?? "none"}", utc);
        }
        catch (SqliteException ex)
        {
            LogError($"Restore audit could not be written for result '{result}'.", ex);
        }
    }

    private static void InsertAudit(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string actor,
        string action,
        string? entityId,
        string details,
        DateTime? timestampUtc = null)
    {
        connection.Execute(
            """
            INSERT INTO AuditLogs (TimestampUtc, Actor, Action, EntityType, EntityId, Details)
            VALUES (@timestampUtc, @actor, @action, 'Backups', @entityId, @details);
            """,
            new { timestampUtc = timestampUtc ?? DateTime.UtcNow, actor, action, entityId, details },
            transaction);
    }

    private void LogError(string message, Exception exception) => _logError(message, exception);

    private (bool Ok, string Message) VerifyLiveLocked()
    {
        try
        {
            _database.Initialize();
            return _database.IsHealthy(out var message) ? (true, message) : (false, message);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (false, ex.Message);
        }
    }

    private bool TryEnsureSchema()
    {
        if (_schemaReady)
        {
            return true;
        }

        if (!File.Exists(_database.DatabasePath) || new FileInfo(_database.DatabasePath).Length == 0)
        {
            return false;
        }

        try
        {
            using var connection = _database.Open();
            connection.Execute(SchemaSql);
            _schemaReady = true;
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private void VacuumInto(string targetPath)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = $"VACUUM INTO '{Escape(targetPath)}';";
        command.ExecuteNonQuery();
    }

    private static (bool Ok, string Message) QuickCheckFile(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var result = command.ExecuteScalar() as string;
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase)
                ? (true, result ?? "ok")
                : (false, result ?? "unknown");
        }
        catch (SqliteException ex)
        {
            return (false, ex.Message);
        }
    }

    private static void FlushToDisk(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool HasFreeSpace(string directory, long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root))
            {
                return true;
            }

            var drive = new DriveInfo(root);
            return !drive.IsReady || drive.AvailableFreeSpace >= requiredBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The volume cannot be probed (network/removable path); the copy itself will report failures.
            return true;
        }
    }

    private static bool InMonth(string localDate, DateTime month)
    {
        var date = ParseLocalDate(localDate);
        return date is not null && date.Value.Year == month.Year && date.Value.Month == month.Month;
    }

    private static DateTime? ParseLocalDate(string localDate) =>
        DateTime.TryParseExact(localDate, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private BackupRecord WithFullPath(BackupRecord record) => record with
    {
        FullPath = Path.Combine(_database.BackupDirectory, record.FileName)
    };

    private string TempPath(string fileName) =>
        Path.Combine(_database.BackupDirectory, $".{fileName}.{Guid.NewGuid():N}.tmp");

    private static BackupKind KindFromName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (name.Contains(LegacySlotTag, StringComparison.OrdinalIgnoreCase))
        {
            return BackupKind.Daily;
        }

        if (name.EndsWith(SafetyTag, StringComparison.OrdinalIgnoreCase))
        {
            return BackupKind.SafetyRestore;
        }

        if (name.EndsWith(PreMigrationTag, StringComparison.OrdinalIgnoreCase))
        {
            return BackupKind.PreMigration;
        }

        return name.EndsWith(DailyTag, StringComparison.OrdinalIgnoreCase) ? BackupKind.Daily : BackupKind.Manual;
    }

    private static string UniqueName(DateTime local, string tag)
    {
        var stamp = local.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        return Prefix + stamp + tag + Suffix;
    }

    private static string? UniqueNameIfExists(string directory, string fileName)
    {
        if (!File.Exists(Path.Combine(directory, fileName)))
        {
            return fileName;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; index < 1000; index++)
        {
            var candidate = $"{stem}-{index}{extension}";
            if (!File.Exists(Path.Combine(directory, candidate)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A locked temp/WAL file is harmless; the next run retries.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: best effort cleanup.
        }
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private BackupResult Failure(BackupKind kind, string code, string message, SnapshotOutcome? snapshot = null) =>
        new(false, kind, snapshot?.Path, snapshot?.CreatedUtc ?? ReadClock().Utc,
            snapshot?.SizeBytes ?? 0, snapshot?.Sha256, false, code, message);

    private (DateTime Local, DateTime Utc) ReadClock()
    {
        var value = _clock();
        return value.Kind switch
        {
            DateTimeKind.Utc => (TimeZoneInfo.ConvertTimeFromUtc(value, Algiers), value),
            DateTimeKind.Local => (TimeZoneInfo.ConvertTimeFromUtc(value.ToUniversalTime(), Algiers), value.ToUniversalTime()),
            _ => (value, TimeZoneInfo.ConvertTimeToUtc(value, Algiers))
        };
    }

    private static TimeZoneInfo ResolveAlgiers()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Africa/Algiers");
        }
        catch (TimeZoneNotFoundException)
        {
            // Fall through to the fixed offset: Algeria has no DST since 1981.
        }
        catch (InvalidTimeZoneException)
        {
            // Same: an unusable tz database entry must not stop backups.
        }

        return TimeZoneInfo.CreateCustomTimeZone("Africa/Algiers", TimeSpan.FromHours(1), "Africa/Algiers", "Africa/Algiers");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private sealed record SnapshotOutcome(
        bool Success,
        string? Path,
        long SizeBytes,
        string? Sha256,
        DateTime CreatedUtc,
        string LocalDate,
        string? Note,
        string? ErrorCode = null,
        string? ErrorMessage = null)
    {
        public static SnapshotOutcome Error(string code, string message) =>
            new(false, null, 0, null, DateTime.UtcNow, string.Empty, null, code, message);
    }
}
