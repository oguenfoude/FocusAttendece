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

    public BackupFileInfo CreateBackup(string actor = "operator")
    {
        var backup = backups.CreateBackup("manual");
        audit.Add(Log("backup.created", actor, backup.Path));
        return backup;
    }

    public IReadOnlyList<BackupFileInfo> ListBackups() => backups.ListBackups();

    public BackupFileInfo RestoreBackup(string path, string actor)
    {
        var safety = backups.Restore(path);
        audit.Add(Log("backup.restored", actor, $"{path} (safety={safety.Path})"));
        return safety;
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