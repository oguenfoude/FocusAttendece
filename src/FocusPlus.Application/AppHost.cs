using FocusPlus.Application.Services;
using FocusPlus.Data;
using FocusPlus.Data.Repositories;
using FocusPlus.Hardware;

namespace FocusPlus.Application;

/// <summary>
/// Composition root: owns the SQLite database, every repository and service, and the background
/// device monitor. Created once at startup; also used directly by the automated tests.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    public AppHost(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
        System.IO.Directory.CreateDirectory(DataDirectory);

        Db = new Database(Path.Combine(DataDirectory, "focus-attendance.db"));
        Logger = new AppLogger(Path.Combine(DataDirectory, "Logs"));

        Sites = new SiteRepository(Db);
        Departments = new DepartmentRepository(Db);
        WorkRules = new WorkRuleRepository(Db);
        EmployeeRepo = new EmployeeRepository(Db);
        DeviceRepo = new DeviceRepository(Db);
        PunchRepo = new PunchRepository(Db);
        AdjustmentRepo = new AttendanceAdjustmentRepository(Db);
        Audit = new AuditRepository(Db);
        HardwareLogs = new HardwareOperationLogRepository(Db);
        Users = new AppUserRepository(Db);
        Preferences = new SettingsRepository(Db);

        Backups = new BackupService(Db);
        Logger.Info("ZKTeco engine: native port-4370 protocol (cross-platform, no vendor SDK).");
        Hardware = new ZkHardwareService();

        Auth = new AuthService(Users, Audit);
        Organization = new OrganizationService(Sites, Departments, Audit);
        Shifts = new WorkRuleService(WorkRules, Audit);
        EmployeeDirectory = new EmployeeService(EmployeeRepo, Departments, WorkRules, DeviceRepo, PunchRepo, Audit, HardwareLogs, Hardware);
        Devices = new DeviceService(DeviceRepo, EmployeeRepo, PunchRepo, Audit, HardwareLogs, Hardware, Logger);
        Attendance = new AttendanceService(EmployeeRepo, Departments, WorkRules, PunchRepo, AdjustmentRepo, DeviceRepo, Audit);
        Dashboard = new DashboardService(Attendance, Devices, PunchRepo, Departments, WorkRules, EmployeeRepo);
        Settings = new SettingsService(Db, Backups, Preferences, Users, Auth, Audit);
        Monitor = new DeviceMonitorService(Devices, Logger);
        BackupScheduler = new BackupSchedulerService(Backups, Logger);
    }

    public string DataDirectory { get; }

    public Database Db { get; }

    public AppLogger Logger { get; }

    public AppSession Session { get; } = new();

    public SiteRepository Sites { get; }

    public DepartmentRepository Departments { get; }

    public WorkRuleRepository WorkRules { get; }

    public EmployeeRepository EmployeeRepo { get; }

    public DeviceRepository DeviceRepo { get; }

    public PunchRepository PunchRepo { get; }

    public AttendanceAdjustmentRepository AdjustmentRepo { get; }

    public AuditRepository Audit { get; }

    public HardwareOperationLogRepository HardwareLogs { get; }

    public AppUserRepository Users { get; }

    public SettingsRepository Preferences { get; }

    public BackupService Backups { get; }

    public ZkHardwareService Hardware { get; }

    public AuthService Auth { get; }

    public OrganizationService Organization { get; }

    public WorkRuleService Shifts { get; }

    public EmployeeService EmployeeDirectory { get; }

    public DeviceService Devices { get; }

    public AttendanceService Attendance { get; }

    public DashboardService Dashboard { get; }

    public SettingsService Settings { get; }

    public DeviceMonitorService Monitor { get; }

    public BackupSchedulerService BackupScheduler { get; }

    public static string DefaultDataDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FocusPlus");
            }

            if (OperatingSystem.IsMacOS())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "FocusPlus");
            }

            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var root = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
                : xdg;
            return Path.Combine(root, "FocusPlus");
        }
    }

    /// <summary>
    /// Takes a pre-migration backup when the database already exists, creates the schema,
    /// seeds the OPGI baseline and starts the 4-hour rolling backup loop.
    /// </summary>
    public void Initialize(bool createStartupBackup = true)
    {
        var existed = File.Exists(Db.DatabasePath);
        if (existed && createStartupBackup)
        {
            try
            {
                var backup = Backups.CreateBackup("startup");
                Logger.Info($"Pre-migration backup: {backup.Path}");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                Logger.Error("Pre-migration backup failed.", ex);
            }
        }

        Db.Initialize();

        Session.IsArabic = Settings.GetLanguage() != "fr";
        Monitor.IntervalSeconds = Settings.GetMonitorIntervalSeconds();
        Logger.Info($"Database ready at {Db.DatabasePath} (schema v{Db.GetInfo().SchemaVersion}).");
    }

    public void StartBackgroundServices()
    {
        Monitor.Start();
        BackupScheduler.Start();
    }

    public void StartMonitoring() => StartBackgroundServices();

    public async ValueTask DisposeAsync()
    {
        await BackupScheduler.StopAsync().ConfigureAwait(false);
        await Monitor.StopAsync().ConfigureAwait(false);
        Db.Dispose();
    }
}
