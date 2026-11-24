namespace FocusPlus.Hardware;

/// <summary>Reads complete ZKTeco TCP frames from a stream, tolerating split reads.</summary>
public static class ZkFrameReader
{
    public const int MaxFrameBytes = 32 * 1024 * 1024;

    public static byte[] ReadFrame(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = ReadExact(stream, 8);
        if (!ZkPacket.HasTcpMagic(header))
        {
            throw new ZkException("Invalid TCP frame (magic header mismatch).");
        }

        var length = ZkPacket.ReadInt32(header, 4);
        if (length is < 8 or > MaxFrameBytes)
        {
            throw new ZkException($"Invalid frame length {length}.");
        }

        return ReadExact(stream, length);
    }

    public static byte[] ReadExact(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
            {
                throw new ZkException("The terminal closed the connection.");
            }

            offset += read;
        }

        return buffer;
    }
}
