using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FocusPlus.Application.ViewModels;

namespace FocusPlus.Desktop.Views;

public partial class DevicesView : UserControl
{
    private readonly DevicesViewModel _vm;

    public DevicesView()
    {
        AvaloniaXamlLoader.Load(this);
        _vm = new DevicesViewModel(App.Host, App.Messenger);
        DataContext = _vm;
        _ = _vm.RefreshAsync();
        App.LanguageChanged += ApplyLanguage;
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        FlowDirection = App.IsArabic ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
        Set("HeaderText", "TerminalConsole");
        Set("ScanButton", "ScanLan");
        Set("RefreshButton", "Refresh");
        Set("AddButton", "AddTerminal");
        Set("ListTitle", "Terminals");
        Set("ScanTitle", "ScanLan");
        Set("NameLabel", "Name");
        Set("IpLabel", "IpAddress");
        Set("PortLabel", "Ready");
        Set("KeyLabel", "CommKeyRequired");
        Set("MaskLabel", "IpAddress");
        Set("GatewayLabel", "IpAddress");
        Set("NoteLabel", "About");
        Set("SaveButton", "Save");
        Set("PullButton", "PullAll");
        Set("SyncButton", "SyncAll");
        Set("RebootButton", "Refresh");
        Set("UsersButton", "Users");
        Set("DumpButton", "TerminalConsole");
        Set("CloseButton", "Close");
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
}
