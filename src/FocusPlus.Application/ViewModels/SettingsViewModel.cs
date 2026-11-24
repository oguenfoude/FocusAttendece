using System.Collections.ObjectModel;
using System.Globalization;
using FocusPlus.Application.Mvvm;
using FocusPlus.Application.Services;
using FocusPlus.Data;
using FocusPlus.Domain;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Application.ViewModels;

public sealed class WorkRuleRowVm
{
    public WorkRuleRowVm(WorkRule model, int employees, bool arabic)
    {
        Model = model;
        Name = arabic ? model.NameAr : model.NameFr;
        ModeText = Labels.ShiftMode(model.Mode, arabic);
        TimesText = model.Mode == WorkMode.FreeTime
            ? (arabic ? "احتساب كل الساعات الفعلية" : "Toutes les heures effectives")
            : $"{model.MorningStart:h\\:mm} – {model.LunchStart:h\\:mm} / {model.LunchEnd:h\\:mm} – {model.EveningEnd:h\\:mm}";
        GraceText = model.Mode == WorkMode.FreeTime
            ? "—"
            : (arabic ? $"{model.MorningGraceMinutes} د" : $"{model.MorningGraceMinutes} min");
        TargetText = Labels.Minutes(model.StandardDailyTargetMinutes);
        EmployeesText = employees.ToString(CultureInfo.InvariantCulture);
        IsDefault = model.IsDefault;
        IsArchived = model.IsArchived;
        DefaultText = model.IsDefault ? (arabic ? "افتراضية" : "Par défaut") : string.Empty;
    }

    public WorkRule Model { get; }
    public string Name { get; }
    public string Code => Model.Code;
    public string ModeText { get; }
    public string TimesText { get; }
    public string GraceText { get; }
    public string TargetText { get; }
    public string EmployeesText { get; }
    public string DefaultText { get; }
    public bool IsDefault { get; }
    public bool IsArchived { get; }
}

public sealed class DepartmentRowVm
{
    public DepartmentRowVm(Department model, string parent, int employees, bool arabic)
    {
        Model = model;
        Name = arabic ? model.NameAr : model.NameFr;
        Parent = parent;
        EmployeesText = employees.ToString(CultureInfo.InvariantCulture);
        Manager = (arabic ? model.ManagerNameAr : model.ManagerNameFr) ?? "—";
        IsArchived = model.IsArchived;
    }

    public Department Model { get; }
    public string Name { get; }
    public string Code => Model.Code;
    public string Parent { get; }
    public string Manager { get; }
    public string EmployeesText { get; }
    public bool IsArchived { get; }
}

public sealed class BackupRowVm
{
    public BackupRowVm(BackupFileInfo file)
    {
        File = file;
        CreatedText = DateTime.SpecifyKind(file.CreatedUtc, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        SizeText = $"{file.SizeMb:0.00} MB";
    }

    public BackupFileInfo File { get; }
    public string Name => File.FileName;
    public string CreatedText { get; }
    public string SizeText { get; }
    public string Path => File.Path;
}

public sealed class OperatorRowVm
{
    public OperatorRowVm(AppUser model, bool arabic)
    {
        Model = model;
        RoleText = model.Role == UserRole.SuperAdmin
            ? (arabic ? "مشرف عام" : "SuperAdmin")
            : (arabic ? "مشغل" : "Opérateur");
        LastLogin = model.LastLoginUtc is { } login
            ? DateTime.SpecifyKind(login, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy HH:mm")
            : "—";
    }

    public AppUser Model { get; }
    public string Username => Model.Username;
    public string DisplayName => Model.DisplayName;
    public string RoleText { get; }
    public string LastLogin { get; }
}

/// <summary>Settings: appearance, shifts CRUD, departments CRUD, backups and operator security.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IUiMessenger _ui;

