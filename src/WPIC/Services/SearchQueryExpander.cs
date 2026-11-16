namespace WPIC.Services;

/// <summary>
/// Расширение поискового запроса для winget: сам winget сопоставляет кириллицу
/// только с русскими именами пакетов, поэтому кириллический запрос дополняем
/// алиасами и транслитом, а латинский — обратными алиасами (русские имена).
/// </summary>
public static class SearchQueryExpander
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["яндекс"] = "yandex",
        ["гугл"] = "google",
        ["хром"] = "chrome",
        ["хромиум"] = "chromium",
        ["фаерфокс"] = "firefox",
        ["огнелис"] = "firefox",
        ["мозилла"] = "mozilla",
        ["опера"] = "opera",
        ["вивальди"] = "vivaldi",
        ["браузер"] = "browser",
        ["телеграм"] = "telegram",
        ["телега"] = "telegram",
        ["вотсап"] = "whatsapp",
        ["ватсап"] = "whatsapp",
        ["вайбер"] = "viber",
        ["скайп"] = "skype",
        ["зум"] = "zoom",
        ["дискорд"] = "discord",
        ["вконтакте"] = "vk",
        ["вк"] = "vk",
        ["спотифай"] = "spotify",
        ["эдж"] = "edge",
        ["виндовс"] = "windows",
        ["офис"] = "office",
        ["ворд"] = "word",
        ["эксель"] = "excel",
        ["поверпоинт"] = "powerpoint",
        ["аутлук"] = "outlook",
        ["тимс"] = "teams",
        ["блокнот"] = "notepad",
        ["нотпад"] = "notepad",
        ["винрар"] = "winrar",
        ["архиватор"] = "zip",
        ["торрент"] = "torrent",
        ["стим"] = "steam",
        ["эпик"] = "epic",
        ["обс"] = "obs",
        ["виртуалбокс"] = "virtualbox",
        ["вмваре"] = "vmware",
        ["питон"] = "python",
        ["джава"] = "java",
        ["гит"] = "git",
        ["гитхаб"] = "github",
        ["вскод"] = "vscode",
        ["докер"] = "docker",
        ["касперский"] = "kaspersky",
        ["аваст"] = "avast",
        ["суматра"] = "sumatra",
        ["фоксит"] = "foxit",
        ["акробат"] = "acrobat",
        ["влк"] = "vlc",
        ["аимп"] = "aimp",
        ["винамп"] = "winamp",
        ["кинопоиск"] = "kinopoisk",
        ["иви"] = "ivi",
        ["окко"] = "okko",
        ["слак"] = "slack",
        ["саблайм"] = "sublime",
        ["фар"] = "far",
        ["путти"] = "putty",
        ["винскп"] = "winscp",
        ["файлзилла"] = "filezilla",
        ["терминал"] = "terminal",
        ["повершелл"] = "powershell",
        ["музыка"] = "music",
        ["диск"] = "disk",
        ["почта"] = "mail",
        ["карты"] = "maps",
        ["мессенджер"] = "messenger",
        ["плеер"] = "player",
        ["драйвер"] = "driver"
    };

    private static readonly Dictionary<string, string> ReverseAliases = BuildReverse();

    private static Dictionary<string, string> BuildReverse()
    {
        var reverse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ru, en) in Aliases)
            reverse.TryAdd(en, ru);
        return reverse;
    }

    private static readonly Dictionary<char, string> RuToEn = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e",
        ['ё'] = "e", ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k",
        ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r",
        ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "",
        ['э'] = "e", ['ю'] = "yu", ['я'] = "ya"
    };

    /// <summary>Варианты запроса для winget: исходный первым, далее алиасы и транслит (до 4 штук).</summary>
    public static IReadOnlyList<string> Expand(string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return Array.Empty<string>();

        var variants = new List<string> { q };
        void Add(string v)
        {
            v = v.Trim();
            if (v.Length > 0 && !variants.Contains(v, StringComparer.OrdinalIgnoreCase))
                variants.Add(v);
        }

        if (HasCyrillic(q))
        {
            var mapped = MapWords(q, Aliases);
            Add(mapped);
            if (HasCyrillic(mapped)) Add(TranslitRuToEn(mapped));
        }
        else
        {
            Add(MapWords(q, ReverseAliases));
        }

        return variants.Count <= 4 ? variants : variants.GetRange(0, 4);
    }

    public static string TranslitRuToEn(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length * 2);
        foreach (var c in text)
        {
            var lower = char.ToLowerInvariant(c);
            sb.Append(RuToEn.TryGetValue(lower, out var en) ? en : lower.ToString());
        }
        return sb.ToString();
    }

    private static bool HasCyrillic(string s) =>
        s.Any(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё');

    private static string MapWords(string query, Dictionary<string, string> map) =>
        string.Join(' ', query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => map.TryGetValue(w, out var mapped) ? mapped : w));
}