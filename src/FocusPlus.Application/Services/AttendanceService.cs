using System.Globalization;
using System.Text;
using FocusPlus.Data.Repositories;
using FocusPlus.Domain;
using FocusPlus.Domain.Attendance;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Application.Services;

public sealed record AttendanceQuery
{
    public DateTime From { get; init; } = DateTime.Today;
    public DateTime To { get; init; } = DateTime.Today;
    public string? EmployeeId { get; init; }
    public string? DepartmentId { get; init; }
    public string? DeviceId { get; init; }
    public string? WorkRuleId { get; init; }
    public AttendanceStatus? Status { get; init; }
    public string? Search { get; init; }
    public bool IncludeArchived { get; init; }
}

public sealed record AttendanceSheet(
    IReadOnlyList<AttendanceSheetRow> Rows,
    IReadOnlyList<AttendanceTotals> Totals,
    DateTime From,
    DateTime To);

public sealed record AttendanceDayDetail(
    Employee Employee,
    WorkRule Rule,
    Department? Department,
    AttendanceDayResult Result,
    IReadOnlyList<Punch> Punches,
    IReadOnlyList<AttendanceAdjustment> Adjustments);

/// <summary>Builds the 7-day attendance sheet and manages the audited HR adjustments.</summary>
public sealed class AttendanceService(
    EmployeeRepository employees,
    DepartmentRepository departments,
    WorkRuleRepository workRules,
    PunchRepository punches,
    AttendanceAdjustmentRepository adjustments,
    DeviceRepository devices,
    AuditRepository audit)
{
    public AttendanceSheet GetSheet(AttendanceQuery query)
    {
        var from = query.From.Date;
        var to = query.To.Date;
        if (to < from)
        {
            (from, to) = (to, from);
        }

        var directory = LoadEmployees(query);
        var rules = workRules.GetAll(includeArchived: true).ToDictionary(r => r.Id, StringComparer.Ordinal);
        var fallback = workRules.GetDefault();
        if (!rules.ContainsKey(fallback.Id))
        {
            rules[fallback.Id] = fallback;
        }

        var departmentsMap = departments.GetAll(includeArchived: true)
            .ToDictionary(d => d.Id, StringComparer.Ordinal);

        var punchTimes = punches.GetPunchTimesByEmployee(from, to.AddDays(1));
        var adjustmentMap = adjustments.GetByEmployee(from, to);

        var rows = AttendanceSheetBuilder.Build(directory, rules, fallback, from, to, punchTimes, adjustmentMap, departmentsMap);
        if (query.Status is { } status)
        {
            rows = rows.Where(r => r.Day.Status == status).ToList();
        }

        return new AttendanceSheet(rows, AttendanceSheetBuilder.Summarize(rows), from, to);
    }

    public IReadOnlyList<Employee> LoadEmployees(AttendanceQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.EmployeeId))
        {
            var employee = employees.Get(query.EmployeeId);
            return employee is null ? [] : [employee];
        }

        return employees.Query(new EmployeeFilter
        {
            DepartmentId = query.DepartmentId,
            DeviceId = query.DeviceId,
            WorkRuleId = query.WorkRuleId,
            Search = query.Search,
            IncludeArchived = query.IncludeArchived
        });
    }

    public AttendanceDayDetail? GetDayDetail(string employeeId, DateTime date)
    {
        var employee = employees.Get(employeeId);
        if (employee is null)
        {
            return null;
        }

        var rules = workRules.GetAll(includeArchived: true).ToDictionary(r => r.Id, StringComparer.Ordinal);
        var fallback = workRules.GetDefault();
        var rule = AttendanceSheetBuilder.ResolveRule(employee, rules, fallback);
        var department = employee.DepartmentId is { } departmentId ? departments.Get(departmentId) : null;

        var dayPunches = punches.GetForEmployee(employeeId, date.Date, date.Date.AddDays(1));
        var dayAdjustments = adjustments.GetForEmployee(employeeId, date.Date, date.Date);

        var result = AttendanceCalculator.Calculate(new AttendanceDayInput
        {
            Date = date.Date,
            Rule = rule,
            IsScheduledDay = employee.IsScheduledOn(date),
            Punches = dayPunches.Select(p => p.PunchTimeLocal).ToList(),
            Adjustments = dayAdjustments,
            HourlyRateDzd = AttendanceCalculator.ResolveHourlyRate(employee, rule),
            OvertimeMultiplier = employee.OvertimeMultiplier
        });

        return new AttendanceDayDetail(employee, rule, department, result, dayPunches, dayAdjustments);
    }

    public OperationResult AddManualPunch(string employeeId, DateTime date, TimeSpan time, string? note, string actor)
    {
        if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
        {
            return OperationResult.Fail(ErrorCodes.InvalidTime);
        }

        var employee = employees.Get(employeeId);
        if (employee is null)
        {
            return OperationResult.Fail(ErrorCodes.NotFound);
        }

        var moment = date.Date + time;
        var existing = adjustments.GetForEmployee(employeeId, date.Date, date.Date)
            .Any(a => a.Kind == AdjustmentKind.ManualPunch && a.StartLocal.HasValue && a.StartLocal.Value == moment);
        if (existing)
        {
            return OperationResult.Fail(ErrorCodes.DuplicateCode, moment.ToString("HH:mm", CultureInfo.InvariantCulture));
        }

        adjustments.Upsert(new AttendanceAdjustment
        {
            EmployeeId = employeeId,
            WorkDate = date.Date,
            Kind = AdjustmentKind.ManualPunch,
            StartLocal = moment,
            Note = note,
            CreatedBy = actor
        });

        audit.Add(Log("adjustment.manualpunch", actor, employeeId, $"{date:yyyy-MM-dd} {time:hh\\:mm} {note}"));
        return OperationResult.Ok();
    }

    public OperationResult AddException(
        string employeeId,
        AdjustmentKind kind,
        DateTime date,
        DateTime? start,
        DateTime? end,
        int creditedMinutes,
        string? reference,
        string? note,
        string actor)
    {
        if (kind == AdjustmentKind.ManualPunch)
        {
            return OperationResult.Fail(ErrorCodes.InvalidTime);
        }

        var employee = employees.Get(employeeId);
        if (employee is null)
        {
            return OperationResult.Fail(ErrorCodes.NotFound);
        }

        var isLeave = kind is AdjustmentKind.AnnualLeave or AdjustmentKind.SickLeave or AdjustmentKind.OtherLeave;
        var minutes = creditedMinutes;

        if (start.HasValue && end.HasValue)
        {
            if (end <= start)
            {
                return OperationResult.Fail(ErrorCodes.InvalidRange);
            }

            if (minutes <= 0 && !isLeave)
            {
                minutes = (int)Math.Floor((end.Value - start.Value).TotalMinutes);
            }
        }

        if (minutes <= 0)
        {
            minutes = workRules.GetDefault().StandardDailyTargetMinutes;
        }

        adjustments.Upsert(new AttendanceAdjustment
        {
            EmployeeId = employeeId,
            WorkDate = date.Date,
            Kind = kind,
            StartLocal = start,
            EndLocal = end,
            HoursCreditedMinutes = Math.Clamp(minutes, 0, 24 * 60),
            Reference = reference,
            Note = note,
            CreatedBy = actor
        });

        audit.Add(Log("adjustment.added", actor, employeeId, $"{kind} {date:yyyy-MM-dd} {minutes}min"));
        return OperationResult.Ok();
    }

    public OperationResult ArchiveAdjustment(string adjustmentId, string reason, string actor)
    {
        adjustments.Archive(adjustmentId, reason, actor);
        audit.Add(Log("adjustment.archived", actor, adjustmentId, reason));
        return OperationResult.Ok();
    }

    public string ExportCsv(AttendanceSheet sheet, string path, bool arabic)
    {
        var deviceNames = devices.GetAll(includeArchived: true).ToDictionary(d => d.Id, d => d.Name, StringComparer.Ordinal);
        var builder = new StringBuilder();
        var headers = arabic
            ? new[] { "التاريخ", "اليوم", "الرقم الوظيفي", "المعرف البيومتري", "اللقب والاسم", "المصلحة", "الوردية", "الجهاز", "الدخول الصباحي", "خروج الغداء", "عودة الغداء", "خروج المساء", "عدد البصمات", "الساعات النظامية", "الساعات الإضافية", "إجمالي الساعات", "دقائق التأخر", "الحالة", "المبلغ المكتسب", "المبلغ المخصوم" }
            : new[] { "Date", "Jour", "Matricule", "PIN", "Nom", "Service", "Poste", "Terminal", "Entrée", "Sortie déj.", "Retour déj.", "Sortie", "Scans", "Heures rég.", "Heures sup.", "Total", "Retard (min)", "Statut", "Gain DZD", "Retenue DZD" };

        builder.AppendLine(string.Join(';', headers.Select(Escape)));
        foreach (var row in sheet.Rows)
        {
            var day = row.Day;
            var values = new[]
            {
                day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Labels.Day(day.Date.DayOfWeek, arabic),
                row.Matricule,
                row.DevicePin,
                arabic ? row.EmployeeNameAr : row.EmployeeNameFr,
                arabic ? row.DepartmentNameAr : row.DepartmentNameFr,
                arabic ? row.ShiftNameAr : row.ShiftNameFr,
                row.Employee.PrimaryDeviceId is { } id && deviceNames.TryGetValue(id, out var name) ? name : string.Empty,
                Format(day.MorningIn),
                Format(day.LunchOut),
                Format(day.LunchIn),
                Format(day.EveningOut),
                day.RawScanCount.ToString(CultureInfo.InvariantCulture),
                Labels.Minutes(day.RegularWorkedMinutes),
                Labels.Minutes(day.ExtraWorkedMinutes),
                Labels.Minutes(day.EffectiveTotalMinutes),
                day.LateMinutes.ToString(CultureInfo.InvariantCulture),
                Labels.Status(day.Status, arabic),
                day.EarnedAmountDzd.ToString("0.00", CultureInfo.InvariantCulture),
                day.DeductionAmountDzd.ToString("0.00", CultureInfo.InvariantCulture)
            };
            builder.AppendLine(string.Join(';', values.Select(Escape)));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    public string ExportHtmlReport(AttendanceSheet sheet, string path, bool arabic)
    {
        var deviceNames = devices.GetAll(includeArchived: true).ToDictionary(d => d.Id, d => d.Name, StringComparer.Ordinal);
        var dir = arabic ? "rtl" : "ltr";
        var builder = new StringBuilder();

        builder.Append("<!DOCTYPE html><html lang=\"").Append(arabic ? "ar" : "fr").Append("\" dir=\"").Append(dir).Append("\"><head><meta charset=\"utf-8\">");
        builder.Append("<title>OPGI M'Sila — ").Append(arabic ? "كشف الحضور" : "Feuille de présence").Append("</title>");
        builder.Append("""
            <style>
            :root { --ink:#12211c; --muted:#5b6b64; --line:#cbd6d1; --accent:#0f6b5c; --sand:#f6f3ec; }
            * { box-sizing:border-box; }
            body { font-family:"Segoe UI","Dubai",Tahoma,sans-serif; color:var(--ink); margin:24px; background:#fff; }
            header { display:flex; align-items:center; justify-content:space-between; gap:24px;
                     border-bottom:3px solid var(--accent); padding-bottom:14px; }
            .brand { display:flex; align-items:center; gap:14px; }
            .mark { width:54px; height:54px; border-radius:14px; background:var(--accent); color:#fff;
                    display:flex; align-items:center; justify-content:center; font-weight:700; font-size:20px; }
            h1 { font-size:19px; margin:0; }
            h2 { font-size:13px; margin:2px 0 0; font-weight:600; color:var(--muted); }
            .meta { font-size:11.5px; color:var(--muted); text-align:end; line-height:1.7; }
            .filters { margin:14px 0 10px; font-size:11.5px; color:var(--muted); }
            table { width:100%; border-collapse:collapse; font-size:10.5px; }
            thead th { background:var(--sand); border:1px solid var(--line); padding:6px 5px; text-align:center; font-weight:600; }
            thead th small { display:block; font-weight:400; color:var(--muted); font-size:9.5px; }
            tbody td { border:1px solid var(--line); padding:5px; }
            tbody tr:nth-child(even) { background:#fbfaf6; }
            .num { text-align:center; font-variant-numeric:tabular-nums; }
            .name { font-weight:600; }
            tfoot td { border:1px solid var(--line); padding:6px; font-weight:700; background:var(--sand); }
            .signatures { display:flex; justify-content:space-between; margin-top:34px; gap:40px; font-size:11.5px; }
            .signature { flex:1; border-top:1px dashed var(--muted); padding-top:8px; text-align:center; color:var(--muted); }
            @page { size:A4 landscape; margin:10mm; }
            @media print { body { margin:0; } .no-print { display:none; } }
            </style>
            """);

        builder.Append("</head><body>");
        builder.Append("<header><div class=\"brand\"><div class=\"mark\">OPGI</div><div>");
        builder.Append("<h1>ديوان الترقية والتسيير العقاري — ولاية المسيلة</h1>");
        builder.Append("<h2>Office de Promotion et de Gestion Immobilière — Direction de M'Sila</h2></div></div>");
        builder.Append("<div class=\"meta\"><div><strong>").Append(arabic ? "كشف الحضور وساعات العمل" : "Feuille de présence et heures de travail").Append("</strong></div>");
        builder.Append("<div>").Append(arabic ? "من" : "Du").Append(' ').Append(sheet.From.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture))
            .Append(' ').Append(arabic ? "إلى" : "au").Append(' ').Append(sheet.To.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)).Append("</div>");
        builder.Append("<div>").Append(arabic ? "طُبع في" : "Édité le").Append(' ').Append(DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)).Append("</div></div></header>");

        builder.Append("<div class=\"filters\">");
        builder.Append(arabic ? "عدد الأسطر: " : "Lignes : ").Append(sheet.Rows.Count.ToString(CultureInfo.InvariantCulture));
        builder.Append(" — ").Append(arabic ? "عدد الموظفين: " : "Employés : ").Append(sheet.Totals.Count.ToString(CultureInfo.InvariantCulture));
        builder.Append("</div>");

        builder.Append("<table><thead><tr>");
        (string Ar, string Fr)[] columns =
        [
            ("التاريخ", "Date"), ("اليوم", "Jour"), ("الرقم الوظيفي", "Matricule"), ("المعرف", "PIN"),
            ("اللقب والاسم", "Nom et prénom"), ("المصلحة", "Service"), ("الوردية", "Poste"), ("الجهاز", "Terminal"),
            ("دخول", "Entrée"), ("خروج", "Sortie déj."), ("عودة", "Retour"), ("مساء", "Sortie"),
            ("بصمات", "Scans"), ("نظامي", "Rég."), ("إضافي", "Sup."), ("الإجمالي", "Total"),
            ("تأخر", "Retard"), ("الحالة", "Statut"), ("المكتسب", "Gain"), ("الخصم", "Retenue")
        ];
        foreach (var column in columns)
        {
            builder.Append("<th>").Append(column.Ar).Append("<small>").Append(column.Fr).Append("</small></th>");
        }

        builder.Append("</tr></thead><tbody>");

        foreach (var row in sheet.Rows)
        {
            var day = row.Day;
            var terminal = row.Employee.PrimaryDeviceId is { } id && deviceNames.TryGetValue(id, out var name) ? name : string.Empty;
            builder.Append("<tr>");
            Cell(day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "num");
            Cell(Labels.Day(day.Date.DayOfWeek, arabic));
            Cell(row.Matricule, "num");
            Cell(row.DevicePin, "num");
            Cell(arabic ? row.EmployeeNameAr : row.EmployeeNameFr, "name");
            Cell(arabic ? row.DepartmentNameAr : row.DepartmentNameFr);
            Cell(arabic ? row.ShiftNameAr : row.ShiftNameFr);
            Cell(terminal);
            Cell(Format(day.MorningIn), "num");
            Cell(Format(day.LunchOut), "num");
            Cell(Format(day.LunchIn), "num");
            Cell(Format(day.EveningOut), "num");
            Cell(day.RawScanCount.ToString(CultureInfo.InvariantCulture), "num");
            Cell(Labels.Minutes(day.RegularWorkedMinutes), "num");
            Cell(Labels.Minutes(day.ExtraWorkedMinutes), "num");
            Cell(Labels.Minutes(day.EffectiveTotalMinutes), "num");
            Cell(day.LateMinutes > 0 ? day.LateMinutes.ToString(CultureInfo.InvariantCulture) : "—", "num");
            Cell(Labels.Status(day.Status, arabic));
            Cell(day.EarnedAmountDzd.ToString("N2", CultureInfo.InvariantCulture), "num");
            Cell(day.DeductionAmountDzd.ToString("N2", CultureInfo.InvariantCulture), "num");
            builder.Append("</tr>");
        }

        builder.Append("</tbody><tfoot><tr>");
        builder.Append("<td colspan=\"13\">").Append(arabic ? "الإجمالي" : "Totaux").Append("</td>");
        builder.Append("<td class=\"num\">").Append(Labels.Minutes(sheet.Totals.Sum(t => t.RegularMinutes))).Append("</td>");
        builder.Append("<td class=\"num\">").Append(Labels.Minutes(sheet.Totals.Sum(t => t.ExtraMinutes))).Append("</td>");
        builder.Append("<td class=\"num\">").Append(Labels.Minutes(sheet.Totals.Sum(t => t.RegularMinutes + t.ExtraMinutes + t.CreditedMinutes))).Append("</td>");
        builder.Append("<td class=\"num\">").Append(sheet.Totals.Sum(t => t.LateMinutes)).Append("</td>");
        builder.Append("<td></td>");
        builder.Append("<td class=\"num\">").Append(sheet.Totals.Sum(t => t.EarnedDzd).ToString("N2", CultureInfo.InvariantCulture)).Append("</td>");
        builder.Append("<td></td></tr></tfoot></table>");

        builder.Append("<div class=\"signatures\"><div class=\"signature\">")
            .Append(arabic ? "المديرية العامة — الموارد البشرية" : "Direction Générale — Ressources Humaines")
            .Append("</div><div class=\"signature\">")
            .Append(arabic ? "المدير العام" : "Le Directeur Général")
            .Append("</div></div>");

        builder.Append("<script>window.onload=function(){if(location.hash==='#print'){window.print();}};</script>");
        builder.Append("</body></html>");

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        return path;

        void Cell(string value, string? css = null)
        {
            builder.Append("<td");
            if (css is not null)
            {
                builder.Append(" class=\"").Append(css).Append('"');
            }

            builder.Append('>').Append(System.Net.WebUtility.HtmlEncode(value)).Append("</td>");
        }
    }

    private static string Format(DateTime? value) =>
        value?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "—";

    private static string Escape(string value)
    {
        if (value.Contains(';', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal))
        {
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return value;
    }

    private static AuditLogEntry Log(string action, string actor, string entityId, string? details) => new()
    {
        Action = action,
        Actor = actor,
        EntityType = "Attendance",
        EntityId = entityId,
        Details = details
    };
}
