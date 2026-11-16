using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WPIC.Models;

namespace WPIC.Services;

public sealed class TweakService
{
    private readonly string _stateFile;
    private readonly string _originalsFile;
    private readonly ISystemParameters _systemParameters;
    private readonly HashSet<string> _applied;
    public string BackupDirectory { get; }

    public TweakService(string dataDirectory, ISystemParameters? systemParameters = null)
    {
        BackupDirectory = Path.Combine(dataDirectory, "backup");
        _stateFile = Path.Combine(dataDirectory, "state.json");
        _originalsFile = Path.Combine(dataDirectory, "tweak-originals.json");
        _systemParameters = systemParameters ?? new WindowsSystemParameters();
        try
        {
            _applied = ReadJson<HashSet<string>>(_stateFile);
        }
        catch (JsonException)
        {
            _applied = new HashSet<string>();
        }
    }

    public static int CurrentBuild => Environment.OSVersion.Version.Build;
    public static string CurrentEdition { get; } = ReadEdition();

    private static string ReadEdition()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return key?.GetValue("EditionID") as string ?? "";
    }

    public static bool IsInBuildRange(int build, int min, int max) => build >= min && (max == 0 || build <= max);

    public static bool IsSupported(Tweak tweak, int build, string edition) =>
        IsInBuildRange(build, tweak.MinBuild, tweak.MaxBuild) &&
        (tweak.Editions.Length == 0 || tweak.Editions.Contains(edition, StringComparer.OrdinalIgnoreCase)) &&
        ActionsForBuild(tweak, build).Any();

    public static IEnumerable<TweakAction> ActionsForBuild(Tweak tweak, int build) =>
        tweak.Actions.Where(a => IsInBuildRange(build, a.MinBuild, a.MaxBuild));

    public static string Compatibility(Tweak tweak)
    {
        var windows = tweak.MinBuild >= 22000 ? "Windows 11" : "Windows 10 / 11";
        if (tweak.Editions.Length > 0) windows += " Pro / Enterprise / Education";
        if (tweak.MinBuild > 17763) windows += L10n.T("tweaks.build", tweak.MinBuild);
        return windows;
    }

    public string GetStatus(Tweak tweak)
    {
        if (!IsSupported(tweak, CurrentBuild, CurrentEdition)) return L10n.T("tweaks.status.unsupported");
        try
        {
            var actions = ActionsForBuild(tweak, CurrentBuild).Where(a => a.Type != "command").ToList();
            if (actions.Count == 0)
                return L10n.T(_applied.Contains(tweak.Id) ? "tweaks.status.appliedByLog" : "tweaks.status.notApplied");
            var matched = actions.Count(a => a.Type switch
            {
                "registry" => IsRegistryApplied(a),
                "systemParameter" => WindowsSystemParameters.IsDisabled(a.Parameter, _systemParameters.Read(a.Parameter)),
                _ => throw new InvalidOperationException(L10n.T("tweak.log.unknownType", a.Type))
            });
            if (matched == 0) return L10n.T("tweaks.status.notApplied");
            return matched == actions.Count ? L10n.T("tweaks.status.applied") : L10n.T("tweaks.status.partial", matched, actions.Count);
        }
        catch (Exception ex)
        {
            return L10n.T("tweaks.status.error", ex.Message);
        }
    }

    private static bool IsRegistryApplied(TweakAction action)
    {
        using var key = OpenKey(action.Key);
        var current = key?.GetValue(action.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (current == null || key!.GetValueKind(action.ValueName) != Kind(action.RegType)) return false;
        return SerializeValue(current) == SerializeValue(ToValue(action.Value, action.RegType));
    }

    public async Task ApplyAsync(Tweak tweak, string backupDir, IProgress<string>? log)
    {
        EnsureSupported(tweak);
        var actions = ActionsForBuild(tweak, CurrentBuild).ToList();
        foreach (var action in actions.Where(a => a.Type == "registry"))
        {
            _ = Kind(action.RegType);
            _ = ToValue(action.Value, action.RegType);
        }
        var snapshots = actions.Where(a => a.Type != "command").Select(a => Capture(tweak, a)).ToList();
        var originals = ReadJson<Dictionary<string, List<RegistryBackupEntry>>>(_originalsFile);
        if (snapshots.Count > 0)
        {
            var backupFile = Path.Combine(backupDir, "registry-backup.json");
            var backup = ReadJson<List<RegistryBackupEntry>>(backupFile);
            backup.AddRange(snapshots);
            SaveJson(backupFile, backup);
            if (!originals.ContainsKey(tweak.Id))
            {
                originals.Add(tweak.Id, snapshots);
                SaveJson(_originalsFile, originals);
            }
        }
        foreach (var action in actions)
        {
            switch (action.Type)
            {
                case "registry":
                    using (var key = OpenKey(action.Key, writable: true, create: true)!)
                        key.SetValue(action.ValueName, ToValue(action.Value, action.RegType), Kind(action.RegType));
                    log?.Report(L10n.T("tweak.log.registrySet", action.Key, action.ValueName, action.Value));
                    break;
                case "systemParameter":
                    _systemParameters.Write(action.Parameter,
                        WindowsSystemParameters.DisabledState(action.Parameter, _systemParameters.Read(action.Parameter)));
                    log?.Report(L10n.T("tweak.log.parameterSet", action.Parameter));
                    break;
                case "command":
                    await RunCommandAsync(action.Command, log);
                    break;
                default:
                    throw new InvalidOperationException(L10n.T("tweak.log.unknownType", action.Type));
            }
        }
        _applied.Add(tweak.Id);
        SaveJson(_stateFile, _applied);
    }

    public async Task UndoAsync(Tweak tweak, IProgress<string>? log)
    {
        EnsureSupported(tweak);
        var actions = ActionsForBuild(tweak, CurrentBuild).ToList();
        var originals = ReadJson<Dictionary<string, List<RegistryBackupEntry>>>(_originalsFile);
        if (actions.Any(a => a.Type != "command"))
        {
            if (!originals.TryGetValue(tweak.Id, out var entries) || entries.Count == 0)
                throw new InvalidOperationException(L10n.T("tweak.err.originalMissing", tweak.Name));
            RestoreEntries(entries, log);
        }
        foreach (var action in actions.Where(a => a.Type == "command"))
        {
            if (string.IsNullOrWhiteSpace(action.UndoCommand))
                throw new InvalidOperationException(L10n.T("tweak.log.undoMissing", tweak.Id, action.Command));
            await RunCommandAsync(action.UndoCommand, log);
        }
        originals.Remove(tweak.Id);
        SaveJson(_originalsFile, originals);
        _applied.Remove(tweak.Id);
        SaveJson(_stateFile, _applied);
    }

    private RegistryBackupEntry Capture(Tweak tweak, TweakAction action)
    {
        var entry = new RegistryBackupEntry
        {
            Tweak = tweak.Id, Type = action.Type, Parameter = action.Parameter,
            Key = action.Key, ValueName = action.ValueName, RegType = action.RegType,
            MinBuild = Math.Max(tweak.MinBuild, action.MinBuild),
            MaxBuild = action.MaxBuild == 0 ? tweak.MaxBuild :
                tweak.MaxBuild == 0 ? action.MaxBuild : Math.Min(tweak.MaxBuild, action.MaxBuild)
        };
        if (action.Type == "systemParameter")
        {
            entry.Existed = true;
            entry.Value = _systemParameters.Read(action.Parameter);
        }
        else if (action.Type == "registry")
        {
            using var key = OpenKey(action.Key);
            var current = key?.GetValue(action.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            entry.Existed = current != null;
            if (current != null)
            {
                entry.RegType = key!.GetValueKind(action.ValueName).ToString();
                entry.Value = SerializeValue(current);
            }
        }
        else throw new InvalidOperationException(L10n.T("tweak.log.unknownType", action.Type));
        return entry;
    }

    public int RestoreBackup(string backupDir, IProgress<string>? log)
    {
        var file = Path.Combine(backupDir, "registry-backup.json");
        if (!File.Exists(file)) throw new FileNotFoundException(L10n.T("tweak.err.backupNotFound", file));
        var entries = ReadJson<List<RegistryBackupEntry>>(file);
        var count = RestoreEntries(entries, log);
        var originals = ReadJson<Dictionary<string, List<RegistryBackupEntry>>>(_originalsFile);
        foreach (var id in entries.Select(e => e.Tweak).Where(id => id.Length > 0).Distinct())
        {
            originals.Remove(id);
            _applied.Remove(id);
        }
        SaveJson(_originalsFile, originals);
        SaveJson(_stateFile, _applied);
        return count;
    }

    private int RestoreEntries(List<RegistryBackupEntry> entries, IProgress<string>? log)
    {
        var unique = entries.DistinctBy(e => e.Type == "systemParameter" ? "SPI:" + e.Parameter :
            $"REG:{Split(e.Key)}\\{e.ValueName}", StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var entry in unique)
        {
            if (!IsInBuildRange(CurrentBuild, entry.MinBuild, entry.MaxBuild))
                throw new InvalidOperationException(L10n.T("tweaks.status.unsupported"));
            if (entry.Type == "systemParameter")
                _ = WindowsSystemParameters.DisabledState(entry.Parameter, entry.Value ?? "");
            else if (entry.Type != "registry")
                throw new InvalidOperationException(L10n.T("tweak.log.unknownType", entry.Type));
            else if (entry.Existed)
                _ = ToValue(entry.Value ?? throw new InvalidDataException(L10n.T("tweak.err.invalidSnapshot")), entry.RegType);
        }
        foreach (var entry in unique)
        {
            if (entry.Type == "systemParameter")
            {
                _systemParameters.Write(entry.Parameter, entry.Value!);
                log?.Report(L10n.T("tweak.log.parameterRestored", entry.Parameter));
            }
            else if (entry.Existed)
            {
                using var key = OpenKey(entry.Key, writable: true, create: true)!;
                key.SetValue(entry.ValueName, ToValue(entry.Value!, entry.RegType), Kind(entry.RegType));
                log?.Report(L10n.T("tweak.log.restoreSet", entry.Key, entry.ValueName, entry.Value));
            }
            else
            {
                using var key = OpenKey(entry.Key, writable: true);
                key?.DeleteValue(entry.ValueName, throwOnMissingValue: false);
                log?.Report(L10n.T("tweak.log.restoreDeleted", entry.Key, entry.ValueName));
            }
        }
        return unique.Count;
    }

    private static void EnsureSupported(Tweak tweak)
    {
        if (!IsSupported(tweak, CurrentBuild, CurrentEdition))
            throw new InvalidOperationException(L10n.T("tweaks.status.unsupported"));
    }

    private static async Task RunCommandAsync(string command, IProgress<string>? log)
    {
        log?.Report(L10n.T("tweak.log.command", command));
        var psi = new ProcessStartInfo("cmd.exe", $"/c {command}")
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException(L10n.T("tweak.err.cmdStart"));
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Report("  " + e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Report("  [!] " + e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(L10n.T("tweak.err.commandExit", process.ExitCode, command));
    }

    private static RegistryValueKind Kind(string type) => type switch
    {
        "DWord" => RegistryValueKind.DWord, "QWord" => RegistryValueKind.QWord,
        "String" => RegistryValueKind.String, "ExpandString" => RegistryValueKind.ExpandString,
        "Binary" => RegistryValueKind.Binary, "None" => RegistryValueKind.None,
        "MultiString" => RegistryValueKind.MultiString,
        _ => throw new ArgumentException(L10n.T("tweak.err.registryType", type))
    };

    private static object ToValue(string raw, string type) => type switch
    {
        "DWord" => unchecked((int)ToLong(raw)), "QWord" => ToLong(raw),
        "Binary" or "None" => Convert.FromHexString(string.Concat(raw.Split(' ', '-'))),
        "MultiString" => JsonSerializer.Deserialize<string[]>(raw) ?? throw new InvalidDataException(L10n.T("tweak.err.invalidSnapshot")),
        "String" or "ExpandString" => raw,
        _ => throw new ArgumentException(L10n.T("tweak.err.registryType", type))
    };

    private static string SerializeValue(object value) => value switch
    {
        byte[] bytes => Convert.ToHexString(bytes),
        string[] strings => JsonSerializer.Serialize(strings),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!
    };

    private static long ToLong(string raw) => raw.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToInt64(raw.Trim()[2..], 16) : long.Parse(raw, CultureInfo.InvariantCulture);

    private static (string Root, string SubKey) Split(string path)
    {
        var i = path.IndexOf('\\');
        if (i <= 0 || i == path.Length - 1) throw new ArgumentException(L10n.T("tweak.err.registryPath", path));
        var root = path[..i].ToUpperInvariant() switch
        {
            "HKEY_LOCAL_MACHINE" or "HKLM" => "HKLM",
            "HKEY_CURRENT_USER" or "HKCU" => "HKCU",
            "HKEY_CLASSES_ROOT" or "HKCR" => "HKCR",
            _ => throw new ArgumentException(L10n.T("tweak.err.registryPath", path))
        };
        return (root, path[(i + 1)..]);
    }

    private static RegistryKey? OpenKey(string path, bool writable = false, bool create = false)
    {
        var (root, sub) = Split(path);
        var hive = root switch
        {
            "HKLM" => Registry.LocalMachine, "HKCU" => Registry.CurrentUser, _ => Registry.ClassesRoot
        };
        return create ? hive.CreateSubKey(sub, writable: true) : hive.OpenSubKey(sub, writable);
    }

    private static T ReadJson<T>(string file) where T : new() => File.Exists(file)
        ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json.Options) ?? throw new InvalidDataException(L10n.T("tweak.err.invalidSnapshot"))
        : new T();

    private static void SaveJson<T>(string file, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json.Options));
        File.Move(temporary, file, overwrite: true);
    }
}
