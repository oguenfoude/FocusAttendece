using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FocusPlus.Hardware;

/// <summary>
/// Native ZKTeco protocol client (TCP with UDP fallback). Implements the real port-4370 binary
/// protocol used by UF100 Plus-S and every ZK standalone terminal, with no external SDK required.
/// </summary>
public sealed class ZkProtocolClient : IDisposable
{
    private const int MaxChunk = 0xFFC0;
    private readonly ZkEndpoint _endpoint;
    private readonly bool _udp;
    private readonly int _timeoutMs;
    private readonly Socket _socket;
    private NetworkStream? _stream;
    private ushort _sessionId;
    private ushort _replyId = ushort.MaxValue - 1;
    private bool _connected;
    private bool _disposed;

    public ZkProtocolClient(ZkEndpoint endpoint, bool udp = false, int timeoutMs = 6000)
    {
        _endpoint = endpoint;
        _udp = udp;
        _timeoutMs = timeoutMs;
        _socket = new Socket(AddressFamily.InterNetwork, udp ? SocketType.Dgram : SocketType.Stream, udp ? ProtocolType.Udp : ProtocolType.Tcp)
        {
            ReceiveTimeout = timeoutMs,
            SendTimeout = timeoutMs
        };

        if (!udp)
        {
            _socket.NoDelay = true;
        }
    }

    public bool IsConnected => _connected;

    /// <summary>Quick TCP reachability test without a protocol handshake (hard timeout).</summary>
    public static bool IsPortOpen(string ipAddress, int port, int timeoutMs = 400)
    {
        if (!IPAddress.TryParse(ipAddress, out var address))
        {
            return false;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                var result = socket.BeginConnect(new IPEndPoint(address, port), null, null);
                if (!result.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    socket.Close();
                }
                else
                {
                    socket.EndConnect(result);
                    return socket.Connected;
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // Retry once: the device may still be completing the previous accept.
            }
            finally
            {
                socket.Dispose();
            }
        }

        return false;
    }

    public void Connect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var endpoint = new IPEndPoint(IPAddress.Parse(_endpoint.IpAddress), _endpoint.Port);
        if (_udp)
        {
            _socket.Connect(endpoint);
        }
        else
        {
            using var cts = new CancellationTokenSource(_timeoutMs);
            try
            {
                _socket.ConnectAsync(endpoint, cts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw new ZkException($"Timeout while connecting to {_endpoint}.");
            }
            catch (SocketException ex)
            {
                throw new ZkException($"Cannot reach {_endpoint}: {ex.SocketErrorCode}.");
            }
        }

        _sessionId = 0;
        _replyId = ushort.MaxValue - 1;

        if (!_udp)
        {
            _stream = new NetworkStream(_socket, ownsSocket: false);
        }

        var (code, _) = Send(ZkCommand.Connect, []);
        if (code == ZkCommand.AckUnauth)
        {
            if (_endpoint.CommKey == 0)
            {
                throw new ZkException("The terminal requires a COMM key but none was configured.", code);
            }

            var key = ZkPacket.MakeCommKey(_endpoint.CommKey, _sessionId);
            (code, _) = Send(ZkCommand.Auth, key);
        }

        if (code is not (ZkCommand.AckOk or ZkCommand.AckData))
        {
            throw new ZkException($"The terminal refused the connection (0x{code:X4}).", code);
        }

        _connected = true;
    }

    public void Disconnect()
    {
        if (!_connected)
        {
            return;
        }

        try
        {
            Send(ZkCommand.Exit, []);
        }
        catch (ZkException)
        {
            // The device may drop the socket immediately after the exit command.
        }
        finally
        {
            _connected = false;
        }
    }

    public (ushort Code, byte[] Data) Send(ushort command, ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // zkemsdk quirk: the checksum is computed with the CURRENT reply id, while the
        // transmitted packet carries the NEXT id. Folding is at USHRT_MAX (65535),
        // so 65534 -> 0 (not 1). Getting either half wrong makes the terminal ignore us.
        var packet = ZkPacket.Build(command, _sessionId, _replyId, payload);
        var nextReplyId = unchecked((ushort)(_replyId + 1));
        if (nextReplyId >= ushort.MaxValue)
        {
            nextReplyId -= ushort.MaxValue;
        }

        ZkPacket.WriteUInt16(packet, 6, nextReplyId);
        _replyId = nextReplyId;

        try
        {
            if (_udp)
            {
                _socket.Send(packet);
            }
            else
            {
                _socket.Send(ZkPacket.FrameTcp(packet));
            }

            var raw = _udp ? ReceiveUdp() : ReceiveTcp();
            if (raw.Length < 8)
            {
                throw new ZkException($"Truncated reply from {_endpoint}.");
            }

            var code = ZkPacket.ReadUInt16(raw, 0);
            _sessionId = ZkPacket.ReadUInt16(raw, 4);
            _replyId = ZkPacket.ReadUInt16(raw, 6);
            return (code, raw.Length > 8 ? raw[8..] : []);
        }
        catch (SocketException ex)
        {
            throw new ZkException($"Network error with {_endpoint}: {ex.SocketErrorCode}.", null);
        }
    }

