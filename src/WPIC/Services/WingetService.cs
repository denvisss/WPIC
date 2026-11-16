using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WPIC.Models;

namespace WPIC.Services;

/// <summary>
/// Работа с winget: установка, поиск, список обновлений, метаданные пакетов,
/// а также определение уже установленных программ по реестру.
/// </summary>
public static class WingetService
{
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            var (code, _) = await RunAsync("--version", null);
            return code == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------- установка ----------

    public static Task<int> InstallAsync(AppPackage app, IProgress<string>? log, CancellationToken ct = default) =>
        InstallByIdAsync(app.Name, app.WingetId, log, ct);

    public static async Task<int> InstallByIdAsync(string name, string wingetId, IProgress<string>? log, CancellationToken ct = default)
    {
        log?.Report(L10n.T("winget.log.install", name, wingetId));
        var args =
            $"install --id {Quote(wingetId)} -e --silent " +
            "--accept-package-agreements --accept-source-agreements --disable-interactivity";
        var (code, _) = await RunAsync(args, log, ct);
        log?.Report(code == 0
            ? L10n.T("winget.log.installed", name)
            : L10n.T("winget.log.exitCode", name, code));
        return code;
    }

    // ---------- поиск ----------

    public static async Task<List<SearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var variants = SearchQueryExpander.Expand(query);
        if (variants.Count == 0) return new List<SearchResult>();

        // все варианты запускаются параллельно, результаты сливаются в порядке вариантов
        var searches = variants
            .Select(v => RunAsync($"search {Quote(v)} --disable-interactivity", null, ct))
            .ToArray();

