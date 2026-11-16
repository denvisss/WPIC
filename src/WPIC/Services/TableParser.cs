using System.Text.RegularExpressions;

namespace WPIC.Services;

public class WingetTableRow
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Extra { get; set; } = "";
    public string Source { get; set; } = "";
}

/// <summary>
/// Разбор табличного вывода winget (search / upgrade / list).
/// Столбцы разделены двумя и более пробелами; таблице предшествует строка из дефисов.
/// Локализованные заголовки игнорируются — важна только структура.
/// </summary>
public static class TableParser
{
    private static readonly string[] KnownSources = { "winget", "msstore", "msix", "portable" };

    public static List<WingetTableRow> Parse(IEnumerable<string> lines)
    {
        var rows = new List<WingetTableRow>();
        var headerPassed = false;

        foreach (var raw in lines)
        {
            if (!headerPassed)
            {
                if (raw.TrimStart().StartsWith("---")) headerPassed = true;
                continue;
            }

            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cells = Regex.Split(line, @"\s{2,}")
                             .Where(c => !string.IsNullOrWhiteSpace(c))
                             .ToArray();

            // строка таблицы: [Имя(with spaces), ИД, Версия, (Доступно), (Источник)]
            if (cells.Length < 3) continue;               // итоговые строки и мусор
            var name = cells[0];
            var id = cells[1];
            if (id.Contains(' ')) continue;               // у идентификатора нет пробелов
            if (!cells[2].Any(char.IsDigit) &&
                !cells[2].Equals("Unknown", StringComparison.OrdinalIgnoreCase)) continue; // у версии есть цифры (или Unknown у MS Store)

            var row = new WingetTableRow { Name = name, Id = id, Version = cells[2] };
            var rest = cells.Skip(3).ToList();

            if (rest.Count > 0 && IsSource(rest[^1]))
            {
                row.Source = rest[^1];
                rest.RemoveAt(rest.Count - 1);
            }
            if (rest.Count > 0)
            {
                var extra = string.Join("  ", rest);
                // иногда перед источником всего один пробел: "... 175.1.0.13416 winget"
                var m = Regex.Match(extra, @"\s+(winget|msstore)$", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    row.Source = m.Groups[1].Value;
                    extra = extra[..m.Index].TrimEnd();
                }
                row.Extra = extra; // для upgrade — доступная версия
            }

            rows.Add(row);
        }
        return rows;
    }

    private static bool IsSource(string s) =>
        KnownSources.Contains(s, StringComparer.OrdinalIgnoreCase);
}
