using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WPIC.Services;

public sealed class NetDevice
{
    public string Name { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string[] Hwids { get; set; } = Array.Empty<string>();
    public int ErrorCode { get; set; }
    public bool IsNetClass { get; set; }
}

public enum DriverExportPhase { Drivers, Packing }

public sealed record DriverExportProgress(DriverExportPhase Phase, int Done, int Total);

/// <summary>Офлайн-набор сетевых драйверов: поиск пакета, подбор по hardware ID, установка через pnputil.</summary>
public static class DriverPackService
{
    public const string PackDirName = "DriverPack";
    private const string SevenZipSha256 = "9E681FA58614833B4DC7405C12FE1050553AA4D03298FF037777BEA131377277";

    public static bool IsAdmin =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Встроенный пак рядом с exe (full-установка); в portable его нет.</summary>
    public static bool HasBundledPack =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, PackDirName, "WPIC-Drivers.7z"));

    public static string? FindPack()
    {
        var candidates = new List<string>();
        var saved = SettingsService.Load().DriverPackPath;
        if (!string.IsNullOrWhiteSpace(saved)) candidates.Add(saved);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, PackDirName));
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType is not (DriveType.Removable or DriveType.Fixed)) continue;
            candidates.Add(Path.Combine(drive.RootDirectory.FullName, PackDirName));
            candidates.Add(drive.RootDirectory.FullName);
        }
        return candidates.FirstOrDefault(IsPack);
    }

    /// <summary>Только сетевые карты и Wi-Fi: класс Net либо устройства, чьи HWID совпали с сетевым драйвером пака.</summary>
    public static List<(NetDevice Device, IReadOnlyList<DriverRow> Candidates)> FindProblemNetDevices(
        IEnumerable<NetDevice> devices, DriverPackIndex index, bool windows11) =>
        devices.Where(d => d.ErrorCode is not (0 or 22 or 45))
            .Select(d => (Device: d, Candidates: index.Match(d.Hwids, windows11)))
            .Where(p => p.Device.IsNetClass || p.Candidates.Count > 0)
            .ToList();

    /// <summary>Курируемый пак: index.tsv рядом с архивом или внутри него.</summary>
    public static bool IsCuratedPack(string dir) =>
        Directory.Exists(dir) &&
        File.Exists(Path.Combine(dir, "WPIC-Drivers.7z"));

    /// <summary>Внешний пак драйверов: Drivers\*.7z + Index\DIA\*.txt (любой совместимый пак).</summary>
    public static bool IsExternalPack(string dir)
    {
        var drivers = Path.Combine(dir, "Drivers");
        return Directory.Exists(drivers) && Directory.EnumerateFiles(drivers, "*.7z").Any() &&
               Directory.Exists(Path.Combine(dir, "Index", "DIA"));
    }

    public static bool IsPack(string dir) => IsCuratedPack(dir) || IsExternalPack(dir);

    public static List<NetDevice> GetNetworkDevices()
    {
        var list = new List<NetDevice>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DeviceID, HardwareID, CompatibleID, ConfigManagerErrorCode, PNPClass FROM Win32_PnPEntity");
        foreach (var obj in searcher.Get())
        {
            using (obj)
            {
                var id = obj["DeviceID"] as string ?? "";
                var isNet = string.Equals(obj["PNPClass"] as string, "Net", StringComparison.OrdinalIgnoreCase);
                var code = obj["ConfigManagerErrorCode"] is uint u ? (int)u : Convert.ToInt32(obj["ConfigManagerErrorCode"] ?? 0);
                var candidate = isNet || (code != 0 && (id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ||
                                                        id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)));
                if (!candidate) continue;
                list.Add(new NetDevice
                {
                    Name = obj["Name"] as string ?? id,
                    DeviceId = id,
                    Hwids = (obj["HardwareID"] as string[] ?? Array.Empty<string>())
                        .Concat(obj["CompatibleID"] as string[] ?? Array.Empty<string>()).ToArray(),
                    ErrorCode = code,
                    IsNetClass = isNet
                });
            }
        }
        return list;
    }

    /// <summary>7-Zip для извлечения драйверов: рядом с приложением, в папке набора
    /// или извлечённый из встроенной копии (portable-сборка).</summary>
    public static string FindExtractor()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "7za64.exe"),
            Path.Combine(AppContext.BaseDirectory, PackDirName, "7za64.exe")
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate) && HasValidHash(candidate)) return candidate;

        var dest = Path.Combine(LogService.DataDirectory, "tools", "7za64.exe");
        if (!File.Exists(dest) || !HasValidHash(dest))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var stream = typeof(DriverPackService).Assembly.GetManifestResourceStream("WPIC.Tools.7za64.exe")
                               ?? throw new InvalidOperationException(L10n.T("drivers.offline.packInvalid"));
            using var file = File.Create(dest);
            stream.CopyTo(file);
        }
        return dest;
    }

    private static bool HasValidHash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file))
            .Equals(SevenZipSha256, StringComparison.Ordinal);
    }

    public static async Task<(bool Ok, bool RebootRequired, string Message)> InstallAsync(
        string packDir, DriverRow row, string deviceId, CancellationToken ct)
    {
        if (!IsAdmin || !DriverRow.IsSafeInfPath(row.InfPath))
            return (false, false, L10n.T("drivers.offline.packInvalid"));

        string seven;
        try { seven = FindExtractor(); }
        catch { return (false, false, L10n.T("drivers.offline.packInvalid")); }

        string archive, insidePath, infRelative;
        if (IsCuratedPack(packDir))
        {
            archive = Path.Combine(packDir, "WPIC-Drivers.7z");
            insidePath = row.InfDir + "\\*";
            infRelative = row.InfPath;
        }
        else if (IsExternalPack(packDir))
        {
            var pack = row.InfPath[..row.InfPath.IndexOf('\\')];
            if (!Regex.IsMatch(pack, @"^[\w.\-]+$"))
                return (false, false, L10n.T("drivers.offline.packInvalid"));
            archive = Path.Combine(packDir, "Drivers", pack + ".7z");
            if (!File.Exists(archive)) return (false, false, L10n.T("drivers.offline.packInvalid"));
            insidePath = row.InfPath[(pack.Length + 1)..];
            insidePath = insidePath[..insidePath.LastIndexOf('\\')] + "\\*";
            infRelative = row.InfPath[(pack.Length + 1)..];
        }
        else return (false, false, L10n.T("drivers.offline.packInvalid"));

        var cache = Path.Combine(LogService.DataDirectory, "driver-cache", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        try
        {
            var (extractCode, extractOut) = await RunAsync(seven,
                new[] { "x", "-y", "-o" + cache, archive, insidePath }, ct);
            if (extractCode != 0)
                return (false, false, L10n.T("drivers.offline.extractError", extractOut.Trim()));

            var inf = Path.Combine(cache, infRelative);
            if (!File.Exists(inf) || !Regex.IsMatch(await File.ReadAllTextAsync(inf, ct),
                    @"^\s*Class\s*=\s*Net\s*(?:;[^\r\n]*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                return (false, false, L10n.T("drivers.offline.packInvalid"));

            var pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
            var (installCode, installOut) = await RunAsync(pnputil, new[] { "/add-driver", inf, "/install" }, ct);
            if (installCode != 0 && installCode != 3010)
                return (false, false, L10n.T("drivers.offline.installError", installOut.Trim()));

            if (Environment.OSVersion.Version.Build >= 19041)
                await RunAsync(pnputil, new[] { "/scan-devices" }, ct);
            var device = (await Task.Run(GetNetworkDevices, ct))
                .FirstOrDefault(d => d.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
            var reboot = installCode == 3010 || device?.ErrorCode == 14;
            if (reboot) return (false, true, installOut.Trim());
            if (device is { IsNetClass: true, ErrorCode: 0 }) return (true, false, installOut.Trim());
            return (false, false, L10n.T("drivers.offline.notApplied") + " " + installOut.Trim());
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); }
            catch (IOException ex) { LogService.Log(ex.Message); }
            catch (UnauthorizedAccessException ex) { LogService.Log(ex.Message); }
        }
    }

    /// <summary>Экспорт всех сторонних (OEM) драйверов в папку: pnputil /export-driver * папка. Код 0 — успех.
    /// Прогресс считается по строкам вывода pnputil (имена oemNN.inf не зависят от языка системы);
    /// общее число берётся из /enum-drivers, 0 означает «неизвестно» (полоса будет неопределённой).</summary>
    public static async Task<int> ExportDriversAsync(string destDir, IProgress<string>? log,
        IProgress<DriverExportProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        log?.Report(L10n.T("drivers.backup.exporting"));
        var pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        var (enumCode, enumOut) = await RunAsync(pnputil, new[] { "/enum-drivers" }, ct);
        var total = enumCode == 0 ? CountOemInfs(enumOut) : 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (code, output) = await RunAsync(pnputil, new[] { "/export-driver", "*", destDir }, ct, text =>
        {
            var before = seen.Count;
            foreach (Match m in OemInf.Matches(text)) seen.Add(m.Value);
            if (seen.Count > before)
                progress?.Report(new DriverExportProgress(DriverExportPhase.Drivers, seen.Count, total));
        });
        var done = Math.Min(seen.Count, total > 0 ? total : seen.Count);
        progress?.Report(new DriverExportProgress(DriverExportPhase.Drivers, done, total > 0 ? total : done));
        if (output.Trim().Length > 0) log?.Report(output.Trim());
        return code;
    }

    private static readonly Regex OemInf = new(@"\boem\d+\.inf\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static int CountOemInfs(string text) =>
        OemInf.Matches(text).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase).Count;

    /// <summary>Восстановление драйверов из папки: pnputil /add-driver папка\*.inf /subdirs /install.</summary>
    public static async Task<int> RestoreDriversAsync(string dir, IProgress<string>? log, CancellationToken ct = default)
    {
        log?.Report(L10n.T("drivers.backup.restoring"));
        var (code, output) = await RunAsync(Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
            new[] { "/add-driver", Path.Combine(dir, "*.inf"), "/subdirs", "/install" }, ct);
        if (output.Trim().Length > 0) log?.Report(output.Trim());
        return code;
    }

    /// <summary>Запуск внешней утилиты (7za) со сбором вывода.</summary>
    public static Task<(int Code, string Output)> RunToolAsync(string exe, string[] args, CancellationToken ct = default, Action<string>? line = null) =>
        RunAsync(exe, args, ct, line);

    private static async Task<(int Code, string Output)> RunAsync(string exe, string[] args, CancellationToken ct, Action<string>? line = null)
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
        using var registration = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        var output = new StringBuilder();
        var stdout = ReadLinesAsync(process.StandardOutput, output, line, ct);
        var stderr = ReadLinesAsync(process.StandardError, output, line, ct);
        await process.WaitForExitAsync(ct);
        await Task.WhenAll(stdout, stderr);
        return (process.ExitCode, output.ToString());
    }

    private static async Task ReadLinesAsync(StreamReader reader, StringBuilder output, Action<string>? line, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } text)
        {
            lock (output) output.AppendLine(text);
            line?.Invoke(text);
        }
    }
}