namespace WPIC.Models;

public class SearchResult
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Source { get; set; } = "";
}

public class UpgradeEntry
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Available { get; set; } = "";
}

/// <summary>Метаданные пакета из `winget show`.</summary>
public class PackageDetails
{
    public string? Publisher { get; set; }
    public string? Description { get; set; }
    public string? Homepage { get; set; }
}
