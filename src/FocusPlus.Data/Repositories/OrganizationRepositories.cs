using Dapper;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Data.Repositories;

public abstract class RepositoryBase(IConnectionFactory factory)
{
    protected IConnectionFactory Factory { get; } = factory;
}

public sealed class SiteRepository(IConnectionFactory factory) : RepositoryBase(factory)
{
    public IReadOnlyList<Site> GetAll(bool includeArchived = false)
    {
        using var connection = Factory.Open();
        var sql = includeArchived
            ? "SELECT * FROM Sites ORDER BY Code;"
            : "SELECT * FROM Sites WHERE IsArchived = 0 ORDER BY Code;";
        return connection.Query<Site>(sql).ToList();
    }

    public Site? Get(string id)
    {
        using var connection = Factory.Open();
        return connection.QuerySingleOrDefault<Site>("SELECT * FROM Sites WHERE Id = @id;", new { id });
    }

    public void Upsert(Site site)
    {
        using var connection = Factory.Open();
        connection.Execute(
            """
            INSERT INTO Sites (Id, Code, NameAr, NameFr, Address, IsArchived, ArchivedAtUtc, ArchiveReason,
                               ArchivedBy, CreatedAtUtc)
            VALUES (@Id, @Code, @NameAr, @NameFr, @Address, @IsArchived, @ArchivedAtUtc, @ArchiveReason,
                    @ArchivedBy, @CreatedAtUtc)
            ON CONFLICT(Id) DO UPDATE SET
                Code = excluded.Code, NameAr = excluded.NameAr, NameFr = excluded.NameFr,
                Address = excluded.Address, IsArchived = excluded.IsArchived,
                ArchivedAtUtc = excluded.ArchivedAtUtc, ArchiveReason = excluded.ArchiveReason,
                ArchivedBy = excluded.ArchivedBy;
            """,
            site);
    }

    public void Archive(string id, string reason, string actor)
    {
        using var connection = Factory.Open();
        connection.Execute(
            "UPDATE Sites SET IsArchived = 1, ArchivedAtUtc = @now, ArchiveReason = @reason, ArchivedBy = @actor WHERE Id = @id;",
            new { id, now = DateTime.UtcNow, reason, actor });
    }

    public void Restore(string id, string actor)
    {
        using var connection = Factory.Open();
        connection.Execute(
            "UPDATE Sites SET IsArchived = 0, ArchivedAtUtc = NULL, ArchiveReason = NULL, ArchivedBy = @actor WHERE Id = @id;",
            new { id, actor });
    }
}

public sealed class DepartmentRepository(IConnectionFactory factory) : RepositoryBase(factory)
{
    public IReadOnlyList<Department> GetAll(bool includeArchived = false)
    {
        using var connection = Factory.Open();
        var sql = includeArchived
            ? "SELECT * FROM Departments ORDER BY Code;"
            : "SELECT * FROM Departments WHERE IsArchived = 0 ORDER BY Code;";
        return connection.Query<Department>(sql).ToList();
    }

    public Department? Get(string id)
    {
        using var connection = Factory.Open();
        return connection.QuerySingleOrDefault<Department>("SELECT * FROM Departments WHERE Id = @id;", new { id });
    }

    public void Upsert(Department department)
    {
        using var connection = Factory.Open();
        connection.Execute(
            """
            INSERT INTO Departments (Id, Code, NameAr, NameFr, SiteId, ParentDepartmentId, DefaultWorkRuleId,
                                     ManagerNameAr, ManagerNameFr, IsArchived, ArchivedAtUtc, ArchiveReason,
                                     ArchivedBy, CreatedAtUtc)
            VALUES (@Id, @Code, @NameAr, @NameFr, @SiteId, @ParentDepartmentId, @DefaultWorkRuleId,
                    @ManagerNameAr, @ManagerNameFr, @IsArchived, @ArchivedAtUtc, @ArchiveReason,
                    @ArchivedBy, @CreatedAtUtc)
            ON CONFLICT(Id) DO UPDATE SET
                Code = excluded.Code, NameAr = excluded.NameAr, NameFr = excluded.NameFr,
                SiteId = excluded.SiteId, ParentDepartmentId = excluded.ParentDepartmentId,
                DefaultWorkRuleId = excluded.DefaultWorkRuleId, ManagerNameAr = excluded.ManagerNameAr,
                ManagerNameFr = excluded.ManagerNameFr, IsArchived = excluded.IsArchived,
                ArchivedAtUtc = excluded.ArchivedAtUtc, ArchiveReason = excluded.ArchiveReason,
                ArchivedBy = excluded.ArchivedBy;
            """,
            department);
    }

    public void Archive(string id, string reason, string actor)
    {
        using var connection = Factory.Open();
        connection.Execute(
            "UPDATE Departments SET IsArchived = 1, ArchivedAtUtc = @now, ArchiveReason = @reason, ArchivedBy = @actor WHERE Id = @id;",
            new { id, now = DateTime.UtcNow, reason, actor });
    }

    public void Restore(string id, string actor)
    {
        using var connection = Factory.Open();
        connection.Execute(
            "UPDATE Departments SET IsArchived = 0, ArchivedAtUtc = NULL, ArchiveReason = NULL, ArchivedBy = @actor WHERE Id = @id;",
            new { id, actor });
    }

