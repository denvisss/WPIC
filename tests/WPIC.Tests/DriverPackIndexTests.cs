using System.IO;
using WPIC.Services;
using Xunit;

namespace WPIC.Tests;

public class DriverPackIndexTests
{
    private const string Hwid = @"PCI\VEN_8086&DEV_15B8&SUBSYS_00008086&REV_00";
    private const string HwidNorm = @"PCI\VEN_8086&DEV_15B8";

    private static DriverRow Row(string hwid, string os, string date, string version, string inf = "e1d.inf") =>
        new(hwid, $@"Intel\LAN\{os}\{version}\{inf}", "Intel", $"{date},{version}", "Intel", "Ethernet Connection");

    [Fact]
    public void TryParse_ReadsFieldsAndOsFolder()
    {
        var line = string.Join("\t", Hwid, @"Intel\LAN\11x64\23.0\e1d.inf", "Intel", "02/13/2024,23.0.1", "Intel", "0", "Ethernet");
        var row = DriverRow.TryParse(line);
        Assert.NotNull(row);
        Assert.Equal(Hwid, row!.Hwid);
        Assert.Equal("11x64", row.OsFolder);
        Assert.Equal("23.0.1", row.Version);
        Assert.Equal(20240213, row.DateKey);
        Assert.Equal(@"Intel\LAN\11x64\23.0", row.InfDir);
    }

    [Theory]
    [InlineData("too\tfew\tfields")]
    [InlineData(@"SW\{guid}" + "\t" + @"x\10x64\a.inf" + "\ts\t01/01/2024,1\tm\t0\tname")]
    [InlineData(@"PCI\VEN_1&DEV_2" + "\tnopath\tsect\t01/01/2024,1\tm\t0\tname")]
    public void TryParse_RejectsInvalidLines(string line) => Assert.Null(DriverRow.TryParse(line));

    [Fact]
    public void NormalizeHwid_DropsSubsysAndRev()
    {
        Assert.Equal(HwidNorm, DriverRow.NormalizeHwid(Hwid));
        Assert.Equal(HwidNorm, DriverRow.NormalizeHwid(HwidNorm.ToLowerInvariant()));
    }

    [Fact]
    public void Match_ReturnsOnlyTheNewestVersion()
    {
        var index = new DriverPackIndex();
        index.Add(Row(Hwid, "10x64", "01/10/2023", "22.0"));
        index.Add(Row(Hwid, "10x64", "02/13/2024", "23.0"));
        var candidates = index.Match(new[] { Hwid }, windows11: false);
        Assert.Single(candidates);
        Assert.Equal("23.0", candidates[0].Version);
    }

    [Fact]
    public void PruneToLatest_KeepsOnlyNewestVersionPerDevice()
    {
        var index = new DriverPackIndex();
        index.Add(Row(Hwid, "10x64", "01/10/2023", "22.0"));
        index.Add(Row(Hwid, "10x64", "02/13/2024", "23.0"));
        index.Add(Row(Hwid, "11x64", "03/01/2025", "24.0"));
        index.Add(Row(HwidNorm, "10x64", "01/01/2022", "21.0"));
        index.PruneToLatest();
        // OS fallback stays (one newest driver per OS); versions inside an OS are collapsed
        Assert.Equal(new[] { "24.0", "23.0" }, index.Match(new[] { Hwid }, true).Select(r => r.Version));
        Assert.Equal(new[] { "23.0" }, index.Match(new[] { Hwid }, false).Select(r => r.Version));
        Assert.Equal(new[] { "21.0" }, index.Match(new[] { HwidNorm }, false).Select(r => r.Version));
    }

    [Fact]
    public void Match_Windows11_Prefers11x64EvenIf10x64Newer()
    {
        var index = new DriverPackIndex();
        index.Add(Row(Hwid, "10x64", "05/01/2025", "24.0"));
        index.Add(Row(Hwid, "11x64", "01/01/2024", "23.0"));
        var candidates = index.Match(new[] { Hwid }, windows11: true);
        Assert.Equal("11x64", candidates[0].OsFolder);
    }

    [Fact]
    public void Match_Windows10_Ignores11x64OnlyDrivers()
    {
        var index = new DriverPackIndex();
        index.Add(Row(Hwid, "11x64", "01/01/2024", "23.0"));
        Assert.Empty(index.Match(new[] { Hwid }, windows11: false));
    }

    [Fact]
    public void Match_FallsBackOnlyToDeclaredGenericHwid()
    {
        var index = new DriverPackIndex();
        index.Add(Row(HwidNorm, "10x64", "02/13/2024", "23.0"));
        var other = @"PCI\VEN_8086&DEV_15B8&SUBSYS_11111111&REV_01";
        var candidates = index.Match(new[] { other }, windows11: false);
        Assert.Single(candidates);
        Assert.Equal("23.0", candidates[0].Version);
    }

