using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Tumbler.Daemon;

namespace Tumbler.Gui;

/// <param name="ActionId">Что делает кнопка: setup, help или port:НОМЕР.</param>
public sealed record Banner(string Text, bool IsError, string? ActionLabel = null, string? ActionId = null)
{
    public bool HasAction => ActionLabel is not null;
}

/// <summary>
/// Главное окно. Само ничего не перехватывает: собирает из профиля нужное состояние и отправляет его службе,
/// а от службы получает, что реально работает. Применение мгновенное — служба ничего не перезапускает.
/// </summary>
public partial class MainWindow : Window
{
    const string DefaultProfileName = "Основной";

    readonly IBackend _backend;
    readonly GuiOptions _options;
    readonly Settings _settings;
    readonly List<AppRow> _rows = new();
    readonly Dictionary<string, AppRow> _byName = new(StringComparer.OrdinalIgnoreCase);
    readonly ObservableCollection<object> _items = new();
    readonly ObservableCollection<Banner> _banners = new();
    readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    readonly DispatcherTimer _applyTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly bool _firstRun;
    bool _autoPickPort;

    List<PortPreset> _found = new();
    Snapshot? _snapshot;
    DaemonStatus? _status;
    bool _serviceDown, _loading = true, _syncing, _scanning, _applying, _applyAgain, _exiting, _searching;
    string _shape = "";
    int _port;
    string? _applyError;
    DateTime _lastProxySearch;
    TrayIcon? _tray;
    string _trayMenuKey = "";
    HelpWindow? _help;

    public MainWindow() : this(new DemoBackend(), new GuiOptions())
    {
    }

    internal MainWindow(IBackend backend, GuiOptions options)
    {
        InitializeComponent();
        _backend = backend;
        _options = options;
        Icon = App.LoadIcon();

        // Настройки и профили — общие с Windows-версией; в демо-режиме начинаем с чистого листа.
        var settingsPath = backend.IsDemo ? Path.Combine(Path.GetTempPath(), "tumbler-demo-settings.json") : null;
        if (settingsPath is not null && File.Exists(settingsPath)) File.Delete(settingsPath);
        var realPath = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tumbler", "settings.json");
        _firstRun = !File.Exists(realPath);
        _settings = Settings.Load(settingsPath);
        // На Linux значок в трее есть не везде (в GNOME — только с расширением), поэтому крестик по умолчанию закрывает окно.
        // Окно и не нужно держать открытым: всю работу делает служба.
        if (_firstRun) _settings.CloseToTray = false;

        AppList.ItemsSource = _items;
        Banners.ItemsSource = _banners;

        _scanTimer.Tick += async (_, _) => await RefreshAsync();
        _applyTimer.Tick += async (_, _) =>
        {
            _applyTimer.Stop();
            await ApplyAsync();
        };

        if (options.Screenshot is not null) Shots.MoveOffscreen(this);
        Opened += async (_, _) => await StartAsync();
        Closing += OnClosing;
    }

    async Task StartAsync()
    {
        var response = await _backend.CallAsync(new Request());
        _serviceDown = response is null;
        _status = response?.Status;
        var state = _status?.State ?? new DaemonState();

        _port = _settings.DefaultPort > 0 ? _settings.DefaultPort
            : state.Rules.Count > 0 ? state.Rules.Values.GroupBy(p => p).MaxBy(g => g.Count())!.Key
            : 1080;
        EnsureProfiles(state);
        // Свежая установка: ни настроек окна, ни правил у службы — порт ещё никто не выбирал.
        _autoPickPort = _firstRun && state.Rules.Count == 0 && _settings.DefaultPort == 0;

        _syncing = true;
        PortBox.Text = _port.ToString();
        MasterSwitch.IsChecked = state.Enabled;
        UdpBox.IsChecked = state.Udp;
        _syncing = false;

        LoadProfile(ActiveProfile);
        _loading = false;

        StartTray();
        await RefreshAsync();
        _scanTimer.Start();
        _ = FindProxiesAsync();

        // Профиль — источник правды: если служба живёт по другому списку (например, после переустановки), приводим её в порядок.
        if (_status is not null && !SameRules(_status.State.Rules, DesiredRules())) await ApplyAsync();

        if (_firstRun && !_backend.IsDemo && !_settings.HelpShown)
        {
            _settings.HelpShown = true;
            _settings.Save();
            ShowHelp(topic: 0);
        }
        await RunDevScriptAsync();
    }

