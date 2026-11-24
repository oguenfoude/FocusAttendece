using System.Collections.ObjectModel;
using FocusPlus.Application.Mvvm;
using FocusPlus.Application.Services;
using FocusPlus.Domain.Entities;
using FocusPlus.Hardware;

namespace FocusPlus.Application.ViewModels;

public sealed class DiscoveredDeviceVm
{
    public DiscoveredDeviceVm(DiscoveredDevice device, bool alreadyRegistered, bool arabic)
    {
        Device = device;
        AlreadyRegistered = alreadyRegistered;
        IpAddress = $"{device.IpAddress}:{device.Port}";
        Model = device.DisplayModel;
        Serial = device.SerialNumber ?? "—";
        Users = device.Snapshot?.UserCount.ToString() ?? "—";
        Punches = device.Snapshot?.PunchCount.ToString() ?? "—";
        Firmware = device.Snapshot?.FirmwareVersion ?? "—";
        Mac = device.Snapshot?.MacAddress ?? "—";
        Platform = device.Snapshot?.Platform ?? "—";
        Biometrics = device.Snapshot is { } s ? $"{s.FingerCount} FP / {s.FaceCount} face" : "—";
        Clock = device.Snapshot?.DeviceClock?.ToString("dd/MM HH:mm") ?? "—";
        StateText = alreadyRegistered
            ? (arabic ? "مسجل مسبقاً" : "Déjà enregistré")
            : device.Status switch
            {
                DiscoveryStatus.Verified => arabic ? "جهاز ZKTeco موثّق" : "ZKTeco vérifié",
                DiscoveryStatus.CommKeyRequired => arabic ? "المنفذ مفتوح — مطلوب مفتاح" : "Port ouvert — CommKey requise",
                DiscoveryStatus.PortOpenUnverified => arabic ? "المنفذ مفتوح — غير موثّق" : "Port ouvert — non vérifié",
                _ => arabic ? "غير متاح" : "Injoignable"
            };
    }

    public DiscoveredDevice Device { get; }
    public string IpAddress { get; }
    public string Model { get; }
    public string Serial { get; }
    public string Users { get; }
    public string Punches { get; }
    public string Firmware { get; }
    public string Mac { get; }
    public string Platform { get; }
    public string Biometrics { get; }
    public string Clock { get; }
    public string StateText { get; }
    public bool AlreadyRegistered { get; set; }

    /// <summary>A TCP-open endpoint can be registered even when the ZK handshake needs a CommKey.</summary>
    public bool CanAdd => (Device.PortOpen || Device.Responded || Device.Snapshot is not null) && !AlreadyRegistered;
}

