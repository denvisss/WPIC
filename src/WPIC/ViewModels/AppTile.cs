namespace WPIC.ViewModels;

using System.Windows.Media;

/// <summary>Цветная плитка-заглушка для приложений, у которых не загрузилась иконка.</summary>
public static class AppTile
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x4C, 0xC2, 0xFF),
        Color.FromRgb(0x7B, 0xD8, 0x8F),
        Color.FromRgb(0xF2, 0xA6, 0x5A),
        Color.FromRgb(0xE5, 0x7C, 0x7C),
        Color.FromRgb(0xB4, 0x8C, 0xFF),
        Color.FromRgb(0x5A, 0xC8, 0xD8),
        Color.FromRgb(0xF2, 0xC9, 0x4C),
        Color.FromRgb(0x9A, 0xA7, 0xFF),
        Color.FromRgb(0x6B, 0xCB, 0x77),
        Color.FromRgb(0xFF, 0x8F, 0xB1)
    };

    public static string Letter(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[0].ToString().ToUpperInvariant();

    public static Brush Brush(string id) =>
        new SolidColorBrush(Palette[(id.GetHashCode() & 0x7FFFFFFF) % Palette.Length]);
}