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

public partial class EmployeesView : UserControl
{
    private readonly EmployeesViewModel _vm;
    private readonly ListBox _list;
    private readonly Border _drawer;
    private readonly Border _loading;
    private readonly StackPanel _empty;
    private readonly TextBox _arNameBox;
    private EmployeeRowVm? _lastRow;
    private bool _suppressSelection;
    private bool _detached;

    public EmployeesView()
    {
        AvaloniaXamlLoader.Load(this);
        _vm = new EmployeesViewModel(App.Host, App.Messenger);
        DataContext = _vm;

        _list = this.FindControl<ListBox>("EmployeeList")!;
        _drawer = this.FindControl<Border>("Drawer")!;
        _loading = this.FindControl<Border>("LoadingPanel")!;
        _empty = this.FindControl<StackPanel>("EmptyPanel")!;
        _arNameBox = this.FindControl<TextBox>("ArNameBox")!;

        _vm.Employees.CollectionChanged += OnRowsChanged;
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
        try
        {
            await _vm.RefreshAsync();
        }
        catch (Exception ex)
        {
            App.Host.Logger.Error("Employee workspace load failed.", ex);
            App.Messenger.Notify(Lang.Get("ErrorLoad"), UiSeverity.Error);
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void EmployeeList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _detached)
        {
            return;
        }

        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not EmployeeRowVm row)
        {
            return;
        }

        _lastRow = row;
        _vm.EditCommand.Execute(row);
        FocusDrawer();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EmployeesViewModel.IsDrawerOpen) && !_vm.IsDrawerOpen)
        {
            RestoreListFocus();
        }
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

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
                _arNameBox.Focus();
            }
        }, DispatcherPriority.Input);

    private void RestoreListFocus() =>
        Dispatcher.UIThread.Post(() =>
        {
            _suppressSelection = true;
            try
            {
                if (_lastRow is not null && _vm.Employees.Contains(_lastRow))
                {
                    _list.SelectedItem = _lastRow;
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
        _empty.IsVisible = !_loading.IsVisible && _vm.Employees.Count == 0;
    }

    private void ApplyLanguage()
    {
        FlowDirection = App.Direction;
        _drawer.HorizontalAlignment = App.IsArabic ? HorizontalAlignment.Left : HorizontalAlignment.Right;

        Set("SearchBox", "Search");
        Set("NewButton", "NewEmployee");
        Set("RefreshButton", "Refresh");
        Set("ImportButton", "ImportFromDevice");
        Set("ClearButton", "ClearFilters");
        Set("ArNameLabel", "Name");
        Set("FrNameLabel", "Name");
        Set("MatriculeLabel", "Matricule");
        Set("PinLabel", "Pin");
        Set("DaysLabel", "WorkDays");
        Set("SaveButton", "Save");
        Set("ArchiveButton", "Archive");
        Set("RestoreButton", "Restore");
        Set("CloseButton", "Close");
        Set("SunCheck", "Sun");
        Set("MonCheck", "Mon");
        Set("TueCheck", "Tue");
        Set("WedCheck", "Wed");
        Set("ThuCheck", "Thu");
        Set("FriCheck", "Fri");
        Set("SatCheck", "Sat");
        Set("EmptyTitle", "EmptyEmployees");
        Set("EmptyHint", "EmptyEmployeesHint");
        Set("EmptyAction", "NewEmployee");
        Set("LoadingText", "Loading");

        var archived = this.FindControl<CheckBox>("ArchivedCheck");
        if (archived is not null) archived.Content = Lang.Get("ShowArchived");
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
            return;
        }

        var check = this.FindControl<CheckBox>(control);
        if (check is not null)
        {
            check.Content = Lang.Get(key);
        }
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        _vm.Employees.CollectionChanged -= OnRowsChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        App.LanguageChanged -= ApplyLanguage;
    }
}
