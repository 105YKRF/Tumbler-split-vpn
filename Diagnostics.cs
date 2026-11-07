using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Tumbler;

public enum CheckState { Ok, Warning, Fail }

/// <summary>
/// Один пункт диагностики. <see cref="Command"/> — готовая команда, которой проблема чинится вручную;
/// <see cref="Repairable"/> — проблему умеет исправить мастер установки.
/// </summary>
public sealed record CheckResult(CheckState State, string Title, string Detail, string? Command = null, bool Repairable = false)
{
    public bool HasCommand => Command is not null;
}

internal sealed record DiagnosticsInput(
    string InstallDir,
    string Host,
    IReadOnlyDictionary<int, int> AppsByPort,
    bool IsAdmin,
    bool Paused,
    bool ConfigInSync);

/// <summary>Проверяет всю цепочку «приложение → ProxiFyre → прокси → интернет» и говорит, какое звено сломано.</summary>
internal static class Diagnostics
{
    const string DriverService = "ndisrd";
    const string RepairHint = "Нажми «Установить недостающее» внизу окна.";

    public static List<CheckResult> Run(DiagnosticsInput input)
    {
        var results = new List<CheckResult>
        {
            Rights(input),
            Driver(),
            Service(input),
            Firewall(input),
        };
        results.AddRange(Ports(input));
        results.Add(Tunnel());
        results.Add(Config(input));
        return results;
    }

    static CheckResult Rights(DiagnosticsInput input) => input.IsAdmin
        ? new(CheckState.Ok, "Права администратора", "Есть — «Тумблер» может менять настройки службы.")
        : new(CheckState.Fail, "Права администратора", "Нет. Закрой «Тумблер» и запусти заново, согласившись в окне UAC.");

    static CheckResult Driver() => ServiceCtl.Query(DriverService) switch
    {
        ServiceState.Running => new(CheckState.Ok, "Драйвер перехвата (Windows Packet Filter)", "Установлен и работает."),
        ServiceState.NotInstalled => new(CheckState.Fail, "Драйвер перехвата (Windows Packet Filter)",
            "Не установлен. " + RepairHint, Repairable: true),
        _ => new(CheckState.Fail, "Драйвер перехвата (Windows Packet Filter)",
            "Установлен, но не запущен. Обычно помогает перезагрузка компьютера."),
    };

    static CheckResult Service(DiagnosticsInput input)
    {
        const string title = "Служба ProxiFyre";
        var state = ServiceCtl.Query();
        if (state == ServiceState.NotInstalled)
            return new(CheckState.Fail, title, "Не установлена. " + RepairHint, Repairable: true);

        bool wanted = input.AppsByPort.Count > 0 && !input.Paused;
        bool auto = AutoStart();
        return (state, wanted) switch
        {
            (ServiceState.Running, true) => new(auto ? CheckState.Ok : CheckState.Warning, title,
                auto ? "Работает и запускается вместе с Windows." : "Работает, но автозапуск выключен — после перезагрузки не поднимется, пока не откроешь «Тумблер»."),
            (ServiceState.Running, false) => new(CheckState.Ok, title, "Работает, хотя сейчас ни одно приложение не выбрано."),
            (_, false) => new(CheckState.Ok, title, "Остановлена — так и должно быть: проксирование на паузе или приложения не выбраны."),
            _ => new(CheckState.Fail, title, "Остановлена, хотя приложения выбраны. Переключи любой тумблер или сними паузу — «Тумблер» запустит её сам."),
        };
    }

