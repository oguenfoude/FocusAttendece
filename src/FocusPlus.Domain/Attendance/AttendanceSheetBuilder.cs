using FocusPlus.Domain.Entities;

namespace FocusPlus.Domain.Attendance;

/// <summary>One line of the 7-day / monthly sheet.</summary>
public sealed record AttendanceSheetRow
{
    public required Employee Employee { get; init; }
    public required WorkRule Rule { get; init; }
    public required AttendanceDayResult Day { get; init; }
    public Department? Department { get; init; }

    public string Matricule => Employee.Matricule;
    public string DevicePin => Employee.DevicePin ?? string.Empty;
    public string EmployeeNameAr => Employee.DisplayNameAr;
    public string EmployeeNameFr => Employee.DisplayNameFr;
    public string DepartmentNameAr => Department?.NameAr ?? string.Empty;
    public string DepartmentNameFr => Department?.NameFr ?? string.Empty;
    public string ShiftNameAr => Rule.NameAr;
    public string ShiftNameFr => Rule.NameFr;
}

public sealed record AttendanceTotals
{
    public required string EmployeeId { get; init; }
    public int DaysWorked { get; init; }
    public int DaysAbsent { get; init; }
    public int DaysLate { get; init; }
    public int LateMinutes { get; init; }
    public int RegularMinutes { get; init; }
    public int ExtraMinutes { get; init; }
    public int CreditedMinutes { get; init; }
    public decimal EarnedDzd { get; init; }
}

/// <summary>Builds the full sheet for every employee over a date range, all seven days included.</summary>
public static class AttendanceSheetBuilder
{
    public static IReadOnlyList<AttendanceSheetRow> Build(
        IEnumerable<Employee> employees,
        IReadOnlyDictionary<string, WorkRule> rules,
        WorkRule fallbackRule,
        DateTime from,
        DateTime to,
        IReadOnlyDictionary<string, List<DateTime>> punchesByEmployee,
        IReadOnlyDictionary<string, List<AttendanceAdjustment>>? adjustmentsByEmployee = null,
        IReadOnlyDictionary<string, Department>? departments = null)
    {
        ArgumentNullException.ThrowIfNull(employees);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(punchesByEmployee);
        ArgumentNullException.ThrowIfNull(fallbackRule);

        var start = from.Date;
        var end = to.Date;
        if (end < start)
        {
            (start, end) = (end, start);
        }

        var rows = new List<AttendanceSheetRow>();

        foreach (var employee in employees)
        {
            var rule = ResolveRule(employee, rules, fallbackRule);
            punchesByEmployee.TryGetValue(employee.Id, out var employeePunches);
            List<AttendanceAdjustment>? employeeAdjustments = null;
            adjustmentsByEmployee?.TryGetValue(employee.Id, out employeeAdjustments);
            Department? department = null;
            departments?.TryGetValue(employee.DepartmentId ?? string.Empty, out department);

            var hourly = AttendanceCalculator.ResolveHourlyRate(employee, rule);

            for (var date = start; date <= end; date = date.AddDays(1))
            {
                if (!IsWithinEmployment(employee, date))
                {
                    continue;
                }

                var dayPunches = employeePunches?.Where(p => p.Date == date).ToList() ?? [];
                var dayAdjustments = employeeAdjustments?.Where(a => a.WorkDate.Date == date).ToList() ?? [];

                var result = AttendanceCalculator.Calculate(new AttendanceDayInput
                {
                    Date = date,
                    Rule = rule,
                    IsScheduledDay = employee.IsScheduledOn(date),
                    Punches = dayPunches,
                    Adjustments = dayAdjustments,
                    HourlyRateDzd = hourly,
                    OvertimeMultiplier = employee.OvertimeMultiplier
                });

                rows.Add(new AttendanceSheetRow
                {
                    Employee = employee,
                    Rule = rule,
                    Day = result,
                    Department = department
                });
            }
        }

        return rows
            .OrderBy(r => r.Employee.DisplayNameFr, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Day.Date)
            .ToList();
    }

    public static WorkRule ResolveRule(Employee employee, IReadOnlyDictionary<string, WorkRule> rules, WorkRule fallbackRule)
    {
        if (!string.IsNullOrWhiteSpace(employee.WorkRuleId)
            && rules.TryGetValue(employee.WorkRuleId, out var rule))
        {
            return rule;
        }

        return fallbackRule;
    }

    public static IReadOnlyList<AttendanceTotals> Summarize(IEnumerable<AttendanceSheetRow> rows)
    {
        return rows
            .GroupBy(r => r.Employee.Id)
            .Select(g => new AttendanceTotals
            {
                EmployeeId = g.Key,
                DaysWorked = g.Count(r => r.Day.TotalNetMinutes > 0),
                DaysAbsent = g.Count(r => r.Day.Status == AttendanceStatus.Absent),
                DaysLate = g.Count(r => r.Day.LateMinutes > 0),
                LateMinutes = g.Sum(r => r.Day.LateMinutes),
                RegularMinutes = g.Sum(r => r.Day.RegularWorkedMinutes),
                ExtraMinutes = g.Sum(r => r.Day.ExtraWorkedMinutes),
                CreditedMinutes = g.Sum(r => r.Day.CreditedMinutes),
                EarnedDzd = g.Sum(r => r.Day.EarnedAmountDzd)
            })
            .ToList();
    }

    public static bool IsWithinEmployment(Employee employee, DateTime date)
    {
        if (employee.HireDate.HasValue && date.Date < employee.HireDate.Value.Date)
        {
            return false;
        }

        return !employee.EndDate.HasValue || date.Date <= employee.EndDate.Value.Date;
    }
}
