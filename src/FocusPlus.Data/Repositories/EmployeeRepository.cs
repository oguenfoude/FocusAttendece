using System.Text;
using Dapper;
using FocusPlus.Domain;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Data.Repositories;

/// <summary>Server side employee filtering: device, service/department, shift, weekday, status and free text.</summary>
public sealed record EmployeeFilter
{
    public string? Search { get; init; }
    public string? DepartmentId { get; init; }
    public string? DeviceId { get; init; }
    public string? WorkRuleId { get; init; }
    public DayOfWeek? WorkDay { get; init; }
    public bool IncludeArchived { get; init; }
    public bool OnlyArchived { get; init; }
}

public sealed class EmployeeRepository(IConnectionFactory factory) : RepositoryBase(factory)
{
    public IReadOnlyList<Employee> Query(EmployeeFilter filter)
    {
        using var connection = Factory.Open();
        var sql = new StringBuilder("SELECT * FROM Employees WHERE 1 = 1");

        if (filter.OnlyArchived)
        {
            sql.Append(" AND IsArchived = 1");
        }
        else if (!filter.IncludeArchived)
        {
            sql.Append(" AND IsArchived = 0");
        }

        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(filter.DepartmentId))
        {
            sql.Append(" AND DepartmentId = @departmentId");
            parameters.Add("departmentId", filter.DepartmentId);
        }

        if (!string.IsNullOrWhiteSpace(filter.DeviceId))
        {
            sql.Append(" AND PrimaryDeviceId = @deviceId");
            parameters.Add("deviceId", filter.DeviceId);
        }

        if (!string.IsNullOrWhiteSpace(filter.WorkRuleId))
        {
            sql.Append(" AND WorkRuleId = @workRuleId");
            parameters.Add("workRuleId", filter.WorkRuleId);
        }

        if (filter.WorkDay is { } day)
        {
            var mask = (int)day.ToFlag();
            sql.Append(" AND (ScheduledWorkDays & @mask) = @mask");
            parameters.Add("mask", mask);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            sql.Append(
                """
                 AND (
                    Matricule LIKE @like OR
                    IFNULL(DevicePin, '') LIKE @like OR
                    IFNULL(FullNameAr, '') LIKE @like OR
                    IFNULL(FirstNameFr, '') LIKE @like OR
                    IFNULL(LastNameFr, '') LIKE @like OR
                    IFNULL(JobTitleAr, '') LIKE @like OR
                    IFNULL(JobTitleFr, '') LIKE @like OR
                    IFNULL(Phone, '') LIKE @like)
                """);
            parameters.Add("like", $"%{filter.Search.Trim()}%");
        }

