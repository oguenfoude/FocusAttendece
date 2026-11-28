using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FocusPlus.Desktop;

/// <summary>Loads the AR/FR resource dictionaries and exposes the active strings.</summary>
public static class Lang
{
    private static ResourceDictionary? _ar;
    private static ResourceDictionary? _fr;
    private static ResourceDictionary? _current;

    /// <summary>Raised after a dictionary failed to load; the exception has already been logged.</summary>
    public static event Action<string, Exception>? LoadFailed;

    /// <summary>Last localization failure, kept for diagnostics; null once every requested dictionary loaded.</summary>
    public static string? LastFailure { get; private set; }

    public static void Load(bool arabic)
    {
        var cached = arabic ? _ar : _fr;
        if (cached is not null)
        {
            _current = cached;
            return;
        }

        var file = arabic ? "Lang.ar.axaml" : "Lang.fr.axaml";
        try
        {
            var dictionary = LoadDictionary(file);
            if (arabic)
            {
                _ar = dictionary;
            }
            else
            {
                _fr = dictionary;
            }

            _current = dictionary;
        }
        catch (Exception ex)
        {
            // Never keep a stale dictionary for the requested language: drop it and fall back
            // to the other language when that one loaded, otherwise expose the raw keys.
            if (arabic)
            {
                _ar = null;
            }
            else
            {
                _fr = null;
            }

            _current = arabic ? _fr : _ar;
            LastFailure = $"{file}: {ex.Message}";
            LogFailure(file, ex);
            LoadFailed?.Invoke(file, ex);
        }
    }

    public static string Get(string key) =>
        _current is not null && _current.TryGetValue(key, out var value) && value is string text
            ? text
            : key;

    private static ResourceDictionary LoadDictionary(string file)
    {
        var uri = new Uri($"avares://FocusPlus/Resources/{file}");
        return (ResourceDictionary)AvaloniaXamlLoader.Load(uri);
    }

    /// <summary>Exception details stay in the log; the UI only ever shows a localized generic key.</summary>
    private static void LogFailure(string file, Exception ex)
    {
        var host = App.Host;
        if (host is not null)
        {
            host.Logger.Error($"Localization dictionary '{file}' failed to load.", ex);
            return;
        }

        Console.Error.WriteLine($"Localization dictionary '{file}' failed to load: {ex}");
    }
}
