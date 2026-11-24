using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FocusPlus.Application;
using FocusPlus.Desktop.Views;

namespace FocusPlus.Desktop;

public partial class App : Avalonia.Application
{
    public static AppHost Host { get; private set; } = null!;
    public static UiMessenger Messenger { get; private set; } = null!;
    public static bool IsArabic { get; private set; } = true;

    public static event Action? LanguageChanged;
    public static event Action<bool>? ThemeChanged;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Lang.Load(IsArabic);
        Host = new AppHost(AppHost.DefaultDataDirectory);
        Host.Initialize();
        Messenger = new UiMessenger();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void SetContent(Control content)
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is MainWindow main)
        {
            main.Show(content);
        }
    }

    public static void SetLanguage(bool arabic)
    {
        IsArabic = arabic;
        Host.Session.IsArabic = arabic;
        Lang.Load(arabic);
        LanguageChanged?.Invoke();
    }

    public static void RaiseThemeChanged(bool dark) => ThemeChanged?.Invoke(dark);
}
