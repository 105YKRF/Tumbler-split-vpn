using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;

namespace Tumbler;

public partial class App : Application
{
    // Через это событие второй запуск просит уже работающий «Тумблер» показать окно.
    const string ShowEventName = @"Local\Tumbler.Show";

    EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = Options.Parse(e.Args);

        // «Тумблер» уже сидит в трее — просто показываем его окно, без повторного UAC.
        if (!options.IsDev && SignalRunningInstance())
        {
            Shutdown();
            return;
        }

        // Ставить драйвер, править конфиг в Program Files и перезапускать службу может только администратор.
        if (!options.NoElevate && !Elevation.IsAdmin && Elevation.RelaunchElevated(e.Args))
        {
            Shutdown();
            return;
        }

        // На новом компьютере сначала ставим драйвер и службу. Пока мастер открыт, главного окна ещё нет,
        // поэтому приложение не должно завершаться по закрытию «первого окна».
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (!options.IsDev && !Setup.IsComplete && !SetupWindow.Run(owner: null))
        {
            Shutdown();
            return;
        }

        var window = new MainWindow(options);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        if (!options.IsDev) ListenForShowRequests(window);

        if (options.Tray) window.StartHidden();
        else window.Show();
    }

    static bool SignalRunningInstance()
    {
        if (!EventWaitHandle.TryOpenExisting(ShowEventName, out var handle)) return false;
        using (handle) handle.Set();
        return true;
    }

    void ListenForShowRequests(MainWindow window)
    {
        // Событие создаёт процесс с правами администратора, а сигналит обычный — доступ нужно выдать явно.
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(
            WindowsIdentity.GetCurrent().User!, EventWaitHandleRights.FullControl, AccessControlType.Allow));

        _showEvent = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ShowEventName, out _, security);
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(window.ShowFromTray);
        })
        {
            IsBackground = true,
            Name = "Show requests",
        };
        listener.Start();
    }
}

/// <summary>Ключи запуска. Всё, кроме --tray и --no-elevate, нужно только для отладки.</summary>
public sealed class Options
{
    /// <summary>Стартовать без окна, только значок в трее (так запускает автозагрузка).</summary>
    public bool Tray { get; private set; }

    public bool NoElevate { get; private set; }

    /// <summary>Работать с копией конфига и не трогать службу.</summary>
    public string? ConfigPath { get; private set; }

    /// <summary>Сохранить снимок окна в PNG и выйти.</summary>
    public string? Screenshot { get; private set; }

    /// <summary>Снимать не главное окно, а диалог: settings, diagnostics, setup, help или help:НОМЕР_РАЗДЕЛА.</summary>
    public string? Dialog { get; private set; }

    /// <summary>Записать текстовый отчёт диагностики в файл.</summary>
    public string? DiagnosticsReport { get; private set; }

    /// <summary>Проверить встроенные установщики, проверку подписей и поиск прокси; результат — в файл.</summary>
    public string? SelfTest { get; private set; }

    /// <summary>Переключить тумблеры этих приложений сразу после запуска.</summary>
    public List<string> Toggle { get; } = new();

    /// <summary>Назначить приложениям свой порт: --route имя=порт.</summary>
    public List<(string App, int Port)> Routes { get; } = new();

    /// <summary>Переключиться на профиль, а если такого нет — создать его.</summary>
    public string? Profile { get; private set; }

    /// <summary>Задать поведение при недоступном VPN: --fallback direct|block.</summary>
    public bool? Fallback { get; private set; }

    public bool IsDev => ConfigPath is not null || Screenshot is not null || DiagnosticsReport is not null || SelfTest is not null;

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--tray": options.Tray = true; break;
                case "--no-elevate": options.NoElevate = true; break;
                case "--config": options.ConfigPath = Next(); break;
                case "--screenshot": options.Screenshot = Next(); break;
                case "--dialog": options.Dialog = Next(); break;
                case "--diag": options.DiagnosticsReport = Next(); break;
                case "--selftest": options.SelfTest = Next(); break;
                case "--profile": options.Profile = Next(); break;
                case "--fallback": options.Fallback = Next() == "direct"; break;
                case "--toggle" when Next() is { } name: options.Toggle.Add(name); break;
                case "--route" when Next() is { } route && route.Split('=') is [var app, var port] && int.TryParse(port, out int number):
                    options.Routes.Add((app, number));
                    break;
            }
        }
        if (options.IsDev) options.NoElevate = true;
        return options;
    }
}

internal static class Elevation
{
    public static bool IsAdmin { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Перезапускает себя через UAC. false — пользователь отказался, продолжаем без прав.</summary>
    public static bool RelaunchElevated(IEnumerable<string> args)
    {
        try
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            Process.Start(info);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
