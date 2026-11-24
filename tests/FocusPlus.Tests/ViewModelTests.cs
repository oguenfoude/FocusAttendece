using FocusPlus.Application.ViewModels;
using FocusPlus.Domain;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Tests;

/// <summary>View-model level checks (no WPF): loading, filtering and counts on real data.</summary>
public sealed class ViewModelTests
{
    private static Employee AddEmployee(Application.AppHost host, string matricule, string pin, string departmentCode)
    {
        var department = host.Organization.GetDepartments().FirstOrDefault(d => d.Code == departmentCode);
        var employee = new Employee
        {
            Matricule = matricule,
            DevicePin = pin,
            FullNameAr = $"موظف {matricule}",
            FirstNameFr = "Test",
            LastNameFr = matricule,
            DepartmentId = department?.Id,
            WorkRuleId = host.Shifts.GetDefault().Id,
            ScheduledWorkDays = WeekDays.Administrative,
            HireDate = new DateTime(2024, 1, 1)
        };
        host.EmployeeRepo.Upsert(employee);
        return employee;
    }

    [Fact]
    public async Task EmployeesViewModel_LoadsDirectoryAndOptions()
    {
        await using var context = new TestHost();
        var host = context.Host;
        AddEmployee(host, "000801", "8001", "DRH");
        AddEmployee(host, "000802", "8002", "DFC");
        AddEmployee(host, "000803", "8003", "DGI");

        var vm = new EmployeesViewModel(host, new NullMessenger());
        await vm.RefreshAsync();

        Assert.Equal(3, vm.Employees.Count);
        Assert.True(vm.Departments.Count >= 10);
        Assert.True(vm.Devices.Count >= 1);
        Assert.True(vm.Shifts.Count >= 2);
        Assert.True(vm.DepartmentOptions.Count >= 10);
    }

    [Fact]
    public async Task EmployeesViewModel_SearchFiltersByNameAndMatricule()
    {
        await using var context = new TestHost();
        var host = context.Host;
        AddEmployee(host, "000811", "8011", "DRH");
        AddEmployee(host, "000812", "8012", "DFC");

        var vm = new EmployeesViewModel(host, new NullMessenger());
        await vm.RefreshAsync();

        vm.Search = "000811";
        Assert.Single(vm.Employees);

        vm.Search = "does-not-exist";
        Assert.Empty(vm.Employees);

        vm.ClearFiltersCommand.Execute(null);
        Assert.Equal(2, vm.Employees.Count);
    }

    [Fact]
    public async Task AttendanceViewModel_LoadsSheetsAndTotals()
    {
        await using var context = new TestHost();
        var host = context.Host;
        var employee = AddEmployee(host, "000821", "8021", "DRH");

        var today = DateTime.Today;
        host.PunchRepo.InsertBatch(
        [
            MakePunch(employee, today.AddHours(8)),
            MakePunch(employee, today.AddHours(16).AddMinutes(30))
        ]);

        var vm = new AttendanceViewModel(host, new NullMessenger());
        await vm.RefreshAsync();
        vm.WeekCommand.Execute(null);

        Assert.Equal(7, vm.Rows.Count);
        Assert.Contains(vm.Rows, r => r.Date == today && r.Model.Day.TotalNetMinutes == 450);
        Assert.Contains(vm.Rows, r => r.TotalText == "07:30");
        Assert.True(vm.Employees.Count >= 2);
    }

    [Fact]
    public async Task SettingsViewModel_LoadsRulesDepartmentsAndBackups()
    {
        await using var context = new TestHost();
        var vm = new SettingsViewModel(context.Host, new NullMessenger());
        await vm.RefreshAsync();

        Assert.Equal(2, vm.Rules.Count);
        Assert.True(vm.Departments.Count >= 10);
        Assert.True(vm.RuleOptions.Count >= 2);
        Assert.Single(vm.Operators);
        Assert.True(vm.ScanTimeout > 0);
        Assert.Contains("v1", vm.DbSchema, StringComparison.Ordinal);
        Assert.True(vm.DbJournal.Length > 0);
    }

    [Fact]
    public async Task DevicesViewModel_LoadsRegisteredDevices()
    {
        await using var context = new TestHost();
        var host = context.Host;
        host.DeviceRepo.Upsert(new Device { Name = "A", IpAddress = "192.168.1.10", SerialNumber = "S1" });
        host.DeviceRepo.Upsert(new Device { Name = "B", IpAddress = "192.168.1.11", SerialNumber = "S2" });

        var vm = new DevicesViewModel(host, new NullMessenger());
        await vm.RefreshAsync();

        Assert.Equal(2, vm.Devices.Count);
        Assert.NotNull(vm.SelectedDevice);
        Assert.Contains("4370", vm.SdkStatus, StringComparison.Ordinal);
    }

    private static Punch MakePunch(Employee employee, DateTime local) => new()
    {
        EmployeeId = employee.Id,
        DevicePin = employee.DevicePin!,
        DeviceSerial = "TEST",
        PunchTimeLocal = local,
        PunchTimeUtc = DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime(),
        DedupeKey = $"{employee.DevicePin}|{local:O}",
        Source = PunchSource.Device
    };
}
