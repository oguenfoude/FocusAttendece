using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Hardware;

/// <summary>How far the scanner got with one endpoint.</summary>
public enum DiscoveryStatus
{
    Unreachable = 0,
    PortOpenUnverified = 1,
    CommKeyRequired = 2,
    Verified = 3
}

public sealed record DiscoveredDevice
{
    public required string IpAddress { get; init; }
    public int Port { get; init; } = 4370;
    public bool Responded { get; init; }
    public bool PortOpen { get; init; }
    public DiscoveryStatus Status { get; init; } = DiscoveryStatus.Unreachable;
    public DeviceSnapshot? Snapshot { get; init; }
    public string? Error { get; init; }
    public string Transport { get; init; } = "TCP";

    public string? SerialNumber => Snapshot?.SerialNumber;
    public string DisplayModel => Snapshot?.ModelName
        ?? (Status == DiscoveryStatus.Verified ? "ZKTeco" : string.Empty);
    public string HandshakeText => Status switch
    {
        DiscoveryStatus.Verified => "Verified ZKTeco",
        DiscoveryStatus.CommKeyRequired => "Port 4370 open — CommKey required",
        DiscoveryStatus.PortOpenUnverified => "Port 4370 open — unverified",
        _ => "Unreachable"
    };
}

public sealed record LocalNetwork(string InterfaceName, string Address, int PrefixLength, string Cidr, int HostCount);

public sealed record ScanProgress(int Scanned, int Total, string? FoundIp, int Found);

/// <summary>
/// Multi-network LAN discovery on port 4370. Every operational IPv4 adapter is scanned, plus an
/// optional custom range. Each endpoint gets a resilient handshake: standard TCP framing, raw TCP,
/// UDP; virtual interfaces (bridges, containers, VPNs) are skipped by default.
/// </summary>
public sealed class LanScanner
{
    public const int DefaultPort = 4370;

    private readonly int _perSubnetLimit;
    private readonly int _probeTimeoutMs;

    public LanScanner(int perSubnetLimit = 254, int probeTimeoutMs = 4000)
    {
        _perSubnetLimit = perSubnetLimit;
        _probeTimeoutMs = probeTimeoutMs;
    }

