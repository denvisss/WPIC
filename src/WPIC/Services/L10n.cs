using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;

namespace WPIC.Services;

/// <summary>
/// Локализация интерфейса. Языки описаны в файлах Resources/strings.{lang}.json.
/// Язык выбирается в настройках: ru / en / как в системе (по умолчанию).
/// </summary>
public sealed class L10n : INotifyPropertyChanged
{
    private static readonly L10n InstanceField = new();
    private Dictionary<string, string> _strings = new();

    public static L10n Instance => InstanceField;

    public static string CurrentLanguage => InstanceField._lang;

    /// <summary>Предпочтение пользователя: system | ru | en.</summary>
    public static string CurrentPreference { get; private set; } = "system";

    public event PropertyChangedEventHandler? PropertyChanged;

    private string _lang = "ru";

    private L10n()
    {
    }

    private static string ResourcesDir => Path.Combine(AppContext.BaseDirectory, "Resources");

    public static void Initialize(string preference) => SetLanguage(preference);

    public static void SetLanguage(string preference)
    {
        CurrentPreference = preference;
        var lang = preference switch
        {
            "ru" => "ru",
            "en" => "en",
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en"
        };

        var strings = LoadFile(lang) ?? LoadFile("en") ?? new Dictionary<string, string>();
        InstanceField._lang = lang;
        InstanceField._strings = strings;

        // null = «обновить все биндинги» — интерфейс переключается на лету
        InstanceField.PropertyChanged?.Invoke(InstanceField, new PropertyChangedEventArgs(null));
    }

    private static Dictionary<string, string>? LoadFile(string lang)
    {
        var text = TryReadFile(Path.Combine(ResourcesDir, $"strings.{lang}.json"))
                   ?? ReadEmbedded($"WPIC.Resources.strings.{lang}.json");
        if (text == null) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(text, Json.Options);
        }
        catch
        {
            // повреждённый файл локализации не критичен — вернётся ключ
            return null;
        }
    }

    private static string? TryReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadEmbedded(string name)
    {
        using var stream = typeof(L10n).Assembly.GetManifestResourceStream(name);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public string this[string key] => _strings.GetValueOrDefault(key, key);

    /// <summary>Перевод строки с подстановкой параметров: L10n.T("key", 1, 2).</summary>
    public static string T(string key, params object?[] args)
    {
        var value = InstanceField[key];
        return args.Length == 0 ? value : string.Format(CultureInfo.InvariantCulture, value, args);
    }
}

/// <summary>Использование в XAML: Text="{loc:L10n main.ready}". Значение обновляется при смене языка.</summary>
public class L10nExtension : MarkupExtension
{
    public string Key { get; set; }

    public L10nExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        // Возврат Binding из ProvideValue парсер применяет как обычное значение (падает на DP),
        // поэтому значение ставим сами и обновляем по событию смены языка.
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget target &&
            target.TargetObject is DependencyObject obj &&
            target.TargetProperty is DependencyProperty dp)
        {
            var weak = new WeakReference(obj);
            PropertyChangedEventHandler? handler = null;
            handler = (_, _) =>
            {
                if (weak.Target is DependencyObject alive) alive.SetValue(dp, L10n.Instance[Key]);
                else L10n.Instance.PropertyChanged -= handler;
            };
            L10n.Instance.PropertyChanged += handler;
        }
        return L10n.Instance[Key];
    }
}
