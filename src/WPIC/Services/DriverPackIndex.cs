using System.Globalization;
using System.IO;

namespace WPIC.Services;

public sealed record DriverRow(
    string Hwid,
    string InfPath,
    string Section,
    string DateVersion,
    string Manufacturer,
    string DeviceName)
{
    public string OsFolder => InfPath.Contains("\\11x64\\", StringComparison.OrdinalIgnoreCase) ? "11x64" : "10x64";
    public string InfDir => InfPath[..InfPath.LastIndexOf('\\')];
    public string Version => DateVersion.Contains(',') ? DateVersion[(DateVersion.IndexOf(',') + 1)..].Trim() : "";
    public System.Version VersionNumber => System.Version.TryParse(Version, out var version) ? version : new(0, 0);
    public int DateKey => DateTime.TryParseExact(DateVersion.Split(',')[0], "MM/dd/yyyy",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date.Year * 10000 + date.Month * 100 + date.Day : 0;

    public static bool IsSafeInfPath(string path)
    {
        if (Path.IsPathRooted(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            path.Contains('/') || path.Contains(':') || path.Contains('*') || path.Contains('?')) return false;
        var parts = path.Split('\\');
        return parts.Length >= 3 && parts.All(p => p.Length > 0 && p != "." && p != ".." &&
            !p.EndsWith(' ') && !p.EndsWith('.')) &&
            parts.Any(p => p.Equals("10x64", StringComparison.OrdinalIgnoreCase) || p.Equals("11x64", StringComparison.OrdinalIgnoreCase)) &&
            path.EndsWith(".inf", StringComparison.OrdinalIgnoreCase);
    }

    public static DriverRow? TryParse(string line)
    {
        var f = line.Split('\t');
        if (f.Length < 7 || !IsSafeInfPath(f[1])) return null;
        if (!f[0].StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) &&
            !f[0].StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)) return null;
        return new DriverRow(f[0], f[1], f[2], f[3], f[4], f[6]);
    }

    public static string NormalizeHwid(string hwid) => string.Join("&", hwid.Split('&')
        .Where(p => !p.StartsWith("SUBSYS_", StringComparison.OrdinalIgnoreCase) &&
                    !p.StartsWith("REV_", StringComparison.OrdinalIgnoreCase))).ToUpperInvariant();
}

public sealed class DriverPackIndex
{
    private readonly Dictionary<string, List<DriverRow>> _rows = new(StringComparer.OrdinalIgnoreCase);
    public int Count => _rows.Count;

    public void Add(DriverRow row)
    {
        if (!DriverRow.IsSafeInfPath(row.InfPath)) return;
        var key = row.Hwid + "|" + row.OsFolder;
        if (!_rows.TryGetValue(key, out var list)) _rows[key] = list = new();
        if (!list.Contains(row)) list.Add(row);
    }

