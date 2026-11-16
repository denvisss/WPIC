namespace WPIC.Models;

public class AppCatalog
{
    public List<AppCategory> Categories { get; set; } = new();
}

public class AppCategory
{
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    public List<AppPackage> Apps { get; set; } = new();
}

public class AppPackage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string WingetId { get; set; } = "";
    /// <summary>Все winget-пакеты записи, если их несколько (например, редакции VC++ 2005–2022); иначе только WingetId.</summary>
    public List<string> WingetIds { get; set; } = new();
    /// <summary>Компонент Windows (DISM FeatureName), включаемый при установке, например NetFx3.</summary>
    public string? WindowsFeature { get; set; }
    public string? Description { get; set; }
    public string? DescriptionEn { get; set; }

    public IReadOnlyList<string> InstallTargets =>
        WingetIds.Count > 0 ? WingetIds :
        WingetId.Length > 0 ? new[] { WingetId } : Array.Empty<string>();
}