    private bool _isArabic;
    private bool _isDark;
    private bool _isBusy;
    private string _dbPath = string.Empty;
    private string _dbSize = string.Empty;
    private string _dbJournal = string.Empty;
    private string _dbCounts = string.Empty;
    private string _dbSchema = string.Empty;
    private BackupRowVm? _selectedBackup;
    private WorkRuleEditor? _ruleEditor;
    private bool _isRuleDrawerOpen;
    private DepartmentEditor? _departmentEditor;
    private bool _isDepartmentDrawerOpen;
    private NamedOption? _ruleParent;
    private string _operatorUsername = string.Empty;
    private string _operatorDisplayName = string.Empty;
    private string _operatorPassword = string.Empty;
    private string _currentPassword = string.Empty;
    private string _newPassword = string.Empty;
    private string _confirmPassword = string.Empty;
    private int _scanTimeout = 250;
    private int _monitorInterval = 20;
    private string _branchNameAr = string.Empty;
    private string _branchNameFr = string.Empty;
    private string _branchAddress = string.Empty;
    private string _branchCity = string.Empty;
    private string _branchPhone = string.Empty;
    private string _todayBackupsText = string.Empty;
    private string _latestBackupText = string.Empty;

    public SettingsViewModel(AppHost host, IUiMessenger ui)
    {
        _host = host;
        _ui = ui;
        _isArabic = host.Session.IsArabic;
        _isDark = string.Equals(host.Settings.GetTheme(), "Dark", StringComparison.OrdinalIgnoreCase);

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        SelectArabicCommand = new RelayCommand(() => SetLanguage(true));
        SelectFrenchCommand = new RelayCommand(() => SetLanguage(false));
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        BackupNowCommand = new AsyncRelayCommand(BackupNowAsync, () => !IsBusy);
        RestoreBackupCommand = new AsyncRelayCommand(RestoreSelectedBackupAsync, () => !IsBusy && SelectedBackup is not null);
        OpenBackupFolderCommand = new RelayCommand(() => OpenFolderRequested?.Invoke(_host.Db.BackupDirectory));

        NewRuleCommand = new RelayCommand(NewRule);
        EditRuleCommand = new RelayCommand<WorkRuleRowVm>(EditRule);
        SaveRuleCommand = new AsyncRelayCommand(SaveRuleAsync, () => !IsBusy && RuleEditor is not null);
        CloseRuleDrawerCommand = new RelayCommand(() => IsRuleDrawerOpen = false);
        ArchiveRuleCommand = new AsyncRelayCommand<WorkRuleRowVm>(ArchiveRuleAsync, _ => !IsBusy);
        RestoreRuleCommand = new AsyncRelayCommand<WorkRuleRowVm>(RestoreRuleAsync, _ => !IsBusy);
        MakeDefaultRuleCommand = new AsyncRelayCommand<WorkRuleRowVm>(MakeDefaultRuleAsync, _ => !IsBusy);

        NewDepartmentCommand = new RelayCommand(NewDepartment);
        EditDepartmentCommand = new RelayCommand<DepartmentRowVm>(EditDepartment);
        SaveDepartmentCommand = new AsyncRelayCommand(SaveDepartmentAsync, () => !IsBusy && DepartmentEditor is not null);
        CloseDepartmentDrawerCommand = new RelayCommand(() => IsDepartmentDrawerOpen = false);
        ArchiveDepartmentCommand = new AsyncRelayCommand<DepartmentRowVm>(ArchiveDepartmentAsync, _ => !IsBusy);
        RestoreDepartmentCommand = new AsyncRelayCommand<DepartmentRowVm>(RestoreDepartmentAsync, _ => !IsBusy);

        CreateOperatorCommand = new AsyncRelayCommand(CreateOperatorAsync, () => !IsBusy);
        ChangePasswordCommand = new AsyncRelayCommand(ChangePasswordAsync, () => !IsBusy);
        SavePreferencesCommand = new AsyncRelayCommand(SavePreferencesAsync, () => !IsBusy);
        SaveBrandingCommand = new AsyncRelayCommand(SaveBrandingAsync, () => !IsBusy);
    }

    public string BrandCompany => _brandCompany;

    public string BrandProduct => _brandProduct;

    private readonly string _brandCompany = "FocusPlus";
    private readonly string _brandProduct = "FocusPlus";

    public string BranchNameAr { get => _branchNameAr; set => SetProperty(ref _branchNameAr, value); }

