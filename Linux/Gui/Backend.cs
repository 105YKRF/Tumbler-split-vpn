using System.Collections.Concurrent;
using Tumbler.Daemon;

namespace Tumbler.Gui;

/// <summary>Все процессы с одним именем исполняемого файла — для службы это одно приложение.</summary>
public sealed record ProcGroup(string Name, string? Path, string Title, string? IconPath, int Count, int Connections, bool IsApp, bool Mine);

/// <summary>Снимок системы для окна: процессы, живые ли порты, кто их слушает, чужие TUN-интерфейсы.</summary>
public sealed record Snapshot(
    List<ProcGroup> Groups,
    Dictionary<int, bool> PortAlive,
    Dictionary<int, string> PortOwners,
    List<string> OtherTunnels);

/// <summary>Откуда окно берёт данные: настоящая система или демонстрационные данные для снимков экрана.</summary>
public interface IBackend
{
    bool IsDemo { get; }

    /// <summary>null — служба не отвечает (не установлена или упала).</summary>
    Task<Response?> CallAsync(Request request);

    Snapshot Scan(IReadOnlyCollection<int> ports);

    /// <summary>Работающие SOCKS5-прокси на этом компьютере.</summary>
    List<PortPreset> FindProxies();
}

internal sealed class LinuxBackend : IBackend
{
    DesktopIndex _desktop = DesktopIndex.Load();
    DateTime _desktopLoaded = DateTime.UtcNow;

    public bool IsDemo => false;

    public async Task<Response?> CallAsync(Request request)
    {
        try
        {
            return await Wire.CallAsync(request, TimeSpan.FromSeconds(4));
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public Snapshot Scan(IReadOnlyCollection<int> ports)
    {
        // Новые приложения ставятся нечасто — индекс меню перечитываем раз в минуту.
        if (DateTime.UtcNow - _desktopLoaded > TimeSpan.FromMinutes(1))
        {
            _desktop = DesktopIndex.Load();
            _desktopLoaded = DateTime.UtcNow;
        }
        return ProcScanner.Scan(ports, _desktop);
    }

    public List<PortPreset> FindProxies() => ProcScanner.FindProxies();
}

/// <summary>
/// Чтение /proc. Процессы других пользователей видны только по имени: их исполняемый файл и сокеты
/// обычному пользователю не доступны, но для списка этого хватает.
/// </summary>
internal static class ProcScanner
{
    public static Snapshot Scan(IReadOnlyCollection<int> ports, DesktopIndex desktop)
    {
        uint me = Sys.Uid;
        var sockets = ReadSockets();
        var groups = new Dictionary<string, Acc>(StringComparer.Ordinal);
        var owners = new Dictionary<int, string>();

        foreach (var dir in SafeDirs("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid) || pid <= 1) continue;
            var (exe, comm, uid) = Describe(pid);
            var name = exe is not null ? Path.GetFileName(exe) : comm;
            if (name is null || (exe is null && uid != me && IsKernelThread(pid))) continue;

            if (!groups.TryGetValue(name, out var group)) groups[name] = group = new Acc();
            group.Count++;
            group.Path ??= exe;
            group.Mine |= uid == me;

            if (uid != me) continue;
            foreach (var inode in SocketInodes(pid))
            {
                if (sockets.Established.Contains(inode)) group.Connections++;
                if (sockets.Listening.TryGetValue(inode, out int port) && ports.Contains(port)) owners[port] = name;
            }
        }

        var list = groups.Select(g =>
        {
            var app = desktop.Find(g.Key);
            return new ProcGroup(g.Key, g.Value.Path, app?.Name ?? g.Key, app?.IconPath, g.Value.Count, g.Value.Connections, app is not null, g.Value.Mine);
        }).ToList();

        var alive = new ConcurrentDictionary<int, bool>();
        Parallel.ForEach(ports, port => alive[port] = Socks.Probe("127.0.0.1", port));
        return new Snapshot(list, new Dictionary<int, bool>(alive), owners, OtherTunnels());
    }

    /// <summary>Опрашивает все локальные слушающие порты и оставляет те, где отвечает SOCKS5.</summary>
    public static List<PortPreset> FindProxies()
    {
        var sockets = ReadSockets();
        var names = new Dictionary<string, string>();
        uint me = Sys.Uid;
        foreach (var dir in SafeDirs("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid)) continue;
            var (exe, comm, uid) = Describe(pid);
            if (uid != me) continue;
            foreach (var inode in SocketInodes(pid))
                if (sockets.Listening.ContainsKey(inode))
                    names[inode] = exe is not null ? Path.GetFileName(exe) : comm ?? "?";
        }

        var found = new ConcurrentBag<PortPreset>();
        var candidates = sockets.Listening.Where(l => sockets.Loopback.Contains(l.Key)).ToList();
        Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = 16 }, listener =>
        {
            var name = names.GetValueOrDefault(listener.Key);
            // Порты процессов root тоже проверяем, хотя их владельца не видно: VPN-клиент мог быть запущен как системная служба.
            if (Socks.Probe("127.0.0.1", listener.Value))
            {
                var label = name is null ? listener.Value.ToString() : $"{listener.Value} · {name}";
                var hint = name is null ? "На этом порту отвечает SOCKS5-прокси" : $"На этом порту отвечает SOCKS5-прокси (процесс {name})";
                found.Add(new PortPreset(listener.Value, label, hint));
            }
        });
        return found.DistinctBy(p => p.Port).OrderBy(p => p.Port).ToList();
    }

    sealed class Acc
    {
        public int Count, Connections;
        public string? Path;
        public bool Mine;
    }

    sealed record Sockets(HashSet<string> Established, Dictionary<string, int> Listening, HashSet<string> Loopback);

    /// <summary>Таблицы TCP ядра: установленные соединения наружу и слушающие порты (inode → порт).</summary>
    static Sockets ReadSockets()
    {
        var established = new HashSet<string>();
        var listening = new Dictionary<string, int>();
        var loopback = new HashSet<string>();
        foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            IEnumerable<string> lines;
            try
            {
                lines = File.ReadLines(table).Skip(1).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var line in lines)
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 10) continue;
                var (localAddress, localPort) = Split(f[1]);
                var (remoteAddress, _) = Split(f[2]);
                if (f[3] == "01" && !IsLoopback(remoteAddress)) established.Add(f[9]);
                if (f[3] == "0A")
                {
                    listening[f[9]] = localPort;
                    if (IsLoopback(localAddress) || IsAny(localAddress)) loopback.Add(f[9]);
                }
            }
        }
        return new Sockets(established, listening, loopback);

