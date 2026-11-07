using System.ComponentModel;
using Avalonia.Media.Imaging;

namespace Tumbler.Gui;

/// <summary>Заголовок раздела в списке приложений.</summary>
public sealed record SectionHeader(string Title, int Count);

/// <summary>Строка списка: одно приложение (все процессы с одним именем исполняемого файла) и его тумблер.</summary>
public sealed class AppRow : INotifyPropertyChanged
{
    string _title;
    string? _path;
    Bitmap? _icon;
    bool _isProxied, _isRunning, _isApp, _mine, _visible = true, _portDown, _suspended;
    int _procCount, _connCount, _captured, _portOverride, _effectivePort;

    public AppRow(string matchName)
    {
        MatchName = matchName;
        _title = matchName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Пользователь переключил тумблер (программная установка через <see cref="SetProxied"/> сюда не попадает).</summary>
    public event Action<AppRow>? Toggled;

    /// <summary>Имя исполняемого файла — по нему служба находит процессы приложения.</summary>
    public string MatchName { get; }

    public string Title
    {
        get => _title;
        set => Set(ref _title, value, nameof(Title));
    }

    public string? Path
    {
        get => _path;
        set => Set(ref _path, value, nameof(Path), nameof(CanRestart));
    }

    public Bitmap? Icon
    {
        get => _icon;
        set => Set(ref _icon, value, nameof(Icon), nameof(NoIcon));
    }

    public bool NoIcon => _icon is null;

    public bool IsProxied
    {
        get => _isProxied;
        set
        {
            if (_isProxied == value) return;
            SetProxied(value);
            Toggled?.Invoke(this);
        }
    }

    /// <summary>Свой порт приложения; 0 — идёт через порт по умолчанию.</summary>
    public int PortOverride
    {
        get => _portOverride;
        set => Set(ref _portOverride, value, nameof(PortOverride));
    }

    public int EffectivePort
    {
        get => _effectivePort;
        set => Set(ref _effectivePort, value, nameof(EffectivePort), nameof(PortLabel));
    }

    public bool PortDown
    {
        get => _portDown;
        set => Set(ref _portDown, value, nameof(PortDown));
    }

    public bool Suspended
    {
        get => _suspended;
        set => Set(ref _suspended, value, nameof(Suspended), nameof(Subtitle));
    }

    /// <summary>Сколько процессов приложения служба сейчас перехватывает.</summary>
    public int Captured
    {
        get => _captured;
        set => Set(ref _captured, value, nameof(Captured), nameof(Subtitle));
    }

    public string PortLabel => _effectivePort.ToString();
    public bool IsRunning => _isRunning;
    public bool Mine => _mine;
    public double IconOpacity => _isRunning ? 1 : 0.4;
    public bool CanRestart => _isRunning && _mine && _path is not null;

    public bool Visible
    {
        get => _visible;
        set => Set(ref _visible, value, nameof(Visible));
    }

    public int SectionOrder => _isProxied ? 0 : _isApp ? 1 : 2;

    public string Subtitle
    {
        get
        {
            if (!_isRunning) return $"{MatchName}  ·  не запущено";
            if (_suspended) return $"{MatchName}  ·  VPN недоступен, временно идёт напрямую";
            var net = _connCount == 0 ? "сеть не использует" : Text.Plural(_connCount, "соединение", "соединения", "соединений");
            var line = $"{MatchName}  ·  {Text.Plural(_procCount, "процесс", "процесса", "процессов")}  ·  {net}";
            // Служба перехватывает процессы раз в секунду; если она отстаёт, это видно здесь.
            return _isProxied && _captured < _procCount ? $"{line}  ·  перехвачено {_captured}" : line;
        }
    }

    public void SetProxied(bool value)
    {
        if (_isProxied == value) return;
        _isProxied = value;
        Raise(nameof(IsProxied), nameof(SectionOrder), nameof(Subtitle));
    }

    internal void Update(ProcGroup group)
    {
        Title = group.Title;
        if (group.Path is not null) Path = group.Path;
        if (group.IconPath is not null && _icon is null) Icon = IconCache.Get(group.IconPath);

        bool changed = !_isRunning || _procCount != group.Count || _connCount != group.Connections || _isApp != group.IsApp || _mine != group.Mine;
        bool runningChanged = !_isRunning;
        _isRunning = true;
        _isApp = group.IsApp;
        _mine = group.Mine;
        _procCount = group.Count;
        _connCount = group.Connections;

        if (changed) Raise(nameof(Subtitle), nameof(SectionOrder), nameof(CanRestart));
        if (runningChanged) Raise(nameof(IsRunning), nameof(IconOpacity));
    }

    internal void MarkNotRunning()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _procCount = _connCount = 0;
        Raise(nameof(IsRunning), nameof(IconOpacity), nameof(CanRestart), nameof(Subtitle), nameof(SectionOrder));
    }

    void Set<T>(ref T field, T value, params string[] names)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(names);
    }

    void Raise(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

internal static class IconCache
{
    static readonly Dictionary<string, Bitmap?> Cache = new();

    public static Bitmap? Get(string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;
        Bitmap? bitmap = null;
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                bitmap = Bitmap.DecodeToWidth(stream, 64);
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Битая или неподдерживаемая картинка — покажем заглушку.
        }
        return Cache[path] = bitmap;
    }
}
