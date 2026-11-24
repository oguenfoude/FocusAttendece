using System.Collections.ObjectModel;
using System.Globalization;
using FocusPlus.Application.Mvvm;
using FocusPlus.Application.Services;
using FocusPlus.Domain;
using FocusPlus.Domain.Attendance;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Application.ViewModels;

public sealed class PunchLineVm
{
    public PunchLineVm(Punch punch, string deviceName, bool arabic)
    {
        Time = punch.PunchTimeLocal.ToString("HH:mm:ss");
        Date = punch.PunchTimeLocal.ToString("dd/MM/yyyy");
        Device = deviceName;
        Source = punch.IsManual || punch.Source == PunchSource.Manual
            ? (arabic ? "يدوي معتمد" : "Manuel vérifié")
            : (arabic ? "جهاز البصمة" : "Terminal");
        Verify = punch.VerifyMode switch
        {
            1 => arabic ? "بصمة" : "Empreinte",
            2 => arabic ? "كلمة سر" : "Mot de passe",
            15 => arabic ? "وجه" : "Visage",
            _ => arabic ? "بطاقة/أخرى" : "Carte/Autre"
        };
        State = punch.PunchState switch
        {
            0 => arabic ? "دخول" : "Entrée",
            1 => arabic ? "خروج" : "Sortie",
            2 => arabic ? "استراحة" : "Pause",
            3 => arabic ? "عودة" : "Reprise",
            _ => punch.PunchState.ToString(CultureInfo.InvariantCulture)
        };
        Note = punch.Note ?? string.Empty;
    }

    public string Time { get; }
    public string Date { get; }
    public string Device { get; }
    public string Source { get; }
    public string Verify { get; }
    public string State { get; }
    public string Note { get; }
}

public sealed class AdjustmentRowVm
{
    public AdjustmentRowVm(AttendanceAdjustment model, bool arabic)
    {
        Model = model;
        Kind = Labels.Kind(model.Kind, arabic);
        Date = model.WorkDate.ToString("dd/MM/yyyy");
        Window = model.StartLocal is { } start
            ? $"{start:HH:mm}" + (model.EndLocal is { } end ? $" → {end:HH:mm}" : string.Empty)
            : "—";
        Minutes = Labels.Minutes(model.HoursCreditedMinutes);
        Reference = model.Reference ?? "—";
        Note = model.Note ?? "—";
        CreatedBy = model.CreatedBy ?? "—";
    }

    public AttendanceAdjustment Model { get; }
    public string Kind { get; }
    public string Date { get; }
    public string Window { get; }
    public string Minutes { get; }
    public string Reference { get; }
    public string Note { get; }
    public string CreatedBy { get; }
}

public sealed class AdjustmentKindOption
{
    public AdjustmentKindOption(AdjustmentKind kind, string display)
    {
        Kind = kind;
        Display = display;
    }

    public AdjustmentKind Kind { get; }
    public string Display { get; }
    public override string ToString() => Display;
}

