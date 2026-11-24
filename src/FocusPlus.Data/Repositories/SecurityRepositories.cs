using Dapper;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Data.Repositories;

public sealed class AppUserRepository(IConnectionFactory factory) : RepositoryBase(factory)
{
    public AppUser? GetByUsername(string username)
    {
        using var connection = Factory.Open();
        return connection.QuerySingleOrDefault<AppUser>(
            "SELECT * FROM AppUsers WHERE Username = @username COLLATE NOCASE LIMIT 1;",
            new { username });
    }

    public IReadOnlyList<AppUser> GetAll(bool includeArchived = false)
    {
        using var connection = Factory.Open();
        var sql = includeArchived
            ? "SELECT * FROM AppUsers ORDER BY Username;"
            : "SELECT * FROM AppUsers WHERE IsArchived = 0 ORDER BY Username;";
        return connection.Query<AppUser>(sql).ToList();
    }

    public void Upsert(AppUser user)
    {
        using var connection = Factory.Open();
        connection.Execute(
            """
            INSERT INTO AppUsers (Id, Username, DisplayName, PasswordHash, PasswordSalt, PasswordIterations,
                                  Role, MustChangePassword, LastLoginUtc, IsArchived, ArchivedAtUtc, ArchiveReason,
                                  ArchivedBy, CreatedAtUtc)
            VALUES (@Id, @Username, @DisplayName, @PasswordHash, @PasswordSalt, @PasswordIterations,
                    @Role, @MustChangePassword, @LastLoginUtc, @IsArchived, @ArchivedAtUtc, @ArchiveReason,
                    @ArchivedBy, @CreatedAtUtc)
            ON CONFLICT(Id) DO UPDATE SET
                Username = excluded.Username, DisplayName = excluded.DisplayName,
                PasswordHash = excluded.PasswordHash, PasswordSalt = excluded.PasswordSalt,
                PasswordIterations = excluded.PasswordIterations, Role = excluded.Role,
                MustChangePassword = excluded.MustChangePassword, IsArchived = excluded.IsArchived,
                ArchivedAtUtc = excluded.ArchivedAtUtc, ArchiveReason = excluded.ArchiveReason,
                ArchivedBy = excluded.ArchivedBy;
            """,
            user);
    }

    public void UpdatePassword(string id, string hash, string salt, int iterations)
    {
        using var connection = Factory.Open();
        connection.Execute(
            """
            UPDATE AppUsers
               SET PasswordHash = @hash, PasswordSalt = @salt, PasswordIterations = @iterations,
                   MustChangePassword = 0
             WHERE Id = @id;
            """,
            new { id, hash, salt, iterations });
    }

    public void MarkLogin(string id, DateTime utcNow)
    {
        using var connection = Factory.Open();
        connection.Execute("UPDATE AppUsers SET LastLoginUtc = @utcNow WHERE Id = @id;", new { id, utcNow });
    }

    public int Count()
    {
        using var connection = Factory.Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM AppUsers WHERE IsArchived = 0;");
    }
}

public sealed class SettingsRepository(IConnectionFactory factory) : RepositoryBase(factory)
{
    public string? Get(string key)
    {
        using var connection = Factory.Open();
        return connection.ExecuteScalar<string?>("SELECT Value FROM AppSettings WHERE Key = @key;", new { key });
    }

    public void Set(string key, string value)
    {
        using var connection = Factory.Open();
        connection.Execute(
            """
            INSERT INTO AppSettings (Key, Value) VALUES (@key, @value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """,
            new { key, value });
    }

    public IReadOnlyDictionary<string, string> GetAll()
    {
        using var connection = Factory.Open();
        return connection.Query<AppSetting>("SELECT * FROM AppSettings;").ToDictionary(s => s.Key, s => s.Value);
    }
}
