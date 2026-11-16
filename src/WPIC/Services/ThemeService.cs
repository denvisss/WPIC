using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace WPIC.Services;

/// <summary>
/// Управление темой оформления: светлая, тёмная или «как в системе».
/// Смена темы = полная замена словаря ресурсов (Themes/Dark.xaml или Themes/Light.xaml).
/// Дополнительно красит заголовок окна в тёмный/светлый цвет (DWM).
/// </summary>
public static class ThemeService
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModePre20h1 = 19;

    private static string _appliedResolved = "";

    /// <summary>Текущее предпочтение пользователя (system / light / dark).</summary>
    public static string CurrentPreference { get; private set; } = System;

    /// <summary>Какая тема применена прямо сейчас (light / dark).</summary>
    public static string Resolved => _appliedResolved;

    /// <summary>Использует ли Windows светлую тему приложений.</summary>
    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return true;
        }
    }

    public static string Resolve(string preference) => preference switch
    {
        Light => Light,
        Dark => Dark,
        _ => SystemUsesLightTheme() ? Light : Dark
    };

    /// <summary>Применяет тему (замена словаря ресурсов + заголовки открытых окон). Возвращает применённый вариант.</summary>
    public static string Apply(string preference)
    {
        CurrentPreference = preference;
        var resolved = Resolve(preference);
        if (resolved == _appliedResolved) return resolved;

        var source = resolved == Light
            ? new Uri("pack://application:,,,/Themes/Light.xaml")
            : new Uri("pack://application:,,,/Themes/Dark.xaml");

        var merged = Application.Current.Resources.MergedDictionaries;
        var dictionary = new ResourceDictionary { Source = source };
        if (merged.Count > 0) merged[0] = dictionary;
        else merged.Insert(0, dictionary);

        _appliedResolved = resolved;

        foreach (Window window in Application.Current.Windows)
            ApplyTitleBar(window, resolved == Dark);

        return resolved;
    }

    /// <summary>Тёмный/светлый заголовок окна (DWM, Windows 10 1809+).</summary>
    public static void ApplyTitleBar(Window window, bool dark)
    {
        try
        {
            var helper = new WindowInteropHelper(window);
            var hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero) return;

            var value = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModePre20h1, ref value, sizeof(int));
        }
        catch
        {
            // старые версии Windows без поддержки атрибута — просто светлый заголовок
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
