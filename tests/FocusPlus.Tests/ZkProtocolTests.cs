using FocusPlus.Domain.Entities;
using FocusPlus.Hardware;

namespace FocusPlus.Tests;

public sealed class ZkProtocolTests
{
    [Fact]
    public void Checksum_MatchesTheOfficialZkemAlgorithm()
    {
        // CMD_CONNECT (1000) / session 0 / reply 65534, checksum field zeroed.
        var header = new byte[8];
        ZkPacket.WriteUInt16(header, 0, 1000);
        ZkPacket.WriteUInt16(header, 2, 0);
        ZkPacket.WriteUInt16(header, 4, 0);
        ZkPacket.WriteUInt16(header, 6, 65534);

        Assert.Equal(0xFC17, ZkPacket.Checksum(header));
    }

    [Fact]
    public void Build_WritesCommandSessionReplyAndChecksum()
    {
        var packet = ZkPacket.Build(ZkCommand.Connect, 0x1234, 0xFFFF - 1, []);

        Assert.Equal(8, packet.Length);
        Assert.Equal(1000, ZkPacket.ReadUInt16(packet, 0));
        Assert.Equal(0x1234, ZkPacket.ReadUInt16(packet, 4));
        Assert.Equal(0xFFFF - 1, ZkPacket.ReadUInt16(packet, 6));
        Assert.NotEqual(0, ZkPacket.ReadUInt16(packet, 2));
    }

    [Fact]
    public void TcpFraming_UsesMachineMagicAndLength()
    {
        var packet = ZkPacket.Build(ZkCommand.Connect, 0, 1, []);
        var framed = ZkPacket.FrameTcp(packet);

        Assert.Equal(16, framed.Length);
        Assert.True(ZkPacket.HasTcpMagic(framed));
        Assert.Equal(0x50, framed[0]);
        Assert.Equal(0x50, framed[1]);
        Assert.Equal(0x82, framed[2]);
        Assert.Equal(0x7D, framed[3]);
        Assert.Equal(8, ZkPacket.ReadInt32(framed, 4));
    }

    [Theory]
    [InlineData(2000, 1, 1, 0, 0, 0)]
    [InlineData(2026, 11, 15, 8, 15, 59)]
    [InlineData(2050, 6, 30, 23, 59, 59)]
    public void TimeCodec_RoundTrips(int year, int month, int day, int hour, int minute, int second)
    {
        var value = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        var encoded = ZkPacket.EncodeTime(value);

        Assert.True(ZkPacket.TryDecodeTime(encoded, out var decoded));
        Assert.Equal(value, decoded);
    }

    [Fact]
    public void TimeCodec_AcceptsEpochAndRejectsImpossibleValues()
    {
        // 0 is the valid ZK epoch (2000-01-01 00:00:00).
        Assert.True(ZkPacket.TryDecodeTime(0, out var epoch));
        Assert.Equal(new DateTime(2000, 1, 1), epoch);
        Assert.False(ZkPacket.TryDecodeTime(uint.MaxValue, out _));
    }

    [Fact]
    public void CommKey_MatchesTheReferenceImplementation()
    {
        var key = ZkPacket.MakeCommKey(0, 0);
        Assert.Equal(new byte[] { 0x61, 0x7D, 0x32, 0x79 }, key);
    }

    [Fact]
    public void CommKey_IsDeterministic_AndChangesWithSession()
    {
        var first = ZkPacket.MakeCommKey(12345, 7);
        var second = ZkPacket.MakeCommKey(12345, 7);
        var otherSession = ZkPacket.MakeCommKey(12345, 263);
        var otherKey = ZkPacket.MakeCommKey(54321, 7);

        Assert.Equal(first, second);
        Assert.NotEqual(first, otherSession);
        Assert.NotEqual(first, otherKey);
    }

