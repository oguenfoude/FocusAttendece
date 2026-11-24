using System.Collections.ObjectModel;
using FocusPlus.Application.Mvvm;
using FocusPlus.Application.Services;
using FocusPlus.Hardware;

namespace FocusPlus.Application.ViewModels;

/// <summary>Executive dashboard: six KPI cards, quick hardware actions and the live feed.</summary>
public sealed class DashboardViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IUiMessenger _ui;

    private bool _isBusy;
    private string _lastUpdated = "—";
    private int _activeEmployees;
    private int _devicesOnline;
    private int _devicesTotal;
    private int _presentToday;
    private int _lateToday;
    private int _onLunchNow;
    private int _absentToday;
    private int _todayScans;
    private string _regularHours = "00:00";
    private string _extraHours = "00:00";
    private string _scanStatus = string.Empty;

    public DashboardViewModel(AppHost host, IUiMessenger ui)
    {
        _host = host;
        _ui = ui;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        PullAllCommand = new AsyncRelayCommand(PullAllAsync, () => !IsBusy && DevicesTotal > 0);
        SyncClocksCommand = new AsyncRelayCommand(SyncClocksAsync, () => !IsBusy && DevicesTotal > 0);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
    }

    public ObservableCollection<DeviceCardVm> Devices { get; } = [];

    public ObservableCollection<DashboardActivityVm> Activity { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand PullAllCommand { get; }

    public AsyncRelayCommand SyncClocksCommand { get; }

    public AsyncRelayCommand ScanCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                PullAllCommand.RaiseCanExecuteChanged();
                SyncClocksCommand.RaiseCanExecuteChanged();
                ScanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int ActiveEmployees { get => _activeEmployees; private set => SetProperty(ref _activeEmployees, value); }

    public int DevicesOnline { get => _devicesOnline; private set => SetProperty(ref _devicesOnline, value); }

    public int DevicesTotal { get => _devicesTotal; private set => SetProperty(ref _devicesTotal, value); }

    public int PresentToday { get => _presentToday; private set => SetProperty(ref _presentToday, value); }

    public int LateToday { get => _lateToday; private set => SetProperty(ref _lateToday, value); }

    public int OnLunchNow { get => _onLunchNow; private set => SetProperty(ref _onLunchNow, value); }

    public int AbsentToday { get => _absentToday; private set => SetProperty(ref _absentToday, value); }

    public int TodayScans { get => _todayScans; private set => SetProperty(ref _todayScans, value); }

    public string RegularHours { get => _regularHours; private set => SetProperty(ref _regularHours, value); }

    public string ExtraHours { get => _extraHours; private set => SetProperty(ref _extraHours, value); }

    public string DevicesRatioText => $"{DevicesOnline}/{DevicesTotal}";

    public string LastUpdated { get => _lastUpdated; private set { if (SetProperty(ref _lastUpdated, value)) OnPropertyChanged(nameof(IsLoading)); } }

    public bool IsLoading => LastUpdated == "—";

    public string ScanStatus { get => _scanStatus; private set => SetProperty(ref _scanStatus, value); }

    public bool IsArabic => _host.Session.IsArabic;

    public event Action<IReadOnlyList<DiscoveredDevice>>? ScanCompleted;

    public async Task RefreshAsync()
    {
        try
        {
            var snapshot = await Task.Run(() => _host.Dashboard.Build()).ConfigureAwait(true);
            var arabic = _host.Session.IsArabic;

            ActiveEmployees = snapshot.ActiveEmployees;
            DevicesOnline = snapshot.DevicesOnline;
            DevicesTotal = snapshot.DevicesTotal;
            PresentToday = snapshot.PresentToday;
            LateToday = snapshot.LateToday;
            OnLunchNow = snapshot.OnLunchNow;
            AbsentToday = snapshot.AbsentToday;
            TodayScans = snapshot.TodayScans;
            RegularHours = Labels.Minutes(snapshot.RegularMinutesToday);
            ExtraHours = Labels.Minutes(snapshot.ExtraMinutesToday);
            OnPropertyChanged(nameof(DevicesRatioText));

            Activity.Clear();
            foreach (var row in snapshot.Activity.Take(60))
            {
                Activity.Add(new DashboardActivityVm(row, arabic));
            }

            Devices.Clear();
            foreach (var device in snapshot.Devices)
            {
                Devices.Add(new DeviceCardVm(device, _host.Devices.GetStatus(device.Id), arabic));
            }

            LastUpdated = DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Dashboard refresh failed.", ex);
            _ui.Notify(Labels.Error(null, _host.Session.IsArabic), UiSeverity.Error);
        }
        finally
        {
            if (IsLoading)
            {
                LastUpdated = DateTime.Now.ToString("HH:mm:ss");
            }
        }
    }

    private async Task PullAllAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _host.Devices.PullAllAsync(_host.Session.Actor).ConfigureAwait(true);
            _ui.Notify(result.Result.Message, result.Result.Success ? UiSeverity.Success : UiSeverity.Warning);
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SyncClocksAsync()
    {
        IsBusy = true;
        try
        {
            var count = await _host.Devices.SyncAllClocksAsync(_host.Session.Actor).ConfigureAwait(true);
            _ui.Notify(
                _host.Session.IsArabic ? $"تمت مزامنة ساعة {count} جهاز." : $"Horloge synchronisée sur {count} terminal(aux).",
                UiSeverity.Success);
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ScanAsync()
    {
        IsBusy = true;
        try
        {
            ScanStatus = _host.Session.IsArabic ? "جارٍ فحص الشبكة المحلية على المنفذ 4370…" : "Scan du réseau local sur le port 4370…";
            var progress = new Progress<ScanProgress>(p => ScanStatus = $"{p.Scanned}/{p.Total}  •  {p.Found}");
            var found = await _host.Devices.ScanLanAsync(_host.Settings.GetScanTimeoutMs(), progress).ConfigureAwait(true);
            ScanStatus = _host.Session.IsArabic
                ? $"تم العثور على {found.Count} جهاز"
                : $"{found.Count} terminal(s) trouvé(s)";
            _ui.Notify(ScanStatus, found.Count > 0 ? UiSeverity.Success : UiSeverity.Info);
            ScanCompleted?.Invoke(found);
        }
        catch (Exception ex)
        {
            _host.Logger.Error("LAN scan failed.", ex);
            _ui.Notify(Labels.Error(ErrorCodes.Hardware, _host.Session.IsArabic, ex.Message), UiSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
