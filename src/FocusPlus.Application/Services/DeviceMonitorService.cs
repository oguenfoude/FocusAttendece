using System.Collections.Concurrent;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Application.Services;

/// <summary>
/// Continuous background engine: keeps every enabled device status live, reconnects automatically
/// after a power/LAN outage, and pulls new punches as soon as they appear.
/// </summary>
public sealed class DeviceMonitorService(DeviceService devices, AppLogger logger) : IAsyncDisposable
{
    private CancellationTokenSource? _cancellation;
    private Task? _loop;

    public int IntervalSeconds { get; set; } = 20;

    public bool IsRunning => _loop is { IsCompleted: false };

    public event Action<DeviceRuntimeStatus>? StatusChanged;

    public event Action<string, int>? PunchesPulled;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cancellation.Token));
        logger.Info("Device monitor started.");
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
                // Normal shutdown.
            }
        }

        _cancellation.Dispose();
        _cancellation = null;
        _loop = null;
        logger.Info("Device monitor stopped.");
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.Error("Device monitor tick failed.", ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, IntervalSeconds)), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One monitoring pass over every enabled device (also used by tests / manual refresh).</summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)    {
        foreach (var listed in devices.GetDevices().Where(d => d.IsEnabled))
        {
            var device = listed;
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var knownPunches = device.PunchCount;
            var current = devices.GetStatus(device.Id);

            if (!current.IsOnline)
            {
                // Auto-reconnect: retries every tick with the stored CommKey.
                var reconnected = await devices.ReconnectAsync(device.Id, cancellationToken).ConfigureAwait(false);
                StatusChanged?.Invoke(reconnected);
                if (!reconnected.IsOnline)
                {
                    continue;
                }

                device = devices.Get(device.Id) ?? device;
            }

            var snapshot = await devices.RefreshAsync(device.Id, cancellationToken).ConfigureAwait(false);
            StatusChanged?.Invoke(devices.GetStatus(device.Id));

            if (!snapshot.Success || snapshot.Value is null)
            {
                continue;
            }

            var punchCountChanged = snapshot.Value.PunchCount != knownPunches;
            var stalePull = device.LastPullUtc is null || DateTime.UtcNow - device.LastPullUtc > TimeSpan.FromMinutes(5);
            if (!punchCountChanged && !stalePull)
            {
                continue;
            }

            var pull = await devices.PullPunchesAsync(device.Id, "monitor", cancellationToken).ConfigureAwait(false);
            if (pull.Success && pull.Inserted > 0)
            {
                PunchesPulled?.Invoke(device.Id, pull.Inserted);
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