    // ---------- Профили ----------

    Profile ActiveProfile => _settings.Profiles.First(p => p.Name == _settings.ActiveProfile);

    void EnsureProfiles(DaemonState state)
    {
        if (_settings.Profiles.Count == 0)
        {
            var profile = new Profile { Name = DefaultProfileName };
            foreach (var (app, port) in state.Rules)
                profile.Apps[app] = port == _port ? 0 : port;
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
        UpdateList();
    }

    void SaveProfile() => ActiveProfile.Apps = _rows.Where(r => r.IsProxied).ToDictionary(r => r.MatchName, r => r.PortOverride);

    void SwitchProfile(string name)
    {
        if (name == _settings.ActiveProfile || _settings.Profiles.All(p => p.Name != name)) return;
        _settings.ActiveProfile = name;
        _loading = true;
        LoadProfile(ActiveProfile);
        _loading = false;
        MarkDirty();
    }

    void CreateProfile(string name)
    {
        _settings.Profiles.Add(new Profile { Name = name, Apps = new(ActiveProfile.Apps) });
        _settings.ActiveProfile = name;
        ProfileName.Text = name;
        _settings.Save();
        RefreshChrome();
    }

    string? ValidateProfileName(string name, string? current = null) =>
        name.Length == 0 ? "Введи название."
        : name.Length > 30 ? "Слишком длинное название."
        : name != current && _settings.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? "Профиль с таким названием уже есть."
        : null;

    void Profile_Click(object? sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        foreach (var profile in _settings.Profiles)
        {
            var name = profile.Name;
            menu.Items.Add(Item($"{name}  ·  {profile.Apps.Count}", () => SwitchProfile(name), isChecked: name == _settings.ActiveProfile));
        }
        menu.Items.Add(new Separator());

        var active = _settings.ActiveProfile;
        menu.Items.Add(Item("Новый профиль…", async () =>
        {
            var name = await Dialogs.Ask(this, "Название нового профиля. В него скопируются текущие тумблеры — дальше меняй как нужно.", "", n => ValidateProfileName(n));
            if (name is not null) CreateProfile(name);
        }));
        menu.Items.Add(Item($"Переименовать «{active}»…", async () =>
        {
            var name = await Dialogs.Ask(this, "Новое название профиля.", active, n => ValidateProfileName(n, active));
            if (name is null) return;
            ActiveProfile.Name = name;
            _settings.ActiveProfile = name;
            ProfileName.Text = name;
            _settings.Save();
            RefreshChrome();
        }));
        var delete = Item($"Удалить «{active}»", async () =>
        {
            if (!await Dialogs.Confirm(this, $"Удалить профиль «{active}»?", "Удалить")) return;
            _settings.Profiles.Remove(ActiveProfile);
            SwitchProfile(_settings.Profiles[0].Name);
        });
        delete.IsEnabled = _settings.Profiles.Count > 1;
        menu.Items.Add(delete);
        menu.ShowAt(ProfileButton);
    }

    static MenuItem Item(string header, Action click, bool isChecked = false)
    {
        var item = new MenuItem { Header = header.Replace("_", "__") };
        if (isChecked)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = true;
        }
        item.Click += (_, _) => click();
        return item;
    }

    // ---------- Список приложений ----------

    AppRow AddRow(string name)
    {
        if (_byName.TryGetValue(name, out var existing)) return existing;

        var row = new AppRow(name) { EffectivePort = _port };
        if (_settings.Known.TryGetValue(name.ToLowerInvariant(), out var known))
        {
            row.Title = known.Title;
            if (File.Exists(known.Path)) row.Path = known.Path;
        }
        row.Toggled += _ =>
        {
            MarkDirty();
            // Строка переезжает в другой раздел; перестраиваем список, когда тумблер закончит обработку клика.
            Dispatcher.UIThread.Post(UpdateList);
        };
        _rows.Add(row);
        _byName[name] = row;
        return row;
    }

    async Task RefreshAsync()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            var ports = PortsInUse().Append(_port).ToHashSet();
            if (TryGetPort(out int typed)) ports.Add(typed);