    public IReadOnlyList<DriverRow> Match(IEnumerable<string> deviceHwids, bool windows11)
    {
        var hwids = deviceHwids.ToArray();
        // Only the device ID is generalized; OEM-specific index rows must never match another OEM.
        foreach (var hwid in hwids.Concat(hwids.Select(DriverRow.NormalizeHwid)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidates = new List<DriverRow>();
            foreach (var os in windows11 ? new[] { "11x64", "10x64" } : new[] { "10x64" })
            {
                if (!_rows.TryGetValue(hwid + "|" + os, out var rows)) continue;
                candidates.AddRange(rows.OrderByDescending(r => r.DateKey).ThenByDescending(r => r.VersionNumber)
                    .DistinctBy(r => (r.DateKey, r.VersionNumber)).Take(1));
            }
            if (candidates.Count > 0) return candidates.DistinctBy(r => r.InfPath, StringComparer.OrdinalIgnoreCase).ToList();
        }
        return Array.Empty<DriverRow>();
    }

    /// <summary>Оставляет только новейшую версию драйвера для каждого устройства и ОС.</summary>
    public void PruneToLatest()
    {
        foreach (var key in _rows.Keys.ToList())
        {
            var best = _rows[key].MaxBy(r => (r.DateKey, r.VersionNumber))!;
            _rows[key] = _rows[key]
                .Where(r => r.DateKey == best.DateKey && r.VersionNumber == best.VersionNumber)
                .DistinctBy(r => r.InfPath, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public static DriverPackIndex Load(string path)
    {
        var index = new DriverPackIndex();
        foreach (var line in File.ReadLines(path))
        {
            var row = DriverRow.TryParse(line);
            if (row != null) index.Add(row);
        }
        return index;
    }

    /// <summary>Курируемый пак: index.tsv лежит рядом с WPIC-Drivers.7z или внутри архива.</summary>
    public static async Task<DriverPackIndex> LoadCuratedAsync(
        string dir, string sevenZip, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var archive = Path.Combine(dir, "WPIC-Drivers.7z");
        if (!File.Exists(archive)) throw new FileNotFoundException(L10n.T("drivers.offline.packInvalid"));
        var external = Path.Combine(dir, "index.tsv");
        var index = new DriverPackIndex();
        var added = 0;
        IEnumerable<string> lines = File.Exists(external)
            ? File.ReadLines(external)
            : await ReadEntryLinesAsync(sevenZip, new[] { "e", "-y", "-so", archive, "index.tsv" }, null, ct);
        foreach (var line in lines)
        {
            var row = DriverRow.TryParse(line);
            if (row == null) continue;
            index.Add(row);
            added++;
        }
        log?.Report(L10n.T("drivers.offline.indexPack", "WPIC-Drivers", added));
        return index;
    }

    /// <summary>Индекс из внешнего пака драйверов (Drivers\*.7z + Index\DIA\*.txt).
    /// Путь INF получает префикс имени пака для маршрутизации извлечения; остаются только новейшие версии.</summary>
    public static async Task<DriverPackIndex> LoadExternalAsync(
        string dir, string sevenZip, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var index = new DriverPackIndex();
        foreach (var pack in Directory.GetFiles(Path.Combine(dir, "Drivers"), "*.7z"))
        {
            var name = Path.GetFileNameWithoutExtension(pack);
            var dia = Path.Combine(dir, "Index", "DIA", name + ".txt");
            if (!File.Exists(dia))
            {
                log?.Report(L10n.T("drivers.offline.indexMissing", name));
                continue;
            }
            var lines = await ReadIndexLinesAsync(sevenZip, dia, ct);
            var added = 0;
            foreach (var line in lines)
            {
                var row = DriverRow.TryParse(line);
                if (row == null) continue;
                index.Add(new DriverRow(row.Hwid, name + "\\" + row.InfPath,
                    row.Section, row.DateVersion, row.Manufacturer, row.DeviceName));
                added++;
            }
            log?.Report(L10n.T("drivers.offline.indexPack", name, added));
        }
        index.PruneToLatest();
        return index;
    }

    /// <summary>DIA-индекс внешнего пака хранится как 7z-сжатый TSV; при неудаче читается как обычный текст.</summary>
    private static async Task<List<string>> ReadIndexLinesAsync(string sevenZip, string dia, CancellationToken ct) =>
        await ReadEntryLinesAsync(sevenZip, new[] { "e", "-y", "-so", dia }, dia, ct);

    private static async Task<List<string>> ReadEntryLinesAsync(string sevenZip, string[] args, string? fallbackFile, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(sevenZip)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in args) psi.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(psi)
                            ?? throw new InvalidOperationException(L10n.T("drivers.err.title"));
        var read = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var text = await read;
        if (process.ExitCode != 0 || text.Length == 0)
        {
            await error;
            if (fallbackFile == null) return new List<string>();
            try { text = await File.ReadAllTextAsync(fallbackFile, ct); }
            catch { return new List<string>(); }
        }
        return text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
    }
}
