using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace WPIC.Services;

/// <summary>
/// Установка winget: пакеты скачиваются из официальных источников Microsoft во время работы
/// (в exe они больше не встроены), проверяются по SHA-256 и ставятся через Add-AppxPackage.
/// </summary>
public static class WingetBootstrapService
{
    public const string PayloadDirName = "winget";

    // Официальные пакеты winget 1.7.10861 (x64). Размер и SHA-256 фиксируют содержимое на момент скачивания.
    public static readonly (string File, long Size, string Sha256)[] PayloadFiles =
    {
        ("AppInstaller_x64.msix", 60247360,
            "0E69BF5F2BCB014ADC1688F0036BC6E9BCEF779831BF6E4FC2435CB062099D85"),
        ("Microsoft.VCLibs.x64.14.00.Desktop.appx", 6764349,
            "B56A9101F706F9D95F815F5B7FA6EFBAC972E86573D378B96A07CFF5540C5961"),
        ("Microsoft.UI.Xaml.2.8.x64.appx", 5121145,
            "249D2AFB41CC009494841372BD6DD2DF46F87386D535DDF8D9F32C97226D2E46")
    };
    private static readonly string[] PackageFiles = PayloadFiles.Select(p => p.File).ToArray();

    public const string BundleUrl =
        "https://github.com/microsoft/winget-cli/releases/download/v1.7.10861/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle";
    public const string VclibsUrl = "https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx";
    public const string UiXamlUrl =
        "https://github.com/microsoft/microsoft-ui-xaml/releases/download/v2.8.6/Microsoft.UI.Xaml.2.8.x64.appx";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private static bool? _cacheValid;

    public static string CacheDir => Path.Combine(LogService.DataDirectory, PayloadDirName);

    /// <summary>Готовый набор пакетов в %LOCALAPPDATA%\WPIC\winget; null, если он ещё не скачан.</summary>
    public static string? FindPayload()
    {
        if (_cacheValid == true) return CacheDir;
        var dir = CacheDir;
        if (PayloadFiles.All(p =>
            {
                var path = Path.Combine(dir, p.File);
                return File.Exists(path) && new FileInfo(path).Length == p.Size;
            }))
        {
            _cacheValid = true;
            return dir;
        }
        return null;
    }

    /// <summary>Скачивает пакеты winget (интернет обязателен) и устанавливает их.</summary>
    public static async Task<bool> InstallAsync(IProgress<string>? log, CancellationToken ct = default)
    {
        var payload = await EnsurePayloadAsync(log, ct);
        return payload != null && await InstallOfflineAsync(payload, log, ct);
    }

