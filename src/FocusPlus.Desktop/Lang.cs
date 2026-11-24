using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FocusPlus.Desktop;

/// <summary>Loads the AR/FR resource dictionaries and exposes the active strings.</summary>
public static class Lang
{
    private static ResourceDictionary? _current;

    public static void Load(bool arabic)
    {
        var file = arabic ? "Lang.ar.axaml" : "Lang.fr.axaml";
        var uri = new Uri($"avares://FocusPlus/Resources/{file}");
        _current = (ResourceDictionary)AvaloniaXamlLoader.Load(uri);
    }

    public static string Get(string key) =>
        _current is not null && _current.TryGetValue(key, out var value) && value is string text
            ? text
            : key;
}
