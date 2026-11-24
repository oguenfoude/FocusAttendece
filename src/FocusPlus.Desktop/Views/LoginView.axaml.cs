using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FocusPlus.Application.ViewModels;

namespace FocusPlus.Desktop.Views;

public partial class LoginView : UserControl
{
    private readonly LoginViewModel _vm;

    public LoginView()
    {
        AvaloniaXamlLoader.Load(this);
        _vm = new LoginViewModel(App.Host);
        DataContext = _vm;
        _vm.SignedIn += OnSignedIn;
        _vm.LanguageToggled += OnLanguageToggled;
        _vm.ThemeToggled += OnThemeToggled;
        App.LanguageChanged += ApplyLanguage;
        ApplyLanguage();
    }

    private void OnSignedIn() => App.SetContent(new ShellView());

    private void OnLanguageToggled()
    {
        App.SetLanguage(!App.IsArabic);
        _vm.RefreshHealth();
    }

    private void OnThemeToggled() { /* the shipped design system uses the OPGI light palette */ }

    private void ApplyLanguage()
    {
        FlowDirection = App.IsArabic ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
        Set("BrandTitle", "FocusPlus");
        Set("BrandSub", "BrandSubtitle");
        Set("BrandMission", "AppTitle");
        Set("WelcomeText", "Login");
        Set("WelcomeSub", "BrandSubtitle");
        Set("UserLabel", "Username");
        Set("PassLabel", "Password");
        Set("SignInText", "SignIn");
        Set("LangText", "Language");
        Set("ThemeText", App.IsArabic ? "Dark" : "Sombre");
    }

    private void Set(string control, string key)
    {
        var text = this.FindControl<TextBlock>(control);
        if (text is not null)
        {
            text.Text = Lang.Get(key);
        }
    }
}
