namespace FocusPlus.Hardware;

/// <summary>Packet framing, checksum and ZK time codec (ported from the official zkemsdk algorithm).</summary>
public static class ZkPacket
{
    public const ushort PrepareData1 = 20560; // 0x5050
    public const ushort PrepareData2 = 32130; // 0x7D82

    /// <summary>16-bit one's complement checksum used by every ZKTeco frame.</summary>
    public static ushort Checksum(ReadOnlySpan<byte> packet)
    {
        var checksum = 0;
        var i = 0;
        for (; i + 1 < packet.Length; i += 2)
        {
            checksum += packet[i] | (packet[i + 1] << 8);
            if (checksum > ushort.MaxValue)
            {
                checksum -= ushort.MaxValue;
            }
        }

        if (i < packet.Length)
        {
            checksum += packet[i];
        }

        while (checksum > ushort.MaxValue)
        {
            checksum -= ushort.MaxValue;
        }

        checksum = ~checksum;
        while (checksum < 0)
        {
            checksum += ushort.MaxValue;
        }

        return (ushort)checksum;
    }

    /// <summary>Builds a protocol packet: 8-byte header followed by the payload.</summary>
    public static byte[] Build(ushort command, ushort sessionId, ushort replyId, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[8 + payload.Length];
        WriteUInt16(packet, 0, command);
        WriteUInt16(packet, 2, 0);
        WriteUInt16(packet, 4, sessionId);
        WriteUInt16(packet, 6, replyId);
        payload.CopyTo(packet.AsSpan(8));

        var checksum = Checksum(packet);
        WriteUInt16(packet, 2, checksum);
        return packet;
    }

    /// <summary>Prefixes a TCP packet with the 8-byte machine header (magic + length).</summary>
    public static byte[] FrameTcp(ReadOnlySpan<byte> packet)
    {
        var framed = new byte[8 + packet.Length];
        WriteUInt16(framed, 0, PrepareData1);
        WriteUInt16(framed, 2, PrepareData2);
        WriteInt32(framed, 4, packet.Length);
        packet.CopyTo(framed.AsSpan(8));
        return framed;
    }

    public static bool HasTcpMagic(ReadOnlySpan<byte> header) =>
        header.Length >= 4
        && header[0] == 0x50 && header[1] == 0x50 && header[2] == 0x82 && header[3] == 0x7D;

    /// <summary>Designs the "comm key" reply used by CMD_AUTH (zc300 key scrambling).</summary>
    public static byte[] MakeCommKey(int key, ushort sessionId, int ticks = 50)
    {
        var k = 0u;
        for (var i = 0; i < 32; i++)
        {
            k = (key & (1 << i)) != 0 ? (k << 1) | 1 : k << 1;
        }

        k += sessionId;

        var bytes = new byte[4];
        ZkPacket.WriteUInt32(bytes, 0, k);
        bytes[0] ^= (byte)'Z';
        bytes[1] ^= (byte)'K';
        bytes[2] ^= (byte)'S';
        bytes[3] ^= (byte)'O';

        var lo = (ushort)(bytes[0] | (bytes[1] << 8));
        var hi = (ushort)(bytes[2] | (bytes[3] << 8));
        WriteUInt16(bytes, 0, hi);
        WriteUInt16(bytes, 2, lo);

        var b = (byte)(0xFF & ticks);
        bytes[0] ^= b;
        bytes[1] ^= b;
        bytes[2] = b;
        bytes[3] ^= b;
        return bytes;
    }

    /// <summary>Decodes the 32-bit ZKTeco timestamp into a device-local DateTime.</summary>
    public static bool TryDecodeTime(uint encoded, out DateTime value)
    {
        value = default;
        long t = encoded;
        var second = (int)(t % 60);
        t /= 60;
        var minute = (int)(t % 60);
        t /= 60;
        var hour = (int)(t % 24);
        t /= 24;
        var day = (int)(t % 31) + 1;
        t /= 31;
        var month = (int)(t % 12) + 1;
        t /= 12;
        var year = (int)t + 2000;

        if (year is < 2000 or > 2100 || month is < 1 or > 12 || day is < 1 or > 31
            || hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59)
        {
            return false;
        }

        try
        {
            value = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>Encodes a DateTime using the same scheme the terminals understand.</summary>
    public static uint EncodeTime(DateTime time)
    {
        var days = ((time.Year % 100) * 12 * 31) + ((time.Month - 1) * 31) + time.Day - 1;
        return (uint)((days * 24L * 60 * 60) + ((time.Hour * 60L + time.Minute) * 60) + time.Second);
    }

    public static ushort ReadUInt16(ReadOnlySpan<byte> buffer, int offset) =>
        (ushort)(buffer[offset] | (buffer[offset + 1] << 8));

    public static void WriteUInt16(Span<byte> buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)(value >> 8);
    }

    public static int ReadInt32(ReadOnlySpan<byte> buffer, int offset) =>
        buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);

    public static uint ReadUInt32(ReadOnlySpan<byte> buffer, int offset) =>
        (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));

    public static void WriteInt32(Span<byte> buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    public static void WriteUInt32(Span<byte> buffer, int offset, uint value) =>
        WriteInt32(buffer, offset, unchecked((int)value));

    /// <summary>Reads a NUL terminated ASCII string.</summary>
    public static string ReadAsciiZ(ReadOnlySpan<byte> buffer)
    {
        var end = buffer.IndexOf((byte)0);
        if (end < 0)
        {
            end = buffer.Length;
        }

        return System.Text.Encoding.UTF8.GetString(buffer[..end]).Trim();
    }
}
