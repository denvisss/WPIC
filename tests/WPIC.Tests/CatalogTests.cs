using WPIC.Models;
using WPIC.Services;
using WPIC.ViewModels;
using Xunit;

namespace WPIC.Tests;

[Collection("L10n")]
public class CatalogTests
{
    private static AppCatalog Load() => ConfigService.LoadAppsAsync().GetAwaiter().GetResult();

    private static AppCategory Cat(AppCatalog catalog, string key) =>
        catalog.Categories.Single(c => c.Key == key);

    [Fact]
    public void Catalog_SectionOrder_MatchesSpec()
    {
        var keys = Load().Categories.Select(c => c.Key).ToList();
        Assert.Equal(
            new[] { "system", "browsers", "utilities", "communication", "popular", "media", "players" },
            keys);
    }

    [Theory]
    [InlineData("system", new[] { "vcredist", "directx", "dotnet-framework", "java", "terminal", "powershell" })]
    [InlineData("browsers", new[] { "firefox", "opera", "chrome", "chromium", "chromium-gost", "yandex", "tor" })]
    [InlineData("utilities", new[] { "7zip", "winrar", "powertoys", "everything", "qbittorrent", "notepad-plus", "sumatra" })]
    [InlineData("communication", new[] { "telegram", "whatsapp", "viber", "zoom", "discord" })]
    public void Catalog_SectionApps_MatchSpec(string key, string[] expectedIds)
    {
        var apps = Cat(Load(), key).Apps;
        Assert.Equal(expectedIds, apps.Select(a => a.Id).ToArray());
        Assert.All(apps, a => Assert.False(string.IsNullOrWhiteSpace(a.WingetId)));
    }

    [Fact]
    public void Catalog_PopularSection_HasApps()
    {
        var apps = Cat(Load(), "popular").Apps;
        Assert.True(apps.Count >= 6);
        Assert.Contains(apps, a => a.WingetId == "Valve.Steam");
        Assert.Contains(apps, a => a.WingetId == "Microsoft.VisualStudioCode");
    }

    [Fact]
    public void Catalog_MediaAndPlayers_Unchanged()
    {
        var catalog = Load();
        Assert.Equal(8, Cat(catalog, "media").Apps.Count);
        Assert.Equal(8, Cat(catalog, "players").Apps.Count);
    }

    [Fact]
    public void Catalog_AllApps_HaveRuAndEnDescriptions()
    {
        foreach (var app in Load().Categories.SelectMany(c => c.Apps))
        {
            Assert.False(string.IsNullOrWhiteSpace(app.Description), app.Id);
            Assert.False(string.IsNullOrWhiteSpace(app.DescriptionEn), app.Id);
        }
    }

    [Fact]
    public void Catalog_IdsUniqueWithinCategory()
    {
        foreach (var category in Load().Categories)
        {
            var ids = category.Apps.Select(a => a.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }
    }

    [Fact]
    public void VcRedist_BundlesAllEditions2005To2022()
    {
        var app = Cat(Load(), "system").Apps.Single(a => a.Id == "vcredist");
        Assert.Equal("Microsoft.VCRedist.2015+.x64", app.WingetId);
        var targets = app.InstallTargets;
        Assert.Equal(12, targets.Count);
        foreach (var year in new[] { "2005", "2008", "2010", "2012", "2013", "2015+" })
        foreach (var arch in new[] { "x86", "x64" })
            Assert.Contains($"Microsoft.VCRedist.{year}.{arch}", targets);
    }

    [Fact]
    public void DotNetFramework_InstallsRuntimeAndEnablesNetFx3()
    {
        var app = Cat(Load(), "system").Apps.Single(a => a.Id == "dotnet-framework");
        Assert.Equal("Microsoft.DotNet.Framework.Runtime", app.WingetId);
        Assert.Equal("NetFx3", app.WindowsFeature);
    }

    [Fact]
    public void InstallTargets_SinglePackage_FallsBackToWingetId()
    {
        var app = new AppPackage { WingetId = "Some.Id" };
        Assert.Equal(new[] { "Some.Id" }, app.InstallTargets);
        Assert.Empty(new AppPackage().InstallTargets);
    }

    [Fact]
    public void IsInstalled_MultiPackage_RequiresAllTargets()
    {
        var pkg = new AppPackage
        {
            Name = "VC++ bundle",
            WingetId = "A",
            WingetIds = new List<string> { "A", "B" }
        };
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A" };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.False(WingetService.IsInstalled(pkg, ids, names));
        ids.Add("B");
        Assert.True(WingetService.IsInstalled(pkg, ids, names));
    }

    [Fact]
    public void TableParser_AcceptsUnknownVersionFromMsStore()
    {
        var lines = new[]
        {
            "Name      Id             Version  Source",
            "----------------------------------------",
            "WhatsApp  9NKSQGP7F2NH   Unknown  msstore"
        };
        var row = Assert.Single(TableParser.Parse(lines));
        Assert.Equal("9NKSQGP7F2NH", row.Id);
        Assert.Equal("Unknown", row.Version);
        Assert.Equal("msstore", row.Source);
    }

    [Theory]
    [InlineData("PowerShell Microsoft.PowerShell 7.7.0.0 winget", "Microsoft.PowerShell", "7.7.0.0")]
    [InlineData("Mozilla Firefox (en-US)  Mozilla.Firefox  160.0  winget", "Mozilla.Firefox", "160.0")]
    [InlineData("WhatsApp  9NKSQGP7F2NH  Unknown  msstore", "9NKSQGP7F2NH", "Unknown")]
    public void ParseExactVersion_HandlesSingleAndDoubleSpaceTables(string row, string id, string expected)
    {
        var lines = new[] { "Имя  ИД  Версия  Источник", "--------------------------", row };
        Assert.Equal(expected, CatalogVersionService.ParseExactVersion(lines, id));
    }

    [Fact]
    public void ParseExactVersion_NoMatch_ReturnsNull()
    {
        var lines = new[] { "Имя  ИД  Версия", "-------------------", "No packages found matching input criteria." };
        Assert.Null(CatalogVersionService.ParseExactVersion(lines, "Some.Id"));
    }

    [Fact]
    public void AppItemVm_VersionLabel_FormatsRawVersion()
    {
        var vm = new AppItemVm(new AppPackage { Name = "X", WingetId = "X.Y" });
        Assert.Equal("", vm.VersionLabel);
        Assert.Equal(System.Windows.Visibility.Collapsed, vm.VersionVisibility);

        vm.VersionRaw = "1.2.3";
        Assert.Equal("v1.2.3", vm.VersionLabel);
        Assert.Equal(System.Windows.Visibility.Visible, vm.VersionVisibility);

        vm.VersionRaw = "Unknown";
        Assert.Equal(L10n.T("apps.version.store"), vm.VersionLabel);
    }

    [Fact]
    public void AppItemVm_Description_FollowsUiLanguage()
    {
        var pkg = new AppPackage { Name = "X", Description = "Русское описание", DescriptionEn = "English description" };
        var vm = new AppItemVm(pkg);
        var previous = L10n.CurrentPreference;
        try
        {
            L10n.SetLanguage("ru");
            Assert.Equal("Русское описание", vm.Description);
            L10n.SetLanguage("en");
            Assert.Equal("English description", vm.Description);
        }
        finally
        {
            L10n.SetLanguage(previous);
        }
    }
}