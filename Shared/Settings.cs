using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tumbler;

internal sealed record KnownApp(string Path, string Title);

/// <summary>Именованный набор тумблеров: какие приложения идут через VPN и через какой порт.</summary>
internal sealed class Profile
{
    public string Name { get; set; } = "";

    /// <summary>Имя exe → порт. 0 — порт по умолчанию.</summary>
    public Dictionary<string, int> Apps { get; set; } = new();
}

/// <summary>
/// Собственные настройки «Тумблера». Профили здесь — источник правды; app-config.json службы
/// собирается из активного профиля с учётом паузы и недоступного VPN.
/// </summary>
internal sealed class Settings
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    string _path = "";

    public bool Enabled { get; set; } = true;
    public bool AutoApply { get; set; } = true;

    /// <summary>0 — ещё не задан, берём порт из конфига службы.</summary>
    public int DefaultPort { get; set; }

    /// <summary>VPN недоступен: false — выбранные приложения остаются без сети, true — временно идут напрямую.</summary>
    public bool FallbackDirect { get; set; }

    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }

    /// <summary>Справка уже открывалась сама при первом запуске.</summary>
    public bool HelpShown { get; set; }

    /// <summary>Глобальная клавиша паузы, например «Ctrl+Alt+P». Пустая строка — выключена.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+P";

    public string ActiveProfile { get; set; } = "";
    public List<Profile> Profiles { get; set; } = new();

    /// <summary>Где лежат выбранные приложения — чтобы показывать иконку, когда они закрыты.</summary>
    public Dictionary<string, KnownApp> Known { get; set; } = new();

    [JsonIgnore]
    public Profile Active => Profiles.First(p => p.Name == ActiveProfile);

    public static Settings Load(string? overridePath)
    {
        var path = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tumbler", "settings.json");

        Settings? settings = null;
        try
        {
            if (File.Exists(path)) settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            // Битый файл настроек не повод не запускаться — начнём с настроек по умолчанию.
        }

        settings ??= new Settings();
        settings._path = path;
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(this, Options));
    }
}
