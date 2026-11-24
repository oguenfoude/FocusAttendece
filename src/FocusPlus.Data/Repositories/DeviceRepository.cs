using Dapper;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Data.Repositories;

public sealed class DeviceRepository(IConnectionFactory factory) : RepositoryBase(factory)
{
    public IReadOnlyList<Device> GetAll(bool includeArchived = false)
    {
        using var connection = Factory.Open();
        var sql = includeArchived
            ? "SELECT * FROM Devices ORDER BY Name;"
            : "SELECT * FROM Devices WHERE IsArchived = 0 ORDER BY Name;";
        return connection.Query<Device>(sql).ToList();
    }

    public Device? Get(string id)
    {
        using var connection = Factory.Open();
        return connection.QuerySingleOrDefault<Device>("SELECT * FROM Devices WHERE Id = @id;", new { id });
    }

    public Device? GetBySerial(string serialNumber)
    {
        if (string.IsNullOrWhiteSpace(serialNumber))
        {
            return null;
        }

        using var connection = Factory.Open();
        return connection.QuerySingleOrDefault<Device>(
            "SELECT * FROM Devices WHERE SerialNumber = @serialNumber LIMIT 1;",
            new { serialNumber });
    }

    public Device? GetByEndpoint(string ipAddress, int port)
    {
        using var connection = Factory.Open();
        return connection.QuerySingleOrDefault<Device>(
            "SELECT * FROM Devices WHERE IpAddress = @ipAddress AND Port = @port AND IsArchived = 0 LIMIT 1;",
            new { ipAddress, port });
    }

    public void Upsert(Device device)
    {
        device.UpdatedAtUtc = DateTime.UtcNow;
        using var connection = Factory.Open();
        connection.Execute(
            """
            INSERT INTO Devices (Id, Name, IpAddress, Port, CommKey, SerialNumber, ModelName, FirmwareVersion,
                                 MacAddress, Platform, DeviceClock, LastSeenUtc, LastSyncUtc, LastPullUtc,
                                 UserCount, FingerCount, FaceCount, PunchCount, IsEnabled, Note, IsArchived,
                                 ArchivedAtUtc, ArchiveReason, ArchivedBy, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@Id, @Name, @IpAddress, @Port, @CommKey, @SerialNumber, @ModelName, @FirmwareVersion,
                    @MacAddress, @Platform, @DeviceClock, @LastSeenUtc, @LastSyncUtc, @LastPullUtc,
                    @UserCount, @FingerCount, @FaceCount, @PunchCount, @IsEnabled, @Note, @IsArchived,
                    @ArchivedAtUtc, @ArchiveReason, @ArchivedBy, @CreatedAtUtc, @UpdatedAtUtc)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name, IpAddress = excluded.IpAddress, Port = excluded.Port,
                CommKey = excluded.CommKey, SerialNumber = excluded.SerialNumber,
                ModelName = excluded.ModelName, FirmwareVersion = excluded.FirmwareVersion,
                MacAddress = excluded.MacAddress, Platform = excluded.Platform,
                DeviceClock = excluded.DeviceClock, LastSeenUtc = excluded.LastSeenUtc,
                LastSyncUtc = excluded.LastSyncUtc, LastPullUtc = excluded.LastPullUtc,
                UserCount = excluded.UserCount, FingerCount = excluded.FingerCount,
                FaceCount = excluded.FaceCount, PunchCount = excluded.PunchCount,
                IsEnabled = excluded.IsEnabled, Note = excluded.Note, IsArchived = excluded.IsArchived,
                ArchivedAtUtc = excluded.ArchivedAtUtc, ArchiveReason = excluded.ArchiveReason,
                ArchivedBy = excluded.ArchivedBy, UpdatedAtUtc = excluded.UpdatedAtUtc;
            """,
            device);
    }

    /// <summary>Persists the live snapshot coming from the hardware engine.</summary>
    public void Touch(string deviceId, DeviceSnapshot snapshot, bool online)
    {
        using var connection = Factory.Open();
        connection.Execute(
            """
            UPDATE Devices
               SET SerialNumber = COALESCE(NULLIF(@SerialNumber, ''), SerialNumber),
                   ModelName = COALESCE(NULLIF(@ModelName, ''), ModelName),
                   FirmwareVersion = COALESCE(NULLIF(@FirmwareVersion, ''), FirmwareVersion),
                   MacAddress = COALESCE(NULLIF(@MacAddress, ''), MacAddress),
                   Platform = COALESCE(NULLIF(@Platform, ''), Platform),
                   DeviceClock = @DeviceClock,
                   LastSeenUtc = @now,
                   UserCount = @UserCount,
                   FingerCount = @FingerCount,
                   FaceCount = @FaceCount,
                   PunchCount = @PunchCount,
                   UpdatedAtUtc = @now
             WHERE Id = @deviceId;
            """,
            new
            {
                deviceId,
                snapshot.SerialNumber,
                snapshot.ModelName,
                snapshot.FirmwareVersion,
                snapshot.MacAddress,
                snapshot.Platform,
                snapshot.DeviceClock,
                snapshot.UserCount,
                snapshot.FingerCount,
                snapshot.FaceCount,
                snapshot.PunchCount,
                now = DateTime.UtcNow
            });
    }

    public void MarkSync(string deviceId, DateTime utcNow)
    {
        using var connection = Factory.Open();
        connection.Execute(
            "UPDATE Devices SET LastSyncUtc = @utcNow, LastSeenUtc = @utcNow, UpdatedAtUtc = @utcNow WHERE Id = @deviceId;",
            new { deviceId, utcNow });
    }

    public void MarkPull(string deviceId, DateTime utcNow)
    {
        using var connection = Factory.Open();
        connection.Execute(
            "UPDATE Devices SET LastPullUtc = @utcNow, LastSeenUtc = @utcNow, UpdatedAtUtc = @utcNow WHERE Id = @deviceId;",
            new { deviceId, utcNow });
    }

    public void SetEnabled(string deviceId, bool enabled)
    {
        using var connection = Factory.Open();
        connection.Execute(
            "UPDATE Devices SET IsEnabled = @enabled, UpdatedAtUtc = @now WHERE Id = @deviceId;",
            new { deviceId, enabled, now = DateTime.UtcNow });
    }

    public void Archive(string id, string reason, string actor)
    {
        using var connection = Factory.Open();
        connection.Execute(
            """
            UPDATE Devices
               SET IsArchived = 1, ArchivedAtUtc = @now, ArchiveReason = @reason, ArchivedBy = @actor,
                   UpdatedAtUtc = @now, IsEnabled = 0
             WHERE Id = @id;
            """,
            new { id, now = DateTime.UtcNow, reason, actor });
    }

    public void Restore(string id, string actor)
    {
        using var connection = Factory.Open();
        connection.Execute(
            """
            UPDATE Devices
               SET IsArchived = 0, ArchivedAtUtc = NULL, ArchiveReason = NULL, ArchivedBy = @actor,
                   UpdatedAtUtc = @now
             WHERE Id = @id;
            """,
            new { id, now = DateTime.UtcNow, actor });
    }

    public int Count()
    {
        using var connection = Factory.Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Devices WHERE IsArchived = 0;");
    }
}
