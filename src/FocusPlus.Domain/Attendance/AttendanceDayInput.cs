using FocusPlus.Domain.Entities;

namespace FocusPlus.Domain.Attendance;

/// <summary>Everything the engine needs to compute one employee-day, across all seven days of the week.</summary>
public sealed record AttendanceDayInput
{
    public required DateTime Date { get; init; }
    public required WorkRule Rule { get; init; }
    public required bool IsScheduledDay { get; init; }
    public IReadOnlyList<DateTime> Punches { get; init; } = [];
    public IReadOnlyList<AttendanceAdjustment> Adjustments { get; init; } = [];
    public decimal HourlyRateDzd { get; init; }
    public decimal OvertimeMultiplier { get; init; } = 1m;
}

/// <summary>Computed attendance for a single employee-day.</summary>
public sealed record AttendanceDayResult
{
    public DateTime Date { get; init; }
    public DayOfWeek DayOfWeek => Date.DayOfWeek;
    public bool IsScheduledDay { get; init; }
    public AttendanceStatus Status { get; init; }

    public DateTime? MorningIn { get; init; }
    public DateTime? LunchOut { get; init; }
    public DateTime? LunchIn { get; init; }
    public DateTime? EveningOut { get; init; }
    public IReadOnlyList<DateTime> ExtraScans { get; init; } = [];

    public int RawScanCount { get; init; }
    public int RetainedScanCount { get; init; }
    public int ManualScanCount { get; init; }
    public int BreakMinutes { get; init; }

    public int RegularWorkedMinutes { get; init; }
    public int ExtraWorkedMinutes { get; init; }
    public int CreditedMinutes { get; init; }
    public int TotalNetMinutes { get; init; }
    public int EffectiveTotalMinutes { get; init; }

    public int LateMinutes { get; init; }
    public int EarlyLeaveMinutes { get; init; }

    public decimal EarnedAmountDzd { get; init; }
    public decimal DeductionAmountDzd { get; init; }
    public IReadOnlyList<AttendanceNote> Notes { get; init; } = [];

    /// <summary>True when there is anything worth showing in the sheet.</summary>
    public bool HasActivity => RawScanCount > 0 || CreditedMinutes > 0 || Status is AttendanceStatus.Absent;

    public bool IsLate => LateMinutes > 0;

    public string StatusKey => Status.ToString();
}