    public static async Task<string?> EnsurePayloadAsync(IProgress<string>? log, CancellationToken ct = default)
    {
        var payload = FindPayload();
        if (payload != null) return payload;
        var dir = CacheDir;
        Directory.CreateDirectory(dir);
        try
        {
            await DownloadAsync(VclibsUrl, Path.Combine(dir, PackageFiles[1]), log, ct);
            Verify(dir, PayloadFiles[1]);
            await DownloadAsync(UiXamlUrl, Path.Combine(dir, PackageFiles[2]), log, ct);
            Verify(dir, PayloadFiles[2]);
            log?.Report(L10n.T("winget.dl.bundle"));
            var bundle = Path.Combine(Path.GetTempPath(), "wpic-winget-" + Guid.NewGuid().ToString("N") + ".msixbundle");
            try
            {
                await DownloadAsync(BundleUrl, bundle, log, ct);
                var seven = DriverPackService.FindExtractor();
                var (extractCode, extractOut) = await RunToolAsync(
                    seven, new[] { "e", "-y", "-o" + dir, bundle, PackageFiles[0] }, ct);
                if (extractCode != 0)
                    throw new InvalidOperationException(L10n.T("winget.dl.extractError", extractOut.Trim()));
            }
            finally
            {
                try { File.Delete(bundle); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            Verify(dir, PayloadFiles[0]);
            _cacheValid = true;
            log?.Report(L10n.T("winget.dl.done"));
            return dir;
        }
        catch
        {
            _cacheValid = null;
            throw;
        }
    }

    private static async Task DownloadAsync(string url, string dest, IProgress<string>? log, CancellationToken ct)
    {
        log?.Report(L10n.T("winget.dl.file", Path.GetFileName(dest)));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(dest);
                var buffer = new byte[81920];
                long read = 0, nextReport = 0;
                int chunk;
                while ((chunk = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, chunk), ct);
                    read += chunk;
                    if (total > 0 && read >= nextReport)
                    {
                        nextReport = read + 8_000_000;
                        log?.Report(L10n.T("winget.dl.progress", read / 1048576, total / 1048576));
                    }
                }
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < 3)
            {
                log?.Report(L10n.T("winget.dl.retry", attempt, ex.Message));
                await Task.Delay(2000, ct);
            }
        }
    }

    private static void Verify(string dir, (string File, long Size, string Sha256) expected)
    {
        var path = Path.Combine(dir, expected.File);
        if (!File.Exists(path)) throw new InvalidOperationException(L10n.T("winget.dl.verifyFailed", expected.File));
        using var stream = File.OpenRead(path);
        if (stream.Length != expected.Size ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException(L10n.T("winget.dl.verifyFailed", expected.File));
    }

    // ---------- Microsoft Store ----------

    /// <summary>Установлен ли Microsoft Store (пакет Microsoft.WindowsStore).</summary>
    /// <summary>Магазин может быть зарегистрирован на другого пользователя, а приложение всегда elevated:
    /// сначала -AllUsers, при ошибке — per-user. Итог кодируется кодом выхода, шум stderr не влияет.</summary>
    public static async Task<bool> IsStoreInstalledAsync(CancellationToken ct = default)
    {
        try
        {
            const string script = "$found=$false; " +
                "try { if (Get-AppxPackage -AllUsers -Name Microsoft.WindowsStore -ErrorAction Stop) { $found=$true } } catch { } " +
                "if (-not $found) { try { if (Get-AppxPackage -Name Microsoft.WindowsStore -ErrorAction Stop) { $found=$true } } catch { } } " +
                "if ($found) { exit 0 } else { exit 1 }";
            return (await RunPowerShellAsync(script, ct)).Code == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Штатное восстановление Microsoft Store (`wsreset -i`, Windows 10 1809+): запускает установку,
    /// система завершает её в фоне — требуется интернет.</summary>
    public static async Task<bool> RestoreStoreAsync(IProgress<string>? log, CancellationToken ct = default)
    {
        log?.Report(L10n.T("winget.store.restoring"));
        var (code, output) = await RunToolAsync(
            Path.Combine(Environment.SystemDirectory, "wsreset.exe"), new[] { "-i" }, ct);
        if (code == 0)
        {
            log?.Report(L10n.T("winget.store.triggered"));
            return true;
        }
        log?.Report(L10n.T("winget.store.failed", code, output.Trim()));
        return false;
    }

    // ---------- обновление winget ----------

    /// <summary>Обновляет winget штатным механизмом: `winget upgrade Microsoft.AppInstaller`.</summary>
    public static async Task<bool> SelfUpdateAsync(IProgress<string>? log, CancellationToken ct = default)
    {
        log?.Report(L10n.T("winget.update.start"));
        await WingetService.RunAsync(
            "upgrade Microsoft.AppInstaller -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
            log, ct);
        var available = await WingetService.IsAvailableAsync();
        log?.Report(available ? L10n.T("winget.update.done") : L10n.T("winget.update.failed"));
        return available;
    }

    // ---------- установка из готового набора ----------

    public static string BuildInstallScript(string payloadDir)
    {
        var main = Escape(Path.Combine(payloadDir, PackageFiles[0]));
        var deps = string.Join(",", PackageFiles.Skip(1).Select(f => "'" + Escape(Path.Combine(payloadDir, f)) + "'"));
        return $$"""
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            function Read-Identity($path) {
                $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
                try {
                    $reader = [System.IO.StreamReader]::new($zip.GetEntry('AppxManifest.xml').Open())
                    try { return ([xml]$reader.ReadToEnd()).Package.Identity }
                    finally { $reader.Dispose() }
                } finally { $zip.Dispose() }
            }
            function Find-Installed($identity) {
                Get-AppxPackage -Name $identity.Name | Where-Object {
                    $_.Publisher -eq $identity.Publisher -and
                    $_.Architecture.ToString() -eq $identity.ProcessorArchitecture -and
                    [version]$_.Version -ge [version]$identity.Version
                } | Sort-Object Version -Descending | Select-Object -First 1
            }
            try {
                $main = '{{main}}'
                $existing = Find-Installed (Read-Identity $main)
                if ($existing) {
                    Add-AppxPackage -Register (Join-Path $existing.InstallLocation 'AppxManifest.xml') -DisableDevelopmentMode
                } else {
                    $dependencies = @({{deps}} | Where-Object { -not (Find-Installed (Read-Identity $_)) })
                    $params = @{ Path = $main; ErrorAction = 'Stop' }
                    if ($dependencies.Count -gt 0) { $params.DependencyPath = $dependencies }
                    Add-AppxPackage @params
                }
                Write-Output 'WPIC-BOOTSTRAP-OK'
            } catch {
                [Console]::Error.WriteLine($_.Exception.ToString())
                exit 1
            }
            """;
    }

    public static async Task<bool> InstallOfflineAsync(string payloadDir, IProgress<string>? log, CancellationToken ct = default)
    {
        if (!PackageFiles.All(f => File.Exists(Path.Combine(payloadDir, f)))) return false;
        log?.Report(L10n.T("winget.boot.installing"));
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildInstallScript(payloadDir)));
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException(L10n.T("winget.err.start"));
        using var registration = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        var lines = new List<string>();
        void OnLine(string? data, bool isError)
        {
            if (string.IsNullOrWhiteSpace(data)) return;
            lock (lines) lines.Add(data);
            log?.Report(isError ? "  [!] " + data : data);
        }
        process.OutputDataReceived += (_, e) => OnLine(e.Data, false);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data, true);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);
        var ok = process.ExitCode == 0 && lines.Any(l => l.Contains("WPIC-BOOTSTRAP-OK", StringComparison.Ordinal)) &&
                 await WingetService.IsAvailableAsync();
        log?.Report(ok ? L10n.T("winget.boot.done") : L10n.T("winget.boot.failed", process.ExitCode));
        return ok;
    }

    private static async Task<(int Code, string Output)> RunPowerShellAsync(string script, CancellationToken ct)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return await RunToolAsync(
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded }, ct);
    }

    private static async Task<(int Code, string Output)> RunToolAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdout + await stderr);
    }

    private static string Escape(string path) => path.Replace("'", "''");
}
