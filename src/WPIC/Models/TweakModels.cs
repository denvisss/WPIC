namespace WPIC.Models;

public class TweakCatalog
{
    public List<Tweak> Tweaks { get; set; } = new();
}

public class Tweak
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "system";
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Уровень риска: green | yellow | red</summary>
    public string Risk { get; set; } = "green";

    public int MinBuild { get; set; } = 17763;
    public int MaxBuild { get; set; }
    public string[] Editions { get; set; } = [];
    public bool SignOutRequired { get; set; }
    public List<TweakAction> Actions { get; set; } = new();
}

public class TweakAction
{
    public string Type { get; set; } = "";
    public string Parameter { get; set; } = "";
    public int MinBuild { get; set; }
    public int MaxBuild { get; set; }

    // --- registry ---
    public string Key { get; set; } = "";
    public string ValueName { get; set; } = "";
    public string RegType { get; set; } = "DWord";
    public string Value { get; set; } = "";

    // --- command ---
    public string Command { get; set; } = "";
    public string? UndoCommand { get; set; }
}

/// <summary>Запись в резервной копии реестра (создаётся перед применением твика).</summary>
public class RegistryBackupEntry
{
    public string Tweak { get; set; } = "";
    public string Type { get; set; } = "registry";
    public string Parameter { get; set; } = "";
    public int MinBuild { get; set; }
    public int MaxBuild { get; set; }
    public string Key { get; set; } = "";
    public string ValueName { get; set; } = "";
    public string RegType { get; set; } = "";
    public bool Existed { get; set; }
    public string? Value { get; set; }
}
