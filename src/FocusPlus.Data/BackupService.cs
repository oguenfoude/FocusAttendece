using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FocusPlus.Data;

public sealed record BackupFileInfo(string Path, DateTime CreatedUtc, long SizeBytes)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public double SizeMb => Math.Round(SizeBytes / 1024d / 1024d, 2);
}

/// <summary>
/// Strict rolling backup lifecycle: up to six slots for the current day
/// (<c>focus-attendance_yyyy-MM-dd_slotNN.db</c>) and automatic removal of every
/// backup older than today. A full day overwrites its oldest slot.
/// </summary>
public sealed class BackupService
{
    public const int SlotsPerDay = 6;
    private const string Prefix = "focus-attendance_";
    private const string Suffix = ".db";

    private readonly Database _database;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();

    public BackupService(Database database, Func<DateTime>? clock = null)
    {
        _database = database;
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>Creates the next free slot of today (or overwrites today's oldest one).</summary>
    public BackupFileInfo CreateBackup(string label = "scheduled")
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_database.BackupDirectory);
            var today = _clock().Date;
            var slots = GetBackupsForDate(today);

            int slot;
            if (slots.Count < SlotsPerDay)
            {
                var used = slots.Select(s => SlotOf(s.Path)).Where(s => s > 0).ToHashSet();
                slot = Enumerable.Range(1, SlotsPerDay).First(s => !used.Contains(s));
            }
            else
            {
                // All six slots exist: recycle the oldest file of today.
                var oldest = slots.OrderBy(s => s.CreatedUtc).First();
                slot = SlotOf(oldest.Path);
                TryDelete(oldest.Path);
            }

            var target = Path.Combine(_database.BackupDirectory, $"{Prefix}{today:yyyy-MM-dd}_slot{slot:00}{Suffix}");

            // VACUUM INTO refuses an existing target: remove it first when recycling.
            TryDelete(target);

            using (var connection = _database.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"VACUUM INTO '{target.Replace("'", "''")}';";
                command.ExecuteNonQuery();
            }

            var info = new FileInfo(target);
            if (!info.Exists || info.Length == 0)
            {
                throw new InvalidOperationException("The backup file is empty; the copy was not accepted.");
            }

            DeleteOlderThan(today);

            return new BackupFileInfo(target, info.LastWriteTimeUtc, info.Length);
        }
    }

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

    public int TodaySlotCount => GetBackupsForDate(_clock().Date).Count;

    public BackupFileInfo? LatestForToday() => GetBackupsForDate(_clock().Date)
        .OrderByDescending(f => f.CreatedUtc)
        .FirstOrDefault();

    /// <summary>Deletes every backup whose encoded date is strictly before <paramref name="date"/>.</summary>
    public int DeleteOlderThan(DateTime date)
    {
        var removed = 0;
        foreach (var file in ListBackups())
        {
            var encoded = ParseDate(file.FileName);
            if (encoded is not null && encoded.Value.Date < date.Date && TryDelete(file.Path))
            {
                removed++;
            }
        }

        return removed;
    }

    public BackupFileInfo Restore(string backupPath)
    {
        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException("Backup file not found.", backupPath);
        }

        var safety = CreateBackup("before-restore");
        SqliteConnection.ClearAllPools();

        File.Copy(backupPath, _database.DatabasePath, true);
        TryDelete(_database.DatabasePath + "-wal");
        TryDelete(_database.DatabasePath + "-shm");

        if (!_database.IsHealthy(out var message))
        {
            throw new InvalidOperationException($"Restored database failed its integrity check: {message}");
        }

        _database.Initialize();
        return safety;
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
        return DateTime.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.Date
            : null;
    }

    private static int SlotOf(string fileName)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var marker = name.LastIndexOf("_slot", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 && int.TryParse(name[(marker + 5)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot)
            ? slot
            : 0;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (IOException)
        {
            // Locked by a reader: the next rotation will remove it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: best effort.
        }

        return false;
    }
}
