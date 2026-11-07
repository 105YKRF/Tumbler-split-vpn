using Tumbler.Daemon;

namespace Tumbler.Gui;

public enum CheckState { Ok, Warning, Fail }

/// <summary>Пункт диагностики. <see cref="Command"/> — готовая команда, которой проблема чинится.</summary>
public sealed record CheckResult(CheckState State, string Title, string Detail, string? Command = null)
{
    public bool HasCommand => Command is not null;
    public bool IsOk => State == CheckState.Ok;
    public bool IsWarning => State == CheckState.Warning;
    public bool IsFail => State == CheckState.Fail;
}

/// <param name="AppsByPort">Порт → сколько приложений на нём.</param>
/// <param name="RunningApps">Выбранные приложения, которые сейчас запущены.</param>
internal sealed record DiagnosticsInput(IReadOnlyDictionary<int, int> AppsByPort, bool Paused, IReadOnlyList<string> RunningApps);

/// <summary>Проверяет всю цепочку «приложение → cgroup → nftables → туннель → прокси → интернет».</summary>
internal static class Diagnostics
{
    public static async Task<List<CheckResult>> RunAsync(IBackend backend, DiagnosticsInput input)
    {
        var results = new List<CheckResult>();
        var response = await backend.CallAsync(new Request());
        var status = response?.Status;

        results.Add(status is null
            ? new(CheckState.Fail, "Служба «Тумблера»", "Не отвечает. Если она не установлена — нажми «Установить» в главном окне. Если установлена — посмотри журнал:",
                "journalctl -u tumbler -n 50")
            : new(CheckState.Ok, "Служба «Тумблера»", $"Работает, версия {status.Version}."));

        if (!backend.IsDemo)
            foreach (var requirement in Installer.Check().Where(r => !r.Ok))
                results.Add(new(CheckState.Fail, requirement.Title, requirement.Detail, requirement.Fix));

        if (status is null) return results;
        foreach (var problem in status.Problems)
            results.Add(new(CheckState.Fail, "Служба сообщает о проблеме", problem));

        if (input.AppsByPort.Count == 0 || input.Paused)
        {
            results.Add(new(CheckState.Warning, "Перехват", input.Paused ? "Проксирование на паузе — всё идёт напрямую." : "Ни одно приложение не выбрано — проверять нечего."));
            return results;
        }

        results.Add(status.RulesLoaded
            ? new(CheckState.Ok, "Правила nftables", "Загружены: трафик выбранных приложений помечается и уходит в туннели.")
            : new(CheckState.Fail, "Правила nftables", "Не загружены, поэтому ничего не перехватывается. Подробности в журнале:", "journalctl -u tumbler -n 50"));

        var tunnels = status.Tunnels.ToDictionary(t => t.Port);
        foreach (var (port, apps) in input.AppsByPort.OrderBy(p => p.Key))
        {
            var title = $"Порт 127.0.0.1:{port} ({Text.Plural(apps, "приложение", "приложения", "приложений")})";
            if (!tunnels.TryGetValue(port, out var tunnel) || !tunnel.Running)
            {
                results.Add(new(CheckState.Fail, title, $"Туннель не поднят{(tunnel?.Error is { } error ? $": {error}" : "")}. Журнал службы:", "journalctl -u tumbler -n 50"));
                continue;
            }
            results.Add(await Task.Run(() => CheckProxy(title, port)));
        }

        var captured = new Dictionary<string, int>(status.Captured, StringComparer.OrdinalIgnoreCase);
        var missed = input.RunningApps.Where(app => captured.GetValueOrDefault(app) == 0).ToList();
        results.Add(missed.Count == 0
            ? new(CheckState.Ok, "Перехват процессов", "Все запущенные выбранные приложения перехватываются.")
            : new(CheckState.Warning, "Перехват процессов",
                $"Служба не перехватила: {string.Join(", ", missed)}. Обычно это процессы, запущенные от другого пользователя, или приложение, которое только что запустилось."));

        if (!backend.IsDemo)
        {
            var other = backend.Scan([]).OtherTunnels;
            if (other.Count > 0)
                results.Add(new(CheckState.Warning, "Режим TUN у VPN-клиента",
                    $"Подняты чужие TUN-интерфейсы ({string.Join(", ", other)}): в VPN может уходить весь трафик компьютера. Выключи TUN в клиенте."));
        }
        return results;
    }

    static CheckResult CheckProxy(string title, int port)
    {
        if (!Socks.Probe("127.0.0.1", port))
            return new(CheckState.Fail, title, "Прокси не отвечает как SOCKS5. Запусти VPN-клиент и проверь, что номер порта совпадает с его настройками.");

        var (ok, detail) = Socks.HttpThrough("127.0.0.1", port);
        if (!ok)
            return new(CheckState.Fail, title, $"Порт отвечает, но интернет через него не идёт: {detail}. Проблема на стороне VPN — попробуй другой сервер.");

        return Socks.UdpAssociate("127.0.0.1", port)
            ? new(CheckState.Ok, title, $"Туннель работает, прокси отвечает, интернет через него идёт ({detail}), UDP поддерживается.")
            : new(CheckState.Warning, title, $"Интернет работает ({detail}), но UDP прокси не поддерживает — голос и звонки через него не пройдут.");
    }

    public static string Report(IEnumerable<CheckResult> results) => string.Join(Environment.NewLine, results.Select(r =>
    {
        var mark = r.State switch { CheckState.Ok => "[OK]", CheckState.Warning => "[!]", _ => "[X]" };
        var line = $"{mark} {r.Title}: {r.Detail}";
        return r.Command is null ? line : line + Environment.NewLine + "    " + r.Command;
    }));
}
