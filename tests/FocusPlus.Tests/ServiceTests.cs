using FocusPlus.Application;
using FocusPlus.Application.Services;
using FocusPlus.Data.Repositories;
using FocusPlus.Domain;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Tests;

public sealed class ServiceTests
{
    private static readonly DateTime Sunday = new(2026, 11, 15);
    private static readonly DateTime Friday = new(2026, 11, 20);

    [Fact]
    public async Task EmployeeSave_AppliesDefaultsAndValidatesUniqueness()
    {
        await using var context = new TestHost();
        var host = context.Host;

        var employee = new Employee
        {
            Matricule = "000001",
            DevicePin = "1001",
            FullNameAr = "بن علي محمد",
            FirstNameFr = "Mohamed",
            LastNameFr = "Benali"
        };

        var saved = host.EmployeeDirectory.Save(employee, "tester");
        Assert.True(saved.Success);

        var stored = host.EmployeeRepo.GetByMatricule("000001");
        Assert.NotNull(stored);
        Assert.Equal(host.Shifts.GetDefault().Id, stored!.WorkRuleId);
        Assert.Equal(WeekDays.Administrative, stored.ScheduledWorkDays);

        var duplicateMatricule = host.EmployeeDirectory.Save(new Employee
        {
            Matricule = "000001",
            FullNameAr = "آخر"
        }, "tester");
        Assert.False(duplicateMatricule.Success);
        Assert.Equal(ErrorCodes.DuplicateMatricule, duplicateMatricule.ErrorCode);

        var duplicatePin = host.EmployeeDirectory.Save(new Employee
        {
            Matricule = "000002",
            DevicePin = "1001",
            FullNameAr = "آخر"
        }, "tester");
        Assert.False(duplicatePin.Success);
        Assert.Equal(ErrorCodes.DuplicatePin, duplicatePin.ErrorCode);

        var noName = host.EmployeeDirectory.Save(new Employee { Matricule = "000003" }, "tester");
        Assert.False(noName.Success);
        Assert.Equal(ErrorCodes.Required, noName.ErrorCode);
    }

    [Fact]
    public async Task EmployeeSave_LinksExistingPunchesByPin()
    {
        await using var context = new TestHost();
        var host = context.Host;
        host.PunchRepo.InsertBatch([MakePunch("1001", Sunday.AddHours(8))]);

        var employee = new Employee { Matricule = "000010", DevicePin = "1001", FullNameAr = "موظف" };
        Assert.True(host.EmployeeDirectory.Save(employee, "tester").Success);

        var punches = host.PunchRepo.GetForEmployee(employee.Id, Sunday, Sunday.AddDays(1));
        Assert.Single(punches);
    }

    [Fact]
    public async Task NextMatricule_IsSequential()
    {
        await using var context = new TestHost();
        var host = context.Host;
        host.EmployeeRepo.Upsert(new Employee { Matricule = "000041", FullNameAr = "أ" });
        host.EmployeeRepo.Upsert(new Employee { Matricule = "000007", FullNameAr = "ب" });

        Assert.Equal("000042", host.EmployeeDirectory.GetNextMatricule());
    }

    [Fact]
    public async Task EmployeeImport_FromDevice_CreatesThenUpdates()
    {
        await using var context = new TestHost();
        var host = context.Host;
        var device = new Device { Name = "UF100 Plus-S", IpAddress = "192.168.1.50", SerialNumber = "SER-1" };
        host.DeviceRepo.Upsert(device);

        host.PunchRepo.InsertBatch([MakePunch("2001", Sunday.AddHours(8), "SER-1")]);

        var users = new List<DeviceUser>
        {
            new() { Uid = 1, UserId = "2001", Name = "AMRANI", Privilege = 0, Card = 111 },
            new() { Uid = 2, UserId = "2002", Name = "ZIANI", Privilege = 14, Card = 222 }
        };

        var first = host.EmployeeDirectory.ImportUsers(device, users, "tester");
        Assert.Equal(2, first.Created);
        Assert.Equal(0, first.Updated);

        var imported = host.EmployeeRepo.GetByPin("2001");
        Assert.NotNull(imported);
        Assert.Equal("2001", imported!.Matricule);
        Assert.Equal(device.Id, imported.PrimaryDeviceId);
        Assert.Single(host.PunchRepo.GetForEmployee(imported.Id, Sunday, Sunday.AddDays(1)));

        var privileged = host.EmployeeRepo.GetByPin("2002");
        Assert.Equal(14, privileged!.DevicePrivilege);

        users[0] = users[0] with { Name = "AMRANI ALI", Privilege = 2 };
        var second = host.EmployeeDirectory.ImportUsers(device, users, "tester");
        Assert.Equal(0, second.Created);
        Assert.Equal(2, second.Updated);
        Assert.Equal(2, host.EmployeeRepo.Count());
    }

