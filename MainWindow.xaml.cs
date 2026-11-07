using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Tumbler;

/// <param name="ActionId">Что делает кнопка: elevate, setup или port:НОМЕР.</param>
public sealed record Banner(string Text, bool IsError, string? ActionLabel = null, string? ActionId = null)
{
    public bool HasAction => ActionLabel is not null;
}

public partial class MainWindow : Window
{
    const string FallbackInstallDir = @"C:\Program Files\ProxiFyre";
    const string DefaultProfileName = "Основной";

    readonly Options _options;
    readonly bool _sandbox;
    readonly bool _canApply;
    readonly string _installDir;
    readonly PfConfig _config;
    readonly Settings _settings;
    readonly bool _firstRun;

    readonly ObservableCollection<AppRow> _rows = new();
    readonly ListCollectionView _view;
    readonly Dictionary<string, AppRow> _byName = new(StringComparer.OrdinalIgnoreCase);
    readonly ObservableCollection<Banner> _banners = new();
    readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    readonly DispatcherTimer _applyTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    // Порт считается упавшим (или ожившим) после двух одинаковых проверок подряд,
    // чтобы единичный сбой не перезапускал службу.
    readonly Dictionary<int, (bool Alive, int Streak)> _probes = new();
    readonly HashSet<int> _downPorts = new();

    // Подсказки портов: из настроек Karing и найденные на этом компьютере работающие SOCKS5-прокси.
    List<PortPreset> _presets;
    List<PortPreset> _found = new();
    DateTime _lastProxySearch;
    bool _searchingProxies;
    int _hiddenScans;

    Tray? _tray;
    Hotkey? _hotkey;
    HelpWindow? _help;
    ScanResult? _last;
    bool _loading = true, _scanning, _applying, _applyAgain, _dirty, _exiting, _firstScanDone;
    string _shape = "";
    int _port;
    string? _applyError;
    DateTime? _appliedAt;

    public MainWindow(Options options)
    {
        InitializeComponent();
        _options = options;
        _sandbox = options.ConfigPath is not null;
        _canApply = _sandbox || Elevation.IsAdmin;
        _installDir = ServiceCtl.InstallDir() ?? FallbackInstallDir;

        var configPath = options.ConfigPath ?? Path.Combine(_installDir, "app-config.json");
        _config = PfConfig.Load(configPath);
        _settings = Settings.Load(_sandbox ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "tumbler-settings.json") : null);
        _presets = Karing.Presets();
        _port = _settings.DefaultPort > 0 ? _settings.DefaultPort : _config.DefaultPort;
        // Свежая установка: порт ещё никто не выбирал, так что возьмём первый найденный прокси.
        _firstRun = _settings.DefaultPort == 0 && _config.Rules.Count == 0;
        EnsureProfiles();

