using System.Diagnostics;

namespace Tumbler.Daemon;

/// <summary>
/// Один экземпляр hev-socks5-tunnel на порт: TUN-интерфейс tmbПОРТ, всё, что в него попадает, уходит в SOCKS5 127.0.0.1:ПОРТ.
/// Если туннель падает, перезапускаем его с нарастающей паузой.
/// </summary>
internal sealed class Tunnel(int port, int slot, string hevPath) : IDisposable
{
    Process? _process;
    bool _attached;
    int _failures;
    DateTime _nextStart;

    public int Port { get; } = port;
    public int Slot { get; } = slot;
    public string Device => $"tmb{Port}";
    public string? Error { get; private set; }
    public int? Pid => _process is { HasExited: false } process ? process.Id : null;

    public bool Ready => _attached && _process is { HasExited: false } && Directory.Exists($"/sys/class/net/{Device}");

    string ConfigPath => $"/run/tumbler/hev-{Port}.yml";

    public void Maintain()
    {
        if (_process is { HasExited: true })
        {
            Error = $"hev-socks5-tunnel завершился с кодом {_process.ExitCode}";
            Sys.Log($"[{Device}] {Error}");
            _process.Dispose();
            _process = null;
            _attached = false;
            _failures++;
            _nextStart = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(_failures, 5)));
        }

        if (_process is null)
        {
            if (DateTime.UtcNow >= _nextStart) Start();
            return;
        }

        if (!_attached && Directory.Exists($"/sys/class/net/{Device}"))
        {
            Sys.Try("ip", $"link set dev {Device} up");
            // Ответы приходят из туннеля с адресов, обратный маршрут к которым идёт через основной интерфейс.
            // Строгая проверка обратного пути (rp_filter=1) такие пакеты отбросила бы, поэтому на туннеле она мягкая.
            Sys.WriteKernelFile($"/proc/sys/net/ipv4/conf/{Device}/rp_filter", "2");
            _attached = Net.AttachDevice(Slot, Device);
            if (_attached)
            {
                _failures = 0;
                Error = null;
                Sys.Log($"[{Device}] туннель поднят → socks5://127.0.0.1:{Port}");
            }
        }
    }

    void Start()
    {
        if (!File.Exists(hevPath))
        {
            Error = $"нет файла {hevPath} — переустанови «Тумблер»";
            _nextStart = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            return;
        }

        Directory.CreateDirectory("/run/tumbler");
        File.WriteAllText(ConfigPath, Config());
        var info = new ProcessStartInfo(hevPath, ConfigPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            _process = Process.Start(info)!;
            _process.OutputDataReceived += (_, e) => LogLine(e.Data);
            _process.ErrorDataReceived += (_, e) => LogLine(e.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Error = $"не удалось запустить hev-socks5-tunnel: {ex.Message}";
            _failures++;
            _nextStart = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        }
    }

    void LogLine(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line)) Sys.Log($"[{Device}] {line}");
    }

    // Адрес интерфейса нужен hev-socks5-tunnel, но в маршрутизации не участвует: трафик попадает в туннель по метке.
    string Config() => $"""
        tunnel:
          name: {Device}
          mtu: 8500
          multi-queue: false
          ipv4: 10.255.{Slot}.1
        socks5:
          port: {Port}
          address: 127.0.0.1
          udp: 'udp'
        misc:
          log-file: stderr
          log-level: warn
        """;

    public void Dispose()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill();
                _process.WaitForExit(3000);
            }
            catch (InvalidOperationException)
            {
                // Уже завершился.
            }
        }
        _process?.Dispose();
        _process = null;
        try
        {
            File.Delete(ConfigPath);
        }
        catch (IOException)
        {
        }
    }
}
