using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Tumbler;

/// <summary>Все процессы с одним именем exe — для ProxiFyre это одно приложение.</summary>
internal sealed record ProcGroup(string BaseName, string Path, string Title, int Count, bool HasWindow, int Connections, bool UserApp);

internal sealed record ScanResult(
    List<ProcGroup> Groups,
    ServiceState Service,
    Dictionary<int, bool> PortAlive,
    Dictionary<int, string> PortOwners,
    List<string> Tunnels,
    KaringStats? Karing);

internal static partial class Scanner
{
    static readonly ConcurrentDictionary<string, string?> Titles = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\b(tun|wintun|wireguard|tap-windows)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TunnelName();

    public static ScanResult Scan(string host, IReadOnlyCollection<int> ports)
    {
        var (connections, listeners) = ReadTcpTable(ports);
        var windowPids = WindowPids();
        int session = Process.GetCurrentProcess().SessionId;
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";

        var groups = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);
        var portOwners = new Dictionary<int, string>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                int pid = process.Id;
                if (pid <= 4) continue;
                var path = ImagePath(pid);
                if (path is null) continue;

                var baseName = Path.GetFileNameWithoutExtension(path);
                foreach (var (port, listenerPid) in listeners)
                    if (listenerPid == pid) portOwners[port] = baseName;

                if (!groups.TryGetValue(baseName, out var group))
                    groups[baseName] = group = new Accumulator(baseName, path);

                bool hasWindow = windowPids.Contains(pid);
                if (hasWindow && !group.HasWindow) group.Path = path;
                group.Count++;
                group.HasWindow |= hasWindow;
                group.Connections += connections.GetValueOrDefault(pid);
                group.UserApp |= process.SessionId == session && !path.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase);
            }
        }

        var list = groups.Values
            .Select(g => new ProcGroup(g.BaseName, g.Path, TitleOf(g.Path) ?? g.BaseName, g.Count, g.HasWindow, g.Connections, g.UserApp))
            .ToList();
        var alive = ports.ToDictionary(p => p, p => Socks.Probe(host, p));

        return new ScanResult(list, ServiceCtl.Query(), alive, portOwners, Tunnels(), Karing.Stats());
    }

    /// <summary>
    /// Ищет на этом компьютере работающие SOCKS5-прокси: опрашивает все локальные слушающие порты.
    /// Так «Тумблер» сам подсказывает порт VPN-клиента, каким бы он ни был.
    /// </summary>
    public static List<PortPreset> FindProxies(string host)
    {
        var found = new ConcurrentBag<PortPreset>();
        Parallel.ForEach(LoopbackListeners(), new ParallelOptions { MaxDegreeOfParallelism = 16 }, listener =>
        {
            var path = ImagePath(listener.Pid);
            if (path is null) return;
            var name = Path.GetFileNameWithoutExtension(path);
            // Локальные порты самого ProxiFyre — это не прокси, а его перехватчик.
            if (name.StartsWith("ProxiFyre", StringComparison.OrdinalIgnoreCase)) return;

            if (Socks.Probe(host, listener.Port))
                found.Add(new PortPreset(listener.Port, $"{listener.Port} · {name}", $"На этом порту отвечает SOCKS5-прокси (процесс {name}.exe)"));
        });
        return found.OrderBy(p => p.Port).ToList();
    }

    public static string? TitleOf(string path) => Titles.GetOrAdd(path, static p =>
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(p).FileDescription;
            return string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    });

    /// <summary>Поднятые TUN-адаптеры: пока такой включён, в VPN уходит весь трафик, а не только выбранные приложения.</summary>
    public static List<string> Tunnels()
    {
        try
        {
            // У каждого адаптера есть двойники вида «…-WinpkFilter NDIS LightWeight Filter-0000» — их не показываем.
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && TunnelName().IsMatch(n.Description)
                            && !n.Description.Contains("Filter-", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("Packet Scheduler", StringComparison.OrdinalIgnoreCase))
                .Select(n => n.Name)
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return new();
        }
    }

    sealed class Accumulator(string baseName, string path)
    {
        public string BaseName = baseName;
        public string Path = path;
        public int Count, Connections;
        public bool HasWindow, UserApp;
    }

    static string? ImagePath(int pid)
    {
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return Native.QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    static HashSet<int> WindowPids()
    {
        var pids = new HashSet<int>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (Native.IsWindowVisible(hwnd) && Native.GetWindowTextLength(hwnd) > 0)
            {
                Native.GetWindowThreadProcessId(hwnd, out int pid);
                pids.Add(pid);
            }
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    /// <summary>Порты, которые кто-то слушает на 127.0.0.1 или на всех адресах сразу.</summary>
    static List<(int Port, int Pid)> LoopbackListeners()
    {
        const int AF_INET = 2, TCP_TABLE_OWNER_PID_LISTENER = 3, RowSize = 24;
        var listeners = new List<(int, int)>();

        int size = 0;
        Native.GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        size += 4096;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (Native.GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                return listeners;

            int rows = Marshal.ReadInt32(buffer);
            for (int i = 0; i < rows; i++)
            {
                var row = buffer + 4 + i * RowSize;
                int address = Marshal.ReadInt32(row, 4);
                int raw = Marshal.ReadInt32(row, 8);
                int port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                if (address == 0 || (address & 0xFF) == 127)
                    listeners.Add((port, Marshal.ReadInt32(row, 20)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return listeners;
    }

    /// <summary>Число внешних TCP-соединений по процессам и PID тех, кто слушает порты прокси.</summary>
    static (Dictionary<int, int> Connections, Dictionary<int, int> Listeners) ReadTcpTable(IReadOnlyCollection<int> ports)
    {
        const int AF_INET = 2, TCP_TABLE_OWNER_PID_ALL = 5;
        const int StateListen = 2, StateEstablished = 5, RowSize = 24;

        var connections = new Dictionary<int, int>();
        var listeners = new Dictionary<int, int>();

        int size = 0;
        Native.GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        size += 4096; // таблица могла вырасти между двумя вызовами
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (Native.GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                return (connections, listeners);

            int rows = Marshal.ReadInt32(buffer);
            for (int i = 0; i < rows; i++)
            {
                var row = buffer + 4 + i * RowSize;
                int state = Marshal.ReadInt32(row);
                int localPort = HostPort(Marshal.ReadInt32(row, 8));
                int remoteFirstOctet = Marshal.ReadInt32(row, 12) & 0xFF;
                int pid = Marshal.ReadInt32(row, 20);

                if (state == StateListen && ports.Contains(localPort)) listeners[localPort] = pid;
                if (state == StateEstablished && remoteFirstOctet != 127)
                    connections[pid] = connections.GetValueOrDefault(pid) + 1;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return (connections, listeners);

        static int HostPort(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
    }
}
