using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FocusPlus.Application;

namespace FocusPlus.Desktop.Views;

public partial class ShellView : UserControl
{
    private readonly ContentControl _pageHost;
    private readonly Dictionary<string, Func<Control>> _pages;
    private readonly Button[] _navButtons;
    private string _current = "dashboard";

    public ShellView()
    {
        AvaloniaXamlLoader.Load(this);
        _pageHost = this.FindControl<ContentControl>("PageHost")!;
        _navButtons =
        [
            this.FindControl<Button>("NavDashboard")!,
            this.FindControl<Button>("NavTerminals")!,
            this.FindControl<Button>("NavEmployees")!,
            this.FindControl<Button>("NavAttendance")!,
            this.FindControl<Button>("NavSettings")!
        ];

        _pages = new Dictionary<string, Func<Control>>
        {
            ["dashboard"] = () => new DashboardView(),
            ["terminals"] = () => new DevicesView(),
            ["employees"] = () => new EmployeesView(),
            ["attendance"] = () => new AttendanceView(),
            ["settings"] = () => new SettingsView()
        };

        App.Messenger.Message += OnMessage;
        App.LanguageChanged += ApplyLanguage;
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

    private void Lang_Click(object? sender, RoutedEventArgs e)
    {
        App.SetLanguage(!App.IsArabic);
        App.Host.Session.IsArabic = App.IsArabic;
        ApplyLanguage();
    }

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

    private void OnMessage(string message, UiSeverity severity, string? title)
    {
        var snack = this.FindControl<Border>("SnackBar")!;
        var text = this.FindControl<TextBlock>("SnackText")!;
        text.Text = title is null ? message : $"{title}: {message}";
        snack.IsVisible = true;
    }

    private void ApplyLanguage()
    {
        FlowDirection = App.IsArabic ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
        Set("NavDashboard", "Dashboard");
        Set("NavTerminals", "Terminals");
        Set("NavEmployees", "Employees");
        Set("NavAttendance", "Attendance");
        Set("NavSettings", "Settings");
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
}
