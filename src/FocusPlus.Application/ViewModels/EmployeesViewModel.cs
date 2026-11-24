using System.Collections.ObjectModel;
using System.Globalization;
using FocusPlus.Application.Mvvm;
using FocusPlus.Data.Repositories;
using FocusPlus.Domain;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Application.ViewModels;

/// <summary>Employees, shifts and salary directory with a full CRUD drawer.</summary>
public sealed class EmployeesViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IUiMessenger _ui;

    private string _search = string.Empty;
    private NamedOption? _selectedDepartment;
    private NamedOption? _selectedDevice;
    private NamedOption? _selectedShift;
    private int _selectedDayIndex;
    private bool _showArchived;
    private bool _isBusy;
    private bool _isDrawerOpen;
    private bool _isNew;
    private Employee? _editing;
    private EmployeeRowVm? _selectedEmployee;
    private string _drawerTitle = string.Empty;
    private bool _sun = true, _mon = true, _tue = true, _wed = true, _thu = true, _fri, _sat;

    public EmployeesViewModel(AppHost host, IUiMessenger ui)
    {
        _host = host;
        _ui = ui;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        NewCommand = new RelayCommand(NewEmployee);
        EditCommand = new RelayCommand<EmployeeRowVm>(EditEmployee);
        CloseDrawerCommand = new RelayCommand(CloseDrawer);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy && Editing is not null);
        ArchiveCommand = new AsyncRelayCommand(ArchiveAsync, () => !IsBusy && Editing is not null && !Editing.IsArchived);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => !IsBusy && Editing is not null && Editing.IsArchived);
        PushCommand = new AsyncRelayCommand(PushAsync, () => !IsBusy && Editing is not null);
        DeleteFromDeviceCommand = new AsyncRelayCommand(DeleteFromDeviceAsync, () => !IsBusy && Editing is not null);
        ImportFromDeviceCommand = new AsyncRelayCommand(ImportFromDeviceAsync, () => !IsBusy && Devices.Count > 0);
        ManageDepartmentsCommand = new RelayCommand(() => NavigateRequested?.Invoke("settings"));
        ClearFiltersCommand = new RelayCommand(ClearFilters);
    }

    public ObservableCollection<EmployeeRowVm> Employees { get; } = [];

    public ObservableCollection<NamedOption> Departments { get; } = [];

    public ObservableCollection<NamedOption> Devices { get; } = [];

    public ObservableCollection<NamedOption> Shifts { get; } = [];

    public ObservableCollection<NamedOption> Sites { get; } = [];

    public ObservableCollection<NamedOption> DepartmentOptions { get; } = [];

    public ObservableCollection<NamedOption> DeviceOptions { get; } = [];

    public ObservableCollection<NamedOption> ShiftOptions { get; } = [];

    public ObservableCollection<PrivilegeOption> PrivilegeOptions { get; } = [];

    public ObservableCollection<NamedOption> DayOptions { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand NewCommand { get; }
    public RelayCommand<EmployeeRowVm> EditCommand { get; }
    public RelayCommand CloseDrawerCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ArchiveCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public AsyncRelayCommand PushCommand { get; }
    public AsyncRelayCommand DeleteFromDeviceCommand { get; }
    public AsyncRelayCommand ImportFromDeviceCommand { get; }
    public RelayCommand ManageDepartmentsCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }

    public event Action<string>? NavigateRequested;

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
            {
                ReloadEmployees();
            }
        }
    }

    public NamedOption? SelectedDepartment
    {
        get => _selectedDepartment;
        set
        {
            if (SetProperty(ref _selectedDepartment, value))
            {
                ReloadEmployees();
            }
        }
    }

    public NamedOption? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                ReloadEmployees();
            }
        }
    }

    public NamedOption? SelectedShift
    {
        get => _selectedShift;
        set
        {
            if (SetProperty(ref _selectedShift, value))
            {
                ReloadEmployees();
            }
        }
    }

    public int SelectedDayIndex
    {
        get => _selectedDayIndex;
        set
        {
            if (SetProperty(ref _selectedDayIndex, value))
            {
                ReloadEmployees();
            }
        }
    }

    public bool ShowArchived
    {
        get => _showArchived;
        set
        {
            if (SetProperty(ref _showArchived, value))
            {
                ReloadEmployees();
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
                SaveCommand.RaiseCanExecuteChanged();
                ArchiveCommand.RaiseCanExecuteChanged();
                RestoreCommand.RaiseCanExecuteChanged();
                PushCommand.RaiseCanExecuteChanged();
                DeleteFromDeviceCommand.RaiseCanExecuteChanged();
                ImportFromDeviceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsDrawerOpen
    {
        get => _isDrawerOpen;
        private set => SetProperty(ref _isDrawerOpen, value);
    }

    public bool IsNew
    {
        get => _isNew;
        private set
        {
            if (SetProperty(ref _isNew, value))
            {
                OnPropertyChanged(nameof(CanEditMatricule));
                ArchiveCommand.RaiseCanExecuteChanged();
                RestoreCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanEditMatricule => IsNew;

    public Employee? Editing
    {
        get => _editing;
        private set
        {
            if (SetProperty(ref _editing, value))
            {
                OnPropertyChanged(nameof(EditingName), nameof(EditingSubtitle));
                SaveCommand.RaiseCanExecuteChanged();
                ArchiveCommand.RaiseCanExecuteChanged();
                RestoreCommand.RaiseCanExecuteChanged();
                PushCommand.RaiseCanExecuteChanged();
                DeleteFromDeviceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public EmployeeRowVm? SelectedEmployee
    {
        get => _selectedEmployee;
        set => SetProperty(ref _selectedEmployee, value);
    }

    public string DrawerTitle
    {
        get => _drawerTitle;
        private set => SetProperty(ref _drawerTitle, value);
    }

    public string EditingName => Editing is null ? "—" : (IsArabic ? Editing.DisplayNameAr : Editing.DisplayNameFr);

    public string EditingSubtitle => Editing?.Matricule ?? "—";

    public bool Sun { get => _sun; set => SetProperty(ref _sun, value); }
    public bool Mon { get => _mon; set => SetProperty(ref _mon, value); }
    public bool Tue { get => _tue; set => SetProperty(ref _tue, value); }
    public bool Wed { get => _wed; set => SetProperty(ref _wed, value); }
    public bool Thu { get => _thu; set => SetProperty(ref _thu, value); }
    public bool Fri { get => _fri; set => SetProperty(ref _fri, value); }
    public bool Sat { get => _sat; set => SetProperty(ref _sat, value); }

    public bool IsArabic => _host.Session.IsArabic;

    public string CountText => IsArabic ? $"{Employees.Count} موظف" : $"{Employees.Count} employé(s)";

    public async Task RefreshAsync()
    {
        try
        {
            var arabic = _host.Session.IsArabic;
            var (departments, devices, shifts, sites) = await Task.Run(() =>
            {
                var d = _host.Organization.GetDepartments(includeArchived: true);
                var dev = _host.Devices.GetDevices(includeArchived: true);
                var s = _host.Shifts.GetRules(includeArchived: true);
                var st = _host.Sites.GetAll(includeArchived: true);
                return (d, dev, s, st);
            }).ConfigureAwait(true);

            var departmentId = SelectedDepartment?.Id;
            var deviceId = SelectedDevice?.Id;
            var shiftId = SelectedShift?.Id;

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

            Sites.Clear();
            foreach (var site in sites.Where(s => !s.IsArchived))
            {
                Sites.Add(new NamedOption(site.Id, arabic ? site.NameAr : site.NameFr));
            }

            DepartmentOptions.Clear();
            foreach (var department in departments.Where(d => !d.IsArchived))
            {
                DepartmentOptions.Add(new NamedOption(department.Id, arabic ? department.NameAr : department.NameFr));
            }

            DeviceOptions.Clear();
            foreach (var device in devices.Where(d => !d.IsArchived))
            {
                DeviceOptions.Add(new NamedOption(device.Id, device.Name));
            }

            ShiftOptions.Clear();
            foreach (var shift in shifts.Where(s => !s.IsArchived))
            {
                ShiftOptions.Add(new NamedOption(shift.Id, arabic ? shift.NameAr : shift.NameFr));
            }

            PrivilegeOptions.Clear();
            PrivilegeOptions.Add(new PrivilegeOption(0, arabic ? "مستخدم" : "Utilisateur"));
            PrivilegeOptions.Add(new PrivilegeOption(2, arabic ? "مسجل" : "Enrôleur"));
            PrivilegeOptions.Add(new PrivilegeOption(6, arabic ? "مدير جهاز" : "Gestionnaire"));
            PrivilegeOptions.Add(new PrivilegeOption(14, arabic ? "مشرف" : "SuperAdmin"));

            DayOptions.Clear();
            DayOptions.Add(new NamedOption("0", arabic ? "كل الأيام" : "Tous les jours"));
            for (var day = 0; day < 7; day++)
            {
                DayOptions.Add(new NamedOption((day + 1).ToString(), Labels.Day((DayOfWeek)day, arabic)));
            }

            SelectedDepartment = Departments.FirstOrDefault(d => d.Id == departmentId) ?? Departments[0];
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == deviceId) ?? Devices[0];
            SelectedShift = Shifts.FirstOrDefault(s => s.Id == shiftId) ?? Shifts[0];

            ReloadEmployees();
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Employee refresh failed.", ex);
            _ui.Notify(Labels.Error(null, IsArabic), UiSeverity.Error);
        }
    }

    public void ReloadEmployees()
    {
        try
        {
            var arabic = _host.Session.IsArabic;
            var filter = new EmployeeFilter
            {
                Search = Search,
                DepartmentId = string.IsNullOrWhiteSpace(SelectedDepartment?.Id) ? null : SelectedDepartment.Id,
                DeviceId = string.IsNullOrWhiteSpace(SelectedDevice?.Id) ? null : SelectedDevice.Id,
                WorkRuleId = string.IsNullOrWhiteSpace(SelectedShift?.Id) ? null : SelectedShift.Id,
                WorkDay = SelectedDayIndex > 0 ? (DayOfWeek)(SelectedDayIndex - 1) : null,
                OnlyArchived = ShowArchived
            };

            var rows = _host.EmployeeDirectory.Search(filter);
            var departmentMap = _host.Organization.GetDepartments(includeArchived: true).ToDictionary(d => d.Id, d => d, StringComparer.Ordinal);
            var ruleMap = _host.Shifts.GetRules(includeArchived: true).ToDictionary(r => r.Id, r => r, StringComparer.Ordinal);
            var deviceMap = _host.Devices.GetDevices(includeArchived: true).ToDictionary(d => d.Id, d => d, StringComparer.Ordinal);
            var fallbackRule = _host.Shifts.GetDefault();

            Employees.Clear();
            foreach (var employee in rows)
            {
                var department = employee.DepartmentId is { } id && departmentMap.TryGetValue(id, out var found) ? found : null;
                var shift = employee.WorkRuleId is { } sid && ruleMap.TryGetValue(sid, out var foundRule) ? foundRule : fallbackRule;
                var device = employee.PrimaryDeviceId is { } did && deviceMap.TryGetValue(did, out var foundDevice) ? foundDevice : null;

                Employees.Add(new EmployeeRowVm(
                    employee,
                    department is null ? "—" : (arabic ? department.NameAr : department.NameFr),
                    arabic ? shift.NameAr : shift.NameFr,
                    device?.Name ?? "—",
                    arabic));
            }

            OnPropertyChanged(nameof(CountText));
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Employee list reload failed.", ex);
        }
    }

    private void NewEmployee()
    {
        IsNew = true;
        DrawerTitle = IsArabic ? "موظف جديد" : "Nouvel employé";
        var defaultRule = _host.Shifts.GetDefault();
        var siteId = Sites.Count > 0 ? Sites[0].Id : null;

        Editing = new Employee
        {
            Matricule = _host.EmployeeDirectory.GetNextMatricule(),
            WorkRuleId = defaultRule.Id,
            SiteId = siteId,
            DepartmentId = Departments.Skip(1).FirstOrDefault()?.Id,
            ScheduledWorkDays = WeekDays.Administrative,
            OvertimeMultiplier = 1.0m,
            HireDate = DateTime.Today
        };

        LoadDays(Editing.ScheduledWorkDays);
        IsDrawerOpen = true;
    }

    private void EditEmployee(EmployeeRowVm? row)
    {
        if (row is null)
        {
            return;
        }

        IsNew = false;
        DrawerTitle = IsArabic ? "بطاقة الموظف" : "Fiche employé";
        Editing = row.Model;
        SelectedEmployee = row;
        LoadDays(Editing.ScheduledWorkDays);
        IsDrawerOpen = true;
    }

    private void CloseDrawer()
    {
        IsDrawerOpen = false;
        Editing = null;
    }

    private void LoadDays(WeekDays mask)
    {
        Sun = mask.Includes(DayOfWeek.Sunday);
        Mon = mask.Includes(DayOfWeek.Monday);
        Tue = mask.Includes(DayOfWeek.Tuesday);
        Wed = mask.Includes(DayOfWeek.Wednesday);
        Thu = mask.Includes(DayOfWeek.Thursday);
        Fri = mask.Includes(DayOfWeek.Friday);
        Sat = mask.Includes(DayOfWeek.Saturday);
    }

    private WeekDays CollectDays()
    {
        var mask = WeekDays.None;
        if (Sun) mask |= WeekDays.Sunday;
        if (Mon) mask |= WeekDays.Monday;
        if (Tue) mask |= WeekDays.Tuesday;
        if (Wed) mask |= WeekDays.Wednesday;
        if (Thu) mask |= WeekDays.Thursday;
        if (Fri) mask |= WeekDays.Friday;
        if (Sat) mask |= WeekDays.Saturday;
        return mask;
    }

    private async Task SaveAsync()
    {
        if (Editing is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Editing.ScheduledWorkDays = CollectDays();
            var result = await Task.Run(() => _host.EmployeeDirectory.Save(Editing, _host.Session.Actor)).ConfigureAwait(true);
            if (!result.Success)
            {
                _ui.Notify(Labels.Error(result.ErrorCode, IsArabic, result.Detail), UiSeverity.Error);
                return;
            }

            _ui.Notify(IsArabic ? "تم حفظ بطاقة الموظف." : "Fiche employé enregistrée.", UiSeverity.Success);
            IsDrawerOpen = false;
            Editing = null;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Employee save failed.", ex);
            _ui.Notify(Labels.Error(null, IsArabic), UiSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ArchiveAsync()
    {
        if (Editing is null)
        {
            return;
        }

        var result = _host.EmployeeDirectory.Archive(Editing.Id, "archived from directory", _host.Session.Actor);
        if (result.Success)
        {
            _ui.Notify(IsArabic ? "تم أرشفة الموظف (دون حذف تاريخه)." : "Employé archivé (historique conservé).", UiSeverity.Warning);
            IsDrawerOpen = false;
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    private async Task RestoreAsync()
    {
        if (Editing is null)
        {
            return;
        }

        var result = _host.EmployeeDirectory.Restore(Editing.Id, _host.Session.Actor);
        if (result.Success)
        {
            _ui.Notify(IsArabic ? "تمت استعادة الموظف." : "Employé restauré.", UiSeverity.Success);
            IsDrawerOpen = false;
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    private async Task PushAsync()
    {
        if (Editing is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _host.EmployeeDirectory.PushToDeviceAsync(Editing.Id, SelectedDevice?.Id, _host.Session.Actor).ConfigureAwait(true);
            _ui.Notify(
                result.Success
                    ? (IsArabic ? "تم تحديث المستخدم داخل الجهاز." : "Utilisateur écrit dans le terminal.")
                    : Labels.Error(result.ErrorCode, IsArabic, result.Detail),
                result.Success ? UiSeverity.Success : UiSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteFromDeviceAsync()
    {
        if (Editing is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _host.EmployeeDirectory.DeleteFromDeviceAsync(Editing).ConfigureAwait(true);
            _ui.Notify(
                result.Success
                    ? (IsArabic ? "تم حذف المستخدم من الجهاز." : "Utilisateur supprimé du terminal.")
                    : Labels.Error(result.ErrorCode, IsArabic, result.Detail),
                result.Success ? UiSeverity.Success : UiSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportFromDeviceAsync()
    {
        var target = !string.IsNullOrWhiteSpace(SelectedDevice?.Id)
            ? SelectedDevice.Id
            : Devices.Skip(1).FirstOrDefault()?.Id;

        if (string.IsNullOrWhiteSpace(target))
        {
            _ui.Notify(IsArabic ? "لا يوجد جهاز مسجل." : "Aucun terminal enregistré.", UiSeverity.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var (result, import) = await _host.EmployeeDirectory.ImportFromDeviceAsync(target, _host.Session.Actor).ConfigureAwait(true);
            if (!result.Success || import is null)
            {
                _ui.Notify(Labels.Error(result.ErrorCode, IsArabic, result.Detail), UiSeverity.Error);
                return;
            }

            _ui.Notify(
                IsArabic
                    ? $"تم استيراد {import.Created} موظف وتحديث {import.Updated}."
                    : $"{import.Created} employé(s) importé(s), {import.Updated} mis à jour.",
                UiSeverity.Success);
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearFilters()
    {
        Search = string.Empty;
        ShowArchived = false;
        SelectedDayIndex = 0;
        SelectedDepartment = Departments.FirstOrDefault();
        SelectedDevice = Devices.FirstOrDefault();
        SelectedShift = Shifts.FirstOrDefault();
    }

    /// <summary>Exports the current directory selection to CSV.</summary>
    public string ExportCsv(string path)
    {
        var arabic = IsArabic;
        var lines = new List<string>
        {
            string.Join(';', arabic
                ? new[] { "الرقم الوظيفي", "المعرف البيومتري", "اللقب والاسم", "المصلحة", "الوردية", "الجهاز", "أيام العمل", "الهاتف", "الوظيفة", "الراتب الأساسي", "الحالة" }
                : new[] { "Matricule", "PIN", "Nom", "Service", "Poste", "Terminal", "Jours", "Téléphone", "Fonction", "Salaire de base", "Statut" })
        };

        foreach (var row in Employees)
        {
            var values = new List<string>(11)
            {
                row.Matricule,
                row.Pin,
                row.Name,
                row.Department,
                row.Shift,
                row.Device,
                row.Days,
                row.Phone,
                row.JobTitle,
                row.Model.BaseSalaryDzd.ToString("0.00", CultureInfo.InvariantCulture),
                row.StatusText
            };
            lines.Add(string.Join(';', values.Select(Escape)));
        }

        File.WriteAllText(path, string.Join(Environment.NewLine, lines), new System.Text.UTF8Encoding(true));
        return path;
    }

    private static string Escape(string value) =>
        value.Contains(';', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
}
