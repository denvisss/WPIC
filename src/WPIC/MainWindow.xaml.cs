using System.Diagnostics;
using System.IO;
using System.Windows;
using WPIC.Services;
using WPIC.Views;

namespace WPIC;

public partial class MainWindow : Window
{
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
        };
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