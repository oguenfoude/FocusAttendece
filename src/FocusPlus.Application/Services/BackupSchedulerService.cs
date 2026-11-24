using FocusPlus.Data;

namespace FocusPlus.Application.Services;

/// <summary>Runs the rolling 4-hour backup inside the application's lifetime.</summary>
public sealed class BackupSchedulerService(BackupService backups, AppLogger logger) : IAsyncDisposable
{
    private CancellationTokenSource? _cancellation;
    private Task? _loop;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(4);

    public bool IsRunning => _loop is { IsCompleted: false };

    public DateTime? LastBackupUtc { get; private set; }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cancellation.Token));
    }

    public async Task StopAsync()
    {
        if (_cancellation is null)
        {
            return;
        }

        await _cancellation.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cancellation.Dispose();
        _cancellation = null;
        _loop = null;
    }

    public void RunNow()
    {
        try
        {
            var backup = backups.CreateBackup("scheduled");
            LastBackupUtc = DateTime.UtcNow;
            logger.Info($"Scheduled backup written: {backup.Path}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            logger.Error("Scheduled backup failed.", ex);
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, cancellationToken).ConfigureAwait(false);
                RunNow();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.Error("Backup scheduler tick failed.", ex);
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
