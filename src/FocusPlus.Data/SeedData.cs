using Dapper;
using FocusPlus.Domain;
using FocusPlus.Domain.Entities;
using Microsoft.Data.Sqlite;

namespace FocusPlus.Data;

/// <summary>
/// Seeds ONLY the real OPGI M'Sila baseline: organisation structure, official work shifts and the
/// initial SuperAdmin. Never seeds employees, devices or punches — those must come from real usage.
/// </summary>
public static class SeedData
{
    public const string InitialAdminUsername = "admin";
    public const string InitialAdminPassword = "Admin@2026";

    public const string SettingLanguage = "ui.language";
    public const string SettingTheme = "ui.theme";
    public const string SettingInitialized = "app.initializedUtc";
    public const string SettingScanTimeout = "device.scan.timeoutMs";
    public const string SettingMonitorInterval = "device.monitor.intervalSeconds";

    public static void EnsureBaseline(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var transaction = connection.BeginTransaction();

        var ruleCount = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM WorkRules;", transaction: transaction);
        WorkRule official;
        WorkRule freeTime;
        if (ruleCount == 0)
        {
            official = WorkRule.CreateOfficialDefault();
            freeTime = WorkRule.CreateFreeTime();
            InsertRule(connection, transaction, official);
            InsertRule(connection, transaction, freeTime);
        }
        else
        {
            official = connection.QuerySingleOrDefault<WorkRule>(
                "SELECT * FROM WorkRules WHERE Code = @code LIMIT 1;",
                new { code = WorkRule.OfficialCode },
                transaction) ?? WorkRule.CreateOfficialDefault();
            freeTime = connection.QuerySingleOrDefault<WorkRule>(
                "SELECT * FROM WorkRules WHERE Code = @code LIMIT 1;",
                new { code = WorkRule.FreeTimeCode },
                transaction) ?? WorkRule.CreateFreeTime();
        }

        var siteCount = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Sites;", transaction: transaction);
        Site site;
        if (siteCount == 0)
        {
            site = new Site
            {
                Code = "OPGI-MSL",
                NameAr = "ديوان الترقية والتسيير العقاري — ولاية المسيلة",
                NameFr = "Office de Promotion et de Gestion Immobilière — M'Sila",
                Address = "شارع 5 جويلية، المسيلة — Rue du 5 Juillet, M'Sila"
            };
            InsertSite(connection, transaction, site);
        }
        else
        {
            site = connection.QueryFirst<Site>("SELECT * FROM Sites ORDER BY CreatedAtUtc LIMIT 1;", transaction: transaction);
        }

        var departmentCount = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Departments;", transaction: transaction);
        if (departmentCount == 0)
        {
            InsertDepartments(connection, transaction, site.Id, official.Id);
        }

        var userCount = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM AppUsers;", transaction: transaction);
        if (userCount == 0)
        {
            var password = Environment.GetEnvironmentVariable("FOCUS_ADMIN_PASSWORD");
            if (string.IsNullOrWhiteSpace(password))
            {
                password = InitialAdminPassword;
            }

            var (hash, salt, iterations) = PasswordHasher.Create(password);
            var admin = new AppUser
            {
                Username = InitialAdminUsername,
                DisplayName = "SuperAdmin — المدير العام للنظام",
                PasswordHash = hash,
                PasswordSalt = salt,
                PasswordIterations = iterations,
                Role = UserRole.SuperAdmin,
                MustChangePassword = true
            };

            connection.Execute(
                """
                INSERT INTO AppUsers (Id, Username, DisplayName, PasswordHash, PasswordSalt, PasswordIterations,
                                      Role, MustChangePassword, LastLoginUtc, IsArchived, ArchivedAtUtc, ArchiveReason,
                                      ArchivedBy, CreatedAtUtc)
                VALUES (@Id, @Username, @DisplayName, @PasswordHash, @PasswordSalt, @PasswordIterations,
                        @Role, @MustChangePassword, NULL, 0, NULL, NULL, NULL, @CreatedAtUtc);
                """,
                admin,
                transaction);
        }

        void Setting(string key, string value) =>
            connection.Execute(
                "INSERT INTO AppSettings (Key, Value) VALUES (@key, @value) ON CONFLICT(Key) DO NOTHING;",
                new { key, value },
                transaction);

        Setting(SettingLanguage, "ar");
        Setting(SettingTheme, "Light");
        Setting(SettingInitialized, DateTime.UtcNow.ToString("O"));
        Setting(SettingScanTimeout, "250");
        Setting(SettingMonitorInterval, "20");

        transaction.Commit();
    }

