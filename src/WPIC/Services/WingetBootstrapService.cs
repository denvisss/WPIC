using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace WPIC.Services;

public static class WingetBootstrapService
{
    public const string PayloadDirName = "winget";
    private static readonly (string File, string Sha256)[] EmbeddedPackages =
    {
        ("AppInstaller_x64.msix", "0E69BF5F2BCB014ADC1688F0036BC6E9BCEF779831BF6E4FC2435CB062099D85"),
        ("Microsoft.VCLibs.x64.14.00.Desktop.appx", "B56A9101F706F9D95F815F5B7FA6EFBAC972E86573D378B96A07CFF5540C5961"),
        ("Microsoft.UI.Xaml.2.8.x64.appx", "249D2AFB41CC009494841372BD6DD2DF46F87386D535DDF8D9F32C97226D2E46")
    };
    private static readonly string[] PackageFiles = EmbeddedPackages.Select(p => p.File).ToArray();

    /// <summary>Пак рядом с exe (установленная версия) либо распакованный из ресурсов в %LOCALAPPDATA%\WPIC\winget.</summary>
    public static string? FindPayload()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, PayloadDirName);
        if (PackageFiles.All(f => File.Exists(Path.Combine(dir, f)))) return dir;
        return ExtractEmbeddedPayload();
    }

    private static string? _extractedCache;
    private static readonly object ExtractLock = new();

    private static string? ExtractEmbeddedPayload()
    {
        lock (ExtractLock)
        {
            if (_extractedCache != null) return _extractedCache;
            var cache = Path.Combine(LogService.DataDirectory, PayloadDirName);
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var (file, sha256) in EmbeddedPackages)
            {
                var target = Path.Combine(cache, file);
                if (File.Exists(target) && HashMatches(target, sha256)) continue;
                using var source = assembly.GetManifestResourceStream("WPIC.Winget." + file);
                if (source == null) return null;
                Directory.CreateDirectory(cache);
                var temp = target + ".tmp";
                using (var output = File.Create(temp)) source.CopyTo(output);
                if (!HashMatches(temp, sha256))
                {
                    File.Delete(temp);
                    return null;
                }
                File.Move(temp, target, overwrite: true);
            }
            _extractedCache = cache;
            return cache;
        }
    }

    private static bool HashMatches(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.Ordinal);
    }

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

    private static string Escape(string path) => path.Replace("'", "''");
}
