using System.IO;
using WPIC.Services;
using Xunit;

namespace WPIC.Tests;

public class UpdateServiceTests
{
    private static string ReleaseJson(string tag, bool draft = false, bool prerelease = false, params string[] assetNames)
    {
        var assets = string.Join(",", assetNames.Select(n =>
            $"{{\"name\":\"{n}\",\"browser_download_url\":\"https://example.test/{n}\",\"size\":12345}}"));
        return $"{{\"tag_name\":\"{tag}\",\"draft\":{draft.ToString().ToLowerInvariant()},\"prerelease\":{prerelease.ToString().ToLowerInvariant()},\"assets\":[{assets}]}}";
    }

    [Fact]
    public void ParseRelease_NewerVersion_PicksVariantAsset()
    {
        var json = ReleaseJson("v0.9.0", false, false, "WPIC_0.9.0-full.exe", "WPIC_0.9.0-portable.exe", "WPIC_0.9.0-full.zip");
        var full = UpdateService.ParseRelease(json, new Version(0, 3, 0), "full");
        Assert.NotNull(full);
        Assert.Equal("0.9.0", full!.Version);
        Assert.Equal("v0.9.0", full.Tag);
        Assert.Equal("https://example.test/WPIC_0.9.0-full.exe", full.DownloadUrl);
        Assert.Equal(12345, full.Size);

        var portable = UpdateService.ParseRelease(json, new Version(0, 3, 0), "portable");
        Assert.NotNull(portable);
        Assert.Equal("https://example.test/WPIC_0.9.0-portable.exe", portable!.DownloadUrl);
    }

    [Theory]
    [InlineData("v0.3.0")]
    [InlineData("v0.2.0")]
    [InlineData("0.1")]
    public void ParseRelease_NotNewer_ReturnsNull(string tag)
    {
        var json = ReleaseJson(tag, false, false, $"WPIC_{tag.TrimStart('v')}-full.exe");
        Assert.Null(UpdateService.ParseRelease(json, new Version(0, 3, 0), "full"));
    }

    [Fact]
    public void ParseRelease_DraftOrPrerelease_ReturnsNull()
    {
        var json = ReleaseJson("v9.0.0", true, false, "WPIC_9.0.0-full.exe");
        Assert.Null(UpdateService.ParseRelease(json, new Version(0, 3, 0), "full"));
        json = ReleaseJson("v9.0.0", false, true, "WPIC_9.0.0-full.exe");
        Assert.Null(UpdateService.ParseRelease(json, new Version(0, 3, 0), "full"));
    }

    [Fact]
    public void ParseRelease_MissingAssetOrBadJson_ReturnsNull()
    {
        var json = ReleaseJson("v9.0.0", false, false, "WPIC_9.0.0-portable.exe");
        Assert.Null(UpdateService.ParseRelease(json, new Version(0, 3, 0), "full"));
        Assert.Null(UpdateService.ParseRelease("not json", new Version(0, 3, 0), "full"));
        Assert.Null(UpdateService.ParseRelease("{}", new Version(0, 3, 0), "full"));
    }

    [Fact]
    public void BuildPortableSwapScript_MovesOldExeAndStartsNew()
    {
        var script = UpdateService.BuildPortableSwapScript(@"C:\Temp\new.exe", @"C:\App\app.exe");
        Assert.Contains(@"move /y ""C:\App\app.exe"" ""C:\App\app.exe.old""", script);
        Assert.Contains(@"move /y ""C:\Temp\new.exe"" ""C:\App\app.exe""", script);
        Assert.Contains(@"start """" ""C:\App\app.exe""", script);
        Assert.Contains(":wait", script);
    }

    [Fact]
    public void Variant_WithoutUninstaller_IsPortable()
    {
        // в каталоге тестовой сборки нет unins000.exe — это portable-окружение
        Assert.False(UpdateService.IsInstalledFull);
        Assert.Equal("portable", UpdateService.Variant);
    }
}