        var results = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var search in searches)
        {
            var (_, lines) = await search;
            foreach (var r in TableParser.Parse(lines))
            {
                if (!seen.Add(r.Id.Length > 0 ? r.Id : r.Name)) continue;
                results.Add(new SearchResult { Name = r.Name, Id = r.Id, Version = r.Version, Source = r.Source });
            }
        }
        return results;
    }

    // ---------- обновления ----------

    public static async Task<List<UpgradeEntry>> ListUpgradesAsync(CancellationToken ct = default)
    {
        var (_, lines) = await RunAsync("upgrade --disable-interactivity", null, ct);
        return TableParser.Parse(lines)
            .Select(r => new UpgradeEntry { Name = r.Name, Id = r.Id, Version = r.Version, Available = r.Extra })
            .ToList();
    }

    public static async Task<int> UpgradeAsync(UpgradeEntry entry, IProgress<string>? log, CancellationToken ct = default)
    {
        log?.Report(L10n.T("winget.log.upgrade", entry.Name, entry.Id, entry.Version, entry.Available));
        var args =
            $"upgrade --id {Quote(entry.Id)} -e --silent --include-unknown " +
            "--accept-package-agreements --accept-source-agreements --disable-interactivity";
        var (code, _) = await RunAsync(args, log, ct);
        log?.Report(code == 0
            ? L10n.T("winget.log.upgraded", entry.Name)
            : L10n.T("winget.log.exitCode", entry.Name, code));
        return code;
    }

    // ---------- метаданные пакета ----------

    /// <summary>Описание, издатель и домашняя страница пакета (вывод `winget show`, заголовки локализованы).</summary>
    public static async Task<PackageDetails?> ShowDetailsAsync(string packageId, CancellationToken ct = default)
    {
        var (_, lines) = await RunAsync($"show --id {Quote(packageId)} -e --disable-interactivity", null, ct);

        var details = new PackageDetails();
        var descBuffer = new StringBuilder();
        var capturingDescription = false;

        foreach (var line in lines)
        {
            // строки с отступом — продолжение многострочного описания
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                if (capturingDescription)
                {
                    var t = line.Trim();
                    if (t.Length == 0) { capturingDescription = false; continue; }
                    descBuffer.Append(descBuffer.Length > 0 ? " " : "").Append(t);
                }
                continue;
            }

            var m = Regex.Match(line, @"^(?<key>[\wА-Яа-яЁё\- ]+?):\s*(?<value>.*)$");
            if (!m.Success)
            {
                capturingDescription = false;
                continue;
            }

            var key = m.Groups["key"].Value.Trim();
            var value = m.Groups["value"].Value.Trim();

            if (key.Equals("Описание", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Description", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length > 0) descBuffer.Append(value);
                capturingDescription = value.Length == 0;
            }
            else
            {
                if (capturingDescription) capturingDescription = false;
                if (key.StartsWith("Домашняя страница", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("Homepage", StringComparison.OrdinalIgnoreCase))
                {
                    details.Homepage ??= value;
                }
                else if (key.Equals("Издатель", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("Publisher", StringComparison.OrdinalIgnoreCase))
                {
                    details.Publisher ??= value;
                }
            }
        }

        if (descBuffer.Length > 0) details.Description = descBuffer.ToString();
        return details;
    }

    // ---------- компоненты Windows ----------

    /// <summary>Включает компонент Windows через DISM (например NetFx3). Коды 0 и 3010 — успех.</summary>
    public static async Task<int> EnableWindowsFeatureAsync(string featureName, IProgress<string>? log, CancellationToken ct = default)
    {
        log?.Report(L10n.T("winget.log.feature", featureName));
        var (code, _) = await RunProcessAsync("dism.exe",
            $"/Online /Enable-Feature /FeatureName:{featureName} /All /NoRestart /English", log, ct);
        log?.Report(code is 0 or 3010
            ? L10n.T("winget.log.featureDone", featureName)
            : L10n.T("winget.log.exitCode", featureName, code));
        return code;
    }

    // ---------- запуск winget ----------

    /// <summary>Запускает winget и читает его вывод построчно (вывод собирается и передаётся в progress).</summary>
    public static Task<(int ExitCode, List<string> Lines)> RunAsync(
        string arguments, IProgress<string>? progress, CancellationToken ct = default)
    {
        var alias = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        return RunProcessAsync(System.IO.File.Exists(alias) ? alias : "winget", arguments, progress, ct);
    }

    private static async Task<(int ExitCode, List<string> Lines)> RunProcessAsync(
        string fileName, string arguments, IProgress<string>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        var lines = new List<string>();
        using var process = Process.Start(psi) ?? throw new InvalidOperationException(L10n.T("winget.err.start"));
        using var ctRegistration = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        void OnLine(string? data, bool isError)
        {
            if (string.IsNullOrWhiteSpace(data)) return;
            lock (lines) lines.Add(data);
            progress?.Report((isError ? "  [!] " : "  ") + data);
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data, false);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data, true);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, lines);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "") + "\"";

    // ---------- установленные программы ----------

    /// <summary>Идентификаторы установленных пакетов по выводу `winget list` (точное совпадение с wingetId).</summary>
    public static async Task<HashSet<string>> GetInstalledIdsAsync(CancellationToken ct = default)
    {
        try
        {
            var (_, lines) = await RunAsync("list --disable-interactivity", null, ct);
            return TableParser.Parse(lines)
                .Select(r => r.Id)
                .Where(id => id.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // winget недоступен — останется проверка по реестру
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Установлено ли приложение: по winget-идентификаторам (все пакеты записи) либо по имени в реестре Uninstall.</summary>
    public static bool IsInstalled(AppPackage pkg, HashSet<string> installedIds, HashSet<string> displayNames)
    {
        var targets = pkg.InstallTargets;
        if (targets.Count > 0 && targets.All(id => installedIds.Contains(id))) return true;
        foreach (var name in displayNames)
        {
            if (string.Equals(name, pkg.Name, StringComparison.OrdinalIgnoreCase)) return true;
            // DisplayName часто содержит суффикс версии/архитектуры: "7-Zip 26.06 (x64 edition)", "Mozilla Firefox (x64 ru)"
            if (name.Length > pkg.Name.Length &&
                name.StartsWith(pkg.Name, StringComparison.OrdinalIgnoreCase) &&
                name[pkg.Name.Length] is ' ' or '(' or '.') return true;
        }
        return false;
    }

    /// <summary>Список установленных программ по ключам реестра Uninstall.</summary>
    public static HashSet<string> GetInstalledDisplayNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };
        var bases = new[] { Registry.LocalMachine, Registry.CurrentUser };

        foreach (var baseKey in bases)
        foreach (var root in roots)
        {
            using var key = baseKey.OpenSubKey(root);
            if (key == null) continue;

            foreach (var subName in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(subName);
                if (sub == null) continue;

                var displayName = sub.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName)) continue;

                var systemComponent = sub.GetValue("SystemComponent");
                if (systemComponent is int sc && sc == 1) continue; // скрытые записи (обновления и т.п.)

                names.Add(displayName);
            }
        }
        return names;
    }
}
