namespace FocusPlus.Domain.Entities;

/// <summary>A single immutable attendance scan. Raw logs are never deleted, only archived rows stay forever.</summary>
public sealed class Punch
{
    public long Id { get; set; }

    /// <summary>Resolved employee, when the PIN is known.</summary>
    public string? EmployeeId { get; set; }

    public string? DeviceId { get; set; }
    public string? DeviceSerial { get; set; }

    /// <summary>PIN / user id punched on the terminal.</summary>
    public string DevicePin { get; set; } = string.Empty;

    public DateTime PunchTimeUtc { get; set; }
    public DateTime PunchTimeLocal { get; set; }

    public int VerifyMode { get; set; }
    public int PunchState { get; set; }
    public int WorkCode { get; set; }

    public PunchSource Source { get; set; }
    public bool IsManual { get; set; }

    /// <summary>DeviceSerial|Pin|PunchTimeUtc — the strict deduplication key.</summary>
    public string DedupeKey { get; set; } = string.Empty;

    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public string? Note { get; set; }
}

/// <summary>
/// An audited HR adjustment: manual completion punch, mission, exit permit or leave.
/// Used to credit official exceptions without ever touching the raw scans.
/// </summary>
public sealed class AttendanceAdjustment : ArchivableEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string EmployeeId { get; set; } = string.Empty;
    public DateTime WorkDate { get; set; }
    public AdjustmentKind Kind { get; set; }

    /// <summary>For a manual punch: the punch time. For a mission/permit: the start.</summary>
    public DateTime? StartLocal { get; set; }

    /// <summary>For a mission/permit: the end.</summary>
    public DateTime? EndLocal { get; set; }

    /// <summary>Credited minutes (full-day leave is credited with the rule target).</summary>
    public int HoursCreditedMinutes { get; set; }

    public string? Reference { get; set; }
    public string? Note { get; set; }
    public string? CreatedBy { get; set; }

    public bool IsFullDayLeave => Kind is AdjustmentKind.AnnualLeave or AdjustmentKind.SickLeave or AdjustmentKind.OtherLeave;

    public TimeSpan? StartTime => StartLocal?.TimeOfDay;
}
