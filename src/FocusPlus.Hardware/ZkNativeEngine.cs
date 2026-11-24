using System.Diagnostics;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Hardware;

/// <summary>
/// ZKTeco standalone protocol engine. Works with any terminal listening on port 4370,
/// including UF100 Plus-S, with no vendor DLL or platform-specific installation.
/// Tries TCP first and transparently falls back to UDP.
/// </summary>
public sealed class ZkNativeEngine : IZkEngine
{
    private readonly int _timeoutMs;

    public ZkNativeEngine(int timeoutMs = 8000) => _timeoutMs = timeoutMs;

    public string Name => "ZKTeco native TCP/UDP 4370";

    public bool IsAvailable => true;

    public Task<HardwareResult<DeviceSnapshot>> ReadSnapshotAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default) =>
        Task.Run(() => Run(endpoint, (client, elapsed) => BuildSnapshot(endpoint, client, elapsed), "Read device info"), cancellationToken);

    public Task<HardwareResult<IReadOnlyList<DevicePunch>>> PullPunchesAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default) =>
        Task.Run(() => Run<IReadOnlyList<DevicePunch>>(endpoint, (client, _) =>
        {
            var sizes = ReadSizes(client);
            if (sizes.Records <= 0)
            {
                return [];
            }

            var buffer = client.ReadBuffer(ZkCommand.AttLogRrq);
            return ZkBufferParser.ParseAttendance(buffer, sizes.Records);
        }, "Pull attendance"), cancellationToken);

    public Task<HardwareResult<IReadOnlyList<DeviceUser>>> ReadUsersAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default) =>
        Task.Run(() => Run<IReadOnlyList<DeviceUser>>(endpoint, (client, _) => ReadUsersCore(client), "Read device users"), cancellationToken);

    public Task<HardwareResult> PushUserAsync(ZkEndpoint endpoint, DeviceUser user, CancellationToken cancellationToken = default) =>
        Task.Run(() => RunPlain(endpoint, (client, _) => PushUserCore(client, user), "Push user"), cancellationToken);

    public Task<HardwareResult> DeleteUserAsync(ZkEndpoint endpoint, string userId, CancellationToken cancellationToken = default) =>
        Task.Run(() => RunPlain(endpoint, (client, _) => DeleteUserCore(client, userId), "Delete user"), cancellationToken);

    public Task<HardwareResult> SetNetworkAsync(ZkEndpoint endpoint, string ipAddress, string subnetMask, string gateway, int? port, CancellationToken cancellationToken = default) =>
        Task.Run(() => RunPlain(endpoint, (client, _) => SetNetworkCore(client, ipAddress, subnetMask, gateway, port), "Update network"), cancellationToken);

    public Task<HardwareResult> SyncClockAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default) =>
        Task.Run(() => RunPlain(endpoint, (client, _) => SetClockCore(client), "Sync clock"), cancellationToken);

    public Task<HardwareResult> RestartAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default) =>
        Task.Run(() => FireAndForget(endpoint, ZkCommand.Restart, "Reboot"), cancellationToken);

    public Task<HardwareResult> PowerOffAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default) =>
        Task.Run(() => FireAndForget(endpoint, ZkCommand.PowerOff, "Power off"), cancellationToken);

    public Task<HardwareResult> ClearAdminsAsync(ZkEndpoint endpoint, CancellationToken cancellationToken = default) =>
        Task.Run(() => RunPlain(endpoint, (client, _) =>
        {
            var (code, _) = client.Send(ZkCommand.ClearAdmin, []);
            if (code is not (ZkCommand.AckOk or ZkCommand.AckData))
            {
                throw new ZkException($"Clear administrators refused (0x{code:X4}).", code);
            }

            return true;
        }, "Clear administrators"), cancellationToken);

    public Task<HardwareResult> TestVoiceAsync(ZkEndpoint endpoint, int index = 0, CancellationToken cancellationToken = default) =>
        Task.Run(() => RunPlain(endpoint, (client, _) =>
        {
            var payload = new byte[4];
            ZkPacket.WriteInt32(payload, 0, index);
            var (code, _) = client.Send(ZkCommand.TestVoice, payload);
            return code is ZkCommand.AckOk or ZkCommand.AckData;
        }, "Voice test"), cancellationToken);

    private HardwareResult<T> Run<T>(ZkEndpoint endpoint, Func<ZkProtocolClient, long, T> action, string operation)
    {
        var errors = new List<string>();
        foreach (var udp in new[] { false, true })
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var client = new ZkProtocolClient(endpoint, udp, _timeoutMs);
                client.Connect();
                var connectMs = stopwatch.ElapsedMilliseconds;
                var value = action(client, connectMs);
                return HardwareResult<T>.Ok(value, $"{operation} OK ({Transport(udp)}, {stopwatch.ElapsedMilliseconds} ms)");
            }
            catch (ZkException ex)
            {
                errors.Add($"{Transport(udp)}: {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or ArgumentException)
            {
                errors.Add($"{Transport(udp)}: {ex.Message}");
            }
        }

        return HardwareResult<T>.Fail($"{operation} failed → {string.Join(" | ", errors)}");
    }

    private HardwareResult RunPlain(ZkEndpoint endpoint, Func<ZkProtocolClient, long, bool> action, string operation)
    {
        var result = Run(endpoint, action, operation);
        return new HardwareResult(result.Success, result.Message);
    }

    private HardwareResult FireAndForget(ZkEndpoint endpoint, ushort command, string operation)
    {
        var errors = new List<string>();
        foreach (var udp in new[] { false, true })
        {
            try
            {
                using var client = new ZkProtocolClient(endpoint, udp, _timeoutMs);
                client.Connect();
                try
                {
                    client.Send(command, []);
                }
                catch (ZkException)
                {
                    // The terminal drops the socket while rebooting: expected.
                }

                return HardwareResult.Ok($"{operation} command sent ({Transport(udp)}).");
            }
            catch (ZkException ex)
            {
                errors.Add($"{Transport(udp)}: {ex.Message}");
            }
        }

        return HardwareResult.Fail($"{operation} failed → {string.Join(" | ", errors)}");
    }

    private static string Transport(bool udp) => udp ? "UDP" : "TCP";

    private static DeviceSnapshot BuildSnapshot(ZkEndpoint endpoint, ZkProtocolClient client, long connectMs)
    {
        var serial = FirstOption(client, "~SerialNumber", "SerialNumber", "~SN");
        var model = FirstOption(client, "~DeviceName", "~OEMDeviceName", "DeviceName");
        var platform = FirstOption(client, "~Platform", "~ZKlanguage", "Platform");
        var mac = FirstOption(client, "MAC", "~MAC", "MacAddress", "~MACAddress");
        var firmware = TryReadFirmware(client);
        if (string.IsNullOrWhiteSpace(firmware))
        {
            firmware = FirstOption(client, "~FirmwareVersion", "FirmwareVersion", "~ZKFPVersion");
        }

        var clock = TryReadClock(client);
        var sizes = ReadSizes(client);

        return new DeviceSnapshot
        {
            IpAddress = endpoint.IpAddress,
            Port = endpoint.Port,
            SerialNumber = serial,
            ModelName = string.IsNullOrWhiteSpace(model) ? null : model,
            FirmwareVersion = firmware,
            MacAddress = mac,
            Platform = platform,
            DeviceClock = clock,
            UserCount = sizes.Users,
            FingerCount = sizes.Fingers,
            FaceCount = sizes.Faces,
            PunchCount = sizes.Records,
            LatencyMs = (int)connectMs,
            Engine = "Native TCP/UDP 4370"
        };
    }

    private static ZkMemorySizes ReadSizes(ZkProtocolClient client)
    {
        var (code, data) = client.Send(ZkCommand.GetFreeSizes, []);
        return code is ZkCommand.AckOk or ZkCommand.AckData
            ? ZkBufferParser.ParseSizes(data)
            : new ZkMemorySizes();
    }

    private static IReadOnlyList<DeviceUser> ReadUsersCore(ZkProtocolClient client)
    {
        var sizes = ReadSizes(client);
        var buffer = client.ReadBuffer(ZkCommand.UserTempRrq, ZkCommand.FctUser);
        return ZkBufferParser.ParseUsers(buffer, sizes.Users);
    }

    private static bool PushUserCore(ZkProtocolClient client, DeviceUser user)
    {
        var (code, _) = client.Send(ZkCommand.UserWrq, ZkBufferParser.BuildUserRecord(user));
        if (code is not (ZkCommand.AckOk or ZkCommand.AckData))
        {
            throw new ZkException($"User write refused (0x{code:X4}).", code);
        }

        client.Send(ZkCommand.RefreshData, []);

        // Some firmware only accepts the legacy 28-byte record; verify and retry once.
        var users = ReadUsersCore(client);
        var found = users.Any(u => string.Equals(u.UserId, user.UserId, StringComparison.Ordinal) || u.Uid == user.Uid);
        if (found)
        {
            return true;
        }

        var (legacyCode, _) = client.Send(ZkCommand.UserWrq, ZkBufferParser.BuildUserRecord(user, legacy28: true));
        if (legacyCode is not (ZkCommand.AckOk or ZkCommand.AckData))
        {
            throw new ZkException($"Legacy user write refused (0x{legacyCode:X4}).", legacyCode);
        }

        client.Send(ZkCommand.RefreshData, []);
        return true;
    }

    private static bool DeleteUserCore(ZkProtocolClient client, string userId)
    {
        var users = ReadUsersCore(client);
        var target = users.FirstOrDefault(u => string.Equals(u.UserId, userId, StringComparison.Ordinal))
                     ?? users.FirstOrDefault(u => string.Equals(u.Uid.ToString(), userId, StringComparison.Ordinal));

        if (target is null)
        {
            throw new ZkException($"User '{userId}' does not exist on the terminal.");
        }

        var payload = new byte[2];
        ZkPacket.WriteUInt16(payload, 0, (ushort)target.Uid);
        var (code, _) = client.Send(ZkCommand.DeleteUser, payload);
        if (code is not (ZkCommand.AckOk or ZkCommand.AckData))
        {
            throw new ZkException($"User delete refused (0x{code:X4}).", code);
        }

        client.Send(ZkCommand.RefreshData, []);
        return true;
    }

    private static bool SetNetworkCore(ZkProtocolClient client, string ipAddress, string subnetMask, string gateway, int? port)
    {
        var applied = 0;
        if (!string.IsNullOrWhiteSpace(ipAddress) && client.WriteOption($"IPAddress={ipAddress}"))
        {
            applied++;
        }

        if (!string.IsNullOrWhiteSpace(subnetMask) && client.WriteOption($"NetMask={subnetMask}"))
        {
            applied++;
        }

        if (!string.IsNullOrWhiteSpace(gateway) && client.WriteOption($"GATEIPAddress={gateway}"))
        {
            applied++;
        }

        if (port is > 0 and not 4370)
        {
            // Best effort: not every firmware accepts a remote port change.
            client.WriteOption($"Port={port.Value}");
        }

        if (applied == 0)
        {
            throw new ZkException("The terminal refused every network parameter.");
        }

        try
        {
            client.Send(ZkCommand.RefreshOption, []);
        }
        catch (ZkException)
        {
            // Optional on older firmware.
        }

        return true;
    }

    private static bool SetClockCore(ZkProtocolClient client)
    {
        var now = DateTime.Now;
        var payload = new byte[4];
        ZkPacket.WriteUInt32(payload, 0, ZkPacket.EncodeTime(now));
        var (code, _) = client.Send(ZkCommand.SetTime, payload);
        if (code is not (ZkCommand.AckOk or ZkCommand.AckData))
        {
            throw new ZkException($"Clock update refused (0x{code:X4}).", code);
        }

        return true;
    }

    private static string TryRead(ZkProtocolClient client, string option) =>
        client.TryReadOption(option, out var value) ? value : string.Empty;

    /// <summary>Firmware builds disagree on option names: try each alias until one answers.</summary>
    private static string FirstOption(ZkProtocolClient client, params string[] options)
    {
        foreach (var option in options)
        {
            var value = TryRead(client, option);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string TryReadFirmware(ZkProtocolClient client)
    {
        try
        {
            var (code, data) = client.Send(ZkCommand.GetVersion, []);
            return code is ZkCommand.AckOk or ZkCommand.AckData
                ? ZkPacket.ReadAsciiZ(data)
                : string.Empty;
        }
        catch (ZkException)
        {
            return string.Empty;
        }
    }

    private static DateTime? TryReadClock(ZkProtocolClient client)
    {
        try
        {
            var (code, data) = client.Send(ZkCommand.GetTime, []);
            if (code is not (ZkCommand.AckOk or ZkCommand.AckData) || data.Length < 4)
            {
                return null;
            }

            return ZkPacket.TryDecodeTime(ZkPacket.ReadUInt32(data, 0), out var clock) ? clock : null;
        }
        catch (ZkException)
        {
            return null;
        }
    }
}
