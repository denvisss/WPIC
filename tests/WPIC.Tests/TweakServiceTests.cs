using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WPIC.Models;
using WPIC.Services;
using WPIC.ViewModels;
using Xunit;

namespace WPIC.Tests;

public sealed class TweakServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WPIC-Tweaks-" + Guid.NewGuid().ToString("N"));
    private readonly string _subKey = @"Software\WPIC.Tests\" + Guid.NewGuid().ToString("N");
    private string Backup => Path.Combine(_directory, "backup");
    private string KeyPath => "HKCU\\" + _subKey;

    private Tweak RegistryTweak(string type = "DWord", string value = "1") => new()
    {
        Id = "test", Name = "Test",
        Actions = [new() { Type = "registry", Key = KeyPath, ValueName = "Setting", RegType = type, Value = value }]
    };

    [Fact]
    public async Task ApplyCreatesMissingKeyAndUndoRestoresMissingValue()
    {
        var service = new TweakService(_directory);
        var tweak = RegistryTweak();
        await service.ApplyAsync(tweak, Backup, null);
        using (var key = Registry.CurrentUser.OpenSubKey(_subKey)) Assert.Equal(1, key!.GetValue("Setting"));
        Assert.Equal(L10n.T("tweaks.status.applied"), service.GetStatus(tweak));
        await service.UndoAsync(tweak, null);
        using (var key = Registry.CurrentUser.OpenSubKey(_subKey)) Assert.Null(key?.GetValue("Setting"));
        Assert.Equal(L10n.T("tweaks.status.notApplied"), service.GetStatus(tweak));
    }

    [Fact]
    public async Task ReapplyAndRestartKeepOriginalUntilUndoThenBeginNewCycle()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_subKey)) key.SetValue("Setting", 37);
        var tweak = RegistryTweak();
        var service = new TweakService(_directory);
        await service.ApplyAsync(tweak, Backup, null);
        service = new TweakService(_directory);
        await service.ApplyAsync(tweak, Backup, null);
        await service.UndoAsync(tweak, null);
        using (var key = Registry.CurrentUser.OpenSubKey(_subKey)) Assert.Equal(37, key!.GetValue("Setting"));
        using (var key = Registry.CurrentUser.OpenSubKey(_subKey, true)) key!.SetValue("Setting", 54);
        await service.ApplyAsync(tweak, Backup, null);
        await service.UndoAsync(tweak, null);
        using (var key = Registry.CurrentUser.OpenSubKey(_subKey)) Assert.Equal(54, key!.GetValue("Setting"));
    }

    [Theory]
    [InlineData(RegistryValueKind.String)]
    [InlineData(RegistryValueKind.ExpandString)]
    [InlineData(RegistryValueKind.Binary)]
    [InlineData(RegistryValueKind.MultiString)]
    [InlineData(RegistryValueKind.DWord)]
    [InlineData(RegistryValueKind.QWord)]
    public async Task UndoPreservesActualRegistryTypeAndRawValue(RegistryValueKind kind)
    {
        object original = kind switch
        {
            RegistryValueKind.Binary => new byte[] { 0, 127, 255 },
            RegistryValueKind.MultiString => new[] { "one", "two" },
            RegistryValueKind.DWord => -1,
            RegistryValueKind.QWord => long.MinValue,
            _ => @"%TEMP%\Original"
        };
        using (var key = Registry.CurrentUser.CreateSubKey(_subKey)) key.SetValue("Setting", original, kind);
        var service = new TweakService(_directory);
        var tweak = RegistryTweak();
        await service.ApplyAsync(tweak, Backup, null);
        await service.UndoAsync(tweak, null);
        using var restored = Registry.CurrentUser.OpenSubKey(_subKey)!;
        Assert.Equal(kind, restored.GetValueKind("Setting"));
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(restored.GetValue("Setting", null, RegistryValueOptions.DoNotExpandEnvironmentNames)));
    }

    [Fact]
    public async Task FailedBackupMakesNoRegistryChanges()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Backup, "not a directory");
        var service = new TweakService(_directory);
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(RegistryTweak(), Backup, null));
        using var key = Registry.CurrentUser.OpenSubKey(_subKey);
        Assert.Null(key);
    }

    [Fact]
    public async Task MissingOriginalDoesNotSubstituteDefaults()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_subKey)) key.SetValue("Setting", 37);
        var service = new TweakService(_directory);
        var tweak = RegistryTweak();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UndoAsync(tweak, null));
        using var unchanged = Registry.CurrentUser.OpenSubKey(_subKey);
        Assert.Equal(37, unchanged!.GetValue("Setting"));
    }

    [Fact]
    public async Task FailedCommandIsNotReportedAsAppliedAndKeepsUndoSnapshot()
    {
        var tweak = RegistryTweak();
        tweak.Actions.Add(new TweakAction { Type = "command", Command = "exit /b 7", UndoCommand = "exit /b 0" });
        var service = new TweakService(_directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(tweak, Backup, null));
        Assert.False(File.Exists(Path.Combine(_directory, "state.json")));
        await service.UndoAsync(tweak, null);
        using var key = Registry.CurrentUser.OpenSubKey(_subKey);
        Assert.Null(key?.GetValue("Setting"));
    }

    [Fact]
    public async Task RestoreBackupKeepsEarliestValueForCaseInsensitiveRegistryIdentity()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_subKey)) key.SetValue("Setting", 37);
        var service = new TweakService(_directory);
        var tweak = RegistryTweak();
        await service.ApplyAsync(tweak, Backup, null);
        tweak.Actions[0].Key = "HKEY_CURRENT_USER\\" + _subKey.ToUpperInvariant();
        tweak.Actions[0].ValueName = "SETTING";
        tweak.Actions[0].Value = "2";
        await service.ApplyAsync(tweak, Backup, null);
        Assert.Equal(1, service.RestoreBackup(Backup, null));
        using var restored = Registry.CurrentUser.OpenSubKey(_subKey);
        Assert.Equal(37, restored!.GetValue("Setting"));
    }

    [Theory]
    [InlineData("stickyKeysHotkey", "[511]", "[507]")]
    [InlineData("filterKeysHotkey", "[127,100,200,300,400]", "[123,100,200,300,400]")]
    [InlineData("animations", "[1,0,1,1,0,1,1]", "[0,0,0,0,0,0,0]")]
    public async Task SystemParametersPreserveFeaturesAndRestoreOriginal(string parameter, string original, string expected)
    {
        var native = new FakeParameters { State = original };
        var service = new TweakService(_directory, native);
        var tweak = new Tweak { Id = "native", Actions = [new() { Type = "systemParameter", Parameter = parameter }] };
        await service.ApplyAsync(tweak, Backup, null);
        Assert.Equal(expected, native.State);
        Assert.True(WindowsSystemParameters.IsDisabled(parameter, native.State));
        await service.ApplyAsync(tweak, Backup, null);
        await service.UndoAsync(tweak, null);
        Assert.Equal(original, native.State);
    }

    [Fact]
    public async Task NativeReadFailureStopsBeforeAnyAction()
    {
        var native = new FakeParameters { FailRead = true };
        var tweak = RegistryTweak();
        tweak.Actions.Add(new TweakAction { Type = "systemParameter", Parameter = "stickyKeysHotkey" });
        var service = new TweakService(_directory, native);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(tweak, Backup, null));
        Assert.Equal(0, native.Writes);
        using var key = Registry.CurrentUser.OpenSubKey(_subKey);
        Assert.Null(key);
    }

    [Theory]
    [InlineData("animations", "[1]")]
    [InlineData("animations", "[2,0,0,0,0,0,0]")]
    [InlineData("filterKeysHotkey", "[127]")]
    [InlineData("stickyKeysHotkey", "null")]
    public void MalformedNativeSnapshotIsRejected(string parameter, string state) =>
        Assert.Throws<InvalidDataException>(() => WindowsSystemParameters.DisabledState(parameter, state));

    [Theory]
    [InlineData("animations")]
    [InlineData("stickyKeysHotkey")]
    [InlineData("filterKeysHotkey")]
    public void NativeReadOnlyProbeReturnsValidSnapshot(string parameter)
    {
        var state = new WindowsSystemParameters().Read(parameter);
        Assert.NotEmpty(WindowsSystemParameters.DisabledState(parameter, state));
    }

    [Theory]
    [InlineData(17763, "Core", false)]
    [InlineData(19045, "Professional", false)]
    [InlineData(22000, "Core", true)]
    [InlineData(26200, "Professional", true)]
    public void Windows11OnlyTweakCompatibility(int build, string edition, bool expected)
    {
        var tweak = RegistryTweak();
        tweak.MinBuild = 22000;
        Assert.Equal(expected, TweakService.IsSupported(tweak, build, edition));
    }

    [Fact]
    public async Task UnsupportedTweakCannotBeAppliedOrSelected()
    {
        var tweak = RegistryTweak();
        tweak.MinBuild = int.MaxValue;
        var service = new TweakService(_directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(tweak, Backup, null));
        var vm = new TweakVm(tweak) { IsSelected = true };
        Assert.False(vm.IsSelected);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task CatalogHasTwentyTweaksAndCorrectOsBranches()
    {
        var catalog = await ConfigService.LoadTweaksAsync();
        Assert.Equal(20, catalog.Tweaks.Count);
        Assert.Equal(20, catalog.Tweaks.Select(t => t.Id).Distinct().Count());
        var widgets = catalog.Tweaks.Single(t => t.Id == "hide-taskbar-widgets");
        Assert.Equal("ShellFeedsTaskbarViewMode", Assert.Single(TweakService.ActionsForBuild(widgets, 19045)).ValueName);
        Assert.Equal("TaskbarDa", Assert.Single(TweakService.ActionsForBuild(widgets, 22000)).ValueName);
        Assert.False(TweakService.IsSupported(widgets, 17763, "Professional"));
        var delivery = catalog.Tweaks.Single(t => t.Id == "disable-delivery-peering");
        Assert.False(TweakService.IsSupported(delivery, 26200, "Core"));
        Assert.True(TweakService.IsSupported(delivery, 19045, "Professional"));
        Assert.Equal("0", Assert.Single(delivery.Actions).Value);
        foreach (var tweak in catalog.Tweaks.Skip(8))
            Assert.DoesNotContain(tweak.Actions, a => a.Type == "command");
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Resources", "strings.en.json")))!;
        foreach (var tweak in catalog.Tweaks)
        {
            Assert.False(string.IsNullOrWhiteSpace(tweak.Name));
            Assert.False(string.IsNullOrWhiteSpace(tweak.Description));
            Assert.True(english.ContainsKey($"tweak.{tweak.Id}.name"));
            Assert.True(english.ContainsKey($"tweak.{tweak.Id}.description"));
        }
    }

    private sealed class FakeParameters : ISystemParameters
    {
        public string State { get; set; } = "[511]";
        public bool FailRead { get; set; }
        public int Writes { get; private set; }
        public string Read(string parameter) => FailRead ? throw new InvalidOperationException("read failed") : State;
        public void Write(string parameter, string state) { State = state; Writes++; }
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_subKey, throwOnMissingSubKey: false);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