    [Fact]
    public async Task DepartmentCrud_BlocksDuplicateCodesAndInUseArchiving()
    {
        await using var context = new TestHost();
        var host = context.Host;
        var site = host.Organization.GetSites()[0];

        var created = host.Organization.SaveDepartment(new Department
        {
            Code = "SERV-TEST",
            NameAr = "مصلحة الاختبار",
            NameFr = "Service Test",
            SiteId = site.Id
        }, "tester");
        Assert.True(created.Success);

        var duplicate = host.Organization.SaveDepartment(new Department
        {
            Code = "SERV-TEST",
            NameAr = "أخرى",
            NameFr = "Autre"
        }, "tester");
        Assert.False(duplicate.Success);
        Assert.Equal(ErrorCodes.DuplicateCode, duplicate.ErrorCode);

        var department = host.Organization.GetDepartments().First(d => d.Code == "SERV-TEST");
        department.NameAr = "مصلحة الاختبار المعدلة";
        Assert.True(host.Organization.SaveDepartment(department, "tester").Success);
        Assert.Equal("مصلحة الاختبار المعدلة", host.Organization.GetDepartment(department.Id)!.NameAr);

        host.EmployeeRepo.Upsert(new Employee
        {
            Matricule = "000100",
            FullNameAr = "موظف",
            DepartmentId = department.Id
        });

        var blocked = host.Organization.ArchiveDepartment(department.Id, "test", "tester");
        Assert.False(blocked.Success);
        Assert.Equal(ErrorCodes.InUse, blocked.ErrorCode);

        host.EmployeeRepo.Archive(host.EmployeeRepo.GetAll()[0].Id, "test", "tester");
        Assert.True(host.Organization.ArchiveDepartment(department.Id, "unused", "tester").Success);
        Assert.True(host.Organization.GetDepartment(department.Id)!.IsArchived);
        Assert.True(host.Organization.RestoreDepartment(department.Id, "tester").Success);
        Assert.False(host.Organization.GetDepartment(department.Id)!.IsArchived);
    }

