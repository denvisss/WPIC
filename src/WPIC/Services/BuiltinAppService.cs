using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WPIC.Services;

public class BuiltinApp
{
    public string Name { get; set; } = "";
    public string PackageFullName { get; set; } = "";
    public string PackageFamilyName { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public bool NonRemovable { get; set; }
    public string DisplayName { get; set; } = "";
    public DateTime? InstallDate { get; set; }
    public string? IconPath { get; set; }
}

public class RemovedAppRecord
{
    public string Name { get; set; } = "";
    public string PackageFullName { get; set; } = "";
    public string PackageFamilyName { get; set; } = "";
    public DateTime RemovedUtc { get; set; }
}

public class ProvisionedPackage
{
    public string DisplayName { get; set; } = "";
    public string PackageName { get; set; } = "";
}

/// <summary>
/// Курируемый список встроенных UWP-приложений Windows: показываются только установленные
/// приложения из каталога (Appx, Main-пакеты). Удаление для текущего пользователя с проверкой
/// факта удаления, опционально вместе с provisioned-пакетом; восстановление из встроенного
/// образа Windows. Удалённые через WPIC приложения запоминаются в bloat-removed.json.
/// </summary>
public static class BuiltinAppService
{
    private static string RemovedFile => Path.Combine(LogService.DataDirectory, "bloat-removed.json");

    /// <summary>Встроенные UWP-приложения Windows 10/11 (точные имена пакетов Get-AppxPackage).</summary>
    public static readonly IReadOnlyCollection<string> Catalog = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // необязательные приложения
        "Microsoft.549981C3F5F10",
        "Microsoft.Windows.Cortana",
        "Microsoft.BingNews",
        "Microsoft.BingWeather",
        "Microsoft.BingSearch",
        "Microsoft.GetHelp",
        "Microsoft.Getstarted",
        "Microsoft.MicrosoftOfficeHub",
        "Microsoft.MicrosoftSolitaireCollection",
        "Microsoft.MixedReality.Portal",
        "Microsoft.MixedReality.OpenXR",
        "Microsoft.Office.OneNote",
        "Microsoft.People",
        "Microsoft.SkypeApp",
        "Microsoft.WindowsFeedbackHub",
        "Microsoft.WindowsMaps",
        "Microsoft.Wallet",
        "Microsoft.Xbox.TCUI",
        "Microsoft.XboxApp",
        "Microsoft.XboxGameOverlay",
        "Microsoft.XboxGamingOverlay",
        "Microsoft.XboxIdentityProvider",
        "Microsoft.XboxSpeechToTextOverlay",
        "Microsoft.YourPhone",
        "Microsoft.ZuneMusic",
        "Microsoft.ZuneVideo",
        "Microsoft.3DBuilder",
        "Microsoft.Print3D",
        "Microsoft.MSPaint",
        "Microsoft.Todos",
        "Microsoft.PowerAutomateDesktop",
        "Microsoft.Clipchamp",
        "Microsoft.OutlookForWindows",
        "Microsoft.Windows.DevHome",
        "MicrosoftCorporationII.QuickAssist",
        "MicrosoftCorporationII.MicrosoftFamily",
        "Microsoft.LinkedIn",
        "Microsoft.Windows.ParentalControls",
        "microsoft.windowscommunicationsapps",
        "Microsoft.Whiteboard",
        "Microsoft.MicrosoftJournal",
        // полезные инструменты — удаление не ломает систему, но лишает привычных функций
        "Microsoft.WindowsCalculator",
        "Microsoft.WindowsCamera",
        "Microsoft.WindowsAlarms",
        "Microsoft.WindowsSoundRecorder",
        "Microsoft.Windows.Photos",
        "Microsoft.WindowsNotepad",
        "Microsoft.WindowsTerminal",
        "Microsoft.Paint",
        "Microsoft.ScreenSketch",
        "Microsoft.MicrosoftStickyNotes",
        "Microsoft.GamingApp",
        "MicrosoftWindows.Client.WebExperience",
        "Microsoft.HEIFImageExtension",
        "Microsoft.HEVCVideoExtension",
        "Microsoft.VP9VideoExtensions",
        "Microsoft.WebpImageExtension",
        "Microsoft.RawImageExtension",
        "Microsoft.WebMediaExtensions",
        // критичные компоненты — удаление рискованно
        "Microsoft.WindowsStore",
        "Microsoft.StorePurchaseApp",
        "Microsoft.DesktopAppInstaller",
        "Microsoft.SecHealthUI",
        "Microsoft.Windows.SecHealthUI",
        "Microsoft.MicrosoftEdge",
        "Microsoft.MicrosoftEdge.Stable"
    };

