using System.IO;
using System.Text.Json;

namespace WPIC.Services;

public class AppSettings
{
    /// <summary>Тема оформления: system | light | dark.</summary>
    public string Theme { get; set; } = ThemeService.System;

    /// <summary>Язык интерфейса: system | ru | en.</summary>
    public string Language { get; set; } = "system";

    /// <summary>Путь к офлайн-набору сетевых драйверов (пусто = искать автоматически).</summary>
    public string DriverPackPath { get; set; } = "";
}

/// <summary>Персистентные настройки (settings.json в каталоге данных приложения).</summary>
public static class SettingsService
{
    private static string FilePath => Path.Combine(LogService.DataDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json.Options)
                       ?? new AppSettings();
        }
        catch
        {
            // повреждённый файл настроек не критичен
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Json.Options));
        }
        catch (Exception ex)
        {
            LogService.Log(L10n.T("settings.err.save", ex.Message));
        }
    }
}