    [Fact]
    public async Task WorkRuleCrud_ValidatesTimesAndKeepsADefault()
    {
        await using var context = new TestHost();
        var host = context.Host;

        var invalid = new WorkRule
        {
            Code = "BAD",
            NameAr = "خطأ",
            NameFr = "Erreur",
            MorningStartMin = 13 * 60,
            LunchStartMin = 12 * 60,
            LunchEndMin = 12 * 60 + 30,
            EveningEndMin = 16 * 60
        };
        var invalidResult = host.Shifts.Save(invalid, "tester");
        Assert.False(invalidResult.Success);
        Assert.Equal(ErrorCodes.InvalidTime, invalidResult.ErrorCode);

        var summer = new WorkRule
        {
            Code = "SUMMER",
            NameAr = "توقيت الصيف",
            NameFr = "Horaire d'été",
            Mode = WorkMode.FixedShift,
            MorningStartMin = 8 * 60 + 30,
            LunchStartMin = 12 * 60,
            LunchEndMin = 12 * 60 + 30,
            EveningEndMin = 15 * 60 + 30,
            MorningGraceMinutes = 10,
            AfternoonGraceMinutes = 10,
            EarlyLeaveGraceMinutes = 5,
            LunchDeductionMinutes = 30,
            StandardDailyTargetMinutes = 390
        };
        Assert.True(host.Shifts.Save(summer, "tester").Success);
        Assert.Equal(3, host.Shifts.GetRules().Count);

        var official = host.Shifts.GetDefault();
        Assert.True(host.Shifts.Archive(official.Id, "replaced", "tester").Success);

        var reassigned = host.Shifts.GetDefault();
        Assert.NotEqual(official.Id, reassigned.Id);
        Assert.False(reassigned.IsArchived);

        foreach (var rule in host.Shifts.GetRules().Where(r => r.Id != reassigned.Id).ToList())
        {
            Assert.True(host.Shifts.Archive(rule.Id, "cleanup", "tester").Success);
        }

        var last = host.Shifts.GetRules().Single();
        var blocked = host.Shifts.Archive(last.Id, "nope", "tester");
        Assert.False(blocked.Success);
        Assert.Equal(ErrorCodes.LastDefaultRule, blocked.ErrorCode);
    }

    [Fact]
    public async Task OfficialTimesChange_RecalculatesAttendanceImmediately()
    {
        await using var context = new TestHost();
        var host = context.Host;
        var employee = CreateEmployee(host, "000200", "3001");
        host.PunchRepo.InsertBatch(
        [
            MakePunch("3001", Sunday.AddHours(8), employeeId: employee.Id),
            MakePunch("3001", Sunday.AddHours(16).AddMinutes(30), employeeId: employee.Id)
        ]);

        var before = host.Attendance.GetSheet(new AttendanceQuery { From = Sunday, To = Sunday });
        Assert.Equal(450, before.Rows[0].Day.TotalNetMinutes);
        Assert.Equal(0, before.Rows[0].Day.LateMinutes);

        var rule = host.Shifts.GetDefault();
        rule.LunchDeductionMinutes = 30;
        Assert.True(host.Shifts.Save(rule, "tester").Success);

        var after = host.Attendance.GetSheet(new AttendanceQuery { From = Sunday, To = Sunday });
        Assert.Equal(480, after.Rows[0].Day.TotalNetMinutes);

        // Grace period change must immediately re-flag a 08:10 arrival as late.
        var monday = Sunday.AddDays(1);
        host.PunchRepo.InsertBatch(
        [
            MakePunch("3001", monday.AddHours(8).AddMinutes(10), employeeId: employee.Id),
            MakePunch("3001", monday.AddHours(16).AddMinutes(30), employeeId: employee.Id)
        ]);

        var withGrace = host.Attendance.GetSheet(new AttendanceQuery { From = monday, To = monday });
        Assert.Equal(0, withGrace.Rows[0].Day.LateMinutes);

        var tightened = host.Shifts.GetDefault();
        tightened.MorningGraceMinutes = 0;
        Assert.True(host.Shifts.Save(tightened, "tester").Success);

        var withoutGrace = host.Attendance.GetSheet(new AttendanceQuery { From = monday, To = monday });
        Assert.Equal(10, withoutGrace.Rows[0].Day.LateMinutes);
        Assert.Equal(employee.Id, withoutGrace.Rows[0].Employee.Id);
    }

