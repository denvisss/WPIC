using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WPIC.Services;

public class ManifestInfo
{
    public string? Description { get; set; }
    public string? Homepage { get; set; }
    public string? Publisher { get; set; }
    /// <summary>Язык манифеста, из которого взято описание (например, ru-RU / en-US).</summary>
    public string? Locale { get; set; }
    public DateTime FetchedUtc { get; set; }
    public bool HasData => Description != null || Homepage != null || Publisher != null;
}

/// <summary>
/// Локализованные метаданные пакетов из репозитория winget-pkgs (GitHub).
/// Описание берётся на языке интерфейса системы (например, ru-RU), если у пакета есть такой манифест;
/// иначе — en-US / default locale. Если GitHub недоступен — вызывающий код откатывается на `winget show`.
/// Результаты кэшируются на 24 часа.
/// </summary>
public static class PackageMetaService
{
    private static readonly HttpClient Http = CreateHttp();
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    private static string CacheDir => Path.Combine(LogService.DataDirectory, "manifests");

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WPIC/0.2 (package metadata)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Предпочтение языка описаний: system | ru | en (берётся из настроек языка интерфейса).</summary>
    public static string LanguagePreference { get; set; } = "system";

    public static async Task<ManifestInfo?> GetAsync(string packageId, CancellationToken ct = default)
    {
        Directory.CreateDirectory(CacheDir);
        var cacheFile = Path.Combine(CacheDir, MakeSafe(packageId) + "." + LanguagePreference + ".json");
        try
        {
            if (File.Exists(cacheFile))
            {
                var cached = JsonSerializer.Deserialize<ManifestInfo>(File.ReadAllText(cacheFile), Json.Options);
                if (cached != null && cached.HasData && cached.Locale != null && DateTime.UtcNow - cached.FetchedUtc < Ttl)
                    return cached;
            }
        }
        catch
        {
            // повреждённый кэш игнорируем
        }

        var info = await FetchFromGitHubAsync(packageId, ct);
        if (info == null) return null;

        info.FetchedUtc = DateTime.UtcNow;
        try
        {
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(info, Json.Options));
        }
        catch
        {
            // кэш не критичен
        }
        return info;
    }

    private static async Task<ManifestInfo?> FetchFromGitHubAsync(string packageId, CancellationToken ct)
    {
        try
        {
            var parts = packageId.Split('.');
            if (parts.Length < 2 || parts.Any(string.IsNullOrWhiteSpace)) return null;

            var publisher = parts[0];
            var package = string.Join(".", parts.Skip(1));
            var letter = char.ToLowerInvariant(publisher[0]).ToString();

            // список версий пакета
            var listUrl =
                $"https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/{letter}" +
                $"/{Encode(publisher)}/{Encode(package)}";
            using var listResponse = await Http.GetAsync(listUrl, ct);
            if (!listResponse.IsSuccessStatusCode) return null;

            var json = await listResponse.Content.ReadAsStringAsync(ct);
            var items = JsonSerializer.Deserialize<List<GhItem>>(json, Json.Options) ?? new List<GhItem>();

            Version? best = null;
            var bestName = "";
            foreach (var item in items)
            {
                if (item.Type != "dir") continue;
                if (!Version.TryParse(item.Name, out var version)) continue;
                if (best == null || version > best)
                {
                    best = version;
                    bestName = item.Name;
                }
            }
            if (bestName.Length == 0) return null;

            // Язык описаний: предпочтение пользователя, затем en-US, затем default locale
            var candidates = new List<string>();
            switch (LanguagePreference)
            {
                case "ru":
                    candidates.Add($"{packageId}.locale.ru-RU.yaml");
                    break;
                case "en":
                    candidates.Add($"{packageId}.locale.en-US.yaml");
                    break;
                default:
                    var culture = CultureInfo.CurrentUICulture;
                    if (!string.IsNullOrEmpty(culture.Name))
                        candidates.Add($"{packageId}.locale.{culture.Name}.yaml");
                    break;
            }
            candidates.Add($"{packageId}.locale.en-US.yaml");
            candidates.Add($"{packageId}.locale.yaml");

            foreach (var file in candidates.Distinct())
            {
                var rawUrl =
                    $"https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/{letter}" +
                    $"/{Encode(publisher)}/{Encode(package)}/{Encode(bestName)}/{Encode(file)}";
                using var response = await Http.GetAsync(rawUrl, ct);
                if (!response.IsSuccessStatusCode) continue;

                var yaml = await response.Content.ReadAsStringAsync(ct);
                var info = ParseYaml(yaml);
                if (info?.Description != null || info?.Homepage != null)
                {
                    var tag = file[(file.IndexOf(".locale.", StringComparison.Ordinal) + 8)..^5];
                    info.Locale = tag.Length == 0 ? "en-US" : tag;
                    return info;
                }
            }
        }
        catch
        {
            // недоступность GitHub — не ошибка приложения, откат на winget show
        }
        return null;
    }

    private static ManifestInfo? ParseYaml(string yaml)
    {
        var shortDescription = MatchValue(yaml, "ShortDescription");
        var longDescription = MatchValue(yaml, "Description");
        var packageUrl = MatchValue(yaml, "PackageUrl");
        var publisher = MatchValue(yaml, "Publisher");

        var description = shortDescription ?? longDescription;
        if (description == null && packageUrl == null && publisher == null) return null;

        return new ManifestInfo
        {
            Description = description,
            Homepage = packageUrl,
            Publisher = publisher
        };
    }

    private static string? MatchValue(string yaml, string key)
    {
        var match = Regex.Match(yaml, $@"(?m)^{key}:\s*(.+)$");
        if (!match.Success) return null;
        var value = match.Groups[1].Value.Trim();
        if (value.Length == 0 || value is "|" or "|-" or "|+" or ">" or ">-" or ">+") return null;
        value = value.Trim('"', '\'');
        return value.Length == 0 ? null : value;
    }

    private static string Encode(string segment) => Uri.EscapeDataString(segment);

    private static string MakeSafe(string s) => string.Join("_", s.Split(Path.GetInvalidFileNameChars()));

    private sealed class GhItem
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
    }
}
