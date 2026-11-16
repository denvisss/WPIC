using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WPIC.Services;

/// <summary>
/// Загрузка иконок приложений по домашней странице пакета (из `winget show`).
/// Источники по очереди: кэш на диске, сервис иконок DuckDuckGo, apple-touch-icon, favicon.
/// </summary>
public static class IconService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static string CacheDir => Path.Combine(LogService.DataDirectory, "icons");

    public static async Task<byte[]?> FetchIconBytesAsync(string? homepageUrl, CancellationToken ct = default, string? packageId = null)
    {
        var host = TryGetHost(homepageUrl);
        var guessed = false;
        if (host == null && !string.IsNullOrWhiteSpace(packageId))
        {
            // Без домашней страницы пробуем домен издателя: Google.Chrome → google.com
            var parts = packageId.Split('.');
            if (parts.Length >= 2 && parts[0].Length > 1)
            {
                host = parts[0].ToLowerInvariant() + ".com";
                guessed = true;
            }
        }
        if (string.IsNullOrEmpty(host)) return null;

        Directory.CreateDirectory(CacheDir);
        var cacheFile = Path.Combine(CacheDir, MakeSafe(host) + ".ico");
        if (File.Exists(cacheFile))
        {
            try
            {
                var cached = await File.ReadAllBytesAsync(cacheFile, ct);
                if (cached.Length > 0) return cached;
            }
            catch
            {
                // игнорируем повреждённый кэш
            }
        }

        var urls = guessed
            ? new[] { $"https://icons.duckduckgo.com/ip3/{host}.ico" }
            : new[]
            {
                $"https://icons.duckduckgo.com/ip3/{host}.ico",
                $"https://{host}/apple-touch-icon.png",
                $"https://{host}/favicon.ico"
            };

        foreach (var url in urls)
        {
            try
            {
                using var response = await Http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) continue;
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (!LooksLikeImage(bytes)) continue;
                await File.WriteAllBytesAsync(cacheFile, bytes, ct);
                return bytes;
            }
            catch
            {
                // пробуем следующий источник
            }
        }
        return null;
    }

    public static ImageSource? ToImageSource(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            var image = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.DecodePixelWidth = 96;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var host = new Uri(url).Host;
            return string.IsNullOrEmpty(host) ? null : host;
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeImage(byte[] b)
    {
        if (b.Length < 16) return false;
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;                 // PNG
        if (b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01 && b[3] == 0x00) return true;                 // ICO
        if (b[0] == 0xFF && b[1] == 0xD8) return true;                                                 // JPEG
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return true;                                 // GIF
        if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46) return true;                 // WEBP
        return false;
    }

    private static string MakeSafe(string s) => string.Join("_", s.Split(Path.GetInvalidFileNameChars()));
}