    [Fact]
    public async Task Sheet_CoversAllSevenDays_IncludingOffDays()
    {
        await using var context = new TestHost();
        var host = context.Host;
        var employee = CreateEmployee(host, "000300", "4001");

        host.PunchRepo.InsertBatch(
        [
            MakePunch("4001", Sunday.AddHours(8), employeeId: employee.Id),
            MakePunch("4001", Sunday.AddHours(16).AddMinutes(30), employeeId: employee.Id),
            MakePunch("4001", Friday.AddHours(9), employeeId: employee.Id),
            MakePunch("4001", Friday.AddHours(12), employeeId: employee.Id)
        ]);

        var sheet = host.Attendance.GetSheet(new AttendanceQuery { From = Sunday, To = Sunday.AddDays(6) });

        Assert.Equal(7, sheet.Rows.Count);
        Assert.Equal(AttendanceStatus.Present, sheet.Rows[0].Day.Status);
        Assert.Equal(450, sheet.Rows[0].Day.RegularWorkedMinutes);

        var friday = sheet.Rows[5];
        Assert.Equal(DayOfWeek.Friday, friday.Day.Date.DayOfWeek);
        Assert.False(friday.Day.IsScheduledDay);
        Assert.Equal(AttendanceStatus.ExtraDay, friday.Day.Status);
        Assert.Equal(180, friday.Day.ExtraWorkedMinutes);

        Assert.Equal(AttendanceStatus.RestDay, sheet.Rows[6].Day.Status);
    }

    [Fact]
    public async Task ManualPunchAndMission_AreAuditedAndCredited()
    {
        await using var context = new TestHost();
        var host = context.Host;
        var employee = CreateEmployee(host, "000400", "5001");
        host.PunchRepo.InsertBatch([MakePunch("5001", Sunday.AddHours(8), employeeId: employee.Id)]);

        var manual = host.Attendance.AddManualPunch(employee.Id, Sunday, new TimeSpan(16, 30, 0), "verified", "tester");
        Assert.True(manual.Success);

        var sheet = host.Attendance.GetSheet(new AttendanceQuery { From = Sunday, To = Sunday, EmployeeId = employee.Id });
        Assert.Equal(450, sheet.Rows[0].Day.TotalNetMinutes);
        Assert.Contains(AttendanceNote.ManualPunchUsed, sheet.Rows[0].Day.Notes);

        var duplicate = host.Attendance.AddManualPunch(employee.Id, Sunday, new TimeSpan(16, 30, 0), null, "tester");
        Assert.False(duplicate.Success);

        var monday = Sunday.AddDays(1);
        var mission = host.Attendance.AddException(
            employee.Id,
            AdjustmentKind.Mission,
            monday,
            monday.AddHours(8),
            monday.AddHours(12),
            0,
            "OM-2026-01",
            "Mission to Algiers",
            "tester");
        Assert.True(mission.Success);

        var mondayDetail = host.Attendance.GetDayDetail(employee.Id, monday);
        Assert.NotNull(mondayDetail);
        Assert.Equal(AttendanceStatus.Mission, mondayDetail!.Result.Status);
        Assert.Equal(240, mondayDetail.Result.CreditedMinutes);
        Assert.Contains(mondayDetail.Adjustments, a => a.Reference == "OM-2026-01");

        var archived = host.Attendance.ArchiveAdjustment(mondayDetail.Adjustments[0].Id, "cancelled", "tester");
        Assert.True(archived.Success);
        Assert.Equal(AttendanceStatus.Absent, host.Attendance.GetDayDetail(employee.Id, monday)!.Result.Status);
    }

    [Fact]
    public async Task Sheet_CanBeFilteredByStatusAndDepartment()
    {
        await using var context = new TestHost();
        var host = context.Host;
        var department = host.Organization.GetDepartments().First(d => d.Code == "DRH");
        var employee = CreateEmployee(host, "000500", "6001");
        employee.DepartmentId = department.Id;
        host.EmployeeRepo.Upsert(employee);

        var absent = host.Attendance.GetSheet(new AttendanceQuery
        {
            From = Sunday,
            To = Sunday,
            DepartmentId = department.Id,
            Status = AttendanceStatus.Absent
        });
        Assert.Single(absent.Rows);

        var present = host.Attendance.GetSheet(new AttendanceQuery
        {
            From = Sunday,
            To = Sunday,
            DepartmentId = department.Id,
            Status = AttendanceStatus.Present
        });
        Assert.Empty(present.Rows);
    }

