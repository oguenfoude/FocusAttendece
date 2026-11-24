using FocusPlus.Data.Repositories;
using FocusPlus.Domain;
using FocusPlus.Domain.Attendance;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Application.Services;

public sealed record DashboardActivityRow
{
    public required Employee Employee { get; init; }
    public required AttendanceDayResult Day { get; init; }
    public string DepartmentAr { get; init; } = string.Empty;
    public string DepartmentFr { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
}

public sealed record DashboardSnapshot
{
    public int ActiveEmployees { get; init; }
    public int DevicesTotal { get; init; }
    public int DevicesOnline { get; init; }
    public int PresentToday { get; init; }
    public int LateToday { get; init; }
    public int OnLunchNow { get; init; }
    public int AbsentToday { get; init; }
    public int TodayScans { get; init; }
    public int RegularMinutesToday { get; init; }
    public int ExtraMinutesToday { get; init; }
    public IReadOnlyList<DashboardActivityRow> Activity { get; init; } = [];
    public IReadOnlyList<Device> Devices { get; init; } = [];
}

/// <summary>Executive dashboard KPIs, rebuilt from real scans only.</summary>
public sealed class DashboardService(
    AttendanceService attendance,
    DeviceService devices,
    PunchRepository punches,
    DepartmentRepository departments,
    WorkRuleRepository workRules,
    EmployeeRepository employees)
{
    public DashboardSnapshot Build(DateTime? date = null)
    {
        var today = (date ?? DateTime.Today).Date;
        var sheet = attendance.GetSheet(new AttendanceQuery { From = today, To = today });
        var departmentMap = departments.GetAll(includeArchived: true).ToDictionary(d => d.Id, StringComparer.Ordinal);
        var deviceMap = devices.GetDevices(includeArchived: true).ToDictionary(d => d.Id, d => d.Name, StringComparer.Ordinal);

        var present = 0;
        var late = 0;
        var absent = 0;
        var onLunch = 0;
        var regular = 0;
        var extra = 0;
        var activity = new List<DashboardActivityRow>();
        var now = DateTime.Now;

        foreach (var row in sheet.Rows)
        {
            var day = row.Day;
            if (day.RawScanCount > 0)
            {
                present++;
            }

            if (day.LateMinutes > 0)
            {
                late++;
            }

            if (day.Status == AttendanceStatus.Absent)
            {
                absent++;
            }

            regular += day.RegularWorkedMinutes;
            extra += day.ExtraWorkedMinutes;

            if (IsOnLunch(row, now))
            {
                onLunch++;
            }

            if (day.RawScanCount == 0 && day.CreditedMinutes == 0)
            {
                continue;
            }

            departmentMap.TryGetValue(row.Employee.DepartmentId ?? string.Empty, out var department);
            activity.Add(new DashboardActivityRow
            {
                Employee = row.Employee,
                Day = day,
                DepartmentAr = department?.NameAr ?? string.Empty,
                DepartmentFr = department?.NameFr ?? string.Empty,
                DeviceName = row.Employee.PrimaryDeviceId is { } id && deviceMap.TryGetValue(id, out var name) ? name : string.Empty
            });
        }

        var deviceList = devices.GetDevices();
        var online = deviceList.Count(d => devices.GetStatus(d.Id).IsOnline);

        return new DashboardSnapshot
        {
            ActiveEmployees = employees.Count(),
            DevicesTotal = deviceList.Count,
            DevicesOnline = online,
            PresentToday = present,
            LateToday = late,
            OnLunchNow = onLunch,
            AbsentToday = absent,
            TodayScans = punches.CountInRange(today, today.AddDays(1)),
            RegularMinutesToday = regular,
            ExtraMinutesToday = extra,
            Activity = activity
                .OrderByDescending(a => a.Day.EveningOut ?? a.Day.LunchIn ?? a.Day.LunchOut ?? a.Day.MorningIn ?? a.Day.Date)
                .ToList(),
            Devices = deviceList
        };
    }

    public WorkRule GetDefaultRule() => workRules.GetDefault();

    private static bool IsOnLunch(AttendanceSheetRow row, DateTime now)
    {
        var day = row.Day;
        if (day.LunchOut is null || day.LunchIn is not null || day.MorningIn is null)
        {
            return false;
        }

        var tolerance = day.Date.Date + row.Rule.LunchEnd + TimeSpan.FromMinutes(row.Rule.AfternoonGraceMinutes + 30);
        return now < tolerance;
    }
}
