namespace WPIC;

/// <summary>Аргументы командной строки (в том числе для отладки интерфейса без прав администратора).</summary>
public static class AppArgs
{
    public static string? ThemeOverride { get; private set; }
    public static string? LangOverride { get; private set; }
    public static string? SearchQuery { get; private set; }

    public static void Parse(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
                ThemeOverride = arg[8..].ToLowerInvariant();
            else if (arg.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase))
                LangOverride = arg[7..].ToLowerInvariant();
            else if (arg.StartsWith("--search=", StringComparison.OrdinalIgnoreCase))
                SearchQuery = Uri.UnescapeDataString(arg[9..]);
        }
    }
}