    [Fact]
    public void Match_UnknownDevice_ReturnsEmpty()
    {
        var index = new DriverPackIndex();
        index.Add(Row(Hwid, "10x64", "02/13/2024", "23.0"));
        Assert.Empty(index.Match(new[] { @"PCI\VEN_10EC&DEV_8168" }, windows11: false));
    }

    [Fact]
    public void Match_DoesNotUseDifferentOemDriver()
    {
        var index = new DriverPackIndex();
        index.Add(Row(Hwid, "10x64", "02/13/2024", "23.0"));
        Assert.Empty(index.Match(new[] { @"PCI\VEN_8086&DEV_15B8&SUBSYS_11111111&REV_01" }, false));
    }

    [Fact]
    public void Match_OrdersVersionsNumericallyAndReturnsNewestOnly()
    {
        var index = new DriverPackIndex();
        foreach (var version in new[] { "9.0", "10.0", "11.0" })
            index.Add(Row(Hwid, "10x64", "01/01/2025", version));
        Assert.Equal(new[] { "11.0" }, index.Match(new[] { Hwid.ToLowerInvariant() }, false).Select(r => r.Version));
    }

    [Fact]
    public void Match_KeepsWindows10FallbackAfterWindows11()
    {
        var index = new DriverPackIndex();
        index.Add(Row(Hwid, "11x64", "01/01/2025", "11.0"));
        index.Add(Row(Hwid, "10x64", "01/01/2024", "10.0"));
        Assert.Equal(new[] { "11x64", "10x64" }, index.Match(new[] { Hwid }, true).Select(r => r.OsFolder));
    }

    [Fact]
    public void Match_PrefersLaterExactIdOverEarlyGenericFallback()
    {
        var index = new DriverPackIndex();
        index.Add(Row(HwidNorm, "10x64", "01/01/2025", "25.0"));
        index.Add(Row(Hwid, "10x64", "01/01/2024", "24.0"));
        Assert.Equal("24.0", index.Match(new[] { Hwid + "&EXTRA_01", Hwid }, false)[0].Version);
    }

    [Theory]
    [InlineData(@"..\10x64\file.inf")]
    [InlineData(@"C:\10x64\file.inf")]
    [InlineData(@"\\host\10x64\file.inf")]
    [InlineData(@"Pack\10x64\..\file.inf")]
    [InlineData(@"Pack\10x64\file.inf:stream")]
    [InlineData(@"Pack\10x64\*.inf")]
    [InlineData(@"Pack\10x86\file.inf")]
    [InlineData(@"Pack/10x64/file.inf")]
    public void TryParse_RejectsUnsafePaths(string inf)
    {
        Assert.Null(DriverRow.TryParse(string.Join("\t", Hwid, inf, "s", "01/01/2024,1.0", "m", "0", "NIC")));
    }