    public static bool InCatalog(string packageName) => Catalog.Contains(packageName);

    /// <summary>Оставляет только приложения из каталога; из нескольких версий берёт новейшую.</summary>
    public static List<BuiltinApp> MatchCatalog(List<BuiltinApp> packages) =>
        packages.Where(p => InCatalog(p.Name))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(PackageVersion).First())
            .ToList();

    private static Version PackageVersion(BuiltinApp app)
    {
        var parts = app.PackageFullName.Split('_');
        return parts.Length > 1 && Version.TryParse(parts[1], out var version) ? version : new Version(0, 0);
    }

    public static async Task<List<BuiltinApp>> ListAsync(CancellationToken ct = default)
    {
        const string script =
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
            "Get-AppxPackage -PackageTypeFilter Main | " +
            "Select-Object Name, PackageFullName, PackageFamilyName, InstallLocation, NonRemovable | " +
            "ConvertTo-Json -Depth 3";
        var json = await RunPowerShellAsync(script, ct);
        var apps = MatchCatalog(ParsePackages(json));
        foreach (var app in apps)
        {
            var (displayName, iconPath) = ReadManifest(app.InstallLocation);
            app.DisplayName = ResolveDisplayName(app.PackageFullName, displayName) ?? FriendlyName(app);
            app.IconPath = iconPath;
            try
            {
                if (Directory.Exists(app.InstallLocation))
                    app.InstallDate = Directory.GetCreationTime(app.InstallLocation);
            }
            catch
            {
                // дата не критична
            }
        }
        return apps.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>ConvertTo-Json отдаёт объект для одного пакета и массив для нескольких.</summary>
    public static List<BuiltinApp> ParsePackages(string json)
    {
        json = json.Trim();
        if (json.Length == 0) return new List<BuiltinApp>();
        try
        {
            if (json.StartsWith("["))
                return JsonSerializer.Deserialize<List<BuiltinApp>>(json, Json.Options) ?? new List<BuiltinApp>();
            var single = JsonSerializer.Deserialize<BuiltinApp>(json, Json.Options);
            return single == null ? new List<BuiltinApp>() : new List<BuiltinApp> { single };
        }
        catch (JsonException)
        {
            return new List<BuiltinApp>();
        }
    }

    /// <summary>AppxManifest.xml: понятное имя и путь к иконке из uap:VisualElements.</summary>
    public static (string? DisplayName, string? IconPath) ReadManifest(string installLocation)
    {
        try
        {
            var manifest = Path.Combine(installLocation, "AppxManifest.xml");
            if (!File.Exists(manifest)) return (null, null);
            var doc = XDocument.Load(manifest);
            XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
            var visual = doc.Descendants(uap + "VisualElements").FirstOrDefault();
            if (visual == null) return (null, null);

            var name = (string?)visual.Attribute("DisplayName");

            var icon = (string?)visual.Attribute("Square44x44List")
                       ?? (string?)visual.Attribute("Square44x44Logo")
                       ?? (string?)visual.Attribute("Square150x150Logo");
            return (name, ResolveIcon(installLocation, icon));
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>ms-resource:… разрешается в локализованное имя через PRI пакета (shlwapi).</summary>
    public static string? ResolveDisplayName(string packageFullName, string? manifestName)
    {
        if (string.IsNullOrEmpty(manifestName)) return null;
        if (!manifestName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return manifestName;
        var candidates = new List<string> { manifestName };
        // голое имя ресурса в PRI лежит под префиксом Resources/
        if (!manifestName.Contains('/'))
            candidates.Add("ms-resource:Resources/" + manifestName["ms-resource:".Length..]);
        foreach (var candidate in candidates)
        {
            var source = "@{" + packageFullName + "? " + candidate + "}";
            var buffer = new StringBuilder(512);
            if (SHLoadIndirectString(source, buffer, buffer.Capacity, IntPtr.Zero) == 0 && buffer.Length > 0)
                return buffer.ToString();
        }
        return null;
    }

    /// <summary>Пакеты системных компонентов называются GUID — имя берётся из папки установки.</summary>
    private static string FriendlyName(BuiltinApp app)
    {
        if (!Regex.IsMatch(app.Name, "^[0-9a-fA-F]{8}-")) return Prettify(app.Name);
        var stem = Path.GetFileName(app.InstallLocation.TrimEnd('\\', '/')).Split('_')[0];
        foreach (var prefix in new[] { "Microsoft.Windows.", "Microsoft." })
            if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                stem = stem[prefix.Length..];
                break;
            }
        return Prettify(stem);
    }

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    private static string? ResolveIcon(string installLocation, string? relative)
    {
        if (string.IsNullOrEmpty(relative)) return null;
        var full = Path.Combine(installLocation, relative.Replace('/', '\\'));
        if (File.Exists(full)) return full;

        // в манифесте часто указан базовый файл, а на диске лежат scale/contrast-варианты
        try
        {
            var dir = Path.GetDirectoryName(full);
            if (dir == null || !Directory.Exists(dir)) return null;
            var baseName = Path.GetFileNameWithoutExtension(full);
            var candidates = Directory.GetFiles(dir, baseName + "*.*")
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return candidates.FirstOrDefault(f => f.Contains("scale-200", StringComparison.OrdinalIgnoreCase))
                   ?? candidates.FirstOrDefault(f => f.Contains("scale-100", StringComparison.OrdinalIgnoreCase))
                   ?? candidates.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Microsoft.WindowsCalculator → Windows Calculator.</summary>
    public static string Prettify(string packageName)
    {
        if (DisplayOverrides.TryGetValue(packageName, out var known)) return known;
        var last = packageName.Split('.').Last();
        last = Regex.Replace(last, "(?<=[a-z0-9])(?=[A-Z])", " ");
        last = Regex.Replace(last, "(?<=[A-Za-z])(?=[0-9])", " ");
        return last;
    }

    private static readonly Dictionary<string, string> DisplayOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.549981C3F5F10"] = "Cortana",
        ["Microsoft.MicrosoftEdge.Stable"] = "Microsoft Edge",
        ["Microsoft.MicrosoftEdge"] = "Microsoft Edge",
        ["MicrosoftWindows.Client.WebExperience"] = "Windows Widgets",
        ["MicrosoftCorporationII.QuickAssist"] = "Quick Assist",
        ["Microsoft.WindowsNotepad"] = "Notepad",
        ["Microsoft.WindowsTerminal"] = "Windows Terminal",
        ["Microsoft.Paint"] = "Paint",
        ["Microsoft.ScreenSketch"] = "Snipping Tool",
        ["Microsoft.Todos"] = "Microsoft To Do",
        ["Microsoft.PowerAutomateDesktop"] = "Power Automate",
        ["Microsoft.OutlookForWindows"] = "Outlook",
        ["Microsoft.Windows.DevHome"] = "Dev Home",
        ["Microsoft.BingSearch"] = "Bing Search",
        ["Microsoft.LinkedIn"] = "LinkedIn",
        ["Microsoft.Windows.Cortana"] = "Cortana",
        ["microsoft.windowscommunicationsapps"] = "Mail & Calendar",
        ["Microsoft.GamingApp"] = "Xbox",
        ["MicrosoftCorporationII.MicrosoftFamily"] = "Family Safety",
        ["Microsoft.HEIFImageExtension"] = "HEIF Image Extension",
        ["Microsoft.HEVCVideoExtension"] = "HEVC Video Extension",
        ["Microsoft.VP9VideoExtensions"] = "VP9 Video Extensions",
        ["Microsoft.WebpImageExtension"] = "Webp Image Extension",
        ["Microsoft.RawImageExtension"] = "Raw Image Extension",
        ["Microsoft.WebMediaExtensions"] = "Web Media Extensions"
    };

    /// <summary>Насколько безопасно удалять приложение: green | yellow | red.</summary>
    public static string RiskOf(string packageName)
    {
        if (Red.Contains(packageName)) return "red";
        if (Green.Contains(packageName)) return "green";
        return "yellow";
    }

    // Удаление ломает магазин, обновления приложений, безопасность или зависимости других программ.
    private static readonly HashSet<string> Red = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.WindowsStore",
        "Microsoft.StorePurchaseApp",
        "Microsoft.DesktopAppInstaller",
        "Microsoft.SecHealthUI",
        "Microsoft.Windows.SecHealthUI",
        "Microsoft.MicrosoftEdge",
        "Microsoft.MicrosoftEdge.Stable",
        "Microsoft.AAD.BrokerPlugin",
        "Microsoft.AccountsControl",
        "Microsoft.LockApp",
        "Microsoft.Win32WebViewHost",
        "Microsoft.Windows.CallingShellApp",
        "Microsoft.Windows.OOBENetworkConnectionFlow",
        "Microsoft.Windows.OOBENetworkCaptivePortal",
        "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.Search",
        "Microsoft.WindowsAppRuntime.1.0",
        "Microsoft.WindowsAppRuntime.1.1",
        "Microsoft.WindowsAppRuntime.1.2",
        "Microsoft.WindowsAppRuntime.1.3",
        "Microsoft.WindowsAppRuntime.1.4",
        "Microsoft.WindowsAppRuntime.1.5",
        "Microsoft.WindowsAppRuntime.1.6",
        "Microsoft.Services.Store.Engagement",
        // системные компоненты с GUID-именами пакетов
        "1527c705-839a-4832-9118-54d4bd6a0c89",
        "E2A4F912-2574-4A75-9BB0-0D023378592B",
        "c5e2524a-ea46-4f67-841f-6a9465d9d515",
        "F46D4000-FD22-4DB4-AC8E-4E1DDDE828FE"
    };

    // Необязательные приложения: удаление не затрагивает систему.
    private static readonly HashSet<string> Green = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.549981C3F5F10",
        "Microsoft.Windows.Cortana",
        "microsoft.windowscommunicationsapps",
        "Microsoft.Whiteboard",
        "Microsoft.MicrosoftJournal",
        "MicrosoftCorporationII.MicrosoftFamily",
        "Microsoft.BingNews",
        "Microsoft.BingWeather",
        "Microsoft.BingSearch",
        "Microsoft.GetHelp",
        "Microsoft.Getstarted",
        "Microsoft.MicrosoftOfficeHub",
        "Microsoft.MicrosoftSolitaireCollection",
        "Microsoft.MixedReality.Portal",
        "Microsoft.Office.OneNote",
        "Microsoft.People",
        "Microsoft.SkypeApp",
        "Microsoft.WindowsFeedbackHub",
        "Microsoft.WindowsMaps",
        "Microsoft.Wallet",
        "Microsoft.Xbox.TCUI",
        "Microsoft.XboxApp",
        "Microsoft.XboxGameOverlay",
        "Microsoft.XboxGamingOverlay",
        "Microsoft.XboxIdentityProvider",
        "Microsoft.XboxSpeechToTextOverlay",
        "Microsoft.YourPhone",
        "Microsoft.ZuneMusic",
        "Microsoft.ZuneVideo",
        "Microsoft.3DBuilder",
        "Microsoft.Print3D",
        "Microsoft.MSPaint",
        "Microsoft.Todos",
        "Microsoft.PowerAutomateDesktop",
        "Microsoft.Clipchamp",
        "Microsoft.OutlookForWindows",
        "Microsoft.Windows.DevHome",
        "MicrosoftCorporationII.QuickAssist",
        "Microsoft.LinkedIn",
        "Microsoft.Windows.ParentalControls",
        "Microsoft.MixedReality.OpenXR",
        "SpotifyAB.SpotifyMusic",
        "Disney.37853FC22B2CE",
        "Facebook.Facebook",
        "Facebook.Instagram",
        "BytedancePte.Ltd.TikTok",
        "Amazon.com.Amazon",
        "AdobeSystemsIncorporated.AdobePhotoshopExpress",
        "Flipboard.Flipboard",
        "PandoraMediaInc.29680B314EFC2",
        "HuluLLC.HuluPlus",
        "Netflix.Netflix",
        "king.com.CandyCrushSaga",
        "king.com.CandyCrushSodaSaga",
        "king.com.BubbleWitch3Saga",
        "DolbyLaboratories.DolbyAccess"
    };

    public static List<RemovedAppRecord> LoadRemoved()
    {
        try
        {
            if (File.Exists(RemovedFile))
                return JsonSerializer.Deserialize<List<RemovedAppRecord>>(File.ReadAllText(RemovedFile), Json.Options)
                       ?? new List<RemovedAppRecord>();
        }
        catch
        {
            // повреждённый журнал не критичен
        }
        return new List<RemovedAppRecord>();
    }

    private static void SaveRemoved(List<RemovedAppRecord> records)
    {
        Directory.CreateDirectory(LogService.DataDirectory);
        File.WriteAllText(RemovedFile, JsonSerializer.Serialize(records, Json.Options));
    }

    public static async Task RemoveAsync(BuiltinApp app, IProgress<string>? log = null, bool removeProvisioned = false, CancellationToken ct = default)
    {
        log?.Report(L10n.T("bloat.log.removing", app.DisplayName));
        var script =
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
            $"Remove-AppxPackage -Package '{Escape(app.PackageFullName)}' -ErrorAction Stop";
        await RunPowerShellAsync(script, ct);

        // Remove-AppxPackage может завершиться успешно, не удалив пакет, — проверяем факт
        var check =
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
            $"if (Get-AppxPackage | Where-Object {{ $_.PackageFullName -eq '{Escape(app.PackageFullName)}' }}) {{ 'present' }}";
        if ((await RunPowerShellAsync(check, ct)).Contains("present"))
            throw new InvalidOperationException(L10n.T("bloat.err.stillPresent"));

        var records = LoadRemoved();
        records.RemoveAll(r => r.PackageFamilyName == app.PackageFamilyName);
        records.Add(new RemovedAppRecord
        {
            Name = app.Name,
            PackageFullName = app.PackageFullName,
            PackageFamilyName = app.PackageFamilyName,
            RemovedUtc = DateTime.UtcNow
        });
        SaveRemoved(records);
        log?.Report(L10n.T("bloat.log.removed", app.DisplayName));

        if (removeProvisioned)
            await RemoveProvisionedAsync(app.Name, log, ct);
    }

    /// <summary>Удаляет provisioned-пакет, чтобы приложение не ставилось новым пользователям.</summary>
    private static async Task RemoveProvisionedAsync(string appName, IProgress<string>? log, CancellationToken ct)
    {
        try
        {
            const string listScript =
                "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
                "Get-AppxProvisionedPackage -Online | Select-Object DisplayName, PackageName | ConvertTo-Json -Depth 3";
            var match = MatchProvisioned(ParseProvisioned(await RunPowerShellAsync(listScript, ct)), appName);
            if (match == null)
            {
                log?.Report(L10n.T("bloat.log.provisionedNone", appName));
                return;
            }
            var script =
                "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
                $"Remove-AppxProvisionedPackage -Online -PackageName '{Escape(match.PackageName)}' -ErrorAction Stop | Out-Null";
            await RunPowerShellAsync(script, ct);
            log?.Report(L10n.T("bloat.log.provisionedRemoved", appName));
        }
        catch (Exception ex)
        {
            // приложение уже удалено для текущего пользователя; provisioned — дополнительная опция
            log?.Report(L10n.T("bloat.log.provisionedError", appName, ex.Message));
        }
    }

    public static List<ProvisionedPackage> ParseProvisioned(string json)
    {
        json = json.Trim();
        if (json.Length == 0) return new List<ProvisionedPackage>();
        try
        {
            if (json.StartsWith("["))
                return JsonSerializer.Deserialize<List<ProvisionedPackage>>(json, Json.Options) ?? new List<ProvisionedPackage>();
            var single = JsonSerializer.Deserialize<ProvisionedPackage>(json, Json.Options);
            return single == null ? new List<ProvisionedPackage>() : new List<ProvisionedPackage> { single };
        }
        catch (JsonException)
        {
            return new List<ProvisionedPackage>();
        }
    }

    public static ProvisionedPackage? MatchProvisioned(List<ProvisionedPackage> packages, string appName) =>
        packages.FirstOrDefault(p => string.Equals(p.DisplayName, appName, StringComparison.OrdinalIgnoreCase));

    public static async Task RestoreAsync(RemovedAppRecord record, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var display = Prettify(record.Name);
        log?.Report(L10n.T("bloat.log.restoring", display));
        var script =
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
            $"Add-AppxPackage -RegisterByFamilyName -MainPackage '{Escape(record.PackageFamilyName)}' -ErrorAction Stop";
        await RunPowerShellAsync(script, ct);
        var records = LoadRemoved();
        records.RemoveAll(r => r.PackageFamilyName == record.PackageFamilyName);
        SaveRemoved(records);
        log?.Report(L10n.T("bloat.log.restored", display));
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken ct)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
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
        using var process = Process.Start(psi) ?? throw new InvalidOperationException(L10n.T("bloat.err.psStart"));
        using var registration = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(stderr.Trim().Length > 0 ? stderr.Trim() : $"PowerShell exit code {process.ExitCode}");
        return stdout;
    }
}