    /// <summary>Number of (non archived) employees attached to each department.</summary>
    public IReadOnlyDictionary<string, int> GetEmployeeCounts()
    {
        using var connection = Factory.Open();
        return connection
            .Query<CountRow>(
                "SELECT DepartmentId AS GroupKey, COUNT(*) AS Total FROM Employees WHERE IsArchived = 0 AND DepartmentId IS NOT NULL GROUP BY DepartmentId;")
            .Where(x => x.GroupKey is not null)
            .ToDictionary(x => x.GroupKey, x => x.Total);
    }
}

public sealed class WorkRuleRepository(IConnectionFactory factory) : RepositoryBase(factory)
{
    public IReadOnlyList<WorkRule> GetAll(bool includeArchived = false)
    {
        using var connection = Factory.Open();
        var sql = includeArchived
            ? "SELECT * FROM WorkRules ORDER BY IsDefault DESC, Code;"
            : "SELECT * FROM WorkRules WHERE IsArchived = 0 ORDER BY IsDefault DESC, Code;";
        return connection.Query<WorkRule>(sql).ToList();
    }

    public WorkRule? Get(string id)
    {
        using var connection = Factory.Open();
        return connection.QuerySingleOrDefault<WorkRule>("SELECT * FROM WorkRules WHERE Id = @id;", new { id });
    }

    public WorkRule GetDefault()
    {
        using var connection = Factory.Open();
        return connection.QueryFirstOrDefault<WorkRule>("SELECT * FROM WorkRules WHERE IsArchived = 0 ORDER BY IsDefault DESC, CreatedAtUtc LIMIT 1;")
               ?? WorkRule.CreateOfficialDefault();
    }

    public void Upsert(WorkRule rule)
    {
        using var connection = Factory.Open();
        using var transaction = connection.BeginTransaction();
        if (rule.IsDefault)
        {
            connection.Execute("UPDATE WorkRules SET IsDefault = 0 WHERE IsArchived = 0;", transaction: transaction);
        }

        connection.Execute(
            """
            INSERT INTO WorkRules (Id, Code, NameAr, NameFr, Mode, MorningStartMin, LunchStartMin, LunchEndMin,
                                   EveningEndMin, MorningGraceMinutes, AfternoonGraceMinutes, EarlyLeaveGraceMinutes,
                                   AutoDeductLunchBreak, LunchDeductionMinutes, AntiBounceMinutes,
                                   StandardDailyTargetMinutes, IsDefault, IsArchived, ArchivedAtUtc, ArchiveReason,
                                   ArchivedBy, CreatedAtUtc)
            VALUES (@Id, @Code, @NameAr, @NameFr, @Mode, @MorningStartMin, @LunchStartMin, @LunchEndMin,
                    @EveningEndMin, @MorningGraceMinutes, @AfternoonGraceMinutes, @EarlyLeaveGraceMinutes,
                    @AutoDeductLunchBreak, @LunchDeductionMinutes, @AntiBounceMinutes,
                    @StandardDailyTargetMinutes, @IsDefault, @IsArchived, @ArchivedAtUtc, @ArchiveReason,
                    @ArchivedBy, @CreatedAtUtc)
            ON CONFLICT(Id) DO UPDATE SET
                Code = excluded.Code, NameAr = excluded.NameAr, NameFr = excluded.NameFr,
                Mode = excluded.Mode, MorningStartMin = excluded.MorningStartMin,
                LunchStartMin = excluded.LunchStartMin, LunchEndMin = excluded.LunchEndMin,
                EveningEndMin = excluded.EveningEndMin, MorningGraceMinutes = excluded.MorningGraceMinutes,
                AfternoonGraceMinutes = excluded.AfternoonGraceMinutes,
                EarlyLeaveGraceMinutes = excluded.EarlyLeaveGraceMinutes,
                AutoDeductLunchBreak = excluded.AutoDeductLunchBreak,
                LunchDeductionMinutes = excluded.LunchDeductionMinutes,
                AntiBounceMinutes = excluded.AntiBounceMinutes,
                StandardDailyTargetMinutes = excluded.StandardDailyTargetMinutes,
                IsDefault = excluded.IsDefault, IsArchived = excluded.IsArchived,
                ArchivedAtUtc = excluded.ArchivedAtUtc, ArchiveReason = excluded.ArchiveReason,
                ArchivedBy = excluded.ArchivedBy;
            """,
            rule,
            transaction);

        transaction.Commit();
    }

    public void Archive(string id, string reason, string actor)
    {
        using var connection = Factory.Open();
        connection.Execute(
            "UPDATE WorkRules SET IsArchived = 1, ArchivedAtUtc = @now, ArchiveReason = @reason, ArchivedBy = @actor, IsDefault = 0 WHERE Id = @id;",
            new { id, now = DateTime.UtcNow, reason, actor });
    }

    public void Restore(string id, string actor)
    {
        using var connection = Factory.Open();
        connection.Execute(
            "UPDATE WorkRules SET IsArchived = 0, ArchivedAtUtc = NULL, ArchiveReason = NULL, ArchivedBy = @actor WHERE Id = @id;",
            new { id, actor });
    }

    public IReadOnlyDictionary<string, int> GetEmployeeCounts()
    {
        using var connection = Factory.Open();
        return connection
            .Query<CountRow>(
                "SELECT WorkRuleId AS GroupKey, COUNT(*) AS Total FROM Employees WHERE IsArchived = 0 AND WorkRuleId IS NOT NULL GROUP BY WorkRuleId;")
            .ToDictionary(x => x.GroupKey, x => x.Total);
    }
}

/// <summary>Small projection used by grouped count queries.</summary>
public sealed class CountRow
{
    public string GroupKey { get; set; } = string.Empty;
    public int Total { get; set; }
}
