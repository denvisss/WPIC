using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WPIC.Services;

/// <summary>
/// Перевод описаний приложений на язык интерфейса, если манифест пакета на другом языке.
/// Используется публичный endpoint Google Translate; при недоступности возвращается оригинал.
/// Результаты кэшируются на диске.
/// </summary>
public static class TranslationService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static string CacheDir => Path.Combine(LogService.DataDirectory, "translations");

    public static async Task<string> TranslateAsync(string text, string from, string to, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || from.Equals(to, StringComparison.OrdinalIgnoreCase)) return text;

        var cacheFile = Path.Combine(CacheDir, MakeKey(text, from, to) + ".txt");
        try
        {
            if (File.Exists(cacheFile))
            {
                var cached = await File.ReadAllTextAsync(cacheFile, ct);
                if (cached.Length > 0) return cached;
            }
        }
        catch
        {
            // повреждённый кэш игнорируем
        }

        try
        {
            var url = "https://translate.googleapis.com/translate_a/single?client=gtx" +
                      $"&sl={Uri.EscapeDataString(from)}&tl={Uri.EscapeDataString(to)}&dt=t&q={Uri.EscapeDataString(text)}";
            using var response = await Http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return text;

            var json = await response.Content.ReadAsStringAsync(ct);
            var translated = ParseResponse(json);
            if (string.IsNullOrWhiteSpace(translated)) return text;

            try
            {
                Directory.CreateDirectory(CacheDir);
                await File.WriteAllTextAsync(cacheFile, translated, ct);
            }
            catch
            {
                // кэш не критичен
            }
            return translated;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return text;
        }
    }

    /// <summary>Ответ gtx: [[["перевод","оригинал",...],...],...]. Склеивает сегменты перевода.</summary>
    public static string? ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;
            var segments = doc.RootElement[0];
            if (segments.ValueKind != JsonValueKind.Array) return null;

            var sb = new StringBuilder();
            foreach (var segment in segments.EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0 &&
                    segment[0].ValueKind == JsonValueKind.String)
                    sb.Append(segment[0].GetString());
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string MakeKey(string text, string from, string to)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{from}>{to}:{text}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}