    private static void InsertRule(SqliteConnection connection, SqliteTransaction transaction, WorkRule rule)
    {
        connection.Execute(
            """
            INSERT INTO WorkRules (Id, Code, NameAr, NameFr, Mode, MorningStartMin, LunchStartMin, LunchEndMin,
                                   EveningEndMin, MorningGraceMinutes, AfternoonGraceMinutes, EarlyLeaveGraceMinutes,
                                   AutoDeductLunchBreak, LunchDeductionMinutes, AntiBounceMinutes,
                                   StandardDailyTargetMinutes, IsDefault, IsArchived, ArchivedAtUtc, ArchiveReason,
                                   ArchivedBy, CreatedAtUtc)
            VALUES (@Id, @Code, @NameAr, @NameFr, @Mode, @MorningStartMin, @LunchStartMin, @LunchEndMin,
                    @EveningEndMin, @MorningGraceMinutes, @AfternoonGraceMinutes, @EarlyLeaveGraceMinutes,
                    @AutoDeductLunchBreak, @LunchDeductionMinutes, @AntiBounceMinutes,
                    @StandardDailyTargetMinutes, @IsDefault, 0, NULL, NULL, NULL, @CreatedAtUtc);
            """,
            rule,
            transaction);
    }

    private static void InsertSite(SqliteConnection connection, SqliteTransaction transaction, Site site)
    {
        connection.Execute(
            """
            INSERT INTO Sites (Id, Code, NameAr, NameFr, Address, IsArchived, ArchivedAtUtc, ArchiveReason,
                               ArchivedBy, CreatedAtUtc)
            VALUES (@Id, @Code, @NameAr, @NameFr, @Address, 0, NULL, NULL, NULL, @CreatedAtUtc);
            """,
            site,
            transaction);
    }

    private static void InsertDepartments(SqliteConnection connection, SqliteTransaction transaction, string siteId, string officialRuleId)
    {
        var direction = new Department
        {
            Code = "DG",
            NameAr = "المديرية العامة",
            NameFr = "Direction Générale",
            SiteId = siteId,
            DefaultWorkRuleId = officialRuleId,
            ManagerNameAr = "المدير العام",
            ManagerNameFr = "Directeur Général"
        };

        var definitions = new (string Code, string Ar, string Fr, string? ManagerAr, string? ManagerFr)[]
        {
            ("SEC-DG", "أمانة المدير العام", "Secrétariat du Directeur Général", null, null),
            ("DRH", "مديرية الموارد البشرية", "Direction des Ressources Humaines", null, null),
            ("DFC", "مديرية المالية والمحاسبة", "Direction des Finances et de la Comptabilité", null, null),
            ("DGI", "مديرية التسيير العقاري", "Direction de la Gestion Immobilière", null, null),
            ("DPI", "مديرية الترقية العقارية", "Direction de la Promotion Immobilière", null, null),
            ("DPF", "مديرية البرمجة والمالية", "Direction de la Programmation et des Finances", null, null),
            ("DMO", "مديرية الوسائل والتنظيم", "Direction des Moyens et de l'Organisation", null, null),
            ("CI", "خلية الإعلام الآلي", "Cellule Informatique", null, null),
            ("AG-MSL", "الوكالة العقارية المحلية بالمسيلة", "Agence Immobilière Locale de M'Sila", null, null),
            ("SERV-TECH", "مصلحة الصيانة والأشغال", "Service Maintenance et Travaux", null, null),
            ("SERV-ARCH", "مصلحة الأرشيف والوثائق", "Service des Archives et Documents", null, null)
        };

        var all = new List<Department> { direction };
        foreach (var definition in definitions)
        {
            all.Add(new Department
            {
                Code = definition.Code,
                NameAr = definition.Ar,
                NameFr = definition.Fr,
                SiteId = siteId,
                ParentDepartmentId = direction.Id,
                DefaultWorkRuleId = officialRuleId,
                ManagerNameAr = definition.ManagerAr,
                ManagerNameFr = definition.ManagerFr
            });
        }

        // The local agency is attached to the property management direction.
        var dgi = all.First(d => d.Code == "DGI");
        all.First(d => d.Code == "AG-MSL").ParentDepartmentId = dgi.Id;

        foreach (var department in all)
        {
            connection.Execute(
                """
                INSERT INTO Departments (Id, Code, NameAr, NameFr, SiteId, ParentDepartmentId, DefaultWorkRuleId,
                                         ManagerNameAr, ManagerNameFr, IsArchived, ArchivedAtUtc, ArchiveReason,
                                         ArchivedBy, CreatedAtUtc)
                VALUES (@Id, @Code, @NameAr, @NameFr, @SiteId, @ParentDepartmentId, @DefaultWorkRuleId,
                        @ManagerNameAr, @ManagerNameFr, 0, NULL, NULL, NULL, @CreatedAtUtc);
                """,
                department,
                transaction);
        }
    }
}
