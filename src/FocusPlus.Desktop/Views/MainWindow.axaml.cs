using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FocusPlus.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly ContentControl _root;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _root = this.FindControl<ContentControl>("Root")!;
        _root.Content = new LoginView();
    }

    public void Show(Control content) => _root.Content = content;
}
