using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using FocusPlus.Application;
using FocusPlus.Application.Services;
using FocusPlus.Desktop.Views;

namespace FocusPlus.Desktop;

public partial class App : Avalonia.Application
{
    private static int _shutdownRequested;

    public static AppHost Host { get; private set; } = null!;
    public static UiMessenger Messenger { get; private set; } = null!;
    public static bool IsArabic { get; private set; } = true;

    /// <summary>Root flow direction; every workspace inherits it unless it overrides a technical value.</summary>
    public static FlowDirection Direction =>
        IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public static event Action? LanguageChanged;
    public static event Action<bool>? ThemeChanged;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Messenger = new UiMessenger();
        // Lang logs the failing file and exception itself; the handler only surfaces a localized message.
        Lang.LoadFailed += (_, _) =>
            Messenger.Notify(Lang.Get("ErrorLocalization"), UiSeverity.Error, Lang.Get("AppTitle"));
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        Lang.Load(IsArabic); // Arabic is the default until the persisted preference is read.

        var hostReady = InitializeHost();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (hostReady)
            {
                StartBackgroundServices();
            }

            var window = new MainWindow
            {
                FlowDirection = Direction,
                Title = Lang.Get("AppTitle")
            };
            desktop.MainWindow = window;
            desktop.Exit += OnExit;

            if (!hostReady)
            {
                window.Show(BuildStartupError());
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static bool InitializeHost()
    {
        try
        {
            Host = new AppHost(AppHost.DefaultDataDirectory);
            Host.Initialize();
            IsArabic = Host.Session.IsArabic;
            Lang.Load(IsArabic);
            return true;
        }
        catch (Exception ex)
        {
            LogStartupFailure(ex);
            return false;
        }
    }

    /// <summary>Exception detail is logged only; the startup screen never renders raw exception text.</summary>
    private static void LogStartupFailure(Exception ex)
    {
        if (Host is { } host)
        {
            host.Logger.Error("Application startup failed.", ex);
            return;
        }

        Console.Error.WriteLine($"Application startup failed: {ex}");
    }

    private static void StartBackgroundServices()
    {
        try
        {
            Host.StartBackgroundServices();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Host.Logger.Error("Background services failed to start.", ex);
            Messenger.Notify(Lang.Get("BackgroundServicesFailed"), UiSeverity.Error);
        }
    }

    private static Border BuildStartupError() => new Border
    {
        Padding = new Thickness(32),
        Child = new StackPanel
        {
            Spacing = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = Lang.Get("StartupFailed"),
                    FontSize = 18,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = Lang.Get("ErrorGeneric"),
                    Classes = { "subtle" },
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                }
            }
        }
    };

    private static void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Host.Logger.Error("Unhandled UI exception.", e.Exception);
        Messenger.Notify(
            Lang.Get("ErrorGeneric"),
            UiSeverity.Error,
            Lang.Get("AppTitle"));
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
        if (IsArabic == arabic)
        {
            return;
        }

        IsArabic = arabic;
        Host.Session.IsArabic = arabic;

        var code = arabic ? "ar" : "fr";
        if (Host.Settings.GetLanguage() != code)
        {
            Host.Settings.SetLanguage(code);
        }

        Lang.Load(arabic);

        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } window)
        {
            window.FlowDirection = Direction;
            window.Title = Lang.Get("AppTitle");
        }

        LanguageChanged?.Invoke();
    }

    public static void RaiseThemeChanged(bool dark) => ThemeChanged?.Invoke(dark);

    private static void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) == 1 || Host is null)
        {
            return;
        }

        try
        {
            _ = Host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Host.Logger.Error("Application shutdown failed.", ex);
        }
    }
}
