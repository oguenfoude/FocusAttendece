using FocusPlus.Application.Mvvm;
using FocusPlus.Domain.Entities;

namespace FocusPlus.Application.ViewModels;

public sealed class LoginViewModel : ObservableObject
{
    private readonly AppHost _host;
    private string _username = DefaultUsername;
    private string _password = string.Empty;
    private string? _errorText;
    private bool _isBusy;
    private bool _databaseHealthy;
    private string _databaseStatus = string.Empty;
    private bool _rememberUsername;
    private bool _isDark;

    private const string DefaultUsername = "admin";
    private const string RememberKey = "auth.rememberUsername";
    private const string LastUserKey = "auth.lastUsername";
    private const string ThemeKey = "ui.theme";

    public LoginViewModel(AppHost host)
    {
        _host = host;
        LoginCommand = new AsyncRelayCommand(LoginAsync, () => !IsBusy);
        ToggleLanguageCommand = new RelayCommand(ToggleLanguage);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        RefreshHealth();

        _rememberUsername = host.Preferences.Get(RememberKey) == "1";
        if (_rememberUsername && host.Preferences.Get(LastUserKey) is { Length: > 0 } saved)
        {
            _username = saved;
        }

        _isDark = string.Equals(host.Preferences.Get(ThemeKey), "Dark", StringComparison.OrdinalIgnoreCase);
    }

    public AsyncRelayCommand LoginCommand { get; }

    public RelayCommand ToggleLanguageCommand { get; }

    public RelayCommand ToggleThemeCommand { get; }

    public bool RememberUsername
    {
        get => _rememberUsername;
        set => SetProperty(ref _rememberUsername, value);
    }

    public bool IsDark
    {
        get => _isDark;
        private set => SetProperty(ref _isDark, value);
    }

    public string ThemeLabel => IsDark ? (IsArabic ? "داكن" : "Sombre") : (IsArabic ? "فاتح" : "Clair");

    public string BrandBranch
    {
        get
        {
            var sites = _host.Organization.GetSites();
            var site = sites.Count > 0 ? sites[0] : null;
            if (site is null)
            {
                return IsArabic ? "ديوان الترقية والتسيير العقاري — ولاية المسيلة" : "OPGI — Direction Générale de M'Sila";
            }

            return IsArabic ? site.NameAr : site.NameFr;
        }
    }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                LoginCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public bool DatabaseHealthy
    {
        get => _databaseHealthy;
        private set => SetProperty(ref _databaseHealthy, value);
    }

    public string DatabaseStatus
    {
        get => _databaseStatus;
        private set => SetProperty(ref _databaseStatus, value);
    }

    public bool IsArabic => _host.Session.IsArabic;

    public string LanguageLabel => IsArabic ? "Français" : "العربية";

    public event Action? SignedIn;

    public event Action? LanguageToggled;

    public event Action? ThemeToggled;

    private void ToggleTheme()
    {
        IsDark = !IsDark;
        _host.Settings.SetTheme(IsDark ? "Dark" : "Light");
        OnPropertyChanged(nameof(ThemeLabel));
        ThemeToggled?.Invoke();
    }

    public void RefreshHealth()
    {
        DatabaseHealthy = _host.Db.IsHealthy(out var message);
        DatabaseStatus = DatabaseHealthy
            ? (IsArabic ? "قاعدة البيانات SQLite جاهزة (WAL)" : "Base SQLite prête (WAL)")
            : message;
        OnPropertyChanged(nameof(IsArabic), nameof(LanguageLabel));
    }

    public void ToggleLanguage()
    {
        _host.Session.IsArabic = !_host.Session.IsArabic;
        _host.Settings.SetLanguage(_host.Session.IsArabic ? "ar" : "fr");
        ErrorText = null;
        OnPropertyChanged(nameof(IsArabic), nameof(LanguageLabel), nameof(BrandBranch), nameof(ThemeLabel));
        RefreshHealth();
        LanguageToggled?.Invoke();
    }

    private async Task LoginAsync()
    {
        ErrorText = null;
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorText = Labels.Error(ErrorCodes.Required, IsArabic);
            return;
        }

        IsBusy = true;
        try
        {
            var result = await Task.Run(() => _host.Auth.Login(Username.Trim(), Password)).ConfigureAwait(true);
            if (!result.Success || result.User is null)
            {
                ErrorText = Labels.Error(result.Error, IsArabic);
                return;
            }

            _host.Session.SignIn(result.User);
            _host.Preferences.Set(RememberKey, RememberUsername ? "1" : "0");
            _host.Preferences.Set(LastUserKey, RememberUsername ? Username.Trim() : string.Empty);
            Password = string.Empty;
            SignedIn?.Invoke();
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Login failed unexpectedly.", ex);
            ErrorText = Labels.Error(null, IsArabic);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
