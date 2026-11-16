using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace WPIC.Services;

public class UpdateInfo
{
    public string Version { get; set; } = "";
    public string Tag { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>
/// Обновление программы из релизов GitHub. Проверка выполняется только при запуске
/// (и вручную из настроек). Full-вариант обновляется тихим запуском Inno-установщика,
/// portable — подменой exe через cmd-скрипт после выхода программы.
/// </summary>
public static class UpdateService
{
    public const string RepoUrl = "https://github.com/denvisss/WPIC";
    public const string DiscussionsUrl = RepoUrl + "/discussions";
    private const string LatestApi = "https://api.github.com/repos/denvisss/WPIC/releases/latest";

    private static readonly HttpClient Http = CreateHttp();

    /// <summary>Найденное обновление (null = не найдено или проверка ещё не выполнялась).</summary>
    public static UpdateInfo? Pending { get; private set; }
    public static event Action? PendingChanged;

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    /// <summary>Full-установщик Inno Setup оставляет unins000.exe рядом с exe.</summary>
    public static bool IsInstalledFull => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public static string Variant => IsInstalledFull ? "full" : "portable";

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WPIC/0.3 (update check)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        UpdateInfo? info = null;
        try
        {
            using var response = await Http.GetAsync(LatestApi, ct);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                info = ParseRelease(json, CurrentVersion, Variant);
            }
        }
        catch
        {
            // недоступность GitHub не мешает работе программы
        }
        Pending = info;
        PendingChanged?.Invoke();
        return info;
    }

    /// <summary>Разбор ответа releases/latest: пропуск черновиков и prerelease, поиск ассета своего варианта.</summary>
    public static UpdateInfo? ParseRelease(string json, Version current, string variant)
    {
        GhRelease? release;
        try
        {
            release = JsonSerializer.Deserialize<GhRelease>(json, Json.Options);
        }
        catch (JsonException)
        {
            return null;
        }
        if (release == null || release.Draft || release.Prerelease) return null;

        var tag = release.TagName.TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var version) || version <= current) return null;

        var assetName = $"WPIC_{tag}-{variant}.exe";
        var asset = release.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
        if (asset == null || string.IsNullOrEmpty(asset.BrowserDownloadUrl)) return null;

        return new UpdateInfo
        {
            Version = tag,
            Tag = release.TagName,
            DownloadUrl = asset.BrowserDownloadUrl,
            Size = asset.Size
        };
    }

    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "WPIC-update");
        Directory.CreateDirectory(dir);
        var fileName = $"WPIC_{info.Version}-{Variant}.exe";
        var target = Path.Combine(dir, fileName);

        using var response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? info.Size;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(target);
        var buffer = new byte[128 * 1024];
        long read = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress?.Report(Math.Min(100.0, read * 100.0 / total));
        }
        progress?.Report(100);
        return target;
    }

    /// <summary>Full: тихая установка поверх текущей версии; вызывающий код закрывает программу.</summary>
    public static void LaunchInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo(installerPath)
        {
            Arguments = "/SILENT /NORESTART /SUPPRESSMSGBOXES",
            UseShellExecute = true
        });
    }

    public static string BuildPortableSwapScript(string newExe, string currentExe)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine(":wait");
        sb.AppendLine($"move /y \"{currentExe}\" \"{currentExe}.old\" >nul 2>&1");
        sb.AppendLine("if errorlevel 1 (");
        sb.AppendLine("    timeout /t 1 /nobreak >nul");
        sb.AppendLine("    goto wait");
        sb.AppendLine(")");
        sb.AppendLine($"move /y \"{newExe}\" \"{currentExe}\" >nul");
        sb.AppendLine($"start \"\" \"{currentExe}\"");
        sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
        return sb.ToString();
    }

    /// <summary>Portable: скрипт подмены exe стартует после закрытия программы.</summary>
    public static void LaunchPortableSwap(string newExe)
    {
        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException(L10n.T("update.err.processPath"));
        var dir = Path.Combine(Path.GetTempPath(), "WPIC-update");
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "swap.cmd");
        File.WriteAllText(script, BuildPortableSwapScript(newExe, currentExe), Encoding.ASCII);
        Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c \"\"{script}\"\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    /// <summary>Удаление старого exe после portable-обновления (вызывается при запуске).</summary>
    public static void CleanupOldPortable()
    {
        try
        {
            var old = (Environment.ProcessPath ?? "") + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch
        {
            // файл может быть ещё занят; попробуем при следующем запуске
        }
    }

    private sealed class GhRelease
    {
        [System.Text.Json.Serialization.JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";
        public bool Draft { get; set; }
        public bool Prerelease { get; set; }
        public List<GhAsset>? Assets { get; set; }
    }

    private sealed class GhAsset
    {
        public string Name { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
        public long Size { get; set; }
    }
}