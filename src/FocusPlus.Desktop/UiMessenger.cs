using FocusPlus.Application;

namespace FocusPlus.Desktop;

/// <summary>In-app notification channel — no modal popups, no cascading windows.</summary>
public sealed class UiMessenger : IUiMessenger
{
    public event Action<string, UiSeverity, string?>? Message;

    public void Notify(string message, UiSeverity severity = UiSeverity.Info, string? title = null) =>
        Message?.Invoke(message, severity, title);
}
