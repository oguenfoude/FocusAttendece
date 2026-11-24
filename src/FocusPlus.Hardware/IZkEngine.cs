using FocusPlus.Domain.Entities;

#pragma warning disable CA1000 // Factory helpers on HardwareResult<T> are intentional and discoverable.

namespace FocusPlus.Hardware;

public sealed record HardwareResult(bool Success, string Message)
{
    public static HardwareResult Ok(string message = "OK") => new(true, message);
    public static HardwareResult Fail(string message) => new(false, message);
    public static HardwareResult From(bool success, string message) => new(success, message);
}

public sealed record HardwareResult<T>(bool Success, T? Value, string Message)
{
    public static HardwareResult<T> Ok(T value, string message = "OK") => new(true, value, message);
    public static HardwareResult<T> Fail(string message) => new(false, default, message);
}

/// <summary>A hardware engine able to talk to a ZKTeco terminal.</summary>
public interface IZkEngine
{
    string Name { get; }
    bool IsAvailable { get; }

    Task<HardwareResult<DeviceSnapshot>> ReadSnapshotAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default);
    Task<HardwareResult<IReadOnlyList<DevicePunch>>> PullPunchesAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default);
    Task<HardwareResult<IReadOnlyList<DeviceUser>>> ReadUsersAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default);
    Task<HardwareResult> PushUserAsync(ZkEndpoint endpoint, DeviceUser user, CancellationToken cancellationToken = default);
    Task<HardwareResult> DeleteUserAsync(ZkEndpoint endpoint, string userId, CancellationToken cancellationToken = default);
    Task<HardwareResult> SetNetworkAsync(ZkEndpoint endpoint, string ipAddress, string subnetMask, string gateway, int? port, CancellationToken cancellationToken = default);
    Task<HardwareResult> SyncClockAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default);
    Task<HardwareResult> RestartAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default);
    Task<HardwareResult> PowerOffAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default);
    Task<HardwareResult> ClearAdminsAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default);
    Task<HardwareResult> TestVoiceAsync(ZkEndpoint endpoint, int index = 0, CancellationToken cancellationToken = default);
}