    /// <summary>All operational IPv4 networks of this machine (name, CIDR, address).</summary>
    public static IReadOnlyList<LocalNetwork> GetLocalNetworks()
    {
        var networks = new List<LocalNetwork>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                {
                    continue;
                }

                var mask = ToUInt32(unicast.IPv4Mask);
                if (mask == 0)
                {
                    continue;
                }

                var prefix = Math.Max(PrefixLength(mask), 24);
                var subnetMask = prefix >= 32 ? uint.MaxValue : ~(uint.MaxValue >> prefix);
                var network = ToUInt32(unicast.Address) & subnetMask;
                var broadcast = network | ~subnetMask;
                var hosts = (int)Math.Min(Math.Max(0, (long)broadcast - network - 1), _perSubnetLimitCap);

                networks.Add(new LocalNetwork(
                    nic.Name,
                    unicast.Address.ToString(),
                    prefix,
                    $"{FromUInt32(network)}/{prefix}",
                    hosts));
            }
        }

        return networks;
    }

    private const int _perSubnetLimitCap = 254;

    /// <summary>Hosts of every local /24 (bounded per subnet).</summary>
    /// <summary>Hard cap on hosts probed in an all-interfaces scan.</summary>
    public const int MaxScanHosts = 1024;

    private static readonly string[] VirtualInterfacePrefixes =
        ["br-", "docker", "veth", "tailscale", "virbr", "vmnet", "vboxnet"];

    public static IReadOnlyList<IPAddress> BuildHostList(int perSubnetLimit = 254) =>
        BuildHostList(perSubnetLimit, networkCidr: null);

    /// <summary>Hosts of one selected network (CIDR) or of every local network when null.</summary>
    public static IReadOnlyList<IPAddress> BuildHostList(int perSubnetLimit, string? networkCidr)
    {
        var hosts = new List<IPAddress>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var filter = ParseCidr(networkCidr);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (hosts.Count >= MaxScanHosts)
            {
                break;
            }

            // Bridges, containers and VPNs are not where a biometric terminal lives.
            // Skipping them keeps discovery fast and predictable on Linux/Windows/macOS.
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel
                || VirtualInterfacePrefixes.Any(p => nic.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (hosts.Count >= MaxScanHosts)
                {
                    break;
                }
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                {
                    continue;
                }

                var address = ToUInt32(unicast.Address);
                var mask = ToUInt32(unicast.IPv4Mask);
                if (mask == 0)
                {
                    continue;
                }

                var prefix = Math.Max(PrefixLength(mask), 24);
                var subnetMask = prefix >= 32 ? uint.MaxValue : ~(uint.MaxValue >> prefix);
                var network = address & subnetMask;

                if (filter is { } range && (network < range.Start || network > range.End))
                {
                    continue;
                }

                var broadcast = network | ~subnetMask;
                var added = 0;
                for (var host = network + 1; host < broadcast && added < perSubnetLimit; host++)
                {
                    if (hosts.Count >= MaxScanHosts)
                    {
                        break;
                    }
                    if (host == address)
                    {
                        continue;
                    }

                    var ip = FromUInt32(host);
                    if (seen.Add(ip))
                    {
                        hosts.Add(IPAddress.Parse(ip));
                        added++;
                    }
                }
            }
        }

        return hosts;
    }

    /// <summary>Hosts from a manual Start–End IPv4 range.</summary>
    public static IReadOnlyList<IPAddress> BuildHostRange(string startIp, string endIp, int limit = 1024)
    {
        if (!IPAddress.TryParse(startIp, out var start) || !IPAddress.TryParse(endIp, out var end))
        {
            return [];
        }

        var first = ToUInt32(start);
        var last = ToUInt32(end);
        if (last < first)
        {
            (first, last) = (last, first);
        }

        var hosts = new List<IPAddress>();
        for (var value = first; value <= last && hosts.Count < limit; value++)
        {
            hosts.Add(IPAddress.Parse(FromUInt32(value)));
        }

        return hosts;
    }

    public Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(
        int timeoutMs = 250,
        int port = DefaultPort,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ScanAsync(BuildHostList(_perSubnetLimit), timeoutMs, port, progress, cancellationToken);

    public async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(
        IReadOnlyList<IPAddress> hosts,
        int timeoutMs,
        int port,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new ConcurrentBag<DiscoveredDevice>();
        var scanned = 0;
        var found = 0;

        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        var bumped = Math.Max(minWorker, 96);
        ThreadPool.SetMinThreads(bumped, minIo);

        try
        {
            using var semaphore = new SemaphoreSlim(64);
            var tasks = hosts.Select(ip => Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var address = ip.ToString();
                    var discovered = await ProbeAsync(address, port, 0, cancellationToken).ConfigureAwait(false);
                    if (discovered.Status != DiscoveryStatus.Unreachable)
                    {
                        results.Add(discovered);
                        Interlocked.Increment(ref found);

                        progress?.Report(new ScanProgress(Volatile.Read(ref scanned), hosts.Count, address, Volatile.Read(ref found)));
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One hostile/slow endpoint must never abort the whole discovery pass.
                    results.Add(new DiscoveredDevice
                    {
                        IpAddress = ip.ToString(),
                        Port = port,
                        PortOpen = true,
                        Status = DiscoveryStatus.PortOpenUnverified,
                        Error = ex.Message
                    });
                }
                finally
                {
                    var done = Interlocked.Increment(ref scanned);
                    progress?.Report(new ScanProgress(done, hosts.Count, null, Volatile.Read(ref found)));
                    semaphore.Release();
                }
            }, cancellationToken));

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            ThreadPool.SetMinThreads(minWorker, minIo);
        }

        return results.OrderBy(r => r.IpAddress, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Probes one endpoint: port check, then framed TCP, raw TCP, UDP and finally the optional SDK.
    /// A TCP-open endpoint that never answers the protocol is still returned (registerable).
    /// </summary>
    public async Task<DiscoveredDevice> ProbeAsync(string ipAddress, int port, int commKey, CancellationToken cancellationToken = default)
    {
        // Fast reachability gate first, then the resilient handshake. This keeps a /24 scan quick
        // while still classifying CommKey-protected and TCP-open-but-unverified endpoints.
        var portOpen = ZkProtocolClient.IsPortOpen(ipAddress, port, 600);
        var udpAnswered = false;
        if (!portOpen)
        {
            udpAnswered = UdpProbe(ipAddress, port, 500);
            if (!udpAnswered)
            {
                return new DiscoveredDevice { IpAddress = ipAddress, Port = port, Status = DiscoveryStatus.Unreachable };
            }
        }

        // A single session only: most terminals accept one connection at a time, so the handshake
        // and the identity read must run on the same TCP/UDP session.
        var engine = new ZkNativeEngine(_probeTimeoutMs);
        var result = await engine
            .ReadSnapshotAsync(new ZkEndpoint(ipAddress, port, commKey), cancellationToken)
            .ConfigureAwait(false);

        if (result.Success && result.Value is { } snapshot && snapshot.IsIdentified)
        {
            return new DiscoveredDevice
            {
                IpAddress = ipAddress,
                Port = port,
                Responded = true,
                PortOpen = true,
                Status = DiscoveryStatus.Verified,
                Snapshot = snapshot,
                Transport = udpAnswered ? "UDP" : "TCP"
            };
        }

        // A port that accepts the connection but returns no serial/model is not a usable terminal:
        // it must not be presented as "verified" in the interface.
        var unnamed = result.Success
            ? $"Port {port} answered but returned no device identity (serial, model, firmware all empty)."
            : result.Message;

        if (NeedsCommKey(result.Message))
        {
            return new DiscoveredDevice
            {
                IpAddress = ipAddress,
                Port = port,
                Responded = false,
                PortOpen = true,
                Status = DiscoveryStatus.CommKeyRequired,
                Error = "The terminal asked for a CommKey: enter the device communication key and scan again.",
                Transport = portOpen ? "TCP" : "UDP"
            };
        }

        var raw = TryRawTcpProbe(ipAddress, port, out var rawDetail);
        return new DiscoveredDevice
        {
            IpAddress = ipAddress,
            Port = port,
            Responded = false,
            PortOpen = true,
            Status = DiscoveryStatus.PortOpenUnverified,
            Error = $"{unnamed} | Raw TCP: {(raw ? "answered" : "no answer")} - {rawDetail}",
            Transport = portOpen ? "TCP" : "UDP"
        };
    }

    private static bool NeedsCommKey(string message) =>
        message.Contains("COMM key", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("0x2005", StringComparison.OrdinalIgnoreCase);
    /// <summary>Sends the bare 8-byte CONNECT without the 4-byte TCP frame header.</summary>
    private static bool TryRawTcpProbe(string ipAddress, int port, out string detail)
    {
        detail = string.Empty;
        if (!IPAddress.TryParse(ipAddress, out var address))
        {
            return false;
        }

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var connect = socket.BeginConnect(new IPEndPoint(address, port), null, null);
            if (!connect.AsyncWaitHandle.WaitOne(700))
            {
                socket.Close();
                detail = "connect timeout";
                return false;
            }

            socket.EndConnect(connect);
            socket.ReceiveTimeout = 800;
            var packet = ZkPacket.Build(ZkCommand.Connect, 0, ushort.MaxValue - 1, []);
            socket.Send(packet);

            var buffer = new byte[8];
            var read = socket.Receive(buffer);
            if (read < 8)
            {
                detail = $"short reply ({read} bytes)";
                return false;
            }

            var code = ZkPacket.ReadUInt16(buffer, 0);
            detail = $"0x{code:X4}";
            return code is ZkCommand.AckOk or ZkCommand.AckData or ZkCommand.AckUnauth or ZkCommand.AckError;
        }
        catch (SocketException ex)
        {
            detail = ex.SocketErrorCode.ToString();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool UdpProbe(string ipAddress, int port, int timeoutMs)
    {
        try
        {
            using var client = new UdpClient(AddressFamily.InterNetwork);
            client.Client.ReceiveTimeout = Math.Max(150, timeoutMs);
            client.Connect(ipAddress, port);
            var packet = ZkPacket.Build(ZkCommand.Connect, 0, ushort.MaxValue - 1, []);
            client.Send(packet, packet.Length);
            var remote = new IPEndPoint(IPAddress.Any, 0);
            var reply = client.Receive(ref remote);
            if (reply.Length < 8)
            {
                return false;
            }

            var code = ZkPacket.ReadUInt16(reply, 0);
            return code is ZkCommand.AckOk or ZkCommand.AckUnauth or ZkCommand.AckData;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static (uint Start, uint End)? ParseCidr(string? cidr)
    {
        if (string.IsNullOrWhiteSpace(cidr) || !cidr.Contains('/', StringComparison.Ordinal))
        {
            return null;
        }

        var parts = cidr.Split('/');
        if (!IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var prefix))
        {
            return null;
        }

        var mask = prefix >= 32 ? uint.MaxValue : ~(uint.MaxValue >> prefix);
        var start = ToUInt32(network) & mask;
        return (start, start | ~mask);
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static string FromUInt32(uint value) =>
        $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";

    private static int PrefixLength(uint mask)
    {
        var count = 0;
        for (var i = 31; i >= 0; i--)
        {
            if ((mask & (1u << i)) != 0)
            {
                count++;
            }
            else
            {
                break;
            }
        }

        return count;
    }
}
