using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tumbler;

/// <summary>Строка списка: одно приложение (все процессы с одним именем exe) и его тумблер.</summary>
public sealed class AppRow : INotifyPropertyChanged
{
    string _title;
    string? _path;
    ImageSource? _icon;
    bool _isProxied, _isRunning, _hasWindow, _userApp, _visible = true, _portDown, _suspended;
    int _procCount, _connCount, _portOverride, _effectivePort;

    public AppRow(string matchName)
    {
        MatchName = matchName;
        _title = matchName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Пользователь переключил тумблер (программная установка через <see cref="SetProxied"/> сюда не попадает).</summary>
    public event Action<AppRow>? Toggled;

    /// <summary>Имя exe без расширения — именно его ProxiFyre сравнивает с именем процесса.</summary>
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

    public ImageSource? Icon
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

    /// <summary>Порт, через который приложение идёт на самом деле (выставляет главное окно).</summary>
    public int EffectivePort
    {
        get => _effectivePort;
        set => Set(ref _effectivePort, value, nameof(EffectivePort), nameof(PortLabel));
    }

    /// <summary>Прокси на порту приложения сейчас не отвечает.</summary>
    public bool PortDown
    {
        get => _portDown;
        set => Set(ref _portDown, value, nameof(PortDown));
    }

    /// <summary>VPN недоступен, и приложение временно пущено напрямую.</summary>
    public bool Suspended
    {
        get => _suspended;
        set => Set(ref _suspended, value, nameof(Suspended), nameof(Subtitle));
    }

    public string PortLabel => _effectivePort.ToString();

    public bool IsRunning => _isRunning;
    public bool UserApp => _userApp;
    public double IconOpacity => _isRunning ? 1 : 0.4;

    // Приложения из Microsoft Store нельзя запустить по пути к exe, поэтому перезапуск для них не предлагаем.
    public bool CanRestart => _isRunning && _path is not null && !_path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    public bool Visible
    {
        get => _visible;
        set => Set(ref _visible, value, nameof(Visible));
    }

    public int SectionOrder => _isProxied ? 0 : _hasWindow ? 1 : 2;

    public string Section => SectionOrder switch
    {
        0 => "ИДУТ ЧЕРЕЗ VPN",
        1 => "ОТКРЫТЫЕ ПРИЛОЖЕНИЯ",
        _ => "ФОНОВЫЕ ПРОЦЕССЫ",
    };

    public string Subtitle
    {
        get
        {
            if (!_isRunning) return $"{MatchName}.exe  ·  не запущено";
            if (_suspended) return $"{MatchName}.exe  ·  VPN недоступен, временно идёт напрямую";
            var net = _connCount == 0 ? "сеть не использует" : Text.Plural(_connCount, "соединение", "соединения", "соединений");
            return $"{MatchName}.exe  ·  {Text.Plural(_procCount, "процесс", "процесса", "процессов")}  ·  {net}";
        }
    }

    public void SetProxied(bool value)
    {
        if (_isProxied == value) return;
        _isProxied = value;
        Raise(nameof(IsProxied), nameof(SectionOrder), nameof(Section));
    }

    internal void Update(ProcGroup group)
    {
        Title = group.Title;
        if (!string.Equals(_path, group.Path, StringComparison.OrdinalIgnoreCase))
        {
            Path = group.Path;
            Icon = IconCache.Get(group.Path);
        }

        bool sectionChanged = _hasWindow != group.HasWindow;
        bool runningChanged = !_isRunning;
        bool textChanged = runningChanged || _procCount != group.Count || _connCount != group.Connections;

        _isRunning = true;
        _hasWindow = group.HasWindow;
        _userApp = group.UserApp;
        _procCount = group.Count;
        _connCount = group.Connections;

        if (textChanged) Raise(nameof(Subtitle));
        if (runningChanged) Raise(nameof(IsRunning), nameof(IconOpacity), nameof(CanRestart));
        if (sectionChanged) Raise(nameof(SectionOrder), nameof(Section));
    }

    internal void MarkNotRunning()
    {
        if (!_isRunning && !_hasWindow) return;
        _isRunning = false;
        _hasWindow = false;
        _procCount = _connCount = 0;
        Raise(nameof(IsRunning), nameof(IconOpacity), nameof(CanRestart), nameof(Subtitle), nameof(SectionOrder), nameof(Section));
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
    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;

        ImageSource? image = null;
        var icons = new IntPtr[1];
        if (Native.PrivateExtractIcons(path, 0, 48, 48, icons, new uint[1], 1, 0) > 0 && icons[0] != IntPtr.Zero)
        {
            try
            {
                image = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
            }
            finally
            {
                Native.DestroyIcon(icons[0]);
            }
        }
        return Cache[path] = image;
    }
}