    private byte[] ReceiveTcp()
    {
        try
        {
            // ZkFrameReader tolerates fragments: the header and body may arrive in separate reads.
            return ZkFrameReader.ReadFrame(_stream!);
        }
        catch (IOException ex)
        {
            throw new ZkException($"Network error with {_endpoint}: {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
            throw new ZkException($"The connection to {_endpoint} was closed.");
        }
    }

    private byte[] ReceiveUdp()
    {
        var buffer = new byte[64 * 1024];
        var received = _socket.Receive(buffer);
        return buffer[..received];
    }


    /// <summary>Buffered read (CMD_PREPARE_BUFFER / CMD_READ_BUFFER), with a legacy fallback.</summary>
    public byte[] ReadBuffer(ushort command, int fct = 0, int ext = 0)
    {
        try
        {
            return ReadBufferV2(command, fct, ext);
        }
        catch (ZkException) when (_connected)
        {
            return ReadBufferLegacy(command, fct);
        }
    }

    private byte[] ReadBufferV2(ushort command, int fct, int ext)
    {
        var payload = new byte[11];
        payload[0] = 1;
        ZkPacket.WriteUInt16(payload, 1, command);
        ZkPacket.WriteInt32(payload, 3, fct);
        ZkPacket.WriteInt32(payload, 7, ext);

        var (code, data) = Send(ZkCommand.PrepareBuffer, payload);
        if (code == ZkCommand.AckData)
        {
            return data;
        }

        if (code != ZkCommand.PrepareData)
        {
            throw new ZkException($"Buffered read refused (0x{code:X4}).", code);
        }

        if (data.Length < 5)
        {
            return [];
        }

        var size = ZkPacket.ReadInt32(data, 1);
        if (size <= 0)
        {
            TryFreeData();
            return [];
        }

        var result = new byte[size];
        var offset = 0;
        while (offset < size)
        {
            var chunkSize = Math.Min(MaxChunk, size - offset);
            var request = new byte[8];
            ZkPacket.WriteInt32(request, 0, offset);
            ZkPacket.WriteInt32(request, 4, chunkSize);
            var (chunkCode, chunkData) = Send(ZkCommand.ReadBuffer, request);
            if (chunkCode is not (ZkCommand.AckData or ZkCommand.AckOk))
            {
                throw new ZkException($"Chunk read refused (0x{chunkCode:X4}).", chunkCode);
            }

            var copy = Math.Min(chunkData.Length, chunkSize);
            chunkData.AsSpan(0, copy).CopyTo(result.AsSpan(offset));
            offset += chunkSize;
        }

        TryFreeData();
        return result;
    }

    private byte[] ReadBufferLegacy(ushort command, int fct)
    {
        var payload = command is ZkCommand.UserTempRrq or ZkCommand.DbRrq
            ? new[] { (byte)(fct & 0xFF), (byte)((fct >> 8) & 0xFF) }
            : [];

        var (code, data) = Send(command, payload);
        if (code == ZkCommand.AckData)
        {
            return data;
        }

        if (code != ZkCommand.PrepareData)
        {
            throw new ZkException($"Legacy read refused (0x{code:X4}).", code);
        }

        var size = data.Length >= 4 ? ZkPacket.ReadInt32(data, 0) : 0;
        using var stream = new MemoryStream(size > 0 ? size : 4096);
        if (data.Length > 4)
        {
            stream.Write(data.AsSpan(4));
        }

        while (stream.Length < size)
        {
            var raw = _udp ? ReceiveUdp() : ReceiveTcp();
            if (raw.Length < 8)
            {
                break;
            }

            var packetCode = ZkPacket.ReadUInt16(raw, 0);
            if (packetCode == ZkCommand.Data)
            {
                stream.Write(raw.AsSpan(8));
            }
            else if (packetCode == ZkCommand.AckOk)
            {
                break;
            }
            else
            {
                throw new ZkException($"Unexpected legacy packet 0x{packetCode:X4}.", packetCode);
            }
        }

        return stream.ToArray();
    }

    public void TryFreeData()
    {
        try
        {
            Send(ZkCommand.FreeData, []);
        }
        catch (ZkException)
        {
            // Freeing an empty buffer is allowed to fail silently.
        }
    }

    /// <summary>Reads a device configuration option (e.g. "~SerialNumber", "~DeviceName", "MAC").</summary>
    public string ReadOption(string name)
    {
        var payload = Encoding.ASCII.GetBytes(name + "\0");
        var (code, data) = Send(ZkCommand.OptionsRrq, payload);
        if (code is not (ZkCommand.AckOk or ZkCommand.AckData))
        {
            throw new ZkException($"Option '{name}' refused (0x{code:X4}).", code);
        }

        var text = ZkPacket.ReadAsciiZ(data);
        var index = text.IndexOf('=', StringComparison.Ordinal);
        return index >= 0 ? text[(index + 1)..].Trim() : text;
    }

    public bool TryReadOption(string name, out string value)
    {
        try
        {
            value = ReadOption(name);
            return true;
        }
        catch (ZkException)
        {
            value = string.Empty;
            return false;
        }
    }

    /// <summary>Writes a device configuration option (e.g. "IPAddress=192.168.1.201").</summary>
    public bool WriteOption(string assignment)
    {
        var payload = Encoding.ASCII.GetBytes(assignment + "\0");
        var (code, _) = Send(ZkCommand.OptionsWrq, payload);
        return code is ZkCommand.AckOk or ZkCommand.AckData;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Disconnect();
        }
        catch (Exception ex) when (ex is ZkException or ObjectDisposedException)
        {
            // Nothing else to do while disposing.
        }

        _socket.Dispose();
        _stream?.Dispose();
    }
}
