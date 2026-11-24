using FocusPlus.Application.Mvvm;
using FocusPlus.Application.Services;
using FocusPlus.Domain;
using FocusPlus.Domain.Attendance;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Application.ViewModels;

/// <summary>Read-only display row for the employee directory.</summary>
public sealed class EmployeeRowVm
{
    public EmployeeRowVm(Employee model, string department, string shift, string device, bool arabic)
    {
        Model = model;
        Name = arabic ? model.DisplayNameAr : model.DisplayNameFr;
        Department = department;
        Shift = shift;
        Device = device;
        Days = Labels.WeekDays(model.ScheduledWorkDays, arabic);
        StatusText = model.IsArchived ? (arabic ? "مؤرشف" : "Archivé") : (arabic ? "نشط" : "Actif");
        Phone = model.Phone ?? "—";
        JobTitle = arabic ? model.JobTitleAr ?? "—" : model.JobTitleFr ?? "—";
        SalaryText = model.BaseSalaryDzd > 0
            ? $"{model.BaseSalaryDzd:N0} DZD"
            : model.DailyRateDzd > 0
                ? $"{model.DailyRateDzd:N0} DZD / {(arabic ? "يوم" : "jour")}"
                : "—";
        PrivilegeText = model.DevicePrivilege switch
        {
            2 => arabic ? "مسجل" : "Enrôleur",
            6 => arabic ? "مدير جهاز" : "Gestionnaire",
            14 => arabic ? "مشرف" : "SuperAdmin",
            _ => arabic ? "مستخدم" : "Utilisateur"
        };
    }

    public Employee Model { get; }
    public string Name { get; }
    public string Matricule => Model.Matricule;
    public string Pin => Model.DevicePin ?? "—";
    public string Department { get; }
    public string Shift { get; }
    public string Device { get; }
    public string Days { get; }
    public string StatusText { get; }
    public string Phone { get; }
    public string JobTitle { get; }
    public string SalaryText { get; }
    public string PrivilegeText { get; }
    public bool IsArchived => Model.IsArchived;
}

/// <summary>Mutable card for the devices console (live status updates in place).</summary>
public sealed class DeviceCardVm : ObservableObject
{
    private DeviceRuntimeStatus _status;

    public DeviceCardVm(Device model, DeviceRuntimeStatus status, bool arabic)
    {
        Model = model;
        _status = status;
        IsArabic = arabic;
    }

    public Device Model { get; }

    public bool IsArabic { get; }

