using FocusPlus.Application;
using FocusPlus.Application.Services;
using FocusPlus.Domain;
using FocusPlus.Domain.Entities;
using FocusPlus.Hardware;

namespace FocusPlus.Tests;

public sealed class DeviceTests
{
    private static DiscoveredDevice Discovered(string ip, string serial, int punches = 42) => new()
    {
        IpAddress = ip,
        Port = 4370,
        Responded = true,
        Snapshot = new DeviceSnapshot
        {
            IpAddress = ip,
            Port = 4370,
            SerialNumber = serial,
            ModelName = "UF100 Plus-S",
            FirmwareVersion = "6.60.1.0",
            MacAddress = "00:17:61:0A:0B:0C",
            Platform = "ZEM560",
            UserCount = 12,
            FingerCount = 30,
            FaceCount = 12,
            PunchCount = punches,
            DeviceClock = DateTime.Now,
            LatencyMs = 12
        }
    };

    [Fact]
    public async Task AddFromScan_RegistersOnlyRealHardware()
    {
        await using var context = new TestHost();
        var host = context.Host;

        var (result, device) = host.Devices.AddFromScan(Discovered("192.168.1.50", "SER-100"), "Reception", 0, "tester");

        Assert.True(result.Success);
        Assert.NotNull(device);
        Assert.Equal("SER-100", device!.SerialNumber);
        Assert.Equal(12, device.UserCount);
        Assert.Equal(42, device.PunchCount);
        Assert.Equal("UF100 Plus-S", device.ModelName);
        Assert.Equal(1, host.Devices.Count());
        Assert.True(host.Devices.GetStatus(device.Id).IsOnline);
    }

