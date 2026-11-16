using System.Diagnostics;
using System.Windows;
using WPIC.Services;

namespace WPIC.Views;

public partial class SettingsWindow : Window
{
    private bool _busy;

    public SettingsWindow()
    {
        InitializeComponent();

        CmbTheme.SelectedIndex = ThemeService.CurrentPreference switch
        {
            ThemeService.Light => 1,
            ThemeService.Dark => 2,
            _ => 0
        };
        CmbLang.SelectedIndex = L10n.CurrentPreference switch
        {
            "ru" => 1,
            "en" => 2,
            _ => 0
        };

        TxtVersion.Text = L10n.T("settings.update.current", UpdateService.CurrentVersion.ToString(3));
        ShowPending();
        Loaded += (_, _) => ThemeService.ApplyTitleBar(this, ThemeService.Resolved == ThemeService.Dark);
    }

    private void ShowPending()
    {
        var pending = UpdateService.Pending;
        if (pending != null)
        {
            TxtUpdateStatus.Text = L10n.T("settings.update.available", pending.Version);
            BtnDoUpdate.Visibility = Visibility.Visible;
        }
        else
        {
            TxtUpdateStatus.Text = "";
            BtnDoUpdate.Visibility = Visibility.Collapsed;
        }
    }

    private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        TxtUpdateStatus.Text = L10n.T("settings.update.checking");
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info == null)
            {
                TxtUpdateStatus.Text = L10n.T("settings.update.none");
                BtnDoUpdate.Visibility = Visibility.Collapsed;
            }
            else
            {
                ShowPending();
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BtnDoUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var pending = UpdateService.Pending;
        if (pending == null) return;

        SetBusy(true);
        PbUpdate.Value = 0;
        PbUpdate.Visibility = Visibility.Visible;
        var progress = new Progress<double>(p =>
        {
            PbUpdate.Value = p;
            TxtUpdateStatus.Text = L10n.T("settings.update.downloading", (int)p);
        });
        try
        {
            var path = await UpdateService.DownloadAsync(pending, progress);
            TxtUpdateStatus.Text = L10n.T("settings.update.installing");
            if (UpdateService.IsInstalledFull)
                UpdateService.LaunchInstaller(path);
            else
                UpdateService.LaunchPortableSwap(path);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            TxtUpdateStatus.Text = L10n.T("settings.update.error", ex.Message);
            ThemedMessageBox.Show(L10n.T("settings.update.error", ex.Message), L10n.T("settings.update"),
                MessageBoxButton.OK, MessageBoxImage.Error, this);
        }
        finally
        {
            PbUpdate.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private void BtnContact_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(UpdateService.DiscussionsUrl) { UseShellExecute = true });
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnCheckUpdate.IsEnabled = !busy;
        BtnDoUpdate.IsEnabled = !busy;
        BtnOk.IsEnabled = !busy;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var theme = CmbTheme.SelectedIndex switch
        {
            1 => ThemeService.Light,
            2 => ThemeService.Dark,
            _ => ThemeService.System
        };
        var language = CmbLang.SelectedIndex switch
        {
            1 => "ru",
            2 => "en",
            _ => "system"
        };

        var settings = SettingsService.Load();
        settings.Theme = theme;
        settings.Language = language;
        SettingsService.Save(settings);

        ThemeService.Apply(theme);
        L10n.SetLanguage(language);
        PackageMetaService.LanguagePreference = language;
        DialogResult = true;
    }
}