    public DeviceRuntimeStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged(nameof(Status), nameof(IsOnline), nameof(ConnectivityKey), nameof(StatusText), nameof(LatencyText), nameof(LastSeenText), nameof(UsersText), nameof(PunchCountText), nameof(ClockText), nameof(FullStatusText), nameof(StatusSummary), nameof(Error), nameof(HasError), nameof(BiometricsText));
        }
    }

    public string Name => Model.Name;
    public string Endpoint => Model.Endpoint;
    public string Serial => Model.SerialNumber ?? string.Empty;
    public string ModelName => Model.ModelName ?? string.Empty;
    public string Firmware => Model.FirmwareVersion ?? string.Empty;
    public string Platform => Model.Platform ?? string.Empty;
    public string Mac => Model.MacAddress ?? string.Empty;

    public bool IsOnline => Status.IsOnline;

    public string ConnectivityKey => Status.Connectivity.ToString();

    public string StatusText => Status.Connectivity switch
    {
        DeviceConnectivity.Online => IsArabic ? "متصل" : "En ligne",
        DeviceConnectivity.Offline => IsArabic ? "غير متصل" : "Hors ligne",
        _ => IsArabic
            ? "غير معروف"
            : (string.IsNullOrWhiteSpace(Model.SerialNumber) ? "Non vérifié" : "Inconnu")
    };

    public string LatencyText => Status.LatencyMs > 0 ? $"{Status.LatencyMs} ms" : "—";

    public string LastSeenText => Model.LastSeenUtc is { } seen
        ? DateTime.SpecifyKind(seen, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM HH:mm")
        : "—";

    public string LastPullText => Model.LastPullUtc is { } pull
        ? DateTime.SpecifyKind(pull, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM HH:mm")
        : "—";

    public string ModelFirmwareText => string.IsNullOrWhiteSpace(Model.FirmwareVersion)
        ? (Model.ModelName ?? "ZKTeco")
        : $"{Model.ModelName ?? "ZKTeco"} · {Model.FirmwareVersion}";

    public string BiometricsText => Model.UserCount + Model.FingerCount + Model.FaceCount > 0
        ? $"{Model.UserCount} users / {Model.FingerCount} FP / {Model.FaceCount} face"
        : string.Empty;

    public string FullStatusText => HasError && !IsOnline
        ? $"{StatusText} · {Error}"
        : StatusText;

    /// <summary>Short readable status for the card; the full detail stays in the tooltip.</summary>
    public string StatusSummary
    {
        get
        {
            var text = FullStatusText;
            return text.Length <= 150 ? text : text[..147] + "...";
        }
    }

    public string ClockText => (Status.Snapshot?.DeviceClock ?? Model.DeviceClock)?.ToString("dd/MM/yyyy HH:mm") ?? string.Empty;

    public string UsersText => (Status.Snapshot?.UserCount ?? Model.UserCount).ToString();

    public string PunchCountText => (Status.Snapshot?.PunchCount ?? Model.PunchCount).ToString();

    public string LastSyncText => Model.LastSyncUtc is { } sync
        ? DateTime.SpecifyKind(sync, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM HH:mm")
        : "—";

    public string Error => Status.Error ?? string.Empty;
    public bool HasError => !string.IsNullOrWhiteSpace(Status.Error);
}

/// <summary>Read-only display row for the attendance sheet.</summary>
public sealed class AttendanceRowVm
{
    public AttendanceRowVm(AttendanceSheetRow model, string deviceName, bool arabic)
    {
        Model = model;
        var day = model.Day;
        DateText = day.Date.ToString("dd/MM/yyyy");
        DayText = Labels.Day(day.Date.DayOfWeek, arabic);
        Matricule = model.Matricule;
        Pin = model.DevicePin;
        Name = arabic ? model.EmployeeNameAr : model.EmployeeNameFr;
        Department = arabic ? model.DepartmentNameAr : model.DepartmentNameFr;
        Shift = arabic ? model.ShiftNameAr : model.ShiftNameFr;
        Device = deviceName;
        MorningIn = Format(day.MorningIn);
        LunchOut = Format(day.LunchOut);
        LunchIn = Format(day.LunchIn);
        EveningOut = Format(day.EveningOut);
        ScansText = day.RawScanCount.ToString();
        RegularText = Labels.Minutes(day.RegularWorkedMinutes);
        ExtraText = Labels.Minutes(day.ExtraWorkedMinutes);
        CreditedText = Labels.Minutes(day.CreditedMinutes);
        TotalText = Labels.Minutes(day.EffectiveTotalMinutes);
        LateText = day.LateMinutes > 0 ? $"+{day.LateMinutes} min" : "—";
        StatusText = Labels.Status(day.Status, arabic);
        StatusKey = day.Status.ToString();
        EarnedText = day.EarnedAmountDzd > 0 ? day.EarnedAmountDzd.ToString("N2") : "—";
        DeductionText = day.DeductionAmountDzd > 0 ? day.DeductionAmountDzd.ToString("N2") : "—";
        IsLate = day.LateMinutes > 0;
        IsAbsent = day.Status == AttendanceStatus.Absent;
        IsOffDay = !day.IsScheduledDay;
        HasMissingScan = day.Status == AttendanceStatus.MissingOutScan;
        ScheduledText = day.IsScheduledDay ? (arabic ? "مبرمج" : "Programmé") : (arabic ? "راحة/إضافي" : "Repos/Supp.");
        AccentBrushKey = day.Status switch
        {
            AttendanceStatus.Present => "SuccessBrush",
            AttendanceStatus.Late => "CautionBrush",
            AttendanceStatus.EarlyLeave => "CautionBrush",
            AttendanceStatus.LateAndEarlyLeave => "CautionBrush",
            AttendanceStatus.Absent => "DangerBrush",
            AttendanceStatus.MissingOutScan => "DangerBrush",
            AttendanceStatus.ExtraDay => "AccentBrush",
            AttendanceStatus.Leave => "InfoBrush",
            AttendanceStatus.Mission => "InfoBrush",
            _ => "NeutralBrush"
        };
    }

    public AttendanceSheetRow Model { get; }
    public Employee Employee => Model.Employee;
    public DateTime Date => Model.Day.Date;
    public string DateText { get; }
    public string DayText { get; }
    public string Matricule { get; }
    public string Pin { get; }
    public string Name { get; }
    public string Department { get; }
    public string Shift { get; }
    public string Device { get; }
    public string MorningIn { get; }
    public string LunchOut { get; }
    public string LunchIn { get; }
    public string EveningOut { get; }
    public string ScansText { get; }
    public string RegularText { get; }
    public string ExtraText { get; }
    public string CreditedText { get; }
    public string TotalText { get; }
    public string LateText { get; }
    public string StatusText { get; }
    public string StatusKey { get; }
    public string ScheduledText { get; }
    public string EarnedText { get; }
    public string DeductionText { get; }
    public string AccentBrushKey { get; }
    public bool IsLate { get; }
    public bool IsAbsent { get; }
    public bool IsOffDay { get; }
    public bool HasMissingScan { get; }

    private static string Format(DateTime? value) => value?.ToString("HH:mm") ?? "—";
}

/// <summary>Row of the device memory inspector.</summary>
public sealed class DeviceUserRowVm
{
    public DeviceUserRowVm(DeviceUser user, string? linkedEmployee, bool arabic)
    {
        User = user;
        LinkedEmployee = linkedEmployee;
        PrivilegeText = user.Privilege switch
        {
            2 => arabic ? "مسجل" : "Enrôleur",
            6 => arabic ? "مدير" : "Gestionnaire",
            14 => arabic ? "مشرف" : "SuperAdmin",
            1 => arabic ? "معطل" : "Désactivé",
            _ => arabic ? "مستخدم" : "Utilisateur"
        };
        InDatabase = !string.IsNullOrWhiteSpace(linkedEmployee);
        LinkText = InDatabase ? linkedEmployee! : (arabic ? "غير مستورد" : "Non importé");
    }

    public DeviceUser User { get; }
    public string Pin => User.UserId;
    public string Name => string.IsNullOrWhiteSpace(User.Name) ? "—" : User.Name;
    public string PrivilegeText { get; }
    public string CardText => User.Card > 0 ? User.Card.ToString() : "—";
    public string LinkText { get; }
    public bool InDatabase { get; }
    public string? LinkedEmployee { get; }
}

/// <summary>Simple option for combo boxes (ID + localized display name).</summary>
public sealed class NamedOption
{
    public NamedOption(string id, string display)
    {
        Id = id;
        Display = display;
    }

    public string Id { get; }
    public string Display { get; }
    public override string ToString() => Display;
}

/// <summary>Numeric option (device privilege mask).</summary>
public sealed class PrivilegeOption
{
    public PrivilegeOption(int value, string display)
    {
        Value = value;
        Display = display;
    }

    public int Value { get; }
    public string Display { get; }
    public override string ToString() => Display;
}

/// <summary>Activity line for the dashboard live feed.</summary>
public sealed class DashboardActivityVm
{
    public DashboardActivityVm(DashboardActivityRow row, bool arabic)
    {
        Name = arabic ? row.Employee.DisplayNameAr : row.Employee.DisplayNameFr;
        Matricule = row.Employee.Matricule;
        Department = arabic ? row.DepartmentAr : row.DepartmentFr;
        Device = row.DeviceName;
        MorningIn = row.Day.MorningIn?.ToString("HH:mm") ?? "—";
        LunchOut = row.Day.LunchOut?.ToString("HH:mm") ?? "—";
        LunchIn = row.Day.LunchIn?.ToString("HH:mm") ?? "—";
        EveningOut = row.Day.EveningOut?.ToString("HH:mm") ?? "—";
        WorkedText = Labels.Minutes(row.Day.EffectiveTotalMinutes);
        StatusText = Labels.Status(row.Day.Status, arabic);
        StatusKey = row.Day.Status.ToString();
        IsLate = row.Day.LateMinutes > 0;
        NoteText = row.Day.CreditedMinutes > 0
            ? (arabic ? $"ساعات معتمدة {Labels.Minutes(row.Day.CreditedMinutes)}" : $"Crédit {Labels.Minutes(row.Day.CreditedMinutes)}")
            : string.Empty;
        LastEvent = row.Day.EveningOut ?? row.Day.LunchIn ?? row.Day.LunchOut ?? row.Day.MorningIn ?? row.Day.Date;
    }

    public string Name { get; }
    public string Matricule { get; }
    public string Department { get; }
    public string Device { get; }
    public string MorningIn { get; }
    public string LunchOut { get; }
    public string LunchIn { get; }
    public string EveningOut { get; }
    public string WorkedText { get; }
    public string StatusText { get; }
    public string StatusKey { get; }
    public bool IsLate { get; }
    public string NoteText { get; }
    public DateTime LastEvent { get; }
}
