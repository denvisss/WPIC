using System.IO;
using System.Security.Cryptography;
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

    [Theory]
    [InlineData("AppInstaller_x64.msix", "0E69BF5F2BCB014ADC1688F0036BC6E9BCEF779831BF6E4FC2435CB062099D85")]
    [InlineData("Microsoft.VCLibs.x64.14.00.Desktop.appx", "B56A9101F706F9D95F815F5B7FA6EFBAC972E86573D378B96A07CFF5540C5961")]
    [InlineData("Microsoft.UI.Xaml.2.8.x64.appx", "249D2AFB41CC009494841372BD6DD2DF46F87386D535DDF8D9F32C97226D2E46")]
    public void EmbeddedWinget_PackagesAreBundledWithPinnedHashes(string file, string sha256)
    {
        using var stream = typeof(DriverPackService).Assembly.GetManifestResourceStream("WPIC.Winget." + file);
        Assert.NotNull(stream);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(stream!)));
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