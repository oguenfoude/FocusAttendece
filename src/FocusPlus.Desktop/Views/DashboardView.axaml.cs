using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FocusPlus.Application.ViewModels;

namespace FocusPlus.Desktop.Views;

public partial class DashboardView : UserControl
{
    private readonly DashboardViewModel _vm;

    public DashboardView()
    {
        AvaloniaXamlLoader.Load(this);
        _vm = new DashboardViewModel(App.Host, App.Messenger);
        DataContext = _vm;
        _ = _vm.RefreshAsync();
        App.LanguageChanged += ApplyLanguage;
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        FlowDirection = App.IsArabic ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
        Set("KpiPresent", "Present");
        Set("KpiLate", "Late");
        Set("KpiAbsent", "Absent");
        Set("KpiLunch", "OnLunch");
        Set("KpiTerminals", "OnlineTerminals");
        Set("KpiScans", "TodayScans");
        Set("HoursLabel", "RegularHours");
        Set("ExtraLabel", "ExtraHours");
        Set("DevicesTitle", "Terminals");
        Set("ActivityTitle", "Activity");
        Set("ScanButton", "ScanLan");
        Set("PullButton", "PullAll");
        Set("ClockButton", "SyncAll");
        Set("RefreshButton", "Refresh");
    }

    private void Set(string control, string key)
    {
        var text = this.FindControl<TextBlock>(control);
        if (text is not null)
        {
            text.Text = Lang.Get(key);
        }
        else
        {
            var button = this.FindControl<Button>(control);
            if (button is not null)
            {
                button.Content = Lang.Get(key);
            }
        }
    }
}
