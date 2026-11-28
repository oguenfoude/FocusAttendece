using Avalonia;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FocusPlus.Application;
using FocusPlus.Application.ViewModels;

namespace FocusPlus.Desktop.Views;

public partial class AttendanceView : UserControl
{
    private readonly AttendanceViewModel _vm;
    private readonly ListBox _list;
    private readonly Border _drawer;
    private readonly Border _loading;
    private readonly StackPanel _empty;
    private readonly Button _exportCsv;
    private readonly Button _exportHtml;
    private readonly TextBox _manualNoteBox;
    private AttendanceRowVm? _lastRow;
    private bool _suppressSelection;
    private bool _exporting;
    private bool _detached;

    public AttendanceView()
    {
        AvaloniaXamlLoader.Load(this);
        _vm = new AttendanceViewModel(App.Host, App.Messenger);
        DataContext = _vm;

        _list = this.FindControl<ListBox>("AttendanceList")!;
        _drawer = this.FindControl<Border>("Drawer")!;
        _loading = this.FindControl<Border>("LoadingPanel")!;
        _empty = this.FindControl<StackPanel>("EmptyPanel")!;
        _exportCsv = this.FindControl<Button>("ExportCsvButton")!;
        _exportHtml = this.FindControl<Button>("ExportHtmlButton")!;
        _manualNoteBox = this.FindControl<TextBox>("ManualNoteBox")!;

        _vm.Rows.CollectionChanged += OnRowsChanged;
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
            App.Host.Logger.Error("Attendance workspace load failed.", ex);
            App.Messenger.Notify(Lang.Get("ErrorLoad"), UiSeverity.Error);
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void AddManual_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.ManualNote))
        {
            App.Messenger.Notify(Lang.Get("ReasonRequired"), UiSeverity.Warning);
            _manualNoteBox.Focus();
            return;
        }

        if (_vm.AddManualPunchCommand.CanExecute(null))
        {
            _vm.AddManualPunchCommand.Execute(null);
        }
    }

    private void AttendanceList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _detached)
        {
            return;
        }

        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not AttendanceRowVm row)
        {
            return;
        }

        _lastRow = row;
        _vm.OpenDetailCommand.Execute(row);
        FocusDrawer();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AttendanceViewModel.IsDetailOpen) && !_vm.IsDetailOpen)
        {
            RestoreListFocus();
        }
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_vm.IsDetailOpen)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            _vm.CloseDetailCommand.Execute(null);
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
            if (_vm.IsDetailOpen)
            {
                _manualNoteBox.Focus();
            }
        }, DispatcherPriority.Input);

    private void RestoreListFocus() =>
        Dispatcher.UIThread.Post(() =>
        {
            _suppressSelection = true;
            try
            {
                if (_lastRow is not null && _vm.Rows.Contains(_lastRow))
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

    private async void ExportCsv_Click(object? sender, RoutedEventArgs e) => await ExportAsync(csv: true);

    private async void ExportHtml_Click(object? sender, RoutedEventArgs e) => await ExportAsync(csv: false);

    private async Task ExportAsync(bool csv)
    {
        if (_exporting || _vm.IsBusy)
        {
            return;
        }

        _exporting = true;
        _exportCsv.IsEnabled = false;
        _exportHtml.IsEnabled = false;
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is null)
            {
                App.Messenger.Notify(Lang.Get("ExportFailed"), UiSeverity.Error);
                return;
            }

            var extension = csv ? "csv" : "html";
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Lang.Get(csv ? "ExportCsv" : "ExportReport"),
                SuggestedFileName = string.Create(
                    CultureInfo.InvariantCulture,
                    $"attendance-{_vm.FromDate:yyyyMMdd}-{_vm.ToDate:yyyyMMdd}.{extension}"),
                DefaultExtension = extension,
                ShowOverwritePrompt = true,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType(csv ? "CSV" : "HTML")
                    {
                        Patterns = new[] { $"*.{extension}" }
                    }
                }
            });

            if (file is null)
            {
                App.Messenger.Notify(Lang.Get("ExportCancelled"), UiSeverity.Info);
                return;
            }

            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                App.Messenger.Notify(Lang.Get("ExportFailed"), UiSeverity.Error);
                return;
            }

            var written = csv ? _vm.ExportCsv(path) : _vm.ExportHtml(path);
            App.Messenger.Notify(
                $"{Lang.Get("ExportSaved")}: {Path.GetFileName(written)}",
                UiSeverity.Success);
        }
        catch (Exception ex)
        {
            App.Host.Logger.Error("Attendance export failed.", ex);
            App.Messenger.Notify(Lang.Get("ErrorExport"), UiSeverity.Error);
        }
        finally
        {
            _exporting = false;
            _exportCsv.IsEnabled = !_vm.IsBusy;
            _exportHtml.IsEnabled = !_vm.IsBusy;
        }
    }

    private void SetLoading(bool loading)
    {
        _loading.IsVisible = loading;
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        _empty.IsVisible = !_loading.IsVisible && _vm.Rows.Count == 0;
    }

    private void ApplyLanguage()
    {
        FlowDirection = App.Direction;
        _drawer.HorizontalAlignment = App.IsArabic ? HorizontalAlignment.Left : HorizontalAlignment.Right;

        Set("TodayButton", "Today");
        Set("WeekButton", "Week");
        Set("MonthButton", "Month");
        Set("RefreshButton", "Refresh");
        Set("ExportCsvButton", "ExportCsv");
        Set("ExportHtmlButton", "ExportReport");
        Set("PunchesLabel", "Punches");
        Set("ManualLabel", "ManualPunch");
        Set("AddManualButton", "Save");
        Set("CloseButton", "Close");
        Set("EmptyTitle", "EmptyAttendance");
        Set("EmptyHint", "EmptyAttendanceHint");
        Set("EmptyAction", "Retry");
        Set("LoadingText", "Loading");

        var note = this.FindControl<TextBox>("ManualNoteBox");
        if (note is not null) note.Watermark = Lang.Get("Reason");
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

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        _vm.Rows.CollectionChanged -= OnRowsChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        App.LanguageChanged -= ApplyLanguage;
    }
}
