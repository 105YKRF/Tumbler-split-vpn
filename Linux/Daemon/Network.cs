using System.Text;

namespace Tumbler.Daemon;

/// <summary>
/// Своя cgroup на каждый порт прокси: /sys/fs/cgroup/tumbler/p3066. Процессы выбранных приложений переносятся туда,
/// а nftables узнаёт их сокеты по этой cgroup.
/// </summary>
internal static class Cgroups
{
    public const string Fs = "/sys/fs/cgroup";
    public const string Base = "tumbler";

    public static bool V2 => File.Exists($"{Fs}/cgroup.controllers");

    /// <summary>Путь относительно корня cgroupfs — в таком виде его ждёт nftables.</summary>
    public static string Name(int port) => $"{Base}/p{port}";

    /// <summary>Путь в том виде, в каком он записан в /proc/PID/cgroup.</summary>
    public static string Path(int port) => $"/{Name(port)}";

    public static bool IsOurs(string cgroup) => cgroup.StartsWith($"/{Base}/", StringComparison.Ordinal);

    public static void Ensure(int port) => Directory.CreateDirectory($"{Fs}/{Name(port)}");

    /// <summary>Текущая cgroup процесса (строка «0::/путь» в /proc/PID/cgroup) или null, если процесса уже нет.</summary>
    public static string? Of(int pid)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/cgroup"))
                if (line.StartsWith("0::", StringComparison.Ordinal))
                    return line[3..];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>Переносит процесс целиком, со всеми потоками. Сокеты, открытые до переноса, остаются в старой cgroup.</summary>
    public static bool Move(int pid, string cgroup) =>
        Sys.WriteKernelFile($"{Fs}{cgroup.TrimEnd('/')}/cgroup.procs", pid.ToString());

    public static IEnumerable<int> Members(string cgroup)
    {
        try
        {
            return File.ReadAllLines($"{Fs}{cgroup}/cgroup.procs").Select(int.Parse).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return [];
        }
    }

    /// <summary>Убирает наши cgroup; оставшиеся в них процессы переезжают в корень.</summary>
    public static void RemoveAll()
    {
        var root = $"{Fs}/{Base}";
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.GetDirectories(root))
        {
            foreach (var pid in Members($"/{Base}/{System.IO.Path.GetFileName(dir)}"))
                Move(pid, "/");
            TryRemove(dir);
        }
        TryRemove(root);
    }

    static void TryRemove(string dir)
    {
        try
        {
            Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Sys.Log($"Не удалось удалить {dir}: {ex.Message}");
        }
    }
}

/// <summary>
/// Маршрутизация: nftables помечает пакеты из cgroup порта, а правило ip rule отправляет помеченные пакеты
/// в отдельную таблицу маршрутов, где маршрут по умолчанию ведёт в туннель этого порта.
/// </summary>
internal static class Net
{
    public const string Table = "tumbler";
    const int MarkBase = 0x7400, TableBase = 7400, MaxSlot = 250;

    public static int Mark(int slot) => MarkBase + slot;
    public static int RouteTable(int slot) => TableBase + slot;

    /// <summary>
    /// Полный набор правил nftables. Применяется одной транзакцией: старая таблица удаляется и создаётся заново.
    /// Локальные и частные адреса не трогаем — как «обход локальной сети» в Windows-версии.
    /// </summary>
    public static string BuildRuleset(IReadOnlyCollection<(int Port, int Slot)> marked, bool udp)
    {
        var protocols = udp ? "{ tcp, udp }" : "tcp";
        var text = new StringBuilder();
        text.AppendLine($"table inet {Table}");
        text.AppendLine($"delete table inet {Table}");
        text.AppendLine($"table inet {Table} {{");
        text.AppendLine("\tset direct4 {");
        text.AppendLine("\t\ttype ipv4_addr");
        text.AppendLine("\t\tflags interval");
        text.AppendLine("\t\telements = { 0.0.0.0/8, 10.0.0.0/8, 100.64.0.0/10, 127.0.0.0/8, 169.254.0.0/16, 172.16.0.0/12, 192.168.0.0/16, 224.0.0.0/4, 255.255.255.255/32 }");
        text.AppendLine("\t}");
        text.AppendLine();
        text.AppendLine("\tchain route_out {");
        text.AppendLine("\t\ttype route hook output priority mangle; policy accept;");
        text.AppendLine("\t\tip daddr @direct4 return");
        foreach (var (port, slot) in marked)
            text.AppendLine($"\t\tmeta nfproto ipv4 meta l4proto {protocols} socket cgroupv2 level 2 \"{Cgroups.Name(port)}\" meta mark set 0x{Mark(slot):x}");
        text.AppendLine("\t}");
        text.AppendLine();
        // Туннель только IPv4. IPv6 выбранных приложений режем, чтобы он не ушёл мимо VPN: приложения сами перейдут на IPv4.
        text.AppendLine("\tchain filter_out {");
        text.AppendLine("\t\ttype filter hook output priority filter; policy accept;");
        foreach (var (port, _) in marked)
            text.AppendLine($"\t\tmeta nfproto ipv6 socket cgroupv2 level 2 \"{Cgroups.Name(port)}\" ip6 daddr != {{ ::1, fe80::/10 }} reject");
        text.AppendLine("\t}");
        text.AppendLine("}");
        return text.ToString();
    }

    public static bool ApplyRuleset(string ruleset, out string error)
    {
        var (code, output) = Sys.Run("nft", "-f -", ruleset);
        error = output;
        return code == 0;
    }

    public static void RemoveRuleset()
    {
        if (Sys.Run("nft", $"list table inet {Table}").Code == 0)
            Sys.Try("nft", $"delete table inet {Table}");
    }

    public static int FreeSlot(IEnumerable<int> used)
    {
        var taken = used.ToHashSet();
        for (int slot = 1; slot <= MaxSlot; slot++)
            if (!taken.Contains(slot)) return slot;
        throw new InvalidOperationException("слишком много разных портов");
    }

    /// <summary>
    /// Таблица маршрутов порта. Запасной маршрут unreachable с большой метрикой остаётся, даже когда туннель
    /// упал и его интерфейс исчез, — иначе помеченный трафик провалился бы в основную таблицу и ушёл мимо VPN.
    /// </summary>
    public static void EnsureRouting(int slot)
    {
        int table = RouteTable(slot);
        Sys.Try("ip", $"route replace unreachable default metric 4000 table {table}");
        if (!RuleExists(table))
            Sys.Try("ip", $"rule add fwmark 0x{Mark(slot):x} lookup {table} pref {table}");
    }

    public static bool AttachDevice(int slot, string device) =>
        Sys.Try("ip", $"route replace default dev {device} metric 100 table {RouteTable(slot)}");

    public static void RemoveRouting(int slot)
    {
        int table = RouteTable(slot);
        while (RuleExists(table) && Sys.Run("ip", $"rule del pref {table}").Code == 0)
        {
        }
        Sys.Run("ip", $"route flush table {table}");
    }

    /// <summary>Убирает всё, что могло остаться после аварийного завершения службы.</summary>
    public static void CleanupLeftovers()
    {
        RemoveRuleset();
        foreach (var line in Sys.Run("ip", "rule show").Output.Split('\n'))
        {
            var pref = line.Split(':')[0].Trim();
            if (int.TryParse(pref, out int table) && table > TableBase && table <= TableBase + MaxSlot)
                RemoveRouting(table - TableBase);
        }
    }

    static bool RuleExists(int table) =>
        Sys.Run("ip", "rule show").Output.Split('\n').Any(line => line.TrimStart().StartsWith($"{table}:", StringComparison.Ordinal));
}
