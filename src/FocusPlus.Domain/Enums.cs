namespace FocusPlus.Domain;

/// <summary>How a work rule measures presence.</summary>
public enum WorkMode
{
    /// <summary>Fixed morning/lunch/evening window (official administrative shift).</summary>
    FixedShift = 0,

    /// <summary>Open hours: every completed IN/OUT pair is credited, whatever the hour.</summary>
    FreeTime = 1
}

/// <summary>Seven day week. Persisted as a bit mask (Sunday = bit 0).</summary>
[Flags]
public enum WeekDays
{
    None = 0,
    Sunday = 1 << 0,
    Monday = 1 << 1,
    Tuesday = 1 << 2,
    Wednesday = 1 << 3,
    Thursday = 1 << 4,
    Friday = 1 << 5,
    Saturday = 1 << 6,

    /// <summary>Official OPGI administrative week: Sunday to Thursday.</summary>
    Administrative = Sunday | Monday | Tuesday | Wednesday | Thursday,

    All = Administrative | Friday | Saturday
}

public enum DeviceConnectivity
{
    Unknown = 0,
    Online = 1,
    Offline = 2
}

public enum PunchSource
{
    Device = 0,
    Manual = 1
}

public enum AttendanceStatus
{
    /// <summary>No data at all (employee not concerned by this date).</summary>
    NoData = 0,

    /// <summary>Not a scheduled working day and no scan: legitimate weekly rest.</summary>
    RestDay = 1,

    Present = 2,
    Late = 3,
    EarlyLeave = 4,
    LateAndEarlyLeave = 5,
    Absent = 6,
    Leave = 7,
    Mission = 8,
    MissingOutScan = 9,

    /// <summary>Worked on a non-scheduled day: extra day / overtime.</summary>
    ExtraDay = 10
}

public enum AdjustmentKind
{
    /// <summary>Verified manual completion of a missing scan (audited).</summary>
    ManualPunch = 0,

    /// <summary>Ordre de mission — أمر بمهمة.</summary>
    Mission = 1,

    /// <summary>Bon de sortie — رخصة خروج ساعية.</summary>
    ExitPermit = 2,

    /// <summary>Congé annuel — عطلة سنوية.</summary>
    AnnualLeave = 3,

    /// <summary>Congé maladie — عطلة مرضية.</summary>
    SickLeave = 4,

    /// <summary>Other justified leave.</summary>
    OtherLeave = 5
}

public enum UserRole
{
    SuperAdmin = 0,
    Operator = 1
}

/// <summary>Identifies what produced an attendance note, so the UI can localise it.</summary>
public enum AttendanceNote
{
    MissingOutScan = 0,
    MissingInScan = 1,
    LateArrival = 2,
    EarlyLeave = 3,
    LunchDeducted = 4,
    OffDayWorked = 5,
    ManualPunchUsed = 6,
    SingleScan = 7,
    MissionCredited = 8,
    ExitPermitCredited = 9,
    LeaveCredited = 10,
    AntiBounceFiltered = 11,
    TargetReached = 12,
    BelowTarget = 13
}
