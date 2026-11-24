using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using FocusPlus.Data.Repositories;
using FocusPlus.Domain;
using FocusPlus.Domain.Entities;
using FocusPlus.Hardware;

namespace FocusPlus.Application.Services;

public sealed record DeviceRuntimeStatus
{
    public required string DeviceId { get; init; }
    public DeviceConnectivity Connectivity { get; init; } = DeviceConnectivity.Unknown;
    public int LatencyMs { get; init; }
    public DateTime CheckedAtUtc { get; init; } = DateTime.UtcNow;
    public string? Error { get; init; }
    public DeviceSnapshot? Snapshot { get; init; }

    public bool IsOnline => Connectivity == DeviceConnectivity.Online;
}

public sealed record PullResult(bool Success, int Fetched, int Inserted, string Message, string? ErrorCode = null);

public enum NetworkUpdateStage
{
    Validating = 0,
    Writing = 1,
    Rebooting = 2,
    WaitingForDevice = 3,
    Reconnected = 4,
    SyncingClock = 5,
    PullingPunches = 6,
    Completed = 7,
    Failed = 8
}

public sealed record NetworkUpdateProgress(NetworkUpdateStage Stage, string? Detail = null, int Attempt = 0);

public sealed record NetworkUpdateResult(
    bool Success,
    string Message,
    string? ErrorCode = null,
    DeviceSnapshot? Snapshot = null,
    int InsertedPunches = 0);

