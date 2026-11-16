using System.Diagnostics;

namespace WPIC.Services;

/// <summary>Создание точки восстановления системы перед опасными операциями.</summary>
public static class RestorePointService
{
    public static async Task<bool> CreateAsync(IProgress<string>? log)
    {
        log?.Report(L10n.T("rp.log.creating"));
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"Checkpoint-Computer -Description 'WPIC backup' -RestorePointType MODIFY_SETTINGS\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(psi);
        if (process == null)
        {
            log?.Report(L10n.T("rp.err.start"));
            return false;
        }

        // оба потока читаются параллельно, иначе возможен дедлок при заполнении буфера stderr
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorsTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var errors = await errorsTask;

        if (!string.IsNullOrWhiteSpace(output)) log?.Report(output.Trim());

        if (process.ExitCode == 0)
        {
            log?.Report(L10n.T("rp.log.created"));
            return true;
        }

        log?.Report(L10n.T("rp.log.failed", process.ExitCode));
        if (!string.IsNullOrWhiteSpace(errors)) log?.Report("  " + errors.Trim());
        log?.Report(L10n.T("rp.log.hint"));
        return false;
    }
}