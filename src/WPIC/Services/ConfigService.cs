using System.IO;
using System.Text.Json;
using WPIC.Models;

namespace WPIC.Services;

/// <summary>Загрузка декларативных конфигов (приложения, твики) из папки configs рядом с exe.</summary>
public static class ConfigService
{
    public static string ConfigDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "configs");

    public static async Task<AppCatalog> LoadAppsAsync() => await LoadAsync<AppCatalog>("apps.json");

    public static async Task<TweakCatalog> LoadTweaksAsync() => await LoadAsync<TweakCatalog>("tweaks.json");

    private static async Task<T> LoadAsync<T>(string fileName) where T : new()
    {
        var path = Path.Combine(ConfigDirectory, fileName);
        if (File.Exists(path))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var result = await JsonSerializer.DeserializeAsync<T>(stream, Json.Options);
                return result ?? new T();
            }
            catch (Exception ex)
            {
                LogService.Log(L10n.T("config.log.parseError", path, ex.Message));
            }
        }
        else
        {
            LogService.Log(L10n.T("config.log.notFound", path));
        }
        var embedded = ReadEmbedded("WPIC.configs." + fileName);
        if (embedded == null) return new T();
        LogService.Log(L10n.T("config.log.embedded", fileName));
        try
        {
            return JsonSerializer.Deserialize<T>(embedded, Json.Options) ?? new T();
        }
        catch (Exception ex)
        {
            LogService.Log(L10n.T("config.log.parseError", fileName, ex.Message));
            return new T();
        }
    }

    private static string? ReadEmbedded(string name)
    {
        using var stream = typeof(ConfigService).Assembly.GetManifestResourceStream(name);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