        sql.Append(" ORDER BY IsArchived, LastNameFr, FirstNameFr, Matricule;");
        return connection.Query<Employee>(sql.ToString(), parameters).ToList();
    }

    public IReadOnlyList<Employee> GetAll(bool includeArchived = false) =>
        Query(new EmployeeFilter { IncludeArchived = includeArchived });

    public Employee? Get(string id)
    {
        using var connection = Factory.Open();
        return connection.QuerySingleOrDefault<Employee>("SELECT * FROM Employees WHERE Id = @id;", new { id });
    }

    public Employee? GetByMatricule(string matricule)
    {
        using var connection = Factory.Open();
        return connection.QuerySingleOrDefault<Employee>(
            "SELECT * FROM Employees WHERE Matricule = @matricule LIMIT 1;",
            new { matricule });
    }

    public Employee? GetByPin(string pin)
    {
        using var connection = Factory.Open();
        return connection.QuerySingleOrDefault<Employee>(
            "SELECT * FROM Employees WHERE DevicePin = @pin AND IsArchived = 0 ORDER BY CreatedAtUtc LIMIT 1;",
            new { pin });
    }

    public void Upsert(Employee employee)
    {
        employee.UpdatedAtUtc = DateTime.UtcNow;
        using var connection = Factory.Open();
        connection.Execute(
            """
            INSERT INTO Employees (Id, Matricule, DevicePin, FullNameAr, FirstNameFr, LastNameFr, SiteId, DepartmentId,
                                   JobTitleAr, JobTitleFr, Phone, BirthDate, HireDate, EndDate, WorkRuleId,
                                   ScheduledWorkDays, BaseSalaryDzd, DailyRateDzd, HourlyRateDzd, OvertimeMultiplier,
                                   PaymentAccount, PrimaryDeviceId, DevicePrivilege, CardNumber, DevicePassword,
                                   FpCount, FaceCount, StatusNote, IsArchived, ArchivedAtUtc, ArchiveReason, ArchivedBy,
                                   CreatedAtUtc, UpdatedAtUtc)
            VALUES (@Id, @Matricule, @DevicePin, @FullNameAr, @FirstNameFr, @LastNameFr, @SiteId, @DepartmentId,
                    @JobTitleAr, @JobTitleFr, @Phone, @BirthDate, @HireDate, @EndDate, @WorkRuleId,
                    @ScheduledWorkDays, @BaseSalaryDzd, @DailyRateDzd, @HourlyRateDzd, @OvertimeMultiplier,
                    @PaymentAccount, @PrimaryDeviceId, @DevicePrivilege, @CardNumber, @DevicePassword,
                    @FpCount, @FaceCount, @StatusNote, @IsArchived, @ArchivedAtUtc, @ArchiveReason, @ArchivedBy,
                    @CreatedAtUtc, @UpdatedAtUtc)
            ON CONFLICT(Id) DO UPDATE SET
                Matricule = excluded.Matricule, DevicePin = excluded.DevicePin,
                FullNameAr = excluded.FullNameAr, FirstNameFr = excluded.FirstNameFr,
                LastNameFr = excluded.LastNameFr, SiteId = excluded.SiteId,
                DepartmentId = excluded.DepartmentId, JobTitleAr = excluded.JobTitleAr,
                JobTitleFr = excluded.JobTitleFr, Phone = excluded.Phone, BirthDate = excluded.BirthDate,
                HireDate = excluded.HireDate, EndDate = excluded.EndDate, WorkRuleId = excluded.WorkRuleId,
                ScheduledWorkDays = excluded.ScheduledWorkDays, BaseSalaryDzd = excluded.BaseSalaryDzd,
                DailyRateDzd = excluded.DailyRateDzd, HourlyRateDzd = excluded.HourlyRateDzd,
                OvertimeMultiplier = excluded.OvertimeMultiplier, PaymentAccount = excluded.PaymentAccount,
                PrimaryDeviceId = excluded.PrimaryDeviceId, DevicePrivilege = excluded.DevicePrivilege,
                CardNumber = excluded.CardNumber, DevicePassword = excluded.DevicePassword,
                FpCount = excluded.FpCount, FaceCount = excluded.FaceCount, StatusNote = excluded.StatusNote,
                IsArchived = excluded.IsArchived, ArchivedAtUtc = excluded.ArchivedAtUtc,
                ArchiveReason = excluded.ArchiveReason, ArchivedBy = excluded.ArchivedBy,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """,
            employee);
    }

    public void Archive(string id, string reason, string actor)
    {
        using var connection = Factory.Open();
        connection.Execute(
            """
            UPDATE Employees
               SET IsArchived = 1, ArchivedAtUtc = @now, ArchiveReason = @reason, ArchivedBy = @actor,
                   UpdatedAtUtc = @now
             WHERE Id = @id;
            """,
            new { id, now = DateTime.UtcNow, reason, actor });
    }

    public void Restore(string id, string actor)
    {
        using var connection = Factory.Open();
        connection.Execute(
            """
            UPDATE Employees
               SET IsArchived = 0, ArchivedAtUtc = NULL, ArchiveReason = NULL, ArchivedBy = @actor,
                   UpdatedAtUtc = @now
             WHERE Id = @id;
            """,
            new { id, now = DateTime.UtcNow, actor });
    }

    public int Count(bool includeArchived = false)
    {
        using var connection = Factory.Open();
        return connection.ExecuteScalar<int>(
            includeArchived
                ? "SELECT COUNT(*) FROM Employees;"
                : "SELECT COUNT(*) FROM Employees WHERE IsArchived = 0;");
    }
}