    [Fact]
    public void ParseSizes_ReadsUsersFingersRecordsAndFaces()
    {
        var data = new byte[92];
        ZkPacket.WriteInt32(data, 4 * 4, 12);
        ZkPacket.WriteInt32(data, 6 * 4, 34);
        ZkPacket.WriteInt32(data, 8 * 4, 99);
        ZkPacket.WriteInt32(data, 12 * 4, 5);
        ZkPacket.WriteInt32(data, 14 * 4, 3000);
        ZkPacket.WriteInt32(data, 15 * 4, 3000);
        ZkPacket.WriteInt32(data, 16 * 4, 100000);
        ZkPacket.WriteInt32(data, 80, 7);
        ZkPacket.WriteInt32(data, 88, 50);

        var sizes = ZkBufferParser.ParseSizes(data);

        Assert.Equal(12, sizes.Users);
        Assert.Equal(34, sizes.Fingers);
        Assert.Equal(99, sizes.Records);
        Assert.Equal(5, sizes.Cards);
        Assert.Equal(3000, sizes.UsersCapacity);
        Assert.Equal(7, sizes.Faces);
        Assert.Equal(50, sizes.FacesCapacity);
    }

    [Fact]
    public void ParseSizes_ReturnsZeroesForEmptyBuffer()
    {
        var sizes = ZkBufferParser.ParseSizes([]);
        Assert.Equal(0, sizes.Users);
        Assert.Equal(0, sizes.Records);
    }

    [Fact]
    public void ParseAttendance_EightByteRecords()
    {
        var buffer = AttendanceBuffer8(
            (1001, new DateTime(2026, 11, 15, 7, 58, 12), 0),
            (1001, new DateTime(2026, 11, 15, 16, 32, 5), 1));

        var punches = ZkBufferParser.ParseAttendance(buffer, 2);

        Assert.Equal(2, punches.Count);
        Assert.Equal("1001", punches[0].DevicePin);
        Assert.Equal(new DateTime(2026, 11, 15, 7, 58, 12), punches[0].Timestamp);
        Assert.Equal(0, punches[0].PunchState);
        Assert.Equal(1, punches[0].VerifyMode);
        Assert.Equal(new DateTime(2026, 11, 15, 16, 32, 5), punches[1].Timestamp);
        Assert.Equal(1, punches[1].PunchState);
    }

    [Fact]
    public void ParseAttendance_SixteenByteRecords()
    {
        var records = new List<byte[]>
        {
            Record16(20260001, new DateTime(2026, 11, 15, 8, 0, 0), 15, 0, 0),
            Record16(20260001, new DateTime(2026, 11, 15, 16, 30, 0), 15, 1, 7)
        };
        var payload = records.SelectMany(r => r).ToArray();
        var buffer = Prefix(payload);

        var punches = ZkBufferParser.ParseAttendance(buffer, 2);

        Assert.Equal(2, punches.Count);
        Assert.Equal("20260001", punches[0].DevicePin);
        Assert.Equal(15, punches[0].VerifyMode);
        Assert.Equal(7, punches[1].WorkCode);
    }

    [Fact]
    public void ParseAttendance_FortyByteRecords()
    {
        var record = new byte[40];
        ZkPacket.WriteUInt16(record, 0, 5);
        System.Text.Encoding.ASCII.GetBytes("OPGI-77").CopyTo(record, 2);
        record[26] = 1;
        ZkPacket.WriteUInt32(record, 27, ZkPacket.EncodeTime(new DateTime(2026, 11, 15, 12, 3, 9)));
        record[31] = 1;

        var punches = ZkBufferParser.ParseAttendance(Prefix(record), 1);

        Assert.Single(punches);
        Assert.Equal("OPGI-77", punches[0].DevicePin);
        Assert.Equal(new DateTime(2026, 11, 15, 12, 3, 9), punches[0].Timestamp);
    }

