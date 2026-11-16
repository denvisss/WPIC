using System.Windows;
using System.Windows.Threading;
using WPIC.Services;
using WPIC.Views;

namespace WPIC;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            LogService.Log(L10n.T("main.log.unhandled", args.Exception));
            ThemedMessageBox.Show(args.Exception.Message, L10n.T("apps.err.title"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppArgs.Parse(e.Args);

        var settings = SettingsService.Load();

        // Тема оформления (по умолчанию — как в системе)
        ThemeService.Apply(AppArgs.ThemeOverride ?? settings.Theme);

        // Язык интерфейса (по умолчанию — как в системе)
        var language = AppArgs.LangOverride ?? settings.Language;
        L10n.Initialize(language);
        PackageMetaService.LanguagePreference = language;

        // Следим за сменой системной темы, пока выбран режим «Как в системе»
        var watcher = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background,
            (_, _) =>
            {
                if (ThemeService.CurrentPreference == ThemeService.System)
                    ThemeService.Apply(ThemeService.System);
            }, Dispatcher);
        watcher.Start();
    }
}
