namespace Tumbler.Gui;

/// <summary>Приложение из меню рабочего стола (.desktop-файл): человеческое название и иконка.</summary>
public sealed record DesktopApp(string Name, string? IconPath, string File);

/// <summary>
/// Индекс .desktop-файлов. По нему процесс «spotify» превращается в «Spotify» с иконкой, а приложения
/// из меню отделяются от фоновых процессов.
/// </summary>
internal sealed class DesktopIndex
{
    static readonly int[] IconSizes = [48, 64, 96, 128, 256, 32, 24];

    readonly Dictionary<string, DesktopApp> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public DesktopApp? Find(string processName) => _byKey.GetValueOrDefault(processName);

    public static DesktopIndex Load()
    {
        var index = new DesktopIndex();
        foreach (var dir in DataDirs().Select(d => Path.Combine(d, "applications")).Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.desktop", SearchOption.AllDirectories).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files) index.Add(file);
        }
        return index;
    }

    void Add(string file)
    {
        Dictionary<string, string> keys;
        try
        {
            keys = ReadEntry(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (keys.GetValueOrDefault("Type") is not (null or "Application") || keys.GetValueOrDefault("NoDisplay") == "true") return;
        if ((keys.GetValueOrDefault("Name[ru]") ?? keys.GetValueOrDefault("Name")) is not { } name) return;

        var app = new DesktopApp(name, ResolveIcon(keys.GetValueOrDefault("Icon")), file);
        foreach (var key in Keys(file, keys))
            _byKey.TryAdd(key, app);
    }

    /// <summary>
    /// По каким именам процесса узнавать приложение: имя программы из Exec, StartupWMClass и хвост идентификатора
    /// (у Flatpak «com.discordapp.Discord» → «Discord»).
    /// </summary>
    static IEnumerable<string> Keys(string file, Dictionary<string, string> keys)
    {
        if (keys.GetValueOrDefault("Exec") is { } exec && ExecProgram(exec) is { } program) yield return program;
        if (keys.GetValueOrDefault("StartupWMClass") is { Length: > 0 } wmClass) yield return wmClass;
        var id = Path.GetFileNameWithoutExtension(file);
        yield return id;
        if (id.Contains('.')) yield return id[(id.LastIndexOf('.') + 1)..];
    }

    static string? ExecProgram(string exec)
    {
        var tokens = exec.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim('"')).ToList();
        int i = 0;
        // «env VAR=1 program» — пропускаем env и переменные.
        if (i < tokens.Count && Path.GetFileName(tokens[i]) == "env") i++;
        while (i < tokens.Count && tokens[i].Contains('=') && !tokens[i].StartsWith('/')) i++;
        if (i >= tokens.Count) return null;
        var program = Path.GetFileName(tokens[i]);
        // «flatpak run com.app.Id» — настоящее имя внутри песочницы узнаем по хвосту идентификатора.
        return program is "flatpak" or "snap" ? null : program;
    }

    static Dictionary<string, string> ReadEntry(string file)
    {
        var keys = new Dictionary<string, string>();
        bool inEntry = false;
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inEntry = line == "[Desktop Entry]";
                continue;
            }
            if (!inEntry || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq > 0) keys.TryAdd(line[..eq].Trim(), line[(eq + 1)..].Trim());
        }
        return keys;
    }

    /// <summary>Ищет PNG-иконку по имени в темах значков. SVG пропускаем — их не показываем.</summary>
    static string? ResolveIcon(string? icon)
    {
        if (string.IsNullOrEmpty(icon)) return null;
        if (Path.IsPathRooted(icon)) return icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(icon) ? icon : null;

        foreach (var data in DataDirs())
        {
            foreach (int size in IconSizes)
            {
                var path = Path.Combine(data, "icons", "hicolor", $"{size}x{size}", "apps", icon + ".png");
                if (File.Exists(path)) return path;
            }
            var pixmap = Path.Combine(data, "pixmaps", icon + ".png");
            if (File.Exists(pixmap)) return pixmap;
        }
        return null;
    }

    /// <summary>Каталоги данных по XDG, плюс экспорт Flatpak и Snap.</summary>
    static IEnumerable<string> DataDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } h ? h : Path.Combine(home, ".local", "share");
        var dirs = new List<string> { dataHome };
        var system = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        dirs.AddRange(string.IsNullOrEmpty(system) ? ["/usr/local/share", "/usr/share"] : system.Split(':', StringSplitOptions.RemoveEmptyEntries));
        dirs.Add(Path.Combine(dataHome, "flatpak", "exports", "share"));
        dirs.Add("/var/lib/flatpak/exports/share");
        dirs.Add("/var/lib/snapd/desktop");
        return dirs.Distinct();
    }
}
