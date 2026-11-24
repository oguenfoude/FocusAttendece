using FocusPlus.Data;
using FocusPlus.Data.Repositories;
using FocusPlus.Domain;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Application.Services;

/// <summary>Operator authentication with PBKDF2 verification and a full audit trail.</summary>
public sealed class AuthService(AppUserRepository users, AuditRepository audit)
{
    public LoginResult Login(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return LoginResult.Fail(ErrorCodes.Required);
        }

        var user = users.GetByUsername(username.Trim());
        if (user is null)
        {
            audit.Add(Entry("login.failed", "unknown"));
            return LoginResult.Fail(ErrorCodes.InvalidCredentials);
        }

        if (user.IsArchived)
        {
            return LoginResult.Fail(ErrorCodes.AccountArchived);
        }

        if (!PasswordHasher.Verify(password, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
        {
            audit.Add(Entry("login.failed", user.Username));
            return LoginResult.Fail(ErrorCodes.InvalidCredentials);
        }

        users.MarkLogin(user.Id, DateTime.UtcNow);
        audit.Add(Entry("login.success", user.Username));
        return LoginResult.Ok(user);
    }

    public OperationResult ChangePassword(AppUser user, string currentPassword, string newPassword)
    {
        if (newPassword.Length < 8)
        {
            return OperationResult.Fail(ErrorCodes.PasswordTooShort);
        }

        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
        {
            return OperationResult.Fail(ErrorCodes.InvalidCredentials);
        }

        var (hash, salt, iterations) = PasswordHasher.Create(newPassword);
        users.UpdatePassword(user.Id, hash, salt, iterations);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.PasswordIterations = iterations;
        user.MustChangePassword = false;
        audit.Add(Entry("operator.password.changed", user.Username));
        return OperationResult.Ok();
    }

    public OperationResult CreateOperator(string username, string password, string displayName, string actor)
    {
        if (string.IsNullOrWhiteSpace(username) || password.Length < 8)
        {
            return OperationResult.Fail(password.Length < 8 ? ErrorCodes.PasswordTooShort : ErrorCodes.Required);
        }

        if (users.GetByUsername(username.Trim()) is not null)
        {
            return OperationResult.Fail(ErrorCodes.DuplicateCode, username);
        }

        var (hash, salt, iterations) = PasswordHasher.Create(password);
        users.Upsert(new AppUser
        {
            Username = username.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim(),
            PasswordHash = hash,
            PasswordSalt = salt,
            PasswordIterations = iterations,
            Role = UserRole.Operator
        });

        audit.Add(Entry("operator.created", actor, username));
        return OperationResult.Ok();
    }

    public IReadOnlyList<AppUser> GetOperators() => users.GetAll();

    private static AuditLogEntry Entry(string action, string actor, string? details = null) => new()
    {
        Action = action,
        Actor = actor,
        EntityType = "AppUser",
        Details = details
    };
}
