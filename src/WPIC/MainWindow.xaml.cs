using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using WPIC.Services;
using WPIC.Views;

namespace WPIC;

public partial class MainWindow : Window
{
    private bool _envBusy;

    public MainWindow()
    {
        InitializeComponent();
        LogService.EntryAdded += OnLogEntry;
        LogService.Log(L10n.T("main.log.started"));
        LogService.Log(L10n.T("main.log.configDir", ConfigService.ConfigDirectory));
        LogService.Log(L10n.T("main.log.dataDir", LogService.DataDirectory));
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this, ThemeService.Resolved == ThemeService.Dark);
        UpdateService.PendingChanged += () =>
        {
            if (Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(() =>
                DotUpdate.Visibility = UpdateService.Pending != null ? Visibility.Visible : Visibility.Collapsed);
        };
        Loaded += async (_, _) =>
        {
            UpdateService.CleanupOldPortable();
            // проверка обновлений только при запуске; результат — красная точка на «Настройках»
            await UpdateService.CheckAsync();
            await RefreshEnvironmentAsync();
        };
    }

    // ---------- панель окружения ----------

    private void SetDot(System.Windows.Shapes.Ellipse dot, bool ok) =>
        dot.Fill = (Brush)FindResource(ok ? "Brush.Success" : "Brush.Danger");

    private void SetEnvButtonsEnabled(bool enabled)
    {
        BtnEnvWingetInstall.IsEnabled = enabled;
        BtnEnvWingetStore.IsEnabled = enabled;
        BtnEnvWingetUpdate.IsEnabled = enabled;
        BtnEnvStoreRestore.IsEnabled = enabled;
        BtnEnvNicFix.IsEnabled = enabled;
    }

    /// <summary>Определяет winget, Microsoft Store и драйвер сетевой карты; показывает кнопки для недостающего.</summary>
    private async Task RefreshEnvironmentAsync()
    {
        if (_envBusy) return;
        _envBusy = true;
        SetEnvButtonsEnabled(false);
        try
        {
            var wingetOk = await WingetService.IsAvailableAsync();
            string? wingetVersion = null;
            if (wingetOk)
            {
                var (_, lines) = await WingetService.RunAsync("--version", null);
                wingetVersion = lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            }
            var storeOk = await WingetBootstrapService.IsStoreInstalledAsync();
            var nicOk = await Task.Run(() =>
                DriverPackService.FindProblemNetDevices(
                    DriverPackService.GetNetworkDevices(), new DriverPackIndex(), DriverPackService.IsWindows11).Count == 0);
            SetDot(DotEnvWinget, wingetOk);
            TxtEnvWinget.Text = wingetOk && wingetVersion != null
                ? L10n.T("env.wingetVersion", wingetVersion) : L10n.T("env.winget");
            SetDot(DotEnvStore, storeOk);
            SetDot(DotEnvNic, nicOk);
            BtnEnvWingetInstall.Visibility = !wingetOk ? Visibility.Visible : Visibility.Collapsed;
            BtnEnvWingetStore.Visibility = !wingetOk ? Visibility.Visible : Visibility.Collapsed;
            BtnEnvWingetUpdate.Visibility = wingetOk ? Visibility.Visible : Visibility.Collapsed;
            BtnEnvStoreRestore.Visibility = !storeOk ? Visibility.Visible : Visibility.Collapsed;
            BtnEnvNicFix.Visibility = !nicOk ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            LogService.Log(ex.Message);
        }
        finally
        {
            _envBusy = false;
            SetEnvButtonsEnabled(true);
        }
    }

    private async void BtnEnvWingetInstall_Click(object sender, RoutedEventArgs e) => await EnvWingetInstallAsync(false);

    private async void BtnEnvWingetStore_Click(object sender, RoutedEventArgs e) => await EnvWingetInstallAsync(true);

    private async Task EnvWingetInstallAsync(bool withStore)
    {
        if (_envBusy) return;
        _envBusy = true;
        SetEnvButtonsEnabled(false);
        var progress = new Progress<string>(LogService.Log);
        try
        {
            if (withStore) await WingetBootstrapService.RestoreStoreAsync(progress);
            var ok = await WingetBootstrapService.InstallAsync(progress);
            LogService.Log(L10n.T(ok ? "winget.ui.done" : "winget.ui.failed"));
            if (withStore && ok)
                ThemedMessageBox.Show(L10n.T("winget.store.triggered"), L10n.T("env.title"),
                    MessageBoxButton.OK, MessageBoxImage.Information, this);
        }
        catch (Exception ex)
        {
            LogService.Log(ex.Message);
            ThemedMessageBox.Show(L10n.T("env.downloadFailed"), L10n.T("env.title"),
                MessageBoxButton.OK, MessageBoxImage.Warning, this);
        }
        finally
        {
            _envBusy = false;
            await RefreshEnvironmentAsync();
        }
    }

    private async void BtnEnvStoreRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_envBusy) return;
        _envBusy = true;
        SetEnvButtonsEnabled(false);
        try
        {
            await WingetBootstrapService.RestoreStoreAsync(new Progress<string>(LogService.Log));
            ThemedMessageBox.Show(L10n.T("winget.store.triggered"), L10n.T("env.title"),
                MessageBoxButton.OK, MessageBoxImage.Information, this);
        }
        finally
        {
            _envBusy = false;
            await RefreshEnvironmentAsync();
        }
    }

    private async void BtnEnvWingetUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_envBusy) return;
        _envBusy = true;
        SetEnvButtonsEnabled(false);
        try
        {
            await WingetBootstrapService.SelfUpdateAsync(new Progress<string>(LogService.Log));
        }
        finally
        {
            _envBusy = false;
            await RefreshEnvironmentAsync();
        }
    }

    private async void BtnEnvNicFix_Click(object sender, RoutedEventArgs e)
    {
        if (_envBusy) return;
        _envBusy = true;
        SetEnvButtonsEnabled(false);
        try
        {
            MainTabs.SelectedItem = TabDrivers.Parent;
            await TabDrivers.FixNetAsync();
        }
        finally
        {
            _envBusy = false;
            await RefreshEnvironmentAsync();
        }
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow { Owner = this };
        dialog.ShowDialog();
    }

    private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LogService.LogDirectory);
        Process.Start(new ProcessStartInfo(LogService.LogDirectory) { UseShellExecute = true });
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await HandlePackDropAsync(paths);
    }

    public async Task HandlePackDropAsync(string[] paths)
    {
        if (paths.Length == 0) return;
        var dir = ResolvePackDrop(paths[0]);
        if (dir == null)
        {
            ThemedMessageBox.Show(L10n.T("drivers.offline.packInvalid"), L10n.T("drivers.offline.title"),
                MessageBoxButton.OK, MessageBoxImage.Warning, this);
            return;
        }
        MainTabs.SelectedItem = TabDrivers.Parent;
        await TabDrivers.UsePackAsync(dir);
    }

    /// <summary>Папка или файл внутри пака; допускается вложенная папка DriverPack.</summary>
    public static string? ResolvePackDrop(string path)
    {
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (dir == null) return null;
        if (DriverPackService.IsPack(dir)) return dir;
        var nested = Path.Combine(dir, DriverPackService.PackDirName);
        return DriverPackService.IsPack(nested) ? nested : null;
    }

    private void OnLogEntry(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TxtLog.AppendText(line + Environment.NewLine);
            TxtLog.ScrollToEnd();
            StatusText.Content = line.Length > 140 ? line[..140] + "…" : line;
        });
    }
}