/// <summary>Devices console: LAN discovery, SDK drawer, live snapshots and the IP reboot pipeline.</summary>
public sealed class DevicesViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IUiMessenger _ui;

    private DeviceCardVm? _selectedDevice;
    private DiscoveredDeviceVm? _selectedScanResult;
    private DeviceUserRowVm? _selectedDeviceUser;
    private bool _isBusy;
    private bool _isDrawerOpen;
    private bool _isNewDevice;
    private string _editName = string.Empty;
    private string _editIp = string.Empty;
    private int _editPort = 4370;
    private string _editMask = "255.255.255.0";
    private string _editGateway = string.Empty;
    private int _editCommKey;
    private string _editNote = string.Empty;
    private string _statusMessage = string.Empty;
    private string _networkLog = string.Empty;
    private string _infoDump = string.Empty;
    private bool _isScanning;
    private int _scanProgress;
    private int _scanTotal;
    private int _scanFound;

    public DevicesViewModel(AppHost host, IUiMessenger ui)
    {
        _host = host;
        _ui = ui;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
        AddByIpCommand = new RelayCommand(OpenNew);
        OpenDeviceCommand = new RelayCommand<DeviceCardVm>(vm => _ = OpenDeviceAsync(vm));
        CloseDrawerCommand = new RelayCommand(CloseDrawer);
        SaveDeviceCommand = new AsyncRelayCommand(SaveDeviceAsync, () => !IsBusy);
        ApplyNetworkCommand = new AsyncRelayCommand(ApplyNetworkAsync, () => !IsBusy && !IsNewDevice);
        PullSelectedCommand = new AsyncRelayCommand(PullSelectedAsync, () => !IsBusy && SelectedDevice is not null);
        SyncSelectedCommand = new AsyncRelayCommand(SyncSelectedAsync, () => !IsBusy && SelectedDevice is not null);
        RebootCommand = new AsyncRelayCommand(RebootAsync, () => !IsBusy && SelectedDevice is not null);
        PowerOffCommand = new AsyncRelayCommand(PowerOffAsync, () => !IsBusy && SelectedDevice is not null);
        ClearAdminsCommand = new AsyncRelayCommand(ClearAdminsAsync, () => !IsBusy && SelectedDevice is not null);
        BeepCommand = new AsyncRelayCommand(BeepAsync, () => !IsBusy && SelectedDevice is not null);
        ReadUsersCommand = new AsyncRelayCommand(ReadUsersAsync, () => !IsBusy && SelectedDevice is not null);
        DumpInfoCommand = new AsyncRelayCommand(DumpInfoAsync, () => !IsBusy && SelectedDevice is not null);
        ImportAllUsersCommand = new AsyncRelayCommand(ImportAllUsersAsync, () => !IsBusy && SelectedDevice is not null);
        ImportSelectedUserCommand = new AsyncRelayCommand(ImportSelectedUserAsync, () => !IsBusy && SelectedDeviceUser is not null);
        AddDiscoveredCommand = new AsyncRelayCommand(AddDiscoveredAsync, () => !IsBusy && SelectedScanResult?.CanAdd == true);
        PullAllCommand = new AsyncRelayCommand(PullAllAsync, () => !IsBusy);
        SyncAllCommand = new AsyncRelayCommand(SyncAllAsync, () => !IsBusy);
        ToggleEnabledCommand = new AsyncRelayCommand(ToggleEnabledAsync, () => !IsBusy && SelectedDevice is not null);
        ArchiveCommand = new AsyncRelayCommand(ArchiveAsync, () => !IsBusy && SelectedDevice is not null);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => !IsBusy && SelectedDevice is not null);
    }

    public ObservableCollection<DeviceCardVm> Devices { get; } = [];

    public ObservableCollection<DiscoveredDeviceVm> ScanResults { get; } = [];

    public ObservableCollection<DeviceUserRowVm> DeviceUsers { get; } = [];

    public ObservableCollection<string> NetworkLogLines { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand AddByIpCommand { get; }
    public RelayCommand<DeviceCardVm> OpenDeviceCommand { get; }
    public RelayCommand CloseDrawerCommand { get; }
    public AsyncRelayCommand SaveDeviceCommand { get; }
    public AsyncRelayCommand ApplyNetworkCommand { get; }
    public AsyncRelayCommand PullSelectedCommand { get; }
    public AsyncRelayCommand SyncSelectedCommand { get; }
    public AsyncRelayCommand RebootCommand { get; }
    public AsyncRelayCommand PowerOffCommand { get; }
    public AsyncRelayCommand ClearAdminsCommand { get; }
    public AsyncRelayCommand BeepCommand { get; }
    public AsyncRelayCommand ReadUsersCommand { get; }
    public AsyncRelayCommand DumpInfoCommand { get; }
    public AsyncRelayCommand ImportAllUsersCommand { get; }
    public AsyncRelayCommand ImportSelectedUserCommand { get; }
    public AsyncRelayCommand AddDiscoveredCommand { get; }
    public AsyncRelayCommand PullAllCommand { get; }
    public AsyncRelayCommand SyncAllCommand { get; }
    public AsyncRelayCommand ToggleEnabledCommand { get; }
    public AsyncRelayCommand ArchiveCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }

    public DeviceCardVm? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetProperty(ref _selectedDevice, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelection), nameof(SelectedDeviceTitle), nameof(SelectedDeviceSubtitle));
            RaiseCommands();
        }
    }

    public DiscoveredDeviceVm? SelectedScanResult
    {
        get => _selectedScanResult;
        set
        {
            if (SetProperty(ref _selectedScanResult, value))
            {
                AddDiscoveredCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public DeviceUserRowVm? SelectedDeviceUser
    {
        get => _selectedDeviceUser;
        set
        {
            if (SetProperty(ref _selectedDeviceUser, value))
            {
                ImportSelectedUserCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelection => SelectedDevice is not null;

    public string SelectedDeviceTitle => SelectedDevice?.Name ?? (IsArabic ? "اختر جهازاً" : "Sélectionnez un terminal");

    public string SelectedDeviceSubtitle => SelectedDevice?.Endpoint ?? "—";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommands();
            }
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    public int ScanProgress { get => _scanProgress; private set => SetProperty(ref _scanProgress, value); }

    public int ScanTotal { get => _scanTotal; private set => SetProperty(ref _scanTotal, value); }

    public int ScanFound { get => _scanFound; private set => SetProperty(ref _scanFound, value); }

    public bool IsDrawerOpen
    {
        get => _isDrawerOpen;
        private set => SetProperty(ref _isDrawerOpen, value);
    }

    public bool IsNewDevice
    {
        get => _isNewDevice;
        private set
        {
            if (SetProperty(ref _isNewDevice, value))
            {
                OnPropertyChanged(nameof(DrawerTitle), nameof(IsEditingExisting), nameof(CanEditName));
                ApplyNetworkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsEditingExisting => !IsNewDevice;

    public bool CanEditName => IsNewDevice;

    public string DrawerTitle => IsNewDevice
        ? (IsArabic ? "إضافة جهاز ZKTeco" : "Ajouter un terminal ZKTeco")
        : (IsArabic ? "لوحة تحكم الجهاز" : "Console du terminal");

    public string EditName { get => _editName; set => SetProperty(ref _editName, value); }

    public string EditIp { get => _editIp; set => SetProperty(ref _editIp, value); }

    public int EditPort { get => _editPort; set => SetProperty(ref _editPort, value); }

    public string EditMask { get => _editMask; set => SetProperty(ref _editMask, value); }

    public string EditGateway { get => _editGateway; set => SetProperty(ref _editGateway, value); }

    public int EditCommKey { get => _editCommKey; set => SetProperty(ref _editCommKey, value); }

    public string EditNote { get => _editNote; set => SetProperty(ref _editNote, value); }

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public string NetworkLog { get => _networkLog; private set => SetProperty(ref _networkLog, value); }

    /// <summary>Raw SDK diagnostic dump of the selected terminal.</summary>
    public string InfoDump
    {
        get => _infoDump;
        private set
        {
            if (SetProperty(ref _infoDump, value))
            {
                OnPropertyChanged(nameof(HasInfoDump));
            }
        }
    }

    public bool HasInfoDump => !string.IsNullOrWhiteSpace(_infoDump);

    public bool IsArabic => _host.Session.IsArabic;

    public AppHost Host => _host;

    public string SdkStatus => _host.Hardware.EngineSummary;

    public event Action? DataChanged;

    public async Task RefreshAsync()
    {
        try
        {
            var arabic = _host.Session.IsArabic;
            var devices = await Task.Run(() => _host.Devices.GetDevices()).ConfigureAwait(true);
            var selectedId = SelectedDevice?.Model.Id;

            Devices.Clear();
            foreach (var device in devices)
            {
                Devices.Add(new DeviceCardVm(device, _host.Devices.GetStatus(device.Id), arabic));
            }

            SelectedDevice = Devices.FirstOrDefault(d => d.Model.Id == selectedId) ?? Devices.FirstOrDefault();
            StatusMessage = arabic
                ? $"{devices.Count} جهاز مسجل"
                : $"{devices.Count} terminal(aux) enregistré(s)";
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Device refresh failed.", ex);
        }
    }

    /// <summary>Updates statuses only (cheap, used by the periodic shell timer).</summary>
    public void RefreshStatuses()
    {
        foreach (var card in Devices)
        {
            card.Status = _host.Devices.GetStatus(card.Model.Id);
            card.Model.LastSeenUtc = _host.Devices.Get(card.Model.Id)?.LastSeenUtc;
        }
    }

    public async Task ScanAsync()
    {
        IsBusy = true;
        IsScanning = true;
        ScanResults.Clear();
        ScanProgress = 0;
        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                ScanProgress = p.Scanned;
                ScanTotal = p.Total;
                ScanFound = p.Found;
            });

            var found = await _host.Devices.ScanLanAsync(_host.Settings.GetScanTimeoutMs(), progress).ConfigureAwait(true);
            ApplyScanResults(found);
            _ui.Notify(
                IsArabic ? $"تم العثور على {found.Count} جهاز على المنفذ 4370." : $"{found.Count} terminal(aux) détecté(s) sur le port 4370.",
                found.Count > 0 ? UiSeverity.Success : UiSeverity.Info);
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Device scan failed.", ex);
            _ui.Notify(Labels.Error(ErrorCodes.Hardware, IsArabic, ex.Message), UiSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            IsScanning = false;
        }
    }

    public void ApplyScanResults(IReadOnlyList<DiscoveredDevice> found)
    {
        var registered = _host.Devices.GetDevices().Select(d => d.SerialNumber ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ScanResults.Clear();
        foreach (var device in found)
        {
            var already = device.SerialNumber is { } serial && registered.Contains(serial);
            ScanResults.Add(new DiscoveredDeviceVm(device, already, IsArabic));
        }

        SelectedScanResult = ScanResults.FirstOrDefault(s => s.CanAdd) ?? ScanResults.FirstOrDefault();
    }

    private void OpenNew()
    {
        SelectedDevice = null;
        IsNewDevice = true;
        EditName = string.Empty;
        EditIp = string.Empty;
        EditPort = 4370;
        EditMask = "255.255.255.0";
        EditGateway = string.Empty;
        EditCommKey = 0;
        EditNote = string.Empty;
        DeviceUsers.Clear();
        NetworkLogLines.Clear();
        NetworkLog = string.Empty;
        InfoDump = string.Empty;
        IsDrawerOpen = true;
    }

    private async Task OpenDeviceAsync(DeviceCardVm? card)
    {
        if (card is null)
        {
            return;
        }

        SelectedDevice = card;
        IsNewDevice = false;
        EditName = card.Model.Name;
        EditIp = card.Model.IpAddress;
        EditPort = card.Model.Port;
        EditCommKey = card.Model.CommKey;
        EditNote = card.Model.Note ?? string.Empty;
        EditMask = "255.255.255.0";
        EditGateway = string.Empty;
        DeviceUsers.Clear();
        NetworkLogLines.Clear();
        NetworkLog = string.Empty;
        IsDrawerOpen = true;

        if (card.IsOnline)
        {
            await ReadUsersAsync().ConfigureAwait(true);
        }
    }

    private void CloseDrawer() => IsDrawerOpen = false;

    private async Task SaveDeviceAsync()
    {
        IsBusy = true;
        try
        {
            if (IsNewDevice)
            {
                var (result, device) = await _host.Devices
                    .AddByIpAsync(EditName, EditIp, EditPort, EditCommKey, _host.Session.Actor)
                    .ConfigureAwait(true);

                if (!result.Success || device is null)
                {
                    _ui.Notify(Labels.Error(result.ErrorCode, IsArabic, result.Detail), UiSeverity.Error);
                    return;
                }

                _ui.Notify(IsArabic ? "تمت إضافة الجهاز بنجاح." : "Terminal ajouté avec succès.", UiSeverity.Success);
                IsDrawerOpen = false;
                await RefreshAsync().ConfigureAwait(true);
                DataChanged?.Invoke();
                return;
            }

            if (SelectedDevice is null)
            {
                return;
            }

            var model = SelectedDevice.Model;
            model.Name = string.IsNullOrWhiteSpace(EditName) ? model.Name : EditName.Trim();
            model.CommKey = EditCommKey;
            model.Note = string.IsNullOrWhiteSpace(EditNote) ? null : EditNote.Trim();
            _host.DeviceRepo.Upsert(model);
            _host.Audit.Add(new AuditLogEntry
            {
                Action = "device.updated",
                Actor = _host.Session.Actor,
                EntityType = "Device",
                EntityId = model.Id,
                Details = model.Name
            });

            _ui.Notify(IsArabic ? "تم حفظ معطيات الجهاز." : "Terminal mis à jour.", UiSeverity.Success);
            await RefreshAsync().ConfigureAwait(true);
            DataChanged?.Invoke();

            // Apply a new CommKey immediately: retry the handshake and pull if it succeeds.
            var status = await _host.Devices.ReconnectAsync(model.Id).ConfigureAwait(true);
            if (status.IsOnline)
            {
                _ui.Notify(
                    IsArabic ? "تم الاتصال بالجهاز بعد الحفظ." : "Terminal reconnected after save.",
                    UiSeverity.Success);
            }
            else
            {
                _ui.Notify(
                    IsArabic ? $"الحفظ تم. الاتصال: {status.Error}" : $"Saved. Connection: {status.Error}",
                    UiSeverity.Warning);
            }

            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Device save failed.", ex);
            _ui.Notify(Labels.Error(null, IsArabic), UiSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyNetworkAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        IsBusy = true;
        NetworkLogLines.Clear();
        try
        {
            var progress = new Progress<NetworkUpdateProgress>(p =>
            {
                var line = Labels.NetworkStage(p.Stage, IsArabic)
                           + (string.IsNullOrWhiteSpace(p.Detail) ? string.Empty : $" — {p.Detail}");
                NetworkLogLines.Add($"{DateTime.Now:HH:mm:ss}  {line}");
                NetworkLog = string.Join(Environment.NewLine, NetworkLogLines);
            });

            var result = await _host.Devices
                .UpdateNetworkAsync(SelectedDevice.Model.Id, EditIp, EditMask, EditGateway, EditPort, _host.Session.Actor, progress)
                .ConfigureAwait(true);

            NetworkLogLines.Add($"{DateTime.Now:HH:mm:ss}  {result.Message}");
            NetworkLog = string.Join(Environment.NewLine, NetworkLogLines);

            _ui.Notify(result.Message, result.Success ? UiSeverity.Success : UiSeverity.Error);
            await RefreshAsync().ConfigureAwait(true);
            DataChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Network update failed.", ex);
            _ui.Notify(Labels.Error(ErrorCodes.Hardware, IsArabic, ex.Message), UiSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PullSelectedAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _host.Devices.PullPunchesAsync(SelectedDevice.Model.Id, _host.Session.Actor).ConfigureAwait(true);
            _ui.Notify(result.Message, result.Success ? UiSeverity.Success : UiSeverity.Error);
            await RefreshAsync().ConfigureAwait(true);
            DataChanged?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SyncSelectedAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _host.Devices.SyncClockAsync(SelectedDevice.Model.Id, _host.Session.Actor).ConfigureAwait(true);
            _ui.Notify(
                result.Success ? (IsArabic ? "تمت مزامنة الساعة." : "Horloge synchronisée.") : Labels.Error(result.ErrorCode, IsArabic, result.Detail),
                result.Success ? UiSeverity.Success : UiSeverity.Error);
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RebootAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _host.Devices.RebootAsync(SelectedDevice.Model.Id, _host.Session.Actor).ConfigureAwait(true);
            _ui.Notify(result.Message, result.Success ? UiSeverity.Success : UiSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PowerOffAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _host.Devices.PowerOffAsync(SelectedDevice.Model.Id, _host.Session.Actor).ConfigureAwait(true);
            _ui.Notify(result.Message, result.Success ? UiSeverity.Success : UiSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ClearAdminsAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _host.Devices.ClearAdminsAsync(SelectedDevice.Model.Id, _host.Session.Actor).ConfigureAwait(true);
            _ui.Notify(
                result.Success
                    ? (IsArabic ? "تم إلغاء قفل المسؤولين على الجهاز." : "Verrou administrateur levé.")
                    : result.Message,
                result.Success ? UiSeverity.Success : UiSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task BeepAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        var result = await _host.Devices.TestVoiceAsync(SelectedDevice.Model.Id).ConfigureAwait(true);
        _ui.Notify(result.Message, result.Success ? UiSeverity.Info : UiSeverity.Error);
    }


    private async Task DumpInfoAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var model = SelectedDevice.Model;
            var endpoint = new ZkEndpoint(model.IpAddress, model.Port, model.CommKey);
            var lines = await _host.Hardware.DumpSdkInfoAsync(endpoint).ConfigureAwait(true);
            InfoDump = string.Join(Environment.NewLine, lines);
            StatusMessage = IsArabic
                ? "تمت قراءة كل المعلومات المتاحة من الجهاز عبر SDK."
                : "Every value the terminal exposes has been read through the SDK.";
            _ui.Notify(
                IsArabic ? "تم الفحص الكامل للجهاز." : "Full terminal dump completed.",
                UiSeverity.Info);
        }
        catch (Exception ex)
        {
            _host.Logger.Error("SDK dump failed.", ex);
            _ui.Notify(Labels.Error(null, IsArabic), UiSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReadUsersAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            DeviceUsers.Clear();
            var result = await _host.Devices.ReadUsersAsync(SelectedDevice.Model.Id).ConfigureAwait(true);
            if (!result.Success || result.Value is null)
            {
                _ui.Notify(Labels.Error(ErrorCodes.Hardware, IsArabic, result.Message), UiSeverity.Warning);
                return;
            }

            var arabic = _host.Session.IsArabic;
            foreach (var user in result.Value.OrderBy(u => u.UserId, StringComparer.Ordinal))
            {
                var linked = _host.EmployeeRepo.GetByPin(user.UserId);
                DeviceUsers.Add(new DeviceUserRowVm(user, linked is null ? null : (arabic ? linked.DisplayNameAr : linked.DisplayNameFr), arabic));
            }

            StatusMessage = arabic
                ? $"{DeviceUsers.Count} مستخدم داخل ذاكرة الجهاز"
                : $"{DeviceUsers.Count} utilisateur(s) dans la mémoire du terminal";
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Read device users failed.", ex);
            _ui.Notify(Labels.Error(null, IsArabic), UiSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportAllUsersAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var (result, import) = await _host.EmployeeDirectory
                .ImportFromDeviceAsync(SelectedDevice.Model.Id, _host.Session.Actor)
                .ConfigureAwait(true);

            if (!result.Success || import is null)
            {
                _ui.Notify(Labels.Error(result.ErrorCode, IsArabic, result.Detail), UiSeverity.Error);
                return;
            }

            _ui.Notify(
                IsArabic
                    ? $"تم استيراد {import.Created} موظف جديد وتحديث {import.Updated}."
                    : $"{import.Created} employé(s) importé(s), {import.Updated} mis à jour.",
                UiSeverity.Success);
            DataChanged?.Invoke();
            await ReadUsersAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportSelectedUserAsync()
    {
        if (SelectedDevice is null || SelectedDeviceUser is null)
        {
            return;
        }

        var import = _host.EmployeeDirectory.ImportUsers(SelectedDevice.Model, [SelectedDeviceUser.User], _host.Session.Actor);
        _ui.Notify(
            IsArabic ? $"تم استيراد المستخدم {SelectedDeviceUser.Pin}." : $"Utilisateur {SelectedDeviceUser.Pin} importé.",
            UiSeverity.Success);
        _host.Logger.Info($"Device user import: created={import.Created}, updated={import.Updated}.");
        DataChanged?.Invoke();
        await ReadUsersAsync().ConfigureAwait(true);
    }

    private async Task AddDiscoveredAsync()
    {
        if (SelectedScanResult is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var (result, device) = _host.Devices.AddFromScan(
                SelectedScanResult.Device,
                SelectedScanResult.Device.DisplayModel,
                0,
                _host.Session.Actor);

            if (!result.Success || device is null)
            {
                _ui.Notify(Labels.Error(result.ErrorCode, IsArabic, result.Detail), UiSeverity.Error);
                return;
            }

            _ui.Notify(
                IsArabic ? $"تمت إضافة {device.Name} بنجاح." : $"{device.Name} ajouté avec succès.",
                UiSeverity.Success);
            SelectedScanResult.AlreadyRegistered = true;
            await RefreshAsync().ConfigureAwait(true);
            DataChanged?.Invoke();
        }
        finally
        {
            IsBusy = false;
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
            DataChanged?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SyncAllAsync()
    {
        IsBusy = true;
        try
        {
            var count = await _host.Devices.SyncAllClocksAsync(_host.Session.Actor).ConfigureAwait(true);
            _ui.Notify(
                IsArabic ? $"تمت مزامنة {count} جهاز." : $"{count} terminal(aux) synchronisé(s).",
                UiSeverity.Success);
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ToggleEnabledAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        var enabled = !SelectedDevice.Model.IsEnabled;
        _host.Devices.SetEnabled(SelectedDevice.Model.Id, enabled, _host.Session.Actor);
        _ui.Notify(
            enabled
                ? (IsArabic ? "تم تفعيل المراقبة التلقائية." : "Surveillance automatique activée.")
                : (IsArabic ? "تم تعطيل المراقبة التلقائية." : "Surveillance automatique désactivée."),
            UiSeverity.Info);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task ArchiveAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        var result = _host.Devices.Archive(SelectedDevice.Model.Id, "archived from console", _host.Session.Actor);
        if (result.Success)
        {
            IsDrawerOpen = false;
            _ui.Notify(IsArabic ? "تم أرشفة الجهاز." : "Terminal archivé.", UiSeverity.Warning);
            await RefreshAsync().ConfigureAwait(true);
            DataChanged?.Invoke();
        }
    }

    private async Task RestoreAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        var result = _host.Devices.Restore(SelectedDevice.Model.Id, _host.Session.Actor);
        if (result.Success)
        {
            _ui.Notify(IsArabic ? "تمت استعادة الجهاز." : "Terminal restauré.", UiSeverity.Success);
            await RefreshAsync().ConfigureAwait(true);
            DataChanged?.Invoke();
        }
    }

    private void RaiseCommands()
    {
        PullSelectedCommand.RaiseCanExecuteChanged();
        SyncSelectedCommand.RaiseCanExecuteChanged();
        RebootCommand.RaiseCanExecuteChanged();
        PowerOffCommand.RaiseCanExecuteChanged();
        ClearAdminsCommand.RaiseCanExecuteChanged();
        BeepCommand.RaiseCanExecuteChanged();
        ReadUsersCommand.RaiseCanExecuteChanged();
        ImportAllUsersCommand.RaiseCanExecuteChanged();
        ToggleEnabledCommand.RaiseCanExecuteChanged();
        ArchiveCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        SaveDeviceCommand.RaiseCanExecuteChanged();
    }
}
