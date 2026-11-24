namespace FocusPlus.Domain.Entities;

/// <summary>Append-only audit trail. One row per sensitive action (device IP change, archive, restore…).</summary>
public sealed class AuditLogEntry
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Actor { get; set; } = "system";
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? Details { get; set; }
}

/// <summary>Application operator (login gate). Passwords are PBKDF2-SHA256 hashed with a per-user salt.</summary>
public sealed class AppUser : ArchivableEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public int PasswordIterations { get; set; } = 120_000;
    public UserRole Role { get; set; } = UserRole.SuperAdmin;
    public DateTime? LastLoginUtc { get; set; }

    /// <summary>Forced password change after the first run.</summary>
    public bool MustChangePassword { get; set; }
}

public sealed class AppSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>Result of an authentication attempt.</summary>
public sealed record LoginResult(bool Success, AppUser? User, string? Error)
{
    public static LoginResult Ok(AppUser user) => new(true, user, null);
    public static LoginResult Fail(string error) => new(false, null, error);
}
