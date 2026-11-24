using System.Globalization;
using FocusPlus.Data.Repositories;
using FocusPlus.Domain.Entities;
using FocusPlus.Hardware;

namespace FocusPlus.Application.Services;

public sealed record EmployeeImportResult(int Total, int Created, int Updated, int Skipped, string Message);

/// <summary>Employee directory, device import/push and all validations.</summary>
public sealed class EmployeeService(
    EmployeeRepository employees,
    DepartmentRepository departments,
    WorkRuleRepository workRules,
    DeviceRepository devices,
    PunchRepository punches,
    AuditRepository audit,
    HardwareOperationLogRepository hardwareLogs,
    ZkHardwareService hardware)
{
    public IReadOnlyList<Employee> Search(EmployeeFilter filter) => employees.Query(filter);

    public IReadOnlyList<Employee> GetAll(bool includeArchived = false) => employees.GetAll(includeArchived);

    public Employee? Get(string id) => employees.Get(id);

    public int Count(bool includeArchived = false) => employees.Count(includeArchived);

    public OperationResult Save(Employee employee, string actor)
    {
        if (string.IsNullOrWhiteSpace(employee.Matricule))
        {
            return OperationResult.Fail(ErrorCodes.Required);
        }

        if (string.IsNullOrWhiteSpace(employee.FullNameAr)
            && string.IsNullOrWhiteSpace(employee.FirstNameFr)
            && string.IsNullOrWhiteSpace(employee.LastNameFr))
        {
            return OperationResult.Fail(ErrorCodes.Required);
        }

        employee.Matricule = employee.Matricule.Trim();
        employee.DevicePin = string.IsNullOrWhiteSpace(employee.DevicePin) ? null : employee.DevicePin.Trim();

        var all = employees.GetAll(includeArchived: true);
        if (all.Any(e => e.Id != employee.Id && string.Equals(e.Matricule, employee.Matricule, StringComparison.OrdinalIgnoreCase)))
        {
            return OperationResult.Fail(ErrorCodes.DuplicateMatricule, employee.Matricule);
        }

        if (employee.DevicePin is { } pin
            && all.Any(e => e.Id != employee.Id && !e.IsArchived && string.Equals(e.DevicePin, pin, StringComparison.Ordinal)))
        {
            return OperationResult.Fail(ErrorCodes.DuplicatePin, pin);
        }

        if (employee.BaseSalaryDzd < 0 || employee.DailyRateDzd < 0 || employee.HourlyRateDzd < 0 || employee.OvertimeMultiplier < 0)
        {
            return OperationResult.Fail(ErrorCodes.Required);
        }

        if (employee.HireDate.HasValue && employee.EndDate.HasValue && employee.EndDate < employee.HireDate)
        {
            return OperationResult.Fail(ErrorCodes.InvalidRange);
        }

        if (string.IsNullOrWhiteSpace(employee.WorkRuleId))
        {
            employee.WorkRuleId = workRules.GetDefault().Id;
        }
        else if (workRules.Get(employee.WorkRuleId) is null)
        {
            return OperationResult.Fail(ErrorCodes.NotFound, employee.WorkRuleId);
        }

        if (!string.IsNullOrWhiteSpace(employee.DepartmentId) && departments.Get(employee.DepartmentId) is null)
        {
            return OperationResult.Fail(ErrorCodes.NotFound, employee.DepartmentId);
        }

        if (!string.IsNullOrWhiteSpace(employee.PrimaryDeviceId) && devices.Get(employee.PrimaryDeviceId) is null)
        {
            employee.PrimaryDeviceId = null;
        }

        var isNew = employees.Get(employee.Id) is null;
        employees.Upsert(employee);

        if (employee.DevicePin is { } devicePin)
        {
            punches.LinkPunchesToEmployee(devicePin, employee.Id);
        }

        audit.Add(Log(isNew ? "employee.created" : "employee.updated", actor, employee.Id, employee.Matricule));
        return OperationResult.Ok();
    }

    public OperationResult Archive(string id, string reason, string actor)
    {
        var employee = employees.Get(id);
        if (employee is null)
        {
            return OperationResult.Fail(ErrorCodes.NotFound);
        }

        employees.Archive(id, reason, actor);
        audit.Add(Log("employee.archived", actor, id, reason));
        return OperationResult.Ok();
    }

    public OperationResult Restore(string id, string actor)
    {
        employees.Restore(id, actor);
        audit.Add(Log("employee.restored", actor, id, null));
        return OperationResult.Ok();
    }

    /// <summary>Next free matricule, 6 digits, based on the real directory.</summary>
    public string GetNextMatricule()
    {
        var max = 0;
        foreach (var employee in employees.GetAll(includeArchived: true))
        {
            if (int.TryParse(employee.Matricule, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > max)
            {
                max = value;
            }
        }

        return (max + 1).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>Reads the real users stored in a terminal and imports them into the directory.</summary>
    public async Task<(OperationResult Result, EmployeeImportResult? Import)> ImportFromDeviceAsync(
        string deviceId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var device = devices.Get(deviceId);
        if (device is null)
        {
            return (OperationResult.Fail(ErrorCodes.NotFound), null);
        }

        var read = await hardware.ReadUsersAsync(Endpoint(device), cancellationToken).ConfigureAwait(false);
        hardwareLogs.Add(new HardwareOperationLog
        {
            DeviceId = device.Id,
            Operation = "ReadUsers",
            Success = read.Success,
            Message = read.Message
        });

        if (!read.Success || read.Value is null)
        {
            return (OperationResult.Fail(ErrorCodes.Hardware, read.Message), null);
        }

        var result = ImportUsers(device, read.Value, actor);
        return (OperationResult.Ok(), result);
    }

    /// <summary>Imports a user selection coming from the device memory inspector.</summary>
    public EmployeeImportResult ImportUsers(Device device, IReadOnlyCollection<DeviceUser> users, string actor)
    {
        var defaultRule = workRules.GetDefault();
        var created = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var user in users)
        {
            var existing = employees.GetByPin(user.UserId);
            if (existing is not null)
            {
                existing.DevicePrivilege = user.Privilege;
                existing.CardNumber = (int)Math.Clamp(user.Card, 0, int.MaxValue);
                existing.PrimaryDeviceId ??= device.Id;
                if (string.IsNullOrWhiteSpace(existing.FirstNameFr) && string.IsNullOrWhiteSpace(existing.FullNameAr))
                {
                    existing.FirstNameFr = user.Name;
                }

                employees.Upsert(existing);
                punches.LinkPunchesToEmployee(user.UserId, existing.Id);
                updated++;
                continue;
            }

            var employee = new Employee
            {
                Matricule = ResolveMatricule(user.UserId),
                DevicePin = user.UserId,
                FirstNameFr = string.IsNullOrWhiteSpace(user.Name) ? null : user.Name,
                FullNameAr = string.IsNullOrWhiteSpace(user.Name) ? null : user.Name,
                PrimaryDeviceId = device.Id,
                WorkRuleId = defaultRule.Id,
                DevicePrivilege = user.Privilege,
                CardNumber = (int)Math.Clamp(user.Card, 0, int.MaxValue),
                StatusNote = $"Imported from {device.Name} ({device.Endpoint})"
            };

            if (employee.DevicePrivilege is not (0 or 2 or 6 or 14))
            {
                employee.DevicePrivilege = 0;
            }

            employees.Upsert(employee);
            punches.LinkPunchesToEmployee(user.UserId, employee.Id);
            created++;
        }

        audit.Add(new AuditLogEntry
        {
            Action = "employee.imported",
            Actor = actor,
            EntityType = "Device",
            EntityId = device.Id,
            Details = $"created={created}; updated={updated}; skipped={skipped}"
        });

        return new EmployeeImportResult(users.Count, created, updated, skipped,
            $"Imported {created} new and updated {updated} employee(s) from {device.Name}.");
    }

    /// <summary>Writes the employee identity, card, password and privilege inside the terminal.</summary>
    public async Task<OperationResult> PushToDeviceAsync(string employeeId, string? deviceId, string actor, CancellationToken cancellationToken = default)
    {
        var employee = employees.Get(employeeId);
        if (employee is null)
        {
            return OperationResult.Fail(ErrorCodes.NotFound);
        }

        if (string.IsNullOrWhiteSpace(employee.DevicePin))
        {
            return OperationResult.Fail(ErrorCodes.Required, nameof(employee.DevicePin));
        }

        var targetId = deviceId ?? employee.PrimaryDeviceId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            var allDevices = devices.GetAll();
            targetId = allDevices.Count > 0 ? allDevices[0].Id : null;
        }

        if (string.IsNullOrWhiteSpace(targetId) || devices.Get(targetId) is not { } device)
        {
            return OperationResult.Fail(ErrorCodes.NotFound, "device");
        }

        var payload = new DeviceUser
        {
            Uid = int.TryParse(employee.DevicePin, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid)
                ? uid
                : 0,
            UserId = employee.DevicePin,
            Name = employee.DisplayNameFr.Length > 0 ? employee.DisplayNameFr : employee.Matricule,
            Privilege = employee.DevicePrivilege is 0 or 2 or 6 or 14 ? employee.DevicePrivilege : 0,
            Password = employee.DevicePassword ?? string.Empty,
            Card = employee.CardNumber
        };

        var result = await hardware.PushUserAsync(Endpoint(device), payload, cancellationToken).ConfigureAwait(false);
        hardwareLogs.Add(new HardwareOperationLog
        {
            DeviceId = device.Id,
            Operation = "PushUser",
            Success = result.Success,
            Message = result.Message
        });

        if (!result.Success)
        {
            return OperationResult.Fail(ErrorCodes.Hardware, result.Message);
        }

        if (employee.PrimaryDeviceId is null)
        {
            employee.PrimaryDeviceId = device.Id;
            employees.Upsert(employee);
        }

        audit.Add(Log("employee.pushed", actor, employee.Id, $"{device.Endpoint} pin={employee.DevicePin}"));
        return OperationResult.Ok();
    }

    public async Task<OperationResult> DeleteFromDeviceAsync(Employee employee, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(employee.DevicePin))
        {
            return OperationResult.Fail(ErrorCodes.Required);
        }

        Device? device = null;
        if (employee.PrimaryDeviceId is { } id)
        {
            device = devices.Get(id);
        }
        else
        {
            var allDevices = devices.GetAll();
            device = allDevices.Count > 0 ? allDevices[0] : null;
        }

        if (device is null)
        {
            return OperationResult.Fail(ErrorCodes.NotFound, "device");
        }

        var result = await hardware.DeleteUserAsync(Endpoint(device), employee.DevicePin, cancellationToken).ConfigureAwait(false);
        hardwareLogs.Add(new HardwareOperationLog
        {
            DeviceId = device.Id,
            Operation = "DeleteUser",
            Success = result.Success,
            Message = result.Message
        });

        return result.Success ? OperationResult.Ok() : OperationResult.Fail(ErrorCodes.Hardware, result.Message);
    }

    private string ResolveMatricule(string pin)
    {
        var candidate = string.IsNullOrWhiteSpace(pin) ? GetNextMatricule() : pin.Trim();
        if (employees.GetByMatricule(candidate) is null)
        {
            return candidate;
        }

        var index = 1;
        while (employees.GetByMatricule($"{candidate}-{index}") is not null)
        {
            index++;
        }

        return $"{candidate}-{index}";
    }

    internal static ZkEndpoint Endpoint(Device device) => new(device.IpAddress, device.Port, device.CommKey);

    private static AuditLogEntry Log(string action, string actor, string entityId, string? details) => new()
    {
        Action = action,
        Actor = actor,
        EntityType = "Employee",
        EntityId = entityId,
        Details = details
    };
}
