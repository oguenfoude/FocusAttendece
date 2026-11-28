using FocusPlus.Domain.Entities;
using FocusPlus.Hardware;

// Cross-platform field diagnostics for ZKTeco terminals on port 4370.
//   zk-probe <ip> [--port 4370] [--key 12345]
//   zk-probe --scan [--cidr 192.168.100.0/24]
//   zk-probe --dump <ip> [--key 12345]
//   zk-probe --networks

var port = 4370;
var key = 0;
var mode = "probe";
string? target = null;
string? cidr = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--port" when i + 1 < args.Length:
            port = int.Parse(args[++i]);
            break;
        case "--key" when i + 1 < args.Length:
            key = int.Parse(args[++i]);
            break;
        case "--cidr" when i + 1 < args.Length:
            cidr = args[++i];
            break;
        case "--scan":
            mode = "scan";
            break;
        case "--dump":
            mode = "dump";
            break;
        case "--networks":
            mode = "networks";
            break;
        default:
            target = args[i];
            break;
    }
}

Console.WriteLine("FocusPlus ZKTeco probe — native protocol, cross-platform");
Console.WriteLine();

if (mode == "networks")
{
    var networks = LanScanner.GetLocalNetworks();
    Console.WriteLine($"Local networks ({networks.Count}):");
    foreach (var n in networks)
    {
        Console.WriteLine($"  {n.InterfaceName,-24} {n.Address,-16}/{n.PrefixLength}  {n.Cidr,-20} {n.HostCount} hosts");
    }
    return;
}

var hardware = new ZkHardwareService();

if (mode == "scan")
{
    var hosts = LanScanner.BuildHostList(254, cidr);
    Console.WriteLine($"Scanning {hosts.Count} addresses on TCP {port} …");
    var found = await hardware.ScanHostsAsync(hosts, 250);
    if (found.Count == 0)
    {
        Console.WriteLine("No responding terminal found.");
        return;
    }

    foreach (var d in found)
    {
        Console.WriteLine($"  {d.IpAddress}:{d.Port}  {d.HandshakeText,-36} {d.Transport}");
        Console.WriteLine($"      serial={d.SerialNumber ?? "-"}  model={d.DisplayModel ?? "-"}");
        if (!string.IsNullOrWhiteSpace(d.Error))
        {
            Console.WriteLine($"      note: {d.Error}");
        }
    }
    return;
}

if (string.IsNullOrWhiteSpace(target))
{
    Console.Error.WriteLine("Usage: zk-probe <ip> [--port N] [--key N] | --scan [--cidr x.x.x.x/24] | --dump <ip> | --networks");
    Environment.ExitCode = 2;
    return;
}

Console.WriteLine($"Target      : {target}:{port}");
Console.WriteLine($"CommKey     : {key}");
Console.WriteLine();

// Field terminals intermittently drop the first TCP session. Retry a few times with a
// short pause; any non-Unreachable status is already proof the endpoint is alive.
var device = await hardware.ProbeAsync(target, port, key);
for (var attempt = 2; attempt <= 4 && device.Status == DiscoveryStatus.Unreachable; attempt++)
{
    Console.WriteLine($"Attempt {attempt - 1} unreachable — retrying in 400 ms …");
    await Task.Delay(TimeSpan.FromMilliseconds(400));
    device = await hardware.ProbeAsync(target, port, key);
}

IReadOnlyList<string> dumpLines = mode == "dump"
    ? await hardware.DumpSdkInfoAsync(new ZkEndpoint(target, port, key))
    : Array.Empty<string>();

// A successful native dump after failed probes is live device evidence, not a fabricated device.
// Only identity values actually returned by the terminal can promote the result to verified.
var dumpVerified = device.Status == DiscoveryStatus.Unreachable && HasDumpIdentity(dumpLines);
var serial = dumpVerified ? DumpValue(dumpLines, "Serial") : device.SerialNumber;
var model = dumpVerified ? DumpValue(dumpLines, "Model") : device.DisplayModel;
var firmware = dumpVerified ? DumpValue(dumpLines, "Firmware") : device.Snapshot?.FirmwareVersion;

Console.WriteLine($"Discovery   : {(dumpVerified
    ? "Verified ZKTeco — device identity recovered by DumpSdkInfoAsync (probe reported Unreachable)"
    : device.HandshakeText)}");
Console.WriteLine($"Port open   : {device.PortOpen}");
Console.WriteLine($"Transport   : {device.Transport}");
Console.WriteLine($"Serial      : {serial ?? "-"}");
Console.WriteLine($"Model       : {model ?? "-"}");
if (firmware is not null || device.Snapshot is not null)
{
    Console.WriteLine($"Firmware    : {firmware ?? "-"}");
}
if (device.Snapshot is { } s)
{
    Console.WriteLine($"MAC         : {s.MacAddress ?? "-"}");
    Console.WriteLine($"Platform    : {s.Platform ?? "-"}");
    Console.WriteLine($"Clock       : {s.DeviceClock:O}");
    Console.WriteLine($"Users/Punch : {s.UserCount} / {s.PunchCount}");
}
if (!string.IsNullOrWhiteSpace(device.Error))
{
    Console.WriteLine($"Note        : {device.Error}");
}

if (mode == "dump")
{
    Console.WriteLine();
    Console.WriteLine("Device diagnostics:");
    foreach (var line in dumpLines)
    {
        Console.WriteLine($"  {line}");
    }
}

Environment.ExitCode = device.Status == DiscoveryStatus.Unreachable && !dumpVerified ? 1 : 0;

static string? DumpValue(IReadOnlyList<string> lines, string key)
{
    foreach (var line in lines)
    {
        var separator = line.IndexOf('=');
        if (separator < 0 || !line[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var value = line[(separator + 1)..].Trim();
        return string.IsNullOrWhiteSpace(value)
            || value == "-"
            || value.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                ? null
                : value;
    }

    return null;
}

static bool HasDumpIdentity(IReadOnlyList<string> lines) =>
    DumpValue(lines, "Serial") is not null
    || DumpValue(lines, "Model") is not null
    || DumpValue(lines, "Firmware") is not null;
