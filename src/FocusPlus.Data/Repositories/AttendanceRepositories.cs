using Dapper;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Data.Repositories;

public sealed class AttendanceAdjustmentRepository(IConnectionFactory factory) : RepositoryBase(factory)
{
    public IReadOnlyList<AttendanceAdjustment> GetByRange(DateTime fromDate, DateTime toDate)
    {
        using var connection = Factory.Open();
        return connection.Query<AttendanceAdjustment>(
            """
            SELECT * FROM AttendanceAdjustments
             WHERE IsArchived = 0 AND WorkDate >= @fromDate AND WorkDate <= @toDate
             ORDER BY WorkDate;
            """,
            new { fromDate = fromDate.Date, toDate = toDate.Date }).ToList();
    }

    public IReadOnlyList<AttendanceAdjustment> GetForEmployee(string employeeId, DateTime fromDate, DateTime toDate)
    {
        using var connection = Factory.Open();
        return connection.Query<AttendanceAdjustment>(
            """
            SELECT * FROM AttendanceAdjustments
             WHERE EmployeeId = @employeeId AND WorkDate >= @fromDate AND WorkDate <= @toDate
             ORDER BY WorkDate;
            """,
            new { employeeId, fromDate = fromDate.Date, toDate = toDate.Date }).ToList();
    }

    public IReadOnlyDictionary<string, List<AttendanceAdjustment>> GetByEmployee(DateTime fromDate, DateTime toDate)
    {
        using var connection = Factory.Open();
        var rows = connection.Query<AttendanceAdjustment>(
            """
            SELECT * FROM AttendanceAdjustments
             WHERE IsArchived = 0 AND WorkDate >= @fromDate AND WorkDate <= @toDate
             ORDER BY WorkDate;
            """,
            new { fromDate = fromDate.Date, toDate = toDate.Date });

        return rows.GroupBy(r => r.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());
    }

    public void Upsert(AttendanceAdjustment adjustment)
    {
        using var connection = Factory.Open();
        connection.Execute(
            """
            INSERT INTO AttendanceAdjustments (Id, EmployeeId, WorkDate, Kind, StartLocal, EndLocal,
                                               HoursCreditedMinutes, Reference, Note, CreatedBy, IsArchived,
                                               ArchivedAtUtc, ArchiveReason, ArchivedBy, CreatedAtUtc)
            VALUES (@Id, @EmployeeId, @WorkDate, @Kind, @StartLocal, @EndLocal, @HoursCreditedMinutes,
                    @Reference, @Note, @CreatedBy, @IsArchived, @ArchivedAtUtc, @ArchiveReason, @ArchivedBy,
                    @CreatedAtUtc)
            ON CONFLICT(Id) DO UPDATE SET
                EmployeeId = excluded.EmployeeId, WorkDate = excluded.WorkDate, Kind = excluded.Kind,
                StartLocal = excluded.StartLocal, EndLocal = excluded.EndLocal,
                HoursCreditedMinutes = excluded.HoursCreditedMinutes, Reference = excluded.Reference,
                Note = excluded.Note, IsArchived = excluded.IsArchived, ArchivedAtUtc = excluded.ArchivedAtUtc,
                ArchiveReason = excluded.ArchiveReason, ArchivedBy = excluded.ArchivedBy;
            """,
            adjustment);
    }

    public void Archive(string id, string reason, string actor)
    {
        using var connection = Factory.Open();
        connection.Execute(
            """
            UPDATE AttendanceAdjustments
               SET IsArchived = 1, ArchivedAtUtc = @now, ArchiveReason = @reason, ArchivedBy = @actor
             WHERE Id = @id;
            """,
            new { id, now = DateTime.UtcNow, reason, actor });
    }

    public void Restore(string id, string actor)
    {
        using var connection = Factory.Open();
        connection.Execute(
            """
            UPDATE AttendanceAdjustments
               SET IsArchived = 0, ArchivedAtUtc = NULL, ArchiveReason = NULL, ArchivedBy = @actor
             WHERE Id = @id;
            """,
            new { id, actor });
    }
}

public sealed class AuditRepository(IConnectionFactory factory) : RepositoryBase(factory)
{
    public void Add(AuditLogEntry entry)
    {
        using var connection = Factory.Open();
        connection.Execute(
            """
            INSERT INTO AuditLogs (TimestampUtc, Actor, Action, EntityType, EntityId, Details)
            VALUES (@TimestampUtc, @Actor, @Action, @EntityType, @EntityId, @Details);
            """,
            entry);
    }

    public IReadOnlyList<AuditLogEntry> Recent(int limit = 100)
    {
        using var connection = Factory.Open();
        return connection.Query<AuditLogEntry>(
            "SELECT * FROM AuditLogs ORDER BY TimestampUtc DESC LIMIT @limit;",
            new { limit }).ToList();
    }
}

public sealed record HardwareOperationLog
{
    public long Id { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string? DeviceId { get; init; }
    public string Operation { get; init; } = string.Empty;
    public bool Success { get; init; }
    public int DurationMs { get; init; }
    public string? Message { get; init; }
}

public sealed class HardwareOperationLogRepository(IConnectionFactory factory) : RepositoryBase(factory)
{
    public void Add(HardwareOperationLog entry)
    {
        using var connection = Factory.Open();
        connection.Execute(
            """
            INSERT INTO HardwareOperationLogs (TimestampUtc, DeviceId, Operation, Success, DurationMs, Message)
            VALUES (@TimestampUtc, @DeviceId, @Operation, @Success, @DurationMs, @Message);
            """,
            entry);
    }

    public IReadOnlyList<HardwareOperationLog> Recent(int limit = 50)
    {
        using var connection = Factory.Open();
        return connection.Query<HardwareOperationLog>(
            "SELECT * FROM HardwareOperationLogs ORDER BY TimestampUtc DESC LIMIT @limit;",
            new { limit }).ToList();
    }
}