        static (string Address, int Port) Split(string field)
        {
            int colon = field.LastIndexOf(':');
            return (field[..colon], Convert.ToInt32(field[(colon + 1)..], 16));
        }

        // Адреса в /proc/net/tcp — шестнадцатеричные, байты IPv4 в обратном порядке: 127.0.0.1 → 0100007F.
        static bool IsLoopback(string hex) => hex.EndsWith("7F") && hex.Length == 8 || hex == "00000000000000000000000001000000";
        static bool IsAny(string hex) => hex.All(c => c == '0');
    }

    static IEnumerable<string> SocketInodes(int pid)
    {
        IEnumerable<string> fds;
        try
        {
            fds = Directory.EnumerateFiles($"/proc/{pid}/fd").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var fd in fds)
        {
            string? link = null;
            try
            {
                link = new FileInfo(fd).LinkTarget;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            if (link is not null && link.StartsWith("socket:[")) yield return link[8..^1];
        }
    }

    static (string? Exe, string? Comm, uint Uid) Describe(int pid)
    {
        string? exe = null, comm = null;
        uint uid = uint.MaxValue;
        try
        {
            var target = new FileInfo($"/proc/{pid}/exe").LinkTarget;
            if (target is not null) exe = target.Replace(" (deleted)", "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        try
        {
            comm = File.ReadAllText($"/proc/{pid}/comm").Trim();
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
                if (line.StartsWith("Uid:"))
                {
                    uid = uint.Parse(line.Split('\t', StringSplitOptions.RemoveEmptyEntries)[1]);
                    break;
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException)
        {
        }
        return (exe, comm, uid);
    }

    // У потоков ядра пустой cmdline.
    static bool IsKernelThread(int pid)
    {
        try
        {
            return File.ReadAllBytes($"/proc/{pid}/cmdline").Length == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>TUN-интерфейсы VPN-клиентов. Пока такой поднят, в VPN уходит весь трафик, а не только выбранный.</summary>
    static List<string> OtherTunnels() =>
        SafeDirs("/sys/class/net")
            .Where(d => File.Exists(Path.Combine(d, "tun_flags")) && !Path.GetFileName(d).StartsWith("tmb"))
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToList();

    static IEnumerable<string> SafeDirs(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
