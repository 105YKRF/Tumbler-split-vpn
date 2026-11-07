using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Tumbler.Daemon;

/// <summary>Что должна делать служба: какие приложения через какой порт. Сохраняется в /etc/tumbler/state.json.</summary>
public sealed class DaemonState
{
    /// <summary>false — пауза: правила перехвата сняты, всё идёт напрямую.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Проксировать и UDP (голос, звонки, QUIC). Без него через прокси идёт только TCP.</summary>
    public bool Udp { get; set; } = true;

    /// <summary>VPN недоступен: false — приложения остаются без сети, true — временно идут напрямую.</summary>
    public bool FallbackDirect { get; set; }

    /// <summary>Имя исполняемого файла → порт SOCKS5.</summary>
    public Dictionary<string, int> Rules { get; set; } = new();
}

public sealed class TunnelStatus
{
    public int Port { get; set; }
    public string Device { get; set; } = "";

    /// <summary>Туннель hev-socks5-tunnel запущен и его интерфейс поднят.</summary>
    public bool Running { get; set; }

    /// <summary>На порту отвечает SOCKS5.</summary>
    public bool ProxyAlive { get; set; }

    /// <summary>VPN упал, и приложения этого порта временно пущены напрямую.</summary>
    public bool Suspended { get; set; }

    public string? Error { get; set; }
}

public sealed class DaemonStatus
{
    public string Version { get; set; } = "";
    public DaemonState State { get; set; } = new();
    public List<TunnelStatus> Tunnels { get; set; } = new();

    /// <summary>Сколько процессов каждого приложения сейчас перехватывается.</summary>
    public Dictionary<string, int> Captured { get; set; } = new();

    /// <summary>Правила nftables загружены (иначе ничего не перехватывается).</summary>
    public bool RulesLoaded { get; set; }

    public List<string> Problems { get; set; } = new();
}

public sealed class Request
{
    /// <summary>status, apply, pause, resume или toggle.</summary>
    public string Command { get; set; } = "status";
    public DaemonState? State { get; set; }
}

public sealed class Response
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public DaemonStatus? Status { get; set; }
}

/// <summary>Обмен с службой: одна строка JSON в каждую сторону через UNIX-сокет.</summary>
public static class Wire
{
    public const string SocketPath = "/run/tumbler/tumbler.sock";

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<Response> CallAsync(Request request, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), cts.Token);
        await using var stream = new NetworkStream(socket, ownsSocket: false);

        var line = JsonSerializer.Serialize(request, Json) + "\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line), cts.Token);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var reply = await reader.ReadLineAsync(cts.Token) ?? throw new IOException("служба закрыла соединение без ответа");
        return JsonSerializer.Deserialize<Response>(reply, Json) ?? throw new IOException("служба прислала пустой ответ");
    }
}
