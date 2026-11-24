using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FocusPlus.Application.ViewModels;

namespace FocusPlus.Desktop.Views;

public partial class SettingsView : UserControl
{
    private readonly SettingsViewModel _vm;

    public SettingsView()
    {
        AvaloniaXamlLoader.Load(this);
        _vm = new SettingsViewModel(App.Host, App.Messenger);
        DataContext = _vm;
        _ = _vm.RefreshAsync();
        App.LanguageChanged += ApplyLanguage;
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        FlowDirection = App.IsArabic ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
        Set("GeneralTitle", "General");
        Set("CompanyLabel", "About");
        Set("ProductLabel", "About");
        Set("DbLabel", "Database");
        Set("ShiftsTitle", "Shifts");
        Set("ServicesTitle", "Services");
        Set("BackupTitle", "Backup");
        Set("OperatorsTitle", "Operators");
        Set("ArabicButton", "العربية");
        Set("FrenchButton", "Français");
        Set("ThemeButton", "Theme");
        Set("NewRuleButton", "AddTerminal");
        Set("BackupButton", "Backup");
    }

    private void Set(string control, string text)
    {
        var label = this.FindControl<TextBlock>(control);
        if (label is not null)
        {
            label.Text = Lang.Get(text);
            return;
        }

        var button = this.FindControl<Button>(control);
        if (button is not null)
        {
            button.Content = text is "العربية" or "Français" ? text : Lang.Get(text);
        }
    }
}
