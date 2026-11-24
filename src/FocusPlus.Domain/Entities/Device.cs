namespace FocusPlus.Domain.Entities;

/// <summary>A ZKTeco terminal registered in the system. Only real discovered/entered hardware is stored.</summary>
public sealed class Device : ArchivableEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; } = 4370;

    /// <summary>COMM key / device password (0 when unprotected).</summary>
    public int CommKey { get; set; }

    public string? SerialNumber { get; set; }
    public string? ModelName { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? MacAddress { get; set; }
    public string? Platform { get; set; }

    public DateTime? DeviceClock { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public DateTime? LastSyncUtc { get; set; }
    public DateTime? LastPullUtc { get; set; }

    public int UserCount { get; set; }
    public int FingerCount { get; set; }
    public int FaceCount { get; set; }
    public int PunchCount { get; set; }

    public bool IsEnabled { get; set; } = true;
    public string? Note { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public string Endpoint => $"{IpAddress}:{Port}";
}

/// <summary>Live, non-persisted hardware snapshot returned by the SDK/native engine.</summary>
public sealed record DeviceSnapshot
{
    public required string IpAddress { get; init; }
    public int Port { get; init; } = 4370;
    public string? SerialNumber { get; init; }
    public string? ModelName { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? MacAddress { get; init; }
    public string? Platform { get; init; }
    public DateTime? DeviceClock { get; init; }
    public int UserCount { get; init; }
    public int FingerCount { get; init; }
    public int FaceCount { get; init; }
    public int PunchCount { get; init; }
    public int LatencyMs { get; init; }
    public string Engine { get; init; } = "Native TCP 4370";

    /// <summary>True when the terminal returned at least one real identity value.</summary>
    public bool IsIdentified =>
        !string.IsNullOrWhiteSpace(SerialNumber)
        || !string.IsNullOrWhiteSpace(ModelName)
        || !string.IsNullOrWhiteSpace(FirmwareVersion)
        || !string.IsNullOrWhiteSpace(MacAddress)
        || UserCount > 0
        || PunchCount > 0;
}

/// <summary>A user record living inside a physical terminal (device memory inspector).</summary>
public sealed record DeviceUser
{
    public int Uid { get; init; }
    public required string UserId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Privilege { get; init; }
    public string Password { get; init; } = string.Empty;
    public long Card { get; init; }
    public string GroupId { get; init; } = "0";
}

/// <summary>A raw attendance log coming from a terminal.</summary>
public sealed record DevicePunch
{
    public required string DevicePin { get; init; }
    public DateTime Timestamp { get; init; }
    public int VerifyMode { get; init; }
    public int PunchState { get; init; }
    public int WorkCode { get; init; }
}
