using Avalonia;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FocusPlus.Application;
using FocusPlus.Application.ViewModels;

namespace FocusPlus.Desktop.Views;

public partial class DevicesView : UserControl
{
    private readonly DevicesViewModel _vm;
    private readonly ListBox _list;
    private readonly Border _drawer;
    private readonly Border _loading;
    private readonly StackPanel _empty;
    private readonly TextBox _nameBox;
    private readonly TextBox _ipBox;
    private DeviceCardVm? _lastCard;
    private bool _suppressSelection;
    private bool _detached;

    public DevicesView()
    {
        AvaloniaXamlLoader.Load(this);
        _vm = new DevicesViewModel(App.Host, App.Messenger);
        DataContext = _vm;

        _list = this.FindControl<ListBox>("DeviceList")!;
        _drawer = this.FindControl<Border>("Drawer")!;
        _loading = this.FindControl<Border>("LoadingPanel")!;
        _empty = this.FindControl<StackPanel>("EmptyPanel")!;
        _nameBox = this.FindControl<TextBox>("NameBox")!;
        _ipBox = this.FindControl<TextBox>("IpBox")!;

        _vm.Devices.CollectionChanged += OnDevicesChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        App.LanguageChanged += ApplyLanguage;
        DetachedFromVisualTree += OnDetached;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        ApplyLanguage();
        SetLoading(true);
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _suppressSelection = true;
        try
        {
            await _vm.RefreshAsync();
        }
        catch (Exception ex)
        {
            App.Host.Logger.Error("Terminal workspace load failed.", ex);
            App.Messenger.Notify(Lang.Get("ErrorLoad"), UiSeverity.Error);
        }
        finally
        {
            _suppressSelection = false;
            SetLoading(false);
        }
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        _suppressSelection = true;
        try
        {
            await _vm.RefreshAsync();
        }
        catch (Exception ex)
        {
            App.Host.Logger.Error("Terminal refresh failed.", ex);
            App.Messenger.Notify(Lang.Get("ErrorLoad"), UiSeverity.Error);
        }
        finally
        {
            _suppressSelection = false;
            UpdateEmptyState();
        }
    }

    private void DeviceList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _detached)
        {
            return;
        }

        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not DeviceCardVm card)
        {
            return;
        }

        _lastCard = card;
        _vm.OpenDeviceCommand.Execute(card);
        FocusDrawer();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DevicesViewModel.IsDrawerOpen) && !_vm.IsDrawerOpen)
        {
            RestoreListFocus();
        }
    }

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_vm.IsDrawerOpen)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            _vm.CloseDrawerCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (DrawerFocusTrap.TryHandleTab(_drawer, TopLevel.GetTopLevel(this), e))
        {
            e.Handled = true;
        }
    }

    private void FocusDrawer() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm.IsDrawerOpen)
            {
                (_vm.IsNewDevice ? _nameBox : _ipBox).Focus();
            }
        }, DispatcherPriority.Input);

    private void RestoreListFocus() =>
        Dispatcher.UIThread.Post(() =>
        {
            _suppressSelection = true;
            try
            {
                if (_lastCard is not null && _vm.Devices.Contains(_lastCard))
                {
                    _list.SelectedItem = _lastCard;
                }
            }
            finally
            {
                _suppressSelection = false;
            }

            _list.Focus();
        }, DispatcherPriority.Input);

    private void SetLoading(bool loading)
    {
        _loading.IsVisible = loading;
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        _empty.IsVisible = !_loading.IsVisible && _vm.Devices.Count == 0;
    }

    private void ApplyLanguage()
    {
        FlowDirection = App.Direction;
        _drawer.HorizontalAlignment = App.IsArabic ? HorizontalAlignment.Left : HorizontalAlignment.Right;

        Set("HeaderText", "TerminalConsole");
        Set("ScanButton", "ScanLan");
        Set("RefreshButton", "Refresh");
        Set("AddButton", "AddTerminal");
        Set("ListTitle", "Terminals");
        Set("ScanTitle", "ScanLan");
        Set("NameLabel", "Name");
        Set("IpLabel", "IpAddress");
        Set("PortLabel", "Port");
        Set("KeyLabel", "CommKey");
        Set("MaskLabel", "Mask");
        Set("GatewayLabel", "Gateway");
        Set("NoteLabel", "Note");
        Set("SaveButton", "Save");
        Set("PullButton", "PullSelected");
        Set("SyncButton", "SyncSelected");
        Set("RebootButton", "Reboot");
        Set("UsersButton", "Users");
        Set("DumpButton", "TerminalConsole");
        Set("CloseButton", "Close");
        Set("EmptyTitle", "EmptyDevices");
        Set("EmptyHint", "EmptyDevicesHint");
        Set("EmptyAction", "ScanLan");
        Set("LoadingText", "Loading");
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

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        _vm.Devices.CollectionChanged -= OnDevicesChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        App.LanguageChanged -= ApplyLanguage;
    }
}