    [Fact]
    public void ParseAttendance_EmptyBuffer_ReturnsEmpty()
    {
        Assert.Empty(ZkBufferParser.ParseAttendance([], 0));
        Assert.Empty(ZkBufferParser.ParseAttendance(Prefix([]), 0));
    }

    [Fact]
    public void ParseUsers_SeventyTwoByteRecords_RoundTrips()
    {
        var user = new DeviceUser
        {
            Uid = 42,
            UserId = "1001",
            Name = "BENALI",
            Privilege = 14,
            Password = "1234",
            Card = 987654,
            GroupId = "2"
        };
        var buffer = Prefix(ZkBufferParser.BuildUserRecord(user));

        var users = ZkBufferParser.ParseUsers(buffer, 1);

        Assert.Single(users);
        Assert.Equal(42, users[0].Uid);
        Assert.Equal("1001", users[0].UserId);
        Assert.Equal("BENALI", users[0].Name);
        Assert.Equal(14, users[0].Privilege);
        Assert.Equal("1234", users[0].Password);
        Assert.Equal(987654u, users[0].Card);
        Assert.Equal("2", users[0].GroupId);
    }

    [Fact]
    public void ParseUsers_LegacyTwentyEightByteRecords_RoundTrips()
    {
        var user = new DeviceUser
        {
            Uid = 7,
            UserId = "77",
            Name = "KHELIFI",
            Privilege = 0,
            Password = "99",
            Card = 0,
            GroupId = "0"
        };
        var buffer = Prefix(ZkBufferParser.BuildUserRecord(user, legacy28: true));

        var users = ZkBufferParser.ParseUsers(buffer, 1);

        Assert.Single(users);
        Assert.Equal(7, users[0].Uid);
       Assert.Equal("77", users[0].UserId);
        Assert.Equal("KHELIFI", users[0].Name);
        Assert.Equal("99", users[0].Password);
    }

    [Fact]
    public void BuildUserRecord_ClampsUnknownPrivilege()
    {
        var record = ZkBufferParser.BuildUserRecord(new DeviceUser
        {
            Uid = 1,
            UserId = "1",
            Name = "X",
            Privilege = 99
        });

        Assert.Equal(72, record.Length);
        Assert.Equal(0, record[2]);
    }

    [Fact]
    public void ParseUsers_Empty_ReturnsEmpty()
    {
        Assert.Empty(ZkBufferParser.ParseUsers([], 0));
    }

    [Fact]
    public void LanScanner_BuildsAHostListFromRealInterfaces()
    {
        var hosts = LanScanner.BuildHostList();

        Assert.NotNull(hosts);
        Assert.All(hosts, host => Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, host.AddressFamily));
        Assert.True(hosts.Count <= 1024);
    }

    private static byte[] AttendanceBuffer8(params (ushort Uid, DateTime Time, byte State)[] records)
    {
        var payload = new byte[records.Length * 8];
        for (var i = 0; i < records.Length; i++)
        {
            var offset = i * 8;
            ZkPacket.WriteUInt16(payload, offset, records[i].Uid);
            payload[offset + 2] = 1;
            ZkPacket.WriteUInt32(payload, offset + 3, ZkPacket.EncodeTime(records[i].Time));
            payload[offset + 7] = records[i].State;
        }

        return Prefix(payload);
    }

    private static byte[] Record16(uint pin, DateTime time, byte verify, byte state, uint workCode)
    {
        var record = new byte[16];
        ZkPacket.WriteUInt32(record, 0, pin);
        ZkPacket.WriteUInt32(record, 4, ZkPacket.EncodeTime(time));
        record[8] = verify;
        record[9] = state;
        ZkPacket.WriteUInt32(record, 12, workCode);
        return record;
    }

    private static byte[] Prefix(byte[] payload)
    {
        var buffer = new byte[4 + payload.Length];
        ZkPacket.WriteInt32(buffer, 0, payload.Length);
        payload.CopyTo(buffer, 4);
        return buffer;
    }
}
