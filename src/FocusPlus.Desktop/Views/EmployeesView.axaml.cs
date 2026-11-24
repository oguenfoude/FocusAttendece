using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FocusPlus.Application.ViewModels;

namespace FocusPlus.Desktop.Views;

public partial class EmployeesView : UserControl
{
    private readonly EmployeesViewModel _vm;

    public EmployeesView()
    {
        AvaloniaXamlLoader.Load(this);
        _vm = new EmployeesViewModel(App.Host, App.Messenger);
        DataContext = _vm;
        _ = _vm.RefreshAsync();
        App.LanguageChanged += ApplyLanguage;
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        FlowDirection = App.IsArabic ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
        Set("SearchBox", "Search");
        Set("NewButton", "AddTerminal");
        Set("RefreshButton", "Refresh");
        Set("ImportButton", "Users");
        Set("ClearButton", "Cancel");
        Set("ArNameLabel", "Name");
        Set("FrNameLabel", "Name");
        Set("MatriculeLabel", "Serial");
        Set("PinLabel", "Password");
        Set("DaysLabel", "Date");
        Set("SaveButton", "Save");
        Set("ArchiveButton", "Restore");
        Set("CloseButton", "Close");
        var archived = this.FindControl<CheckBox>("ArchivedCheck");
        if (archived is not null) archived.Content = Lang.Get("Status");
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
            return;
        }

        var box = this.FindControl<TextBox>(control);
        if (box is not null)
        {
            box.Watermark = Lang.Get(key);
        }
    }
}