/// <summary>7-day / monthly attendance sheet with the raw-scan drawer and official exceptions.</summary>
public sealed class AttendanceViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IUiMessenger _ui;

    private DateTime _fromDate = DateTime.Today;
    private DateTime _toDate = DateTime.Today;
    private NamedOption? _selectedEmployee;
    private NamedOption? _selectedDepartment;
    private NamedOption? _selectedDevice;
    private NamedOption? _selectedShift;
    private NamedOption? _selectedStatus;
    private AttendanceRowVm? _selectedRow;
    private AttendanceSheet? _sheet;
    private bool _isBusy;
    private bool _isDetailOpen;
    private string _detailTitle = string.Empty;
    private string _detailSubtitle = string.Empty;
    private string _detailStatus = string.Empty;
    private string _detailTimes = string.Empty;
    private string _detailTotals = string.Empty;
    private string _detailNotes = string.Empty;
    private string _manualTime = "08:00";
    private string _manualNote = string.Empty;
    private AdjustmentKindOption? _selectedExceptionKind;
    private string _exceptionStart = "08:00";
    private string _exceptionEnd = "12:00";
    private int _exceptionMinutes;
    private string _exceptionReference = string.Empty;
    private string _exceptionNote = string.Empty;
    private string _summaryText = string.Empty;

    public AttendanceViewModel(AppHost host, IUiMessenger ui)
    {
        _host = host;
        _ui = ui;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        TodayCommand = new RelayCommand(() => { FromDate = DateTime.Today; ToDate = DateTime.Today; });
        WeekCommand = new RelayCommand(SelectWeek);
        MonthCommand = new RelayCommand(SelectMonth);
        OpenDetailCommand = new RelayCommand<AttendanceRowVm>(row => _ = OpenDetailAsync(row));
        CloseDetailCommand = new RelayCommand(() => IsDetailOpen = false);
        AddManualPunchCommand = new AsyncRelayCommand(AddManualPunchAsync, () => !IsBusy && SelectedRow is not null);
        AddExceptionCommand = new AsyncRelayCommand(AddExceptionAsync, () => !IsBusy && SelectedRow is not null);
        ArchiveAdjustmentCommand = new AsyncRelayCommand<AdjustmentRowVm>(ArchiveAdjustmentAsync, _ => !IsBusy);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
    }

    public ObservableCollection<AttendanceRowVm> Rows { get; } = [];

    public ObservableCollection<NamedOption> Employees { get; } = [];

    public ObservableCollection<NamedOption> Departments { get; } = [];

    public ObservableCollection<NamedOption> Devices { get; } = [];

    public ObservableCollection<NamedOption> Shifts { get; } = [];

    public ObservableCollection<NamedOption> Statuses { get; } = [];

    public ObservableCollection<PunchLineVm> DetailPunches { get; } = [];

    public ObservableCollection<AdjustmentRowVm> DetailAdjustments { get; } = [];

    public ObservableCollection<AdjustmentKindOption> ExceptionKinds { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand TodayCommand { get; }
    public RelayCommand WeekCommand { get; }
    public RelayCommand MonthCommand { get; }
    public RelayCommand<AttendanceRowVm> OpenDetailCommand { get; }
    public RelayCommand CloseDetailCommand { get; }
    public AsyncRelayCommand AddManualPunchCommand { get; }
    public AsyncRelayCommand AddExceptionCommand { get; }
    public AsyncRelayCommand<AdjustmentRowVm> ArchiveAdjustmentCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }

    public DateTime FromDate
    {
        get => _fromDate;
        set
        {
            if (!SetProperty(ref _fromDate, value.Date))
            {
                return;
            }

            if (_fromDate > _toDate)
            {
                _toDate = _fromDate;
                OnPropertyChanged(nameof(ToDate));
            }

            ReloadSheet();
        }
    }

    public DateTime ToDate
    {
        get => _toDate;
        set
        {
            if (!SetProperty(ref _toDate, value.Date))
            {
                return;
            }

            if (_toDate < _fromDate)
            {
                _fromDate = _toDate;
                OnPropertyChanged(nameof(FromDate));
            }

            ReloadSheet();
        }
    }

    public NamedOption? SelectedEmployee
    {
        get => _selectedEmployee;
        set { if (SetProperty(ref _selectedEmployee, value)) ReloadSheet(); }
    }

    public NamedOption? SelectedDepartment
    {
        get => _selectedDepartment;
        set { if (SetProperty(ref _selectedDepartment, value)) ReloadSheet(); }
    }

    public NamedOption? SelectedDevice
    {
        get => _selectedDevice;
        set { if (SetProperty(ref _selectedDevice, value)) ReloadSheet(); }
    }

    public NamedOption? SelectedShift
    {
        get => _selectedShift;
        set { if (SetProperty(ref _selectedShift, value)) ReloadSheet(); }
    }

    public NamedOption? SelectedStatus
    {
        get => _selectedStatus;
        set { if (SetProperty(ref _selectedStatus, value)) ReloadSheet(); }
    }

    public AttendanceRowVm? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                AddManualPunchCommand.RaiseCanExecuteChanged();
                AddExceptionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                AddManualPunchCommand.RaiseCanExecuteChanged();
                AddExceptionCommand.RaiseCanExecuteChanged();
                ArchiveAdjustmentCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsDetailOpen
    {
        get => _isDetailOpen;
        private set => SetProperty(ref _isDetailOpen, value);
    }

    public string DetailTitle { get => _detailTitle; private set => SetProperty(ref _detailTitle, value); }

    public string DetailSubtitle { get => _detailSubtitle; private set => SetProperty(ref _detailSubtitle, value); }

    public string DetailStatus { get => _detailStatus; private set => SetProperty(ref _detailStatus, value); }

    public string DetailTimes { get => _detailTimes; private set => SetProperty(ref _detailTimes, value); }

    public string DetailTotals { get => _detailTotals; private set => SetProperty(ref _detailTotals, value); }

    public string DetailNotes { get => _detailNotes; private set => SetProperty(ref _detailNotes, value); }

    public string ManualTime { get => _manualTime; set => SetProperty(ref _manualTime, value); }

    public string ManualNote { get => _manualNote; set => SetProperty(ref _manualNote, value); }

    public AdjustmentKindOption? SelectedExceptionKind
    {
        get => _selectedExceptionKind;
        set => SetProperty(ref _selectedExceptionKind, value);
    }

    public string ExceptionStart { get => _exceptionStart; set => SetProperty(ref _exceptionStart, value); }

    public string ExceptionEnd { get => _exceptionEnd; set => SetProperty(ref _exceptionEnd, value); }

    public int ExceptionMinutes { get => _exceptionMinutes; set => SetProperty(ref _exceptionMinutes, value); }

    public string ExceptionReference { get => _exceptionReference; set => SetProperty(ref _exceptionReference, value); }

    public string ExceptionNote { get => _exceptionNote; set => SetProperty(ref _exceptionNote, value); }

    public string SummaryText { get => _summaryText; private set => SetProperty(ref _summaryText, value); }

    public bool IsArabic => _host.Session.IsArabic;

    public string RangeText => $"{FromDate:dd/MM/yyyy} → {ToDate:dd/MM/yyyy}";

    public async Task RefreshAsync()
    {
        try
        {
            var arabic = _host.Session.IsArabic;
            var (employees, departments, devices, shifts) = await Task.Run(() =>
            {
                var e = _host.EmployeeRepo.GetAll(includeArchived: true);
                var d = _host.Organization.GetDepartments(includeArchived: true);
                var dev = _host.Devices.GetDevices(includeArchived: true);
                var s = _host.Shifts.GetRules(includeArchived: true);
                return (e, d, dev, s);
            }).ConfigureAwait(true);

            var employeeId = SelectedEmployee?.Id;
            var departmentId = SelectedDepartment?.Id;
            var deviceId = SelectedDevice?.Id;
            var shiftId = SelectedShift?.Id;
            var statusId = SelectedStatus?.Id;

            Employees.Clear();
            Employees.Add(new NamedOption(string.Empty, arabic ? "كل الموظفين" : "Tous les employés"));
            foreach (var employee in employees.OrderBy(e => e.LastNameFr ?? e.DisplayNameAr, StringComparer.OrdinalIgnoreCase))
            {
                Employees.Add(new NamedOption(employee.Id, $"{employee.Matricule} — {(arabic ? employee.DisplayNameAr : employee.DisplayNameFr)}"));
            }

            Departments.Clear();
            Departments.Add(new NamedOption(string.Empty, arabic ? "كل المصالح" : "Tous les services"));
            foreach (var department in departments.Where(d => !d.IsArchived))
            {
                Departments.Add(new NamedOption(department.Id, arabic ? department.NameAr : department.NameFr));
            }

            Devices.Clear();
            Devices.Add(new NamedOption(string.Empty, arabic ? "كل الأجهزة" : "Tous les terminaux"));
            foreach (var device in devices.Where(d => !d.IsArchived))
            {
                Devices.Add(new NamedOption(device.Id, device.Name));
            }

            Shifts.Clear();
            Shifts.Add(new NamedOption(string.Empty, arabic ? "كل الورديات" : "Toutes les règles"));
            foreach (var shift in shifts.Where(s => !s.IsArchived))
            {
                Shifts.Add(new NamedOption(shift.Id, arabic ? shift.NameAr : shift.NameFr));
            }

            Statuses.Clear();
            Statuses.Add(new NamedOption(string.Empty, arabic ? "كل الحالات" : "Tous les statuts"));
            foreach (var status in Enum.GetValues<AttendanceStatus>())
            {
                Statuses.Add(new NamedOption(status.ToString(), Labels.Status(status, arabic)));
            }

            ExceptionKinds.Clear();
            ExceptionKinds.Add(new AdjustmentKindOption(AdjustmentKind.Mission, Labels.Kind(AdjustmentKind.Mission, arabic)));
            ExceptionKinds.Add(new AdjustmentKindOption(AdjustmentKind.ExitPermit, Labels.Kind(AdjustmentKind.ExitPermit, arabic)));
            ExceptionKinds.Add(new AdjustmentKindOption(AdjustmentKind.AnnualLeave, Labels.Kind(AdjustmentKind.AnnualLeave, arabic)));
            ExceptionKinds.Add(new AdjustmentKindOption(AdjustmentKind.SickLeave, Labels.Kind(AdjustmentKind.SickLeave, arabic)));
            ExceptionKinds.Add(new AdjustmentKindOption(AdjustmentKind.OtherLeave, Labels.Kind(AdjustmentKind.OtherLeave, arabic)));

            SelectedEmployee = Employees.FirstOrDefault(o => o.Id == employeeId) ?? Employees[0];
            SelectedDepartment = Departments.FirstOrDefault(o => o.Id == departmentId) ?? Departments[0];
            SelectedDevice = Devices.FirstOrDefault(o => o.Id == deviceId) ?? Devices[0];
            SelectedShift = Shifts.FirstOrDefault(o => o.Id == shiftId) ?? Shifts[0];
            SelectedStatus = Statuses.FirstOrDefault(o => o.Id == statusId) ?? Statuses[0];
            SelectedExceptionKind ??= ExceptionKinds[0];

            ReloadSheet();
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Attendance refresh failed.", ex);
            _ui.Notify(Labels.Error(null, IsArabic), UiSeverity.Error);
        }
    }

    public void ReloadSheet()
    {
        try
        {
            var arabic = _host.Session.IsArabic;
            var query = new AttendanceQuery
            {
                From = FromDate,
                To = ToDate,
                EmployeeId = string.IsNullOrWhiteSpace(SelectedEmployee?.Id) ? null : SelectedEmployee.Id,
                DepartmentId = string.IsNullOrWhiteSpace(SelectedDepartment?.Id) ? null : SelectedDepartment.Id,
                DeviceId = string.IsNullOrWhiteSpace(SelectedDevice?.Id) ? null : SelectedDevice.Id,
                WorkRuleId = string.IsNullOrWhiteSpace(SelectedShift?.Id) ? null : SelectedShift.Id,
                Status = string.IsNullOrWhiteSpace(SelectedStatus?.Id)
                    ? null
                    : Enum.TryParse<AttendanceStatus>(SelectedStatus.Id, out var status) ? status : null
            };

            var sheet = _host.Attendance.GetSheet(query);
            _sheet = sheet;
            var deviceNames = _host.Devices.GetDevices(includeArchived: true).ToDictionary(d => d.Id, d => d.Name, StringComparer.Ordinal);

            Rows.Clear();
            foreach (var row in sheet.Rows)
            {
                var terminal = row.Employee.PrimaryDeviceId is { } id && deviceNames.TryGetValue(id, out var name) ? name : "—";
                Rows.Add(new AttendanceRowVm(row, terminal, arabic));
            }

            var regular = sheet.Totals.Sum(t => t.RegularMinutes);
            var extra = sheet.Totals.Sum(t => t.ExtraMinutes);
            var credited = sheet.Totals.Sum(t => t.CreditedMinutes);
            var earned = sheet.Totals.Sum(t => t.EarnedDzd);
            SummaryText = arabic
                ? $"{Rows.Count} سطر • {sheet.Totals.Count} موظف • نظامي {Labels.Minutes(regular)} • إضافي {Labels.Minutes(extra)} • معتمد {Labels.Minutes(credited)} • {earned:N0} دج"
                : $"{Rows.Count} lignes • {sheet.Totals.Count} employés • rég. {Labels.Minutes(regular)} • sup. {Labels.Minutes(extra)} • crédit {Labels.Minutes(credited)} • {earned:N0} DZD";
            OnPropertyChanged(nameof(RangeText));
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Attendance sheet reload failed.", ex);
        }
    }

    private void SelectWeek()
    {
        var today = DateTime.Today;
        var offset = ((int)today.DayOfWeek + 7) % 7; // Sunday = 0
        FromDate = today.AddDays(-offset);
        ToDate = FromDate.AddDays(6);
    }

    private void SelectMonth()
    {
        var today = DateTime.Today;
        FromDate = new DateTime(today.Year, today.Month, 1);
        ToDate = FromDate.AddMonths(1).AddDays(-1);
    }

    private void ClearFilters()
    {
        FromDate = DateTime.Today;
        ToDate = DateTime.Today;
        SelectedEmployee = Employees.FirstOrDefault();
        SelectedDepartment = Departments.FirstOrDefault();
        SelectedDevice = Devices.FirstOrDefault();
        SelectedShift = Shifts.FirstOrDefault();
        SelectedStatus = Statuses.FirstOrDefault();
    }

    private async Task OpenDetailAsync(AttendanceRowVm? row)
    {
        if (row is null)
        {
            return;
        }

        SelectedRow = row;
        var arabic = _host.Session.IsArabic;
        DetailTitle = row.Name;
        DetailSubtitle = $"{row.DateText} — {row.DayText} — {row.Department}";

        var deviceNames = _host.Devices.GetDevices(includeArchived: true).ToDictionary(d => d.Id, d => d.Name, StringComparer.Ordinal);

        var detail = await Task.Run(() => _host.Attendance.GetDayDetail(row.Employee.Id, row.Date)).ConfigureAwait(true);
        if (detail is null)
        {
            return;
        }

        DetailPunches.Clear();
        foreach (var punch in detail.Punches.OrderBy(p => p.PunchTimeLocal))
        {
            var terminal = punch.DeviceId is { } id && deviceNames.TryGetValue(id, out var name) ? name : "—";
            DetailPunches.Add(new PunchLineVm(punch, terminal, arabic));
        }

        DetailAdjustments.Clear();
        foreach (var adjustment in detail.Adjustments.OrderBy(a => a.StartLocal))
        {
            DetailAdjustments.Add(new AdjustmentRowVm(adjustment, arabic));
        }

        var day = detail.Result;
        DetailStatus = Labels.Status(day.Status, arabic);
        DetailTimes = arabic
            ? $"صباح: {Format(day.MorningIn)} • غداء: {Format(day.LunchOut)} / {Format(day.LunchIn)} • مساء: {Format(day.EveningOut)}"
            : $"Matin : {Format(day.MorningIn)} • Déj. : {Format(day.LunchOut)} / {Format(day.LunchIn)} • Soir : {Format(day.EveningOut)}";
        DetailTotals = arabic
            ? $"نظامي {Labels.Minutes(day.RegularWorkedMinutes)} • إضافي {Labels.Minutes(day.ExtraWorkedMinutes)} • معتمد {Labels.Minutes(day.CreditedMinutes)} • الإجمالي {Labels.Minutes(day.EffectiveTotalMinutes)} • تأخر {day.LateMinutes} د • البصمات {day.RawScanCount}"
            : $"Rég. {Labels.Minutes(day.RegularWorkedMinutes)} • Sup. {Labels.Minutes(day.ExtraWorkedMinutes)} • Crédit {Labels.Minutes(day.CreditedMinutes)} • Total {Labels.Minutes(day.EffectiveTotalMinutes)} • Retard {day.LateMinutes} min • Scans {day.RawScanCount}";
        DetailNotes = string.Join(" • ", day.Notes.Select(n => Labels.Note(n, arabic)));

        ManualTime = day.MorningIn?.ToString("HH:mm") ?? "08:00";
        IsDetailOpen = true;
    }

    private async Task AddManualPunchAsync()
    {
        if (SelectedRow is null)
        {
            return;
        }

        if (!TimeSpan.TryParseExact(ManualTime, @"hh\:mm", CultureInfo.InvariantCulture, out var time))
        {
            _ui.Notify(Labels.Error(ErrorCodes.InvalidTime, IsArabic), UiSeverity.Error);
            return;
        }

        IsBusy = true;
        try
        {
            var result = _host.Attendance.AddManualPunch(SelectedRow.Employee.Id, SelectedRow.Date, time, ManualNote, _host.Session.Actor);
            if (!result.Success)
            {
                _ui.Notify(Labels.Error(result.ErrorCode, IsArabic, result.Detail), UiSeverity.Error);
                return;
            }

            _ui.Notify(IsArabic ? "تمت إضافة البصمة اليدوية." : "Pointage manuel ajouté.", UiSeverity.Success);
            ManualNote = string.Empty;
            await OpenDetailAsync(SelectedRow).ConfigureAwait(true);
            ReloadSheet();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddExceptionAsync()
    {
        if (SelectedRow is null || SelectedExceptionKind is null)
        {
            return;
        }

        DateTime? start = null;
        DateTime? end = null;
        if (TimeSpan.TryParseExact(ExceptionStart, @"hh\:mm", CultureInfo.InvariantCulture, out var startTime))
        {
            start = SelectedRow.Date + startTime;
        }

        if (TimeSpan.TryParseExact(ExceptionEnd, @"hh\:mm", CultureInfo.InvariantCulture, out var endTime))
        {
            end = SelectedRow.Date + endTime;
        }

        IsBusy = true;
        try
        {
            var result = _host.Attendance.AddException(
                SelectedRow.Employee.Id,
                SelectedExceptionKind.Kind,
                SelectedRow.Date,
                start,
                end,
                ExceptionMinutes,
                ExceptionReference,
                ExceptionNote,
                _host.Session.Actor);

            if (!result.Success)
            {
                _ui.Notify(Labels.Error(result.ErrorCode, IsArabic, result.Detail), UiSeverity.Error);
                return;
            }

            _ui.Notify(IsArabic ? "تم تسجيل الاستثناء الرسمي." : "Exception officielle enregistrée.", UiSeverity.Success);
            ExceptionReference = string.Empty;
            ExceptionNote = string.Empty;
            await OpenDetailAsync(SelectedRow).ConfigureAwait(true);
            ReloadSheet();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ArchiveAdjustmentAsync(AdjustmentRowVm? row)
    {
        if (row is null || SelectedRow is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _host.Attendance.ArchiveAdjustment(row.Model.Id, "archived from sheet", _host.Session.Actor);
            _ui.Notify(IsArabic ? "تم أرشفة السطر." : "Ligne archivée.", UiSeverity.Warning);
            await OpenDetailAsync(SelectedRow).ConfigureAwait(true);
            ReloadSheet();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>CSV export of the currently filtered sheet.</summary>
    public string ExportCsv(string path)
    {
        if (_sheet is null)
        {
            ReloadSheet();
        }

        return _host.Attendance.ExportCsv(_sheet ?? new AttendanceSheet([], [], FromDate, ToDate), path, IsArabic);
    }

    /// <summary>Printable official bilingual OPGI report.</summary>
    public string ExportHtml(string path)
    {
        if (_sheet is null)
        {
            ReloadSheet();
        }

        return _host.Attendance.ExportHtmlReport(_sheet ?? new AttendanceSheet([], [], FromDate, ToDate), path, IsArabic);
    }

    private static string Format(DateTime? value) => value?.ToString("HH:mm") ?? "—";
}