    [Fact]
    public async Task LoadExternalPack_ReadsDiaIndex_KeepsLatestAndPrefixesPack()
    {
        var root = NewTempDir("samdr-");
        Directory.CreateDirectory(Path.Combine(root, "Drivers"));
        Directory.CreateDirectory(Path.Combine(root, "Index", "DIA"));
        try
        {
            var stage = Path.Combine(root, "stage");
            Directory.CreateDirectory(Path.Combine(stage, "A", "10x64", "1.0"));
            Directory.CreateDirectory(Path.Combine(stage, "A", "10x64", "2.0"));
            foreach (var version in new[] { "1.0", "2.0" })
                File.WriteAllText(Path.Combine(stage, "A", "10x64", version, "a.inf"), "[Version]\r\nClass=Net\r\n");
            var seven = ExtractEmbeddedSevenZip(root);
            Run7z(seven, Path.Combine(root, "Drivers", "DP_A.7z"), Path.Combine(stage, "A"));

            Assert.True(DriverPackService.IsExternalPack(root));
            Assert.False(DriverPackService.IsCuratedPack(root));
            Assert.True(DriverPackService.IsPack(root));

            var tsv = Path.Combine(root, "dia.tsv");
            File.WriteAllLines(tsv, new[]
            {
                string.Join("\t", Hwid, @"A\10x64\1.0\a.inf", "s", "01/10/2023,1.0", "m", "0", "old"),
                string.Join("\t", Hwid, @"A\10x64\2.0\a.inf", "s", "02/13/2024,2.0", "m", "0", "new"),
                "garbage line without tabs"
            });
            Run7z(seven, Path.Combine(root, "Index", "DIA", "DP_A.txt"), tsv);

            var logs = new List<string>();
            var index = await DriverPackIndex.LoadExternalAsync(root, seven, new Progress<string>(logs.Add));
            var candidates = index.Match(new[] { Hwid }, windows11: false);
            Assert.Single(candidates);
            Assert.Equal("2.0", candidates[0].Version);
            Assert.Equal(@"DP_A\A\10x64\2.0\a.inf", candidates[0].InfPath);
            Assert.Contains(logs, l => l.Contains("DP_A"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LoadExternalPack_SkipsMissingIndexAndReadsPlainText()
    {
        var root = NewTempDir("samdr-");
        Directory.CreateDirectory(Path.Combine(root, "Drivers"));
        Directory.CreateDirectory(Path.Combine(root, "Index", "DIA"));
        try
        {
            var seven = ExtractEmbeddedSevenZip(root);
            var stage = Path.Combine(root, "stage");
            Directory.CreateDirectory(Path.Combine(stage, "B", "10x64", "9.0"));
            File.WriteAllText(Path.Combine(stage, "B", "10x64", "9.0", "b.inf"), "[Version]\r\nClass=Net\r\n");
            Run7z(seven, Path.Combine(root, "Drivers", "DP_B.7z"), Path.Combine(stage, "B"));
            File.WriteAllLines(Path.Combine(root, "Index", "DIA", "DP_B.txt"), new[]
            {
                string.Join("\t", Hwid, @"B\10x64\9.0\b.inf", "s", "01/01/2025,9.0", "m", "0", "plain")
            });

            var logs = new List<string>();
            var index = await DriverPackIndex.LoadExternalAsync(root, seven, new Progress<string>(logs.Add));
            Assert.Equal("9.0", index.Match(new[] { Hwid }, false).Single().Version);
            Assert.DoesNotContain(logs, l => l.Contains("skipped") || l.Contains("пропущен"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EmbeddedSevenZip_IsBundledForPortable()
    {
        using var stream = typeof(WPIC.Services.DriverPackService).Assembly
            .GetManifestResourceStream("WPIC.Tools.7za64.exe");
        Assert.NotNull(stream);
        Assert.True(stream!.Length > 1_000_000);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void EmbeddedStrings_AreBundledInMainAssembly(string lang)
    {
        // имена strings.ru/en.json MSBuild может увести в спутниковые сборки — тогда portable без строк
        using var stream = typeof(WPIC.Services.DriverPackService).Assembly
            .GetManifestResourceStream($"WPIC.Resources.strings.{lang}.json");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var strings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
        Assert.NotNull(strings);
        Assert.True(strings!.TryGetValue("apps.tab.catalog", out var value));
        Assert.NotEqual("apps.tab.catalog", value);
    }

    private static string NewTempDir(string prefix) =>
        Path.Combine(Path.GetTempPath(), "wic-" + prefix + Guid.NewGuid().ToString("N"));

    private static string ExtractEmbeddedSevenZip(string workDir)
    {
        var tools = Path.Combine(workDir, "tools");
        Directory.CreateDirectory(tools);
        var dest = Path.Combine(tools, "7za64.exe");
        using (var stream = typeof(WPIC.Services.DriverPackService).Assembly
                   .GetManifestResourceStream("WPIC.Tools.7za64.exe")!)
        using (var file = File.Create(dest))
            stream.CopyTo(file);
        return dest;
    }

    private static void Run7z(string seven, string archive, string content)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(seven)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var arg in new[] { "a", "-t7z", archive, content }) psi.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error);
    }

    [Fact]
    public void BootstrapScript_ParsesWithoutExecutingSystemChanges()
    {
        var script = WingetBootstrapService.BuildInstallScript(@"C:\Test O'Brien\payload");
        Assert.Contains("O''Brien", script);
        Assert.Contains("DependencyPath", script);
        Assert.DoesNotContain("ForceUpdateFromAnyVersion", script);
        var encodedScript = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var check = "$source=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + encodedScript +
            "')); $tokens=$null; $errors=$null; [void][System.Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors); if($errors.Count){$errors;exit 1}";
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(check)));
        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output);
    }

    [Theory]
    [InlineData("missing", 0)]
    [InlineData("dependency-installed", 0)]
    [InlineData("newer-main", 0)]
    [InlineData("install-fails", 1)]
    public async Task BootstrapScript_UsesDependenciesWithoutChangingSystem(string scenario, int expectedExitCode)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wic-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (file, name) in new[]
            {
                ("AppInstaller_x64.msix", "Main"),
                ("Microsoft.VCLibs.x64.14.00.Desktop.appx", "VCLibs"),
                ("Microsoft.UI.Xaml.2.8.x64.appx", "Xaml")
            })
            {
                using var zip = System.IO.Compression.ZipFile.Open(Path.Combine(dir, file), System.IO.Compression.ZipArchiveMode.Create);
                using var writer = new StreamWriter(zip.CreateEntry("AppxManifest.xml").Open());
                writer.Write($"<Package><Identity Name=\"{name}\" Publisher=\"CN=Test\" ProcessorArchitecture=\"x64\" Version=\"1.0.0.0\" /></Package>");
            }
            var script = $$"""
                $scenario = '{{scenario}}'
                $calls = 0
                function Get-AppxPackage($Name) {
                    if (($scenario -eq 'newer-main' -and $Name -eq 'Main') -or
                        ($scenario -eq 'dependency-installed' -and $Name -eq 'VCLibs')) {
                        [pscustomobject]@{ Publisher='CN=Test'; Architecture='X64'; Version='9.0.0.0'; InstallLocation='C:\Existing' }
                    }
                }
                function Add-AppxPackage($Path, $DependencyPath, $Register, [switch]$DisableDevelopmentMode, $ErrorAction) {
                    $script:calls++
                    if ($scenario -eq 'install-fails') { throw 'Simulated deployment failure' }
                    if ($scenario -eq 'newer-main') {
                        if ($Register -ne 'C:\Existing\AppxManifest.xml' -or -not $DisableDevelopmentMode -or $Path) { throw 'Unexpected registration arguments' }
                    } else {
                        if ((Split-Path $Path -Leaf) -ne 'AppInstaller_x64.msix') { throw 'Wrong main package' }
                        $expected = if ($scenario -eq 'dependency-installed') { 1 } else { 2 }
                        if (@($DependencyPath).Count -ne $expected) { throw 'Wrong dependencies' }
                        if ($scenario -eq 'dependency-installed' -and (Split-Path $DependencyPath[0] -Leaf) -ne 'Microsoft.UI.Xaml.2.8.x64.appx') { throw 'Already-installed dependency included' }
                    }
                }
                """ + Environment.NewLine + WingetBootstrapService.BuildInstallScript(dir) + Environment.NewLine +
                "if ($calls -ne 1) { exit 2 }";
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
            using var process = System.Diagnostics.Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var registration = timeout.Token.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            });
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output;
            Assert.True(process.ExitCode == expectedExitCode, stdout + await error);
            Assert.Equal(expectedExitCode == 0, stdout.Contains("WPIC-BOOTSTRAP-OK"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task LoadCuratedPack_ReadsIndexFromArchiveAndPrefersExternalFile()
    {
        var root = NewTempDir("cur-");
        try
        {
            var stage = Path.Combine(root, "stage");
            Directory.CreateDirectory(Path.Combine(stage, "Intel", "LAN", "10x64", "23.0"));
            File.WriteAllText(Path.Combine(stage, "Intel", "LAN", "10x64", "23.0", "e1d.inf"), "[Version]\r\nClass=Net\r\n");
            var seven = ExtractEmbeddedSevenZip(root);
            var archive = Path.Combine(root, "WPIC-Drivers.7z");
            Run7z(seven, archive, Path.Combine(stage, "Intel"));
            var tsv = Path.Combine(root, "index.tsv");
            File.WriteAllLines(tsv, new[]
            {
                string.Join("\t", Hwid, @"Intel\LAN\10x64\23.0\e1d.inf", "Intel", "02/13/2024,23.0", "Intel", "0", "Ethernet")
            });
            Run7z(seven, archive, tsv);

            Assert.True(DriverPackService.IsCuratedPack(root));
            Assert.False(DriverPackService.IsExternalPack(root));
            Assert.True(DriverPackService.IsPack(root));
            var index = await DriverPackIndex.LoadCuratedAsync(root, seven);
            Assert.Equal("23.0", index.Match(new[] { Hwid }, windows11: false).Single().Version);

            File.WriteAllLines(Path.Combine(root, "index.tsv"), new[]
            {
                string.Join("\t", HwidNorm, @"Realtek\10x64\22.0\net.inf", "Realtek", "01/10/2023,22.0", "Realtek", "0", "GbE")
            });
            var external = await DriverPackIndex.LoadCuratedAsync(root, seven);
            var candidate = external.Match(new[] { Hwid }, windows11: false).Single();
            Assert.Equal("22.0", candidate.Version);
            Assert.Equal(@"Realtek\10x64\22.0\net.inf", candidate.InfPath);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Load_ReadsTsvFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, new[]
            {
                string.Join("\t", Hwid, @"Intel\LAN\10x64\23.0\e1d.inf", "Intel", "02/13/2024,23.0", "Intel", "0", "Ethernet"),
                "garbage line",
                string.Join("\t", HwidNorm, @"Realtek\10x64\22.0\netrtw.inf", "Realtek", "01/10/2023,22.0", "Realtek", "0", "GbE")
            });
            var index = DriverPackIndex.Load(path);
            var candidates = index.Match(new[] { @"PCI\VEN_8086&DEV_15B8&SUBSYS_22222222&REV_02" }, windows11: false);
            Assert.Single(candidates);
            Assert.Equal("22.0", candidates[0].Version);
        }
        finally
        {
            File.Delete(path);
        }
    }
}