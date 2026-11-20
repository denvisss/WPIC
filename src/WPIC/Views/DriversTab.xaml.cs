using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WPIC.Services;
using WPIC.ViewModels;

namespace WPIC.Views;

public partial class DriversTab : UserControl
{
    private string? _packDir;
    private DriverPackIndex? _index;
    private bool _netBusy;
    private bool _loadingPack;
    private bool _backupBusy;

    private sealed class NetDeviceVm : ObservableObject
    {
        public string Name { get; set; } = "";
        public string DeviceId { get; set; } = "";
        private string _state = "";
        public string State { get => _state; set => Set(ref _state, value); }
    }

    public DriversTab()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await RefreshPackAsync();
            await DetectProblemsAsync();
        };
        L10n.Instance.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            TxtPackStatus.Text = PackStatus();
            if (!_netBusy) await DetectProblemsAsync();
        });
    }

    /// <summary>При открытии вкладки сама показывает сетевые карты и Wi-Fi без драйвера.</summary>
    private async Task DetectProblemsAsync()
    {
        if (_netBusy) return;
        var index = _index ?? new DriverPackIndex();
        var problems = await Task.Run(() => DriverPackService.FindProblemNetDevices(
            DriverPackService.GetNetworkDevices(), index, DriverPackService.IsWindows11));
        if (problems.Count == 0)
        {
            ListNetDevices.Visibility = Visibility.Collapsed;
            TxtOfflineStatus.Text = L10n.T("drivers.offline.none");
            return;
        }
        ListNetDevices.ItemsSource = problems.Select(p => new NetDeviceVm
        {
            Name = p.Device.Name,
            DeviceId = p.Device.DeviceId,
            State = L10n.T(_index == null ? "drivers.offline.needPack"
                : p.Candidates.Count > 0 ? "drivers.offline.ready" : "drivers.offline.noMatch")
        }).ToList();
        ListNetDevices.Visibility = Visibility.Visible;
        TxtOfflineStatus.Text = L10n.T("drivers.offline.detected", problems.Count);
    }

    public async Task UsePackAsync(string dir)
    {
        var settings = SettingsService.Load();
        settings.DriverPackPath = dir;
        SettingsService.Save(settings);
        _index = null;
        await RefreshPackAsync();
        await DetectProblemsAsync();
    }

    private string PackStatus() =>
        _packDir == null ? L10n.T(DriverPackService.HasBundledPack
            ? "drivers.offline.packMissing" : "drivers.offline.packMissingPortable") :
        _index == null ? L10n.T("drivers.offline.packInvalid") :
        L10n.T("drivers.offline.packFound", _packDir);

    private async Task<DriverPackIndex> LoadIndexAsync(string dir)
    {
        if (DriverPackService.IsCuratedPack(dir))
        {
            TxtPackStatus.Text = L10n.T("drivers.offline.loadingPack");
            return await DriverPackIndex.LoadCuratedAsync(dir, DriverPackService.FindExtractor(),
                new Progress<string>(LogService.Log));
        }
        TxtPackStatus.Text = L10n.T("drivers.offline.loadingPack");
        return await DriverPackIndex.LoadExternalAsync(dir, DriverPackService.FindExtractor(),
            new Progress<string>(LogService.Log));
    }

    private async Task RefreshPackAsync()
    {
        if (_netBusy || _loadingPack) return;
        _loadingPack = true;
        BtnNetFix.IsEnabled = false;
        BtnPackBrowse.IsEnabled = false;
        try
        {
            var dir = await Task.Run(DriverPackService.FindPack);
            if (!string.Equals(dir, _packDir, StringComparison.OrdinalIgnoreCase)) _index = null;
            _packDir = dir;
            if (dir != null)
            {
                _index ??= await LoadIndexAsync(dir);
                if (_index.Count == 0) throw new InvalidDataException(L10n.T("drivers.offline.packInvalid"));
            }
            TxtPackStatus.Text = PackStatus();
        }
        catch (Exception ex)
        {
            _index = null;
            TxtPackStatus.Text = L10n.T("drivers.offline.packInvalid");
            LogService.Log(ex.Message);
        }
        finally
        {
            _loadingPack = false;
            BtnPackBrowse.IsEnabled = true;
            BtnNetFix.IsEnabled = _index != null;
        }
    }

    private async void BtnPackBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (dialog.ShowDialog() != true) return;
        var dir = DriverPackService.IsPack(dialog.FolderName)
            ? dialog.FolderName
            : System.IO.Path.Combine(dialog.FolderName, DriverPackService.PackDirName);
        if (!DriverPackService.IsPack(dir))
        {
            ThemedMessageBox.Show(L10n.T("drivers.offline.packInvalid"), L10n.T("drivers.err.title"),
                MessageBoxButton.OK, MessageBoxImage.Warning, Window.GetWindow(this));
            return;
        }
        await UsePackAsync(dir);
    }

    private async void BtnNetFix_Click(object sender, RoutedEventArgs e) => await FixNetAsync();

    /// <summary>Подбор и установка драйверов для сетевых карт из пака (кнопка вкладки и панель окружения).</summary>
    public async Task FixNetAsync()
    {
        if (_netBusy || _loadingPack) return;
        if (_packDir == null || _index == null) await RefreshPackAsync();
        if (_packDir == null || _index == null)
        {
            ThemedMessageBox.Show(L10n.T(DriverPackService.HasBundledPack
                ? "drivers.offline.packMissing" : "drivers.offline.packMissingPortable"), L10n.T("drivers.err.title"),
                MessageBoxButton.OK, MessageBoxImage.Warning, Window.GetWindow(this));
            return;
        }
        if (!DriverPackService.IsAdmin)
        {
            ThemedMessageBox.Show(L10n.T("drivers.offline.notAdmin"), L10n.T("drivers.err.title"),
                MessageBoxButton.OK, MessageBoxImage.Warning, Window.GetWindow(this));
            return;
        }
        _netBusy = true;
        BtnNetFix.IsEnabled = false;
        BtnPackBrowse.IsEnabled = false;
        TxtOfflineStatus.Text = L10n.T("drivers.offline.working");
        try
        {
            var index = _index!;
            var packDir = _packDir!;
            var problems = await Task.Run(() => DriverPackService.FindProblemNetDevices(
                DriverPackService.GetNetworkDevices(), index, DriverPackService.IsWindows11));
            if (problems.Count == 0)
            {
                ListNetDevices.Visibility = Visibility.Collapsed;
                TxtOfflineStatus.Text = L10n.T("drivers.offline.none");
                return;
            }

            var vms = problems.ToDictionary(p => p.Device, p => new NetDeviceVm
            {
                Name = p.Device.Name,
                DeviceId = p.Device.DeviceId,
                State = L10n.T("drivers.offline.matching")
            });
            ListNetDevices.ItemsSource = vms.Values.ToList();
            ListNetDevices.Visibility = Visibility.Visible;

            var installed = 0;
            foreach (var problem in problems)
            {
                var device = problem.Device;
                var vm = vms[device];
                var candidates = problem.Candidates;
                if (candidates.Count == 0)
                {
                    vm.State = L10n.T("drivers.offline.noMatch");
                    continue;
                }
                var ok = false;
                var reboot = false;
                foreach (var row in candidates)
                {
                    vm.State = L10n.T("drivers.offline.installing", row.DeviceName);
                    LogService.Log(L10n.T("drivers.log.offlineInstall", device.Name, row.InfPath));
                    try
                    {
                        string message;
                        (ok, reboot, message) = await DriverPackService.InstallAsync(_packDir, row, device.DeviceId, CancellationToken.None);
                        LogService.Log(message);
                        if (ok || reboot) break;
                    }
                    catch (Exception ex) { LogService.Log(L10n.T("drivers.log.offlineError", device.Name, ex.Message)); }
                }
                vm.State = L10n.T(reboot ? "drivers.offline.reboot" : ok ? "drivers.offline.done" : "drivers.offline.failed");
                if (ok) installed++;
            }
            TxtOfflineStatus.Text = L10n.T("drivers.offline.summary", installed, problems.Count);
            LogService.Log(L10n.T("drivers.log.offlineSummary", installed, problems.Count));
        }
        catch (Exception ex)
        {
            TxtOfflineStatus.Text = L10n.T("drivers.offline.failed");
            LogService.Log(L10n.T("drivers.log.problemsError", ex.Message));
        }
        finally
        {
            _netBusy = false;
            BtnPackBrowse.IsEnabled = true;
            BtnNetFix.IsEnabled = _index != null;
        }
    }

    private async void BtnLoad_Click(object sender, RoutedEventArgs e)
    {
        BtnLoad.IsEnabled = false;
        LogService.Log(L10n.T("drivers.log.loading"));
        try
        {
            var drivers = await Task.Run(DriverService.GetInstalledDrivers);
            GridProblems.Visibility = Visibility.Collapsed;
            GridDrivers.Visibility = Visibility.Visible;
            GridDrivers.ItemsSource = drivers;
            LogService.Log(L10n.T("drivers.log.count", drivers.Count));
        }
        catch (Exception ex)
        {
            LogService.Log(L10n.T("drivers.log.loadError", ex.Message));
            ThemedMessageBox.Show(L10n.T("drivers.err.load", ex.Message), L10n.T("drivers.err.title"),
                MessageBoxButton.OK, MessageBoxImage.Error, Window.GetWindow(this));
        }
        finally
        {
            BtnLoad.IsEnabled = true;
        }
    }

    private async void BtnProblems_Click(object sender, RoutedEventArgs e)
    {
        BtnProblems.IsEnabled = false;
        LogService.Log(L10n.T("drivers.log.problems"));
        try
        {
            var problems = await Task.Run(DriverService.GetProblemDevices);
            GridDrivers.Visibility = Visibility.Collapsed;
            GridProblems.Visibility = Visibility.Visible;
            GridProblems.ItemsSource = problems;
            LogService.Log(problems.Count == 0
                ? L10n.T("drivers.log.none")
                : L10n.T("drivers.log.found", problems.Count));
        }
        catch (Exception ex)
        {
            LogService.Log(L10n.T("drivers.log.problemsError", ex.Message));
            ThemedMessageBox.Show(L10n.T("drivers.err.problems", ex.Message), L10n.T("drivers.err.title"),
                MessageBoxButton.OK, MessageBoxImage.Error, Window.GetWindow(this));
        }
        finally
        {
            BtnProblems.IsEnabled = true;
        }
    }

    private void BtnWindowsUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:windows-update") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Log(L10n.T("drivers.log.wuError", ex.Message));
        }
    }

    // ---------- резервная копия драйверов ----------

    private void SetBackupBusy(bool busy)
    {
        _backupBusy = busy;
        BtnDriversExport.IsEnabled = !busy;
        BtnDriversRestore.IsEnabled = !busy;
        ChkExport7z.IsEnabled = !busy;
    }

    /// <summary>Оценка оставшегося времени по линейному темпу: часы, если есть, иначе минуты.</summary>
    private static string EtaClock(TimeSpan elapsed, int done, int total)
    {
        var eta = TimeSpan.FromSeconds(elapsed.TotalSeconds * (total - done) / Math.Max(done, 1));
        return (int)eta.TotalHours > 0
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)eta.TotalHours, eta.Minutes, eta.Seconds)
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1:00}", (int)eta.TotalMinutes, eta.Seconds);
    }

    private async void BtnDriversExport_Click(object sender, RoutedEventArgs e)
    {
        if (_backupBusy) return;
        if (!DriverPackService.IsAdmin)
        {
            ThemedMessageBox.Show(L10n.T("drivers.backup.notAdmin"), L10n.T("drivers.err.title"),
                MessageBoxButton.OK, MessageBoxImage.Warning, Window.GetWindow(this));
            return;
        }
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = L10n.T("drivers.backup.pickFolder") };
        if (dialog.ShowDialog() != true) return;
        var dest = Path.Combine(dialog.FolderName, "DriverBackup_" + DateTime.Now.ToString("yyyyMMdd_HHmm"));
        SetBackupBusy(true);
        PanelBackupProgress.Visibility = Visibility.Visible;
        BarBackup.IsIndeterminate = true;
        BarBackup.Value = 0;
        TxtBackupProgress.Text = L10n.T("drivers.backup.exporting");
        var stopwatch = Stopwatch.StartNew();
        IProgress<DriverExportProgress> ui = new Progress<DriverExportProgress>(p =>
        {
            if (p.Phase == DriverExportPhase.Packing)
            {
                BarBackup.IsIndeterminate = true;
                TxtBackupProgress.Text = L10n.T("drivers.backup.compressing");
                return;
            }
            if (p.Total <= 0)
            {
                BarBackup.IsIndeterminate = true;
                TxtBackupProgress.Text = L10n.T("drivers.backup.exporting");
                return;
            }
            BarBackup.IsIndeterminate = false;
            BarBackup.Value = Math.Min(100, p.Done * 100.0 / p.Total);
            var text = L10n.T("drivers.backup.progress", Math.Min(p.Done, p.Total), p.Total);
            if (p.Done > 0 && p.Done < p.Total)
                text += " · " + L10n.T("drivers.backup.eta", EtaClock(stopwatch.Elapsed, p.Done, p.Total));
            TxtBackupProgress.Text = text;
        });
        try
        {
            var code = await DriverPackService.ExportDriversAsync(dest, new Progress<string>(LogService.Log), ui);
            if (code != 0)
            {
                TxtBackupStatus.Text = L10n.T("drivers.backup.exportFailed", code);
                ThemedMessageBox.Show(TxtBackupStatus.Text, L10n.T("drivers.backup.title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning, Window.GetWindow(this));
                return;
            }
            BarBackup.Value = 100;
            var count = await Task.Run(() => Directory.EnumerateFiles(dest, "*.inf", SearchOption.AllDirectories).Count());
            var message = L10n.T("drivers.backup.exportDone", count, dest);
            TxtBackupStatus.Text = L10n.T("drivers.backup.exportDone", count, dest);
            if (ChkExport7z.IsChecked == true)
            {
                TxtBackupStatus.Text = L10n.T("drivers.backup.compressing");
                ui.Report(new DriverExportProgress(DriverExportPhase.Packing, 0, 0));
                var archive = dest + ".7z";
                var seven = DriverPackService.FindExtractor();
                var (zipCode, zipOut) = await DriverPackService.RunToolAsync(
                    seven, new[] { "a", "-t7z", "-mx=5", "-y", archive, dest }, CancellationToken.None);
                if (zipCode == 0)
                {
                    var size = new FileInfo(archive).Length / 1048576.0;
                    message += "\n\n" + L10n.T("drivers.backup.compressDone", archive, size);
                    TxtBackupStatus.Text = L10n.T("drivers.backup.compressDone", archive, size);
                }
                else LogService.Log(zipOut.Trim());
            }
            LogService.Log(message);
            ThemedMessageBox.Show(message, L10n.T("drivers.backup.title"),
                MessageBoxButton.OK, MessageBoxImage.Information, Window.GetWindow(this));
        }
        catch (Exception ex)
        {
            TxtBackupStatus.Text = ex.Message;
            LogService.Log(ex.Message);
        }
        finally
        {
            PanelBackupProgress.Visibility = Visibility.Collapsed;
            SetBackupBusy(false);
        }
    }

    private async void BtnDriversRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_backupBusy) return;
        if (!DriverPackService.IsAdmin)
        {
            ThemedMessageBox.Show(L10n.T("drivers.backup.notAdmin"), L10n.T("drivers.err.title"),
                MessageBoxButton.OK, MessageBoxImage.Warning, Window.GetWindow(this));
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = L10n.T("drivers.backup.pickFile"), Filter = L10n.T("drivers.backup.filter") };
        if (dialog.ShowDialog() != true) return;
        SetBackupBusy(true);
        TxtBackupStatus.Text = L10n.T("drivers.backup.restoring");
        string? temp = null;
        try
        {
            var source = dialog.FileName;
            var dir = Path.GetDirectoryName(source)!;
            if (string.Equals(Path.GetExtension(source), ".7z", StringComparison.OrdinalIgnoreCase))
            {
                var seven = DriverPackService.FindExtractor();
                temp = Path.Combine(Path.GetTempPath(), "wpic-driver-restore-" + Guid.NewGuid().ToString("N"));
                var (extractCode, extractOut) = await DriverPackService.RunToolAsync(
                    seven, new[] { "x", "-y", "-o" + temp, source }, CancellationToken.None);
                if (extractCode != 0)
                    throw new InvalidOperationException(L10n.T("drivers.offline.extractError", extractOut.Trim()));
                dir = temp;
            }
            else if (!string.Equals(Path.GetExtension(source), ".inf", StringComparison.OrdinalIgnoreCase))
            {
                ThemedMessageBox.Show(L10n.T("drivers.backup.badSource"), L10n.T("drivers.backup.title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning, Window.GetWindow(this));
                return;
            }
            var code = await DriverPackService.RestoreDriversAsync(dir, new Progress<string>(LogService.Log));
            TxtBackupStatus.Text = L10n.T(code == 0 ? "drivers.backup.restoreDone" : "drivers.backup.restoreFailed", code);
            LogService.Log(TxtBackupStatus.Text);
            ThemedMessageBox.Show(TxtBackupStatus.Text, L10n.T("drivers.backup.title"), MessageBoxButton.OK,
                code == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning, Window.GetWindow(this));
        }
        catch (Exception ex)
        {
            TxtBackupStatus.Text = ex.Message;
            LogService.Log(ex.Message);
        }
        finally
        {
            if (temp != null)
            {
                try { Directory.Delete(temp, true); }
                catch (IOException ex) { LogService.Log(ex.Message); }
                catch (UnauthorizedAccessException ex) { LogService.Log(ex.Message); }
            }
            SetBackupBusy(false);
        }
    }
}