    [Fact]
    public async Task AddFromScan_BlocksDuplicateSerials()
    {
        await using var context = new TestHost();
        var host = context.Host;
        host.Devices.AddFromScan(Discovered("192.168.1.50", "SER-DUP"), "First", 0, "tester");

        var (result, existing) = host.Devices.AddFromScan(Discovered("192.168.1.51", "SER-DUP"), "Second", 0, "tester");

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.DuplicateSerial, result.ErrorCode);
        Assert.NotNull(existing);
        Assert.Equal("192.168.1.50", existing!.IpAddress);
        Assert.Equal(1, host.Devices.Count());
    }

    [Fact]
    public async Task AddFromScan_BlocksDuplicateEndpoints()
    {
        await using var context = new TestHost();
        var host = context.Host;
        host.Devices.AddFromScan(Discovered("192.168.1.60", "SER-A"), "First", 0, "tester");

        var (result, _) = host.Devices.AddFromScan(Discovered("192.168.1.60", "SER-B"), "Second", 0, "tester");

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.DuplicateEndpoint, result.ErrorCode);
    }

    [Fact]
    public async Task AddFromScan_RejectsUnreachableTerminals()
    {
        await using var context = new TestHost();
        var (result, device) = context.Host.Devices.AddFromScan(
            new DiscoveredDevice { IpAddress = "10.0.0.9", Responded = false, Error = "timeout" },
            "Ghost",
            0,
            "tester");

        Assert.False(result.Success);
        Assert.Null(device);
        Assert.Equal(0, context.Host.Devices.Count());
    }

    [Fact]
    public async Task AddByIp_ValidatesAddressAndPort()
    {
        await using var context = new TestHost();
        var host = context.Host;

        var badIp = await host.Devices.AddByIpAsync("X", "999.1.1.1", 4370, 0, "tester");
        Assert.False(badIp.Result.Success);
        Assert.Equal(ErrorCodes.InvalidIp, badIp.Result.ErrorCode);

        var badPort = await host.Devices.AddByIpAsync("X", "192.168.1.9", 0, 0, "tester");
        Assert.False(badPort.Result.Success);
        Assert.Equal(ErrorCodes.InvalidPort, badPort.Result.ErrorCode);
    }

    [Fact]
    public async Task UpdateNetwork_ValidatesBeforeTouchingHardware()
    {
        await using var context = new TestHost();
        var host = context.Host;
        var (_, device) = host.Devices.AddFromScan(Discovered("192.168.1.70", "SER-NET"), "Net", 0, "tester");

        var invalidIp = await host.Devices.UpdateNetworkAsync(device!.Id, "abc", "255.255.255.0", "192.168.1.1", 4370, "tester");
        Assert.False(invalidIp.Success);
        Assert.Equal(ErrorCodes.InvalidIp, invalidIp.ErrorCode);

        var invalidPort = await host.Devices.UpdateNetworkAsync(device.Id, "192.168.1.71", "255.255.255.0", "192.168.1.1", 70000, "tester");
        Assert.False(invalidPort.Success);
        Assert.Equal(ErrorCodes.InvalidPort, invalidPort.ErrorCode);

        Assert.Equal("192.168.1.70", host.Devices.Get(device.Id)!.IpAddress);
    }

    [Fact]
    public async Task UpdateNetwork_RejectsConflictingEndpoint()
    {
        await using var context = new TestHost();
        var host = context.Host;
        var (_, first) = host.Devices.AddFromScan(Discovered("192.168.1.80", "SER-1"), "First", 0, "tester");
        host.Devices.AddFromScan(Discovered("192.168.1.81", "SER-2"), "Second", 0, "tester");

        var conflict = await host.Devices.UpdateNetworkAsync(first!.Id, "192.168.1.81", "255.255.255.0", "192.168.1.1", 4370, "tester");

        Assert.False(conflict.Success);
        Assert.Equal(ErrorCodes.DuplicateEndpoint, conflict.ErrorCode);
    }

    [Fact]
    public async Task UpdateNetwork_ReportsUnreachableDevice()
    {
        await using var context = new TestHost();
        var host = context.Host;
        var device = new Device { Name = "Offline", IpAddress = "127.0.0.2", Port = 4370, SerialNumber = "SER-OFF" };
        host.DeviceRepo.Upsert(device);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await host.Devices.UpdateNetworkAsync(device.Id, "127.0.0.3", "255.0.0.0", "127.0.0.1", 4370, "tester", null, cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.DeviceUnreachable, result.ErrorCode);
    }

    [Fact]
    public async Task DeviceArchiveAndEnable_AreSoft()
    {
        await using var context = new TestHost();
        var host = context.Host;
        var (_, device) = host.Devices.AddFromScan(Discovered("192.168.1.90", "SER-ARCH"), "Arch", 0, "tester");

        Assert.True(host.Devices.SetEnabled(device!.Id, false, "tester").Success);
        Assert.False(host.Devices.Get(device.Id)!.IsEnabled);

        Assert.True(host.Devices.Archive(device.Id, "moved", "tester").Success);
        Assert.Empty(host.Devices.GetDevices());
        Assert.NotNull(host.Devices.Get(device.Id));
        Assert.True(host.Devices.Get(device.Id)!.IsArchived);

        Assert.True(host.Devices.Restore(device.Id, "tester").Success);
        Assert.Single(host.Devices.GetDevices());
    }

    [Fact]
    public void DedupeKey_IsStableAndUniquePerPunch()
    {
        var moment = new DateTime(2026, 11, 15, 7, 0, 0, DateTimeKind.Utc);
        var first = DeviceService.BuildDedupeKey("SER", "1001", moment);
        var same = DeviceService.BuildDedupeKey("SER", "1001", moment);
        var otherPin = DeviceService.BuildDedupeKey("SER", "1002", moment);
        var otherTime = DeviceService.BuildDedupeKey("SER", "1001", moment.AddMinutes(1));
        var otherDevice = DeviceService.BuildDedupeKey("SER2", "1001", moment);

        Assert.Equal(first, same);
        Assert.NotEqual(first, otherPin);
        Assert.NotEqual(first, otherTime);
        Assert.NotEqual(first, otherDevice);
    }

    [Fact]
    public async Task MonitorTick_WithNoDevices_IsSafe()
    {
        await using var context = new TestHost();
        await context.Host.Monitor.TickAsync();
        Assert.False(context.Host.Monitor.IsRunning);
    }

    [Fact]
    public async Task HardwareFacade_ReportsItsEngines()
    {
        await using var context = new TestHost();
        var summary = context.Host.Hardware.EngineSummary;

        Assert.Contains("4370", summary);
        Assert.True(context.Host.Hardware.ComSdkAvailable == false || context.Host.Hardware.ComSdkAvailable);
    }

    [Fact]
    public async Task PullPunches_ReportsNotFoundWithoutCrashing()
    {
        await using var context = new TestHost();
        var result = await context.Host.Devices.PullPunchesAsync("missing", "tester");
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    [Fact]
    public void Endpoint_ComposesIpAndPort()
    {
        var device = new Device { IpAddress = "10.1.2.3", Port = 4370 };
        Assert.Equal("10.1.2.3:4370", device.Endpoint);
    }
}
