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
        _vm.AppearanceChanged += OnAppearanceChanged;
        _ = _vm.RefreshAsync();
        App.LanguageChanged += ApplyLanguage;
        ApplyLanguage();
    }

    private void OnAppearanceChanged() => App.SetLanguage(App.Host.Session.IsArabic);

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
        Set("NewRuleButton", "Shifts");
        Set("BackupButton", "Backup");
        Set("BackupDailyTimeLabel", "Hours");
        Set("BackupKeepDaysLabel", "WorkDays");
        Set("BackupKeepMonthlyLabel", "Month");
        Set("SaveBackupPolicyButton", "Save");
        Set("RuleCodeLabel", "Serial");
        Set("RuleArLabel", "Name");
        Set("RuleFrLabel", "Name");
        Set("MorningStartLabel", "Date");
        Set("EveningEndLabel", "Date");
        Set("LunchStartLabel", "Date");
        Set("LunchEndLabel", "Date");
        Set("MorningGraceLabel", "Status");
        Set("AfternoonGraceLabel", "Status");
        Set("EarlyLabel", "Status");
        Set("LunchDeductionLabel", "Hours");
        Set("AntiBounceLabel", "Status");
        Set("TargetLabel", "Hours");
        Set("SaveRuleButton", "Save");
        Set("CloseRuleButton", "Close");
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
