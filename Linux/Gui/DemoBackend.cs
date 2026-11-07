using Tumbler.Daemon;

namespace Tumbler.Gui;

/// <summary>
/// Демонстрационные данные: окно можно открыть и снять на любой системе, включая Windows, без службы и /proc.
/// Нужно для проверки вёрстки (ключ --demo).
/// </summary>
internal sealed class DemoBackend : IBackend
{
    DaemonState _state = new()
    {
        Rules = new() { ["Discord"] = 10808, ["spotify"] = 10808, ["firefox"] = 2080 },
    };

    public bool IsDemo => true;

    public Task<Response?> CallAsync(Request request)
    {
        switch (request.Command)
        {
            case "apply" when request.State is { } state: _state = state; break;
            case "pause": _state.Enabled = false; break;
            case "resume": _state.Enabled = true; break;
            case "toggle": _state.Enabled = !_state.Enabled; break;
        }

        var ports = _state.Rules.Values.Distinct().Order();
        var status = new DaemonStatus
        {
            Version = "1.0.0",
            State = _state,
            RulesLoaded = _state.Enabled && _state.Rules.Count > 0,
            Tunnels = ports.Select(p => new TunnelStatus
            {
                Port = p,
                Device = $"tmb{p}",
                Running = true,
                ProxyAlive = p != 2080,
                Suspended = p == 2080 && _state.FallbackDirect,
            }).ToList(),
            Captured = _state.Rules.Keys.ToDictionary(k => k, k => k == "firefox" ? 9 : 4),
        };
        return Task.FromResult<Response?>(new Response { Ok = true, Status = status });
    }

    public Snapshot Scan(IReadOnlyCollection<int> ports) => new(
        [
            new("Discord", "/usr/share/discord/Discord", "Discord", null, 4, 7, true, true),
            new("spotify", "/usr/share/spotify/spotify", "Spotify", null, 5, 4, true, true),
            new("firefox", "/usr/lib/firefox/firefox", "Firefox", null, 11, 23, true, true),
            new("telegram-desktop", "/usr/bin/telegram-desktop", "Telegram", null, 1, 3, true, true),
            new("steam", "/home/user/.steam/ubuntu12_32/steam", "Steam", null, 3, 6, true, true),
            new("code", "/usr/share/code/code", "Visual Studio Code", null, 14, 2, true, true),
            new("nekobox_core", "/opt/nekobox/nekobox_core", "nekobox_core", null, 1, 2, false, true),
            new("xray", "/opt/v2rayN/bin/xray/xray", "xray", null, 1, 3, false, true),
            new("pipewire", "/usr/bin/pipewire", "pipewire", null, 1, 0, false, true),
            new("gnome-shell", "/usr/bin/gnome-shell", "gnome-shell", null, 1, 1, false, true),
        ],
        ports.ToDictionary(p => p, p => p != 2080),
        new() { [10808] = "xray", [2080] = "nekobox_core" },
        []);

    public List<PortPreset> FindProxies() =>
    [
        new(7897, "7897 · verge_mihomo", "На этом порту отвечает SOCKS5-прокси (процесс verge_mihomo)"),
        new(10808, "10808 · xray", "На этом порту отвечает SOCKS5-прокси (процесс xray)"),
    ];
}
