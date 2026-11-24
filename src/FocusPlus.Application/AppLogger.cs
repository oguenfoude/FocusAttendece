using System.Text;

namespace FocusPlus.Application;

/// <summary>Small rolling file logger (no external dependency).</summary>
public sealed class AppLogger
{
    private readonly object _gate = new();
    private readonly string _directory;

    public AppLogger(string logDirectory)
    {
        _directory = logDirectory;
        Directory.CreateDirectory(_directory);
    }

    public string LogPath => Path.Combine(_directory, $"app-{DateTime.Now:yyyyMMdd}.log");

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            var builder = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("] ")
                .Append(message);

            if (exception is not null)
            {
                builder.AppendLine().Append(exception);
            }

            lock (_gate)
            {
                File.AppendAllText(LogPath, builder.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // Logging must never break the application.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: logging is best effort.
        }
    }
}
