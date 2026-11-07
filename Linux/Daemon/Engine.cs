using System.Reflection;
using System.Text.Json;

namespace Tumbler.Daemon;

/// <summary>
/// Сердце службы. Раз в секунду приводит систему к нужному состоянию: туннели по портам, процессы выбранных
/// приложений в своих cgroup, правила nftables и маршруты. Всё идемпотентно — сбой одного шага чинится на следующем круге.
/// </summary>
internal sealed class Engine : IDisposable
{
    const string StateFile = "/etc/tumbler/state.json";
    const string OriginsFile = "/run/tumbler/origins.json";
    static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);
    static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    readonly object _gate = new();
    readonly string _hevPath;
    readonly Dictionary<int, Tunnel> _tunnels = new();
    readonly Dictionary<int, (bool Alive, int Streak)> _probes = new();
    readonly HashSet<int> _down = new();
    readonly Dictionary<string, int> _captured = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<int> _vpnClients = new();
    readonly HashSet<int> _moveFailures = new();
    Dictionary<int, string> _origins;
    DaemonState _state;
    string _ruleset = "";
    string? _rulesetError;
    DateTime _retryRulesetAt;
    DateTime _lastProbe = DateTime.MinValue;
    bool _firstProbe = true;
    bool _originsDirty;

    public Engine(string hevPath)
    {
        _hevPath = hevPath;
        _state = Load<DaemonState>(StateFile) ?? new DaemonState();
        _origins = Load<Dictionary<int, string>>(OriginsFile) ?? new();
    }

    static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

    public async Task RunAsync(CancellationToken cancel)
    {
        Net.CleanupLeftovers();
        Sys.Run("modprobe", "tun");
        Sys.Run("modprobe", "nft_socket");
        if (Cgroups.V2) Directory.CreateDirectory($"{Cgroups.Fs}/{Cgroups.Base}");
        Sys.Log($"Тумблер {Version}: служба запущена, приложений в правилах: {_state.Rules.Count}");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            lock (_gate) Reconcile();
        }
        while (await WaitTick(timer, cancel));
    }

    static async Task<bool> WaitTick(PeriodicTimer timer, CancellationToken cancel)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public Response Handle(Request request)
    {
        lock (_gate)
        {
            switch (request.Command)
            {
                case "status":
                    break;
                case "apply" when request.State is { } state:
                    if (Validate(state) is { } error) return new Response { Error = error, Status = Status() };
                    _state = state;
                    break;
                case "pause":
                    _state.Enabled = false;
                    break;
                case "resume":
                    _state.Enabled = true;
                    break;
                case "toggle":
                    _state.Enabled = !_state.Enabled;
                    break;
                default:
                    return new Response { Error = $"неизвестная команда «{request.Command}»" };
            }

            if (request.Command != "status")
            {
                Save(StateFile, _state);
                Reconcile();
            }
            return new Response { Ok = true, Status = Status() };
        }
    }

    static string? Validate(DaemonState state)
    {
        foreach (var (app, port) in state.Rules)
        {
            if (string.IsNullOrWhiteSpace(app) || app.Contains('/')) return $"некорректное имя приложения «{app}»";
            if (port is < 1 or > 65535) return $"некорректный порт {port} у «{app}»";
        }
        return null;
    }

    // ---------- Основной цикл ----------

    void Reconcile()
    {
        try
        {
            if (!Cgroups.V2) return;

            var rules = _state.Rules
                .Where(r => !string.IsNullOrWhiteSpace(r.Key))
                .GroupBy(r => r.Key.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
            var ports = rules.Values.ToHashSet();

            UpdateTunnels(ports);
            ProbePorts(ports);
            MoveProcesses(rules, ports);
            UpdateRuleset();
            SaveOrigins();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Sys.Log($"Ошибка в цикле службы: {ex.Message}");
        }
    }

    void UpdateTunnels(HashSet<int> ports)
    {
        foreach (int port in ports)
        {
            Cgroups.Ensure(port);
            if (!_tunnels.TryGetValue(port, out var tunnel))
            {
                int slot = Net.FreeSlot(_tunnels.Values.Select(t => t.Slot));
                Net.EnsureRouting(slot);
                _tunnels[port] = tunnel = new Tunnel(port, slot, _hevPath);
            }
            tunnel.Maintain();
        }

        foreach (int port in _tunnels.Keys.Where(p => !ports.Contains(p)).ToList())
        {
            var tunnel = _tunnels[port];
            tunnel.Dispose();
            Net.RemoveRouting(tunnel.Slot);
            _tunnels.Remove(port);
            _probes.Remove(port);
            _down.Remove(port);
        }
    }

    /// <summary>Порт считается упавшим (или ожившим) после двух одинаковых проверок подряд — кроме самой первой.</summary>
    void ProbePorts(HashSet<int> ports)
    {
        if (DateTime.UtcNow - _lastProbe < ProbeInterval) return;
        _lastProbe = DateTime.UtcNow;

        var results = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();
        Parallel.ForEach(ports, port => results[port] = Socks.Probe("127.0.0.1", port));
        foreach (var (port, alive) in results)
        {
            var previous = _probes.GetValueOrDefault(port, (Alive: alive, Streak: 0));
            int streak = previous.Alive == alive ? previous.Streak + 1 : 1;
            _probes[port] = (alive, streak);

            if (streak < 2 && !_firstProbe) continue;
            bool changed = alive ? _down.Remove(port) : _down.Add(port);
            if (changed) Sys.Log(alive ? $"Прокси на порту {port} снова отвечает" : $"Прокси на порту {port} не отвечает");
        }
        _firstProbe = false;
    }

    /// <summary>
    /// Переносит процессы выбранных приложений в cgroup их порта, а остальные — обратно, туда, откуда они пришли.
    /// Новые процессы приложения подхватываются на следующем круге, то есть в пределах секунды.
    /// </summary>
    void MoveProcesses(Dictionary<string, int> rules, HashSet<int> ports)
    {
        _captured.Clear();
        var listeners = ListeningSockets(ports);
        var seen = new HashSet<int>();
        int self = Environment.ProcessId;
        var tunnels = _tunnels.Values.Select(t => t.Pid).OfType<int>().ToHashSet();

        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(System.IO.Path.GetFileName(dir), out int pid) || pid <= 1 || pid == self || tunnels.Contains(pid))
                continue;
            var cgroup = Cgroups.Of(pid);
            var (exe, comm) = Names(pid);
            if (cgroup is null || (exe is null && comm is null)) continue;
            seen.Add(pid);

            bool ours = Cgroups.IsOurs(cgroup);
            int? port = Match(rules, exe) ?? Match(rules, comm);
            if (port is int target && !_vpnClients.Contains(pid))
            {
                var wanted = Cgroups.Path(target);
                if (cgroup != wanted)
                {
                    // Сам VPN-клиент проксировать нельзя: его трафик к серверу ушёл бы в его же порт по кругу.
                    if (!ours && OwnsAny(pid, listeners))
                    {
                        _vpnClients.Add(pid);
                        Sys.Log($"{exe ?? comm} (PID {pid}) слушает порт прокси — это VPN-клиент, его не трогаю");
                        continue;
                    }
                    if (!ours)
                    {
                        _origins[pid] = cgroup;
                        _originsDirty = true;
                    }
                    if (!Cgroups.Move(pid, wanted))
                    {
                        if (_moveFailures.Add(pid)) Sys.Log($"Не удалось перенести {exe ?? comm} (PID {pid}) в {wanted}");
                        continue;
                    }
                }
                var name = rules.Keys.First(k => k.Equals(exe, StringComparison.OrdinalIgnoreCase) || k.Equals(comm, StringComparison.OrdinalIgnoreCase));
                _captured[name] = _captured.GetValueOrDefault(name) + 1;
            }
            else if (ours)
            {
                Restore(pid);
            }
        }

        _vpnClients.IntersectWith(seen);
        _moveFailures.IntersectWith(seen);
        foreach (int pid in _origins.Keys.Where(p => !seen.Contains(p)).ToList())
        {
            _origins.Remove(pid);
            _originsDirty = true;
        }
    }

    static int? Match(Dictionary<string, int> rules, string? name) =>
        name is not null && rules.TryGetValue(name, out int port) ? port : null;

    /// <summary>
    /// Возвращает процесс в его прежнюю cgroup. Если её уже нет (systemd убирает опустевшие scope) или процесс
    /// родился у нас — например, игра, запущенная из проксируемого лаунчера, — переносим его в корень.
    /// </summary>
    void Restore(int pid)
    {
        if (!_origins.Remove(pid, out var origin))
            origin = ParentOrigin(pid) ?? "/";
        _originsDirty = true;
        if (!Cgroups.Move(pid, origin)) Cgroups.Move(pid, "/");
    }

    string? ParentOrigin(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            // Имя процесса в скобках может содержать пробелы, поэтому считаем поля после последней «)».
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return int.TryParse(fields[1], out int parent) && _origins.TryGetValue(parent, out var origin) ? origin : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Имя исполняемого файла и имя процесса (comm, до 15 символов).</summary>
    static (string? Exe, string? Comm) Names(int pid)
    {
        string? exe = null, comm = null;
        try
        {
            var target = new FileInfo($"/proc/{pid}/exe").LinkTarget;
            if (target is not null)
                exe = System.IO.Path.GetFileName(target.Replace(" (deleted)", ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        try
        {
            comm = File.ReadAllText($"/proc/{pid}/comm").Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        // У потоков ядра нет исполняемого файла — их не трогаем.
        return exe is null ? (null, null) : (exe, comm);
    }

    /// <summary>Inode сокетов, которые слушают порты прокси: по ним узнаём сам VPN-клиент.</summary>
    static HashSet<string> ListeningSockets(HashSet<int> ports)
    {
        var inodes = new HashSet<string>();
        foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            try
            {
                foreach (var line in File.ReadLines(table).Skip(1))
                {
                    var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 10 || f[3] != "0A") continue;
                    int port = Convert.ToInt32(f[1][(f[1].LastIndexOf(':') + 1)..], 16);
                    if (ports.Contains(port)) inodes.Add(f[9]);
                }
            }
            catch (Exception ex) when (ex is IOException or FormatException)
            {
            }
        }
        return inodes;
    }

    static bool OwnsAny(int pid, HashSet<string> inodes)
    {
        if (inodes.Count == 0) return false;
        try
        {
            foreach (var fd in Directory.EnumerateFiles($"/proc/{pid}/fd"))
            {
                var link = new FileInfo(fd).LinkTarget;
                if (link is not null && link.StartsWith("socket:[") && inodes.Contains(link[8..^1])) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return false;
    }

    void UpdateRuleset()
    {
        // Помечаем трафик порта, если не пауза. В режиме «пускать напрямую» упавший VPN или туннель снимает метку;
        // в режиме «без сети» метка остаётся, и трафик упирается в запасной маршрут unreachable.
        var marked = _state.Enabled
            ? _tunnels.Values
                .Where(t => !(_state.FallbackDirect && (_down.Contains(t.Port) || !t.Ready)))
                .OrderBy(t => t.Port)
                .Select(t => (t.Port, t.Slot))
                .ToList()
            : [];
        var ruleset = marked.Count == 0 ? "" : Net.BuildRuleset(marked, _state.Udp);

        bool retry = _rulesetError is not null && DateTime.UtcNow >= _retryRulesetAt;
        if (ruleset == _ruleset && !retry) return;

        if (ruleset.Length == 0)
        {
            Net.RemoveRuleset();
            _rulesetError = null;
        }
        else if (Net.ApplyRuleset(ruleset, out var error))
        {
            _rulesetError = null;
        }
        else
        {
            _rulesetError = error;
            _retryRulesetAt = DateTime.UtcNow + RetryInterval;
            Sys.Log($"nftables отклонил правила: {error}");
        }
        _ruleset = ruleset;
    }

    DaemonStatus Status() => new()
    {
        Version = Version,
        State = _state,
        Tunnels = _tunnels.Values.OrderBy(t => t.Port).Select(t => new TunnelStatus
        {
            Port = t.Port,
            Device = t.Device,
            Running = t.Ready,
            ProxyAlive = !_down.Contains(t.Port),
            Suspended = _state.Enabled && _state.FallbackDirect && (_down.Contains(t.Port) || !t.Ready),
            Error = t.Error,
        }).ToList(),
        Captured = new Dictionary<string, int>(_captured),
        RulesLoaded = _ruleset.Length > 0 && _rulesetError is null,
        Problems = Problems(),
    };

    List<string> Problems()
    {
        var problems = new List<string>();
        if (!Cgroups.V2) problems.Add("В системе нет cgroup v2 — без неё перехват по приложениям невозможен.");
        if (Sys.Find("nft") is null) problems.Add("Не установлен nftables (команда nft).");
        if (Sys.Find("ip") is null) problems.Add("Не установлен iproute2 (команда ip).");
        if (!File.Exists(_hevPath)) problems.Add($"Нет файла {_hevPath} — переустанови «Тумблер».");
        if (_rulesetError is not null) problems.Add($"nftables отклонил правила: {_rulesetError}");
        return problems;
    }

    // ---------- Сохранение ----------

    static T? Load<T>(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Wire.Json) : default;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Sys.Log($"Не прочитался {path}: {ex.Message}");
            return default;
        }
    }

    static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Wire.Json));
        File.Move(temp, path, overwrite: true);
    }

    void SaveOrigins()
    {
        if (!_originsDirty) return;
        Save(OriginsFile, _origins);
        _originsDirty = false;
    }

    /// <summary>Остановка службы: снимаем правила и маршруты, гасим туннели, возвращаем процессы на место.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            Net.RemoveRuleset();
            foreach (var tunnel in _tunnels.Values)
            {
                tunnel.Dispose();
                Net.RemoveRouting(tunnel.Slot);
            }
            _tunnels.Clear();

            if (Cgroups.V2 && Directory.Exists($"{Cgroups.Fs}/{Cgroups.Base}"))
            {
                foreach (var dir in Directory.GetDirectories($"{Cgroups.Fs}/{Cgroups.Base}"))
                    foreach (int pid in Cgroups.Members($"/{Cgroups.Base}/{System.IO.Path.GetFileName(dir)}"))
                        Restore(pid);
                Cgroups.RemoveAll();
            }
            try
            {
                File.Delete(OriginsFile);
            }
            catch (IOException)
            {
            }
            Sys.Log("Служба остановлена, всё вернулось как было.");
        }
    }
}
