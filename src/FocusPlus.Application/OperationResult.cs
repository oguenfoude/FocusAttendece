namespace FocusPlus.Application;

/// <summary>Result of a business operation with a stable error code the UI can localise.</summary>
public sealed record OperationResult(bool Success, string? ErrorCode = null, string? Detail = null)
{
    public static OperationResult Ok() => new(true);
    public static OperationResult Fail(string errorCode, string? detail = null) => new(false, errorCode, detail);
}

public static class ErrorCodes
{
    public const string Required = "error.required";
    public const string DuplicateCode = "error.duplicateCode";
    public const string DuplicateMatricule = "error.duplicateMatricule";
    public const string DuplicatePin = "error.duplicatePin";
    public const string DuplicateSerial = "error.duplicateSerial";
    public const string DuplicateEndpoint = "error.duplicateEndpoint";
    public const string NotFound = "error.notFound";
    public const string InvalidIp = "error.invalidIp";
    public const string InvalidPort = "error.invalidPort";
    public const string InvalidTime = "error.invalidTime";
    public const string DeviceUnreachable = "error.deviceUnreachable";
    public const string DeviceSerialMismatch = "error.deviceSerialMismatch";
    public const string Hardware = "error.hardware";
    public const string InvalidCredentials = "error.invalidCredentials";
    public const string AccountArchived = "error.accountArchived";
    public const string PasswordTooShort = "error.passwordTooShort";
    public const string LastDefaultRule = "error.lastDefaultRule";
    public const string InUse = "error.inUse";
    public const string InvalidRange = "error.invalidRange";
}

public enum UiSeverity
{
    Info = 0,
    Success = 1,
    Warning = 2,
    Error = 3
}

/// <summary>Implemented by the UI layer to surface snackbars.</summary>
public interface IUiMessenger
{
    void Notify(string message, UiSeverity severity = UiSeverity.Info, string? title = null);
}

/// <summary>Implemented by the UI layer for single in-window Fluent dialogs (never popups).</summary>
public interface IUserPrompt
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText);
    Task<string?> PromptAsync(string title, string label, string? initialValue = null);
}
