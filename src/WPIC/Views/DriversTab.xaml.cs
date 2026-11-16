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
            return await Task.Run(() => DriverPackIndex.Load(System.IO.Path.Combine(dir, "index.tsv")));
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

    private async void BtnNetFix_Click(object sender, RoutedEventArgs e)
    {
        if (_netBusy || _loadingPack || _packDir == null || _index == null) return;
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
            _index ??= await LoadIndexAsync(_packDir!);
            var index = _index;
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
}