public class BuiltinAppServiceTests
{
    [Fact]
    public void ParsePackages_SingleObjectAndArray()
    {
        var single = "{\"Name\":\"Microsoft.Test\",\"PackageFullName\":\"Microsoft.Test_1.0.0.0_x64__abc\",\"PackageFamilyName\":\"Microsoft.Test_abc\",\"InstallLocation\":\"C:\\\\Program Files\\\\WindowsApps\\\\Test\",\"NonRemovable\":false}";
        var list = BuiltinAppService.ParsePackages(single);
        Assert.Single(list);
        Assert.Equal("Microsoft.Test", list[0].Name);
        Assert.Equal("Microsoft.Test_abc", list[0].PackageFamilyName);
        Assert.False(list[0].NonRemovable);

        var array = "[" + single + "," + single.Replace("Microsoft.Test", "Microsoft.Other") + "]";
        Assert.Equal(2, BuiltinAppService.ParsePackages(array).Count);
    }

    [Fact]
    public void ParsePackages_EmptyOrInvalid_ReturnsEmpty()
    {
        Assert.Empty(BuiltinAppService.ParsePackages(""));
        Assert.Empty(BuiltinAppService.ParsePackages("   "));
        Assert.Empty(BuiltinAppService.ParsePackages("broken {"));
    }

    [Theory]
    [InlineData("Microsoft.WindowsCalculator", "Windows Calculator")]
    [InlineData("Microsoft.549981C3F5F10", "Cortana")]
    [InlineData("Microsoft.MicrosoftEdge.Stable", "Microsoft Edge")]
    [InlineData("Microsoft.WindowsSoundRecorder", "Windows Sound Recorder")]
    public void Prettify_MakesReadableNames(string package, string expected)
    {
        Assert.Equal(expected, BuiltinAppService.Prettify(package));
    }

    [Theory]
    [InlineData("Microsoft.WindowsStore", "red")]
    [InlineData("Microsoft.DesktopAppInstaller", "red")]
    [InlineData("Microsoft.SecHealthUI", "red")]
    [InlineData("Microsoft.BingWeather", "green")]
    [InlineData("Microsoft.ZuneVideo", "green")]
    [InlineData("Microsoft.WindowsCalculator", "yellow")]
    [InlineData("Some.UnknownApp", "yellow")]
    public void RiskOf_ClassifiesPackages(string package, string expected)
    {
        Assert.Equal(expected, BuiltinAppService.RiskOf(package));
    }

    [Fact]
    public void ReadManifest_ReadsDisplayNameAndIcon()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WPIC-Bloat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Assets"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "AppxManifest.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\" " +
                "xmlns:uap=\"http://schemas.microsoft.com/appx/manifest/uap/windows10\">" +
                "<Applications><Application Id=\"App\"><uap:VisualElements " +
                "DisplayName=\"Test App\" Square44x44List=\"Assets\\logo.png\" Square150x150Logo=\"Assets\\tile.png\"/>" +
                "</Application></Applications></Package>");
            var icon = Path.Combine(dir, "Assets", "logo.png");
            File.WriteAllBytes(icon, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

            var (name, path) = BuiltinAppService.ReadManifest(dir);
            Assert.Equal("Test App", name);
            Assert.Equal(icon, path);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ReadManifest_MsResourceAndScaleVariants()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WPIC-Bloat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Assets"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "AppxManifest.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\" " +
                "xmlns:uap=\"http://schemas.microsoft.com/appx/manifest/uap/windows10\">" +
                "<Applications><Application Id=\"App\"><uap:VisualElements " +
                "DisplayName=\"ms-resource:AppName\" Square44x44List=\"Assets\\logo.png\"/>" +
                "</Application></Applications></Package>");
            var scaled = Path.Combine(dir, "Assets", "logo.scale-200.png");
            File.WriteAllBytes(scaled, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

            var (name, path) = BuiltinAppService.ReadManifest(dir);
            Assert.Equal("ms-resource:AppName", name);
            Assert.Equal(scaled, path);
            Assert.Equal("Plain Name", BuiltinAppService.ResolveDisplayName("Any.Package_1.0_x64__hash", "Plain Name"));
            Assert.Null(BuiltinAppService.ResolveDisplayName("Absent.Package_1.0_x64__hash", "ms-resource:AppName"));
            Assert.Null(BuiltinAppService.ResolveDisplayName("Any.Package_1.0_x64__hash", null));

            var (missingName, missingPath) = BuiltinAppService.ReadManifest(Path.Combine(dir, "absent"));
            Assert.Null(missingName);
            Assert.Null(missingPath);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}