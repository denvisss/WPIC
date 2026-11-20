using System.IO;
using WPIC.Services;
using Xunit;

namespace WPIC.Tests;

public class AutoDetectAndDropTests
{
    private const string Hwid = @"PCI\VEN_8086&DEV_15B8&SUBSYS_00008086&REV_00";

    private static DriverPackIndex IndexWithNic()
    {
        var index = new DriverPackIndex();
        index.Add(new DriverRow(Hwid, @"Intel\LAN\10x64\23.0\e1d.inf", "Intel", "02/13/2024,23.0", "Intel", "Ethernet"));
        return index;
    }

    private static NetDevice Device(string id, int error, bool netClass, params string[] hwids) =>
        new() { Name = id, DeviceId = id, ErrorCode = error, IsNetClass = netClass, Hwids = hwids };

    [Fact]
    public void FindProblemNetDevices_KeepsOnlyProblemNicAndWifi()
    {
        var index = IndexWithNic();
        var devices = new[]
        {
            Device("nic-missing", 28, true, Hwid),
            Device("wifi-missing", 28, false, Hwid),
            Device("nic-ok", 0, true, Hwid),
            Device("nic-disabled", 22, true, Hwid),
            Device("nic-absent", 45, true, Hwid),
            Device("gpu-missing", 28, false, @"PCI\VEN_10DE&DEV_1234"),
            Device("nic-unknown", 28, true, @"PCI\VEN_10EC&DEV_8168")
        };
        var problems = DriverPackService.FindProblemNetDevices(devices, index, windows11: false);
        Assert.Equal(new[] { "nic-missing", "wifi-missing", "nic-unknown" }, problems.Select(p => p.Device.Name));
        Assert.Single(problems[0].Candidates);
        Assert.Single(problems[1].Candidates);
        Assert.Empty(problems[2].Candidates);
    }

    [Fact]
    public void FindProblemNetDevices_EmptyWhenAllHealthy()
    {
        var problems = DriverPackService.FindProblemNetDevices(
            new[] { Device("nic-ok", 0, true, Hwid) }, IndexWithNic(), false);
        Assert.Empty(problems);
    }

    [Fact]
    public void WingetPayload_PinnedFilesHaveValidHashes()
    {
        Assert.Equal(3, WingetBootstrapService.PayloadFiles.Length);
        foreach (var (file, size, sha256) in WingetBootstrapService.PayloadFiles)
        {
            Assert.Matches(@"^[\w.\-]+(\.msix|\.appx)$", file);
            Assert.True(size > 0);
            Assert.Matches("^[0-9A-F]{64}$", sha256);
        }
        Assert.Equal(
            WingetBootstrapService.PayloadFiles.Select(p => p.File).ToArray(),
            WingetBootstrapService.PayloadFiles.Select(p => p.File).Where(f =>
                f == "AppInstaller_x64.msix" ||
                f == "Microsoft.VCLibs.x64.14.00.Desktop.appx" ||
                f == "Microsoft.UI.Xaml.2.8.x64.appx").ToArray());
    }

    [Fact]
    public void ResolvePackDrop_AcceptsPackFolderFileInsideAndNestedDriverPack()
    {
        var root = Path.Combine(Path.GetTempPath(), "wic-drop-" + Guid.NewGuid().ToString("N"));
        var pack = Path.Combine(root, "DriverPack");
        Directory.CreateDirectory(pack);
        try
        {
            File.WriteAllText(Path.Combine(pack, "index.tsv"), "");
            File.WriteAllText(Path.Combine(pack, "WPIC-Drivers.7z"), "");
            var fileInside = Path.Combine(pack, "index.tsv");

            Assert.Equal(pack, MainWindow.ResolvePackDrop(pack));
            Assert.Equal(pack, MainWindow.ResolvePackDrop(fileInside));
            Assert.Equal(pack, MainWindow.ResolvePackDrop(root));

            var empty = Path.Combine(Path.GetTempPath(), "wic-empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(empty);
            try { Assert.Null(MainWindow.ResolvePackDrop(empty)); }
            finally { Directory.Delete(empty); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}