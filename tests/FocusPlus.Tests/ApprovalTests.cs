using FocusPlus.Application;
using FocusPlus.Application.Services;
using FocusPlus.Application.ViewModels;
using FocusPlus.Domain;
using FocusPlus.Domain.Attendance;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Tests;

/// <summary>Explicit auto-approval contract for routine attendance adjustments.</summary>
public sealed class ApprovalTests
{
    private static readonly DateTime Day = new(2026, 11, 15);

    private static Employee AddEmployee(AppHost host, string matricule = "000901", string pin = "9001")
    {
        var employee = new Employee
        {
            Matricule = matricule,
            DevicePin = pin,
            FullNameAr = $"موظف {matricule}",
            FirstNameFr = "Test",
            LastNameFr = matricule,
            WorkRuleId = host.Shifts.GetDefault().Id,
            ScheduledWorkDays = WeekDays.Administrative,
            HireDate = new DateTime(2024, 1, 1)
        };
        host.EmployeeRepo.Upsert(employee);
        return employee;
    }

    private static AppUser SignIn(AppHost host, UserRole role)
    {
        var user = new AppUser
        {
            Username = role == UserRole.SuperAdmin ? "root" : "operator",
            DisplayName = role.ToString(),
            Role = role
        };
        host.Session.SignIn(user);
        return user;
    }

    private static AttendanceViewModel OpenSheet(AppHost host, IUiMessenger messenger, Employee employee)
    {
        var vm = new AttendanceViewModel(host, messenger);
        vm.RefreshAsync().GetAwaiter().GetResult();
        vm.SelectedRow = vm.Rows.First(r => r.Employee.Id == employee.Id && r.Date == DateTime.Today);
        return vm;
    }

    [Fact]
    public async Task AutoApprove_DefaultsToOnWhenAbsentOrInvalid()
    {
        await using var context = new TestHost();
        var host = context.Host;

        Assert.Null(host.Preferences.Get(SettingsService.AutoApproveKey));
        Assert.True(host.Settings.GetAutoApprove());

        host.Preferences.Set(SettingsService.AutoApproveKey, "not-a-bool");
        Assert.True(host.Settings.GetAutoApprove());
    }

    [Fact]
    public async Task AutoApprove_PersistsAndAudits()
    {
        await using var context = new TestHost();
        var host = context.Host;

        host.Settings.SetAutoApprove(false, "root");

        Assert.False(host.Settings.GetAutoApprove());
        Assert.Equal("false", host.Preferences.Get(SettingsService.AutoApproveKey));

        var audit = Assert.Single(host.Audit.Recent(50), e => e.Action.Contains(SettingsService.AutoApproveKey, StringComparison.Ordinal));
        Assert.Equal("root", audit.Actor);
        Assert.Equal("false", audit.Details);

        await using var reopened = new AppHost(context.DataDirectory);
        reopened.Initialize(createStartupBackup: false);
        Assert.False(reopened.Settings.GetAutoApprove());

        host.Settings.SetAutoApprove(true, "root");
        Assert.True(host.Settings.GetAutoApprove());
        Assert.Equal("true", host.Preferences.Get(SettingsService.AutoApproveKey));
    }

    [Fact]
    public async Task SettingsViewModel_IsAutoApprove_RoundTripsThroughPersistence()
    {
        await using var context = new TestHost();
        var host = context.Host;
        host.Session.SignIn(new AppUser { Username = "root", DisplayName = "SuperAdmin", Role = UserRole.SuperAdmin });

        var vm = new SettingsViewModel(host, new NullMessenger());
        Assert.True(vm.IsAutoApprove);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.IsAutoApprove = false;

        Assert.Contains(nameof(SettingsViewModel.IsAutoApprove), changed);
        Assert.False(vm.IsAutoApprove);
        Assert.False(host.Settings.GetAutoApprove());
        Assert.Equal("false", host.Preferences.Get(SettingsService.AutoApproveKey));

        changed.Clear();
        vm.IsAutoApprove = false;
        Assert.Empty(changed);
        Assert.Single(host.Audit.Recent(50), e => e.Action.Contains(SettingsService.AutoApproveKey, StringComparison.Ordinal));

        var reopened = new SettingsViewModel(host, new NullMessenger());
        Assert.False(reopened.IsAutoApprove);
    }

