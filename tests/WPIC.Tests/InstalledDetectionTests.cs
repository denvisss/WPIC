using WPIC.Models;
using WPIC.Services;
using Xunit;

namespace WPIC.Tests;

public class InstalledDetectionTests
{
    private static HashSet<string> Ids(params string[] ids) => new(ids, StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> Names(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void IsInstalled_MatchesByWingetId()
    {
        var pkg = new AppPackage { Name = "Node.js LTS", WingetId = "OpenJS.NodeJS.LTS" };
        Assert.True(WingetService.IsInstalled(pkg, Ids("OpenJS.NodeJS.LTS"), Names()));
    }

    [Fact]
    public void IsInstalled_MatchesExactDisplayName()
    {
        var pkg = new AppPackage { Name = "Google Chrome", WingetId = "Google.Chrome" };
        Assert.True(WingetService.IsInstalled(pkg, Ids(), Names("Google Chrome")));
    }

    [Theory]
    [InlineData("7-Zip", "7-Zip 26.06 (x64 edition)")]
    [InlineData("Mozilla Firefox", "Mozilla Firefox (x64 ru)")]
    [InlineData("Notepad++", "Notepad++ (64-bit x64)")]
    [InlineData("Everything", "Everything 1.4.1.1026 (x64)")]
    [InlineData("Python 3.12", "Python 3.12.7 (64-bit)")]
    public void IsInstalled_MatchesDisplayNameWithVersionSuffix(string appName, string displayName)
    {
        var pkg = new AppPackage { Name = appName, WingetId = "Some.Id" };
        Assert.True(WingetService.IsInstalled(pkg, Ids(), Names(displayName)));
    }

    [Fact]
    public void IsInstalled_DoesNotMatchDifferentProductWithSamePrefix()
    {
        var pkg = new AppPackage { Name = "Git", WingetId = "Git.Git" };
        Assert.False(WingetService.IsInstalled(pkg, Ids(), Names("GitHub Desktop")));
    }

    [Fact]
    public void IsInstalled_EmptyWingetId_FallsBackToNames()
    {
        var pkg = new AppPackage { Name = "Steam", WingetId = "" };
        Assert.True(WingetService.IsInstalled(pkg, Ids(), Names("Steam")));
        Assert.False(WingetService.IsInstalled(pkg, Ids(), Names("Discord")));
    }
}