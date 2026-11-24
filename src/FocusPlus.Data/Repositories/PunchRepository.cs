using Dapper;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Data.Repositories;

public sealed class PunchRepository(IConnectionFactory factory) : RepositoryBase(factory)
{
    /// <summary>Inserts raw scans, ignoring duplicates through the unique dedupe key. Returns inserted rows.</summary>
    public int InsertBatch(IReadOnlyCollection<Punch> punches)
    {
        if (punches.Count == 0)
        {
            return 0;
        }

        using var connection = Factory.Open();
        using var transaction = connection.BeginTransaction();
        var inserted = 0;
        foreach (var punch in punches)
        {
            inserted += connection.Execute(
                """
                INSERT OR IGNORE INTO Punches (EmployeeId, DeviceId, DeviceSerial, DevicePin, PunchTimeUtc,
                                               PunchTimeLocal, VerifyMode, PunchState, WorkCode, Source, IsManual,
                                               DedupeKey, ReceivedAtUtc, Note)
                VALUES (@EmployeeId, @DeviceId, @DeviceSerial, @DevicePin, @PunchTimeUtc, @PunchTimeLocal,
                        @VerifyMode, @PunchState, @WorkCode, @Source, @IsManual, @DedupeKey, @ReceivedAtUtc, @Note);
                """,
                punch,
                transaction);
        }

        transaction.Commit();
        return inserted;
    }

    public IReadOnlyList<Punch> GetByRange(DateTime fromLocalInclusive, DateTime toLocalExclusive)
    {
        using var connection = Factory.Open();
        return connection.Query<Punch>(
            """
            SELECT * FROM Punches
             WHERE PunchTimeLocal >= @fromLocalInclusive AND PunchTimeLocal < @toLocalExclusive
             ORDER BY PunchTimeLocal;
            """,
            new { fromLocalInclusive, toLocalExclusive }).ToList();
    }

    public IReadOnlyList<Punch> GetForEmployee(string employeeId, DateTime fromLocalInclusive, DateTime toLocalExclusive)
    {
        using var connection = Factory.Open();
        return connection.Query<Punch>(
            """
            SELECT * FROM Punches
             WHERE EmployeeId = @employeeId AND PunchTimeLocal >= @fromLocalInclusive AND PunchTimeLocal < @toLocalExclusive
             ORDER BY PunchTimeLocal;
            """,
            new { employeeId, fromLocalInclusive, toLocalExclusive }).ToList();
    }

    /// <summary>Punches grouped per employee, ready for the attendance sheet builder.</summary>
    public IReadOnlyDictionary<string, List<DateTime>> GetPunchTimesByEmployee(DateTime fromLocalInclusive, DateTime toLocalExclusive)
    {
        using var connection = Factory.Open();
        var rows = connection.Query<PunchTimeRow>(
            """
            SELECT EmployeeId, PunchTimeLocal FROM Punches
             WHERE EmployeeId IS NOT NULL AND PunchTimeLocal >= @fromLocalInclusive AND PunchTimeLocal < @toLocalExclusive
             ORDER BY PunchTimeLocal;
            """,
            new { fromLocalInclusive, toLocalExclusive });

        return rows
            .GroupBy(r => r.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.PunchTimeLocal).ToList());
    }

    public int CountAll()
    {
        using var connection = Factory.Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Punches;");
    }

    public int CountInRange(DateTime fromLocalInclusive, DateTime toLocalExclusive)
    {
        using var connection = Factory.Open();
        return connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM Punches WHERE PunchTimeLocal >= @fromLocalInclusive AND PunchTimeLocal < @toLocalExclusive;",
            new { fromLocalInclusive, toLocalExclusive });
    }

    public int CountForDevice(string deviceId)
    {
        using var connection = Factory.Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Punches WHERE DeviceId = @deviceId;", new { deviceId });
    }

    public DateTime? GetLatestLocalTime(string deviceId)
    {
        using var connection = Factory.Open();
        return connection.ExecuteScalar<string?>(
            "SELECT MAX(PunchTimeLocal) FROM Punches WHERE DeviceId = @deviceId;",
            new { deviceId }) is { } value && DateTime.TryParse(value, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Distinct PINs present in raw logs but not yet linked to an employee.</summary>
    public IReadOnlyList<string> GetUnresolvedPins()
    {
        using var connection = Factory.Open();
        return connection.Query<string>(
            "SELECT DISTINCT DevicePin FROM Punches WHERE EmployeeId IS NULL ORDER BY DevicePin;").ToList();
    }

    public int LinkPunchesToEmployee(string devicePin, string employeeId)
    {
        using var connection = Factory.Open();
        return connection.Execute(
            "UPDATE Punches SET EmployeeId = @employeeId WHERE DevicePin = @devicePin AND (EmployeeId IS NULL OR EmployeeId <> @employeeId);",
            new { devicePin, employeeId });
    }

    private sealed class PunchTimeRow
    {
        public string EmployeeId { get; set; } = string.Empty;
        public DateTime PunchTimeLocal { get; set; }
    }
}
