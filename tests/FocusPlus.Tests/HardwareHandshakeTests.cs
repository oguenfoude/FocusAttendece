using System.Net;
using System.Net.Sockets;
using FocusPlus.Application;
using FocusPlus.Application.ViewModels;
using FocusPlus.Domain;
using FocusPlus.Hardware;

namespace FocusPlus.Tests;

public sealed class HardwareHandshakeTests
{
    [Fact]
    public void ZkFrameReader_HandlesFragmentedTcpReads()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var packet = ZkPacket.Build(ZkCommand.AckOk, 0x1234, 0x4321, payload);
        var frame = ZkPacket.FrameTcp(packet);

        using var trickle = new TrickleStream(frame, 1);
        var read = ZkFrameReader.ReadFrame(trickle);

        Assert.Equal(packet, read);
        Assert.Equal(0x1234, ZkPacket.ReadUInt16(read, 4));
        Assert.Equal(payload, read[8..]);
    }

    [Fact]
    public void ZkFrameReader_RejectsInvalidMagic()
    {
        using var stream = new MemoryStream([9, 9, 9, 9, 8, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Throws<ZkException>(() => ZkFrameReader.ReadFrame(stream));
    }

    [Fact]
    public void Client_SendsIncrementedReplyId_OnFirstConnect()
    {
        // Real terminals ignore packets whose transmitted reply id is stale.
        // zkemsdk computes the checksum with 65534 but sends 0 (fold at 65535).
        using var server = new FakeZkServer(requireCommKey: false);
        using var client = new ZkProtocolClient(
            new ZkEndpoint("127.0.0.1", server.Port), udp: false, timeoutMs: 5000);

        client.Connect();

        Assert.Equal((ushort?)0, server.FirstConnectReplyId);
    }

    [Fact]
    public void Client_Authenticates_WhenServerRepliesAckUnauth()
    {
        using var server = new FakeZkServer(requireCommKey: true);
        using var client = new ZkProtocolClient(new ZkEndpoint("127.0.0.1", server.Port, 12345), udp: false, timeoutMs: 5000);

        client.Connect();
        var serial = client.ReadOption("~SerialNumber");

        Assert.True(server.ReceivedAuth, "the client must send CMD_AUTH after CMD_ACK_UNAUTH");
        Assert.Equal("FAKE-SN-001", serial);
    }

    [Fact]
    public void Client_Fails_WhenCommKeyIsRequiredButMissing()
    {
        using var server = new FakeZkServer(requireCommKey: true);
        using var client = new ZkProtocolClient(new ZkEndpoint("127.0.0.1", server.Port, 0), udp: false, timeoutMs: 5000);

        var exception = Assert.Throws<ZkException>(() => client.Connect());

        Assert.Equal(ZkCommand.AckUnauth, exception.Code);
        Assert.False(server.ReceivedAuth);
    }

    [Fact]
    public async Task Scan_ClassifiesCommKeyProtectedTerminal()
    {
        using var server = new FakeZkServer(requireCommKey: true);
        var scanner = new LanScanner(perSubnetLimit: 1, probeTimeoutMs: 2500);

        var result = await scanner.ProbeAsync("127.0.0.1", server.Port, 0);

        Assert.True(result.PortOpen);
        Assert.False(result.Responded);
        Assert.Equal(DiscoveryStatus.CommKeyRequired, result.Status);
    }

    [Fact]
    public async Task Scan_VerifiesAnUnprotectedTerminal()
    {
        using var server = new FakeZkServer(requireCommKey: false);
        var scanner = new LanScanner(perSubnetLimit: 1, probeTimeoutMs: 2500);

        var result = await scanner.ProbeAsync("127.0.0.1", server.Port, 0);

        Assert.True(result.Responded);
        Assert.Equal(DiscoveryStatus.Verified, result.Status);
        Assert.Equal("FAKE-SN-001", result.SerialNumber);
        Assert.Equal("FakeZK-1000", result.Snapshot?.ModelName);
    }

    [Fact]
    public void BuildHostRange_ProducesExpectedCount()
    {
        var hosts = LanScanner.BuildHostRange("192.168.1.10", "192.168.1.14");
        Assert.Equal(5, hosts.Count);
        Assert.Equal("192.168.1.10", hosts[0].ToString());
        Assert.Equal("192.168.1.14", hosts[^1].ToString());
    }

    [Fact]
    public void LocalNetworks_AreEnumeratedWithCidr()
    {
        var networks = LanScanner.GetLocalNetworks();

        Assert.NotNull(networks);
        Assert.All(networks, n => Assert.Contains("/", n.Cidr, StringComparison.Ordinal));
        Assert.All(networks.Where(n => n.PrefixLength < 32), n => Assert.True(n.HostCount > 0));
    }

    [Fact]
    public async Task TcpOpenButUnverifiedHost_CanBeRegistered()
    {
        await using var context = new TestHost();

        var discovered = new DiscoveredDevice
        {
            IpAddress = "192.168.1.201",
            Port = 4370,
            PortOpen = true,
            Responded = false,
            Status = DiscoveryStatus.PortOpenUnverified,
            Error = "Port 4370 reachable but ZK handshake failed."
        };

        var (result, device) = context.Host.Devices.AddFromScan(discovered, "Reception (unverified)", 0, "tester");

        Assert.True(result.Success);
        Assert.NotNull(device);
        Assert.Null(device.SerialNumber);
        Assert.Equal("192.168.1.201", device.IpAddress);
        Assert.Contains("open", device.Note ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DeviceConnectivity.Unknown, context.Host.Devices.GetStatus(device.Id).Connectivity);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task UnverifiedDeviceVm_CanAdd_ButUnreachableCannot()
    {
        await using var context = new TestHost();

        var vm = new DevicesViewModel(context.Host, new NullMessenger());
        var reachable = new DiscoveredDeviceVm(
            new DiscoveredDevice
            {
                IpAddress = "192.168.1.202",
                PortOpen = true,
                Responded = false,
                Status = DiscoveryStatus.PortOpenUnverified
            },
            alreadyRegistered: false,
            arabic: false);
        var unreachable = new DiscoveredDeviceVm(
            new DiscoveredDevice { IpAddress = "10.0.0.9", PortOpen = false, Status = DiscoveryStatus.Unreachable },
            alreadyRegistered: false,
            arabic: false);

        Assert.True(reachable.CanAdd);
        Assert.False(unreachable.CanAdd);
        Assert.True(reachable.StateText.Contains("Port", StringComparison.OrdinalIgnoreCase));
        await Task.CompletedTask;
    }

    private sealed class TrickleStream(byte[] data, int chunkSize) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = Math.Min(Math.Min(chunkSize, count), data.Length - _offset);
            if (available <= 0)
            {
                return 0;
            }

            Array.Copy(data, _offset, buffer, offset, available);
            _offset += available;
            return available;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Minimal ZKTeco server used to validate the handshake sequence.</summary>
    private sealed class FakeZkServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly bool _requireCommKey;
        private readonly Task _loop;

        public FakeZkServer(bool requireCommKey)
        {
            _requireCommKey = requireCommKey;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public bool ReceivedAuth { get; private set; }

        public ushort? FirstConnectReplyId { get; private set; }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            await using var stream = client.GetStream();
            ushort session = 0;
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var packet = ZkFrameReader.ReadFrame(stream);
                    var command = ZkPacket.ReadUInt16(packet, 0);
                    session = ZkPacket.ReadUInt16(packet, 4);
                    var replyId = ZkPacket.ReadUInt16(packet, 6);
                    var payload = packet.Length > 8 ? packet[8..] : [];

                    switch (command)
                    {
                        case ZkCommand.Connect:
                            FirstConnectReplyId ??= replyId;
                            session = 0x2222;
                            await ReplyAsync(stream, _requireCommKey ? ZkCommand.AckUnauth : ZkCommand.AckOk, session, replyId).ConfigureAwait(false);
                            break;

                        case ZkCommand.Auth:
                            ReceivedAuth = true;
                            await ReplyAsync(stream, ZkCommand.AckOk, session, replyId).ConfigureAwait(false);
                            break;

                        case ZkCommand.OptionsRrq:
                            var option = System.Text.Encoding.ASCII.GetString(payload).TrimEnd('\0');
                            var value = option switch
                            {
                                "~SerialNumber" => "FAKE-SN-001",
                                "~DeviceName" => "FakeZK-1000",
                                "~Platform" => "ZEM-FAKE",
                                "MAC" => "00:11:22:33:44:55",
                                _ => string.Empty
                            };
                            await ReplyAsync(stream, ZkCommand.AckOk, session, replyId,
                                System.Text.Encoding.ASCII.GetBytes(option + "=" + value + "\0")).ConfigureAwait(false);
                            break;

                        case ZkCommand.GetVersion:
                            await ReplyAsync(stream, ZkCommand.AckOk, session, replyId,
                                System.Text.Encoding.ASCII.GetBytes("6.60.0-fake\0")).ConfigureAwait(false);
                            break;

                        case ZkCommand.GetTime:
                            var time = new byte[4];
                            ZkPacket.WriteUInt32(time, 0, ZkPacket.EncodeTime(DateTime.Now));
                            await ReplyAsync(stream, ZkCommand.AckOk, session, replyId, time).ConfigureAwait(false);
                            break;

                        case ZkCommand.GetFreeSizes:
                            var sizes = new byte[92];
                            ZkPacket.WriteInt32(sizes, 16, 12);
                            ZkPacket.WriteInt32(sizes, 24, 30);
                            ZkPacket.WriteInt32(sizes, 32, 420);
                            ZkPacket.WriteInt32(sizes, 60, 1000);
                            ZkPacket.WriteInt32(sizes, 64, 2000);
                            await ReplyAsync(stream, ZkCommand.AckOk, session, replyId, sizes).ConfigureAwait(false);
                            break;

                        case ZkCommand.Exit:
                            await ReplyAsync(stream, ZkCommand.AckOk, session, replyId).ConfigureAwait(false);
                            return;

                        default:
                            await ReplyAsync(stream, ZkCommand.AckOk, session, replyId).ConfigureAwait(false);
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is ZkException or IOException or ObjectDisposedException)
            {
                // Client disconnected.
            }
        }

        private static async Task ReplyAsync(Stream stream, ushort code, ushort session, ushort replyId, byte[]? payload = null)
        {
            var packet = ZkPacket.Build(code, session, replyId, payload ?? []);
            await stream.WriteAsync(ZkPacket.FrameTcp(packet)).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }

            _cts.Dispose();
        }
    }
}
