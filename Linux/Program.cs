using Avalonia;
using Tumbler.Daemon;
using Tumbler.Gui;

namespace Tumbler;

/// <summary>
/// Один файл на все роли:
///   tumbler                       — окно (обычный пользователь);
///   tumbler daemon                — служба (её запускает systemd от root);
///   tumbler install --user UID    — установка (root, окно вызывает через pkexec);
///   tumbler uninstall             — удаление (root);
///   tumbler pause|resume|toggle|status — управление из терминала или горячей клавишей рабочего стола.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "daemon":
                return DaemonMain.Run();
            case "install":
                return Installer.Install(UserArgument(args));
            case "uninstall":
                return Installer.Uninstall();
            case "pause" or "resume" or "toggle" or "status":
                return Cli(args[0]);
            case "--plan":
                // Отладка: какие правила nftables и какой конфиг туннеля служба сгенерирует для двух портов.
                Console.Write(Net.BuildRuleset([(3066, 1), (10808, 2)], udp: true));
                return 0;
            default:
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    static uint UserArgument(string[] args)
    {
        int index = Array.IndexOf(args, "--user");
        if (index >= 0 && index + 1 < args.Length && uint.TryParse(args[index + 1], out uint uid)) return uid;
        // sudo оставляет UID того, кто его вызвал.
        return uint.TryParse(Environment.GetEnvironmentVariable("SUDO_UID") ?? Environment.GetEnvironmentVariable("PKEXEC_UID"), out uid) ? uid : 0;
    }

    static int Cli(string command)
    {
        try
        {
            var response = Wire.CallAsync(new Request { Command = command }, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (!response.Ok)
            {
                Console.Error.WriteLine($"Ошибка: {response.Error}");
                return 1;
            }
            var status = response.Status!;
            Console.WriteLine(status.State.Enabled ? "Проксирование включено." : "Проксирование на паузе.");
            foreach (var tunnel in status.Tunnels)
                Console.WriteLine($"  порт {tunnel.Port}: туннель {(tunnel.Running ? "работает" : "не работает")}, прокси {(tunnel.ProxyAlive ? "отвечает" : "не отвечает")}{(tunnel.Suspended ? ", приложения временно напрямую" : "")}");
            foreach (var (app, count) in status.Captured)
                Console.WriteLine($"  {app}: перехвачено процессов — {count}");
            foreach (var problem in status.Problems)
                Console.WriteLine($"  ! {problem}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            Console.Error.WriteLine($"Служба «Тумблера» не отвечает: {ex.Message}. Она установлена? Проверь: systemctl status tumbler");
            return 1;
        }
    }
}
