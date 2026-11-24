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

var device = await hardware.ProbeAsync(target, port, key);
Console.WriteLine($"Discovery   : {device.HandshakeText}");
Console.WriteLine($"Port open   : {device.PortOpen}");
Console.WriteLine($"Transport   : {device.Transport}");
Console.WriteLine($"Serial      : {device.SerialNumber ?? "-"}");
Console.WriteLine($"Model       : {device.DisplayModel ?? "-"}");
if (device.Snapshot is { } s)
{
    Console.WriteLine($"Firmware    : {s.FirmwareVersion ?? "-"}");
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
    foreach (var line in await hardware.DumpSdkInfoAsync(new ZkEndpoint(target, port, key)))
    {
        Console.WriteLine($"  {line}");
    }
}