            _snapshot = await Task.Run(() => _backend.Scan(ports));
            var response = await _backend.CallAsync(new Request());
            _serviceDown = response is null;
            if (response?.Status is { } status) SyncFromStatus(status);

            MergeRows(_snapshot);
            UpdateRouting();
            RefreshChrome();

            if (_snapshot.PortAlive.GetValueOrDefault(_port, true) == false && DateTime.UtcNow - _lastProxySearch > TimeSpan.FromSeconds(15))
                _ = FindProxiesAsync();
        }
        finally
        {
            _scanning = false;
        }
    }

    /// <summary>Пауза могла смениться из терминала («tumbler toggle»), поэтому выключатели берём у службы.</summary>
    void SyncFromStatus(DaemonStatus status)
    {
        _status = status;
        if (_applying || _applyTimer.IsEnabled) return;
        _syncing = true;
        MasterSwitch.IsChecked = status.State.Enabled;
        UdpBox.IsChecked = status.State.Udp;
        _syncing = false;
    }

    void MergeRows(Snapshot snapshot)
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in snapshot.Groups)
        {
            if (IsProtected(group.Name, snapshot)) continue;
            running.Add(group.Name);
            AddRow(group.Name).Update(group);
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
        UpdateList();
    }

    /// <summary>Сам VPN-клиент, «Тумблер» и его туннели проксировать нельзя: получится петля.</summary>
    static bool IsProtected(string name, Snapshot? snapshot) =>
        name is "tumbler" or "hev-socks5-tunnel"
        || snapshot?.PortOwners.Values.Contains(name, StringComparer.OrdinalIgnoreCase) == true;

    void UpdateList()
    {
        var search = SearchBox.Text?.Trim() ?? "";
        bool showAll = ShowAllBox.IsChecked == true;
        foreach (var row in _rows)
        {
            // Поиск идёт по всем процессам: у многих нужных программ нет ярлыка в меню.
            bool inScope = showAll || search.Length > 0 || row.IsProxied || !row.IsRunning || (row.SectionOrder == 1 && row.Mine);
            bool matches = search.Length == 0
                || row.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.MatchName.Contains(search, StringComparison.OrdinalIgnoreCase);
            row.Visible = inScope && matches;
        }

        var visible = _rows.Where(r => r.Visible)
            .OrderBy(r => r.SectionOrder)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        EmptyHint.IsVisible = visible.Count == 0;

        // Перестраиваем список, только когда изменились разделы или порядок: перестройка пересоздаёт строки.
        var shape = string.Join('|', visible.Select(r => $"{r.MatchName}:{r.SectionOrder}:{r.Title}"));
        if (shape == _shape) return;
        _shape = shape;

        _items.Clear();
        foreach (var section in visible.GroupBy(r => r.SectionOrder))
        {
            _items.Add(new SectionHeader(section.Key switch
            {
                0 => "ИДУТ ЧЕРЕЗ VPN",
                1 => "ПРИЛОЖЕНИЯ",
                _ => "ФОНОВЫЕ ПРОЦЕССЫ",
            }, section.Count()));
            foreach (var row in section) _items.Add(row);
        }
    }

    // ---------- Порты и состояние службы ----------

    int PortOf(AppRow row) => row.PortOverride > 0 ? row.PortOverride : _port;

    IEnumerable<int> PortsInUse() => _rows.Where(r => r.IsProxied).Select(PortOf).Distinct();

    Dictionary<string, int> DesiredRules() =>
        _rows.Where(r => r.IsProxied).ToDictionary(r => r.MatchName, PortOf, StringComparer.OrdinalIgnoreCase);

    static bool SameRules(Dictionary<string, int> a, Dictionary<string, int> b)
    {
        var left = new Dictionary<string, int>(a, StringComparer.OrdinalIgnoreCase);
        return left.Count == b.Count && b.All(r => left.TryGetValue(r.Key, out int port) && port == r.Value);
    }

    DaemonState DesiredState() => new()
    {
        Enabled = MasterSwitch.IsChecked == true,
        Udp = UdpBox.IsChecked == true,
        FallbackDirect = _status?.State.FallbackDirect ?? false,
        Rules = DesiredRules(),
    };

    void UpdateRouting()
    {
        var tunnels = _status?.Tunnels.ToDictionary(t => t.Port) ?? new();
        var captured = new Dictionary<string, int>(_status?.Captured ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            int port = PortOf(row);
            row.EffectivePort = port;
            row.PortDown = row.IsProxied && _snapshot?.PortAlive.GetValueOrDefault(port, true) == false;
            row.Suspended = row.IsProxied && tunnels.TryGetValue(port, out var tunnel) && tunnel.Suspended;
            row.Captured = captured.GetValueOrDefault(row.MatchName);
        }
    }

    async Task FindProxiesAsync()
    {
        if (_searching) return;
        _searching = true;
        _lastProxySearch = DateTime.UtcNow;
        try
        {
            _found = await Task.Run(_backend.FindProxies);
            Presets.ItemsSource = _found.Take(4).ToList();

            // Свежая установка: берём первый найденный прокси, если выбранный порт не отвечает.
            bool currentAlive = _snapshot?.PortAlive.GetValueOrDefault(_port) == true;
            if (_autoPickPort && !currentAlive && _found.FirstOrDefault() is { } first && first.Port != _port)
            {
                _autoPickPort = false;
                UsePort(first.Port);
            }
            RefreshChrome();
        }
        finally
        {
            _searching = false;
        }
    }

    void RowPort_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AppRow row } button) return;

        void Choose(int port)
        {
            if (row.PortOverride == port) return;
            row.PortOverride = port;
            MarkDirty();
        }

        var menu = new MenuFlyout();
        menu.Items.Add(Item($"По умолчанию  ·  {_port}", () => Choose(0), isChecked: row.PortOverride == 0));
        var ports = _found.Select(p => p.Port)
            .Concat(_settings.Profiles.SelectMany(p => p.Apps.Values))
            .Concat(_rows.Select(r => r.PortOverride))
            .Where(p => p > 0).Distinct().Order();
        foreach (int port in ports)
        {
            var label = _found.FirstOrDefault(p => p.Port == port)?.Label ?? port.ToString();
            menu.Items.Add(Item(label, () => Choose(port), isChecked: row.PortOverride == port));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Другой порт…", async () =>
        {
            var text = await Dialogs.Ask(this, $"Порт SOCKS5 для «{row.Title}».", PortOf(row).ToString(),
                value => int.TryParse(value, out int p) && p is > 0 and < 65536 ? null : "Порт должен быть числом от 1 до 65535.");
            if (text is not null) Choose(int.Parse(text));
        }));
        menu.ShowAt(button);
    }

    // ---------- Применение ----------

    void MarkDirty()
    {
        if (_loading) return;
        SaveProfile();
        _settings.Save();
        UpdateRouting();
        _applyTimer.Stop();
        _applyTimer.Start();
        RefreshChrome();
    }

    async Task ApplyAsync()
    {
        if (_backend.IsDemo == false && _serviceDown && await _backend.CallAsync(new Request()) is null)
        {
            _applyError = "служба «Тумблера» не отвечает";
            RefreshChrome();
            return;
        }
        if (_applying)
        {
            _applyAgain = true;
            return;
        }

        _applying = true;
        try
        {
            do
            {
                _applyAgain = false;
                foreach (var row in _rows.Where(r => r.IsProxied && r.Path is not null))
                    _settings.Known[row.MatchName.ToLowerInvariant()] = new KnownApp(row.Path!, row.Title);
                _settings.Save();

                var response = await _backend.CallAsync(new Request { Command = "apply", State = DesiredState() });
                _serviceDown = response is null;
                _applyError = response is null ? "служба «Тумблера» не отвечает" : response.Ok ? null : response.Error;
                if (response?.Status is { } status) _status = status;
            } while (_applyAgain);
        }
        finally
        {
            _applying = false;
        }
        UpdateRouting();
        RefreshChrome();
    }

    bool TryGetPort(out int port) => int.TryParse(PortBox.Text?.Trim(), out port) && port is > 0 and < 65536;

    // ---------- Шапка, предупреждения, трей ----------

    void RefreshChrome()
    {
        bool portValid = TryGetPort(out int typedPort);
        var snapshot = _snapshot;
        bool paused = MasterSwitch.IsChecked != true;
        int proxied = _rows.Count(r => r.IsProxied);
        int suspended = _rows.Count(r => r.Suspended);
        var deadPorts = snapshot is null || paused
            ? new List<int>()
            : PortsInUse().Where(p => snapshot.PortAlive.GetValueOrDefault(p, true) == false).Order().ToList();

        MasterTitle.Text = _serviceDown ? "Служба не отвечает"
            : paused ? "Проксирование на паузе"
            : proxied == 0 ? "Выбери приложения ниже"
            : suspended == proxied ? "VPN недоступен — всё идёт напрямую"
            : "Проксирование включено";

        PortDot.Fill = Brush(!portValid || snapshot is null || !snapshot.PortAlive.TryGetValue(typedPort, out bool typedAlive) ? "Muted"
            : typedAlive ? "Accent" : "Danger");

        var parts = new List<string>();
        if (_serviceDown) parts.Add("служба не запущена");
        else if (_status is { } status)
        {
            parts.Add(status.RulesLoaded ? "перехват включён" : paused || proxied == 0 ? "перехват не нужен" : "правила не загружены");
            int tunnelsUp = status.Tunnels.Count(t => t.Running);
            if (status.Tunnels.Count > 0) parts.Add($"туннели: {tunnelsUp} из {status.Tunnels.Count}");
            int captured = status.Captured.Values.Sum();
            if (captured > 0) parts.Add("в VPN " + Text.Plural(captured, "процесс", "процесса", "процессов"));
        }
        if (deadPorts.Count > 0) parts.Add($"не отвечает порт {string.Join(", ", deadPorts)}");
        StatusText.Text = parts.Count == 0 ? "Связываюсь со службой…" : string.Join("  ·  ", parts);

        bool problem = _serviceDown || (_status?.Problems.Count ?? 0) > 0 || (deadPorts.Count > 0 && _status?.State.FallbackDirect != true);
        StatusDot.Fill = Brush(paused || proxied == 0 ? "Muted" : problem ? "Danger" : suspended > 0 || deadPorts.Count > 0 ? "Warn" : "Accent");

        FooterText.Text = _applying ? "Применяю…"
            : !portValid ? "Порт должен быть числом от 1 до 65535"
            : typedPort != _port ? "Нажми Enter, чтобы сменить порт"
            : "";

        var banners = BuildBanners(snapshot, deadPorts, suspended);
        if (!banners.SequenceEqual(_banners))
        {
            _banners.Clear();
            foreach (var banner in banners) _banners.Add(banner);
        }
        UpdateTray(paused, proxied - suspended);
    }

    List<Banner> BuildBanners(Snapshot? snapshot, List<int> deadPorts, int suspended)
    {
        var list = new List<Banner>();
        if (_serviceDown)
            list.Add(new("Служба «Тумблера» не отвечает — без неё ничего не перехватывается. Если она ещё не установлена, установи её.", true, "Установить", "setup"));
        if (_applyError is not null && !_serviceDown)
            list.Add(new($"Не удалось применить: {_applyError}", true));
        foreach (var problem in _status?.Problems ?? [])
            list.Add(new(problem, true, "Диагностика", "diagnostics"));
        if (snapshot is null) return list;

        if (deadPorts.Count > 0)
        {
            var ports = string.Join(", ", deadPorts.Select(p => $"127.0.0.1:{p}"));
            list.Add(_status?.State.FallbackDirect == true
                ? new(suspended > 0
                    ? $"VPN на порту {ports} недоступен. Временно напрямую: {Text.Plural(suspended, "приложение", "приложения", "приложений")}. Возврат в VPN — автоматически."
                    : $"VPN на порту {ports} не отвечает. Если не оживёт через пару секунд, приложения на нём пойдут напрямую.", false)
                : new($"Порт {ports} не отвечает. Пока VPN-клиент не запущен, приложения на этом порту остаются без сети.", true, "Как настроить", "help"));
        }

        var suggestion = _found.FirstOrDefault(p => p.Port != _port);
        if (snapshot.PortAlive.GetValueOrDefault(_port, true) == false && suggestion is not null)
            list.Add(new($"Порт по умолчанию {_port} не отвечает, зато на этом компьютере найден работающий прокси: {suggestion.Label}.", false,
                $"Использовать {suggestion.Port}", $"port:{suggestion.Port}"));
        else if (deadPorts.Count == 0 && MasterSwitch.IsChecked == true && snapshot.PortAlive.GetValueOrDefault(_port, true) == false)
            list.Add(new($"На порту {_port} прокси не отвечает. Запусти VPN-клиент — «Тумблер» найдёт его порт сам.", false, "Как настроить", "help"));

        if (snapshot.OtherTunnels.Count > 0)
            list.Add(new($"У VPN-клиента включён режим TUN ({string.Join(", ", snapshot.OtherTunnels)}): в VPN может уходить весь трафик компьютера. " +
                         "Выключи TUN в клиенте, чтобы через VPN шли только выбранные приложения.", false));
        return list;
    }

    static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;

    void StartTray()
    {
        if (_options.Screenshot is not null) return;
        try
        {
            _tray = new TrayIcon { Icon = App.LoadIcon(), ToolTipText = "Тумблер" };
            _tray.Clicked += (_, _) => ShowFromTray();
            TrayIcon.SetIcons(Application.Current!, [_tray]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or IOException)
        {
            // Нет области уведомлений — обойдёмся без значка.
            _tray = null;
        }
    }

    void UpdateTray(bool paused, int throughVpn)
    {
        if (_tray is null) return;
        _tray.ToolTipText = paused ? "Тумблер — на паузе" : $"Тумблер · «{_settings.ActiveProfile}» · {Text.Plural(throughVpn, "приложение", "приложения", "приложений")} через VPN";

        var key = $"{paused}|{_settings.ActiveProfile}|{string.Join(',', _settings.Profiles.Select(p => p.Name))}";
        if (key == _trayMenuKey) return;
        _trayMenuKey = key;

        var menu = new NativeMenu();
        menu.Items.Add(TrayItem("Открыть Тумблер", ShowFromTray));
        menu.Items.Add(TrayItem(paused ? "Снять с паузы" : "Поставить на паузу", () => MasterSwitch.IsChecked = paused));
        if (_settings.Profiles.Count > 1)
        {
            menu.Items.Add(new NativeMenuItemSeparator());
            foreach (var profile in _settings.Profiles)
            {
                var name = profile.Name;
                var item = TrayItem($"Профиль «{name}»", () => SwitchProfile(name));
                item.ToggleType = NativeMenuItemToggleType.CheckBox;
                item.IsChecked = name == _settings.ActiveProfile;
                menu.Items.Add(item);
            }
        }
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(TrayItem("Выход", Exit));
        _tray.Menu = menu;
    }

    static NativeMenuItem TrayItem(string header, Action click)
    {
        var item = new NativeMenuItem(header.Replace("_", "__"));
        item.Click += (_, _) => click();
        return item;
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void Exit()
    {
        _exiting = true;
        Close();
    }

    void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_exiting && _tray is not null && _settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _scanTimer.Stop();
        _applyTimer.Stop();
        _tray?.Dispose();
    }

    // ---------- Обработчики ----------

    void Master_Changed(object? sender, RoutedEventArgs e)
    {
        if (_syncing || _loading) return;
        MarkDirty();
    }

    void Udp_Changed(object? sender, RoutedEventArgs e)
    {
        if (_syncing || _loading) return;
        MarkDirty();
    }

    void Port_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_loading) RefreshChrome();
    }

    void Port_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitPort();
    }

    void Port_LostFocus(object? sender, RoutedEventArgs e) => CommitPort();

    void Preset_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int port }) UsePort(port);
    }

    void UsePort(int port)
    {
        PortBox.Text = port.ToString();
        CommitPort();
    }

    // Порт меняем только по Enter или уходу из поля: недопечатанный «30» вместо «3066» оставил бы приложения без сети.
    void CommitPort()
    {
        if (_loading || !TryGetPort(out int port) || port == _port) return;
        _port = port;
        _settings.DefaultPort = port;
        MarkDirty();
    }

    void Search_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_loading) UpdateList();
    }

    void ShowAll_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_loading) UpdateList();
    }

    async void AddApp_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Какое приложение пустить через VPN",
            AllowMultiple = false,
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

        var name = Path.GetFileName(path);
        if (IsProtected(name, _snapshot))
        {
            await Dialogs.Info(this, "Это приложение проксировать нельзя: оно само обслуживает VPN.");
            return;
        }
        var row = AddRow(name);
        row.Path ??= path;
        row.IsProxied = true;
    }

    async void Restart_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: AppRow { Path: { } path } row }) return;
        if (!await Dialogs.Confirm(this, $"Перезапустить «{row.Title}»?\n\nВсе его окна закроются, несохранённые данные в нём пропадут.", "Перезапустить")) return;

        await Task.Run(() =>
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (new FileInfo($"/proc/{process.Id}/exe").LinkTarget is { } exe && Path.GetFileName(exe.Replace(" (deleted)", "")) == row.MatchName)
                        {
                            process.Kill();
                            process.WaitForExit(3000);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Чужой или уже завершившийся процесс.
                    }
                }
            }
            Thread.Sleep(500);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = false })?.Dispose();
        });
        await RefreshAsync();
    }

    async void BannerAction_Click(object? sender, RoutedEventArgs e)
    {
        switch ((sender as Button)?.Tag as string)
        {
            case "setup":
                var setup = new SetupWindow(_backend);
                await setup.ShowDialog(this);
                await RefreshAsync();
                if (!_serviceDown) await ApplyAsync();
                break;
            case "help":
                ShowHelp();
                break;
            case "diagnostics":
                await new DiagnosticsWindow(DiagnosticsSnapshot, _backend).ShowDialog(this);
                break;
            case { } action when action.StartsWith("port:"):
                UsePort(int.Parse(action["port:".Length..]));
                break;
        }
    }

    async void Diagnostics_Click(object? sender, RoutedEventArgs e) =>
        await new DiagnosticsWindow(DiagnosticsSnapshot, _backend).ShowDialog(this);

    DiagnosticsInput DiagnosticsSnapshot() => new(
        _rows.Where(r => r.IsProxied).GroupBy(PortOf).ToDictionary(g => g.Key, g => g.Count()),
        MasterSwitch.IsChecked != true,
        _rows.Where(r => r.IsProxied && r.IsRunning).Select(r => r.MatchName).ToList());

    async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings, _status?.State.FallbackDirect ?? false, OnFallbackChanged, Uninstall);
        await dialog.ShowDialog(this);
        _settings.Save();
    }

    async void OnFallbackChanged(bool direct)
    {
        if (_status is not null) _status.State.FallbackDirect = direct;
        var state = DesiredState();
        state.FallbackDirect = direct;
        var response = await _backend.CallAsync(new Request { Command = "apply", State = state });
        if (response?.Status is { } status) _status = status;
        RefreshChrome();
    }

    async void Uninstall(Window owner)
    {
        if (!await Dialogs.Confirm(owner,
                "Удалить службу «Тумблера» и всё, что она поставила?\n\nВыбранные приложения снова пойдут напрямую. Профили и настройки окна сохранятся.",
                "Удалить"))
            return;
        var (ok, message) = await Pkexec.RunAsync(["uninstall"], _ => { });
        await Dialogs.Info(owner, ok
            ? "Служба удалена. «Тумблер» сейчас закроется. Сам файл программы, из которого ты его запускал, можно удалить вручную."
            : $"Удалить не получилось: {message}");
        if (!ok) return;
        Exit();
    }

    void Help_Click(object? sender, RoutedEventArgs e) => ShowHelp();

    void ShowHelp(int? topic = null)
    {
        if (_help is not null)
        {
            _help.Activate();
            return;
        }
        _help = new HelpWindow(() => (_found, _port), UsePort, topic);
        _help.Closed += (_, _) => _help = null;
        _help.Show(this);
    }

    // ---------- Отладка: --demo --screenshot файл [--dialog settings|diagnostics|help|help:N] ----------

    async Task RunDevScriptAsync()
    {
        if (_options.Screenshot is null) return;
        await Task.Delay(1500);

        if (_options.Dialog is { } dialog)
        {
            Window window = dialog switch
            {
                "settings" => new SettingsWindow(_settings, false, _ => { }, null),
                "diagnostics" => new DiagnosticsWindow(DiagnosticsSnapshot, _backend),
                _ => new HelpWindow(() => (_found, _port), UsePort, int.TryParse(dialog.Split(':').Last(), out int topic) ? topic : null),
            };
            await Shots.CaptureAsync(window, _options.Screenshot, dialog == "diagnostics" ? 4000 : 1200);
        }
        else
        {
            Shots.Save(this, _options.Screenshot);
        }
        _exiting = true;
        Close();
    }
}
