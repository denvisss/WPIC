using System.Windows;
using WPIC.Services;

namespace WPIC.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        switch (ThemeService.CurrentPreference)
        {
            case ThemeService.Light: OptLight.IsChecked = true; break;
            case ThemeService.Dark: OptDark.IsChecked = true; break;
            default: OptSystem.IsChecked = true; break;
        }

        switch (L10n.CurrentPreference)
        {
            case "ru": OptLangRu.IsChecked = true; break;
            case "en": OptLangEn.IsChecked = true; break;
            default: OptLangSystem.IsChecked = true; break;
        }

        Loaded += (_, _) => ThemeService.ApplyTitleBar(this, ThemeService.Resolved == ThemeService.Dark);
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var theme =
            OptLight.IsChecked == true ? ThemeService.Light :
            OptDark.IsChecked == true ? ThemeService.Dark :
            ThemeService.System;

        var language =
            OptLangRu.IsChecked == true ? "ru" :
            OptLangEn.IsChecked == true ? "en" :
            "system";

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