    static bool AutoStart()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceCtl.Name}");
        return key?.GetValue("Start") is int start && start == 2;
    }

    // ---------- Брандмауэр ----------

    /// <summary>
    /// ProxiFyre разворачивает перехваченные соединения на свой локальный порт как входящие.
    /// Без разрешающего правила брандмауэр Windows молча их режет, и приложения зависают на подключении.
    /// </summary>
    static CheckResult Firewall(DiagnosticsInput input)
    {
        const string title = "Правило брандмауэра Windows";
        var exe = Path.Combine(input.InstallDir, "ProxiFyre.exe");
        var command = $"New-NetFirewallRule -DisplayName \"ProxiFyre\" -Direction Inbound -Program \"{exe}\" -Action Allow -Profile Any";

        return FirewallAllows(exe) switch
        {
            true => new(CheckState.Ok, title, "Входящие соединения для ProxiFyre.exe разрешены."),
            false => new(CheckState.Fail, title,
                $"Разрешающего правила нет — из-за этого приложения зависают на подключении. {RepairHint} Или выполни в PowerShell от имени администратора:",
                command, Repairable: true),
            null => new(CheckState.Warning, title,
                "Не удалось прочитать правила брандмауэра. Если приложения зависают на подключении, создай правило сам:", command),
        };
    }

    /// <summary>
    /// Пропускает ли брандмауэр Windows входящие соединения к программе: есть включённое разрешающее правило
    /// для активного профиля сети либо брандмауэр выключен. null — правила прочитать не удалось.
    /// </summary>
    public static bool? FirewallAllows(string exe)
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new COMException("нет HNetCfg.FwPolicy2");
            object policy = Activator.CreateInstance(type)!;
            try
            {
                int active = (int)Get(policy, "CurrentProfileTypes")!;
                foreach (object rule in (IEnumerable)Get(policy, "Rules")!)
                {
                    try
                    {
                        if (Get(rule, "ApplicationName") is string app
                            && string.Equals(app, exe, StringComparison.OrdinalIgnoreCase)
                            && (int)Get(rule, "Direction")! == 1
                            && (int)Get(rule, "Action")! == 1
                            && (bool)Get(rule, "Enabled")!
                            && ((int)Get(rule, "Profiles")! & active) != 0)
                            return true;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(rule);
                    }
                }

                bool firewallOn = new[] { 1, 2, 4 }.Any(profile => (active & profile) != 0 && (bool)Get(policy, "FirewallEnabled", profile)!);
                return !firewallOn;
            }
            finally
            {
                Marshal.ReleaseComObject(policy);
            }
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidCastException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static object? Get(object target, string property, params object[] index) =>
        target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, index);

    // ---------- Прокси ----------

    static IEnumerable<CheckResult> Ports(DiagnosticsInput input)
    {
        if (input.AppsByPort.Count == 0)
        {
            yield return new(CheckState.Warning, "Прокси", "Ни одно приложение не выбрано — проверять нечего.");
            yield break;
        }

        foreach (var (port, apps) in input.AppsByPort.OrderBy(p => p.Key))
        {
            var title = $"Прокси {input.Host}:{port} ({Text.Plural(apps, "приложение", "приложения", "приложений")})";
            if (!Socks.Probe(input.Host, port))
            {
                yield return new(CheckState.Fail, title,
                    "Порт не отвечает как SOCKS5. Запусти VPN-клиент и проверь, что номер порта совпадает с его настройками.");
                continue;
            }

            var (ok, detail) = Socks.HttpThrough(input.Host, port);
            if (!ok)
            {
                yield return new(CheckState.Fail, title,
                    $"Порт отвечает, но интернет через него не идёт: {detail}. Проблема на стороне VPN — попробуй другой сервер.");
                continue;
            }

            yield return Socks.UdpAssociate(input.Host, port)
                ? new(CheckState.Ok, title, $"Отвечает, интернет через него работает ({detail}), UDP поддерживается.")
                : new(CheckState.Warning, title, $"Интернет работает ({detail}), но UDP прокси не поддерживает — голос и звонки через него не пройдут.");
        }
    }

    static CheckResult Tunnel()
    {
        var tunnels = Scanner.Tunnels();
        return tunnels.Count == 0
            ? new(CheckState.Ok, "Режим TUN", "Выключен — через VPN идут только выбранные приложения.")
            : new(CheckState.Warning, "Режим TUN",
                $"Включён ({string.Join(", ", tunnels)}): в VPN уходит весь трафик компьютера, тумблеры ничего не меняют. Выключи TUN в VPN-клиенте.");
    }

    static CheckResult Config(DiagnosticsInput input) => input.ConfigInSync
        ? new(CheckState.Ok, "Конфиг службы", "Совпадает с тем, что выбрано в «Тумблере».")
        : new(CheckState.Warning, "Конфиг службы", "Отличается от выбранного в «Тумблере» — есть неприменённые изменения. Нажми «Применить».");
}