    [Fact]
    public async Task Exports_ProduceCsvAndPrintableBilingualHtml()
    {
        await using var context = new TestHost();
        var host = context.Host;
        CreateEmployee(host, "000600", "7001");
        var exportEmployee = host.EmployeeRepo.GetByPin("7001")!;
        host.PunchRepo.InsertBatch(
        [
            MakePunch("7001", Sunday.AddHours(8), employeeId: exportEmployee.Id),
            MakePunch("7001", Sunday.AddHours(16).AddMinutes(30), employeeId: exportEmployee.Id)
        ]);

        var sheet = host.Attendance.GetSheet(new AttendanceQuery { From = Sunday, To = Sunday.AddDays(6) });
        var csvPath = Path.Combine(context.DataDirectory, "sheet.csv");
        var htmlPath = Path.Combine(context.DataDirectory, "sheet.html");

        host.Attendance.ExportCsv(sheet, csvPath, arabic: false);
        host.Attendance.ExportHtmlReport(sheet, htmlPath, arabic: true);

        Assert.True(File.Exists(csvPath));
        Assert.True(File.Exists(htmlPath));

        var csv = await File.ReadAllTextAsync(csvPath);
        Assert.Contains("Matricule", csv);
        Assert.Contains("08:00", csv);
        Assert.Contains("16:30", csv);

        var html = await File.ReadAllTextAsync(htmlPath);
        Assert.Contains("OPGI", html);
        Assert.Contains("<table", html);
        Assert.Contains("dir=\"rtl\"", html);
    }

    [Fact]
    public async Task Dashboard_ReflectsRealScansOnly()
    {
        await using var context = new TestHost();
        var host = context.Host;
        var today = DateTime.Today;

        var employee = new Employee
        {
            Matricule = "000700",
            DevicePin = "8001",
            FullNameAr = "لوحة",
            FirstNameFr = "Dash",
            LastNameFr = "Board",
            WorkRuleId = host.Shifts.GetDefault().Id,
            ScheduledWorkDays = WeekDays.All,
            HireDate = today.AddMonths(-2)
        };
        host.EmployeeRepo.Upsert(employee);

        var empty = host.Dashboard.Build(today);
        Assert.Equal(1, empty.ActiveEmployees);
        Assert.Equal(1, empty.AbsentToday);
        Assert.Equal(0, empty.PresentToday);

        host.PunchRepo.InsertBatch(
        [
            MakePunch("8001", today.AddHours(8), employeeId: employee.Id),
            MakePunch("8001", today.AddHours(16).AddMinutes(30), employeeId: employee.Id)
        ]);

        var filled = host.Dashboard.Build(today);
        Assert.Equal(1, filled.PresentToday);
        Assert.Equal(2, filled.TodayScans);
        Assert.Equal(450, filled.RegularMinutesToday);
        Assert.Single(filled.Activity);
    }

    [Fact]
    public async Task AuthAudit_RecordsFailedAttempts()
    {
        await using var context = new TestHost();
        context.Host.Auth.Login("admin", "bad");
        var entries = context.Host.Audit.Recent(20);
        Assert.Contains(entries, e => e.Action == "login.failed");
    }

    private static Employee CreateEmployee(Application.AppHost host, string matricule, string pin)
    {
        var employee = new Employee
        {
            Matricule = matricule,
            DevicePin = pin,
            FullNameAr = "موظف اختبار",
            FirstNameFr = "Test",
            LastNameFr = "User",
            WorkRuleId = host.Shifts.GetDefault().Id,
            ScheduledWorkDays = WeekDays.Administrative,
            HireDate = new DateTime(2026, 1, 1),
            BaseSalaryDzd = 45000m
        };
        host.EmployeeRepo.Upsert(employee);
        return employee;
    }

    private static Punch MakePunch(string pin, DateTime local, string serial = "TEST-SERIAL", string? employeeId = null) => new()
    {
        EmployeeId = employeeId,
        DevicePin = pin,
        DeviceSerial = serial,
        PunchTimeLocal = local,
        PunchTimeUtc = DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime(),
        DedupeKey = DeviceService.BuildDedupeKey(serial, pin, DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime()),
        Source = PunchSource.Device
    };
}