    public string BranchNameFr { get => _branchNameFr; set => SetProperty(ref _branchNameFr, value); }

    public string BranchAddress { get => _branchAddress; set => SetProperty(ref _branchAddress, value); }

    public string BranchCity { get => _branchCity; set => SetProperty(ref _branchCity, value); }

    public string BranchPhone { get => _branchPhone; set => SetProperty(ref _branchPhone, value); }

    public string TodayBackupsText { get => _todayBackupsText; private set => SetProperty(ref _todayBackupsText, value); }

    public string LatestBackupText { get => _latestBackupText; private set => SetProperty(ref _latestBackupText, value); }

    public AsyncRelayCommand SaveBrandingCommand { get; }

    public ObservableCollection<WorkRuleRowVm> Rules { get; } = [];

    public ObservableCollection<DepartmentRowVm> Departments { get; } = [];

    public ObservableCollection<NamedOption> DepartmentOptions { get; } = [];

    public ObservableCollection<NamedOption> RuleOptions { get; } = [];

    public ObservableCollection<BackupRowVm> Backups { get; } = [];

    public ObservableCollection<OperatorRowVm> Operators { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand SelectArabicCommand { get; }
    public RelayCommand SelectFrenchCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public AsyncRelayCommand BackupNowCommand { get; }
    public AsyncRelayCommand RestoreBackupCommand { get; }
    public RelayCommand OpenBackupFolderCommand { get; }
    public RelayCommand NewRuleCommand { get; }
    public RelayCommand<WorkRuleRowVm> EditRuleCommand { get; }
    public AsyncRelayCommand SaveRuleCommand { get; }
    public RelayCommand CloseRuleDrawerCommand { get; }
    public AsyncRelayCommand<WorkRuleRowVm> ArchiveRuleCommand { get; }
    public AsyncRelayCommand<WorkRuleRowVm> RestoreRuleCommand { get; }
    public AsyncRelayCommand<WorkRuleRowVm> MakeDefaultRuleCommand { get; }
    public RelayCommand NewDepartmentCommand { get; }
    public RelayCommand<DepartmentRowVm> EditDepartmentCommand { get; }
    public AsyncRelayCommand SaveDepartmentCommand { get; }
    public RelayCommand CloseDepartmentDrawerCommand { get; }
    public AsyncRelayCommand<DepartmentRowVm> ArchiveDepartmentCommand { get; }
    public AsyncRelayCommand<DepartmentRowVm> RestoreDepartmentCommand { get; }
    public AsyncRelayCommand CreateOperatorCommand { get; }
    public AsyncRelayCommand ChangePasswordCommand { get; }
    public AsyncRelayCommand SavePreferencesCommand { get; }

    public event Action? AppearanceChanged;

    public event Action<string>? OpenFolderRequested;

    public event Action? DataChanged;

    public bool IsArabic
    {
        get => _isArabic;
        private set => SetProperty(ref _isArabic, value);
    }

    public bool IsDark
    {
        get => _isDark;
        private set => SetProperty(ref _isDark, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                BackupNowCommand.RaiseCanExecuteChanged();
                RestoreBackupCommand.RaiseCanExecuteChanged();
                SaveRuleCommand.RaiseCanExecuteChanged();
                SaveDepartmentCommand.RaiseCanExecuteChanged();
                CreateOperatorCommand.RaiseCanExecuteChanged();
                ChangePasswordCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DbPath { get => _dbPath; private set => SetProperty(ref _dbPath, value); }
    public string DbSize { get => _dbSize; private set => SetProperty(ref _dbSize, value); }
    public string DbJournal { get => _dbJournal; private set => SetProperty(ref _dbJournal, value); }
    public string DbCounts { get => _dbCounts; private set => SetProperty(ref _dbCounts, value); }
    public string DbSchema { get => _dbSchema; private set => SetProperty(ref _dbSchema, value); }

    public BackupRowVm? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            if (SetProperty(ref _selectedBackup, value))
            {
                RestoreBackupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public WorkRuleEditor? RuleEditor
    {
        get => _ruleEditor;
        private set
        {
            if (SetProperty(ref _ruleEditor, value))
            {
                OnPropertyChanged(nameof(RuleDrawerTitle));
                SaveRuleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsRuleDrawerOpen
    {
        get => _isRuleDrawerOpen;
        private set => SetProperty(ref _isRuleDrawerOpen, value);
    }

    public string RuleDrawerTitle => IsArabic ? "وردية عمل / قاعدة توقيت" : "Règle de travail / horaire";

    public DepartmentEditor? DepartmentEditor
    {
        get => _departmentEditor;
        private set
        {
            if (SetProperty(ref _departmentEditor, value))
            {
                OnPropertyChanged(nameof(DepartmentDrawerTitle));
                SaveDepartmentCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsDepartmentDrawerOpen
    {
        get => _isDepartmentDrawerOpen;
        private set => SetProperty(ref _isDepartmentDrawerOpen, value);
    }

    public string DepartmentDrawerTitle => IsArabic ? "مصلحة / مديرية" : "Service / direction";

    public NamedOption? RuleParent
    {
        get => _ruleParent;
        set => SetProperty(ref _ruleParent, value);
    }

    public string OperatorUsername { get => _operatorUsername; set => SetProperty(ref _operatorUsername, value); }
    public string OperatorDisplayName { get => _operatorDisplayName; set => SetProperty(ref _operatorDisplayName, value); }
    public string OperatorPassword { get => _operatorPassword; set => SetProperty(ref _operatorPassword, value); }
    public string CurrentPassword { get => _currentPassword; set => SetProperty(ref _currentPassword, value); }
    public string NewPassword { get => _newPassword; set => SetProperty(ref _newPassword, value); }
    public string ConfirmPassword { get => _confirmPassword; set => SetProperty(ref _confirmPassword, value); }
    public int ScanTimeout { get => _scanTimeout; set => SetProperty(ref _scanTimeout, value); }
    public int MonitorInterval { get => _monitorInterval; set => SetProperty(ref _monitorInterval, value); }

    public string LanguageLabel => IsArabic ? "العربية (RTL)" : "Français (LTR)";

    public string ThemeLabel => IsDark ? (IsArabic ? "داكن" : "Sombre") : (IsArabic ? "فاتح" : "Clair");

    public async Task RefreshAsync()
    {
        try
        {
            var arabic = _host.Session.IsArabic;
            var info = await Task.Run(() => _host.Settings.GetDatabaseInfo()).ConfigureAwait(true);
            DbPath = info.Path;
            DbSize = $"{info.SizeMb:0.00} MB";
            DbJournal = info.JournalMode.ToUpperInvariant();
            DbSchema = $"v{info.SchemaVersion}";
            DbCounts = arabic
                ? $"{info.Employees} موظف • {info.Devices} جهاز • {info.Punches} بصمة • {info.Departments} مصلحة"
                : $"{info.Employees} employés • {info.Devices} terminaux • {info.Punches} pointages • {info.Departments} services";

            var (rules, departmentList, counts) = await Task.Run(() =>
            {
                var r = _host.Shifts.GetRules(includeArchived: true);
                var d = _host.Organization.GetDepartments(includeArchived: true);
                var c = _host.Organization.GetDepartmentEmployeeCounts();
                return (r, d, c);
            }).ConfigureAwait(true);

            var ruleCounts = _host.Shifts.GetEmployeeCounts();
            var departmentById = departmentList.ToDictionary(d => d.Id, d => d, StringComparer.Ordinal);

            Rules.Clear();
            foreach (var rule in rules.OrderByDescending(r => r.IsDefault).ThenBy(r => r.Code, StringComparer.Ordinal))
            {
                Rules.Add(new WorkRuleRowVm(rule, ruleCounts.GetValueOrDefault(rule.Id), arabic));
            }

            Departments.Clear();
            foreach (var department in departmentList.OrderBy(d => d.Code, StringComparer.Ordinal))
            {
                var parent = department.ParentDepartmentId is { } parentId && departmentById.TryGetValue(parentId, out var found)
                    ? (arabic ? found.NameAr : found.NameFr)
                    : "—";
                Departments.Add(new DepartmentRowVm(department, parent, counts.GetValueOrDefault(department.Id), arabic));
            }

            RuleOptions.Clear();
            foreach (var rule in rules.Where(r => !r.IsArchived))
            {
                RuleOptions.Add(new NamedOption(rule.Id, arabic ? rule.NameAr : rule.NameFr));
            }

            DepartmentOptions.Clear();
            DepartmentOptions.Add(new NamedOption(string.Empty, arabic ? "بدون أب" : "Aucun parent"));
            foreach (var department in departmentList.Where(d => !d.IsArchived))
            {
                DepartmentOptions.Add(new NamedOption(department.Id, arabic ? department.NameAr : department.NameFr));
            }

            Backups.Clear();
            foreach (var backup in _host.Settings.ListBackups())
            {
                Backups.Add(new BackupRowVm(backup));
            }

            Operators.Clear();
            foreach (var user in _host.Settings.GetOperators())
            {
                Operators.Add(new OperatorRowVm(user, arabic));
            }

            ScanTimeout = _host.Settings.GetScanTimeoutMs();
            MonitorInterval = _host.Settings.GetMonitorIntervalSeconds();

            var sites = _host.Organization.GetSites(includeArchived: true);
            var site = sites.Count > 0 ? sites[0] : null;
            BranchNameAr = site?.NameAr ?? string.Empty;
            BranchNameFr = site?.NameFr ?? string.Empty;
            BranchAddress = site?.Address ?? string.Empty;
            BranchCity = _host.Settings.Get("brand.city", "M'Sila / المسيلة");
            BranchPhone = _host.Settings.Get("brand.phone", string.Empty);

            var today = _host.Backups.GetBackupsForDate(DateTime.Today);
            TodayBackupsText = $"{today.Count} / {BackupService.SlotsPerDay}";
            LatestBackupText = today.Count == 0
                ? "—"
                : today.OrderByDescending(b => b.CreatedUtc).First().CreatedUtc.ToLocalTime().ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Settings refresh failed.", ex);
            _ui.Notify(Labels.Error(null, IsArabic), UiSeverity.Error);
        }
    }

    private void SetLanguage(bool arabic)
    {
        _host.Session.IsArabic = arabic;
        _host.Settings.SetLanguage(arabic ? "ar" : "fr");
        IsArabic = arabic;
        OnPropertyChanged(nameof(LanguageLabel));
        AppearanceChanged?.Invoke();
        _ = RefreshAsync();
    }

    private void ToggleTheme()
    {
        IsDark = !IsDark;
        _host.Settings.SetTheme(IsDark ? "Dark" : "Light");
        OnPropertyChanged(nameof(ThemeLabel));
        AppearanceChanged?.Invoke();
    }

    private async Task SavePreferencesAsync()
    {
        IsBusy = true;
        try
        {
            _host.Settings.SetScanTimeoutMs(ScanTimeout);
            _host.Settings.SetMonitorIntervalSeconds(MonitorInterval);
            _host.Monitor.IntervalSeconds = MonitorInterval;
            _ui.Notify(IsArabic ? "تم حفظ الإعدادات." : "Préférences enregistrées.", UiSeverity.Success);
            await Task.CompletedTask;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveBrandingAsync()
    {
        IsBusy = true;
        try
        {
            var sites = _host.Organization.GetSites(includeArchived: true);
            var site = sites.Count > 0 ? sites[0] : null;
            if (site is null)
            {
                _ui.Notify(Labels.Error(ErrorCodes.NotFound, IsArabic), UiSeverity.Error);
                return;
            }

            site.NameAr = string.IsNullOrWhiteSpace(BranchNameAr) ? site.NameAr : BranchNameAr.Trim();
            site.NameFr = string.IsNullOrWhiteSpace(BranchNameFr) ? site.NameFr : BranchNameFr.Trim();
            site.Address = string.IsNullOrWhiteSpace(BranchAddress) ? site.Address : BranchAddress.Trim();

            var result = _host.Organization.SaveSite(site, _host.Session.Actor);
            if (!result.Success)
            {
                _ui.Notify(Labels.Error(result.ErrorCode, IsArabic, result.Detail), UiSeverity.Error);
                return;
            }

            _host.Settings.Set("brand.city", BranchCity, _host.Session.Actor);
            _host.Settings.Set("brand.phone", BranchPhone, _host.Session.Actor);

            _ui.Notify(IsArabic ? "تم حفظ هوية الفرع." : "Identité de la direction enregistrée.", UiSeverity.Success);
            DataChanged?.Invoke();
            AppearanceChanged?.Invoke();
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task BackupNowAsync()
    {
        IsBusy = true;
        try
        {
            var backup = await Task.Run(() => _host.Settings.CreateBackup(_host.Session.Actor)).ConfigureAwait(true);
            _ui.Notify(
                IsArabic ? $"تم إنشاء نسخة احتياطية: {backup.FileName}" : $"Sauvegarde créée : {backup.FileName}",
                UiSeverity.Success);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Backup failed.", ex);
            _ui.Notify(Labels.Error(null, IsArabic, ex.Message), UiSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Restores the selected backup (the UI asks for confirmation first).</summary>
    public async Task RestoreSelectedBackupAsync()
    {
        if (SelectedBackup is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var safety = await Task.Run(() => _host.Settings.RestoreBackup(SelectedBackup.Path, _host.Session.Actor)).ConfigureAwait(true);
            _ui.Notify(
                IsArabic
                    ? $"تمت الاستعادة. نسخة أمان: {safety.FileName}. يُنصح بإعادة التشغيل."
                    : $"Restauration effectuée. Sauvegarde de sécurité : {safety.FileName}. Redémarrage conseillé.",
                UiSeverity.Warning);
            await RefreshAsync().ConfigureAwait(true);
            DataChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Restore failed.", ex);
            _ui.Notify(Labels.Error(null, IsArabic, ex.Message), UiSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NewRule()
    {
        RuleEditor = new WorkRuleEditor
        {
            Code = "SHIFT-",
            NameAr = string.Empty,
            NameFr = string.Empty,
            IsFreeTime = false
        };
        RuleParent = null;
        IsRuleDrawerOpen = true;
    }

    private void EditRule(WorkRuleRowVm? row)
    {
        if (row is null)
        {
            return;
        }

        RuleEditor = WorkRuleEditor.From(row.Model);
        IsRuleDrawerOpen = true;
    }

    private async Task SaveRuleAsync()
    {
        if (RuleEditor is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var entity = RuleEditor.ToEntity();
            var result = _host.Shifts.Save(entity, _host.Session.Actor);
            if (!result.Success)
            {
                _ui.Notify(Labels.Error(result.ErrorCode, IsArabic, result.Detail), UiSeverity.Error);
                return;
            }

            _ui.Notify(IsArabic ? "تم حفظ قاعدة التوقيت." : "Règle de travail enregistrée.", UiSeverity.Success);
            IsRuleDrawerOpen = false;
            await RefreshAsync().ConfigureAwait(true);
            DataChanged?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ArchiveRuleAsync(WorkRuleRowVm? row)
    {
        if (row is null)
        {
            return;
        }

        var result = _host.Shifts.Archive(row.Model.Id, "archived from settings", _host.Session.Actor);
        if (!result.Success)
        {
            _ui.Notify(Labels.Error(result.ErrorCode, IsArabic), UiSeverity.Warning);
            return;
        }

        _ui.Notify(IsArabic ? "تم أرشفة الوردية." : "Règle archivée.", UiSeverity.Warning);
        await RefreshAsync().ConfigureAwait(true);
        DataChanged?.Invoke();
    }

    private async Task RestoreRuleAsync(WorkRuleRowVm? row)
    {
        if (row is null)
        {
            return;
        }

        _host.Shifts.Restore(row.Model.Id, _host.Session.Actor);
        _ui.Notify(IsArabic ? "تمت استعادة الوردية." : "Règle restaurée.", UiSeverity.Success);
        await RefreshAsync().ConfigureAwait(true);
        DataChanged?.Invoke();
    }

    private async Task MakeDefaultRuleAsync(WorkRuleRowVm? row)
    {
        if (row is null)
        {
            return;
        }

        _host.Shifts.MakeDefault(row.Model.Id, _host.Session.Actor);
        _ui.Notify(IsArabic ? "أصبحت الوردية الافتراضية." : "Règle définie par défaut.", UiSeverity.Success);
        await RefreshAsync().ConfigureAwait(true);
        DataChanged?.Invoke();
    }

    private void NewDepartment()
    {
        DepartmentEditor = new DepartmentEditor
        {
            Code = string.Empty,
            NameAr = string.Empty,
            NameFr = string.Empty,
            SiteId = _host.Organization.GetSites() is { Count: > 0 } sites ? sites[0].Id : null,
            ParentId = null,
            WorkRuleId = _host.Shifts.GetDefault().Id
        };
        IsDepartmentDrawerOpen = true;
    }

    private void EditDepartment(DepartmentRowVm? row)
    {
        if (row is null)
        {
            return;
        }

        DepartmentEditor = DepartmentEditor.From(row.Model);
        IsDepartmentDrawerOpen = true;
    }

    private async Task SaveDepartmentAsync()
    {
        if (DepartmentEditor is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = _host.Organization.SaveDepartment(DepartmentEditor.ToEntity(), _host.Session.Actor);
            if (!result.Success)
            {
                _ui.Notify(Labels.Error(result.ErrorCode, IsArabic, result.Detail), UiSeverity.Error);
                return;
            }

            _ui.Notify(IsArabic ? "تم حفظ المصلحة." : "Service enregistré.", UiSeverity.Success);
            IsDepartmentDrawerOpen = false;
            await RefreshAsync().ConfigureAwait(true);
            DataChanged?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ArchiveDepartmentAsync(DepartmentRowVm? row)
    {
        if (row is null)
        {
            return;
        }

        var result = _host.Organization.ArchiveDepartment(row.Model.Id, "archived from settings", _host.Session.Actor);
        if (!result.Success)
        {
            _ui.Notify(Labels.Error(result.ErrorCode, IsArabic, result.Detail), UiSeverity.Warning);
            return;
        }

        _ui.Notify(IsArabic ? "تمت أرشفة المصلحة." : "Service archivé.", UiSeverity.Warning);
        await RefreshAsync().ConfigureAwait(true);
        DataChanged?.Invoke();
    }

    private async Task RestoreDepartmentAsync(DepartmentRowVm? row)
    {
        if (row is null)
        {
            return;
        }

        _host.Organization.RestoreDepartment(row.Model.Id, _host.Session.Actor);
        _ui.Notify(IsArabic ? "تمت استعادة المصلحة." : "Service restauré.", UiSeverity.Success);
        await RefreshAsync().ConfigureAwait(true);
        DataChanged?.Invoke();
    }

    private async Task CreateOperatorAsync()
    {
        IsBusy = true;
        try
        {
            var result = _host.Settings.CreateOperator(OperatorUsername, OperatorPassword, OperatorDisplayName, _host.Session.Actor);
            if (!result.Success)
            {
                _ui.Notify(Labels.Error(result.ErrorCode, IsArabic, result.Detail), UiSeverity.Error);
                return;
            }

            _ui.Notify(IsArabic ? "تم إنشاء حساب المشغل." : "Compte opérateur créé.", UiSeverity.Success);
            OperatorUsername = string.Empty;
            OperatorDisplayName = string.Empty;
            OperatorPassword = string.Empty;
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ChangePasswordAsync()
    {
        if (NewPassword != ConfirmPassword)
        {
            _ui.Notify(
                IsArabic ? "كلمتا المرور غير متطابقتين." : "Les deux mots de passe ne correspondent pas.",
                UiSeverity.Error);
            return;
        }

        if (_host.Session.User is not { } user)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = _host.Settings.ChangePassword(user, CurrentPassword, NewPassword);
            if (!result.Success)
            {
                _ui.Notify(Labels.Error(result.ErrorCode, IsArabic), UiSeverity.Error);
                return;
            }

            CurrentPassword = string.Empty;
            NewPassword = string.Empty;
            ConfirmPassword = string.Empty;
            _ui.Notify(IsArabic ? "تم تغيير كلمة المرور." : "Mot de passe modifié.", UiSeverity.Success);
            await Task.CompletedTask;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
