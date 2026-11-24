using FocusPlus.Application;

namespace FocusPlus.Tests;

/// <summary>Isolated application host on a throw-away temp folder.</summary>
public sealed class TestHost : IAsyncDisposable
{
    public TestHost()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "focus-attendance-tests", Guid.NewGuid().ToString("N"));
        Host = new AppHost(DataDirectory);
        Host.Initialize(createStartupBackup: false);
    }

    public string DataDirectory { get; }

    public AppHost Host { get; }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Temp files can still be locked by the SQLite pool on shutdown.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: cleanup is best effort in tests.
        }
    }
}

public sealed class NullMessenger : IUiMessenger
{
    public List<string> Messages { get; } = [];

    public void Notify(string message, UiSeverity severity = UiSeverity.Info, string? title = null) =>
        Messages.Add($"{severity}: {message}");
}

public sealed class NullPrompt : IUserPrompt
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText) =>
        Task.FromResult(true);

    public Task<string?> PromptAsync(string title, string label, string? initialValue = null) =>
        Task.FromResult(initialValue);
}
