using System.IO;
using System.Text.Json;

namespace WPIC.Services;

/// <summary>
/// Актуальные версии пакетов каталога из winget. Кэш на диске показывается сразу при запуске,
/// затем версии обновляются в фоне (параллельные запросы search --id).
/// </summary>
public static class CatalogVersionService
{
    private static string CachePath => Path.Combine(LogService.DataDirectory, "catalog-versions.json");

    public static Dictionary<string, string> LoadCache()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var cache = JsonSerializer.Deserialize<VersionCache>(File.ReadAllText(CachePath), Json.Options);
                if (cache?.Versions != null)
                    return new Dictionary<string, string>(cache.Versions, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // повреждённый кэш игнорируем
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public static async Task RefreshAsync(
        IEnumerable<string> packageIds, Action<string, string> onVersion, CancellationToken ct = default)
    {
        var ids = packageIds.Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0) return;

        var versions = LoadCache();
        using var gate = new SemaphoreSlim(5);
        var tasks = ids.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var args = $"search --id \"{id.Replace("\"", "")}\" -e --accept-source-agreements --disable-interactivity";
                var (_, lines) = await WingetService.RunAsync(args, null, ct);
                var version = ParseExactVersion(lines, id);
                if (version == null || version.Length == 0) return;
                lock (versions) versions[id] = version;
                onVersion(id, version);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // пакет не найден или winget недоступен — останется кэш
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);

        try
        {
            Dictionary<string, string> snapshot;
            lock (versions) snapshot = new Dictionary<string, string>(versions, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(
                new VersionCache { FetchedUtc = DateTime.UtcNow, Versions = snapshot }, Json.Options));
        }
        catch
        {
            // кэш не критичен
        }
    }

    /// <summary>
    /// Версия из вывода `search --id … -e`: единственная строка результата, токен после идентификатора.
    /// В узких таблицах winget столбцы разделены одним пробелом, поэтому общий TableParser не подходит.
    /// </summary>
    public static string? ParseExactVersion(IEnumerable<string> lines, string packageId)
    {
        foreach (var line in lines)
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < tokens.Length - 1; i++)
                if (string.Equals(tokens[i], packageId, StringComparison.OrdinalIgnoreCase))
                    return tokens[i + 1];
        }
        return null;
    }

    private sealed class VersionCache
    {
        public DateTime FetchedUtc { get; set; }
        public Dictionary<string, string> Versions { get; set; } = new();
    }
}