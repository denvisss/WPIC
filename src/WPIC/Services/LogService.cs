using System.IO;

namespace WPIC.Services;

/// <summary>Единый журнал: окно «Журнал» + файл на диске.</summary>
public static class LogService
{
    public static event Action<string>? EntryAdded;

    public static string DataDirectory { get; } = EnsureDataDirectory();

    /// <summary>Папка данных переименована вместе с приложением; старая %LOCALAPPDATA%\WIC переносится один раз.</summary>
    private static string EnsureDataDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = Path.Combine(appData, "WPIC");
        var legacy = Path.Combine(appData, "WIC");
        try
        {
            if (!Directory.Exists(current) && Directory.Exists(legacy))
                Directory.Move(legacy, current);
        }
        catch
        {
            // старая папка занята другим процессом — продолжим с чистым каталогом
        }
        return current;
    }

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    private static readonly object FileLock = new();

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        EntryAdded?.Invoke(line);
        try
        {
            Directory.CreateDirectory(LogDirectory);
            lock (FileLock)
                File.AppendAllText(
                    Path.Combine(LogDirectory, $"log-{DateTime.Now:yyyy-MM-dd}.txt"),
                    line + Environment.NewLine);
        }
        catch
        {
            // журнал не должен ломать работу приложения
        }
    }
}
