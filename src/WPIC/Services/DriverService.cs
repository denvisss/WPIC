using System.Diagnostics;
using System.Management;

namespace WPIC.Services;

public class DriverInfo
{
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Version { get; set; } = "";
    public string Date { get; set; } = "";
}

public class DeviceProblem
{
    public string Name { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string ErrorCode { get; set; } = "";
}

/// <summary>Информация о драйверах и устройствах через WMI.</summary>
public static class DriverService
{
    public static List<DriverInfo> GetInstalledDrivers()
    {
        var list = new List<DriverInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceName, Manufacturer, DriverVersion, DriverDate FROM Win32_PnPSignedDriver");
        foreach (var obj in searcher.Get())
        {
            using (obj)
            {
                var name = obj["DeviceName"] as string;
                var version = obj["DriverVersion"] as string;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version)) continue;

                var date = "";
                if (obj["DriverDate"] is string rawDate)
                {
                    try
                    {
                        var parsed = ManagementDateTimeConverter.ToDateTime(rawDate);
                        date = parsed.ToString("dd.MM.yyyy");
                    }
                    catch
                    {
                        // нет даты — не страшно
                    }
                }

                list.Add(new DriverInfo
                {
                    Name = name,
                    Manufacturer = obj["Manufacturer"] as string ?? "",
                    Version = version,
                    Date = date
                });
            }
        }
        return list.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<DeviceProblem> GetProblemDevices()
    {
        var list = new List<DeviceProblem>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
        foreach (var obj in searcher.Get())
        {
            using (obj)
            {
                var code = Convert.ToString(obj["ConfigManagerErrorCode"]) ?? "?";
                list.Add(new DeviceProblem
                {
                    Name = obj["Name"] as string ?? L10n.T("drivers.noName"),
                    DeviceId = obj["DeviceID"] as string ?? "",
                    ErrorCode = $"{code} — {DescribeCode(code)}"
                });
            }
        }
        return list;
    }

    private static string DescribeCode(string code) => code switch
    {
        "1" => L10n.T("drivers.code.1"),
        "3" => L10n.T("drivers.code.3"),
        "10" => L10n.T("drivers.code.10"),
        "12" => L10n.T("drivers.code.12"),
        "18" => L10n.T("drivers.code.18"),
        "22" => L10n.T("drivers.code.22"),
        "28" => L10n.T("drivers.code.28"),
        "31" => L10n.T("drivers.code.31"),
        "37" => L10n.T("drivers.code.37"),
        "39" => L10n.T("drivers.code.39"),
        "43" => L10n.T("drivers.code.43"),
        "45" => L10n.T("drivers.code.45"),
        "52" => L10n.T("drivers.code.52"),
        _ => L10n.T("drivers.code.other")
    };
}
