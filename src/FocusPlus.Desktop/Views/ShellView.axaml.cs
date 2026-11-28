using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FocusPlus.Application;

namespace FocusPlus.Desktop.Views;

public partial class ShellView : UserControl
{
    private readonly ContentControl _pageHost;
    private readonly Grid _shellRoot;
    private readonly Border _sidebar;
    private readonly Grid _mainArea;
    private readonly Border _snackBar;
    private readonly TextBlock _snackText;
    private readonly Dictionary<string, Func<Control>> _pages;
    private readonly Button[] _navButtons;
    private readonly Dictionary<Button, string> _navKeys;
    private readonly DispatcherTimer _snackTimer;
    private string _current = "dashboard";
    private bool _detached;

    public ShellView()
    {
        AvaloniaXamlLoader.Load(this);
        _pageHost = this.FindControl<ContentControl>("PageHost")!;
        _shellRoot = this.FindControl<Grid>("ShellRoot")!;
        _sidebar = this.FindControl<Border>("Sidebar")!;
        _mainArea = this.FindControl<Grid>("MainArea")!;
        _snackBar = this.FindControl<Border>("SnackBar")!;
        _snackText = this.FindControl<TextBlock>("SnackText")!;
        _navButtons =
        [
            this.FindControl<Button>("NavDashboard")!,
            this.FindControl<Button>("NavTerminals")!,
            this.FindControl<Button>("NavEmployees")!,
            this.FindControl<Button>("NavAttendance")!,
            this.FindControl<Button>("NavSettings")!
        ];

        _navKeys = new Dictionary<Button, string>
        {
            [_navButtons[0]] = "Dashboard",
            [_navButtons[1]] = "Terminals",
            [_navButtons[2]] = "Employees",
            [_navButtons[3]] = "Attendance",
            [_navButtons[4]] = "Settings"
        };

        _pages = new Dictionary<string, Func<Control>>
        {
            ["dashboard"] = () => new DashboardView(),
            ["terminals"] = () => new DevicesView(),
            ["employees"] = () => new EmployeesView(),
            ["attendance"] = () => new AttendanceView(),
            ["settings"] = () => new SettingsView()
        };

        _snackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _snackTimer.Tick += OnSnackTimer;

        App.Messenger.Message += OnMessage;
        App.LanguageChanged += OnLanguageChanged;
        DetachedFromVisualTree += OnDetached;

        Show("dashboard");
        ApplyLanguage();
    }

    private void Nav_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key })
        {
            Show(key);
        }
    }

    private void Lang_Click(object? sender, RoutedEventArgs e) => App.SetLanguage(!App.IsArabic);

    private void SignOut_Click(object? sender, RoutedEventArgs e)
    {
        App.Host.Session.SignOut();
        App.SetContent(new LoginView());
    }

    private void Show(string key)
    {
        if (!_pages.TryGetValue(key, out var factory))
        {
            return;
        }

        _current = key;
        _pageHost.Content = factory();
        foreach (var button in _navButtons)
        {
            button.Classes.Remove("active");
            if (button.Tag as string == key)
            {
                button.Classes.Add("active");
            }
        }
        ApplyLanguage();
    }

    /// <summary>Re-creates the mounted page so VM-generated strings pick up the new language.</summary>
    private void RebuildCurrentPage()
    {
        if (_pages.TryGetValue(_current, out var factory))
        {
            _pageHost.Content = factory();
        }
    }

    private void OnLanguageChanged()
    {
        ApplyLanguage();
        RebuildCurrentPage();
    }

    private void OnMessage(string message, UiSeverity severity, string? title)
    {
        _snackText.Text = title is null ? message : $"{title}: {message}";
        _snackBar.Background = severity switch
        {
            UiSeverity.Error => Brush("ErrorBrush", _snackBar.Background),
            UiSeverity.Warning => Brush("WarningBrush", _snackBar.Background),
            UiSeverity.Success => Brush("SuccessBrush", _snackBar.Background),
            _ => Brush("EmeraldDeepBrush", _snackBar.Background)
        };
        _snackBar.IsVisible = true;
        _snackTimer.Stop();
        _snackTimer.Start();
    }

    private static IBrush? Brush(string key, IBrush? fallback) =>
        Avalonia.Application.Current is { } app && app.TryFindResource(key, out var value) && value is IBrush brush
            ? brush
            : fallback;

    private void OnSnackTimer(object? sender, EventArgs e)
    {
        _snackTimer.Stop();
        _snackBar.IsVisible = false;
    }

    private void ApplyLanguage()
    {
        FlowDirection = App.Direction;

        // Avalonia does not mirror Grid columns for RTL, so place the sidebar explicitly.
        // The fixed 224px track must follow the sidebar; otherwise RTL leaves the workspace 224px wide.
        var rtl = App.IsArabic;
        _shellRoot.ColumnDefinitions = new ColumnDefinitions(rtl ? "*,224" : "224,*");
        Grid.SetColumn(_sidebar, rtl ? 1 : 0);
        Grid.SetColumn(_mainArea, rtl ? 0 : 1);

        foreach (var button in _navButtons)
        {
            if (_navKeys.TryGetValue(button, out var key))
            {
                button.Content = Lang.Get(key);
            }
            button.HorizontalContentAlignment = rtl ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }

        Set("LangButton", "Language");
        Set("SignOutButton", "SignOut");
        Set("PageTitle", _current switch
        {
            "terminals" => "Terminals",
            "employees" => "Employees",
            "attendance" => "Attendance",
            "settings" => "Settings",
            _ => "Dashboard"
        });

        var sub = this.FindControl<TextBlock>("SideSubtitle");
        if (sub is not null) sub.Text = Lang.Get("BrandSubtitle");
        var op = this.FindControl<TextBlock>("OperatorText");
        if (op is not null) op.Text = App.Host.Session.Actor;
        var status = this.FindControl<TextBlock>("StatusText");
        if (status is not null) status.Text = Lang.Get("Ready");
    }

    private void Set(string control, string key)
    {
        var button = this.FindControl<Button>(control);
        if (button is not null)
        {
            button.Content = Lang.Get(key);
        }
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        _snackTimer.Stop();
        _snackTimer.Tick -= OnSnackTimer;
        App.Messenger.Message -= OnMessage;
        App.LanguageChanged -= OnLanguageChanged;
    }
}

/// <summary>Keeps keyboard focus inside an open drawer so Tab cannot reach the workspace behind it.</summary>
internal static class DrawerFocusTrap
{
    /// <summary>
    /// Wraps Tab/Shift+Tab at the drawer's focus boundaries and pulls focus back into the drawer
    /// when it starts outside. Returns true when focus was moved and the key handled.
    /// </summary>
    public static bool TryHandleTab(Visual scope, TopLevel? top, KeyEventArgs e)
    {
        if (e.Key != Key.Tab)
        {
            return false;
        }

        var candidates = scope.GetVisualDescendants()
            .OfType<InputElement>()
            .Where(control => control.Focusable && control.IsEffectivelyEnabled && control.IsEffectivelyVisible)
            .ToList();

        if (candidates.Count == 0)
        {
            return false;
        }

        var backwards = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var focused = top?.FocusManager?.GetFocusedElement() as InputElement;
        var index = focused is null ? -1 : candidates.IndexOf(focused);

        if (index < 0)
        {
            candidates[backwards ? candidates.Count - 1 : 0].Focus();
            return true;
        }

        if (backwards && index == 0)
        {
            candidates[^1].Focus();
            return true;
        }

        if (!backwards && index == candidates.Count - 1)
        {
            candidates[0].Focus();
            return true;
        }

        return false;
    }
}
