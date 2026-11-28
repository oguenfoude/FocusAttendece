using System.Globalization;
using FocusPlus.Data;

namespace FocusPlus.Application.Services;

/// <summary>Outcome of one scheduled backup tick (daily copy plus retention).</summary>
public sealed record BackupRunReport(
    bool Success,
    bool Skipped,
    string LocalDate,
    DateTime RunUtc,
    BackupResult? Backup,
    PruneResult? Prune,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>
/// Runs the daily backup once per Africa/Algiers day (startup catch-up plus the configured
/// daily time) and prunes only after the daily copy succeeds.
/// </summary>
public sealed class BackupSchedulerService : IAsyncDisposable
{
    public static readonly TimeSpan DefaultDailyTime = new(20, 0, 0);
    private static readonly TimeSpan MinimumTick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumTick = TimeSpan.FromHours(24);

    private readonly BackupService _backups;
    private readonly AppLogger _logger;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();

    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private TimeSpan _dailyTime = DefaultDailyTime;
    private int _keepDays = BackupService.DefaultKeepDays;
    private int _keepMonthly = BackupService.DefaultKeepMonthly;
    private string? _lastRunDate;
    private bool _lastRunSucceeded;

    public BackupSchedulerService(BackupService backups, AppLogger logger, Func<DateTime>? clock = null)
    {
        _backups = backups;
        _logger = logger;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Local (Africa/Algiers) time of day for the scheduled run. Defaults to 20:00.</summary>
    public TimeSpan DailyTime
    {
        get => _dailyTime;
        set
        {
            if (value < TimeSpan.Zero || value >= TimeSpan.FromDays(1))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The daily backup time must be inside a single day.");
            }

            _dailyTime = value;
        }
    }

    public int KeepDays
    {
        get => _keepDays;
        set
        {
            if (value is < 1 or > 3650)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "KeepDays must be between 1 and 3650.");
            }

            _keepDays = value;
        }
    }

    public int KeepMonthly
    {
        get => _keepMonthly;
        set
        {
            if (value is < 0 or > 120)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "KeepMonthly must be between 0 and 120.");
            }

            _keepMonthly = value;
        }
    }

    public DateTime? LastBackupUtc { get; private set; }

    public DateTime? LastRunUtc { get; private set; }

    public string? LastRunLocalDate { get; private set; }

    public bool? LastRunSucceeded { get; private set; }

    public string? LastErrorCode { get; private set; }

    public int LastPrunedFiles { get; private set; }

    public string? LastDailyPath { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            if (IsRunning)
            {
                return;
            }

            _cancellation = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cancellation.Token));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation;
        Task? loop;
        lock (_gate)
        {
            cancellation = _cancellation;
            loop = _loop;
            _cancellation = null;
            _loop = null;
        }

        if (cancellation is null)
        {
            return;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the loop observes cancellation.
            }
        }

        cancellation.Dispose();
    }

    /// <summary>Forces a same-day replacement (manual "Backup now") and then prunes.</summary>
    public BackupRunReport RunManualBackup() => RunScheduledBackup(force: true);

    /// <summary>
    /// One scheduler tick. Non-forced calls are idempotent per local date; pruning happens only when
    /// the daily copy succeeded.
    /// </summary>
    public BackupRunReport RunScheduledBackup(bool force = false)
    {
        lock (_gate)
        {
            // One clock read per tick: RunUtc and the local date always describe the same instant.
            var runUtc = ReadNowUtc();
            var local = TimeZoneInfo.ConvertTimeFromUtc(runUtc, Algiers);
            var localDate = local.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            if (!force && _lastRunDate == localDate && _lastRunSucceeded)
            {
                return new BackupRunReport(true, true, localDate, runUtc, null, null, null, null);
            }

            try
            {
                var backup = _backups.CreateDailyBackup(force, "scheduler");
                if (!backup.Success)
                {
                    RecordFailure(localDate, runUtc, backup.ErrorCode);
                    _logger.Error($"Daily backup failed: {backup.ErrorCode} {backup.ErrorMessage}");
                    return new BackupRunReport(false, false, localDate, runUtc, backup, null,
                        backup.ErrorCode, backup.ErrorMessage);
                }

                _lastRunDate = localDate;
                _lastRunSucceeded = true;
                LastRunUtc = runUtc;
                LastRunLocalDate = localDate;
                LastRunSucceeded = true;
                LastBackupUtc = DateTime.SpecifyKind(backup.CreatedUtc, DateTimeKind.Utc);
                LastDailyPath = backup.Path;
                LastErrorCode = null;

                var prune = _backups.PruneBackups(_keepDays, _keepMonthly);
                LastPrunedFiles = prune.DeletedFiles;

                _logger.Info(
                    $"Daily backup {(backup.AlreadyExisted ? "already present" : "written")}: {backup.FileName} " +
                    $"(pruned={prune.DeletedFiles}, failed={prune.FailedFiles.Count})");

                return new BackupRunReport(true, false, localDate, runUtc, backup, prune, null, null);
            }
            catch (Exception ex)
            {
                // A scheduler tick must never take the application down; the failure is recorded and logged.
                RecordFailure(localDate, runUtc, "backup.scheduler-failed");
                _logger.Error("Scheduled backup tick failed.", ex);
                return new BackupRunReport(false, false, localDate, runUtc, null, null,
                    "backup.scheduler-failed", ex.Message);
            }
        }
    }

    private void RecordFailure(string localDate, DateTime runUtc, string? code)
    {
        _lastRunDate = localDate;
        _lastRunSucceeded = false;
        LastRunUtc = runUtc;
        LastRunLocalDate = localDate;
        LastRunSucceeded = false;
        LastErrorCode = code;
        LastPrunedFiles = 0;
        LastDailyPath = null;
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Startup catch-up: create today's copy immediately when it is missing.
            RunScheduledBackup();

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(NextDelay(), cancellationToken).ConfigureAwait(false);
                RunScheduledBackup();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            _logger.Error("Backup scheduler loop stopped unexpectedly.", ex);
        }
    }

    private TimeSpan NextDelay()
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(ReadNowUtc(), Algiers);
        var now = TimeSpan.FromTicks(local.TimeOfDay.Ticks);
        var delay = now <= _dailyTime ? _dailyTime - now : TimeSpan.FromDays(1) - now + _dailyTime;
        if (delay < MinimumTick)
        {
            delay = MinimumTick;
        }

        return delay > MaximumTick ? MaximumTick : delay;
    }

    private static readonly TimeZoneInfo Algiers = ResolveAlgiers();

    private DateTime ReadNowUtc()
    {
        var raw = _clock();
        return raw.Kind switch
        {
            DateTimeKind.Utc => raw,
            DateTimeKind.Local => raw.ToUniversalTime(),
            _ => TimeZoneInfo.ConvertTimeToUtc(raw, Algiers)
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
            // Algeria has no DST; the fixed offset keeps scheduling correct.
        }
        catch (InvalidTimeZoneException)
        {
            // Same: an unusable tz entry must not stop backups.
        }

        return TimeZoneInfo.CreateCustomTimeZone("Africa/Algiers", TimeSpan.FromHours(1), "Africa/Algiers", "Africa/Algiers");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
