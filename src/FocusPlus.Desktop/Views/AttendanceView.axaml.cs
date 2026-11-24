using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FocusPlus.Application.ViewModels;

namespace FocusPlus.Desktop.Views;

public partial class AttendanceView : UserControl
{
    private readonly AttendanceViewModel _vm;

    public AttendanceView()
    {
        AvaloniaXamlLoader.Load(this);
        _vm = new AttendanceViewModel(App.Host, App.Messenger);
        DataContext = _vm;
        _ = _vm.RefreshAsync();
        App.LanguageChanged += ApplyLanguage;
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        FlowDirection = App.IsArabic ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
        Set("TodayButton", "Date");
        Set("WeekButton", "Attendance");
        Set("MonthButton", "History");
        Set("RefreshButton", "Refresh");
        Set("PunchesLabel", "Punches");
        Set("ManualLabel", "Hours");
        Set("AddManualButton", "Save");
        Set("CloseButton", "Close");
    }

    private void Set(string control, string key)
    {
        var text = this.FindControl<TextBlock>(control);
        if (text is not null)
        {
            text.Text = Lang.Get(key);
            return;
        }

        var button = this.FindControl<Button>(control);
        if (button is not null)
        {
            button.Content = Lang.Get(key);
        }
    }
}