    [Fact]
    public async Task AutoApprovedAdjustments_WriteAutoApprovalAudit()
    {
        await using var context = new TestHost();
        var host = context.Host;
        var employee = AddEmployee(host);

        Assert.True(host.Attendance.AddManualPunch(employee.Id, Day, new TimeSpan(16, 30, 0), "verified", "operator").Success);
        Assert.True(host.Attendance.AddException(
            employee.Id,
            AdjustmentKind.Mission,
            Day.AddDays(1),
            Day.AddDays(1).AddHours(8),
            Day.AddDays(1).AddHours(12),
            0,
            "OM-2026-09",
            "Mission",
            "operator").Success);

        var entries = host.Audit.Recent(20);
        var punch = Assert.Single(entries, e => e.Action == "adjustment.manualpunch");
        Assert.Equal("operator", punch.Actor);
        Assert.Contains("kind=ManualPunch", punch.Details, StringComparison.Ordinal);
        Assert.Contains("approval=auto", punch.Details, StringComparison.Ordinal);

        var exception = Assert.Single(entries, e => e.Action == "adjustment.added");
        Assert.Equal("operator", exception.Actor);
        Assert.Contains("kind=Mission", exception.Details, StringComparison.Ordinal);
        Assert.Contains("approval=auto", exception.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitSuperAdminApproval_WritesSuperadminAudit()
    {
        await using var context = new TestHost();
        var host = context.Host;
        var employee = AddEmployee(host);

        Assert.True(host.Attendance.AddException(
            employee.Id,
            AdjustmentKind.ExitPermit,
            Day,
            Day.AddHours(9),
            Day.AddHours(11),
            0,
            "BS-2026-01",
            null,
            "root",
            autoApproved: false).Success);

        var entry = Assert.Single(host.Audit.Recent(20), e => e.Action == "adjustment.added");
        Assert.Contains("kind=ExitPermit", entry.Details, StringComparison.Ordinal);
        Assert.Contains("approval=superadmin", entry.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoApproveOff_BlocksOperatorAndWritesNoAdjustment()
    {
        await using var context = new TestHost();
        var host = context.Host;
        var employee = AddEmployee(host);
        host.Settings.SetAutoApprove(false, "root");
        SignIn(host, UserRole.Operator);

        var messenger = new NullMessenger();
        var vm = OpenSheet(host, messenger, employee);

        await vm.AddManualPunchAsync();

        Assert.Contains(messenger.Messages, m => m.Contains(Labels.Error(ErrorCodes.ApprovalRequired, true), StringComparison.Ordinal));
        Assert.Empty(host.AdjustmentRepo.GetForEmployee(employee.Id, DateTime.Today, DateTime.Today));
        Assert.DoesNotContain(host.Audit.Recent(20), e => e.Action is "adjustment.manualpunch" or "adjustment.added");

        var denied = Assert.Single(host.Audit.Recent(20), e => e.Action == "approval.required");
        Assert.Equal("operator", denied.Actor);
        Assert.Contains("kind=ManualPunch", denied.Details, StringComparison.Ordinal);
        Assert.Contains("approval=required", denied.Details, StringComparison.Ordinal);

        await vm.AddExceptionAsync();

        Assert.Equal(2, messenger.Messages.Count(m => m.Contains(Labels.Error(ErrorCodes.ApprovalRequired, true), StringComparison.Ordinal)));
        Assert.Empty(host.AdjustmentRepo.GetForEmployee(employee.Id, DateTime.Today, DateTime.Today));
    }

    [Fact]
    public async Task AutoApproveOff_AllowsSuperAdminWithExplicitApproval()
    {
        await using var context = new TestHost();
        var host = context.Host;
        var employee = AddEmployee(host);
        host.Settings.SetAutoApprove(false, "root");
        SignIn(host, UserRole.SuperAdmin);

        var messenger = new NullMessenger();
        var vm = OpenSheet(host, messenger, employee);
        vm.ManualTime = "16:30";

        await vm.AddManualPunchAsync();

        Assert.DoesNotContain(messenger.Messages, m => m.StartsWith("Error:", StringComparison.Ordinal));
        var adjustment = Assert.Single(host.AdjustmentRepo.GetForEmployee(employee.Id, DateTime.Today, DateTime.Today));
        Assert.Equal(AdjustmentKind.ManualPunch, adjustment.Kind);
        Assert.Equal("root", adjustment.CreatedBy);

        var audit = Assert.Single(host.Audit.Recent(20), e => e.Action == "adjustment.manualpunch");
        Assert.Equal("root", audit.Actor);
        Assert.Contains("approval=superadmin", audit.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Audit.Recent(20), e => e.Action == "approval.required");
    }

    [Fact]
    public async Task AutoApproveOn_AllowsOperatorThroughViewModel()
    {
        await using var context = new TestHost();
        var host = context.Host;
        var employee = AddEmployee(host);
        SignIn(host, UserRole.Operator);

        var messenger = new NullMessenger();
        var vm = OpenSheet(host, messenger, employee);
        vm.ManualTime = "07:45";

        await vm.AddManualPunchAsync();

        Assert.DoesNotContain(messenger.Messages, m => m.StartsWith("Error:", StringComparison.Ordinal));
        Assert.Single(host.AdjustmentRepo.GetForEmployee(employee.Id, DateTime.Today, DateTime.Today));
        var audit = Assert.Single(host.Audit.Recent(20), e => e.Action == "adjustment.manualpunch");
        Assert.Contains("approval=auto", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateManualPunch_IsStillRejected()
    {
        await using var context = new TestHost();
        var host = context.Host;
        var employee = AddEmployee(host);

        Assert.True(host.Attendance.AddManualPunch(employee.Id, Day, new TimeSpan(16, 30, 0), null, "operator").Success);
        var duplicate = host.Attendance.AddManualPunch(employee.Id, Day, new TimeSpan(16, 30, 0), null, "operator");

        Assert.False(duplicate.Success);
        Assert.Equal(ErrorCodes.DuplicateCode, duplicate.ErrorCode);
        Assert.Single(host.AdjustmentRepo.GetForEmployee(employee.Id, Day, Day));
        Assert.Single(host.Audit.Recent(20), e => e.Action == "adjustment.manualpunch");
    }
}
