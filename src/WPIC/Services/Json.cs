using System.Text.Json;

namespace WPIC.Services;

/// <summary>Настройки JSON (разрешены комментарии и запятые в конце — для удобного редактирования конфигов).</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };
}