        _view = new ListCollectionView(_rows) { Filter = row => ((AppRow)row).Visible };
        _view.SortDescriptions.Add(new SortDescription(nameof(AppRow.SectionOrder), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(AppRow.Title), ListSortDirection.Ascending));
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AppRow.Section)));
        AppList.ItemsSource = _view;
        Banners.ItemsSource = _banners;

        HostText.Text = $"{_config.Host} :";
        PortBox.Text = _port.ToString();
        UdpBox.IsChecked = _config.Udp;
        AutoApplyBox.IsChecked = _settings.AutoApply;
        MasterSwitch.IsChecked = _settings.Enabled;
        Presets.ItemsSource = _presets;
        Icon = Chrome.DrawWindowIcon();

        LoadProfile(_settings.Active);
        _loading = false;

        _scanTimer.Tick += async (_, _) => await ScanAsync();
        _applyTimer.Tick += async (_, _) =>
        {
            _applyTimer.Stop();
            await ApplyAsync();
        };
        // В трее список не виден — сканируем реже, только чтобы следить за VPN.
        IsVisibleChanged += (_, _) => _scanTimer.Interval = TimeSpan.FromSeconds(IsVisible ? 2.5 : 5);

        if (options.Screenshot is not null) MoveOffscreen(this);

        Loaded += (_, _) =>
        {
            // Новому пользователю сразу показываем, что делать с VPN-клиентом.
            if (!_firstRun || _settings.HelpShown || _options.IsDev) return;
            _settings.HelpShown = true;
            _settings.Save();
            Dispatcher.BeginInvoke(() => ShowHelp(topic: 0), DispatcherPriority.ApplicationIdle);
        };

        SourceInitialized += async (_, _) =>
        {
            Chrome.UseDarkTitleBar(this);
            StartTrayAndHotkey();
            RefreshChrome();
            await ScanAsync();
            _scanTimer.Start();
            _ = FindProxiesAsync();
            RefreshAutostartPath();
            await RunDevScriptAsync();
        };
    }

    /// <summary>Запуск без окна (автозагрузка): значок в трее есть, окно появится по клику.</summary>
    public void StartHidden() => new WindowInteropHelper(this).EnsureHandle();

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    bool Paused => !_settings.Enabled;

    // ---------- Профили ----------

    void EnsureProfiles()
    {
        if (_settings.Profiles.Count == 0)
        {
            // Первый запуск: текущий конфиг службы становится профилем «Основной».
            var profile = new Profile { Name = DefaultProfileName };
            foreach (var (app, port) in _config.Rules)
                profile.Apps[AppName(app)] = port == _port ? 0 : port;
            _settings.Profiles.Add(profile);
        }
        if (_settings.Profiles.All(p => p.Name != _settings.ActiveProfile))
            _settings.ActiveProfile = _settings.Profiles[0].Name;
    }

    void LoadProfile(Profile profile)
    {
        foreach (var row in _rows)
        {
            row.SetProxied(false);
            row.PortOverride = 0;
        }
        foreach (var (app, port) in profile.Apps)
        {
            var row = AddRow(app);
            row.SetProxied(true);
            row.PortOverride = port;
        }
        ProfileName.Text = profile.Name;
        UpdateRouting();
        UpdateVisibility();
    }

    void SaveProfile() =>
        _settings.Active.Apps = _rows.Where(r => r.IsProxied).ToDictionary(r => r.MatchName, r => r.PortOverride);

    void SwitchProfile(string name)
    {
        if (name == _settings.ActiveProfile || _settings.Profiles.All(p => p.Name != name)) return;
        _settings.ActiveProfile = name;
        _loading = true;
        LoadProfile(_settings.Active);
        _loading = false;
        MarkDirty(applyNow: _settings.AutoApply);
    }

    /// <summary>Новый профиль — копия текущих тумблеров; он сразу становится активным.</summary>
    void CreateProfile(string name)
    {
        _settings.Profiles.Add(new Profile { Name = name, Apps = new(_settings.Active.Apps) });
        _settings.ActiveProfile = name;
        ProfileName.Text = name;
        _settings.Save();
        RefreshChrome();
    }

    string? ValidateProfileName(string name, string? current = null) =>
        name.Length == 0 ? "Введи название."
        : name.Length > 30 ? "Слишком длинное название."
        : name != current && _settings.Profiles.Any(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)) ? "Профиль с таким названием уже есть."
        : null;

    void Profile_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ProfileButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var profile in _settings.Profiles)
        {
            var name = profile.Name;
            menu.Items.Add(Item($"{name}  ·  {profile.Apps.Count}", () => SwitchProfile(name), isChecked: name == _settings.ActiveProfile));
        }
        menu.Items.Add(new Separator());

        var active = _settings.ActiveProfile;
        menu.Items.Add(Item("Новый профиль…", () =>
        {
            var name = PromptWindow.Ask(this, "Название нового профиля. В него скопируются текущие тумблеры — дальше меняй как нужно.", "", n => ValidateProfileName(n));
            if (name is not null) CreateProfile(name);
        }));
        menu.Items.Add(Item($"Переименовать «{active}»…", () =>
        {
            var name = PromptWindow.Ask(this, "Новое название профиля.", active, n => ValidateProfileName(n, active));
            if (name is null) return;
            _settings.Active.Name = name;
            _settings.ActiveProfile = name;
            ProfileName.Text = name;
            _settings.Save();
            RefreshChrome();
        }));

        var delete = Item($"Удалить «{active}»", () =>
        {
            var answer = MessageBox.Show(this, $"Удалить профиль «{active}»?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
            _settings.Profiles.Remove(_settings.Active);
            SwitchProfile(_settings.Profiles[0].Name);
        });
        delete.IsEnabled = _settings.Profiles.Count > 1;
        menu.Items.Add(delete);

        menu.IsOpen = true;
    }

    static MenuItem Item(string header, Action click, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked };
        item.Click += (_, _) => click();
        return item;
    }

    // ---------- Список приложений ----------

    /// <summary>В конфиге имя может быть записано как «app», «app.exe» или путём — тумблеру нужно имя exe без расширения.</summary>
    static string AppName(string configEntry)
    {
        var name = configEntry.Trim();
        if (name.Contains('\\') || name.Contains('/')) name = Path.GetFileName(name);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    AppRow AddRow(string name)
    {
        if (_byName.TryGetValue(name, out var existing)) return existing;

        var row = new AppRow(name) { EffectivePort = _port };
        if (_settings.Known.TryGetValue(name.ToLowerInvariant(), out var known))
        {
            row.Title = known.Title;
            if (File.Exists(known.Path))
            {
                row.Path = known.Path;
                row.Icon = IconCache.Get(known.Path);
            }
        }
        row.Toggled += _ =>
        {
            MarkDirty();
            // Строка переезжает в другой раздел; перестраиваем список после того, как тумблер закончит обработку клика.
            Dispatcher.BeginInvoke(UpdateVisibility);
        };

        _rows.Add(row);
        _byName[name] = row;
        return row;
    }

    async Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        bool portsChanged;
        try
        {
            string host = _config.Host;
            var ports = PortsInUse().Append(_port).ToHashSet();
            if (TryGetPort(out int typed)) ports.Add(typed);

            _last = await Task.Run(() => Scanner.Scan(host, ports));
            MergeRows(_last);
            portsChanged = UpdateProbes(_last);
            UpdateRouting();
            RefreshChrome();
        }
        finally
        {
            _scanning = false;
        }

        if (_last.PortAlive.GetValueOrDefault(_port, true) == false && DateTime.UtcNow - _lastProxySearch > TimeSpan.FromSeconds(15))
            _ = FindProxiesAsync();
        TrimMemoryWhileHidden();

        if (!_canApply || _applying) return;
        if (!_firstScanDone)
        {
            // Профиль — источник правды. Если конфиг службы с ним разошёлся (например, «Тумблер» был закрыт,
            // пока VPN лежал), один раз приводим службу в соответствие.
            _firstScanDone = true;
            if (!InSync()) await ApplyAsync();
        }
        else if (portsChanged && _settings.FallbackDirect && !Paused)
        {
            await ApplyAsync();
            NotifyFallback();
        }
    }

    void MergeRows(ScanResult scan)
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in scan.Groups)
        {
            if (IsProtected(group.BaseName, scan)) continue;
            running.Add(group.BaseName);
            AddRow(group.BaseName).Update(group);
        }

        foreach (var row in _rows.ToList())
        {
            if (running.Contains(row.MatchName)) continue;
            if (row.IsProxied)
            {
                row.MarkNotRunning();
            }
            else
            {
                _rows.Remove(row);
                _byName.Remove(row.MatchName);
            }
        }
        UpdateVisibility();
    }

    /// <summary>Сам прокси-клиент, ProxiFyre и «Тумблер» проксировать нельзя: получится петля.</summary>
    bool IsProtected(string baseName, ScanResult? scan) =>
        baseName.Equals("Tumbler", StringComparison.OrdinalIgnoreCase)
        || baseName.StartsWith("ProxiFyre", StringComparison.OrdinalIgnoreCase)
        || scan?.PortOwners.Values.Contains(baseName, StringComparer.OrdinalIgnoreCase) == true
        || _config.Excludes.Any(e => (baseName + ".exe").Contains(e, StringComparison.OrdinalIgnoreCase));

    void UpdateVisibility()
    {
        var search = SearchBox.Text.Trim();
        bool showAll = ShowAllBox.IsChecked == true;
        foreach (var row in _rows)
        {
            bool inScope = showAll || row.IsProxied || row.UserApp || !row.IsRunning;
            bool matches = search.Length == 0
                || row.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || row.MatchName.Contains(search, StringComparison.CurrentCultureIgnoreCase);
            row.Visible = inScope && matches;
        }
        EmptyHint.Visibility = _rows.Any(r => r.Visible) ? Visibility.Collapsed : Visibility.Visible;

        // Перестраиваем разделы и сортировку только когда они реально изменились: полная перестройка
        // пересоздаёт строки, а сканирование идёт каждые пару секунд.
        var shape = string.Join('|', _rows.Select(r => $"{r.MatchName}:{r.SectionOrder}:{r.Visible}:{r.Title}"));
        if (shape == _shape) return;
        _shape = shape;
        _view.Refresh();
    }

    // ---------- Порты и маршруты ----------

    int PortOf(AppRow row) => row.PortOverride > 0 ? row.PortOverride : _port;

    IEnumerable<int> PortsInUse() => _rows.Where(r => r.IsProxied).Select(PortOf).Distinct();

    /// <summary>Что должно быть в конфиге службы прямо сейчас: активный профиль минус пауза и минус приложения на упавшем VPN.</summary>
    Dictionary<string, int> DesiredRules()
    {
        var rules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (Paused) return rules;
        foreach (var row in _rows.Where(r => r.IsProxied))
        {
            int port = PortOf(row);
            if (_settings.FallbackDirect && _downPorts.Contains(port)) continue;
            rules[row.MatchName] = port;
        }
        return rules;
    }

    bool InSync()
    {
        var desired = DesiredRules();
        var actual = _config.Rules.ToDictionary(r => AppName(r.Key), r => r.Value, StringComparer.OrdinalIgnoreCase);
        bool sameRules = desired.Count == actual.Count
                         && desired.All(r => actual.TryGetValue(r.Key, out int port) && port == r.Value);
        bool sameRunState = _sandbox || _last is null || _last.Service == ServiceState.NotInstalled
                            || (desired.Count > 0) == (_last.Service == ServiceState.Running);
        return sameRules && sameRunState;
    }

    /// <returns>true, если какой-то из используемых портов упал или ожил.</returns>
    bool UpdateProbes(ScanResult scan)
    {
        var inUse = PortsInUse().ToHashSet();
        bool changed = _downPorts.RemoveWhere(p => !inUse.Contains(p)) > 0;

        foreach (int port in inUse)
        {
            if (!scan.PortAlive.TryGetValue(port, out bool alive)) continue;
            var previous = _probes.GetValueOrDefault(port, (Alive: alive, Streak: 0));
            int streak = previous.Alive == alive ? previous.Streak + 1 : 1;
            _probes[port] = (alive, streak);

            if (streak >= 2 || !_firstScanDone)
                changed |= alive ? _downPorts.Remove(port) : _downPorts.Add(port);
        }
        return changed;
    }

    void UpdateRouting()
    {
        foreach (var row in _rows)
        {
            int port = PortOf(row);
            row.EffectivePort = port;
            row.PortDown = row.IsProxied && _last?.PortAlive.GetValueOrDefault(port, true) == false;
            row.Suspended = row.IsProxied && !Paused && _settings.FallbackDirect && _downPorts.Contains(port);
        }
    }

    void NotifyFallback()
    {
        int suspended = _rows.Count(r => r.Suspended);
        _tray?.Notify(suspended > 0
            ? $"VPN недоступен. Временно напрямую: {Text.Plural(suspended, "приложение", "приложения", "приложений")}."
            : "VPN снова доступен — приложения вернулись в него.");
    }

    // В трее «Тумблер» только следит за VPN, а в сжатом exe весь .NET лежит в памяти процесса.
    // Раз в минуту возвращаем системе страницы, которые сейчас не нужны.
    void TrimMemoryWhileHidden()
    {
        if (IsVisible)
        {
            _hiddenScans = 0;
            return;
        }
        if (_hiddenScans++ % 12 == 1)
            Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), -1, -1);
    }

    /// <summary>Ищет работающие SOCKS5-прокси на этом компьютере и обновляет подсказки портов.</summary>
    async Task FindProxiesAsync()
    {
        if (_searchingProxies) return;
        _searchingProxies = true;
        _lastProxySearch = DateTime.UtcNow;
        try
        {
            string host = _config.Host;
            var (found, direct) = await Task.Run(() => (Scanner.FindProxies(host), Karing.DirectPort()));
            _found = found.Where(p => p.Port != direct).ToList();

            var presets = Karing.Presets();
            presets.AddRange(_found.Where(f => presets.All(p => p.Port != f.Port)));
            _presets = presets.Take(4).ToList();
            Presets.ItemsSource = _presets;

            if (_firstRun && SuggestedProxy() is { } suggested && suggested.Port != _port)
            {
                PortBox.Text = suggested.Port.ToString();
                CommitPort();
            }
            RefreshChrome();
        }
        finally
        {
            _searchingProxies = false;
        }
    }

    /// <summary>Лучший кандидат среди найденных прокси: сначала известные порты Karing, потом остальные.</summary>
    PortPreset? SuggestedProxy() => _presets.FirstOrDefault(p => _found.Any(f => f.Port == p.Port));

    // Если exe перенесли в другую папку, задача автозапуска должна указывать на новое место.
    void RefreshAutostartPath()
    {
        if (_sandbox || !Elevation.IsAdmin) return;
        Task.Run(() =>
        {
            if (Autostart.IsEnabled()) Autostart.Set(true);
        });
    }

    void RowPort_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.DataContext is not AppRow row) return;

        void Choose(int port)
        {
            if (row.PortOverride == port) return;
            row.PortOverride = port;
            MarkDirty();
        }

        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(Item($"По умолчанию  ·  {_port}", () => Choose(0), isChecked: row.PortOverride == 0));

        var ports = _presets.Select(p => p.Port)
            .Concat(_settings.Profiles.SelectMany(p => p.Apps.Values))
            .Concat(_rows.Select(r => r.PortOverride))
            .Where(p => p > 0).Distinct().Order();
        foreach (int port in ports)
        {
            var label = _presets.FirstOrDefault(p => p.Port == port)?.Label ?? port.ToString();
            menu.Items.Add(Item(label, () => Choose(port), isChecked: row.PortOverride == port));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Другой порт…", () =>
        {
            var text = PromptWindow.Ask(this, $"Порт SOCKS5 для «{row.Title}».", PortOf(row).ToString(),
                value => int.TryParse(value, out int p) && p is > 0 and < 65536 ? null : "Порт должен быть числом от 1 до 65535.");
            if (text is not null) Choose(int.Parse(text));
        }));
        menu.IsOpen = true;
    }

    // ---------- Применение ----------

    /// <param name="applyNow">Применить немедленно, даже если «Применять сразу» выключено (пауза с клавиши, смена профиля).</param>
    void MarkDirty(bool applyNow = false)
    {
        if (_loading) return;
        SaveProfile();
        _settings.Save();
        UpdateRouting();

        _dirty = true;
        _applyTimer.Stop();
        if (_canApply && applyNow) _ = ApplyAsync();
        else if (_canApply && _settings.AutoApply) _applyTimer.Start();
        RefreshChrome();
    }

    async Task ApplyAsync()
    {
        if (!_canApply) return;
        if (_applying)
        {
            _applyAgain = true;
            return;
        }

        _applying = true;
        RefreshChrome();
        try
        {
            do
            {
                _applyAgain = false;

                var rules = DesiredRules();
                _config.DefaultPort = _port;
                _config.Udp = UdpBox.IsChecked == true;
                _config.SetRules(rules);
                if (_last is { } scan)
                    foreach (var owner in scan.PortOwners.Values)
                        if (!_config.Excludes.Any(e => owner.Contains(e, StringComparison.OrdinalIgnoreCase)))
                            _config.Excludes.Add(owner);

                foreach (var row in _rows.Where(r => r.IsProxied && r.Path is not null))
                    _settings.Known[row.MatchName.ToLowerInvariant()] = new KnownApp(row.Path!, row.Title);
                _settings.Save();

                bool run = rules.Count > 0;
                // Автозапуск службы отражает намерение пользователя, а не временное падение VPN.
                bool autoStart = !Paused && _rows.Any(r => r.IsProxied);
                await Task.Run(() =>
                {
                    bool changed = _config.Save();
                    if (_sandbox) return;

                    ServiceCtl.SetAutoStart(autoStart);
                    if (!run) ServiceCtl.Stop();
                    else if (changed || ServiceCtl.Query() != ServiceState.Running) ServiceCtl.Restart();
                });
            } while (_applyAgain);

            _dirty = false;
            _applyError = null;
            _appliedAt = DateTime.Now;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
                                       or InvalidOperationException or TimeoutException)
        {
            _applyError = ex.Message;
        }
        finally
        {
            _applying = false;
        }

        await ScanAsync();
        RefreshChrome();
    }

    bool TryGetPort(out int port) =>
        int.TryParse(PortBox.Text.Trim(), out port) && port is > 0 and < 65536;

    // ---------- Состояние и предупреждения ----------

    void RefreshChrome()
    {
        bool portValid = TryGetPort(out int typedPort);
        var scan = _last;
        int proxied = _rows.Count(r => r.IsProxied);
        int suspended = _rows.Count(r => r.Suspended);
        var deadPorts = scan is null || Paused
            ? new List<int>()
            : PortsInUse().Where(p => scan.PortAlive.GetValueOrDefault(p, true) == false).Order().ToList();

        MasterTitle.Text = Paused ? "Проксирование на паузе"
            : proxied == 0 ? "Выбери приложения ниже"
            : suspended == proxied ? "VPN недоступен — всё идёт напрямую"
            : "Проксирование включено";

        PortBox.BorderBrush = Chrome.Brush(portValid ? "Line" : "Danger");
        PortDot.Fill = Chrome.Brush(!portValid || scan is null || !scan.PortAlive.TryGetValue(typedPort, out bool typedAlive) ? "Muted"
            : typedAlive ? "Accent" : "Danger");

        bool serviceOk = scan is not null && (scan.Service == ServiceState.Running || Paused || proxied == 0 || suspended == proxied);
        if (scan is not null)
        {
            var parts = new List<string>
            {
                scan.Service switch
                {
                    ServiceState.Running => "служба работает",
                    ServiceState.Starting => "служба запускается",
                    ServiceState.Stopping => "служба останавливается",
                    ServiceState.Stopped => "служба остановлена",
                    _ => "служба не установлена",
                },
                deadPorts.Count == 0 ? (PortsInUse().Count() > 1 ? "порты отвечают" : "порт отвечает")
                    : $"не отвечает порт {string.Join(", ", deadPorts)}",
            };
            if (scan is { Service: ServiceState.Running, Karing: { } karing })
            {
                var through = "через прокси " + Text.Plural(karing.Connections, "соединение", "соединения", "соединений");
                parts.Add(karing.Outbound is null ? through : $"{through} → {karing.Outbound}");
            }
            StatusText.Text = string.Join("  ·  ", parts);
        }

        var state = Paused || proxied == 0 ? TrayState.Paused
            : !serviceOk || (deadPorts.Count > 0 && !_settings.FallbackDirect) ? TrayState.Problem
            : suspended > 0 || deadPorts.Count > 0 ? TrayState.Warning
            : TrayState.Active;
        StatusDot.Fill = Chrome.Brush(state switch
        {
            TrayState.Active => "Accent",
            TrayState.Warning => "Warn",
            TrayState.Problem => "Danger",
            _ => "Muted",
        });

        FooterText.Text = _applying ? "Применяю…"
            : !_canApply && _dirty ? "Нет прав администратора — изменения не применены"
            : !portValid ? "Порт должен быть числом от 1 до 65535"
            : typedPort != _port ? "Нажми Enter, чтобы сменить порт"
            : _dirty && !_settings.AutoApply ? "Есть неприменённые изменения"
            : _appliedAt is { } at ? $"Применено в {at:HH:mm:ss}"
            : "";
        ApplyButton.Visibility = _settings.AutoApply ? Visibility.Collapsed : Visibility.Visible;
        ApplyButton.IsEnabled = _canApply && _dirty && !_applying;

        var banners = BuildBanners(scan, deadPorts, suspended);
        if (!banners.SequenceEqual(_banners))
        {
            _banners.Clear();
            foreach (var banner in banners) _banners.Add(banner);
        }

        var tooltip = Paused ? "Тумблер — на паузе"
            : $"Тумблер · «{_settings.ActiveProfile}» · {Text.Plural(proxied - suspended, "приложение", "приложения", "приложений")} через VPN"
              + (state is TrayState.Problem or TrayState.Warning ? " · VPN недоступен" : "");
        _tray?.Update(state, tooltip, !Paused, _settings.Profiles.Select(p => p.Name).ToList(), _settings.ActiveProfile);
    }

    List<Banner> BuildBanners(ScanResult? scan, List<int> deadPorts, int suspended)
    {
        var list = new List<Banner>();
        if (!_canApply)
            list.Add(new("Тумблер запущен без прав администратора, поэтому менять настройки не может.", true, "Запустить с правами", "elevate"));
        if (_applyError is not null)
            list.Add(new($"Не удалось применить: {_applyError}", true));
        if (scan is null) return list;

        if (scan.Service == ServiceState.NotInstalled)
            list.Add(new("Служба ProxiFyre не установлена — без неё трафик приложений не перехватывается.", true, "Установить", "setup"));

        if (deadPorts.Count > 0)
        {
            var ports = string.Join(", ", deadPorts.Select(p => $"{_config.Host}:{p}"));
            list.Add(_settings.FallbackDirect
                ? new(suspended > 0
                    ? $"VPN на порту {ports} недоступен. Временно напрямую: {Text.Plural(suspended, "приложение", "приложения", "приложений")}. Возврат в VPN — автоматически, когда порт оживёт."
                    : $"VPN на порту {ports} не отвечает. Если не оживёт через пару секунд, приложения на нём пойдут напрямую.", false)
                : new($"Порт {ports} не отвечает. Пока VPN-клиент не запущен, приложения на этом порту остаются без сети.", true, "Как настроить", "help"));
        }

        if (scan.PortAlive.GetValueOrDefault(_port, true) == false && SuggestedProxy() is { } suggested && suggested.Port != _port)
            list.Add(new($"Порт по умолчанию {_port} не отвечает, зато на этом компьютере найден работающий прокси: {suggested.Label}.", false,
                $"Использовать {suggested.Port}", $"port:{suggested.Port}"));

        else if (deadPorts.Count == 0 && !Paused && scan.PortAlive.GetValueOrDefault(_port, true) == false)
            list.Add(new($"На порту {_port} прокси не отвечает. Запусти VPN-клиент — «Тумблер» найдёт его порт сам.", false, "Как настроить", "help"));

        if (scan.Tunnels.Count > 0)
            list.Add(new($"Включён TUN ({string.Join(", ", scan.Tunnels)}): сейчас в VPN идёт весь трафик компьютера, и тумблеры ничего не меняют. " +
                         "Выключи TUN в VPN-клиенте, чтобы через VPN шли только выбранные приложения.", false));
        return list;
    }

    // ---------- Трей, горячая клавиша, окно ----------

    void StartTrayAndHotkey()
    {
        // В режиме снимка для отладки значок и глобальная клавиша не нужны.
        if (_options.Screenshot is not null) return;

        _tray = new Tray();
        _tray.OpenRequested += ShowFromTray;
        _tray.PauseToggled += TogglePause;
        _tray.ProfileSelected += SwitchProfile;
        _tray.ExitRequested += Exit;

        _hotkey = new Hotkey(new WindowInteropHelper(this).Handle);
        _hotkey.Pressed += () =>
        {
            TogglePause();
            _tray?.Notify(Paused ? "Проксирование на паузе — всё идёт напрямую." : "Проксирование включено.");
        };
        RegisterHotkey(_settings.Hotkey);
    }

    bool RegisterHotkey(string combo)
    {
        if (_hotkey is null) return false;
        if (combo.Length > 0) return _hotkey.Register(combo);
        _hotkey.Unregister();
        return true;
    }

    void TogglePause()
    {
        _settings.Enabled = !_settings.Enabled;
        MasterSwitch.IsChecked = _settings.Enabled;
        MarkDirty(applyNow: true);
    }

    void Exit()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _tray is not null && _settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_settings.TrayHintShown)
            {
                _settings.TrayHintShown = true;
                _settings.Save();
                _tray.Notify("Тумблер остался в трее и следит за VPN. Выйти можно через меню значка.");
            }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _scanTimer.Stop();
        _applyTimer.Stop();
        _hotkey?.Dispose();
        _tray?.Dispose();
        base.OnClosed(e);
    }

    // ---------- Обработчики ----------

    void Master_Click(object sender, RoutedEventArgs e)
    {
        _settings.Enabled = MasterSwitch.IsChecked == true;
        MarkDirty();
    }

    void Port_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) RefreshChrome();
    }

    void Port_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitPort();
    }

    void Port_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitPort();

    void Preset_Click(object sender, RoutedEventArgs e)
    {
        PortBox.Text = ((Button)sender).Tag.ToString();
        CommitPort();
    }

    // Порт меняем только по Enter или уходу из поля: недопечатанный «30» вместо «3066» оставил бы приложения без сети.
    void CommitPort()
    {
        if (!TryGetPort(out int port) || port == _port) return;
        _port = port;
        _settings.DefaultPort = port;
        MarkDirty();
    }

    void Udp_Click(object sender, RoutedEventArgs e) => MarkDirty();

    void AutoApply_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoApply = AutoApplyBox.IsChecked == true;
        if (_dirty) MarkDirty();
        else
        {
            _settings.Save();
            RefreshChrome();
        }
    }

    async void Apply_Click(object sender, RoutedEventArgs e) => await ApplyAsync();

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_loading) UpdateVisibility();
    }

    void ShowAll_Click(object sender, RoutedEventArgs e) => UpdateVisibility();

    void AddExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Программы (*.exe)|*.exe", Title = "Какое приложение пустить через VPN" };
        if (dialog.ShowDialog(this) != true) return;

        var path = dialog.FileName;
        var name = Path.GetFileNameWithoutExtension(path);
        if (IsProtected(name, _last))
        {
            MessageBox.Show(this, "Это приложение проксировать нельзя: оно само обслуживает VPN.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var row = AddRow(name);
        if (row.Path is null)
        {
            row.Path = path;
            row.Icon = IconCache.Get(path);
            row.Title = Scanner.TitleOf(path) ?? name;
        }
        row.IsProxied = true;
        AppList.ScrollIntoView(row);
    }

    async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppRow { Path: { } path } row) return;

        var answer = MessageBox.Show(this,
            $"Перезапустить «{row.Title}»?\n\nВсе его окна закроются, несохранённые данные в нём пропадут.",
            Title, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        await Task.Run(() =>
        {
            foreach (var process in Process.GetProcessesByName(row.MatchName))
            {
                using (process)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                    {
                        // Процесс уже завершился сам или защищён — идём дальше.
                    }
                }
            }
            Thread.Sleep(500);
            OpenViaShell(path);
        });
        await ScanAsync();
    }

    void Help_Click(object sender, RoutedEventArgs e) => ShowHelp();

    /// <summary>Открывает справку рядом с главным окном; повторный вызов поднимает уже открытую.</summary>
    void ShowHelp(int? topic = null)
    {
        if (_help is not null)
        {
            _help.Activate();
            return;
        }
        _help = new HelpWindow(HelpState, UsePort, topic) { Owner = this };
        _help.Closed += (_, _) => _help = null;
        _help.Show();
    }

    // Для портов Karing показываем понятные подписи («всё через VPN»), а не имя процесса.
    (IReadOnlyList<PortPreset> Proxies, int CurrentPort) HelpState() =>
        (_found.Select(f => _presets.FirstOrDefault(p => p.Port == f.Port) ?? f).ToList(), _port);

    void UsePort(int port)
    {
        PortBox.Text = port.ToString();
        CommitPort();
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        bool canChangeSystem = _canApply && !_sandbox;
        var dialog = new SettingsWindow(_settings, canChangeSystem, RegisterHotkey, OnSettingsChanged, canChangeSystem ? UninstallComponents : null) { Owner = this };
        dialog.ShowDialog();
    }

    /// <summary>Убирает с компьютера всё, что поставил мастер установки, и закрывает «Тумблер».</summary>
    async void UninstallComponents(Window owner)
    {
        var answer = MessageBox.Show(owner,
            "Удалить службу ProxiFyre, её файлы, правило брандмауэра и автозапуск «Тумблера»?\n\n" +
            "Выбранные приложения снова пойдут напрямую. Профили и настройки «Тумблера» сохранятся.",
            Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        bool removeDriver = MessageBox.Show(owner,
            "Удалить и драйвер Windows Packet Filter?\n\nЕсли им пользуются другие программы (например, WireSock), выбери «Нет».",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        _scanTimer.Stop();
        _applyTimer.Stop();
        owner.IsEnabled = false;
        try
        {
            await Task.Run(() => Setup.Uninstall(removeDriver, _ => { }));
            MessageBox.Show(owner, "Компоненты удалены. «Тумблер» сейчас закроется; при следующем запуске он предложит установить их заново.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(owner, $"Удалить получилось не всё: {ex.Message}\n\nПодробности — в журнале {Setup.LogFile}",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _exiting = true;
        Application.Current.Shutdown();
    }

    /// <summary>Открывает мастер установки поверх окна; true — после него всё на месте.</summary>
    bool RunSetup(Window owner)
    {
        bool installed = SetupWindow.Run(owner);
        _ = ScanAsync();
        return installed;
    }

    void OnSettingsChanged()
    {
        _settings.Save();
        UpdateRouting();
        // Смена поведения при упавшем VPN может сразу поменять то, что должно быть в конфиге службы.
        if (_firstScanDone && !InSync()) MarkDirty(applyNow: true);
        else RefreshChrome();
    }

    void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DiagnosticsWindow(DiagnosticsSnapshot, OpenLog, RunSetup) { Owner = this };
        dialog.ShowDialog();
    }

    DiagnosticsInput DiagnosticsSnapshot() => new(
        _installDir,
        _config.Host,
        _rows.Where(r => r.IsProxied).GroupBy(PortOf).ToDictionary(g => g.Key, g => g.Count()),
        Elevation.IsAdmin,
        Paused,
        InSync());

    void OpenLog()
    {
        var log = Path.Combine(_installDir, "logs", "logfile.txt");
        OpenViaShell(File.Exists(log) ? log : _installDir);
    }

    void BannerAction_Click(object sender, RoutedEventArgs e)
    {
        switch (((Button)sender).Tag as string)
        {
            case "elevate":
                if (Elevation.RelaunchElevated([])) Exit();
                break;
            case "setup":
                RunSetup(this);
                break;
            case "help":
                ShowHelp();
                break;
            case { } action when action.StartsWith("port:"):
                UsePort(int.Parse(action["port:".Length..]));
                break;
        }
    }

    // «Тумблер» работает с правами администратора; через explorer приложение стартует с обычными правами.
    static void OpenViaShell(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\""))?.Dispose();

    // ---------- Отладка (ключи --profile, --toggle, --route, --fallback, --dialog, --diag, --selftest, --screenshot) ----------

    static void MoveOffscreen(Window window)
    {
        // Снимок для отладки делаем за пределами экрана, не забирая фокус.
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = window.Top = -30000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
    }

    async Task RunDevScriptAsync()
    {
        if (_options.Profile is { } profile)
        {
            if (_settings.Profiles.Any(p => p.Name == profile)) SwitchProfile(profile);
            else CreateProfile(profile);
        }
        foreach (var name in _options.Toggle)
            if (_byName.TryGetValue(name, out var row))
                row.IsProxied = !row.IsProxied;
        foreach (var (name, port) in _options.Routes)
            if (_byName.TryGetValue(name, out var row))
            {
                row.PortOverride = port;
                MarkDirty();
            }
        if (_options.Fallback is { } fallback)
        {
            _settings.FallbackDirect = fallback;
            OnSettingsChanged();
        }

        if (_options.DiagnosticsReport is { } reportFile)
        {
            var input = DiagnosticsSnapshot();
            var results = await Task.Run(() => Diagnostics.Run(input));
            File.WriteAllText(reportFile, DiagnosticsWindow.Report(results));
        }

        if (_options.SelfTest is { } selfTestFile)
            File.WriteAllLines(selfTestFile, await Task.Run(SelfTest));

        if (_options.Screenshot is null)
        {
            // Самопроверка и отчёт без снимка — это пакетный запуск: окно больше не нужно.
            if (_options.SelfTest is not null || _options.DiagnosticsReport is not null)
            {
                _exiting = true;
                Application.Current.Shutdown();
            }
            return;
        }

        await Task.Delay(1500);
        while (_applying || _applyTimer.IsEnabled) await Task.Delay(100);
        // Ждём два круга сканирования, чтобы сработала защита от единичных сбоев порта.
        await Task.Delay(_options.Fallback == true ? 7000 : 400);

        Window target = this;
        if (_options.Dialog is { } dialogName)
        {
            target = dialogName == "settings"
                ? new SettingsWindow(_settings, false, RegisterHotkey, OnSettingsChanged, null)
                : dialogName.StartsWith("help")
                    ? new HelpWindow(HelpState, UsePort, int.TryParse(dialogName.Split(':').Last(), out int topic) ? topic : null)
                : dialogName == "setup"
                    ? new SetupWindow(
                    [
                        new($"Драйвер Windows Packet Filter {Setup.DriverVersion}", "Перехватывает сетевые пакеты выбранных приложений. Официальный установщик с цифровой подписью разработчика.", false),
                        new("Microsoft Visual C++ Runtime", "Нужна для ProxiFyre. Будет скачана с сайта Microsoft (около 25 МБ) — потребуется интернет.", false),
                        new($"ProxiFyre {Setup.ProxiFyreVersion}", $"Служба Windows, которая отправляет перехваченные пакеты в твой прокси. Ставится в {Setup.InstallDir}.", false),
                        new("Правило брандмауэра Windows", "Разрешает входящие соединения для ProxiFyre.exe — без него перехваченные соединения блокируются.", true),
                    ])
                    : new DiagnosticsWindow(DiagnosticsSnapshot, OpenLog, null);
            MoveOffscreen(target);
            target.Show();
            await Task.Delay(dialogName == "diagnostics" ? 9000 : 600);
        }

        SaveScreenshot(target, _options.Screenshot);
        _exiting = true;
        Application.Current.Shutdown();
    }

    /// <summary>Проверяет то, что нельзя увидеть на снимке: встроенные установщики, проверку подписей, поиск прокси.</summary>
    static List<string> SelfTest()
    {
        var lines = new List<string>();
        var dir = Directory.CreateTempSubdirectory("tumbler-selftest-").FullName;
        try
        {
            var (msi, zip) = Setup.ExtractPayload(dir);
            lines.Add($"payload driver: {new FileInfo(msi).Length} bytes, checksum ok");
            using (var archive = ZipFile.OpenRead(zip))
                lines.Add($"payload proxifyre: {archive.Entries.Count} files, checksum ok, has ProxiFyre.exe: {archive.GetEntry("ProxiFyre.exe") is not null}");

            lines.Add($"driver msi signed by The Anti-Cloud Corporation: {Trust.IsSignedBy(msi, "The Anti-Cloud Corporation")}");
            lines.Add($"driver msi signed by Microsoft Corporation (must be False): {Trust.IsSignedBy(msi, "Microsoft Corporation")}");
            lines.Add($"unsigned zip passes signature check (must be False): {Trust.IsSignedBy(zip, "Microsoft Corporation")}");
            var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (File.Exists(dotnet))
                lines.Add($"dotnet.exe signed by Microsoft Corporation: {Trust.IsSignedBy(dotnet, "Microsoft Corporation")}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        lines.Add($"setup complete: {Setup.IsComplete}");
        foreach (var item in Setup.Check())
            lines.Add($"  [{(item.Installed ? "x" : " ")}] {item.Title}");
        foreach (var proxy in Scanner.FindProxies("127.0.0.1"))
            lines.Add($"proxy found: {proxy.Label}");
        lines.Add($"karing direct port: {Karing.DirectPort()}");
        return lines;
    }

    static void SaveScreenshot(Window window, string file)
    {
        var root = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var bounds = new Rect(0, 0, root.ActualWidth + root.Margin.Left + root.Margin.Right, root.ActualHeight + root.Margin.Top + root.Margin.Bottom);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, bounds);
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(root.Margin.Left, root.Margin.Top, root.ActualWidth, root.ActualHeight));
        }

        var bitmap = new RenderTargetBitmap((int)(bounds.Width * dpi.DpiScaleX), (int)(bounds.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }
}
