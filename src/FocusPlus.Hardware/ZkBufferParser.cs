using System.Text;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Hardware;

/// <summary>Device memory capacities parsed from CMD_GET_FREE_SIZES.</summary>
public sealed record ZkMemorySizes
{
    public int Users { get; init; }
    public int Fingers { get; init; }
    public int Records { get; init; }
    public int Cards { get; init; }
    public int UsersCapacity { get; init; }
    public int FingersCapacity { get; init; }
    public int RecordsCapacity { get; init; }
    public int Faces { get; init; }
    public int FacesCapacity { get; init; }
}

/// <summary>
/// Binary layout decoding for attendance logs, user records and memory sizes.
/// All layouts are the documented ZKTeco standalone structures.
/// </summary>
public static class ZkBufferParser
{
    public static ZkMemorySizes ParseSizes(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20)
        {
            return new ZkMemorySizes();
        }

        // 20 little-endian int32: [.., users, .., fingers, .., records, .., cards, caps..]
        var fields = new int[20];
        for (var i = 0; i < 20 && (i * 4) + 4 <= data.Length; i++)
        {
            fields[i] = ZkPacket.ReadInt32(data, i * 4);
        }

        var faces = 0;
        var facesCapacity = 0;
        if (data.Length >= 92)
        {
            faces = ZkPacket.ReadInt32(data, 80);
            facesCapacity = ZkPacket.ReadInt32(data, 88);
        }

