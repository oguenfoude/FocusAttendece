-- Focus Attendance — initial schema.
-- Design rules: no physical deletes (archive flags everywhere), raw punches immutable,
-- strict deduplication of device logs, full audit trail.

CREATE TABLE IF NOT EXISTS WorkRules (
    Id                          TEXT PRIMARY KEY NOT NULL,
    Code                        TEXT NOT NULL,
    NameAr                      TEXT NOT NULL,
    NameFr                      TEXT NOT NULL,
    Mode                        INTEGER NOT NULL DEFAULT 0,
    MorningStartMin             INTEGER NOT NULL DEFAULT 480,
    LunchStartMin               INTEGER NOT NULL DEFAULT 720,
    LunchEndMin                 INTEGER NOT NULL DEFAULT 780,
    EveningEndMin               INTEGER NOT NULL DEFAULT 990,
    MorningGraceMinutes         INTEGER NOT NULL DEFAULT 15,
    AfternoonGraceMinutes       INTEGER NOT NULL DEFAULT 15,
    EarlyLeaveGraceMinutes      INTEGER NOT NULL DEFAULT 5,
    AutoDeductLunchBreak        INTEGER NOT NULL DEFAULT 1,
    LunchDeductionMinutes       INTEGER NOT NULL DEFAULT 60,
    AntiBounceMinutes           INTEGER NOT NULL DEFAULT 3,
    StandardDailyTargetMinutes  INTEGER NOT NULL DEFAULT 450,
    IsDefault                   INTEGER NOT NULL DEFAULT 0,
    IsArchived                  INTEGER NOT NULL DEFAULT 0,
    ArchivedAtUtc               TEXT NULL,
    ArchiveReason               TEXT NULL,
    ArchivedBy                  TEXT NULL,
    CreatedAtUtc                TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_WorkRules_Code ON WorkRules(Code);
CREATE INDEX IF NOT EXISTS IX_WorkRules_Archived ON WorkRules(IsArchived);

CREATE TABLE IF NOT EXISTS Sites (
    Id              TEXT PRIMARY KEY NOT NULL,
    Code            TEXT NOT NULL,
    NameAr          TEXT NOT NULL,
    NameFr          TEXT NOT NULL,
    Address         TEXT NULL,
    IsArchived      INTEGER NOT NULL DEFAULT 0,
    ArchivedAtUtc   TEXT NULL,
    ArchiveReason   TEXT NULL,
    ArchivedBy      TEXT NULL,
    CreatedAtUtc    TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Sites_Code ON Sites(Code);

CREATE TABLE IF NOT EXISTS Departments (
    Id                  TEXT PRIMARY KEY NOT NULL,
    Code                TEXT NOT NULL,
    NameAr              TEXT NOT NULL,
    NameFr              TEXT NOT NULL,
    SiteId              TEXT NULL REFERENCES Sites(Id),
    ParentDepartmentId  TEXT NULL REFERENCES Departments(Id),
    DefaultWorkRuleId   TEXT NULL REFERENCES WorkRules(Id),
    ManagerNameAr       TEXT NULL,
    ManagerNameFr       TEXT NULL,
    IsArchived          INTEGER NOT NULL DEFAULT 0,
    ArchivedAtUtc       TEXT NULL,
    ArchiveReason       TEXT NULL,
    ArchivedBy          TEXT NULL,
    CreatedAtUtc        TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Departments_Code ON Departments(Code);
CREATE INDEX IF NOT EXISTS IX_Departments_Site ON Departments(SiteId);

CREATE TABLE IF NOT EXISTS Devices (
    Id              TEXT PRIMARY KEY NOT NULL,
    Name            TEXT NOT NULL,
    IpAddress       TEXT NOT NULL,
    Port            INTEGER NOT NULL DEFAULT 4370,
    CommKey         INTEGER NOT NULL DEFAULT 0,
    SerialNumber    TEXT NULL,
    ModelName       TEXT NULL,
    FirmwareVersion TEXT NULL,
    MacAddress      TEXT NULL,
    Platform        TEXT NULL,
    DeviceClock     TEXT NULL,
    LastSeenUtc     TEXT NULL,
    LastSyncUtc     TEXT NULL,
    LastPullUtc     TEXT NULL,
    UserCount       INTEGER NOT NULL DEFAULT 0,
    FingerCount     INTEGER NOT NULL DEFAULT 0,
    FaceCount       INTEGER NOT NULL DEFAULT 0,
    PunchCount      INTEGER NOT NULL DEFAULT 0,
    IsEnabled       INTEGER NOT NULL DEFAULT 1,
    Note            TEXT NULL,
    IsArchived      INTEGER NOT NULL DEFAULT 0,
    ArchivedAtUtc   TEXT NULL,
    ArchiveReason   TEXT NULL,
    ArchivedBy      TEXT NULL,
    CreatedAtUtc    TEXT NOT NULL,
    UpdatedAtUtc    TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Devices_Serial ON Devices(SerialNumber)
    WHERE SerialNumber IS NOT NULL AND SerialNumber <> '';
CREATE UNIQUE INDEX IF NOT EXISTS UX_Devices_Endpoint ON Devices(IpAddress, Port)
    WHERE IsArchived = 0;
CREATE INDEX IF NOT EXISTS IX_Devices_Archived ON Devices(IsArchived);

CREATE TABLE IF NOT EXISTS Employees (
    Id                  TEXT PRIMARY KEY NOT NULL,
    Matricule           TEXT NOT NULL,
    DevicePin           TEXT NULL,
    FullNameAr          TEXT NULL,
    FirstNameFr         TEXT NULL,
    LastNameFr          TEXT NULL,
    SiteId              TEXT NULL REFERENCES Sites(Id),
    DepartmentId        TEXT NULL REFERENCES Departments(Id),
    JobTitleAr          TEXT NULL,
    JobTitleFr          TEXT NULL,
    Phone               TEXT NULL,
    BirthDate           TEXT NULL,
    HireDate            TEXT NULL,
    EndDate             TEXT NULL,
    WorkRuleId          TEXT NULL REFERENCES WorkRules(Id),
    ScheduledWorkDays   INTEGER NOT NULL DEFAULT 31,
    BaseSalaryDzd       TEXT NOT NULL DEFAULT '0',
    DailyRateDzd        TEXT NOT NULL DEFAULT '0',
    HourlyRateDzd       TEXT NOT NULL DEFAULT '0',
    OvertimeMultiplier  TEXT NOT NULL DEFAULT '1',
    PaymentAccount      TEXT NULL,
    PrimaryDeviceId     TEXT NULL REFERENCES Devices(Id),
    DevicePrivilege     INTEGER NOT NULL DEFAULT 0,
    CardNumber          INTEGER NOT NULL DEFAULT 0,
    DevicePassword      TEXT NULL,
    FpCount             INTEGER NOT NULL DEFAULT 0,
    FaceCount           INTEGER NOT NULL DEFAULT 0,
    StatusNote          TEXT NULL,
    IsArchived          INTEGER NOT NULL DEFAULT 0,
    ArchivedAtUtc       TEXT NULL,
    ArchiveReason       TEXT NULL,
    ArchivedBy          TEXT NULL,
    CreatedAtUtc        TEXT NOT NULL,
    UpdatedAtUtc        TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Employees_Matricule ON Employees(Matricule);
CREATE INDEX IF NOT EXISTS IX_Employees_Pin ON Employees(DevicePin);
CREATE INDEX IF NOT EXISTS IX_Employees_Department ON Employees(DepartmentId);
CREATE INDEX IF NOT EXISTS IX_Employees_Archived ON Employees(IsArchived);

CREATE TABLE IF NOT EXISTS Punches (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    EmployeeId      TEXT NULL REFERENCES Employees(Id),
    DeviceId        TEXT NULL REFERENCES Devices(Id),
    DeviceSerial    TEXT NULL,
    DevicePin       TEXT NOT NULL,
    PunchTimeUtc    TEXT NOT NULL,
    PunchTimeLocal  TEXT NOT NULL,
    VerifyMode      INTEGER NOT NULL DEFAULT 0,
    PunchState      INTEGER NOT NULL DEFAULT 0,
    WorkCode        INTEGER NOT NULL DEFAULT 0,
    Source          INTEGER NOT NULL DEFAULT 0,
    IsManual        INTEGER NOT NULL DEFAULT 0,
    DedupeKey       TEXT NOT NULL,
    ReceivedAtUtc   TEXT NOT NULL,
    Note            TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Punches_Dedupe ON Punches(DedupeKey);
CREATE INDEX IF NOT EXISTS IX_Punches_LocalTime ON Punches(PunchTimeLocal);
CREATE INDEX IF NOT EXISTS IX_Punches_EmployeeTime ON Punches(EmployeeId, PunchTimeLocal);
CREATE INDEX IF NOT EXISTS IX_Punches_PinTime ON Punches(DevicePin, PunchTimeLocal);
CREATE INDEX IF NOT EXISTS IX_Punches_Device ON Punches(DeviceId);

CREATE TABLE IF NOT EXISTS AttendanceAdjustments (
    Id                      TEXT PRIMARY KEY NOT NULL,
    EmployeeId              TEXT NOT NULL REFERENCES Employees(Id),
    WorkDate                TEXT NOT NULL,
    Kind                    INTEGER NOT NULL DEFAULT 0,
    StartLocal              TEXT NULL,
    EndLocal                TEXT NULL,
    HoursCreditedMinutes    INTEGER NOT NULL DEFAULT 0,
    Reference               TEXT NULL,
    Note                    TEXT NULL,
    CreatedBy               TEXT NULL,
    IsArchived              INTEGER NOT NULL DEFAULT 0,
    ArchivedAtUtc           TEXT NULL,
    ArchiveReason           TEXT NULL,
    ArchivedBy              TEXT NULL,
    CreatedAtUtc            TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Adjustments_EmployeeDate ON AttendanceAdjustments(EmployeeId, WorkDate);

CREATE TABLE IF NOT EXISTS AuditLogs (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    TimestampUtc    TEXT NOT NULL,
    Actor           TEXT NOT NULL,
    Action          TEXT NOT NULL,
    EntityType      TEXT NOT NULL,
    EntityId        TEXT NULL,
    Details         TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_Audit_Timestamp ON AuditLogs(TimestampUtc);

CREATE TABLE IF NOT EXISTS AppUsers (
    Id                  TEXT PRIMARY KEY NOT NULL,
    Username            TEXT NOT NULL,
    DisplayName         TEXT NOT NULL,
    PasswordHash        TEXT NOT NULL,
    PasswordSalt        TEXT NOT NULL,
    PasswordIterations  INTEGER NOT NULL,
    Role                INTEGER NOT NULL,
    MustChangePassword  INTEGER NOT NULL DEFAULT 0,
    LastLoginUtc        TEXT NULL,
    IsArchived          INTEGER NOT NULL DEFAULT 0,
    ArchivedAtUtc       TEXT NULL,
    ArchiveReason       TEXT NULL,
    ArchivedBy          TEXT NULL,
    CreatedAtUtc        TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_AppUsers_Username ON AppUsers(Username);

CREATE TABLE IF NOT EXISTS AppSettings (
    Key     TEXT PRIMARY KEY NOT NULL,
    Value   TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS HardwareOperationLogs (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    TimestampUtc TEXT NOT NULL,
    DeviceId     TEXT NULL,
    Operation    TEXT NOT NULL,
    Success      INTEGER NOT NULL,
    DurationMs   INTEGER NOT NULL DEFAULT 0,
    Message      TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_HwLog_Timestamp ON HardwareOperationLogs(TimestampUtc);
