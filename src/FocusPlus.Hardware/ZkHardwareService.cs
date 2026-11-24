using FocusPlus.Domain.Entities;

namespace FocusPlus.Hardware;

/// <summary>
/// Cross-platform hardware facade. Uses only the pure-managed port-4370 protocol —
/// no vendor DLL, no COM, no registry, works identically on Windows, Linux and macOS.
/// Every operation returns a result instead of throwing.
/// </summary>
public sealed class ZkHardwareService
{
    private readonly ZkNativeEngine _native;
    private readonly LanScanner _scanner = new();

    public ZkHardwareService(int timeoutMs = 8000)
    {
        _native = new ZkNativeEngine(timeoutMs);
    }

    /// <summary>Retained for backwards compatibility: the vendor SDK is never available.</summary>
#pragma warning disable CA1822 // instance member kept for API compatibility with existing view models
    public bool ComSdkAvailable => false;
#pragma warning restore CA1822

    public string EngineSummary => $"{_native.Name} (cross-platform, no vendor SDK)";

    public Task<IReadOnlyList<DiscoveredDevice>> ScanLanAsync(
        int timeoutMs = 250,
        int port = LanScanner.DefaultPort,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _scanner.ScanAsync(timeoutMs, port, progress, cancellationToken);

    public Task<IReadOnlyList<DiscoveredDevice>> ScanHostsAsync(
        IReadOnlyList<System.Net.IPAddress> hosts,
        int timeoutMs,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _scanner.ScanAsync(hosts, timeoutMs, LanScanner.DefaultPort, progress, cancellationToken);

    public Task<DiscoveredDevice> ProbeAsync(string ipAddress, int port, int commKey, CancellationToken cancellationToken = default) =>
        _scanner.ProbeAsync(ipAddress, port, commKey, cancellationToken);

    public Task<HardwareResult<DeviceSnapshot>> ReadSnapshotAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default) =>
        _native.ReadSnapshotAsync(endpoint, cancellationToken);

    public Task<HardwareResult<IReadOnlyList<DevicePunch>>> PullPunchesAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default) =>
        _native.PullPunchesAsync(endpoint, cancellationToken);

    public Task<HardwareResult<IReadOnlyList<DeviceUser>>> ReadUsersAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default) =>
        _native.ReadUsersAsync(endpoint, cancellationToken);

    public Task<HardwareResult> PushUserAsync(ZkEndpoint endpoint, DeviceUser user, CancellationToken cancellationToken = default) =>
        _native.PushUserAsync(endpoint, user, cancellationToken);

    public Task<HardwareResult> DeleteUserAsync(ZkEndpoint endpoint, string userId, CancellationToken cancellationToken = default) =>
        _native.DeleteUserAsync(endpoint, userId, cancellationToken);

    public Task<HardwareResult> SetNetworkAsync(ZkEndpoint endpoint, string ip, string mask, string gateway, int? port, CancellationToken cancellationToken = default) =>
        _native.SetNetworkAsync(endpoint, ip, mask, gateway, port, cancellationToken);

    public Task<HardwareResult> SyncClockAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default) =>
        _native.SyncClockAsync(endpoint, cancellationToken);

    public Task<HardwareResult> RestartAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default) =>
        _native.RestartAsync(endpoint, cancellationToken);

    public Task<HardwareResult> PowerOffAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default) =>
        _native.PowerOffAsync(endpoint, cancellationToken);

    public Task<HardwareResult> ClearAdminsAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default) =>
        _native.ClearAdminsAsync(endpoint, cancellationToken);

    public Task<HardwareResult> TestVoiceAsync(ZkEndpoint endpoint, int index = 0, CancellationToken cancellationToken = default) =>
        _native.TestVoiceAsync(endpoint, index, cancellationToken);

    /// <summary>True when the endpoint answers the protocol handshake.</summary>
    public async Task<bool> IsReachableAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        var result = await _native.ReadSnapshotAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return result.Success;
    }

    /// <summary>Native diagnostic dump for one endpoint (one "key = value" line per item).</summary>
    public async Task<IReadOnlyList<string>> DumpSdkInfoAsync(
        ZkEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        var result = await _native.ReadSnapshotAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var lines = new List<string>
        {
            $"Engine = {_native.Name} (native, cross-platform)",
            $"Endpoint = {endpoint.IpAddress}:{endpoint.Port}",
            $"CommKey = {endpoint.CommKey}"
        };

        if (result.Success && result.Value is { } s)
        {
            lines.Add($"Serial = {s.SerialNumber}");
            lines.Add($"Model = {s.ModelName}");
            lines.Add($"Firmware = {s.FirmwareVersion}");
            lines.Add($"Platform = {s.Platform}");
            lines.Add($"MAC = {s.MacAddress}");
            lines.Add($"Clock = {s.DeviceClock:O}");
            lines.Add($"Users = {s.UserCount}; Fingers = {s.FingerCount}; Faces = {s.FaceCount}; Punches = {s.PunchCount}");
            lines.Add($"Latency = {s.LatencyMs} ms");
        }
        else
        {
            lines.Add("Result = " + result.Message);
        }

        return lines;
    }
}