/// <summary>
/// Device registry and every SuperAdmin hardware operation, including the
/// IP update → reboot → auto-reconnect → auto-pull pipeline.
/// </summary>
public sealed class DeviceService(
    DeviceRepository devices,
    EmployeeRepository employees,
    PunchRepository punches,
    AuditRepository audit,
    HardwareOperationLogRepository hardwareLogs,
    ZkHardwareService hardware,
    AppLogger logger)
{
    private readonly ConcurrentDictionary<string, DeviceRuntimeStatus> _statuses = new(StringComparer.Ordinal);

    public IReadOnlyList<Device> GetDevices(bool includeArchived = false) => devices.GetAll(includeArchived);

    public Device? Get(string id) => devices.Get(id);

    public Device? GetBySerial(string serial) => devices.GetBySerial(serial);

    public int Count() => devices.Count();

    public DeviceRuntimeStatus GetStatus(string deviceId) =>
        _statuses.TryGetValue(deviceId, out var status)
            ? status
            : new DeviceRuntimeStatus { DeviceId = deviceId };

    public IReadOnlyDictionary<string, DeviceRuntimeStatus> GetStatuses() =>
        new Dictionary<string, DeviceRuntimeStatus>(_statuses, StringComparer.Ordinal);

    public Task<IReadOnlyList<DiscoveredDevice>> ScanLanAsync(
        int timeoutMs,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken = default) =>
        hardware.ScanLanAsync(timeoutMs, LanScanner.DefaultPort, progress, cancellationToken);

    /// <summary>Registers a terminal discovered by the LAN scan, blocking duplicate serials.</summary>
    public (OperationResult Result, Device? Device) AddFromScan(DiscoveredDevice discovered, string name, int commKey, string actor)
    {
        // A TCP-open terminal that has not completed the handshake is still registered:
        // the admin can set its CommKey and use the SDK console immediately.
        var reachable = discovered.PortOpen || discovered.Responded || discovered.Snapshot is not null;
        if (!reachable)
        {
            return (OperationResult.Fail(ErrorCodes.DeviceUnreachable, discovered.Error), null);
        }

        var snapshot = discovered.Snapshot;

        if (!string.IsNullOrWhiteSpace(snapshot?.SerialNumber))
        {
            var duplicateSerial = devices.GetBySerial(snapshot.SerialNumber);
            if (duplicateSerial is not null)
            {
                return (OperationResult.Fail(ErrorCodes.DuplicateSerial, snapshot.SerialNumber), duplicateSerial);
            }
        }

        var duplicateEndpoint = devices.GetByEndpoint(discovered.IpAddress, discovered.Port);
        if (duplicateEndpoint is not null)
        {
            return (OperationResult.Fail(ErrorCodes.DuplicateEndpoint, $"{discovered.IpAddress}:{discovered.Port}"), duplicateEndpoint);
        }

        var device = new Device
        {
            Name = string.IsNullOrWhiteSpace(name)
                ? $"{(snapshot?.ModelName is { Length: > 0 } model ? model : "ZKTeco")} {discovered.IpAddress}"
                : name.Trim(),
            IpAddress = discovered.IpAddress,
            Port = discovered.Port,
            CommKey = commKey,
            SerialNumber = string.IsNullOrWhiteSpace(snapshot?.SerialNumber) ? null : snapshot.SerialNumber,
            ModelName = snapshot?.ModelName,
            FirmwareVersion = snapshot?.FirmwareVersion,
            MacAddress = snapshot?.MacAddress,
            Platform = snapshot?.Platform,
            DeviceClock = snapshot?.DeviceClock,
            UserCount = snapshot?.UserCount ?? 0,
            FingerCount = snapshot?.FingerCount ?? 0,
            FaceCount = snapshot?.FaceCount ?? 0,
            PunchCount = snapshot?.PunchCount ?? 0,
            LastSeenUtc = DateTime.UtcNow,
            Note = discovered.Responded ? null : discovered.HandshakeText
        };

        devices.Upsert(device);
        _statuses[device.Id] = new DeviceRuntimeStatus
        {
            DeviceId = device.Id,
            Connectivity = discovered.Responded ? DeviceConnectivity.Online : DeviceConnectivity.Unknown,
            LatencyMs = snapshot?.LatencyMs ?? 0,
            Snapshot = snapshot,
            Error = discovered.Responded ? null : discovered.Error
        };

        audit.Add(Log("device.added", actor, device.Id,
            $"{device.Endpoint} serial={device.SerialNumber} status={discovered.Status}"));
        return (OperationResult.Ok(), device);
    }

    /// <summary>Adds a terminal by IP. The terminal must answer the real protocol handshake.</summary>
    public async Task<(OperationResult Result, Device? Device)> AddByIpAsync(
        string name,
        string ipAddress,
        int port,
        int commKey,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(ipAddress, out _))
        {
            return (OperationResult.Fail(ErrorCodes.InvalidIp), null);
        }

        if (port is < 1 or > 65535)
        {
            return (OperationResult.Fail(ErrorCodes.InvalidPort), null);
        }

        var duplicateEndpoint = devices.GetByEndpoint(ipAddress, port);
        if (duplicateEndpoint is not null)
        {
            return (OperationResult.Fail(ErrorCodes.DuplicateEndpoint, $"{ipAddress}:{port}"), duplicateEndpoint);
        }

        var discovered = await hardware.ProbeAsync(ipAddress, port, commKey, cancellationToken).ConfigureAwait(false);
        if (!discovered.PortOpen)
        {
            return (OperationResult.Fail(ErrorCodes.DeviceUnreachable, discovered.Error), null);
        }

        return AddFromScan(discovered, name, commKey, actor);
    }

    /// <summary>
    /// One reconnect attempt for a device that is not currently online: probes with the stored
    /// CommKey, adopts the real identity when verified, and pulls punches immediately.
    /// </summary>
    public async Task<DeviceRuntimeStatus> ReconnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var device = devices.Get(deviceId);
        if (device is null)
        {
            return new DeviceRuntimeStatus { DeviceId = deviceId, Connectivity = DeviceConnectivity.Unknown };
        }

        var probe = await hardware.ProbeAsync(device.IpAddress, device.Port, device.CommKey, cancellationToken).ConfigureAwait(false);

        if (probe.Status == DiscoveryStatus.CommKeyRequired)
        {
            var status = new DeviceRuntimeStatus
            {
                DeviceId = device.Id,
                Connectivity = DeviceConnectivity.Unknown,
                Error = "CommKey required (CMD_ACK_UNAUTH)"
            };
            _statuses[device.Id] = status;
            return status;
        }

        if (!probe.Responded || probe.Snapshot is null)
        {
            var status = new DeviceRuntimeStatus
            {
                DeviceId = device.Id,
                Connectivity = device.SerialNumber is null ? DeviceConnectivity.Unknown : DeviceConnectivity.Offline,
                Error = probe.Error ?? "Handshake failed"
            };
            _statuses[device.Id] = status;
            return status;
        }

        var snapshot = probe.Snapshot;

        // Never steal a serial from another registered terminal (unique index).
        if (!string.IsNullOrWhiteSpace(snapshot.SerialNumber)
            && devices.GetBySerial(snapshot.SerialNumber) is { } owner
            && owner.Id != device.Id)
        {
            snapshot = snapshot with { SerialNumber = string.Empty };
        }

        var punchCountBefore = device.PunchCount;
        devices.Touch(device.Id, snapshot, online: true);

        var online = new DeviceRuntimeStatus
        {
            DeviceId = device.Id,
            Connectivity = DeviceConnectivity.Online,
            LatencyMs = snapshot.LatencyMs,
            Snapshot = snapshot
        };
        _statuses[device.Id] = online;

        var changed = snapshot.PunchCount != punchCountBefore
                      || device.LastPullUtc is null
                      || DateTime.UtcNow - device.LastPullUtc > TimeSpan.FromMinutes(5);
        if (changed)
        {
            await PullPunchesAsync(device.Id, "auto-reconnect", cancellationToken).ConfigureAwait(false);
        }

        audit.Add(Log("device.reconnected", "auto-reconnect", device.Id, snapshot.SerialNumber));
        logger.Info($"Reconnected {device.Name} ({device.Endpoint})");
        return online;
    }

    public async Task<HardwareResult<DeviceSnapshot>> RefreshAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var device = devices.Get(deviceId);
        if (device is null)
        {
            return HardwareResult<DeviceSnapshot>.Fail("Device not found.");
        }

        var result = await hardware.ReadSnapshotAsync(Endpoint(device), cancellationToken).ConfigureAwait(false);
        if (result.Success && result.Value is { } snapshot)
        {
            devices.Touch(device.Id, snapshot, online: true);
            _statuses[device.Id] = new DeviceRuntimeStatus
            {
                DeviceId = device.Id,
                Connectivity = DeviceConnectivity.Online,
                LatencyMs = snapshot.LatencyMs,
                Snapshot = snapshot
            };
        }
        else
        {
            _statuses[device.Id] = new DeviceRuntimeStatus
            {
                DeviceId = device.Id,
                Connectivity = DeviceConnectivity.Offline,
                Error = result.Message
            };
        }

        hardwareLogs.Add(new HardwareOperationLog
        {
            DeviceId = device.Id,
            Operation = "Refresh",
            Success = result.Success,
            Message = result.Message
        });

        return result;
    }

    public async Task<int> RefreshAllAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var online = 0;
        foreach (var device in devices.GetAll())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var result = await RefreshAsync(device.Id, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                online++;
            }

            progress?.Report($"{device.Name}: {(result.Success ? "online" : "offline")}");
        }

        return online;
    }

    /// <summary>Pulls all stored punches and stores only the new ones (strict deduplication).</summary>
    public async Task<PullResult> PullPunchesAsync(string deviceId, string actor, CancellationToken cancellationToken = default)
    {
        var device = devices.Get(deviceId);
        if (device is null)
        {
            return new PullResult(false, 0, 0, "Device not found.", ErrorCodes.NotFound);
        }

        var result = await hardware.PullPunchesAsync(Endpoint(device), cancellationToken).ConfigureAwait(false);
        hardwareLogs.Add(new HardwareOperationLog
        {
            DeviceId = device.Id,
            Operation = "PullPunches",
            Success = result.Success,
            Message = result.Message
        });

        if (!result.Success || result.Value is null)
        {
            _statuses[device.Id] = new DeviceRuntimeStatus
            {
                DeviceId = device.Id,
                Connectivity = DeviceConnectivity.Offline,
                Error = result.Message
            };
            return new PullResult(false, 0, 0, result.Message, ErrorCodes.Hardware);
        }

        var raw = result.Value;
        var pins = employees.GetAll(includeArchived: true)
            .Where(e => !string.IsNullOrWhiteSpace(e.DevicePin))
            .GroupBy(e => e.DevicePin!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

        var serial = device.SerialNumber ?? device.Id;
        var receivedAt = DateTime.UtcNow;
        var batch = new List<Punch>(raw.Count);

        foreach (var punch in raw)
        {
            var local = DateTime.SpecifyKind(punch.Timestamp, DateTimeKind.Local);
            var utc = local.ToUniversalTime();
            pins.TryGetValue(punch.DevicePin, out var employeeId);

            batch.Add(new Punch
            {
                EmployeeId = employeeId,
                DeviceId = device.Id,
                DeviceSerial = serial,
                DevicePin = punch.DevicePin,
                PunchTimeLocal = local,
                PunchTimeUtc = utc,
                VerifyMode = punch.VerifyMode,
                PunchState = punch.PunchState,
                WorkCode = punch.WorkCode,
                Source = PunchSource.Device,
                DedupeKey = BuildDedupeKey(serial, punch.DevicePin, utc),
                ReceivedAtUtc = receivedAt
            });
        }

        var inserted = punches.InsertBatch(batch);
        devices.MarkPull(device.Id, DateTime.UtcNow);
        _statuses[device.Id] = new DeviceRuntimeStatus
        {
            DeviceId = device.Id,
            Connectivity = DeviceConnectivity.Online,
            CheckedAtUtc = DateTime.UtcNow
        };

        if (inserted > 0)
        {
            audit.Add(Log("device.punches.pulled", actor, device.Id, $"fetched={raw.Count}; inserted={inserted}"));
            logger.Info($"Pulled {inserted} new punch(es) from {device.Name} ({device.Endpoint}).");
        }

        return new PullResult(true, raw.Count, inserted, $"Fetched {raw.Count}, stored {inserted} new punch(es).");
    }

    public async Task<(PullResult Result, int Devices)> PullAllAsync(string actor, CancellationToken cancellationToken = default)
    {
        var fetched = 0;
        var inserted = 0;
        var processed = 0;
        var failures = new List<string>();

        foreach (var device in devices.GetAll().Where(d => d.IsEnabled))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var result = await PullPunchesAsync(device.Id, actor, cancellationToken).ConfigureAwait(false);
            processed++;
            if (result.Success)
            {
                fetched += result.Fetched;
                inserted += result.Inserted;
            }
            else
            {
                failures.Add($"{device.Name}: {result.Message}");
            }
        }

        var message = failures.Count == 0
            ? $"Fetched {fetched}, stored {inserted} new punch(es) from {processed} device(s)."
            : $"Fetched {fetched}, stored {inserted}. Errors: {string.Join(" | ", failures)}";

        return (new PullResult(failures.Count < processed || processed > 0, fetched, inserted, message), processed);
    }

    public async Task<OperationResult> SyncClockAsync(string deviceId, string actor, CancellationToken cancellationToken = default)
    {
        var device = devices.Get(deviceId);
        if (device is null)
        {
            return OperationResult.Fail(ErrorCodes.NotFound);
        }

        var result = await hardware.SyncClockAsync(Endpoint(device), cancellationToken).ConfigureAwait(false);
        hardwareLogs.Add(new HardwareOperationLog { DeviceId = device.Id, Operation = "SyncClock", Success = result.Success, Message = result.Message });

        if (!result.Success)
        {
            return OperationResult.Fail(ErrorCodes.Hardware, result.Message);
        }

        devices.MarkSync(device.Id, DateTime.UtcNow);
        audit.Add(Log("device.clock.synced", actor, device.Id, device.Endpoint));
        return OperationResult.Ok();
    }

    public async Task<int> SyncAllClocksAsync(string actor, CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var device in devices.GetAll().Where(d => d.IsEnabled))
        {
            var result = await SyncClockAsync(device.Id, actor, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                count++;
            }
        }

        return count;
    }

    public Task<HardwareResult> RebootAsync(string deviceId, string actor, CancellationToken cancellationToken = default) =>
        ExecuteAsync(deviceId, actor, "Reboot", (endpoint, ct) => hardware.RestartAsync(endpoint, ct), cancellationToken);

    public Task<HardwareResult> PowerOffAsync(string deviceId, string actor, CancellationToken cancellationToken = default) =>
        ExecuteAsync(deviceId, actor, "PowerOff", (endpoint, ct) => hardware.PowerOffAsync(endpoint, ct), cancellationToken);

    public Task<HardwareResult> ClearAdminsAsync(string deviceId, string actor, CancellationToken cancellationToken = default) =>
        ExecuteAsync(deviceId, actor, "ClearAdmins", (endpoint, ct) => hardware.ClearAdminsAsync(endpoint, ct), cancellationToken);

    public Task<HardwareResult> TestVoiceAsync(string deviceId, int index = 0, CancellationToken cancellationToken = default) =>
        ExecuteAsync(deviceId, "operator", "TestVoice", (endpoint, ct) => hardware.TestVoiceAsync(endpoint, index, ct), cancellationToken);

    private async Task<HardwareResult> ExecuteAsync(
        string deviceId,
        string actor,
        string operation,
        Func<ZkEndpoint, CancellationToken, Task<HardwareResult>> action,
        CancellationToken cancellationToken)
    {
        var device = devices.Get(deviceId);
        if (device is null)
        {
            return HardwareResult.Fail("Device not found.");
        }

        var result = await action(Endpoint(device), cancellationToken).ConfigureAwait(false);
        hardwareLogs.Add(new HardwareOperationLog
        {
            DeviceId = device.Id,
            Operation = operation,
            Success = result.Success,
            Message = result.Message
        });

        audit.Add(Log($"device.{operation.ToLowerInvariant()}", actor, device.Id, result.Message));
        return result;
    }

    /// <summary>
    /// Writes new network parameters into the device NVRAM, reboots it, then polls the NEW endpoint
    /// every two seconds for up to 60 seconds. Once it answers: serial is verified, the clock is
    /// synchronised and new punches are pulled automatically.
    /// </summary>
    public async Task<NetworkUpdateResult> UpdateNetworkAsync(
        string deviceId,
        string newIpAddress,
        string subnetMask,
        string gateway,
        int newPort,
        string actor,
        IProgress<NetworkUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new NetworkUpdateProgress(NetworkUpdateStage.Validating));

        if (!IPAddress.TryParse(newIpAddress, out _))
        {
            return Fail(ErrorCodes.InvalidIp, newIpAddress);
        }

        if (newPort is < 1 or > 65535)
        {
            return Fail(ErrorCodes.InvalidPort, newPort.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(subnetMask) && !IPAddress.TryParse(subnetMask, out _))
        {
            return Fail(ErrorCodes.InvalidIp, subnetMask);
        }

        if (!string.IsNullOrWhiteSpace(gateway) && !IPAddress.TryParse(gateway, out _))
        {
            return Fail(ErrorCodes.InvalidIp, gateway);
        }

        var device = devices.Get(deviceId);
        if (device is null)
        {
            return Fail(ErrorCodes.NotFound, deviceId);
        }

        var conflict = devices.GetAll().FirstOrDefault(d => d.Id != device.Id && d.IpAddress == newIpAddress && d.Port == newPort);
        if (conflict is not null)
        {
            return Fail(ErrorCodes.DuplicateEndpoint, $"{newIpAddress}:{newPort}");
        }

        var oldIp = device.IpAddress;
        var oldPort = device.Port;
        var oldEndpoint = Endpoint(device);

        var before = await hardware.ReadSnapshotAsync(oldEndpoint, cancellationToken).ConfigureAwait(false);
        if (!before.Success)
        {
            return Fail(ErrorCodes.DeviceUnreachable, before.Message);
        }

        if (!string.IsNullOrWhiteSpace(device.SerialNumber)
            && !string.IsNullOrWhiteSpace(before.Value?.SerialNumber)
            && !string.Equals(device.SerialNumber, before.Value!.SerialNumber, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(ErrorCodes.DeviceSerialMismatch, $"{device.SerialNumber} ≠ {before.Value.SerialNumber}");
        }

        progress?.Report(new NetworkUpdateProgress(NetworkUpdateStage.Writing, $"{newIpAddress}:{newPort}"));
        var write = await hardware.SetNetworkAsync(
            oldEndpoint,
            newIpAddress,
            subnetMask,
            gateway,
            newPort == 4370 ? null : newPort,
            cancellationToken).ConfigureAwait(false);

        hardwareLogs.Add(new HardwareOperationLog
        {
            DeviceId = device.Id,
            Operation = "SetNetwork",
            Success = write.Success,
            Message = write.Message
        });

        if (!write.Success)
        {
            return Fail(ErrorCodes.Hardware, write.Message);
        }

        device.IpAddress = newIpAddress;
        device.Port = newPort;
        devices.Upsert(device);
        audit.Add(Log("device.ip.updated", actor, device.Id, $"{oldIp}:{oldPort} → {newIpAddress}:{newPort}"));

        progress?.Report(new NetworkUpdateProgress(NetworkUpdateStage.Rebooting));
        var restart = await hardware.RestartAsync(oldEndpoint, cancellationToken).ConfigureAwait(false);
        logger.Info($"IP update on {device.Name}: {oldIp}:{oldPort} → {newIpAddress}:{newPort}; reboot={restart.Success}");

        progress?.Report(new NetworkUpdateProgress(NetworkUpdateStage.WaitingForDevice, "0", 0));
        var deadline = DateTime.UtcNow.AddSeconds(60);
        var ports = newPort == 4370 ? new[] { 4370 } : new[] { newPort, 4370 };
        DeviceSnapshot? snapshot = null;
        var attempt = 0;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;
            foreach (var candidatePort in ports)
            {
                var probe = await hardware.ProbeAsync(newIpAddress, candidatePort, device.CommKey, cancellationToken).ConfigureAwait(false);
                if (probe.Responded && probe.Snapshot is not null)
                {
                    snapshot = probe.Snapshot;
                    if (candidatePort != newPort)
                    {
                        device.Port = candidatePort;
                        devices.Upsert(device);
                        audit.Add(Log("device.port.fallback", actor, device.Id, $"port {candidatePort}"));
                    }

                    break;
                }
            }

            if (snapshot is not null)
            {
                break;
            }

            progress?.Report(new NetworkUpdateProgress(NetworkUpdateStage.WaitingForDevice, attempt.ToString(CultureInfo.InvariantCulture), attempt));
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        if (snapshot is null)
        {
            _statuses[device.Id] = new DeviceRuntimeStatus
            {
                DeviceId = device.Id,
                Connectivity = DeviceConnectivity.Offline,
                Error = "The terminal did not answer on its new address within 60 seconds."
            };
            progress?.Report(new NetworkUpdateProgress(NetworkUpdateStage.Failed, newIpAddress));
            return Fail(ErrorCodes.DeviceUnreachable, $"{newIpAddress}:{newPort}");
        }

        if (!string.IsNullOrWhiteSpace(device.SerialNumber)
            && !string.Equals(device.SerialNumber, snapshot.SerialNumber, StringComparison.OrdinalIgnoreCase))
        {
            device.IpAddress = oldIp;
            device.Port = oldPort;
            devices.Upsert(device);
            audit.Add(Log("device.ip.rollback", actor, device.Id, $"serial mismatch {snapshot.SerialNumber}"));
            return Fail(ErrorCodes.DeviceSerialMismatch, $"{snapshot.SerialNumber}");
        }

        progress?.Report(new NetworkUpdateProgress(NetworkUpdateStage.Reconnected, snapshot.SerialNumber));
        devices.Touch(device.Id, snapshot, online: true);
        _statuses[device.Id] = new DeviceRuntimeStatus
        {
            DeviceId = device.Id,
            Connectivity = DeviceConnectivity.Online,
            LatencyMs = snapshot.LatencyMs,
            Snapshot = snapshot
        };

        progress?.Report(new NetworkUpdateProgress(NetworkUpdateStage.SyncingClock));
        await hardware.SyncClockAsync(new ZkEndpoint(newIpAddress, device.Port, device.CommKey), cancellationToken).ConfigureAwait(false);
        devices.MarkSync(device.Id, DateTime.UtcNow);

        progress?.Report(new NetworkUpdateProgress(NetworkUpdateStage.PullingPunches));
        var pull = await PullPunchesAsync(device.Id, actor, cancellationToken).ConfigureAwait(false);
        devices.MarkPull(device.Id, DateTime.UtcNow);

        progress?.Report(new NetworkUpdateProgress(NetworkUpdateStage.Completed, snapshot.SerialNumber));
        audit.Add(Log("device.ip.reconnected", actor, device.Id, $"{newIpAddress}:{device.Port} pulls={pull.Inserted}"));

        return new NetworkUpdateResult(
            true,
            $"Device is back on {newIpAddress}:{device.Port}. {pull.Inserted} new punch(es) stored.",
            null,
            snapshot,
            pull.Inserted);
    }

    public async Task<HardwareResult<IReadOnlyList<DeviceUser>>> ReadUsersAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var device = devices.Get(deviceId);
        if (device is null)
        {
            return HardwareResult<IReadOnlyList<DeviceUser>>.Fail("Device not found.");
        }

        var result = await hardware.ReadUsersAsync(Endpoint(device), cancellationToken).ConfigureAwait(false);
        hardwareLogs.Add(new HardwareOperationLog
        {
            DeviceId = device.Id,
            Operation = "ReadUsers",
            Success = result.Success,
            Message = result.Message
        });
        return result;
    }

    public OperationResult SetEnabled(string deviceId, bool enabled, string actor)
    {
        var device = devices.Get(deviceId);
        if (device is null)
        {
            return OperationResult.Fail(ErrorCodes.NotFound);
        }

        devices.SetEnabled(deviceId, enabled);
        audit.Add(Log(enabled ? "device.enabled" : "device.disabled", actor, deviceId, null));
        return OperationResult.Ok();
    }

    public OperationResult Archive(string deviceId, string reason, string actor)
    {
        var device = devices.Get(deviceId);
        if (device is null)
        {
            return OperationResult.Fail(ErrorCodes.NotFound);
        }

        devices.Archive(deviceId, reason, actor);
        _statuses.TryRemove(deviceId, out _);
        audit.Add(Log("device.archived", actor, deviceId, reason));
        return OperationResult.Ok();
    }

    public OperationResult Restore(string deviceId, string actor)
    {
        devices.Restore(deviceId, actor);
        audit.Add(Log("device.restored", actor, deviceId, null));
        return OperationResult.Ok();
    }

    private static NetworkUpdateResult Fail(string errorCode, string? detail) =>
        new(false, detail ?? errorCode, errorCode);

    internal static ZkEndpoint Endpoint(Device device) => new(device.IpAddress, device.Port, device.CommKey);

    internal static string BuildDedupeKey(string deviceSerial, string devicePin, DateTime utc) =>
        $"{deviceSerial}|{devicePin}|{utc:O}";

    private static AuditLogEntry Log(string action, string actor, string entityId, string? details) => new()
    {
        Action = action,
        Actor = actor,
        EntityType = "Device",
        EntityId = entityId,
        Details = details
    };
}
