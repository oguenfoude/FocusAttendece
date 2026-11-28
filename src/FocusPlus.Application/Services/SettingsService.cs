using FocusPlus.Data;
using FocusPlus.Data.Repositories;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Application.Services;

/// <summary>Application preferences, database health, backups and operator management.</summary>
public sealed class SettingsService(
    Database database,
    BackupService backups,
    SettingsRepository settings,
    AppUserRepository users,
    AuthService auth,
    AuditRepository audit)
{
    public const string LanguageKey = SeedData.SettingLanguage;
    public const string ThemeKey = SeedData.SettingTheme;
    public const string ScanTimeoutKey = SeedData.SettingScanTimeout;
    public const string MonitorIntervalKey = SeedData.SettingMonitorInterval;
    public const string BackupKeepDaysKey = "Backup.KeepDays";
    public const string BackupKeepMonthlyKey = "Backup.KeepMonthly";
    public const string BackupDailyTimeKey = "Backup.DailyTime";

    public string GetLanguage() => settings.Get(LanguageKey) ?? "ar";

    public void SetLanguage(string language)
    {
        settings.Set(LanguageKey, language == "fr" ? "fr" : "ar");
        audit.Add(Log($"settings.language.{language}", "operator"));
    }

    public string GetTheme() => settings.Get(ThemeKey) ?? "Light";

    public void SetTheme(string theme)
    {
        settings.Set(ThemeKey, string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light");
        audit.Add(Log($"settings.theme.{theme}", "operator"));
    }

    public int GetScanTimeoutMs() =>
        int.TryParse(settings.Get(ScanTimeoutKey), out var value) ? Math.Clamp(value, 80, 5000) : 250;

    public void SetScanTimeoutMs(int value) => settings.Set(ScanTimeoutKey, Math.Clamp(value, 80, 5000).ToString());

    public int GetMonitorIntervalSeconds() =>
        int.TryParse(settings.Get(MonitorIntervalKey), out var value) ? Math.Clamp(value, 10, 3600) : 20;

    public void SetMonitorIntervalSeconds(int value) => settings.Set(MonitorIntervalKey, Math.Clamp(value, 10, 3600).ToString());

    public string Get(string key, string fallback) => settings.Get(key) ?? fallback;

    public void Set(string key, string value, string actor)
    {
        settings.Set(key, value);
        audit.Add(Log($"settings.set.{key}", actor, value));
    }

    public DatabaseInfo GetDatabaseInfo() => database.GetInfo();

    /// <summary>Manual "Backup now": forces today's daily file to be replaced with a fresh verified copy.</summary>
    public BackupResult CreateBackup(string actor = "operator")
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException("An actor is required.", nameof(actor));
        }

        return backups.CreateDailyBackup(force: true, actor);
    }

    public IReadOnlyList<BackupRecord> ListBackups() => backups.ListBackupRecords();

    public RestoreResult RestoreBackup(string path, string actor) => backups.Restore(path, actor);

    public int GetBackupKeepDays() =>
        int.TryParse(settings.Get(BackupKeepDaysKey), out var value) && value is >= 1 and <= 3650
            ? value
            : BackupService.DefaultKeepDays;

    public int GetBackupKeepMonthly() =>
        int.TryParse(settings.Get(BackupKeepMonthlyKey), out var value) && value is >= 0 and <= 120
            ? value
            : BackupService.DefaultKeepMonthly;

    public TimeSpan GetDailyBackupTime() =>
        TimeSpan.TryParseExact(settings.Get(BackupDailyTimeKey), @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var value)
        && value >= TimeSpan.Zero && value < TimeSpan.FromDays(1)
            ? value
            : BackupSchedulerService.DefaultDailyTime;

    public void SetBackupPolicy(int keepDays, int keepMonthly, TimeSpan dailyTime, string actor)
    {
        if (keepDays is < 1 or > 3650)
        {
            throw new ArgumentOutOfRangeException(nameof(keepDays), keepDays, "KeepDays must be between 1 and 3650.");
        }

        if (keepMonthly is < 0 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(keepMonthly), keepMonthly, "KeepMonthly must be between 0 and 120.");
        }

        if (dailyTime < TimeSpan.Zero || dailyTime >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(dailyTime), dailyTime, "The daily backup time must be inside a single day.");
        }

        settings.Set(BackupKeepDaysKey, keepDays.ToString(System.Globalization.CultureInfo.InvariantCulture));
        settings.Set(BackupKeepMonthlyKey, keepMonthly.ToString(System.Globalization.CultureInfo.InvariantCulture));
        settings.Set(BackupDailyTimeKey, dailyTime.ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture));
        audit.Add(Log("settings.set.backup.policy", actor,
            $"keepDays={keepDays}; keepMonthly={keepMonthly}; dailyTime={dailyTime:hh\\:mm}"));
    }

    public IReadOnlyList<AppUser> GetOperators() => users.GetAll();

    public OperationResult ChangePassword(AppUser user, string currentPassword, string newPassword)
    {
        var result = auth.ChangePassword(user, currentPassword, newPassword);
        if (result.Success)
        {
            audit.Add(Log("operator.password.changed", user.Username));
        }

        return result;
    }

    public OperationResult CreateOperator(string username, string password, string displayName, string actor) =>
        auth.CreateOperator(username, password, displayName, actor);

    private static AuditLogEntry Log(string action, string actor, string? details = null) => new()
    {
        Action = action,
        Actor = actor,
        EntityType = "Settings",
        Details = details
    };
}