        return new ZkMemorySizes
        {
            Users = Math.Max(0, fields[4]),
            Fingers = Math.Max(0, fields[6]),
            Records = Math.Max(0, fields[8]),
            Cards = Math.Max(0, fields[12]),
            UsersCapacity = Math.Max(0, fields[15]),
            FingersCapacity = Math.Max(0, fields[14]),
            RecordsCapacity = Math.Max(0, fields[16]),
            Faces = Math.Max(0, faces),
            FacesCapacity = Math.Max(0, facesCapacity)
        };
    }

    /// <summary>Parses a raw attendance buffer that starts with a 4-byte payload size.</summary>
    public static IReadOnlyList<DevicePunch> ParseAttendance(ReadOnlySpan<byte> buffer, int expectedRecords)
    {
        if (buffer.Length < 4)
        {
            return [];
        }

        var totalSize = ZkPacket.ReadInt32(buffer, 0);
        if (totalSize <= 0 || totalSize > buffer.Length - 4)
        {
            totalSize = buffer.Length - 4;
        }

        var payload = buffer.Slice(4, totalSize);
        var recordSize = InferAttendanceRecordSize(payload, expectedRecords);
        if (recordSize == 0)
        {
            return [];
        }

        var punches = new List<DevicePunch>(recordSize > 0 ? payload.Length / recordSize : 0);
        for (var offset = 0; offset + recordSize <= payload.Length; offset += recordSize)
        {
            var record = payload.Slice(offset, recordSize);
            var punch = ParseAttendanceRecord(record, recordSize);
            if (punch is not null)
            {
                punches.Add(punch);
            }
        }

        return punches;
    }

    private static int InferAttendanceRecordSize(ReadOnlySpan<byte> payload, int expectedRecords)
    {
        if (payload.Length == 0)
        {
            return 0;
        }

        foreach (var candidate in new[] { 16, 8, 40 })
        {
            if (payload.Length % candidate != 0)
            {
                continue;
            }

            var count = payload.Length / candidate;
            if (expectedRecords > 0 && count != expectedRecords)
            {
                continue;
            }

            var valid = 0;
            for (var i = 0; i < count; i++)
            {
                var offset = i * candidate;
                var timeOffset = candidate switch
                {
                    8 => offset + 3,
                    16 => offset + 4,
                    _ => offset + 27
                };

                if (timeOffset + 4 <= payload.Length
                    && ZkPacket.TryDecodeTime(ZkPacket.ReadUInt32(payload, timeOffset), out _))
                {
                    valid++;
                }
            }

            if (valid == count)
            {
                return candidate;
            }
        }

        // Last resort: trust the arithmetic from the device's record count.
        if (expectedRecords > 0 && payload.Length % expectedRecords == 0)
        {
            var size = payload.Length / expectedRecords;
            if (size is 8 or 16 or 40)
            {
                return size;
            }
        }

        return 0;
    }

    private static DevicePunch? ParseAttendanceRecord(ReadOnlySpan<byte> record, int recordSize)
    {
        int timeOffset;
        string pin;
        byte verify;
        byte state;
        var workCode = 0;

        switch (recordSize)
        {
            case 8:
            {
                var uid = ZkPacket.ReadUInt16(record, 0);
                verify = record[2];
                timeOffset = 3;
                state = record[7];
                pin = uid.ToString();
                break;
            }

            case 16:
            {
                var userId = ZkPacket.ReadUInt32(record, 0);
                timeOffset = 4;
                verify = record[8];
                state = record[9];
                workCode = ZkPacket.ReadInt32(record, 12);
                pin = userId.ToString();
                break;
            }

            default:
            {
                var uid = ZkPacket.ReadUInt16(record, 0);
                pin = ZkPacket.ReadAsciiZ(record.Slice(2, 24));
                verify = record[26];
                timeOffset = 27;
                state = record[31];
                if (string.IsNullOrEmpty(pin))
                {
                    pin = uid.ToString();
                }

                break;
            }
        }

        if (!ZkPacket.TryDecodeTime(ZkPacket.ReadUInt32(record, timeOffset), out var timestamp))
        {
            return null;
        }

        return new DevicePunch
        {
            DevicePin = pin,
            Timestamp = timestamp,
            VerifyMode = verify,
            PunchState = state,
            WorkCode = Math.Max(0, workCode)
        };
    }

    /// <summary>Parses a raw user buffer that starts with a 4-byte payload size.</summary>
    public static IReadOnlyList<DeviceUser> ParseUsers(ReadOnlySpan<byte> buffer, int expectedUsers)
    {
        if (buffer.Length < 4)
        {
            return [];
        }

        var totalSize = ZkPacket.ReadInt32(buffer, 0);
        if (totalSize <= 0 || totalSize > buffer.Length - 4)
        {
            totalSize = buffer.Length - 4;
        }

        var payload = buffer.Slice(4, totalSize);
        var recordSize = InferUserRecordSize(payload.Length, expectedUsers);
        if (recordSize == 0)
        {
            return [];
        }

        var users = new List<DeviceUser>();
        for (var offset = 0; offset + recordSize <= payload.Length; offset += recordSize)
        {
            var record = payload.Slice(offset, recordSize);
            var user = ParseUserRecord(record, recordSize);
            if (user is not null)
            {
                users.Add(user);
            }
        }

        return users;
    }

    private static int InferUserRecordSize(int length, int expectedUsers)
    {
        if (length == 0)
        {
            return 0;
        }

        foreach (var candidate in new[] { 72, 28, 120 })
        {
            if (length % candidate == 0 && (expectedUsers <= 0 || length / candidate == expectedUsers))
            {
                return candidate;
            }
        }

        foreach (var candidate in new[] { 72, 28, 120 })
        {
            if (length % candidate == 0)
            {
                return candidate;
            }
        }

        return 0;
    }

    private static DeviceUser? ParseUserRecord(ReadOnlySpan<byte> record, int recordSize)
    {
        if (recordSize == 28)
        {
            var uid = ZkPacket.ReadUInt16(record, 0);
            var privilege = record[2];
            var password = ZkPacket.ReadAsciiZ(record.Slice(3, 5));
            var name = ZkPacket.ReadAsciiZ(record.Slice(8, 8));
            var card = ZkPacket.ReadUInt32(record, 16);
            var group = record[21];
            var userId = ZkPacket.ReadUInt32(record, 24);
            return new DeviceUser
            {
                Uid = uid,
                UserId = userId.ToString(),
                Name = name,
                Privilege = privilege,
                Password = password,
                Card = card,
                GroupId = group.ToString()
            };
        }

        if (recordSize == 120)
        {
            var uid = ZkPacket.ReadUInt16(record, 0);
            var privilege = record[2];
            var password = ZkPacket.ReadAsciiZ(record.Slice(3, 8));
            var first = ZkPacket.ReadAsciiZ(record.Slice(35, 24));
            var last = ZkPacket.ReadAsciiZ(record.Slice(59, 24));
            var card = ZkPacket.ReadUInt32(record, 83);
            var userId = ZkPacket.ReadAsciiZ(record.Slice(96, 24));
            var name = (first + " " + last).Trim();
            return new DeviceUser
            {
                Uid = uid,
                UserId = string.IsNullOrEmpty(userId) ? uid.ToString() : userId,
                Name = name,
                Privilege = privilege,
                Password = password,
                Card = card
            };
        }

        if (record.Length < 72)
        {
            return null;
        }

        var uid72 = ZkPacket.ReadUInt16(record, 0);
        var privilege72 = record[2];
        var password72 = ZkPacket.ReadAsciiZ(record.Slice(3, 8));
        var name72 = ZkPacket.ReadAsciiZ(record.Slice(11, 24));
        var card72 = ZkPacket.ReadUInt32(record, 35);
        var group72 = ZkPacket.ReadAsciiZ(record.Slice(40, 7));
        var userId72 = ZkPacket.ReadAsciiZ(record.Slice(48, 24));

        return new DeviceUser
        {
            Uid = uid72,
            UserId = string.IsNullOrEmpty(userId72) ? uid72.ToString() : userId72,
            Name = name72,
            Privilege = privilege72,
            Password = password72,
            Card = card72,
            GroupId = string.IsNullOrEmpty(group72) ? "0" : group72
        };
    }

    /// <summary>Builds the CMD_USER_WRQ payload (72-byte layout, 28-byte for legacy firmware).</summary>
    public static byte[] BuildUserRecord(DeviceUser user, bool legacy28 = false)
    {
        var privilege = user.Privilege is 0 or 2 or 6 or 14 ? user.Privilege : 0;

        if (legacy28)
        {
            var record = new byte[28];
            ZkPacket.WriteUInt16(record, 0, (ushort)user.Uid);
            record[2] = (byte)privilege;
            WriteFixed(record, 3, 5, user.Password);
            WriteFixed(record, 8, 8, user.Name);
            ZkPacket.WriteUInt32(record, 16, (uint)user.Card);
            record[20] = 0x00;
            record[21] = byte.TryParse(user.GroupId, out var group) ? group : (byte)0;
            // 22-23: timezone (0)
            var id = int.TryParse(user.UserId, out var numeric) ? numeric : user.Uid;
            ZkPacket.WriteInt32(record, 24, id);
            return record;
        }

        var result = new byte[72];
        ZkPacket.WriteUInt16(result, 0, (ushort)user.Uid);
        result[2] = (byte)privilege;
        WriteFixed(result, 3, 8, user.Password);
        WriteFixed(result, 11, 24, user.Name);
        ZkPacket.WriteUInt32(result, 35, (uint)user.Card);
        result[39] = 0x00;
        WriteFixed(result, 40, 7, user.GroupId);
        result[47] = 0x00;
        WriteFixed(result, 48, 24, user.UserId);
        return result;
    }

    private static void WriteFixed(Span<byte> target, int offset, int length, string value)
    {
        target.Slice(offset, length).Clear();
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        var count = Math.Min(bytes.Length, length);
        bytes.AsSpan(0, count).CopyTo(target.Slice(offset));
    }
}
